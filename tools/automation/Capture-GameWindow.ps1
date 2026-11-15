param([Parameter(Mandatory)][string]$OutFile, [string]$Process = 'PlateUp')
# Captures only the client area of the game window (not other monitors).
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W]::SetProcessDPIAware() | Out-Null
$p = Get-Process $Process -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "No window for $Process" }
$r = New-Object W+RECT; [W]::GetClientRect($p.MainWindowHandle, [ref]$r) | Out-Null
$pt = New-Object W+POINT; [W]::ClientToScreen($p.MainWindowHandle, [ref]$pt) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object Drawing.Bitmap $w, $h
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($pt.X, $pt.Y, 0, 0, (New-Object Drawing.Size $w, $h))
$bmp.Save($OutFile, [Drawing.Imaging.ImageFormat]::Png)
"client origin=($($pt.X),$($pt.Y)) size=${w}x${h} -> $OutFile"
