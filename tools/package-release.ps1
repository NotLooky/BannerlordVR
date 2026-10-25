<#
.SYNOPSIS
    Builds the release zip that users download.

.DESCRIPTION
    Packages the module FROM THE GAME FOLDER, not from build output. That is
    deliberate: deploy.ps1 has already curated that folder down to our own
    artifacts and purged the strays, and it is the exact set of files the build
    was played and judged on. Packaging anything else ships something nobody
    tested.

    The live config ships as Modules\BannerlordVR\BannerlordVR.default.cfg.
    The built-in defaults drifted from what is actually played (every proven
    setting lands in the live config and nowhere else), so the mod did not work
    without it. Both loaders read the shipped file first and the player's
    Documents\...\BannerlordVR.cfg on top. Only the effective value of each key
    goes in - no journal comments - and probe/debug keys are dropped.

.EXAMPLE
    pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1
    pwsh tools\package-release.ps1 -Version 0.1.0-alpha.1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$BannerlordDir = 'E:\steam\steamapps\common\Mount & Blade II Bannerlord',

    [string]$OutDir,

    [string]$ConfigPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
        'Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg')
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid release version '$Version'."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist' }

$moduleDir = Join-Path $BannerlordDir 'Modules\BannerlordVR'
$moduleBin = Join-Path $moduleDir 'bin\Win64_Shipping_Client'

# Everything the module needs at runtime. openxr_loader is NOT here: the SDK
# loader is linked statically into BannerlordVR.Native.dll, so no
# openxr_loader.dll is built and none is shipped.
$required = @(
    'BannerlordVR.dll',
    'BannerlordVR.Native.dll',
    'nvngx_dlss.dll'
)

# --- 1. verify the deployed module ----------------------------------------
if (-not (Test-Path $moduleBin)) {
    throw "No deployed module at '$moduleBin'. Build and run tools\deploy.ps1 first."
}

$missing = $required | Where-Object { -not (Test-Path (Join-Path $moduleBin $_)) }
if ($missing) {
    throw "Deployed module is incomplete - missing: $($missing -join ', ')"
}

$dlss = Join-Path $moduleBin 'nvngx_dlss.dll'
if ((Get-FileHash -LiteralPath $dlss -Algorithm SHA256).Hash -ne '3975567B8943C53ACCE397F2B72380092F84F162D00B0D2C7D08A1025C563983') {
    throw 'DLSS runtime does not match the NVIDIA-signed 310.9.1.0 release binary.'
}
if ((Get-AuthenticodeSignature -LiteralPath $dlss).Status -ne 'Valid') { throw 'DLSS signature is invalid.' }

# A stale native DLL beside a fresh managed one is a real failure mode, and it
# surfaces as behaviour from two different builds at once. Just say the dates.
Write-Host "Packaging from: $moduleDir"
foreach ($f in $required) {
    $item = Get-Item (Join-Path $moduleBin $f)
    Write-Host ("  {0,-28} {1}" -f $item.Name, $item.LastWriteTime)
}

# Warn if the SubModule.xml version disagrees with what is being cut, because
# the launcher is where a user reads their version number from.
#
# Only the NUMERIC core is compared. SubModule.xml cannot carry a prerelease
# suffix: the launcher int.Parses each dot-separated field, and 'v0.1.0-alpha.1'
# makes it refuse to load the module with "Input string was not in a correct
# format". So 0.1.0-alpha.1 here is expected to meet v0.1.0 there.
$manifest = Join-Path $moduleDir 'SubModule.xml'
$declared = ([xml](Get-Content $manifest)).Module.Version.value
$core = ($Version -split '-')[0]
if ($declared -cne "v$core") {
    throw "SubModule.xml declares '$declared' but this release requires 'v$core'."
}
if ($declared -match '[A-Za-z]' -and $declared -notmatch '^[A-Za-z]?[\d.]+$') {
    throw "SubModule.xml declares '$declared'. The launcher parses that with " +
          "ApplicationVersion and will refuse to load the module. Numbers only, " +
          "after at most one letter: v0.1.0"
}

# --- 2. stage and zip -----------------------------------------------------
$stage = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) "bvr-release-$Version-$([guid]::NewGuid().ToString('N'))"))
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $stage.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release staging folder is outside the temporary directory.'
}

$stageModule = Join-Path $stage 'Modules\BannerlordVR'
New-Item -ItemType Directory -Force -Path $stageModule | Out-Null

if ($PSCmdlet.ShouldProcess($stage, 'stage release')) {
    # Curate the archive rather than inheriting stray game DLLs or dev files.
    $stageBin = Join-Path $stageModule 'bin\Win64_Shipping_Client'
    New-Item -ItemType Directory -Force -Path $stageBin | Out-Null
    Copy-Item -LiteralPath $manifest -Destination $stageModule
    Copy-Item -LiteralPath (Join-Path $moduleDir 'NVIDIA-DLSS-LICENSE.txt') -Destination $stageModule
    foreach ($name in $required) {
        Copy-Item -LiteralPath (Join-Path $moduleBin $name) -Destination $stageBin
    }
    $deployedDefaults = Join-Path $moduleDir 'BannerlordVR.default.cfg'
    if (-not (Test-Path -LiteralPath $deployedDefaults)) { throw 'Deployed defaults are missing.' }
    Copy-Item -LiteralPath $deployedDefaults -Destination $stageModule

    foreach ($doc in 'README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md') {
        Copy-Item (Join-Path $repoRoot $doc) -Destination $stage -Force
    }
    $stageDocs = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $stageDocs | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\BUILDING.md') -Destination $stageDocs
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\RELEASING.md') -Destination $stageDocs
    $notes = Join-Path $OutDir "RELEASE-NOTES-v$Version.md"
    if (Test-Path -LiteralPath $notes) { Copy-Item -LiteralPath $notes -Destination $stage }

    # The tracked module\BannerlordVR.default.cfg is the base; the writer throws if
    # neither it nor a live config exists.
    & (Join-Path $PSScriptRoot 'write-default-cfg.ps1') `
        -OutFile (Join-Path $stageModule 'BannerlordVR.default.cfg') `
        -BaseFile (Join-Path $stageModule 'BannerlordVR.default.cfg') `
        -ConfigPath $ConfigPath -Header "BannerlordVR $Version shipped defaults"

    $buildInfo = [ordered]@{
        version = $Version
        source_commit = (& git -C $repoRoot rev-parse HEAD)
        source_branch = (& git -C $repoRoot branch --show-current)
        source_changes = @(& git -C $repoRoot status --porcelain)
        submodules = @(& git -C $repoRoot submodule status)
        files = @(Get-ChildItem -LiteralPath $stageModule -Recurse -File | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    }
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'BUILD-INFO.json') -Encoding UTF8
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "BannerlordVR-$Version.zip"
if (Test-Path -LiteralPath $zip) { throw "Release archive already exists: $zip" }

if ($PSCmdlet.ShouldProcess($zip, 'write zip')) {
    # Not Compress-Archive: on Windows PowerShell 5.1 it writes backslash path
    # separators into the zip. The format requires forward slashes, and some
    # extractors take it literally and produce one file per path instead of a
    # tree - which looks to a user exactly like a corrupt download.
    # ZipFile::CreateFromDirectory is no better here - on .NET Framework it
    # uses the platform separator too - so the entries are named by hand.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($f in Get-ChildItem $stage -Recurse -File) {
            $entry = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $f.FullName, $entry,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
    Remove-Item -LiteralPath $stage -Recurse -Force

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Wrote $zip ($size MB)" -ForegroundColor Green
    Write-Host "Check the zip once by hand before uploading it: extracting it over a"
    Write-Host "clean game folder must produce Modules\BannerlordVR\bin\Win64_Shipping_Client."
}
