/* =============================================================================
 * vr_overlay - an in-headset settings panel, drawn with ImGui and submitted as
 * an OpenXR quad composition layer.
 *
 * WHY A QUAD LAYER AND NOT A GAME UI
 *
 * The headset sees exactly one thing: the scene view that AFR retargets into the
 * eye texture. Gauntlet draws into the game window's own layers, so a menu built
 * the game's way is perfectly functional and completely invisible from inside the
 * headset. The only two ways to put pixels in front of the player are to draw
 * into the eye texture itself, or to hand the compositor a second layer.
 *
 * A layer is the right one. Drawing into the eye texture would put the panel at
 * the same pixel coordinates in both eyes, which the brain reads as infinitely
 * far away - legible, but a headache to look at up close. A quad layer has a real
 * position and size in metres, so it sits at a comfortable arm's length with
 * correct stereo, and the compositor samples it at its own resolution rather than
 * through our eye texture's.
 *
 * THE MOUSE
 *
 * The panel is driven with the desktop mouse, because that is what the player
 * already has in their hand. The game window's client-relative cursor position
 * maps to the panel's own coordinates, so moving the mouse moves a cursor across
 * the panel in the headset. This is the same shape as the reference project's
 * "laser to virtual mouse", with the desktop mouse standing in for the laser
 * until motion controllers are wired up.
 * ========================================================================== */
#ifndef BVR_VR_OVERLAY_H
#define BVR_VR_OVERLAY_H

#include <stdint.h>

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11Texture2D;

namespace bvr {

/* Values the panel edits. The managed side owns what they MEAN - it is the half
   that can apply a world scale and write the config file - so this is a mirror
   that both sides keep in step, not the authority. */
struct OverlaySettings
{
    float worldScale = 1.0f;
    float renderScale = 0.9f;

    /* 0 = alternate-frame rendering, 1 = native stereo. A mode rather than two
       independent toggles: they are the same decision seen twice, and a UI that
       lets both be off has to invent an answer for what renders then. */
    int32_t stereoMode = 0;

    /* Set by the managed side when a mode has been asked for but the scene has
       not restarted yet, so the panel can say so instead of appearing inert. */
    int32_t stereoPending = 0;

    /* --- motion controller calibration ----------------------------------

       All of these are pushed by the managed half, which is the only one that
       can see a skeleton or a controller pose. The panel draws them and sends
       back a COMMAND - it asks for a calibration rather than performing one,
       because the work happens on the game thread against a live agent and the
       panel runs on the render thread. */

    int32_t calibState = 0;      /* BvrCalibState: 0 idle, 1 auto, 2 manual */
    int32_t calibHand = 0;       /* 0 = weapon hand, 1 = shield hand */
    float calibProgress = 0.0f;  /* 0..1 while the automatic sample runs */
    float calibSpread = 0.0f;    /* degrees of wobble in the last auto sample */
    int32_t calibHave = 0;       /* bit 0 weapon hand, bit 1 shield hand */

    /* The manual adjustment for the hand in calibHand: metres and degrees, the
       six numbers a slider can show. See bvr_api.h. */
    float calibX = 0.0f;
    float calibY = 0.0f;
    float calibZ = 0.0f;
    float calibYaw = 0.0f;
    float calibPitch = 0.0f;
    float calibRoll = 0.0f;

    /* Poll direction only. BvrCalibCommand; cleared as it is taken, so one
       click is one request rather than one per frame the panel is open. */
    int32_t calibCommand = 0;

    /* Two-way toggles. STATE rather than events, so unlike calibCommand these
       are not cleared on read - the panel and the managed side each hold the
       same answer and either may change it. */
    int32_t hideBody = 1;

    /* Hands only (TEST 299): only has an effect while hideBody is on - keeps
       the hand mesh(es) drawn alongside the weapon instead of hiding
       everything but the weapon. Two-way, like hideBody. */
    int32_t handsOnly = 0;

    int32_t motionHands = 0;

    /* Physical collision combat: the mod's own hits, blocks and arrow blocks.
       On by default; off leaves the hands and weapons posed and the game's own
       trigger attacks in charge. Two-way, like the two above. */
    int32_t physicalCombat = 1;

    /* The full-body IK (TEST 253-257): the pelvis and torso follow the player,
       the legs are solved to the game's own gait, and body_anim decides how
       much animation is left. Off reverts to the normal animated body.
       Two-way, like the toggles above. */
    int32_t bodyIk = 1;

    /* Motion-control combat (TEST 245-252): swings drive the game's attack
       direction, a raised hand is the guard, the blade lands the damage. On by
       default; off is the game's own trigger-and-stick combat with every
       animation playing. Two-way, like the toggles above. */
    int32_t motionMelee = 1;

    /* Friendly fire (TEST 261): whether a physical swing can land on friends
       and neutrals. Off by default - the vanilla single-player rule. Two-way,
       like the toggles above; the managed side refuses it on missions that
       forbid friendly fire. */
    int32_t friendlyFire = 0;

    /* Which controller the grip is saved for; index into kControllers in
       vr_overlay.cpp. Two-way, like the toggles above. */
    int32_t gripController = 0;

    /* Body holsters (TEST 271). Two-way, like the toggles above. */
    int32_t holster = 1;

    /* The campaign map in VR (map_stereo). Two-way. */
    int32_t mapVr = 0;

    /* The VR tab (TEST 293). Two-way offsets; recenter is a one-shot. */
    float   headUp = 0.0f;
    float   headForward = 0.0f;
    float   bodyTurnDeg = 90.0f;
    int32_t recenter = 0;

    /* Your height (TEST 356). playerHeight two-way (0 = the game's size),
       heightMeasure a one-shot like recenter, bodyScale push-only. */
    float   playerHeight = 0.0f;
    int32_t heightMeasure = 0;
    float   bodyScale = 1.0f;

    /* The wrist HUD's width in metres (TEST 377e). Two-way; the panel applies it. */
    float   wristHudWidth = 0.12f;

    /* Foveated rendering (TEST 427): 0 off, 1 wide, 2 balanced, 3 narrow.
       Two-way; the panel applies it. */
    int32_t vrs = 0;

    /* Eye-tracked foveation (TEST 428): the centre follows the gaze. Two-way. */
    int32_t vrsEye = 1;

    /* The head camera's height in the saddle (TEST 442). Two-way. */
    float   headUpMounted = 0.10f;

    /* Your body (TEST 450 / 447). Two-way. */
    float   armScale = 1.0f;
    float   bodyWeight = 0.25f;

    /* Battle HUD extras (TEST 462). Two-way. */
    int32_t hudHitMarker = 0;
    int32_t hudPrompts = 0;

    /* Five-finger hands (TEST 485). Two-way. */
    int32_t handFingers = 1;

    /* The campaign map's hand palette width in metres (TEST 527). Two-way; the
       panel applies it. */
    float   mapHandUiWidth = 0.15f;

    /* The left thumbrest is a D-pad modifier too. Two-way. */
    int32_t dpadThumbrest = 0;

    /* TEST 620: battle markers / campaign labels size under the split. Two-way. */
    float   uiWorldScaleBattle = 1.0f;
    float   uiWorldScaleMap = 1.0f;

    /* TEST 645: the weapon is solid against your own shield. Two-way. */
    int32_t ownShieldBash = 1;

    /* TEST 648: pre-battle formation markers size. Two-way. */
    float   uiWorldScaleDeploy = 0.30f;

    /* TEST 711: new-screen re-centre on/off and its turn-away angle. Two-way. */
    int32_t uiRecenterNewScreen = 1;
    float   uiRecenterAngle = 75.0f;

    /* fov_true_vertical: render only the lens's own up/down. Two-way. */
    int32_t fovTrueVertical = 0;

    /* TEST 750: the Calibration tool. See BvrOverlayState. */
    int32_t toolMode = 0;
    int32_t toolHand = 0;
    int32_t toolLive = -1;
    int32_t toolCommand = 0;
    float   toolValue[6] = {};
    char    toolLabel[128] = {};
    char    toolStatus[128] = {};

    /* TEST 751: pose profile, the wielded slot and the four slot names. */
    int32_t toolProfile = 0;
    int32_t toolWielded = -1;
    char    toolSlot[4][48] = {};

    /* TEST 770: fov crop tangent multipliers. Two-way. */
    float   fovCropH = 1.0f;
    float   fovCropV = 1.0f;

    /* TEST 785: detailed fov crop, per edge. Two-way. */
    int32_t fovCropDetailed = 0;
    float   fovCropLeft = 1.0f;
    float   fovCropRight = 1.0f;
    float   fovCropUp = 1.0f;
    float   fovCropDown = 1.0f;


    /* The keyed interface layer: shown at all, and how big it hangs. */
    int32_t uiShow = 1;
    float   uiWidth = 2.2f;
    float   uiDistance = 1.6f;

    /* The flat screen: the panel the menus and the campaign map appear on. */
    float   screenWidth = 2.0f;
    float   screenDistance = 2.0f;

    /* The tableau probe. An EVENT, cleared as it is read, because clicking the
       button means "run it once" and not "be in probe mode" - and a state that
       stayed set would re-run it every time the panel changed anything else.
       tableauProbeState comes back the other way so the button can report. */
    int32_t tableauProbe = 0;
    int32_t tableauProbeState = 0;

    /* Sharpening of the submitted eye images, 0..1 (0 = off). */
    float   sharpen = 0.0f;
    /* TEST 790: 0 CAS, 1 RCAS. */
    int32_t sharpenMode = 0;
    /* TEST 797: the cfg's dlss_preset (0 auto, 10-13 J-M, -1 DLSS's own). */
    int32_t dlssPreset = 0;
    int32_t gripToggle = 0;
    /* Enemy physics: -1 hidden (enemy_physics = 0), 0 off, 1 on. */
    int32_t enemyPhysics = -1;

    /* Menu pointer speed, in screen widths per second at full stick. Nothing
       native reads it - it is a managed setting that happens to need a slider,
       and the panel is the only slider a player wearing the headset has. */
    float   stickMouseSpeed = 1.1f;

    /* Applying a resolution change without a restart. resolutionApply is an
       EVENT and is cleared as it is read, for the same reason tableauProbe is:
       one click is one rebuild, and a state that stayed set would tear the eye
       pair down again every time any other setting changed.

       resolutionState comes back the other way - 0 idle, 1 rebuilding, 2 live,
       3 failed - so the button can report instead of the player guessing
       whether a black flash meant success. */
    int32_t resolutionApply = 0;
    int32_t resolutionState = 0;
};

/* Called from the managed side through the ABI. */
void overlay_toggle();
bool overlay_visible();

/* Shown or hidden outright, rather than flipped. The calibration key opens the
   panel ON the calibration section, and a toggle would close it for anyone who
   already had it open. */
void overlay_show(bool visible);

/* Mouse-wheel notches from the game thread, added to the panel's next frame. */
void overlay_scroll(float notches);

/* TEST 378: the laser pointer on the panel, in panel UV (0..1, top-left
   origin), with its click. onPanel false hands the pointer back to the desktop
   cursor. Render thread only. */
void overlay_set_pointer(bool onPanel, float u, float v, bool down);

/* Managed pushes the current truth in; the panel edits its copy. */
void overlay_set_settings(const OverlaySettings& in);

/* Managed polls. Returns true and clears the flag when the panel has changed
   something since the last call, so the caller applies and persists exactly
   once per edit rather than writing the config file at frame rate. */
bool overlay_take_settings(OverlaySettings* out);

/* Draws this frame's panel into its own texture. Called from the Present hook,
   on the render thread, with the engine's device. Cheap and immediate when the
   panel is hidden. */
void overlay_render(ID3D11Device* device, ID3D11DeviceContext* context);

/* This frame's panel image, or null when there is nothing to show. The OpenXR
   side copies it into a quad swapchain and submits it; this module deliberately
   knows nothing about OpenXR, so the two can be tested and broken separately. */
ID3D11Texture2D* overlay_texture();
uint32_t overlay_width();
uint32_t overlay_height();

/* True on the first frame of a fresh opening, so the layer can be pinned in
   front of wherever the player was looking at that moment. A panel that follows
   the head is much harder to point a cursor at than one that stays put. */
bool overlay_take_just_opened();

/* Releases the ImGui context, the render target and the quad swapchain. */
void overlay_shutdown();

} /* namespace bvr */

#endif /* BVR_VR_OVERLAY_H */
