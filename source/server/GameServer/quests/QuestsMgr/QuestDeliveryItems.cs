using System;
using System.Collections.Generic;

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
