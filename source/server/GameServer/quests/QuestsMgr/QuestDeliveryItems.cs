using System;
using System.Collections.Generic;

namespace DOL.GS.Quests
{
    /// <summary>
    /// HearthDAoC: whether a data quest hands a Deliver or DeliverFinish step's item when that step begins.
    /// Upstream always did, so a quest that had already given the item (an earlier step, or the step just finishing)
    /// gave a second copy ("Traveler's Way -- Supply Run" gave two bundles of supplies; 77 classic quests do this).
    /// Item template ids compare without case, as <c>DataQuest.OnPlayerGiveItem</c> compares them.
    /// </summary>
    public static class QuestDeliveryItems
    {
        /// <summary>True when the player should be handed <paramref name="template"/> now: it is not empty, none is among
        /// the backpack's item ids (<paramref name="carried"/>), and the step just finishing isn't handing it
        /// (<paramref name="beingHanded"/>). A player who lost or destroyed it gets a new one.</summary>
        public static bool ShouldHand(string template, IEnumerable<string> carried, IEnumerable<string> beingHanded)
        {
            string wanted = template?.Trim();
            if (string.IsNullOrEmpty(wanted))
                return false;

            return !Contains(carried, wanted) && !Contains(beingHanded, wanted);
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
