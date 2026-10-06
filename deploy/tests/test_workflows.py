import os
import re
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))


def read(name):
    with open(os.path.join(ROOT, ".github", "workflows", name), encoding="utf-8") as f:
        return f.read()


class ReleasePrTriggerTests(unittest.TestCase):
    """The release PR bot must also run after a build on main finishes: merges that land while a release
    is still publishing are skipped as 'pending', and nothing else would bring them back."""

    def setUp(self):
        self.text = read("release-pr.yml")
        self.on = self.text.split("\non:", 1)[1].split("\npermissions:", 1)[0]

    def test_runs_after_the_server_image_workflow_on_main(self):
        self.assertRegex(self.on, r"workflow_run:\s*\n\s+workflows:\s*\[\s*server-image\s*\]")
        self.assertRegex(self.on, r"types:\s*\[\s*completed\s*\]")
        self.assertRegex(self.on, r"branches:\s*\[\s*main\s*\]")

    def test_can_be_started_by_hand(self):
        self.assertIn("workflow_dispatch:", self.on)

    def test_still_runs_on_merges_to_main(self):
        self.assertRegex(self.on, r"push:\s*\n\s+branches:\s*\[\s*main\s*\]")

    def test_checks_out_main_whatever_started_it(self):
        self.assertRegex(self.text, r"ref:\s*main")


if __name__ == "__main__":
    unittest.main()
