$exe = 'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-Process -FilePath $exe
Write-Host 'launched plain GUI, waiting 15s with NO network probing...'
Start-Sleep -Seconds 15
$p = Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue
if ($p -eq $null) {
  Write-Host 'DEAD within 15s - crash reproduces on plain launch'
} else {
  Write-Host ('ALIVE after 15s - pid=' + $p.Id)
  Start-Sleep -Seconds 10
  $p2 = Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue
  if ($p2 -eq $null) {
    Write-Host 'died between 15-25s'
  } else {
    Write-Host 'STILL ALIVE after 25s total - plain GUI launch is STABLE'
  }
}
