using System;

namespace DOL.GS.Quests
{
    /// <summary>
    /// HearthDAoC: how a data quest's names (a step's TargetName, the quest's StartName) match the name of a monster,
    /// NPC or object: without case (ordinal). Upstream compared them exactly, but its quest data spells many names
    /// differently from the names they spawn with: level 20 "Path of the Renegade" wants "Arawnite Messenger", whose
    /// NpcTemplate 12071 ("arawnite messenger", ReplaceMobValues) names him at every spawn, so his kill never counted
    /// (owner test 2026-10-10). On the clean 0.35 world 25 step targets (103 quests, such as "Cornwall hunter",
    /// "Fanged Sinach", "Elder Tidal Sheerie" and "witch") and one giver ("Albion Runner") exist only under other
    /// capitals.
    /// </summary>
    public static class QuestNames
    {
        /// <summary>True when a quest's name and an object's name are the same but for case (ordinal, so the server's
        /// culture doesn't matter). Spacing and punctuation still count; null matches only null, as upstream's ==
        /// did.</summary>
        public static bool Same(string questName, string objectName)
            => string.Equals(questName, objectName, StringComparison.OrdinalIgnoreCase);
    }
}
