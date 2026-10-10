using System.Collections.Generic;
using System.Linq;
using DOL.GS.HearthDAoC;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: /epic's decisions (spec docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.3): a class's epic
// chain from the classic data quests' dependency entries, each step's state for a character, and what
// "/epic done <level>" marks finished.
[TestFixture]
public sealed class UT_EpicChain
{
    private const int Infiltrator = 9, Armsman = 2, Heretic = 33;

    private static EpicQuest Q(int id, string name, int level, string classes, string dependency, ushort region = 10) =>
        new(id, name, level, 50, region, EpicChain.ParseClasses(classes), EpicChain.ParseDependencies(dependency));

    // A Guild of Shadows chain cut short (with epic-chains-v1's links), a Defenders-like line mixing pinned IDs and a
    // name, and a quest with no links.
    private static readonly List<EpicQuest> Quests = new()
    {
        Q(21500, "Traveler's Way -- Supply Run", 7, "9", "!#20478/21324"),
        Q(21324, "Traveler's Way -- Supply Run", 7, "9", "#21324"),
        Q(20478, "Strange Beings", 7, "9", "!#21500/21324", region: 51),
        Q(20157, "Entry Into Tomorrow", 11, "9", "#21500/21324/20478|!#20469"),
        Q(20469, "Shades and Shadows", 11, "9", "#21500/21324/20478|!#20157", region: 51),
        Q(20188, "Rebellion Accepted", 15, "9", "#20157/20469"),
        Q(990509, "Lord of Deceit", 50, "9", "#20188", region: 1),
        Q(20001, "Some Errand", 5, "9", ""),
        Q(20401, "Legend of the Lake", 15, "2", ""),
        Q(20419, "Legend of the Lake", 20, "2", "#20401"),
        Q(20500, "Hands Of Fate", 30, "2", "#20419"),
        Q(20600, "Feast of the Decadent", 43, "2", "Hands Of Fate"),
        Q(20407, "Feast of the Decadent", 45, "2", "#20600"),
        Q(21287, "Feast of the Decadent", 48, "2|3|10|5", "#20407"),
    };

    // The same chain with the 11s' links the dialogue fix gives them (deploy/bin/quest_dialogue.json): each 11 needs
    // the 7 of its own branch, Camelot's or the Shrouded Isles'.
    private static readonly List<EpicQuest> Branches = Quests.Select(q => q.Id switch
    {
        20157 => q with { Dependencies = EpicChain.ParseDependencies("#21500/21324|!#20469") },
        20469 => q with { Dependencies = EpicChain.ParseDependencies("#20478|!#20157") },
        _ => q,
    }).ToList();

    private static List<int> Ids(IEnumerable<EpicQuest> chain) => chain.Select(q => q.Id).ToList();

    private static EpicProgress Progress(int level, int[] finished = null, Dictionary<int, int> active = null)
    {
        finished ??= new int[0];
        return new EpicProgress(finished.Select(id => Quests.First(q => q.Id == id).Name).ToList(),
            new HashSet<int>(finished), active ?? new Dictionary<int, int>(), level);
    }

    private static IReadOnlyList<EpicQuest> Shadows => EpicChain.ChainFor(Quests, Infiltrator);

    [Test]
    public void TheChainFollowsTheLinksBackFromItsLastStep()
    {
        Assert.Multiple(() =>
        {
            // In level order; at one level, the steps given outside the Shrouded Isles first.
            Assert.That(Ids(Shadows), Is.EqualTo(new[] { 21324, 21500, 20478, 20157, 20469, 20188, 990509 }));
            // Names are followed too, to the same-named quests at the highest level below.
            Assert.That(Ids(EpicChain.ChainFor(Quests, Armsman)), Is.EqualTo(new[] { 20401, 20419, 20500, 20600, 20407, 21287 }));
        });
    }

    [Test]
    public void AClassWithoutLinksHasNoChain()
    {
        Assert.That(EpicChain.ChainFor(Quests, Heretic), Is.Empty);
    }

    [Test]
    public void EachStepSaysWhereItStands()
    {
        IReadOnlyList<EpicStep> steps = EpicChain.Steps(Shadows, Progress(50, new[] { 21500 }, new Dictionary<int, int> { [20157] = 2 }));
        Assert.That(steps.Select(s => (s.Quest.Id, s.State, s.Detail)), Is.EqualTo(new[]
        {
            (21324, EpicStepState.Closed, "offered to no one"),
            (21500, EpicStepState.Finished, "finished"),
            (20478, EpicStepState.Closed, "closed by !#21500/21324"),
            (20157, EpicStepState.Active, "active, stage 2"),
            (20469, EpicStepState.Closed, "closed by !#20157"),
            (20188, EpicStepState.Waiting, "needs #20157/20469"),
            (990509, EpicStepState.Waiting, "needs #20188"),
        }));
    }

    [Test]
    public void ALowLevelWaitsForItsLevel()
    {
        EpicStep step = EpicChain.Steps(Shadows, Progress(10, new[] { 21500 })).First(s => s.Quest.Id == 20157);
        Assert.That((step.State, step.Detail), Is.EqualTo((EpicStepState.Waiting, "needs level 11")));
    }

    [Test]
    public void DoneMarksTheClassicStepsBelowTheLevel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EpicChain.FinishBelow(Shadows, 50, Progress(50)), Is.EqualTo(new[] { 21500, 20157, 20188 }));
            Assert.That(EpicChain.FinishBelow(Shadows, 15, Progress(50)), Is.EqualTo(new[] { 21500, 20157 }));
            Assert.That(EpicChain.FinishBelow(Shadows, 7, Progress(50)), Is.Empty);
        });
    }

    [Test]
    public void DoneKeepsTheVersionsAlreadyTaken()
    {
        Assert.Multiple(() =>
        {
            // SI 7 finished: the classic 7 stays closed, and 11 is the classic one.
            Assert.That(EpicChain.FinishBelow(Shadows, 50, Progress(50, new[] { 20478 })), Is.EqualTo(new[] { 20157, 20188 }));
            // SI 11 active: it is the one marked finished.
            Assert.That(EpicChain.FinishBelow(Shadows, 50, Progress(50, null, new Dictionary<int, int> { [20469] = 1 })),
                Is.EqualTo(new[] { 21500, 20469, 20188 }));
        });
    }

    // Where "/epic goto" leads an Infiltrator with no step active, from a region.
    private static int? GoTo(ushort region, int level, params int[] finished) =>
        EpicChain.NextStep(EpicChain.ChainFor(Branches, Infiltrator), Progress(level, finished), region)?.Id;

    [Test]
    public void GotoTakesTheBranchGivenWhereTheCharacterIs()
    {
        Assert.Multiple(() =>
        {
            // Both 7s are open: in the Shrouded Isles (region 51), its own; anywhere else, Camelot's, the first.
            Assert.That(GoTo(51, 7), Is.EqualTo(20478));
            Assert.That(GoTo(10, 7), Is.EqualTo(21500));
            Assert.That(GoTo(1, 7), Is.EqualTo(21500));
            // Both still wait for the level: the same choice.
            Assert.That(GoTo(51, 5), Is.EqualTo(20478));
        });
    }

    [Test]
    public void GotoStaysInTheBranchTheCharacterTook()
    {
        Assert.Multiple(() =>
        {
            // After the Shrouded Isles 7, Camelot's 11 waits on a 7 that is closed for good: the Shrouded Isles 11,
            // even from Camelot, where the 15's giver stands.
            Assert.That(GoTo(10, 11, 20478), Is.EqualTo(20469));
            Assert.That(GoTo(1, 11, 20478), Is.EqualTo(20469));
            // After Camelot's 7, Camelot's 11, even from the Shrouded Isles, and while it waits for level 11.
            Assert.That(GoTo(51, 11, 21500), Is.EqualTo(20157));
            Assert.That(GoTo(51, 10, 21500), Is.EqualTo(20157));
        });
    }

    [Test]
    public void GotoNeverSkipsAheadToALaterGiverInTheRegion()
    {
        Assert.Multiple(() =>
        {
            // The 15 is given in Camelot (region 10); the Lord of Deceit's giver stands in region 1, but waits on it.
            Assert.That(GoTo(1, 15, 21500, 20157), Is.EqualTo(20188));
            Assert.That(GoTo(1, 14, 20478, 20469), Is.EqualTo(20188));
            Assert.That(GoTo(10, 50, 21500, 20157, 20188), Is.EqualTo(990509));
            Assert.That(GoTo(51, 50, 21500, 20157, 20188, 990509), Is.Null);
        });
    }

    [Test]
    public void ParsingTheRowValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EpicChain.ParseClasses("22|31; 21"), Is.EquivalentTo(new[] { 22, 31, 21 }));
            Assert.That(EpicChain.ParseClasses(""), Is.Empty);
            Assert.That(EpicChain.ParseDependencies("#20157/20469|!#20478"), Is.EqualTo(new[] { "#20157/20469", "!#20478" }));
            Assert.That(EpicChain.ParseDependencies(null), Is.Empty);
        });
    }
}
