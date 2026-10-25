[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Checkpoint,
    [Parameter(Mandatory=$true)][string]$Candidate
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Checkpoint = (Resolve-Path -LiteralPath $Checkpoint).Path
$Candidate = (Resolve-Path -LiteralPath $Candidate).Path
$test = Join-Path $repo ('build\perf-recovery-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$module = Join-Path $test 'module'
New-Item -ItemType Directory -Path $module -Force | Out-Null
Copy-Item -Path (Join-Path $Checkpoint 'module\*') -Destination $module -Recurse
$before = @{}
Get-ChildItem -LiteralPath $module -Recurse -File | ForEach-Object { $before[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
function Run([string]$action, [string]$from = $Candidate) {
    & (Join-Path $PSScriptRoot 'perf-build.ps1') -Action $action -Checkpoint $Checkpoint -Candidate $from -ModulePath $module
}
Run 'Install'
Run 'Install' # idempotent reinstall
Run 'Rollback'
foreach ($path in $before.Keys) {
    if ((Get-FileHash -LiteralPath $path).Hash -ne $before[$path]) { throw "Recovery changed $path" }
}
# A corrupt candidate must be rejected before either module DLL changes.
$bad = Join-Path $test 'bad-candidate'
New-Item -ItemType Directory -Path $bad | Out-Null
Copy-Item -Path (Join-Path $Candidate '*') -Destination $bad -Recurse
[IO.File]::AppendAllText((Join-Path $bad 'bin\Win64_Shipping_Client\BannerlordVR.Native.dll'), 'corruption-for-test')
$rejected = $false
try { Run 'Install' $bad } catch { $rejected = $_.Exception.Message -like 'Candidate hash mismatch*' }
if (-not $rejected) { throw 'Corrupt candidate was not rejected as expected.' }
foreach ($path in $before.Keys) {
    if ((Get-FileHash -LiteralPath $path).Hash -ne $before[$path]) { throw "Rejected install changed $path" }
}
Write-Host 'PASS: install, reinstall, exact rollback and corrupt-candidate rejection; module config and metadata preserved.'
