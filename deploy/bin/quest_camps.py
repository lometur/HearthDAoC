#!/usr/bin/env python3
"""Quest camps upstream archived, copied back into the world: the company the period gave a quest's target.

Upstream's classic setup moved every generic spawn last edited in 2021 or later out of the Mob table into
offline_classic165_removed_mobs ("Post-legacy generic neutral spawn excluded from the server 1.65 authority"). With them
went camps the period's quest walkthroughs describe: the Tylwyth Teg rangers around Delfina ("by the fire, linked to 2
rangers", 2002), the isolationist camp of the level-45 courier, the Ellyll village around the level-48 seer, and
Lieutenant Grimarth at Sir Dillus's tower. `hdc spawns restore` brings back none of them at the owner's level 20, and
never all: it leaves capitalised names and the levels above its maximum archived. quest_camps.json, beside this file, lists each camp's rows by Mob_ID, with the
names they must carry and why. Two more kinds of entry (owner 2026-10-13) use the same once-per-world marker: "clone"
adds an NPC the period had and the world lacks, made like another row ("like") with its own Mob_ID ("id") and the
"set" columns changed (Lieutenant Cian like Lieutenant Brude); "archive" moves rows the period didn't have into
fork_removed_mobs, as world_fixes.py's duplicate-townspeople fix does (the generated Bline, the standing Martley), and
leaves a row that carries another name than the entry's. A fourth kind, "path" (2026-10-22), adds a walk the period
gave a quest monster (Mulgrut Maggot between the Camelot Hills cemetery and the tall stone): a Path row and its
PathPoints under the entry's PathID, written in the same step as its "walkers" (Mob_IDs) are put on it, never one
without the other, since a PathID without points makes the server flip the NPC between idle and patrolling. A walker
already on another path keeps it and is named in the start log. A PathID already in Path or PathPoints in any case is
someone else's path, left as it is and named: the tables' unique index compares PathIDs without case. The server's path
cache matches them by their exact case (MovementMgr), so only a walker holding the exact PathID walks the path.

world_fixes.py calls apply() inside its transaction, before mob_fixes.py (which then moves rows of these camps), under
its own savepoint: if it fails it is rolled back alone, apply() returns only the "not applied" line, and the other
fixes and the start go on. A camp is copied once per world: it records the marker "quest-camp:<key>" in the fork table
fork_world_fixes, so rows the owner removes later stay removed, and a camp added to the file later is copied on a world
that has the others. The rows keep their Mob_IDs and stay in the archive (as epic_chains.py copies Lord Elidyn's camp);
a row already in Mob is left as it is. Rows missing from both, or archived under another name, are named in the
start log; a camp none of whose rows the world has (in Mob or the archive, under any name) is skipped without a word
(and tried again at the next start). `hdc spawns restore` at a high --max-level can bring back some of a camp's
lowercase-named rows and list them in fork_restored_mobs, which a later restore, `hdc spawns undo` and the quest-twin
removal delete; once the camp is copied, its rows leave that list and stay.
"""
import datetime
import json
import os
import uuid

SAVEPOINT = "quest_camps"
ARCHIVE = "offline_classic165_removed_mobs"
MARKER_TABLE = "fork_world_fixes"
RESTORED = "fork_restored_mobs"  # spawns.py's list of the rows `hdc spawns restore` brought back
REMOVED = "fork_removed_mobs"  # world_fixes.py's and battlegrounds.py's archive of the rows the fork took out
MARKER = "quest-camp:{}"
NOT_APPLIED = "Quest camps: not applied ({}); the camps stay in upstream's archive"
DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "quest_camps.json")


def load_data(path=DATA_FILE):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


KINDS = ("copy", "clone", "archive", "path")
PATH_TYPES = (1, 2, 3)  # EPathType: Once, Path_Reverse (out and back), Loop (CoreDatabase/Enums/EPathType.cs)


def path_id(path, step=None):
    """The Path_ID of a path entry's Path row (step None) or the PathPoints_ID of its point number `step` (from 1)."""
    key = "hearthdaoc:path:" + path.lower() + ("" if step is None else f":{step}")
    return str(uuid.uuid5(uuid.NAMESPACE_URL, key))


def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def _is_point(point):
    """[X, Y, Z, MaxSpeed, WaitTime]: whole numbers, a speed above 0, a wait (tenths of a second) of 0 or more."""
    return (isinstance(point, list) and len(point) == 5 and all(_is_int(v) for v in point)
            and point[3] > 0 and point[4] >= 0)


def check(camp):
    """A camp entry's fields; a bad entry raises ValueError."""
    for field in ("key", "name", "why"):
        if not isinstance(camp.get(field), str) or not camp[field].strip():
            raise ValueError(f"camp {camp.get('key') or camp.get('name') or '?'}: no {field}")
    kind = camp.get("kind", "copy")
    if kind not in KINDS:
        raise ValueError(f"camp {camp['key']}: unknown kind {kind}")
    if kind == "path":
        if not isinstance(camp.get("path"), str) or not camp["path"].strip():
            raise ValueError(f"camp {camp['key']}: no path")
        if not _is_int(camp.get("type")) or camp["type"] not in PATH_TYPES:
            raise ValueError(f"camp {camp['key']}: type must be 1 (once), 2 (out and back) or 3 (loop)")
        if not _is_int(camp.get("region")) or camp["region"] < 0:
            raise ValueError(f"camp {camp['key']}: no region")
        points = camp.get("points")
        if not isinstance(points, list) or len(points) < 2 or not all(_is_point(p) for p in points):
            raise ValueError(f"camp {camp['key']}: points must be 2 or more [X, Y, Z, MaxSpeed, WaitTime]")
        walkers = camp.get("walkers")
        if not isinstance(walkers, list) or not walkers or not all(isinstance(w, str) and w for w in walkers):
            raise ValueError(f"camp {camp['key']}: walkers must be a list of Mob_IDs")
        return
    if kind == "clone":
        for field in ("like", "id"):
            if not isinstance(camp.get(field), str) or not camp[field]:
                raise ValueError(f"camp {camp['key']}: no {field}")
        if not isinstance(camp.get("set"), dict) or not camp["set"] or "Mob_ID" in camp["set"]:
            raise ValueError(f"camp {camp['key']}: set must hold the columns to change (not Mob_ID)")
        return
    for field in ("names", "ids"):
        values = camp.get(field)
        if not isinstance(values, list) or not values or not all(isinstance(v, str) and v for v in values):
            raise ValueError(f"camp {camp['key']}: {field} must be a list of names")


def apply(conn, now=None, data=None):
    """Copy every camp not yet copied in this world; returns the summary line, the "not applied" line, or nothing.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open, the savepoint
    is the transaction, and its release commits it."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if "mob" not in tables or ARCHIVE.lower() not in tables:
        return []
    now = now or _now()
    data = data if data is not None else load_data()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        conn.execute(f"CREATE TABLE IF NOT EXISTS {MARKER_TABLE} (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)")
        columns = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")'))
        copied, missing, added, not_added, archived_rows, not_archived = [], [], [], [], [], []
        paths, other_path = [], []
        for camp in data["camps"]:
            check(camp)
            marker = MARKER.format(camp["key"])
            if conn.execute(f"SELECT 1 FROM {MARKER_TABLE} WHERE FixId=?", (marker,)).fetchone():
                continue
            kind = camp.get("kind", "copy")
            if kind == "path":
                result = _path(conn, camp, tables, now)
                if result is None:
                    continue  # no path tables, or a walker not in Mob: not this world's (another world, or a test's)
                done, others = result
                if done:
                    paths.append(f"{camp['name']} ({len(camp['points'])} points)")
                else:
                    not_added.append(f"{camp['name']} (its path {camp['path']} is taken)")
                if others:
                    other_path.append(f"{camp['name']} ({', '.join(others)})")
                conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (marker, now))
                continue
            if kind == "clone":
                if not conn.execute("SELECT 1 FROM Mob WHERE Mob_ID=?", (camp["like"],)).fetchone():
                    continue  # no row to make it like: not this world's (another world, or a test's); tried again later
                if _clone(conn, camp):
                    added.append(camp["name"])
                else:
                    not_added.append(f"{camp['name']} (its Mob_ID {camp['id']} is taken)")
                conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (marker, now))
                continue
            if kind == "archive":
                n, other = _archive(conn, camp, marker, now)
                if n:
                    archived_rows.append(f"{camp['name']} ({n})")
                if other:
                    not_archived.append(f"{camp['name']} ({', '.join(other)})")
                conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (marker, now))
                continue
            names = [name.lower() for name in camp["names"]]
            marks = ", ".join("?" * len(camp["ids"]))
            archived = {mob_id for (mob_id,) in conn.execute(
                f"SELECT Mob_ID FROM {ARCHIVE} WHERE Mob_ID IN ({marks}) "
                f"AND lower(Name) IN ({', '.join('?' * len(names))})", (*camp["ids"], *names))}
            live = {mob_id for (mob_id,) in conn.execute(f"SELECT Mob_ID FROM Mob WHERE Mob_ID IN ({marks})", camp["ids"])}
            known = {mob_id for (mob_id,) in conn.execute(f"SELECT Mob_ID FROM {ARCHIVE} WHERE Mob_ID IN ({marks})", camp["ids"])}
            if not known and not live:
                continue  # none of the camp is in this world (another world, or a test's): not this world's camp
            gone = [mob_id for mob_id in camp["ids"] if mob_id not in archived and mob_id not in live]
            n = 0
            for mob_id in camp["ids"]:
                if mob_id in archived:
                    n += conn.execute(f"INSERT INTO Mob ({columns}) SELECT {columns} FROM {ARCHIVE} WHERE Mob_ID=? "
                                      "AND Mob_ID NOT IN (SELECT Mob_ID FROM Mob)", (mob_id,)).rowcount
            conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (marker, now))
            if RESTORED.lower() in tables:  # rows `hdc spawns restore` brought back are the camp's now
                conn.execute(f"DELETE FROM {RESTORED} WHERE Mob_ID IN ({marks})", camp["ids"])
            if n:
                copied.append(f"{camp['name']} ({n})")
            if gone:
                missing.append(f"{camp['name']} ({', '.join(gone)})")
    except Exception as e:  # any failure: undo the whole step, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    parts = []
    for label, items in (("copied back from upstream's archive", copied), ("added", added), ("paths added", paths),
                         (f"archived in {REMOVED}", archived_rows), ("not in the archive as named", missing),
                         ("not added", not_added), ("not archived (another name)", not_archived),
                         ("left on another path", other_path)):
        if items:
            parts.append(f"{label}: " + ", ".join(items))
    return ["Quest camps: " + "; ".join(parts)] if parts else []


def _clone(conn, camp):
    """Add the clone camp's row, made like its "like" row in Mob with its own Mob_ID and the "set" columns; False when
    the new Mob_ID is taken."""
    cursor = conn.execute("SELECT * FROM Mob WHERE Mob_ID=?", (camp["like"],))
    row = cursor.fetchone()
    if row is None or conn.execute("SELECT 1 FROM Mob WHERE Mob_ID=?", (camp["id"],)).fetchone():
        return False
    values = dict(zip([d[0] for d in cursor.description], row))
    unknown = sorted(set(camp["set"]) - set(values))
    if unknown:
        raise ValueError(f"camp {camp['key']}: Mob has no column {', '.join(unknown)}")
    values.update(camp["set"], Mob_ID=camp["id"])
    columns = ", ".join(f'"{column}"' for column in values)
    conn.execute(f"INSERT INTO Mob ({columns}) VALUES ({', '.join('?' * len(values))})", list(values.values()))
    return True


def _path(conn, camp, tables, now):
    """Add the path entry's Path row and points and put its walkers on it.

    None when the world lacks the path tables or Mob.PathID, or a walker (not this world's: it waits, as a clone without
    its row does). (False, []) when the PathID is already in Path or PathPoints, in any case: someone else's path, left
    as it is. Else (True, the walkers left on another path): a walker already on a path keeps it; only one without one
    walks this. The Path row and its points are written in the same step as the walkers' PathID, never one without the
    other."""
    if "path" not in tables or "pathpoints" not in tables:
        return None
    if "pathid" not in {name.lower() for _, name, *_ in conn.execute('PRAGMA table_info("Mob")')}:
        return None
    walkers = list(dict.fromkeys(camp["walkers"]))
    marks = ", ".join("?" * len(walkers))
    found = dict(conn.execute(f"SELECT Mob_ID, COALESCE(PathID, '') FROM Mob WHERE Mob_ID IN ({marks})", walkers))
    if len(found) < len(walkers):
        return None
    path = camp["path"]
    if conn.execute("SELECT 1 FROM Path WHERE PathID=? COLLATE NOCASE UNION ALL "
                    "SELECT 1 FROM PathPoints WHERE PathID=? COLLATE NOCASE", (path, path)).fetchone():
        return False, []
    conn.execute("INSERT INTO Path (PathID, PathType, RegionID, LastTimeRowUpdated, Path_ID) VALUES (?, ?, ?, ?, ?)",
                 (path, camp["type"], camp["region"], now, path_id(path)))
    conn.executemany("INSERT INTO PathPoints (PathID, Step, X, Y, Z, MaxSpeed, WaitTime, LastTimeRowUpdated, "
                     "PathPoints_ID) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                     [(path, step, x, y, z, speed, wait, now, path_id(path, step))
                      for step, (x, y, z, speed, wait) in enumerate(camp["points"], 1)])
    others = [mob_id for mob_id in walkers if found[mob_id] and found[mob_id] != path]
    conn.execute(f"UPDATE Mob SET PathID=? WHERE Mob_ID IN ({marks}) AND COALESCE(PathID, '')=''", (path, *walkers))
    return True, others


def _archive(conn, camp, marker, now):
    """Move the archive camp's rows that still carry one of its names into fork_removed_mobs (FixId: the marker);
    returns how many, and the ids found under another name (left as they are)."""
    marks = ", ".join("?" * len(camp["ids"]))
    names = {name.lower() for name in camp["names"]}
    found = conn.execute(f"SELECT Mob_ID, Name FROM Mob WHERE Mob_ID IN ({marks})", camp["ids"]).fetchall()
    take = [mob_id for mob_id, name in found if (name or "").lower() in names]
    other = [mob_id for mob_id, name in found if (name or "").lower() not in names]
    if take:
        # The same archive as the battlegrounds' and duplicate-townspeople fixes (the Mob columns, FixId, RemovedUtc).
        declared = ", ".join(f'"{name}" {kind}' for _, name, kind, *_ in conn.execute('PRAGMA table_info("Mob")'))
        conn.execute(f"CREATE TABLE IF NOT EXISTS {REMOVED} ({declared}, FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL)")
        have = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{REMOVED}")')}
        cols = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in have)
        tmarks = ", ".join("?" * len(take))
        conn.execute(f"INSERT INTO {REMOVED} ({cols}, FixId, RemovedUtc) SELECT {cols}, ?, ? FROM Mob WHERE Mob_ID IN ({tmarks})",
                     (marker, now, *take))
        conn.execute(f"DELETE FROM Mob WHERE Mob_ID IN ({tmarks})", take)
    return len(take), other


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
