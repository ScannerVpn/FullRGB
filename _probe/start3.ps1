$exe = 'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-Process -FilePath $exe -ArgumentList '--server','--serverport','6742'
Start-Sleep -Seconds 20
Write-Host '=== process ==='
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Format-Table Id, ProcessName, MainWindowTitle
Write-Host '=== netstat 6742 ==='
netstat -ano | Select-String '6742'
Write-Host '=== latest log tail ==='
$log = Get-ChildItem "$env:APPDATA\OpenRGB\logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log.FullName | Select-Object -Last 12
