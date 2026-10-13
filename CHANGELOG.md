# Changelog

## v0.2.0-alpha.1 — 2026-10-13

### Added

- Physical melee: swing or thrust to attack, and block with your weapon or shield.
- Tracked hands, body holsters, grabbing, two-handed grips, and throwing weapons.
- Manual bows and crossbows: grab ammunition, load, draw or span, and release/fire.
- Body IK and room-scale movement, horse reins, ladder climbing, and siege interactions.
- Controller menu pointing, wrist HUD, campaign-map controls, and hand calibration.

### Improved

- Hands and weapons stay hidden before battle; pre-battle menus support controller pointing.
- Right index trigger confirms move-to-flag during deployment.
- **Native stereo is the default render mode. Resolution scale defaults to 1.00.**

### Compatibility and known issues

- This ZIP was built and checked against **Bannerlord v1.5.5** with Harmony. Other game versions were not verified for this package.
- Alpha: some menus/interactions remain unfinished, performance varies, and crashes are possible.
- AFR is optional. Depth reprojection is experimental and can show edge artifacts.
- A handled view-restoration error can appear in the log during shutdown; the tested session still exited normally.
- Single-player only. Meta/Oculus OpenXR is not supported; use VDXR or SteamVR.

### Validation

- 830 automated checks passed, along with DLL and archive validation.
- The owner reported all requested headset checks passed on VDXR, including native stereo.
- Release startup defaults were then set to native stereo at 1.00 scale; the tested DLLs are unchanged.

## v0.1.0 alpha — 2026-09-20

- Initial public PCVR release with 6DoF head tracking, OpenXR, stereo rendering, gamepad-style controller input, and an in-VR settings panel.
