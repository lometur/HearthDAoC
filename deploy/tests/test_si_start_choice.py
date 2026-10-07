"""HEARTHDAOC_SI_START_CHOICE from .env to the server, and the world rows the server's start choice reads.

The setting goes .env -> compose.yml -> entrypoint.sh -> server_properties.py -> the si_start_choice
server property, default on at every step. hdc update appends every KEY= line of .env.example that an
existing .env lacks, so servers set up before the setting existed get it on too.
"""
import os
import pathlib
import re
import shlex
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

import server_properties as sp  # noqa: E402
from tests.test_server_properties import SCHEMA, row  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCRIPT_CS = os.path.join(ROOT, "source", "server", "GameServer", "scripts", "hearthdaoc", "SiStartChoiceScript.cs")


def read(name):
    with open(os.path.join(DEPLOY, name), encoding="utf-8") as f:
        return f.read()


class EntrypointTests(unittest.TestCase):
    """Runs the entrypoint's own server_properties.py command against a scratch world."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        os.makedirs(os.path.join(self.tmp.name, "world"))
        self.db = os.path.join(self.tmp.name, "world", "opendaoc.sqlite3.db")
        with sqlite3.connect(self.db) as c:
            c.execute(SCHEMA)
        lines = read("entrypoint.sh").replace("\\\n", " ").splitlines()
        commands = [line for line in lines if line.startswith('python3 "$BIN/server_properties.py"')]
        self.assertEqual(len(commands), 1, "the entrypoint runs server_properties.py once")
        self.command = commands[0]

    def tearDown(self):
        self.tmp.cleanup()

    def run_entrypoint_line(self, **env):
        script = f"set -euo pipefail\nBIN={shlex.quote(BIN)}\nDATA={shlex.quote(self.tmp.name)}\n{self.command}\n"
        base = {k: v for k, v in os.environ.items() if not k.startswith("HEARTHDAOC_")}
        return subprocess.run(["bash", "-c", script], env={**base, **env}, capture_output=True, text=True)

    def test_passes_the_setting_with_default_on(self):
        self.assertIn('--si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}"', self.command)
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))

    def test_off_in_any_case_turns_it_off(self):
        self.run_entrypoint_line()
        r = self.run_entrypoint_line(HEARTHDAOC_SI_START_CHOICE="OFF")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Shrouded Isles start choice: off\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY)[3], "False")

    def test_a_bad_value_stops_the_start(self):
        for bad in ("maybe", ""):
            with self.subTest(bad=bad):
                r = self.run_entrypoint_line(HEARTHDAOC_SI_START_CHOICE=bad)
                self.assertEqual(r.returncode, 2)
                self.assertEqual(r.stderr, f"ERROR: HEARTHDAOC_SI_START_CHOICE must be on or off, got {bad!r}\n")
                self.assertIsNone(row(self.db, sp.SI_KEY))


class SettingDocsTests(unittest.TestCase):
    def test_compose_passes_the_setting_with_default_on(self):
        environment = read("compose.yml").split("\n    environment:\n", 1)[1].split("\n    volumes:\n", 1)[0]
        self.assertIn("\n      HEARTHDAOC_SI_START_CHOICE: ${HEARTHDAOC_SI_START_CHOICE-on}\n", environment + "\n")

    def test_env_example_documents_the_setting_default_on(self):
        lines = read(".env.example").splitlines()
        self.assertEqual([line for line in lines if line.startswith("HEARTHDAOC_SI_START_CHOICE")],
                         ["HEARTHDAOC_SI_START_CHOICE=on"])
        i = lines.index("HEARTHDAOC_SI_START_CHOICE=on")
        comment = []
        while i > 0 and lines[i - 1].startswith("#"):
            i -= 1
            comment.insert(0, lines[i])
        self.assertIn("Shrouded Isles", " ".join(comment))
        self.assertIn("off", " ".join(comment))

    def test_handoff_lists_the_setting(self):
        handoff = read("HANDOFF.md")
        self.assertIn("`HEARTHDAOC_SI_START_CHOICE` (default `on`", handoff)
        self.assertIn("`HEARTHDAOC_SI_START_CHOICE=on`", handoff)  # what ./hdc update adds to an existing .env


class ServerDeclarationTests(unittest.TestCase):
    @unittest.skipUnless(os.path.exists(SCRIPT_CS), "needs source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs")
    def test_server_declares_the_property_with_the_same_description(self):
        with open(SCRIPT_CS, encoding="utf-8") as f:
            found = re.findall(r'\[ServerProperty\(\s*"([^"]*)",\s*"si_start_choice",\s*"([^"]*)",\s*(\w+)\s*\)\]', f.read())
        self.assertEqual(found, [("server", sp.SI_DESCRIPTION, "false")])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class ArrivalRowTests(unittest.TestCase):
    """The server looks these up with WorldMgr.GetTeleportLocation(realm, ":" + TeleportID): Type is
    empty and the key is case-sensitive. The first row per key wins, so there must be exactly one."""

    EXPECTED = [  # TeleportID, Realm, RegionID, X, Y, Z, Heading, Type
        ("Caer Gothwaite", 1, 51, 535518, 547214, 4800, 2105, ""),
        ("Aegirhamn", 2, 151, 293910, 356255, 3488, 1199, ""),
        ("Grove of Domnann", 3, 181, 423187, 440300, 5952, 3866, ""),
    ]

    def test_each_realm_has_exactly_one_arrival_row(self):
        conn = sqlite3.connect(pathlib.Path(TEST_WORLD).resolve().as_uri() + "?mode=ro", uri=True)
        try:
            for expected in self.EXPECTED:
                # TeleportID is COLLATE NOCASE, so this also finds rows that differ only in case, in any realm.
                found = conn.execute("SELECT TeleportID, Realm, RegionID, X, Y, Z, Heading, Type FROM Teleport "
                                     "WHERE TeleportID = ?", (expected[0],)).fetchall()
                with self.subTest(teleport=expected[0]):
                    self.assertEqual(found, [expected])
        finally:
            conn.close()


if __name__ == "__main__":
    unittest.main()
