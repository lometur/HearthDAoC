"""Carry the RvR state of one world into another: who holds each keep, its doors' health, where the relics
are, the items on keep hookpoints, and the keep capture log.

world_admin.py runs carry() in upgrade-world, on the new world before it replaces the old one (upstream's
import engine copies only progress tables, so keeps, doors and relics would come back as upstream ships
them, and it clears the capture log), and in carry-rvr, on the live world from an archived one. Only what
the server itself writes while it runs is copied, never a keep's structure (KeepID, BaseLevel, position):
- Keep: Realm, Level and ClaimedGuildName (AbstractGameKeep.cs: Reset, ChangeLevel, SaveIntoDatabase).
  Keeps are matched by Name and Region, not by KeepID, which upstream may renumber. Names are compared
  without case, as the Name column does. A Name and Region found twice in either world is skipped.
- Door: Health and State, for the doors of the carried keeps. GameKeepDoor.SaveIntoDatabase writes Health
  and the State setter writes State; nothing writes the other columns of a keep door's row. A door
  belongs to the keep whose area holds it, as DoorMgr.LoadDoor decides: same region (that of the zone
  InternalID / 1000000), and within 4,000 of a portal keep (BaseLevel 100 or more) or 3,000 of another
  keep (KeepArea.cs), the nearest if two. Doors are matched by InternalID within each pair of keeps; an
  InternalID found twice in a keep's area is skipped.
- Relic: where it is and who holds it (GameRelic.SaveIntoDatabase), matched by RelicID, relicType and
  OriginalRealm.
- KeepHookPointItem: the rows of carried keeps, with the new KeepID, where the hookpoint has no item yet.
- KeepCaptureLog: the old rows, with new IDs, except those already there.
Everything runs in one transaction: on an error nothing is changed, and the error is raised.
"""
import datetime

TABLES = ("Keep", "Door", "Relic", "KeepHookPointItem", "KeepCaptureLog")
KEEP_COLUMNS = ("Realm", "Level", "ClaimedGuildName")
DOOR_COLUMNS = ("Health", "State")
RELIC_KEY = ("RelicID", "relicType", "OriginalRealm")
RELIC_COLUMNS = ("Region", "X", "Y", "Z", "Heading", "Realm", "LastRealm", "LastCaptureDate")
HOOKPOINT = ("KeepID", "ComponentID", "HookPointID")
# keeps/KeepArea.cs; a keep is a portal keep from this BaseLevel on (AbstractGameKeep.IsPortalKeep).
PORTAL_KEEP_RADIUS = 4000
KEEP_RADIUS = 3000
PORTAL_KEEP_BASE_LEVEL = 100


def carry(conn, old_db, now=None):
    """Copy the RvR state of the world old_db into conn's world. Returns (counts, notes): the rows carried
    per table, and one line for each kind of row that was not carried."""
    now = now or _now()
    conn.execute("ATTACH DATABASE ? AS old", (old_db,))
    try:
        with conn:
            return _carry(conn, now)
    finally:
        conn.execute("DETACH DATABASE old")


def _carry(conn, now):
    counts, notes = dict.fromkeys(TABLES, 0), []
    present = {table: _has(conn, table) for table in TABLES + ("Zones",)}
    pairs = _match_keeps(conn, notes) if present["Keep"] else {}
    for old_id, new_id in pairs.items():
        values = conn.execute(f"SELECT {', '.join(KEEP_COLUMNS)} FROM old.Keep WHERE KeepID=?", (old_id,)).fetchone()
        _update(conn, "Keep", "KeepID=?", (new_id,), KEEP_COLUMNS, values, now)
    counts["Keep"] = len(pairs)
    if pairs and present["Door"] and present["Zones"]:
        counts["Door"] = _carry_doors(conn, pairs, notes, now)
    if present["Relic"]:
        counts["Relic"] = _carry_relics(conn, notes, now)
    if pairs and present["KeepHookPointItem"]:
        counts["KeepHookPointItem"] = _carry_hookpoint_items(conn, pairs)
    if present["KeepCaptureLog"]:
        counts["KeepCaptureLog"] = _carry_capture_log(conn)
    return counts, notes


def _match_keeps(conn, notes):
    """{old KeepID: new KeepID} for the keeps found once in each world by Name and Region."""
    old, new = _keeps_by_name(conn, "old"), _keeps_by_name(conn, "main")
    pairs, twice, old_only, new_only = {}, [], [], []
    for key in sorted(old.keys() | new.keys(), key=lambda k: (k[1], k[0])):
        o, n = old.get(key, []), new.get(key, [])
        label = f"{(o or n)[0][1]} (region {key[1]})"
        if len(o) > 1 or len(n) > 1:
            twice.append(label)
        elif not n:
            old_only.append(label)
        elif not o:
            new_only.append(label)
        else:
            pairs[o[0][0]] = n[0][0]
    if old_only:
        notes.append("Keeps only in the old world, not carried: " + ", ".join(old_only))
    if new_only:
        notes.append("Keeps only in the new world, left as it ships them: " + ", ".join(new_only))
    if twice:
        notes.append("Keeps whose name is not unique in their region, left as the new world ships them: "
                     + ", ".join(twice))
    return pairs


def _keeps_by_name(conn, schema):
    """{(name in lower case, Region): [(KeepID, Name), ...]} of a world's keeps."""
    found = {}
    for keep_id, name, region in conn.execute(f"SELECT KeepID, Name, Region FROM {schema}.Keep ORDER BY KeepID"):
        found.setdefault((name.lower(), region), []).append((keep_id, name))
    return found


def _carry_doors(conn, pairs, notes, now):
    old, new = _doors_by_keep(conn, "old"), _doors_by_keep(conn, "main")
    carried = missed = 0
    for old_id, new_id in pairs.items():
        new_doors = new.get(new_id, {})
        for internal_id, rows in old.get(old_id, {}).items():
            targets = new_doors.get(internal_id, [])
            if len(rows) != 1 or len(targets) != 1:
                missed += 1
                continue
            values = conn.execute(f"SELECT {', '.join(DOOR_COLUMNS)} FROM old.Door WHERE rowid=?",
                                  (rows[0],)).fetchone()
            _update(conn, "Door", "rowid=?", (targets[0],), DOOR_COLUMNS, values, now)
            carried += 1
    if missed:
        notes.append(f"Doors of carried keeps not carried (no single door with their InternalID in the keep's "
                     f"area in both worlds): {missed}")
    return carried


def _doors_by_keep(conn, schema):
    """{KeepID: {InternalID: [Door rowid, ...]}} of a world's keep doors (see the module's notes)."""
    keeps = conn.execute(f"SELECT KeepID, Region, X, Y, BaseLevel FROM {schema}.Keep").fetchall()
    regions = dict(conn.execute(f"SELECT ZoneID, RegionID FROM {schema}.Zones"))
    found = {}
    for rowid, internal_id, x, y in conn.execute(f"SELECT rowid, InternalID, X, Y FROM {schema}.Door"):
        region, nearest = regions.get(internal_id // 1000000), None
        for keep_id, keep_region, keep_x, keep_y, base_level in keeps:
            radius = PORTAL_KEEP_RADIUS if base_level >= PORTAL_KEEP_BASE_LEVEL else KEEP_RADIUS
            distance = (x - keep_x) ** 2 + (y - keep_y) ** 2
            if keep_region == region and distance <= radius * radius and (nearest is None or distance < nearest[0]):
                nearest = (distance, keep_id)
        if nearest:
            found.setdefault(nearest[1], {}).setdefault(internal_id, []).append(rowid)
    return found


def _carry_relics(conn, notes, now):
    key, n = ", ".join(RELIC_KEY), len(RELIC_KEY)
    new = set(conn.execute(f"SELECT {key} FROM main.Relic"))
    old = conn.execute(f"SELECT {key}, {', '.join(RELIC_COLUMNS)} FROM old.Relic ORDER BY RelicID").fetchall()
    carried, old_only = 0, []
    for row in old:
        if row[:n] in new:
            _update(conn, "Relic", " AND ".join(f"{c}=?" for c in RELIC_KEY), row[:n], RELIC_COLUMNS, row[n:], now)
            carried += 1
        else:
            old_only.append(str(row[0]))
    new_only = sorted(str(row[0]) for row in new - {row[:n] for row in old})
    if old_only:
        notes.append("Relics only in the old world, not carried (RelicID): " + ", ".join(old_only))
    if new_only:
        notes.append("Relics only in the new world, left as it ships them (RelicID): " + ", ".join(new_only))
    return carried


def _carry_hookpoint_items(conn, pairs):
    """Add the old items of carried keeps on hookpoints that have none in the new world; returns how many."""
    columns = _shared_columns(conn, "KeepHookPointItem")
    names = ", ".join(f'"{c}"' for c in columns)
    taken = set(conn.execute(f"SELECT {', '.join(HOOKPOINT)} FROM main.KeepHookPointItem"))
    added = 0
    for row in conn.execute(f"SELECT {names} FROM old.KeepHookPointItem ORDER BY rowid").fetchall():
        item = dict(zip(columns, row))
        if item["KeepID"] not in pairs:
            continue
        item["KeepID"] = pairs[item["KeepID"]]
        spot = tuple(item[c] for c in HOOKPOINT)
        if spot in taken:
            continue
        taken.add(spot)
        added += conn.execute(f"INSERT OR IGNORE INTO main.KeepHookPointItem ({names}) VALUES "
                              f"({', '.join('?' * len(columns))})", tuple(item.values())).rowcount
    return added


def _carry_capture_log(conn):
    """Add the old capture log rows that the new world does not have; the new world numbers them."""
    columns = [c for c in _shared_columns(conn, "KeepCaptureLog") if c != "ID"]
    names = ", ".join(f'"{c}"' for c in columns)
    same = " AND ".join(f'n."{c}" IS o."{c}"' for c in columns if c != "LastTimeRowUpdated")
    return conn.execute(f"INSERT INTO main.KeepCaptureLog ({names}) SELECT {names} FROM old.KeepCaptureLog o "
                        f"WHERE NOT EXISTS (SELECT 1 FROM main.KeepCaptureLog n WHERE {same}) "
                        "ORDER BY o.rowid").rowcount


def _update(conn, table, where, params, columns, values, now):
    """Set columns to values in the new world's row(s) that match where, if a value differs."""
    sets = ", ".join(f'"{c}"=?' for c in columns)
    differs = " OR ".join(f'"{c}" IS NOT ? COLLATE BINARY' for c in columns)
    conn.execute(f'UPDATE main."{table}" SET {sets}, LastTimeRowUpdated=? WHERE {where} AND ({differs})',
                 (*values, now, *params, *values))


def _shared_columns(conn, table):
    new = {r[1] for r in conn.execute(f'PRAGMA main.table_info("{table}")')}
    return [r[1] for r in conn.execute(f'PRAGMA old.table_info("{table}")') if r[1] in new]


def _has(conn, table):
    """Whether both worlds have the table."""
    return all(conn.execute(f"SELECT 1 FROM {schema}.sqlite_master WHERE type='table' AND name=? COLLATE NOCASE",
                            (table,)).fetchone() for schema in ("main", "old"))


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
