<#
    vrwatch.ps1 - watch the native log and capture the headset view for a battle.

    WHY THE LOG IS THE TRIGGER. The mod already knows when a mission starts and
    says so. Nothing else needs detecting, and nothing has to be pressed.

    WHICH LOG, AND WHY IT IS THE MANAGED ONE. The native log is the one with the
    numbers in it - "Mission active: the headset is on the stereo path" among
    them - but it was opened with _wfopen_s, which on Windows means EXCLUSIVE
    access: unreadable while the game runs. bvr_log.cpp now opens it with
    _wfsopen/_SH_DENYWR instead, so from the next native build it can be tailed
    too and -Log can point at it.

    Until then the trigger is the MANAGED log, which is written with
    File.AppendAllText - opened and closed per line, so it can always be read -
    and the marker is SceneRenderingPatch's "MissionScreen.OnSceneRendering-
    Started.", which is unguarded and therefore fires once per mission rather
    than once per process.

    There is no matching mission-END line in the managed log, so -StopPattern
    defaults to empty and the run ends on the frame cap instead.

    IT FLAPS ON ENTRY. A real mission entry in the logs reads active / no
    mission / active within three seconds, as the scene loads. So a start arms a
    SETTLE timer rather than the camera, and a stop is only believed after a
    grace period without the mission coming back.

    WHAT IT CAPTURES. A small BURST every few seconds rather than single frames
    at a steady rate: a burst answers the temporal questions - smear, trails,
    shimmer, crawl - and the spacing between bursts covers the battle.

    It exits on its own after -MaxMissions battles, or -MaxFrames frames, or
    -TimeoutSec with no battle. Exiting is the point: it is meant to be started,
    left alone, and read afterwards.
#>
[CmdletBinding()]
param(
    [string] $Match       = 'VR View',
    [string] $Log         = "$env:USERPROFILE\Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log",
    [string] $OutDir      = "$env:TEMP\vrshot",
    [int]    $SettleSec   = 5,
    [int]    $EveryMs     = 3000,
    [int]    $BurstCount  = 3,
    [int]    $BurstMs     = 120,
    [int]    $MaxFrames   = 90,
    [int]    $MaxWidth    = 1400,
    [int]    $MaxMissions = 1,
    [int]    $TimeoutSec  = 900,
    [int]    $GraceSec    = 4,
    [string] $StartPattern = 'OnSceneRenderingStarted',
    [string] $StopPattern  = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VrShot.psm1') -Force

if (-not (Test-Path $Log)) { Write-Error "No log at $Log - is the mod loaded?" ; return }

Write-Output "watching : $Log"

# WAIT FOR THE WINDOW RATHER THAN REFUSING TO START.
#
# This used to Write-Error and exit if the VR View was not already up, which is
# the normal state of things when the watcher is armed BEFORE the headset
# session - exactly when you want it armed. Worse, it had failed that way twice
# and the second time was mistaken for a capture problem. So the window is
# resolved lazily: the log is followed from the first moment, and the window is
# looked up when there is actually a frame to take.
$win = Find-VrWindow -Match $Match
if ($win) {
    Write-Output "capturing: $($win.Title)  $($win.Width)x$($win.Height)"
} else {
    Write-Output "capturing: '$Match' is not up yet - will look again when a mission starts"
}

# Only new lines. Opened shared, because the game holds this file open and is
# writing to it while we read.
$share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
$fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
$sr = New-Object System.IO.StreamReader($fs)
[void]$fs.Seek(0, [System.IO.SeekOrigin]::End)

$started    = Get-Date
$inMission  = $false
$armedAt    = $null
$stopSeenAt = $null
$missions   = 0
$frames     = 0
$failed     = 0
$dir        = $null
$manifest   = $null

try {
    while ($true) {
        if (((Get-Date) - $started).TotalSeconds -gt $TimeoutSec) {
            Write-Output "timeout: no battle within ${TimeoutSec}s"
            break
        }

        # The log is recreated between sessions; follow it rather than die.
        if ($fs.Length -lt $fs.Position) { [void]$fs.Seek(0, [System.IO.SeekOrigin]::Begin) }

        while ($null -ne ($line = $sr.ReadLine())) {
            if ($line -match $StartPattern) {
                $stopSeenAt = $null
                if (-not $inMission) {
                    $inMission = $true
                    $armedAt   = (Get-Date).AddSeconds($SettleSec)
                    $stamp     = Get-Date -Format 'HHmmss'
                    $dir       = Join-Path $OutDir "mission_$stamp"
                    [void](New-Item -ItemType Directory -Path $dir -Force)
                    $manifest  = Join-Path $dir 'frames.csv'
                    Set-Content -Path $manifest -Value 'file,wallclock' -Encoding utf8
                    $t = Get-Date -Format 'HH:mm:ss'
                    Write-Output "MISSION START $t -> $dir (settling ${SettleSec}s)"
                }
            }
            elseif ($StopPattern -and ($line -match $StopPattern)) {
                if ($inMission -and -not $stopSeenAt) { $stopSeenAt = Get-Date }
            }
        }

        # A stop only counts once it has stood for the grace period; the entry
        # flap puts a "No mission" between two "Mission active" lines.
        if ($stopSeenAt -and ((Get-Date) - $stopSeenAt).TotalSeconds -ge $GraceSec) {
            $inMission  = $false
            $stopSeenAt = $null
            $missions++
            $t = Get-Date -Format 'HH:mm:ss'
            Write-Output "MISSION END   $t  frames so far: $frames"
            if ($missions -ge $MaxMissions) { break }
            $frames = 0
        }

        if ($inMission -and $armedAt -and (Get-Date) -ge $armedAt -and $frames -lt $MaxFrames) {
            for ($b = 1; $b -le $BurstCount -and $frames -lt $MaxFrames; $b++) {
                # Provisional. Only committed once a file actually exists - a
                # run that reported "60 frames" into an empty directory is worse
                # than one that reports nothing, because it is believed.
                $file = Join-Path $dir ("f{0:d3}.png" -f ($frames + 1))
                $now  = Get-Date -Format 'HH:mm:ss.fff'
                try {
                    # May still be null here: the watcher is allowed to start
                    # before the VR View exists.
                    if (-not $win) { $win = Find-VrWindow -Match $Match }

                    $r = $null
                    if ($win) {
                        $r = Save-VrWindowCapture -Handle $win.Handle -Path $file -MaxWidth $MaxWidth
                    }

                    # The handle is resolved once at startup, and SteamVR may
                    # recreate the VR View when the game takes the session -
                    # which is exactly the moment this tool is armed for. A
                    # capture that returns nothing means the handle died, so
                    # look the window up again rather than spending the battle
                    # photographing a closed window.
                    if (-not $r) {
                        $again = Find-VrWindow -Match $Match
                        if ($again) {
                            $win = $again
                            Write-Output "  window re-acquired: $($win.Title) $($win.Width)x$($win.Height)"
                            $r = Save-VrWindowCapture -Handle $win.Handle -Path $file -MaxWidth $MaxWidth
                        }
                    }

                    if ($r -and (Test-Path $file)) {
                        $frames++
                        $leaf = [System.IO.Path]::GetFileName($file)
                        Add-Content -Path $manifest -Value "$leaf,$now" -Encoding utf8
                        if ($frames -eq 1) {
                            Write-Output "  first frame via $($r.Method), source $($r.Width)x$($r.Height)"
                        }
                    }
                    else {
                        $failed++
                        if ($failed -le 3) {
                            Write-Output "  NO FRAME SAVED (handle stale or window gone) - attempt $failed"
                        }
                        if ($failed -eq 12) {
                            Write-Output "  giving up: 12 consecutive failures, nothing is being captured"
                            $frames = $MaxFrames
                        }
                    }
                }
                catch { $failed++; Write-Output "  capture failed: $($_.Exception.Message)" }
                if ($b -lt $BurstCount) { Start-Sleep -Milliseconds $BurstMs }
            }
            if ($frames -ge $MaxFrames) {
                Write-Output "frame cap reached ($MaxFrames); stopping"
                break
            }
            Start-Sleep -Milliseconds $EveryMs
        }
        else {
            Start-Sleep -Milliseconds 250
        }
    }
}
finally {
    $sr.Dispose()
    $fs.Dispose()
}

if ($dir) {
    Write-Output ""
    Write-Output "DONE. $frames frame(s) SAVED in $dir ($failed capture failure(s))"
}
