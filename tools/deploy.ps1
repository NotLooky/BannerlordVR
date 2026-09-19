<#
.SYNOPSIS
    Stages BannerlordVR into the game's Modules folder and removes anything that
    does not belong there.

.DESCRIPTION
    The module's bin folder must contain ONLY our own artifacts. Earlier builds
    ran with the default CopyLocal=true on the TaleWorlds references, which
    copied the entire game into the module folder. Mono can then bind against
    those duplicates and produce type-identity errors that surface as crashes
    with no obvious connection to the real cause.

    This script enforces the allow-list. It only ever touches
    Modules\BannerlordVR, which is entirely build output.

.EXAMPLE
    pwsh tools\deploy.ps1
    pwsh tools\deploy.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$BannerlordDir = 'E:\steam\steamapps\common\Mount & Blade II Bannerlord',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$moduleDir = Join-Path $BannerlordDir 'Modules\BannerlordVR'
$moduleBin = Join-Path $moduleDir 'bin\Win64_Shipping_Client'

# The complete set of files permitted in the module's bin folder.
$allowed = @(
    'BannerlordVR.dll',
    'BannerlordVR.pdb',
    'BannerlordVR.Native.dll',
    'BannerlordVR.Native.pdb',
    'openxr_loader.dll'
)

if (-not (Test-Path $BannerlordDir)) {
    throw "Bannerlord not found at '$BannerlordDir'."
}

New-Item -ItemType Directory -Force -Path $moduleBin | Out-Null

# --- 1. copy our artifacts ------------------------------------------------
$sources = @(
    (Join-Path $repoRoot 'build\managed'),
    (Join-Path $repoRoot 'build\native')
)

foreach ($src in $sources) {
    if (-not (Test-Path $src)) { continue }
    Get-ChildItem $src -File | Where-Object { $allowed -contains $_.Name } | ForEach-Object {
        Write-Host "  copy  $($_.Name)"
        if (-not $DryRun) {
            Copy-Item $_.FullName -Destination $moduleBin -Force
        }
    }
}

# --- 2. module manifest ---------------------------------------------------
Get-ChildItem (Join-Path $repoRoot 'module') -File -Recurse | ForEach-Object {
    Write-Host "  copy  $($_.Name)"
    if (-not $DryRun) {
        Copy-Item $_.FullName -Destination $moduleDir -Force
    }
}

# --- 3. purge anything not on the allow-list ------------------------------
$stray = Get-ChildItem $moduleBin -File | Where-Object { $allowed -notcontains $_.Name }

if ($stray) {
    Write-Host ""
    Write-Host "Removing $($stray.Count) file(s) that do not belong in the module bin:" -ForegroundColor Yellow
    foreach ($f in $stray) {
        Write-Host "  purge $($f.Name)"
        if (-not $DryRun) {
            Remove-Item $f.FullName -Force
        }
    }
} else {
    Write-Host ""
    Write-Host "Module bin is clean." -ForegroundColor Green
}

Write-Host ""
Write-Host "Deployed to: $moduleDir"
if ($DryRun) { Write-Host "(dry run - nothing was written)" -ForegroundColor Cyan }
