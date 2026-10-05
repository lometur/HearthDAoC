import glob
import os
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "bin"))

import backup  # noqa: E402


def make_world(data, rows=3):
    os.makedirs(os.path.join(data, "world"), exist_ok=True)
    c = sqlite3.connect(os.path.join(data, "world", "opendaoc.sqlite3.db"))
    c.execute("PRAGMA journal_mode=WAL")
    c.execute("CREATE TABLE t (v INTEGER)")
    c.executemany("INSERT INTO t VALUES (?)", [(i,) for i in range(rows)])
    c.commit()
    return c  # keep open so the WAL stays in use, like the running server


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def test_backup_is_consistent_while_writer_is_open(self):
        live = make_world(self.data, rows=5)
        live.execute("INSERT INTO t VALUES (99)")
        live.commit()  # committed into the WAL, not yet checkpointed
        path = backup.create(self.data)
        live.close()
        with sqlite3.connect(path) as c:
            self.assertEqual(c.execute("SELECT count(*) FROM t").fetchone()[0], 6)
        self.assertTrue(path.endswith("-backup.db"))

    def test_rotation_keeps_newest_and_ignores_other_labels(self):
        make_world(self.data).close()
        pre = backup.create(self.data, label="pre-restore")
        made = [backup.create(self.data, keep=3) for _ in range(5)]
        kept = sorted(glob.glob(os.path.join(self.data, "backups", "*-backup.db")))
        self.assertEqual(kept, sorted(made[-3:]))
        self.assertTrue(os.path.exists(pre))

    def test_failed_backup_keeps_old_backups(self):
        make_world(self.data).close()
        old = [backup.create(self.data, keep=7) for _ in range(2)]
        with open(os.path.join(self.data, "world", "opendaoc.sqlite3.db"), "wb") as f:
            f.write(b"this is not a database")
        for p in ("-wal", "-shm"):
            path = os.path.join(self.data, "world", "opendaoc.sqlite3.db" + p)
            if os.path.exists(path):
                os.remove(path)
        with self.assertRaises(Exception):
            backup.create(self.data, keep=1)
        self.assertTrue(all(os.path.exists(p) for p in old))
        self.assertEqual(glob.glob(os.path.join(self.data, "backups", "*.part")), [])

    def test_daily_backup_is_due_only_when_the_newest_is_old(self):
        # Restarting the container must not add a backup each time (rotation would drop older days).
        make_world(self.data).close()
        self.assertEqual(backup.seconds_until_due(self.data, 86400), 0)
        path = backup.create(self.data)
        self.assertGreater(backup.seconds_until_due(self.data, 86400), 86000)
        old = os.path.getmtime(path) - 90000
        os.utime(path, (old, old))
        self.assertEqual(backup.seconds_until_due(self.data, 86400), 0)


if __name__ == "__main__":
    unittest.main()
