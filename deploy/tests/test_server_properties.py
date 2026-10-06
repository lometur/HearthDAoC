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

import server_properties as sp  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCHEMA = ("CREATE TABLE ServerProperty (Category TEXT NOT NULL DEFAULT '', `Key` VARCHAR(255) NOT NULL DEFAULT '', "
          "Description TEXT NOT NULL DEFAULT '', DefaultValue TEXT NOT NULL DEFAULT '', Value TEXT NOT NULL DEFAULT '', "
          "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', ServerProperty_ID VARCHAR(255), "
          "PRIMARY KEY (`Key`))")


def value(db, key):
    with sqlite3.connect(db) as c:
        row = c.execute("SELECT Value FROM ServerProperty WHERE `Key`=?", (key,)).fetchone()
    return row[0] if row else None


class GmOnlyTests(unittest.TestCase):
    def test_commands_become_gm_level_overrides(self):
        self.assertEqual(sp.gm_only_overrides("/tele;/tc"), "/tele=2;/tc=2")
        self.assertEqual(sp.gm_only_overrides(" /tele ; /tc ;"), "/tele=2;/tc=2")

    def test_empty_means_no_overrides(self):
        self.assertEqual(sp.gm_only_overrides(""), "")
        self.assertEqual(sp.gm_only_overrides("none"), "")

    def test_bad_entries_are_refused(self):
        for bad in ("tele", "/tele=1", "/te le", "/tele;rm -rf"):
            with self.subTest(bad=bad), self.assertRaisesRegex(sp.PropertyError, "HEARTHDAOC_GM_ONLY_COMMANDS"):
                sp.gm_only_overrides(bad)


class UpsertTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            c.execute(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def test_new_property_is_created_then_updated(self):
        self.assertTrue(sp.set_property(self.db, sp.OVERRIDES_KEY, "/tele=2"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2")
        self.assertTrue(sp.set_property(self.db, sp.OVERRIDES_KEY, "/tele=2;/tc=2"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")

    def test_same_value_changes_nothing(self):
        sp.set_property(self.db, sp.OVERRIDES_KEY, "/tc=2")
        self.assertFalse(sp.set_property(self.db, sp.OVERRIDES_KEY, "/tc=2"))

    def test_cli(self):
        run = lambda *a: subprocess.run([sys.executable, os.path.join(BIN, "server_properties.py"), "--db", self.db, *a],  # noqa: E731
                                        capture_output=True, text=True)
        r = run("--gm-only-commands", "/tele;/tc")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        self.assertEqual(run("--gm-only-commands", "tele").returncode, 2)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertIsNone(value(self.db, sp.OVERRIDES_KEY))
        sp.set_property(self.db, sp.OVERRIDES_KEY, sp.gm_only_overrides("/tele;/tc"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")


if __name__ == "__main__":
    unittest.main()
