import json
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

import spawns  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")


def mob_ids(db):
    with sqlite3.connect(db) as c:
        return {r[0] for r in c.execute("SELECT Mob_ID FROM Mob")}


class SpawnsErrorTests(unittest.TestCase):
    def test_world_without_archive_is_refused(self):
        with tempfile.TemporaryDirectory() as data:
            os.makedirs(os.path.join(data, "world"))
            with sqlite3.connect(os.path.join(data, "world", "opendaoc.sqlite3.db")) as c:
                c.execute("CREATE TABLE Mob (Mob_ID TEXT PRIMARY KEY, Name TEXT)")
            with self.assertRaisesRegex(spawns.SpawnsError, "no archived spawns"):
                spawns.restore(data, 20)
            self.assertFalse(os.path.exists(os.path.join(data, "spawns.json")))

    def test_max_level_is_validated(self):
        with tempfile.TemporaryDirectory() as data:
            for bad in (0, 51):
                with self.subTest(level=bad), self.assertRaisesRegex(spawns.SpawnsError, "max level"):
                    spawns.restore(data, bad)


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class SpawnsWorldTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = self.tmp.name
        self.db = os.path.join(self.data, "world", "opendaoc.sqlite3.db")
        os.makedirs(os.path.dirname(self.db))
        shutil.copyfile(TEST_WORLD, self.db)
        self.original = mob_ids(self.db)

    def tearDown(self):
        self.tmp.cleanup()

    def test_restore_adds_only_archived_home_region_leveling_monsters(self):
        n = spawns.restore(self.data, 20)
        added = mob_ids(self.db) - self.original
        self.assertEqual(len(added), n)
        self.assertGreater(n, 9000)
        with sqlite3.connect(self.db) as c:
            c.execute("CREATE TEMP TABLE added (Mob_ID TEXT PRIMARY KEY)")
            c.executemany("INSERT INTO added VALUES (?)", [(i,) for i in added])
            outside = c.execute("""
                SELECT count(*) FROM Mob m JOIN added a ON a.Mob_ID = m.Mob_ID
                LEFT JOIN NpcTemplate t ON t.TemplateId = m.NPCTemplateID
                WHERE m.Region NOT IN (1, 100, 200) OR m.Realm <> 0 OR m.Name <> lower(m.Name)
                   OR CASE WHEN t.ReplaceMobValues = 1 AND CAST(t.Level AS INTEGER) > 0
                           THEN CAST(t.Level AS INTEGER) ELSE m.Level END NOT BETWEEN 1 AND 20
                   OR m.Mob_ID NOT IN (SELECT Mob_ID FROM offline_classic165_removed_mobs)""").fetchone()[0]
        self.assertEqual(outside, 0)
        with open(os.path.join(self.data, "spawns.json"), encoding="utf-8") as f:
            self.assertEqual(json.load(f)["max_level"], 20)

    def test_restore_twice_changes_nothing(self):
        spawns.restore(self.data, 20)
        first = mob_ids(self.db)
        spawns.restore(self.data, 20)
        self.assertEqual(mob_ids(self.db), first)

    def test_lower_max_level_resets_to_exactly_that_range(self):
        spawns.restore(self.data, 10)
        only_ten = mob_ids(self.db)
        spawns.undo(self.data)
        spawns.restore(self.data, 20)
        spawns.restore(self.data, 10)
        self.assertEqual(mob_ids(self.db), only_ten)

    def test_undo_returns_the_original_mob_table(self):
        n = spawns.restore(self.data, 20)
        self.assertEqual(spawns.undo(self.data), n)
        self.assertEqual(mob_ids(self.db), self.original)
        self.assertFalse(os.path.exists(os.path.join(self.data, "spawns.json")))

    def test_status(self):
        self.assertEqual(spawns.status(self.data)["enabled"], False)
        n = spawns.restore(self.data, 20)
        st = spawns.status(self.data)
        self.assertEqual((st["enabled"], st["max_level"], st["restored"]), (True, 20, n))

    def test_reapply_restores_the_saved_setting_on_a_fresh_world(self):
        n = spawns.restore(self.data, 20)
        shutil.copyfile(TEST_WORLD, self.db)  # e.g. after new-world or upgrade-world
        self.assertEqual(spawns.reapply(self.data, log=lambda *a: None), n)
        self.assertEqual(len(mob_ids(self.db) - self.original), n)

    def test_reapply_does_nothing_when_not_enabled(self):
        self.assertIsNone(spawns.reapply(self.data, log=lambda *a: None))
        self.assertEqual(mob_ids(self.db), self.original)

    def test_cli(self):
        run = lambda *a: subprocess.run([sys.executable, os.path.join(BIN, "spawns.py"), "--data", self.data, *a],  # noqa: E731
                                        capture_output=True, text=True)
        r = run("restore", "--max-level", "20")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Restored", r.stdout)
        self.assertIn("max_level", run("status").stdout)
        self.assertEqual(run("undo").returncode, 0)


if __name__ == "__main__":
    unittest.main()
