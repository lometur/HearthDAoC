"""The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era), in the world data.

world_fixes.py calls apply() inside its transaction, after its own fixes. The fix runs once per world:
it records the marker classic-battlegrounds-v2 in the fork table fork_world_fixes, and a world with the
marker is left alone, so changes the owner makes later stay. Every statement, the CREATE TABLE of the
two fork tables included, runs under one savepoint. If any step fails, the savepoint is rolled back
(no change, no fork table, no marker, so the next start tries again) and apply() returns only the
"not applied" line; world_fixes.py still commits its own fixes and the server starts.

v2 is for upstream's 0.35 world. Every world the 0.35 image runs is a fresh 0.35 world (hdc new-world
and hdc upgrade-world start from upstream's clean world, without the fork tables), so v2 expects 0.35's
values, not 0.34's (v1). 0.35 ships central keeps of its own in Abermenai and Murdaigean, Dun Abermenai
(KeepID 33) and Dun Murdaigean (KeepID 32), with their guards; the fork keeps them and only levels them
for the ranges and adds its wall casters and a hastener.

Each step changes a value only while it still holds upstream's value (the keep Level reset in step 3 is
the exception, as the spec requires), adds rows only where none of their kind are there yet, and adds
one line to the result when it changed something:
1. the Battleground rows get the classic level ranges and realm rank caps;
2. Caledon is shown as Caledonia, and no battleground keeps a zone XP bonus;
3. the four central keeps (Dun Abermenai, Thidranki Faste, Dun Murdaigean, Caer Caledon) get base levels
   for their ranges and keep Level 1, and their gates (closed already) the matching full health;
4. the portal keeps of Abermenai and Murdaigean, which have no guards, get a copy of Thidranki's portal
   keep guards and hasteners (all four battlegrounds share one map and the same portal keep spots);
5. Dun Abermenai and Dun Murdaigean get six casters on their walls and a hastener beside their gate:
   Thidranki's Hibernia portal keep casters and hastener, moved onto the central keep model (moved());
6. Atlas's leftovers go: training dummies, Void Merchants and a stray Wizard (archived first in the
   fork table fork_removed_mobs), and saved quests of the deleted battleground daily quest classes.
"""
import datetime
import math

FIX_ID = "classic-battlegrounds-v2"
MARKER_TABLE = "fork_world_fixes"
ARCHIVE_TABLE = "fork_removed_mobs"
SAVEPOINT = "classic_battlegrounds"
NEEDED_TABLES = ("Battleground", "Keep", "Mob", "Door", "Zones", "Regions", "Quest")
NOT_APPLIED = "Classic battlegrounds: not applied ({}); the battlegrounds stay as upstream ships them"
NAMES = {253: "Abermenai", 252: "Thidranki", 251: "Murdaigean", 250: "Caledonia"}
ORDER = (253, 252, 251, 250)

# Step 1, by RegionID: (label, MinLevel, MaxLevel, MaxRealmLevel) as upstream 0.35 ships it, then classic.
# MaxRealmLevel means "must be below": 3 is up to 1L2 (under 125 realm points), 4 up to 1L3 (350),
# 6 up to 1L5 (1,375) and 10 up to 1L9 (7,125).
BATTLEGROUND_ROWS = {
    253: (("Abermenai (Level 15-19)", 15, 19, 2), ("Abermenai (Level 15-19 - RR1L2)", 15, 19, 3)),
    252: (("Thidranki (Level 20-24 - RR2L0)", 20, 24, 10), ("Thidranki (Level 20-24 - RR1L3)", 20, 24, 4)),
    251: (("Murdaigean (Level 25-29)", 25, 29, 5), ("Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6)),
    250: (("Caledonia (Level 34-39 - RR3L5)", 30, 35, 25), ("Caledonia (Level 30-35 - RR1L9)", 30, 35, 10)),
}
# Step 2: the zones whose Experience is 50 upstream, in ORDER (251 and 253 are already 0).
XP_BONUS_ZONES = (252, 250)
# Step 3, by KeepID, in ORDER: (Region, name, upstream BaseLevel, classic BaseLevel). At keep Level 1, with
# keep_guard_level_multiplier 1.6, the server makes the guards BaseLevel + 2 and the lord
# BaseLevel + (BaseLevel / 10 + 1) x 2 + 1 (spec 3.4): 21 and 24 in Dun Abermenai, 31 and 36 in Dun Murdaigean.
KEEP_LEVELS = {33: (253, "Dun Abermenai", 21, 19), 11: (252, "Thidranki Faste", 26, 24),
               32: (251, "Dun Murdaigean", 31, 29), 31: (250, "Caer Caledon", 46, 35)}
# Their gates (outer and inner Door.InternalID) go from upstream's full health to the new one, BaseLevel x
# keep_doors_base_health 200 (4,200 to 3,800 in Dun Abermenai), only once the keep has the new BaseLevel.
GATES = {33: (253000301, 253000302), 11: (252000301, 252000302), 32: (251000301, 251000302),
         31: (250000301, 250000302)}
KEEP_DOORS_BASE_HEALTH = 200
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

# The keep areas (keeps/KeepArea.cs): a guard within this radius of a keep's X, Y belongs to that keep. A
# portal keep is a keep with BaseLevel 100 or more (AbstractGameKeep.IsPortalKeep).
PORTAL_KEEP_RADIUS = 4000
KEEP_RADIUS = 3000
# Step 4: Thidranki's portal keep guards and hasteners are the Mob rows of these classes in this region
# within the portal keep area of these KeepIDs. They are copied into the regions in PORTAL_KEEP_TARGETS.
PORTAL_KEEP_SOURCE = (252, (12, 13, 14))   # region, KeepIDs
KEEP_GUARD_CLASSES = ("DOL.GS.Keeps.FrontierHastener", "DOL.GS.Keeps.GuardFighter", "DOL.GS.Keeps.GuardStaticCaster")
PORTAL_KEEP_TARGETS = (253, 251)
# Step 5, by region: upstream's central keep (KeepID) that gets the wall casters and the hastener, and its name.
CENTRAL_KEEPS = {253: (33, "Dun Abermenai"), 251: (32, "Dun Murdaigean")}
CASTER_CLASS = "DOL.GS.Keeps.GuardStaticCaster"
HASTENER_CLASS = "DOL.GS.Keeps.FrontierHastener"
# Thidranki's Hibernia portal keep rows that stand on its model, moved onto a central keep: its six casters on
# the walls (Z 4736), and its hastener beside the outer gate (on the floor, Z 4320).
WALL_CASTERS = ("62f874d0-333b-475f-a044-109cb0bd74b6", "be8e2cbf-6569-4c46-a4aa-d84903a902fc",
                "3a07da41-d088-4174-980f-1d5ad21fc334", "2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a",
                "b05f95a5-9e55-4ddf-93d0-340336bc2e16", "f1f1d987-1b9a-421b-a8a1-9df423f118fe")
HASTENER = "802a1b0a-f47e-47b9-a688-e401ad33e42f"
# Each kind is added on its own: only when all its rows are there and the keep's area has no row of its class
# yet. (class, source Mob_IDs, the result line's word for one, for more)
CENTRAL_ROWS = ((CASTER_CLASS, WALL_CASTERS, "wall caster", "wall casters"),
                (HASTENER_CLASS, (HASTENER,), "hastener", "hasteners"))

# Step 5, the move (spec 3.2): a spot on Thidranki's Hibernia portal keep model goes onto a central keep
# model. P is the portal keep model's origin. By region, C is the central model's origin and the angle
# (degrees, from +X towards +Y) is how far that model is turned from the portal keep's. The central
# keep's floor is FLOOR_DROP lower (3720 against 4320, from the door rows), so a caster on the portal keep's
# walls (4736) stands at 4136 on the central keep's, where upstream 0.35 puts its own wall-top guards (4137),
# and the hastener (4320) at 3720, beside the outer gate, where upstream's gate guards stand at 3719 to 3746.
P = (18048, 18176)
FLOOR_DROP = 600
MOVES = {253: ((33152, 38400), 30), 251: ((33408, 38272), 190)}


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
    for keep_id, (region, _, old, new) in KEEP_LEVELS.items():
        gates += conn.execute("UPDATE Door SET Health=?, LastTimeRowUpdated=? WHERE InternalID IN (?, ?) AND Health=? "
                              "AND EXISTS (SELECT 1 FROM Keep WHERE KeepID=? AND Region=? AND BaseLevel=?)",
                              (new * KEEP_DOORS_BASE_HEALTH, now, *GATES[keep_id], old * KEEP_DOORS_BASE_HEALTH,
                               keep_id, region, new)).rowcount
    if gates:
        items.append(_count(gates, "gate's health", "gates' health"))
    return "Battlegrounds: keep levels for the ranges (" + ", ".join(items) + ")" if items else None


def _step4_portal_keep_guards(conn, now):
    source, keeps = PORTAL_KEEP_SOURCE
    radius = PORTAL_KEEP_RADIUS * PORTAL_KEEP_RADIUS
    items = []
    for region in PORTAL_KEEP_TARGETS:
        # Only the portal keeps' areas count: upstream 0.35's central keep guards are DOL.GS.Keeps.* rows too.
        if conn.execute("SELECT 1 FROM Mob m JOIN Keep k ON k.Region=m.Region AND k.BaseLevel>=100 AND "
                        "(m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= ? "
                        "WHERE m.Region=? AND m.ClassType LIKE 'DOL.GS.Keeps.%'", (radius, region)).fetchone():
            continue  # a portal keep has keep guards already: the owner's, or an earlier run's
        added = _copy_mobs(conn, {"Region": ("?", region), "Mob_ID": ("? || m.Mob_ID", f"hdc-bg{region}-pk-"),
                                  "LastTimeRowUpdated": ("?", now)},
                           "m.Region=? AND m.ClassType IN (?, ?, ?) AND EXISTS (SELECT 1 FROM Keep k WHERE "
                           "k.Region=m.Region AND k.KeepID IN (?, ?, ?) AND "
                           "(m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= ?) "
                           "AND NOT EXISTS (SELECT 1 FROM Mob x WHERE x.Mob_ID = ? || m.Mob_ID)",
                           (source, *KEEP_GUARD_CLASSES, *keeps, radius, f"hdc-bg{region}-pk-"))
        if added:
            items.append(f"{NAMES[region]} ({added})")
    return "Battlegrounds: portal keep guards and hasteners for " + ", ".join(items) if items else None


def _step5_central_keep_guards(conn, now):
    items = []
    for region, (keep_id, name) in CENTRAL_KEEPS.items():
        keep = conn.execute("SELECT X, Y FROM Keep WHERE KeepID=? AND Region=?", (keep_id, region)).fetchone()
        if keep is None:
            continue  # the keep is missing: leave this region as it is
        added = []
        for class_type, sources, one, many in CENTRAL_ROWS:
            n = _move_onto_central_keep(conn, now, region, keep, class_type, sources)
            if n:
                added.append(_count(n, one, many))
        if added:
            items.append(f"{name} (" + ", ".join(added) + ")")
    return "Battlegrounds: central keep guards for " + ", ".join(items) if items else None


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
         _step5_central_keep_guards, _step6_atlas_leftovers)


def _archive(conn, now, where, params):
    """Copy the Mob rows that match into the archive table, then delete them; returns how many."""
    archive = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{ARCHIVE_TABLE}")')}
    names = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in archive)
    conn.execute(f"INSERT INTO {ARCHIVE_TABLE} ({names}, FixId, RemovedUtc) "
                 f"SELECT {names}, ?, ? FROM Mob WHERE {where}", (FIX_ID, now, *params))
    return conn.execute(f"DELETE FROM Mob WHERE {where}", params).rowcount


def _move_onto_central_keep(conn, now, region, keep, class_type, sources):
    """Add a copy of each source row (all of class_type), moved onto region's central keep model, whose Keep row
    stands at keep (X, Y). Adds nothing when a source row is missing or the keep's area has a class_type row
    already (the owner's, or an earlier run's), and each copy only if its Mob_ID is free. Returns how many."""
    spots = {mob_id: conn.execute("SELECT X, Y, Z, Heading FROM Mob WHERE Mob_ID=?", (mob_id,)).fetchone()
             for mob_id in sources}
    if None in spots.values():
        return 0
    if conn.execute("SELECT 1 FROM Mob WHERE Region=? AND ClassType=? AND (X-?)*(X-?) + (Y-?)*(Y-?) <= ?",
                    (region, class_type, keep[0], keep[0], keep[1], keep[1], KEEP_RADIUS * KEEP_RADIUS)).fetchone():
        return 0
    added = 0
    for mob_id, spot in spots.items():
        new_id = f"hdc-bg{region}-ck-{mob_id}"
        x, y, z, heading = moved(region, *spot)
        added += _copy_mobs(conn, {"Region": ("?", region), "Mob_ID": ("?", new_id),
                                   "LastTimeRowUpdated": ("?", now), "X": ("?", x), "Y": ("?", y),
                                   "Z": ("?", z), "Heading": ("?", heading)},
                            "m.Mob_ID=? AND NOT EXISTS (SELECT 1 FROM Mob WHERE Mob_ID=?)", (mob_id, new_id))
    return added


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
