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
    int32_t motionHands = 0;

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
};

/* Called from the managed side through the ABI. */
void overlay_toggle();
bool overlay_visible();

/* Shown or hidden outright, rather than flipped. The calibration key opens the
   panel ON the calibration section, and a toggle would close it for anyone who
   already had it open. */
void overlay_show(bool visible);

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
