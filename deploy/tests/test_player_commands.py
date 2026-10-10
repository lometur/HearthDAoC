"""The fork's changes to player and GM commands (source checks).

/rp off works at any level (owner 2026-10-09), so a player under a battleground's realm point cap can stop gaining
realm points and stay in. OpenDAoC allowed it only from level 40.

A /harm kill counts for the GM's quests (owner test 2026-10-10: Frund killed with /harm advanced nothing; a swing
first, then /harm, worked). A death tells only the attackers in the target's AttackerTracker, which real attacks and
spells fill; /harm called TakeDamage alone. A unit test would need a live player, client and region, so these check
the source.
"""
import os
import re
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
RP = os.path.join(GAME_SERVER, "commands", "playercommands", "rp.cs")
HARM = os.path.join(GAME_SERVER, "commands", "gmcommands", "harm.cs")


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


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


class HarmCommandTests(unittest.TestCase):
    def test_the_gm_joins_the_targets_attackers_before_the_damage(self):
        text = read(HARM)
        add = text.find("living.attackComponent.AddAttacker(new AttackData { Attacker = client.Player, Target = living")
        damage = text.find("living.TakeDamage(client.Player, eDamageType.GM, amount, 0);")
        self.assertGreater(add, 0)
        self.assertGreater(damage, add)
        self.assertIn("// HearthDAoC:", text[:add])

    def test_a_death_still_tells_only_the_attackers_tracked_and_real_attacks_still_add_themselves(self):
        # What the /harm fix relies on; if upstream changes either, review the fix.
        living = read(os.path.join(GAME_SERVER, "gameobjects", "GameLiving.cs"))
        death = living[living.index("public virtual void ProcessDeath(GameObject killer)"):]
        self.assertIn("foreach (GameObject attacker in attackComponent.AttackerTracker.Attackers)", death[:3000])
        self.assertIn("ad.Target.attackComponent.AddAttacker(ad);",
                      read(os.path.join(GAME_SERVER, "ECS-Components", "AttackComponent.cs")))

    def test_the_file_keeps_its_crlf_endings(self):
        with open(HARM, "rb") as f:
            raw = f.read()
        self.assertEqual(raw.count(b"\r\n"), raw.count(b"\n"))


if __name__ == "__main__":
    unittest.main()
