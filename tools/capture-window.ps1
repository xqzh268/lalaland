# Dev helper: captures a window by title (even when occluded) via PrintWindow(PW_RENDERFULLCONTENT).
param([string]$Title, [string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class C {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
'@
[C]::SetProcessDPIAware() | Out-Null
$h = [C]::FindWindow($null, $Title)
if ($h -eq [IntPtr]::Zero) { throw "Window '$Title' not found" }
$r = New-Object C+RECT; [C]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[C]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $bmp.Save($Out); "$($bmp.Width)x$($bmp.Height)"
