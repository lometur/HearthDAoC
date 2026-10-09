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


if __name__ == "__main__":
    unittest.main()
