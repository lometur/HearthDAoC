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

# The patch step: when patch-client.ps1 is next to the .bat, it runs just before connect.exe starts.
# A stand-in powershell.cmd in the app folder (cmd finds it before the real one) echoes its arguments
# and exits with FAKE_PS_EXIT.
save_settings() { printf '"SERVER=192.168.1.64:10301"\r\n"ACCOUNT=Tester1"\r\n"PASSWORD=pw1"\r\n' > "$T/app/hearthdaoc.cfg"; }
setup_patch() {
    setup; save_settings
    : > "$T/app/patch-client.ps1"
    printf '@echo off\r\necho stand-in powershell %%*\r\nexit /b %%FAKE_PS_EXIT%%\r\n' > "$T/app/powershell.cmd"
}
run_bat() {  # run_bat <stand-in exit code> [folder]: the .bat's output; a key for its pause comes on stdin
    (cd "${2:-$T/app}" && FAKE_PS_EXIT="$1" DRYRUN=1 wine cmd /c connect-hearthdaoc.bat 2>/dev/null <<<"" | tr -d '\r')
}
line_of() { { grep -nE -m1 -- "$1" <<<"$2" || true; } | cut -d: -f1; }  # line_of <regex> <text>: first matching line number, or nothing
PATCH_LINE='^powershell -NoProfile -ExecutionPolicy Bypass -File ".*\\app\\patch-client\.ps1"$'
STAND_IN='^stand-in powershell -NoProfile -ExecutionPolicy Bypass -File ".*\\app\\patch-client\.ps1"$'
# (Not anchored at the start: after the pause, Wine prints the next line on the same one.)
CONNECT_LINE='connect\.exe game\.dll "192\.168\.1\.64:10301" "Tester1" "pw1"$'
WARNING='^Warning: .*patch-client\.bat'
PAUSE='Press any key'

for code in 0 3 1; do
    setup_patch
    out="$(run_bat "$code")"
    patch="$(line_of "$PATCH_LINE" "$out")"; ran="$(line_of "$STAND_IN" "$out")"; connect="$(line_of "$CONNECT_LINE" "$out")"
    [[ -n "$patch" && -n "$ran" && -n "$connect" ]] || fail "exit $code: patch line, patch run or connect line missing: got: $out"
    (( patch < ran && ran < connect )) || fail "exit $code: the patch step must come before connect.exe: got: $out"
    warning="$(line_of "$WARNING" "$out")"; pause="$(line_of "$PAUSE" "$out")"
    if [[ $code == 1 ]]; then
        [[ -n "$warning" && -n "$pause" ]] && (( ran < warning && warning < pause && pause <= connect )) ||
            fail "exit 1: no warning and pause before connect.exe: got: $out"
    else
        [[ -z "$warning$pause" ]] || fail "exit $code: unexpected warning or pause: got: $out"
    fi
done
echo "ok - connect-hearthdaoc.bat runs patch-client.ps1 before connect.exe; exit 0 and 3 go on, others warn, pause and go on"

# The folder name reaches powershell as it is, cmd's special characters included (not %: see above).
setup_patch
odd="$T/odd & dir ^1 (x)!"
rm -rf "$odd"; cp -r "$T/app" "$odd"
out="$(run_bat 0 "$odd")"
grep -F 'stand-in powershell -NoProfile -ExecutionPolicy Bypass -File "' <<<"$out" |
    grep -qF '\odd & dir ^1 (x)!\patch-client.ps1"' || fail "folder with & ^ ( ) !: got: $out"
[[ -n "$(line_of "$CONNECT_LINE" "$out")" ]] || fail "folder with & ^ ( ) !: no connect line: got: $out"
echo "ok - the patch step works in a folder whose name has & ^ ( ) !"

setup_patch
rm "$T/app/patch-client.ps1"
out="$(run_bat 1)"
[[ -n "$(line_of "$CONNECT_LINE" "$out")" ]] || fail "without patch-client.ps1: no connect line: got: $out"
! grep -qi 'powershell' <<<"$out" || fail "without patch-client.ps1: a patch step ran: got: $out"
echo "ok - without patch-client.ps1 there is no patch step"
