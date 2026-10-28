import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import quest_dialogue as qd  # noqa: E402
import world_fixes as wf  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCHEMA = [
    "CREATE TABLE ServerProperty (Category TEXT NOT NULL DEFAULT '', `Key` VARCHAR(255) NOT NULL DEFAULT '', "
    "Description TEXT NOT NULL DEFAULT '', DefaultValue TEXT NOT NULL DEFAULT '', Value TEXT NOT NULL DEFAULT '', "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', ServerProperty_ID VARCHAR(255), PRIMARY KEY (`Key`))",
    "CREATE TABLE StartupLocation (StartupLoc_ID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, XPos INT NOT NULL DEFAULT 0, "
    "YPos INT NOT NULL DEFAULT 0, ZPos INT NOT NULL DEFAULT 0, Heading INT NOT NULL DEFAULT 0, Region INT NOT NULL DEFAULT 0, "
    "MinVersion INT NOT NULL DEFAULT 0, RealmID INT NOT NULL DEFAULT 0, RaceID INT NOT NULL DEFAULT 0, ClassID INT NOT NULL DEFAULT 0, "
    "ClientRegionID INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00')",
]


class EnableClassesTests(unittest.TestCase):
    def test_removes_a_single_id(self):
        self.assertEqual(wf.without_classes("20;33;34;39;58-62", {20}), "33;34;39;58-62")

    def test_splits_a_range(self):
        self.assertEqual(wf.without_classes("18-22;33", {20}), "18-19;21-22;33")
        self.assertEqual(wf.without_classes("20-21", {20}), "21")

    def test_keeps_other_separators_and_empty(self):
        self.assertEqual(wf.without_classes("", {20}), "")
        self.assertEqual(wf.without_classes("33,20", {20}), "33")


# Upstream's Mob columns these fixes read, and Level, Heading, Model, Size, Gender, RoamingRange and FactionID, which
# mob_fixes.json's entries name (a column they name that the
# table lacks stops the mob fixes).
MOB = ("CREATE TABLE Mob (Mob_ID VARCHAR(255) PRIMARY KEY, ClassType TEXT NOT NULL DEFAULT '', Name TEXT NOT NULL DEFAULT '', "
       "Guild TEXT NOT NULL DEFAULT '', ItemsListTemplateID TEXT, Region INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, "
       "Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, Heading INT NOT NULL DEFAULT 0, "
       "Model INT NOT NULL DEFAULT 0, Size INT NOT NULL DEFAULT 0, Gender INT NOT NULL DEFAULT 0, "
       "RoamingRange INT NOT NULL DEFAULT 0, FactionID INT NOT NULL DEFAULT 0, "
       "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00')")


class DuplicateTownspeopleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            for stmt in SCHEMA:
                c.execute(stmt)
            c.execute(MOB)
            self.add(c, "m1", "DOL.GS.GameMerchant", "Ley Manton", "tpl", 1, 511475, 380909, 7992)
            self.add(c, "n1", "DOL.GS.GameNPC", "Ley Manton", None, 1, 511475, 380909, 7992)
            self.add(c, "m2", "DOL.GS.GameMerchant", "Tria Ellowis", "tpl2", 1, 508468, 476039, 2304)
            self.add(c, "n2", "DOL.GS.GameNPC", "Tria Ellowis", "", 1, 508468, 476039, 2304)

    def tearDown(self):
        self.tmp.cleanup()

    @staticmethod
    def add(c, mob_id, cls, name, items, region, x, y, z):
        c.execute("INSERT INTO Mob (Mob_ID, ClassType, Name, ItemsListTemplateID, Region, X, Y, Z) VALUES (?,?,?,?,?,?,?,?)",
                  (mob_id, cls, name, items, region, x, y, z))

    def q(self, sql):
        with sqlite3.connect(self.db) as c:
            return c.execute(sql).fetchall()

    def test_plain_copies_go_and_the_merchants_stay(self):
        changes = wf.apply(self.db)
        self.assertEqual(changes, ["Duplicate townspeople removed, archived in fork_removed_mobs: Ley Manton, Tria Ellowis"])
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"), [("m1",), ("m2",)])
        self.assertEqual(self.q("SELECT Mob_ID, FixId FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [("n1", "duplicate-townspeople"), ("n2", "duplicate-townspeople")])

    def test_second_run_changes_nothing(self):
        wf.apply(self.db)
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM fork_removed_mobs"), [(2,)])

    def test_lone_plain_copy_is_kept(self):
        with sqlite3.connect(self.db) as c:
            c.execute("DELETE FROM Mob WHERE Mob_ID='m1'")
        wf.apply(self.db)
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"), [("m2",), ("n1",)])

    def test_copy_at_another_spot_is_kept(self):
        with sqlite3.connect(self.db) as c:
            c.execute("UPDATE Mob SET Z=Z+1 WHERE Mob_ID='n1'")
            c.execute("UPDATE Mob SET Region=2 WHERE Mob_ID='n2'")
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob"), [(4,)])

    def test_copy_with_an_item_list_is_kept(self):
        with sqlite3.connect(self.db) as c:
            c.execute("UPDATE Mob SET ItemsListTemplateID='x' WHERE Mob_ID='n1'")
        wf.apply(self.db)
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"), [("m1",), ("m2",), ("n1",)])

    def test_other_names_are_left_alone(self):
        with sqlite3.connect(self.db) as c:
            self.add(c, "a", "DOL.GS.GameMerchant", "Someone", "t", 1, 5, 5, 5)
            self.add(c, "b", "DOL.GS.GameNPC", "Someone", None, 1, 5, 5, 5)
        wf.apply(self.db)
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Name='Someone'"), [(2,)])


GRAENT_GOES, GRAENT_STAYS = "1e0dc1a7-6b01-4a38-845d-f8ef417a09e1", "deecddf1-2306-4194-a3e8-bab803d450fa"
TRAINER = "DOL.GS.Trainer.FighterTrainer"
GRAENT_LINE = "Duplicate townspeople removed, archived in fork_removed_mobs: Master Graent"


class DuplicateRowTests(unittest.TestCase):
    """Prydwen Keep's two Master Graents: the one among the merchants outside goes, the one in the keep stays."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            for stmt in SCHEMA:
                c.execute(stmt)
            c.execute(MOB)
            self.add(c, GRAENT_STAYS, TRAINER, "Master Graent", None, 1, 574197, 530518, 2933)
            self.add(c, GRAENT_GOES, TRAINER, "Master Graent", None, 1, 574886, 530169, 2896)

    def tearDown(self):
        self.tmp.cleanup()

    add = staticmethod(DuplicateTownspeopleTests.add)
    q = DuplicateTownspeopleTests.q

    def change(self, sql, *args):
        with sqlite3.connect(self.db) as c:
            c.execute(sql, args)

    def test_the_trainer_outside_goes_and_the_one_in_the_keep_stays(self):
        self.assertEqual(wf.apply(self.db), [GRAENT_LINE])
        self.assertEqual(self.q("SELECT Mob_ID, X, Y FROM Mob"), [(GRAENT_STAYS, 574197, 530518)])
        self.assertEqual(self.q("SELECT Mob_ID, Name, ClassType, Region, X, Y, Z, FixId FROM fork_removed_mobs"),
                         [(GRAENT_GOES, "Master Graent", TRAINER, 1, 574886, 530169, 2896, "duplicate-townspeople")])
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM fork_removed_mobs"), [(1,)])

    def test_one_line_names_the_townspeople_and_master_graent(self):
        with sqlite3.connect(self.db) as c:
            self.add(c, "m1", "DOL.GS.GameMerchant", "Ley Manton", "tpl", 1, 511475, 380909, 7992)
            self.add(c, "n1", "DOL.GS.GameNPC", "Ley Manton", None, 1, 511475, 380909, 7992)
            self.add(c, "m2", "DOL.GS.GameMerchant", "Tria Ellowis", "tpl2", 1, 508468, 476039, 2304)
            self.add(c, "n2", "DOL.GS.GameNPC", "Tria Ellowis", "", 1, 508468, 476039, 2304)
        self.assertEqual(wf.apply(self.db), [
            "Duplicate townspeople removed, archived in fork_removed_mobs: Ley Manton, Master Graent, Tria Ellowis"])
        self.assertEqual(self.q("SELECT Mob_ID FROM fork_removed_mobs ORDER BY Mob_ID"), [(GRAENT_GOES,), ("n1",), ("n2",)])

    def test_a_world_whose_townspeople_went_earlier_loses_only_master_graent(self):
        # A world started by hearth.7 to hearth.11: Ley Manton's and Tria Ellowis's copies are in the archive.
        with sqlite3.connect(self.db) as c:
            self.add(c, "m1", "DOL.GS.GameMerchant", "Ley Manton", "tpl", 1, 511475, 380909, 7992)
            self.add(c, "n1", "DOL.GS.GameNPC", "Ley Manton", None, 1, 511475, 380909, 7992)
        with mock.patch.object(wf, "DUPLICATE_ROWS", []):
            self.assertEqual(wf.apply(self.db), ["Duplicate townspeople removed, archived in fork_removed_mobs: Ley Manton"])
        self.assertEqual(wf.apply(self.db), [GRAENT_LINE])
        self.assertEqual(self.q("SELECT Mob_ID FROM fork_removed_mobs ORDER BY Mob_ID"), [(GRAENT_GOES,), ("n1",)])

    def test_a_row_the_owner_changed_stays(self):
        for column, value in (("Name", "Master Graentt"), ("ClassType", "DOL.GS.GameNPC"), ("Region", 2),
                              ("X", 574887), ("Y", 1), ("Z", 2897)):
            with self.subTest(column):
                self.change(f"UPDATE Mob SET {column}=? WHERE Mob_ID=?", value, GRAENT_GOES)
                try:
                    self.assertEqual(wf.apply(self.db), [])
                    self.assertEqual(self.q("SELECT COUNT(*) FROM Mob"), [(2,)])
                finally:
                    self.change("DELETE FROM Mob WHERE Mob_ID=?", GRAENT_GOES)
                    with sqlite3.connect(self.db) as c:
                        self.add(c, GRAENT_GOES, TRAINER, "Master Graent", None, 1, 574886, 530169, 2896)

    def test_without_the_trainer_in_the_keep_the_one_outside_stays(self):
        for name, sql in (("gone", "DELETE FROM Mob WHERE Mob_ID=?"),
                          ("renamed", "UPDATE Mob SET Name='Master Grant' WHERE Mob_ID=?"),
                          ("in another region", "UPDATE Mob SET Region=2 WHERE Mob_ID=?")):
            with self.subTest(name):
                self.change(sql, GRAENT_STAYS)
                try:
                    self.assertEqual(wf.apply(self.db), [])
                    self.assertEqual(self.q(f"SELECT Mob_ID FROM Mob WHERE Mob_ID='{GRAENT_GOES}'"), [(GRAENT_GOES,)])
                finally:
                    self.change("DELETE FROM Mob WHERE Mob_ID=?", GRAENT_STAYS)
                    with sqlite3.connect(self.db) as c:
                        self.add(c, GRAENT_STAYS, TRAINER, "Master Graent", None, 1, 574197, 530518, 2933)

    def test_only_that_row_goes_never_the_one_in_the_keep_or_another_at_its_spot(self):
        # The kept trainer moved onto the other's spot, and a third one there too: only the listed row goes.
        self.change("UPDATE Mob SET X=574886, Y=530169, Z=2896 WHERE Mob_ID=?", GRAENT_STAYS)
        with sqlite3.connect(self.db) as c:
            self.add(c, "another", TRAINER, "Master Graent", None, 1, 574886, 530169, 2896)
        self.assertEqual(wf.apply(self.db), [GRAENT_LINE])
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"), [("another",), (GRAENT_STAYS,)])
        # Without the listed row, nothing else of that name goes.
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob"), [(2,)])


COMMANDER_STAYS = "62348415-4e5e-482f-a804-c504e29c20f3"


class HostileCommanderTests(unittest.TestCase):
    """The outpost by Camelot's north gate: upstream's bestiary import put a realm-0 level-49 "commander" on the
    friendly Albion Commander's spot, and it attacked and killed the owner (2026-10-13). The fork removed it until
    upstream did, in 0.36 ("Removed the hostile copies of friendly NPCs that 0.35's classic bestiary spawns added",
    CHANGELOG.md); the fork's own removal went with the 0.37b sync, as a copy of upstream's fix."""

    def test_the_fork_lists_only_master_graent(self):
        self.assertEqual([kept for _row, kept in wf.DUPLICATE_ROWS], [GRAENT_STAYS])

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_the_world_has_only_the_albion_commander_there(self):
        conn = sqlite3.connect(f"file:{TEST_WORLD}?mode=ro", uri=True)
        try:
            spot = conn.execute("SELECT Mob_ID, Name, Realm FROM Mob WHERE Region=1 AND X=516726 AND Y=495717").fetchall()
        finally:
            conn.close()
        self.assertEqual(spot, [(COMMANDER_STAYS, "Commander", 1)])


class FixesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            for stmt in SCHEMA:
                c.execute(stmt)
            c.execute("INSERT INTO ServerProperty (Category, `Key`, Value) VALUES ('classes', 'disabled_classes', '20;33;34;39;58-62')")
            c.execute("INSERT INTO StartupLocation (XPos, YPos, ZPos, Heading, Region, RealmID, RaceID, ClassID) "
                      "VALUES (532903, 549729, 4800, 5559, 51, 1, 13, 20)")
            for key, text in wf.UPSTREAM_WELCOME.items():
                c.execute("INSERT INTO ServerProperty (Category, `Key`, DefaultValue, Value) VALUES ('server', ?, ?, ?)",
                          (key, text, text))

    def tearDown(self):
        self.tmp.cleanup()

    def q(self, sql):
        with sqlite3.connect(self.db) as c:
            return c.execute(sql).fetchall()

    def test_apply_enables_disciple_and_adds_the_saracen_start(self):
        changes = wf.apply(self.db)
        self.assertEqual(len(changes), 3)
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'"), [("33;34;39;58-62",)])
        self.assertEqual(self.q("SELECT XPos, YPos, ZPos, Heading, Region, RealmID FROM StartupLocation WHERE ClassID=20 AND RaceID=4"),
                         [(532903, 549729, 4800, 5559, 51, 1)])

    def test_second_run_changes_nothing(self):
        wf.apply(self.db)
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT COUNT(*) FROM StartupLocation WHERE ClassID=20 AND RaceID=4"), [(1,)])

    def test_owner_choices_are_kept(self):
        with sqlite3.connect(self.db) as c:
            c.execute("UPDATE ServerProperty SET Value='33;35' WHERE `Key`='disabled_classes'")
            c.execute("INSERT INTO StartupLocation (XPos, Region, RealmID, RaceID, ClassID) VALUES (1, 1, 1, 4, 20)")
            c.execute("UPDATE ServerProperty SET Value='Our own message' WHERE `Key`='motd'")
            c.execute("UPDATE ServerProperty SET Value='' WHERE `Key`='starting_msg'")
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT XPos FROM StartupLocation WHERE ClassID=20 AND RaceID=4"), [(1,)])
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key` IN ('motd', 'starting_msg') ORDER BY `Key`"),
                         [("Our own message",), ("",)])

    def test_welcome_messages_name_hearthdaoc(self):
        changes = wf.apply(self.db)
        self.assertIn("Welcome messages now name HearthDAoC (motd, starting_msg)", changes)
        rows = dict(self.q("SELECT `Key`, Value FROM ServerProperty WHERE `Key` IN ('motd', 'starting_msg')"))
        self.assertEqual(rows, wf.HEARTHDAOC_WELCOME)
        for text in rows.values():
            self.assertIn("HearthDAoC", text)
            self.assertNotIn("Offline", text)
            self.assertNotIn("alone", text)

    def test_cli(self):
        r = subprocess.run([sys.executable, os.path.join(BIN, "world_fixes.py"), "--db", self.db], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Disciple", r.stdout)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertEqual(wf.apply(self.db), [
            "Disciple (Necromancer's base class) enabled: disabled_classes 20;33;34;39;58-62 -> 33;34;39;58-62",
            "Saracen Disciples get a starting location (with the Inconnu Disciples, region 51)",
            "Welcome messages now name HearthDAoC (motd, starting_msg)",
            "Duplicate townspeople removed, archived in fork_removed_mobs: Ley Manton, Master Graent, Tria Ellowis",
            "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
            "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
            "Battlegrounds: keep levels for the ranges (Dun Abermenai base level 19, Thidranki Faste base level 24, "
            "Dun Murdaigean base level 29, Caer Caledon base level 35, 8 gates' health)",
            "Battlegrounds: portal keep guards and hasteners for Abermenai (34), Murdaigean (34)",
            "Battlegrounds: central keep guards for Dun Abermenai (6 wall casters, 1 hastener), "
            "Dun Murdaigean (6 wall casters, 1 hastener)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
            "3 Void Merchants, the stray Wizard)",
            "Epic chains: Guild of Shadows 60 links, 60 XP and coin, 4 Supply Runs closed, 2 rewards and 7 texts "
            "fixed; 87 other links; 0 items added, 41 replaced, 36 item fixes; 5 level-50 quests, Lord Elidyn's camp "
            "17 restored; Shadows_50: 0 finished carried, 0 removed, 0 epic vests recharged",
            "Quest dialogue: 142 quests rewritten",
            "Quest camps: copied back from upstream's archive: Delfina's camp (22), the isolationist camp (39), "
            "the Ellyll Seer's village (30), Lieutenant Grimarth (1); added: Lieutenant Cian, Mulgrut Maggot's first "
            "decayed zombie, Mulgrut Maggot's second decayed zombie, Mulgrut Maggot's putrid zombie; paths added: "
            "Mulgrut Maggot's walk (12 points); archived in fork_removed_mobs: the generated Bline (1), the standing "
            "Martley (1)",
            "Mob fixes: 126 corrected: Agisthil (Mob fe76247d-ab9e-5a21-b66a-f629e577c87b), Agisthil (NpcTemplate 12070), "
            "Frund (NpcTemplate 12165), Frund (Mob 3ce2271f-b9f6-4504-b55a-250da35504ba), "
            "Cloudy Green Stone (ItemTemplate cq_alb_cloudy_green_stone), "
            "Cloudy Green Stone (ItemTemplate cq_cloudy_green_stone), Flint Knife (ItemTemplate cq_flint_knife), "
            "Flinthead Club (ItemTemplate cq_flinthead_club), Flint Dirk (ItemTemplate cq_flint_dirk), "
            "Lady Leana (DataQuest 20022) and 116 more",
            "Route fixes: 138 bridge crossings of 59 horse routes given points on the deck: 12 on the Magical "
            "Bridge's north arch (Black Mtns. South), 12 on the Magical Bridge's south arch (Black Mtns. South), 26 "
            "on PKB (the Prydwen Keep bridge in Camelot Hills), 4 on the Cotswold bridgehouse (Camelot Hills), 4 on "
            "the MarshBridge (Avalon Marsh), 23 on East Svealand's bridge 506, 15 on East Svealand's bridge 736, 11 "
            "on Bridge Wood (East Svealand), 14 on the Big Bridge (Gotar), 11 on the Celtic bridge (Caillte Garran), "
            "6 on the stone bridge (Shannon Estuary)",
            "Start towns: 13 starting locations moved to the period's towns: Briton Mage to Adribard's Retreat (Avalon "
            "Marsh), Dwarf Seer to Vasudheim (East Svealand), Kobold Seer to Haggerfel (Vale of Mularn), Norseman Seer "
            "to Fort Atla (Gotar), Elf Stalker to Mag Mell (Lough Derg), Briton Disciple to Caer Gothwaite (Shrouded "
            "Isles), Troll Mystic to Aegirhamn (Shrouded Isles), Celt Forester to the Grove of Domnann (Shrouded "
            "Isles), Firbolg Forester to the Grove of Domnann (Shrouded Isles), Highlander Scout to Ludlow Village "
            "(Black Mtns. South), Elf Blademaster to Howth (Silvermine Mts.), Elf Champion to Howth (Silvermine Mts.), "
            "Norseman Healer to Fort Atla (Gotar)",
            "Starter guilds: removed Clan Cotswold, Mularn Protectors, Tir na Nog Adventurers and their ranks; "
            "they had no members",
        ])
        self.assertEqual(self.q("SELECT Mob_ID FROM fork_removed_mobs WHERE FixId='duplicate-townspeople' ORDER BY Mob_ID"),
                         [(GRAENT_GOES,), ("231313cf-eb6a-408f-9811-f697c705ffb7",),
                          ("cc83cc14-e5ff-4eb1-b35c-8ad140793909",)])
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob WHERE Name IN ('Ley Manton', 'Tria Ellowis') ORDER BY Mob_ID"),
                         [("2b478cbe-cb3f-44e3-bc86-c5f05ad4714d",), ("a4de3772-723f-452f-9c1a-a00a64e50922",)])
        self.assertEqual(self.q("SELECT Mob_ID, Region, X, Y, Z FROM Mob WHERE Name='Master Graent'"),
                         [(GRAENT_STAYS, 1, 574197, 530518, 2933)])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes ORDER BY FixId"),
                         [("classic-battlegrounds-v2",), ("epic-chains-v1",), ("quest-camp:delfina",),
                          ("quest-camp:ellyll-seer",), ("quest-camp:generated-bline",), ("quest-camp:grimarth",),
                          ("quest-camp:isolationists",), ("quest-camp:lieutenant-cian",),
                          ("quest-camp:mulgrut-escort-1",), ("quest-camp:mulgrut-escort-2",),
                          ("quest-camp:mulgrut-escort-3",), ("quest-camp:mulgrut-walk",),
                          ("quest-camp:standing-martley",)])
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'"), [("33;34;39;58-62",)])
        self.assertEqual(self.q("SELECT RaceID FROM StartupLocation WHERE ClassID=20 ORDER BY RaceID"), [(1,), (4,), (13,)])
        self.assertEqual(dict(self.q("SELECT `Key`, Value FROM ServerProperty WHERE `Key` IN ('motd', 'starting_msg')")),
                         wf.HEARTHDAOC_WELCOME)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world_started_before_master_graent_was_listed(self):
        # A world that hearth.7 to hearth.11 started had every other fix already: its next start removes only the
        # Master Graent among the merchants outside Prydwen Keep.
        shutil.copyfile(TEST_WORLD, self.db)
        with mock.patch.object(wf, "DUPLICATE_ROWS", []):
            self.assertIn("Duplicate townspeople removed, archived in fork_removed_mobs: Ley Manton, Tria Ellowis",
                          wf.apply(self.db))
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Name='Master Graent'"), [(2,)])
        self.assertEqual(wf.apply(self.db), [GRAENT_LINE])
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob WHERE Name='Master Graent'"), [(GRAENT_STAYS,)])
        self.assertEqual(self.q(f"SELECT Name, NPCTemplateID, X, Y FROM fork_removed_mobs WHERE Mob_ID='{GRAENT_GOES}'"),
                         [("Master Graent", -1, 574886, 530169)])
        self.assertEqual(wf.apply(self.db), [])

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_no_monster_stands_by_a_realm_npc_of_its_name(self):
        # The commander's defect: upstream's bestiary import (PackageID BestiaryG9, "placed where 1.65 radar logs saw
        # them, never inside towns") made realm-0 monsters of names its logs saw, and one was the Albion Commander.
        # Upstream 0.36 removed it and Dodens Gruva's six realm-0 "innocent": after the fixes no realm-0 row stands
        # within 3,000 units of a realm NPC of its name.
        shutil.copyfile(TEST_WORLD, self.db)
        wf.apply(self.db)
        rows = self.q("""SELECT DISTINCT h.Mob_ID, lower(h.Name), h.Region FROM Mob h JOIN Mob f
                         ON f.Region = h.Region AND lower(f.Name) = lower(h.Name) AND f.Realm <> 0 AND f.Mob_ID <> h.Mob_ID
                         WHERE h.Realm = 0 AND abs(h.X - f.X) < 3000 AND abs(h.Y - f.Y) < 3000""")
        self.assertEqual(rows, [])

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world_started_before_the_classic_quests_texts(self):
        # A world with this file's Guild of Shadows dialogue (its step entries) but not the classic quests' texts, as
        # hearth.9 to hearth.11 left it apart from later revisions of the chain's own text: its next start rewrites only
        # the classic quests' texts (the ids entries, 77 quest rows), and the next nothing.
        shutil.copyfile(TEST_WORLD, self.db)
        data = qd.load_data()
        with mock.patch.object(qd, "load_data", return_value=dict(data, quests=[e for e in data["quests"] if "step" in e])):
            self.assertIn("Quest dialogue: 65 quests rewritten", wf.apply(self.db))
        self.assertEqual(wf.apply(self.db), ["Quest dialogue: 77 quests rewritten"])
        self.assertEqual(wf.apply(self.db), [])


if __name__ == "__main__":
    unittest.main()
