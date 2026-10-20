"""The bot CPU meter (spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.2).

While the meter is on, each game-loop callback is timed and charged to the kind of its owner and to its stage. The hot
path is unit-tested (UT_BotCpuMeter), through the real hook H1 in GameLoop.ExecuteForEach; the hook's shape in the
upstream file, the upstream facts the meter mirrors (the stage of each profile key, the pool's entry point, the member
each item's owner is read from) and the upstream file's line endings need the source, so these check it.
"""
import os
import re
import unittest

from tests import endings

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
FORK = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
LOOP = os.path.join(GAME_SERVER, "Managers", "GameLoop")
GL = os.path.join(LOOP, "GameLoop.cs")
POOL = os.path.join(LOOP, "GameLoopThreadPool.cs")
MT = os.path.join(LOOP, "GameLoopThreadPoolMultiThreaded.cs")

# Hook H1 (spec 3.1): while the meter is on, it gets the pool call; off, one bool read before the unchanged call.
GUARD = ("if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, "
         "action); return; }")
POOL_CALL = "_threadPool.ExecuteForEach(items, toExclusive, action);"


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

    def test_each_owner_is_read_from_the_member_the_meter_names(self):
        for path, header, member in self.OWNERS:
            with self.subTest(path=path, member=member):
                self.assertIn(member, type_body(source(os.path.join(GAME_SERVER, *path.split("/"))), header))


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


if __name__ == "__main__":
    unittest.main()
