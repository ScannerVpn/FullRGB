$ErrorActionPreference = 'Continue'
$dir = 'G:\Ai\RGB Control\_probe'
$exe = '"G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe"'
# Kill any existing instance first
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
# Register + run elevated via Task Scheduler
schtasks /Create /F /TN "FullRGB_probe" /SC ONCE /ST 23:59 /IT /RL HIGHEST /TR $exe | Out-Null
schtasks /Run /TN "FullRGB_probe" | Out-Null
Write-Host "scheduled-run issued, waiting for device detection..."
# Wait until SDK port responds or timeout
$ok = $false
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Seconds 1
  $t = Test-NetConnection -ComputerName 127.0.0.1 -Port 6742 -WarningAction SilentlyContinue -InformationLevel Quiet
  if ($t) { $ok = $true; break }
}
Write-Host "sdk_port_6742_open=$ok after $i seconds"
if ($ok) {
  Write-Host '=== probing SDK ==='
  $c = New-Object Net.Sockets.TcpClient('127.0.0.1', 6742)
  $s = $c.GetStream()
  # NET_PROTOCOL_VERSION packet: magic 0x696C6F4E 'Noli', cmd 2, proto 0
  $p = [byte[]](0x4E,0x69,0x6C,0x6F,0x02,0x00,0x00,0x00,0x00)
  $s.Write($p,0,$p.Length); $s.Flush()
  Start-Sleep -Milliseconds 800
  $buf = New-Object byte[] 65536
  try { if ($s.DataAvailable) { $n = $s.Read($buf,0,$buf.Length); Write-Host ("reply_bytes=" + $n); Write-Host (($buf[0..([Math]::Min($n,32)-1)] | ForEach-Object { $_.ToString('X2') }) -join ' ') } else { Write-Host 'no reply yet' } } catch { Write-Host "read error: $_" }
  $c.Close()
}
