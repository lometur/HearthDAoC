"""deploy/bin/carry_rvr.py: the keep, door and relic state, hookpoint items and capture log carried from one
world into another (upgrade-world and carry-rvr).

The rule tests run on small made-up worlds. With HDC_TEST_WORLD (a clean classic 0.35 world), a copy with
captures carries into a fresh copy, and the world fixes run after it. With HDC_TEST_WORLD_OLD as well (a
clean classic 0.34 world), the 0.34 world carries into the 0.35 one.
"""
import os
import shutil
import sqlite3
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "bin"))

import carry_rvr  # noqa: E402
import world_fixes  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
TEST_WORLD_OLD = os.environ.get("HDC_TEST_WORLD_OLD")
NOW = "2026-10-09 12:00:00"
# The tables as upstream's worlds have them, with fewer columns.
SCHEMA = (
    "CREATE TABLE Keep (KeepID INT(11) NOT NULL DEFAULT 0, Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "Region UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, X INT(11) NOT NULL DEFAULT 0, Y INT(11) NOT NULL DEFAULT 0, "
    "Realm UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, Level UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, "
    "ClaimedGuildName TEXT DEFAULT NULL COLLATE NOCASE, OriginalRealm INT(11) NOT NULL DEFAULT 0, "
    "BaseLevel UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, SkinType UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, "
    "CreateInfo VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (KeepID))",
    "CREATE TABLE Door (Name TEXT DEFAULT NULL COLLATE NOCASE, X INT(11) NOT NULL DEFAULT 0, "
    "Y INT(11) NOT NULL DEFAULT 0, InternalID INT(11) NOT NULL DEFAULT 0, Guild TEXT DEFAULT NULL COLLATE NOCASE, Level UNSIGNED TINYINT(3) NOT NULL "
    "DEFAULT 0, Realm UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, Locked INT(11) NOT NULL DEFAULT 0, Health INT(11) NOT NULL "
    "DEFAULT 0, IsPostern TINYINT(1) NOT NULL DEFAULT 0, State INT(11) NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', Door_ID VARCHAR(255) NOT NULL DEFAULT '' "
    "COLLATE NOCASE, PRIMARY KEY (Door_ID))",
    "CREATE TABLE Zones (ZoneID INT(11) NOT NULL DEFAULT 0, RegionID UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, "
    "PRIMARY KEY (ZoneID))",
    "CREATE TABLE Relic (RelicID INT(11) NOT NULL DEFAULT 0, Region INT(11) NOT NULL DEFAULT 0, X INT(11) NOT NULL "
    "DEFAULT 0, Y INT(11) NOT NULL DEFAULT 0, Z INT(11) NOT NULL DEFAULT 0, Heading INT(11) NOT NULL DEFAULT 0, "
    "Realm INT(11) NOT NULL DEFAULT 0, OriginalRealm INT(11) NOT NULL DEFAULT 0, LastRealm INT(11) NOT NULL DEFAULT 0, "
    "relicType INT(11) NOT NULL DEFAULT 0, LastCaptureDate DATETIME DEFAULT NULL, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (RelicID))",
    "CREATE TABLE KeepHookPointItem (KeepID INT(11) NOT NULL DEFAULT 0, ComponentID INT(11) NOT NULL DEFAULT 0, "
    "HookPointID INT(11) NOT NULL DEFAULT 0, ClassType TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', KeepHookPointItem_ID VARCHAR(255) NOT NULL "
    "DEFAULT '' COLLATE NOCASE, PRIMARY KEY (KeepHookPointItem_ID))",
    "CREATE TABLE KeepCaptureLog (ID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, DateTaken DATETIME NOT NULL DEFAULT "
    "'2000-01-01 00:00:00', KeepName TEXT NOT NULL DEFAULT '' COLLATE NOCASE, CapturedBy TEXT NOT NULL DEFAULT '' "
    "COLLATE NOCASE, RPReward INT(11) NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT "
    "'2000-01-01 00:00:00')",
)
ZONES = ((15, 1), (16, 1), (2, 2), (253, 253))
# Caer Benowyc's area holds two doors; a door 3,500 away is outside it, as is one in zone 2 (region 2); the
# portal keep's area (4,000) holds a door 3,500 away; Dun Abermenai holds its two gates.
KEEPS = (
    dict(KeepID=50, Name="Caer Benowyc", Region=1, X=100000, Y=100000, Realm=1, Level=5, OriginalRealm=1, BaseLevel=50),
    dict(KeepID=22, Name="Hibernia Portal Keep", Region=1, X=200000, Y=200000, Realm=3, Level=1, OriginalRealm=3,
         BaseLevel=255),
    dict(KeepID=33, Name="Dun Abermenai", Region=253, X=33089, Y=38271, Realm=0, Level=1, BaseLevel=21),
)
DOORS = (
    dict(Door_ID="d1", InternalID=15000101, X=100500, Y=100500, Health=50000, State=1, Realm=6, Level=20),
    dict(Door_ID="d2", InternalID=15000102, X=99000, Y=101000, Health=2545, State=0, IsPostern=1, Realm=6, Level=20),
    dict(Door_ID="d3", InternalID=16000101, X=103500, Y=100000, Health=40000, State=1),
    dict(Door_ID="d4", InternalID=2000101, X=100100, Y=100100, Health=40000, State=1),
    dict(Door_ID="d5", InternalID=15000201, X=203500, Y=200000, Health=51000, State=1),
    dict(Door_ID="d6", InternalID=253000301, X=33849, Y=39604, Health=4200, State=1),
    dict(Door_ID="d7", InternalID=253000302, X=33659, Y=39059, Health=4200, State=1),
)
RELICS = (
    dict(RelicID=30, Region=1, X=601545, Y=430727, Z=6496, Heading=3104, Realm=1, OriginalRealm=1, LastRealm=1,
         relicType=0, LastCaptureDate="2025-09-04 07:53:58"),
    dict(RelicID=78, Region=100, X=772136, Y=626640, Z=7824, Heading=1541, Realm=2, OriginalRealm=2, LastRealm=2,
         relicType=0, LastCaptureDate="2025-09-04 07:53:58"),
)


def make_world(path, keeps=KEEPS, doors=DOORS, relics=RELICS, items=(), log=()):
    conn = sqlite3.connect(path)
    try:
        with conn:
            for stmt in SCHEMA:
                conn.execute(stmt)
            conn.executemany("INSERT INTO Zones VALUES (?, ?)", ZONES)
            for table, rows in (("Keep", keeps), ("Door", doors), ("Relic", relics), ("KeepHookPointItem", items),
                                ("KeepCaptureLog", log)):
                for row in rows:
                    insert(conn, table, row)
    finally:
        conn.close()


def insert(conn, table, row):
    conn.execute(f"INSERT INTO {table} ({', '.join(row)}) VALUES ({', '.join('?' * len(row))})", tuple(row.values()))


def query(path, sql, params=()):
    conn = sqlite3.connect(path)
    try:
        return conn.execute(sql, params).fetchall()
    finally:
        conn.close()


def execute(path, *statements):
    conn = sqlite3.connect(path)
    try:
        with conn:
            for stmt in statements:
                if isinstance(stmt, tuple):
                    conn.execute(*stmt)
                else:
                    conn.execute(stmt)
    finally:
        conn.close()


def carry(new_db, old_db, limit_claims=False):
    conn = sqlite3.connect(new_db)
    try:
        return carry_rvr.carry(conn, old_db, NOW, limit_claims)
    finally:
        conn.close()


def keeps_line(matched, in_play):
    return (f"Keeps matched by name and region: {matched}, of which {in_play} in play (held by another realm than "
            "their own, or claimed) and carried")


def relics_line(matched, carried):
    return f"Relics matched: {matched}, of which {carried} away from home and carried"


def dump(path):
    """Every row of every table, to compare a world before and after."""
    conn = sqlite3.connect(path)
    try:
        tables = [t for (t,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
        return {t: sorted(map(repr, conn.execute(f'SELECT * FROM "{t}"'))) for t in tables}
    finally:
        conn.close()


class CarryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old = os.path.join(self.tmp.name, "old.db")
        self.new = os.path.join(self.tmp.name, "new.db")

    def tearDown(self):
        self.tmp.cleanup()

    def worlds(self, old=None, new=None):
        make_world(self.old, **(old or {}))
        make_world(self.new, **(new or {}))

    def test_realm_level_and_claim_are_carried_and_nothing_else(self):
        self.worlds()
        execute(self.old, "UPDATE Keep SET Realm=2, Level=4, ClaimedGuildName='Raiders', X=1, Y=1, BaseLevel=60, "
                          "CreateInfo='moved' WHERE KeepID=50")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT Realm, Level, ClaimedGuildName, X, Y, BaseLevel, OriginalRealm, CreateInfo, "
                                         "LastTimeRowUpdated FROM Keep WHERE KeepID=50"),
                         [(2, 4, "Raiders", 100000, 100000, 50, 1, "", NOW)])
        self.assertEqual(counts["Keep"], 1)
        self.assertEqual(notes, [keeps_line(3, 1), relics_line(2, 0)])

    def test_only_keeps_in_play_carry(self):
        # In play: held by another realm than its own, or claimed. Upstream's own changes to the others stay.
        self.worlds()
        execute(self.old, "UPDATE Keep SET Level=4, ClaimedGuildName='' WHERE KeepID=50",      # its realm's, unclaimed
                          "UPDATE Keep SET Level=3 WHERE KeepID=33",                           # no realm's, claim NULL
                          "UPDATE Keep SET Level=5, ClaimedGuildName='Hibernians' WHERE KeepID=22",  # claimed
                          "UPDATE Door SET Health=1, State=0")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT KeepID, Realm, Level, ClaimedGuildName FROM Keep ORDER BY KeepID"),
                         [(22, 3, 5, "Hibernians"), (33, 0, 1, None), (50, 1, 5, None)])
        self.assertEqual(query(self.new, "SELECT Door_ID FROM Door WHERE Health=1"), [("d5",)])  # the portal keep's
        self.assertEqual((counts["Keep"], counts["Door"]), (1, 1))
        self.assertEqual(notes[0], keeps_line(3, 1))

    def test_a_keep_whose_state_is_the_same_is_left_as_it_is(self):
        self.worlds()
        execute(self.old, "UPDATE Keep SET Realm=2 WHERE KeepID=50")
        execute(self.new, "UPDATE Keep SET Realm=2 WHERE KeepID=50")
        counts, _ = carry(self.new, self.old)
        self.assertEqual(counts["Keep"], 1)
        self.assertEqual(query(self.new, "SELECT DISTINCT LastTimeRowUpdated FROM Keep"), [("2000-01-01 00:00:00",)])

    def test_keeps_are_matched_by_name_and_region_not_by_keep_id(self):
        # The fork's 0.34 world numbered Dun Abermenai 32; upstream 0.35 numbers it 33.
        old_keeps = [dict(k, KeepID=32) if k["KeepID"] == 33 else k for k in KEEPS]
        old_keeps.append(dict(KeepID=33, Name="Dun Murdaigean", Region=251, X=33089, Y=38271, BaseLevel=29))
        new_keeps = list(KEEPS) + [dict(KeepID=32, Name="Dun Murdaigean", Region=251, X=33089, Y=38271, BaseLevel=31)]
        self.worlds(dict(keeps=old_keeps), dict(keeps=new_keeps))
        execute(self.old, "UPDATE Keep SET Realm=2, ClaimedGuildName='Raiders' WHERE KeepID=32",
                          "UPDATE Keep SET Realm=3, ClaimedGuildName='Wardens' WHERE KeepID=33")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT KeepID, Name, Realm, ClaimedGuildName FROM Keep WHERE KeepID IN (32, 33) "
                                         "ORDER BY KeepID"),
                         [(32, "Dun Murdaigean", 3, "Wardens"), (33, "Dun Abermenai", 2, "Raiders")])
        self.assertEqual((counts["Keep"], notes), (2, [keeps_line(4, 2), relics_line(2, 0)]))

    def test_names_are_matched_without_case(self):
        self.worlds(dict(keeps=[dict(k, Name=k["Name"].lower()) for k in KEEPS]))
        execute(self.old, "UPDATE Keep SET Realm=2 WHERE KeepID=50")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT Name, Realm FROM Keep WHERE KeepID=50"), [("Caer Benowyc", 2)])
        self.assertEqual(notes[0], keeps_line(3, 1))

    def test_unmatched_and_ambiguous_keeps_are_skipped_and_reported(self):
        twin = dict(KeepID=60, Name="Caer Benowyc", Region=1, X=150000, Y=150000, Realm=1, BaseLevel=50)
        gone = dict(KeepID=70, Name="Caer Gone", Region=1, X=300000, Y=300000, Realm=1, BaseLevel=50)
        added = dict(KeepID=80, Name="Caer New", Region=1, X=400000, Y=400000, Realm=1, BaseLevel=50)
        elsewhere = dict(KeepID=90, Name="Dun Abermenai", Region=252, X=33089, Y=38271, BaseLevel=26)
        self.worlds(dict(keeps=list(KEEPS) + [twin, gone, elsewhere]), dict(keeps=list(KEEPS) + [added]))
        execute(self.old, "UPDATE Keep SET Realm=2", "UPDATE Door SET Health=1")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT KeepID, Realm FROM Keep ORDER BY KeepID"),
                         [(22, 2), (33, 2), (50, 1), (80, 1)])
        self.assertEqual(notes, [
            keeps_line(2, 2),
            "Keeps only in the old world, not carried: Caer Gone (region 1), Dun Abermenai (region 252)",
            "Keeps only in the new world, left as it ships them: Caer New (region 1)",
            "Keeps whose name is not unique in their region, left as the new world ships them: Caer Benowyc (region 1)",
            relics_line(2, 0),
        ])
        self.assertEqual(counts["Keep"], 2)
        # Caer Benowyc's doors stay too; the portal keep's door and Dun Abermenai's gates are carried.
        self.assertEqual(query(self.new, "SELECT Door_ID FROM Door WHERE Health=1 ORDER BY Door_ID"), [("d5",), ("d6",), ("d7",)])

    def test_doors_of_keeps_in_play_get_health_and_state_only(self):
        self.worlds()
        execute(self.old, "UPDATE Keep SET Realm=2", "UPDATE Door SET Health=1000, State=0, Realm=2, Level=5, "
                          "Guild='Raiders', Locked=1, IsPostern=1, X=X+1, Name='moved'")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT Door_ID, Health, State FROM Door ORDER BY Door_ID"), [
            ("d1", 1000, 0), ("d2", 1000, 0),   # Caer Benowyc's
            ("d3", 40000, 1),                   # 3,500 from Caer Benowyc: outside its area
            ("d4", 40000, 1),                   # in Caer Benowyc's area, but in region 2
            ("d5", 1000, 0),                    # 3,500 from the portal keep: inside its area
            ("d6", 1000, 0), ("d7", 1000, 0),   # Dun Abermenai's gates
        ])
        self.assertEqual(query(self.new, "SELECT Door_ID, Realm, Level, Guild, Locked, Name FROM Door WHERE Door_ID IN "
                                         "('d1', 'd6') ORDER BY Door_ID"),
                         [("d1", 6, 20, None, 0, None), ("d6", 0, 0, None, 0, None)])
        self.assertEqual(query(self.new, "SELECT DISTINCT IsPostern FROM Door WHERE Door_ID IN ('d1', 'd6')"), [(0,)])
        self.assertEqual(counts["Door"], 5)
        self.assertEqual(notes, [keeps_line(3, 3), relics_line(2, 0)])

    def test_doors_keep_their_old_health_and_state_up_to_full_health_at_the_carried_level(self):
        # Caer Benowyc (base level 50) is taken: Reset leaves it at level 4, whose gates' full health is 40,000.
        # Its outer gate still has level 5's 50,000; the new world ships it damaged.
        self.worlds()
        execute(self.old, "UPDATE Keep SET Realm=2, Level=4 WHERE KeepID=50",
                          "UPDATE Door SET Health=50000, State=1 WHERE Door_ID='d1'",
                          "UPDATE Door SET Health=100, State=0 WHERE Door_ID='d2'")
        execute(self.new, "UPDATE Door SET Health=2500, State=0 WHERE Door_ID='d1'")
        carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT Door_ID, Health, State FROM Door WHERE Door_ID IN ('d1', 'd2') "
                                         "ORDER BY Door_ID"), [("d1", 40000, 1), ("d2", 100, 0)])

    def test_full_health_follows_the_new_worlds_settings(self):
        self.worlds()
        execute(self.new, "CREATE TABLE ServerProperty (`Key` VARCHAR(255), Value TEXT)",
                ("INSERT INTO ServerProperty VALUES (?, ?)", ("keep_doors_base_health", "100")),
                ("INSERT INTO ServerProperty VALUES (?, ?)", ("Keep_Doors_Health_Upgrade_Modifier", "0.5")),
                ("INSERT INTO ServerProperty VALUES (?, ?)", ("relic_doors_health", "90000")),
                "UPDATE Keep SET SkinType=99 WHERE KeepID=22")  # the portal keep stands in for a relic keep
        execute(self.old, "UPDATE Keep SET Realm=2, Level=4 WHERE KeepID IN (50, 22)", "UPDATE Door SET Health=200000")
        carry(self.new, self.old)
        # 50 x 100 = 5,000, plus 5,000 x 3 x 0.5
        self.assertEqual(query(self.new, "SELECT Door_ID, Health FROM Door WHERE Door_ID IN ('d1', 'd5') ORDER BY Door_ID"),
                         [("d1", 12500), ("d5", 90000)])

    def test_door_full_health(self):
        settings = dict(carry_rvr.DOOR_HEALTH_SETTINGS)
        self.assertEqual(settings, {"keep_doors_base_health": 200, "keep_doors_health_upgrade_modifier": 1.0,
                                    "relic_doors_health": 180000})  # ServerProperties.cs defaults
        self.assertEqual(carry_rvr.door_full_health(50, 0, 1), 10000)
        self.assertEqual(carry_rvr.door_full_health(50, 0, 4), 40000)
        self.assertEqual(carry_rvr.door_full_health(19, 0, 1), 3800)
        self.assertEqual(carry_rvr.door_full_health(60, 99, 10), 180000)
        settings["keep_doors_health_upgrade_modifier"] = 0.33
        self.assertEqual(carry_rvr.door_full_health(19, 0, 2, settings), 3800 + 1254)  # (int) 1254.0: rounded down

    def test_a_setting_that_is_not_a_number_keeps_the_default(self):
        self.worlds()
        execute(self.new, "CREATE TABLE ServerProperty (`Key` VARCHAR(255), Value TEXT)",
                "INSERT INTO ServerProperty VALUES ('keep_doors_base_health', 'lots')")
        execute(self.old, "UPDATE Keep SET Realm=2, Level=1 WHERE KeepID=50", "UPDATE Door SET Health=60000")
        carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT Health FROM Door WHERE Door_ID='d1'"), [(10000,)])

    def test_doors_follow_their_keep_when_the_keep_id_changes(self):
        old_keeps = [dict(k, KeepID=32) if k["KeepID"] == 33 else k for k in KEEPS]
        self.worlds(dict(keeps=old_keeps))
        execute(self.old, "UPDATE Keep SET Realm=1 WHERE KeepID=32",
                          "UPDATE Door SET Health=900, State=0 WHERE InternalID=253000301")
        carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT InternalID, Health, State FROM Door WHERE InternalID BETWEEN 253000301 "
                                         "AND 253000302 ORDER BY InternalID"),
                         [(253000301, 900, 0), (253000302, 4200, 1)])

    def test_a_door_id_found_twice_in_a_keep_area_is_skipped(self):
        twin = dict(Door_ID="d1b", InternalID=15000101, X=100400, Y=100400, Health=50000, State=1)
        stray = dict(Door_ID="d2b", InternalID=15000102, X=500000, Y=500000, Health=2545, State=0)  # not in an area
        self.worlds(dict(doors=list(DOORS) + [twin, stray]))
        execute(self.old, "UPDATE Keep SET Realm=2 WHERE KeepID=50",
                          "UPDATE Door SET Health=7 WHERE InternalID IN (15000101, 15000102)")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT Door_ID, Health FROM Door WHERE Door_ID IN ('d1', 'd2') ORDER BY Door_ID"),
                         [("d1", 50000), ("d2", 7)])
        self.assertEqual(notes, [keeps_line(3, 1), "Doors of keeps in play not carried (no single door with their "
                                 "InternalID in the keep's area in both worlds): 1", relics_line(2, 0)])

    def test_only_relics_away_from_home_carry(self):
        self.worlds()
        execute(self.old, "UPDATE Relic SET Region=100, X=772100, Y=626600, Z=7824, Heading=12, Realm=2, LastRealm=2, "
                          "LastCaptureDate='2026-10-08 20:00:00' WHERE RelicID=30",  # Albion's, in Midgard
                          "UPDATE Relic SET X=X+50, Heading=7 WHERE RelicID=78")   # Midgard's, at home
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT RelicID, Region, X, Y, Z, Heading, Realm, OriginalRealm, LastRealm, "
                                         "relicType, LastCaptureDate FROM Relic ORDER BY RelicID"), [
            (30, 100, 772100, 626600, 7824, 12, 2, 1, 2, 0, "2026-10-08 20:00:00"),
            (78, 100, 772136, 626640, 7824, 1541, 2, 2, 2, 0, "2025-09-04 07:53:58"),  # where the new world has it
        ])
        self.assertEqual(counts["Relic"], 1)
        self.assertEqual(notes, [keeps_line(3, 0), relics_line(2, 1)])

    def test_relics_are_matched_by_id_type_and_realm(self):
        carried_off = dict(RELICS[1], RelicID=17, relicType=1, Realm=0)  # a player had it when the server stopped
        self.worlds(dict(relics=[dict(RELICS[0], Realm=2), dict(RELICS[1], Realm=0), carried_off]),
                    dict(relics=[RELICS[0], dict(RELICS[1], relicType=1)]))
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT RelicID, Realm, relicType FROM Relic ORDER BY RelicID"),
                         [(30, 2, 0), (78, 2, 1)])  # 78 is another relicType in the new world: left
        self.assertEqual(counts["Relic"], 1)
        self.assertEqual(notes, [keeps_line(3, 0), relics_line(1, 1),
                                 "Relics only in the old world, not carried (RelicID): 17, 78",
                                 "Relics only in the new world, left as it ships them (RelicID): 78"])

    def test_hookpoint_items_of_keeps_in_play_follow_the_new_keep_id(self):
        old_keeps = [dict(k, KeepID=32) if k["KeepID"] == 33 else k for k in KEEPS]
        gone = dict(KeepID=70, Name="Caer Gone", Region=1, X=300000, Y=300000, BaseLevel=50)
        items = (dict(KeepID=32, ComponentID=1, HookPointID=65, ClassType="DOL.GS.GameSiegeBallista", KeepHookPointItem_ID="i1"),
                 dict(KeepID=50, ComponentID=2, HookPointID=97, ClassType="DOL.GS.Keeps.GuardFighter", KeepHookPointItem_ID="i2"),
                 dict(KeepID=50, ComponentID=2, HookPointID=129, ClassType="DOL.GS.Keeps.GuardArcher", KeepHookPointItem_ID="i3"),
                 dict(KeepID=70, ComponentID=1, HookPointID=65, ClassType="DOL.GS.Keeps.GuardFighter", KeepHookPointItem_ID="i4"),
                 dict(KeepID=22, ComponentID=1, HookPointID=65, ClassType="DOL.GS.Keeps.GuardFighter", KeepHookPointItem_ID="i5"))
        taken = (dict(KeepID=50, ComponentID=2, HookPointID=97, ClassType="DOL.GS.Keeps.GuardCaster", KeepHookPointItem_ID="n1"),)
        self.worlds(dict(keeps=old_keeps + [gone], items=items), dict(items=taken))
        execute(self.old, "UPDATE Keep SET Realm=1 WHERE KeepID=32", "UPDATE Keep SET Realm=2 WHERE KeepID IN (50, 70)")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT KeepHookPointItem_ID, KeepID, ComponentID, HookPointID, ClassType "
                                         "FROM KeepHookPointItem ORDER BY KeepHookPointItem_ID"), [
            ("i1", 33, 1, 65, "DOL.GS.GameSiegeBallista"),
            ("i3", 50, 2, 129, "DOL.GS.Keeps.GuardArcher"),
            ("n1", 50, 2, 97, "DOL.GS.Keeps.GuardCaster"),  # the hookpoint had an item in the new world
        ])  # i4: Caer Gone is not in the new world; i5: the portal keep is not in play
        self.assertEqual(counts["KeepHookPointItem"], 2)

    def test_the_capture_log_is_copied_without_key_clashes(self):
        log = (dict(ID=1, DateTaken="2026-10-01 20:00:00", KeepName="Caer Benowyc", CapturedBy="Midgard", RPReward=500),
               dict(ID=2, DateTaken="2026-10-02 21:00:00", KeepName="Dun Abermenai", CapturedBy="Albion", RPReward=50))
        since = (dict(ID=1, DateTaken="2026-10-05 10:00:00", KeepName="Caer Benowyc", CapturedBy="Albion", RPReward=500),
                 dict(log[1], ID=2))  # already there
        self.worlds(dict(log=log), dict(log=since))
        counts, notes = carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT ID, DateTaken, KeepName, CapturedBy FROM KeepCaptureLog ORDER BY ID"), [
            (1, "2026-10-05 10:00:00", "Caer Benowyc", "Albion"),
            (2, "2026-10-02 21:00:00", "Dun Abermenai", "Albion"),
            (3, "2026-10-01 20:00:00", "Caer Benowyc", "Midgard"),
        ])
        self.assertEqual(counts["KeepCaptureLog"], 1)

    def test_a_failure_leaves_the_new_world_unchanged(self):
        self.worlds(dict(log=(dict(DateTaken="2026-10-01 20:00:00", KeepName="Caer Benowyc"),)))
        execute(self.old, "UPDATE Keep SET Realm=2", "UPDATE Door SET Health=1", "UPDATE Relic SET Realm=3")
        before = dump(self.new)
        with mock.patch.object(carry_rvr, "_carry_capture_log", side_effect=sqlite3.OperationalError("disk I/O error")):
            with self.assertRaisesRegex(sqlite3.OperationalError, "disk I/O error"):
                carry(self.new, self.old)
        self.assertEqual(dump(self.new), before)
        conn = sqlite3.connect(self.new)  # and the old world is detached again
        try:
            carry_rvr.carry(conn, self.old, NOW)
            self.assertEqual([name for _, name, _ in conn.execute("PRAGMA database_list")], ["main"])
        finally:
            conn.close()

    def test_a_table_missing_from_a_world_is_left_out(self):
        self.worlds()
        execute(self.old, "DROP TABLE Relic", "DROP TABLE KeepCaptureLog", "UPDATE Keep SET Realm=2")
        counts, notes = carry(self.new, self.old)
        self.assertEqual(counts, {"Keep": 3, "Door": 5, "Relic": 0, "KeepHookPointItem": 0, "KeepCaptureLog": 0})
        self.assertEqual(query(self.new, "SELECT DISTINCT Realm FROM Keep"), [(2,)])
        self.assertEqual(notes, [keeps_line(3, 3), "Relic not carried: no Relic table in the old world",
                                 "KeepCaptureLog not carried: no KeepCaptureLog table in the old world"])

    def test_a_column_missing_from_a_world_leaves_its_table_out(self):
        # A new version may rename or drop a column; the rest still carries.
        self.worlds()
        execute(self.old, "ALTER TABLE Relic DROP COLUMN LastCaptureDate", "UPDATE Keep SET Realm=2 WHERE KeepID=50",
                          "UPDATE Relic SET Realm=2 WHERE RelicID=30")
        counts, notes = carry(self.new, self.old)
        self.assertEqual((counts["Keep"], counts["Door"], counts["Relic"]), (1, 2, 0))
        self.assertEqual(notes, [keeps_line(3, 1),
                                 "Relic not carried: no LastCaptureDate column in the old world's Relic table"])
        self.assertEqual(query(self.new, "SELECT Realm FROM Relic WHERE RelicID=30"), [(1,)])

    def test_a_keep_column_missing_leaves_out_keeps_doors_and_hookpoint_items(self):
        self.worlds()
        execute(self.new, "ALTER TABLE Keep DROP COLUMN SkinType")
        execute(self.old, "UPDATE Keep SET Realm=2", "UPDATE Door SET Health=1")
        counts, notes = carry(self.new, self.old)
        self.assertEqual((counts["Keep"], counts["Door"]), (0, 0))
        why = "no SkinType column in the new world's Keep table"
        self.assertEqual(notes, [relics_line(2, 0), f"Keep not carried: {why}", f"Door not carried: {why}",
                                 f"KeepHookPointItem not carried: {why}"])

    def test_with_limit_claims_a_guild_holding_another_keep_loses_the_carried_claim(self):
        # carry-rvr into the live world: guilds_claim_limit is 1. Realm and level still carry.
        self.worlds()
        execute(self.old, "UPDATE Keep SET Realm=2, Level=5, ClaimedGuildName='Raiders' WHERE KeepID=50",
                          "UPDATE Keep SET Realm=1, ClaimedGuildName='Wardens' WHERE KeepID=33")
        execute(self.new, "UPDATE Keep SET ClaimedGuildName='raiders' WHERE KeepID=22",  # held since, kept
                          "UPDATE Keep SET ClaimedGuildName='Wardens' WHERE KeepID=33")  # this carry overwrites it
        counts, notes = carry(self.new, self.old, limit_claims=True)
        self.assertEqual(query(self.new, "SELECT KeepID, Realm, Level, ClaimedGuildName FROM Keep ORDER BY KeepID"),
                         [(22, 3, 1, "raiders"), (33, 1, 1, "Wardens"), (50, 2, 5, "")])
        self.assertIn("Claims left out, as the guild holds another keep in this world: Caer Benowyc (region 1, Raiders)",
                      notes)

    def test_without_limit_claims_every_claim_carries(self):
        self.worlds()
        execute(self.old, "UPDATE Keep SET Realm=2, ClaimedGuildName='Raiders' WHERE KeepID=50")
        execute(self.new, "UPDATE Keep SET ClaimedGuildName='Raiders' WHERE KeepID=22")
        carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT ClaimedGuildName FROM Keep WHERE KeepID=50"), [("Raiders",)])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic 0.35 world)")
class RealWorldCarryTests(unittest.TestCase):
    """A copy of the clean 0.35 world, run by the server for a while (world fixes, then captures), carries into
    a fresh copy, as upgrade-world does. Dun Abermenai and Dun Murdaigean swap KeepIDs in the old copy, as
    between the fork's 0.34 world and upstream's 0.35 one. Five keeps are in play; Caer Sursbrooke is not
    (its own realm's, unclaimed), though its level and gate differ from the new world's."""

    CAPTURES = (
        "UPDATE Keep SET Realm=2, Level=5, ClaimedGuildName='Hearth Raiders' WHERE Name='Caer Benowyc' AND Region=1",
        "UPDATE Keep SET Realm=1, Level=4, ClaimedGuildName='' WHERE Name='Dun Crauchon' AND Region=200",
        "UPDATE Keep SET Realm=3, Level=4 WHERE Name='Bledmeer Faste' AND Region=100",
        # A capture (AbstractGameKeep.Reset) leaves the keep at level 4 and its gates at level 4's full health,
        # 40,000 at base level 50. Upstream ships Bledmeer Faste's gates damaged, Dun nGed's at level 1's 10,000.
        "UPDATE Door SET Health=40000, State=1 WHERE InternalID IN (115001201, 115001202)",
        "UPDATE Keep SET Realm=1, Level=4 WHERE Name='Dun nGed' AND Region=200",
        "UPDATE Door SET Health=40000 WHERE InternalID IN (212006301, 212006302, 212006304, 212006306)",
        "UPDATE Keep SET Realm=2, ClaimedGuildName='Thid Wardens' WHERE Name='Dun Abermenai' AND Region=253",
        "UPDATE Keep SET Level=5 WHERE Name='Caer Sursbrooke' AND Region=1",
        "UPDATE Door SET Health=1200, State=0 WHERE InternalID=15110601",   # Caer Benowyc's gate, broken open
        "UPDATE Door SET Health=1000, State=0 WHERE InternalID=253000301",  # Dun Abermenai's outer gate
        "UPDATE Door SET Health=500 WHERE InternalID=14078601",             # Caer Sursbrooke's gate
        # Albion's strength relic, from Castle Excalibur to Midgard's Mjollner Faste
        "UPDATE Relic SET Region=100, X=772136, Y=626640, Z=7824, Heading=1541, Realm=2, LastRealm=2, "
        "LastCaptureDate='2026-10-08 21:30:00' WHERE RelicID=30",
        "INSERT INTO KeepCaptureLog (DateTaken, KeepName, KeepType, CapturedBy, RPGainerList) "
        "VALUES ('2026-10-08 21:00:00', 'Caer Benowyc', 'Keep', 'Midgard', '')",
    )
    SWAP = ("UPDATE Keep SET KeepID=-1 WHERE KeepID=32", "UPDATE Keep SET KeepID=32 WHERE KeepID=33",
            "UPDATE Keep SET KeepID=33 WHERE KeepID=-1",
            "INSERT INTO KeepHookPointItem (KeepID, ComponentID, HookPointID, ClassType, KeepHookPointItem_ID) "
            "VALUES (32, 1, 65, 'DOL.GS.GameSiegeBallista', 'hp-abermenai')")  # on Dun Abermenai

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.old = os.path.join(cls.tmp.name, "old.db")
        cls.new = os.path.join(cls.tmp.name, "new.db")
        shutil.copyfile(TEST_WORLD, cls.old)
        shutil.copyfile(TEST_WORLD, cls.new)
        os.chmod(cls.old, 0o644)
        os.chmod(cls.new, 0o644)
        world_fixes.apply(cls.old)  # the old world ran on the 0.35 image
        execute(cls.old, *cls.CAPTURES)
        execute(cls.old, *cls.SWAP)
        cls.counts, cls.notes = carry(cls.new, cls.old)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def keep(self, name, region):
        return query(self.new, "SELECT KeepID, Realm, Level, ClaimedGuildName FROM Keep WHERE Name=? AND Region=?",
                     (name, region))[0]

    def test_every_keep_matches_and_five_are_in_play(self):
        self.assertEqual(query(TEST_WORLD, "SELECT count(*) FROM Keep"), [(81,)])
        self.assertEqual(self.notes, [keeps_line(81, 5), relics_line(6, 1)])
        self.assertEqual(self.counts["Keep"], 5)

    def test_captured_keeps_arrive_on_the_right_keeps(self):
        self.assertEqual(self.keep("Caer Benowyc", 1), (50, 2, 5, "Hearth Raiders"))
        self.assertEqual(self.keep("Dun Crauchon", 200), (100, 1, 4, ""))
        self.assertEqual(self.keep("Bledmeer Faste", 100), (75, 3, 4, ""))
        self.assertEqual(self.keep("Dun Abermenai", 253), (33, 2, 1, "Thid Wardens"))
        self.assertEqual(self.keep("Dun Murdaigean", 251), (32, 0, 1, ""))

    def test_a_keep_not_in_play_stays_as_the_new_world_ships_it(self):
        self.assertEqual(self.keep("Caer Sursbrooke", 1), (54, 1, 4, ""))
        self.assertEqual(query(self.new, "SELECT Health FROM Door WHERE InternalID=14078601"), [(40000,)])

    def test_the_keep_structure_stays_the_new_worlds(self):
        columns = "KeepID, Name, Region, X, Y, Z, Heading, OriginalRealm, KeepType, BaseLevel, SkinType, CreateInfo, Keep_ID"
        self.assertEqual(query(self.new, f"SELECT {columns} FROM Keep ORDER BY KeepID"),
                         query(TEST_WORLD, f"SELECT {columns} FROM Keep ORDER BY KeepID"))

    def test_gates_arrive(self):
        self.assertEqual(query(self.new, "SELECT InternalID, Health, State FROM Door WHERE InternalID IN "
                                         "(15110601, 253000301, 253000302) ORDER BY InternalID"),
                         [(15110601, 1200, 0), (253000301, 1000, 0), (253000302, 3800, 1)])  # 3,800: the fix's full health
        self.assertEqual(self.counts["Door"], 24)  # the five keeps' doors: 4, 6, 6, 2 and 6

    def test_captured_keeps_gates_arrive_full(self):
        self.assertEqual(query(TEST_WORLD, "SELECT InternalID, Health FROM Door WHERE InternalID IN (115001201, 115001202, "
                                           "212006301) ORDER BY InternalID"),
                         [(115001201, 2500), (115001202, 43583), (212006301, 10000)])
        self.assertEqual(query(self.new, "SELECT InternalID, Health, State FROM Door WHERE InternalID IN (115001201, "
                                         "115001202, 212006301, 212006302, 212006304, 212006306) ORDER BY InternalID"),
                         [(115001201, 40000, 1), (115001202, 40000, 1), (212006301, 40000, 1), (212006302, 40000, 1),
                          (212006304, 40000, 1), (212006306, 40000, 1)])
        self.assertEqual(self.keep("Dun nGed", 200), (103, 1, 4, ""))

    def test_the_relic_stays_where_it_was_taken(self):
        self.assertEqual(query(self.new, "SELECT Region, X, Y, Realm, LastRealm, LastCaptureDate FROM Relic WHERE RelicID=30"),
                         [(100, 772136, 626640, 2, 2, "2026-10-08 21:30:00")])
        self.assertEqual(self.counts["Relic"], 1)

    def test_hookpoint_item_and_capture_log(self):
        self.assertEqual(query(self.new, "SELECT KeepID, ClassType FROM KeepHookPointItem"), [(33, "DOL.GS.GameSiegeBallista")])
        self.assertEqual(query(self.new, "SELECT KeepName, CapturedBy FROM KeepCaptureLog"), [("Caer Benowyc", "Midgard")])

    def test_the_world_fixes_keep_the_carried_state(self):
        """The next start runs world_fixes.py (the classic battlegrounds included) on the upgraded world."""
        db = os.path.join(self.tmp.name, "fixed.db")
        shutil.copyfile(self.new, db)
        execute(db, "UPDATE Keep SET Level=4 WHERE KeepID=33")  # a capture before the fork's level 1 hook
        lines = world_fixes.apply(db)
        # Dun Abermenai's gates hold the old world's health, the fix's full health or less, and keep it; the six
        # gates of the three central keeps not in play go from upstream's full health to the fix's.
        self.assertIn("Battlegrounds: keep levels for the ranges (Dun Abermenai base level 19, Thidranki Faste base level "
                      "24, Dun Murdaigean base level 29, Caer Caledon base level 35, Dun Abermenai back to level 1, "
                      "6 gates' health)", lines)
        self.assertEqual(query(db, "SELECT Realm, Level, ClaimedGuildName, BaseLevel FROM Keep WHERE KeepID=33"),
                         [(2, 1, "Thid Wardens", 19)])  # step 3 puts a central keep back to level 1
        self.assertEqual(query(db, "SELECT Realm, Level, ClaimedGuildName FROM Keep WHERE KeepID=50"),
                         [(2, 5, "Hearth Raiders")])
        self.assertEqual(query(db, "SELECT InternalID, Health, State FROM Door WHERE InternalID IN "
                                   "(15110601, 253000301, 253000302) ORDER BY InternalID"),
                         [(15110601, 1200, 0), (253000301, 1000, 0), (253000302, 3800, 1)])
        self.assertEqual(query(db, "SELECT Region, Realm FROM Relic WHERE RelicID=30"), [(100, 2)])


@unittest.skipUnless(TEST_WORLD and TEST_WORLD_OLD,
                     "needs HDC_TEST_WORLD (clean classic 0.35 world) and HDC_TEST_WORLD_OLD (clean classic 0.34 world)")
class Upgrade034Tests(unittest.TestCase):
    """The clean 0.34 world, with captures, carries into the clean 0.35 world (these two worlds only)."""

    CAPTURES = (
        "UPDATE Keep SET Realm=2, Level=5, ClaimedGuildName='Hearth Raiders' WHERE Name='Caer Benowyc' AND Region=1",
        "UPDATE Keep SET Realm=1 WHERE Name='Thidranki Faste' AND Region=252",
        "UPDATE Door SET Health=1200, State=0 WHERE InternalID=15110601",
        "UPDATE Relic SET Region=100, X=772136, Y=626640, Realm=2, LastRealm=2 WHERE RelicID=30",
    )
    # The fork's battlegrounds v1 added these keeps to the 0.34 world, numbered from 32.
    V1_KEEPS = (
        "INSERT INTO Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
        "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, CreateInfo, Keep_ID) "
        "VALUES (32, 'Dun Abermenai', 253, 33089, 38271, 3720, 0, 3, 1, 'Aber Guard', 1, 1, 1, 0, 0, 19, 0, "
        "'HearthDAoC classic-battlegrounds-v1', 'hdc-bg253-dun-abermenai')",
        "INSERT INTO Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
        "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, CreateInfo, Keep_ID) "
        "VALUES (33, 'Dun Murdaigean', 251, 33089, 38271, 3720, 0, 1, 1, '', 1, 1, 1, 0, 0, 29, 0, "
        "'HearthDAoC classic-battlegrounds-v1', 'hdc-bg251-dun-murdaigean')",
        "UPDATE Door SET Health=900, State=1 WHERE InternalID=253000301",
    )

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old = os.path.join(self.tmp.name, "old.db")
        self.new = os.path.join(self.tmp.name, "new.db")
        shutil.copyfile(TEST_WORLD_OLD, self.old)
        shutil.copyfile(TEST_WORLD, self.new)
        os.chmod(self.old, 0o644)
        os.chmod(self.new, 0o644)
        execute(self.old, *self.CAPTURES)

    def tearDown(self):
        self.tmp.cleanup()

    def test_every_034_keep_matches_and_the_new_central_keeps_are_reported(self):
        counts, notes = carry(self.new, self.old)
        self.assertEqual(counts["Keep"], 2)
        self.assertEqual(notes, [keeps_line(79, 2), "Keeps only in the new world, left as it ships them: Dun Murdaigean "
                                 "(region 251), Dun Abermenai (region 253)", relics_line(6, 1)])
        self.assertEqual(query(self.new, "SELECT Realm, Level, ClaimedGuildName FROM Keep WHERE KeepID=50"),
                         [(2, 5, "Hearth Raiders")])
        self.assertEqual(query(self.new, "SELECT Realm FROM Keep WHERE KeepID=11"), [(1,)])
        self.assertEqual(query(self.new, "SELECT Health, State FROM Door WHERE InternalID=15110601"), [(1200, 0)])
        self.assertEqual(query(self.new, "SELECT Region, Realm FROM Relic WHERE RelicID=30"), [(100, 2)])
        # 0.34's doors at the new central keeps belonged to no keep there: the new world's stay.
        self.assertEqual(query(self.new, "SELECT Health, State FROM Door WHERE InternalID=253000301"), [(4200, 1)])

    def test_upstreams_changes_to_keeps_not_in_play_stay(self):
        # 0.35 lowered Caer Sursbrooke and Nottmoor Faste from level 5 to 4, and their gates' health with it.
        self.assertEqual(query(self.old, "SELECT KeepID, Level FROM Keep WHERE KeepID IN (54, 76) ORDER BY KeepID"),
                         [(54, 5), (76, 5)])
        carry(self.new, self.old)
        self.assertEqual(query(self.new, "SELECT KeepID, Level FROM Keep WHERE KeepID IN (54, 76) ORDER BY KeepID"),
                         [(54, 4), (76, 4)])
        self.assertEqual(query(self.new, "SELECT Health FROM Door WHERE InternalID IN (14078602, 113057701)"),
                         query(TEST_WORLD, "SELECT Health FROM Door WHERE InternalID IN (14078602, 113057701)"))

    def test_the_fork_v1_central_keeps_map_onto_upstreams(self):
        execute(self.old, *self.V1_KEEPS)
        counts, notes = carry(self.new, self.old)
        self.assertEqual((counts["Keep"], notes), (4, [keeps_line(81, 4), relics_line(6, 1)]))
        self.assertEqual(query(self.new, "SELECT KeepID, Name, Realm, ClaimedGuildName, BaseLevel FROM Keep "
                                         "WHERE KeepID IN (32, 33) ORDER BY KeepID"),
                         [(32, "Dun Murdaigean", 1, "", 31), (33, "Dun Abermenai", 3, "Aber Guard", 21)])
        self.assertEqual(query(self.new, "SELECT Health, State FROM Door WHERE InternalID=253000301"), [(900, 1)])


if __name__ == "__main__":
    unittest.main()
