using System.Collections.Generic;
using System.Text.Json;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: ClassicQuests also reads hearthdaoc-quests.json, the fork's additions to upstream's classic-quests.json
// (the Guild of Shadows level-50 quests' map markers, and Lord Elidyn as a quest monster gamebots leave alone). Its
// quests are added where upstream's file has no entry for the ID (an entry with "Replace": true replaces upstream's), and
// its quest monsters join upstream's (spec docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.2).
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

    // A quest the fork rebuilt (level 11's "Entry Into Tomorrow") needs its own entry: "Replace": true makes ours win.
    private const string Rebuilt = """
    { "Quests": { "21482": { "Replace": true, "Steps": [ null, { "Marker": { "Region": 1, "X": 9, "Y": 9, "Z": 9 } },
                                                             { "NeedsItem": "cq_crediac", "Issuer": "Omis" } ] },
                  "990509": { "Replace": true, "Steps": [ null, { "Marker": { "Region": 1, "X": 5, "Y": 5, "Z": 5 } } ] } } }
    """;

    [Test]
    public void OurEntryWithReplaceReplacesUpstreamsOnTheSameId()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(Rebuilt));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests.Keys, Is.EquivalentTo(new[] { 21482, 990509 }));
            Assert.That(merged.Quests[21482].Steps, Has.Count.EqualTo(3));
            Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 9, 9, 9)));
            Assert.That(merged.Quests[21482].Steps[2].Marker, Is.Null);
            Assert.That(merged.Quests[21482].Steps[2].NeedsItem, Is.EqualTo("cq_crediac"));
            Assert.That(merged.Quests[21482].Steps[2].Issuer, Is.EqualTo("Omis"));
            Assert.That(merged.Quests[990509].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 5, 5, 5)));
            Assert.That(merged.QuestMonsterIds, Is.EquivalentTo(new[] { "upstream-mob" }));
        });
    }

    [Test]
    public void ReplaceFalseOrMissingLeavesUpstreamsEntry()
    {
        foreach (string replace in new[] { "\"Replace\": false, ", "" })
        {
            string extra = $$"""{ "Quests": { "21482": { {{replace}}"Steps": [ null, { "Marker": { "Region": 1, "X": 9, "Y": 9, "Z": 9 } } ] } } }""";
            ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(extra));
            Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 1, 2, 3)), replace);
        }
    }

    [Test]
    public void ReplaceIsReadWithoutCase()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream),
            Parse("""{ "Quests": { "21482": { "replace": true, "Steps": [ null, { "Marker": { "Region": 1, "X": 9, "Y": 9, "Z": 9 } } ] } } }"""));
        Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 9, 9, 9)));
    }

    [Test]
    public void ANullExtraEntryLeavesUpstreamsEntry()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse("""{ "Quests": { "21482": null } }"""));
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

    private const string UpstreamChat = """
    { "Chat": { "Captain Rhodri": { "Cabalist": "You are a Cabalist.", "Hello": "Greetings.", "Silenced": "Old notes [Step#3] loc=1" },
                "Twr ap Alsig": { "Infiltrator": "upstream reply" } } }
    """;

    private static ClassicQuests.Config MergeChat(string extra) => ClassicQuests.Merge(Parse(UpstreamChat), Parse(extra));

    [Test]
    public void OurChatReplyReplacesUpstreamsAndTheRestStays()
    {
        ClassicQuests.Config merged = MergeChat("""{ "Chat": { "Captain Rhodri": { "Cabalist": "Well met, cabalist." } } }""");
        Assert.Multiple(() =>
        {
            Assert.That(merged.Chat["Captain Rhodri"]["Cabalist"], Is.EqualTo("Well met, cabalist."));
            Assert.That(merged.Chat["Captain Rhodri"]["Hello"], Is.EqualTo("Greetings."));
            Assert.That(merged.Chat["Twr ap Alsig"]["Infiltrator"], Is.EqualTo("upstream reply"));
        });
    }

    [Test]
    public void OurChatAddsNpcsAndKeywordsUpstreamHasNone()
    {
        ClassicQuests.Config merged = MergeChat("""{ "Chat": { "Captain Rhodri": { "Reaver": "Reaver reply" }, "Ley Manton": { "Hello": "Hi" } } }""");
        Assert.Multiple(() =>
        {
            Assert.That(merged.Chat["Captain Rhodri"]["Reaver"], Is.EqualTo("Reaver reply"));
            Assert.That(merged.Chat["Ley Manton"]["Hello"], Is.EqualTo("Hi"));
            Assert.That(merged.Chat["Captain Rhodri"].Keys, Is.EquivalentTo(new[] { "Cabalist", "Hello", "Silenced", "Reaver" }));
        });
    }

    [Test]
    public void AnEmptyChatReplySilencesUpstreamsLine()
    {
        ClassicQuests.Config merged = MergeChat("""{ "Chat": { "Captain Rhodri": { "Silenced": "" } } }""");
        Assert.That(merged.Chat["Captain Rhodri"]["Silenced"], Is.Empty);
    }

    [Test]
    public void ChatNpcNamesAndKeywordsCompareWithoutCase()
    {
        ClassicQuests.Config merged = MergeChat("""{ "Chat": { "captain rhodri": { "CABALIST": "mine", "hello": "mine too" } } }""");
        Assert.Multiple(() =>
        {
            Assert.That(merged.Chat.Keys, Is.EquivalentTo(new[] { "Captain Rhodri", "Twr ap Alsig" }));
            Assert.That(merged.Chat["Captain Rhodri"], Has.Count.EqualTo(3));
            Assert.That(merged.Chat["Captain Rhodri"]["Cabalist"], Is.EqualTo("mine"));
            Assert.That(merged.Chat["Captain Rhodri"]["Hello"], Is.EqualTo("mine too"));
            Assert.That(merged.Chat["CAPTAIN RHODRI"].ContainsKey("cabalist"), Is.True);
        });
    }

    [Test]
    public void OurChatKeywordsAreTrimmedLikeAWhisper()
    {
        ClassicQuests.Config merged = MergeChat("""{ "Chat": { "Captain Rhodri": { " Hello! ": "mine" } } }""");
        Assert.Multiple(() =>
        {
            Assert.That(merged.Chat["Captain Rhodri"]["Hello"], Is.EqualTo("mine"));
            Assert.That(merged.Chat["Captain Rhodri"], Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void ChatNeedsNoUpstreamChat()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse("""{ "Chat": { "Captain Rhodri": { "Hello": "Hi" } } }"""));
        Assert.That(merged.Chat["captain rhodri"]["hello"], Is.EqualTo("Hi"));
    }

    [Test]
    public void ANullOrMissingChatChangesNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MergeChat("""{ "Chat": null }""").Chat["Captain Rhodri"]["Cabalist"], Is.EqualTo("You are a Cabalist."));
            Assert.That(MergeChat("""{ "Quests": {} }""").Chat["Captain Rhodri"]["Cabalist"], Is.EqualTo("You are a Cabalist."));
            Assert.That(ClassicQuests.Merge(Parse(UpstreamChat), null).Chat["Twr ap Alsig"]["Infiltrator"], Is.EqualTo("upstream reply"));
        });
    }

    [Test]
    public void UpstreamKeywordsThatDifferOnlyInCaseDoNotStopTheLoad()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(
            Parse("""{ "Chat": { "Omis": { "Stone": "first", "stone": "second" } } }"""),
            Parse("""{ "Chat": { "Captain Rhodri": { "Hello": "Hi" } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Chat["Omis"], Has.Count.EqualTo(1));
            Assert.That(merged.Chat["Omis"]["STONE"], Is.EqualTo("second"));
        });
    }

    [Test]
    public void ANullNpcEntryIsIgnored()
    {
        ClassicQuests.Config merged = MergeChat("""{ "Chat": { "Captain Rhodri": null } }""");
        Assert.That(merged.Chat["Captain Rhodri"]["Hello"], Is.EqualTo("Greetings."));
    }

    // HearthDAoC (owner 2026-10-13, #121 #126): an NPC the fork moves (Captain Rhodri to the north wall, Sir Tilian to the
    // north-west corner, the supply run to Bline Tengit) takes the map markers on his old spot with him, in every quest
    // of either file, the replaced ones included.
    private const string Moves = """
    { "MovedMarkers": [ { "From": { "Region": 1, "X": 1, "Y": 2, "Z": 3 }, "To": { "Region": 1, "X": 7, "Y": 8, "Z": 9 } } ] }
    """;

    [Test]
    public void AMovedNpcTakesTheMarkersOnHisOldSpot()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(Moves));
        Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 7, 8, 9)));
    }

    [Test]
    public void MarkersElsewhereStay()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Extra), Parse(Moves));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[990509].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 568158, 404718, 5032)));
            Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 9, 9, 9)));
        });
    }

    [Test]
    public void AReplacedEntrysMarkersMoveToo()
    {
        const string replaced = """
        { "Quests": { "21482": { "Replace": true, "Steps": [ null, { "Marker": { "Region": 1, "X": 1, "Y": 2, "Z": 3 } } ] } },
          "MovedMarkers": [ { "From": { "Region": 1, "X": 1, "Y": 2, "Z": 3 }, "To": { "Region": 1, "X": 7, "Y": 8, "Z": 9 } } ] }
        """;
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(replaced));
        Assert.That(merged.Quests[21482].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 7, 8, 9)));
    }

    [Test]
    public void TheExtraFilesLinksAreRead()
    {
        const string links = """
        { "Links": [ { "Name": "Delfina's fire", "Members": [ "d", "r1", "r2" ], "Callers": [ "d" ], "Range": 1500 } ] }
        """;
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Upstream), Parse(links));
        Assert.That(merged.Links, Has.Count.EqualTo(1));
        Assert.That(merged.Links[0].Members, Is.EqualTo(new[] { "d", "r1", "r2" }));
    }

    // HearthDAoC (2026-10-22): the fork's Fellows (Mulgrut Maggot's escort, QuestFellowBrain) join upstream's (Fjall's);
    // ours win on the same Mob_ID.
    private const string UpstreamFellows = """
    { "Quests": {}, "Fellows": { "fjall-friend": "Fjall", "shared": "Fjall" } }
    """;

    [Test]
    public void TheExtraFilesFellowsJoinUpstreams()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(UpstreamFellows),
            Parse("""{ "Fellows": { "zombie-1": "Mulgrut Maggot", "zombie-2": "Mulgrut Maggot" } }"""));
        Assert.That(merged.Fellows, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["fjall-friend"] = "Fjall", ["shared"] = "Fjall",
            ["zombie-1"] = "Mulgrut Maggot", ["zombie-2"] = "Mulgrut Maggot",
        }));
    }

    [Test]
    public void OurFellowWinsOnTheSameMobId()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(UpstreamFellows), Parse("""{ "Fellows": { "shared": "Mulgrut Maggot" } }"""));
        Assert.That(merged.Fellows["shared"], Is.EqualTo("Mulgrut Maggot"));
    }

    [TestCase("""{ "Fellows": null }""")]
    [TestCase("""{ }""")]
    [TestCase("""{ "Fellows": { "": "Mulgrut Maggot", "no-leader": "" } }""")]
    public void NoFellowsOfOursLeaveUpstreams(string extra)
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(UpstreamFellows), Parse(extra));
        Assert.That(merged.Fellows, Is.EquivalentTo(new Dictionary<string, string> { ["fjall-friend"] = "Fjall", ["shared"] = "Fjall" }));
    }

    [Test]
    public void OurFellowsNeedNoUpstreamFellows()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse("""{ "Quests": {}, "Fellows": null }"""),
            Parse("""{ "Fellows": { "zombie-1": "Mulgrut Maggot" } }"""));
        Assert.That(merged.Fellows["zombie-1"], Is.EqualTo("Mulgrut Maggot"));
    }

    [Test]
    public void OurFellowsAreLookedUpByTheirExactMobId()
    {
        // FellowLeaderName reads the merged list by the fellow's Mob_ID, as upstream's (Ordinal).
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse("""{ "Quests": {}, "Fellows": null }"""),
            Parse("""{ "Fellows": { "zombie-1": "Mulgrut Maggot" } }"""));
        Assert.That(merged.Fellows.ContainsKey("ZOMBIE-1"), Is.False);
    }

    // HearthDAoC (0.37 sync, #138): upstream 0.36 pays a classic quest's XP as a share of the player's current level
    // (XpShares) and has a step's NPC greet a player who comes back (Greet). Our entry for a quest upstream has carries
    // its XpShares and each step's Greet onto upstream's entry, and ours win: the Guild of Shadows chain pays the owner's
    // shares (decision 1.c), and "" drops a greeting (R6: level 43's stage 3). Everything else stays upstream's.
    private const string Scaled = """
    { "Quests": { "20433": { "XpShares": [ 0.0, 0.0, 0.100364 ],
                             "Steps": [ null, { "Marker": { "Region": 1, "X": 1, "Y": 2, "Z": 3 }, "Greet": "Back already?" },
                                              { "Greet": "Were you able to deliver the dispatch?" },
                                              { "Greet": "Did Twr ap Alsig take the dispatch?" } ] } } }
    """;

    [Test]
    public void OurXpSharesWinOnUpstreamsEntry()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled), Parse("""{ "Quests": { "20433": { "XpShares": [ 0.0, 0.0, 0.1 ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].XpShares, Is.EqualTo(new[] { 0.0, 0.0, 0.1 }));
            Assert.That(merged.Quests[20433].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 1, 2, 3)));
            Assert.That(merged.Quests[20433].Steps[2].Greet, Is.EqualTo("Were you able to deliver the dispatch?"));
        });
    }

    [TestCase("")]
    [TestCase("\"XpShares\": [], ")]
    [TestCase("\"XpShares\": null, ")]
    public void WithoutSharesOfOursUpstreamsStay(string shares)
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled), Parse($$"""{ "Quests": { "20433": { {{shares}}"Steps": [ null ] } } }"""));
        Assert.That(merged.Quests[20433].XpShares, Is.EqualTo(new[] { 0.0, 0.0, 0.100364 }));
    }

    [Test]
    public void OurGreetWinsOnItsStepAndTheOthersStay()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled),
            Parse("""{ "Quests": { "20433": { "Steps": [ null, null, { "Greet": "Were you able to retrieve Tepok's precious sack?" } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].Steps[1].Greet, Is.EqualTo("Back already?"));
            Assert.That(merged.Quests[20433].Steps[2].Greet, Is.EqualTo("Were you able to retrieve Tepok's precious sack?"));
            Assert.That(merged.Quests[20433].Steps[3].Greet, Is.EqualTo("Did Twr ap Alsig take the dispatch?"));
        });
    }

    [Test]
    public void AnEmptyGreetRemovesUpstreams()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled), Parse("""{ "Quests": { "20433": { "Steps": [ null, null, { "Greet": "" } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].Steps[2].Greet, Is.Null);
            Assert.That(merged.Quests[20433].Steps[1].Greet, Is.EqualTo("Back already?"));
            Assert.That(merged.Quests[20433].Steps[3].Greet, Is.EqualTo("Did Twr ap Alsig take the dispatch?"));
        });
    }

    [Test]
    public void OnlyTheGreetOfOurStepIsCarried()
    {
        // A marker, an item or a custom step of ours needs a Replace entry.
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled),
            Parse("""{ "Quests": { "20433": { "Steps": [ null, { "Marker": { "Region": 1, "X": 9, "Y": 9, "Z": 9 }, "NeedsItem": "cq_x" } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 1, 2, 3)));
            Assert.That(merged.Quests[20433].Steps[1].NeedsItem, Is.Null);
            Assert.That(merged.Quests[20433].Steps[1].Greet, Is.EqualTo("Back already?"));
            Assert.That(merged.Quests[20433].Steps, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public void AGreetOnAStepUpstreamHasNoInfoForAddsTheStep()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse("""{ "Quests": { "20433": { "Steps": [ null, null ] } } }"""),
            Parse("""{ "Quests": { "20433": { "Steps": [ null, { "Greet": "Back already?" }, null, { "Greet": "Well?" } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].Steps, Has.Count.EqualTo(4));
            Assert.That(merged.Quests[20433].Steps[1].Greet, Is.EqualTo("Back already?"));
            Assert.That(merged.Quests[20433].Steps[2], Is.Null);
            Assert.That(merged.Quests[20433].Steps[3].Greet, Is.EqualTo("Well?"));
            Assert.That(merged.Quests[20433].Steps[3].Marker, Is.Null);
        });
    }

    [Test]
    public void AnEmptyGreetOnAStepUpstreamHasNoInfoForAddsNothing()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse("""{ "Quests": { "20433": { "Steps": [ null, null ] } } }"""),
            Parse("""{ "Quests": { "20433": { "Steps": [ null, { "Greet": "" }, null, { "Greet": "" } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].Steps, Has.Count.EqualTo(2));
            Assert.That(merged.Quests[20433].Steps[1], Is.Null);
        });
    }

    [Test]
    public void SharesAndGreetsAreReadWithoutCase()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled),
            Parse("""{ "Quests": { "20433": { "xpshares": [ 0.25 ], "steps": [ null, { "greet": "" } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].XpShares, Is.EqualTo(new[] { 0.25 }));
            Assert.That(merged.Quests[20433].Steps[1].Greet, Is.Null);
        });
    }

    [Test]
    public void ANewIdIsAddedWithItsSharesAndGreets()
    {
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled),
            Parse("""{ "Quests": { "990509": { "XpShares": [ 0.0, 0.1 ], "Steps": [ null, { "Greet": "You return." } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests.Keys, Is.EquivalentTo(new[] { 20433, 990509 }));
            Assert.That(merged.Quests[990509].XpShares, Is.EqualTo(new[] { 0.0, 0.1 }));
            Assert.That(merged.Quests[990509].Steps[1].Greet, Is.EqualTo("You return."));
            Assert.That(merged.Quests[20433].XpShares, Is.EqualTo(new[] { 0.0, 0.0, 0.100364 }));
        });
    }

    [Test]
    public void AReplaceEntryStillReplacesTheWholeEntry()
    {
        // Upstream's shares and greetings go with its entry; a Replace entry brings its own (the fork's rebuilt quests).
        ClassicQuests.Config merged = ClassicQuests.Merge(Parse(Scaled),
            Parse("""{ "Quests": { "20433": { "Replace": true, "Steps": [ null, { "Marker": { "Region": 1, "X": 9, "Y": 9, "Z": 9 } } ] } } }"""));
        ClassicQuests.Config own = ClassicQuests.Merge(Parse(Scaled),
            Parse("""{ "Quests": { "20433": { "Replace": true, "XpShares": [ 0.0, 0.25 ], "Steps": [ null, { "Greet": "Mine." } ] } } }"""));
        Assert.Multiple(() =>
        {
            Assert.That(merged.Quests[20433].XpShares, Is.Empty);
            Assert.That(merged.Quests[20433].Steps, Has.Count.EqualTo(2));
            Assert.That(merged.Quests[20433].Steps[1].Greet, Is.Null);
            Assert.That(merged.Quests[20433].Steps[1].Marker, Is.EqualTo(new ClassicQuests.Point(1, 9, 9, 9)));
            Assert.That(own.Quests[20433].XpShares, Is.EqualTo(new[] { 0.0, 0.25 }));
            Assert.That(own.Quests[20433].Steps[1].Greet, Is.EqualTo("Mine."));
            Assert.That(own.Quests[20433].Steps[1].Marker, Is.Null);
        });
    }
}
