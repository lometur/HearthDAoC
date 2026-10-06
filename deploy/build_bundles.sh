#!/usr/bin/env bash
# Build a release's two bundles (no EA files): the deploy bundle and the player (client) bundle.
# Usage: deploy/build_bundles.sh <tag> <output dir>   (run from the repository root; used by CI and tests)
set -euo pipefail
tag="${1:?release tag}"
mkdir -p "${2:?output directory}"; out="$(cd "$2" && pwd)"  # absolute: the zip step runs from another folder
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/deploy" "$work/client/hearthdaoc-client-$tag/windows"
cp "$root"/deploy/{compose.yml,.env.example,hdc,HANDOFF.md,upstream.lock} "$work/deploy/"
sed -i "s/^HEARTHDAOC_TAG=.*/HEARTHDAOC_TAG=$tag/" "$work/deploy/.env.example"
tar czf "$out/hearthdaoc-deploy-$tag.tar.gz" -C "$work/deploy" .
c="$work/client/hearthdaoc-client-$tag"
cp "$root"/client/README.md "$root"/client/linux/setup.sh "$root"/client/linux/play.sh.in \
   "$root"/tools/linux/odaoc_fetch.py "$root"/deploy/upstream.lock "$c/"
cp "$root"/client/windows/connect-hearthdaoc.bat "$c/windows/"
(cd "$work/client" && zip -qr "$out/hearthdaoc-client-$tag.zip" "hearthdaoc-client-$tag")
echo "Built $out/hearthdaoc-deploy-$tag.tar.gz and $out/hearthdaoc-client-$tag.zip"
