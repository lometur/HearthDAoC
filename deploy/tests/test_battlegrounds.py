"""The classic battlegrounds: Abermenai 15-19, Thidranki 20-24, Murdaigean 25-29 and Caledonia 30-35.

ClassicBattlegroundSourceTests check the fork's small edits to upstream server code, so that an upstream
sync that brings the old code back fails CI: the frontier porter's battleground blocks call the fork,
KeepManager names the Midgard teleport "Svasud Faste", and Atlas's battleground daily quests stay deleted.
"""
import os
import re
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
SOURCE = os.path.join(ROOT, "source")
GAME_SERVER = os.path.join(SOURCE, "server", "GameServer")
OF_TELEPORTERS = os.path.join(GAME_SERVER, "scripts", "teleporters", "OFTeleporters.cs")
KEEP_MANAGER = os.path.join(GAME_SERVER, "keeps", "KeepManager.cs")
BATTLEGROUND_QUESTS = os.path.join(GAME_SERVER, "scripts", "quests", "BattlegroundQuests")

PORTER_CALL = "PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);"
# Atlas's daily quests for Caledonia 34-39 and Thidranki 20-24, whose scripts also made the Pazz NPCs.
QUEST_CLASS_NAMES = (
    "CaleKeepCaptureAlb", "CaleKeepCaptureHib", "CaleKeepCaptureMid",
    "CaleKillQuestAlb", "CaleKillQuestHib", "CaleKillQuestMid",
    "ThidKeepCaptureAlb", "ThidKeepCaptureHib", "ThidKeepCaptureMid",
    "ThidKillQuestAlb", "ThidKillQuestHib", "ThidKillQuestMid",
)


def cs_files(top):
    """Every .cs file under top, as paths relative to the repository root, sorted."""
    found = []
    for folder, _, names in os.walk(top):
        found.extend(os.path.relpath(os.path.join(folder, name), ROOT) for name in names if name.endswith(".cs"))
    return sorted(found)


class ClassicBattlegroundSourceTests(unittest.TestCase):
    def test_porter_blocks_call_the_fork(self):
        # The file starts with a BOM, and its line endings are mixed (CRLF and LF).
        with open(OF_TELEPORTERS, encoding="utf-8-sig") as f:
            text = f.read()
        lines = text.splitlines()
        starts = [i for i, line in enumerate(lines) if line.strip() == "case BattlegroundsID:"]
        self.assertEqual(len(starts), 3, "one battlegrounds block per realm")

        for start in starts:
            body = []
            for line in lines[start + 1:]:
                if line.strip().startswith("case "):
                    break
                if line.strip():
                    body.append(line.strip())
            self.assertEqual(body, [PORTER_CALL, "break;"], f"the block at line {start + 1}")

        # Atlas's caps: Thidranki under 7,125 realm points, Caledonia under 122,500.
        self.assertNotIn("7125", text)
        self.assertNotIn("122500", text)

    def test_keep_manager_names_svasud_faste(self):
        # ExitBattleground looks the realm's home portal keep up by TeleportID; the world's row is "Svasud Faste".
        with open(KEEP_MANAGER, encoding="utf-8") as f:
            text = f.read()
        midgard = [line.strip() for line in text.splitlines() if "case eRealm.Midgard: location =" in line]
        self.assertEqual(midgard, ['case eRealm.Midgard: location = "Svasud Faste"; break;'])
        self.assertNotIn("Svasudheim", text)

    def test_battleground_quests_are_gone(self):
        self.assertEqual(cs_files(BATTLEGROUND_QUESTS), [])

        # deploy/ is left out: the world fix names the classes as data, to delete their saved quests.
        names = re.compile(r"\b(?:" + "|".join(QUEST_CLASS_NAMES) + r")\b")
        naming = []
        for path in cs_files(SOURCE):
            with open(os.path.join(ROOT, path), encoding="utf-8", errors="replace") as f:
                if names.search(f.read()):
                    naming.append(path)
        self.assertEqual(naming, [])


if __name__ == "__main__":
    unittest.main()
