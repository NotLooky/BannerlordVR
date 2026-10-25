<#
.SYNOPSIS
    Builds and runs tests\ui_quad_math_test.cpp: the wrist HUD's placement and
    fade gate (TEST 377) and the laser's ray-to-quad hit (TEST 378).

.EXAMPLE
    powershell -File tools\run-ui-quad-math-test.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

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

$exe = Join-Path $outDir 'ui_quad_math_test.exe'

# Forward slashes: a quoted path ending in a backslash escapes its own quote in cmd.exe.
function Fwd([string]$p) { $p.Replace('\', '/') }

$root = Fwd $repoRoot
$compile = @(
    'cl /std:c++17 /EHsc /nologo /W3'
    "/Fo:`"$(Fwd $outDir)/`" /Fe:`"$(Fwd $exe)`""
    "/I `"$root/native/BannerlordVR.Native/src`""
    "/I `"$root/native/thirdparty/OpenXR-SDK/include`""
    "`"$root/tests/ui_quad_math_test.cpp`""
) -join ' '

Write-Host 'Compiling ui_quad_math_test...' -ForegroundColor Cyan
cmd.exe /c "`"$vcvars`" >nul && $compile"
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed (exit $LASTEXITCODE)."
}

Write-Host ''
& $exe
exit $LASTEXITCODE
