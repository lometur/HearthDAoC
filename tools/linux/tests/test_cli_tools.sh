#!/usr/bin/env bash
# Integration tests for the Linux CLIs. Usage: test_cli_tools.sh <tools-out-dir>
# Needs HDC_TEST_WORLD = path to a clean classic world database (opendaoc.sqlite3.db).
set -euo pipefail
OUT="${1:?usage: $0 <tools-out-dir>}"
: "${HDC_TEST_WORLD:?set HDC_TEST_WORLD to a clean classic world database}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
pass=0; fail() { echo "FAIL: $*" >&2; exit 1; }; ok() { pass=$((pass + 1)); echo "ok - $*"; }
q() { sqlite3 "$1" "$2"; }

# The upstream importer refuses while anything listens on TCP 10300 or a CoreServer process runs.
# Run it in private network and process namespaces when port 10300 is busy on this machine.
importer() {
    if ss -ltn 'sport = :10300' | grep -q 10300; then
        bwrap --ro-bind / / --dev /dev --proc /proc --bind "$T" "$T" --unshare-net --unshare-pid \
            dotnet "$OUT/progress-import/progress-import.dll" "$@"
    else
        dotnet "$OUT/progress-import/progress-import.dll" "$@"
    fi
}

# offline-bots
mkdir -p "$T/w"; cp "$HDC_TEST_WORLD" "$T/w/opendaoc.sqlite3.db"
dotnet "$OUT/offline-bots/offline-bots.dll" add "$T/w/opendaoc.sqlite3.db" 2 1x1 >/dev/null
[[ "$(q "$T/w/opendaoc.sqlite3.db" 'SELECT count(*) FROM offline_world_bots')" == 1 ]] || fail "offline-bots did not add a bot"
[[ "$(q "$T/w/opendaoc.sqlite3.db" "SELECT Value FROM offline_population_settings WHERE Key='ActiveTarget'")" == 1 ]] || fail "population target not updated"
ok "offline-bots adds a level-1 Midgard bot and updates the population target"
dotnet "$OUT/offline-bots/offline-bots.dll" add "$T/w/opendaoc.sqlite3.db" 1 1x50 >/dev/null
(( $(q "$T/w/opendaoc.sqlite3.db" "SELECT count(*) FROM Inventory WHERE OwnerID LIKE 'offlinebot:%'") >= 15 )) || fail "level-50 bot has no gear"
ok "offline-bots gives a level-50 bot its gear"

# bot-goals (port 9 is never listening, so writes are allowed)
dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" set 50 10 30 60 >/dev/null
[[ -f "$T/w/bot-goals.json" ]] && dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" show | grep -qE '^  50 .*60%$' \
    || fail "bot-goals set did not write"
ok "bot-goals set writes bot-goals.json"
if dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" set 50 10 30 50 2>"$T/err"; then fail "bad total accepted"; fi
grep -q "add up to exactly 100" "$T/err" || fail "bad total message missing"
if dotnet "$OUT/bot-goals/bot-goals.dll" --port 9 "$T/w" set 1-19 50 40 10 2>"$T/err"; then fail "low-level RvR accepted"; fi
grep -q "cannot have RvR" "$T/err" || fail "low-level RvR message missing"
ok "bot-goals rejects invalid percentages with upstream's messages"
python3 -m http.server 10399 --bind 127.0.0.1 >/dev/null 2>&1 & srv=$!; sleep 1
if dotnet "$OUT/bot-goals/bot-goals.dll" --port 10399 "$T/w" reset 2>"$T/err"; then kill "$srv"; fail "write allowed while port busy"; fi
kill "$srv"; grep -q "Stop the server" "$T/err" || fail "busy-port message missing"
ok "bot-goals refuses to save while the server port is listening"

# progress-import round trip: old = world with bots and an account, new = clean world
mkdir -p "$T/old/runtime/data" "$T/new/runtime/data"
cp "$T/w/opendaoc.sqlite3.db" "$T/old/runtime/data/opendaoc.sqlite3.db"
sqlite3 "$T/old/runtime/data/opendaoc.sqlite3.db" "INSERT INTO Account (Name, Password, CreationDate, PrivLevel, Account_ID) VALUES ('tester', '##00', '2026-10-05 10:00:00.000000', 1, 'acc-1')"
cp "$HDC_TEST_WORLD" "$T/new/runtime/data/opendaoc.sqlite3.db"
importer --import "$T/old" "$T/new" --replace-progress "$T/report.txt" || { cat "$T/report.txt"; fail "import failed"; }
[[ "$(q "$T/new/runtime/data/opendaoc.sqlite3.db" 'SELECT count(*) FROM offline_world_bots')" == 2 ]] || fail "bots not imported"
[[ "$(q "$T/new/runtime/data/opendaoc.sqlite3.db" "SELECT count(*) FROM Account WHERE Name='tester'")" == 1 ]] || fail "account not imported"
ok "progress-import moves bots and accounts into a clean world"

echo "All $pass CLI checks passed."
