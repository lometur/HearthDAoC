import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.join(HERE, "..")
sys.path.insert(0, DEPLOY)

import release_tag as rt  # noqa: E402

HANDOFF = """# Handoff
curl -fLO https://github.com/lometur/HearthDAoC/releases/download/{t}/hearthdaoc-deploy-{t}.tar.gz
tar xzf hearthdaoc-deploy-{t}.tar.gz
## Upgrading
curl -fLO https://github.com/lometur/HearthDAoC/releases/download/<new tag>/hearthdaoc-deploy-<new tag>.tar.gz
"""


class ReleaseTagTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        os.makedirs(os.path.join(self.root, "deploy"))
        self.write("v0.34b-hearth.3", "v0.34b-hearth.3")
        with open(os.path.join(self.root, "deploy", "upstream.lock"), "w") as f:
            json.dump({"version": "0.34b"}, f)

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, env_tag, handoff_tag):
        with open(os.path.join(self.root, "deploy", ".env.example"), "w") as f:
            f.write(f"# settings\nHEARTHDAOC_TAG={env_tag}\nHEARTHDAOC_EDITION=classic\n")
        with open(os.path.join(self.root, "deploy", "HANDOFF.md"), "w") as f:
            f.write(HANDOFF.format(t=handoff_tag))

    def test_matching_docs_pass(self):
        self.assertEqual(rt.check(self.root, "v0.34b-hearth.3"), [])

    def test_stale_env_example_is_reported(self):
        self.write("v0.34b-hearth.1", "v0.34b-hearth.3")
        self.assertTrue(any(".env.example" in p for p in rt.check(self.root, "v0.34b-hearth.3")))

    def test_stale_handoff_is_reported(self):
        self.write("v0.34b-hearth.3", "v0.34b-hearth.1")
        self.assertTrue(any("HANDOFF.md" in p for p in rt.check(self.root, "v0.34b-hearth.3")))

    def test_tag_for_another_upstream_version_is_reported(self):
        self.write("v0.35b-hearth.1", "v0.35b-hearth.1")
        self.assertTrue(any("upstream.lock" in p for p in rt.check(self.root, "v0.35b-hearth.1")))

    def test_malformed_tag_is_refused(self):
        for bad in ("0.34b-hearth.3", "v0.34b-fork.3", "v0.34b-hearth.x"):
            with self.subTest(tag=bad), self.assertRaises(rt.TagError):
                rt.check(self.root, bad)

    def test_bump_updates_both_and_keeps_placeholders(self):
        self.write("v0.34b-hearth.1", "v0.34b-hearth.1")
        rt.bump(self.root, "v0.34b-hearth.4")
        self.assertEqual(rt.check(self.root, "v0.34b-hearth.4"), [])
        with open(os.path.join(self.root, "deploy", "HANDOFF.md")) as f:
            self.assertIn("download/<new tag>/", f.read())

    def test_cli(self):
        run = lambda *a: subprocess.run([sys.executable, os.path.join(DEPLOY, "release_tag.py"), "--root", self.root, *a],  # noqa: E731
                                        capture_output=True, text=True)
        self.assertEqual(run("check", "v0.34b-hearth.3").returncode, 0)
        r = run("check", "v0.34b-hearth.4")
        self.assertEqual(r.returncode, 1)
        self.assertIn("release_tag.py bump", r.stderr)

    def test_repository_docs_name_the_same_release(self):
        root = os.path.abspath(os.path.join(DEPLOY, ".."))
        with open(os.path.join(root, "deploy", ".env.example")) as f:
            tag = next(line.split("=", 1)[1].strip() for line in f if line.startswith("HEARTHDAOC_TAG="))
        self.assertEqual(rt.check(root, tag), [])


if __name__ == "__main__":
    unittest.main()
