#!/usr/bin/env bash
# Container smoke test (world database only, test ports). Usage: smoke.sh <image>
set -euo pipefail
IMAGE="${1:?usage: $0 <image>}"
NAME="offlinedaoc-smoke-$$"; VOL="offlinedaoc-smoke-$$"; PORT="${SMOKE_PORT:-10391}"; UDP="${SMOKE_UDP:-10491}"
cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; docker volume rm -f "$VOL" "$VOL-ro" >/dev/null 2>&1 || true; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; docker logs "$NAME" 2>&1 | tail -40 >&2 || true; exit 1; }
run() { docker run -d --name "$NAME" --network host -v "$VOL":/data -e OFFLINEDAOC_EDITION="${1:-classic}" \
            -e OFFLINEDAOC_PORT="$PORT" -e OFFLINEDAOC_UDP_PORT="$UDP" -e OFFLINEDAOC_SKIP_NAVMESH=1 "$IMAGE" >/dev/null; }
wait_listen() {
    for _ in $(seq 1 300); do
        docker logs "$NAME" 2>&1 | grep -q "Server is now listening for incoming connections on 0.0.0.0:$PORT" && return 0
        [[ "$(docker inspect -f '{{.State.Running}}' "$NAME")" == true ]] || return 1
        sleep 2
    done
    return 1
}
db=/data/world/opendaoc.sqlite3.db

run; wait_listen || fail "server did not start"
echo "ok - server listens on $PORT"
docker exec "$NAME" grep -q "<Port>$PORT</Port>" /app/server/config/serverconfig.xml || fail "port not in config"
docker exec "$NAME" grep -q "<EnableUPnP>False</EnableUPnP>" /app/server/config/serverconfig.xml || fail "UPnP not off"
docker exec "$NAME" test -f /app/server/config/logconfig.xml || fail "logconfig.xml missing"
echo "ok - generated config and release config files present"
docker exec "$NAME" python3 /app/tools/accounts/accounts.py --db "$db" create smoketest Sm0keTest >/dev/null || fail "account create"
docker stop -t 120 "$NAME" >/dev/null
docker logs "$NAME" 2>&1 | grep -q "| DOL.GS.GameServer | Stopped" || fail "no clean save on docker stop"
echo "ok - docker stop saves and stops cleanly"
docker rm "$NAME" >/dev/null
run; wait_listen || fail "restart failed"
docker exec "$NAME" python3 /app/tools/accounts/accounts.py --db "$db" list | grep -q smoketest || fail "data lost on restart"
echo "ok - restart keeps the data"
docker rm -f "$NAME" >/dev/null

docker run --rm -v "$VOL":/data --entrypoint sh "$IMAGE" -c 'echo "{ not json" > /data/bot-goals.json'
run
for _ in $(seq 1 90); do [[ "$(docker inspect -f '{{.State.Running}}' "$NAME")" == true ]] || break; sleep 2; done
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 70 ]] || fail "a failed server start did not stop the container"
docker logs "$NAME" 2>&1 | grep -q "The server failed to start" || fail "failed-start message missing"
echo "ok - a server that fails to start stops the container with a clear message"
docker rm -f "$NAME" >/dev/null
docker run --rm -v "$VOL":/data --entrypoint rm "$IMAGE" -f /data/bot-goals.json

run b; sleep 5
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 3 ]] || fail "edition change not refused"
docker logs "$NAME" 2>&1 | grep -q "odc new-world" || fail "edition message missing"
echo "ok - changing the edition of an existing world is refused"
docker rm -f "$NAME" >/dev/null

python3 -c "import socket,time; s=socket.socket(); s.bind(('0.0.0.0', $PORT)); s.listen(); time.sleep(30)" & holder=$!
sleep 1; run; sleep 5; kill "$holder" 2>/dev/null || true
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 65 ]] || fail "busy port not detected"
docker logs "$NAME" 2>&1 | grep -q "already in use" || fail "port message missing"
echo "ok - a busy port is refused with a clear message"
docker rm -f "$NAME" >/dev/null

docker volume create "$VOL-ro" >/dev/null
# Non-empty on purpose: Docker gives an empty volume the image's /data ownership again on every mount.
docker run --rm -v "$VOL-ro":/data --user 0 --entrypoint sh "$IMAGE" -c 'touch /data/.root-owned && chown -R 0:0 /data && chmod 755 /data'
docker run -d --name "$NAME" --network host -v "$VOL-ro":/data -e OFFLINEDAOC_PORT="$PORT" -e OFFLINEDAOC_SKIP_NAVMESH=1 "$IMAGE" >/dev/null; sleep 3
[[ "$(docker inspect -f '{{.State.ExitCode}}' "$NAME")" == 64 ]] || fail "unwritable volume not detected"
docker logs "$NAME" 2>&1 | grep -q "chown" || fail "chown hint missing"
echo "ok - an unwritable volume is refused with the chown fix"
echo "SMOKE OK"
