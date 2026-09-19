# Cutting a release

The alpha is `v0.1.0-alpha.1`. Every alpha after it bumps the last number.

There is no CI: a build machine has no copy of Bannerlord, so the release is cut
from the machine that can actually run it. That makes step 6 the important one —
nothing else verifies the zip.

## 1. Version numbers

Three places have to agree, because each one is where somebody reads the version
from:

| File | Field | Read by |
|---|---|---|
| `module/SubModule.xml` | `<Version value="v0.1.0-alpha.1" />` | the launcher's Mods tab |
| `managed/BannerlordVR/BannerlordVR.csproj` | `AssemblyVersion`, `FileVersion` | the managed log |
| `CHANGELOG.md` | a new section | everybody |

`package-release.ps1` warns if `SubModule.xml` disagrees with the version you
pass it.

## 2. Build clean

```powershell
git status                       # nothing uncommitted that belongs in the release
cmake --build build/native-cmake --config Release
dotnet build -c Release
dotnet build tests\BannerlordVR.MathTests
build\tests\BannerlordVR.MathTests.exe      # exit 0
powershell -ExecutionPolicy Bypass -File tools\deploy.ps1
```

`deploy.ps1` prints `Module bin is clean.` If it instead lists files it purged,
find out how they got there before shipping.

## 3. Play it

Not a smoke test — an actual session, in the headset, in the modes you are about
to tell people to use. At minimum:

- a custom battle in AFR, on foot and mounted
- the `End` panel: change world scale and resolution, confirm they save
- the main menu and the campaign map (the flat screen path)
- quit to the main menu and back into a mission, twice

Note down what is broken. It goes in the release notes rather than into an
issue tracker after the fact — an alpha that lists its own faults gets useful
reports back, and one that does not gets the same three duplicates.

### Then play it once with no config file

**Rename your own `BannerlordVR.cfg` out of the way and play a battle.** Users
have no config file, so the built-in defaults are the only thing they get, and
this is the only check that they still are what you play on.

The defaults drift. Every setting proven in a test run gets written into the
live config and nowhere else, so the source keeps whatever it was born with
while the config quietly becomes the real specification. `late_latch` spent its
whole life defaulting to off. Anything you find here is a source change, not a
config to ship:

```powershell
# what your config overrides, and what the code would have used instead
git grep -hoE '(config_bool|config_float|config_int|config_string|VrConfig\.(Bool|Float|Int|String))\("[a-z_]+", *[^)]+' -- managed native
```

## 4. Changelog

Write it from the commit messages. They carry the reasoning; the changelog
carries what a player notices.

## 5. Package

```powershell
pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1
```

This packages the module **from the game folder** — what you just played. No
config is shipped: the built-in defaults are the tested configuration. It writes
`dist\BannerlordVR-<version>.zip`.

## 6. Verify the zip on a clean module folder

The one step nothing else covers:

```powershell
Rename-Item "<game>\Modules\BannerlordVR" BannerlordVR.bak
# extract the zip over the game folder
# launch, load a mission, confirm the headset lights up
```

Then check the version string in `BannerlordVR.Native.log` says
`0.2.0-openxr` and not `0.2.0-stub`. A stub build installs and runs and reports
`NoRuntime`, which every user will report to you as "it does not work".

## 7. Tag and push

```powershell
git tag -a v0.1.0-alpha.1 -m "Alpha 1"
git push origin main --tags
```

## 8. Publish the release

On GitHub: **Releases → Draft a new release**, pick the tag, and

- tick **Set as a pre-release** — this is an alpha, and the tick is what stops
  it being served as "latest stable" to tooling and to people skimming
- attach `dist\BannerlordVR-<version>.zip`
- paste the changelog section, then the install steps, then the known issues
- state the supported game version (**1.4.8**) in the first line, because the
  single most common bad report is a different game version

## 9. After publishing

Download your own zip from the release page and install it once. It costs two
minutes and it is the only check that the file you uploaded is the file you
built.
