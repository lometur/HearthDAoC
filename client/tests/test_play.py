import glob
import hashlib
import http.server
import io
import json
import os
import pty
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "linux", "tests"))
import release_fixture as fx  # noqa: E402

TEMPLATE = os.path.join(HERE, "..", "linux", "play.sh.in")
PATCHES = os.path.join(HERE, "..", "patches")
RUNTIME_APPID = "1628350"
# Updates: the installed release, the one GitHub offers, and the settings setup.sh saved.
OLD, NEW = "v0.35b-hearth.2", "v0.35b-hearth.3"
CONF = "hearthdaoc-client.conf"
EDITION_DLL = "editions/0.34-no-custom-class/runtime/client-opendaoc/app/game.dll"  # classic's, in the fixture
BASE = "/home/player/Games/OpenDAoC client"  # with a space: it must reach the next setup.sh as one argument
QUESTION = f"HearthDAoC {NEW} is out (you have {OLD}). Update the client now?"
NO_CHECK = f"play.sh: could not check for a HearthDAoC update; playing {OLD}.\n"
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

    def run_play(self, *args, stdin=subprocess.DEVNULL, **env):
        """Run play.sh; the result also has its pid. No DISPLAY unless a test sets one: like a headless or
        broken launch. Updates are looked for only where a test points HEARTHDAOC_RELEASES_URL (nothing
        listens on port 9), never on GitHub."""
        env = {"HOME": self.home, "PATH": os.environ["PATH"],
               "HEARTHDAOC_RELEASES_URL": "http://127.0.0.1:9/releases", **env}
        p = subprocess.Popen(["bash", self.play, *args], env=env, stdin=stdin, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, text=True)
        try:
            out, err = p.communicate(timeout=60)
        finally:
            p.kill()
        r = subprocess.CompletedProcess(p.args, p.returncode, out, err)
        r.pid = p.pid
        return r

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


# A stand-in for the next release's setup.sh: it logs its arguments, one per line, and installs its
# play.sh and its release in the settings, as setup.sh does (both renamed into place).
FAKE_SETUP = r'''#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$@" > "@ARGS@"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
while [[ $# -gt 0 ]]; do if [[ "$1" == --dest ]]; then dest="$2"; fi; shift; done
echo "Setting up the client ..."
cp "$here/new-play.sh" "$dest/play.sh.new"
mv -f "$dest/play.sh.new" "$dest/play.sh"
sed "s/^tag=.*/tag=$(cat "$here/VERSION")/" "$dest/hearthdaoc-client.conf" > "$dest/hearthdaoc-client.conf.new"
mv -f "$dest/hearthdaoc-client.conf.new" "$dest/hearthdaoc-client.conf"
'''
FAILING_SETUP = r'''#!/usr/bin/env bash
printf '%s\n' "$@" > "@ARGS@"
echo "Patching the client failed (apply_patches.py exit 2, see the message above)." >&2
exit 1
'''
# The next release's play.sh: it logs how it was started.
NEW_PLAY = r'''#!/usr/bin/env bash
printf 'new play.sh pid=%s no_update=%s args=%s\n' "$$" "${HEARTHDAOC_NO_UPDATE:-}" "$*" >> "@CALLS@"
'''


class ReleaseServer:
    """GitHub's releases pages as play.sh uses them, on a local port: <url>/latest redirects to
    <url>/tag/<latest>, and <url>/download/<tag>/<name> serves files[<tag>/<name>]. requests logs
    (method, path) of every request."""

    def __init__(self, latest, files=None):
        self.latest, self.files, self.requests = latest, dict(files or {}), []

    def __enter__(self):
        owner = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_HEAD(self):
                self.answer(body=False)

            def do_GET(self):
                self.answer(body=True)

            def answer(self, body):
                owner.requests.append((self.command, self.path))
                if self.path == "/releases/latest":
                    self.send_response(302)
                    self.send_header("Location", f"{owner.url}/tag/{owner.latest}")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                prefix = "/releases/download/"
                data = owner.files.get(self.path[len(prefix):]) if self.path.startswith(prefix) else None
                if data is None:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                if body:
                    self.wfile.write(data)

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.httpd.server_address[1]}/releases"
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()
        return self

    def __exit__(self, *exc):
        self.httpd.shutdown()
        self.httpd.server_close()

    def latest_only(self):
        return self.requests == [("HEAD", "/releases/latest")]


def zip_name(tag):
    """The client bundle's name on the releases page, as served by ReleaseServer."""
    return f"{tag}/hearthdaoc-client-{tag}.zip"


def free_port():
    """A local port nothing listens on: connecting is refused at once."""
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class UpdateTestCase(FakeSteamTestCase):
    """An install made by a release's setup.sh: its settings, a saved login and a fake zenity at hand."""

    def setUp(self):
        super().setUp()
        self.save_login()
        self.setup_args = os.path.join(self.tmp.name, "setup-args")
        self.progress = os.path.join(self.tmp.name, "progress.log")
        self.write_conf(OLD)

    def write_conf(self, tag):
        write(os.path.join(self.dest, CONF), "# Written by setup.sh: play.sh installs updates with these settings.\n"
              f"server=192.168.1.64:10301\nedition=classic\nbase_client={BASE}\ntag={tag}\n")

    def saved_tag(self):
        with open(os.path.join(self.dest, CONF), encoding="utf-8") as f:
            return [line[4:] for line in f.read().splitlines() if line.startswith("tag=")]

    def bundle(self, tag=NEW, version=None, setup=FAKE_SETUP):
        """A client bundle zip as deploy/build_bundles.sh makes it, cut to what the update uses. setup=None
        leaves setup.sh out."""
        top = f"hearthdaoc-client-{tag}/"
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            z.writestr(top + "VERSION", (version or tag) + "\n")
            if setup is not None:
                z.writestr(top + "setup.sh", setup.replace("@ARGS@", self.setup_args))
            z.writestr(top + "new-play.sh", NEW_PLAY.replace("@CALLS@", self.calls))
        return buf.getvalue()

    def zenity(self, answer):
        """Environment for a launch from Steam: a display, and a zenity that logs its calls in calls.log,
        answers --question with exit code <answer> (0 yes, 1 no) and keeps what --progress is sent."""
        bin_dir = os.path.join(self.tmp.name, "bin")
        write(os.path.join(bin_dir, "zenity"),
              f'#!/bin/sh\nprintf "zenity %s\\n" "$*" >> "{self.calls}"\n'
              f'case "$1" in\n  --question) exit {answer} ;;\n  --progress) cat >> "{self.progress}" ;;\nesac\n',
              executable=True)
        return {"DISPLAY": ":99", "PATH": bin_dir + os.pathsep + os.environ["PATH"]}

    def calls_of(self, prefix):
        try:
            with open(self.calls, encoding="utf-8") as f:
                return [line for line in f.read().splitlines() if line.startswith(prefix)]
        except FileNotFoundError:
            return []

    def assert_played_this_release(self, r):
        """The installed play.sh started the game, and the release it records is still the old one."""
        self.assertEqual(r.returncode, 0, r.stderr)
        launches = self.launches()
        self.assertEqual(len(launches), 1, r.stderr)
        self.assertIn("192.168.1.64:10301 Tester1 pw1", launches[0])
        self.assertEqual(self.calls_of("new play.sh"), [])
        self.assertEqual(self.saved_tag(), [OLD])
        self.assertEqual(glob.glob(os.path.join(self.dest, ".update.*")), [])


class UpdateCheckTests(UpdateTestCase):
    """When play.sh looks for a newer release, and what it does when there is none."""

    def test_without_a_saved_release_it_never_asks_github(self):
        # An install by an older bundle (no settings), or by setup.sh from a checkout (no release).
        for conf in (None, ""):
            with self.subTest(conf=conf), ReleaseServer(NEW, {zip_name(NEW): self.bundle()}) as srv:
                if conf is None:
                    os.remove(os.path.join(self.dest, CONF))
                else:
                    self.write_conf(conf)
                r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, **self.zenity(0))
                self.assertEqual(r.returncode, 0, r.stderr)
                self.assertEqual(srv.requests, [])
                self.assertEqual(r.stderr, "")
                self.assertEqual(self.calls_of("zenity"), [])
            os.remove(self.calls)

    def test_hearthdaoc_no_update_turns_the_check_off(self):
        with ReleaseServer(NEW, {zip_name(NEW): self.bundle()}) as srv:
            r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, HEARTHDAOC_NO_UPDATE="1", **self.zenity(0))
        self.assert_played_this_release(r)
        self.assertEqual(srv.requests, [])
        self.assertEqual(r.stderr, "")

    def test_offline_the_game_starts_at_once_after_one_short_line(self):
        start = time.monotonic()
        r = self.run_play(HEARTHDAOC_RELEASES_URL=f"http://127.0.0.1:{free_port()}/releases", **self.zenity(0))
        self.assertLess(time.monotonic() - start, 4)
        self.assert_played_this_release(r)
        self.assertEqual(r.stderr, NO_CHECK)
        self.assertEqual(self.calls_of("zenity"), [])

    def test_a_server_that_never_answers_holds_the_game_5_seconds_at_most(self):
        with socket.socket() as silent:  # accepts connections, never answers
            silent.bind(("127.0.0.1", 0))
            silent.listen(4)
            start = time.monotonic()
            r = self.run_play(HEARTHDAOC_RELEASES_URL=f"http://127.0.0.1:{silent.getsockname()[1]}/releases")
            elapsed = time.monotonic() - start
        self.assertLess(elapsed, 8)
        self.assert_played_this_release(r)
        self.assertEqual(r.stderr, NO_CHECK)

    def test_the_same_or_an_older_release_is_never_offered(self):
        # 0.35b is upstream's release after 0.35, so v0.35-hearth.9 is older than v0.35b-hearth.2.
        for latest in (OLD, "v0.35b-hearth.1", "v0.35-hearth.9", "v0.34b-hearth.12"):
            with self.subTest(latest=latest), ReleaseServer(latest, {zip_name(latest): self.bundle(latest)}) as srv:
                r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, **self.zenity(0))  # zenity would say yes
                self.assert_played_this_release(r)
                self.assertTrue(srv.latest_only(), srv.requests)
                self.assertEqual(self.calls_of("zenity"), [])
                self.assertEqual(r.stderr, "")
            os.remove(self.calls)

    def test_a_newer_release_is_offered(self):
        for installed, latest in ((OLD, NEW), ("v0.35b-hearth.9", "v0.35b-hearth.10"),
                                  ("v0.35-hearth.4", "v0.35b-hearth.1"), ("v0.35b-hearth.7", "v0.36-hearth.1")):
            with self.subTest(installed=installed, latest=latest), ReleaseServer(latest) as srv:
                self.write_conf(installed)
                r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, **self.zenity(1))  # no
                self.assertEqual(r.returncode, 0, r.stderr)
                self.assertEqual(len(self.launches()), 1)
                self.assertEqual(self.calls_of("zenity --question"),
                                 [f"zenity --question --title=HearthDAoC --text=HearthDAoC {latest} is out "
                                  f"(you have {installed}). Update the client now?"])
            os.remove(self.calls)

    def test_a_tag_that_is_not_a_release_is_ignored(self):
        with ReleaseServer("v0.35b-hearth.3;rm") as srv:
            r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, **self.zenity(0))
        self.assert_played_this_release(r)
        self.assertEqual(r.stderr, NO_CHECK)
        self.assertEqual(self.calls_of("zenity"), [])


class UpdateTests(UpdateTestCase):
    """A newer release: the player is asked first, and the update replaces play.sh or warns and plays."""

    def test_yes_from_steam_installs_the_release_and_starts_its_play_sh(self):
        with ReleaseServer(NEW, {zip_name(NEW): self.bundle()}) as srv:
            r = self.run_play("--launch-option", "two words", HEARTHDAOC_RELEASES_URL=srv.url, **self.zenity(0))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(srv.requests, [("HEAD", "/releases/latest"), ("GET", "/releases/download/" + zip_name(NEW))])
        with open(self.setup_args, encoding="utf-8") as f:
            self.assertEqual(f.read().splitlines(), ["--server", "192.168.1.64:10301", "--edition", "classic",
                                                     "--base-client", BASE, "--dest", self.dest])
        # The new play.sh replaced this one in the same process, with the same arguments, and won't check again.
        self.assertEqual(self.calls_of("new play.sh"),
                         [f"new play.sh pid={r.pid} no_update=1 args=--launch-option two words"])
        self.assertEqual(self.launches(), [])
        self.assertEqual(self.saved_tag(), [NEW])
        self.assertEqual(glob.glob(os.path.join(self.dest, ".update.*")), [])
        zenity = self.calls_of("zenity")
        self.assertEqual(zenity[0], f"zenity --question --title=HearthDAoC --text={QUESTION}")
        self.assertTrue(zenity[1].startswith("zenity --progress --pulsate --auto-close --no-cancel"), zenity)
        self.assertEqual(len(zenity), 2, zenity)  # no warning
        with open(self.progress, encoding="utf-8") as f:
            progress = f.read().splitlines()
        self.assertEqual(progress[0], f"# Downloading HearthDAoC {NEW} ...")
        self.assertIn("# Setting up the client ...", progress)

    def test_yes_in_a_terminal_installs_the_release_with_plain_output(self):
        master, slave = pty.openpty()
        try:
            os.write(master, b"y\n")
            with ReleaseServer(NEW, {zip_name(NEW): self.bundle()}) as srv:
                r = self.run_play(stdin=slave, HEARTHDAOC_RELEASES_URL=srv.url)
        finally:
            os.close(slave)
            os.close(master)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(QUESTION + " [y/N] ", r.stderr)
        self.assertIn(f"Downloading HearthDAoC {NEW} ...\n", r.stdout)
        self.assertIn("Setting up the client ...\n", r.stdout)
        self.assertEqual(self.calls_of("new play.sh"), [f"new play.sh pid={r.pid} no_update=1 args="])
        self.assertEqual(self.saved_tag(), [NEW])

    def test_no_plays_this_release_and_asks_again_next_time(self):
        for answer, env in (("zenity", self.zenity(1)), ("terminal", {})):
            with self.subTest(answer), ReleaseServer(NEW, {zip_name(NEW): self.bundle()}) as srv:
                master, slave = pty.openpty()
                try:
                    os.write(master, b"n\n")
                    r = self.run_play(stdin=slave if answer == "terminal" else subprocess.DEVNULL,
                                      HEARTHDAOC_RELEASES_URL=srv.url, **env)
                finally:
                    os.close(slave)
                    os.close(master)
                self.assert_played_this_release(r)
                self.assertTrue(srv.latest_only(), srv.requests)
                self.assertFalse(os.path.exists(self.setup_args))
            os.remove(self.calls)

    def test_without_a_way_to_ask_it_warns_and_plays_this_release(self):
        # No terminal, and no display (or no zenity): nobody to say yes.
        with ReleaseServer(NEW, {zip_name(NEW): self.bundle()}) as srv:
            r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url)
        self.assert_played_this_release(r)
        self.assertTrue(srv.latest_only(), srv.requests)
        self.assertEqual(r.stderr, f"play.sh: HearthDAoC {NEW} is out (you have {OLD}). To update, run {self.play} "
                                   "from a terminal, or run setup.sh from the new release's client bundle.\n")

    def test_a_failed_update_warns_and_plays_this_release(self):
        url = None  # set below: the server's
        cases = {
            "no download": ({}, lambda: f"could not download {url}/download/{zip_name(NEW)}"),
            "not a zip": ({zip_name(NEW): b"<html>not a zip</html>"}, lambda: "the download is not a zip file"),
            "no setup.sh": ({zip_name(NEW): self.bundle(setup=None)},
                            lambda: f"the download holds no hearthdaoc-client-{NEW}/setup.sh"),
            "another release": ({zip_name(NEW): self.bundle(version="v0.35b-hearth.4")},
                                lambda: f"the download's VERSION file doesn't say {NEW}"),
            "setup.sh fails": ({zip_name(NEW): self.bundle(setup=FAILING_SETUP)},
                               lambda: "its setup.sh failed (exit 1)"),
        }
        for name, (files, why) in cases.items():
            with self.subTest(name), ReleaseServer(NEW, files) as srv:
                url = srv.url
                play_inode = os.stat(self.play).st_ino
                r = self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, **self.zenity(0))
                self.assert_played_this_release(r)
                self.assertEqual(os.stat(self.play).st_ino, play_inode)
                warning = (f"Warning: the update to HearthDAoC {NEW} failed: {why()}. The game starts with {OLD}; "
                           "the next launch offers the update again.")
                self.assertIn("play.sh: " + warning, r.stderr)
                with open(self.calls, encoding="utf-8") as f:
                    calls = f.read().splitlines()
                warnings = [n for n, line in enumerate(calls) if line.startswith("zenity --warning")]
                self.assertEqual(len(warnings), 1, calls)  # in a window: started from Steam
                self.assertIn(warning, "\n".join(calls[warnings[0]:]))
                self.assertLess(warnings[0], next(n for n, line in enumerate(calls) if "connect.exe" in line))
                if name == "setup.sh fails":  # its own message is in the window too
                    self.assertIn("Patching the client failed (apply_patches.py exit 2", "\n".join(calls[warnings[0]:]))
            os.remove(self.calls)


def make_base(folder, files):
    """A fake 1.127 base client folder, as the player's OpenDAoC install would be."""
    for name, data in (("connect.exe", files["runtime/client-opendaoc/app/connect.exe"]), ("game1127.dll", b"scaling"),
                       ("game.dll", b"stock"), ("paths.dat", b"[paths]\r\nsettings=Atlas1")):
        write_bytes(os.path.join(folder, name), data)


class UpdateRoundTripTests(FakeSteamTestCase):
    """The real scripts: a release's setup.sh installs the client, and its play.sh updates it with the next
    release's client bundle, built by deploy/build_bundles.sh, whose setup.sh runs with the saved settings."""

    def setUp(self):
        super().setUp()
        t = self.tmp.name
        self.dest = os.path.join(t, "Games", "Hearth DAoC")
        self.play = os.path.join(self.dest, "play.sh")
        self.base = os.path.join(t, "my games", "OpenDAoC 1.127 client")
        lock, files = fx.build(t)
        make_base(self.base, files)
        out = os.path.join(t, "dist")
        r = subprocess.run(["bash", os.path.join(REPO, "deploy", "build_bundles.sh"), NEW, out], cwd=REPO,
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.built = os.path.join(out, f"hearthdaoc-client-{NEW}.zip")
        parts = fx.RangeServer(t)
        parts.__enter__()
        self.addCleanup(parts.__exit__, None, None, None)
        self.lock_json = json.dumps(parts.lock(lock)).encode()
        # The installed release: this bundle with an older VERSION, set up with the test's lock.
        with zipfile.ZipFile(self.built) as z:
            z.extractall(os.path.join(t, "old"))
        old = os.path.join(t, "old", f"hearthdaoc-client-{NEW}")
        write(os.path.join(old, "VERSION"), OLD + "\n")
        write_bytes(os.path.join(old, "upstream.lock"), self.lock_json)
        r = subprocess.run(["bash", os.path.join(old, "setup.sh"), "--server", "192.168.1.64:10301",
                            "--edition", "classic", "--base-client", self.base, "--dest", self.dest],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.save_login()

    def update(self, lock_json):
        """Start the installed play.sh from Steam; the player says yes to the next release: this bundle, with
        lock_json as its upstream.lock and a play.sh that says who it is."""
        top = f"hearthdaoc-client-{NEW}/"
        changes = {top + "upstream.lock": lambda data: lock_json,
                   top + "play.sh.in": lambda data: data.replace(
                       b"\n", b'\necho "play.sh of the next release" >&2\n', 1)}
        served = io.BytesIO()
        with zipfile.ZipFile(self.built) as z, zipfile.ZipFile(served, "w") as new:
            for info in z.infolist():
                data = z.read(info)
                new.writestr(info.filename, changes.get(info.filename, lambda d: d)(data))
        bin_dir = os.path.join(self.tmp.name, "bin")
        write(os.path.join(bin_dir, "zenity"), '#!/bin/sh\n[ "$1" != --progress ] || cat > /dev/null\n',
              executable=True)  # says yes
        with ReleaseServer(NEW, {zip_name(NEW): served.getvalue()}) as srv:
            return self.run_play(HEARTHDAOC_RELEASES_URL=srv.url, DISPLAY=":99",
                                 PATH=bin_dir + os.pathsep + os.environ["PATH"])

    def install(self):
        """What an update changes: the client, the patches, play.sh and the settings."""
        with open(os.path.join(self.dest, CONF), encoding="utf-8") as f:
            conf = f.read()
        return (snapshot(os.path.join(self.dest, "client")), snapshot(os.path.join(self.dest, "patches")),
                os.stat(self.play).st_ino, conf)

    def assert_launched_once(self):
        launches = self.launches()
        self.assertEqual(len(launches), 1, launches)
        self.assertIn("192.168.1.64:10301 Tester1 pw1", launches[0])

    def assert_nothing_left(self):
        self.assertEqual([n for n in os.listdir(self.dest) if n.startswith(".update.") or n.endswith((".new", ".old"))],
                         [])

    def test_play_sh_updates_a_client_installed_by_setup_sh(self):
        inode = os.stat(self.play).st_ino
        r = self.update(self.lock_json)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("play.sh of the next release\n", r.stderr)
        self.assert_launched_once()
        self.assertNotEqual(os.stat(self.play).st_ino, inode)
        with open(os.path.join(self.dest, CONF), encoding="utf-8") as f:
            settings = dict(line.split("=", 1) for line in f.read().splitlines() if not line.startswith("#"))
        self.assertEqual(settings, {"server": "192.168.1.64:10301", "edition": "classic", "base_client": self.base,
                                    "tag": NEW})
        self.assert_nothing_left()

    def test_an_update_whose_download_stops_leaves_the_installed_client_and_plays_it(self):
        # The new setup.sh has already put the base client back and fetched some OfflineDAoC files when the
        # fetch stops: here the manifest has a wrong SHA-256 for game.dll, the last file it fetches.
        broken = os.path.join(self.tmp.name, "broken")
        lock, _files = fx.build(broken, tamper={EDITION_DLL: "0" * 64})
        before = self.install()
        with fx.RangeServer(broken) as parts:
            r = self.update(json.dumps(parts.lock(lock)).encode())
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(f"play.sh: Warning: the update to HearthDAoC {NEW} failed: its setup.sh failed (exit 1). "
                      f"The game starts with {OLD}; the next launch offers the update again.", r.stderr)
        self.assertIn("SHA-256 mismatch against the release manifest", r.stderr)  # setup.sh's own error
        self.assertNotIn("play.sh of the next release", r.stderr)
        self.assert_launched_once()
        self.assertEqual(self.install(), before)
        self.assert_nothing_left()


if __name__ == "__main__":
    unittest.main()
