<#
.SYNOPSIS
    Builds the release zip that users download.

.DESCRIPTION
    Packages the module FROM THE GAME FOLDER, not from build output. That is
    deliberate: deploy.ps1 has already curated that folder down to our own
    artifacts and purged the strays, and it is the exact set of files the build
    was played and judged on. Packaging anything else ships something nobody
    tested.

    The config is flattened rather than copied. config\BannerlordVR.cfg and the
    live one in Documents are both research journals - hundreds of kilobytes of
    recorded experiments, with the same key set a dozen times and the last one
    winning. Users get the resolved values, one line each, with a short header.

.EXAMPLE
    pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1
    pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$BannerlordDir = 'E:\steam\steamapps\common\Mount & Blade II Bannerlord',

    # The config the release is built from. Defaults to the live one, because
    # that is the file the game actually read during testing.
    [string]$ConfigSource = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg'),

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

# --- 2. flatten the config ------------------------------------------------
if (-not (Test-Path $ConfigSource)) {
    throw "Config not found at '$ConfigSource'."
}

$order = New-Object System.Collections.Generic.List[string]
$values = @{}
$duplicates = 0

foreach ($line in Get-Content $ConfigSource) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';')) { continue }

    $eq = $trimmed.IndexOf('=')
    if ($eq -lt 1) { continue }

    $key = $trimmed.Substring(0, $eq).Trim()
    $value = $trimmed.Substring($eq + 1)

    # Strip a trailing comment, which the journal uses heavily.
    foreach ($marker in '#', ';') {
        $at = $value.IndexOf($marker)
        if ($at -ge 0) { $value = $value.Substring(0, $at) }
    }
    $value = $value.Trim()

    if ($values.ContainsKey($key)) { $duplicates++ } else { $order.Add($key) }
    $values[$key] = $value
}

Write-Host ""
Write-Host "Config: $($order.Count) settings ($duplicates duplicate assignments resolved, last wins)"

$header = @(
    '# ==========================================================================='
    "#  BannerlordVR $Version - tested configuration"
    '#'
    '#  Read by both the managed mod and the native DLL at startup. Edit, save,'
    '#  and restart the game.'
    '#'
    '#  Every line is key = value. A line starting with # or ; is a comment.'
    '#'
    '#  These are the values this release was tested on, with the development'
    '#  journal stripped out. Most settings also live on the in-VR panel (End),'
    '#  which writes its changes back here.'
    '#'
    '#  See docs/CONFIG.md for which keys are meant to be touched. A key that is'
    '#  not listed there is development machinery - changing one is how you get a'
    '#  build that behaves like nothing anyone can reproduce.'
    '# ==========================================================================='
    ''
)

$body = foreach ($key in $order) { "$key = $($values[$key])" }

# --- 3. stage and zip -----------------------------------------------------
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "bvr-release-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$stageModule = Join-Path $stage 'Modules\BannerlordVR'
New-Item -ItemType Directory -Force -Path $stageModule | Out-Null

if ($PSCmdlet.ShouldProcess($stage, 'stage release')) {
    Copy-Item $moduleDir\* -Destination $stageModule -Recurse -Force

    # PDBs are useful to us and noise to everyone else.
    Get-ChildItem $stageModule -Recurse -Filter *.pdb | Remove-Item -Force

    ($header + $body) | Set-Content (Join-Path $stage 'BannerlordVR.cfg') -Encoding utf8

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
