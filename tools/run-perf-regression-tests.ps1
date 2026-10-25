<# Builds the actual new sharpening source and the checkpoint source into the
same D3D11 fixture, then compares every output byte. Does not deploy to the game. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Checkpoint,
    [switch]$Hardware
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Checkpoint = (Resolve-Path -LiteralPath $Checkpoint).Path
$out = Join-Path $repo 'build\perf-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$baseline = Join-Path $out 'baseline'
New-Item -ItemType Directory -Force -Path $baseline | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Checkpoint 'source-before.zip'))
try {
    foreach ($name in @('vr_sharpen.cpp','vr_sharpen.h')) {
        $entry = $zip.Entries | Where-Object { $_.FullName.Replace('\','/').EndsWith('native/BannerlordVR.Native/src/' + $name) }
        if (@($entry).Count -ne 1) { throw "Checkpoint must contain exactly one original $name." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $baseline $name), $true)
    }
} finally { $zip.Dispose() }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ toolchain not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
function Fwd([string]$path) { $path.Replace('\','/') }
$native = Fwd (Join-Path $repo 'native/BannerlordVR.Native/src')
$wrapper = Join-Path $out 'baseline_sharpen_test.cpp'
@(
    '#define BVR_BASELINE_SOURCE'
    ('#define BVR_SHARPEN_SOURCE "' + (Fwd (Join-Path $baseline 'vr_sharpen.cpp')) + '"')
    ('#include "' + (Fwd (Join-Path $repo 'tests/perf_sharpen_test.cpp')) + '"')
) | Set-Content -LiteralPath $wrapper -Encoding ascii
function Build-Test([string]$name, [string]$source, [string]$libs = '') {
    $compile = 'cl /std:c++17 /EHsc /O2 /nologo /W3 /I "' + $native + '" /Fo:"' +
        (Fwd (Join-Path $out ($name + '.obj'))) + '" /Fe:"' + (Fwd (Join-Path $out ($name + '.exe'))) +
        '" "' + (Fwd $source) + '" ' + $libs
    & cmd.exe /c "`"$vcvars`" >nul && $compile"
    if ($LASTEXITCODE -ne 0) { throw "$name compile failed." }
}
Build-Test 'perf_srv_runs_test' (Join-Path $repo 'tests/perf_srv_runs_test.cpp')
Build-Test 'baseline_sharpen_test' $wrapper 'd3d11.lib d3dcompiler.lib dxgi.lib'
Build-Test 'perf_sharpen_test' (Join-Path $repo 'tests/perf_sharpen_test.cpp') 'd3d11.lib d3dcompiler.lib dxgi.lib'
& (Join-Path $out 'perf_srv_runs_test.exe')
if ($LASTEXITCODE -ne 0) { throw 'Shader declaration range comparison failed.' }
$device = if ($Hardware) { 'hardware' } else { 'warp' }
$hashes = @()
foreach ($variant in @('baseline','legacy','optimized')) {
    foreach ($direct in @(0,1)) {
        $pixels = Join-Path $out "$device-$variant-direct$direct.bin"
        $exe = if ($variant -eq 'baseline') { 'baseline_sharpen_test.exe' } else { 'perf_sharpen_test.exe' }
        $legacy = if ($variant -eq 'legacy') { 1 } else { 0 }
        & (Join-Path $out $exe) $pixels $legacy $direct ([int]$Hardware.IsPresent)
        if ($LASTEXITCODE -ne 0) { throw "$device $variant direct=$direct failed." }
        $hashes += [pscustomobject]@{ variant=$variant; direct=$direct; sha256=(Get-FileHash -LiteralPath $pixels -Algorithm SHA256).Hash }
    }
}
$hashes | Format-Table
if (@($hashes.sha256 | Select-Object -Unique).Count -ne 1) { throw 'Sharpening pixels differ from original or between direct and copy paths.' }
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out "$device-results.json") -Encoding ascii
Write-Host "PASS: $device original, legacy and optimized sharpening output is byte-identical (1,152 cases)."
