"""The classic battlegrounds (sub-project 5): the fork's upstream source edits and the world data fix.

An upstream sync that brings back the old porter blocks, "Svasudheim Faste" or Atlas's battleground
quest files fails the source checks. The world data tests run deploy/bin/battlegrounds.py on a scratch
world (make_world) and, with HDC_TEST_WORLD, on a copy of a clean classic world.
"""
import math
import os
import pathlib
import re
import shutil
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

import battlegrounds  # noqa: E402
from tests.test_world_fixes import SCHEMA as WORLD_FIXES_SCHEMA  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SOURCE = os.path.join(ROOT, "source")
GAME_SERVER = os.path.join(SOURCE, "server", "GameServer")
OF_TELEPORTERS = os.path.join(GAME_SERVER, "scripts", "teleporters", "OFTeleporters.cs")
KEEP_MANAGER = os.path.join(GAME_SERVER, "keeps", "KeepManager.cs")
BATTLEGROUND_QUESTS = os.path.join(GAME_SERVER, "scripts", "quests", "BattlegroundQuests")
BATTLEGROUND_OPTIONS = os.path.join(GAME_SERVER, "scripts", "teleporters", "BattlegroundTeleportOptions.cs")
BOTS = os.path.join(GAME_SERVER, "bots", "autonomous")
OBJECTIVE_ASSIGNMENTS = os.path.join(BOTS, "AutonomousObjectiveAssignments.cs")
BOT_CONTROLLER = os.path.join(BOTS, "AutonomousWorldBotController.cs")
BOT_BATTLEGROUND = os.path.join(BOTS, "AutonomousWorldBotController.Battleground.cs")
GOAL_POLICY = os.path.join(BOTS, "AutonomousBotGoalPolicy.cs")
FORK_CODE = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")

PORTER_CALL = "PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);"
# Atlas's daily quests for Caledonia 34-39 and Thidranki 20-24, whose scripts also made the Pazz NPCs.
QUEST_CLASS_NAMES = (
    "CaleKeepCaptureAlb", "CaleKeepCaptureHib", "CaleKeepCaptureMid",
    "CaleKillQuestAlb", "CaleKillQuestHib", "CaleKillQuestMid",
    "ThidKeepCaptureAlb", "ThidKeepCaptureHib", "ThidKeepCaptureMid",
    "ThidKillQuestAlb", "ThidKillQuestHib", "ThidKillQuestMid",
)


def read(path):
    """A C# source file's text (upstream files may start with a BOM; a few are not UTF-8)."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


def between(text, start, end):
    """The text from the first start up to the first end after it."""
    body = text[text.index(start):]
    return body[:body.index(end)]


def cs_files(top):
    """Every .cs file under top, as paths relative to the repository root, sorted."""
    found = []
    for folder, _, names in os.walk(top):
        found.extend(os.path.relpath(os.path.join(folder, name), ROOT) for name in names if name.endswith(".cs"))
    return sorted(found)


class ClassicBattlegroundSourceTests(unittest.TestCase):
    def test_porter_blocks_call_the_fork(self):
        # The file starts with a BOM; upstream 0.35 made its line endings all CRLF.
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
        # Upstream fixed it the same way in 0.35, with a comment after the code that names the old "Svasudheim".
        text = read(KEEP_MANAGER)
        midgard = [line.split("//")[0].strip() for line in text.splitlines() if "case eRealm.Midgard: location =" in line]
        self.assertEqual(midgard, ['case eRealm.Midgard: location = "Svasud Faste"; break;'])
        self.assertNotIn('location = "Svasudheim', text)

    # Every way into a battleground keeps the classic realm rank caps (owner, #50). Upstream 0.35 checks level
    # only, so a sync that drops one of these hooks fails here.

    def test_battleground_portal_keep_keeps_the_cap(self):
        # GetBGPK, GameTeleporter's "battlegrounds" whisper: upstream 0.35 dropped the cap; the fork keeps 0.34's.
        body = between(read(KEEP_MANAGER), "AbstractGameKeep GetBGPK(GamePlayer player)", "return null;")
        self.assertIn("// HearthDAoC:", body)
        self.assertIn("(bg.MaxRealmLevel == 0 || player.RealmLevel < bg.MaxRealmLevel)", body)

    def test_realm_teleporters_check_the_cap(self):
        # Upstream 0.35's [Battlegrounds] choice on every realm teleporter checks level only; the fork adds the
        # porter's cap refusal after the level check, for players who are not game masters.
        text = read(BATTLEGROUND_OPTIONS)
        call = "string capRefusal = gameMaster ? null : HearthDAoC.ClassicBattlegroundsScript.RealmRankRefusal(player, bracket.RegionId);"
        self.assertEqual(text.count(call), 1)
        self.assertEqual(text.count("BattlegroundBrackets.Entry("), 1)
        self.assertLess(text.index("BattlegroundBrackets.Allows(bracket, player.Level)"), text.index(call))
        self.assertLess(text.index(call), text.index("BattlegroundBrackets.Entry("))

    def test_bots_get_no_battleground_goal_over_the_cap(self):
        text = read(OBJECTIVE_ASSIGNMENTS)
        # The allocator counts and draws only bots under the cap.
        can_take = between(text, "public static bool CanTakeBattleground(GameBot bot)", ";\n")
        self.assertIn("HearthDAoC.ClassicBattlegroundsScript.BotFitsItsBattleground(bot)", can_take)
        # Every assignment goes through Assign, which swaps a battleground goal over the cap for another goal.
        assign = between(text, "private static void Assign(GameBot bot,", "record.ObjectiveKind = kind.ToString();")
        swap = ("if (kind == eAutonomousObjectiveKind.Battleground && "
                "!HearthDAoC.ClassicBattlegroundsScript.BotFitsItsBattleground(bot))")
        self.assertIn(swap, assign)
        self.assertIn("kind = AutonomousBotGoalPolicy.Choose(bot.Level, excludeBattlegrounds: true);", assign)
        self.assertLess(assign.index("EnsureAllowed("), assign.index(swap))

    def test_bots_over_the_cap_are_never_sent_in(self):
        # A battleground tour ends before the bot travels; one already inside stays (BotOverCap says so).
        tour = between(read(BOT_BATTLEGROUND), "private bool ExecuteBattleground(", "private bool RoamBattleground(")
        self.assertIn("HearthDAoC.ClassicBattlegroundsScript.BotOverCap(bot, bracket.RegionId) is string overCap", tour)
        self.assertIn("AutonomousObjectiveAssignments.EndBattlegroundTour(bot, overCap);", tour)
        self.assertLess(tour.index("BotOverCap("), tour.index("TravelToBattleground("))

        controller = read(BOT_CONTROLLER)
        # A monster camp in a battleground is not offered over the cap (the bot, or any member of a shared party) ...
        offer = between(controller, "campBattleground = AutonomousTownTeleporters.IsEnabled",
                        "reachableRegions.Add(campBattleground.RegionId);")
        self.assertIn("HearthDAoC.ClassicBattlegroundsScript.PartyFits(bot, sharedGroup, campBattleground.RegionId)", offer)
        # ... and a camp chosen before the bot (or a member of its shared party) reached the cap is given up
        # instead of travelled to. The check runs on every travel turn, so also on the turn of the port.
        travel = between(controller, "private bool TravelAcrossRegions(GameBot bot)", "FindNextCrossing(")
        self.assertIn("HearthDAoC.ClassicBattlegroundsScript.PartyOverCap(bot, _groupDirective?.IsDynamic == true,\n"
                      "                        bracket.RegionId) is string overCap", travel)
        self.assertLess(travel.index("PartyOverCap("), travel.index("TravelToBattleground("))

    def test_saved_records_get_no_battleground_goal_over_the_cap(self):
        # ReconcileSavedAssignment re-rolls a disabled saved goal before the bot enters the world, without Assign.
        text = read(GOAL_POLICY)
        reconcile = between(text, "public static bool ReconcileSavedAssignment(", "record.ObjectiveAssignmentId = string.Empty;")
        self.assertIn("// HearthDAoC:", reconcile)
        self.assertIn("record.ObjectiveKind = Choose(record.Level,\n                excludeBattlegrounds: "
                      "!HearthDAoC.ClassicBattlegroundsScript.RecordFitsItsBattleground(record)).ToString();", reconcile)

    def test_every_bot_trip_into_a_battleground_is_capped(self):
        # TravelToBattleground is the bots' only way in (a realm teleporter's [Battlegrounds] choice). If a sync
        # adds a caller, check that it keeps the cap and add it here.
        callers = {}
        for path in cs_files(GAME_SERVER):
            count = read(os.path.join(ROOT, path)).count("TravelToBattleground(")
            if count:
                callers[os.path.basename(path)] = count
        self.assertEqual(callers, {
            # The definition and the tour's call.
            "AutonomousWorldBotController.Battleground.cs": 2,
            # The battleground monster camp's call.
            "AutonomousWorldBotController.cs": 1,
        })

    def test_upstream_files_that_call_the_fork(self):
        # The fork's battleground hooks in upstream files, and how many calls each has.
        calls = {}
        fork = os.path.relpath(FORK_CODE, ROOT)
        for path in cs_files(GAME_SERVER):
            if path.startswith(fork + os.sep):
                continue
            count = len(re.findall(r"HearthDAoC\.ClassicBattlegroundsScript\.", read(os.path.join(ROOT, path))))
            if count:
                calls[os.path.basename(path)] = count
        self.assertEqual(calls, {
            "OFTeleporters.cs": 3,
            "BattlegroundTeleportOptions.cs": 1,
            "AutonomousBotGoalPolicy.cs": 1,
            "AutonomousObjectiveAssignments.cs": 2,
            "AutonomousWorldBotController.Battleground.cs": 1,
            "AutonomousWorldBotController.cs": 2,
        })

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


NOW = "2026-10-07 12:00:00"
LATER = "2026-10-08 12:00:00"
INJECTED = "CREATE TRIGGER injected {} BEGIN SELECT RAISE(ABORT, 'injected failure'); END"

# The scratch world: the tables battlegrounds.py needs, trimmed to the columns it uses (Keep in full),
# with rows copied from the clean classic world (clean-classic-0.35.db) unless a comment says otherwise.
WORLD_SCHEMA = [
    "CREATE TABLE Battleground (RegionID INT NOT NULL DEFAULT 0, MinLevel INT NOT NULL DEFAULT 0, "
    "MaxLevel INT NOT NULL DEFAULT 0, MaxRealmLevel INT NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Battleground_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Battleground_ID))",
    "CREATE TABLE Zones (ZoneID INT NOT NULL DEFAULT 0, RegionID INT NOT NULL DEFAULT 0, "
    "Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, Experience INT NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (ZoneID))",
    "CREATE TABLE Regions (RegionID INT NOT NULL DEFAULT 0, Description TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (RegionID))",
    "CREATE TABLE Keep (KeepID INT NOT NULL DEFAULT 0, Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "Region INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, "
    "Heading INT NOT NULL DEFAULT 0, Realm INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, "
    "ClaimedGuildName TEXT DEFAULT NULL COLLATE NOCASE, AlbionDifficultyLevel INT NOT NULL DEFAULT 0, "
    "MidgardDifficultyLevel INT NOT NULL DEFAULT 0, HiberniaDifficultyLevel INT NOT NULL DEFAULT 0, "
    "OriginalRealm INT NOT NULL DEFAULT 0, KeepType INT NOT NULL DEFAULT 0, BaseLevel INT NOT NULL DEFAULT 0, "
    "SkinType INT NOT NULL DEFAULT 0, CreateInfo VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Keep_ID VARCHAR(255) DEFAULT NULL COLLATE NOCASE, PRIMARY KEY (KeepID))",
    "CREATE TABLE Mob (ClassType TEXT DEFAULT NULL COLLATE NOCASE, Name VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "X INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, Heading INT NOT NULL DEFAULT 0, "
    "Region INT NOT NULL DEFAULT 0, Model INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, "
    "Realm INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Mob_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Mob_ID))",
    "CREATE TABLE Door (Z INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, "
    "Heading INT NOT NULL DEFAULT 0, InternalID INT NOT NULL DEFAULT 0, Health INT NOT NULL DEFAULT 0, "
    "State INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Door_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Door_ID))",
    "CREATE TABLE Quest (Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, Step INT NOT NULL DEFAULT 0, "
    "Character_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Quest_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Quest_ID))",
]
SEED = {
    "Battleground (RegionID, MinLevel, MaxLevel, MaxRealmLevel, Battleground_ID)": [
        (165, 45, 49, 45, "Cathal Valley (Level 45-49)"),
        (250, 30, 35, 25, "Caledonia (Level 34-39 - RR3L5)"),
        (251, 25, 29, 5, "Murdaigean (Level 25-29)"),
        (252, 20, 24, 10, "Thidranki (Level 20-24 - RR2L0)"),
        (253, 15, 19, 2, "Abermenai (Level 15-19)"),
    ],
    "Zones (ZoneID, RegionID, Name, Experience, LastTimeRowUpdated)": [
        (250, 250, "Caledon", 50, "2017-01-08 18:51:19"),
        (251, 251, "Murdaigean", 0, "2017-01-08 18:51:19"),
        (252, 252, "Thidranki", 50, "2017-01-08 18:51:19"),
        (253, 253, "Abermenai", 0, "2017-01-08 18:51:19"),
    ],
    "Regions (RegionID, Description, LastTimeRowUpdated)": [
        (250, "Caledon", "2017-01-08 18:50:36"),
        (251, "Murdaigean", "2017-01-08 18:50:36"),
        (252, "Thidranki", "2017-01-08 18:50:36"),
        (253, "Abermenai", "2017-01-08 18:50:36"),
    ],
    "Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
    "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, CreateInfo, "
    "LastTimeRowUpdated, Keep_ID)": [
        # The four central keeps: Atlas's two and upstream 0.35's two.
        (11, "Thidranki Faste", 252, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 26, 0, "Atlas BG",
         "2022-10-09 21:09:42", "11"),
        (31, "Caer Caledon", 250, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 46, 0, "Atlas BG",
         "2022-08-18 17:36:33", "31"),
        (32, "Dun Murdaigean", 251, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 31, 0,
         "Classic BG (Claude 2026-10-07)", "2022-10-09 21:09:42", "32"),
        (33, "Dun Abermenai", 253, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 21, 0,
         "Classic BG (Claude 2026-10-07)", "2022-10-09 21:09:42", "33"),
        # Thidranki's portal keeps (step 4), and the Hibernia portal keeps of 253 and 251: BaseLevel 255,
        # so they are portal keeps.
        (12, "Hibernia Portal Keep", 252, 18362, 18257, 4320, 3517, 3, 1, "", 1, 1, 1, 3, 0, 255, 0,
         "Kelt;/keep create 12 255 0 Hibernia Portal Keep", "2021-12-03 21:32:39", "5e26c703-7d47-4176-961f-d87c2adf1cd1"),
        (13, "Midgard Portal Keep", 252, 54053, 24680, 4320, 346, 2, 1, "", 1, 1, 1, 2, 0, 255, 0,
         "Kelt;/keep create 13 255 0 Midgard Portal Keep", "2021-12-03 21:44:42", "5c02edf9-3350-4e30-b6ae-f6d2577a89bf"),
        (14, "Albion Portal Keep", 252, 37301, 52362, 3944, 2035, 1, 1, "", 1, 1, 1, 1, 0, 255, 0,
         "Kelt;/keep create 14 255 0 Albion Portal Keep", "2021-12-03 21:46:26", "bd9d9b80-881d-44b9-afd9-ed9bac6a559d"),
        (35, "Hibernia Portal Keep", 253, 18362, 18257, 4320, 3517, 3, 1, "", 1, 1, 1, 3, 0, 255, 0, "HPK Abermenai",
         "2021-12-03 21:32:39", "35"),
        (41, "Hibernia Portal Keep", 251, 18362, 18257, 4320, 3517, 3, 1, "", 1, 1, 1, 3, 0, 255, 0, "HPK Murdaigean",
         "2021-12-03 21:32:39", "41"),
    ],
    # The gates of the four central keeps (step 3), closed at upstream's full health.
    "Door (Z, Y, X, Heading, InternalID, Health, State, LastTimeRowUpdated, Door_ID)": [
        (3783, 39237, 32673, 2665, 250000301, 9200, 1, "2023-06-15 11:03:20", "55c128b4-5410-49a3-b7de-cf288dd244ec"),
        (3914, 37634, 33192, 1599, 250000302, 9200, 1, "2023-06-15 11:03:20", "cbad55f4-2e57-4533-b083-a92357043c6a"),
        (3724, 37404, 32337, 3642, 251000301, 6200, 1, "2026-10-07 19:30:10.0098444Z", "779977ba-fca8-4898-b797-936bd3f9627d"),
        (3720, 37833, 32698, 1589, 251000302, 6200, 1, "2026-10-07 19:30:10.0105201Z", "5b46ea74-78fd-4c74-b969-4ba635c6ceca"),
        (3720, 38275, 34333, 1024, 252000301, 5200, 1, "2023-06-15 11:03:20", "2b95f0c0-f9a8-493d-ac4f-11d89d0809e7"),
        (3720, 38180, 32654, 1030, 252000302, 5200, 1, "2023-06-15 11:03:20", "91ce710c-043d-4c17-aaad-26b5046df3d0"),
        (3737, 39604, 33849, 1864, 253000301, 4200, 1, "2026-10-07 16:27:09.0947576Z", "cb36d60f-d020-44ac-84bd-f1e7780d5422"),
        (3720, 39059, 33659, 3901, 253000302, 4200, 1, "2026-10-07 16:27:09.0973911Z", "45d1b8c3-4b8e-499f-9ed0-e5e6eadecccf"),
    ],
    "Mob (ClassType, Name, X, Y, Z, Heading, Region, Model, Level, Realm, LastTimeRowUpdated, Mob_ID)": [
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 18826, 17862, 4320, 447, 252, 34, 24, 0, "2022-08-16 17:54:56",
         "08cf170a-3774-42c2-9816-934f78d5bd4e"),
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 54584, 24735, 4320, 1175, 252, 34, 24, 0, "2022-08-16 17:53:38",
         "120a1ccf-94e8-4420-bc72-05ec20390e44"),
        ("DOL.GS.HitbackDummy", "Hitback Dummy - Right Click to Reset", 37678, 53068, 3944, 1008, 252, 34, 24, 0,
         "2022-08-16 19:19:47", "853682fd-1de2-4100-adb5-02f05c8ed7d1"),
        # Atlas put Heal Dummies only outside the battlegrounds; this one (from region 1) is put in 250 so
        # the test covers the class.
        ("DOL.GS.HealDummy", "Heal Dummy", 583988, 476486, 2600, 4088, 250, 34, 50, 0, "2021-12-28 22:03:39",
         "7b98ecad-244c-4edc-a503-1f5def9935e3"),
        # A dummy outside the battlegrounds stays.
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 584452, 476256, 2600, 4, 1, 34, 10, 0, "2021-12-28 22:02:30",
         "125d80ca-f7b0-4b13-8340-d14d1c306a94"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 53441, 25129, 4312, 2617, 252, 2212, 75, 2,
         "2022-08-12 00:15:42", "27d30f96-e238-482d-b359-fc5387589108"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 36776, 52400, 3944, 3072, 250, 2212, 75, 1,
         "2022-08-12 04:56:01", "2ae9038a-72f0-4400-bbb1-53574dd5dfe9"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 18933, 18185, 4320, 976, 252, 2212, 75, 3,
         "2022-08-12 00:15:11", "ecb08ffb-cf86-47f1-a53b-28581a48666b"),
        ("DOL.GS.Keeps.GuardStaticCaster", "Wizard", 33185, 37386, 3722, 2005, 250, 32, 48, 1, "2026-04-05 05:21:17",
         "caledon-guard-25"),
        # A Caer Caledon guard stays.
        ("DOL.GS.Keeps.GuardStaticCaster", "Renegade Runemaster", 32475, 38015, 4106, 1539, 250, 507, 38, 0,
         "2022-06-21 20:04:28", "caledon-guard-23"),
        # Thidranki's Hibernia portal keep (12): its hastener beside its gate and the six casters on its walls
        # (step 5 moves them), and its two fighters.
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 19075, 19035, 4320, 3547, 252, 408, 1, 0, "2022-05-29 21:49:07",
         "802a1b0a-f47e-47b9-a688-e401ad33e42f"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 16751, 18401, 4736, 946, 252, 408, 1, 0, "2022-05-29 21:48:07",
         "62f874d0-333b-475f-a044-109cb0bd74b6"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 17690, 19219, 4736, 519, 252, 408, 1, 0, "2022-05-29 21:48:16",
         "be8e2cbf-6569-4c46-a4aa-d84903a902fc"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 18069, 16884, 4736, 1861, 252, 408, 1, 0, "2022-05-29 21:48:51",
         "3a07da41-d088-4174-980f-1d5ad21fc334"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 18221, 19611, 4736, 3882, 252, 408, 1, 0, "2022-05-29 21:48:21",
         "2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 18598, 17320, 4736, 2478, 252, 408, 1, 0, "2022-05-29 21:48:46",
         "b05f95a5-9e55-4ddf-93d0-340336bc2e16"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 19521, 18014, 4736, 2979, 252, 408, 1, 0, "2022-05-29 21:48:31",
         "f1f1d987-1b9a-421b-a8a1-9df423f118fe"),
        ("DOL.GS.Keeps.GuardFighter", "new mob", 18983, 20057, 4080, 3493, 252, 408, 1, 0, "2022-05-29 21:49:26",
         "b67eacce-2719-48a9-8be7-1dbf0c16b7d2"),
        ("DOL.GS.Keeps.GuardFighter", "new mob", 20133, 18787, 4022, 3507, 252, 408, 1, 0, "2022-05-29 21:49:34",
         "ccaf179f-6c9e-4d09-b375-254d4429ebc2"),
        # The hasteners of Thidranki's Midgard (13) and Albion (14) portal keeps.
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 53318, 25496, 4293, 225, 252, 408, 1, 0, "2022-05-29 21:45:14",
         "9a6e81bd-4024-48e1-8703-04ab84a72f8c"),
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 37196, 51612, 3948, 2009, 252, 408, 1, 0, "2022-05-29 21:43:12",
         "bde71996-1358-42da-a6a2-1a49c7c0adf1"),
        # Thidranki Faste's hastener and its lord: keep guards, but at no portal keep, so step 4 leaves them.
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 34365, 38483, 3720, 3180, 252, 408, 1, 0, "2022-05-29 21:52:39",
         "d558473e-fc07-4a9b-804f-26127f296afd"),
        ("DOL.GS.Keeps.GuardLord", "new mob", 32296, 38267, 4592, 1068, 252, 408, 1, 0, "2022-05-29 21:50:18",
         "863582fc-af9c-4661-8e60-4d8b2985ad2a"),
        # Upstream's guards of Dun Abermenai and Dun Murdaigean (two of each keep's): keep guards in 253 and
        # 251, but in the central keep's area, so step 4 still adds the portal keep guards; and no casters or
        # hastener, so step 5 adds its own.
        ("DOL.GS.Keeps.GuardLord", "Renegade Chieftain Abermenai", 32880, 37637, 4937, 1722, 253, 700, 29, 0,
         "2026-10-07 06:32:18", "91c88f86-159d-486c-a374-a1b0c96ba3cc"),
        ("DOL.GS.Keeps.GuardArcher", "Renegade Ranger", 31879, 37912, 4137, 1349, 253, 342, 22, 0,
         "2026-10-07 06:32:18", "47454e17-34d6-4a6a-8569-c3cf5aab47e0"),
        ("DOL.GS.Keeps.GuardLord", "Renegade Chieftain Murdaigean", 33901, 38869, 4937, 3553, 251, 700, 39, 0,
         "2026-10-07 06:32:18", "fd847893-4c73-4613-a7c2-fda3e6c16dcf"),
        ("DOL.GS.Keeps.GuardArcher", "Renegade Ranger", 34758, 38283, 4137, 3180, 251, 342, 32, 0,
         "2026-10-07 06:32:18", "fcfe1db1-7930-4f00-9964-c4427ca5795a"),
    ],
    # The clean world has no Quest rows; these stand for characters that took Atlas's daily quests.
    "Quest (Name, Step, Character_ID, Quest_ID)": [
        ("DOL.GS.DailyQuest.Albion.ThidKillQuestAlb", 1, "char-alb", "quest-thid"),
        ("DOL.GS.DailyQuest.Hibernia.CaleKillQuestMid", 2, "char-mid", "quest-cale"),
        ("DOL.GS.DailyQuest.Hibernia.CaptureKeepQuestHib", 1, "char-hib", "quest-frontier"),  # not a battleground quest
    ],
}
REMOVED = ("08cf170a-3774-42c2-9816-934f78d5bd4e", "120a1ccf-94e8-4420-bc72-05ec20390e44",
           "27d30f96-e238-482d-b359-fc5387589108", "2ae9038a-72f0-4400-bbb1-53574dd5dfe9",
           "7b98ecad-244c-4edc-a503-1f5def9935e3", "853682fd-1de2-4100-adb5-02f05c8ed7d1",
           "caledon-guard-25", "ecb08ffb-cf86-47f1-a53b-28581a48666b")
# The scratch world's rows that step 4 copies: the keep guards and hasteners within 4,000 of Thidranki's
# portal keeps (not the Void Merchant or the dummy that stand there, nor Thidranki Faste's guards).
PORTAL_KEEP_GUARDS = ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", "3a07da41-d088-4174-980f-1d5ad21fc334",
                      "62f874d0-333b-475f-a044-109cb0bd74b6", "802a1b0a-f47e-47b9-a688-e401ad33e42f",
                      "9a6e81bd-4024-48e1-8703-04ab84a72f8c", "b05f95a5-9e55-4ddf-93d0-340336bc2e16",
                      "b67eacce-2719-48a9-8be7-1dbf0c16b7d2", "bde71996-1358-42da-a6a2-1a49c7c0adf1",
                      "be8e2cbf-6569-4c46-a4aa-d84903a902fc", "ccaf179f-6c9e-4d09-b375-254d4429ebc2",
                      "f1f1d987-1b9a-421b-a8a1-9df423f118fe")
UPSTREAM_CENTRAL_GUARDS = ("47454e17-34d6-4a6a-8569-c3cf5aab47e0", "91c88f86-159d-486c-a374-a1b0c96ba3cc",
                           "fcfe1db1-7930-4f00-9964-c4427ca5795a", "fd847893-4c73-4613-a7c2-fda3e6c16dcf")
KEEP_COLUMNS = ("KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
                "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, "
                "CreateInfo, LastTimeRowUpdated, Keep_ID")
# Step 5's six wall casters of each central keep (spec 3.2 and its 0.35 update): the source Mob_ID (the end of
# the new Mob_ID, after "hdc-bg<region>-ck-"), then X, Y, Z, Heading.
CASTERS = {
    253: [
        ("62f874d0-333b-475f-a044-109cb0bd74b6", 31916, 37946, 4136, 1287),
        ("be8e2cbf-6569-4c46-a4aa-d84903a902fc", 32320, 39124, 4136, 860),
        ("3a07da41-d088-4174-980f-1d5ad21fc334", 33816, 37292, 4136, 2202),
        ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", 32584, 39729, 4136, 127),
        ("b05f95a5-9e55-4ddf-93d0-340336bc2e16", 34056, 37934, 4136, 2819),
        ("f1f1d987-1b9a-421b-a8a1-9df423f118fe", 34509, 38996, 4136, 3320),
    ],
    251: [
        ("62f874d0-333b-475f-a044-109cb0bd74b6", 34724, 38276, 4136, 3108),
        ("be8e2cbf-6569-4c46-a4aa-d84903a902fc", 33942, 37307, 4136, 2681),
        ("3a07da41-d088-4174-980f-1d5ad21fc334", 33163, 39541, 4136, 4023),
        ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", 33487, 36829, 4136, 1948),
        ("b05f95a5-9e55-4ddf-93d0-340336bc2e16", 32718, 39019, 4136, 544),
        ("f1f1d987-1b9a-421b-a8a1-9df423f118fe", 31929, 38176, 4136, 1045),
    ],
}
# Step 5's hastener of each central keep: Thidranki's Hibernia portal keep hastener (the end of the new Mob_ID),
# moved beside the central keep's outer gate (spec 7.3), then X, Y, Z, Heading.
HASTENERS = {
    253: ("802a1b0a-f47e-47b9-a688-e401ad33e42f", 33612, 39657, 3720, 3888),
    251: ("802a1b0a-f47e-47b9-a688-e401ad33e42f", 32546, 37248, 3720, 1613),
}
# The guard and lord levels the server gives each central keep at keep Level 1 (spec 3.4), by KeepID.
GUARD_LEVELS = {33: (21, 24), 11: (26, 31), 32: (31, 36), 31: (37, 44)}


def query(path, sql, params=()):
    conn = sqlite3.connect(path)
    try:
        return conn.execute(sql, params).fetchall()
    finally:
        conn.close()


def execute(path, *statements):
    conn = sqlite3.connect(path)
    try:
        with conn:
            for statement in statements:
                conn.execute(statement)
    finally:
        conn.close()


def dump(path):
    conn = sqlite3.connect(path)
    try:
        return list(conn.iterdump())
    finally:
        conn.close()


def make_world(path):
    execute(path, *WORLD_SCHEMA)
    conn = sqlite3.connect(path)
    try:
        with conn:
            for table, rows in SEED.items():
                conn.executemany(f"INSERT INTO {table} VALUES ({', '.join('?' * len(rows[0]))})", rows)
    finally:
        conn.close()


def apply_fix(path, now=NOW):
    """battlegrounds.apply inside a transaction, as world_fixes.py calls it."""
    conn = sqlite3.connect(path)
    try:
        with conn:
            conn.execute("BEGIN")
            return battlegrounds.apply(conn, now)
    finally:
        conn.close()


def marks(n):
    return ", ".join("?" * n)


def copies(q, region, kind, rows, now):
    """(actual, expected) for the Mob rows a step added to region whose Mob_ID starts "hdc-bg<region>-<kind>-".
    rows lists the expected ones as (end of the Mob_ID, template Mob_ID, (X, Y, Z, Heading) or None): each
    is its template row with that Mob_ID, Region region, LastTimeRowUpdated now and the spot, when given."""
    names = [name for _, name, *_ in q('PRAGMA table_info("Mob")')]
    prefix = f"hdc-bg{region}-{kind}-"
    actual = q(f"SELECT {', '.join(names)} FROM Mob WHERE Mob_ID LIKE ? ORDER BY Mob_ID", (prefix + "%",))
    expected = []
    for end, template, spot in rows:
        (row,) = q(f"SELECT {', '.join(names)} FROM Mob WHERE Mob_ID=?", (template,))
        new = dict(zip(names, row), Mob_ID=prefix + end, Region=region, LastTimeRowUpdated=now)
        if spot:
            new.update(zip(("X", "Y", "Z", "Heading"), spot))
        expected.append(tuple(new[name] for name in names))
    return actual, sorted(expected, key=lambda row: row[names.index("Mob_ID")])


def casters(region):
    """CASTERS[region] as copies() takes them: each caster copies its source."""
    return [(end, end, spot) for end, *spot in CASTERS[region]]


def hastener(region):
    """HASTENERS[region] as copies() takes it: the hastener copies its source."""
    end, *spot = HASTENERS[region]
    return [(end, end, spot)]


def guard_levels(base_level, level, multiplier):
    """A central keep's guard and lord levels, as the server sets them (AbstractGameKeep.GetBaseLevel and
    SetGuardLevel: the byte cast rounds down)."""
    return (int(base_level + 1 + level * multiplier),
            int(base_level + (base_level // 10 + 1) * 2 + level * multiplier))


def to_model(region, x, y):
    """The opposite of battlegrounds.moved for X, Y: a spot on region's central keep model, put back on
    Thidranki's Hibernia portal keep model, where the central keeps of 253 and 251 can be compared."""
    (cx, cy), degrees = battlegrounds.MOVES[region]
    angle = -math.radians(degrees)
    dx, dy = x - cx, y - cy
    return (battlegrounds.P[0] + dx * math.cos(angle) - dy * math.sin(angle),
            battlegrounds.P[1] + dx * math.sin(angle) + dy * math.cos(angle))


class BattlegroundFixTests(unittest.TestCase):
    LINES = [
        "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
        "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
        "Battlegrounds: keep levels for the ranges (Dun Abermenai base level 19, Thidranki Faste base level 24, "
        "Dun Murdaigean base level 29, Caer Caledon base level 35, 8 gates' health)",
        "Battlegrounds: portal keep guards and hasteners for Abermenai (11), Murdaigean (11)",
        "Battlegrounds: central keep guards for Dun Abermenai (6 wall casters, 1 hastener), "
        "Dun Murdaigean (6 wall casters, 1 hastener)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
        "3 Void Merchants, the stray Wizard); 2 saved battleground daily quests deleted",
    ]
    # Where each step is made to fail: on the last statement it runs, so the steps before it, and that
    # step's own earlier statements, have already changed rows.
    FAILURES = {
        "_step1_battleground_rows": "BEFORE UPDATE ON Battleground WHEN OLD.RegionID = 250",
        "_step2_names_and_xp": "BEFORE UPDATE OF Experience ON Zones WHEN OLD.ZoneID = 250",
        "_step3_keep_levels": "BEFORE UPDATE ON Door WHEN OLD.InternalID = 250000302",
        "_step4_portal_keep_guards": "BEFORE INSERT ON Mob WHEN NEW.Region = 251",
        "_step5_central_keep_guards": "BEFORE INSERT ON Mob WHEN NEW.Mob_ID = "
                                      "'hdc-bg251-ck-802a1b0a-f47e-47b9-a688-e401ad33e42f'",
        "_step6_atlas_leftovers": "BEFORE DELETE ON Quest",
    }

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = self.world("world.db")

    def tearDown(self):
        self.tmp.cleanup()

    def world(self, name):
        path = os.path.join(self.tmp.name, name)
        make_world(path)
        return path

    def q(self, sql, params=()):
        return query(self.db, sql, params)

    def run_world_fixes(self):
        return subprocess.run([sys.executable, os.path.join(BIN, "world_fixes.py"), "--db", self.db],
                              capture_output=True, text=True)

    def test_each_step_changes_upstream_values_and_reports(self):
        execute(self.db, "UPDATE Keep SET Level=4 WHERE KeepID=31")  # a capture on this world left it at 4
        self.assertEqual(apply_fix(self.db), [
            "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
            "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
            "Battlegrounds: keep levels for the ranges (Dun Abermenai base level 19, Thidranki Faste base level 24, "
            "Dun Murdaigean base level 29, Caer Caledon base level 35, Caer Caledon back to level 1, 8 gates' health)",
            "Battlegrounds: portal keep guards and hasteners for Abermenai (11), Murdaigean (11)",
            "Battlegrounds: central keep guards for Dun Abermenai (6 wall casters, 1 hastener), "
            "Dun Murdaigean (6 wall casters, 1 hastener)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
            "3 Void Merchants, the stray Wizard); 2 saved battleground daily quests deleted",
        ])
        self.assertEqual(self.q("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel, LastTimeRowUpdated "
                                "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45, "2000-01-01 00:00:00"),
            (250, "Caledonia (Level 30-35 - RR1L9)", 30, 35, 10, NOW),
            (251, "Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6, NOW),
            (252, "Thidranki (Level 20-24 - RR1L3)", 20, 24, 4, NOW),
            (253, "Abermenai (Level 15-19 - RR1L2)", 15, 19, 3, NOW),
        ])
        self.assertEqual(self.q("SELECT ZoneID, Name, Experience, LastTimeRowUpdated FROM Zones ORDER BY ZoneID"), [
            (250, "Caledonia", 0, NOW), (251, "Murdaigean", 0, "2017-01-08 18:51:19"),
            (252, "Thidranki", 0, NOW), (253, "Abermenai", 0, "2017-01-08 18:51:19"),
        ])
        self.assertEqual(self.q("SELECT RegionID, Description, LastTimeRowUpdated FROM Regions ORDER BY RegionID"), [
            (250, "Caledonia", NOW), (251, "Murdaigean", "2017-01-08 18:50:36"),
            (252, "Thidranki", "2017-01-08 18:50:36"), (253, "Abermenai", "2017-01-08 18:50:36"),
        ])
        self.assertEqual(self.q("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep ORDER BY KeepID"), [
            (11, 24, 1, NOW), (12, 255, 1, "2021-12-03 21:32:39"), (13, 255, 1, "2021-12-03 21:44:42"),
            (14, 255, 1, "2021-12-03 21:46:26"), (31, 35, 1, NOW), (32, 29, 1, NOW), (33, 19, 1, NOW),
            (35, 255, 1, "2021-12-03 21:32:39"), (41, 255, 1, "2021-12-03 21:32:39"),
        ])
        self.assertEqual(self.q("SELECT InternalID, Health, State, LastTimeRowUpdated FROM Door ORDER BY InternalID"), [
            (250000301, 7000, 1, NOW), (250000302, 7000, 1, NOW), (251000301, 5800, 1, NOW), (251000302, 5800, 1, NOW),
            (252000301, 4800, 1, NOW), (252000302, 4800, 1, NOW), (253000301, 3800, 1, NOW), (253000302, 3800, 1, NOW),
        ])
        # Apart from the keep guards of 251 to 253, only these two rows stay.
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob WHERE Region NOT IN (251, 252, 253) "
                                "OR ClassType NOT LIKE 'DOL.GS.Keeps.%' ORDER BY Mob_ID"),
                         [("125d80ca-f7b0-4b13-8340-d14d1c306a94",), ("caledon-guard-23",)])
        # Thidranki's 13 keep guard rows stay, and so do upstream's central keep guards. 253 and 251 each get a
        # copy of Thidranki's 11 portal keep rows on the same spots, although their central keeps have guards,
        # and six wall casters and a hastener on the spec's spots, every other column from the source.
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Region=252"), [(13,)])
        self.assertEqual(self.q(f"SELECT COUNT(*) FROM Mob WHERE Mob_ID IN ({marks(4)}) AND LastTimeRowUpdated=?",
                                UPSTREAM_CENTRAL_GUARDS + ("2026-10-07 06:32:18",)), [(4,)])
        for region in (253, 251):
            with self.subTest(region=region):
                self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Region=?", (region,)), [(2 + 11 + 7,)])
                self.assertEqual(*copies(self.q, region, "pk", [(m, m, None) for m in PORTAL_KEEP_GUARDS], NOW))
                self.assertEqual(*copies(self.q, region, "ck", casters(region) + hastener(region), NOW))
        self.assertEqual(self.q("SELECT Name FROM Quest"), [("DOL.GS.DailyQuest.Hibernia.CaptureKeepQuestHib",)])
        self.assertEqual(self.q("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v2", NOW)])

    def test_a_keep_put_back_to_level_1_gets_level_1_gate_health(self):
        # As the server does after a capture (OnKeepTaken). Here an upgrade carried Dun Abermenai at level 4, its
        # outer gate at level 4's full health, its inner gate damaged and open.
        execute(self.db, "UPDATE Keep SET Level=4 WHERE KeepID=33",
                "UPDATE Door SET Health=15200 WHERE InternalID=253000301",
                "UPDATE Door SET Health=2000, State=0 WHERE InternalID=253000302")
        self.assertIn("Battlegrounds: keep levels for the ranges (Dun Abermenai base level 19, Thidranki Faste base "
                      "level 24, Dun Murdaigean base level 29, Caer Caledon base level 35, Dun Abermenai back to level 1, "
                      "7 gates' health)", apply_fix(self.db))
        self.assertEqual(self.q("SELECT InternalID, Health, State FROM Door WHERE InternalID IN (253000301, 253000302) "
                                "ORDER BY InternalID"), [(253000301, 3800, 1), (253000302, 2000, 0)])
        self.assertEqual(self.q("SELECT Level, BaseLevel FROM Keep WHERE KeepID=33"), [(1, 19)])

    def test_second_run_changes_nothing(self):
        self.assertEqual(apply_fix(self.db), self.LINES)
        before = dump(self.db)
        self.assertEqual(apply_fix(self.db, LATER), [])
        self.assertEqual(dump(self.db), before)

    def test_a_failing_step_rolls_back_everything(self):
        self.assertEqual([step.__name__ for step in battlegrounds.STEPS], list(self.FAILURES))
        for step, when in self.FAILURES.items():
            with self.subTest(step=step):
                db = self.world(f"{step}.db")
                execute(db, INJECTED.format(when))
                before = dump(db)
                self.assertEqual(apply_fix(db), [
                    "Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them"])
                self.assertEqual(dump(db), before)  # no change, no fork table, no marker
                execute(db, "DROP TRIGGER injected")
                self.assertEqual(apply_fix(db), self.LINES)  # so the next start tries again

    def test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails(self):
        execute(self.db, *WORLD_FIXES_SCHEMA,
                "INSERT INTO ServerProperty (Category, `Key`, Value) VALUES ('classes', 'disabled_classes', '20;33')",
                INJECTED.format("BEFORE UPDATE ON Keep"))
        r = self.run_world_fixes()
        self.assertEqual((r.returncode, r.stderr), (0, ""))
        self.assertEqual(r.stdout.splitlines(), [
            "Disciple (Necromancer's base class) enabled: disabled_classes 20;33 -> 33",
            "Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them",
        ])
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'"), [("33",)])
        self.assertEqual(self.q("SELECT name FROM sqlite_master WHERE name LIKE 'fork%'"), [])
        self.assertEqual(self.q("SELECT MaxRealmLevel FROM Battleground WHERE RegionID=252"), [(10,)])
        self.assertEqual(self.q("SELECT Name FROM Zones WHERE ZoneID=250"), [("Caledon",)])
        execute(self.db, "DROP TRIGGER injected")
        r = self.run_world_fixes()
        self.assertEqual((r.returncode, r.stderr, r.stdout.splitlines()), (0, "", self.LINES))

    def test_owner_values_are_kept(self):
        execute(self.db,
                "UPDATE Battleground SET MaxRealmLevel=7 WHERE RegionID=252",
                "UPDATE Zones SET Name='Caledonia Fields' WHERE ZoneID=250",
                "UPDATE Regions SET Description='Caledonia Fields' WHERE RegionID=250",
                "UPDATE Zones SET Experience=25 WHERE ZoneID=252",
                # Thidranki Faste and Dun Abermenai at the owner's base levels: their gates stay as they are.
                "UPDATE Keep SET BaseLevel=30 WHERE KeepID=11",
                "UPDATE Keep SET BaseLevel=25 WHERE KeepID=33",
                "UPDATE Door SET Health=6000 WHERE InternalID=252000301",
                "UPDATE Door SET Health=3000 WHERE InternalID=253000301",
                "UPDATE Mob SET Level=50 WHERE Mob_ID='caledon-guard-25'",
                # The owner removed all but one dummy and one Void Merchant; one saved quest is left.
                "DELETE FROM Mob WHERE Mob_ID IN ('08cf170a-3774-42c2-9816-934f78d5bd4e', "
                "'120a1ccf-94e8-4420-bc72-05ec20390e44', '853682fd-1de2-4100-adb5-02f05c8ed7d1', "
                "'27d30f96-e238-482d-b359-fc5387589108', 'ecb08ffb-cf86-47f1-a53b-28581a48666b')",
                "DELETE FROM Quest WHERE Quest_ID='quest-cale'",
                # A guard of the owner's at Abermenai's Hibernia portal keep, a caster in Dun Murdaigean and a
                # hastener in Dun Abermenai.
                "INSERT INTO Mob (ClassType, Name, X, Y, Z, Region, Mob_ID) VALUES ('DOL.GS.Keeps.GuardFighter', "
                "'new mob', 19000, 19000, 4320, 253, 'owner-guard')",
                "INSERT INTO Mob (ClassType, Name, X, Y, Z, Region, Mob_ID) VALUES ('DOL.GS.Keeps.GuardStaticCaster', "
                "'new mob', 34000, 39000, 4137, 251, 'owner-caster')",
                "INSERT INTO Mob (ClassType, Name, X, Y, Z, Region, Mob_ID) VALUES ('DOL.GS.Keeps.FrontierHastener', "
                "'new mob', 32000, 37500, 3721, 253, 'owner-hastener')")
        self.assertEqual(apply_fix(self.db), [
            "Battlegrounds: classic level and realm rank limits for Abermenai, Murdaigean, Caledonia",
            "Battlegrounds: no zone XP bonus in Caledonia",
            "Battlegrounds: keep levels for the ranges (Dun Murdaigean base level 29, Caer Caledon base level 35, "
            "4 gates' health)",
            "Battlegrounds: portal keep guards and hasteners for Murdaigean (11)",
            "Battlegrounds: central keep guards for Dun Abermenai (6 wall casters), Dun Murdaigean (1 hastener)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (1 training dummy, "
            "1 Void Merchant); 1 saved battleground daily quest deleted",
        ])
        self.assertEqual(self.q("SELECT Battleground_ID, MaxRealmLevel FROM Battleground WHERE RegionID=252"),
                         [("Thidranki (Level 20-24 - RR2L0)", 7)])
        self.assertEqual(self.q("SELECT Name, Experience FROM Zones WHERE ZoneID IN (250, 252) ORDER BY ZoneID"),
                         [("Caledonia Fields", 0), ("Thidranki", 25)])
        self.assertEqual(self.q("SELECT Description FROM Regions WHERE RegionID=250"), [("Caledonia Fields",)])
        self.assertEqual(self.q("SELECT KeepID, BaseLevel FROM Keep WHERE KeepID IN (11, 33) ORDER BY KeepID"),
                         [(11, 30), (33, 25)])
        self.assertEqual(self.q("SELECT InternalID, Health FROM Door WHERE InternalID IN (252000301, 252000302, "
                                "253000301, 253000302) ORDER BY InternalID"),
                         [(252000301, 6000), (252000302, 5200), (253000301, 3000), (253000302, 4200)])
        self.assertEqual(self.q("SELECT Name, Level FROM Mob WHERE Mob_ID='caledon-guard-25'"), [("Wizard", 50)])
        # 253 gets no portal keep copies but its wall casters; 251 the other way round. The casters and the
        # hastener go by their own class: 253 gets no hastener, and 251 gets its hastener but no casters.
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg253-pk-%'"), [(0,)])
        self.assertEqual(*copies(self.q, 253, "ck", casters(253), NOW))
        self.assertEqual(*copies(self.q, 251, "ck", hastener(251), NOW))
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg251-pk-%'"), [(11,)])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v2",)])

    def test_removed_rows_are_archived(self):
        mob = [(name, declared) for _, name, declared, *_ in self.q('PRAGMA table_info("Mob")')]
        names = ", ".join(name for name, _ in mob)
        rows = self.q(f"SELECT {names} FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))}) ORDER BY Mob_ID", REMOVED)
        self.assertEqual(len(rows), 8)
        apply_fix(self.db)
        self.assertEqual(self.q(f"SELECT Mob_ID FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))})", REMOVED), [])
        self.assertEqual(self.q(f"SELECT {names}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v2", NOW) for row in rows])
        self.assertEqual([(name, declared) for _, name, declared, *_ in self.q('PRAGMA table_info("fork_removed_mobs")')],
                         mob + [("FixId", "TEXT"), ("RemovedUtc", "TEXT")])

        # An archive table made before Mob gained a column (the server adds new columns to a world's tables
        # at start) still takes the rows, in the columns both tables have.
        db = self.world("older-archive.db")
        execute(db, "CREATE TABLE fork_removed_mobs (ClassType TEXT, Name VARCHAR(255), Region INT, "
                    "Mob_ID VARCHAR(255), FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL)")
        older = "ClassType, Name, Region, Mob_ID"
        rows = query(db, f"SELECT {older} FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))}) ORDER BY Mob_ID", REMOVED)
        apply_fix(db)
        self.assertEqual(query(db, f"SELECT {older}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v2", NOW) for row in rows])

    def test_a_run_without_the_marker_adds_nothing_twice(self):
        # The owner removes one of Dun Abermenai's casters and deletes the marker: the next run finds every step
        # done (the keep still has casters, and its hastener), changes nothing else and writes the marker again.
        self.assertEqual(apply_fix(self.db), self.LINES)
        caster = "hdc-bg253-ck-" + CASTERS[253][0][0]
        execute(self.db, f"DELETE FROM Mob WHERE Mob_ID='{caster}'", "DELETE FROM fork_world_fixes")
        before = dump(self.db)
        self.assertEqual(apply_fix(self.db, LATER), [])
        after = dump(self.db)
        self.assertEqual([line for line in after if line not in before],
                         ["INSERT INTO \"fork_world_fixes\" VALUES('classic-battlegrounds-v2','2026-10-08 12:00:00');"])
        self.assertEqual([line for line in before if line not in after], [])

    def test_a_stray_copy_id_does_not_stop_the_fix(self):
        # A row of the owner's that already has one of step 4's or step 5's Mob_IDs (and is no keep guard) is
        # left as it is, the step adds the other copies, and the fix applies.
        strays = ("hdc-bg253-pk-" + PORTAL_KEEP_GUARDS[0], "hdc-bg251-ck-" + CASTERS[251][0][0],
                  "hdc-bg253-ck-" + HASTENERS[253][0])
        for stray in strays:
            execute(self.db, "INSERT INTO Mob (ClassType, Name, X, Y, Z, Region, Mob_ID) VALUES "
                             f"('DOL.GS.GameNPC', 'stray', 1, 2, 3, {stray[6:9]}, '{stray}')")
        lines = apply_fix(self.db)
        self.assertEqual(lines[3:5], ["Battlegrounds: portal keep guards and hasteners for Abermenai (10), Murdaigean (11)",
                                      "Battlegrounds: central keep guards for Dun Abermenai (6 wall casters), "
                                      "Dun Murdaigean (5 wall casters, 1 hastener)"])
        for stray in strays:
            self.assertEqual(self.q("SELECT ClassType, Name, X, Y, Z, Region FROM Mob WHERE Mob_ID=?", (stray,)),
                             [("DOL.GS.GameNPC", "stray", 1, 2, 3, int(stray[6:9]))])
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg253-pk-%'"), [(11,)])
        self.assertEqual(self.q("SELECT Region, COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg%-ck-%' GROUP BY Region"),
                         [(251, 7), (253, 7)])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v2",)])

    def test_no_central_keep_rows_without_the_rows_they_are_made_from(self):
        # Without a central keep's Keep row (here: in another region), step 5 leaves that keep as it is. Without
        # one of Thidranki's six wall casters, no keep gets casters, and without its hastener no keep gets a
        # hastener; each time the other kind is still added. The fix still applies and writes its marker.
        both = "Dun Murdaigean (6 wall casters, 1 hastener)"
        cases = [
            ("DELETE FROM Keep WHERE KeepID=33", ["Battlegrounds: central keep guards for " + both], [(251, 7)]),
            ("UPDATE Keep SET Region=251 WHERE KeepID=33", ["Battlegrounds: central keep guards for " + both],
             [(251, 7)]),
            ("DELETE FROM Mob WHERE Mob_ID='f1f1d987-1b9a-421b-a8a1-9df423f118fe'",
             ["Battlegrounds: central keep guards for Dun Abermenai (1 hastener), Dun Murdaigean (1 hastener)"],
             [(251, 1), (253, 1)]),
            ("DELETE FROM Mob WHERE Mob_ID='802a1b0a-f47e-47b9-a688-e401ad33e42f'",
             ["Battlegrounds: central keep guards for Dun Abermenai (6 wall casters), Dun Murdaigean (6 wall casters)"],
             [(251, 6), (253, 6)]),
        ]
        for n, (statement, lines, added) in enumerate(cases):
            with self.subTest(statement):
                db = self.world(f"missing-{n}.db")
                execute(db, statement)
                self.assertEqual([x for x in apply_fix(db) if "central keep guards" in x], lines)
                self.assertEqual(query(db, "SELECT Region, COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg%-ck-%' "
                                           "GROUP BY Region"), added)
                self.assertEqual(query(db, "SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v2",)])

    def test_no_marker_without_the_needed_tables(self):
        for table in battlegrounds.NEEDED_TABLES:
            with self.subTest(table=table):
                db = self.world(f"no-{table}.db")
                execute(db, f"DROP TABLE {table}")
                before = dump(db)
                self.assertEqual(apply_fix(db), [])
                self.assertEqual(dump(db), before)


class BattlegroundGeometryTests(unittest.TestCase):
    """The move onto a central keep model (spec 3.2, "The move")."""

    def test_p_maps_to_c(self):
        self.assertEqual(battlegrounds.moved(253, 18048, 18176, 4320, 0), (33152, 38400, 3720, 341))
        self.assertEqual(battlegrounds.moved(251, 18048, 18176, 4320, 0), (33408, 38272, 3720, 2162))

    def test_a_point_east_of_p_turns_by_the_angle(self):
        # P + (100, 0) lands 100 units from C (give or take the rounding to whole units), at 30 degrees in 253
        # and 190 in 251 (from +X towards +Y).
        for region, (cx, cy), spot, degrees in ((253, (33152, 38400), (33239, 38450, 3720, 341), 30),
                                                (251, (33408, 38272), (33310, 38255, 3720, 2162), 190)):
            with self.subTest(region=region):
                x, y, z, heading = battlegrounds.moved(region, 18148, 18176, 4320, 0)
                self.assertEqual((x, y, z, heading), spot)
                self.assertAlmostEqual(math.hypot(x - cx, y - cy), 100, delta=1)
                self.assertAlmostEqual(math.degrees(math.atan2(y - cy, x - cx)) % 360, degrees, delta=0.5)
                self.assertLess(math.dist(to_model(region, x, y), (18148, 18176)), 1)

    def test_headings_wrap_at_4096(self):
        self.assertEqual(battlegrounds.moved(253, 18048, 18176, 4320, 4000)[3], 245)  # 4000 + 341 - 4096
        self.assertEqual(battlegrounds.moved(251, 18048, 18176, 4320, 1934)[3], 0)  # 1934 + 2162 = 4096
        self.assertEqual(battlegrounds.moved(251, 18048, 18176, 4320, 4095)[3], 2161)

    def test_guard_levels(self):
        # Spec 3.4: at keep Level 1 and keep_guard_level_multiplier 1.6.
        self.assertEqual({keep_id: guard_levels(battlegrounds.KEEP_LEVELS[keep_id][3], 1, 1.6)
                          for keep_id in GUARD_LEVELS}, GUARD_LEVELS)
        # Upstream 0.35's base levels would give Dun Abermenai 23 and 28, Dun Murdaigean 33 and 40.
        self.assertEqual([guard_levels(battlegrounds.KEEP_LEVELS[keep_id][2], 1, 1.6) for keep_id in (33, 32)],
                         [(23, 28), (33, 40)])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class BattlegroundShippedWorldTests(unittest.TestCase):
    """The clean classic world (0.35) before the fix (read-only), and a copy of it after one run."""

    LINES = [
        "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
        "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
        "Battlegrounds: keep levels for the ranges (Dun Abermenai base level 19, Thidranki Faste base level 24, "
        "Dun Murdaigean base level 29, Caer Caledon base level 35, 8 gates' health)",
        "Battlegrounds: portal keep guards and hasteners for Abermenai (34), Murdaigean (34)",
        "Battlegrounds: central keep guards for Dun Abermenai (6 wall casters, 1 hastener), "
        "Dun Murdaigean (6 wall casters, 1 hastener)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
        "3 Void Merchants, the stray Wizard)",
    ]
    LEFTOVERS = ("(ClassType IN ('DOL.GS.DPSDummy', 'DOL.GS.HitbackDummy', 'DOL.GS.HealDummy', "
                 "'DOL.GS.Scripts.RPTradeInMerchant') AND Region BETWEEN 250 AND 253) OR Mob_ID='caledon-guard-25'")
    CENTRAL_KEEPS = "KeepID IN (11, 31, 32, 33)"
    GATES = ("InternalID IN (250000301, 250000302, 251000301, 251000302, 252000301, 252000302, 253000301, "
             "253000302)")
    # Thidranki's portal keep guards and hasteners: the rows step 4 copies.
    PORTAL_KEEP_GUARDS = ("Region=252 AND ClassType IN ('DOL.GS.Keeps.FrontierHastener', 'DOL.GS.Keeps.GuardFighter', "
                          "'DOL.GS.Keeps.GuardStaticCaster') AND EXISTS (SELECT 1 FROM Keep k WHERE k.KeepID IN (12, 13, 14) "
                          "AND (Mob.X-k.X)*(Mob.X-k.X) + (Mob.Y-k.Y)*(Mob.Y-k.Y) <= 4000*4000)")
    # The keep areas (keeps/KeepArea.cs): 4,000 around a portal keep (BaseLevel 100 or more), 3,000 around a keep.
    AREA = ("k.Region=m.Region AND (m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= "
            "CASE WHEN k.BaseLevel >= 100 THEN 4000*4000 ELSE 3000*3000 END")
    # Upstream 0.35's guards of Dun Abermenai (253) and Dun Murdaigean (251), as Mob rows m.
    UPSTREAM_CENTRAL_GUARDS = "m.Region IN (251, 253) AND m.ClassType LIKE 'DOL.GS.Keeps.%' AND m.Mob_ID NOT LIKE 'hdc-%'"

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.lines = apply_fix(cls.db)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def before(self, sql, params=()):
        conn = sqlite3.connect(pathlib.Path(TEST_WORLD).resolve().as_uri() + "?mode=ro", uri=True)
        try:
            return conn.execute(sql, params).fetchall()
        finally:
            conn.close()

    def after(self, sql, params=()):
        return query(self.db, sql, params)

    def test_world_holds_what_the_steps_expect(self):
        self.assertEqual(self.before("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel "
                                     "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45),
            (250, "Caledonia (Level 34-39 - RR3L5)", 30, 35, 25),
            (251, "Murdaigean (Level 25-29)", 25, 29, 5),
            (252, "Thidranki (Level 20-24 - RR2L0)", 20, 24, 10),
            (253, "Abermenai (Level 15-19)", 15, 19, 2),
        ])
        self.assertEqual(self.before("SELECT ZoneID, RegionID, Name, Experience FROM Zones WHERE RegionID BETWEEN 250 AND 253 "
                                     "ORDER BY ZoneID"), [
            (250, 250, "Caledon", 50), (251, 251, "Murdaigean", 0), (252, 252, "Thidranki", 50), (253, 253, "Abermenai", 0),
        ])
        self.assertEqual(self.before("SELECT RegionID, Description FROM Regions WHERE RegionID BETWEEN 250 AND 253 "
                                     "ORDER BY RegionID"),
                         [(250, "Caledon"), (251, "Murdaigean"), (252, "Thidranki"), (253, "Abermenai")])
        # The four central keeps: Atlas's two, and upstream 0.35's Dun Murdaigean (32) and Dun Abermenai (33), all
        # on the same spot, held by renegades, at keep Level 1.
        self.assertEqual(self.before(f"SELECT {KEEP_COLUMNS} FROM Keep WHERE {self.CENTRAL_KEEPS} ORDER BY KeepID"), [
            (11, "Thidranki Faste", 252, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 26, 0, "Atlas BG",
             "2022-10-09 21:09:42", "11"),
            (31, "Caer Caledon", 250, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 46, 0, "Atlas BG",
             "2022-08-18 17:36:33", "31"),
            (32, "Dun Murdaigean", 251, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 31, 0,
             "Classic BG (Claude 2026-10-07)", "2022-10-09 21:09:42", "32"),
            (33, "Dun Abermenai", 253, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 21, 0,
             "Classic BG (Claude 2026-10-07)", "2022-10-09 21:09:42", "33"),
        ])
        self.assertEqual(self.before("SELECT Region, KeepID FROM Keep WHERE Region BETWEEN 250 AND 253 AND BaseLevel < 100 "
                                     "ORDER BY Region"), [(250, 31), (251, 32), (252, 11), (253, 33)])
        # Their gates: closed, at full health for upstream's BaseLevel (x 200).
        self.assertEqual(self.before(f"SELECT InternalID, Health, State FROM Door WHERE {self.GATES} ORDER BY InternalID"), [
            (250000301, 9200, 1), (250000302, 9200, 1), (251000301, 6200, 1), (251000302, 6200, 1),
            (252000301, 5200, 1), (252000302, 5200, 1), (253000301, 4200, 1), (253000302, 4200, 1),
        ])
        self.assertEqual(self.before("SELECT `Key`, Value FROM ServerProperty WHERE `Key` IN ('keep_doors_base_health', "
                                     "'keep_guard_level_multiplier', 'starting_keep_level') ORDER BY `Key`"),
                         [("keep_doors_base_health", "200"), ("keep_guard_level_multiplier", "1.6"),
                          ("starting_keep_level", "4")])
        self.assertEqual(self.before("SELECT ClassType, Region, COUNT(*) FROM Mob WHERE ClassType IN ('DOL.GS.DPSDummy', "
                                     "'DOL.GS.HitbackDummy', 'DOL.GS.HealDummy') AND Region BETWEEN 250 AND 253 "
                                     "GROUP BY ClassType, Region ORDER BY ClassType"),
                         [("DOL.GS.DPSDummy", 252, 9), ("DOL.GS.HitbackDummy", 252, 6)])
        self.assertEqual(self.before("SELECT Mob_ID, Region, Realm FROM Mob WHERE ClassType='DOL.GS.Scripts.RPTradeInMerchant' "
                                     "AND Region BETWEEN 250 AND 253 ORDER BY Mob_ID"), [
            ("27d30f96-e238-482d-b359-fc5387589108", 252, 2),
            ("2ae9038a-72f0-4400-bbb1-53574dd5dfe9", 250, 1),
            ("ecb08ffb-cf86-47f1-a53b-28581a48666b", 252, 3),
        ])
        self.assertEqual(self.before("SELECT Mob_ID, ClassType, Name, Realm, Level, X, Y, Z, Region FROM Mob "
                                     "WHERE Mob_ID='caledon-guard-25'"),
                         [("caledon-guard-25", "DOL.GS.Keeps.GuardStaticCaster", "Wizard", 1, 48, 33185, 37386, 3722, 250)])
        self.assertEqual(self.before("SELECT COUNT(*) FROM Quest"), [(0,)])
        # KeepManager.ExitBattleground moves an over-limit character to its realm's portal keep by these
        # TeleportIDs; the fork fixed "Svasudheim Faste" to "Svasud Faste".
        self.assertEqual(self.before("SELECT TeleportID, Realm, RegionID, COUNT(*) FROM Teleport WHERE TeleportID IN "
                                     "('Castle Sauvage', 'Svasud Faste', 'Druim Ligen', 'Svasudheim Faste') "
                                     "GROUP BY TeleportID, Realm, RegionID ORDER BY TeleportID"),
                         [("Castle Sauvage", 1, 1, 2), ("Druim Ligen", 3, 200, 2), ("Svasud Faste", 2, 100, 2)])
        # Step 4: Thidranki's 34 portal keep guards and hasteners. The Hibernia (12) and Midgard (13) portal keeps
        # have 1 hastener, 2 fighters and 8 casters each, and the Albion one (14) 1, 2 and 9. The portal keeps of
        # 251 and 253 have no keep guards.
        self.assertEqual(self.before(
            "SELECT k.KeepID, m.ClassType, COUNT(*) FROM Mob m JOIN Keep k ON k.KeepID IN (12, 13, 14) "
            "AND (m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= 4000*4000 WHERE m.Region=252 AND m.ClassType IN "
            "('DOL.GS.Keeps.FrontierHastener', 'DOL.GS.Keeps.GuardFighter', 'DOL.GS.Keeps.GuardStaticCaster') "
            "GROUP BY k.KeepID, m.ClassType ORDER BY k.KeepID, m.ClassType"), [
            (12, "DOL.GS.Keeps.FrontierHastener", 1), (12, "DOL.GS.Keeps.GuardFighter", 2),
            (12, "DOL.GS.Keeps.GuardStaticCaster", 8),
            (13, "DOL.GS.Keeps.FrontierHastener", 1), (13, "DOL.GS.Keeps.GuardFighter", 2),
            (13, "DOL.GS.Keeps.GuardStaticCaster", 8),
            (14, "DOL.GS.Keeps.FrontierHastener", 1), (14, "DOL.GS.Keeps.GuardFighter", 2),
            (14, "DOL.GS.Keeps.GuardStaticCaster", 9),
        ])
        self.assertEqual(self.before(f"SELECT COUNT(*) FROM Mob WHERE {self.PORTAL_KEEP_GUARDS}"), [(34,)])
        self.assertEqual(self.before(f"SELECT COUNT(*) FROM Mob m JOIN Keep k ON {self.AREA} AND k.BaseLevel >= 100 "
                                     "WHERE m.Region IN (251, 253) AND m.ClassType LIKE 'DOL.GS.Keeps.%'"), [(0,)])
        # Upstream's central keep guards: 26 in Dun Abermenai and 27 in Dun Murdaigean, each in its keep's area and
        # in no other, and no casters or hastener among them (step 5's preconditions).
        self.assertEqual(self.before(f"SELECT m.Region, k.KeepID, m.ClassType, COUNT(*) FROM Mob m JOIN Keep k ON {self.AREA} "
                                     f"WHERE {self.UPSTREAM_CENTRAL_GUARDS} GROUP BY 1, 2, 3 ORDER BY 1 DESC, 3"), [
            (253, 33, "DOL.GS.Keeps.GuardArcher", 4), (253, 33, "DOL.GS.Keeps.GuardCommander", 3),
            (253, 33, "DOL.GS.Keeps.GuardFighter", 18), (253, 33, "DOL.GS.Keeps.GuardLord", 1),
            (251, 32, "DOL.GS.Keeps.GuardArcher", 5), (251, 32, "DOL.GS.Keeps.GuardCommander", 3),
            (251, 32, "DOL.GS.Keeps.GuardFighter", 18), (251, 32, "DOL.GS.Keeps.GuardLord", 1),
        ])
        self.assertEqual(self.before(f"SELECT Region, COUNT(*) FROM Mob m WHERE {self.UPSTREAM_CENTRAL_GUARDS} "
                                     "GROUP BY Region ORDER BY Region"), [(251, 27), (253, 26)])
        # No monster (upstream 0.35's GameNPC rows) stands in a keep area of 251 or 253.
        self.assertEqual(self.before(f"SELECT COUNT(*) FROM Mob m JOIN Keep k ON {self.AREA} WHERE m.Region IN (251, 253) "
                                     "AND m.ClassType='DOL.GS.GameNPC'"), [(0,)])
        # Step 5: the Hibernia portal keep's six casters on its walls and its hastener beside its gate, "new mob"
        # placeholders at level 1, model 408.
        sources = [end for end, *_ in CASTERS[253]] + [HASTENERS[253][0]]
        self.assertEqual(self.before(f"SELECT Mob_ID, ClassType, Name, Region, Level, Model, X, Y, Z, Heading FROM Mob "
                                     f"WHERE Mob_ID IN ({marks(7)}) ORDER BY Mob_ID", sources), [
            ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", "DOL.GS.Keeps.GuardStaticCaster", "new mob", 252, 1, 408,
             18221, 19611, 4736, 3882),
            ("3a07da41-d088-4174-980f-1d5ad21fc334", "DOL.GS.Keeps.GuardStaticCaster", "new mob", 252, 1, 408,
             18069, 16884, 4736, 1861),
            ("62f874d0-333b-475f-a044-109cb0bd74b6", "DOL.GS.Keeps.GuardStaticCaster", "new mob", 252, 1, 408,
             16751, 18401, 4736, 946),
            ("802a1b0a-f47e-47b9-a688-e401ad33e42f", "DOL.GS.Keeps.FrontierHastener", "new mob", 252, 1, 408,
             19075, 19035, 4320, 3547),
            ("b05f95a5-9e55-4ddf-93d0-340336bc2e16", "DOL.GS.Keeps.GuardStaticCaster", "new mob", 252, 1, 408,
             18598, 17320, 4736, 2478),
            ("be8e2cbf-6569-4c46-a4aa-d84903a902fc", "DOL.GS.Keeps.GuardStaticCaster", "new mob", 252, 1, 408,
             17690, 19219, 4736, 519),
            ("f1f1d987-1b9a-421b-a8a1-9df423f118fe", "DOL.GS.Keeps.GuardStaticCaster", "new mob", 252, 1, 408,
             19521, 18014, 4736, 2979),
        ])
        # The central doors (000301 outer, 000302 inner) of 251 and 253, and their Hibernia portal keep doors
        # (041601, 041602), which the door check moves.
        self.assertEqual(self.before("SELECT InternalID, X, Y, Z, Heading FROM Door WHERE InternalID IN "
                                     "(251000301, 251000302, 251041601, 251041602, 253000301, 253000302, 253041601, "
                                     "253041602) ORDER BY InternalID"), [
            (251000301, 32337, 37404, 3724, 3642), (251000302, 32698, 37833, 3720, 1589),
            (251041601, 19263, 18884, 4317, 1482), (251041602, 18821, 18479, 4320, 3581),
            (253000301, 33849, 39604, 3737, 1864), (253000302, 33659, 39059, 3720, 3901),
            (253041601, 19257, 18865, 4318, 1469), (253041602, 18807, 18470, 4320, 3591),
        ])

    def test_after_the_fix(self):
        self.assertEqual(self.lines, self.LINES)
        self.assertEqual(self.after("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel, LastTimeRowUpdated "
                                    "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45, "2000-01-01 00:00:00"),
            (250, "Caledonia (Level 30-35 - RR1L9)", 30, 35, 10, NOW),
            (251, "Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6, NOW),
            (252, "Thidranki (Level 20-24 - RR1L3)", 20, 24, 4, NOW),
            (253, "Abermenai (Level 15-19 - RR1L2)", 15, 19, 3, NOW),
        ])
        self.assertEqual(self.after("SELECT ZoneID, Name, Experience FROM Zones WHERE RegionID BETWEEN 250 AND 253 "
                                    "ORDER BY ZoneID"),
                         [(250, "Caledonia", 0), (251, "Murdaigean", 0), (252, "Thidranki", 0), (253, "Abermenai", 0)])
        self.assertEqual(self.after("SELECT Description FROM Regions WHERE RegionID=250"), [("Caledonia",)])
        # The central keeps: only BaseLevel and LastTimeRowUpdated change.
        before = self.before(f"SELECT {KEEP_COLUMNS} FROM Keep WHERE {self.CENTRAL_KEEPS} ORDER BY KeepID")
        self.assertEqual(self.after(f"SELECT {KEEP_COLUMNS} FROM Keep WHERE {self.CENTRAL_KEEPS} ORDER BY KeepID"),
                         [row[:15] + (base_level,) + row[16:18] + (NOW,) + row[19:]
                          for row, base_level in zip(before, (24, 35, 29, 19))])
        self.assertEqual(self.after(f"SELECT InternalID, Health, State, LastTimeRowUpdated FROM Door WHERE {self.GATES} "
                                    "ORDER BY InternalID"), [
            (250000301, 7000, 1, NOW), (250000302, 7000, 1, NOW), (251000301, 5800, 1, NOW), (251000302, 5800, 1, NOW),
            (252000301, 4800, 1, NOW), (252000302, 4800, 1, NOW), (253000301, 3800, 1, NOW), (253000302, 3800, 1, NOW),
        ])
        for sql in (f"SELECT * FROM Keep WHERE NOT {self.CENTRAL_KEEPS} ORDER BY KeepID",
                    f"SELECT * FROM Door WHERE NOT {self.GATES} ORDER BY Door_ID",
                    f"SELECT * FROM Mob m WHERE {self.UPSTREAM_CENTRAL_GUARDS} ORDER BY Mob_ID"):
            with self.subTest(unchanged=sql):
                self.assertEqual(self.after(sql), self.before(sql))
        self.assertEqual(self.after(f"SELECT COUNT(*) FROM Mob WHERE {self.LEFTOVERS}"), [(0,)])
        mob = ", ".join(name for _, name, *_ in self.before('PRAGMA table_info("Mob")'))
        removed = self.before(f"SELECT {mob} FROM Mob WHERE {self.LEFTOVERS} ORDER BY Mob_ID")
        self.assertEqual(len(removed), 19)
        self.assertEqual(self.after(f"SELECT {mob}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v2", NOW) for row in removed])
        # 19 removed; 34 portal keep rows, 6 wall casters and a hastener added in each of 253 and 251.
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob")[0][0],
                         self.before("SELECT COUNT(*) FROM Mob")[0][0] - 19 + 2 * (34 + 6 + 1))
        self.assertEqual(self.after("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v2", NOW)])
        # Step 4: in 253 and 251, a copy of each of Thidranki's 34 portal keep rows, on the same spot.
        sources = [(m, m, None) for (m,) in self.before(f"SELECT Mob_ID FROM Mob WHERE {self.PORTAL_KEEP_GUARDS}")]
        self.assertEqual(len(sources), 34)
        for region in (253, 251):
            with self.subTest(region=region):
                self.assertEqual(*copies(self.after, region, "pk", sources, NOW))

    def test_second_run_changes_nothing(self):
        conn = sqlite3.connect(self.db)
        try:
            with conn:
                self.assertEqual(battlegrounds.apply(conn, LATER), [])
            self.assertEqual(conn.total_changes, 0)
        finally:
            conn.close()

    def test_wall_casters_and_hastener(self):
        for region in (253, 251):
            with self.subTest(region=region):
                self.assertEqual(*copies(self.after, region, "ck", casters(region) + hastener(region), NOW))

    def test_guard_levels(self):
        # The levels the server gives the central keeps' guards and lords from their Keep rows (spec 3.4):
        # 21 and 24 in Dun Abermenai, 31 and 36 in Dun Murdaigean.
        ((multiplier,),) = self.after("SELECT Value FROM ServerProperty WHERE `Key`='keep_guard_level_multiplier'")
        keeps = self.after(f"SELECT KeepID, BaseLevel, Level FROM Keep WHERE {self.CENTRAL_KEEPS}")
        self.assertEqual({keep_id: guard_levels(base_level, level, float(multiplier))
                          for keep_id, base_level, level in keeps}, GUARD_LEVELS)

    def test_every_guard_stands_in_its_keep_area(self):
        # The server gives every keep an area (keeps/KeepArea.cs), and a guard belongs to the keep whose area
        # holds it. Every new row stands in exactly one area of its region: a portal keep copy in that portal
        # keep's, a wall caster or a hastener in its central keep's. So does every one of upstream's central keep
        # guards.
        self.assertEqual(self.after(f"SELECT m.Region, k.KeepID, substr(m.Mob_ID, 11, 2), COUNT(*) FROM Mob m "
                                    f"JOIN Keep k ON {self.AREA} WHERE m.Mob_ID LIKE 'hdc-bg%' "
                                    f"GROUP BY m.Region, k.KeepID, substr(m.Mob_ID, 11, 2) ORDER BY m.Region DESC, k.KeepID"), [
            (253, 33, "ck", 7), (253, 35, "pk", 11), (253, 36, "pk", 11), (253, 37, "pk", 12),
            (251, 32, "ck", 7), (251, 41, "pk", 11), (251, 42, "pk", 11), (251, 43, "pk", 12),
        ])
        self.assertEqual(self.after(f"SELECT m.Mob_ID FROM Mob m WHERE (m.Mob_ID LIKE 'hdc-bg%' OR "
                                    f"{self.UPSTREAM_CENTRAL_GUARDS}) AND "
                                    f"(SELECT COUNT(*) FROM Keep k WHERE {self.AREA}) <> 1"), [])
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg%'"), [(82,)])

    def test_door_check(self):
        # Spec 3.2, "Check": each region's Hibernia portal keep doors, moved like the casters, land on its central
        # doors (outer 041601 on 000301, inner 041602 on 000302).
        for region in (253, 251):
            for portal, central in ((41601, 301), (41602, 302)):
                with self.subTest(region=region, door=central):
                    (door,) = self.before("SELECT X, Y, Z, Heading FROM Door WHERE InternalID=?", (region * 1000000 + portal,))
                    (target,) = self.before("SELECT X, Y, Z, Heading FROM Door WHERE InternalID=?",
                                            (region * 1000000 + central,))
                    x, y, z, heading = battlegrounds.moved(region, *door)
                    self.assertLessEqual(math.hypot(x - target[0], y - target[1]), 41)
                    self.assertLessEqual(abs(z - target[2]), 25)
                    self.assertLessEqual(abs((heading - target[3] + 2048) % 4096 - 2048), 60)

    def test_upstream_guards_confirm_the_move(self):
        # Upstream placed each central keep's guards on that keep's model from its navigation mesh, on its own.
        # Put back on the portal keep model, every guard of Dun Abermenai that stands in the keep (Z 3700 or more:
        # courtyard 3721, wall tops 4137, tower floor 4937) lands within 65 units of a guard of Dun Murdaigean of
        # the same class at the same height, so both moves fit the models upstream placed them on.
        guards = {region: [(cls, *to_model(region, x, y), z) for cls, x, y, z in self.before(
            f"SELECT ClassType, X, Y, Z FROM Mob m WHERE Region=? AND {self.UPSTREAM_CENTRAL_GUARDS}", (region,))]
            for region in (253, 251)}
        inside = [guard for guard in guards[253] if guard[3] >= 3700]
        self.assertEqual(len(inside), 23)
        for cls, x, y, z in inside:
            with self.subTest(guard=(cls, round(x), round(y), z)):
                self.assertLessEqual(min(math.hypot(x - x2, y - y2) for cls2, x2, y2, z2 in guards[251]
                                         if cls2 == cls and abs(z - z2) < 60), 65)

    def test_wall_casters_stand_on_the_walls(self):
        # Upstream's guards on the wall tops stand at 4137 to 4154, 416 above the courtyard (3721): the height of
        # the portal keep's walls above its floor (4736 against 4320). The casters stand at 4136. Where upstream
        # put a wall-top guard next to one (Dun Abermenai 2, Dun Murdaigean 3), it is within 100 units.
        expected = {253: ["2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", "62f874d0-333b-475f-a044-109cb0bd74b6"],
                    251: ["2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", "3a07da41-d088-4174-980f-1d5ad21fc334",
                          "62f874d0-333b-475f-a044-109cb0bd74b6"]}
        for region in (253, 251):
            with self.subTest(region=region):
                walls = self.before(f"SELECT X, Y, Z FROM Mob m WHERE Region=? AND {self.UPSTREAM_CENTRAL_GUARDS} "
                                    "AND Z BETWEEN 4100 AND 4200", (region,))
                self.assertEqual(min(z for _, _, z in walls), 4137)
                self.assertLessEqual(max(z for _, _, z in walls), 4154)
                spots = self.after("SELECT substr(Mob_ID, 14), X, Y, Z FROM Mob WHERE Mob_ID LIKE ? AND ClassType=?",
                                   (f"hdc-bg{region}-ck-%", battlegrounds.CASTER_CLASS))
                self.assertEqual({z for _, _, _, z in spots}, {4136})
                near = sorted(mob_id for mob_id, x, y, _ in spots
                              if min(math.hypot(x - x2, y - y2) for x2, y2, _ in walls) <= 100)
                self.assertEqual(near, expected[region])

    def test_hasteners_stand_beside_the_outer_gate(self):
        # Each central keep's hastener stands on the floor (Z 3720) beside its outer gate, on the same side. Measured
        # from the outer gate door (000301), with u along the line from the inner gate door (000302) out through
        # it and v across that line: Caer Caledon's at (-93, 233), Thidranki Faste's at (44, 206), and the moved
        # ones at (-15, 260) in Dun Murdaigean and (-28, 241) in Dun Abermenai. (The Keep rows cannot show this:
        # the four share one spot and heading, while the client draws a different model, or one turned, in each.)
        spots = {}
        for region in (250, 251, 252, 253):
            (outer,), (inner,) = (self.after("SELECT X, Y FROM Door WHERE InternalID=?", (region * 1000000 + door,))
                                  for door in (301, 302))
            ((x, y, z),) = self.after(f"SELECT m.X, m.Y, m.Z FROM Mob m JOIN Keep k ON {self.AREA} AND k.BaseLevel < 100 "
                                      "WHERE m.Region=? AND m.ClassType='DOL.GS.Keeps.FrontierHastener'", (region,))
            angle = math.atan2(outer[1] - inner[1], outer[0] - inner[0])
            dx, dy = x - outer[0], y - outer[1]
            spots[region] = (round(dx * math.cos(angle) + dy * math.sin(angle)),
                             round(dy * math.cos(angle) - dx * math.sin(angle)), z)
        self.assertEqual(spots, {250: (-93, 233, 3720), 251: (-15, 260, 3720), 252: (44, 206, 3720),
                                 253: (-28, 241, 3720)})
        # The nearest of upstream's guards is one of its gate fighters, 65 and 60 units away. (Upstream's own
        # guards at the same height stand at least 152 apart.)
        for region, distance in ((251, 65), (253, 60)):
            with self.subTest(region=region):
                ((x, y),) = self.after("SELECT X, Y FROM Mob WHERE Mob_ID=?", (f"hdc-bg{region}-ck-{HASTENERS[region][0]}",))
                guards = self.after(f"SELECT ClassType, X, Y FROM Mob m WHERE Region=? AND {self.UPSTREAM_CENTRAL_GUARDS}",
                                    (region,))
                nearest = min(guards, key=lambda guard: math.hypot(guard[1] - x, guard[2] - y))
                self.assertEqual((nearest[0], round(math.hypot(nearest[1] - x, nearest[2] - y))),
                                 ("DOL.GS.Keeps.GuardFighter", distance))

if __name__ == "__main__":
    unittest.main()
