# Building BannerlordVR

You need Bannerlord installed. The mod compiles against the game's own
assemblies and none of them are redistributed here, which is also why there is
no CI build: a build machine has no copy of the game.

## Toolchain

- .NET SDK 10 (the solution is `.slnx`, the SDK's XML format — `dotnet build`
  handles it, older Visual Studio versions may not)
- MSVC 14.44 + Windows SDK 10.0.26100, for the native DLL
- CMake
- git, for the submodules

## Layout

```
managed\BannerlordVR\           C# net472, x64 - game logic, camera, UI, input
  Interop\NativeBridge.cs         P/Invoke surface; mirrors native\...\bvr_api.h
  VR\VrMath.cs                    the ONE place OpenXR space becomes Bannerlord space
  VR\VrCameraDriver.cs            head pose -> eye cameras; owns recentring
  VR\VrSystem.cs                  lifecycle; owns "is VR actually running"
  VR\VrRenderMode.cs              the stereo modes, and why the dead ones died
  VR\VrMenu.cs                    the in-VR settings panel
  VR\VrLog.cs                     file log -> Documents\...\Logs\BannerlordVR.log
  Patches\                        Harmony patches on MissionScreen and friends
native\BannerlordVR.Native\     C++17, MSVC x64 - OpenXR session, D3D11 interop,
                                  the Present hook, the reprojection warp, NGX/DLSS
module\                         SubModule.xml, deployed verbatim
tests\BannerlordVR.MathTests\   console app, exit 0/1 - coordinate maths
tools\deploy.ps1                stages artifacts, purges anything else from the module
tools\package-release.ps1       builds the release zip
config\BannerlordVR.cfg         the research journal; NOT what ships (see below)
build\                          staging output (gitignored)
```

## Build

```powershell
# managed only
dotnet build -c Release

# native (needs the toolchain above + submodules)
git submodule update --init --recursive
cmake -S native/BannerlordVR.Native -B build/native-cmake -A x64
cmake --build build/native-cmake --config Release

# stage into the game + purge strays
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1
```

`dotnet build` deploys automatically. Suppress with `-p:SkipDeploy=true`.
Different install path: `-p:BannerlordDir="D:\...\Mount & Blade II Bannerlord"`
(the same switch exists on `deploy.ps1` and `package-release.ps1`).

```powershell
# coordinate maths - run after touching VrMath or VrCameraDriver
dotnet build tests\BannerlordVR.MathTests
build\tests\BannerlordVR.MathTests.exe      # exit 0 = pass
```

## Two rules that are not negotiable

**The module bin folder contains only our own artifacts.** MSBuild's default
`CopyLocal=true` on the TaleWorlds references copies the entire game into the
module folder; Mono then binds against the duplicates and throws type-identity
errors that look like unrelated crashes. Every game reference carries
`<Private>false</Private>`, and `deploy.ps1` enforces an allow-list.

**A failure anywhere in the VR stack drops to flat mode, never to a crash.**
No headset, no runtime, no native DLL are all normal states. `VrSystem.IsActive`
gates every VR code path, and the camera patch shuts the system down rather than
throwing once per frame.

## Check the version string first

`BannerlordVR.Native.log` prints it on every launch:

- `0.2.0-openxr` — the real build.
- `0.2.0-stub` — OpenXR was compiled out. It loads fine and then reports
  `bvr_init failed: NoRuntime`, which is indistinguishable from a missing
  headset or a broken runtime, so it will send you debugging the wrong machine.
  Cause is a configure without `BVR_ENABLE_OPENXR`. CMake defaults it from
  whether the submodules are present and prints which build it chose, so the fix
  is to reconfigure — deleting `build/native-cmake` first, since the OFF value is
  sticky in `CMakeCache.txt`.

## Engine facts that cost time to learn

Measured against the shipped 1.4.8 assemblies. None of them are documented
anywhere by TaleWorlds, and each one was paid for with a crash that gave no
error, only a fault offset.

- **A camera looks along `-u`, not `f`.** Read from a live `CombatCamera` whose
  rotation was identity: `Direction=(0,0,-1)` against `u=(0,0,1)`. Building an
  eye frame the natural way (`f` = gaze) aims the camera along the player's *up*
  axis, so a level head renders the ground. `MatrixFrame.CreateLookAt` is **not**
  a reliable guide: it returns an origin of `eye + forward + up`, and implies
  `+u`.
- **`UpdateCamera(float)` is not per-frame.** `MissionScreen` reaches it through
  `CheckForUpdateCamera`, which calls it only when the engine decides the camera
  is stale. Hooking it leaves the VR camera frozen. `OnFrameTick(float)` is the
  unconditional per-frame hook.
- **`CustomCamera` is required, and it feeds back.** Assigning it is what makes
  the engine render from our camera; `SceneLayer.SetCamera` alone is overridden
  later in the frame. But the engine folds that camera into `CombatCamera`, so
  using `CombatCamera` as the body anchor adds our own head offset to itself
  every frame. The anchor is the player agent
  (`GetEyeGlobalPosition` / `LookDirection`), which the engine owns outright.
- **There is no main agent during deployment.** The anchor falls back to the
  engine camera there, with echo rejection.
- **A SceneView may be BUILT mid-frame but never ENABLED mid-frame.** Calling
  `SetEnable(true)` from `MissionScreen.OnFrameTick`, while rgl is walking its
  own view list, kills the renderer in about a second. Build and enable from a
  postfix on `MissionScreen.OnSceneRenderingStarted`.
- **An under-configured view crashes the engine.** With `SetRenderWithPostfx`
  and `SetSceneUsesShadows` off, rgl dereferences a null pointer at +0x50 on its
  own worker thread, with none of our code in the call path. Postfx and shadows
  ON, plus `SetPostfxFromConfig()`, is the configuration the engine actually
  runs.
- **`Texture.CreateDepthTarget` gives D16_UNORM with no stencil.** Forcing a
  scene render onto it via `SetAutoDepthTargetCreation(false)` crashes within
  seconds. Let the engine allocate its own depth for the eye views.
- **The engine's immediate context needs `ID3D11Multithread`.** We touch it from
  the Present hook while rgl renders on another thread. Without
  `SetMultithreadProtected(TRUE)` and a lock across the frame it survives a
  minute alone and one second once two scene renders exist.
- **rgl allocates render targets ~1 ms later, on another thread.** A texture
  capture window scoped to the `Texture.CreateRenderTarget` call sees nothing.
- **Eye targets are `B8G8R8A8_UNORM` (87).** `CopyResource` needs the same
  typeless family, so the OpenXR swapchain must be BGRA —
  `R8G8B8A8_UNORM_SRGB` is rejected outright. The runtime hands back
  `B8G8R8A8_TYPELESS` (90), which is compatible.
- **rgl camera reconfiguration is not safe at frame rate.** `SetViewVolume` and
  `SetFovHorizontal` are cached and applied only on change; `SetCamera` on a
  SceneView is the exception and must be re-pushed every frame, because a view
  snapshots its camera rather than reading it per render.
- **The SandBox module's folder and Id disagree.** The folder is
  `Modules\SandBox`, but its `SubModule.xml` declares `<Id value="Sandbox"/>`
  (lowercase `b`). `DependedModule` matches on Id, so spelling it `SandBox`
  makes the dependency unresolvable and the launcher silently greys out our
  checkbox with no error anywhere. Same trap for `<Assembly value="..."/>` — the
  attribute is `value`, not `name`, so `name` parses without complaint and loads
  nothing.

## Diagnosing a crash

`BannerlordVR.Native.log` carries a vectored exception handler. Any access
violation inside `TaleWorlds.Native.dll` is logged with a full stack as
`module+RVA` before the engine's own handler runs. There are no PDBs for the
engine, so frames are offsets — but the module chain alone says whether the
fault is rgl's own worker thread, our Present hook, or something else, which is
what a bare fault offset can never tell you.

## Where the knowledge lives

Not in `docs/`. The two places that are kept current are:

- **`config/BannerlordVR.cfg`** — a research journal as much as a config file.
  Every experiment is recorded next to the key it set, with its result. It is
  *not* what ships to users; `tools/package-release.ps1` flattens the live
  config into a clean one for the zip.
- **The commit messages.** They are written to be read later and carry the
  reasoning that never made it into a comment.

## Cutting a release

See [RELEASING.md](RELEASING.md).
