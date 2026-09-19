<#
.SYNOPSIS
    Builds the release zip that users download.

.DESCRIPTION
    Packages the module FROM THE GAME FOLDER, not from build output. That is
    deliberate: deploy.ps1 has already curated that folder down to our own
    artifacts and purged the strays, and it is the exact set of files the build
    was played and judged on. Packaging anything else ships something nobody
    tested.

    No config file is shipped. The mod's built-in defaults are the tested
    configuration, so an absent BannerlordVR.cfg is the correct starting state -
    and shipping one would mean shipping whatever probes, experiments and hand
    calibration happened to be in it on the day.

.EXAMPLE
    pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1
    pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$BannerlordDir = 'E:\steam\steamapps\common\Mount & Blade II Bannerlord',

    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist' }

$moduleDir = Join-Path $BannerlordDir 'Modules\BannerlordVR'
$moduleBin = Join-Path $moduleDir 'bin\Win64_Shipping_Client'

# Exactly what deploy.ps1 allows into the module bin, and nothing else.
$required = @(
    'BannerlordVR.dll',
    'BannerlordVR.Native.dll',
    'openxr_loader.dll'
)

# --- 1. verify the deployed module ----------------------------------------
if (-not (Test-Path $moduleBin)) {
    throw "No deployed module at '$moduleBin'. Build and run tools\deploy.ps1 first."
}

$missing = $required | Where-Object { -not (Test-Path (Join-Path $moduleBin $_)) }
if ($missing) {
    throw "Deployed module is incomplete - missing: $($missing -join ', ')"
}

# A stale native DLL beside a fresh managed one is a real failure mode, and it
# surfaces as behaviour from two different builds at once. Just say the dates.
Write-Host "Packaging from: $moduleDir"
foreach ($f in $required) {
    $item = Get-Item (Join-Path $moduleBin $f)
    Write-Host ("  {0,-28} {1}" -f $item.Name, $item.LastWriteTime)
}

# Warn if the SubModule.xml version disagrees with what is being cut, because
# the launcher is where a user reads their version number from.
$manifest = Join-Path $moduleDir 'SubModule.xml'
$declared = ([xml](Get-Content $manifest)).Module.Version.value
if ($declared -notlike "*$Version*") {
    Write-Warning "SubModule.xml declares '$declared' but this release is '$Version'."
}

# --- 2. stage and zip -----------------------------------------------------
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "bvr-release-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$stageModule = Join-Path $stage 'Modules\BannerlordVR'
New-Item -ItemType Directory -Force -Path $stageModule | Out-Null

if ($PSCmdlet.ShouldProcess($stage, 'stage release')) {
    Copy-Item $moduleDir\* -Destination $stageModule -Recurse -Force

    # PDBs are useful to us and noise to everyone else.
    Get-ChildItem $stageModule -Recurse -Filter *.pdb | Remove-Item -Force

    foreach ($doc in 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md') {
        Copy-Item (Join-Path $repoRoot $doc) -Destination $stage -Force
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "BannerlordVR-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

if ($PSCmdlet.ShouldProcess($zip, 'write zip')) {
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item $stage -Recurse -Force

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Wrote $zip ($size MB)" -ForegroundColor Green
    Write-Host "Check the zip once by hand before uploading it: extracting it over a"
    Write-Host "clean game folder must produce Modules\BannerlordVR\bin\Win64_Shipping_Client."
}
