#!/usr/bin/env bash
# Runs connect-central.bat under Wine in a throwaway prefix with DRYRUN=1. Local only (needs wine).
set -euo pipefail
command -v wine >/dev/null || { echo "SKIP: wine not installed"; exit 0; }
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
T="$(mktemp -d)"; trap 'WINEPREFIX="$T/pfx" wineserver -k 2>/dev/null || true; rm -rf "$T"' EXIT
mkdir -p "$T/app"; cp "$HERE/../windows/connect-central.bat" "$T/app/"; : > "$T/app/connect.exe"
printf 'SERVER=192.168.1.64:10301\r\nACCOUNT=Tester1\r\nPASSWORD=pw1\r\n' > "$T/app/central-server.cfg"
export WINEPREFIX="$T/pfx" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d"
wineboot -i >/dev/null 2>&1
out="$(cd "$T/app" && DRYRUN=1 wine cmd /c connect-central.bat 2>/dev/null | tr -d '\r')"
grep -q "connect.exe game.dll 192.168.1.64:10301 Tester1 pw1" <<<"$out" || { echo "FAIL: got: $out" >&2; exit 1; }
echo "ok - connect-central.bat builds the connect.exe command from central-server.cfg"
