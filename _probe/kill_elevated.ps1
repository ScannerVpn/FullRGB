# Stop the elevated FullRGB instance via Task Scheduler (runs as SYSTEM/admin)
$taskId = 'FullRGB_Kill'
$cmd = 'taskkill /F /IM FullRGB.exe'
schtasks /Create /F /TN $taskId /SC ONCE /ST 23:59 /RL HIGHEST /TR $cmd | Out-Null
schtasks /Run /TN $taskId | Out-Null
Start-Sleep -Seconds 4
schtasks /Delete /F /TN $taskId | Out-Null
$p = Get-Process -Name 'FullRGB' -ErrorAction SilentlyContinue
if ($p) { Write-Host 'STILL RUNNING:' $p.Id } else { Write-Host 'FULLRGB_STOPPED' }
