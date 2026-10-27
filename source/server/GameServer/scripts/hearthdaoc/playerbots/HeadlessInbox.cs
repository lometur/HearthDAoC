using System.Collections.Generic;
using System.Threading;
using DOL.GS.PacketHandler;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC player bots (#137): what the server asked a player bot, for its driver to read and answer (HeadlessClient.Answer*).
// The packet library writes it on any game-loop thread; the driver reads it. One lock guards short lists, and the
// oldest entry drops at 16.
public sealed class HeadlessInbox
{
    public const int MaxPerList = 16;

    public sealed record QuestOffer(GameNPC Npc, ushort NpcObjectId, ushort QuestId, string Text, bool Abort, long At);
    public sealed record Invite(GamePlayer Inviter, ushort InviterSessionId, bool Guild, string Text, long At);
    public sealed record Dialog(eDialogCode Code, string Text, ushort Data1, ushort Data2, ushort Data3, bool Question, long At);
    public sealed record QuestWindow(string Kind, GameNPC Npc, object Quest, long At);

    private readonly Lock _lock = new();
    private readonly List<QuestOffer> _questOffers = [];
    private readonly List<Invite> _invites = [];
    private readonly List<Dialog> _dialogs = [];
    private readonly List<QuestWindow> _questWindows = [];

    public void AddQuestOffer(QuestOffer offer) => Add(_questOffers, offer);
    public void AddInvite(Invite invite) => Add(_invites, invite);
    public void AddDialog(Dialog dialog) => Add(_dialogs, dialog);
    public void AddQuestWindow(QuestWindow window) => Add(_questWindows, window);

    public bool TryTakeQuestOffer(out QuestOffer offer) => TryTakeLast(_questOffers, out offer);
    public bool TryTakeInvite(out Invite invite) => TryTakeLast(_invites, out invite);
    public bool TryTakeDialog(out Dialog dialog) => TryTakeLast(_dialogs, out dialog);
    public bool TryTakeQuestWindow(out QuestWindow window) => TryTakeLast(_questWindows, out window);

    public int Count
    {
        get { lock (_lock) return _questOffers.Count + _invites.Count + _dialogs.Count + _questWindows.Count; }
    }

    private void Add<T>(List<T> list, T item)
    {
        lock (_lock)
        {
            if (list.Count == MaxPerList)
                list.RemoveAt(0);
            list.Add(item);
        }
    }

    private bool TryTakeLast<T>(List<T> list, out T item) where T : class
    {
        lock (_lock)
        {
            if (list.Count == 0)
            {
                item = null;
                return false;
            }
            item = list[^1];
            list.RemoveAt(list.Count - 1);
            return true;
        }
    }
}
