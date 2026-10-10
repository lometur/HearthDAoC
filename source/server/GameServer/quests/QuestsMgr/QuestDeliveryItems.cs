using System;
using System.Collections.Generic;
using System.Globalization;

namespace DOL.GS.Quests
{
    /// <summary>
    /// HearthDAoC: whether a data quest hands a Deliver or DeliverFinish step's item when that step begins.
    /// Upstream always did, so a quest that had already given the item (an earlier step, or the step just finishing)
    /// gave a second copy ("Traveler's Way -- Supply Run" gave two bundles of supplies; 77 classic quests do this).
    /// Item template ids compare without case, as <c>DataQuest.OnPlayerGiveItem</c> compares them.
    /// Accepting a quest whose first step is a delivery hands that step's item too (<see cref="FirstStepItem"/>): upstream
    /// never did, because nothing "begins" step 1, so 56 classic quests could not be finished. A first delivery back to
    /// the giver hands nothing: the giver wants the player to bring the item (11 more classic quests).
    /// A turn-in can need several items: a CollectItemTemplate entry "id;N" (<see cref="CollectEntry"/>,
    /// <see cref="OthersToTake"/>).
    /// </summary>
    public static class QuestDeliveryItems
    {
        /// <summary>True when the player should be handed <paramref name="template"/> now: it is not empty, none is among
        /// the carried item ids (<paramref name="carried"/>, from <see cref="CarriedIds"/>: the copy being handed over
        /// doesn't count), and the step just finishing isn't handing it (<paramref name="beingHanded"/>). A player who
        /// lost or destroyed it gets a new one.</summary>
        public static bool ShouldHand(string template, IEnumerable<string> carried, IEnumerable<string> beingHanded)
        {
            string wanted = template?.Trim();
            if (string.IsNullOrEmpty(wanted))
                return false;

            return !Contains(carried, wanted) && !Contains(beingHanded, wanted);
        }

        /// <summary>The ids of the backpack's items that count as carried while a step advances, from (id, count, copies
        /// being handed over) entries: an item counts unless the hand-over takes all its copies. The item a player hands
        /// over is still in the backpack while its step advances (<c>GamePlayerEvent.GiveItem</c> fires before the move,
        /// and <c>DataQuest.OnPlayerGiveItem</c> removes it after); counting it made a step that hands the same item
        /// back hand nothing, so the player had none ("Path of the Renegade": Omis writes between the lines of the
        /// Arawnite orders and gives them back; 62 classic quests). An item counts for the copies that stay after the
        /// hand-over, and for a data quest none stay: <c>OnPlayerGiveItem</c> takes the whole item with
        /// <c>AbstractQuest.RemoveItem(GameObject, GamePlayer, DbInventoryItem, bool)</c> (a stack goes whole), so it
        /// passes the item's count as handed over and the handed-over entry is never carried, whatever its count. A
        /// stack can't even reach it from an NPC: <c>PlayerMoveItemRequestHandler</c> fires GiveItem for an NPC only
        /// when the item's count is 1.</summary>
        public static List<string> CarriedIds(IEnumerable<(string Id, int Count, int HandedOver)> backpack)
        {
            List<string> ids = new();
            if (backpack == null)
                return ids;

            foreach ((string id, int count, int handedOver) in backpack)
            {
                if (handedOver <= 0 || count > handedOver)
                    ids.Add(id);
            }

            return ids;
        }

        /// <summary>The item template accepting a quest should hand for its first step, or null for none: the first step is
        /// a delivery (<paramref name="firstStepIsDelivery"/>), the quest's StepItemTemplates have an entry for it, the
        /// step's target (<paramref name="firstTargetName"/>, TargetName's first entry; the part before ';' counts) isn't
        /// the giver (<paramref name="giverName"/>; names compare without case), and <see cref="ShouldHand"/> agrees (the
        /// player doesn't carry one). The template is returned trimmed.</summary>
        public static string FirstStepItem(bool firstStepIsDelivery, IReadOnlyList<string> stepItemTemplates, IEnumerable<string> carried,
            string firstTargetName, string giverName)
        {
            if (!firstStepIsDelivery || stepItemTemplates == null || stepItemTemplates.Count == 0)
                return null;

            // A first delivery back to the giver is an item the giver wants the player to bring: 20098 "Sveabone Hilt
            // Sword" says "Run along and buy me a bronze short sword"; "Seek the Moonstone C" and "An End to the Daggers"
            // want what an earlier quest gave.
            if (IsGiver(firstTargetName, giverName))
                return null;

            string template = stepItemTemplates[0]?.Trim();
            return ShouldHand(template, carried, null) ? template : null;
        }

        /// <summary>A CollectItemTemplate entry's item id and the number of items its step needs. "id;N" with N a whole
        /// number of 2 or more needs N items of id (both parts trimmed): an NPC is only ever handed one item, so a step
        /// that wants two non-stacking stones needs a count (level 11 "Entry Into Tomorrow": Omis cuts Frund's and
        /// Agisthil's stones into the Crediac). Any other entry ("id", "id;1", "id;0", "id;x", ";2") is the id as it
        /// is, needing one, exactly as upstream reads it.</summary>
        public static (string Id, int Needed) CollectEntry(string entry)
        {
            int semicolon = entry?.LastIndexOf(';') ?? -1;
            if (semicolon >= 0
                && int.TryParse(entry[(semicolon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int needed)
                && needed >= 2)
            {
                string id = entry[..semicolon].Trim();
                if (id.Length > 0)
                    return (id, needed);
            }

            return (entry, 1);
        }

        /// <summary>True when an item (its Id_nb) is a collect entry's item: upstream's rule in
        /// <c>DataQuest.OnPlayerGiveItem</c>, the lowercased item id contains the lowercased entry id. An empty id is
        /// no item.</summary>
        public static bool IsCollectItem(string itemId, string id)
            => !string.IsNullOrEmpty(id) && itemId != null && itemId.ToLower().Contains(id.ToLower());

        /// <summary>The copies to take from each of the backpack's other items (<paramref name="others"/>: id and count,
        /// without the item handed over) for a step that needs <paramref name="needed"/> items of <paramref name="id"/>,
        /// or null when the player has too few: then nothing advances and nothing is taken. The item handed over counts
        /// for <paramref name="handedCount"/> copies (at least one); the others count for their copies (at least one each)
        /// when <see cref="IsCollectItem"/> matches them, and are taken in order, a stack only in part when fewer copies
        /// are wanted.</summary>
        public static int[] OthersToTake(IReadOnlyList<(string Id, int Count)> others, string id, int needed, int handedCount)
        {
            int[] take = new int[others?.Count ?? 0];
            int wanted = needed - Math.Max(handedCount, 1);
            for (int i = 0; i < take.Length && wanted > 0; i++)
            {
                if (!IsCollectItem(others[i].Id, id))
                    continue;

                take[i] = Math.Min(wanted, Math.Max(others[i].Count, 1));
                wanted -= take[i];
            }

            return wanted > 0 ? null : take;
        }

        private static bool IsGiver(string targetName, string giverName)
        {
            string target = targetName?.Split(';')[0].Trim();
            return !string.IsNullOrEmpty(target) && string.Equals(target, giverName?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(IEnumerable<string> ids, string wanted)
        {
            if (ids == null)
                return false;

            foreach (string id in ids)
            {
                if (string.Equals(id?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
