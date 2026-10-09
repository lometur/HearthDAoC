using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DOL.Events;

namespace DOL.GS.Quests
{
    /// <summary>
    /// HearthDAoC: a data quest asks "Do you accept?" before it starts (owner 2026-10-09: the player never just clicks a
    /// key word to start a quest; they click it and are then asked whether they want to start the quest or questline).
    /// Whispering a Standard quest's AcceptText to its giver sends the client's quest prompt and remembers a pending
    /// offer for the player; the answer (<c>GamePlayerEvent.AcceptQuest</c> or <c>DeclineQuest</c>) starts the quest or
    /// drops the offer. Upstream started the quest at once (about 1,300 classic quests).
    /// </summary>
    public static class DataQuestOffers
    {
        /// <summary>The quest ID sent with the prompt. <c>QuestMgr.RegisterQuestType</c> counts up from 1 and every
        /// scripted quest's AcceptQuest handler ignores IDs that aren't its own, so a value far above any registered
        /// type can't be taken for a scripted quest's prompt. One ID serves every data quest: the pending offer says which.</summary>
        public const ushort OfferQuestId = 0xFFF0;

        /// <summary>How long an offer can be answered.</summary>
        public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(5);

        public const string DeclineText = "Come back when you're ready.";

        /// <summary>The prompt text: <c>&lt;NPC&gt; offers you the quest "&lt;quest name&gt;". Do you accept?</c></summary>
        public static string PromptText(string npcName, string questName)
            => $"{npcName} offers you the quest \"{questName}\". Do you accept?";

        /// <summary>True when an answer belongs to the pending offer: it carries our quest ID, comes from the NPC that
        /// made the offer, and the offer isn't older than <see cref="OfferLifetime"/> (<paramref name="now"/> is
        /// <paramref name="offeredAt"/> or later; a clock that went back counts as not expired).</summary>
        public static bool AnswerMatches(ushort answeredQuestId, bool sameNpc, DateTime offeredAt, DateTime now)
            => answeredQuestId == OfferQuestId && sameNpc && now - offeredAt <= OfferLifetime;

        public static bool IsExpired(DateTime offeredAt, DateTime now) => now - offeredAt > OfferLifetime;

        private sealed class Offer
        {
            public DataQuest Quest;
            public GameNPC Npc;
            public DateTime OfferedAt;
        }

        private static readonly ConcurrentDictionary<GamePlayer, Offer> s_pending = new();

        /// <summary>Asks <paramref name="player"/> whether to start <paramref name="quest"/> from <paramref name="npc"/>.
        /// A newer offer replaces an older one.</summary>
        public static void Offer(GamePlayer player, GameNPC npc, DataQuest quest)
        {
            DateTime now = DateTime.UtcNow;
            Prune(now);
            s_pending[player] = new Offer { Quest = quest, Npc = npc, OfferedAt = now };
            player.Out.SendQuestSubscribeCommand(npc, OfferQuestId, PromptText(npc.GetName(0, true), quest.Name));
        }

        private static void Prune(DateTime now)
        {
            foreach (KeyValuePair<GamePlayer, Offer> pair in s_pending.ToArray())
            {
                if (IsExpired(pair.Value.OfferedAt, now))
                    s_pending.TryRemove(pair);
            }
        }

        [GameServerStartedEvent]
        public static void OnServerStart(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.AddHandler(GamePlayerEvent.AcceptQuest, OnAcceptQuest);
            GameEventMgr.AddHandler(GamePlayerEvent.DeclineQuest, OnDeclineQuest);
            GameEventMgr.AddHandler(GamePlayerEvent.Quit, OnPlayerQuit);
        }

        [GameServerStoppedEvent]
        public static void OnServerStop(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GamePlayerEvent.AcceptQuest, OnAcceptQuest);
            GameEventMgr.RemoveHandler(GamePlayerEvent.DeclineQuest, OnDeclineQuest);
            GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, OnPlayerQuit);
            s_pending.Clear();
        }

        private static void OnPlayerQuit(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer player)
                s_pending.TryRemove(player, out _);
        }

        /// <summary>The player's pending offer when <paramref name="answer"/> belongs to it, else null.</summary>
        private static Offer Find(QuestEventArgs answer, bool ignoreAge)
        {
            GamePlayer player = answer?.Player;
            if (player == null || answer.QuestID != OfferQuestId || !s_pending.TryGetValue(player, out Offer offer))
                return null;

            DateTime now = DateTime.UtcNow;
            bool matches = AnswerMatches(answer.QuestID, ReferenceEquals(answer.Source, offer.Npc), offer.OfferedAt, ignoreAge ? offer.OfferedAt : now);
            return matches ? offer : null;
        }

        private static void OnAcceptQuest(DOLEvent e, object sender, EventArgs args)
        {
            if (args is not QuestEventArgs answer)
                return;

            Offer offer = Find(answer, false);

            // Removing the very offer found is the one step that wins when two answers arrive in one tick (issue #92).
            if (offer == null || !s_pending.TryRemove(new KeyValuePair<GamePlayer, Offer>(answer.Player, offer)))
                return;

            GamePlayer player = answer.Player;
            if (!player.IsAlive || !offer.Quest.CheckQuestQualification(player))
                return;

            offer.Quest.StartOffered(player, offer.Npc);
        }

        private static void OnDeclineQuest(DOLEvent e, object sender, EventArgs args)
        {
            if (args is not QuestEventArgs answer)
                return;

            // A decline drops the offer even when it is old; the NPC speaks only if there was an offer to drop.
            Offer offer = Find(answer, true);
            if (offer == null || !s_pending.TryRemove(new KeyValuePair<GamePlayer, Offer>(answer.Player, offer)))
                return;

            offer.Npc.SayTo(answer.Player, eChatLoc.CL_PopupWindow, DeclineText);
        }
    }
}
