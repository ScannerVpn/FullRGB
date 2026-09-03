$exe = 'G:\Ai\RGB Control\src\FullRGB\bin\Debug\net8.0-windows\win-x64\FullRGB.exe'
Write-Host '--- killing leftover OpenRGB ---'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Write-Host '--- running selftest ---'
$out = & $exe --selftest 2>&1
$out | Select-Object -Last 30
Write-Host ('exitcode=' + $LASTEXITCODE)
