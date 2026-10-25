"""Mulgrut Maggot back in the Camelot Hills cemetery, walking to the tall stone with his escort (2026-10-22): level 5
"Cleric Mulgrut" (DataQuest 20207). deploy/bin/mob_fixes.json moves him to upstream's own archived spot in the cemetery
and makes his template attack on sight; deploy/bin/quest_camps.json adds his escort (two decayed zombies and a putrid
zombie that follow him, QuestFellowBrain) and his walk (a path around the cemetery, out to the menhir on the hill
north-east and back); deploy/hearthdaoc-quests.json lists the escort as his fellows and as quest monsters, and moves the
quest's map marker with him.

The world tests run world_fixes on a copy of a clean classic world (HDC_TEST_WORLD) and read upstream's
classic-quests.json (HDC_CLASSIC_QUESTS) merged with the fork's file, as ClassicQuests merges them.
"""
import json
import math
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(DEPLOY, "bin"))

import world_fixes as wf  # noqa: E402
from tests.test_epic_followups import merged_quests, point  # noqa: E402
from tests.test_quest_reachability import TEST_WORLD, UPSTREAM_QUESTS  # noqa: E402

MULGRUT = "d5913594-869f-5d90-bcea-fce428f63926"
ARCHIVED_TWIN = "38199e50-f39f-4ad1-8710-f9c6a7c38c40"  # upstream's own archived Mulgrut, on the spot he moves to
TEMPLATE = 12373  # his row's only
SPAWN = (1, 565958, 528471, 2168)
OLD = (1, 570194, 524831, 2619)  # upstream's row, still on the wooded hill 5.6k north-east
PATH = "HDC_Mulgrut_CemeteryToMenhir"
GRAVEYARD = (566400, 528384)  # the graveyard fixture, loc 17536, 45056
MENHIR = (574208, 519680)  # the tall stone, loc 25344, 36352
ESCORT = {  # Mob_ID: (name, level, size, speed, melee damage type, its template's articles)
    "f4945947-03cc-5cd1-8969-5ecd228ac713": ("decayed zombie", 3, 50, 175, 1, ("the", "The")),
    "230b08ea-77df-5256-96f6-997db4c9f1cf": ("decayed zombie", 3, 50, 175, 1, ("the", "The")),
    "ccc4a788-1356-5a60-86ac-ff5c1d9709e4": ("putrid zombie", 4, 45, 180, 2, (None, None)),
}
DAILY_QUEST = 20207
HANDOFF = os.path.join(DEPLOY, "HANDOFF.md")
FORK = os.path.join(os.path.dirname(DEPLOY), "docs", "fork", "FORK.md")
QUEST_CAMPS = os.path.join(DEPLOY, "bin", "quest_camps.json")
BOT_GOALS = os.path.join(os.path.dirname(DEPLOY), "source", "server", "GameServer", "bots", "autonomous", "data",
                         "capnbry_classic_si_goals.json")
CAMELOT_HILLS = (67 * 8192, 59 * 8192)  # zone 0's offset in region 1 (Zones: OffsetX 67, OffsetY 59)
# HANDOFF.md's way to stop his walk, with the server stopped (StopTheWalkDocsTests).
STOP_THE_WALK = f"UPDATE Mob SET PathID='' WHERE Mob_ID='{MULGRUT}'"


def distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def off_line(p, a=GRAVEYARD, b=MENHIR):
    """How far p is from the straight line from a to b."""
    return abs((p[0] - a[0]) * (b[1] - a[1]) - (p[1] - a[1]) * (b[0] - a[0])) / distance(a, b)


@unittest.skipUnless(TEST_WORLD and UPSTREAM_QUESTS,
                     "needs HDC_TEST_WORLD (a clean classic world) and upstream's classic-quests.json (HDC_CLASSIC_QUESTS)")
class MulgrutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.changes = wf.apply(cls.db)
        cls.conn = sqlite3.connect(cls.db)
        cls.quests, cls.extra = merged_quests()

    @classmethod
    def tearDownClass(cls):
        cls.conn.close()
        cls.tmp.cleanup()

    def mob(self, mob_id, *columns):
        return self.conn.execute(f"SELECT {', '.join(columns)} FROM Mob WHERE Mob_ID=?", (mob_id,)).fetchone()

    def points(self):
        return self.conn.execute("SELECT Step, X, Y, Z, MaxSpeed, WaitTime FROM PathPoints WHERE PathID=? ORDER BY Step",
                                 (PATH,)).fetchall()

    def others_near(self, p, reach):
        """(distance, Z) of every monster spawn, live or in upstream's archive (which `hdc spawns restore` brings back),
        within reach of p in region 1: Mulgrut, his archived twin and his escort left out."""
        mine = (MULGRUT, ARCHIVED_TWIN, *ESCORT)
        rows = self.conn.execute(
            "SELECT Mob_ID, X, Y, Z FROM Mob WHERE Region=1 AND abs(X-?)<=? AND abs(Y-?)<=? UNION "
            "SELECT Mob_ID, X, Y, Z FROM offline_classic165_removed_mobs WHERE Region=1 AND abs(X-?)<=? AND abs(Y-?)<=?",
            (p[0], reach, p[1], reach) * 2).fetchall()
        return sorted((distance(p, (x, y)), z) for mob_id, x, y, z in rows if mob_id not in mine)

    # A: in the cemetery, attacking on sight
    def test_he_stands_in_the_cemetery_on_his_walk(self):
        self.assertEqual(self.mob(MULGRUT, "Region", "X", "Y", "Z", "Heading", "RoamingRange", "PathID", "Speed"),
                         (*SPAWN, 3043, 500, PATH, 185))
        self.assertEqual(self.conn.execute("SELECT X, Y, Z, Heading FROM offline_classic165_removed_mobs WHERE Mob_ID=?",
                                           (ARCHIVED_TWIN,)).fetchone(), (*SPAWN[1:], 3043))

    def test_he_and_his_template_attack_on_sight(self):
        self.assertEqual(self.mob(MULGRUT, "NPCTemplateID", "FactionID"), (TEMPLATE, 63))
        self.assertEqual(self.conn.execute("SELECT AggroLevel, AggroRange, ReplaceMobValues FROM NpcTemplate "
                                           "WHERE TemplateId=?", (TEMPLATE,)).fetchall(), [(51, 500, 1)])
        self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM Mob WHERE NPCTemplateID=?", (TEMPLATE,)).fetchone(), (1,))

    def test_he_is_the_only_mulgrut_maggot_in_the_world(self):
        # His fellows find him by name, the nearest by spawn point (QuestFellowBrain): his archived twin stays archived.
        self.assertEqual(self.conn.execute("SELECT Mob_ID FROM Mob WHERE Name='Mulgrut Maggot'").fetchall(), [(MULGRUT,)])
        self.assertIsNotNone(self.conn.execute("SELECT 1 FROM offline_classic165_removed_mobs WHERE Mob_ID=?",
                                               (ARCHIVED_TWIN,)).fetchone())

    # B: the walk
    def test_his_walk_goes_out_and_back(self):
        self.assertEqual(self.conn.execute("SELECT PathType, RegionID FROM Path WHERE PathID=?", (PATH,)).fetchall(), [(2, 1)])
        points = self.points()
        self.assertEqual([p[0] for p in points], list(range(1, 13)))
        self.assertEqual({p[4] for p in points}, {70})  # a walk (DEFAULT_WALK_SPEED); his own 185 stays for fights

    def test_he_wanders_the_cemetery_before_and_after_each_walk(self):
        # Five points around his spawn, 20-40 s at each (WaitTime in tenths of a second), the first his spawn.
        cemetery = self.points()[:5]
        self.assertEqual(cemetery[0][1:4], SPAWN[1:])
        for step, x, y, _, _, wait in cemetery:
            with self.subTest(step=step):
                self.assertLessEqual(distance((x, y), SPAWN[1:]), 520)
                self.assertTrue(200 <= wait <= 400, wait)
        legs = [distance(a[1:3], b[1:3]) for a, b in zip(cemetery, cemetery[1:])]
        self.assertGreaterEqual(min(legs), 400)

    def test_he_walks_on_without_stopping_and_waits_two_minutes_by_the_stone(self):
        points = self.points()
        self.assertEqual([p[5] for p in points[5:]], [0] * 6 + [1200])
        self.assertLessEqual(distance(points[-1][1:3], MENHIR), 300)

    def test_his_waiting_points_keep_clear_of_other_monsters_spawns(self):
        # Each waiting point but his spawn stands 100-150 from the nearest other monster's spawn spot, at its height.
        for step, x, y, z, _, wait in self.points()[1:]:
            if not wait:
                continue
            with self.subTest(step=step):
                (nearest, height), *rest = self.others_near((x, y), 300)
                self.assertTrue(100 <= nearest <= 150, nearest)
                self.assertEqual(z, height)

    def test_his_walk_follows_the_line_to_the_stone(self):
        points = self.points()
        for a, b in zip(points, points[1:]):
            with self.subTest(leg=(a[0], b[0])):
                self.assertLessEqual(distance(a[1:3], b[1:3]), 2500)
        for step, x, y, *_ in points:
            with self.subTest(step=step):
                self.assertLessEqual(off_line((x, y)), 1000)

    # B: the escort
    def test_his_escort_follows_him_and_attacks_on_sight(self):
        respawn = self.mob(MULGRUT, "RespawnInterval")[0]
        self.assertEqual(respawn, 300)
        for mob_id, (name, level, size, speed, damage, articles) in ESCORT.items():
            with self.subTest(mob_id):
                self.assertEqual(
                    self.mob(mob_id, "Name", "Level", "Size", "Speed", "MeleeDamageType", "ExamineArticle", "MessageArticle",
                             "NPCTemplateID", "Brain", "AggroLevel", "AggroRange", "RespawnInterval", "RoamingRange",
                             "PackageID", "FactionID", "Realm", "Model", "Region", "Z", "Heading", "Gender", "COALESCE(PathID, '')"),
                    (name, level, size, speed, damage, *articles, -1, "DOL.AI.Brain.QuestFellowBrain", 51, 500, respawn, 0,
                     "ClassicQuest", 63, 0, 110, 1, 2168, 3043, 0, ""))

    def test_his_escort_spawns_beside_him_clear_of_other_monsters(self):
        spots = {mob_id: self.mob(mob_id, "X", "Y") for mob_id in ESCORT}
        for mob_id, spot in spots.items():
            with self.subTest(mob_id):
                self.assertLessEqual(distance(spot, SPAWN[1:]), 200)
                self.assertGreaterEqual(self.others_near(spot, 300)[0][0], 100)
                self.assertGreaterEqual(min(distance(spot, other) for o, other in spots.items() if o != mob_id), 80)

    def test_his_escort_are_his_fellows_and_quest_monsters(self):
        self.assertEqual(self.extra["Fellows"], {mob_id: "Mulgrut Maggot" for mob_id in ESCORT})
        self.assertTrue(set(ESCORT) <= set(self.extra["QuestMonsterIds"]))

    # The quest
    def test_the_quests_marker_moves_with_him(self):
        self.assertEqual(point(self.quests[DAILY_QUEST]["Steps"][1]["Marker"]), SPAWN)
        markers = [point(step["Marker"]) for info in self.quests.values() for step in info.get("Steps") or []
                   if step and step.get("Marker")]
        self.assertNotIn(OLD, markers)

    def test_a_second_start_adds_nothing(self):
        self.assertIn("Quest camps:", " ".join(self.changes))
        again = wf.apply(self.db)
        self.assertFalse([line for line in again if line.startswith("Quest camps:")], again)
        self.assertFalse([line for line in again if "Mulgrut" in line], again)

    def next_start(self, sql, *params):
        """The start log after `sql` on a copy of this world (the server stopped), and his row then."""
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "world.db")
            shutil.copyfile(self.db, db)
            conn = sqlite3.connect(db)
            with conn:
                conn.execute(sql, params)
            conn.close()
            lines = wf.apply(db)
            conn = sqlite3.connect(db)
            try:
                row = conn.execute("SELECT Region, X, Y, Z, Heading, RoamingRange, COALESCE(PathID, '') FROM Mob "
                                   "WHERE Mob_ID=?", (MULGRUT,)).fetchone()
            finally:
                conn.close()
        return lines, row

    def test_clearing_his_path_id_stops_his_walk_and_keeps_the_start_quiet(self):
        # The HANDOFF's way: his row keeps the file's values, and the walk is not put back (the camp's marker).
        lines, row = self.next_start(STOP_THE_WALK)
        self.assertFalse([line for line in lines if "Mulgrut" in line], lines)
        self.assertEqual(row, (*SPAWN, 3043, 500, ""))

    def test_mob_path_while_he_walks_would_move_his_spawn_for_good(self):
        # Why not `/mob path` with no name: it saves his whole row (GameNPC.SaveIntoDatabase), with where he stands
        # and the way he faces, so done by the stone it makes that his spawn, which mob_fixes then keeps as the
        # owner's and names at every start.
        lines, row = self.next_start("UPDATE Mob SET PathID='', X=573930, Y=519750, Z=2571, Heading=1200 "
                                     "WHERE Mob_ID=?", MULGRUT)
        self.assertEqual(row, (1, 573930, 519750, 2571, 1200, 500, ""))
        self.assertTrue([line for line in lines if "left as they are" in line and MULGRUT in line], lines)


class StopTheWalkDocsTests(unittest.TestCase):
    """Where the fork tells the owner how to stop Mulgrut's walk, it clears his PathID in the world database with the
    server stopped, not with `/mob path` and no name, which also saves his live position and heading as his spawn
    (the review, 2026-10-25)."""

    def test_the_handoff_clears_his_path_id_with_the_server_stopped(self):
        with open(HANDOFF, encoding="utf-8-sig") as f:
            told = [p for p in re.split(r"\n\s*\n", f.read()) if "stop his walk" in p.lower()]
        self.assertEqual(len(told), 1, told)
        self.assertIn(STOP_THE_WALK, told[0])
        self.assertLess(told[0].index("./hdc stop"), told[0].index(STOP_THE_WALK))
        self.assertLess(told[0].index(STOP_THE_WALK), told[0].index("./hdc up"))
        self.assertNotRegex(told[0], r"(?i)type `/mob path`")


class BotCampsDocsTests(unittest.TestCase):
    """Gamebots don't hunt Mulgrut or his escort (quest monsters), but the group attacks every bot it doesn't see as
    gray (faction 63's default standing is aggressive: StandardMobBrain.CanAggroTarget), and the bots' goal catalog
    has low-level camps within his aggro range (500) of his walk's points. A bot camping there gets jumped, fights back
    and may kill him: FORK.md says so (the review, 2026-10-25)."""

    def test_low_level_bot_camps_lie_within_his_reach_and_fork_md_says_so(self):
        with open(QUEST_CAMPS, encoding="utf-8") as f:
            [walk] = [camp for camp in json.load(f)["camps"] if camp.get("path") == PATH]
        with open(BOT_GOALS, encoding="utf-8") as f:
            goals = json.load(f)["goals"]
        near = sorted({(goal["name"], level) for goal in goals if goal["zone_id"] == 0
                       for level in goal["levels"]
                       if min(distance((goal["local_x"] + CAMELOT_HILLS[0], goal["local_y"] + CAMELOT_HILLS[1]), p)
                              for p in walk["points"]) <= 500})
        self.assertEqual(near, [("decayed zombie", 3), ("skeleton", 2)])
        with open(FORK, encoding="utf-8") as f:
            [row] = [line for line in f if PATH in line and line.startswith("| Quest camps")]
        for words in ("gamebots", "skeletons", "decayed zombies", "may kill him"):
            self.assertIn(words, row)


if __name__ == "__main__":
    unittest.main()
