using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.Commands;
using DOL.GS.HearthDAoC;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC proto (#137): the player-bot driver's pure parts, its timer's meter charge, the mover's step maths and
// corridor use, the never-throw tick, the report line and its one kill per monster life. Helpers copied from
// UT_BotCpuMeter.cs:98-130 and UT_PlayerTravel.cs:27-58, :103-109.
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

    // The roster's classes have no power pool, so ManaPercent reads 0 (GameLiving.cs:2753): only health and endurance count.
    [TestCase((byte)100, 0, (byte)0, (byte)100, false)] [TestCase((byte)49, 0, (byte)0, (byte)100, true)]
    [TestCase((byte)100, 0, (byte)0, (byte)19, true)] [TestCase((byte)100, 120, (byte)21, (byte)100, true)]
    [TestCase((byte)100, 120, (byte)22, (byte)100, false)]
    public void NeedsRestIgnoresPowerWithoutAPowerPool(byte health, int maxMana, byte mana, byte endurance, bool expected) =>
        Assert.That(PlayerBotDriver.NeedsRest(health, maxMana, mana, endurance), Is.EqualTo(expected));

    [TestCase(1000, ConColor.YELLOW, 1000)] [TestCase(1000, ConColor.BLUE, 1220)] [TestCase(700, ConColor.GREEN, 1140)]
    public void TargetScore(int distance, ConColor con, int expected) =>
        Assert.That(PlayerBotDriver.TargetScore(distance, con), Is.EqualTo(expected));

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
}
