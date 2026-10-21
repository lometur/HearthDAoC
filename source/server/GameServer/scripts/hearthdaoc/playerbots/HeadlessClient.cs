using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.Logging;
using DOL.Network;

namespace DOL.GS.HearthDAoC.PlayerBots;

public enum HeadlessStep { None, Registering, LoadingCharacter, Reentering, AwaitPlayerInit, InWorld, LoggingOut }

// HearthDAoC #137 spike: a player bot's client. A real GameClient with a real account, a real session id and the bot
// packet library as Out, but no socket and no packet processor. ClientService's two marked blocks skip its receive,
// ping, idle and send work and call OnClientServiceBeginTick instead, where it feeds the login handlers the packet
// bodies a client sends (world init, game open, player init), handles the answers its library queued (LoS, dialogs),
// and keeps its player alive. It marks itself IPlayerBotClient, so the CPU meter counts its player as playerbot.
public sealed class HeadlessClient : GameClient, IPlayerBotClient
{
    private static readonly Logger Log = LoggerManager.Create(typeof(HeadlessClient));

    public const eClientVersion BotVersion = eClientVersion.Version1127;         // FORK.md:60, the owner's client
    public const eClientType BotClientType = eClientType.LabyrinthOfTheMinotaur; // >= every region Expansion (jump.cs:433)
    public const int LoginTimeoutMs = 60000;
    // Same interval as PlayerBotPathMover.IdleBroadcastMilliseconds (est.; PlayerMovementComponent.cs:15 implies < 5 s).
    public const int HeartbeatMs = 1500;
    public const int ReapAfterMs = 10000;

    private static readonly FieldInfo SessionIdAllocatorField =
        typeof(BaseServer).GetField("_sessionIdAllocator", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly WorldInitRequestHandler WorldInit = new();
    private static readonly GameOpenRequestHandler GameOpen = new();
    private static readonly PlayerInitRequestHandler PlayerInit = new();
    private static readonly DialogResponseHandler DialogResponse = new();

    private readonly ConcurrentQueue<(ushort Source, ushort Target, LosCheckResponse Answer)> _losAnswers = new();
    private readonly ConcurrentQueue<byte[]> _dialogAnswers = new();
    private byte _charIndex;
    private int _quitPosted;
    private volatile bool _regionChangePending;
    private volatile bool _heartbeatEnabled = true;
    private volatile string _logoutReason = "none";
    private long _nextHeartbeat;

    public HeadlessStep Step { get; private set; }
    public long StepSince { get; private set; }
    public long LastBeginTick { get; private set; }
    public long BeginTicks { get; private set; }
    public long Heartbeats { get; private set; }
    public int Entries { get; private set; }
    public string LogoutReason => _logoutReason;
    public HeadlessInbox Inbox { get; } = new();
    public bool RegionChangePending => _regionChangePending;
    public int PendingAnswers => _losAnswers.Count + _dialogAnswers.Count;

    // The client's own position heartbeat. PlayerBotDriver.Start turns it off (its path mover re-broadcasts an idle bot
    // through the movement component instead) and PlayerBotDriver.Stop turns it back on: one broadcaster per bot.
    public bool HeartbeatEnabled { get => _heartbeatEnabled; set => _heartbeatEnabled = value; }

    // Raised on the game loop (ClientService.BeginTick) after player init: first = true at login, false after a region change.
    public event Action<HeadlessClient, bool> EnteredWorld;
    // Raised on the game loop (a ClientService posted action) once the client is Disconnected.
    public event Action<HeadlessClient> LoggedOut;

    public HeadlessClient(DbAccount account) : base((Socket)null)
    {
        ArgumentNullException.ThrowIfNull(account);
        Account = account; // GameClient.cs:25-34: custom params, AccountLoaded (as BotDummyClient does)
        Out = new HeadlessPacketLib(this);
        Version = BotVersion;
        ClientType = BotClientType;
    }

    // Once, from any thread. Registration goes through ClientService's posted actions (GameClient.OnConnect,
    // GameClient.cs:461-471), and the login runs over the next ticks.
    public void BeginLogin(DbCoreCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (Step != HeadlessStep.None)
            throw new InvalidOperationException($"{Account.Name}: BeginLogin called twice");
        if (Account.Characters == null || !Account.Characters.Any(c => c != null && c.AccountSlot == character.AccountSlot))
            throw new ArgumentException($"{character.Name} (slot {character.AccountSlot}) is not on account {Account.Name}", nameof(character));

        // A real client's character overview sets the realm (CharacterOverviewRequestHandler.cs:71, :135); WorldInit's
        // slot math reads it (WorldInitRequestHandler.cs:159-160).
        Account.Realm = character.AccountSlot / 100;
        _charIndex = CharIndexFor(character.AccountSlot);
        LastBeginTick = GameLoop.GameLoopTime; // NeedsReap stays false until the first tick
        SetStep(HeadlessStep.Registering);
        ClientState = eClientState.CharScreen; // WorldInit accepts CharScreen or Playing (WorldInitRequestHandler.cs:15)
        OnConnect(AllocateSessionId());
    }

    // The inverse of WorldInitRequestHandler.cs:159-160 for AccountSlot = Realm*100 + slot (CharacterCreateRequestHandler.cs:439).
    public static byte CharIndexFor(int accountSlot) => (byte)(accountSlot % 100 + accountSlot / 100 * 10 - 10);

    public static SessionId AllocateSessionId()
    {
        SessionIdAllocator allocator = (SessionIdAllocator)SessionIdAllocatorField.GetValue(GameServer.Instance);
        SessionId id = new(allocator);
        if (id.Value != ushort.MaxValue)
            return id;
        // ClientService's table is ushort.MaxValue long (ClientService.cs:26): 65535 would throw in OnClientConnect.
        SessionId next = new(allocator);
        id.Dispose();
        return next;
    }

    // ClientService.BeginTickInternal's marked block, once per tick, on a ClientService worker. Never throws: an
    // exception there would drop the client from the store (GameServiceUtils.cs:27-28) and nothing would tick it again.
    internal void OnClientServiceBeginTick()
    {
        try
        {
            BeginTicks++;
            LastBeginTick = GameLoop.GameLoopTime;
            BeginTickCore();
        }
        catch (Exception e)
        {
            Log.Error($"PLAYER_BOT error {Account?.Name} step={Step}", e);
            Logout("error");
        }
    }

    private void BeginTickCore()
    {
        long now = GameLoop.GameLoopTime;
        GamePlayer player = Player;

        // What a client's packets do for a real one: the soft link-death clock (PlayerMovementComponent.cs:58-59, 5 s),
        // which no packet resets here, and the ping time.
        PingTime = now;
        if (player != null)
            player.LastPositionUpdatePacketReceivedTime = now;

        switch (Step)
        {
            case HeadlessStep.None:
            case HeadlessStep.LoggingOut:
                return;
            case HeadlessStep.InWorld when ClientState is eClientState.CharScreen:
                Logout("kicked"); // GameServiceUtils.KickPlayerToCharScreen (GameServiceUtils.cs:73-87)
                return;
        }

        if (Step is not HeadlessStep.InWorld && now - StepSince > LoginTimeoutMs)
        {
            Logout($"login-timeout step={Step}");
            return;
        }

        switch (Step)
        {
            case HeadlessStep.Registering:
                SetStep(HeadlessStep.LoadingCharacter);
                // async void: awaits LoadPlayer (GameClient.cs:112-169); its continuation is posted back to ClientService
                // (GameLoopSynchronizationContext.cs:14-27), then WorldEnter (:29) and AddToWorld (:97-106).
                WorldInit.HandlePacket(this, Packet(_charIndex));
                return;
            case HeadlessStep.LoadingCharacter:
            case HeadlessStep.Reentering:
                if (ClientState is eClientState.WorldEnter && player?.ObjectState is GameObject.eObjectState.Active)
                {
                    SetStep(HeadlessStep.AwaitPlayerInit);
                    GameOpen.HandlePacket(this, Packet(0)); // WorldEnter -> Playing (GameOpenRequestHandler.cs:8-12)
                }
                return;
            case HeadlessStep.AwaitPlayerInit:
                if (ClientState is eClientState.Playing)
                {
                    bool first = Entries == 0;
                    Entries++;
                    SetStep(HeadlessStep.InWorld);
                    PlayerInit.HandlePacket(this, Packet()); // GameEntered or RegionChanged (PlayerInitRequestHandler.cs:37-51)
                    _nextHeartbeat = now;
                    if (Log.IsInfoEnabled)
                        Log.Info($"PLAYER_BOT {(first ? "login" : "reenter")} {Account.Name} {Player?.Name} region={Player?.CurrentRegionID} session={SessionID}");
                    EnteredWorld?.Invoke(this, first);
                }
                return;
            case HeadlessStep.InWorld:
                if (_regionChangePending)
                {
                    // GamePlayer.MoveTo removed the player (GamePlayer.cs:7953) and called SendRegionChanged (:7983).
                    if (ClientState is eClientState.Playing && player?.ObjectState is GameObject.eObjectState.Inactive)
                    {
                        _regionChangePending = false;
                        SetStep(HeadlessStep.Reentering);
                        WorldInit.HandlePacket(this, Packet(_charIndex)); // from Playing: no load (WorldInitRequestHandler.cs:19)
                    }
                    return;
                }

                DrainAnswers(player);

                if (_heartbeatEnabled && ClientState is eClientState.Playing &&
                    player?.ObjectState is GameObject.eObjectState.Active && now >= _nextHeartbeat)
                {
                    // Players are not sent by UpdateWorld (ClientService.cs:808); others see a player through its
                    // position broadcasts, as the pilot does it (AutonomousPlayerPilot.cs:546).
                    _nextHeartbeat = now + HeartbeatMs;
                    Heartbeats++;
                    PlayerPositionUpdateHandler.BroadcastPosition(this);
                }
                return;
        }
    }

    private void DrainAnswers(GamePlayer player)
    {
        if (player == null || ClientState is not eClientState.Playing)
            return;
        // Same stage as a real answer (CheckLosResponseHandler.cs:13): TimerService is not ticking (LosCheckHandler.cs:42-46).
        while (_losAnswers.TryDequeue(out (ushort Source, ushort Target, LosCheckResponse Answer) los))
            player.LosCheckHandler.HandleLosResponse(los.Source, los.Target, los.Answer);
        while (_dialogAnswers.TryDequeue(out byte[] body))
            DialogResponse.HandlePacket(this, Packet(body));
    }

    internal void QueueLosAnswer(ushort source, ushort target, LosCheckResponse answer) => _losAnswers.Enqueue((source, target, answer));

    internal void OnRegionChanged() => _regionChangePending = true;

    // The bytes a dialog click sends: data1, data2, data3 (big-endian shorts), code, response (DialogResponseHandler.cs:15-19).
    public static byte[] DialogResponseBytes(eDialogCode code, ushort data1, ushort data2, ushort data3, bool yes) =>
    [
        (byte)(data1 >> 8), (byte)data1,
        (byte)(data2 >> 8), (byte)data2,
        (byte)(data3 >> 8), (byte)data3,
        (byte)code, (byte)(yes ? 0x01 : 0x00),
    ];

    // Handled at this client's next ClientService.BeginTick (DrainAnswers), the stage a real click is handled in.
    public void AnswerDialog(eDialogCode code, ushort data1, ushort data2, ushort data3, bool yes) =>
        _dialogAnswers.Enqueue(DialogResponseBytes(code, data1, data2, data3, yes));

    // PacketLib168.cs:1557-1584: data2 = 0x01 marks a custom dialog (DialogResponseHandler.cs:27).
    public void AnswerCustomDialog(bool yes) => AnswerDialog(eDialogCode.CustomDialog, SessionId?.Value ?? 0, 0x01, 0, yes);

    // PacketLib168.cs:1497-1535: data1 = quest id, data2 = NPC object id, data3 = 1 for an abort question (handler :102-115).
    public bool AnswerQuestOffer(bool yes)
    {
        if (!Inbox.TryTakeQuestOffer(out HeadlessInbox.QuestOffer offer))
            return false;
        AnswerDialog(eDialogCode.QuestSubscribe, offer.QuestId, offer.NpcObjectId, (ushort)(offer.Abort ? 1 : 0), yes);
        return true;
    }

    // PacketLib168.cs:1416-1451: data1 = the inviter's session id; the handler checks the key the library set (:58, :131).
    public bool AnswerInvite(bool yes)
    {
        if (!Inbox.TryTakeInvite(out HeadlessInbox.Invite invite))
            return false;
        AnswerDialog(invite.Guild ? eDialogCode.GuildInvite : eDialogCode.GroupInvite, invite.InviterSessionId, 0, 0, yes);
        return true;
    }

    // A client packet's body as the handlers read it: GSPacketIn is a MemoryStream (PacketIn.cs:10), read from 0.
    public static GSPacketIn Packet(params byte[] body)
    {
        GSPacketIn packet = new();
        packet.Write(body, 0, body.Length);
        packet.Position = 0;
        return packet;
    }

    // For the spawner's loop: the client is out of ClientService's store (GameServiceUtils.cs:27-28 drops a client
    // whose own tick threw), so nothing ticks it. The spawner then calls Logout("reaped").
    public bool NeedsReap(long now) =>
        Step is not HeadlessStep.None and not HeadlessStep.LoggingOut
        && ClientState is not eClientState.Disconnected
        && !ServiceObjectId.IsSet && !ServiceObjectId.IsPendingAddition
        && now - LastBeginTick > ReapAfterMs;

    public void Logout(string reason)
    {
        _logoutReason = reason;
        Disconnect(); // BaseClient.cs:176-193: OnDisconnect (below), then CloseSocket, which returns on a null socket
    }

    // Replaces GameClient.OnDisconnect (GameClient.cs:414-459), which starts a link death for a Playing client.
    protected override void OnDisconnect()
    {
        if (ClientState is eClientState.Disconnected || Interlocked.Exchange(ref _quitPosted, 1) != 0)
            return;
        ClientService.Instance.Post(static client => client.QuitOnGameLoop(), this);
    }

    private void QuitOnGameLoop()
    {
        if (ClientState is eClientState.Disconnected)
            return;
        SetStep(HeadlessStep.LoggingOut);
        GamePlayer player = Player;
        try
        {
            if (player != null && player.ObjectState is GameObject.eObjectState.Active)
            {
                CraftingProgressMgr.FlushAndSaveInstance(player); // as the link-death end does (GamePlayer.cs:1141-1142)
                player.SaveIntoDatabase();
            }
        }
        catch (Exception e)
        {
            Log.Error($"PLAYER_BOT save failed {Account?.Name}", e);
        }
        finally
        {
            if (SessionId == null)
                ClientState = eClientState.Disconnected; // never registered: GameClient.Quit reads SessionId.Value
            else
                LinkDeathQuit(); // GameClient.cs:209-272: Player.Quit(true), Disconnected, OnClientDisconnect, frees the id, saves the account
            if (Log.IsInfoEnabled)
                Log.Info($"PLAYER_BOT logout {Account?.Name} {player?.Name} reason={_logoutReason}");
            LoggedOut?.Invoke(this);
        }
    }

    private void SetStep(HeadlessStep step)
    {
        Step = step;
        StepSince = GameLoop.GameLoopTime;
    }
}
