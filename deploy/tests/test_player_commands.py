"""The fork's changes to player and GM commands (source checks).

/rp off works at any level (owner 2026-10-09), so a player under a battleground's realm point cap can stop gaining
realm points and stay in. OpenDAoC allowed it only from level 40.

A /harm kill counts for the GM's quests (owner test 2026-10-10: Frund killed with /harm advanced nothing; a swing
first, then /harm, worked). A death tells only the attackers in the target's AttackerTracker, which real attacks and
spells fill; /harm called TakeDamage alone. A unit test would need a live player, client and region, so these check
the source.

/indicator (GM only, a test tool) forces a quest indicator on an NPC for one GM through one marked block in
GameNPC.GetQuestIndicator; its decisions are unit-tested (UT_QuestIndicatorProbe), the hook and what it relies on are
checked here.
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


class IndicatorCommandTests(unittest.TestCase):
    def test_the_gm_value_is_asked_first_in_get_quest_indicator(self):
        text = read(os.path.join(GAME_SERVER, "gameobjects", "GameNPC.cs"))
        body = text[text.index("public virtual eQuestIndicator GetQuestIndicator(GamePlayer player)"):]
        body = body[:body.index("CanShowOneQuest(player)")]
        self.assertIn("// HearthDAoC:", body)
        self.assertIn("if (HearthDAoC.IndicatorOverrides.TryGet(player, this, out eQuestIndicator forced))", body)

    def test_only_game_npc_calls_the_store_from_upstream_files(self):
        fork = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
        calling = []
        for folder, _, names in os.walk(GAME_SERVER):
            if folder.startswith(fork):
                continue
            for name in names:
                if name.endswith(".cs") and "IndicatorOverrides." in read_any(os.path.join(folder, name)):
                    calling.append(name)
        self.assertEqual(calling, ["GameNPC.cs"])

    def test_the_create_packet_and_the_re_create_are_upstreams(self):
        # What /indicator relies on; if upstream changes any of it, review the command.
        create = read(os.path.join(GAME_SERVER, "packets", "Server", "PacketLib1124.cs"))
        self.assertIn("eQuestIndicator questIndicator = npc.GetQuestIndicator(m_gameClient.Player);", create)
        # The re-create is the create an NPC gets when it comes into view, or when the client asks for one it lacks.
        service = read(os.path.join(GAME_SERVER, "ECS-Services", "ClientService.cs"))
        for_player = between(service, "public static void CreateObjectForPlayer(GamePlayer player, GameObject gameObject)",
                             "public static void CreateObjectForPlayers")
        self.assertIn("CreateNpcForPlayerInternal(player, gameObject as GameNPC);", for_player)
        self.assertIn("CreateNpcForPlayerInternal(player, npcInRange);", between(service, "private static void UpdateNpcs(", "\n        }\n"))
        request = read(os.path.join(GAME_SERVER, "packets", "Client", "168", "CreateObjectRequestHandler.cs"))
        self.assertIn("ClientService.CreateObjectForPlayer(client.Player, obj);", request)
        # The client drops the NPC first, and gets it back only after the delay.
        command = read(os.path.join(GAME_SERVER, "scripts", "hearthdaoc", "IndicatorCommand.cs"))
        recreate = between(command, "private static void Recreate(", "\n    }\n")
        remove = recreate.index("gm.Out.SendObjectRemove(npc);")
        timer = recreate.index("new ECSGameTimer(gm, _ =>")
        create = recreate.index("ClientService.CreateObjectForPlayer(gm, npc);")
        self.assertLess(remove, timer)
        self.assertLess(timer, create)
        self.assertIn("}, RecreateDelay);", recreate[create:])

def between(text, start, end):
    """The text from start up to the first end after it."""
    body = text[text.index(start):]
    return body[:body.index(end)]


def read_any(path):
    """A C# file's text; a few upstream files are not UTF-8."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


if __name__ == "__main__":
    unittest.main()
