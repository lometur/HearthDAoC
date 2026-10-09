using System.Text.Json;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: ClassicQuests also reads hearthdaoc-quests.json, the fork's additions to upstream's classic-quests.json
// (the Guild of Shadows level-50 quests' map markers, and Lord Elidyn as a quest monster gamebots leave alone). Its
// quests are added where upstream's file has no entry for the ID, and its quest monsters join upstream's
// (spec docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.2).
[TestFixture]
public sealed class UT_ClassicQuestsExtra
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static ClassicQuests.Config Parse(string json) => JsonSerializer.Deserialize<ClassicQuests.Config>(json, Options);

    private const string Upstream = """
    { "Quests": { "21482": { "Steps": [ null, { "Marker": { "Region": 1, "X": 1, "Y": 2, "Z": 3 } } ] } },
      "QuestMonsterIds": [ "upstream-mob" ] }
    """;

    private const string Extra = """
    { "_about": "HearthDAoC's additions",
      "Quests": { "990509": { "Steps": [ null, { "Marker": { "Region": 1, "X": 568158, "Y": 404718, "Z": 5032 } },
                                             { "Marker": { "Region": 1, "X": 528239, "Y": 359818, "Z": 9088 } } ] },
                  "21482": { "Steps": [ null, { "Marker": { "Region": 1, "X": 9, "Y": 9, "Z": 9 } } ] } },
      "QuestMonsterIds": [ "1f005bc1-27ae-40b0-bc42-1ec407a4aa34", "upstream-mob" ] }
    """;

    [Test]
    public void TheExtraFileAddsQuestsAndQuestMonsters()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(Extra));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests.Keys, Is.EquivalentTo(new[] { 21482, 990509 }));
            Assert.That(merged.Quests[990509].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 568158, 404718, 5032)));
            Assert.That(merged.Quests[990509].Steps[2].Marker, Is.EqualTo(new ClassicQuests.Point(1, 528239, 359818, 9088)));
            Assert.That(merged.QuestMonsterIds, Is.EquivalentTo(new[] { "upstream-mob", "1f005bc1-27ae-40b0-bc42-1ec407a4aa34" }));
        });
    }

    [Test]
    public void UpstreamsEntryWinsOnTheSameId()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(Extra));
        Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 1, 2, 3)));
    }

    [Test]
    public void NoExtraFileChangesNothing()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), null);
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests.Keys, Is.EquivalentTo(new[] { 21482 }));
            Assert.That(merged.QuestMonsterIds, Is.EquivalentTo(new[] { "upstream-mob" }));
        });
    }

    [Test]
    public void AnExtraFileWithNothingInItChangesNothing()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse("""{ "Quests": null, "QuestMonsterIds": null }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests.Keys, Is.EquivalentTo(new[] { 21482 }));
            Assert.That(merged.QuestMonsterIds, Is.EquivalentTo(new[] { "upstream-mob" }));
        });
    }
}
