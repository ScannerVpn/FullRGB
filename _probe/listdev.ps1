$dir = 'G:\Ai\RGB Control\_probe'
$exe = 'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
Start-Process -FilePath $exe -ArgumentList '--list-devices' -Wait -NoNewWindow `
  -RedirectStandardOutput "$dir\out.txt" -RedirectStandardError "$dir\err.txt"
Write-Host '=== STDOUT ==='
Get-Content "$dir\out.txt"
Write-Host '=== STDERR ==='
Get-Content "$dir\err.txt"
