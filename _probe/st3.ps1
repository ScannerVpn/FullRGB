$exe = 'G:\Ai\RGB Control\src\FullRGB\bin\Debug\net8.0-windows\win-x64\FullRGB.exe'
Get-Process -Name 'OpenRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'FullRGB' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$p = Start-Process -FilePath $exe -ArgumentList '--selftest' -PassThru -NoNewWindow `
     -RedirectStandardOutput 'G:\Ai\RGB Control\_probe\st_out.txt' -RedirectStandardError 'G:\Ai\RGB Control\_probe\st_err.txt'
$done = $p.WaitForExit(60000)
if (-not $done) {
  Write-Host 'TIMEOUT'
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
} else {
  Write-Host ('exitcode=' + $p.ExitCode)
}
Get-Content 'G:\Ai\RGB Control\_probe\st_out.txt'
