# Configuration

**There is no config file until you make one, and you do not need one.** The
mod's built-in defaults are the settings it was tested on, and the in-VR panel
(`End`) covers what you actually want to change while playing — it creates the
file itself and writes to it as soon as you change something.

If you want to set something by hand, create:

```
Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
```

Every line is `key = value`. `#` or `;` starts a comment. Only the keys present
in the file are overridden; everything else keeps its default. Edit, save, and
restart the game — launch it however you normally do, Steam included.

Deleting the file puts everything back to defaults.

> **A warning worth taking seriously.** The config file exposes nearly every
> internal switch in the mod, including the ones an experiment was hung on for
> an afternoon. **If a key is not listed on this page, treat it as development
> machinery rather than a setting** — that goes double for anything named
> `*_probe`, `*_census`, `*_measure` or `*_debug`. Some combinations are known
> to be broken with each other. If you change one and the mod misbehaves, delete
> the file and restart before reporting anything.

---

## The ones worth touching

### Comfort and scale

| Key | What it does |
|---|---|
| `world_scale` | How big the world feels. Also on the panel. |
| `render_scale` | Per-eye render resolution multiplier, 0.25–1.0. Also on the panel. Lowering it applies as you drag; raising it needs the panel's **Apply now**, which rebuilds the eye images live (the view blacks out for a moment). |
| `render_scale_live` | 0 turns both live halves off, including Apply, and makes `render_scale` a next-launch setting again. |
| `render_scale_quiesce_frames` | Frames Apply waits after unbinding before releasing anything, so no frame in flight still references the old eye pair. Default 8. |
| `render_scale_timeout_frames` | How long the native half waits for new eye textures before giving up, rather than submitting black forever. Default 600. |
| `render_scale_allocate_timeout_frames` | The managed half's matching ceiling on waiting for rgl to service the new targets. Default 600. |
| `sharpen` | 0–1 sharpening pass (RCAS to 1.5). Also on the panel. |
| `sharpen_mode` | Sharpener: 0 CAS, 1 RCAS (FSR's sharpener: no clipped outlines, spares noise). Also on the panel. |
| `dlss_preset` | DLSS preset on every mode (also the VR panel's Render tab > DLSS preset). 0 (default) = the preset DLSS Swapper / the NVIDIA App put in the driver profile, else K; 10 = J, 11 = K, 12 = L, 13 = M; -1 = DLSS's own pick. The driver applies its override only in Performance, so the mod sets it. Restart to apply. |
| `mip_bias_offset` | Added to the engine's texture LOD bias, e.g. `-0.5`: negative = sharper textures under DLSS. 0 = off. Restart to apply. |
| `ngx_post_size_fix` | 1 (default) fixes engine post-processing target rounding that softened DLSS Quality/Balanced at some eye sizes, including FOV crops. Restart to apply. Requires a matching engine signature; logs a warning if unavailable. |
| `ngx_dump_output` | 0 (default) disables the DLSS and submitted-image diagnostic dumps. Set 1 only for investigation; restart to apply. |
| `ngx_jitter_probe` | 0 (default) disables the DLSS jitter diagnostic. Restart to apply. |
| `vrs` | Foveated rendering with NVIDIA Variable Rate Shading: 0 off, 1 wide, 2 balanced, 3 narrow (the size of the full-detail centre, largest to smallest). Native stereo only; needs an NVIDIA RTX 20 / GTX 16 or newer and does nothing on other GPUs. Also on the panel (Render tab). |
| `vrs_eye` | 1 (default) moves the full-detail area of `vrs` to wherever your eyes look, when the headset reports eye tracking through OpenXR (PSVR2 needs PSVR2Toolkit). Falls back to the centre of the view when there is no gaze. Also on the panel. |
| `vrs_4x4` | 1 lets the outer ring of `vrs` shade one pixel per 4×4 block. Default 0: 2×2 everywhere outside the sharp area, which looks much less blocky with DLSS and measured as fast. |
| `vrs_gaze_scale` | Multiplies the eye-tracked full-detail and half-detail radii. Raise it if the sharp area lags a fast glance. Default 1.0. |
| `dlss_foveated` | 1 resolves each eye as DLSS anti-aliasing at the render size for the whole view (stretched, soft) plus a full-resolution DLSS window in the middle, instead of one full-size DLSS upscale. Cheaper DLSS; native stereo only. Default 0. |
| `dlss_fovea_size` | The DLSS window's share of the eye's width and height. Default 0.40. |
| `dlss_fovea_feather` | How much of the window's half-size the blend into the periphery takes. Default 0.30. |
| `eye_gaze` | 0 keeps the OpenXR eye-tracking extension off entirely. Default 1. |
| `head_move` | Head movement drives the viewpoint. |
| `head_move_mounted` | The same while mounted. Off by default; the engine seats riders differently. |

### Battle grips

`grip_toggle = 1` enables **VR menu > Motion controls > Toggle battle grips**: press once
to hold, press again to let go. It covers weapons, off-hand two-handed grips,
reins, ladders, siege handles, ship controls and crossbow loading. Bowstrings
remain hold-to-draw and release-to-fire. Menus and firing triggers remain physical.
Default 0 keeps hold-to-grip. When switching back to hold mode with an item already
held, squeeze and release once to resume physical release behavior.

### Body and room scale

The full body is the experimental half. `hide_body = 1` stands all of it down and
the player is floating hands and weapons, as before.

| Key | What it does |
|---|---|
| `hide_body` | `1` hides the whole body (the older behaviour). `0` draws it. |
| `hide_head` | While the body is drawn, hide the head/hair meshes the camera sits inside. On by default. |
| `hide_head_meshes` | Comma-separated name substrings deciding what `hide_head` hides. The mesh table is printed once to the log, so the list can be corrected from it. |
| `hands_only` | Only has an effect while `hide_body = 1`: keeps the hand mesh(es) drawn alongside the weapon instead of hiding everything but the weapon. Also a checkbox in the VR panel (**End → Display → Body → Show hands only**, right under "Hide my body"), applied live and saved. |
| `hide_body_hand_meshes` | Comma-separated name substrings deciding what `hands_only` keeps. Default `hands_,glove,gauntlet`; the mesh table above names every mesh so the list can be corrected from it. |
| `body_ik` | The pelvis, spine and leg solve. Needs the body drawn and motion-control hands on. Also a checkbox in the VR panel (**End → Motion controls → Full body**), applied live; off is the game's own animated body. |
| `melee` | Motion-control combat: swings write the game's own attack direction, a raised hand is the guard, the blade lands the hit, and the body holds still through engine attacks. Also a checkbox in the VR panel (**End → Motion controls → Motion-control combat**), applied live and saved. `0` is the game's own combat — the trigger and the movement stick attack and block, every animation plays, and the camera follows the body. |
| `melee_ready_ticks` | How long the engine's own attack (and so the AI's chance to see it) is held, in mission ticks. Default 4. The blade's damage lands on the swing frame either way. |
| `melee_hit_window_ms` | How long the blade stays live after a swing, milliseconds. Default 250. |
| `combat_sweep_steps` | In-between blade poses swept per frame during a swing: `1` (default) is one, `0` is the two end poses only. Long frames get at least two. |
| `combat_ray_pad` | Added to the measured blade radius on every hit ray, metres. Default 0.02. |
| `combat_brush` | When every ray misses but a body is within `combat_brush_radius` of the blade, the hit lands anyway and is logged as a brush. `0` (default since TEST 263): `Ray()` now tests every real candidate directly, so the brush should rarely be needed; kept as a key for diagnosis. |
| `combat_probe_miss` | Log a swing that hit nothing, with its peak speed and sweep count. On by default. |
| `combat_probe_enemy_blade` | TEST 262: fit an enemy's blade per weapon class from real engine melee collisions (bone-local contact vs. `CollisionDistanceOnWeapon`), logged as `TEST 262 fit`. On by default; needs NPC-vs-NPC blows to have anything to fit. |
| `combat_block_reach_m` | TEST 264: how far away (metres) an attacking enemy is even considered for the blade-contact guard test. Default 3.0. |
| `melee_ai_block` | While a swing is coming, raise the engine's own defend stats (`AIBlockOnDecideAbility` and friends) on enemies in reach so their AI can choose to block. Restored exactly after `melee_ai_window_ms`. |
| `melee_ai_probe` | Log every enemy guard raised in response to a swing, with its latency, and a per-swing summary. On by default. |
| `melee_ai_reach`, `melee_ai_arc`, `melee_ai_window_ms`, `melee_ai_target` | Who the nudge concerns: metres, degrees of facing, and the raised chance and its duration. Defaults 2.5 / 60 / 400 / 0.9. |
| `combat_haptic_hit`, `combat_haptic_hit_min` | Pulse the striking hand on a landed hit, scaled by damage. `combat_haptic_hit_min` (default 0.35) is a graze. |
| `combat_haptic_clash`, `combat_haptic_clash_amp`, `combat_haptic_clash_ms` | The short pulse when the enemy's guard or shield stops your blade. |
| `combat_haptic_block` | Pulse the blocking hand when the engine resolves your own block. On by default. |
| `combat_friendly_fire` | `1` lets physical swings land on friends and neutrals; `0` (default) is the vanilla single-player rule, enemies only. Also a checkbox in the VR panel (**End → Motion controls → Motion-control combat**), applied live and saved. A mission that forbids friendly fire refuses it either way. |
| `block_raise_m` | How low the guarding hand may sit relative to the eye, in metres. Default -0.45, so a sword held at chest height guards; a hand at the waist does not. |
| `block_auto_direction` | `1` (default): while a guard is held, the direction it defends against comes from the game's own `AutoBlockModel` — the answer vanilla's "Block: Auto" uses — so the sword blocks the blow that is actually coming. `0` reads the direction off the hand instead. |
| `body_anim` | `legs` (default) holds everything above the legs at the pose it was in when the body calibrated, so only the legs animate — the engine's own gait — while the arms stay yours. Works whether or not `body_ik` (the Full body switch) is on: on foot, the held body is centred under the camera and the legs walk either way; `body_ik` on then layers your physical crouch/lean on top of it. `full` lets the animation drive the pelvis and spine as before, and needs `body_ik` on to show any of the player's own offset. The held pose is re-captured on every recentre, and only at a calm moment — never mid-swing, mid-block or mid-stride. |
| `body_ik_pelvis_gain`, `body_ik_pelvis_clamp` | How much of your body offset the hips follow, and the per-axis ceiling in metres. Default 0.6 / 0.30. |
| `body_ik_chest_gain`, `body_ik_chest_clamp` | The same for the chest. A little higher than the hips, which is what reads as leaning. Default 0.9 / 0.50. |
| `body_ik_smooth_ms` | Low-pass on the body offset, milliseconds. Default 60. |
| `roomscale` | Physically walking in the room walks the character through the game's own movement input. |
| `roomscale_kp` | Position servo strength: input per metre of offset still outstanding. Default 2.0. |
| `roomscale_kd` | Feed-forward: input per metre per second of head motion. 1.0 passes your own walking pace through. |
| `roomscale_dead` | Offset below which nothing is commanded, metres. Default 0.05. |
| `roomscale_speed` | The speed an input of 1.0 is taken to mean, m/s. Default 1.8. |
| `roomscale_max` | Ceiling on the injected movement, 0–1. Default 0.9. |
| `roomscale_recentre_m` | Offset at which the world starts to slide instead of the body stretching. Default 1.2. |
| `roomscale_recentre_rate` | How fast it may slide, m/s. Default 0.6. |
| `body_ik_debug`, `roomscale_debug` | Log the solve and the movement numbers once a second. |

**Stand roughly still for a moment at the start of a mission** (or press `F10`):
that is when the body calibrates to your standing pose, and the log says so.

### Aiming

| Key | What it does |
|---|---|
| `head_aim` | Aim where you look. |
| `head_aim_pitch` | Include pitch, not just yaw. |
| `head_aim_gain` | How hard the aim chases your head. Raise it if aim lags a fast head turn; lower it if the crosshair visibly hunts around the target. |
| `crosshair`, `crosshair_distance`, `crosshair_size_px` | The VR crosshair. It is drawn where the head actually points, which is why it exists at all. |

### Archery

Manual bow: reach over either shoulder and press the string hand's trigger to take an arrow,
keep holding and bring it to the bow to nock, pull back, release to shoot. The arrow flies
from the string hand through the bow hand, and a harder pull shoots faster and hits harder.

| Key | What it does |
|---|---|
| `bow_manual` | 1 = manual archery; 0 = the vanilla trigger draw. |
| `bow_quiver_behind_m`, `bow_quiver_low_m` | The quiver is on your back: the hand at least this far behind your eyes, and no further below them. Defaults 0.10 and 0.35. Every grab and missed grab logs where your hand was. |
| `bow_arrow_flip` | 1 if the arrow in your hand has its fletching at the bow end. |
| `bow_arrow_up_m` | How far above your bow fist the arrow rests, so it sits on the hand instead of through it. Default 0.05. |
| `bow_nock_grip` | Where the nock sits in your string hand, as `palm,up,forward` metres from the grip centre. Default the thumb and index finger. |
| `bow_draw_visual` | 1 = while drawn, the bow turns with the arrow and the string runs to your hand. 0 leaves the bow as it is held. |
| `bow_string_band_m` | How close to the bow's own string line a vertex must be to be removed with it. Default 0.006; lower it if the bow tips look dented. |
| `bow_string_flip` | 1 if the drawn string starts from the front of the bow instead of the back. |
| `bow_nock_m` | String hand to bow grip distance that nocks the arrow. Default 0.20. |
| `bow_min_draw_m` | Pull past the nock below which a release puts the arrow back unspent. Default 0.10. |
| `bow_full_draw_m` | Grip to string hand at full power. Default 0.65; lower it for a shorter arm span. |
| `bow_probe` | Log grabs, nocks and shots. |

### Controllers

| Key | What it does |
|---|---|
| `gamepad` | Controllers act as an Xbox pad. |
| `gamepad_deadzone` | Stick deadzone. |
| `stick_mouse`, `stick_mouse_speed`, `stick_mouse_stick` | Drive the mouse cursor from a stick, for menus. Exclusive with real mouse input — touch the mouse and it hands back. |
| `grip_align_key`, `grip_calibrate_key`, `grip_default_key` | Rebind the calibration keys (`F11`, `Home`, `Insert`). |

### Screens and UI

| Key | What it does |
|---|---|
| `flat_screen`, `flat_screen_width`, `flat_screen_distance` | The flat panel that menus and the campaign map are shown on. Turning it off puts the headset on the stereo path everywhere, which leaves menus blank. |
| `ui_show`, `ui_overlay_width`, `ui_overlay_distance` | The in-mission UI overlay, drawn on the view rather than in the room. |
| `monitor_mirror` | Fix up the desktop window's picture. `0` leaves it as the engine left it, which looks broken. |

### Rendering

| Key | What it does |
|---|---|
| `stereo_mode` | `afr` (the shipping path, cheapest), `nativestereo` (both eyes rendered for real, much more expensive) or `depth` (experimental depth reprojection — cheap, with missing edges on the right eye). Also on the panel; a change takes effect at the next mission. |

---

## Starting over

If your config gets into a state you cannot explain, **delete the file**. The
built-in defaults are the tested configuration, so an absent file is a known-good
one.
