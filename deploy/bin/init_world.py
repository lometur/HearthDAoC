#!/usr/bin/env python3
"""Create or check the world in /data from the pinned upstream release.

First start: download the edition's clean world database and the navmeshes (all verified), then
write /data/world.json LAST, so an interrupted first start simply resumes on the next start.
Later starts: refuse if OFFLINEDAOC_EDITION or the image's upstream version differs from the world's.
"""
import argparse
import datetime
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(HERE, "..", "..", "tools", "linux")]

from odaoc_fetch import FetchError, Release, sha256_file  # noqa: E402

EXIT_DOWNLOAD = 2
EXIT_EDITION = 3
EXIT_VERSION = 4


def world_paths(data):
    return {
        "meta": os.path.join(data, "world.json"),
        "db": os.path.join(data, "world", "opendaoc.sqlite3.db"),
        "navmesh": os.path.join(data, "navmesh"),
    }


def write_meta(path, meta):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def ensure_navmesh(release, navmesh_dir, seed=None, log=print):
    prefix = release.lock["navmesh_prefix"]
    files = release.files_under(prefix)
    os.makedirs(navmesh_dir, exist_ok=True)
    fetched = 0
    for i, rel in enumerate(files, 1):
        name = rel[len(prefix):]
        dest = os.path.join(navmesh_dir, name)
        if seed and not os.path.isfile(dest):
            src = os.path.join(seed, name)
            if os.path.isfile(src) and sha256_file(src) == release.expected_sha256(rel):
                shutil.copyfile(src, dest + ".part")
                os.replace(dest + ".part", dest)
        if release.extract(rel, dest) == "ok":
            fetched += 1
        if i % 10 == 0 or i == len(files):
            log(f"  navmesh {i}/{len(files)}")
    return len(files), fetched


def init(release, data, edition, skip_navmesh=False, seed_navmesh=None, log=print):
    p = world_paths(data)
    if os.path.isfile(p["meta"]):
        with open(p["meta"], encoding="utf-8") as f:
            meta = json.load(f)
        if meta["edition"] != edition:
            log(f"ERROR: this world was created as edition '{meta['edition']}', but OFFLINEDAOC_EDITION is "
                f"'{edition}'. Set OFFLINEDAOC_EDITION={meta['edition']} in .env, or start a new world on purpose "
                f"with: odc new-world --edition {edition}")
            return EXIT_EDITION
        if meta["version"] != release.version:
            log(f"ERROR: this world is from upstream {meta['version']}, but this image is for {release.version}. "
                "Deploy the matching image, or run: odc upgrade-world (it backs up first).")
            return EXIT_VERSION
        if not skip_navmesh and not meta.get("navmesh"):
            ensure_navmesh(release, p["navmesh"], seed_navmesh, log)
            meta["navmesh"] = True
            write_meta(p["meta"], meta)
        return 0
    log(f"Creating a new '{edition}' world from upstream {release.version}...")
    release.extract(release.edition(edition)["world_db"], p["db"])
    log("  world database ready")
    navmesh = False
    if not skip_navmesh:
        total, fetched = ensure_navmesh(release, p["navmesh"], seed_navmesh, log)
        navmesh = True
        log(f"  navmeshes ready ({total} files, {fetched} downloaded)")
    write_meta(p["meta"], {
        "version": release.version,
        "edition": edition,
        "navmesh": navmesh,
        "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
    })
    log("World ready.")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="Create or check the OfflineDAoC world in a data folder.")
    ap.add_argument("--lock", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--edition", required=True, choices=["classic", "b"])
    ap.add_argument("--skip-navmesh", action="store_true")
    ap.add_argument("--seed-navmesh", help="folder of already-downloaded zoneNNN.nav files to verify and reuse")
    a = ap.parse_args(argv)
    try:
        return init(Release.from_lock_file(a.lock), a.data, a.edition, a.skip_navmesh, a.seed_navmesh)
    except FetchError as e:
        print(f"ERROR: world download failed: {e}. The server was not started; the next start resumes.",
              file=sys.stderr)
        return EXIT_DOWNLOAD


if __name__ == "__main__":
    sys.exit(main())
