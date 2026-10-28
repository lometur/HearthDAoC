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
    // A death at its camp (#167 F4) owes a relocation at once, before the capped and outgrown clocks.
    [TestCase(0L, 0L, 1_000_000L, false, null)]
    [TestCase(10_000L, 0L, 129_999L, false, null)] [TestCase(10_000L, 0L, 130_000L, false, "outgrown")]
    [TestCase(0L, 100_000L, 129_999L, false, null)] [TestCase(0L, 100_000L, 130_000L, false, "capped")]
    [TestCase(10_000L, 100_000L, 130_000L, false, "capped")]
    [TestCase(0L, 0L, 1_000_000L, true, "died")] [TestCase(10_000L, 100_000L, 130_000L, true, "died")]
    public void A_capped_bot_relocates_after_30_s_and_an_outgrown_one_after_2_minutes(long roamingSince, long cappedSince,
        long now, bool diedAtCamp, string reason) =>
        Assert.That(PlayerBotDriver.RelocateReason(roamingSince, cappedSince, now, diedAtCamp), Is.EqualTo(reason));

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

    // A capped bot that cannot move its leash (a lever spot bot, out of its anchor region or off the mesh, or no camp
    // in its zone) takes the safest con it passed over where it is, as a GameBot takes the safest camp there is rather
    // than plan forever (AutonomousBotDecisionEngine.cs:287-313); run 1's bots stood idle after two deaths without it.
    // Not for an outgrown bot, nor one that passed over only names it avoids (they expire).
    [TestCase("capped", ConColor.BLUE, ConColor.BLUE)] [TestCase("capped", null, null)]
    [TestCase("outgrown", ConColor.BLUE, null)] [TestCase(null, ConColor.BLUE, null)]
    public void A_capped_bot_that_cannot_move_takes_the_safest_con_it_passed_over(string reason, ConColor? safest,
        ConColor? cap) =>
        Assert.That(PlayerBotDriver.FallbackInPlace(reason, safest), Is.EqualTo(cap));

    // A capped bot stands while it waits to move its leash, unless it walks to its anchor; one that cannot move it now
    // (FallbackInPlace) roams as before. So does one owed a relocation after a death at its camp (#167 F4), from its
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
    // move, the fallback camp's con (none: the death cap) come last.
    [Test]
    public void A_relocation_line_gives_its_reason_last()
    {
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, new Vector3(572911.4f, 457576.6f, 2000), new Vector3(579794,
                456080, 2216.6f), 7040, "silver oaken fellwood", 33, 51, PlayerBotDriver.Capped, ConColor.BLUE),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 from=(572911,457577) to=(579794,456080,2217) distance=7040 " +
                       "monster=\"silver oaken fellwood\" monsterLevel=33 zone=51 reason=capped fallback=BLUE"));
        Assert.That(PlayerBotDriver.RelocateLine("Pbotaaaaz", 34, Vector3.Zero, Vector3.One, 1, "m", 30, 51,
            PlayerBotDriver.Outgrown, null), Does.EndWith(" zone=51 reason=outgrown fallback=-"));
        Assert.That(PlayerBotDriver.RelocateNoneLine("Pbotaaaaz", 34, 3, 51, PlayerBotDriver.Capped),
            Is.EqualTo("PLAYER_BOT_RELOCATE bot=Pbotaaaaz level=34 none checked=3 zone=51 reason=capped"));
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

    // --- the recovery from a death (#167 F4) ---

    // A death within its anchor's leash and roam (LeashRadius + RoamRadius, the reach FindWithin hunts in) is a death at
    // its camp: it owes a relocation before it walks back.
    [TestCase(5_000f, true)] [TestCase(5_400f, true)] [TestCase(5_401f, false)] [TestCase(5_500f, false)]
    public void A_death_within_its_hunting_reach_is_a_death_at_its_camp(float distance, bool atCamp) =>
        Assert.That(PlayerBotDriver.DiedAtCamp(new Vector3(distance, 0, 0), Vector3.Zero), Is.EqualTo(atCamp));

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

    // After a release: it fights back what hits it; else it steps out of reach, then plans the relocation it owes (a
    // death at its camp), then waits out its back-off (the GameBot's, from the third death with no XP:
    // AutonomousDeathRecoveryPolicy) and the resurrection illness; then the recovery is over.
    [TestCase(true, true, true, true, true, PlayerBotRecovery.Fight)]
    [TestCase(false, true, true, true, true, PlayerBotRecovery.StepOut)]
    [TestCase(false, false, true, true, true, PlayerBotRecovery.Replan)]
    [TestCase(false, false, false, true, false, PlayerBotRecovery.Wait)]
    [TestCase(false, false, false, false, true, PlayerBotRecovery.Wait)]
    [TestCase(false, false, false, false, false, PlayerBotRecovery.Done)]
    public void After_a_release_a_bot_steps_out_of_reach_then_replans_then_backs_off(bool attacked, bool stepsOut,
        bool replanOwed, bool backingOff, bool ill, PlayerBotRecovery step) =>
        Assert.That(PlayerBotDriver.AfterReleaseStep(attacked, stepsOut, replanOwed, backingOff, ill), Is.EqualTo(step));

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
            PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
            (state.Target, state.Engaged) = (dead, engaged);
            return state;
        }
        PlayerBotState won = Fought(true), late = Fought(false);
        settle!.Invoke(null, [won, Player(null)]);
        settle.Invoke(null, [late, Player(null)]);
        Assert.That((won.RecoverBeforePull, late.RecoverBeforePull), Is.EqualTo((true, false)), "the late bot never fought it");
    }

    // #167 F3: a rest stops at full health, the role's power stop and 60% endurance; power counts only with a pool.
    [TestCase((byte)100, 300, (byte)72, (byte)61, true)] [TestCase((byte)99, 300, (byte)100, (byte)100, false)]
    [TestCase((byte)100, 300, (byte)69, (byte)100, false)] [TestCase((byte)100, 0, (byte)0, (byte)61, true)]
    [TestCase((byte)100, 300, (byte)100, (byte)59, false)] [TestCase((byte)100, 300, (byte)70, (byte)60, true)]
    public void A_rest_stops_at_full_health_and_the_power_and_endurance_stops(byte health, int maxMana, byte mana,
        byte endurance, bool recovered) =>
        Assert.That(PlayerBotDriver.Recovered(health, maxMana, mana, endurance, 70, 60), Is.EqualTo(recovered));

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

    // Casters stop resting power at 70% (rest-work's 95th-percentile fight cost + 18%); pet classes keep 100% (a 75% stop
    // raised their pet deaths per 100 kills 16% in the model), and so does every other role until measured.
    [Test]
    public void Only_a_caster_stops_resting_power_short_of_full()
    {
        foreach (PlayerBotRole role in Enum.GetValues<PlayerBotRole>())
            Assert.That(PlayerBotDriver.RestPowerStop(role), Is.EqualTo(role == PlayerBotRole.Caster ? 70 : 100), role.ToString());
        Assert.That(PlayerBotDriver.RestEnduranceStop, Is.EqualTo(60));
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
            interruptible, false, due: true) == PlayerBotCombatAction.Cast, Is.EqualTo(casts));

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
            due: true) == PlayerBotCombatAction.Quickcast, Is.EqualTo(uses));

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
            due: true), Is.EqualTo(action));

    // Between casts (the cast choice not due yet, :417) the old think restarted the swing after every cast, and that swing
    // blocked the next cast: with a spell it can cast the bot waits instead. An instant spell does not mind the swing.
    [TestCase(true, false, 0L, true, PlayerBotCombatAction.WaitToCast)]
    [TestCase(true, false, 0L, false, PlayerBotCombatAction.Swing)]                        // an instant spell
    [TestCase(false, false, 0L, true, PlayerBotCombatAction.Swing)]                        // the watch's block, no power
    [TestCase(true, true, 0L, true, PlayerBotCombatAction.Swing)]                          // hit: the old rule
    public void Between_casts_a_bot_with_a_spell_to_cast_does_not_swing(bool canCastSpell, bool interruptedByOther,
        long selfInterrupt, bool interruptible, PlayerBotCombatAction action) =>
        Assert.That(PlayerBotDriver.CombatAction(canCastSpell, interruptedByOther, selfInterrupt, interruptible, false,
            due: false), Is.EqualTo(action));

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
        Assert.That(PlayerBotDriver.CombatStopDistance(canCastSpell, interruptedByOther, interruptible, quickcastReady, 200, 1_500),
            Is.EqualTo(stop));
        Assert.That(PlayerBotDriver.CombatAction(true, false, 0, false, false, due: false), Is.EqualTo(PlayerBotCombatAction.Swing));
        Assert.That(1_420 > PlayerBotDriver.CombatStopDistance(true, false, false, false, 200, 1_500), Is.False,
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
        PlayerBotDriver.ChooseNuke(spells.Select(spell => (spell, Subterranean)), ready, addInRadius).Item1?.Name ?? "-";

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
        Assert.That(PlayerBotDriver.DamageSpells(usable, 20).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Fungal Thistle", "Fungal Ooze" }));                    // Pin: the Thistle outranks it; 30: above
        Assert.That(PlayerBotDriver.DamageSpells(usable, 19).Select(pair => pair.Spell.Name),
            Is.EqualTo(new[] { "Fungal Pin" }));
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

    // As the world's rows (Spell, LineXSpell): power, concentration, pulse and pulse power as there.
    private static (Spell, SpellLine) Buff(int id, string name, string type, string target, int level, int power,
        int concentration = 0, int pulse = 0, int pulsePower = 0, int instrument = 0) =>
        (new(new DbSpell { SpellID = id, Name = name, Type = type, Target = target, Power = power, Concentration = concentration,
            Pulse = pulse, PulsePower = pulsePower, Frequency = pulse == 0 ? 0 : 50, InstrumentRequirement = instrument,
            CastTime = pulse == 0 ? 3.0 : 0, Duration = concentration > 0 ? 0 : pulse == 0 ? 1200 : 6 }, level), Mending);

    private static readonly (Spell, SpellLine) DiamondShield = Buff(9, "Diamond Shield", "BaseArmorFactorBuff", "Self", 40, 25),
        BufferOfStone = Buff(22, "Buffer of Stone", "ArmorAbsorptionBuff", "Self", 41, 26);

    private static string SelfBuffName(IEnumerable<(Spell, SpellLine)> spells, int level, IReadOnlySet<eSpellType> active = null,
        bool chantUp = false, int concentration = 20, int mana = 300, Func<Spell, bool> ready = null) =>
        PlayerBotDriver.NextSelfBuff(spells, active ?? new HashSet<eSpellType>(), chantUp, level, concentration, 300, mana,
            ready ?? (_ => true)) is (Spell s, SpellLine) ? s.Name : "-";

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
    // the buffs' order while none is up, never one that costs power each pulse (it would drain the power its rest
    // waits for). Songs need an instrument; buffs on a pet, kinds not kept (haste) and resist buffs are left alone.
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
        Assert.That(SelfBuffName(chants, 30, chantUp: true), Is.EqualTo("-"));
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

    // pet= and heals= come last (the earlier fields stay as they were: runs/tools read them): whether the bot had a
    // living pet at the fight's first think in reach (DEATHS.md A5), and its heals on itself in the fight (A6); then qc=,
    // whether a cast in the fight went with Quickcast on; then (#167) buffs=, its self-buffs up at that first think (F8),
    // petCasts=, a Theurgist's pets cast at the target (F1), and waits=, the thinks a cast due waited for its own swing's
    // interrupt (WaitToCast, F6).
    [Test]
    public void FightFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatFight(new PlayerBotFight("Pbotaaaac", 40, "widow claw", 40, ConColor.YELLOW, false,
                true, 100, 38, 100, 12, 21, 1, 9, 2, 4, 0, 0, Pet: true, Heals: 2, Quickcast: true, Buffs: 3, PetCasts: 2, Waits: 5)),
            Is.EqualTo("PLAYER_BOT_FIGHT bot=Pbotaaaac level=40 target=\"widow claw\" targetLevel=40 con=YELLOW from=pull result=kill " +
                       "startHp=100 endHp=38 startMana=100 endMana=12 sec=21 adds=1 swings=9 notVisible=2 casts=4 lifeKills=0 rests=0 " +
                       "pet=alive heals=2 qc=1 buffs=3 petCasts=2 waits=5"));
        Assert.That(PlayerBotDriverReport.FormatFight(new PlayerBotFight("Pbothaaab", 43, "empyrean guardian", 36, ConColor.BLUE, true,
                false, 64, 51, 100, 100, 7, 2, 3, 0, 0, 3, 1)),
            Is.EqualTo("PLAYER_BOT_FIGHT bot=Pbothaaab level=43 target=\"empyrean guardian\" targetLevel=36 con=BLUE from=defence " +
                       "result=dropped startHp=64 endHp=51 startMana=100 endMana=100 sec=7 adds=2 swings=3 notVisible=0 casts=0 " +
                       "lifeKills=3 rests=1 pet=none heals=0 qc=0 buffs=0 petCasts=0 waits=0"));
    }

    // #167: one PLAYER_BOT_REST line per rest when the bot stands: the seconds it sat and why it stood (recovered, hit,
    // heal, pet or buff: a cast on itself, died, other).
    [Test]
    public void RestFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatRest("Pbotaaaac", 40, 37, "recovered"),
            Is.EqualTo("PLAYER_BOT_REST bot=Pbotaaaac level=40 sec=37 why=recovered"));
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
        Assert.That((PlayerBotDriver.RestStop(mayRest: true), PlayerBotDriver.RestStop(mayRest: false)), Is.EqualTo(("recovered", "hit")));
    }

    // The fields PLAYER_BOT_DEATH appends after task= (its earlier fields stay as they were: runs/legcheck.py reads them),
    // the last the names the bot avoided when it died (DEATHS.md A3), before this death's.
    [Test]
    public void DeathDetailFormat()
    {
        Assert.That(PlayerBotDriverReport.FormatDeathDetail(new PlayerBotDeathDetail(PlayerBotActivity.Fighting, 62, 14, 71, false,
                2, 3, 0, 40, 100, 6, 1, "widow claw", 40, true, 748, Avoided: 2)),
            Is.EqualTo("activity=Fighting startHp=62 fightSec=14 targetHp=71 from=pull adds=2 lifeKills=3 rests=0 sinceKillSec=40 " +
                       "mana=100 swings=6 notVisible=1 killer=\"widow claw\" killerLevel=40 killerIsTarget=True fromBind=748 " +
                       "avoided=2"));
        // Out of a fight, no target, no kill this life, no killer named by the Dying event, the bind in another region.
        Assert.That(PlayerBotDriverReport.FormatDeathDetail(new PlayerBotDeathDetail(PlayerBotActivity.Hunting, -1, -1, -1, true,
                -1, 0, 2, -1, 37, -1, -1, null, 0, false, -1)),
            Is.EqualTo("activity=Hunting startHp=-1 fightSec=-1 targetHp=-1 from=defence adds=-1 lifeKills=0 rests=2 sinceKillSec=-1 " +
                       "mana=37 swings=-1 notVisible=-1 killer=\"\" killerLevel=0 killerIsTarget=False fromBind=-1 avoided=0"));
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
            "killerLevel=40 killerIsTarget=True fromBind=748 avoided=0"));
        Assert.That(PlayerBotDriver.DeathLine(head, detail, add, target),
            Does.EndWith(" killer=\"a wolf\" killerLevel=38 killerIsTarget=False fromBind=748 avoided=0"));
        Assert.That(PlayerBotDriver.DeathLine(head, detail, null, null),
            Does.EndWith(" killer=\"\" killerLevel=0 killerIsTarget=False fromBind=748 avoided=0"),
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
