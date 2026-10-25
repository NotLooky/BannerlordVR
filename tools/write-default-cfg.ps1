<#
.SYNOPSIS
    Writes BannerlordVR.default.cfg - the effective value of every key in the
    live config - into the module folder.

.DESCRIPTION
    Both loaders (bvr_config.cpp config_load, VrConfig.Load) read this file
    first and the player's Documents\...\BannerlordVR.cfg on top. The built-in
    defaults drift from what is played (every proven setting lands in the live
    config and nowhere else), so without this file a deleted config renders
    with stale code defaults. TEST 540: the dev build writes it too, not only
    the release.

    Last assignment wins, as in both readers. Probe/debug keys are dropped:
    diagnostics cost frames and fill the logs.

    The defaults ACCUMULATE in module\BannerlordVR.default.cfg (tracked): the
    live config is laid over it and the result written back, so a fresh or
    deleted live config never drops a saved key - weapon and shield poses
    above all. A key leaves the defaults only by editing that file.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutFile,

    [string]$ConfigPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
        'Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg'),

    [string]$BaseFile = '',

    [string]$Header = 'BannerlordVR shipped defaults'
)

$ErrorActionPreference = 'Stop'

# Here, not in param(): $PSScriptRoot is empty in param defaults under
# Windows PowerShell 5.1 -File, which is how the build runs this.
if (-not $BaseFile) { $BaseFile = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\module\BannerlordVR.default.cfg' }

$diagnostic = '_(probe|debug|dump|log)(_|$)|^combat_test_|^stereo_capture_'
$effective = [ordered]@{}
$sources = @($BaseFile, $ConfigPath) | Where-Object { Test-Path $_ }
if (-not $sources) { throw "Neither '$BaseFile' nor '$ConfigPath' exists." }
# TEST 553: equipment poses (skin_*) the mod saved straight into the deployed
# file survive this rewrite, even with the live config gone; the live one wins.
$text = @()
if (Test-Path $BaseFile)   { $text += Get-Content $BaseFile }
if (Test-Path $OutFile)    { $text += Get-Content $OutFile | Where-Object { $_.TrimStart() -match '^skin_' } }
if (Test-Path $ConfigPath) { $text += Get-Content $ConfigPath }
foreach ($line in $text) {
    $t = $line.Trim()
    if ($t -eq '' -or $t[0] -eq '#' -or $t[0] -eq ';') { continue }
    $eq = $t.IndexOf('=')
    if ($eq -le 0) { continue }
    $key = $t.Substring(0, $eq).Trim().ToLowerInvariant()
    $value = $t.Substring($eq + 1).Trim()
    # Cheeky Foveated DLSS is a separate GPLv3 download the player installs
    # themselves; its switch must never ship on.
    if ($key -match '^cheeky_') { continue }
    # One-shot markers (chat_off_done) record that THIS player's game was set
    # once; shipped, they would skip the step on every new install.
    if ($key -match '_done$') { continue }
    $effective.Remove($key)
    # A diagnostic switched OFF stays: some default to on in code (camera_probe).
    if ($key -match $diagnostic -and $value -notmatch '^(0|false|off|no)$') { continue }
    $effective[$key] = $value
}

$out = @("# $Header. Do not edit: a build or an update replaces",
         "# this file. Put changes in Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg,",
         "# which is read after this one and wins key by key.", '')
$out += $effective.Keys | ForEach-Object { "$_ = $($effective[$_])" }
Set-Content -Path $OutFile -Value $out -Encoding ascii
$base = @("# BannerlordVR shipped defaults, accumulated by tools\write-default-cfg.ps1.", '')
Set-Content -Path $BaseFile -Value ($base + $out[4..($out.Count - 1)]) -Encoding ascii
Write-Host "  BannerlordVR.default.cfg      $($effective.Count) keys ($($sources -join ' + '))"
