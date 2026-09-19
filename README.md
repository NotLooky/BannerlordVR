# BannerlordVR

Native stereo PCVR for **Mount & Blade II: Bannerlord**.

Two real eye views rendered by the game's own engine and submitted to an OpenXR
swapchain — not a flat image reprojected into a headset. You stand in the
battle line at 1:1 scale, lean out of it, and look around with your own head.

> **Alpha.** This is an early build of a mod that hooks a closed-source engine.
> Expect rough edges, broken menus, and the occasional crash. It never
> redistributes game files, and it always falls back to flat mode rather than
> taking the game down with it — but treat it as an experiment you are joining,
> not a finished product.

---

## Current features

- **Full 6DoF head movement** — lean, duck, look around, at real scale
- **Native stereo rendering** — both eyes drawn by the engine
- **AFR** (Alternate Frame Rendering) for performance
- **Head-based aiming** (for now)
- **Motion controllers working as a gamepad** — not full 6DoF controls yet
- **DLSS support**

## Current issues

- Melee combat is awkward and difficult to use in VR
- Native stereo rendering is very performance-intensive
- Some UI elements are currently broken

## In development

- Full 6DoF motion controls for combat and navigation
- Proper VR-friendly UI
- More optimisations

---

## Requirements

- **Mount & Blade II: Bannerlord v1.4.8** (this exact version — the mod reads
  engine internals that move between patches)
- **Harmony** — the latest [Bannerlord.Harmony](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006)
  module, enabled in the launcher
- Windows 10/11, 64-bit
- An OpenXR runtime (see *Tested on* below)

Bannerlord is a very CPU-bound game and already asks for a beefy PC.
Unfortunately, VR is even more demanding. Plan accordingly.

## Tested on

| Headset | Runtime | Status |
|---|---|---|
| Quest 3 | VDXR (Virtual Desktop) | Working |
| Quest 3 | SteamVR OpenXR | Working |
| PSVR2 | SteamVR OpenXR | Working |
| Quest 3 | **MetaXR / Oculus OpenXR** | **Not supported** |

Other headsets may well work through SteamVR — nobody has reported back yet. If
you try one, please [open an issue](../../issues) and say what happened.

---

## Install

> **There is no download yet.** The first alpha build will be posted on the
> [Releases](../../releases) page when it is ready — watch this repository to
> hear about it. Until then the only way to run it is to
> [build it yourself](docs/BUILDING.md), which needs the game plus a C++ and
> .NET toolchain.
>
> The steps below are how installing it will work, and are here so you know what
> you are in for.

1. Install and enable **Bannerlord.Harmony** first, if you have not already.
2. Download the latest `BannerlordVR-*.zip` from the
   [Releases](../../releases) page.
3. Extract it into your Bannerlord folder so that you end up with:

   ```
   Mount & Blade II Bannerlord\Modules\BannerlordVR\
       SubModule.xml
       bin\Win64_Shipping_Client\BannerlordVR.dll
       bin\Win64_Shipping_Client\BannerlordVR.Native.dll
       bin\Win64_Shipping_Client\openxr_loader.dll
   ```

   The Steam default is
   `C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord`.
4. Copy the `BannerlordVR.cfg` from the zip into
   `Documents\Mount and Blade II Bannerlord\Configs\`. This is the tested
   configuration; without it you get the code defaults, which are not the
   settings this release was judged on.
5. Start your headset's OpenXR runtime **before** launching the game — Virtual
   Desktop Streamer, or SteamVR. Make sure it is the *active* OpenXR runtime.
6. Launch Bannerlord however you normally do (Steam is fine), tick
   **Bannerlord VR** in the launcher's Mods tab, and make sure it sits *below*
   Harmony in the load order.
7. Load a mission — a custom battle is the quickest way in.

If the launcher greys out the checkbox, a dependency is missing: that is almost
always Harmony not being installed or not being enabled.

## Controls

The headset is your head. The controllers act as an Xbox pad, so everything the
game does with a gamepad works today; full motion controls are the next thing
being built.

| Key | What it does |
|---|---|
| `End` | Open the **in-VR settings panel** |
| `F10` | **Recentre** — re-anchors forward and position to where you are facing now |
| `Home` | Start grip calibration (align your real hand to the in-game hand) |
| `F11` | Restore your saved grip calibration |
| `Insert` | Fall back to the built-in default grip |

Yes, they are keyboard keys, and yes, reaching for a keyboard in a headset is
its own small indignity. They are chosen so you can find them by feel, and
moving them onto a controller chord is on the list.

## The in-VR settings panel

Press `End`. Almost nothing needs the config file, because the panel covers what
you actually want to change mid-session, and **saves every change immediately**:

- **Stereo mode** — AFR or native stereo (takes effect at the next mission)
- **World scale** — how big the world feels
- **Resolution scale** and **sharpening**
- **Hide body** — the player's own body, which is in your face by default
- **Motion hands** — experimental motion-control hands and melee
- **UI overlay** width and distance, and the flat screen's geometry
- **Hand calibration**

## Configuration

Everything else lives in one file:

```
Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg
```

Every line is `key = value`; `#` or `;` starts a comment. Edit, save, restart
the game. See [docs/CONFIG.md](docs/CONFIG.md) for the settings worth touching
and what they cost you.

## When something goes wrong

Two logs, both rewritten on every launch:

```
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log          (managed)
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.Native.log   (native)
```

[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) covers the failures that look
like something else: black headset, no head tracking, frozen menus, the module
not loading at all.

**Reporting a bug:** open an [issue](../../issues/new/choose) and attach both
logs. The native log carries a crash handler that records where the game died,
which is usually the difference between a fixable report and a guess.

## Building from source

You need the game installed — the mod compiles against its assemblies and none
of them are redistributed here. See [docs/BUILDING.md](docs/BUILDING.md).

## License

BannerlordVR's own code is released under the [MIT License](LICENSE).

Third-party components remain the property of their respective authors and are
distributed under their own license terms; the MIT license does not relicense
them. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the full notices
covering Dear ImGui, MinHook, the OpenXR SDK loader, JsonCpp and Harmony.

## Disclaimer

> This is an unofficial fan work and is not approved or endorsed by TaleWorlds
> Entertainment.

BannerlordVR is not affiliated with TaleWorlds Entertainment. Mount & Blade II:
Bannerlord and all related assets are the property of TaleWorlds Entertainment.
No game files or assemblies are redistributed by this project; the mod resolves
them from your existing installation.

**Single-player only.** `MultiplayerModule` is false deliberately: camera and
input hooks are indistinguishable from cheating, both to other players and to
TaleWorlds' server logic. Do not try to work around this.
