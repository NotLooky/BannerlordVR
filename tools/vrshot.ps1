<#
    vrshot.ps1 - capture a window (the SteamVR VR View, or the game) to PNG.

    Exists so the headset view can be LOOKED AT rather than described. A still
    settles anything spatial - doubling, smear, disocclusion holes, a stereo
    pair that does not match, a shadow that has crawled. It settles nothing
    temporal: resistance and latency are not in a picture, they are in the log.

    For a whole battle, unattended, use vrwatch.ps1 instead.

    EXAMPLES
      .\vrshot.ps1                          the VR View window, one frame
      .\vrshot.ps1 -Match Bannerlord        the game window instead
      .\vrshot.ps1 -Count 8 -IntervalMs 120 a burst, for anything that moves
      .\vrshot.ps1 -List                    what windows are capturable
#>
[CmdletBinding()]
param(
    [string] $Match      = 'VR View',
    [int]    $Count      = 1,
    [int]    $IntervalMs = 150,
    [int]    $MaxWidth   = 1600,
    [string] $OutDir     = "$env:TEMP\vrshot",
    [switch] $List,
    [switch] $Raise
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VrShot.psm1') -Force

if ($List) {
    Get-VrWindows | ForEach-Object { '{0,-60} {1}x{2}' -f $_.Title, $_.Width, $_.Height }
    return
}

$win = Find-VrWindow -Match $Match
if (-not $win) {
    Write-Error "No visible window matching '$Match'. Run with -List to see what there is."
    return
}

if ([VrShotNative]::IsIconic($win.Handle)) {
    [void][VrShotNative]::ShowWindow($win.Handle, 9)
    Start-Sleep -Milliseconds 300
}
if ($Raise) {
    [void][VrShotNative]::SetForegroundWindow($win.Handle)
    Start-Sleep -Milliseconds 250
}

if (-not (Test-Path $OutDir)) { [void](New-Item -ItemType Directory -Path $OutDir -Force) }

$stamp  = Get-Date -Format 'HHmmss'
$method = $null

for ($n = 1; $n -le $Count; $n++) {
    $file = if ($Count -eq 1) { Join-Path $OutDir "vr_$stamp.png" }
            else { Join-Path $OutDir ("vr_{0}_{1:d2}.png" -f $stamp, $n) }

    $r = Save-VrWindowCapture -Handle $win.Handle -Path $file -MaxWidth $MaxWidth
    if (-not $r) { Write-Error "Window '$($win.Title)' has no client area to capture." ; return }
    if (-not $method) { $method = $r.Method }
    Write-Output $r.Path

    if ($n -lt $Count) { Start-Sleep -Milliseconds $IntervalMs }
}

Write-Output "-- '$($win.Title)' $($win.Width)x$($win.Height) via $method"
