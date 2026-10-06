import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import region_ports  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")


def make_world(path, ports=(10400, 10400, 10400)):
    with sqlite3.connect(path) as c:
        c.execute("CREATE TABLE Regions (RegionID INT PRIMARY KEY, IP TEXT, Port INT)")
        c.executemany("INSERT INTO Regions VALUES (?, '127.0.0.1', ?)", list(enumerate(ports, 1)))


def ports(path):
    with sqlite3.connect(path) as c:
        return sorted({r[0] for r in c.execute("SELECT Port FROM Regions")})


class RegionPortTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")

    def tearDown(self):
        self.tmp.cleanup()

    def test_every_region_gets_the_server_udp_port(self):
        make_world(self.db, ports=(10400, 10400, 10401))
        self.assertEqual(region_ports.align(self.db, 10401), 2)
        self.assertEqual(ports(self.db), [10401])

    def test_second_run_changes_nothing(self):
        make_world(self.db)
        region_ports.align(self.db, 10401)
        self.assertEqual(region_ports.align(self.db, 10401), 0)

    def test_world_without_regions_is_refused(self):
        sqlite3.connect(self.db).close()
        with self.assertRaisesRegex(region_ports.RegionPortError, "Regions"):
            region_ports.align(self.db, 10401)

    def test_cli(self):
        make_world(self.db)
        run = lambda *a: subprocess.run([sys.executable, os.path.join(BIN, "region_ports.py"), "--db", self.db, *a],  # noqa: E731
                                        capture_output=True, text=True)
        r = run("--port", "10401")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("3 regions", r.stdout)
        self.assertEqual(run("--port", "70000").returncode, 2)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertEqual(ports(self.db), [10400])  # what upstream ships
        self.assertEqual(region_ports.align(self.db, 10401), 354)
        self.assertEqual(ports(self.db), [10401])


if __name__ == "__main__":
    unittest.main()
