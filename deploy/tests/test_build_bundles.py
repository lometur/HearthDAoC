import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.abspath(os.path.join(HERE, "..", "build_bundles.sh"))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
PATCHES = os.path.join(REPO, "client", "patches")
# OfflineDAoC 0.34's own pregame/splash.mpk: upstream keeps a byte-identical copy in the repo.
STOCK_SPLASH = os.path.join(REPO, "source", "tools", "OfflineDaoc.Launcher", "Assets", "offline-daoc-client-splash.mpk")
PWSH = os.environ.get("HDC_PWSH") or shutil.which("pwsh")

TAG = "v0.34b-hearth.99"
CLIENT_FILES = {
    "README.md", "VERSION", "CONTENT_ID", "setup.sh", "play.sh.in", "odaoc_fetch.py", "upstream.lock",
    "patches/classic-creation.json", "patches/apply_patches.py", "patches/patchset.py", "patches/splash.mpk",
    "windows/connect-hearthdaoc.bat", "windows/patch-client.bat", "windows/patch-client.ps1",
    "windows/patches/classic-creation.json", "windows/patches/splash.mpk",
}
FROM_REPO = {  # bundled byte for byte (the .bat and .ps1 keep their CRLF line ends)
    "patches/classic-creation.json": "client/patches/classic-creation.json",
    "patches/apply_patches.py": "client/patches/apply_patches.py",
    "patches/patchset.py": "client/patches/patchset.py",
    "patches/splash.mpk": "client/patches/splash.mpk",
    "windows/patch-client.bat": "client/windows/patch-client.bat",
    "windows/patch-client.ps1": "client/windows/patch-client.ps1",
    "windows/patches/classic-creation.json": "client/patches/classic-creation.json",
    "windows/patches/splash.mpk": "client/patches/splash.mpk",
}
EXECUTABLE = {"setup.sh", "patches/apply_patches.py"}
SPLASH_PATCHED = "Patched: pregame/splash.mpk (original saved as pregame/splash.mpk.hearthdaoc-orig)\n"


def read(path):
    with open(path, "rb") as f:
        return f.read()


def sha256(path):
    return hashlib.sha256(read(path)).hexdigest()


def content_id(bundle):
    """The client's content ID, worked out here independently: SHA-256 of sha256sum-style lines for every
    bundled file but VERSION and CONTENT_ID, in path order."""
    lines = []
    for folder, _dirs, names in os.walk(bundle):
        for name in names:
            rel = os.path.relpath(os.path.join(folder, name), bundle).replace(os.sep, "/")
            if rel not in ("VERSION", "CONTENT_ID"):
                lines.append((rel, f"{sha256(os.path.join(folder, name))}  {rel}\n"))
    return hashlib.sha256("".join(line for _rel, line in sorted(lines)).encode()).hexdigest()


def splash_only(patches):
    """Write <patches>/splash-only.json, the bundle's patch set cut to its splash entry; return its path.
    The other patched files are EA's, so tests without them use the splash entry alone."""
    with open(os.path.join(patches, "classic-creation.json"), encoding="utf-8") as f:
        data = json.load(f)
    data["files"] = [entry for entry in data["files"] if entry["path"] == "pregame/splash.mpk"]
    path = os.path.join(patches, "splash-only.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f)
    return path


class BuildBundlesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def build(self, out, cwd):
        r = subprocess.run(["bash", SCRIPT, TAG, out], cwd=cwd, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r

    def unpack(self, name):
        """Build both bundles into <tmp>/<name> and unzip the client bundle there; return its folder."""
        out = os.path.join(self.tmp.name, name)
        self.build(out, REPO)
        with zipfile.ZipFile(os.path.join(out, f"hearthdaoc-client-{TAG}.zip")) as z:
            z.extractall(out)
        return os.path.join(out, f"hearthdaoc-client-{TAG}")

    def check_deploy(self, out):
        with tarfile.open(os.path.join(out, f"hearthdaoc-deploy-{TAG}.tar.gz")) as t:
            names = {os.path.normpath(n) for n in t.getnames()}
            env = t.extractfile("./.env.example").read().decode()
        self.assertTrue({"compose.yml", ".env.example", "hdc", "HANDOFF.md", "upstream.lock"} <= names)
        self.assertIn(f"HEARTHDAOC_TAG={TAG}", env.splitlines())

    def check(self, out):
        self.check_deploy(out)
        top = f"hearthdaoc-client-{TAG}/"
        with zipfile.ZipFile(os.path.join(out, f"hearthdaoc-client-{TAG}.zip")) as z:
            files = {i.filename[len(top):]: i for i in z.infolist() if not i.is_dir()}
            self.assertEqual(set(files), CLIENT_FILES)  # nothing else: no tests, branding or __pycache__
            for name in EXECUTABLE:
                self.assertTrue((files[name].external_attr >> 16) & 0o100, name)
            for name, src in FROM_REPO.items():
                self.assertEqual(z.read(top + name), read(os.path.join(REPO, src)), name)
            self.assertEqual(z.read(top + "VERSION"), f"{TAG}\n".encode())  # setup.sh saves it; play.sh's updater compares it
            extracted = os.path.join(self.tmp.name, "unzipped")
            z.extractall(extracted)
        # The content ID: in the bundle and beside it (a release asset), the same, and of these files.
        bundle = os.path.join(extracted, top)
        ident = read(os.path.join(bundle, "CONTENT_ID")).decode()
        self.assertRegex(ident, r"\A[0-9a-f]{64}\n\Z")
        self.assertEqual(read(os.path.join(out, f"hearthdaoc-client-{TAG}.content-id")).decode(), ident)
        self.assertEqual(ident.strip(), content_id(bundle))
        return bundle

    def test_the_content_id_names_the_client_not_the_release(self):
        # Two releases of the same client have the same ID (play.sh offers neither over the other); the ID
        # follows the bundled files (check() recomputes it from them).
        ids = []
        for tag in (TAG, "v0.34b-hearth.100"):
            out = os.path.join(self.tmp.name, tag)
            r = subprocess.run(["bash", SCRIPT, tag, out], cwd=REPO, capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)
            ids.append(read(os.path.join(out, f"hearthdaoc-client-{tag}.content-id")))
        self.assertEqual(ids[0], ids[1])

    def test_relative_output_folder_like_ci(self):
        cwd = self.tmp.name
        self.build("dist", cwd)
        self.check(os.path.join(cwd, "dist"))

    def test_absolute_output_folder(self):
        out = os.path.join(self.tmp.name, "out")
        self.build(out, tempfile.gettempdir())
        bundle = self.check(out)
        # The bundled applier runs from the unpacked bundle with its own patch set.
        with open(os.path.join(PATCHES, "classic-creation.json"), encoding="utf-8") as f:
            paths = [entry["path"] for entry in json.load(f)["files"]]
        empty = os.path.join(self.tmp.name, "empty-client")
        os.makedirs(empty)
        r = subprocess.run([sys.executable, os.path.join(bundle, "patches", "apply_patches.py"),
                            "--client", empty, "--check"], capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (3, "".join(f"{p}: missing\n" for p in paths)), r.stderr)
        self.assertFalse(os.path.exists(os.path.join(bundle, "patches", "__pycache__")))

    def test_every_build_bundles_the_same_splash_mpk(self):
        # A rebuilt splash.mpk carries new timestamps, so a new SHA-256: the bundles carry the committed one.
        hashes = set()
        for name in ("first", "second"):
            bundle = self.unpack(name)
            hashes |= {sha256(os.path.join(bundle, "patches", "splash.mpk")),
                       sha256(os.path.join(bundle, "windows", "patches", "splash.mpk"))}
        self.assertEqual(hashes, {sha256(os.path.join(PATCHES, "splash.mpk"))})

    def test_a_client_patched_by_one_release_is_patched_for_the_next(self):
        # The next release's appliers take a client patched by an earlier release as patched, and restore it.
        older, newer = self.unpack("older"), self.unpack("newer")
        appliers = {
            "python": lambda client, *switches: [
                sys.executable, os.path.join(newer, "patches", "apply_patches.py"), "--client", client,
                "--patchset", splash_only(os.path.join(newer, "patches")), *switches],
            "powershell": lambda client, *switches: [
                PWSH, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                os.path.join(newer, "windows", "patch-client.ps1"), "-Client", client,
                "-PatchSet", splash_only(os.path.join(newer, "windows", "patches")), *switches],
        }
        restore = {"python": "--restore", "powershell": "-Restore"}
        for name, command in appliers.items():
            with self.subTest(name):
                if name == "powershell" and not PWSH:
                    self.skipTest("pwsh is not installed (HDC_PWSH may name it)")
                client = os.path.join(self.tmp.name, "client-" + name)
                os.makedirs(os.path.join(client, "pregame"))
                splash = os.path.join(client, "pregame", "splash.mpk")
                shutil.copyfile(STOCK_SPLASH, splash)
                r = subprocess.run([sys.executable, os.path.join(older, "patches", "apply_patches.py"),
                                    "--client", client, "--patchset", splash_only(os.path.join(older, "patches"))],
                                   capture_output=True, text=True)
                self.assertEqual((r.returncode, r.stdout), (0, SPLASH_PATCHED), r.stderr)
                r = subprocess.run(command(client), capture_output=True, text=True, timeout=120)
                self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Already patched: pregame/splash.mpk\n", ""))
                r = subprocess.run(command(client, restore[name]), capture_output=True, text=True, timeout=120)
                self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Restored: pregame/splash.mpk\n", ""))
                self.assertEqual(sha256(splash), sha256(STOCK_SPLASH))

    def test_a_splash_mpk_without_the_pinned_hash_is_not_bundled(self):
        # Its appliers would refuse to patch with it: nothing is built, not even the output folder.
        repo = os.path.join(self.tmp.name, "repo")
        for rel in ("deploy/build_bundles.sh", "client/patches/classic-creation.json"):
            os.makedirs(os.path.dirname(os.path.join(repo, rel)), exist_ok=True)
            shutil.copy(os.path.join(REPO, rel), os.path.join(repo, rel))
        splash = os.path.join(repo, "client", "patches", "splash.mpk")
        with open(splash, "wb") as f:
            f.write(b"a splash.mpk from an older splash.png")
        with open(os.path.join(PATCHES, "classic-creation.json"), encoding="utf-8") as f:
            pinned = [entry["after"] for entry in json.load(f)["files"] if entry["path"] == "pregame/splash.mpk"]
        out = os.path.join(self.tmp.name, "out")
        stale = (f"build_bundles.sh: client/patches/splash.mpk has SHA-256 {sha256(splash)}, but "
                 f"client/patches/classic-creation.json pins {pinned[0]}: commit splash.png, splash.mpk and the "
                 "rebuilt patch set together (docs/fork/FORK.md, Client patches)\n")
        missing = "build_bundles.sh: cannot read client/patches/splash.mpk: No such file or directory\n"
        for name, expected in (("stale", stale), ("missing", missing)):
            if name == "missing":
                os.remove(splash)
            with self.subTest(name):
                r = subprocess.run(["bash", os.path.join(repo, "deploy", "build_bundles.sh"), TAG, out],
                                   capture_output=True, text=True)
                self.assertEqual((r.returncode, r.stdout, r.stderr), (1, "", expected))
                self.assertFalse(os.path.exists(out))

    def test_deploy_only(self):
        # deploy/tests/hdc_integration.sh needs only the deploy bundle.
        out = os.path.join(self.tmp.name, "out")
        r = subprocess.run(["bash", SCRIPT, TAG, out, "--deploy-only"], capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (0, f"Built {out}/hearthdaoc-deploy-{TAG}.tar.gz\n"), r.stderr)
        self.assertEqual(os.listdir(out), [f"hearthdaoc-deploy-{TAG}.tar.gz"])
        self.check_deploy(out)
        r = subprocess.run(["bash", SCRIPT, TAG, out, "--client-only"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 2)
        self.assertIn("usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]", r.stderr)

    def test_repo_leaves_the_tag_to_the_bundle(self):
        # The release tag is stamped into the bundle (hdc update reads it there); the repo names none.
        root = os.path.abspath(os.path.join(HERE, "..", ".."))
        with open(os.path.join(root, "deploy", ".env.example")) as f:
            self.assertEqual([line for line in f.read().splitlines() if line.startswith("HEARTHDAOC_TAG")], ["HEARTHDAOC_TAG="])
        with open(os.path.join(root, "deploy", "HANDOFF.md")) as f:
            self.assertEqual(re.findall(r"v\d+(?:\.\d+)*[a-z]?-hearth\.\d+", f.read()), [])


if __name__ == "__main__":
    unittest.main()
