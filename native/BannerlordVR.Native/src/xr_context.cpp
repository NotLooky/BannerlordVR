#include "xr_context.h"
#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "stereo_dup.h"
#include "vr_sharpen.h"
#include "vp_patch.h"
#include "vr_overlay.h"
#include "afw_warp.h"
#include "image_latch.h"
#include "late_latch.h"
#include "depth_upscale.h"
#include "ui_key.h"
#include "vr_reticle.h"
#include "xr_input.h"

#include <d3d11.h>

/* XR_USE_GRAPHICS_API_D3D11 and XR_USE_PLATFORM_WIN32 come from CMake. d3d11.h
   must be included before openxr_platform.h for the D3D11 structs to appear. */
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <mutex>
#include <vector>

namespace bvr {
namespace {

constexpr uint32_t kEyeCount = 2;
constexpr XrViewConfigurationType kViewConfig = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;

struct EyeSwapchain
{
    XrSwapchain handle = XR_NULL_HANDLE;
    uint32_t width = 0;
    uint32_t height = 0;
    std::vector<XrSwapchainImageD3D11KHR> images;
    std::vector<ID3D11RenderTargetView*> rtvs;

    /* The depth image submitted alongside the colour one, when the runtime
       supports XR_KHR_composition_layer_depth. No views are needed: nothing
       ever RENDERS into this, it only ever receives a CopyResource from the
       depth buffer the game drew with. */
    XrSwapchain depthHandle = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageD3D11KHR> depthImages;

    /* Its own size, which is the GAME's depth resolution and not the eye's.
       The subimage rect must describe THIS image - quoting the colour size
       would tell the compositor to sample a rectangle twice the size of the
       one that exists. */
    uint32_t depthWidth = 0;
    uint32_t depthHeight = 0;
};

struct XrState
{
    XrInstance instance = XR_NULL_HANDLE;
    XrSystemId systemId = XR_NULL_SYSTEM_ID;
    XrSession  session  = XR_NULL_HANDLE;
    XrSpace    appSpace = XR_NULL_HANDLE;

    XrSessionState sessionState = XR_SESSION_STATE_UNKNOWN;
    bool sessionRunning = false;
    bool sessionRequested = false;
    bool sessionCreateFailed = false;

    XrViewConfigurationView viewConfigs[kEyeCount] = {};
    EyeSwapchain swapchains[kEyeCount];

    int64_t swapchainFormat = 0;
    int64_t depthFormat = 0;

    /* Eye copy path. Checked once - a format or size mismatch is a property of
       the session, and re-testing it at 90 Hz would only flood the log. */
    bool copyChecked = false;
    bool copyUsable = false;

    /* --- alternate-frame rendering ---------------------------------------
       One scene render per frame, alternating which eye it is for. Each eye's
       last image is kept here so BOTH eyes can be submitted every frame: an
       OpenXR swapchain hands back a different image each acquire, so "leave the
       other eye alone" is not an option - that image holds a frame from three
       acquires ago. */
    std::atomic<int> afrEye{-1};

    /* Whether the eye index published for THIS tick was actually consumed by a
       scene capture.
     *
       The managed side toggles _eye once per game tick and publishes it before
       the engine draws; the Present hook then captures whatever was drawn and
       labels it with that index. The two live on different clocks. A tick that
       produces no scene render - a hitch, a menu frame, a load - advances the
       toggle without a capture, and from then on every captured frame is
       labelled with the WRONG eye.

       That is not a small error. p.dir is derived straight from srcEye, so a
       flipped label reverses the direction of the whole warp: the disparity is
       applied backwards and the synthesised eye lands two full disparities out.
       The residual meter measured exactly that - a bad window of 96 near blocks
       with 81 classed "wrong way" and a mean residual of 60.6 px against a mean
       movement of 31.4, a ratio of 1.93 where a correct warp leaves ~0 - while a
       quiet window minutes earlier, at the same distances and the same movement,
       came back with 47 of 72 blocks correct.

       It also explains everything the artefact did: both eyes go bad together
       because one sign serves both directions, it persists for seconds until the
       parity slips back, it scales as 1/z so near objects flick hardest, and
       pinning the source eye removed it completely because a pinned eye has no
       parity to lose.

       Cleared when a new index is published, set when a capture consumes it. The
       managed side reads it back and only advances the toggle once the previous
       eye was really drawn. */
    std::atomic<bool> afrEyeUsed{false};

    /* WHICH EYE THE ENGINE SHOULD DRAW NEXT - owned HERE, not by the tick.
     *
       The managed side used to own the alternation (_eye ^= 1 once per game
       tick) and publish it for the Present hook to read. Those are two different
       clocks with nothing tying them together, and the instrumentation measured
       the consequence directly: 1499 of 18267 captures - 8.2%, spread over 152
       seconds out of 205 - read an eye index the tick had already moved past.
       Each of those labels a frame with the wrong eye, and p.dir comes straight
       from the label, so the disparity is applied backwards and the synthesised
       eye lands two full disparities out.

       The correlation against the residual meter is exact. While parity slips
       sat at 0-1 per second the meter found 0 "wrong way" blocks; the second
       slips rose to 8, then 23, 21, 21, 17, the meter found 6, 11, 6, 6, 5. Not
       a frame-rate effect either - slips run at the same rate at a healthy 90 fps
       as at 80.

       Inverting the ownership removes the race instead of narrowing it. This
       counter advances on the SAME event that consumes it - a real capture - so
       it cannot drift from the captures. Managed reads it before setting the
       camera and renders whatever it is told.

       The worst remaining case is benign by comparison: if a tick reads this
       before the previous frame's capture has toggled it, the same eye is drawn
       twice. That is a momentary stereo hiccup, and the label still MATCHES the
       frame - which is the thing that actually mattered. */
    std::atomic<int> afrNextEye{0};

    /* >= 0 while the managed side is holding the engine camera on ONE eye
       (afw_pin_source_eye). -1 means it is alternating and the capture path owns
       the sequence. Kept separate so pinning never has to fight the toggle. */
    std::atomic<int> afrPinnedEye{-1};

    /* THE EYE THE CAMERA IS ACTUALLY SET TO.
     *
       Written when the managed tick ASKS for an eye, because that is the moment
       the camera is configured for it. The capture labels the frame with this,
       not with the sequence counter.
     *
       The distinction matters and my previous parity counter could not see it.
       Once the capture path owned afrNextEye and toggled it per capture, the
       labels alternated BY CONSTRUCTION - the counter was reading back its own
       output and could only ever report zero. It did, and that proved nothing.
     *
       What it could not see is a capture with no managed tick in between. The
       camera is unchanged in that case, so the frame is the SAME eye again - but
       the sequence had already advanced, so the frame was labelled with the
       other one. p.dir follows the label, so the warp is signed backwards for
       exactly those frames. The residual meter kept reporting ~30% of near
       blocks landing at twice the disparity with a sharp peak at 2.0 all the way
       through, which is that, and it never moved because none of the fixes
       addressed it.
     *
       Labelling from what managed actually read makes the label follow the
       camera by definition. A repeated eye now stays correctly labelled instead
       of being mislabelled as its opposite. */
    std::atomic<int> afrCameraEye{0};
    ID3D11Texture2D* afrResult[kEyeCount] = { nullptr, nullptr };

    /* The last REAL render of each eye, kept apart from afrResult because under
       AFW afrResult[e] is overwritten by the warp every other frame. This is
       what a disocclusion is filled from: geometry the SOURCE eye cannot
       contain, because a disocclusion is by definition what the source could
       not see, but which this eye rendered for itself one frame ago. */
    ID3D11Texture2D* afwReal[kEyeCount] = { nullptr, nullptr };
    bool             afwRealHas[kEyeCount] = { false, false };

    /* The last SYNTHESISED frame of each eye, which is what a disocclusion is
       filled from when there is no real render to use.
     *
       With afw_pin_source_eye the engine only ever draws one eye, so afwReal
       for the other is never written and the fill above is starved. What was
       left was the last resort - stretching the background sideways across the
       hole - and at the right-hand edge of a near object the hole is as wide as
       the disparity step across that edge, which is where the stretched edges
       come from.

       The previous synthesised frame is not real, but it is a warp of a real
       render and is therefore correct everywhere except in ITS own holes. Head
       movement slides this frame's holes off last frame's, so most of what it
       supplies is genuine geometry rather than a guess.

       Deliberately NOT stored in afwReal, which the hybrid substitutes whole as
       a held REAL eye: putting a synthesised frame there would have the hybrid
       submit a warp while believing it was submitting an engine render. Two
       different questions, two textures. */
    ID3D11Texture2D* afwSynth[kEyeCount] = { nullptr, nullptr };
    bool             afwSynthHas[kEyeCount] = { false, false };

    /* The pose each real render was actually drawn from, and the body yaw with
       it. Needed because a held REAL eye must be submitted with the pose it was
       drawn at - that is what lets the compositor reproject it - and not with
       the pose of the frame that happens to be submitting it. */
    XrPosef          afwRealPose[kEyeCount] = {};
    float            afwRealYaw[kEyeCount] = { 0.0f, 0.0f };

    /* Which half of the hybrid is running, and the smoothed head speed the
       choice is made on. Render-thread only. */
    bool             stillAfr = false;
    float            stillMotion = 0.0f;
    bool  afrHasResult[kEyeCount] = { false, false };

    /* The DEPTH that goes with each held colour image, and it has to be held in
       exactly the same way and promoted at exactly the same moment. A held eye
       submitted with the depth of the frame the OTHER eye was drawn from would
       hand the compositor a disparity map that disagrees with the picture it is
       correcting, which is worse than sending no depth at all. */
    ID3D11Texture2D* afrDepthResult[kEyeCount] = { nullptr, nullptr };
    ID3D11Texture2D* afrDepthStage[kEyeCount] = { nullptr, nullptr };
    bool afrDepthHas[kEyeCount] = { false, false };
    bool afrDepthStageHas[kEyeCount] = { false, false };

    /* Eyes submitted WITH depth in the last second. Two per frame is the
       healthy number; zero with the layer reported up means the depth is being
       dropped somewhere between capture and submit, which is worth seeing in
       the log rather than inferring from how the headset feels. */
    uint32_t depthSubmitted = 0;

    /* The pose each eye was actually rendered with. Submitting an eye with the
       CURRENT pose when its image is a frame old is what makes naive AFR swim;
       giving the compositor the true pose lets it reproject correctly. */
    XrPosef afrPose[kEyeCount] = {};
    XrFovf  afrFov[kEyeCount] = {};

    /* Previously located generations, kept so the AFR capture can be attributed
       to the pose its image was RENDERED from rather than the one located a
       moment ago.

       The frame the engine hands us at Present N was drawn during the tick
       BEFORE that Present, and that tick read the pose published by the locate
       at Present N-1. Tagging it with the fresh generation asks the compositor
       to reproject from a camera the image was never drawn with, and the error
       is proportional to how fast the head is moving - i.e. a world that swims
       against head motion and sits still when the head does.

       How many generations back the content actually sits is a property of the
       engine's own pipelining, not something derivable here, so `afr_pose_lag`
       selects it: 0 = the fresh locate (the behaviour before this was
       measured), 1 = one generation back (the default, and the depth this
       ordering implies), 2 = two. */
    XrView viewsHist[2][kEyeCount] = {};
    bool   viewsHistValid[2] = { false, false };

    /* How long each eye's held image has been on screen. Under healthy AFR both
       eyes refresh every other frame, so the worst age over a second should sit
       near two frame times. A number far above that means one eye is being
       submitted stale for many frames running - which is what a viewer reports
       as flicker rather than as judder, and it is worth being able to tell the
       two apart without a headset. */
    uint64_t afrLastCaptureMs[kEyeCount] = { 0, 0 };

    /* WHICH EYE WAS PROMOTED MOST RECENTLY. -1 until the first promotion.
     *
       afrLastCaptureMs cannot answer this and it was asked to. GetTickCount64
       advances in 10-16 ms steps and a frame at 90 Hz is 11.1 ms, so the two
       eyes' stamps land on the SAME tick most of the time - and a >= comparison
       between equal stamps always returns the same eye whichever one was really
       fresh. Anything keyed on that comparison flips at random, which is how a
       steady bias turns into one that pops. */
    int afrLastPromotedEye = -1;
    uint32_t afrAgeMaxMs[kEyeCount] = { 0, 0 };

    /* The game's own camera yaw at the moment each eye's image was captured,
       and the newest one the managed side has published. This is the HALF OF
       THE ROTATION THE RUNTIME CANNOT SEE - the rendered eye frame is
       `anchor * eyeStage`, the compositor is told all about eyeStage, and
       nothing anywhere tells it about the anchor. See turn_corrected_pose. */
    float afrBodyYaw[kEyeCount] = { 0.0f, 0.0f };
    std::atomic<float> bodyYaw{ 0.0f };
    std::atomic<bool>  bodyYawValid{ false };

    /* Where the anchor was when each eye was captured, and where it is now. The
       yaw above covers the game camera TURNING; this covers it MOVING, which on
       a horse is the bigger of the two by a wide margin. See
       move_corrected_pose. */
    float afrBodyPos[kEyeCount][3] = { { 0.0f, 0.0f, 0.0f }, { 0.0f, 0.0f, 0.0f } };
    std::atomic<float> bodyPosX{ 0.0f };
    std::atomic<float> bodyPosY{ 0.0f };
    std::atomic<float> bodyPosZ{ 0.0f };
    std::atomic<bool>  bodyPosValid{ false };

    /* WHERE THE CAMERA IS NOW, as opposed to where the frame was DRAWN from.
     *
       With the pair camera latched, both eyes are rendered from one anchor - the
       thing that makes the stereo correct - and the anchor published above is
       that latched one, because it is what the image actually contains. Compared
       against itself it yields a delta of exactly zero, which is why the turn
       correction sat at 0.00 for the whole of a latched run and could not smooth
       anything: it was being asked how far the camera had moved from where it is.

       So the live anchor is published separately. The rendered one identifies
       what the pixels are; this one is where they should be carried TO. Their
       difference is a real quantity again, and rotation between them is
       depth-free, so mouse turning can be smoothed back to display rate without
       the compositor needing to parallax anything.

       With latching off the two are the same value and everything behaves exactly
       as it did before. */
    std::atomic<float> targetYaw{ 0.0f };
    std::atomic<bool>  targetValid{ false };

    /* The poses the frame was ACTUALLY rendered from, published by managed.
     *
       Normally identical to what our own xrLocateViews returned, and then this
       changes nothing. With the pair camera latched they are not: both eyes are
       DRAWN from one latched head pose, while each is captured on its own frame
       and would otherwise be labelled with that frame's live locate. The
       compositor then reprojects the two eyes by different amounts - a
       differential error, which is a disparity the eyes cannot fuse rather than
       a wobble they can ignore. That is eye strain and an apparent change of
       scale, appearing only once something moves. */
    XrPosef usedPose[kEyeCount] = {};

    /* THE LATE LATCH'S ORIENTATION FOR AN EYE, AWAITING THAT EYE'S CAPTURE.
       Written on the engine's matrix-building thread (late_latch.cpp) just
       before the frame is built from it, consumed by afr_capture when that frame
       is captured. latchOri is written before latchHas is released. */
    BvrQuat               latchOri[kEyeCount] = {};
    std::atomic<bool>     latchHas[kEyeCount]{};
    std::atomic<uint64_t> latchCaptures{0};

    /* Writes that found latchHas still set - the build replaced an orientation
       no capture had read. A diagnostic only (TEST 176/177): ~0 in steady play. */
    std::atomic<uint64_t> latchOverwrites{0};
    std::atomic<bool> usedValid{ false };

    /* Display time the used pose was predicted for; 0 until managed publishes. */
    std::atomic<int64_t> usedDisplayTime{ 0 };
    std::atomic<float> targetPosX{ 0.0f };
    std::atomic<float> targetPosY{ 0.0f };
    std::atomic<float> targetPosZ{ 0.0f };
    float movePeakMm = 0.0f;

    /* Largest turn correction applied in the last second, in degrees, purely so
       the log can say whether this is doing anything and by how much. */
    float turnPeakDeg = 0.0f;

    /* The pair being ASSEMBLED, as opposed to the pair on screen.
       See afr_capture: an eye is copied in here first, and a pair is only
       promoted to afrResult once both halves came from the same world state. */
    ID3D11Texture2D* afrStage[kEyeCount] = { nullptr, nullptr };
    bool     afrStageHas[kEyeCount] = { false, false };
    int32_t  afrStageGen[kEyeCount] = { -1, -1 };
    XrPosef  afrStagePose[kEyeCount] = {};
    XrFovf   afrStageFov[kEyeCount] = {};
    float    afrStageBodyYaw[kEyeCount] = { 0.0f, 0.0f };
    float    afrStageBodyPos[kEyeCount][3] = { { 0.0f, 0.0f, 0.0f }, { 0.0f, 0.0f, 0.0f } };

    /* AFW: synthesise the other eye instead of holding it. Runtime-switchable
       from the menu, so it is a flag rather than a config read. */
    std::atomic<bool> afwEnabled{ false };

    /* AFW+AFR hybrid. A SEPARATE mode in the menu rather than a hidden habit of
       AFW: while the head is still the second eye is its own last REAL render,
       and once it moves it is warped from the rendered one. It used to be a
       config default that AFW turned on behind the player, which meant
       "AFW" in the menu was never actually AFW. */
    std::atomic<bool> afwHybrid{ false };

    /* Raised when the renderer stands down, so the render thread knows to
       forget every held eye before the next mission can submit one. Set from
       the game thread and acted on in Present: the holds themselves are
       render-thread property and clearing them from here directly would be a
       cross-thread write to textures being copied. */
    std::atomic<bool> afrForget{ false };

    uint32_t afwFallbacks = 0;

    /* Which simulation tick the world is on, published by the managed side. */
    std::atomic<int32_t> simGen{ -1 };

    uint32_t afrPromotions = 0;
    uint32_t afrHeldForPairing = 0;
    uint32_t afrForcedPromotions = 0;
    uint32_t afrForcedRunLength = 0;
    bool     afrPairingAbandoned = false;
    int      afrFramesSincePromotion = 0;

    /* --- full AFR ---------------------------------------------------------
       Ordinary AFR renders one eye per DISPLAY frame, so each eye is refreshed
       at half the headset's rate and the other one is held or warped. Full AFR
       renders one eye per GAME frame and runs two game frames inside a single
       display frame: eye 0, freeze the world, eye 1, submit the pair. Both eyes
       are then real renders of one simulation tick at the full display rate,
       with nothing held, warped, or reprojected from a frame ago.

       From this layer, the whole of it is that the FIRST frame of a pair does
       not submit. Wait, Begin and Locate happened once at the top of the pair;
       the first Present captures its eye and hands the begun frame BACK rather
       than ending it; the second renders the other eye from that same locate
       and that same world, and ends the frame carrying both.

       It also paces itself with no throttle of its own. xrWaitFrame still
       blocks once per pair, so the second render runs free inside whatever is
       left of the display period - and when the engine cannot fit two renders
       into one period, Wait stops blocking and the pair lands on the next one.
       That is what any VR app does when it misses a deadline; here it is the
       only thing standing between "144 Hz of render" and a machine that cannot
       produce it. */
    std::atomic<bool> fullAfr{ false };

    /* Set by afr_promote_eye, cleared before each capture. Full AFR submits
       exactly when this comes back true: a completed pair, or the pairing
       safety valve deciding to put a single eye up regardless. Keying off
       promotion rather than counting frames is what makes the valve and the
       abandon-pairing path carry full AFR with them instead of deadlocking it -
       whatever reason pairing has for giving up, the frame still ends. */
    bool afrPromotedThisPresent = false;

    /* How many Presents in a row have handed the begun frame back. Never more
       than one while pairs are completing; the cap is what stops a mission that
       has stopped promoting from holding an XR frame open indefinitely. */
    /* Set when the blit has filled the other eye's stage this capture, so the
       labelling below knows to mark it present. */
    bool     secondEyeStaged = false;

    int      afrDeferRun = 0;
    uint32_t afrDeferred = 0;
    uint32_t afrPairsSubmitted = 0;

    /* Pose publication: single writer (render thread), single reader (managed).
       Reader takes the index first, so it can only ever see a fully written
       buffer or the previous one - never a torn mix. */
    BvrEyeView poseBuffers[2][kEyeCount] = {};
    int64_t    poseTimes[2] = {};
    std::atomic<int> poseIndex{0};

    /* False until publish_views() has run at least once. Without it the reader
       cannot tell "here is a pose" from "here is the zero-initialised buffer",
       and hands the managed side a zero quaternion and a zero FOV that look
       exactly like a real answer. Downstream that is a collapsed rotation matrix
       and a frustum with no width. */
    std::atomic<bool> posePublished{false};

    /* --- a frame begun BEFORE the engine renders it ------------------------
     *
       Wait, Begin and Locate normally happen in the Present hook, which is
       after the engine has already drawn - so the pose it drew with was
       located a frame earlier, for a display time that has passed. Every
       reference VR injection does the opposite: the frame is begun at the top
       of the render, the pose is located for the display time of the frame
       about to be drawn, and the camera is built from that.

       Filled by xr_prepare_render_frame on the game's own thread, consumed once
       by render_frame on the render thread. The mutex covers all four fields
       together: a half-published frame state is a frame begun with a pose from
       a different one. */
    std::mutex   frameMutex;
    XrFrameState preparedState{ XR_TYPE_FRAME_STATE };
    XrView       preparedViews[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    bool         preparedValid = false;   /* poses usable */
    bool         framePrepared = false;   /* a frame is begun and unsubmitted */

    /* --- settings panel ---------------------------------------------------
       Its own quad layer, its own swapchain, and a pose that is PINNED when the
       panel opens rather than following the head. A panel that moves with you is
       markedly harder to put a cursor on than one that stays where you left it,
       and it is the difference between adjusting a slider and chasing one. */
    XrSwapchain overlaySwapchain = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageD3D11KHR> overlayImages;
    XrPosef overlayPose{};
    bool overlayPosePinned = false;

    /* --- the flat screen ---------------------------------------------------
       The game's own backbuffer on a quad, for everything that is not a
       mission. See flat_screen_submit(). */
    XrSwapchain flatSwapchain = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageD3D11KHR> flatImages;
    int64_t  flatFormat = 0;
    uint32_t flatWidth = 0;
    uint32_t flatHeight = 0;
    bool flatReady = false;
    bool flatAttempted = false;
    bool flatSizeWarned = false;
    /* Identity, not zero. A zero-initialised XrPosef carries a zero quaternion,
       which is a degenerate rotation rather than "no rotation". */
    XrPosef flatPose{ { 0.0f, 0.0f, 0.0f, 1.0f }, { 0.0f, 0.0f, 0.0f } };
    bool flatPosePinned = false;
    /* Consecutive valid-pose frames seen while unpinned. See flat_screen_submit. */
    uint32_t flatSettleFrames = 0;
    /* True once a pin has ever succeeded. The settle wait is a cold-start
       measure and must not tax every later pin. */
    bool flatEverPinned = false;

    /* --- the keyed UI overlay --------------------------------------------
       The interface composited over the world rather than replacing it. See
       ui_overlay_submit(). */
    XrSwapchain uiSwapchain = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageD3D11KHR> uiImages;
    ID3D11Texture2D* uiSource = nullptr;   /* backbuffer copy the shader can read */
    ID3D11Texture2D* uiKeyed = nullptr;
    ID3D11Texture2D* uiMirror = nullptr;   /* the composed monitor image */
    int64_t  uiFormat = 0;
    uint32_t uiWidth = 0;
    uint32_t uiHeight = 0;
    /* Set once the interface has been keyed this frame, so the headset overlay
       and the monitor mirror share one pass instead of doing it twice. */
    bool uiKeyedThisFrame = false;
    bool uiReady = false;
    bool uiAttempted = false;

    /* Published by the managed side: a menu is open that should float over the
       world rather than take it over. */
    std::atomic<bool> uiOverlay{ false };

    /* Whether a mission exists at all, as distinct from whether the STEREO path
       is live. The two parted company once read-menus started replacing the
       view: the window mirror and the backbuffer clear belong to "there is a
       battle", not to "the headset is showing it in stereo". */
    std::atomic<bool> inMission{ false };

    /* Published by the managed side. False - the startup default - is correct:
       the game opens on the main menu, which is exactly what the flat screen is
       for, and a mission has to announce itself before the stereo path runs. */
    std::atomic<bool> missionActive{ false };

    uint64_t frameCount = 0;
    bool loggedFirstFrame = false;
};

XrState g_xr;

PFN_xrGetD3D11GraphicsRequirementsKHR g_getD3D11Requirements = nullptr;

const char* xr_result_name(XrResult r)
{
    static char buffer[XR_MAX_RESULT_STRING_SIZE];
    if (g_xr.instance != XR_NULL_HANDLE &&
        XR_SUCCEEDED(xrResultToString(g_xr.instance, r, buffer)))
    {
        return buffer;
    }
    _snprintf_s(buffer, _TRUNCATE, "XrResult(%d)", static_cast<int>(r));
    return buffer;
}

bool check(XrResult r, const char* what)
{
    if (XR_SUCCEEDED(r))
        return true;
    BVR_ERR("%s failed: %s", what, xr_result_name(r));
    return false;
}

// ---------------------------------------------------------------------------
// Instance
// ---------------------------------------------------------------------------

/* Set by has_d3d11_extension() while it enumerates, because that is the one
   place the extension list is already in hand. Read when the instance is
   created and again when the depth swapchains are allocated. */
/* Used by several of the geometry helpers below, all of which clamp values
   that arrive from a slider. Defined here so it precedes every one of them. */
float clampf(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

bool g_depthExtAvailable = false;

/* True once the extension is actually ENABLED on the instance, which is a
   different question from the runtime offering it: the config can decline it,
   and xrCreateInstance can still fail for unrelated reasons. Everything
   downstream gates on this rather than on availability. */
bool g_depthExtEnabled = false;

/* Whether the depth swapchains exist and the game's depth can be copied into
   them. False disables submission for the session without disturbing anything
   else - the projection layer simply goes up with no depth attached, which is
   exactly what the mod did before. */
bool g_depthLayerReady = false;

/* Whether the one-shot allocation has already been tried and failed. A file
   scope flag rather than a function-local static so that tearing the session
   down can clear it: a static would make a failure permanent for the life of
   the PROCESS, and the next session would run without depth for a reason that
   no longer applied. */
bool g_depthAttempted = false;

/* True when the game renders depth smaller than the eye - an upscaler is in the
   way - so the depth is resampled to the eye size before submission. See
   depth_upscale.h. */
bool g_depthUpscaling = false;

/* 1 (default) submits a depth layer when the runtime supports one. 0 is the
   control: it restores rotation-only reprojection, which is what every AFR
   session before this shipped with. */
bool depth_layer_allowed()
{
    static int cached = -1;
    if (cached < 0)
        cached = config_bool("depth_layer", true) ? 1 : 0;
    return cached != 0;
}

bool has_d3d11_extension()
{
    uint32_t count = 0;
    if (!check(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr),
               "xrEnumerateInstanceExtensionProperties(count)"))
        return false;

    std::vector<XrExtensionProperties> props(count, { XR_TYPE_EXTENSION_PROPERTIES });
    if (!check(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, props.data()),
               "xrEnumerateInstanceExtensionProperties"))
        return false;

    /* Logged in full, once. Which reprojection and frame-generation extensions
       this runtime exposes decides what is even POSSIBLE here - depth-aware
       reprojection, application space warp, and motion-vector submission are
       all extensions rather than things an app can simply switch on - and
       guessing at it from the outside has already cost more than one round. */
    bool haveD3D11 = false;

    BVR_INFO("Runtime exposes %u OpenXR extension(s):", count);
    for (const XrExtensionProperties& p : props)
    {
        BVR_INFO("    %s (v%u)", p.extensionName, p.extensionVersion);

        if (std::strcmp(p.extensionName, XR_KHR_D3D11_ENABLE_EXTENSION_NAME) == 0)
            haveD3D11 = true;

        /* The one extension that lets the compositor correct the held eye for
           head TRANSLATION as well as rotation. See submit_depth(). */
        if (std::strcmp(p.extensionName, XR_KHR_COMPOSITION_LAYER_DEPTH_EXTENSION_NAME) == 0)
            g_depthExtAvailable = true;
    }

    if (haveD3D11)
        return true;

    BVR_ERR("Runtime does not expose %s. Bannerlord is a D3D11 title; there is no fallback.",
            XR_KHR_D3D11_ENABLE_EXTENSION_NAME);
    return false;
}

// ---------------------------------------------------------------------------
// Swapchains
// ---------------------------------------------------------------------------

/* Prefer sRGB: the engine's eye render targets are sRGB, and letting the
   compositor do the conversion keeps gamma correct without a shader pass.
   Never hardcode - runtimes differ on what they offer. */
int64_t pick_swapchain_format()
{
    uint32_t count = 0;
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, 0, &count, nullptr),
               "xrEnumerateSwapchainFormats(count)"))
        return 0;

    std::vector<int64_t> formats(count);
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, count, &count, formats.data()),
               "xrEnumerateSwapchainFormats"))
        return 0;

    /* BGRA first, deliberately. rgl allocates its render targets as
       DXGI_FORMAT_B8G8R8A8_UNORM (87) - confirmed from the CreateTexture2D hook -
       and CopyResource only accepts source and destination in the same typeless
       family. B8G8R8A8_UNORM and B8G8R8A8_UNORM_SRGB share B8G8R8A8_TYPELESS;
       R8G8B8A8_UNORM_SRGB does not, and the copy would simply be rejected.

       _SRGB rather than plain _UNORM because the engine's target holds
       display-ready, sRGB-encoded pixels. Declaring the swapchain _SRGB is what
       tells the compositor to linearise before blending; the same bits through a
       plain _UNORM swapchain composite as though they were linear and come out
       visibly washed out. */
    const int64_t preferred[] = {
        DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
        DXGI_FORMAT_B8G8R8A8_UNORM,
        DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        DXGI_FORMAT_R8G8B8A8_UNORM,
    };

    for (int64_t want : preferred)
    {
        for (int64_t have : formats)
        {
            if (have == want)
                return want;
        }
    }

    if (!formats.empty())
    {
        BVR_WARN("None of the preferred swapchain formats offered; falling back to %lld.",
                 static_cast<long long>(formats[0]));
        return formats[0];
    }
    return 0;
}

/* The typeless family a depth format belongs to.
 *
 * CopyResource is the whole reason this matters. It refuses anything but a
 * source and destination in the SAME typeless family, so the runtime's depth
 * swapchain format has to match whatever rgl allocated its depth buffer as. We
 * do not get to convert: there is no shader here and no spare frame time to run
 * one in, and a mismatched copy is not a wrong picture but a rejected call. */
DXGI_FORMAT depth_family_of(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_D16_UNORM:
    case DXGI_FORMAT_R16_UNORM:
        return DXGI_FORMAT_R16_TYPELESS;

    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT:
    case DXGI_FORMAT_R32_FLOAT:
        return DXGI_FORMAT_R32_TYPELESS;

    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
    case DXGI_FORMAT_R24_UNORM_X8_TYPELESS:
        return DXGI_FORMAT_R24G8_TYPELESS;

    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
    case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
        return DXGI_FORMAT_R32G8X24_TYPELESS;

    default:
        return DXGI_FORMAT_UNKNOWN;
    }
}

/* Picks the depth swapchain format, and it is NOT a preference list.
 *
 * The choice is forced: it must be in the same typeless family as the depth
 * buffer the game actually drew with, or the copy that fills it cannot happen.
 * So the game's format decides, the runtime's list only says yes or no, and
 * when it says no there is nothing to fall back to - we return 0 and the
 * session runs without a depth layer, exactly as it always has.
 *
 * `gameDepth` is the format rgl allocated. On this build it has been observed
 * as R16_TYPELESS, which is a 16-bit depth buffer - coarse, but the compositor
 * only needs it to separate near geometry from far. */
/* Whether the runtime will hand out a swapchain in exactly this format. Asked
   rather than assumed on the resampled path, where a wrong answer is a silent
   CopyResource that leaves the depth image holding stale contents. */
bool runtime_offers_depth_format(DXGI_FORMAT wanted)
{
    uint32_t count = 0;
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, 0, &count, nullptr),
               "xrEnumerateSwapchainFormats(count)"))
        return false;

    std::vector<int64_t> formats(count);
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, count, &count, formats.data()),
               "xrEnumerateSwapchainFormats"))
        return false;

    for (int64_t have : formats)
    {
        if (static_cast<DXGI_FORMAT>(have) == wanted)
            return true;
    }
    return false;
}

int64_t pick_depth_format(DXGI_FORMAT gameDepth)
{
    const DXGI_FORMAT family = depth_family_of(gameDepth);
    if (family == DXGI_FORMAT_UNKNOWN)
    {
        BVR_WARN("Depth layer: the game's depth buffer is format %d, which is not a "
                 "depth family this knows how to copy. Submitting without depth.",
                 static_cast<int>(gameDepth));
        return 0;
    }

    uint32_t count = 0;
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, 0, &count, nullptr),
               "xrEnumerateSwapchainFormats(depth count)"))
        return 0;

    std::vector<int64_t> formats(count);
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, count, &count, formats.data()),
               "xrEnumerateSwapchainFormats(depth)"))
        return 0;

    for (int64_t have : formats)
    {
        if (depth_family_of(static_cast<DXGI_FORMAT>(have)) == family)
            return have;
    }

    BVR_WARN("Depth layer: the game's depth is family %d but the runtime offers no "
             "swapchain format in it, and CopyResource cannot cross families. "
             "Submitting without depth.", static_cast<int>(family));
    return 0;
}

/* --------------------------------------------------------------------------
 * The settings panel's own swapchain.
 *
 * A quad composition layer, not pixels painted into the eye image. The panel
 * then has a real position and size in metres, so it sits at arm's length with
 * correct stereo instead of at infinity, and the compositor samples it at the
 * panel's own resolution rather than through whatever the eye render target
 * happens to be scaled to today.
 *
 * Failure here is deliberately NOT fatal. A headset that cannot allocate one
 * more small swapchain should still play the game; it just cannot show a menu.
 * ------------------------------------------------------------------------ */
bool create_overlay_swapchain()
{
    const uint32_t w = bvr::overlay_width();
    const uint32_t h = bvr::overlay_height();

    XrSwapchainCreateInfo info{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
    info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT |
                      XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    info.format = g_xr.swapchainFormat;
    info.sampleCount = 1;
    info.width = w;
    info.height = h;
    info.faceCount = 1;
    info.arraySize = 1;
    info.mipCount = 1;

    if (XR_FAILED(xrCreateSwapchain(g_xr.session, &info, &g_xr.overlaySwapchain)))
    {
        g_xr.overlaySwapchain = XR_NULL_HANDLE;
        BVR_WARN("Overlay swapchain could not be created; the settings panel will "
                 "not be shown. Everything else is unaffected.");
        return false;
    }

    uint32_t imageCount = 0;
    if (!check(xrEnumerateSwapchainImages(g_xr.overlaySwapchain, 0, &imageCount, nullptr),
               "xrEnumerateSwapchainImages(overlay count)"))
        return false;

    g_xr.overlayImages.assign(imageCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
    if (!check(xrEnumerateSwapchainImages(
                   g_xr.overlaySwapchain, imageCount, &imageCount,
                   reinterpret_cast<XrSwapchainImageBaseHeader*>(g_xr.overlayImages.data())),
               "xrEnumerateSwapchainImages(overlay)"))
        return false;

    BVR_INFO("Overlay swapchain: %ux%u, %u images.", w, h, imageCount);
    return true;
}

bool create_swapchains(ID3D11Device* device)
{
    g_xr.swapchainFormat = pick_swapchain_format();
    if (g_xr.swapchainFormat == 0)
        return false;

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        EyeSwapchain& sc = g_xr.swapchains[eye];
        sc.width  = g_xr.viewConfigs[eye].recommendedImageRectWidth;
        sc.height = g_xr.viewConfigs[eye].recommendedImageRectHeight;

        XrSwapchainCreateInfo info{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT |
                          XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
        info.format = g_xr.swapchainFormat;
        info.sampleCount = 1;
        info.width = sc.width;
        info.height = sc.height;
        info.faceCount = 1;
        info.arraySize = 1;
        info.mipCount = 1;

        if (!check(xrCreateSwapchain(g_xr.session, &info, &sc.handle), "xrCreateSwapchain"))
            return false;

        uint32_t imageCount = 0;
        if (!check(xrEnumerateSwapchainImages(sc.handle, 0, &imageCount, nullptr),
                   "xrEnumerateSwapchainImages(count)"))
            return false;

        sc.images.assign(imageCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        if (!check(xrEnumerateSwapchainImages(
                       sc.handle, imageCount, &imageCount,
                       reinterpret_cast<XrSwapchainImageBaseHeader*>(sc.images.data())),
                   "xrEnumerateSwapchainImages"))
            return false;

        sc.rtvs.assign(imageCount, nullptr);
        for (uint32_t i = 0; i < imageCount; ++i)
        {
            D3D11_RENDER_TARGET_VIEW_DESC rtvDesc = {};
            rtvDesc.Format = static_cast<DXGI_FORMAT>(g_xr.swapchainFormat);
            rtvDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;

            if (FAILED(device->CreateRenderTargetView(sc.images[i].texture, &rtvDesc, &sc.rtvs[i])))
            {
                BVR_ERR("CreateRenderTargetView failed for eye %u image %u.", eye, i);
                return false;
            }
        }

        BVR_INFO("Eye %u swapchain: %ux%u, %u images, format %lld.",
                 eye, sc.width, sc.height, imageCount,
                 static_cast<long long>(g_xr.swapchainFormat));
    }

    create_overlay_swapchain();
    return true;
}

void destroy_swapchains()
{
    bvr::overlay_shutdown();

    if (g_xr.overlaySwapchain != XR_NULL_HANDLE)
    {
        xrDestroySwapchain(g_xr.overlaySwapchain);
        g_xr.overlaySwapchain = XR_NULL_HANDLE;
    }
    g_xr.overlayImages.clear();
    g_xr.overlayPosePinned = false;

    /* flatReady goes first, for the reason the depth flag does: it is the only
       thing flat_screen_submit checks, and a destroyed swapchain behind a flag
       still reading true is an acquire on a dangling handle. */
    g_xr.uiReady = false;
    g_xr.uiAttempted = false;
    ui_key_release();

    if (g_xr.uiSwapchain != XR_NULL_HANDLE)
    {
        xrDestroySwapchain(g_xr.uiSwapchain);
        g_xr.uiSwapchain = XR_NULL_HANDLE;
    }
    g_xr.uiImages.clear();

    if (g_xr.uiSource != nullptr)
    {
        g_xr.uiSource->Release();
        g_xr.uiSource = nullptr;
    }

    if (g_xr.uiMirror != nullptr)
    {
        g_xr.uiMirror->Release();
        g_xr.uiMirror = nullptr;
    }

    if (g_xr.uiKeyed != nullptr)
    {
        g_xr.uiKeyed->Release();
        g_xr.uiKeyed = nullptr;
    }
    g_xr.uiFormat = 0;
    g_xr.uiWidth = 0;
    g_xr.uiHeight = 0;

    g_xr.flatReady = false;
    g_xr.flatAttempted = false;
    g_xr.flatSizeWarned = false;
    g_xr.flatPosePinned = false;
    g_xr.flatSettleFrames = 0;
    g_xr.flatEverPinned = false;

    if (g_xr.flatSwapchain != XR_NULL_HANDLE)
    {
        xrDestroySwapchain(g_xr.flatSwapchain);
        g_xr.flatSwapchain = XR_NULL_HANDLE;
    }
    g_xr.flatImages.clear();
    g_xr.flatFormat = 0;
    g_xr.flatWidth = 0;
    g_xr.flatHeight = 0;

    /* BEFORE the handles go, not after. submit_depth gates on this flag alone,
       and a destroyed swapchain behind a flag still reading true is an acquire
       on a dangling handle. Clearing g_depthAttempted with it lets the next
       session allocate afresh rather than inheriting this one's verdict. */
    g_depthLayerReady = false;
    g_depthAttempted = false;
    g_depthUpscaling = false;
    g_xr.depthFormat = 0;

    /* The resampler holds a view of a held depth texture that is about to be
       released below, so it goes first. Clearing its failure latch with it is
       deliberate: the next session gets to try again rather than inherit a
       verdict reached about a device that no longer exists. */
    depth_upscale_release();

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        EyeSwapchain& sc = g_xr.swapchains[eye];

        if (sc.depthHandle != XR_NULL_HANDLE)
        {
            xrDestroySwapchain(sc.depthHandle);
            sc.depthHandle = XR_NULL_HANDLE;
        }
        sc.depthImages.clear();

        for (ID3D11Texture2D** held : { &g_xr.afrDepthResult[eye], &g_xr.afrDepthStage[eye] })
        {
            if (*held != nullptr)
            {
                (*held)->Release();
                *held = nullptr;
            }
        }
        g_xr.afrDepthHas[eye] = false;
        g_xr.afrDepthStageHas[eye] = false;

        for (ID3D11RenderTargetView* rtv : sc.rtvs)
        {
            if (rtv != nullptr)
                rtv->Release();
        }
        sc.rtvs.clear();
        sc.images.clear();

        if (sc.handle != XR_NULL_HANDLE)
        {
            xrDestroySwapchain(sc.handle);
            sc.handle = XR_NULL_HANDLE;
        }
    }
}

// ---------------------------------------------------------------------------
// Session
// ---------------------------------------------------------------------------

bool luid_matches(const LUID& a, const LUID& b)
{
    return a.LowPart == b.LowPart && a.HighPart == b.HighPart;
}

bool create_session(ID3D11Device* device)
{
    /* Spec requires this call before xrCreateSession, and it is also how we learn
       which adapter the runtime insists on. */
    XrGraphicsRequirementsD3D11KHR req{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
    if (g_getD3D11Requirements == nullptr ||
        !check(g_getD3D11Requirements(g_xr.instance, g_xr.systemId, &req),
               "xrGetD3D11GraphicsRequirementsKHR"))
        return false;

    const LUID engineLuid = engine_adapter_luid();
    if (!luid_matches(req.adapterLuid, engineLuid))
    {
        /* On a machine with both a discrete GPU and an iGPU this is a real
           possibility, and it is the single most confusing failure mode in the
           whole project: the monitor looks perfect and the headset stays black.
           Phase 2 refuses rather than pretending; the shared-NT-handle bridge is
           Phase 4 work if it ever actually happens here. */
        BVR_ERR("Adapter mismatch. Engine is on LUID %08lX:%08lX, OpenXR runtime demands "
                "%08lX:%08lX. Force Bannerlord onto the discrete GPU "
                "(engine_config.txt graphics_adapter, or the Windows graphics preference).",
                static_cast<unsigned long>(engineLuid.HighPart),
                static_cast<unsigned long>(engineLuid.LowPart),
                static_cast<unsigned long>(req.adapterLuid.HighPart),
                static_cast<unsigned long>(req.adapterLuid.LowPart));
        return false;
    }
    BVR_INFO("Adapter LUIDs match; binding OpenXR to the engine's own device (zero-copy path).");

    XrGraphicsBindingD3D11KHR binding{ XR_TYPE_GRAPHICS_BINDING_D3D11_KHR };
    binding.device = device;

    XrSessionCreateInfo info{ XR_TYPE_SESSION_CREATE_INFO };
    info.next = &binding;
    info.systemId = g_xr.systemId;

    if (!check(xrCreateSession(g_xr.instance, &info, &g_xr.session), "xrCreateSession"))
        return false;

    /* STAGE is roomscale and gives a floor-level origin; LOCAL is the seated
       fallback every runtime supports. */
    XrReferenceSpaceCreateInfo spaceInfo{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
    spaceInfo.poseInReferenceSpace.orientation.w = 1.0f;
    spaceInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;

    if (XR_FAILED(xrCreateReferenceSpace(g_xr.session, &spaceInfo, &g_xr.appSpace)))
    {
        BVR_WARN("STAGE reference space unavailable; falling back to LOCAL (seated).");
        spaceInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
        if (!check(xrCreateReferenceSpace(g_xr.session, &spaceInfo, &g_xr.appSpace),
                   "xrCreateReferenceSpace(LOCAL)"))
            return false;
    }

    if (!create_swapchains(device))
        return false;

    BVR_INFO("OpenXR session created.");

    /* Actions must be attached before the first sync and cannot be attached
       twice, so this is the one place it can happen. A failure here leaves
       input inactive and everything else exactly as it was. */
    input_create(g_xr.instance, g_xr.session);
    return true;
}

void destroy_session()
{
    destroy_swapchains();

    /* Action spaces belong to the session, so they go before it does. */
    input_destroy();

    if (g_xr.appSpace != XR_NULL_HANDLE)
    {
        xrDestroySpace(g_xr.appSpace);
        g_xr.appSpace = XR_NULL_HANDLE;
    }
    if (g_xr.session != XR_NULL_HANDLE)
    {
        xrDestroySession(g_xr.session);
        g_xr.session = XR_NULL_HANDLE;
    }
    g_xr.sessionRunning = false;
    set_xr_pacing(false);
    g_xr.sessionState = XR_SESSION_STATE_UNKNOWN;

    /* The poses in the ring described a session that no longer exists. */
    g_xr.posePublished.store(false, std::memory_order_release);

    /* New swapchains next time, so the copy compatibility must be re-tested. */
    g_xr.copyChecked = false;
    g_xr.copyUsable = false;
}

// ---------------------------------------------------------------------------
// Event pump
// ---------------------------------------------------------------------------

void handle_state_change(const XrEventDataSessionStateChanged& ev)
{
    g_xr.sessionState = ev.state;
    BVR_INFO("Session state -> %d.", static_cast<int>(ev.state));

    switch (ev.state)
    {
    case XR_SESSION_STATE_READY:
    {
        XrSessionBeginInfo begin{ XR_TYPE_SESSION_BEGIN_INFO };
        begin.primaryViewConfigurationType = kViewConfig;
        if (check(xrBeginSession(g_xr.session, &begin), "xrBeginSession"))
        {
            g_xr.sessionRunning = true;
            set_xr_pacing(true);
            BVR_INFO("Session running.");
        }
        break;
    }
    case XR_SESSION_STATE_STOPPING:
        g_xr.sessionRunning = false;
        set_xr_pacing(false);
        check(xrEndSession(g_xr.session), "xrEndSession");
        break;

    case XR_SESSION_STATE_EXITING:
    case XR_SESSION_STATE_LOSS_PENDING:
        BVR_WARN("Session exiting or lost; tearing down.");
        destroy_session();
        break;

    default:
        break;
    }
}

void poll_events()
{
    for (;;)
    {
        XrEventDataBuffer ev{ XR_TYPE_EVENT_DATA_BUFFER };
        const XrResult r = xrPollEvent(g_xr.instance, &ev);
        if (r == XR_EVENT_UNAVAILABLE)
            return;
        if (XR_FAILED(r))
        {
            BVR_ERR("xrPollEvent failed: %s", xr_result_name(r));
            return;
        }

        switch (ev.type)
        {
        case XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED:
            handle_state_change(*reinterpret_cast<XrEventDataSessionStateChanged*>(&ev));
            break;

        case XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING:
            BVR_WARN("Runtime signalled instance loss.");
            destroy_session();
            break;

        case XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING:
            BVR_INFO("Reference space changed (recentre).");
            break;

        default:
            break;
        }
    }
}

// ---------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------

int afr_pose_lag()
{
    static int cached = -1;
    if (cached < 0)
    {
        const int v = static_cast<int>(config_float("afr_pose_lag", 1.0f));
        cached = (v < 0) ? 0 : (v > 2 ? 2 : v);
    }
    return cached;
}

/* The generation the frame now in our hands was drawn from. Falls back towards
   the fresh locate whenever the requested history is not filled yet, so the
   first frames after a session start are merely imprecise rather than wrong. */
const XrView* content_views(const XrView fresh[kEyeCount])
{
    const int lag = afr_pose_lag();
    if (lag >= 2 && g_xr.viewsHistValid[1])
        return g_xr.viewsHist[1];
    if (lag >= 1 && g_xr.viewsHistValid[0])
        return g_xr.viewsHist[0];
    return fresh;
}

void roll_view_history(const XrView views[kEyeCount])
{
    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
        g_xr.viewsHist[1][eye] = g_xr.viewsHist[0][eye];
    g_xr.viewsHistValid[1] = g_xr.viewsHistValid[0];

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
        g_xr.viewsHist[0][eye] = views[eye];
    g_xr.viewsHistValid[0] = true;
}

/* --------------------------------------------------------------------------
 * TURNING: correcting the held eye for the GAME camera's rotation.
 *
 * The compositor reprojects every submitted view from the pose we tag it with
 * to the pose the headset is really at by display time. That covers the head
 * turning. It cannot cover the GAME camera turning, because the game camera is
 * not in stage space and the runtime has never heard of it.
 *
 * Under AFR one eye is always a frame old. Stand still and that costs nothing -
 * the held image is a picture of the same world from the same place, and the
 * pose it carries is exactly right. Turn, though, and the held eye was drawn
 * from a world orientation the fresh eye no longer shares. At 90 fps a 180
 * deg/s turn puts two degrees of pure horizontal disparity between the eyes,
 * which is an order of magnitude past what fuses comfortably. It appears only
 * while turning and disappears the instant you stop, which is exactly how the
 * artefact is described.
 *
 * Note what this is NOT. It is not the pose-lag bug: that was tagging an image
 * with a locate it was never drawn from, and it is fixed. Both of those are
 * "the tagged pose is wrong", but afr_pose_lag fixes it for HEAD motion and
 * this fixes it for the mouse, and neither one covers the other's case.
 *
 * The fix is to say so in the pose. The compositor is already willing to
 * rotate a stale image into place; it just needs to be told the whole rotation
 * rather than the half it can see. If the image was drawn with the anchor
 * yawed by dTheta relative to now, tag it with a head pose yawed to match and
 * the compositor's own reprojection cancels it:
 *
 *     P = B_now^-1 . B_old . H_old
 *
 * Bannerlord's yaw runs opposite to a rotation about OpenXR's +Y - its basis
 * is X = x, Y = -z, Z = y, and yaw is measured from +Y toward +X, so an XR
 * rotation of phi about +Y reads as a Bannerlord yaw of -phi - which makes
 * B_now^-1 . B_old a rotation of +dTheta about +Y. If that reasoning is off by
 * a sign the artefact gets WORSE while turning rather than merely staying put,
 * which is easy to see and one config line to flip.
 *
 * Position is deliberately left alone. Correcting it needs the depth buffer to
 * reproject against, and we submit no depth layer; a positional offset the
 * runtime cannot parallax against would move the whole image rigidly and trade
 * one artefact for a worse one. Rotation is what turning is made of.
 * ------------------------------------------------------------------------ */

/* 0 disables the correction, 1 applies it, -1 applies it inverted. Not a bool,
   because the sign is the thing most likely to be wrong and the useful A/B is
   three-way: off, one way, the other. */
float turn_reprojection()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("afr_turn_reprojection", 1.0f);
        if (cached > 1.0f)  cached = 1.0f;
        if (cached < -1.0f) cached = -1.0f;
    }
    return cached;
}

/* Hamilton product, a then b applied right to left: (a * b) rotates by b and
   then by a. Written out because this is the only place in the file that
   composes two rotations. */
XrQuaternionf quat_mul(const XrQuaternionf& a, const XrQuaternionf& b)
{
    return XrQuaternionf{
        a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
        a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
    };
}

float wrap_pi(float a)
{
    const float kPi = 3.14159265358979f;
    const float kTau = 6.28318530717959f;

    /* A turn across the +/-pi seam is a small movement, not a full revolution.
       Bounded rather than a while loop: the input is a difference of two
       recent yaws, so one wrap is all it can ever need, and a NaN would spin a
       while loop forever inside the Present hook. */
    if (a >  kPi) a -= kTau;
    if (a < -kPi) a += kTau;
    return a;
}

/* The pose the image is tagged with, given the pose it CONTAINS.
 *
   Split from turn_corrected_pose so the image latch can hand in the orientation
   it has just rotated the pixels to. The body-yaw correction below is about the
   GAME camera and is orthogonal to the head: both have to be applied, and
   applying this one to a stale head orientation would undo half the latch. */
XrPosef turn_corrected_from(uint32_t eye, XrPosef pose)
{
    const float sign = turn_reprojection();
    if (sign == 0.0f || !g_xr.bodyYawValid.load(std::memory_order_acquire))
        return pose;

    /* The LIVE anchor when one has been published, the rendered one otherwise.
       See XrState::targetYaw: with the pair camera latched these differ, and
       comparing the rendered anchor against itself is what pinned this at zero. */
    const float now = g_xr.targetValid.load(std::memory_order_acquire)
                          ? g_xr.targetYaw.load(std::memory_order_acquire)
                          : g_xr.bodyYaw.load(std::memory_order_acquire);
    const float dTheta = wrap_pi(now - g_xr.afrBodyYaw[eye]);

    /* Not turning. Skip the work, and - more to the point - leave a pose that
       was already exactly right exactly as it was, rather than running it
       through a quaternion multiply every frame to accumulate float drift for
       no reason. This is the common case: the fresh eye was captured against
       this very yaw, so its delta is a true zero. */
    if (!(dTheta > 1e-5f || dTheta < -1e-5f))
        return pose;

    const float deg = (dTheta < 0.0f ? -dTheta : dTheta) * 57.2957795f;
    if (deg > g_xr.turnPeakDeg)
        g_xr.turnPeakDeg = deg;

    const float half = dTheta * 0.5f * sign;
    const XrQuaternionf yaw{ 0.0f, sinf(half), 0.0f, cosf(half) };

    pose.orientation = quat_mul(yaw, pose.orientation);
    return pose;
}

XrPosef turn_corrected_pose(uint32_t eye)
{
    return turn_corrected_from(eye, g_xr.afrPose[eye]);
}

/* 0 off, 1 applied, -1 inverted - the same three-way as the turn knob, and for
   the same reason: a sign is the easiest thing here to get backwards. */
float move_reprojection()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("afr_move_reprojection", 1.0f);
        if (cached > 1.0f)  cached = 1.0f;
        if (cached < -1.0f) cached = -1.0f;
    }
    return cached;
}

/* THE OTHER HALF OF TURNING, WHICH IS NOT TURNING AT ALL.
 *
   turn_corrected_pose fixes the anchor's ROTATION between the two eyes of a
   pair, and its header explains why it stopped there: correcting position needs
   a depth buffer to parallax against, and at the time no depth layer was
   submitted.
 *
   There is one now. And position turned out to be the larger error by far,
   because the anchor is the GAME camera and the game camera rides a horse. At
   ten metres a second it travels 110 mm between one eye and the next - larger
   than the 65 mm IPD it is supposed to be separated by - so the pair carries a
   false disparity bigger than the real one. That is not a subtle depth error,
   it is the stereo being wrong outright, and it appears only while moving and
   clears the moment you stop, which is exactly how it was reported.
 *
   Rotation could be corrected without depth because a rotation about the eye is
   a homography - depth-independent. Translation is not: how far a pixel should
   move depends on how far away it is, which is precisely what the compositor
   needs the depth layer for. Submitting the displacement in the pose lets the
   runtime do that work with the depth it already has. */
XrPosef move_corrected_pose(uint32_t eye, XrPosef pose)
{
    const float sign = move_reprojection();
    if (sign == 0.0f || !g_xr.bodyPosValid.load(std::memory_order_acquire))
        return pose;

    const bool useTarget = g_xr.targetValid.load(std::memory_order_acquire);
    const float nx = useTarget ? g_xr.targetPosX.load(std::memory_order_acquire)
                               : g_xr.bodyPosX.load(std::memory_order_acquire);
    const float ny = useTarget ? g_xr.targetPosY.load(std::memory_order_acquire)
                               : g_xr.bodyPosY.load(std::memory_order_acquire);
    const float nz = useTarget ? g_xr.targetPosZ.load(std::memory_order_acquire)
                               : g_xr.bodyPosZ.load(std::memory_order_acquire);

    const float dx = (nx - g_xr.afrBodyPos[eye][0]) * sign;
    const float dy = (ny - g_xr.afrBodyPos[eye][1]) * sign;
    const float dz = (nz - g_xr.afrBodyPos[eye][2]) * sign;

    const float d2 = dx * dx + dy * dy + dz * dz;
    if (!(d2 > 1e-10f))
        return pose;   /* standing still: leave an already-correct pose alone */

    const float mm = sqrtf(d2) * 1000.0f;
    if (mm > g_xr.movePeakMm)
        g_xr.movePeakMm = mm;

    /* The held eye was drawn from an anchor that has since moved. Telling the
       compositor it was drawn from HERE, offset by how far the world has slid
       under it, makes its own reprojection carry the image the rest of the way.
       Same shape as the turn correction, one dimension up. */
    pose.position.x += dx;
    pose.position.y += dy;
    pose.position.z += dz;
    return pose;
}

/* HOW FAR AHEAD THE POSE HANDED TO THE ENGINE IS PREDICTED, IN FRAMES.
 *
 * 1, because that is the depth of the pipeline this pose is about to go
 * through. 0 restores the behaviour this had before. Fractions are allowed -
 * the true figure is the engine's render latency, which is a frame plus a bit
 * and not a round number. Above 2 the runtime is being asked to extrapolate
 * further than it can, and an overshoot is worse than the lag it removes. */
/* How hard to low-pass the lead's own contribution. See the note in
   locate_views_ahead. 0 disables the filter; higher follows the raw prediction
   more closely and filters less. 0.25 keeps sustained motion essentially intact
   while averaging a speech tremor away over a few frames. */
float pose_lead_smooth()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        /* Defaults OFF. It costs a second xrLocateViews every frame, and TEST 78
           established that CPU on this path comes back as latency. Opt in. */
        cached = config_float("pose_lead_smooth", 0.0f);
        if (cached < 0.0f) cached = 0.0f;
        if (cached > 1.0f) cached = 1.0f;
    }
    return cached;
}

/* The measured Present-to-Present interval, in the same nanosecond units as
   XrDuration. An exponential average, so a single long frame nudges the lead
   rather than jerking it - a prediction that jitters is worse than one that is
   slightly short. */
std::atomic<int64_t> g_frameTimeNs{ 0 };

bool pose_lead_adaptive()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("pose_lead_adaptive", true); }
    return cached;
}

float pose_lead_frames()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("pose_lead_frames", 1.0f);
        if (cached < 0.0f) cached = 0.0f;
        if (cached > 2.0f) cached = 2.0f;
    }
    return cached;
}

/* THE POSE THE ENGINE RENDERS WITH IS FOR A MOMENT THAT HAS ALREADY PASSED.
 *
 * The views located in this Present serve two consumers, and only one of them
 * is being served correctly.
 *
 *   SUBMISSION wants the pose for THIS frame's display time, because that is
 *   when the images now being handed over will be shown. It gets it.
 *
 *   THE ENGINE wants the pose for the display time of the frame it is ABOUT TO
 *   RENDER, which is the next one. It was getting this one, so everything the
 *   engine drew was aimed a frame behind. That is not a subtlety - it is the
 *   13.9 ms the pose-age instrument reports at 72 Hz, every second, dead
 *   steady. One frame, which is what a pipeline offset looks like when nothing
 *   else is wrong.
 *
 * This is the second of the two frame-shaped latencies, and they are different
 * things. frame_latency in d3d_hooks is how long the engine's drawing sits in a
 * queue after it is issued; this is the pose it was issued WITH being sampled
 * for the wrong moment. Fixing either alone leaves the other.
 *
 * The compositor carries the difference by warping, which is why it reads as
 * floaty rather than as judder: right shape, wrong place, moved there by a
 * reprojection instead of being drawn there.
 *
 * So the engine's copy is located separately, at the time it will actually be
 * displayed. Runtimes predict comfortably over one frame - it is the same
 * extrapolation they already perform for their own reprojection, and asking for
 * it directly is cheaper than being corrected for it afterwards. The submitted
 * pose still says where the image was drawn from, so the compositor keeps doing
 * its job; there is simply almost nothing left for it to do.
 *
 * Returns false if the second locate fails, and the caller then publishes what
 * it already has - a frame late, as before, rather than nothing at all. */
bool locate_views_ahead(XrTime displayTime, XrDuration period,
                        XrView out[kEyeCount], XrTime* outTime)
{
    const float lead = pose_lead_frames();
    if (lead <= 0.0f || period <= 0)
        return false;

    /* THE LEAD HAS TO BE IN REAL FRAMES, NOT DISPLAY PERIODS.
     *
     * predictedDisplayPeriod is the HEADSET's period - 11.1 ms at 90 Hz - and it
     * does not change when the game slows down. But the head-to-photon path runs
     * at the GAME's frame rate (TEST 78), so at 45 fps the pose is a full 22 ms
     * stale while the prediction still covers 11.1. The correction is then half
     * of what is needed, and it gets worse exactly as the frame time gets worse.
     *
     * That is the reported behaviour word for word: "when fps and frametime get
     * worse the head resistance and delay get stronger". It is not the frame rate
     * being felt directly - it is the prediction silently under-covering it.
     *
     * So the lead is measured against the actual Present-to-Present interval and
     * the display period is only a floor. At 90 fps the two agree and nothing
     * changes; below it the prediction grows to match the latency it is there to
     * hide. Clamped to four display periods so a hitch cannot fling the pose into
     * next week.
     *
     * pose_lead_adaptive = 0 restores the old fixed-period behaviour. */
    XrDuration step = period;
    if (pose_lead_adaptive())
    {
        const int64_t frameNs = g_frameTimeNs.load(std::memory_order_relaxed);
        if (frameNs > step)
            step = (frameNs > period * 4) ? period * 4 : frameNs;
    }

    const XrTime ahead = displayTime + static_cast<XrTime>(step * lead);

    XrViewLocateInfo info{ XR_TYPE_VIEW_LOCATE_INFO };
    info.viewConfigurationType = kViewConfig;
    info.displayTime = ahead;
    info.space = g_xr.appSpace;

    XrViewState state{ XR_TYPE_VIEW_STATE };
    XrView located[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    uint32_t count = 0;

    const XrResult r = xrLocateViews(g_xr.session, &info, &state,
                                     kEyeCount, &count, located);

    if (!XR_SUCCEEDED(r) || count != kEyeCount ||
        (state.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) == 0 ||
        (state.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) == 0)
        return false;

    /* THE LEAD AMPLIFIES JITTER, AND TALKING IS JITTER.
     *
     * Extrapolation multiplies whatever it extrapolates. Real head motion is
     * low frequency and comes out of it correctly; the millimetre tremor of
     * speech, a jaw moving, a floor vibration, is high frequency, and predicting
     * it forward a frame and a half makes it BIGGER. That is the reported
     * "very sensitive to any noise or vibration from talking" - the lead working
     * exactly as designed, on a signal it should be ignoring.
     *
     * The two are separable because they differ in frequency, not in size. So:
     * locate the unled pose as well, take the difference - which IS the whole of
     * what the lead contributes - and low-pass THAT. Sustained motion keeps a
     * consistent difference and the filter passes it, so the lag reduction
     * survives intact. Tremor reverses sign every frame or two, the filter
     * averages it toward zero, and the lead stops amplifying it.
     *
     * Filtering the difference rather than the pose is the point: a filter on
     * the pose itself would add latency, which is the thing being removed.
     *
     * pose_lead_smooth = 0 disables it and restores raw extrapolation. */
    const float alpha = pose_lead_smooth();
    if (alpha <= 0.0f || alpha >= 1.0f)
    {
        out[0] = located[0];
        out[1] = located[1];
        *outTime = ahead;
        return true;
    }

    XrViewLocateInfo nowInfo{ XR_TYPE_VIEW_LOCATE_INFO };
    nowInfo.viewConfigurationType = kViewConfig;
    nowInfo.displayTime = displayTime;
    nowInfo.space = g_xr.appSpace;

    XrViewState nowState{ XR_TYPE_VIEW_STATE };
    XrView nowV[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    uint32_t nowCount = 0;

    const XrResult nr = xrLocateViews(g_xr.session, &nowInfo, &nowState,
                                      kEyeCount, &nowCount, nowV);

    if (!XR_SUCCEEDED(nr) || nowCount != kEyeCount ||
        (nowState.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) == 0 ||
        (nowState.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) == 0)
    {
        /* No unled pose to difference against; the raw lead is still better than
           nothing and this is the same result the filter would converge to. */
        out[0] = located[0];
        out[1] = located[1];
        *outTime = ahead;
        return true;
    }

    static XrVector3f    s_dPos[kEyeCount] = {};
    static XrQuaternionf s_dRot[kEyeCount] = { {0,0,0,1}, {0,0,0,1} };
    static bool          s_primed = false;

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        const XrVector3f dp = {
            located[eye].pose.position.x - nowV[eye].pose.position.x,
            located[eye].pose.position.y - nowV[eye].pose.position.y,
            located[eye].pose.position.z - nowV[eye].pose.position.z
        };

        /* The rotation the lead adds: qAhead * inverse(qNow). Unit quaternions,
           so the conjugate is the inverse. */
        const XrQuaternionf& qn = nowV[eye].pose.orientation;
        const XrQuaternionf  qnInv = { -qn.x, -qn.y, -qn.z, qn.w };
        const XrQuaternionf  dq = quat_mul(located[eye].pose.orientation, qnInv);

        if (!s_primed)
        {
            s_dPos[eye] = dp;
            s_dRot[eye] = dq;
        }
        else
        {
            s_dPos[eye].x += alpha * (dp.x - s_dPos[eye].x);
            s_dPos[eye].y += alpha * (dp.y - s_dPos[eye].y);
            s_dPos[eye].z += alpha * (dp.z - s_dPos[eye].z);

            /* nlerp toward the new delta. These are one frame of head rotation
               apart, so the small-angle case where nlerp and slerp agree. */
            XrQuaternionf t = s_dRot[eye];
            if (t.x*dq.x + t.y*dq.y + t.z*dq.z + t.w*dq.w < 0.0f)
            { t.x = -t.x; t.y = -t.y; t.z = -t.z; t.w = -t.w; }

            XrQuaternionf n = {
                t.x + alpha * (dq.x - t.x),
                t.y + alpha * (dq.y - t.y),
                t.z + alpha * (dq.z - t.z),
                t.w + alpha * (dq.w - t.w)
            };
            const float len = sqrtf(n.x*n.x + n.y*n.y + n.z*n.z + n.w*n.w);
            if (len > 1e-6f)
            { n.x /= len; n.y /= len; n.z /= len; n.w /= len; }
            else
                n = XrQuaternionf{ 0.0f, 0.0f, 0.0f, 1.0f };
            s_dRot[eye] = n;
        }

        out[eye] = nowV[eye];
        out[eye].pose.position.x = nowV[eye].pose.position.x + s_dPos[eye].x;
        out[eye].pose.position.y = nowV[eye].pose.position.y + s_dPos[eye].y;
        out[eye].pose.position.z = nowV[eye].pose.position.z + s_dPos[eye].z;
        out[eye].pose.orientation = quat_mul(s_dRot[eye], nowV[eye].pose.orientation);
        out[eye].fov = located[eye].fov;
    }

    s_primed = true;
    *outTime = ahead;
    return true;
}

/* TREMOR THAT IS IN THE TRACKED POSE ITSELF, not just in the lead.
 *
 * pose_lead_smooth stops the LEAD from amplifying a tremor. It cannot remove one
 * that is already in the pose the runtime reports - speech and jaw movement
 * genuinely shake the headset, the tracker genuinely sees it, and every bit of
 * that reaches the eye whatever the lead does. That is the half left over.
 *
 * A plain low-pass would remove it and add latency to real head movement, which
 * is the thing this whole session has been trying to remove. So the strength has
 * to depend on SPEED: hold hard when the head is essentially still, and let go
 * completely as soon as it is actually moving. Tremor is small and slow-moving in
 * net terms; a head turn is not, and it passes through untouched.
 *
 *   alpha = minAlpha + (1 - minAlpha) * clamp(speed / releaseSpeed)
 *
 * head_filter    = minAlpha. How much of a new pose is taken when perfectly
 *                  still. 1.0 disables the filter; lower holds harder.
 * head_filter_ms = the speed, in mm per second, at which it fully releases.
 *
 * The same alpha drives both eyes, so the separation between them is never
 * filtered - only where the pair as a whole sits. */
float head_filter_alpha()
{
    static float cached = 1.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("head_filter", 1.0f);
        if (cached < 0.02f) cached = 0.02f;
        if (cached > 1.0f)  cached = 1.0f;
    }
    return cached;
}

float head_filter_release()
{
    static float cached = 250.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("head_filter_ms", 250.0f);
        if (cached < 10.0f) cached = 10.0f;
    }
    return cached / 1000.0f;   /* to metres per second */
}

void filter_head_tremor(XrView views[kEyeCount], XrTime displayTime)
{
    const float minAlpha = head_filter_alpha();
    if (minAlpha >= 1.0f)
        return;                      /* head_filter = 1 is off */

    static XrView  s_prev[kEyeCount] = {};
    static XrTime  s_prevTime = 0;
    static bool    s_primed = false;

    if (!s_primed)
    {
        s_prev[0] = views[0];
        s_prev[1] = views[1];
        s_prevTime = displayTime;
        s_primed = true;
        return;
    }

    double dt = static_cast<double>(displayTime - s_prevTime) * 1e-9;
    if (!(dt > 1e-5) || dt > 0.5)     /* a stall or a hitch: restart clean */
    {
        s_prev[0] = views[0];
        s_prev[1] = views[1];
        s_prevTime = displayTime;
        return;
    }

    /* Speed from eye 0, applied to both, so the pair moves together. */
    const float dx = views[0].pose.position.x - s_prev[0].pose.position.x;
    const float dy = views[0].pose.position.y - s_prev[0].pose.position.y;
    const float dz = views[0].pose.position.z - s_prev[0].pose.position.z;
    const float speed = sqrtf(dx*dx + dy*dy + dz*dz) / static_cast<float>(dt);

    float t = speed / head_filter_release();
    if (t > 1.0f) t = 1.0f;
    if (t < 0.0f) t = 0.0f;

    const float alpha = minAlpha + (1.0f - minAlpha) * t;

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        XrVector3f p = s_prev[eye].pose.position;
        p.x += alpha * (views[eye].pose.position.x - p.x);
        p.y += alpha * (views[eye].pose.position.y - p.y);
        p.z += alpha * (views[eye].pose.position.z - p.z);

        XrQuaternionf q = s_prev[eye].pose.orientation;
        const XrQuaternionf& n = views[eye].pose.orientation;
        if (q.x*n.x + q.y*n.y + q.z*n.z + q.w*n.w < 0.0f)
        { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }

        XrQuaternionf o = {
            q.x + alpha * (n.x - q.x),
            q.y + alpha * (n.y - q.y),
            q.z + alpha * (n.z - q.z),
            q.w + alpha * (n.w - q.w)
        };
        const float len = sqrtf(o.x*o.x + o.y*o.y + o.z*o.z + o.w*o.w);
        if (len > 1e-6f) { o.x /= len; o.y /= len; o.z /= len; o.w /= len; }
        else o = n;

        views[eye].pose.position = p;
        views[eye].pose.orientation = o;

        s_prev[eye] = views[eye];
    }

    s_prevTime = displayTime;
}

void publish_views(const XrView inViews[kEyeCount], XrTime displayTime)
{
    const int next = 1 - g_xr.poseIndex.load(std::memory_order_relaxed);

    /* The one funnel every consumer reads from - the engine camera and, through
       bvr_set_used_views, the pose the frame is submitted with. Filtering here
       keeps those two agreeing, which is what stops a filter from becoming a
       render-versus-submit mismatch of its own. */
    XrView views[kEyeCount] = { inViews[0], inViews[1] };
    filter_head_tremor(views, displayTime);

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        BvrEyeView& out = g_xr.poseBuffers[next][eye];
        out.orientation.x = views[eye].pose.orientation.x;
        out.orientation.y = views[eye].pose.orientation.y;
        out.orientation.z = views[eye].pose.orientation.z;
        out.orientation.w = views[eye].pose.orientation.w;
        out.position.x = views[eye].pose.position.x;
        out.position.y = views[eye].pose.position.y;
        out.position.z = views[eye].pose.position.z;
        out.fov.angleLeft  = views[eye].fov.angleLeft;
        out.fov.angleRight = views[eye].fov.angleRight;
        out.fov.angleUp    = views[eye].fov.angleUp;
        out.fov.angleDown  = views[eye].fov.angleDown;
    }
    g_xr.poseTimes[next] = static_cast<int64_t>(displayTime);

    /* Release only after the buffer is fully written. */
    g_xr.poseIndex.store(next, std::memory_order_release);
    g_xr.posePublished.store(true, std::memory_order_release);
}

/* Publishes the pose the ENGINE should render with: predicted forward by
   pose_lead_frames when the runtime can do it, and this frame's otherwise. */
void publish_views_for_engine(const XrView views[kEyeCount], XrTime displayTime,
                              XrDuration period)
{
    XrView ahead[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    XrTime aheadTime = 0;

    /* Measure the real frame interval here, where a frame is unambiguously
       beginning, and feed the adaptive lead above. */
    {
        static int64_t s_lastQpc = 0;
        LARGE_INTEGER f, n;
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&n);
        if (s_lastQpc != 0 && f.QuadPart > 0)
        {
            const int64_t ns = (n.QuadPart - s_lastQpc) * 1000000000LL / f.QuadPart;
            if (ns > 0 && ns < 200000000LL)   /* ignore load hitches over 200 ms */
            {
                const int64_t prev = g_frameTimeNs.load(std::memory_order_relaxed);
                g_frameTimeNs.store(prev == 0 ? ns : (prev * 7 + ns) / 8,
                                    std::memory_order_relaxed);
            }
        }
        s_lastQpc = n.QuadPart;
    }

    if (locate_views_ahead(displayTime, period, ahead, &aheadTime))
        publish_views(ahead, aheadTime);
    else
        publish_views(views, displayTime);
}

/* Scales the runtime's recommended eye size down, IN PLACE, before anything
   reads it.
 *
 * Doing it here rather than at each call site is deliberate: the swapchain, the
 * size reported to C# for its render targets, and therefore the CopyResource
 * source and destination all derive from these same two fields. Scaling in one
 * place makes them impossible to disagree, and a mismatch would not be a
 * stretched image - CopyResource would refuse outright and the headset would go
 * black.
 *
 * 2764x2940 per eye is 8.1 MP; two of those plus the game's own view is ~18 MP a
 * frame with shadows and postfx, which no GPU delivers at 90 Hz in a Bannerlord
 * battle. Override with BVR_RENDER_SCALE (0.25 to 1.0).
 */
void apply_render_scale()
{
    float scale = config_float("render_scale", 0.6f);
    if (scale < 0.25f || scale > 1.0f)
    {
        BVR_WARN("render_scale %.2f out of range (0.25-1.0); using 0.60.", scale);
        scale = 0.6f;
    }

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        XrViewConfigurationView& view = g_xr.viewConfigs[eye];

        /* Rounded to a multiple of 4. Odd render target dimensions upset block
           compression and some postfx passes. */
        uint32_t w = static_cast<uint32_t>(view.recommendedImageRectWidth * scale) & ~3u;
        uint32_t h = static_cast<uint32_t>(view.recommendedImageRectHeight * scale) & ~3u;

        if (w < 256) w = 256;
        if (h < 256) h = 256;

        view.recommendedImageRectWidth = w;
        view.recommendedImageRectHeight = h;
    }

    BVR_INFO("Render scale %.2f -> %ux%u per eye (%.1f MP total for both eyes).",
             scale,
             g_xr.viewConfigs[0].recommendedImageRectWidth,
             g_xr.viewConfigs[0].recommendedImageRectHeight,
             2.0 * g_xr.viewConfigs[0].recommendedImageRectWidth *
                   g_xr.viewConfigs[0].recommendedImageRectHeight / 1.0e6);
}

/* BVR_COPY=0 keeps the SceneViews rendering but stops us touching the engine's
   context at all, so the headset falls back to the flat colours. That single
   switch separates "the eye copy is unsafe" from "two extra scene renders are
   unsafe" - two very different bugs that look identical from the outside. */
bool copy_enabled()
{
    static int cached = -1;
    if (cached < 0)
    {
        cached = config_bool("copy", true) ? 1 : 0;
        BVR_INFO("Eye copy is %s (config: copy).", cached ? "enabled" : "DISABLED");
    }
    return cached != 0;
}

/* symmetric_fov = 1 makes the MANAGED side build a symmetric frustum that
   CONTAINS the runtime's asymmetric one, to test whether rgl culls against a
   cone derived from the camera rather than against the view volume it is handed.
   Both halves of the mod read the same config file, so this side can know it is
   happening without a new ABI call.
 *
 * It matters here because the compositor undistorts using the fov we SUBMIT, not
 * the one we rendered with. Submitting the runtime's asymmetric fov for an image
 * that was rendered symmetrically makes the two disagree - and disagree by
 * mirror-opposite amounts per eye, because the runtime's asymmetry is itself
 * mirrored. With VirtualDesktopXR's L-54/R+40 the submitted frustum's centre
 * sits 15 degrees off one way in the left eye and 15 the other way in the right:
 * 30 degrees of opposed angular error between the eyes. No stereo fusion
 * survives that, and it reads as "the image is distorted and the eyes look
 * nothing like each other" - which is a submission bug, not a render bug. */
/* WHICH FRUSTUM WAS RENDERED, AND THEREFORE WHICH ONE TO SUBMIT.
 *
 * symmetric_fov below zero (the default) follows the render mode; zero forces
 * the runtime's true asymmetric frustum; above zero forces the containing
 * symmetric one.
 *
 * Following the mode, because the right answer differs by mode and the managed
 * side decides it the same way:
 *
 *   AFR and native   Every eye is a real render, so each may have its own true
 *                    frustum. On this headset that is 91.2 x 93.1 degrees
 *                    instead of a containing 105.2 x 104.2 - a third of the
 *                    pixels no longer drawn and then cropped away.
 *
 *   AFW and hybrid   The second eye is BUILT from the first, so it can only be
 *                    shown what the first eye saw. The runtime's canted fovs are
 *                    mirrored, which leaves each eye around 709 pixels of outer
 *                    field the other has never seen - a quarter of the width
 *                    with no source at all. Both eyes must therefore render one
 *                    shared symmetric frustum, which is the rectified stereo
 *                    case the warp is built on.
 *
 * g_xr.afwEnabled is exactly "AFW or the hybrid is running", set from the same
 * managed call that decides the mode, so the two halves cannot disagree by more
 * than the frame in which the mode changed.
 *
 * atof, not a word: an unparseable value reads as 0, and 0 here is not "no
 * opinion" but "force asymmetric". The sentinel has to be a number. */
bool symmetric_fov_enabled()
{
    static int forced = -2;   /* -2 unread, -1 follow the mode, 0 off, 1 on */
    if (forced == -2)
    {
        const float choice = config_float("symmetric_fov", -1.0f);
        forced = choice < -0.5f ? -1 : (choice > 0.5f ? 1 : 0);

        if (forced < 0)
            BVR_INFO("symmetric_fov follows the render mode: AFR and native stereo "
                     "submit each eye's true asymmetric fov, and AFW and the hybrid "
                     "submit a containing symmetric one because a warped eye can "
                     "only be shown what the rendered eye saw.");
        else
            BVR_INFO("symmetric_fov = %d, pinned by config; the render mode does not "
                     "change it. Pinning it to 0 while AFW or the hybrid is running "
                     "is what breaks the warp - the two eyes stop sharing a frustum.",
                     forced);
    }

    if (forced >= 0)
        return forced != 0;

    return g_xr.afwEnabled.load(std::memory_order_acquire);
}

/* The symmetric frustum that contains `fov`: the larger half-angle on each axis.
   Must stay identical to VrCameraDriver's version, or the fov we submit and the
   fov we render with drift apart again. */
XrFovf containing_symmetric_fov(const XrFovf& fov)
{
    const float l = fov.angleLeft  < 0.0f ? -fov.angleLeft  : fov.angleLeft;
    const float r = fov.angleRight < 0.0f ? -fov.angleRight : fov.angleRight;
    const float u = fov.angleUp    < 0.0f ? -fov.angleUp    : fov.angleUp;
    const float d = fov.angleDown  < 0.0f ? -fov.angleDown  : fov.angleDown;

    const float h = l > r ? l : r;
    const float v = u > d ? u : d;

    XrFovf out{};
    out.angleLeft  = -h;
    out.angleRight =  h;
    out.angleUp    =  v;
    out.angleDown  = -v;
    return out;
}

/* The fov to hand the compositor for an image rendered under `runtimeFov`.
 *
 * The compositor undistorts using the fov we SUBMIT, so the one rule that cannot
 * be broken is that it must equal the fov the image was actually rendered with.
 * Break it and the world comes out the wrong size and the eyes fight - which is
 * what "3d effect and world scale is not optimal" was.
 *
 * With vp_patch running, the truth is knowable rather than assumable: the
 * patcher recovers the engine's real frustum from the matrix on its way to the
 * GPU. Two cases:
 *
 *   vp_fov_expand = 0 - the render keeps the engine's own frustum, so THAT is
 *       what gets submitted. The view is narrower than the headset can show, so
 *       there is unrendered space at the edges, but everything inside it is the
 *       right size and the two eyes agree. Nothing is culled that would
 *       otherwise be drawn.
 *
 *   vp_fov_expand = 1 - the patcher reshapes clip space so the engine renders
 *       the headset's own frustum, and the runtime's fov is then the correct
 *       thing to submit. Full field of view, at the cost of objects the engine's
 *       CPU-side culling drops just outside its narrower frustum.
 */
XrFovf submitted_fov(const XrFovf& runtimeFov)
{
    if (vp_enabled() && vp_expanding_fov())
    {
        /* The patcher reshaped clip space to render EXACTLY this fov, so this is
           the only correct thing to submit.
         *
           symmetric_fov must not get a say here. It is a Phase 3 debug switch
           that submits a containing SYMMETRIC frustum, and it was still set to 1
           in the config when expand mode went on - so we rendered the runtime's
           canted -54/+40 and told the compositor it was a symmetric -54/+54.
           That is the identical render-versus-submit mismatch this whole project
           opened with, reintroduced by two settings that were each reasonable
           alone. */
        return runtimeFov;
    }

    /* NOT gated on vp_enabled() any more, and that was the bug behind "the view
       resists my head".
     *
       Measuring the engine's frustum and SUBSTITUTING its matrix were the same
       switch, so turning the substitution off to stop the frame being drawn from
       two viewpoints also stopped the measurement - and this fell through to the
       computed frustum below, which is not what rgl rendered. The compositor then
       stretches the image to reconcile them, and head motion comes out scaled:
       measured at a head-to-world gain of 0.617, worse vertically (0.57) than
       horizontally (0.81), which is exactly the reported asymmetry.
     *
       vp_measured_tangents already returns false until something has actually
       measured, so asking it directly is both sufficient and honest: whoever
       filled it in - the patcher or vp_measure - this is the frustum the pixels
       were drawn with, and it is the only correct thing to submit. */
    float l = 0.0f, r = 0.0f, u = 0.0f, d = 0.0f;
    if (vp_measured_tangents(&l, &r, &u, &d))
    {
        XrFovf out{};
        out.angleLeft  = atanf(l);
        out.angleRight = atanf(r);
        out.angleUp    = atanf(u);
        out.angleDown  = atanf(d);
        return out;
    }

    return symmetric_fov_enabled() ? containing_symmetric_fov(runtimeFov) : runtimeFov;
}

/* What actually reaches the compositor, printed when it changes.
 *
 * The submitted fov has been wrong three separate times in this project and each
 * time it was inferred from config rather than read. It is one line; print it. */
void log_submitted_fov(const XrFovf& fov)
{
    static float last[4] = { 99.0f, 99.0f, 99.0f, 99.0f };

    const float now[4] = { fov.angleLeft, fov.angleRight, fov.angleUp, fov.angleDown };
    for (int i = 0; i < 4; ++i)
    {
        if (fabsf(now[i] - last[i]) > 0.01f)
        {
            for (int k = 0; k < 4; ++k)
                last[k] = now[k];

            BVR_INFO("Submitting fov L%.1f R%.1f U%.1f D%.1f deg (%.1f x %.1f).",
                     fov.angleLeft * 57.2957795f, fov.angleRight * 57.2957795f,
                     fov.angleUp * 57.2957795f, fov.angleDown * 57.2957795f,
                     (fov.angleRight - fov.angleLeft) * 57.2957795f,
                     (fov.angleUp - fov.angleDown) * 57.2957795f);
            return;
        }
    }
}

/* Reads one pixel out of the captured eye texture, once a second.
 *
 * This answers the question blocking the headset: is the texture we copy from the
 * texture the SceneView actually draws into? The monitor tracking while the
 * headset sits frozen fits BOTH "the pointer is wrong" and "the copy runs at the
 * wrong moment", and no amount of reading allocation logs separates them. A pixel
 * that never changes while the view moves means the texture is dead; one that
 * changes means it is live and the fault is downstream.
 *
 * The 1x1 staging copy plus Map stalls the pipeline, so this is a diagnostic only
 * and runs at 1 Hz. */
void sample_eye_pixel(ID3D11DeviceContext* context, ID3D11Texture2D* source)
{
    static ID3D11Texture2D* staging = nullptr;
    static uint32_t lastValue = 0xFFFFFFFFu;
    static int changes = 0;
    static int samples = 0;

    ID3D11Device* device = engine_device();
    if (source == nullptr || device == nullptr)
        return;

    D3D11_TEXTURE2D_DESC srcDesc = {};
    source->GetDesc(&srcDesc);

    if (staging == nullptr)
    {
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = 1;
        desc.Height = 1;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = srcDesc.Format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

        if (FAILED(device->CreateTexture2D(&desc, nullptr, &staging)) || staging == nullptr)
        {
            BVR_WARN("Could not create the 1x1 staging texture; pixel probe disabled.");
            return;
        }
    }

    /* Centre of the view - the least likely place to be permanently empty sky. */
    D3D11_BOX box = {};
    box.left = srcDesc.Width / 2;
    box.right = box.left + 1;
    box.top = srcDesc.Height / 2;
    box.bottom = box.top + 1;
    box.front = 0;
    box.back = 1;

    context->CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, &box);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped)))
        return;

    const uint32_t value = mapped.pData != nullptr ? *static_cast<const uint32_t*>(mapped.pData) : 0;
    context->Unmap(staging, 0);

    ++samples;
    if (value != lastValue)
    {
        ++changes;
        lastValue = value;
    }

    BVR_INFO("Eye 0 centre pixel 0x%08X (%d/%d samples changed) - %s",
             value, changes, samples,
             (changes <= 1 && samples >= 4)
                 ? "NOT CHANGING: nothing draws into the texture we copy"
                 : "changing: the texture is live");
}

/* --- alternate-frame rendering -------------------------------------------
 *
 * The engine renders one scene per frame, so AFR alternates which eye that
 * frame belongs to. Two things separate a usable AFR from a shimmering one:
 *
 *   1. Both eyes must be submitted EVERY frame with valid content. A swapchain
 *      hands back a different image on each acquire, so the untouched eye's
 *      image is not "last frame" - it is whatever was in that slot three
 *      acquires ago. Each eye's last render is therefore kept in a texture of
 *      our own and re-copied every frame.
 *   2. Each eye must be submitted with the pose it was ACTUALLY rendered with.
 *      Submitting a one-frame-old image against the current pose is precisely
 *      what makes naive AFR swim; handing the compositor the true pose lets its
 *      reprojection put the stale eye where it belongs.
 */
/* Whether AFW may run at all this session. Separate from whether it is SELECTED,
   because the hold textures have to be allocated differently for it and they are
   allocated once - so this has to be known before the mode is chosen. */
bool afw_allowed()
{
    static int cached = -1;
    if (cached < 0)
        cached = config_float("afw", 1.0f) > 0.5f ? 1 : 0;
    return cached != 0;
}

/* The typeless member of a format's family. Only the two eight-bit RGBA layouts
   are listed because those are the only ones an eye target has ever been. */
DXGI_FORMAT typeless_family_of(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:  return DXGI_FORMAT_B8G8R8A8_TYPELESS;
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:  return DXGI_FORMAT_R8G8B8A8_TYPELESS;
    default:                             return DXGI_FORMAT_UNKNOWN;
    }
}

/* --------------------------------------------------------------------------
 * DEPTH SUBMISSION
 *
 * WHAT IT FIXES, PRECISELY
 *
 * Under AFR one eye is always a frame old, and the compositor is asked to move
 * that stale image to where the head is at display time. Given only a pose it
 * can rotate the image, and rotation is a rigid operation on the whole picture
 * - which is correct for geometry at infinity and wrong for everything else.
 * Head movement is never pure rotation: the eyes orbit the neck, so any turn
 * translates them, and a translated viewpoint changes NEAR geometry's position
 * on screen more than FAR geometry's. That difference is parallax, it cannot be
 * expressed as a rotation, and with no depth to work from the compositor cannot
 * apply it. It shows up as near objects sliding against the background on one
 * eye only, alternating at half the frame rate.
 *
 * A depth image is what makes the correction possible: with a per-pixel
 * distance the compositor can move each pixel by the right amount instead of
 * moving all of them by the same amount.
 *
 * WHY IT IS ALLOCATED HERE AND NOT WITH THE COLOUR SWAPCHAINS
 *
 * Because the format is not ours to choose. The depth swapchain has to be in
 * the same typeless family as the buffer rgl draws with, and that buffer does
 * not exist at session creation - it is found later, by watching for a depth
 * target bound alongside an eye-sized render target. So the first frame that
 * has one is the earliest this can run, and it runs exactly once.
 * ------------------------------------------------------------------------ */
bool ensure_depth_swapchains(ID3D11Device* device)
{
    if (g_depthLayerReady)
        return true;

    if (!g_depthExtEnabled)
        return false;

    /* Tried once. Without this a session whose depth is unusable would re-run
       the whole enumerate-and-create dance on every single frame. */
    if (g_depthAttempted)
        return false;

    ID3D11Texture2D* gameDepth = eye_depth_texture();
    if (gameDepth == nullptr)
        return false;   /* Not yet. Not a failure - keep looking. */

    D3D11_TEXTURE2D_DESC gd = {};
    gameDepth->GetDesc(&gd);

    /* The depth swapchain is allocated at the size the game actually renders
       depth AT, never at the eye's size and never blitted up to it.
       XrCompositionLayerDepthInfoKHR carries its own XrSwapchainSubImage - its
       own swapchain and its own imageRect - so the two images need not share
       dimensions, and the compositor samples both across the same normalised
       rectangle. The CopyResource below is then same-size and same-family, and
       there is no new render pass on the engine's context, which is where this
       project's crashes have always come from.

       That machinery is why the gate immediately below is about WHAT A SMALLER
       DEPTH IMPLIES rather than about the sampling grid, which OpenXR handles
       on its own. */
    /* A DEPTH SMALLER THAN ITS COLOUR MEANS AN UPSCALER, AND AN UPSCALER MEANS
       THE DEPTH DOES NOT DESCRIBE THE COLOUR.
     *
       The gate that used to stand here rejected a size mismatch for the wrong
       reason - it claimed the two images had to share a sampling grid, and they
       do not: XrCompositionLayerDepthInfoKHR carries its own subimage precisely
       so that the depth may be a different size, and the compositor samples both
       across the same normalised rectangle. That reasoning was corrected and the
       gate came out with it. The gate was right anyway, for a reason nobody had
       written down.

       This machine renders depth at 1460x1470 against a 2920x2940 eye - exactly
       half in each axis, which is DLSS Performance, and engine_config.txt says
       so directly (antialiasing_technique = 5, dlss_technique = 1). Under an
       upscaler the colour that reaches us is not a render of that depth:

         - it was assembled by TEMPORAL ACCUMULATION out of several past frames,
           each with its own camera, so no single depth map describes it; and
         - every one of those frames was rendered with a sub-pixel JITTER offset
           that changes each frame. The colour is de-jittered on the way out.
           The depth is not, so it slides against the colour by a fraction of a
           pixel, on a slow repeating pattern.

       Handing that to the compositor as a depth layer is worse than handing it
       nothing at all. It computes a per-pixel displacement from a depth that is
       both half-resolution and drifting, then applies it to a colour image the
       depth does not match. The error is largest exactly where depth changes
       fastest per pixel - ground seen at a grazing angle, which is most of the
       lower field of view - and it varies with the jitter rather than with the
       head. That is a world that shakes slowly while the player stands still,
       and preventing it is what this gate is for.

       Turning the game's upscaling off makes the depth full-resolution and
       un-jittered, this gate opens on its own, and the positional reprojection
       it unlocks is a real improvement to AFR's weakest case. That is the fix.
       Declining is the correct behaviour until then. */
    g_depthUpscaling = gd.Width != g_xr.swapchains[0].width ||
                       gd.Height != g_xr.swapchains[0].height;

    if (g_depthUpscaling)
    {
        /* NOT latched as an attempt yet, so a larger depth appearing later
           still gets its chance at the direct path. The depth search prefers
           the largest candidate and keeps looking for 180 frames, and the first
           depth bound with an eye target is not always the scene pass. */
        if (!config_bool("depth_layer_upscale", true))
        {
            static bool warned = false;
            if (warned)
                return false;
            warned = true;

            BVR_WARN("Depth layer: the game renders depth at %ux%u against a %ux%u "
                     "eye and depth_layer_upscale = 0, so it is declined. The eye "
                     "goes up with a pose only, which limits the compositor to "
                     "rotational reprojection.",
                     gd.Width, gd.Height,
                     g_xr.swapchains[0].width, g_xr.swapchains[0].height);
            return false;
        }

        BVR_INFO("Depth layer: the game renders depth at %ux%u against a %ux%u eye "
                 "(%.0f%% in each axis), which is an upscaler - DLSS or FSR - "
                 "sitting between the scene and the image we submit. The depth is "
                 "resampled to the eye's size before it goes up, so the two rects "
                 "match exactly and no runtime is left to interpret a mismatch. "
                 "See depth_upscale.h for what this does and does not fix.",
                 gd.Width, gd.Height,
                 g_xr.swapchains[0].width, g_xr.swapchains[0].height,
                 100.0 * gd.Width / (double)g_xr.swapchains[0].width);
    }

    /* Past the size gate: from here the attempt is real and is made once. */
    g_depthAttempted = true;

    if (gd.SampleDesc.Count != 1)
    {
        BVR_WARN("Depth layer: the game's depth is %u-sample multisampled, which "
                 "CopyResource cannot move into a single-sample swapchain. "
                 "Submitting without depth.", gd.SampleDesc.Count);
        return false;
    }

    /* THE UPSCALED PATH PINS THE FORMAT; THE DIRECT PATH INHERITS IT.
     *
       Direct, the copy is game depth -> swapchain image, so the swapchain has
       to be in the game depth's own type group and pick_depth_format finds one.

       Upscaled, the copy is OUR R32_TYPELESS intermediate -> swapchain image,
       and D3D11 will only copy inside one type group. D32_FLOAT is the sibling
       of R32_TYPELESS, so that is the only format this path can use - and it
       has to be checked rather than assumed, because CopyResource returns void.
       A mismatched copy does not fail loudly; it fails by leaving the depth
       image holding whatever it held before, which reaches the compositor as
       confident nonsense. Refusing up front is the only way to know. */
    if (g_depthUpscaling)
    {
        if (!runtime_offers_depth_format(DXGI_FORMAT_D32_FLOAT))
        {
            BVR_WARN("Depth layer: the resampled path needs a D32_FLOAT swapchain "
                     "so the copy out of the R32_TYPELESS intermediate stays inside "
                     "one format group, and this runtime does not offer one. "
                     "Submitting without depth.");
            return false;
        }
        g_xr.depthFormat = static_cast<int64_t>(DXGI_FORMAT_D32_FLOAT);
    }
    else
    {
        g_xr.depthFormat = pick_depth_format(gd.Format);
        if (g_xr.depthFormat == 0)
            return false;   /* pick_depth_format has already said why. */
    }

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        EyeSwapchain& sc = g_xr.swapchains[eye];

        XrSwapchainCreateInfo info{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        info.usageFlags = XR_SWAPCHAIN_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT |
                          XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
        info.format = g_xr.depthFormat;
        info.sampleCount = 1;
        /* Upscaled, this is the EYE's size and the submitted depth rect is
           identical to the colour rect. Direct, it is the game's own depth size
           and the copy into it is same-size either way. */
        sc.depthWidth  = g_depthUpscaling ? sc.width  : gd.Width;
        sc.depthHeight = g_depthUpscaling ? sc.height : gd.Height;
        info.width  = sc.depthWidth;
        info.height = sc.depthHeight;
        info.faceCount = 1;
        info.arraySize = 1;
        info.mipCount = 1;

        if (!check(xrCreateSwapchain(g_xr.session, &info, &sc.depthHandle),
                   "xrCreateSwapchain(depth)"))
            return false;

        uint32_t imageCount = 0;
        if (!check(xrEnumerateSwapchainImages(sc.depthHandle, 0, &imageCount, nullptr),
                   "xrEnumerateSwapchainImages(depth count)"))
            return false;

        sc.depthImages.assign(imageCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        if (!check(xrEnumerateSwapchainImages(
                       sc.depthHandle, imageCount, &imageCount,
                       reinterpret_cast<XrSwapchainImageBaseHeader*>(sc.depthImages.data())),
                   "xrEnumerateSwapchainImages(depth)"))
            return false;
    }

    /* The held depth, one pair mirroring the colour hold. Created from the
       game's own descriptor so the copy into them is same-format by
       construction. Held at the GAME's depth size in both paths: resampling
       happens at submit time, on the one eye being submitted, so holding the
       upscaled version instead would cost four times the memory to store
       exactly the same information.

       Readable only on the upscaled path, where the shader has to sample them.
       Direct, nothing ever reads these - they are pure staging between one
       frame and the next - and a bind flag that nothing uses is a permission
       granted for no reason. */
    D3D11_TEXTURE2D_DESC hold = gd;
    hold.Usage = D3D11_USAGE_DEFAULT;
    hold.BindFlags = g_depthUpscaling ? D3D11_BIND_SHADER_RESOURCE : 0;
    hold.CPUAccessFlags = 0;
    hold.MiscFlags = 0;
    hold.MipLevels = 1;
    hold.ArraySize = 1;

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        if (FAILED(device->CreateTexture2D(&hold, nullptr, &g_xr.afrDepthResult[eye])) ||
            FAILED(device->CreateTexture2D(&hold, nullptr, &g_xr.afrDepthStage[eye])))
        {
            BVR_ERR("Depth layer: could not allocate the held depth for eye %u. "
                    "Submitting without depth.", eye);
            return false;
        }
    }

    g_depthLayerReady = true;
    BVR_INFO("Depth layer up: %ux%u, format %lld, from the game's own depth buffer "
             "(format %d). A held eye is now reprojected for head MOVEMENT as well "
             "as rotation.",
             g_xr.swapchains[0].depthWidth, g_xr.swapchains[0].depthHeight,
             static_cast<long long>(g_xr.depthFormat), static_cast<int>(gd.Format));
    return true;
}

/* --------------------------------------------------------------------------
 * THE FLAT SCREEN
 *
 * Everything Bannerlord draws that is not a mission - the main menu, the
 * campaign map, the inventory, the encounter screens, loading - is a 2D image
 * in the game's own swapchain. None of it goes anywhere near the stereo path,
 * so with only a projection layer to submit, the headset showed the cleared eye
 * images: a flat colour, while the game was perfectly playable on the monitor.
 *
 * The answer is not to render those screens in stereo. They have no depth to
 * have, and a menu painted across the full field of view at infinity is
 * uncomfortable to read and worse to aim at. What a headset wants is a SCREEN:
 * the game's own backbuffer on a quad, at a fixed distance, at its own aspect
 * ratio, with the rest of the view left empty.
 *
 * That is a composition layer quad, which is the same mechanism the settings
 * panel already uses - so the runtime samples the image at its own resolution
 * and reprojects it as the head moves, and it stays sharp and stable without
 * this code doing anything per frame but one CopyResource.
 *
 * WHY IT REPLACES THE PROJECTION LAYER RATHER THAN SITTING OVER IT
 *
 * Because behind it there is nothing worth showing. Outside a mission the eye
 * swapchains hold either a cleared colour or the last frame of a battle that
 * has ended, and both are worse than black. Submitting the quad alone also
 * means the whole eye path - capture, hold, depth, submit - is skipped on menu
 * frames, which is why the campaign map does not pay for stereo it never uses.
 * ------------------------------------------------------------------------ */

/* Defined further down, next to the settings panel that also uses it. */
XrVector3f rotate_by(const XrQuaternionf& q, const XrVector3f& v);

/* Both defined below, with the UI overlay they belong to. The flat screen needs
   them because inside a mission the picture it should show is the composed one
   rather than the backbuffer - see the note at its CopyResource. */
bool ensure_ui_overlay(ID3D11Device* device, ID3D11Texture2D* backbuffer);
bool build_mirror_image(ID3D11DeviceContext* context, ID3D11Texture2D* backbuffer,
                        bool preferLiveEye);

/* The backbuffer is the SOURCE, so its format decides ours: CopyResource will
   not cross typeless families any more here than it will for depth. */
int64_t pick_flat_format(DXGI_FORMAT backbuffer)
{
    const DXGI_FORMAT family = typeless_family_of(backbuffer);
    if (family == DXGI_FORMAT_UNKNOWN)
    {
        BVR_WARN("Flat screen: backbuffer format %d is not a family this knows how "
                 "to copy. Menus stay on the monitor.", static_cast<int>(backbuffer));
        return 0;
    }

    uint32_t count = 0;
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, 0, &count, nullptr),
               "xrEnumerateSwapchainFormats(flat count)"))
        return 0;

    std::vector<int64_t> formats(count);
    if (!check(xrEnumerateSwapchainFormats(g_xr.session, count, &count, formats.data()),
               "xrEnumerateSwapchainFormats(flat)"))
        return 0;

    /* sRGB first for the same reason the eye swapchain prefers it: the
       backbuffer holds display-ready pixels, and declaring the swapchain sRGB is
       what tells the compositor to linearise before blending. Getting this wrong
       is not a broken menu, only a washed-out one. */
    for (int pass = 0; pass < 2; ++pass)
    {
        for (int64_t have : formats)
        {
            const DXGI_FORMAT f = static_cast<DXGI_FORMAT>(have);
            if (typeless_family_of(f) != family)
                continue;

            const bool srgb = (f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
                               f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB);
            if ((pass == 0) == srgb)
                return have;
        }
    }

    BVR_WARN("Flat screen: the runtime offers no swapchain format in the "
             "backbuffer's family. Menus stay on the monitor.");
    return 0;
}

bool ensure_flat_swapchain(ID3D11Texture2D* backbuffer)
{
    if (g_xr.flatReady)
        return true;

    if (g_xr.flatAttempted || backbuffer == nullptr)
        return false;

    g_xr.flatAttempted = true;

    D3D11_TEXTURE2D_DESC bd = {};
    backbuffer->GetDesc(&bd);

    if (bd.SampleDesc.Count != 1)
    {
        BVR_WARN("Flat screen: the backbuffer is %u-sample multisampled, which "
                 "CopyResource cannot move into a swapchain. Menus stay on the "
                 "monitor.", bd.SampleDesc.Count);
        return false;
    }

    g_xr.flatFormat = pick_flat_format(bd.Format);
    if (g_xr.flatFormat == 0)
        return false;

    XrSwapchainCreateInfo info{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
    info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT |
                      XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    info.format = g_xr.flatFormat;
    info.sampleCount = 1;
    info.width = bd.Width;
    info.height = bd.Height;
    info.faceCount = 1;
    info.arraySize = 1;
    info.mipCount = 1;

    if (!check(xrCreateSwapchain(g_xr.session, &info, &g_xr.flatSwapchain),
               "xrCreateSwapchain(flat)"))
        return false;

    uint32_t imageCount = 0;
    if (!check(xrEnumerateSwapchainImages(g_xr.flatSwapchain, 0, &imageCount, nullptr),
               "xrEnumerateSwapchainImages(flat count)"))
        return false;

    g_xr.flatImages.assign(imageCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
    if (!check(xrEnumerateSwapchainImages(
                   g_xr.flatSwapchain, imageCount, &imageCount,
                   reinterpret_cast<XrSwapchainImageBaseHeader*>(g_xr.flatImages.data())),
               "xrEnumerateSwapchainImages(flat)"))
        return false;

    g_xr.flatWidth = bd.Width;
    g_xr.flatHeight = bd.Height;
    g_xr.flatReady = true;

    BVR_INFO("Flat screen up: %ux%u, format %lld. Menus, the campaign map and "
             "everything else outside a mission now appear on a screen in the "
             "headset instead of leaving it blank.",
             bd.Width, bd.Height, static_cast<long long>(g_xr.flatFormat));
    return true;
}

/* How wide the screen is, in metres, at flat_screen_distance. Two metres of
   width at two metres of distance is a little over 50 degrees - big enough to
   read Bannerlord's small campaign-map text, comfortably inside the field of
   view so the edges do not need a head turn to see. */
/* Live, like the overlay's, so the panel can move them while the player is
   looking at the screen they move. */
std::atomic<float> g_screenWidth{ -1.0f };
std::atomic<float> g_screenDistance{ -1.0f };

float flat_screen_width()
{
    float v = g_screenWidth.load(std::memory_order_acquire);
    if (v < 0.0f)
    {
        v = clampf(config_float("flat_screen_width", 2.0f), 0.2f, 20.0f);
        g_screenWidth.store(v, std::memory_order_release);
    }
    return v;
}

/* Closer and wider than the flat screen: this one is an overlay ON the view
   rather than a panel in the room, so it wants to subtend roughly what the
   monitor did - which means filling most of the field rather than sitting in a
   window in the middle of it. */
/* Live rather than cached, because the VR panel moves them while the player is
   looking at the thing they move. Atomics because the panel writes from the
   managed thread and the frame loop reads from the render thread. */
std::atomic<float> g_uiWidth{ -1.0f };
std::atomic<float> g_uiDistance{ -1.0f };

float ui_overlay_width()
{
    float v = g_uiWidth.load(std::memory_order_acquire);
    if (v < 0.0f)
    {
        v = clampf(config_float("ui_overlay_width", 2.2f), 0.2f, 20.0f);
        g_uiWidth.store(v, std::memory_order_release);
    }
    return v;
}

float ui_overlay_distance()
{
    float v = g_uiDistance.load(std::memory_order_acquire);
    if (v < 0.0f)
    {
        v = clampf(config_float("ui_overlay_distance", 1.6f), 0.3f, 20.0f);
        g_uiDistance.store(v, std::memory_order_release);
    }
    return v;
}

float flat_screen_distance()
{
    float v = g_screenDistance.load(std::memory_order_acquire);
    if (v < 0.0f)
    {
        v = clampf(config_float("flat_screen_distance", 2.0f), 0.3f, 20.0f);
        g_screenDistance.store(v, std::memory_order_release);
    }
    return v;
}

/* Copies the backbuffer onto the quad and describes where it hangs. Returns
   false if there is nothing to show, in which case the caller submits the
   stereo path as usual. */
bool flat_screen_submit(ID3D11DeviceContext* context, IDXGISwapChain* swapChain,
                        const XrView views[kEyeCount], bool posesValid,
                        XrCompositionLayerQuad* out)
{
    if (swapChain == nullptr || out == nullptr || context == nullptr)
        return false;

    ID3D11Texture2D* backbuffer = nullptr;
    if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                    reinterpret_cast<void**>(&backbuffer))) ||
        backbuffer == nullptr)
        return false;

    /* Released on every path out of here, including the failures below. */
    struct Release
    {
        ID3D11Texture2D* t;
        ~Release() { if (t != nullptr) t->Release(); }
    } release{ backbuffer };

    if (!ensure_flat_swapchain(backbuffer))
        return false;

    /* The backbuffer can be resized under us by a resolution change, and a
       CopyResource between different sizes is rejected outright. Rebuilding the
       swapchain mid-session is more machinery than this needs; saying so once
       and staying on the monitor is the honest failure. */
    D3D11_TEXTURE2D_DESC bd = {};
    backbuffer->GetDesc(&bd);
    if (bd.Width != g_xr.flatWidth || bd.Height != g_xr.flatHeight)
    {
        if (!g_xr.flatSizeWarned)
        {
            g_xr.flatSizeWarned = true;
            BVR_WARN("Flat screen: the backbuffer is now %ux%u but the screen was "
                     "built for %ux%u. Restart the game after a resolution change "
                     "to get menus back in the headset.",
                     bd.Width, bd.Height, g_xr.flatWidth, g_xr.flatHeight);
        }
        return false;
    }

    uint32_t imageIndex = 0;
    XrSwapchainImageAcquireInfo acquire{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
    if (XR_FAILED(xrAcquireSwapchainImage(g_xr.flatSwapchain, &acquire, &imageIndex)))
        return false;

    XrSwapchainImageWaitInfo wait{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
    wait.timeout = XR_INFINITE_DURATION;
    const bool waited = XR_SUCCEEDED(xrWaitSwapchainImage(g_xr.flatSwapchain, &wait));

    if (waited)
    {
        /* INSIDE A MISSION THE BACKBUFFER IS NOT THE PICTURE.
         *
         * Outside one it is exactly right: the game draws its whole image
         * there and the screen shows it.
         *
         * Inside one it holds the interface and nothing else - the world goes
         * to the eye target, and the backbuffer is cleared each frame so the
         * key can separate the interface from it. Copying that gives a menu
         * floating on black, which is what a conversation looked like.
         *
         * So when there is a world to put behind it, the screen shows the
         * composed image instead: the same eye the headset is being handed,
         * with the interface on top. Falls back to the backbuffer whenever the
         * composition is not available, which is the correct picture in every
         * case where it is not. */
        ID3D11Texture2D* source = backbuffer;

        if (g_xr.inMission.load(std::memory_order_acquire) &&
            ensure_ui_overlay(engine_device(), backbuffer) &&
            build_mirror_image(context, backbuffer, /* preferLiveEye */ true))
        {
            source = g_xr.uiMirror;
        }

        context->CopyResource(g_xr.flatImages[imageIndex].texture, source);
    }

    XrSwapchainImageReleaseInfo releaseInfo{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
    xrReleaseSwapchainImage(g_xr.flatSwapchain, &releaseInfo);

    if (!waited)
        return false;

    /* PINNED to where the head was when the screen first appeared, not carried
       with it. A menu that follows your head cannot be looked away from and is
       markedly harder to put a cursor on - the settings panel pins itself for
       the same reason. */
    /* LET TRACKING SETTLE BEFORE PINNING.
     *
     * "The screen is behind me on launch" survived the placement fix, and the
     * arithmetic was right, so the input must be wrong: a runtime can report
     * posesValid on its first frames while still handing back an identity pose,
     * or one from before it has worked out where the player is. Pin to that and
     * the screen goes two metres along the reference space's forward, which is
     * wherever the player is NOT - and being a one-shot pin, it stayed there.
     *
     * A quarter of a second of consecutive valid poses is far longer than the
     * settle takes and far shorter than the walk from launch to the menu. The
     * counter resets on any invalid frame, so a headset picked up mid-launch
     * waits for it to be genuinely tracking rather than merely reporting. */
    if (!g_xr.flatPosePinned && posesValid)
    {
        /* ONCE PER SESSION, not once per pin. The settle is there for the cold
           start; every pin after that - leaving a mission, opening a menu, a
           recentre - happens while tracking is long since good, and making
           those wait a quarter of a second would put a visible stutter on
           every menu the player opens. */
        if (!g_xr.flatEverPinned && ++g_xr.flatSettleFrames < 30)
            return false;
    }
    else if (!posesValid)
    {
        g_xr.flatSettleFrames = 0;
    }

    if (!g_xr.flatPosePinned && posesValid)
    {
        /* Straight ahead of where the head is looking, but LEVEL: the heading is
           kept and the pitch and roll discarded, so a screen pinned while
           glancing down does not end up on the floor, and one pinned with your
           head tilted does not hang crooked for the rest of the session.

           The direction comes from ROTATING the view's forward axis, not from
           reconstructing it out of an extracted yaw angle. Both are the same
           arithmetic in principle; only one of them has a sign to get wrong, and
           getting it wrong put the screen behind the player at some headings and
           in front at others - which is exactly how it presented. */
        const XrPosef& head = views[0].pose;
        const XrVector3f forward = rotate_by(head.orientation,
                                             XrVector3f{ 0.0f, 0.0f, -1.0f });

        /* Flatten to the horizontal plane. Looking straight up or down leaves
           nothing to flatten, so fall back to the reference space's own forward
           rather than normalising a zero vector. */
        float fx = forward.x;
        float fz = forward.z;
        float len = std::sqrt(fx * fx + fz * fz);

        if (len < 1e-4f)
        {
            fx = 0.0f;
            fz = -1.0f;
            len = 1.0f;
        }

        fx /= len;
        fz /= len;

        /* The heading whose own forward is (fx, fz). Derived FROM the direction
           the screen is being placed along, so the quad cannot end up facing a
           different way from where it sits. */
        const float yaw = std::atan2(-fx, -fz);
        const float half = yaw * 0.5f;
        g_xr.flatPose.orientation = { 0.0f, std::sin(half), 0.0f, std::cos(half) };

        /* Height taken from the head as it is, so the screen sits at eye level
           rather than at the floor's. */
        const float d = flat_screen_distance();
        g_xr.flatPose.position = {
            head.position.x + fx * d,
            head.position.y,
            head.position.z + fz * d
        };

        g_xr.flatPosePinned = true;
        g_xr.flatEverPinned = true;
    }

    /* Never submit an unpinned pose. flatPose starts zero-initialised, which is
       a ZERO QUATERNION rather than an identity one - not merely a screen in the
       wrong place but a degenerate rotation, and at the space's origin, which is
       wherever the player happens not to be. Waiting a frame for tracking costs
       nothing; the menu is still there next frame. */
    if (!g_xr.flatPosePinned)
        return false;

    const float width = flat_screen_width();
    const float height = width * static_cast<float>(g_xr.flatHeight) /
                                 static_cast<float>(g_xr.flatWidth);

    out->type = XR_TYPE_COMPOSITION_LAYER_QUAD;
    out->next = nullptr;
    out->layerFlags = 0;
    out->space = g_xr.appSpace;
    out->eyeVisibility = XR_EYE_VISIBILITY_BOTH;
    out->subImage.swapchain = g_xr.flatSwapchain;
    out->subImage.imageArrayIndex = 0;
    out->subImage.imageRect.offset = { 0, 0 };
    out->subImage.imageRect.extent = {
        static_cast<int32_t>(g_xr.flatWidth), static_cast<int32_t>(g_xr.flatHeight)
    };
    out->pose = g_xr.flatPose;
    out->size = { width, height };

    /* IS THE MENU SCREEN ACTUALLY WORLD-LOCKED?
     *
     * It is by construction - a quad, in STAGE space, at a pose pinned once and
     * dropped only by an explicit recentre - and yet it is reported as following
     * the head slightly and springing back, which is what a HEAD-locked quad
     * under compositor reprojection looks like. Reading the code cannot settle
     * that: every re-pin site is an explicit action, so if the pose is moving,
     * it is moving for a reason the code does not state.
     *
     * So print both. If the pinned pose is constant while the head moves, the
     * quad is world-locked and the motion is the runtime's own reprojection of
     * it - which is a different problem from ours and lives in the compositor.
     * If the pinned pose tracks the head, something IS re-pinning, and this says
     * so with the two numbers side by side rather than by inference. */
    static uint64_t lastProbeMs = 0;
    const uint64_t nowMs = GetTickCount64();
    if (posesValid && nowMs - lastProbeMs >= 1000)
    {
        lastProbeMs = nowMs;
        const XrPosef& head = views[0].pose;
        BVR_INFO("Flat screen: quad pinned at (%.3f,%.3f,%.3f) yaw-quat "
                 "(%.3f,%.3f,%.3f,%.3f) in STAGE space; head now at "
                 "(%.3f,%.3f,%.3f) (%.3f,%.3f,%.3f,%.3f). A constant quad pose "
                 "against a moving head is world-lock working.",
                 g_xr.flatPose.position.x, g_xr.flatPose.position.y,
                 g_xr.flatPose.position.z,
                 g_xr.flatPose.orientation.x, g_xr.flatPose.orientation.y,
                 g_xr.flatPose.orientation.z, g_xr.flatPose.orientation.w,
                 head.position.x, head.position.y, head.position.z,
                 head.orientation.x, head.orientation.y,
                 head.orientation.z, head.orientation.w);
    }

    return true;
}

/* --------------------------------------------------------------------------
 * THE UI OVERLAY
 *
 * The same picture the flat screen shows, but keyed and composited OVER the
 * stereo world instead of replacing it.
 *
 * Which of the two a menu gets is not a style choice. A screen you stop to read
 * - inventory, the escape menu, a conversation - loses nothing by replacing the
 * view, because during a mission the game's image has no world in it anyway.
 * A menu you use WHILE playing is the opposite case: the tactics wheel exists
 * to be turned mid-fight and is meaningless without the field behind it. That
 * one has to float over the battle, which means the background has to go, which
 * means keying. See ui_key.cpp for why keying is honest here.
 *
 * Its own texture rather than the flat screen's, because the two want different
 * formats: the flat screen is a straight CopyResource of the backbuffer and
 * this needs a UAV to write alpha into.
 * ------------------------------------------------------------------------ */
bool ensure_ui_overlay(ID3D11Device* device, ID3D11Texture2D* backbuffer)
{
    if (g_xr.uiReady)
        return true;

    if (g_xr.uiAttempted || backbuffer == nullptr || device == nullptr)
        return false;

    g_xr.uiAttempted = true;

    D3D11_TEXTURE2D_DESC bd = {};
    backbuffer->GetDesc(&bd);

    if (bd.SampleDesc.Count != 1)
        return false;

    const DXGI_FORMAT family = typeless_family_of(bd.Format);
    if (family == DXGI_FORMAT_UNKNOWN)
    {
        BVR_WARN("UI overlay: backbuffer format %d has no typeless family this knows, "
                 "so alpha cannot be written into a copy of it.",
                 static_cast<int>(bd.Format));
        return false;
    }

    /* Swapchain first: if the runtime will not give us a format in the
       backbuffer's family there is no point allocating anything else. */
    g_xr.uiFormat = pick_flat_format(bd.Format);
    if (g_xr.uiFormat == 0)
        return false;

    XrSwapchainCreateInfo info{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
    info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT |
                      XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    info.format = g_xr.uiFormat;
    info.sampleCount = 1;
    info.width = bd.Width;
    info.height = bd.Height;
    info.faceCount = 1;
    info.arraySize = 1;
    info.mipCount = 1;

    if (!check(xrCreateSwapchain(g_xr.session, &info, &g_xr.uiSwapchain),
               "xrCreateSwapchain(ui)"))
        return false;

    uint32_t imageCount = 0;
    if (!check(xrEnumerateSwapchainImages(g_xr.uiSwapchain, 0, &imageCount, nullptr),
               "xrEnumerateSwapchainImages(ui count)"))
        return false;

    g_xr.uiImages.assign(imageCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
    if (!check(xrEnumerateSwapchainImages(
                   g_xr.uiSwapchain, imageCount, &imageCount,
                   reinterpret_cast<XrSwapchainImageBaseHeader*>(g_xr.uiImages.data())),
               "xrEnumerateSwapchainImages(ui)"))
        return false;

    /* A READABLE COPY OF THE BACKBUFFER, and it is not optional.
     *
     * A swapchain's backbuffer is created by DXGI with render-target usage and
     * nothing else, so CreateShaderResourceView on it simply fails - which is
     * exactly what the first build of this did, and the log said so:
     *
     *     UI key: the game's image cannot be read as a shader resource
     *     (format 28); no overlay this session.
     *
     * Format 28 is R8G8B8A8_UNORM and there was never anything wrong with it;
     * the texture just could not be sampled. The flat screen never hit this
     * because a straight CopyResource into a swapchain image reads nothing.
     *
     * So the backbuffer is copied into a texture that CAN be sampled, and the
     * shader reads that. One extra full-screen copy on menu frames only. */
    D3D11_TEXTURE2D_DESC rd = bd;
    rd.Usage = D3D11_USAGE_DEFAULT;
    rd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    rd.CPUAccessFlags = 0;
    rd.MiscFlags = 0;
    rd.MipLevels = 1;
    rd.ArraySize = 1;

    if (FAILED(device->CreateTexture2D(&rd, nullptr, &g_xr.uiSource)))
    {
        BVR_ERR("UI overlay: could not allocate the readable copy of the game's image.");
        return false;
    }

    /* The keyed copy. TYPELESS so a UAV can be created on it - a typed UNORM
       surface will not take one - and same-family so the copy into the
       swapchain image is legal. */
    D3D11_TEXTURE2D_DESC kd = bd;
    kd.Format = family;
    kd.Usage = D3D11_USAGE_DEFAULT;
    kd.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    kd.CPUAccessFlags = 0;
    kd.MiscFlags = 0;
    kd.MipLevels = 1;
    kd.ArraySize = 1;

    if (FAILED(device->CreateTexture2D(&kd, nullptr, &g_xr.uiKeyed)))
    {
        BVR_ERR("UI overlay: could not allocate the keyed texture.");
        return false;
    }

    /* The composed monitor image. Same size and family as the backbuffer it
       will be copied into, and a UAV because the compose shader writes it. */
    if (FAILED(device->CreateTexture2D(&kd, nullptr, &g_xr.uiMirror)))
    {
        BVR_ERR("UI overlay: could not allocate the monitor mirror texture.");
        return false;
    }

    g_xr.uiWidth = bd.Width;
    g_xr.uiHeight = bd.Height;
    g_xr.uiReady = true;

    BVR_INFO("UI overlay up: %ux%u. Menus meant to be used while playing now float "
             "over the world instead of replacing it.", bd.Width, bd.Height);
    return true;
}

/* Copies the game image and keys it, at most once per frame. Both the headset
   overlay and the monitor mirror want the same result. */
bool key_ui_once(ID3D11DeviceContext* context, ID3D11Texture2D* backbuffer)
{
    if (g_xr.uiKeyedThisFrame)
        return true;

    context->CopyResource(g_xr.uiSource, backbuffer);

    if (!ui_key_apply(engine_device(), context, g_xr.uiSource, g_xr.uiKeyed))
        return false;

    g_xr.uiKeyedThisFrame = true;
    return true;
}

bool ui_overlay_submit(ID3D11DeviceContext* context, IDXGISwapChain* swapChain,
                       const XrView views[kEyeCount], bool posesValid,
                       XrCompositionLayerQuad* out)
{
    if (swapChain == nullptr || out == nullptr || context == nullptr || !posesValid)
        return false;

    ID3D11Texture2D* backbuffer = nullptr;
    if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                    reinterpret_cast<void**>(&backbuffer))) ||
        backbuffer == nullptr)
        return false;

    struct Release
    {
        ID3D11Texture2D* t;
        ~Release() { if (t != nullptr) t->Release(); }
    } release{ backbuffer };

    if (!ensure_ui_overlay(engine_device(), backbuffer))
        return false;

    D3D11_TEXTURE2D_DESC bd = {};
    backbuffer->GetDesc(&bd);
    if (bd.Width != g_xr.uiWidth || bd.Height != g_xr.uiHeight)
        return false;

    /* No key, no overlay. An unkeyed copy is an opaque rectangle across the
       middle of the battle, which is worse than showing nothing. */
    if (!key_ui_once(context, backbuffer))
        return false;

    uint32_t index = 0;
    XrSwapchainImageAcquireInfo acquire{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
    if (XR_FAILED(xrAcquireSwapchainImage(g_xr.uiSwapchain, &acquire, &index)))
        return false;

    XrSwapchainImageWaitInfo wait{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
    wait.timeout = XR_INFINITE_DURATION;
    const bool waited = XR_SUCCEEDED(xrWaitSwapchainImage(g_xr.uiSwapchain, &wait));

    if (waited)
        context->CopyResource(g_xr.uiImages[index].texture, g_xr.uiKeyed);

    XrSwapchainImageReleaseInfo releaseInfo{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
    xrReleaseSwapchainImage(g_xr.uiSwapchain, &releaseInfo);

    if (!waited)
        return false;

    /* HEAD-LOCKED, unlike the flat screen, and for the opposite reason. The flat
       screen is a thing you stand in front of and read; this is an overlay on
       the view itself, and the interface it carries - a crosshair, a tactics
       wheel centred on where you are looking - is authored to sit at fixed
       screen positions. Pinning it to the world would leave the wheel behind
       your shoulder the moment you turned to use it. */
    const XrPosef& head = views[0].pose;
    const XrVector3f forward = rotate_by(head.orientation, XrVector3f{ 0.0f, 0.0f, -1.0f });

    const float d = ui_overlay_distance();

    out->type = XR_TYPE_COMPOSITION_LAYER_QUAD;
    out->next = nullptr;
    out->layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
    out->space = g_xr.appSpace;
    out->eyeVisibility = XR_EYE_VISIBILITY_BOTH;
    out->subImage.swapchain = g_xr.uiSwapchain;
    out->subImage.imageArrayIndex = 0;
    out->subImage.imageRect.offset = { 0, 0 };
    out->subImage.imageRect.extent = {
        static_cast<int32_t>(g_xr.uiWidth), static_cast<int32_t>(g_xr.uiHeight)
    };
    out->pose.orientation = head.orientation;
    out->pose.position = {
        head.position.x + forward.x * d,
        head.position.y + forward.y * d,
        head.position.z + forward.z * d,
    };

    const float width = ui_overlay_width();
    out->size = { width, width * static_cast<float>(g_xr.uiHeight) /
                          static_cast<float>(g_xr.uiWidth) };
    return true;
}

/* Builds "the world with the interface on top" into g_xr.uiMirror, at the game
 * window's size. At most once per frame - the key pass is shared, and the
 * compose is cheap enough that a second caller in the same frame simply gets
 * the same picture rebuilt from the same inputs.
 *
 * TWO CALLERS, AND THE SECOND ONE IS WHY THIS IS A FUNCTION
 *
 * The window wants it, obviously. So does the FLAT SCREEN, and that took a bug
 * to notice: a conversation is treated as something to read, so it replaces the
 * view with the flat screen, and the flat screen shows the game's own image -
 * which during a mission is the interface on black, because the world went to
 * the eye target and the backbuffer was cleared so the key would work. Correct
 * on both counts and useless together: the player got a dialogue floating on
 * nothing.
 *
 * The composed image is what the flat screen actually wanted all along - the
 * scene behind the interface, at the panel's own resolution.
 */
bool build_mirror_image(ID3D11DeviceContext* context, ID3D11Texture2D* backbuffer,
                        bool preferLiveEye)
{
    if (!g_xr.uiReady || g_xr.uiMirror == nullptr)
        return false;

    /* WHICH EYE IMAGE, AND IT DEPENDS ON WHO IS ASKING.
     *
     * The window mirror wants afrResult: that is the held copy the headset is
     * actually being shown, so the two agree.
     *
     * The flat screen cannot use it. It only runs on frames the stereo path is
     * skipped, and skipping the stereo path skips the capture that fills
     * afrResult - so it holds whatever was last captured before the menu
     * opened. During a conversation that is a frozen picture of wherever the
     * player was standing when the dialogue began.
     *
     * The engine's own eye target is still being drawn into every frame, though
     * - the retarget and the camera are untouched by any of this - so that is
     * the live one, and it is what the flat screen should show. */
    ID3D11Texture2D* eye = nullptr;

    if (preferLiveEye)
        eye = eye_texture(0);

    if (eye == nullptr && g_xr.afrHasResult[0])
        eye = g_xr.afrResult[0];

    if (eye == nullptr)
        return false;

    /* The interface, lifted off the frame it was drawn over. Shared with the
       headset overlay when a menu frame has already done it. */
    if (!key_ui_once(context, backbuffer))
        return false;

    /* Composed into our own texture rather than written straight into a
       swapchain image: those will not take a UAV any more than they will take
       an SRV. */
    return ui_mirror_compose(engine_device(), context, eye, g_xr.uiKeyed, g_xr.uiMirror);
}

/* 1 (default) rebuilds the game window from the headset's own image. 0 leaves
   whatever the game put there - which during a mission is the interface over a
   stale frame, because AFR renders the world into the eye target instead. */
bool monitor_mirror_enabled()
{
    static int cached = -1;
    if (cached < 0)
        cached = config_bool("monitor_mirror", true) ? 1 : 0;
    return cached != 0;
}

/* Makes the window show what the headset shows.
 *
 * Runs LAST in the frame, after the flat screen and the UI overlay have taken
 * their copies of the backbuffer - because this overwrites it, and they need
 * the game's own image rather than the one we are about to assemble.
 *
 * Keying happens on every mission frame rather than only when a menu is open,
 * and that is deliberate: the health bar, the ammo count and the kill feed are
 * interface too, and the monitor should carry them even when no menu is up.
 * The keyed texture is shared with the headset overlay, so a menu frame pays
 * for one key pass rather than two.
 */
void mirror_to_monitor(ID3D11DeviceContext* context, IDXGISwapChain* swapChain)
{
    if (!monitor_mirror_enabled() || swapChain == nullptr || context == nullptr)
        return;

    /* The eye the headset is actually being shown. Under AFR both eyes are held
       textures, and eye 0 is as good a choice as eye 1 - a monitor has no
       stereo to preserve. */
    ID3D11Texture2D* eye = g_xr.afrResult[0];
    if (eye == nullptr || !g_xr.afrHasResult[0])
        return;

    ID3D11Texture2D* backbuffer = nullptr;
    if (FAILED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                    reinterpret_cast<void**>(&backbuffer))) ||
        backbuffer == nullptr)
        return;

    struct Release
    {
        ID3D11Texture2D* t;
        ~Release() { if (t != nullptr) t->Release(); }
    } release{ backbuffer };

    if (!ensure_ui_overlay(engine_device(), backbuffer))
        return;

    D3D11_TEXTURE2D_DESC bd = {};
    backbuffer->GetDesc(&bd);
    if (bd.Width != g_xr.uiWidth || bd.Height != g_xr.uiHeight)
        return;

    /* Have the NEXT frame's interface drawn onto black.
     *
     * Without this the interface is painted over whatever the flip chain hands
     * back - which, once this function starts writing its composite there, is
     * this function's own output from a few frames ago. Keying by brightness
     * then keeps all of it, the mirror eats its own tail, and the window only
     * resolves while the escape menu happens to dim the background.
     *
     * Armed every frame rather than once: the swapchain can hand back a
     * different buffer each time, and the clear has to know which one. */
    arm_backbuffer_clear(backbuffer);

    if (!build_mirror_image(context, backbuffer, /* preferLiveEye */ false))
        return;

    context->CopyResource(backbuffer, g_xr.uiMirror);
}

/* Fills one eye's depth swapchain image and chains the description of it onto
   that eye's projection view. Returns false if there was nothing to send, in
   which case the eye goes up exactly as it did before depth existed.

   `source` is the depth to submit: the held depth for an AFR eye, whose colour
   is equally held, so the two always describe the same instant. */
/* Whether the game depth we have just been handed is still the shape the held
   depth textures were built for.

   CopyResource between different sizes does nothing and reports nothing - it
   returns void - so without this check a depth buffer that changed size
   would leave the hold containing whatever it contained before, and
   afrDepthHas would still call it good. The compositor would then be handed
   a stale depth map with full confidence, which is worse than being handed
   none. Changing an upscaler is exactly the thing that changes the size. */
/* Drops the depth layer so ensure_depth_swapchains can build it again at
   whatever size the game is rendering depth at NOW.
 *
   The holds are allocated once, from the first game depth buffer seen, and the
   size is then fixed for the session. If rgl rebuilds its targets at a
   different size - which changing an upscaler does, and which this session's
   log caught going from 2920x2940 to 1947x1960 - every CopyResource into the
   hold silently does nothing, depth_hold_matches starts refusing, and depth
   submission stops for good. The counter read "0 eye(s) submitted" for 101 of
   153 seconds because of exactly that, and the advice in the old warning -
   restart the mission - does not help, because g_depthAttempted survives it.

   Losing positional reprojection is not cosmetic: without depth the compositor
   can only correct a frame for rotation, so head TRANSLATION goes uncorrected
   and the world swims behind the head. That is the "floaty, delayed" feel, and
   it is worst exactly when the head is moving.

   So rebuild instead of giving up. Called from the render thread, which is the
   same thread that creates these. */
void depth_layer_release()
{
    g_depthLayerReady = false;
    g_depthAttempted  = false;
    g_depthUpscaling  = false;
    g_xr.depthFormat  = 0;

    depth_upscale_release();

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        EyeSwapchain& sc = g_xr.swapchains[eye];

        if (sc.depthHandle != XR_NULL_HANDLE)
        {
            xrDestroySwapchain(sc.depthHandle);
            sc.depthHandle = XR_NULL_HANDLE;
        }
        sc.depthImages.clear();

        for (ID3D11Texture2D** held : { &g_xr.afrDepthResult[eye], &g_xr.afrDepthStage[eye] })
        {
            if (*held != nullptr)
            {
                (*held)->Release();
                *held = nullptr;
            }
        }

        g_xr.afrDepthHas[eye]      = false;
        g_xr.afrDepthStageHas[eye] = false;
    }
}

bool depth_hold_matches(ID3D11Texture2D* gameDepth)
{
    if (g_xr.afrDepthStage[0] == nullptr)
        return false;

    D3D11_TEXTURE2D_DESC incoming = {}, hold = {};
    gameDepth->GetDesc(&incoming);
    g_xr.afrDepthStage[0]->GetDesc(&hold);

    if (incoming.Width == hold.Width && incoming.Height == hold.Height &&
        incoming.Format == hold.Format)
        return true;

    BVR_WARN("Depth layer: the game now renders depth at %ux%u fmt %d, but the "
             "held depth was built for %ux%u fmt %d - the render targets were "
             "rebuilt under us, which is what changing an upscaler does. "
             "Dropping the layer and rebuilding it at the new size; depth "
             "submission resumes on the next frame that binds a scene depth.",
             incoming.Width, incoming.Height, static_cast<int>(incoming.Format),
             hold.Width, hold.Height, static_cast<int>(hold.Format));

    /* Rebuild rather than stand down. Refusing left the compositor with no
       depth for the rest of the session - rotation-only reprojection, so head
       translation uncorrected - and told the player to restart the mission,
       which does not clear g_depthAttempted and therefore never helped. */
    depth_layer_release();
    return false;
}

/* Drops every held eye, so a new mission starts from real renders rather than
   inheriting whatever the last one left in the hold textures. Render thread
   only - see XrState::afrForget.

   The textures themselves are kept and simply marked empty. They are the
   right size and format for the next mission too, and freeing them here would
   mean reallocating during the first frames of a scene, which is the one part
   of this pipeline with a history of crashing. */
void forget_afr_holds()
{
    for (uint32_t e = 0; e < kEyeCount; ++e)
    {
        g_xr.afrHasResult[e] = false;
        g_xr.afrStageHas[e]  = false;
        g_xr.afrStageGen[e]  = -1;
        g_xr.afwRealHas[e]   = false;
        g_xr.afwSynthHas[e]  = false;
        g_xr.afrDepthHas[e]      = false;
        g_xr.afrDepthStageHas[e] = false;
    }
}

/* Says so once a second rather than once a frame. A mono eye is a fault that
   the player can see but not diagnose, so it has to reach the log - and at
   ninety frames a second it has to do that without burying everything else. */
void note_mono_fallback(uint32_t eye)
{
    static uint64_t lastMs = 0;
    static uint32_t count = 0;

    ++count;
    const uint64_t now = GetTickCount64();
    if (lastMs != 0 && now - lastMs < 1000)
        return;

    lastMs = now;
    BVR_WARN("Eye %u had no image of its own on %u frame(s) this second and "
             "was given the other eye's instead - the view is correct but FLAT, "
             "with no stereo. Something upstream is not filling that eye: the "
             "AFW line above says whether the warp is declining, and with "
             "afw_pin_source_eye = 1 a declining warp leaves this eye empty "
             "because AFR alone can only fill the eye it renders.", eye, count);
    count = 0;
}

bool submit_depth(ID3D11DeviceContext* context, uint32_t eye,
                  ID3D11Texture2D* source,
                  XrCompositionLayerProjectionView& view,
                  XrCompositionLayerDepthInfoKHR& info)
{
    if (!g_depthLayerReady || source == nullptr)
        return false;

    /* The planes the depth was rendered with. Without them the values are
       meaningless numbers between 0 and 1 and the compositor would be guessing
       at the scale of the correction - so no parameters means no depth. */
    const float zNear = afw_z_near();
    const float zFar  = afw_z_far();
    if (!(zNear > 0.0f) || !(zFar > zNear))
        return false;

    EyeSwapchain& sc = g_xr.swapchains[eye];

    uint32_t imageIndex = 0;
    XrSwapchainImageAcquireInfo acquire{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
    if (XR_FAILED(xrAcquireSwapchainImage(sc.depthHandle, &acquire, &imageIndex)))
        return false;

    XrSwapchainImageWaitInfo wait{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
    wait.timeout = XR_INFINITE_DURATION;
    if (XR_FAILED(xrWaitSwapchainImage(sc.depthHandle, &wait)))
    {
        /* Acquired but not waited: it still has to be released, or the
           swapchain runs out of images and the next acquire blocks forever. */
        XrSwapchainImageReleaseInfo release{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
        xrReleaseSwapchainImage(sc.depthHandle, &release);
        return false;
    }

    /* Resampled to the eye's size first when an upscaler is in the way, so the
       depth rect submitted below is identical to the colour rect rather than a
       smaller one the runtime has to interpret. depth_upscale owns the
       intermediate and it is consumed immediately, here, before anything else
       can ask for another. */
    ID3D11Texture2D* toCopy = source;

    if (g_depthUpscaling)
    {
        toCopy = depth_upscale(engine_device(), context, source,
                               sc.depthWidth, sc.depthHeight);

        if (toCopy == nullptr)
        {
            /* The image was acquired, so it has to be released whatever
               happens, or the swapchain runs dry and the next acquire blocks
               for ever. */
            XrSwapchainImageReleaseInfo bail{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
            xrReleaseSwapchainImage(sc.depthHandle, &bail);

            if (depth_upscale_failed() && g_depthLayerReady)
            {
                g_depthLayerReady = false;
                BVR_WARN("Depth layer: the resample failed, so depth submission is "
                         "off for the rest of the session. The eyes still go up - "
                         "with a pose only, which is what AFR did before the depth "
                         "layer existed.");
            }
            return false;
        }
    }

    context->CopyResource(sc.depthImages[imageIndex].texture, toCopy);

    XrSwapchainImageReleaseInfo release{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
    xrReleaseSwapchainImage(sc.depthHandle, &release);

    info.type = XR_TYPE_COMPOSITION_LAYER_DEPTH_INFO_KHR;
    info.next = nullptr;
    info.subImage.swapchain = sc.depthHandle;
    info.subImage.imageArrayIndex = 0;
    info.subImage.imageRect.offset = { 0, 0 };
    /* The DEPTH image's own extent, which on the resampled path is the eye's -
       so this rect and the colour rect are identical and there is nothing left
       for a runtime to interpret. Where the game already renders depth at the
       eye's size the two are equal anyway. */
    info.subImage.imageRect.extent = {
        static_cast<int32_t>(sc.depthWidth), static_cast<int32_t>(sc.depthHeight)
    };

    /* minDepth/maxDepth are the range of values stored in the image; nearZ and
       farZ are the world distances those two ends MEAN. Under reversed-Z the
       stored 0 is the far plane and 1 is the near one, so the pair is swapped -
       and nearZ > farZ is exactly how the spec says to express that, not an
       error to be normalised away. Getting this backwards does not produce
       nothing; it produces a correction applied the wrong way round, which is
       why the direction comes from the same probe the warp uses. */
    info.minDepth = 0.0f;
    info.maxDepth = 1.0f;

    if (afw_depth_is_reversed())
    {
        info.nearZ = zFar;
        info.farZ  = zNear;
    }
    else
    {
        info.nearZ = zNear;
        info.farZ  = zFar;
    }

    view.next = &info;
    ++g_xr.depthSubmitted;
    return true;
}

bool ensure_afr_results(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& modelDesc)
{
    if (g_xr.afrResult[0] != nullptr && g_xr.afrResult[1] != nullptr &&
        g_xr.afrStage[0]  != nullptr && g_xr.afrStage[1]  != nullptr)
        return true;

    D3D11_TEXTURE2D_DESC desc = modelDesc;
    desc.Usage = D3D11_USAGE_DEFAULT;

    /* RENDER_TARGET as well as SHADER_RESOURCE.
     *
       These are staging copies and every existing path fills them with
       CopyResource, which needs no bind flag at all. The second eye does not:
       it is SCALED in from a duplicate at the upscaler's internal size, and a
       scale means a draw, and a draw means a render target view.
     *
       Without this CreateRenderTargetView refuses the texture, the blit returns
       false, and afr_capture quietly falls back to copying the engine's own eye
       - which looks exactly like the second eye not working at all. It cost a
       round trip to find, because the failure had no voice. */
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;

    /* AFW writes the synthesised eye through a compute shader, which needs an
       unordered-access view, and a TYPED uav is not allowed on B8G8R8A8_UNORM.
       The way round it is the TYPELESS member of the same family: the texture
       is created typeless, the uav names the UNORM interpretation, and
       CopyResource still works in both directions because same-family copies
       are legal. Only done when AFW is allowed at all, so a run with it off
       allocates exactly what it always did. */
    if (afw_allowed())
    {
        const DXGI_FORMAT typeless = typeless_family_of(modelDesc.Format);
        if (typeless != DXGI_FORMAT_UNKNOWN)
        {
            desc.Format = typeless;
            desc.BindFlags |= D3D11_BIND_UNORDERED_ACCESS;
        }
        else
        {
            BVR_WARN("AFW: eye format %d has no typeless family this knows, so the "
                     "synthesised eye cannot be written. AFW will stay off.",
                     static_cast<int>(modelDesc.Format));
        }
    }
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;
    desc.MipLevels = 1;
    desc.ArraySize = 1;

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        if (g_xr.afrResult[eye] == nullptr &&
            FAILED(device->CreateTexture2D(&desc, nullptr, &g_xr.afrResult[eye])))
        {
            BVR_ERR("Could not create the AFR hold texture for eye %u.", eye);
            return false;
        }

        /* The staging half of the pair. Same description in every respect -
           promotion is a POINTER SWAP, so the two must be interchangeable. */
        if (g_xr.afrStage[eye] == nullptr &&
            FAILED(device->CreateTexture2D(&desc, nullptr, &g_xr.afrStage[eye])))
        {
            BVR_ERR("Could not create the AFR staging texture for eye %u.", eye);
            return false;
        }

        /* Only AFW needs this one, and only AFW pays for it. Same description
           again so a CopyResource between them is legal. */
        if (afw_allowed() && g_xr.afwReal[eye] == nullptr &&
            FAILED(device->CreateTexture2D(&desc, nullptr, &g_xr.afwReal[eye])))
        {
            BVR_WARN("AFW: could not create the real-render keep for eye %u, so "
                     "disocclusions will be filled by stretching the background "
                     "instead of from this eye's own last render.", eye);
        }

        /* The synthesised keep, for when the pin means there is no real render
           of this eye to fall back on. Same description again, same reason. */
        if (afw_allowed() && g_xr.afwSynth[eye] == nullptr &&
            FAILED(device->CreateTexture2D(&desc, nullptr, &g_xr.afwSynth[eye])))
        {
            BVR_WARN("AFW: could not create the synthesised-frame keep for eye "
                     "%u, so with a pinned source eye disocclusions fall back to "
                     "stretching the background.", eye);
        }
    }

    BVR_INFO("AFR hold textures created: %ux%u fmt %d.",
             desc.Width, desc.Height, static_cast<int>(desc.Format));
    return true;
}


/* --------------------------------------------------------------------------
 * PAIRING: making the two eyes on screen come from ONE world state.
 *
 * WHY THE OBVIOUS VERSION OF THIS IS WRONG
 *
 * "Freeze the simulation on every other frame so both eyes of a pair share a
 * tick" is the standard description of Skip Tick, and taken literally it does
 * not work here - because AFR submits BOTH eyes on EVERY frame, from held
 * images. So the pair actually on screen at Present N is (frame N, frame N-1),
 * and at Present N+1 it is (frame N+1, frame N). EVERY adjacent pair of frames
 * is displayed as a stereo pair at some point.
 *
 * Freeze alternate frames and the world states run S0 S0 S1 S1 S2 S2, so the
 * displayed pairs run consistent, INCONSISTENT, consistent, INCONSISTENT. Half
 * the pairs fuse and half do not, alternating at half the frame rate. That is
 * a very good way to make things worse: a steady error is something the visual
 * system settles into, and an error that switches on and off sixty times a
 * second is not.
 *
 * WHAT ACTUALLY MAKES THE PAIR CONSISTENT
 *
 * Freezing is necessary but it is not sufficient. The missing half is to stop
 * putting a fresh eye on screen the moment it arrives. An eye is captured into
 * a STAGING slot, and a pair is promoted to the screen only when both of its
 * halves were rendered from the same simulation tick:
 *
 *   F0  gen G0  eye0 -> stage.  stage1 is older     -> hold
 *   F1  frozen  eye1 -> stage.  both G0             -> PROMOTE
 *   F2  gen G1  eye0 -> stage.  stage1 still G0     -> hold, screen keeps G0
 *   F3  frozen  eye1 -> stage.  both G1             -> PROMOTE
 *
 * The displayed pair changes every second frame and is ALWAYS internally
 * consistent, while the compositor keeps reprojecting it for head motion and
 * for the turn at the full frame rate. That is the whole trade: world updates
 * at half rate, stereo agrees always.
 *
 * It also makes the freeze PHASE irrelevant, which the first version of this
 * worried about at some length and could not actually measure. Promotion keys
 * off the generations matching, so whichever frames are frozen, the adjacent
 * pair that shares a tick is the one that goes up. sync_sequential_phase is
 * kept only because removing a config line is not worth a rebuild.
 *
 * Promotion is a POINTER SWAP. The pair coming off screen becomes the staging
 * buffer for the pair being assembled next, which is exactly what it should be
 * and costs nothing.
 * ------------------------------------------------------------------------ */

/* LATE LATCH LABELS, SHARED BY EVERY CAPTURE PATH (TEST 169).
 *
   latch_take: consume the slot, AFR's original semantics exactly - exchange,
   then read the orientation.
   latch_peek / latch_consume: for AFW, which may fail its warp and fall back to
   afr_capture in the same frame. It must not eat the latch before it knows it
   is the path that labels this image. */
/* ONE SLOT PER EYE, AND THAT IS DELIBERATE (TEST 177).
 *
   TEST 176 put a FIFO here on the theory that AFW's next build overran the slot
   before the capture read it. The overwrite counter it added reads 0 in steady
   AFW, so there was no overrun - and the FIFO then kept the surplus builds of a
   mode switch as a standing stale entry, labelling every later AFW frame a
   frame or two old: floating until a battle restart. The slot always holds the
   newest build, so a switch disturbs it for a frame and no longer. */
bool latch_take(uint32_t eye, XrQuaternionf* out)
{
    if (!g_xr.latchHas[eye].exchange(false, std::memory_order_acq_rel))
        return false;
    const BvrQuat& q = g_xr.latchOri[eye];
    *out = XrQuaternionf{ q.x, q.y, q.z, q.w };
    g_xr.latchCaptures.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool latch_peek(uint32_t eye, XrQuaternionf* out)
{
    if (!g_xr.latchHas[eye].load(std::memory_order_acquire))
        return false;
    const BvrQuat& q = g_xr.latchOri[eye];
    *out = XrQuaternionf{ q.x, q.y, q.z, q.w };
    return true;
}

void latch_consume(uint32_t eye)
{
    if (g_xr.latchHas[eye].exchange(false, std::memory_order_acq_rel))
        g_xr.latchCaptures.fetch_add(1, std::memory_order_relaxed);
}

void afr_promote_eye(uint32_t eye)
{
    ID3D11Texture2D* tmp = g_xr.afrResult[eye];
    g_xr.afrResult[eye] = g_xr.afrStage[eye];
    g_xr.afrStage[eye] = tmp;

    g_xr.afrPose[eye]    = g_xr.afrStagePose[eye];
    g_xr.afrFov[eye]     = g_xr.afrStageFov[eye];
    g_xr.afrBodyYaw[eye] = g_xr.afrStageBodyYaw[eye];
    for (int c = 0; c < 3; ++c)
        g_xr.afrBodyPos[eye][c] = g_xr.afrStageBodyPos[eye][c];

    /* The depth rides with the colour, by the same pointer swap and in the same
       breath. Promoting one without the other is the one way to end up
       correcting a picture with a disparity map from a different frame. */
    if (g_depthLayerReady)
    {
        ID3D11Texture2D* dtmp = g_xr.afrDepthResult[eye];
        g_xr.afrDepthResult[eye] = g_xr.afrDepthStage[eye];
        g_xr.afrDepthStage[eye] = dtmp;

        g_xr.afrDepthHas[eye] = g_xr.afrDepthStageHas[eye];
        g_xr.afrDepthStageHas[eye] = false;
    }

    g_xr.afrHasResult[eye] = true;
    g_xr.afrLastCaptureMs[eye] = GetTickCount64();
    g_xr.afrLastPromotedEye = static_cast<int>(eye);
    g_xr.afrStageHas[eye] = false;

    /* Full AFR reads this to decide whether the frame it is in the middle of
       should END or be handed back for the other eye. Set on EVERY promotion,
       not only on a completed pair: a forced single-eye promotion means the
       pairing has given up on this frame, and a frame that never ends because
       its partner never came is far worse than a frame that goes up half fresh.
       See XrState::afrPromotedThisPresent. */
    g_xr.afrPromotedThisPresent = true;
}

void afr_promote_pair()
{
    afr_promote_eye(0);
    afr_promote_eye(1);

    g_xr.afrPromotions++;
    g_xr.afrFramesSincePromotion = 0;

    /* A real pair completed, so whatever run of forced promotions was building
       up was a hiccup rather than a deadlock. */
    g_xr.afrForcedRunLength = 0;
}

/* Captures the eye the engine has just drawn, and promotes if that completes a
   consistent pair. */
void afr_capture(ID3D11DeviceContext* context, ID3D11Texture2D* source,
                 const XrView* drawn, uint32_t eye)
{
    /* THE SECOND EYE, FROM THE DUPLICATED DRAWS RATHER THAN FROM THE ENGINE.
     *
       Everything else here stages the eye the engine just drew. When the second
       draw is running, eye 1 has ALSO been rendered - into a target of ours,
       from the other eye's matrices, in the same engine frame - and this is
       where that becomes visible instead of merely counted.
     *
       It is the proof, not the destination. The duplicate carries raw scene
       colour at the upscaler's internal size, so the right eye will look wrong:
       untonemapped, soft, missing every post pass. What it establishes is
       whether the GEOMETRY is right and offset by the IPD, which no counter can
       answer.
     *
       Falls straight through to the ordinary copy when the duplicate is not
       ready, so a failure here is the behaviour this arrived with. */
    context->CopyResource(g_xr.afrStage[eye], source);

    /* THE OTHER EYE, FROM THE DUPLICATED DRAWS, IN THE SAME ENGINE FRAME.
     *
       The duplicate always holds the eye the engine did NOT just draw - the
       publication names the current eye and the second draw is built from its
       opposite - so it belongs in stage 1-eye, never in stage 1.
     *
       Hardcoding eye 1 was wrong and full AFR is where it shows. There the
       engine alternates every frame, so by the time eye 1 was captured the
       duplicate held eye 0's view, and blitting it into stage 1 would have put
       the left eye's image into the right. Under plain AFR, where eye 1 is
       drawn every other frame, it happened to line up half the time.
     *
       Written AFTER the engine's own copy above, so a failed blit leaves a
       correct frame behind rather than a hole. */
    const uint32_t other = 1u - eye;
    if (bvr::vp_second_eye_ready() &&
        g_xr.afrStage[other] != nullptr &&
        bvr::vp_blit_second_eye(context, g_xr.afrStage[other]))
    {
        g_xr.secondEyeStaged = true;

        static bool logged = false;
        if (!logged)
        {
            logged = true;
            BVR_INFO("Both eyes now come from ONE engine frame: the eye the engine drew, "
                     "and the other one from the duplicated draws. The second will look "
                     "raw - no tonemap, no upscale, no post - and that is expected. What "
                     "matters is whether it is the battlefield from slightly to the side "
                     "and whether the two fuse.");
        }
    }

    /* NATIVE STEREO: the other eye is not a raw duplicate to be blitted - it is
       a finished image. Every view-dependent operation of this frame ran a
       second time into twins of whatever it wrote, so the twin of the texture
       the engine composited into holds that eye lit, tonemapped and post-
       processed, from its own viewpoint, out of the same engine frame.
     *
       Checked after the draw-duplication path above so the two can never both
       claim the stage, and skipped silently until the first frame has reached
       the composite. */
    if (!g_xr.secondEyeStaged && bvr::stereo_active() && g_xr.afrStage[other] != nullptr)
    {
        /* TEST 164 NEVER GOT HERE. This block sat INSIDE the braces of the
           draw-duplication branch above, which had already set secondEyeStaged
           - so it could only run when it would immediately skip itself. Every
           frame of that test showed eye 0 in both eyes: "Eye 1 had no image of
           its own on 28 frame(s)", every second, and no depth at all.
         *
           And it is the twin of what the engine DREW, not of eye texture 0:
           the duplicate always renders the eye opposite the published one. */
        ID3D11Texture2D* eye1 = bvr::stereo_twin_texture(source);
        if (eye1 != nullptr)
        {
            context->CopyResource(g_xr.afrStage[other], eye1);
            eye1->Release();
            g_xr.secondEyeStaged = true;

            static bool loggedNative = false;
            if (!loggedNative)
            {
                loggedNative = true;
                BVR_INFO("Native stereo: both eyes are now real renders of ONE engine "
                         "frame - the engine's own eye, and the second from the "
                         "duplicated pipeline, complete with lighting and post.");
            }
        }
    }

    /* Once per capture, never per submitted frame - the copy above has just
       overwritten the whole texture, so nothing accumulates. */
    reticle_draw(engine_device(), context, g_xr.afrStage[eye],
                 static_cast<int>(eye), afw_focal_px(), afw_baseline());

    /* The depth the engine drew THIS image with, staged beside it. Taken here
       rather than at submit time for the reason the whole staging mechanism
       exists: by the time this eye goes up it may be a frame old, and the
       game's depth buffer will by then describe the OTHER eye. */
    if (g_depthLayerReady)
    {
        ID3D11Texture2D* gameDepth = eye_depth_texture();
        if (gameDepth != nullptr && depth_hold_matches(gameDepth))
        {
            context->CopyResource(g_xr.afrDepthStage[eye], gameDepth);
            g_xr.afrDepthStageHas[eye] = true;
        }
        else
        {
            /* The buffer went away - a scene change, usually. Say so by
               omission rather than by submitting the last frame's depth. */
            g_xr.afrDepthStageHas[eye] = false;
        }
    }

    /* The pose it was RENDERED from when managed has published one - see
       XrState::usedPose. Labelling an image with a pose it was not drawn from is
       what makes the compositor swim. */
    g_xr.afrStagePose[eye] = g_xr.usedValid.load(std::memory_order_acquire)
                                 ? g_xr.usedPose[eye]
                                 : drawn[eye].pose;

    /* LATE LATCH: this image was built from a fresher orientation than the one
       managed reported, because the latch rotated the engine's camera to it at
       the matrix builder. Labelling it with the used pose would make the
       compositor rotate it a second time - floating, by exactly the latch. */
    XrQuaternionf latchedQ{};
    bool latchedHere = false;
    if (bvr::stereo_active())
    {
        /* NATIVE STEREO PAIRS BY CONTENT, LIKE AFW (TEST 183). The engine draws
           the same eye every frame, so - exactly as under pinned AFW before
           TEST 178 - the slot already holds the NEXT frame's build when this
           one is captured. Every frame went up labelled a frame of head
           rotation ahead of its pixels, and the compositor turned it by the
           difference: the old floating, in native stereo ("the latency feels
           the same as the floating"). The build is found from the frame's own
           per_framef camera rows instead; the slot is only the fallback. */
        float q[4] = {};
        if (bvr::late_latch_drawn_orientation(eye, q))
        {
            latchedQ = XrQuaternionf{ q[0], q[1], q[2], q[3] };
            latchedHere = true;
            latch_consume(eye);
        }
        else
        {
            latchedHere = latch_take(eye, &latchedQ);
        }
    }
    else
    {
        latchedHere = latch_take(eye, &latchedQ);
    }
    if (latchedHere)
        g_xr.afrStagePose[eye].orientation = latchedQ;

    g_xr.afrStageFov[eye]  = drawn[eye].fov;

    /* The game camera's own yaw at this instant. Published by the tick that
       rendered this very image, so no history is needed here - unlike the XR
       views, which are located at Present and therefore run a generation
       ahead. See turn_corrected_pose and content_views respectively. */
    g_xr.afrStageBodyYaw[eye] = g_xr.bodyYaw.load(std::memory_order_acquire);
    g_xr.afrStageBodyPos[eye][0] = g_xr.bodyPosX.load(std::memory_order_acquire);
    g_xr.afrStageBodyPos[eye][1] = g_xr.bodyPosY.load(std::memory_order_acquire);
    g_xr.afrStageBodyPos[eye][2] = g_xr.bodyPosZ.load(std::memory_order_acquire);

    const int32_t gen = g_xr.simGen.load(std::memory_order_acquire);
    g_xr.afrStageGen[eye] = gen;
    g_xr.afrStageHas[eye] = true;

    /* THE DUPLICATE'S EYE NEEDS ITS LABEL, NOT JUST ITS PIXELS.
     *
       The blit above fills afrStage[other] with a real render of the other eye,
       and that is where the first run through the headset stopped: nothing
       marked the stage as PRESENT, so the pair never completed, afrResult[other]
       was never promoted, and the submit fell through to its last resort - "the
       other eye, rather than a flat colour". Both eyes then showed eye 0, which
       is exactly the reported "render looks mono, no raw image in either eye".
       The blit was working the whole time and its output was never displayed.
     *
       So the other eye is labelled here the same way the drawn one is, from the
       located view for ITS index. The pose and the frustum both have to be its
       own: they are what the compositor reprojects against, and handing it eye
       0's pose for eye 1's pixels would put the right image at the left eye's
       position - stereo with the disparity cancelled back out.
     *
       Same generation as the drawn eye, because it IS the same frame. That is
       the whole claim of this path, and it makes the pair complete immediately
       under the existing rule rather than needing one of its own. */
    if (g_xr.secondEyeStaged)
    {
        const uint32_t other = 1u - eye;

        g_xr.afrStagePose[other] = drawn[other].pose;
        g_xr.afrStageFov[other]  = drawn[other].fov;

        /* Native stereo: the same moment as the drawn eye's label (TEST 183). The
           drawn eye carries the pose the frame was RENDERED from; the located view
           is a generation newer, so the pair went up with its two eyes labelled
           from different instants - the other eye's position swam by a frame of
           head translation against its pixels. */
        if (bvr::stereo_active() && g_xr.usedValid.load(std::memory_order_acquire))
            g_xr.afrStagePose[other] = g_xr.usedPose[other];

        /* NATIVE STEREO: the other eye is the latched engine eye moved sideways
           by stereo_dup - a pure translation, same orientation. So it carries
           the same latched orientation as the drawn eye, or the compositor would
           rotate one eye of the pair and not the other. */
        if (latchedHere)
            g_xr.afrStagePose[other].orientation = latchedQ;

        g_xr.afrStageBodyYaw[other] = g_xr.afrStageBodyYaw[eye];
        for (int c = 0; c < 3; ++c)
            g_xr.afrStageBodyPos[other][c] = g_xr.afrStageBodyPos[eye][c];

        g_xr.afrStageGen[other] = gen;
        g_xr.afrStageHas[other] = true;
        g_xr.secondEyeStaged = false;

        /* ITS OWN DEPTH, NOT THE DRAWN EYE'S. Native stereo renders the other
           eye's scene depth too - the twin of the game's depth buffer - and the
           compositor reprojects that eye against it. Submitting none (as before)
           left the right eye reprojected for rotation only; submitting eye 0's
           would put every surface at the wrong distance for that eye. */
        if (g_depthLayerReady && g_xr.afrDepthStage[other] != nullptr)
        {
            bool has = false;
            if (bvr::stereo_active())
            {
                ID3D11Texture2D* gameDepth = eye_depth_texture();
                if (gameDepth != nullptr && depth_hold_matches(gameDepth))
                {
                    ID3D11Texture2D* d1 = bvr::stereo_twin_texture(gameDepth);
                    if (d1 != nullptr)
                    {
                        context->CopyResource(g_xr.afrDepthStage[other], d1);
                        d1->Release();
                        has = true;
                    }
                }
            }
            g_xr.afrDepthStageHas[other] = has;
        }

        /* PROMOTED AS A PAIR, HERE, AND NOTHING BELOW RUNS.
         *
           Both stages are full and they came from one engine frame, so this IS
           a complete pair and there is nothing left to wait for.
         *
           Going on would have thrown it away. With sync_sequential off the
           generation is negative, and the next branch reads that as "no pairing
           in use" and promotes the DRAWN EYE ALONE before returning. The second
           eye was staged, labelled, and then left behind every frame -
           afrResult[1] never updated, and the submit filled eye 1 from eye 0
           with its mono fallback. That is the "Eye 1 had no image of its own on
           90 frame(s)" warning, ninety times a second, and on screen it is a
           flat picture with no stereo at all. */
        afr_promote_pair();
        return;
    }

    /* No generation to pair on - sync sequential is off, or the managed side
       is not publishing one. Straight to the screen, which is bit for bit the
       behaviour this had before pairing existed and is exactly what
       sync_sequential = 0 restores. */
    if (gen < 0 || g_xr.afrPairingAbandoned)
    {
        afr_promote_eye(eye);
        return;
    }

    /* FULL AFR PAIRS BY EYE, NOT BY GENERATION.
     *
       Ordinary AFR has to match generations because it cannot otherwise know
       whether the two eyes it holds came from one world state - its pair is
       assembled across display frames and the sim runs freely between them.
       Full AFR knows by construction: the freeze is mandatory in that mode and
       the pair is drawn inside one display frame, so both halves ARE one tick
       whenever the alternation is behaving.
     *
       Requiring the generations to agree as well turned out to be the mode's
       one failure point, and the log named it. A Present that lands without
       MissionScreen.OnFrameTick advancing the eye gives two captures in a row
       the same label - "AFR parity: 8 did NOT alternate" - and the second
       restages the SAME eye against a NEW generation. Its partner still carries
       the old one, the generations can now never match, and the pair that was
       one frame from completing is dead. The defer cap then ends the frame with
       whatever partner was left over, which is the previous pair's: "held-image
       age 31 ms" against 0 ms whenever parity was clean, in exactly the seconds
       the parity counter was non-zero.
     *
       A fresh eye submitted beside a 31 ms-old one is not a stereo pair. The
       baseline it carries is the IPD plus however far the camera travelled in
       those 31 ms, which while turning a horse is the larger of the two - so
       the depth reads wrong, which is how it was reported.
     *
       Pairing by eye makes the repeat harmless. The duplicate capture simply
       overwrites its own stage, the partner's stage is untouched, and the pair
       completes on the next frame with the LATEST render of each eye - which is
       also the pair the camera latch built, since that re-latched on the same
       generation change. One extra deferred frame instead of a stale eye. */
    const bool pairComplete =
        g_xr.fullAfr.load(std::memory_order_acquire)
            ? (g_xr.afrStageHas[0] && g_xr.afrStageHas[1])
            : (g_xr.afrStageHas[0] && g_xr.afrStageHas[1] &&
               g_xr.afrStageGen[0] == g_xr.afrStageGen[1]);

    if (pairComplete)
    {
        afr_promote_pair();
        return;
    }

    g_xr.afrHeldForPairing++;

    /* THE SAFETY, and it is not hypothetical. Pairing depends on the world
       actually being frozen on alternate frames. If Mission.OnTick is not
       being patched, or the mission is paused, or anything else stops the
       generations from ever lining up, then a strict reading of the rule is
       "never promote again" - which shows a frozen picture in the headset
       while the game plays on underneath it. Far worse than the artefact this
       is here to fix, and indistinguishable in the moment from a hang.
       So: after a few frames with no pair, put the fresh eye up anyway. */
    if (++g_xr.afrFramesSincePromotion > 8)
    {
        afr_promote_eye(eye);
        g_xr.afrFramesSincePromotion = 0;
        g_xr.afrForcedPromotions++;

        /* And if that keeps happening, STOP PAIRING ALTOGETHER.
         *
         * The valve alone was not enough, and a run proved it. Forcing promotes
         * ONE eye, so a pairing that can never complete leaves each eye
         * refreshing about five times a second while the frame rate reads 90.
         * That is unplayable, and from inside the headset it is indistinguishable
         * from the mod having broken - the player has no way to know a comfort
         * feature is quietly starving the image.
         *
         * So the valve now has an end. Twenty forced promotions with nothing
         * promoted normally means the generations are not going to line up,
         * whatever the reason, and the right answer is plain AFR rather than a
         * degraded imitation of it. Latched for the session and said out loud,
         * because it means something upstream is wrong and quietly limping is
         * how that goes unnoticed for a week. */
        if (g_xr.afrForcedRunLength++ >= 20 && !g_xr.afrPairingAbandoned)
        {
            g_xr.afrPairingAbandoned = true;
            g_xr.simGen.store(-1, std::memory_order_release);

            BVR_WARN("AFR pairing has been abandoned for this session: 20 forced "
                     "promotions in a row with no pair completed. Each eye was "
                     "refreshing about five times a second, which is worse than "
                     "the artefact pairing exists to fix. Running plain AFR from "
                     "here. The generations never matched - something is "
                     "publishing them wrong.");
        }
    }
}

/* --------------------------------------------------------------------------
 * AFW's capture: one render in, a complete stereo pair out.
 *
 * Note what is NOT here. No staging, no generation, no waiting for a partner,
 * no held image and nothing to hold it from. The pair is complete the moment it
 * is made, because the second eye is manufactured from the first rather than
 * remembered from a frame ago - so the entire pairing apparatus that TESTs 48
 * and 49 exist to run is simply not on this path.
 *
 * Both eyes are tagged with the poses from the SAME locate, which is the point:
 * they are a stereo pair of one instant and the compositor can reproject them
 * as a unit.
 *
 * Returns false if the warp could not run, and the caller then falls back to
 * AFR for that frame. A missing depth buffer on the first frames of a mission
 * is normal and self-correcting; it should not black out an eye.
 * ------------------------------------------------------------------------ */
/* Whether the hybrid is the SELECTED MODE, not a config default.
 *
 * This used to read afw_still_afr, which defaulted to on - so choosing "AFW"
 * in the menu silently ran the hybrid, and there was no way to ask for the
 * warp on its own and see what it actually does. Three modes were offered and
 * two of them were the same one. The menu now names four modes and this
 * follows whichever was picked, so AFW means AFW and the hybrid means the
 * hybrid. */
bool StillAfrEnabled()
{
    return g_xr.afwHybrid.load(std::memory_order_acquire);
}

/* How far the head may travel, in metres per frame, before a held real eye is
   considered stale.
 *
 * THE FIRST VALUE WAS FAR TOO GENEROUS AND IT SHOWED AS JUDDER.
 *
 * 1.5 mm with a doubled release is 3 mm per frame, which at 90 fps is 27 cm/s -
 * ordinary head movement, all of it. So the hybrid sat in AFR through exactly
 * the motion AFR is worst at, and AFR's characteristic fault is that each eye
 * alternates between fresh and a frame stale. That is the "movement is not
 * smooth like AFW" - it was AFR's judder, reintroduced by a threshold that
 * called moving heads still.
 *
 * The arithmetic that sets it: a held eye is displaced by focal * d / z, so one
 * pixel at a metre with a 955 px focal length is about 1.05 mm. Half a
 * millimetre is therefore comfortably sub-pixel out to arm's length, and that
 * is the only regime where "exact" is a true claim. Anything faster belongs to
 * the warp, which is time-correct by construction.
 *
 * 0.5 mm/frame is 4.5 cm/s - slower than head sway while standing and looking
 * at something, which is the case this exists for, and slower than any movement
 * a player would call movement. */
float StillAfrThreshold()
{
    static float cached = -1.0f;
    if (cached < 0.0f)
        cached = static_cast<float>(config_float("afw_still_threshold_m", 0.0005f));
    return cached;
}

/* ARE THE TWO EYES ACTUALLY RENDERED FROM TWO DIFFERENT PLACES?
 *
   One eye is reported perfect and the other doubled, and the warp is running on
   every frame, so the difference is not the warp declining. The remaining
   asymmetry that pinning would have hidden completely is the CAMERA: while a
   single eye is rendered it does not matter whether the engine moves the camera
   between eyes, because it is never asked to. The moment the eyes alternate, it
   does.

   If the engine camera does not actually move by the interpupillary distance
   between one frame and the next, then the "real" render of one eye is taken
   from the other eye's position. That eye is then wrong by a full baseline
   while the eye the camera happens to sit at stays right - one eye perfect, one
   eye doubled, which is the report.

   So measure it rather than reason about it: the two poses the eyes were really
   rendered from, how far apart they are, and what the warp believes the baseline
   to be. Those three numbers separate "the camera is not moving" from "the
   camera moves but the warp disagrees about how far". */
void afw_eye_census(uint32_t srcEye)
{
    static uint64_t lastMs = 0;
    static uint32_t srcCount[kEyeCount] = { 0, 0 };

    if (srcEye < kEyeCount)
        ++srcCount[srcEye];

    const uint64_t now = GetTickCount64();
    if (lastMs == 0) { lastMs = now; return; }
    if (now - lastMs < 1000) return;
    lastMs = now;

    float sep = -1.0f;
    if (g_xr.afwRealHas[0] && g_xr.afwRealHas[1])
    {
        const XrVector3f& a = g_xr.afwRealPose[0].position;
        const XrVector3f& b = g_xr.afwRealPose[1].position;
        const float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        sep = std::sqrt(dx * dx + dy * dy + dz * dz);
    }

    BVR_INFO("AFW eye census: eye 0 was the source on %u frame(s) and eye 1 on "
             "%u this second. The two real renders were taken %.1f mm apart, and "
             "the warp is shifting by a baseline of %.1f mm. Those last two "
             "should match: if the separation is ~0 the engine camera is not "
             "moving between eyes and one eye is being rendered from the other's "
             "position.",
             srcCount[0], srcCount[1],
             sep >= 0.0f ? sep * 1000.0f : -1.0f,
             afw_baseline() * 1000.0f);

    srcCount[0] = 0;
    srcCount[1] = 0;
}

/* Whether a disocclusion may be filled from the previous SYNTHESISED frame when
   there is no real render of that eye to use.
 *
   On by default because the thing it replaces is worse: with a pinned source eye
   the alternative is not a real render, it is a sideways stretch of the
   background across a hole as wide as the disparity step at a near object's
   edge.

   Off is worth trying if it shows as trails. This is temporal feedback - a hole
   filled from a frame that may itself have been filled - and with a still head
   the hole does not move, so it settles on whatever it already had rather than
   improving. It is head movement that slides the holes apart and makes the
   previous frame carry real geometry into them. */
bool SynthFillEnabled()
{
    static int cached = -1;
    if (cached < 0)
        cached = config_float("afw_prev_from_synth", 1.0f) > 0.5f ? 1 : 0;
    return cached != 0;
}

bool afw_capture(ID3D11DeviceContext* context, ID3D11Texture2D* source,
                 const XrView* drawn, uint32_t eye)
{
    const uint32_t other = 1u - eye;

    /* LATE LATCH: the engine drew `source` from this orientation. Peeked, not
       taken - if the warp fails below, afr_capture labels this image instead and
       must still find it. Consumed only on the two successful returns. The
       synthesised eye is a warp of this one, so it shares the orientation. */
    XrQuaternionf latchedQ{};
    bool latched = false;
    {
        /* The build THIS frame was drawn from, identified by the frame's own
           camera upload (TEST 178). The slot is only the fallback: under AFW it
           holds the NEXT frame's build by the time this one is captured. */
        float q[4] = {};
        if (bvr::late_latch_drawn_orientation(eye, q))
        {
            latchedQ = XrQuaternionf{ q[0], q[1], q[2], q[3] };
            latched  = true;
        }
        else
        {
            latched = latch_peek(eye, &latchedQ);
        }
    }

    /* The synthesised eye's OWN last real render, or null until it has one.
       Passed rather than looked up inside the warp so the ownership of these
       textures stays in one place. */
    ID3D11Texture2D* prevReal = g_xr.afwRealHas[other] ? g_xr.afwReal[other] : nullptr;

    /* What the warp fills disocclusions from. A real render of this eye when
       there is one, and otherwise the last frame it was synthesised into.
     *
       Kept separate from prevReal because the two are used for different things
       and only one of them may be a warp: prevReal is submitted WHOLE by the
       hybrid as a held real eye, which a synthesised frame must never be, while
       this is only ever read in the holes.

       Under afw_pin_source_eye the engine draws one eye and never the other, so
       prevReal for the synthesised eye is null for the entire mission and the
       fill fell through to stretching the background - a smear down the right
       side of every near object, as wide as the disparity step across its edge.
       The previous synthesised frame is a warp rather than a render, but it is a
       warp of a real render, so it is right everywhere except in its own holes,
       and head movement slides those off the ones being filled now. */
    ID3D11Texture2D* prevFill = prevReal;
    if (prevFill == nullptr && SynthFillEnabled() && g_xr.afwSynthHas[other])
        prevFill = g_xr.afwSynth[other];

    /* ---- AFR WHILE STILL, AFW WHILE MOVING -------------------------------
     *
     * The two modes fail in opposite directions, and that is what makes a
     * hybrid worth having rather than a compromise.
     *
     *   AFR  every eye is a REAL render, so the geometry is exact - but the
     *        held one is a frame old, so it is wrong in TIME.
     *   AFW  both eyes come from one instant, so the time is right - but the
     *        second one is APPROXIMATE: disocclusions invented, silhouettes no
     *        finer than the depth buffer, magnified surfaces reconstructed.
     *
     * The asymmetry that matters: when nothing has moved, the held real render
     * is not merely good, it is EXACT - pixel for pixel what a fresh render of
     * that eye would produce. No warp can beat it, and every warp artifact is
     * pure loss. And standing still looking at something is exactly when those
     * artifacts get examined.
     *
     * So the warp is not the default; it is what we reach for when the held eye
     * has gone stale. Translation is the test, not rotation: the compositor
     * already reprojects a held eye for rotation from the pose we submit with
     * it, and does that correctly. It cannot fix translation without a depth
     * layer, and the depth layer is declined here because the game's depth does
     * not match the eye swapchain's size. Translation is therefore the only
     * error AFR actually leaves on the screen, so it is the only thing worth
     * measuring.
     *
     * Hysteresis, because a threshold crossed twice a second would swap the
     * character of the image while the player was looking straight at it. */
    if (StillAfrEnabled() && prevReal != nullptr)
    {
        const XrVector3f& a = drawn[other].pose.position;
        const XrVector3f& b = g_xr.afwRealPose[other].position;

        const float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        const float instant = std::sqrt(dx * dx + dy * dy + dz * dz);

        /* SMOOTHED, because one frame of it is mostly tracking noise.
         *
           A single inter-frame delta at 90 fps carries a few tenths of a
           millimetre of jitter even from a head clamped in a vice, which is the
           same order as the threshold itself. Deciding on the raw value flips
           the mode several times a second, and each flip changes what the second
           eye IS - warped or real - which is a visible stutter of its own, on
           top of whichever mode is running. Averaging over about a tenth of a
           second decides on how the head is actually behaving rather than on
           what one sample of it did. */
        g_xr.stillMotion = g_xr.stillMotion * 0.9f + instant * 0.1f;
        const float moved = g_xr.stillMotion;

        const float on  = StillAfrThreshold();
        const float off = on * 2.0f;

        if (g_xr.stillAfr ? (moved < off) : (moved < on))
        {
            if (!g_xr.stillAfr)
            {
                g_xr.stillAfr = true;
                BVR_INFO("AFW: head is still (%.2f mm since that eye was drawn), so "
                         "the other eye is its own REAL render rather than a warp of "
                         "this one. Nothing moved, so it is exact - no synthesis "
                         "artifact can be better than the thing itself.", moved * 1000.0f);
            }

            /* The real held eye, tagged with the pose it was DRAWN at so the
               compositor reprojects it rather than assuming it is current. */
            context->CopyResource(g_xr.afrResult[other], g_xr.afwReal[other]);
            context->CopyResource(g_xr.afrResult[eye], source);

            const uint64_t stillNow = GetTickCount64();

            g_xr.afrPose[eye]   = drawn[eye].pose;
            if (latched)
                g_xr.afrPose[eye].orientation = latchedQ;
            g_xr.afrFov[eye]    = drawn[eye].fov;
            g_xr.afrBodyYaw[eye] = g_xr.bodyYaw.load(std::memory_order_acquire);
            g_xr.afrBodyPos[eye][0] = g_xr.bodyPosX.load(std::memory_order_acquire);
            g_xr.afrBodyPos[eye][1] = g_xr.bodyPosY.load(std::memory_order_acquire);
            g_xr.afrBodyPos[eye][2] = g_xr.bodyPosZ.load(std::memory_order_acquire);

            g_xr.afrPose[other]    = g_xr.afwRealPose[other];
            g_xr.afrFov[other]     = drawn[other].fov;
            g_xr.afrBodyYaw[other] = g_xr.afwRealYaw[other];

            for (uint32_t e = 0; e < kEyeCount; ++e)
            {
                g_xr.afrHasResult[e] = true;
                g_xr.afrLastCaptureMs[e] = stillNow;
                g_xr.afrStageHas[e] = false;
                g_xr.afrStageGen[e] = -1;
            }

            /* This eye's real render is still what the NEXT frame will hold. */
            if (g_xr.afwReal[eye] != nullptr)
            {
                context->CopyResource(g_xr.afwReal[eye], source);
                g_xr.afwRealHas[eye]  = true;
                g_xr.afwRealPose[eye] = drawn[eye].pose;
                if (latched)
                    g_xr.afwRealPose[eye].orientation = latchedQ;
                g_xr.afwRealYaw[eye]  = g_xr.afrBodyYaw[eye];
            }

            if (latched)
                latch_consume(eye);
            return true;
        }

        if (g_xr.stillAfr)
        {
            g_xr.stillAfr = false;
            BVR_INFO("AFW: moving again (%.2f mm), so the second eye goes back to "
                     "being warped from this one - a frame-old real render would "
                     "now be displaced by the movement.", moved * 1000.0f);
        }
    }

    /* PREDICTION MEETS TRUTH, ONCE PER EYE.
     *
       afwSynth[eye] is the frame in which THIS eye was synthesised. The eyes have
       just swapped, so `source` is the engine's own render of the same eye and
       the two can be compared directly. Done before the warp rather than after,
       because the warp is about to overwrite the other eye's copy and this one
       must be read while it is still the previous frame's. */
    if (g_xr.afwSynthHas[eye] && g_xr.afwSynth[eye] != nullptr)
    {
        afw_check_synthesis(engine_device(), context,
                            g_xr.afwSynth[eye], source, static_cast<int>(eye));
    }

    /* HOW FAR THE HEAD TURNED SINCE THE FILL WAS RENDERED.
     *
       The disocclusion fill reads the destination eye's own last real render at
       the same screen coordinate, which is only where that geometry belongs if
       the head has not turned since. Give the warp the rotation between the two
       so it can look in the right place; see afw_set_prev_reproject. */
    {
        float rot[9] = { 1,0,0, 0,1,0, 0,0,1 };
        bool  haveRot = false;

        if (g_xr.afwRealHas[other])
        {
            /* A direction in this frame's view, expressed in the previous
               render's view: conjugate(previous) * current. */
            const XrQuaternionf& qp = g_xr.afwRealPose[other].orientation;
            const XrQuaternionf  qpInv{ -qp.x, -qp.y, -qp.z, qp.w };
            const XrQuaternionf  q = quat_mul(qpInv, drawn[other].pose.orientation);

            const float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
            const float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
            const float wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;

            rot[0] = 1.0f - 2.0f * (yy + zz);
            rot[1] =        2.0f * (xy - wz);
            rot[2] =        2.0f * (xz + wy);
            rot[3] =        2.0f * (xy + wz);
            rot[4] = 1.0f - 2.0f * (xx + zz);
            rot[5] =        2.0f * (yz - wx);
            rot[6] =        2.0f * (xz - wy);
            rot[7] =        2.0f * (yz + wx);
            rot[8] = 1.0f - 2.0f * (xx + yy);

            haveRot = true;
        }

        const XrFovf& f = drawn[other].fov;
        afw_set_prev_reproject(rot,
                               std::tan(f.angleLeft),  std::tan(f.angleRight),
                               std::tan(f.angleUp),    std::tan(f.angleDown),
                               haveRot);
    }

    if (!afw_warp_eye(engine_device(), context, source,
                      static_cast<int>(eye), g_xr.afrResult[other], prevFill))
    {
        ++g_xr.afwFallbacks;
        return false;
    }

    context->CopyResource(g_xr.afrResult[eye], source);

    /* THE DEPTH LAYER, WHICH AFW HAS NEVER ONCE SUBMITTED.
     *
       afrDepthHas is only ever set from afrDepthStageHas, and the single place
       that becomes true is afr_capture. AFW does not go through afr_capture - it
       writes afrResult directly, exactly as it does above - so the flag stayed
       false for every frame of every AFW mission and the per-second counter read
       "0 eye(s) submitted with depth" from the day the mode was written.

       That is not cosmetic. Without a depth layer the compositor can only
       reproject a late frame for ROTATION, so head TRANSLATION goes uncorrected
       and the world slides whenever a frame is missed. It is felt as head
       movement that is not smooth, and it is worst exactly where a depth error
       always is - on near geometry.

       Only the RENDERED eye gets one. The game's depth describes the image the
       engine just drew and nothing else; the synthesised eye's depth would have
       to be warped alongside its colour, which is a separate piece of work. The
       other eye is left with no depth rather than the wrong eye's, and sending
       none is always safe - that eye simply goes up as it did before. */
    if (g_depthLayerReady && g_xr.afrDepthResult[eye] != nullptr)
    {
        ID3D11Texture2D* gameDepth = eye_depth_texture();
        if (gameDepth != nullptr && depth_hold_matches(gameDepth))
        {
            context->CopyResource(g_xr.afrDepthResult[eye], gameDepth);
            g_xr.afrDepthHas[eye] = true;
        }
        else
        {
            g_xr.afrDepthHas[eye] = false;
        }

        /* THE SYNTHESISED HALF GETS THE DRAWN EYE'S DEPTH TOO (TEST 174).
         *
           It used to get none, on the grounds that the game's depth belongs to
           the eye that was drawn. True, and it made AFW float: TEST 132 found the
           depth layer is what closes the head-TRANSLATION half of the float, and
           with depth on one eye only, SteamVR corrected translation in the left
           eye and not the right - so the right eye floated exactly as AFR did
           before TEST 132, and the two eyes disagreed while doing it.
         *
           The drawn eye's depth is the synthesised eye's depth shifted by the
           disparity, so it is exact everywhere except a band beside near
           silhouettes, where the error is focal * per-frame translation * (1/z
           difference) - a pixel or two - against the whole eye being uncorrected.
           afw_depth_both_eyes = 0 restores depth on the drawn eye only. */
        static int bothEyes = -1;
        if (bothEyes < 0)
            bothEyes = config_float("afw_depth_both_eyes", 1.0f) > 0.5f ? 1 : 0;

        if (bothEyes != 0 && g_xr.afrDepthHas[eye] && g_xr.afrDepthResult[other] != nullptr)
        {
            context->CopyResource(g_xr.afrDepthResult[other], g_xr.afrDepthResult[eye]);
            g_xr.afrDepthHas[other] = true;
        }
        else
        {
            g_xr.afrDepthHas[other] = false;
        }
    }

    /* Keep what was just synthesised, so the next frame's disocclusions have
       something better than a sideways stretch to come from. Written after the
       warp and before the reticle, for the same reason the real keep is: a hole
       should be filled with the world, not with a crosshair. */
    /* NOT KEPT WHEN NOTHING WILL READ IT (TEST 175). Its readers are the hole
       fill (SynthFillEnabled) and the residual meter, which compares it with a
       later REAL render of the same eye - and under the pin that eye is never
       rendered. Pinned with the fill off, this was a full-frame copy per frame
       for nobody, inside the frame budget that decides whether SteamVR shows
       this frame or reprojects the last one. */
    const bool eyesPinned = g_xr.afrPinnedEye.load(std::memory_order_acquire) >= 0;

    if (g_xr.afwSynth[other] != nullptr && !(eyesPinned && !SynthFillEnabled()))
    {
        context->CopyResource(g_xr.afwSynth[other], g_xr.afrResult[other]);
        g_xr.afwSynthHas[other] = true;
    }

    /* Record THIS eye's real render for the next frame, when the eyes swap and
       this one becomes the synthesised half. Kept before the reticle is drawn:
       a disocclusion should be filled with the world, not with a crosshair
       stamped into the middle of it. */
    /* Every reader of afwReal asks for the OTHER eye's (prevReal, the still
       check, the hole reprojection), and under the pin the other eye is never
       drawn - so this keep was never read. Skipped while pinned (TEST 175);
       alternation, which does read it, still records it. */
    if (g_xr.afwReal[eye] != nullptr && !eyesPinned)
    {
        context->CopyResource(g_xr.afwReal[eye], source);
        g_xr.afwRealHas[eye]  = true;
        g_xr.afwRealPose[eye] = drawn[eye].pose;
        if (latched)
            g_xr.afwRealPose[eye].orientation = latchedQ;
        /* Read here rather than reusing the one below: that is declared after
           the reticle draws, and this record has to be made before them. */
        g_xr.afwRealYaw[eye]  = g_xr.bodyYaw.load(std::memory_order_acquire);
    }

    afw_eye_census(eye);

    reticle_draw(engine_device(), context, g_xr.afrResult[eye],
                 static_cast<int>(eye), afw_focal_px(), afw_baseline());
    reticle_draw(engine_device(), context, g_xr.afrResult[other],
                 static_cast<int>(other), afw_focal_px(), afw_baseline());

    const uint64_t now = GetTickCount64();
    const float yaw = g_xr.bodyYaw.load(std::memory_order_acquire);

    for (uint32_t e = 0; e < kEyeCount; ++e)
    {
        g_xr.afrPose[e] = g_xr.usedValid.load(std::memory_order_acquire)
                              ? g_xr.usedPose[e]
                              : drawn[e].pose;
        if (latched)
            g_xr.afrPose[e].orientation = latchedQ;
        g_xr.afrFov[e]  = drawn[e].fov;
        g_xr.afrBodyYaw[e] = yaw;
        g_xr.afrBodyPos[e][0] = g_xr.bodyPosX.load(std::memory_order_acquire);
        g_xr.afrBodyPos[e][1] = g_xr.bodyPosY.load(std::memory_order_acquire);
        g_xr.afrBodyPos[e][2] = g_xr.bodyPosZ.load(std::memory_order_acquire);
        g_xr.afrHasResult[e] = true;
        g_xr.afrLastCaptureMs[e] = now;

        /* Nothing is staged any more, and leaving stale staging behind would
           let a later switch back to AFR promote a pair from before the mode
           changed. */
        g_xr.afrStageHas[e] = false;
        g_xr.afrStageGen[e] = -1;
    }

    if (latched)
        latch_consume(eye);
    return true;
}

/* ---- WHAT AFW COSTS, MEASURED (TEST 175) ---------------------------------
 *
   SteamVR reprojected 30% of an AFW session's frames against 3.8% of an AFR
   session at the same draw load, and nothing in the pose path differs any
   more - so AFW frames are missing their display deadline. This times the AFW
   work itself: GPU with timestamp queries read back a few frames late (never
   waited on), CPU with the performance counter. It runs only when AFW is
   attempted; AFR never reaches it. */
struct AfwTimingSlot
{
    ID3D11Query* disjoint = nullptr;
    ID3D11Query* begin    = nullptr;
    ID3D11Query* end      = nullptr;
    bool         active   = false;
    bool         pending  = false;
};

constexpr int kAfwTimingSlots = 6;
static AfwTimingSlot g_afwTiming[kAfwTimingSlots];
static int           g_afwTimingNext   = 0;
static bool          g_afwTimingFailed = false;
static double        g_afwGpuSum = 0.0, g_afwGpuMax = 0.0;
static uint32_t      g_afwGpuN   = 0;
static double        g_afwCpuSum = 0.0, g_afwCpuMax = 0.0;
static uint32_t      g_afwCpuN   = 0;
static LARGE_INTEGER g_afwCpuStart = {};
static uint64_t      g_afwTimingLog = 0;

static void afw_timing_release()
{
    for (AfwTimingSlot& s : g_afwTiming)
    {
        if (s.disjoint != nullptr) { s.disjoint->Release(); s.disjoint = nullptr; }
        if (s.begin != nullptr)    { s.begin->Release();    s.begin = nullptr; }
        if (s.end != nullptr)      { s.end->Release();      s.end = nullptr; }
        s.active  = false;
        s.pending = false;
    }
    g_afwTimingNext = 0;
}

static void afw_timing_collect(ID3D11DeviceContext* context)
{
    for (AfwTimingSlot& s : g_afwTiming)
    {
        if (!s.pending)
            continue;

        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj = {};
        UINT64 t0 = 0, t1 = 0;
        if (context->GetData(s.disjoint, &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            context->GetData(s.begin, &t0, sizeof(t0), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            context->GetData(s.end, &t1, sizeof(t1), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK)
            continue;

        s.pending = false;
        if (dj.Disjoint || dj.Frequency == 0 || t1 < t0)
            continue;

        const double ms = static_cast<double>(t1 - t0) * 1000.0 /
                          static_cast<double>(dj.Frequency);
        g_afwGpuSum += ms;
        if (ms > g_afwGpuMax)
            g_afwGpuMax = ms;
        ++g_afwGpuN;
    }
}

static void afw_timing_begin(ID3D11DeviceContext* context)
{
    QueryPerformanceCounter(&g_afwCpuStart);
    if (g_afwTimingFailed || context == nullptr)
        return;

    afw_timing_collect(context);

    AfwTimingSlot& s = g_afwTiming[g_afwTimingNext];
    if (s.pending)
        return;   /* ring full: this frame goes unmeasured rather than waited on */

    if (s.disjoint == nullptr)
    {
        ID3D11Device* device = engine_device();
        const D3D11_QUERY_DESC dj = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
        const D3D11_QUERY_DESC ts = { D3D11_QUERY_TIMESTAMP, 0 };
        if (device == nullptr ||
            FAILED(device->CreateQuery(&dj, &s.disjoint)) ||
            FAILED(device->CreateQuery(&ts, &s.begin)) ||
            FAILED(device->CreateQuery(&ts, &s.end)))
        {
            g_afwTimingFailed = true;
            afw_timing_release();
            BVR_WARN("AFW cost: timestamp queries unavailable; the GPU cost will not "
                     "be measured.");
            return;
        }
    }

    context->Begin(s.disjoint);
    context->End(s.begin);
    s.active = true;
}

static void afw_timing_end(ID3D11DeviceContext* context)
{
    LARGE_INTEGER now = {}, freq = {};
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    if (freq.QuadPart > 0)
    {
        const double ms = static_cast<double>(now.QuadPart - g_afwCpuStart.QuadPart) *
                          1000.0 / static_cast<double>(freq.QuadPart);
        g_afwCpuSum += ms;
        if (ms > g_afwCpuMax)
            g_afwCpuMax = ms;
        ++g_afwCpuN;
    }

    AfwTimingSlot& s = g_afwTiming[g_afwTimingNext];
    if (s.active && context != nullptr)
    {
        context->End(s.end);
        context->End(s.disjoint);
        s.active  = false;
        s.pending = true;
        g_afwTimingNext = (g_afwTimingNext + 1) % kAfwTimingSlots;
    }

    const uint64_t tick = GetTickCount64();
    if (g_afwTimingLog == 0)
        g_afwTimingLog = tick;
    else if (tick - g_afwTimingLog >= 1000)
    {
        g_afwTimingLog = tick;
        BVR_INFO("AFW cost: GPU %.2f ms mean / %.2f max over %u frame(s), CPU %.2f ms "
                 "mean / %.2f max over %u. A frame has 11.1 ms at 90 Hz for everything; "
                 "this is what AFW adds on top of the engine's own render.",
                 g_afwGpuN ? g_afwGpuSum / g_afwGpuN : 0.0, g_afwGpuMax, g_afwGpuN,
                 g_afwCpuN ? g_afwCpuSum / g_afwCpuN : 0.0, g_afwCpuMax, g_afwCpuN);
        g_afwGpuSum = 0.0; g_afwGpuMax = 0.0; g_afwGpuN = 0;
        g_afwCpuSum = 0.0; g_afwCpuMax = 0.0; g_afwCpuN = 0;
    }
}

void release_afr_results()
{
    afw_timing_release();
    /* FIRST, and inside here rather than at the call site.
     *
       The image latch caches shader resource views KEYED ON TEXTURE POINTER,
       and the allocator is perfectly capable of handing the same address back
       for a new texture in the next mission. A stale cache entry would then
       return a view onto a dead resource for an address that now holds a live
       one - a black eye at best. Standing the latch down here makes that
       impossible for every caller this function ever grows. */
    bvr::image_latch_release();
    bvr::vr_sharpen_release();

    for (uint32_t eye = 0; eye < kEyeCount; ++eye)
    {
        if (g_xr.afrResult[eye] != nullptr)
        {
            g_xr.afrResult[eye]->Release();
            g_xr.afrResult[eye] = nullptr;
        }
        if (g_xr.afrStage[eye] != nullptr)
        {
            g_xr.afrStage[eye]->Release();
            g_xr.afrStage[eye] = nullptr;
        }
        if (g_xr.afwReal[eye] != nullptr)
        {
            g_xr.afwReal[eye]->Release();
            g_xr.afwReal[eye] = nullptr;
        }
        if (g_xr.afwSynth[eye] != nullptr)
        {
            g_xr.afwSynth[eye]->Release();
            g_xr.afwSynth[eye] = nullptr;
        }

        g_xr.afrHasResult[eye] = false;
        g_xr.afrStageHas[eye] = false;
        g_xr.afrStageGen[eye] = -1;

        /* Cleared with the texture. A kept render from a mission that has ended
           would fill the next mission's first disocclusions with the last one's
           scenery. */
        g_xr.afwRealHas[eye] = false;
        g_xr.afwSynthHas[eye] = false;
    }
}

/* True when two DXGI formats share a typeless family, which is what
   CopyResource actually requires - not bit-identical formats. */
bool same_format_family(DXGI_FORMAT a, DXGI_FORMAT b)
{
    if (a == b)
        return true;

    auto family = [](DXGI_FORMAT f) -> int {
        switch (f)
        {
        case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        case DXGI_FORMAT_B8G8R8A8_UNORM:
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
            return 1;
        case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        case DXGI_FORMAT_R8G8B8A8_UNORM:
        case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
            return 2;
        default:
            return 0;
        }
    };

    const int fa = family(a);
    return fa != 0 && fa == family(b);
}
/* Copies an arbitrary source texture into an acquired swapchain image, with a
   one-time size and format compatibility check. AFR feeds this from its per-eye
   hold textures; the stereo path feeds it the engine eye targets directly. */
bool copy_from(ID3D11DeviceContext* context, EyeSwapchain& sc,
               uint32_t imageIndex, ID3D11Texture2D* source)
{
    if (source == nullptr || imageIndex >= sc.images.size())
        return false;

    ID3D11Texture2D* dest = sc.images[imageIndex].texture;
    if (dest == nullptr)
        return false;

    D3D11_TEXTURE2D_DESC srcDesc = {};
    D3D11_TEXTURE2D_DESC dstDesc = {};
    source->GetDesc(&srcDesc);
    dest->GetDesc(&dstDesc);

    /* Checked once and remembered. A format or size mismatch is a permanent
       property of this session, and logging it every frame at 90 Hz would bury
       the log in seconds. */
    if (!g_xr.copyChecked)
    {
        g_xr.copyChecked = true;
        g_xr.copyUsable =
            srcDesc.Width == dstDesc.Width &&
            srcDesc.Height == dstDesc.Height &&
            srcDesc.SampleDesc.Count == dstDesc.SampleDesc.Count &&
            same_format_family(srcDesc.Format, dstDesc.Format);

        if (g_xr.copyUsable)
        {
            BVR_INFO("Eye copy path enabled: %ux%u fmt %d -> fmt %d.",
                     srcDesc.Width, srcDesc.Height,
                     static_cast<int>(srcDesc.Format), static_cast<int>(dstDesc.Format));
        }
        else
        {
            BVR_ERR("Cannot copy eye texture into the swapchain: source %ux%u fmt %d "
                    "samples %u, destination %ux%u fmt %d samples %u. Staying on the "
                    "flat-colour path.",
                    srcDesc.Width, srcDesc.Height, static_cast<int>(srcDesc.Format),
                    srcDesc.SampleDesc.Count,
                    dstDesc.Width, dstDesc.Height, static_cast<int>(dstDesc.Format),
                    dstDesc.SampleDesc.Count);
        }
    }

    if (!g_xr.copyUsable)
        return false;

    context->CopyResource(dest, source);
    return true;
}

bool copy_eye(ID3D11DeviceContext* context, EyeSwapchain& sc,
              uint32_t imageIndex, uint32_t eye)
{
    if (!copy_enabled())
        return false;

    return copy_from(context, sc, imageIndex, eye_texture(static_cast<int32_t>(eye)));
}

/* Holds the D3D multithread lock for a scope. Taken around the whole frame body
   rather than just the copy: acquiring and releasing per eye would leave the two
   copies interleaved with engine work, which is the same race in smaller pieces. */
struct ContextLock
{
    ID3D11Multithread* mt;

    ContextLock() : mt(engine_multithread())
    {
        if (mt != nullptr)
            mt->Enter();
    }

    ~ContextLock()
    {
        if (mt != nullptr)
            mt->Leave();
    }

    ContextLock(const ContextLock&) = delete;
    ContextLock& operator=(const ContextLock&) = delete;
};

/* --------------------------------------------------------------------------
 * The settings panel as a quad layer.
 *
 * Returns true when `out` has been filled and should be submitted. Everything
 * here is skipped entirely when the panel is hidden, which is almost always.
 * ------------------------------------------------------------------------ */
XrVector3f rotate_by(const XrQuaternionf& q, const XrVector3f& v)
{
    /* v + 2w(q x v) + 2(q x (q x v)), the standard form. Written out rather than
       pulled in from a maths header because this is the only place in the file
       that needs it. */
    const XrVector3f u{ q.x, q.y, q.z };
    const XrVector3f uv{ u.y * v.z - u.z * v.y,
                         u.z * v.x - u.x * v.z,
                         u.x * v.y - u.y * v.x };
    const XrVector3f uuv{ u.y * uv.z - u.z * uv.y,
                          u.z * uv.x - u.x * uv.z,
                          u.x * uv.y - u.y * uv.x };

    return XrVector3f{ v.x + 2.0f * (q.w * uv.x + uuv.x),
                       v.y + 2.0f * (q.w * uv.y + uuv.y),
                       v.z + 2.0f * (q.w * uv.z + uuv.z) };
}

bool submit_overlay(ID3D11DeviceContext* context, const XrView views[kEyeCount],
                    XrCompositionLayerQuad* out)
{
    ID3D11Texture2D* panel = bvr::overlay_texture();
    if (panel == nullptr || g_xr.overlaySwapchain == XR_NULL_HANDLE ||
        g_xr.overlayImages.empty() || context == nullptr)
    {
        /* Hidden. Drop the pin so the next opening lands in front of wherever
           the player is looking THEN, not where they were standing last time. */
        g_xr.overlayPosePinned = false;
        return false;
    }

    /* Pinned once per opening. XR's view space looks down -Z, so a metre and a
       quarter along the head's forward axis is where the panel goes - close
       enough to read, far enough not to have to cross your eyes. */
    if (!g_xr.overlayPosePinned || bvr::overlay_take_just_opened())
    {
        const XrPosef& head = views[0].pose;
        const XrVector3f forward = rotate_by(head.orientation, XrVector3f{ 0.0f, 0.0f, -1.0f });

        g_xr.overlayPose.orientation = head.orientation;
        g_xr.overlayPose.position = {
            head.position.x + forward.x * 1.25f,
            head.position.y + forward.y * 1.25f,
            head.position.z + forward.z * 1.25f,
        };
        g_xr.overlayPosePinned = true;
    }

    uint32_t index = 0;
    XrSwapchainImageAcquireInfo acquire{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
    if (XR_FAILED(xrAcquireSwapchainImage(g_xr.overlaySwapchain, &acquire, &index)))
        return false;

    XrSwapchainImageWaitInfo wait{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
    wait.timeout = XR_INFINITE_DURATION;
    const bool ready = XR_SUCCEEDED(xrWaitSwapchainImage(g_xr.overlaySwapchain, &wait));

    if (ready && index < g_xr.overlayImages.size())
    {
        /* Same size, same typeless family, so this is a straight bit copy - the
           panel's colours reach the compositor exactly as ImGui authored them.
           See the format note where the panel texture is created. */
        context->CopyResource(g_xr.overlayImages[index].texture, panel);
    }

    XrSwapchainImageReleaseInfo release{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
    xrReleaseSwapchainImage(g_xr.overlaySwapchain, &release);

    if (!ready)
        return false;

    const float width = 0.90f;   /* metres across, at 1.25 m away */
    const float height = width * static_cast<float>(bvr::overlay_height()) /
                                 static_cast<float>(bvr::overlay_width());

    out->layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
    out->space = g_xr.appSpace;
    out->eyeVisibility = XR_EYE_VISIBILITY_BOTH;
    out->subImage.swapchain = g_xr.overlaySwapchain;
    out->subImage.imageArrayIndex = 0;
    out->subImage.imageRect.offset = { 0, 0 };
    out->subImage.imageRect.extent = {
        static_cast<int32_t>(bvr::overlay_width()),
        static_cast<int32_t>(bvr::overlay_height())
    };
    out->pose = g_xr.overlayPose;
    out->size = { width, height };
    return true;
}

/* Whether the frame may be begun before the engine renders it, rather than in
   the Present hook afterwards. 0 restores the old order exactly - and it is not
   a small switch, so it stays one line away for as long as it takes to trust. */
bool pre_render_frame_enabled()
{
    static int cached = -1;
    if (cached < 0)
        cached = config_float("pre_render_frame", 1.0f) > 0.5f ? 1 : 0;
    return cached != 0;
}

/* Hands render_frame the frame that was already begun, and clears it so the
   next Present cannot submit the same one twice. False means nothing was
   prepared and the caller owns the whole Wait/Begin/Locate sequence itself. */
bool take_prepared_frame(XrFrameState& state, XrView views[kEyeCount],
                         bool& posesValid)
{
    std::lock_guard<std::mutex> guard(g_xr.frameMutex);

    if (!g_xr.framePrepared)
        return false;

    state      = g_xr.preparedState;
    views[0]   = g_xr.preparedViews[0];
    views[1]   = g_xr.preparedViews[1];
    posesValid = g_xr.preparedValid;

    g_xr.framePrepared = false;
    return true;
}

void render_frame(ID3D11DeviceContext* context, IDXGISwapChain* swapChain)
{
    ContextLock lock;

    /* THE FRAME MAY ALREADY HAVE BEEN BEGUN, BEFORE THE ENGINE DREW IT.
     *
       See xr_prepare_render_frame. When the managed side calls it from the top
       of the camera build, Wait and Begin have already happened and the views
       were located for THIS frame's display time - the engine then rendered
       with them, and they are what has to be submitted. Locating again here
       would produce a newer pose than the pixels were drawn from, which is the
       one thing the submitted pose must never be.

       Nothing prepared means the old path: Wait and Begin here, and the pose
       published for whatever frame the engine draws next. */
    XrFrameState frameState{ XR_TYPE_FRAME_STATE };
    XrView       preparedViews[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    bool         preparedPosesValid = false;

    const bool prepared = take_prepared_frame(frameState, preparedViews,
                                              preparedPosesValid);

    if (!prepared)
    {
        XrFrameWaitInfo waitInfo{ XR_TYPE_FRAME_WAIT_INFO };
        if (!check(xrWaitFrame(g_xr.session, &waitInfo, &frameState), "xrWaitFrame"))
            return;
    }

    /* One key pass per frame at most; both consumers set and check this. */
    g_xr.uiKeyedThisFrame = false;

    /* Re-arms the one-shot window clear for this frame. */
    begin_backbuffer_frame();

    if (!prepared)
    {
        XrFrameBeginInfo beginInfo{ XR_TYPE_FRAME_BEGIN_INFO };
        if (!check(xrBeginFrame(g_xr.session, &beginInfo), "xrBeginFrame"))
            return;
    }

    /* Drawn before anything is acquired, so the panel texture is finished by the
       time the quad layer wants to copy it. Returns immediately when hidden. */
    bvr::overlay_render(engine_device(), context);

    XrCompositionLayerProjectionView projViews[kEyeCount] = {};
    /* Function scope, not loop scope: these are chained off projViews[].next and
       the runtime reads them inside xrEndFrame, long after a loop-local would
       have gone out of scope. */
    XrCompositionLayerDepthInfoKHR depthInfos[kEyeCount] = {};
    XrCompositionLayerProjection layer{ XR_TYPE_COMPOSITION_LAYER_PROJECTION };
    XrCompositionLayerQuad quadLayer{ XR_TYPE_COMPOSITION_LAYER_QUAD };
    XrCompositionLayerQuad flatLayer{ XR_TYPE_COMPOSITION_LAYER_QUAD };
    XrCompositionLayerQuad uiLayer{ XR_TYPE_COMPOSITION_LAYER_QUAD };
    /* The world OR the flat screen, then the settings panel over whichever it
       was. Order is the submission order - the compositor draws layers back to
       front as given - so the panel is always added last and stays readable. */
    /* World or flat screen, then the keyed UI over it, then the settings panel
       over everything. Three, because the UI overlay is a layer of its own. */
    const XrCompositionLayerBaseHeader* layers[3] = {};
    uint32_t layerCount = 0;

    if (frameState.shouldRender == XR_TRUE)
    {
        XrViewLocateInfo locateInfo{ XR_TYPE_VIEW_LOCATE_INFO };
        locateInfo.viewConfigurationType = kViewConfig;
        locateInfo.displayTime = frameState.predictedDisplayTime;
        locateInfo.space = g_xr.appSpace;

        XrViewState viewState{ XR_TYPE_VIEW_STATE };
        XrView views[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
        uint32_t viewCount = 0;

        bool posesValid = false;

        if (prepared)
        {
            /* Located before the engine drew, at this frame's display time, and
               already published to the engine - which then rendered with it.
               Re-using it is the entire point: the pose the pixels came from and
               the pose they are submitted with are then the same object, not two
               samples of a moving head taken a render apart. */
            views[0] = preparedViews[0];
            views[1] = preparedViews[1];
            posesValid = preparedPosesValid;
        }
        else
        {
            const XrResult lr = xrLocateViews(g_xr.session, &locateInfo, &viewState,
                                              kEyeCount, &viewCount, views);

            posesValid =
                XR_SUCCEEDED(lr) &&
                (viewState.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) != 0 &&
                (viewState.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) != 0;
        }

        /* DID THE LAST FRAME MAKE ITS DISPLAY PERIOD? (TEST 175)
         *
           The heartbeat counts Presents, and SteamVR can take 90 a second while
           still showing a third of them a period late: it reprojected 1872 of
           6279 frames in an AFW session whose heartbeat read 88-90. A frame that
           missed shows here as the next predicted display time jumping two
           periods instead of one. Labelled with the running mode, so AFR and AFW
           can be compared by switching mid-battle, in one scene. */
        {
            static XrTime   lastDisplay = 0;
            static uint32_t lateFrames = 0, paceFrames = 0;
            static uint64_t lastPaceLog = 0;

            const XrTime period = frameState.predictedDisplayPeriod;
            if (lastDisplay != 0 && period > 0 &&
                frameState.predictedDisplayTime > lastDisplay)
            {
                ++paceFrames;
                if (frameState.predictedDisplayTime - lastDisplay > period + period / 2)
                    ++lateFrames;
            }
            lastDisplay = frameState.predictedDisplayTime;

            const uint64_t paceTick = GetTickCount64();
            if (lastPaceLog == 0)
                lastPaceLog = paceTick;
            else if (paceTick - lastPaceLog >= 1000)
            {
                lastPaceLog = paceTick;
                if (paceFrames > 0 && g_xr.missionActive.load(std::memory_order_acquire))
                {
                    BVR_INFO("Display pacing (%s): %u of %u frame(s) this second arrived a "
                             "display period late - SteamVR showed a reprojection instead. "
                             "Late latch slot overwritten before its capture: %llu.",
                             g_xr.afwEnabled.load(std::memory_order_acquire) ? "AFW" : "AFR",
                             lateFrames, paceFrames,
                             static_cast<unsigned long long>(
                                 g_xr.latchOverwrites.exchange(0, std::memory_order_relaxed)));
                }
                lateFrames = 0;
                paceFrames = 0;
            }
        }

        /* OUTSIDE A MISSION THE STEREO PATH IS SKIPPED ENTIRELY.
           The main menu, the campaign map and every other screen are 2D images
           in the game's swapchain with no camera and no depth behind them, so
           there is nothing to render in stereo and the eye images would only
           carry a cleared colour or the last frame of a finished battle. The
           backbuffer goes up on a quad instead. See flat_screen_submit(). */
        if (!g_xr.missionActive.load(std::memory_order_acquire))
        {
            if (posesValid && !prepared)
                publish_views_for_engine(views, frameState.predictedDisplayTime,
                                         frameState.predictedDisplayPeriod);

            /* CONTROLLERS ARE SYNCED HERE TOO, AND FORGETTING THAT BROKE THEM.
             *
             * This used to live only on the mission path, because that was the
             * only path there was. Skipping the stereo work outside a mission
             * skipped this with it, and the result was that every stick and
             * every button went dead the moment there was no battle - which is
             * to say, in exactly the menus a player most needs to press
             * something in. The action state is not read from anywhere else;
             * without an xrSyncActions the runtime keeps returning the last
             * values it had, forever.
             *
             * The main menu has no predicted display time worth speaking of and
             * no world to place a hand in, but the poses are located against the
             * same instant regardless, so this is the same call it always was. */
            input_sync(g_xr.session, g_xr.appSpace, frameState.predictedDisplayTime);

            if (flat_screen_submit(context, swapChain, views, posesValid, &flatLayer))
            {
                layers[layerCount++] =
                    reinterpret_cast<const XrCompositionLayerBaseHeader*>(&flatLayer);
            }

            /* The settings panel reaches the menus too. It pins itself off the
               located views, so it needs them to be valid - but a panel that
               only worked inside a battle would be missing exactly where a
               player is most likely to go looking for settings. */
            if (posesValid && submit_overlay(context, views, &quadLayer))
            {
                layers[layerCount++] =
                    reinterpret_cast<const XrCompositionLayerBaseHeader*>(&quadLayer);
            }
        }
        else if (posesValid)
        {
            /* Back in a mission: let the screen re-pin itself in front of
               wherever the player is looking the next time one is needed,
               rather than where they happened to be standing at the menu. */
            g_xr.flatPosePinned = false;
            g_xr.flatSettleFrames = 0;

            if (!prepared)
                publish_views_for_engine(views, frameState.predictedDisplayTime,
                                         frameState.predictedDisplayPeriod);

            /* AFR: capture the frame the engine just drew as belonging to one
               eye, and record the pose it was drawn with. */
            if (g_xr.afrForget.exchange(false, std::memory_order_acquire))
                forget_afr_holds();

            const int afrEye = g_xr.afrEye.load(std::memory_order_acquire);
            const bool afr = afrEye >= 0 && afrEye < static_cast<int>(kEyeCount);

            /* Cleared before the capture that may set it, so "did this Present
               put something on screen" is a question about THIS Present and not
               about whichever one last managed it. Full AFR reads it below. */
            g_xr.afrPromotedThisPresent = false;

            if (afr && copy_enabled())
            {
                ID3D11Texture2D* source = eye_texture(0);
                if (source != nullptr)
                {
                    D3D11_TEXTURE2D_DESC srcDesc = {};
                    source->GetDesc(&srcDesc);

                    if (ensure_afr_results(engine_device(), srcDesc))
                    {
                        /* Attributed to the generation this image was RENDERED
                           from, never the one located moments ago. */
                        const XrView* drawn = content_views(views);

                        /* AND WHEN THE ENGINE HAS TOLD US OUTRIGHT, BELIEVE IT.
                         *
                           content_views infers which locate the image came from
                           by counting frames back - afr_pose_lag - because for a
                           long time inference was all there was. It is not any
                           more: bvr_set_used_views reports the pose managed
                           actually built the eye cameras from, every frame.
                         *
                           Inference and fact then disagree, and the gap grew
                           when pose_lead_frames started publishing a pose a
                           period AHEAD: the counted-back locate is a frame
                           behind, the real render pose is a frame in front, and
                           at 72 Hz that is 28 ms of head motion between what the
                           warp believes and what the pixels are.
                         *
                           It matters because `drawn` is not decoration. It is
                           the still/moving decision (how far the head has moved
                           since that eye was drawn), the rotation handed to
                           afw_set_prev_reproject for reading the previous eye in
                           the holes, and the pose a held eye is submitted with.
                           Feed those a pose the render never used and every one
                           of them is wrong BY THE HEAD'S OWN MOTION - which is
                           why the artefacts scale with how fast you move and
                           vanish when you stop.
                         *
                           The fov stays the located one: usedPose carries a pose
                           and nothing else, and the frustum is not what is in
                           question here. afr_pose_lag keeps governing the
                           fallback, for the frames before managed has reported
                           anything at all. */
                        XrView drawnUsed[kEyeCount];

                        if (g_xr.usedValid.load(std::memory_order_acquire))
                        {
                            for (uint32_t e = 0; e < kEyeCount; ++e)
                            {
                                drawnUsed[e] = drawn[e];
                                drawnUsed[e].pose = g_xr.usedPose[e];
                            }
                            drawn = drawnUsed;

                            static bool loggedUsed = false;
                            if (!loggedUsed)
                            {
                                loggedUsed = true;
                                BVR_INFO("AFR/AFW pose source: the engine's OWN "
                                         "reported render pose, not a locate "
                                         "counted %d frame(s) back. The two "
                                         "disagreed by the head's motion over "
                                         "that gap, and everything derived from "
                                         "it - the still/moving decision, the "
                                         "hole reprojection, the submitted pose "
                                         "of a held eye - was wrong in proportion "
                                         "to how fast the head was moving.",
                                         afr_pose_lag());
                            }
                        }
                        /* LABELLED WITH WHAT THE CAMERA WAS ACTUALLY SET TO.
                         *
                           afrCameraEye is written when the tick asks for an eye,
                           which is the moment the camera is configured. A capture
                           with no tick in between therefore keeps the same label,
                           because it really is the same eye drawn again - where
                           the sequence counter would have advanced and labelled
                           it as the opposite. See XrState::afrCameraEye. */
                        const int pinnedEye =
                            g_xr.afrPinnedEye.load(std::memory_order_acquire);
                        const uint32_t captureEye = static_cast<uint32_t>(
                            g_xr.afrCameraEye.load(std::memory_order_acquire));
                        /* This tick's eye index has now been used by a real
                           capture, so the managed side may advance the toggle.
                           See XrState::afrEyeUsed. */
                        g_xr.afrEyeUsed.store(true, std::memory_order_release);
                        /* Advance for the NEXT frame, on the same event that
                           consumed this one. See XrState::afrNextEye. */
                        if (pinnedEye < 0)
                            g_xr.afrNextEye.store(1 - static_cast<int>(captureEye),
                                              std::memory_order_release);

                        /* DID THE LABEL ACTUALLY ALTERNATE?
                         *
                           Under AFR the engine draws one eye per frame, so
                           consecutive captures must read 0,1,0,1. A REPEAT means
                           the Present hook read an index the managed tick had
                           already moved on from - the toggle runs on the game
                           tick and this runs on the render thread, and nothing
                           ties them together. A repeat labels the frame with the
                           wrong eye, and p.dir is derived from that label, so the
                           whole warp is signed backwards for as long as the phase
                           stays slipped.

                           The residual meter says that is happening in sustained
                           multi-second episodes: 6 of 6 near blocks classed
                           "wrong way" for eight seconds together, then clean for
                           five, then wrong again. This counts the cause directly
                           instead of inferring it from the symptom. */
                        if (pinnedEye < 0)
                        {
                            static int      lastEye = -1;
                            static uint32_t repeats = 0;
                            static uint32_t total   = 0;
                            static uint64_t lastLog = 0;

                            ++total;
                            if (lastEye >= 0 &&
                                captureEye == static_cast<uint32_t>(lastEye))
                                ++repeats;
                            lastEye = static_cast<int>(captureEye);

                            const uint64_t nowMs = GetTickCount64();
                            if (lastLog == 0)
                                lastLog = nowMs;
                            else if (nowMs - lastLog >= 1000)
                            {
                                lastLog = nowMs;
                                BVR_INFO("AFR parity: %u capture(s) this second, %u "
                                         "did NOT alternate. Anything but 0 means the "
                                         "eye label is out of step with the frame and "
                                         "the warp is signed backwards for those.",
                                         total, repeats);
                                total = 0;
                                repeats = 0;
                            }
                        }

                        /* AFW first, AFR only if it could not run. The
                           fallback is per frame and silent by design: a
                           mission that has not bound its depth buffer yet
                           should look like AFR, not like a black eye. */
                        /* afw_stood_down() is checked BEFORE afw_capture, and
                           short-circuits it. Once the calibration has decided
                           the depth cannot predict this scene, AFW is out of the
                           path permanently - asking it every frame would count a
                           fallback ninety times a second for a decision that was
                           taken once, and bury the one line that explains it. */
                        /* Same short-circuit as before, unrolled so the AFW
                           attempt can be timed (TEST 175). AFR never enters it. */
                        bool afwDone = false;
                        if (g_xr.afwEnabled.load(std::memory_order_acquire) &&
                            !afw_stood_down())
                        {
                            afw_timing_begin(context);
                            afwDone = afw_capture(context, source, drawn, captureEye);
                            afw_timing_end(context);
                        }
                        if (!afwDone)
                        {
                            afr_capture(context, source, drawn, captureEye);
                        }
                    }
                }
            }

            /* Look for the depth buffer bound with the eye target on the NEXT
               frame. Armed here rather than at startup so the detour costs an
               atomic load and nothing else for the rest of the frame.

               Wanted by AFW to synthesise an eye, and now equally by the depth
               composition layer to correct a held one - either reason arms the
               same watch, and it stands itself down once satisfied. */
            if (g_xr.afwEnabled.load(std::memory_order_acquire) ||
                (g_depthExtEnabled && !g_depthLayerReady))
                arm_depth_capture();

            /* Needs the game's depth buffer to exist before it can match its
               format, so it runs here rather than at session creation and
               succeeds on the first frame that has one. */
            if (g_depthExtEnabled && !g_depthLayerReady)
                ensure_depth_swapchains(engine_device());

            /* ---------------------------------------------------------------
               FULL AFR: HAND THE BEGUN FRAME BACK AND WAIT FOR THE OTHER EYE.
             *
               This is the whole of full AFR at this layer. The eye just drawn
               went into staging and no pair came out of it, so there is nothing
               new to put up - and putting up the half that exists is precisely
               what ordinary AFR does and full AFR exists not to do. Instead the
               XR frame stays open: no view history rolled, no swapchain touched,
               no xrEndFrame. The engine renders the other eye next, from the
               same locate this frame was begun with and the same world state,
               and THAT Present completes the pair and ends the frame.
             *
               framePrepared going back to true is what makes the next
               xr_prepare_render_frame decline: managed then skips Wait, Begin
               and Locate, keeps the pose it already published, and the second
               render is drawn from the identical head pose as the first. That
               is not a side effect to be tolerated - it is the property that
               makes the two eyes a stereo pair rather than two samples of a
               moving head.
             *
               THE CAP IS THE ONLY THING BETWEEN THIS AND A HANG. A mission where
               promotion stops - a paused tick, a scene teardown, an alternation
               that has died - would otherwise hold an XR frame open for as long
               as it lasted, and a runtime with no xrEndFrame coming decides the
               app has died. afr_promote_eye sets the flag on forced single-eye
               promotions too, so the pairing safety valve already ends most of
               these; the cap catches the rest.

               TWO, not one, and the difference is a real artefact. A Present
               that lands without the managed tick advancing the eye costs one
               extra frame to recover from - the duplicate capture restages its
               own eye and the true partner arrives on the frame after. A cap of
               one ended the frame on that duplicate instead, pairing a fresh eye
               with the PREVIOUS pair's leftover partner: the 31 ms held-image
               age that showed up in the log for exactly the seconds the parity
               counter was non-zero, and the wrong-baseline depth that came with
               it. Two lets the pair complete properly.

               It is still a hard bound. Three game frames at 144 Hz is about
               21 ms of open frame, which is inside what any runtime tolerates,
               and the warning below still fires if it is ever reached. */
            if (g_xr.fullAfr.load(std::memory_order_acquire) &&
                !g_xr.afrPromotedThisPresent &&
                afr && copy_enabled())
            {
                if (g_xr.afrDeferRun < 2)
                {
                    ++g_xr.afrDeferRun;
                    ++g_xr.afrDeferred;

                    /* The monitor still gets this frame. It is a real render of
                       a real eye, and freezing the window for every other game
                       frame would make the mode look like it had halved the
                       frame rate when it has doubled it. */
                    if (g_xr.inMission.load(std::memory_order_acquire))
                        mirror_to_monitor(context, swapChain);

                    {
                        std::lock_guard<std::mutex> guard(g_xr.frameMutex);
                        g_xr.preparedState    = frameState;
                        g_xr.preparedViews[0] = views[0];
                        g_xr.preparedViews[1] = views[1];
                        g_xr.preparedValid    = posesValid;
                        g_xr.framePrepared    = true;
                    }

                    static bool loggedDefer = false;
                    if (!loggedDefer)
                    {
                        loggedDefer = true;
                        BVR_INFO("Full AFR is live: the first eye of each pair is "
                                 "captured without ending the XR frame, so the engine "
                                 "renders the second eye from the same locate and the "
                                 "same world state and the pair goes up together. Both "
                                 "eyes are real renders at the full display rate - "
                                 "nothing is held and nothing is warped. The engine now "
                                 "has to produce two renders per display period; when it "
                                 "cannot, xrWaitFrame stops blocking and pairs land on "
                                 "later periods instead.");
                    }

                    return;
                }

                /* The cap. Something upstream has stopped completing pairs; end
                   the frame with what there is rather than hold it open. */
                static uint64_t lastWarn = 0;
                const uint64_t nowMs = GetTickCount64();
                if (nowMs - lastWarn >= 5000)
                {
                    lastWarn = nowMs;
                    BVR_WARN("Full AFR: three Presents in a row with no pair completed, "
                             "so the frame is being ended with a stale partner eye and "
                             "the depth will read wrong until it recovers. One duplicate "
                             "capture is absorbed now, so reaching this means the "
                             "alternation is not recovering - check the AFR parity line.");
                }
            }

            g_xr.afrDeferRun = 0;
            if (g_xr.fullAfr.load(std::memory_order_acquire))
                ++g_xr.afrPairsSubmitted;

            /* Controllers, at the same display time the eyes were located for -
               so a hand pose and the view it will be judged against belong to
               the same instant. */
            input_sync(g_xr.session, g_xr.appSpace, frameState.predictedDisplayTime);

            /* After the capture has consumed it, this locate becomes history. */
            roll_view_history(views);

            /* Age of what each eye is about to be submitted with. Sampled here,
               after this frame's capture, so the eye just drawn reads ~0 and the
               held one reads how long it has waited. */
            if (afr)
            {
                const uint64_t nowMs = GetTickCount64();
                for (uint32_t eye = 0; eye < kEyeCount; ++eye)
                {
                    if (!g_xr.afrHasResult[eye] || g_xr.afrLastCaptureMs[eye] == 0)
                        continue;
                    const uint32_t age =
                        static_cast<uint32_t>(nowMs - g_xr.afrLastCaptureMs[eye]);
                    if (age > g_xr.afrAgeMaxMs[eye])
                        g_xr.afrAgeMaxMs[eye] = age;
                }
            }


            /* THE LIVE HEAD POSE, LOCATED AS LATE AS THIS THREAD CAN GET IT.
             *
               The views at the top of this function were located before the
               engine drew, which is what the pixels correspond to. The image
               latch needs the other end of the interval: where the head is NOW,
               microseconds before the frame is handed over. The gap between the
               two IS the rotation vp_patch used to write into the geometry, and
               rotating the finished image by it puts the whole frame - sky,
               shadows and effects included - where the head is, instead of
               moving the geometry out from under them.
             *
               One extra xrLocateViews per frame, only when the latch is armed.
               It costs a runtime call on a thread that is about to block in
               xrEndFrame anyway. */
            XrView nowViews[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
            bool   nowValid = false;

            if (bvr::image_latch_enabled() || bvr::head_overdrive() != 0.0f)
            {
                XrViewLocateInfo nowInfo{ XR_TYPE_VIEW_LOCATE_INFO };
                nowInfo.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                nowInfo.displayTime = frameState.predictedDisplayTime;
                nowInfo.space = g_xr.appSpace;

                XrViewState nowState{ XR_TYPE_VIEW_STATE };
                uint32_t nowCount = 0;
                const XrResult nr = xrLocateViews(g_xr.session, &nowInfo, &nowState,
                                                  kEyeCount, &nowCount, nowViews);

                nowValid = XR_SUCCEEDED(nr) && nowCount == kEyeCount &&
                           (nowState.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) != 0;
            }

            /* ONE OVER-ROTATION FOR THE WHOLE FRAME, FROM THE FRESHER EYE.
             *
               Per-eye was the first version and it doubled: under AFR the two
               eyes hold render poses from different moments, so a per-eye delta
               turns the stale eye further than the fresh one and the pair stops
               differing by the IPD alone. A head rotation is rigid - one
               rotation, both eyes - so it is measured once, against whichever
               eye was captured most recently, and applied unchanged to both. */
            XrQuaternionf overdriveDelta{ 0.0f, 0.0f, 0.0f, 1.0f };

            if (afr && nowValid && bvr::head_overdrive() != 0.0f &&
                g_xr.afrHasResult[0] && g_xr.afrHasResult[1])
            {
                /* THE PROMOTION SAYS WHICH EYE IS FRESH. THE CLOCK CANNOT.
                 *
                   This compared afrLastCaptureMs, and GetTickCount64 steps in
                   10-16 ms while a frame is 11.1 ms - so both eyes usually
                   carry the SAME stamp and the comparison returned eye 1 no
                   matter which had just been captured. On the frames where eye
                   0 was really the fresh one the delta was measured against a
                   pose a whole frame older, which is a bias spike on those
                   frames and steady on the others. That is a bias that POPS
                   while the head moves rather than one that is simply wrong,
                   and it is exactly how it was described. */
                const uint32_t fresh =
                    (g_xr.afrLastPromotedEye >= 0)
                        ? static_cast<uint32_t>(g_xr.afrLastPromotedEye)
                        : ((g_xr.afrLastCaptureMs[1] >= g_xr.afrLastCaptureMs[0]) ? 1u : 0u);

                /* THE PREDICTOR CARRIES THE 'NOW' END FORWARD.
                 *
                   head_overdrive is reactive: it measures rotation that has
                   already happened, so at the ONSET of a movement it is zero
                   and contributes nothing - which is the resistance on a sudden
                   first movement that no value of k has ever reached. The
                   predictor extrapolates the live pose by velocity and
                   acceleration, and acceleration is precisely what is non-zero
                   at an onset. Off by default; head_predict_ms = 0 restores the
                   reactive-only behaviour exactly. */
                const XrQuaternionf predictedNow = bvr::head_predict(
                    nowViews[fresh].pose.orientation,
                    frameState.predictedDisplayTime);

                overdriveDelta = bvr::head_overdrive_delta(
                    g_xr.afrPose[fresh].orientation, predictedNow);
            }

            for (uint32_t eye = 0; eye < kEyeCount; ++eye)
            {
                EyeSwapchain& sc = g_xr.swapchains[eye];

                uint32_t imageIndex = 0;
                XrSwapchainImageAcquireInfo acquire{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
                if (!check(xrAcquireSwapchainImage(sc.handle, &acquire, &imageIndex),
                           "xrAcquireSwapchainImage"))
                    break;

                XrSwapchainImageWaitInfo wait{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
                wait.timeout = XR_INFINITE_DURATION;
                if (!check(xrWaitSwapchainImage(sc.handle, &wait), "xrWaitSwapchainImage"))
                    break;

                bool filled = false;

                if (afr)
                {
                    /* Both eyes every frame, each from its own hold texture. */
                    if (g_xr.afrHasResult[eye] && g_xr.afrResult[eye] != nullptr)
                    {
                        /* The latch. Returns the hold texture itself whenever it
                           declines, so this is the same CopyResource it always
                           was unless the rotation was actually applied. */
                        ID3D11Texture2D* toSubmit = g_xr.afrResult[eye];

                        if (nowValid)
                        {
                            toSubmit = bvr::image_latch_apply(
                                engine_device(), context, eye, toSubmit,
                                g_xr.afrPose[eye].orientation,
                                nowViews[eye].pose.orientation,
                                submitted_fov(g_xr.afrFov[eye]));
                        }

                        /* The VR panel's sharpening, on the finished image - the
                           one point every render mode shares (TEST 187). Returns
                           the image itself when it is off. */
                        toSubmit = bvr::vr_sharpen_apply(engine_device(), context, eye,
                                                         toSubmit);

                        filled = copy_from(context, sc, imageIndex, toSubmit);
                    }

                    /* LAST RESORT: THE OTHER EYE, RATHER THAN A FLAT COLOUR.
                     *
                       An eye with no image of its own used to go up as the
                       proof-of-life clear, which is a solid red or blue field
                       filling half the headset. That was written when the only
                       way to reach it was during the first frames of a session,
                       before either eye had been captured - a second of colour
                       while things started up.

                       It stopped being that. Pinning the source eye means AFR
                       can only ever fill the eye it is given, so any frame where
                       the warp declines leaves the other one with nothing, and a
                       warp that declined every frame left it that way for the
                       whole session. Showing the same picture in both eyes is
                       wrong - it is flat, with no stereo at all - but it is a
                       picture of the world, and it degrades to something a
                       player can still see and act in. A red field is not.

                       Deliberately AFTER the real attempt, so it never competes
                       with a correct image; it only fills a gap that would
                       otherwise be a colour. */
                    if (!filled)
                    {
                        const uint32_t other = 1u - eye;
                        if (g_xr.afrHasResult[other] && g_xr.afrResult[other] != nullptr)
                        {
                            filled = copy_from(context, sc, imageIndex,
                                               g_xr.afrResult[other]);
                            if (filled)
                                note_mono_fallback(eye);
                        }
                    }
                }
                else
                {
                    filled = copy_eye(context, sc, imageIndex, eye);
                }

                if (!filled)

                {
                    /* Phase 2 proof of life, still the fallback: a flat colour,
                       distinct per eye so that swapped eyes are immediately
                       visible rather than silent. */
                    const float clearLeft[4]  = { 0.05f, 0.10f, 0.25f, 1.0f };
                    const float clearRight[4] = { 0.25f, 0.10f, 0.05f, 1.0f };
                    context->ClearRenderTargetView(sc.rtvs[imageIndex],
                                                   eye == 0 ? clearLeft : clearRight);
                }

                XrSwapchainImageReleaseInfo release{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
                check(xrReleaseSwapchainImage(sc.handle, &release), "xrReleaseSwapchainImage");

                projViews[eye] = { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW };
                /* The pose the image was RENDERED with, not the newest one. For the
                   eye that is a frame old this is what lets the compositor
                   reproject it correctly instead of smearing it. */
                /* TELL THE COMPOSITOR WHAT THE PIXELS NOW ARE, NOT WHAT THEY WERE.
                 *
                   Without the latch this is the render pose, which is what lets
                   the runtime reproject a held eye instead of smearing it. With
                   the latch applied the image has ALREADY been rotated to the
                   live orientation, so submitting the render pose would ask the
                   compositor to rotate it a second time and the correction would
                   be applied twice. The position is untouched either way: the
                   latch is a rotation about the eye and moves nothing. */
                XrPosef contained = g_xr.afrPose[eye];
                if (afr && g_xr.afrHasResult[eye] && bvr::image_latch_applied(eye))
                    contained.orientation = nowViews[eye].pose.orientation;

                projViews[eye].pose = (afr && g_xr.afrHasResult[eye])
                                          ? move_corrected_pose(eye, turn_corrected_from(eye, contained))
                                          : views[eye].pose;

                /* The head overdrive is NOT applied here. It moves the pair as
                   one rigid head and therefore needs both poses to exist first;
                   see the call after this loop. */
                /* ...and the fov it was really rendered with, which is NOT the
                   runtime's when the managed side has widened it to a symmetric
                   frustum. See submitted_fov. */
                projViews[eye].fov  = submitted_fov(
                    (afr && g_xr.afrHasResult[eye]) ? g_xr.afrFov[eye] : views[eye].fov);

                /* The depth belonging to the image just submitted, so the
                   compositor can correct it for head MOVEMENT rather than for
                   rotation alone. Held alongside the colour and promoted with
                   it, so the two always describe one instant. Sending none is
                   always safe - the eye simply goes up as it used to. */
                if (afr && g_xr.afrHasResult[eye] && g_xr.afrDepthHas[eye])
                {
                    submit_depth(context, eye, g_xr.afrDepthResult[eye],
                                 projViews[eye], depthInfos[eye]);
                }
                if (eye == 0)
                    log_submitted_fov(projViews[eye].fov);
                projViews[eye].subImage.swapchain = sc.handle;
                projViews[eye].subImage.imageArrayIndex = 0;
                projViews[eye].subImage.imageRect.offset = { 0, 0 };
                projViews[eye].subImage.imageRect.extent = {
                    static_cast<int32_t>(sc.width), static_cast<int32_t>(sc.height)
                };
            }

            /* HEAD OVERDRIVE, on the finished pair.
             *
               After the loop because it turns the two eyes as ONE HEAD -
               orientations pre-multiplied and positions orbited about the
               midpoint between them - and that needs both poses built first.
               Rotating the orientations alone left the eyes swivelling where
               they stood, which disagrees with a submitted depth layer by an
               amount that depends on depth and differs per eye. That was the
               doubling TEST 135 halved and did not close. */
            if (afr && nowValid && g_xr.afrHasResult[0] && g_xr.afrHasResult[1])
                bvr::head_overdrive_rigid(overdriveDelta, projViews[0].pose,
                                          projViews[1].pose);

            layer.space = g_xr.appSpace;
            layer.viewCount = kEyeCount;
            layer.views = projViews;
            layers[0] = reinterpret_cast<const XrCompositionLayerBaseHeader*>(&layer);
            layerCount = 1;

            /* The game's interface, keyed, over the world. Before the settings
               panel so the panel stays the topmost thing there is. */
            if (g_xr.uiOverlay.load(std::memory_order_acquire) &&
                ui_overlay_submit(context, swapChain, views, posesValid, &uiLayer))
            {
                layers[layerCount++] =
                    reinterpret_cast<const XrCompositionLayerBaseHeader*>(&uiLayer);
            }

            if (submit_overlay(context, views, &quadLayer))
                layers[layerCount++] =
                    reinterpret_cast<const XrCompositionLayerBaseHeader*>(&quadLayer);

            if (!g_xr.loggedFirstFrame)
            {
                g_xr.loggedFirstFrame = true;
                BVR_INFO("First stereo frame submitted.");
            }
        }
        else if (!g_xr.loggedFirstFrame)
        {
            BVR_WARN("Views not yet trackable (flags 0x%llX); submitting an empty frame.",
                     static_cast<unsigned long long>(viewState.viewStateFlags));
        }
    }

    /* xrEndFrame must be called for EVERY xrBeginFrame, including frames with no
       layers. Skipping it on the not-renderable path is what makes runtimes
       decide the app has stopped responding. */
    /* LAST, and after every read of the backbuffer above.
       Only during a mission: outside one the game is drawing its own interface
       into its own swapchain perfectly well, and there is no stereo world to
       mirror anyway. */
    if (g_xr.inMission.load(std::memory_order_acquire))
    {
        mirror_to_monitor(context, swapChain);
    }
    else
    {
        /* Outside a mission the game draws its own complete picture into the
           window, and clearing it would erase exactly that. */
        arm_backbuffer_clear(nullptr);
    }

    XrFrameEndInfo endInfo{ XR_TYPE_FRAME_END_INFO };

    /* HOW OLD IS THE CONTENT BEING SUBMITTED?
     *
       The pose managed rendered from was predicted for one display time; this
       frame is being submitted for another. The gap between them is the age of
       the picture, and it is the only honest measure of "not instant".
     *
       The compositor reprojects for the difference, so rotation should still
       feel immediate however large this is. What reprojection cannot recover is
       anything that moved in the world, or any translation it lacks depth for -
       so a large age reads as the world lagging the head without juddering,
       which is smooth-but-late rather than shaky.
     *
       One frame at 90 Hz is 11.1 ms. Two is 22.2. Anything near or above that is
       a pipeline depth worth attacking; a few ms is not. */
    {
        const int64_t used = g_xr.usedDisplayTime.load(std::memory_order_acquire);

        if (used > 0)
        {
            static uint64_t lastAgeLog = 0;
            static double   ageSum     = 0.0;
            static double   ageMax     = 0.0;
            static uint32_t ageCount   = 0;

            const double ageMs =
                static_cast<double>(frameState.predictedDisplayTime - used) / 1.0e6;

            ageSum += ageMs;
            if (ageMs > ageMax)
                ageMax = ageMs;
            ++ageCount;

            const uint64_t nowAge = GetTickCount64();
            if (lastAgeLog == 0)
                lastAgeLog = nowAge;
            else if (nowAge - lastAgeLog >= 1000 && ageCount > 0)
            {
                lastAgeLog = nowAge;
                BVR_INFO("Pose age: %.1f ms mean, %.1f ms worst, over %u frame(s). "
                         "One frame at 90 Hz is 11.1 ms. This is how far behind the "
                         "head the RENDERED content is; the compositor reprojects "
                         "for it, so it reads as late rather than juddery.",
                         ageSum / ageCount, ageMax, ageCount);
                ageSum = 0.0;
                ageMax = 0.0;
                ageCount = 0;
            }
        }
    }

    /* PER-AXIS FRESHNESS, BECAUSE THE COMPLAINT IS PER-AXIS.
     *
       Pose age says the content is ~14 ms old, but it says nothing about WHICH
       axis. Left-right reads as instant and up-down does not, and every theory
       for that so far has been wrong - the fov mismatch was real but symmetric,
       and disabling the pitch injection changed nothing.
     *
       So measure the two separately. This compares the pose the frame was
       RENDERED from against the pose located for this frame's display time, and
       reports the yaw and pitch components of the difference. That difference is
       what the compositor has to reproject away.
     *
       If the two are the same size, the pipeline is symmetric and the feel is
       coming from something other than latency - the vertical fov, or the
       reprojection quality on an axis where there is less to work with. If pitch
       is consistently larger, the pitch path really is losing a frame somewhere
       the yaw path is not, and that is a bug with an address. */
    if (g_xr.usedValid.load(std::memory_order_acquire) &&
        g_xr.posePublished.load(std::memory_order_acquire))
    {
        static uint64_t lastAxis = 0;
        static double   yawSum   = 0.0;
        static double   pitchSum = 0.0;
        static uint32_t axisN    = 0;

        const XrQuaternionf& a = g_xr.usedPose[0].orientation;
        const int poseIdx = g_xr.poseIndex.load(std::memory_order_acquire);
        const auto& b = g_xr.poseBuffers[poseIdx][0].orientation;

        /* Yaw and pitch of each, in the usual aerospace decomposition. Only the
           DIFFERENCE matters, so a shared convention is enough. */
        const double yawA = atan2(2.0 * (a.w * a.y + a.x * a.z),
                                  1.0 - 2.0 * (a.y * a.y + a.x * a.x));
        const double yawB = atan2(2.0 * (b.w * b.y + b.x * b.z),
                                  1.0 - 2.0 * (b.y * b.y + b.x * b.x));

        double sa = 2.0 * (a.w * a.x - a.y * a.z);
        double sb = 2.0 * (b.w * b.x - b.y * b.z);
        sa = sa > 1.0 ? 1.0 : (sa < -1.0 ? -1.0 : sa);
        sb = sb > 1.0 ? 1.0 : (sb < -1.0 ? -1.0 : sb);

        double dYaw = yawB - yawA;
        while (dYaw > 3.14159265) dYaw -= 6.28318531;
        while (dYaw < -3.14159265) dYaw += 6.28318531;

        const double dPitch = asin(sb) - asin(sa);

        yawSum   += (dYaw   < 0.0 ? -dYaw   : dYaw)   * 57.2957795;
        pitchSum += (dPitch < 0.0 ? -dPitch : dPitch) * 57.2957795;
        ++axisN;

        const uint64_t nowAxis = GetTickCount64();
        if (lastAxis == 0)
            lastAxis = nowAxis;
        else if (nowAxis - lastAxis >= 1000 && axisN > 0)
        {
            lastAxis = nowAxis;
            BVR_INFO("Pose lag by axis: yaw %.2f deg, pitch %.2f deg mean over %u "
                     "frame(s) - how far the head moved on each axis between the "
                     "render and this frame's display time. Similar numbers mean "
                     "the pipeline is symmetric and the vertical feel is not "
                     "latency.", yawSum / axisN, pitchSum / axisN, axisN);
            yawSum = 0.0;
            pitchSum = 0.0;
            axisN = 0;
        }
    }
    endInfo.displayTime = frameState.predictedDisplayTime;
    endInfo.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    endInfo.layerCount = layerCount;
    endInfo.layers = layerCount > 0 ? layers : nullptr;
    check(xrEndFrame(g_xr.session, &endInfo), "xrEndFrame");

    ++g_xr.frameCount;

    /* Render-thread heartbeat, on a clock rather than a frame count.
     *
     * Paired with the managed heartbeat this says WHICH thread died. If these
     * lines continue past the last managed one, the main thread went down and
     * the render thread is still turning; if they stop first, the crash is here,
     * in the Present hook or the OpenXR frame loop. Guessing between the two
     * costs a game restart per attempt. */
    static uint64_t lastBeat = 0;
    const uint64_t now = GetTickCount64();
    if (now - lastBeat >= 1000)
    {
        lastBeat = now;
        BVR_INFO("Present heartbeat: %llu frames, copy %s.",
                 static_cast<unsigned long long>(g_xr.frameCount),
                 g_xr.copyUsable ? "on" : "off");

        if (g_xr.afrHasResult[0] || g_xr.afrHasResult[1])
        {
            BVR_INFO("AFR held-image age this second: L max %u ms, R max %u ms "
                     "(pose lag %d generation(s)).",
                     g_xr.afrAgeMaxMs[0], g_xr.afrAgeMaxMs[1], afr_pose_lag());
            g_xr.afrAgeMaxMs[0] = 0;
            g_xr.afrAgeMaxMs[1] = 0;
        }

        /* How much rotation the finished image is carrying. Beside the held-age
           line on purpose: together they say what each eye is and how far it
           had to be moved to be current. */
        bvr::image_latch_report();
        bvr::head_overdrive_report();
        bvr::head_predict_report();

        /* Peak turn correction over the same second. Zero while standing
           still is correct and expected; the number that matters is what it
           reads while sweeping the mouse, because that is the disparity
           that would otherwise have gone to the headset uncorrected. */
        if (turn_reprojection() != 0.0f)
        {
            BVR_INFO("AFR turn correction: peak %.2f deg this second "
                     "(sign %+.0f).", g_xr.turnPeakDeg, turn_reprojection());
            g_xr.turnPeakDeg = 0.0f;

            if (move_reprojection() != 0.0f)
            {
                BVR_INFO("AFR move correction: peak %.1f mm this second (sign %+.0f). "
                         "This is how far the GAME camera travelled between the two "
                         "eyes of a pair - anything approaching the 65 mm IPD is a "
                         "false disparity the size of the real one.",
                         g_xr.movePeakMm, move_reprojection());
            }
            g_xr.movePeakMm = 0.0f;

            if (g_depthLayerReady)
            {
                BVR_INFO("Depth layer: %u eye(s) submitted with depth this second "
                         "(%s). Two per frame is healthy; zero means the depth is "
                         "being dropped between capture and submit.",
                         g_xr.depthSubmitted,
                         afw_depth_is_reversed() ? "reversed-Z" : "conventional Z");
            }
            g_xr.depthSubmitted = 0;
        }

        afw_log_second();

        if (g_xr.afwFallbacks > 0)
        {
            BVR_WARN("AFW: fell back to AFR on %u frame(s) this second - the warp "
                     "could not run, so those frames showed a held eye.",
                     g_xr.afwFallbacks);
            g_xr.afwFallbacks = 0;
        }

        /* Pairing, when it is on. promotions should sit at about half the
           frame rate and forced should be ZERO - a non-zero forced count means
           the generations are not lining up and the safety is carrying the
           mode, which is worth knowing because the artefact will still be
           there and the sim rate is being paid for nothing. */
        if (g_xr.simGen.load(std::memory_order_acquire) >= 0)
        {
            BVR_INFO("AFR pairing: %u pair(s) promoted, %u capture(s) held back, "
                     "%u forced. Forced should be 0.",
                     g_xr.afrPromotions, g_xr.afrHeldForPairing,
                     g_xr.afrForcedPromotions);
            g_xr.afrPromotions = 0;
            g_xr.afrHeldForPairing = 0;
            g_xr.afrForcedPromotions = 0;
        }

        /* Full AFR, which is readable in one number: how many pairs reached the
           headset in a second. That IS the per-eye refresh rate, and unlike
           every other mode it is also the display rate, because both halves are
           real. Deferred should track it one for one - each pair costs exactly
           one deferred Present. Pairs well below the headset's rate mean the
           engine cannot produce two renders per display period, which is the
           one cost this mode has and the only thing worth measuring about it. */
        if (g_xr.fullAfr.load(std::memory_order_acquire))
        {
            BVR_INFO("Full AFR: %u pair(s) submitted this second, %u Present(s) "
                     "deferred. Pairs are BOTH eyes really rendered, so that "
                     "number is the per-eye refresh rate - compare it against the "
                     "headset's. Deferred should equal pairs.",
                     g_xr.afrPairsSubmitted, g_xr.afrDeferred);
            g_xr.afrPairsSubmitted = 0;
            g_xr.afrDeferred = 0;
        }

        /* Off unless asked for: Map(READ) on the immediate context forces a GPU
           sync from inside the Present hook, which is its own hazard and would
           confound the very crash we are chasing. BVR_PIXEL_PROBE=1 to enable. */
        static int probe = -1;
        if (probe < 0)
            probe = config_bool("pixel_probe", false) ? 1 : 0;

        if (probe == 1 && g_xr.copyUsable)
            sample_eye_pixel(context, eye_texture(0));
    }
}

} // namespace

// ---------------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------------

int32_t xr_create_instance()
{
    if (g_xr.instance != XR_NULL_HANDLE)
        return BVR_OK;

    if (!has_d3d11_extension())
        return BVR_NO_RUNTIME;

    /* Depth is asked for only when the runtime offers it AND the config allows
       it, and its absence is never fatal - a missing depth layer costs the held
       eye its positional correction and nothing else. Enabling an extension the
       runtime does not expose fails xrCreateInstance outright, which would take
       the whole mod down over an optimisation. */
    const bool wantDepth = g_depthExtAvailable && depth_layer_allowed();

    const char* extensions[2] = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME, nullptr };
    uint32_t extensionCount = 1;

    if (wantDepth)
        extensions[extensionCount++] = XR_KHR_COMPOSITION_LAYER_DEPTH_EXTENSION_NAME;

    XrInstanceCreateInfo info{ XR_TYPE_INSTANCE_CREATE_INFO };
    info.enabledExtensionCount = extensionCount;
    info.enabledExtensionNames = extensions;
    /* Request 1.0 rather than XR_CURRENT_API_VERSION: the headers are 1.1 but
       several shipping runtimes still cap at 1.0, and we use nothing 1.1-only. */
    info.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    _snprintf_s(info.applicationInfo.applicationName, _TRUNCATE, "BannerlordVR");
    _snprintf_s(info.applicationInfo.engineName, _TRUNCATE, "TaleWorlds rgl");
    info.applicationInfo.applicationVersion = 1;
    info.applicationInfo.engineVersion = 1;

    const XrResult r = xrCreateInstance(&info, &g_xr.instance);
    if (XR_FAILED(r))
    {
        BVR_ERR("xrCreateInstance failed (%d). Is an OpenXR runtime installed and active?",
                static_cast<int>(r));
        g_xr.instance = XR_NULL_HANDLE;
        return BVR_NO_RUNTIME;
    }

    g_depthExtEnabled = wantDepth;

    if (wantDepth)
        BVR_INFO("%s enabled. The compositor can now reproject a held eye for head "
                 "MOVEMENT as well as rotation, once the depth swapchains are up.",
                 XR_KHR_COMPOSITION_LAYER_DEPTH_EXTENSION_NAME);
    else if (!g_depthExtAvailable)
        BVR_INFO("Runtime does not expose %s, so a held AFR eye can only be "
                 "reprojected for rotation. Near geometry will shift slightly "
                 "against far geometry while the head moves.",
                 XR_KHR_COMPOSITION_LAYER_DEPTH_EXTENSION_NAME);
    else
        BVR_INFO("Depth layer declined by the config (depth_layer = 0); "
                 "reprojection stays rotation-only.");

    XrInstanceProperties instProps{ XR_TYPE_INSTANCE_PROPERTIES };
    if (XR_SUCCEEDED(xrGetInstanceProperties(g_xr.instance, &instProps)))
        BVR_INFO("Runtime: %s", instProps.runtimeName);

    XrSystemGetInfo systemInfo{ XR_TYPE_SYSTEM_GET_INFO };
    systemInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    if (XR_FAILED(xrGetSystem(g_xr.instance, &systemInfo, &g_xr.systemId)))
    {
        BVR_WARN("No HMD available. Runtime is present but no headset is connected.");
        xrDestroyInstance(g_xr.instance);
        g_xr.instance = XR_NULL_HANDLE;
        return BVR_NO_HEADSET;
    }

    /* ZERO, not kEyeCount. This is the two-call idiom and the capacity argument
     * is a PROMISE about the array that follows it.
     *
     * It said kEyeCount while passing nullptr for the array - "here are two
     * elements you may fill in", followed by no array at all. The spec is
     * explicit that the runtime ignores the pointer only when the capacity is
     * zero; given a non-zero capacity it is entitled to write, and writing two
     * XrViewConfigurationView structs to address zero is an access violation
     * inside the runtime.
     *
     * VirtualDesktopXR happens to null-check and had worked for months, which is
     * exactly what makes this kind of mistake survive: it is not a bug that
     * shows up on the machine it was written on. SteamVR does not null-check,
     * and the log ends on the line immediately before this call - the last thing
     * printed is the runtime name, and then nothing.
     *
     * Every other enumerate in this file already passes zero here. This one was
     * the only one that did not. */
    uint32_t viewCount = 0;
    if (!check(xrEnumerateViewConfigurationViews(g_xr.instance, g_xr.systemId, kViewConfig,
                                                 0, &viewCount, nullptr),
               "xrEnumerateViewConfigurationViews(count)"))
        return BVR_XR_ERROR;

    /* The runtime is asked how many views this configuration has and the answer
       is compared rather than assumed. Stereo is two, and the whole mod is
       built on that - eye arrays, swapchains, the AFR pair - so a runtime
       reporting anything else has to be refused here rather than allowed to
       overrun a two-element array further down. */
    if (viewCount != kEyeCount)
    {
        BVR_ERR("Runtime reports %u views for the stereo configuration, not %u. "
                "This mod is built for two eyes; staying in flat mode.",
                viewCount, kEyeCount);
        return BVR_XR_ERROR;
    }

    for (uint32_t i = 0; i < kEyeCount; ++i)
        g_xr.viewConfigs[i] = { XR_TYPE_VIEW_CONFIGURATION_VIEW };

    if (!check(xrEnumerateViewConfigurationViews(g_xr.instance, g_xr.systemId, kViewConfig,
                                                 kEyeCount, &viewCount, g_xr.viewConfigs),
               "xrEnumerateViewConfigurationViews"))
        return BVR_XR_ERROR;

    if (XR_FAILED(xrGetInstanceProcAddr(
            g_xr.instance, "xrGetD3D11GraphicsRequirementsKHR",
            reinterpret_cast<PFN_xrVoidFunction*>(&g_getD3D11Requirements))))
    {
        BVR_ERR("xrGetD3D11GraphicsRequirementsKHR unavailable despite the extension being enabled.");
        return BVR_XR_ERROR;
    }

    BVR_INFO("System ready. Recommended per-eye render target: %ux%u (max %ux%u).",
             g_xr.viewConfigs[0].recommendedImageRectWidth,
             g_xr.viewConfigs[0].recommendedImageRectHeight,
             g_xr.viewConfigs[0].maxImageRectWidth,
             g_xr.viewConfigs[0].maxImageRectHeight);

    apply_render_scale();
    return BVR_OK;
}

int32_t xr_request_session()
{
    if (g_xr.instance == XR_NULL_HANDLE)
        return BVR_NOT_INITIALIZED;

    g_xr.sessionRequested = true;
    return g_xr.session != XR_NULL_HANDLE ? BVR_OK : BVR_SESSION_NOT_READY;
}

void xr_tick(IDXGISwapChain* swapChain, ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (g_xr.instance == XR_NULL_HANDLE || device == nullptr || context == nullptr)
        return;

    if (g_xr.session == XR_NULL_HANDLE)
    {
        if (!g_xr.sessionRequested || g_xr.sessionCreateFailed)
            return;

        if (!create_session(device))
        {
            /* One attempt only. Retrying every Present would flood the log at
               frame rate and hammer the runtime. */
            g_xr.sessionCreateFailed = true;
            destroy_session();
            BVR_ERR("Session creation failed; staying in flat mode for this run.");
            return;
        }
    }

    poll_events();

    if (g_xr.sessionRunning)
        render_frame(context, swapChain);
}

bool xr_recommended_size(uint32_t* width, uint32_t* height)
{
    if (g_xr.instance == XR_NULL_HANDLE || width == nullptr || height == nullptr)
        return false;

    *width = g_xr.viewConfigs[0].recommendedImageRectWidth;
    *height = g_xr.viewConfigs[0].recommendedImageRectHeight;
    return *width > 0 && *height > 0;
}

int32_t xr_current_afr_eye()
{
    return g_xr.afrEye.load(std::memory_order_acquire);
}

int32_t xr_set_afr_eye(int32_t eye)
{
    if (eye < -1 || eye >= static_cast<int32_t>(kEyeCount))
        return BVR_NOT_INITIALIZED;

    /* A new index supersedes the last one, used or not. */
    g_xr.afrEyeUsed.store(false, std::memory_order_release);

    const int previous = g_xr.afrEye.exchange(eye, std::memory_order_release);

    if (previous < 0 && eye >= 0)
        BVR_INFO("AFR enabled; one scene render per frame, alternating eyes.");
    else if (previous >= 0 && eye < 0)
    {
        /* EVERY HELD EYE IS NOW STALE AND MUST NOT BE SUBMITTED AGAIN.
         *
           Standing down used to leave them alone, which was survivable only
           because the renderer alternated: whatever a new mission inherited
           was overwritten within a frame or two. Pinning the source eye
           removed that guarantee - the synthesised eye is written ONLY by the
           warp, so any frame the warp declines leaves the previous mission's
           picture sitting in it, and afrHasResult still says it is good. That
           is edges from the last mission stuck on the screen after a reload.

           Flagged rather than done here; the render thread owns these. */
        g_xr.afrForget.store(true, std::memory_order_release);

        /* AND THE DEPTH BUFFER, WHICH IS THE ONE THAT ACTUALLY BIT.
         *
           The depth search switches itself off after 180 stable frames and
           holds one texture for the rest of the session. That is fine until
           rgl rebuilds its render targets, at which point we are reading a
           buffer nothing writes to any more - and a frozen depth map is not a
           subtle error. Every disparity comes from geometry that is no longer
           there, so everything doubles, and the holes it opens sit at fixed
           screen positions and look welded to the view. That is edges stuck
           on screen after reloading a mission.

           A scene change is the signal we can actually observe. The obvious
           alternative - watching for an eye-sized render target being
           recreated - was tried and does not fire: that watch only sees the
           COLOUR target, which this mod allocates itself and rgl therefore
           never rebuilds. Nothing in two full session logs matched it.

           Re-opening the search costs a couple of seconds of surveying at the
           start of each mission, which is what it already pays on the first
           one. */
        depth_rearm("the scene is being torn down");
        BVR_INFO("AFR disabled; every held eye is dropped so nothing from this "
                 "mission can be submitted into the next one.");
    }

    return BVR_OK;
}

int32_t xr_afr_eye_consumed()
{
    return g_xr.afrEyeUsed.load(std::memory_order_acquire) ? 1 : 0;
}

int32_t xr_next_afr_eye()
{
    const int pinned = g_xr.afrPinnedEye.load(std::memory_order_acquire);
    const int eye = pinned >= 0
                        ? pinned
                        : g_xr.afrNextEye.load(std::memory_order_acquire);

    /* The tick is about to configure the camera for this eye, so this is what
       the next captured frame will actually have been drawn with. The capture
       labels from here rather than from the sequence counter - see
       XrState::afrCameraEye. */
    g_xr.afrCameraEye.store(eye, std::memory_order_release);
    return eye;
}

int32_t xr_pin_afr_eye(int32_t eye)
{
    if (eye < -1 || eye >= static_cast<int32_t>(kEyeCount))
        return BVR_NOT_INITIALIZED;

    g_xr.afrPinnedEye.store(eye, std::memory_order_release);
    if (eye >= 0)
    {
        g_xr.afrNextEye.store(eye, std::memory_order_release);

        /* AND THE LABEL (TEST 173). The capture labels from afrCameraEye, which
           only xr_next_afr_eye wrote - and the pinned tick never calls it. So a
           pin inherited whichever eye the last ALTERNATING frame asked for: a
           coin flip at the moment the warp went live. Landing on the wrong side
           submitted the left-eye render as the right eye, signed the warp
           backwards, and looked up the late latch under the eye it never wrote
           - "0 capture(s) labelled", so the compositor rotated every frame a
           second time. That is AFW's floating. Release (-1) leaves it alone, so
           AFR is untouched. */
        g_xr.afrCameraEye.store(eye, std::memory_order_release);
    }

    return BVR_OK;
}

int32_t xr_set_body_yaw(float yawRadians)
{
    /* NaN check written as a self-comparison because that is the one form no
       compiler turns into a constant. A NaN here would poison every subsequent
       delta and the correction would rotate the held eye to somewhere the
       world has never been. */
    if (!(yawRadians == yawRadians))
        return BVR_NOT_INITIALIZED;

    g_xr.bodyYaw.store(yawRadians, std::memory_order_release);
    g_xr.bodyYawValid.store(true, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_used_views(const BvrEyeView* views, int64_t displayTime)
{
    if (views == nullptr)
        return BVR_NOT_INITIALIZED;

    for (uint32_t e = 0; e < kEyeCount; ++e)
    {
        const BvrEyeView& v = views[e];

        /* A NaN here would be written straight into a submitted pose, which a
           runtime rejects rather than merely draws wrong. Self-comparison
           because it is the one form no compiler folds away. */
        if (!(v.orientation.x == v.orientation.x) || !(v.orientation.y == v.orientation.y) ||
            !(v.orientation.z == v.orientation.z) || !(v.orientation.w == v.orientation.w) ||
            !(v.position.x == v.position.x) || !(v.position.y == v.position.y) ||
            !(v.position.z == v.position.z))
            return BVR_NOT_INITIALIZED;
    }

    for (uint32_t e = 0; e < kEyeCount; ++e)
    {
        g_xr.usedPose[e].orientation.x = views[e].orientation.x;
        g_xr.usedPose[e].orientation.y = views[e].orientation.y;
        g_xr.usedPose[e].orientation.z = views[e].orientation.z;
        g_xr.usedPose[e].orientation.w = views[e].orientation.w;
        g_xr.usedPose[e].position.x    = views[e].position.x;
        g_xr.usedPose[e].position.y    = views[e].position.y;
        g_xr.usedPose[e].position.z    = views[e].position.z;
    }

    /* The display time this pose was PREDICTED for. Compared at submit against
       the frame being submitted, it gives the age of the content in ms - the
       one number that says whether "not instant" is real latency or not. */
    g_xr.usedDisplayTime.store(displayTime, std::memory_order_release);
    g_xr.usedValid.store(true, std::memory_order_release);
    return BVR_OK;
}

bool xr_get_used_views(BvrEyeView* outViews)
{
    if (outViews == nullptr || !g_xr.usedValid.load(std::memory_order_acquire))
        return false;

    for (uint32_t e = 0; e < kEyeCount; ++e)
    {
        const XrPosef& p = g_xr.usedPose[e];
        outViews[e].orientation = BvrQuat{ p.orientation.x, p.orientation.y,
                                           p.orientation.z, p.orientation.w };
        outViews[e].position = BvrVec3{ p.position.x, p.position.y, p.position.z };
        outViews[e].fov = BvrFov{ 0.0f, 0.0f, 0.0f, 0.0f };
    }
    return true;
}

void xr_set_latched_orientation(uint32_t eye, const BvrQuat& orientation)
{
    if (eye >= kEyeCount)
        return;

    /* A write that finds the slot still unconsumed is counted (the Display
       pacing line): ~0 in steady play, a burst for a second around a mode
       switch. TEST 177. */
    g_xr.latchOri[eye] = orientation;
    if (g_xr.latchHas[eye].exchange(true, std::memory_order_acq_rel))
        g_xr.latchOverwrites.fetch_add(1, std::memory_order_relaxed);
}

uint64_t xr_latched_captures()
{
    return g_xr.latchCaptures.exchange(0, std::memory_order_relaxed);
}

int32_t xr_set_body_target(float yawRadians, float x, float y, float z)
{
    /* Same self-comparison NaN guard as its siblings: this value ends up added
       to a submitted pose, and a pose carrying a NaN is a layer the runtime
       rejects outright rather than an image that merely looks wrong. */
    if (!(yawRadians == yawRadians) || !(x == x) || !(y == y) || !(z == z))
        return BVR_NOT_INITIALIZED;

    g_xr.targetYaw.store(yawRadians, std::memory_order_release);
    g_xr.targetPosX.store(x, std::memory_order_release);
    g_xr.targetPosY.store(y, std::memory_order_release);
    g_xr.targetPosZ.store(z, std::memory_order_release);
    g_xr.targetValid.store(true, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_body_position(float x, float y, float z)
{
    /* Same self-comparison NaN guard as the yaw, and for a worse reason: a NaN
       here would be ADDED to a submitted pose position, and a pose with a NaN in
       it is not a wrong image, it is a runtime rejecting the layer. */
    if (!(x == x) || !(y == y) || !(z == z))
        return BVR_NOT_INITIALIZED;

    g_xr.bodyPosX.store(x, std::memory_order_release);
    g_xr.bodyPosY.store(y, std::memory_order_release);
    g_xr.bodyPosZ.store(z, std::memory_order_release);
    g_xr.bodyPosValid.store(true, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_sim_generation(int32_t generation)
{
    /* Negative means "no generation", which switches pairing off and sends
       every capture straight to the screen. That is the path sync_sequential
       = 0 takes, and it is also what a stand-down leaves behind. */
    g_xr.simGen.store(generation, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_full_afr(int32_t active)
{
    const bool on = active != 0;
    const bool was = g_xr.fullAfr.exchange(on, std::memory_order_acq_rel);

    if (was != on)
    {
        /* A mode change must never leave a half-built pair behind it. Turning
           full AFR OFF while a frame was deferred would strand that frame: the
           deferring branch is gone, so nothing would hand it to xrEndFrame, and
           the runtime would be waiting for a frame that no code path still
           knows it began. Clearing the run counter is not enough - the frame is
           already begun and framePrepared already true, which is precisely the
           state Present consumes and submits, so the next one ends it normally.
           This just makes sure the next pair starts counting from zero. */
        g_xr.afrDeferRun = 0;

        BVR_INFO("Full AFR %s. %s", on ? "on" : "off",
                 on ? "One eye per game frame, two game frames per display frame: "
                      "both eyes are real renders of one simulation tick, and the "
                      "engine is now asked for twice the frame rate."
                    : "Back to one eye per display frame with the other held or "
                      "warped.");
    }

    return BVR_OK;
}

int32_t xr_repin_flat_screen()
{
    /* Drops the pin only. The next frame that shows the screen places it in
       front of wherever the head is looking THEN, which is what someone who
       has just pressed recentre is asking for. */
    g_xr.flatPosePinned = false;
    g_xr.flatSettleFrames = 0;
    return BVR_OK;
}

int32_t xr_set_screen_geometry(float width, float distance)
{
    const float w = clampf(width, 0.2f, 20.0f);
    const float d = clampf(distance, 0.3f, 20.0f);

    /* WIDTH IS LIVE, DISTANCE NEEDS A RE-PIN.
     *
     * The quad's size is read every frame, so widening it takes immediately.
     * Its POSITION is not: the pose is computed once, when the screen is placed,
     * from the distance at that moment - so moving the slider would otherwise
     * change a number nothing reads again until the next mission. Dropping the
     * pin makes the next frame place it afresh, at the new distance, in front of
     * wherever the head is now. */
    if (fabsf(d - g_screenDistance.load(std::memory_order_acquire)) > 1e-3f)
        g_xr.flatPosePinned = false;

    g_screenWidth.store(w, std::memory_order_release);
    g_screenDistance.store(d, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_ui_geometry(float width, float distance)
{
    /* Clamped here rather than trusted: these arrive from a slider, and a
       zero-width quad is a divide by zero in the aspect calculation. */
    g_uiWidth.store(clampf(width, 0.2f, 20.0f), std::memory_order_release);
    g_uiDistance.store(clampf(distance, 0.3f, 20.0f), std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_in_mission(int32_t active)
{
    g_xr.inMission.store(active != 0, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_ui_overlay(int32_t active)
{
    g_xr.uiOverlay.store(active != 0, std::memory_order_release);
    return BVR_OK;
}

int32_t xr_set_mission_active(int32_t active)
{
    const bool wanted = active != 0;
    const bool was = g_xr.missionActive.exchange(wanted, std::memory_order_release);

    if (was != wanted)
    {
        BVR_INFO(wanted
                     ? "Mission active: the headset is on the stereo path."
                     : "No mission: the headset is on the flat screen, showing the "
                       "game's own image on a quad.");
    }

    return BVR_OK;
}

bool xr_mission_active()
{
    return g_xr.missionActive.load(std::memory_order_acquire);
}

/* mode: 0 = off (plain AFR), 1 = AFW, 2 = AFW+AFR hybrid.
 *
 * One int rather than a second export, because the three are one decision and
 * a caller cannot express a contradiction with it. Anything above 2 is treated
 * as the hybrid rather than refused: a stale managed assembly against a newer
 * DLL should degrade to a mode that renders, not to a black eye. */
int32_t xr_set_afw(int32_t mode, float focalPx, float baseline,
                   float zNear, float zFar)
{
    const bool wantAfw    = mode != 0;
    const bool wantHybrid = mode >= 2;
    /* Geometry first, and REGARDLESS of the mode. The reticle needs the focal
       length and the baseline to place itself at a finite distance, and it is
       drawn in AFR too - where the warp that this was written for never runs.
       afw_set_params rejects anything not usable, so passing it unconditionally
       costs nothing. */
    AfwParams p = {};
    p.focalPx = focalPx;
    p.baseline = baseline;
    p.zNear = zNear;
    p.zFar = zFar;
    afw_set_params(p);

    if (wantAfw && !afw_allowed())
    {
        BVR_WARN("AFW was selected but afw = 0 in the config, so the hold "
                 "textures were not allocated for it. Staying on AFR.");
        return BVR_SESSION_NOT_READY;
    }

    /* The hybrid flag is set BEFORE the enable, so a frame that samples them
       between the two writes sees the old mode entirely rather than AFW running
       with the hybrid's answer to whether the head is still. */
    const bool wasHybrid = g_xr.afwHybrid.exchange(wantHybrid, std::memory_order_release);
    const bool was = g_xr.afwEnabled.exchange(wantAfw, std::memory_order_release);

    if (was != wantAfw || wasHybrid != wantHybrid)
    {
        if (!wantAfw)
            BVR_INFO("Stereo mode: AFR. One render per frame, alternating eyes; "
                     "the eye that was not drawn is held from its own last real "
                     "render and submitted with the pose it was drawn at.");
        else if (!wantHybrid)
            BVR_INFO("Stereo mode: AFW. One render per frame, and the other eye is "
                     "SYNTHESISED from it through its depth buffer. Both eyes come "
                     "from one instant, so nothing moves between them - at the cost "
                     "of invented pixels where a near edge hid something.");
        else
            BVR_INFO("Stereo mode: AFW+AFR hybrid. While the head is still the "
                     "second eye is its own last REAL render, which is exact and "
                     "beats any warp; once it moves it is warped from the rendered "
                     "eye, which is time-correct. Each mode covers the other's "
                     "failure. The seam is a change of character in the second eye "
                     "at the threshold - that is the trade, and it is why this is "
                     "its own mode rather than something AFW does quietly.");
    }

    return BVR_OK;
}

int32_t xr_session_state()
{
    return static_cast<int32_t>(g_xr.sessionState);
}

bool xr_instance_ready()
{
    return g_xr.instance != XR_NULL_HANDLE;
}

bool xr_prepare_render_frame()
{
    if (!pre_render_frame_enabled())
        return false;
    if (!g_xr.sessionRunning || g_xr.session == XR_NULL_HANDLE)
        return false;

    {
        /* One frame in flight. If Present has not consumed the last one - a
           frame the engine never finished, a loading screen, the camera being
           built twice - beginning another would be an unmatched xrBeginFrame,
           which the runtime is entitled to fail and some do worse than that. */
        std::lock_guard<std::mutex> guard(g_xr.frameMutex);
        if (g_xr.framePrepared)
            return false;
    }

    /* xrWaitFrame BLOCKS until the compositor wants the next frame, and being
       blocked here is the point: this is the game's own thread, at the top of
       the camera build, so the engine is paced by the headset instead of
       racing ahead and being throttled later by a Present it cannot see. */
    XrFrameWaitInfo waitInfo{ XR_TYPE_FRAME_WAIT_INFO };
    XrFrameState    state{ XR_TYPE_FRAME_STATE };
    if (XR_FAILED(xrWaitFrame(g_xr.session, &waitInfo, &state)))
        return false;

    XrFrameBeginInfo beginInfo{ XR_TYPE_FRAME_BEGIN_INFO };
    if (XR_FAILED(xrBeginFrame(g_xr.session, &beginInfo)))
        return false;

    XrViewLocateInfo locateInfo{ XR_TYPE_VIEW_LOCATE_INFO };
    locateInfo.viewConfigurationType = kViewConfig;
    locateInfo.displayTime = state.predictedDisplayTime;
    locateInfo.space = g_xr.appSpace;

    XrViewState viewState{ XR_TYPE_VIEW_STATE };
    XrView      views[kEyeCount] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    uint32_t    viewCount = 0;

    const XrResult lr = xrLocateViews(g_xr.session, &locateInfo, &viewState,
                                      kEyeCount, &viewCount, views);

    const bool posesValid =
        XR_SUCCEEDED(lr) && viewCount >= kEyeCount &&
        (viewState.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) != 0 &&
        (viewState.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT) != 0;

    {
        std::lock_guard<std::mutex> guard(g_xr.frameMutex);
        g_xr.preparedState    = state;
        g_xr.preparedViews[0] = views[0];
        g_xr.preparedViews[1] = views[1];
        g_xr.preparedValid    = posesValid;
        g_xr.framePrepared    = true;
    }

    /* Published for the camera the caller is about to build. NO LEAD, and that
       is the whole difference: this pose is already for the display time of the
       frame about to be drawn, which is the thing pose_lead_frames could only
       approximate by predicting. Leading it again would hand the engine a pose
       a frame INTO the future.

       pose_lead_frames still applies on the fallback path in Present, where it
       is still right - so it stays as it is rather than being turned off. */
    if (posesValid)
        publish_views(views, state.predictedDisplayTime);

    static bool logged = false;
    if (!logged)
    {
        logged = true;
        BVR_INFO("Frame begun BEFORE the render: xrWaitFrame, xrBeginFrame and "
                 "xrLocateViews now run at the top of the camera build, so the "
                 "engine draws with a pose located for the display time of the "
                 "frame it is drawing - not one located a frame earlier for a "
                 "moment that has passed. Pose age should sit near zero without "
                 "prediction, and pose_lead_frames now applies only on the "
                 "fallback path in Present. pre_render_frame = 0 "
                 "puts Wait and Begin back in the Present hook.");
    }

    return true;
}

bool xr_get_predicted_views(BvrEyeView* outViews, int64_t* outDisplayTime)
{
    if (outViews == nullptr || outDisplayTime == nullptr)
        return false;
    if (!g_xr.sessionRunning)
        return false;

    /* Reporting success with the zero-initialised buffer is worse than reporting
       failure: the caller has no way to tell it apart from a real pose. */
    if (!g_xr.posePublished.load(std::memory_order_acquire))
        return false;

    const int index = g_xr.poseIndex.load(std::memory_order_acquire);
    std::memcpy(outViews, g_xr.poseBuffers[index], sizeof(BvrEyeView) * kEyeCount);
    *outDisplayTime = g_xr.poseTimes[index];
    return true;
}

void xr_shutdown()
{
    destroy_session();
    release_afr_results();

    if (g_xr.instance != XR_NULL_HANDLE)
    {
        xrDestroyInstance(g_xr.instance);
        g_xr.instance = XR_NULL_HANDLE;
    }

    g_xr.systemId = XR_NULL_SYSTEM_ID;
    g_xr.sessionRequested = false;
    g_xr.sessionCreateFailed = false;
    g_xr.loggedFirstFrame = false;
    g_getD3D11Requirements = nullptr;

    BVR_INFO("OpenXR shut down after %llu frames.",
             static_cast<unsigned long long>(g_xr.frameCount));
}

} // namespace bvr
