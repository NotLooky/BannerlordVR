<#
.SYNOPSIS
    Builds and runs the view-projection patcher's math self-test.

.DESCRIPTION
    The VP patcher rests on one claim: that the replacement matrix can be built
    without knowing rgl's depth range, handedness, or reversed-Z. If that claim
    is wrong, the symptom is a black or scrambled headset image, and each attempt
    to find out costs a game launch.

    So it is checked here instead. tests\vp_math_test.cpp includes vp_patch.cpp
    directly and exercises the shipping functions against projections built with
    each of those conventions deliberately different. It has already earned its
    keep once, catching a transposed index in mat_rigid_inverse that placed the
    camera 1214 metres from where it belonged.

    Run this after touching anything in vp_patch.cpp.

.EXAMPLE
    powershell -File tools\run-vp-math-test.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$minhookLib = Join-Path $repoRoot 'build\native-cmake\minhook\Release\minhook.x64.lib'
if (-not (Test-Path $minhookLib)) {
    throw "MinHook is not built yet. Run: cmake --build build/native-cmake --config Release"
}

# The compiler is not on PATH; vswhere finds whichever VS is installed.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found; a Visual Studio C++ toolchain is required."
}

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw "No Visual Studio installation with the C++ toolchain was found."
}

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
$outDir = Join-Path $repoRoot 'build\tests'
New-Item -ItemType Directory -Force $outDir | Out-Null

$exe = Join-Path $outDir 'vp_math_test.exe'

# Forward slashes throughout. cl accepts them, and a quoted path ending in a
# BACKslash escapes its own closing quote in cmd.exe - which shows up as
# "D8003: missing source filename", with nothing pointing at the real cause.
function Fwd([string]$p) { $p.Replace('\', '/') }

$root = Fwd $repoRoot
$compile = @(
    'cl /std:c++17 /EHsc /nologo /W3'
    "/Fo:`"$(Fwd $outDir)/`" /Fe:`"$(Fwd $exe)`""
    "/I `"$root/native/BannerlordVR.Native/src`""
    "/I `"$root/native/BannerlordVR.Native/include`""
    "/I `"$root/native/thirdparty/minhook/include`""
    "`"$root/tests/vp_math_test.cpp`""
    "`"$root/native/BannerlordVR.Native/src/bvr_log.cpp`""
    "`"$root/native/BannerlordVR.Native/src/bvr_config.cpp`""
    "`"$(Fwd $minhookLib)`" user32.lib shell32.lib"
) -join ' '

Write-Host 'Compiling vp_math_test...' -ForegroundColor Cyan
cmd.exe /c "`"$vcvars`" >nul && $compile"
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed (exit $LASTEXITCODE)."
}

Write-Host ''
& $exe
exit $LASTEXITCODE
