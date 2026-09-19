<#
    VrShot.psm1 - the capture engine behind vrshot.ps1 and vrwatch.ps1.

    Capture is PrintWindow(PW_RENDERFULLCONTENT) first, which reaches a
    DWM-composited window even when something is in front of it - which is the
    case that matters here, because during a battle the game is in front of the
    VR View. It falls back to a screen-rectangle grab when PrintWindow returns
    something uniform, and that fallback DOES need the window unoccluded.
#>

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class VrShotNative
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern int  GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);

    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);

    public static System.Collections.Generic.List<string> Titles = new System.Collections.Generic.List<string>();
    public static System.Collections.Generic.List<IntPtr> Handles = new System.Collections.Generic.List<IntPtr>();

    public static void Collect()
    {
        Titles.Clear();
        Handles.Clear();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            StringBuilder sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left < 64 || r.Bottom - r.Top < 64) return true;
            Titles.Add(sb.ToString());
            Handles.Add(h);
            return true;
        }, IntPtr.Zero);
    }
}
"@ -ErrorAction SilentlyContinue

[void][VrShotNative]::SetProcessDPIAware()

function Get-VrWindows {
    [VrShotNative]::Collect()
    for ($i = 0; $i -lt [VrShotNative]::Titles.Count; $i++) {
        $h = [VrShotNative]::Handles[$i]
        $r = New-Object VrShotNative+RECT
        [void][VrShotNative]::GetWindowRect($h, [ref]$r)
        [pscustomobject]@{
            Title  = [VrShotNative]::Titles[$i]
            Handle = $h
            Width  = $r.Right - $r.Left
            Height = $r.Bottom - $r.Top
        }
    }
}

function Find-VrWindow {
    param([Parameter(Mandatory)] [string] $Match)
    Get-VrWindows | Where-Object { $_.Title -like "*$Match*" } | Select-Object -First 1
}

# A PrintWindow that returns a near-uniform image did not reach the swapchain.
function Test-VrBlank {
    param([Parameter(Mandatory)] [System.Drawing.Bitmap] $Bitmap)
    $step = [Math]::Max(1, [int]($Bitmap.Width / 24))
    $seen = @{}
    for ($x = 0; $x -lt $Bitmap.Width; $x += $step) {
        for ($y = 0; $y -lt $Bitmap.Height; $y += $step) {
            $p = $Bitmap.GetPixel($x, $y)
            $seen[('{0}_{1}_{2}' -f $p.R, $p.G, $p.B)] = $true
            if ($seen.Count -gt 6) { return $false }
        }
    }
    return $true
}

function Save-VrScaled {
    param(
        [Parameter(Mandatory)] [System.Drawing.Bitmap] $Bitmap,
        [Parameter(Mandatory)] [string] $Path,
        [int] $MaxWidth = 1600
    )
    if ($Bitmap.Width -le $MaxWidth) {
        $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        return
    }
    $nw  = $MaxWidth
    $nh  = [int]([Math]::Round($Bitmap.Height * ($MaxWidth / $Bitmap.Width)))
    $out = New-Object System.Drawing.Bitmap $nw, $nh
    $g   = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($Bitmap, 0, 0, $nw, $nh)
    $g.Dispose()
    $out.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
}

function Save-VrWindowCapture {
    param(
        [Parameter(Mandatory)] [IntPtr] $Handle,
        [Parameter(Mandatory)] [string] $Path,
        [int] $MaxWidth = 1600
    )

    $cr = New-Object VrShotNative+RECT
    if (-not [VrShotNative]::GetClientRect($Handle, [ref]$cr)) { return $null }
    $w = $cr.Right - $cr.Left
    $h = $cr.Bottom - $cr.Top
    if ($w -lt 8 -or $h -lt 8) { return $null }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok  = [VrShotNative]::PrintWindow($Handle, $hdc, 2)   # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    $method = 'PrintWindow'
    if (-not $ok -or (Test-VrBlank $bmp)) {
        $bmp.Dispose()
        $origin = New-Object VrShotNative+POINT
        [void][VrShotNative]::ClientToScreen($Handle, [ref]$origin)
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($origin.X, $origin.Y, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $g.Dispose()
        $method = 'screen grab'
    }

    Save-VrScaled -Bitmap $bmp -Path $Path -MaxWidth $MaxWidth
    $bmp.Dispose()

    [pscustomobject]@{ Path = $Path; Method = $method; Width = $w; Height = $h }
}

Export-ModuleMember -Function Get-VrWindows, Find-VrWindow, Save-VrWindowCapture, Save-VrScaled, Test-VrBlank
