#!/usr/bin/env bash
# FullRGB verification gate. Runs the three headless suites and reports each result.
# Usage: bash tools/verify.sh [Debug|Release]
set -euo pipefail
CFG="${1:-Debug}"
if [[ "$CFG" != "Debug" && "$CFG" != "Release" ]]; then echo "usage: $0 [Debug|Release]" >&2; exit 2; fi
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="$SCRIPT_DIR/../src/FullRGB/bin/$CFG/net8.0-windows/win-x64/FullRGB.exe"
if [[ ! -x "$EXE" && ! -f "$EXE" ]]; then echo "missing exe: $EXE (build first)" >&2; exit 1; fi
TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

run() {   # run <outfile> <timeout-ms> <grep-pattern> <args...>
  local out="$1" ms="$2" pat="$3"; shift 3
  local arglist=""
  for a in "$@"; do arglist+="@('$a'),"; done
  arglist="@(${arglist%,})"
  local win_exe
  win_exe="$(cygpath -w "$EXE" 2>/dev/null || echo "$EXE")"
  local win_tmp
  win_tmp="$(cygpath -w "$TMPDIR" 2>/dev/null || echo "$TMPDIR")"
  powershell.exe -NoProfile -Command "\$p = Start-Process -FilePath '$win_exe' -ArgumentList $arglist -RedirectStandardOutput '$win_tmp\\$out' -PassThru -NoNewWindow; if (-not \$p.WaitForExit($ms)) { try { \$p.Kill() } catch {}; exit 11 }; exit \$p.ExitCode" >/dev/null 2>&1
  local code=$?
  if [[ $code -eq 11 ]]; then echo "TIMEOUT after ${ms}ms: $*" >&2; return 1; fi
  if [[ ! -f "$TMPDIR/$out" ]]; then echo "no output captured: $*" >&2; return 1; fi
  tr -d '\000' < "$TMPDIR/$out" | grep -aE "$pat"
  # veto real failures only: bare FAIL also matches test names like "failure flagged"
  if tr -d '\000' < "$TMPDIR/$out" | grep -aqE "^\[FAIL\]|TEST\(S\) FAILED"; then echo "gate FAILED: $*" >&2; return 1; fi
  return $code
}

echo "== rendertest (pure logic) =="
run rt.txt 120000 "FAIL|PASSED|TEST\(S\)" '--rendertest'

echo "== uitest (XAML, resources, glyphs, l10n) =="
run ui.txt 120000 "FAIL|PASSED|inner|TEST\(S\)" '--uitest'

echo "== fxtest (REAL hardware path) =="
taskkill /F /IM OpenRGB.exe >/dev/null 2>&1 || true
run fx.txt 180000 "framesSent|ERR|FAILED|dev[0-9]" '--fxtest' '--seconds=14'
