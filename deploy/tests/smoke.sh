#!/usr/bin/env bash
# Container smoke test (world database only, test ports). Usage: smoke.sh <image>
set -euo pipefail
IMAGE="${1:?usage: $0 <image>}"
NAME="hearthdaoc-smoke-$$"; VOL="hearthdaoc-smoke-$$"; PORT="${SMOKE_PORT:-10391}"; UDP="${SMOKE_UDP:-10491}"
T="$(mktemp -d)"
cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; docker volume rm -f "$VOL" "$VOL-ro" "$VOL-uid" >/dev/null 2>&1 || true; rm -rf "$T"; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; docker logs "$NAME" > "$T/fail.log" 2>&1 || true; tail -40 "$T/fail.log" >&2; exit 1; }
# Read logs into a file first: piping docker logs into grep -q under pipefail can fail on SIGPIPE.
logs_have() { docker logs "$NAME" > "$T/logs" 2>&1 || true; grep -q -- "$1" "$T/logs"; }
run() { docker run -d --name "$NAME" --network host -v "$VOL":/data -e HEARTHDAOC_EDITION="${1:-classic}" \
            -e HEARTHDAOC_PORT="$PORT" -e HEARTHDAOC_UDP_PORT="$UDP" -e HEARTHDAOC_SKIP_NAVMESH=1 "$IMAGE" >/dev/null; }
wait_listen() {
    for _ in $(seq 1 300); do
        logs_have "Server is now listening for incoming connections on 0.0.0.0:$PORT" && return 0
        [[ "$(docker inspect -f '{{.State.Running}}' "$NAME")" == true ]] || return 1
        sleep 2
    done
    return 1
}
db=/data/world/opendaoc.sqlite3.db

run; wait_listen || fail "server did not start"
echo "ok - server listens on $PORT"
[[ "$(docker exec "$NAME" printenv PYTHONUNBUFFERED)" == 1 ]] || fail "python output is buffered (no download progress in logs)"
echo "ok - python output is unbuffered"
sleep 5
if docker exec "$NAME" grep -q "no such table: Events" /data/logs/warn.log; then fail "realm-event ledger broken"; fi
docker exec "$NAME" find /data/state -name 'realm-event-records.sqlite3' -empty | grep -q . && fail "empty realm-event ledger file in /data/state"
# The server keeps its realm-event ledger in its own folder; the entrypoint must move it into /data on stop.
docker exec "$NAME" sh -c 'test -L /app/server/realm-event-records.sqlite3 || sqlite3 /app/server/realm-event-records.sqlite3 "CREATE TABLE IF NOT EXISTS smoke_probe (x)"'
echo "ok - realm-event ledger healthy at start"
docker exec "$NAME" grep -q "<Port>$PORT</Port>" /app/server/config/serverconfig.xml || fail "port not in config"
docker exec "$NAME" grep -q "<EnableUPnP>False</EnableUPnP>" /app/server/config/serverconfig.xml || fail "UPnP not off"
docker exec "$NAME" test -f /app/server/config/logconfig.xml || fail "logconfig.xml missing"
echo "ok - generated config and release config files present"
[[ "$(docker exec "$NAME" sqlite3 /data/world/opendaoc.sqlite3.db 'SELECT group_concat(DISTINCT Port) FROM Regions')" == "$UDP" ]] \
    || fail "world regions do not tell clients UDP port $UDP"
echo "ok - world regions tell clients the server's UDP port"
docker exec "$NAME" cat /data/logs/server.log > "$T/server.log"
grep -q "Command - '&tele' .* required plvl:2" "$T/server.log" || fail "/tele is not GM-only"
grep -q "Command - '&tc' .* required plvl:2" "$T/server.log" || fail "/tc is not GM-only"
grep -q "Command - '&spawn' .* required plvl:1" "$T/server.log" || fail "/spawn should stay open to players"
echo "ok - single-player teleports are GM-only, companions stay open"
[[ "$(docker exec "$NAME" sqlite3 /data/world/opendaoc.sqlite3.db "SELECT Value FROM ServerProperty WHERE \`Key\`='disabled_classes'")" == "33;34;39;58-62" ]] \
    || fail "Disciple (20) is still disabled"
[[ "$(docker exec "$NAME" sqlite3 /data/world/opendaoc.sqlite3.db "SELECT COUNT(*) FROM StartupLocation WHERE ClassID=20 AND RaceID=4")" == 1 ]] \
    || fail "Saracen Disciples have no starting location"
echo "ok - Disciple enabled and Saracen Disciples have a starting location"
docker exec "$NAME" python3 /app/tools/accounts/accounts.py --db "$db" create smoketest Sm0keTest >/dev/null || fail "account create"
docker stop -t 120 "$NAME" >/dev/null
logs_have "| DOL.GS.GameServer | Stopped" || fail "no clean save on docker stop"
echo "ok - docker stop saves and stops cleanly"
docker rm "$NAME" >/dev/null
run; wait_listen || fail "restart failed"
docker exec "$NAME" test -L /app/server/realm-event-records.sqlite3 || fail "realm-event ledger not linked after restart"
docker exec "$NAME" sqlite3 /data/state/realm-event-records.sqlite3 .tables | grep -q smoke_probe || fail "realm-event ledger not kept across restart"
echo "ok - realm-event ledger kept across restart"
docker exec "$NAME" python3 /app/tools/accounts/accounts.py --db "$db" list | grep -q smoketest || fail "data lost on restart"
echo "ok - restart keeps the data"
docker rm -f "$NAME" >/dev/null

docker run --rm -v "$VOL":/data --entrypoint sh "$IMAGE" -c 'echo "{ not json" > /data/bot-goals.json'
run
for _ in $(seq 1 90); do [[ "$(docker inspect -f '{{.State.Running}}' "$NAME")" == true ]] || break; sleep 2; done
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 70 ]] || fail "a failed server start did not stop the container"
logs_have "The server failed to start" || fail "failed-start message missing"
echo "ok - a server that fails to start stops the container with a clear message"
docker rm -f "$NAME" >/dev/null
docker run --rm -v "$VOL":/data --entrypoint rm "$IMAGE" -f /data/bot-goals.json

run b; sleep 5
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 3 ]] || fail "edition change not refused"
logs_have "hdc new-world" || fail "edition message missing"
echo "ok - changing the edition of an existing world is refused"
docker rm -f "$NAME" >/dev/null

python3 -c "import socket,time; s=socket.socket(); s.bind(('0.0.0.0', $PORT)); s.listen(); time.sleep(30)" & holder=$!
sleep 1; run; sleep 5; kill "$holder" 2>/dev/null || true
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 65 ]] || fail "busy port not detected"
logs_have "already in use" || fail "port message missing"
echo "ok - a busy port is refused with a clear message"
docker rm -f "$NAME" >/dev/null

docker volume create "$VOL-ro" >/dev/null
# Non-empty on purpose: Docker gives an empty volume the image's /data ownership again on every mount.
docker run --rm -v "$VOL-ro":/data --user 0 --entrypoint sh "$IMAGE" -c 'touch /data/.root-owned && chown -R 0:0 /data && chmod 755 /data'
docker run -d --name "$NAME" --network host -v "$VOL-ro":/data -e HEARTHDAOC_PORT="$PORT" -e HEARTHDAOC_SKIP_NAVMESH=1 "$IMAGE" >/dev/null; sleep 3
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 64 ]] || fail "unwritable volume not detected"
logs_have "chown" && logs_have "touch /data/.owner" || fail "chown hint missing or incomplete"
echo "ok - an unwritable volume is refused with the chown fix"
docker rm -f "$NAME" >/dev/null
docker run -d --name "$NAME" --network host --user 1234:1234 -v "$VOL-uid":/data -e HEARTHDAOC_PORT="$PORT" \
    -e HEARTHDAOC_UDP_PORT="$UDP" -e HEARTHDAOC_SKIP_NAVMESH=1 "$IMAGE" >/dev/null
wait_listen || fail "a non-default uid cannot use a fresh volume"
echo "ok - a non-default uid works on a fresh volume"
echo "SMOKE OK"
