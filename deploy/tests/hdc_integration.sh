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
HEARTHDAOC_AUTOSAVE_MINUTES=7
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
# Checks grep hdc's whole output (grep -q X <<<"$(hdc ...)"), not a pipe: grep -q stops reading at its first
# match, a later write by hdc then meets a closed pipe (SIGPIPE), and pipefail turns that into a failure.
cleanup() { hdc down >/dev/null 2>&1 || true; docker volume rm -f hearthdaoc-it-data >/dev/null 2>&1 || true; docker rmi "${IMAGE%%:*}:it-update" "${IMAGE%%:*}:it-update2" "${IMAGE%%:*}:it-update3" >/dev/null 2>&1 || true; rm -rf "$W"; }
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
docker exec hearthdaoc-it-server grep -q "<DBAutosaveInterval>7</DBAutosaveInterval>" /app/server/config/serverconfig.xml \
    || fail "HEARTHDAOC_AUTOSAVE_MINUTES from .env did not reach the server config"
echo "ok - autosave interval comes from .env"
out="$(hdc status 2>&1)" || fail "status exited $?: $out"
grep -q "edition .* classic" <<<"$out" || fail "status: $out"
hdc account create Tester1 pw1 >/dev/null || fail "account create"
grep -q Tester1 <<<"$(hdc account list)" || fail "account list"
grep -q . <<<"$(hdc add-bots hib 1 1)" || fail "add-bots"
grep -q "/data/backups/world-.*-manual.db" <<<"$(hdc backup)" || fail "hdc backup should make a 'manual' backup (not counted in the daily ones)"
echo "ok - status, accounts, add-bots and backup while running"
if hdc bot-goals set 50 10 30 60 2>/dev/null; then fail "bot-goals write allowed while running"; fi
if hdc restore x.db 2>/dev/null; then fail "restore allowed while running"; fi
if hdc spawns restore 2>/dev/null; then fail "spawns restore allowed while running"; fi
echo "ok - stopped-only commands refuse while running"
hdc stop >/dev/null
docker logs hearthdaoc-it-server > "$W/stop.log" 2>&1; grep -q "| DOL.GS.GameServer | Stopped" "$W/stop.log" || fail "no clean save"
grep -q "Saved" <<<"$(hdc bot-goals set 50 10 30 60)" || fail "bot-goals set while stopped"
grep -q "plvl 3" <<<"$(hdc account plvl Tester1 3)" || fail "plvl while stopped"
latest="$(hdc backups | awk '/-manual.db/ {print $NF}' | tail -1)"
grep -q "Restored" <<<"$(hdc restore "$latest")" || fail "restore"
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
grep -q "Restored" <<<"$(hdc spawns restore --max-level 20)" || fail "spawns restore while stopped"
grep -qE "^enabled +True" <<<"$(hdc spawns status)" || fail "spawns status after restore"
echo "ok - spawns restore works when stopped"
hdc auto-accounts off >/dev/null; healthy || fail "not healthy after auto-accounts off"
docker exec hearthdaoc-it-server grep -q "<AutoAccountCreation>False</AutoAccountCreation>" /app/server/config/serverconfig.xml || fail "auto-accounts not off"
echo "ok - auto-accounts off recreates the server with the new setting"
grep -qE "^restored +[1-9]" <<<"$(hdc spawns status)" || fail "restored spawns missing after restart"
echo "ok - the server starts healthy with the restored spawns"
# A crash: kill the game server inside the container (docker kill would count as a manual stop).
docker exec hearthdaoc-it-server sh -c 'for p in /proc/[0-9]*; do grep -qa "CoreServer[.]dll" "$p/cmdline" 2>/dev/null && kill -9 "${p#/proc/}"; done; true'
for _ in $(seq 1 30); do [[ "$(docker inspect -f '{{.RestartCount}}' hearthdaoc-it-server)" -gt 0 ]] && break; sleep 1; done
healthy || fail "not healthy after an automatic restart"
hdc stop >/dev/null
grep -q "^Server: stopped" <<<"$(hdc status)" || fail "a stopped server is reported as not starting after an earlier crash"
echo "ok - status is right for a server stopped after an earlier crash"

# hdc update: a release bundle built like CI builds it, for a tag that exists as a local image.
make_bundle() {  # make_bundle <tag> <upstream version>: build, then add a new setting and markers
    docker tag "$IMAGE" "${IMAGE%%:*}:$1"
    "$HERE/../build_bundles.sh" "$1" "$W/b-$1" --deploy-only >/dev/null
    mkdir "$W/b-$1/x" && tar xzf "$W/b-$1/hearthdaoc-deploy-$1.tar.gz" -C "$W/b-$1/x"
    echo "HEARTHDAOC_IT_NEW_SETTING=hello" >> "$W/b-$1/x/.env.example"
    echo "# bundle $1" >> "$W/b-$1/x/compose.yml"
    sed -i "s/\"version\": *\"[^\"]*\"/\"version\": \"$2\"/" "$W/b-$1/x/upstream.lock"
    tar czf "$W/b-$1/hearthdaoc-deploy-$1.tar.gz" -C "$W/b-$1/x" .
    echo "$W/b-$1/hearthdaoc-deploy-$1.tar.gz"
}
upstream="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["version"])' "$HERE/../upstream.lock")"
hdc up >/dev/null; healthy || fail "not healthy before update"
out="$(hdc update --bundle "$(make_bundle it-update "$upstream")")" || fail "hdc update failed: $out"
grep -q "^HEARTHDAOC_TAG=it-update$" "$W/.env" || fail "update did not set the tag"
grep -q "^HEARTHDAOC_IT_NEW_SETTING=hello$" "$W/.env" || fail "update did not add the new setting"
grep -q "^HEARTHDAOC_PORT=10392$" "$W/.env" || fail "update lost the owner's settings"
grep -q "# bundle it-update" "$W/compose.yml" || fail "update did not install the new compose.yml"
grep -q -- "-pre-update.db" <<<"$(hdc backups)" || fail "update made no backup first"
healthy || fail "not healthy after update"
[[ "$(docker inspect -f '{{.Config.Image}}' hearthdaoc-it-server)" == "${IMAGE%%:*}:it-update" ]] || fail "server not running the new image"
echo "ok - hdc update backs up, installs the release, keeps settings and restarts"
# A release for another upstream version makes hdc update upgrade the world first. Here the upgrade fails (the
# image is still for the world's version), so the world must stay as it was and the server stopped.
world_version() { docker run --rm -v hearthdaoc-it-data:/data --entrypoint cat "$IMAGE" /data/world.json \
    | python3 -c 'import json, sys; print(json.load(sys.stdin)["version"])'; }
if out="$(hdc update --bundle "$(make_bundle it-update2 9.99z)" 2>&1)"; then fail "a failed world upgrade should stop hdc update"; fi
grep -q "The world is as it was" <<<"$out" && grep -q "./hdc upgrade-world" <<<"$out" && grep -q "./hdc update it-update" <<<"$out" \
    || fail "no retry or go-back instruction: $out"
if docker inspect -f '{{.State.Running}}' hearthdaoc-it-server 2>/dev/null | grep -q true; then fail "server started on a world from another upstream version"; fi
[[ "$(world_version)" == "$upstream" ]] || fail "a failed world upgrade changed the world"
echo "ok - a failed world upgrade in hdc update leaves the world as it was and the server stopped"
out="$(hdc update --bundle "$W/b-it-update/hearthdaoc-deploy-it-update.tar.gz")" || fail "going back to the previous release failed: $out"
healthy || fail "not healthy after going back to the previous release"
echo "ok - hdc update goes back to the previous release"
# A world from an older upstream version: hdc update upgrades it (as ./hdc upgrade-world does), then starts.
hdc stop >/dev/null
docker run --rm -v hearthdaoc-it-data:/data --entrypoint sh "$IMAGE" \
    -c 'sed -i "s/\"version\": *\"[^\"]*\"/\"version\": \"0.0it\"/" /data/world.json'
[[ "$(world_version)" == 0.0it ]] || fail "could not make an older world"
out="$(hdc update --bundle "$(make_bundle it-update3 "$upstream")")" || fail "hdc update with a world upgrade failed: $out"
grep -q "^Upgrade report (server settings to re-check): docker exec hearthdaoc-it-server cat /data/archive/world-pre-upgrade-.*/upgrade-report.txt$" <<<"$out" \
    || fail "no upgrade report location: $out"
[[ "$(world_version)" == "$upstream" ]] || fail "the world was not upgraded"
healthy || fail "not healthy after the world upgrade"
grep -q Tester1 <<<"$(hdc account list)" || fail "accounts lost in the world upgrade"
grep -q -- "-pre-upgrade.db" <<<"$(hdc backups)" || fail "the world upgrade made no backup"
echo "ok - hdc update upgrades a world from another upstream version, then starts"
python3 - "$W" <<'PY' &
import http.server, sys, os
class H(http.server.BaseHTTPRequestHandler):
    def do_HEAD(self):
        self.send_response(302); self.send_header("Location", "https://example.invalid/releases/tag/v0.34b-hearth.99"); self.end_headers()
    do_GET = do_HEAD
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", 0), H); open(os.path.join(sys.argv[1], "port"), "w").write(str(s.server_port)); s.serve_forever()
PY
srv=$!; for _ in $(seq 1 20); do [[ -s "$W/port" ]] && break; sleep 0.2; done
out="$(HEARTHDAOC_RELEASES_URL="http://127.0.0.1:$(cat "$W/port")" hdc update --check)"; kill "$srv"
grep -q "v0.34b-hearth.99" <<<"$out" && grep -q "./hdc update v0.34b-hearth.99" <<<"$out" || fail "update --check: $out"
echo "ok - hdc update --check reports a newer release"
echo "HDC INTEGRATION OK"
