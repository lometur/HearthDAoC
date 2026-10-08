#!/usr/bin/env python3
"""Create or check the world in /data from the pinned upstream release.

First start: download the edition's clean world database and the navmeshes (all verified), then
write /data/world.json LAST, so an interrupted first start simply resumes on the next start.
Later starts: refuse if HEARTHDAOC_EDITION or the image's upstream version differs from the world's.
Every start: make sure the release's server data files (the lock's server_files, which the server
reads from its own folder) are in /data/server-files/<upstream version>, and with --server-dir link
them into the server's folder.
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
EXIT_NOT_WRITABLE = 64


def not_writable_hint(data):
    """None when this user can write the data folder and the world in it, else the fix to print."""
    try:
        os.makedirs(data, exist_ok=True)
    except OSError:
        pass
    paths = [data] + [os.path.join(data, d) for d in ("world", "navmesh", "server-files", "logs", "state", "backups")]
    paths.append(world_paths(data)["db"])
    bad = [p for p in paths if os.path.exists(p) and not os.access(p, os.W_OK)]
    if not bad and os.access(data, os.W_OK):
        return None
    uid, gid = os.getuid(), os.getgid()
    return (f"ERROR: {', '.join(bad) or data} is not writable by uid {uid}:{gid}. Give the volume to that user:\n"
            f"  docker run --rm --user 0 -v <volume, e.g. hearthdaoc-data>:/data --entrypoint sh <image> "
            f"-c 'touch /data/.owner && chown -R {uid}:{gid} /data'\n"
            "(The touch matters: Docker gives an empty volume back to the image's owner on every mount.)")


def swap_marker(data):
    return os.path.join(data, "world-swap.json")


def write_swap_marker(data, archive):
    """Written before upgrade-world/new-world move the current world into `archive`."""
    with open(swap_marker(data) + ".tmp", "w", encoding="utf-8") as f:
        json.dump({"archive": archive}, f)
    os.replace(swap_marker(data) + ".tmp", swap_marker(data))


def recover_interrupted_swap(data, log=print):
    """If a world swap was interrupted (marker present, no world.json), put the archived world back."""
    marker = swap_marker(data)
    if not os.path.isfile(marker):
        return False
    p = world_paths(data)
    with open(marker, encoding="utf-8") as f:
        archive = json.load(f)["archive"]
    if os.path.isfile(p["meta"]) or not os.path.isfile(os.path.join(archive, "world.json")):
        os.remove(marker)  # the swap finished (or there is nothing to put back)
        return False
    shutil.rmtree(os.path.dirname(p["db"]), ignore_errors=True)
    shutil.move(os.path.join(archive, "world"), os.path.dirname(p["db"]))
    shutil.move(os.path.join(archive, "world.json"), p["meta"])
    os.rmdir(archive)
    os.remove(marker)
    log("WARNING: an upgrade-world or new-world was interrupted; the previous world was put back. "
        "Run the command again if you still want it.")
    return True


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


def server_files_dir(data, version):
    return os.path.join(data, "server-files", version)


def ensure_server_files(release, data, log=print):
    """Download the lock's server_files (verified) into this upstream version's folder. A file is
    only written once its hash checked out, so one that exists is complete: later starts need no
    network. Returns how many were downloaded."""
    folder = server_files_dir(data, release.version)
    fetched = 0
    for name in release.lock.get("server_files", []):
        dest = os.path.join(folder, name)
        if not os.path.isfile(dest):
            release.extract(release.lock["server_prefix"] + name, dest)
            fetched += 1
    if fetched:
        log(f"  server data files ready ({fetched} downloaded)")
    return fetched


def prune_server_files(data, version):
    """Remove other upstream versions' server data files (the world is on `version`)."""
    parent = os.path.dirname(server_files_dir(data, version))
    if os.path.isdir(parent):
        for name in os.listdir(parent):
            if name != version:
                shutil.rmtree(os.path.join(parent, name), ignore_errors=True)


def link_server_files(release, data, server_dir):
    """Link each server data file into server_dir, where the server reads it. .NET treats a dangling
    link as an existing file, so a file that is missing gets no link."""
    folder = server_files_dir(data, release.version)
    for name in release.lock.get("server_files", []):
        link, target = os.path.join(server_dir, name), os.path.join(folder, name)
        if os.path.isdir(link) and not os.path.islink(link):
            shutil.rmtree(link)
        elif os.path.lexists(link):
            os.remove(link)
        if os.path.isfile(target):
            os.symlink(target, link)


def init(release, data, edition, skip_navmesh=False, seed_navmesh=None, log=print, recover=True):
    if recover:  # at server start; new-world turns it off because it is the swap in progress
        recover_interrupted_swap(data, log)
    p = world_paths(data)
    if os.path.isfile(p["meta"]):
        with open(p["meta"], encoding="utf-8") as f:
            meta = json.load(f)
        if meta["edition"] != edition:
            log(f"ERROR: this world was created as edition '{meta['edition']}', but HEARTHDAOC_EDITION is "
                f"'{edition}'. Set HEARTHDAOC_EDITION={meta['edition']} in .env, or start a new world on purpose "
                f"with: hdc new-world --edition {edition}")
            return EXIT_EDITION
        if meta["version"] != release.version:
            log(f"ERROR: this world is from upstream {meta['version']}, but this image is for {release.version}. "
                "Deploy the matching image, or run: hdc upgrade-world (it backs up first).")
            return EXIT_VERSION
        ensure_server_files(release, data, log)  # a world from before server_files, or a new version
        if not skip_navmesh and not meta.get("navmesh"):
            ensure_navmesh(release, p["navmesh"], seed_navmesh, log)
            meta["navmesh"] = True
            write_meta(p["meta"], meta)
        return 0
    log(f"Creating a new '{edition}' world from upstream {release.version}...")
    release.extract(release.edition(edition)["world_db"], p["db"])
    log("  world database ready")
    ensure_server_files(release, data, log)
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
    ap = argparse.ArgumentParser(description="Create or check the HearthDAoC world in a data folder.")
    ap.add_argument("--lock", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--edition", required=True, choices=["classic", "b"])
    ap.add_argument("--skip-navmesh", action="store_true")
    ap.add_argument("--seed-navmesh", help="folder of already-downloaded zoneNNN.nav files to verify and reuse")
    ap.add_argument("--server-dir", help="the server's folder: link the server data files into it")
    a = ap.parse_args(argv)
    hint = not_writable_hint(a.data)
    if hint:
        print(hint, file=sys.stderr)
        return EXIT_NOT_WRITABLE
    try:
        release = Release.from_lock_file(a.lock)
        rc = init(release, a.data, a.edition, a.skip_navmesh, a.seed_navmesh)
        if rc == 0:  # the world is on this release's version, so other versions' files are stale
            prune_server_files(a.data, release.version)
            if a.server_dir:
                link_server_files(release, a.data, a.server_dir)
        return rc
    except FetchError as e:
        print(f"ERROR: world download failed: {e}. The server was not started; the next start resumes.",
              file=sys.stderr)
        return EXIT_DOWNLOAD


if __name__ == "__main__":
    sys.exit(main())
