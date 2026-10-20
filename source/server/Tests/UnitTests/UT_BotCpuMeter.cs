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
// in their report order (2.5, 2.3), the stage of each profile key (3.4) and the kind of each owner, pets included
// (3.3), with game objects built without their constructors and their fields set by reflection.
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
}
