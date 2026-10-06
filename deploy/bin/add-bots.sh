#!/usr/bin/env bash
# Inside the container: back up, then add autonomous bots like the launcher's buttons.
# Usage: add-bots.sh <alb|mid|hib> <1|10|100> <1|50>
set -euo pipefail
case "${1:-}" in
    alb|albion|1) realm=1 ;;
    mid|midgard|2) realm=2 ;;
    hib|hibernia|3) realm=3 ;;
    *) echo "usage: add-bots <alb|mid|hib> <1|10|100> <1|50>" >&2; exit 2 ;;
esac
count="${2:?count: 1, 10 or 100}"
level="${3:?level: 1 or 50}"
python3 /app/bin/backup.py --data /data create --label pre-bots >/dev/null
dotnet /app/tools/offline-bots/offline-bots.dll add /data/world/opendaoc.sqlite3.db "$realm" "${count}x${level}"
