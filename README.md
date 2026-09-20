# BannerlordVR

PCVR for **Mount & Blade II: Bannerlord**. 6DoF head tracking, native stereo
rendering, OpenXR.

Both eye views are rendered by the game's own engine into an OpenXR swapchain —
not a flat image reprojected into a headset. The world is at 1:1 scale.

> **Alpha.** The mod hooks a closed-source engine. Menus can be broken and
> crashes happen. No game files are redistributed.

## Features

* Full 6DoF head movement
* Native stereo rendering — both eyes rendered by the game engine
* AFR (Alternate Frame Rendering) for performance
* Depth reprojection — experimental, see Render modes
* Head-based aiming
* Motion controllers as a gamepad
* DLSS
* OpenXR

## Known issues

* Melee is awkward in VR
* Native stereo is very performance intensive
* Some UI elements are broken

## In development

* Full 6DoF motion controls
* VR-friendly UI
* Performance optimisation

## Requirements

* **Bannerlord v1.4.8** — required, not just recommended
* **[Bannerlord.Harmony](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006)**, enabled in the launcher
* Windows 10/11, 64-bit
* An active OpenXR runtime

Bannerlord is CPU-bound before VR is involved. VR adds to that.

## Tested

| Headset | Runtime | Status |
| ------- | ------- | ------ |
| Quest 3 | Virtual Desktop (VDXR) | Working |
| Quest 3 | SteamVR OpenXR | Working |
| Quest 3 | Meta/Oculus OpenXR | **Not supported** |
| PSVR2 | SteamVR OpenXR | Working |

Other headsets may work through SteamVR. Untested — open an issue either way.


## Installation

>BannerlordVR ZIP will be posted on the
> [Releases](../../releases) page.

1. Install and enable **[Harmony](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006)**.
2. Download the **BannerlordVR** release ZIP.
3. Extract into your Bannerlord folder:

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

   `bin` goes inside `Modules\BannerlordVR`, not the game's own `bin`.
4. Start your OpenXR runtime (Virtual Desktop Streamer or SteamVR) and confirm
   it is the active one.
5. Launch Bannerlord, enable **Bannerlord VR** in the launcher, below Harmony.


Greyed-out checkbox in the launcher = Harmony missing or not enabled.

### VR-Settings

| Key | Action |
| --- | ------ |
| `End` | In-VR settings panel |
| `F10` | Recenter |



## Motion Controls as gamepad

Your head is the camera. Your motioncontrollers are an **Xbox gamepad** — the game
never learns they are VR controllers, so every gamepad binding and on-screen
prompt works unchanged.

### In a battle

| VR input | Pad | In game |
| -------- | --- | ------- |
| Left stick | Left stick | Move |
| Right stick | Right stick | Camera |
| **Right trigger** | RT | **Attack** |
| **Left trigger** | LT | **Block** |
| A — right controller | A | Jump |
| B — right controller | B | Leave / back out |
| X — left controller | X | Kick |
| Y — left controller | Y | Interact — pick up, open, talk |
| Right grip | RB | Switch equipment |
| Left grip | LB | Show indicators/command army |

Attack direction follows the Left stick movement, Make sure you choose **Attack and Block by movement**.

### D-pad — press and hold left stick , move right stick up,down,right,or left

VR controllers have no D-pad and several screens bind nothing else. So it is a
chord: **press and hold left stick , move right stick up,down,right,or left**

| Direction | In a battle | In the order menu |
| --------- | ----------- | ----------------- |
| Up | Cheer | Toggle selection |
| Down | Crouch | Apply selection |
| Left | View character | Select left formation |
| Right | Push to talk | Select right formation |

### Giving orders

| VR input | Pad | Order |
| -------- | --- | ----- |
| X | X | Order 1 |
| A | A | Order 2 |
| B | B | Order 3 |
| Y | Y | Order 4 |
| Left grip | LB | Hold |
| Left menu button | Start | Return |

### Campaign map

| VR input | Pad | In game |
| -------- | --- | ------- |
| Right trigger | RT | Zoom in |
| Left trigger | LT | Zoom out |
| X | X | Pause / resume time |
| Right grip | RB | Fast forward |
| Left stick click | L3 | Camera follow mode |
| Right stick click | R3 | Track settlement |


## Render modes

Switch in the `End` panel. Takes effect at the next mission.

| Mode | How the second eye is produced | Cost |
| ---- | ------------------------------ | ---- |
| **AFR** — default | One eye is rendered per frame. The other is its own last real render, submitted with the pose it was drawn at so the compositor reprojects it. Exact geometry, one frame old. | Lowest |
| **Native stereo** | Both eyes rendered for real, from one engine frame. | Highest |
| **Depth reprojection** — experimental | One eye is rendered and the other is rebuilt from its depth buffer, so both eyes are the same instant. | Low |

**Depth reprojection has missing edges on the right eye.** Where a near object
hid something the rendered eye never saw, there is no data to rebuild from, so
those slivers are filled in rather than drawn. Most visible along the edges of
things close to you. Try it for the frame rate; expect the artifact.

Config: `stereo_mode = afr`, `nativestereo` or `depth`.



## What i noticed to improved performance
* Disable **Screen space reflection** for better performance
* Disable **Dynamic shadows**  
* Lower **Army Max sizes**
* Lower Resolution Scale
* Enable DLSS



## In-VR settings

Press `End`. Changes save immediately.

* Stereo mode — see above
* World scale
* Resolution scale
* CAS Sharpening
* UI overlay width and distance
* Flat-screen geometry



## Logs

```text
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.Native.log
```

Attach **both** to any issue. The native log records where the game died.

Rewritten every launch — grab them before relaunching. The previous run is kept
as `*.prev.log`.

Common failures: [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).


## License

BannerlordVR's own code: [MIT](LICENSE).

Third-party components keep their own licenses — Dear ImGui, MinHook, the
OpenXR SDK loader, JsonCpp, Harmony. See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Disclaimer

Unofficial fan project. Not affiliated with, approved by or endorsed by
TaleWorlds Entertainment. No game files or assemblies are redistributed.
This project is build with the help of Claude code.

**Single-player only.** `MultiplayerModule` is disabled deliberately: camera and
input hooks are indistinguishable from cheating to other players and to
TaleWorlds' server logic. Do not work around it.
This mod was build with the help of Claude code.
