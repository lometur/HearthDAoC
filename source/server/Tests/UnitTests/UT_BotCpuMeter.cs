using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
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
// game objects built without their constructors and their fields set by reflection.
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

    private GameServer _savedServer;

    [SetUp]
    public void SaveGlobals()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }

    [TearDown]
    public void RestoreGlobals() => GameServer.LoadTestDouble(_savedServer);

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

    // Classify<T> for an item type the test can only name at run time (a private one, or one of a list).
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

    [TestCase(BotCpuStage.GameLoop)]
    [TestCase(BotCpuStage.Npc)]
    [TestCase(BotCpuStage.Quests)]
    public void APostedActionIsUnattributedInTheStageItWasPostedTo(BotCpuStage stage)
    {
        // GameServiceBase.PostedAction is private (3.6), so Classify<PostedAction> is reached by reflection.
        Type posted = typeof(GameServiceBase).GetNestedType("PostedAction", BindingFlags.NonPublic)!;
        AssertCharge(ChargeAs(posted, RuntimeHelpers.GetUninitializedObject(posted), stage), BotCpuKind.Unattributed,
            stage, null, presence: false);
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
}
