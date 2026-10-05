#!/usr/bin/env bash
# Runs connect-hearthdaoc.bat under Wine in a throwaway prefix with DRYRUN=1. Local only (needs wine).
set -euo pipefail
command -v wine >/dev/null || { echo "SKIP: wine not installed"; exit 0; }
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
T="$(mktemp -d)"; trap 'WINEPREFIX="$T/pfx" wineserver -k 2>/dev/null || true; rm -rf "$T"' EXIT
export WINEPREFIX="$T/pfx" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d"
wineboot -i >/dev/null 2>&1
fail() { echo "FAIL: $*" >&2; exit 1; }
setup() { rm -rf "$T/app"; mkdir -p "$T/app"; cp "$HERE/../windows/connect-hearthdaoc.bat" "$T/app/"; : > "$T/app/connect.exe"; }

setup
printf '"SERVER=192.168.1.64:10301"\r\n"ACCOUNT=Tester1"\r\n"PASSWORD=pw1"\r\n' > "$T/app/hearthdaoc.cfg"
out="$(cd "$T/app" && DRYRUN=1 wine cmd /c connect-hearthdaoc.bat 2>/dev/null | tr -d '\r')"
grep -qF 'connect.exe game.dll "192.168.1.64:10301" "Tester1" "pw1"' <<<"$out" || fail "saved settings: got: $out"
echo "ok - connect-hearthdaoc.bat builds the connect.exe command from hearthdaoc.cfg"

# First run: the answers are saved; cmd metacharacters in the password must survive saving and loading.
setup
# (Not % : Wine's cmd re-expands percent signs inside values, which real cmd does not; players are told to avoid it.)
pw='p&w|x<y>^z!)'
# (Answers come from the environment: Wine's set /p swallows the rest of piped or redirected input.)
out="$(cd "$T/app" && SERVER=192.168.1.64:10301 ACCOUNT=Tester2 PASSWORD="$pw" DRYRUN=1 wine cmd /c connect-hearthdaoc.bat 2>/dev/null | tr -d '\r')"
grep -qF "connect.exe game.dll \"192.168.1.64:10301\" \"Tester2\" \"$pw\"" <<<"$out" || fail "special password on first run: got: $out"
grep -qF "\"PASSWORD=$pw\"" <(tr -d '\r' < "$T/app/hearthdaoc.cfg") || fail "special password not saved intact: $(cat "$T/app/hearthdaoc.cfg")"
out="$(cd "$T/app" && DRYRUN=1 wine cmd /c connect-hearthdaoc.bat 2>/dev/null | tr -d '\r')"
grep -qF "\"$pw\"" <<<"$out" || fail "special password from saved settings: got: $out"
echo "ok - passwords with & | < > ^ ! ) survive saving, loading and the command line"
