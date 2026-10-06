import hashlib
import json
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATE = os.path.join(HERE, "..", "linux", "play.sh.in")
PATCHES = os.path.join(HERE, "..", "patches")
RUNTIME_APPID = "1628350"
# A fake client and a patch set for it, in the shape of client/patches/classic-creation.json.
DLL = b"MZ the classic game.dll " * 8
DLL_PATCHED = b"HDC!" + DLL[4:] + b"cave"
SPLASH = b"stock splash"
SPLASH_NEW = b"HEARTH DAoC splash"
REFUSED_NOTE = ("play.sh: HearthDAoC's client patches don't support this client (for example the b edition); "
                "playing with the standard creation screen.\n")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    with open(path, "rb") as f:
        return f.read()


def write_bytes(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def snapshot(folder):
    """{relative path: (inode, mtime, size)} of every file under folder: any write changes it."""
    out = {}
    for parent, _dirs, names in os.walk(folder):
        for name in names:
            st = os.stat(os.path.join(parent, name))
            out[os.path.relpath(os.path.join(parent, name), folder)] = (st.st_ino, st.st_mtime_ns, st.st_size)
    return out


def write(path, text, executable=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    if executable:
        os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)


class FakeSteamTestCase(unittest.TestCase):
    """play.sh against a fake Steam install whose Proton and runtime live in a second library."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        t = self.tmp.name
        self.home, self.root, self.lib2 = (os.path.join(t, d) for d in ("home", "steamroot", "lib2"))
        os.makedirs(os.path.join(self.home, ".steam"))
        os.symlink(self.root, os.path.join(self.home, ".steam", "steam"))
        write(os.path.join(self.root, "steamapps", "libraryfolders.vdf"),
              f'"libraryfolders"\n{{\n\t"0"\n\t{{\n\t\t"path"\t\t"{self.root}"\n\t}}\n'
              f'\t"1"\n\t{{\n\t\t"path"\t\t"{self.lib2}"\n\t}}\n}}\n')
        proton = os.path.join(self.lib2, "steamapps", "common", "Proton - Experimental")
        write(os.path.join(proton, "proton"), "#!/bin/sh\nexit 0\n", executable=True)
        write(os.path.join(proton, "toolmanifest.vdf"),
              f'"manifest"\n{{\n  "commandline" "/proton %verb%"\n  "require_tool_appid" "{RUNTIME_APPID}"\n}}\n')
        self.calls = os.path.join(t, "calls.log")
        write(os.path.join(self.lib2, "steamapps", f"appmanifest_{RUNTIME_APPID}.acf"),
              f'"AppState"\n{{\n\t"appid"\t\t"{RUNTIME_APPID}"\n\t"installdir"\t\t"SteamLinuxRuntime_sniper"\n}}\n')
        write(os.path.join(self.lib2, "steamapps", "common", "SteamLinuxRuntime_sniper", "_v2-entry-point"),
              f'#!/bin/sh\nprintf "%s " "$@" >> "{self.calls}"\necho >> "{self.calls}"\n', executable=True)
        self.dest = os.path.join(t, "dest")
        write(os.path.join(self.dest, "client", "paths.dat"), "[paths]\r\nsettings=OfflineDAoC034")
        with open(TEMPLATE, encoding="utf-8") as f:
            script = f.read().replace("@SERVER@", "192.168.1.64:10301").replace("@EDITION@", "classic")
        self.play = os.path.join(self.dest, "play.sh")
        write(self.play, script, executable=True)

    def tearDown(self):
        self.tmp.cleanup()

    def run_play(self, **env):
        # No DISPLAY unless a test sets one: like a headless or broken launch.
        env = {"HOME": self.home, "PATH": os.environ["PATH"], **env}
        return subprocess.run(["bash", self.play], env=env, stdin=subprocess.DEVNULL,
                              capture_output=True, text=True, timeout=60)

    def save_login(self):
        write(os.path.join(self.dest, "account.txt"), "Account: Tester1\nPassword: pw1\n")

    def launches(self):
        """The game launches play.sh made: the runtime calls that start connect.exe."""
        try:
            with open(self.calls, encoding="utf-8") as f:
                return [line for line in f.read().splitlines() if "connect.exe" in line]
        except FileNotFoundError:
            return []


class PlayScriptTests(FakeSteamTestCase):
    def test_finds_proton_and_runtime_in_another_steam_library(self):
        self.save_login()
        r = self.run_play()
        self.assertEqual(r.returncode, 0, r.stderr)
        with open(self.calls, encoding="utf-8") as f:
            last = f.read().splitlines()[-1]
        self.assertIn(os.path.join(self.lib2, "steamapps", "common", "Proton - Experimental", "proton"), last)
        self.assertIn("192.168.1.64:10301 Tester1 pw1", last)

    def test_missing_runtime_is_reported_not_silent(self):
        self.save_login()
        os.remove(os.path.join(self.lib2, "steamapps", f"appmanifest_{RUNTIME_APPID}.acf"))
        r = self.run_play()
        self.assertEqual(r.returncode, 1)
        self.assertIn(f"Steam Linux Runtime (app {RUNTIME_APPID})", r.stderr)

    def test_first_run_without_a_terminal_or_dialog_explains_what_to_do(self):
        r = self.run_play()
        self.assertEqual(r.returncode, 1)
        self.assertIn("from a terminal", r.stderr)



class LaunchPatchTests(FakeSteamTestCase):
    """play.sh applies <dest>/patches (installed by setup.sh) to the client before every launch."""

    def setUp(self):
        super().setUp()
        self.save_login()
        self.client = os.path.join(self.dest, "client")
        self.patches = os.path.join(self.dest, "patches")
        write_bytes(os.path.join(self.client, "game.dll"), DLL)
        write_bytes(os.path.join(self.client, "pregame", "splash.mpk"), SPLASH)
        os.makedirs(self.patches)
        for name in ("apply_patches.py", "patchset.py"):
            shutil.copy(os.path.join(PATCHES, name), self.patches)
        write_bytes(os.path.join(self.patches, "splash.mpk"), SPLASH_NEW)
        self.write_patchset(sha(DLL))

    def write_patchset(self, dll_before):
        data = {
            "format": 1,
            "name": "classic-creation",
            "client": "test fixture",
            "files": [
                {"path": "game.dll", "before": dll_before, "after": sha(DLL_PATCHED),
                 "ops": [{"op": "replace", "offset": 0, "from": DLL[:4].hex(), "to": b"HDC!".hex()},
                         {"op": "append", "data": b"cave".hex()}]},
                {"path": "pregame/splash.mpk", "before": sha(SPLASH), "after": sha(SPLASH_NEW),
                 "ops": [{"op": "file", "source": "splash.mpk"}]},
            ],
        }
        with open(os.path.join(self.patches, "classic-creation.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1)

    def apply(self):
        r = subprocess.run(["python3", os.path.join(self.patches, "apply_patches.py"), "--client", self.client],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)

    def assert_launched_once(self, r):
        self.assertEqual(r.returncode, 0, r.stderr)
        launches = self.launches()
        self.assertEqual(len(launches), 1, launches)
        self.assertIn("192.168.1.64:10301 Tester1 pw1", launches[0])

    def assert_unpatched(self):
        self.assertEqual(read(os.path.join(self.client, "game.dll")), DLL)
        self.assertEqual(read(os.path.join(self.client, "pregame", "splash.mpk")), SPLASH)

    def test_patches_the_client_again_when_its_files_were_put_back(self):
        self.apply()
        # Something (a repair, another launcher) puts the original files back; the backups stay.
        write_bytes(os.path.join(self.client, "game.dll"), DLL)
        write_bytes(os.path.join(self.client, "pregame", "splash.mpk"), SPLASH)
        r = self.run_play()
        self.assert_launched_once(r)
        self.assertEqual(r.stderr, "")
        self.assertIn("Patched: game.dll", r.stdout)
        self.assertEqual(read(os.path.join(self.client, "game.dll")), DLL_PATCHED)
        self.assertEqual(read(os.path.join(self.client, "pregame", "splash.mpk")), SPLASH_NEW)
        self.assertEqual(read(os.path.join(self.client, "game.dll.hearthdaoc-orig")), DLL)

    def test_patches_a_client_that_was_never_patched(self):
        r = self.run_play()
        self.assert_launched_once(r)
        self.assertEqual(read(os.path.join(self.client, "game.dll")), DLL_PATCHED)
        self.assertEqual(read(os.path.join(self.client, "pregame", "splash.mpk")), SPLASH_NEW)

    def test_an_already_patched_client_launches_without_any_write(self):
        self.apply()
        before = (snapshot(self.client), snapshot(self.patches))
        r = self.run_play()
        self.assert_launched_once(r)
        self.assertEqual((snapshot(self.client), snapshot(self.patches)), before)
        self.assertEqual(r.stderr, "")
        self.assertNotIn("Already patched", r.stdout)  # nothing to say

    def test_a_refused_client_launches_with_the_standard_screen_and_one_short_note(self):
        self.write_patchset(sha(b"the game.dll of another edition"))  # exit 3: refused, nothing changed
        r = self.run_play()
        self.assert_launched_once(r)
        self.assertEqual(r.stderr, REFUSED_NOTE)
        self.assertNotIn("Not patched", r.stdout)
        self.assert_unpatched()
        self.assertFalse(os.path.exists(os.path.join(self.client, "game.dll.hearthdaoc-orig")))

    def test_a_failed_patch_warns_and_the_game_still_launches(self):
        os.remove(os.path.join(self.patches, "splash.mpk"))  # exit 2: the patch set can't be used
        r = self.run_play()
        self.assert_launched_once(r)
        self.assertIn("Error: bundled file missing: " + os.path.join(self.patches, "splash.mpk"), r.stderr)
        self.assertIn("play.sh: Warning: HearthDAoC's client patches could not be applied (apply_patches.py exit 2)",
                      r.stderr)
        self.assert_unpatched()

    def test_a_write_failure_warns_in_a_dialog_without_a_terminal_and_the_game_still_launches(self):
        # A stand-in applier that fails like a client folder it can't write (exit 1).
        with open(os.path.join(self.patches, "apply_patches.py"), "w", encoding="utf-8") as f:
            f.write("import sys\nprint('Error: [Errno 13] Permission denied', file=sys.stderr)\nsys.exit(1)\n")
        bin_dir = os.path.join(self.tmp.name, "bin")
        write(os.path.join(bin_dir, "zenity"), f'#!/bin/sh\nprintf "zenity %s\\n" "$*" >> "{self.calls}"\n',
              executable=True)
        r = self.run_play(DISPLAY=":99", PATH=bin_dir + os.pathsep + os.environ["PATH"])
        self.assert_launched_once(r)
        self.assertIn("Error: [Errno 13] Permission denied", r.stderr)
        self.assertIn("(apply_patches.py exit 1)", r.stderr)
        with open(self.calls, encoding="utf-8") as f:
            calls = f.read().splitlines()
        dialogs = [n for n, line in enumerate(calls) if line.startswith("zenity ")]
        self.assertEqual(len(dialogs), 1, calls)
        self.assertIn("--warning", calls[dialogs[0]])
        self.assertIn("could not be applied", calls[dialogs[0]])
        self.assertLess(dialogs[0], next(n for n, line in enumerate(calls) if "connect.exe" in line))

    def test_without_a_patches_folder_nothing_is_patched(self):
        shutil.rmtree(self.patches)
        r = self.run_play()
        self.assert_launched_once(r)
        self.assertEqual(r.stderr, "")
        self.assert_unpatched()
        self.assertEqual(sorted(os.listdir(self.client)), ["game.dll", "paths.dat", "pregame"])


if __name__ == "__main__":
    unittest.main()
