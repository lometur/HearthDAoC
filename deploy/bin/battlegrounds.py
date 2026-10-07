"""The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era), in the world data.

world_fixes.py calls apply() inside its transaction, after its own fixes. The fix runs once per world:
it records the marker classic-battlegrounds-v1 in the fork table fork_world_fixes, and a world with the
marker is left alone, so changes the owner makes later stay. Every statement, the CREATE TABLE of the
two fork tables included, runs under one savepoint. If any step fails, the savepoint is rolled back
(no change, no fork table, no marker, so the next start tries again) and apply() returns only the
"not applied" line; world_fixes.py still commits its own fixes and the server starts.

Each step changes a value only while it still holds upstream's value, adds rows only where none of their
kind are there yet, and adds one line to the result when it changed something:
1. the Battleground rows get the classic level ranges and realm rank caps;
2. Caledon is shown as Caledonia, and no battleground keeps a zone XP bonus;
3. Thidranki Faste and Caer Caledon get base levels for their ranges and keep Level 1, and their gates
   the matching health;
4. Abermenai and Murdaigean, which have no guards, get a copy of Thidranki's portal keep guards and
   hasteners (all four battlegrounds share one map and the same portal keep spots);
5. they also get a central keep each, Dun Abermenai and Dun Murdaigean, held by renegades. Its guards
   are Thidranki's Hibernia portal keep guards moved onto the central keep model (moved()), four
   fighters at its gate (gate_spots()) and a lord. They are added only in the run that adds the keep's
   Keep row, and only those not there yet. Its doors are closed at full health;
6. Atlas's leftovers go: training dummies, Void Merchants and a stray Wizard (archived first in the
   fork table fork_removed_mobs), and saved quests of the deleted battleground daily quest classes.
"""
import datetime
import math

FIX_ID = "classic-battlegrounds-v1"
MARKER_TABLE = "fork_world_fixes"
ARCHIVE_TABLE = "fork_removed_mobs"
SAVEPOINT = "classic_battlegrounds"
NEEDED_TABLES = ("Battleground", "Keep", "Mob", "Door", "Zones", "Regions", "Quest")
NOT_APPLIED = "Classic battlegrounds: not applied ({}); the battlegrounds stay as upstream ships them"
NAMES = {253: "Abermenai", 252: "Thidranki", 251: "Murdaigean", 250: "Caledonia"}
ORDER = (253, 252, 251, 250)

# Step 1, by RegionID: (label, MinLevel, MaxLevel, MaxRealmLevel) as upstream ships it, then classic.
# MaxRealmLevel means "must be below": 3 is up to 1L2 (under 125 realm points), 4 up to 1L3 (350),
# 6 up to 1L5 (1,375) and 10 up to 1L9 (7,125).
BATTLEGROUND_ROWS = {
    253: (("Abermenai (Level 15-19)", 15, 19, 2), ("Abermenai (Level 15-19 - RR1L2)", 15, 19, 3)),
    252: (("Thidranki (Level 20-24 - RR2L0)", 20, 24, 10), ("Thidranki (Level 20-24 - RR1L3)", 20, 24, 4)),
    251: (("Murdaigean (Level 25-29)", 25, 29, 5), ("Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6)),
    250: (("Caledonia (Level 34-39 - RR3L5)", 30, 34, 25), ("Caledonia (Level 30-35 - RR1L9)", 30, 35, 10)),
}
# Step 2: the zones whose Experience is 50 upstream, in ORDER (251 and 253 are already 0).
XP_BONUS_ZONES = (252, 250)
# Step 3, by KeepID: (Region, name, upstream BaseLevel, classic BaseLevel).
KEEP_LEVELS = {11: (252, "Thidranki Faste", 26, 24), 31: (250, "Caer Caledon", 46, 35)}
# Their gates, by Door.InternalID: (upstream Health, the new BaseLevel x keep_doors_base_health 200).
GATE_HEALTH = {252000301: (5200, 4800), 252000302: (5200, 4800), 250000301: (9200, 7000), 250000302: (9200, 7000)}
# Step 6.
QUEST_CLASSES = (
    "DOL.GS.DailyQuest.Albion.CaleKeepCaptureAlb", "DOL.GS.DailyQuest.Hibernia.CaleKeepCaptureHib",
    "DOL.GS.DailyQuest.Midgard.CaleKeepCaptureMid", "DOL.GS.DailyQuest.Albion.CaleKillQuestAlb",
    "DOL.GS.DailyQuest.Hibernia.CaleKillQuestHib", "DOL.GS.DailyQuest.Hibernia.CaleKillQuestMid",
    "DOL.GS.DailyQuest.Albion.ThidKeepCaptureAlb", "DOL.GS.DailyQuest.Hibernia.ThidKeepCaptureHib",
    "DOL.GS.DailyQuest.Midgard.ThidKeepCaptureMid", "DOL.GS.DailyQuest.Albion.ThidKillQuestAlb",
    "DOL.GS.DailyQuest.Hibernia.ThidKillQuestHib", "DOL.GS.DailyQuest.Hibernia.ThidKillQuestMid",
)
DUMMY_CLASSES = ("DOL.GS.DPSDummy", "DOL.GS.HitbackDummy", "DOL.GS.HealDummy")
VOID_MERCHANT_CLASS = "DOL.GS.Scripts.RPTradeInMerchant"
STRAY_WIZARD = {"Mob_ID": "caledon-guard-25", "ClassType": "DOL.GS.Keeps.GuardStaticCaster", "Name": "Wizard",
                "Realm": 1, "Level": 48, "X": 33185, "Y": 37386, "Z": 3722, "Region": 250}
IN_BATTLEGROUNDS = "Region BETWEEN 250 AND 253"

# Step 4: Thidranki's portal keep guards and hasteners are the Mob rows of these classes in this region
# within the portal keep area's radius (keeps/KeepArea.cs) of these KeepIDs.
PORTAL_KEEP_SOURCE = (252, (12, 13, 14), 4000)   # region, KeepIDs, radius
KEEP_GUARD_CLASSES = ("DOL.GS.Keeps.FrontierHastener", "DOL.GS.Keeps.GuardFighter", "DOL.GS.Keeps.GuardStaticCaster")
# Step 5, by region, in the order they are added: the central keep's Name, Keep_ID and BaseLevel (the top
# of the range). Its KeepID is the first free one from FIRST_KEEP_ID.
CENTRAL_KEEPS = {253: ("Dun Abermenai", "hdc-bg253-dun-abermenai", 19),
                 251: ("Dun Murdaigean", "hdc-bg251-dun-murdaigean", 29)}
FIRST_KEEP_ID = 32
KEEP_CREATE_INFO = "HearthDAoC classic-battlegrounds-v1"
PORTAL_KEEP_HIB = 12          # its Keep row is moved to make the central Keep row's X, Y, Z, Heading
# The Hibernia portal keep's rows that stand on its model, moved onto the central keep: its hastener (on
# the floor, Z 4320) and its six casters on the walls (Z 4736).
CENTRAL_SOURCES = ("802a1b0a-f47e-47b9-a688-e401ad33e42f",
                   "62f874d0-333b-475f-a044-109cb0bd74b6", "be8e2cbf-6569-4c46-a4aa-d84903a902fc",
                   "3a07da41-d088-4174-980f-1d5ad21fc334", "2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a",
                   "b05f95a5-9e55-4ddf-93d0-340336bc2e16", "f1f1d987-1b9a-421b-a8a1-9df423f118fe")
FIGHTER_TEMPLATE = "b67eacce-2719-48a9-8be7-1dbf0c16b7d2"   # a Hibernia portal keep fighter
LORD_TEMPLATE = "863582fc-af9c-4661-8e60-4d8b2985ad2a"      # Thidranki Faste's lord
# The central doors, by region: outer and inner Door.InternalID, then Health as upstream ships it and the
# keep's full health (BaseLevel x keep_doors_base_health 200). Their State goes from 0 (open) to 1 (closed).
CENTRAL_DOORS = {253: (253000301, 253000302, 2545, 3800), 251: (251000301, 251000302, 2545, 5800)}

# Step 5, the move (spec 3.2): a spot on Thidranki's Hibernia portal keep model goes onto a central keep
# model. P is the portal keep model's origin. By region, C is the central model's origin and the angle
# (degrees, from +X towards +Y) is how far that model is turned from the portal keep's. The central
# keep's floor is FLOOR_DROP lower (3720 against 4320, from the door rows).
P = (18048, 18176)
FLOOR_DROP = 600
MOVES = {253: ((33152, 38400), 30), 251: ((33408, 38272), 190)}
# The four fighters at a central keep's gate stand GATE_SIDE to either side of the line through its two
# doors: two in the passage between the doors, and two GATE_INSIDE inside the inner door.
GATE_SIDE = 60
GATE_INSIDE = 150


def apply(conn, now=None):
    """Apply the classic battlegrounds once per world; returns one line per step that changed something.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open,
    the savepoint is the transaction, and its release commits it."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if any(table.lower() not in tables for table in NEEDED_TABLES):
        return []
    if MARKER_TABLE in tables and conn.execute(f"SELECT 1 FROM {MARKER_TABLE} WHERE FixId=?", (FIX_ID,)).fetchone():
        return []
    now = now or _now()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        conn.execute(f"CREATE TABLE IF NOT EXISTS {MARKER_TABLE} (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)")
        mob = conn.execute('PRAGMA table_info("Mob")').fetchall()
        columns = ", ".join(f'"{name}" {declared}' for _, name, declared, *_ in mob)
        conn.execute(f"CREATE TABLE IF NOT EXISTS {ARCHIVE_TABLE} ({columns}, FixId TEXT NOT NULL, "
                     "RemovedUtc TEXT NOT NULL)")
        lines = []
        for step in STEPS:
            line = step(conn, now)
            if line:
                lines.append(line)
        conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (FIX_ID, now))
    except Exception as e:  # any failure: undo the whole fix, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    return lines


def moved(region, x, y, z, heading):
    """A spot on Thidranki's Hibernia portal keep model, moved onto region's central keep model (spec 3.2,
    "The move"): turned about P by the region's angle and put at C, FLOOR_DROP lower, with the heading
    turned by the same angle. Returns whole units: (x, y, z, heading)."""
    (cx, cy), degrees = MOVES[region]
    angle = math.radians(degrees)
    dx, dy = x - P[0], y - P[1]
    return (round(cx + dx * math.cos(angle) - dy * math.sin(angle)),
            round(cy + dx * math.sin(angle) + dy * math.cos(angle)),
            z - FLOOR_DROP,
            (heading + round(degrees * 4096 / 360)) % 4096)


def facing(frm, to):
    """The heading (0 to 4095) of something at frm that faces to, as the server reckons headings
    (world/Point2D.cs GetHeading: 0 towards +Y, turning the same way as the angles in MOVES), rounded."""
    return round(math.atan2(-(to[0] - frm[0]), to[1] - frm[1]) * 4096 / (2 * math.pi)) % 4096


def gate_spots(outer, inner):
    """The four fighters' spots at a central keep's gate, from its outer and inner door rows (x, y, z): two
    in the passage between the doors, at its midpoint, then two GATE_INSIDE inside the inner door; each pair
    GATE_SIDE to either side of the gate's line, at the inner door's height, facing out through the gate."""
    length = math.hypot(outer[0] - inner[0], outer[1] - inner[1])
    ux, uy = (outer[0] - inner[0]) / length, (outer[1] - inner[1]) / length
    sx, sy = -uy * GATE_SIDE, ux * GATE_SIDE
    mx, my = (outer[0] + inner[0]) / 2, (outer[1] + inner[1]) / 2
    ix, iy = inner[0] - GATE_INSIDE * ux, inner[1] - GATE_INSIDE * uy
    heading = facing(inner, outer)
    return [(round(x), round(y), inner[2], heading)
            for x, y in ((mx + sx, my + sy), (mx - sx, my - sy), (ix + sx, iy + sy), (ix - sx, iy - sy))]


def _step1_battleground_rows(conn, now):
    changed = []
    for region in ORDER:
        old, new = BATTLEGROUND_ROWS[region]
        cur = conn.execute("UPDATE Battleground SET Battleground_ID=?, MinLevel=?, MaxLevel=?, MaxRealmLevel=?, "
                           "LastTimeRowUpdated=? WHERE RegionID=? AND Battleground_ID=? AND MinLevel=? AND MaxLevel=? "
                           "AND MaxRealmLevel=?", (*new, now, region, *old))
        if cur.rowcount:
            changed.append(NAMES[region])
    return "Battlegrounds: classic level and realm rank limits for " + ", ".join(changed) if changed else None


def _step2_names_and_xp(conn, now):
    parts = []
    renamed = conn.execute("UPDATE Zones SET Name='Caledonia', LastTimeRowUpdated=? "
                           "WHERE ZoneID=250 AND Name='Caledon'", (now,)).rowcount
    renamed += conn.execute("UPDATE Regions SET Description='Caledonia', LastTimeRowUpdated=? "
                            "WHERE RegionID=250 AND Description='Caledon'", (now,)).rowcount
    if renamed:
        parts.append("Caledon is shown as Caledonia")
    no_bonus = []
    for zone in XP_BONUS_ZONES:
        if conn.execute("UPDATE Zones SET Experience=0, LastTimeRowUpdated=? WHERE ZoneID=? AND Experience=50",
                        (now, zone)).rowcount:
            no_bonus.append(NAMES[zone])
    if no_bonus:
        parts.append("no zone XP bonus in " + ", ".join(no_bonus))
    return "Battlegrounds: " + "; ".join(parts) if parts else None


def _step3_keep_levels(conn, now):
    items = []
    for keep_id, (region, name, old, new) in KEEP_LEVELS.items():
        if conn.execute("UPDATE Keep SET BaseLevel=?, LastTimeRowUpdated=? WHERE KeepID=? AND Region=? AND BaseLevel=?",
                        (new, now, keep_id, region, old)).rowcount:
            items.append(f"{name} base level {new}")
    for keep_id, (region, name, _, _) in KEEP_LEVELS.items():  # a capture on this world left it higher
        if conn.execute("UPDATE Keep SET Level=1, LastTimeRowUpdated=? WHERE KeepID=? AND Region=? AND Level>1",
                        (now, keep_id, region)).rowcount:
            items.append(f"{name} back to level 1")
    gates = 0
    for door, (old, new) in GATE_HEALTH.items():
        gates += conn.execute("UPDATE Door SET Health=?, LastTimeRowUpdated=? WHERE InternalID=? AND Health=?",
                              (new, now, door, old)).rowcount
    if gates:
        items.append(_count(gates, "gate's health", "gates' health"))
    return "Battlegrounds: keep levels for the ranges (" + ", ".join(items) + ")" if items else None


def _step4_portal_keep_guards(conn, now):
    source, keeps, radius = PORTAL_KEEP_SOURCE
    items = []
    for region in CENTRAL_KEEPS:
        if conn.execute("SELECT 1 FROM Mob WHERE Region=? AND ClassType LIKE 'DOL.GS.Keeps.%'", (region,)).fetchone():
            continue  # it has keep guards already: the owner's, or an earlier run's
        added = _copy_mobs(conn, {"Region": ("?", region), "Mob_ID": ("? || m.Mob_ID", f"hdc-bg{region}-pk-"),
                                  "LastTimeRowUpdated": ("?", now)},
                           "m.Region=? AND m.ClassType IN (?, ?, ?) AND EXISTS (SELECT 1 FROM Keep k WHERE "
                           "k.Region=m.Region AND k.KeepID IN (?, ?, ?) AND "
                           "(m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= ?)",
                           (source, *KEEP_GUARD_CLASSES, *keeps, radius * radius))
        if added:
            items.append(f"{NAMES[region]} ({added})")
    return "Battlegrounds: portal keep guards and hasteners for " + ", ".join(items) if items else None


def _step5_central_keeps(conn, now):
    keeps, doors = [], 0
    for region, (name, keep_key, base_level) in CENTRAL_KEEPS.items():
        outer, inner, old_health, new_health = CENTRAL_DOORS[region]
        portal_keep = conn.execute("SELECT X, Y, Z, Heading FROM Keep WHERE KeepID=?", (PORTAL_KEEP_HIB,)).fetchone()
        spots = {door: conn.execute("SELECT X, Y, Z FROM Door WHERE InternalID=?", (door,)).fetchone()
                 for door in (outer, inner)}
        sources = {mob_id: conn.execute("SELECT X, Y, Z, Heading FROM Mob WHERE Mob_ID=?", (mob_id,)).fetchone()
                   for mob_id in CENTRAL_SOURCES + (FIGHTER_TEMPLATE, LORD_TEMPLATE)}
        if portal_keep is None or None in spots.values() or None in sources.values():
            continue  # a row the keep is made from is missing: leave this region as it is
        if not conn.execute("SELECT 1 FROM Keep WHERE Region=? AND BaseLevel<100", (region,)).fetchone():
            keep_id = FIRST_KEEP_ID
            while conn.execute("SELECT 1 FROM Keep WHERE KeepID=?", (keep_id,)).fetchone():
                keep_id += 1
            x, y, z, heading = moved(region, *portal_keep)
            conn.execute("INSERT INTO Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, "
                         "AlbionDifficultyLevel, MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, "
                         "KeepType, BaseLevel, SkinType, CreateInfo, LastTimeRowUpdated, Keep_ID) "
                         "VALUES (?, ?, ?, ?, ?, ?, ?, 0, 1, '', 1, 1, 1, 0, 0, ?, 0, ?, ?, ?)",
                         (keep_id, name, region, x, y, z, heading, base_level, KEEP_CREATE_INFO, now, keep_key))
            gate = gate_spots(spots[outer], spots[inner])
            rows = [(f"hdc-bg{region}-ck-{mob_id}", mob_id, moved(region, *sources[mob_id]))
                    for mob_id in CENTRAL_SOURCES]
            rows += [(f"hdc-bg{region}-ck-fighter-{n}", FIGHTER_TEMPLATE, spot) for n, spot in enumerate(gate, 1)]
            rows.append((f"hdc-bg{region}-ck-lord", LORD_TEMPLATE, (x, y, z, gate[0][3])))  # facing the gate
            guards = 0
            for new_id, template, (gx, gy, gz, gh) in rows:
                guards += _copy_mobs(conn, {"Region": ("?", region), "Mob_ID": ("?", new_id),
                                            "LastTimeRowUpdated": ("?", now), "X": ("?", gx), "Y": ("?", gy),
                                            "Z": ("?", gz), "Heading": ("?", gh)},
                                     "m.Mob_ID=? AND NOT EXISTS (SELECT 1 FROM Mob WHERE Mob_ID=?)", (template, new_id))
            keeps.append(f"{name} (keep {keep_id}, {_count(guards, 'guard', 'guards')})")
        doors += conn.execute("UPDATE Door SET Health=CASE WHEN Health=? THEN ? ELSE Health END, "
                              "State=CASE WHEN State=0 THEN 1 ELSE State END, LastTimeRowUpdated=? "
                              "WHERE InternalID IN (?, ?) AND (Health=? OR State=0)",
                              (old_health, new_health, now, outer, inner, old_health)).rowcount
    parts = []
    if keeps:
        parts.append(("central keep " if len(keeps) == 1 else "central keeps ") + ", ".join(keeps))
    if doors:
        parts.append(_count(doors, "central door", "central doors") + " closed at full health")
    return "Battlegrounds: " + "; ".join(parts) if parts else None


def _step6_atlas_leftovers(conn, now):
    dummies = _archive(conn, now, f"ClassType IN (?, ?, ?) AND {IN_BATTLEGROUNDS}", DUMMY_CLASSES)
    merchants = _archive(conn, now, f"ClassType=? AND {IN_BATTLEGROUNDS}", (VOID_MERCHANT_CLASS,))
    wizard = _archive(conn, now, " AND ".join(f"{column}=?" for column in STRAY_WIZARD), tuple(STRAY_WIZARD.values()))
    quests = conn.execute("DELETE FROM Quest WHERE Name IN (%s)" % ", ".join("?" * len(QUEST_CLASSES)),
                          QUEST_CLASSES).rowcount
    removed = []
    if dummies:
        removed.append(_count(dummies, "training dummy", "training dummies"))
    if merchants:
        removed.append(_count(merchants, "Void Merchant", "Void Merchants"))
    if wizard:
        removed.append("the stray Wizard")
    parts = []
    if removed:
        parts.append(f"Atlas leftovers archived in {ARCHIVE_TABLE} and removed (" + ", ".join(removed) + ")")
    if quests:
        parts.append(_count(quests, "saved battleground daily quest", "saved battleground daily quests") + " deleted")
    return "Battlegrounds: " + "; ".join(parts) if parts else None


STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step4_portal_keep_guards,
         _step5_central_keeps, _step6_atlas_leftovers)


def _archive(conn, now, where, params):
    """Copy the Mob rows that match into the archive table, then delete them; returns how many."""
    archive = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{ARCHIVE_TABLE}")')}
    names = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in archive)
    conn.execute(f"INSERT INTO {ARCHIVE_TABLE} ({names}, FixId, RemovedUtc) "
                 f"SELECT {names}, ?, ? FROM Mob WHERE {where}", (FIX_ID, now, *params))
    return conn.execute(f"DELETE FROM Mob WHERE {where}", params).rowcount


def _copy_mobs(conn, replace, where, params):
    """Add a copy of each Mob row m that matches where. Every column is copied as it is, except those in
    replace, which maps a column to (an SQL expression with one ?, its value). Returns how many rows it added."""
    names = [name for _, name, *_ in conn.execute('PRAGMA table_info("Mob")')]
    select = ", ".join(replace[name][0] if name in replace else f'm."{name}"' for name in names)
    values = tuple(replace[name][1] for name in names if name in replace)
    columns = ", ".join(f'"{name}"' for name in names)
    return conn.execute(f"INSERT INTO Mob ({columns}) SELECT {select} FROM Mob m WHERE {where}",
                        values + tuple(params)).rowcount


def _count(n, one, many):
    return f"{n} {one if n == 1 else many}"


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
