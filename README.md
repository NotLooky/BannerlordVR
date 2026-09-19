# BannerlordVR

**Stand in the shield wall.** A VR mod for **Mount & Blade II: Bannerlord** that
brings the game into PCVR with 6DoF head tracking, native stereo rendering, and
OpenXR support.

Two real eye views are rendered by the game's own engine and submitted to an
OpenXR swapchain — not a flat image reprojected into a headset. So the hillside
has actual depth, a couched lance arrives with actual distance, and when five
hundred Khuzait horsemen come over the ridge you find out how tall a horse
really is. Duck under a swing. Lean out of the line to see how far it stretches.
Look *up* at the castle wall you are about to climb.

It is the same Bannerlord, at 1:1 scale, with your own head.

> **Alpha warning:** This is an early build. The mod hooks into parts of
> Bannerlord's closed-source engine, so there are rough edges. Menus can be
> broken, crashes can happen, and some features are still experimental. No game
> files are redistributed.

## Current Features

* Full 6DoF head movement
* Native stereo rendering — both eyes are rendered by the game engine
* AFR (Alternate Frame Rendering) for improved performance
* Head-based aiming
* Motion controllers working as a gamepad — full 6DoF controller support is still in development
* DLSS support
* OpenXR support

## Current Issues

* Melee can be awkward and difficult in VR
* Native stereo rendering is very performance intensive
* Some UI elements are broken
* Motion controllers do not yet provide full 6DoF interaction

## In Development

* Full 6DoF motion controls
* Proper VR-friendly UI
* More performance optimisations
* Improved VR interaction

## Requirements

* **Mount & Blade II: Bannerlord v1.4.8** — this version is currently required
* **Harmony** — install the latest `Bannerlord.Harmony` from Nexus Mods and enable it in the Bannerlord launcher
* Windows 10/11 64-bit
* An active OpenXR runtime

Bannerlord is already a CPU-bound game, and running it in VR adds additional
rendering and CPU workload.

## Tested

### Quest 3

* **Virtual Desktop + VDXR** — Working
* **SteamVR OpenXR** — Working
* **Meta/Oculus OpenXR runtime** — Not currently supported

### PSVR2

* **SteamVR OpenXR** — Working

Other headsets may work through SteamVR/OpenXR, but have not been tested. If you
get it working on another headset, please open an issue and let me know.

## Installation

> **Note:** The first alpha release will be available through the GitHub
> **Releases** page.

### 1. Install Harmony

Install and enable **Bannerlord.Harmony** through Nexus Mods.

Harmony is a separate dependency and is **not included** with BannerlordVR.

### 2. Download BannerlordVR

Download the latest BannerlordVR release from the **Releases** page.

### 3. Extract the mod

Extract the contents of the release ZIP into your Bannerlord installation
folder.

The installation should look like:

```text
Mount & Blade II Bannerlord\
└── Modules\
    └── BannerlordVR\
        ├── SubModule.xml
        └── bin\
            └── Win64_Shipping_Client\
                ├── BannerlordVR.dll
                ├── BannerlordVR.Native.dll
                └── openxr_loader.dll
```

The `bin` folder belongs **inside** `Modules\BannerlordVR`, not in the game's own
`bin` folder at the root of the installation.

### 4. Start your OpenXR runtime

Start your preferred OpenXR runtime before launching Bannerlord.

For example:

* Virtual Desktop Streamer / VDXR
* SteamVR

Make sure the correct OpenXR runtime is active before starting the game.

### 5. Launch Bannerlord

Start Bannerlord and enable **Bannerlord VR** in the launcher.

Make sure **Bannerlord VR is loaded below Harmony** in the module load order.

### 6. Start a mission

Load into a mission or custom battle.

A custom battle is usually the quickest way to test whether everything is
working.

> If the Bannerlord VR checkbox is greyed out in the launcher, the dependency is
> usually missing or Harmony is not installed/enabled correctly.

## Controls

The headset controls your head position and rotation.

For now, VR controllers behave like an Xbox gamepad rather than providing full
6DoF interaction.

### Keyboard

| Key      | Action                   |
| -------- | ------------------------ |
| `End`    | Open in-VR settings      |
| `F10`    | Recenter headset         |
| `Home`   | Start grip calibration   |
| `F11`    | Restore grip calibration |
| `Insert` | Restore default grip     |

Using a keyboard while wearing a VR headset is, unfortunately, part of the
experience for now.

## Motion Controller Bindings

Your controllers are presented to Bannerlord as an **Xbox gamepad**. The game
never learns they are VR controllers, which is the point: every existing gamepad
binding, every controller prompt and every menu that already understood a pad
works untouched. Nothing needs rebinding in the game's own controls screen.

Enabled by default. Set `gamepad = 0` in the config to turn it off and play on
keyboard and mouse.

### The direct mapping

| VR controller             | Reported as              | Notes                                                              |
| ------------------------- | ------------------------ | ------------------------------------------------------------------ |
| Left thumbstick           | Left stick               | Analog. Movement.                                                   |
| Right thumbstick          | Right stick              | Analog. Camera.                                                     |
| Left thumbstick click     | Left stick click (L3)    | Also the D-pad modifier — see below.                                |
| Right thumbstick click    | Right stick click (R3)   |                                                                     |
| Left trigger              | Left trigger             | **Analog** — the squeeze keeps its travel, it is not an on/off switch. |
| Right trigger             | Right trigger            | **Analog**, same.                                                   |
| Left grip                 | Left bumper (LB)         | A threshold, not an axis — a grip is a squeeze, a bumper is a click. |
| Right grip                | Right bumper (RB)        | Same.                                                               |
| **X** (left controller)   | **X**                    |                                                                     |
| **Y** (left controller)   | **Y**                    |                                                                     |
| **A** (right controller)  | **A**                    |                                                                     |
| **B** (right controller)  | **B**                    |                                                                     |
| Menu button (left)        | Start                    | Start rather than Back: Start is what opens things, and closing already has a face button. |

Touch and Sense controllers split the four face buttons across two hands — X and
Y on the left, A and B on the right — while an Xbox pad has all four in one
cluster. Both hands feed the same four buttons, so nothing is lost and nothing
collides.

### The D-pad chord

VR controllers have no D-pad. Bannerlord wants one: several screens bind the
D-pad directions and nothing else, so without it those screens are simply
unreachable.

> **Hold the left thumbstick in, then push the right thumbstick.**
> That is your D-pad.

Everything else on that hand keeps working while you hold it — triggers, grip
and face buttons are all still live. Only the right stick is borrowed, and only
while you hold the click. The right stick was chosen over the left deliberately:
a chord that walked you across the battlefield while you pressed it would be its
own kind of problem.

Set `dpad_chord = 0` in the config to turn it off.

### The pointer, for menus

Bannerlord's interface is only half navigable by direction. The map, the
inventory grid, anything with a scrollbar or a drag — all of it assumes a mouse,
and no amount of up/down/left/right reaches those.

So when a menu is open, a stick drives the **real** cursor:

| Input                          | Does                                            |
| ------------------------------ | ----------------------------------------------- |
| Left thumbstick                | Moves the mouse pointer                         |
| Right trigger                  | Left mouse button                               |
| Left trigger                   | Right mouse button                              |

It moves the engine's own cursor, so hover states, tooltips, hit testing and
dragging all behave exactly as they do with a real mouse. While the pointer has
the stick, that stick does nothing else — no panning the map behind the menu and
no scrolling the list you are aiming at.

It only runs in menus. During play the right stick is the camera and the
triggers are your weapons, and binding a mouse click there would fire both.

Config: `stick_mouse = 0` turns it off, `stick_mouse_stick = right` moves it to
the other stick, `stick_mouse_speed` and `stick_mouse_deadzone` tune it.

### Deployment

While you are placing troops before a battle, the left trigger reports as held
so that the left stick flies the deployment camera outright.

On a couch, Bannerlord makes you hold that trigger to move the camera, because
the stick otherwise belongs to troop selection. In a headset, the one phase
where you are flying a camera over a battlefield became the one phase the stick
felt dead — so the mod holds it for you.

### Tuning

| Key                 | Default | What it does                                      |
| ------------------- | ------- | ------------------------------------------------- |
| `gamepad`           | `1`     | The whole pad emulation                           |
| `gamepad_deadzone`  | `0.18`  | Stick deadzone                                    |
| `gamepad_digital`   | `0.6`   | How far a stick must go to count as a direction   |
| `gamepad_trigger`   | `0.5`   | Where a trigger counts as pressed                 |
| `gamepad_grip`      | `0.5`   | Where a grip counts as a bumper                   |
| `dpad_chord`        | `1`     | The D-pad chord                                   |

If reading the controllers ever fails, the pad switches itself off for the rest
of the session and the keyboard carries on unaffected. It is written down in
`BannerlordVR.log` when it happens.

### Not the same thing as motion hands

**Motion hands** — the experimental toggle in the in-VR settings panel — is the
separate, unfinished work on hands and melee that actually track your
controllers in space. The gamepad emulation above keeps working either way, and
it is what you play with today.

## In-VR Settings

Press `End` to open the in-VR settings panel.

Changes are saved immediately.

Available settings include:

* Stereo mode

  * AFR
  * Native Stereo
* World scale
* Resolution scale
* Sharpening
* Hide body — on by default; turn it off to see your own body
* Motion hands — experimental
* UI overlay width
* UI overlay distance
* Flat-screen UI geometry
* Hand calibration

Some settings, such as stereo mode, take effect on the next mission.

## Configuration

**You do not need a configuration file.** The mod ships with the settings it was
tested on as its built-in defaults, and the in-VR panel covers the rest. This
section is for tuning, not for installing.

If you want to change something the panel does not offer, create:

```text
Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
```

The file uses simple `key = value` entries. A line beginning with `#` or `;` is
a comment. Only the keys you write are overridden; everything else keeps its
default. Some changes require restarting the game.

Deleting the file puts everything back to defaults.

More information is available in [docs/CONFIG.md](docs/CONFIG.md).

## Logs

BannerlordVR creates two log files:

```text
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.Native.log
```

If you encounter a crash or other problem, please attach both logs when opening
an issue.

The native log also contains information from the native crash handler, which
records where the game died — usually the difference between a report that can
be fixed and a guess.

Both are rewritten on every launch, so grab them before relaunching. The
previous run is kept beside them as `BannerlordVR.prev.log` and
`BannerlordVR.Native.prev.log`.

See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) for additional
information.

## Build From Source

You will need a working installation of Bannerlord to build the project.

The project compiles against the game's assemblies. No Bannerlord game files or
assemblies are redistributed with this repository.

See [docs/BUILDING.md](docs/BUILDING.md) for build instructions.

## License

The original code written for BannerlordVR is released under the
**[MIT License](LICENSE)**.

Third-party components retain their respective licenses.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for information about:

* Dear ImGui
* MinHook
* OpenXR SDK loader
* JsonCpp
* Harmony

## Disclaimer

BannerlordVR is an unofficial fan-made project.

It is not affiliated with, approved by, or endorsed by **TaleWorlds
Entertainment**.

This project does **not** redistribute Bannerlord game files or game assemblies.

BannerlordVR is intended for **single-player use only**.

The `MultiplayerModule` is deliberately disabled because the mod uses camera and
input hooks that are not appropriate for multiplayer/server logic.

Do not use BannerlordVR to bypass or interfere with Bannerlord's multiplayer
protections.
