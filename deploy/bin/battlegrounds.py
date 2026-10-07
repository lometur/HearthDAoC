"""The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era), in the world data.

world_fixes.py calls apply() inside its transaction, after its own fixes. The fix runs once per world:
it records the marker classic-battlegrounds-v1 in the fork table fork_world_fixes, and a world with the
marker is left alone, so changes the owner makes later stay. Every statement, the CREATE TABLE of the
two fork tables included, runs under one savepoint. If any step fails, the savepoint is rolled back
(no change, no fork table, no marker, so the next start tries again) and apply() returns only the
"not applied" line; world_fixes.py still commits its own fixes and the server starts.

Each step changes a value only while it still holds upstream's value, and adds one line to the result
when it changed something:
1. the Battleground rows get the classic level ranges and realm rank caps;
2. Caledon is shown as Caledonia, and no battleground keeps a zone XP bonus;
3. Thidranki Faste and Caer Caledon get base levels for their ranges and keep Level 1, and their gates
   the matching health;
6. Atlas's leftovers go: training dummies, Void Merchants and a stray Wizard (archived first in the
   fork table fork_removed_mobs), and saved quests of the deleted battleground daily quest classes.
"""
import datetime

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


STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step6_atlas_leftovers)


def _archive(conn, now, where, params):
    """Copy the Mob rows that match into the archive table, then delete them; returns how many."""
    archive = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{ARCHIVE_TABLE}")')}
    names = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in archive)
    conn.execute(f"INSERT INTO {ARCHIVE_TABLE} ({names}, FixId, RemovedUtc) "
                 f"SELECT {names}, ?, ? FROM Mob WHERE {where}", (FIX_ID, now, *params))
    return conn.execute(f"DELETE FROM Mob WHERE {where}", params).rowcount


def _count(n, one, many):
    return f"{n} {one if n == 1 else many}"


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
