<# Installs or rolls back ONLY the two performance-test DLLs. Configs, saves,
module metadata and game assemblies are never overwritten. Close the game first. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Install','Rollback','Status')][string]$Action,
    [Parameter(Mandatory=$true)][string]$Checkpoint,
    [string]$Candidate,
    # Also allows recovery tests against a disposable module copy.
    [string]$ModulePath
)
$ErrorActionPreference = 'Stop'
$Checkpoint = (Resolve-Path -LiteralPath $Checkpoint).Path
$manifest = Get-Content -LiteralPath (Join-Path $Checkpoint 'checkpoint.json') -Raw | ConvertFrom-Json
if (-not $ModulePath) { $ModulePath = $manifest.module_path }
$ModulePath = (Resolve-Path -LiteralPath $ModulePath).Path
$relative = @('bin/Win64_Shipping_Client/BannerlordVR.dll', 'bin/Win64_Shipping_Client/BannerlordVR.Native.dll')
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Original-Hash([string]$name) {
    $entry = @($manifest.files | Where-Object { $_.path -eq $name })
    if ($entry.Count -ne 1) { throw "Missing or duplicate checkpoint entry: $name" }
    return $entry[0].sha256
}
if ($Action -eq 'Status') {
    foreach ($name in $relative) {
        $value = Hash (Join-Path $ModulePath $name)
        [pscustomobject]@{ file=$name; original=($value -eq (Original-Hash $name)); sha256=$value }
    }
    return
}
if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Bannerlord|TaleWorlds\.MountAndBlade' }) {
    throw 'Close Bannerlord and its launcher before changing DLLs. Nothing has been installed.'
}
# Validate EVERY recovery DLL before overwriting anything.
foreach ($name in $relative) {
    if ((Hash (Join-Path (Join-Path $Checkpoint 'module') $name)) -ne (Original-Hash $name)) {
        throw "Recovery hash mismatch: $name. No changes made."
    }
}
$source = Join-Path $Checkpoint 'module'
if ($Action -eq 'Install') {
    if (-not $Candidate) { throw '-Candidate is required for Install.' }
    $source = (Resolve-Path -LiteralPath $Candidate).Path
    $candidateManifest = Get-Content -LiteralPath (Join-Path $source 'candidate.json') -Raw | ConvertFrom-Json
    foreach ($name in $relative) {
        $entry = @($candidateManifest.files | Where-Object { $_.path -eq $name })
        if ($entry.Count -ne 1 -or (Hash (Join-Path $source $name)) -ne $entry[0].sha256) {
            throw "Candidate hash mismatch: $name. No changes made."
        }
        $installed = Hash (Join-Path $ModulePath $name)
        if ($installed -ne (Original-Hash $name) -and $installed -ne $entry[0].sha256) {
            throw "Installed $name changed since the checkpoint. Create a new checkpoint before installing over it."
        }
    }
}
# Fail early if either DLL is locked. Never terminate the game.
foreach ($name in $relative) {
    $stream = [IO.File]::Open((Join-Path $ModulePath $name), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $stream.Dispose()
}
$snapshot = Join-Path $Checkpoint ('before-' + $Action.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $snapshot | Out-Null
$before = @{}
foreach ($name in $relative) {
    $target = Join-Path $ModulePath $name
    $leaf = Split-Path -Leaf $name
    $before[$name] = Hash $target
    Copy-Item -LiteralPath $target -Destination (Join-Path $snapshot $leaf)
    if ((Hash (Join-Path $snapshot $leaf)) -ne $before[$name]) { throw 'Pre-install backup verification failed.' }
}
if ($ModulePath -eq $manifest.module_path) {
    $logDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord\Logs'
    foreach ($name in @('BannerlordVR.log','BannerlordVR.Native.log')) {
        $log = Join-Path $logDir $name
        if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination $snapshot }
    }
}
try {
    foreach ($name in $relative) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $ModulePath $name) -Force
        if ((Hash (Join-Path $ModulePath $name)) -ne (Hash (Join-Path $source $name))) {
            throw "Installed hash mismatch: $name"
        }
    }
} catch {
    $installError = $_
    foreach ($name in $relative) {
        Copy-Item -LiteralPath (Join-Path $snapshot (Split-Path -Leaf $name)) -Destination (Join-Path $ModulePath $name) -Force
        if ((Hash (Join-Path $ModulePath $name)) -ne $before[$name]) {
            throw "Recovery failed for $name. Restore manually from $snapshot. Original error: $installError"
        }
    }
    throw "Update failed; both previous DLLs restored. $installError"
}
[pscustomobject]@{ action=$Action; time=(Get-Date -Format o); module=$ModulePath; previous_dlls=$snapshot } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $snapshot 'operation.json') -Encoding utf8
Write-Host "$Action verified: both DLLs in $ModulePath. Configs and saves unchanged."
