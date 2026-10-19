# Changelog

All notable changes to BannerlordVR. Versions follow the game they are built
against as well as their own number: every release states the Bannerlord
version it was tested on, because a mismatch there is the single most common
cause of "it does not work".

## v0.2.6-alpha.1 ? 2026-10-20

Built against **Bannerlord v1.4.8** (stable, Steam build 24573425). Headset testing of this exact ZIP is pending.

- Fixed DLSS softness with cropped fields of view and added DLSS preset selection and a resolution readout.
- Added in-VR controller and equipment calibration with saved pose profiles.
- Added optional toggle battle grips; bowstrings remain hold-to-draw and release-to-fire.
- Improved bowstring rendering, camel camera and rider alignment, and camera height after falling from a mount.
- Fixed disappearing hands and weapons in the practice arena.
- Menus and screens now follow the configured auto-recentre angle. Large modded mounts retain first-person view.
- Added detailed field-of-view crop controls; removed the experimental true-vertical panel control.
- Native stereo at 1.00 render scale and hold-to-grab remain the defaults. DLSS uses the runtime supplied by the game/driver; no separate DLSS binary is bundled.
- **AMD graphics cards are not fully supported.** Single-player only; use SteamVR or VDXR.

## v0.2.5 — 2026-10-16

Built against **Bannerlord v1.5.5**. Headset testing of this exact ZIP is pending.

- Sharper, steadier VR menus, HUD and campaign-map labels.
- Better pre-battle formation controls; commander portraits on the left-hand panel are now clickable.
- Fixed black banners, stereo artifacts, disappearing fingers and mounted positioning.
- Improved crossbow handling, throwing-weapon weight and siege interactions.
- Updated default settings and saved controller/weapon calibration. Native stereo at 1.00 resolution scale remains the default.
- **AMD graphics cards are not fully supported.**

## v0.2.0-alpha.1 — 2026-10-13

Built from physical-controls development commit `0b141c1` against **Bannerlord v1.5.5** (Steam build 25822820). Other game versions are unverified for this archive.

- Physical melee, tracked weapons, body holsters, hold-to-grab, two-handed grips, throwing, manual bows and crossbows.
- Body IK, room-scale movement, finger hands, horse reins, ladder climbing, and physical siege interactions.
- VR menu pointing, wrist HUD, campaign-map controls, and a combined physical-controls switch.
- Latest pre-battle fixes: hide hands/weapons before battle, allow pointing at pre-battle menus, and confirm move-to-flag with the right index trigger.
- Release defaults use native stereo at 1.00 render scale; AFR and experimental depth reprojection remain selectable. Shipped defaults include calibrated equipment poses; personal config overrides them.
- The OpenXR loader is statically linked. Install the two mod DLLs and the matching shipped default config together.

Automated compilation, managed/ABI math, projection math, and UI math checks are recorded in the release validation report. The owner reported all requested headset acceptance checks passed on 2026-10-13. Logs confirm shipped defaults, VR initialization, mission rendering, campaign-map rendering, and normal shutdown. A handled view-restoration error during shutdown remains disclosed as a known alpha issue.

## v0.2.0 — 2026-09-26

### Fixed

- **Native stereo showed only the left eye on Virtual Desktop (VDXR).** The
  right eye was a flat copy of the left, or a broken, out-of-step image. The
  hooks native stereo needs on the game's draw calls were refused all session:
  with VDXR running, nothing was free within the 1 GB of memory MinHook searches
  for its jump code. The search now reaches about 2 GB (MinHook is patched at
  build time, with a check that refuses a jump it cannot encode rather than
  corrupting it), and a refused hook is retried once a second and logged once
  with the distance to the nearest free memory. SteamVR was not affected.
- **Battles froze on load with a black headset on Virtual Desktop (VDXR).**
  The game thread and the render thread could both begin an OpenXR frame at
  once, and VDXR's `xrBeginFrame` works on the game's own D3D11 context while
  the render thread held it — each waited on the other forever. The two now
  take turns and lock in the same order. SteamVR was never affected. On 0.1.0,
  `pre_render_frame = 0` in the cfg avoids it.
- **The body separated from the camera while walking, and legs-only needed
  the Full body switch on.** A raised shield or guard turned on a camera hold
  meant for standing still, decided from movement *input* rather than actual
  motion, so it froze the view while a visibly walking body kept going —
  `body_anim = legs` (TEST 257) already held everything above the legs still
  and let only the legs animate, but it ran through the Full body switch and
  did nothing while that switch was off. Legs-only now runs on its own, holds
  are cleared instead of gliding in stale, and the held body is centred under
  the camera so looking down puts you inside the neck opening rather than
  behind it.

### Added

- **A grip calibration per controller.** A Controller dropdown in the VR
  panel's Calibration section (Generic, PSVR2, Quest) keeps each one's grip
  separately; a controller never calibrated starts from the Generic grip. It is
  chosen, not detected, because PSVR2 and Quest both report as Touch
  controllers through SteamVR.
- **Full-body IK.** The pelvis, spine and both legs follow the player: crouch
  and the knees bend with the feet planted, lean and the torso moves over the
  hips, and the arms keep working off the moved chest. The engine's own gait,
  ground contact and mount placement stay the game's — the body is an offset on
  top of the animation, and a still player gets the animation back exactly.
- **One swing, one event.** The gesture that fires the engine's own attack and
  the blade's own hit window used to be two detectors with their own thresholds,
  smoothing and cooldowns, and they could disagree — the engine swung while the
  blade window never opened. The gesture now publishes one swing event and the
  blade window opens on that exact event, staying live through the
  follow-through (`melee_hit_window_ms`). Missing swings also stopped being
  invisible: `combat_probe_miss` logs every swing that ends without a hit, with
  its peak speed and sweep count.
- **A swept blade, and a brush.** A swing now casts the blade at the previous
  pose and the in-between poses of the rotation, not just the two edges, so a
  body cannot slip through the fan of a fast swing; a body within
  `combat_brush_radius` of the blade is hit even when every ray missed, logged
  as a brush. Guards and shields are still tested first.
- **Enemies react to a swing.** The engine's own attack state is held long
  enough for the AI to sample it (`melee_ready_ticks`), enemies near the swing
  have the engine's own defend stats raised for a short window
  (`melee_ai_block`), and `melee_ai_probe` logs every guard raised in response,
  with latency and a per-swing summary. A raised guard stops the blade through
  the existing weapon-vs-weapon path.
- **Haptics on what happens.** A landed hit pulses the striking hand, scaled by
  damage; an enemy guard or shield stopping the blade gets a short sharp pulse;
  and the engine's own resolution of your block pulses the blocking hand
  (`PlayerBlockHapticPatch` on `Mission.OnAgentHitBlocked`). Keys:
  `combat_haptic_hit*`, `combat_haptic_clash*`, `combat_haptic_block`.
- **Friendly fire is a setting.** `combat_friendly_fire` defaults to off — the
  vanilla single-player rule, enemies only — with a checkbox in the VR panel's
  Motion-control combat section, applied live and saved. Missions that set
  `ForceNoFriendlyFire` or `IsFriendlyMission` refuse it either way.
- **Weapon blocking actually blocks.** Checked against the session log, the
  guard state was live — `CurrentGuardMode` followed the written bits, enemy
  weapon guards stopped the player's blade, and the engine's block callback
  fired — but a sword could not block: 30 log lines showed a chest-height hand
  failing `block_raise_m` (-0.25), so no guard bit was ever written, and every
  centered guard took its direction off the hand while the engine's block test
  is directional. The guard height is -0.45 now, the direction comes from the
  game's own `AutoBlockModel` while a guard is held
  (`block_auto_direction`), and every engine-resolved block logs a line naming
  the weapon or the shield.
- **Your own hit now runs the game's own damage pipeline.** The blade used to
  build its own `Blow` from the weapon's raw `SwingDamage`/`ThrustDamage`: no
  armor, no bone multiplier, no difficulty, no perks, no knockback/knockdown,
  no combat log or damage popup, and blood from a plain particle burst instead
  of the game's own. The hit is now handed to the real
  `Mission.MeleeHitCallback` with a contact point and direction built from what
  the mod actually measured — the rest (armor, perks, the bone multiplier,
  knockback, the combat log, the hit sound, death, XP) is the engine's, exactly
  as a real collision's would be. `combat_friendly_fire = 1` keeps the old
  hand-built path for a friend or neutral specifically: vanilla's own single-
  player rule zeroes non-enemy damage inside the same pipeline, with no
  setting to ask it not to. The engine's own hidden attack — still fired so
  the AI sees and answers a swing — no longer lands a duplicate blow of its
  own: only the blade does damage. A weapon class's blade is fitted from real
  engine collisions (bone-local contact vs. how far along the weapon the
  engine says it hit), the same measurement discipline behind the room-scale
  and body work below; `combat_probe_enemy_blade` logs the fit.
- **A ray bug that turned near-misses into brushes.** The hit ray excluded one
  agent and re-cast on a skip, but the exclusion moved to whichever agent was
  skipped rather than staying on the attacker — so a third attempt could ray
  straight into the player's own hand and read as a miss. Every real candidate
  in reach is now tested directly, so nothing is ever excluded by accident;
  `combat_brush` defaults off, since the fixed ray rarely needs it.
- **The body and the view hold through a block, not just a swing.** The whole-
  skeleton freeze and the camera's attack hold used to cover only the engine's
  attack actions; a guard now held the same way plays a defend animation
  neither one caught, so a block after a swing still moved the body and the
  view. Both now cover the engine's defend actions too, and the camera hold
  applies with the body visible as well, now that the whole skeleton — the
  head included — is held with it.
- **Blade-on-blade blocking.** Your own weapon guard now triggers on your
  blade actually touching a nearby attacking enemy's own fitted blade
  (`combat_block_reach_m`), not a hand held in a position zone — the shield
  guard is unchanged. An enemy's own weapon guard stops your attack the same
  way now, instead of a reach zone; a raised shield keeps its existing plate
  test. Either case now runs through the real `Mission.MeleeHitCallback`
  exactly as a clean hit does, so a block's stagger, a shield's own durability
  damage and the combat log are the engine's, not a hand-built approximation.
  Built from decompiling the shipped engine DLL rather than assuming how it
  behaves: `CollisionResult`/`AttackBlockedWithShield`/`CorrectSideShieldBlock`
  turned out to be inputs the caller decides, not something the engine infers
  from a defender's action state. Both directions only fire once a weapon
  class has enough real engine hits for its blade to be fitted (TEST 262).
- **A polearm's own damage was silently zeroed.** `GuardStops` (above) matched
  any defend action, including `DefendShield` — an enemy defending with a
  shield still had `GuardStops` test her idle *weapon* against the player's
  swept blade, and a long polearm's much bigger swept volume "found" that
  idle weapon often enough to force every hit to read as blocked (0 damage,
  by the engine's own rule for a non-shield block). Fixed: `GuardStops` skips
  `DefendShield`, which is `ShieldStops`' job. Also: `combat_brush` (off by
  default since TEST 263) is back on — a live session with `melee = 1` showed
  it is still needed for offense to land at all, unrelated to the bug it was
  turned off for.
- **Room-scale movement.** Physically walking drives the game's own movement
  input, so the engine walks the character with its collision, animation and
  speed; the room origin consumes what the avatar walked off, so the world stays
  where the player left it and the body stays under them. A soft recentre covers
  walls and play-area limits.
- **A drawn player body**, with the head/hair meshes hidden by name because the
  camera lives inside them. `hide_body = 1` returns to the floating hands.
- **`body_anim = legs`** (default): everything above the legs is held at the pose
  it was in when the body calibrated, so only the legs animate — the engine's own
  gait — and the torso stops playing the walk's rock, the idle's sway and the
  combat lean. `body_anim = full` brings the animation back.
- **A Full body switch in the VR panel** (End → Motion controls): off reverts to
  the game's own animated body — no follow, no leg solve, no held pose, and the
  camera anchors to the engine's own head again. It writes `body_ik` and applies
  live; the arms and the room scale are untouched.
- **A Motion-control combat switch in the VR panel** (End → Motion controls): one
  checkbox for the whole gesture melee — swings writing the game's own attack
  direction, raised hands as the guard, the blade landing the hit, and the body
  holding still through engine attacks. Off is the game's own combat: the trigger
  and the movement stick attack and block, every animation plays, and the camera
  follows the body. It writes `melee` and applies live, like the switches beside
  it; the hands, the weapons and the full-body IK are separate switches and are
  untouched.
- **The attack holds now apply only while the body is hidden.** The camera hold
  (TEST 249/251), the body freeze (TEST 252) and the chest hold (TEST 250) exist
  to stop the *view* riding the engine's attack animation, which is a pure win
  with no body drawn — but with the body drawn they froze the camera while the
  body kept moving, so the body walked out in front of the player. Visible, the
  camera follows the engine's own camera live and the animation plays, so body
  and view cannot separate. `hide_body = 1` restores the old behaviour exactly.
- New keys: `body_ik*`, `roomscale*`, `hide_head`, `hide_head_meshes`. See
  [docs/CONFIG.md](docs/CONFIG.md).

### Notes

- A failure in the hit path no longer turns physical combat off for the whole
  session: the first failure is logged and it keeps running, and only five in a
  row stand it down.
- The settings-panel ABI grew one field (`sizeof(BvrOverlayState)` 128 → 132),
  so the native and managed DLLs ship together.
- Everything above stands down mounted, in deployment, while a menu is being
  read, and while the body is hidden (`hide_body = 1`).
- `F10` recentres and re-measures the body at the same time.

## v0.1.0-alpha.1 — 2026-09-20

First public build. **Bannerlord v1.4.8.** Pre-release: expect broken menus and
crashes.

### Added

- **6DoF head tracking.** Your head is the camera, at 1:1 world scale, in
  missions and on the campaign map.
- **Native stereo rendering.** Both eye views come out of the game's own
  renderer into an OpenXR swapchain. Not a flat image reprojected into a
  headset.
- **Three render modes**, switchable in the `End` panel and applied at the next
  mission:
  - **AFR** (default) — one eye rendered per frame; the other is its own last
    real render, submitted at the pose it was drawn at so the compositor
    reprojects it. Exact geometry, one frame old. Cheapest.
  - **Native stereo** — both eyes rendered for real from one engine frame.
    Correct, and the most expensive thing here by a wide margin.
  - **Depth reprojection** (experimental) — one eye rendered, the other rebuilt
    from its depth buffer, so both eyes show the same instant.
- **Motion controllers as an Xbox gamepad.** The game never learns they are VR
  controllers, so every existing gamepad binding and on-screen prompt works
  unchanged. Hands and weapons follow the controllers, mounted and on foot.
- **D-pad chord.** VR controllers have no D-pad and several screens bind
  nothing else: hold the left stick in and flick the right stick. Disable with
  `dpad_chord = 0`.
- **Mouse pointer from the left stick in menus**, because the inventory grid,
  the map and anything with a scrollbar assume one exists.
- **Head-based aiming**, with the engine's own aim path driven from the
  headset.
- **In-VR settings panel** on `End` — render mode, world scale, resolution
  scale, sharpening, hide body, UI overlay placement, flat-screen geometry and
  hand calibration. Changes save immediately.
- **DLSS support.**
- **Optional config file** at
  `Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg`. None is
  shipped: the built-in defaults are the tested configuration, and the settings
  panel writes the file itself.

### Known issues

- **Melee is awkward.** Swings are driven by stick direction, not by where your
  arm actually went.
- **Native stereo is very performance intensive.** Bannerlord is CPU-bound
  before VR is involved.
- **Depth reprojection has missing edges on the right eye.** Where a near
  object hid something the rendered eye never saw, there is no data to rebuild
  from, so those slivers are filled rather than drawn. Most visible along the
  edges of things close to you.
- **Some UI elements are broken.**
- A render-mode switch can float for a moment before the new mode settles.
- **Meta/Oculus OpenXR is not supported.** Use Virtual Desktop (VDXR) or
  SteamVR as the active runtime.

### Tested on

| Headset | Runtime | Status |
| ------- | ------- | ------ |
| Quest 3 | Virtual Desktop (VDXR) | Working |
| Quest 3 | SteamVR OpenXR | Working |
| Quest 3 | Meta/Oculus OpenXR | Not supported |
| PSVR2 | SteamVR OpenXR | Working |

### Not included

- **Single-player only.** `MultiplayerModule` is disabled deliberately — camera
  and input hooks are indistinguishable from cheating to other players and to
  TaleWorlds' server logic.
- No game files or assemblies are redistributed.
