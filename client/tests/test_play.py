import os
import stat
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATE = os.path.join(HERE, "..", "linux", "play.sh.in")
RUNTIME_APPID = "1628350"


def write(path, text, executable=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    if executable:
        os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)


class PlayScriptTests(unittest.TestCase):
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

    def run_play(self):
        env = {"HOME": self.home, "PATH": os.environ["PATH"]}  # no DISPLAY: like a headless or broken launch
        return subprocess.run(["bash", self.play], env=env, stdin=subprocess.DEVNULL,
                              capture_output=True, text=True, timeout=60)

    def save_login(self):
        write(os.path.join(self.dest, "account.txt"), "Account: Tester1\nPassword: pw1\n")

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


if __name__ == "__main__":
    unittest.main()
