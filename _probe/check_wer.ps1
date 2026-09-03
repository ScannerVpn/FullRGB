Write-Host '=== WER reports mentioning OpenRGB ==='
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddHours(-3)} -ErrorAction SilentlyContinue |
  Where-Object { $_.Message -match 'OpenRGB' } |
  Select-Object -First 5 TimeCreated, Id, ProviderName | Format-List
Write-Host '=== WER ReportArchive files ==='
Get-ChildItem 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive' -Directory -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match 'OpenRGB' } | Select-Object -Last 3 -ExpandProperty Name
Write-Host '=== move old config away ==='
Move-Item "$env:APPDATA\OpenRGB\OpenRGB.json" "$env:APPDATA\OpenRGB\OpenRGB.json.bak" -Force -ErrorAction SilentlyContinue
Write-Host "config moved: $(-not (Test-Path "$env:APPDATA\OpenRGB\OpenRGB.json"))"
