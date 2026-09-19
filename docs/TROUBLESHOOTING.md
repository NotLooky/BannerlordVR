# Troubleshooting

Start with the logs. Both are rewritten on every launch, and the previous run is
kept beside them as `*.prev.log`:

```
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log          (managed)
Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.Native.log   (native)
```

Attach **both** to any bug report. The native log carries a crash handler that
records where the game died; without it a crash report is a guess.

---

## The module is greyed out in the launcher

A dependency did not resolve. In practice this is always Harmony: install
**Bannerlord.Harmony** and enable it, and make sure BannerlordVR is below it in
the load order.

## The game starts flat and the headset never wakes up

By design, every failure in the VR stack drops to flat mode instead of crashing
the game — so "it just runs normally" is what a missing runtime looks like.
`BannerlordVR.Native.log` says which one it was.

- **`bvr_init failed: NoRuntime`** — no OpenXR runtime was active when the game
  started. Start Virtual Desktop Streamer or SteamVR *first*, confirm it is the
  **active** OpenXR runtime, then launch the game.
- **MetaXR / the Oculus OpenXR runtime is not supported.** On a Quest, use VDXR
  (Virtual Desktop) or SteamVR.
- **No log at all** — the module is not loading. Check the launcher's Mods tab,
  and check that `bin\Win64_Shipping_Client\` inside the module folder actually
  contains `BannerlordVR.dll`, `BannerlordVR.Native.dll` and
  `openxr_loader.dll`.

## Black headset, but the monitor looks fine

Almost always a **GPU adapter mismatch**. If your CPU has integrated graphics,
the OpenXR runtime may be bound to a different adapter than the one Bannerlord
picked. The native log prints both adapter LUIDs and says whether they matched.
Force Bannerlord onto your discrete GPU in Windows' graphics settings.

## The view does not follow my head

Press `F10` to recentre. If it still does not move, the log will say whether the
VR camera ever took over; report it with both logs.

## Everything is doll-sized, or I am a giant

World scale. Open the in-VR panel with `End` and move the world-scale slider
until the ground is where your feet expect it. It saves immediately.

## The frame rate is bad

Bannerlord is CPU-bound before VR is involved, and VR roughly doubles what the
GPU has to do. In the `End` panel, in rough order of what it buys you:

1. **Stereo mode → AFR.** Native stereo renders both eyes for real and is much
   more expensive. AFR is the shipping path.
2. **Resolution scale** down. Live, up to the size the eye targets were
   allocated for at launch; above that it waits for a relaunch.
3. **DLSS** on, in the game's own graphics settings.
4. The game's own settings — shadows and crowd density are the usual suspects.

## Menus and UI are broken or in the wrong place

Known, and being worked on. The `End` panel has **UI overlay** width and
distance and the flat screen's geometry, which can make an unreadable menu
readable in the meantime.

## My own body is in my face

`End` panel → **Hide body**.

## The hands are holding the weapon wrong

`Home` starts grip calibration. `F11` restores your saved calibration if you
lose it, and `Insert` falls back to the built-in default. Calibration is saved
the moment it changes.

## Melee combat feels terrible

Accurate, and known. Full 6DoF motion controls for combat are what is being
built now. Until they land, melee runs on the gamepad mapping like everything
else.

---

## Reporting something not listed here

[Open an issue](../../issues/new/choose) with:

- both log files
- your headset and OpenXR runtime
- your GPU
- the stereo mode you were in, and whether it happens in every mission or one

If it is a crash, say what you were doing in the ten seconds before it — the
engine gives no error text of its own, so the description is real evidence.
