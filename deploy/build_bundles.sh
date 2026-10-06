#!/usr/bin/env bash
# Build a release's two bundles (no EA files): the deploy bundle and the player (client) bundle.
# Usage: HDC_MPK_TOOL=<OfflineDaoc.Mpk.dll> deploy/build_bundles.sh <tag> <output dir> [--deploy-only]
#   (run from the repository root; used by CI and tests). HDC_MPK_TOOL is upstream's MPK tool (it needs
#   dotnet); it packs the client bundle's splash.mpk from client/patches/branding/splash.png.
#   --deploy-only builds only the deploy bundle, without the MPK tool (deploy/tests/hdc_integration.sh).
set -euo pipefail
tag="${1:?release tag}"
case "${3:-}" in
    "") client=yes ;;
    --deploy-only) client=no ;;
    *) echo "usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]" >&2; exit 2 ;;
esac
if [[ $client == yes && ! -f "${HDC_MPK_TOOL:-}" ]]; then
    echo "build_bundles.sh: set HDC_MPK_TOOL to upstream's OfflineDaoc.Mpk.dll (it packs the client's splash.mpk):" >&2
    echo "  dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release" >&2
    echo "  HDC_MPK_TOOL=source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll deploy/build_bundles.sh $tag ${2:-dist}" >&2
    exit 2
fi
mkdir -p "${2:?output directory}"; out="$(cd "$2" && pwd)"  # absolute: the zip step runs from another folder
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
if [[ $client == yes ]]; then  # the client's loading splash first: if it can't be built, nothing is written
    python3 -B "$root/client/patches/branding/build_splash_mpk.py" --mpk-tool "$(realpath "$HDC_MPK_TOOL")" \
        --out "$work/splash.mpk" >/dev/null
fi
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
cp "$root"/client/patches/{classic-creation.json,apply_patches.py,patchset.py} "$work/splash.mpk" "$c/patches/"
cp "$root"/client/windows/{connect-hearthdaoc.bat,patch-client.bat,patch-client.ps1} "$c/windows/"
cp "$root"/client/patches/classic-creation.json "$work/splash.mpk" "$c/windows/patches/"
(cd "$work/client" && zip -qr "$out/hearthdaoc-client-$tag.zip" "hearthdaoc-client-$tag")
echo "Built $out/hearthdaoc-deploy-$tag.tar.gz and $out/hearthdaoc-client-$tag.zip"
