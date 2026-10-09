using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.Quests;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the GM's /epic command (spec docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.3). This
// class holds every decision: which classic data quests make up a class's epic chain, the state of each step for a
// character, and which steps "/epic done <level>" marks finished. It reads nothing from the running server
// (GameServer, WorldMgr, GamePlayer), so unit tests drive it directly; EpicCommand feeds it the quest rows and the
// character's quests, and carries out the outcome.

// One classic data quest. Classes empty: every class. Dependencies: its QuestDependency entries.
public sealed record EpicQuest(int Id, string Name, int MinLevel, int MaxLevel, ushort StartRegion,
    IReadOnlySet<int> Classes, IReadOnlyList<string> Dependencies);

public enum EpicStepState { Finished, Active, CanTake, Closed, Waiting }

public sealed record EpicStep(EpicQuest Quest, EpicStepState State, string Detail);

// A character's quests: finished quest names and IDs, active quest ID -> stage, and level.
public sealed record EpicProgress(ICollection<string> FinishedNames, ICollection<int> FinishedIds,
    IReadOnlyDictionary<int, int> ActiveStages, int Level);

public static class EpicChain
{
    // The Shrouded Isles home regions (Albion, Midgard, Hibernia). At one level, "/epic done" takes a step given
    // elsewhere first, so it marks the classic 7 and 11 unless a Shrouded Isles version is already finished.
    public static readonly IReadOnlySet<ushort> ShroudedIslesRegions = new HashSet<ushort> { 51, 151, 181 };

    public static IReadOnlySet<int> ParseClasses(string allowed)
    {
        var classes = new HashSet<int>();
        foreach (string part in (allowed ?? string.Empty).Split(new[] { '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int id))
                classes.Add(id);
        }
        return classes;
    }

    public static IReadOnlyList<string> ParseDependencies(string dependency) =>
        (dependency ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);

    // The chain of a class: from its last linked step (a quest with an ID entry that no other quest of the class
    // needs; the highest level first), every quest its entries name, back to the start, in level order (at one
    // level, steps given outside the Shrouded Isles first). A name entry leads to the class's quests of that name at
    // the highest level not above the step's. Nothing for a class whose quests have no ID entries.
    public static IReadOnlyList<EpicQuest> ChainFor(IEnumerable<EpicQuest> quests, int classId)
    {
        List<EpicQuest> mine = quests.Where(q => q.Classes.Count == 0 || q.Classes.Contains(classId)).ToList();
        Dictionary<int, EpicQuest> byId = mine.ToDictionary(q => q.Id);
        var needed = new HashSet<int>();
        foreach (EpicQuest quest in mine)
        {
            foreach (string entry in quest.Dependencies)
            {
                if (!QuestDependencies.TryParseIds(entry, out int[] ids, out bool closes) || closes)
                    continue;
                foreach (int id in ids)
                {
                    if (id != quest.Id)
                        needed.Add(id);
                }
            }
        }
        EpicQuest last = mine
            .Where(q => q.Dependencies.Any(QuestDependencies.IsIdEntry) && !needed.Contains(q.Id) && !NeedsItself(q))
            .OrderByDescending(q => q.MinLevel).ThenBy(q => q.Id).FirstOrDefault();
        if (last == null)
            return Array.Empty<EpicQuest>();
        var found = new Dictionary<int, EpicQuest>();
        var queue = new Queue<EpicQuest>();
        queue.Enqueue(last);
        while (queue.Count > 0)
        {
            EpicQuest quest = queue.Dequeue();
            if (!found.TryAdd(quest.Id, quest))
                continue;
            foreach (string entry in quest.Dependencies)
            {
                if (QuestDependencies.TryParseIds(entry, out int[] ids, out _))
                {
                    foreach (int id in ids)
                    {
                        if (byId.TryGetValue(id, out EpicQuest before))
                            queue.Enqueue(before);
                    }
                }
                else if (!QuestDependencies.IsIdEntry(entry))
                {
                    List<EpicQuest> named = mine.Where(q => q.Id != quest.Id && q.MinLevel <= quest.MinLevel
                        && string.Equals(q.Name, entry, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (named.Count == 0)
                        continue;
                    int top = named.Max(q => q.MinLevel);
                    foreach (EpicQuest before in named.Where(q => q.MinLevel == top))
                        queue.Enqueue(before);
                }
            }
        }
        return found.Values.OrderBy(q => q.MinLevel)
            .ThenBy(q => ShroudedIslesRegions.Contains(q.StartRegion) ? 1 : 0)
            .ThenBy(q => q.Id).ToList();
    }

    public static IReadOnlyList<EpicStep> Steps(IReadOnlyList<EpicQuest> chain, EpicProgress progress)
    {
        var active = new HashSet<int>(progress.ActiveStages.Keys);
        return chain.Select(q => StepOf(q, progress, active)).ToList();
    }

    // The steps below <level> that "/epic done" marks finished, in chain order: each one whose entries are met by
    // what the character has finished, plus the steps marked before it. A closed step is never marked; an active
    // one is (EpicCommand ends it).
    public static IReadOnlyList<int> FinishBelow(IReadOnlyList<EpicQuest> chain, int level, EpicProgress progress)
    {
        var names = new List<string>(progress.FinishedNames);
        var finished = new HashSet<int>(progress.FinishedIds);
        var active = new HashSet<int>(progress.ActiveStages.Keys);
        var marked = new List<int>();
        foreach (EpicQuest quest in chain)
        {
            if (quest.MinLevel >= level || finished.Contains(quest.Id) || IsClosedForAll(quest))
                continue;
            bool wasActive = active.Remove(quest.Id);
            if (!QuestDependencies.AreMet(quest.Dependencies, names, finished, active))
            {
                if (wasActive)
                    active.Add(quest.Id);
                continue;
            }
            marked.Add(quest.Id);
            finished.Add(quest.Id);
            names.Add(quest.Name);
        }
        return marked;
    }

    private static EpicStep StepOf(EpicQuest quest, EpicProgress progress, ICollection<int> active)
    {
        if (progress.FinishedIds.Contains(quest.Id))
            return new EpicStep(quest, EpicStepState.Finished, "finished");
        if (progress.ActiveStages.TryGetValue(quest.Id, out int stage))
            return new EpicStep(quest, EpicStepState.Active, $"active, stage {stage}");
        if (IsClosedForAll(quest))
            return new EpicStep(quest, EpicStepState.Closed, "offered to no one");
        string closer = quest.Dependencies.FirstOrDefault(e => e.Trim().StartsWith("!#", StringComparison.Ordinal)
            && !QuestDependencies.IsMet(e, progress.FinishedNames, progress.FinishedIds, active));
        if (closer != null)
            return new EpicStep(quest, EpicStepState.Closed, $"closed by {closer.Trim()}");
        List<string> needs = quest.Dependencies
            .Where(e => !QuestDependencies.IsMet(e, progress.FinishedNames, progress.FinishedIds, active))
            .Select(e => e.Trim()).ToList();
        if (progress.Level < quest.MinLevel)
            needs.Insert(0, $"level {quest.MinLevel}");
        return needs.Count == 0
            ? new EpicStep(quest, EpicStepState.CanTake, "can take")
            : new EpicStep(quest, EpicStepState.Waiting, "needs " + string.Join(", ", needs));
    }

    // Offered to no one: a level range that admits no level, or a dependency on itself (the world fix closes Lady
    // Aelawen's Supply Run that way).
    private static bool IsClosedForAll(EpicQuest quest) => quest.MaxLevel < quest.MinLevel || NeedsItself(quest);

    private static bool NeedsItself(EpicQuest quest) => quest.Dependencies.Any(e =>
        QuestDependencies.TryParseIds(e, out int[] ids, out bool closes) && !closes && ids.Length == 1 && ids[0] == quest.Id);
}
