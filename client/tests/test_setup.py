import hashlib
import json
import os
import shutil
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
RELEASE_DLL = "editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"
RELEASE_SPLASH = "runtime/client-opendaoc/app/pregame/splash.mpk"
SPLASH = b"stock splash"
SPLASH_NEW = b"HEARTH DAoC splash"
UNKNOWN_DLL = ("Not patched: game.dll is not the file this HearthDAoC release supports (for example the "
               "0.34b edition or a newer upstream client). The client still works with the standard "
               "creation screen.\n")
WARNING = "Warning: the client was set up without HearthDAoC's patches (see the message above).\n"
# What setup.sh installs in <dest>/patches, for play.sh to apply at every launch: exactly these.
PATCH_FILES = ["apply_patches.py", "classic-creation.json", "patchset.py", "splash.mpk"]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    with open(path, "rb") as f:
        return f.read()


def installed_patches(test, dest, source):
    """Check that <dest>/patches holds exactly PATCH_FILES, each a copy of the one in source."""
    installed = os.path.join(dest, "patches")
    test.assertEqual(sorted(os.listdir(installed)), PATCH_FILES)
    for name in PATCH_FILES:
        test.assertEqual(read(os.path.join(installed, name)), read(os.path.join(source, name)), name)


def make_base(directory, files):
    """A fake 1.127 base client folder, as the player's OpenDAoC install would be."""
    base = os.path.join(directory, "base")
    os.makedirs(base)
    for name, data in (("connect.exe", files["runtime/client-opendaoc/app/connect.exe"]),
                       ("game1127.dll", b"scaling"), ("game.dll", b"stock"), ("paths.dat", b"[paths]\r\nsettings=Atlas1")):
        with open(os.path.join(base, name), "wb") as f:
            f.write(data)
    return base


def run_setup(script, directory, base, lock, *extra):
    """Run setup.sh with lock (part URLs pointing at a RangeServer); return (dest, result)."""
    lock_path = os.path.join(directory, "upstream.lock")
    with open(lock_path, "w") as f:
        json.dump(lock, f)
    dest = os.path.join(directory, "dest")
    args = ["bash", script, "--server", "192.168.1.64:10301", "--edition", "classic",
            "--base-client", base, "--dest", dest, "--lock", lock_path, *extra]
    return dest, subprocess.run(args, capture_output=True, text=True)


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.lock, self.files = fx.build(self.dir)
        self.base = make_base(self.dir, self.files)

    def tearDown(self):
        self.tmp.cleanup()

    def run_setup(self, srv, *extra):
        return run_setup(SETUP, self.dir, self.base, srv.lock(self.lock), *extra)

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
        self.assertIn('SERVER="${HEARTHDAOC_SERVER:-192.168.1.64:10301}"', text)
        self.assertNotIn("@SERVER@", text)
        self.assertEqual(subprocess.run(["bash", "-n", play]).returncode, 0)

    def test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds(self):
        # From a checkout, setup.sh uses client/patches/. The fixture's game.dll isn't OfflineDAoC
        # 0.34 classic's, so the applier refuses it (exit 3): a warning, and the client is unchanged.
        with fx.RangeServer(self.dir) as srv:
            dest, r = self.run_setup(srv)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_DLL, r.stdout)
        self.assertIn(WARNING, r.stderr)
        self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.files[RELEASE_DLL])
        self.assertEqual([n for n in os.listdir(os.path.join(dest, "client")) if n.endswith(".hearthdaoc-orig")], [])
        installed_patches(self, dest, os.path.join(REPO, "client", "patches"))  # for play.sh, even when refused

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


class BundlePatchTests(unittest.TestCase):
    """setup.sh from an unpacked client bundle installs its patches folder as <dest>/patches and applies it."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = self.tmp.name
        files = dict(fx.DEFAULT_FILES)
        files[RELEASE_SPLASH] = SPLASH
        self.lock, self.files = fx.build(self.dir, files)
        self.base = make_base(self.dir, self.files)
        self.dll = self.files[RELEASE_DLL]
        self.dll_patched = b"HDC!" + self.dll[4:] + b"cave"
        # The bundle's layout (deploy/build_bundles.sh), two folders down so that setup.sh's
        # repository fallback (../../client/patches) finds nothing.
        self.bundle = os.path.join(self.dir, "download", "hearthdaoc-client-vtest")
        self.patches = os.path.join(self.bundle, "patches")
        os.makedirs(self.patches)
        for src in ("client/linux/setup.sh", "client/linux/play.sh.in", "tools/linux/odaoc_fetch.py"):
            shutil.copy(os.path.join(REPO, src), self.bundle)
        for name in ("apply_patches.py", "patchset.py"):
            shutil.copy(os.path.join(REPO, "client", "patches", name), self.patches)
        with open(os.path.join(self.patches, "splash.mpk"), "wb") as f:
            f.write(SPLASH_NEW)
        self.write_patchset(sha(self.dll))

    def write_patchset(self, dll_before):
        data = {
            "format": 1,
            "name": "classic-creation",
            "client": "test fixture",
            "files": [
                {"path": "game.dll", "before": dll_before, "after": sha(self.dll_patched),
                 "ops": [{"op": "replace", "offset": 0, "from": self.dll[:4].hex(), "to": b"HDC!".hex()},
                         {"op": "append", "data": b"cave".hex()}]},
                {"path": "pregame/splash.mpk", "before": sha(SPLASH), "after": "source",
                 "ops": [{"op": "file", "source": "splash.mpk"}]},
            ],
        }
        with open(os.path.join(self.patches, "classic-creation.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1)

    def setup_sh(self):
        with fx.RangeServer(self.dir) as srv:
            return run_setup(os.path.join(self.bundle, "setup.sh"), self.dir, self.base, srv.lock(self.lock))

    def test_setup_patches_the_new_client(self):
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        client = os.path.join(dest, "client")
        self.assertIn("Patched: game.dll (original saved as game.dll.hearthdaoc-orig)\n"
                      "Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)\n",
                      r.stdout)
        self.assertNotIn("Warning", r.stderr)
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
        self.assertEqual(read(os.path.join(client, "game.dll.hearthdaoc-orig")), self.dll)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH_NEW)
        self.assertTrue(os.path.isfile(os.path.join(dest, "play.sh")))
        self.assertFalse(os.path.exists(os.path.join(self.patches, "__pycache__")))

    def test_running_setup_again_ends_patched_with_one_verified_backup_per_file(self):
        self.assertEqual(self.setup_sh()[1].returncode, 0)
        dest, r = self.setup_sh()  # on the client the first run patched
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertNotIn("Warning", r.stderr)
        client = os.path.join(dest, "client")
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH_NEW)
        ours = sorted(os.path.relpath(os.path.join(folder, name), client).replace(os.sep, "/")
                      for folder, _dirs, names in os.walk(client) for name in names if "hearthdaoc" in name)
        self.assertEqual(ours, ["game.dll.hearthdaoc-orig", "pregame/splash.mpk.hearthdaoc-orig"])  # nothing else
        self.assertEqual(sha(read(os.path.join(client, "game.dll.hearthdaoc-orig"))), sha(self.dll))
        self.assertEqual(sha(read(os.path.join(client, "pregame", "splash.mpk.hearthdaoc-orig"))), sha(SPLASH))

    def test_a_refused_patch_set_warns_and_setup_still_succeeds(self):
        self.write_patchset(sha(b"the game.dll of another edition"))
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_DLL, r.stdout)
        self.assertIn(WARNING, r.stderr)
        client = os.path.join(dest, "client")
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH)
        self.assertFalse(os.path.exists(os.path.join(client, "game.dll.hearthdaoc-orig")))
        self.assertTrue(os.path.isfile(os.path.join(dest, "play.sh")))

    def test_setup_installs_the_patch_files_next_to_play_sh(self):
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        installed_patches(self, dest, self.patches)  # nothing else: no __pycache__ either

    def test_running_setup_again_refreshes_the_installed_patch_files(self):
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        installed = os.path.join(dest, "patches")
        with open(os.path.join(installed, "old-patch-set.json"), "w", encoding="utf-8") as f:
            f.write("{}")  # left by an older release
        with open(os.path.join(installed, "patchset.py"), "a", encoding="utf-8") as f:
            f.write("\nraise SystemExit('an older patchset.py')\n")
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertNotIn("Warning", r.stderr)
        installed_patches(self, dest, self.patches)
        self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.dll_patched)

    def test_a_failed_patch_fails_setup(self):
        # An invalid patch set (exit 2). The message names the installed copy: setup.sh applies
        # <dest>/patches, the files play.sh applies at every launch, not the bundle's.
        with open(os.path.join(self.patches, "classic-creation.json"), "w", encoding="utf-8") as f:
            json.dump({"format": 2}, f)
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
        installed = os.path.join(dest, "patches", "classic-creation.json")
        self.assertIn(f"Error: invalid patch set: {installed}: not a format 1 patch set", r.stderr)
        self.assertIn("Patching the client failed (apply_patches.py exit 2, see the message above).", r.stderr)
        self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.dll)
        self.assertFalse(os.path.exists(os.path.join(dest, "play.sh")))

    def test_a_bundle_missing_a_patch_file_is_refused_before_anything_is_copied(self):
        aside = os.path.join(self.dir, "aside")
        for name in PATCH_FILES:
            with self.subTest(name=name):
                os.replace(os.path.join(self.patches, name), aside)
                try:
                    dest, r = self.setup_sh()
                finally:
                    os.replace(aside, os.path.join(self.patches, name))
                self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
                self.assertIn(f"Missing patches/{name} next to setup.sh; download the full client bundle.", r.stderr)
                self.assertFalse(os.path.exists(dest))

    def test_a_bundle_without_patches_is_refused_before_anything_is_copied(self):
        shutil.rmtree(self.patches)
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 1)
        self.assertIn("Missing patches/apply_patches.py next to setup.sh; download the full client bundle.", r.stderr)
        self.assertFalse(os.path.exists(dest))


if __name__ == "__main__":
    unittest.main()
