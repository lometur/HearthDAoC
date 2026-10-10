using System.Collections.Generic;
using DOL.GS.HearthDAoC;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: /indicator's decisions: reading the arguments, the per-(GM, NPC) store of /indicator create values, and
// the plain words for an NPC's real indicator.
[TestFixture]
public sealed class UT_QuestIndicatorProbe
{
    private static IndicatorRequest Parse(params string[] words)
    {
        var args = new List<string> { "&indicator" };
        args.AddRange(words);
        return QuestIndicatorProbe.Parse(args.ToArray());
    }

    [Test]
    public void NoArgumentsOrShowShows()
    {
        Assert.That(Parse().Action, Is.EqualTo(IndicatorAction.Show));
        Assert.That(Parse("show").Action, Is.EqualTo(IndicatorAction.Show));
        Assert.That(Parse("SHOW").Action, Is.EqualTo(IndicatorAction.Show));
    }

    [Test]
    public void ClearRefreshAndAnythingElse()
    {
        Assert.That(Parse("clear").Action, Is.EqualTo(IndicatorAction.Clear));
        Assert.That(Parse("Refresh").Action, Is.EqualTo(IndicatorAction.Refresh));
        Assert.That(Parse("ring").Action, Is.EqualTo(IndicatorAction.Syntax));
    }

    [TestCase("0", 0)]
    [TestCase("1", 1)]
    [TestCase("16", 16)]
    [TestCase("255", 255)]
    [TestCase("0x10", 16)]
    [TestCase("0XfF", 255)]
    public void EffectTakesAByte(string text, int value)
    {
        IndicatorRequest request = Parse("effect", text);
        Assert.That(request.Error, Is.Null);
        Assert.That(request.Action, Is.EqualTo(IndicatorAction.Effect));
        Assert.That(request.Effect, Is.EqualTo((byte)value));
    }

    [TestCase("256")]
    [TestCase("-1")]
    [TestCase("+1")]
    [TestCase("1.5")]
    [TestCase("ten")]
    [TestCase("0x100")]
    [TestCase("0x")]
    [TestCase("")]
    public void EffectRefusesWhatIsNotAByte(string text)
    {
        IndicatorRequest request = Parse("effect", text);
        Assert.That(request.Action, Is.EqualTo(IndicatorAction.Effect));
        Assert.That(request.Error, Is.EqualTo(QuestIndicatorProbe.EffectNeeds));
    }

    [Test]
    public void EffectWithoutANumberSaysWhatItNeeds()
    {
        Assert.That(Parse("effect").Error, Is.EqualTo("effect needs a byte, 0 to 255 (or 0x00 to 0xFF)"));
    }

    [TestCase("none", eQuestIndicator.None)]
    [TestCase("available", eQuestIndicator.Available)]
    [TestCase("finish", eQuestIndicator.Finish)]
    [TestCase("lesson", eQuestIndicator.Lesson)]
    [TestCase("lore", eQuestIndicator.Lore)]
    [TestCase("Pending", eQuestIndicator.Pending)]
    public void CreateTakesAName(string name, eQuestIndicator indicator)
    {
        IndicatorRequest request = Parse("create", name);
        Assert.That(request.Error, Is.Null);
        Assert.That(request.Action, Is.EqualTo(IndicatorAction.Create));
        Assert.That(request.Indicator, Is.EqualTo(indicator));
        Assert.That(QuestIndicatorProbe.Name(indicator), Is.EqualTo(name.ToLowerInvariant()));
    }

    [Test]
    public void CreateRefusesOtherNamesAndNumbers()
    {
        const string needs = "create needs one of: none, available, finish, lesson, lore, pending";
        Assert.That(Parse("create").Error, Is.EqualTo(needs));
        Assert.That(Parse("create", "blue").Error, Is.EqualTo(needs));
        Assert.That(Parse("create", "8").Error, Is.EqualTo(needs));
    }

    [Test]
    public void CreateFlagsAreTheCreatePacketsBits()
    {
        // PacketLib1124.SendNPCCreate.
        Assert.That(QuestIndicatorProbe.CreateFlag(eQuestIndicator.None), Is.EqualTo("no flag"));
        Assert.That(QuestIndicatorProbe.CreateFlag(eQuestIndicator.Available), Is.EqualTo("flags2 0x08"));
        Assert.That(QuestIndicatorProbe.CreateFlag(eQuestIndicator.Finish), Is.EqualTo("flags2 0x10"));
        Assert.That(QuestIndicatorProbe.CreateFlag(eQuestIndicator.Lesson), Is.EqualTo("flags3 0x01"));
        Assert.That(QuestIndicatorProbe.CreateFlag(eQuestIndicator.Lore), Is.EqualTo("flags3 0x02"));
        Assert.That(QuestIndicatorProbe.CreateFlag(eQuestIndicator.Pending), Is.EqualTo("flags3 0x20"));
        Assert.That(QuestIndicatorProbe.Name((eQuestIndicator)0x20), Is.EqualTo("0x20"));
    }

    // The store.

    [Test]
    public void AnEmptyStoreHasNothing()
    {
        var store = new IndicatorOverrideStore();
        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(store.TryGet(new object(), new object(), out eQuestIndicator indicator), Is.False);
        Assert.That(indicator, Is.EqualTo(eQuestIndicator.None));
        Assert.That(store.RemoveNpc(new object()), Is.EqualTo(0));
        Assert.That(store.RemoveGm(new object()), Is.Empty);
    }

    [Test]
    public void AValueIsForItsGmAndNpcOnly()
    {
        var store = new IndicatorOverrideStore();
        object gm = new(), other = new(), elaru = new(), guard = new();
        store.Set(gm, elaru, eQuestIndicator.Lore);
        Assert.That(store.TryGet(gm, elaru, out eQuestIndicator indicator), Is.True);
        Assert.That(indicator, Is.EqualTo(eQuestIndicator.Lore));
        Assert.That(store.TryGet(other, elaru, out _), Is.False);
        Assert.That(store.TryGet(gm, guard, out _), Is.False);
        Assert.That(store.TryGet(null, elaru, out _), Is.False);

        store.Set(gm, elaru, eQuestIndicator.Pending);
        store.TryGet(gm, elaru, out indicator);
        Assert.That(indicator, Is.EqualTo(eQuestIndicator.Pending));
        Assert.That(store.Count, Is.EqualTo(1));
    }

    [Test]
    public void KeysCompareByReference()
    {
        var store = new IndicatorOverrideStore();
        string gm = new('g', 1), sameText = new('g', 1);
        object npc = new();
        store.Set(gm, npc, eQuestIndicator.Finish);
        Assert.That(store.TryGet(sameText, npc, out _), Is.False);
    }

    [Test]
    public void RemoveDropsOneValue()
    {
        var store = new IndicatorOverrideStore();
        object gm = new(), elaru = new(), guard = new();
        store.Set(gm, elaru, eQuestIndicator.Lore);
        store.Set(gm, guard, eQuestIndicator.Lesson);
        Assert.That(store.Remove(gm, elaru), Is.True);
        Assert.That(store.Remove(gm, elaru), Is.False);
        Assert.That(store.TryGet(gm, guard, out _), Is.True);
        Assert.That(store.Count, Is.EqualTo(1));
    }

    [Test]
    public void ClearingAGmLeavesTheOtherGms()
    {
        var store = new IndicatorOverrideStore();
        object gm = new(), other = new(), elaru = new(), guard = new();
        store.Set(gm, elaru, eQuestIndicator.Lore);
        store.Set(gm, guard, eQuestIndicator.Lesson);
        store.Set(other, elaru, eQuestIndicator.Finish);
        Assert.That(store.RemoveGm(gm), Is.EquivalentTo(new[] { elaru, guard }));
        Assert.That(store.TryGet(gm, elaru, out _), Is.False);
        Assert.That(store.TryGet(gm, guard, out _), Is.False);
        Assert.That(store.TryGet(other, elaru, out eQuestIndicator indicator), Is.True);
        Assert.That(indicator, Is.EqualTo(eQuestIndicator.Finish));
        Assert.That(store.RemoveGm(gm), Is.Empty);
    }

    [Test]
    public void AnNpcLeavingTheWorldDropsEveryGmsValue()
    {
        var store = new IndicatorOverrideStore();
        object gm = new(), other = new(), elaru = new(), guard = new();
        store.Set(gm, elaru, eQuestIndicator.Lore);
        store.Set(other, elaru, eQuestIndicator.Finish);
        store.Set(gm, guard, eQuestIndicator.Lesson);
        Assert.That(store.RemoveNpc(elaru), Is.EqualTo(2));
        Assert.That(store.TryGet(gm, elaru, out _), Is.False);
        Assert.That(store.TryGet(other, elaru, out _), Is.False);
        Assert.That(store.TryGet(gm, guard, out _), Is.True);
        Assert.That(store.Count, Is.EqualTo(1));
        store.Clear();
        Assert.That(store.Count, Is.EqualTo(0));
        Assert.That(store.TryGet(gm, guard, out _), Is.False);
    }

    // The "why".

    private const int Level = 7;

    private static GivenDataQuest Data(int id, string name, int minLevel = 7, int maxLevel = 50, bool qualifies = false,
        string startType = "Standard", bool shows = true, bool classAllowed = true, bool doing = false,
        bool done = false, bool dependenciesMet = true, string dependency = "") =>
        new(id, name, startType, shows, qualifies, minLevel, maxLevel, classAllowed, doing, done, dependenciesMet,
            dependency);

    private static IndicatorFacts Facts(eQuestIndicator actual, IReadOnlyList<GivenDataQuest> data = null,
        IReadOnlyList<GivenScriptedQuest> scripted = null, IReadOnlyList<StepAtNpc> steps = null,
        IReadOnlyList<string> rewards = null, string overriding = null, eQuestIndicator? forced = null, int level = Level) =>
        new(level, data ?? new GivenDataQuest[0], scripted ?? new GivenScriptedQuest[0], steps ?? new StepAtNpc[0],
            rewards ?? new string[0], overriding, actual, forced);

    [Test]
    public void AvailableNamesTheQuestThatQualifies()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.Available, new[]
        {
            Data(21500, "Traveler's Way -- Supply Run", dependenciesMet: false, dependency: "!#20478/21324"),
            Data(20478, "Strange Beings", qualifies: true),
        });
        Assert.That(QuestIndicatorProbe.RulesIndicator(facts), Is.EqualTo(eQuestIndicator.Available));
        Assert.That(QuestIndicatorProbe.Explain(facts), Is.EqualTo(new[]
        {
            "available: data quest 20478 Strange Beings (qualifies)",
            "  data quest 21500 Traveler's Way -- Supply Run: dependencies !#20478/21324 not met",
        }));
    }

    [Test]
    public void AvailableCountsTheOthersThatQualify()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.Available, new[]
        {
            Data(20478, "Strange Beings", qualifies: true),
            Data(20001, "Some Errand", qualifies: true),
        });
        Assert.That(QuestIndicatorProbe.Explain(facts)[0], Is.EqualTo("available: data quest 20478 Strange Beings (qualifies) and 1 more"));
        Assert.That(QuestIndicatorProbe.Explain(facts)[1], Is.EqualTo("  data quest 20001 Some Errand: qualifies"));
    }

    [Test]
    public void FinishNamesTheStepThatTargetsTheNpc()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.Finish,
            new[] { Data(20157, "Entry Into Tomorrow", minLevel: 11) },
            steps: new[]
            {
                new StepAtNpc(20188, "Rebellion Accepted", 2, DataQuest.eStepType.Kill),
                new StepAtNpc(20157, "Entry Into Tomorrow", 6, DataQuest.eStepType.InteractFinish),
            });
        Assert.That(QuestIndicatorProbe.Explain(facts), Is.EqualTo(new[]
        {
            "finish: data quest 20157 step 6 targets this NPC",
            "  data quest 20157 Entry Into Tomorrow: needs level 11",
            "  your data quest 20188 Rebellion Accepted is at step 2 (Kill) here, not a finishing step",
        }));
    }

    [Test]
    public void ARewardQuestWithEveryGoalDoneFinishes()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.Finish, rewards: new[] { "Wolf Pelts" });
        Assert.That(QuestIndicatorProbe.Explain(facts), Is.EqualTo(new[] { "finish: reward quest Wolf Pelts has every goal done" }));
    }

    [Test]
    public void AvailableComesBeforeFinish()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.Available, new[] { Data(20478, "Strange Beings", qualifies: true) },
            steps: new[] { new StepAtNpc(20157, "Entry Into Tomorrow", 6, DataQuest.eStepType.InteractFinish) });
        Assert.That(QuestIndicatorProbe.RulesIndicator(facts), Is.EqualTo(eQuestIndicator.Available));
        Assert.That(QuestIndicatorProbe.Explain(facts)[^1],
            Is.EqualTo("  your data quest 20157 Entry Into Tomorrow is at step 6 (InteractFinish) here"));
    }

    [Test]
    public void NoneSaysWhyEachQuestFails()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.None, new[]
        {
            Data(20478, "Strange Beings", minLevel: 7),
            Data(20157, "Entry Into Tomorrow", minLevel: 11),
            Data(20001, "Some Errand", minLevel: 1, done: true),
        }, level: 6);
        Assert.That(QuestIndicatorProbe.Explain(facts), Is.EqualTo(new[]
        {
            "none: 3 data quests, none qualifies (20478: needs level 7; 20157: needs level 11; 20001: already done)",
        }));
    }

    [Test]
    public void NoneWithNoQuests()
    {
        Assert.That(QuestIndicatorProbe.Explain(Facts(eQuestIndicator.None)),
            Is.EqualTo(new[] { "none: this NPC gives no quests, and no quest of yours finishes here" }));
    }

    [Test]
    public void ScriptedQuestsCount()
    {
        IndicatorFacts none = Facts(eQuestIndicator.None, new[] { Data(20478, "Strange Beings", doing: true) },
            new[] { new GivenScriptedQuest("Shadows_50", true, 1, false, 1) });
        Assert.That(QuestIndicatorProbe.Explain(none)[0], Is.EqualTo(
            "none: 1 data quest and 1 scripted quest, none qualifies (Shadows_50: already done; 20478: you are doing it)"));

        IndicatorFacts available = Facts(eQuestIndicator.Available, new[] { Data(20478, "Strange Beings", qualifies: true) },
            new[] { new GivenScriptedQuest("Academy_50", true, 0, false, 1), new GivenScriptedQuest("Viking_50", false, 0, false, 1) });
        Assert.That(QuestIndicatorProbe.Explain(available), Is.EqualTo(new[]
        {
            "available: scripted quest Academy_50 (qualifies) and 1 more",
            "  scripted quest Viking_50: its own check refuses",
            "  data quest 20478 Strange Beings: qualifies",
        }));
    }

    [Test]
    public void AScriptedQuestBeingDoneShowsNothing()
    {
        var quest = new GivenScriptedQuest("Academy_50", true, 0, true, 1);
        Assert.That(QuestIndicatorProbe.Shows(quest), Is.False);
        Assert.That(QuestIndicatorProbe.Why(quest), Is.EqualTo("you are doing it"));
        Assert.That(QuestIndicatorProbe.Shows(quest with { MaxCount = 2 }), Is.True);
    }

    [Test]
    public void EachReasonADataQuestFails()
    {
        string Why(GivenDataQuest quest) => QuestIndicatorProbe.Why(quest, Level);
        Assert.That(Why(Data(1, "a", minLevel: 8)), Is.EqualTo("needs level 8"));
        Assert.That(Why(Data(1, "a", minLevel: 1, maxLevel: 5)), Is.EqualTo("only to level 5"));
        Assert.That(Why(Data(1, "a", classAllowed: false)), Is.EqualTo("not for your class"));
        Assert.That(Why(Data(1, "a", doing: true)), Is.EqualTo("you are doing it"));
        Assert.That(Why(Data(1, "a", done: true)), Is.EqualTo("already done"));
        Assert.That(Why(Data(1, "a", dependenciesMet: false, dependency: "#20478")), Is.EqualTo("dependencies #20478 not met"));
        Assert.That(Why(Data(1, "a")), Is.EqualTo("its custom step check refuses"));
        Assert.That(Why(Data(1, "a", qualifies: true)), Is.EqualTo("qualifies"));
    }

    [Test]
    public void AQuestThatNeverShowsSaysWhy()
    {
        Assert.That(QuestIndicatorProbe.Why(Data(1, "a", qualifies: true, startType: "Collection", shows: false), Level),
            Is.EqualTo("qualifies; never shows an indicator (a Collection quest)"));
        Assert.That(QuestIndicatorProbe.Why(Data(1, "a", qualifies: true, shows: false), Level),
            Is.EqualTo("qualifies; never shows an indicator (NO_INDICATOR)"));

        IndicatorFacts facts = Facts(eQuestIndicator.None,
            new[] { Data(117, "Wolf Pelts", qualifies: true, startType: "Collection", shows: false) });
        Assert.That(QuestIndicatorProbe.RulesIndicator(facts), Is.EqualTo(eQuestIndicator.None));
    }

    [Test]
    public void OnlyFinishStepsFinish()
    {
        foreach (DataQuest.eStepType step in new[] { DataQuest.eStepType.KillFinish, DataQuest.eStepType.DeliverFinish,
                     DataQuest.eStepType.InteractFinish, DataQuest.eStepType.WhisperFinish, DataQuest.eStepType.CollectFinish })
            Assert.That(QuestIndicatorProbe.Finishes(step), Is.True, step.ToString());
        foreach (DataQuest.eStepType step in new[] { DataQuest.eStepType.Kill, DataQuest.eStepType.Deliver,
                     DataQuest.eStepType.Interact, DataQuest.eStepType.Whisper, DataQuest.eStepType.Collect,
                     DataQuest.eStepType.Search, DataQuest.eStepType.SearchFinish })
            Assert.That(QuestIndicatorProbe.Finishes(step), Is.False, step.ToString());
    }

    [Test]
    public void AClassThatDecidesItsOwnIndicatorIsNamed()
    {
        IndicatorFacts differs = Facts(eQuestIndicator.Available, overriding: "BountyMaster");
        Assert.That(QuestIndicatorProbe.Explain(differs), Is.EqualTo(new[]
        {
            "available: BountyMaster decides; GameNPC's rules give none: this NPC gives no quests, and no quest of yours finishes here",
            "  BountyMaster overrides GetQuestIndicator",
        }));

        IndicatorFacts agrees = Facts(eQuestIndicator.Available, new[] { Data(20478, "Strange Beings", qualifies: true) },
            overriding: "SluaghbinderTrainer");
        Assert.That(QuestIndicatorProbe.Explain(agrees), Is.EqualTo(new[]
        {
            "available: data quest 20478 Strange Beings (qualifies)",
            "  SluaghbinderTrainer overrides GetQuestIndicator; here it agrees with GameNPC's rules",
        }));
    }

    [Test]
    public void AForcedValueComesFirst()
    {
        IndicatorFacts facts = Facts(eQuestIndicator.None, forced: eQuestIndicator.Lore);
        Assert.That(QuestIndicatorProbe.Explain(facts), Is.EqualTo(new[]
        {
            "You see lore (flags3 0x02), set by /indicator create for you only; the real one:",
            "none: this NPC gives no quests, and no quest of yours finishes here",
        }));
    }
}
