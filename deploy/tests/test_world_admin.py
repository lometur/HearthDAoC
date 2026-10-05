import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

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

QUIET = lambda *a, **k: None  # noqa: E731
TEST_WORLD = os.environ.get("ODC_TEST_WORLD")
TOOLS = os.environ.get("ODC_TOOLS")


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

    def test_status(self):
        db = self.make_sqlite_world()
        st = world_admin.status(self.data)
        self.assertEqual((st["edition"], st["version"]), ("classic", "test"))
        self.assertIn("latest_backup", st)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs ODC_TEST_WORLD (clean classic world) and ODC_TOOLS (built CLIs)")
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

    @unittest.skipUnless(TEST_WORLD, "needs ODC_TEST_WORLD (a clean classic world)")
    def test_new_world_reapplies_restored_spawns(self):
        init_world.init(FakeRelease("test", TEST_WORLD), self.data, "classic", skip_navmesh=True, log=QUIET)
        n = spawns.restore(self.data, 20)
        world_admin.new_world(FakeRelease("test", TEST_WORLD), self.data, "classic", skip_navmesh=True, log=QUIET)
        self.assertEqual(spawns.status(self.data)["restored"], n)

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs ODC_TEST_WORLD (clean classic world) and ODC_TOOLS (built CLIs)")
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

    @unittest.skipUnless(TEST_WORLD and TOOLS, "needs ODC_TEST_WORLD (clean classic world) and ODC_TOOLS (built CLIs)")
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

    def test_upgrade_world_refuses_same_version_by_default(self):
        self.make_sqlite_world(version="0.34b")
        with self.assertRaisesRegex(world_admin.AdminError, "already"):
            world_admin.upgrade_world(FakeRelease("0.34b", "/nonexistent"), self.data, ["true"], log=QUIET)


if __name__ == "__main__":
    unittest.main()
