import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

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

    def tearDown(self):
        self.tmp.cleanup()

    def q(self, sql):
        with sqlite3.connect(self.db) as c:
            return c.execute(sql).fetchall()

    def test_apply_enables_disciple_and_adds_the_saracen_start(self):
        changes = wf.apply(self.db)
        self.assertEqual(len(changes), 2)
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
        self.assertEqual(wf.apply(self.db), [])
        self.assertEqual(self.q("SELECT XPos FROM StartupLocation WHERE ClassID=20 AND RaceID=4"), [(1,)])

    def test_cli(self):
        r = subprocess.run([sys.executable, os.path.join(BIN, "world_fixes.py"), "--db", self.db], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Disciple", r.stdout)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertEqual(len(wf.apply(self.db)), 2)
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'"), [("33;34;39;58-62",)])
        self.assertEqual(self.q("SELECT RaceID FROM StartupLocation WHERE ClassID=20 ORDER BY RaceID"), [(1,), (4,), (13,)])


if __name__ == "__main__":
    unittest.main()
