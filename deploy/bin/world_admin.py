#!/usr/bin/env python3
"""World lifecycle for the central server: status, restore a backup, start a new world, and upgrade
to a new upstream version with upstream's progress importer. Everything except status and fetch-clean
expects the server to be stopped (deploy/odc enforces that) and keeps the previous world first: restore
takes a backup, new-world and upgrade-world move the whole old world into /data/archive."""
import argparse
import datetime
import json
import os
import shlex
import shutil
import sqlite3
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(HERE, "..", "..", "tools", "linux")]

import backup  # noqa: E402
import init_world  # noqa: E402
import spawns  # noqa: E402
from odaoc_fetch import FetchError, Release  # noqa: E402

CLEAN_WORLD = "upgrade-clean-world.db"


class AdminError(Exception):
    pass


def _ts():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S")


def _meta(data):
    path = init_world.world_paths(data)["meta"]
    if not os.path.isfile(path):
        raise AdminError("there is no world yet; start the server once (odc up) to create it")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _integrity_ok(path):
    try:
        c = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        try:
            return c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        finally:
            c.close()
    except sqlite3.DatabaseError:
        return False


def _remove_sidecars(db):
    for suffix in ("-wal", "-shm"):
        try:
            os.remove(db + suffix)
        except FileNotFoundError:
            pass


def status(data):
    meta = _meta(data)
    db = init_world.world_paths(data)["db"]
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True, timeout=10)
    try:
        def count(sql):
            try:
                return c.execute(sql).fetchone()[0]
            except sqlite3.OperationalError:
                return None
        st = {
            "version": meta["version"], "edition": meta["edition"], "navmesh": meta.get("navmesh", False),
            "accounts": count("SELECT count(*) FROM Account"),
            "characters": count("SELECT count(*) FROM DOLCharacters"),
            "players_possibly_online": count("SELECT count(*) FROM Account WHERE LastLogin IS NOT NULL AND "
                                             "(LastDisconnected IS NULL OR LastDisconnected < LastLogin)"),
            "bots_online": count("SELECT count(*) FROM offline_world_bots WHERE IsOnline=1"),
            "bots_total": count("SELECT count(*) FROM offline_world_bots WHERE IsRetired=0"),
        }
    finally:
        c.close()
    backups = sorted(f for f in os.listdir(backup.backups_dir(data))) if os.path.isdir(backup.backups_dir(data)) else []
    st["latest_backup"] = backups[-1] if backups else None
    return st


def restore(data, name):
    src = name if os.path.isabs(name) else os.path.join(backup.backups_dir(data), name)
    if not os.path.isfile(src):
        raise AdminError(f"backup not found: {src}")
    if not _integrity_ok(src):
        raise AdminError("that backup failed its integrity check; nothing was changed")
    pre = backup.create(data, label="pre-restore")
    db = init_world.world_paths(data)["db"]
    _remove_sidecars(db)
    shutil.copyfile(src, db + ".restore")
    os.replace(db + ".restore", db)
    return pre


def _archive_world(data, label):
    p = init_world.world_paths(data)
    dest = os.path.join(data, "archive", f"{label}-{_ts()}")
    os.makedirs(dest)
    shutil.move(os.path.dirname(p["db"]), os.path.join(dest, "world"))
    shutil.move(p["meta"], os.path.join(dest, "world.json"))
    return dest


def new_world(release, data, edition, skip_navmesh=False, log=print):
    _meta(data)
    archive = _archive_world(data, "world")  # moved intact, so this also works when the old world is damaged
    rc = init_world.init(release, data, edition, skip_navmesh=skip_navmesh, log=log)
    if rc != 0:
        raise AdminError(f"creating the new world failed (code {rc}); the old world is in {archive}")
    spawns.reapply(data, log)  # keep the owner's restored leveling spawns (odc spawns)
    log(f"New '{edition}' world created; the old one is archived in {archive}.")
    return archive


def fetch_clean(release, data, log=print):
    """Download (verified, resumable) this release's clean world for the world's edition. Kept separate
    from upgrade_world so the download can run with the network and the import without it."""
    meta = _meta(data)
    dest = os.path.join(data, CLEAN_WORLD)
    release.extract(release.edition(meta["edition"])["world_db"], dest)
    log(f"Clean {meta['edition']} world for upstream {release.version} ready.")
    return dest


def upgrade_world(release, data, importer_cmd, clean_world=None, same_version_ok=False, log=print):
    meta = _meta(data)
    if meta["version"] == release.version and not same_version_ok:
        raise AdminError(f"the world is already on upstream {release.version}; nothing to upgrade")
    db = init_world.world_paths(data)["db"]
    backup.create(data, label="pre-upgrade")
    stage = os.path.join(data, "upgrade", _ts())
    old_rt, new_rt = os.path.join(stage, "old", "runtime"), os.path.join(stage, "new", "runtime")
    os.makedirs(os.path.join(old_rt, "data"))
    os.makedirs(os.path.join(new_rt, "data"))
    old_db = os.path.join(old_rt, "data", "opendaoc.sqlite3.db")
    new_db = os.path.join(new_rt, "data", "opendaoc.sqlite3.db")
    src = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    dst = sqlite3.connect(old_db)
    try:
        src.backup(dst)
        names = [r[0] for r in src.execute("SELECT Name FROM Account ORDER BY Name")]
        plvls = dict(src.execute("SELECT Name, PrivLevel FROM Account").fetchall())
        counts_old = {t: src.execute(f"SELECT count(*) FROM {t}").fetchone()[0]
                      for t in ("Account", "DOLCharacters", "offline_world_bots")}
    finally:
        dst.close()
        src.close()
    if names:
        # The importer needs runtime/account.txt to name an existing account when there are several.
        # Naming one keeps every account's password (ImportEngine.cs only rewrites a password when
        # account.txt is missing or invalid); the password value here is never used.
        with open(os.path.join(old_rt, "account.txt"), "w", encoding="utf-8", newline="") as f:
            f.write(f"Account: {names[0]}\r\nPassword: unused\r\n")
    if clean_world:
        shutil.copyfile(clean_world, new_db)
    else:
        log(f"Fetching the clean {meta['edition']} world for upstream {release.version}...")
        release.extract(release.edition(meta["edition"])["world_db"], new_db)
    report = os.path.join(stage, "import-report.txt")
    cmd = list(importer_cmd) + ["--import", os.path.join(stage, "old"), os.path.join(stage, "new"), "--replace-progress", report]
    log("Importing progress with upstream's import engine...")
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        detail = open(report, encoding="utf-8", errors="replace").read()[:2000] if os.path.exists(report) else r.stderr
        raise AdminError(f"the import failed; the current world is unchanged. Details:\n{detail}")
    c = sqlite3.connect(new_db)
    try:
        with c:  # the importer resets every account to plvl 1; restore GM/admin rights
            c.executemany("UPDATE Account SET PrivLevel=? WHERE Name=?", [(v, k) for k, v in plvls.items()])
        counts_new = {t: c.execute(f"SELECT count(*) FROM {t}").fetchone()[0] for t in counts_old}
        ok = c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    finally:
        c.close()
    if not ok or counts_new != counts_old:
        raise AdminError(f"verification failed (before {counts_old}, after {counts_new}); the current world is unchanged")
    archive = _archive_world(data, "world-pre-upgrade")
    os.makedirs(os.path.dirname(db))
    shutil.move(new_db, db)
    init_world.write_meta(init_world.world_paths(data)["meta"],
                          dict(meta, version=release.version, navmesh=False,
                               upgraded_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")))
    shutil.rmtree(stage, ignore_errors=True)
    spawns.reapply(data, log)  # the clean world has upstream's spawn list; restore the owner's choice again
    log(f"Upgraded to upstream {release.version}: {counts_new}. Previous world archived in {archive}. "
        "Navmeshes are re-verified on the next start.")
    return archive


def main(argv=None):
    ap = argparse.ArgumentParser(description="World administration (server must be stopped except for status).")
    ap.add_argument("--data", required=True)
    ap.add_argument("--lock", required=True)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("status")
    r = sub.add_parser("restore")
    r.add_argument("name", help="file name in /data/backups, or an absolute path")
    n = sub.add_parser("new-world")
    n.add_argument("--edition", required=True, choices=["classic", "b"])
    n.add_argument("--skip-navmesh", action="store_true")
    sub.add_parser("fetch-clean", help="download the clean world that upgrade-world imports into")
    u = sub.add_parser("upgrade-world")
    u.add_argument("--importer", required=True, help='e.g. "dotnet /app/tools/progress-import/progress-import.dll"')
    u.add_argument("--clean-world", help="an already-downloaded clean world (from fetch-clean); removed on success")
    u.add_argument("--same-version", action="store_true", help="re-import into a fresh copy of the same version")
    a = ap.parse_args(argv)
    try:
        if a.cmd == "status":
            for k, v in status(a.data).items():
                print(f"{k:24} {v}")
        elif a.cmd == "restore":
            print(f"Restored {a.name}. The previous database was saved as {os.path.basename(restore(a.data, a.name))}.")
        elif a.cmd == "new-world":
            new_world(Release.from_lock_file(a.lock), a.data, a.edition, a.skip_navmesh)
        elif a.cmd == "fetch-clean":
            fetch_clean(Release.from_lock_file(a.lock), a.data)
        else:
            upgrade_world(Release.from_lock_file(a.lock), a.data, shlex.split(a.importer), a.clean_world, a.same_version)
            if a.clean_world and os.path.exists(a.clean_world):
                os.remove(a.clean_world)
    except (AdminError, FetchError) as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
