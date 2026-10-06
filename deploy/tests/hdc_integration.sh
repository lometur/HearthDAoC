#!/usr/bin/env bash
# Compose + hdc integration test on a separate project, volume and ports. Usage: hdc_integration.sh <image>
set -euo pipefail
IMAGE="${1:?usage: $0 <image e.g. hearthdaoc:dev>}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
W="$(mktemp -d)"
cp "$HERE/../compose.yml" "$HERE/../hdc" "$W/"
cat > "$W/.env" <<EOF
HEARTHDAOC_IMAGE=${IMAGE%%:*}
HEARTHDAOC_TAG=${IMAGE##*:}
HEARTHDAOC_EDITION=classic
HEARTHDAOC_AUTO_ACCOUNTS=True
HEARTHDAOC_PORT=10392
HEARTHDAOC_UDP_PORT=10492
HEARTHDAOC_PROJECT=hearthdaoc-it
HEARTHDAOC_CONTAINER=hearthdaoc-it-server
HEARTHDAOC_VOLUME=hearthdaoc-it-data
HEARTHDAOC_SKIP_NAVMESH=1
HEARTHDAOC_MEM_LIMIT=4g
HEARTHDAOC_CPUS=2
EOF
hdc() { "$W/hdc" "$@"; }
cleanup() { hdc down >/dev/null 2>&1 || true; docker volume rm -f hearthdaoc-it-data >/dev/null 2>&1 || true; rm -rf "$W"; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; docker logs hearthdaoc-it-server 2>&1 | tail -30 >&2 || true; exit 1; }
healthy() { for _ in $(seq 1 150); do [[ "$(docker inspect -f '{{.State.Health.Status}}' hearthdaoc-it-server 2>/dev/null)" == healthy ]] && return 0; sleep 2; done; return 1; }

hdc up >/dev/null; healthy || fail "not healthy"
echo "ok - up and healthy"
[[ "$(docker inspect -f '{{.HostConfig.Memory}}' hearthdaoc-it-server)" == 4294967296 ]] || fail "memory limit not applied"
docker inspect -f '{{.HostConfig.CapDrop}}' hearthdaoc-it-server | grep -q ALL || fail "capabilities not dropped"
[[ "$(docker inspect -f '{{.HostConfig.NetworkMode}}' hearthdaoc-it-server)" == host ]] || fail "not host networking"
[[ -n "$(docker inspect -f '{{index .HostConfig.LogConfig.Config "max-size"}}' hearthdaoc-it-server)" ]] || fail "docker logs not rotated"
echo "ok - isolation settings applied"
hdc status | grep -q "edition .* classic" || fail "status"
hdc account create Tester1 pw1 >/dev/null || fail "account create"
hdc account list | grep -q Tester1 || fail "account list"
hdc add-bots hib 1 1 | grep -q . || fail "add-bots"
hdc backup | grep -q "/data/backups/world-" || fail "backup"
echo "ok - status, accounts, add-bots and backup while running"
if hdc bot-goals set 50 10 30 60 2>/dev/null; then fail "bot-goals write allowed while running"; fi
if hdc restore x.db 2>/dev/null; then fail "restore allowed while running"; fi
if hdc spawns restore 2>/dev/null; then fail "spawns restore allowed while running"; fi
echo "ok - stopped-only commands refuse while running"
hdc stop >/dev/null
docker logs hearthdaoc-it-server > "$W/stop.log" 2>&1; grep -q "| DOL.GS.GameServer | Stopped" "$W/stop.log" || fail "no clean save"
hdc bot-goals set 50 10 30 60 | grep -q "Saved" || fail "bot-goals set while stopped"
hdc account plvl Tester1 3 | grep -q "plvl 3" || fail "plvl while stopped"
latest="$(hdc backups | awk '/-backup.db/ {print $NF}' | tail -1)"
hdc restore "$latest" | grep -q "Restored" || fail "restore"
echo "ok - stopped-only commands work when stopped"
sed -i 's/^HEARTHDAOC_EDITION=classic/HEARTHDAOC_EDITION=b/' "$W/.env"
if out="$(hdc up 2>&1)"; then fail "hdc up did not report the refused start"; fi
grep -qi "edition" <<<"$out" || fail "hdc up does not explain the refusal: $out"
# Docker resets ExitCode whenever the restart policy starts the container again: catch a restart window.
refused=""
for _ in $(seq 1 60); do
    read -r restarting code <<<"$(docker inspect -f '{{.State.Restarting}} {{.State.ExitCode}}' hearthdaoc-it-server)"
    if [[ "$restarting" == true && "$code" == 3 ]]; then refused=1; break; fi
    sleep 0.5
done
[[ -n "$refused" ]] || fail "edition change not refused (exit code 3)"
grep -qi "edition" <<<"$(hdc status)" || fail "hdc status does not explain the refusal"
hdc stop >/dev/null 2>&1 || true
sed -i 's/^HEARTHDAOC_EDITION=b/HEARTHDAOC_EDITION=classic/' "$W/.env"
echo "ok - edition change refused through compose"
hdc spawns restore --max-level 20 | grep -q "Restored" || fail "spawns restore while stopped"
hdc spawns status | grep -qE "^enabled +True" || fail "spawns status after restore"
echo "ok - spawns restore works when stopped"
hdc auto-accounts off >/dev/null; healthy || fail "not healthy after auto-accounts off"
docker exec hearthdaoc-it-server grep -q "<AutoAccountCreation>False</AutoAccountCreation>" /app/server/config/serverconfig.xml || fail "auto-accounts not off"
echo "ok - auto-accounts off recreates the server with the new setting"
hdc spawns status | grep -qE "^restored +[1-9]" || fail "restored spawns missing after restart"
echo "ok - the server starts healthy with the restored spawns"
# A crash: kill the game server inside the container (docker kill would count as a manual stop).
docker exec hearthdaoc-it-server sh -c 'for p in /proc/[0-9]*; do grep -qa "CoreServer[.]dll" "$p/cmdline" 2>/dev/null && kill -9 "${p#/proc/}"; done; true'
for _ in $(seq 1 30); do [[ "$(docker inspect -f '{{.RestartCount}}' hearthdaoc-it-server)" -gt 0 ]] && break; sleep 1; done
healthy || fail "not healthy after an automatic restart"
hdc stop >/dev/null
grep -q "^Server: stopped" <<<"$(hdc status)" || fail "a stopped server is reported as not starting after an earlier crash"
echo "ok - status is right for a server stopped after an earlier crash"
echo "HDC INTEGRATION OK"
