#include "xr_input.h"
#include "bvr_config.h"
#include "bvr_log.h"

#include <windows.h>    /* GetTickCount64 */

#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <mutex>
#include <vector>

namespace bvr {
namespace {

constexpr uint32_t kHands = 2;   /* 0 = left, 1 = right */
std::mutex g_syncMutex;          /* TEST 452: the sync's time, see InputState */

struct Action
{
    XrAction handle = XR_NULL_HANDLE;
};

struct InputState
{
    XrInstance  instance = XR_NULL_HANDLE;
    XrActionSet actionSet = XR_NULL_HANDLE;
    XrPath      handPath[kHands] = {};

    XrAction aimPose = XR_NULL_HANDLE;
    XrAction gripPose = XR_NULL_HANDLE;
    XrAction trigger = XR_NULL_HANDLE;
    XrAction squeeze = XR_NULL_HANDLE;
    XrAction thumbstick = XR_NULL_HANDLE;
    /* The stick CLICK - L3/R3. Bound as its own boolean because the vector
       action cannot report it, and it is the only spare modifier a Touch or
       Sense controller has left once the triggers and grips are spoken for. */
    XrAction thumbClick = XR_NULL_HANDLE;
    XrAction primary = XR_NULL_HANDLE;     /* A / X */
    XrAction secondary = XR_NULL_HANDLE;   /* B / Y */
    XrAction menu = XR_NULL_HANDLE;

    /* TEST 482: the capacitive sensors, for the finger hands - a finger
       RESTING on the trigger, the thumb resting on the stick, a face button
       or the thumbrest (every thumb surface is bound to the one action and the
       runtime ORs them). */
    XrAction triggerTouch = XR_NULL_HANDLE;
    XrAction thumbTouch = XR_NULL_HANDLE;
    /* The thumbrest alone, for the D-pad chord (dpad_thumbrest) - thumbTouch
       ORs it with the stick and the face buttons. Touch only. */
    XrAction thumbrestTouch = XR_NULL_HANDLE;

    /* Vibration output. A haptic action is the only way OpenXR lets an app
       buzz a controller; there is no "buzz hand 1" call. BOUND in every
       profile that has one (all but the simple controller's left hand, which
       still takes the right-hand path harmlessly). */
    XrAction haptic = XR_NULL_HANDLE;

    /* Kept so a haptic can be applied from the game thread while the render
       thread owns the frame. Guarded by g_hapticMutex because shutdown clears
       it from the other thread. */
    XrSession session = XR_NULL_HANDLE;

    XrSpace aimSpace[kHands] = {};
    XrSpace gripSpace[kHands] = {};

    /* TEST 428: the eye gaze (XR_EXT_eye_gaze_interaction), located against
       the VIEW space so it comes out as a direction in the head's frame. */
    XrAction gaze = XR_NULL_HANDLE;
    XrSpace  gazeSpace = XR_NULL_HANDLE;
    XrSpace  viewSpace = XR_NULL_HANDLE;
    bool     loggedGaze = false;
    uint64_t gazeValidTick = 0;
    float    gazeX = 0.0f, gazeY = 0.0f;

    bool ready = false;

    /* Single writer on the render thread, single reader on the managed thread.
       Same double-buffer arrangement the eye poses use: the reader takes the
       index first, so it can only ever see a completely written buffer or the
       previous one. */
    BvrInputState buffers[2] = {};
    std::atomic<int> index{ 0 };

    bool warnedProfile[kHands] = {};
    XrPath lastProfile[kHands] = {};

    /* TEST 452: what the render thread last synced for - its display time, the
       performance counter at that moment, the display period and the space -
       so the game thread can locate the hands for ITS moment (input_latch).
       Guarded by g_syncMutex: four values written together. */
    XrTime  syncTime = 0;
    int64_t syncQpc = 0;
    XrTime  period = 0;
    XrSpace syncBase = XR_NULL_HANDLE;

    /* The game thread's own sample, and when it was taken. Game thread only. */
    BvrInputState latched = {};
    int64_t latchedQpc = 0;
    BvrVec3 lastRaw = {};
    uint32_t latchCount = 0, repeatCount = 0;
    double leadSumMs = 0.0;
    bool loggedTracking = false;
    bool loggedHaptic = false;
    int  reportTick = 0;
};

InputState g_in;

/* TEST 428: the gaze for any thread - tangent x (right) and y (up) in the
   head's frame, packed so a reader never sees half of one sample. A NaN x
   means no gaze. */
std::atomic<uint64_t> g_gazePacked{ 0x7FC00000ull };

/* TEST 482: the touch sensors at the last sync, left in bits 0-7 and right in
   8-15 (BVR_TOUCH_*). Render thread writes, any thread reads. */
std::atomic<uint32_t> g_touch{ 0 };

/* Serialises the session handle the haptic path uses against input_destroy,
   which clears it from the render thread. A block or a parry takes this once;
   the cost is nothing next to the XR call itself. */
std::mutex g_hapticMutex;

bool ok(XrResult r, const char* what)
{
    if (XR_SUCCEEDED(r))
        return true;

    BVR_ERR("Input: %s failed (%d).", what, static_cast<int>(r));
    return false;
}

XrPath path_of(XrInstance instance, const char* text)
{
    XrPath p = XR_NULL_PATH;
    if (XR_FAILED(xrStringToPath(instance, text, &p)))
    {
        BVR_ERR("Input: could not convert path '%s'.", text);
        return XR_NULL_PATH;
    }
    return p;
}

XrAction make_action(XrActionSet set, XrActionType type,
                     const char* name, const char* localised,
                     const XrPath* subactions, uint32_t subactionCount)
{
    XrActionCreateInfo info{ XR_TYPE_ACTION_CREATE_INFO };
    info.actionType = type;
    std::strncpy(info.actionName, name, XR_MAX_ACTION_NAME_SIZE - 1);
    std::strncpy(info.localizedActionName, localised, XR_MAX_LOCALIZED_ACTION_NAME_SIZE - 1);
    info.countSubactionPaths = subactionCount;
    info.subactionPaths = subactions;

    XrAction action = XR_NULL_HANDLE;
    if (!ok(xrCreateAction(set, &info, &action), name))
        return XR_NULL_HANDLE;

    return action;
}

/* One profile's worth of bindings.
 *
 * Every entry is a SUGGESTION. The runtime is free to ignore a profile it does
 * not recognise, and it will pick whichever one matches the hardware actually
 * connected - so listing several costs nothing and missing the right one
 * presents as controllers that never become active. */
bool suggest_once(XrInstance instance, const char* profile,
                  const std::vector<std::pair<XrAction, const char*>>& pairs)
{
    XrPath profilePath = path_of(instance, profile);
    if (profilePath == XR_NULL_PATH)
        return false;

    std::vector<XrActionSuggestedBinding> bindings;
    bindings.reserve(pairs.size());

    for (const auto& pair : pairs)
    {
        if (pair.first == XR_NULL_HANDLE)
            continue;

        const XrPath bound = path_of(instance, pair.second);
        if (bound == XR_NULL_PATH)
            continue;

        bindings.push_back(XrActionSuggestedBinding{ pair.first, bound });
    }

    if (bindings.empty())
        return false;

    XrInteractionProfileSuggestedBinding suggestion{
        XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING };
    suggestion.interactionProfile = profilePath;
    suggestion.countSuggestedBindings = static_cast<uint32_t>(bindings.size());
    suggestion.suggestedBindings = bindings.data();

    /* Not an error worth shouting about: a runtime that does not know a profile
       returns a failure here and simply will not use it. */
    const XrResult r = xrSuggestInteractionProfileBindings(instance, &suggestion);
    if (XR_FAILED(r))
        BVR_INFO("Input: runtime declined bindings for %s (%d); other profiles stand.",
                 profile, static_cast<int>(r));
    return XR_SUCCEEDED(r);
}

/* TEST 482: the profile with its touch sensors; a runtime that declines a
   single path declines the whole profile - every control with it - so it is
   suggested again without them. */
void suggest(XrInstance instance, const char* profile,
             std::vector<std::pair<XrAction, const char*>> pairs,
             const std::vector<std::pair<XrAction, const char*>>& touch = {})
{
    if (touch.empty())
    {
        suggest_once(instance, profile, pairs);
        return;
    }

    const size_t plain = pairs.size();
    pairs.insert(pairs.end(), touch.begin(), touch.end());
    if (suggest_once(instance, profile, pairs))
        return;

    pairs.resize(plain);
    BVR_INFO("Input: TEST 482 - %s again without its touch sensors (the finger hands use the "
             "trigger and grip alone there).", profile);
    suggest_once(instance, profile, pairs);
}

/* TEST 482: BVR_TOUCH_BOUND when the runtime bound a sensor to the action,
   plus touched when a finger is on it. */
uint32_t read_touch(XrSession session, XrAction action, XrPath hand, uint32_t touched, uint32_t bound)
{
    if (action == XR_NULL_HANDLE)
        return 0;

    XrActionStateGetInfo info{ XR_TYPE_ACTION_STATE_GET_INFO };
    info.action = action;
    info.subactionPath = hand;

    XrActionStateBoolean state{ XR_TYPE_ACTION_STATE_BOOLEAN };
    if (XR_FAILED(xrGetActionStateBoolean(session, &info, &state)) || !state.isActive)
        return 0;

    return bound | (state.currentState == XR_TRUE ? touched : 0u);
}

void locate_hand(XrSpace space, XrSpace baseSpace, XrTime time,
                 BvrQuat* orientation, BvrVec3* position,
                 BvrVec3* velocity, bool* tracked)
{
    if (space == XR_NULL_HANDLE)
        return;

    XrSpaceVelocity vel{ XR_TYPE_SPACE_VELOCITY };
    XrSpaceLocation loc{ XR_TYPE_SPACE_LOCATION };
    loc.next = (velocity != nullptr) ? &vel : nullptr;

    if (XR_FAILED(xrLocateSpace(space, baseSpace, time, &loc)))
        return;

    const bool haveOrientation =
        (loc.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) != 0;
    const bool havePosition =
        (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0;

    if (haveOrientation && orientation != nullptr)
    {
        orientation->x = loc.pose.orientation.x;
        orientation->y = loc.pose.orientation.y;
        orientation->z = loc.pose.orientation.z;
        orientation->w = loc.pose.orientation.w;
    }

    if (havePosition && position != nullptr)
    {
        position->x = loc.pose.position.x;
        position->y = loc.pose.position.y;
        position->z = loc.pose.position.z;
    }

    if (velocity != nullptr &&
        (vel.velocityFlags & XR_SPACE_VELOCITY_LINEAR_VALID_BIT) != 0)
    {
        velocity->x = vel.linearVelocity.x;
        velocity->y = vel.linearVelocity.y;
        velocity->z = vel.linearVelocity.z;
    }

    if (tracked != nullptr)
        *tracked = haveOrientation && havePosition;
}

float read_float(XrSession session, XrAction action, XrPath hand)
{
    if (action == XR_NULL_HANDLE)
        return 0.0f;

    XrActionStateGetInfo info{ XR_TYPE_ACTION_STATE_GET_INFO };
    info.action = action;
    info.subactionPath = hand;

    XrActionStateFloat state{ XR_TYPE_ACTION_STATE_FLOAT };
    if (XR_FAILED(xrGetActionStateFloat(session, &info, &state)) || !state.isActive)
        return 0.0f;

    return state.currentState;
}

bool read_bool(XrSession session, XrAction action, XrPath hand)
{
    if (action == XR_NULL_HANDLE)
        return false;

    XrActionStateGetInfo info{ XR_TYPE_ACTION_STATE_GET_INFO };
    info.action = action;
    info.subactionPath = hand;

    XrActionStateBoolean state{ XR_TYPE_ACTION_STATE_BOOLEAN };
    if (XR_FAILED(xrGetActionStateBoolean(session, &info, &state)) || !state.isActive)
        return false;

    return state.currentState == XR_TRUE;
}

void read_vector2(XrSession session, XrAction action, XrPath hand,
                  float* x, float* y)
{
    *x = 0.0f;
    *y = 0.0f;

    if (action == XR_NULL_HANDLE)
        return;

    XrActionStateGetInfo info{ XR_TYPE_ACTION_STATE_GET_INFO };
    info.action = action;
    info.subactionPath = hand;

    XrActionStateVector2f state{ XR_TYPE_ACTION_STATE_VECTOR2F };
    if (XR_FAILED(xrGetActionStateVector2f(session, &info, &state)) || !state.isActive)
        return;

    *x = state.currentState.x;
    *y = state.currentState.y;
}

void publish_gaze(bool valid, float x, float y)
{
    float xy[2] = { valid ? x : std::nanf(""), y };
    uint64_t packed = 0;
    std::memcpy(&packed, xy, sizeof(packed));
    g_gazePacked.store(packed, std::memory_order_relaxed);
}

/* The gaze at the frame's display time. Held through a blink (a third of a
   second) so the foveation does not flick to the fixed centre and back. */
void sync_gaze(XrSession session, XrTime time)
{
    XrActionStateGetInfo get{ XR_TYPE_ACTION_STATE_GET_INFO };
    get.action = g_in.gaze;
    XrActionStatePose pose{ XR_TYPE_ACTION_STATE_POSE };

    XrSpaceLocation loc{ XR_TYPE_SPACE_LOCATION };
    constexpr XrSpaceLocationFlags kTracked =
        XR_SPACE_LOCATION_ORIENTATION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;

    const uint64_t now = GetTickCount64();
    if (XR_SUCCEEDED(xrGetActionStatePose(session, &get, &pose)) && pose.isActive &&
        XR_SUCCEEDED(xrLocateSpace(g_in.gazeSpace, g_in.viewSpace, time, &loc)) &&
        (loc.locationFlags & kTracked) == kTracked)
    {
        /* The gaze looks down its -Z. */
        const XrQuaternionf& q = loc.pose.orientation;
        const float dx = -2.0f * (q.x * q.z + q.w * q.y);
        const float dy = -2.0f * (q.y * q.z - q.w * q.x);
        const float dz = -(1.0f - 2.0f * (q.x * q.x + q.y * q.y));
        if (dz < -0.1f)
        {
            g_in.gazeX = dx / -dz;
            g_in.gazeY = dy / -dz;
            g_in.gazeValidTick = now;
            if (!g_in.loggedGaze)
            {
                g_in.loggedGaze = true;
                BVR_INFO("Input: eye gaze is tracking (TEST 428) - looking %.1f deg right, "
                         "%.1f deg up.", std::atan(g_in.gazeX) * 57.2958f,
                         std::atan(g_in.gazeY) * 57.2958f);
            }
        }
    }

    publish_gaze(g_in.gazeValidTick != 0 && now - g_in.gazeValidTick < 333,
                 g_in.gazeX, g_in.gazeY);
}

void report_profile(XrInstance instance, XrSession session)
{
    /* NOT once per session any more (TEST 238g): the profile is often not known
       at session start - "no interaction profile yet" was logged for both hands
       and never revisited, which is exactly the line that would have named the
       PSVR2 profile and whether a haptic binding can exist at all. Now every
       hand is re-checked periodically and reported whenever its profile appears
       or changes. */
    for (uint32_t hand = 0; hand < kHands; ++hand)
    {
        XrInteractionProfileState state{ XR_TYPE_INTERACTION_PROFILE_STATE };
        if (XR_FAILED(xrGetCurrentInteractionProfile(session, g_in.handPath[hand], &state)))
            continue;

        if (state.interactionProfile == XR_NULL_PATH)
        {
            if (!g_in.warnedProfile[hand])
            {
                g_in.warnedProfile[hand] = true;
                BVR_INFO("Input: %s hand has no interaction profile yet - nothing is "
                         "bound until the controller wakes up.", hand == 0 ? "left" : "right");
            }
            continue;
        }

        if (state.interactionProfile == g_in.lastProfile[hand])
            continue;

        g_in.lastProfile[hand] = state.interactionProfile;
        g_in.warnedProfile[hand] = false;

        char name[XR_MAX_PATH_LENGTH] = {};
        uint32_t written = 0;
        if (XR_SUCCEEDED(xrPathToString(instance, state.interactionProfile,
                                        sizeof(name), &written, name)))
        {
            BVR_INFO("Input: %s hand bound to %s. Haptics follow the same profile.",
                     hand == 0 ? "left" : "right", name);
        }
    }
}

} // namespace

bool input_create(XrInstance instance, XrSession session, bool eyeGaze)
{
    if (g_in.ready)
        return true;

    g_in.instance = instance;

    XrActionSetCreateInfo setInfo{ XR_TYPE_ACTION_SET_CREATE_INFO };
    std::strncpy(setInfo.actionSetName, "gameplay", XR_MAX_ACTION_SET_NAME_SIZE - 1);
    std::strncpy(setInfo.localizedActionSetName, "Gameplay",
                 XR_MAX_LOCALIZED_ACTION_SET_NAME_SIZE - 1);
    setInfo.priority = 0;

    if (!ok(xrCreateActionSet(instance, &setInfo, &g_in.actionSet), "xrCreateActionSet"))
        return false;

    g_in.handPath[0] = path_of(instance, "/user/hand/left");
    g_in.handPath[1] = path_of(instance, "/user/hand/right");

    if (g_in.handPath[0] == XR_NULL_PATH || g_in.handPath[1] == XR_NULL_PATH)
        return false;

    const XrPath* hands = g_in.handPath;

    g_in.aimPose    = make_action(g_in.actionSet, XR_ACTION_TYPE_POSE_INPUT,
                                  "aim_pose", "Aim pose", hands, kHands);
    g_in.gripPose   = make_action(g_in.actionSet, XR_ACTION_TYPE_POSE_INPUT,
                                  "grip_pose", "Grip pose", hands, kHands);
    g_in.trigger    = make_action(g_in.actionSet, XR_ACTION_TYPE_FLOAT_INPUT,
                                  "trigger", "Trigger", hands, kHands);
    g_in.squeeze    = make_action(g_in.actionSet, XR_ACTION_TYPE_FLOAT_INPUT,
                                  "squeeze", "Grip squeeze", hands, kHands);
    g_in.thumbstick = make_action(g_in.actionSet, XR_ACTION_TYPE_VECTOR2F_INPUT,
                                  "thumbstick", "Thumbstick", hands, kHands);
    g_in.primary    = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                  "primary", "Primary button", hands, kHands);
    g_in.secondary  = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                  "secondary", "Secondary button", hands, kHands);
    g_in.menu       = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                  "menu", "Menu", hands, kHands);
    g_in.thumbClick = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                  "thumb_click", "Thumbstick click", hands, kHands);
    g_in.haptic     = make_action(g_in.actionSet, XR_ACTION_TYPE_VIBRATION_OUTPUT,
                                  "haptic", "Haptic", hands, kHands);
    g_in.triggerTouch = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                    "trigger_touch", "Finger on the trigger", hands, kHands);
    g_in.thumbTouch   = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                    "thumb_touch", "Thumb resting", hands, kHands);
    g_in.thumbrestTouch = make_action(g_in.actionSet, XR_ACTION_TYPE_BOOLEAN_INPUT,
                                      "thumbrest_touch", "Thumb on the thumbrest", hands, kHands);

    if (g_in.aimPose == XR_NULL_HANDLE || g_in.gripPose == XR_NULL_HANDLE)
        return false;

    /* The simple controller is the fallback every runtime must support. It has
       no trigger axis and no stick, so it buys presence and nothing else - but
       "the controllers are tracking" is exactly what this stage needs to prove. */
    suggest(instance, "/interaction_profiles/khr/simple_controller", {
        { g_in.aimPose,   "/user/hand/left/input/aim/pose" },
        { g_in.aimPose,   "/user/hand/right/input/aim/pose" },
        { g_in.gripPose,  "/user/hand/left/input/grip/pose" },
        { g_in.gripPose,  "/user/hand/right/input/grip/pose" },
        { g_in.primary,   "/user/hand/left/input/select/click" },
        { g_in.primary,   "/user/hand/right/input/select/click" },
        { g_in.menu,      "/user/hand/left/input/menu/click" },
        { g_in.menu,      "/user/hand/right/input/menu/click" },
        { g_in.haptic,    "/user/hand/left/output/haptic" },
        { g_in.haptic,    "/user/hand/right/output/haptic" },
    });

    /* Quest 3 through VirtualDesktopXR reports as Touch. */
    suggest(instance, "/interaction_profiles/oculus/touch_controller", {
        { g_in.aimPose,    "/user/hand/left/input/aim/pose" },
        { g_in.aimPose,    "/user/hand/right/input/aim/pose" },
        { g_in.gripPose,   "/user/hand/left/input/grip/pose" },
        { g_in.gripPose,   "/user/hand/right/input/grip/pose" },
        { g_in.trigger,    "/user/hand/left/input/trigger/value" },
        { g_in.trigger,    "/user/hand/right/input/trigger/value" },
        { g_in.squeeze,    "/user/hand/left/input/squeeze/value" },
        { g_in.squeeze,    "/user/hand/right/input/squeeze/value" },
        { g_in.thumbstick, "/user/hand/left/input/thumbstick" },
        { g_in.thumbstick, "/user/hand/right/input/thumbstick" },
        { g_in.primary,    "/user/hand/left/input/x/click" },
        { g_in.primary,    "/user/hand/right/input/a/click" },
        { g_in.secondary,  "/user/hand/left/input/y/click" },
        { g_in.secondary,  "/user/hand/right/input/b/click" },
        { g_in.menu,       "/user/hand/left/input/menu/click" },
        { g_in.thumbClick, "/user/hand/left/input/thumbstick/click" },
        { g_in.thumbClick, "/user/hand/right/input/thumbstick/click" },
        { g_in.haptic,     "/user/hand/left/output/haptic" },
        { g_in.haptic,     "/user/hand/right/output/haptic" },
    }, {
        { g_in.triggerTouch, "/user/hand/left/input/trigger/touch" },
        { g_in.triggerTouch, "/user/hand/right/input/trigger/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/thumbstick/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/thumbstick/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/x/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/y/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/a/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/b/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/thumbrest/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/thumbrest/touch" },
        { g_in.thumbrestTouch, "/user/hand/left/input/thumbrest/touch" },
        { g_in.thumbrestTouch, "/user/hand/right/input/thumbrest/touch" },
    });

    suggest(instance, "/interaction_profiles/valve/index_controller", {
        { g_in.aimPose,    "/user/hand/left/input/aim/pose" },
        { g_in.aimPose,    "/user/hand/right/input/aim/pose" },
        { g_in.gripPose,   "/user/hand/left/input/grip/pose" },
        { g_in.gripPose,   "/user/hand/right/input/grip/pose" },
        { g_in.trigger,    "/user/hand/left/input/trigger/value" },
        { g_in.trigger,    "/user/hand/right/input/trigger/value" },
        { g_in.squeeze,    "/user/hand/left/input/squeeze/value" },
        { g_in.squeeze,    "/user/hand/right/input/squeeze/value" },
        { g_in.thumbstick, "/user/hand/left/input/thumbstick" },
        { g_in.thumbstick, "/user/hand/right/input/thumbstick" },
        { g_in.primary,    "/user/hand/left/input/a/click" },
        { g_in.primary,    "/user/hand/right/input/a/click" },
        { g_in.secondary,  "/user/hand/left/input/b/click" },
        { g_in.secondary,  "/user/hand/right/input/b/click" },
        { g_in.thumbClick, "/user/hand/left/input/thumbstick/click" },
        { g_in.thumbClick, "/user/hand/right/input/thumbstick/click" },
        { g_in.haptic,     "/user/hand/left/output/haptic" },
        { g_in.haptic,     "/user/hand/right/output/haptic" },
    }, {
        { g_in.triggerTouch, "/user/hand/left/input/trigger/touch" },
        { g_in.triggerTouch, "/user/hand/right/input/trigger/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/thumbstick/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/thumbstick/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/a/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/b/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/a/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/b/touch" },
        { g_in.thumbTouch,   "/user/hand/left/input/trackpad/touch" },
        { g_in.thumbTouch,   "/user/hand/right/input/trackpad/touch" },
    });

    suggest(instance, "/interaction_profiles/htc/vive_controller", {
        { g_in.aimPose,   "/user/hand/left/input/aim/pose" },
        { g_in.aimPose,   "/user/hand/right/input/aim/pose" },
        { g_in.gripPose,  "/user/hand/left/input/grip/pose" },
        { g_in.gripPose,  "/user/hand/right/input/grip/pose" },
        { g_in.trigger,   "/user/hand/left/input/trigger/value" },
        { g_in.trigger,   "/user/hand/right/input/trigger/value" },
        { g_in.primary,   "/user/hand/left/input/trackpad/click" },
        { g_in.primary,   "/user/hand/right/input/trackpad/click" },
        { g_in.menu,      "/user/hand/left/input/menu/click" },
        { g_in.menu,      "/user/hand/right/input/menu/click" },
        { g_in.haptic,    "/user/hand/left/output/haptic" },
        { g_in.haptic,    "/user/hand/right/output/haptic" },
    }, {
        { g_in.thumbTouch, "/user/hand/left/input/trackpad/touch" },
        { g_in.thumbTouch, "/user/hand/right/input/trackpad/touch" },
    });

    suggest(instance, "/interaction_profiles/microsoft/motion_controller", {
        { g_in.aimPose,    "/user/hand/left/input/aim/pose" },
        { g_in.aimPose,    "/user/hand/right/input/aim/pose" },
        { g_in.gripPose,   "/user/hand/left/input/grip/pose" },
        { g_in.gripPose,   "/user/hand/right/input/grip/pose" },
        { g_in.trigger,    "/user/hand/left/input/trigger/value" },
        { g_in.trigger,    "/user/hand/right/input/trigger/value" },
        { g_in.squeeze,    "/user/hand/left/input/squeeze/click" },
        { g_in.squeeze,    "/user/hand/right/input/squeeze/click" },
        { g_in.thumbstick, "/user/hand/left/input/thumbstick" },
        { g_in.thumbstick, "/user/hand/right/input/thumbstick" },
        { g_in.menu,       "/user/hand/left/input/menu/click" },
        { g_in.thumbClick, "/user/hand/left/input/thumbstick/click" },
        { g_in.thumbClick, "/user/hand/right/input/thumbstick/click" },
        { g_in.menu,       "/user/hand/right/input/menu/click" },
        { g_in.haptic,     "/user/hand/left/output/haptic" },
        { g_in.haptic,     "/user/hand/right/output/haptic" },
    });

    for (uint32_t hand = 0; hand < kHands; ++hand)
    {
        XrActionSpaceCreateInfo spaceInfo{ XR_TYPE_ACTION_SPACE_CREATE_INFO };
        spaceInfo.poseInActionSpace.orientation.w = 1.0f;
        spaceInfo.subactionPath = g_in.handPath[hand];

        spaceInfo.action = g_in.aimPose;
        ok(xrCreateActionSpace(session, &spaceInfo, &g_in.aimSpace[hand]), "aim space");

        spaceInfo.action = g_in.gripPose;
        ok(xrCreateActionSpace(session, &spaceInfo, &g_in.gripSpace[hand]), "grip space");
    }

    /* TEST 428: the eye tracker, for foveated rendering that follows the gaze.
       On PSVR2 the gaze reaches SteamVR only through PSVR2Toolkit. */
    if (eyeGaze)
    {
        XrSystemGetInfo sysInfo{ XR_TYPE_SYSTEM_GET_INFO };
        sysInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        XrSystemId system = XR_NULL_SYSTEM_ID;
        XrSystemEyeGazeInteractionPropertiesEXT gazeProps{
            XR_TYPE_SYSTEM_EYE_GAZE_INTERACTION_PROPERTIES_EXT };
        XrSystemProperties sysProps{ XR_TYPE_SYSTEM_PROPERTIES };
        sysProps.next = &gazeProps;
        const bool known = XR_SUCCEEDED(xrGetSystem(instance, &sysInfo, &system)) &&
                           XR_SUCCEEDED(xrGetSystemProperties(instance, system, &sysProps));

        g_in.gaze = make_action(g_in.actionSet, XR_ACTION_TYPE_POSE_INPUT,
                                "eye_gaze", "Eye gaze", nullptr, 0);
        suggest(instance, "/interaction_profiles/ext/eye_gaze_interaction", {
            { g_in.gaze, "/user/eyes_ext/input/gaze_ext/pose" },
        });

        XrActionSpaceCreateInfo gazeInfo{ XR_TYPE_ACTION_SPACE_CREATE_INFO };
        gazeInfo.action = g_in.gaze;
        gazeInfo.poseInActionSpace.orientation.w = 1.0f;
        XrReferenceSpaceCreateInfo viewInfo{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
        viewInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
        viewInfo.poseInReferenceSpace.orientation.w = 1.0f;
        if (g_in.gaze == XR_NULL_HANDLE ||
            !ok(xrCreateActionSpace(session, &gazeInfo, &g_in.gazeSpace), "gaze space") ||
            !ok(xrCreateReferenceSpace(session, &viewInfo, &g_in.viewSpace), "view space"))
        {
            g_in.gaze = XR_NULL_HANDLE;
        }

        BVR_INFO("Input: eye gaze action %s (TEST 428); the system says eye gaze is %s.",
                 g_in.gaze != XR_NULL_HANDLE ? "bound" : "NOT bound",
                 !known ? "unknown" : gazeProps.supportsEyeGazeInteraction ? "supported"
                                                                             : "NOT supported");
    }

    XrSessionActionSetsAttachInfo attach{ XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO };
    attach.countActionSets = 1;
    attach.actionSets = &g_in.actionSet;

    if (!ok(xrAttachSessionActionSets(session, &attach), "xrAttachSessionActionSets"))
        return false;

    g_in.session = session;
    g_in.ready = true;
    BVR_INFO("Input: action set attached; both controllers are being tracked from "
             "here. Nothing consumes this yet - it is the floor the motion combat "
             "stands on.");
    return true;
}

void input_sync(XrSession session, XrSpace baseSpace, XrTime displayTime)
{
    if (!g_in.ready || baseSpace == XR_NULL_HANDLE)
        return;

    XrActiveActionSet active{ g_in.actionSet, XR_NULL_PATH };

    XrActionsSyncInfo sync{ XR_TYPE_ACTIONS_SYNC_INFO };
    sync.countActiveActionSets = 1;
    sync.activeActionSets = &active;

    const XrResult synced = xrSyncActions(session, &sync);
    if (XR_FAILED(synced))
        return;

    /* XR_SESSION_NOT_FOCUSED is a success code, and it means the runtime is
       deliberately withholding input - a system menu is up. Everything below
       would read zeros, which is correct, so there is nothing to special-case. */

    const int next = 1 - g_in.index.load(std::memory_order_relaxed);
    BvrInputState& out = g_in.buffers[next];
    std::memset(&out, 0, sizeof(out));

    BvrHandState* hands[kHands] = { &out.left, &out.right };
    bool anyTracked = false;
    uint32_t touch = 0;

    for (uint32_t hand = 0; hand < kHands; ++hand)
    {
        BvrHandState& state = *hands[hand];
        bool tracked = false;

        locate_hand(g_in.aimSpace[hand], baseSpace, displayTime,
                    &state.aimOrientation, &state.aimPosition, nullptr, &tracked);

        locate_hand(g_in.gripSpace[hand], baseSpace, displayTime,
                    &state.gripOrientation, &state.gripPosition,
                    &state.linearVelocity, nullptr);

        state.trigger = read_float(session, g_in.trigger, g_in.handPath[hand]);
        state.grip = read_float(session, g_in.squeeze, g_in.handPath[hand]);
        read_vector2(session, g_in.thumbstick, g_in.handPath[hand],
                     &state.thumbstickX, &state.thumbstickY);

        int32_t buttons = 0;
        if (read_bool(session, g_in.primary, g_in.handPath[hand]))   buttons |= 0x1;
        if (read_bool(session, g_in.secondary, g_in.handPath[hand])) buttons |= 0x2;
        /* Bit 2, which the header records as unbound because Touch has no third
           face button. The stick click is exactly what it was being saved for. */
        if (read_bool(session, g_in.thumbClick, g_in.handPath[hand])) buttons |= 0x4;
        if (read_bool(session, g_in.menu, g_in.handPath[hand]))      buttons |= 0x8;
        if (state.trigger > 0.7f)                                    buttons |= 0x10;
        if (state.grip > 0.7f)                                       buttons |= 0x20;
        state.buttons = buttons;

        state.isActive = tracked ? 1 : 0;
        anyTracked = anyTracked || tracked;

        touch |= (read_touch(session, g_in.triggerTouch, g_in.handPath[hand], BVR_TOUCH_TRIGGER, BVR_TOUCH_TRIGGER_BOUND)
                  | read_touch(session, g_in.thumbTouch, g_in.handPath[hand], BVR_TOUCH_THUMB, BVR_TOUCH_THUMB_BOUND)
                  | read_touch(session, g_in.thumbrestTouch, g_in.handPath[hand], BVR_TOUCH_THUMBREST, 0u))
                 << (8 * hand);
    }

    g_in.index.store(next, std::memory_order_release);
    g_touch.store(touch, std::memory_order_relaxed);

    /* TEST 452: when, and for what, so input_latch can locate for its own moment. */
    {
        LARGE_INTEGER q;
        QueryPerformanceCounter(&q);
        std::lock_guard<std::mutex> lock(g_syncMutex);
        const XrTime step = displayTime - g_in.syncTime;
        if (g_in.syncTime != 0 && step > 4000000 && step < 30000000)
            g_in.period = step;
        g_in.syncTime = displayTime;
        g_in.syncQpc = q.QuadPart;
        g_in.syncBase = baseSpace;
    }

    if (g_in.gaze != XR_NULL_HANDLE)
        sync_gaze(session, displayTime);

    if (anyTracked && !g_in.loggedTracking)
    {
        g_in.loggedTracking = true;
        BVR_INFO("Input: controllers are tracking. L (%.2f, %.2f, %.2f) "
                 "R (%.2f, %.2f, %.2f), in stage space.",
                 out.left.gripPosition.x, out.left.gripPosition.y, out.left.gripPosition.z,
                 out.right.gripPosition.x, out.right.gripPosition.y, out.right.gripPosition.z);
    }

    /* The profile is often not known at session start; re-check it periodically
       so the log names it when it appears (TEST 238g). The hand report below
       shares the same tick counter. */
    if (anyTracked && ++g_in.reportTick >= 450)
    {
        g_in.reportTick = 0;
        report_profile(g_in.instance, session);

        const BvrVec3& v = out.right.linearVelocity;
        const float speed = std::sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        BVR_INFO("Input: right hand at (%.2f, %.2f, %.2f), speed %.2f m/s, "
                 "trigger %.2f, grip %.2f, buttons 0x%X.",
                 out.right.gripPosition.x, out.right.gripPosition.y,
                 out.right.gripPosition.z, speed,
                 out.right.trigger, out.right.grip, out.right.buttons);
    }
}

/* TEST 452. "Circles with the hand jump, it feels like it skips", with or
   without the body. input_sync runs on the RENDER thread at present time and the
   game thread posed the hands from whichever buffer it last wrote; the threads
   run in parallel, so some game frames got the same sample twice and the next
   one two steps at once - the hand skipped while the view (late-latched) stayed
   smooth. So once per game frame the hands are located HERE, on the game thread,
   for this moment: the last synced display time plus the real time since that
   sync (the same lead the render thread had), plus leadFrames display periods
   (a game frame is drawn a frame after it poses). Each game frame gets its own,
   evenly spaced sample. Buttons, sticks and triggers are the last sync's.
   xrLocateSpace needs no external synchronisation. */
bool input_latch(float leadFrames)
{
    BvrInputState s{};
    if (!input_read(&s))
        return false;

    XrTime t0 = 0, period = 0;
    int64_t q0 = 0;
    XrSpace base = XR_NULL_HANDLE;
    {
        std::lock_guard<std::mutex> lock(g_syncMutex);
        t0 = g_in.syncTime;
        q0 = g_in.syncQpc;
        period = g_in.period;
        base = g_in.syncBase;
    }

    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);

    /* The probe: had the render thread's sample moved since the last latch? */
    if (s.right.isActive && std::memcmp(&g_in.lastRaw, &s.right.gripPosition, sizeof(BvrVec3)) == 0)
        g_in.repeatCount++;
    g_in.lastRaw = s.right.gripPosition;

    BvrHandState* hands[kHands] = { &s.left, &s.right };
    bool located = false;
    const int64_t ticks = now.QuadPart - q0;
    if (t0 != 0 && base != XR_NULL_HANDLE && ticks >= 0 && ticks < freq.QuadPart / 10)
    {
        const XrTime age = static_cast<XrTime>(ticks * 1000000000LL / freq.QuadPart);
        const XrTime lead = static_cast<XrTime>(static_cast<double>(leadFrames) * static_cast<double>(period));
        const XrTime t = t0 + age + lead;
        for (uint32_t hand = 0; hand < kHands; ++hand)
        {
            if (!hands[hand]->isActive)
                continue;
            locate_hand(g_in.aimSpace[hand], base, t,
                        &hands[hand]->aimOrientation, &hands[hand]->aimPosition, nullptr, nullptr);
            locate_hand(g_in.gripSpace[hand], base, t,
                        &hands[hand]->gripOrientation, &hands[hand]->gripPosition,
                        &hands[hand]->linearVelocity, nullptr);
        }
        located = true;
        g_in.leadSumMs += static_cast<double>(age + lead) / 1e6;
    }

    g_in.latched = s;
    g_in.latchedQpc = now.QuadPart;

    if (++g_in.latchCount >= 450)
    {
        BVR_INFO("TEST 452 hands: over %u game frames the render thread's controller sample had "
                 "not moved on %u (the skip); each frame now locates the hands for itself, %.1f ms "
                 "past the last synced display time on average (lead %.2f frames of %.1f ms).",
                 g_in.latchCount, g_in.repeatCount, g_in.leadSumMs / g_in.latchCount,
                 leadFrames, static_cast<double>(period) / 1e6);
        g_in.latchCount = 0;
        g_in.repeatCount = 0;
        g_in.leadSumMs = 0.0;
    }
    return located;
}

bool input_read_game(BvrInputState* out)
{
    if (out == nullptr)
        return false;

    /* Buttons, sticks and triggers always the newest sync's; the POSES this
       frame's latch while it is fresh. Outside a mission (menus, the map)
       nothing latches and the render thread's sync is all there is. */
    if (!input_read(out))
        return false;

    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    if (g_in.latchedQpc != 0 && now.QuadPart - g_in.latchedQpc < freq.QuadPart / 10)
    {
        /* aim, grip and velocity lead the struct, ahead of the trigger */
        constexpr size_t poseBytes = offsetof(BvrHandState, trigger);
        if (g_in.latched.left.isActive && out->left.isActive)
            std::memcpy(&out->left, &g_in.latched.left, poseBytes);
        if (g_in.latched.right.isActive && out->right.isActive)
            std::memcpy(&out->right, &g_in.latched.right, poseBytes);
    }
    return true;
}

bool input_read(BvrInputState* out)
{
    if (out == nullptr || !g_in.ready)
        return false;

    const int current = g_in.index.load(std::memory_order_acquire);
    *out = g_in.buffers[current];
    return true;
}

bool input_haptic(int hand, float amplitude, float durationSeconds)
{
    if (hand < 0 || hand >= static_cast<int>(kHands))
        return false;

    if (!(amplitude > 0.0f) || !(durationSeconds > 0.0f))
        return false;

    std::lock_guard<std::mutex> lock(g_hapticMutex);

    if (!g_in.ready || g_in.session == XR_NULL_HANDLE || g_in.haptic == XR_NULL_HANDLE)
        return false;

    XrHapticActionInfo info{ XR_TYPE_HAPTIC_ACTION_INFO };
    info.action = g_in.haptic;
    info.subactionPath = g_in.handPath[hand];

    /* TEST 413: the PSVR2's Sense controllers felt nothing (other games buzz).
       0 is the runtime's default frequency - fine for the Touch, Index and Vive
       motors, which ignore it - but a voice coil is DRIVEN at a frequency, and
       most of our pulses (8-12 ms ticks) are too short for one to play. Both are
       keys, defaulting to the old behaviour: haptic_frequency_hz (0 = runtime's
       choice) and haptic_min_ms (0 = as asked). */
    static const float frequency = config_float("haptic_frequency_hz", 0.0f);
    static const float minSeconds = config_float("haptic_min_ms", 0.0f) / 1000.0f;
    if (durationSeconds < minSeconds)
        durationSeconds = minSeconds;

    XrHapticVibration vibration{ XR_TYPE_HAPTIC_VIBRATION };
    vibration.amplitude = amplitude > 1.0f ? 1.0f : amplitude;
    vibration.duration = static_cast<XrDuration>(durationSeconds * 1e9);
    vibration.frequency = frequency > 0.0f ? frequency : 0.0f;

    const XrResult r = xrApplyHapticFeedback(
        g_in.session, &info,
        reinterpret_cast<const XrHapticBaseHeader*>(&vibration));

    if (XR_FAILED(r))
    {
        BVR_ERR("Input: xrApplyHapticFeedback failed (%d).", static_cast<int>(r));
        return false;
    }

    if (!g_in.loggedHaptic)
    {
        g_in.loggedHaptic = true;
        BVR_INFO("Input: first haptic pulse applied (%s hand, amplitude %.2f, %.0f ms, "
                 "%.0f Hz (0 = runtime's choice), result %d - TEST 413). "
                 "If nothing can be felt from here, the runtime or the controller is "
                 "not delivering it.",
                 hand == 0 ? "left" : "right", amplitude, durationSeconds * 1000.0f,
                 vibration.frequency, static_cast<int>(r));
    }

    return true;
}

XrSpace input_grip_space(int hand)
{
    if (!g_in.ready || hand < 0 || hand >= static_cast<int>(kHands))
        return XR_NULL_HANDLE;
    return g_in.gripSpace[hand];
}

bool input_gaze(float* x, float* y)
{
    const uint64_t packed = g_gazePacked.load(std::memory_order_relaxed);
    float xy[2];
    std::memcpy(xy, &packed, sizeof(xy));
    if (std::isnan(xy[0]))
        return false;
    if (x) *x = xy[0];
    if (y) *y = xy[1];
    return true;
}

bool input_gaze_bound()
{
    return g_in.gaze != XR_NULL_HANDLE;
}

uint32_t input_finger_touch()
{
    return g_touch.load(std::memory_order_relaxed);
}

void input_destroy()
{
    for (XrSpace* s : { &g_in.gazeSpace, &g_in.viewSpace })
    {
        if (*s != XR_NULL_HANDLE)
        {
            xrDestroySpace(*s);
            *s = XR_NULL_HANDLE;
        }
    }
    g_in.gaze = XR_NULL_HANDLE;   /* goes with the action set below */
    g_in.gazeValidTick = 0;
    g_in.loggedGaze = false;
    publish_gaze(false, 0.0f, 0.0f);

    for (uint32_t hand = 0; hand < kHands; ++hand)
    {
        if (g_in.aimSpace[hand] != XR_NULL_HANDLE)
        {
            xrDestroySpace(g_in.aimSpace[hand]);
            g_in.aimSpace[hand] = XR_NULL_HANDLE;
        }
        if (g_in.gripSpace[hand] != XR_NULL_HANDLE)
        {
            xrDestroySpace(g_in.gripSpace[hand]);
            g_in.gripSpace[hand] = XR_NULL_HANDLE;
        }
    }

    {
        /* Clear the session and the action before either is destroyed: a haptic
           call from the game thread in the window between the two would use a
           dead handle, and the destroy must not race one either. */
        std::lock_guard<std::mutex> lock(g_hapticMutex);
        g_in.haptic = XR_NULL_HANDLE;
        g_in.session = XR_NULL_HANDLE;
    }

    if (g_in.actionSet != XR_NULL_HANDLE)
    {
        xrDestroyActionSet(g_in.actionSet);
        g_in.actionSet = XR_NULL_HANDLE;
    }

    g_in.triggerTouch = g_in.thumbTouch = g_in.thumbrestTouch = XR_NULL_HANDLE;   /* went with the set */
    g_touch.store(0, std::memory_order_relaxed);

    g_in.ready = false;
    g_in.loggedTracking = false;
    g_in.loggedHaptic = false;
}

} // namespace bvr
