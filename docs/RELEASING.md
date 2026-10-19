# Preparing a public release

The current candidate is `0.2.6-alpha.1`, built against Bannerlord **1.4.8 stable** (Steam build 24573425). Use a new archive name for each candidate; never overwrite an already published archive.

1. Keep the launcher manifest numeric (`v0.2.6`), managed assembly/file versions `0.2.6.0`, and native version `0.2.6-openxr`. Put the alpha suffix only in the archive, release, tag, and changelog.
2. Rebuild native Release with `BVR_ENABLE_OPENXR=ON` and managed Release with `SkipDeploy=true`. Run the managed checks against the candidate defaults, then the projection and UI math checks.
3. Stage both fresh DLLs, `SubModule.xml`, and the effective shipped defaults under `Modules/BannerlordVR`. Keep native stereo and render scale 1.00 as release defaults. Preserve controller/equipment calibration. Disable diagnostic switches; exclude `cheeky_*` and personal one-shot markers. Do not change the owner's personal config.
4. Write player-facing README, changelog, and release notes. State the actual game version, changes, and untested behavior. Preserve owner edits from public main.
5. Run `tools/package-release.ps1 -Version 0.2.6-alpha.1 -BannerlordDir <staged-game> -ConfigPath <release-overrides>`. The packager reads a prepared module; use an isolated staging directory to avoid changing the live game. It bundles two mod DLLs; NVIDIA DLSS and third-party plugins are not included.
6. Verify ZIP integrity, safe paths, exact contents, x64 DLLs, module hashes, native version/API exports, default settings, and packaged documentation links. Write a SHA-256 sidecar and local validation report.
7. Install the exact ZIP into a clean module folder with personal overrides backed up and disabled. Check a battle on foot and mounted, weapons/ranged combat, calibration/settings persistence, campaign map, two mission re-entries, and shutdown. Record the result; automated checks cannot establish headset acceptance.
8. Prepare public documentation on an isolated branch based on current `origin/main`. Private development history and research stay local. Binary provenance belongs in `BUILD-INFO.json`; the public tag may identify the curated documentation commit instead.
9. When publishing is requested, attach ZIP and checksum to a GitHub **prerelease** with the release notes. Push only the intended public branch/tag. Download the uploaded ZIP and verify its hash before announcing it.

Release preparation alone does not publish, push, or tag anything. Mark exact-package headset acceptance pending until it has actually happened.
