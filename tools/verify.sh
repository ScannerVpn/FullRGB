#!/usr/bin/env bash
# FullRGB verification gate. Runs the three headless suites and reports each result.
# Usage: bash tools/verify.sh [Debug|Release]
set -u
CFG="${1:-Debug}"
EXE="G:/Ai/RGB Control/src/FullRGB/bin/$CFG/net8.0-windows/win-x64/FullRGB.exe"
TMP="$LOCALAPPDATA/Temp"

run() {   # run <arg-list> <outfile> <timeout-ms> <grep-pattern>
  local args="$1" out="$2" ms="$3" pat="$4"
  rm -f "$TMP/$out"
  powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath '$(cygpath -w "$EXE" 2>/dev/null || echo "$EXE")' -ArgumentList $args -RedirectStandardOutput \$env:TEMP\\$out -PassThru -NoNewWindow; \$p.WaitForExit($ms)" >/dev/null 2>&1
  tr -d '\000' < "$TMP/$out" | grep -aE "$pat"
}

echo "== rendertest (pure logic) =="
run "'--rendertest'" rt.txt 120000 "FAIL|PASSED|TEST\(S\)"

echo "== uitest (XAML, resources, glyphs, l10n) =="
run "'--uitest'" ui.txt 120000 "FAIL|PASSED|inner|TEST\(S\)"

echo "== fxtest (REAL hardware path) =="
taskkill /F /IM OpenRGB.exe >/dev/null 2>&1
run "'--fxtest','--seconds=14'" fx.txt 180000 "framesSent|ERR|FAILED|dev[0-9]"
