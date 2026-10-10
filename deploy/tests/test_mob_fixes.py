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
]
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


class SyntheticTests(unittest.TestCase):
    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        for stmt in SCHEMA:
            self.conn.execute(stmt)
        add_rows(self.conn)
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
                                              "the monsters keep the values they have"])

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


if __name__ == "__main__":
    unittest.main()
