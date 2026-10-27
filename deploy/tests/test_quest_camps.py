"""deploy/bin/quest_camps.py: the quest camps upstream archived as "post-legacy" spawns, copied back once per world.

Built on a small database with the tables the step reads; the camps in deploy/bin/quest_camps.json are checked on a
real world by test_epic_period.py.
"""
import os
import sqlite3
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "bin"))

import quest_camps  # noqa: E402

NOW = "2026-10-12 12:00:00"
DATA = {"camps": [
    {"key": "fire", "name": "Delfina's camp", "names": ["Tylwyth Teg ranger"], "ids": ["r1", "r2"], "why": "period"},
    {"key": "tower", "name": "Lieutenant Grimarth", "names": ["Lieutenant Grimarth"], "ids": ["g1"], "why": "period"},
]}


def world():
    conn = sqlite3.connect(":memory:")
    for table in ("Mob", quest_camps.ARCHIVE):
        extra = ", RemovalReason TEXT" if table == quest_camps.ARCHIVE else ""
        conn.execute(f"CREATE TABLE {table} (Mob_ID TEXT PRIMARY KEY, Name TEXT, Region INT, X INT, Y INT, Z INT{extra})")
    conn.executemany(f"INSERT INTO {quest_camps.ARCHIVE} VALUES (?, ?, 1, ?, 0, 0, 'Post-legacy')",
                     [("r1", "Tylwyth Teg ranger", 1), ("r2", "Tylwyth Teg ranger", 2), ("g1", "Lieutenant Grimarth", 3),
                      ("other", "Tylwyth Teg ranger", 4)])
    return conn


class QuestCampTests(unittest.TestCase):
    def test_copies_each_camps_rows_back_and_keeps_the_archive(self):
        conn = world()
        self.assertEqual(quest_camps.apply(conn, NOW, DATA),
                         ["Quest camps: copied back from upstream's archive: Delfina's camp (2), Lieutenant Grimarth (1)"])
        self.assertEqual(conn.execute("SELECT Mob_ID, Name, X FROM Mob ORDER BY Mob_ID").fetchall(),
                         [("g1", "Lieutenant Grimarth", 3), ("r1", "Tylwyth Teg ranger", 1), ("r2", "Tylwyth Teg ranger", 2)])
        self.assertEqual(conn.execute(f"SELECT COUNT(*) FROM {quest_camps.ARCHIVE}").fetchone(), (4,))

    def test_a_camp_is_copied_once_so_rows_the_owner_removes_stay_removed(self):
        conn = world()
        quest_camps.apply(conn, NOW, DATA)
        conn.execute("DELETE FROM Mob WHERE Mob_ID='r1'")
        self.assertEqual(quest_camps.apply(conn, NOW, DATA), [])
        self.assertIsNone(conn.execute("SELECT 1 FROM Mob WHERE Mob_ID='r1'").fetchone())

    def test_a_new_camp_in_the_file_is_copied_on_a_world_that_has_the_others(self):
        conn = world()
        quest_camps.apply(conn, NOW, {"camps": DATA["camps"][:1]})
        self.assertEqual(quest_camps.apply(conn, NOW, DATA),
                         ["Quest camps: copied back from upstream's archive: Lieutenant Grimarth (1)"])

    def test_rows_already_in_the_world_are_left_as_they_are(self):
        conn = world()
        conn.execute("INSERT INTO Mob VALUES ('r1', 'Tylwyth Teg ranger', 1, 99, 0, 0)")
        self.assertEqual(quest_camps.apply(conn, NOW, DATA)[0],
                         "Quest camps: copied back from upstream's archive: Delfina's camp (1), Lieutenant Grimarth (1)")
        self.assertEqual(conn.execute("SELECT X FROM Mob WHERE Mob_ID='r1'").fetchone(), (99,))

    def test_a_row_missing_from_the_archive_or_named_otherwise_is_named_in_the_log(self):
        conn = world()
        data = {"camps": [{"key": "fire", "name": "Delfina's camp", "names": ["Tylwyth Teg ranger"],
                           "ids": ["r1", "gone", "g1"], "why": "period"}]}
        self.assertEqual(quest_camps.apply(conn, NOW, data),
                         ["Quest camps: copied back from upstream's archive: Delfina's camp (1); "
                          "not in the archive as named: Delfina's camp (gone, g1)"])
        self.assertIsNone(conn.execute("SELECT 1 FROM Mob WHERE Mob_ID='g1'").fetchone())

    def test_a_world_without_the_archive_is_left_alone(self):
        conn = sqlite3.connect(":memory:")
        conn.execute("CREATE TABLE Mob (Mob_ID TEXT PRIMARY KEY, Name TEXT)")
        self.assertEqual(quest_camps.apply(conn, NOW, DATA), [])
        self.assertEqual(conn.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").fetchall(),
                         [("Mob",)])

    def test_a_failure_rolls_the_step_back_and_says_so(self):
        # A good camp first, so rows and a marker are written before the bad entry fails: all of it is undone.
        conn = world()
        bad = {"camps": [DATA["camps"][0], {"key": "bad", "name": "No source", "names": ["x"], "ids": ["g1"]}]}
        line, = quest_camps.apply(conn, NOW, bad)
        self.assertTrue(line.startswith("Quest camps: not applied (camp bad: no why)"), line)
        self.assertIsNone(conn.execute("SELECT 1 FROM Mob").fetchone())
        self.assertEqual(conn.execute("SELECT name FROM sqlite_master WHERE name=?", (quest_camps.MARKER_TABLE,)).fetchall(), [])
        self.assertEqual(quest_camps.apply(conn, NOW, DATA)[0],
                         "Quest camps: copied back from upstream's archive: Delfina's camp (2), Lieutenant Grimarth (1)")

    def test_rows_hdc_spawns_restored_become_the_camps(self):
        # `hdc spawns restore` at a high level can bring back some of a camp's (lowercase-named) rows and track them in
        # fork_restored_mobs, which a later restore, `hdc spawns undo` and the quest-twin removal delete. Once the camp
        # is copied, its rows are the camp's: they leave that list.
        conn = world()
        conn.execute("CREATE TABLE fork_restored_mobs (Mob_ID TEXT PRIMARY KEY, PatchId TEXT, MaxLevel INT, RestoredUtc TEXT)")
        conn.execute("INSERT INTO Mob VALUES ('r1', 'Tylwyth Teg ranger', 1, 1, 0, 0)")
        conn.executemany("INSERT INTO fork_restored_mobs VALUES (?, 'restore-leveling-spawns-v1', 50, ?)", [("r1", NOW), ("other", NOW)])
        quest_camps.apply(conn, NOW, DATA)
        self.assertEqual(conn.execute("SELECT Mob_ID FROM fork_restored_mobs").fetchall(), [("other",)])

    def test_a_camp_archived_under_other_names_is_named_in_the_log(self):
        conn = world()
        conn.execute(f"UPDATE {quest_camps.ARCHIVE} SET Name='Renamed ranger' WHERE Mob_ID IN ('r1', 'r2')")
        data = {"camps": DATA["camps"][:1]}
        self.assertEqual(quest_camps.apply(conn, NOW, data),
                         ["Quest camps: not in the archive as named: Delfina's camp (r1, r2)"])
        self.assertIsNone(conn.execute("SELECT 1 FROM Mob").fetchone())

    # "clone": an NPC the period had and the world lacks, made like another row (Lieutenant Cian like Lieutenant Brude).
    CIAN = {"key": "cian", "name": "Lieutenant Cian", "kind": "clone", "like": "b1", "id": "c1",
            "set": {"Name": "Lieutenant Cian", "X": 7}, "why": "period"}

    def test_a_clone_is_added_like_its_row_once(self):
        conn = world()
        conn.execute("INSERT INTO Mob VALUES ('b1', 'Lieutenant Brude', 1, 5, 6, 9200)")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.CIAN]}), ["Quest camps: added: Lieutenant Cian"])
        self.assertEqual(conn.execute("SELECT Mob_ID, Name, Region, X, Y, Z FROM Mob ORDER BY Mob_ID").fetchall(),
                         [("b1", "Lieutenant Brude", 1, 5, 6, 9200), ("c1", "Lieutenant Cian", 1, 7, 6, 9200)])
        conn.execute("DELETE FROM Mob WHERE Mob_ID='c1'")  # the owner removed him: he stays removed
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.CIAN]}), [])
        self.assertIsNone(conn.execute("SELECT 1 FROM Mob WHERE Mob_ID='c1'").fetchone())

    def test_a_clone_whose_row_is_missing_waits_quietly(self):
        # Not this world's (another world, or a test's); a later start tries again.
        conn = world()
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.CIAN]}), [])
        conn.execute("INSERT INTO Mob VALUES ('b1', 'Lieutenant Brude', 1, 5, 6, 9200)")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.CIAN]}), ["Quest camps: added: Lieutenant Cian"])

    def test_a_clone_whose_id_is_taken_is_named_in_the_log(self):
        conn = world()
        conn.execute("INSERT INTO Mob VALUES ('b1', 'Lieutenant Brude', 1, 5, 6, 9200)")
        conn.execute("INSERT INTO Mob VALUES ('c1', 'Someone', 1, 5, 6, 9200)")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.CIAN]}),
                         ["Quest camps: not added: Lieutenant Cian (its Mob_ID c1 is taken)"])

    # "archive": a row the period didn't have, kept in fork_removed_mobs (the generated Bline beside Bline Tengit).
    BLINE = {"key": "bline", "name": "the generated Bline", "kind": "archive", "ids": ["x1"], "names": ["Bline"],
             "why": "period"}

    def test_an_archived_row_leaves_the_world_and_is_kept(self):
        conn = world()
        conn.execute("INSERT INTO Mob VALUES ('x1', 'Bline', 1, 5, 6, 7)")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.BLINE]}),
                         ["Quest camps: archived in fork_removed_mobs: the generated Bline (1)"])
        self.assertIsNone(conn.execute("SELECT 1 FROM Mob WHERE Mob_ID='x1'").fetchone())
        self.assertEqual(conn.execute("SELECT Mob_ID, Name, FixId FROM fork_removed_mobs").fetchall(),
                         [("x1", "Bline", "quest-camp:bline")])
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.BLINE]}), [])

    def test_a_row_to_archive_with_another_name_stays(self):
        conn = world()
        conn.execute("INSERT INTO Mob VALUES ('x1', 'Bline Tengit', 1, 5, 6, 7)")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.BLINE]}),
                         ["Quest camps: not archived (another name): the generated Bline (x1)"])
        self.assertIsNotNone(conn.execute("SELECT 1 FROM Mob WHERE Mob_ID='x1'").fetchone())

    def test_a_row_to_archive_already_gone_is_quietly_done(self):
        conn = world()
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.BLINE]}), [])

    # "path": a walk the period gave a quest monster, added with its points and its walker put on it (Mulgrut Maggot's
    # walk between the Camelot Hills cemetery and the tall stone).
    WALK = {"key": "walk", "name": "M's walk", "kind": "path", "path": "HDC_Test", "type": 2, "region": 1,
            "walkers": ["m1"], "points": [[1, 2, 3, 70, 1800], [4, 5, 6, 70, 0]], "why": "period"}

    @staticmethod
    def path_world():
        """world() with Mob.PathID and the Path and PathPoints tables as a real world declares them."""
        conn = world()
        conn.execute("ALTER TABLE Mob ADD COLUMN PathID TEXT")
        conn.execute("CREATE TABLE Path (PathID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PathType INT(11) NOT NULL "
                     "DEFAULT 0, RegionID UNSIGNED SMALLINT(5) NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL "
                     "DEFAULT '2000-01-01 00:00:00', Path_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
                     "PRIMARY KEY (Path_ID))")
        conn.execute("CREATE UNIQUE INDEX U_Path_PathID ON Path (PathID)")
        conn.execute("CREATE TABLE PathPoints (PathID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, Step INT(11) NOT NULL "
                     "DEFAULT 0, X INT(11) NOT NULL DEFAULT 0, Y INT(11) NOT NULL DEFAULT 0, Z INT(11) NOT NULL DEFAULT 0, "
                     "MaxSpeed INT(11) NOT NULL DEFAULT 0, WaitTime INT(11) NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME "
                     "NOT NULL DEFAULT '2000-01-01 00:00:00', PathPoints_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
                     "PRIMARY KEY (PathPoints_ID))")
        conn.execute("CREATE INDEX I_PathPoints_PathID ON PathPoints (PathID)")
        return conn

    @staticmethod
    def add_walker(conn, path_id=None):
        conn.execute("INSERT INTO Mob (Mob_ID, Name, Region, X, Y, Z, PathID) VALUES ('m1', 'M', 1, 1, 2, 3, ?)", (path_id,))

    @staticmethod
    def path_rows(conn):
        return (conn.execute("SELECT PathID, PathType, RegionID, LastTimeRowUpdated, Path_ID FROM Path").fetchall(),
                conn.execute("SELECT PathID, Step, X, Y, Z, MaxSpeed, WaitTime, LastTimeRowUpdated, PathPoints_ID "
                             "FROM PathPoints ORDER BY Step").fetchall())

    def test_a_path_is_added_and_its_walker_put_on_it_once(self):
        conn = self.path_world()
        self.add_walker(conn)
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), ["Quest camps: paths added: M's walk (2 points)"])
        self.assertEqual(self.path_rows(conn), (
            [("HDC_Test", 2, 1, NOW, quest_camps.path_id("HDC_Test"))],
            [("HDC_Test", 1, 1, 2, 3, 70, 1800, NOW, quest_camps.path_id("HDC_Test", 1)),
             ("HDC_Test", 2, 4, 5, 6, 70, 0, NOW, quest_camps.path_id("HDC_Test", 2))]))
        self.assertEqual(conn.execute("SELECT PathID FROM Mob WHERE Mob_ID='m1'").fetchone(), ("HDC_Test",))
        self.assertEqual(conn.execute("SELECT FixId FROM fork_world_fixes").fetchall(), [("quest-camp:walk",)])
        # The owner takes him off it and deletes the path: it stays so.
        conn.execute("UPDATE Mob SET PathID=NULL")
        conn.execute("DELETE FROM Path")
        conn.execute("DELETE FROM PathPoints")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), [])
        self.assertEqual(self.path_rows(conn), ([], []))
        self.assertEqual(conn.execute("SELECT PathID FROM Mob WHERE Mob_ID='m1'").fetchone(), (None,))

    def test_a_path_whose_walker_is_missing_waits_quietly(self):
        # Not this world's (another world, or a test's): nothing is written, and a later start tries again.
        conn = self.path_world()
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), [])
        self.assertEqual(self.path_rows(conn), ([], []))
        self.assertEqual(conn.execute("SELECT COUNT(*) FROM fork_world_fixes").fetchone(), (0,))
        self.add_walker(conn)
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), ["Quest camps: paths added: M's walk (2 points)"])

    def test_a_taken_path_id_is_named_in_the_log(self):
        # The tables' unique index compares PathIDs without case, so a Path row in another case takes the PathID too;
        # points without a Path row are someone's path as well.
        for taken in ("INSERT INTO Path (PathID, PathType, RegionID, Path_ID) VALUES ('hdc_test', 1, 1, 'p')",
                      "INSERT INTO PathPoints (PathID, Step, X, Y, Z, PathPoints_ID) VALUES ('HDC_Test', 1, 9, 9, 9, 'q')"):
            with self.subTest(taken):
                conn = self.path_world()
                self.add_walker(conn)
                conn.execute(taken)
                before = self.path_rows(conn)
                self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}),
                                 ["Quest camps: not added: M's walk (its path HDC_Test is taken)"])
                self.assertEqual(self.path_rows(conn), before)
                self.assertEqual(conn.execute("SELECT PathID FROM Mob WHERE Mob_ID='m1'").fetchone(), (None,))
                self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), [])

    def test_a_walker_on_another_path_keeps_it(self):
        # The server's path cache matches PathIDs by their exact case (MovementMgr), so a walker on the path's name in
        # another case is on another path.
        for other in ("Other", "hdc_test"):
            with self.subTest(other):
                conn = self.path_world()
                self.add_walker(conn, other)
                self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}),
                                 ["Quest camps: paths added: M's walk (2 points); left on another path: M's walk (m1)"])
                self.assertEqual(conn.execute("SELECT PathID FROM Mob WHERE Mob_ID='m1'").fetchone(), (other,))
                self.assertEqual(len(self.path_rows(conn)[1]), 2)

    def test_a_walker_already_on_the_path_is_not_named(self):
        conn = self.path_world()
        self.add_walker(conn, "HDC_Test")
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), ["Quest camps: paths added: M's walk (2 points)"])
        self.assertEqual(conn.execute("SELECT PathID FROM Mob WHERE Mob_ID='m1'").fetchone(), ("HDC_Test",))

    def test_a_world_without_path_tables_waits_quietly(self):
        conn = world()
        conn.execute("ALTER TABLE Mob ADD COLUMN PathID TEXT")
        self.add_walker(conn)
        self.assertEqual(quest_camps.apply(conn, NOW, {"camps": [self.WALK]}), [])
        self.assertEqual(conn.execute("SELECT COUNT(*) FROM fork_world_fixes").fetchone(), (0,))

    def test_a_bad_path_entry_stops_the_step(self):
        bad = {"type 4": {"type": 4}, "type true": {"type": True}, "one point": {"points": [[1, 2, 3, 70, 0]]},
               "four numbers": {"points": [[1, 2, 3, 70], [4, 5, 6, 70, 0]]},
               "speed 0": {"points": [[1, 2, 3, 0, 0], [4, 5, 6, 70, 0]]},
               "wait -1": {"points": [[1, 2, 3, 70, -1], [4, 5, 6, 70, 0]]},
               "true as a number": {"points": [[1, 2, True, 70, 0], [4, 5, 6, 70, 0]]},
               "a text number": {"points": [[1, 2, "3", 70, 0], [4, 5, 6, 70, 0]]},
               "no walkers": {"walkers": []}, "a walker not a text": {"walkers": [1]}, "no path": {"path": " "},
               "no region": {"region": None}}
        for label, change in bad.items():
            with self.subTest(label):
                conn = self.path_world()
                self.add_walker(conn)
                line, = quest_camps.apply(conn, NOW, {"camps": [DATA["camps"][0], dict(self.WALK, **change)]})
                self.assertTrue(line.startswith("Quest camps: not applied (camp walk: "), line)
                self.assertEqual(self.path_rows(conn), ([], []))
                self.assertIsNone(conn.execute("SELECT 1 FROM Mob WHERE Mob_ID <> 'm1'").fetchone())  # the camp too
                self.assertEqual(conn.execute("SELECT PathID FROM Mob WHERE Mob_ID='m1'").fetchone(), (None,))

    def test_path_ids_are_stable(self):
        # Path_ID and PathPoints_ID are the tables' keys: made from the PathID (without case) and the step.
        for name in ("HDC_Mulgrut_CemeteryToMenhir", "hdc_mulgrut_cemeterytomenhir"):
            with self.subTest(name):
                self.assertEqual(quest_camps.path_id(name), "5c900371-1191-55f7-a15b-3f43e6aa7242")
                self.assertEqual(quest_camps.path_id(name, 1), "a1fcdeae-a6d0-57e9-9fa5-4f3a29efb691")
                self.assertEqual(quest_camps.path_id(name, 12), "fe0eddd8-78ed-57af-8d21-b677c10fdd51")

    def test_the_shipped_file_is_well_formed(self):
        data = quest_camps.load_data()
        keys = [camp["key"] for camp in data["camps"]]
        self.assertEqual(len(keys), len(set(keys)))
        ids = [i for camp in data["camps"] for i in camp.get("ids", []) + [camp.get("id")] if i]  # a clone has "id"
        self.assertEqual(len(ids), len(set(ids)))
        for camp in data["camps"]:
            with self.subTest(camp["key"]):
                quest_camps.check(camp)


if __name__ == "__main__":
    unittest.main()
