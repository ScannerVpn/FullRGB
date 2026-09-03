Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PW {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
# Captures N frames of the FullRGB window and reports whether the HERO PREVIEW STRIP pixels
# change between frames. A single screenshot cannot prove animation; this can.
# usage: anim.ps1 <frames> <delayMs>
$frames = if ($args.Count -ge 1) { [int]$args[0] } else { 4 }
$delay  = if ($args.Count -ge 2) { [int]$args[1] } else { 350 }
$p = Get-Process FullRGB -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'NO_WINDOW'; exit 1 }
$h = $p.MainWindowHandle
$r = New-Object PW+RECT
[PW]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top

$prev = $null
for ($i = 0; $i -lt $frames; $i++) {
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [PW]::PrintWindow($h, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()

  # sample a horizontal line across the hero strip (y ~ 120 in client space, inside the preview)
  $y = 120
  $samples = @()
  foreach ($x in 120, 200, 280, 360, 440) {
    $c = $bmp.GetPixel($x, $y)
    $samples += ('{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B)
  }
  $line = $samples -join ' '
  $changed = if ($prev -eq $null) { 'first' } elseif ($prev -ne $line) { 'CHANGED' } else { 'same' }
  Write-Output ("frame {0}: {1}   {2}" -f $i, $line, $changed)
  $prev = $line
  $bmp.Save((Join-Path $env:TEMP ("anim-$i.png")), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Start-Sleep -Milliseconds $delay
}
