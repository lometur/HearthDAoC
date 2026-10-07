"""The classic battlegrounds (sub-project 5): the fork's upstream source edits and the world data fix.

An upstream sync that brings back the old porter blocks, "Svasudheim Faste" or Atlas's battleground
quest files fails the source checks. The world data tests run deploy/bin/battlegrounds.py on a scratch
world (make_world) and, with HDC_TEST_WORLD, on a copy of a clean classic world.
"""
import os
import pathlib
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

import battlegrounds  # noqa: E402
from tests.test_world_fixes import SCHEMA as WORLD_FIXES_SCHEMA  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SOURCE = os.path.join(ROOT, "source")
GAME_SERVER = os.path.join(SOURCE, "server", "GameServer")
OF_TELEPORTERS = os.path.join(GAME_SERVER, "scripts", "teleporters", "OFTeleporters.cs")
KEEP_MANAGER = os.path.join(GAME_SERVER, "keeps", "KeepManager.cs")
BATTLEGROUND_QUESTS = os.path.join(GAME_SERVER, "scripts", "quests", "BattlegroundQuests")

PORTER_CALL = "PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);"
# Atlas's daily quests for Caledonia 34-39 and Thidranki 20-24, whose scripts also made the Pazz NPCs.
QUEST_CLASS_NAMES = (
    "CaleKeepCaptureAlb", "CaleKeepCaptureHib", "CaleKeepCaptureMid",
    "CaleKillQuestAlb", "CaleKillQuestHib", "CaleKillQuestMid",
    "ThidKeepCaptureAlb", "ThidKeepCaptureHib", "ThidKeepCaptureMid",
    "ThidKillQuestAlb", "ThidKillQuestHib", "ThidKillQuestMid",
)


def cs_files(top):
    """Every .cs file under top, as paths relative to the repository root, sorted."""
    found = []
    for folder, _, names in os.walk(top):
        found.extend(os.path.relpath(os.path.join(folder, name), ROOT) for name in names if name.endswith(".cs"))
    return sorted(found)


class ClassicBattlegroundSourceTests(unittest.TestCase):
    def test_porter_blocks_call_the_fork(self):
        # The file starts with a BOM, and its line endings are mixed (CRLF and LF).
        with open(OF_TELEPORTERS, encoding="utf-8-sig") as f:
            text = f.read()
        lines = text.splitlines()
        starts = [i for i, line in enumerate(lines) if line.strip() == "case BattlegroundsID:"]
        self.assertEqual(len(starts), 3, "one battlegrounds block per realm")

        for start in starts:
            body = []
            for line in lines[start + 1:]:
                if line.strip().startswith("case "):
                    break
                if line.strip():
                    body.append(line.strip())
            self.assertEqual(body, [PORTER_CALL, "break;"], f"the block at line {start + 1}")

        # Atlas's caps: Thidranki under 7,125 realm points, Caledonia under 122,500.
        self.assertNotIn("7125", text)
        self.assertNotIn("122500", text)

    def test_keep_manager_names_svasud_faste(self):
        # ExitBattleground looks the realm's home portal keep up by TeleportID; the world's row is "Svasud Faste".
        with open(KEEP_MANAGER, encoding="utf-8") as f:
            text = f.read()
        midgard = [line.strip() for line in text.splitlines() if "case eRealm.Midgard: location =" in line]
        self.assertEqual(midgard, ['case eRealm.Midgard: location = "Svasud Faste"; break;'])
        self.assertNotIn("Svasudheim", text)

    def test_battleground_quests_are_gone(self):
        self.assertEqual(cs_files(BATTLEGROUND_QUESTS), [])

        # deploy/ is left out: the world fix names the classes as data, to delete their saved quests.
        names = re.compile(r"\b(?:" + "|".join(QUEST_CLASS_NAMES) + r")\b")
        naming = []
        for path in cs_files(SOURCE):
            with open(os.path.join(ROOT, path), encoding="utf-8", errors="replace") as f:
                if names.search(f.read()):
                    naming.append(path)
        self.assertEqual(naming, [])


NOW = "2026-10-07 12:00:00"
LATER = "2026-10-08 12:00:00"
INJECTED = "CREATE TRIGGER injected {} BEGIN SELECT RAISE(ABORT, 'injected failure'); END"

# The scratch world: the tables battlegrounds.py needs, trimmed to the columns it uses (Keep in full),
# with rows copied from the clean classic world (clean-classic-0.34.db) unless a comment says otherwise.
WORLD_SCHEMA = [
    "CREATE TABLE Battleground (RegionID INT NOT NULL DEFAULT 0, MinLevel INT NOT NULL DEFAULT 0, "
    "MaxLevel INT NOT NULL DEFAULT 0, MaxRealmLevel INT NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Battleground_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Battleground_ID))",
    "CREATE TABLE Zones (ZoneID INT NOT NULL DEFAULT 0, RegionID INT NOT NULL DEFAULT 0, "
    "Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, Experience INT NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (ZoneID))",
    "CREATE TABLE Regions (RegionID INT NOT NULL DEFAULT 0, Description TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (RegionID))",
    "CREATE TABLE Keep (KeepID INT NOT NULL DEFAULT 0, Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "Region INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, "
    "Heading INT NOT NULL DEFAULT 0, Realm INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, "
    "ClaimedGuildName TEXT DEFAULT NULL COLLATE NOCASE, AlbionDifficultyLevel INT NOT NULL DEFAULT 0, "
    "MidgardDifficultyLevel INT NOT NULL DEFAULT 0, HiberniaDifficultyLevel INT NOT NULL DEFAULT 0, "
    "OriginalRealm INT NOT NULL DEFAULT 0, KeepType INT NOT NULL DEFAULT 0, BaseLevel INT NOT NULL DEFAULT 0, "
    "SkinType INT NOT NULL DEFAULT 0, CreateInfo VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Keep_ID VARCHAR(255) DEFAULT NULL COLLATE NOCASE, PRIMARY KEY (KeepID))",
    "CREATE TABLE Mob (ClassType TEXT DEFAULT NULL COLLATE NOCASE, Name VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "X INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, Heading INT NOT NULL DEFAULT 0, "
    "Region INT NOT NULL DEFAULT 0, Model INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, "
    "Realm INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Mob_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Mob_ID))",
    "CREATE TABLE Door (Z INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, "
    "Heading INT NOT NULL DEFAULT 0, InternalID INT NOT NULL DEFAULT 0, Health INT NOT NULL DEFAULT 0, "
    "State INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Door_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Door_ID))",
    "CREATE TABLE Quest (Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, Step INT NOT NULL DEFAULT 0, "
    "Character_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Quest_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Quest_ID))",
]
SEED = {
    "Battleground (RegionID, MinLevel, MaxLevel, MaxRealmLevel, Battleground_ID)": [
        (165, 45, 49, 45, "Cathal Valley (Level 45-49)"),
        (250, 30, 34, 25, "Caledonia (Level 34-39 - RR3L5)"),
        (251, 25, 29, 5, "Murdaigean (Level 25-29)"),
        (252, 20, 24, 10, "Thidranki (Level 20-24 - RR2L0)"),
        (253, 15, 19, 2, "Abermenai (Level 15-19)"),
    ],
    "Zones (ZoneID, RegionID, Name, Experience, LastTimeRowUpdated)": [
        (250, 250, "Caledon", 50, "2017-01-08 18:51:19"),
        (251, 251, "Murdaigean", 0, "2017-01-08 18:51:19"),
        (252, 252, "Thidranki", 50, "2017-01-08 18:51:19"),
        (253, 253, "Abermenai", 0, "2017-01-08 18:51:19"),
    ],
    "Regions (RegionID, Description, LastTimeRowUpdated)": [
        (250, "Caledon", "2017-01-08 18:50:36"),
        (251, "Murdaigean", "2017-01-08 18:50:36"),
        (252, "Thidranki", "2017-01-08 18:50:36"),
        (253, "Abermenai", "2017-01-08 18:50:36"),
    ],
    "Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
    "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, CreateInfo, "
    "LastTimeRowUpdated, Keep_ID)": [
        (11, "Thidranki Faste", 252, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 26, 0, "Atlas BG",
         "2022-10-09 21:09:42", "11"),
        (31, "Caer Caledon", 250, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 46, 0, "Atlas BG",
         "2022-08-18 17:36:33", "31"),
    ],
    "Door (Z, Y, X, Heading, InternalID, Health, State, LastTimeRowUpdated, Door_ID)": [
        (3783, 39237, 32673, 2665, 250000301, 9200, 1, "2023-06-15 11:03:20", "55c128b4-5410-49a3-b7de-cf288dd244ec"),
        (3914, 37634, 33192, 1599, 250000302, 9200, 1, "2023-06-15 11:03:20", "cbad55f4-2e57-4533-b083-a92357043c6a"),
        (3720, 38275, 34333, 1024, 252000301, 5200, 1, "2023-06-15 11:03:20", "2b95f0c0-f9a8-493d-ac4f-11d89d0809e7"),
        (3720, 38180, 32654, 1030, 252000302, 5200, 1, "2023-06-15 11:03:20", "91ce710c-043d-4c17-aaad-26b5046df3d0"),
    ],
    "Mob (ClassType, Name, X, Y, Z, Heading, Region, Model, Level, Realm, LastTimeRowUpdated, Mob_ID)": [
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 18826, 17862, 4320, 447, 252, 34, 24, 0, "2022-08-16 17:54:56",
         "08cf170a-3774-42c2-9816-934f78d5bd4e"),
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 54584, 24735, 4320, 1175, 252, 34, 24, 0, "2022-08-16 17:53:38",
         "120a1ccf-94e8-4420-bc72-05ec20390e44"),
        ("DOL.GS.HitbackDummy", "Hitback Dummy - Right Click to Reset", 37678, 53068, 3944, 1008, 252, 34, 24, 0,
         "2022-08-16 19:19:47", "853682fd-1de2-4100-adb5-02f05c8ed7d1"),
        # Atlas put Heal Dummies only outside the battlegrounds; this one (from region 1) is put in 250 so
        # the test covers the class.
        ("DOL.GS.HealDummy", "Heal Dummy", 583988, 476486, 2600, 4088, 250, 34, 50, 0, "2021-12-28 22:03:39",
         "7b98ecad-244c-4edc-a503-1f5def9935e3"),
        # A dummy outside the battlegrounds stays.
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 584452, 476256, 2600, 4, 1, 34, 10, 0, "2021-12-28 22:02:30",
         "125d80ca-f7b0-4b13-8340-d14d1c306a94"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 53441, 25129, 4312, 2617, 252, 2212, 75, 2,
         "2022-08-12 00:15:42", "27d30f96-e238-482d-b359-fc5387589108"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 36776, 52400, 3944, 3072, 250, 2212, 75, 1,
         "2022-08-12 04:56:01", "2ae9038a-72f0-4400-bbb1-53574dd5dfe9"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 18933, 18185, 4320, 976, 252, 2212, 75, 3,
         "2022-08-12 00:15:11", "ecb08ffb-cf86-47f1-a53b-28581a48666b"),
        ("DOL.GS.Keeps.GuardStaticCaster", "Wizard", 33185, 37386, 3722, 2005, 250, 32, 48, 1, "2026-04-05 05:21:17",
         "caledon-guard-25"),
        # A Caer Caledon guard stays.
        ("DOL.GS.Keeps.GuardStaticCaster", "Renegade Runemaster", 32475, 38015, 4106, 1539, 250, 507, 38, 0,
         "2022-06-21 20:04:28", "caledon-guard-23"),
    ],
    # The clean world has no Quest rows; these stand for characters that took Atlas's daily quests.
    "Quest (Name, Step, Character_ID, Quest_ID)": [
        ("DOL.GS.DailyQuest.Albion.ThidKillQuestAlb", 1, "char-alb", "quest-thid"),
        ("DOL.GS.DailyQuest.Hibernia.CaleKillQuestMid", 2, "char-mid", "quest-cale"),
        ("DOL.GS.DailyQuest.Hibernia.CaptureKeepQuestHib", 1, "char-hib", "quest-frontier"),  # not a battleground quest
    ],
}
REMOVED = ("08cf170a-3774-42c2-9816-934f78d5bd4e", "120a1ccf-94e8-4420-bc72-05ec20390e44",
           "27d30f96-e238-482d-b359-fc5387589108", "2ae9038a-72f0-4400-bbb1-53574dd5dfe9",
           "7b98ecad-244c-4edc-a503-1f5def9935e3", "853682fd-1de2-4100-adb5-02f05c8ed7d1",
           "caledon-guard-25", "ecb08ffb-cf86-47f1-a53b-28581a48666b")


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
            for statement in statements:
                conn.execute(statement)
    finally:
        conn.close()


def dump(path):
    conn = sqlite3.connect(path)
    try:
        return list(conn.iterdump())
    finally:
        conn.close()


def make_world(path):
    execute(path, *WORLD_SCHEMA)
    conn = sqlite3.connect(path)
    try:
        with conn:
            for table, rows in SEED.items():
                conn.executemany(f"INSERT INTO {table} VALUES ({', '.join('?' * len(rows[0]))})", rows)
    finally:
        conn.close()


def apply_fix(path, now=NOW):
    """battlegrounds.apply inside a transaction, as world_fixes.py calls it."""
    conn = sqlite3.connect(path)
    try:
        with conn:
            conn.execute("BEGIN")
            return battlegrounds.apply(conn, now)
    finally:
        conn.close()


def marks(n):
    return ", ".join("?" * n)


class BattlegroundFixTests(unittest.TestCase):
    LINES = [
        "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
        "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
        "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
        "4 gates' health)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
        "3 Void Merchants, the stray Wizard); 2 saved battleground daily quests deleted",
    ]
    # Where each step is made to fail: on the last statement it runs, so the steps before it, and that
    # step's own earlier statements, have already changed rows.
    FAILURES = {
        "_step1_battleground_rows": "BEFORE UPDATE ON Battleground WHEN OLD.RegionID = 250",
        "_step2_names_and_xp": "BEFORE UPDATE OF Experience ON Zones WHEN OLD.ZoneID = 250",
        "_step3_keep_levels": "BEFORE UPDATE ON Door WHEN OLD.InternalID = 250000302",
        "_step6_atlas_leftovers": "BEFORE DELETE ON Quest",
    }

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = self.world("world.db")

    def tearDown(self):
        self.tmp.cleanup()

    def world(self, name):
        path = os.path.join(self.tmp.name, name)
        make_world(path)
        return path

    def q(self, sql, params=()):
        return query(self.db, sql, params)

    def run_world_fixes(self):
        return subprocess.run([sys.executable, os.path.join(BIN, "world_fixes.py"), "--db", self.db],
                              capture_output=True, text=True)

    def test_each_step_changes_upstream_values_and_reports(self):
        execute(self.db, "UPDATE Keep SET Level=4 WHERE KeepID=31")  # a capture on this world left it at 4
        self.assertEqual(apply_fix(self.db), [
            "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
            "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
            "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
            "Caer Caledon back to level 1, 4 gates' health)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
            "3 Void Merchants, the stray Wizard); 2 saved battleground daily quests deleted",
        ])
        self.assertEqual(self.q("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel, LastTimeRowUpdated "
                                "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45, "2000-01-01 00:00:00"),
            (250, "Caledonia (Level 30-35 - RR1L9)", 30, 35, 10, NOW),
            (251, "Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6, NOW),
            (252, "Thidranki (Level 20-24 - RR1L3)", 20, 24, 4, NOW),
            (253, "Abermenai (Level 15-19 - RR1L2)", 15, 19, 3, NOW),
        ])
        self.assertEqual(self.q("SELECT ZoneID, Name, Experience, LastTimeRowUpdated FROM Zones ORDER BY ZoneID"), [
            (250, "Caledonia", 0, NOW), (251, "Murdaigean", 0, "2017-01-08 18:51:19"),
            (252, "Thidranki", 0, NOW), (253, "Abermenai", 0, "2017-01-08 18:51:19"),
        ])
        self.assertEqual(self.q("SELECT RegionID, Description, LastTimeRowUpdated FROM Regions ORDER BY RegionID"), [
            (250, "Caledonia", NOW), (251, "Murdaigean", "2017-01-08 18:50:36"),
            (252, "Thidranki", "2017-01-08 18:50:36"), (253, "Abermenai", "2017-01-08 18:50:36"),
        ])
        self.assertEqual(self.q("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep ORDER BY KeepID"),
                         [(11, 24, 1, NOW), (31, 35, 1, NOW)])
        self.assertEqual(self.q("SELECT InternalID, Health, State, LastTimeRowUpdated FROM Door ORDER BY InternalID"), [
            (250000301, 7000, 1, NOW), (250000302, 7000, 1, NOW), (252000301, 4800, 1, NOW), (252000302, 4800, 1, NOW),
        ])
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"),
                         [("125d80ca-f7b0-4b13-8340-d14d1c306a94",), ("caledon-guard-23",)])
        self.assertEqual(self.q("SELECT Name FROM Quest"), [("DOL.GS.DailyQuest.Hibernia.CaptureKeepQuestHib",)])
        self.assertEqual(self.q("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", NOW)])

    def test_second_run_changes_nothing(self):
        self.assertEqual(apply_fix(self.db), self.LINES)
        before = dump(self.db)
        self.assertEqual(apply_fix(self.db, LATER), [])
        self.assertEqual(dump(self.db), before)

    def test_a_failing_step_rolls_back_everything(self):
        self.assertEqual([step.__name__ for step in battlegrounds.STEPS], list(self.FAILURES))
        for step, when in self.FAILURES.items():
            with self.subTest(step=step):
                db = self.world(f"{step}.db")
                execute(db, INJECTED.format(when))
                before = dump(db)
                self.assertEqual(apply_fix(db), [
                    "Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them"])
                self.assertEqual(dump(db), before)  # no change, no fork table, no marker
                execute(db, "DROP TRIGGER injected")
                self.assertEqual(apply_fix(db), self.LINES)  # so the next start tries again

    def test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails(self):
        execute(self.db, *WORLD_FIXES_SCHEMA,
                "INSERT INTO ServerProperty (Category, `Key`, Value) VALUES ('classes', 'disabled_classes', '20;33')",
                INJECTED.format("BEFORE UPDATE ON Keep"))
        r = self.run_world_fixes()
        self.assertEqual((r.returncode, r.stderr), (0, ""))
        self.assertEqual(r.stdout.splitlines(), [
            "Disciple (Necromancer's base class) enabled: disabled_classes 20;33 -> 33",
            "Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them",
        ])
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'"), [("33",)])
        self.assertEqual(self.q("SELECT name FROM sqlite_master WHERE name LIKE 'fork%'"), [])
        self.assertEqual(self.q("SELECT MaxRealmLevel FROM Battleground WHERE RegionID=252"), [(10,)])
        self.assertEqual(self.q("SELECT Name FROM Zones WHERE ZoneID=250"), [("Caledon",)])
        execute(self.db, "DROP TRIGGER injected")
        r = self.run_world_fixes()
        self.assertEqual((r.returncode, r.stderr, r.stdout.splitlines()), (0, "", self.LINES))

    def test_owner_values_are_kept(self):
        execute(self.db,
                "UPDATE Battleground SET MaxRealmLevel=7 WHERE RegionID=252",
                "UPDATE Zones SET Name='Caledonia Fields' WHERE ZoneID=250",
                "UPDATE Regions SET Description='Caledonia Fields' WHERE RegionID=250",
                "UPDATE Zones SET Experience=25 WHERE ZoneID=252",
                "UPDATE Keep SET BaseLevel=30 WHERE KeepID=11",
                "UPDATE Door SET Health=6000 WHERE InternalID=252000301",
                "UPDATE Mob SET Level=50 WHERE Mob_ID='caledon-guard-25'",
                # The owner removed all but one dummy and one Void Merchant; one saved quest is left.
                "DELETE FROM Mob WHERE Mob_ID IN ('08cf170a-3774-42c2-9816-934f78d5bd4e', "
                "'120a1ccf-94e8-4420-bc72-05ec20390e44', '853682fd-1de2-4100-adb5-02f05c8ed7d1', "
                "'27d30f96-e238-482d-b359-fc5387589108', 'ecb08ffb-cf86-47f1-a53b-28581a48666b')",
                "DELETE FROM Quest WHERE Quest_ID='quest-cale'")
        self.assertEqual(apply_fix(self.db), [
            "Battlegrounds: classic level and realm rank limits for Abermenai, Murdaigean, Caledonia",
            "Battlegrounds: no zone XP bonus in Caledonia",
            "Battlegrounds: keep levels for the ranges (Caer Caledon base level 35, 3 gates' health)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (1 training dummy, "
            "1 Void Merchant); 1 saved battleground daily quest deleted",
        ])
        self.assertEqual(self.q("SELECT Battleground_ID, MaxRealmLevel FROM Battleground WHERE RegionID=252"),
                         [("Thidranki (Level 20-24 - RR2L0)", 7)])
        self.assertEqual(self.q("SELECT Name, Experience FROM Zones WHERE ZoneID IN (250, 252) ORDER BY ZoneID"),
                         [("Caledonia Fields", 0), ("Thidranki", 25)])
        self.assertEqual(self.q("SELECT Description FROM Regions WHERE RegionID=250"), [("Caledonia Fields",)])
        self.assertEqual(self.q("SELECT BaseLevel FROM Keep WHERE KeepID=11"), [(30,)])
        self.assertEqual(self.q("SELECT Health FROM Door WHERE InternalID=252000301"), [(6000,)])
        self.assertEqual(self.q("SELECT Name, Level FROM Mob WHERE Mob_ID='caledon-guard-25'"), [("Wizard", 50)])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v1",)])

    def test_removed_rows_are_archived(self):
        mob = [(name, declared) for _, name, declared, *_ in self.q('PRAGMA table_info("Mob")')]
        names = ", ".join(name for name, _ in mob)
        rows = self.q(f"SELECT {names} FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))}) ORDER BY Mob_ID", REMOVED)
        self.assertEqual(len(rows), 8)
        apply_fix(self.db)
        self.assertEqual(self.q(f"SELECT Mob_ID FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))})", REMOVED), [])
        self.assertEqual(self.q(f"SELECT {names}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v1", NOW) for row in rows])
        self.assertEqual([(name, declared) for _, name, declared, *_ in self.q('PRAGMA table_info("fork_removed_mobs")')],
                         mob + [("FixId", "TEXT"), ("RemovedUtc", "TEXT")])

        # An archive table made before Mob gained a column (the server adds new columns to a world's tables
        # at start) still takes the rows, in the columns both tables have.
        db = self.world("older-archive.db")
        execute(db, "CREATE TABLE fork_removed_mobs (ClassType TEXT, Name VARCHAR(255), Region INT, "
                    "Mob_ID VARCHAR(255), FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL)")
        older = "ClassType, Name, Region, Mob_ID"
        rows = query(db, f"SELECT {older} FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))}) ORDER BY Mob_ID", REMOVED)
        apply_fix(db)
        self.assertEqual(query(db, f"SELECT {older}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v1", NOW) for row in rows])

    def test_no_marker_without_the_needed_tables(self):
        for table in battlegrounds.NEEDED_TABLES:
            with self.subTest(table=table):
                db = self.world(f"no-{table}.db")
                execute(db, f"DROP TABLE {table}")
                before = dump(db)
                self.assertEqual(apply_fix(db), [])
                self.assertEqual(dump(db), before)


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class BattlegroundShippedWorldTests(unittest.TestCase):
    """The clean classic world before the fix (read-only), and a copy of it after one run."""

    LINES = [
        "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
        "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
        "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
        "4 gates' health)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
        "3 Void Merchants, the stray Wizard)",
    ]
    LEFTOVERS = ("(ClassType IN ('DOL.GS.DPSDummy', 'DOL.GS.HitbackDummy', 'DOL.GS.HealDummy', "
                 "'DOL.GS.Scripts.RPTradeInMerchant') AND Region BETWEEN 250 AND 253) OR Mob_ID='caledon-guard-25'")
    GATES = "InternalID IN (250000301, 250000302, 252000301, 252000302)"

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.lines = apply_fix(cls.db)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def before(self, sql, params=()):
        conn = sqlite3.connect(pathlib.Path(TEST_WORLD).resolve().as_uri() + "?mode=ro", uri=True)
        try:
            return conn.execute(sql, params).fetchall()
        finally:
            conn.close()

    def after(self, sql, params=()):
        return query(self.db, sql, params)

    def test_world_holds_what_the_steps_expect(self):
        self.assertEqual(self.before("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel "
                                     "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45),
            (250, "Caledonia (Level 34-39 - RR3L5)", 30, 34, 25),
            (251, "Murdaigean (Level 25-29)", 25, 29, 5),
            (252, "Thidranki (Level 20-24 - RR2L0)", 20, 24, 10),
            (253, "Abermenai (Level 15-19)", 15, 19, 2),
        ])
        self.assertEqual(self.before("SELECT ZoneID, RegionID, Name, Experience FROM Zones WHERE RegionID BETWEEN 250 AND 253 "
                                     "ORDER BY ZoneID"), [
            (250, 250, "Caledon", 50), (251, 251, "Murdaigean", 0), (252, 252, "Thidranki", 50), (253, 253, "Abermenai", 0),
        ])
        self.assertEqual(self.before("SELECT RegionID, Description FROM Regions WHERE RegionID BETWEEN 250 AND 253 "
                                     "ORDER BY RegionID"),
                         [(250, "Caledon"), (251, "Murdaigean"), (252, "Thidranki"), (253, "Abermenai")])
        self.assertEqual(self.before("SELECT KeepID, Name, Region, BaseLevel, Level FROM Keep WHERE KeepID IN (11, 31) "
                                     "ORDER BY KeepID"),
                         [(11, "Thidranki Faste", 252, 26, 1), (31, "Caer Caledon", 250, 46, 1)])
        self.assertEqual(self.before(f"SELECT InternalID, Health, State FROM Door WHERE {self.GATES} ORDER BY InternalID"), [
            (250000301, 9200, 1), (250000302, 9200, 1), (252000301, 5200, 1), (252000302, 5200, 1),
        ])
        self.assertEqual(self.before("SELECT ClassType, Region, COUNT(*) FROM Mob WHERE ClassType IN ('DOL.GS.DPSDummy', "
                                     "'DOL.GS.HitbackDummy', 'DOL.GS.HealDummy') AND Region BETWEEN 250 AND 253 "
                                     "GROUP BY ClassType, Region ORDER BY ClassType"),
                         [("DOL.GS.DPSDummy", 252, 9), ("DOL.GS.HitbackDummy", 252, 6)])
        self.assertEqual(self.before("SELECT Mob_ID, Region, Realm FROM Mob WHERE ClassType='DOL.GS.Scripts.RPTradeInMerchant' "
                                     "AND Region BETWEEN 250 AND 253 ORDER BY Mob_ID"), [
            ("27d30f96-e238-482d-b359-fc5387589108", 252, 2),
            ("2ae9038a-72f0-4400-bbb1-53574dd5dfe9", 250, 1),
            ("ecb08ffb-cf86-47f1-a53b-28581a48666b", 252, 3),
        ])
        self.assertEqual(self.before("SELECT Mob_ID, ClassType, Name, Realm, Level, X, Y, Z, Region FROM Mob "
                                     "WHERE Mob_ID='caledon-guard-25'"),
                         [("caledon-guard-25", "DOL.GS.Keeps.GuardStaticCaster", "Wizard", 1, 48, 33185, 37386, 3722, 250)])
        self.assertEqual(self.before("SELECT COUNT(*) FROM Quest"), [(0,)])
        # KeepManager.ExitBattleground moves an over-limit character to its realm's portal keep by these
        # TeleportIDs; the fork fixed "Svasudheim Faste" to "Svasud Faste".
        self.assertEqual(self.before("SELECT TeleportID, Realm, RegionID, COUNT(*) FROM Teleport WHERE TeleportID IN "
                                     "('Castle Sauvage', 'Svasud Faste', 'Druim Ligen', 'Svasudheim Faste') "
                                     "GROUP BY TeleportID, Realm, RegionID ORDER BY TeleportID"),
                         [("Castle Sauvage", 1, 1, 2), ("Druim Ligen", 3, 200, 2), ("Svasud Faste", 2, 100, 2)])

    def test_after_the_fix(self):
        self.assertEqual(self.lines, self.LINES)
        self.assertEqual(self.after("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel, LastTimeRowUpdated "
                                    "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45, "2000-01-01 00:00:00"),
            (250, "Caledonia (Level 30-35 - RR1L9)", 30, 35, 10, NOW),
            (251, "Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6, NOW),
            (252, "Thidranki (Level 20-24 - RR1L3)", 20, 24, 4, NOW),
            (253, "Abermenai (Level 15-19 - RR1L2)", 15, 19, 3, NOW),
        ])
        self.assertEqual(self.after("SELECT ZoneID, Name, Experience FROM Zones WHERE RegionID BETWEEN 250 AND 253 "
                                    "ORDER BY ZoneID"),
                         [(250, "Caledonia", 0), (251, "Murdaigean", 0), (252, "Thidranki", 0), (253, "Abermenai", 0)])
        self.assertEqual(self.after("SELECT Description FROM Regions WHERE RegionID=250"), [("Caledonia",)])
        self.assertEqual(self.after("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep WHERE KeepID IN (11, 31) "
                                    "ORDER BY KeepID"), [(11, 24, 1, NOW), (31, 35, 1, NOW)])
        self.assertEqual(self.after(f"SELECT InternalID, Health, State FROM Door WHERE {self.GATES} ORDER BY InternalID"), [
            (250000301, 7000, 1), (250000302, 7000, 1), (252000301, 4800, 1), (252000302, 4800, 1),
        ])
        for sql in ("SELECT * FROM Keep WHERE KeepID NOT IN (11, 31) ORDER BY KeepID",
                    f"SELECT * FROM Door WHERE NOT {self.GATES} ORDER BY Door_ID"):
            with self.subTest(unchanged=sql):
                self.assertEqual(self.after(sql), self.before(sql))
        self.assertEqual(self.after(f"SELECT COUNT(*) FROM Mob WHERE {self.LEFTOVERS}"), [(0,)])
        mob = ", ".join(name for _, name, *_ in self.before('PRAGMA table_info("Mob")'))
        removed = self.before(f"SELECT {mob} FROM Mob WHERE {self.LEFTOVERS} ORDER BY Mob_ID")
        self.assertEqual(len(removed), 19)
        self.assertEqual(self.after(f"SELECT {mob}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v1", NOW) for row in removed])
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob")[0][0], self.before("SELECT COUNT(*) FROM Mob")[0][0] - 19)
        self.assertEqual(self.after("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", NOW)])

    def test_second_run_changes_nothing(self):
        conn = sqlite3.connect(self.db)
        try:
            with conn:
                self.assertEqual(battlegrounds.apply(conn, LATER), [])
            self.assertEqual(conn.total_changes, 0)
        finally:
            conn.close()


if __name__ == "__main__":
    unittest.main()
