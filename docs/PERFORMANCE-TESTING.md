# Performance candidate, 2026-10-14

This is a local test build, not a published release. The first stage removes
avoidable CPU/diagnostic work and unused sharpening allocations. It does not
change render scale, stereo mode, shaders, DLSS quality/history, shadows, troop
counts, combat rules or simulation frequency. Headset performance and visual
acceptance are still required. Passing offline tests cannot guarantee that every
game scene or runtime works.

## Recovery

Close Bannerlord and its launcher, then run:

```powershell
build\ROLLBACK-PERFORMANCE.cmd
```

This restores the exact two DLLs installed before this work. It validates their
SHA-256 hashes first and keeps a copy of the DLLs being replaced. It leaves
settings, saves and module metadata alone. Do not delete the checkpoint:

`build/perf-checkpoint-20261014-205231`

That directory also contains `source-before.zip`, the pre-existing dirty diff,
original module metadata/defaults, personal config and logs. Source recovery is
separate from binary recovery: extract selected files from the ZIP if needed;
do not reset the whole checkout and lose the owner's prior edits.

The reusable command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/perf-build.ps1 -Action Rollback -Checkpoint build/perf-checkpoint-20261014-205231
```

To reinstall this candidate after rollback:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/perf-build.ps1 -Action Install -Checkpoint build/perf-checkpoint-20261014-205231 -Candidate build/perf-candidate-20261014
```

The installer refuses a running game, a corrupted source DLL, or an unrelated
installed build. It backs up both current DLLs before replacing either, verifies
the installed hashes, and attempts to restore both if a copy fails. Recovery
itself was exercised against a disposable copy of the module.

## Implemented changes

| Change | Preserved behavior / fallback |
| --- | --- |
| Precompute texture-slot fetch ranges per shader | Same gap-eight grouping and live D3D context reads; unknown shaders still use the original path. No cache of mutable resource bindings. |
| Allocate sharpening copy-input textures only when direct reading fails | Same shader and output textures. Copy path remains for typed sRGB textures, missing SRV binding, or failed direct-view creation. |
| Avoid captured callbacks in hand visibility swaps | Same selection, attach/detach, visibility and ordering; original callback path retained. |
| Make detailed late-latch, ray and stereo census diagnostics opt-in | Real late-latching, upload pairing, projection measurement and lightweight timing remain active. |
| Disable implicit automatic frame captures | Manual trigger remains available; all hooks needed by stereo remain installed. |
| Honor `stereo_perf` for sampled per-operation CPU timers | Existing sample cadence retained while stereo is active. |

When both eyes use direct sharpening at 3556 x 3372, omitting two unused 32-bit
input textures avoids approximately 91.5 MiB of allocation. This is an allocation
saving, not a measured FPS gain. The direct path already avoided the copy before
this work; this change must not be described as removing two new per-frame copies.

Configuration switches are read at startup; restart the game after changes.
Personal config overrides module defaults. None of these keys were written into
the owner's settings as part of installation.

| Key | Built-in default | Use |
| --- | --- | --- |
| `perf_legacy` | `0` | `1` restores the previous optimization paths and diagnostic/capture defaults for comparison. Full DLL rollback is the exact original implementation. |
| `perf_shader_runs` | `1` | `0` recomputes texture fetch ranges per operation. |
| `perf_lazy_sharpen` | `1` | `0` allocates the copy-input textures eagerly. |
| `perf_direct_hand_swap` | `1` | `0` uses the original callback-based hand swap. |
| `perf_diagnostics` | `0` | `1` enables the detailed stereo census and late-latch inspection; ray census also requires `vp_ray_census=1`. |
| `frame_map_auto` | `0` | `1` enables the existing mission-delay capture, subject to `frame_map` and `frame_map_delay`. |
| `stereo_capture_after` | `0` | Explicit positive seconds requests the existing stereo-triggered capture. Legacy fallback is 15 seconds. |

An explicit `late_latch_probe=1` still enables its probe. `frame_map=1` still
retains shader bytecode for manual captures. Do not disable `vp_measure` or remove
the frame-map hooks: they also support functional stereo/camera behavior.

## Completed automated checks

- Baseline: 701 managed checks, 88 native projection checks, 41 UI math checks.
- Candidate: managed checks and native projection checks passed again; UI math
  code is unchanged from the passing baseline.
- Native and managed Release builds succeeded. The native build reported four
  existing unused-local warnings in untouched projection code; managed build had
  zero warnings.
- 296,624 declaration patterns matched independently calculated resource-fetch
  ranges, including both ends of the 128-slot space and the 64-bit boundary.
- Actual original sharpening source from the checkpoint was compiled alongside
  the candidate in the same D3D11 fixture. Original, legacy and optimized paths,
  with direct source reads both enabled and disabled, produced byte-identical
  output within each device: 1,152 cases on WARP and 1,152 on hardware D3D11.
  Cases cover both eyes, eight format variants, disabled/enabled sharpening,
  resize, same-size fallback transitions, unchanged source pixels, context
  restoration and teardown. WARP and hardware hashes need not equal each other.
- Recovery checks passed: installation, reinstall, exact rollback, corrupt
  candidate rejection, unchanged module defaults and metadata.

Reproduce the additional checks:

```powershell
powershell -File tools/run-perf-regression-tests.ps1 -Checkpoint build/perf-checkpoint-20261014-205231
powershell -File tools/run-perf-regression-tests.ps1 -Checkpoint build/perf-checkpoint-20261014-205231 -Hardware
powershell -File tools/test-perf-recovery.ps1 -Checkpoint build/perf-checkpoint-20261014-205231 -Candidate build/perf-candidate-20261014
```

Always pass `-p:SkipDeploy=true` to managed builds during this investigation. The
normal post-build deployment target also regenerates defaults from the personal
research settings; the performance installer deliberately copies just the DLLs.

## Headset baseline

Owner: Ryzen 7 9800X3D, RTX 4080 SUPER, Quest 3, VDXR, Godlike 110%.
Comparison run at 90 Hz, 3556 x 3372 per eye, native stereo, render scale 1.00.

| Scene / view | Owner-reported original FPS |
| --- | --- |
| Battle, 97 versus 93, looking toward army | 60-72 |
| Same battle, looking away from army | Locked 90 |
| Town walk | 50-65 across views |
| Tavern, looking at NPCs | 64-72 |
| Tavern, looking at wall | Locked 90 |

Logs are preserved in `build/perf-checkpoint-20261014-205231/headset-baseline`.
Scene IDs: `battle_terrain_b`, `empire_town_h`, `empire_house_c_tavern_a`.
The engine agent census includes more than the stated troop count (e.g. mounts);
do not treat its count as proof of the selected battle size.

Example army-view log windows around 22:09:53-22:09:58 report 14.9 ms frame
period, 13.93-13.96 ms GPU engine interval and 1.99-2.00 ms CPU stereo decisions.
At 22:10:18, looking away is consistent with 11.1 ms / 90 FPS and 0.55 ms CPU
stereo decisions. Town samples around 22:13:20-22:13:30 report 17.2 ms frame
period and 3.12-3.26 ms stereo decisions. These are overlapping intervals, not
costs to add. GPU timestamp intervals may include bubbles and waits. The bucket
named "after DLSS, before the next pass" is not an isolated DLSS measurement.
Five-second means and maxima are not frame-time percentiles.

The original build also triggered a four-frame capture at 22:09:58 despite a
99999-second frame-map delay, through the separate stereo capture timer.

## Acceptance and next stages

1. Repeat the same three scenes and view directions at 90 Hz, same settings,
   save, location and troop composition. Wait at least 30 seconds after loading;
   hold each view for about 30 seconds, repeat three times. Archive logs between
   builds. Compare stable sections separately from loading, menus and captures.
2. Check both eyes, image edges, shadows, hair, DLSS motion history, head turns,
   hands/fingers, weapon switching, melee, bow/crossbow, HUD and mission exits.
   Any reproducible visual or interaction regression means rollback, then isolate
   the affected switch. A faster build with missing features is not accepted.
3. Treat a small or inconsistent FPS change as inconclusive. Revert and rerun the
   original if needed. Do not claim gains from changing the scene or refresh cap.
4. Larger GPU work remains a separate measured stage: identify costly passes and
   shaders in paired captures before changing duplicate-work rules. A runtime
   visibility mask is a candidate only after checking support and proving it
   cannot remove visible pixels or corrupt temporal history. Do not share a pass
   between eyes merely because its target size looks familiar.
5. More extensive D3D binding caches need explicit invalidation for every binding
   mutation, automatic unbinding, ClearState and command-list playback. Combat
   query batching needs conservative coverage and unchanged timing/order. Neither
   is enabled in this candidate; both need their own comparison and fallback.

## First candidate result

The owner repeated the battle, town and tavern run and reported **no noticeable
difference**. Custom battle generation varies, so that run is not a controlled
before/after comparison. Candidate logs are preserved in
`build/perf-checkpoint-20261014-205231/headset-candidate`, with timing CSVs beside
both sets of logs. Town samples still take roughly 16-18 ms per frame; neither
FPS nor CPU stereo-decision times demonstrate a clear improvement. Do not claim
that these changes fixed the NPC performance problem.

The new paths were active, automatic capture was disabled, and camera self-tests
finished at 57 passed / 0 broken. The run shut down cleanly. No visual regression
was reported, but the owner did not provide a complete combat/feature acceptance
checklist. Actual VRAM use stayed below its budget; cumulative twin allocation
counters are not evidence of a live memory leak.

The follow-up diagnostic was an NPC-facing tavern capture (results below). Recent
tavern GPU intervals include a six-target 1778x1686 geometry pass around 2.5-2.8 ms,
a 6144-KB-buffer compute pass around 2 ms, and an approximately 4.4-4.7 ms interval
after DLSS. Their exact shader/resource dependencies must be identified before
changing stereo sharing rules. The runtime advertises `XR_KHR_visibility_mask`,
but this candidate does not enable or apply it; compatibility and temporal-history
correctness still need proof. No further GPU optimization has been deployed.

No confirmed headset FPS improvement or complete in-game acceptance has been
recorded. Do not publish this candidate as a proven performance release.

## Stationary tavern capture comparison

The owner held the NPC-facing view and then turned toward a wall. Four-frame
captures completed at 23:01:55 and 23:05:51, with all used
shader bytecode retained (141 and 76 shaders respectively). Copies, reports and
`comparison.json` are in the checkpoint's `tavern-profiling` directory.

| Metric | NPC view | Wall view |
| --- | ---: | ---: |
| Recorded draws per captured frame, excluding Present work | 664 | 187.75 |
| Recorded compute dispatches per captured frame, excluding Present work | 32 | 14 |
| Nearby stable five-second FPS reports | 64.1-64.5 | 89.5 |
| GPU engine interval in those reports | 14.46-14.55 ms | 10.05-10.07 ms |
| CPU stereo decisions | 1.14-1.18 ms | 0.21-0.23 ms |
| Six-target geometry pass | 3.33-3.47 ms | 0.52-0.61 ms |
| HDR-format draw pass at internal resolution | 2.10-2.19 ms | 0.78-0.80 ms |
| Compute group writing the 6 MiB buffer | 2.20-2.25 ms | 1.35 ms |

Timing rows come from nearby steady windows, not the capture frames. Pass
intervals are attribution buckets, not isolated shader microbenchmarks. These
figures compare two view directions on the same candidate, not an optimization
gain against the original DLLs. The recovered engine positions differ by about
1.04 m: NPC (8.60294, 17.19702, 1.87734), wall (7.90913, 16.43069, 1.81262).
This is a same-scene comparison, not an exact fixed-position measurement.

The 6 MiB group includes shaders `90c3c702daef9dab`, `a7050978186c13be`,
`9c0bc03cbf729632`, `33c3befaac17dfbe`, `beebc61529621e52`, and
`4e077dc981c54eee`. Their declarations use dynamic constant buffer 11, raw vertex
inputs and high resource slot 68, with float/integer UAV outputs. Their structure
is consistent with character vertex/skinning work. They do not declare the main
camera buffer merely because it remains bound in slot 0; the current stereo code
already checks declarations. Do not introduce a blanket "skip character compute
for eye 1" rule based on this timing label.

The capture originally recorded only slots 0-47. This omits slot 68 and limits
resource-dependency conclusions. Source now captures all 128 D3D11 SRV slots and
writes `srv_slots=128` into the capture header. That diagnostic correction builds
successfully and is staged separately in `build/perf-capture-fix-20261014`.
It is now **installed and its headset capture is verified** (details below). Both installed DLL
hashes match that candidate manifest; the original default config and module
metadata hashes remain unchanged. Snapshot work remains gated by an active
capture, so it introduces no additional ordinary-frame D3D queries. Ordinary
rendering uses the same paths as the first performance candidate. The original
rollback command still restores both pre-test DLLs.

The primary follow-up is GPU geometry/shading work, with complete resource
captures and a fixed tavern view. Runtime visibility-mask support is confirmed
by extension enumeration, but merely enabling the extension does not save any
rendering work. Any integration must preserve the visible region, per-eye FOV,
depth/stencil contents and temporal history, and retain an off switch. There is
currently no demonstrated, large, quality-preserving FPS fix ready to deploy.

## Visibility-mask investigation

`tools/xr-visibility-probe.cpp` is a standalone diagnostic, not part of the mod.
It creates an OpenXR/D3D11 session without beginning it or rendering, validates
mask sizes and indices, and prints the runtime's geometry. The first successful
query returned zero vertices and indices for both eyes' hidden and visible masks.
A repeat identified VirtualDesktopXR 1.1.0 but returned
`XR_ERROR_FORM_FACTOR_UNAVAILABLE` from `xrGetSystem`; the headset was unavailable.
After the owner closed Bannerlord and left Quest 3 connected, the probe completed
successfully (exit 0) on VirtualDesktopXR 1.1.0. All four hidden/visible eye masks
again contained zero vertices and indices. The confirmed output and runtime identity
are saved in `tavern-profiling/visibility-connected-confirmed*.{json,txt}`.
There is no runtime-supplied mask to apply in this tested configuration. This does
not establish behavior on other headsets, runtime versions or settings. No masking
change has been implemented.

The [VDXR implementation](https://github.com/mbucchia/VirtualDesktop-OpenXR/blob/main/virtualdesktop-openxr/visibility_mask.cpp)
queries the underlying runtime stencil after session creation; it does not require
the OpenXR session to have begun. It can also return empty geometry for an oversized
mask setting. This source inspection does not establish which condition produced
the local result. Revisit only with a connected headset and measured mask geometry.

## Full-slot NPC capture and GPU utilization

Capture `20261014_233637` completed four frames with `srv_slots=128`, 284 resources,
310 views, and all 141 used shaders with bytecode. The archived copy is
`tavern-profiling/npc-full-slots`; its report, summary JSON, GPU samples and final
game logs are alongside it. Assertions confirmed the header and captured slots
67/68; bindings extend through slot 92. Ordinary recorded draws were 663, 663,
662 and 660 across the four frames (second-eye reissues are not independently
recorded by these draw hooks).

Excluding the first five samples around capture, 25 one-second GPU samples show
98-99% utilization (98.48% mean). Nearby steady reports show 64.7-65.4 FPS,
14.22-14.39 ms engine GPU interval and about 1.06-1.08 ms for our Present GPU work.
Combined, these support a GPU bottleneck for this NPC view, not just a long
timestamp interval containing CPU waits. They do not isolate individual shader
costs. The geometry bucket is 3.22-3.42 ms, character-compute bucket 1.97-2.02 ms,
HDR draw bucket 1.83-1.93 ms, and post-DLSS interval 4.36-4.40 ms. These are
attribution buckets, not costs to add to frame time or an isolated DLSS duration.

The six character-compute variants bind slot 68 to a CPU-writable 384 KiB SRV
buffer; one variant also binds slot 67 to an 8 KiB CPU-writable SRV buffer. They declare
CB11 and vertex/bone inputs, not the main camera CB0. These previously omitted
bindings do not reveal an eye-image dependency or justify skipping character
work. Inferring actual duplication requires examining the stereo decision or
resource divergence, not merely seeing a buffer bound during a dispatch.

Camera-hook self-tests finished at 42 passed / 0 broken. The game shut down
cleanly, and the installed candidate and original recovery
files were hash-verified again. No visual issue was reported; a complete feature
and combat acceptance test is still outstanding. The diagnostic correction does
not change ordinary rendering and is not an FPS improvement claim.

The next substantial optimization must identify redundant GPU work within the
geometry/shading or postprocessing paths, prove the affected inputs/outputs remain
equivalent for each eye, and compare its images and frame times against this build.
CPU cleanup alone has not solved this scene. Do not remove depth submission,
per-eye history, sharpening, shadows or character work to manufacture a gain.
