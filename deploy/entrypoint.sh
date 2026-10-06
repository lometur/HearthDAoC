#!/usr/bin/env bash
# Container entrypoint: check /data, create or check the world, generate the config, link the
# server's state files into /data, start the server with a console pipe, and turn docker stop
# (SIGTERM) into the server's own "exit" command so the world is saved.
# Exit codes: 2 download failed, 3 edition mismatch, 4 version mismatch, 64 /data not writable,
# 65 port in use, 70 server failed to start; otherwise the server's own exit code.
set -euo pipefail

DATA=/data
SRV=/app/server
BIN=/app/bin
LOCK=/app/upstream.lock
EDITION="${HEARTHDAOC_EDITION:-classic}"
LISTEN_IP="${HEARTHDAOC_LISTEN_IP:-0.0.0.0}"
PORT="${HEARTHDAOC_PORT:-10301}"

# init_world.py checks that /data is writable first and prints the exact fix (exit 64).
init_args=(--lock "$LOCK" --data "$DATA" --edition "$EDITION")
[[ -n "${HEARTHDAOC_SKIP_NAVMESH:-}" ]] && init_args+=(--skip-navmesh)
python3 "$BIN/init_world.py" "${init_args[@]}"

python3 "$BIN/gen_config.py" --data "$DATA" --out "$SRV/config/serverconfig.xml"
# Clients get their UDP port from the world's Regions table, which upstream ships as 10400.
python3 "$BIN/region_ports.py" --db "$DATA/world/opendaoc.sqlite3.db" --port "${HEARTHDAOC_UDP_PORT:-10401}"

mkdir -p "$DATA/logs" "$DATA/state" "$DATA/backups" "$DATA/navmesh"
link() {  # link <path in /app/server> <target in /data>
    [[ -e "$1" && ! -L "$1" ]] && rm -rf "$1"
    ln -sfn "$2" "$1"
}
link "$SRV/navmesh" "$DATA/navmesh"
link "$SRV/logs" "$DATA/logs"
# .NET treats a dangling link as an existing file, so link only to files that exist. The server
# creates its realm-event ledger itself (with its tables); keep_ledger moves it into /data on exit.
LEDGER=realm-event-records.sqlite3
if [[ -f "$DATA/state/$LEDGER" && ! -s "$DATA/state/$LEDGER" ]]; then
    rm -f "$DATA/state/$LEDGER"   # empty file left by an earlier image: it broke the ledger
fi
if [[ -f "$DATA/state/$LEDGER" ]]; then
    link "$SRV/$LEDGER" "$DATA/state/$LEDGER"
else
    rm -f "$SRV/$LEDGER"
fi
keep_ledger() {
    if [[ -f "$SRV/$LEDGER" && ! -L "$SRV/$LEDGER" ]]; then
        mv -f "$SRV/$LEDGER" "$DATA/state/$LEDGER"
        for suffix in -wal -journal; do
            if [[ -e "$SRV/$LEDGER$suffix" ]]; then mv -f "$SRV/$LEDGER$suffix" "$DATA/state/$LEDGER$suffix"; fi
        done
    fi
}
if [[ -f "$DATA/bot-goals.json" ]]; then
    link "$SRV/bot-goals.json" "$DATA/bot-goals.json"
else
    rm -f "$SRV/bot-goals.json"   # no file: the server uses upstream's default bot goals
fi

if ! python3 - "$LISTEN_IP" "$PORT" <<'EOF'
import socket, sys
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
try:
    s.bind((sys.argv[1], int(sys.argv[2])))
except OSError:
    sys.exit(1)
EOF
then
    echo "ERROR: TCP port $PORT is already in use on this host. Choose another HEARTHDAOC_PORT in .env (OpenDAoC uses 10300)." >&2
    exit 65
fi

python3 "$BIN/backup.py" --data "$DATA" loop --keep "${HEARTHDAOC_BACKUP_KEEP:-7}" &
backup_pid=$!

fifo=/tmp/hearthdaoc-console
rm -f "$fifo"
mkfifo "$fifo"
exec 3<>"$fifo"   # keep a writer open: the server's console loop busy-spins if stdin reaches EOF

# After "Failed to start the server" the process keeps running without listening; stop it and
# exit with code 70 instead, so the failure shows in docker ps / hdc status rather than hanging.
failed=/tmp/hearthdaoc-start-failed
rm -f "$failed"
touch "$DATA/logs/server.log"
# (grep reads tail through process substitution: in a pipeline under pipefail, tail's SIGPIPE
# after the match would make the result a failure.)
( if grep -m1 -q "Failed to start the server" < <(tail -n0 -F "$DATA/logs/server.log" 2>/dev/null); then
      touch "$failed"
      echo exit >&3
  fi ) &
watch_pid=$!

cd "$SRV"
dotnet CoreServer.dll <&3 &
server_pid=$!

stopping=0
stop() {
    if (( ! stopping )); then
        stopping=1
        echo "Saving the world and stopping the server..."
        echo exit >&3
    fi
}
trap stop TERM INT

rc=0
while kill -0 "$server_pid" 2>/dev/null; do
    if wait "$server_pid"; then rc=0; else rc=$?; fi
done
kill "$backup_pid" "$watch_pid" 2>/dev/null || true
keep_ledger
if [[ -e "$failed" ]]; then
    echo "ERROR: The server failed to start; the error is above and in /data/logs/server.log." >&2
    exit 70
fi
exit "$rc"
