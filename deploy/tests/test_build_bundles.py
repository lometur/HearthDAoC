import os
import re
import subprocess
import tarfile
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.abspath(os.path.join(HERE, "..", "build_bundles.sh"))


class BuildBundlesTests(unittest.TestCase):
    def build(self, out, cwd):
        r = subprocess.run(["bash", SCRIPT, "v0.34b-hearth.99", out], cwd=cwd, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)

    def check(self, out):
        with tarfile.open(os.path.join(out, "hearthdaoc-deploy-v0.34b-hearth.99.tar.gz")) as t:
            names = {os.path.normpath(n) for n in t.getnames()}
            env = t.extractfile("./.env.example").read().decode()
        self.assertTrue({"compose.yml", ".env.example", "hdc", "HANDOFF.md", "upstream.lock"} <= names)
        self.assertIn("HEARTHDAOC_TAG=v0.34b-hearth.99", env.splitlines())
        with zipfile.ZipFile(os.path.join(out, "hearthdaoc-client-v0.34b-hearth.99.zip")) as z:
            self.assertIn("hearthdaoc-client-v0.34b-hearth.99/setup.sh", z.namelist())

    def test_relative_output_folder_like_ci(self):
        with tempfile.TemporaryDirectory() as cwd:
            self.build("dist", cwd)
            self.check(os.path.join(cwd, "dist"))

    def test_absolute_output_folder(self):
        with tempfile.TemporaryDirectory() as out:
            self.build(out, tempfile.gettempdir())
            self.check(out)

    def test_repo_leaves_the_tag_to_the_bundle(self):
        # The release tag is stamped into the bundle (hdc update reads it there); the repo names none.
        root = os.path.abspath(os.path.join(HERE, "..", ".."))
        with open(os.path.join(root, "deploy", ".env.example")) as f:
            self.assertEqual([line for line in f.read().splitlines() if line.startswith("HEARTHDAOC_TAG")], ["HEARTHDAOC_TAG="])
        with open(os.path.join(root, "deploy", "HANDOFF.md")) as f:
            self.assertEqual(re.findall(r"v\d+(?:\.\d+)*[a-z]?-hearth\.\d+", f.read()), [])


if __name__ == "__main__":
    unittest.main()
