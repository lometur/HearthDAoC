import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
import unittest.mock

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import spawns  # noqa: E402
import world_fixes  # noqa: E402
from tests.test_quest_dialogue import SCHEMA as DATAQUEST  # noqa: E402
from tests.test_world_fixes import SCHEMA as WORLD_FIXES_SCHEMA  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")


def mob_ids(db):
    with sqlite3.connect(db) as c:
        return {r[0] for r in c.execute("SELECT Mob_ID FROM Mob")}


class SpawnsErrorTests(unittest.TestCase):
    def test_world_without_archive_is_refused(self):
        with tempfile.TemporaryDirectory() as data:
            os.makedirs(os.path.join(data, "world"))
            with sqlite3.connect(os.path.join(data, "world", "opendaoc.sqlite3.db")) as c:
                c.execute("CREATE TABLE Mob (Mob_ID TEXT PRIMARY KEY, Name TEXT)")
            with self.assertRaisesRegex(spawns.SpawnsError, "no archived spawns"):
                spawns.restore(data, 20)
            self.assertFalse(os.path.exists(os.path.join(data, "spawns.json")))

    def test_max_level_is_validated(self):
        with tempfile.TemporaryDirectory() as data:
            for bad in (0, 51):
                with self.subTest(level=bad), self.assertRaisesRegex(spawns.SpawnsError, "max level"):
                    spawns.restore(data, bad)


# Upstream's columns the restore reads; Mob.Name compares without case, as upstream's does.
MOB_COLUMNS = ("Mob_ID VARCHAR(255) PRIMARY KEY, Name VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
               "ClassType TEXT, Region INT, X INT, Y INT, Z INT, Realm INT DEFAULT 0, Level INT, "
               "NPCTemplateID INT DEFAULT 0, EquipmentTemplateID TEXT, PackageID TEXT")
TWIN_SCHEMA = WORLD_FIXES_SCHEMA + [
    f"CREATE TABLE Mob ({MOB_COLUMNS})",
    f"CREATE TABLE {spawns.ARCHIVE} ({MOB_COLUMNS.replace(' COLLATE NOCASE', '').replace(' PRIMARY KEY', '')})",
    "CREATE TABLE NpcTemplate (TemplateId INT, Level TEXT, ReplaceMobValues INT)",
    "CREATE TABLE NPCEquipment (TemplateID TEXT)",
    DATAQUEST.replace("StepType TEXT,", "StepType TEXT, TargetName TEXT COLLATE NOCASE,"),
]
# Level 20's "Path of the Renegade": kill a renegade guard, talk to Rhodri, kill the Arawnite Messenger; and a
# KillFinish stage, which counts too.
QUESTS = [(9001, "0|2|0|2|3", "renegade guard;1|Captain Rhodri;1|Arawnite Messenger;1|Omis;1|Captain Rhodri;1"),
          (9002, "4|1", "Cynwik the Wizard;1|CORNWALL HUNTER;1")]


def add(c, table, mob_id, name, x, y, region=1, level=19):
    c.execute(f"INSERT INTO {table} (Mob_ID, Name, ClassType, Region, X, Y, Z, Realm, Level) "
              "VALUES (?, ?, 'DOL.GS.GameNPC', ?, ?, ?, 0, 0, ?)", (mob_id, name, region, x, y, level))


class QuestTwinTests(unittest.TestCase):
    """The restore leaves out an archived row within 1000 units of a live quest kill target whose name differs from
    its own only in case: quests match a kill's name exactly, so the twin counted for nothing (owner test
    2026-10-10, "arawnite messenger" 170 units from the "Arawnite Messenger"). The world fix removes such rows an
    earlier restore added."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = self.tmp.name
        self.db = os.path.join(self.data, "world", "opendaoc.sqlite3.db")
        os.makedirs(os.path.dirname(self.db))
        with sqlite3.connect(self.db) as c:
            for stmt in TWIN_SCHEMA:
                c.execute(stmt)
            c.executemany("INSERT INTO DataQuest (ID, StepType, TargetName) VALUES (?, ?, ?)", QUESTS)
            add(c, "Mob", "messenger", "Arawnite Messenger", 488019, 407567, level=21)
            add(c, "Mob", "guard", "renegade guard", 100000, 100000)
            add(c, "Mob", "hunter", "Cornwall hunter", 409902, 675889)
            add(c, "Mob", "rhodri", "Captain Rhodri", 200000, 200000)
            add(c, "Mob", "spider", "Giant Spider", 300000, 300000)  # not a quest target
            archive = spawns.ARCHIVE
            add(c, archive, "twin", "arawnite messenger", 488085, 407724)  # 170 units away
            add(c, archive, "far", "arawnite messenger", 489100, 407567)  # 1081 units away
            add(c, archive, "other-region", "arawnite messenger", 488085, 407724, region=100)
            add(c, archive, "hunter-twin", "cornwall hunter", 410072, 675847)  # a KillFinish target
            add(c, archive, "guard-copy", "renegade guard", 100100, 100000)  # the same name: its kill counts
            add(c, archive, "rhodri-copy", "captain rhodri", 200100, 200000)  # Rhodri is talked to, not killed
            add(c, archive, "spider-copy", "giant spider", 300100, 300000)

    def tearDown(self):
        self.tmp.cleanup()

    def q(self, sql, *args):
        with sqlite3.connect(self.db) as c:
            return c.execute(sql, args).fetchall()

    def mobs(self):
        return {row[0] for row in self.q("SELECT Mob_ID FROM Mob")}

    def test_kill_targets_are_the_kill_stages_names_without_case(self):
        with sqlite3.connect(self.db) as c:
            self.assertEqual(spawns.kill_targets(c), {"renegade guard", "arawnite messenger", "cornwall hunter"})

    def test_the_restore_leaves_out_twins_of_quest_targets(self):
        self.assertEqual(spawns.restore(self.data, 20), 5)
        self.assertEqual(self.mobs() - {"messenger", "guard", "hunter", "rhodri", "spider"},
                         {"far", "other-region", "guard-copy", "rhodri-copy", "spider-copy"})

    def test_the_world_fix_removes_the_twins_an_earlier_restore_added_and_keeps_them_archived(self):
        self.old_restore()
        changes = world_fixes.apply(self.db)
        self.assertEqual(changes, ["Restored spawns: 2 twins of quest targets removed (killing them counted for no "
                                   "quest), still archived in offline_classic165_removed_mobs: arawnite messenger (1), "
                                   "cornwall hunter (1)"])
        self.assertEqual(self.mobs() & {"twin", "hunter-twin"}, set())
        self.assertEqual({r[0] for r in self.q(f"SELECT Mob_ID FROM {spawns.TRACK}")},
                         {"far", "other-region", "guard-copy", "rhodri-copy", "spider-copy"})
        self.assertEqual(self.q(f"SELECT COUNT(*) FROM {spawns.ARCHIVE} WHERE Mob_ID IN ('twin', 'hunter-twin')"), [(2,)])
        self.assertEqual(world_fixes.apply(self.db), [])  # a second start: nothing left to remove
        spawns.restore(self.data, 20)  # restoring again does not bring them back
        self.assertEqual(self.mobs() & {"twin", "hunter-twin"}, set())

    def test_one_twin_is_said_so(self):
        self.old_restore()
        with sqlite3.connect(self.db) as c:
            c.execute(f"DELETE FROM {spawns.TRACK} WHERE Mob_ID='hunter-twin'")
            self.assertEqual(spawns.remove_quest_twins(c), [
                "Restored spawns: 1 twin of a quest target removed (killing it counted for no quest), still archived "
                "in offline_classic165_removed_mobs: arawnite messenger (1)"])

    def test_only_tracked_rows_still_in_the_archive_are_touched(self):
        self.old_restore()
        with sqlite3.connect(self.db) as c:
            c.execute(f"DELETE FROM {spawns.TRACK} WHERE Mob_ID='twin'")  # in Mob, not tracked: upstream's or the owner's
            c.execute(f"DELETE FROM {spawns.ARCHIVE} WHERE Mob_ID='hunter-twin'")  # tracked, but not recoverable
            self.assertEqual(spawns.remove_quest_twins(c), [])
        self.assertEqual(self.mobs() & {"twin", "hunter-twin"}, {"twin", "hunter-twin"})

    def test_a_world_without_restored_spawns_is_left_alone(self):
        before = self.q("SELECT * FROM Mob ORDER BY Mob_ID")
        self.assertEqual(world_fixes.apply(self.db), [])
        self.assertEqual(self.q("SELECT * FROM Mob ORDER BY Mob_ID"), before)

    def test_a_failure_undoes_only_this_fix(self):
        self.old_restore()
        with sqlite3.connect(self.db) as c:
            c.execute("INSERT INTO ServerProperty (`Key`, Value) VALUES ('disabled_classes', '20;33')")
        with unittest.mock.patch.object(spawns, "kill_targets", side_effect=sqlite3.OperationalError("boom")):
            changes = world_fixes.apply(self.db)
        self.assertEqual(changes, ["Disciple (Necromancer's base class) enabled: disabled_classes 20;33 -> 33",
                                   "Restored spawns: twins of quest targets not removed (boom); they stay in the world"])
        self.assertEqual(self.mobs() & {"twin", "hunter-twin"}, {"twin", "hunter-twin"})
        self.assertEqual(self.q("SELECT Value FROM ServerProperty"), [("33",)])

    def old_restore(self):
        """What a restore before this fix added: every archived row here, the twins too."""
        with sqlite3.connect(self.db) as c:
            c.execute(f"CREATE TABLE {spawns.TRACK} (Mob_ID TEXT PRIMARY KEY, PatchId TEXT NOT NULL, "
                      "MaxLevel INTEGER NOT NULL, RestoredUtc TEXT NOT NULL)")
            c.execute(f"INSERT INTO Mob SELECT * FROM {spawns.ARCHIVE}")
            c.execute(f"INSERT INTO {spawns.TRACK} SELECT Mob_ID, ?, 20, '2026-10-09T12:00:00Z' FROM {spawns.ARCHIVE}",
                      (spawns.PATCH_ID,))


MESSENGER_TWIN = "aaf45112-80d0-46df-8d41-d5436c3a8bc5"  # "arawnite messenger", 170 units from the quest's
CORNWALL_TWINS = {"0f41c0c4-298b-489e-bfb6-735dd980fc59", "417d0c59-cc11-4a25-af55-99250f29032d",
                  "4c05d5da-792b-4531-be7a-17b4f75f3307", "5f39c2bd-9d47-4ab3-9570-9763074be965",
                  "a5be35d0-e479-462f-9790-3d4bdba179c3", "d045850f-a142-46f6-b1cb-dddfeaaac864"}
LONE_CORNWALL_HUNTER = "24065847-547f-4eb1-862c-73092a2b01ea"  # 1,890 units from the nearest quest Cornwall hunter


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class SpawnsWorldTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = self.tmp.name
        self.db = os.path.join(self.data, "world", "opendaoc.sqlite3.db")
        os.makedirs(os.path.dirname(self.db))
        shutil.copyfile(TEST_WORLD, self.db)
        self.original = mob_ids(self.db)

    def tearDown(self):
        self.tmp.cleanup()

    def test_restore_adds_only_archived_home_region_leveling_monsters(self):
        n = spawns.restore(self.data, 20)
        added = mob_ids(self.db) - self.original
        self.assertEqual(len(added), n)
        self.assertGreater(n, 9000)
        with sqlite3.connect(self.db) as c:
            c.execute("CREATE TEMP TABLE added (Mob_ID TEXT PRIMARY KEY)")
            c.executemany("INSERT INTO added VALUES (?)", [(i,) for i in added])
            outside = c.execute("""
                SELECT count(*) FROM Mob m JOIN added a ON a.Mob_ID = m.Mob_ID
                LEFT JOIN NpcTemplate t ON t.TemplateId = m.NPCTemplateID
                WHERE m.Region NOT IN (1, 100, 200) OR m.Realm <> 0 OR m.Name <> lower(m.Name)
                   OR CASE WHEN t.ReplaceMobValues = 1 AND CAST(t.Level AS INTEGER) > 0
                           THEN CAST(t.Level AS INTEGER) ELSE m.Level END NOT BETWEEN 1 AND 20
                   OR m.Mob_ID NOT IN (SELECT Mob_ID FROM offline_classic165_removed_mobs)""").fetchone()[0]
        self.assertEqual(outside, 0)
        with open(os.path.join(self.data, "spawns.json"), encoding="utf-8") as f:
            self.assertEqual(json.load(f)["max_level"], 20)

    def test_restore_twice_changes_nothing(self):
        spawns.restore(self.data, 20)
        first = mob_ids(self.db)
        spawns.restore(self.data, 20)
        self.assertEqual(mob_ids(self.db), first)

    def test_lower_max_level_resets_to_exactly_that_range(self):
        spawns.restore(self.data, 10)
        only_ten = mob_ids(self.db)
        spawns.undo(self.data)
        spawns.restore(self.data, 20)
        spawns.restore(self.data, 10)
        self.assertEqual(mob_ids(self.db), only_ten)

    def test_undo_returns_the_original_mob_table(self):
        n = spawns.restore(self.data, 20)
        self.assertEqual(spawns.undo(self.data), n)
        self.assertEqual(mob_ids(self.db), self.original)
        self.assertFalse(os.path.exists(os.path.join(self.data, "spawns.json")))

    def test_status(self):
        self.assertEqual(spawns.status(self.data)["enabled"], False)
        n = spawns.restore(self.data, 20)
        st = spawns.status(self.data)
        self.assertEqual((st["enabled"], st["max_level"], st["restored"]), (True, 20, n))

    def test_reapply_restores_the_saved_setting_on_a_fresh_world(self):
        n = spawns.restore(self.data, 20)
        shutil.copyfile(TEST_WORLD, self.db)  # e.g. after new-world or upgrade-world
        self.assertEqual(spawns.reapply(self.data, log=lambda *a: None), n)
        self.assertEqual(len(mob_ids(self.db) - self.original), n)

    def test_reapply_does_nothing_when_not_enabled(self):
        self.assertIsNone(spawns.reapply(self.data, log=lambda *a: None))
        self.assertEqual(mob_ids(self.db), self.original)

    def test_the_restore_leaves_out_the_twins_of_quest_targets(self):
        self.assertEqual(spawns.restore(self.data, 20), 12873)
        self.assertNotIn(MESSENGER_TWIN, mob_ids(self.db))
        self.assertEqual(spawns.restore(self.data, 25), 14840)  # the Cornwall hunters' template makes them level 23
        added = mob_ids(self.db) - self.original
        self.assertEqual(added & ({MESSENGER_TWIN} | CORNWALL_TWINS), set())
        self.assertIn(LONE_CORNWALL_HUNTER, added)

    def test_no_restored_row_stands_near_a_quest_target_named_like_it_but_for_case(self):
        spawns.restore(self.data, 50)
        with sqlite3.connect(self.db) as c:
            targets = spawns.kill_targets(c)
            live = {}
            for name, region, x, y in c.execute(f"SELECT Name, Region, X, Y FROM Mob WHERE Mob_ID NOT IN "
                                                f"(SELECT Mob_ID FROM {spawns.TRACK})"):
                if name.lower() in targets:
                    live.setdefault((name.lower(), region), []).append((name, x, y))
            restored = c.execute(f"SELECT Mob_ID, Name, Region, X, Y FROM Mob WHERE Mob_ID IN "
                                 f"(SELECT Mob_ID FROM {spawns.TRACK})").fetchall()
        self.assertGreater(len(targets), 600)
        twins = [mob_id for mob_id, name, region, x, y in restored
                 for other, ox, oy in live.get((name.lower(), region), ())
                 if other != name and (ox - x) ** 2 + (oy - y) ** 2 <= 1000 ** 2]
        self.assertEqual(twins, [])

    def test_the_world_fix_removes_the_twins_an_earlier_restore_added(self):
        spawns.restore(self.data, 25)
        twins = {MESSENGER_TWIN} | CORNWALL_TWINS
        with sqlite3.connect(self.db) as c:  # what a restore before the fix added too
            archive = {r[1] for r in c.execute(f'PRAGMA table_info("{spawns.ARCHIVE}")')}
            cols = ", ".join(f'"{r[1]}"' for r in c.execute('PRAGMA table_info("Mob")') if r[1] in archive)
            marks = ", ".join("?" * len(twins))
            c.execute(f"INSERT INTO Mob ({cols}) SELECT {cols} FROM {spawns.ARCHIVE} WHERE Mob_ID IN ({marks})", sorted(twins))
            c.executemany(f"INSERT INTO {spawns.TRACK} VALUES (?, ?, 25, '2026-10-09T12:00:00Z')",
                          [(mob_id, spawns.PATCH_ID) for mob_id in sorted(twins)])
        before = mob_ids(self.db)
        restored = {r[0] for r in sqlite3.connect(self.db).execute(f"SELECT Mob_ID FROM {spawns.TRACK}")}
        line = ("Restored spawns: 7 twins of quest targets removed (killing them counted for no quest), still archived "
                "in offline_classic165_removed_mobs: arawnite messenger (1), cornwall hunter (6)")
        self.assertIn(line, world_fixes.apply(self.db))
        self.assertEqual((before - mob_ids(self.db)) & restored, twins)
        self.assertEqual(spawns.status(self.data)["restored"], 14840)
        self.assertFalse([change for change in world_fixes.apply(self.db) if change.startswith("Restored spawns")])

    def test_cli(self):
        run = lambda *a: subprocess.run([sys.executable, os.path.join(BIN, "spawns.py"), "--data", self.data, *a],  # noqa: E731
                                        capture_output=True, text=True)
        r = run("restore", "--max-level", "20")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Restored", r.stdout)
        self.assertIn("max_level", run("status").stdout)
        self.assertEqual(run("undo").returncode, 0)


if __name__ == "__main__":
    unittest.main()
