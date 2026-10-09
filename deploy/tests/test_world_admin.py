import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path[:0] = [os.path.join(REPO, "deploy", "bin"), os.path.join(REPO, "tools", "linux"),
                os.path.join(REPO, "tools", "linux", "accounts"), os.path.join(REPO, "tools", "linux", "tests")]

import accounts  # noqa: E402
import backup  # noqa: E402
import init_world  # noqa: E402
import release_fixture as fx  # noqa: E402
import spawns  # noqa: E402
import world_admin  # noqa: E402
from odaoc_fetch import Release  # noqa: E402
from tests import test_carry_rvr  # noqa: E402

QUIET = lambda *a, **k: None  # noqa: E731
TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
TOOLS = os.environ.get("HDC_TOOLS")


class FakeRelease:
    """Stands in for Release in upgrade tests: 'downloads' the given clean world file."""

    def __init__(self, version, clean_world):
        self.version = version
        self.lock = {"editions": {"classic": {"world_db": "w"}, "b": {"world_db": "w"}}}
        self._clean = clean_world

    def edition(self, name):
        return self.lock["editions"][name]

    def extract(self, rel, dest):
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(self._clean, dest)
        return "ok"


def importer_cmd(data):
    cmd = ["env", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0", "dotnet",
           os.path.join(TOOLS, "progress-import", "progress-import.dll")]
    busy = subprocess.run(["ss", "-ltn", "sport = :10300"], capture_output=True, text=True).stdout.count("10300")
    if busy:  # the upstream engine refuses while anything listens on 10300 or a CoreServer process runs
        cmd = ["bwrap", "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc",
               "--bind", data, data, "--unshare-net", "--unshare-pid"] + cmd
    return cmd


class WorldAdminTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.data = os.path.join(self.dir, "data")

    def tearDown(self):
        self.tmp.cleanup()

    def make_sqlite_world(self, edition="classic", version="test"):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db), exist_ok=True)
        with sqlite3.connect(db) as c:
            c.execute("CREATE TABLE t (v TEXT)")
            c.execute("INSERT INTO t VALUES ('original')")
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": version, "edition": edition, "navmesh": False, "created_utc": "x"})
        return db

    def test_restore(self):
        db = self.make_sqlite_world()
        saved = backup.create(self.data)
        with sqlite3.connect(db) as c:
            c.execute("UPDATE t SET v='changed'")
        world_admin.restore(self.data, os.path.basename(saved))
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "original")
        self.assertEqual(len([f for f in os.listdir(os.path.join(self.data, "backups")) if f.endswith("-pre-restore.db")]), 1)

    def test_restore_recovers_a_damaged_live_world(self):
        db = self.make_sqlite_world()
        saved = backup.create(self.data)
        with open(db, "wb") as f:
            f.write(b"damaged")
        kept = world_admin.restore(self.data, os.path.basename(saved))
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "original")
        with open(kept, "rb") as f:
            self.assertEqual(f.read(), b"damaged")  # the damaged file is kept, not deleted

    def test_restore_recovers_a_missing_live_world(self):
        db = self.make_sqlite_world()
        saved = backup.create(self.data)
        os.remove(db)
        world_admin.restore(self.data, os.path.basename(saved))
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "original")

    def test_restore_from_a_wal_mode_world_keeps_its_last_writes(self):
        self.make_sqlite_world()
        src = os.path.join(self.data, "archive", "world-old", "world", "opendaoc.sqlite3.db")
        os.makedirs(os.path.dirname(src))
        writer = sqlite3.connect(src)
        writer.execute("PRAGMA journal_mode=WAL")
        writer.execute("CREATE TABLE t (v TEXT)")
        writer.execute("INSERT INTO t VALUES ('archived last session')")
        writer.commit()  # still only in the -wal while this connection stays open
        try:
            world_admin.restore(self.data, src)
        finally:
            writer.close()
        with sqlite3.connect(init_world.world_paths(self.data)["db"]) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "archived last session")

    def _backup_with_edition(self, sluaghbinder):
        path = os.path.join(backup.backups_dir(self.data), "world-20990101-000000-000000-manual.db")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with sqlite3.connect(path) as c:
            c.execute("CREATE TABLE t (v TEXT)")
            c.execute("INSERT INTO t VALUES ('from backup')")
            c.execute("CREATE TABLE ServerProperty (`Key` TEXT, Value TEXT)")
            c.execute("INSERT INTO ServerProperty VALUES ('enable_sluaghbinder', ?)", (sluaghbinder,))
        return os.path.basename(path)

    def test_restore_refuses_a_backup_from_another_edition(self):
        db = self.make_sqlite_world(edition="classic")
        name = self._backup_with_edition("True")  # an edition "b" world
        with self.assertRaisesRegex(world_admin.AdminError, "edition 'b'.*--force"):
            world_admin.restore(self.data, name)
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "original")

    def test_restore_with_force_accepts_another_edition(self):
        db = self.make_sqlite_world(edition="classic")
        world_admin.restore(self.data, self._backup_with_edition("True"), force=True)
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "from backup")

    def test_restore_of_the_same_edition_needs_no_force(self):
        db = self.make_sqlite_world(edition="classic")
        world_admin.restore(self.data, self._backup_with_edition("False"))
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT v FROM t").fetchone()[0], "from backup")

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world)")
    def test_edition_is_read_from_a_real_world(self):
        self.assertEqual(world_admin.world_edition(TEST_WORLD), "classic")

    def test_restore_rejects_a_corrupt_backup(self):
        self.make_sqlite_world()
        os.makedirs(os.path.join(self.data, "backups"), exist_ok=True)
        bad = os.path.join(self.data, "backups", "bad.db")
        with open(bad, "wb") as f:
            f.write(b"junk")
        with self.assertRaisesRegex(world_admin.AdminError, "integrity"):
            world_admin.restore(self.data, "bad.db")

    def test_new_world_archives_and_switches_edition(self):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            rel = Release(srv.lock(lock), retries=1, backoff=0)
            init_world.init(rel, self.data, "classic", skip_navmesh=True, log=QUIET)
            archive = world_admin.new_world(rel, self.data, "b", skip_navmesh=True, log=QUIET)
        with open(init_world.world_paths(self.data)["db"], "rb") as f:
            self.assertEqual(f.read(), files["runtime/data/opendaoc.sqlite3.db"])
        with open(init_world.world_paths(self.data)["meta"], encoding="utf-8") as f:
            self.assertEqual(json.load(f)["edition"], "b")
        self.assertTrue(os.path.isfile(os.path.join(archive, "world", "opendaoc.sqlite3.db")))

    def test_new_world_failure_puts_the_old_world_back(self):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            init_world.init(Release(srv.lock(lock), retries=1, backoff=0), self.data, "classic", skip_navmesh=True, log=QUIET)
            db = init_world.world_paths(self.data)["db"]
            with open(db, "rb") as f:
                before = f.read()
            srv.fail = lambda path, rng: True
            with self.assertRaisesRegex(world_admin.AdminError, "back in place"):
                world_admin.new_world(Release(srv.lock(lock), retries=1, backoff=0), self.data, "b",
                                      skip_navmesh=True, log=QUIET)
        with open(db, "rb") as f:
            self.assertEqual(f.read(), before)
        with open(init_world.world_paths(self.data)["meta"], encoding="utf-8") as f:
            self.assertEqual(json.load(f)["edition"], "classic")
        self.assertEqual(os.listdir(os.path.join(self.data, "archive")), [])

    def _new_world_interrupted_by(self, exc):
        lock, files = fx.build(self.dir)
        with fx.RangeServer(self.dir) as srv:
            init_world.init(Release(srv.lock(lock), retries=1, backoff=0), self.data, "classic", skip_navmesh=True, log=QUIET)

        class Interrupted(FakeRelease):
            def extract(self, rel, dest):
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                with open(dest + ".part", "wb") as f:
                    f.write(b"partial")
                raise exc
        db = init_world.world_paths(self.data)["db"]
        with open(db, "rb") as f:
            before = f.read()
        return Interrupted("test", "/nonexistent"), db, before

    def test_new_world_interrupted_by_ctrl_c_puts_the_old_world_back(self):
        rel, db, before = self._new_world_interrupted_by(KeyboardInterrupt())
        with self.assertRaises(KeyboardInterrupt):
            world_admin.new_world(rel, self.data, "b", skip_navmesh=True, log=QUIET)
        with open(db, "rb") as f:
            self.assertEqual(f.read(), before)
        self.assertEqual(os.listdir(os.path.join(self.data, "archive")), [])

    def test_new_world_disk_full_puts_the_old_world_back(self):
        rel, db, before = self._new_world_interrupted_by(OSError(28, "No space left on device"))
        with self.assertRaisesRegex(world_admin.AdminError, "back in place"):
            world_admin.new_world(rel, self.data, "b", skip_navmesh=True, log=QUIET)
        with open(db, "rb") as f:
            self.assertEqual(f.read(), before)

    def test_restore_command_reports_success_when_the_live_world_was_missing(self):
        db = self.make_sqlite_world()
        saved = backup.create(self.data)
        os.remove(db)
        r = subprocess.run([sys.executable, os.path.join(REPO, "deploy", "bin", "world_admin.py"), "--data", self.data,
                            "--lock", "/nonexistent.lock", "restore", os.path.basename(saved)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Restored", r.stdout)

    def test_status(self):
        db = self.make_sqlite_world()
        st = world_admin.status(self.data)
        self.assertEqual((st["edition"], st["version"]), ("classic", "test"))
        self.assertIn("latest_backup", st)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs HDC_TEST_WORLD (clean classic world) and HDC_TOOLS (built CLIs)")
    def test_upgrade_world_keeps_accounts_passwords_and_privilege(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": "0.34b", "edition": "classic", "navmesh": False, "created_utc": "x"})
        conn = accounts.connect(db)
        accounts.create(conn, "Admin1", "adminpw", plvl=3)
        accounts.create(conn, "Player2", "playerpw")
        before = dict(conn.execute("SELECT Name, Password FROM Account").fetchall())
        conn.close()
        archive = world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                            importer_cmd(self.data), same_version_ok=True, log=QUIET)
        with sqlite3.connect(db) as c:
            after = dict(c.execute("SELECT Name, Password FROM Account").fetchall())
            plvl = dict(c.execute("SELECT Name, PrivLevel FROM Account").fetchall())
            self.assertEqual(c.execute("PRAGMA integrity_check").fetchone()[0], "ok")
        self.assertEqual(after, before)  # every account kept, passwords unchanged
        self.assertEqual(plvl, {"Admin1": 3, "Player2": 1})  # admin rights restored after the importer reset them
        self.assertTrue(os.path.isfile(os.path.join(archive, "world", "opendaoc.sqlite3.db")))

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world)")
    def test_new_world_reapplies_restored_spawns(self):
        init_world.init(FakeRelease("test", TEST_WORLD), self.data, "classic", skip_navmesh=True, log=QUIET)
        n = spawns.restore(self.data, 20)
        world_admin.new_world(FakeRelease("test", TEST_WORLD), self.data, "classic", skip_navmesh=True, log=QUIET)
        self.assertEqual(spawns.status(self.data)["restored"], n)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs HDC_TEST_WORLD (clean classic world) and HDC_TOOLS (built CLIs)")
    def test_upgrade_world_reapplies_restored_spawns(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": "0.34b", "edition": "classic", "navmesh": False, "created_utc": "x"})
        n = spawns.restore(self.data, 20)
        world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                  importer_cmd(self.data), same_version_ok=True, log=QUIET)
        self.assertEqual(spawns.status(self.data)["restored"], n)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs HDC_TEST_WORLD (clean classic world) and HDC_TOOLS (built CLIs)")
    def test_upgrade_world_keeps_bans_and_permissions_and_reports_changed_settings(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": "0.34b", "edition": "classic", "navmesh": False, "created_utc": "x"})
        conn = accounts.connect(db)
        accounts.create(conn, "Griefer1", "pw")
        with conn:
            conn.execute("INSERT INTO Ban (Author, Type, Account, Reason, Ban_ID) VALUES ('admin', 'A', 'Griefer1', 'test', 'ban-1')")
            conn.execute("INSERT INTO SinglePermission (PlayerID, Command, SinglePermission_ID) VALUES ('Griefer1', '&summon', 'perm-1')")
            conn.execute("UPDATE ServerProperty SET Value='Welcome to the hearth' WHERE Key='motd'")
        conn.close()
        archive = world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                            importer_cmd(self.data), same_version_ok=True, log=QUIET)
        with sqlite3.connect(db) as c:
            self.assertEqual(c.execute("SELECT count(*) FROM Ban WHERE Ban_ID='ban-1'").fetchone()[0], 1)
            self.assertEqual(c.execute("SELECT count(*) FROM SinglePermission WHERE SinglePermission_ID='perm-1'").fetchone()[0], 1)
        with open(os.path.join(archive, "upgrade-report.txt"), encoding="utf-8") as f:
            report = f.read()
        self.assertIn("motd", report)
        self.assertIn("Welcome to the hearth", report)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs HDC_TEST_WORLD (clean classic world) and HDC_TOOLS (built CLIs)")
    def test_upgrade_failure_during_the_swap_puts_the_old_world_back(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        meta = {"version": "0.34b", "edition": "classic", "navmesh": False, "created_utc": "x"}
        init_world.write_meta(init_world.world_paths(self.data)["meta"], meta)
        before = backup.sha256(db) if hasattr(backup, "sha256") else open(db, "rb").read()
        with mock.patch.object(world_admin.init_world, "write_meta", side_effect=OSError(28, "No space left on device")):
            with self.assertRaisesRegex(world_admin.AdminError, "back in place"):
                world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                          importer_cmd(self.data), same_version_ok=True, log=QUIET)
        after = backup.sha256(db) if hasattr(backup, "sha256") else open(db, "rb").read()
        self.assertEqual(after, before)
        with open(init_world.world_paths(self.data)["meta"], encoding="utf-8") as f:
            self.assertEqual(json.load(f), meta)
        self.assertEqual(os.listdir(os.path.join(self.data, "archive")), [])
        self.assertFalse(os.path.exists(init_world.swap_marker(self.data)))

    def test_upgrade_world_refuses_same_version_by_default(self):
        self.make_sqlite_world(version="0.34b")
        with self.assertRaisesRegex(world_admin.AdminError, "already"):
            world_admin.upgrade_world(FakeRelease("0.34b", "/nonexistent"), self.data, ["true"], log=QUIET)

    def _real_world(self):
        db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(db))
        shutil.copyfile(TEST_WORLD, db)
        os.chmod(db, 0o644)
        meta = {"version": "0.34b", "edition": "classic", "navmesh": False, "created_utc": "x"}
        init_world.write_meta(init_world.world_paths(self.data)["meta"], meta)
        return db, meta

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs HDC_TEST_WORLD (clean classic world) and HDC_TOOLS (built CLIs)")
    def test_upgrade_world_carries_keep_door_and_relic_state(self):
        db, _ = self._real_world()
        test_carry_rvr.execute(db, "UPDATE Keep SET Realm=2, ClaimedGuildName='Hearth Raiders' WHERE KeepID=50",
                               "UPDATE Door SET Health=1200, State=0 WHERE InternalID=15110601",
                               "UPDATE Relic SET Region=100, X=772136, Y=626640, Realm=2, LastRealm=2 WHERE RelicID=30",
                               "INSERT INTO KeepCaptureLog (DateTaken, KeepName, CapturedBy) "
                               "VALUES ('2026-10-08 21:00:00', 'Caer Benowyc', 'Midgard')")
        archive = world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                            importer_cmd(self.data), same_version_ok=True, log=QUIET)
        q = lambda sql: test_carry_rvr.query(db, sql)  # noqa: E731
        self.assertEqual(q("SELECT Realm, ClaimedGuildName FROM Keep WHERE KeepID=50"), [(2, "Hearth Raiders")])
        self.assertEqual(q("SELECT Health, State FROM Door WHERE InternalID=15110601"), [(1200, 0)])
        self.assertEqual(q("SELECT Region, Realm FROM Relic WHERE RelicID=30"), [(100, 2)])
        self.assertEqual(q("SELECT KeepName, CapturedBy FROM KeepCaptureLog"), [("Caer Benowyc", "Midgard")])
        with open(os.path.join(archive, "upgrade-report.txt"), encoding="utf-8") as f:
            report = f.read()
        self.assertIn("Carried over: {'Ban': 0, 'SinglePermission': 0, 'Keep': 1, 'Door': 4, 'Relic': 1, "
                      "'KeepHookPointItem': 0, 'KeepCaptureLog': 1}\n"
                      "Keeps matched by name and region: 81, of which 1 in play (held by another realm than their own, "
                      "or claimed) and carried\nRelics matched: 6, of which 1 away from home and carried\n", report)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs HDC_TEST_WORLD (clean classic world) and HDC_TOOLS (built CLIs)")
    def test_upgrade_stops_with_the_world_unchanged_when_the_carry_fails(self):
        db, meta = self._real_world()
        with open(db, "rb") as f:
            before = f.read()
        with mock.patch.object(world_admin.carry_rvr, "_carry_relics", side_effect=sqlite3.OperationalError("disk full")):
            with self.assertRaisesRegex(world_admin.AdminError, "keep, door and relic state failed .*disk full.*unchanged"):
                world_admin.upgrade_world(FakeRelease("0.34b", TEST_WORLD), self.data,
                                          importer_cmd(self.data), same_version_ok=True, log=QUIET)
        with open(db, "rb") as f:
            self.assertEqual(f.read(), before)
        with open(init_world.world_paths(self.data)["meta"], encoding="utf-8") as f:
            self.assertEqual(json.load(f), meta)
        self.assertFalse(os.path.exists(os.path.join(self.data, "archive")))


class CarryRvrCommandTests(unittest.TestCase):
    """world_admin.py carry-rvr: list the archived worlds, or carry one's keep, door and relic state into the live world."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = os.path.join(self.tmp.name, "data")
        self.db = init_world.world_paths(self.data)["db"]
        os.makedirs(os.path.dirname(self.db))
        test_carry_rvr.make_world(self.db)
        init_world.write_meta(init_world.world_paths(self.data)["meta"],
                              {"version": "0.35b", "edition": "classic", "navmesh": False, "created_utc": "x"})

    def tearDown(self):
        self.tmp.cleanup()

    def archive(self, name, version="0.34b"):
        """An archived world as _archive_world leaves it, with Caer Benowyc held by Midgard; returns its database."""
        db = os.path.join(self.data, "archive", name, "world", "opendaoc.sqlite3.db")
        os.makedirs(os.path.dirname(db))
        test_carry_rvr.make_world(db)
        test_carry_rvr.execute(db, "UPDATE Keep SET Realm=2, ClaimedGuildName='Raiders' WHERE KeepID=50",
                               "UPDATE Door SET Health=1000, State=0 WHERE Door_ID='d1'")
        init_world.write_meta(os.path.join(self.data, "archive", name, "world.json"),
                              {"version": version, "edition": "classic", "navmesh": True, "created_utc": "x"})
        return db

    def run_admin(self, *args):
        return subprocess.run([sys.executable, os.path.join(REPO, "deploy", "bin", "world_admin.py"), "--data", self.data,
                               "--lock", "/nonexistent.lock", "carry-rvr", *args], capture_output=True, text=True)

    def test_without_an_archive_it_lists_them_oldest_first(self):
        self.archive("world-pre-upgrade-20261008-120000", "0.35b")
        self.archive("world-20261001-090000")
        os.makedirs(os.path.join(self.data, "archive", "world-20261005-100000"))  # no world database in it
        self.assertEqual(world_admin.archived_worlds(self.data),
                         [("world-20261001-090000", "0.34b"), ("world-pre-upgrade-20261008-120000", "0.35b")])
        r = self.run_admin()
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.splitlines(), ["world-20261001-090000                    upstream 0.34b",
                                                 "world-pre-upgrade-20261008-120000        upstream 0.35b"])
        self.assertEqual(test_carry_rvr.query(self.db, "SELECT Realm FROM Keep WHERE KeepID=50"), [(1,)])

    def test_without_archives_it_says_so(self):
        r = self.run_admin()
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("No archived worlds", r.stdout)

    def test_it_backs_up_then_carries_and_reports(self):
        archived = self.archive("world-pre-upgrade-20261008-120000")
        with open(archived, "rb") as f:
            archived_before = f.read()
        r = self.run_admin("world-pre-upgrade-20261008-120000")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.splitlines()[:3], [
            "Carried over from world-pre-upgrade-20261008-120000: {'Keep': 1, 'Door': 2, 'Relic': 0, "
            "'KeepHookPointItem': 0, 'KeepCaptureLog': 0}",
            "Keeps matched by name and region: 3, of which 1 in play (held by another realm than their own, or claimed) "
            "and carried",
            "Relics matched: 2, of which 0 away from home and carried"])
        self.assertRegex(r.stdout, r"The state before was backed up as world-.*-pre-carry-rvr\.db\.")
        self.assertEqual(test_carry_rvr.query(self.db, "SELECT Realm, ClaimedGuildName FROM Keep WHERE KeepID=50"),
                         [(2, "Raiders")])
        self.assertEqual(test_carry_rvr.query(self.db, "SELECT Health, State FROM Door WHERE Door_ID='d1'"), [(1000, 0)])
        saved = [f for f in os.listdir(backup.backups_dir(self.data)) if f.endswith("-pre-carry-rvr.db")]
        self.assertEqual(len(saved), 1)
        self.assertEqual(test_carry_rvr.query(os.path.join(backup.backups_dir(self.data), saved[0]),
                                              "SELECT Realm FROM Keep WHERE KeepID=50"), [(1,)])
        with open(archived, "rb") as f:
            self.assertEqual(f.read(), archived_before)
        self.assertEqual(sorted(os.listdir(self.data)), ["archive", "backups", "world", "world.json"])  # no copy left

    def test_a_world_database_can_be_named_by_its_path(self):
        archived = self.archive("world-20261001-090000")
        counts, _ = world_admin.carry_rvr_from_archive(self.data, archived, log=QUIET)
        self.assertEqual(counts["Keep"], 1)
        self.assertEqual(test_carry_rvr.query(self.db, "SELECT Realm FROM Keep WHERE KeepID=50"), [(2,)])

    def test_the_report_names_what_was_not_carried(self):
        archived = self.archive("world-20261001-090000")
        test_carry_rvr.execute(archived, "UPDATE Keep SET Name='Caer Gone' WHERE KeepID=50")
        counts, notes = world_admin.carry_rvr_from_archive(self.data, "world-20261001-090000", log=QUIET)
        self.assertEqual(counts["Keep"], 0)
        self.assertEqual(notes[1:3], ["Keeps only in the old world, not carried: Caer Gone (region 1)",
                                      "Keeps only in the new world, left as it ships them: Caer Benowyc (region 1)"])

    def test_a_keep_at_home_in_the_archive_keeps_its_live_state(self):
        # Only keeps in play in the archived world get their state back; a capture since of another keep stays.
        self.archive("world-20261001-090000")
        test_carry_rvr.execute(self.db, "UPDATE Keep SET Realm=1 WHERE KeepID=22")
        world_admin.carry_rvr_from_archive(self.data, "world-20261001-090000", log=QUIET)
        self.assertEqual(test_carry_rvr.query(self.db, "SELECT KeepID, Realm FROM Keep WHERE KeepID IN (22, 50) "
                                                       "ORDER BY KeepID"), [(22, 1), (50, 2)])

    def test_an_unknown_archive_changes_nothing(self):
        r = self.run_admin("world-19990101-000000")
        self.assertEqual(r.returncode, 1)
        self.assertIn("archived world not found", r.stderr)
        self.assertFalse(os.path.exists(backup.backups_dir(self.data)))

    def test_an_archive_of_another_edition_changes_nothing(self):
        archived = self.archive("world-20261001-090000")
        test_carry_rvr.execute(archived, "CREATE TABLE ServerProperty (`Key` TEXT, Value TEXT)",
                               "INSERT INTO ServerProperty VALUES ('enable_sluaghbinder', 'True')")
        with self.assertRaisesRegex(world_admin.AdminError, "edition 'b'.*nothing was changed"):
            world_admin.carry_rvr_from_archive(self.data, "world-20261001-090000", log=QUIET)
        self.assertEqual(test_carry_rvr.query(self.db, "SELECT Realm FROM Keep WHERE KeepID=50"), [(1,)])

    def test_a_failure_leaves_the_live_world_unchanged(self):
        self.archive("world-20261001-090000")
        with open(self.db, "rb") as f:
            before = f.read()
        with mock.patch.object(world_admin.carry_rvr, "_carry_relics", side_effect=sqlite3.OperationalError("disk full")):
            with self.assertRaisesRegex(world_admin.AdminError, "disk full.*the current world is unchanged"):
                world_admin.carry_rvr_from_archive(self.data, "world-20261001-090000", log=QUIET)
        with open(self.db, "rb") as f:
            self.assertEqual(f.read(), before)
        self.assertEqual(sorted(os.listdir(self.data)), ["archive", "backups", "world", "world.json"])


if __name__ == "__main__":
    unittest.main()
