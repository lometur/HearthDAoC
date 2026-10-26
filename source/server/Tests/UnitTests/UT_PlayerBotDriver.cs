using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Events;
using DOL.GS.Commands;
using DOL.GS.HearthDAoC;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC proto (#137): the player-bot driver's pure parts, its timer's meter charge, the mover's step maths and
// corridor use, the never-throw tick, the report line and its one kill per monster life, and the deaths' diagnostics.
// Helpers copied from UT_BotCpuMeter.cs:98-130 and UT_PlayerTravel.cs:27-58, :103-109.
[TestFixture, NonParallelizable]
public sealed class UT_PlayerBotDriver
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class TestServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
    }
    private static readonly IObjectDatabase EmptyDatabase =
        DispatchProxy.Create<IObjectDatabase, DOL.UnitTests.UT_AutonomousLootFlow.EmptyReadDatabase>();
    private GameServer _previousServer;
    [SetUp] public void SetUp()
    {
        _previousServer = GameServer.Instance;
        GameServer.LoadTestDouble((TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer)));
    }
    [TearDown] public void TearDown() => GameServer.LoadTestDouble(_previousServer);

    private sealed class TestPlayerBotClient : GameClient, IPlayerBotClient
    {
        public TestPlayerBotClient() : base((Socket)null) { }
    }

    private sealed class Mesh : PathfindingMgrBase
    {
        public bool Available = true;
        public bool Blocked;
        public PathfindingStatus Status = PathfindingStatus.PathFound;
        public Vector3[] Corners;
        public int Searches;
        public override bool HasNavmesh(Zone zone) => Available;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            Searches++;
            Vector3[] route = Corners ?? new[] { start, end };
            for (int i = 0; i < route.Length; i++) destination[i] = new(route[i], EDtPolyFlags.Walk);
            return new(Status, route.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => Blocked ? start : end;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => !Blocked;
    }

    private static (Region Region, Zone Zone) World()
    {
        var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
        Zone zone = new(region, 1, "Test", 0, 0, 65536, 65536, 1, false, 0, false, 0, 0, 0, 0, 0);
        typeof(Region).GetField("m_zones", Hidden).SetValue(region, new List<Zone> { zone });
        return (region, zone);
    }

    private static GamePlayer Player(GameClient client)
    {
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(player, client);
        return player;
    }

    private static void SetState(GameClient client, GameClient.eClientState state) =>
        typeof(GameClient).GetField("<ClientState>k__BackingField", Hidden)!.SetValue(client, state);

    [Test]
    public void TheDriverTimerIsChargedToItsPlayerBot()
    {
        GamePlayer bot = Player(new TestPlayerBotClient());
        BotCpuCharge charge = BotCpuMeter.Classify<ECSGameTimer>(new PlayerBotDriverTimer(bot, null), BotCpuStage.Timer, false, _ => false);
        Assert.That((charge.Kind, charge.Stage, charge.Bot, charge.Presence), Is.EqualTo((BotCpuKind.Playerbot, BotCpuStage.Timer, (GameObject)bot, false)));
        GamePlayer human = Player((GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient)));
        Assert.That(BotCpuMeter.Classify<ECSGameTimer>(new PlayerBotDriverTimer(human, null), BotCpuStage.Timer, false, _ => false).Kind,
            Is.EqualTo(BotCpuKind.Player));
    }

    [Test]
    public void TheDriverTimerConstructorDoesNotStartIt() =>
        Assert.That(new PlayerBotDriverTimer(Player(new TestPlayerBotClient()), null).IsAlive, Is.False);   // TimerService.cs:80, :110-117

    [TestCase((short)191, 250L, 47.75f)] [TestCase((short)191, 400L, 76.4f)] [TestCase((short)191, 0L, 47.75f)]
    [TestCase((short)191, 600L, 47.75f)] [TestCase((short)191, -5L, 47.75f)] [TestCase((short)0, 250L, 0f)] [TestCase((short)-1, 250L, 0f)]
    public void StepLength(short speed, long elapsed, float expected) =>
        Assert.That(PlayerBotPathMover.StepLength(speed, elapsed), Is.EqualTo(expected).Within(0.001));

    [Test]
    public void StepsStayUnderTheSpeedHackBound()   // PlayerMovementMonitor.cs:96-103
    {
        foreach (short speed in new short[] { 50, 191, 300, 600 })
            for (long elapsed = 1; elapsed <= 500; elapsed++)
            {
                long used = elapsed > 2 * PlayerBotDriver.TickMilliseconds ? PlayerBotDriver.TickMilliseconds : elapsed;
                double bound = speed * 1.15 * (used + 1000.0 / 30) / 1000;
                Assert.That(PlayerBotPathMover.StepLength(speed, elapsed), Is.LessThanOrEqualTo(bound), $"{speed} {elapsed}");
            }
    }

    [TestCase(128, 80)] [TestCase(0, 80)] [TestCase(160, 112)] [TestCase(60, 48)]
    public void MeleeStopDistanceStaysInsideMeleeRange(int range, int expected)
    {
        Assert.That(PlayerBotDriver.MeleeStopDistance(range), Is.EqualTo(expected));
        if (range >= 96) Assert.That(PlayerBotDriver.MeleeStopDistance(range), Is.LessThan(Math.Max(range, 128)));
    }

    [TestCase(1, 0, ConColor.YELLOW)] [TestCase(1, 1, ConColor.BLUE)] [TestCase(1, 5, ConColor.GREEN)]
    [TestCase(2, 0, ConColor.ORANGE)] [TestCase(4, 0, ConColor.RED)] [TestCase(6, 0, ConColor.PURPLE)]
    public void MaximumTargetCon(int group, int deaths, ConColor expected) =>
        Assert.That(PlayerBotDriver.MaximumTargetCon(group, deaths), Is.EqualTo(expected));

    // Run 1 (2026-10-21): two deaths capped the bots at green for good, and nothing cons green to a level-1 player
    // (ConLevels.cs:20: level 0 is blue, 1 yellow), so the Hibernia bot roamed for 2 hours at 35 XP.
    [Test]
    public void NothingConsGreenToALevelOnePlayer() =>
        Assert.That(Enumerable.Range(0, 128).Select(level => ConLevels.GetConColor(ConLevels.GetConLevel(1, level))),
            Has.None.EqualTo(ConColor.GREEN));

    // Nothing in reach fits the death cap: keep just enough steps for the safest monster there, as a GameBot falls
    // back to the safest camp (AWBC:3422-3437). The steps only go down.
    [TestCase(ConColor.BLUE, 2, 1)] [TestCase(ConColor.YELLOW, 3, 0)] [TestCase(ConColor.GREEN, 2, 2)]
    [TestCase(ConColor.BLUE, 0, 0)] [TestCase(ConColor.GREEN, 5, 2)]
    public void DeathStepsForTheSafestMonsterInReach(ConColor safest, int deaths, int expected) =>
        Assert.That(PlayerBotDriver.DeathStepsFor(safest, 1, deaths), Is.EqualTo(expected));

    // Runs r1-r1h (2026-10-21): on the switchback stairs by Master Lorik the two corners at their foot (Z 1795) counted as
    // reached from the stairs 37 units above, so the bot made for the next corner, under the stairs, along the stair
    // surface: it climbed back to the top and stalled there until the errand failed after 15 minutes.
    [Test]
    public void ACornerAStairFlightBelowIsNotReached()
    {
        Assert.That(PlayerTravelPath.HasReachedCorner(new(493135, 592932, 1832), new(493114, 592931, 1795)), Is.False);
        Assert.That(PlayerTravelPath.HasReachedCorner(new(493135, 592932, 1832), new(493130, 592938, 1824)), Is.True);
    }

    // The errand check of 2026-10-21 (cc18c2d): at the top of a descent on the way to Guardian Aron the corridor's next
    // corner lay at the bot's own spot, 24.4 units below its feet (Z 2157.6 against 2182). A grounded step cannot change
    // height on the spot, so the bot stalled there for good. A corner within 2 units is reached up to 64 units of height;
    // one 3 units away keeps the voxel rule. Coordinates are moved into the test zone (480000/580000 less).
    [Test]
    public void ACornerStraightBelowTheBotIsReached()
    {
        Assert.That(PlayerTravelPath.HasReachedCorner(new(497313, 588390, 2182), new(497313, 588390, 2157.6f)), Is.True);
        Assert.That(PlayerTravelPath.HasReachedCorner(new(497313, 588390, 2182), new(497316, 588390, 2157.6f)), Is.False);
        Assert.That(PlayerTravelPath.HasReachedCorner(new(497313, 588390, 2182), new(497313, 588390, 2110)), Is.False);

        var (region, zone) = World();
        var mesh = new EdgeMesh
        {
            Corners = new[] { new Vector3(17313, 8390, 2182), new Vector3(17313, 8390, 2157.6f), new Vector3(17248, 8547, 2038) }
        };
        Assert.That(new PlayerTravelPath().TryStep(region, zone, mesh.Corners[0], mesh.Corners[^1], 47.75f, 0, mesh,
            out Vector3 position, out string failure), Is.True, failure);
        Assert.That(new Vector2(position.X, position.Y), Is.Not.EqualTo(new Vector2(17313, 8390)), "on toward the next corner");
    }

    // The promotion check after the run 1 rerun (2026-10-21): by Cotswold the surface mover returned the bot's own spot,
    // and the mesh floor at the next corner lay 5 units above the corner (Z 1826 against 1821), within one voxel. The
    // fallback wanted 2 units, so the bot stood there for good. Coordinates are moved into the test zone (480000/580000
    // less). A floor more than a voxel away is another surface, a bridge or a roof: no step.
    [TestCase(5f, true)] [TestCase(24f, true)] [TestCase(40f, false)]
    public void AStepOntoTheFloorAVoxelAboveTheCornerIsTaken(float rise, bool moves)
    {
        var (region, zone) = World();
        var mesh = new EdgeMesh
        {
            FloorRise = rise,
            Corners = new[] { new Vector3(15139, 13283, 1821), new Vector3(15175, 13306, 1821), new Vector3(15309, 13390, 1820) }
        };
        Assert.That(new PlayerTravelPath().TryStep(region, zone, mesh.Corners[0], mesh.Corners[^1], 47.75f, 0, mesh,
            out Vector3 position, out string failure), Is.True, failure);
        Assert.That(position, moves ? Is.EqualTo(mesh.Corners[1] + new Vector3(0, 0, rise)) : Is.EqualTo(mesh.Corners[0]));
    }

    // The walk replay (2026-10-21): 120 steps of about 81 units every 30 s, 45-50 units nearer the goal. A window of
    // 15 s that walked over 300 units and ended less than a third of that from where it began is churning.
    [TestCase(0f, true)] [TestCase(48f, false)] [TestCase(24f, false)] [TestCase(10f, true)]
    public void AWalkThatGetsNowhereIsChurning(float forwardPerStep, bool churning)
    {
        var watch = new PlayerBotChurnWatch();
        Vector3 at = Vector3.Zero;
        bool seen = false;
        for (int tick = 0; tick <= 64; tick++)                                       // 16 s of 250 ms steps
        {
            Vector3 next = at + new Vector3(forwardPerStep, tick % 2 == 0 ? 48 : -48, 0);  // sideways back and forth
            seen |= watch.Observe(next, Goal, Vector3.Distance(at, next), tick * 250L);
            at = next;
        }
        Assert.That(seen, Is.EqualTo(churning));
    }

    [Test]
    public void AStraightWalkIsNotChurning()
    {
        var watch = new PlayerBotChurnWatch();
        for (int tick = 0; tick <= 64; tick++)
            Assert.That(watch.Observe(new(tick * 48, 0, 0), Goal, 48, tick * 250L), Is.False);
    }

    // Roaming (the review of 2026-10-21): legs back and forth between two points 1,200 units apart, a new goal each leg.
    // A 15 s window covers 2-3 legs and often ends near where it began; that is no churning walk.
    [Test]
    public void RoamingBetweenGoalsIsNotChurning()
    {
        var watch = new PlayerBotChurnWatch();
        Vector3 a = Vector3.Zero, b = new(1200, 0, 0), at = a;
        for (int tick = 0, leg = 0; tick < 4 * 120; tick++)                        // 2 minutes of 250 ms steps
        {
            Vector3 goal = leg % 2 == 0 ? b : a;
            Vector3 next = PlayerTravelPath.Advance(at, goal, 48);
            Assert.That(watch.Observe(next, goal, Vector3.Distance(at, next), tick * 250L), Is.False, $"tick {tick}");
            at = next;
            if (at == goal) leg++;
        }
    }

    // Two churning windows of one walk, then a new goal, a pause or the mover's Reset: the next walk starts with no
    // churning windows.
    [TestCase("goal")] [TestCase("pause")] [TestCase("reset")]
    public void ANewGoalAPauseOrAResetStartsANewWalk(string how)
    {
        var watch = new PlayerBotChurnWatch();
        long now = 0;
        for (; now <= 30_000; now += 250)                                            // two windows of sideways bouncing
            watch.Observe(new(0, now % 500 == 0 ? 48 : -48, 0), Goal, 96, now);
        Assert.That(watch.ChurningWindows, Is.EqualTo(2));
        if (how == "pause") watch.Observe(Vector3.Zero, Goal, 96, now + PlayerBotChurnWatch.PauseMilliseconds + 250);
        else if (how == "goal") watch.Observe(Vector3.Zero, Goal + new Vector3(0, 100, 0), 96, now);
        else watch.Reset();
        Assert.That(watch.ChurningWindows, Is.Zero);
    }

    private static readonly Vector3 Goal = new(100_000, 0, 0);                       // far away: the window never arrives

    // The surface mover never moves; the mesh floor lies FloorRise above any point asked about.
    private sealed class EdgeMesh : PathfindingMgrBase
    {
        public float FloorRise;
        public Vector3[] Corners;
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            for (int i = 0; i < Corners.Length; i++) destination[i] = new(Corners[i], EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, Corners.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => start;
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => position + new Vector3(0, 0, FloorRise);
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => true;
    }

    // A camp a bot has no corridor to is checked once: the next try 2 minutes later takes the next nearest monsters,
    // instead of the same five for the rest of the run (review of 76e41cf).
    [Test]
    public void RelocationSkipsMonstersFoundUnreachable()
    {
        int[] distances = { 700, 100, 500, 300, 900, 200, 1100, 400 };
        var rejected = new HashSet<int>();
        int[] first = PlayerBotDriver.RelocationCandidates(distances, d => new Vector3(d, 0, 0), Vector3.Zero, rejected, 5);
        Assert.That(first, Is.EqualTo(new[] { 100, 200, 300, 400, 500 }));
        rejected.UnionWith(first);
        Assert.That(PlayerBotDriver.RelocationCandidates(distances, d => new Vector3(d, 0, 0), Vector3.Zero, rejected, 5),
            Is.EqualTo(new[] { 700, 900, 1100 }));
    }

    // The GameBot's recovery thresholds, 70% health, 45% power, 35% endurance (AutonomousRestPolicy.NeedsRecovery;
    // levelmatch/DEATHS.md A1). A class with no power pool reads ManaPercent 0 (GameLiving.cs:2753): only health and
    // endurance count for it.
    [TestCase((byte)100, 0, (byte)0, (byte)100, false)] [TestCase((byte)69, 0, (byte)0, (byte)100, true)]
    [TestCase((byte)70, 0, (byte)0, (byte)100, false)] [TestCase((byte)100, 0, (byte)0, (byte)34, true)]
    [TestCase((byte)100, 0, (byte)0, (byte)35, false)] [TestCase((byte)100, 120, (byte)44, (byte)100, true)]
    [TestCase((byte)100, 120, (byte)45, (byte)100, false)]
    public void NeedsRestIgnoresPowerWithoutAPowerPool(byte health, int maxMana, byte mana, byte endurance, bool expected) =>
        Assert.That(PlayerBotDriver.NeedsRest(health, maxMana, mana, endurance), Is.EqualTo(expected));

    // A player may sit in combat (GamePlayer.Sit has no combat check): the bot sits once nothing hits it and no live
    // target is engaged, not after InCombat's 10 s, by when the next pull had started (DEATHS.md A1). A monster hitting
    // it keeps it standing to fight back.
    [TestCase(false, false, true)] [TestCase(true, false, false)] [TestCase(false, true, false)] [TestCase(true, true, false)]
    public void A_bot_may_rest_when_nothing_hits_it_and_no_live_fight_is_engaged(bool beingHit, bool inLiveFight, bool may) =>
        Assert.That(PlayerBotDriver.MayRest(beingHit, inLiveFight), Is.EqualTo(may));

    // Low, or sitting and not full, as before; and after a kill until full, as a GameBot rests to full after a camp
    // fight before its next pull (AutonomousWorldBotController.cs:423-424, :2727-2730, :3019-3047).
    [TestCase(true, false, false, false, true)] [TestCase(false, true, false, false, true)]
    [TestCase(false, true, false, true, false)] [TestCase(false, false, true, false, true)]
    [TestCase(false, false, true, true, false)] [TestCase(false, false, false, false, false)]
    public void A_bot_rests_when_low_while_sitting_and_after_a_kill_until_full(bool needsRest, bool sitting,
        bool recoverBeforePull, bool recovered, bool wants) =>
        Assert.That(PlayerBotDriver.WantsRest(needsRest, sitting, recoverBeforePull, recovered), Is.EqualTo(wants));

    [Test]
    public void A_kill_has_the_bot_recover_before_its_next_pull_and_a_dropped_fight_does_not()
    {
        MethodInfo settle = typeof(PlayerBotDriver).GetMethod("SettleTarget", BindingFlags.Static | BindingFlags.NonPublic);
        var dead = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));     // health 0: dead
        PlayerBotState Fought(bool engaged)
        {
            PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
            (state.Target, state.Engaged) = (dead, engaged);
            return state;
        }
        PlayerBotState won = Fought(true), late = Fought(false);
        settle!.Invoke(null, [won, Player(null)]);
        settle.Invoke(null, [late, Player(null)]);
        Assert.That((won.RecoverBeforePull, late.RecoverBeforePull), Is.EqualTo((true, false)), "the late bot never fought it");
    }

    [TestCase(1000, ConColor.YELLOW, 1000)] [TestCase(1000, ConColor.BLUE, 1220)] [TestCase(700, ConColor.GREEN, 1140)]
    public void TargetScore(int distance, ConColor con, int expected) =>
        Assert.That(PlayerBotDriver.TargetScore(distance, con), Is.EqualTo(expected));

    // --- the adds rule: a target with no aggressive neighbour that would join its fight ---

    // Aggressive (aggro level and range above 0), not grey to the bot (a grey monster never aggroes, StandardMobBrain.cs:685)
    // and not already fighting someone else.
    [TestCase(50, 500, false, false, true)] [TestCase(0, 500, false, false, false)] [TestCase(50, 0, false, false, false)]
    [TestCase(50, 500, true, false, false)] [TestCase(50, 500, false, true, false)]
    public void A_potential_add_is_aggressive_not_grey_and_not_busy(int level, int range, bool grey, bool busy, bool add) =>
        Assert.That(PlayerBotDriver.IsPotentialAdd(level, range, grey, busy), Is.EqualTo(add));

    private static (string, Vector3, int) At(string name, float x, int score) => (name, new Vector3(x, 0, 0), score);
    private static (string, Vector3, int) Add(string name, float x, int aggroRange = 500) => (name, new Vector3(x, 0, 0), aggroRange);

    [Test]
    public void A_crowded_candidate_gives_way_to_one_with_no_add_near_it()
    {
        var candidates = new List<(string, Vector3, int)> { At("crowded", 0, 100), At("clear", 5_000, 900) };
        var adds = new List<(string, Vector3, int)> { Add("wolf", 600) };                // 500 + 200 reaches 0
        Assert.That(PlayerBotDriver.PickUncrowded(candidates, adds, uncrowdedOnly: false), Is.EqualTo("clear"));
        Assert.That(PlayerBotDriver.PickUncrowded(candidates, [], uncrowdedOnly: false), Is.EqualTo("crowded"),
            "with no adds, the best score as before");
    }

    [TestCase(700f, 1)] [TestCase(701f, 0)] [TestCase(-700f, 1)]
    public void An_add_counts_within_its_aggro_range_and_200_of_the_candidate(float x, int crowd) =>
        Assert.That(PlayerBotDriver.Crowding("target", Vector3.Zero, new List<(string, Vector3, int)> { Add("wolf", x) }),
            Is.EqualTo(crowd));

    [Test]
    public void A_candidate_is_not_its_own_add()
    {
        var adds = new List<(string, Vector3, int)> { Add("target", 0), Add("wolf", 300, 100) };
        Assert.That(PlayerBotDriver.Crowding("target", Vector3.Zero, adds), Is.EqualTo(1));
    }

    [Test]
    public void When_every_candidate_is_crowded_the_least_crowded_then_the_best_score_wins()
    {
        var candidates = new List<(string, Vector3, int)> { At("two", 0, 100), At("one", 10_000, 900), At("one, nearer", 20_000, 800) };
        var adds = new List<(string, Vector3, int)> { Add("a", 100), Add("b", 200), Add("c", 10_100), Add("d", 20_100) };
        Assert.That(PlayerBotDriver.PickUncrowded(candidates, adds, uncrowdedOnly: false), Is.EqualTo("one, nearer"));
    }

    // The 2,600-unit ring takes only an uncrowded target; with none, the 5,000-unit ring decides.
    [Test]
    public void The_near_ring_takes_no_crowded_target()
    {
        var crowded = new List<(string, Vector3, int)> { At("crowded", 0, 100) };
        var adds = new List<(string, Vector3, int)> { Add("wolf", 100) };
        Assert.That(PlayerBotDriver.PickUncrowded(crowded, adds, uncrowdedOnly: true), Is.Null);
        crowded.Add(At("clear", 5_000, 900));
        Assert.That(PlayerBotDriver.PickUncrowded(crowded, adds, uncrowdedOnly: true), Is.EqualTo("clear"));
        Assert.That(PlayerBotDriver.PickUncrowded(new List<(string, Vector3, int)>(), adds, uncrowdedOnly: false), Is.Null);
    }

    // Every bot engaged with a monster reports its death; bots on one start row share monsters (runs 2-3).
    [Test]
    public void AMonsterLifeCountsOneKillHoweverManyBotsFoughtIt()
    {
        var monster = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
        monster.SpawnTick = 0;                                                          // never respawned
        Assert.That(PlayerBotDriverReport.Kill(monster), Is.True);
        Assert.That(PlayerBotDriverReport.Kill(monster), Is.False, "a second bot that fought it");
        monster.SpawnTick = 300_000;                                                    // respawned (GameObject.cs:614)
        Assert.That(PlayerBotDriverReport.Kill(monster), Is.True);
        Assert.That(PlayerBotDriverReport.Kill((GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC))), Is.True, "another monster");
    }

    [Test]
    public void TryComputeNext()
    {
        var (region, zone) = World();
        // No navmesh: a straight surface step.
        var flat = new Mesh { Available = false };
        Assert.That(PlayerBotPathMover.TryComputeNext(flat, region, zone, new PlayerTravelPath(), Vector3.Zero, new(1000, 0, 0), 0, 47.75f, 0,
            out Vector3 next, out _), Is.True);
        Assert.That(next.X, Is.EqualTo(47.75f).Within(0.001));
        // Never inside the stop radius.
        Assert.That(PlayerBotPathMover.TryComputeNext(flat, region, zone, new PlayerTravelPath(), Vector3.Zero, new(100, 0, 0), 80, 47.75f, 0,
            out next, out _), Is.True);
        Assert.That(next.X, Is.EqualTo(20).Within(0.001));
        // Far: the corridor turns the corner (UT_PlayerTravel.cs:151), one search.
        var mesh = new Mesh { Corners = new[] { new Vector3(100, 100, 0), new Vector3(100, 500, 0), new Vector3(600, 500, 0) } };
        var path = new PlayerTravelPath();
        Vector3 current = mesh.Corners[0];
        for (int tick = 0; tick < 4; tick++)
        {
            Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, path, current, mesh.Corners[^1], 0, 47.75f, tick * 250,
                out next, out string failure), Is.True, failure);
            Assert.That(next.X, Is.EqualTo(100), "Do not cut the corner.");
            current = next;
        }
        Assert.That(mesh.Searches, Is.EqualTo(1));
        // Near: no corridor search.
        var near = new Mesh();
        Assert.That(PlayerBotPathMover.TryComputeNext(near, region, zone, new PlayerTravelPath(), Vector3.Zero, new(200, 0, 0), 0, 47.75f, 0,
            out next, out _), Is.True);
        Assert.That((near.Searches, next.X), Is.EqualTo((0, 47.75f)));
        // Blocked: false after two replans (PTP:97-105).
        var blocked = new Mesh { Blocked = true };
        path = new PlayerTravelPath();
        for (int i = 0; i < 3; i++)
            Assert.That(PlayerBotPathMover.TryComputeNext(blocked, region, zone, path, new(100, 100, 0), new(1000, 1000, 0), 0, 47.75f, i * 15000,
                out _, out _), Is.True);
        Assert.That(PlayerBotPathMover.TryComputeNext(blocked, region, zone, path, new(100, 100, 0), new(1000, 1000, 0), 0, 47.75f, 45000,
            out _, out _), Is.False);
    }

    [Test]
    public void TickStopsWithoutAClientAndNeverThrows()
    {
        Assert.That(PlayerBotDriver.Tick(null), Is.EqualTo(0));
        var state = new PlayerBotState(Player(null));
        Assert.That(PlayerBotDriver.Tick(state), Is.EqualTo(0));
        Assert.That(state.Stopped, Is.True);
    }

    [Test]
    public void TickWhileLoadingOnlyKeepsAlive()
    {
        var client = new TestPlayerBotClient();
        SetState(client, GameClient.eClientState.WorldEnter);
        GamePlayer player = Player(client);
        player.ObjectState = GameObject.eObjectState.Active;
        player.movementComponent = new PlayerMovementComponent(player);
        player.LastPositionUpdatePacketReceivedTime = -1;
        var state = new PlayerBotState(player);
        Assert.That(PlayerBotDriver.Tick(state), Is.EqualTo(PlayerBotDriver.TickMilliseconds));
        Assert.That(player.LastPositionUpdatePacketReceivedTime, Is.EqualTo(GameLoop.GameLoopTime));
        Assert.That(state.Activity, Is.EqualTo(PlayerBotActivity.Waiting));
    }

    // --- the level-matched run's casting (levelmatch/DESIGN.md 2.7) ---

    private static Spell Damage(string type, int level, double damage) =>
        new(new DbSpell { SpellID = 900_000 + level * 1_000 + (int)damage, Name = $"{type}{level}", Type = type,
            Target = "Enemy", Damage = damage, Range = 1500, CastTime = 2.5 }, level);   // as UT_ParryBuffEffect.cs:15

    [Test]
    public void A_direct_hit_beats_damage_over_time_then_the_level_then_the_damage()
    {
        Spell dot20 = Damage("DamageOverTime", 20, 40), dd15 = Damage("DirectDamage", 15, 60),
              dd20 = Damage("DirectDamage", 20, 70), bolt20 = Damage("Bolt", 20, 90);
        Assert.That((PlayerBotDriver.Better(dd15, dot20), PlayerBotDriver.Better(dot20, dd15)), Is.EqualTo((true, false)));
        Assert.That((PlayerBotDriver.Better(dd20, dd15), PlayerBotDriver.Better(bolt20, dd20), PlayerBotDriver.Better(dd20, bolt20)),
            Is.EqualTo((true, true, false)));
        Spell flatDot = Damage("DamageOverTimeNoVariance", 25, 60);
        Assert.That((PlayerBotDriver.Better(dd15, flatDot), PlayerBotDriver.Better(flatDot, dot20)), Is.EqualTo((true, true)));
    }

    [Test]
    public void The_cast_watch_allows_three_fruitless_requests_then_swings()
    {
        (int a, byte m, bool s) = PlayerBotDriver.WatchCast(false, 0, 0, 100);
        Assert.That((a, m, s), Is.EqualTo((1, (byte)100, false)));
        (a, m, s) = PlayerBotDriver.WatchCast(true, a, m, 100);
        (a, m, s) = PlayerBotDriver.WatchCast(true, a, m, 100);
        Assert.That((a, s), Is.EqualTo((3, false)));
        Assert.That(PlayerBotDriver.WatchCast(true, a, m, 100), Is.EqualTo((4, (byte)100, true)));
        Assert.That(PlayerBotDriver.WatchCast(true, 3, 100, 80), Is.EqualTo((1, (byte)80, false)));   // it hurt: a new run
        Assert.That(PlayerBotDriver.WatchCast(false, 3, 100, 100), Is.EqualTo((1, (byte)100, false))); // a new target
    }

    // A player gets "must wait" for an interruptible cast while hits interrupt it (SpellHandler.cs:570-590), so the bot
    // does not start one then (DEATHS.md A2; a GameBot under melee stops casting and fights, BotBrain.cs:3336-3370); an
    // instant or uninterruptible spell still goes. Otherwise the old rules: a spell, 18% power, the cast watch's block.
    [TestCase(true, (byte)50, 0L, true, true, false)] [TestCase(true, (byte)50, 0L, true, false, true)]
    [TestCase(true, (byte)50, 0L, false, true, true)] [TestCase(false, (byte)50, 0L, false, false, false)]
    [TestCase(true, (byte)17, 0L, false, true, false)] [TestCase(true, (byte)18, 0L, false, true, true)]
    [TestCase(true, (byte)50, 1_001L, false, false, false)] [TestCase(true, (byte)50, 1_000L, false, false, true)]
    public void A_bot_starts_no_interruptible_cast_while_hits_interrupt_it(bool hasSpell, byte mana, long blockedUntil,
        bool interrupted, bool interruptible, bool casts) =>
        Assert.That(PlayerBotDriver.ShouldCast(hasSpell, mana, 1_000, blockedUntil, interrupted, interruptible), Is.EqualTo(casts));

    [Test]
    public void A_cast_is_interruptible_unless_it_is_instant_or_uninterruptible()   // SpellHandler.cs:571
    {
        Spell Cast(double seconds, bool uninterruptible = false) => new(new DbSpell { SpellID = 990_001, Name = "Bolt",
            Type = "Bolt", Target = "Enemy", Damage = 90, Range = 1500, CastTime = seconds, Uninterruptible = uninterruptible }, 20);
        Assert.That((PlayerBotDriver.Interruptible(Cast(3.0)), PlayerBotDriver.Interruptible(Cast(0)),
            PlayerBotDriver.Interruptible(Cast(3.0, uninterruptible: true))), Is.EqualTo((true, false, false)));
    }

    // A player at a level with its skill caches set as GetAllUsableSkills / GetAllUsableListSpells hold them
    // (GamePlayer.cs:3394-3447; non-empty, so neither rebuilds).
    private static GamePlayer Caster(int level, List<(Skill, Skill)> usable, List<(SpellLine, List<Skill>)> listSpells)
    {
        GamePlayer player = Player(null);
        typeof(GamePlayer).GetField("m_dbCharacter", Hidden)!.SetValue(player, new DbCoreCharacter { Level = level });
        typeof(GamePlayer).GetField("_usableSkills", Hidden)!.SetValue(player, usable);
        typeof(GamePlayer).GetField("_usableListSpells", Hidden)!.SetValue(player, listSpells);
        return player;
    }

    [Test]
    public void A_hybrids_damage_spell_comes_from_its_usable_skills()          // REVIEW-FIXES 19: Paladin, Thane
    {
        var storm = new SpellLine("Stormcalling", "Stormcalling", Specs.Stormcalling, false);
        Spell dd20 = Damage("DirectDamage", 20, 70), dd30 = Damage("DirectDamage", 30, 90), dot25 = Damage("DamageOverTime", 25, 60);
        GamePlayer thane = Caster(28, [(dd20, storm), (dd30, storm), (dot25, storm), (storm, null)], [(storm, [])]);
        Assert.That(PlayerBotDriver.BestDamageSpellName(thane), Is.EqualTo("DirectDamage20:20"));   // 30 is above its level
        var fire = new SpellLine("Fire", "Fire", Specs.Fire_Magic, true);
        GamePlayer wizard = Caster(40, [(storm, null)], [(fire, [dot25, Damage("Bolt", 35, 120)])]);
        Assert.That(PlayerBotDriver.BestDamageSpellName(wizard), Is.EqualTo("Bolt35:35"));
        Assert.That(PlayerBotDriver.BestDamageSpellName(Caster(40, [(storm, null)], [(fire, [])])), Is.EqualTo("-"));
    }

    // --- the healer's self-heal (DEATHS.md A6) ---

    private static readonly SpellLine Mending = new("Rejuvenation", "Rejuvenation", Specs.Rejuvenation, true);

    private static (Spell, SpellLine) Heal(int level, double value, string target = "Realm", double castTime = 2.5,
        string type = "Heal", int instrument = 0, bool uninterruptible = false) =>
        (new(new DbSpell { SpellID = 920_000 + level * 1_000 + (int)Math.Abs(value), Name = $"{type}{level}", Type = type,
            Target = target, Value = value, Range = 2000, CastTime = castTime, InstrumentRequirement = instrument,
            Uninterruptible = uninterruptible }, level), Mending);

    private static string SelfHealName(IEnumerable<(Spell, SpellLine)> spells, int level = 25, int maxHealth = 1000,
        bool interrupted = false, Func<Spell, bool> ready = null) =>
        PlayerBotDriver.FindSelfHeal(spells, level, maxHealth, interrupted, ready ?? (_ => true)) is (Spell s, SpellLine)
            ? FormattableString.Invariant($"{s.Name}:{s.Value}") : "-";

    // Below 60% and out of a cast, as a GameBot heals itself below 65% before it fights or rests (BotBrain.cs:4890-4897,
    // AutonomousWorldBotController.cs:2725), with the retry timer's wait.
    [TestCase((byte)59, false, 0L, true)] [TestCase((byte)60, false, 0L, false)] [TestCase((byte)30, true, 0L, false)]
    [TestCase((byte)30, false, 1_001L, false)] [TestCase((byte)30, false, 1_000L, true)]
    public void A_bot_heals_itself_below_60_percent_out_of_a_cast(byte health, bool casting, long nextTry, bool heals) =>
        Assert.That(PlayerBotDriver.WantsSelfHeal(health, casting, 1_000, nextTry), Is.EqualTo(heals));

    // A heal on the bot itself: a Heal on a realm mate, itself or its group, at or below its level, with no instrument;
    // the most health back (a negative value is a percent of its health, GameBot.cs:1887-1892).
    [Test]
    public void A_bot_heals_itself_with_the_heal_that_gives_the_most_back()
    {
        var spells = new List<(Spell, SpellLine)>
        {
            Heal(10, 100), Heal(15, 200, target: "Group"), Heal(20, -25, target: "Self"),
            Heal(30, 500),                                                   // above its level
            Heal(12, 400, instrument: 1),                                    // a song
            Heal(14, 450, target: "Pet"), Heal(16, 460, target: "Enemy"),    // not on itself
            Heal(18, 470, type: "HealOverTime"),                             // not a heal at once
            (Damage("DirectDamage", 20, 900), Mending),
        };
        Assert.That(SelfHealName(spells), Is.EqualTo("Heal20:-25"));                     // 25% of 1,000 = 250
        Assert.That(SelfHealName(spells, maxHealth: 400), Is.EqualTo("Heal15:200"));     // 25% of 400 = 100
        Assert.That(SelfHealName(spells, level: 9), Is.EqualTo("-"));
        Assert.That(SelfHealName([]), Is.EqualTo("-"));
    }

    [Test]
    public void An_instant_heal_wins_a_tie_then_the_higher_level()
    {
        Assert.That(SelfHealName([Heal(20, 300), Heal(10, 300, castTime: 0)]), Is.EqualTo("Heal10:300"));
        Assert.That(SelfHealName([Heal(10, 300), Heal(20, 300)]), Is.EqualTo("Heal20:300"));
    }

    // A player gets "must wait" for an interruptible cast while hits interrupt it (SpellHandler.cs:570-590); a GameBot then
    // heals only with an instant heal (BotBrain.cs:4761).
    [Test]
    public void While_hits_interrupt_it_a_bot_heals_only_with_an_instant_or_uninterruptible_heal()
    {
        var spells = new List<(Spell, SpellLine)> { Heal(20, 400), Heal(10, 150, castTime: 0), Heal(12, 120, uninterruptible: true) };
        Assert.That((SelfHealName(spells), SelfHealName(spells, interrupted: true)), Is.EqualTo(("Heal20:400", "Heal10:150")));
        Assert.That(SelfHealName([Heal(20, 400)], interrupted: true), Is.EqualTo("-"));
    }

    [Test]
    public void A_heal_not_ready_is_skipped()
    {
        var spells = new List<(Spell, SpellLine)> { Heal(20, 400), Heal(10, 150) };
        Assert.That(SelfHealName(spells, ready: spell => spell.Level != 20), Is.EqualTo("Heal10:150"));
        Assert.That(SelfHealName(spells, ready: _ => false), Is.EqualTo("-"));
    }

    // The nominal cost, before focus and the like: a negative power is a percent of the pool (SpellHandler.cs:1207-1213).
    [TestCase(200, 50, 50, true)] [TestCase(200, 49, 50, false)] [TestCase(200, 50, -25, true)] [TestCase(200, 49, -25, false)]
    [TestCase(0, 0, 0, true)]
    public void A_heal_needs_the_power_it_costs(int maxMana, int mana, int power, bool enough) =>
        Assert.That(PlayerBotDriver.HasPowerFor(maxMana, mana, power), Is.EqualTo(enough));

    [Test]
    public void ReportFormat()
    {
        var w = new PlayerBotDriverWindow(60, 30, 7200, 180.0, 55.8, 0, 1, 6, 7, 12, 0, 4, 0, 41, 1, 3, 4, 12850, 2, 0, 0, 0,
            8.7, 0.0, 0, 12, 218);
        Assert.That(PlayerBotDriverReport.Format(w), Is.EqualTo(
            "PLAYER_BOT_DRIVER win=60 bots=30 ticksPerBotSec=4.0 thinkMsPerBotSec=0.069 moverMsPerBotSec=0.031 waiting=0 dead=1 " +
            "resting=6 hunting=7 fighting=12 casting=0 roaming=4 task=0 kills=41 deaths=1 levelUps=3 maxLevel=4 xp=12850 blocked=2 " +
            "stuck=0 ldCancelled=0 errors=0 nearPlayerBots=8.7 nearGamebots=0.0 gamebotFidelity=0/12/218"));
        string empty = PlayerBotDriverReport.Format(new PlayerBotDriverWindow { Seconds = 60 });
        Assert.That(empty, Does.Contain("bots=0 ticksPerBotSec=- thinkMsPerBotSec=- moverMsPerBotSec=-"));
        Assert.That(empty, Does.Contain("nearPlayerBots=- nearGamebots=-"));
    }

    [Test]
    public void TallyFormat() => Assert.That(
        PlayerBotDriverReport.FormatTally(20, "Pbotaaaab", 40, 12, 1, 123456, 120456, 0, 1, PlayerBotActivity.Fighting),
        Is.EqualTo("PLAYER_BOT_TALLY window=20 bot=Pbotaaaab level=40 kills=12 deaths=1 xp=123456 netXp=120456 castFailed=0 " +
                   "retrained=1 activity=Fighting"));

    [Test]
    public void A_bots_tally_keeps_its_gains_and_its_net_experience()      // levelmatch REVIEW-FIXES 9: end minus start
    {
        MethodInfo observe = typeof(PlayerBotDriver).GetMethod("ObserveXp", BindingFlags.Static | BindingFlags.NonPublic);
        GamePlayer player = Player(null);
        var row = new DbCoreCharacter { Level = 40, Experience = 1_000 };
        typeof(GamePlayer).GetField("m_dbCharacter", Hidden)!.SetValue(player, row);
        PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
        (state.LastLevel, state.LastExperience, state.StartExperience) = (40, 1_000, 1_000);
        row.Experience = 1_500;
        observe!.Invoke(null, [state, player]);
        row.Experience = 1_300;                                                  // a death's loss (none on base-lm)
        observe.Invoke(null, [state, player]);
        Assert.That((state.Gained, state.NetExperience, state.Retrains), Is.EqualTo((500L, 300L, 0)));
    }

    [Test]
    public void A_monster_two_bots_fought_counts_in_one_tally()               // the TALLY kills sum to the DRIVER kills
    {
        MethodInfo settle = typeof(PlayerBotDriver).GetMethod("SettleTarget", BindingFlags.Static | BindingFlags.NonPublic);
        var monster = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));   // health 0: dead
        PlayerBotState Engaged()
        {
            PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
            (state.Target, state.Engaged) = (monster, true);
            return state;
        }
        PlayerBotState first = Engaged(), second = Engaged();
        settle!.Invoke(null, [first, Player(null)]);
        settle.Invoke(null, [second, Player(null)]);
        Assert.That((first.Kills, second.Kills), Is.EqualTo((1, 0)), "the second bot's kill is the first's (PlayerBotDriverReport.Kill)");
        Assert.That((first.Target, second.Target, first.Engaged, second.Engaged), Is.EqualTo(((GameLiving)null, (GameLiving)null, false, false)));
    }

    // --- the deaths' diagnostics (levelmatch/DEATHS.md section 2) ---

    [Test]
    public void Every_bot_that_fought_a_monster_counts_it_in_its_life()      // DEATH lifeKills= and sinceKillSec=
    {
        PropertyInfo time = typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime))!;
        long saved = GameLoop.GameLoopTime;
        MethodInfo settle = typeof(PlayerBotDriver).GetMethod("SettleTarget", BindingFlags.Static | BindingFlags.NonPublic);
        var monster = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));   // health 0: dead
        PlayerBotState Fought(bool engaged)
        {
            PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
            (state.Target, state.Engaged) = (monster, engaged);
            return state;
        }
        try
        {
            time.SetValue(null, 90_000L);
            PlayerBotState first = Fought(true), second = Fought(true), late = Fought(false);
            foreach (PlayerBotState state in new[] { first, second, late })
                settle!.Invoke(null, [state, Player(null)]);
            Assert.That((first.LifeKills, second.LifeKills, late.LifeKills), Is.EqualTo((1, 1, 0)),
                "the second bot shares the monster with the first; the late one never reached it");
            Assert.That((first.LastKillTick, second.LastKillTick, late.LastKillTick), Is.EqualTo((90_000L, 90_000L, 0L)));
        }
        finally
        {
            time.SetValue(null, saved);
        }
    }

    [Test]
    public void FightFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatFight(new PlayerBotFight("Pbotaaaac", 40, "widow claw", 40, ConColor.YELLOW, false,
                true, 100, 38, 100, 12, 21, 1, 9, 2, 4, 0, 0)),
            Is.EqualTo("PLAYER_BOT_FIGHT bot=Pbotaaaac level=40 target=\"widow claw\" targetLevel=40 con=YELLOW from=pull result=kill " +
                       "startHp=100 endHp=38 startMana=100 endMana=12 sec=21 adds=1 swings=9 notVisible=2 casts=4 lifeKills=0 rests=0"));
        Assert.That(PlayerBotDriverReport.FormatFight(new PlayerBotFight("Pbothaaab", 43, "empyrean guardian", 36, ConColor.BLUE, true,
                false, 64, 51, 100, 100, 7, 2, 3, 0, 0, 3, 1)),
            Is.EqualTo("PLAYER_BOT_FIGHT bot=Pbothaaab level=43 target=\"empyrean guardian\" targetLevel=36 con=BLUE from=defence " +
                       "result=dropped startHp=64 endHp=51 startMana=100 endMana=100 sec=7 adds=2 swings=3 notVisible=0 casts=0 " +
                       "lifeKills=3 rests=1"));
    }

    // The fields PLAYER_BOT_DEATH appends after task= (its earlier fields stay as they were: runs/legcheck.py reads them).
    [Test]
    public void DeathDetailFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatDeathDetail(new PlayerBotDeathDetail(PlayerBotActivity.Fighting, 62, 14, 71, false,
                2, 3, 0, 40, 100, 6, 1, "widow claw", 40, true, 748)),
            Is.EqualTo("activity=Fighting startHp=62 fightSec=14 targetHp=71 from=pull adds=2 lifeKills=3 rests=0 sinceKillSec=40 " +
                       "mana=100 swings=6 notVisible=1 killer=\"widow claw\" killerLevel=40 killerIsTarget=True fromBind=748"));
        // Out of a fight, no target, no kill this life, no killer named by the Dying event, the bind in another region.
        Assert.That(PlayerBotDriverReport.FormatDeathDetail(new PlayerBotDeathDetail(PlayerBotActivity.Hunting, -1, -1, -1, true,
                -1, 0, 2, -1, 37, -1, -1, null, 0, false, -1)),
            Is.EqualTo("activity=Hunting startHp=-1 fightSec=-1 targetHp=-1 from=defence adds=-1 lifeKills=0 rests=2 sinceKillSec=-1 " +
                       "mana=37 swings=-1 notVisible=-1 killer=\"\" killerLevel=0 killerIsTarget=False fromBind=-1"));
    }

    private sealed class TestKiller : GameObject
    {
        public override eGameObjectType GameObjectType => eGameObjectType.NPC;
    }

    // A bolt or an arrow lands from a timer (BoltSpellHandler.cs:153-165, AttackAction.cs:364-365), in the timer phase the
    // think runs in: the bot can be dead at its think before the reaper's Dying event names the killer (GameLoop.cs:149-159).
    [TestCase(false, false, false)] [TestCase(false, true, true)] [TestCase(true, true, false)]
    public void A_death_line_waits_for_its_killer_only_while_the_reaper_has_the_bot(bool named, bool beingReaped, bool waits) =>
        Assert.That(PlayerBotDriver.AwaitsDying(named ? (TestKiller)RuntimeHelpers.GetUninitializedObject(typeof(TestKiller)) : null,
            beingReaped), Is.EqualTo(waits));

    [Test]
    public void A_death_line_names_the_killer_it_has_by_then()
    {
        var detail = new PlayerBotDeathDetail(PlayerBotActivity.Fighting, 62, 14, 71, false, 2, 3, 0, 40, 100, 6, 1, null, 0, false, 748);
        var target = (TestKiller)RuntimeHelpers.GetUninitializedObject(typeof(TestKiller));
        var add = (TestKiller)RuntimeHelpers.GetUninitializedObject(typeof(TestKiller));
        (target.Name, target.Level, add.Name, add.Level) = ("widow claw", 40, "a wolf", 38);
        const string head = "PLAYER_BOT_DEATH bot=Pbotaaaac task=False";
        Assert.That(PlayerBotDriver.DeathLine(head, detail, target, target), Is.EqualTo(head + " activity=Fighting startHp=62 fightSec=14 " +
            "targetHp=71 from=pull adds=2 lifeKills=3 rests=0 sinceKillSec=40 mana=100 swings=6 notVisible=1 killer=\"widow claw\" " +
            "killerLevel=40 killerIsTarget=True fromBind=748"));
        Assert.That(PlayerBotDriver.DeathLine(head, detail, add, target), Does.EndWith(" killer=\"a wolf\" killerLevel=38 killerIsTarget=False fromBind=748"));
        Assert.That(PlayerBotDriver.DeathLine(head, detail, null, null), Does.EndWith(" killer=\"\" killerLevel=0 killerIsTarget=False fromBind=748"),
            "no killer and no target: not the target");
    }

    [TestCase(0, (byte)0, (byte)100)] [TestCase(120, (byte)37, (byte)37)] [TestCase(120, (byte)0, (byte)0)]
    public void PowerPercentIsFullWithoutAPowerPool(int maxMana, byte mana, byte expected) =>
        Assert.That(PlayerBotDriver.PowerPercent(maxMana, mana), Is.EqualTo(expected));

    // WeaponAction.cs:309 sets a new AttackData per swing; the think sees the last one, often twice.
    [Test]
    public void Each_swing_counts_once_and_the_view_gates_refusals_apart()
    {
        PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
        var hit = new AttackData { AttackResult = eAttackResult.HitUnstyled };
        PlayerBotDriver.CountSwing(state, null);                                        // no swing yet
        PlayerBotDriver.CountSwing(state, hit);
        PlayerBotDriver.CountSwing(state, hit);                                         // the same swing, a think later
        PlayerBotDriver.CountSwing(state, new AttackData { AttackResult = eAttackResult.TargetNotVisible });
        PlayerBotDriver.CountSwing(state, new AttackData { AttackResult = eAttackResult.TargetNotVisible });   // its retry
        PlayerBotDriver.CountSwing(state, new AttackData { AttackResult = eAttackResult.Missed });
        Assert.That((state.Swings, state.NotVisible), Is.EqualTo((4, 2)));
    }

    [Test]
    public void The_dying_handler_names_a_bots_killer_ignores_anyone_else_and_goes_with_stop()
    {
        FieldInfo instance = typeof(GameEventMgr).GetField("_soleInstance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (GameEventMgr)instance.GetValue(null)!;
        var states = (ConcurrentDictionary<GamePlayer, PlayerBotState>)typeof(PlayerBotDriver)
            .GetField("_states", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        GamePlayer bot = Player(null), human = Player(null);
        bot.ObjectState = GameObject.eObjectState.Inactive;                             // Stop leaves the world alone
        var killer = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
        var state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
        try
        {
            GameEventMgr.LoadTestDouble(new GameEventMgr());
            states[bot] = state;
            GameEventMgr.AddHandler(bot, GameLivingEvent.Dying, PlayerBotDriver.OnBotDying);   // as Start adds it
            Assert.DoesNotThrow(() => PlayerBotDriver.OnBotDying(GameLivingEvent.Dying, human, new DyingEventArgs(killer)));
            Assert.DoesNotThrow(() => PlayerBotDriver.OnBotDying(GameLivingEvent.Dying, killer, new DyingEventArgs(bot)));
            Assert.DoesNotThrow(() => PlayerBotDriver.OnBotDying(GameLivingEvent.Dying, null, null));
            Assert.DoesNotThrow(() => PlayerBotDriver.OnBotDying(GameLivingEvent.Dying, bot, EventArgs.Empty));
            Assert.That(state.Killer, Is.Null);
            GameEventMgr.Notify(GameLivingEvent.Dying, bot, new DyingEventArgs(killer));   // GameObject.Notify (GameLiving.cs:2073)
            Assert.That(state.Killer, Is.SameAs(killer));
            PlayerBotDriver.Stop(bot);
            Assert.That(GameEventMgr.NumObjectHandlers, Is.Zero, "Stop removes the handler Start added");
        }
        finally
        {
            states.TryRemove(bot, out _);
            GameEventMgr.LoadTestDouble(previous);
        }
    }
}
