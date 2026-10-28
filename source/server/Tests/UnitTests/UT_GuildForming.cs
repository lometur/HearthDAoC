using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.HearthDAoC;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.PacketHandler;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: forming a guild needs a group of guild_num (8) again, bots included (owner 2026-10-28, #176). GuildForming
// holds the rule; guild.cs counts the group (every member: players, player bots, gamebots, companions) and its human
// players, and carries the outcome out. Player bots never join a starter guild (StartupGuilds.AddNewbieToStarterGuild).
// The group doubles are made without their constructors, as UT_PlayerBotWake.cs:127-140 and
// UT_PlayerLedPullCoordinator.cs:874-919 make them: a human's client is a plain GameClient, a player bot's a
// HeadlessClient. GameObject's type initializer reads the database: an empty one stands in (UT_PlayerBotWake.cs:31-35).
// Guild's reads the server type (Guild.cs:334-338): a default configuration (GST_Normal) stands in, so it makes no
// dummy guild.
[TestFixture, NonParallelizable]
public sealed class UT_GuildForming
{
    private const int GuildNum = 8; // guild_num's default and the clean world's value
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        public override GameServerConfiguration Configuration => DefaultConfiguration;
    }
    private static readonly IObjectDatabase EmptyDatabase =
        DispatchProxy.Create<IObjectDatabase, UT_HeadlessClient.EmptyReads>();
    private static readonly GameServerConfiguration DefaultConfiguration = new();

    private GameServer _savedServer;

    [SetUp]
    public void SetUp()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }

    [TearDown]
    public void TearDown() => GameServer.LoadTestDouble(_savedServer);

    private static GuildFormStep Rule(int members, int humans, int guildNum = GuildNum) =>
        GuildForming.Rule(members, humans, guildNum);

    [Test]
    public void APlayerAloneIsRefused()
    {
        Assert.That(Rule(1, 1), Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void APlayerAndSixBotsAreRefused()
    {
        Assert.That(Rule(7, 1), Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void APlayerAndSevenBotsFormAtOnce()
    {
        // The bots can't answer the accept dialog: the player forms the guild at the registrar, for the fee.
        Assert.That(Rule(8, 1), Is.EqualTo(GuildFormStep.FormNow));
    }

    [Test]
    public void TwoPlayersAndSixBotsAskTheOtherPlayer()
    {
        // The dialog goes to the human players other than the leader; the bots count as accepting.
        Assert.That(Rule(8, 2), Is.EqualTo(GuildFormStep.AskOthers));
    }

    [Test]
    public void EightPlayersAskTheOtherSeven()
    {
        Assert.That(Rule(8, 8), Is.EqualTo(GuildFormStep.AskOthers));
    }

    [Test]
    public void PlayersShortOfTheNumberAreRefusedToo()
    {
        Assert.That(Rule(7, 2), Is.EqualTo(GuildFormStep.Refused));
        Assert.That(Rule(7, 7), Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void WithGuildNumOneAPlayerAloneFormsAtOnce()
    {
        // An owner who sets guild_num to 1 lets a player form a guild alone.
        Assert.That(Rule(1, 1, guildNum: 1), Is.EqualTo(GuildFormStep.FormNow));
    }

    [Test]
    public void AGroupOverTheNumberFormsToo()
    {
        Assert.That(Rule(9, 1), Is.EqualTo(GuildFormStep.FormNow));
        Assert.That(Rule(9, 3), Is.EqualTo(GuildFormStep.AskOthers));
    }

    [TestCase("hdbot-alb-01", true)]
    [TestCase("hdbot-hib-150", true)]
    [TestCase("HDBOT-mid-07", true)] // Account.Name is compared without case in the database
    [TestCase("hdbot", false)]
    [TestCase("myhdbot-alb-01", false)]
    [TestCase("lometur", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void APlayerBotIsKnownByItsAccount(string account, bool bot)
    {
        // StartupGuilds.AddNewbieToStarterGuild skips these characters, so a player bot never joins a starter guild.
        Assert.That(PlayerBotPlan.IsBotAccount(account), Is.EqualTo(bot));
    }

    [Test]
    public void APlayerAndSevenPlayerBotsAreOneHumanAndFormAtOnce()
    {
        // The owner's case: the player bots fill the group to 8, and none of them is asked.
        GamePlayer player = Human("Lometur");
        Group group = GroupOf(player, PlayerBots(7));
        Assert.That(GuildForming.Humans(player, group), Is.EqualTo(new[] { player }));
        Assert.That(GuildForming.Rule(group.MemberCount, GuildForming.Humans(player, group).Count, GuildNum),
            Is.EqualTo(GuildFormStep.FormNow));
    }

    [Test]
    public void TwoPlayersAndSixPlayerBotsAskTheOtherPlayer()
    {
        GamePlayer player = Human("Lometur"), friend = Human("Thelegend");
        Group group = GroupOf(player, [friend, ..PlayerBots(6)]);
        Assert.That(GuildForming.Humans(player, group), Is.EquivalentTo(new[] { player, friend }));
        Assert.That(GuildForming.Rule(group.MemberCount, GuildForming.Humans(player, group).Count, GuildNum),
            Is.EqualTo(GuildFormStep.AskOthers));
    }

    [Test]
    public void APlayerAndSixPlayerBotsAreRefused()
    {
        GamePlayer player = Human("Lometur");
        Group group = GroupOf(player, PlayerBots(6));
        Assert.That(GuildForming.Rule(group.MemberCount, GuildForming.Humans(player, group).Count, GuildNum),
            Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void GameBotsCountAsMembersButAreNeverAsked()
    {
        GamePlayer player = Human("Lometur");
        Group group = GroupOf(player, [..PlayerBots(3), ..GameBots(4)]);
        Assert.That(group.MemberCount, Is.EqualTo(8));
        Assert.That(GuildForming.Humans(player, group), Is.EqualTo(new[] { player }));
    }

    [Test]
    public void APlayerWithNoGroupIsTheOnlyHuman()
    {
        GamePlayer player = Human("Lometur");
        Assert.That(GuildForming.Humans(player, null), Is.EqualTo(new[] { player }));
    }

    [Test]
    public void APlayerBotInAGuildStopsTheForming()
    {
        GamePlayer player = Human("Lometur");
        List<GamePlayer> bots = PlayerBots(7);
        Field(typeof(GamePlayer), bots[4], "m_guild", Make<Guild>());
        Assert.That(GuildForming.BotInAGuild(GroupOf(player, bots)), Is.EqualTo(bots[4].Name));
    }

    [Test]
    public void APlayersOwnGuildIsNotABotsOne()
    {
        // guild.cs checks the players' guilds itself; BotInAGuild names only a bot.
        GamePlayer player = Human("Lometur");
        Field(typeof(GamePlayer), player, "m_guild", Make<Guild>());
        Assert.That(GuildForming.BotInAGuild(GroupOf(player, PlayerBots(7))), Is.Null);
        Assert.That(GuildForming.BotInAGuild(null), Is.Null);
    }

    [Test]
    public void AGameBotInAGuildStopsTheForming()
    {
        // A gamebot's guild is kept by AutonomousBotGuilds (by its id), not on the bot.
        GameBot bot = WorldBot("Rook", worldBot: true, databaseId: 9_000_176);
        Guild guild = Make<Guild>();
        var members = (ConcurrentDictionary<long, OfflineWorldBotGuildRecord>)typeof(AutonomousBotGuilds)
            .GetField("Members", HiddenStatic)!.GetValue(null)!;
        var guilds = (Dictionary<string, Guild>)typeof(GuildMgr).GetField("_idToGuild", HiddenStatic)!.GetValue(null)!;
        var gate = (System.Threading.Lock)typeof(GuildMgr).GetField("_lock", HiddenStatic)!.GetValue(null)!;
        const string GuildId = "ut-guild-forming";
        try
        {
            lock (gate) guilds[GuildId] = guild;
            members[bot.DatabaseID] = new OfflineWorldBotGuildRecord { BotId = bot.DatabaseID, GuildID = GuildId };
            Assert.That(GuildForming.BotInAGuild(GroupOf(Human("Lometur"), [bot, ..PlayerBots(6)])),
                Is.EqualTo("Rook"));
        }
        finally
        {
            members.TryRemove(bot.DatabaseID, out _);
            lock (gate) guilds.Remove(GuildId);
        }
        Assert.That(GuildForming.BotInAGuild(GroupOf(Human("Lometur"), [bot, ..PlayerBots(6)])), Is.Null);
    }

    [Test]
    public void JoinBotsTakesEachGameBotAndNamesTheOnesThatCannotJoin()
    {
        // A dead gamebot can't be invited (AutonomousBotGuilds.JoinRefusal): the leader is told. A companion stays out
        // without a word: it already wears its owner's guild name and emblem.
        (GamePlayer leader, List<string> told) = Leader("Lometur");
        Field(typeof(GamePlayer), leader, "m_guild", Make<Guild>());
        Group group = GroupOf(leader,
            [WorldBot("Rook", worldBot: true, databaseId: 9_000_177), WorldBot("Pet", worldBot: false)]);
        GuildForming.JoinBots(Make<Guild>(), leader, group, addPlayerBots: false);
        Assert.That(told, Is.EqualTo(new[] { "Rook did not join the guild: That character cannot be invited." }));
    }

    [Test]
    public void JoinBotsAddsThePlayerBotsOnlyWhenAsked()
    {
        // A player bot of another realm can't be added (Guild.AddPlayer refuses it before any change): the leader is
        // told, which shows JoinBots tried. With addPlayerBots false the caller has added them itself.
        (GamePlayer leader, List<string> told) = Leader("Lometur");
        GamePlayer bot = PlayerBots(1)[0];
        bot.Realm = eRealm.Midgard;
        Guild guild = Make<Guild>();
        Field(typeof(Guild), guild, "m_DBguild", new DbGuild { Realm = (byte)eRealm.Albion, Ranks = [] });
        Group group = GroupOf(leader, [bot]);

        GuildForming.JoinBots(guild, leader, group, addPlayerBots: false);
        Assert.That(told, Is.Empty);
        GuildForming.JoinBots(guild, leader, group, addPlayerBots: true);
        Assert.That(told, Is.EqualTo(new[] { $"{bot.Name} could not join the guild." }));
    }

    // IPacketLib's SendMessage lines, kept; every other call does nothing.
    public class Told : DispatchProxy
    {
        public List<string> Lines = [];
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IPacketLib.SendMessage) && args[0] is string line) Lines.Add(line);
            Type result = method.ReturnType;
            return result.IsValueType && result != typeof(void) ? Activator.CreateInstance(result) : null;
        }
    }

    private static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Field(Type type, object owner, string name, object value) =>
        type.GetField(name, Hidden)!.SetValue(owner, value);

    private static GamePlayer Human(string name, GameClient client = null)
    {
        GamePlayer player = Make<GamePlayer>();
        Field(typeof(GamePlayer), player, "m_client", client ?? Make<GameClient>());
        Field(typeof(GameObject), player, "m_name", name);
        return player;
    }

    private static (GamePlayer, List<string>) Leader(string name)
    {
        GameClient client = Make<GameClient>();
        IPacketLib output = DispatchProxy.Create<IPacketLib, Told>();
        client.Out = output;
        return (Human(name, client), ((Told)(object)output).Lines);
    }

    private static List<GamePlayer> PlayerBots(int count) => Enumerable.Range(1, count).Select(index =>
    {
        GamePlayer bot = Make<GamePlayer>();
        Field(typeof(GamePlayer), bot, "m_client", Make<HeadlessClient>());
        Field(typeof(GameObject), bot, "m_name", $"Hdbot{index}");
        return bot;
    }).ToList();

    private static GameBot WorldBot(string name, bool worldBot, long databaseId = 0)
    {
        GameBot bot = Make<GameBot>();
        Field(typeof(GameObject), bot, "m_name", name);
        Field(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", worldBot);
        bot.DatabaseID = databaseId;
        return bot;
    }

    private static IEnumerable<GameBot> GameBots(int count) =>
        Enumerable.Range(1, count)
            .Select(index => WorldBot($"Rook{index}", worldBot: true, databaseId: 9_000_200 + index));

    private static Group GroupOf(GamePlayer leader, IEnumerable<GameLiving> others)
    {
        var group = new Group(leader);
        var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden)!.GetValue(group)!;
        members.Add(leader);
        members.AddRange(others);
        return group;
    }
}
