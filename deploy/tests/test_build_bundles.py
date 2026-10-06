import os
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
        self.assertIn("HEARTHDAOC_TAG=v0.34b-hearth.99", env)
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


if __name__ == "__main__":
    unittest.main()
