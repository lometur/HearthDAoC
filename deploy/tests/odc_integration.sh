#!/usr/bin/env bash
# Compose + odc integration test on a separate project, volume and ports. Usage: odc_integration.sh <image>
set -euo pipefail
IMAGE="${1:?usage: $0 <image e.g. offlinedaoc:dev>}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
W="$(mktemp -d)"
cp "$HERE/../compose.yml" "$HERE/../odc" "$W/"
cat > "$W/.env" <<EOF
OFFLINEDAOC_IMAGE=${IMAGE%%:*}
OFFLINEDAOC_TAG=${IMAGE##*:}
OFFLINEDAOC_EDITION=classic
OFFLINEDAOC_PORT=10392
OFFLINEDAOC_UDP_PORT=10492
OFFLINEDAOC_PROJECT=offlinedaoc-it
OFFLINEDAOC_CONTAINER=offlinedaoc-it-server
OFFLINEDAOC_VOLUME=offlinedaoc-it-data
OFFLINEDAOC_SKIP_NAVMESH=1
OFFLINEDAOC_MEM_LIMIT=4g
OFFLINEDAOC_CPUS=2
EOF
odc() { "$W/odc" "$@"; }
cleanup() { odc down >/dev/null 2>&1 || true; docker volume rm -f offlinedaoc-it-data >/dev/null 2>&1 || true; rm -rf "$W"; }
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; docker logs offlinedaoc-it-server 2>&1 | tail -30 >&2 || true; exit 1; }
healthy() { for _ in $(seq 1 150); do [[ "$(docker inspect -f '{{.State.Health.Status}}' offlinedaoc-it-server 2>/dev/null)" == healthy ]] && return 0; sleep 2; done; return 1; }

odc up >/dev/null; healthy || fail "not healthy"
echo "ok - up and healthy"
[[ "$(docker inspect -f '{{.HostConfig.Memory}}' offlinedaoc-it-server)" == 4294967296 ]] || fail "memory limit not applied"
docker inspect -f '{{.HostConfig.CapDrop}}' offlinedaoc-it-server | grep -q ALL || fail "capabilities not dropped"
[[ "$(docker inspect -f '{{.HostConfig.NetworkMode}}' offlinedaoc-it-server)" == host ]] || fail "not host networking"
echo "ok - isolation settings applied"
odc status | grep -q "edition .* classic" || fail "status"
odc account create Tester1 pw1 >/dev/null || fail "account create"
odc account list | grep -q Tester1 || fail "account list"
odc add-bots hib 1 1 | grep -q . || fail "add-bots"
odc backup | grep -q "/data/backups/world-" || fail "backup"
echo "ok - status, accounts, add-bots and backup while running"
if odc bot-goals set 50 10 30 60 2>/dev/null; then fail "bot-goals write allowed while running"; fi
if odc restore x.db 2>/dev/null; then fail "restore allowed while running"; fi
if odc spawns restore 2>/dev/null; then fail "spawns restore allowed while running"; fi
echo "ok - stopped-only commands refuse while running"
odc stop >/dev/null
docker logs offlinedaoc-it-server 2>&1 | grep -q "| DOL.GS.GameServer | Stopped" || fail "no clean save"
odc bot-goals set 50 10 30 60 | grep -q "Saved" || fail "bot-goals set while stopped"
odc account plvl Tester1 3 | grep -q "plvl 3" || fail "plvl while stopped"
latest="$(odc backups | awk '/-backup.db/ {print $NF}' | tail -1)"
odc restore "$latest" | grep -q "Restored" || fail "restore"
echo "ok - stopped-only commands work when stopped"
sed -i 's/^OFFLINEDAOC_EDITION=classic/OFFLINEDAOC_EDITION=b/' "$W/.env"
odc up >/dev/null 2>&1 || true; sleep 6
[[ "$(docker inspect -f '{{.State.ExitCode}}' offlinedaoc-it-server)" == 3 ]] || fail "edition change not refused"
odc stop >/dev/null 2>&1 || true
sed -i 's/^OFFLINEDAOC_EDITION=b/OFFLINEDAOC_EDITION=classic/' "$W/.env"
echo "ok - edition change refused through compose"
odc spawns restore --max-level 20 | grep -q "Restored" || fail "spawns restore while stopped"
odc spawns status | grep -qE "^enabled +True" || fail "spawns status after restore"
echo "ok - spawns restore works when stopped"
odc auto-accounts off >/dev/null; healthy || fail "not healthy after auto-accounts off"
docker exec offlinedaoc-it-server grep -q "<AutoAccountCreation>False</AutoAccountCreation>" /app/server/config/serverconfig.xml || fail "auto-accounts not off"
echo "ok - auto-accounts off recreates the server with the new setting"
odc spawns status | grep -qE "^restored +[1-9]" || fail "restored spawns missing after restart"
echo "ok - the server starts healthy with the restored spawns"
echo "ODC INTEGRATION OK"
