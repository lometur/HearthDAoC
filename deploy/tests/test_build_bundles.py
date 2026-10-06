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
sys.path.insert(0, PATCHES)
sys.path.insert(0, os.path.join(PATCHES, "branding"))
import build_splash_mpk  # noqa: E402
import mpk  # noqa: E402
import splash_entry  # noqa: E402

TAG = "v0.34b-hearth.99"
CLIENT_FILES = {
    "README.md", "setup.sh", "play.sh.in", "odaoc_fetch.py", "upstream.lock",
    "patches/classic-creation.json", "patches/apply_patches.py", "patches/patchset.py", "patches/splash.mpk",
    "windows/connect-hearthdaoc.bat", "windows/patch-client.bat", "windows/patch-client.ps1",
    "windows/patches/classic-creation.json", "windows/patches/splash.mpk",
}
FROM_REPO = {  # bundled byte for byte (the .bat and .ps1 keep their CRLF line ends)
    "patches/classic-creation.json": "client/patches/classic-creation.json",
    "patches/apply_patches.py": "client/patches/apply_patches.py",
    "patches/patchset.py": "client/patches/patchset.py",
    "windows/patch-client.bat": "client/windows/patch-client.bat",
    "windows/patch-client.ps1": "client/windows/patch-client.ps1",
    "windows/patches/classic-creation.json": "client/patches/classic-creation.json",
}
EXECUTABLE = {"setup.sh", "patches/apply_patches.py"}

# Stands in for `dotnet OfflineDaoc.Mpk.dll pack <folder> <archive> <name>`: logs its arguments
# and packs the folder the way upstream's MPK tool does (the layout client/patches/mpk.py reads).
FAKE_DOTNET = r'''
import json, os, struct, sys, zlib
with open(os.environ["FAKE_DOTNET_LOG"], "w") as f:
    json.dump(sys.argv[1:], f)
_tool, _pack, folder, archive, name = sys.argv[1:]
names = sorted(os.listdir(folder))
blobs, directory, offset, data_offset = [], b"", 0, 0
for entry in names:
    with open(os.path.join(folder, entry), "rb") as f:
        data = f.read()
    blob = zlib.compress(data, 1)
    directory += entry.encode().ljust(256, b"\0") + struct.pack(
        "<IiIIIII", 0, 4, offset, len(data), data_offset, len(blob), zlib.crc32(blob))
    offset, data_offset = offset + len(data), data_offset + len(blob)
    blobs.append(blob)
packed_dir, packed_name = zlib.compress(directory), zlib.compress(name.encode())
head = struct.pack("<IIII", zlib.crc32(packed_dir), len(packed_dir), len(packed_name), len(names))
with open(archive, "wb") as f:
    f.write(b"MPAK\x02" + bytes(b ^ i for i, b in enumerate(head)) + packed_name + packed_dir + b"".join(blobs))
'''


def read(path):
    with open(path, "rb") as f:
        return f.read()


class BuildBundlesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        bin_dir = os.path.join(self.tmp.name, "bin")
        os.makedirs(bin_dir)
        with open(os.path.join(bin_dir, "dotnet"), "w") as f:
            f.write(f"#!{sys.executable}\n{FAKE_DOTNET}")
        os.chmod(os.path.join(bin_dir, "dotnet"), 0o755)
        self.tool = os.path.join(self.tmp.name, "tool", "OfflineDaoc.Mpk.dll")
        os.makedirs(os.path.dirname(self.tool))
        open(self.tool, "wb").close()
        self.log = os.path.join(self.tmp.name, "dotnet.log")
        self.env = dict(os.environ, PATH=bin_dir + os.pathsep + os.environ["PATH"],
                        HDC_MPK_TOOL=self.tool, FAKE_DOTNET_LOG=self.log)

    def build(self, out, cwd, env=None):
        r = subprocess.run(["bash", SCRIPT, TAG, out], cwd=cwd, env=env or self.env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r

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
            extracted = os.path.join(self.tmp.name, "unzipped")
            z.extractall(extracted)
        bundle = os.path.join(extracted, top)
        splash = os.path.join(bundle, "patches", "splash.mpk")
        self.assertEqual(read(splash), read(os.path.join(bundle, "windows", "patches", "splash.mpk")))
        return bundle, splash

    def check_splash_from_png(self, splash):
        """The bundled splash.mpk holds branding/splash.png as the client's 8 TGAs."""
        splash_entry.check_splash_mpk(splash)
        png = os.path.join(PATCHES, "branding", "splash.png")
        tga = build_splash_mpk.tga_bytes(*build_splash_mpk.read_png(png))
        _name, entries = mpk.read_mpk(splash)
        self.assertTrue(all(data == tga for _entry, data in entries))

    def test_relative_output_folder_like_ci(self):
        cwd = self.tmp.name
        env = dict(self.env, HDC_MPK_TOOL=os.path.join("tool", "OfflineDaoc.Mpk.dll"))
        self.build("dist", cwd, env)
        self.check(os.path.join(cwd, "dist"))
        with open(self.log) as f:
            args = json.load(f)
        self.assertEqual((os.path.realpath(args[0]), args[1], args[-1]),
                         (os.path.realpath(self.tool), "pack", "splash.mpk"))

    def test_absolute_output_folder(self):
        out = os.path.join(self.tmp.name, "out")
        self.build(out, tempfile.gettempdir())
        bundle, splash = self.check(out)
        self.check_splash_from_png(splash)
        # The bundled applier runs from the unpacked bundle with its own patch set.
        with open(os.path.join(PATCHES, "classic-creation.json"), encoding="utf-8") as f:
            paths = [entry["path"] for entry in json.load(f)["files"]]
        empty = os.path.join(self.tmp.name, "empty-client")
        os.makedirs(empty)
        r = subprocess.run([sys.executable, os.path.join(bundle, "patches", "apply_patches.py"),
                            "--client", empty, "--check"], capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (3, "".join(f"{p}: missing\n" for p in paths)), r.stderr)
        self.assertFalse(os.path.exists(os.path.join(bundle, "patches", "__pycache__")))

    def test_without_the_mpk_tool_nothing_is_built(self):
        out = os.path.join(self.tmp.name, "out")
        for tool in (None, os.path.join(self.tmp.name, "missing.dll")):
            env = dict(self.env)
            env.pop("HDC_MPK_TOOL")
            if tool:
                env["HDC_MPK_TOOL"] = tool
            r = subprocess.run(["bash", SCRIPT, TAG, out], env=env, capture_output=True, text=True)
            self.assertEqual(r.returncode, 2, tool)
            self.assertIn("build_bundles.sh: set HDC_MPK_TOOL to upstream's OfflineDaoc.Mpk.dll", r.stderr)
            self.assertFalse(os.path.exists(out))

    def test_deploy_only_needs_no_mpk_tool(self):
        # deploy/tests/hdc_integration.sh needs only the deploy bundle; CI's test job has no MPK tool.
        out = os.path.join(self.tmp.name, "out")
        env = dict(self.env)
        env.pop("HDC_MPK_TOOL")
        r = subprocess.run(["bash", SCRIPT, TAG, out, "--deploy-only"], env=env, capture_output=True, text=True)
        self.assertEqual((r.returncode, r.stdout), (0, f"Built {out}/hearthdaoc-deploy-{TAG}.tar.gz\n"), r.stderr)
        self.assertEqual(os.listdir(out), [f"hearthdaoc-deploy-{TAG}.tar.gz"])
        self.check_deploy(out)
        self.assertFalse(os.path.exists(self.log))  # no splash.mpk was packed
        r = subprocess.run(["bash", SCRIPT, TAG, out, "--client-only"], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 2)
        self.assertIn("usage: deploy/build_bundles.sh <tag> <output dir> [--deploy-only]", r.stderr)

    @unittest.skipUnless(os.environ.get("HDC_MPK_TOOL") and shutil.which("dotnet"),
                         "set HDC_MPK_TOOL to OfflineDaoc.Mpk.dll (needs dotnet)")
    def test_with_the_real_mpk_tool(self):
        out = os.path.join(self.tmp.name, "out")
        self.build(out, REPO, dict(os.environ))
        _bundle, splash = self.check(out)
        self.check_splash_from_png(splash)

    def test_repo_leaves_the_tag_to_the_bundle(self):
        # The release tag is stamped into the bundle (hdc update reads it there); the repo names none.
        root = os.path.abspath(os.path.join(HERE, "..", ".."))
        with open(os.path.join(root, "deploy", ".env.example")) as f:
            self.assertEqual([line for line in f.read().splitlines() if line.startswith("HEARTHDAOC_TAG")], ["HEARTHDAOC_TAG="])
        with open(os.path.join(root, "deploy", "HANDOFF.md")) as f:
            self.assertEqual(re.findall(r"v\d+(?:\.\d+)*[a-z]?-hearth\.\d+", f.read()), [])


if __name__ == "__main__":
    unittest.main()
