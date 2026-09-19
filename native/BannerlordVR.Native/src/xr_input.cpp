#include "xr_input.h"
#include "bvr_log.h"

#include <atomic>
#include <cmath>
#include <cstring>
#include <vector>

namespace bvr {
namespace {

constexpr uint32_t kHands = 2;   /* 0 = left, 1 = right */

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

    XrSpace aimSpace[kHands] = {};
    XrSpace gripSpace[kHands] = {};

    bool ready = false;

    /* Single writer on the render thread, single reader on the managed thread.
       Same double-buffer arrangement the eye poses use: the reader takes the
       index first, so it can only ever see a completely written buffer or the
       previous one. */
    BvrInputState buffers[2] = {};
    std::atomic<int> index{ 0 };

    bool loggedProfile = false;
    bool loggedTracking = false;
    int  reportTick = 0;
};

InputState g_in;

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
void suggest(XrInstance instance, const char* profile,
             const std::vector<std::pair<XrAction, const char*>>& pairs)
{
    XrPath profilePath = path_of(instance, profile);
    if (profilePath == XR_NULL_PATH)
        return;

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
        return;

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

void report_profile(XrInstance instance, XrSession session)
{
    if (g_in.loggedProfile)
        return;

    g_in.loggedProfile = true;

    for (uint32_t hand = 0; hand < kHands; ++hand)
    {
        XrInteractionProfileState state{ XR_TYPE_INTERACTION_PROFILE_STATE };
        if (XR_FAILED(xrGetCurrentInteractionProfile(session, g_in.handPath[hand], &state)))
            continue;

        if (state.interactionProfile == XR_NULL_PATH)
        {
            BVR_INFO("Input: %s hand has no interaction profile yet - nothing is "
                     "bound until the controller wakes up.", hand == 0 ? "left" : "right");
            continue;
        }

        char name[XR_MAX_PATH_LENGTH] = {};
        uint32_t written = 0;
        if (XR_SUCCEEDED(xrPathToString(instance, state.interactionProfile,
                                        sizeof(name), &written, name)))
        {
            BVR_INFO("Input: %s hand bound to %s.", hand == 0 ? "left" : "right", name);
        }
    }
}

} // namespace

bool input_create(XrInstance instance, XrSession session)
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

    XrSessionActionSetsAttachInfo attach{ XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO };
    attach.countActionSets = 1;
    attach.actionSets = &g_in.actionSet;

    if (!ok(xrAttachSessionActionSets(session, &attach), "xrAttachSessionActionSets"))
        return false;

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
    }

    g_in.index.store(next, std::memory_order_release);

    if (anyTracked && !g_in.loggedTracking)
    {
        g_in.loggedTracking = true;
        report_profile(g_in.instance, session);
        BVR_INFO("Input: controllers are tracking. L (%.2f, %.2f, %.2f) "
                 "R (%.2f, %.2f, %.2f), in stage space.",
                 out.left.gripPosition.x, out.left.gripPosition.y, out.left.gripPosition.z,
                 out.right.gripPosition.x, out.right.gripPosition.y, out.right.gripPosition.z);
    }

    /* Once every few seconds while tracking, so a run can be read afterwards
       without a headset: position, speed and the trigger. Speed is what the
       swing threshold will eventually be set against, so seeing its real range
       during ordinary play is the point of printing it now. */
    if (anyTracked && ++g_in.reportTick >= 450)
    {
        g_in.reportTick = 0;

        const BvrVec3& v = out.right.linearVelocity;
        const float speed = std::sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        BVR_INFO("Input: right hand at (%.2f, %.2f, %.2f), speed %.2f m/s, "
                 "trigger %.2f, grip %.2f, buttons 0x%X.",
                 out.right.gripPosition.x, out.right.gripPosition.y,
                 out.right.gripPosition.z, speed,
                 out.right.trigger, out.right.grip, out.right.buttons);
    }
}

bool input_read(BvrInputState* out)
{
    if (out == nullptr || !g_in.ready)
        return false;

    const int current = g_in.index.load(std::memory_order_acquire);
    *out = g_in.buffers[current];
    return true;
}

void input_destroy()
{
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

    if (g_in.actionSet != XR_NULL_HANDLE)
    {
        xrDestroyActionSet(g_in.actionSet);
        g_in.actionSet = XR_NULL_HANDLE;
    }

    g_in.ready = false;
    g_in.loggedProfile = false;
    g_in.loggedTracking = false;
}

} // namespace bvr
