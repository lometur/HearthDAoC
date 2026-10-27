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
RELEASE_INI = "runtime/client-opendaoc/app/default.ini"
SPLASH = b"stock splash"
SPLASH_NEW = b"HEARTH DAoC splash"
# A fake default.ini, and what the bundle's default-layout.json makes of it.
INI = b"[Panels-800x600]\r\nVersion=1\r\n"
INI_PATCHED = b"[Panels-1920x1080]\r\nVersion=1\r\n" + INI
UNKNOWN_DLL = ("Not patched: game.dll is not the file this HearthDAoC release supports (for example the "
               "b edition, or an older or newer upstream client). The client still works with the standard "
               "creation screen.\n")
UNKNOWN_INI = ("Not patched: default.ini is not the file this HearthDAoC release supports (for example one changed "
               "by hand, or an older or newer upstream client). New characters start with the game's standard window "
               "layout.\n")
WARNING = "Warning: the client was set up without HearthDAoC's classic creation patches (see the message above).\n"
LAYOUT_WARNING = "Warning: the client was set up without HearthDAoC's default layout (see the message above).\n"
# A fake zone archive and the 2006 Albion portal model, with sets in the shape of client/patches/si-portals.json and
# si-portal-art.json (a text edit stands in for the archive edit, which client/patches/tests cover).
ARCHIVE_PATH, MODEL_PATH = "zones/Zone000/csv000.mpk", "Newtowns/zones/Nifs/bavteleporter.npk"
RELEASE_ARCHIVE, RELEASE_MODEL = "runtime/client-opendaoc/app/" + ARCHIVE_PATH, "runtime/client-opendaoc/app/" + MODEL_PATH
PORTAL_ROW = "455,SI Portal Visual 000,bavteleporter.NIF\r\n"
ARCHIVE = b"10,Our Tree,ourtree.nif\r\n" + PORTAL_ROW.encode()
ARCHIVE_PATCHED = b"10,Our Tree,ourtree.nif\r\n"
MODEL = b"the 2006 portal model"
SI_WARNING = "Warning: the client was set up without HearthDAoC's Shrouded Isles portal fix (see the message above).\n"
ART_WARNING = "Warning: the client was set up without HearthDAoC's Albion portal look (see the message above).\n"
# What setup.sh installs in <dest>/patches, for play.sh to apply at every launch: exactly these.
PATCH_FILES = ["apply_patches.py", "classic-creation.json", "default-layout.json", "patchset.py", "si-portal-art.json",
               "si-portals.json", "splash.mpk"]
CONF = "hearthdaoc-client.conf"


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


def saved_settings(dest):
    """The key=value lines of <dest>/hearthdaoc-client.conf, which play.sh reads to install updates."""
    with open(os.path.join(dest, CONF), encoding="utf-8") as f:
        return dict(line.split("=", 1) for line in f.read().splitlines() if line and not line.startswith("#"))


def leftovers(dest):
    """What setup.sh builds beside the install (.new) or keeps until it is done (.old): none may stay."""
    return [name for name in os.listdir(dest) if name.endswith((".new", ".old"))]


def snapshot(folder):
    """{relative path: (inode, mtime, size)} of every file under folder: any write changes it."""
    out = {}
    for parent, _dirs, names in os.walk(folder):
        for name in names:
            st = os.stat(os.path.join(parent, name))
            out[os.path.relpath(os.path.join(parent, name), folder)] = (st.st_ino, st.st_mtime_ns, st.st_size)
    return out


def install_state(dest):
    """Everything a setup.sh run changes in <dest>: the client, the patches, play.sh and the settings."""
    with open(os.path.join(dest, CONF), encoding="utf-8") as f:
        conf = f.read()
    return (snapshot(os.path.join(dest, "client")), snapshot(os.path.join(dest, "patches")),
            os.stat(os.path.join(dest, "play.sh")).st_ino, conf)


def make_base(directory, files):
    """A fake 1.127 base client folder, as the player's OpenDAoC install would be."""
    base = os.path.join(directory, "base")
    os.makedirs(base)
    for name, data in (("connect.exe", files["runtime/client-opendaoc/app/connect.exe"]),
                       ("game1127.dll", b"scaling"), ("game.dll", b"stock"), ("paths.dat", b"[paths]\r\nsettings=Atlas1")):
        with open(os.path.join(base, name), "wb") as f:
            f.write(data)
    return base


def run_setup(script, directory, base, lock, *extra, cwd=None, env=None):
    """Run setup.sh with lock (part URLs pointing at a RangeServer); return (dest, result)."""
    lock_path = os.path.join(directory, "upstream.lock")
    with open(lock_path, "w") as f:
        json.dump(lock, f)
    dest = os.path.join(directory, "dest")
    args = ["bash", script, "--server", "192.168.1.64:10301", "--edition", "classic",
            "--base-client", base, "--dest", dest, "--lock", lock_path, *extra]
    return dest, subprocess.run(args, capture_output=True, text=True, cwd=cwd, env=env)


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

    def test_setup_from_a_checkout_saves_its_settings_without_a_release(self):
        # No VERSION next to setup.sh: nothing for play.sh to compare a release with, so it never checks.
        with fx.RangeServer(self.dir) as srv:
            dest, r = self.run_setup(srv)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertEqual(saved_settings(dest), {"server": "192.168.1.64:10301", "edition": "classic",
                                                "base_client": self.base, "tag": "", "content_id": ""})
        self.assertEqual(leftovers(dest), [])

    def test_repository_patch_set_refuses_a_foreign_client_and_setup_still_succeeds(self):
        # From a checkout, setup.sh uses client/patches/. The fixture's game.dll isn't OfflineDAoC
        # 0.35 classic's, so the applier refuses it (exit 3): a warning, and the client is unchanged. Its
        # default.ini isn't OfflineDAoC's either, so the default layout is refused too, with its own warning.
        files = dict(fx.DEFAULT_FILES)
        files[RELEASE_INI] = INI
        self.lock, self.files = fx.build(os.path.join(self.dir, "with-ini"), files)
        with fx.RangeServer(os.path.join(self.dir, "with-ini")) as srv:
            dest, r = self.run_setup(srv)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_DLL, r.stdout)
        self.assertIn(WARNING, r.stderr)
        self.assertIn(UNKNOWN_INI, r.stdout)
        self.assertIn(LAYOUT_WARNING, r.stderr)
        # The fixture has no zone archives: refused, with a warning; and no 2006 model, which counts as patched.
        self.assertIn("Not patched: zones/Zone000/csv000.mpk is missing from the client folder.\n", r.stdout)
        self.assertIn(SI_WARNING, r.stderr)
        self.assertIn(f"Already patched: {MODEL_PATH}\n", r.stdout)
        self.assertNotIn(ART_WARNING, r.stderr)
        self.assertEqual(read(os.path.join(dest, "client", "default.ini")), INI)
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
        files[RELEASE_INI] = INI
        files[RELEASE_ARCHIVE] = ARCHIVE
        files[RELEASE_MODEL] = MODEL
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
        self.write_layout_set(sha(INI))
        self.write_si_sets()

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

    def write_layout_set(self, ini_before):
        find = "[Panels-800x600]\r\n"
        data = {
            "format": 1,
            "name": "default-layout",
            "client": "test fixture",
            "refused": "(for example one changed by hand, or an older or newer upstream client). New characters "
                       "start with the game's standard window layout.",
            "files": [{"path": "default.ini", "before": ini_before, "after": sha(INI_PATCHED),
                       "ops": [{"op": "text-replace", "find": find,
                                "replace": "[Panels-1920x1080]\r\nVersion=1\r\n" + find}]}],
        }
        with open(os.path.join(self.patches, "default-layout.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1)

    def write_si_sets(self, archive_before=sha(ARCHIVE), model_before=sha(MODEL), retired=False):
        portals = {"format": 1, "name": "si-portals", "client": "test fixture",
                   "refused": "(our test's). The town portal art stays.",
                   "files": [{"path": ARCHIVE_PATH, "before": archive_before, "after": sha(ARCHIVE_PATCHED),
                              "ops": [{"op": "text-replace", "find": PORTAL_ROW, "replace": ""}]}]}
        art = {"format": 1, "name": "si-portal-art", "client": "test fixture",
               "refused": "(our test's). The portal keeps its 2006 look.",
               **({"retired": True} if retired else {}),
               "files": [{"path": MODEL_PATH, "before": model_before, "after": "absent", "ops": [{"op": "remove"}]}]}
        for name, data in (("si-portals.json", portals), ("si-portal-art.json", art)):
            with open(os.path.join(self.patches, name), "w", encoding="utf-8") as f:
                json.dump(data, f, indent=1)

    def setup_sh(self, base=None, cwd=None, release=None, env=None):
        """Run the bundle's setup.sh; release=(folder, lock) serves another release than setUp's."""
        folder, lock = release or (self.dir, self.lock)
        with fx.RangeServer(folder) as srv:
            return run_setup(os.path.join(self.bundle, "setup.sh"), self.dir, base or self.base, srv.lock(lock),
                             cwd=cwd, env=env)

    def broken_release(self):
        """This release with a wrong SHA-256 for the edition's game.dll in its manifest: the fetch stops at
        game.dll, its last file, after it has written the others. Like a download that stops halfway."""
        folder = os.path.join(self.dir, "broken")
        lock, _files = fx.build(folder, self.files, tamper={RELEASE_DLL: "0" * 64})
        return folder, lock

    def write_version(self, tag):
        with open(os.path.join(self.bundle, "VERSION"), "w", encoding="utf-8") as f:
            f.write(tag + "\n")  # as deploy/build_bundles.sh writes it

    def test_the_bundles_fetcher_takes_a_client_file_a_patch_replaces(self):
        # A lock for a release made of a base and an upstream patch (0.37b: 0.36b + the 0.37 game patch), read by
        # the bundle's own odaoc_fetch.py: a client file the patch lists comes from the patch zip, game.dll from
        # the base's edition, and the client patches still apply.
        paths = "runtime/client-opendaoc/app/paths.dat"
        lock, patch_files = fx.build_patch(self.dir, self.lock, self.files,
                                           files={paths: b"[paths]\r\nsettings=OfflineDAoC037"})
        dest, r = self.setup_sh(release=(self.dir, lock))
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertEqual(read(os.path.join(dest, "client", "paths.dat")), patch_files[paths])
        self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.dll_patched)

    def test_setup_patches_the_new_client(self):
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        client = os.path.join(dest, "client")
        self.assertIn("Patched: game.dll (original saved as game.dll.hearthdaoc-orig)\n"
                      "Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)\n"
                      "Patched: default.ini (original saved as default.ini.hearthdaoc-orig)\n"
                      f"Patched: {ARCHIVE_PATH} (original saved as {ARCHIVE_PATH}.hearthdaoc-orig)\n"
                      f"Patched: {MODEL_PATH} (original saved as {MODEL_PATH}.hearthdaoc-orig)\n",
                      r.stdout)
        self.assertNotIn("Warning", r.stderr)
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
        self.assertEqual(read(os.path.join(client, "game.dll.hearthdaoc-orig")), self.dll)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH_NEW)
        self.assertEqual(read(os.path.join(client, "default.ini")), INI_PATCHED)
        self.assertEqual(read(os.path.join(client, "default.ini.hearthdaoc-orig")), INI)
        self.assertEqual(read(os.path.join(client, *ARCHIVE_PATH.split("/"))), ARCHIVE_PATCHED)
        self.assertFalse(os.path.exists(os.path.join(client, *MODEL_PATH.split("/"))))
        self.assertEqual(read(os.path.join(client, *MODEL_PATH.split("/")) + ".hearthdaoc-orig"), MODEL)
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
        self.assertEqual(ours, [MODEL_PATH + ".hearthdaoc-orig", "default.ini.hearthdaoc-orig", "game.dll.hearthdaoc-orig",
                                "pregame/splash.mpk.hearthdaoc-orig", ARCHIVE_PATH + ".hearthdaoc-orig"])  # nothing else
        self.assertEqual(sha(read(os.path.join(client, "game.dll.hearthdaoc-orig"))), sha(self.dll))
        self.assertEqual(sha(read(os.path.join(client, "pregame", "splash.mpk.hearthdaoc-orig"))), sha(SPLASH))
        self.assertEqual(read(os.path.join(client, "default.ini.hearthdaoc-orig")), INI)
        self.assertEqual(read(os.path.join(client, "default.ini")), INI_PATCHED)
        self.assertEqual(read(os.path.join(client, *MODEL_PATH.split("/")) + ".hearthdaoc-orig"), MODEL)
        self.assertFalse(os.path.exists(os.path.join(client, *MODEL_PATH.split("/"))))

    def test_a_refused_patch_set_warns_and_setup_still_succeeds(self):
        self.write_patchset(sha(b"the game.dll of another edition"))
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_DLL, r.stdout)
        self.assertEqual(r.stderr, WARNING)
        client = os.path.join(dest, "client")
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll)
        self.assertEqual(read(os.path.join(client, "pregame", "splash.mpk")), SPLASH)
        self.assertFalse(os.path.exists(os.path.join(client, "game.dll.hearthdaoc-orig")))
        self.assertTrue(os.path.isfile(os.path.join(dest, "play.sh")))
        # The default layout is a set of its own: the b edition's client gets it.
        self.assertEqual(read(os.path.join(client, "default.ini")), INI_PATCHED)

    def test_a_refused_default_layout_warns_and_classic_creation_still_applies(self):
        self.write_layout_set(sha(b"a default.ini changed by hand"))
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertIn(UNKNOWN_INI, r.stdout)
        self.assertEqual(r.stderr, LAYOUT_WARNING)
        client = os.path.join(dest, "client")
        self.assertEqual(read(os.path.join(client, "default.ini")), INI)
        self.assertFalse(os.path.exists(os.path.join(client, "default.ini.hearthdaoc-orig")))
        self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
        self.assertTrue(os.path.isfile(os.path.join(dest, "play.sh")))

    def test_refused_shrouded_isles_sets_warn_each_and_the_other_sets_still_apply(self):
        for refused, warning in (("si-portals.json", SI_WARNING), ("si-portal-art.json", ART_WARNING)):
            with self.subTest(refused):
                shutil.rmtree(os.path.join(self.dir, "dest"), ignore_errors=True)
                self.write_si_sets(**{"archive_before" if refused == "si-portals.json" else "model_before":
                                      sha(b"another client's file")})
                dest, r = self.setup_sh()
                self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
                self.assertEqual(r.stderr, warning)
                path = ARCHIVE_PATH if refused == "si-portals.json" else MODEL_PATH
                self.assertIn(f"Not patched: {path} is not the file this HearthDAoC release supports (our test's).",
                              r.stdout)
                client = os.path.join(dest, "client")
                self.assertEqual(read(os.path.join(client, *ARCHIVE_PATH.split("/"))),
                                 ARCHIVE if refused == "si-portals.json" else ARCHIVE_PATCHED)
                self.assertEqual(os.path.exists(os.path.join(client, *MODEL_PATH.split("/"))),
                                 refused == "si-portal-art.json")
                self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
                self.assertEqual(read(os.path.join(client, "default.ini")), INI_PATCHED)

    def test_a_release_that_drops_the_art_set_gives_the_2006_model_back(self):
        # The switch to the 2006 look is a release (FORK.md): one whose setup.sh no longer applies
        # si-portal-art.json builds the client afresh, so the model is back and no backup of it stays.
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        model = os.path.join(dest, "client", *MODEL_PATH.split("/"))
        self.assertFalse(os.path.exists(model))
        with open(os.path.join(self.bundle, "setup.sh"), encoding="utf-8") as f:
            script = f.read()
        for part, new in ((" si-portal-art.json splash.mpk)", " splash.mpk)"),  # PATCH_FILES, and the call
                          ("\napply_set si-portal-art.json ", "\n: dropped ")):
            self.assertEqual(script.count(part), 1, part)
            script = script.replace(part, new)
        self.assertEqual([line for line in script.splitlines()
                          if "si-portal-art.json" in line and not line.lstrip().startswith("#")], [])
        with open(os.path.join(self.bundle, "setup.sh"), "w", encoding="utf-8") as f:
            f.write(script)
        os.remove(os.path.join(self.patches, "si-portal-art.json"))
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertEqual(read(model), MODEL)
        self.assertFalse(os.path.exists(model + ".hearthdaoc-orig"))
        self.assertNotIn("si-portal-art.json", os.listdir(os.path.join(dest, "patches")))

    def test_a_release_with_the_art_set_retired_gives_the_2006_model_back(self):
        # The other way (and the one for Windows clients, which are never built afresh): the set ships with
        # "retired": true, and setup.sh, like play.sh at every launch, restores it instead of applying it.
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.write_si_sets(retired=True)
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertEqual(r.stderr, "")
        self.assertIn(f"Nothing to restore: {MODEL_PATH}\n", r.stdout)  # the client was built afresh
        model = os.path.join(dest, "client", *MODEL_PATH.split("/"))
        self.assertEqual(read(model), MODEL)
        self.assertFalse(os.path.exists(model + ".hearthdaoc-orig"))
        self.assertEqual(read(os.path.join(dest, "client", *ARCHIVE_PATH.split("/"))), ARCHIVE_PATCHED)

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

    def write_content_id(self, text):
        with open(os.path.join(self.bundle, "CONTENT_ID"), "w", encoding="utf-8") as f:
            f.write(text + "\n")  # as deploy/build_bundles.sh writes it

    def test_setup_saves_its_settings_and_the_release_for_play_sh(self):
        self.write_version("v0.35b-hearth.2")
        self.write_content_id("c" * 64)
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertEqual(saved_settings(dest), {"server": "192.168.1.64:10301", "edition": "classic",
                                                "base_client": self.base, "tag": "v0.35b-hearth.2",
                                                "content_id": "c" * 64})
        self.assertEqual(leftovers(dest), [])

    def test_a_bundle_without_a_valid_content_id_saves_none(self):
        # A bundle from before content IDs, or a damaged file: play.sh then offers every newer release.
        self.write_version("v0.35b-hearth.2")
        for text in (None, "not an id"):
            with self.subTest(text=text):
                if text is not None:
                    self.write_content_id(text)
                dest, r = self.setup_sh()
                self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
                self.assertEqual(saved_settings(dest)["content_id"], "")

    def test_a_relative_base_client_path_with_spaces_is_saved_in_full(self):
        # play.sh runs the next setup.sh from another folder: the saved path must be absolute.
        games = os.path.join(self.dir, "my games")
        shutil.copytree(self.base, os.path.join(games, "1.127 client"))
        self.write_version("v0.35b-hearth.2")
        dest, r = self.setup_sh(base="1.127 client", cwd=games)
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        self.assertEqual(saved_settings(dest)["base_client"], os.path.join(games, "1.127 client"))

    def test_running_setup_again_replaces_play_sh_by_rename(self):
        # A running play.sh that updates itself runs the next setup.sh: bash goes on reading its own
        # file, so the new play.sh must be a new file, not the old one written over.
        self.write_version("v0.35b-hearth.2")
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        play = os.path.join(dest, "play.sh")
        with open(play, "rb") as running:
            old = running.read()
            inode = os.fstat(running.fileno()).st_ino
            self.write_version("v0.35b-hearth.3")
            dest, r = self.setup_sh()
            self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
            running.seek(0)
            self.assertEqual(running.read(), old)  # what the running play.sh still reads
        self.assertNotEqual(os.stat(play).st_ino, inode)
        self.assertTrue(os.stat(play).st_mode & stat.S_IXUSR)
        self.assertEqual(saved_settings(dest)["tag"], "v0.35b-hearth.3")
        self.assertEqual(leftovers(dest), [])

    def test_a_failed_setup_over_an_install_leaves_it_as_it_was(self):
        # play.sh updates by running the next release's setup.sh. When that fails, play.sh starts the game
        # with the installed release, and the saved release is unchanged, so the next launch offers it again.
        self.write_version("v0.35b-hearth.2")
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        before = install_state(dest)
        self.write_version("v0.35b-hearth.3")
        with self.subTest("the download stops"):
            dest, r = self.setup_sh(release=self.broken_release())
            self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
            self.assertIn("SHA-256 mismatch against the release manifest", r.stderr)
            self.assertEqual(install_state(dest), before)
            self.assertEqual(leftovers(dest), [])
        with self.subTest("the patches fail"):
            with open(os.path.join(self.patches, "classic-creation.json"), "w", encoding="utf-8") as f:
                json.dump({"format": 2}, f)
            dest, r = self.setup_sh()
            self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
            self.assertIn("Patching the client failed (apply_patches.py exit 2", r.stderr)
            self.assertEqual(install_state(dest), before)
            self.assertEqual(leftovers(dest), [])

    def test_running_setup_again_keeps_the_game_logs_with_or_without_hard_links(self):
        # rsync leaves the game's logs alone (excluded), so client.new starts as a copy of the client: hard
        # links, or a full copy on a file system without them.
        no_hard_links = os.path.join(self.dir, "bin")
        os.makedirs(no_hard_links)
        with open(os.path.join(no_hard_links, "cp"), "w", encoding="utf-8") as f:
            f.write(f'#!/bin/sh\n[ "$1" != -al ] || exit 1\nexec {shutil.which("cp")} "$@"\n')
        os.chmod(os.path.join(no_hard_links, "cp"), 0o755)
        for name, path in (("hard links", os.environ["PATH"]),
                           ("no hard links", no_hard_links + os.pathsep + os.environ["PATH"])):
            with self.subTest(name):
                dest, r = self.setup_sh()
                self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
                log = os.path.join(dest, "client", "logs", "chat.log")
                os.makedirs(os.path.dirname(log), exist_ok=True)
                with open(log, "w", encoding="utf-8") as f:
                    f.write(name)
                dest, r = self.setup_sh(env={**os.environ, "PATH": path})
                self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
                self.assertNotIn("Warning", r.stderr)
                self.assertEqual(read(log), name.encode())
                self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.dll_patched)
                self.assertEqual(leftovers(dest), [])

    def test_running_setup_again_after_one_cut_short_starts_afresh(self):
        # A setup cut short (a power cut) leaves what it built beside the install. The next one, failed or
        # not, uses none of it: a failed one leaves the install as it was.
        self.write_version("v0.35b-hearth.2")
        dest, r = self.setup_sh()
        self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
        before = install_state(dest)

        def cut_short():
            for name in ("client.new", "client.old", "patches.old"):
                os.makedirs(os.path.join(dest, name, "client"), exist_ok=True)
                with open(os.path.join(dest, name, "half.bin"), "wb") as f:
                    f.write(b"cut short")
        self.write_version("v0.35b-hearth.3")
        with self.subTest("the next one fails"):
            cut_short()
            dest, r = self.setup_sh(release=self.broken_release())
            self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
            self.assertEqual(install_state(dest), before)
            self.assertEqual(leftovers(dest), [])
        with self.subTest("the next one works"):
            cut_short()
            dest, r = self.setup_sh()
            self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
            self.assertEqual(leftovers(dest), [])
            client = os.path.join(dest, "client")
            self.assertEqual(read(os.path.join(client, "game.dll")), self.dll_patched)
            self.assertFalse(os.path.exists(os.path.join(client, "half.bin")))
            installed_patches(self, dest, self.patches)
            self.assertEqual(saved_settings(dest)["tag"], "v0.35b-hearth.3")

    def test_a_failed_patch_fails_setup(self):
        # An invalid patch set (exit 2), either one. The message names the installed copy: setup.sh applies
        # <dest>/patches, the files play.sh applies at every launch, not the bundle's.
        for name in ("classic-creation.json", "default-layout.json", "si-portals.json", "si-portal-art.json"):
            with self.subTest(name):
                shutil.rmtree(os.path.join(self.dir, "dest"), ignore_errors=True)
                kept = read(os.path.join(self.patches, name))
                with open(os.path.join(self.patches, name), "w", encoding="utf-8") as f:
                    json.dump({"format": 2}, f)
                try:
                    dest, r = self.setup_sh()
                finally:
                    with open(os.path.join(self.patches, name), "wb") as f:
                        f.write(kept)
                self.assertEqual(r.returncode, 1, r.stderr + r.stdout)
                installed = os.path.join(dest, "patches", name)
                self.assertIn(f"Error: invalid patch set: {installed}: not a format 1 patch set", r.stderr)
                self.assertIn("Patching the client failed (apply_patches.py exit 2, see the message above).", r.stderr)
                if name == "classic-creation.json":  # the first set: nothing was patched
                    self.assertEqual(read(os.path.join(dest, "client", "game.dll")), self.dll)
                self.assertFalse(os.path.exists(os.path.join(dest, "play.sh")))
                self.assertFalse(os.path.exists(os.path.join(dest, CONF)))

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
        self.assertEqual(r.stderr.count("Missing patches/apply_patches.py next to setup.sh; download the full client bundle."), 1)
        self.assertFalse(os.path.exists(dest))


if __name__ == "__main__":
    unittest.main()
