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


def row(db, key):
    """(Category, Description, DefaultValue, Value) of a property, or None."""
    with sqlite3.connect(db) as c:
        return c.execute("SELECT Category, Description, DefaultValue, Value FROM ServerProperty WHERE `Key`=?",
                         (key,)).fetchone()


def run_cli(db, *args):
    return subprocess.run([sys.executable, os.path.join(BIN, "server_properties.py"), "--db", db, *args],
                          capture_output=True, text=True)


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


class SiStartChoiceTests(unittest.TestCase):
    def test_on_and_off_in_any_case_become_true_and_false(self):
        for spec, expected in (("on", "True"), ("ON", "True"), ("On", "True"),
                               ("off", "False"), ("OFF", "False"), ("oFf", "False")):
            with self.subTest(spec=spec):
                self.assertEqual(sp.si_start_value(spec), expected)

    def test_other_values_are_refused(self):
        for bad in ("", "yes", "no", "true", "false", "1", "0", "none", "onn", " on", "off "):
            with self.subTest(bad=bad), self.assertRaisesRegex(
                    sp.PropertyError, f"^HEARTHDAOC_SI_START_CHOICE must be on or off, got {bad!r}$"):
                sp.si_start_value(bad)

    def test_the_property_matches_the_server_declaration(self):
        # [ServerProperty("server", "si_start_choice", <description>, false)]: off in code, "False" in the table.
        self.assertEqual((sp.SI_KEY, sp.SI_DEFAULT), ("si_start_choice", "False"))
        self.assertTrue(sp.SI_DESCRIPTION)
        self.assertNotIn('"', sp.SI_DESCRIPTION)
        self.assertNotIn("\\", sp.SI_DESCRIPTION)


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

    def test_a_new_row_gets_the_description_and_default_it_is_given(self):
        sp.set_property(self.db, sp.OVERRIDES_KEY, "/tc=2")
        self.assertEqual(row(self.db, sp.OVERRIDES_KEY), ("server", sp.OVERRIDES_DESCRIPTION, "", "/tc=2"))
        sp.set_property(self.db, sp.SI_KEY, "True", description=sp.SI_DESCRIPTION, default=sp.SI_DEFAULT)
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))

    def test_cli(self):
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        self.assertEqual(run_cli(self.db, "--gm-only-commands", "tele", "--si-start-choice", "on").returncode, 2)

    def test_cli_writes_the_si_start_choice_as_true_or_false(self):
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "ON")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "GM-only commands: none\nShrouded Isles start choice: on\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "Off")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Shrouded Isles start choice: off\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "False"))

    def test_cli_prints_nothing_when_nothing_changes(self):
        run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "off")
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "OFF")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "", ""))

    def test_cli_refuses_a_bad_si_start_choice_and_writes_nothing(self):
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "yes")
        self.assertEqual((r.returncode, r.stdout), (2, ""))
        self.assertEqual(r.stderr, "ERROR: HEARTHDAOC_SI_START_CHOICE must be on or off, got 'yes'\n")
        self.assertIsNone(value(self.db, sp.OVERRIDES_KEY))
        self.assertIsNone(value(self.db, sp.SI_KEY))

    def test_cli_needs_the_si_start_choice(self):
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc")
        self.assertEqual(r.returncode, 2)
        self.assertIn("the following arguments are required: --si-start-choice", r.stderr)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertIsNone(value(self.db, sp.OVERRIDES_KEY))
        self.assertIsNone(value(self.db, sp.SI_KEY))
        sp.set_property(self.db, sp.OVERRIDES_KEY, sp.gm_only_overrides("/tele;/tc"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on")
        self.assertEqual((r.returncode, r.stdout), (0, "Shrouded Isles start choice: on\n"), r.stderr)
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))


if __name__ == "__main__":
    unittest.main()
