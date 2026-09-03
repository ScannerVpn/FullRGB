$ErrorActionPreference = 'Continue'
$exe = 'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
# Try server-only mode first
Start-Process -FilePath $exe -ArgumentList '--server','--serverport','6742'
$ok = $false
for ($i = 0; $i -lt 25; $i++) {
  Start-Sleep -Seconds 1
  $t = Test-NetConnection -ComputerName 127.0.0.1 -Port 6742 -WarningAction SilentlyContinue -InformationLevel Quiet
  if ($t) { $ok = $true; break }
}
Write-Host "server_mode_port_open=$ok after $i s"
if (-not $ok) {
  Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  Write-Host 'trying --startserver variant...'
  Start-Process -FilePath $exe -ArgumentList '--startserver','--serverport','6742'
  for ($i = 0; $i -lt 25; $i++) {
    Start-Sleep -Seconds 1
    $t = Test-NetConnection -ComputerName 127.0.0.1 -Port 6742 -WarningAction SilentlyContinue -InformationLevel Quiet
    if ($t) { $ok = $true; break }
  }
  Write-Host "startserver_mode_port_open=$ok after $i s"
}
