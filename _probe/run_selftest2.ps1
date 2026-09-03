$exe = 'G:\Ai\RGB Control\src\FullRGB\bin\Debug\net8.0-windows\win-x64\FullRGB.exe'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'FullRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process -FilePath $exe -ArgumentList '--selftest' -PassThru -NoNewWindow `
     -RedirectStandardOutput 'G:\Ai\RGB Control\_probe\st_out.txt' -RedirectStandardError 'G:\Ai\RGB Control\_probe\st_err.txt'
$done = $p.WaitForExit(150000)
if (-not $done) {
  Write-Host 'TIMEOUT: selftest still running after 150s, killing'
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
} else {
  Write-Host ('exitcode=' + $p.ExitCode)
}
Write-Host '--- stdout ---'
Get-Content 'G:\Ai\RGB Control\_probe\st_out.txt'
Write-Host '--- stderr ---'
Get-Content 'G:\Ai\RGB Control\_probe\st_err.txt'
Write-Host '--- leftover procs ---'
Get-Process -Name 'OpenRGB','FullRGB' -ErrorAction SilentlyContinue | Format-Table Id, ProcessName
