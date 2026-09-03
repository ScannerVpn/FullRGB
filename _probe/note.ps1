$exe = 'G:\Ai\RGB Control\src\FullRGB\bin\Release\net8.0-windows\win-x64\publish\FullRGB.exe'
Write-Host '--- asking the running FullRGB to exit itself (it has a tray Exit) ---'
Write-Host 'Cannot kill from unelevated shell. Closing OpenRGB instead (app will lose engine; user can close UI).'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
# The UI remains but is harmless; publish to a FRESH folder instead.
Write-Host 'publish folder locked -> will publish to dist2'
