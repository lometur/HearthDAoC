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

// HearthDAoC player bots (#137): the player-bot driver's pure parts, its timer's meter charge, the mover's step maths and
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
        public bool Blocked, Walled;
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
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded) =>
            GetMoveAlongSurface(zone, start, end, filters);     // a flat floor: the grounded move is the plain one
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => !Blocked && !Walled;
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

    // A death lowers the cap a step, down to green, and nothing raises it again: a GameBot's steps are clamped to its
    // natural cap's distance from green and never decay, neither over time nor on a level-up (AWBC:3755-3765,
    // :3793-3796). The old rollback reset them to fit the safest monster in reach (DEATHS.md A4: 729 of lm-fsmoke's
    // 1,482 deaths came at deathSteps=0).
    [TestCase(0, 1)] [TestCase(1, 2)] [TestCase(2, 2)] [TestCase(5, 2)]
    public void A_death_lowers_the_cap_a_step_down_to_green(int steps, int next) =>
        Assert.That(PlayerBotDriver.NextDeathSteps(steps), Is.EqualTo(next));

    // DEATHS.md A4: a bot whose searches find monsters in reach but none under its cap takes none and, 30 s on, moves to
    // a camp that fits; one that has reached no target for 2 minutes has outgrown its ground. The cap's reason wins.
    // A death (#167 F4; anywhere, C1b) owes a relocation at once, before the capped and outgrown clocks, while the
    // release's immunity lasts too (#167 D4: the plan reads IsPlannable; C1's immune rows went with its gate). A
    // break-off (#167 C2) owes one too, as brokeoff.
    [TestCase(0L, 0L, 1_000_000L, null, null)]
    [TestCase(10_000L, 0L, 129_999L, null, null)] [TestCase(10_000L, 0L, 130_000L, null, "outgrown")]
    [TestCase(0L, 100_000L, 129_999L, null, null)] [TestCase(0L, 100_000L, 130_000L, null, "capped")]
    [TestCase(10_000L, 100_000L, 130_000L, null, "capped")]
    [TestCase(0L, 0L, 1_000_000L, "died", "died")] [TestCase(10_000L, 100_000L, 130_000L, "died", "died")]
    [TestCase(0L, 0L, 1_000_000L, "brokeoff", "brokeoff")] [TestCase(10_000L, 100_000L, 130_000L, "brokeoff", "brokeoff")]
    public void A_capped_bot_relocates_after_30_s_and_an_outgrown_one_after_2_minutes(long roamingSince, long cappedSince,
        long now, string owed, string reason) =>
        Assert.That(PlayerBotDriver.RelocateReason(roamingSince, cappedSince, now, owed), Is.EqualTo(reason));

    // A player the server holds immune (GamePlayer.cs:12284-12287): the release's 10 s.
    private sealed class ImmunePlayer : GamePlayer
    {
        private ImmunePlayer() : base(null, null) { }
        public override bool IsInvulnerableToAttack => true;
    }

    // An immune bot, alive in the world (a released one).
    private static ImmunePlayer LiveImmunePlayer()
    {
        var player = (ImmunePlayer)RuntimeHelpers.GetUninitializedObject(typeof(ImmunePlayer));
        player.ObjectState = GameObject.eObjectState.Active;
        typeof(GameObject).GetField("m_health", Hidden)!.SetValue(player, 100);
        return player;
    }

    // #167 D4: the replan no longer waits out the release's immunity. Its old gate (RelocateReason's immune, C1) held
    // the plan, because the plan's IsAttackable dropped every monster while the bot was immune; the plan reads
    // IsPlannable now. A bot still immune after its release runs its death's replan at once: here, in a world with no
    // region, it finds no zone and hunts where it is (EndReplan), as it does then after the immunity.
    [Test]
    public void A_bot_still_immune_after_its_release_replans_at_once()
    {
        ImmunePlayer player = LiveImmunePlayer();
        var state = new PlayerBotState(Player(null)) { ReplanOwed = PlayerBotDriver.Died, ReplanSince = 9_000 };
        typeof(PlayerBotDriver).GetMethod("RelocateIfOutgrown", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state, player, 20_000L]);
        Assert.That((state.ReplanOwed, state.ReplanSince), Is.EqualTo(((string)null, 0L)));
    }

    // #167 D4: the plan's monsters (IsPlannable): a monster the bot would hunt, alive, in its region, whether or not the
    // bot may attack it now. While the release's immunity lasts a player may attack no monster
    // (AbstractServerRules.cs:385-392), so the old filter (IsAttackable) found no camp: st-deathsB's 279 of 312 none
    // checked=0 replans, and C1's gate that held the plan for 10 s since.
    [Test]
    public void The_plan_counts_a_camp_while_the_bot_is_immune()
    {
        ImmunePlayer player = LiveImmunePlayer();
        GameNPC monster = LiveMonster();
        typeof(GameNPC).GetField("m_ownBrain", Hidden)!.SetValue(monster, new DOL.AI.Brain.StandardMobBrain());
        Assert.That(PlayerBotDriver.IsPlannable(player, monster), Is.True, "a monster it can't attack until the immunity ends");
        typeof(GameObject).GetField("m_health", Hidden)!.SetValue(monster, 0);
        Assert.That(PlayerBotDriver.IsPlannable(player, monster), Is.False, "a dead one");
    }

    // #167 E M1: a training dummy is no monster: region 51's are plain GameNPC rows that never lose health (st-deathsD2:
    // a bot died on one in 92 s; two planned camps of them and killed nothing for about 12 minutes each).
    [TestCase("training dummy", true)] [TestCase("fighting dummy", true)] [TestCase("target dummy", true)]
    [TestCase("Hitback Dummy - Right Click to Reset", true)] [TestCase("dummy golem", false)]
    [TestCase("tattered product", false)] [TestCase(null, false)]
    public void A_training_dummy_is_known_by_its_name(string name, bool prop) =>
        Assert.That(PlayerBotDriver.IsTrainingPropName(name), Is.EqualTo(prop));

    [Test]
    public void The_plan_and_the_pick_leave_out_a_training_dummy()
    {
        GameNPC dummy = Mob(), monster = Mob();
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(dummy, "training dummy");     // the setter needs a client
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(monster, "tattered product"); //   service when active
        Assert.That(PlayerBotDriver.IsExperienceTarget(dummy), Is.False);
        Assert.That(PlayerBotDriver.IsPlannable(LiveImmunePlayer(), dummy), Is.False);
        Assert.That(PlayerBotDriver.IsExperienceTarget(monster), Is.True);
    }

    // A monster the plan would count, alive in region at (x, y) (an NPC's position is its movement component's).
    private static GameNPC CampMonster(Region region, float x, float y, byte level)
    {
        GameNPC npc = LiveMonster();
        typeof(GameNPC).GetField("m_ownBrain", Hidden)!.SetValue(npc, new DOL.AI.Brain.StandardMobBrain());
        typeof(GameObject).GetField("m_level", Hidden)!.SetValue(npc, level);
        var movement = new NpcMovementComponent(npc);
        typeof(NpcMovementComponent).GetField("_ownerPosition", Hidden)!.SetValue(movement, new Vector3(x, y, 0));
        npc.movementComponent = movement;
        npc.CurrentRegion = region;
        return npc;
    }

    // The review of D (D4): the plan's monsters where RelocateIfOutgrown takes them (PlanMonsters). A bot still immune
    // after its release counts the camp round it: with the old filter put back (IsAttackable, false for every monster
    // while the bot is immune) no test failed, and the immune replan planned nothing in the game. A monster outside the
    // zone or a dead one is none.
    [Test]
    public void The_plans_monsters_count_a_camp_while_the_bot_is_immune()
    {
        var calculators = (DOL.GS.PropertyCalc.IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        DOL.GS.PropertyCalc.IPropertyCalculator previous = calculators[(int)eProperty.LivingEffectiveLevel];
        calculators[(int)eProperty.LivingEffectiveLevel] = new LevelOnly();
        try
        {
            var (region, zone) = World();
            ImmunePlayer player = LiveImmunePlayer();
            typeof(GameObject).GetField("m_level", Hidden)!.SetValue(player, (byte)20);
            player.CurrentRegion = region;
            GameNPC camp = CampMonster(region, 1_000, 1_000, 20), outside = CampMonster(region, 70_000, 1_000, 20),
                dead = CampMonster(region, 2_000, 1_000, 20);
            typeof(GameObject).GetField("m_health", Hidden)!.SetValue(dead, 0);
            var monsters = PlayerBotDriver.PlanMonsters(player, [camp, outside, dead], zone, ConColor.YELLOW,
                new Dictionary<string, long>(), 0);
            Assert.That(monsters.Select(m => (m.Npc, m.At, m.Con, m.Huntable, m.Avoided)),
                Is.EqualTo(new[] { (camp, new Vector3(1_000, 1_000, 0), ConColor.YELLOW, true, false) }));
        }
        finally
        {
            calculators[(int)eProperty.LivingEffectiveLevel] = previous;
        }
    }

    // #167 D4, step 11b: after a release, while the immunity lasts, no self-buff and no pet: the bot plans first (a buff
    // cast returned the think before the plan, and a cast stands the bot).
    [Test]
    public void No_buff_or_pet_while_immune_after_a_release()
    {
        ImmunePlayer player = LiveImmunePlayer();
        var state = new PlayerBotState(Player(null)) { AfterRelease = true, NoPetSpellLevel = 99 };
        foreach (string cast in new[] { "SelfBuff", "SummonPet" })
            Assert.That(typeof(PlayerBotDriver).GetMethod(cast, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [state, player, 20_000L]), Is.False, cast);
        Assert.That((state.NextSelfBuffTick, state.NextSummonTick), Is.EqualTo((0L, 0L)), "not even a scan");
    }

    // The capped clock: it starts at the first search that passed a monster over for the cap and found nothing, keeps
    // running while that lasts, and stops at a search that found a target or nothing at all to pass over.
    [TestCase(0L, false, true, 5_000L, 5_000L)] [TestCase(2_000L, false, true, 5_000L, 2_000L)]
    [TestCase(2_000L, true, true, 5_000L, 0L)] [TestCase(2_000L, false, false, 5_000L, 0L)]
    public void The_capped_clock_runs_while_searches_pass_monsters_over_and_find_none(long since, bool found,
        bool passedOver, long now, long next) =>
        Assert.That(PlayerBotDriver.CappedClock(since, found, passedOver, now), Is.EqualTo(next));

    private static (string Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided) Monster(string name, float x,
        ConColor con, bool avoided = false) =>
        (name, new Vector3(x, 0, 0), con, con is > ConColor.GREY and <= ConColor.YELLOW, avoided);

    // A camp is the monsters around one the bot would hunt, within RoamRadius: it fits when that one is under the cap
    // and so are most of them (their median con, as a GameBot's camp is its levels' median, AWBC:3312-3330, :3386).
    [Test]
    public void A_relocation_takes_the_nearest_camps_its_cap_fits()
    {
        var monsters = new[]
        {
            Monster("yellow", 1_000, ConColor.YELLOW),                                       // over the cap
            Monster("blue among yellows", 3_000, ConColor.BLUE), Monster("y1", 3_100, ConColor.YELLOW),
            Monster("y2", 3_200, ConColor.YELLOW),                                           // median yellow
            Monster("blue camp", 10_000, ConColor.BLUE), Monster("g1", 10_100, ConColor.GREEN),
            Monster("g2", 10_200, ConColor.GREEN), Monster("y3", 10_300, ConColor.YELLOW),   // median blue
            Monster("green", 20_000, ConColor.GREEN), Monster("grey1", 20_100, ConColor.GREY),
            Monster("grey2", 20_200, ConColor.GREY),                                         // greys are no camp
        };
        (string[] spots, ConColor? fallback, bool stale) = PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero,
            ConColor.BLUE, ConColor.YELLOW, null, new HashSet<string>(), 5);
        Assert.That(spots, Is.EqualTo(new[] { "blue camp", "g1", "g2", "green" }));
        Assert.That(fallback, Is.Null);
        Assert.That(stale, Is.False);
    }

    // A camp a bot has no corridor to is checked once: the next try takes the next nearest monsters, instead of the same
    // five for the rest of the run (review of 76e41cf).
    [Test]
    public void RelocationSkipsMonstersFoundUnreachable()
    {
        int[] distances = { 700, 100, 500, 300, 900, 200, 1100, 400 };
        var monsters = distances.Select(d => Monster($"m{d}", d, ConColor.GREEN)).ToArray();
        var rejected = new HashSet<string>();
        string[] first = PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, null,
            rejected, 5).Spots;
        Assert.That(first, Is.EqualTo(new[] { "m100", "m200", "m300", "m400", "m500" }));
        rejected.UnionWith(first);
        Assert.That(PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, null, rejected,
            5).Spots, Is.EqualTo(new[] { "m700", "m900", "m1100" }));
    }

    // No camp fits: a GameBot takes the safest camp there is, not its failed one while another exists, and hunts it at
    // that camp's con (SelectSafestAvailableAfterDeath, AutonomousBotDecisionEngine.cs:293-313; AWBC:3422-3452). A camp
    // over the natural cap is none (AWBC:3426).
    [Test]
    public void When_no_camp_fits_the_safest_other_camp_takes_the_bot_at_its_con()
    {
        var monsters = new[]
        {
            Monster("a1", 0, ConColor.BLUE), Monster("a2", 100, ConColor.BLUE), Monster("a3", 200, ConColor.BLUE),
            Monster("b1", 10_000, ConColor.YELLOW), Monster("b2", 10_100, ConColor.YELLOW), Monster("b3", 10_200, ConColor.BLUE),
            Monster("c1", 20_000, ConColor.YELLOW), Monster("c2", 20_100, ConColor.BLUE), Monster("c3", 20_200, ConColor.BLUE),
            Monster("e", 30_000, ConColor.BLUE), Monster("o1", 30_100, ConColor.ORANGE), Monster("o2", 30_200, ConColor.ORANGE),
        };
        (string[] spots, ConColor? fallback, bool stale) = PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero,
            ConColor.GREEN, ConColor.YELLOW, new[] { new Vector3(50, 0, 0) }, new HashSet<string>(), 5);
        Assert.That(spots, Is.EqualTo(new[] { "c1", "c2", "c3" }), "camp a failed; b is yellow; e is orange");
        Assert.That(fallback, Is.EqualTo(ConColor.BLUE));
        Assert.That(stale, Is.False);
        Assert.That(PlayerBotDriver.PlanRelocation(monsters[..3], Vector3.Zero, ConColor.GREEN, ConColor.YELLOW,
            new[] { new Vector3(50, 0, 0) }, new HashSet<string>(), 5).Spots, Is.EqualTo(new[] { "a1", "a2", "a3" }),
            "the failed camp when it is the only one");
        Assert.That(PlayerBotDriver.PlanRelocation(monsters[9..], Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, null,
            new HashSet<string>(), 5), Is.EqualTo((Array.Empty<string>(), (ConColor?)null, false)));
        // Every failed camp of the last 15 minutes stays out, not only the last: a bot that died at a, then at c, goes
        // to b, nearest first, not back to a (a GameBot's failed camps, AWBC:3246, :3803-3804).
        Assert.That(PlayerBotDriver.PlanRelocation(monsters, new Vector3(20_000, 0, 0), ConColor.GREEN, ConColor.YELLOW,
            new[] { new Vector3(50, 0, 0), new Vector3(20_050, 0, 0) }, new HashSet<string>(), 5),
            Is.EqualTo((new[] { "b3", "b2", "b1" }, (ConColor?)ConColor.YELLOW, false)));
    }

    // Where the bot died, its failed camp, is remembered for a GameBot's failure memory, 15 minutes, every one (its
    // _recentFailedSoloCamps, AWBC:3246, :3803-3804), not forgotten at the next relocation; one whose time is up goes
    // at the next death. Each is its region's (#167 F4): kept across a region change, and only its own region's count.
    [Test]
    public void Failed_camps_are_remembered_for_15_minutes()
    {
        var failed = new List<(ushort Region, Vector3 At, long Until)>();
        PlayerBotDriver.RememberFailedCamp(failed, 51, new Vector3(1, 0, 0), 1_000);
        PlayerBotDriver.RememberFailedCamp(failed, 51, new Vector3(2, 0, 0), 600_000);
        PlayerBotDriver.RememberFailedCamp(failed, 100, new Vector3(5, 0, 0), 600_000);
        Assert.That(PlayerBotDriver.FailedCamps(failed, 51, 900_999),
            Is.EqualTo(new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0) }));
        Assert.That(PlayerBotDriver.FailedCamps(failed, 51, 901_000), Is.EqualTo(new[] { new Vector3(2, 0, 0) }));
        Assert.That(PlayerBotDriver.FailedCamps(failed, 100, 901_000), Is.EqualTo(new[] { new Vector3(5, 0, 0) }),
            "another region's camp, kept");
        PlayerBotDriver.RememberFailedCamp(failed, 51, new Vector3(3, 0, 0), 901_000);
        Assert.That(failed.Select(f => f.At), Is.EqualTo(new[] { new Vector3(2, 0, 0), new Vector3(5, 0, 0),
            new Vector3(3, 0, 0) }), "the expired camp went");
    }

    // A region change (a release to a bind in another region, a re-entry) starts the hunt afresh there but keeps the
    // failed camps (#167 F4): a bot that released to another region and came back planned into the camp it died at. The
    // review of 77ba437: with the old Clear put back, no test failed.
    [Test]
    public void A_region_change_starts_the_hunt_afresh_but_keeps_the_failed_camps()
    {
        var state = new PlayerBotState(Player(null));
        PlayerBotDriver.RememberFailedCamp(state.FailedCamps, 51, new Vector3(1, 0, 0), 1_000);
        state.Target = Player(null);
        state.RoamDestination = new Vector3(1, 2, 3);
        (state.RoamingSince, state.CappedSince, state.FallbackCap, state.HuntAvoided) = (5, 6, ConColor.BLUE, true);
        PlayerBotDriver.ResetOnRegionChange(state, 100);
        Assert.That((state.LastRegion, state.Target, state.RoamDestination, state.RoamingSince, state.CappedSince,
            state.FallbackCap, state.HuntAvoided), Is.EqualTo(((ushort)100, (GameLiving)null, (Vector3?)null, 0L, 0L,
            (ConColor?)null, false)));
        Assert.That(state.FailedCamps.Select(f => (f.Region, f.At)), Is.EqualTo(new[] { ((ushort)51, new Vector3(1, 0, 0)) }));
    }

    // A capped bot that cannot move its leash (a lever spot bot, out of its anchor region or off the mesh, or no camp
    // in its zone) takes the safest con it passed over where it is, as a GameBot takes the safest camp there is rather
    // than plan forever (AutonomousBotDecisionEngine.cs:287-313); run 1's bots stood idle after two deaths without it.
    // Not for an outgrown bot, nor one that passed over only names it avoids (they expire). #167 D6: nor a con at or
    // over the lowest that killed the bot in the last 5 minutes (LowestKillerCon), a YELLOW or harder one: a BLUE or
    // GREEN killer leaves the fallback alone (st-deathsD's fix round).
    [TestCase("capped", ConColor.BLUE, null, ConColor.BLUE)] [TestCase("capped", null, null, null)]
    [TestCase("outgrown", ConColor.BLUE, null, null)] [TestCase(null, ConColor.BLUE, null, null)]
    [TestCase("capped", ConColor.BLUE, ConColor.YELLOW, ConColor.BLUE)] [TestCase("capped", ConColor.YELLOW, ConColor.YELLOW, null)]
    [TestCase("capped", ConColor.YELLOW, ConColor.ORANGE, ConColor.YELLOW)]
    [TestCase("capped", ConColor.BLUE, ConColor.BLUE, ConColor.BLUE)] [TestCase("capped", ConColor.YELLOW, ConColor.BLUE, ConColor.YELLOW)]
    [TestCase("capped", ConColor.GREEN, ConColor.GREEN, ConColor.GREEN)]
    public void A_capped_bot_that_cannot_move_takes_the_safest_con_it_passed_over(string reason, ConColor? safest,
        ConColor? lowestKillerCon, ConColor? cap) =>
        Assert.That(PlayerBotDriver.FallbackInPlace(reason, safest, lowestKillerCon), Is.EqualTo(cap));

    // Its wiring (#167 D6): FallBackInPlace reads the killers' cons the bot keeps, for 5 minutes.
    [Test]
    public void A_capped_bot_takes_no_fallback_con_that_killed_it_where_it_is()
    {
        MethodInfo fallBack = typeof(PlayerBotDriver).GetMethod("FallBackInPlace", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = new PlayerBotState(Player(null)) { SafestPassedOver = ConColor.YELLOW, CappedSince = 5_000 };
        PlayerBotDriver.RememberKillerCon(state.KillerCons, ConColor.YELLOW, 1_000);
        fallBack.Invoke(null, [state, PlayerBotDriver.Capped, 300_999L]);
        Assert.That((state.FallbackCap, state.CappedSince), Is.EqualTo(((ConColor?)null, 5_000L)));
        fallBack.Invoke(null, [state, PlayerBotDriver.Capped, 301_000L]);              // its 5 minutes are up
        Assert.That((state.FallbackCap, state.CappedSince), Is.EqualTo(((ConColor?)ConColor.YELLOW, 0L)));
    }

    // A capped bot stands while it waits to move its leash, unless it walks to its anchor; one that cannot move it now
    // (FallbackInPlace) roams as before. So does one owed a relocation after a death (#167 F4), from its
    // release. Neither holds past 60 s: with no camp planned by then it hunts where it is, under its cap (FallbackInPlace
    // has no cap for a death's relocation, so nothing else would end that hold).
    [TestCase(true, 5_000L, 0L, false, 5_000L, true)] [TestCase(true, 0L, 0L, false, 5_000L, false)]
    [TestCase(true, 5_000L, 0L, true, 5_000L, false)] [TestCase(false, 5_000L, 0L, false, 5_000L, false)]
    [TestCase(true, 5_000L, 0L, false, 64_999L, true)] [TestCase(true, 5_000L, 0L, false, 65_000L, false)]
    [TestCase(true, 0L, 10_000L, false, 69_999L, true)] [TestCase(true, 0L, 10_000L, false, 70_000L, false)]
    [TestCase(true, 0L, 10_000L, true, 20_000L, false)] [TestCase(false, 0L, 10_000L, false, 20_000L, false)]
    [TestCase(true, 5_000L, 10_000L, false, 69_999L, true)]
    public void A_capped_bot_holds_only_while_it_can_relocate(bool canRelocate, long cappedSince, long replanSince,
        bool walkingToAnchor, long now, bool holds) =>
        Assert.That(PlayerBotDriver.HoldsForRelocation(canRelocate, cappedSince, replanSince, walkingToAnchor, now),
            Is.EqualTo(holds));

    // PLAYER_BOT_RELOCATE's fields stay as they were (runs/legcheck.py reads bot= and to or none); its reason and, for a
    // move, the fallback camp's con (none: the death cap) come last; after a death, the milliseconds since the release
    // (#167 C1: sinceRelease=), barred=old when the plan bars the anchor it had (#167 D9), and capKill= (#167 D6).
    [Test]
    public void A_relocation_line_gives_its_reason_last()
    {
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, new Vector3(572911.4f, 457576.6f, 2000), new Vector3(579794,
                456080, 2216.6f), 7040, "silver oaken fellwood", 33, 51, PlayerBotDriver.Capped, ConColor.BLUE, 0, false, null),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 from=(572911,457577) to=(579794,456080,2217) distance=7040 " +
                       "monster=\"silver oaken fellwood\" monsterLevel=33 zone=51 reason=capped fallback=BLUE"));
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, Vector3.Zero, Vector3.One, 1, "m", 30, 51,
            PlayerBotDriver.Outgrown, null, 0, false, null), Does.EndWith(" zone=51 reason=outgrown fallback=-"));
        Assert.That(PlayerBotDriver.RelocateNoneLine("Pbotaaaaz", 34, 3, 51, PlayerBotDriver.Capped, 0, false, null),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 none checked=3 zone=51 reason=capped"));
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, Vector3.Zero, Vector3.One, 1, "m", 30, 51,
            PlayerBotDriver.Died, null, 10_250, false, null), Does.EndWith(" zone=51 reason=died fallback=- sinceRelease=10250"));
        Assert.That(PlayerBotDriver.RelocateNoneLine("Pbotaaaaz", 34, 0, 51, PlayerBotDriver.Died, 10_250, false, null),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 none checked=0 zone=51 reason=died sinceRelease=10250"));
        // #167 D9: barred=old, last, when the plan bars the anchor the bot had (BarsOldAnchor)
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, Vector3.Zero, Vector3.One, 1, "m", 30, 51,
            PlayerBotDriver.Died, null, 10_250, true, null), Does.EndWith(" reason=died fallback=- sinceRelease=10250 barred=old"));
        Assert.That(PlayerBotDriver.RelocateNoneLine("Pbotaaaaz", 34, 2, 51, PlayerBotDriver.BrokeOff, 10_250, true, null),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 none checked=2 zone=51 reason=brokeoff barred=old"));
        // #167 D6: then capKill=, the lowest con that killed the bot in the last 15 minutes, while it keeps one
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, Vector3.Zero, Vector3.One, 1, "m", 30, 51,
                PlayerBotDriver.Died, ConColor.BLUE, 10_250, true, ConColor.YELLOW),
            Does.EndWith(" reason=died fallback=BLUE sinceRelease=10250 barred=old capKill=YELLOW"));
        Assert.That(PlayerBotDriver.RelocateNoneLine("Pbotaaaaz", 34, 3, 51, PlayerBotDriver.Capped, 0, false, ConColor.BLUE),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 none checked=3 zone=51 reason=capped capKill=BLUE"));
        // #167 E W1: then recent=1 on a plan after a death, and near=1 on a camp its near pass took
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, Vector3.Zero, Vector3.One, 1, "m", 30, 51,
                PlayerBotDriver.Died, ConColor.BLUE, 10_250, false, ConColor.YELLOW, recent: true, near: true),
            Does.EndWith(" reason=died fallback=BLUE sinceRelease=10250 capKill=YELLOW recent=1 near=1"));
        Assert.That(PlayerBotDriver.RelocateNoneLine("Pbotaaaaz", 34, 0, 51, PlayerBotDriver.BrokeOff, 0, false, null,
            recent: true), Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 none checked=0 zone=51 reason=brokeoff recent=1"));
    }

    // --- the killer memory (DEATHS.md A3) ---

    // A death's names are remembered for a GameBot's failure memory, 15 minutes (AutonomousDeathRecoveryPolicy.cs:10,
    // AWBC:3804, :3811), by any case of the name (its dictionaries are OrdinalIgnoreCase, AWBC:147-148); an expired one is
    // dropped at the next.
    [Test]
    public void A_remembered_name_is_avoided_for_15_minutes()
    {
        var avoid = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        PlayerBotDriver.Remember(avoid, "rainbow sprite", 1_000);
        PlayerBotDriver.Remember(avoid, null, 1_000);                                   // no killer named: nothing
        Assert.That(PlayerBotDriver.IsAvoided(avoid, "rainbow sprite", 1_001), Is.True);
        Assert.That(PlayerBotDriver.IsAvoided(avoid, "Rainbow Sprite", 900_999), Is.True);
        Assert.That(PlayerBotDriver.IsAvoided(avoid, "rainbow sprite", 901_000), Is.False);
        Assert.That(PlayerBotDriver.IsAvoided(avoid, "apple fly", 1_001), Is.False);
        Assert.That(PlayerBotDriver.IsAvoided(avoid, null, 1_001), Is.False);
        Assert.That(PlayerBotDriver.AvoidedCount(avoid, 1_001), Is.EqualTo(1));
        PlayerBotDriver.Remember(avoid, "apple fly", 901_000);
        Assert.That(avoid.Keys, Is.EqualTo(new[] { "apple fly" }), "the expired name went");
        Assert.That(PlayerBotDriver.AvoidedCount(avoid, 1_801_000), Is.Zero);
    }

    // A camp full of avoided names fits no cap: its fresh monsters under the cap must be most of it, and the spot itself
    // fresh. With no camp that fits, the fallback prefers a fresh camp, as a GameBot's PreferFreshTargets
    // (AutonomousDeathRecoveryPolicy.cs:26-38, AWBC:3424-3427); with none left it takes a stale one (Stale), whose names
    // the bot then hunts, as a GameBot hunts its camp's monster (AWBC:3087-3088): else it stood there passing them over
    // and moved every 30 s until they expired.
    [Test]
    public void A_relocation_counts_a_camp_of_avoided_names_as_unfit()
    {
        var monsters = new[]
        {
            Monster("s1", 1_000, ConColor.GREEN, avoided: true), Monster("s2", 1_100, ConColor.GREEN, avoided: true),
            Monster("f1", 1_200, ConColor.GREEN),                                     // fresh, but 1 of 3
            Monster("g1", 10_000, ConColor.GREEN), Monster("g2", 10_100, ConColor.GREEN),
            Monster("g3", 10_200, ConColor.GREEN, avoided: true),                     // fresh: 2 of 3
        };
        Assert.That(PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, null,
            new HashSet<string>(), 5).Spots, Is.EqualTo(new[] { "g1", "g2" }), "g3 is avoided, the camp at 1,000 stale");
        var fallback = new[]
        {
            Monster("s1", 1_000, ConColor.BLUE, avoided: true), Monster("s2", 1_100, ConColor.BLUE, avoided: true),
            Monster("y1", 10_000, ConColor.YELLOW), Monster("y2", 10_100, ConColor.YELLOW),
        };
        Assert.That(PlayerBotDriver.PlanRelocation(fallback, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, null,
            new HashSet<string>(), 5), Is.EqualTo((new[] { "y1", "y2" }, (ConColor?)ConColor.YELLOW, false)),
            "the fresh yellow camp before the safer blue one the bot died to");
        Assert.That(PlayerBotDriver.PlanRelocation(fallback[..2], Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, null,
            new HashSet<string>(), 5), Is.EqualTo((new[] { "s1", "s2" }, (ConColor?)ConColor.BLUE, true)),
            "a stale camp when no fresh one is left");
    }

    // #167 E W1: a plan after a death first looks near the bot: within 10,000 of where it stands (its bind after a
    // release), a fit camp, else the safest no harder than BLUE, never one it failed at; only with none the plan as
    // before, from the death spot. st-deathsD2/D2b: after-death plans of 10k+ died first 27 of 72, under 10k 13 of 148.
    [Test]
    public void A_replan_after_a_death_takes_a_near_camp_before_a_far_fit()
    {
        var monsters = new[]
        {
            Monster("b1", 3_000, ConColor.BLUE), Monster("b2", 3_100, ConColor.BLUE), Monster("b3", 3_200, ConColor.BLUE),
            Monster("g1", 30_000, ConColor.GREEN), Monster("g2", 30_100, ConColor.GREEN), Monster("g3", 30_200, ConColor.GREEN),
        };
        Vector3 bind = Vector3.Zero, deathSpot = new(20_000, 0, 0);
        Vector3[] failed = [deathSpot];
        Assert.That(PlayerBotDriver.PlanCampChoice(monsters, monsters, bind, deathSpot, ConColor.GREEN, ConColor.YELLOW,
                failed, new HashSet<string>(), 5, null, afterDeath: true),
            Is.EqualTo((new[] { "b1", "b2", "b3" }, (ConColor?)ConColor.BLUE, false, true)), "the blue camp 3,000 away");
        Assert.That(PlayerBotDriver.PlanCampChoice(monsters, monsters, bind, deathSpot, ConColor.GREEN, ConColor.YELLOW,
                failed, new HashSet<string>(), 5, null, afterDeath: false),
            Is.EqualTo((new[] { "g1", "g2", "g3" }, (ConColor?)null, false, false)), "no recent death: as before");
        Assert.That(PlayerBotDriver.PlanCampChoice(monsters, monsters, bind, deathSpot, ConColor.GREEN, ConColor.YELLOW,
                new[] { deathSpot, new Vector3(3_100, 0, 0) }, new HashSet<string>(), 5, null, afterDeath: true),
            Is.EqualTo((new[] { "g1", "g2", "g3" }, (ConColor?)null, false, false)), "never a near camp it failed at");
        Assert.That(PlayerBotDriver.PlanCampChoice(monsters, monsters, bind, deathSpot, ConColor.GREEN, ConColor.YELLOW,
                failed, new HashSet<string>(), 5, null, afterDeath: true, tryNear: false),
            Is.EqualTo((new[] { "g1", "g2", "g3" }, (ConColor?)null, false, false)), "no corridor to the near camps");
        var yellowNear = new[]
        {
            Monster("y1", 3_000, ConColor.YELLOW), Monster("y2", 3_100, ConColor.YELLOW), Monster("y3", 3_200, ConColor.YELLOW),
            Monster("g1", 30_000, ConColor.GREEN), Monster("g2", 30_100, ConColor.GREEN), Monster("g3", 30_200, ConColor.GREEN),
        };
        Assert.That(PlayerBotDriver.PlanCampChoice(yellowNear, yellowNear, bind, deathSpot, ConColor.GREEN, ConColor.YELLOW,
                failed, new HashSet<string>(), 5, null, afterDeath: true),
            Is.EqualTo((new[] { "g1", "g2", "g3" }, (ConColor?)null, false, false)), "no near camp harder than blue");
        var avoidedNear = new[]                                 // the killer's names: CampCon counts them PURPLE, stale
        {
            Monster("b1", 3_000, ConColor.BLUE, avoided: true), Monster("b2", 3_100, ConColor.BLUE, avoided: true),
            Monster("b3", 3_200, ConColor.BLUE, avoided: true),
            Monster("g1", 30_000, ConColor.GREEN), Monster("g2", 30_100, ConColor.GREEN), Monster("g3", 30_200, ConColor.GREEN),
        };
        Assert.That(PlayerBotDriver.PlanCampChoice(avoidedNear, avoidedNear, bind, deathSpot, ConColor.GREEN,
                ConColor.YELLOW, failed, new HashSet<string>(), 5, null, afterDeath: true),
            Is.EqualTo((new[] { "g1", "g2", "g3" }, (ConColor?)null, false, false)),
            "a stale near camp (names it avoids) is no near pick: the far fit, not back to its killers");
    }

    // #167 E W1: a plan after a death is a died, brokeoff or capped one within 15 minutes of the last death (a GameBot's
    // failure memory); an outgrown one is left alone.
    [TestCase("died", 10_000L, true)] [TestCase("brokeoff", 899_999L, true)] [TestCase("capped", 0L, true)]
    [TestCase("outgrown", 10_000L, false)] [TestCase("brokeoff", 900_000L, false)] [TestCase("died", -1L, false)]
    public void A_plan_after_a_death_is_a_died_brokeoff_or_capped_one_within_15_minutes(string reason, long sinceDeathMs,
        bool afterDeath) =>
        Assert.That(PlayerBotDriver.AfterDeathPlan(reason, sinceDeathMs), Is.EqualTo(afterDeath));

    // --- the killers' cons (#167 D6) ---

    // HandleDeath stepped the cap down and cleared FallbackCap, then a fallback plan set FallbackCap to YELLOW again and
    // Cap lifted the cap back to it: st-deathsC's release first fights at fallback=YELLOW camps were fatal 32 of 42 (fit
    // camps 4 of 76). The cap after deaths is EncounterMaximum's, under the lowest con that killed the bot in the last 5
    // minutes, never under GREEN. st-deathsD's fix round: a YELLOW or harder killer only; a BLUE or GREEN one leaves the
    // cap and the fallback alone.
    [TestCase(ConColor.GREEN, ConColor.YELLOW, ConColor.YELLOW, ConColor.BLUE)]
    [TestCase(ConColor.BLUE, ConColor.YELLOW, ConColor.ORANGE, ConColor.YELLOW)]
    [TestCase(ConColor.GREEN, null, ConColor.YELLOW, ConColor.GREEN)]
    [TestCase(ConColor.GREEN, ConColor.YELLOW, null, ConColor.YELLOW)]               // as before
    [TestCase(ConColor.GREEN, ConColor.BLUE, ConColor.BLUE, ConColor.BLUE)]          // a BLUE killer: as before
    [TestCase(ConColor.BLUE, null, ConColor.BLUE, ConColor.BLUE)]
    [TestCase(ConColor.GREEN, ConColor.YELLOW, ConColor.GREEN, ConColor.YELLOW)]     // a GREEN killer: as before
    [TestCase(ConColor.GREEN, null, ConColor.GREEN, ConColor.GREEN)]
    [TestCase(ConColor.BLUE, null, null, ConColor.BLUE)]
    public void The_cap_after_deaths_stays_under_the_lowest_con_that_killed_the_bot(ConColor deathCap, ConColor? fallback,
        ConColor? lowestKillerCon, ConColor cap) =>
        Assert.That(PlayerBotDriver.CapAfterDeaths(deathCap, fallback, lowestKillerCon), Is.EqualTo(cap));

    // A killer's con is kept KillerConMemoryMilliseconds (5 minutes), each death's, for a YELLOW or harder killer only; the
    // lowest one kept counts, and one whose time is up goes. st-deathsD's fix round: after one death the cap is BLUE
    // already, so a BLUE or GREEN killer changed no cap and only left the fallback no camp, and a YELLOW one's 15 minutes
    // left a bot among YELLOW camps none: 10 bots logged 75 capped-none plans (BLUE killers 31, YELLOW 44), and 16 lost
    // 42 kills against st-hearth11. A grey killer is none.
    [Test]
    public void A_yellow_or_harder_killers_con_is_kept_for_5_minutes_and_the_lowest_counts()
    {
        var cons = new List<(ConColor Con, long Until)>();
        Assert.That(PlayerBotDriver.LowestKillerCon(cons, 0), Is.Null);
        PlayerBotDriver.RememberKillerCon(cons, ConColor.GREY, 0);
        PlayerBotDriver.RememberKillerCon(cons, ConColor.GREEN, 0);
        PlayerBotDriver.RememberKillerCon(cons, ConColor.ORANGE, 0);
        PlayerBotDriver.RememberKillerCon(cons, ConColor.BLUE, 60_000);
        PlayerBotDriver.RememberKillerCon(cons, ConColor.YELLOW, 120_000);
        Assert.That(cons.Select(c => c.Con), Is.EqualTo(new[] { ConColor.ORANGE, ConColor.YELLOW }), "no GREY, GREEN or BLUE");
        Assert.That(PlayerBotDriver.LowestKillerCon(cons, 120_001), Is.EqualTo(ConColor.YELLOW));
        Assert.That(PlayerBotDriver.LowestKillerCon(cons, 419_999), Is.EqualTo(ConColor.YELLOW));
        Assert.That(PlayerBotDriver.LowestKillerCon(cons, 420_000), Is.Null);
        PlayerBotDriver.RememberKillerCon(cons, ConColor.RED, 420_000);
        Assert.That(cons.Select(c => c.Con), Is.EqualTo(new[] { ConColor.RED }), "the expired ones went");
        Assert.That((PlayerBotDriver.LowestKillerCon(cons, 719_999), PlayerBotDriver.LowestKillerCon(cons, 720_000)),
            Is.EqualTo(((ConColor?)ConColor.RED, (ConColor?)null)));
    }

    // st-deathsD's fix round: a bot whose camps all con YELLOW, after a death to a YELLOW (its cap BLUE), planned none for
    // the killer's 15 minutes (the fallback took no camp at or over the killer's con) and retried every 30 s. It still
    // plans none for the 5 minutes, then the plan e028a26 made (no killer's con): the YELLOW fallback camp.
    [Test]
    public void Among_yellow_camps_after_a_yellow_death_the_old_fallback_comes_back_after_5_minutes()
    {
        var monsters = new[] { Monster("y1", 1_000, ConColor.YELLOW), Monster("y2", 1_100, ConColor.YELLOW) };
        var state = new PlayerBotState(Player(null)) { DeathSafetySteps = 1 };
        PlayerBotDriver.RememberKillerCon(state.KillerCons, ConColor.YELLOW, 1_000);
        (string[], ConColor?, bool) Plan(long now)
        {
            (ConColor fit, ConColor? killerCon) = PlayerBotDriver.PlanCaps(state, now);   // as RelocateIfOutgrown
            return PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero, fit, ConColor.YELLOW, null, new HashSet<string>(),
                5, killerCon);
        }
        (string[], ConColor?, bool) atE028a26 = PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero, ConColor.BLUE,
            ConColor.YELLOW, null, new HashSet<string>(), 5);
        Assert.That(atE028a26, Is.EqualTo((new[] { "y1", "y2" }, (ConColor?)ConColor.YELLOW, false)));
        Assert.That(Plan(2_000), Is.EqualTo((Array.Empty<string>(), (ConColor?)null, false)), "none for the 5 minutes");
        Assert.That(Plan(300_999), Is.EqualTo((Array.Empty<string>(), (ConColor?)null, false)));
        Assert.That(Plan(301_000), Is.EqualTo(atE028a26), "then e028a26's fallback");
    }

    private sealed class LevelOnly : DOL.GS.PropertyCalc.PropertyCalculator
    {
        public override int CalcValue(GameLiving living, eProperty property) => living.Level;
    }

    // HandleDeath keeps the con of the killer the reaper names (the killer's name already, DEATHS.md A3), against the
    // bot's level then: a level 20 killed by a level 20 (YELLOW), then by a level 17 (BLUE: none kept, st-deathsD's fix
    // round).
    [Test]
    public void A_death_keeps_its_killers_con()
    {
        MethodInfo handleDeath = typeof(PlayerBotDriver).GetMethod("HandleDeath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var calculators = (DOL.GS.PropertyCalc.IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        DOL.GS.PropertyCalc.IPropertyCalculator previous = calculators[(int)eProperty.LivingEffectiveLevel];
        calculators[(int)eProperty.LivingEffectiveLevel] = new LevelOnly();
        try
        {
            FieldInfo level = typeof(GameObject).GetField("m_level", Hidden)!;
            GamePlayer bot = Player(null);
            level.SetValue(bot, (byte)20);
            GameNPC KilledBy(byte at)
            {
                var npc = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
                level.SetValue(npc, at);
                return npc;
            }
            var state = new PlayerBotState(bot) { Killer = KilledBy(20) };
            handleDeath.Invoke(null, [state, bot, 54_000L]);
            Assert.That(PlayerBotDriver.LowestKillerCon(state.KillerCons, 54_001), Is.EqualTo(ConColor.YELLOW));
            (state.DeathRecorded, state.Killer, state.KillerAvoided) = (false, KilledBy(17), false);
            handleDeath.Invoke(null, [state, bot, 120_000L]);
            Assert.That(state.KillerCons.Select(c => c.Con), Is.EqualTo(new[] { ConColor.YELLOW }));
            Assert.That(state.Deaths, Is.EqualTo(2), "both deaths handled");
        }
        finally
        {
            calculators[(int)eProperty.LivingEffectiveLevel] = previous;
        }
    }

    // Cap (FindWithin's and SettleTarget's) reads them: one death (BLUE) to a YELLOW at a fallback=YELLOW camp hunts
    // BLUE, and YELLOW again once the 5 minutes are up.
    [Test]
    public void The_pull_cap_reads_the_killers_cons()
    {
        MethodInfo cap = typeof(PlayerBotDriver).GetMethod("Cap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = new PlayerBotState(Player(null)) { DeathSafetySteps = 1, FallbackCap = ConColor.YELLOW };
        PlayerBotDriver.RememberKillerCon(state.KillerCons, ConColor.YELLOW, 1_000);
        Assert.That(cap.Invoke(null, [state, 300_999L]), Is.EqualTo(ConColor.BLUE));
        Assert.That(cap.Invoke(null, [state, 301_000L]), Is.EqualTo(ConColor.YELLOW));
    }

    // The plan's caps (RelocateIfOutgrown): its fit pass under the death cap and the killers' cons, as the pulls are
    // (else it plans a camp the bot then passes over, capped), and the lowest killer's con for its fallback: after deaths
    // to an ORANGE and a YELLOW (cap GREEN), the YELLOW. A BLUE killer is none kept (st-deathsD's fix round).
    [Test]
    public void The_plan_caps_read_the_killers_cons()
    {
        var state = new PlayerBotState(Player(null)) { DeathSafetySteps = 1, FallbackCap = ConColor.YELLOW };
        Assert.That(PlayerBotDriver.PlanCaps(state, 2_000), Is.EqualTo((ConColor.BLUE, (ConColor?)null)));
        PlayerBotDriver.RememberKillerCon(state.KillerCons, ConColor.BLUE, 1_000);
        Assert.That(PlayerBotDriver.PlanCaps(state, 2_000), Is.EqualTo((ConColor.BLUE, (ConColor?)null)));
        state.DeathSafetySteps = 2;
        PlayerBotDriver.RememberKillerCon(state.KillerCons, ConColor.ORANGE, 1_000);
        PlayerBotDriver.RememberKillerCon(state.KillerCons, ConColor.YELLOW, 1_500);
        Assert.That(PlayerBotDriver.PlanCaps(state, 2_000), Is.EqualTo((ConColor.GREEN, (ConColor?)ConColor.YELLOW)));
    }

    // #167 E (CY4): Bards, Rangers, Healers, Mercenaries, Nightshades, Blademasters and Mentalists take no YELLOW solo
    // (their YELLOW pulls were fatal 20% or more on both pairs of legs); every other class keeps YELLOW.
    [TestCase(eCharacterClass.Bard, 1)] [TestCase(eCharacterClass.Ranger, 1)] [TestCase(eCharacterClass.Healer, 1)]
    [TestCase(eCharacterClass.Mercenary, 1)] [TestCase(eCharacterClass.Nightshade, 1)] [TestCase(eCharacterClass.Blademaster, 1)]
    [TestCase(eCharacterClass.Mentalist, 1)] [TestCase(eCharacterClass.Warden, 0)] [TestCase(eCharacterClass.Savage, 0)]
    [TestCase(eCharacterClass.Infiltrator, 0)] [TestCase(eCharacterClass.Eldritch, 0)] [TestCase(eCharacterClass.Scout, 0)]
    [TestCase(eCharacterClass.Warrior, 0)]
    public void Some_classes_take_no_yellow_solo(eCharacterClass cls, int steps) =>
        Assert.That(PlayerBotDriver.SoloSteps((int)cls), Is.EqualTo(steps));

    // A floor under the death steps: a listed class starts at BLUE, its first death leaves it there and its second
    // takes it to GREEN; another class as before.
    [TestCase(0, 1, ConColor.BLUE)] [TestCase(1, 1, ConColor.BLUE)] [TestCase(2, 1, ConColor.GREEN)]
    [TestCase(0, 0, ConColor.YELLOW)] [TestCase(1, 0, ConColor.BLUE)]
    public void The_solo_steps_are_a_floor_under_the_death_steps(int deathSteps, int soloSteps, ConColor cap) =>
        Assert.That(PlayerBotDriver.MaximumTargetCon(1, PlayerBotDriver.CapSteps(deathSteps, soloSteps)), Is.EqualTo(cap));

    // The pull cap and the plan's fit pass both read them (else the plan sends the bot to a YELLOW camp it then passes
    // over, and the capped clock moves it on); a YELLOW fallback camp still lifts the pull cap, as after one death.
    [Test]
    public void The_pull_cap_and_the_plan_read_the_solo_steps()
    {
        MethodInfo cap = typeof(PlayerBotDriver).GetMethod("Cap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = new PlayerBotState(Player(null)) { SoloSteps = 1 };
        Assert.That(cap.Invoke(null, [state, 1_000L]), Is.EqualTo(ConColor.BLUE));
        Assert.That(PlayerBotDriver.PlanCaps(state, 1_000), Is.EqualTo((ConColor.BLUE, (ConColor?)null)));
        state.FallbackCap = ConColor.YELLOW;
        Assert.That(cap.Invoke(null, [state, 1_000L]), Is.EqualTo(ConColor.YELLOW));
        state.SoloSteps = 0;
        state.FallbackCap = null;
        Assert.That(cap.Invoke(null, [state, 1_000L]), Is.EqualTo(ConColor.YELLOW));
    }

    // The review of CY4: a bot's state reads its class's solo steps when it is made (Think reads them again each life),
    // so a listed class's cap is BLUE from its first think; a player with no class yet has none.
    [Test]
    public void A_bots_state_takes_its_classs_solo_steps()
    {
        GamePlayer bard = Player(null);
        typeof(GamePlayer).GetField("m_characterClass", Hidden)!.SetValue(bard, new DOL.GS.PlayerClass.ClassBard());
        Assert.That(new PlayerBotState(bard).SoloSteps, Is.EqualTo(1));
        GamePlayer warrior = Player(null);
        typeof(GamePlayer).GetField("m_characterClass", Hidden)!.SetValue(warrior, new DOL.GS.PlayerClass.ClassWarrior());
        Assert.That(new PlayerBotState(warrior).SoloSteps, Is.EqualTo(0));
        Assert.That(new PlayerBotState(Player(null)).SoloSteps, Is.EqualTo(0), "no class yet");
    }

    // With no camp that fits, the fallback takes no camp whose median is at or over the lowest con that killed the bot:
    // none at all rather than one (EndReplan: it hunts where it is, under its cap). Before the failed camps' preference:
    // the failed BLUE camp, the only one left, before a YELLOW one after a YELLOW death. A BLUE killer leaves it alone
    // (st-deathsD's fix round).
    [Test]
    public void A_fallback_camp_cons_under_the_lowest_con_that_killed_the_bot()
    {
        var monsters = new[]
        {
            Monster("b1", 1_000, ConColor.BLUE), Monster("b2", 1_100, ConColor.BLUE),
            Monster("y1", 10_000, ConColor.YELLOW), Monster("y2", 10_100, ConColor.YELLOW),
        };
        (string[], ConColor?, bool) Plan(IReadOnlyList<(string, Vector3, ConColor, bool, bool)> camps, Vector3[] failed,
            ConColor? killerCon) => PlayerBotDriver.PlanRelocation(camps, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW,
            failed, new HashSet<string>(), 5, killerCon);
        Assert.That(Plan(monsters, null, null), Is.EqualTo((new[] { "b1", "b2" }, (ConColor?)ConColor.BLUE, false)));
        Assert.That(Plan(monsters, null, ConColor.YELLOW), Is.EqualTo((new[] { "b1", "b2" }, (ConColor?)ConColor.BLUE, false)));
        Assert.That(Plan(monsters, null, ConColor.BLUE), Is.EqualTo(Plan(monsters, null, null)));
        Assert.That(Plan(monsters[2..], null, ConColor.BLUE), Is.EqualTo(Plan(monsters[2..], null, null)));
        Assert.That(Plan(monsters[2..], null, null), Is.EqualTo((new[] { "y1", "y2" }, (ConColor?)ConColor.YELLOW, false)));
        Assert.That(Plan(monsters[2..], null, ConColor.YELLOW), Is.EqualTo((Array.Empty<string>(), (ConColor?)null, false)));
        Assert.That(Plan(monsters, [new Vector3(1_050, 0, 0)], null),
            Is.EqualTo((new[] { "y1", "y2" }, (ConColor?)ConColor.YELLOW, false)), "the other camp before the failed one");
        Assert.That(Plan(monsters, [new Vector3(1_050, 0, 0)], ConColor.YELLOW),
            Is.EqualTo((new[] { "b1", "b2" }, (ConColor?)ConColor.BLUE, false)));
    }

    // The review of D (D6): the fallback's limit is the cap's (UnderKillerCon): the con under the lowest killer's, never
    // under GREEN. A GREEN killer leaves the bot its GREEN cap, so a GREEN fallback camp too: with `median < killer` no
    // camp was left (grey monsters form none), and a capped bot planned none every 30 s for 15 minutes.
    [TestCase(ConColor.ORANGE, ConColor.YELLOW)] [TestCase(ConColor.YELLOW, ConColor.BLUE)]
    [TestCase(ConColor.BLUE, ConColor.GREEN)] [TestCase(ConColor.GREEN, ConColor.GREEN)]
    public void The_con_under_a_killers_is_never_under_green(ConColor killer, ConColor under) =>
        Assert.That(PlayerBotDriver.UnderKillerCon(killer), Is.EqualTo(under));

    // st-deathsD's fix round: the cap's and the fallback's limit (KillerLimit) after a YELLOW or harder killer only; a BLUE
    // or GREEN one, as none (CountsAsKiller).
    [TestCase(null, null)] [TestCase(ConColor.GREEN, null)] [TestCase(ConColor.BLUE, null)]
    [TestCase(ConColor.YELLOW, ConColor.BLUE)] [TestCase(ConColor.ORANGE, ConColor.YELLOW)] [TestCase(ConColor.PURPLE, ConColor.RED)]
    public void The_killers_limit_counts_a_yellow_or_harder_killer_only(ConColor? killer, ConColor? limit) =>
        Assert.That(PlayerBotDriver.KillerLimit(killer), Is.EqualTo(limit));

    // After a GREEN or a BLUE killer the fallback is the one with none: the failed GREEN camp the review of D (D6) left
    // after a GREEN killer gives way to the other camp, and a BLUE camp is left after one (none before).
    [Test]
    public void After_a_green_or_blue_killer_the_fallback_is_as_with_none()
    {
        var monsters = new[]
        {
            Monster("g1", 1_000, ConColor.GREEN), Monster("g2", 1_100, ConColor.GREEN),
            Monster("b1", 10_000, ConColor.BLUE), Monster("b2", 10_100, ConColor.BLUE),
        };
        (string[], ConColor?, bool) Plan(IReadOnlyList<(string, Vector3, ConColor, bool, bool)> camps, ConColor? killerCon) =>
            PlayerBotDriver.PlanRelocation(camps, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, [new Vector3(1_050, 0, 0)],
                new HashSet<string>(), 5, killerCon);
        Assert.That(Plan(monsters, null), Is.EqualTo((new[] { "b1", "b2" }, (ConColor?)ConColor.BLUE, false)),
            "the other camp before the failed one");
        foreach (ConColor killer in new[] { ConColor.GREEN, ConColor.BLUE })
        {
            Assert.That(Plan(monsters, killer), Is.EqualTo(Plan(monsters, null)), $"{killer}");
            Assert.That(Plan(monsters[..2], killer), Is.EqualTo((new[] { "g1", "g2" }, (ConColor?)ConColor.GREEN, false)));
            Assert.That(Plan(monsters[2..], killer), Is.EqualTo((new[] { "b1", "b2" }, (ConColor?)ConColor.BLUE, false)),
                $"a BLUE camp after a {killer} killer");
        }
    }

    // --- the recovery from a death (#167 F4) ---

    // A death owes a replan before the walk back when the bot is on no task and can move its leash (no lever spot bot,
    // in its anchor's region: CanRelocate). The review of 77ba437: with it always false, no test failed. Wherever it
    // died (#167 C1b): a death more than LeashRadius + RoamRadius from its anchor (on a walk, a relocation) owed none,
    // and the bot walked back past the camp it died at; those lives held 44 of st-deathsB's 146 again deaths.
    [TestCase(false, true, true)] [TestCase(true, true, false)] [TestCase(false, false, false)] [TestCase(true, false, false)]
    public void A_death_owes_a_replan_wherever_it_died_unless_on_a_task_or_held(bool onTask, bool canRelocate, bool owes) =>
        Assert.That(PlayerBotDriver.OwesReplan(onTask, canRelocate), Is.EqualTo(owes));

    // The replan a death owes as HandleDeath sets it, from the death spot and the anchor (the review of 9aacd69: with
    // the old LeashRadius + RoamRadius limit put back at the call no test failed): died 5,401 or 9,322 units from its
    // anchor (on a walk, a relocation) it owes one as at its camp; on a task or a lever spot bot, none.
    [TestCase(false, true, 0f, "died")] [TestCase(false, true, 5_401f, "died")] [TestCase(false, true, 9_322f, "died")]
    [TestCase(true, true, 5_401f, null)] [TestCase(false, false, 9_322f, null)] [TestCase(true, false, 0f, null)]
    public void A_deaths_replan_is_owed_however_far_from_its_anchor_it_died(bool onTask, bool canRelocate, float away,
        string owed) =>
        Assert.That(PlayerBotDriver.ReplanOwedAfterDeath(onTask, canRelocate, new Vector3(1_000 + away, 2_000, 0),
            new Vector3(1_000, 2_000, 0)), Is.EqualTo(owed));

    // The plan's origin: after a death the spot it died at, not where it stands (its bind: 5,808 units off at the median
    // in st-deathsA, the review of 77ba437); a capped or outgrown bot plans from where it is.
    [Test]
    public void A_replan_after_a_death_starts_from_the_death_spot()
    {
        Vector3 spot = new(1_000, 2_000, 30), here = new(6_000, 5_000, 0);
        Assert.That(PlayerBotDriver.RelocationOrigin(PlayerBotDriver.Died, spot, here), Is.EqualTo(spot));
        Assert.That(PlayerBotDriver.RelocationOrigin(PlayerBotDriver.Capped, spot, here), Is.EqualTo(here));
        Assert.That(PlayerBotDriver.RelocationOrigin(PlayerBotDriver.Outgrown, spot, here), Is.EqualTo(here));
        Assert.That(PlayerBotDriver.RelocationOrigin(PlayerBotDriver.BrokeOff, spot, here), Is.EqualTo(here), "#167 C2");
    }

    // The corridor test's origin (a corridor is one zone's, PlayerTravelPath.cs:75-81): where the bot stands in the plan's
    // zone; after a death, with the bot at a bind in another zone of its region, the death spot, which lies in the plan's
    // zone (the review of 77ba437: the test was skipped then, and the anchor moved to a camp across a cliff from it);
    // none with no navmesh there.
    [Test]
    public void A_replans_corridors_start_from_the_death_spot_when_the_bot_is_in_another_zone()
    {
        Vector3 spot = new(1_000, 2_000, 30), here = new(6_000, 5_000, 0);
        Assert.That(PlayerBotDriver.CorridorOrigin(PlayerBotDriver.Died, true, true, spot, here), Is.EqualTo(here));
        Assert.That(PlayerBotDriver.CorridorOrigin(PlayerBotDriver.Died, true, false, spot, here), Is.EqualTo(spot));
        Assert.That(PlayerBotDriver.CorridorOrigin(PlayerBotDriver.Capped, true, true, spot, here), Is.EqualTo(here));
        Assert.That(PlayerBotDriver.CorridorOrigin(PlayerBotDriver.Died, false, false, spot, here), Is.Null, "no navmesh");
        Assert.That(PlayerBotDriver.CorridorOrigin(PlayerBotDriver.Died, false, true, spot, here), Is.Null);
        Assert.That(PlayerBotDriver.CorridorOrigin(PlayerBotDriver.BrokeOff, true, true, spot, here), Is.EqualTo(here));
    }

    // The fit pass bars a failed camp too, not only the fallback (st-deathsA: after a relocation the bots walked back to
    // the camp they died at, the nearest that fitted): with the camp it died at and one 3,000 units on that both fit, the
    // other one.
    [Test]
    public void A_relocation_after_a_death_takes_another_camp_that_fits_not_the_one_it_died_at()
    {
        var monsters = new[]
        {
            Monster("a1", 0, ConColor.GREEN), Monster("a2", 100, ConColor.GREEN), Monster("a3", 200, ConColor.GREEN),
            Monster("b1", 3_000, ConColor.GREEN), Monster("b2", 3_100, ConColor.GREEN), Monster("b3", 3_200, ConColor.GREEN),
        };
        Vector3[] failed = [new(50, 0, 0)];
        Assert.That(PlayerBotDriver.PlanRelocation(monsters, Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, failed,
            new HashSet<string>(), 5), Is.EqualTo((new[] { "b1", "b2", "b3" }, (ConColor?)null, false)));
        Assert.That(PlayerBotDriver.PlanRelocation(monsters[..3], Vector3.Zero, ConColor.GREEN, ConColor.YELLOW, failed,
            new HashSet<string>(), 5), Is.EqualTo((new[] { "a1", "a2", "a3" }, (ConColor?)ConColor.GREEN, false)),
            "the failed camp when it is the only one, as the fallback takes it");
    }

    // The adds rule's ring, round the bot itself: a monster that would aggro (IsPotentialAdd) reaches its aggro range +
    // AddMargin; with an aggro range of 500, a spot 300 units off is in its reach and one 800 off is not.
    [TestCase(300f, true)] [TestCase(700f, true)] [TestCase(701f, false)] [TestCase(800f, false)]
    public void A_spot_within_an_adds_aggro_range_and_200_is_in_its_reach(float x, bool inReach) =>
        Assert.That(PlayerBotDriver.InAddReach(Vector3.Zero, new List<(string, Vector3, int)> { ("add", new(x, 0, 0), 500) }),
            Is.EqualTo(inReach));

    // The clear spots nearest first, and the spot a bot in reach steps to: the nearest out of every reach; none, here.
    [Test]
    public void A_bot_in_an_adds_reach_steps_to_the_nearest_clear_spot()
    {
        var adds = new List<(string, Vector3, int)> { ("a", new(0, 0, 0), 500), ("b", new(1_200, 0, 0), 500) };
        Vector3 here = new(100, 0, 0);
        Vector3[] candidates = [new(600, 0, 0), new(100, -1_500, 0), new(-900, 0, 0), new(100, 900, 0)];
        Assert.That(PlayerBotDriver.ClearSpots(here, candidates, adds),
            Is.EqualTo(new[] { new Vector3(100, 900, 0), new Vector3(-900, 0, 0), new Vector3(100, -1_500, 0) }),
            "(600,0) is in both reaches");
        Assert.That(PlayerBotDriver.RestSpot(here, candidates, adds), Is.EqualTo(new Vector3(100, 900, 0)));
        Assert.That(PlayerBotDriver.RestSpot(here, [new Vector3(600, 0, 0)], adds), Is.EqualTo(here));
    }

    // #167 D10: the rest walk's spots on the same data (RestSpots): ClearSpots' order, the first RestSpot's; none from a
    // spot out of every reach, where RestSpot gives here. ClearSpotNear's single spot (SpotOutOfReach) is unchanged.
    [Test]
    public void The_rest_walks_spots_are_the_clear_spots_nearest_first_the_first_the_rest_spot()
    {
        var adds = new List<(string, Vector3, int)> { ("a", new(0, 0, 0), 500), ("b", new(1_200, 0, 0), 500) };
        Vector3 here = new(100, 0, 0);
        Vector3[] candidates = [new(600, 0, 0), new(100, -1_500, 0), new(-900, 0, 0), new(100, 900, 0)];
        List<Vector3> spots = PlayerBotDriver.RestSpots(here, candidates, adds);
        Assert.That(spots, Is.EqualTo(PlayerBotDriver.ClearSpots(here, candidates, adds)));
        Assert.That(spots[0], Is.EqualTo(PlayerBotDriver.RestSpot(here, candidates, adds)).And.EqualTo(new Vector3(100, 900, 0)));
        Assert.That(PlayerBotDriver.RestSpots(new Vector3(100, 2_000, 0), candidates, adds), Is.Empty, "it stands clear");
    }

    // #167 D10: a blocked rest walk takes the next clear spot (RestGoalAt), RestSpotTries in all, then the bot sits where
    // it is. BLOCKED walk=rest went from 3 to 36 lines in st-deathsC, each "no complete walkable path" at once, and the bot
    // sat inside the reach it was leaving: a monster ended 5 of the 34 next rests (1% overall), and all 4 residual respawn
    // stands and a death came after one.
    [Test]
    public void A_blocked_rest_walk_takes_the_next_clear_spot()
    {
        Vector3[] clear = [new(600, 0, 0), new(0, 600, 0), new(-600, 0, 0), new(0, -600, 0)];
        Assert.That(Enumerable.Range(0, 4).Select(blocked => PlayerBotDriver.RestGoalAt(clear, blocked, 3)),
            Is.EqualTo(new Vector3?[] { new(600, 0, 0), new(0, 600, 0), new(-600, 0, 0), null }), "3 tries, then it sits");
        Assert.That(PlayerBotDriver.RestGoalAt([], 0, 3), Is.Null);
        Assert.That(PlayerBotDriver.RestSpotTries, Is.EqualTo(3));
    }

    // #167 D10: the rest walk's goal after a step (WalksToRestSpot): blocked, the next spot and one block more; arrived,
    // none (it sits there); walking or waiting, the same.
    [Test]
    public void The_rest_walk_goes_on_to_the_next_spot_after_a_block()
    {
        Vector3[] clear = [new(600, 0, 0), new(0, 600, 0), new(-600, 0, 0), new(0, -600, 0)];
        Assert.That(PlayerBotDriver.AfterRestStep(PlayerBotStepResult.Blocked, clear[0], clear, 0),
            Is.EqualTo(((Vector3?)new Vector3(0, 600, 0), 1)));
        Assert.That(PlayerBotDriver.AfterRestStep(PlayerBotStepResult.Blocked, clear[2], clear, 2),
            Is.EqualTo(((Vector3?)null, 3)), "the third block: it sits");
        Assert.That(PlayerBotDriver.AfterRestStep(PlayerBotStepResult.Arrived, clear[1], clear, 1),
            Is.EqualTo(((Vector3?)null, 1)));
        Assert.That(PlayerBotDriver.AfterRestStep(PlayerBotStepResult.Moving, clear[1], clear, 1),
            Is.EqualTo(((Vector3?)clear[1], 1)));
        Assert.That(PlayerBotDriver.AfterRestStep(PlayerBotStepResult.Waiting, clear[0], clear, 0),
            Is.EqualTo(((Vector3?)clear[0], 0)));
    }

    // The candidates: directions points on each ring round the bot, at its height, the first along +X.
    [Test]
    public void The_candidate_spots_lie_on_rings_round_the_bot()
    {
        Vector3[] points = PlayerBotDriver.RingPoints(new Vector3(1_000, 2_000, 30), [500, 900], 4).ToArray();
        Vector3[] expected = [new(1_500, 2_000, 30), new(1_000, 2_500, 30), new(500, 2_000, 30), new(1_000, 1_500, 30),
            new(1_900, 2_000, 30), new(1_000, 2_900, 30), new(100, 2_000, 30), new(1_000, 1_100, 30)];
        Assert.That(points.Length, Is.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
            Assert.That(Vector3.Distance(points[i], expected[i]), Is.LessThan(0.01f), $"point {i}: {points[i]}");
    }

    // After a release: it fights back what hits it; else it steps out of reach, then plans the relocation it owes (any
    // death, C1b), then waits out its back-off (the GameBot's, from the third death with no XP:
    // AutonomousDeathRecoveryPolicy) and the resurrection illness; then the recovery is over. #167 D4: while the release's
    // immunity lasts with a camp planned and a point toward it (immuneWalk), it walks out toward the camp, before all but
    // the fight: the back-off and the illness wait at the point.
    [TestCase(true, true, true, true, true, false, PlayerBotRecovery.Fight)]
    [TestCase(false, true, true, true, true, false, PlayerBotRecovery.StepOut)]
    [TestCase(false, false, true, true, true, false, PlayerBotRecovery.Replan)]
    [TestCase(false, false, false, true, false, false, PlayerBotRecovery.Wait)]
    [TestCase(false, false, false, false, true, false, PlayerBotRecovery.Wait)]
    [TestCase(false, false, false, false, false, false, PlayerBotRecovery.Done)]
    [TestCase(false, false, false, true, true, true, PlayerBotRecovery.WalkOut)]
    [TestCase(false, true, false, false, false, true, PlayerBotRecovery.WalkOut)]
    [TestCase(true, false, false, false, false, true, PlayerBotRecovery.Fight)]
    public void After_a_release_a_bot_steps_out_of_reach_then_replans_then_backs_off(bool attacked, bool stepsOut,
        bool replanOwed, bool backingOff, bool ill, bool immuneWalk, PlayerBotRecovery step) =>
        Assert.That(PlayerBotDriver.AfterReleaseStep(attacked, stepsOut, replanOwed, backingOff, ill, immuneWalk),
            Is.EqualTo(step));

    // #167 D4: the walk out's point, here (0,0,0): of the clear points within reach (the immunity left times the bot's
    // speed), the one that leaves the least distance to the planned camp (progress, not detour: the verifier's
    // correction); on a tie, the one nearest here. With no camp, the nearest; none in reach, none.
    [TestCase(10_000f, new[] { -700f, 0f, 0f, 900f, 1_500f, 0f, 2_500f, 0f }, 2_400f, 1_500f, 0f)]
    [TestCase(10_000f, new[] { -700f, 0f, 0f, 900f, 1_500f, 0f, 2_500f, 0f }, 1_000f, 0f, 900f)]
    [TestCase(float.NaN, new[] { -700f, 0f, 0f, 900f, 1_500f, 0f, 2_500f, 0f }, 2_400f, -700f, 0f)]
    [TestCase(2_000f, new[] { 1_000f, 0f, 500f, 0f }, 2_400f, 1_000f, 0f)]
    [TestCase(2_000f, new[] { 2_000f, 600f, 1_400f, 0f }, 2_400f, 1_400f, 0f)]
    public void The_walk_out_takes_the_clear_point_nearest_the_camp_within_reach(float goalX, float[] xy, float reach,
        float x, float y)
    {
        Vector3[] points = Enumerable.Range(0, xy.Length / 2).Select(i => new Vector3(xy[2 * i], xy[2 * i + 1], 0)).ToArray();
        Vector3? goal = float.IsNaN(goalX) ? null : new Vector3(goalX, 0, 0);
        Assert.That(PlayerBotDriver.StepOutPoint(Vector3.Zero, goal, points, reach), Is.EqualTo((Vector3?)new Vector3(x, y, 0)));
    }

    [Test]
    public void The_walk_out_has_no_point_out_of_reach() =>
        Assert.That(PlayerBotDriver.StepOutPoint(Vector3.Zero, new Vector3(10_000, 0, 0), [new Vector3(3_000, 0, 0)], 2_400),
            Is.Null);

    // #167 D4: it walks on to the point unless it stands clear already and the point leaves no less to the camp: the
    // rings start 500 units out, so at the camp's edge it would step between clear points until the immunity ran out.
    // In a monster's reach it steps out wherever the point is.
    [TestCase(false, 10_000f, -700f, 0f, true)]
    [TestCase(true, 10_000f, 1_500f, 0f, true)]
    [TestCase(true, 10_000f, 0f, 900f, false)]
    [TestCase(true, float.NaN, -700f, 0f, false)]
    public void From_a_clear_spot_the_walk_out_goes_on_only_toward_the_camp(bool hereClear, float goalX, float x, float y,
        bool walks) =>
        Assert.That(PlayerBotDriver.WalksOut(Vector3.Zero, hereClear, float.IsNaN(goalX) ? null : new Vector3(goalX, 0, 0),
            new Vector3(x, y, 0)), Is.EqualTo(walks));

    [Test]
    public void With_no_point_there_is_no_walk_out() =>
        Assert.That(PlayerBotDriver.WalksOut(Vector3.Zero, false, new Vector3(10_000, 0, 0), null), Is.False);

    // The review of D (D4): the walk out's start (StartsWalkOut), StepsOutOfReach's gate. With it switched off no test
    // failed, and a bot back from its release with immunity left stood at its anchor. It starts while the immunity lasts,
    // with no point to walk to yet, the walk not over for this release, and a camp planned since the release.
    [TestCase(5_000L, false, false, true, true)]
    [TestCase(0L, false, false, true, false)]                                 // the immunity over
    [TestCase(5_000L, true, false, true, false)]                              // a point already
    [TestCase(5_000L, false, true, true, false)]                              // over for this release
    [TestCase(5_000L, false, false, false, false)]                            // no camp planned yet
    public void The_walk_out_starts_while_immune_with_a_camp_planned(long immuneLeft, bool hasGoal, bool over,
        bool backMoved, bool starts) =>
        Assert.That(PlayerBotDriver.StartsWalkOut(immuneLeft, hasGoal, over, backMoved), Is.EqualTo(starts));

    // #167 D4: the immunity left. The server's end (m_invulnerabilityTick) is protected (GamePlayer.cs:12236): the release
    // starts TIMER_KILLED_BY_MOB seconds (AbstractServerRules.cs:258-262) from the release the bot asked for (the ask that
    // revived it; else from its first think alive), and none with the server's immunity off (a PvE server starts none,
    // GamePlayer.cs:12249-12250) or over. The reach is that time at its run, or its sprint.
    [TestCase(9_750L, 10_000L, 10, 19_750L)] [TestCase(0L, 10_000L, 10, 20_000L)] [TestCase(9_750L, 10_000L, 0, 9_750L)]
    public void The_immunity_ends_its_length_after_the_release(long askedAt, long releasedAt, int seconds, long end) =>
        Assert.That(PlayerBotDriver.ImmunityEnd(askedAt, releasedAt, seconds), Is.EqualTo(end));

    [TestCase(19_750L, 12_000L, true, 7_750L)] [TestCase(19_750L, 12_000L, false, 0L)]
    [TestCase(19_750L, 20_000L, true, 0L)]
    public void The_immunity_left_is_none_once_the_server_holds_none(long end, long now, bool invulnerable, long left) =>
        Assert.That(PlayerBotDriver.ImmunityLeft(end, now, invulnerable), Is.EqualTo(left));

    [TestCase(6_000L, true, 1_488f)] [TestCase(6_000L, false, 1_146f)] [TestCase(0L, true, 0f)]
    public void The_walk_outs_reach_is_the_immunity_left_at_its_speed(long leftMs, bool sprinting, float reach) =>
        Assert.That(PlayerBotDriver.WalkOutReach(leftMs, sprinting), Is.EqualTo(reach).Within(0.01f));

    // #167 D4: the walk out sprints when the bot has the endurance to start one (GamePlayer.cs:8633), and its sprint goes
    // off when the walk ends: a sprint drains endurance whenever the bot moves (GamePlayer.cs:2397-2425), so the walk on
    // to the camp would end near 5%. A sprint of its own only (a break-off's stays).
    [TestCase(true, false, 100, true)] [TestCase(true, false, 10, null)] [TestCase(true, true, 50, null)]
    [TestCase(false, true, 50, false)] [TestCase(false, false, 50, null)]
    public void The_walk_out_sprints_and_its_sprint_ends_with_it(bool walkingOut, bool walkOutSprint, int endurance,
        bool? sprint) =>
        Assert.That(PlayerBotDriver.SprintChange(walkingOut, walkOutSprint, endurance), Is.EqualTo(sprint));

    // The release reads the immunity's end from the release asked for (still set at StartLife; Think clears it after).
    [Test]
    public void A_release_starts_the_walk_out_afresh()
    {
        int saved = ServerProperties.Properties.TIMER_KILLED_BY_MOB;
        try
        {
            ServerProperties.Properties.TIMER_KILLED_BY_MOB = 10;
            var released = new PlayerBotState(Player(null)) { AliveSince = 1_000, DeathRecorded = true, ReleaseAskedAt = 8_750,
                WalkOutGoal = Vector3.One, WalkOutOver = true, WalkOutSprint = true, BackImmuneMs = 2_000, BackSame = true };
            PlayerBotDriver.StartLife(released, 9_000, PlayerBotRole.Melee);
            Assert.That((released.ImmuneUntil, released.WalkOutGoal, released.WalkOutOver, released.WalkOutSprint,
                released.BackImmuneMs, released.BackSame), Is.EqualTo((18_750L, (Vector3?)null, false, false, 0L, false)));
            var login = new PlayerBotState(Player(null));
            PlayerBotDriver.StartLife(login, 5_000, PlayerBotRole.Melee);
            Assert.That(login.ImmuneUntil, Is.Zero, "no release, no walk out");
        }
        finally
        {
            ServerProperties.Properties.TIMER_KILLED_BY_MOB = saved;
        }
    }

    // #167 C6: a login's first life steps out of reach too (73 of st-deathsB's 370 logins died in their first fight, 37
    // of the 42 login-life deaths with adds within 60 s of the login: the test spots are camp centres), with no back-off
    // and no replan; a life after a release does all three (#167 F4); a later think starts none.
    [TestCase(true, false, true, true)] [TestCase(false, true, true, false)] [TestCase(false, false, false, false)]
    [TestCase(true, true, true, true)]
    public void A_login_steps_out_of_reach_as_a_release_does_with_no_back_off(bool afterDeath, bool firstLife,
        bool stepOut, bool backOff) =>
        Assert.That(PlayerBotDriver.LifeStart(afterDeath, firstLife), Is.EqualTo((stepOut, backOff)));

    // Its wiring: the login's life starts the step out only (no release time, so DEATH again= and sinceRelease= stay the
    // release's); the release's the back-off and the replan's hold too.
    [Test]
    public void A_new_life_starts_the_step_out_at_login_and_the_whole_recovery_after_a_release()
    {
        var login = new PlayerBotState(Player(null));
        PlayerBotDriver.StartLife(login, 5_000, PlayerBotRole.Melee);
        Assert.That((login.AfterRelease, login.RecoverUntil, login.ReleasedAt, login.ReplanSince, login.AliveSince),
            Is.EqualTo((true, 5_000L, 0L, 0L, 5_000L)));
        var released = new PlayerBotState(Player(null)) { AliveSince = 1_000, DeathRecorded = true,
            ReplanOwed = PlayerBotDriver.Died, NoXpDeaths = 3 };
        PlayerBotDriver.StartLife(released, 9_000, PlayerBotRole.Melee);
        Assert.That((released.AfterRelease, released.RecoverUntil, released.ReleasedAt, released.ReplanSince,
            released.AliveSince), Is.EqualTo((true, 39_000L, 9_000L, 9_000L, 9_000L)), "the third death with no XP: 30 s");
        var later = new PlayerBotState(Player(null)) { AliveSince = 1_000 };
        PlayerBotDriver.StartLife(later, 9_000, PlayerBotRole.Melee);
        Assert.That((later.AfterRelease, later.AliveSince), Is.EqualTo((false, 1_000L)), "no new life");
    }

    // #167 D3: a caster's login life opens rested (RecoverBeforePull): its login buffs drain power, and 13 of st-deathsC's
    // 53 caster login first fights with no rest were fatal, 7 of the 12 that died in the fight at 16% power or less. A
    // release restores full power already (GamePlayer.cs:1723-1725), so not after one; nor any other role.
    [TestCase(PlayerBotRole.Caster, false, true)] [TestCase(PlayerBotRole.Caster, true, false)]
    [TestCase(PlayerBotRole.Melee, false, false)] [TestCase(PlayerBotRole.Pet, false, false)]
    [TestCase(PlayerBotRole.Healer, false, false)] [TestCase(PlayerBotRole.Archer, false, false)]
    public void A_casters_login_life_opens_rested(PlayerBotRole role, bool afterRelease, bool rested) =>
        Assert.That(PlayerBotDriver.OpensLifeRested(role, afterRelease), Is.EqualTo(rested));

    [Test]
    public void A_casters_login_owes_a_rest_and_its_release_does_not()
    {
        var login = new PlayerBotState(Player(null));
        PlayerBotDriver.StartLife(login, 5_000, PlayerBotRole.Caster);
        Assert.That(login.RecoverBeforePull, Is.True);
        var released = new PlayerBotState(Player(null)) { AliveSince = 1_000, DeathRecorded = true, RecoverBeforePull = true };
        PlayerBotDriver.StartLife(released, 9_000, PlayerBotRole.Caster);
        Assert.That(released.RecoverBeforePull, Is.False);
        var melee = new PlayerBotState(Player(null));
        PlayerBotDriver.StartLife(melee, 5_000, PlayerBotRole.Melee);
        Assert.That(melee.RecoverBeforePull, Is.False);
    }

    // PLAYER_BOT_BACK (#167 C1, the verifier's logging): from a release to the first fight, the seconds in all and those
    // stepping out, held and walking, and the replan: moved (a camp planned since the release), none (owed, no camp
    // planned) or notOwed. The 40 s more from a death to the next fight in st-deathsB was not the failed replan. #167 D4:
    // then immune=, the seconds it walked while immune.
    [Test]
    public void The_way_back_line_splits_the_time_from_a_release_to_the_first_fight() =>
        Assert.That(PlayerBotDriverReport.FormatBack("Pbotaaaaz", 34, 81, 6, 42, 20, "moved", 8),
            Is.EqualTo("PLAYER_BOT_BACK bot=Pbotaaaaz level=34 sec=81 stepOut=6 hold=42 walk=20 replan=moved immune=8"));

    // #167 D4, D9: replan=same when the camp planned is the anchor the bot had (37 of st-deathsC's 174 died replans, 24
    // of them not Pbotaaacv's).
    [TestCase(false, false, false, "notOwed")] [TestCase(false, true, true, "notOwed")]
    [TestCase(true, true, false, "moved")] [TestCase(true, true, true, "same")]
    [TestCase(true, false, false, "none")] [TestCase(true, false, true, "none")]
    public void The_way_back_gives_the_replan_owed_at_the_release(bool owed, bool planned, bool sameAnchor, string replan) =>
        Assert.That(PlayerBotDriver.BackReplan(owed, planned, sameAnchor), Is.EqualTo(replan));

    // The same camp: within 200 units of the anchor (d-after-release/a3_samespot.py's rule), and only a death's plan
    // counts it.
    [TestCase("died", 150f, true)] [TestCase("died", 250f, false)] [TestCase("capped", 150f, false)]
    public void A_deaths_plan_to_the_anchor_it_had_counts_as_the_same(string reason, float away, bool same)
    {
        var state = new PlayerBotState(Player(null)) { Anchor = new Vector3(5_000, 6_000, 70), ReplanOwed = reason };
        PlayerBotDriver.PlanCamp(state, new Vector3(5_000 + away, 6_000, 70), null, stale: false, reason);
        Assert.That(state.BackSame, Is.EqualTo(same));
    }

    // A release starts the way back's count afresh (the replan owed then, nothing moved, no time in any phase); a login
    // starts none: no release, no line.
    [Test]
    public void A_release_starts_the_way_back_and_a_login_does_not()
    {
        var released = new PlayerBotState(Player(null)) { AliveSince = 1_000, DeathRecorded = true,
            ReplanOwed = PlayerBotDriver.Died, BackMoved = true, BackStepOutMs = 5_000, BackHoldMs = 250, BackWalkMs = 500 };
        PlayerBotDriver.StartLife(released, 9_000, PlayerBotRole.Melee);
        Assert.That((released.BackPending, released.BackOwed, released.BackMoved, released.BackStepOutMs,
            released.BackHoldMs, released.BackWalkMs), Is.EqualTo((true, true, false, 0L, 0L, 0L)));
        var login = new PlayerBotState(Player(null));
        PlayerBotDriver.StartLife(login, 5_000, PlayerBotRole.Melee);
        Assert.That(login.BackPending, Is.False);
    }

    // A camp planned (PlanCamp, RelocateIfOutgrown's move) after a death sets BACK replan=moved; a capped or outgrown
    // move doesn't. Either way the relocation owed is planned (none owed after), the leash and the walk go to the spot,
    // the capped clock starts afresh. The review of 9aacd69: with BackMoved's line dropped no test failed.
    [TestCase("died", true)] [TestCase("brokeoff", false)] [TestCase("capped", false)]
    public void A_planned_camp_ends_the_replan_owed_and_counts_for_the_way_back_after_a_death(string reason, bool moved)
    {
        var state = new PlayerBotState(Player(null)) { ReplanOwed = reason == "capped" ? null : reason,
            ReplanSince = 4_000, CappedSince = 3_000, BackOwed = true };
        Vector3 spot = new(5_000, 6_000, 70);
        PlayerBotDriver.PlanCamp(state, spot, ConColor.BLUE, stale: true, reason);
        Assert.That((state.Anchor, state.RoamDestination, state.FallbackCap, state.HuntAvoided, state.CappedSince),
            Is.EqualTo((spot, (Vector3?)spot, (ConColor?)ConColor.BLUE, true, 0L)));
        Assert.That((state.ReplanOwed, state.ReplanSince, state.BackMoved), Is.EqualTo(((string)null, 0L, moved)));
    }

    // The fight's first think in reach (OpenFight) starts its counts afresh, ends the kill's second-monster case (C2) and
    // gives BACK once since the release: the review of 9aacd69, with Engage's ReportBack dropped no test failed.
    [Test]
    public void A_fights_first_think_starts_its_counts_and_gives_the_way_back_once()
    {
        var state = new PlayerBotState(Player(null)) { BackPending = true, KilledWithAdds = true, ControlTries = 3,
            FightCasts = 4, FightAdd = (2, 50), NextAddScanTick = 9_000, NextKiteTick = 9_000, TargetHeldBreaks = true,
            FollowsAddKill = true, BreakOffRefused = true, FightKiteNone = (1, 2, 3, 4),
            LastWalk = PlayerBotWalk.Rest, FightWalk = PlayerBotWalk.Hunt, FightHeldMs = 3_000, FightCtlLanded = 2,
            FightCtlFailed = 1, FightHpAtReach = 50, FightTargetHpAtReach = 40, WouldBreakHp = 30,
            PendingControl = (Control(500, "root", "SpeedDecrease", 10, 99), Player(null), 8_000), FightBuffHitters = 2,
            FightDots = 2, FightKillingQuickcast = true, FightTargetMezzes = 1, FightRaceLeft = 22, FightStylesHeld = 3,
            FightCastsHeld = 2, Reserve = 36, NextReserveTick = 9_000 };
        Assert.That(PlayerBotDriver.OpenFight(state, 10_000, 90, 60, 1_420, 100), Is.True, "the first fight since a release");
        Assert.That((state.BackPending, state.KilledWithAdds, state.ControlTries, state.FightCasts, state.FightAdd),
            Is.EqualTo((false, false, 0, 0, (-1, -1))));
        Assert.That((state.EngagedSince, state.EngageHealth, state.EngageMana, state.EngageDistance, state.EngageTargetHealth),
            Is.EqualTo((10_000L, (byte)90, (byte)60, 1_420, (byte)100)));
        Assert.That((state.NextAddScanTick, state.NextKiteTick, state.TargetHeldBreaks), Is.EqualTo((0L, 0L, false)));
        Assert.That((state.FollowsAddKill, state.BreakOffRefused), Is.EqualTo((false, false)),
            "#167 D2: no add kill 3 s before (the mark's tick is 0), no refusal logged in this fight");
        Assert.That(state.FightKiteNone, Is.EqualTo((0, 0, 0, 0)), "#167 D8: kiteNone= afresh");
        Assert.That((state.FightWalk, state.FightHeldMs, state.FightCtlLanded, state.FightCtlFailed, state.FightHpAtReach,
            state.FightTargetHpAtReach, state.WouldBreakHp, state.PendingControl.Spell), Is.EqualTo((PlayerBotWalk.Rest, 0L, 0, 0,
            -1, -1, -1, (Spell)null)), "st-deathsD's fields afresh; walk=: the last think's walk");
        Assert.That(state.FightBuffHitters, Is.Zero, "#167 D12: no monster on the bot before the fight's first think");
        Assert.That(state.FightDots, Is.Zero, "#167 E C1: dots= afresh");
        Assert.That(state.FightKillingQuickcast, Is.False, "#167 E C2b: qcKill= afresh");
        Assert.That(state.FightTargetMezzes, Is.Zero, "#167 E C4: ctlMez= afresh");
        Assert.That(state.FightRaceLeft, Is.EqualTo(PlayerBotDriver.NoRace), "#167 E-L: raceLeft= afresh");
        Assert.That((state.FightStylesHeld, state.FightCastsHeld, state.Reserve, state.NextReserveTick),
            Is.EqualTo((0, 0, 0, 0L)), "#167 E-R: styleHeld= and castHeld= afresh, and a fresh reserve read");
        Assert.That(PlayerBotDriver.OpenFight(state, 30_000, 90, 60, 1_420, 100), Is.False, "the next one");
    }

    // --- the release (#167 D7) ---

    // Pbotaaacv stayed dead from its 15th death to the end of st-deathsB and st-deathsC. A keep guard lord's arrow lands
    // from a timer (AttackAction.cs:364-365), in the timer phase the think runs in: the bot's first dead think can come
    // before the reaper's ProcessDeath (ReaperService.cs:55), which stamps DeathTick (GamePlayer.cs:5953). Its DeathTick
    // was the last death's, so the 10 s had long passed and the release went at once; ProcessDeath then killed the
    // revived bot (Health = 0, GameLiving.cs:2066) and the one-shot release was spent, with no think alive to clear it:
    // dead until the server's own 900-s release (GamePlayer.cs:1399, :5955). Now no release while the reaper has it, 10 s
    // from the later of DeathTick and the bot's first dead think, and again 10 s after an ask that didn't revive it.
    [Test]
    public void No_release_while_the_reaper_has_the_bot_and_a_release_that_didnt_take_goes_again()
    {
        MethodInfo handleDeath = typeof(PlayerBotDriver).GetMethod("HandleDeath", BindingFlags.Static | BindingFlags.NonPublic)!;
        FieldInfo deathTick = typeof(GamePlayer).GetField("<DeathTick>k__BackingField", Hidden)!,
            reaped = typeof(GameLiving).GetField("_isBeingHandledByReaperService", Hidden)!;
        GamePlayer bot = Player(null);                                                  // no character: Release returns
        deathTick.SetValue(bot, 20_000L);                                               // the 14th death's
        reaped.SetValue(bot, true);                                                     // Die, not yet reaped
        var state = new PlayerBotState(bot) { AliveSince = 25_000 };
        handleDeath.Invoke(null, [state, bot, 54_000L]);                                // its first dead think
        Assert.That((state.ReleaseAskedAt, state.ReleaseTries), Is.EqualTo((0L, 0)),
            "no release before the reaper has stamped this death");
        (bool, long)[] asks = [(false, 60_000), (true, 64_000), (false, 70_000), (true, 74_000)];
        deathTick.SetValue(bot, 54_000L);                                               // ProcessDeath, the same tick
        reaped.SetValue(bot, false);
        foreach ((bool asked, long now) in asks)
        {
            int tries = state.ReleaseTries;
            handleDeath.Invoke(null, [state, bot, now]);
            Assert.That(state.ReleaseTries - tries, Is.EqualTo(asked ? 1 : 0), $"at {now}");
            if (asked) Assert.That(state.ReleaseAskedAt, Is.EqualTo(now));
        }
        Assert.That(state.ReleaseTries, Is.EqualTo(2), "still dead 10 s after the first ask: the second");
    }

    // The release's rule (ReleaseDue): not while the reaper has the bot (Die ran, ProcessDeath not yet); 10 s from the
    // later of the server's DeathTick and the driver's first dead think (lastDeathTick: a think before ProcessDeath reads
    // the last death's tick); and 10 s after an ask (askedAt, 0 for none) that didn't revive it.
    [TestCase(20_000L, 5_000L, 5_000L, false, 0L, true)]
    [TestCase(20_000L, 5_000L, 15_000L, false, 0L, false)]
    [TestCase(20_000L, 15_000L, 15_000L, true, 0L, false)]
    [TestCase(40_000L, 15_000L, 15_000L, false, 25_000L, true)]
    [TestCase(30_000L, 15_000L, 15_000L, false, 25_000L, false)]
    public void A_release_is_due_10_s_after_the_death_is_stamped_and_again_10_s_after_an_ask(long now, long deathTick,
        long lastDeathTick, bool beingReaped, long askedAt, bool due) =>
        Assert.That(PlayerBotDriver.ReleaseDue(now, deathTick, lastDeathTick, beingReaped, askedAt), Is.EqualTo(due));

    // PLAYER_BOT_RELEASE (#167 D7): each ask, its try in this death and the milliseconds since the death (the later of
    // DeathTick and the first dead think).
    [Test]
    public void A_release_line_gives_its_try_and_the_time_since_the_death() =>
        Assert.That(PlayerBotDriverReport.FormatRelease("Pbotaaacv", 35, 2, 20_000),
            Is.EqualTo("PLAYER_BOT_RELEASE bot=Pbotaaacv level=35 try=2 sinceDeath=20000"));

    // #167 D7: Pbotaaacv's bind lay by a battleground keep's realm-less "Renegade" guards, which killed it in 14 of its 14
    // release first fights, 10-13 s after the immunity. IsExperienceTarget leaves keep guards out, so the step out
    // (ClearSpotNear) and the adds rule (the pick's adds, the rest spot's) never saw them. A keep guard of another realm,
    // or of none, counts there; keep guards stay out of pulls and plans (IsExperienceTarget).
    [TestCase(false, true, eRealm.None, eRealm.Albion, true)]
    [TestCase(false, true, eRealm.Albion, eRealm.Albion, false)]
    [TestCase(false, true, eRealm.Midgard, eRealm.Albion, true)]
    [TestCase(true, false, eRealm.None, eRealm.Albion, true)]
    [TestCase(false, false, eRealm.None, eRealm.Albion, false)]
    public void A_keep_guard_of_another_realm_or_none_is_a_threat(bool expTarget, bool keepGuard, eRealm guardRealm,
        eRealm botRealm, bool threat) =>
        Assert.That(PlayerBotDriver.CountsAsThreat(expTarget, keepGuard, guardRealm, botRealm), Is.EqualTo(threat));

    // The scans' read of a monster (IsThreat) with a real guard: a lord of no realm counts against an Albion bot, its own
    // realm's doesn't, and neither is a target to pull.
    [Test]
    public void The_scans_see_a_realmless_keep_guard_and_still_pull_none()
    {
        DOL.GS.Keeps.GuardLord lord = RealmlessLord();
        GameNPC monster = Mob();
        Assert.That((PlayerBotDriver.IsExperienceTarget(lord), PlayerBotDriver.IsThreat(lord, eRealm.Albion)),
            Is.EqualTo((false, true)), "no pull or plan, but a threat");
        Assert.That(PlayerBotDriver.IsThreat(monster, eRealm.Albion), Is.True, "a monster, as before");
        lord.Realm = eRealm.Albion;
        Assert.That(PlayerBotDriver.IsThreat(lord, eRealm.Albion), Is.False, "its own realm's");
    }

    // A keep guard lord of no realm, active, with its brain.
    private static DOL.GS.Keeps.GuardLord RealmlessLord()
    {
        var lord = (DOL.GS.Keeps.GuardLord)RuntimeHelpers.GetUninitializedObject(typeof(DOL.GS.Keeps.GuardLord));
        lord.ObjectState = GameObject.eObjectState.Active;
        typeof(GameNPC).GetField("m_brains", Hidden)!.SetValue(lord, new System.Collections.ArrayList());
        typeof(GameNPC).GetField("m_ownBrain", Hidden)!.SetValue(lord, new DOL.AI.Brain.KeepGuardBrain());
        return lord;
    }

    // A live monster with a mob's brain.
    private static GameNPC Mob()
    {
        GameNPC monster = LiveMonster();
        typeof(GameNPC).GetField("m_ownBrain", Hidden)!.SetValue(monster, new DOL.AI.Brain.StandardMobBrain());
        return monster;
    }

    // The review of D (D7): the break-off's run point keeps clear of a hostile keep guard too (IsBreakOffAdd, BreakOffGoal's
    // adds), as the step out and the walk out do (ThreatsWithin): at Pbotaaacv's bind the run could end in the aggro of a
    // realm-less guard not on it yet. One with the bot as its target is on it already, no add.
    [Test]
    public void A_break_off_runs_clear_of_a_realmless_keep_guard()
    {
        DOL.GS.Keeps.GuardLord lord = RealmlessLord();
        GamePlayer bot = Player(null);
        Assert.That(PlayerBotDriver.IsBreakOffAdd(lord, bot, eRealm.Albion), Is.True, "a realm-less guard");
        Assert.That(PlayerBotDriver.IsBreakOffAdd(Mob(), bot, eRealm.Albion), Is.True, "a monster, as before");
        lord.TargetObject = bot;
        Assert.That(PlayerBotDriver.IsBreakOffAdd(lord, bot, eRealm.Albion), Is.False, "on the bot already");
        (lord.TargetObject, lord.Realm) = (null, eRealm.Albion);
        Assert.That(PlayerBotDriver.IsBreakOffAdd(lord, bot, eRealm.Albion), Is.False, "its own realm's");
    }

    // --- the break-off when losing to two (#167 C2) ---

    // It breaks off with 2 or more monsters on it, or right after a kill in a fight that had 2 or more on it with 1 still
    // on (the second-monster case, k12's rule at the kill), below 40% health, its target (at the kill: the one still on
    // it) above 10%, no control ready for the add (C4), and only from chasers it outruns. One monster in an ordinary
    // fight, it fights on. #167 D1: one monster in the one-monster case (oneMonster: OneMonsterCase) breaks it off too.
    [TestCase(2, false, false, (byte)39, (byte)50, false, true, true)]
    [TestCase(2, false, false, (byte)40, (byte)50, false, true, false)]
    [TestCase(1, false, false, (byte)20, (byte)50, false, true, false)]
    [TestCase(2, false, false, (byte)39, (byte)10, false, true, false)]
    [TestCase(2, false, false, (byte)39, (byte)11, false, true, true)]
    [TestCase(2, false, false, (byte)39, (byte)50, true, true, false)]
    [TestCase(2, false, false, (byte)39, (byte)50, false, false, false)]
    [TestCase(3, false, false, (byte)10, (byte)90, false, true, true)]
    [TestCase(1, true, false, (byte)39, (byte)100, false, true, true)]
    [TestCase(1, true, false, (byte)40, (byte)100, false, true, false)]
    [TestCase(0, true, false, (byte)20, (byte)100, false, true, false)]
    [TestCase(1, false, true, (byte)39, (byte)59, false, true, true)]
    [TestCase(1, false, true, (byte)40, (byte)59, false, true, false)]
    [TestCase(1, false, false, (byte)39, (byte)59, false, true, false)]     // one monster with no case still fights on
    [TestCase(1, false, true, (byte)39, (byte)10, false, true, false)]
    [TestCase(0, false, true, (byte)20, (byte)100, false, true, false)]
    [TestCase(1, false, true, (byte)39, (byte)59, false, false, false)]
    public void A_bot_losing_to_two_breaks_off(int monstersOnBot, bool addFight, bool oneMonster, byte health,
        byte targetHealth, bool addControlReady, bool outruns, bool breaksOff) =>
        Assert.That(PlayerBotDriver.BreaksOff(monstersOnBot, addFight, oneMonster, health, targetHealth, addControlReady,
            outruns), Is.EqualTo(breaksOff));

    // #167 D1: one monster breaks the bot off (OneMonsterCase) when it is a name the bot avoids (a death's or a
    // break-off's, DEATHS.md A3: name avoidance stopped the pull, not the monster that aggroed; 31 of st-deathsC's
    // deaths), unless the bot was sent to a camp of such names (huntAvoided); or the bot is a caster losing to it with
    // the target above 25% (the caster trap, 41 deaths); #167 E-L: or a bot that is no caster losing the race to it
    // (losingRace; a caster's target at 25% or less is C2's killing nuke's). Not while a hold on its target is ready
    // (holdReady): the hold first.
    [TestCase(PlayerBotRole.Melee, true, false, (byte)60, false, false, true)]
    [TestCase(PlayerBotRole.Melee, true, true, (byte)60, false, false, false)]      // sent to a camp of names it avoids
    [TestCase(PlayerBotRole.Melee, false, false, (byte)60, false, false, false)]
    [TestCase(PlayerBotRole.Healer, false, false, (byte)90, false, false, false)]
    [TestCase(PlayerBotRole.Caster, false, false, (byte)26, false, false, true)]
    [TestCase(PlayerBotRole.Caster, false, false, (byte)25, false, false, false)]
    [TestCase(PlayerBotRole.Caster, false, false, (byte)60, true, false, false)]    // the hold first
    [TestCase(PlayerBotRole.Caster, true, false, (byte)20, true, false, false)]
    [TestCase(PlayerBotRole.Melee, false, false, (byte)60, false, true, true)]      // #167 E-L: the race lost
    [TestCase(PlayerBotRole.Healer, false, false, (byte)60, false, true, true)]
    [TestCase(PlayerBotRole.Pet, false, false, (byte)60, false, true, true)]
    [TestCase(PlayerBotRole.Archer, false, false, (byte)20, false, true, true)]
    [TestCase(PlayerBotRole.Caster, false, false, (byte)20, false, true, false)]    //   a caster's: C2's killing nuke instead
    [TestCase(PlayerBotRole.Melee, false, false, (byte)60, true, true, false)]      //   the hold still first
    [TestCase(PlayerBotRole.Melee, true, true, (byte)60, false, true, true)]        //   in a camp of avoided names too
    public void One_monster_breaks_the_bot_off_for_a_name_it_avoids_a_caster_losing_or_a_race_lost(PlayerBotRole role,
        bool avoidedOnBot, bool huntAvoided, byte targetHealth, bool holdReady, bool losingRace, bool oneMonster) =>
        Assert.That(PlayerBotDriver.OneMonsterCase(role, avoidedOnBot, huntAvoided, targetHealth, holdReady, losingRace),
            Is.EqualTo(oneMonster));

    // #167 D1: a caster spent (CasterSpent): no damage spell it can pay for, its target held and above 25%. 2 of
    // st-deathsC's out-of-power deaths waited out a root at range, 190 and 168 waits.
    [TestCase(false, true, (byte)60, true)] [TestCase(true, true, (byte)60, false)] [TestCase(false, false, (byte)60, false)]
    [TestCase(false, true, (byte)25, false)]
    public void A_caster_with_no_nuke_it_can_pay_for_and_its_target_held_is_spent(bool canNuke, bool held, byte targetHealth,
        bool spent) =>
        Assert.That(PlayerBotDriver.CasterSpent(canNuke, held, targetHealth), Is.EqualTo(spent));

    // #167 D1: a caster's hold is ready (HoldReady) while its target is held, or with a control CasterStep would cast at it
    // now (TargetControl): in melee with hits interrupting it, only with Quickcast ready or on, as CasterAction casts one.
    [TestCase(true, false, true, false, false, true)]
    [TestCase(false, false, false, false, false, false)]
    [TestCase(false, true, false, false, false, true)]
    [TestCase(false, true, true, false, false, false)]
    [TestCase(false, true, true, true, false, true)]
    [TestCase(false, true, true, false, true, true)]
    public void A_casters_hold_is_ready_while_held_or_with_a_control_it_would_cast_now(bool held, bool hasControl,
        bool interrupted, bool quickcastReady, bool quickcastOn, bool ready) =>
        Assert.That(PlayerBotDriver.HoldReady(held, hasControl, interrupted, quickcastReady, quickcastOn), Is.EqualTo(ready));

    // The review of D (D1): the hold first while the control's own cast is under way. TargetControl reads none until the
    // cast's margin is over (NextControlTick), so the one-monster case fired mid-cast and the break-off's Disengage
    // cancelled the root (CastingComponent.InterruptCasting). The bot's cast now, a control at that monster
    // (ControlCastAt), is its hold ready.
    [Test]
    public void A_control_cast_at_the_monster_is_one_under_way()
    {
        GameNPC monster = LiveMonster(), other = LiveMonster();
        Assert.That(PlayerBotDriver.ControlCastAt(Cripple, monster, monster), Is.True);
        Assert.That(PlayerBotDriver.ControlCastAt(Cripple, other, monster), Is.False, "at another monster");
        Assert.That(PlayerBotDriver.ControlCastAt(Damage("DirectDamage", 10, 40), monster, monster), Is.False, "a nuke");
        Assert.That(PlayerBotDriver.ControlCastAt(null, null, monster), Is.False, "no cast");
    }

    // The one-monster case's holdReady (OneMonsterHold): a control cast at the one monster on the bot under way; else,
    // only when that monster is the bot's target (at a kill, the one on it), its target's hold ready (HoldReady, asked
    // only then). The review of D: a held target beside another monster hitting the bot held the break-off back.
    [TestCase(true, false, false, true)]                                     // the root under way
    [TestCase(false, true, true, true)] [TestCase(false, true, false, false)]
    [TestCase(false, false, true, false)]                                    // its held target isn't the one on it
    public void The_one_monster_case_reads_the_hold_of_the_monster_on_the_bot(bool controlCasting, bool itsTarget,
        bool targetHoldReady, bool ready) =>
        Assert.That(PlayerBotDriver.OneMonsterHold(controlCasting, itsTarget, () => targetHoldReady), Is.EqualTo(ready));

    // The target's hold walks the spells (TargetControl): not asked for another monster or with a control under way.
    [Test]
    public void The_target_hold_is_not_asked_for_another_monster_or_with_a_control_under_way()
    {
        static bool Asked() => throw new AssertionException("asked");
        Assert.That(PlayerBotDriver.OneMonsterHold(true, true, Asked), Is.True);
        Assert.That(PlayerBotDriver.OneMonsterHold(false, false, Asked), Is.False);
    }

    // A resisted root's margin before CasterStep casts it again (NextControlTick, ControlMarginMilliseconds): the control
    // it would cast is read at the margin's end, so the hold stays first then too.
    [TestCase(10_000L, 10_500L, 10_500L)] [TestCase(10_000L, 9_000L, 10_000L)] [TestCase(10_000L, 0L, 10_000L)]
    public void The_control_to_come_is_read_at_the_end_of_the_casts_margin(long now, long nextControlTick, long at) =>
        Assert.That(PlayerBotDriver.ControlReadTick(now, nextControlTick), Is.EqualTo(at));

    private static bool HoldReadyNow(PlayerBotState state, GamePlayer bot, params GameNPC[] onBot) =>
        (bool)typeof(PlayerBotDriver).GetMethod("HoldReadyNow", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state, bot, PlayerBotRole.Caster, onBot.ToList(), 10_000L])!;

    // A bot with a casting component, casting spell at target (none: not casting).
    private static GamePlayer CastingBot(Spell spell = null, GameLiving target = null)
    {
        GamePlayer bot = Player(null);
        bot.castingComponent = CastingComponent.Create(bot);
        if (spell != null)
            typeof(CastingComponent).GetProperty("SpellHandler")!.SetValue(bot.castingComponent,
                new DOL.GS.Spells.SpellHandler(bot, spell, BodyLine) { Target = target });
        return bot;
    }

    // Where StartsBreakOff reads it (HoldReadyNow), the review's case: a caster in melee, its target at 70% and not
    // held, its root cast at it and not landed yet: the hold is ready, no break-off.
    [Test]
    public void A_casters_root_under_way_holds_the_one_monster_break_off_back()
    {
        GameNPC target = LiveMonster(70);
        var state = new PlayerBotState(Player(null)) { Target = target };
        Assert.That(HoldReadyNow(state, CastingBot(Cripple, target), target), Is.True);
    }

    // The review of D: a target rooted at range (no longer on the bot: AttackerTracker drops it after its attack
    // interval) beside one add hitting the bot under 40%: the target's hold is no hold on the add, and the one-monster
    // case still fires. The add's own control to come is C4's (AddControlReady); one cast at it under way is its hold.
    [Test]
    public void A_held_target_is_no_hold_on_another_monster_hitting_the_bot()
    {
        GameNPC target = LiveMonster(70), add = LiveMonster();
        Effects(target)[eEffect.MovementSpeedDebuff] = [SpellEffect(target, Player(null), Cripple, 9_000)];
        var state = new PlayerBotState(Player(null)) { Target = target };
        Assert.That(HoldReadyNow(state, CastingBot(), add), Is.False, "an add on it, the target held at range");
        Assert.That(HoldReadyNow(state, CastingBot(), target), Is.True, "its held target on it");
        Assert.That(HoldReadyNow(state, CastingBot(Cripple, add), add), Is.True, "the add's root under way");
        Assert.That(HoldReadyNow(state, CastingBot(Cripple, target), add), Is.False, "a root at the target, not the add");
        Assert.That(HoldReadyNow(state, CastingBot(), target, add), Is.False, "two on it: no one-monster case");
    }

    // #167 D1: BREAKOFF why=, the rule that fired: spent, adds (2 or more on it), atkill (the second-monster case),
    // followup (the fight after an add kill), avoided (one monster of a name it avoids), caster (a caster losing to one
    // with its target above 25%) or (#167 E-L) race (a bot that is no caster losing the race to one).
    [TestCase(true, 1, false, false, false, false, false, "spent")]
    [TestCase(false, 2, true, true, true, false, true, "adds")]
    [TestCase(false, 1, true, true, false, false, false, "atkill")]
    [TestCase(false, 1, false, true, true, false, false, "followup")]
    [TestCase(false, 1, false, false, true, false, true, "avoided")]
    [TestCase(false, 1, false, false, true, true, true, "caster")]               // a camp of avoided names: the caster arm
    [TestCase(false, 1, false, false, false, false, true, "caster")]
    [TestCase(false, 1, false, false, false, false, false, "race")]              // #167 E-L
    [TestCase(false, 1, false, false, true, true, false, "race")]                //   a camp of avoided names, no caster
    public void A_break_off_says_which_rule_fired(bool spent, int monstersOnBot, bool atKill, bool followUp,
        bool avoidedOnBot, bool huntAvoided, bool casterArm, string why) =>
        Assert.That(PlayerBotDriver.BreakOffWhy(spent, monstersOnBot, atKill, followUp, avoidedOnBot, huntAvoided, casterArm),
            Is.EqualTo(why));

    // #167 D1 where StartsBreakOff applies it (BreakOffFires): one monster of a name the bot avoids, not in a camp of
    // them; a caster losing to one with no hold ready.
    [Test]
    public void One_monster_of_an_avoided_name_or_on_a_losing_caster_fires_the_break_off()
    {
        var state = new PlayerBotState(Player(null));
        Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Melee, false, 39, [(80, 150, true)], 25_000,
            () => false), Is.True, "an avoided name");
        state.HuntAvoided = true;
        Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Melee, false, 39, [(80, 150, true)], 25_000,
            () => false), Is.False, "sent to a camp of them");
        state.HuntAvoided = false;
        Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Melee, false, 39, [(80, 150, false)], 25_000,
            () => false), Is.False, "one monster, no case");
        Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Caster, false, 39, [(80, 150, false)], 25_000,
            () => false), Is.True, "a caster losing, no hold ready");
        Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Caster, true, 39, [(80, 150, false)], 25_000,
            () => false), Is.False, "the hold first");
        Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Caster, false, 39, [(25, 150, false)], 25_000,
            () => false), Is.False, "its target nearly down");
    }

    // #167 E-L where StartsBreakOff applies it (BreakOffFires, BreakOffStartWhy): one monster, a bot that is no caster,
    // engaged with its target alive, losing the race read since the first hit on it; not with the target dying first,
    // not before 20 health lost since that hit, not out of its fight, and not for a caster (its target at 25% or less is
    // C2's killing nuke's; above, D1's caster arm).
    [Test]
    public void A_bot_losing_the_race_to_one_monster_breaks_off_unless_it_is_a_caster()
    {
        var calculators = (DOL.GS.PropertyCalc.IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        DOL.GS.PropertyCalc.IPropertyCalculator previous = calculators[(int)eProperty.MaxHealth];
        calculators[(int)eProperty.MaxHealth] = new Fixed(100);                       // the target's HealthPercent
        try
        {
            var state = new PlayerBotState(Player(null))
                { Engaged = true, Target = LiveMonster(60), FightHpAtReach = 100, FightTargetHpAtReach = 100 };
            bool Fires(PlayerBotRole role) =>
                PlayerBotDriver.BreakOffFires(state, role, false, 39, [(60, 150, false)], 25_000, () => false);
            Assert.That(Fires(PlayerBotRole.Melee), Is.True, "35 left at its death: the race lost");
            Assert.That(Fires(PlayerBotRole.Healer), Is.True);
            Assert.That(Fires(PlayerBotRole.Pet), Is.True);
            Assert.That(PlayerBotDriver.BreakOffStartWhy(state, PlayerBotRole.Archer, false, 39, false, [(60, 150, false)],
                25_000, () => false), Is.EqualTo(PlayerBotDriver.RaceLost));
            state.FightHpAtReach = 55;
            Assert.That(Fires(PlayerBotRole.Melee), Is.False, "16 lost since the first hit: no read yet");
            state.FightHpAtReach = 100;
            state.Target = LiveMonster(20);
            Assert.That(Fires(PlayerBotRole.Melee), Is.False, "its target dies first: -31 left");
            state.FightTargetHpAtReach = 25;
            Assert.That(Fires(PlayerBotRole.Melee), Is.True, "20 and losing: 17 left at its death");
            Assert.That(Fires(PlayerBotRole.Caster), Is.False, "a caster's target at 20: C2's killing nuke, no race");
            state.Target = LiveMonster(60);
            state.FightTargetHpAtReach = 100;
            state.Engaged = false;
            Assert.That(Fires(PlayerBotRole.Melee), Is.False, "not in its fight: no race");
            (state.Engaged, state.Target) = (true, LiveMonster(0));                     // at a kill, an add at 100% on it
            Assert.That(state.Target.IsAlive, Is.False);
            Assert.That(PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Melee, false, 35, [(100, 100, false)], 25_000,
                () => false), Is.False, "its target dead: the race read since the first hit was the target's, not the add's");
        }
        finally
        {
            calculators[(int)eProperty.MaxHealth] = previous;
        }
    }

    // The review of D (D1): StartsBreakOff's own rule. It scans the monsters on the bot under 40% health only, a spent
    // caster at any health (BreakOffScans): with spent dropped from both of its arms no test failed, and a spent caster
    // at full health never broke off.
    [TestCase(true, (byte)100, true)] [TestCase(false, (byte)100, false)] [TestCase(false, (byte)39, true)]
    [TestCase(false, (byte)40, false)] [TestCase(true, (byte)40, true)]
    public void The_break_off_scans_under_40_percent_or_for_a_spent_caster(bool spent, byte health, bool scans) =>
        Assert.That(PlayerBotDriver.BreakOffScans(spent, health), Is.EqualTo(scans));

    // Once it scanned (BreakOffStartWhy): a spent caster breaks off at any health, why=spent, with no monster on it too;
    // else the rule (BreakOffFires) with its why= (BreakOffWhy); none: null.
    [Test]
    public void A_spent_caster_at_full_health_breaks_off_and_says_spent()
    {
        var state = new PlayerBotState(Player(null)) { Engaged = true };
        string Why(PlayerBotRole role, bool spent, byte health, bool holdReady, params (byte, int, bool)[] hitters) =>
            PlayerBotDriver.BreakOffStartWhy(state, role, spent, health, holdReady, hitters, 25_000, () => false);
        Assert.That(Why(PlayerBotRole.Caster, true, 100, false, (80, 150, false)), Is.EqualTo(PlayerBotDriver.Spent));
        Assert.That(Why(PlayerBotRole.Caster, true, 100, false), Is.EqualTo(PlayerBotDriver.Spent), "its target held at range");
        Assert.That(Why(PlayerBotRole.Caster, false, 100, false, (80, 150, false)), Is.Null, "not spent: the 40%");
        Assert.That(Why(PlayerBotRole.Caster, false, 39, false, (80, 150, false)), Is.EqualTo("caster"));
        Assert.That(Why(PlayerBotRole.Caster, false, 39, true, (80, 150, false)), Is.Null, "the hold first");
        Assert.That(Why(PlayerBotRole.Melee, false, 39, false, (80, 150, true)), Is.EqualTo("avoided"));
        Assert.That(Why(PlayerBotRole.Melee, false, 39, false, (80, 150, true), (80, 150, false)), Is.EqualTo("adds"));
    }

    // #167 D1: a spent caster's break-off runs, then rests: it bars no camp, avoids no name and owes no relocation (the
    // power was the problem, not the camp).
    [Test]
    public void A_spent_casters_break_off_bars_nothing_and_owes_a_rest_not_a_relocation()
    {
        var state = new PlayerBotState(Player(null)) { AnchorRegion = 51, Anchor = new(3_000, 0, 0),
            BreakOffWhy = PlayerBotDriver.Spent, BreakOffSince = 2_000 };
        state.RoamDestination = state.Anchor;
        PlayerBotDriver.BrokeOffFrom(state, 51, new Vector3(1_000, 0, 0), ["wolf"], 2_000);
        Assert.That((PlayerBotDriver.FailedCamps(state.FailedCamps, 51, 2_001).Length,
            PlayerBotDriver.IsAvoided(state.AvoidUntil, "wolf", 2_001)), Is.EqualTo((0, false)));
        PlayerBotDriver.FinishBreakOff(state, PlayerBotDriver.Shook, owesReplan: true, 7_000);
        Assert.That((state.ReplanOwed, state.RecoverBeforePull, state.BreakOffWhy), Is.EqualTo(((string)null, true,
            (string)null)));
        var losing = new PlayerBotState(Player(null)) { BreakOffWhy = "caster", BreakOffSince = 2_000 };
        PlayerBotDriver.BrokeOffFrom(losing, 51, new Vector3(1_000, 0, 0), ["wolf"], 2_000);
        PlayerBotDriver.FinishBreakOff(losing, PlayerBotDriver.Shook, owesReplan: true, 7_000);
        Assert.That((PlayerBotDriver.IsAvoided(losing.AvoidUntil, "wolf", 2_001), losing.ReplanOwed),
            Is.EqualTo((true, PlayerBotDriver.BrokeOff)), "any other break-off as before");
    }

    // It outruns a chaser no faster than its run (191, GamePlayer.cs:8250; enable_pve_speed is off in the world DB), or
    // one under its sprint (1.3 x 191 = 248, MaxSpeedCalculator.cs:25) while the sprint's lead, 57 units a second, holds
    // it off for the 25 s a monster chases after its last swing (StandardMobState.cs:89, :121): S x 57 >= 25 x (v - 191).
    // The review of 9aacd69: either way the sprint's lead must clear the chaser's melee reach first, S x (248 - v) above
    // 200 (MeleeAttackRange, GameLiving.cs:303), and it needs a sprint (the old canSprint). With none (endurance 10 or
    // less) the bot turned its back on a 191 or 185 chaser that started in its reach, which swung at its back for 30 s; a
    // 247 one gains 99 units on a 99-s sprint, a 200 one 192 on a 4-s one.
    [TestCase(191, 0, false)] [TestCase(185, 0, false)] [TestCase(150, 0, false)] [TestCase(191, 4, true)]
    [TestCase(191, 3, false)] [TestCase(150, 3, true)] [TestCase(150, 2, false)]
    [TestCase(220, 13, true)] [TestCase(220, 12, false)]
    [TestCase(248, 99, false)] [TestCase(247, 99, false)] [TestCase(200, 5, true)] [TestCase(200, 4, false)]
    [TestCase(200, 3, false)] [TestCase(200, int.MaxValue, true)]
    public void A_bot_outruns_a_chaser_while_its_sprint_lasts(int fastestSpeed, int sprintSeconds, bool outruns) =>
        Assert.That(PlayerBotDriver.Outruns(fastestSpeed, sprintSeconds, rangedHarm: false), Is.EqualTo(outruns));

    // #167 D2: no run from a chaser that harms from range (RangedHarm: a snare, damage, a lifedrain, a stun or a mez at
    // 1,000 or more): all 6 of st-deathsC's break-offs from monsters with a 1,500-range snare, DoT or lifedrain died
    // (grovewood, rainbow sprite; 4 of them one bot's loop), against 4 of the other 58.
    [Test]
    public void No_break_off_from_a_chaser_that_harms_from_range()
    {
        Assert.That(PlayerBotDriver.RangedHarm([(eSpellType.SpeedDecrease, 1_500)]), Is.True);
        Assert.That(PlayerBotDriver.RangedHarm([(eSpellType.Lifedrain, 1_500)]), Is.True);
        Assert.That(PlayerBotDriver.RangedHarm([(eSpellType.DirectDamage, 700), (eSpellType.Stun, 700)]), Is.False);
        Assert.That(PlayerBotDriver.RangedHarm([(eSpellType.DamageShield, 0), (eSpellType.HealOverTime, 0)]), Is.False);
        Assert.That(PlayerBotDriver.RangedHarm([]), Is.False);
        Assert.That((PlayerBotDriver.Outruns(191, 4, true), PlayerBotDriver.Outruns(191, 4, false),
            PlayerBotDriver.Outruns(150, 3, true)), Is.EqualTo((false, true, false)));
    }

    // #167 D2: a break-off that would fire but doesn't says why, in StartsBreakOff's order (BREAKOFF outcome=refused, once
    // a fight): an add's control first (C4), a chaser as fast as the sprint, a sprint too short, a chaser that harms from
    // range, nowhere to run.
    [TestCase(false, false, 191, 4, false, true, null)]
    [TestCase(true, true, 191, 4, false, true, "control")]
    [TestCase(true, false, 248, 99, false, true, "speed")]
    [TestCase(true, false, 191, 3, false, true, "sprint")]
    [TestCase(true, false, 191, 4, true, true, "ranged")]
    [TestCase(true, false, 191, 4, false, false, "point")]
    [TestCase(true, false, 191, 4, false, true, null)]
    public void A_break_off_that_would_fire_says_why_it_was_refused(bool fires, bool controlReady, int fastest,
        int sprintSeconds, bool ranged, bool hasPoint, string why) =>
        Assert.That(PlayerBotDriver.BreakOffRefusal(fires, controlReady, fastest, sprintSeconds, ranged, hasPoint),
            Is.EqualTo(why));

    // The sprint's seconds, at the least, from the bot's endurance and its real cost (GamePlayer.cs:2393-2413): each
    // second's regeneration (no base while moving or in combat, ClassicRestRegeneration.cs:12-15: buffs and items only)
    // less the sprint's 5 (less Long Wind's), scaled by the fatigue consumption above 1%; the sprint ends at 5 endurance
    // or less (GamePlayer.cs:2420-2423, SprintECSEffect.cs) and doesn't start at 10 or less (GamePlayer.cs:8633). A cost
    // of 0 or less never ends it.
    [TestCase(100, 0, 100, 5, 18)] [TestCase(11, 0, 100, 5, 1)] [TestCase(10, 0, 100, 5, 0)] [TestCase(50, 0, 100, 5, 8)]
    [TestCase(100, 2, 100, 5, 31)] [TestCase(100, 0, 50, 5, 47)] [TestCase(100, 5, 100, 5, int.MaxValue)]
    [TestCase(100, 0, 100, 4, 23)]
    public void A_sprint_lasts_while_the_endurance_pays_for_it(int endurance, int regeneration, int fatigueConsumption,
        int longWind, int seconds) =>
        Assert.That(PlayerBotDriver.SprintSeconds(endurance, regeneration, fatigueConsumption, longWind), Is.EqualTo(seconds));

    // #167 E-R: the least endurance whose sprint outruns a chaser (Outruns over SprintSeconds): 4 s of sprint against one
    // no faster than the run (26), 5 against 192-200 (31), 6 against 203-204 (36), 7 against 205 (41); none at 220 within
    // 41. Long Wind's 1 a second less makes the 4 s cost 16 (22); a 150 chaser needs 3 s (21).
    [TestCase(191, 5, 26)] [TestCase(200, 5, 31)] [TestCase(204, 5, 36)] [TestCase(205, 5, 41)]
    [TestCase(220, 5, int.MaxValue)] [TestCase(191, 4, 22)] [TestCase(150, 5, 21)]
    public void The_escape_endurance_is_the_least_sprint_that_outruns_the_chaser(int fastest, int longWind, int endurance) =>
        Assert.That(PlayerBotDriver.EscapeEndurance(fastest, 0, 100, longWind), Is.EqualTo(endurance));

    // #167 E-R: a close fight keeps that endurance and 5 more, read at the run's 191 at the least (a choice, not the
    // arithmetic: a 150-180 chaser needs 21, and the floor holds 5 more); none against 205 and faster (41 + 5 is past 41:
    // the two styles it costs buy no run).
    [TestCase(150, 31)] [TestCase(191, 31)] [TestCase(200, 36)] [TestCase(204, 41)] [TestCase(205, 0)] [TestCase(248, 0)]
    public void A_close_fight_keeps_a_runs_endurance_and_a_margin(int fastest, int reserve) =>
        Assert.That(PlayerBotDriver.EscapeReserve(fastest, 0, 100, PlayerBotDriver.LongWind), Is.EqualTo(reserve));

    // #167 E-R: a style's cost as StyleProcessor charges it (StyleProcessor.cs:460-479): speed x cost / 40, 1 more under
    // speed 40, by the fatigue consumption, 1 at the least; a Battlemaster style 30 x cost / 40 at any speed.
    [TestCase(37, 20, 100, false, 19)] [TestCase(45, 20, 100, false, 22)] [TestCase(45, 20, 50, false, 11)]
    [TestCase(30, 2, 100, false, 2)] [TestCase(45, 20, 100, true, 15)] [TestCase(10, 1, 100, false, 1)]
    public void A_styles_endurance_cost_is_the_servers(int weaponSpeed, int styleCost, int fatigue, bool battlemaster,
        int cost) =>
        Assert.That(PlayerBotDriver.StyleEnduranceCost(weaponSpeed, styleCost, fatigue, battlemaster), Is.EqualTo(cost));

    // #167 E-R: a style goes only when it leaves the reserve (with none, when the bot can pay for it).
    [TestCase(60, 20, 36, true)] [TestCase(55, 20, 36, false)] [TestCase(56, 20, 36, true)] [TestCase(20, 19, 0, true)]
    [TestCase(10, 19, 0, false)]
    public void A_style_keeps_the_reserve(int endurance, int cost, int reserve, bool keeps) =>
        Assert.That(PlayerBotDriver.KeepsReserve(endurance, cost, reserve), Is.EqualTo(keeps));

    // #167 E-R, the review's seam for QueueUsableStyle's choice: of the usable styles by level, highest first (costs), the
    // highest-level one that leaves the reserve; with no reserve, today's pick, the highest whatever it costs (the server
    // drops one it can't pay for at the swing, PlayerAttackAction.cs:36-42); none, -1. FIGHT styleHeld= counts a choice
    // that went unstyled while the reserve held back a style the bot could pay for (held), not one it couldn't pay.
    [TestCase(new[] { 22, 15, 11 }, 60, 36, 0, false)]
    [TestCase(new[] { 22, 15, 11 }, 55, 36, 1, false)]                    // a cheaper style keeps it
    [TestCase(new[] { 22, 15, 11 }, 45, 36, -1, true)]                    // held: the swing goes unstyled
    [TestCase(new[] { 22, 15, 11 }, 10, 36, -1, false)]                   // none it could pay for: not held
    [TestCase(new[] { 22, 15 }, 10, 0, 0, false)]                         // no reserve: today's pick
    [TestCase(new[] { 22, 15 }, 50, 0, 0, false)]
    [TestCase(new int[0], 80, 0, -1, false)] [TestCase(new int[0], 80, 36, -1, false)]
    public void The_style_is_the_highest_that_leaves_the_reserve(int[] costs, int endurance, int reserve, int index,
        bool held) =>
        Assert.That(PlayerBotDriver.StyleWithin(costs, endurance, reserve), Is.EqualTo((index, held)));

    // #167 E-R: a hybrid's damage cast (5 endurance, SpellHandler.cs:1442-1445) waits when it would take the bot under the
    // reserve; a caster's or a pet class's never does, nor any with no reserve kept.
    [TestCase(PlayerBotRole.Melee, 41, 36, false)] [TestCase(PlayerBotRole.Melee, 40, 36, true)]
    [TestCase(PlayerBotRole.Healer, 40, 36, true)] [TestCase(PlayerBotRole.Archer, 40, 36, true)]
    [TestCase(PlayerBotRole.Caster, 10, 36, false)] [TestCase(PlayerBotRole.Pet, 10, 36, false)]
    [TestCase(PlayerBotRole.Melee, 3, 0, false)]
    public void A_hybrids_damage_cast_keeps_the_reserve(PlayerBotRole role, int endurance, int reserve, bool holds) =>
        Assert.That(PlayerBotDriver.HoldsCast(role, endurance, reserve), Is.EqualTo(holds));

    // #167 E-R: the bot's health at its target's death on the race since the first hit on it; no read under 20 lost; a
    // target that lost nothing, never.
    [TestCase((byte)60, (byte)50, 100, 100, 20)] [TestCase((byte)85, (byte)50, 100, 100, null)]
    [TestCase((byte)60, (byte)100, 100, 100, int.MinValue)] [TestCase((byte)39, (byte)60, 100, 100, -52)]
    [TestCase((byte)60, (byte)50, -1, -1, null)]
    public void The_race_projects_the_bots_health_at_the_kill(byte health, byte targetHealth, int hpAtReach,
        int targetHpAtReach, int? left) =>
        Assert.That(PlayerBotDriver.HealthLeftAtKill(health, targetHealth, hpAtReach, targetHpAtReach), Is.EqualTo(left));

    // #167 E-R: the reserve is kept in a close fight only: 2 or more on the bot now or in the fight, a defence or the
    // fight after an add kill, or the race projecting it under 30 at its target's death.
    [TestCase(2, 2, false, null, true)] [TestCase(1, 2, false, null, true)] [TestCase(1, 1, true, null, true)]
    [TestCase(1, 1, false, null, false)] [TestCase(1, 1, false, 29, true)] [TestCase(1, 1, false, 30, false)]
    [TestCase(0, 0, false, int.MinValue, true)]
    public void Only_a_close_fight_keeps_the_reserve(int monstersOnBot, int fightAdds, bool defence, int? leftAtKill,
        bool close) =>
        Assert.That(PlayerBotDriver.CloseFight(monstersOnBot, fightAdds, defence, leftAtKill), Is.EqualTo(close));

    // #167 E-R, the review's change: the reserve is a fight's only. Engage's cast gate runs on the walk in and the first
    // think in reach, before OpenFight's resets: out of a fight it read the last fight's adds and race, and cached them for
    // a second. In a fight it is read once a StyleMilliseconds (Reserve, NextReserveTick).
    [Test]
    public void The_reserve_is_none_out_of_a_fight_and_read_once_a_second_in_one()
    {
        MethodInfo reserveNow = typeof(PlayerBotDriver).GetMethod("ReserveNow", BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = new PlayerBotState(Player(null)) { FromDefence = true, FightAdds = 2, Reserve = 31, NextReserveTick = 10_500 };
        Assert.That(reserveNow.Invoke(null, [state, Player(null), null, 10_000L]), Is.Zero, "not engaged: none");
        Assert.That(state.Reserve, Is.EqualTo(31), "and no read kept");
        state.Engaged = true;
        Assert.That(reserveNow.Invoke(null, [state, Player(null), null, 10_000L]), Is.EqualTo(31), "the last read's");
    }

    // Where it runs: ahead of straight away from its chasers (a dot product above 0, so never square to them), out of
    // every other monster's reach, the farthest from the nearest chaser; none, null.
    [Test]
    public void A_break_off_runs_ahead_away_from_the_chasers_and_clear_of_the_adds()
    {
        Vector3 here = Vector3.Zero;
        Vector3[] chasers = [new(300, 0, 0)];
        Vector3[] candidates = [new(-1_500, 0, 0), new(1_500, 0, 0), new(0, 1_500, 0), new(-1_061, 1_061, 0)];
        var none = new List<(string, Vector3, int)>();
        Assert.That(PlayerBotDriver.BreakOffPoint(here, chasers, candidates, none), Is.EqualTo(new Vector3(-1_500, 0, 0)));
        var add = new List<(string, Vector3, int)> { ("add", new(-1_500, 0, 0), 500) };
        Assert.That(PlayerBotDriver.BreakOffPoint(here, chasers, candidates, add), Is.EqualTo(new Vector3(-1_061, 1_061, 0)));
        Assert.That(PlayerBotDriver.BreakOffPoint(here, chasers, [new Vector3(0, 1_500, 0), new Vector3(1_500, 0, 0)], none),
            Is.Null, "square to the chaser, or toward it");
        var both = new List<(string, Vector3, int)> { ("a", new(-1_500, 0, 0), 500), ("b", new(-1_061, 1_061, 0), 500) };
        Assert.That(PlayerBotDriver.BreakOffPoint(here, chasers, candidates, both), Is.Null, "every point ahead in reach");
        Vector3[] around = [new(-1_500, 0, 0), new(0, -1_500, 0), new(-1_061, -1_061, 0), new(-1_061, 1_061, 0)];
        Assert.That(PlayerBotDriver.BreakOffPoint(here, [new Vector3(300, 0, 0), new Vector3(0, 300, 0)], around, none),
            Is.EqualTo(new Vector3(-1_061, -1_061, 0)), "two chasers: straight away from their middle");
    }

    // The review of 9aacd69: a point on another mesh island (across water, up a cliff) blocked the step at once, and the
    // next think chose the same point from the same spot, again and again for 30 s, while the monsters hit the bot. Now
    // the points it was blocked on (and those within 100 units of one) are left out, and a point is taken only with a walk
    // there (the corridor test, walkable); one without is blocked too. Blocked twice here, then none is left: it fights.
    [Test]
    public void A_break_off_leaves_out_the_points_it_was_blocked_on_and_those_with_no_walk()
    {
        Vector3 here = Vector3.Zero, first = new(-1_500, 0, 0), second = new(-1_061, 1_061, 0), third = new(-1_061, -1_061, 0);
        Vector3[] chasers = [new(300, 0, 0)];
        Vector3[] candidates = [first, second, third, new(1_500, 0, 0)];
        var none = new List<(string, Vector3, int)>();
        var blocked = new List<Vector3>();
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, blocked, _ => true), Is.EqualTo(first));
        blocked.Add(first);                                                             // its step there blocked
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, blocked, _ => true), Is.EqualTo(second));
        blocked.Add(second);                                                            // and the next one's
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, blocked, _ => true), Is.EqualTo(third));
        blocked.Add(third);
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, blocked, _ => true), Is.Null,
            "none ahead left: it fights at once");
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, [new Vector3(-1_480, 10, 0)], _ => true),
            Is.EqualTo(second), "near a blocked one");
        var tested = new List<Vector3>();
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, tested, point => point.Y > 0),
            Is.EqualTo(second), "no walk to the farthest");
        Assert.That(tested, Is.EqualTo(new[] { first }), "the point with no walk is left out after");
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, candidates, none, new List<Vector3>(), _ => false), Is.Null,
            "no walk anywhere");
    }

    // #167 D2: with no ring point to run to, the bot ran no more and fought (6 of st-deathsC's 13 caught runs). Now the
    // point straight away from its chasers' middle, 1,500 out, adds or not; none with no chaser.
    [Test]
    public void With_no_ring_point_a_break_off_runs_straight_away_from_its_chasers()
    {
        Vector3 here = Vector3.Zero;
        Assert.That(PlayerBotDriver.StraightAwayPoint(here, [new Vector3(300, 0, 0)], 1_500), Is.EqualTo(new Vector3(-1_500, 0, 0)));
        Vector3? two = PlayerBotDriver.StraightAwayPoint(here, [new Vector3(300, 0, 0), new Vector3(0, 300, 0)], 1_500);
        Assert.That((MathF.Round(two!.Value.X), MathF.Round(two.Value.Y)), Is.EqualTo((-1_061f, -1_061f)));
        Assert.That(PlayerBotDriver.StraightAwayPoint(here, [], 1_500), Is.Null);
    }

    // The verifier's correction (the island loop of 9aacd69): the straight-away point passes the break-off's own walk test
    // (walkable: the corridor) and leaves out the points it was blocked on; one try, and one with no walk is blocked too,
    // so the next try fights. On the navmesh first (onMesh), none there: none.
    [Test]
    public void The_straight_away_point_is_tried_once_on_the_mesh_with_a_walk_and_never_where_it_was_blocked()
    {
        Vector3 here = Vector3.Zero, away = new(-1_500, 0, 0);
        Vector3[] chasers = [new(300, 0, 0)];
        Vector3? OnMesh(Vector3 p) => p;
        Assert.That(PlayerBotDriver.StraightAwayGoal(here, chasers, 1_500, OnMesh, new List<Vector3>(), _ => true),
            Is.EqualTo(away));
        Assert.That(PlayerBotDriver.StraightAwayGoal(here, chasers, 1_500, OnMesh, [new Vector3(-1_490, 5, 0)], _ => true),
            Is.Null, "blocked on it already: it fights");
        var blocked = new List<Vector3>();
        Assert.That(PlayerBotDriver.StraightAwayGoal(here, chasers, 1_500, OnMesh, blocked, _ => false), Is.Null, "no walk");
        Assert.That(blocked, Is.EqualTo(new[] { away }), "and never again");
        Assert.That(PlayerBotDriver.StraightAwayGoal(here, chasers, 1_500, _ => null, new List<Vector3>(), _ => true), Is.Null,
            "off the mesh");
    }

    // #167 E T1: with no point on its rings (1,500 and out, every one in another monster's reach here) nor straight away,
    // a break-off tries 800 out: the ring's point ahead of the chasers and out of every other monster's reach
    // (NextBreakOffPoint), else the point straight away 800 out (StraightAwayGoal), never one in an add's reach.
    // st-deathsD2/D2b: 11 of 14 turns for want of a point were fatal.
    [Test]
    public void With_no_point_out_a_break_off_tries_800_out_never_in_an_adds_reach()
    {
        Vector3 here = Vector3.Zero;
        Vector3[] chasers = [new(300, 0, 0)];
        Vector3? OnMesh(Vector3 p) => p;
        Vector3[] rings = [new(-1_500, 0, 0), new(-1_061, 1_061, 0), new(-1_061, -1_061, 0)];
        var adds = rings.Select(at => ("add", at, 300)).ToList();                     // each in reach of one (300 + 200)
        Assert.That(PlayerBotDriver.NextBreakOffPoint(here, chasers, rings, adds, new List<Vector3>(), _ => true), Is.Null,
            "the rings out: every point in reach");
        Vector3[] near = [new(-800, 0, 0), new(-566, 566, 0), new(-566, -566, 0), new(800, 0, 0)];
        Assert.That(PlayerBotDriver.NearBreakOffGoal(here, chasers, near, adds, new List<Vector3>(), _ => true, OnMesh),
            Is.EqualTo(new Vector3(-800, 0, 0)), "the 800 ring is clear: its farthest from the chaser");
        var add = adds.Append(("near", new Vector3(-800, 0, 0), 300)).ToList();
        Assert.That(PlayerBotDriver.NearBreakOffGoal(here, chasers, near, add, new List<Vector3>(), _ => true, OnMesh),
            Is.EqualTo(new Vector3(-566, 566, 0)), "out of an add's reach");
        var all = add.Append(("a", new Vector3(-566, 566, 0), 300)).Append(("b", new Vector3(-566, -566, 0), 300)).ToList();
        Assert.That(PlayerBotDriver.NearBreakOffGoal(here, chasers, near, all, new List<Vector3>(), _ => true, OnMesh),
            Is.Null, "every point 800 out in reach, straight away too: none");
        Assert.That(PlayerBotDriver.NearBreakOffGoal(here, chasers, [], adds, new List<Vector3>(), _ => true, OnMesh),
            Is.EqualTo(new Vector3(-800, 0, 0)), "no ring point on the mesh: straight away, clear");
        var blocked = new List<Vector3>();
        Assert.That(PlayerBotDriver.NearBreakOffGoal(here, chasers, [], adds, blocked, _ => false, OnMesh), Is.Null, "no walk");
        Assert.That(blocked, Is.EqualTo(new[] { new Vector3(-800, 0, 0) }), "and never again");
    }

    // #167 E T1: with still no point it turns on a chaser not held in reach (within 250), when hit while it holds (a chaser
    // that harms from range, or one held but still casting, never comes within 250: the review), or at once as a caster
    // or pet class with power (7 of the 11 fatal turns opened with a gap a ranged class could use); else it holds.
    [TestCase(900, false, false, 900, false)]
    [TestCase(180, false, false, 180, true)]
    [TestCase(250, false, false, 250, true)]
    [TestCase(251, false, false, 251, false)]
    [TestCase(600, false, false, 600, false)]
    [TestCase(600, true, false, 600, true)]                                    // the chaser hit it while it held
    [TestCase(int.MaxValue, true, false, 1_500, true)]                         //   every chaser held, one harms from range
    [TestCase(int.MaxValue, false, true, 1_400, true)]                         // a caster with power, chasers far off
    [TestCase(int.MaxValue, false, true, 300, true)]                           //   or near: no 1,000 floor
    [TestCase(int.MaxValue, false, false, 1_400, false)]
    [TestCase(int.MaxValue, false, true, int.MaxValue, false)]                 // no chaser to turn on
    public void With_no_point_it_turns_only_when_caught_hit_or_a_caster_with_power(int freeDistance, bool hitNow,
        bool casterOrPetWithPower, int nearest, bool turns) =>
        Assert.That(PlayerBotDriver.NoPointTurns(freeDistance, hitNow, casterOrPetWithPower, nearest), Is.EqualTo(turns));

    // #167 E T1: hit while it holds: an attack on the bot (HitNow: within the last second, a miss too) since the hold began
    // (holdSince; 0: not holding). A hit before the hold is the run's, which CaughtNow reads.
    [TestCase(0L, 9_500L, 10_000L, false)]
    [TestCase(8_000L, 9_500L, 10_000L, true)]
    [TestCase(8_000L, 8_000L, 8_900L, true)]
    [TestCase(8_000L, 7_900L, 8_500L, false)]                                  // before the hold
    [TestCase(8_000L, 8_500L, 9_600L, false)]                                  // over a second ago
    [TestCase(8_000L, 0L, 9_000L, false)]
    public void A_hit_while_it_holds_is_one_since_the_hold_began(long holdSince, long lastHit, long now, bool hit) =>
        Assert.That(PlayerBotDriver.HitWhileHolding(holdSince, lastHit, now), Is.EqualTo(hit));

    // #167 E T1: a bot holding for want of a point heals itself there (SelfHeal, Think's step 11b, which the hold's think
    // never reaches): before T1 no point ended the break-off and the heal began in the same think, the chaser still far
    // off; held, a Healer-role bot cast nothing until it turned, then healed under melee. The heal's cast keeps it
    // (Casting; WaitsForSelfCast then waits it out); with no heal it holds (Fighting).
    [Test]
    public void A_bot_holding_for_want_of_a_point_heals_itself_there()
    {
        var state = new PlayerBotState(Player(null));
        bool asked = false;
        PlayerBotDriver.HoldForPoint(state, 5_000, PlayerBotDriver.TurnNoPoint, tried: true, () =>
        {
            asked = true;
            state.Activity = PlayerBotActivity.Casting;                         // CastOnSelf's
            return true;
        });
        Assert.That((asked, state.Activity, state.Walk, state.BreakOffHoldSince, state.BreakOffHoldWhy, state.BreakOffHolds),
            Is.EqualTo((true, PlayerBotActivity.Casting, PlayerBotWalk.BreakOff, 5_000L, PlayerBotDriver.TurnNoPoint, 1)));
        PlayerBotDriver.HoldForPoint(state, 6_000, PlayerBotDriver.TurnBlocked, tried: false, () => false);
        Assert.That((state.Activity, state.BreakOffHoldSince, state.BreakOffHoldWhy, state.BreakOffHolds),
            Is.EqualTo((PlayerBotActivity.Fighting, 5_000L, PlayerBotDriver.TurnNoPoint, 1)), "no heal: it holds");
    }

    // The break-off's step: it runs on; at its point with a chaser still after it, it takes the next point; with none
    // after it, it is done. #167 D2: caught (CaughtNow) it acts on the chaser (Caught); from 30 s, with the nearest chaser
    // beyond 1,500 it is done (a monster keeps its target 25 s after its last swing, StandardMobState.cs:122: at the old
    // 30-s cap it turned on one far behind, and 7 of the 13 caught runs died that way); at 45 s it fights.
    [TestCase(false, 2, 3_000L, 300, false, PlayerBotBreakOff.Run)]
    [TestCase(true, 1, 8_000L, 900, false, PlayerBotBreakOff.NextPoint)]
    [TestCase(false, 0, 8_000L, 0, false, PlayerBotBreakOff.Done)]
    [TestCase(true, 0, 8_000L, 0, true, PlayerBotBreakOff.Done)]
    [TestCase(false, 1, 5_000L, 150, true, PlayerBotBreakOff.Caught)]
    [TestCase(false, 1, 30_000L, 2_000, false, PlayerBotBreakOff.Done)]
    [TestCase(false, 1, 29_999L, 2_000, false, PlayerBotBreakOff.Run)]
    [TestCase(false, 1, 30_000L, 600, false, PlayerBotBreakOff.Run)]
    [TestCase(true, 1, 30_000L, 600, false, PlayerBotBreakOff.NextPoint)]
    [TestCase(false, 1, 45_000L, 600, false, PlayerBotBreakOff.Fight)]
    [TestCase(false, 1, 45_000L, 2_000, false, PlayerBotBreakOff.Done)]
    public void A_break_off_runs_until_no_monster_chases_it_or_none_is_near_from_30_s(bool arrived, int chasers,
        long sinceMs, int nearestChaser, bool caught, PlayerBotBreakOff step) =>
        Assert.That(PlayerBotDriver.BreakOffStep(arrived, chasers, sinceMs, nearestChaser, caught), Is.EqualTo(step));

    // #167 E-S: a break-off's sprint goes off once the nearest chaser not held is far enough behind that it can't close to
    // its reach in the 25 s it chases at the run's 191 (400, and 25 x its speed over 191 more), and on again when one
    // comes within 300 with the endurance to start one (GamePlayer.cs:8633). Left on, the run ended near 5% endurance and
    // the next fight's break-off was refused (breakoff E3: 15 deaths).
    [TestCase(true, 900, 191, 40, false)] [TestCase(true, 400, 191, 40, false)] [TestCase(true, 399, 191, 40, null)]
    [TestCase(true, 450, 200, 40, null)] [TestCase(true, 625, 200, 40, false)] [TestCase(true, int.MaxValue, 0, 40, false)]
    [TestCase(false, 250, 191, 40, true)] [TestCase(false, 250, 191, 10, null)] [TestCase(false, 300, 191, 40, null)]
    [TestCase(false, 900, 191, 40, null)]
    [TestCase(true, 200, 150, 40, null)]                     // a chaser slower than the run, in contact: no gain, 400 still
    public void A_break_offs_sprint_goes_off_once_clear_and_on_when_a_chaser_closes(bool sprinting, int nearestFree,
        int fastest, int endurance, bool? sprint) =>
        Assert.That(PlayerBotDriver.BreakOffSprint(sprinting, nearestFree, fastest, endurance), Is.EqualTo(sprint));

    // #167 E T2b: done (the chasers gone, or from 30 s none within 1,500) in another monster's reach with its point ahead,
    // it runs on to the point (one scan, then remembered); at its point, with none, or at the 45-s cap it stops.
    // st-deathsD2/D2b: Done stopped the bot where the chaser gave up, and a fight opened there within 3 s of 7 fatal
    // shook ends (a chaser that gave up stands in its own aggro reach).
    [TestCase(true, false, 26_000L, false, true, true)]
    [TestCase(true, false, 26_000L, false, false, false)]                      // clear where it is
    [TestCase(true, false, 26_000L, true, false, true)]                        // running on: no new scan
    [TestCase(true, true, 26_000L, false, true, false)]                        // at its point
    [TestCase(false, false, 26_000L, false, true, false)]                      // no point (holding)
    [TestCase(true, false, 45_000L, false, true, false)]                       // the cap
    [TestCase(true, false, 45_000L, true, true, false)]
    public void A_break_off_done_in_a_monsters_reach_runs_on_to_its_point(bool hasGoal, bool arrived, long sinceMs,
        bool runningOn, bool inReach, bool runsOn)
    {
        bool scanned = false;
        Assert.That(PlayerBotDriver.RunsOnToClear(hasGoal, arrived, sinceMs, runningOn, () => { scanned = true; return inReach; }),
            Is.EqualTo(runsOn));
        if (!hasGoal || arrived || runningOn || sinceMs >= PlayerBotDriver.BreakOffMaxMilliseconds)
            Assert.That(scanned, Is.False, "no scan then");
    }

    // #167 D2: caught on the run, a chaser (not held) within 250 (ChaserReach and 50) for 2 s. st-deathsD: the clock started
    // with the chaser still in contact and fired 2 s into the run, before the sprint could gain on it (57 a second at
    // most on a 191 chaser): 89 of 98 break-offs ended fought at sec=2, and health fell during only 31 of 87 runs. Now
    // caught means really hit after the sprint had time to build a lead: the clock from the 3-s grace's end
    // (inReachSince no earlier), 2 s in reach, and a hit on the bot in the last 2 s. Runs from 10,000.
    [TestCase(180, 10_000L, 10_000L, 11_500L, 12_000L, false)]                 // inside the grace
    [TestCase(180, 10_000L, 13_000L, 14_800L, 15_000L, true)]
    [TestCase(180, 10_000L, 13_000L, 12_000L, 15_000L, false)]                 // no hit in 2 s
    [TestCase(180, 10_000L, 14_000L, 15_500L, 15_500L, false)]                 // in reach 1.5 s
    [TestCase(300, 10_000L, 13_000L, 14_900L, 15_000L, false)]                 // out of reach
    [TestCase(180, 10_000L, 13_000L, 0L, 15_000L, false)]                      // never hit
    public void A_chaser_hitting_the_bot_in_reach_for_2_s_after_the_grace_catches_it(int nearestChaser, long runStart,
        long inReachSince, long lastHitOnBot, long now, bool caught) =>
        Assert.That(PlayerBotDriver.CaughtNow(nearestChaser, runStart, inReachSince, lastHitOnBot, now), Is.EqualTo(caught));

    // InReachSince keeps when one came within it (0: none), and starts no clock inside the grace. Runs from 0, and from
    // 10,000.
    [TestCase(0L, 180, 0L, 5_000L, 5_000L)] [TestCase(3_000L, 250, 0L, 5_000L, 3_000L)]
    [TestCase(3_000L, 251, 0L, 5_000L, 0L)] [TestCase(0L, int.MaxValue, 0L, 5_000L, 0L)]
    [TestCase(0L, 150, 10_000L, 12_999L, 0L)] [TestCase(0L, 150, 10_000L, 13_000L, 13_000L)]
    public void The_reach_clock_starts_when_a_chaser_comes_within_250_after_the_grace(long since, int nearestChaser,
        long runStart, long now, long kept) =>
        Assert.That(PlayerBotDriver.InReachSince(since, nearestChaser, runStart, now), Is.EqualTo(kept));

    // st-deathsD's caught clock where BreaksOffNow runs it (CaughtThink), from the state StartsBreakOff leaves (BreakOffSince
    // now, CaughtSince 0; the same think runs it next): a break-off begun with its chaser in contact (150) and hit on
    // every think was caught 2 s later, before its sprint could build a lead (89 of st-deathsD's 98 break-offs ended
    // fought at sec=2). Now it is not; still in contact and hit, it is caught 2 s after the 3-s grace. The hit is the
    // bot's own LastAttackedByEnemyTick, set by GameLiving's backing field (GamePlayer's setter sends to its client,
    // GamePlayer.cs:1185-1195); its own swings (LastAttackTickPvE, in LastCombatTickPvE) are no hit.
    [Test]
    public void A_break_off_begun_in_contact_is_not_caught_2_s_later()
    {
        GamePlayer bot = Player(null);
        FieldInfo hit = typeof(GameLiving).GetField("<LastAttackedByEnemyTickPvE>k__BackingField", Hidden)!,
            swing = typeof(GameLiving).GetField("<LastAttackTickPvE>k__BackingField", Hidden)!;
        var state = new PlayerBotState(bot) { BreakOffSince = 10_000 };
        for (long now = 10_000; now < 15_000; now += PlayerBotDriver.TickMilliseconds)
        {
            hit.SetValue(bot, now - 100);
            Assert.That(PlayerBotDriver.CaughtThink(state, bot, 150, now), Is.False, $"{now}");
        }
        hit.SetValue(bot, 14_900L);
        Assert.That((PlayerBotDriver.CaughtThink(state, bot, 150, 15_000), state.CaughtSince), Is.EqualTo((true, 13_000L)));
        swing.SetValue(bot, 16_900L);
        Assert.That(PlayerBotDriver.CaughtThink(state, bot, 150, 17_000), Is.False, "its own swing; the last hit 2.1 s ago");
    }

    // #167 D2: a held chaser stays a chaser (KeepsChaser): a mezzed or stunned monster keeps its target and comes back
    // (2 of st-deathsC's 41 "shook" died within 10 s); one goes when it turns from the bot or is past ChaseRadius.
    [TestCase(true, 2_000, true)] [TestCase(true, 6_001, false)] [TestCase(false, 100, false)]
    public void A_held_monster_after_the_bot_stays_a_chaser(bool targetsBot, int distance, bool kept) =>
        Assert.That(PlayerBotDriver.KeepsChaser(targetsBot, distance), Is.EqualTo(kept));

    // #167 D2: caught with no control to cast, it turns on the weakest chaser in reach that isn't held (a monster under
    // 33% health also moves slower, MaxSpeedCalculator.cs:142-143); none in reach, none.
    [Test]
    public void Caught_with_no_control_it_turns_on_the_weakest_chaser_in_reach()
    {
        Assert.That(PlayerBotDriver.WeakestInReach([("a", (byte)80, 120, false), ("b", (byte)25, 180, false),
            ("c", (byte)10, 600, false)], 250), Is.EqualTo("b"));
        Assert.That(PlayerBotDriver.WeakestInReach([("a", (byte)80, 120, false), ("b", (byte)25, 180, true)], 250),
            Is.EqualTo("a"), "a held one is no threat now");
        Assert.That(PlayerBotDriver.WeakestInReach([("c", (byte)10, 600, false)], 250), Is.Null);
    }

    // At its start the bot remembers the camp and the names as after a death (DEATHS.md A3, #167 F4), and at its end it
    // owes a relocation planned from where it stands: FailedCamps is read by PlanRelocation only, and with the anchor
    // left at the camp Roam's leash walked it back in. A lever spot bot or a task owes none (OwesReplan).
    [Test]
    public void A_break_off_bars_the_camp_and_its_names_and_owes_a_relocation()
    {
        var state = new PlayerBotState(Player(null));
        PlayerBotDriver.BrokeOffFrom(state, 51, new Vector3(1_000, 0, 0), ["forest bear", null, "Forest Bear", "wolf"], 2_000);
        Assert.That((PlayerBotDriver.IsAvoided(state.AvoidUntil, "forest bear", 2_001),
            PlayerBotDriver.IsAvoided(state.AvoidUntil, "wolf", 2_001)), Is.EqualTo((true, true)));
        Assert.That(PlayerBotDriver.FailedCamps(state.FailedCamps, 51, 2_001), Is.EqualTo(new[] { new Vector3(1_000, 0, 0) }));
        state.NextRelocateTick = 50_000;
        state.RelocateRejected.Add(null);
        PlayerBotDriver.AfterBreakOff(state, true, 7_000);
        Assert.That((state.ReplanOwed, state.ReplanSince, state.NextRelocateTick, state.RelocateRejected.Count),
            Is.EqualTo((PlayerBotDriver.BrokeOff, 7_000L, 0L, 0)));
        var held = new PlayerBotState(Player(null));
        PlayerBotDriver.AfterBreakOff(held, false, 7_000);
        Assert.That((held.ReplanOwed, held.ReplanSince), Is.EqualTo(((string)null, 0L)));
    }

    // #167 D9: a death or a break-off on the walk to the anchor (RoamDestination is the anchor, as the hold reads it)
    // more than RoamRadius from it bars the anchor too: the death spot's bar (RememberFailedCamp) covers RoamRadius round
    // it (PlanRelocation's Failed), and st-deathsC's 21 died replans that kept the anchor (Pbotaaacv aside) all died over
    // 1,500 from it, their first fights fatal 10 of 21 against 38 of 128 after a new camp; 6 of the 7 break-off
    // relocations back to the old anchor came on the walk to it. A capped or outgrown bot bars nothing.
    [TestCase(PlayerBotDriver.Died, true, 3_000f, true)]
    [TestCase(PlayerBotDriver.Died, true, 1_000f, false)]                     // the death spot's bar covers it
    [TestCase(PlayerBotDriver.Died, true, 1_400f, false)] [TestCase(PlayerBotDriver.Died, true, 1_401f, true)]
    [TestCase(PlayerBotDriver.Died, false, 3_000f, false)]
    [TestCase(PlayerBotDriver.BrokeOff, true, 3_000f, true)]
    [TestCase(PlayerBotDriver.BrokeOff, false, 756f, false)]
    [TestCase(PlayerBotDriver.Capped, true, 3_000f, false)] [TestCase(PlayerBotDriver.Outgrown, true, 3_000f, false)]
    public void A_death_or_break_off_on_the_walk_to_the_anchor_bars_it(string reason, bool walkingToAnchor,
        float fromAnchor, bool bars) =>
        Assert.That(PlayerBotDriver.BarsOldAnchor(reason, walkingToAnchor, fromAnchor), Is.EqualTo(bars));

    // The bar where it applies (#167 D9): at the break-off's start, from where it ran, in the anchor's region; off the
    // walk to the anchor (a roam point) only the camp it ran from.
    [Test]
    public void A_break_off_on_the_walk_to_the_anchor_bars_the_anchor_too()
    {
        var walking = new PlayerBotState(Player(null)) { AnchorRegion = 51, Anchor = new(3_000, 0, 0) };
        walking.RoamDestination = walking.Anchor;
        PlayerBotDriver.BrokeOffFrom(walking, 51, new Vector3(1_000, 0, 0), ["wolf"], 2_000);
        Assert.That(PlayerBotDriver.FailedCamps(walking.FailedCamps, 51, 2_001),
            Is.EqualTo(new[] { new Vector3(1_000, 0, 0), new Vector3(3_000, 0, 0) }));
        var roaming = new PlayerBotState(Player(null)) { AnchorRegion = 51, Anchor = new(3_000, 0, 0) };
        roaming.RoamDestination = new Vector3(1_200, 0, 0);
        PlayerBotDriver.BrokeOffFrom(roaming, 51, new Vector3(1_000, 0, 0), ["wolf"], 2_000);
        Assert.That(PlayerBotDriver.FailedCamps(roaming.FailedCamps, 51, 2_001), Is.EqualTo(new[] { new Vector3(1_000, 0, 0) }));
    }

    // And at the death (HandleDeath), which reads the walk before it clears RoamDestination (back at the bind point, a
    // fresh start): a bot that died (here at 0,0,0) 3,000 from the anchor it walked to bars both; one on a roam point, or
    // within RoamRadius of the anchor, only the death spot.
    [Test]
    public void A_death_on_the_walk_to_the_anchor_bars_the_anchor_too()
    {
        MethodInfo handleDeath = typeof(PlayerBotDriver).GetMethod("HandleDeath", BindingFlags.Static | BindingFlags.NonPublic)!;
        Vector3[] Barred(Vector3 anchor, Vector3? walk)
        {
            GamePlayer bot = Player(null);
            var state = new PlayerBotState(bot) { Anchor = anchor };
            state.RoamDestination = walk ?? state.Anchor;
            handleDeath.Invoke(null, [state, bot, 54_000L]);
            Assert.That(state.RoamDestination, Is.Null);
            return PlayerBotDriver.FailedCamps(state.FailedCamps, state.AnchorRegion, 54_001);
        }
        Assert.That(Barred(new Vector3(3_000, 0, 0), null), Is.EqualTo(new[] { Vector3.Zero, new Vector3(3_000, 0, 0) }));
        Assert.That(Barred(new Vector3(3_000, 0, 0), new Vector3(500, 0, 0)), Is.EqualTo(new[] { Vector3.Zero }));
        Assert.That(Barred(new Vector3(1_000, 0, 0), null), Is.EqualTo(new[] { Vector3.Zero }));
    }

    // The plan then takes the next camp (the brief's plan check; it held before the bar, which puts the anchor among the
    // failed camps): camps at 0 and 8,000, both fitting, from 3,000.
    [Test]
    public void A_barred_anchor_sends_the_plan_to_the_next_camp()
    {
        var monsters = new[] { Monster("a", 0, ConColor.GREEN), Monster("b", 8_000, ConColor.GREEN) };
        Assert.That(PlayerBotDriver.PlanRelocation(monsters, new Vector3(3_000, 0, 0), ConColor.GREEN, ConColor.YELLOW, null,
            new HashSet<string>(), 5).Spots, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(PlayerBotDriver.PlanRelocation(monsters, new Vector3(3_000, 0, 0), ConColor.GREEN, ConColor.YELLOW,
            new[] { Vector3.Zero }, new HashSet<string>(), 5).Spots, Is.EqualTo(new[] { "b" }));
    }

    // The second-monster case's mark: a kill in a fight that had 2 or more monsters on the bot (FIGHT adds=), so the
    // break-off may fire at once with the one still on it; a kill with one, or a fight dropped, sets none.
    [TestCase(true, 2, true)] [TestCase(true, 1, false)] [TestCase(false, 3, false)]
    public void A_kill_with_an_add_still_on_marks_the_second_monster_case(bool engaged, int fightAdds, bool marked)
    {
        MethodInfo settle = typeof(PlayerBotDriver).GetMethod("SettleTarget", BindingFlags.Static | BindingFlags.NonPublic);
        PlayerBotState state = new PlayerBotState(Player(null));
        (state.Target, state.Engaged, state.FightAdds) = ((GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC)),
            engaged, fightAdds);                                                         // health 0: dead
        settle!.Invoke(null, [state, Player(null)]);
        Assert.That(state.KilledWithAdds, Is.EqualTo(marked));
    }

    // The review of 9aacd69: the mark held until the next fight's first think, so a bot that sat at 35% after the kill
    // broke off from one monster that jumped it mid-rest (a respawn, a wanderer). The case is the kill's: 3 s from it
    // (inf.; the analysts' add fights leave out those begun within 3 s of a kill).
    [TestCase(true, 10_000L, 10_000L, true)] [TestCase(true, 10_000L, 12_999L, true)] [TestCase(true, 10_000L, 13_000L, false)]
    [TestCase(false, 10_000L, 10_000L, false)]
    public void The_second_monster_case_holds_only_right_after_the_kill(bool killedWithAdds, long lastKill, long now,
        bool holds) =>
        Assert.That(PlayerBotDriver.SecondMonsterCase(killedWithAdds, lastKill, now), Is.EqualTo(holds));

    // The break-off's first rule as StartsBreakOff applies it (the review of 9aacd69: with the kill's mark replaced by false
    // no test failed): the monsters on the bot, its target's health or, at the kill (no target), the nearest hitter's, the
    // kill's mark (SecondMonsterCase), and C4's add control, asked only once the rest holds (it scans).
    [Test]
    public void At_the_kill_the_break_off_reads_the_kills_mark_and_the_nearest_hitters_health()
    {
        var state = new PlayerBotState(Player(null)) { KilledWithAdds = true, LastKillTick = 10_000 };
        int asked = 0;
        bool NoControl() { asked++; return false; }
        bool Fires(byte health, (byte, int, bool)[] hitters, long now, Func<bool> control) =>
            PlayerBotDriver.BreakOffFires(state, PlayerBotRole.Melee, false, health, hitters, now, control);
        Assert.That(Fires(39, [(100, 150, false)], 10_250, NoControl), Is.True, "the second-monster case");
        Assert.That(Fires(39, [(100, 150, false)], 10_250, () => true), Is.False, "C4's control first");
        Assert.That(Fires(39, [(100, 150, false)], 13_000, NoControl), Is.False, "3 s on");
        Assert.That(Fires(40, [(100, 150, false)], 10_250, NoControl), Is.False, "at 40%");
        Assert.That(Fires(39, [(80, 300, false), (10, 150, false)], 10_250, NoControl), Is.False,
            "the nearest hitter nearly dead");
        Assert.That(Fires(39, [(10, 300, false), (80, 150, false)], 10_250, NoControl), Is.True);
        state.KilledWithAdds = false;
        asked = 0;
        Assert.That(Fires(39, [(100, 150, false)], 10_250, NoControl), Is.False, "no mark: it fights on");
        Assert.That(asked, Is.Zero, "the add control is not asked when the rest fails");
        Assert.That(Fires(39, [(100, 150, false), (80, 300, false)], 10_250, NoControl), Is.True, "two on it");
    }

    // #167 D2: the whole fight after an add kill (FollowsAddKill). OpenFight cleared the kill's mark on the kill's own think
    // when the add was already in reach, so the 3-s case got one think and 14 second-monster deaths stayed; now the fight
    // that opens within 3 s of such a kill keeps the case to its end.
    [Test]
    public void The_fight_after_a_kill_with_adds_keeps_the_second_monster_case()
    {
        var state = new PlayerBotState(Player(null)) { KilledWithAdds = true, LastKillTick = 10_000 };
        PlayerBotDriver.OpenFight(state, 10_250, 50, 50, 100, 100);
        Assert.That((state.FollowsAddKill, state.KilledWithAdds), Is.EqualTo((true, false)));
        state.KilledWithAdds = true;
        PlayerBotDriver.OpenFight(state, 13_000, 50, 50, 100, 100);
        Assert.That(state.FollowsAddKill, Is.False, "3 s on: an ordinary fight");
        var follows = new PlayerBotState(Player(null)) { Engaged = true, FollowsAddKill = true };
        Assert.That(PlayerBotDriver.BreakOffFires(follows, PlayerBotRole.Melee, false, 39, [(100, 150, false)], 25_000,
            () => false), Is.True, "15 s into the follow-up");
        follows.FollowsAddKill = false;
        Assert.That(PlayerBotDriver.BreakOffFires(follows, PlayerBotRole.Melee, false, 39, [(100, 150, false)], 25_000,
            () => false), Is.False);
    }

    // The break-off's end where EndBreakOff applies it (the review of 9aacd69: with the relocation's call deleted no test
    // failed): it clears the run, and unless the bot died it owes the relocation (AfterBreakOff); fought, no new break-off
    // for 30 s.
    [TestCase("shook", "brokeoff", 0L)] [TestCase("fought", "brokeoff", 37_000L)] [TestCase("died", null, 0L)]
    public void A_break_offs_end_owes_the_relocation_unless_it_died(string outcome, string owed, long nextBreakOff)
    {
        var state = new PlayerBotState(Player(null)) { BreakOffSince = 2_000, BreakOffGoal = new Vector3(1, 0, 0),
            BreakOffArrived = true };
        state.BreakOffChasers.Add(null);
        state.BreakOffBlocked.Add(Vector3.One);
        PlayerBotDriver.FinishBreakOff(state, outcome, owesReplan: true, 7_000);
        Assert.That((state.BreakOffSince, state.BreakOffGoal, state.BreakOffArrived, state.BreakOffChasers.Count,
            state.BreakOffBlocked.Count), Is.EqualTo((0L, (Vector3?)null, false, 0, 0)));
        Assert.That((state.ReplanOwed, state.NextBreakOffTick), Is.EqualTo((owed, nextBreakOff)));
        var held = new PlayerBotState(Player(null)) { BreakOffSince = 2_000 };
        PlayerBotDriver.FinishBreakOff(held, PlayerBotDriver.Shook, owesReplan: false, 7_000);
        Assert.That(held.ReplanOwed, Is.Null, "a lever spot bot or a task owes none");
        var turned = new PlayerBotState(Player(null)) { BreakOffSince = 2_000, BreakOffTurn = PlayerBotDriver.TurnNoPoint,
            BreakOffHolds = 3, NextBreakOffRetryTick = 9_000, BreakOffHoldSince = 6_000,
            BreakOffHoldWhy = PlayerBotDriver.TurnBlocked, BreakOffSpeed = 191, BreakOffChaserSpeed = 248 };
        PlayerBotDriver.FinishBreakOff(turned, PlayerBotDriver.Fought, owesReplan: true, 7_000);
        Assert.That((turned.BreakOffTurn, turned.BreakOffHolds, turned.NextBreakOffRetryTick, turned.BreakOffHoldSince,
                turned.BreakOffHoldWhy, turned.BreakOffSpeed, turned.BreakOffChaserSpeed),
            Is.EqualTo(((string)null, 0, 0L, 0L, (string)null, -1, -1)),
            "#167 E T1: the turn, the holds, the retry and the speeds go with the run");
        var ranOn = new PlayerBotState(Player(null)) { BreakOffSince = 2_000, BreakOffRunsOn = true };
        PlayerBotDriver.FinishBreakOff(ranOn, PlayerBotDriver.Shook, owesReplan: true, 7_000);
        Assert.That(ranOn.BreakOffRunsOn, Is.False, "#167 E T2b: and the run-on");
    }

    // The review of 9aacd69: after a break-off the bot rests (it is under 40%), and the first search after the rest ran
    // before step 15's relocation, from where it ran to and still leashed to the camp it fled: a monster of another name
    // there could be pulled. No ring search while a relocation is owed, as after a release (self-defence and a monster
    // coming at it still go).
    [TestCase(true, false, null, true)] [TestCase(false, false, null, false)] [TestCase(true, true, null, false)]
    [TestCase(true, false, "brokeoff", false)]
    public void No_ring_search_while_the_recovery_lasts_or_a_relocation_is_owed(bool searchDue, bool afterRelease,
        string replanOwed, bool searches) =>
        Assert.That(PlayerBotDriver.SearchesRings(searchDue, afterRelease, replanOwed), Is.EqualTo(searches));

    // PLAYER_BOT_BREAKOFF: the bot's health, endurance and attackers when it broke off, the seconds it ran and how it ended;
    // then (#167, st-deathsD's) rate=, the health it lost a second over the 3 s before it broke off (HealthRate), and
    // (#167 D1, D2) why=: the rule that fired (BreakOffWhy), or on a refused one (outcome=refused, sec=0) why it wasn't;
    // then (#167 E-S) sprintOff=, the seconds into the run its sprint first went off (-1: never), and endLeft=, its
    // endurance at the run's end (a refused one's: then); then (#167 E T1) turn=, why a fought one turned (caught,
    // nopoint, blocked or cap; - for none), holds=, its one-second tries that found no point while it held, and speed= and
    // chaserSpeed=, its run speed and the fastest chaser's when it began (an encumbered bot runs under 191; -1 before
    // they are read).
    [Test]
    public void A_break_off_line_gives_its_start_and_outcome()
    {
        Assert.That(PlayerBotDriverReport.FormatBreakOff("Pbotaaaaz", 34, 38, 72, 2, 26, "shook", 6.26, "adds", 9, 41),
            Is.EqualTo("PLAYER_BOT_BREAKOFF bot=Pbotaaaaz level=34 hp=38 end=72 attackers=2 sec=26 outcome=shook rate=6.3 " +
                "why=adds sprintOff=9 endLeft=41 turn=- holds=0 speed=-1 chaserSpeed=-1"));
        Assert.That(PlayerBotDriverReport.FormatBreakOff("Pbotaaaaz", 34, 38, 72, 1, 0, "refused", 3.0, "speed", -1, 72,
                speed: 191, chaserSpeed: 191),
            Is.EqualTo("PLAYER_BOT_BREAKOFF bot=Pbotaaaaz level=34 hp=38 end=72 attackers=1 sec=0 outcome=refused rate=3.0 " +
                "why=speed sprintOff=-1 endLeft=72 turn=- holds=0 speed=191 chaserSpeed=191"));
        Assert.That(PlayerBotDriverReport.FormatBreakOff("Pbotaaaaz", 34, 38, 72, 2, 12, "fought", 6.26, "adds", 4, 30,
                "nopoint", 3, 170, 248),
            Is.EqualTo("PLAYER_BOT_BREAKOFF bot=Pbotaaaaz level=34 hp=38 end=72 attackers=2 sec=12 outcome=fought rate=6.3 " +
                "why=adds sprintOff=4 endLeft=30 turn=nopoint holds=3 speed=170 chaserSpeed=248"));
    }

    // The dropped FIGHT line's adds= (#167 D2): BreaksOffNow returns before Engage folds the attackers in, so the monsters
    // on the bot at the break-off (BREAKOFF attackers=) fold in there; with 2 or more and no add seen yet, the add came now
    // (addAt=, the whole seconds into the fight, and addHp=, the bot's health). One on it, or an add seen, as it was.
    [TestCase(1, -1, -1, 2, 12_400L, (byte)36, 2, 12, 36)]
    [TestCase(4, 2, 86, 5, 13_000L, (byte)36, 5, 2, 86)]
    [TestCase(3, 1, 90, 2, 5_000L, (byte)30, 3, 1, 90)]
    [TestCase(1, -1, -1, 1, 5_000L, (byte)30, 1, -1, -1)]
    public void A_break_off_folds_the_monsters_on_the_bot_into_the_dropped_fights_adds(int fightAdds, int addAt, int addHp,
        int onBot, long fightMs, byte health, int adds, int at, int hp) =>
        Assert.That(PlayerBotDriver.FoldBreakOffAdds(fightAdds, (addAt, addHp), onBot, fightMs, health),
            Is.EqualTo((adds, (at, hp))));

    // --- monsters coming, and the rest out of their reach (#167 F7) ---

    // A live monster the bot may attack, within IncomingRadius (2,000) and with the bot as its target, is coming at it:
    // it counts as an attacker before its first hit, so the bot stands and engages first. Not the one Engage found
    // unreachable, between scans too (the review of 9549283: the scan's skip alone left the cached one re-picked, and
    // walked to and blocked again, every think until the next scan).
    [TestCase(true, true, 1_500, false, true)] [TestCase(true, true, 2_000, false, true)]
    [TestCase(true, true, 2_001, false, false)] [TestCase(true, true, 2_500, false, false)]
    [TestCase(false, true, 500, false, false)] [TestCase(true, false, 500, false, false)]
    [TestCase(true, true, 500, true, false)]                                                  // unreachable
    public void A_monster_with_the_bot_as_its_target_within_2000_is_coming_at_it(bool targetsBot, bool attackable,
        int distance, bool unreachable, bool incoming) =>
        Assert.That(PlayerBotDriver.IsIncoming(targetsBot, attackable, distance, unreachable), Is.EqualTo(incoming));

    // The rest spot: where the bot stands when no potential add reaches it; else the nearest clear point on the rest
    // rings (RestSpotRings, SpotDirections round it). With an add 400 units along +X (aggro 300, reach 500), the ring
    // of 300 is clear only past 90 degrees, and the first clear point there is at 120.
    [Test]
    public void A_bot_rests_where_it_is_unless_an_add_reaches_it_then_on_the_nearest_clear_ring_point()
    {
        var adds = new List<(string, Vector3, int)> { ("add", new(400, 0, 0), 300) };
        Vector3 spot = PlayerBotDriver.RestSpot(Vector3.Zero,
            PlayerBotDriver.RingPoints(Vector3.Zero, PlayerBotDriver.RestSpotRings, PlayerBotDriver.SpotDirections), adds);
        Assert.That(Vector3.Distance(spot, new Vector3(-150, 259.81f, 0)), Is.LessThan(0.1f), $"{spot}");
        Vector3 clear = new(-1_000, 0, 0);
        Assert.That(PlayerBotDriver.RestSpot(clear, PlayerBotDriver.RingPoints(clear, PlayerBotDriver.RestSpotRings,
            PlayerBotDriver.SpotDirections), adds), Is.EqualTo(clear), "out of every reach: where it is");
    }

    // --- the rest out of the killed monsters' respawns (#167 C7) ---

    // A kill's spawn spot counts for RespawnWatchMilliseconds (150 s; the world's usual RespawnInterval is 80 s): 142 of
    // st-deathsB's rests were stood up 75-95 s after a kill by a monster of the killed one's name, and 18 deaths followed.
    [Test]
    public void A_kills_spawn_spot_counts_for_150_s()
    {
        var kills = new List<(Vector3, int, long)> { (Vector3.Zero, 500, 0) };
        Assert.That((PlayerBotDriver.RespawnSpots(kills, 60_000).Count(), PlayerBotDriver.RespawnSpots(kills, 151_000).Count()),
            Is.EqualTo((1, 0)));
    }

    // The bot keeps its last 3 kills, while the watch lasts.
    [Test]
    public void A_bot_remembers_its_last_three_kills_while_the_watch_lasts()
    {
        var kills = new List<(Vector3 At, int Reach, long KilledAt)>();
        for (int i = 0; i < 4; i++) PlayerBotDriver.RememberKill(kills, new Vector3(i, 0, 0), 500, i * 10_000L);
        Assert.That(kills.Select(kill => kill.At.X), Is.EqualTo(new[] { 1f, 2f, 3f }));
        PlayerBotDriver.RememberKill(kills, new Vector3(9, 0, 0), 500, 175_000);
        Assert.That(kills.Select(kill => kill.At.X), Is.EqualTo(new[] { 3f, 9f }), "the kills at 10 and 20 s are over 150 s old");
    }

    private static List<(string, Vector3, int)> Spots(IEnumerable<(Vector3 At, int Reach)> spots) =>
        spots.Select(spot => ((string)null, spot.At, spot.Reach)).ToList();

    // Before it sits, the bot steps out of the reach of its recent kills' spawn spots as of a live add's: AddReach gives a
    // dead monster none, so F7's rest spot never saw the camp the bot had just emptied, and a sitting player takes double
    // melee damage that never misses (AttackAction.cs:236-238, AttackComponent.cs:2206-2208).
    [Test]
    public void A_bot_rests_out_of_reach_of_its_kills_spawn_spots_and_the_live_adds()
    {
        List<(string, Vector3, int)> adds = Spots(PlayerBotDriver.RespawnSpots(
            new List<(Vector3, int, long)> { (Vector3.Zero, 500, 0) }, 60_000));
        Assert.That(PlayerBotDriver.RestSpot(new Vector3(100, 0, 0), [new(600, 0, 0), new(100, 900, 0), new(-900, 0, 0)], adds),
            Is.EqualTo(new Vector3(100, 900, 0)));
        adds.Add(("add", new(1_200, 0, 0), 500));
        Assert.That(PlayerBotDriver.RestSpot(new Vector3(100, 0, 0), [new(600, 0, 0), new(100, -1_500, 0)], adds),
            Is.EqualTo(new Vector3(100, -1_500, 0)));
    }

    // At the pick the bot reads its target's spawn spot, where it respawns (GameNPC.cs:3278-3280), not where it stands
    // (the verifier's correction: a defence pick often stands far from it), and the reach it will aggro from: its aggro
    // range when it would aggro the bot at all (IsPotentialAdd), else none.
    // The review of 9aacd69: RespawnOf took any spot, and with where the target stood passed in no test failed; it reads
    // the monster's own spawn spot now, here 4,900 units from where it stands.
    [Test]
    public void A_targets_respawn_is_its_spawn_spot_and_aggro_range_when_it_would_aggro()
    {
        GameNPC Monster(Point3D spawn)
        {
            var npc = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            var mover = (NpcMovementComponent)RuntimeHelpers.GetUninitializedObject(typeof(NpcMovementComponent));
            typeof(MovementComponent).GetField("_ownerPosition", Hidden)!.SetValue(mover, new Vector3(5_000, 200, 30));
            npc.movementComponent = mover;
            npc.SpawnPoint = spawn;
            return npc;
        }
        GameNPC wolf = Monster(new Point3D(100, 200, 30)), unplaced = Monster(null);
        Assert.That(wolf.X, Is.EqualTo(5_000), "where it stands");
        Assert.That(PlayerBotDriver.RespawnOf(wolf, 50, 500, false), Is.EqualTo((new Vector3(100, 200, 30), 500)));
        Assert.That((PlayerBotDriver.RespawnOf(wolf, 0, 500, false).Reach, PlayerBotDriver.RespawnOf(wolf, 50, 500, true).Reach,
            PlayerBotDriver.RespawnOf(unplaced, 50, 500, false).Reach, PlayerBotDriver.RespawnOf(null, 50, 500, false).Reach),
            Is.EqualTo((0, 0, 0, 0)), "passive, grey, no spawn spot, none");
    }

    // A kill remembers that spot (RememberKill); a target that would not aggro, or a fight the bot never had, none.
    [TestCase(true, 500, 1)] [TestCase(true, 0, 0)] [TestCase(false, 500, 0)]
    public void A_kill_remembers_its_targets_respawn(bool engaged, int reach, int remembered)
    {
        MethodInfo settle = typeof(PlayerBotDriver).GetMethod("SettleTarget", BindingFlags.Static | BindingFlags.NonPublic);
        var state = new PlayerBotState(Player(null))
        {
            Target = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC)), Engaged = engaged,   // health 0: dead
            TargetSpawn = new(100, 200, 30), TargetRespawnReach = reach,
        };
        settle!.Invoke(null, [state, Player(null)]);
        Assert.That(state.RecentKills.Select(kill => (kill.At, kill.Reach)),
            Is.EqualTo(Enumerable.Repeat((new Vector3(100, 200, 30), reach), remembered)));
    }

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

    // A flat floor at Z 100: the surface move keeps that height, so it cannot step up or down on the spot, and no floor
    // lies within a voxel of a step below it. A closest-point query finds it within its height range. Open: the mesh's
    // line of sight.
    private sealed class FloorMesh : PathfindingMgrBase
    {
        public Vector3[] Corners;
        public bool Open = true;
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            for (int i = 0; i < Corners.Length; i++) destination[i] = new(Corners[i], EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, Corners.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            new(end.X, end.Y, 100);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Math.Abs(position.Z - 100) <= zRange ? new(position.X, position.Y, 100) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => Open;
    }

    // st-smoke (2026-10-27, #166): Detour's straight-path corners take the polygons' height, which on steep ground lies
    // far off the floor the bot stands on. In Snowdonia the corridor's first corner lay 0.4 units beside the bot and 70
    // below it: never reached, and no surface step moved the bot, so it stood there for the rest of the run, whatever its
    // goal. With the mesh open along the corridor the bot follows it, as a GameBot walks its path; a wall still stops it.
    [Test]
    public void ACorridorTheSurfaceStepCannotFollowIsWalkedWhereTheMeshIsOpen()
    {
        var (region, zone) = World();
        Vector3[] corners = { new(0, 0, 100), new(0.4f, 0, 30), new(500, 0, 100) };
        Assert.That(PlayerTravelPath.HasReachedCorner(corners[0], corners[1]), Is.False);
        var path = new PlayerTravelPath();
        var mesh = new FloorMesh { Corners = corners };
        Vector3 position = corners[0];
        for (int tick = 0; tick < 3; tick++)
            Assert.That(path.TryStep(region, zone, position, corners[^1], 47.75f, tick * 250, mesh, out position, out string failure),
                Is.True, failure);
        Assert.That(position.X, Is.GreaterThan(90), "past the corner below and on toward the next one");

        mesh = new FloorMesh { Corners = corners, Open = false };
        Assert.That(new PlayerTravelPath().TryStep(region, zone, corners[0], corners[^1], 47.75f, 0, mesh, out position, out _), Is.True);
        Assert.That(new Vector2(position.X, position.Y), Is.EqualTo(new Vector2(0, 0)).Using<Vector2>((a, b) => Vector2.Distance(a, b) < 1),
            "no step where the mesh is closed");

        // st-fix (2026-10-27): no step to where the surface moves cannot find the mesh (more than 64 units off it), from
        // where every step failed and the bot lost the walkable surface for good.
        mesh = new FloorMesh { Corners = new[] { new Vector3(0, 0, 100), new Vector3(0.4f, 0, -100), new Vector3(500, 0, 100) } };
        Assert.That(new PlayerTravelPath().TryStep(region, zone, mesh.Corners[0], mesh.Corners[^1], 100, 0, mesh, out position, out _), Is.True);
        Assert.That(position.Z, Is.EqualTo(100), "no step 100 units under the floor");
    }

    // Floors as in Tir na Nog by the link of st-fix4 (#166): a lower floor (Z 0) everywhere, an upper one (Z 167) from
    // X 30 on, and a middle one (Z 80) from Y 30 on, beside the link. The mesh joins lower and upper at X 30-41, with no
    // floor between. A query from a point takes the nearest floor its box reaches; a move or a ray keeps to that floor.
    private sealed class StackedMesh : PathfindingMgrBase
    {
        public Vector3[] Corners;
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            for (int i = 0; i < Corners.Length; i++) destination[i] = new(Corners[i], EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, Corners.Length);
        }
        private static Vector3 On(float floor, Vector3 p) => floor switch
        {
            167 => new(Math.Max(p.X, 30), p.Y, 167),
            80 => new(p.X, Math.Max(p.Y, 30), 80),
            _ => new(p.X, p.Y, 0),
        };
        private static float? Floor(Vector3 p, float xRange, float yRange, float zRange)
        {
            float? best = null;
            foreach (float floor in new float[] { 0, 167, 80 })
            {
                Vector3 q = On(floor, p);
                if (Math.Abs(q.X - p.X) > xRange || Math.Abs(q.Y - p.Y) > yRange || Math.Abs(q.Z - p.Z) > zRange) continue;
                if (best == null || Vector3.Distance(p, q) < Vector3.Distance(p, On(best.Value, p))) best = floor;
            }
            return best;
        }
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Floor(position, xRange, yRange, zRange) is float f ? On(f, position) : null;
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Floor(start, 32, 32, 64) is float f ? On(f, end) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Floor(start, 32, 32, 64) is float f && Vector2.Distance(new(On(f, end).X, On(f, end).Y), new(end.X, end.Y)) < 0.01f;
    }

    // A ledge (Z 40 from Y 100 on) that the lower floor (Z 0) reaches only by the ramp at X 200 or more. A corridor
    // planned from the start (X < 10) runs over the ledge's edge at X 150, which no step crosses; one planned from
    // anywhere else goes by the ramp.
    private sealed class LedgeMesh : PathfindingMgrBase
    {
        public override bool HasNavmesh(Zone zone) => true;
        private static float Floor(float y) => y >= 100 ? 40 : 0;
        private static bool Crosses(Vector3 a, Vector3 b) => (a.Y < 100) != (b.Y < 100) && Math.Min(a.X, b.X) < 195;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            float x = start.X < 10 ? 150 : 200;
            Vector3[] route = { start, new(x, 90, 0), new(x, 110, 40), end };
            for (int i = 0; i < route.Length; i++) destination[i] = new(route[i], EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, route.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Crosses(start, end) ? new(end.X, 99, 0) : new(end.X, end.Y, Floor(end.Y));
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Math.Abs(position.Z - Floor(position.Y)) <= zRange ? new(position.X, position.Y, Floor(position.Y)) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => !Crosses(start, end);
    }

    // Corners overhead at both ends of a lane, which no step climbs: a corridor planned from the lane's west half runs
    // to X 100 and up, one planned from its east half to X 0 and up.
    private sealed class OverheadMesh : PathfindingMgrBase
    {
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            float x = start.X < 50 ? 100 : 0;
            Vector3[] route = { start, new(x, 0, 0), new(x, 0, 200), end };
            for (int i = 0; i < route.Length; i++) destination[i] = new(route[i], EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, route.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            new(end.X, end.Y, 0);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Math.Abs(position.Z) <= zRange ? new(position.X, position.Y, 0) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => end.Z == 0;
    }

    // st-fix6 (#166): most stalls left had one shape, the corridor's next corner on another level than the bot (a ramp
    // above it), while a fresh corridor from the same spot walked 48-57 units on. After 3 seconds the walk ended blocked
    // and the bot gave up its goal. A second without a step plans the corridor again from where the bot stands, at most
    // twice a walk, and it walks on to the same goal.
    [Test]
    public void AStalledWalkPlansAgainFromHereAndKeepsItsGoal()
    {
        var (region, zone) = World();
        Assert.That(Walk(new LedgeMesh(), region, zone, Vector3.Zero, new(150, 150, 40), keepExact: true, blockEnds: true),
            Is.InRange(1, 200));

        // The rule itself: a plan again at ReplanSteps, blocked at StallSteps.
        Vector3 goal = new(150, 150, 40);
        var path = new PlayerTravelPath();
        var stall = new PlayerBotStallWatch();
        long now = 0;
        bool Rebuilt() => path.TryStep(region, zone, Vector3.Zero, goal, 1, now, new LedgeMesh(), out _, out _) && PathNodes(path) > 0;
        bool StillRun(Vector3 toward)
        {
            bool any = false;
            for (int step = 1; step <= PlayerBotPathMover.ReplanSteps; step++)
            {
                stall.Toward(toward, now += 250);
                stall.Still(path, out bool replanned);
                any |= replanned;
            }
            return any;
        }
        bool Run()
        {
            Assert.That(Rebuilt(), Is.True, "a corridor to plan again");
            return StillRun(goal);
        }
        Assert.That(Rebuilt(), Is.True);
        for (int step = 1; step < PlayerBotPathMover.StallSteps; step++)
        {
            stall.Toward(goal, now += 250);
            Assert.That(stall.Still(path, out bool replanned), Is.False, $"step {step}");
            Assert.That(replanned, Is.EqualTo(step == PlayerBotPathMover.ReplanSteps), $"replanned (step {step})");
            Assert.That(PathNodes(path) == 0, Is.EqualTo(step >= PlayerBotPathMover.ReplanSteps), $"planned again (step {step})");
        }
        stall.Toward(goal, now += 250);
        Assert.That(stall.Still(path, out _), Is.True, "blocked");

        // ReplansPerWalk (2) a walk: after a step that moved, the walk plans again once more, then not.
        stall.Reset();
        Assert.That(StillRun(goal), Is.True, "first");
        stall.Moved();
        Assert.That(Run(), Is.True, "second");
        stall.Moved();
        Assert.That(Run(), Is.False, "third");
        Assert.That(PathNodes(path), Is.GreaterThan(0), "the corridor kept");

        // A new walk counts afresh: a goal that jumps more than 64 units, a pause of more than a second in the steps, or
        // a walk that ended (Reset: arrived or blocked). A goal that drifts on with the steps is the same walk.
        foreach ((string what, Action start) in new (string, Action)[]
                 {
                     ("a goal 65 units on", () => goal += new Vector3(65, 0, 0)),
                     ("a pause", () => now += PlayerBotChurnWatch.PauseMilliseconds + 1),
                     ("an ended walk", () => stall.Reset()),
                 })
        {
            stall.Moved();
            Assert.That(Run(), Is.False, $"spent before {what}");
            stall.Moved();
            start();
            Assert.That(Run(), Is.True, what);
            stall.Moved();
            Assert.That(Run(), Is.True, $"{what}: its second");
        }
        stall.Moved();
        foreach (int drift in new[] { 60, 60, 60 }) { goal += new Vector3(drift, 0, 0); stall.Toward(goal, now += 250); }
        Assert.That(Run(), Is.False, "a drifting goal");

        // A target that runs 65 units a step while the bot stands still: the still steps count on, and the walk ends
        // blocked as any other.
        stall.Reset();
        for (int step = 1; step < PlayerBotPathMover.StallSteps; step++)
        {
            stall.Toward(goal += new Vector3(65, 0, 0), now += 250);
            Assert.That(stall.Still(path, out _), Is.False, $"a running target (step {step})");
        }
        stall.Toward(goal += new Vector3(65, 0, 0), now += 250);
        Assert.That(stall.Still(path, out _), Is.True, "a running target: blocked");
    }

    // A floor at Z 0 whose surface moves stop at the start within MoveBand of the polygon edge at X 0, and whose ray is
    // blocked from within RayBand of it; both from over the floor too.
    private sealed class PolygonEdgeMesh : PathfindingMgrBase
    {
        public float MoveBand, RayBand;
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            destination[0] = new(start, EDtPolyFlags.Walk);
            destination[1] = new(end, EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Math.Abs(start.X) < MoveBand || start.Z > 1 ? start : new(end.X, end.Y, 0);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Math.Abs(position.Z) <= zRange ? new(position.X, position.Y, 0) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Math.Abs(start.X) >= RayBand && start.Z <= 1;
    }

    // st-final2 (#166): a bot stood for minutes on a polygon edge in Snowdonia, where Detour took a polygon that led
    // nowhere: its moves stopped at the start and its ray was blocked (117 of 507 starts within 0.06 units of the spot).
    // Further down it floated over the floor after a corridor-line step, and no move started there either. The moves
    // and the ray are taken again from the floor under the walker, half a unit on toward the step.
    [Test]
    public void AWalkerOnAPolygonEdgeOrOverTheFloorStepsOn()
    {
        var (region, zone) = World();
        // The moves stop only on the edge, but the ray is blocked half a unit on too: the move taken again walks it on.
        var moves = new PolygonEdgeMesh { MoveBand = 0.01f, RayBand = 0.6f };
        foreach (Vector3 start in new[] { Vector3.Zero, new Vector3(0, 0, 19) })
            Assert.That(Walk(moves, region, zone, start, new(1000, 0, 0), keepExact: true, blockEnds: true),
                Is.InRange(1, 200), $"the move again, from {start}");
        // The moves stop half a unit on too, the ray only on the edge: the floor fallback, its ray from the floor, does.
        var ray = new PolygonEdgeMesh { MoveBand = 0.6f, RayBand = 0.01f };
        Assert.That(Walk(ray, region, zone, Vector3.Zero, new(1000, 0, 0), keepExact: true, blockEnds: true),
            Is.InRange(1, 200), "the ray from the floor");
    }

    // A wall along Y 10 from X -5 east, with the corridor round its west end: a corner 11.7 units from the start (by the
    // wall's end), then one 104 units off behind the wall. No step or line of sight crosses the wall.
    private sealed class WallEndMesh : PathfindingMgrBase
    {
        private static readonly Vector3 End = new(-6, 10, 0), Beyond = new(100, 30, 0);
        public override bool HasNavmesh(Zone zone) => true;
        private static bool Crosses(Vector3 a, Vector3 b)
        {
            if ((a.Y < 10) == (b.Y < 10)) return false;
            float x = a.X + (10 - a.Y) / (b.Y - a.Y) * (b.X - a.X);
            return x >= -5;
        }
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            Vector3[] route = start.Y < 10 ? new[] { start, End, Beyond, end } : new[] { start, Beyond, end };
            for (int i = 0; i < route.Length; i++) destination[i] = new(route[i], EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, route.Length);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Crosses(start, end) ? start : new(end.X, end.Y, 0);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Math.Abs(position.Z) <= zRange ? new(position.X, position.Y, 0) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => !Crosses(start, end);
    }

    // st-replan (#166): in Vendo Caverns a bot's corridor turned round a wall by two corners 13 and 20 units from it.
    // Both counted as reached (within 32 units), so it walked at the next corner, 100 units off behind the wall, and
    // every fresh corridor did the same, for minutes. A corner within reach is passed only with the next one in sight,
    // or on it, as the NPC pathfinder passes its nodes.
    [Test]
    public void AWalkerGoesRoundAWallEndNearItsStart()
    {
        var (region, zone) = World();
        Assert.That(Walk(new WallEndMesh(), region, zone, Vector3.Zero, new(400, 60, 0), keepExact: true, blockEnds: true),
            Is.InRange(1, 200));
    }

    // The review of the stall replan (#166): planned again at each stall, a bot whose fresh corridors each led to
    // another stall walked to and fro between them for good, never blocked. With two plans again a walk, its third
    // stall blocks.
    [Test]
    public void AWalkThatStallsAgainAfterItsPlanAgainEndsBlocked()
    {
        var (region, zone) = World();
        Assert.That(Walk(new OverheadMesh(), region, zone, Vector3.Zero, new(1000, 0, 0), keepExact: true, blockEnds: true),
            Is.EqualTo(-2));
    }

    // A flat floor at Z 100 that leads on everywhere, whose polygon's bounds reach 128 units down: a box query finds it
    // from up to 128 below, whatever its height range.
    private sealed class SunkMesh : PathfindingMgrBase
    {
        public override bool IsAvailable => true;
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            destination[0] = new(start with { Z = 100 }, EDtPolyFlags.Walk);
            destination[1] = new(end with { Z = 100 }, EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => position.Z >= 100 - 128 && position.Z <= 100 + zRange ? new(position.X, position.Y, 100) : null;
    }

    // st-layers3 (#166): a respawn spot in Domnann lay 124 units under the floor. Detour's 64-unit box found that floor,
    // so the bot counted as within the moves' reach, but every walk from it ended there. Out of the moves' reach, the
    // bot steps up onto the floor; within it, it doesn't.
    [Test]
    public void ABotFarUnderTheFloorStepsUpOntoItThoughDetoursBoxFindsIt()
    {
        var (region, zone) = World();
        typeof(Region).GetField("m_regionData", Hidden)!.SetValue(region, new RegionData { Id = 1 });
        Assert.That(PlayerBotPathMover.FloorStep(new SunkMesh(), zone, new(5000, 5000, -24)), Is.EqualTo(new Vector3(5000, 5000, 100)));
        Assert.That(PlayerBotPathMover.FloorStep(new SunkMesh(), zone, new(5000, 5000, 60)), Is.Null, "within the moves' reach");
    }

    private static int PathNodes(PlayerTravelPath path) =>
        ((Queue<WrappedPathfindingNode>)typeof(PlayerTravelPath).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(path)!).Count;

    // st-fix4 (#166): in Tir na Nog the mesh joins two stacked floors 167 units apart over 41, with no floor between. Half
    // way up, the surface moves found a middle floor beside the link and the bot stood on its edge for minutes, its next
    // corner 87 units overhead. A walker on a link of its own corridor goes along it to the corner, as a GameBot does,
    // whatever the moves find; down as well as up.
    [Test]
    public void AWalkerGoesUpALinkBetweenStackedFloorsToItsCorner()
    {
        var (region, zone) = World();
        var mesh = new StackedMesh { Corners = new[] { new Vector3(0, 0, 0), new Vector3(30, 0, 0), new Vector3(41, 0, 167), new Vector3(300, 0, 167) } };
        var path = new PlayerTravelPath();
        Vector3 position = mesh.Corners[0];
        for (int tick = 0; tick < 12; tick++)
        {
            Assert.That(path.TryStep(region, zone, position, mesh.Corners[^1], 47.75f, tick * 250, mesh, out position, out string failure),
                Is.True, failure);
            Assert.That(position.Y, Is.LessThan(30), $"never on the middle floor (tick {tick})");
        }
        Assert.That(position.Z, Is.EqualTo(167), "on the upper floor");
        Assert.That(position.X, Is.GreaterThan(100), "and on along it");

        // A link from the middle floor's edge to the corner overhead: up to it, and on to the goal (the mover stops within 20).
        path = new PlayerTravelPath();
        mesh = new StackedMesh { Corners = new[] { new Vector3(41, 30, 80), new Vector3(41, 0, 167), new Vector3(300, 0, 167) } };
        position = mesh.Corners[0];
        for (int tick = 0; tick < 12 && Vector3.Distance(position, mesh.Corners[^1]) > 20.25f; tick++)
            Assert.That(path.TryStep(region, zone, position, mesh.Corners[^1], 47.75f, tick * 250, mesh, out position, out string failure),
                Is.True, failure);
        Assert.That(Vector3.Distance(position, mesh.Corners[^1]), Is.LessThanOrEqualTo(20.25f));
    }

    [Test]
    public void AWalkerGoesDownALinkBetweenStackedFloorsToItsCorner()
    {
        var (region, zone) = World();
        var mesh = new StackedMesh { Corners = new[] { new Vector3(400, 0, 167), new Vector3(341, 0, 167), new Vector3(330, 0, 0), new Vector3(100, 0, 0) } };
        var path = new PlayerTravelPath();
        Vector3 position = mesh.Corners[0];
        for (int tick = 0; tick < 12 && Vector3.Distance(position, mesh.Corners[^1]) > 20.25f; tick++)
        {
            Assert.That(path.TryStep(region, zone, position, mesh.Corners[^1], 47.75f, tick * 250, mesh, out position, out string failure),
                Is.True, failure);
            Assert.That(position.Y, Is.LessThan(30), $"never on the middle floor (tick {tick})");
        }
        Assert.That(Vector3.Distance(position, mesh.Corners[^1]), Is.LessThanOrEqualTo(20.25f), "down on the lower floor at the goal");
    }

    // The review of #168: Detour's ray is 2D, so it cannot tell a link from two floors stacked. A walker that stands on
    // another floor than its corridor (here the middle floor, over the corridor's next corner) is not taken through the
    // floor to that corner; it stalls, and the mover plans again from the floor it stands on. A link starts only from
    // its foot.
    [Test]
    public void AWalkerOnAnotherFloorThanItsCorridorIsNotTakenThroughTheFloor()
    {
        var (region, zone) = World();
        var mesh = new StackedMesh { Corners = new[] { new Vector3(0, 0, 0), new Vector3(5, 0, 0), new Vector3(60, 0, 0), new Vector3(71, 0, 167), new Vector3(300, 0, 167) } };
        var path = new PlayerTravelPath();
        Assert.That(path.TryStep(region, zone, mesh.Corners[0], mesh.Corners[^1], 1, 0, mesh, out _, out string failure), Is.True, failure);
        Vector3 position = new(65, 40, 80);
        for (int tick = 1; tick < 12; tick++)
        {
            Assert.That(path.TryStep(region, zone, position, mesh.Corners[^1], 47.75f, tick * 250, mesh, out position, out failure),
                Is.True, failure);
            Assert.That(position.Z, Is.EqualTo(80), $"on the middle floor (tick {tick})");
        }
    }

    // The promotion check after the run 1 rerun (2026-10-21): by Cotswold the surface mover returned the bot's own spot,
    // and the mesh floor at the next corner lay 5 units above the corner (Z 1826 against 1821), within one voxel. The
    // fallback wanted 2 units, so the bot stood there for good. Coordinates are moved into the test zone (480000/580000
    // less). A floor more than a voxel away is another surface, a bridge or a roof: the step keeps to the corridor (#166).
    [TestCase(5f, true)] [TestCase(24f, true)] [TestCase(40f, false)]
    public void AStepOntoTheFloorAVoxelAboveTheCornerIsTaken(float rise, bool onto)
    {
        var (region, zone) = World();
        var mesh = new EdgeMesh
        {
            FloorRise = rise,
            Corners = new[] { new Vector3(15139, 13283, 1821), new Vector3(15175, 13306, 1821), new Vector3(15309, 13390, 1820) }
        };
        Assert.That(new PlayerTravelPath().TryStep(region, zone, mesh.Corners[0], mesh.Corners[^1], 47.75f, 0, mesh,
            out Vector3 position, out string failure), Is.True, failure);
        Assert.That(position, Is.EqualTo(mesh.Corners[1] + new Vector3(0, 0, onto ? rise : 0)));
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

    // Self-defence passes over the monster it found unreachable while that lasts (Engage's blocked walk); the rest gate
    // never does (anyHitter): that monster still hits the bot, a sitting player takes double melee damage that never
    // misses (AttackAction.cs:236-238, AttackComponent.cs:2206-2208), and no hit stands it up. The old gate, InCombat,
    // kept such a bot standing.
    [TestCase(true, 1_000L, 2_000L, false, true)] [TestCase(true, 1_000L, 2_000L, true, false)]
    [TestCase(true, 2_000L, 2_000L, false, false)] [TestCase(false, 1_000L, 2_000L, false, false)]
    public void Self_defence_passes_over_an_unreachable_hitter_and_the_rest_gate_does_not(bool unreachable, long now,
        long until, bool anyHitter, bool skips) =>
        Assert.That(PlayerBotDriver.SkipsHitter(unreachable, now, until, anyHitter), Is.EqualTo(skips));

    // A target over the con cap stays the bot's while it fights the bot or the bot's pet (SettleTarget): one that turned
    // on the pet was dropped, and the bot sat or pulled another while its pet fought on alone. No target is no fight,
    // with no pet too.
    [Test]
    public void A_monster_fighting_the_bot_or_its_pet_is_still_its_fight()
    {
        GameObject bot = Player(null), other = Player(null), pet = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
        Assert.That((PlayerBotDriver.FightsBotOrPet(bot, bot, pet), PlayerBotDriver.FightsBotOrPet(pet, bot, pet),
                PlayerBotDriver.FightsBotOrPet(other, bot, pet), PlayerBotDriver.FightsBotOrPet(null, bot, null)),
            Is.EqualTo((true, true, false, false)));
    }

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
            PlayerBotState state = new PlayerBotState(Player(null));
            (state.Target, state.Engaged) = (dead, engaged);
            return state;
        }
        PlayerBotState won = Fought(true), late = Fought(false);
        settle!.Invoke(null, [won, Player(null)]);
        settle.Invoke(null, [late, Player(null)]);
        Assert.That((won.RecoverBeforePull, late.RecoverBeforePull), Is.EqualTo((true, false)), "the late bot never fought it");
    }

    // #167 F3, C10: a caster's rest stops at full health, its power stop (90) and (#167 E-R) 80% endurance; power counts
    // only with a pool.
    [TestCase((byte)100, 300, (byte)90, (byte)81, true)] [TestCase((byte)99, 300, (byte)100, (byte)100, false)]
    [TestCase((byte)100, 300, (byte)89, (byte)81, false)] [TestCase((byte)100, 0, (byte)0, (byte)81, true)]
    [TestCase((byte)100, 300, (byte)100, (byte)79, false)] [TestCase((byte)100, 300, (byte)90, (byte)80, true)]
    [TestCase((byte)100, 300, (byte)72, (byte)100, false)]
    public void A_rest_stops_at_full_health_and_the_power_and_endurance_stops(byte health, int maxMana, byte mana,
        byte endurance, bool recovered) =>
        Assert.That(PlayerBotDriver.Recovered(health, maxMana, mana, endurance, PlayerBotDriver.RestPowerStop(PlayerBotRole.Caster),
            PlayerBotDriver.RestEnduranceStop), Is.EqualTo(recovered));

    // The class roles of tools/lmroles.py (CLASS_ROLES, by class id): the Hunter has a pet and counts as pet.
    [Test]
    public void The_class_role_is_the_one_lmroles_gives()
    {
        var roles = new Dictionary<PlayerBotRole, int[]>
        {
            [PlayerBotRole.Pet] = [5, 12, 13, 25, 27, 30, 41, 47, 55], [PlayerBotRole.Healer] = [6, 10, 26, 28, 46, 48],
            [PlayerBotRole.Caster] = [7, 8, 29, 40, 42], [PlayerBotRole.Archer] = [3, 50],
        };
        foreach ((PlayerBotRole role, int[] ids) in roles)
            foreach (int id in ids)
                Assert.That(PlayerBotDriver.RoleOf(id), Is.EqualTo(role), $"class {id}");
        foreach (int id in Enum.GetValues<eCharacterClass>().Select(c => (int)c).Where(id => !roles.Values.Any(ids => ids.Contains(id))))
            Assert.That(PlayerBotDriver.RoleOf(id), Is.EqualTo(PlayerBotRole.Melee), $"class {id}");
    }

    // Casters stop resting power at 90% (#167 C10: a list caster regenerates at half rate under 50% power,
    // PowerRegenerationAmountCalculator.cs:38-50, and 46.6% of st-deathsB's caster pulls ended under 50% with batch A's
    // 70); pet classes keep 100% (a 75% stop raised their pet deaths per 100 kills 16% in the model), and so does every
    // other role until measured.
    [Test]
    public void Only_a_caster_stops_resting_power_short_of_full()
    {
        foreach (PlayerBotRole role in Enum.GetValues<PlayerBotRole>())
            Assert.That(PlayerBotDriver.RestPowerStop(role), Is.EqualTo(role == PlayerBotRole.Caster ? 90 : 100), role.ToString());
        Assert.That(PlayerBotDriver.RestEnduranceStop, Is.EqualTo(80));            // #167 E-R
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

    // The pick with no walk in (#167 C5): a bot within its stop of every candidate stands where it is, on a spot no add
    // reaches, so CrowdingAlong counts the adds round each target only, as Crowding did.
    private static readonly Vector3 Aside = new(0, 100_000, 0);
    private static string Pick(IReadOnlyList<(string, Vector3, int)> candidates, IReadOnlyList<(string, Vector3, int)> adds,
        bool uncrowdedOnly) =>
        PlayerBotDriver.PickUncrowded(candidates, adds, uncrowdedOnly, Aside, int.MaxValue);

    [Test]
    public void A_crowded_candidate_gives_way_to_one_with_no_add_near_it()
    {
        var candidates = new List<(string, Vector3, int)> { At("crowded", 0, 100), At("clear", 5_000, 900) };
        var adds = new List<(string, Vector3, int)> { Add("wolf", 600) };                // 500 + 200 reaches 0
        Assert.That(Pick(candidates, adds, uncrowdedOnly: false), Is.EqualTo("clear"));
        Assert.That(Pick(candidates, [], uncrowdedOnly: false), Is.EqualTo("crowded"),
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
        Assert.That(Pick(candidates, adds, uncrowdedOnly: false), Is.EqualTo("one, nearer"));
    }

    // The 2,600-unit ring takes only an uncrowded target; with none, the 5,000-unit ring decides.
    [Test]
    public void The_near_ring_takes_no_crowded_target()
    {
        var crowded = new List<(string, Vector3, int)> { At("crowded", 0, 100) };
        var adds = new List<(string, Vector3, int)> { Add("wolf", 100) };
        Assert.That(Pick(crowded, adds, uncrowdedOnly: true), Is.Null);
        crowded.Add(At("clear", 5_000, 900));
        Assert.That(Pick(crowded, adds, uncrowdedOnly: true), Is.EqualTo("clear"));
        Assert.That(Pick(new List<(string, Vector3, int)>(), adds, uncrowdedOnly: false), Is.Null);
    }

    // --- the pick checks the walk in and the stand spot (#167 C5) ---

    // Where the bot stands to fight its target: its stop short of the target toward the bot (a caster's 1,420 for a
    // 1,500-unit spell), or where it is when it is that near already.
    [Test]
    public void The_stand_point_is_the_stop_short_of_the_target_or_where_the_bot_is()
    {
        Assert.That(Vector3.Distance(PlayerBotDriver.StandPoint(Vector3.Zero, new(3_000, 0, 0), 1_420), new(1_580, 0, 0)),
            Is.LessThan(0.01f));
        Assert.That(PlayerBotDriver.StandPoint(Vector3.Zero, new(1_000, 0, 0), 1_420), Is.EqualTo(Vector3.Zero));
    }

    // The stop the pick checks the walk in to (PullStopDistance, FindWithin's): Engage's first think's, the spell's with
    // a damage spell it can cast now and the power for it (18%, ShouldCast), else melee's. The review of 9aacd69: with
    // no stop passed (the walk in unchecked) no test failed.
    [TestCase(true, 50, 1_420)] [TestCase(true, 18, 1_420)] [TestCase(true, 17, 152)] [TestCase(false, 100, 152)]
    public void The_pick_checks_the_walk_in_to_the_stop_its_pull_takes(bool nuke, int mana, int stop) =>
        Assert.That(PlayerBotDriver.PullStopDistance(nuke ? FungalOoze : null, (byte)mana, 200, nuke ? 1_500 : 0),
            Is.EqualTo(stop));

    // An add counts once when its reach (aggro + 200) covers the target or the walk from the bot, at (0,0), to its stand
    // point: st-deathsB's 82 defence fights begun 2 s or more after a recovered rest were jumped on the way in (59% with
    // adds, 17 fatal), and 16 pulls had an add's ring over the stand spot but not over the target, which is all Crowding
    // looks at. The first row's add is 1,552 units from the target: Crowding gives 0.
    [TestCase(3_000f, 2_920f, 1_500f, 400f, 1)] [TestCase(3_000f, 2_920f, 1_500f, 701f, 0)]
    [TestCase(3_000f, 1_580f, 900f, -650f, 1)]                                       // a caster's stand spot
    [TestCase(1_000f, 920f, 950f, 300f, 1)]                                          // the target and the walk: once
    public void An_add_counts_once_when_its_reach_covers_the_target_or_the_walk_to_the_stand_point(float targetX,
        float standX, float addX, float addY, int crowd) =>
        Assert.That(PlayerBotDriver.CrowdingAlong("target", new Vector3(targetX, 0, 0), Vector3.Zero, new Vector3(standX, 0, 0),
            new List<(string, Vector3, int)> { ("add", new(addX, addY, 0), 500) }), Is.EqualTo(crowd));

    [Test]
    public void A_candidate_is_not_its_own_add_on_the_walk_in_either()
    {
        var adds = new List<(string, Vector3, int)> { ("target", new(3_000, 0, 0), 500), ("wolf", new(1_500, 400, 0), 500) };
        Assert.That(PlayerBotDriver.CrowdingAlong("target", new Vector3(3_000, 0, 0), Vector3.Zero, new Vector3(2_920, 0, 0),
            adds), Is.EqualTo(1));
    }

    // The pick counts the adds along the walk in from where the bot stands to its stand point at its stop: a target
    // behind an add gives way to one with a clear walk, and the near ring takes it no more; from beside it, the walk in
    // is clear and its better score wins.
    [Test]
    public void A_target_behind_an_add_gives_way_to_one_with_a_clear_walk_in()
    {
        var candidates = new List<(string, Vector3, int)> { ("behind", new(3_000, 0, 0), 100), ("clear", new(0, 3_000, 0), 900) };
        var adds = new List<(string, Vector3, int)> { ("wolf", new(1_500, 400, 0), 500) };
        Assert.That(PlayerBotDriver.PickUncrowded(candidates, adds, false, Vector3.Zero, 80), Is.EqualTo("clear"));
        Assert.That(PlayerBotDriver.PickUncrowded(candidates.Take(1).ToList(), adds, true, Vector3.Zero, 80), Is.Null,
            "the near ring");
        Assert.That(PlayerBotDriver.PickUncrowded(candidates, adds, false, new Vector3(3_000, 1_000, 0), 80),
            Is.EqualTo("behind"));
    }

    // FIGHT crowd= (#167, the next leg's): the pick's crowding as the pick read it (CrowdingAlong): 0 for a clear one, the
    // least when every one is crowded, -1 when the ring took none.
    [Test]
    public void The_pick_gives_the_crowding_it_read()
    {
        var candidates = new List<(string, Vector3, int)> { ("behind", new(3_000, 0, 0), 100), ("near", new(0, 3_000, 0), 900) };
        var adds = new List<(string, Vector3, int)>
            { ("wolf", new(1_500, 400, 0), 500), ("boar", new(3_200, 0, 0), 500), ("bear", new(0, 3_300, 0), 500) };
        Assert.That((PlayerBotDriver.PickUncrowded(candidates, adds, false, Vector3.Zero, 80, out int crowd), crowd),
            Is.EqualTo(("near", 1)));
        Assert.That((PlayerBotDriver.PickUncrowded(candidates, adds, true, Vector3.Zero, 80, out crowd), crowd),
            Is.EqualTo(((string)null, -1)), "the near ring took none");
        Assert.That((PlayerBotDriver.PickUncrowded(candidates, [], false, Vector3.Zero, 80, out crowd), crowd),
            Is.EqualTo(("behind", 0)));
    }

    // FIGHT and DEATH addAt= and addHp= (#167, the next leg's): the first think in a fight a second monster, any but the
    // target, is on the bot's attacker list or comes at it (the add scan's): the whole seconds into the fight and the
    // bot's health then, kept after; -1 until then. Only one alive and on the bot (onBot; the review of 9aacd69: the
    // last fight's dead target stays in the attacker list until its attack interval runs out, AttackerTracker.cs:209-219,
    // and a lone next monster engaged within it read addAt=0).
    [Test]
    public void The_first_add_is_the_first_monster_but_the_target_on_the_bot()
    {
        (int, int) none = (-1, -1);
        Func<string, bool> any = _ => true, alive = monster => monster != "the last kill";
        Assert.That(PlayerBotDriver.FirstAdd(none, "target", ["target"], [], any, 4_500, 80), Is.EqualTo(none),
            "the target alone");
        Assert.That(PlayerBotDriver.FirstAdd(none, "target", ["target", "wolf"], [], any, 4_500, 80), Is.EqualTo((4, 80)));
        Assert.That(PlayerBotDriver.FirstAdd(none, "target", [], ["target", "wolf"], any, 6_999, 55), Is.EqualTo((6, 55)),
            "coming at it");
        Assert.That(PlayerBotDriver.FirstAdd((4, 80), "target", ["wolf", "boar"], [], any, 9_000, 30), Is.EqualTo((4, 80)),
            "kept");
        Assert.That(PlayerBotDriver.FirstAdd(none, "target", [], [], any, 0, 100), Is.EqualTo(none));
        Assert.That(PlayerBotDriver.FirstAdd(none, "target", ["the last kill", "target"], ["the last kill"], alive, 0, 100),
            Is.EqualTo(none), "the dead last target, still in the list");
        Assert.That(PlayerBotDriver.FirstAdd(none, "target", ["the last kill", "wolf"], [], alive, 2_000, 90),
            Is.EqualTo((2, 90)));
    }

    // FIGHT addAt='s scan of the monsters coming at the bot (the review of 9aacd69: every role scanned 2,000 units every
    // 2 s for the whole of an add-free fight, for a log field): only a bot that scans for its add step anyway, one with a
    // control or a Theurgist's pets (Add); the others read their attacker list alone.
    [TestCase(0, 0, false)] [TestCase(1, 0, true)] [TestCase(0, 2, true)] [TestCase(3, 1, true)]
    public void Only_a_bot_with_an_add_step_scans_for_monsters_coming_at_it(int controls, int combatPets, bool scans) =>
        Assert.That(PlayerBotDriver.ScansComing(controls, combatPets), Is.EqualTo(scans));

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
        // At most one unit inside the stop radius, so that the whole-unit spot is within it (Arrived).
        Assert.That(PlayerBotPathMover.TryComputeNext(flat, region, zone, new PlayerTravelPath(), Vector3.Zero, new(100, 0, 0), 80, 47.75f, 0,
            out next, out _), Is.True);
        Assert.That(next.X, Is.EqualTo(21).Within(0.001));
        // Less than a unit short still steps (it stood still, and 12 such steps blocked the walk).
        Assert.That(PlayerBotPathMover.TryComputeNext(flat, region, zone, new PlayerTravelPath(), new(19.5f, 0, 0), new(100, 0, 0), 80, 47.75f, 0,
            out next, out _), Is.True);
        Assert.That(next.X, Is.EqualTo(21).Within(0.001));
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
        // Near, with a wall between: the corridor, round it. A bot 267 units from its target crept along a wall for the
        // rest of the run (st-smoke, #166).
        var walled = new Mesh { Walled = true, Corners = new[] { Vector3.Zero, new Vector3(0, 200, 0), new Vector3(200, 200, 0), new Vector3(200, 0, 0) } };
        Assert.That(PlayerBotPathMover.TryComputeNext(walled, region, zone, new PlayerTravelPath(), Vector3.Zero, new(200, 0, 0), 0, 47.75f, 0,
            out next, out _), Is.True);
        Assert.That((walled.Searches, next), Is.EqualTo((1, new Vector3(0, 47.75f, 0))));
        // Within a corner's reach of it (32 units across), the direct step whatever the wall: the corridor drops its last
        // corner there, and the bot stood short of the goal (the review of #168).
        walled = new Mesh { Walled = true, Corners = new[] { Vector3.Zero, new Vector3(30, 0, 0) } };
        Assert.That(PlayerBotPathMover.TryComputeNext(walled, region, zone, new PlayerTravelPath(), Vector3.Zero, new(30, 0, 0), 0, 47.75f, 0,
            out next, out _), Is.True);
        Assert.That((walled.Searches, next), Is.EqualTo((0, new Vector3(30, 0, 0))));
        // Blocked: false after two replans (PTP:130-143).
        var blocked = new Mesh { Blocked = true };
        path = new PlayerTravelPath();
        for (int i = 0; i < 3; i++)
            Assert.That(PlayerBotPathMover.TryComputeNext(blocked, region, zone, path, new(100, 100, 0), new(1000, 1000, 0), 0, 47.75f, i * 15000,
                out _, out _), Is.True);
        Assert.That(PlayerBotPathMover.TryComputeNext(blocked, region, zone, path, new(100, 100, 0), new(1000, 1000, 0), 0, 47.75f, 45000,
            out _, out _), Is.False);
    }

    // Floors as in Jordheim by the pocket of st-final4 (#166): a lower floor (Z 0) up to X Seam, walled in there, and a
    // ramp (Z 60) from X Seam on, which the mesh links to it only across the seam. The plain corridor turns at the seam at
    // the height of the floor it leaves and hides the climb; the layered one gives a corner on each floor there, the one
    // on the floor it enters flagged LayerLink. A move keeps to the floor its start stands on and stops at its edge, and a
    // ray sees only along it. A point stands on the ramp from Split up: half way up the link, or, with a low Split, as
    // Detour's nearest polygon takes a point over the ramp, though far nearer the floor below (st-layers1). With Ramp over
    // 64 and Flag Walk alone, a floor link that Detour's seams don't flag (IsFloorLink), as an in-tile link or a jump; with
    // Run the ramp starts that far beyond the seam, and the link slants across.
    private sealed class SeamMesh : PathfindingMgrBase
    {
        public float Split = 30, Seam = 30, Ramp = 60, Run;
        public EDtPolyFlags Flag = EDtPolyFlags.Walk | EDtPolyFlags.LayerLink;
        public override bool HasNavmesh(Zone zone) => true;
        private float FloorOf(Vector3 p) => p.Z >= Split ? Ramp : 0;
        private float Edge(float floor) => floor == 0 ? Seam : Seam + Run;
        private Vector3 On(float floor, Vector3 p) => floor == 0 ? new(Math.Min(p.X, Seam), p.Y, 0) : new(Math.Max(p.X, Seam + Run), p.Y, Ramp);
        private static PathfindingResult Route(Span<WrappedPathfindingNode> destination, params WrappedPathfindingNode[] route)
        {
            route.CopyTo(destination);
            return new(PathfindingStatus.PathFound, route.Length);
        }
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination) => FloorOf(start) == FloorOf(end)
            ? Route(destination, new(start, EDtPolyFlags.Walk), new(end, EDtPolyFlags.Walk))
            : Route(destination, new(start, EDtPolyFlags.Walk), new(new(Edge(FloorOf(start)), start.Y, FloorOf(start)), EDtPolyFlags.Walk), new(end, EDtPolyFlags.Walk));
        public override PathfindingResult GetPathStraightLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination) => FloorOf(start) == FloorOf(end)
            ? GetPathStraight(zone, start, end, filters, destination)
            : Route(destination, new(start, EDtPolyFlags.Walk), new(new(Edge(FloorOf(start)), start.Y, FloorOf(start)), EDtPolyFlags.Walk),
                new(new(Edge(FloorOf(end)), start.Y, FloorOf(end)), Flag), new(end, EDtPolyFlags.Walk));
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => On(FloorOf(start), end);
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded) =>
            GetMoveAlongSurface(zone, start, end, filters);     // on its floor's height already
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => new[] { On(0, position), On(Ramp, position) }
            .Where(q => Math.Abs(q.X - position.X) <= xRange && Math.Abs(q.Z - position.Z) <= zRange)
            .OrderBy(q => Vector3.Distance(q, position)).Cast<Vector3?>().FirstOrDefault();
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            Vector3.Distance(On(FloorOf(start), end), end) < 0.01f;
    }

    // st-final4 (#166): from the pocket the corridor out climbed a seam link by a corner that hid the climb, and the bot
    // stood at the wall for the rest of the run. With layers (the player bots) the corridor gives the link as two corners:
    // the walker follows the line from the lower one straight up to the upper one, which counts as reached only there, and
    // walks on along the ramp. Without layers (/travel) the corridor stays the plain one.
    [Test]
    public void APlayerBotClimbsALinkToAnotherFloorByItsTwoCorners()
    {
        var (region, zone) = World();
        Vector3 goal = new(300, 0, 60);
        foreach (bool layers in new[] { true, false })
        {
            var path = new PlayerTravelPath();
            Vector3 position = Vector3.Zero;
            for (int tick = 0; tick < 10; tick++)
                Assert.That(path.TryStep(region, zone, position, goal, 47.75f, tick * 250, new SeamMesh(), out position, out string failure,
                    layers), Is.True, failure);
            if (layers)
                Assert.That((position.X, position.Z), Is.EqualTo((300f, 60f)).Using<(float, float)>((a, b) =>
                    Math.Abs(a.Item1 - b.Item1) <= 32 && a.Item2 == b.Item2), "up the link and along the ramp");
            else
                Assert.That(position, Is.EqualTo(new Vector3(30, 0, 0)), "at the wall");
        }
    }

    // st-layers1 (#166): in Jordheim the walker passed a layer link's upper corner from 26 units across and went straight
    // for the foot, 73 units down. 22.6 units above it, within a corner's 24, it counted the foot as reached, but it stood
    // over the ramp only, 51 units under it, and Detour's nearest polygon counts only the height beyond a climb for a
    // polygon the point lies over: its next move started on the ramp, and it walked on along it, over its corridor on the
    // floor below, and stalled. On the link's line the corner counts as reached only there.
    [Test]
    public void APlayerBotGoesDownALinkToItsFoot()
    {
        var (region, zone) = World();
        var path = new PlayerTravelPath();
        var mesh = new SeamMesh { Split = 10, Seam = 330 };
        Vector3 position = new(600, 0, 60), goal = new(30, 0, 0);
        for (int tick = 0; tick < 30; tick++)
            Assert.That(path.TryStep(region, zone, position, goal, 47.75f, tick * 250, mesh, out position, out string failure, true),
                Is.True, failure);
        Assert.That((position.X, position.Z), Is.EqualTo((30f, 0f)).Using<(float, float)>((a, b) =>
            Math.Abs(a.Item1 - b.Item1) <= 32 && a.Item2 == b.Item2), "down the link and along the floor");
    }

    // On a link's climb the bot goes on up to its far corner, though its goal lies in sight within 300 units: in Jordheim
    // a direct step from short of that corner left it queued, and each corridor step after went back to it, for 15 seconds
    // (the review of #166).
    [Test]
    public void APlayerBotClimbsALinkToItsTopBeforeItStepsStraightAtItsGoal()
    {
        var (region, zone) = World();
        var mesh = new SeamMesh();
        var path = new PlayerTravelPath();
        Vector3 position = Vector3.Zero, goal = new(200, 0, 60);
        var climb = new List<Vector3>();
        for (int tick = 0; tick < 10 && !PlayerBotPathMover.Arrived(position, goal, 20); tick++)
        {
            Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, path, position, goal, 20, 47.75f, tick * 250,
                out position, out string failure), Is.True, failure);
            if (position.X == 30) climb.Add(position);
        }
        Assert.That(climb, Is.EqualTo(new Vector3[] { new(30, 0, 0), new(30, 0, 47.75f), new(30, 0, 60) }), "to the seam, then up it");
        Assert.That(PlayerBotPathMover.Arrived(position, goal, 20), Is.True);
    }

    // Near its goal too, the climb goes on to the link's top in whole steps: they head for the top, not the goal, and cut
    // to the goal's distance they crept 2-8 units a step (the third review of #166). Only the first, taken before the
    // corridor knows the link, is cut.
    [Test]
    public void APlayerBotClimbsALinkNearItsGoalWithoutCreeping()
    {
        var (region, zone) = World();
        var mesh = new SeamMesh();
        var path = new PlayerTravelPath();
        Vector3 position = Vector3.Zero, goal = new(50, 0, 60);
        var climb = new List<Vector3>();
        for (int tick = 0; tick < 10 && !PlayerBotPathMover.Arrived(position, goal, 20); tick++)
        {
            Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, path, position, goal, 20, 47.75f, tick * 250,
                out position, out string failure), Is.True, failure);
            if (position.X == 30) climb.Add(position);
        }
        Assert.That(climb, Is.EqualTo(new Vector3[] { new(30, 0, 0), new(30, 0, 44.25f), new(30, 0, 60) })
            .Using<Vector3>((a, b) => Vector3.Distance(a, b) < 0.01f), "to the seam, up it, onto its top");
    }

    // From the link's top the step is cut to the goal's distance again: whole, it took bots up to 58 units inside their
    // stop radius (the fourth review of #166).
    [Test]
    public void APlayerBotOffALinksTopStopsAtItsStopDistance()
    {
        var (region, zone) = World();
        var mesh = new SeamMesh();
        var path = new PlayerTravelPath();
        Vector3 position = Vector3.Zero, goal = new(100, 0, 60);
        for (int tick = 0; tick < 10 && !PlayerBotPathMover.Arrived(position, goal, 50); tick++)
            Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, path, position, goal, 50, 47.75f, tick * 250,
                out position, out string failure), Is.True, failure);
        Assert.That((position.Z, Vector3.Distance(position, goal)), Is.EqualTo((60f, 49f))
            .Using<(float, float)>((a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 0.01f), "on the ramp, one unit inside the stop radius");
    }

    // A link that Detour's seams don't flag, 150 units up over 41 across: the corridor passes its top within a corner's
    // reach (PTP:177), 12.25 units short of it here, and the step goes on for the goal, cut to its distance. Whole, it took
    // the bot 23.9 units inside its stop radius (the fifth review of #166).
    [Test]
    public void APlayerBotPastAnUnflaggedLinksTopStopsAtItsStopDistance()
    {
        var (region, zone) = World();
        var mesh = new SeamMesh { Ramp = 150, Run = 41, Split = 75, Flag = EDtPolyFlags.Walk };
        var path = new PlayerTravelPath();
        Vector3 position = Vector3.Zero, goal = new(141, 0, 150);
        var steps = new List<Vector3>();
        for (int tick = 0; tick < 10 && !PlayerBotPathMover.Arrived(position, goal, 50); tick++)
        {
            Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, path, position, goal, 50, 47.75f, tick * 250,
                out position, out string failure), Is.True, failure);
            steps.Add(position);
        }
        Assert.That(steps, Has.Some.Matches<Vector3>(p => Vector3.Distance(p, new(67.77f, 0, 138.18f)) < 0.1f), "12.25 units short of the top");
        Assert.That((position.Z, Vector3.Distance(position, goal)), Is.EqualTo((150f, 49f))
            .Using<(float, float)>((a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 1), "on the ramp, about one unit inside the stop radius");
    }

    // A ramp (Z 60) over a floor walled in under it (Z 0, from X 200 to 300) that the mesh links to it only at tile seams,
    // as in Jordheim (#166). Detour's plain surface move is 2D: from the ramp, toward a point over that floor, it enters
    // the floor, the first linked polygon whose footprint holds the point; the layered one keeps to the ramp. The corridor
    // runs straight along the ramp, which is open to the goal.
    private sealed class PocketMesh : PathfindingMgrBase
    {
        public override bool HasNavmesh(Zone zone) => true;
        private static bool OverFloor(Vector3 p) => p.X >= 200 && p.X <= 300;
        private static Vector3 Floor(Vector3 p) => new(Math.Clamp(p.X, 200, 300), p.Y, 0);
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination)
        {
            destination[0] = new(start, EDtPolyFlags.Walk);
            destination[1] = new(end, EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            start.Z < 30 || OverFloor(end) ? Floor(end) : new(end.X, end.Y, 60);
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded) =>
            start.Z < 30 ? Floor(end) : new(end.X, end.Y, 60);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => position.Z < 30 && OverFloor(position) ? Floor(position) : new(position.X, position.Y, 60);
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => true;
    }

    // st-final4 (#166): player bots walking a Jordheim ramp stepped down onto the floor walled in under it and stood there.
    // From afar by the corridor, and within 300 units by the direct step: both keep to the ramp.
    [TestCase(0f, 700f)] [TestCase(100f, 400f)]
    public void APlayerBotKeepsToTheRampOverAFloorWalledInUnderIt(float from, float to)
    {
        var (region, zone) = World();
        var path = new PlayerTravelPath();
        Vector3 position = new(from, 0, 60), goal = new(to, 0, 60);
        for (int tick = 0; tick < 20 && !PlayerBotPathMover.Arrived(position, goal, 20); tick++)
        {
            Assert.That(PlayerBotPathMover.TryComputeNext(new PocketMesh(), region, zone, path, position, goal, 20, 47.75f, tick * 250,
                out position, out string failure), Is.True, failure);
            Assert.That(position.Z, Is.EqualTo(60), $"on the ramp at X {position.X}");
        }
        Assert.That(PlayerBotPathMover.Arrived(position, goal, 20), Is.True);
    }

    // A hill (floor Z 100 from X 100 to 200, Z 0 elsewhere) between the bot and its target, in sight across it (the ray
    // is 2D). In Jordheim the line from the top of a ramp to a target at its foot ran 25-40 units under the ramp, over a
    // floor walled in below it, and the next direct step stood on that floor (#166). Where the line runs more than 24
    // units under the floor the bot follows, it takes the corridor; under a lower hill the direct step stays.
    private sealed class HillMesh : PathfindingMgrBase
    {
        public float Height;
        public int Searches;
        public bool NoWay;
        public float Link = float.MaxValue;     // a link to another floor across the hill's line here: the layered moves stop at it
        public float Edge = float.MaxValue;     // from here on the line runs along a polygon's border edge: no grounded move ends there
        public override bool HasNavmesh(Zone zone) => true;
        private Vector3 On(Vector3 p) => new(p.X, p.Y, p.X >= 100 && p.X <= 200 ? Height : 0);
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination)
        {
            Searches++;
            if (NoWay) return new(PathfindingStatus.NoPathFound, 0);
            destination[0] = new(start, EDtPolyFlags.Walk);
            destination[1] = new(end, EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => end;
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded)
        {
            Vector3 to = start.X < Link && end.X > Link ? new(Link, end.Y, end.Z) : end;
            return !grounded ? to : to.X >= Edge ? null : On(to);
        }
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => On(position);
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => true;
    }

    // Past where the bot stops (200 units short), the hill does not count.
    [TestCase(100f, 0, 1)] [TestCase(20f, 0, 0)] [TestCase(100f, 200, 0)]
    public void TheDirectStepTakesTheCorridorWhereTheLineRunsUnderTheFloor(float hill, int stop, int searches)
    {
        var (region, zone) = World();
        var mesh = new HillMesh { Height = hill };
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), Vector3.Zero, new(280, 0, 0), stop, 47.75f, 0,
            out _, out string failure), Is.True, failure);
        Assert.That(mesh.Searches, Is.EqualTo(searches));
    }

    // A link to another floor on the line, 100 units on, which the 2D ray sees through: where the floor walk meets it before
    // the bot stops, it takes the corridor. In Jordheim the line and the corridor took turns by such links, the direct
    // steps leaving the corridor's next corner behind the bot, back and forth for about 15 seconds, until the churn watch
    // planned the walk again (the review of #166).
    [TestCase(0, 1)] [TestCase(200, 0)]
    public void TheDirectStepTakesTheCorridorWhereTheLineLeavesTheFloor(int stop, int searches)
    {
        var (region, zone) = World();
        var mesh = new HillMesh { Link = 100 };
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), Vector3.Zero, new(280, 0, 0), stop, 47.75f, 0,
            out _, out string failure), Is.True, failure);
        Assert.That(mesh.Searches, Is.EqualTo(searches));
    }

    // From 50 units on, the line runs along a polygon's border edge, where no grounded move ends though nothing blocks the
    // way: the bot steps straight on. Taken as a wall, it stalled bots under targets raised over the floor by such an edge
    // (the third review of #166). A link on the edge, where the plain move stops too, still takes the corridor, also 0.7
    // units before the last point the bot walks to (stopping 100 short, at X 181): taken as the edge, it put bots by a
    // Jordheim link's foot onto the floor walled in there, and back and forth (the fourth review).
    [TestCase(float.MaxValue, 0, 0)] [TestCase(100f, 0, 1)] [TestCase(180.3f, 100, 1)]
    public void TheDirectStepGoesOnAlongABorderEdgeButNotOverALink(float link, int stop, int searches)
    {
        var (region, zone) = World();
        var mesh = new HillMesh { Edge = 50, Link = link };
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), Vector3.Zero, new(280, 0, 0), stop, 47.75f, 0,
            out _, out string failure), Is.True, failure);
        Assert.That(mesh.Searches, Is.EqualTo(searches));
    }

    // A ledge (floor Z Height) up to X 100 and a floor at Z 0 beyond it, joined there, as at a Gripklosa seam whose floors
    // part (#166). The moves take the floor under their end, the 2D ray sees across, and the closest point is the floor
    // under the point within the box's height. With NoWay no corridor is found; with Cliff the corridor's box (over 64
    // units high) finds the ledge's edge for any point, as Detour's nearest polygon to a goal below a ledge can be the
    // ledge: the corridor ends at the edge, though the moves still reach the floor beyond. With Hill a hill as high as the
    // ledge stands on that floor from X 200 to 260.
    private sealed class DropMesh : PathfindingMgrBase
    {
        public float Height = 100;
        public int Searches;
        public bool NoWay, Cliff, Hill;
        public override bool HasNavmesh(Zone zone) => true;
        private Vector3 On(Vector3 p) => new(p.X, p.Y, p.X < 100 || Hill && p.X >= 200 && p.X <= 260 ? Height : 0);
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination)
        {
            Searches++;
            if (NoWay) return new(PathfindingStatus.NoPathFound, 0);
            destination[0] = new(start, EDtPolyFlags.Walk);
            destination[1] = new(end, EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => On(end);
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded) =>
            On(end);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => Cliff && zRange > 64 ? new(Math.Min(position.X, 99), position.Y, Height)
            : Math.Abs(On(position).Z - position.Z) <= zRange ? On(position) : null;
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => true;
    }

    // From the ledge, the line to a goal on the floor beyond runs more than 24 units over that floor: the corridor takes the
    // bot down. In Gripklosa the direct step floated it off a ledge 94 units up, and from the air the next corridor step
    // took it back up onto the ledge, Detour's nearest polygon, back and forth for good (the fourth review of #166). Not
    // off a ledge 20 units up, and not to a goal raised 40 units over the floor, but at no more than that height either.
    // A goal raised beyond Detour's 64-unit box, whose floor it does not find, is walked at straight, as before.
    [TestCase(100f, 0f, 1)] [TestCase(20f, 0f, 0)] [TestCase(60f, 40f, 0)] [TestCase(100f, 40f, 1)] [TestCase(100f, 80f, 0)]
    public void TheDirectStepTakesTheCorridorOffALedge(float ledge, float raised, int searches)
    {
        var (region, zone) = World();
        var mesh = new DropMesh { Height = ledge };
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), new(60, 0, ledge), new(280, 0, raised), 0,
            47.75f, 0, out _, out string failure), Is.True, failure);
        Assert.That(mesh.Searches, Is.EqualTo(searches));
    }

    // Where no corridor leads down off the ledge (NoWay), or the corridor ends where the bot stands on its edge (Cliff),
    // the bot still takes the line over the drop, as before the fourth review of #166: without it, in the offline
    // sweeps, 38 walks to goals that the line reached ended, 37 of them at once, most in Tir na Nog and Jordheim. Under
    // the floor it doesn't (WithNoCorridorRoundTheHillTheWalkEnds), nor over a drop and then under the floor.
    [TestCase(false)] [TestCase(true)]
    public void WithNoCorridorDownOffALedgeTheBotTakesTheLine(bool cliff)
    {
        var (region, zone) = World();
        var mesh = new DropMesh { NoWay = !cliff, Cliff = cliff };
        Vector3 from = new(cliff ? 99 : 60, 0, 100), goal = new(cliff ? 150 : 280, 0, cliff ? 20 : 0);
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), from, goal, 0, 47.75f, 0,
            out Vector3 next, out string failure), Is.True, failure);
        Assert.That((mesh.Searches, next.X), Is.EqualTo((1, PlayerTravelPath.Advance(from, goal, 47.75f).X))
            .Using<(int, float)>((a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 0.01f), "a step along the line");
    }

    // Over the drop and then under a hill beyond it, the walk ends at once, as before the fourth review of #166: taken over
    // the drop, the line floated the bot off the ledge and the walk ended in the air before the hill (the fifth review).
    [Test]
    public void WithNoCorridorOverADropAndUnderAHillTheWalkEnds()
    {
        var (region, zone) = World();
        var mesh = new DropMesh { NoWay = true, Hill = true };
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), new(60, 0, 100), new(290, 0, 0), 0, 47.75f, 0,
            out _, out string failure), Is.False);
        Assert.That((mesh.Searches, failure), Is.EqualTo((1, "No complete walkable path was found from here; try an open road or another destination.")));
    }

    // With no corridor round the hill, as to a goal on a mesh island the 2D ray sees, the walk ends: the line would take the
    // bot through the hill (the review of #166: taken anyway, it sank bots through floors in Jordheim).
    [Test]
    public void WithNoCorridorRoundTheHillTheWalkEnds()
    {
        var (region, zone) = World();
        var mesh = new HillMesh { Height = 100, NoWay = true };
        Assert.That(PlayerBotPathMover.TryComputeNext(mesh, region, zone, new PlayerTravelPath(), Vector3.Zero, new(280, 0, 0), 0, 47.75f, 0,
            out _, out string failure), Is.False);
        Assert.That((mesh.Searches, failure), Is.EqualTo((1, "No complete walkable path was found from here; try an open road or another destination.")));
    }

    // A shelf at z 0, everywhere, the only floor: the closest-point query finds it under or over any point.
    private sealed class ShelfMesh : PathfindingMgrBase
    {
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
            Span<WrappedPathfindingNode> destination)
        {
            destination[0] = new(start, EDtPolyFlags.Walk);
            destination[1] = new(end, EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => new(end.X, end.Y, 0);
        public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded) =>
            new(end.X, end.Y, 0);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => new(position.X, position.Y, 0);
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => true;
    }

    // A roam goal 300 units straight under the bot: the corridor takes the shelf under it for the goal and ends where the
    // bot stands, so the walk ends at once, not after a 3-second stall (st-layers2, #166).
    [Test]
    public void AWalkToAGoalUnderTheFloorItsCorridorEndsOnEndsAtOnce()
    {
        var (region, zone) = World();
        Assert.That(PlayerBotPathMover.TryComputeNext(new ShelfMesh(), region, zone, new PlayerTravelPath(), Vector3.Zero, new(0, 0, -300), 0,
            47.75f, 0, out _, out string failure), Is.False);
        Assert.That(failure, Is.EqualTo(PlayerBotPathMover.OffTheCorridor));
    }

    // The walkable mesh is the strip y >= 0.4. From a point off it, the surface move only snaps back onto it (0.4 units),
    // as Detour did 0.6 units off the mesh by Olorustos (lv-r1-stall, 2026-10-25), and the line of sight is refused.
    private sealed class SliverMesh : PathfindingMgrBase
    {
        private static Vector3 OnMesh(Vector3 v) => new(v.X, Math.Max(v.Y, 0.4f), v.Z);
        public override bool HasNavmesh(Zone zone) => true;
        public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end,
            EDtPolyFlags[] filters, Span<WrappedPathfindingNode> destination)
        {
            destination[0] = new(OnMesh(start), EDtPolyFlags.Walk);
            destination[1] = new(OnMesh(end), EDtPolyFlags.Walk);
            return new(PathfindingStatus.PathFound, 2);
        }
        public override Vector3? GetMoveAlongSurface(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) =>
            start.Y < 0.4f ? OnMesh(start) : OnMesh(end);
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
            EDtPolyFlags[] filters) => OnMesh(position);
        public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => start.Y >= 0.4f;
    }

    // StepToward's position and stall rule: GamePlayer keeps whole units (X, Y, Z are ints), so the bot stands where the
    // mover put it, rounded; a second without a unit of progress plans the corridor again (twice a walk), and 12 steps drop it. Returns
    // the tick the bot arrived, -1 if it never did, or with blockEnds -2 where the mover's walk would end blocked.
    internal static int Walk(IPathfindingMgr nav, Region region, Zone zone, Vector3 from, Vector3 goal, bool keepExact,
        int ticks = 200, bool blockEnds = false)
    {
        var path = new PlayerTravelPath();
        Vector3 player = from;
        Vector3? exact = null;
        var stall = new PlayerBotStallWatch();
        for (int tick = 0; tick < ticks; tick++)
        {
            if (keepExact ? PlayerBotPathMover.Arrived(player, goal, 20) : Vector3.Distance(player, goal) <= 20) return tick;
            stall.Toward(goal, tick * 250L);
            Vector3 current = keepExact ? PlayerBotPathMover.StepStart(player, exact) : player;
            Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, path, current, goal, 20, 47.75f, tick * 250L,
                out Vector3 next, out string failure), Is.True, failure);
            exact = next;
            if (Vector3.DistanceSquared(next, current) < 1)
            {
                if (stall.Still(path, out _))
                {
                    if (blockEnds) return -2;                                   // the mover's walk ends blocked here
                    stall.Reset(); path.Clear();
                }
                continue;
            }
            stall.Moved();
            player = new(MathF.Round(next.X), MathF.Round(next.Y), MathF.Round(next.Z));
        }
        return -1;
    }

    [Test]
    public void A_bot_rounded_off_the_mesh_walks_on_from_where_the_mover_put_it()
    {
        var (region, zone) = World();
        Assert.That(Walk(new SliverMesh(), region, zone, Vector3.Zero, new(1000, 0, 0), keepExact: false), Is.EqualTo(-1), "the stall by Olorustos");
        Assert.That(Walk(new SliverMesh(), region, zone, Vector3.Zero, new(1000, 0, 0), keepExact: true), Is.InRange(1, 25));
    }

    // The exact spot counts while the bot stands where the mover put it (rounding moves it at most 0.87 units); after a
    // teleport, a release or a speed-hack correction the bot's own position counts.
    [Test]
    public void The_next_step_starts_from_the_exact_spot_only_while_the_bot_stands_on_it()
    {
        Vector3 exact = new(497254.4f, 587785.6f, 2310.4f);
        Assert.That(PlayerBotPathMover.StepStart(new(497254, 587786, 2310), exact), Is.EqualTo(exact));
        Assert.That(PlayerBotPathMover.StepStart(new(497254, 587786, 2310), null), Is.EqualTo(new Vector3(497254, 587786, 2310)));
        Assert.That(PlayerBotPathMover.StepStart(new(497300, 587786, 2310), exact), Is.EqualTo(new Vector3(497300, 587786, 2310)));
        Assert.That(PlayerBotPathMover.StepStart(new(497254, 587786, 2312), exact), Is.EqualTo(new Vector3(497254, 587786, 2312)));
    }

    // The driver is in range when GetDistanceTo, the whole-unit distance cut to an int (Point3D.cs:78-87), is within the
    // stop radius: the mover's arrival agrees.
    [TestCase(20.9f, true)] [TestCase(20f, true)] [TestCase(21f, false)] [TestCase(21.5f, false)]
    public void A_bot_has_arrived_where_the_driver_counts_it_in_range(float distance, bool expected) =>
        Assert.That(PlayerBotPathMover.Arrived(new(distance, 0, 0), Vector3.Zero, 20), Is.EqualTo(expected));

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
        Assert.That(PlayerBotDriver.CombatAction(PlayerBotDriver.ShouldCast(hasSpell, mana, 1_000, blockedUntil), interrupted, 0,
            interruptible, false, due: true, heldBreaksOnHit: false) == PlayerBotCombatAction.Cast, Is.EqualTo(casts));

    // A class with Quickcast, its damage spell blocked only by the hits' interrupt, uses it as a player does, then casts
    // (SpellHandler.cs:578-590 lets a quickcast spell through). Not while it is on (the ability toggles it off,
    // QuickCastAbilityHandler.cs:51-55), nor in its 30-s disable after a quickcast spell (SpellHandler.cs:1430-1434;
    // CastingComponent.cs:487), nor while casting (QuickCastAbilityHandler.cs:44-48), nor for a spell the cast watch
    // blocked, one the bot lacks the power for, or an instant one, which goes anyway.
    [TestCase(true, false, 0, 0L, false, true, (byte)50, 0L, true, true, true)]
    [TestCase(false, false, 0, 0L, false, true, (byte)50, 0L, true, true, false)]           // no Quickcast
    [TestCase(true, true, 0, 0L, false, true, (byte)50, 0L, true, true, false)]             // on already
    [TestCase(true, false, 1, 0L, false, true, (byte)50, 0L, true, true, false)]            // disabled
    [TestCase(true, false, 0, 1_001L, false, true, (byte)50, 0L, true, true, false)]        // a try 2 s ago
    [TestCase(true, false, 0, 0L, true, true, (byte)50, 0L, true, true, false)]             // casting
    [TestCase(true, false, 0, 0L, false, false, (byte)50, 0L, true, true, false)]           // no damage spell
    [TestCase(true, false, 0, 0L, false, true, (byte)17, 0L, true, true, false)]            // low power
    [TestCase(true, false, 0, 0L, false, true, (byte)50, 1_001L, true, true, false)]        // the watch blocked it
    [TestCase(true, false, 0, 0L, false, true, (byte)50, 0L, false, true, false)]           // not interrupted: casts
    [TestCase(true, false, 0, 0L, false, true, (byte)50, 0L, true, false, false)]           // instant: casts
    public void An_interrupted_caster_with_Quickcast_uses_it_then_casts(bool hasQuickcast, bool quickcastOn, int disabled,
        long nextTry, bool casting, bool hasSpell, byte mana, long blockedUntil, bool interrupted, bool interruptible,
        bool uses) =>
        Assert.That(PlayerBotDriver.CombatAction(PlayerBotDriver.ShouldCast(hasSpell, mana, 1_000, blockedUntil), interrupted, 0,
            interruptible, PlayerBotDriver.QuickcastReady(hasQuickcast, quickcastOn, disabled, nextTry, casting, 1_000),
            due: true, heldBreaksOnHit: false) == PlayerBotCombatAction.Quickcast, Is.EqualTo(uses));

    // #167 D3: a caster casts by its damage spell's cost, not 18% (below it canCast was false and it swung: 14 of
    // st-deathsC's 20 deaths under 25% power waited 0, with a nuke of about 9% still paid for). While its target isn't
    // held it keeps a control's cost in reserve (reserve, ControlReserve) for the escape. The cost is the server's
    // (PowerCost): a negative power a percent of the pool, a focus caster's factor (FocusCostFactor), doubled under
    // Quickcast for a spell with a cast time; and under Quickcast the server checks no cost, only some power left.
    [TestCase(290, 30, 25, 0, 1.0, false, 3_000, true)] [TestCase(290, 20, 25, 0, 1.0, false, 3_000, false)]
    [TestCase(290, 45, 25, 20, 1.0, false, 3_000, true)] [TestCase(290, 44, 25, 20, 1.0, false, 3_000, false)]
    [TestCase(300, 30, -10, 0, 1.0, false, 3_000, true)] [TestCase(300, 29, -10, 0, 1.0, false, 3_000, false)]
    [TestCase(290, 30, 25, 0, 1.2, false, 3_000, true)] [TestCase(290, 29, 25, 0, 1.2, false, 3_000, false)]  // no focus
    [TestCase(290, 20, 25, 0, 0.8, false, 3_000, true)] [TestCase(290, 19, 25, 0, 0.8, false, 3_000, false)]  // its focus
    [TestCase(290, 70, 25, 20, 1.0, true, 3_000, true)] [TestCase(290, 69, 25, 20, 1.0, true, 3_000, false)]  // Quickcast
    [TestCase(290, 45, 25, 20, 1.0, true, 0, true)] [TestCase(290, 44, 25, 20, 1.0, true, 0, false)]          // instant
    [TestCase(290, 5, 25, 0, 1.0, true, 3_000, true)]                       // no reserve: Quickcast casts at any power
    [TestCase(290, 0, 25, 0, 1.0, true, 3_000, false)]                      //   but none (SpellHandler.cs:967-973)
    public void A_caster_casts_by_the_spells_cost_and_keeps_a_control_in_reserve(int maxMana, int mana, int power,
        int reserve, double focusFactor, bool quickcast, int castTime, bool casts) =>
        Assert.That(PlayerBotDriver.CasterCanCast(maxMana, mana, power, reserve, focusFactor, quickcast, castTime),
            Is.EqualTo(casts));

    // The server's cost (SpellHandler.cs:1181-1240), its own truncation too: 15 at the full focus factor (1.2 - 0.4 is
    // just under 0.8 in doubles) is 11, not 12.
    [TestCase(290, 25, 1.0, false, 3_000, 25)] [TestCase(300, -10, 1.0, false, 3_000, 30)]
    [TestCase(290, 25, 1.2, false, 3_000, 30)] [TestCase(290, 25, 1.2, true, 3_000, 60)] [TestCase(290, 25, 1.2, true, 0, 30)]
    [TestCase(290, 15, 1.2 - 0.4, false, 3_000, 11)]
    public void A_spells_power_cost_is_the_servers(int percentBase, int power, double focusFactor, bool quickcast,
        int castTime, int cost) =>
        Assert.That(PlayerBotDriver.PowerCost(percentBase, power, focusFactor, quickcast, castTime), Is.EqualTo(cost));

    // A focus caster pays 120% with no focus, down to 80% (SpellHandler.cs:1214-1233): the focus level x 0.4 over the
    // spell's level, at most 0.4, scaled by its spec over the spell's level up to 1. Anyone else, or a line with no focus,
    // pays 100%.
    [TestCase(false, 50, 20, 20, 1.0)] [TestCase(true, 0, 20, 20, 1.2)] [TestCase(true, 50, 20, 20, 0.8)]
    [TestCase(true, 10, 20, 20, 1.0)] [TestCase(true, 50, 20, 10, 1.0)] [TestCase(true, 50, 20, 40, 0.8)]
    public void A_focus_casters_cost_runs_from_120_to_80_percent(bool focusCaster, int focusLevel, int spellLevel,
        int specLevel, double factor) =>
        Assert.That(PlayerBotDriver.FocusCostFactor(focusCaster, focusLevel, spellLevel, specLevel),
            Is.EqualTo(factor).Within(1e-9));

    // The reserve: the cheapest known control's cost while the target isn't held, else none (one root stays for the
    // escape). #167 E C2b: none for the killing quickcast.
    [TestCase(new[] { 20, 12 }, false, false, 12)] [TestCase(new[] { 20, 12 }, true, false, 0)]
    [TestCase(new int[0], false, false, 0)] [TestCase(new[] { 20, 12 }, false, true, 0)]
    public void A_caster_keeps_its_cheapest_control_in_reserve_while_its_target_is_free(int[] costs, bool held,
        bool killingQuickcast, int reserve) =>
        Assert.That(PlayerBotDriver.ControlReserve(costs, held, killingQuickcast), Is.EqualTo(reserve));

    // #167 E C2b: the killing quickcast keeps no control in reserve, so a dry Mentalist (7 of 300 power, Aurora Ray 26, the
    // reserve Prismatic Shimmer's 16) casts it: the server checks only power left under Quickcast.
    [Test]
    public void A_dry_caster_quickcasts_its_killing_nuke_with_no_reserve()
    {
        Assert.That(PlayerBotDriver.CasterCanCast(300, 7, 26,
            PlayerBotDriver.ControlReserve(new[] { 16 }, held: false, killingQuickcast: true), 1.0, true, 2_600), Is.True);
        Assert.That(PlayerBotDriver.CasterCanCast(300, 7, 26,
            PlayerBotDriver.ControlReserve(new[] { 16 }, held: false, killingQuickcast: false), 1.0, true, 2_600), Is.False,
            "the reserve kept");
    }

    // The cast gate by role: a caster's is its cost (CasterCanCast), every other role keeps 18% (ShouldCast's rows hold
    // for them), and the cast watch blocks either.
    [TestCase(PlayerBotRole.Caster, true, (byte)10, true, 0L, true)]
    [TestCase(PlayerBotRole.Caster, true, (byte)6, false, 0L, false)]
    [TestCase(PlayerBotRole.Healer, true, (byte)10, true, 0L, false)]
    [TestCase(PlayerBotRole.Caster, true, (byte)100, true, 5_000L, false)]                   // the cast watch
    [TestCase(PlayerBotRole.Caster, false, (byte)100, true, 0L, false)]
    [TestCase(PlayerBotRole.Caster, true, (byte)100, false, 0L, false)]                      // its cost, not 18%
    public void A_casters_cast_gate_is_its_spells_cost(PlayerBotRole role, bool hasSpell, byte mana, bool casterCanCast,
        long blockedUntil, bool casts) =>
        Assert.That(PlayerBotDriver.ShouldCastFor(role, hasSpell, mana, casterCanCast, 0, blockedUntil), Is.EqualTo(casts));

    [Test]
    public void Every_other_role_keeps_the_18_percent_gate()
    {
        foreach (PlayerBotRole role in new[] { PlayerBotRole.Melee, PlayerBotRole.Healer, PlayerBotRole.Pet, PlayerBotRole.Archer })
        foreach (bool hasSpell in new[] { false, true })
        foreach (byte mana in new byte[] { 0, 17, 18, 100 })
        foreach (long blockedUntil in new[] { 0L, 1_000L, 1_001L })
        foreach (bool casterCanCast in new[] { false, true })
            Assert.That(PlayerBotDriver.ShouldCastFor(role, hasSpell, mana, casterCanCast, 1_000, blockedUntil),
                Is.EqualTo(PlayerBotDriver.ShouldCast(hasSpell, mana, 1_000, blockedUntil)), $"{role} {hasSpell} {mana} {blockedUntil}");
    }

    // #167 F6: the bot's own swing must not block its next cast. A swing interrupts the swinger for 3 s
    // (StartInterruptTimer, GameLiving.cs:686-693; InterruptRemainingDuration, :736), and a player then gets "must wait"
    // (SpellHandler.cs:570-590): with a spell it can cast and no monster interrupting it, the bot stops swinging and waits
    // it out, as a GameBot caster ends its own swings before a spell (BotBrain.cs:3074-3080). A hit from another still
    // means Quickcast or a swing; an instant or uninterruptible spell goes whatever the interrupt.
    [TestCase(true, false, 0L, true, false, PlayerBotCombatAction.Cast)]
    [TestCase(true, false, 2_000L, true, false, PlayerBotCombatAction.WaitToCast)]
    [TestCase(true, true, 0L, true, true, PlayerBotCombatAction.Quickcast)]
    [TestCase(true, true, 0L, true, false, PlayerBotCombatAction.Swing)]
    [TestCase(true, true, 0L, false, false, PlayerBotCombatAction.Cast)]                   // an instant spell
    [TestCase(true, false, 2_000L, false, false, PlayerBotCombatAction.Cast)]              // an instant spell
    [TestCase(true, true, 2_000L, true, false, PlayerBotCombatAction.Swing)]               // another's hit decides
    [TestCase(false, false, 2_000L, true, false, PlayerBotCombatAction.Swing)]             // nothing to cast
    public void The_bots_own_swing_does_not_block_its_next_cast(bool canCastSpell, bool interruptedByOther, long selfInterrupt,
        bool interruptible, bool quickcastReady, PlayerBotCombatAction action) =>
        Assert.That(PlayerBotDriver.CombatAction(canCastSpell, interruptedByOther, selfInterrupt, interruptible, quickcastReady,
            due: true, heldBreaksOnHit: false), Is.EqualTo(action));

    // Between casts (the cast choice not due yet, :417) the old think restarted the swing after every cast, and that swing
    // blocked the next cast: with a spell it can cast the bot waits instead. An instant spell does not mind the swing.
    [TestCase(true, false, 0L, true, PlayerBotCombatAction.WaitToCast)]
    [TestCase(true, false, 0L, false, PlayerBotCombatAction.Swing)]                        // an instant spell
    [TestCase(false, false, 0L, true, PlayerBotCombatAction.Swing)]                        // the watch's block, no power
    [TestCase(true, true, 0L, true, PlayerBotCombatAction.Swing)]                          // hit: the old rule
    public void Between_casts_a_bot_with_a_spell_to_cast_does_not_swing(bool canCastSpell, bool interruptedByOther,
        long selfInterrupt, bool interruptible, PlayerBotCombatAction action) =>
        Assert.That(PlayerBotDriver.CombatAction(canCastSpell, interruptedByOther, selfInterrupt, interruptible, false,
            due: false, heldBreaksOnHit: false), Is.EqualTo(action));

    // The stop distance comes from the spell the bot may cast, not from this think's action: between casts it swings
    // with an instant spell (above), yet holds its spell range. A Bonedancer's Crunch Skeleton (Lifedrain, instant,
    // 1,500 units) cast from 1,420 units walked it toward melee until its next choice, every 4 s. A hit that blocks an
    // interruptible spell with no Quickcast ready, or nothing to cast, still means melee, as at d1d524d.
    [TestCase(true, false, false, false, 1_420)]                                          // an instant spell
    [TestCase(true, false, true, false, 1_420)]
    [TestCase(true, true, false, false, 1_420)]                                           // an instant spell, hit
    [TestCase(true, true, true, true, 1_420)]                                             // hit, Quickcast ready
    [TestCase(true, true, true, false, 152)]                                              // hit, no Quickcast: melee
    [TestCase(false, false, false, false, 152)]                                           // nothing to cast
    public void Between_casts_a_bot_holds_its_spell_range(bool canCastSpell, bool interruptedByOther, bool interruptible,
        bool quickcastReady, int stop)
    {
        Assert.That(PlayerBotDriver.CombatStopDistance(canCastSpell, interruptedByOther, interruptible, quickcastReady, 200, 1_500,
            casterHold: false), Is.EqualTo(stop));
        Assert.That(PlayerBotDriver.CombatAction(true, false, 0, false, false, due: false, heldBreaksOnHit: false),
            Is.EqualTo(PlayerBotCombatAction.Swing));
        Assert.That(1_420 > PlayerBotDriver.CombatStopDistance(true, false, false, false, 200, 1_500, casterHold: false), Is.False,
            "at 1,420 units an instant caster between casts takes no step toward its target");
    }

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

    // --- casters control and step back (#167 F11) ---

    // World-DB spells (Spell, LineXSpell): a Sorcerer's Body Destruction roots and Mind Twisting mezzes, a Wizard's
    // Path of Ice root, an Eldritch's Way of the Sun stun, a Bonedancer's snare and a Wizard's area root.
    private static Spell Control(int id, string name, string type, int level, double value, int radius = 0,
        double castTime = 2.5, int duration = 20) =>
        new(new DbSpell { SpellID = id, Name = name, Type = type, Target = "Enemy", Value = value, Range = 1500,
            Radius = radius, CastTime = castTime, Duration = duration }, level);

    private static readonly SpellLine BodyLine = new("Body Destruction", "Body Destruction", Specs.Body_Magic, true),
        SunLine = new("Way of the Sun", "Way of the Sun", Specs.Light, true);

    private static readonly Spell LesserCrippling = Control(682, "Lesser Crippling", "SpeedDecrease", 6, 99),
        Cripple = Control(683, "Cripple", "SpeedDecrease", 13, 99),
        MinorMesmerization = Control(911, "Minor Mesmerization", "Mesmerize", 1, 0, castTime: 3.0),
        LesserMesmerization = Control(912, "Lesser Mesmerization", "Mesmerize", 5, 0, castTime: 3.0),
        ColdsBitterGrip = Control(171, "Cold's Bitter Grip (Lesser)", "SpeedDecrease", 5, 99),
        PrismaticFlare = Control(4121, "Prismatic Flare", "Stun", 5, 0),
        ShroudOfDoubt = Control(10082, "Shroud of Doubt", "SpeedDecrease", 14, 50, castTime: 3.0),
        TremblingEarth = Control(78, "Trembling Earth", "SpeedDecrease", 13, 99, radius: 350);

    // The GameBot's control split (GameBot.cs:1957-1962: a mez, or a speed decrease of 99, a root), with the stun the
    // brief adds, single-target only: an area one would pull the adds round the target.
    [Test]
    public void A_control_spell_is_a_single_target_root_mez_or_stun()
    {
        Assert.That(new[] { ColdsBitterGrip, Cripple, MinorMesmerization, PrismaticFlare }.All(PlayerBotDriver.IsControlSpell));
        Assert.That(PlayerBotDriver.IsControlSpell(ShroudOfDoubt), Is.False, "a snare (50%) is no root");
        Assert.That(PlayerBotDriver.IsControlSpell(TremblingEarth), Is.False, "an area root");
        Assert.That(PlayerBotDriver.IsControlSpell(FungalOoze), Is.False, "a nuke");
    }

    // The review of 9aacd69: a Minstrel's Instruments mezzes (Compelling Cadence and the rest: Mesmerize, Enemy, range
    // 1,500, InstrumentRequirement 3) counted as controls, so its add step spent its 3 tries an add on a mez the server
    // refuses with no instrument (SpellHandler.cs:546-553). A song is no control, as the damage spells, heals, self-buffs
    // and chants already leave it out; a Bard's mezzes need no instrument (InstrumentRequirement 0) and stay.
    [Test]
    public void A_song_that_needs_an_instrument_is_no_control()
    {
        Spell cadence = new(new DbSpell { SpellID = 5001, Name = "Compelling Cadence", Type = "Mesmerize", Target = "Enemy",
            Range = 1500, CastTime = 3.0, Duration = 20, InstrumentRequirement = 3 }, 20);
        Spell melody = new(new DbSpell { SpellID = 5002, Name = "Mesmerizing Melody", Type = "Mesmerize", Target = "Enemy",
            Range = 1500, CastTime = 3.0, Duration = 20 }, 20);
        Assert.That((PlayerBotDriver.IsControlSpell(cadence), PlayerBotDriver.IsControlSpell(melody)), Is.EqualTo((false, true)));
    }

    // The stun first (damage doesn't break it, GameLiving.cs:1784-1803), then the root, then the mez (the server resists
    // a mez on a monster under 75% health, CCSpellHandler.cs:173-177); the highest level of each; only one the target
    // can take now (allowed, #167 C8: the server's own tests) and that is ready.
    [Test]
    public void A_caster_controls_with_a_stun_then_a_root_then_a_mez_the_highest_ready()
    {
        (Spell, SpellLine)[] sorcerer = [(LesserCrippling, BodyLine), (Cripple, BodyLine), (MinorMesmerization, BodyLine),
            (LesserMesmerization, BodyLine)];
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, _ => true, false, false).Item1, Is.SameAs(Cripple));
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, s => s != Cripple, _ => true, false, false).Item1,
            Is.SameAs(LesserCrippling));
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, s => s.SpellType != eSpellType.SpeedDecrease, false,
            false).Item1, Is.SameAs(LesserMesmerization), "a target that can't be rooted");
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, s => s.SpellType == eSpellType.Stun, false, false).Item1,
            Is.Null);
        (Spell, SpellLine)[] both = [(Cripple, BodyLine), (PrismaticFlare, SunLine)];
        Assert.That(PlayerBotDriver.ChooseControl(both, _ => true, _ => true, false, false).Item1, Is.SameAs(PrismaticFlare));
        Assert.That(PlayerBotDriver.ChooseControl(both, _ => true, s => s.SpellType != eSpellType.Stun, false, false).Item1,
            Is.SameAs(Cripple), "stun-immune");
    }

    // #167 C8: the server's own immunity test. An NPC's stun or mez starts a 60-s NpcImmunityEffect that halves each next
    // hold and refuses one under 1 s (ECSImmunityEffect.cs:38-51; CCSpellHandler.cs:168-169, :282-283), which the old
    // StunOk and MezOk read as full immunity: Eldritch and Mentalist cast 0.90 and 0.92 controls per kill, the root classes 1.30-1.81.
    // World-DB stuns (Way of the Sun): an 8-s Prismatic Sheen holds 4 s the second time and 2 s the third, a 5-s
    // Prismatic Flash 2.5 s the second time; an 8-s one is refused the sixth time (800 ms).
    [Test]
    public void A_stun_or_mez_goes_while_the_servers_diminishing_immunity_still_allows_a_second_of_it()
    {
        Spell sheen = Control(4124, "Prismatic Sheen", "Stun", 36, 0), flash = Control(4122, "Prismatic Flash", "Stun", 15, 0);
        NpcStunImmunityEffect Immunity(Spell spell) =>
            new(new ECSGameEffectInitParams(null, 60_000, 1, new DOL.GS.Spells.StunSpellHandler(null, spell, SunLine)));
        NpcStunImmunityEffect afterSheen = Immunity(sheen), afterFlash = Immunity(flash);
        Assert.That((afterSheen.CalculateNewEffectDuration(8_000), afterFlash.CalculateNewEffectDuration(5_000)),
            Is.EqualTo((4_000L, 2_500L)), "the second time");
        Assert.That(PlayerBotDriver.DiminishedAllows(null, 8_000), Is.True, "none on it");
        Assert.That(PlayerBotDriver.DiminishedAllows(afterSheen, 8_000), Is.True, "the old rule: immune");
        afterSheen.OnApplyNewEffect();
        Assert.That(afterSheen.CalculateNewEffectDuration(8_000), Is.EqualTo(2_000L), "the third time");
        afterSheen.OnApplyNewEffect();
        afterSheen.OnApplyNewEffect();
        Assert.That(PlayerBotDriver.DiminishedAllows(afterSheen, 8_000), Is.True, "1 s, the fifth time");
        afterSheen.OnApplyNewEffect();
        Assert.That(PlayerBotDriver.DiminishedAllows(afterSheen, 8_000), Is.False, "800 ms, the sixth time");
        Assert.That(PlayerBotDriver.DiminishedAllows(Immunity(flash), 1_500), Is.False, "750 ms the second time");
    }

    // Takes as the controls read it (the review of 9aacd69: with the old "any immunity effect is immune" terms put back
    // in Takes no test failed): the server's own tests by kind. After one Prismatic Sheen the monster takes the next for
    // 4 s (the old rule: immune), not the sixth (800 ms); a CC-immune monster or one immune to the kind takes none; a mez
    // only from 75% health (CCSpellHandler.cs:173-177); a root reads no diminishing immunity.
    [Test]
    public void A_monster_takes_a_control_by_the_servers_own_tests_of_its_kind()
    {
        Spell sheen = Control(4124, "Prismatic Sheen", "Stun", 36, 0, duration: 8);
        var afterSheen = new NpcStunImmunityEffect(new ECSGameEffectInitParams(null, 60_000, 1,
            new DOL.GS.Spells.StunSpellHandler(null, sheen, SunLine)));
        Assert.That(PlayerBotDriver.Takes(sheen, false, false, afterSheen, 100), Is.True, "the second hold, 4 s");
        Assert.That(PlayerBotDriver.Takes(sheen, false, false, null, 100), Is.True);
        Assert.That((PlayerBotDriver.Takes(sheen, true, false, null, 100), PlayerBotDriver.Takes(sheen, false, true, null, 100)),
            Is.EqualTo((false, false)), "CC-immune, stun-immune");
        for (int i = 0; i < 4; i++) afterSheen.OnApplyNewEffect();
        Assert.That(PlayerBotDriver.Takes(sheen, false, false, afterSheen, 100), Is.False, "the sixth hold, 800 ms");
        Assert.That((PlayerBotDriver.Takes(LesserMesmerization, false, false, null, 75),
            PlayerBotDriver.Takes(LesserMesmerization, false, false, null, 74)), Is.EqualTo((true, false)));
        Assert.That(PlayerBotDriver.Takes(Cripple, false, false, afterSheen, 10), Is.True, "a root");
        Assert.That(PlayerBotDriver.Takes(Cripple, false, true, null, 100), Is.False, "root-immune");
    }

    // --- #167 E C4: the mez before a stun the step back outlasts ---

    // World-DB Mentalist controls: Prismatic Shimmer (Way of the Sun 26, a 6-s stun), Prismatic Sheen (36, 8 s), Lock Mind
    // (Mind Mastery 31, a 55-s mez).
    private static readonly Spell PrismaticShimmer = Control(4123, "Prismatic Shimmer", "Stun", 26, 0, duration: 6),
        PrismaticSheen = Control(4124, "Prismatic Sheen", "Stun", 36, 0, duration: 8),
        LockMind = Control(4676, "Lock Mind", "Mesmerize", 31, 0, castTime: 3.0, duration: 55);
    private static readonly SpellLine MindMastery = new("Mind Mastery", "Mind Mastery", Specs.Mentalism, false);

    // The server's diminished hold: 6 s, then 3 s, then 1.5 s within the minute.
    [Test]
    public void A_stuns_hold_now_is_what_the_servers_diminishing_leaves_of_it()
    {
        NpcStunImmunityEffect afterShimmer = new(new ECSGameEffectInitParams(null, 60_000, 1,
            new DOL.GS.Spells.StunSpellHandler(null, PrismaticShimmer, SunLine)));
        Assert.That(PlayerBotDriver.HoldNowMs(null, PrismaticShimmer.Duration), Is.EqualTo(6_000L));
        Assert.That(PlayerBotDriver.HoldNowMs(afterShimmer, PrismaticShimmer.Duration), Is.EqualTo(3_000L));
        afterShimmer.OnApplyNewEffect();
        Assert.That(PlayerBotDriver.HoldNowMs(afterShimmer, PrismaticShimmer.Duration), Is.EqualTo(1_500L));
    }

    // The step back's walk: from melee (150) to a 1,420 stop less KiteStopMargin, 1,220 units at 191, 6,387 ms; from
    // beyond that point, none.
    [TestCase(1_420, 150, 6_387L)] [TestCase(1_420, 1_400, 0L)]
    public void The_step_back_walks_to_its_stop_at_run_speed(int stop, int distance, long walkMs) =>
        Assert.That(PlayerBotDriver.StepBackWalkMs(stop, distance), Is.EqualTo(walkMs));

    // With hits on the bot from its target alone (soleHitter: CasterAction then steps back from a stunned target too) a
    // 6-s stun ends before a 6,387-ms walk and the margin (the mez first), an 8-s one doesn't; not hit, nothing to walk,
    // or an add's hits too (the review: then CasterHolds is false and no step back comes), the stun.
    [TestCase(true, true, 6_000L, 6_387L, true)] [TestCase(true, true, 8_000L, 6_387L, false)]
    [TestCase(false, false, 6_000L, 6_387L, false)] [TestCase(true, true, 6_000L, 0L, false)]
    [TestCase(true, false, 6_000L, 6_387L, false)]
    [TestCase(true, true, 6_500L, 6_387L, true)]                               // within the walk and the margin
    public void The_mez_goes_before_a_stun_the_step_back_outlasts(bool hitsOnBot, bool soleHitter, long stunHoldMs,
        long walkMs, bool mezFirst) =>
        Assert.That(PlayerBotDriver.MezBeforeStun(hitsOnBot, soleHitter, stunHoldMs, walkMs), Is.EqualTo(mezFirst));

    // v9: Pbothaadw's defences against arbordons, in melee and hit: Quickcast to Prismatic Shimmer, then a 6.4-s step
    // back the 6-s stun didn't outlast, 1-3 casts in 26-34 s. Now Lock Mind, and it walks out with the monster held; not
    // hit, the stun (it stands and nukes); hit by an add too, the stun (no step back comes); no mez it may cast (the
    // target under 75%, or its DoT on it), the stun; an 8-s Sheen outlasts the walk; a second Shimmer (3 s) doesn't.
    [Test]
    public void A_mentalist_hit_in_melee_mezzes_before_a_stun_its_step_back_outlasts()
    {
        (Spell, SpellLine)[] mentalist = [(PrismaticShimmer, SunLine), (LockMind, MindMastery)];
        long walk = PlayerBotDriver.StepBackWalkMs(1_420, 150);
        Spell Choose((Spell, SpellLine)[] controls, bool hit, bool sole, Func<Spell, long> hold, Func<Spell, bool> allowed) =>
            PlayerBotDriver.ChooseTargetControl(controls, _ => true, allowed, hold, hit, sole, walk).Item1;
        Assert.That(Choose(mentalist, true, true, s => s.Duration, _ => true), Is.SameAs(LockMind), "before: Prismatic Shimmer");
        Assert.That(Choose(mentalist, false, false, s => s.Duration, _ => true), Is.SameAs(PrismaticShimmer), "not hit");
        Assert.That(Choose(mentalist, true, false, s => s.Duration, _ => true), Is.SameAs(PrismaticShimmer),
            "an add hits it too: no step back comes");
        Assert.That(Choose(mentalist, true, true, s => s.Duration, s => s.SpellType is eSpellType.Stun),
            Is.SameAs(PrismaticShimmer), "no mez it may cast");
        Assert.That(Choose([(PrismaticSheen, SunLine), (LockMind, MindMastery)], true, true, s => s.Duration, _ => true),
            Is.SameAs(PrismaticSheen), "8 s outlasts the walk");
        Assert.That(Choose(mentalist, true, true, s => s.SpellType is eSpellType.Stun ? 3_000 : s.Duration, _ => true),
            Is.SameAs(LockMind), "a second Shimmer, 3 s");
        Assert.That(Choose([(PrismaticSheen, SunLine), (LockMind, MindMastery)], true, true,
                s => s.SpellType is eSpellType.Stun ? s.Duration / 2 : s.Duration, _ => true),
            Is.SameAs(LockMind), "a second Sheen, 4 s (its whole 8 s would keep the Sheen)");
        Assert.That(Choose([(PrismaticShimmer, SunLine)], true, true, s => s.Duration, _ => true), Is.SameAs(PrismaticShimmer),
            "no mez known");
    }

    // CasterStep's filter on its target (C8; the review of 9aacd69: with MezWanted dropped from it no test failed): one the
    // target takes, and a mez only as the escape.
    [TestCase(false, true, 5_000L, true)] [TestCase(true, true, 3_500L, true)] [TestCase(true, true, 3_501L, false)]
    [TestCase(false, false, 0L, false)] [TestCase(true, false, 0L, false)]
    public void A_caster_controls_its_target_with_what_it_takes_and_a_mez_only_as_the_escape(bool mez, bool takes,
        long contactMs, bool allowed) =>
        Assert.That(PlayerBotDriver.CasterControlAllowed(mez ? LesserMesmerization : Cripple, takes, contactMs),
            Is.EqualTo(allowed));

    // #167 C8: a mez goes on the target only as the escape, the monster in reach before the mez and its margin end (it
    // breaks on the next nuke's damage, GameLiving.cs:1784-1803); an add takes it whenever (C4).
    [TestCase(0L, 3_000, true)] [TestCase(3_500L, 3_000, true)] [TestCase(3_501L, 3_000, false)] [TestCase(5_000L, 3_000, false)]
    public void A_mez_on_the_target_is_only_the_escape(long contactMs, int mezCastMs, bool wanted) =>
        Assert.That(PlayerBotDriver.MezWanted(contactMs, mezCastMs), Is.EqualTo(wanted));

    // The time a monster needs to reach the bot: to ContactDistance (250) at its own speed, none at a speed of 0.
    [TestCase(1_250, 200, 5_000L)] [TestCase(250, 200, 0L)] [TestCase(100, 200, 0L)] [TestCase(1_000, 0, long.MaxValue)]
    public void A_monster_reaches_the_bot_at_its_own_speed(int distance, int speed, long contactMs) =>
        Assert.That(PlayerBotDriver.TimeToContact(distance, speed), Is.EqualTo(contactMs));

    // A caster's action against its target (a nuke of 3 s, a control of 2.5 s): it nukes while there is time for a nuke
    // and the control after it; then it controls the monster coming at it while the cast ends before contact; in melee
    // it quickcasts the control (the GameBot's rule: a caster under melee controls, BotBrain.cs:3874-3901). A held target
    // it nukes, unless the monster is near and the nuke would break a root or mez, or hits interrupt the bot (#167 C3):
    // then it steps back to range first. Nuke hands the think back to CombatAction (cast, wait, quickcast or swing).
    [TestCase(false, 2_000L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Nuke)]   // not coming at it
    [TestCase(true, 8_000L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Nuke)]    // time for a nuke
    [TestCase(true, 6_000L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Control)]
    [TestCase(true, 3_000L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Control)]
    [TestCase(true, 2_900L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Control)] // not yet hit
    [TestCase(true, 0L, false, false, 2_500, false, true, false, PlayerBotCasterAction.QcControl)]    // in melee
    [TestCase(true, 0L, false, false, 2_500, true, false, true, PlayerBotCasterAction.Control)]       // quickcast on
    [TestCase(true, 0L, false, false, 2_500, true, false, false, PlayerBotCasterAction.Nuke)]         // hit, no quickcast
    [TestCase(true, 6_000L, false, false, 2_500, true, true, false, PlayerBotCasterAction.QcControl)] // an add hits it (C4: the add step's first)
    [TestCase(true, 6_000L, false, false, 2_500, true, false, false, PlayerBotCasterAction.Nuke)]
    [TestCase(true, 0L, false, false, 0, false, true, false, PlayerBotCasterAction.Nuke)]             // no control ready
    [TestCase(true, 1_000L, true, true, 2_500, false, false, false, PlayerBotCasterAction.StepBack)]  // rooted, near
    [TestCase(true, 5_000L, true, true, 2_500, false, false, false, PlayerBotCasterAction.Nuke)]      // rooted, at range
    [TestCase(true, 0L, true, false, 2_500, false, false, false, PlayerBotCasterAction.Nuke)]         // stunned
    // #167 C3, the caster melee trap (81 of st-deathsB's 111 caster deaths): rooted in melee and hit, it nuked or swung,
    // and its own hit ended the root (GameLiving.cs:1762-1777); a stun in melee with hits on it, it steps back too.
    [TestCase(true, 0L, true, true, 0, true, false, false, PlayerBotCasterAction.StepBack)]           // rooted, hit
    [TestCase(true, 0L, true, false, 0, true, false, false, PlayerBotCasterAction.StepBack)]          // stunned, hit
    // Each threshold from both sides (the review of 5feb32b: no case told them apart). Before contact, Quickcast ready
    // and not hit: the control's cast while it ends before contact (more than 3,000 ms), else Quickcast's.
    [TestCase(true, 3_001L, false, false, 2_500, false, true, false, PlayerBotCasterAction.Control)]
    [TestCase(true, 3_000L, false, false, 2_500, false, true, false, PlayerBotCasterAction.QcControl)]
    // A held target the nuke frees: the step back while contact comes before the nuke's end and its margin (3,500).
    [TestCase(true, 3_499L, true, true, 2_500, false, false, false, PlayerBotCasterAction.StepBack)]
    [TestCase(true, 3_500L, true, true, 2_500, false, false, false, PlayerBotCasterAction.Nuke)]
    // The nuke first while there is time for it, the control and both margins (6,500).
    [TestCase(true, 6_500L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Control)]
    [TestCase(true, 6_501L, false, false, 2_500, false, false, false, PlayerBotCasterAction.Nuke)]
    public void A_caster_nukes_controls_the_monster_coming_at_it_and_steps_back_from_a_held_one(bool closing, long contactMs,
        bool held, bool holdBreaksOnHit, int controlCastMs, bool interrupted, bool quickcastReady, bool quickcastOn,
        PlayerBotCasterAction expected) =>
        Assert.That(PlayerBotDriver.CasterAction(closing, contactMs, held, holdBreaksOnHit, controlCastMs, 3_000, interrupted,
            quickcastReady, quickcastOn, killsFirst: false), Is.EqualTo(expected));

    // #167 C3, the caster melee trap: after a quickcast root in melee, Quickcast is down and the rooted monster's hits
    // kept interrupting the bot (AttackComponent.cs:1298), so Engage stopped it in melee (HoldsSpellRange false), the held
    // branch nuked, CombatAction swung, and the bot's own hit ended the root (a miss a mez, GameLiving.cs:1762-1777). Now
    // a caster's held target (CasterHolds) keeps the spell's stop, so the step back (83dcb96's gate, spellStop, goes:
    // Kites runs before the walk in) ends within it; and its swing, which would end a root or mez (SwingBreaksHold), waits
    // instead, power under 18% too: it stands off until the hold ends rather than break it.
    [Test]
    public void A_caster_in_melee_with_a_held_monster_steps_back_and_holds_its_spell_range()
    {
        long contact = PlayerBotDriver.TimeToContact(80, 200);
        Assert.That(PlayerBotDriver.CasterAction(true, contact, true, true, 0, 3_000, true, false, false, false),
            Is.EqualTo(PlayerBotCasterAction.StepBack), "rooted in melee, hit, no Quickcast ready: it nuked");
        Assert.That(PlayerBotDriver.CombatStopDistance(true, true, true, false, 200, 1_500, casterHold: true), Is.EqualTo(1_420));
        Assert.That(PlayerBotDriver.CombatStopDistance(true, true, true, false, 200, 1_500, casterHold: false),
            Is.EqualTo(PlayerBotDriver.MeleeStopDistance(200)), "no hold: melee, as before");
        Assert.That(PlayerBotDriver.CombatAction(true, true, 0, true, false, due: true, heldBreaksOnHit: true),
            Is.EqualTo(PlayerBotCombatAction.WaitToCast));
        Assert.That(PlayerBotDriver.CombatAction(true, true, 0, true, false, due: true, heldBreaksOnHit: false),
            Is.EqualTo(PlayerBotCombatAction.Swing));
        Assert.That(PlayerBotDriver.CombatAction(false, false, 0, true, false, due: true, heldBreaksOnHit: true),
            Is.EqualTo(PlayerBotCombatAction.WaitToCast), "nothing to cast (power under 18%): it waits, no swing");
        Assert.That(PlayerBotDriver.CombatAction(true, true, 0, true, true, due: true, heldBreaksOnHit: true),
            Is.EqualTo(PlayerBotCombatAction.Quickcast), "only a swing becomes a wait");
    }

    // The trap's rows where Engage takes them (the review of 9aacd69: with Engage's casterHold, swingBreaksHold, the
    // blocked step back or the guard switched off no test failed): one plan gives the action and the stop. A caster with
    // its target rooted in melee, hit and no Quickcast: a wait, and the spell's stop (1,420), not the swing that freed it;
    // its step back blocked: the swing, as before, the stop kept; an un-held add hitting it, or a melee class: melee's
    // (152); a stun: the swing (a hit doesn't end it) and the spell's stop.
    [TestCase(PlayerBotRole.Caster, true, true, false, false, PlayerBotCombatAction.WaitToCast, 1_420, true)]
    [TestCase(PlayerBotRole.Caster, true, true, false, true, PlayerBotCombatAction.Swing, 1_420, true)]      // step back blocked
    [TestCase(PlayerBotRole.Caster, true, true, true, false, PlayerBotCombatAction.Swing, 152, false)]      // an add hits it
    [TestCase(PlayerBotRole.Caster, true, false, false, false, PlayerBotCombatAction.Swing, 1_420, true)]   // a stun
    [TestCase(PlayerBotRole.Caster, false, false, false, false, PlayerBotCombatAction.Swing, 152, false)]   // not held
    [TestCase(PlayerBotRole.Melee, true, true, false, false, PlayerBotCombatAction.Swing, 152, false)]
    public void A_caster_rooted_in_melee_waits_at_its_spell_range_unless_its_step_back_is_blocked_or_an_add_hits_it(
        PlayerBotRole role, bool held, bool breaksOnHit, bool otherHitting, bool stepBackBlocked, PlayerBotCombatAction action,
        int stop, bool holds) =>
        Assert.That(PlayerBotDriver.CasterHoldPlan(role, held, breaksOnHit, otherHitting, stepBackBlocked, canCast: true,
            interrupted: true, selfInterruptMs: 0, interruptible: true, quickcastReady: false, due: true, attackRange: 200,
            spellRange: 1_500), Is.EqualTo((action, stop, holds)));

    // The review of 9aacd69: with no nuke chosen (power under the cheapest one's cost, or all recasting), Engage read the
    // stop from CalculateSpellRange(null), 32 (CastingComponent.cs:188-192): SpellStopDistance's 145. A caster with its
    // target rooted 1,000 units off walked in to 145, inside the monster's reach, "stepped back" 95 units from it (nearer
    // than it stood), and waited there taking hits until the root ended. The stop now reads its longest damage spell's
    // range; the step back goes only farther out than the bot stands; and with no damage spell at all it holds nothing.
    [Test]
    public void A_caster_with_no_nuke_chosen_holds_its_longest_nukes_range_off_a_rooted_monster()
    {
        (Spell, SpellLine)[] nukes = [(FungalOoze, null), (FungalThistle, null)];
        Assert.That(PlayerBotDriver.StopSpellRange(null, nukes, spell => spell.Range), Is.EqualTo(1_875), "the longest");
        Assert.That(PlayerBotDriver.StopSpellRange(FungalOoze, nukes, spell => spell.Range), Is.EqualTo(FungalOoze.Range));
        Assert.That(PlayerBotDriver.StopSpellRange(null, [], spell => spell.Range), Is.Zero);
        Assert.That(PlayerBotDriver.StopSpellRange(null, null, spell => spell.Range), Is.Zero);
        Assert.That(PlayerBotDriver.CasterHoldPlan(PlayerBotRole.Caster, true, true, false, false, canCast: false,
            interrupted: false, selfInterruptMs: 0, interruptible: false, quickcastReady: false, due: true, attackRange: 200,
            spellRange: 1_875), Is.EqualTo((PlayerBotCombatAction.WaitToCast, 1_795, true)), "it stands off, no swing");
        Assert.That(PlayerBotDriver.CasterHoldPlan(PlayerBotRole.Caster, true, true, false, false, canCast: false,
            interrupted: false, selfInterruptMs: 0, interruptible: false, quickcastReady: false, due: true, attackRange: 200,
            spellRange: 0), Is.EqualTo((PlayerBotCombatAction.Swing, 152, false)), "no damage spell: no hold to keep");
        Assert.That(PlayerBotDriver.KiteRange(1_420, 100), Is.EqualTo(1_370));
        Assert.That((PlayerBotDriver.KiteRange(145, 145), PlayerBotDriver.KiteRange(1_420, 1_370)),
            Is.EqualTo(((int?)null, (int?)null)), "never toward the target");
    }

    // Only a caster, only while its target is held, and only while no other monster hits it that is not held (the guard:
    // then the add's control, C4, or a break-off, C2): the step back would not shake an add, and a wait with an add's
    // hits on it would stand the bot idle.
    [TestCase(PlayerBotRole.Caster, true, false, true)]
    [TestCase(PlayerBotRole.Caster, true, true, false)]                                     // an add hits it, not held
    [TestCase(PlayerBotRole.Caster, false, false, false)]                                   // not held
    [TestCase(PlayerBotRole.Healer, true, false, false)]                                    // no caster: as before
    [TestCase(PlayerBotRole.Melee, true, false, false)]
    public void Only_a_caster_alone_with_its_held_target_keeps_the_hold(PlayerBotRole role, bool held, bool otherHitting,
        bool holds) =>
        Assert.That(PlayerBotDriver.CasterHolds(role, held, otherHitting), Is.EqualTo(holds));

    // The verifier's correction: a blocked step back pauses KitePauseMilliseconds (Kites), and the wait would stand the bot
    // idle in the rooted monster's reach that long: then the old rule, a swing (or the cast). A stun, which a hit doesn't
    // end (GameLiving.cs:1784-1803), takes the swing as before.
    [TestCase(true, true, false, PlayerBotCombatAction.WaitToCast)]
    [TestCase(true, true, true, PlayerBotCombatAction.Swing)]                               // its step back blocked
    [TestCase(true, false, false, PlayerBotCombatAction.Swing)]                             // a stun
    [TestCase(false, true, false, PlayerBotCombatAction.Swing)]                             // no caster's hold
    public void A_caster_with_its_step_back_blocked_swings_as_before(bool casterHold, bool breaksOnHit, bool stepBackBlocked,
        PlayerBotCombatAction action) =>
        Assert.That(PlayerBotDriver.CombatAction(true, true, 0, true, false, due: true,
            PlayerBotDriver.SwingBreaksHold(casterHold, breaksOnHit, stepBackBlocked)), Is.EqualTo(action));

    // The step back's point keeps the bot within its stop: Engage's distance (GetDistanceTo) is 3D, KiteStep's range flat
    // at the bot's height, and the navmesh may move the point up or down. With a stop of 1,420 and the bot arriving
    // within 20 of it: a point 1,370 out on the flat fits; 1,370 out and 400 up (a slope of 16 degrees) is 1,427 away,
    // past the stop, where Engage walked it back in and it cast nothing for the whole root.
    [TestCase(1_370f, 0f, true)] [TestCase(1_400f, 0f, true)] [TestCase(1_401f, 0f, false)] [TestCase(1_370f, 400f, false)]
    public void A_step_backs_point_lies_within_the_stop_in_3d(float x, float z, bool fits) =>
        Assert.That(PlayerBotDriver.KiteFits(new Vector3(x, 0, z), Vector3.Zero, 1_420), Is.EqualTo(fits));

    // The step back: a point at range from the target, straight away from it, else turned 30, 60 or 90 degrees either
    // way, the first out of every potential add's reach (the adds rule's ring) so the step pulls nothing; none, null.
    [Test]
    public void A_caster_steps_back_away_from_its_target_clear_of_the_adds()
    {
        var none = new List<(string, Vector3, int)>();
        Assert.That(PlayerBotDriver.KiteStep(new Vector3(300, 0, 0), Vector3.Zero, 1_420, none), Is.EqualTo(new Vector3(1_420, 0, 0)));
        Assert.That(PlayerBotDriver.KiteStep(Vector3.Zero, Vector3.Zero, 1_420, none), Is.EqualTo(new Vector3(1_420, 0, 0)),
            "on top of it: along +X");
        var behind = new List<(string, Vector3, int)> { ("add", new(1_400, 0, 0), 300) };
        Vector3? turned = PlayerBotDriver.KiteStep(new Vector3(300, 0, 0), Vector3.Zero, 1_420, behind);
        Assert.That(turned, Is.Not.Null);
        Assert.That(Vector3.Distance(turned!.Value, new Vector3(1_229.76f, 710f, 0)), Is.LessThan(0.1f), $"{turned}");
        var round = new List<(string, Vector3, int)> { ("add", Vector3.Zero, 1_500) };
        Assert.That(PlayerBotDriver.KiteStep(new Vector3(300, 0, 0), Vector3.Zero, 1_420, round), Is.Null);
    }

    // #167 C3 (inf.): with no point clear of the adds, a bot in melee steps back anyway, straight away to 400 units from
    // its target: a null left it in the held monster's reach. Out of melee, none, as before. A clear point is Clear: its
    // place on the navmesh is checked against the adds again, the fallback's is not. #167 D8: every clear point in turn
    // (KiteCandidates), and in melee the fallback last: KitePoint took only KiteStep's first clear point, and the fallback
    // only with none clear.
    [Test]
    public void A_caster_in_melee_with_no_clear_point_steps_back_straight_away_anyway()
    {
        var round = new List<(string, Vector3, int)> { ("add", Vector3.Zero, 1_500) };
        Assert.That(PlayerBotDriver.KiteCandidates(new Vector3(100, 0, 0), Vector3.Zero, 1_370, round, inMelee: true),
            Is.EqualTo(new[] { (new Vector3(400, 0, 0), false) }));
        Assert.That(PlayerBotDriver.KiteCandidates(new Vector3(600, 0, 0), Vector3.Zero, 1_370, round, inMelee: false), Is.Empty);
    }

    [Test]
    public void The_step_back_tries_every_clear_point_then_in_melee_the_straight_away_one()
    {
        var none = new List<(string, Vector3, int)>();
        List<(Vector3 Point, bool Clear)> melee = PlayerBotDriver.KiteCandidates(new Vector3(300, 0, 0), Vector3.Zero, 1_370, none,
            inMelee: true);
        Assert.That(melee, Has.Count.EqualTo(8));
        Assert.That((melee[0], melee[^1]), Is.EqualTo(((new Vector3(1_370, 0, 0), true), (new Vector3(400, 0, 0), false))));
        Assert.That(melee.Take(7).All(c => c.Clear), "the turned points are clear");
        Assert.That(PlayerBotDriver.KiteCandidates(new Vector3(300, 0, 0), Vector3.Zero, 1_370, none, inMelee: false),
            Has.Count.EqualTo(7));
        var behind = new List<(string, Vector3, int)> { ("add", new(1_370, 0, 0), 300) };
        List<(Vector3 Point, bool Clear)> turned = PlayerBotDriver.KiteCandidates(new Vector3(300, 0, 0), Vector3.Zero, 1_370,
            behind, inMelee: false);
        Assert.That(Vector3.Distance(turned[0].Point, new Vector3(1_186.4f, 685f, 0)), Is.LessThan(0.1f), $"{turned[0]}");
    }

    // #167 D8: a point the height rules out (KiteFits leaves 30 units of 3D slack, so about 287 units above or below the
    // target fails at 1,370) is pulled in once to the flat range that fits at that height (KiteRangeFor), still farther
    // out than the bot stands; none past the reach (stop less KiteStopMargin).
    [TestCase(1_420, 0, 145, 1_370)] [TestCase(1_420, 300, 145, 1_337)] [TestCase(1_420, 400, 145, 1_311)]
    [TestCase(1_420, -400, 145, 1_311)] [TestCase(1_420, 600, 1_300, null)] [TestCase(1_420, 1_400, 145, null)]
    public void A_step_back_point_too_high_or_low_pulls_in_to_fit(int stop, int dz, int distance, int? range) =>
        Assert.That(PlayerBotDriver.KiteRangeFor(stop, dz, distance), Is.EqualTo(range));

    [Test]
    public void The_pulled_in_point_fits() =>
        Assert.That(PlayerBotDriver.KiteFits(new Vector3(1_311, 0, 400), Vector3.Zero, 1_420), Is.True);

    // #167 D8: KitePoint's walk (KiteWalk): each candidate on the navmesh, a clear one's place there against the adds
    // again, then KiteFits, a miss pulled in once (KiteRangeFor); at most KiteMeshCalls navmesh calls a think. None:
    // why, the nearest miss (fit, then clear, then mesh; no candidate: clear), for FIGHT and DEATH kiteNone=.
    private static readonly List<(string, Vector3, int)> NoAdds = new();

    [Test]
    public void The_step_back_takes_the_first_candidate_that_fits_on_the_mesh()
    {
        var candidates = new List<(Vector3, bool)> { (new(1_370, 0, 0), true), (new(1_186.4f, 685, 0), true) };
        Assert.That(PlayerBotDriver.KiteWalk(candidates, Vector3.Zero, 1_420, 145, p => p, NoAdds),
            Is.EqualTo(((Vector3?)new Vector3(1_370, 0, 0), (string)null)));
        Assert.That(PlayerBotDriver.KiteWalk(candidates, Vector3.Zero, 1_420, 145, p => p.Y == 0 ? null : p, NoAdds),
            Is.EqualTo(((Vector3?)new Vector3(1_186.4f, 685, 0), (string)null)), "off the mesh: the next");
    }

    [Test]
    public void A_step_back_point_the_height_rules_out_is_pulled_in_once()
    {
        var candidates = new List<(Vector3, bool)> { (new(1_370, 0, 0), true) };
        (Vector3? point, string why) = PlayerBotDriver.KiteWalk(candidates, Vector3.Zero, 1_420, 145,
            p => new Vector3(p.X, p.Y, 400), NoAdds);
        Assert.That((point, why), Is.EqualTo(((Vector3?)new Vector3(1_311, 0, 400), (string)null)));
        // Straight away the mesh lies 400 up at 1,370 and 600 up nearer in: its pull-in (1,311) misses too, and it is not
        // pulled in again; the next point (30 degrees) misses at 600 up and its own pull-in (1,232) fits.
        var two = new List<(Vector3, bool)> { (new(1_370, 0, 0), true), (new(1_186.4f, 685, 0), true) };
        int calls = 0;
        (point, why) = PlayerBotDriver.KiteWalk(two, Vector3.Zero, 1_420, 145, p =>
        {
            calls++;
            return new Vector3(p.X, p.Y, Math.Abs(p.X - 1_370) < 0.5f && Math.Abs(p.Y) < 0.5f ? 400 : 600);
        }, NoAdds);
        Assert.That(point, Is.Not.Null);
        Assert.That(Vector3.Distance(point!.Value, new Vector3(1_066.9f, 616, 600)), Is.LessThan(1f), $"{point}");
        Assert.That((why, calls), Is.EqualTo(((string)null, 4)));
    }

    [Test]
    public void The_step_back_asks_the_mesh_at_most_8_times_a_think()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => (new Vector3(1_370, i * 10, 0), true)).ToList();
        int calls = 0;
        Assert.That(PlayerBotDriver.KiteWalk(candidates, Vector3.Zero, 1_420, 145, p => { calls++; return null; }, NoAdds),
            Is.EqualTo(((Vector3?)null, "mesh")));
        Assert.That(calls, Is.EqualTo(PlayerBotDriver.KiteMeshCalls).And.EqualTo(8));
    }

    [Test]
    public void A_step_back_that_finds_none_says_its_nearest_miss()
    {
        var clear = new List<(Vector3, bool)> { (new(1_370, 0, 0), true) };
        var near = new List<(string, Vector3, int)> { ("add", new(1_370, 300, 0), 300) };
        Assert.That(PlayerBotDriver.KiteWalk(clear, Vector3.Zero, 1_420, 145, p => p + new Vector3(0, 100, 0), near),
            Is.EqualTo(((Vector3?)null, "clear")), "the mesh moved it into an add's reach");
        Assert.That(PlayerBotDriver.KiteWalk(new List<(Vector3, bool)> { (new(400, 0, 0), false) }, Vector3.Zero, 1_420, 145,
            p => p + new Vector3(0, 100, 0), near), Is.EqualTo(((Vector3?)new Vector3(400, 100, 0), (string)null)),
            "the fallback isn't checked against the adds");
        Assert.That(PlayerBotDriver.KiteWalk(clear, Vector3.Zero, 1_420, 145, p => new Vector3(p.X, p.Y, 1_400), NoAdds),
            Is.EqualTo(((Vector3?)null, "fit")), "no flat range fits 1,400 up");
        var both = new List<(Vector3, bool)> { (new(1_370, 0, 0), true), (new(1_186.4f, 685, 0), true) };
        Assert.That(PlayerBotDriver.KiteWalk(both, Vector3.Zero, 1_420, 145,
                p => p.Y == 0 ? new Vector3(p.X, p.Y, 1_400) : null, NoAdds),
            Is.EqualTo(((Vector3?)null, "fit")), "the nearest miss: one on the mesh that didn't fit");
        Assert.That(PlayerBotDriver.KiteWalk(new List<(Vector3, bool)>(), Vector3.Zero, 1_420, 145, p => p, NoAdds),
            Is.EqualTo(((Vector3?)null, "clear")), "no candidate: none clear");
    }

    // FIGHT and DEATH kiteNone=range:clear:mesh:fit: each step back that found no point, by why (range: KiteRange had
    // none, the bot already at its reach).
    [Test]
    public void A_step_back_with_no_point_counts_why()
    {
        var counts = (Range: 0, Clear: 0, Mesh: 0, Fit: 0);
        foreach (string why in new[] { "range", "clear", "mesh", "mesh", "fit", "fit", "fit" })
            counts = PlayerBotDriver.KiteNoneCounted(counts, why);
        Assert.That(counts, Is.EqualTo((1, 1, 2, 3)));
    }

    // --- no control when the nukes kill first (#167 C11) ---

    // The nukes the target still needs, from what the fight's casts took off it so far: ceil(health / ((start - health)
    // / casts)); nothing measured (no cast, or no health off it yet), int.MaxValue.
    [TestCase(100, 65, 1, 2)] [TestCase(100, 70, 1, 3)] [TestCase(100, 30, 2, 1)] [TestCase(100, 0, 3, 0)]
    [TestCase(100, 100, 1, int.MaxValue)] [TestCase(100, 100, 0, int.MaxValue)]
    public void The_nukes_a_target_still_needs_come_from_what_the_fights_casts_took(int startHealth, int health, int nukesCast,
        int needed) =>
        Assert.That(PlayerBotDriver.NukesNeeded(startHealth, health, nukesCast), Is.EqualTo(needed));

    // Whether those nukes land before the monster reaches the bot: one per nuke's cast and 250 ms, and one more with
    // Quickcast ready (the killing nuke through the hits).
    [TestCase(2, 6_100L, 3_000, true, true)] [TestCase(2, 6_100L, 3_000, false, false)]
    [TestCase(1, 3_300L, 3_000, false, true)] [TestCase(1, 0L, 3_000, true, true)]
    [TestCase(int.MaxValue, 60_000L, 3_000, true, false)]
    public void The_nukes_kill_first_while_they_land_before_contact(int needed, long contactMs, int nukeCastMs,
        bool quickcastReady, bool killsFirst) =>
        Assert.That(PlayerBotDriver.KillsFirst(needed, contactMs, nukeCastMs, quickcastReady), Is.EqualTo(killsFirst));

    // A GREEN pull with 3 nukes took 9.5 s in st-deathsA and 16.8 s in st-deathsB: the control went whenever contact was
    // under a nuke, the control and 1 s (6.5 s), every pull from 1,420 units. Now the nuke while the nukes kill first;
    // in melee, hit, with Quickcast ready, the nuke too (CombatAction quickcasts it).
    [Test]
    public void A_caster_nukes_instead_of_controlling_when_the_nukes_kill_first()
    {
        Assert.That(PlayerBotDriver.CasterAction(true, 6_000, false, false, 2_500, 3_000, false, true, false, killsFirst: true),
            Is.EqualTo(PlayerBotCasterAction.Nuke));
        Assert.That(PlayerBotDriver.CasterAction(true, 6_000, false, false, 2_500, 3_000, false, true, false, killsFirst: false),
            Is.EqualTo(PlayerBotCasterAction.Control));
        Assert.That(PlayerBotDriver.CasterAction(true, 0, false, false, 2_500, 3_000, true, true, false, killsFirst: true),
            Is.EqualTo(PlayerBotCasterAction.Nuke));
        Assert.That(PlayerBotDriver.CombatAction(true, true, 0, true, true, due: true, heldBreaksOnHit: false),
            Is.EqualTo(PlayerBotCombatAction.Quickcast), "the killing nuke quickcast");
        Assert.That(PlayerBotDriver.CasterAction(true, 1_000, true, true, 2_500, 3_000, false, false, false, killsFirst: true),
            Is.EqualTo(PlayerBotCasterAction.StepBack), "a held one: as before");
    }

    // CasterStep's killsFirst as it reads it (the review of 9aacd69: forced false there, no test failed): the nukes the
    // fight's casts say the target needs land before contact, the bot at 60% health or more and one monster at most on it.
    // #167 E C2a: the Quickcast already on counts as the ready one did (once on, QuickcastReady is false and the killing
    // nuke lost it to the control). #167 E C2b: at any health when one nuke kills (oneNukeKills: the chosen nuke's hit
    // at the variance minimum, less the resist, at least the target's health less its own DoTs' ticks due; E2's 13 died
    // with the target on 2-15%), not NukesNeeded's 1, which a bolt or a landed DoT over-reads.
    [TestCase(100, 65, 1, 6_100L, 3_000, true, false, false, (byte)100, 1, true)]
    [TestCase(100, 65, 1, 6_100L, 3_000, false, false, false, (byte)100, 1, false)]          // no Quickcast: 1 lands
    [TestCase(100, 65, 1, 6_100L, 3_000, true, false, false, (byte)59, 1, false)]            // under 60%
    [TestCase(100, 65, 1, 6_100L, 3_000, true, false, false, (byte)60, 1, true)]
    [TestCase(100, 65, 1, 6_100L, 3_000, true, false, false, (byte)100, 2, false)]           // an add on it
    [TestCase(100, 100, 0, 60_000L, 3_000, true, false, false, (byte)100, 0, false)]         // nothing measured
    [TestCase(100, 65, 1, 6_100L, 3_000, false, true, false, (byte)100, 1, true)]            // E C2a: Quickcast on
    [TestCase(100, 8, 5, 0L, 2_600, false, true, false, (byte)100, 1, true)]                 // E C2a: on, in melee
    [TestCase(100, 8, 5, 0L, 2_600, false, false, false, (byte)100, 1, false)]               // in melee, none: no nuke lands
    [TestCase(100, 8, 5, 0L, 2_600, true, false, true, (byte)30, 1, true)]                   // E C2b: one kills, at 30%
    [TestCase(100, 8, 5, 0L, 2_600, false, true, true, (byte)30, 1, true)]                   // E C2b: and Quickcast on
    [TestCase(100, 8, 5, 0L, 2_600, true, false, false, (byte)30, 1, false)]                 // E C2b: its hit falls short
    [TestCase(100, 8, 5, 0L, 2_600, true, false, true, (byte)30, 2, false)]                  // E C2b: an add on it
    public void A_caster_skips_its_control_when_its_nukes_kill_first_healthy_and_alone(int engageHealth, int health,
        int casts, long contactMs, int nukeCastMs, bool quickcastReady, bool quickcastOn, bool oneNukeKills, byte botHealth,
        int attackers, bool killsFirst) =>
        Assert.That(PlayerBotDriver.KillsFirstNow(engageHealth, health, casts, contactMs, nukeCastMs, quickcastReady,
            quickcastOn, oneNukeKills, botHealth, attackers), Is.EqualTo(killsFirst));

    // #167 E C2a, Correction 3: the killing nuke's two thinks. In melee and hit, Quickcast ready: the nuke, and
    // CombatAction turns Quickcast on. Next think, Quickcast on (not ready, not interrupted under it): still the nuke, not
    // the control the Quickcast used to go to.
    [Test]
    public void The_killing_nuke_keeps_the_quickcast_it_turned_on()
    {
        bool first = PlayerBotDriver.KillsFirstNow(100, 8, 5, 0L, 2_600, true, false, false, (byte)100, 1);
        Assert.That(PlayerBotDriver.CasterAction(true, 0, false, false, 2_500, 2_600, true, true, false, first),
            Is.EqualTo(PlayerBotCasterAction.Nuke));
        Assert.That(PlayerBotDriver.CombatAction(true, true, 0, true, true, due: true, heldBreaksOnHit: false),
            Is.EqualTo(PlayerBotCombatAction.Quickcast));
        bool ready = PlayerBotDriver.QuickcastReady(true, quickcastOn: true, 0, 0, false, 1_000);
        bool second = PlayerBotDriver.KillsFirstNow(100, 8, 5, 0L, 2_600, ready, quickcastOn: true, false, (byte)100, 1);
        Assert.That(PlayerBotDriver.CasterAction(true, 0, false, false, 2_500, 2_600, false, ready, true, second),
            Is.EqualTo(PlayerBotCasterAction.Nuke), "before: Control, the Quickcast to the control");
    }

    // #167 E C2b: a caster at 30% in melee, its target on 8%: the nuke's hit at the variance minimum kills it, so the
    // killing nuke goes first (and keeps its Quickcast) where the 60% gate gave the Quickcast to the control.
    [Test]
    public void The_killing_nuke_goes_first_at_any_health_when_one_nuke_kills()
    {
        bool Kills(bool oneNukeKills) => PlayerBotDriver.KillsFirstNow(100, 8, 5, 0L, 2_600, true, false, oneNukeKills,
            (byte)30, 1);
        Assert.That(PlayerBotDriver.CasterAction(true, 0, false, false, 2_500, 2_600, true, true, false, Kills(true)),
            Is.EqualTo(PlayerBotCasterAction.Nuke));
        Assert.That(PlayerBotDriver.CasterAction(true, 0, false, false, 2_500, 2_600, true, true, false, Kills(false)),
            Is.EqualTo(PlayerBotCasterAction.QcControl), "its hit falls short: the control, as before");
    }

    // #167 E C2b: one nuke kills when its hit at the variance minimum, less the resist, is at least the target's health
    // less its own DoTs' ticks still due. Aurora Ray at Light 3 on a level-41 monster: 148 x 0.3104, 45.9.
    [TestCase(40, 0.0, 0, true)] [TestCase(50, 0.0, 0, false)] [TestCase(50, 12.0, 0, true)] [TestCase(40, 0.0, 20, false)]
    [TestCase(45, 0.0, 0, true)]
    public void One_nuke_kills_by_its_lowest_hit_less_the_resist(int health, double pendingDot, int resist, bool kills) =>
        Assert.That(PlayerBotDriver.OneNukeKills(PlayerBotDriver.ExpectedHit(148, 0.3104, 0.3104, resist, false, 0), health,
            pendingDot), Is.EqualTo(kills));

    // #167 E C2b (the review of the batch: risk-1): the cast choice and whether one nuke kills, judged on the nuke the
    // bot will cast. A dry Mentalist alone (solo): Aurora Ray, the nuke it has any power for (as the server lets it
    // under Quickcast), and Fungal Ooze, the one it can pay for. Under Quickcast (on or due) a killing any-power nuke
    // goes; else the paid one, killing or not. Before, the any-power nuke was judged at every cast choice while the bot
    // cast the paid one: the Quickcast and the control's reserve went to a nuke that didn't kill. passes: Nuke's.
    [TestCase(true, true, true, false, "Aurora Ray", true, 1)]             // any power kills under Quickcast: it goes
    [TestCase(true, true, false, true, "Fungal Ooze", true, 2)]            //   it doesn't: the paid one, which kills
    [TestCase(true, true, false, false, "Fungal Ooze", false, 2)]
    [TestCase(true, false, true, false, "Fungal Ooze", false, 1)]          // no Quickcast on or due (today: kills)
    [TestCase(true, false, false, true, "Fungal Ooze", true, 1)]           //   the paid one kills (today: no)
    [TestCase(false, true, true, true, "Fungal Ooze", false, 1)]           // not alone, or no caster's fight
    public void One_nuke_kills_is_judged_on_the_nuke_the_bot_casts(bool solo, bool quickcastOnOrDue, bool anyKills,
        bool paidKills, string cast, bool oneNukeKills, int passes)
    {
        int nukes = 0;
        (Spell spell, SpellLine line, bool kills) = PlayerBotDriver.KillingNukeChoice(solo, quickcastOnOrDue,
            () => { nukes++; return (AuroraRay, Holism); }, () => { nukes++; return (FungalOoze, Subterranean); },
            s => s == AuroraRay ? anyKills : paidKills);
        Assert.That((spell.Name, line, kills, nukes), Is.EqualTo((cast, spell == AuroraRay ? Holism : Subterranean,
            oneNukeKills, passes)));
    }

    [Test]
    public void With_no_nuke_it_has_power_for_the_paid_choice_is_judged()
    {
        (Spell spell, _, bool kills) = PlayerBotDriver.KillingNukeChoice(true, true, () => default,
            () => (FungalOoze, Subterranean), s => s == FungalOoze);
        Assert.That((spell, kills), Is.EqualTo((FungalOoze, true)));
        Assert.That(PlayerBotDriver.KillingNukeChoice(true, false, () => default, () => default, _ => true),
            Is.EqualTo(((Spell)null, (SpellLine)null, false)), "no spell: none kills");
    }

    // #167 E C2b: the killing quickcast: a caster in a fight whose chosen nuke kills at once (oneNukeKills), one monster at
    // most on it (the review), and the cast under Quickcast (on, or ready with hits interrupting it: due).
    [TestCase(true, true, true, false, 1, true)] [TestCase(true, true, false, true, 1, true)]
    [TestCase(true, false, true, false, 1, false)] [TestCase(false, true, true, false, 1, false)]
    [TestCase(true, true, false, false, 1, false)] [TestCase(true, true, true, false, 2, false)]
    [TestCase(true, true, true, false, 0, true)]
    public void The_killing_quickcast_is_one_killing_nuke_under_quickcast(bool caster, bool oneNukeKills, bool on, bool due,
        int attackers, bool killing) =>
        Assert.That(PlayerBotDriver.KillingQuickcast(caster, oneNukeKills, on, due, attackers), Is.EqualTo(killing));

    // #167 D11: a GREEN gets a control only as the escape. 143 of st-deathsC's 347 GREEN wins had a control, 22.1 s and 40.9
    // power each against 7.9 s and 19.2 without one, and 106 of the 143 spent Quickcast. A control on the target is worth
    // it on a BLUE or harder, with the bot under KillFirstHealthPercent, or with 2 or more live monsters on it (the
    // verifier's correction: TargetFirst reads the same gate).
    [TestCase(ConColor.GREEN, (byte)100, 1, false)] [TestCase(ConColor.GREEN, (byte)60, 1, false)]
    [TestCase(ConColor.GREEN, (byte)59, 1, true)] [TestCase(ConColor.BLUE, (byte)100, 1, true)]
    [TestCase(ConColor.GREY, (byte)100, 1, false)] [TestCase(ConColor.GREEN, (byte)100, 2, true)]
    [TestCase(ConColor.YELLOW, (byte)100, 0, true)] [TestCase(ConColor.GREEN, (byte)100, 0, false)]
    public void A_green_gets_a_control_only_as_the_escape(ConColor con, byte botHealth, int attackers, bool worth) =>
        Assert.That(PlayerBotDriver.ControlWorth(con, botHealth, attackers), Is.EqualTo(worth));

    // #167 D11: a control not worth it reaches CasterAction as none (CasterStep passes no cast time): it nukes. And the
    // add's control isn't held back for it (TargetFirst reads the same gate): a GREEN coming at a healthy bot first, its
    // add after, the add's control goes; with 2 on the bot the target's comes first, as before.
    [Test]
    public void A_control_not_worth_it_leaves_the_nuke_and_the_adds_control()
    {
        Assert.That(PlayerBotDriver.CasterAction(true, 2_000, false, false, 0, 3_000, false, true, false, false),
            Is.EqualTo(PlayerBotCasterAction.Nuke));
        bool First(int attackers) => PlayerBotDriver.TargetFirst(
            PlayerBotDriver.ControlWorth(ConColor.GREEN, 100, attackers) && true, false, true, 1_000, 3_000);
        Assert.That(PlayerBotDriver.AddAction(true, true, 3_000, First(1), 1_500, true, false, false, false),
            Is.EqualTo(PlayerBotAddAction.Control), "a GREEN alone: the add's control");
        Assert.That(PlayerBotDriver.AddAction(true, true, 3_000, First(2), 1_500, true, false, false, false),
            Is.EqualTo(PlayerBotAddAction.None), "2 on it: the target's first");
    }

    // --- the add's control (#167 C4) ---

    // Caster add fights were fatal 96% of the time in st-deathsB (51 of 53): casters controlled their target, never an add
    // (CasterStep's state.Target only). The add: the nearest monster with the bot as its target, not held and not the
    // target (the merged seam: caster-pulls' attacker within 250 kept as its cases).
    [Test]
    public void The_add_to_control_is_the_nearest_unheld_monster_on_the_bot_but_the_target()
    {
        Assert.That(PlayerBotDriver.ChooseAdd([("target", 100, true, false), ("a", 600, true, false), ("b", 300, true, true),
            ("c", 200, false, false)], "target"), Is.EqualTo("a"), "b is held, c isn't on the bot");
        Assert.That(PlayerBotDriver.ChooseAdd([("A", 1_400, true, false), ("B", 120, true, false)], "A"), Is.EqualTo("B"));
        Assert.That(PlayerBotDriver.ChooseAdd([("A", 1_400, true, false), ("B", 120, true, false), ("C", 90, true, false)], "A"),
            Is.EqualTo("C"));
        Assert.That(PlayerBotDriver.ChooseAdd([("A", 100, true, false), ("B", 120, true, true)], "A"), Is.Null);
        Assert.That(PlayerBotDriver.ChooseAdd([("A", 100, true, false)], "A"), Is.Null, "only the target");
    }

    // For an add the order is a mez, then a root, then a stun (the GameBot mezzes adds, BotBrain.cs:3887-3893); a pet class
    // skips the mez: its pet's defence hits the add and ends it (ControlledMobBrain.cs:930-970, inf.). The target's order
    // stays a stun, a root, a mez.
    [Test]
    public void An_add_takes_a_mez_first_but_from_a_pet_class()
    {
        (Spell, SpellLine)[] sorcerer = [(LesserCrippling, BodyLine), (Cripple, BodyLine), (MinorMesmerization, BodyLine),
            (LesserMesmerization, BodyLine)];
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, _ => true, forAdd: true, petClass: false).Item1,
            Is.SameAs(LesserMesmerization));
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, _ => true, forAdd: false, petClass: false).Item1,
            Is.SameAs(Cripple), "the target's order");
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, s => s.SpellType != eSpellType.Mesmerize, forAdd: true,
            petClass: false).Item1, Is.SameAs(Cripple), "the add under 75%: no mez (CCSpellHandler.cs:173-177)");
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => true, _ => true, forAdd: true, petClass: true).Item1,
            Is.SameAs(Cripple), "a pet class");
        (Spell, SpellLine)[] enchanter = [(Control(4124, "Prismatic Sheen", "Stun", 36, 0), SunLine)];
        Assert.That(PlayerBotDriver.ChooseControl(enchanter, _ => true, _ => true, forAdd: true, petClass: true).Item1?.Name,
            Is.EqualTo("Prismatic Sheen"));
        Assert.That(PlayerBotDriver.ChooseControl(sorcerer, _ => false, _ => true, forAdd: true, petClass: false).Item1, Is.Null,
            "none ready");
    }

    // A root keeps an add off only while it is still away: in melee a rooted monster still hits (inf.). A mez or a stun
    // stops its hits there too.
    [Test]
    public void A_root_on_an_add_already_in_melee_is_no_control()
    {
        Assert.That(PlayerBotDriver.ControlKeepsAddOff(Cripple, 251), Is.True);
        Assert.That(PlayerBotDriver.ControlKeepsAddOff(Cripple, 250), Is.False);
        Assert.That(PlayerBotDriver.ControlKeepsAddOff(LesserMesmerization, 100), Is.True);
        Assert.That(PlayerBotDriver.ControlKeepsAddOff(PrismaticFlare, 0), Is.True);
    }

    // The add's control (a 2,500-ms cast, the 500-ms margin), for every role with a control: cast while it ends before
    // the add's contact and no hit interrupts the bot; else only with Quickcast (Quickcast ready: turn it on; on: cast).
    // Not while the target's control comes first (TargetFirst), nor with no add or no control. The review of 9aacd69: an
    // instant control (a cast time of 0: a Skald's Stunning Shout and Disabling Shout, a Minstrel's Stunning Yell) read
    // as none; it goes whatever the hits and the contact, as CombatAction casts an uninterruptible spell.
    [TestCase(false, true, 4_000L, false, 2_500, true, false, false, false, PlayerBotAddAction.None)]
    [TestCase(true, true, 4_000L, false, 2_500, true, false, false, false, PlayerBotAddAction.Control)]
    [TestCase(true, true, 3_001L, false, 2_500, true, false, true, false, PlayerBotAddAction.Control)]
    [TestCase(true, true, 3_000L, false, 2_500, true, false, true, false, PlayerBotAddAction.QcControl)]
    [TestCase(true, true, 3_000L, false, 2_500, true, false, false, false, PlayerBotAddAction.None)]          // no Quickcast
    [TestCase(true, true, 0L, false, 2_500, true, true, true, false, PlayerBotAddAction.QcControl)]
    [TestCase(true, true, 0L, false, 2_500, true, true, false, true, PlayerBotAddAction.Control)]            // Quickcast on
    [TestCase(true, true, 0L, false, 2_500, true, true, false, false, PlayerBotAddAction.None)]             // a healer once hit
    [TestCase(true, true, 6_000L, true, 2_500, true, false, false, false, PlayerBotAddAction.None)]          // the target's first
    [TestCase(true, false, 6_000L, false, 0, true, false, false, false, PlayerBotAddAction.None)]            // no control
    [TestCase(true, true, 6_000L, false, 2_500, true, true, false, false, PlayerBotAddAction.None)]          // the target's hits
    [TestCase(true, true, 6_000L, false, 2_500, true, true, true, false, PlayerBotAddAction.QcControl)]
    [TestCase(true, true, 0L, false, 0, false, true, false, false, PlayerBotAddAction.Control)]              // instant, in melee, hit
    [TestCase(true, true, 6_000L, false, 0, false, false, false, false, PlayerBotAddAction.Control)]         // instant
    [TestCase(true, true, 0L, true, 0, false, true, false, false, PlayerBotAddAction.None)]                  // the target's first
    public void A_bot_controls_the_add_before_it_arrives_else_with_quickcast(bool hasAdd, bool hasControl, long addContactMs,
        bool targetFirst, int controlCastMs, bool interruptible, bool interrupted, bool quickcastReady, bool quickcastOn,
        PlayerBotAddAction expected) =>
        Assert.That(PlayerBotDriver.AddAction(hasAdd, hasControl, addContactMs, targetFirst, controlCastMs, interruptible,
            interrupted, quickcastReady, quickcastOn), Is.EqualTo(expected));

    // One Quickcast in 30 s (SpellHandler.cs:1430-1434): an add hitting the bot (contact 0) while its target comes at 6 s
    // took it on the target (CasterAction's "an add hits it" row); the add step runs first now, turns it on for the add,
    // and casts the add's control with it at the next think, where CasterStep sees it as not ready.
    [Test]
    public void Quickcast_goes_to_the_add_first()
    {
        Assert.That(PlayerBotDriver.CasterAction(true, 6_000, false, false, 2_500, 3_000, true, true, false, false),
            Is.EqualTo(PlayerBotCasterAction.QcControl), "the target's, after the add step");
        Assert.That(PlayerBotDriver.AddAction(true, true, 0, PlayerBotDriver.TargetFirst(true, false, true, 6_000, 0), 2_500,
            true, true, true, false), Is.EqualTo(PlayerBotAddAction.QcControl), "the add's first");
        Assert.That(PlayerBotDriver.AddAction(true, true, 0, false, 2_500, true, true, false, true),
            Is.EqualTo(PlayerBotAddAction.Control), "the next think, Quickcast on");
        Assert.That(PlayerBotDriver.QuickcastReady(true, true, 0, 0, false, 5_000), Is.False, "CasterStep's view: on already");
    }

    // A caster's own control on its target comes first while the target, un-held and coming at it, reaches it before the
    // add (CasterStep controls it then); else the add's. Both in melee: the add's, as Quickcast's order has it.
    [TestCase(true, false, true, 1_000L, 3_000L, true)]
    [TestCase(true, false, true, 3_000L, 1_000L, false)]
    [TestCase(true, false, true, 0L, 0L, false)]
    [TestCase(true, true, true, 1_000L, 3_000L, false)]                                     // the target held
    [TestCase(false, false, true, 1_000L, 3_000L, false)]                                   // no control for it
    [TestCase(true, false, false, 1_000L, 3_000L, false)]                                   // not coming at it
    public void The_targets_control_comes_first_while_it_arrives_first(bool controlsTarget, bool targetHeld, bool closing,
        long targetContactMs, long addContactMs, bool first) =>
        Assert.That(PlayerBotDriver.TargetFirst(controlsTarget, targetHeld, closing, targetContactMs, addContactMs),
            Is.EqualTo(first));

    // The review of 9aacd69: TargetFirst counted any control the target takes, not one CasterStep would cast now. The
    // target's only control a mez not yet wanted (contact 5,000 against its 3,000-ms cast: MezWanted) gave the add's step
    // None, and CasterStep nuked: the add reached melee uncontrolled. Now it is CasterStep's own choice (its filter,
    // CasterControlAllowed), so with none the add's mez goes (6,000 ms off).
    [Test]
    public void The_target_comes_first_only_with_a_control_casterstep_would_cast_now()
    {
        (Spell, SpellLine)[] onlyMez = [(LesserMesmerization, BodyLine)];
        Spell forTarget = PlayerBotDriver.ChooseControl(onlyMez, _ => true,
            spell => PlayerBotDriver.CasterControlAllowed(spell, takes: true, contactMs: 5_000), false, false).Item1;
        Assert.That(forTarget, Is.Null, "a mez not yet wanted");
        bool targetFirst = PlayerBotDriver.TargetFirst(forTarget != null, false, true, 5_000, 6_000);
        Assert.That(PlayerBotDriver.AddAction(true, true, 6_000, targetFirst, 3_000, true, false, false, false),
            Is.EqualTo(PlayerBotAddAction.Control));
        Spell escape = PlayerBotDriver.ChooseControl(onlyMez, _ => true,
            spell => PlayerBotDriver.CasterControlAllowed(spell, takes: true, contactMs: 3_000), false, false).Item1;
        Assert.That(PlayerBotDriver.TargetFirst(escape != null, false, true, 3_000, 6_000), Is.True, "the escape: the target's");
    }

    // The verifier's correction: CheckBeginCast and CheckEndCast need the spell's target in the front 180 degrees
    // (SpellHandler.cs:725, :939), and Engage faced the target every think before its IsCasting return. While a cast at
    // the add is under way the bot faces the add.
    [Test]
    public void A_bot_casting_at_the_add_faces_the_add()
    {
        Assert.That(PlayerBotDriver.FacesWhom("target", "add", casting: true), Is.EqualTo("add"));
        Assert.That(PlayerBotDriver.FacesWhom("target", "add", casting: false), Is.EqualTo("target"));
        Assert.That(PlayerBotDriver.FacesWhom("target", null, casting: true), Is.EqualTo("target"));
    }

    // Whom Engage faces, from the add a cast was made at (the review of 9aacd69: with Engage's facing set back to the target
    // no test failed): the add while the cast at it is under way and it lives; once it died, or the cast ended, the target.
    [TestCase("add", true, true, "add")] [TestCase("add", true, false, "target")] [TestCase("add", false, true, "target")]
    [TestCase(null, true, true, "target")]
    public void Engage_faces_the_add_while_its_cast_is_under_way_and_it_lives(string addCast, bool casting, bool addAlive,
        string faced) =>
        Assert.That(PlayerBotDriver.EngageFacing("target", addCast, casting, addAlive), Is.EqualTo(faced));

    // The order of Engage's steps (the review of 9aacd69: with the add's step switched off no test failed): a cast under
    // way, nothing; at a due cast choice the add's step first when it acts (its control, Quickcast for it, or a
    // Theurgist's pets at it), then the pet leads, then CasterStep and CombatAction.
    [TestCase(true, true, PlayerBotAddAction.Control, true, PlayerBotEngageStep.Wait)]
    [TestCase(true, false, PlayerBotAddAction.Control, true, PlayerBotEngageStep.Add)]
    [TestCase(true, false, PlayerBotAddAction.QcControl, false, PlayerBotEngageStep.Add)]
    [TestCase(true, false, PlayerBotAddAction.Pets, false, PlayerBotEngageStep.Add)]
    [TestCase(false, false, PlayerBotAddAction.Control, true, PlayerBotEngageStep.PetLeads)]
    [TestCase(true, false, PlayerBotAddAction.None, true, PlayerBotEngageStep.PetLeads)]
    [TestCase(true, false, PlayerBotAddAction.None, false, PlayerBotEngageStep.Caster)]
    [TestCase(false, false, PlayerBotAddAction.Control, false, PlayerBotEngageStep.Caster)]
    public void The_adds_control_comes_before_the_pet_leads_and_the_casters_step(bool due, bool casting,
        PlayerBotAddAction addAction, bool petLeads, PlayerBotEngageStep step) =>
        Assert.That(PlayerBotDriver.EngageStepOrder(due, casting, addAction, petLeads), Is.EqualTo(step));

    // A mezzed or stunned monster keeps the bot as its target (CrowdControlECSEffect.cs:26-37 stops its attack only), but
    // it hits nothing: it is not on the bot for the break-off (C2), so a controlled add doesn't start one.
    [TestCase(true, false, true)] [TestCase(true, true, false)] [TestCase(false, false, false)]
    public void A_mezzed_or_stunned_add_is_not_on_the_bot(bool attacksBot, bool crowdControlled, bool onBot) =>
        Assert.That(PlayerBotDriver.OnBot(attacksBot, crowdControlled), Is.EqualTo(onBot));

    // --- the nuke per cast (#167 F9) ---

    // World-DB spells (Spell, LineXSpell): a Shaman's Subterranean bolts and its direct damage, a Wizard's Pyromancy.
    private static Spell Nuke(int id, string name, string type, int level, double damage, int range, int radius = 0) =>
        new(new DbSpell { SpellID = id, Name = name, Type = type, Target = "Enemy", Damage = damage, Range = range,
            Radius = radius, CastTime = 3.0 }, level);

    private static readonly Spell FungalPin = Nuke(3461, "Fungal Pin", "Bolt", 1, 7, 1875),
        FungalThistle = Nuke(3466, "Fungal Thistle", "Bolt", 20, 114, 1875),
        FungalOoze = Nuke(3490, "Fungal Ooze", "DirectDamage", 20, 73, 1500),
        FlameStreak = Nuke(374, "Flame Streak", "Bolt", 19, 133, 1875),
        LesserConflagration = Nuke(357, "Lesser Conflagration", "DirectDamage", 23, 108, 1500),
        ConcussiveBlast = Nuke(366, "Concussive Blast", "DirectDamage", 24, 87, 1500, radius: 350);

    private static readonly SpellLine Subterranean = new("Subterranean Spec", "Subterranean", Specs.Subterranean, true),
        Pyromancy = new("Pyromancy", "Pyromancy", Specs.Fire_Magic, true);

    private static string NukeName(IEnumerable<Spell> spells, Func<Spell, bool> ready, bool addInRadius = false) =>
        PlayerBotDriver.ChooseNuke(spells.Select(spell => (spell, Subterranean)), ready, addInRadius, null).Item1?.Name ?? "-";

    [Test]
    public void A_bot_casts_its_bolt_when_it_is_ready_else_its_best_ready_nuke()
    {
        // Fungal Thistle and Fungal Ooze share no timer, each a 20-s recast: the Ooze while the Thistle waits (the old
        // choice kept the Thistle, the better spell, and the cast failed)
        Spell[] shaman = [FungalThistle, FungalOoze];
        Assert.That(NukeName(shaman, spell => spell != FungalThistle), Is.EqualTo("Fungal Ooze"));
        Assert.That(NukeName(shaman, _ => true), Is.EqualTo("Fungal Thistle"));
        Assert.That(NukeName(shaman, _ => false), Is.EqualTo("-"));                  // none ready: it swings
        // a ready bolt before a higher, single-target spell (Better's choice): the Wizard's Flame Streak at 19
        Spell[] wizard = [LesserConflagration, FlameStreak];
        Assert.That(NukeName(wizard, _ => true), Is.EqualTo("Flame Streak"));
        Assert.That(NukeName(wizard, spell => spell != FlameStreak), Is.EqualTo("Lesser Conflagration"));
    }

    [Test]
    public void An_area_nuke_comes_after_the_single_target_ones_and_never_with_an_add_in_its_radius()
    {
        Spell[] wizard = [ConcussiveBlast, LesserConflagration];                       // Pyromancy at 24
        Assert.That(NukeName(wizard, _ => true), Is.EqualTo("Lesser Conflagration"));   // not the higher area one
        Assert.That(NukeName(wizard, spell => spell == ConcussiveBlast), Is.EqualTo("Concussive Blast"));
        Assert.That(NukeName(wizard, spell => spell == ConcussiveBlast, addInRadius: true), Is.EqualTo("-"));
        Assert.That(NukeName(wizard, _ => true, addInRadius: true), Is.EqualTo("Lesser Conflagration"));
    }

    [Test]
    public void A_bots_damage_spells_leave_out_the_ranks_a_gamebot_outgrew()        // BotBrain.cs:3413
    {
        List<(Spell, SpellLine)> usable = [(FungalPin, Subterranean), (FungalThistle, Subterranean),
            (FungalOoze, Subterranean), (Damage("DirectDamage", 30, 90), Subterranean)];
        Assert.That(PlayerBotDriver.DamageSpells(usable, 20, _ => null, perLine: false).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Fungal Thistle", "Fungal Ooze" }));                    // Pin: the Thistle outranks it; 30: above
        Assert.That(PlayerBotDriver.DamageSpells(usable, 19, _ => null, perLine: false).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Fungal Pin" }));
    }

    // --- #167 E C1: the nuke by the server's expected damage per cast-second ---

    // World-DB spells (Spell, LineXSpell).
    private static Spell DbNuke(int id, string name, string type, int level, double damage, double castTime, int duration = 0,
        int frequency = 0) =>
        new(new DbSpell { SpellID = id, Name = name, Type = type, Target = "Enemy", Damage = damage, Range = 1500,
            CastTime = castTime, Duration = duration, Frequency = frequency }, level);

    private static readonly Spell AuroraRay = DbNuke(4110, "Aurora Ray", "DirectDamage", 41, 148, 2.6),
        MindAnnihilation = DbNuke(4430, "Mind Annihilation", "DamageOverTime", 41, 73, 3.0, duration: 24, frequency: 40),
        SignetOfDestruction = DbNuke(2918, "Signet of Destruction", "DirectDamage", 38, 140, 3.0),
        MuninsClaw = DbNuke(2577, "Munin's Claw", "DirectDamageWithDebuff", 38, 139, 3.0, duration: 60),
        GreaterConflagration = DbNuke(359, "Greater Conflagration", "DirectDamage", 37, 173, 2.8),
        IceBlastPotent = DbNuke(160, "Ice Blast (Potent)", "DirectDamage", 41, 148, 2.6),
        LesserConstrictingForce = DbNuke(635, "Lesser Constricting Force", "DamageSpeedDecrease", 31, 112, 3.0, duration: 30),
        ConstrictingForce = DbNuke(636, "Constricting Force", "DamageSpeedDecrease", 40, 143, 3.0, duration: 30),
        IncreasedEssenceConsumption = DbNuke(661, "Increased Essence Consumption", "Lifedrain", 35, 128, 2.5);

    private static readonly SpellLine Holism = new("Holism", "Holism", Specs.Mana, false),
        Runecarving = new("Runecarving", "Runecarving", Specs.Runecarving, true),
        RunesOfDarkness = new("Runes of Darkness", "Runes of Darkness", Specs.Darkness, false),
        PathOfIce = new("Path of Ice", "Path of Ice", Specs.Cold_Magic, false),
        Telekinesis = new("Telekinesis", "Telekinesis", Specs.Matter_Magic, false);

    // E1: a Mentalist at 43 (Mana 43, Light 3) against a level-41 monster (resist 0). Aurora Ray at Light 3: the server's
    // lower bound 0.248 (CalculateLowerVarianceBound(3, 41): 94 steps) plus the level offset 0.0624 (2 levels x 3.12%), a
    // mean of 0.686, 101.6 a hit; Mind Annihilation at Mana 43: 1.0 (a DoT has no offset), 73 a tick, 6 ticks (24 s at
    // 4 s, the first at once). Both land 89.5% (87.5 + 2 levels). Per cast-second 0.0319 against 0.1206; with the kill 2
    // ticks away 0.0402, 1 tick away 0.0201: then the direct hit. Better chose Aurora Ray every time.
    [Test]
    public void A_casters_nuke_is_its_most_expected_damage_per_cast_second()
    {
        double auroraRay = PlayerBotDriver.NukeValue(PlayerBotDriver.ExpectedHit(148, 0.248 + 0.0624, 1 + 0.0624, 0, false, 0),
            89.5, 1, AuroraRay.CastTime);
        double Annihilation(double horizonMs) => PlayerBotDriver.NukeValue(PlayerBotDriver.ExpectedHit(73, 1, 1, 0, false, 0),
            89.5, PlayerBotDriver.RealizedTicks(PlayerBotDriver.DotTicks(MindAnnihilation.Duration, MindAnnihilation.Frequency),
                MindAnnihilation.Frequency, horizonMs), MindAnnihilation.CastTime);
        (Spell, SpellLine)[] mentalist = [(AuroraRay, SunLine), (MindAnnihilation, Holism)];
        string Choose(Func<Spell, bool> ready, double horizonMs) => PlayerBotDriver.ChooseNuke(mentalist, ready, false,
            spell => spell == AuroraRay ? auroraRay : Annihilation(horizonMs)).Item1?.Name ?? "-";
        Assert.That(auroraRay, Is.EqualTo(0.0319).Within(0.0001));
        Assert.That(Annihilation(double.PositiveInfinity), Is.EqualTo(0.1206).Within(0.0001));
        Assert.That(Choose(_ => true, double.PositiveInfinity), Is.EqualTo("Mind Annihilation"), "Better: Aurora Ray");
        Assert.That(Choose(spell => spell != MindAnnihilation, double.PositiveInfinity), Is.EqualTo("Aurora Ray"),
            "its DoT already on the target");
        Assert.That(Choose(_ => true, 4_702), Is.EqualTo("Mind Annihilation"), "2 ticks before the kill");
        Assert.That(Choose(_ => true, 1_881), Is.EqualTo("Aurora Ray"), "1 tick: the direct hit kills sooner");
        Assert.That(PlayerBotDriver.ChooseNuke(mentalist, _ => true, false, null).Item1, Is.SameAs(AuroraRay),
            "no score (every other role): Better");
    }

    // The skeptic's row: a Runemaster at 40 (Darkness 41, Runecarving 1) against a level-38 monster. Signet of Destruction
    // at Runecarving 1: 0.208 + 0.072 to 1.072, 94.6 a hit, lands 89.5%: 0.0261. Munin's Claw at Darkness 41: 1.072 (the
    // bound clamps at 1, plus the offset), 149.0 a hit, lands 87.0% (dual-component: -2.5): 0.0399. Better took the
    // Signet (the same level, 1 more damage).
    [Test]
    public void A_specced_rank_beats_an_unspecced_one_of_the_same_level()
    {
        double signet = PlayerBotDriver.NukeValue(PlayerBotDriver.ExpectedHit(140, 0.208 + 0.072, 1.072, 0, false, 0), 89.5, 1,
            SignetOfDestruction.CastTime);
        double claw = PlayerBotDriver.NukeValue(PlayerBotDriver.ExpectedHit(139, 1.072, 1.072, 0, false, 0), 87.0, 1,
            MuninsClaw.CastTime);
        (Spell, SpellLine)[] runemaster = [(SignetOfDestruction, Runecarving), (MuninsClaw, RunesOfDarkness)];
        Assert.That(signet, Is.EqualTo(0.0261).Within(0.0001));
        Assert.That(claw, Is.EqualTo(0.0399).Within(0.0001));
        Assert.That(PlayerBotDriver.ChooseNuke(runemaster, _ => true, false, spell => spell == MuninsClaw ? claw : signet).Item1,
            Is.SameAs(MuninsClaw));
        Assert.That(PlayerBotDriver.ChooseNuke(runemaster, _ => true, false, null).Item1, Is.SameAs(SignetOfDestruction),
            "Better, as before");
    }

    // The expected hit's resist and a bolt's armour: a 100 base at full variance, 20% resisted, 80; a bolt resists half,
    // the other half x 2.5 L over the target's armour (0.5 here): 100 x (0.4 + 0.25) = 65. The area ones stay last, by
    // Better, whatever the score.
    [Test]
    public void The_expected_hit_takes_the_resist_and_a_bolts_armour_and_the_area_ones_stay_last()
    {
        Assert.That(PlayerBotDriver.ExpectedHit(100, 1, 1, 20, false, 0), Is.EqualTo(80).Within(1e-9));
        Assert.That(PlayerBotDriver.ExpectedHit(100, 1, 1, 20, true, 0.5), Is.EqualTo(65).Within(1e-9));
        Assert.That(PlayerBotDriver.ExpectedHit(100, 0.5, 1, 0, false, 0), Is.EqualTo(75).Within(1e-9));
        Assert.That(PlayerBotDriver.CastCycleMs(2_600), Is.EqualTo(2_850));
        Assert.That(PlayerBotDriver.CastCycleMs(0), Is.EqualTo(900), "an instant: the next choice's 900 ms");
        (Spell, SpellLine)[] wizard = [(ConcussiveBlast, Pyromancy), (LesserConflagration, Pyromancy)];
        Assert.That(PlayerBotDriver.ChooseNuke(wizard, _ => true, false, spell => spell == ConcussiveBlast ? 1 : 0.01).Item1,
            Is.SameAs(LesserConflagration));
        Assert.That(PlayerBotDriver.ChooseNuke(wizard, spell => spell == ConcussiveBlast, false, _ => 1).Item1,
            Is.SameAs(ConcussiveBlast));
    }

    // #167 E C1: under a score a bolt ranks with the other single-target nukes by its value (the design: "Bolts are scored
    // the same"; Light Eldritch cast an unspecced Void bolt first); with no score (every other role) it goes first, as
    // before. A hit chance over 100 (87.5 + half the level gap + the level difference, about 102.5 on a GREEN) counts
    // as 100: the server lands no more than every cast.
    [Test]
    public void A_bolt_is_scored_with_the_other_single_target_nukes()
    {
        (Spell, SpellLine)[] shaman = [(FungalThistle, Subterranean), (FungalOoze, Subterranean)];
        Assert.That(PlayerBotDriver.ChooseNuke(shaman, _ => true, false, spell => spell == FungalThistle ? 0.01 : 0.05).Item1,
            Is.SameAs(FungalOoze), "the bolt worth less");
        Assert.That(PlayerBotDriver.ChooseNuke(shaman, _ => true, false, null).Item1, Is.SameAs(FungalThistle), "no score");
        Assert.That(PlayerBotDriver.NukeValue(100, 110, 1, 3_000), Is.EqualTo(PlayerBotDriver.NukeValue(100, 100, 1, 3_000)));
    }

    // #167 E C1: OutclassedInLine keeps BotBrain's half-level rule across lines: a nuke under half the level of the best
    // damage spell the caster has in any line, and 5 levels or more under it, is outclassed (a caster at 40 drops its
    // level-15 nuke of another line; a dry caster cast such low nukes when its best weren't paid for).
    [TestCase(15, true)] [TestCase(19, true)] [TestCase(20, false)]
    public void A_casters_nuke_under_half_its_best_in_any_line_is_outclassed(int level, bool outclassed)
    {
        Spell low = DbNuke(9_015, "Low Nuke", "DirectDamage", level, 40, 2.5),
            best = DbNuke(9_040, "Best Nuke", "DirectDamage", 40, 140, 2.6);
        Assert.That(PlayerBotDriver.OutclassedInLine(low, Pyromancy, [(low, Pyromancy), (best, PathOfIce)], 40),
            Is.EqualTo(outclassed));
    }

    // Pbotaaaaf (Fire 41): Ice Blast (Potent) 41 at Cold 6 outclassed Greater Conflagration 37 (BotBrain.cs:3413, any
    // line's higher rank of the kind). A caster keeps both and its score chooses; a higher rank in its own line still
    // outclasses a lower one.
    [Test]
    public void A_casters_damage_spells_drop_a_rank_only_for_a_higher_one_of_its_own_line()
    {
        List<(Spell, SpellLine)> usable = [(GreaterConflagration, Pyromancy), (IceBlastPotent, PathOfIce)];
        Assert.That(PlayerBotDriver.DamageSpells(usable, 41, _ => null, perLine: false).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Ice Blast (Potent)" }), "the GameBot's rule");
        Assert.That(PlayerBotDriver.DamageSpells(usable, 41, _ => null, perLine: true).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Greater Conflagration", "Ice Blast (Potent)" }));
        List<(Spell, SpellLine)> pyromancy = [(LesserConflagration, Pyromancy), (GreaterConflagration, Pyromancy)];
        Assert.That(PlayerBotDriver.DamageSpells(pyromancy, 41, _ => null, perLine: true).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Greater Conflagration" }), "its own line's higher rank");
    }

    // The server's DoT: a tick at the start, then every frequency while before the expiry (read first): Mind Annihilation
    // 6, Illusory Excrutiation 4 (not 7 and 5). The ticks the fight sees, and those an earlier one still owes.
    [TestCase(24_000, 4_000, 6)] [TestCase(20_000, 5_000, 4)] [TestCase(24_001, 4_000, 7)] [TestCase(0, 0, 1)]
    public void A_dot_ticks_at_once_then_every_frequency_before_its_expiry(int durationMs, int frequencyMs, int ticks) =>
        Assert.That(PlayerBotDriver.DotTicks(durationMs, frequencyMs), Is.EqualTo(ticks));

    [Test]
    public void A_dot_counts_the_ticks_before_the_kill_and_the_ticks_still_due()
    {
        Assert.That(PlayerBotDriver.RealizedTicks(6, 4_000, double.PositiveInfinity), Is.EqualTo(6));
        Assert.That(PlayerBotDriver.RealizedTicks(6, 4_000, 7_999), Is.EqualTo(2));
        Assert.That(PlayerBotDriver.RealizedTicks(6, 4_000, 8_000), Is.EqualTo(3));
        Assert.That(PlayerBotDriver.RealizedTicks(6, 4_000, 0), Is.EqualTo(1), "the first lands at once");
        Assert.That(PlayerBotDriver.KillHorizonMs(1_000, 400, 0.1), Is.EqualTo(6_000).Within(1e-6));
        Assert.That(PlayerBotDriver.KillHorizonMs(300, 400, 0.1), Is.EqualTo(0), "its DoTs already kill it");
        Assert.That(PlayerBotDriver.KillHorizonMs(1_000, 0, 0), Is.EqualTo(double.PositiveInfinity), "no direct nuke");
        Assert.That(PlayerBotDriver.DotTicksLeft(24_000, 4_000, 4_000), Is.EqualTo(5));
        Assert.That(PlayerBotDriver.DotTicksLeft(3_000, 2_000, 4_000), Is.EqualTo(1));
        Assert.That(PlayerBotDriver.DotTicksLeft(2_000, 2_000, 4_000), Is.EqualTo(0), "the expiry first");
    }

    // A DoT's ticks end a root and a mez, not a stun (GameLiving.cs:1749-1830): a stun caster casts one wherever, a root
    // caster only where no control is worth it; and no root or mez goes on while its own DoT ticks.
    [TestCase(true, true, true)] [TestCase(false, true, false)] [TestCase(false, false, true)] [TestCase(true, false, true)]
    public void A_dot_fits_a_stun_caster_or_a_fight_with_no_control_wanted(bool hasStun, bool controlWorth, bool fits) =>
        Assert.That(PlayerBotDriver.DotFits(hasStun, controlWorth), Is.EqualTo(fits));

    [Test]
    public void No_root_or_mez_goes_on_while_the_bots_own_dot_ticks()
    {
        Assert.That(PlayerBotDriver.HoldSurvivesDot(PrismaticFlare, ownDotOn: true), Is.True, "a stun");
        Assert.That(PlayerBotDriver.HoldSurvivesDot(Cripple, ownDotOn: true), Is.False, "a root");
        Assert.That(PlayerBotDriver.HoldSurvivesDot(LesserMesmerization, ownDotOn: true), Is.False, "a mez");
        Assert.That(PlayerBotDriver.HoldSurvivesDot(Cripple, ownDotOn: false), Is.True);
    }

    // A Matter Sorcerer at 37-39: Better took Increased Essence Consumption 35 over Lesser Constricting Force 31, and its
    // root stayed castable (79 of 79 YELLOW pulls won, s5). The score would move it onto the snare nuke, whose slow no root
    // replaces (RootTakes): not while it has a root (the review: at any con, so no root caster is newly moved onto one).
    // At 40+ Better took Constricting Force: unchanged.
    [Test]
    public void A_root_caster_isnt_moved_onto_a_snare_nuke_it_didnt_take_before()
    {
        Assert.That(PlayerBotDriver.KeepsRootCastable(ConstrictingForce, IncreasedEssenceConsumption, rootWanted: true), Is.False);
        Assert.That(PlayerBotDriver.KeepsRootCastable(ConstrictingForce, ConstrictingForce, rootWanted: true), Is.True,
            "the old order chose it");
        Assert.That(PlayerBotDriver.KeepsRootCastable(ConstrictingForce, IncreasedEssenceConsumption, rootWanted: false), Is.True,
            "no root");
        Assert.That(PlayerBotDriver.KeepsRootCastable(IncreasedEssenceConsumption, ConstrictingForce, rootWanted: true), Is.True);
    }

    // The review's seams for Nuke's composition (a wiring slip there failed no test): the ready filter and the scored
    // choice with KeepsRootCastable applied. Ready: off its recast and paid for; a scored DoT only where it fits and none
    // it conflicts with is on the target; unscored (every other role, the pull's stop), as before. #167 E C2b: under the
    // killing quickcast a nuke it has any power for (the server checks only power left under Quickcast).
    [TestCase(true, true, false, 100, false, true, false, true, true)]      // unscored: as before
    [TestCase(true, true, false, 100, true, false, false, true, true)]      // a direct hit: no DoT test
    [TestCase(true, true, false, 100, true, true, true, false, true)]
    [TestCase(true, true, false, 100, true, true, false, false, false)]     // a root caster's control wanted
    [TestCase(true, true, false, 100, true, true, true, true, false)]       // its own or a conflicting DoT on it
    [TestCase(false, true, false, 100, true, false, true, false, false)]    // on its recast
    [TestCase(true, false, false, 100, true, false, true, false, false)]    // not paid for
    [TestCase(true, false, true, 7, true, false, true, false, true)]        // E C2b: the killing quickcast, 7 power left
    [TestCase(true, false, true, 0, true, false, true, false, false)]       //   but none
    [TestCase(false, false, true, 7, true, false, true, false, false)]       //   on its recast
    public void A_nuke_is_ready_off_its_recast_paid_for_and_a_dot_only_where_it_fits(bool offRecast, bool paid,
        bool killingQuickcast, int mana, bool scored, bool dot, bool dotFits, bool dotConflict, bool ready) =>
        Assert.That(PlayerBotDriver.NukeReady(offRecast, paid, killingQuickcast, mana, scored, dot, dotFits, dotConflict),
            Is.EqualTo(ready));

    [Test]
    public void A_scored_nuke_keeps_a_root_casters_root_castable()
    {
        (Spell, SpellLine)[] sorcerer = [(IncreasedEssenceConsumption, BodyLine), (LesserConstrictingForce, Telekinesis)];
        double Value(Spell spell) => spell == LesserConstrictingForce ? 0.05 : 0.04;
        Assert.That(PlayerBotDriver.ScoredNuke(sorcerer, _ => true, false, Value, rootWanted: true).Item1,
            Is.SameAs(IncreasedEssenceConsumption), "the score's snare nuke, not taken before");
        Assert.That(PlayerBotDriver.ScoredNuke(sorcerer, _ => true, false, Value, rootWanted: false).Item1,
            Is.SameAs(LesserConstrictingForce), "no root: the score");
        Assert.That(PlayerBotDriver.ScoredNuke(sorcerer, spell => spell != IncreasedEssenceConsumption, false, Value,
            rootWanted: true).Item1, Is.SameAs(LesserConstrictingForce), "the old order's only one");
        (Spell, SpellLine)[] at40 = [(IncreasedEssenceConsumption, BodyLine), (ConstrictingForce, Telekinesis)];
        Assert.That(PlayerBotDriver.ScoredNuke(at40, _ => true, false, spell => spell == ConstrictingForce ? 0.05 : 0.04,
            rootWanted: true).Item1, Is.SameAs(ConstrictingForce), "at 40+ Better took it: unchanged");
        Assert.That(PlayerBotDriver.ScoredNuke(sorcerer, _ => true, false, null, rootWanted: true).Item1,
            Is.SameAs(IncreasedEssenceConsumption), "no score: Better");
    }

    // --- Animists (#167 F10b) ---

    // World-DB spells: an Animist's Creeping Path bombers and their payloads (a bomber casts only its SubSpellID at the
    // target, BomberBrain.cs:32-34), and an Arboreal Path lifedrain.
    private static Spell Bomber(int id, string name, int level, int payload) =>
        new(new DbSpell { SpellID = id, Name = name, Type = "Bomber", Target = "Enemy", Damage = 50, Range = 1500,
            CastTime = 3.0, SubSpellID = payload }, level);

    private static readonly SpellLine CreepingPath = new("Creeping Path", "Creeping Path", Specs.Creeping_Path, true);
    private static readonly Spell WispSpark = Bomber(11319, "Wisp Spark", 7, 11343),
        SpreadingVines = Bomber(11334, "Spreading Vines", 8, 11361),
        SurvivalOfTheFittest = Nuke(11014, "Survival of the Fittest", "Lifedrain", 8, 33, 1500);
    private static readonly Dictionary<int, Spell> BomberPayloads = new()
    {
        [11343] = Nuke(11343, "Wisp Spark", "DirectDamageWithDebuff", 7, 29, 1500),
        [11361] = new(new DbSpell { SpellID = 11361, Name = "Spreading Vines", Type = "SpeedDecrease", Target = "Enemy",
            Value = 99, Range = 1500, Duration = 10 }, 8),
    };

    // A bomber is a nuke only when its payload does damage: Spreading Vines (Damage 50 on the bomber, a root as its
    // payload) was the level-9 Animist's best nuke, over its lifedrain, and outranked its damage bomber.
    [Test]
    public void An_animists_bomber_is_a_nuke_only_when_its_payload_does_damage()
    {
        List<(Spell, SpellLine)> usable = [(WispSpark, CreepingPath), (SpreadingVines, CreepingPath),
            (SurvivalOfTheFittest, CreepingPath)];
        List<(Spell Spell, SpellLine Line)> nukes = PlayerBotDriver.DamageSpells(usable, 9, id => BomberPayloads.GetValueOrDefault(id),
            perLine: false);
        Assert.That(nukes.Select(pair => pair.Spell.Name), Is.EqualTo(new[] { "Wisp Spark", "Survival of the Fittest" }));
        Assert.That(PlayerBotDriver.ChooseNuke(nukes, _ => true, false, null).Item1, Is.SameAs(SurvivalOfTheFittest));
        Assert.That(PlayerBotDriver.DamageSpells(usable, 9, _ => null, perLine: false).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Survival of the Fittest" }), "a payload it cannot resolve is none");
    }

    // Where an Animist stands to pull, with its turret planted (reach 1,000) and a spell stop of 1,420. With a damage
    // turret (Arboreal's): by its turret while both its spell and its turret reach the target from there, the turret
    // less the margin (850); else by the target, the margin inside 850 of it (700), to plant a new turret there (#167 E
    // CY3: F10b pulled while only the spell reached, and from its spell's reach toward the turret). It plants within the
    // margin of that stand, so the new turret lands within 850 and keeps. A snare or taunt turret (Creeping's,
    // Verdant's: reach 1,350) keeps F10b's stand: by it while its spell reaches the target, else at its spell's reach
    // toward the turret if the turret reaches that spot (the turret wears the monster down or draws it on its way in).
    [TestCase(50f, 800f, 1_000, true, 50f, false, TestName = "Animist stand: by its turret already")]
    [TestCase(600f, 800f, 1_000, true, 0f, false, TestName = "Animist stand: back to its turret")]
    [TestCase(50f, 850f, 1_000, true, 50f, false, TestName = "Animist stand: a turret at the limit keeps")]
    [TestCase(50f, 1_000f, 1_000, true, 300f, true, TestName = "Animist stand: the margin inside the limit (today: by it)")]
    [TestCase(150f, 1_000f, 1_000, true, 150f, true, TestName = "Animist stand: a new turret where it stands, at the limit")]
    [TestCase(600f, 1_000f, 1_000, true, 600f, true, TestName = "Animist stand: a new turret where it stands (today: back to it)")]
    [TestCase(100f, 2_000f, 1_000, true, 1_300f, true, TestName = "Animist stand: not its spell's reach toward the turret (today: 580)")]
    [TestCase(2_500f, 3_000f, 1_000, true, 2_500f, true, TestName = "Animist stand: near the target already")]
    [TestCase(100f, 3_000f, 1_000, true, 2_300f, true, TestName = "Animist stand: a new turret 700 from the target")]
    [TestCase(50f, 1_150f, 1_350, false, 50f, false, TestName = "Animist stand: a taunt turret reaches 1,200")]
    [TestCase(50f, 1_000f, 1_000, false, 50f, false, TestName = "Animist stand: a snare turret, by it while its spell reaches")]
    [TestCase(100f, 2_000f, 1_000, false, 580f, false, TestName = "Animist stand: a snare turret, its spell's reach toward it")]
    [TestCase(100f, 3_000f, 1_000, false, 2_150f, true, TestName = "Animist stand: a snare turret, a new one 850 from the target")]
    public void An_animist_pulls_from_by_its_turret_only_when_the_turret_reaches_the_target(float botX, float targetX,
        int turretReach, bool damageTurret, float standX, bool replant)
    {
        (Vector3 stand, bool again) = PlayerBotDriver.AnimistStand(new Vector3(botX, 0, 0), Vector3.Zero,
            new Vector3(targetX, 0, 0), 1_420, turretReach, damageTurret);
        Assert.That(again, Is.EqualTo(replant));
        Assert.That(stand.X, Is.EqualTo(standX).Within(0.01f));
        Assert.That((stand.Y, stand.Z), Is.EqualTo((0f, 0f)));
    }

    // #167 E CY3: an Arboreal Animist (damage turret, reach 1,000, spell stop 1,420: limit 850) with its turret 3,000
    // from its target walks to its stand (95 a think, StepToward's stop of 100), plants a new turret at its feet once
    // AnimistStepFor gives Replant (within the margin of the stand), then pulls by it. With the stand at the limit it
    // planted up to the margin beyond it, the new turret failed the limit, and every think planted another.
    [Test]
    public void An_animist_plants_one_new_turret_then_pulls()
    {
        Vector3 target = new(3_000, 0, 0), turret = Vector3.Zero, bot = new(100, 0, 0);
        int replants = 0;
        PlayerBotAnimistStep step = PlayerBotAnimistStep.Walk;
        for (int think = 0; think < 60 && step is not PlayerBotAnimistStep.Pull; think++)
        {
            (Vector3 stand, bool replant) = PlayerBotDriver.AnimistStand(bot, turret, target, 1_420, 1_000, true);
            float toStand = Vector3.Distance(bot, stand);
            step = PlayerBotDriver.AnimistStepFor(toStand, replant, true, 0, think * 1_000L);
            if (step is PlayerBotAnimistStep.Replant) { replants++; turret = bot; }
            else if (step is PlayerBotAnimistStep.Walk && toStand >= 101)
                bot += Vector3.Normalize(stand - bot) * Math.Min(95f, toStand);
        }
        Assert.That((step, replants), Is.EqualTo((PlayerBotAnimistStep.Pull, 1)));
        Assert.That(Vector3.Distance(target, turret), Is.LessThanOrEqualTo(850f));
    }

    // #167 E CY3: with a new turret due and none it can plant (the in-combat flag after a fight, too little power), an
    // Animist holds the pull 12 s, then leaves the target (F10b pulled it from its spell's reach, out of the turret's);
    // else it walks to its stand, then plants or pulls.
    [TestCase(0f, false, false, 0L, 1_000L, PlayerBotAnimistStep.Pull)]
    [TestCase(400f, false, false, 0L, 1_000L, PlayerBotAnimistStep.Walk)]
    [TestCase(400f, true, true, 0L, 1_000L, PlayerBotAnimistStep.Walk)]
    [TestCase(100f, true, true, 0L, 1_000L, PlayerBotAnimistStep.Replant)]
    [TestCase(100f, true, false, 0L, 1_000L, PlayerBotAnimistStep.Wait)]
    [TestCase(100f, true, false, 0L, 50_000_000L, PlayerBotAnimistStep.Wait)]          // its first think, at a real game time
    [TestCase(400f, true, false, 1_000L, 12_999L, PlayerBotAnimistStep.Wait)]
    [TestCase(100f, true, false, 1_000L, 13_000L, PlayerBotAnimistStep.Skip)]
    public void An_animist_leaves_a_target_its_turret_cant_reach_when_it_cant_plant_a_new_one(float toStand, bool replant,
        bool canReplant, long waitSince, long now, PlayerBotAnimistStep step) =>
        Assert.That(PlayerBotDriver.AnimistStepFor(toStand, replant, canReplant, waitSince, now), Is.EqualTo(step));

    // #167 E CY3: the target an Animist leaves (Skip) is out of a new turret's reach, not unreachable: the pick alone
    // passes it over 30 s (MarkBlind, as M2's blind drop), so self-defence (Attacker) and the monster coming at it
    // (Incoming) still answer it if it aggroes, and a blocked walk's Unreachable mark is kept.
    [Test]
    public void A_target_an_animist_leaves_is_passed_over_by_the_pick_only()
    {
        GameNPC left = Mob(), blocked = Mob();
        var state = new PlayerBotState(Player(null)) { Target = left, Unreachable = blocked, UnreachableUntil = 20_000 };
        PlayerBotDriver.LeaveTurretTarget(state, left, 10_000);
        Assert.That((state.Target, PlayerBotDriver.BlindSkipped(state, left, 39_999),
                PlayerBotDriver.BlindSkipped(state, left, 40_000)),
            Is.EqualTo(((GameLiving)null, true, false)), "the pick passes it over 30 s");
        Assert.That((state.Unreachable == blocked, state.UnreachableUntil), Is.EqualTo((true, 20_000L)), "the unreachable one kept");
        Assert.That(PlayerBotDriver.SkipsHitter(left == state.Unreachable, 39_999, state.UnreachableUntil, false), Is.False,
            "self-defence still takes it");
    }

    [Test]
    public void A_turret_line_gives_its_step_and_the_turrets_reach() =>
        Assert.That(PlayerBotDriver.TurretLine("Pbothaaaj", 35, PlayerBotAnimistStep.Skip, "irewood", ConColor.GREEN, 1_419, 1_000),
            Is.EqualTo("PLAYER_BOT_TURRET bot=Pbothaaaj level=35 step=skip target=\"irewood\" con=GREEN toTurret=1419 reach=1000"));

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

    // In a fight: below 60% and out of a cast, as a GameBot heals itself below 65% before it fights or rests
    // (BotBrain.cs:4890-4897, AutonomousWorldBotController.cs:2725), with the retry timer's wait; whatever its power.
    [TestCase((byte)59, false, 0L, true)] [TestCase((byte)60, false, 0L, false)] [TestCase((byte)30, true, 0L, false)]
    [TestCase((byte)30, false, 1_001L, false)] [TestCase((byte)30, false, 1_000L, true)]
    public void A_bot_heals_itself_below_60_percent_out_of_a_cast(byte health, bool casting, long nextTry, bool heals) =>
        Assert.That(PlayerBotDriver.WantsSelfHeal(health, casting, 1_000, nextTry, inFight: true, 10, 8), Is.EqualTo(heals));

    // Out of a fight (#167 F2): below 85%, with half its power or more left after the heal's cost, then it sits for power;
    // a heal is quicker than sitting for health. Below 60% it heals whatever its power, in a fight or out of one, as
    // before (d1d524d) and as a GameBot heals itself below 65% before it rests (BotBrain.cs:4890-4897): 45% health
    // and 40% power, an 8% heal, heals to about 75% and then sits, rather than sitting at 45%.
    [TestCase((byte)84, false, 1_000L, false, (byte)70, 8, true)] [TestCase((byte)84, false, 1_000L, true, (byte)70, 8, false)]
    [TestCase((byte)59, false, 1_000L, true, (byte)70, 8, true)] [TestCase((byte)84, false, 1_000L, false, (byte)55, 8, false)]
    [TestCase((byte)90, false, 1_000L, false, (byte)70, 8, false)] [TestCase((byte)84, false, 1_000L, false, (byte)58, 8, true)]
    [TestCase((byte)84, true, 1_000L, false, (byte)70, 8, false)] [TestCase((byte)84, false, 1_001L, false, (byte)70, 8, false)]
    [TestCase((byte)85, false, 1_000L, false, (byte)70, 8, false)]                         // the 85% boundary
    [TestCase((byte)45, false, 1_000L, false, (byte)40, 8, true)]                          // low power: the 60% rule
    [TestCase((byte)59, false, 1_000L, false, (byte)10, 8, true)] [TestCase((byte)60, false, 1_000L, false, (byte)10, 8, false)]
    [TestCase((byte)45, true, 1_000L, false, (byte)40, 8, false)] [TestCase((byte)45, false, 1_001L, false, (byte)40, 8, false)]
    public void Out_of_a_fight_a_bot_heals_itself_below_85_percent_before_it_sits(byte health, bool casting, long nextTry,
        bool inFight, byte mana, int cost, bool heals) =>
        Assert.That(PlayerBotDriver.WantsSelfHeal(health, casting, 1_000, nextTry, inFight, mana, cost), Is.EqualTo(heals));

    // A spell's cost in percent of the pool, rounded up: a negative power is a percent already (SpellHandler.cs:1207-1213).
    [TestCase(300, 24, 8)] [TestCase(300, 25, 9)] [TestCase(300, -12, 12)] [TestCase(0, 24, 0)] [TestCase(300, 0, 0)]
    public void A_spells_cost_in_percent_of_the_pool(int maxMana, int power, int percent) =>
        Assert.That(PlayerBotDriver.CostPercent(maxMana, power), Is.EqualTo(percent));

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

    // --- the pet classes' pets (DEATHS.md A5) ---

    private static (Spell, SpellLine) Summon(string type, int level, int id) =>
        (new(new DbSpell { SpellID = id, Name = $"{type}{level}#{id}", Type = type, Target = "Self", Power = -20,
            CastTime = 6.0 }, level), Mending);

    private static string PetSpellName(IEnumerable<(Spell, SpellLine)> spells, int level) =>
        PlayerBotDriver.FindPetSpell(spells, level) is (Spell s, SpellLine) ? s.Name : "-";

    // The main-pet summons (AutonomousPetSupport.IsMainPetSummon): the highest level at or below the bot's, then the
    // higher ID, as a GameBot picks its highest rank. A Bonedancer's minions, a Theurgist's and an Animist's field pets
    // are no main pet.
    [Test]
    public void A_pet_class_summons_its_highest_main_pet_at_or_below_its_level()
    {
        var spells = new List<(Spell, SpellLine)>
        {
            Summon("SummonCommander", 1, 10101), Summon("SummonCommander", 15, 10102), Summon("SummonCommander", 30, 10103),
            Summon("SummonMinion", 18, 10201), Summon("SummonTheurgistPet", 19, 10301), Summon("SummonAnimistFnF", 19, 10401),
            (Damage("DirectDamage", 19, 90), Mending),
        };
        Assert.That(PetSpellName(spells, 20), Is.EqualTo("SummonCommander15#10102"));
        Assert.That(PetSpellName(spells, 30), Is.EqualTo("SummonCommander30#10103"));
        Assert.That(PetSpellName(spells, 0), Is.EqualTo("-"));
        Assert.That(PetSpellName([Summon("SummonTheurgistPet", 5, 11)], 50), Is.EqualTo("-"));
        Assert.That(PetSpellName([Summon("SummonAnimistPet", 11, 11125), Summon("SummonAnimistPet", 11, 11464)], 20),
            Is.EqualTo("SummonAnimistPet11#11464"));
        foreach (string type in new[] { "SummonUnderhill", "SummonDruidPet", "SummonSimulacrum", "SummonSpiritFighter",
                     "SummonHunterPet", "SummonAnimistPet", "SummonNecroPet" })
            Assert.That(PetSpellName([Summon(type, 1, 7)], 1), Is.EqualTo($"{type}1#7"));
    }

    // With no pet, nothing hitting it, out of combat, out of a cast, at 30% power or more, the retry timer due.
    [TestCase(false, false, false, (byte)30, false, 1_000L, true)] [TestCase(true, false, false, (byte)30, false, 1_000L, false)]
    [TestCase(false, true, false, (byte)30, false, 1_000L, false)] [TestCase(false, false, true, (byte)30, false, 1_000L, false)]
    [TestCase(false, false, false, (byte)29, false, 1_000L, false)] [TestCase(false, false, false, (byte)30, true, 1_000L, false)]
    [TestCase(false, false, false, (byte)30, false, 1_001L, false)]
    public void A_bot_summons_its_pet_when_it_has_none_out_of_combat_with_power(bool hasPet, bool beingHit, bool inCombat,
        byte mana, bool casting, long nextTry, bool summons) =>
        Assert.That(PlayerBotDriver.WantsPet(hasPet, beingHit, inCombat, mana, casting, 1_000, nextTry), Is.EqualTo(summons));

    // --- the Theurgist's pets at its target (#167 F1) ---

    // As the world's rows: an Enemy target, 12% of the pool, a 3-s cast, no recast, 2,000 units (Abrasion, Refrigeration,
    // Vapormancy).
    private static (Spell, SpellLine) TheurgistPet(int level, int id) =>
        (new(new DbSpell { SpellID = id, Name = $"Elemental{level}#{id}", Type = "SummonTheurgistPet", Target = "Enemy",
            Power = -12, CastTime = 3.0, Range = 2000 }, level), Mending);

    private static string CombatPetName(IEnumerable<(Spell, SpellLine)> spells, int level, Func<Spell, bool> ready = null) =>
        PlayerBotDriver.FindCombatPetSpell(spells, level, ready ?? (_ => true)) is (Spell s, SpellLine) ? s.Name : "-";

    // A GameBot Theurgist casts its pet at the target whenever it can (AutonomousPetSupport.cs:636-650): the highest
    // summon at or below its level that is ready, then the higher ID, as FindPetSpell picks a main pet.
    [Test]
    public void A_theurgist_casts_its_highest_ready_pet_at_its_target()
    {
        var spells = new List<(Spell, SpellLine)>
        {
            TheurgistPet(10, 102), TheurgistPet(20, 104), TheurgistPet(30, 105), (Damage("DirectDamage", 20, 90), Mending),
        };
        Assert.That(CombatPetName(spells, 25), Is.EqualTo("Elemental20#104"));
        Assert.That(CombatPetName(spells, 25, spell => spell.Level != 20), Is.EqualTo("Elemental10#102"));   // not ready
        Assert.That(CombatPetName([TheurgistPet(20, 264), TheurgistPet(20, 454)], 25), Is.EqualTo("Elemental20#454"));
        Assert.That(CombatPetName([(Damage("Bolt", 20, 90), Mending), Summon("SummonCommander", 15, 10102)], 25),
            Is.EqualTo("-"));                                                           // a Wizard's, a main pet's
    }

    // At a cast choice: a pet summon ready, the target within its range and in view, out of a cast, the retry timer due.
    [TestCase(true, true, false, 1_000L, true)] [TestCase(false, true, false, 1_000L, false)]
    [TestCase(true, false, false, 1_000L, false)] [TestCase(true, true, true, 1_000L, false)]
    [TestCase(true, true, false, 1_001L, false)]
    public void A_theurgist_wants_a_pet_at_its_target_when_one_is_ready_in_range(bool hasSpell, bool inRange, bool casting,
        long nextTry, bool wants) =>
        Assert.That(PlayerBotDriver.WantsCombatPet(hasSpell, inRange, casting, 1_000, nextTry), Is.EqualTo(wants));

    // A summon hurts the target only once its pet reaches it: three in a row would read as fruitless and block the
    // bot's spells for 10 s (the cast watch), so only the damage spells count.
    [Test]
    public void A_pet_cast_does_not_count_in_the_cast_watch()
    {
        Assert.That(PlayerBotDriver.CountsInCastWatch(TheurgistPet(20, 104).Item1), Is.False);
        Assert.That(PlayerBotDriver.CountsInCastWatch(Damage("DirectDamage", 20, 90)), Is.True);
        Assert.That(PlayerBotDriver.CountsInCastWatch(Damage("Bolt", 20, 90)), Is.True);
    }

    // The watch's step Engage takes per cast request: 4 pet casts at an unhurt target never stall it and leave it as it
    // was (its target too); 4 damage casts do. Pets between damage casts neither stall the run nor start a new one.
    [Test]
    public void Pet_casts_never_stall_the_cast_watch_and_damage_casts_do()
    {
        object monster = new(), earlier = new();
        Spell pet = TheurgistPet(20, 104).Item1, nuke = Damage("DirectDamage", 20, 90);
        (object castTarget, int attempts, byte mark, bool stalled) = (earlier, 2, (byte)100, false);
        for (int i = 1; i <= 4; i++)
        {
            (castTarget, attempts, mark, stalled) = PlayerBotDriver.WatchStep(pet, castTarget, monster, attempts, mark, 100);
            Assert.That(stalled, Is.False, $"pet cast {i}");
        }
        Assert.That((castTarget, attempts, mark), Is.EqualTo((earlier, 2, (byte)100)), "the watch as it was");
        for (int i = 1; i <= 4; i++)
            (castTarget, attempts, mark, stalled) = PlayerBotDriver.WatchStep(nuke, castTarget, monster, attempts, mark, 100);
        Assert.That((castTarget, attempts, stalled), Is.EqualTo((monster, 4, true)), "a new target's run, stalled at 4");
        (castTarget, attempts, mark, stalled) = (null, 0, (byte)0, false);
        foreach (Spell spell in new[] { nuke, pet, nuke, pet, nuke, pet })
            (castTarget, attempts, mark, stalled) = PlayerBotDriver.WatchStep(spell, castTarget, monster, attempts, mark, 100);
        Assert.That((attempts, stalled), Is.EqualTo((3, false)));
        Assert.That(PlayerBotDriver.WatchStep(nuke, castTarget, monster, attempts, mark, 100).Stalled, Is.True);
    }

    // The spell choice (the pet, then the damage spell, with its add scan) is made when the choice is due, out of a cast,
    // at most once a second while no cast follows (walking in, swinging, waiting); a cast, a refused pet and a new
    // target make the next due think choose afresh (Engage, RefreshTarget).
    [TestCase(true, false, 1_000L, true)] [TestCase(false, false, 0L, false)] [TestCase(true, true, 0L, false)]
    [TestCase(true, false, 1_001L, false)] [TestCase(true, false, 0L, true)]
    public void A_bot_chooses_its_spell_when_due_at_most_once_a_second(bool due, bool casting, long nextChoice, bool chooses) =>
        Assert.That(PlayerBotDriver.ChoosesSpell(due, casting, 1_000, nextChoice), Is.EqualTo(chooses));

    // --- the pet leads (#167 F10c) ---

    // An owner that anything is hitting never holds (the GameBot's LetMainPetLead needs an owner out of combat,
    // AutonomousPetSupport.cs:913): an add on it while the pet has not drawn the target left it idle for up to 4 s.
    [Test]
    public void The_owner_holds_its_nuke_until_the_target_is_on_its_pet_or_4_s_pass()
    {
        // petAlive, targetOnPet, targetOnBot, beingHit, sinceOrderMs
        Assert.That(PlayerBotDriver.PetLeads(true, false, false, false, 0, 4_000), Is.True);       // the pet just sent in
        Assert.That(PlayerBotDriver.PetLeads(true, false, false, false, 3_999, 4_000), Is.True);
        Assert.That(PlayerBotDriver.PetLeads(true, false, false, false, 4_000, 4_000), Is.False);  // 4 s: the owner nukes
        Assert.That(PlayerBotDriver.PetLeads(true, true, false, false, 500, 4_000), Is.False);     // on the pet: nuke
        Assert.That(PlayerBotDriver.PetLeads(false, false, false, false, 500, 4_000), Is.False);   // no pet to lead
        Assert.That(PlayerBotDriver.PetLeads(true, false, true, false, 500, 4_000), Is.False);     // the target on the owner
        Assert.That(PlayerBotDriver.PetLeads(true, false, false, true, 500, 4_000), Is.False);     // an add hitting it
    }

    // --- the class's own self-buffs (#167 F8) ---

    // As the world's rows (Spell, LineXSpell): power, concentration, pulse and pulse power as there; the duration (s) and
    // the cast time (s) as there when given (#167 C9), else 20 minutes and 3 s (none and instant for a concentration buff
    // and a chant).
    private static (Spell, SpellLine) Buff(int id, string name, string type, string target, int level, int power,
        int concentration = 0, int pulse = 0, int pulsePower = 0, int instrument = 0, int? duration = null,
        double? castTime = null) =>
        (new(new DbSpell { SpellID = id, Name = name, Type = type, Target = target, Power = power, Concentration = concentration,
            Pulse = pulse, PulsePower = pulsePower, Frequency = pulse == 0 ? 0 : 50, InstrumentRequirement = instrument,
            CastTime = castTime ?? (pulse == 0 ? 3.0 : 0),
            Duration = duration ?? (concentration > 0 ? 0 : pulse == 0 ? 1200 : 6) }, level), Mending);

    private static readonly (Spell, SpellLine) DiamondShield = Buff(9, "Diamond Shield", "BaseArmorFactorBuff", "Self", 40, 25),
        BufferOfStone = Buff(22, "Buffer of Stone", "ArmorAbsorptionBuff", "Self", 41, 26);

    // up: the chant up; wanted: the one to run (#167 C13), unless given WantedChant's from the chants the spells give at
    // that level (ChantKinds), standing, at full health, power and endurance.
    private static string SelfBuffName(IEnumerable<(Spell, SpellLine)> spells, int level, IReadOnlySet<eSpellType> active = null,
        eSpellType? up = null, int concentration = 20, int mana = 300, Func<Spell, bool> ready = null, eSpellType? wanted = null)
    {
        (Spell, SpellLine)[] all = spells.ToArray();
        wanted ??= PlayerBotDriver.WantedChant(PlayerBotDriver.ChantKinds(all, level), false, 100, 100, 300, 100, 100);
        return PlayerBotDriver.NextSelfBuff(all, active ?? new HashSet<eSpellType>(), wanted, up, level, concentration, 300, mana,
            ready ?? (_ => true)) is (Spell s, SpellLine) ? s.Name : "-";
    }

    // A Wizard's Path of Earth: its armour factor first, then its absorb (Buffer of Stone is level 41), then none once
    // both are up; a level-40 Wizard has no absorb yet.
    [Test]
    public void A_bot_keeps_its_armour_factor_then_its_absorb_up()
    {
        (Spell, SpellLine)[] earth = [BufferOfStone, DiamondShield];
        var af = new HashSet<eSpellType> { eSpellType.BaseArmorFactorBuff };
        Assert.That(SelfBuffName(earth, 41), Is.EqualTo("Diamond Shield"));
        Assert.That(SelfBuffName(earth, 41, af), Is.EqualTo("Buffer of Stone"));
        Assert.That(SelfBuffName(earth, 41, new HashSet<eSpellType> { eSpellType.BaseArmorFactorBuff, eSpellType.ArmorAbsorptionBuff }),
            Is.EqualTo("-"));
        Assert.That(SelfBuffName(earth, 40, af), Is.EqualTo("-"), "Buffer of Stone comes at 41");
        Assert.That(SelfBuffName(earth, 41, mana: 24), Is.EqualTo("-"), "neither for 24 of 300 power: 25, 26 needed");
        Assert.That(SelfBuffName(earth, 41, ready: spell => spell.ID != 9), Is.EqualTo("Buffer of Stone"), "AF not ready");
    }

    // A Cleric's concentration buffs (Enhancement) need the concentration they take; with too little, the best rank that
    // fits, or a buff that takes none (Hammer of Faith). No cap of our own: the server's 20-effect limit counts
    // concentration effects only (SpellHandler.cs:34, :818).
    [Test]
    public void A_concentration_buff_needs_the_concentration_left()
    {
        (Spell, SpellLine)[] cleric =
        [
            Buff(1465, "Aura of Guarding", "BaseArmorFactorBuff", "Realm", 22, 0, concentration: 8),
            Buff(1466, "Greater Aura of Guarding", "BaseArmorFactorBuff", "Realm", 31, 0, concentration: 11),
            Buff(1651, "Hammer of Faith", "DamageAdd", "Self", 1, 2),
        ];
        Assert.That(SelfBuffName(cleric, 35, concentration: 11), Is.EqualTo("Greater Aura of Guarding"));
        Assert.That(SelfBuffName(cleric, 35, concentration: 9), Is.EqualTo("Aura of Guarding"));
        Assert.That(SelfBuffName(cleric, 35, concentration: 0), Is.EqualTo("Hammer of Faith"));
    }

    // A Paladin runs one chant at a time (a new pulse effect ends the others, EffectListComponent.cs:362-371): the first by
    // the buffs' order (WantedChant), not while it is up, and in place of another that is (#167 C13); never one that costs
    // power each pulse (it would drain the power its rest waits for). Songs need an instrument; buffs on a pet, kinds not
    // kept (haste) and resist buffs are left alone.
    [Test]
    public void A_bot_runs_one_chant_that_costs_no_power_each_pulse()
    {
        (Spell, SpellLine)[] chants =
        [
            Buff(1093, "Soul Ward", "BodySpiritEnergyBuff", "Group", 25, 0, pulse: 1, pulsePower: 8),
            Buff(1026, "Greater Battle Fury", "DamageAdd", "Group", 28, 0, pulse: 1),
            Buff(1036, "Crusader's Shelter", "SpecArmorFactorBuff", "Group", 29, 0, pulse: 1),
        ];
        Assert.That(SelfBuffName(chants, 30), Is.EqualTo("Crusader's Shelter"));
        Assert.That(SelfBuffName(chants, 28), Is.EqualTo("Greater Battle Fury"));
        Assert.That(SelfBuffName(chants, 30, new HashSet<eSpellType> { eSpellType.SpecArmorFactorBuff },
            up: eSpellType.SpecArmorFactorBuff), Is.EqualTo("-"));
        Assert.That(SelfBuffName(chants, 30, new HashSet<eSpellType> { eSpellType.DamageAdd }, up: eSpellType.DamageAdd),
            Is.EqualTo("Crusader's Shelter"), "the first by the order in place of the one up");
        Assert.That(SelfBuffName(chants, 27), Is.EqualTo("-"), "Soul Ward costs power each pulse");
        Assert.That(SelfBuffName(
            [
                Buff(2001, "Song of Rest", "HealthRegenBuff", "Group", 10, 0, pulse: 1, instrument: 1),
                Buff(2002, "Spirit Shield", "BaseArmorFactorBuff", "Pet", 10, 5),
                Buff(2003, "Haste", "CombatSpeedBuff", "Self", 10, 5),
                Buff(2004, "Ward of the Spirit", "BodySpiritEnergyBuff", "Self", 10, 5),     // a resist buff, no chant
            ], 30), Is.EqualTo("-"));
    }

    // The resist chants are no self-buff the bot keeps: the world's only ones of those kinds a class casts are the
    // Paladin's (Chants), and each costs power every pulse (v0.37b Spell and LineXSpell rows), so a bot at any level never
    // ran one; not even one that pulsed for free would be kept, and FIGHT buffs= does not count them.
    [Test]
    public void A_bot_keeps_no_resist_chant()
    {
        (Spell, SpellLine)[] paladin =
        [
            Buff(1093, "Soul Ward", "BodySpiritEnergyBuff", "Group", 25, 0, pulse: 1, pulsePower: 8),
            Buff(1091, "Elemental Ward", "HeatColdMatterBuff", "Group", 27, 0, pulse: 1, pulsePower: 9),
            Buff(1094, "Soul Shield", "BodySpiritEnergyBuff", "Group", 44, 0, pulse: 1, pulsePower: 15),
            Buff(1092, "Elemental Shield", "HeatColdMatterBuff", "Group", 46, 0, pulse: 1, pulsePower: 16),
        ];
        Assert.That(SelfBuffName(paladin, 50), Is.EqualTo("-"));
        Assert.That(SelfBuffName(
            [
                Buff(1093, "Soul Ward", "BodySpiritEnergyBuff", "Group", 25, 0, pulse: 1),
                Buff(1091, "Elemental Ward", "HeatColdMatterBuff", "Group", 27, 0, pulse: 1),
            ], 50), Is.EqualTo("-"), "a resist chant that pulsed for free");
        Assert.That(PlayerBotDriver.SelfBuffsUp([eSpellType.BodySpiritEnergyBuff, eSpellType.HeatColdMatterBuff]), Is.Zero);
    }

    // --- self-buffs in the post-kill window, never mid-rest; short ones only in a fight (#167 C9) ---

    // Short buffs as the world has them (c-rest/out-a7.txt): a Spiritmaster's Benison of the Fallen, a Reaver's Surge of
    // Maiming (12% of its power, instant), a Shaman's Frigg's Cure; a Sorcerer's Perspicuity of Power lasts 20 minutes.
    private static readonly (Spell, SpellLine) Benison = Buff(3001, "Benison of the Fallen", "DamageShield", "Self", 30, 18,
            duration: 60, castTime: 4.0),
        Surge = Buff(3002, "Surge of Maiming", "DamageAdd", "Self", 30, -12, duration: 30, castTime: 0),
        FriggsCure = Buff(3003, "Frigg's Cure", "HealthRegenBuff", "Realm", 30, 15, duration: 30, castTime: 3.0),
        Perspicuity = Buff(3004, "Perspicuity of Power", "PowerRegenBuff", "Self", 30, 20, duration: 1_200),
        ChantOfTheWar = Buff(3005, "Chant of the War", "DamageAdd", "Group", 35, 0, pulse: 1),
        SongOfRest = Buff(3006, "Magnificent Song of Rest", "HealthRegenBuff", "Group", 30, 0, pulse: 1);

    // The scan runs standing, or sitting once recovered (it stands to cast, CastOnSelf): never mid-rest. 708 of
    // st-deathsB's 788 rests a buff ended were stood up in their middle, 2,989 s of casts.
    [TestCase(false, false, true)] [TestCase(true, false, false)] [TestCase(true, true, true)] [TestCase(false, true, true)]
    public void The_self_buff_scan_never_stands_the_bot_mid_rest(bool sitting, bool recovered, bool allowed) =>
        Assert.That(PlayerBotDriver.BuffScanAllowed(sitting, recovered), Is.EqualTo(allowed));

    // SelfBuff's whole gate (SelfBuffScanDue; the review of 9aacd69: with BuffScanAllowed switched off in it no test
    // failed): its scan due, no cast under way, no live fight, and never mid-rest.
    [TestCase(5_000L, 5_000L, false, false, false, false, true)] [TestCase(4_999L, 5_000L, false, false, false, false, false)]
    [TestCase(5_000L, 0L, true, false, false, false, false)] [TestCase(5_000L, 0L, false, true, false, false, false)]
    [TestCase(5_000L, 0L, false, false, true, false, false)] [TestCase(5_000L, 0L, false, false, true, true, true)]
    public void The_self_buff_scan_runs_due_out_of_a_cast_and_a_fight_and_never_mid_rest(long now, long next, bool casting,
        bool fighting, bool sitting, bool recovered, bool due) =>
        Assert.That(PlayerBotDriver.SelfBuffScanDue(now, next, casting, fighting, sitting, recovered), Is.EqualTo(due));

    // Out of a fight the bot keeps up chants and buffs of 2 minutes or more (or for good); a shorter one only a
    // regeneration buff while its resource is under its rest's stop: Benison (176 stands in st-deathsB), Surge (85, up at
    // engage in 35% of the Reaver's fights) and Frigg's Cure at full health (157) were cast again and again between fights.
    [Test]
    public void Out_of_a_fight_a_bot_keeps_up_its_long_buffs_and_its_chant_a_short_one_only_to_regenerate()
    {
        bool Keeps((Spell Spell, SpellLine) buff, byte health = 100) =>
            PlayerBotDriver.KeepsUpOutOfFight(buff.Spell, health, 300, 100, 100, 100);
        Assert.That((Keeps(Benison), Keeps(Surge), Keeps(FriggsCure, 80), Keeps(FriggsCure, 100)),
            Is.EqualTo((false, false, true, false)));
        Assert.That((Keeps(Perspicuity), Keeps(DiamondShield), Keeps(ChantOfTheWar)), Is.EqualTo((true, true, true)));
        Assert.That(PlayerBotDriver.KeepsUpOutOfFight(Buff(3007, "a short power regeneration", "PowerRegenBuff", "Self", 30, 5,
            duration: 30).Item1, 100, 300, 89, 90, 100), Is.True, "under the caster's power stop");
    }

    // The short buffs go up in a fight instead: an instant one at the fight's first think (Surge); one with a cast
    // (Benison, Frigg's Cure), a long one or a chant not then.
    [Test]
    public void A_fight_opens_with_an_instant_short_buff()
    {
        Assert.That(PlayerBotDriver.OpensFight(Surge.Item1), Is.True);
        Assert.That((PlayerBotDriver.OpensFight(Benison.Item1), PlayerBotDriver.OpensFight(FriggsCure.Item1),
            PlayerBotDriver.OpensFight(DiamondShield.Item1), PlayerBotDriver.OpensFight(ChantOfTheWar.Item1)),
            Is.EqualTo((false, false, false, false)));
    }

    // #167 D12: the fight's own buff (C9's Surge) only in a hard fight: a YELLOW or harder at the fight's first think, or
    // a second live monster arriving on the bot. With a Surge at every fight's start a Reaver's power per won fight went
    // from 12.7 to 27.0 in st-deathsC; 101 of its 193 rests were bound by power, and a Surge costs about 23 s of rest to
    // save 2.5 s or less in a GREEN fight.
    [TestCase(true, ConColor.GREEN, 1, 0, false)] [TestCase(true, ConColor.BLUE, 1, 0, false)]
    [TestCase(true, ConColor.YELLOW, 1, 0, true)] [TestCase(true, ConColor.ORANGE, 0, 0, true)]
    [TestCase(false, ConColor.GREEN, 2, 1, true)] [TestCase(false, ConColor.GREEN, 2, 2, false)]
    [TestCase(false, ConColor.BLUE, 1, 1, false)] [TestCase(true, ConColor.GREEN, 2, 0, true)]
    public void The_fights_buff_goes_only_in_a_hard_fight(bool opening, ConColor con, int attackers, int before, bool due) =>
        Assert.That(PlayerBotDriver.FightBuffDue(opening, con, attackers, before), Is.EqualTo(due));

    // #167 D12, the verifier's correction: the monsters before are those of the last think the check ran on, never one the
    // bot casts on (Engage skips the buff then): an add that arrives while the bot casts brings the buff on the next think.
    [Test]
    public void An_add_arriving_while_the_bot_casts_brings_the_fights_buff_on_the_next_think()
    {
        Assert.That(PlayerBotDriver.FightBuffCheck(casting: true, opening: false, ConColor.GREEN, 2, 1), Is.EqualTo((false, 1)));
        Assert.That(PlayerBotDriver.FightBuffCheck(casting: false, opening: false, ConColor.GREEN, 2, 1), Is.EqualTo((true, 2)));
        Assert.That(PlayerBotDriver.FightBuffCheck(casting: false, opening: false, ConColor.GREEN, 2, 2), Is.EqualTo((false, 2)));
        Assert.That(PlayerBotDriver.FightBuffCheck(casting: true, opening: true, ConColor.YELLOW, 1, 0), Is.EqualTo((false, 0)),
            "a fight opened mid-cast: no buff, as before");
    }

    // After a kill the self-buffs scan at once, in the window before the rest, and back to back (the verifier's correction:
    // preset, or the test passes before the fix). A dropped fight leaves the scan's time.
    [TestCase(true, 0L)] [TestCase(false, 9_000L)]
    public void A_kill_has_the_self_buffs_scan_at_once(bool engaged, long next)
    {
        MethodInfo settle = typeof(PlayerBotDriver).GetMethod("SettleTarget", BindingFlags.Static | BindingFlags.NonPublic);
        var state = new PlayerBotState(Player(null))
        {
            Target = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC)), Engaged = engaged,   // health 0: dead
            NextSelfBuffTick = 9_000,
        };
        settle!.Invoke(null, [state, Player(null)]);
        Assert.That(state.NextSelfBuffTick, Is.EqualTo(next));
    }

    // --- the Skald's Song of Rest while it rests (#167 C13) ---

    // The chant the bot runs: while it rests, the regeneration chant of what is short of its rest's stop (a Skald's Song
    // of Rest while its health is: the pulse runs on while it sits, SpellHandler.cs:196-232), else its first by the buffs'
    // order, its combat chant (the DamageAdd came first and blocked every other chant: the Skald never sang its Song).
    [Test]
    public void A_skald_wants_its_song_of_rest_while_it_rests_short_of_health_and_its_combat_chant_else()
    {
        eSpellType[] skald = [eSpellType.DamageAdd, eSpellType.HealthRegenBuff];
        eSpellType? Wanted(IReadOnlyCollection<eSpellType> kinds, bool resting, byte health = 100, byte endurance = 100) =>
            PlayerBotDriver.WantedChant(kinds, resting, health, endurance, 300, 100, 100);
        Assert.That(Wanted(skald, true, 70), Is.EqualTo(eSpellType.HealthRegenBuff));
        Assert.That(Wanted(skald, true), Is.EqualTo(eSpellType.DamageAdd), "full health and endurance");
        Assert.That(Wanted(skald, false, 70), Is.EqualTo(eSpellType.DamageAdd), "not resting");
        Assert.That(Wanted([eSpellType.DamageAdd], true, 70), Is.EqualTo(eSpellType.DamageAdd), "no Song");
        Assert.That(Wanted([], true, 70), Is.Null, "no chant");
    }

    // It rests for the chant while sitting or wanting a rest (Resting, SelfBuff's; the review of 9aacd69: with it false
    // no test failed and no Song of Rest ran).
    [TestCase(true, false, true)] [TestCase(false, true, true)] [TestCase(false, false, false)] [TestCase(true, true, true)]
    public void A_bot_sings_for_its_rest_while_it_sits_or_wants_one(bool sitting, bool wantsRest, bool resting) =>
        Assert.That(PlayerBotDriver.Resting(sitting, wantsRest), Is.EqualTo(resting));

    // The chants it can run: of a kind it keeps, at or below its level, no song that needs an instrument and none that
    // costs power each pulse.
    [Test]
    public void A_bots_chants_are_its_kept_kinds_at_its_level_with_no_instrument_or_pulse_power()
    {
        (Spell, SpellLine)[] spells =
        [
            ChantOfTheWar, SongOfRest, Buff(1093, "Soul Ward", "BodySpiritEnergyBuff", "Group", 25, 0, pulse: 1, pulsePower: 8),
            Buff(4001, "a chant that costs power", "SpecArmorFactorBuff", "Group", 29, 0, pulse: 1, pulsePower: 2),
            Buff(4002, "a song", "EnduranceRegenBuff", "Group", 10, 0, pulse: 1, instrument: 1), DiamondShield,
        ];
        Assert.That(PlayerBotDriver.ChantKinds(spells, 40), Is.EquivalentTo(new[] { eSpellType.DamageAdd, eSpellType.HealthRegenBuff }));
        Assert.That(PlayerBotDriver.ChantKinds(spells, 34), Is.EquivalentTo(new[] { eSpellType.HealthRegenBuff }), "the War at 35");
    }

    // NextSelfBuff runs the chant wanted in place of the one up (one pulse effect ends the others), and none while the
    // one wanted is up. With C9 the Skald swaps back to its combat chant once recovered.
    [Test]
    public void A_skald_swaps_its_chant_for_the_one_it_wants()
    {
        (Spell, SpellLine)[] skald = [ChantOfTheWar, SongOfRest];
        var war = new HashSet<eSpellType> { eSpellType.DamageAdd };
        Assert.That(SelfBuffName(skald, 40, war, up: eSpellType.DamageAdd, wanted: eSpellType.HealthRegenBuff),
            Is.EqualTo("Magnificent Song of Rest"));
        Assert.That(SelfBuffName(skald, 40, war, up: eSpellType.DamageAdd, wanted: eSpellType.DamageAdd), Is.EqualTo("-"));
        Assert.That(SelfBuffName(skald, 40, new HashSet<eSpellType> { eSpellType.HealthRegenBuff }, up: eSpellType.HealthRegenBuff),
            Is.EqualTo("Chant of the War"), "recovered: its combat chant back");
    }

    // A Necromancer in shade form cannot swing (AttackComponent.cs:511-516): its servant fights.
    [Test]
    public void A_shade_does_not_swing()
    {
        Assert.That(PlayerBotDriver.CanSwing(null), Is.True);
        Assert.That(PlayerBotDriver.CanSwing((GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameSummonedPet))), Is.True);
        Assert.That(PlayerBotDriver.CanSwing((GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(NecromancerPet))), Is.False);
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
            PlayerBotState state = new PlayerBotState(Player(null));
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
            PlayerBotState state = new PlayerBotState(Player(null));
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

    // pet= and heals= come last (the earlier fields stay as they were: runs/tools read them): whether the bot had a
    // living pet at the fight's first think in reach (DEATHS.md A5), and its heals on itself in the fight (A6); then qc=,
    // whether a cast in the fight went with Quickcast on; then (#167) buffs=, its self-buffs up at that first think (F8),
    // petCasts=, a Theurgist's pets cast at the target (F1), and waits=, the thinks a cast due waited for its own swing's
    // interrupt (WaitToCast, F6); then a caster's control casts, ctl=, and its steps back, kites= (F11); then ctlBroken=,
    // the holds its own swing ended (C3); then addCtl=, its control casts at an add (C4), counted in ctl= too; then
    // (#167, the next leg's) addAt= and addHp=, the seconds into the fight a second monster first hit the bot or came at
    // it and its health then, breakOff=, the fight dropped by a break-off (C2), engageDist=, the distance at its first
    // think in reach, and crowd=, the pick's crowding (CrowdingAlong, C5): -1 for none, a defence or Incoming's pick; then
    // (#167, st-deathsD's) heldSec=, the seconds its target was held (Hold), ctlLanded= and ctlFailed=, its control casts
    // that held or not (ControlLanded), hpAtReach= and tHpAtReach=, its and its target's health at the first hit on it
    // (-1: none), rate=, the health it lost a second over the last 3 s (HealthRate; a gain is negative), wouldBreak=, its
    // health when the projected break-off would have fired (ProjectedBreak; -1: never), and walk=, the walk on the think
    // before the fight's first; then (#167 D8) kiteNone=, (#167 E C1) dots=, its damage-over-time casts, and (#167 E
    // C2b) qcKill=, 1 when a cast went under Quickcast as the killing nuke, and (#167 E C4) ctlMez=, its mezzes at the
    // target; then (#167 E-L) raceLeft=, the race's projection of its target's health at its death, at its first think
    // under 40% with a read (-999: none); then (#167 E-R) endStart= and end=, its endurance at the first think and at
    // the line, styleHeld= and castHeld=, the style choices and the thinks that held one back for a run's reserve; then
    // (#167 E M2) sight= and blind=, and (#167 E R1) blindSteps=, its steps in toward the target out of sight.
    [Test]
    public void FightFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatFight(new PlayerBotFight("Pbotaaaac", 40, "widow claw", 40, ConColor.YELLOW, false,
                true, 100, 38, 100, 12, 21, 1, 9, 2, 4, 0, 0, Pet: true, Heals: 2, Quickcast: true, Buffs: 3, PetCasts: 2, Waits: 5,
                Controls: 2, Kites: 1, CtlBroken: 1, AddControls: 1, AddAt: 6, AddHp: 71, BreakOff: true, EngageDistance: 1_450,
                Crowd: 1, HeldSeconds: 7, CtlLanded: 1, CtlFailed: 1, HpAtReach: 92, TargetHpAtReach: 64, Rate: 4.26, WouldBreak: 38,
                Walk: PlayerBotWalk.Hunt, KiteNone: (1, 0, 2, 3), Dots: 2, KillingQuickcast: true, TargetMezzes: 1,
                RaceLeft: 22, EndStart: 85, End: 31, StylesHeld: 3, CastsHeld: 2, Sight: 4, Blind: true, BlindSteps: 2)),
            Is.EqualTo("PLAYER_BOT_FIGHT bot=Pbotaaaac level=40 target=\"widow claw\" targetLevel=40 con=YELLOW from=pull result=kill " +
                       "startHp=100 endHp=38 startMana=100 endMana=12 sec=21 adds=1 swings=9 notVisible=2 casts=4 lifeKills=0 rests=0 " +
                       "pet=alive heals=2 qc=1 buffs=3 petCasts=2 waits=5 ctl=2 kites=1 ctlBroken=1 addCtl=1 addAt=6 addHp=71 " +
                       "breakOff=1 engageDist=1450 crowd=1 heldSec=7 ctlLanded=1 ctlFailed=1 hpAtReach=92 tHpAtReach=64 rate=4.3 " +
                       "wouldBreak=38 walk=hunt kiteNone=1:0:2:3 dots=2 qcKill=1 ctlMez=1 raceLeft=22 endStart=85 end=31 " +
                       "styleHeld=3 castHeld=2 sight=4 blind=1 blindSteps=2"));
        Assert.That(PlayerBotDriverReport.FormatFight(new PlayerBotFight("Pbothaaab", 43, "empyrean guardian", 36, ConColor.BLUE, true,
                false, 64, 51, 100, 100, 7, 2, 3, 0, 0, 3, 1, Rate: -0.04, Walk: PlayerBotWalk.BreakOff)),
            Is.EqualTo("PLAYER_BOT_FIGHT bot=Pbothaaab level=43 target=\"empyrean guardian\" targetLevel=36 con=BLUE from=defence " +
                       "result=dropped startHp=64 endHp=51 startMana=100 endMana=100 sec=7 adds=2 swings=3 notVisible=0 casts=0 " +
                       "lifeKills=3 rests=1 pet=none heals=0 qc=0 buffs=0 petCasts=0 waits=0 ctl=0 kites=0 ctlBroken=0 addCtl=0 " +
                       "addAt=-1 addHp=-1 breakOff=0 engageDist=-1 crowd=-1 heldSec=0 ctlLanded=0 ctlFailed=0 hpAtReach=-1 " +
                       "tHpAtReach=-1 rate=0.0 wouldBreak=-1 walk=breakoff kiteNone=0:0:0:0 dots=0 qcKill=0 ctlMez=0 " +
                       "raceLeft=-999 endStart=-1 end=-1 styleHeld=0 castHeld=0 sight=0 blind=0 blindSteps=0"),
            "a gain under 0.05 a second: 0.0, not -0.0");
    }

    // #167 E M2: a swing needs sight (AttackComponent.cs:1062-1070). The navmesh's 2D ray, or with none its target hitting
    // the bot in contact (within CaughtReach, 250): a client in contact sees what hits it. Not at range: a ranged monster
    // casting at a ranged bot from behind a wall is no sight (the review). st-deathsD2/D2b: 5 deaths in fights with half
    // the swings or more refused for sight (98 of 111, 344 of 345), every killer the bot's own target in contact.
    [TestCase(true, 1_000, false, true)]
    [TestCase(false, 150, true, true)]
    [TestCase(false, 250, true, true)]
    [TestCase(false, 251, true, false)]
    [TestCase(false, 150, false, false)]
    [TestCase(false, 1_500, true, false)]                                      // a ranged bot hit from range
    public void A_target_hitting_the_bot_in_contact_is_in_sight(bool los, int distance, bool fightsBot, bool inView) =>
        Assert.That(PlayerBotDriver.SightInReach(los, distance, fightsBot), Is.EqualTo(inView));

    // #167 E M2: out of sight since (0: in sight): kept while out of sight, now when it goes.
    [TestCase(0L, false, 5_000L, 5_000L)]
    [TestCase(5_000L, false, 6_000L, 5_000L)]
    [TestCase(5_000L, true, 6_000L, 0L)]
    [TestCase(0L, true, 6_000L, 0L)]
    public void The_blind_clock_runs_while_the_target_is_out_of_sight(long since, bool inView, long now, long next) =>
        Assert.That(PlayerBotDriver.BlindClock(since, inView, now), Is.EqualTo(next));

    // #167 E M2: out of sight 5 s, fighting neither the bot nor its pet, nor coming at it: dropped. 15 stuck fights
    // ended in a kill with the bot unhurt (another bot's or the pet's), the longest 250 s. One with the bot as its
    // target is kept (the review): Incoming took it back at the next think, and 5 s on it was dropped again, a FIGHT
    // line each time; it is coming at the bot (round an obstacle) or fighting it.
    [TestCase(1_000L, 6_000L, false, false, false, true)]
    [TestCase(1_000L, 5_999L, false, false, false, false)]
    [TestCase(0L, 60_000L, false, false, false, false)]
    [TestCase(1_000L, 6_000L, true, false, false, false)]
    [TestCase(1_000L, 6_000L, false, true, false, false)]
    [TestCase(1_000L, 6_000L, false, false, true, false)]
    public void A_target_out_of_sight_5_s_that_fights_neither_bot_nor_pet_is_dropped(long blindSince, long now,
        bool fightsBot, bool petOnTarget, bool targetsBot, bool drops) =>
        Assert.That(PlayerBotDriver.DropsBlindTarget(blindSince, now, fightsBot, petOnTarget, targetsBot), Is.EqualTo(drops));

    // #167 E M2: a target dropped blind is passed over by the pick (FindWithin) for 30 s, and only by it: self-defence
    // (Attacker) and the monster coming at it (Incoming) still take it, so one that comes round and hits the bot is fought,
    // and a blocked walk's Unreachable mark stays as it was (the review).
    [Test]
    public void A_target_dropped_blind_is_passed_over_by_the_pick_only()
    {
        GameNPC blind = Mob(), blocked = Mob();
        var state = new PlayerBotState(Player(null)) { Unreachable = blocked, UnreachableUntil = 20_000 };
        PlayerBotDriver.MarkBlind(state, blind, 10_000);
        Assert.That((state.Unreachable == blocked, state.UnreachableUntil), Is.EqualTo((true, 20_000L)),
            "the unreachable one kept");
        Assert.That(PlayerBotDriver.BlindSkipped(state, blind, 39_999), Is.True, "the pick passes it over");
        Assert.That(PlayerBotDriver.BlindSkipped(state, blind, 40_000), Is.False, "30 s on");
        Assert.That(PlayerBotDriver.BlindSkipped(state, blocked, 39_999), Is.False, "another monster");
        Assert.That(PlayerBotDriver.SkipsHitter(blind == state.Unreachable, 39_999, state.UnreachableUntil, false), Is.False,
            "self-defence still takes it");
    }

    // #167 E R1: every monster dropped blind is passed over by the pick, each for its own 30 s. With one mark, a second
    // drop forgot the first and the pick went back to it: st-deathsE/Eb, 213 drops of a name dropped again within 30 s
    // with another between (A, B, A); Pbotmaabw's spider and goblin guard, one every 6-8 s for 3 minutes.
    [Test]
    public void Monsters_dropped_blind_are_each_passed_over_for_their_own_30_s()
    {
        GameNPC first = Mob(), second = Mob();
        var state = new PlayerBotState(Player(null));
        PlayerBotDriver.MarkBlind(state, first, 10_000);
        PlayerBotDriver.MarkBlind(state, second, 15_000);
        Assert.That((PlayerBotDriver.BlindSkipped(state, first, 20_000), PlayerBotDriver.BlindSkipped(state, second, 20_000)),
            Is.EqualTo((true, true)), "the first still passed over after the second's drop");
        Assert.That((PlayerBotDriver.BlindSkipped(state, first, 40_000), PlayerBotDriver.BlindSkipped(state, second, 40_000)),
            Is.EqualTo((false, true)), "each its own 30 s");
        PlayerBotDriver.MarkBlind(state, first, 41_000);
        Assert.That((PlayerBotDriver.BlindSkipped(state, first, 70_999), PlayerBotDriver.BlindSkipped(state, second, 44_999)),
            Is.EqualTo((true, true)), "dropped again: 30 s from then, the other's mark kept");
    }

    // #167 E R1: a bot out of sight of a target it would drop (fighting neither it nor its pet, nor coming at it: kept is
    // false) steps in toward it, BlindStepDistance at a time, until in sight or in contact (CaughtReach); in contact the
    // blind clock drops it as before (0: no step). The blind drops' stands on st-deathsE/Eb: 644 of 657 beyond contact,
    // 547 past 1,000 (a caster's or a Thane's spell stop, round a cave's bend), where the bot stood 5 s and dropped it,
    // and the next pick from there was blind too; D's cast watch had walked it in (the top three Thanes: castFailed
    // 13-18, 47 kills of 48 fights on D; 5 kills on E). Never nearer than its melee stop, nor than contact (an archer's
    // "melee" stop is its bow's).
    [TestCase(false, false, 1_420, 80, 1_120)]                                       // a caster at its spell's stop
    [TestCase(false, false, 500, 80, 200)]
    [TestCase(false, false, 300, 80, 80)]                                           // no nearer than its melee stop
    [TestCase(false, false, 251, 80, 80)]
    [TestCase(false, false, 250, 80, 0)]                                            // in contact: the blind clock
    [TestCase(false, false, 100, 80, 0)]
    [TestCase(true, false, 1_420, 80, 0)]                                           // in sight
    [TestCase(false, true, 1_420, 80, 0)]                                           // kept: it fights the bot or its pet,
    [TestCase(false, false, 1_450, 1_452, 1_150)]                                   //   or comes at it; an archer
    [TestCase(false, false, 500, 1_452, 250)]                                       //   down to contact
    public void A_bot_out_of_sight_of_its_target_beyond_contact_steps_in(bool inView, bool kept, int distance, int meleeStop,
        int stepStop) =>
        Assert.That(PlayerBotDriver.BlindStepStop(inView, kept, distance, meleeStop), Is.EqualTo(stepStop));

    // #167: one PLAYER_BOT_REST line per rest when the bot stands: the seconds it sat and why it stood (recovered, hit,
    // heal, pet or buff: a cast on itself, died, other); then (the next leg's) its health and power when it sat, hp= and
    // mp=, and respawn=, the killed monsters' spawn spots its rest spot was read against (RespawnSpots, C7); then (#167
    // D10) blocked=, its rest walks blocked before it sat (RestGoalAt's tries), and inReach=, 1 when it sat in a potential
    // add's reach as the rest check read them (RestReach); then (#167 E-R) end=, its endurance when it sat.
    [Test]
    public void RestFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatRest("Pbotaaaac", 40, 37, "recovered", 54, 12, 2, 0, false, 55),
            Is.EqualTo("PLAYER_BOT_REST bot=Pbotaaaac level=40 sec=37 why=recovered hp=54 mp=12 respawn=2 blocked=0 inReach=0 " +
                       "end=55"));
        Assert.That(PlayerBotDriverReport.FormatRest("Pbotaaaac", 40, 12, "hit", 70, 40, 0, 3, true, 100),
            Is.EqualTo("PLAYER_BOT_REST bot=Pbotaaaac level=40 sec=12 why=hit hp=70 mp=40 respawn=0 blocked=3 inReach=1 end=100"));
    }

    [Test]
    public void The_self_buffs_up_count_each_kept_kind_once()                    // FIGHT buffs= (#167 F8)
    {
        Assert.That(PlayerBotDriver.SelfBuffsUp([eSpellType.BaseArmorFactorBuff, eSpellType.BaseArmorFactorBuff,
            eSpellType.StrengthBuff, eSpellType.CombatSpeedBuff, eSpellType.DirectDamage]), Is.EqualTo(2));
        Assert.That(PlayerBotDriver.SelfBuffsUp([]), Is.Zero);
    }

    [Test]
    public void A_rest_ends_recovered_unless_a_fight_stood_the_bot_up()           // PLAYER_BOT_REST why=
    {
        Assert.That((PlayerBotDriver.RestStop(mayRest: true, incomingOnly: false),
            PlayerBotDriver.RestStop(mayRest: false, incomingOnly: false)), Is.EqualTo(("recovered", "hit")));
        Assert.That(PlayerBotDriver.RestStop(mayRest: false, incomingOnly: true), Is.EqualTo("incoming"),
            "a monster coming at it, no hit yet (#167 F7)");
    }

    // A cast on itself that stands the bot up ends the rest as heal, pet or buff, unless the bot had recovered (the review
    // of 9aacd69: C9's buffs wait for the rest's end, so a rest that ended recovered logged why=buff, and every Skald's
    // did, its Song of Rest swapped back; C9's mid-rest count reads why=buff).
    [TestCase("buff", true, "recovered")] [TestCase("pet", true, "recovered")] [TestCase("buff", false, "buff")]
    [TestCase("heal", false, "heal")]
    public void A_cast_on_itself_ends_a_rest_as_recovered_once_the_bot_has(string why, bool recovered, string logged) =>
        Assert.That(PlayerBotDriver.RestEndWhy(why, recovered), Is.EqualTo(logged));

    // #167 D13 R1a: a cast on itself as the bot sits recovered ends the rest owed (CastOnSelf). 84 re-sits came straight
    // after REST why=recovered in st-deathsC, 38 of them a Skald's chant swap (5 endurance, SpellHandler.cs:1244-1250)
    // taking it under the 60% stop while RecoverBeforePull was still set: Think clears it only after step 11b's casts. A
    // standing cast keeps the rest owed (D3: a caster's login buffs, cast standing, keep its login rest).
    [TestCase(true, true, true, false)] [TestCase(true, false, true, true)] [TestCase(true, true, false, true)]
    [TestCase(false, true, true, false)]
    public void A_cast_as_the_bot_sits_recovered_ends_the_rest_owed(bool owed, bool sitting, bool recovered, bool stillOwed) =>
        Assert.That(PlayerBotDriver.RestOwedAfterCast(owed, sitting, recovered), Is.EqualTo(stillOwed));

    // #167 D13 R1b: the kill stops a cast still under way at the dead target (SettleTarget). The kill's buff scan needs no
    // cast under way (SelfBuffScanDue), so it waited; the rest then stopped the cast and sat, and the buffs waited for the
    // rest's end (BuffScanAllowed): 46 of the 84 re-sits. A cast at anything else (a heal on itself) goes on.
    [TestCase(true, true, true)] [TestCase(true, false, false)] [TestCase(false, true, false)]
    public void The_kill_stops_a_cast_at_the_dead_target(bool casting, bool atDeadTarget, bool stops) =>
        Assert.That(PlayerBotDriver.StopsCastAtKill(casting, atDeadTarget), Is.EqualTo(stops));

    // The fields PLAYER_BOT_DEATH appends after task= (its earlier fields stay as they were: runs/legcheck.py reads them),
    // then the names the bot avoided when it died (DEATHS.md A3), before this death's; then (#167 F4) again=, no kill
    // since its last release, sinceRelease=, the seconds since it (-1: none this login), and ill=, the resurrection
    // illness on at the fatal fight's start; then (#167 C3) the fatal fight's caster fields as FIGHT gives them, ctl=,
    // kites=, casts=, qc= and waits=, ctlBroken=, the holds its own swing ended, and addCtl= (C4): -1 out of a fight;
    // then FIGHT's addAt=, addHp= and engageDist= (-1 out of a fight too) and breakOff=, a death on a break-off's run;
    // then (st-deathsD's) FIGHT's heldSec=, ctlLanded=, ctlFailed=, hpAtReach=, tHpAtReach= and wouldBreak= (-1 out of a
    // fight), rate= at the death, walk=, the fight's (FIGHT's) or, out of a fight, the last think's alive, and end=, the
    // endurance; then (#167 D8) FIGHT's kiteNone=, (#167 E C1) dots=, (#167 E C2b) qcKill= and (#167 E C4) ctlMez= (-1
    // out of a fight), and (#167 E-L) raceLeft= (-999 out of a fight), and (#167 E-R) styleHeld= and castHeld= (-1 out
    // of a fight).
    [Test]
    public void DeathDetailFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatDeathDetail(new PlayerBotDeathDetail(PlayerBotActivity.Fighting, 62, 14, 71, false,
                2, 3, 0, 40, 100, 6, 1, "widow claw", 40, true, 748, Avoided: 2, Again: true, SinceRelease: 42, Ill: true,
                Controls: 2, Kites: 1, Casts: 4, Quickcast: 1, Waits: 3, CtlBroken: 1, AddControls: 1, AddAt: 6, AddHp: 71,
                EngageDistance: 1_450, HeldSeconds: 5, CtlLanded: 1, CtlFailed: 0, HpAtReach: 88, TargetHpAtReach: 90, Rate: 12.5,
                WouldBreak: 52, Walk: PlayerBotWalk.Anchor, Endurance: 64, KiteNone: (0, 2, 1, 0), Dots: 1, KillingQuickcast: 1,
                TargetMezzes: 1, RaceLeft: 35, StylesHeld: 3, CastsHeld: 1)),
            Is.EqualTo("activity=Fighting startHp=62 fightSec=14 targetHp=71 from=pull adds=2 lifeKills=3 rests=0 sinceKillSec=40 " +
                       "mana=100 swings=6 notVisible=1 killer=\"widow claw\" killerLevel=40 killerIsTarget=True fromBind=748 " +
                       "avoided=2 again=True sinceRelease=42 ill=True ctl=2 kites=1 casts=4 qc=1 waits=3 ctlBroken=1 addCtl=1 " +
                       "addAt=6 addHp=71 breakOff=0 engageDist=1450 heldSec=5 ctlLanded=1 ctlFailed=0 hpAtReach=88 tHpAtReach=90 " +
                       "rate=12.5 wouldBreak=52 walk=anchor end=64 kiteNone=0:2:1:0 dots=1 qcKill=1 ctlMez=1 raceLeft=35 " +
                       "styleHeld=3 castHeld=1"));
        // Out of a fight, no target, no kill this life, no killer named by the Dying event, the bind in another region.
        Assert.That(PlayerBotDriverReport.FormatDeathDetail(new PlayerBotDeathDetail(PlayerBotActivity.Hunting, -1, -1, -1, true,
                -1, 0, 2, -1, 37, -1, -1, null, 0, false, -1, BreakOff: true, Rate: 3, Walk: PlayerBotWalk.Clear, Endurance: 100)),
            Is.EqualTo("activity=Hunting startHp=-1 fightSec=-1 targetHp=-1 from=defence adds=-1 lifeKills=0 rests=2 sinceKillSec=-1 " +
                       "mana=37 swings=-1 notVisible=-1 killer=\"\" killerLevel=0 killerIsTarget=False fromBind=-1 avoided=0 " +
                       "again=False sinceRelease=-1 ill=False ctl=-1 kites=-1 casts=-1 qc=-1 waits=-1 ctlBroken=-1 addCtl=-1 " +
                       "addAt=-1 addHp=-1 breakOff=1 engageDist=-1 heldSec=-1 ctlLanded=-1 ctlFailed=-1 hpAtReach=-1 tHpAtReach=-1 " +
                       "rate=3.0 wouldBreak=-1 walk=clear end=100 kiteNone=-1:-1:-1:-1 dots=-1 qcKill=-1 ctlMez=-1 " +
                       "raceLeft=-999 styleHeld=-1 castHeld=-1"),
            "died on a break-off's run: out of a fight (the dropped one's FIGHT has it)");
    }

    // DEATH again= and sinceRelease= (#167 F4): a death with no kill since the bot's last release; none before the first
    // release of the login.
    [TestCase(0L, 0, 9_000L, false, -1L)] [TestCase(5_000L, 0, 47_500L, true, 42L)] [TestCase(5_000L, 2, 47_500L, false, 42L)]
    public void A_death_with_no_kill_since_the_last_release_is_a_death_again(long releasedAt, int lifeKills, long now, bool again,
        long sinceRelease) =>
        Assert.That((PlayerBotDriver.DiedAgain(releasedAt, lifeKills), PlayerBotDriver.SecondsSinceRelease(releasedAt, now)),
            Is.EqualTo((again, sinceRelease)));

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
            "killerLevel=40 killerIsTarget=True fromBind=748 avoided=0 again=False sinceRelease=-1 ill=False ctl=-1 kites=-1 casts=-1 " +
            "qc=-1 waits=-1 ctlBroken=-1 addCtl=-1 addAt=-1 addHp=-1 breakOff=0 engageDist=-1 heldSec=-1 ctlLanded=-1 ctlFailed=-1 " +
            "hpAtReach=-1 tHpAtReach=-1 rate=0.0 wouldBreak=-1 walk=none end=-1 kiteNone=-1:-1:-1:-1 dots=-1 qcKill=-1 " +
            "ctlMez=-1 raceLeft=-999 styleHeld=-1 castHeld=-1"));
        Assert.That(PlayerBotDriver.DeathLine(head, detail, add, target),
            Does.Contain(" killer=\"a wolf\" killerLevel=38 killerIsTarget=False fromBind=748 avoided=0 again=False sinceRelease=-1 ill=False "));
        Assert.That(PlayerBotDriver.DeathLine(head, detail, null, null),
            Does.Contain(" killer=\"\" killerLevel=0 killerIsTarget=False fromBind=748 avoided=0 again=False sinceRelease=-1 ill=False "),
            "no killer and no target: not the target");
    }

    // --- st-deathsD's fight fields (#167 FIGHT and DEATH heldSec=, ctlLanded=, hpAtReach=, rate=, wouldBreak=, walk=) ---

    // rate=: the health lost a second since the oldest think within the last 3 s, from the thinks' samples (SampleHealth);
    // 0 with none (a new life's first think) and none of this think's own; a gain (a heal, regeneration) is negative.
    [TestCase(new long[] { 7_000, 8_000, 9_000 }, new byte[] { 90, 80, 70 }, (byte)60, 10.0)]
    [TestCase(new long[] { 6_750, 7_000 }, new byte[] { 100, 90 }, (byte)60, 10.0)]
    [TestCase(new long[] { 10_000 }, new byte[] { 80 }, (byte)60, 0.0)]
    [TestCase(new long[0], new byte[0], (byte)60, 0.0)]
    [TestCase(new long[] { 8_000 }, new byte[] { 50 }, (byte)60, -5.0)]
    public void The_health_rate_reads_the_oldest_think_of_the_last_3_seconds(long[] ticks, byte[] healths, byte health,
        double rate) =>
        Assert.That(PlayerBotDriver.HealthRate(ticks.Zip(healths), 10_000, health), Is.EqualTo(rate).Within(1e-9));

    // The samples keep the last 3 s of thinks: each think adds its own and drops the older ones.
    [Test]
    public void The_health_samples_keep_the_last_3_seconds()
    {
        var samples = new Queue<(long Tick, byte Health)>();
        foreach ((long tick, byte health) in new (long, byte)[] { (0, 100), (1_000, 90), (2_000, 80), (3_000, 70), (3_250, 60) })
            PlayerBotDriver.SampleHealth(samples, tick, health);
        Assert.That(samples.Select(s => s.Tick), Is.EqualTo(new long[] { 1_000, 2_000, 3_000, 3_250 }));
    }

    // wouldBreak=, logged only (breakoff #5, d-breakoff/projected.py's P6): below the break-off's 40%, or below the health
    // 6 s of the loss would take (95 at most).
    [TestCase((byte)39, 0.0, true)] [TestCase((byte)40, 0.0, false)] [TestCase((byte)59, 10.0, true)]
    [TestCase((byte)60, 10.0, false)] [TestCase((byte)94, 20.0, true)] [TestCase((byte)95, 20.0, false)]
    public void The_projected_break_off_keeps_6_seconds_of_the_loss_in_hand(byte health, double rate, bool fires) =>
        Assert.That(PlayerBotDriver.ProjectedBreak(health, rate), Is.EqualTo(fires));

    // #167 E-L: the race since the first hit on the bot (hpAtReach, tHpAtReach): its target's health when it dies, at both
    // sides' loss since; no read before 20 lost; a target that lost nothing keeps its health.
    [TestCase((byte)39, (byte)60, 100, 100, 35)] [TestCase((byte)39, (byte)20, 100, 100, -31)]
    [TestCase((byte)39, (byte)60, 55, 100, null)] [TestCase((byte)35, (byte)50, 60, 70, 22)]
    [TestCase((byte)35, (byte)40, 60, 100, -44)] [TestCase((byte)39, (byte)100, 100, 100, 100)]
    [TestCase((byte)39, (byte)60, -1, -1, null)]
    [TestCase((byte)80, (byte)60, 100, 100, -100)]                            // 20 lost: it reads
    [TestCase((byte)50, (byte)90, 80, 80, 90)]                                // a healed target keeps its health
    public void The_race_projects_the_targets_health_at_the_bots_death(byte health, byte targetHealth, int hpAtReach,
        int targetHpAtReach, int? left) =>
        Assert.That(PlayerBotDriver.TargetLeftAtDeath(health, targetHealth, hpAtReach, targetHpAtReach), Is.EqualTo(left));

    // #167 E-L: the race is lost with the target projected above 10 at the bot's death. The row (35, 40, 60, 100) is a
    // hurt bot's defence it is winning, where the target is ahead (40 against 35) but falls faster: rest E1's "target
    // ahead" rule would break it off, the race doesn't.
    [TestCase((byte)20, (byte)28, 100, 100, false)] [TestCase((byte)20, (byte)29, 100, 100, true)]
    [TestCase((byte)35, (byte)40, 60, 100, false)] [TestCase((byte)39, (byte)60, 55, 100, false)]
    public void A_race_is_lost_with_the_target_above_10_at_the_bots_death(byte health, byte targetHealth, int hpAtReach,
        int targetHpAtReach, bool losing) =>
        Assert.That(PlayerBotDriver.LosingRace(health, targetHealth, hpAtReach, targetHpAtReach), Is.EqualTo(losing));

    // hpAtReach=: an attack on the bot (GameLiving.LastAttackedByEnemyTick, set on any attack that causes combat, a
    // miss, block, parry, evade or resisted spell too, GameLiving.cs:1515-1535) within the last second.
    [TestCase(0L, false)] [TestCase(4_000L, true)] [TestCase(3_999L, false)] [TestCase(5_000L, true)]
    public void A_hit_counts_at_the_think_within_a_second_of_it(long lastHit, bool now) =>
        Assert.That(PlayerBotDriver.HitNow(lastHit, 5_000), Is.EqualTo(now));

    // ctlLanded=: the control's effect on its target, the bot's own, begun since the cast was asked for; else ctlFailed=
    // (resisted, refused, interrupted). It settles once the cast could have ended and the bot casts no more.
    [Test]
    public void A_control_landed_when_its_effect_from_this_cast_is_on_the_target()
    {
        Assert.That(PlayerBotDriver.ControlLanded([(500, true, 10_250L)], 500, 10_000), Is.True);
        Assert.That(PlayerBotDriver.ControlLanded([(500, false, 10_250L)], 500, 10_000), Is.False, "another's");
        Assert.That(PlayerBotDriver.ControlLanded([(500, true, 9_000L)], 500, 10_000), Is.False, "an earlier cast's");
        Assert.That(PlayerBotDriver.ControlLanded([(501, true, 10_250L), (400, true, 10_500L)], 500, 10_000), Is.False);
        Assert.That(PlayerBotDriver.ControlLanded([], 500, 10_000), Is.False);
        Assert.That(PlayerBotDriver.ControlResolves(12_749, 10_000, 2_500, casting: false), Is.False);
        Assert.That(PlayerBotDriver.ControlResolves(12_750, 10_000, 2_500, casting: false), Is.True);
        Assert.That(PlayerBotDriver.ControlResolves(12_750, 10_000, 2_500, casting: true), Is.False, "still casting it");
    }

    // A fight think (TrackFight): heldSec= by think, hpAtReach= and tHpAtReach= at the first hit, wouldBreak= at the
    // first think the projected break-off would fire; neither moves after.
    [Test]
    public void A_fight_think_counts_the_hold_and_the_first_hit_and_projected_break()
    {
        var state = new PlayerBotState(Player(null));
        PlayerBotDriver.OpenFight(state, 9_750, 100, 100, 300, 100);
        PlayerBotDriver.SampleHealth(state.HealthSamples, 7_500, 100);
        PlayerBotDriver.SampleHealth(state.HealthSamples, 10_000, 90);
        PlayerBotDriver.TrackFight(state, 10_000, targetHeld: true, health: 90, targetHealth: 80, lastHit: 0);
        Assert.That((state.FightHeldMs, state.FightHpAtReach, state.FightTargetHpAtReach, state.WouldBreakHp),
            Is.EqualTo((250L, -1, -1, -1)), "4 a second: under 40 + 6 s of it");
        PlayerBotDriver.SampleHealth(state.HealthSamples, 10_250, 85);
        PlayerBotDriver.TrackFight(state, 10_250, targetHeld: false, health: 85, targetHealth: 70, lastHit: 10_100);
        Assert.That((state.FightHeldMs, state.FightHpAtReach, state.FightTargetHpAtReach), Is.EqualTo((250L, 85, 70)));
        PlayerBotDriver.SampleHealth(state.HealthSamples, 10_500, 47);
        PlayerBotDriver.TrackFight(state, 10_500, targetHeld: true, health: 47, targetHealth: 60, lastHit: 10_400);
        Assert.That((state.FightHeldMs, state.FightHpAtReach, state.FightTargetHpAtReach, state.WouldBreakHp),
            Is.EqualTo((500L, 85, 70, 47)), "53 in 3 s: 17.7 a second, 6 s of it is past 95");
        Assert.That(state.FightRaceLeft, Is.EqualTo(PlayerBotDriver.NoRace), "#167 E-L raceLeft=: 47 is not under 40 yet");
        PlayerBotDriver.TrackFight(state, 10_750, targetHeld: false, health: 30, targetHealth: 50, lastHit: 10_700);
        Assert.That((state.FightHpAtReach, state.WouldBreakHp), Is.EqualTo((85, 47)), "the first ones stay");
        Assert.That(state.FightRaceLeft, Is.EqualTo(40), "the race's first read under 40: 55 lost against 20, 50 - 30 x 20 / 55");
        PlayerBotDriver.TrackFight(state, 11_000, targetHeld: false, health: 20, targetHealth: 50, lastHit: 10_950);
        Assert.That(state.FightRaceLeft, Is.EqualTo(40), "the first read stays");
    }

    // A monster in the world, alive, with an effect list (UT_LongRunPetRecovery.cs:77-86); its effects by type.
    private static GameNPC LiveMonster(int health = 100)
    {
        var npc = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
        npc.ObjectState = GameObject.eObjectState.Active;
        typeof(GameNPC).GetField("m_brains", Hidden)!.SetValue(npc, new System.Collections.ArrayList());
        typeof(GameObject).GetField("m_health", Hidden)!.SetValue(npc, health);
        npc.effectListComponent = EffectListComponent.Create(npc);
        return npc;
    }

    private static Dictionary<eEffect, List<ECSGameEffect>> Effects(GameLiving living) =>
        (Dictionary<eEffect, List<ECSGameEffect>>)typeof(EffectListComponent).GetField("_effects", Hidden)!
            .GetValue(living.effectListComponent)!;

    private static ECSGameSpellEffect SpellEffect(GameLiving target, GameLiving caster, Spell spell, long startTick,
        double effectiveness = 1) =>
        new(new(target, 20_000, effectiveness, new DOL.GS.Spells.SpellHandler(caster, spell, BodyLine))) { StartTick = startTick };

    // ctlLanded= where SettleControl reads it: the control's effect from the bot, begun since the cast, on its target
    // counts landed, none (resisted) failed, a dead target neither; a new control settles the one awaited at once.
    [Test]
    public void A_control_awaited_settles_against_the_effects_on_its_target()
    {
        MethodInfo awaitControl = typeof(PlayerBotDriver).GetMethod("AwaitControl", BindingFlags.Static | BindingFlags.NonPublic)!;
        GamePlayer bot = Player(null), other = Player(null);
        GameNPC monster = LiveMonster();
        Spell root = Control(500, "root", "SpeedDecrease", 10, 99);
        Effects(monster)[eEffect.MovementSpeedDebuff] = [SpellEffect(monster, other, root, 12_100),
            SpellEffect(monster, bot, root, 10_250)];
        var state = new PlayerBotState(Player(null)) { PendingControl = (root, monster, 10_000) };
        awaitControl.Invoke(null, [state, bot, root, monster, 12_000L]);
        Assert.That((state.FightCtlLanded, state.FightCtlFailed, state.PendingControl.At), Is.EqualTo((1, 0, 12_000L)));
        awaitControl.Invoke(null, [state, bot, root, monster, 14_000L]);
        Assert.That((state.FightCtlLanded, state.FightCtlFailed), Is.EqualTo((1, 1)), "another's since 12_000, none of the bot's");
        typeof(GameObject).GetField("m_health", Hidden)!.SetValue(monster, 0);
        awaitControl.Invoke(null, [state, bot, root, monster, 16_000L]);
        Assert.That((state.FightCtlLanded, state.FightCtlFailed), Is.EqualTo((1, 1)), "killed: neither");
    }

    // walk= (F12d's field): each think starts with no walk and keeps the last think's; the fight's first think reads it
    // (OpenFight), so a fight's walk is the one on the think before.
    [Test]
    public void A_think_starts_with_no_walk_and_keeps_the_last_ones()
    {
        var state = new PlayerBotState(Player(null)) { Walk = PlayerBotWalk.Anchor };
        PlayerBotDriver.NextWalk(state);
        Assert.That((state.Walk, state.LastWalk), Is.EqualTo((PlayerBotWalk.None, PlayerBotWalk.Anchor)));
        PlayerBotDriver.NextWalk(state);
        Assert.That((state.Walk, state.LastWalk), Is.EqualTo((PlayerBotWalk.None, PlayerBotWalk.None)));
    }

    // A new life drops the last life's health samples: a release's full health is no gain.
    [Test]
    public void A_new_life_starts_its_health_samples_afresh()
    {
        var state = new PlayerBotState(Player(null)) { DeathRecorded = true, AliveSince = 1_000 };
        PlayerBotDriver.SampleHealth(state.HealthSamples, 5_000, 10);
        PlayerBotDriver.StartLife(state, 20_000, PlayerBotRole.Melee);
        Assert.That(state.HealthSamples, Is.Empty);
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

    // st-deathsD's fix round (R3): D5 (a root read under a snare) changed nothing, and the casters cast roots the server
    // refuses. A snare and a root always conflict (SpeedDecreaseSpellHandler.HasConflictingEffectWith,
    // SpeedDecreaseSpellHandler.cs:44-48, which the damage-and-snare nuke's handler doesn't override), and a new effect
    // replaces one it conflicts with only when it is better (ECSGameEffect.IsBetterThan, ECSGameEffect.cs:179-183: Value
    // and Damage, each by its Effectiveness, both at least as high), else the server refuses it
    // (EffectListComponent.cs:745-779). Every root's Damage is 0 and the snare nukes' 143-148: the snare group landed 10
    // controls and failed 56. RootTakes is that rule on the products: no enabled movement debuff on the target, or the
    // root better than each (equal counts as better).
    [TestCase(new double[0], new double[0], new bool[0], 99.0, 0.0, true)]
    [TestCase(new[] { 35.0 }, new[] { 143.0 }, new[] { false }, 99.0, 0.0, false)]
    [TestCase(new[] { 35.0 }, new[] { 0.0 }, new[] { false }, 99.0, 0.0, true)]
    [TestCase(new[] { 35.0 }, new[] { 143.0 }, new[] { true }, 99.0, 0.0, true)]
    [TestCase(new[] { 99.0 }, new[] { 0.0 }, new[] { false }, 99.0, 0.0, true)]
    [TestCase(new[] { 123.75 }, new[] { 0.0 }, new[] { false }, 99.0, 0.0, false)]           // another's root at 125%
    [TestCase(new[] { 35.0, 99.0 }, new[] { 0.0, 0.0 }, new[] { false, false }, 99.0, 0.0, true)]
    public void A_root_takes_only_over_the_slows_it_beats(double[] values, double[] damages, bool[] disabled,
        double rootValue, double rootDamage, bool takes) =>
        Assert.That(PlayerBotDriver.RootTakes(values.Select((value, i) => (value, damages[i], disabled[i])), rootValue,
            rootDamage), Is.EqualTo(takes));

    // A calculator that gives one value (the caster's debuff effectiveness here).
    private sealed class Fixed(int value) : DOL.GS.PropertyCalc.PropertyCalculator
    {
        public override int CalcValue(GameLiving living, eProperty property) => value;
    }

    // Where the controls read it (Takes, C8): a monster the snare nuke slowed takes no root, so the caster picks its next
    // control (here its mez, ChooseControl's order for a target); a plain snare or none takes the root. The root's product
    // is at the caster's debuff effectiveness (SpellHandler.cs:2169-2172; 0%, then 5%), the slow's at its own
    // Effectiveness: a 50% snare that crit (x2, SpeedDecreaseSpellHandler.cs:64-80) is 100, over a 99 root at 0% and
    // under one at 5% (103.95).
    [Test]
    public void A_monster_under_a_snare_nuke_takes_no_root_and_the_caster_picks_its_next_control()
    {
        MethodInfo takes = typeof(PlayerBotDriver).GetMethod("Takes", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(GamePlayer), typeof(GameLiving), typeof(Spell)])!;
        var calculators = (DOL.GS.PropertyCalc.IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        DOL.GS.PropertyCalc.IPropertyCalculator previous = calculators[(int)eProperty.DebuffEffectiveness],
            previousHealth = calculators[(int)eProperty.MaxHealth];
        calculators[(int)eProperty.DebuffEffectiveness] = new Fixed(0);
        calculators[(int)eProperty.MaxHealth] = new Fixed(100);                       // HealthPercent's (a mez's 75%)
        try
        {
            GamePlayer bot = Player(null), other = Player(null);
            GameNPC monster = LiveMonster();
            typeof(GameLiving).GetField("m_abilities", Hidden)!.SetValue(monster, new Dictionary<string, Ability>());
            typeof(GameLiving).GetField("_abilitiesLock", Hidden)!.SetValue(monster, new System.Threading.Lock());
            Spell snareNuke = new(new DbSpell { SpellID = 5208, Name = "Constricting Force", Type = "DamageSpeedDecrease",
                    Target = "Enemy", Value = 35, Damage = 143, Range = 1500, CastTime = 2.5, Duration = 20 }, 20),
                snare = Control(5209, "a snare", "SpeedDecrease", 20, 35);
            (Spell, SpellLine)[] controls = [(Cripple, BodyLine), (LesserMesmerization, BodyLine)];
            Spell Choose() => PlayerBotDriver.ChooseControl(controls, _ => true,
                spell => (bool)takes.Invoke(null, [bot, monster, spell])!, forAdd: false, petClass: false).Item1;
            Assert.That(Choose(), Is.EqualTo(Cripple), "nothing on it: the root");
            Effects(monster)[eEffect.MovementSpeedDebuff] = [SpellEffect(monster, other, snareNuke, 10_000)];
            Assert.That(Choose(), Is.EqualTo(LesserMesmerization), "under the snare nuke: the mez");
            Effects(monster)[eEffect.MovementSpeedDebuff] = [SpellEffect(monster, other, snare, 10_000)];
            Assert.That(Choose(), Is.EqualTo(Cripple), "a plain snare: the root replaces it");
            Spell halfSnare = Control(5210, "a 50% snare", "SpeedDecrease", 20, 50);
            Effects(monster)[eEffect.MovementSpeedDebuff] = [SpellEffect(monster, other, halfSnare, 10_000, effectiveness: 2.0)];
            Assert.That(Choose(), Is.EqualTo(LesserMesmerization), "a 50% snare that crit (100) over the 99 root: the mez");
            calculators[(int)eProperty.DebuffEffectiveness] = new Fixed(5);
            Assert.That(Choose(), Is.EqualTo(Cripple), "the root at 5% debuff effectiveness (103.95) replaces it");
        }
        finally
        {
            calculators[(int)eProperty.DebuffEffectiveness] = previous;
            calculators[(int)eProperty.MaxHealth] = previousHealth;
        }
    }

    // FIGHT and DEATH ctlBroken= (#167 C3): a hold the bot's own swing ended, as a think sees it: its target held by a root
    // or a mez (a hit or a miss ends them, GameLiving.cs:1762-1777) at the last think, no longer now, and a swing of its
    // own counted since (CountSwing).
    [TestCase(true, false, true, true)] [TestCase(true, true, true, false)] [TestCase(true, false, false, false)]
    [TestCase(false, false, true, false)]
    public void A_hold_that_ends_with_the_bots_own_swing_counts_as_broken(bool heldBefore, bool heldNow, bool swung, bool broke) =>
        Assert.That(PlayerBotDriver.BrokeHold(heldBefore, heldNow, swung), Is.EqualTo(broke));

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
