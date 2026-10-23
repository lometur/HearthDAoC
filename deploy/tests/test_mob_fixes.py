"""Period corrections to monsters: deploy/bin/mob_fixes.py and its data file mob_fixes.json.

The synthetic tests use tiny Mob and NpcTemplate tables and data passed in. The tests of the real data file run
world_fixes on a copy of a clean classic world (HDC_TEST_WORLD).
"""
import json
import math
import os
import shutil
import sqlite3
import sys
import tempfile
import unittest
import unittest.mock

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import mob_fixes as mf  # noqa: E402
import world_fixes as wf  # noqa: E402
from tests.test_world_fixes import SCHEMA as WORLD_FIXES_SCHEMA  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCHEMA = [
    "CREATE TABLE Mob (Name VARCHAR(255) NOT NULL DEFAULT '', X INT(11) NOT NULL DEFAULT 0, Y INT(11) NOT NULL DEFAULT 0, "
    "Z INT(11) NOT NULL DEFAULT 0, Region UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, Level UNSIGNED TINYINT(3) NOT NULL DEFAULT 0, "
    "RoamingRange INT(11) NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Mob_ID VARCHAR(255) NOT NULL DEFAULT '', PRIMARY KEY (Mob_ID))",
    "CREATE TABLE NpcTemplate (TemplateId INT(11) NOT NULL DEFAULT 0, Name TEXT NOT NULL DEFAULT '', "
    "Level TEXT NOT NULL DEFAULT '', LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "NpcTemplate_ID VARCHAR(255) NOT NULL DEFAULT '', PRIMARY KEY (NpcTemplate_ID))",
    "CREATE TABLE ItemTemplate (Id_nb VARCHAR(255) NOT NULL DEFAULT '', Name TEXT NOT NULL DEFAULT '', "
    "Price BIGINT(20) NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "PRIMARY KEY (Id_nb))",
]
# A quest row for the synthetic tests only: quest_dialogue (which world_fixes runs too) wants the whole table.
QUEST_SCHEMA = ("CREATE TABLE DataQuest (ID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Name VARCHAR(255) NOT NULL DEFAULT '', "
                "FinalRewardItemTemplates TEXT DEFAULT NULL, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00')")
# Upstream's ZonePoint table (CoreDatabase/Tables/DbZonePoint.cs), for the zone point tests only.
ZONE_POINT_SCHEMA = ("CREATE TABLE ZonePoint (Id UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, TargetX INT(11) NOT NULL DEFAULT 0, "
                     "TargetY INT(11) NOT NULL DEFAULT 0, TargetZ INT(11) NOT NULL DEFAULT 0, "
                     "TargetRegion UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, TargetHeading UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, "
                     "SourceX INT(11) NOT NULL DEFAULT 0, SourceY INT(11) NOT NULL DEFAULT 0, SourceZ INT(11) NOT NULL DEFAULT 0, "
                     "SourceRegion UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, Realm UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, "
                     "ClassType TEXT DEFAULT NULL COLLATE NOCASE, "
                     "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
                     "ZonePoint_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (ZonePoint_ID))")
OLD = "2000-01-01 00:00:00"
NOW = "2026-10-10 12:00:00"


def fixes():
    return {"fixes": [
        {"name": "Agisthil", "table": "Mob", "id": "agisthil", "expect": {"Level": 12}, "set": {"Level": 10},
         "why": "bestiary"},
        {"name": "Frund", "table": "NpcTemplate", "id": 12165, "expect": {"Level": "12-13"}, "set": {"Level": "10"},
         "why": "bestiary"},
        {"name": "Frund", "table": "Mob", "id": "frund", "expect": {"Region": 1, "X": 100, "Y": 200, "Z": 30},
         "set": {"X": 500, "Y": 600, "Z": 70}, "why": "walkthrough"},
    ]}


def add_rows(conn):
    conn.execute("INSERT INTO Mob (Mob_ID, Name, Level, Region, X, Y, Z, RoamingRange) VALUES ('agisthil', 'Agisthil', 12, 1, 9, 9, 9, 0)")
    conn.execute("INSERT INTO Mob (Mob_ID, Name, Level, Region, X, Y, Z, RoamingRange) VALUES ('frund', 'Frund', 0, 1, 100, 200, 30, 200)")
    conn.execute("INSERT INTO NpcTemplate (NpcTemplate_ID, TemplateId, Name, Level) VALUES ('t1', 12165, 'Frund', '12-13')")
    conn.execute("INSERT INTO ItemTemplate (Id_nb, Name, Price) VALUES ('stone', 'Cloudy Green Stone', 0)")


class SyntheticTests(unittest.TestCase):
    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        for stmt in SCHEMA + [QUEST_SCHEMA]:
            self.conn.execute(stmt)
        add_rows(self.conn)
        self.conn.execute("INSERT INTO DataQuest (ID, Name, FinalRewardItemTemplates) VALUES (20207, 'Cleric Mulgrut', 'belt|alb_belt')")
        self.conn.commit()

    def tearDown(self):
        self.conn.close()

    def run_fix(self, data=None):
        with self.conn:
            return mf.apply(self.conn, NOW, data if data is not None else fixes())

    def rows(self):
        return (self.conn.execute("SELECT * FROM Mob ORDER BY Mob_ID").fetchall(),
                self.conn.execute("SELECT * FROM NpcTemplate ORDER BY NpcTemplate_ID").fetchall())

    def mob(self, mob_id, *columns):
        return self.conn.execute(f"SELECT {', '.join(columns)} FROM Mob WHERE Mob_ID=?", (mob_id,)).fetchone()

    def test_a_row_that_holds_upstreams_values_is_corrected(self):
        self.assertEqual(self.run_fix(), ["Mob fixes: 3 corrected: Agisthil (Mob agisthil), Frund (NpcTemplate 12165), "
                                          "Frund (Mob frund)"])
        self.assertEqual(self.mob("agisthil", "Level", "LastTimeRowUpdated"), (10, NOW))
        self.assertEqual(self.mob("frund", "Region", "X", "Y", "Z", "RoamingRange", "Level", "LastTimeRowUpdated"),
                         (1, 500, 600, 70, 200, 0, NOW))
        self.assertEqual(self.conn.execute("SELECT Level, LastTimeRowUpdated FROM NpcTemplate").fetchone(), ("10", NOW))

    # Owner 2026-10-13: a later correction of a row an earlier one already corrected, in another column (the level-7
    # Infiltrator quest got Master Eadig in one release and Bline Tengit in the next). Each entry checks its own columns.
    TWO = {"fixes": [
        {"name": "Mulgrut giver", "table": "DataQuest", "id": 20207, "expect": {"Name": "Cleric Mulgrut"},
         "set": {"Name": "Cleric Mulgrut (fixed)"}, "why": "2026"},
        {"name": "Mulgrut reward", "table": "DataQuest", "id": 20207, "expect": {"FinalRewardItemTemplates": "belt|alb_belt"},
         "set": {"FinalRewardItemTemplates": "alb_belt"}, "why": "2026"},
    ]}

    def test_two_entries_of_one_row_in_other_columns_each_apply(self):
        self.run_fix({"fixes": self.TWO["fixes"][:1]})  # an earlier release had only the first
        self.assertEqual(self.run_fix(self.TWO), ["Mob fixes: 1 corrected: Mulgrut reward (DataQuest 20207)"])
        self.assertEqual(self.conn.execute("SELECT Name, FinalRewardItemTemplates FROM DataQuest WHERE ID=20207").fetchone(),
                         ("Cleric Mulgrut (fixed)", "alb_belt"))
        self.assertEqual(self.run_fix(self.TWO), [])

    def test_two_entries_of_one_row_may_not_share_a_column(self):
        data = {"fixes": [self.TWO["fixes"][0], dict(self.TWO["fixes"][1], expect={"Name": "x"}, set={"Name": "y"})]}
        line, = self.run_fix(data)
        self.assertEqual(line, "Mob fixes: not applied (fix 2 (Mulgrut reward): another entry of its row also has Name); "
                               "the monsters, items, quests and zone points keep the values they have")

    # The 0.37 sync: a decision made of entries that depend on each other (a carried-only copy made the reward, and the
    # quest's second copy dropped) applies whole or not at all, so it never leaves two real rewards, or none.
    GROUP = "Cleric Mulgrut's belt"
    GROUPED = {"fixes": [
        {"name": "Belt of Animation", "table": "ItemTemplate", "id": "stone", "group": GROUP, "expect": {"Price": 0},
         "set": {"Price": 100}, "why": "2026"},
        {"name": "Agisthil", "table": "Mob", "id": "agisthil", "expect": {"Level": 12}, "set": {"Level": 10}, "why": "2026"},
        {"name": "Cleric Mulgrut", "table": "DataQuest", "id": 20207, "group": GROUP,
         "expect": {"FinalRewardItemTemplates": "belt|alb_belt"}, "set": {"FinalRewardItemTemplates": "belt"},
         "why": "2026"},
    ]}
    HELD = ("1 group left as it is (an entry's row holds neither upstream's values nor this file's, or is missing, so no "
            "row of the group changes): Cleric Mulgrut's belt ({})")

    def grouped_rows(self):
        return (self.conn.execute("SELECT Price, LastTimeRowUpdated FROM ItemTemplate WHERE Id_nb='stone'").fetchall(),
                self.conn.execute("SELECT FinalRewardItemTemplates, LastTimeRowUpdated FROM DataQuest").fetchall())

    def test_a_whole_group_applies_and_a_second_run_is_silent(self):
        self.assertEqual(self.run_fix(self.GROUPED), ["Mob fixes: 3 corrected: Belt of Animation (ItemTemplate stone), "
                                                      "Agisthil (Mob agisthil), Cleric Mulgrut (DataQuest 20207)"])
        self.assertEqual(self.grouped_rows(), ([(100, NOW)], [("belt", NOW)]))
        self.assertEqual(self.run_fix(self.GROUPED), [])

    def test_a_group_half_matching_changes_none_of_its_rows_and_is_named_at_every_start(self):
        self.conn.execute("UPDATE DataQuest SET FinalRewardItemTemplates='alb_belt'")  # the owner's own change
        self.conn.commit()
        before = self.grouped_rows()
        line = "Mob fixes: 1 corrected: Agisthil (Mob agisthil); " + self.HELD.format("Cleric Mulgrut (DataQuest 20207)")
        self.assertEqual(self.run_fix(self.GROUPED), [line])
        self.assertEqual(self.grouped_rows(), before)  # the belt is not made the reward while the quest still hands two
        self.assertEqual(self.run_fix(self.GROUPED), ["Mob fixes: " + self.HELD.format("Cleric Mulgrut (DataQuest 20207)")])
        self.assertEqual(self.grouped_rows(), before)

    def test_a_group_whose_row_is_missing_changes_none_of_its_rows(self):
        self.conn.execute("DELETE FROM ItemTemplate WHERE Id_nb='stone'")
        self.conn.commit()
        self.assertEqual(self.run_fix(self.GROUPED), [
            "Mob fixes: 1 corrected: Agisthil (Mob agisthil); " + self.HELD.format("Belt of Animation (ItemTemplate stone)")])
        self.assertEqual(self.grouped_rows()[1], [("belt|alb_belt", OLD)])

    def test_a_group_entry_already_at_its_values_lets_the_others_apply(self):
        self.conn.execute("UPDATE ItemTemplate SET Price=100 WHERE Id_nb='stone'")  # an earlier release had that entry
        self.conn.commit()
        self.assertEqual(self.run_fix(self.GROUPED), ["Mob fixes: 2 corrected: Agisthil (Mob agisthil), "
                                                      "Cleric Mulgrut (DataQuest 20207)"])
        self.assertEqual(self.grouped_rows(), ([(100, OLD)], [("belt", NOW)]))

    def test_a_group_none_of_whose_rows_is_in_the_world_is_skipped(self):
        self.conn.execute("DELETE FROM ItemTemplate")
        self.conn.execute("DELETE FROM DataQuest")
        self.conn.commit()
        self.assertEqual(self.run_fix(self.GROUPED), ["Mob fixes: 1 corrected: Agisthil (Mob agisthil)"])

    def test_a_second_run_changes_nothing_and_prints_nothing(self):
        self.run_fix()
        self.conn.execute("UPDATE Mob SET LastTimeRowUpdated=?", (OLD,))
        self.conn.execute("UPDATE NpcTemplate SET LastTimeRowUpdated=?", (OLD,))
        self.conn.commit()
        before = self.rows()
        self.assertEqual(self.run_fix(), [])
        self.assertEqual(self.rows(), before)

    def test_a_row_the_owner_changed_is_kept_and_named_at_every_start(self):
        self.conn.execute("UPDATE Mob SET X=101 WHERE Mob_ID='frund'")  # the owner moved him a little
        self.conn.execute("UPDATE NpcTemplate SET Level='14'")
        self.conn.commit()
        kept = ("2 left as they are (they hold neither upstream's values nor this file's): Frund (NpcTemplate 12165), "
                "Frund (Mob frund)")
        self.assertEqual(self.run_fix(), ["Mob fixes: 1 corrected: Agisthil (Mob agisthil); " + kept])
        self.assertEqual(self.mob("frund", "X", "Y", "Z", "LastTimeRowUpdated"), (101, 200, 30, OLD))
        self.assertEqual(self.conn.execute("SELECT Level FROM NpcTemplate").fetchone(), ("14",))
        self.assertEqual(self.run_fix(), ["Mob fixes: " + kept])

    def test_one_value_that_differs_keeps_the_whole_row(self):
        # The row changes only while it holds every expected value: Region too, which the fix doesn't set.
        self.conn.execute("UPDATE Mob SET Region=2 WHERE Mob_ID='frund'")
        self.conn.commit()
        self.assertIn("left as they are (they hold neither upstream's values nor this file's): Frund (Mob frund)",
                      self.run_fix()[0])
        self.assertEqual(self.mob("frund", "X", "Y", "Z"), (100, 200, 30))

    def test_a_row_already_at_the_files_values_is_up_to_date(self):
        self.conn.execute("UPDATE Mob SET Level=10 WHERE Mob_ID='agisthil'")
        self.conn.commit()
        self.assertEqual(self.run_fix(), ["Mob fixes: 2 corrected: Frund (NpcTemplate 12165), Frund (Mob frund)"])
        self.assertEqual(self.mob("agisthil", "LastTimeRowUpdated"), (OLD,))

    def test_values_compare_as_sqlite_compares_them_with_the_column(self):
        data = fixes()
        data["fixes"][0]["expect"] = {"Level": "12"}  # text for an integer column
        data["fixes"][1]["expect"] = {"Level": "12-13"}
        self.assertTrue(self.run_fix(data)[0].startswith("Mob fixes: 3 corrected: "))
        self.assertEqual(self.mob("agisthil", "Level"), (10,))

    def test_an_item_template_row_is_corrected(self):
        data = {"fixes": [{"name": "Cloudy Green Stone", "table": "ItemTemplate", "id": "stone", "expect": {"Price": 0},
                           "set": {"Price": 100}, "why": "Allakhazam"}]}
        self.assertEqual(self.run_fix(data), ["Mob fixes: 1 corrected: Cloudy Green Stone (ItemTemplate stone)"])
        self.assertEqual(self.conn.execute("SELECT Price, LastTimeRowUpdated FROM ItemTemplate WHERE Id_nb='stone'").fetchone(),
                         (100, NOW))
        self.assertEqual(self.run_fix(data), [])

    def test_a_quest_row_is_corrected(self):
        # A quest that lists one reward under two templates hands both (DataQuest gives every final reward).
        data = {"fixes": [{"name": "Cleric Mulgrut", "table": "DataQuest", "id": 20207,
                           "expect": {"FinalRewardItemTemplates": "belt|alb_belt"},
                           "set": {"FinalRewardItemTemplates": "alb_belt"}, "why": "Allakhazam"}]}
        self.assertEqual(self.run_fix(data), ["Mob fixes: 1 corrected: Cleric Mulgrut (DataQuest 20207)"])
        self.assertEqual(self.conn.execute("SELECT FinalRewardItemTemplates, LastTimeRowUpdated FROM DataQuest").fetchone(),
                         ("alb_belt", NOW))
        self.assertEqual(self.run_fix(data), [])

    def test_a_zone_point_row_is_corrected(self):
        # The Shrouded Isles portals at their 1.65 spots (2026-10-22): a zone point's source spot, by its Id. Its
        # target, realm and class stay.
        self.conn.execute(ZONE_POINT_SCHEMA)
        self.conn.execute("INSERT INTO ZonePoint (ZonePoint_ID, Id, TargetRegion, TargetX, TargetY, TargetZ, SourceRegion, "
                          "SourceX, SourceY, SourceZ) VALUES ('ALB SI ENTRANCE', 153, 51, 525818, 542658, 3168, 1, 564976, "
                          "509200, 2811)")
        self.conn.commit()
        data = {"fixes": [{"name": "Albion Shrouded Isles portal", "table": "ZonePoint", "id": 153,
                           "expect": {"SourceRegion": 1, "SourceX": 564976, "SourceY": 509200, "SourceZ": 2811},
                           "set": {"SourceX": 462038, "SourceY": 632414, "SourceZ": 1707}, "why": "1.74 notes, 2005"}]}
        self.assertEqual(self.run_fix(data), ["Mob fixes: 1 corrected: Albion Shrouded Isles portal (ZonePoint 153)"])
        self.assertEqual(self.conn.execute("SELECT Id, SourceRegion, SourceX, SourceY, SourceZ, TargetRegion, TargetX, TargetY, "
                                           "TargetZ, Realm, ClassType, LastTimeRowUpdated FROM ZonePoint").fetchall(),
                         [(153, 1, 462038, 632414, 1707, 51, 525818, 542658, 3168, 0, None, NOW)])
        self.assertEqual(self.run_fix(data), [])

    def test_a_quest_rows_id_is_never_set(self):
        data = {"fixes": [{"name": "Cleric Mulgrut", "table": "DataQuest", "id": 20207, "expect": {"ID": 20207},
                           "set": {"ID": 1}, "why": "Allakhazam"}]}
        self.assertEqual(self.run_fix(data), ["Mob fixes: not applied (fix 1 (Cleric Mulgrut): set changes the row's ID); "
                                              "the monsters, items, quests and zone points keep the values they have"])
        self.assertEqual(self.conn.execute("SELECT ID FROM DataQuest").fetchall(), [(20207,)])

    def test_every_npc_template_row_of_the_id_is_corrected(self):
        self.conn.execute("INSERT INTO NpcTemplate (NpcTemplate_ID, TemplateId, Name, Level) VALUES ('t2', 12165, 'Frund', '12-13')")
        self.conn.commit()
        self.run_fix()
        self.assertEqual(self.conn.execute("SELECT Level FROM NpcTemplate ORDER BY NpcTemplate_ID").fetchall(), [("10",), ("10",)])

    def test_a_missing_row_or_table_is_skipped(self):
        self.conn.execute("DELETE FROM Mob WHERE Mob_ID='frund'")
        self.conn.execute("DROP TABLE NpcTemplate")
        self.conn.commit()
        self.assertEqual(self.run_fix(), ["Mob fixes: 1 corrected: Agisthil (Mob agisthil)"])

    def test_no_tables_is_not_an_error(self):
        conn = sqlite3.connect(":memory:")
        self.assertEqual(mf.apply(conn, NOW, fixes()), [])
        conn.close()

    def test_the_lists_are_capped(self):
        names = [f"m{i}" for i in range(12)]
        self.assertEqual(mf.summary(names, []), ["Mob fixes: 12 corrected: m0, m1, m2, m3, m4, m5, m6, m7, m8, m9 and 2 more"])
        self.assertEqual(mf.summary([], []), [])

    def test_a_bad_file_rolls_back_the_fix_and_says_so(self):
        def changed(**fields):
            data = fixes()
            data["fixes"][2].update(fields)
            return data

        bad = {
            "an unknown column in set": changed(set={"X": 1, "Colour": 2}, expect={"X": 100, "Colour": 3}),
            "an unknown column in expect": changed(expect={"X": 100, "Y": 200, "Z": 30, "Region": 1, "Heading2": 0}),
            "an unknown table": changed(table="Monster"),
            "a set column expect lacks": changed(expect={"X": 100, "Y": 200}),
            "set changes the id": changed(set={"Mob_ID": "x"}, expect={"Mob_ID": "frund"}),
            "an empty set": changed(set={}),
            "no why": changed(why=""),
            "no name": changed(name=None),
            "no id": changed(id=None),
            "a value that is a list": changed(set={"X": [1]}, expect={"X": 100}),
            "a value that is a boolean": changed(set={"X": True}, expect={"X": 100}),
            "a row twice": {"fixes": fixes()["fixes"] + [fixes()["fixes"][0]]},
            "a group of one entry": changed(group="Frund"),
            "a group that is not a name": {"fixes": [dict(entry, group=3) for entry in fixes()["fixes"]]},
            "an empty group name": {"fixes": [dict(entry, group=" ") for entry in fixes()["fixes"]]},
        }
        for what, data in bad.items():
            with self.subTest(what):
                before = self.rows()
                lines = self.run_fix(data)
                self.assertEqual(len(lines), 1, lines)
                self.assertTrue(lines[0].startswith("Mob fixes: not applied ("), lines[0])
                self.assertEqual(self.rows(), before)

    def test_the_unknown_column_is_named(self):
        data = fixes()
        data["fixes"][0]["set"] = {"Lvl": 10}
        data["fixes"][0]["expect"] = {"Lvl": 12}
        self.assertEqual(self.run_fix(data), ["Mob fixes: not applied (fix 1 (Agisthil): Mob has no column Lvl); "
                                              "the monsters, items, quests and zone points keep the values they have"])

    def test_a_missing_data_file_is_a_not_applied_line(self):
        with unittest.mock.patch.object(mf, "load_data", side_effect=FileNotFoundError("mob_fixes.json")):
            with self.conn:
                lines = mf.apply(self.conn, NOW)
        self.assertEqual(len(lines), 1)
        self.assertTrue(lines[0].startswith("Mob fixes: not applied ("), lines)


class FailureLeavesTheOtherFixesTests(unittest.TestCase):
    """world_fixes.apply: a failing mob fix undoes only itself; the fixes before it are saved."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            for stmt in WORLD_FIXES_SCHEMA + SCHEMA:
                c.execute(stmt)
            c.execute("INSERT INTO ServerProperty (`Key`, Value) VALUES ('disabled_classes', '20;33')")
            add_rows(c)

    def tearDown(self):
        self.tmp.cleanup()

    def test_a_bad_data_file_does_not_stop_the_other_fixes(self):
        bad = fixes()
        bad["fixes"][0]["set"] = {"Lvl": 10}
        bad["fixes"][0]["expect"] = {"Lvl": 12}
        with unittest.mock.patch.object(mf, "load_data", return_value=bad):
            changes = wf.apply(self.db)
        self.assertEqual(changes[0], "Disciple (Necromancer's base class) enabled: disabled_classes 20;33 -> 33")
        self.assertEqual(len(changes), 2)
        self.assertTrue(changes[1].startswith("Mob fixes: not applied ("), changes)
        with sqlite3.connect(self.db) as c:
            self.assertEqual(c.execute("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'").fetchone(), ("33",))
            self.assertEqual(c.execute("SELECT Level, X FROM Mob ORDER BY Mob_ID").fetchall(), [(12, 9), (0, 100)])

    def test_the_mob_fixes_run_after_the_other_fixes(self):
        with unittest.mock.patch.object(mf, "load_data", return_value=fixes()):
            changes = wf.apply(self.db)
        self.assertEqual(changes[-1], "Mob fixes: 3 corrected: Agisthil (Mob agisthil), Frund (NpcTemplate 12165), "
                                      "Frund (Mob frund)")


class DataFileTests(unittest.TestCase):
    """The shape of mob_fixes.json (no world needed)."""

    def test_every_entry_is_well_formed_and_says_why(self):
        data = mf.load_data()
        self.assertTrue(data["fixes"])
        conn = sqlite3.connect(":memory:")
        try:
            self.assertEqual(len(mf._plan(conn, data)), 0)  # no tables: checks every entry, plans none
        finally:
            conn.close()
        for entry in data["fixes"]:
            with self.subTest(entry["name"], table=entry["table"]):
                self.assertGreater(len(entry["why"]), 20)
                self.assertRegex(entry["why"], r"\d{4}")  # its source is dated


AGISTHIL = "fe76247d-ab9e-5a21-b66a-f629e577c87b"
FRUND = "3ce2271f-b9f6-4504-b55a-250da35504ba"
# The decisions made of entries that depend on each other, each applied whole or not at all: the reward decisions (the
# 0.37 sync; test_quest_rewards finds them: a carried-only copy made the reward, or priced, and the quest's other copy
# dropped), and the three realms' Shrouded Isles portals moved together to their 1.65 spots (test_si_portals).
GROUPS = {
    "Zombie Problem's stone": ["Cloudy Green Stone (ItemTemplate cq_alb_cloudy_green_stone)",
                               "Cloudy Green Stone (ItemTemplate cq_cloudy_green_stone)",
                               "Zombie Problem (DataQuest 20515)"],
    "Ogre's Might's rewards": ["Ogre's Might (DataQuest 21371)", "Scurceol's Bracer (ItemTemplate cq_scurceol_s_bracer)"],
    "Strange Bedfellows' reward": ["Strange Bedfellows (DataQuest 20975)",
                                   "Perfect Life Stone (ItemTemplate cq_perfect_life_stone)"],
    "Darksong's Dirge's ring": ["Darksong's Dirge, Healer (DataQuest 21021)", "Darksong's Dirge, Shaman (DataQuest 21022)",
                                "Pious Ring (ItemTemplate cq_pious_ring)"],
    "Dark Places of the Soul's rewards": ["Dark Places of the Soul (DataQuest 21264)",
                                          "Ring of Spun Silk (ItemTemplate cq_ring_of_spun_silk)",
                                          "Herbalist's Hauberk of Ivy (ItemTemplate cq_herbalist_s_hauberk_of_ivy)"],
    "Hunt for Slith's tail": ["Hunt for Slith (DataQuest 20021)", "Slith's Tail (ItemTemplate cq_slith_s_tail)",
                              "Slith's Tail (the reward) (ItemTemplate sliths_tail)"],
    "the Shrouded Isles portals' 1.65 spots": ["Albion Shrouded Isles portal (ZonePoint 153)",
                                               "Midgard Shrouded Isles portal (ZonePoint 165)",
                                               "Hibernia Shrouded Isles portal (ZonePoint 167)"],
}


def groups(data):
    """{group: [entry label]} of the data file, in its order."""
    found = {}
    for entry in data["fixes"]:
        if "group" in entry:
            found.setdefault(entry["group"], []).append(f"{entry['name']} ({entry['table']} {entry['id']})")
    return found


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class RealFileTests(unittest.TestCase):
    """The real data file on a copy of a clean classic world."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.data = mf.load_data()
        cls.first = wf.apply(cls.db)
        cls.conn = sqlite3.connect(cls.db)

    @classmethod
    def tearDownClass(cls):
        cls.conn.close()
        cls.tmp.cleanup()

    def test_every_entry_is_corrected_and_none_is_kept(self):
        lines = [line for line in self.first if line.startswith("Mob fixes:")]
        self.assertEqual(len(lines), 1, self.first)
        self.assertTrue(lines[0].startswith(f"Mob fixes: {len(self.data['fixes'])} corrected: "), lines[0])
        self.assertNotIn("left as they are", lines[0])
        self.assertNotIn("group", lines[0])

    def test_the_groups_are_the_decisions_of_several_entries(self):
        self.assertEqual(groups(self.data), GROUPS)

    def test_every_row_holds_the_files_values(self):
        for entry in self.data["fixes"]:
            key = mf.KEYS[entry["table"]]
            with self.subTest(entry["name"], table=entry["table"]):
                columns = list(entry["set"])
                rows = self.conn.execute(f'SELECT {", ".join(columns)} FROM "{entry["table"]}" WHERE "{key}"=?',
                                         (entry["id"],)).fetchall()
                self.assertEqual(rows, [tuple(entry["set"][c] for c in columns)])

    def test_a_second_run_changes_nothing(self):
        self.assertEqual(wf.apply(self.db), [])

    def test_frund_and_agisthil_are_level_10(self):
        self.assertEqual(self.conn.execute("SELECT Level FROM NpcTemplate WHERE TemplateId IN (12070, 12165)").fetchall(),
                         [("10",), ("10",)])
        self.assertEqual(self.conn.execute("SELECT Level FROM Mob WHERE Mob_ID=?", (AGISTHIL,)).fetchone(), (10,))
        self.assertEqual(self.conn.execute("SELECT Level, NPCTemplateID FROM Mob WHERE Mob_ID=?", (FRUND,)).fetchone(),
                         (0, 12165))  # his level comes from his template

    def test_the_cloudy_green_stone_sells_for_50_copper(self):
        # "Zombie Problem" (level 4, Gronyr Gloyw): "he will give you a cloudy green stone (worth about 50 copper)".
        # A merchant pays the world's item_sell_ratio percent of the price. Gronyr's trade hands cq_cloudy_green_stone;
        # cq_alb_cloudy_green_stone is the one upstream's final reward handed too, which players may still carry.
        ratio = int(self.conn.execute("SELECT Value FROM ServerProperty WHERE `Key`='item_sell_ratio'").fetchone()[0])
        for stone in ("cq_cloudy_green_stone", "cq_alb_cloudy_green_stone"):
            with self.subTest(stone):
                price = self.conn.execute("SELECT Price FROM ItemTemplate WHERE Id_nb=?", (stone,)).fetchone()[0]
                self.assertEqual(price * ratio // 100, 50)

    def test_gronyrs_stone_is_the_periods_item(self):
        # Allakhazam's item page (archived 2003-02-12 to 2004-12-26): Albion, 0.00 lbs, level 4; the client's model 113
        # is "Green Gem" (upstream's 229 is the cloth dyes' bottle).
        self.assertEqual(self.conn.execute("SELECT Realm, Weight, Level, Model FROM ItemTemplate "
                                           "WHERE Id_nb='cq_cloudy_green_stone'").fetchone(), (1, 0, 4, 113))

    def test_gridash_takes_the_bronze_short_sword_gyda_sells(self):
        # #100: "Buy a bronze short sword from Gyda ... and give it to Gridash." DataQuest takes an item whose Id_nb
        # contains the step's turn-in id; upstream's was cq_bronze_short_sword, which nothing sells or hands out.
        step_items, collect = self.conn.execute(
            "SELECT StepItemTemplates, CollectItemTemplate FROM DataQuest WHERE ID=20098").fetchone()
        turn_in = collect.split("|")[0]
        self.assertEqual((step_items.split("|")[0], turn_in), ("bronze_short_sword", "bronze_short_sword"))
        sold = [i for (i,) in self.conn.execute(
            "SELECT m.ItemTemplateID FROM MerchantItem m JOIN Mob g ON g.ItemsListTemplateID = m.ItemListID "
            "WHERE g.Name = 'Gyda'")]
        self.assertTrue([i for i in sold if turn_in in i.lower()], sold)

    def test_frund_stands_at_the_red_dwarf_camp_beside_agisthil(self):
        frund = self.conn.execute("SELECT Region, X, Y, Z, RoamingRange FROM Mob WHERE Mob_ID=?", (FRUND,)).fetchone()
        agisthil = self.conn.execute("SELECT Region, X, Y, Z FROM Mob WHERE Mob_ID=?", (AGISTHIL,)).fetchone()
        self.assertEqual(frund[0], agisthil[0])
        self.assertEqual(frund[4], 200)  # his roaming range stays
        self.assertTrue(150 <= math.dist(frund[1:3], agisthil[1:3]) <= 300)
        others = self.conn.execute("SELECT Name, X, Y, Z FROM Mob WHERE Region=? AND Mob_ID<>? AND ABS(X-?)<2000 AND "
                                   "ABS(Y-?)<2000", (frund[0], FRUND, frund[1], frund[2])).fetchall()
        nearest = min(others, key=lambda m: math.dist(m[1:3], frund[1:3]))
        self.assertEqual(nearest[0], "Shaman Aslis")
        self.assertEqual(frund[3], nearest[3])  # on the camp's ground: the nearest camp mob's Z
        self.assertGreaterEqual(math.dist(nearest[1:3], frund[1:3]), 75)  # not on top of anyone


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class HalfGroupTests(unittest.TestCase):
    """A group of the real file on a clean world where one of its rows no longer holds upstream's values."""

    def test_a_group_half_matching_changes_none_of_its_rows(self):
        # Say the owner changed the Perfect Life Stone: the necklace must not lose Strange Bedfellows' second copy
        # (cq_hib_perfect_life_stone, the final reward) while the grant's copy stays a token, nor the other way round.
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "world.db")
            shutil.copyfile(TEST_WORLD, db)
            os.chmod(db, 0o644)
            with sqlite3.connect(db) as c:
                c.execute("UPDATE ItemTemplate SET Level=2 WHERE Id_nb='cq_perfect_life_stone'")
            line, = [line for line in wf.apply(db) if line.startswith("Mob fixes:")]
            with sqlite3.connect(db) as c:
                self.assertEqual(c.execute("SELECT FinalRewardItemTemplates FROM DataQuest WHERE ID=20975").fetchone(),
                                 ("cq_hib_perfect_life_stone",))
                self.assertEqual(c.execute("SELECT Level, Item_Type FROM ItemTemplate WHERE Id_nb='cq_perfect_life_stone'")
                                 .fetchone(), (2, 40))
        fixes = len(mf.load_data()["fixes"])
        self.assertTrue(line.startswith(f"Mob fixes: {fixes - 2} corrected: "), line)
        self.assertTrue(line.endswith("; 1 group left as it is (an entry's row holds neither upstream's values nor this "
                                      "file's, or is missing, so no row of the group changes): Strange Bedfellows' reward "
                                      "(Perfect Life Stone (ItemTemplate cq_perfect_life_stone))"), line)


if __name__ == "__main__":
    unittest.main()
