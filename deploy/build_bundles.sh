#!/usr/bin/env bash
# Build a release's two bundles (no EA files): the deploy bundle and the player (client) bundle.
# Usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]
#   (run from the repository root; used by CI and tests). The client bundle carries the committed
#   client/patches/splash.mpk, only when it has the SHA-256 client/patches/classic-creation.json pins,
#   its tag in VERSION and its content ID in CONTENT_ID (also written beside it, for the release).
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
echo "$tag" > "$c/VERSION"  # the release: setup.sh saves it, and play.sh offers the newer ones
# Client patches (classic character creation, splash): our patch data, the appliers and our splash.mpk.
cp "$root"/client/patches/{classic-creation.json,apply_patches.py,patchset.py,splash.mpk} "$c/patches/"
cp "$root"/client/windows/{connect-hearthdaoc.bat,patch-client.bat,patch-client.ps1} "$c/windows/"
cp "$root"/client/patches/{classic-creation.json,splash.mpk} "$c/windows/patches/"
# The client's content ID, in the bundle and beside it on the release: play.sh doesn't offer a newer release with
# the installed one, since only the server changed. SHA-256 of the bundled files' SHA-256 sums and paths (as
# sha256sum prints them, in path order), without VERSION, which only names the release.
python3 - "$c" > "$c/CONTENT_ID" <<'PY'
import hashlib, os, sys
top, lines = sys.argv[1], []
for folder, _dirs, names in os.walk(top):
    for name in names:
        rel = os.path.relpath(os.path.join(folder, name), top).replace(os.sep, "/")
        if rel not in ("VERSION", "CONTENT_ID"):
            with open(os.path.join(folder, name), "rb") as f:
                lines.append((rel, f"{hashlib.sha256(f.read()).hexdigest()}  {rel}\n"))
print(hashlib.sha256("".join(line for _rel, line in sorted(lines)).encode()).hexdigest())
PY
cp "$c/CONTENT_ID" "$out/hearthdaoc-client-$tag.content-id"
(cd "$work/client" && zip -qr "$out/hearthdaoc-client-$tag.zip" "hearthdaoc-client-$tag")
echo "Built $out/hearthdaoc-deploy-$tag.tar.gz, $out/hearthdaoc-client-$tag.zip and $out/hearthdaoc-client-$tag.content-id"
