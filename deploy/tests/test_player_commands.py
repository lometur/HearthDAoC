"""The fork's changes to player and GM commands (source checks).

/rp off works at any level (owner 2026-10-09), so a player under a battleground's realm point cap can stop gaining
realm points and stay in. OpenDAoC allowed it only from level 40.

A /harm kill counts for the GM's quests (owner test 2026-10-10: Frund killed with /harm advanced nothing; a swing
first, then /harm, worked). A death tells only the attackers in the target's AttackerTracker, which real attacks and
spells fill; /harm called TakeDamage alone. A unit test would need a live player, client and region, so these check
the source.

/gc form needs a group of guild_num (8) again, bots counting (owner 2026-10-28, #176): upstream's two commented-out
checks are back, the solo form at the registrar needs the number too, only the group's human players are asked, and the
founding group's bots join the new guild. The rule is unit-tested (UT_GuildForming); its wiring in guild.cs needs live
players, a group and a registrar, so it is checked here.

/indicator (GM only, a test tool) forces a quest indicator on an NPC for one GM through one marked block in
GameNPC.GetQuestIndicator; its decisions are unit-tested (UT_QuestIndicatorProbe), the hook and what it relies on are
checked here.
"""
import os
import re
import unittest

from tests import endings

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

    def test_the_file_keeps_upstreams_endings(self):
        endings.check(self, RP)


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

    def test_the_file_keeps_upstreams_endings(self):
        endings.check(self, HARM)


class IndicatorCommandTests(unittest.TestCase):
    def test_the_gm_value_is_asked_first_in_get_quest_indicator(self):
        text = read(os.path.join(GAME_SERVER, "gameobjects", "GameNPC.cs"))
        body = text[text.index("public virtual eQuestIndicator GetQuestIndicator(GamePlayer player)"):]
        body = body[:body.index("HearthDAoC.QuestIndicators.For(")]
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


DIALOG = os.path.join(GAME_SERVER, "packets", "Client", "168", "DialogResponseHandler.cs")


class DialogAnswerTests(unittest.TestCase):
    """#92: an answer to a custom dialog takes the player's callback under the lock SendCustomDialog sets it under, so a
    new dialog sent in the same tick can't run the same callback twice (once with the answer, once as superseded).
    Clients are handled in parallel; a unit test would need two live clients, so this checks the source."""

    def test_the_answer_reads_and_clears_the_callback_under_the_players_lock(self):
        text = read(DIALOG)
        block = re.search(r"lock \(player\)\s*\{\s*callback = player\.CustomDialogCallback;\s*"
                          r"player\.CustomDialogCallback = null;\s*\}", text)
        self.assertIsNotNone(block, "the read and clear are not under lock (player)")
        self.assertIn("// HearthDAoC:", text[:block.start()])
        self.assertLess(block.end(), text.index("callback(player, response);"))  # the callback runs outside the lock

    def test_send_custom_dialog_still_takes_that_lock(self):
        # What the fix pairs with; if upstream changes it, review the fix.
        lib = read(os.path.join(GAME_SERVER, "packets", "Server", "PacketLib168.cs"))
        send = lib[lib.index("public virtual void SendCustomDialog(string msg, CustomDialogResponse callback)"):]
        self.assertIn("lock (m_gameClient.Player)", send[:400])

    def test_the_file_keeps_upstreams_endings(self):
        endings.check(self, DIALOG)


GUILD = os.path.join(GAME_SERVER, "commands", "playercommands", "guild.cs")
# The founders count (the members that join: players, player bots, gamebots), not the group's companions.
NUM_CHECK = re.compile(r"if \(HearthDAoC\.GuildForming\.Founders\((?:leader|client\.Player), group\) < Properties\.GUILD_NUM\)")


def line_above(text, at):
    return text[:at].rsplit("\n", 2)[-2]


class GuildFormTests(unittest.TestCase):
    def setUp(self):
        self.text = read(GUILD)

    def body(self, start, end):
        body = self.text[self.text.index(start):]
        return body[:body.index(end)]

    def test_both_guild_num_checks_are_back_and_marked(self):
        self.assertEqual(len(NUM_CHECK.findall(self.text)), 2)
        self.assertNotIn("group.MemberCount < Properties.GUILD_NUM", self.text)
        for name in ("private static bool GuildFormCheck(", 'case "form":'):
            body = self.body(name, "#endregion\n\t\t\t\t\t\t#region Player already" if name == 'case "form":'
                             else "return true;")
            if name == 'case "form":':
                body = body[body.index("#region Enough members to form Check"):]
            at = NUM_CHECK.search(body).start()
            self.assertIn("// HearthDAoC:", line_above(body, at), name)
            self.assertNotIn("*/", body[at:])

    def test_the_solo_form_needs_the_number_too(self):
        form = self.body('case "form":', 'case "quit":')
        rule = form.index("HearthDAoC.GuildForming.Rule(HearthDAoC.GuildForming.Founders(client.Player, group),")
        self.assertIn("HearthDAoC.GuildForming.Humans(client.Player, group).Count, Properties.GUILD_NUM);", form[rule:])
        self.assertIn("// HearthDAoC:", form[:rule].rsplit("\n", 3)[-3])
        refused = form.index("if (formStep == HearthDAoC.GuildFormStep.Refused)")
        now = form.index("if (formStep == HearthDAoC.GuildFormStep.FormNow)")
        self.assertLess(refused, now)
        self.assertIn('"Scripts.Player.Guild.FormNoMembers", Properties.GUILD_NUM', form[refused:now])
        self.assertIn("FormGuildAlone(client.Player,", form[now:now + 200])
        self.assertNotIn("GetPlayersInTheGroup().All(", form)  # upstream's "only bots" test, which missed player bots
        self.assertLess(now, form.index("Only the group leader can create a guild"))

    def test_only_the_human_players_are_asked_and_must_accept(self):
        form = self.body('case "form":', 'case "quit":')
        ask = form.index("foreach (GamePlayer p in HearthDAoC.GuildForming.Humans(client.Player, group).Where(p => p != @group.Leader))")
        self.assertIn("// HearthDAoC:", line_above(form, ask))
        self.assertIn("SendCustomDialog(", form[ask:ask + 400])
        create = self.body("protected void CreateGuild(GamePlayer player, byte response)", "Group group = player.Group;")
        check = create.index("memnum != HearthDAoC.GuildForming.Humans(player, player.Group).Count")
        self.assertIn("// HearthDAoC:", line_above(create, check))

    def test_the_founding_bots_join_and_none_may_be_in_a_guild(self):
        create = self.body("protected void CreateGuild(GamePlayer player, byte response)", "private void FormGuildAlone(")
        join = create.index("HearthDAoC.GuildForming.JoinBots(newGuild, group.Leader, group, addPlayerBots: false);")
        self.assertIn("// HearthDAoC:", line_above(create, join))
        self.assertLess(create.index("newGuild.AddPlayer(ply, newGuild.GetRankByID(0));"), join)  # the leader is in first
        alone = self.body("private void FormGuildAlone(", "public void OnCommand(")
        check = alone.index("HearthDAoC.GuildForming.BotInAGuild(player.Group);")
        self.assertIn("// HearthDAoC:", line_above(alone, check))
        self.assertLess(check, alone.index("GuildMgr.CreateGuild("))
        join = alone.index("HearthDAoC.GuildForming.JoinBots(newGuild, player, player.Group, addPlayerBots: true);")
        self.assertIn("// HearthDAoC:", line_above(alone, join))
        self.assertLess(alone.index("newGuild.AddPlayer(player, newGuild.GetRankByID(0));"), join)
        form = self.body('case "form":', 'case "quit":')
        check = form.index("HearthDAoC.GuildForming.BotInAGuild(group);")
        self.assertIn("// HearthDAoC:", line_above(form, check))
        self.assertLess(check, form.index("GuildFormCheck(client.Player)"))

    def test_the_bots_join_by_the_invite_path(self):
        # JoinBots takes a gamebot in as /gc invite does; if upstream changes either, review JoinBots.
        fork = read(os.path.join(GAME_SERVER, "scripts", "hearthdaoc", "GuildForming.cs"))
        self.assertIn("AutonomousBotGuilds.JoinRefusal(leader, bot)", fork)
        self.assertIn("AutonomousBotGuilds.Join(leader, bot)", fork)
        invite = self.body('case "invite":', 'case "remove":')
        self.assertIn("AutonomousBotGuilds.JoinRefusal(client.Player, invitedBot)", invite)
        self.assertIn("AutonomousBotGuilds.Join(client.Player, invitedBot)", invite)

    def test_the_file_keeps_upstreams_endings(self):
        endings.check(self, GUILD)


if __name__ == "__main__":
    unittest.main()
