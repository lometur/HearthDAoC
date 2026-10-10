#!/usr/bin/env python3
"""Restore leveling monsters that OfflineDAoC's classic setup archived.

Upstream's "classic165-legacy-spawns-v1" setup step moved every generic monster spawn last edited in
2021 or later out of the Mob table into offline_classic165_removed_mobs, leaving the home regions with
roughly a third of OpenDAoC's leveling population. `restore` copies back the archived spawns of the
three home regions whose effective level is 1..max_level (no named, quest or "add" mobs, only creatures
that still exist in the world, only rows whose templates exist), records them in fork_restored_mobs and
saves the setting in /data/spawns.json, so new-world and upgrade-world can re-apply it. `undo` removes
exactly the restored rows. Run with the server stopped: mobs load at server start.

The existence check compares names without case, so it also matched named quest monsters: the archived
"arawnite messenger" came back 170 units from level 20's "Arawnite Messenger", and quests then matched a
kill's name exactly, so killing the twin counted for nothing (owner test 2026-10-10; quests now match names
without case, QuestNames.Same, but the twins stay left out as duplicates). `restore` leaves out an
archived row standing within TWIN_RADIUS of a live monster whose name differs from its own only in case and
is a quest's kill target, and world_fixes.py removes such rows an earlier restore added
(remove_quest_twins).
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

# A restored row this close (map units, X and Y) to a quest's kill target named like it but for case is its twin.
TWIN_RADIUS = 1000
TWINS_NOT_REMOVED = "Restored spawns: twins of quest targets not removed ({}); they stay in the world"
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
        raise SpawnsError("there is no world yet; start the server once (hdc up) or run hdc init")
    conn = sqlite3.connect(_db(data), timeout=30)
    conn.isolation_level = None  # explicit transactions below
    return conn


def _has_table(conn, name):
    return conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (name,)).fetchone() is not None


def _columns(conn, table):
    return [r[1] for r in conn.execute(f'PRAGMA table_info("{table}")')]


def kill_targets(conn):
    """The lowercased names of every DataQuest's kill targets: a stage of StepType 0 (Kill) or 1 (KillFinish),
    the name part of its TargetName ("name;region")."""
    if not _has_table(conn, "DataQuest"):
        return set()
    names = set()
    for types, targets in conn.execute("SELECT StepType, TargetName FROM DataQuest"):
        for stage_type, target in zip((types or "").split("|"), (targets or "").split("|")):
            name = target.split(";")[0].strip()
            if stage_type.strip() in ("0", "1") and name:
                names.add(name.lower())
    return names


def _load_quest_targets(conn):
    """temp.quest_targets: the live monsters that are a quest's kill target (by name, without case), the
    restored rows left out."""
    names = kill_targets(conn)
    restored = _has_table(conn, TRACK)
    conn.execute("DROP TABLE IF EXISTS temp.quest_targets")
    conn.execute("CREATE TEMP TABLE quest_targets (LowerName TEXT, Name TEXT, Region INT, X INT, Y INT)")
    rows = conn.execute("SELECT Name, Region, X, Y FROM Mob" +
                        (f" WHERE Mob_ID NOT IN (SELECT Mob_ID FROM {TRACK})" if restored else ""))
    conn.executemany("INSERT INTO temp.quest_targets VALUES (?, ?, ?, ?, ?)",
                     ((name.lower(), name, region, x, y) for name, region, x, y in rows
                      if name and name.lower() in names))
    conn.execute("CREATE INDEX temp.quest_targets_name ON quest_targets (LowerName, Region)")


def _twin(row):
    """SQL true where the Mob-shaped `row` stands within TWIN_RADIUS of a quest target in temp.quest_targets whose name
    differs from its own only in case (compared exactly: Mob.Name has NOCASE)."""
    return (f"EXISTS (SELECT 1 FROM temp.quest_targets q WHERE q.LowerName = lower({row}.Name) "
            f"AND q.Region = {row}.Region AND q.Name <> {row}.Name COLLATE BINARY "
            f"AND (q.X - {row}.X) * (q.X - {row}.X) + (q.Y - {row}.Y) * (q.Y - {row}.Y) <= {TWIN_RADIUS * TWIN_RADIUS})")


def remove_quest_twins(conn):
    """World fix (world_fixes.py, every start): removes the restored rows that restore now leaves out, twins of a
    quest target, as undo removes restored rows: from Mob and fork_restored_mobs, kept in the archive. Only rows
    listed in fork_restored_mobs and still in the archive are touched. Inside world_fixes.py's transaction, under its
    own savepoint; returns the start log's line, the "not removed" line, or nothing."""
    if not all(_has_table(conn, table) for table in ("Mob", TRACK, ARCHIVE)):
        return []
    conn.execute("SAVEPOINT quest_twins")
    try:
        _load_quest_targets(conn)
        twins = conn.execute(f"SELECT Mob_ID, lower(Name) FROM Mob WHERE Mob_ID IN (SELECT Mob_ID FROM {TRACK}) "
                             f"AND Mob_ID IN (SELECT Mob_ID FROM {ARCHIVE}) AND {_twin('Mob')} "
                             "ORDER BY lower(Name), Mob_ID").fetchall()
        for mob_id, _name in twins:
            conn.execute("DELETE FROM Mob WHERE Mob_ID=?", (mob_id,))
            conn.execute(f"DELETE FROM {TRACK} WHERE Mob_ID=?", (mob_id,))
        conn.execute("DROP TABLE temp.quest_targets")
    except Exception as e:  # undo this fix only; the other fixes and the start go on
        conn.execute("ROLLBACK TO quest_twins")
        conn.execute("RELEASE quest_twins")
        conn.execute("DROP TABLE IF EXISTS temp.quest_targets")
        return [TWINS_NOT_REMOVED.format(str(e) or type(e).__name__)]
    conn.execute("RELEASE quest_twins")
    if not twins:
        return []
    counts = {}
    for _mob_id, name in twins:
        counts[name] = counts.get(name, 0) + 1
    what = ("twin of a quest target removed (killing it" if len(twins) == 1
            else "twins of quest targets removed (killing them")
    return [f"Restored spawns: {len(twins)} {what} counted for no quest), still archived in {ARCHIVE}: "
            + ", ".join(f"{name} ({n})" for name, n in counts.items())]


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
            _load_quest_targets(conn)
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
                  AND NOT {_twin("r")}
                GROUP BY r.Mob_ID""", (max_level,))
            conn.execute(f"INSERT INTO Mob ({cols}) SELECT {cols} FROM {ARCHIVE} "
                         "WHERE Mob_ID IN (SELECT Mob_ID FROM temp.pick)")
            now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            n = conn.execute(f"INSERT INTO {TRACK} SELECT Mob_ID, ?, ?, ? FROM temp.pick",
                             (PATCH_ID, max_level, now)).rowcount
            conn.execute("DROP TABLE temp.pick")
            conn.execute("DROP TABLE temp.quest_targets")
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
