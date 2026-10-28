"""Gamebots come back worth realm points, stage 1 (spec docs/fork/specs/2026-10-13-rp-timer-waits-design.md).

The switch goes .env -> compose.yml -> entrypoint.sh -> server_properties.py -> the bot_rp_timer_wait server
property, default on at every step (hdc update appends the new .env.example line to an existing .env). The
decisions are unit-tested (UT_RpTimerWait, UT_RpTimerWaitScript); the six hooks in upstream files, the handlers'
registration and the upstream facts the waits mirror need a live server, so these check the source.
"""
import os
import re
import shlex
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

import server_properties as sp  # noqa: E402
from tests import endings  # noqa: E402
from tests.test_server_properties import SCHEMA, row  # noqa: E402

GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
FORK = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
SCRIPT_CS = os.path.join(FORK, "RpTimerWaitScript.cs")
DECISIONS_CS = os.path.join(FORK, "RpTimerWait.cs")
CONTROLLER_CS = os.path.join(FORK, "RpTimerWaitController.cs")
BOTS = os.path.join(GAME_SERVER, "bots", "autonomous")
WBC = os.path.join(BOTS, "AutonomousWorldBotController.cs")
BG = os.path.join(BOTS, "AutonomousWorldBotController.Battleground.cs")
FT = os.path.join(BOTS, "AutonomousWorldBotController.FrontierThreat.cs")
GC = os.path.join(BOTS, "AutonomousBotGroupCoordinator.cs")
KEEP_TRAVEL = os.path.join(BOTS, "AutonomousWorldBotController.KeepTravel.cs")
SIEGE = os.path.join(BOTS, "AutonomousWorldBotController.Siege.cs")
ARPR = os.path.join(BOTS, "AutonomousBotRealmPointRewards.cs")
EVENT_LAYER = os.path.join(BOTS, "AutonomousRvrEventLayer.cs")
GAME_BOT = os.path.join(GAME_SERVER, "bots", "GameBot.cs")


def read(name):
    with open(os.path.join(DEPLOY, name), encoding="utf-8") as f:
        return f.read()


def source(path):
    """A C# source file's text (upstream files may start with a BOM)."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


def between(text, start, end):
    """The text from the first start up to the first end after it."""
    body = text[text.index(start):]
    return body[:body.index(end)]


def marked(test, body, call):
    """The body calls the fork once, with a // HearthDAoC: comment on the line above the call."""
    test.assertEqual(body.count(call), 1, call)
    above = body[:body.index(call)].rsplit("\n", 2)[-2]
    test.assertIn("// HearthDAoC:", above, call)


def cs_files(top):
    """Every .cs file under top, sorted."""
    found = []
    for folder, _, names in os.walk(top):
        found.extend(os.path.join(folder, name) for name in names if name.endswith(".cs"))
    return sorted(found)


class SwitchValueTests(unittest.TestCase):
    def test_on_and_off_in_any_case_become_true_and_false(self):
        for spec, expected in (("on", "True"), ("ON", "True"), ("On", "True"),
                               ("off", "False"), ("OFF", "False"), ("oFf", "False")):
            with self.subTest(spec=spec):
                self.assertEqual(sp.bot_rp_timer_wait_value(spec), expected)

    def test_other_values_are_refused(self):
        for bad in ("", "yes", "no", "true", "false", "1", "0", "none", "onn", " on", "off "):
            with self.subTest(bad=bad), self.assertRaisesRegex(
                    sp.PropertyError, f"^HEARTHDAOC_BOT_RP_TIMER_WAIT must be on or off, got {re.escape(repr(bad))}$"):
                sp.bot_rp_timer_wait_value(bad)

    def test_the_property_matches_the_server_declaration(self):
        self.assertEqual((sp.RP_WAIT_KEY, sp.RP_WAIT_DEFAULT), ("bot_rp_timer_wait", "False"))
        self.assertNotIn('"', sp.RP_WAIT_DESCRIPTION)
        self.assertNotIn("\\", sp.RP_WAIT_DESCRIPTION)


class EntrypointTests(unittest.TestCase):
    """Runs the entrypoint's own server_properties.py command against a scratch world."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        os.makedirs(os.path.join(self.tmp.name, "world"))
        self.db = os.path.join(self.tmp.name, "world", "opendaoc.sqlite3.db")
        with sqlite3.connect(self.db) as c:
            c.execute(SCHEMA)
        lines = read("entrypoint.sh").replace("\\\n", " ").splitlines()
        commands = [line for line in lines if line.startswith('python3 "$BIN/server_properties.py"')]
        self.assertEqual(len(commands), 1, "the entrypoint runs server_properties.py once")
        self.command = commands[0]

    def tearDown(self):
        self.tmp.cleanup()

    def run_entrypoint_line(self, **env):
        script = f"set -euo pipefail\nBIN={shlex.quote(BIN)}\nDATA={shlex.quote(self.tmp.name)}\n{self.command}\n"
        base = {k: v for k, v in os.environ.items() if not k.startswith("HEARTHDAOC_")}
        return subprocess.run(["bash", "-c", script], env={**base, **env}, capture_output=True, text=True)

    def test_passes_the_setting_with_default_on(self):
        self.assertIn('--bot-rp-timer-wait "${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}"', self.command)
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\nBot realm point timer wait: on\n"
                             "Bot CPU meter: off\nStarter guilds: off\n", ""))
        self.assertEqual(row(self.db, sp.RP_WAIT_KEY), ("server", sp.RP_WAIT_DESCRIPTION, "False", "True"))

    def test_off_in_any_case_turns_it_off(self):
        self.run_entrypoint_line()
        r = self.run_entrypoint_line(HEARTHDAOC_BOT_RP_TIMER_WAIT="OFF")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Bot realm point timer wait: off\n", ""))
        self.assertEqual(row(self.db, sp.RP_WAIT_KEY)[3], "False")

    def test_a_bad_value_stops_the_start(self):
        for bad in ("maybe", ""):
            with self.subTest(bad=bad):
                r = self.run_entrypoint_line(HEARTHDAOC_BOT_RP_TIMER_WAIT=bad)
                self.assertEqual(r.returncode, 2)
                self.assertEqual(r.stderr, f"ERROR: HEARTHDAOC_BOT_RP_TIMER_WAIT must be on or off, got {bad!r}\n")
                self.assertIsNone(row(self.db, sp.RP_WAIT_KEY))
                self.assertIsNone(row(self.db, sp.OVERRIDES_KEY))  # checked before any write: nothing at all
                self.assertIsNone(row(self.db, sp.SI_KEY))


class SettingDocsTests(unittest.TestCase):
    def test_compose_passes_the_setting_with_default_on(self):
        environment = read("compose.yml").split("\n    environment:\n", 1)[1].split("\n    volumes:\n", 1)[0]
        self.assertIn("\n      HEARTHDAOC_BOT_RP_TIMER_WAIT: ${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}\n", environment + "\n")

    def test_env_example_documents_the_setting_default_on(self):
        lines = read(".env.example").splitlines()
        self.assertEqual([line for line in lines if line.startswith("HEARTHDAOC_BOT_RP_TIMER_WAIT")],
                         ["HEARTHDAOC_BOT_RP_TIMER_WAIT=on"])
        i = lines.index("HEARTHDAOC_BOT_RP_TIMER_WAIT=on")
        comment = []
        while i > 0 and lines[i - 1].startswith("#"):
            i -= 1
            comment.insert(0, lines[i])
        self.assertIn("realm points", " ".join(comment))
        self.assertIn("off =", " ".join(comment))

    def test_handoff_lists_the_setting(self):
        handoff = read("HANDOFF.md")
        self.assertIn("`HEARTHDAOC_BOT_RP_TIMER_WAIT` (default `on`", handoff)
        self.assertIn("`HEARTHDAOC_BOT_RP_TIMER_WAIT=on`", handoff)  # what ./hdc update adds to an existing .env


class ServerDeclarationTests(unittest.TestCase):
    def test_server_declares_the_property_with_the_same_description(self):
        with open(SCRIPT_CS, encoding="utf-8") as f:
            found = re.findall(r'\[ServerProperty\(\s*"([^"]*)",\s*"bot_rp_timer_wait",\s*"([^"]*)",\s*(\w+)\s*\)\]', f.read())
        self.assertEqual(found, [("server", sp.RP_WAIT_DESCRIPTION, "false")])


class ScriptWiringTests(unittest.TestCase):
    def test_the_timer_is_read_through_the_upstream_key(self):
        # The fork reads ARPR's internal const (same assembly); the unit tests use the literal key.
        self.assertIn("AutonomousBotRealmPointRewards.LastRealmPointDeathTickProperty", source(SCRIPT_CS))

    def test_the_script_registers_the_death_and_group_handlers(self):
        text = source(SCRIPT_CS)
        loaded = between(text, "public static void OnScriptLoaded(", "\n    }\n")
        self.assertIn("GameEventMgr.AddHandler(GameLivingEvent.Dying, OnDying);", loaded)
        self.assertIn("GameEventMgr.AddHandler(GroupEvent.MemberJoined, OnMemberJoined);", loaded)
        unloaded = between(text, "public static void OnScriptUnloaded(", "\n    }\n")
        self.assertIn("GameEventMgr.RemoveHandler(GameLivingEvent.Dying, OnDying);", unloaded)
        self.assertIn("GameEventMgr.RemoveHandler(GroupEvent.MemberJoined, OnMemberJoined);", unloaded)
        self.assertIn("[ScriptLoadedEvent]", text)

    def test_no_wait_code_calls_is_safe_area(self):
        for path in (DECISIONS_CS, SCRIPT_CS, CONTROLLER_CS):
            if os.path.exists(path):
                self.assertNotIn("IsSafeArea(", source(path), path)


class UpstreamHookTests(unittest.TestCase):
    def test_the_release_note_comes_after_observe_deaths_and_before_the_hand_over(self):
        tick = between(source(WBC), "public bool Tick(BotBrain brain)", "if (!bot.IsAlive || bot.IsReturningAfterRelease)")
        call = "HearthDAoC.RpTimerWaitScript.NoteRelease(bot);"
        marked(self, tick, call)
        self.assertLess(tick.index("bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup)\n                return false;"),
                        tick.index(call))
        self.assertLess(tick.index("ObserveDeaths(bot);"), tick.index(call))
        self.assertLess(tick.index(call), tick.index("if (!bot.IsPlayerLedGroup)"))
        self.assertIn("if (!bot.IsPlayerLedGroup)\n                    bot.HandOverReleaseReturnToGroup();", tick)

    def test_the_warband_gate_is_in_the_regroup_completion_before_try_complete(self):
        text = source(GC)
        condition = between(text, "if (session.LeaderReadyForAssembly &&\n", "\n            {")
        call = ("!HearthDAoC.RpTimerWaitScript.WarbandWaits(session.Id, session.ObjectiveKind, session.RendezvousRegion, "
                "session.Rendezvous, members) &&")
        marked(self, condition, call)
        self.assertLess(condition.index(call), condition.index("session.Recovery.TryComplete("))

    def test_recruiting_skips_waiting_bots(self):
        available = between(source(GC), "GameBot[] available = activeRoster", ".ToArray();")
        marked(self, available, "!HearthDAoC.RpTimerWaitScript.IsWaiting(bot) &&")
        self.assertLess(available.index("IsWaiting(bot)"), available.index(".OrderBy("))

    def test_the_threat_scan_filter_is_inside_the_nearby_chain(self):
        nearby = between(source(FT), "GameLiving[] nearby = ", ".ToArray();")
        call = ".Where(target => !HearthDAoC.RpTimerWaitScript.IsWaiting(bot) || FiringOnRealm(bot, target))"
        marked(self, nearby, call)
        self.assertLess(nearby.index("<= TargetSearchRadius)"), nearby.index(call))
        self.assertLess(nearby.index(call), nearby.index(".OrderBy("))


class UpstreamFactTests(unittest.TestCase):
    """What the waits mirror from upstream code: a sync that changes one fails here."""

    def test_the_realm_point_timer_is_as_the_waits_mirror_it(self):
        text = source(ARPR)
        self.assertIn('internal const string LastRealmPointDeathTickProperty = "autonomous.rvr.last.realm.point.death.tick";', text)
        self.assertIn("long worthInterval = Math.Max(0, Properties.RP_WORTH_SECONDS) * 1000L;", text)
        self.assertIn("bool isWorthRealmPoints = previousDeath < 0 || now - previousDeath >= worthInterval;", text)

    def test_the_first_contact_point_is_from_upstreams_roam_and_scan(self):
        roam = between(source(BG), "private bool RoamBattleground(", "IssueVariedRvrPath(")
        self.assertIn("central != null ? new(central.X, central.Y, central.Z) : new(entry.X, entry.Y, entry.Z), 0.6f))", roam)
        self.assertIn("anchor = nav.GetRandomPoint(zone, anchor, 900, nav.DefaultFilters) ?? anchor;", roam)
        self.assertIn("private const int TargetSearchRadius = 5000;", source(WBC))

    def test_the_force_key_and_the_formation_point_are_upstreams(self):
        self.assertIn('bot.TempProperties.SetProperty("RvrEventForce", forceId);', source(WBC))
        self.assertIn("public static Vector3 RendezvousFormationPoint(GameBot bot, string groupId, Vector3 center, bool tight)",
                      source(GC))

    def test_a_released_siege_attacker_comes_back_at_its_frontier_muster(self):
        # Since upstream 0.37 an attacker of a running siege with no bind of its realm nearby releases at its realm's
        # muster for the keep's frontier, not in its capital, so its first look is in the region of its fight (spec 1,
        # departures; the in-game guide's check 7).
        release = between(source(GAME_BOT), "Point3D release = FindNearestBindPoint();",
                          "release = new Point3D(capital.X, capital.Y, capital.Z);")
        self.assertIn("AutonomousRvrEventLayer.TrySiegeRelease(AutonomousBotGroupCoordinator.RvrForceId(this), Realm,", release)
        self.assertLess(release.index("TrySiegeRelease("), release.index("SafeCapitalFor(Realm)"))
        siege = between(source(EVENT_LAYER), "public static bool TrySiegeRelease(", "\n    }")
        self.assertIn("realm == entry.AttackerRealm && entry.Attackers.ContainsKey(forceId)", siege)
        self.assertIn("AutonomousRvrStaging.TryGetSiegeMuster(realm, target.RegionId, out", siege)


class ControllerHookTests(unittest.TestCase):
    def test_the_battleground_wait_comes_after_the_region_check_and_before_every_role(self):
        tour = between(source(BG), "private bool ExecuteBattleground(", "private bool RoamBattleground(")
        call = "if (RpTimerWaitBattleground(brain, bot, bracket, central)) return true;"
        marked(self, tour, call)
        self.assertLess(tour.index("BotOverCap("), tour.index(call))
        self.assertLess(tour.index("bot.CurrentRegionID != bracket.RegionId"), tour.index(call))
        self.assertLess(tour.index("_rvrIntent = assault && !tooFew ?"), tour.index(call))
        self.assertLess(tour.index(call), tour.index("TryRunSiegeJob(bot)"))
        self.assertLess(tour.index(call), tour.index("return RoamBattleground("))

    def test_the_rvr_wait_comes_after_the_musters_and_the_stale_target_clear(self):
        rvr = between(source(WBC), "private bool ExecuteRvr(BotBrain brain, GameBot bot)", "GameLiving enemy = FindRvrTarget(bot);")
        call = "if (RpTimerWaitRvr(brain, bot, battleActive, dynamicWarband, committedPlan != null)) return true;"
        marked(self, rvr, call)
        self.assertLess(rvr.index("return HandleSiegeRally(bot, forceId, rally);"), rvr.index(call))
        self.assertLess(rvr.index("_nextRvrPlanReview = 0;"), rvr.index(call))
        self.assertLess(rvr.index(call), rvr.index("if (TryRunSiegeJob(bot)) return true;"))

    def test_the_fork_is_called_from_upstream_files_only_by_the_six_hooks(self):
        calls = {}
        for path in cs_files(GAME_SERVER):
            if path.startswith(FORK + os.sep):
                continue
            count = source(path).count("RpTimerWait")
            if count:
                calls[os.path.basename(path)] = count
        self.assertEqual(calls, {
            "AutonomousWorldBotController.Battleground.cs": 1,
            "AutonomousWorldBotController.cs": 2,
            "AutonomousBotGroupCoordinator.cs": 2,
            "AutonomousWorldBotController.FrontierThreat.cs": 1,
        })

    def test_the_controller_part_is_a_partial_of_the_controller(self):
        text = source(CONTROLLER_CS)
        self.assertIn("namespace DOL.GS;\n", text)
        self.assertIn("public sealed partial class AutonomousWorldBotController\n", text)
        self.assertIn("private bool RpTimerWaitBattleground(BotBrain brain, GameBot bot, BattlegroundBrackets.Bracket bracket,", text)
        self.assertIn("private bool RpTimerWaitRvr(BotBrain brain, GameBot bot, bool battleActive, bool dynamicWarband, bool keepPlan)", text)

    def test_the_private_members_the_controller_part_uses_are_upstreams(self):
        wbc, bg, keep, siege = source(WBC), source(BG), source(KEEP_TRAVEL), source(SIEGE)
        self.assertIn("private bool IssuePath(GameBot bot, Vector3 destination, Vector3? validatedContinuation = null,", wbc)
        self.assertIn("private void SetStatus(\n            GameBot bot,\n            string activity,", wbc)
        self.assertIn("private void ResetRouteOrderState()", wbc)
        self.assertIn("private CampDestination _rvrDestination;", wbc)
        self.assertIn("private long _nextRvrPlanReview;", wbc)
        self.assertIn("private void SetBattlegroundStatus(GameBot bot, BattlegroundBrackets.Bracket bracket, string activity, string detail, string target = \"\")", bg)
        self.assertIn("private long _keepStallSince;", keep)
        self.assertIn("private void ReleaseSiegeJob(GameBot bot)", siege)
        # The part never reads _siegeJobKeep; it relies on ReleaseSiegeJob clearing it (spec 3.3, Siege.cs:469).
        self.assertIn("_siegeJobKeep=null;", between(siege, "private void ReleaseSiegeJob(GameBot bot)", "\n        }"))


class EndingsTests(unittest.TestCase):
    def test_the_upstream_files_keep_upstreams_endings(self):
        # Upstream's endings, whichever they are (it turned GC to CRLF in 0.36), line by line: the fork's lines take
        # those of the upstream lines beside them (tests/endings.py).
        for path in (BG, WBC, GC, FT):
            with self.subTest(path=path):
                endings.check(self, path)
                with open(path, "rb") as f:
                    self.assertFalse(f.read().startswith(b"\xef\xbb\xbf"), path)

    def test_the_fork_files_are_lf_without_bom(self):
        tests = os.path.join(ROOT, "source", "server", "Tests", "UnitTests")
        for path in (DECISIONS_CS, SCRIPT_CS, CONTROLLER_CS, os.path.join(tests, "UT_RpTimerWait.cs"),
                     os.path.join(tests, "UT_RpTimerWaitScript.cs"), os.path.abspath(__file__)):
            with open(path, "rb") as f:
                raw = f.read()
            self.assertNotIn(b"\r", raw, path)
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), path)
            self.assertNotIn(b"\t", raw, path)


class GuideTests(unittest.TestCase):
    GUIDE = os.path.join(ROOT, "docs", "fork", "verification", "rp-timer-waits-ingame.md")
    EVERY_LOG = "docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)'"

    def test_the_log_reads_take_every_server_log_file_oldest_first(self):
        # logconfig.xml starts a new server log at midnight and at 100 MB (archiveEvery="Day", kept 7 days), so a read
        # of server.log alone would lose the day's earlier lines and show a wait across midnight as a lone leave.
        guide = source(self.GUIDE)
        self.assertEqual(guide.count("/data/logs/server.log"), 0, "a read of today's server.log only")
        reads = guide.count("docker exec hearthdaoc-server")
        self.assertEqual(guide.count(self.EVERY_LOG), reads - 1)  # all but `ls /data/logs`
        self.assertIn("docker exec hearthdaoc-server ls /data/logs", guide)

    def test_check_7_has_siege_attackers_wait_at_their_frontier_muster(self):
        # A released siege attacker comes back at its realm's portal outpost in the keep's frontier (UpstreamFactTests):
        # it waits there or goes at once; only other invaders release in their capital (ruling R8).
        seven = between(source(self.GUIDE), "### 7. Frontier, over a day of play", "### 8. ")
        self.assertIn("portal outpost", seven)
        self.assertIn("`start case=siege`", seven)
        self.assertIn("`skip … reason=trip`", seven)
        self.assertNotIn("for invaders, who release in their capital", seven)


if __name__ == "__main__":
    unittest.main()
