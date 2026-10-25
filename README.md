# BannerlordVR

**Play Mount & Blade II: Bannerlord in PCVR with tracked hands, physical combat, and OpenXR.**

Swing your sword, raise your shield, draw a bow by hand, and reach for weapons on your body. Look around naturally with six degrees of freedom (6DoF) head tracking, with stereo views produced by the game's renderer.

Disclaimer: The mod is build with the help of Claude Code.

[Downloads](https://github.com/NotLooky/BannerlordVR/releases) · [Report a bug](https://github.com/NotLooky/BannerlordVR/issues/new/choose)


<img width="385" height="209" alt="14011-1791990479-900614996" src="https://github.com/user-attachments/assets/181151a1-4797-4197-978b-8de1a4c47185" />

<img width="385" height="208" alt="14011-1791990474-1915983548" src="https://github.com/user-attachments/assets/53226c20-1bcf-4640-bd6b-77f784679401" />



> **v0.2.6-alpha.2 is available:** [download the alpha](https://github.com/NotLooky/BannerlordVR/releases/tag/v0.2.6-alpha.2). Built against **Bannerlord v1.4.8** (stable, Steam build 24573425). Other game versions are unverified. Expect glitches, unfinished UI, and crashes. Headset acceptance with this exact ZIP is pending.

## Contents

- [Features](#features)
- [Requirements and headset support](#requirements-and-headset-support)
- [Installation](#installation)
- [First-time setup](#first-time-setup)
- [Motion-control bindings](#motion-control-bindings)
- [Weapons and physical combat](#weapons-and-physical-combat)
- [Movement, riding, and siege equipment](#movement-riding-and-siege-equipment)
- [Menus, campaign map, and army orders](#menus-campaign-map-and-army-orders)
- [VR settings and calibration](#vr-settings-and-calibration)
- [Rendering and performance](#rendering-and-performance)
- [Configuration](#configuration)
- [Troubleshooting](#troubleshooting)
- [Bug reports and feedback](#bug-reports-and-feedback)
- [Updating and uninstalling](#updating-and-uninstalling)
- [Credits](#credits)
- [Development and license](#development-and-license)

## Features

- **6DoF head tracking:** look, lean, and move your head naturally.
- **Tracked hands and weapons:** your controllers position your hands; weapon weight adds resistance and lag.
- **Physical melee:** swing and thrust to attack, with weapon and shield contact, game damage rules, and haptic feedback.
- **Body holsters and grabbing:** draw equipped weapons by reaching for them, pick up nearby weapons, and release to stow or drop them.
- **Two-handed weapons:** hold a sword or polearm with both hands and adjust your grip along the shaft.
- **Manual ranged combat:** take arrows and bolts from behind your shoulder, draw bows, span crossbows, and physically throw throwing weapons.
- **Body tracking from head and hands:** body movement, arm IK, and room-scale movement without requiring external body trackers.
- **Physical interactions:** horse reins, saddle mounting, ladder climbing, and siege controls.
- **VR interface:** controller pointing, wrist HUD, campaign-map controls, and an in-headset settings panel.
- **Rendering options:** AFR, native stereo, experimental depth reprojection, DLSS support, and optional NVIDIA variable rate shading.

## Requirements and headset support

- **Mount & Blade II: Bannerlord v1.4.8 or v1.5.5 .** This package was built against this version; other game versions are not verified. Steam is the tested installation.
- **Windows 10 or 11, 64-bit**, with a PCVR-capable system.
- **AMD graphics cards are not fully supported.**
- **[Bannerlord.Harmony](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006)** installed and enabled.
- A PCVR headset, tracked motion controllers, and an active **OpenXR runtime**.

**Single-player only.** Multiplayer is disabled in the module.

The following headset/runtime combinations have been tested by the project. This is not a guarantee that every development feature works on every device.

| Headset | OpenXR runtime | Project status |
| --- | --- | --- |
| Quest 3 | Virtual Desktop / VDXR | Tested |
| Quest 3 | SteamVR OpenXR | Tested |
| PSVR2 | SteamVR OpenXR | Tested |
| Quest 3 | Meta / Oculus OpenXR | Tested |

Other headsets may work through SteamVR, but are unverified. Controllers with fewer buttons may not provide every binding below.

## Installation

1. Check your Bannerlord version before installing.
2. Install **Bannerlord.Harmony** and enable it in the launcher.
3. Use the included **BannerlordVR-0.2.6-alpha.2.zip**, or download the matching build from [Releases](https://github.com/NotLooky/BannerlordVR/releases) once published. GitHub's **Code → Download ZIP** downloads source code, not an installable mod.
4. Extract the release into your Bannerlord installation folder. The result should look like this:

   ```text
   Mount & Blade II Bannerlord/
   `-- Modules/
       `-- BannerlordVR/
           |-- SubModule.xml
           |-- BannerlordVR.default.cfg
           |-- NVIDIA-DLSS-LICENSE.txt
           `-- bin/
               `-- Win64_Shipping_Client/
                   |-- BannerlordVR.dll
                   |-- BannerlordVR.Native.dll
                   `-- nvngx_dlss.dll
   ```

   The mod's `bin` belongs **inside `Modules/BannerlordVR`**. Avoid a second nested `BannerlordVR` folder.
5. Connect your headset, wake both controllers, and start VDXR or SteamVR. Confirm that your chosen runtime is the **active OpenXR runtime**.
6. Launch Bannerlord. Enable **Bannerlord VR** in the Mods tab and place it **below Harmony**, with its required base-game modules enabled.
7. Start a single-player mission. A small Custom Battle is a useful place to learn the controls.



Keep the managed DLL, native DLL, bundled DLSS DLL, NVIDIA license, and shipped default config from the **same build** together.

The package includes NVIDIA DLSS **310.9.1.0**. When the mod loads, it upgrades an older game `bin/Win64_Shipping_Client/nvngx_dlss.dll` and saves the first original beside it as `nvngx_dlss.dll.bannerlordvr-original.bak`. Equal or newer versions are preserved. If the old DLSS was already loaded, restart Bannerlord once to use the upgrade; the managed log records this. The game bin folder must be writable; failures are logged and the game continues with its existing DLL.

## First-time setup

1. Enter a mission, stand in a comfortable neutral pose, and recenter with **F10**. You can also hold the **left stick click for 3 seconds**, keeping the right stick centered.
2. Open the VR panel with **End**. With both hands empty, holding **both grips for 2 seconds** also toggles it with the shipped control layout.
3. Under **Motion controls**, enable **Motion controls - full physical combat**. This combines tracked hands, physical combat, body IK, and room-scale movement.
4. Check height, world scale, and hand alignment. Use the panel's calibration tools if weapons sit incorrectly in your hands.


The optional **Toggle battle grips** setting keeps weapons held after a grip press; bowstrings still require holding and releasing the grip. It is off by default (`grip_toggle = 0`).

These instructions use the new build's shipped layout: `controls_swap_trigger_grip = 1` and `grip_hold = 1`. Your personal config overrides the shipped defaults.

## Motion-control bindings

**Grip** means the squeeze control under your middle fingers. **Trigger** means the index-finger trigger. A stick click means pressing the stick inward, not tilting it.

### Main controls

| Physical input | Action with physical controls enabled |
| --- | --- |
| Move your head | Look around; lean and move the viewpoint |
| Left stick | Move; also provides conventional riding and climbing input |
| Right stick | Turn / camera control, depending on context |
| Grip near a weapon or holster | Grab or draw; **hold it to keep holding** |
| Release grip at the item's holster | Put the item away |
| Release grip away from a holster | Drop/toss the item, or throw a throwing weapon |
| Swing / thrust the held weapon | Melee attack |
| Raise and position shield / weapon | Defend; contact and guard direction matter |
| Off-hand grip near a two-handed weapon's shaft | Take a second grip; release to let go |
| Weapon-hand stick click while holding a two-handed weapon | Allow that hand to slide along the shaft |
| Right index trigger | Gamepad RB when no physical interaction owns it |
| Left index trigger | Gamepad LB when no physical interaction owns it |
| Right primary button (Quest A / PSVR2 Cross) | Gamepad A: jump or confirm; click while pointing at UI |
| Right secondary button (Quest B / PSVR2 Circle) | Gamepad B: back / cancel / leave, depending on context |
| Left primary button (Quest X / PSVR2 Square) | Gamepad X: kick or context action |
| Left secondary button (Quest Y / PSVR2 Triangle) | Gamepad Y: interact / use |
| Left menu button | Gamepad Start / menu, where supplied by the runtime |
| Hold left stick click + tilt right stick | D-pad direction; see below |
| Hold left stick click for 3 seconds | Recenter; keep right stick centered |
| Both grips held for 2 seconds, hands empty | Toggle VR settings panel |

Face-button names above describe the equivalent button positions. PSVR2 can report as a Touch controller through SteamVR; use the game's Xbox prompts as the reference. Context actions follow Bannerlord's own gamepad bindings and can change if you rebind them.

### Trigger/grip swap

The new layout uses **grips for holding** and index triggers for firing or bumper actions. If your buttons behave the other way around, check your personal config:

```ini
controls_swap_trigger_grip = 1
```

Restart after changing it. With `0`, **swap grip and trigger throughout these instructions**, including holsters, bow drawing, menu shortcuts, and the gamepad mapping. Stick and face-button bindings stay the same.

### D-pad without a D-pad

**Press and hold the left stick inward, then tilt the right stick up, down, left, or right.** The right stick sends a D-pad direction instead of turning the camera until you release the left stick. or for Quest3 hold left thumbrest + right stick

| Direction | Battle action with standard gamepad bindings | Army-order selection |
| --- | --- | --- |
| Up | Cheer | Toggle selection |
| Down | Crouch | Apply selection |
| Left | View character | Previous formation |
| Right | Push-to-talk binding | Next formation |

The left stick click also reaches the game as L3, so its normal action can occur when you start the chord. Moving the right stick cancels the recenter hold for that press. Set `dpad_chord = 0` to disable the chord, or `recenter_hold_seconds = 0` to disable controller recentering.

## Weapons and physical combat

### Drawing, holding, and putting weapons away

Reach to an equipped item's holster, squeeze the grip, and bring your hand forward **without releasing it**. To stow it, bring it back to its holster and release.

| Default reach zone | Item / hand |
| --- | --- |
| Left hip | One-handed weapon |
| Behind left shoulder | Bow; normally held in the left hand |
| Behind right shoulder | Shield with the left hand; two-handed weapon, polearm, throwing weapon, crossbow, or sling with the appropriate hand |
| Behind either shoulder, with bow/crossbow already held | Arrow or bolt with the free loading hand |

Zones depend on equipped items and the build's holster placement. Reach near or behind ear level for shoulder grabs. A loading-hand reach while holding a bow takes an arrow rather than drawing another weapon.

To pick up a weapon from the ground, bring the correct hand close and squeeze the grip. Shields and bows use the off hand; ordinary melee weapons use the main hand. Pickup follows the game's inventory rules and can replace an occupied slot.

Releasing an ordinary weapon away from a holster tosses a visual copy that returns to its holster. **Tossed ordinary swords and shields do not deal throw damage.** Actual throwing weapons use the ranged damage system.

### Swords, shields, and polearms

- **Attack:** swing or thrust with the weapon. Blade motion and contact drive physical hits; the game handles armor, damage, and combat outcomes.
- **Shield:** raise it between yourself and the incoming blow or projectile.
- **Weapon block:** bring your weapon into the incoming weapon's path. A hand gesture alone is not a guaranteed block.
- **Two hands:** hold the main grip, bring the other hand near the shaft, and hold its grip. Your hand positions determine the weapon's direction.
- **Slide a grip:** while holding with both hands, click and hold the weapon-hand stick and move that hand along the shaft. Release the click to fix the grip again.

Weapon weight can make the weapon lag behind quick controller movements. Friendly fire is a separate setting: **My swings can hit friends and neutrals**. Missions that forbid friendly fire still take priority.

### Bow

1. Draw the bow and keep holding it in the bow hand, normally the **left**.
2. Reach the free string hand behind either shoulder and **hold its grip** to take an arrow.
3. Keep holding and bring the arrow to the bow to nock it.
4. Pull the string hand back, aim along the arrow, and **release that grip to shoot**.

A longer draw gives more power. Releasing before the minimum draw returns the arrow without spending it. `bow_full_draw_m` adjusts the distance needed for full power.

### Crossbow

1. Hold the front of the crossbow with the **left hand**.
2. With the right hand, grip behind either shoulder to take a bolt, then bring it to the loading seat.
3. Grip the string and pull it back until it latches. The span is deliberately limited by the crossbow's draw time; releasing early lets the string return.
4. Take the stock with the right hand, aim with both hands, and pull the **right index trigger** to fire.

Seating the bolt and spanning the string can happen in either order; both are required to fire. Heavy crossbows can take longer to span on horseback.

### Throwing weapons

Draw a javelin, throwing axe, knife, or other throwing weapon, hold the grip, then make a throwing motion and **release the grip**. Hand velocity determines the throw. A release that is too slow is treated as a drop rather than a damaging throw.

## Movement, riding, and siege equipment

| Interaction | How to use it |
| --- | --- |
| Room-scale movement | Walk within your play area; the avatar follows through the game's movement system |
| Physical crouch / lean | Move your head and body with body IK enabled |
| Mount a riderless horse | With a free hand, grip near its saddle |
| Hold reins | Grip the reins with a free hand |
| Steer with reins | Move the holding hand left or right from its grab position |
| Increase riding speed | Flick the rein hand up, then down |
| Slow / stop | Pull toward the chest to slow; yank toward the chest for a quick stop |
| Use conventional riding | Move the left stick; stick input takes priority over rein gestures |
| Climb a raised ladder | Grip a rung with a free hand and pull downward; alternate hands to climb |
| Climb with the stick | Use forward/back movement; stick input takes priority |
| Siege aiming | Grip the machine's controls and move your hands to aim |
| Siege firing | Use the main hand's index trigger while operating the machine |
| War Sails helm, when available | Use the helm, grip the tiller, and move the hand forward/back to steer; rowing and sail input remain on the stick |
| Manual ballista reload | Grip a bolt from the rack, seat it in the groove, and grip/turn the winding wheel until ready |

War Sails interactions require the relevant game content. Body and movement behavior differs while mounted, climbing, or operating equipment. Keep using the game's interact action for interactions without a physical grab, including normal dismounting.

## Menus, campaign map, and army orders

### Pointing at menus

Point a controller at the interface and press **A / Cross** to click. Outside the campaign map, pulling a hand's **grip** selects that pointing hand with the shipped layout. The campaign map uses the **right hand** for pointing.

The left stick can also move the mouse pointer in menus when stick-mouse control is enabled. Physical combat stands down while menus or the VR panel own input.

### Campaign map

With the VR map enabled, point the right controller at terrain and press **A / Cross** to choose a destination. Pointing at a menu widget makes the same button click that widget. The map also has a left-hand interface palette.

| Input with shipped layout | Standard gamepad action |
| --- | --- |
| Right grip | RT: zoom in, when not owned by UI |
| Left grip | LT: zoom out, when not owned by UI |
| Left primary (X / Square) | Pause / resume time |
| Right index trigger | RB: fast-forward |
| Left stick click | L3: camera follow mode |
| Right stick click | R3: track settlement |

### Army orders

Use the game's gamepad order interface. With the shipped layout, **left index trigger maps to LB** for indicators / orders. Face buttons provide the four order choices, and the D-pad chord changes formation selection.

| Input | Order-interface binding |
| --- | --- |
| X / Square | Order 1 |
| A / Cross | Order 2 |
| B / Circle | Order 3 |
| Y / Triangle | Order 4 |
| Left index trigger | LB: hold / order modifier |
| Left menu button | Start: return |

These are context bindings, not fixed troop commands. Follow the current on-screen order labels.

## VR settings and calibration

Open the panel with **End**, or hold both grips for **2 seconds with empty hands**. Point at its controls and click with A / Cross. Changes are saved as you make them, except calibration edits that have an explicit **Save** button.

| Panel area | Useful settings |
| --- | --- |
| Render | Stereo mode, resolution scale, sharpening, NVIDIA VRS and eye-tracking options |
| Display | HUD size and placement, campaign-map VR, interface geometry, hit markers and pickup prompts |
| Motion controls | Full physical combat, friendly fire, body holsters, hand calibration |
| VR | Height and body fit, head offsets, body turning, and hand appearance |

### Hand calibration

1. Enter a mission and stand still with the controllers in a neutral holding pose.
2. Open **Motion controls → Calibration** and select your controller preset: **Generic**, **PSVR2**, or **Quest**. Presets are selected manually, since runtimes can report different controllers under the same profile.
3. Use **Automatic** to sample alignment, or **Manual** to adjust each hand's position and rotation.
4. Check the weapon and shield hands separately, then **Save**. Use **Cancel** to discard manual edits or **Reset this hand** to return to its baseline.

| Keyboard key | Action |
| --- | --- |
| `End` | Open / close VR settings |
| `F10` | Recenter and re-measure body height |



## Rendering and performance

**Release defaults: native stereo at 1.00 resolution scale.** Native stereo is the project's preferred stability baseline.

Choose a mode in the Render tab. **Stereo modes can switch live in this build; follow the panel status if a change is pending.**

| Mode | What it does | Tradeoff |
| --- | --- | --- |
| **AFR** | Renders alternating eyes; the runtime reprojects each eye's latest real image | Lower rendering cost; the eyes can differ by one frame, especially noticeable when frame rate drops |
| **Native stereo** | Renders both eyes for the same engine frame | Highest rendering cost |
| **Depth reprojection** | Renders one eye and rebuilds the other from depth | Experimental; missing or duplicated edges around nearby objects |

Bannerlord can be CPU-limited in large battles. Start with a small battle, then adjust:

1. Try **AFR** if native stereo is too expensive.
2. Lower **resolution scale** in the VR panel. Lowering can apply live; increasing can require **Apply now**, with a brief black view while targets rebuild.
3. Try **DLSS** in the game's graphics settings.
4. Reduce shadows, screen-space reflections, and battle size according to the bottleneck.
5. On supported NVIDIA hardware, try **VRS** in native stereo. Eye-following VRS needs a runtime that exposes eye tracking; otherwise it uses a fixed center.

**tips for performance**

-(this only work with nvidia card) Install cheekyfoveateddlss(0.5.5 standalone) from their github, then paste the CheekyFoveatedDLSS folder only into the game's \bin\Win64_Shipping_Client, don't add anything else from the folder. this will enable cheekydlss , this alone should give you a good boost to performance when using dlss in game. for me it was about 12fps .

-The other tip is , FOV cropping , the mod now ships with fov croping sits at 1.0 , lower it , until the edges bothers you , for quest 3 best was (H=0.83, V=0.87) this gave me about another 10fps

-Enable VRS wide , this will give you maybe 2 to 4 fps with no visual loss , only at the very edges of the lens.

Optimized = 25 fps increase for me ,

There is no guaranteed frame rate or universal graphics preset. Compare one setting at a time on your headset and runtime.

## Configuration

Start with the settings panel. The new build layers configuration in this order:

1. Built-in fallback values.
2. The release's `Modules\BannerlordVR\BannerlordVR.default.cfg`.
3. Your personal overrides in:

   ```text
   Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
   ```

Keep the shipped default file with its build. Edit your **personal** file rather than the shipped file. The format is one `key = value` per line; lines beginning with `#` or `;` are comments. Restart after manual edits unless the panel applies that setting live.

| Key | Purpose |
| --- | --- |
| `controls_swap_trigger_grip = 1` | Grips hold items; index triggers serve firing / bumper actions |
| `grip_hold = 1` | Hold to keep an item; release to stow, drop, or throw |
| `holster = 1` | Enable body holsters |
| `bow_manual = 1` | Manual arrow pickup, nocking, and drawing |
| `xbow_manual = 1` | Manual crossbow loading and spanning |
| `stereo_mode = nativestereo` | Default; alternatives are `afr` and `depth` |
| `render_scale = 1.00` | Default per-eye resolution scale |
| `dpad_chord = 1` | Left-stick-click + right-stick D-pad shortcut |
| `recenter_hold_seconds = 3` | Controller recenter hold time; `0` disables it |
| `menu_hold_seconds = 2` | Empty-hand panel shortcut hold time; `0` disables it |
| `gamepad_deadzone` | Stick deadzone |
| `bow_full_draw_m` | Physical draw distance for full bow power |

To reset personal overrides, close the game and **back up, then rename** your personal config. On the next launch, the shipped defaults take over.

## Troubleshooting

| Problem | Check / fix |
| --- | --- |
| Physical controls are missing | Confirm you have the new physical-controls build, not v0.1.0. Check **Motion controls - full physical combat** in the panel |
| Mod is greyed out | Install and enable Harmony, enable the required base modules, and put Bannerlord VR below Harmony |
| Game starts flat | Start the headset/runtime first, select it as active OpenXR runtime, and inspect the native log |
| No log files | Check module enablement and the installation folder structure; the module may not be loading |
| Black headset, monitor looks normal | Check the native log for a GPU adapter mismatch; select the discrete GPU for Bannerlord |
| Hands are empty | Reach to a holster and hold the grip; spawning with stowed equipment is intentional |
| Weapon drops when you release | Expected with `grip_hold = 1`; release at the holster to stow it |
| Grips and triggers feel reversed | Check `controls_swap_trigger_grip` in the personal override file, then restart |
| Holster or quiver grab misses | Recenter, check body/height calibration, and reach near or behind the ears for shoulder zones |
| Weapon sits at the wrong angle | Select the correct controller calibration preset and calibrate each hand |
| World scale or height feels wrong | Recenter in a neutral pose, then check world scale and height in the panel |
| Menus are misplaced or hard to read | Adjust interface size/distance and HUD placement; try the stick pointer |
| Crash after updating | Check game version, matching mod files, and old config overrides; preserve both logs |
| Windows blocks a downloaded file | Check the ZIP's Properties for **Unblock**, if present, before extracting again. For a security detection, review Protection History and report the detection details with the build version |

Some menus and physical interactions remain unfinished. For reproducible issues, report the exact action and equipment involved.

## Bug reports and feedback

[Open an issue](https://github.com/NotLooky/BannerlordVR/issues/new/choose) and include:

- Bannerlord version and the exact mod build / archive name.
- Headset, controller, connection method, and OpenXR runtime.
- CPU, GPU, stereo mode, and any changed config settings or other enabled mods.
- Steps to reproduce, expected result, and what happened. For combat bugs, name the weapon and whether you were mounted.
- **Both logs**, plus a screenshot or short recording if useful:

  ```text
  Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log
  Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.Native.log
  ```

Logs are rewritten each launch. Copy them before relaunching; the previous run may also be available as `*.prev.log`. Review attachments for personal information before posting.

## Updating and uninstalling

The DLSS upgrade remains in the game bin after removing the mod. To restore the original, close the game, remove or disable BannerlordVR, back up the current `nvngx_dlss.dll`, and rename `nvngx_dlss.dll.bannerlordvr-original.bak` to `nvngx_dlss.dll`. Steam file verification can also restore the game's version.

**Update:** close the game, back up your personal config, and replace the mod with the complete new release. Keep its shipped defaults and DLLs together. Personal overrides remain in Documents; test without them if behavior changes unexpectedly.

**Disable:** untick **Bannerlord VR** in the launcher and restart the game.

**Uninstall:** close the game and remove `Modules\BannerlordVR`. Optionally remove its personal config and logs from Documents. Leave Harmony installed if other mods need it.

## Credits

The optional enemy physics (`enemy_physics = 1`, see [docs/enemy-physics.md](docs/enemy-physics.md)) is modelled on two VR melee games, whose mechanics and tuning it reproduces for Bannerlord:

- **[Blade & Sorcery](https://warpfrog.com/)** by **Warpfrog**: damagers and penetration, material and armour modifiers, dismemberment, blade stops on shields and weapons, grabbing, and the push and stagger reactions.
- **Tales of Glory** by **BlackTale Games**: the stab entry test (kinetic energy and thresholds by armour, helmet and mount), the stab joint, axe bites, blade recoil, killing-blow pushes, and the mounted hand-speed model.

No code, models, sounds or other assets from either game are included or redistributed. BannerlordVR is not affiliated with, approved by, or endorsed by Warpfrog or BlackTale Games.

## Development and license

Build instructions and toolchain details: [docs/BUILDING.md](docs/BUILDING.md). An installed copy of Bannerlord is required to compile against the game's assemblies. The included `BUILD-INFO.json` identifies the source commit and hashes of the packaged module. The public source branch may lag this binary build; check the commit before attempting to reproduce it.

For contributions, describe the issue or proposed change in an issue or pull request and include relevant validation. Rendering or input bugs benefit from exact runtime and hardware details.

BannerlordVR's own code uses the [MIT license](LICENSE). Third-party components retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Unofficial fan project. Not affiliated with, approved by, or endorsed by TaleWorlds Entertainment. No game files or game assemblies are redistributed.
