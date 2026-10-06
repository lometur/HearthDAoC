#!/usr/bin/env bash
# Build a release's two bundles (no EA files): the deploy bundle and the player (client) bundle.
# Usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]
#   (run from the repository root; used by CI and tests). The client bundle carries the committed
#   client/patches/splash.mpk, only when it has the SHA-256 client/patches/classic-creation.json pins.
#   --deploy-only builds only the deploy bundle (deploy/tests/hdc_integration.sh).
set -euo pipefail
tag="${1:?release tag}"
case "${3:-}" in
    "") client=yes ;;
    --deploy-only) client=no ;;
    *) echo "usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]" >&2; exit 2 ;;
esac
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ $client == yes ]]; then  # the client's loading splash first: if it isn't the pinned one, nothing is written
    python3 -c '
import hashlib, json, os, sys
os.chdir(sys.argv[1])
splash, patchset = "client/patches/splash.mpk", "client/patches/classic-creation.json"
with open(patchset, encoding="utf-8") as f:
    pinned = [entry["after"] for entry in json.load(f)["files"] if entry["path"] == "pregame/splash.mpk"]
try:
    with open(splash, "rb") as f:
        actual = hashlib.sha256(f.read()).hexdigest()
except OSError as e:
    sys.exit(f"build_bundles.sh: cannot read {splash}: {e.strerror}")
if pinned != [actual]:
    pins = " and ".join(pinned) or "no splash.mpk"
    sys.exit(f"build_bundles.sh: {splash} has SHA-256 {actual}, but {patchset} pins {pins}: "
             "commit splash.png, splash.mpk and the rebuilt patch set together (docs/fork/FORK.md, Client patches)")
' "$root"
fi
mkdir -p "${2:?output directory}"; out="$(cd "$2" && pwd)"  # absolute: the zip step runs from another folder
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/deploy"
cp "$root"/deploy/{compose.yml,.env.example,hdc,HANDOFF.md,upstream.lock} "$work/deploy/"
sed -i "s/^HEARTHDAOC_TAG=.*/HEARTHDAOC_TAG=$tag/" "$work/deploy/.env.example"  # hdc update reads the tag here
grep -qxF "HEARTHDAOC_TAG=$tag" "$work/deploy/.env.example" || { echo "deploy/.env.example has no HEARTHDAOC_TAG= line" >&2; exit 1; }
tar czf "$out/hearthdaoc-deploy-$tag.tar.gz" -C "$work/deploy" .
if [[ $client == no ]]; then echo "Built $out/hearthdaoc-deploy-$tag.tar.gz"; exit 0; fi
c="$work/client/hearthdaoc-client-$tag"
mkdir -p "$c/patches" "$c/windows/patches"
cp "$root"/client/README.md "$root"/client/linux/setup.sh "$root"/client/linux/play.sh.in \
   "$root"/tools/linux/odaoc_fetch.py "$root"/deploy/upstream.lock "$c/"
# Client patches (classic character creation, splash): our patch data, the appliers and our splash.mpk.
cp "$root"/client/patches/{classic-creation.json,apply_patches.py,patchset.py,splash.mpk} "$c/patches/"
cp "$root"/client/windows/{connect-hearthdaoc.bat,patch-client.bat,patch-client.ps1} "$c/windows/"
cp "$root"/client/patches/{classic-creation.json,splash.mpk} "$c/windows/patches/"
(cd "$work/client" && zip -qr "$out/hearthdaoc-client-$tag.zip" "hearthdaoc-client-$tag")
echo "Built $out/hearthdaoc-deploy-$tag.tar.gz and $out/hearthdaoc-client-$tag.zip"
