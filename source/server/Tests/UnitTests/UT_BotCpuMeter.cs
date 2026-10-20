using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.HearthDAoC;
using DOL.GS.Keeps;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.1). While it is on,
// each game-loop callback is charged to the kind of its owner and to its stage. These tests pin the kinds and stages
// in their report order (2.5, 2.3), the stage of each profile key (3.4), the kind of each owner, pets included (3.3),
// and what each item charges: its kind, its stage, the bot whose counter it adds to and its presence (3.3, 3.8), with
// game objects built without their constructors and their fields set by reflection. Then the real hook H1 (3.1, 3.2,
// 3.7, 3.10, 3.11): GameLoop's pool is a recording pool, and while the meter is on each item runs once and is charged
// to its thread's slot and its bot's counter; off, the pool gets the caller's own delegate. Then hook H6's fleet scope
// (3.5, 5.1): the GameBot fleet planning's own work and its nested call's items go to gamebot / Fleet, counted once.
[TestFixture, NonParallelizable]
public sealed class UT_BotCpuMeter
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    // GameObject's type initializer reads the database (FillDataQuestCache): a test double server with an empty
    // database, as UT_RpTimerWaitScript does, before any game object is made.
    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    public class EmptyReads : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            Type result = method.ReturnType;
            if (result.IsGenericType && typeof(IEnumerable).IsAssignableFrom(result))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GetGenericArguments()[0]));
            return null;
        }
    }

    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();

    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly FieldInfo ThreadPoolField = typeof(GameLoop).GetField("_threadPool", HiddenStatic)!;
    private static readonly FieldInfo StageHookField = typeof(BotCpuMeter).GetField("_stageHook", HiddenStatic)!;
    private static readonly FieldInfo WarnHookField = typeof(BotCpuMeter).GetField("_warnHook", HiddenStatic)!;
    private static readonly FieldInfo SlotsField = typeof(BotCpuMeter).GetField("_slots", HiddenStatic)!;
    private static readonly FieldInfo ThreadSlotField = typeof(BotCpuMeter).GetField("_threadSlot", HiddenStatic)!;
    private static readonly FieldInfo InFleetField = typeof(BotCpuMeter).GetField("_inFleet", HiddenStatic)!;

    private GameServer _savedServer;
    private GameLoopThreadPool _savedPool;
    private string _savedActiveService;
    private RecordingPool _pool;

    [SetUp]
    public void SaveGlobals()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        // Hook H1 hands each call to GameLoop's private pool (GL:18): a recording pool here, and the stage is
        // GameLoop.ActiveService, which each test sets (6.1).
        _savedPool = (GameLoopThreadPool)ThreadPoolField.GetValue(null);
        _savedActiveService = GameLoop.ActiveService;
        _pool = new RecordingPool();
        ThreadPoolField.SetValue(null, _pool);
    }

    // A failing test never leaves the meter on, a fleet scope open or the setup's test hook set, for the next (6.1).
    [TearDown]
    public void RestoreGlobals()
    {
        BotCpuMeter.Disable();
        InFleetField.SetValue(null, false);
        StageHookField.SetValue(null, null);
        WarnHookField.SetValue(null, null);
        ThreadPoolField.SetValue(null, _savedPool);
        GameLoop.ActiveService = _savedActiveService;
        GameServer.LoadTestDouble(_savedServer);
    }

    // A client of the prototype's first kind: derived from BotDummyClient (2.5).
    private sealed class TestDummyClient : BotDummyClient { }

    // A client of the contract's kind: a plain GameClient marked with IPlayerBotClient (2.5).
    private sealed class TestPlayerBotClient : GameClient, IPlayerBotClient
    {
        public TestPlayerBotClient() : base((Socket)null) { }
    }

    private static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Set(Type type, object target, string name, object value) =>
        type.GetField(name, Hidden)!.SetValue(target, value);

    private static GameNPC Npc(ABrain brain)
    {
        GameNPC npc = Make<GameNPC>();
        Set(typeof(GameNPC), npc, "m_brains", new ArrayList());
        Set(typeof(GameNPC), npc, "m_ownBrain", brain);
        return npc;
    }

    private static ControlledMobBrain PetBrain(GameLiving owner)
    {
        ControlledMobBrain brain = Make<ControlledMobBrain>();
        Set(typeof(ControlledMobBrain), brain, "m_owner", owner);
        return brain;
    }

    private static GameNPC Pet(GameLiving owner) => Npc(PetBrain(owner));

    private static GamePlayer Player(GameClient client)
    {
        GamePlayer player = Make<GamePlayer>();
        Set(typeof(GamePlayer), player, "m_client", client);
        return player;
    }

    // A GameBot with its BotBrain (itself an IControlledBrain, bots/BotBrain.cs:21) and, for a companion, the player
    // it serves: the walk must stop at the bot, not go on to that player.
    private static GameBot Bot(bool autonomous, GamePlayer owner = null)
    {
        GameBot bot = Make<GameBot>();
        Set(typeof(GameNPC), bot, "m_brains", new ArrayList());
        Set(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", autonomous);
        Set(typeof(GameBot), bot, "<Owner>k__BackingField", owner);
        Set(typeof(GameNPC), bot, "m_ownBrain", new BotBrain { Body = bot });
        return bot;
    }

    private static bool NotPiloted(GamePlayer player) => false;

    private static bool Piloted(GamePlayer player) => true;

    private static void AssertKind(GameObject owner, BotCpuKind kind, GameObject charged, bool pet,
        bool piloted = false)
    {
        BotCpuKind found = BotCpuMeter.KindOf(owner, piloted ? Piloted : NotPiloted, out GameObject foundCharged,
            out bool foundPet);
        Assert.That(found, Is.EqualTo(kind));
        Assert.That(foundCharged, Is.SameAs(charged));
        Assert.That(foundPet, Is.EqualTo(pet));
    }

    [Test]
    public void TheKindsAreInTheReportOrderAndTheirNamesAreLowerCase()
    {
        Assert.That(Enum.GetNames<BotCpuKind>().Select(name => name.ToLowerInvariant()),
            Is.EqualTo(new[] { "gamebot", "companion", "playerbot", "piloted", "player", "monster", "unattributed" }));
        Assert.That(Enum.GetValues<BotCpuKind>().Select(kind => (int)kind), Is.EqualTo(Enumerable.Range(0, 7)));
    }

    [Test]
    public void TheStagesAreTheCsvStageColumnsInOrder()
    {
        // The stage columns of the CSV header (2.3).
        string[] columns = ("GameLoop,Timer,ClientBegin,Npc,Fleet,Attack,Casting,Effect,EffectListBegin," +
            "EffectListEnd,Movement,Crafting,Reaper,Zone,ClientEnd,Quests,Pets,Other").Split(',');
        Assert.That(Enum.GetNames<BotCpuStage>(), Is.EqualTo(columns));
        Assert.That(Enum.GetValues<BotCpuStage>().Select(stage => (int)stage), Is.EqualTo(Enumerable.Range(0, 18)));
    }

    // The profile key is the service's class name (GameServiceBase.ServiceName), plus .BeginTick or .EndTick for those
    // steps (GameLoop.BuildTickSequence).
    [TestCase(nameof(GameLoopService), BotCpuStage.GameLoop)]
    [TestCase(nameof(TimerService), BotCpuStage.Timer)]
    [TestCase(nameof(ClientService) + "." + nameof(ClientService.BeginTick), BotCpuStage.ClientBegin)]
    [TestCase(nameof(NpcService), BotCpuStage.Npc)]
    [TestCase(nameof(AttackService), BotCpuStage.Attack)]
    [TestCase(nameof(CastingService), BotCpuStage.Casting)]
    [TestCase(nameof(EffectService), BotCpuStage.Effect)]
    [TestCase(nameof(EffectListService) + "." + nameof(EffectListService.BeginTick), BotCpuStage.EffectListBegin)]
    [TestCase(nameof(EffectListService) + "." + nameof(EffectListService.EndTick), BotCpuStage.EffectListEnd)]
    [TestCase(nameof(MovementService), BotCpuStage.Movement)]
    [TestCase(nameof(CraftingService), BotCpuStage.Crafting)]
    [TestCase(nameof(ReaperService), BotCpuStage.Reaper)]
    [TestCase(nameof(ZoneService), BotCpuStage.Zone)]
    [TestCase(nameof(ClientService) + "." + nameof(ClientService.EndTick), BotCpuStage.ClientEnd)]
    [TestCase(nameof(DailyQuestService), BotCpuStage.Quests)]
    [TestCase(nameof(WeeklyQuestService), BotCpuStage.Quests)]
    [TestCase(nameof(MonthlyQuestService), BotCpuStage.Quests)]
    public void EachProfileKeyHasItsStage(string profileKey, BotCpuStage stage) =>
        Assert.That(BotCpuMeter.StageOf(profileKey), Is.EqualTo(stage));

    [TestCase("SomeNewService")]
    [TestCase("npcservice")]
    [TestCase("")]
    [TestCase(null)]
    public void AnUnknownMissingOrEmptyProfileKeyIsOther(string profileKey) =>
        Assert.That(BotCpuMeter.StageOf(profileKey), Is.EqualTo(BotCpuStage.Other));

    [Test]
    public void TheMeterHasNoStaticInitializer() =>
        Assert.That(typeof(BotCpuMeter).TypeInitializer, Is.Null, "nothing in BotCpuMeter may run while off (5.1)");

    [TestCase(true, BotCpuKind.Gamebot)]
    [TestCase(false, BotCpuKind.Companion)]
    public void AGameBotIsGamebotWhenAutonomousElseCompanion(bool autonomous, BotCpuKind kind)
    {
        GameBot bot = Bot(autonomous, autonomous ? null : Player(Make<GameClient>()));
        AssertKind(bot, kind, bot, pet: false);
    }

    [TestCase(typeof(TestDummyClient), false)]
    [TestCase(typeof(TestDummyClient), true)]
    [TestCase(typeof(TestPlayerBotClient), false)]
    [TestCase(typeof(TestPlayerBotClient), true)]
    public void APlayerWithABotClientIsPlayerbot(Type client, bool piloted)
    {
        GamePlayer player = Player((GameClient)RuntimeHelpers.GetUninitializedObject(client));
        AssertKind(player, BotCpuKind.Playerbot, player, pet: false, piloted);
    }

    [TestCase(false, BotCpuKind.Player)]
    [TestCase(true, BotCpuKind.Piloted)]
    public void APlayerWithAPlainClientIsPilotedWhenThePilotCheckSaysSoElsePlayer(bool piloted, BotCpuKind kind)
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertKind(player, kind, player, pet: false, piloted);
    }

    [Test]
    public void ThePilotCheckIsAskedAboutThatPlayer()
    {
        GamePlayer player = Player(Make<GameClient>());
        GamePlayer asked = null;
        BotCpuMeter.KindOf(player, p => { asked = p; return false; }, out _, out _);
        Assert.That(asked, Is.SameAs(player));
    }

    [Test]
    public void TheRealPilotCheckIsAutonomousPlayerPilot()
    {
        GamePlayer player = Player(Make<GameClient>());
        Assert.That(BotCpuMeter.KindOf(player, out GameObject charged, out bool pet), Is.EqualTo(BotCpuKind.Player));
        Assert.That(charged, Is.SameAs(player));
        Assert.That(pet, Is.False);

        // A pilot as AutonomousPlayerPilot.Enable registers one: an entry in its Pilots table, which IsActive reads
        // (bots/autonomous/AutonomousPlayerPilot.cs:32, :89).
        IDictionary pilots = (IDictionary)typeof(AutonomousPlayerPilot)
            .GetField("Pilots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        pilots[player] = RuntimeHelpers.GetUninitializedObject(
            typeof(AutonomousPlayerPilot).GetNestedType("PilotState", BindingFlags.NonPublic)!);
        try
        {
            Assert.That(BotCpuMeter.KindOf(player, out charged, out pet), Is.EqualTo(BotCpuKind.Piloted));
            Assert.That(charged, Is.SameAs(player));
        }
        finally
        {
            pilots.Remove(player);
        }
    }

    [Test]
    public void TheRealPilotCheckAllocatesNothing()
    {
        GamePlayer player = Player(Make<GameClient>());
        BotCpuMeter.KindOf(player, out _, out _); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuMeter.KindOf(player, out _, out _);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void AnyOtherLivingIsMonster()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        AssertKind(monster, BotCpuKind.Monster, monster, pet: false);
        GameNPC brainless = Npc(null);
        AssertKind(brainless, BotCpuKind.Monster, brainless, pet: false);
        GameKeepDoor door = Make<GameKeepDoor>();
        AssertKind(door, BotCpuKind.Monster, door, pet: false);
    }

    [Test]
    public void AControlledBrainWithNoOwnerIsMonster()
    {
        GameNPC npc = Pet(null);
        AssertKind(npc, BotCpuKind.Monster, npc, pet: false);
    }

    [Test]
    public void APetOfAPlayerIsPlayer()
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertKind(Pet(player), BotCpuKind.Player, player, pet: true);
        AssertKind(Pet(player), BotCpuKind.Piloted, player, pet: true, piloted: true);
    }

    [Test]
    public void APetOfAPetOfAnAutonomousGameBotIsGamebot()
    {
        GameBot bot = Bot(autonomous: true);
        AssertKind(Pet(Pet(bot)), BotCpuKind.Gamebot, bot, pet: true);
    }

    [Test]
    public void APetOfACompanionIsCompanionNotTheCompanionsPlayer()
    {
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        AssertKind(Pet(companion), BotCpuKind.Companion, companion, pet: true);
    }

    [Test]
    public void APetOfAMonsterIsMonster()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        AssertKind(Pet(monster), BotCpuKind.Monster, monster, pet: true);
    }

    [Test]
    public void TheWalkFollowsEightOwnersAndNoMore()
    {
        GamePlayer player = Player(Make<GameClient>());
        GameLiving owner = player;
        for (int i = 0; i < 8; i++)
            owner = Pet(owner);
        AssertKind(owner, BotCpuKind.Player, player, pet: true);
        AssertKind(Pet(owner), BotCpuKind.Unattributed, null, pet: false);
    }

    [Test]
    public void APetCycleIsUnattributed()
    {
        GameNPC a = Npc(null);
        GameNPC b = Pet(a);
        Set(typeof(GameNPC), a, "m_ownBrain", PetBrain(b));
        AssertKind(a, BotCpuKind.Unattributed, null, pet: false);
    }

    [Test]
    public void NoOwnerOrANonLivingOwnerIsUnattributed()
    {
        AssertKind(null, BotCpuKind.Unattributed, null, pet: false);
        AssertKind(Make<GameStaticItem>(), BotCpuKind.Unattributed, null, pet: false);
        Assert.That(BotCpuMeter.KindOf(null, out GameObject charged, out bool pet),
            Is.EqualTo(BotCpuKind.Unattributed));
        Assert.That(charged, Is.Null);
        Assert.That(pet, Is.False);
    }

    // What each item charges (3.3 item table, 3.8 presence): Classify reads the owner its item type names, then the
    // kind of that owner; a pet's work goes to Pets, and an item with no owner inside the fleet scope to gamebot /
    // Fleet.

    private const string OwnerField = "<Owner>k__BackingField";

    private static BotCpuCharge Charge<T>(T item, BotCpuStage stage, bool inFleet = false, bool piloted = false) =>
        BotCpuMeter.Classify(item, stage, inFleet, piloted ? Piloted : NotPiloted);

    // Classify<T> for an item type the test names at run time (one of a list).
    private static BotCpuCharge ChargeAs(Type itemType, object item, BotCpuStage stage) =>
        (BotCpuCharge)typeof(BotCpuMeter).GetMethod(nameof(BotCpuMeter.Classify))!.MakeGenericMethod(itemType)
            .Invoke(null, [item, stage, false, (Func<GamePlayer, bool>)NotPiloted])!;

    private static void AssertCharge(BotCpuCharge charge, BotCpuKind kind, BotCpuStage stage, GameObject bot,
        bool presence, string item = "")
    {
        Assert.That(charge.Kind, Is.EqualTo(kind), item + " kind");
        Assert.That(charge.Stage, Is.EqualTo(stage), item + " stage");
        Assert.That(charge.Bot, Is.SameAs(bot), item + " bot");
        Assert.That(charge.Presence, Is.EqualTo(presence), item + " presence");
    }

    // An item of type T, made as T or as a concrete type derived from it, with its owner member set.
    private static T Owned<T>(string member, object owner, Type made = null)
    {
        T item = (T)RuntimeHelpers.GetUninitializedObject(made ?? typeof(T));
        typeof(T).GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(item, owner);
        return item;
    }

    // The brain an NpcService batch holds for an NPC, its Body set as GameNPC.SetOwnBrain sets it.
    private static ABrain BrainOf(GameNPC npc)
    {
        ABrain brain = npc.Brain;
        brain.Body = npc;
        return brain;
    }

    private static LivingBeingKilled Kill(GameLiving killed, GameObject killer)
    {
        LivingBeingKilled kill = Owned<LivingBeingKilled>("<Killed>k__BackingField", killed);
        Set(typeof(LivingBeingKilled), kill, "<Killer>k__BackingField", killer);
        return kill;
    }

    // A client as ClientService holds it, with a player in the given object state, or none; the state and the player
    // are set without their setters' events.
    private static GameClient Client(GameClient.eClientState state, GameObject.eObjectState? playerState,
        Type made = null)
    {
        GameClient client = Owned<GameClient>("<ClientState>k__BackingField", state, made);
        if (playerState is { } objectState)
        {
            GamePlayer player = Player(client);
            player.ObjectState = objectState;
            Set(typeof(GameClient), client, "<Player>k__BackingField", player);
        }

        return client;
    }

    [Test]
    public void TheChargeIsAReadonlyStruct()
    {
        Assert.That(typeof(BotCpuCharge).IsValueType, Is.True);
        Assert.That(typeof(BotCpuCharge).IsDefined(typeof(IsReadOnlyAttribute), false), Is.True);
    }

    [TestCase(BotCpuKind.Gamebot, true)]
    [TestCase(BotCpuKind.Companion, true)]
    [TestCase(BotCpuKind.Playerbot, true)]
    [TestCase(BotCpuKind.Piloted, true)]
    [TestCase(BotCpuKind.Player, false)]
    [TestCase(BotCpuKind.Monster, false)]
    [TestCase(BotCpuKind.Unattributed, false)]
    public void TheBotKindsAreTheFourWithPerBotCounters(BotCpuKind kind, bool bot) =>
        Assert.That(BotCpuMeter.IsBotKind(kind), Is.EqualTo(bot));

    // GameBots' n comes from the registry, companions' from their live brains (3.8).
    [TestCase(true, BotCpuKind.Gamebot, false)]
    [TestCase(false, BotCpuKind.Companion, true)]
    public void AGameBotsBrainIsChargedToTheBotAndCountsPresenceOnlyForACompanion(bool autonomous, BotCpuKind kind,
        bool presence)
    {
        GameBot bot = Bot(autonomous, autonomous ? null : Player(Make<GameClient>()));
        AssertCharge(Charge(bot.Brain, BotCpuStage.Npc), kind, BotCpuStage.Npc, bot, presence);
    }

    [Test]
    public void AMonstersBrainIsMonsterAndCountsPresence()
    {
        AssertCharge(Charge(BrainOf(Npc(Make<StandardMobBrain>())), BotCpuStage.Npc), BotCpuKind.Monster,
            BotCpuStage.Npc, null, presence: true);
        // A controlled brain with no owner is no pet.
        AssertCharge(Charge(BrainOf(Pet(null)), BotCpuStage.Npc), BotCpuKind.Monster, BotCpuStage.Npc, null,
            presence: true);
    }

    [Test]
    public void APetsBrainIsItsOwnersKindUnderPetsWithTheOwnersCounterAndNoPresence()
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertCharge(Charge(BrainOf(Pet(player)), BotCpuStage.Npc), BotCpuKind.Player, BotCpuStage.Pets, null,
            presence: false);
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(BrainOf(Pet(Pet(bot))), BotCpuStage.Npc), BotCpuKind.Gamebot, BotCpuStage.Pets, bot,
            presence: false);
        GameBot companion = Bot(autonomous: false, player);
        AssertCharge(Charge(BrainOf(Pet(companion)), BotCpuStage.Npc), BotCpuKind.Companion, BotCpuStage.Pets,
            companion, presence: false);
    }

    [Test]
    public void APetsWorkInAnyStageIsPets()
    {
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(Owned<AttackComponent>("owner", Pet(bot)), BotCpuStage.Attack), BotCpuKind.Gamebot,
            BotCpuStage.Pets, bot, presence: false);
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, Pet(bot)), BotCpuStage.Timer), BotCpuKind.Gamebot,
            BotCpuStage.Pets, bot, presence: false);
    }

    [Test]
    public void APetCycleIsUnattributedInItsStage()
    {
        GameNPC a = Npc(null);
        GameNPC b = Pet(a);
        Set(typeof(GameNPC), a, "m_ownBrain", PetBrain(b));
        AssertCharge(Charge(BrainOf(a), BotCpuStage.Npc), BotCpuKind.Unattributed, BotCpuStage.Npc, null,
            presence: false);
    }

    [TestCase(typeof(TestDummyClient))]
    [TestCase(typeof(TestPlayerBotClient))]
    public void TheAttackComponentOfAPlayerWithABotClientIsPlayerbotWithItsCounter(Type client)
    {
        GamePlayer player = Player((GameClient)RuntimeHelpers.GetUninitializedObject(client));
        AssertCharge(Charge(Owned<AttackComponent>("owner", player), BotCpuStage.Attack), BotCpuKind.Playerbot,
            BotCpuStage.Attack, player, presence: false);
    }

    [TestCase(false, BotCpuKind.Player)]
    [TestCase(true, BotCpuKind.Piloted)]
    public void TheAttackComponentOfAPlayerWithAPlainClientIsPilotedWithItsCounterElsePlayer(bool piloted,
        BotCpuKind kind)
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertCharge(Charge(Owned<AttackComponent>("owner", player), BotCpuStage.Attack, piloted: piloted), kind,
            BotCpuStage.Attack, piloted ? player : null, presence: false);
    }

    private static void AssertGamebot<T>(T item, BotCpuStage stage, GameBot bot) =>
        AssertCharge(Charge(item, stage), BotCpuKind.Gamebot, stage, bot, presence: false, typeof(T).Name);

    [Test]
    public void EachItemTypeIsChargedToItsOwner()
    {
        GameBot bot = Bot(autonomous: true);
        GamePlayer crafter = Player((GameClient)RuntimeHelpers.GetUninitializedObject(typeof(TestDummyClient)));
        Assert.Multiple(() =>
        {
            AssertGamebot(Owned<AttackComponent>("owner", bot), BotCpuStage.Attack, bot);
            AssertGamebot(Owned<CastingComponent>(OwnerField, bot), BotCpuStage.Casting, bot);
            AssertGamebot(Owned<ECSGameEffect>("Owner", bot, typeof(ECSGameAbilityEffect)), BotCpuStage.Effect, bot);
            AssertGamebot(Owned<EffectListComponent>(OwnerField, bot), BotCpuStage.EffectListBegin, bot);
            AssertGamebot(Owned<EffectListComponent>(OwnerField, bot), BotCpuStage.EffectListEnd, bot);
            AssertGamebot(Owned<MovementComponent>(OwnerField, bot), BotCpuStage.Movement, bot);
            AssertGamebot(Owned<ECSGameTimer>(OwnerField, bot), BotCpuStage.Timer, bot);
            AssertGamebot(Owned<SubZoneObject>("<Node>k__BackingField", new LinkedListNode<GameObject>(bot)),
                BotCpuStage.Zone, bot);
            // A craft component's owner is a player.
            AssertCharge(Charge(Owned<CraftComponent>(OwnerField, crafter), BotCpuStage.Crafting),
                BotCpuKind.Playerbot, BotCpuStage.Crafting, crafter, presence: false, nameof(CraftComponent));
        });
    }

    [Test]
    public void AnItemTypeDerivedFromAKnownOneIsReadAsThatOne()
    {
        var brain = (StandardMobBrain)BrainOf(Npc(Make<StandardMobBrain>()));
        AssertCharge(Charge(brain, BotCpuStage.Npc), BotCpuKind.Monster, BotCpuStage.Npc, null, presence: true);
        GameBot bot = Bot(autonomous: true);
        var movement = (NpcMovementComponent)Owned<MovementComponent>(OwnerField, bot, typeof(NpcMovementComponent));
        AssertCharge(Charge(movement, BotCpuStage.Movement), BotCpuKind.Gamebot, BotCpuStage.Movement, bot,
            presence: false);
    }

    [Test]
    public void ATimerWithNoOwnerOrANonLivingOwnerIsUnattributedInTimer()
    {
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, null), BotCpuStage.Timer), BotCpuKind.Unattributed,
            BotCpuStage.Timer, null, presence: false);
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, Make<GameStaticItem>()), BotCpuStage.Timer),
            BotCpuKind.Unattributed, BotCpuStage.Timer, null, presence: false);
    }

    // XP, loot and quest credit run for a killer of a bot or player kind (3.3, the Reaper rule).
    [Test]
    public void AKillIsChargedToAKillerOfABotOrPlayerKind()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        GameBot bot = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        GamePlayer playerBot = Player((GameClient)RuntimeHelpers.GetUninitializedObject(typeof(TestPlayerBotClient)));
        AssertCharge(Charge(Kill(monster, bot), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Reaper, bot,
            presence: false);
        AssertCharge(Charge(Kill(monster, Pet(bot)), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Pets, bot,
            presence: false);
        AssertCharge(Charge(Kill(monster, companion), BotCpuStage.Reaper), BotCpuKind.Companion, BotCpuStage.Reaper,
            companion, presence: false);
        AssertCharge(Charge(Kill(monster, playerBot), BotCpuStage.Reaper), BotCpuKind.Playerbot, BotCpuStage.Reaper,
            playerBot, presence: false);
        AssertCharge(Charge(Kill(monster, player), BotCpuStage.Reaper, piloted: true), BotCpuKind.Piloted,
            BotCpuStage.Reaper, player, presence: false);
        AssertCharge(Charge(Kill(monster, player), BotCpuStage.Reaper), BotCpuKind.Player, BotCpuStage.Reaper, null,
            presence: false);
    }

    [Test]
    public void AKillByAMonsterByNothingOrByAnObjectIsChargedToTheKilled()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(Kill(bot, monster), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Reaper, bot,
            presence: false);
        AssertCharge(Charge(Kill(monster, null), BotCpuStage.Reaper), BotCpuKind.Monster, BotCpuStage.Reaper, null,
            presence: false);
        // A monster's pet is no bot or player kind either; the killed is no pet, so the stage stays Reaper.
        AssertCharge(Charge(Kill(bot, Pet(monster)), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Reaper,
            bot, presence: false);
        AssertCharge(Charge(Kill(bot, Make<GameStaticItem>()), BotCpuStage.Reaper), BotCpuKind.Gamebot,
            BotCpuStage.Reaper, bot, presence: false);
        AssertCharge(Charge(Kill(Player(Make<GameClient>()), monster), BotCpuStage.Reaper), BotCpuKind.Player,
            BotCpuStage.Reaper, null, presence: false);
    }

    // A client has an owner only while it IsPlaying (Playing or Linkdead) with an Active player (3.3); in
    // ClientService.BeginTick such a client counts presence (3.8).
    [TestCase(GameClient.eClientState.Playing, null, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.CharScreen, GameObject.eObjectState.Active, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.WorldEnter, GameObject.eObjectState.Active, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.Playing, GameObject.eObjectState.Inactive, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.Playing, GameObject.eObjectState.Active, BotCpuKind.Player, true)]
    [TestCase(GameClient.eClientState.Linkdead, GameObject.eObjectState.Active, BotCpuKind.Player, true)]
    public void AClientInClientBeginIsItsPlayersOnlyWhilePlayingWithAnActivePlayer(GameClient.eClientState state,
        GameObject.eObjectState? player, BotCpuKind kind, bool presence)
    {
        AssertCharge(Charge(Client(state, player), BotCpuStage.ClientBegin), kind, BotCpuStage.ClientBegin, null,
            presence);
    }

    [Test]
    public void APlayingPlayerBotOrPilotedClientCountsPresenceAndChargesItsPlayersCounter()
    {
        GameClient playerBot = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active,
            typeof(TestPlayerBotClient));
        AssertCharge(Charge(playerBot, BotCpuStage.ClientBegin), BotCpuKind.Playerbot, BotCpuStage.ClientBegin,
            playerBot.Player, presence: true);
        GameClient piloted = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active);
        AssertCharge(Charge(piloted, BotCpuStage.ClientBegin, piloted: true), BotCpuKind.Piloted,
            BotCpuStage.ClientBegin, piloted.Player, presence: true);
    }

    [Test]
    public void AClientInClientEndIsChargedButNeverCounted()
    {
        GameClient client = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active);
        AssertCharge(Charge(client, BotCpuStage.ClientEnd), BotCpuKind.Player, BotCpuStage.ClientEnd, null,
            presence: false);
    }

    [Test]
    public void AnIntIsUnattributedOutsideAFleetScopeAndGamebotFleetInsideOne()
    {
        AssertCharge(Charge(7, BotCpuStage.Npc), BotCpuKind.Unattributed, BotCpuStage.Npc, null, presence: false);
        AssertCharge(Charge(7, BotCpuStage.Npc, inFleet: true), BotCpuKind.Gamebot, BotCpuStage.Fleet, null,
            presence: false);
    }

    [Test]
    public void InsideAFleetScopeAnItemWithAnOwnerIsChargedAsUsual()
    {
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(bot.Brain, BotCpuStage.Npc, inFleet: true), BotCpuKind.Gamebot, BotCpuStage.Npc, bot,
            presence: false);
        AssertCharge(Charge(BrainOf(Npc(Make<StandardMobBrain>())), BotCpuStage.Npc, inFleet: true),
            BotCpuKind.Monster, BotCpuStage.Npc, null, presence: true);
        AssertCharge(Charge(BrainOf(Pet(bot)), BotCpuStage.Npc, inFleet: true), BotCpuKind.Gamebot, BotCpuStage.Pets,
            bot, presence: false);
        // An owner that is not living is none: the fleet's.
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, Make<GameStaticItem>()), BotCpuStage.Timer, inFleet: true),
            BotCpuKind.Gamebot, BotCpuStage.Fleet, null, presence: false);
    }

    [Test]
    public void AnItemTypeTheMeterDoesNotKnowIsUnattributed()
    {
        AssertCharge(Charge("text", BotCpuStage.Other), BotCpuKind.Unattributed, BotCpuStage.Other, null,
            presence: false);
        AssertCharge(Charge(new object(), BotCpuStage.Other), BotCpuKind.Unattributed, BotCpuStage.Other, null,
            presence: false);
        // A game object as the item is not in 3.3's table either.
        AssertCharge(Charge<GameObject>(Bot(autonomous: true), BotCpuStage.Other), BotCpuKind.Unattributed,
            BotCpuStage.Other, null, presence: false);
    }

    [TestCase(typeof(ABrain))]
    [TestCase(typeof(AttackComponent))]
    [TestCase(typeof(CastingComponent))]
    [TestCase(typeof(ECSGameEffect))]
    [TestCase(typeof(EffectListComponent))]
    [TestCase(typeof(MovementComponent))]
    [TestCase(typeof(CraftComponent))]
    [TestCase(typeof(ECSGameTimer))]
    [TestCase(typeof(SubZoneObject))]
    [TestCase(typeof(LivingBeingKilled))]
    [TestCase(typeof(GameClient))]
    public void ANullItemIsUnattributed(Type itemType) =>
        AssertCharge(ChargeAs(itemType, null, BotCpuStage.Other), BotCpuKind.Unattributed, BotCpuStage.Other, null,
            presence: false);

    [Test]
    public void ClassifyAllocatesNothing()
    {
        GameBot bot = Bot(autonomous: true);
        ABrain brain = bot.Brain;
        GameClient client = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active);
        LivingBeingKilled kill = Kill(Npc(Make<StandardMobBrain>()), Pet(bot));
        Func<GamePlayer, bool> isPiloted = NotPiloted;

        void ClassifyEach()
        {
            BotCpuMeter.Classify(7, BotCpuStage.Npc, true, isPiloted); // an int is never boxed
            BotCpuMeter.Classify(brain, BotCpuStage.Npc, false, isPiloted);
            BotCpuMeter.Classify(client, BotCpuStage.ClientBegin, false, isPiloted);
            BotCpuMeter.Classify(kill, BotCpuStage.Reaper, false, isPiloted);
        }

        ClassifyEach(); // warm-up: each item type's owner reader is chosen on its first use
        long before = GC.GetAllocatedBytesForCurrentThread();
        ClassifyEach();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    // The slots, the wrapper and hook H1 (3.1, 3.2, 3.7, 3.10, 3.11): each test calls the real GameLoop.ExecuteForEach,
    // whose pool keeps the delegate it was given, and reads what was charged as the difference of two snapshots.

    // A test subclass of GameLoopThreadPool that keeps the delegate it was given and runs the items in order, on the
    // calling thread (6.1).
    private sealed class RecordingPool : GameLoopThreadPool
    {
        public Delegate Given;
        public int Calls;

        public override void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            Calls++;
            Given = action;
            int count = Math.Min(items.Count, toExclusive);
            for (int i = 0; i < count; i++)
                action(items[i]);
        }

        public override void Dispose() { }
    }

    // A brain whose Body getter throws (Body is virtual, ai/ABrain.cs:15).
    private sealed class ThrowingBrain : ABrain
    {
        public override GameNPC Body
        {
            get => throw new InvalidOperationException("Body");
            set { }
        }

        public override void Think() { }

        public override void KillFSM() { }
    }

    // What was charged between two snapshots.
    private sealed class Charged(BotCpuTotals before, BotCpuTotals after)
    {
        public long Ticks(BotCpuKind kind, BotCpuStage stage) => after.Ticks(kind, stage) - before.Ticks(kind, stage);

        public long Calls(BotCpuKind kind, BotCpuStage stage) => after.Calls(kind, stage) - before.Calls(kind, stage);

        public long Bytes(BotCpuKind kind, BotCpuStage stage) => after.Bytes(kind, stage) - before.Bytes(kind, stage);

        public long Presence(BotCpuKind kind) => after.Presence(kind) - before.Presence(kind);

        public long AllCalls =>
            Enum.GetValues<BotCpuKind>().Sum(kind => Enum.GetValues<BotCpuStage>().Sum(stage => Calls(kind, stage)));

        public long AllPresence => Enum.GetValues<BotCpuKind>().Sum(Presence);
    }

    private static Charged Since(BotCpuTotals before) => new(before, BotCpuMeter.TakeSnapshot());

    // Every value of a snapshot: per kind its stages' ticks, calls and bytes, then its presence.
    private static long[] Values(BotCpuTotals totals) =>
        Enum.GetValues<BotCpuKind>().SelectMany(kind => Enum.GetValues<BotCpuStage>()
            .SelectMany(stage =>
                new[] { totals.Ticks(kind, stage), totals.Calls(kind, stage), totals.Bytes(kind, stage) })
            .Append(totals.Presence(kind))).ToArray();

    private static int SlotCount()
    {
        var slots = (List<long[]>)SlotsField.GetValue(null);
        if (slots == null)
            return 0;
        lock (slots)
            return slots.Count;
    }

    // Runs work on a new thread, as a game-loop worker would, and returns what it threw, if anything.
    private static Exception OnNewThread(Action work)
    {
        Exception failure = null;
        Thread thread = new(() =>
        {
            try { work(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        thread.Join();
        return failure;
    }

    private static void Spin(long ticks)
    {
        long end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end) { }
    }

    private static byte[] _sink;

    [Test]
    public void TheSwitchIsAPlainPublicStaticBoolOffByDefault()
    {
        FieldInfo enabled = typeof(BotCpuMeter).GetField(nameof(BotCpuMeter.Enabled));
        Assert.That(enabled, Is.Not.Null, "a field, not a property: H1 costs one bool read");
        Assert.That(enabled!.IsPublic && enabled.IsStatic && !enabled.IsInitOnly && !enabled.IsLiteral, Is.True);
        Assert.That(enabled.FieldType, Is.EqualTo(typeof(bool)));
        Assert.That(BotCpuMeter.Enabled, Is.False);
    }

    [Test]
    public void OffTheGameLoopHandsThePoolTheCallersDelegateAndMetersNothing()
    {
        BotCpuMeter.Enable();
        BotCpuMeter.Disable();
        GameLoop.ActiveService = nameof(NpcService);
        List<ABrain> brains = [Bot(autonomous: true).Brain, BrainOf(Npc(Make<StandardMobBrain>()))];
        int ran = 0;
        Action<ABrain> action = _ => ran++;
        int slots = SlotCount();
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        object threadSlot = "not read";

        // On a new thread, which has no slot yet: an off call must not make one.
        Exception failure = OnNewThread(() =>
        {
            GameLoop.ExecuteForEach(brains, brains.Count, action);
            threadSlot = ThreadSlotField.GetValue(null);
        });

        Assert.That(failure, Is.Null);
        Assert.That(_pool.Given, Is.SameAs(action));
        Assert.That(ran, Is.EqualTo(2));
        Assert.That(threadSlot, Is.Null, "no slot for the thread");
        Assert.That(SlotCount(), Is.EqualTo(slots));
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));
    }

    [Test]
    public void OffTheGameLoopAllocatesNothing()
    {
        List<ABrain> brains = [Bot(autonomous: true).Brain];
        Action<ABrain> action = static _ => { };
        GameLoop.ExecuteForEach(brains, brains.Count, action); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        GameLoop.ExecuteForEach(brains, brains.Count, action);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void OnEachItemRunsOnceAndChargesOneCallToItsKindAndStage()
    {
        BotCpuMeter.Enable();
        GameBot bot = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        List<ABrain> brains =
            [bot.Brain, companion.Brain, BrainOf(Npc(Make<StandardMobBrain>())), BrainOf(Pet(player))];
        List<GameClient> clients =
        [
            Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active),
            Client(GameClient.eClientState.CharScreen, GameObject.eObjectState.Active),
        ];
        Dictionary<object, int> runs = new();
        Action<ABrain> think = brain => runs[brain] = runs.GetValueOrDefault(brain) + 1;
        Action<GameClient> begin = client => runs[client] = runs.GetValueOrDefault(client) + 1;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ActiveService = nameof(NpcService);
        GameLoop.ExecuteForEach(brains, brains.Count, think);
        Assert.That(_pool.Given, Is.Not.SameAs(think));
        GameLoop.ActiveService = nameof(ClientService) + "." + nameof(ClientService.BeginTick);
        GameLoop.ExecuteForEach(clients, clients.Count, begin);
        Assert.That(_pool.Given, Is.Not.SameAs(begin));

        Charged charged = Since(before);
        Assert.That(brains.Select(brain => runs.GetValueOrDefault(brain)), Is.EqualTo(new[] { 1, 1, 1, 1 }));
        Assert.That(clients.Select(client => runs.GetValueOrDefault(client)), Is.EqualTo(new[] { 1, 1 }));
        Assert.Multiple(() =>
        {
            Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Npc), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Companion, BotCpuStage.Npc), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Monster, BotCpuStage.Npc), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Player, BotCpuStage.Pets), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Player, BotCpuStage.ClientBegin), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.ClientBegin), Is.EqualTo(1));
            Assert.That(charged.AllCalls, Is.EqualTo(6));
            Assert.That(charged.Presence(BotCpuKind.Companion), Is.EqualTo(1));
            Assert.That(charged.Presence(BotCpuKind.Monster), Is.EqualTo(1));
            Assert.That(charged.Presence(BotCpuKind.Player), Is.EqualTo(1));
            Assert.That(charged.AllPresence, Is.EqualTo(3));
        });
    }

    [Test]
    public void OnACallWithNothingToRunIsPassedThroughUnwrapped()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        Action<int> action = _ => Assert.Fail("no item runs");
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        GameLoop.ExecuteForEach(new List<int>(), 0, action);
        Assert.That(_pool.Given, Is.SameAs(action), "an empty list");
        _pool.Given = null;
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 0, action);
        Assert.That(_pool.Given, Is.SameAs(action), "nothing below toExclusive");
        Assert.That(_pool.Calls, Is.EqualTo(2));
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));
    }

    [Test]
    public void EachRunHasTheNextNumberAndAnEmptyPerBotTableWhileTheSlotsKeepTheirTotals()
    {
        int first = BotCpuMeter.Enable();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(first));
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        GameLoop.ExecuteForEach(new List<ABrain> { bot.Brain }, 1, static _ => { });
        Assert.That(BotCpuMeter.Counters().Select(pair => pair.Key), Is.EqualTo(new[] { bot }));
        BotCpuTotals firstRun = BotCpuMeter.TakeSnapshot();

        BotCpuMeter.Disable();
        Assert.That(BotCpuMeter.Enabled, Is.False);
        // Off, the report still reads the closed run's counters for its last window.
        Assert.That(BotCpuMeter.Counters().Select(pair => pair.Key), Is.EqualTo(new[] { bot }));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(first));

        int second = BotCpuMeter.Enable();
        Assert.That(second, Is.EqualTo(first + 1));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(second));
        Assert.That(BotCpuMeter.Counters(), Is.Empty);
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(firstRun)), "slots are never zeroed");
    }

    [Test]
    public void WhenTheSetupThrowsEveryItemRunsOnceUnmeteredWithTheCallersDelegate()
    {
        BotCpuMeter.Enable();
        List<string> warnings = [];
        WarnHookField.SetValue(null, (Action<string>)warnings.Add);
        GameLoop.ActiveService = nameof(NpcService);
        StageHookField.SetValue(null, (Func<string, BotCpuStage>)(_ => throw new InvalidOperationException("stage")));
        List<ABrain> brains = [Bot(autonomous: true).Brain, BrainOf(Npc(Make<StandardMobBrain>()))];
        Dictionary<ABrain, int> runs = new();
        Action<ABrain> action = brain => runs[brain] = runs.GetValueOrDefault(brain) + 1;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, action));
        Assert.That(_pool.Given, Is.SameAs(action));
        Assert.That(brains.Select(brain => runs.GetValueOrDefault(brain)), Is.EqualTo(new[] { 1, 1 }));
        // One warning a run (3.1, 5.1), naming the failure. Again: no second warning, and the call runs the same way.
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.StartWith("Bot CPU meter: the setup of a game-loop call failed")
            .And.Contain("InvalidOperationException"));
        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, action));
        Assert.That(brains.Select(brain => runs.GetValueOrDefault(brain)), Is.EqualTo(new[] { 2, 2 }));
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));
        Assert.That(warnings, Has.Count.EqualTo(1));
        // A new run may warn once again.
        BotCpuMeter.Enable();
        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, action));
        Assert.That(warnings, Has.Count.EqualTo(2));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ANullOrEmptyActiveServiceIsOther(string activeService)
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = activeService;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { }));
        Assert.That(Since(before).Calls(BotCpuKind.Unattributed, BotCpuStage.Other), Is.EqualTo(2));
    }

    // A service as upstream's are: its Tick runs what was posted to it, as NpcService.Tick does first.
    private sealed class PostingService : GameServiceBase
    {
        public override void Tick() => ProcessPostedActionsParallel();
    }

    // Posted actions reach the meter through their service's own GameLoop.ExecuteForEach call
    // (ECS-Services/GameServiceBase.cs:62), over PostedAction items, which are private: the meter cannot read who
    // posted them or for whom (3.6). Each is unattributed in the stage the game loop set for the service, even one
    // whose state is a bot.
    [TestCase(nameof(GameLoopService), BotCpuStage.GameLoop)]
    [TestCase(nameof(NpcService), BotCpuStage.Npc)]
    [TestCase(nameof(DailyQuestService), BotCpuStage.Quests)]
    public void APostedActionIsUnattributedInItsServicesStage(string profileKey, BotCpuStage stage)
    {
        BotCpuMeter.Enable();
        PostingService service = new();
        GameBot bot = Bot(autonomous: true);
        List<object> ran = [];
        service.Post(state => ran.Add(state), bot);
        service.Post(state => ran.Add(state), 7);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ActiveService = profileKey; // as GameLoop's tick sets it before the service's step
        service.Tick();

        Charged charged = Since(before);
        Assert.Multiple(() =>
        {
            Assert.That(ran, Is.EqualTo(new object[] { bot, 7 }), "each action ran once");
            Assert.That(_pool.Calls, Is.EqualTo(1));
            Assert.That(_pool.Given.Method.DeclaringType, Is.Not.EqualTo(typeof(GameServiceBase)),
                "the pool got the meter's wrapper, not the service's own callback");
            Assert.That(charged.Calls(BotCpuKind.Unattributed, stage), Is.EqualTo(2));
            Assert.That(charged.AllCalls, Is.EqualTo(2), "nothing else");
            Assert.That(charged.AllPresence, Is.Zero);
            Assert.That(BotCpuMeter.Counters(), Is.Empty, "no bot's counter");
        });
    }

    [Test]
    public void TheOwnerIsReadBeforeTheCallback()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        List<ABrain> brains = [bot.Brain, BrainOf(Npc(Make<StandardMobBrain>()))];
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(brains, brains.Count, static brain => brain.Body = null);

        Charged charged = Since(before);
        Assert.That(brains.Select(brain => brain.Body), Is.All.Null, "each callback ran");
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Npc), Is.EqualTo(1));
        Assert.That(charged.Calls(BotCpuKind.Monster, BotCpuStage.Npc), Is.EqualTo(1));
        Assert.That(charged.Presence(BotCpuKind.Monster), Is.EqualTo(1));
        Assert.That(BotCpuMeter.Counters().Single(pair => pair.Key == bot).Value.Calls, Is.EqualTo(1));
    }

    [Test]
    public void AnItemWhoseOwnerThrowsIsUnattributedAndStillRunsOnce()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        List<ABrain> brains = [Make<ThrowingBrain>()];
        int ran = 0;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, _ => ran++));

        Charged charged = Since(before);
        Assert.That(ran, Is.EqualTo(1));
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(1));
        Assert.That(charged.AllCalls, Is.EqualTo(1));
        Assert.That(charged.AllPresence, Is.Zero);
        Assert.That(BotCpuMeter.Counters(), Is.Empty);
    }

    [Test]
    public void AnExceptionFromACallbackReachesTheCallerUnchanged()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(TimerService);
        InvalidOperationException thrown = new("callback");
        Exception caught = Assert.Throws<InvalidOperationException>(() =>
            GameLoop.ExecuteForEach(new List<int> { 1 }, 1, _ => throw thrown));
        Assert.That(caught, Is.SameAs(thrown));
    }

    [Test]
    public void EachBotsItemsAddToItsOwnCounterAndAPetsToItsOwners()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot first = Bot(autonomous: true);
        GameBot second = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        List<ABrain> brains =
        [
            first.Brain, second.Brain, BrainOf(Pet(first)), companion.Brain, BrainOf(Pet(player)),
            BrainOf(Npc(Make<StandardMobBrain>())),
        ];
        long spin = Stopwatch.Frequency / 10_000; // 0.1 ms
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(brains, brains.Count, _ => Spin(spin));

        Charged charged = Since(before);
        Dictionary<GameObject, BotCpuCounter> counters =
            BotCpuMeter.Counters().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.That(counters.Keys, Is.EquivalentTo(new GameObject[] { first, second, companion }), "bots only");
        Assert.Multiple(() =>
        {
            Assert.That(counters[first].Calls, Is.EqualTo(2), "its brain and its pet's");
            Assert.That(counters[second].Calls, Is.EqualTo(1));
            Assert.That(counters[companion].Calls, Is.EqualTo(1));
            Assert.That(counters[first].Ticks, Is.GreaterThanOrEqualTo(2 * spin));
            Assert.That(counters[second].Ticks, Is.GreaterThanOrEqualTo(spin));
            Assert.That(counters[first].Kind, Is.EqualTo(BotCpuKind.Gamebot));
            Assert.That(counters[companion].Kind, Is.EqualTo(BotCpuKind.Companion));
            Assert.That(counters.Values.Select(counter => counter.LastTicks + counter.LastCalls), Is.All.Zero,
                "written by the report only");
            // A bot's ticks are those its items added to the slot.
            Assert.That(counters[first].Ticks + counters[second].Ticks,
                Is.EqualTo(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Npc) +
                    charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Pets)));
            Assert.That(counters[companion].Ticks, Is.EqualTo(charged.Ticks(BotCpuKind.Companion, BotCpuStage.Npc)));
        });
    }

    [Test]
    public void AnItemThatAllocatesChargesItsBytesToItsKindAndAnIntListChargesNone()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(new List<ABrain> { bot.Brain }, 1, static _ => _sink = new byte[1000]);
        GameLoop.ExecuteForEach(new List<int> { 1, 2, 3 }, 3, static _ => { });

        Charged charged = Since(before);
        Assert.That(charged.Bytes(BotCpuKind.Gamebot, BotCpuStage.Npc), Is.GreaterThanOrEqualTo(1000));
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(3));
        Assert.That(charged.Bytes(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.Zero);
    }

    [Test]
    public void TheMeterAllocatesForEachCallNotForEachItem()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        List<ABrain> one = [bot.Brain];
        List<ABrain> many =
            [.. Enumerable.Range(0, 20).SelectMany(_ => new[] { bot.Brain, BrainOf(Pet(bot)), BrainOf(Pet(player)) })];
        Action<ABrain> action = static _ => { };

        long Cost(List<ABrain> items)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            GameLoop.ExecuteForEach(items, items.Count, action);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Cost(many); // warm-up: the thread's slot, the owner readers, each bot's counter
        long perCall = Cost(one);
        Assert.That(perCall, Is.GreaterThan(0), "one wrapper and its delegate");
        Assert.That(Cost(many), Is.EqualTo(perCall));
    }

    [Test]
    public void ASnapshotAddsUpTheSlotOfEveryThreadThatRanAnItem()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(TimerService);
        GameLoop.ExecuteForEach(new List<int> { 1 }, 1, static _ => { }); // this thread's slot, if it had none
        int slots = SlotCount();
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        object otherSlot = null;

        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        Exception failure = OnNewThread(() =>
        {
            GameLoop.ExecuteForEach(new List<int> { 3 }, 1, static _ => { });
            otherSlot = ThreadSlotField.GetValue(null);
        });

        Assert.That(failure, Is.Null);
        Assert.That(Since(before).Calls(BotCpuKind.Unattributed, BotCpuStage.Timer), Is.EqualTo(3));
        Assert.That(SlotCount(), Is.EqualTo(slots + 1), "the new thread registered its slot");
        var registered = (List<long[]>)SlotsField.GetValue(null);
        lock (registered!)
            Assert.That(registered, Has.Some.SameAs(otherSlot));
        Assert.That(((long[])otherSlot)!.Length, Is.EqualTo(BotCpuTotals.SlotLength));
    }

    [Test]
    public void ASlotHasEachCellAndPresenceCountOnceBetween64BytesOfPaddingAtEachEnd()
    {
        Assert.That(BotCpuTotals.SlotLength, Is.EqualTo(401));
        List<int> indexes = [];
        foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
        {
            foreach (BotCpuStage stage in Enum.GetValues<BotCpuStage>())
                indexes.AddRange(Enumerable.Range(BotCpuTotals.CellIndex(kind, stage), 3));
            indexes.Add(BotCpuTotals.PresenceIndex(kind));
        }

        Assert.That(indexes, Is.Unique);
        Assert.That(indexes, Has.Count.EqualTo(385));
        Assert.That(indexes.Min(), Is.EqualTo(8));
        Assert.That(indexes.Max(), Is.EqualTo(401 - 8 - 1));
    }

    [Test]
    public void TheTotalsAreTheSumsOfTheSlotsAndReadingThemChangesNoSlot()
    {
        long[] a = new long[BotCpuTotals.SlotLength];
        long[] b = new long[BotCpuTotals.SlotLength];
        int cell = BotCpuTotals.CellIndex(BotCpuKind.Monster, BotCpuStage.Movement);
        (a[cell], a[cell + 1], a[cell + 2]) = (5, 2, 300);
        (b[cell], b[cell + 1], b[cell + 2]) = (7, 1, 0);
        a[BotCpuTotals.PresenceIndex(BotCpuKind.Monster)] = 30;
        b[BotCpuTotals.PresenceIndex(BotCpuKind.Monster)] = 12;
        long[] aBefore = (long[])a.Clone();
        long[] bBefore = (long[])b.Clone();

        BotCpuTotals totals = BotCpuTotals.Sum([a, b]);

        Assert.That(a, Is.EqualTo(aBefore));
        Assert.That(b, Is.EqualTo(bBefore));
        Assert.That(totals.Ticks(BotCpuKind.Monster, BotCpuStage.Movement), Is.EqualTo(12));
        Assert.That(totals.Calls(BotCpuKind.Monster, BotCpuStage.Movement), Is.EqualTo(3));
        Assert.That(totals.Bytes(BotCpuKind.Monster, BotCpuStage.Movement), Is.EqualTo(300));
        Assert.That(totals.Presence(BotCpuKind.Monster), Is.EqualTo(42));
        Assert.That(Values(totals).Sum(), Is.EqualTo(12 + 3 + 300 + 42), "nothing else");

        // The report builds its windows from plain values.
        BotCpuTotals built = new();
        built.Add(BotCpuKind.Monster, BotCpuStage.Movement, 12, 3, 300);
        built.AddPresence(BotCpuKind.Monster, 42);
        Assert.That(Values(built), Is.EqualTo(Values(totals)));
        Assert.That(Values(new BotCpuTotals()), Is.All.Zero);
    }

    [Test]
    public void ACounterKeepsItsHotFieldsBetween64BytesOfPaddingAndIsSmall()
    {
        Assert.That(typeof(BotCpuCounter).IsExplicitLayout, Is.True);
        FieldInfo[] fields =
            typeof(BotCpuCounter).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        int Offset(FieldInfo field) => field.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
        int ticks = Offset(typeof(BotCpuCounter).GetField(nameof(BotCpuCounter.Ticks))!);
        int calls = Offset(typeof(BotCpuCounter).GetField(nameof(BotCpuCounter.Calls))!);
        Assert.That(ticks, Is.GreaterThanOrEqualTo(64));
        Assert.That(calls, Is.EqualTo(ticks + 8));
        IEnumerable<FieldInfo> cold =
            fields.Where(field => field.Name is not (nameof(BotCpuCounter.Ticks) or nameof(BotCpuCounter.Calls)));
        Assert.That(cold.Select(Offset), Is.All.GreaterThanOrEqualTo(calls + 8 + 64));

        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuCounter counter = new(BotCpuKind.Companion);
        long size = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(size, Is.LessThan(200));
        Assert.That(counter.Kind, Is.EqualTo(BotCpuKind.Companion));
        Assert.That(new[] { counter.Ticks, counter.Calls, counter.LastTicks, counter.LastCalls }, Is.All.Zero);
    }

    // The fleet scope and hook H6 (3.5, 5.1): NpcService.Tick runs the GameBot fleet planning between BeginFleet and
    // EndFleet, as these tests do. The scope's own work and the items of its nested call (the camp planning's int
    // items, which have no owner) go to gamebot / Fleet, and the nested call's time is taken out of the scope's, so
    // nothing is counted twice.

    private static readonly long Millisecond = Stopwatch.Frequency / 1000;

    private static bool InFleet => (bool)InFleetField.GetValue(null)!;

    // A pool that runs the items on a thread of its own and waits for it, as the multi-threaded pool's workers do.
    private sealed class WorkerPool : GameLoopThreadPool
    {
        public override void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            Exception failure = OnNewThread(() =>
            {
                int count = Math.Min(items.Count, toExclusive);
                for (int i = 0; i < count; i++)
                    action(items[i]);
            });
            if (failure != null)
                throw failure;
        }

        public override void Dispose() { }
    }

    [TestCase(nameof(BotCpuMeter.BeginFleet))]
    [TestCase(nameof(BotCpuMeter.EndFleet))]
    public void TheFleetScopesCallsInline(string name)
    {
        MethodInfo method = typeof(BotCpuMeter).GetMethod(name)!;
        Assert.That(method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining,
            Is.EqualTo(MethodImplAttributes.AggressiveInlining), "off, H6 is a bool read and a test of a local (5.1)");
    }

    [Test]
    public void OffBeginFleetReturnsZeroAndEndFleetOfZeroChargesNothing()
    {
        BotCpuMeter.Enable();
        BotCpuMeter.Disable();
        GameLoop.ActiveService = nameof(NpcService);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();

        Assert.That(fleetStart, Is.Zero);
        Assert.That(InFleet, Is.False);
        BotCpuMeter.EndFleet(fleetStart);
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));

        // Turned on inside a scope begun while off: its nested items are charged as outside any scope, and
        // EndFleet(0) charges nothing.
        BotCpuMeter.Enable();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);
        Charged charged = Since(before);
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(2));
        Assert.That(charged.AllCalls, Is.EqualTo(2));
    }

    [Test]
    public void OffTheFleetScopeAllocatesNothing()
    {
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet()); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet());
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void OnTheFleetScopeItselfAllocatesNothing()
    {
        BotCpuMeter.Enable();
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet()); // warm-up: this thread's slot, if it had none
        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet());
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void TheFleetIsTheScopesOwnWorkPlusItsNestedItemsCountedOnce()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        List<int> projections = [1, 2, 3];
        Action<int> project = static _ => Spin(2 * Millisecond);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        // As NpcService.Tick: the scope's own serial work (about 1 ms), and the camp planning's nested call over its
        // int items (3 × about 2 ms), which the recording pool runs inline.
        long wallStart = Stopwatch.GetTimestamp();
        long fleetStart = BotCpuMeter.BeginFleet();
        Assert.That(InFleet, Is.True);
        Spin(Millisecond);
        GameLoop.ExecuteForEach(projections, projections.Count, project);
        BotCpuMeter.EndFleet(fleetStart);
        long wall = Stopwatch.GetTimestamp() - wallStart;

        Charged charged = Since(before);
        long fleet = charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet);
        Assert.Multiple(() =>
        {
            Assert.That(fleetStart, Is.Not.Zero);
            Assert.That(InFleet, Is.False, "EndFleet clears the flag");
            Assert.That(fleet, Is.GreaterThanOrEqualTo(6 * Millisecond), "the nested items' own times");
            // Counted twice (the nested call inside the scope's time, and its items again) it would be about 13 ms.
            Assert.That(fleet, Is.LessThanOrEqualTo(wall * 1.05), "no more than the scope's wall time");
            Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(4), "3 items and the scope");
            Assert.That(charged.AllCalls, Is.EqualTo(4), "nothing else");
            Assert.That(BotCpuMeter.Counters(), Is.Empty, "Fleet counts for no bot");
        });
    }

    [Test]
    public void TheFleetsBytesAreTheScopesOwnPlusItsNestedItemsCountedOnce()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        List<int> projections = [1, 2, 3];
        Action<int> project = static _ => _sink = new byte[1000];
        GameLoop.ExecuteForEach(new List<int> { 0 }, 1, project); // warm-up, outside any scope
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        _sink = new byte[1000];
        GameLoop.ExecuteForEach(projections, projections.Count, project);
        BotCpuMeter.EndFleet(fleetStart);

        long bytes = Since(before).Bytes(BotCpuKind.Gamebot, BotCpuStage.Fleet);
        Assert.That(bytes, Is.GreaterThanOrEqualTo(4 * 1000), "the scope's own 1,000 and the 3 items' 1,000 each");
        // Counted twice, the items' 3,000 bytes would be in the scope's bytes too.
        Assert.That(bytes, Is.LessThan(4 * 1000 + 3 * 1000));
    }

    [Test]
    public void ANestedItemOnAWorkerThreadIsStillFleet()
    {
        // Only the game-loop thread reads the flag: the wrapper carries it to the worker threads (3.1).
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        ThreadPoolField.SetValue(null, new WorkerPool());
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);

        Charged charged = Since(before);
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(3), "2 items and the scope");
        Assert.That(charged.AllCalls, Is.EqualTo(3));
    }

    [Test]
    public void OffInsideTheScopeDropsItAndClearsTheFlagAndTheNextRunStartsClean()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        BotCpuMeter.Disable(); // /botcpu off, inside the scope
        BotCpuMeter.EndFleet(fleetStart);

        Assert.That(InFleet, Is.False, "a scope cut by off never leaves the flag set");
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)), "the scope is not charged");

        // The next run: an int batch outside any scope is unattributed, and the next scope is Fleet again.
        BotCpuMeter.Enable();
        before = BotCpuMeter.TakeSnapshot();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        Charged outside = Since(before);
        Assert.That(outside.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(2));
        Assert.That(outside.AllCalls, Is.EqualTo(2));

        before = BotCpuMeter.TakeSnapshot();
        fleetStart = BotCpuMeter.BeginFleet();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);
        Charged inside = Since(before);
        Assert.That(inside.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(3), "2 items and the scope");
        Assert.That(inside.AllCalls, Is.EqualTo(3));
    }

    [Test]
    public void OffAndOnInsideTheScopeDropsItWithItsNestedItems()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        long fleetStart = BotCpuMeter.BeginFleet();
        BotCpuMeter.Disable();
        BotCpuMeter.Enable(); // off, then on: a new run, inside the old run's scope
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);

        // The scope belongs to the closed run: neither its own work nor its nested items are the new run's Fleet.
        Charged charged = Since(before);
        Assert.That(InFleet, Is.False);
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(2));
        Assert.That(charged.AllCalls, Is.EqualTo(2));
    }

    [Test]
    public void ANestedCallThatThrowsStillAddsItsTimeToTheScopeAndTheExceptionReachesTheCaller()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        InvalidOperationException thrown = new("projection");
        // Warm-up, outside any scope: a process's first exception takes milliseconds to unwind.
        Assert.Throws<InvalidOperationException>(() =>
            GameLoop.ExecuteForEach(new List<int> { 1 }, 1, _ => throw new InvalidOperationException("warm-up")));
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        Spin(Millisecond);
        Exception caught = Assert.Throws<InvalidOperationException>(() =>
            GameLoop.ExecuteForEach(new List<int> { 1 }, 1, _ =>
            {
                Spin(20 * Millisecond);
                throw thrown;
            }));
        BotCpuMeter.EndFleet(fleetStart);

        Charged charged = Since(before);
        Assert.That(caught, Is.SameAs(thrown), "unchanged");
        // The item that threw is charged to nothing (its callback did not return), and the nested call's 20 ms are
        // still taken out of the scope: the scope's own work is about 1 ms, not 21.
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(1), "the scope only");
        Assert.That(charged.AllCalls, Is.EqualTo(1));
        Assert.That(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.GreaterThanOrEqualTo(Millisecond));
        Assert.That(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.LessThan(20 * Millisecond));
    }

    [Test]
    public void BeginFleetReplacesAStaleScope()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        // A scope whose EndFleet never ran (on the server, a throw there stops it), with a nested call of about 5 ms.
        long stale = BotCpuMeter.BeginFleet();
        GameLoop.ExecuteForEach(new List<int> { 1 }, 1, static _ => Spin(5 * Millisecond));
        long fleetStart = BotCpuMeter.BeginFleet();
        Spin(Millisecond);
        BotCpuMeter.EndFleet(fleetStart);
        BotCpuMeter.EndFleet(stale); // the flag is already clear: nothing

        Charged charged = Since(before);
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(2), "the item and the new scope");
        // The new scope's own 1 ms, not less the stale scope's 5 ms nested call.
        Assert.That(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.GreaterThanOrEqualTo(6 * Millisecond));
    }
}
