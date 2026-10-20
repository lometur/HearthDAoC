"""The relic pad: upstream's rule as it ships (owner decision 3 of the 0.37b sync, issue #138, 2026-10-18).

Since upstream 0.36, 8 enemies must be near an enemy relic pad before one of them can take its relic
(relic_players_required_on_pad; 16 before), and a player's companions and the gamebots count. The fork sets
nothing here: the server's default and the world's ServerProperty row both hold 8. Upstream's own
UT_RelicPadAttackers, which CI runs in the fork's filter, checks the default and who counts. These tests check that
the world holds 8, that a start (server_properties.py, world_fixes.py) leaves it so, and that hdc upgrade-world gives
an old world that held 16 (as 0.35b shipped it) the new world's 8: ServerProperty is no progress table, so the
importer never carries it.

With HDC_TEST_WORLD (a clean classic world, 0.37b), on copies of it; the upgrade also needs HDC_TOOLS (the built CLIs).
"""
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

import init_world  # noqa: E402
import world_admin  # noqa: E402
import world_fixes  # noqa: E402
from tests.test_world_admin import QUIET, FakeRelease, importer_cmd  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
TOOLS = os.environ.get("HDC_TOOLS")
KEY = "relic_players_required_on_pad"
UPSTREAM = ("pvp", "8", "8")  # Category, DefaultValue, Value: [ServerProperty("pvp", KEY, ..., 8)] since 0.36


def pad(db):
    """(Category, DefaultValue, Value) of the relic pad's row."""
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        return conn.execute("SELECT Category, DefaultValue, Value FROM ServerProperty WHERE `Key`=?", (KEY,)).fetchone()
    finally:
        conn.close()


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class RelicPadWorldTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = os.path.join(self.tmp.name, "data")

    def tearDown(self):
        self.tmp.cleanup()

    def test_the_world_holds_upstreams_eight(self):
        self.assertEqual(pad(TEST_WORLD), UPSTREAM)

    def test_a_start_leaves_it_at_eight(self):
        # The entrypoint's world steps with its defaults: server_properties.py, then world_fixes.py.
        db = os.path.join(self.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, db)
        os.chmod(db, 0o644)
        r = subprocess.run([sys.executable, os.path.join(BIN, "server_properties.py"), "--db", db,
                            "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on",
                            "--bot-cpu", "off"],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        world_fixes.apply(db)
        self.assertEqual(pad(db), UPSTREAM)

    @unittest.skipUnless(TOOLS, "needs HDC_TOOLS (the built CLIs)")
    def test_an_upgraded_world_holds_the_new_worlds_eight(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        os.chmod(db, 0o644)
        conn = sqlite3.connect(db)
        try:
            with conn:  # as the 0.35b world ships it, and the owner's world holds it
                conn.execute("UPDATE ServerProperty SET DefaultValue='16', Value='16' WHERE `Key`=?", (KEY,))
        finally:
            conn.close()
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": "0.35b", "edition": "classic", "navmesh": False, "created_utc": "x"})
        archive = world_admin.upgrade_world(FakeRelease("0.37b", TEST_WORLD), self.data, importer_cmd(self.data),
                                            log=QUIET)
        self.assertEqual(pad(db), UPSTREAM)
        with open(os.path.join(archive, "upgrade-report.txt"), encoding="utf-8") as f:
            self.assertIn(f"  {KEY}: yours='16' new='8'\n", f.read())


if __name__ == "__main__":
    unittest.main()
