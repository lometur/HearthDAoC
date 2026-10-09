"""The fork's changes to player commands (source checks).

/rp off works at any level (owner 2026-10-09), so a player under a battleground's realm point cap can stop gaining
realm points and stay in. OpenDAoC allowed it only from level 40.
"""
import os
import re
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RP = os.path.join(ROOT, "source", "server", "GameServer", "commands", "playercommands", "rp.cs")


class RpCommandTests(unittest.TestCase):
    def setUp(self):
        with open(RP, encoding="utf-8") as f:
            self.text = f.read()

    def test_rp_off_works_at_any_level(self):
        self.assertIsNone(re.search(r"Player\.Level\s*<", self.text))
        self.assertIn("client.Player.GainRP = false;", self.text)
        self.assertIn("// HearthDAoC:", self.text)

    def test_the_file_keeps_its_crlf_endings(self):
        with open(RP, "rb") as f:
            raw = f.read()
        self.assertEqual(raw.count(b"\r\n"), raw.count(b"\n"))


if __name__ == "__main__":
    unittest.main()
