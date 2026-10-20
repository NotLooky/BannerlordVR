# Changelog

## v0.2.6-alpha.2 ? 2026-10-20

Built against **Bannerlord v1.4.8** (stable, Steam build 24573425). Headset testing of this exact ZIP is pending.

- Fixed DLSS softness with cropped fields of view and added DLSS preset selection and a resolution readout.
- Added in-VR controller and equipment calibration with saved pose profiles.
- Added optional toggle battle grips; bowstrings remain hold-to-draw and release-to-fire.
- Improved bowstring rendering, camel camera and rider alignment, and camera height after falling from a mount.
- Fixed disappearing hands and weapons in the practice arena.
- Menus and screens now follow the configured auto-recentre angle. Large modded mounts retain first-person view.
- Added detailed field-of-view crop controls; removed the experimental true-vertical panel control.
- Native stereo at 1.00 render scale and hold-to-grab remain the defaults. Bundled NVIDIA DLSS 310.9.1.0 upgrades an older game runtime at startup, preserving its original backup and any equal/newer installed version. Restart once if the old runtime was already loaded.
- **AMD graphics cards are not fully supported.** Single-player only; use SteamVR or VDXR.

## v0.2.5 — 2026-10-16

- Sharper, steadier VR menus, HUD and campaign-map labels.
- Better pre-battle formation controls; commander portraits on the left-hand panel are now clickable.
- Fixed black banners, stereo artifacts, disappearing fingers and mounted positioning.
- Improved crossbow handling, throwing-weapon weight and siege interactions.
- Updated default settings and saved controller/weapon calibration. Native stereo at 1.00 resolution scale remains the default.

Built against **Bannerlord v1.5.5**. **AMD graphics cards are not fully supported.** Development quality; glitches and crashes are possible.

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

## v0.1.0 alpha — 2026-09-20

- Initial public PCVR release with 6DoF head tracking, OpenXR, stereo rendering, gamepad-style controller input, and an in-VR settings panel.
