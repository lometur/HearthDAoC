using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.Quests;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC #137 spike: the player bot's Out. BotDummyPacketLib (bots/BotDummyPacketLib.cs) is a no-op IPacketLib with
// no virtual members, so this class lists IPacketLib again. C# maps the interface to the `new` members below and, for
// every other member, to BotDummyPacketLib's (interface re-implementation). Every caller reaches it as IPacketLib
// (GameClient.Out, GameClient.cs:90). Beyond the no-ops it: answers LoS checks from the navmesh; keeps the custom
// dialog callback as PacketLib168 does; sets the invite keys the dialog handler checks; records quest offers and
// dialogs for the driver; tells the client about a region change.
public sealed class HeadlessPacketLib : BotDummyPacketLib, IPacketLib
{
    private static readonly Logger Log = LoggerManager.Create(typeof(HeadlessPacketLib));

    [System.ThreadStatic] private static bool t_packetSent;

    private readonly HeadlessClient _client;

    public HeadlessPacketLib(HeadlessClient client) => _client = client;

    // PacketLib168.cs:1586-1592, then the client's answer. StartLosCheck sends its packet only when no check of the
    // same pair is pending or cached (LosCheckHandler.cs:50-109). For that case the navmesh answers now, and the answer
    // is handled at the client's next ClientService.BeginTick. Never synchronous: StandardMobBrain counts the check as
    // started only after this returns (StandardMobBrain.cs:178-179).
    public new bool SendLosCheckRequest(GameObject source, GameObject target, ILosCheckListener listener)
    {
        GamePlayer player = _client.Player;
        if (_client.ClientState is not GameClient.eClientState.Playing || player == null)
            return false;

        t_packetSent = false;
        bool started = player.LosCheckHandler.StartLosCheck(source, target, listener);
        if (started && t_packetSent)
        {
            bool sight = HeadlessLos.Answer(PathfindingProvider.Instance, source, target);
            _client.QueueLosAnswer(source.ObjectID, target.ObjectID, sight ? LosCheckResponse.True : LosCheckResponse.False);
        }
        t_packetSent = false;
        return started;
    }

    // Marks that StartLosCheck sent its request (LosCheckHandler.cs:102-107). Read only around that call, on this thread.
    // A sent packet goes back to the tick pool, as for a client without a socket (PacketProcessor.cs:217-220, :240-244):
    // left issued, the pool logs a warning and allocates a new one at its next use (TickPool.cs:28-33). The other
    // players' position and heading updates reach a bot here (PlayerPositionUpdateHandler.cs:562-577): 380,000 warnings
    // in a one-hour run of 30 bots.
    public new void SendTCP(GSTCPPacketOut packet)
    {
        t_packetSent = true;
        packet.ReleasePooledObject();
    }

    public new void SendUDP(GSUDPPacketOut packet) => packet.ReleasePooledObject();

    // PacketLib168.cs:1557-1567: a new dialog first answers a pending one with 0x00, under the player's lock (#92).
    public new void SendCustomDialog(string msg, CustomDialogResponse callback)
    {
        GamePlayer player = _client.Player;
        if (player == null)
            return;
        lock (player)
        {
            player.CustomDialogCallback?.Invoke(player, 0x00);
            player.CustomDialogCallback = callback;
        }
        _client.Inbox.AddDialog(new(eDialogCode.CustomDialog, msg, 0, 0x01, 0, callback != null, GameLoop.GameLoopTime));
        Note($"custom-dialog \"{Trim(msg)}\"");
    }

    public new void SendDialogBox(eDialogCode code, ushort data1, ushort data2, ushort data3, ushort data4, eDialogType type, bool autoWrapText, string message)
    {
        _client.Inbox.AddDialog(new(code, message, data1, data2, data3, type != eDialogType.Ok, GameLoop.GameLoopTime));
        Note($"dialog code={code} \"{Trim(message)}\"");
    }

    public new void SendQuestSubscribeCommand(GameNPC invitingNPC, ushort questid, string inviteMessage) => Offer(invitingNPC, questid, inviteMessage, false);
    public new void SendQuestAbortCommand(GameNPC abortingNPC, ushort questid, string abortMessage) => Offer(abortingNPC, questid, abortMessage, true);

    private void Offer(GameNPC npc, ushort questId, string text, bool abort)
    {
        _client.Inbox.AddQuestOffer(new(npc, npc?.ObjectID ?? 0, questId, text, abort, GameLoop.GameLoopTime));
        Note($"quest-{(abort ? "abort" : "offer")} quest={questId} npc={npc?.Name}");
    }

    // Recorded only. A window's answer is not a dialog response; nothing in the spike answers one.
    public new void SendQuestOfferWindow(GameNPC questNPC, GamePlayer player, RewardQuest quest) => Window("offer", questNPC, quest);
    public new void SendQuestOfferWindow(GameNPC questNPC, GamePlayer player, DataQuest quest) => Window("offer", questNPC, quest);
    public new void SendQuestRewardWindow(GameNPC questNPC, GamePlayer player, RewardQuest quest) => Window("reward", questNPC, quest);
    public new void SendQuestRewardWindow(GameNPC questNPC, GamePlayer player, DataQuest quest) => Window("reward", questNPC, quest);

    private void Window(string kind, GameNPC npc, object quest)
    {
        _client.Inbox.AddQuestWindow(new(kind, npc, quest, GameLoop.GameLoopTime));
        Note($"quest-window {kind} npc={npc?.Name}");
    }

    // PacketLib168.cs:1432 and :1451: the handler accepts only the inviter named under this key (DialogResponseHandler.cs:58, :131).
    public new void SendGroupInviteCommand(GamePlayer invitingPlayer, string inviteMessage) => Invite(invitingPlayer, inviteMessage, false);
    public new void SendGuildInviteCommand(GamePlayer invitingPlayer, string inviteMessage) => Invite(invitingPlayer, inviteMessage, true);

    private void Invite(GamePlayer inviter, string text, bool guild)
    {
        GamePlayer player = _client.Player;
        if (player == null || inviter == null)
            return;
        player.TempProperties.SetProperty(guild ? DialogResponseHandler.GUILD_INVITE_KEY : DialogResponseHandler.GROUP_INVITE_KEY, inviter.Name);
        _client.Inbox.AddInvite(new(inviter, inviter.Client?.SessionId?.Value ?? 0, guild, text, GameLoop.GameLoopTime));
        Note($"{(guild ? "guild" : "group")}-invite from={inviter.Name}");
    }

    // GamePlayer.MoveTo to another region ends here (GamePlayer.cs:7983). A real client then sends world init, game
    // open and player init again, and so does the client, from its next BeginTick.
    public new void SendRegionChanged() => _client.OnRegionChanged();

    // The other two members BotDummyPacketLib throws in (bots/BotDummyPacketLib.cs:60-63, :366-369).
    public new void SendChangeGroundTarget(int x, int y, int z) { }
    public new void SendRawMessage(string msg, eChatType type, eChatLoc loc) { }

    // DEBUG would be quieter at scale; INFO keeps the spike's run-1 checks readable.
    private void Note(string what)
    {
        if (Log.IsInfoEnabled)
            Log.Info($"PLAYER_BOT inbox {_client.Account?.Name} {what}");
    }

    private static string Trim(string text) =>
        text == null ? "" : (text.Length <= 60 ? text : text[..60] + "...").Replace('\n', ' ');
}
