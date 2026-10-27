using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Events;
using DOL.GS.HearthDAoC;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.ServerRules;
using DOL.Network;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the player bot's client and packet library (#137), with no running server: the meter kind; the
// session-id allocator the client reaches by reflection; the login and dialog packet bodies, read as the handlers
// read them; the library's answers and records; the navmesh LoS answer; ClientService's two marked blocks.
[TestFixture, NonParallelizable]
public sealed class UT_HeadlessClient
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    // As UT_BotCpuMeter.cs:36-53: GameObject's type initializer reads the database.
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

    private sealed class FakeNav : PathfindingMgrBase
    {
        public bool Available = true, Mesh = true, Sight;
        public int Rays;
        public override bool IsAvailable => Available;
        public override bool HasNavmesh(Zone zone) => Mesh;
        public override bool HasLineOfSight(Zone zone, Vector3 position, Vector3 target, EDtPolyFlags[] filters)
        {
            Rays++;
            return Sight;
        }
    }

    private GameServer _savedServer;

    [SetUp]
    public void SaveServer()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }

    [TearDown]
    public void RestoreServer() => GameServer.LoadTestDouble(_savedServer);

    private static HeadlessClient Client() =>
        new(new DbAccount { Name = "hdbot-test", Language = "EN", PrivLevel = (int)ePrivLevel.Player });

    // A player made without its constructor (UT_BotCpuMeter.cs:125-130), with its client, name and temp properties.
    private static GamePlayer Player(GameClient client, string name = "Bot")
    {
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(player, client);
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(player, name);
        typeof(GameLiving).GetField("<TempProperties>k__BackingField", Hidden)!.SetValue(player, new PropertyCollection());
        return player;
    }

    private static HeadlessClient InWorldClient(out GamePlayer player)
    {
        HeadlessClient client = Client();
        player = Player(client);
        client.Player = player;
        return client;
    }

    [Test]
    public void A_headless_client_is_a_playerbot_even_when_piloted()
    {
        HeadlessClient client = Client();
        GamePlayer player = Player(client);
        Assert.That(client, Is.InstanceOf<IPlayerBotClient>().And.Not.InstanceOf<BotDummyClient>());
        Assert.That(BotCpuMeter.KindOf(player, _ => true, out GameObject charged, out bool pet), Is.EqualTo(BotCpuKind.Playerbot));
        Assert.That(charged, Is.SameAs(player));
        Assert.That(pet, Is.False);
    }

    [Test]
    public void The_client_is_a_1127_client_with_the_bot_library_and_no_socket()
    {
        HeadlessClient client = Client();
        Assert.That(client.Version, Is.EqualTo(GameClient.eClientVersion.Version1127));
        Assert.That((int)client.ClientType, Is.GreaterThanOrEqualTo(4)); // every region Expansion in the 0.37b world
        Assert.That(client.Out, Is.InstanceOf<HeadlessPacketLib>());
        Assert.That(client.Socket, Is.Null);
        Assert.That(client.PacketProcessor, Is.Null);
        Assert.That(client.HeartbeatEnabled, Is.True);
    }

    [Test]
    public void The_session_id_comes_from_the_servers_allocator()
    {
        FieldInfo field = typeof(BaseServer).GetField("_sessionIdAllocator", Hidden);
        Assert.That(field, Is.Not.Null);
        Assert.That(field!.FieldType, Is.EqualTo(typeof(SessionIdAllocator)));
        field.SetValue(GameServer.Instance, new SessionIdAllocator());
        using SessionId id = HeadlessClient.AllocateSessionId();
        Assert.That((int)id.Value, Is.InRange(1, ushort.MaxValue - 1));
    }

    [Test]
    public void The_character_index_round_trips_through_world_inits_math()
    {
        for (int realm = 1; realm <= 3; realm++)
        {
            for (int slot = 0; slot < 10; slot++)
            {
                int accountSlot = realm * 100 + slot;              // CharacterCreateRequestHandler.cs:439
                byte index = HeadlessClient.CharIndexFor(accountSlot);
                int realmOffset = index - (realm * 10 - 10);      // WorldInitRequestHandler.cs:159-160
                Assert.That(realm * 100 + realmOffset, Is.EqualTo(accountSlot), $"realm {realm} slot {slot}");
            }
        }
    }

    [Test]
    public void A_dialog_answer_reads_back_in_the_handlers_order()
    {
        GSPacketIn packet = HeadlessClient.Packet(
            HeadlessClient.DialogResponseBytes(eDialogCode.QuestSubscribe, 0x1234, 0xBEEF, 1, true));
        // DialogResponseHandler.cs:15-19
        Assert.That(packet.ReadShort(), Is.EqualTo((ushort)0x1234));
        Assert.That(packet.ReadShort(), Is.EqualTo((ushort)0xBEEF));
        Assert.That(packet.ReadShort(), Is.EqualTo((ushort)1));
        Assert.That((eDialogCode)(byte)packet.ReadByte(), Is.EqualTo(eDialogCode.QuestSubscribe));
        Assert.That(packet.ReadByte(), Is.EqualTo(1));
    }

    [Test]
    public void The_library_never_throws_through_IPacketLib()
    {
        IPacketLib lib = Client().Out;
        // BotDummyPacketLib throws in these (bots/BotDummyPacketLib.cs:60-63, :117-120, :366-369).
        Assert.DoesNotThrow(() => lib.SendChangeGroundTarget(1, 2, 3));
        Assert.DoesNotThrow(() => lib.SendRawMessage("x", eChatType.CT_System, eChatLoc.CL_SystemWindow));
        Assert.That(lib.SendLosCheckRequest(null, null, null), Is.False); // not Playing (PacketLib168.cs:1588-1589)
    }

    // G1 (levelmatch/DESIGN.md 1.4, 2.6): BotDummyPacketLib's no-ops left a bot its login skill lists after a level-up or
    // a training. Through IPacketLib the library now rebuilds both caches as PacketLib168 does (PacketLib168.cs:2162-2168,
    // :2324-2330). With no specs a rebuild leaves them empty; the login's lists were not.
    [Test]
    public void Updating_the_skills_rebuilds_the_bots_skill_caches()
    {
        HeadlessClient client = InWorldClient(out GamePlayer player);
        foreach (string name in new[] { "_usableSkillsLock", "_usableListSpellsLock", "_specializationLock" })
            typeof(GamePlayer).GetField(name, Hidden)!.SetValue(player, new System.Threading.Lock());
        typeof(GamePlayer).GetField("m_specialization", Hidden)!.SetValue(player, new Dictionary<string, Specialization>());
        var login = new SpellLine("Login", "Login", "", true);
        List<(Skill, Skill)> usable = [(login, null)];
        List<(SpellLine, List<Skill>)> listSpells = [(login, [])];
        typeof(GamePlayer).GetField("_usableSkills", Hidden)!.SetValue(player, usable);
        typeof(GamePlayer).GetField("_usableListSpells", Hidden)!.SetValue(player, listSpells);

        client.Out.SendNonHybridSpellLines();
        Assert.That(player.GetAllUsableListSpells(), Is.Not.SameAs(listSpells).And.Empty);
        Assert.That(player.GetAllUsableSkills(), Is.SameAs(usable));
        client.Out.SendUpdatePlayerSkills(true);
        Assert.That(player.GetAllUsableSkills(), Is.Not.SameAs(usable).And.Empty);
        Assert.DoesNotThrow(() => Client().Out.SendUpdatePlayerSkills(true));             // no player yet: nothing
        Assert.DoesNotThrow(() => Client().Out.SendNonHybridSpellLines());
    }

    [Test]
    public void A_pooled_packet_sent_to_the_bot_goes_back_to_the_pool()
    {
        // Left issued, the tick pool logs and replaces it at its next use (TickPool.cs:28-33).
        IPacketLib lib = Client().Out;
        GSUDPPacketOut udp = new() { IssuedTimestamp = 1000 };
        GSTCPPacketOut tcp = new() { IssuedTimestamp = 1000 };
        lib.SendUDP(udp);
        lib.SendTCP(tcp);
        Assert.That(udp.IssuedTimestamp, Is.Zero);
        Assert.That(tcp.IssuedTimestamp, Is.Zero);
    }

    [Test]
    public void A_new_custom_dialog_answers_the_pending_one_no()
    {
        HeadlessClient client = InWorldClient(out GamePlayer player);
        int first = -1;
        client.Out.SendCustomDialog("one", (_, response) => first = response);
        client.Out.SendCustomDialog("two", (_, _) => { });
        Assert.That(first, Is.EqualTo(0x00)); // PacketLib168.cs:1562-1567
        Assert.That(player.CustomDialogCallback, Is.Not.Null);
        Assert.That(client.Inbox.TryTakeDialog(out HeadlessInbox.Dialog dialog), Is.True);
        Assert.That(dialog.Text, Is.EqualTo("two"));
    }

    [Test]
    public void A_quest_offer_is_recorded_and_answered_once()
    {
        HeadlessClient client = InWorldClient(out _);
        client.Out.SendQuestSubscribeCommand(null, 42, "Will you help?");
        Assert.That(client.AnswerQuestOffer(true), Is.True);
        Assert.That(client.AnswerQuestOffer(true), Is.False);
        Assert.That(client.PendingAnswers, Is.EqualTo(1));
    }

    [Test]
    public void A_group_invite_sets_the_key_the_dialog_handler_checks()
    {
        HeadlessClient client = InWorldClient(out GamePlayer player);
        GamePlayer leader = Player(Client(), "Leader");
        client.Out.SendGroupInviteCommand(leader, "Join?");
        Assert.That(player.TempProperties.GetProperty<string>(DialogResponseHandler.GROUP_INVITE_KEY), Is.EqualTo("Leader"));
        Assert.That(client.AnswerInvite(true), Is.True);
        Assert.That(client.PendingAnswers, Is.EqualTo(1));
    }

    [Test]
    public void A_player_loaded_after_the_quit_was_posted_never_enters_the_world()
    {
        // The Player setter raises PlayerLoaded (GameClient.cs:73); WorldInit adds only an Inactive player (:26).
        GameEventMgr.AddHandler(GameClientEvent.PlayerLoaded, HeadlessClient.OnPlayerLoaded);
        try
        {
            HeadlessClient quitting = Client();
            typeof(HeadlessClient).GetField("_quitPosted", Hidden)!.SetValue(quitting, 1);
            GamePlayer ghost = Player(quitting);
            ghost.ObjectState = GameObject.eObjectState.Inactive;
            quitting.Player = ghost;
            Assert.That(quitting.QuitPosted, Is.True);
            Assert.That(ghost.ObjectState, Is.EqualTo(GameObject.eObjectState.Deleted));

            HeadlessClient loading = Client();
            GamePlayer bot = Player(loading);
            bot.ObjectState = GameObject.eObjectState.Inactive;
            loading.Player = bot;
            Assert.That(bot.ObjectState, Is.EqualTo(GameObject.eObjectState.Inactive));
        }
        finally
        {
            GameEventMgr.RemoveHandler(GameClientEvent.PlayerLoaded, HeadlessClient.OnPlayerLoaded);
        }
    }

    [Test]
    public void A_region_change_flags_the_client()
    {
        HeadlessClient client = Client();
        Assert.That(client.RegionChangePending, Is.False);
        client.Out.SendRegionChanged();
        Assert.That(client.RegionChangePending, Is.True);
    }

    [Test]
    public void The_navmesh_answers_LoS_and_a_zone_without_a_mesh_sees()
    {
        Zone zone = (Zone)RuntimeHelpers.GetUninitializedObject(typeof(Zone));
        FakeNav nav = new() { Sight = false };
        Assert.That(HeadlessLos.Answer(nav, zone, Vector3.Zero, Vector3.One), Is.False);
        nav.Sight = true;
        Assert.That(HeadlessLos.Answer(nav, zone, Vector3.Zero, Vector3.One), Is.True);
        nav.Mesh = false;
        nav.Sight = false;
        Assert.That(HeadlessLos.Answer(nav, zone, Vector3.Zero, Vector3.One), Is.True); // as GameBots fall back
        nav.Mesh = true;
        nav.Available = false;
        Assert.That(HeadlessLos.Answer(nav, zone, Vector3.Zero, Vector3.One), Is.True);
        Assert.That(HeadlessLos.Answer(nav, null, Vector3.Zero, Vector3.One), Is.True);
        Assert.That(nav.Rays, Is.EqualTo(2));
    }

    [Test]
    public void ClientService_hands_a_headless_client_its_tick_and_sends_nothing()
    {
        HeadlessClient client = Client();
        MethodInfo beginTick = typeof(ClientService).GetMethod("BeginTickInternal", HiddenStatic);
        MethodInfo send = typeof(ClientService).GetMethod("Send", HiddenStatic);
        Assert.That(beginTick, Is.Not.Null);
        Assert.That(send, Is.Not.Null);
        beginTick!.Invoke(null, [client]);
        Assert.That(client.BeginTicks, Is.EqualTo(1));
        Assert.DoesNotThrow(() => send!.Invoke(null, [client])); // upstream's Send reads PacketProcessor, null here
    }
}
