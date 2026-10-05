import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "linux", "tests"))
import release_fixture as fx  # noqa: E402

SETUP = os.path.join(REPO, "client", "linux", "setup.sh")


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.lock, self.files = fx.build(self.dir)
        self.base = os.path.join(self.dir, "base")
        os.makedirs(self.base)
        for name, data in (("connect.exe", self.files["runtime/client-opendaoc/app/connect.exe"]),
                           ("game1127.dll", b"scaling"), ("game.dll", b"stock"), ("paths.dat", b"[paths]\r\nsettings=Atlas1")):
            with open(os.path.join(self.base, name), "wb") as f:
                f.write(data)

    def tearDown(self):
        self.tmp.cleanup()

    def run_setup(self, srv, *extra):
        lock_path = os.path.join(self.dir, "upstream.lock")
        with open(lock_path, "w") as f:
            json.dump(srv.lock(self.lock), f)
        dest = os.path.join(self.dir, "dest")
        args = ["bash", SETUP, "--server", "192.168.1.64:10301", "--edition", "classic",
                "--base-client", self.base, "--dest", dest, "--lock", lock_path, *extra]
        return dest, subprocess.run(args, capture_output=True, text=True)

    def test_setup_builds_verified_client_and_play_script(self):
        with fx.RangeServer(self.dir) as srv:
            dest, r = self.run_setup(srv)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        with open(os.path.join(dest, "client", "game.dll"), "rb") as f:
            self.assertEqual(f.read(), self.files["editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"])
        with open(os.path.join(dest, "client", "paths.dat"), "rb") as f:
            self.assertEqual(f.read(), self.files["runtime/client-opendaoc/app/paths.dat"])
        with open(os.path.join(self.base, "game.dll"), "rb") as f:
            self.assertEqual(f.read(), b"stock")  # base client untouched
        play = os.path.join(dest, "play.sh")
        self.assertTrue(os.stat(play).st_mode & stat.S_IXUSR)
        with open(play, encoding="utf-8") as f:
            text = f.read()
        self.assertIn('SERVER="${OFFLINEDAOC_SERVER:-192.168.1.64:10301}"', text)
        self.assertNotIn("@SERVER@", text)
        self.assertEqual(subprocess.run(["bash", "-n", play]).returncode, 0)

    def test_rejects_bad_arguments(self):
        with fx.RangeServer(self.dir) as srv:
            for bad in (["--server", "no-port"], ["--edition", "gold"]):
                args = ["bash", SETUP, "--server", "h:1", "--edition", "classic", "--base-client", self.base, *bad]
                r = subprocess.run(args, capture_output=True, text=True)
                self.assertEqual(r.returncode, 2, bad)
            not_client = os.path.join(self.dir, "empty")
            os.makedirs(not_client)
            r = subprocess.run(["bash", SETUP, "--server", "h:1", "--edition", "classic", "--base-client", not_client],
                               capture_output=True, text=True)
            self.assertEqual(r.returncode, 2)
            self.assertIn("doesn't look like a 1.127 client", r.stderr)


if __name__ == "__main__":
    unittest.main()
