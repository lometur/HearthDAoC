#!/usr/bin/env python3
"""Restore leveling monsters that OfflineDAoC's classic setup archived.

Upstream's "classic165-legacy-spawns-v1" setup step moved every generic monster spawn last edited in
2021 or later out of the Mob table into offline_classic165_removed_mobs, leaving the home regions with
roughly a third of OpenDAoC's leveling population. `restore` copies back the archived spawns of the
three home regions whose effective level is 1..max_level (no named, quest or "add" mobs, only creatures
that still exist in the world, only rows whose templates exist), records them in fork_restored_mobs and
saves the setting in /data/spawns.json, so new-world and upgrade-world can re-apply it. `undo` removes
exactly the restored rows. Run with the server stopped: mobs load at server start.
"""
import argparse
import datetime
import json
import os
import sqlite3
import sys

PATCH_ID = "restore-leveling-spawns-v1"
ARCHIVE = "offline_classic165_removed_mobs"
TRACK = "fork_restored_mobs"
HOME_REGIONS = (1, 100, 200)  # Albion, Midgard, Hibernia

# A template that replaces the mob's values decides its level; "10-11" style ranges count by the low end.
EFFECTIVE_LEVEL = ("CASE WHEN t.ReplaceMobValues = 1 AND CAST(t.Level AS INTEGER) > 0 "
                   "THEN CAST(t.Level AS INTEGER) ELSE r.Level END")


class SpawnsError(Exception):
    pass


def _db(data):
    return os.path.join(data, "world", "opendaoc.sqlite3.db")


def _settings(data):
    return os.path.join(data, "spawns.json")


def _connect(data):
    if not os.path.isfile(_db(data)):
        raise SpawnsError("there is no world yet; start the server once (odc up) or run odc init")
    conn = sqlite3.connect(_db(data), timeout=30)
    conn.isolation_level = None  # explicit transactions below
    return conn


def _has_table(conn, name):
    return conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (name,)).fetchone() is not None


def _columns(conn, table):
    return [r[1] for r in conn.execute(f'PRAGMA table_info("{table}")')]


def _remove_restored(conn):
    if not _has_table(conn, TRACK):
        return 0
    n = conn.execute(f"DELETE FROM Mob WHERE Mob_ID IN (SELECT Mob_ID FROM {TRACK})").rowcount
    conn.execute(f"DELETE FROM {TRACK}")
    return n


def restore(data, max_level=20):
    if not isinstance(max_level, int) or not 1 <= max_level <= 50:
        raise SpawnsError("max level must be between 1 and 50")
    conn = _connect(data)
    try:
        if not _has_table(conn, ARCHIVE):
            raise SpawnsError(f"this world has no archived spawns ({ARCHIVE} is missing); nothing to restore")
        archive_cols = set(_columns(conn, ARCHIVE))
        cols = ",".join(f'"{c}"' for c in _columns(conn, "Mob") if c in archive_cols)
        regions = ",".join(str(r) for r in HOME_REGIONS)
        conn.execute("BEGIN IMMEDIATE")
        try:
            conn.execute(f"CREATE TABLE IF NOT EXISTS {TRACK} (Mob_ID TEXT PRIMARY KEY, PatchId TEXT NOT NULL, "
                         "MaxLevel INTEGER NOT NULL, RestoredUtc TEXT NOT NULL)")
            _remove_restored(conn)  # restore always yields exactly levels 1..max_level
            conn.execute("DROP TABLE IF EXISTS temp.pick")
            conn.execute(f"""
                CREATE TEMP TABLE pick AS
                SELECT r.Mob_ID FROM {ARCHIVE} r
                LEFT JOIN NpcTemplate t ON t.TemplateId = r.NPCTemplateID
                WHERE r.Region IN ({regions})
                  AND r.Realm = 0 AND r.ClassType = 'DOL.GS.GameNPC'
                  AND r.Name = lower(r.Name)
                  AND lower(r.Name) IN (SELECT lower(Name) FROM Mob)
                  AND COALESCE(r.PackageID, '') NOT LIKE '%Baf' AND COALESCE(r.PackageID, '') NOT LIKE '%Add'
                  AND (r.NPCTemplateID <= 0 OR t.TemplateId IS NOT NULL)
                  AND (COALESCE(r.EquipmentTemplateID, '') = ''
                       OR r.EquipmentTemplateID IN (SELECT TemplateID FROM NPCEquipment))
                  AND {EFFECTIVE_LEVEL} BETWEEN 1 AND ?
                  AND r.Mob_ID NOT IN (SELECT Mob_ID FROM Mob)
                GROUP BY r.Mob_ID""", (max_level,))
            conn.execute(f"INSERT INTO Mob ({cols}) SELECT {cols} FROM {ARCHIVE} "
                         "WHERE Mob_ID IN (SELECT Mob_ID FROM temp.pick)")
            now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            n = conn.execute(f"INSERT INTO {TRACK} SELECT Mob_ID, ?, ?, ? FROM temp.pick",
                             (PATCH_ID, max_level, now)).rowcount
            conn.execute("DROP TABLE temp.pick")
            conn.execute("COMMIT")
        except BaseException:
            conn.execute("ROLLBACK")
            raise
    finally:
        conn.close()
    tmp = _settings(data) + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"patch": PATCH_ID, "max_level": max_level}, f)
        f.write("\n")
    os.replace(tmp, _settings(data))
    return n


def undo(data):
    conn = _connect(data)
    try:
        conn.execute("BEGIN IMMEDIATE")
        try:
            n = _remove_restored(conn)
            conn.execute(f"DROP TABLE IF EXISTS {TRACK}")
            conn.execute("COMMIT")
        except BaseException:
            conn.execute("ROLLBACK")
            raise
    finally:
        conn.close()
    if os.path.exists(_settings(data)):
        os.remove(_settings(data))
    return n


def _saved(data):
    if not os.path.isfile(_settings(data)):
        return None
    with open(_settings(data), encoding="utf-8") as f:
        return json.load(f)


def status(data):
    saved = _saved(data)
    conn = _connect(data)
    try:
        restored = conn.execute(f"SELECT count(*) FROM {TRACK}").fetchone()[0] if _has_table(conn, TRACK) else 0
        mobs = conn.execute("SELECT count(*) FROM Mob").fetchone()[0]
    finally:
        conn.close()
    return {"enabled": saved is not None, "max_level": saved["max_level"] if saved else None,
            "restored": restored, "mobs_total": mobs}


def reapply(data, log=print):
    """After new-world or upgrade-world: restore again with the saved setting. None when not enabled."""
    saved = _saved(data)
    if saved is None:
        return None
    n = restore(data, int(saved["max_level"]))
    log(f"Restored {n} leveling spawns (levels 1-{saved['max_level']}) in the new world.")
    return n


def main(argv=None):
    ap = argparse.ArgumentParser(description="Restore archived leveling spawns (server must be stopped).")
    ap.add_argument("--data", required=True)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("restore")
    r.add_argument("--max-level", type=int, default=20)
    sub.add_parser("undo")
    sub.add_parser("status")
    a = ap.parse_args(argv)
    try:
        if a.cmd == "restore":
            n = restore(a.data, a.max_level)
            print(f"Restored {n} archived leveling spawns (levels 1-{a.max_level}). They appear at the next server start.")
        elif a.cmd == "undo":
            print(f"Removed {undo(a.data)} restored spawns. The world is back to upstream's spawn list.")
        else:
            for k, v in status(a.data).items():
                print(f"{k:12} {v}")
    except SpawnsError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
