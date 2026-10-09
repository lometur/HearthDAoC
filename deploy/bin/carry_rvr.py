"""Carry the RvR state of one world into another: who holds the keeps in play, their doors' health, where
the relics away from home are, the items on keep hookpoints, and the keep capture log.

world_admin.py runs carry() in upgrade-world, on the new world before it replaces the old one (upstream's
import engine copies only progress tables, so keeps, doors and relics would come back as upstream ships
them, and it clears the capture log), and in carry-rvr, on the live world from an archived one. Only state
that came from play is copied, so upstream's own changes to keeps and relics nobody touched stay; and only
what the server itself writes while it runs, never a keep's structure (KeepID, BaseLevel, position):
- Keep: Realm, Level and ClaimedGuildName (AbstractGameKeep.cs: Reset, ChangeLevel, SaveIntoDatabase), for
  the keeps in play in the old world: held by another realm than their own (Realm is not OriginalRealm),
  or claimed. Keeps are matched by Name and Region, not by KeepID, which upstream may renumber. Names are
  compared without case, as the Name column does. A Name and Region found twice in either world is skipped.
- Door: Health and State, both from the old row, for the doors of the keeps in play. GameKeepDoor
  writes Health in SaveIntoDatabase and State in the State setter; nothing writes the other columns of a
  keep door's row. Health is at most the door's full health in the new world at the carried Level
  (door_full_health), as the new version may change base levels. A door belongs to the keep whose area
  holds it, as DoorMgr.LoadDoor decides: same region (that of the zone InternalID / 1000000), and within
  4,000 of a portal keep (BaseLevel 100 or more) or 3,000 of another keep (KeepArea.cs). The server takes
  the first such area it finds; this takes the nearest keep (no door is in two areas in the 0.34 or 0.35
  world). Doors are matched by InternalID within each pair of keeps; an InternalID found twice in a
  keep's area is skipped.
- Relic: where it is and who holds it (GameRelic.SaveIntoDatabase), for the relics away from home in the
  old world (Realm is not OriginalRealm), matched by RelicID, relicType and OriginalRealm.
- KeepHookPointItem: the rows of the keeps in play, with the new KeepID, where the hookpoint has no item yet.
- KeepCaptureLog: the old rows, with new IDs, except those already there.
A table, or a column one needs, missing in either world (a new version may rename or drop one) leaves that
table out, with a note. In carry-rvr (limit_claims), a carried claim is left out when its guild holds a
keep in the live world that the carry does not overwrite (guilds_claim_limit). Everything runs in one
transaction: on an error nothing is changed, and the error is raised.
"""
import datetime

TABLES = ("Keep", "Door", "Relic", "KeepHookPointItem", "KeepCaptureLog")
KEEP_COLUMNS = ("Realm", "Level", "ClaimedGuildName")
IN_PLAY = "Realm IS NOT OriginalRealm OR coalesce(ClaimedGuildName, '') <> ''"
DOOR_COLUMNS = ("Health", "State")
RELIC_KEY = ("RelicID", "relicType", "OriginalRealm")
RELIC_COLUMNS = ("Region", "X", "Y", "Z", "Heading", "Realm", "LastRealm", "LastCaptureDate")
HOOKPOINT = ("KeepID", "ComponentID", "HookPointID")
# The columns each part needs in both worlds.
NEEDED = {
    "Keep": ("KeepID", "Name", "Region", "X", "Y", "BaseLevel", "SkinType", "OriginalRealm", "LastTimeRowUpdated")
            + KEEP_COLUMNS,
    "Zones": ("ZoneID", "RegionID"),
    "Door": ("InternalID", "X", "Y", "LastTimeRowUpdated") + DOOR_COLUMNS,
    "Relic": RELIC_KEY + RELIC_COLUMNS + ("LastTimeRowUpdated",),
    "KeepHookPointItem": HOOKPOINT,
    "KeepCaptureLog": ("DateTaken", "KeepName"),
}
# Keep door health settings and their defaults (serverproperty/ServerProperties.cs). A relic keep has SkinType 99.
DOOR_HEALTH_SETTINGS = {"keep_doors_base_health": 200, "keep_doors_health_upgrade_modifier": 1.0,
                        "relic_doors_health": 180000}
RELIC_SKIN_TYPE = 99
# keeps/KeepArea.cs; a keep is a portal keep from this BaseLevel on (AbstractGameKeep.IsPortalKeep).
PORTAL_KEEP_RADIUS = 4000
KEEP_RADIUS = 3000
PORTAL_KEEP_BASE_LEVEL = 100


def carry(conn, old_db, now=None, limit_claims=False):
    """Copy the RvR state of the world old_db into conn's world. Returns (counts, notes): the rows carried
    per table, and lines on how many keeps and relics matched, and on what was not carried. limit_claims:
    leave out a claim whose guild holds another keep in conn's world (carry-rvr into the live world)."""
    now = now or _now()
    conn.execute("ATTACH DATABASE ? AS old", (old_db,))
    try:
        with conn:
            return _carry(conn, now, limit_claims)
    finally:
        conn.execute("DETACH DATABASE old")


def _carry(conn, now, limit_claims):
    counts, notes = dict.fromkeys(TABLES, 0), []
    why = {table: _missing(conn, table) for table in NEEDED}
    why["Door"] = why["Door"] or why["Zones"] or why["Keep"]
    why["KeepHookPointItem"] = why["KeepHookPointItem"] or why["Keep"]
    pairs = {}
    if not why["Keep"]:
        matched = _match_keeps(conn, notes)
        in_play = {keep_id for (keep_id,) in conn.execute(f"SELECT KeepID FROM old.Keep WHERE {IN_PLAY}")}
        pairs = {old_id: new_id for old_id, new_id in matched.items() if old_id in in_play}
        notes.insert(0, f"Keeps matched by name and region: {len(matched)}, of which {len(pairs)} in play (held by "
                        "another realm than their own, or claimed) and carried")
        _carry_keeps(conn, pairs, limit_claims, notes, now)
        counts["Keep"] = len(pairs)
    if pairs and not why["Door"]:
        counts["Door"] = _carry_doors(conn, pairs, notes, now)
    if not why["Relic"]:
        counts["Relic"] = _carry_relics(conn, notes, now)
    if pairs and not why["KeepHookPointItem"]:
        counts["KeepHookPointItem"] = _carry_hookpoint_items(conn, pairs)
    if not why["KeepCaptureLog"]:
        counts["KeepCaptureLog"] = _carry_capture_log(conn)
    notes.extend(f"{table} not carried: {why[table]}" for table in TABLES if why[table])
    return counts, notes


def _carry_keeps(conn, pairs, limit_claims, notes, now):
    held, overwritten = set(), set(pairs.values())
    if limit_claims:  # the guilds that hold a keep this carry does not overwrite
        held = {guild.lower() for keep_id, guild in conn.execute(
            "SELECT KeepID, ClaimedGuildName FROM main.Keep WHERE coalesce(ClaimedGuildName, '') <> ''")
            if keep_id not in overwritten}
    left_out = []
    for old_id, new_id in pairs.items():
        realm, level, claim = conn.execute(f"SELECT {', '.join(KEEP_COLUMNS)} FROM old.Keep WHERE KeepID=?",
                                           (old_id,)).fetchone()
        if claim and claim.lower() in held:
            name, region = conn.execute("SELECT Name, Region FROM main.Keep WHERE KeepID=?", (new_id,)).fetchone()
            left_out.append(f"{name} (region {region}, {claim})")
            claim = ""
        _update(conn, "Keep", "KeepID=?", (new_id,), KEEP_COLUMNS, (realm, level, claim), now)
    if left_out:
        notes.append("Claims left out, as the guild holds another keep in this world: " + ", ".join(left_out))


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
    settings = _door_health_settings(conn)
    carried = missed = 0
    for old_id, new_id in pairs.items():
        (level,) = conn.execute("SELECT Level FROM old.Keep WHERE KeepID=?", (old_id,)).fetchone()
        base_level, skin_type = conn.execute("SELECT BaseLevel, SkinType FROM main.Keep WHERE KeepID=?",
                                             (new_id,)).fetchone()
        full = door_full_health(base_level, skin_type, level, settings)
        new_doors = new.get(new_id, {})
        for internal_id, rows in old.get(old_id, {}).items():
            targets = new_doors.get(internal_id, [])
            if len(rows) != 1 or len(targets) != 1:
                missed += 1
                continue
            health, state = conn.execute("SELECT Health, State FROM old.Door WHERE rowid=?", (rows[0],)).fetchone()
            _update(conn, "Door", "rowid=?", (targets[0],), DOOR_COLUMNS, (min(health, full), state), now)
            carried += 1
    if missed:
        notes.append(f"Doors of keeps in play not carried (no single door with their InternalID in the keep's "
                     f"area in both worlds): {missed}")
    return carried


def door_full_health(base_level, skin_type, level, settings=DOOR_HEALTH_SETTINGS):
    """A keep door's full health (propertycalc/MaxHealthCalculator.cs, GameKeepDoor): relic_doors_health at a
    relic keep; elsewhere BaseLevel x keep_doors_base_health, plus that x (Level - 1) x
    keep_doors_health_upgrade_modifier, rounded towards zero."""
    if skin_type == RELIC_SKIN_TYPE:
        return settings["relic_doors_health"]
    base = base_level * settings["keep_doors_base_health"]
    return base + int(base * (level - 1) * settings["keep_doors_health_upgrade_modifier"])


def _door_health_settings(conn):
    """The new world's door health settings; the server's default where a row is missing or not a number."""
    settings = dict(DOOR_HEALTH_SETTINGS)
    if _missing_columns(conn, "main", "ServerProperty", ("Key", "Value")) == []:
        for key, value in conn.execute("SELECT lower(`Key`), Value FROM main.ServerProperty"):
            if key in settings:
                try:
                    settings[key] = type(DOOR_HEALTH_SETTINGS[key])(value)
                except (TypeError, ValueError):
                    pass
    return settings


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
    old = conn.execute(f"SELECT {key}, Realm IS NOT OriginalRealm, {', '.join(RELIC_COLUMNS)} FROM old.Relic "
                       "ORDER BY RelicID").fetchall()
    matched = carried = 0
    old_only = []
    for row in old:
        if row[:n] not in new:
            old_only.append(str(row[0]))
            continue
        matched += 1
        if row[n]:  # away from home
            _update(conn, "Relic", " AND ".join(f"{c}=?" for c in RELIC_KEY), row[:n], RELIC_COLUMNS, row[n + 1:], now)
            carried += 1
    notes.append(f"Relics matched: {matched}, of which {carried} away from home and carried")
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


def _missing(conn, table):
    """Why a part cannot be carried: its table, or a column it needs, is missing in a world; None if neither."""
    for schema, world in (("old", "old"), ("main", "new")):
        gone = _missing_columns(conn, schema, table, NEEDED[table])
        if gone is None:
            return f"no {table} table in the {world} world"
        if gone:
            return f"no {', '.join(gone)} column in the {world} world's {table} table"
    return None


def _missing_columns(conn, schema, table, columns):
    """The columns the table lacks, or None when there is no such table."""
    have = {r[1].lower() for r in conn.execute(f'PRAGMA {schema}.table_info("{table}")')}
    return [c for c in columns if c.lower() not in have] if have else None


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
