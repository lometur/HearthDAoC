"""Quest names against the names the world spawns with (source/server/GameServer/quests/QuestsMgr/QuestNames.cs).

DataQuest matched a step's target and a quest's giver to a monster, NPC or object by its exact name, but upstream's
quest data spells many of them differently from the names they spawn with. Level 20's "Path of the Renegade" wants
"Arawnite Messenger", whose NpcTemplate 12071 ("arawnite messenger", ReplaceMobValues 1) names him at every spawn, so
his kill never counted (owner test 2026-10-10). The server now compares these names without case (QuestNames.Same;
the source checks below). With HDC_TEST_WORLD (a clean classic world), world_fixes runs on a copy and every name a
quest waits for must be spawned in its region, ignoring case, except the names listed here.
"""
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import unittest
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import world_fixes as wf  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
QUESTS = os.path.join(ROOT, "source", "server", "GameServer", "quests", "QuestsMgr")
GAME_OBJECTS = os.path.join(ROOT, "source", "server", "GameServer", "gameobjects")

# The step types that name a target (DataQuest.eStepType): Kill, Deliver, Interact, Whisper and Collect, and their
# Finish steps. Search and SearchFinish name a place.
TARGET_STEPS = {0, 1, 2, 3, 4, 5, 6, 7, 10, 11}

# Not in the world data: upstream's classic-quests.json (a server file the first start downloads, not in this
# repository) spawns each one under this exact name, in the step's region, when a player reaches the step
# (ClassicQuests.TrySpawnEvent; checked against the 0.35 file on 2026-10-10).
EVENT_SPAWNED = {
    "Anklebiter", "Arawnite Assassin", "Archdruid Cadwallen", "Cadoc", "Calikana", "Ceriallen", "Dash", "Driss",
    "Enraged Bwca", "Fasius Previlus", "Gashir", "Ghostwalker's Apprentice", "Little Wind", "Lunaris Primus Pilus",
    "Neophyte Spirit", "Sephucoth", "Shade of Harish", "Shyene Muire", "Sockburn worm", "Spicket", "Straw", "Widower",
}

# Givers nothing spawns as, under any capitals: the "XP Item" collection quests 117 ("Glowing Soul Gem") and 217
# ("Thick White Pelt"). Their NpcTemplates exist (60158101, 60158100) but no Mob row uses them. Left as they are.
MISSING_GIVERS = {"Aserod Ilonus", "Asdis"}


def spawned_names(conn):
    """{region: {lowercased name: {names}}}: the names the server gives the Mob and WorldObject rows. A Mob row whose
    NpcTemplate replaces its values spawns with the template's name, any other with its own (GameNPC.LoadTemplate);
    the server picks one of several templates under one TemplateId at random, so each of them counts."""
    templates = defaultdict(list)
    for template_id, name, replace in conn.execute("SELECT TemplateId, Name, ReplaceMobValues FROM NpcTemplate"):
        templates[template_id].append((name, bool(replace)))
    names = defaultdict(lambda: defaultdict(set))

    def add(region, name):
        if name:
            names[region][name.lower()].add(name)

    for name, template_id, region in conn.execute("SELECT Name, NPCTemplateID, Region FROM Mob"):
        found = templates.get(template_id, [])
        if any(replace for _, replace in found):
            for template_name, replace in found:
                add(region, template_name if replace else name)
        else:
            add(region, name)
    for name, region in conn.execute("SELECT Name, Region FROM WorldObject"):
        add(region, name)
    return names


def quest_names(conn):
    """(kind, quest ID, name, region) for every DataQuest giver ("giver", StartName and StartRegionID) and every step
    that names a target ("target", TargetName's "name;region"). Region 0 is any region."""
    for quest_id, start_name, start_region, types, targets in conn.execute(
            "SELECT ID, StartName, StartRegionID, StepType, TargetName FROM DataQuest"):
        if start_name:
            yield "giver", quest_id, start_name, start_region or 0
        for step_type, target in zip((types or "").split("|"), (targets or "").split("|")):
            name, _, region = target.partition(";")
            if step_type.strip().isdigit() and int(step_type) in TARGET_STEPS and name.strip():
                yield "target", quest_id, name, int(region) if region.strip().isdigit() else 0


def scan(conn):
    """{"exact" | "case" | "missing": {(kind, name, region): {quest IDs}}}: whether the names spawned in the region
    (anywhere for region 0) hold the quest's name exactly, only under other capitals, or not at all; and "wider": the
    exact ones that other capitals of the name would now match too."""
    spawned = spawned_names(conn)
    anywhere = defaultdict(set)
    for region_names in spawned.values():
        for lower, names in region_names.items():
            anywhere[lower] |= names
    result = {found: defaultdict(set) for found in ("exact", "case", "missing", "wider")}
    for kind, quest_id, name, region in quest_names(conn):
        names = (spawned.get(region, {}) if region else anywhere).get(name.lower(), set())
        found = "exact" if name in names else "case" if names else "missing"
        result[found][(kind, name, region)].add(quest_id)
        if found == "exact" and len(names) > 1:
            result["wider"][(kind, name, region)].add(quest_id)
    return result


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


class ScanTests(unittest.TestCase):
    """The scan's rules on a tiny world."""

    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        self.conn.execute("CREATE TABLE NpcTemplate (TemplateId INT, Name TEXT, ReplaceMobValues INT)")
        self.conn.execute("CREATE TABLE Mob (Name TEXT, NPCTemplateID INT, Region INT)")
        self.conn.execute("CREATE TABLE WorldObject (Name TEXT, Region INT)")
        self.conn.execute("CREATE TABLE DataQuest (ID INT, StartName TEXT, StartRegionID INT, StepType TEXT, "
                          "TargetName TEXT)")
        self.conn.executemany("INSERT INTO NpcTemplate VALUES (?, ?, ?)",
                              [(12071, "arawnite messenger", 1), (500, "Witch", 0)])
        self.conn.executemany("INSERT INTO Mob VALUES (?, ?, ?)",
                              [("Arawnite Messenger", 12071, 1), ("witch", 500, 100), ("Omis", -1, 1),
                               ("Druid", -1, 165), ("druid", -1, 165)])
        self.conn.execute("INSERT INTO WorldObject VALUES ('Old Chest', 1)")
        self.addCleanup(self.conn.close)

    def quest(self, start, types, targets):
        self.conn.execute("INSERT INTO DataQuest VALUES (1, ?, 1, ?, ?)", (start, types, targets))
        return {kind: {key: sorted(ids) for key, ids in found.items()} for kind, found in scan(self.conn).items()}

    def test_a_template_that_replaces_the_mobs_values_names_the_spawn(self):
        self.assertEqual(spawned_names(self.conn)[1]["arawnite messenger"], {"arawnite messenger"})

    def test_a_template_that_doesnt_leaves_the_mobs_name(self):
        self.assertEqual(spawned_names(self.conn)[100]["witch"], {"witch"})

    def test_steps_and_givers_are_found_exactly_by_case_or_not_at_all(self):
        found = self.quest("Omis", "0|4|8|5", "Arawnite Messenger;1|old chest;0|;0|Omis;2")
        self.assertEqual(found["exact"], {("giver", "Omis", 1): [1]})
        self.assertEqual(found["case"], {("target", "Arawnite Messenger", 1): [1], ("target", "old chest", 0): [1]})
        self.assertEqual(found["missing"], {("target", "Omis", 2): [1]})  # a name in another region doesn't count
        self.assertEqual(found["wider"], {})

    def test_an_exact_name_with_other_capitals_beside_it_is_wider(self):
        found = self.quest("Omis", "1", "Druid;165")
        self.assertEqual(found["exact"], {("giver", "Omis", 1): [1], ("target", "Druid", 165): [1]})
        self.assertEqual(found["wider"], {("target", "Druid", 165): [1]})


class SourceTests(unittest.TestCase):
    """Every compare of a quest's target or giver name with an object's name goes through QuestNames.Same."""

    def test_the_helper_compares_ordinal_without_case(self):
        text = read(os.path.join(QUESTS, "QuestNames.cs"))
        self.assertIn("string.Equals(questName, objectName, StringComparison.OrdinalIgnoreCase)", text)

    def test_data_quest_has_no_exact_name_compare_left(self):
        text = read(os.path.join(QUESTS, "DataQuest.cs"))
        self.assertIsNone(re.search(r"TargetName\s*[!=]=|[!=]=\s*TargetName|StartName\s*[!=]=", text))
        self.assertEqual(len(re.findall(r"QuestNames\.Same\(TargetName, (?:npc|obj|living)\.Name\)", text)), 5)

    def test_the_quest_indicator_and_the_giver_use_it(self):
        self.assertIn("QuestNames.Same(dataQuest.TargetName, Name)", read(os.path.join(GAME_OBJECTS, "GameNPC.cs")))
        giver = read(os.path.join(GAME_OBJECTS, "GameObject.cs"))
        self.assertIn("if (!QuestNames.Same(quest.StartName, obj.Name))", giver)
        self.assertNotIn("quest.StartName != obj.Name", giver)

    def test_the_files_keep_their_endings(self):
        for path in (os.path.join(QUESTS, "DataQuest.cs"), os.path.join(GAME_OBJECTS, "GameNPC.cs"),
                     os.path.join(GAME_OBJECTS, "GameObject.cs")):
            with open(path, "rb") as f:
                raw = f.read()
            self.assertEqual(raw.count(b"\r\n"), raw.count(b"\n"), path)
        with open(os.path.join(QUESTS, "DataQuest.cs"), "rb") as f:
            self.assertTrue(f.read().startswith(b"\xef\xbb\xbf"))


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class WorldTests(unittest.TestCase):
    """The scan on a copy of a clean classic world, after world_fixes (the epic chains add quests and a camp)."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, db)
        wf.apply(db)
        conn = sqlite3.connect(db)
        try:
            cls.found = scan(conn)
        finally:
            conn.close()

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def missing(self, kind):
        return sorted({name for (k, name, _), _ in self.found["missing"].items() if k == kind})

    def test_every_step_target_is_spawned_ignoring_case(self):
        self.assertEqual([name for name in self.missing("target") if name not in EVENT_SPAWNED], [])

    def test_every_giver_is_spawned_ignoring_case(self):
        self.assertEqual([name for name in self.missing("giver") if name not in MISSING_GIVERS], [])

    def test_no_quest_now_matches_a_second_creature(self):
        # No region holds a quest's name both exactly and under other capitals, so ignoring case only finds the names
        # an exact compare missed.
        self.assertEqual(sorted(self.found["wider"]), [])

    def test_the_names_the_owner_found_exist_only_under_other_capitals(self):
        case = set(self.found["case"])
        for key in [("target", "Arawnite Messenger", 1), ("target", "Cornwall hunter", 1),
                    ("target", "Fanged Sinach", 200), ("target", "Elder Tidal Sheerie", 200),
                    ("target", "Dverge Smith", 100), ("target", "Isolationist Courier", 1),
                    ("target", "Ellyll Seer", 1), ("target", "witch", 100), ("target", "Shale Golem Emissary", 151),
                    ("giver", "Albion Runner", 10)]:
            with self.subTest(key):
                self.assertIn(key, case)


if __name__ == "__main__":
    unittest.main()
