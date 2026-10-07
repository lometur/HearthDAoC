using System;
using System.Collections.Generic;
using DOL.GS.HearthDAoC;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: a new character of a classic race is asked once, a few seconds after its first entry into
// the world, whether to begin its journey in its realm's Shrouded Isles town. SiStartChoice holds every
// decision; the game wiring (SiStartChoiceScript) only feeds it the character's state.
[TestFixture]
public sealed class UT_SiStartChoice
{
    private const int Albion = 1, Midgard = 100, Hibernia = 200;

    private static readonly SiStartDestination CaerGothwaite = new("Caer Gothwaite", 51, 535518, 547214, 4800, 2105);
    private static readonly SiStartDestination Aegirhamn = new("Aegirhamn", 151, 293910, 356255, 3488, 1199);
    private static readonly SiStartDestination Domnann = new("Grove of Domnann", 181, 423187, 440300, 5952, 3866);

    private static IEnumerable<SiStartDestination> Destinations()
    {
        return new[] { CaerGothwaite, Aegirhamn, Domnann };
    }

    // ShouldAsk for a qualifying character, with one thing changed per call.
    private static bool Asks(bool enabled = true, int level = 1, eRace race = eRace.Briton, int region = Albion,
        string saved = null, bool hasDestination = true)
    {
        return SiStartChoice.ShouldAsk(enabled, level, (int)race, region, saved, hasDestination);
    }

    [TestCase(eRace.Briton, Albion)]
    [TestCase(eRace.Avalonian, Albion)]
    [TestCase(eRace.Highlander, Albion)]
    [TestCase(eRace.Saracen, Albion)]
    [TestCase(eRace.Norseman, Midgard)]
    [TestCase(eRace.Troll, Midgard)]
    [TestCase(eRace.Dwarf, Midgard)]
    [TestCase(eRace.Kobold, Midgard)]
    [TestCase(eRace.Celt, Hibernia)]
    [TestCase(eRace.Firbolg, Hibernia)]
    [TestCase(eRace.Elf, Hibernia)]
    [TestCase(eRace.Lurikeen, Hibernia)]
    public void AsksALevelOneClassicRaceInItsHomeRegion(eRace race, int region)
    {
        Assert.That(Asks(race: race, region: region), Is.True);
        Assert.That(Asks(race: race, region: region, saved: ""), Is.True);
    }

    [Test]
    public void NotAskedWhenTheSettingIsOff()
    {
        Assert.That(Asks(enabled: false), Is.False);
    }

    [TestCase(2)]
    [TestCase(5)]
    [TestCase(50)]
    public void NotAskedFromLevelTwo(int level)
    {
        Assert.That(Asks(level: level), Is.False);
    }

    [TestCase(eRace.Inconnu)]
    [TestCase(eRace.Valkyn)]
    [TestCase(eRace.Sylvan)]
    [TestCase(eRace.HalfOgre)]
    [TestCase(eRace.Frostalf)]
    [TestCase(eRace.Shar)]
    [TestCase(eRace.AlbionMinotaur)]
    [TestCase(eRace.MidgardMinotaur)]
    [TestCase(eRace.HiberniaMinotaur)]
    public void NotAskedForAnSiRaceOrARaceFrom16To21(eRace race)
    {
        Assert.That(Asks(race: race), Is.False);
    }

    [TestCase(51)]
    [TestCase(151)]
    [TestCase(181)]
    public void NotAskedInsideAnSiRegion(int region)
    {
        Assert.That(Asks(region: region), Is.False);
    }

    [TestCase("yes")]
    [TestCase("no")]
    public void NotAskedWithASavedAnswer(string saved)
    {
        Assert.That(Asks(saved: saved), Is.False);
    }

    [Test]
    public void NotAskedWhenTheRealmHasNoDestination()
    {
        Assert.That(Asks(hasDestination: false), Is.False);
    }

    [Test]
    public void ClassicRacesAreOneToTwelveAndSiRegionsAre51_151_181()
    {
        for (int race = 0; race <= 22; race++)
            Assert.That(SiStartChoice.IsClassicRace(race), Is.EqualTo(race >= 1 && race <= 12), $"race {race}");

        foreach (int region in new[] { 51, 151, 181 })
            Assert.That(SiStartChoice.IsSiRegion(region), Is.True, $"region {region}");

        foreach (int region in new[] { 0, 1, 27, 50, 52, 100, 150, 152, 180, 182, 200 })
            Assert.That(SiStartChoice.IsSiRegion(region), Is.False, $"region {region}");
    }

    [Test]
    public void EachRealmHasItsSiTownsTeleportId()
    {
        Assert.That(SiStartChoice.TeleportIds, Is.EqualTo(new Dictionary<int, string>
        {
            [1] = "Caer Gothwaite",
            [2] = "Aegirhamn",
            [3] = "Grove of Domnann",
        }));
    }

    [Test]
    public void TheQuestionNamesTheRealmsSiTown()
    {
        Assert.That(SiStartChoice.Question(1),
            Is.EqualTo("Begin your journey in the Shrouded Isles, at Caer Gothwaite? Decline to stay here."));
        Assert.That(SiStartChoice.Question(2),
            Is.EqualTo("Begin your journey in the Shrouded Isles, at Aegirhamn? Decline to stay here."));
        Assert.That(SiStartChoice.Question(3),
            Is.EqualTo("Begin your journey in the Shrouded Isles, at the Grove of Domnann? Decline to stay here."));
        Assert.That(SiStartChoice.Question(0), Is.Null);
        Assert.That(SiStartChoice.Question(4), Is.Null);

        // Under 100 characters, plain ASCII (so plain CP1252) and on one line: the client wraps it itself.
        for (int realm = 1; realm <= 3; realm++)
        {
            string question = SiStartChoice.Question(realm);
            Assert.That(question.Length, Is.LessThan(100), question);
            Assert.That(question, Does.Match("^[ -~]+$"), question);
        }
    }

    [Test]
    public void TheAnswerIsSavedUnderOneKeyAsYesOrNo()
    {
        Assert.That(SiStartChoice.AnswerKey, Is.EqualTo("hearthdaoc_si_start"));
        Assert.That(SiStartChoice.AnswerYes, Is.EqualTo("yes"));
        Assert.That(SiStartChoice.AnswerNo, Is.EqualTo("no"));
    }

    [Test]
    public void ResponseOneIsAnAccept()
    {
        Assert.That(SiStartChoice.Classify(0x01, stillPending: false, qualifies: true, isAlive: true),
            Is.EqualTo(SiStartOutcome.Accept));
    }

    // A decline saves "no" and moves nothing: the script writes AnswerNo for this outcome and nothing else.
    [TestCase(0x00)]
    [TestCase(0x02)]
    [TestCase(0xFF)]
    public void AnyOtherResponseFromThePlayerIsADecline(byte response)
    {
        Assert.That(SiStartChoice.Classify(response, stillPending: false, qualifies: true, isAlive: true),
            Is.EqualTo(SiStartOutcome.Decline));
    }

    // The server answers 0x00 for our dialog when it sends another one while ours is still pending; a real
    // click clears the pending callback first. Nothing is saved, so the question comes back.
    [Test]
    public void ADialogThatIsStillPendingWasSuperseded()
    {
        foreach (byte response in new byte[] { 0x00, 0x01 })
        foreach (bool qualifies in new[] { true, false })
        foreach (bool isAlive in new[] { true, false })
        {
            Assert.That(SiStartChoice.Classify(response, stillPending: true, qualifies, isAlive),
                Is.EqualTo(SiStartOutcome.Superseded), $"response {response}, qualifies {qualifies}, alive {isAlive}");
        }
    }

    [Test]
    public void AnAnswerWhileDeadIsNotTakenNow()
    {
        Assert.That(SiStartChoice.Classify(0x01, stillPending: false, qualifies: true, isAlive: false),
            Is.EqualTo(SiStartOutcome.NotNow));
        Assert.That(SiStartChoice.Classify(0x00, stillPending: false, qualifies: true, isAlive: false),
            Is.EqualTo(SiStartOutcome.NotNow));
    }

    // When the timer fires, the script calls ShouldAsk again with the state the character has then:
    // PlayerInit may have moved it, or another login may have saved its answer.
    [Test]
    public void QualificationIsCheckedAgainWhenTheTimerFires()
    {
        Assert.That(Asks(), Is.True);
        Assert.That(Asks(region: 51), Is.False);
        Assert.That(Asks(saved: SiStartChoice.AnswerNo), Is.False);
        Assert.That(Asks(level: 2), Is.False);
        Assert.That(Asks(enabled: false), Is.False);
    }

    // The callback passes ShouldAsk's verdict at that moment as "qualifies".
    [Test]
    public void QualificationIsCheckedAgainWhenTheCallbackRuns()
    {
        Assert.That(SiStartChoice.Classify(0x01, stillPending: false, qualifies: false, isAlive: true),
            Is.EqualTo(SiStartOutcome.NotNow));
        Assert.That(SiStartChoice.Classify(0x00, stillPending: false, qualifies: false, isAlive: true),
            Is.EqualTo(SiStartOutcome.NotNow));
    }

    // Records every call ApplyAccept makes, in order.
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public readonly List<SiStartDestination> Moved = new(), Bound = new();
        public bool MoveSucceeds = true;
        public bool SaveThrows;

        public bool Apply(SiStartDestination destination)
        {
            return SiStartChoice.ApplyAccept(destination,
                d =>
                {
                    Calls.Add("move");
                    Moved.Add(d);
                    return MoveSucceeds;
                },
                d =>
                {
                    Calls.Add("bind");
                    Bound.Add(d);
                },
                () =>
                {
                    Calls.Add("save");
                    if (SaveThrows)
                        throw new InvalidOperationException("database gone");
                },
                answer => Calls.Add("write " + answer),
                message => Calls.Add("tell " + message));
        }
    }

    [TestCaseSource(nameof(Destinations))]
    public void AcceptMovesBindsSavesThenWritesYes(SiStartDestination destination)
    {
        var recorder = new Recorder();

        Assert.That(recorder.Apply(destination), Is.True);
        Assert.That(recorder.Calls, Is.EqualTo(new[]
        {
            "move",
            "bind",
            "save",
            "write yes",
            $"tell Welcome to {destination.Name}, in the Shrouded Isles. You are bound here.",
        }));
        Assert.That(recorder.Moved, Has.Count.EqualTo(1));
        Assert.That(recorder.Moved[0], Is.SameAs(destination));
        Assert.That(recorder.Bound, Has.Count.EqualTo(1));
        Assert.That(recorder.Bound[0], Is.SameAs(destination));
    }

    [Test]
    public void AFailedMoveBindsSavesAndWritesNothing()
    {
        var recorder = new Recorder { MoveSucceeds = false };

        Assert.That(recorder.Apply(CaerGothwaite), Is.False);
        Assert.That(recorder.Calls, Is.EqualTo(new[]
        {
            "move",
            "tell You could not be moved to Caer Gothwaite. You stay here; you will be asked again at your next login.",
        }));
        Assert.That(recorder.Bound, Is.Empty);
    }

    // A crash before the answer is written leaves no answer, so the question comes back.
    [Test]
    public void ACrashBeforeTheAnswerLeavesItUnwritten()
    {
        var recorder = new Recorder { SaveThrows = true };

        Assert.Throws<InvalidOperationException>(() => recorder.Apply(Aegirhamn));
        Assert.That(recorder.Calls, Is.EqualTo(new[] { "move", "bind", "save" }));
    }
}
