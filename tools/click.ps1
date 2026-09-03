Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class C {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
# click at a CLIENT-relative offset inside the FullRGB window
$dx = [int]$args[0]; $dy = [int]$args[1]
$p = Get-Process FullRGB -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$r = New-Object C+RECT
[C]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
[C]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 400
$x = $r.Left + $dx; $y = $r.Top + $dy
[C]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 150
[C]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
[C]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
Start-Sleep -Milliseconds 700
Write-Output ("clicked {0},{1} (screen {2},{3})" -f $dx, $dy, $x, $y)
