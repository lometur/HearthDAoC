#!/usr/bin/env bash
# Set up a HearthDAoC client: OfflineDAoC's client files for a HearthDAoC server (Linux, Steam/Proton).
set -euo pipefail

usage() {
    cat <<'EOF'
usage: setup.sh --server HOST:PORT --edition classic|b --base-client DIR [--dest DIR]

  --server       the central server, e.g. 192.168.1.64:10301 (ask the server owner)
  --edition      classic or b; must match the server (ask the server owner)
  --base-client  your 1.127 client folder (e.g. from the OpenDAoC installer); only read
  --dest         where to create the OfflineDAoC client (default ~/Games/HearthDAoC)
EOF
}

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER="" EDITION="" BASE="" DEST="$HOME/Games/HearthDAoC" LOCK=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --server) SERVER="${2:-}"; shift 2 ;;
        --edition) EDITION="${2:-}"; shift 2 ;;
        --base-client) BASE="${2:-}"; shift 2 ;;
        --dest) DEST="${2:-}"; shift 2 ;;
        --lock) LOCK="${2:-}"; shift 2 ;;   # advanced/testing: alternative upstream.lock
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
[[ -n "$SERVER" && -n "$EDITION" && -n "$BASE" ]] || { usage >&2; exit 2; }
[[ "$SERVER" =~ ^[A-Za-z0-9.-]+:[0-9]+$ ]] || { echo "--server must look like 192.168.1.64:10301" >&2; exit 2; }
[[ "$EDITION" == classic || "$EDITION" == b ]] || { echo "--edition must be classic or b" >&2; exit 2; }
[[ -f "$BASE/connect.exe" && -f "$BASE/game1127.dll" ]] || {
    echo "$BASE doesn't look like a 1.127 client (connect.exe and game1127.dll not found)." >&2; exit 2; }

find_file() {  # find_file <bundle name> <repo path>
    for p in "$here/$1" "$here/../../$2"; do [[ -f "$p" ]] && { echo "$p"; return; }; done
    echo "Missing $1 next to setup.sh; download the full client bundle." >&2; exit 1
}
FETCH="$(find_file odaoc_fetch.py tools/linux/odaoc_fetch.py)"
[[ -n "$LOCK" ]] || LOCK="$(find_file upstream.lock deploy/upstream.lock)"
TEMPLATE="$(find_file play.sh.in client/linux/play.sh.in)"
# The client patches (client/patches): installed in $DEST/patches, where play.sh applies them at every launch.
PATCH_FILES=(apply_patches.py patchset.py classic-creation.json splash.mpk)
PATCH_SRC="$(dirname "$(find_file patches/apply_patches.py client/patches/apply_patches.py)")"
for f in "${PATCH_FILES[@]}"; do
    [[ -f "$PATCH_SRC/$f" ]] || { echo "Missing patches/$f next to setup.sh; download the full client bundle." >&2; exit 1; }
done
for t in python3 rsync; do command -v "$t" >/dev/null || { echo "Please install $t first." >&2; exit 1; }; done

mkdir -p "$DEST"
echo "Copying your base client (read only) to $DEST/client ..."
rsync -a --delete --exclude='*.dxvk-cache' --exclude='/logs/' --exclude='/login.log' "$BASE/" "$DEST/client/"
echo "Fetching the OfflineDAoC $EDITION client files (each verified) ..."
python3 "$FETCH" --lock "$LOCK" client --edition "$EDITION" --client-dir "$DEST/client"
# Classic character creation and the HearthDAoC splash. A fresh copy of this bundle's patch files
# replaces $DEST/patches (nothing from an older release is left), and is applied from there. Exit 3
# means the applier refused a client file it doesn't know (e.g. the b edition) and changed nothing:
# the client works.
echo "Applying HearthDAoC's client patches (classic character creation, loading splash) ..."
rm -rf "$DEST/patches.new"
mkdir "$DEST/patches.new"
for f in "${PATCH_FILES[@]}"; do cp "$PATCH_SRC/$f" "$DEST/patches.new/"; done
rm -rf "$DEST/patches"
mv "$DEST/patches.new" "$DEST/patches"
rc=0; python3 "$DEST/patches/apply_patches.py" --client "$DEST/client" || rc=$?
if [[ $rc -eq 3 ]]; then
    echo "Warning: the client was set up without HearthDAoC's patches (see the message above)." >&2
elif [[ $rc -ne 0 ]]; then
    echo "Patching the client failed (apply_patches.py exit $rc, see the message above)." >&2; exit 1
fi
sed -e "s|@SERVER@|$SERVER|g" -e "s|@EDITION@|$EDITION|g" "$TEMPLATE" > "$DEST/play.sh"
chmod +x "$DEST/play.sh"
cat <<EOF

Done. Play with: $DEST/play.sh (it checks the client patches at every launch)
Add it to Steam: Games > Add a Non-Steam Game > Browse > $DEST/play.sh, and leave
"Force the use of a specific Steam Play compatibility tool" unchecked.
EOF
