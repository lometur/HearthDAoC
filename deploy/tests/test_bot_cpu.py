"""The bot CPU meter (spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.2, 6.3).

While the meter is on, each game-loop callback is timed and charged to the kind of its owner and to its stage. The hot
path is unit-tested (UT_BotCpuMeter), through the real hook H1 in GameLoop.ExecuteForEach and hook H6's fleet scope;
the hooks' shape in the upstream files, the upstream facts the meter mirrors (the stage of each profile key, the pool's
entry point, the member each item's owner is read from, the one nested call inside the fleet scope) and the upstream
files' line endings need the source, so these check it.

The switch goes .env -> compose.yml -> entrypoint.sh -> server_properties.py -> the bot_cpu_meter server property,
default off at every step (hdc update appends the new .env.example line to an existing .env); the server reads it once,
at start (4.1, 4.2). The image's smoke test checks that a default start loads /botcpu for GMs only and leaves the
meter off (6.3); its two lines are run here on sample server logs.
"""
import os
import re
import shlex
import sqlite3
import subprocess
import sys
import tempfile
import unittest

from tests import endings

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEPLOY = os.path.join(ROOT, "deploy")
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

import server_properties as sp  # noqa: E402
from tests.test_server_properties import SCHEMA, row, run_cli  # noqa: E402

GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
FORK = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
SCRIPT_CS = os.path.join(FORK, "BotCpuScript.cs")
LOOP = os.path.join(GAME_SERVER, "Managers", "GameLoop")
GL = os.path.join(LOOP, "GameLoop.cs")
POOL = os.path.join(LOOP, "GameLoopThreadPool.cs")
MT = os.path.join(LOOP, "GameLoopThreadPoolMultiThreaded.cs")
NS = os.path.join(GAME_SERVER, "ECS-Services", "NpcService.cs")
CPP = os.path.join(GAME_SERVER, "bots", "autonomous", "AutonomousCampPlanningPipeline.cs")

# Hook H1 (spec 3.1): while the meter is on, it gets the pool call; off, one bool read before the unchanged call.
GUARD = ("if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, "
         "action); return; }")
POOL_CALL = "_threadPool.ExecuteForEach(items, toExclusive, action);"

# Hook H6 (spec 3.5): NpcService.Tick's GameBot fleet planning, between the posted actions and the brains, in a scope
# charged to gamebot / Fleet.
BEGIN_FLEET = "long fleetStart = HearthDAoC.BotCpuMeter.BeginFleet();"
END_FLEET = "HearthDAoC.BotCpuMeter.EndFleet(fleetStart); // HearthDAoC: end of the fleet scope."
FLEET_PLANNING = ["AutonomousBotRegistry.PrepareBrainTick();", "AutonomousWorldBotController.PrepareRvrPlanningTick();",
                  "AutonomousBotGroupCoordinator.PrepareCoordinatorTick();",
                  "AutonomousWorldBotController.PrepareCampPlanningTick();"]


def read(name):
    """A file of deploy/."""
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


def type_body(text, header):
    """A type's text, from its declaration up to the next type declared at the same indentation (or the file's end)."""
    start = text.index(header)
    indent = text[text.rfind("\n", 0, start) + 1:start]
    following = re.compile(r"\n" + re.escape(indent) + r"\S[^\n]*\b(class|interface|struct|record|enum)\b")
    found = following.search(text, start + len(header))
    return text[start:found.start() if found else len(text)]


def upstream_lines(pattern):
    """{path under GameServer/: [each line matching the pattern, stripped]} over every file under GameServer/ outside
    the fork's scripts/hearthdaoc/ folder."""
    found = {}
    for folder, _, names in os.walk(GAME_SERVER):
        if folder == FORK or folder.startswith(FORK + os.sep):
            continue
        for name in names:
            path = os.path.join(folder, name)
            with open(path, "rb") as f:
                text = f.read().decode("utf-8", errors="replace")
            hits = [line.strip() for line in text.splitlines() if re.search(pattern, line)]
            if hits:
                found[os.path.relpath(path, GAME_SERVER).replace(os.sep, "/")] = hits
    return found


class HookTests(unittest.TestCase):
    def test_h1_hands_the_pool_call_to_the_meter_while_it_is_on(self):
        body = between(source(GL),
                       "public static void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)",
                       "\n        }")
        marked(self, body, GUARD)
        statements = [line.strip() for line in body.split("\n")[2:]]
        # The comment, the guard, then upstream's call, unchanged: nothing else in the method.
        self.assertEqual(statements[1:], [GUARD, POOL_CALL])
        self.assertTrue(statements[0].startswith("// HearthDAoC: the per-kind CPU meter (#137)"), statements[0])

    def test_h6_scopes_the_gamebot_fleet_planning(self):
        tick = between(source(NS), "public override void Tick()", "GameLoop.ExecuteForEach(")
        marked(self, tick, BEGIN_FLEET)
        statements = [line.strip() for line in tick.split("\n")]
        first = statements.index("ProcessPostedActionsParallel();")
        # The posted actions, the comment, BeginFleet, the four planning calls in their order, EndFleet with its
        # trailing mark: nothing else in the scope.
        self.assertTrue(statements[first + 1].startswith("// HearthDAoC: the per-kind CPU meter (#137)"),
                        statements[first + 1])
        self.assertEqual(statements[first + 2:first + 8], [BEGIN_FLEET, *FLEET_PLANNING, END_FLEET])
        for call in FLEET_PLANNING + [END_FLEET]:
            self.assertEqual(tick.count(call), 1, call)
        # Then the brains.
        self.assertLess(tick.index(END_FLEET), tick.index("ServiceObjectStore.UpdateAndGetAll<ABrain>"))

    def test_the_meter_is_in_no_other_upstream_file(self):
        # The upstream lines are H1's guard in GL and H6's two calls in NS (spec 5.2), nothing else.
        self.assertEqual(upstream_lines("BotCpu"), {
            "Managers/GameLoop/GameLoop.cs": [GUARD],
            "ECS-Services/NpcService.cs": [BEGIN_FLEET, END_FLEET],
        })


class UpstreamFactTests(unittest.TestCase):
    """What the meter mirrors from upstream (spec 6.2): a change upstream fails here."""

    # GameLoop.BuildTickSequence's steps in their order: the stages of spec 3.4.
    STEPS = [("GameLoopService", "Tick"), ("TimerService", "Tick"), ("ClientService", "BeginTick"),
             ("NpcService", "Tick"), ("AttackService", "Tick"), ("CastingService", "Tick"), ("EffectService", "Tick"),
             ("EffectListService", "BeginTick"), ("EffectListService", "EndTick"), ("MovementService", "Tick"),
             ("CraftingService", "Tick"), ("ReaperService", "Tick"), ("ZoneService", "Tick"),
             ("ClientService", "EndTick"), ("DailyQuestService", "Tick"), ("WeeklyQuestService", "Tick"),
             ("MonthlyQuestService", "Tick")]

    def test_the_game_loop_names_each_stage_before_it_runs(self):
        gl = source(GL)
        self.assertIn("public static string ActiveService { get; set; }", gl)
        callback = between(gl, "static void TickCallback(object state)", "\n            }\n")
        self.assertLess(callback.index("ActiveService = tickState.ProfileKey;"),
                        callback.index("tickState.TickAction();"))
        # The profile key: the service's name, plus .BeginTick or .EndTick for those steps (GL:169).
        self.assertIn('string profileKey = methodName == nameof(IGameService.Tick) ? service.ServiceName : '
                      '$"{service.ServiceName}.{methodName}";', gl)

    def test_the_stages_are_the_17_steps_in_their_order_and_the_meter_maps_each_key(self):
        steps = re.findall(r"^ *AddStep\((\w+)\.Instance, \1\.Instance\.(\w+)\);\r?$", source(GL), re.M)
        self.assertEqual(steps, self.STEPS)
        stage_of = between(source(METER_CS), "public static BotCpuStage StageOf(string profileKey)", "};")
        for service, method in steps:
            key = service if method == "Tick" else f"{service}.{method}"
            self.assertIn(f'"{key}"', stage_of, key)

    def test_the_game_loop_hands_each_call_to_its_pool(self):
        gl = source(GL)
        self.assertIn('public const string THREAD_NAME = "GameLoop";', gl)
        self.assertIn("private static GameLoopThreadPool _threadPool;", gl)
        self.assertIn("public abstract void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action);",
                      source(POOL))
        # The loop threads' names, which the process line reads (spec 3.9).
        self.assertIn('Name = $"{GameLoop.THREAD_NAME}_Worker_{i}",', source(MT))
        # The process line finds the camp planner's thread by this name; renamed, campThreadCpuPct would read 0.0.
        self.assertIn('_campBuilder ??= new("CampSnapshotPlanner", BuildCampCatalogDraft);', source(CPP))
        # Its builder names its thread with that name as it is (Managers/GameLoop/BackgroundSnapshotBuilder.cs:28).
        self.assertIn("_worker = new Thread(Run) { Name = name, IsBackground = true };",
                      source(os.path.join(LOOP, "BackgroundSnapshotBuilder.cs")))

    # The member each item's owner is read from (spec 3.3), and what the kind of an owner tests: (file, type, member).
    OWNERS = [
        ("ai/ABrain.cs", "public abstract class ABrain", "public virtual GameNPC Body { get; set; }"),
        ("ECS-Components/AttackComponent.cs", "public class AttackComponent", "public GameLiving owner;"),
        ("ECS-Components/CastingComponent.cs", "public class CastingComponent", "public GameLiving Owner { get; }"),
        ("ECS-Effects/ECSGameEffect.cs", "public abstract class ECSGameEffect", "public GameLiving Owner;"),
        ("ECS-Components/EffectListComponent.cs", "public class EffectListComponent",
         "public GameLiving Owner { get; }"),
        ("ECS-Components/MovementComponent.cs", "public class MovementComponent", "public GameLiving Owner { get; }"),
        ("ECS-Components/CraftComponent.cs", "public class CraftComponent", "public GamePlayer Owner { get; }"),
        ("ECS-Services/TimerService.cs", "public class ECSGameTimer", "public GameObject Owner { get; }"),
        ("world/SubZone.cs", "public class SubZoneObject", "public LinkedListNode<GameObject> Node { get; }"),
        ("ECS-Services/ReaperService.cs", "public class LivingBeingKilled",
         "public GameLiving Killed { get; private set; }"),
        ("ECS-Services/ReaperService.cs", "public class LivingBeingKilled",
         "public GameObject Killer { get; private set; }"),
        ("GameClient.cs", "public class GameClient", "public GamePlayer Player\n"),
        ("GameClient.cs", "public class GameClient",
         "public bool IsPlaying => ClientState is eClientState.Playing or eClientState.Linkdead;"),
        ("bots/GameBot.cs", "public class GameBot : GameNPC", "public bool IsAutonomousWorldBot { get; private set; }"),
        ("bots/BotDummyClient.cs", "public class BotDummyClient : GameClient", "public BotDummyClient()"),
        ("ai/brain/IControlledBrain.cs", "public interface IControlledBrain", "GameLiving Owner { get; }"),
        ("bots/autonomous/AutonomousPlayerPilot.cs", "public static class AutonomousPlayerPilot",
         "public static bool IsActive(GamePlayer player)"),
        ("gameobjects/GameNPC.cs", "public class GameNPC : GameLiving", "public ABrain Brain\n"),
        # A posted action has no owner the meter can read; if upstream opens it, H5 becomes cheap (spec 3.6).
        ("ECS-Services/GameServiceBase.cs", "public abstract class GameServiceBase",
         "private sealed class PostedAction"),
        # The gamebot n: the registry's count of GameBots in the world, which the report reads every second (spec 3.8).
        ("bots/autonomous/AutonomousBotRegistry.cs", "public static class AutonomousBotRegistry",
         "public static int PopulationForBrainTick =>"),
    ]

    def test_the_nested_call_stays_inside_the_fleet_scope(self):
        # The camp planning runs the scope's one nested call (spec 3.5), over int items, which have no owner and so go
        # to gamebot / Fleet; it is reached only from H6's scope in NpcService.Tick.
        cpp = source(CPP)
        self.assertEqual(cpp.count("GameLoop.ExecuteForEach("), 1)
        self.assertIn("GameLoop.ExecuteForEach(ProjectionWork, ProjectionWork.Count, ProjectCamp);", cpp)
        self.assertIn("private static readonly List<int> ProjectionWork = new(128);", cpp)
        self.assertEqual(upstream_lines(r"\bPrepareCampPlanningTick\b"), {
            "bots/autonomous/AutonomousCampPlanningPipeline.cs": ["public static void PrepareCampPlanningTick()"],
            "ECS-Services/NpcService.cs": ["AutonomousWorldBotController.PrepareCampPlanningTick();"],
        })

    def test_each_owner_is_read_from_the_member_the_meter_names(self):
        for path, header, member in self.OWNERS:
            with self.subTest(path=path, member=member):
                self.assertIn(member, type_body(source(os.path.join(GAME_SERVER, *path.split("/"))), header))


class SwitchValueTests(unittest.TestCase):
    def test_on_and_off_in_any_case_become_true_and_false(self):
        for spec, expected in (("on", "True"), ("ON", "True"), ("On", "True"),
                               ("off", "False"), ("OFF", "False"), ("oFf", "False")):
            with self.subTest(spec=spec):
                self.assertEqual(sp.bot_cpu_value(spec), expected)

    def test_other_values_are_refused(self):
        for bad in ("", "yes", "no", "true", "false", "1", "0", "none", "onn", " on", "off "):
            with self.subTest(bad=bad), self.assertRaisesRegex(
                    sp.PropertyError, f"^HEARTHDAOC_BOT_CPU must be on or off, got {re.escape(repr(bad))}$"):
                sp.bot_cpu_value(bad)

    def test_the_property_matches_the_server_declaration(self):
        # [ServerProperty("server", "bot_cpu_meter", <description>, false)]: off in code, "False" in the table (4.1).
        self.assertEqual((sp.CPU_KEY, sp.CPU_DEFAULT), ("bot_cpu_meter", "False"))
        self.assertNotIn('"', sp.CPU_DESCRIPTION)
        self.assertNotIn("\\", sp.CPU_DESCRIPTION)

    def test_the_setting_is_required_and_declared_last(self):
        # A start without it stops before any write; argparse names the missing arguments in declaration order, so
        # test_server_properties.py's missing-argument test still finds --si-start-choice first (6.3).
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "world.db")
            with sqlite3.connect(db) as c:
                c.execute(SCHEMA)
            r = run_cli(db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on")
            self.assertEqual((r.returncode, r.stdout), (2, ""))
            self.assertIn("the following arguments are required: --bot-cpu\n", r.stderr)
            self.assertIsNone(row(db, sp.OVERRIDES_KEY))
            self.assertIsNone(row(db, sp.CPU_KEY))
            r = run_cli(db, "--gm-only-commands", "/tele;/tc")
            self.assertIn("the following arguments are required: --si-start-choice, --bot-rp-timer-wait, --bot-cpu\n",
                          r.stderr)


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

    def test_passes_the_setting_with_default_off(self):
        self.assertIn('--bot-cpu "${HEARTHDAOC_BOT_CPU-off}"', self.command)
        r = self.run_entrypoint_line()
        # A new world has no row, so its first start writes False and says so, once (4.2).
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\n"
                             "Bot realm point timer wait: on\nBot CPU meter: off\n", ""))
        self.assertEqual(row(self.db, sp.CPU_KEY), ("server", sp.CPU_DESCRIPTION, "False", "False"))
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "", ""))

    def test_on_in_any_case_turns_it_on_until_the_next_start_says_off(self):
        r = self.run_entrypoint_line(HEARTHDAOC_BOT_CPU="ON")
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\n"
                             "Bot realm point timer wait: on\nBot CPU meter: on\n", ""))
        self.assertEqual(row(self.db, sp.CPU_KEY), ("server", sp.CPU_DESCRIPTION, "False", "True"))
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Bot CPU meter: off\n", ""))
        self.assertEqual(row(self.db, sp.CPU_KEY)[3], "False")

    def test_a_bad_value_stops_the_start(self):
        for bad in ("maybe", ""):
            with self.subTest(bad=bad):
                r = self.run_entrypoint_line(HEARTHDAOC_BOT_CPU=bad)
                self.assertEqual((r.returncode, r.stdout), (2, ""))
                self.assertEqual(r.stderr, f"ERROR: HEARTHDAOC_BOT_CPU must be on or off, got {bad!r}\n")
                # Checked before any write: nothing at all.
                for key in (sp.CPU_KEY, sp.OVERRIDES_KEY, sp.SI_KEY, sp.RP_WAIT_KEY):
                    self.assertIsNone(row(self.db, key), key)


class SettingDocsTests(unittest.TestCase):
    def test_compose_passes_the_setting_with_default_off(self):
        environment = read("compose.yml").split("\n    environment:\n", 1)[1].split("\n    volumes:\n", 1)[0]
        self.assertIn("\n      HEARTHDAOC_BOT_RP_TIMER_WAIT: ${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}\n"
                      "      HEARTHDAOC_BOT_CPU: ${HEARTHDAOC_BOT_CPU-off}\n", environment + "\n")

    # The setting in .env.example, under its comment, after the realm point waits' line (spec 4.2).
    ENV_EXAMPLE = """\
HEARTHDAOC_BOT_RP_TIMER_WAIT=on
# on = the server measures the game loop's CPU per kind of bot and player and writes BOT_CPU lines to the server log
# every minute, and rows to /data/logs/bot-cpu.csv; it costs about 1-5% of one core while on. off = nothing is
# measured. A GM can also turn it on or off in game with /botcpu, until the next start. Case is ignored.
HEARTHDAOC_BOT_CPU=off
"""

    def test_env_example_documents_the_setting_default_off(self):
        example = read(".env.example")
        self.assertEqual([line for line in example.splitlines() if line.startswith("HEARTHDAOC_BOT_CPU")],
                         ["HEARTHDAOC_BOT_CPU=off"])
        self.assertIn("\n" + self.ENV_EXAMPLE, example)

    def test_handoff_lists_the_setting(self):
        handoff = read("HANDOFF.md")
        settings = between(handoff, "Settings live in `.env`", "`./hdc up` recreates the server with them.")
        self.assertIn("`HEARTHDAOC_BOT_CPU` (default `off`", settings)
        update = between(handoff, "`./hdc update <tag>` installs a specific release.", "It always backs up the world")
        self.assertIn("`HEARTHDAOC_BOT_CPU=off`", update)  # what ./hdc update adds to an existing .env


class ServerDeclarationTests(unittest.TestCase):
    def test_server_declares_the_property_with_the_same_description(self):
        found = re.findall(r'\[ServerProperty\(\s*"([^"]*)",\s*"bot_cpu_meter",\s*"([^"]*)",\s*(\w+)\s*\)\]\s*'
                           r'public static bool BOT_CPU_METER;', source(SCRIPT_CS))
        self.assertEqual(found, [("server", sp.CPU_DESCRIPTION, "false")])


# The server log's line for /botcpu at start (gameutils/ScriptMgr.cs:241, config/logconfig.xml:20): the command, the
# attribute's description and the plvl.
COMMAND_LOADED = ("10/20 14:02:00 | DBG | DOL.GS.ScriptMgr | ScriptMgr: Command - '&botcpu' - (HearthDAoC: measure the "
                  "game loop's CPU per kind of bot (BOT_CPU lines in the server log)) required plvl:2")


class SmokeTests(unittest.TestCase):
    """deploy/tests/smoke.sh's checks of the meter after the image's first, default start (spec 6.3)."""

    def smoke_lines(self):
        """smoke.sh's lines that check the meter."""
        return [line for line in read(os.path.join("tests", "smoke.sh")).splitlines()
                if "&botcpu" in line or "BOT_CPU" in line]

    def run_smoke_lines(self, log):
        """The lines, with a fail() like smoke.sh's (no docker logs), on a copy of the server log that holds log:
        (exit code, stderr)."""
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "server.log"), "w", encoding="utf-8") as f:
                f.write(log)
            script = "\n".join(["set -euo pipefail", 'fail() { echo "FAIL: $*" >&2; exit 1; }',
                                f"T={shlex.quote(tmp)}", *self.smoke_lines(), ""])
            r = subprocess.run(["bash", "-c", script], capture_output=True, text=True)
            return r.returncode, r.stderr

    def test_the_smoke_test_has_two_plain_grep_lines_after_epics(self):
        # Plain grep -q lines on the server log smoke.sh copied, not logs_have calls or a loop:
        # test_container_lines.py reads every logs_have string and smoke.sh's first loop as world-fix lines.
        lines = self.smoke_lines()
        self.assertEqual(len(lines), 2, lines)
        for line in lines:
            self.assertRegex(line, r'^(if )?grep -q "[^"]+" "\$T/server\.log"', line)
            self.assertNotIn("logs_have", line)
        smoke = read(os.path.join("tests", "smoke.sh"))
        self.assertLess(smoke.index("grep -q \"Command - '&epic' .* required plvl:2\""), smoke.index(lines[0]))

    def test_a_default_start_passes_with_botcpu_for_gms_and_no_state_line(self):
        log = f"10/20 14:01:59 | INF | DOL.GS.GameServer | Starting\n{COMMAND_LOADED}\n"
        self.assertEqual(self.run_smoke_lines(log), (0, ""))

    def test_botcpu_missing_or_open_to_players_or_a_meter_on_fails(self):
        for log in ("", COMMAND_LOADED.replace("plvl:2", "plvl:1") + "\n",
                    COMMAND_LOADED + "\n10/20 14:02:05 | INF | DOL.GS.HearthDAoC.BotCpuScript | "
                                     "BOT_CPU_STATE on source=property\n"):
            with self.subTest(log=log):
                code, stderr = self.run_smoke_lines(log)
                self.assertEqual(code, 1, stderr)
                self.assertTrue(stderr.startswith("FAIL: "), stderr)


class EndingsTests(unittest.TestCase):
    def test_the_game_loop_keeps_upstreams_endings_and_no_bom(self):
        # GL is CRLF with ten LF-only lines and no BOM (spec 5.2); the hook's lines end CRLF, as the lines beside them.
        endings.check(self, GL)
        with open(GL, "rb") as f:
            raw = f.read()
        self.assertFalse(raw.startswith(endings.BOM))
        hooks = [line for line in raw.split(b"\n") if b"HearthDAoC" in line]
        self.assertEqual(len(hooks), 2)
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)

    def test_the_npc_service_keeps_its_bom_and_crlf(self):
        # NS has a BOM and CRLF throughout (spec 5.2); the hook's three lines end CRLF too.
        endings.check(self, NS)
        with open(NS, "rb") as f:
            raw = f.read()
        self.assertTrue(raw.startswith(endings.BOM))
        self.assertEqual(raw.count(b"\n"), raw.count(b"\r\n"), "CRLF throughout")
        hooks = [line for line in raw.split(b"\n") if b"HearthDAoC" in line]
        self.assertEqual(len(hooks), 3)
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)


if __name__ == "__main__":
    unittest.main()
