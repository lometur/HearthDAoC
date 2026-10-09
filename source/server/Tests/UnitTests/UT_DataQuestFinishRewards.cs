using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the entry of a data quest's reward lists (RewardXP, RewardRP, RewardCLXP, RewardBP, RewardMoney) that
// finishing the quest pays. Upstream paid the first entry, but the classic quests keep the final reward in the last
// one, so 1,240 of them never paid their final XP (spec docs/fork/specs/2026-10-09-epic-chains-design.md, 3.1).
[TestFixture]
public sealed class UT_DataQuestFinishRewards
{
    [TestCase(3, 3, 2)]   // "0|0|3300" finishing at stage 3: the last entry
    [TestCase(4, 4, 3)]   // "0|0|600|600": the stage-4 entry (stage 3's was paid when stage 3 advanced)
    [TestCase(1, 1, 0)]   // a one-stage quest: its value, as before
    [TestCase(3, 1, 0)]   // a single value: that value, as before
    [TestCase(5, 3, 2)]   // fewer entries than stages: the last one
    [TestCase(0, 2, 0)]   // no stage (a collection quest): the first, as before
    public void FinishingPaysTheFinishingStagesEntry(int step, int entries, int expected)
    {
        Assert.That(DataQuest.FinishRewardIndex(step, entries), Is.EqualTo(expected));
    }

    [Test]
    public void AnEmptyListPaysNothing()
    {
        Assert.That(DataQuest.FinishRewardIndex(3, 0), Is.EqualTo(-1));
    }
}
