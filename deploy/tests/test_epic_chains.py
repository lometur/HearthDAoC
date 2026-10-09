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
import unittest.mock

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)
import epic_chains  # noqa: E402

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


CLASSIC = "DOL.GS.Quests.ClassicQuestStep"
NOW = "2026-10-09 12:00:00"
# The clean 0.35 world's summary line. Tasks 6 and 7 update it as they add steps.
SUMMARY = ("Epic chains: Guild of Shadows 60 links, 60 XP and coin, 4 Supply Runs closed, 2 rewards and 7 texts fixed; "
           "87 other links; 41 items added, 36 item fixes; 0 level-50 quests, Lord Elidyn's camp 0 restored; "
           "Shadows_50: 0 finished carried, 0 removed, 0 epic vests recharged")
# Every other line's steps pinned on the clean 0.35 world: (name, level) -> rows (spec 2.5).
EXPECTED_PINS = {
    ("A War of Old", 20): 2, ("A War of Old", 25): 2, ("A War of Old", 30): 2,
    ("An End to the Daggers", 43): 5, ("An End to the Daggers", 45): 5, ("An End to the Daggers", 48): 1,
    ("Feast of the Decadent", 45): 4, ("Feast of the Decadent", 48): 1,
    ("Hands Of Fate", 30): 4,
    ("Last Heir", 45): 5, ("Last Heir", 47): 5,
    ("Legend of the Lake", 20): 4, ("Legend of the Lake", 25): 4,
    ("Passage to Eternity", 45): 2, ("Passage to Eternity", 48): 2,
    ("Regal Nobility", 30): 1,
    ("Saving the Clan", 45): 3, ("Saving the Clan", 48): 1,
    ("Symbol of the Broken", 45): 3, ("Symbol of the Broken", 48): 3,
    ("Thane's Blood", 30): 6,
    ("The Desire of a God", 45): 2, ("The Desire of a God", 48): 1,
    ("The Moonstone Twin", 45): 4, ("The Moonstone Twin", 47): 1,
    ("The Red Dagger", 20): 5, ("The Red Dagger", 25): 6,
    ("The War Continues", 40): 1,
    ("Unnatural Powers", 47): 2,
}
FINALES = {"Feast of the Decadent", "Passage to Eternity", "Symbol of the Broken", "An End to the Daggers",
           "Saving the Clan", "The Desire of a God", "Last Heir", "The Moonstone Twin", "Lord of Deceit"}
# Upstream's level-40 lists (classic-quests.json, quests 20169-20173), less the two the fix drops for the Reaver.
WEAPON_CHOICES = {
    "Infiltrator": ["cq_alb_crackling_impaler", "cq_alb_death_s_touch", "cq_alb_death_dancer", "cq_alb_spark_of_midnight"],
    "Mercenary": ["cq_alb_crackling_impaler", "cq_alb_death_s_touch", "cq_alb_glitter", "cq_alb_spark", "cq_alb_dazzle",
                  "cq_alb_arcing_bludgeoner"],
    "Cabalist": ["cq_alb_staff_of_eternal_lifeforce", "cq_alb_staff_of_earth_channeling", "cq_alb_staff_of_spirit_consumption"],
    "Necromancer": ["cq_alb_staff_of_cursed_bondage", "cq_alb_staff_of_clouded_vision", "cq_alb_staff_of_ceaseless_agony"],
    "Reaver": ["cq_alb_blood_encrusted_whip", "cq_alb_sap_of_lost_will", "cq_alb_bloodletter", "cq_alb_flail_of_fallen_graces"],
}


def rows(conn, sql, params=()):
    """Query results as dicts."""
    cur = conn.execute(sql, params)
    columns = [d[0] for d in cur.description]
    return [dict(zip(columns, r)) for r in cur.fetchall()]


def entry_met(entry, finished_names, finished_ids, active_ids):
    """DataQuest's rule for one dependency entry (QuestDependencies.IsMet in C#), to walk the chains here."""
    text = entry.strip()
    if not text.startswith(("#", "!#")):
        return entry.lower() in {n.lower() for n in finished_names}
    closes = text.startswith("!#")
    parts = text[2 if closes else 1:].split("/")
    if not all(p.strip().isdigit() and int(p) > 0 for p in parts):
        return False
    ids = {int(p) for p in parts}
    if closes:
        return not ids & (set(finished_ids) | set(active_ids))
    return bool(ids & set(finished_ids))


def can_take(row, level, finished_ids, active_ids, names):
    """DataQuest.CheckQuestQualification for a chain row: the level range, not finished or active (MaxCount 1), and
    every dependency entry met. names: quest ID -> name, for name entries."""
    if not row["MinLevel"] <= level <= row["MaxLevel"] or row["ID"] in finished_ids or row["ID"] in active_ids:
        return False
    finished_names = [names[i] for i in finished_ids]
    return all(entry_met(e, finished_names, finished_ids, active_ids)
               for e in (row["QuestDependency"] or "").split("|") if e)


class DataQuestSourceTests(unittest.TestCase):
    def test_dataquest_reads_dependencies_only_when_offering(self):
        # The closed Supply Runs need themselves finished, which closes them for offers only
        # (CheckQuestQualification), so a character already on one can finish it (spec 3.4, step 3).
        text = read(os.path.join(GAME_SERVER, "quests", "QuestsMgr", "DataQuest.cs"))
        methods = set()
        for m in re.finditer(r"m_questDependencies", text):
            line = text[text.rindex("\n", 0, m.start()) + 1:text.index("\n", m.start())]
            if "new List<string>()" in line:
                continue  # the field itself
            heads = list(re.finditer(r"\n\t\t(?:public|protected|private|internal)[^\n(]*?\b(\w+)\s*\(", text[:m.start()]))
            methods.add(heads[-1].group(1))
        self.assertEqual(methods, {"CheckQuestQualification", "ParseQuestData"})


class EpicDataTests(unittest.TestCase):
    """deploy/bin/epic_chains_data.json is consistent with itself and the spec (no world needed)."""

    def setUp(self):
        self.data = epic_chains.load_data()

    def test_five_classes_in_the_spec_order(self):
        self.assertEqual(self.data["classes"], {"Infiltrator": 9, "Mercenary": 11, "Cabalist": 13, "Necromancer": 12,
                                                "Reaver": 19})

    def test_every_step_has_one_quest_per_class_and_links_to_known_steps(self):
        steps = self.data["steps"]
        self.assertEqual(list(steps), ["7", "7closed", "7si", "11", "11si", "15", "20", "25", "30", "40", "43", "45",
                                       "48", "50"])
        for key, step in steps.items():
            with self.subTest(key):
                self.assertEqual(len(step["ids"]), 5)
                for link in step.get("links", []):
                    for target in link.lstrip("!").split("/"):
                        self.assertIn(target, steps)
        self.assertEqual(steps["50"]["ids"], LEVEL_50_IDS)
        self.assertEqual([i is None for i in steps["7closed"]["ids"]], [False, False, False, True, False])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class EpicWorldTests(unittest.TestCase):
    """The fix on a copy of the clean classic world, applied once (spec 2 and 3.4)."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.conn = sqlite3.connect(cls.db)
        cls.before = {r["ID"]: r for r in rows(cls.conn, "SELECT * FROM DataQuest")}
        cls.data = epic_chains.load_data()
        cls.result = epic_chains.apply(cls.conn, NOW)
        cls.conn.commit()
        cls.after = {r["ID"]: r for r in rows(cls.conn, "SELECT * FROM DataQuest")}
        cls.names = {i: r["Name"] for i, r in cls.after.items()}

    @classmethod
    def tearDownClass(cls):
        cls.conn.close()
        cls.tmp.cleanup()

    def open_steps(self, col, finished=(), active=()):
        """The chain steps (data keys) a class (data column) can take at level 50."""
        steps = self.data["steps"]
        finished_ids = {steps[k]["ids"][col] for k in finished}
        active_ids = {steps[k]["ids"][col] for k in active}
        return [key for key, step in steps.items()
                if step["ids"][col] in self.after
                and can_take(self.after[step["ids"][col]], 50, finished_ids, active_ids, self.names)]

    def test_the_summary_line(self):
        self.assertEqual(self.result, [SUMMARY])
        self.assertEqual(self.conn.execute("SELECT FixId FROM fork_world_fixes").fetchall(), [("epic-chains-v1",)])

    def test_the_classic_route_opens_one_step_at_a_time(self):
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col), ["7", "7si"])
                done = []
                for key, opened in (("7", ["11", "11si"]), ("11", ["15"]), ("15", ["20"]), ("20", ["25"]),
                                    ("25", ["30"]), ("30", ["40"]), ("40", ["43"]), ("43", ["45"]), ("45", ["48"])):
                    done.append(key)
                    self.assertEqual(self.open_steps(col, done), opened, done)

    def test_the_shrouded_isles_route_reaches_15(self):
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, ["7si"]), ["11", "11si"])
                self.assertEqual(self.open_steps(col, ["7si", "11si"]), ["15"])
                self.assertEqual(self.open_steps(col, ["7", "11si"]), ["15"])

    def test_one_version_closes_the_other(self):
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, active=["7"]), [])
                self.assertEqual(self.open_steps(col, active=["7si"]), [])
                self.assertEqual(self.open_steps(col, ["7"], active=["11si"]), [])

    def test_the_closed_supply_runs_open_for_no_one_and_count_as_7(self):
        for col, name in enumerate(self.data["classes"]):
            closed = self.data["steps"]["7closed"]["ids"][col]
            if closed is None:
                continue  # the Necromancer has one Supply Run
            with self.subTest(name):
                self.assertNotIn("7closed", self.open_steps(col))
                self.assertEqual(self.open_steps(col, ["7closed"]), ["11", "11si"])
                self.assertEqual(self.after[closed]["QuestDependency"], f"#{closed}")
                same = lambda r: {k: v for k, v in r.items() if k not in ("QuestDependency", "LastTimeRowUpdated")}
                self.assertEqual(same(self.after[closed]), same(self.before[closed]))

    def test_30_cannot_be_skipped_and_48_needs_45(self):
        upto = ["7", "11", "15", "20", "25"]
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, upto), ["30"])
                self.assertEqual(self.open_steps(col, upto + ["30", "40", "43"]), ["45"])

    def test_every_other_line_is_pinned_to_its_step_before(self):
        gos = {i for step in self.data["steps"].values() for i in step["ids"] if i is not None}
        counts = {}
        for qid, old_row in self.before.items():
            row = self.after[qid]
            if qid in gos or row["QuestDependency"] == old_row["QuestDependency"]:
                continue
            key = (row["Name"], row["MinLevel"])
            counts[key] = counts.get(key, 0) + 1
            old = [e for e in (old_row["QuestDependency"] or "").split("|") if e]
            new = [e for e in row["QuestDependency"].split("|") if e]
            self.assertEqual(len(old), len(new), qid)
            for was, now in zip(old, new):
                if now == was:
                    continue
                targets = [self.after[int(t)] for t in now[1:].split("/")]
                self.assertEqual({t["Name"].lower() for t in targets}, {was.lower()}, qid)
                self.assertEqual(len({t["MinLevel"] for t in targets}), 1, qid)
                self.assertLess(targets[0]["MinLevel"], row["MinLevel"], qid)
        self.assertEqual(counts, EXPECTED_PINS)

    def test_finale_parts_need_the_part_before(self):
        for row in self.after.values():
            if row["Name"] not in FINALES or row["MinLevel"] <= 43 or CLASSIC not in (row["ClassType"] or ""):
                continue
            mine = epic_chains._classes(row["AllowedClasses"])
            lower = sorted({r["MinLevel"] for r in self.after.values()
                            if r["Name"] == row["Name"] and r["MinLevel"] < row["MinLevel"]
                            and epic_chains._shares(mine, epic_chains._classes(r["AllowedClasses"]))})
            with self.subTest(row["ID"]):
                if not lower:  # no earlier part for its classes (An End to the Daggers 45 for class 34): as upstream has it
                    self.assertEqual(row["QuestDependency"], self.before[row["ID"]]["QuestDependency"])
                    continue
                entries = row["QuestDependency"].split("|")
                self.assertEqual(len(entries), 1)
                self.assertTrue(entries[0].startswith("#"))
                self.assertEqual({self.after[int(t)]["MinLevel"] for t in entries[0][1:].split("/")}, {lower[-1]})

    def test_xp_and_coin_are_in_the_last_stage(self):
        expected = {"7": ("0|0|5500", "0|0|700"), "7si": ("0|0|0|0|5500", "0|0|0|0|700"),
                    "11": ("0|0|0|0|0|95000", "0|0|0|0|0|1100"), "11si": ("0|0|0|95000", "0|0|600|600"),
                    "15": ("0|0|0|180000", "0|0|0|1500"), "20": ("0|0|0|0|1230000", "0|0|0|0|2000"),
                    "25": ("0|0|0|5300000", "0|0|0|2500"), "30": ("0|0|21000000", "0|0|3000"),
                    "40": ("0|0|0|0|0|0|0|430000000", "0|0|0|0|0|0|0|4000"),
                    "43": ("0|0|0|0|0|0|0|1080000000", "0|0|0|0|0|0|0|0"),
                    "45": ("0|0|0|0|0|0|1560000000", "0|0|0|0|0|0|4500"), "48": ("0|0|2700000000", "0|0|4800")}
        for key, (xp, money) in expected.items():
            for qid in self.data["steps"][key]["ids"]:
                with self.subTest(key=key, quest=qid):
                    self.assertEqual((self.after[qid]["RewardXP"], self.after[qid]["RewardMoney"]), (xp, money))
        for qid in [i for i in self.data["steps"]["7closed"]["ids"] if i is not None]:
            self.assertEqual(self.after[qid]["RewardXP"], "0|0|3300")  # the closed versions keep upstream's values

    def test_one_necromancer_reward_per_version_of_11(self):
        self.assertEqual(self.after[20159]["FinalRewardItemTemplates"], "cq_alb_flayed_skin_necklace")
        self.assertEqual(self.after[20471]["FinalRewardItemTemplates"], "cq_alb_arawn_s_beads")

    def test_the_reaver_list_and_the_level_30_speech(self):
        reaver = self.after[20172]
        self.assertTrue(reaver["StepText"].endswith(
            "(Blood Encrusted Whip, Sap of Lost Will, Bloodletter, Flail of Fallen Graces)."))
        self.assertTrue(reaver["SourceText"].endswith(
            "[Blood Encrusted Whip], [Sap of Lost Will], [Bloodletter], [Flail of Fallen Graces]."))
        for qid in range(21489, 21495):
            with self.subTest(qid):
                text = self.after[qid]["SourceText"]
                self.assertIn("Let this reward be the start", text)
                self.assertIn("call upon the Guild of Shadows and thier most cunning <Class>!", text)
                self.assertNotIn("(", text)

    def test_nothing_else_in_the_quests_changes(self):
        touched = {"QuestDependency", "RewardXP", "RewardMoney", "FinalRewardItemTemplates", "StepText", "SourceText",
                   "LastTimeRowUpdated"}
        for qid, old in self.before.items():
            new = self.after[qid]
            for column, value in old.items():
                if column not in touched:
                    self.assertEqual(new[column], value, (qid, column))

    def test_every_reward_and_weapon_choice_exists(self):
        def exists(item):
            return self.conn.execute("SELECT 1 FROM ItemTemplate WHERE Id_nb=?", (item,)).fetchone() is not None

        for key, step in self.data["steps"].items():
            if step.get("new"):
                continue
            for qid in step["ids"]:
                if qid is None:
                    continue
                for item in filter(None, (self.after[qid]["FinalRewardItemTemplates"] or "").split("|")):
                    with self.subTest(step=key, quest=qid, item=item):
                        self.assertTrue(exists(item))
        for name, items in WEAPON_CHOICES.items():
            for item in items:
                with self.subTest(name=name, item=item):
                    self.assertTrue(exists(item))

    def test_the_items_added_are_the_data_rows(self):
        import uuid
        for item in self.data["items"]:
            with self.subTest(item["Id_nb"]):
                row = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb=?", (item["Id_nb"],))[0]
                self.assertEqual({k: row[k] for k in item}, item)
                self.assertEqual(row["ItemTemplate_ID"], str(uuid.uuid5(uuid.NAMESPACE_URL, "hearthdaoc:item:" + item["Id_nb"])))
                self.assertEqual(row["LastTimeRowUpdated"], NOW)

    def test_the_item_fixes(self):
        # Every fix applied as the data says (the level-50 armour, upstream's broken rewards, the class locks).
        for fix in self.data["item_fixes"]:
            with self.subTest(fix["Id_nb"]):
                row = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb=?", (fix["Id_nb"],))[0]
                self.assertEqual({k: str(row[k]) for k in fix["set"]}, {k: str(v) for k, v in fix["set"].items()})
        vest = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb='MercenaryEpicVest'")[0]
        self.assertEqual((vest["Name"], vest["AllowedClasses"], vest["SpellID"], vest["Charges"]),
                         ("Hauberk of the Shadowy Embers", "11", 31131, 3))
        choker = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb='cq_alb_choker_of_dark_deeds'")[0]
        self.assertEqual(sorted((choker[f"Bonus{i}Type"], choker[f"Bonus{i}"]) for i in range(1, 5)),
                         [(5, 4), (9, 3), (11, 1), (26, 2)])  # Int 4, Power 3, Body 1%, Death Servant +2

    def test_every_reward_is_locked_to_the_class_it_is_for(self):
        classes = self.data["classes"]
        given = {}
        for key, step in self.data["steps"].items():
            if step.get("new"):
                continue
            for qid, class_id in zip(step["ids"], self.data["classes"].values()):
                if qid is not None:
                    for item in filter(None, (self.after[qid]["FinalRewardItemTemplates"] or "").split("|")):
                        given.setdefault(item, set()).add(class_id)
        for name, items in WEAPON_CHOICES.items():
            for item in items:
                given.setdefault(item, set()).add(classes[name])
        self.assertEqual(len(given), 68)
        for item, class_ids in given.items():
            with self.subTest(item):
                allowed = self.conn.execute("SELECT AllowedClasses FROM ItemTemplate WHERE Id_nb=?", (item,)).fetchone()[0]
                self.assertNotIn(allowed, ("", "0"))
                self.assertTrue(class_ids <= {int(c) for c in allowed.split(";")}, allowed)


def world_digest(conn):
    """Every row of the tables the fix touches, as one comparable value."""
    tables = ("DataQuest", "ItemTemplate", "Mob", "Quest", "CharacterXDataQuest", "Inventory")
    return {t: hash(tuple(conn.execute(f'SELECT * FROM "{t}" ORDER BY rowid').fetchall())) for t in tables}


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class EpicSafetyTests(unittest.TestCase):
    """Once per world, all or nothing, and never over a row upstream or the owner changed (spec 3.4)."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, self.db)
        self.conn = sqlite3.connect(self.db)

    def tearDown(self):
        self.conn.close()
        self.tmp.cleanup()

    def test_a_second_run_changes_nothing(self):
        self.assertEqual(len(epic_chains.apply(self.conn, NOW)), 1)
        self.conn.commit()
        before = world_digest(self.conn)
        self.assertEqual(epic_chains.apply(self.conn, NOW), [])
        self.assertEqual(world_digest(self.conn), before)

    def test_a_failing_step_changes_nothing(self):
        before = world_digest(self.conn)

        def boom(conn, now, data):
            raise RuntimeError("boom")

        with unittest.mock.patch.object(epic_chains, "STEPS", epic_chains.STEPS + (boom,)):
            self.assertEqual(epic_chains.apply(self.conn, NOW), [epic_chains.NOT_APPLIED.format("boom")])
        self.conn.commit()
        self.assertEqual(world_digest(self.conn), before)
        tables = {n for (n,) in self.conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        self.assertNotIn("fork_world_fixes", tables)

    def test_a_row_upstream_already_changed_is_left_alone(self):
        self.conn.execute("UPDATE DataQuest SET QuestDependency='Path of the Renegade' WHERE ID=21491")
        result = epic_chains.apply(self.conn, NOW)
        self.assertIn("Guild of Shadows 59 links", result[0])
        self.assertEqual(self.conn.execute("SELECT QuestDependency FROM DataQuest WHERE ID=21491").fetchone(),
                         ("Path of the Renegade",))

    def test_a_world_without_the_quest_tables_is_left_alone(self):
        empty = sqlite3.connect(":memory:")
        self.assertEqual(epic_chains.apply(empty, NOW), [])

    def test_items_that_exist_are_left_alone(self):
        # Upstream added one meanwhile (or the owner edited it), and the owner set a vest's charges.
        self.conn.execute("INSERT INTO ItemTemplate (Id_nb, Name) VALUES ('cq_alb_ring_of_shades', 'Their Ring')")
        self.conn.execute("UPDATE ItemTemplate SET SpellID=999, Charges=1, MaxCharges=1 WHERE Id_nb='ReaverEpicVest'")
        result = epic_chains.apply(self.conn, NOW)
        self.assertIn("40 items added, 35 item fixes", result[0])
        self.assertEqual(self.conn.execute("SELECT Name FROM ItemTemplate WHERE Id_nb='cq_alb_ring_of_shades'").fetchone(),
                         ("Their Ring",))
        self.assertEqual(self.conn.execute("SELECT SpellID, Charges FROM ItemTemplate WHERE Id_nb='ReaverEpicVest'").fetchone(),
                         (999, 1))


if __name__ == "__main__":
    unittest.main()
