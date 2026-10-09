"""The epic chains (sub-project 4): the fork's quest data file, the upstream source edits, and the world fix
deploy/bin/epic_chains.py.

Spec: docs/fork/specs/2026-10-09-epic-chains-design.md. The world data tests run on a copy of a clean classic world
(HDC_TEST_WORLD); the others need no world.
"""
import json
import os
import pathlib
import re
import shutil
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
EXTRA_QUESTS = os.path.join(DEPLOY, "hearthdaoc-quests.json")
DOCKERFILE = os.path.join(DEPLOY, "Dockerfile")
LORD_ELIDYN = "1f005bc1-27ae-40b0-bc42-1ec407a4aa34"
LEVEL_50_IDS = [990509, 990511, 990513, 990512, 990519]


def read(path):
    """A source file's text (upstream files may start with a BOM)."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


class ExtraQuestFileTests(unittest.TestCase):
    """deploy/hearthdaoc-quests.json: ClassicQuests reads it beside upstream's classic-quests.json (spec 3.2)."""

    def setUp(self):
        with open(EXTRA_QUESTS, encoding="utf-8") as f:
            self.extra = json.load(f)

    def test_it_marks_the_level_50_stages_and_lord_elidyn(self):
        self.assertEqual(sorted(int(k) for k in self.extra["Quests"]), sorted(LEVEL_50_IDS))
        for quest in self.extra["Quests"].values():
            steps = quest["Steps"]
            self.assertEqual(len(steps), 3)
            self.assertIsNone(steps[0])
            self.assertEqual(steps[1]["Marker"], {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032})
            self.assertEqual(steps[2]["Marker"], {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088})
        self.assertEqual(self.extra["QuestMonsterIds"], [LORD_ELIDYN])

    def test_the_image_puts_it_next_to_the_server(self):
        self.assertIn("COPY deploy/hearthdaoc-quests.json /app/server/hearthdaoc-quests.json\n", read(DOCKERFILE))


# Each level-50 quest looks its NPC up at a spot and creates it at another when none is found there, so a copy a GM
# saved shows up as a second NPC at every start (spec 3.3). (file, NPC name, variable the quest creates it in)
LOOKUPS = (
    ("scripts/quests/Albion/epic/Academy50.cs", "Master Ferowl", "Ferowl"),
    ("scripts/quests/Hibernia/epic/Essence50.cs", "Brigit", "Brigit"),
    ("scripts/quests/Midgard/epic/Mystic50.cs", "Danica", "Danica"),
    ("scripts/quests/Midgard/epic/Viking50.cs", "Elizabeth", "Elizabeth"),
)


def lookup_and_creation(text, name, var):
    """((lookup X, Y), (creation X, Y)) of the NPC's block in a level-50 quest."""
    block = text[text.index(f'GetNPCsByName("{name}"'):]
    found = re.search(r"npc\.X == (\d+) && npc\.Y == (\d+)", block)
    x = re.search(rf"\b{var}\.X = (\d+);", block)
    y = re.search(rf"\b{var}\.Y = (\d+);", block)
    return (found.group(1), found.group(2)), (x.group(1), y.group(1))


class EpicSourceTests(unittest.TestCase):
    def test_the_defenders_copy_of_shadows_50_is_gone(self):
        self.assertFalse(os.path.exists(os.path.join(GAME_SERVER, "scripts", "quests", "Albion", "epic", "Shadows50.cs")))
        for path in pathlib.Path(GAME_SERVER).rglob("*.cs"):
            self.assertNotIn("class Shadows_50", read(str(path)), str(path))

    def test_each_level_50_quest_finds_its_npc_where_it_creates_it(self):
        for rel, name, var in LOOKUPS:
            with self.subTest(rel):
                found, made = lookup_and_creation(read(os.path.join(GAME_SERVER, rel)), name, var)
                self.assertEqual(found, made)

    def test_the_four_files_keep_their_crlf_endings(self):
        for rel, _name, _var in LOOKUPS:
            with open(os.path.join(GAME_SERVER, rel), "rb") as f:
                raw = f.read()
            self.assertEqual(raw.count(b"\r\n"), raw.count(b"\n"), rel)
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), rel)


if __name__ == "__main__":
    unittest.main()
