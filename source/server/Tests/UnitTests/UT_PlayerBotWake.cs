using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.HearthDAoC;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.Quests;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC player bots (#137 lever): HEARTHDAOC_PLAYER_BOT_WAKE=gamebot. ClientService's marked gate keeps a player bot's
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
        public string Called;
        public override string Name { get => Called; set { } }
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
        private ConcurrentDictionary<AbstractQuest, byte> _journal;
        public override ConcurrentDictionary<AbstractQuest, byte> QuestList => _journal ??= new();   // an empty journal
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

    private sealed class TestPetBrain : ControlledMobBrain
    {
        private TestPetBrain() : base(null) { }                         // never run
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

    // The bot's own pet: a controlled brain in the bot's slot (GameLiving.cs:3798, :3819-3831) on an NPC by the bot.
    private static Mob PetOf(Bot bot, int x = 300)
    {
        TestPetBrain brain = Make<TestPetBrain>();
        typeof(ABrain).GetField("<ServiceObjectId>k__BackingField", Hidden)!.SetValue(brain, new ServiceObjectId(ServiceObjectType.Brain));
        Mob pet = Npc(brain, x);
        brain.Body = pet;
        typeof(GameLiving).GetField("m_controlledBrain", Hidden)!.SetValue(bot, new IControlledBrain[] { brain });
        return pet;
    }

    private static PlayerBotState State(GameLiving target, long nextScan)
    {
        PlayerBotState state = Make<PlayerBotState>();
        state.Target = target;
        state.NextWakeScanTick = nextScan;
        return state;
    }

    // --- the setting ---

    // Unset or empty is the lever: player bots wake what GameBots wake (the #137 measurements: 0.44x vs 1.10x the cost).
    [TestCase(null, true)] [TestCase("", true)] [TestCase("  ", true)] [TestCase(" player ", false)] [TestCase("PLAYER", false)]
    [TestCase("gamebot", true)] [TestCase(" GameBot ", true)]
    public void Parse_reads_player_or_gamebot_and_defaults_to_gamebot(string text, bool lever) => Assert.That(PlayerBotWake.Parse(text), Is.EqualTo(lever));

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
        clicked.TargetObject = bot;                                     // not the driver's, but it fights the bot
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

    // The driver lets a target go (unreachable, no longer fair game, a task takes over) without clearing the client
    // target, and a killed monster respawns as the same object: that NPC is no longer the bot's business.
    [Test]
    public void Every_think_leaves_a_client_target_the_driver_let_go()
    {
        Bot bot = PlayerBot();
        Mob dropped = Npc();
        bot.TargetObject = dropped;
        var into = new List<(GameNPC, PlayerBotWakeKind)>();
        PlayerBotWake.CollectAlways(bot, null, null, into);
        Assert.That(into, Is.Empty);
        dropped.TargetObject = bot;                                     // until it comes for the bot
        PlayerBotWake.CollectAlways(bot, null, null, into);
        Assert.That(into, Is.EqualTo(new List<(GameNPC, PlayerBotWakeKind)> { (dropped, PlayerBotWakeKind.Target) }));
    }

    [TestCase("dead")] [TestCase("deleted")] [TestCase("another region")] [TestCase("beyond 6,000")]
    public void Every_think_skips_a_target_that_is_gone_or_out_of_sight(string why)
    {
        Bot bot = PlayerBot();
        Mob target = Npc(x: WorldMgr.VISIBILITY_DISTANCE), clicked = Npc(x: WorldMgr.VISIBILITY_DISTANCE);
        (bot.TargetObject, clicked.TargetObject) = (clicked, bot);
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

    // A human's client update stamps its pet (ClientService.cs:679); under the lever nothing stamps a player bot's, and
    // a player's pet is no GameBot's (AutonomousSummonActivity.AutonomousOwner), so NpcService would stop its brain
    // (NpcService.cs:66, ABrain.IsActive). The driver wakes it, while the bot could see it, after everything else.
    [Test]
    public void Every_think_collects_the_bots_own_pet_last_while_in_sight()
    {
        Bot bot = PlayerBot();
        Mob target = Npc(), attacker = Npc(), pet = PetOf(bot);
        var into = new List<(GameNPC, PlayerBotWakeKind)>();
        PlayerBotWake.CollectAlways(bot, target, [attacker], into);
        Assert.That(into, Is.EqualTo(new List<(GameNPC, PlayerBotWakeKind)>
            { (target, PlayerBotWakeKind.Target), (attacker, PlayerBotWakeKind.Attacker), (pet, PlayerBotWakeKind.Pet) }));
        pet.PositionX = WorldMgr.VISIBILITY_DISTANCE + 1;
        into.Clear();
        PlayerBotWake.CollectAlways(bot, null, null, into);
        Assert.That(into, Is.Empty, "out of sight");
        (pet.PositionX, pet.Alive) = (300, false);
        PlayerBotWake.CollectAlways(bot, null, null, into);
        Assert.That(into, Is.Empty, "dead");
    }

    [Test]
    public void A_think_under_the_lever_keeps_the_bots_own_pets_brain_running()
    {
        PlayerBotWake.Lever = true;
        Bot bot = PlayerBot();
        Mob pet = PetOf(bot);
        Assert.That(pet.Brain.IsActive, Is.False, "no client stamp, no GameBot owner");
        PlayerBotWake.OnThink(State(null, nextScan: Now + 1), bot, Now);
        Assert.That((pet.Wakes, pet.Brain.IsActive, pet.IsVisibleToPlayers, PlayerBotWake.BrainState(pet)),
            Is.EqualTo((1, true, false, "waking")));
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 1, 0, 0, 0, 0, 0, 0, 1)));
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
    public void A_think_under_the_lever_wakes_the_target_before_the_scan_is_due()
    {
        PlayerBotWake.Lever = true;
        Bot bot = PlayerBot();
        Mob target = Npc(Brain(level: 0));
        PlayerBotState state = State(target, nextScan: Now + 1);
        PlayerBotWake.OnThink(state, bot, Now);
        Assert.That((target.Wakes, state.NextWakeScanTick, PlayerBotWake.BrainState(target)), Is.EqualTo((1, Now + 1, "waking")));
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 1, 0, 0, 0, 1, 0, 0)));
    }

    // The GameBot cadence, not one scan per 250 ms think. The double has no region, so the scan finds no NPC
    // (GameObject.cs:939-940): only the bookkeeping is under test.
    [Test]
    public void A_due_scan_runs_once_then_waits_for_the_gamebot_cadence()
    {
        PlayerBotWake.Lever = true;
        Bot bot = PlayerBot(objectId: 7);
        PlayerBotState state = State(null, nextScan: Now);
        PlayerBotWake.OnThink(state, bot, Now);
        PlayerBotWake.OnThink(state, bot, Now + 250);                     // the next think
        Assert.That((state.NextWakeScanTick, PlayerBotWake.TakeWindow(60).Scans), Is.EqualTo((Now + 1_257, 1L)));   // 1,250 + 7 % 500
        PlayerBotWake.OnThink(state, bot, Now + 1_257);
        Assert.That((state.NextWakeScanTick, PlayerBotWake.TakeWindow(60).Scans), Is.EqualTo((Now + 2 * 1_257, 1L)));
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
        Assert.That(PlayerBotWake.Format(new PlayerBotWakeWindow(60, true, 15600, 212, 31, 2, 4, 2900, 140, 9, 960, 3)), Is.EqualTo(
            "PLAYER_BOT_WAKE win=60 lever=gamebot scans=15600 started=212 aggro=31 targetsBot=2 heldAggro=4 target=2900 " +
            "attacker=140 talk=9 pet=960 quest=3"));
        Assert.That(PlayerBotWake.Format(new PlayerBotWakeWindow { Seconds = 60 }), Is.EqualTo(
            "PLAYER_BOT_WAKE win=60 lever=player scans=0 started=0 aggro=0 targetsBot=0 heldAggro=0 target=0 attacker=0 talk=0 " +
            "pet=0 quest=0"));
    }

    [Test]
    public void Taking_a_window_zeroes_the_counters()
    {
        PlayerBotWake.Lever = true;
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.HeldAggro);
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.TargetsBot);
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.Attacker);
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.Pet);
        PlayerBotWake.Wake(Npc(), PlayerBotWakeKind.Quest);
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1)));
        Assert.That(PlayerBotWake.TakeWindow(60), Is.EqualTo(new PlayerBotWakeWindow(60, true, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));
    }

    // --- the quest rule: the NPCs the bot's current quests name, within 6,000 (the owner's rule, 2026-10-22) ---

    private static Mob Named(string name, int x = 400, ushort region = 1)
    {
        Mob npc = Npc(Brain(level: 0), x, region);
        npc.Called = name;
        return npc;
    }

    private sealed class Quest : AbstractQuest
    {
        public int At;
        public override int Step { get => At; set { } }
        public override bool CheckQuestQualification(GamePlayer player) => true;
        public override void Notify(DOLEvent e, object sender, EventArgs args) { }
    }

    // The DataQuest getters log an error at every read of a malformed step, and a scan comes every 1.25-1.75 s: a bot
    // reads its quest names again only when its journal's quests or steps change (PlayerBotState.QuestJournal).
    [Test]
    public void The_journal_key_changes_with_a_quest_taken_its_next_step_and_its_end()
    {
        Bot bot = PlayerBot();
        long empty = PlayerBotWake.JournalKey(bot);
        Assert.That(new PlayerBotState(bot).QuestJournal, Is.Not.EqualTo(empty), "the first scan reads the journal");
        Assert.That(PlayerBotWake.JournalKey(bot), Is.EqualTo(empty));
        Quest quest = Make<Quest>();
        quest.At = 1;
        bot.QuestList[quest] = 0;
        long taken = PlayerBotWake.JournalKey(bot);
        Assert.That(taken, Is.Not.EqualTo(empty));
        quest.At = 2;
        Assert.That(PlayerBotWake.JournalKey(bot), Is.Not.EqualTo(taken));
        bot.QuestList.TryRemove(quest, out _);
        Assert.That(PlayerBotWake.JournalKey(bot), Is.EqualTo(empty));
    }

    // As the quest markers match a step (QuestIndicators.Targets): the name without case, region 0 or the NPC's.
    [Test]
    public void A_step_names_an_npc_by_name_without_case_in_its_region_or_any()
    {
        var steps = new List<JournalStep> { new(DataQuest.eStepType.Interact, "Olorustos", 1) };
        Assert.That(PlayerBotWake.Named(steps, Named("olorustos")), Is.True);
        Assert.That(PlayerBotWake.Named(steps, Named("Olorustos", region: 2)), Is.False);
        Assert.That(PlayerBotWake.Named(steps, Named("Guardian Aron")), Is.False);
        Assert.That(PlayerBotWake.Named(new List<JournalStep> { new(DataQuest.eStepType.Kill, "Olorustos", 0) }, Named("Olorustos", region: 2)), Is.True);
        Assert.That(PlayerBotWake.Named(new List<JournalStep>(), Named("Olorustos")), Is.False);
    }

    [Test]
    public void The_quest_rule_wakes_named_npcs_and_givers_within_6000_and_nothing_else()
    {
        PlayerBotWake.Lever = true;
        Bot bot = PlayerBot();
        Mob olorustos = Named("Olorustos", x: 5_900), stranger = Named("Guardian Aron", x: 300), giver = Named("Master Lorik", x: 5_900),
            farGiver = Named("Captain Falron", x: 6_100), deadNamed = Named("Olorustos", x: 200);
        deadNamed.Alive = false;
        var steps = new List<JournalStep> { new(DataQuest.eStepType.Interact, "Olorustos", 1) };
        var into = new List<(GameNPC Npc, PlayerBotWakeKind Kind)>();
        PlayerBotWake.CollectQuest(bot, steps, new List<GameNPC> { giver, farGiver }, new GameNPC[] { olorustos, stranger, deadNamed }, into);
        Assert.That(into, Is.EquivalentTo(new (GameNPC, PlayerBotWakeKind)[] { (giver, PlayerBotWakeKind.Quest), (olorustos, PlayerBotWakeKind.Quest) }));
    }

    // The names a classic quest's current step adds (classic-quests.json, hearthdaoc-quests.json): the issuer, the
    // grant's giver, the custom step's target and the monsters that drop for it.
    [Test]
    public void A_classic_step_adds_its_issuer_grant_giver_target_and_drop_monsters()
    {
        var config = new ClassicQuests.Config();
        config.Quests[20206] = new ClassicQuests.QuestInfo
        {
            Steps = new()
            {
                null,
                new ClassicQuests.StepInfo { Issuer = "Master Lorik" },
                new ClassicQuests.StepInfo
                {
                    Issuer = "Olorustos", GrantFrom = "Guardian Aron",
                    Custom = new ClassicQuests.CustomStep { Kind = "trade", Target = "Elvar", Drops = new() { new() { Mob = "bandit" }, new() { Mob = "" } } },
                },
            },
        };
        Assert.That(ClassicQuests.StepNames(config, 20206, 2), Is.EqualTo(new[] { "Olorustos", "Guardian Aron", "Elvar", "bandit" }));
        Assert.That(ClassicQuests.StepNames(config, 20206, 1), Is.EqualTo(new[] { "Master Lorik" }));
        Assert.That(ClassicQuests.StepNames(config, 20206, 3), Is.Empty);
        Assert.That(ClassicQuests.StepNames(config, 20206, 0), Is.Empty);
        Assert.That(ClassicQuests.StepNames(config, 1, 1), Is.Empty);
    }
}
