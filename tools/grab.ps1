Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class P {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
# Captures the FullRGB window's OWN pixels via PrintWindow(PW_RENDERFULLCONTENT).
# Screen-copy capture (CopyFromScreen) grabs whatever is visually on top, which silently
# produced a screenshot of a different app; PrintWindow asks the window to render itself.
$name = if ($args.Count -ge 2) { $args[1] } else { 'FullRGB' }
$p = Get-Process $name -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'NO_WINDOW'; exit 1 }
$h = $p.MainWindowHandle
if ([P]::IsIconic($h)) { [P]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 700 }
$r = New-Object P+RECT
[P]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Output 'BAD_RECT'; exit 1 }
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [P]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
$out = Join-Path $env:TEMP ("win-" + $args[0] + ".png")
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("printwindow=" + $ok + " rect=" + $w + "x" + $ht + " SAVED " + $out)
