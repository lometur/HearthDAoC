using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC proto (#137 lever): HEARTHDAOC_PLAYER_BOT_WAKE=gamebot. ClientService's marked gate keeps a player bot's
// NPC cache but wakes nothing; PlayerBotWake then wakes what a GameBot wakes (BotBrain.cs:1561-1584) plus the bot's
// target, its attackers and the NPC it talks to, with the GameBot stamp only. No running server: doubles made without
// their constructors, as UT_BotCpuMeter.cs:108-114 and UT_HeadlessClient.cs:73-84 make them.
[TestFixture, NonParallelizable]
public sealed class UT_PlayerBotWake
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const int Span = 60_000;                                    // GameNPC.cs:36, VISIBLE_TO_PLAYER_SPAN (private)
    private const long Now = 120_000;                                   // past the span: a fresh double is seen by nobody

    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_HeadlessClient.EmptyReads>();

    // Field initializers do not run on these doubles: the helpers below set every field.
    private sealed class Mob : GameNPC
    {
        public int PositionX, Wakes;
        public ushort Region;
        public bool Alive;
        public override int X => PositionX;
        public override int Y => 0;
        public override int Z => 0;
        public override ushort CurrentRegionID { get => Region; set { } }
        public override bool IsAlive => Alive;
        public override byte HealthPercent => 100;                     // ClientService's cache reads it (:621-624)
        public override void OnAutonomousBotNearby() { Wakes++; base.OnAutonomousBotNearby(); }
    }

    // On the bot (0,0,0 in region 1), so only the exclusion keeps an aggressive brain from waking them.
    private sealed class Taxi : GameTaxi
    {
        public override bool IsAlive => true;
        public override ushort CurrentRegionID { get => 1; set { } }
    }
    private sealed class Dummy : GameTrainingDummy
    {
        public override bool IsAlive => true;
        public override ushort CurrentRegionID { get => 1; set { } }
    }

    private sealed class Bot : GamePlayer
    {
        private Bot() : base(null, null) { }                           // never run
        public ushort Region;
        public bool Alive, Stealthed;
        public override int X => 0;
        public override int Y => 0;
        public override int Z => 0;
        public override ushort CurrentRegionID { get => Region; set { } }
        public override bool IsAlive => Alive;
        public override bool IsStealthed => Stealthed;
    }

    private sealed class TestBrain : StandardMobBrain
    {
        public int Level, Range;
        public bool Held, CanAggro;
        public override int AggroLevel { get => Level; set { } }
        public override int AggroRange { get => Range; set { } }
        public override bool HasAggro => Held;
        public override bool CanAggroTarget(GameLiving target) => CanAggro;
        // ServiceObjectStore.Add's mark (ServiceObjectStore.cs:79-82) without the process-wide store.
        public override bool Start() { ServiceObjectId.OnPreAdd(); return true; }
    }

    private GameServer _savedServer;
    private long _savedTime;

    [SetUp]
    public void SetUp()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        _savedTime = GameLoop.GameLoopTime;
        SetTime(Now);
        PlayerBotWake.Lever = false;
        PlayerBotWake.TakeWindow(0);                                    // the counters are process-wide
    }

    [TearDown]
    public void TearDown()
    {
        PlayerBotWake.Lever = false;
        SetTime(_savedTime);
        GameServer.LoadTestDouble(_savedServer);
    }

    private static void SetTime(long now) =>                            // as UT_AutonomousSummonPolicy.cs:133-134
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime))!.SetValue(null, now);

    private static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static HeadlessClient Client() =>
        new(new DbAccount { Name = "hdbot-test", Language = "EN", PrivLevel = (int)ePrivLevel.Player });

    private static GamePlayer Human()
    {
        GamePlayer player = Make<GamePlayer>();
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(player, Make<GameClient>());
        return player;
    }

    private static Bot PlayerBot(ushort objectId = 7)
    {
        Bot bot = Make<Bot>();
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(bot, Client());
        (bot.Region, bot.Alive, bot.ObjectState, bot.ObjectID) = (1, true, GameObject.eObjectState.Active, objectId);
        return bot;
    }

    private static T WithBrain<T>(T npc, ABrain brain) where T : GameNPC
    {
        typeof(GameNPC).GetField("m_brains", Hidden)!.SetValue(npc, new ArrayList());
        typeof(GameNPC).GetField("m_ownBrain", Hidden)!.SetValue(npc, brain);
        npc.ObjectState = GameObject.eObjectState.Active;
        return npc;
    }

    private static Mob Npc(ABrain brain = null, int x = 400, ushort region = 1)
    {
        Mob npc = WithBrain(Make<Mob>(), brain);
        (npc.PositionX, npc.Region, npc.Alive) = (x, region, true);
        return npc;
    }

    // An aggressive monster (the GameBot rule's every condition holds), or a passive one with level 0.
    private static TestBrain Brain(int level = 50, int range = 500, bool canAggro = true, bool held = false)
    {
        TestBrain brain = Make<TestBrain>();
        typeof(ABrain).GetField("<ServiceObjectId>k__BackingField", Hidden)!.SetValue(brain, new ServiceObjectId(ServiceObjectType.Brain));
        (brain.Level, brain.Range, brain.CanAggro, brain.Held) = (level, range, canAggro, held);
        return brain;
    }

    private static PlayerBotState State(GameLiving target, long nextScan)
    {
        PlayerBotState state = Make<PlayerBotState>();
        state.Target = target;
        state.NextWakeScanTick = nextScan;
        return state;
    }

    // --- the setting ---

    [TestCase(null, false)] [TestCase("", false)] [TestCase(" player ", false)] [TestCase("PLAYER", false)]
    [TestCase("gamebot", true)] [TestCase(" GameBot ", true)]
    public void Parse_reads_player_or_gamebot(string text, bool lever) => Assert.That(PlayerBotWake.Parse(text), Is.EqualTo(lever));

    [TestCase("on")] [TestCase("bots")] [TestCase("1")] [TestCase("game bot")]
    public void Parse_refuses_anything_else_naming_the_variable(string text)
    {
        var e = Assert.Throws<FormatException>(() => PlayerBotWake.Parse(text));
        Assert.That(e!.Message, Is.EqualTo($"HEARTHDAOC_PLAYER_BOT_WAKE must be player or gamebot, got '{text}'"));
    }

    [Test]
    public void Describe_names_the_setting_value() =>
        Assert.That((PlayerBotWake.Describe(false), PlayerBotWake.Describe(true)), Is.EqualTo(("player", "gamebot")));

    // --- the gate ---

    [Test]
    public void A_humans_update_always_wakes_and_a_bots_only_with_the_lever_off()
    {
        GamePlayer human = Human(), bot = PlayerBot();
        Assert.That((PlayerBotWake.ClientUpdateWakes(human), PlayerBotWake.ClientUpdateWakes(bot)), Is.EqualTo((true, true)));
        PlayerBotWake.Lever = true;
        Assert.That((PlayerBotWake.ClientUpdateWakes(human), PlayerBotWake.ClientUpdateWakes(bot)), Is.EqualTo((true, false)));
    }

    [Test]
    public void ClientService_keeps_the_cache_but_does_not_stamp_the_npc_for_a_bot_under_the_lever()
    {
        PlayerBotWake.Lever = true;
        Bot bot = PlayerBot();
        typeof(GamePlayer).GetField("<PlayerObjectCache>k__BackingField", Hidden)!.SetValue(bot, new PlayerObjectCache());
        Mob npc = Npc();
        MethodInfo update = typeof(ClientService).GetMethod("OnObjectCreateOrUpdateForPlayer", HiddenStatic);
        Assert.That(update, Is.Not.Null);
        update!.Invoke(null, [bot, npc]);
        Assert.That(bot.PlayerObjectCache.NpcUpdateCache.ContainsKey(npc), Is.True);
        Assert.That((npc.IsVisibleToPlayers, npc.IsVisibleToPlayersOrBots), Is.EqualTo((false, false)));
        PlayerBotWake.Lever = false;                                    // today's path, unchanged
        update.Invoke(null, [bot, npc]);
        Assert.That(npc.IsVisibleToPlayers, Is.True);
    }

    // --- a wake ---

    [Test]
    public void A_wake_is_the_gamebot_stamp_for_sixty_seconds_never_the_player_stamp()
    {
        Mob npc = Npc();
        PlayerBotWake.Wake(npc, PlayerBotWakeKind.Target);
        Assert.That((npc.IsVisibleToPlayers, npc.IsVisibleToPlayersOrBots, npc.Wakes), Is.EqualTo((false, true, 1)));
        SetTime(Now + Span - 1);
        Assert.That(npc.IsVisibleToPlayersOrBots, Is.True);
        SetTime(Now + Span);
        Assert.That(npc.IsVisibleToPlayersOrBots, Is.False);
    }

    [Test]
    public void A_wake_counts_a_brain_it_starts_once()
    {
        Mob asleep = Npc(Brain());
        PlayerBotWake.Wake(asleep, PlayerBotWakeKind.Target);
        Assert.That(PlayerBotWake.BrainState(asleep), Is.EqualTo("waking"));
        PlayerBotWake.Wake(asleep, PlayerBotWakeKind.Target);           // the same tick: still pending, not started again
        Mob awake = Npc(Brain());
        awake.Brain.ServiceObjectId.Value = 3;
        PlayerBotWake.Wake(awake, PlayerBotWakeKind.Aggro);
        PlayerBotWake.Wake(Npc(brain: null), PlayerBotWakeKind.Talk);
        PlayerBotWakeWindow w = PlayerBotWake.TakeWindow(60);
        Assert.That((w.Started, w.Target, w.Aggro, w.Talk), Is.EqualTo((1L, 2L, 1L, 1L)));
    }

    [Test]
    public void BrainState_tells_none_asleep_waking_and_awake()
    {
        Assert.That(PlayerBotWake.BrainState(null), Is.EqualTo("none"));
        Assert.That(PlayerBotWake.BrainState(Npc(brain: null)), Is.EqualTo("none"));
        Mob npc = Npc(Brain());
        Assert.That(PlayerBotWake.BrainState(npc), Is.EqualTo("asleep"));
        npc.Brain.ServiceObjectId.OnPreAdd();
        Assert.That(PlayerBotWake.BrainState(npc), Is.EqualTo("waking"));
        npc.Brain.ServiceObjectId.Value = 3;                            // what the store's update sets (public setter)
        Assert.That(PlayerBotWake.BrainState(npc), Is.EqualTo("awake"));
    }

    // --- the 3,600-unit scan ---

    [Test]
    public void The_scan_wakes_an_aggressive_monster_by_the_gamebot_rule() =>
        Assert.That(PlayerBotWake.ScanReason(PlayerBot(), Npc(Brain())), Is.EqualTo(PlayerBotWakeKind.Aggro));

    [TestCase("dead")] [TestCase("inactive")] [TestCase("deleted")] [TestCase("aggro level 0")] [TestCase("aggro range 0")]
    [TestCase("outside its aggro range")] [TestCase("cannot aggro the bot")]
    public void The_scan_refuses_what_the_gamebot_rule_refuses(string refusal)
    {
        Bot bot = PlayerBot();
        TestBrain brain = Brain(range: 500);
        Mob npc = Npc(brain, x: 400);
        switch (refusal)
        {
            case "dead": npc.Alive = false; break;
            case "inactive": npc.ObjectState = GameObject.eObjectState.Inactive; break;
            case "deleted": npc.ObjectState = GameObject.eObjectState.Deleted; break;
            case "aggro level 0": brain.Level = 0; break;
            case "aggro range 0": brain.Range = 0; npc.PositionX = 0; break;   // on the bot: only the range rule refuses
            case "outside its aggro range": npc.PositionX = 501; break;
            case "cannot aggro the bot": brain.CanAggro = false; break;
        }
        Assert.That(PlayerBotWake.ScanReason(bot, npc), Is.Null);
    }

    [Test]
    public void The_scan_refuses_taxis_and_training_dummies()
    {
        Assert.That(PlayerBotWake.ScanReason(PlayerBot(), WithBrain(Make<Taxi>(), Brain(range: 3600))), Is.Null);
        Assert.That(PlayerBotWake.ScanReason(PlayerBot(), WithBrain(Make<Dummy>(), Brain(range: 3600))), Is.Null);
    }

    [Test]
    public void The_scan_wakes_what_targets_the_bot_whatever_its_brain()
    {
        Bot bot = PlayerBot();
        Mob npc = Npc(new BlankBrain());
        Assert.That(PlayerBotWake.ScanReason(bot, npc), Is.Null);
        npc.TargetObject = bot;
        Assert.That(PlayerBotWake.ScanReason(bot, npc), Is.EqualTo(PlayerBotWakeKind.TargetsBot));
    }

    [Test]
    public void The_scan_wakes_a_passive_monster_only_when_it_holds_aggro()
    {
        Bot bot = PlayerBot();
        Assert.That(PlayerBotWake.ScanReason(bot, Npc(Brain(level: 0))), Is.Null, "a quest giver stays asleep");
        Assert.That(PlayerBotWake.ScanReason(bot, Npc(Brain(level: 0, held: true))), Is.EqualTo(PlayerBotWakeKind.HeldAggro));
        Assert.That(PlayerBotWake.ScanReason(bot, Npc(brain: null)), Is.Null);
    }

    [TestCase((ushort)0, 1_250L)] [TestCase((ushort)499, 1_749L)] [TestCase((ushort)500, 1_250L)] [TestCase((ushort)12345, 1_595L)]
    public void The_scan_comes_every_1250_ms_plus_the_gamebots_spread(ushort objectId, long after) =>
        Assert.That(PlayerBotWake.NextScanTick(PlayerBot(objectId), Now), Is.EqualTo(Now + after));   // BotBrain.cs:1568

    // --- every think ---

    [Test]
    public void Every_think_collects_the_target_the_client_target_and_the_attackers()
    {
        Bot bot = PlayerBot();
        Mob target = Npc(), clicked = Npc(), first = Npc(), second = Npc();
        bot.TargetObject = clicked;
        var into = new List<(GameNPC, PlayerBotWakeKind)>();
        PlayerBotWake.CollectAlways(bot, target, [first, bot, second], into);
        Assert.That(into, Is.EqualTo(new List<(GameNPC, PlayerBotWakeKind)>
        {
            (target, PlayerBotWakeKind.Target), (clicked, PlayerBotWakeKind.Target),
            (first, PlayerBotWakeKind.Attacker), (second, PlayerBotWakeKind.Attacker),
        }));
    }

    [Test]
    public void Every_think_collects_an_npc_once()
    {
        Bot bot = PlayerBot();
        Mob target = Npc(), other = Npc();
        bot.TargetObject = target;
        var into = new List<(GameNPC, PlayerBotWakeKind)>();
        PlayerBotWake.CollectAlways(bot, target, [target, other, other], into);
        Assert.That(into, Is.EqualTo(new List<(GameNPC, PlayerBotWakeKind)>
            { (target, PlayerBotWakeKind.Target), (other, PlayerBotWakeKind.Attacker) }));
    }

    [TestCase("dead")] [TestCase("deleted")] [TestCase("another region")] [TestCase("beyond 6,000")]
    public void Every_think_skips_a_target_that_is_gone_or_out_of_sight(string why)
    {
        Bot bot = PlayerBot();
        Mob target = Npc(x: WorldMgr.VISIBILITY_DISTANCE), clicked = Npc(x: WorldMgr.VISIBILITY_DISTANCE);
        bot.TargetObject = clicked;
        var into = new List<(GameNPC, PlayerBotWakeKind)>();
        PlayerBotWake.CollectAlways(bot, target, null, into);
        Assert.That(into, Has.Count.EqualTo(2), "at 6,000 exactly both are in sight");
        foreach (Mob npc in new[] { target, clicked })
        {
            switch (why)
            {
                case "dead": npc.Alive = false; break;
                case "deleted": npc.ObjectState = GameObject.eObjectState.Deleted; break;
                case "another region": npc.Region = 2; break;
                case "beyond 6,000": npc.PositionX = WorldMgr.VISIBILITY_DISTANCE + 1; break;
            }
        }
        into.Clear();
        PlayerBotWake.CollectAlways(bot, target, null, into);
        Assert.That(into, Is.Empty);
    }

    [TestCase("dead")] [TestCase("deleted")]
    public void Every_think_skips_an_attacker_that_is_gone(string why)
    {
        Mob attacker = Npc();
        if (why == "dead") attacker.Alive = false;
        else attacker.ObjectState = GameObject.eObjectState.Deleted;
        var into = new List<(GameNPC, PlayerBotWakeKind)>();
        PlayerBotWake.CollectAlways(PlayerBot(), null, [attacker], into);
        Assert.That(into, Is.Empty);
    }

    [TestCase("lever off")] [TestCase("dead")] [TestCase("not active")] [TestCase("stealthed")]
    public void A_think_wakes_nothing_with_the_lever_off_or_a_bot_a_gamebot_would_skip(string why)
    {
        PlayerBotWake.Lever = why != "lever off";
        Bot bot = PlayerBot();
        switch (why)
        {
            case "dead": bot.Alive = false; break;
            case "not active": bot.ObjectState = GameObject.eObjectState.Inactive; break;
            case "stealthed": bot.Stealthed = true; break;
        }
        Mob target = Npc();
        PlayerBotState state = State(target, nextScan: 0);
        PlayerBotWake.OnThink(state, bot, Now);
        Assert.That((target.Wakes, state.NextWakeScanTick), Is.EqualTo((0, 0L)));
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, PlayerBotWake.Lever, 0, 0, 0, 0, 0, 0, 0, 0)));
    }

    [Test]
    public void A_think_under_the_lever_wakes_the_target_and_scans_only_when_due()
    {
        PlayerBotWake.Lever = true;
        Bot bot = PlayerBot();
        Mob target = Npc(Brain(level: 0));
        PlayerBotState state = State(target, nextScan: Now + 1);
        PlayerBotWake.OnThink(state, bot, Now);
        Assert.That((target.Wakes, state.NextWakeScanTick, PlayerBotWake.BrainState(target)), Is.EqualTo((1, Now + 1, "waking")));
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 1, 0, 0, 0, 1, 0, 0)));
    }

    // --- the NPC talked to ---

    [Test]
    public void Talking_wakes_the_npc_only_under_the_lever_and_only_for_a_player_bot()
    {
        Mob npc = Npc();
        PlayerBotWake.Interacting(PlayerBot(), npc);
        PlayerBotWake.Lever = true;
        PlayerBotWake.Interacting(Human(), npc);
        PlayerBotWake.Interacting(PlayerBot(), null);
        Assert.That(npc.Wakes, Is.EqualTo(0));
        PlayerBotWake.Interacting(PlayerBot(), npc);
        Assert.That((npc.Wakes, npc.IsVisibleToPlayers, PlayerBotWake.TakeWindow(60).Talk), Is.EqualTo((1, false, 1L)));
    }

    // --- the report line ---

    [Test]
    public void The_report_line_names_every_counter()
    {
        Assert.That(PlayerBotWake.Format(new PlayerBotWakeWindow(60, true, 15600, 212, 31, 2, 4, 2900, 140, 9)), Is.EqualTo(
            "PLAYER_BOT_WAKE win=60 lever=gamebot scans=15600 started=212 aggro=31 targetsBot=2 heldAggro=4 target=2900 " +
            "attacker=140 talk=9"));
        Assert.That(PlayerBotWake.Format(new PlayerBotWakeWindow { Seconds = 60 }), Is.EqualTo(
            "PLAYER_BOT_WAKE win=60 lever=player scans=0 started=0 aggro=0 targetsBot=0 heldAggro=0 target=0 attacker=0 talk=0"));
    }

    [Test]
    public void Taking_a_window_zeroes_the_counters()
    {
        PlayerBotWake.Lever = true;
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.HeldAggro);
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.TargetsBot);
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.Attacker);
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 0, 0, 1, 1, 0, 1, 0)));
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 0, 0, 0, 0, 0, 0, 0)));
    }
}
