"""The Guild of Shadows chain's dialogue: deploy/bin/quest_dialogue.py and its data file quest_dialogue.json.

The synthetic tests use a tiny DataQuest table and data passed in. The tests of the real data file run world_fixes on a
copy of a clean classic world (HDC_TEST_WORLD).
"""
import contextlib
import io
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
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import epic_chains  # noqa: E402
import quest_dialogue as qd  # noqa: E402
import world_fixes as wf  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCHEMA = ("CREATE TABLE DataQuest (ID INTEGER PRIMARY KEY, StepType TEXT, AcceptText TEXT, Description TEXT, "
          "SourceText TEXT, StepText TEXT, TargetText TEXT, AdvanceText TEXT, FinishText TEXT, StepItemTemplates TEXT, "
          "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00')")
CHAINS = {"classes": {"Alpha": 1, "Beta": 2}, "steps": {"7": {"ids": [100, 200]}, "9": {"ids": [None, 300]}}}
OLD = "2000-01-01 00:00:00"
NOW = "2026-10-09 12:00:00"
CLASS_NAMES = ("Infiltrator", "Mercenary", "Cabalist", "Necromancer", "Reaver")


def guard_for(*rows):
    """The digest of a row's values given in sorted column-name order."""
    return qd.digest(list(rows))


class SyntheticTests(unittest.TestCase):
    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        self.conn.execute(SCHEMA)
        self.conn.execute("INSERT INTO DataQuest (ID, StepType, Description, FinishText) VALUES (100, '2|3', 'old A', 'bye')")
        self.conn.execute("INSERT INTO DataQuest (ID, StepType, Description, FinishText) VALUES (200, '2|3', 'old B', 'bye')")
        self.conn.commit()

    def tearDown(self):
        self.conn.close()

    def data(self, **overrides):
        entry = {"step": "7",
                 "set": {"Description": {"Alpha": "new A", "Beta": "new B"}, "FinishText": "farewell"},
                 "guard": {"100": [guard_for("old A", "bye")], "200": [guard_for("old B", "bye")]}}
        entry.update(overrides)
        return {"quests": [entry]}

    def run_fix(self, data):
        with self.conn:
            return qd.apply(self.conn, NOW, data, CHAINS)

    def row(self, qid, *columns):
        return self.conn.execute(f"SELECT {', '.join(columns)} FROM DataQuest WHERE ID=?", (qid,)).fetchone()

    def test_a_guarded_row_is_rewritten_for_its_class(self):
        self.assertEqual(self.run_fix(self.data()), ["Quest dialogue: 2 quests rewritten"])
        self.assertEqual(self.row(100, "Description", "FinishText", "LastTimeRowUpdated"), ("new A", "farewell", NOW))
        self.assertEqual(self.row(200, "Description", "FinishText", "LastTimeRowUpdated"), ("new B", "farewell", NOW))

    def test_a_second_run_changes_nothing_and_prints_nothing(self):
        data = self.data()
        self.run_fix(data)
        self.conn.execute("UPDATE DataQuest SET LastTimeRowUpdated=?", (OLD,))
        self.conn.commit()
        before = self.conn.execute("SELECT * FROM DataQuest ORDER BY ID").fetchall()
        self.assertEqual(self.run_fix(data), [])
        self.assertEqual(self.conn.execute("SELECT * FROM DataQuest ORDER BY ID").fetchall(), before)

    def test_a_row_the_owner_edited_is_kept_and_counted(self):
        self.conn.execute("UPDATE DataQuest SET Description='the owner wrote this' WHERE ID=100")
        self.conn.commit()
        self.assertEqual(self.run_fix(self.data()),
                         ["Quest dialogue: 1 quests rewritten; 1 left as they are (changed since upstream)"])
        self.assertEqual(self.row(100, "Description", "LastTimeRowUpdated"), ("the owner wrote this", OLD))
        self.assertEqual(self.row(200, "Description"), ("new B",))

    def test_a_kept_row_is_counted_again_on_every_run_and_nothing_else_is_said(self):
        self.conn.execute("UPDATE DataQuest SET Description='mine' WHERE ID=100")
        self.conn.execute("UPDATE DataQuest SET Description='mine too' WHERE ID=200")
        self.conn.commit()
        self.assertEqual(self.run_fix(self.data()), [])
        self.assertEqual(self.row(100, "Description"), ("mine",))

    def test_a_row_already_at_the_new_text_is_left_alone(self):
        self.conn.execute("UPDATE DataQuest SET Description='new A', FinishText='farewell' WHERE ID=100")
        self.conn.commit()
        data = self.data(guard={})  # no guard at all: the row at the new text is not "changed since upstream" either
        self.assertEqual(self.run_fix(data), [])
        self.assertEqual(self.row(100, "LastTimeRowUpdated"), (OLD,))

    def test_an_earlier_version_of_the_text_is_rewritten_by_a_later_one(self):
        self.run_fix(self.data())
        later = self.data(set={"Description": {"Alpha": "newer A", "Beta": "newer B"}, "FinishText": "farewell"},
                          guard={"100": [guard_for("new A", "farewell")], "200": [guard_for("new B", "farewell")]})
        self.assertEqual(self.run_fix(later), ["Quest dialogue: 2 quests rewritten"])
        self.assertEqual(self.row(200, "Description"), ("newer B",))

    def test_null_and_empty_text_are_the_same(self):
        self.conn.execute("UPDATE DataQuest SET Description=NULL, FinishText='' WHERE ID=100")
        self.conn.commit()
        data = self.data(guard={"100": [guard_for("", "")], "200": [guard_for("old B", "bye")]})
        self.assertEqual(self.run_fix(data), ["Quest dialogue: 2 quests rewritten"])

    def test_per_stage_lists_and_per_class_objects_resolve(self):
        data = self.data(set={"StepText": ["first", {"Alpha": "second A", "Beta": "second B"}],
                              "TargetText": ["", ""], "AcceptText": "errand"},
                         guard={"100": [guard_for("", "", "")], "200": [guard_for("", "", "")]})
        self.assertEqual(self.run_fix(data), ["Quest dialogue: 2 quests rewritten"])
        self.assertEqual(self.row(100, "AcceptText", "StepText", "TargetText"), ("errand", "first|second A", "|"))
        self.assertEqual(self.row(200, "AcceptText", "StepText", "TargetText"), ("errand", "first|second B", "|"))

    def test_a_null_id_is_skipped(self):
        self.conn.execute("INSERT INTO DataQuest (ID, StepType, Description) VALUES (300, '2', 'old C')")
        self.conn.commit()
        data = {"quests": [{"step": "9", "set": {"Description": "new C"},
                            "guard": {"300": [guard_for("old C")]}}]}
        self.assertEqual(self.run_fix(data), ["Quest dialogue: 1 quests rewritten"])
        self.assertEqual(self.row(300, "Description"), ("new C",))

    def test_a_missing_row_is_skipped(self):
        self.conn.execute("DELETE FROM DataQuest WHERE ID=200")
        self.conn.commit()
        self.assertEqual(self.run_fix(self.data()), ["Quest dialogue: 1 quests rewritten"])

    def test_no_dataquest_table_is_not_an_error(self):
        conn = sqlite3.connect(":memory:")
        self.assertEqual(qd.apply(conn, NOW, self.data(), CHAINS), [])
        conn.close()

    def test_a_list_with_another_number_of_stages_keeps_the_rows_and_is_not_an_error(self):
        before = self.conn.execute("SELECT * FROM DataQuest ORDER BY ID").fetchall()
        self.assertEqual(self.run_fix(self.data(set={"StepText": ["only one"]})), [])
        self.assertEqual(self.conn.execute("SELECT * FROM DataQuest ORDER BY ID").fetchall(), before)

    def test_one_row_with_another_number_of_stages_is_kept_and_the_other_is_rewritten(self):
        # The owner (or a later world) gave quest 200 a third stage: the file's two-stage text doesn't fit it.
        self.conn.execute("UPDATE DataQuest SET StepType='2|0|3' WHERE ID=200")
        self.conn.commit()
        data = self.data(set={"Description": {"Alpha": "new A", "Beta": "new B"}, "StepText": ["go", "come back"]},
                         guard={"100": [guard_for("old A", "")], "200": [guard_for("old B", "")]})
        self.assertEqual(self.run_fix(data),
                         ["Quest dialogue: 1 quests rewritten; 1 left as they are (changed since upstream)"])
        self.assertEqual(self.row(100, "Description", "StepText"), ("new A", "go|come back"))
        self.assertEqual(self.row(200, "Description", "StepText", "LastTimeRowUpdated"), ("old B", None, OLD))

    def test_a_bad_file_rolls_back_the_fix_and_says_so(self):
        bad = {
            "a list in a column that has no stages": self.data(set={"Description": ["a", "b"]}),
            "a class missing": self.data(set={"Description": {"Alpha": "x"}}),
            "an unknown class": self.data(set={"Description": {"Alpha": "x", "Beta": "y", "Gamma": "z"}}),
            "a | in a value": self.data(set={"Description": "a|b"}),
            "a | in a per-class value": self.data(set={"Description": {"Alpha": "a", "Beta": "b|c"}}),
            "a | in a stage": self.data(set={"StepText": ["a|b", "c"]}),
            "an unknown column": self.data(set={"Name": "x"}),
            "an unknown step": self.data(step="8"),
            "a step twice": {"quests": self.data()["quests"] * 2},
            "a value that is not text": self.data(set={"Description": 5}),
        }
        for what, data in bad.items():
            with self.subTest(what):
                before = self.conn.execute("SELECT * FROM DataQuest ORDER BY ID").fetchall()
                lines = self.run_fix(data)
                self.assertEqual(len(lines), 1, lines)
                self.assertTrue(lines[0].startswith("Quest dialogue: not applied ("), lines[0])
                self.assertEqual(self.conn.execute("SELECT * FROM DataQuest ORDER BY ID").fetchall(), before)

    def test_a_database_error_is_a_not_applied_line(self):
        self.conn.execute("DROP TABLE DataQuest")
        self.conn.execute(SCHEMA.replace("FinishText TEXT,", ""))  # FinishText is gone: reading the row fails
        self.conn.execute("INSERT INTO DataQuest (ID, StepType, Description) VALUES (100, '2|3', 'old A')")
        self.conn.commit()
        lines = self.run_fix(self.data())
        self.assertTrue(lines[0].startswith("Quest dialogue: not applied ("), lines)
        self.assertEqual(self.row(100, "Description"), ("old A",))

    def test_a_missing_data_file_is_a_not_applied_line(self):
        with unittest.mock.patch.object(qd, "load_data", side_effect=FileNotFoundError("quest_dialogue.json")):
            with self.conn:
                lines = qd.apply(self.conn, NOW, None, CHAINS)
        self.assertEqual(len(lines), 1)
        self.assertTrue(lines[0].startswith("Quest dialogue: not applied ("), lines)

    def test_the_digest_is_the_documented_one(self):
        import hashlib
        self.assertEqual(qd.digest(["a", None, "é"]), hashlib.sha256('["a", "", "é"]'.encode("utf-8")).hexdigest())


class FailureLeavesTheOtherFixesTests(unittest.TestCase):
    """world_fixes.apply: a failing dialogue fix undoes only itself; the fixes before it are saved."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            c.execute("CREATE TABLE ServerProperty (Category TEXT NOT NULL DEFAULT '', `Key` VARCHAR(255) NOT NULL DEFAULT '', "
                      "Description TEXT NOT NULL DEFAULT '', DefaultValue TEXT NOT NULL DEFAULT '', Value TEXT NOT NULL DEFAULT '', "
                      "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', ServerProperty_ID VARCHAR(255), "
                      "PRIMARY KEY (`Key`))")
            c.execute("INSERT INTO ServerProperty (`Key`, Value) VALUES ('disabled_classes', '20;33')")
            c.execute("CREATE TABLE StartupLocation (StartupLoc_ID INTEGER PRIMARY KEY AUTOINCREMENT, XPos INT, YPos INT, "
                      "ZPos INT, Heading INT, Region INT, MinVersion INT, RealmID INT, RaceID INT, ClassID INT, "
                      "ClientRegionID INT, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00')")
            c.execute(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def test_a_bad_data_file_does_not_stop_the_other_fixes(self):
        bad = {"quests": [{"step": "7", "set": {"Name": "x"}, "guard": {}}]}
        with unittest.mock.patch.object(qd, "load_data", return_value=bad), \
                unittest.mock.patch.object(epic_chains, "load_data", return_value=CHAINS):
            changes = wf.apply(self.db)
        self.assertEqual(changes[0], "Disciple (Necromancer's base class) enabled: disabled_classes 20;33 -> 33")
        self.assertEqual(len(changes), 2)
        self.assertTrue(changes[1].startswith("Quest dialogue: not applied ("), changes)
        with sqlite3.connect(self.db) as c:
            self.assertEqual(c.execute("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'").fetchone(), ("33",))


def real_data():
    return qd.load_data(), epic_chains.load_data()


def quest_ids(data, chains):
    return [qid for entry in data["quests"] for qid in chains["steps"][entry["step"]]["ids"] if qid is not None]


class DataFileTests(unittest.TestCase):
    """The shape of quest_dialogue.json (no world needed). Waits for the data file."""

    def test_every_entry_is_well_formed(self):
        data, chains = real_data()
        self.assertTrue(data["quests"])
        seen = set()
        for entry in data["quests"]:
            with self.subTest(step=entry["step"]):
                self.assertIn(entry["step"], chains["steps"])
                self.assertNotIn(entry["step"], seen)
                seen.add(entry["step"])
                self.assertTrue(entry["set"])
                self.assertLessEqual(set(entry["set"]), set(qd.COLUMNS))
                ids = {str(qid) for qid in chains["steps"][entry["step"]]["ids"] if qid is not None}
                self.assertEqual(set(entry["guard"]), ids)
                for digests in entry["guard"].values():
                    self.assertTrue(digests)
                    for d in digests:
                        self.assertRegex(d, r"^[0-9a-f]{64}$")


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class RealDialogueTests(unittest.TestCase):
    """The real data file on a copy of a clean classic world. These wait for quest_dialogue.json."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.data, cls.chains = real_data()
        cls.first = wf.apply(cls.db)
        cls.conn = sqlite3.connect(cls.db)
        cls.rows = {qid: cls.conn.execute(
            "SELECT AcceptText, Description, SourceText, StepType, StepText, TargetText, AdvanceText, FinishText "
            "FROM DataQuest WHERE ID=?", (qid,)).fetchone() for qid in quest_ids(cls.data, cls.chains)}

    @classmethod
    def tearDownClass(cls):
        cls.conn.close()
        cls.tmp.cleanup()

    def test_every_quest_of_every_entry_is_rewritten_and_none_is_kept(self):
        total = len(quest_ids(self.data, self.chains))
        self.assertIn(f"Quest dialogue: {total} quests rewritten", self.first)

    def test_a_second_run_changes_nothing(self):
        self.assertEqual(wf.apply(self.db), [])

    def test_the_rows_hold_the_resolved_text_of_the_file(self):
        planned = qd._plan(self.conn, self.data, self.chains)
        self.assertEqual(len(planned), len(self.rows))
        for qid, columns, values, _guard in planned:
            with self.subTest(quest=qid):
                held = self.conn.execute(f"SELECT {', '.join(columns)} FROM DataQuest WHERE ID=?", (qid,)).fetchone()
                self.assertEqual([v or "" for v in held], values)

    def test_a_description_that_names_the_accept_text_has_it_in_brackets(self):
        for qid, (accept, description, *_rest) in self.rows.items():
            if accept and accept.lower() in (description or "").lower():
                with self.subTest(quest=qid):
                    self.assertIn(f"[{accept}]", description)

    def test_every_quest_can_be_accepted_from_what_its_giver_says(self):
        # The giver's Description offers [keywords]; one is the AcceptText, or leads to it through our Chat replies
        # (level 25: [matter] -> [dispatch] -> [supporting]; level 43: [interested] -> [profitable]).
        with open(os.path.join(HERE, "..", "hearthdaoc-quests.json"), encoding="utf-8") as f:
            chat = json.load(f).get("Chat", {})
        for qid in self.rows:
            accept, description, giver = self.conn.execute(
                "SELECT AcceptText, Description, StartName FROM DataQuest WHERE ID=?", (qid,)).fetchone()
            replies = {k.lower(): v for k, v in chat.get(giver, {}).items()}
            offered, todo = set(), re.findall(r"\[([^\]]+)\]", description or "")
            while todo:
                keyword = todo.pop()
                if keyword not in offered:
                    offered.add(keyword)
                    todo.extend(re.findall(r"\[([^\]]+)\]", replies.get(keyword.lower(), "")))
            with self.subTest(quest=qid, giver=giver):
                self.assertIn(accept, offered)

    def test_every_whisper_steps_advance_text_is_a_bracketed_keyword_in_its_target_text(self):
        for qid, (_accept, _desc, _source, step_types, _steps, targets, advances, _finish) in self.rows.items():
            types, targets, advances = step_types.split("|"), (targets or "").split("|"), (advances or "").split("|")
            for stage, step_type in enumerate(types):
                if step_type in ("6", "7"):
                    with self.subTest(quest=qid, stage=stage + 1):
                        self.assertTrue(advances[stage])
                        self.assertIn(f"[{advances[stage]}]", targets[stage])

    def test_no_popup_text_is_longer_than_1000_characters(self):
        for qid, (accept, description, source, _types, steps, targets, _advances, finish) in self.rows.items():
            texts = [description, finish] + (source or "").split("|") + (steps or "").split("|") + (targets or "").split("|")
            for text in texts:
                with self.subTest(quest=qid):
                    self.assertLessEqual(len(text or ""), 1000, (text or "")[:60])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class DigestCommandTests(unittest.TestCase):
    """--digests on a clean world prints upstream's text's digests, as the guard lists hold them; with --current, the
    digests of the file's own text."""

    def test_the_digests_are_those_of_the_worlds_rows_after_the_epic_chains(self):
        data, chains = real_data()
        out = qd.digests(TEST_WORLD, data, chains)
        self.assertEqual(sorted(out), sorted(entry["step"] for entry in data["quests"]))
        with tempfile.TemporaryDirectory() as tmp:
            copy = os.path.join(tmp, "world.db")
            shutil.copyfile(TEST_WORLD, copy)
            conn = sqlite3.connect(copy)
            with conn:
                epic_chains.apply(conn)
            for entry in data["quests"]:
                columns = sorted(entry["set"])
                for qid, d in out[entry["step"]].items():
                    row = conn.execute(f"SELECT {', '.join(columns)} FROM DataQuest WHERE ID=?", (int(qid),)).fetchone()
                    self.assertEqual(d, qd.digest(list(row)))
            conn.close()
        self.assertTrue(all(d in entry["guard"][qid] for entry in data["quests"]
                            for qid, d in out[entry["step"]].items()))

    def test_current_prints_the_digests_of_the_files_own_text(self):
        data, chains = real_data()
        out = qd.digests(TEST_WORLD, data, chains, current=True)
        printed = {int(qid): d for step in out.values() for qid, d in step.items()}
        with tempfile.TemporaryDirectory() as tmp:
            copy = os.path.join(tmp, "world.db")
            shutil.copyfile(TEST_WORLD, copy)
            conn = sqlite3.connect(copy)
            with conn:
                epic_chains.apply(conn)
            planned = {qid: qd.digest(values) for qid, _columns, values, _guard in qd._plan(conn, data, chains)}
            conn.close()
        self.assertEqual(printed, planned)


class DigestCliTests(unittest.TestCase):
    """The --digests command on a synthetic world, with a data file of its own."""

    def test_it_prints_the_digests_of_the_sets_columns_and_leaves_the_world_alone(self):
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "world.db")
            with sqlite3.connect(db) as c:
                c.execute(SCHEMA)
                c.execute("INSERT INTO DataQuest (ID, StepType, Description, FinishText) VALUES (100, '2|3', 'old A', 'bye')")
                c.execute("INSERT INTO DataQuest (ID, StepType, Description) VALUES (300, '2', NULL)")
            data = {"quests": [{"step": "7", "set": {"FinishText": "x", "Description": "y"}},
                               {"step": "9", "set": {"Description": "z"}}]}
            before = pathlib.Path(db).read_bytes()
            out = qd.digests(db, data, CHAINS)
            self.assertEqual(pathlib.Path(db).read_bytes(), before)
        self.assertEqual(out, {"7": {"100": qd.digest(["old A", "bye"])}, "9": {"300": qd.digest([""])}})


def apply_file(db, data):
    conn = sqlite3.connect(db)
    try:
        with conn:
            return qd.apply(conn, NOW, data, CHAINS)
    finally:
        conn.close()


def held(db):
    conn = sqlite3.connect(db)
    try:
        return conn.execute("SELECT ID, Description, FinishText FROM DataQuest ORDER BY ID").fetchall()
    finally:
        conn.close()


class RevisionTests(unittest.TestCase):
    """The documented revision procedure: before changing the text, `--digests <clean world> --current`, and each
    digest appended to its quest's guard list; then the new text. A world at the earlier revision gets the new one."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.clean = os.path.join(self.tmp.name, "clean.db")
        conn = sqlite3.connect(self.clean)
        with conn:
            conn.execute(SCHEMA)
            conn.execute("INSERT INTO DataQuest (ID, StepType, Description, FinishText) VALUES (100, '2|3', 'old A', 'bye')")
            conn.execute("INSERT INTO DataQuest (ID, StepType, Description, FinishText) VALUES (200, '2|3', 'old B', 'bye')")
        conn.close()
        self.upstream = {"100": [guard_for("old A", "bye")], "200": [guard_for("old B", "bye")]}
        self.revision = {"quests": [{"step": "7", "set": {"Description": {"Alpha": "new A", "Beta": "new B"},
                                                          "FinishText": "farewell"},
                                     "guard": self.upstream}]}
        self.world = os.path.join(self.tmp.name, "world.db")  # a world that had this revision
        shutil.copyfile(self.clean, self.world)
        self.assertEqual(apply_file(self.world, self.revision), ["Quest dialogue: 2 quests rewritten"])

    def tearDown(self):
        self.tmp.cleanup()

    def next_revision(self, added):
        guard = {qid: digests + [added[qid]] for qid, digests in self.upstream.items()}
        return {"quests": [{"step": "7", "set": {"Description": {"Alpha": "newer A", "Beta": "newer B"},
                                                 "FinishText": "farewell"},
                            "guard": guard}]}

    def test_current_prints_the_digests_of_the_files_own_text_and_leaves_the_world_alone(self):
        before = pathlib.Path(self.clean).read_bytes()
        out = qd.digests(self.clean, self.revision, CHAINS, current=True)
        self.assertEqual(pathlib.Path(self.clean).read_bytes(), before)
        self.assertEqual(out, {"7": {"100": guard_for("new A", "farewell"), "200": guard_for("new B", "farewell")}})

    def test_a_revision_guarded_by_the_current_digests_rewrites_every_row(self):
        current = qd.digests(self.clean, self.revision, CHAINS, current=True)["7"]
        self.assertEqual(apply_file(self.world, self.next_revision(current)), ["Quest dialogue: 2 quests rewritten"])
        self.assertEqual(held(self.world), [(100, "newer A", "farewell"), (200, "newer B", "farewell")])

    def test_a_revision_guarded_by_the_clean_worlds_digests_alone_keeps_every_row(self):
        # Without --current, a clean world gives upstream's digests again: the earlier revision's text stays.
        clean = qd.digests(self.clean, self.revision, CHAINS)["7"]
        self.assertEqual(clean, {qid: digests[0] for qid, digests in self.upstream.items()})
        before = held(self.world)
        self.assertEqual(apply_file(self.world, self.next_revision(clean)), [])
        self.assertEqual(held(self.world), before)

    def test_current_is_an_error_when_a_row_would_keep_another_text(self):
        unguarded = json.loads(json.dumps(self.revision))
        unguarded["quests"][0]["guard"]["200"] = []
        with self.assertRaisesRegex(RuntimeError, "quest 200 does not hold the file's text"):
            qd.digests(self.clean, unguarded, CHAINS, current=True)

    def test_the_command_takes_current(self):
        out = io.StringIO()
        with unittest.mock.patch.object(qd, "load_data", return_value=self.revision), \
                unittest.mock.patch.object(epic_chains, "load_data", return_value=CHAINS), \
                contextlib.redirect_stdout(out):
            self.assertEqual(qd.main(["--digests", self.clean, "--current"]), 0)
        self.assertEqual(json.loads(out.getvalue()),
                         {"7": {"100": guard_for("new A", "farewell"), "200": guard_for("new B", "farewell")}})


if __name__ == "__main__":
    unittest.main()
