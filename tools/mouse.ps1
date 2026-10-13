# Dev helper: moves the cursor (physical px) and optionally clicks, for scripted UI checks.
param([int]$X, [int]$Y, [switch]$Click)
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, System.IntPtr e);
}
'@
[M]::SetProcessDPIAware() | Out-Null
[M]::SetCursorPos($X, $Y) | Out-Null
# Nudge with real input so the target window receives WM_MOUSEMOVE (SetCursorPos alone may not)
[M]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30; [M]::mouse_event(1, -1, 0, 0, [IntPtr]::Zero)
if ($Click) { Start-Sleep -Milliseconds 50; [M]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); [M]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero) }
