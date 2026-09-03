$exe = 'G:\Ai\RGB Control\src\FullRGB\bin\Release\net8.0-windows\win-x64\publish\FullRGB.exe'
Write-Host '--- processes locking publish exe ---'
Get-Process -Name 'FullRGB' -ErrorAction SilentlyContinue | Format-Table Id, ProcessName, Path
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Format-Table Id, ProcessName
Write-Host '--- kill all ---'
Get-Process -Name 'FullRGB','OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if (Test-Path $exe) {
  try {
    Remove-Item $exe -Force
    Write-Host 'old exe deleted OK'
  } catch {
    Write-Host ('delete failed: ' + $_.Exception.Message)
  }
}
