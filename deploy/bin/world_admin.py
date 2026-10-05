#!/usr/bin/env python3
"""World lifecycle for the central server: status, restore a backup, start a new world, and upgrade
to a new upstream version with upstream's progress importer. Everything except status and fetch-clean
expects the server to be stopped (deploy/hdc enforces that) and keeps the previous world first: restore
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
# Upstream's importer clears these (meaningless single-player); a shared server keeps them.
CARRY_TABLES = ("Ban", "SinglePermission")


class AdminError(Exception):
    pass


def _ts():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S")


def _meta(data):
    path = init_world.world_paths(data)["meta"]
    if not os.path.isfile(path):
        raise AdminError("there is no world yet; start the server once (hdc up) to create it")
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
    """Replace the world database with a backup (or an archived world). Returns where the previous
    database went: a pre-restore backup, or the damaged file moved aside when it could not be read."""
    _meta(data)  # without world.json the next start would build a clean world over the restore
    src = name if os.path.isabs(name) else os.path.join(backup.backups_dir(data), name)
    if not os.path.isfile(src):
        raise AdminError(f"backup not found: {src}")
    if not _integrity_ok(src):
        raise AdminError("that backup failed its integrity check; nothing was changed")
    db = init_world.world_paths(data)["db"]
    os.makedirs(os.path.dirname(db), exist_ok=True)
    tmp = db + ".restore"
    # Copy through SQLite so a WAL-mode source (an archived world) brings its last writes along.
    s, d = sqlite3.connect(f"file:{src}?mode=ro", uri=True), sqlite3.connect(tmp)
    try:
        s.backup(d)
        d.execute("PRAGMA journal_mode=DELETE")
    finally:
        d.close()
        s.close()
    previous = None
    if os.path.isfile(db) and _integrity_ok(db):
        previous = backup.create(data, label="pre-restore")
    elif os.path.exists(db):  # damaged: keep it aside instead of deleting it
        previous = f"{db}.damaged-{_ts()}"
        os.replace(db, previous)
        for suffix in ("-wal", "-shm"):
            if os.path.exists(db + suffix):
                os.replace(db + suffix, previous + suffix)
    _remove_sidecars(db)
    os.replace(tmp, db)
    return previous


def _archive_world(data, label):
    p = init_world.world_paths(data)
    dest = os.path.join(data, "archive", f"{label}-{_ts()}")
    os.makedirs(dest)
    shutil.move(os.path.dirname(p["db"]), os.path.join(dest, "world"))
    shutil.move(p["meta"], os.path.join(dest, "world.json"))
    return dest


def _unarchive_world(data, archive):
    """Put an archived world back in place, discarding whatever partial world replaced it."""
    p = init_world.world_paths(data)
    shutil.rmtree(os.path.dirname(p["db"]), ignore_errors=True)
    if os.path.exists(p["meta"]):
        os.remove(p["meta"])
    shutil.move(os.path.join(archive, "world"), os.path.dirname(p["db"]))
    shutil.move(os.path.join(archive, "world.json"), p["meta"])
    os.rmdir(archive)


def new_world(release, data, edition, skip_navmesh=False, log=print):
    _meta(data)
    archive = _archive_world(data, "world")
    try:
        rc = init_world.init(release, data, edition, skip_navmesh=skip_navmesh, log=log)
        problem = None if rc == 0 else f"code {rc}"
    except FetchError as e:
        problem = str(e)
    if problem:
        _unarchive_world(data, archive)
        raise AdminError(f"creating the new world failed ({problem}). Your previous world is back in place; "
                         "nothing changed. Try again when the download works.")
    spawns.reapply(data, log)  # keep the owner's restored leveling spawns (hdc spawns)
    log(f"New '{edition}' world created; the old one is archived in {archive}.")
    return archive


def _carry_admin_state(conn, old_db):
    """Copy bans and single-command permissions from the old world; list server settings that differ."""
    conn.execute("ATTACH DATABASE ? AS old", (old_db,))
    try:
        carried = {}
        with conn:
            for table in CARRY_TABLES:
                main_cols = {r[1] for r in conn.execute(f'PRAGMA main.table_info("{table}")')}
                old_cols = [r[1] for r in conn.execute(f'PRAGMA old.table_info("{table}")')]
                cols = ",".join(f'"{c}"' for c in old_cols if c in main_cols)
                if cols:
                    carried[table] = conn.execute(f'INSERT OR IGNORE INTO main."{table}" ({cols}) '
                                                  f'SELECT {cols} FROM old."{table}"').rowcount
        changed = conn.execute(
            "SELECT o.Key, o.Value, n.Value FROM old.ServerProperty o JOIN main.ServerProperty n "
            "ON lower(n.Key) = lower(o.Key) WHERE o.Value IS NOT n.Value ORDER BY o.Key").fetchall()
    finally:
        conn.execute("DETACH DATABASE old")
    return carried, changed


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
        carried, changed = _carry_admin_state(c, old_db)
        counts_new = {t: c.execute(f"SELECT count(*) FROM {t}").fetchone()[0] for t in counts_old}
        ok = c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    finally:
        c.close()
    if not ok or counts_new != counts_old:
        raise AdminError(f"verification failed (before {counts_old}, after {counts_new}); the current world is unchanged")
    archive = _archive_world(data, "world-pre-upgrade")
    report = os.path.join(archive, "upgrade-report.txt")
    with open(report, "w", encoding="utf-8") as f:
        f.write(f"Upgrade to upstream {release.version}\nCarried over: {carried}\n\n")
        f.write("Server settings that differ from the new world (not carried; re-apply any you changed on purpose):\n")
        f.writelines(f"  {k}: yours={old!r} new={new!r}\n" for k, old, new in changed)
    os.makedirs(os.path.dirname(db))
    shutil.move(new_db, db)
    init_world.write_meta(init_world.world_paths(data)["meta"],
                          dict(meta, version=release.version, navmesh=False,
                               upgraded_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")))
    shutil.rmtree(stage, ignore_errors=True)
    spawns.reapply(data, log)  # the clean world has upstream's spawn list; restore the owner's choice again
    if changed:
        log(f"{len(changed)} server setting(s) differ from the new world's values and were not carried over; see {report}.")
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
