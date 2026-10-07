# Shrouded Isles Start Choice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On its first entry into the world, a new level-1 character of a classic race is asked once whether to begin its journey in its realm's Shrouded Isles town. Accept moves the character there and binds it there; Decline keeps it at home.

**Architecture:** The work is split into four pieces:
- **Decision class (`SiStartChoice`):** a plain class holding every rule, unit-tested without a server.
- **Wiring script (`SiStartChoiceScript`):**
  - handles `GamePlayerEvent.GameEntered`;
  - after a short timer, asks with the client's own accept/decline dialog;
  - on Accept, moves, rebinds and saves the character, then writes the answer to the per-character custom-parameter table.
- **Setting:** a server property, `si_start_choice` (off in code), turned on for HearthDAoC through `HEARTHDAOC_SI_START_CHOICE`. `deploy/bin/server_properties.py` writes it before every start.
- **Where the code lives:** in new fork files only. No upstream file is edited.

**Tech Stack:**
- C# (.NET 10; the OpenDAoC/DOL server), NUnit 4 for the server unit tests;
- Python 3.10+ standard library for the deploy scripts and tests;
- bash and Docker Compose;
- GitHub Actions.

**Spec:** `docs/fork/specs/2026-10-06-shrouded-isles-start-choice-design.md` (approved; branch `sub3-si-start-choice`).

## Global Constraints

- **Where the code lives:** only new fork-owned files under `source/server/GameServer/scripts/hearthdaoc/` and `source/server/Tests/UnitTests/`. No upstream server file is edited. Fork-owned deploy, CI and docs files may change.
- **The server property `si_start_choice`:**
  - It is a bool in category `server`, with code default `false`, declared with `[ServerProperty]` in the new script file.
  - Its description is exactly: "Ask new level 1 characters of the classic races whether to start in their realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)".
  - The HearthDAoC setting `HEARTHDAOC_SI_START_CHOICE` takes `on` or `off` (case ignored) and defaults to `on`.
  - `deploy/bin/server_properties.py` writes exactly `True` or `False`, with that description, before every start. Any other value exits 2.
- **Who is asked:** the setting is on; the character is level 1; its race is 1-12; it is not in region 51, 151 or 181; it has no saved answer; its realm's destination is known. Bots are never asked (a `GameBot` is a `GameNPC`).
- **The answer:** it is a per-character custom parameter with key `hearthdaoc_si_start` and value `yes` or `no`. There is one row per character: select it, then add or update it, through one write path only.
- **Destinations:** looked up with `WorldMgr.GetTeleportLocation(realm, ":" + id)`, which is case-sensitive:

  | Realm | ID | Region | X | Y | Z | Heading |
  |---|---|---|---|---|---|---|
  | 1 | `Caer Gothwaite` | 51 | 535518 | 547214 | 4800 | 2105 |
  | 2 | `Aegirhamn` | 151 | 293910 | 356255 | 3488 | 1199 |
  | 3 | `Grove of Domnann` | 181 | 423187 | 440300 | 5952 | 3866 |

  If a realm's row is missing, that realm is not asked, and one warning is logged at load.
- **Texts** (exact):
  - Albion: "Begin your journey in the Shrouded Isles, at Caer Gothwaite? Decline to stay here."
  - Midgard: "Begin your journey in the Shrouded Isles, at Aegirhamn? Decline to stay here."
  - Hibernia: "Begin your journey in the Shrouded Isles, at the Grove of Domnann? Decline to stay here."
  - After a successful move: "Welcome to <Name>, in the Shrouded Isles. You are bound here."
  - After a failed move: "You could not be moved to <Name>. You stay here; you will be asked again at your next login."
- **Responses:**
  - `0x01`, with our dialog no longer pending, is an Accept.
  - Any other value from the player is a Decline.
  - A callback that runs while our dialog is still the player's pending one means it was superseded, and nothing is saved.
  - If the character is dead, or no longer qualifies, when the callback runs, the answer is not taken now, and nothing is saved.
- **Accept order:**
  1. `MoveTo(destination)`. If it returns `false`, tell the player and save nothing.
  2. Bind from the destination values.
  3. `GameServer.Database.SaveObject(player.DBCharacter)`.
  4. Write the answer `yes`.
  5. Tell the player where they are.
- **Delay:** about 5 s with an `ECSGameTimer` after `GameEntered`. When the timer fires, send nothing unless the player is still active and playing on the same client, and still qualifies.
- **Workflow:** work on branch `sub3-si-start-choice`. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push; the owner merges the PR.

## Review Focus

1. **The server replaces our dialog before the player answers** (for example a merchant, healer or group invite while the question is up). Expected: nothing is saved, and the question comes back at the next login. Pinned by Task 1, `ADialogThatIsStillPendingWasSuperseded`, and checked in game in Task 4.
2. **The player logs out, or goes to the character screen, with the question open.** Expected: nothing is saved, and the question comes back at the next login while the character is still level 1. The logic is pinned by Task 1, `NotAskedWithASavedAnswer` and `AsksALevelOneClassicRaceInItsHomeRegion`, since there is no answer without a click. Checked in game in Task 4.
3. **The player answers while dead, or the move fails.** Expected: nothing is saved, the player is told, and the question comes back. Pinned by Task 1, `AnAnswerWhileDeadIsNotTakenNow` and `AFailedMoveBindsSavesAndWritesNothing`.
4. **A world data update renames or drops one of the three Teleport rows.** Expected: that realm is simply not asked, and the server logs a warning. Pinned by Task 1, `NotAskedWhenTheRealmHasNoDestination`, and Task 3's real-world-data test of the exact rows.
5. **A classic-race character already starts in the Shrouded Isles** (Saracen Disciple, or a Sluaghbinder novice when that class is enabled). Expected: no question. Pinned by Task 1, `NotAskedInsideAnSiRegion`.

## Prerequisites

- **Branch:** `sub3-si-start-choice` holds `main` (`32ddb77`) and the approved spec.
- **Tools:** you need the .NET 10 SDK (server build and unit tests), Python 3.10+, `ruby` (optional, for the YAML checks in `deploy/tests/test_workflows.py`), and the clean classic world for the real-world-data tests: `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`, used read-only as `HDC_TEST_WORLD`.
- **CI:** it runs the Python suites. With Task 1's filter change, its server unit-test step also runs `UT_SiStartChoice`.

## File structure

| Path | Responsibility | Task |
|---|---|---|
| `source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs` | Every rule, as a pure decision class | 1, 2 (`Qualifies`) |
| `source/server/Tests/UnitTests/UT_SiStartChoice.cs` | Unit tests for the rules | 1, 2 |
| `.github/workflows/server-image.yml`, `deploy/tests/test_workflows.py` | CI runs the new unit tests (pinned) | 1 |
| `source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs` | Property, destinations, event, timer, dialog, move, bind, save, answer | 2 |
| `docs/fork/FORK.md`, `client/README.md` | Fork record, player guide | 2 |
| `deploy/bin/server_properties.py`, `deploy/entrypoint.sh`, `deploy/compose.yml`, `deploy/.env.example`, `deploy/HANDOFF.md`, `deploy/tests/test_server_properties.py`, `deploy/tests/test_si_start_choice.py` | The setting, from `.env` to the server property, plus the real-world-data test | 3 |
| `docs/fork/verification/sub3-ingame.md` | In-game verification record | 4 |

---

### Task 1: SiStartChoice decision class, its unit tests and the CI filter

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs` (the decision class; plain C#, compiled into `GameServer.dll` like every file under `GameServer/`)
- Test: `source/server/Tests/UnitTests/UT_SiStartChoice.cs` (NUnit 4, namespace `DOL.GS.Tests`, like `UT_CommandPrivLevelOverrides.cs`)
- Modify: `.github/workflows/server-image.yml` (one line: the `--filter` of the step "Server unit tests for the fork's server changes")
- Modify: `deploy/tests/test_workflows.py` (a new class `ServerUnitTestWorkflowTests` at the end)

**Interfaces:**
- Consumes: nothing. This is the first task. `SiStartChoice.cs` uses only `System`, `System.Collections.Generic` and `System.Collections.ObjectModel`. It never reads `GameServer`, `WorldMgr` or `ServerProperties`, so the tests need no running server.
- Produces, in `source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs`, namespace `DOL.GS.HearthDAoC`. The file uses a file-scoped namespace and LF line endings, like the fork's own `UT_CommandPrivLevelOverrides.cs`.
  - `public sealed record SiStartDestination(string Name, ushort Region, int X, int Y, int Z, ushort Heading);` Task 2 builds one per realm from that realm's Teleport row, with `Name = TeleportIds[realm]`.
  - `public enum SiStartOutcome { Accept, Decline, Superseded, NotNow }`
  - `public static class SiStartChoice`:
    - `AnswerKey = "hearthdaoc_si_start"`, `AnswerYes = "yes"`, `AnswerNo = "no"` (constants).
    - `TeleportIds`: a read-only `IReadOnlyDictionary<int, string>`: 1 `"Caer Gothwaite"`, 2 `"Aegirhamn"`, 3 `"Grove of Domnann"`.
    - `IsClassicRace(int race)`: true for 1 to 12.
    - `IsSiRegion(int region)`: true for 51, 151 and 181.
    - `Question(int realm)`: the spec's exact text for realms 1, 2 and 3; `null` for any other realm.
    - `ShouldAsk(bool enabled, int level, int race, int region, string savedAnswer, bool hasDestination)`: `enabled && level == 1 && IsClassicRace(race) && !IsSiRegion(region) && string.IsNullOrEmpty(savedAnswer) && hasDestination`. Any non-empty saved value counts as an answer. Task 2 calls it three times: at `GameEntered`, when the timer fires, and in the callback, where its result is passed to `Classify` as `qualifies`.
    - `Classify(byte response, bool stillPending, bool qualifies, bool isAlive)`, checked in this order:
      1. `stillPending` gives `Superseded`, whatever the response and whether or not the character is alive or qualifies.
      2. Otherwise `!isAlive || !qualifies` gives `NotNow`.
      3. Otherwise `0x01` gives `Accept`.
      4. Any other response gives `Decline`.
    - `ApplyAccept(SiStartDestination destination, Func<SiStartDestination, bool> moveTo, Action<SiStartDestination> bind, Action saveCharacter, Action<string> writeAnswer, Action<string> tell)` returns `bool`. It calls `moveTo(destination)` first:
      - **The move fails** (`false`): it calls only `tell("You could not be moved to <Name>. You stay here; you will be asked again at your next login.")` and returns `false`.
      - **The move succeeds:** it calls `bind(destination)`, `saveCharacter()`, `writeAnswer("yes")` and `tell("Welcome to <Name>, in the Shrouded Isles. You are bound here.")`, in that order, and returns `true`.
      - **A delegate throws:** the exception is not caught, so a crash before `writeAnswer` leaves no answer.
  - For Task 2's wiring: `Decline` means `writeAnswer(AnswerNo)` and nothing else (no move). `Superseded` and `NotNow` mean nothing at all.
- Produces, for CI:
  - the step "Server unit tests for the fork's server changes" runs `--filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"`;
  - `deploy/tests/test_workflows.py` pins that exact command line as `SERVER_UNIT_TESTS` and checks that every name in it is a test class in `source/server/Tests/UnitTests`. That check matters because `dotnet test` exits 0 when a filter matches no test. A later task that adds a fork test class changes both lines together.

All commands run from the repository root, on branch `sub3-si-start-choice`.

**Prerequisites (once per clone):**

The test build writes into `source/server/build/` (the Tests project's obj and lib folders). Only a local exclude ignores that folder, as in the owner's clone. The server projects build into `source/server/Debug/`, which `source/server/.gitignore` already ignores. `serverconfig.xml` is already ignored by `source/server/CoreServer/config/.gitignore`, so its exclude line is only a second guard. The build needs the placeholder config, as in CI. Set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` in the shell, as CI does:

```bash
grep -qxF 'source/server/build/' .git/info/exclude || echo 'source/server/build/' >> .git/info/exclude
grep -qxF 'source/server/CoreServer/config/serverconfig.xml' .git/info/exclude || echo 'source/server/CoreServer/config/serverconfig.xml' >> .git/info/exclude
cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
git status --short
```

Expected: no output from `git status --short`.

- [ ] **Step 1: Write the failing test**

Create `source/server/Tests/UnitTests/UT_SiStartChoice.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_SiStartChoice"; echo "rc=$?"`

The first run restores packages and builds the whole server. It took about 25 s here, with the NuGet packages already cached; the time varies.

Expected: the Tests project doesn't compile, because `DOL.GS.HearthDAoC` doesn't exist yet. These 8 errors (the compiler may print them in another order); the output below leaves out the build warnings and shortens `<clone>/source/...` paths and the trailing `[...Tests.csproj]`:
```
source/server/Tests/UnitTests/UT_SiStartChoice.cs(3,14): error CS0234: The type or namespace name 'HearthDAoC' does not exist in the namespace 'DOL.GS' (are you missing an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(16,29): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(17,29): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(18,29): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(20,32): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(218,30): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(222,27): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_SiStartChoice.cs(248,52): error CS0246: The type or namespace name 'SiStartDestination' could not be found (are you missing a using directive or an assembly reference?)
rc=1
```

- [ ] **Step 3: Write the minimal implementation**

```bash
mkdir -p source/server/GameServer/scripts/hearthdaoc
```

Create `source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the Shrouded Isles start choice. A new character of a classic race is asked once, on its
// first entry into the world, whether to begin its journey in its realm's Shrouded Isles town instead of
// its home village. This class holds every decision and reads nothing from the running server
// (GameServer, WorldMgr, ServerProperties), so unit tests drive it directly; SiStartChoiceScript feeds it
// the character's state and carries out the outcome.

// Where an accepted choice takes the character: a realm's SI town arrival point, from its Teleport row.
public sealed record SiStartDestination(string Name, ushort Region, int X, int Y, int Z, ushort Heading);

public enum SiStartOutcome
{
    // Response 0x01 from the player: move, bind, save the character, then save "yes".
    Accept,
    // Any other response from the player: save "no", move nothing.
    Decline,
    // The callback ran while our dialog was still the player's pending one: the server replaced it with
    // another dialog. Nothing is saved, so the question comes back at the next login.
    Superseded,
    // The character is dead or no longer qualifies. Nothing is saved.
    NotNow,
}

public static class SiStartChoice
{
    // Per-character custom parameter (DbCoreCharacterXCustomParam) that holds the answer.
    public const string AnswerKey = "hearthdaoc_si_start";
    public const string AnswerYes = "yes", AnswerNo = "no";

    private const byte AcceptResponse = 0x01;

    // Realm -> TeleportID of the SI town's arrival point (WorldMgr.GetTeleportLocation(realm, ":" + id)).
    public static readonly IReadOnlyDictionary<int, string> TeleportIds = new ReadOnlyDictionary<int, string>(
        new Dictionary<int, string>
        {
            [1] = "Caer Gothwaite",
            [2] = "Aegirhamn",
            [3] = "Grove of Domnann",
        });

    // Briton to Lurikeen. Not Inconnu (13), Valkyn (14), Sylvan (15) or races 16-21.
    public static bool IsClassicRace(int race)
    {
        return race >= 1 && race <= 12;
    }

    // The Shrouded Isles regions: Albion's (51), Midgard's (151) and Hibernia's (181).
    public static bool IsSiRegion(int region)
    {
        return region == 51 || region == 151 || region == 181;
    }

    // The two-button question for a realm, or null for any other realm.
    public static string Question(int realm)
    {
        return realm switch
        {
            1 => "Begin your journey in the Shrouded Isles, at Caer Gothwaite? Decline to stay here.",
            2 => "Begin your journey in the Shrouded Isles, at Aegirhamn? Decline to stay here.",
            3 => "Begin your journey in the Shrouded Isles, at the Grove of Domnann? Decline to stay here.",
            _ => null,
        };
    }

    // Asked when the setting is on, the character is level 1, of a classic race, not already in an SI
    // region, has no saved answer, and its realm's destination is known.
    public static bool ShouldAsk(bool enabled, int level, int race, int region, string savedAnswer, bool hasDestination)
    {
        return enabled
            && level == 1
            && IsClassicRace(race)
            && !IsSiRegion(region)
            && string.IsNullOrEmpty(savedAnswer)
            && hasDestination;
    }

    // stillPending: the player's pending dialog callback is still ours when the callback runs.
    // qualifies: ShouldAsk for the character's state when the callback runs.
    public static SiStartOutcome Classify(byte response, bool stillPending, bool qualifies, bool isAlive)
    {
        if (stillPending)
            return SiStartOutcome.Superseded;

        if (!isAlive || !qualifies)
            return SiStartOutcome.NotNow;

        return response == AcceptResponse ? SiStartOutcome.Accept : SiStartOutcome.Decline;
    }

    // Applies an accepted choice in the spec's order; returns true when the answer was written.
    // Move first. If the move fails, nothing is bound, saved or written, the player is told, and the
    // question comes back at the next login. Otherwise bind there, save the character, write "yes", and
    // tell the player where they are. The answer comes last, so a crash before it leaves no answer.
    public static bool ApplyAccept(SiStartDestination destination, Func<SiStartDestination, bool> moveTo,
        Action<SiStartDestination> bind, Action saveCharacter, Action<string> writeAnswer, Action<string> tell)
    {
        if (!moveTo(destination))
        {
            tell($"You could not be moved to {destination.Name}. You stay here; you will be asked again at your next login.");
            return false;
        }

        bind(destination);
        saveCharacter();
        writeAnswer(AnswerYes);
        tell($"Welcome to {destination.Name}, in the Shrouded Isles. You are bound here.");
        return true;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_SiStartChoice" 2>&1 | tail -1`

Expected: all 48 cases pass. That is 20 test methods; the `[TestCase]` and `[TestCaseSource]` rows count separately. The duration varies:
```
Passed!  - Failed:     0, Passed:    48, Skipped:     0, Total:    48, Duration: 46 ms - Tests.dll (net10.0)
```

- [ ] **Step 5: Write the failing CI pin test**

In `deploy/tests/test_workflows.py`, insert this block just above the last two lines, `if __name__ == "__main__":` and `unittest.main()`. Keep two blank lines after `ClientPatchWorkflowTests` and two before `if __name__`:

```python
SERVER_UNIT_TESTS = ("dotnet test source/server/Tests/Tests.csproj --nologo --filter "
                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"')
UNIT_TESTS = os.path.join(ROOT, "source", "server", "Tests", "UnitTests")


class ServerUnitTestWorkflowTests(unittest.TestCase):
    """CI runs the fork's own server unit tests by name. dotnet test passes (exit 0) when its filter matches
    no test at all, so every name in the filter must be a test class in source/server/Tests/UnitTests."""

    def test_the_fork_server_unit_tests_run_with_the_pinned_filter(self):
        steps = job(read("server-image.yml"), TEST_JOB).split("\n      - ")
        found = [s for s in steps if s.startswith("name: Server unit tests for the fork's server changes\n")]
        self.assertEqual(len(found), 1)
        self.assertIn('DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: "0"', found[0])
        self.assertIn("cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml\n", found[0])
        self.assertIn(SERVER_UNIT_TESTS + "\n", found[0])

    def test_every_name_in_the_filter_is_a_test_class(self):
        names = re.findall(r"FullyQualifiedName~(\w+)", SERVER_UNIT_TESTS)
        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice"])
        for name in names:
            with open(os.path.join(UNIT_TESTS, name + ".cs"), encoding="utf-8") as f:
                self.assertIn(f"public sealed class {name}\n", f.read(), name)
```

- [ ] **Step 6: Run the pin test to verify it fails**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_workflows.py -k ServerUnitTestWorkflowTests -v`

Expected: the class check passes, because `UT_SiStartChoice.cs` exists since Step 1. The filter check fails, because the workflow still runs only `UT_CommandPrivLevelOverrides`:
```
test_every_name_in_the_filter_is_a_test_class (tests.test_workflows.ServerUnitTestWorkflowTests.test_every_name_in_the_filter_is_a_test_class) ... ok
test_the_fork_server_unit_tests_run_with_the_pinned_filter (tests.test_workflows.ServerUnitTestWorkflowTests.test_the_fork_server_unit_tests_run_with_the_pinned_filter) ... FAIL

======================================================================
FAIL: test_the_fork_server_unit_tests_run_with_the_pinned_filter (tests.test_workflows.ServerUnitTestWorkflowTests.test_the_fork_server_unit_tests_run_with_the_pinned_filter)
----------------------------------------------------------------------
Traceback (most recent call last):
  File "<clone>/deploy/tests/test_workflows.py", line 236, in test_the_fork_server_unit_tests_run_with_the_pinned_filter
    self.assertIn(SERVER_UNIT_TESTS + "\n", found[0])
AssertionError: 'dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"\n' not found in 'name: Server unit tests...

----------------------------------------------------------------------
Ran 2 tests in 0.001s

FAILED (failures=1)
```

- [ ] **Step 7: Extend the CI filter**

```bash
sed -i 's/--filter "FullyQualifiedName~UT_CommandPrivLevelOverrides"$/--filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"/' .github/workflows/server-image.yml
git diff --stat .github/workflows/server-image.yml
git diff -U0 .github/workflows/server-image.yml | grep '^[-+] '
```

Expected:
```
 .github/workflows/server-image.yml | 2 +-
 1 file changed, 1 insertion(+), 1 deletion(-)
-          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides"
+          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_workflows.py -v 2>&1 | grep -E 'ServerUnitTestWorkflowTests|^Ran|^OK|FAIL'`

Expected: the whole workflow test file passes, 16 tests, including the two new ones; the time varies. The `ClientPatchWorkflowTests` need `ruby`; without it they are skipped and the last line reads `OK (skipped=6)`:
```
test_every_name_in_the_filter_is_a_test_class (tests.test_workflows.ServerUnitTestWorkflowTests.test_every_name_in_the_filter_is_a_test_class) ... ok
test_the_fork_server_unit_tests_run_with_the_pinned_filter (tests.test_workflows.ServerUnitTestWorkflowTests.test_the_fork_server_unit_tests_run_with_the_pinned_filter) ... ok
Ran 16 tests in 0.893s
OK
```

Then run the CI step's own command, with both fork test classes:

Run: `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml && dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice" 2>&1 | tail -1`

Expected: 3 `UT_CommandPrivLevelOverrides` tests plus the 48 new ones:
```
Passed!  - Failed:     0, Passed:    51, Skipped:     0, Total:    51, Duration: 44 ms - Tests.dll (net10.0)
```

Run: `git status --short --untracked-files=all`

Expected: only this task's four files; the build output is ignored:
```
 M .github/workflows/server-image.yml
 M deploy/tests/test_workflows.py
?? source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs
?? source/server/Tests/UnitTests/UT_SiStartChoice.cs
```

- [ ] **Step 9: Commit**

```bash
git add source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs source/server/Tests/UnitTests/UT_SiStartChoice.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -q -m "feat(server): SiStartChoice decides the Shrouded Isles start question

SiStartChoice holds every decision of the Shrouded Isles start choice and
reads nothing from the running server, so unit tests drive it directly:
who is asked (setting on, level 1, classic race 1-12, not in region 51, 151
or 181, no saved answer, realm destination known), the realm's question and
SI town, how a dialog response is classified (accept, decline, superseded,
not now) and the accept order (move; if that fails tell the player and save
nothing; else bind, save the character, write yes, tell the player).

UT_SiStartChoice covers each case of the spec's test list. CI runs it next
to UT_CommandPrivLevelOverrides, and test_workflows.py pins that filter and
checks every name in it is a real test class, since dotnet test passes when
a filter matches nothing.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline -1 && git status --short --untracked-files=all
```

Expected: the new commit (the hash differs) and a clean tree:
```
76871ac feat(server): SiStartChoice decides the Shrouded Isles start question
```

---

### Task 2: SiStartChoiceScript, the game wiring, and the docs

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs` (the server property and the game wiring; compiled into `GameServer.dll` like every file under `GameServer/`)
- Modify: `source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs` (one new method, `Qualifies`)
- Test: `source/server/Tests/UnitTests/UT_SiStartChoice.cs` (one helper and three tests for `Qualifies`)
- Modify: `docs/fork/FORK.md` (one row in the server-code changes table)
- Modify: `client/README.md` (one paragraph in "Classic character creation")

**Interfaces:**
- Consumes, from Task 1 (`SiStartChoice.cs`, namespace `DOL.GS.HearthDAoC`): `SiStartDestination`, `SiStartOutcome`, and from `SiStartChoice` the members `AnswerKey`, `AnswerNo`, `TeleportIds`, `Question(realm)`, `ShouldAsk(...)`, `Classify(...)` and `ApplyAccept(...)`. `ApplyAccept` writes `yes` itself. From `UT_SiStartChoice.cs` the tests use the constant `Albion` and the enum `eRace`.
- Produces, in `SiStartChoice.cs`:
  - `public static bool Qualifies(bool enabled, int level, int race, int region, Func<string> readSavedAnswer, bool hasDestination)`. It gives the same verdict as `ShouldAsk` with `readSavedAnswer()`, but it calls `readSavedAnswer` only when `ShouldAsk(..., null, ...)` is already true, and then once. The script reads the saved answer from the database, so a login that can't be asked costs no query. This method is an addition to the plan's fixed interface; the fixed names are unchanged.
- Produces, in `SiStartChoiceScript.cs` (namespace `DOL.GS.HearthDAoC`, file-scoped, LF line endings, plain ASCII):
  - `public static class SiStartChoiceScript`
  - `[ServerProperty("server", "si_start_choice", "<description>", false)] public static bool SI_START_CHOICE;` The description is one string literal, exactly `Ask new level 1 characters of the classic races whether to start in their realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)`. Task 3 copies it as `SI_DESCRIPTION`, and its test `test_server_declares_the_property_with_the_same_description` reads the attribute with a regex.
  - `[ScriptLoadedEvent] public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)`: builds the realm-to-destination table from `WorldMgr.GetTeleportLocation((eRealm)realm, ":" + TeleportIds[realm])`, with `Name = TeleportIds[realm]`, then adds the `GamePlayerEvent.GameEntered` handler. For each missing row it logs one warning: `Shrouded Isles start choice: no Teleport row "<TeleportID>" for realm <n>, so that realm's new characters are not asked.`
  - `[ScriptUnloadedEvent] public static void OnScriptUnloaded(...)`: removes the handler.
  - Everything else is private:
    - `OnGameEntered`: a `GamePlayer` sender that qualifies gets a timer of 5000 ms (`AskDelay`).
    - `AskIfStillHere`: when the timer fires, it sends the dialog only if the player's `ObjectState` is `Active`, `Client.Player` is still this player, `Client.ClientState` is `Playing`, and the player still qualifies. It returns 0, so the timer runs once.
    - `OnResponse`: the dialog callback. It is kept in one static delegate, `ResponseCallback`, so `stillPending` is `player.CustomDialogCallback == ResponseCallback`. The outcomes:
      - `Accept` calls `ApplyAccept` with `MoveTo`, the five `Bind*` setters, `GameServer.Database.SaveObject(player.DBCharacter)`, the answer write and `Out.SendMessage(..., CT_System, CL_SystemWindow)`.
      - `Decline` writes `no`.
      - `Superseded` and `NotNow` do nothing.
    - `SelectAnswer` and `WriteAnswer`: one `DbCoreCharacterXCustomParam` row per character, selected by `DOLCharactersObjectId` and `KeyName`, then added or updated. This is the only write path.
  - Error logs: the timer and the callback catch every exception and log `Shrouded Isles start choice: could not ask <name>` or `... could not apply the answer of <name>`.
- Produces, docs: a `docs/fork/FORK.md` row ("Upstream files touched: none", "Candidate: off by default") and a `client/README.md` paragraph that starts "**Where you start.**".

**Notes (checked against the source on this branch; paths under `source/server/`):**
- **Event.** `GamePlayerEvent.GameEntered`: `GameServer/events/gameobjects/GamePlayerEvent.cs:53`.
  - It fires once per login, when `player.EnteredGame` is still false (`GameServer/packets/Client/168/PlayerInitRequestHandler.cs:37-41`). That is before `ShowPatchNotes` (:42), `SendPlayerInitFinished` (:63) and `SendStarterHelp` (:76-77), hence the delay.
  - `GameObject.Notify` forwards to `GameEventMgr.Notify` (`GameServer/gameobjects/GameObject.cs:900-903`), which calls the global handlers (`GameServer/events/GameEventMgr.cs:230`, the call at :253).
- **Handlers.** `GameEventMgr.AddHandler(DOLEvent, DOLEventHandler)` is at `GameServer/events/GameEventMgr.cs:84` and `RemoveHandler` at :154. The delegate is `GameServer/events/DOLEventHandlerCollection.cs:7`.
  - A handler that throws is logged and skipped (`GameServer/events/DOLEventHandlerChain.cs:85-93`). `BountyMasterRuntime` adds the same handler the same way (`GameServer/scripts/quests/Bounty/BountyMasterRuntime.cs:37-59`).
- **Script events.** `[ScriptLoadedEvent]` and `[ScriptUnloadedEvent]` are registered on public static methods of `GameServer.dll` (`GameServer/GameServer.cs:759-760`, `GameServer/events/GameEventMgr.cs:53-82`, `BindingFlags.Public | BindingFlags.Static` at :64).
  - `ScriptEvent.Loaded` fires at `GameServer/GameServer.cs:502`. That is after `WorldMgr.EarlyInit` (:376), which loads the Teleport table (`GameServer/world/WorldMgr.cs:251-252`).
  - Upstream loads that table only when info logging is on. If it is not loaded, `GetTeleportLocation` throws, the event chain logs the error, the handler is never added, and nobody is asked. The towns' own teleporters need the same table.
- **Teleport rows.** `WorldMgr.GetTeleportLocation(eRealm, string)` is at `GameServer/world/WorldMgr.cs:91`. The key is `"{Type}:{TeleportID}"` (:400), and the first row per key wins (:401-406).
  - `DbTeleport` (`CoreDatabase/Tables/DbTeleport.cs:30-72`) has the strings `Type` and `TeleportID`, and the ints `Realm`, `RegionID`, `X`, `Y`, `Z` and `Heading`. Hence the `(ushort)` casts for the region and the heading.
- **Timer.** `new ECSGameTimer(GameObject, ECSTimerCallback, int)` starts at once (`GameServer/ECS-Services/TimerService.cs:96-102`). The callback returns the next interval, and 0 stops the timer (:125-136, delegate at :73).
  - A callback that throws gets its `GamePlayer` owner kicked to the character screen (`TimerService.cs:64-66`, `GameServer/ECS-Services/GameServiceUtils.cs:25`, :37-41). That is why `AskIfStillHere` catches everything.
  - Precedent: `new ECSGameTimer(player, timer => ..., 3000)` in `GameServer/scripts/quests/Albion/shrouded isles/LostStoneOfArawn.cs:673`.
- **Dialog.**
  - `CustomDialogResponse(GamePlayer player, byte response)` is at `GameServer/packets/Server/IPacketLib.cs:508`, and `IPacketLib.SendCustomDialog` at :666.
  - The implementation (`GameServer/packets/Server/PacketLib168.cs:1556-1566`) first calls a still-pending callback with `0x00`, then stores the new one. It sends a yes/no dialog when the callback isn't null (:1576) and auto-wraps text without `\n` (:1577).
  - A click clears `CustomDialogCallback` before calling it (`GameServer/packets/Client/168/DialogResponseHandler.cs:29-35`). `GamePlayer.CustomDialogCallback` is at `GameServer/gameobjects/GamePlayer.cs:10709`.
  - Delegates compare by method and target, so one static instance is enough.
  - A packet handler that throws is only logged (`GameServer/packets/Server/PacketProcessor.cs:190-201`), but the callback is also called inside another script's `SendCustomDialog`, so `OnResponse` catches everything too.
- **Move.** `GamePlayer.MoveTo(ushort regionID, int x, int y, int z, ushort heading)` is at `GameServer/gameobjects/GamePlayer.cs:7914`. It returns false when the region is missing or zoning there isn't allowed, when there is no zone at x, y, or when `RemoveFromWorld` fails (:7927-7943).
  - The position setters write through to `DBCharacter` (`X` at :8222-8230, `CurrentRegion` at :8276-8282), so the save after the move stores the new position too.
- **Bind.** `BindRegion`, `BindXpos`, `BindYpos`, `BindZpos` and `BindHeading` are int properties that write to `DBCharacter` (`GameServer/gameobjects/GamePlayer.cs:605-649`). `GamePlayer.Bind()` (:1246) sets the same five and saves (:1278-1284).
- **Save.**
  - `GamePlayer.DBCharacter` is `internal` (`GameServer/gameobjects/GamePlayer.cs:247`). The script compiles into the same `GameServer.dll`, and upstream calls `GameServer.Database.SaveObject(client.Player.DBCharacter)` the same way in `GameServer/commands/playercommands/api.cs:28`. So this one member is not public API, although the spec's risk table says only public API is used; it is the call the spec itself names for the save.
  - `SaveObject(DataObject)` and `AddObject(DataObject)`: `CoreDatabase/ObjectDatabase.cs:168` and :83. `SaveObject` writes only dirty objects (:193). Saving the character also saves its loaded custom-parameter relation, but never deletes rows (`CoreDatabase/ObjectDatabase.cs:322-400`), so the answer row stays.
- **Answer row.**
  - `DOLDB<T>.SelectObject(WhereClause)` (`GameServer/database/DOLDB.cs:40`) returns the first match or null (`CoreDatabase/ObjectDatabase.cs:758-762`). `DB.Column(...)`, `.IsEqualTo(...)` and `.And(...)` are at `CoreDatabase/WhereClause.cs:362`, :380 and :58.
  - `DbCoreCharacterXCustomParam(string DOLCharactersObjectId, string KeyName, string Value)` is at `CoreDatabase/Tables/DbCoreCharacterXCustomParam.cs:29`; `KeyName` and `Value` are at `CoreDatabase/CustomParam.cs:17` and :28.
  - `player.ObjectId` is `DBCharacter.ObjectId` (`GameServer/gameobjects/GamePlayer.cs:345-349`).
  - Pattern: `BountyQuest.RememberCompletedTarget` (`GameServer/scripts/quests/Bounty/BountyQuest.cs:505-530`) and its read (:490-492).
- **States.** `GameObject.eObjectState.Active` is at `GameServer/gameobjects/GameObject.cs:32-37`, and `ObjectState` at :63. `GameClient.eClientState.Playing` is at `GameServer/GameClient.cs:499-505`, with `ClientState` at :36 and `Player` at :56. `GamePlayer.Client` is at `GameServer/gameobjects/GamePlayer.cs:231`, and `IsAlive` at `GameServer/gameobjects/GameLiving.cs:448`.
- **Logger.** `BountyMasterRuntime` itself has no logger. The pattern is `BountyQuest.cs:27` (`Logging.LoggerManager.Create(typeof(...))`) and `PlayerInitRequestHandler.cs:17`. `LoggerManager.Create(Type)` is at `CoreBase/Logging/Logger/LoggerManager.cs:86`, `Warn(string)` at `CoreBase/Logging/Logger/Logger.cs:89`, and `Error(string, Exception)` at :119. The namespace is `DOL.Logging`.
- **Property.**
  - `ServerPropertyAttribute(category, key, description, defaultValue)` is at `GameServer/serverproperty/ServerPropertyAttribute.cs:41`. Every public static field with it in a loaded assembly is found (`GameServer/serverproperty/ServerProperties.cs:2577-2616`); a new row gets the description and the default.
  - Precedent: `start_as_base_class` in `GameServer/scripts/startup/script/StartAsBaseClass.cs:36-37`.
- **Bots.** `GameBot : GameNPC` (`GameServer/bots/GameBot.cs:32`), so `sender is GamePlayer` leaves bots out.
- **Messages.** `eChatType` and `eChatLoc` are in `DOL.GS.PacketHandler` (`GameServer/packets/Server/IPacketLib.cs:252`, :262).

All commands run from the repository root, on branch `sub3-si-start-choice`, with Task 1 committed.

**Prerequisites (once per clone; the same as Task 1):**

The test build writes into `source/server/build/`, and only a local exclude ignores that folder. The Release build writes into `source/server/Release/`, which `source/server/.gitignore` already ignores. The build needs the placeholder config, as in CI. Set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` in the shell, as CI does:

```bash
grep -qxF 'source/server/build/' .git/info/exclude || echo 'source/server/build/' >> .git/info/exclude
grep -qxF 'source/server/CoreServer/config/serverconfig.xml' .git/info/exclude || echo 'source/server/CoreServer/config/serverconfig.xml' >> .git/info/exclude
cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
git status --short
```

Expected: no output from `git status --short`.

- [ ] **Step 1: Write the failing test**

In `source/server/Tests/UnitTests/UT_SiStartChoice.cs`, insert this block just above the line `    // Records every call ApplyAccept makes, in order.`. That puts it after the test `QualificationIsCheckedAgainWhenTheCallbackRuns`. Leave one blank line before and after the block:

```csharp
    // Qualifies for a qualifying character, with one thing changed per call. reads counts how often it
    // read the saved answer, which the script reads from the database.
    private static bool QualifiesReading(out int reads, bool enabled = true, int level = 1, eRace race = eRace.Briton,
        int region = Albion, string saved = null, bool hasDestination = true)
    {
        int count = 0;
        bool result = SiStartChoice.Qualifies(enabled, level, (int)race, region,
            () =>
            {
                count++;
                return saved;
            },
            hasDestination);
        reads = count;
        return result;
    }

    [Test]
    public void QualifiesGivesTheSameVerdictAsShouldAsk()
    {
        foreach (bool enabled in new[] { true, false })
        foreach (int level in new[] { 1, 2 })
        foreach (int race in new[] { 1, 12, 13, 16 })
        foreach (int region in new[] { 1, 51, 100, 151, 181, 200 })
        foreach (string saved in new[] { null, "", SiStartChoice.AnswerYes, SiStartChoice.AnswerNo })
        foreach (bool hasDestination in new[] { true, false })
        {
            Assert.That(SiStartChoice.Qualifies(enabled, level, race, region, () => saved, hasDestination),
                Is.EqualTo(SiStartChoice.ShouldAsk(enabled, level, race, region, saved, hasDestination)),
                $"enabled {enabled}, level {level}, race {race}, region {region}, saved '{saved}', destination {hasDestination}");
        }
    }

    [Test]
    public void QualifiesReadsTheSavedAnswerOnceWhenAllElseQualifies()
    {
        Assert.That(QualifiesReading(out int reads), Is.True);
        Assert.That(reads, Is.EqualTo(1));
        Assert.That(QualifiesReading(out reads, saved: SiStartChoice.AnswerYes), Is.False);
        Assert.That(reads, Is.EqualTo(1));
        Assert.That(QualifiesReading(out reads, saved: SiStartChoice.AnswerNo), Is.False);
        Assert.That(reads, Is.EqualTo(1));
    }

    // Most logins can't be asked. They never read the saved answer, so they cost no database query.
    [Test]
    public void QualifiesDoesNotReadTheSavedAnswerWhenSomethingElseRulesItOut()
    {
        Assert.That(QualifiesReading(out int reads, enabled: false), Is.False);
        Assert.That(reads, Is.Zero, "setting off");
        Assert.That(QualifiesReading(out reads, level: 2), Is.False);
        Assert.That(reads, Is.Zero, "level 2");
        Assert.That(QualifiesReading(out reads, race: eRace.Inconnu), Is.False);
        Assert.That(reads, Is.Zero, "Inconnu");
        Assert.That(QualifiesReading(out reads, region: 51), Is.False);
        Assert.That(reads, Is.Zero, "region 51");
        Assert.That(QualifiesReading(out reads, hasDestination: false), Is.False);
        Assert.That(reads, Is.Zero, "no destination");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_SiStartChoice"; echo "rc=$?"`

Expected: the Tests project doesn't compile, because `SiStartChoice.Qualifies` doesn't exist yet. The output below leaves out the build warnings and shortens `<clone>/source/...` paths and the trailing `[...Tests.csproj]`:
```
source/server/Tests/UnitTests/UT_SiStartChoice.cs(220,37): error CS0117: 'SiStartChoice' does not contain a definition for 'Qualifies'
source/server/Tests/UnitTests/UT_SiStartChoice.cs(241,39): error CS0117: 'SiStartChoice' does not contain a definition for 'Qualifies'
rc=1
```

- [ ] **Step 3: Write the minimal implementation**

In `source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs`, insert this method between `ShouldAsk` and the comment line `    // stillPending: the player's pending dialog callback is still ours when the callback runs.`. Leave one blank line before and after it. `using System;` is already there, for `Func`:

```csharp
    // ShouldAsk, but it reads the saved answer only when everything else already qualifies. The script
    // reads the answer from the database, so most logins cost no query.
    public static bool Qualifies(bool enabled, int level, int race, int region, Func<string> readSavedAnswer,
        bool hasDestination)
    {
        return ShouldAsk(enabled, level, race, region, null, hasDestination)
            && ShouldAsk(enabled, level, race, region, readSavedAnswer(), hasDestination);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_SiStartChoice" 2>&1 | tail -1`

Expected: Task 1's 48 cases plus the 3 new tests pass; the duration varies:
```
Passed!  - Failed:     0, Passed:    51, Skipped:     0, Total:    51, Duration: 41 ms - Tests.dll (net10.0)
```

- [ ] **Step 5: Write the game wiring**

The wiring needs a running server and a client, so it has no unit test. Its decisions are the ones Steps 1-4 and Task 1 test, and the owner checks it in game (Task 4). Here it must build cleanly (Step 6) and use only the calls listed in the notes (Step 8).

Create `source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs`:

```csharp
using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the game wiring of the Shrouded Isles start choice. A few seconds after a qualifying
// character's first entry into the world, the server asks with the client's two-button dialog whether to
// begin in its realm's Shrouded Isles town. SiStartChoice makes every decision; this class reads the
// character's state, sends the question and carries out the answer. Off by default; HearthDAoC turns it on
// through HEARTHDAOC_SI_START_CHOICE.
public static class SiStartChoiceScript
{
    [ServerProperty("server", "si_start_choice",
        "Ask new level 1 characters of the classic races whether to start in their realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)",
        false)]
    public static bool SI_START_CHOICE;

    // GameEntered fires before the server sends "player init finished", the patch notes and the starter
    // help, so the question waits this long.
    private const int AskDelay = 5000;

    private static readonly Logger Log = LoggerManager.Create(typeof(SiStartChoiceScript));

    // One delegate instance, so the callback can tell whether it is still the player's pending dialog.
    private static readonly CustomDialogResponse ResponseCallback = OnResponse;

    // Realm -> its SI town's arrival point, read from the world's Teleport rows at script load.
    private static IReadOnlyDictionary<int, SiStartDestination> _destinations =
        new Dictionary<int, SiStartDestination>();

    [ScriptLoadedEvent]
    public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
    {
        Dictionary<int, SiStartDestination> destinations = new();

        foreach (KeyValuePair<int, string> pair in SiStartChoice.TeleportIds)
        {
            // The towns' own SI teleporters use these rows: Type is empty, and the key is case-sensitive.
            DbTeleport row = WorldMgr.GetTeleportLocation((eRealm)pair.Key, ":" + pair.Value);
            if (row == null)
            {
                Log.Warn($"Shrouded Isles start choice: no Teleport row \"{pair.Value}\" for realm {pair.Key}, " +
                    "so that realm's new characters are not asked.");
                continue;
            }

            destinations[pair.Key] = new SiStartDestination(pair.Value, (ushort)row.RegionID, row.X, row.Y, row.Z,
                (ushort)row.Heading);
        }

        _destinations = destinations;
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, OnGameEntered);
    }

    [ScriptUnloadedEvent]
    public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, OnGameEntered);
    }

    // Fires once per login (PlayerInitRequestHandler). Bots are GameBot, a GameNPC, so they never get here.
    private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || !Qualifies(player))
            return;

        new ECSGameTimer(player, timer => AskIfStillHere(player), AskDelay);
    }

    // PlayerInit can still move a player after GameEntered, so the timer asks only if the player is still in
    // the game on the same client and still qualifies.
    private static int AskIfStillHere(GamePlayer player)
    {
        try
        {
            GameClient client = player.Client;
            if (player.ObjectState == GameObject.eObjectState.Active
                && client.Player == player
                && client.ClientState == GameClient.eClientState.Playing
                && Qualifies(player))
                player.Out.SendCustomDialog(SiStartChoice.Question((int)player.Realm), ResponseCallback);
        }
        catch (Exception ex)
        {
            // A timer that throws sends its owner to the character screen, so this one only logs.
            Log.Error($"Shrouded Isles start choice: could not ask {player.Name}", ex);
        }

        return 0;
    }

    private static void OnResponse(GamePlayer player, byte response)
    {
        try
        {
            // A click clears the pending callback before calling it (DialogResponseHandler). When the server
            // sends another dialog over ours, it calls ours with 0x00 while ours is still pending.
            bool stillPending = player.CustomDialogCallback == ResponseCallback;
            // Classify ignores qualification for a dialog still pending, so only an answer reads the database.
            bool qualifies = !stillPending && Qualifies(player);

            switch (SiStartChoice.Classify(response, stillPending, qualifies, player.IsAlive))
            {
                case SiStartOutcome.Accept:
                    SiStartChoice.ApplyAccept(_destinations[(int)player.Realm],
                        d => player.MoveTo(d.Region, d.X, d.Y, d.Z, d.Heading),
                        d => Bind(player, d),
                        () => GameServer.Database.SaveObject(player.DBCharacter),
                        answer => WriteAnswer(player, answer),
                        message => player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow));
                    break;
                case SiStartOutcome.Decline:
                    WriteAnswer(player, SiStartChoice.AnswerNo);
                    break;
                // Superseded or NotNow: nothing is saved, so the question comes back at the next login.
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Shrouded Isles start choice: could not apply the answer of {player.Name}", ex);
        }
    }

    // The character's state now. The saved answer is read only when everything else qualifies.
    private static bool Qualifies(GamePlayer player)
    {
        return SiStartChoice.Qualifies(SI_START_CHOICE, player.Level, player.Race, player.CurrentRegionID,
            () => SelectAnswer(player)?.Value, _destinations.ContainsKey((int)player.Realm));
    }

    // As GamePlayer.Bind() does, but at the destination instead of where the player stands.
    private static void Bind(GamePlayer player, SiStartDestination destination)
    {
        player.BindRegion = destination.Region;
        player.BindXpos = destination.X;
        player.BindYpos = destination.Y;
        player.BindZpos = destination.Z;
        player.BindHeading = destination.Heading;
    }

    // The answer is one custom parameter row per character (BountyQuest.RememberCompletedTarget pattern).
    private static DbCoreCharacterXCustomParam SelectAnswer(GamePlayer player)
    {
        return DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
            DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId)
                .And(DB.Column("KeyName").IsEqualTo(SiStartChoice.AnswerKey)));
    }

    // The only write path: update the character's row when it has one, otherwise add it.
    private static void WriteAnswer(GamePlayer player, string answer)
    {
        DbCoreCharacterXCustomParam record = SelectAnswer(player);
        if (record == null)
            GameServer.Database.AddObject(
                new DbCoreCharacterXCustomParam(player.ObjectId, SiStartChoice.AnswerKey, answer));
        else
        {
            record.Value = answer;
            GameServer.Database.SaveObject(record);
        }
    }
}
```

- [ ] **Step 6: Build the server as the image does**

`deploy/Dockerfile` builds the server with `dotnet build source/server/DOLLinux.sln -c Release`. `--no-incremental` recompiles every project, so the warning count is the full one.

Run: `dotnet build source/server/DOLLinux.sln -c Release --nologo --no-incremental 2>&1 | grep -E "scripts/hearthdaoc/|GameServer -> |Warning\(s\)|Error\(s\)"; echo "rc=${PIPESTATUS[0]}"`

The build took about 25 s here; the time varies.

Expected: no warning or error line names a file under `scripts/hearthdaoc/`. All the warnings are upstream's: the same 639 come from the Task 1 commit alone, and the count may differ with another SDK. The path is shortened:
```
  GameServer -> <clone>/source/server/Release/lib/GameServer.dll
    639 Warning(s)
    0 Error(s)
rc=0
```

- [ ] **Step 7: Run the unit tests again**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_SiStartChoice" 2>&1 | tail -1`

Expected (the duration varies):
```
Passed!  - Failed:     0, Passed:    51, Skipped:     0, Total:    51, Duration: 45 ms - Tests.dll (net10.0)
```

Then run the CI step's own command:

Run: `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml && dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice" 2>&1 | tail -1`

Expected: the 3 `UT_CommandPrivLevelOverrides` tests plus the 51 `UT_SiStartChoice` tests:
```
Passed!  - Failed:     0, Passed:    54, Skipped:     0, Total:    54, Duration: 52 ms - Tests.dll (net10.0)
```

- [ ] **Step 8: Static checks**

First, list every upstream member the script uses:

Run: `grep -oE "\b(GameEventMgr|GamePlayerEvent|WorldMgr|GameServer\.Database|DOLDB<\w+>|player\.Out|player|client)\.\w+|new [A-Z]\w+" source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs | sort | uniq -c`

Expected: only the calls in the notes above. `player.DBCharacter` is the one internal member; the spec names it for the save. The sort order can differ with the locale:
```
      1 client.ClientState
      1 client.Player
      1 DOLDB<DbCoreCharacterXCustomParam>.SelectObject
      1 GameEventMgr.AddHandler
      1 GameEventMgr.RemoveHandler
      2 GamePlayerEvent.GameEntered
      1 GameServer.Database.AddObject
      2 GameServer.Database.SaveObject
      1 new DbCoreCharacterXCustomParam
      1 new Dictionary
      1 new ECSGameTimer
      1 new SiStartDestination
      1 player.BindHeading
      1 player.BindRegion
      1 player.BindXpos
      1 player.BindYpos
      1 player.BindZpos
      1 player.Client
      1 player.CurrentRegionID
      1 player.CustomDialogCallback
      1 player.DBCharacter
      1 player.IsAlive
      1 player.Level
      1 player.MoveTo
      2 player.Name
      2 player.ObjectId
      1 player.ObjectState
      1 player.Out.SendCustomDialog
      1 player.Out.SendMessage
      1 player.Race
      3 player.Realm
      1 WorldMgr.GetTeleportLocation
```

Next, run Task 3's own check of the property declaration, so the deploy script and the server agree on the description:

```bash
python3 - <<'EOF'
import re
with open("source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs", encoding="utf-8") as f:
    print(re.findall(r'\[ServerProperty\(\s*"([^"]*)",\s*"si_start_choice",\s*"([^"]*)",\s*(\w+)\s*\)\]', f.read()))
EOF
```

Expected:
```
[('server', "Ask new level 1 characters of the classic races whether to start in their realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)", 'false')]
```

Last, stage this task's server files and check that sub-project 3 adds files under `source/server` and changes no upstream file. `32ddb77` is the `main` commit this branch starts from:

```bash
git add source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs source/server/Tests/UnitTests/UT_SiStartChoice.cs
git diff --cached --name-status 32ddb77 -- source/server
file source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs
```

Expected: three added files (`A`), no modified one; and plain ASCII with LF line endings (`file` would add "with CRLF line terminators" otherwise):
```
A	source/server/GameServer/scripts/hearthdaoc/SiStartChoice.cs
A	source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs
A	source/server/Tests/UnitTests/UT_SiStartChoice.cs
source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs: ASCII text
```

- [ ] **Step 9: Commit the server change**

```bash
git commit -q -m "feat(server): ask new characters the Shrouded Isles start question

SiStartChoiceScript wires SiStartChoice into the game. It declares the
si_start_choice server property (off by default), reads each realm's SI
town arrival point from the world's Teleport rows at script load (a
missing row turns that realm off, with one warning) and handles
GamePlayerEvent.GameEntered. About five seconds after a qualifying
character enters the world, if it is still in the game on the same client
and still qualifies, the server sends the client's two-button dialog.
SiStartChoice classifies the answer: accept moves, binds, saves the
character and writes yes; decline writes no; a superseded dialog, or an
answer while dead or no longer qualifying, saves nothing. The answer is
one DbCoreCharacterXCustomParam row per character (hearthdaoc_si_start),
selected and then added or updated.

SiStartChoice.Qualifies reads the saved answer only when everything else
qualifies, so a login that can't be asked costs no database query.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline -1 && git status --short --untracked-files=all
```

Expected: the new commit (the hash differs). Nothing else is changed yet, so the status prints nothing:
```
7eaab6a feat(server): ask new characters the Shrouded Isles start question
```

- [ ] **Step 10: Document the change**

In `docs/fork/FORK.md`, add this row to the server-code changes table, right after the `command_plvl_overrides` row. It is one line:

```markdown
| Shrouded Isles start choice: `si_start_choice` server property | `GameServer/scripts/hearthdaoc/SiStartChoice.cs` (the decisions), `GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs` (the property and the game wiring), test `Tests/UnitTests/UT_SiStartChoice.cs`. Upstream files touched: none | A new character of a classic race is asked once, a few seconds after its first entry into the world, whether to begin in its realm's Shrouded Isles town (#40, [spec](specs/2026-10-06-shrouded-isles-start-choice-design.md)); set from `HEARTHDAOC_SI_START_CHOICE` (default on) | Candidate: off by default |
```

In `client/README.md`, section "Classic character creation", add this paragraph at the end of the section: after the paragraph that ends "then works with the standard creation screen." and before `## What happens at each launch`. Leave one blank line before and after it:

```markdown
**Where you start.** A new character of a classic race, such as a Briton, Troll or Celt (not an
Inconnu, Valkyn or Sylvan), first enters the world in its usual home village. A few seconds later, a
two-button question asks whether to begin your journey in your realm's Shrouded Isles town instead:
Caer Gothwaite, Aegirhamn or the Grove of Domnann. Accept takes you there and makes it your bind
point, where you return after a death; Decline keeps you at home. Each character is asked only once.
If the question goes away unanswered, for example because you logged out, it comes back at your next
login while the character is still level 1. The question comes from the server, not from the patch,
so it also appears with the standard creation screen, unless the server has turned it off.
```

Run: `git diff --stat`

Expected:
```
 client/README.md  | 9 +++++++++
 docs/fork/FORK.md | 1 +
 2 files changed, 10 insertions(+)
```

- [ ] **Step 11: Commit the docs**

```bash
git add docs/fork/FORK.md client/README.md
git commit -q -m "docs(fork): list the Shrouded Isles start choice and tell players

FORK.md lists the new server files (no upstream file touched; an upstream
candidate, off by default). The player guide explains the question a new
character of a classic race gets a few seconds after it first enters the
world, and that it comes from the server, not from the client patch.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline -3 && git status --short --untracked-files=all
```

Expected: this task's two commits on top of Task 1's (the hashes differ), and a clean tree:
```
9136de4 docs(fork): list the Shrouded Isles start choice and tell players
7eaab6a feat(server): ask new characters the Shrouded Isles start question
b07fad3 feat(server): SiStartChoice decides the Shrouded Isles start question
```

---

### Task 3: Deploy plumbing for HEARTHDAOC_SI_START_CHOICE

**Files:**
- Modify: `deploy/bin/server_properties.py` (new `--si-start-choice on|off`, writes the `si_start_choice` property)
- Modify: `deploy/entrypoint.sh` (passes the setting, default `on`)
- Modify: `deploy/compose.yml` (passes the setting into the container, default `on`)
- Modify: `deploy/.env.example` (documents the setting, default `on`)
- Modify: `deploy/HANDOFF.md` (settings text in "6. Day-to-day" and "Upgrading to a new fork release")
- Test: `deploy/tests/test_server_properties.py` (extended)
- Test: `deploy/tests/test_si_start_choice.py` (new: entrypoint, compose and docs wiring; the server declaration's description; the world's three Teleport rows)

**Interfaces:**
- Consumes, from Task 2: `source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs` declares
  `[ServerProperty("server", "si_start_choice", "<description>", false)]` with the description as one string
  literal, character for character equal to `SI_DESCRIPTION` below. The new test
  `test_server_declares_the_property_with_the_same_description` checks this. Nothing else from Tasks 1 and 2 is used.
- Produces, in `deploy/bin/server_properties.py`:
  - `SI_KEY = "si_start_choice"`
  - `SI_DESCRIPTION = "Ask new level 1 characters of the classic races whether to start in their realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)"` (the same text as the server's attribute)
  - `SI_DEFAULT = "False"`: the code default (off), written as the server itself writes it in a new row's `DefaultValue`
  - `SI_VALUES = {"on": "True", "off": "False"}`
  - `si_start_value(spec) -> str`: `"on"`/`"off"` in any case become `"True"`/`"False"`. Anything else, including an empty value or one with spaces around it, raises `PropertyError("HEARTHDAOC_SI_START_CHOICE must be on or off, got '<spec>'")`.
  - `set_property(db, key, value, category="server", description=OVERRIDES_DESCRIPTION, default="")`: the new `default` keyword is the `DefaultValue` of a new row. An existing row only gets the new value, as before.
  - The command line is `server_properties.py --db DB --gm-only-commands SPEC --si-start-choice on|off`, and all three options are required:

    | Case | Output | Exit |
    |---|---|---|
    | both values valid | stdout `GM-only commands: <spec or none>` if `command_plvl_overrides` changed, then `Shrouded Isles start choice: on` or `off` if `si_start_choice` changed. Nothing is printed for a property that did not change. | 0 |
    | bad `--gm-only-commands` or `--si-start-choice` | stderr `ERROR: <message>`. Both values are checked first, so nothing is written. | 2 |
    | `--si-start-choice` missing | argparse usage error on stderr | 2 |
- Produces, in `deploy/entrypoint.sh`: `--si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}"`. In `deploy/compose.yml`: `HEARTHDAOC_SI_START_CHOICE: ${HEARTHDAOC_SI_START_CHOICE-on}`. In `deploy/.env.example`: `HEARTHDAOC_SI_START_CHOICE=on`. The entrypoint runs under `set -e`, so a bad value stops the container with exit 2, and `./hdc up` / `./hdc status` show its `ERROR:` line, as they already do for `HEARTHDAOC_GM_ONLY_COMMANDS`.
- Existing servers: `./hdc update` appends to `.env` every `KEY=` line of the new `.env.example` that `.env` lacks (`deploy/hdc`, the loop after `local line key added=""`). It then prints `New settings added to .env with their defaults: ... HEARTHDAOC_SI_START_CHOICE`, so an existing server gets `on`. CI's `deploy/tests/hdc_integration.sh` already checks that loop (`HEARTHDAOC_IT_NEW_SETTING`). Without the line, compose's own default is `on` as well.
- Produces, for later tests: `deploy/tests/test_server_properties.py` gains the helpers `row(db, key)` (returns `(Category, Description, DefaultValue, Value)` or `None`) and `run_cli(db, *args)`. `test_si_start_choice.py` imports `SCHEMA` and `row` from it as `tests.test_server_properties`, which works with `-t deploy`.

**Prerequisites:**
- Tasks 1 and 2 are done on branch `sub3-si-start-choice`. Run every command from the repository root.
- The C# build output and the copied build config are not tracked. Task 1 already excluded them; these lines do nothing when they are present:
  ```bash
  grep -qxF 'source/server/build/' .git/info/exclude || echo 'source/server/build/' >> .git/info/exclude
  grep -qxF 'source/server/CoreServer/config/serverconfig.xml' .git/info/exclude || echo 'source/server/CoreServer/config/serverconfig.xml' >> .git/info/exclude
  ```
- The real-world test needs the clean classic world (read-only): `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`. The commands below pass it as `HDC_TEST_WORLD`. Without it, those tests are skipped.
- This task only changes Python, shell, YAML and docs, so it does not run the C# tests.

- [ ] **Step 1: Write the failing tests for server_properties.py**

Replace the whole of `deploy/tests/test_server_properties.py` with:

```python
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "..", "bin")
sys.path.insert(0, BIN)

import server_properties as sp  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCHEMA = ("CREATE TABLE ServerProperty (Category TEXT NOT NULL DEFAULT '', `Key` VARCHAR(255) NOT NULL DEFAULT '', "
          "Description TEXT NOT NULL DEFAULT '', DefaultValue TEXT NOT NULL DEFAULT '', Value TEXT NOT NULL DEFAULT '', "
          "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', ServerProperty_ID VARCHAR(255), "
          "PRIMARY KEY (`Key`))")


def value(db, key):
    with sqlite3.connect(db) as c:
        row = c.execute("SELECT Value FROM ServerProperty WHERE `Key`=?", (key,)).fetchone()
    return row[0] if row else None


def row(db, key):
    """(Category, Description, DefaultValue, Value) of a property, or None."""
    with sqlite3.connect(db) as c:
        return c.execute("SELECT Category, Description, DefaultValue, Value FROM ServerProperty WHERE `Key`=?",
                         (key,)).fetchone()


def run_cli(db, *args):
    return subprocess.run([sys.executable, os.path.join(BIN, "server_properties.py"), "--db", db, *args],
                          capture_output=True, text=True)


class GmOnlyTests(unittest.TestCase):
    def test_commands_become_gm_level_overrides(self):
        self.assertEqual(sp.gm_only_overrides("/tele;/tc"), "/tele=2;/tc=2")
        self.assertEqual(sp.gm_only_overrides(" /tele ; /tc ;"), "/tele=2;/tc=2")

    def test_empty_means_no_overrides(self):
        self.assertEqual(sp.gm_only_overrides(""), "")
        self.assertEqual(sp.gm_only_overrides("none"), "")

    def test_bad_entries_are_refused(self):
        for bad in ("tele", "/tele=1", "/te le", "/tele;rm -rf"):
            with self.subTest(bad=bad), self.assertRaisesRegex(sp.PropertyError, "HEARTHDAOC_GM_ONLY_COMMANDS"):
                sp.gm_only_overrides(bad)


class SiStartChoiceTests(unittest.TestCase):
    def test_on_and_off_in_any_case_become_true_and_false(self):
        for spec, expected in (("on", "True"), ("ON", "True"), ("On", "True"),
                               ("off", "False"), ("OFF", "False"), ("oFf", "False")):
            with self.subTest(spec=spec):
                self.assertEqual(sp.si_start_value(spec), expected)

    def test_other_values_are_refused(self):
        for bad in ("", "yes", "no", "true", "false", "1", "0", "none", "onn", " on", "off "):
            with self.subTest(bad=bad), self.assertRaisesRegex(
                    sp.PropertyError, f"^HEARTHDAOC_SI_START_CHOICE must be on or off, got {bad!r}$"):
                sp.si_start_value(bad)

    def test_the_property_matches_the_server_declaration(self):
        # [ServerProperty("server", "si_start_choice", <description>, false)]: off in code, "False" in the table.
        self.assertEqual((sp.SI_KEY, sp.SI_DEFAULT), ("si_start_choice", "False"))
        self.assertTrue(sp.SI_DESCRIPTION)
        self.assertNotIn('"', sp.SI_DESCRIPTION)
        self.assertNotIn("\\", sp.SI_DESCRIPTION)


class UpsertTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        with sqlite3.connect(self.db) as c:
            c.execute(SCHEMA)

    def tearDown(self):
        self.tmp.cleanup()

    def test_new_property_is_created_then_updated(self):
        self.assertTrue(sp.set_property(self.db, sp.OVERRIDES_KEY, "/tele=2"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2")
        self.assertTrue(sp.set_property(self.db, sp.OVERRIDES_KEY, "/tele=2;/tc=2"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")

    def test_same_value_changes_nothing(self):
        sp.set_property(self.db, sp.OVERRIDES_KEY, "/tc=2")
        self.assertFalse(sp.set_property(self.db, sp.OVERRIDES_KEY, "/tc=2"))

    def test_a_new_row_gets_the_description_and_default_it_is_given(self):
        sp.set_property(self.db, sp.OVERRIDES_KEY, "/tc=2")
        self.assertEqual(row(self.db, sp.OVERRIDES_KEY), ("server", sp.OVERRIDES_DESCRIPTION, "", "/tc=2"))
        sp.set_property(self.db, sp.SI_KEY, "True", description=sp.SI_DESCRIPTION, default=sp.SI_DEFAULT)
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))

    def test_cli(self):
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        self.assertEqual(run_cli(self.db, "--gm-only-commands", "tele", "--si-start-choice", "on").returncode, 2)

    def test_cli_writes_the_si_start_choice_as_true_or_false(self):
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "ON")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "GM-only commands: none\nShrouded Isles start choice: on\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "Off")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Shrouded Isles start choice: off\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "False"))

    def test_cli_prints_nothing_when_nothing_changes(self):
        run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "off")
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "OFF")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "", ""))

    def test_cli_refuses_a_bad_si_start_choice_and_writes_nothing(self):
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "yes")
        self.assertEqual((r.returncode, r.stdout), (2, ""))
        self.assertEqual(r.stderr, "ERROR: HEARTHDAOC_SI_START_CHOICE must be on or off, got 'yes'\n")
        self.assertIsNone(value(self.db, sp.OVERRIDES_KEY))
        self.assertIsNone(value(self.db, sp.SI_KEY))

    def test_cli_needs_the_si_start_choice(self):
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc")
        self.assertEqual(r.returncode, 2)
        self.assertIn("the following arguments are required: --si-start-choice", r.stderr)

    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertIsNone(value(self.db, sp.OVERRIDES_KEY))
        self.assertIsNone(value(self.db, sp.SI_KEY))
        sp.set_property(self.db, sp.OVERRIDES_KEY, sp.gm_only_overrides("/tele;/tc"))
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on")
        self.assertEqual((r.returncode, r.stdout), (0, "Shrouded Isles start choice: on\n"), r.stderr)
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_server_properties.py -v`

Expected: 5 of the 7 existing tests still pass. `test_cli` and `test_shipped_world` now fail. The new tests fail because `si_start_value`, `SI_KEY` and `--si-start-choice` don't exist yet. Timing varies:
```
test_bad_entries_are_refused (tests.test_server_properties.GmOnlyTests.test_bad_entries_are_refused) ... ok
test_commands_become_gm_level_overrides (tests.test_server_properties.GmOnlyTests.test_commands_become_gm_level_overrides) ... ok
test_empty_means_no_overrides (tests.test_server_properties.GmOnlyTests.test_empty_means_no_overrides) ... ok
test_on_and_off_in_any_case_become_true_and_false (tests.test_server_properties.SiStartChoiceTests.test_on_and_off_in_any_case_become_true_and_false) ...
  test_on_and_off_in_any_case_become_true_and_false (tests.test_server_properties.SiStartChoiceTests.test_on_and_off_in_any_case_become_true_and_false) (spec='on') ... ERROR
...
test_the_property_matches_the_server_declaration (tests.test_server_properties.SiStartChoiceTests.test_the_property_matches_the_server_declaration) ... ERROR
test_a_new_row_gets_the_description_and_default_it_is_given (tests.test_server_properties.UpsertTests.test_a_new_row_gets_the_description_and_default_it_is_given) ... ERROR
test_cli (tests.test_server_properties.UpsertTests.test_cli) ... FAIL
test_cli_needs_the_si_start_choice (tests.test_server_properties.UpsertTests.test_cli_needs_the_si_start_choice) ... FAIL
test_cli_prints_nothing_when_nothing_changes (tests.test_server_properties.UpsertTests.test_cli_prints_nothing_when_nothing_changes) ... FAIL
test_cli_refuses_a_bad_si_start_choice_and_writes_nothing (tests.test_server_properties.UpsertTests.test_cli_refuses_a_bad_si_start_choice_and_writes_nothing) ... FAIL
test_cli_writes_the_si_start_choice_as_true_or_false (tests.test_server_properties.UpsertTests.test_cli_writes_the_si_start_choice_as_true_or_false) ... FAIL
test_new_property_is_created_then_updated (tests.test_server_properties.UpsertTests.test_new_property_is_created_then_updated) ... ok
test_same_value_changes_nothing (tests.test_server_properties.UpsertTests.test_same_value_changes_nothing) ... ok
test_shipped_world (tests.test_server_properties.UpsertTests.test_shipped_world) ... ERROR
...
AttributeError: module 'server_properties' has no attribute 'si_start_value'
...
AttributeError: module 'server_properties' has no attribute 'SI_KEY'
...
AssertionError: 2 != 0 : usage: server_properties.py [-h] --db DB --gm-only-commands GM_ONLY_COMMANDS
server_properties.py: error: unrecognized arguments: --si-start-choice on
...
Ran 15 tests in 0.619s

FAILED (failures=5, errors=20)
```
The 20 errors are the 17 subtests of `test_on_and_off_in_any_case_become_true_and_false` and `test_other_values_are_refused` (no `si_start_value`), plus three tests that use `SI_KEY`. The 5 failures are the command-line tests: argparse rejects the unknown `--si-start-choice` with exit 2, and `test_cli_needs_the_si_start_choice` gets exit 0 where it expects 2.

- [ ] **Step 3: Implement the setting in server_properties.py**

Replace the whole of `deploy/bin/server_properties.py` with the content below. The file stays executable (mode 100755).

```python
#!/usr/bin/env python3
"""Write HearthDAoC's settings into the world's ServerProperty table before the server starts.

HEARTHDAOC_GM_ONLY_COMMANDS ("/tele;/tc") becomes the command_plvl_overrides property
("/tele=2;/tc=2"), which the server applies when it loads commands: the listed single-player
shortcuts then need GM rights.
HEARTHDAOC_SI_START_CHOICE (on or off, case ignored) becomes the si_start_choice property (True or
False): a new level-1 character of a classic race is then asked once whether to begin in its realm's
Shrouded Isles town.
Only writes a property when its value differs, and prints a line for each one it changed.
"""
import argparse
import datetime
import re
import sqlite3
import sys
import uuid

OVERRIDES_KEY = "command_plvl_overrides"
OVERRIDES_DESCRIPTION = ("Privilege level a command needs, overriding its default, separated by semi-colon, "
                         "example /tele=2;/tc=2 (1 player, 2 GM, 3 admin)")
GM = 2
COMMAND = re.compile(r"^/[A-Za-z0-9_]+$")

# Must match the server's [ServerProperty("server", "si_start_choice", SI_DESCRIPTION, false)] in
# source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs (deploy/tests/test_si_start_choice.py checks).
SI_KEY = "si_start_choice"
SI_DESCRIPTION = ("Ask new level 1 characters of the classic races whether to start in their realm's "
                  "Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)")
SI_DEFAULT = "False"  # the code default (off), as the server itself writes it
SI_VALUES = {"on": "True", "off": "False"}


class PropertyError(Exception):
    pass


def gm_only_overrides(spec):
    """'/tele;/tc' -> '/tele=2;/tc=2'; '' or 'none' -> ''."""
    if spec.strip().lower() in ("", "none"):
        return ""
    commands = [c.strip() for c in spec.split(";") if c.strip()]
    bad = [c for c in commands if not COMMAND.match(c)]
    if bad:
        raise PropertyError(f"HEARTHDAOC_GM_ONLY_COMMANDS must be commands like /tele;/tc (or none), got {bad[0]!r}")
    return ";".join(f"{c.lower()}={GM}" for c in commands)


def si_start_value(spec):
    """'on' -> 'True', 'off' -> 'False', in any case; anything else is refused."""
    try:
        return SI_VALUES[spec.lower()]
    except KeyError:
        raise PropertyError(f"HEARTHDAOC_SI_START_CHOICE must be on or off, got {spec!r}") from None


def set_property(db, key, value, category="server", description=OVERRIDES_DESCRIPTION, default=""):
    """Create or update a server property. Returns True when something changed.

    A new row gets the category, description and default value given here, as the server would
    create it; an existing row only gets the new value."""
    conn = sqlite3.connect(db, timeout=30)
    try:
        row = conn.execute("SELECT Value FROM ServerProperty WHERE `Key`=?", (key,)).fetchone()
        if row is not None and row[0] == value:
            return False
        now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        with conn:
            if row is None:
                conn.execute("INSERT INTO ServerProperty (Category, `Key`, Description, DefaultValue, Value, "
                             "LastTimeRowUpdated, ServerProperty_ID) VALUES (?, ?, ?, ?, ?, ?, ?)",
                             (category, key, description, default, value, now, str(uuid.uuid4())))
            else:
                conn.execute("UPDATE ServerProperty SET Value=?, LastTimeRowUpdated=? WHERE `Key`=?", (value, now, key))
        return True
    finally:
        conn.close()


def main(argv=None):
    ap = argparse.ArgumentParser(description="Apply HearthDAoC settings to the world's server properties.")
    ap.add_argument("--db", required=True)
    ap.add_argument("--gm-only-commands", required=True, help='e.g. "/tele;/tc", or "none"')
    ap.add_argument("--si-start-choice", required=True, help="on or off (case ignored)")
    a = ap.parse_args(argv)
    try:  # check every setting before writing any
        overrides = gm_only_overrides(a.gm_only_commands)
        si_start = si_start_value(a.si_start_choice)
    except PropertyError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2
    if set_property(a.db, OVERRIDES_KEY, overrides):
        print(f"GM-only commands: {a.gm_only_commands.strip() or 'none'}")
    if set_property(a.db, SI_KEY, si_start, description=SI_DESCRIPTION, default=SI_DEFAULT):
        print(f"Shrouded Isles start choice: {a.si_start_choice.lower()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_server_properties.py -v`

Expected (timing varies):
```
test_bad_entries_are_refused (tests.test_server_properties.GmOnlyTests.test_bad_entries_are_refused) ... ok
test_commands_become_gm_level_overrides (tests.test_server_properties.GmOnlyTests.test_commands_become_gm_level_overrides) ... ok
test_empty_means_no_overrides (tests.test_server_properties.GmOnlyTests.test_empty_means_no_overrides) ... ok
test_on_and_off_in_any_case_become_true_and_false (tests.test_server_properties.SiStartChoiceTests.test_on_and_off_in_any_case_become_true_and_false) ... ok
test_other_values_are_refused (tests.test_server_properties.SiStartChoiceTests.test_other_values_are_refused) ... ok
test_the_property_matches_the_server_declaration (tests.test_server_properties.SiStartChoiceTests.test_the_property_matches_the_server_declaration) ... ok
test_a_new_row_gets_the_description_and_default_it_is_given (tests.test_server_properties.UpsertTests.test_a_new_row_gets_the_description_and_default_it_is_given) ... ok
test_cli (tests.test_server_properties.UpsertTests.test_cli) ... ok
test_cli_needs_the_si_start_choice (tests.test_server_properties.UpsertTests.test_cli_needs_the_si_start_choice) ... ok
test_cli_prints_nothing_when_nothing_changes (tests.test_server_properties.UpsertTests.test_cli_prints_nothing_when_nothing_changes) ... ok
test_cli_refuses_a_bad_si_start_choice_and_writes_nothing (tests.test_server_properties.UpsertTests.test_cli_refuses_a_bad_si_start_choice_and_writes_nothing) ... ok
test_cli_writes_the_si_start_choice_as_true_or_false (tests.test_server_properties.UpsertTests.test_cli_writes_the_si_start_choice_as_true_or_false) ... ok
test_new_property_is_created_then_updated (tests.test_server_properties.UpsertTests.test_new_property_is_created_then_updated) ... ok
test_same_value_changes_nothing (tests.test_server_properties.UpsertTests.test_same_value_changes_nothing) ... ok
test_shipped_world (tests.test_server_properties.UpsertTests.test_shipped_world) ... ok

----------------------------------------------------------------------
Ran 15 tests in 0.762s

OK
```

- [ ] **Step 5: Write the failing wiring and world-data tests**

Create `deploy/tests/test_si_start_choice.py`:

```python
"""HEARTHDAOC_SI_START_CHOICE from .env to the server, and the world rows the server's start choice reads.

The setting goes .env -> compose.yml -> entrypoint.sh -> server_properties.py -> the si_start_choice
server property, default on at every step. hdc update appends every KEY= line of .env.example that an
existing .env lacks, so servers set up before the setting existed get it on too.
"""
import os
import pathlib
import re
import shlex
import sqlite3
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

import server_properties as sp  # noqa: E402
from tests.test_server_properties import SCHEMA, row  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SCRIPT_CS = os.path.join(ROOT, "source", "server", "GameServer", "scripts", "hearthdaoc", "SiStartChoiceScript.cs")


def read(name):
    with open(os.path.join(DEPLOY, name), encoding="utf-8") as f:
        return f.read()


class EntrypointTests(unittest.TestCase):
    """Runs the entrypoint's own server_properties.py command against a scratch world."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        os.makedirs(os.path.join(self.tmp.name, "world"))
        self.db = os.path.join(self.tmp.name, "world", "opendaoc.sqlite3.db")
        with sqlite3.connect(self.db) as c:
            c.execute(SCHEMA)
        lines = read("entrypoint.sh").replace("\\\n", " ").splitlines()
        commands = [line for line in lines if line.startswith('python3 "$BIN/server_properties.py"')]
        self.assertEqual(len(commands), 1, "the entrypoint runs server_properties.py once")
        self.command = commands[0]

    def tearDown(self):
        self.tmp.cleanup()

    def run_entrypoint_line(self, **env):
        script = f"set -euo pipefail\nBIN={shlex.quote(BIN)}\nDATA={shlex.quote(self.tmp.name)}\n{self.command}\n"
        base = {k: v for k, v in os.environ.items() if not k.startswith("HEARTHDAOC_")}
        return subprocess.run(["bash", "-c", script], env={**base, **env}, capture_output=True, text=True)

    def test_passes_the_setting_with_default_on(self):
        self.assertIn('--si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}"', self.command)
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))

    def test_off_in_any_case_turns_it_off(self):
        self.run_entrypoint_line()
        r = self.run_entrypoint_line(HEARTHDAOC_SI_START_CHOICE="OFF")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Shrouded Isles start choice: off\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY)[3], "False")

    def test_a_bad_value_stops_the_start(self):
        for bad in ("maybe", ""):
            with self.subTest(bad=bad):
                r = self.run_entrypoint_line(HEARTHDAOC_SI_START_CHOICE=bad)
                self.assertEqual(r.returncode, 2)
                self.assertEqual(r.stderr, f"ERROR: HEARTHDAOC_SI_START_CHOICE must be on or off, got {bad!r}\n")
                self.assertIsNone(row(self.db, sp.SI_KEY))


class SettingDocsTests(unittest.TestCase):
    def test_compose_passes_the_setting_with_default_on(self):
        environment = read("compose.yml").split("\n    environment:\n", 1)[1].split("\n    volumes:\n", 1)[0]
        self.assertIn("\n      HEARTHDAOC_SI_START_CHOICE: ${HEARTHDAOC_SI_START_CHOICE-on}\n", environment + "\n")

    def test_env_example_documents_the_setting_default_on(self):
        lines = read(".env.example").splitlines()
        self.assertEqual([line for line in lines if line.startswith("HEARTHDAOC_SI_START_CHOICE")],
                         ["HEARTHDAOC_SI_START_CHOICE=on"])
        i = lines.index("HEARTHDAOC_SI_START_CHOICE=on")
        comment = []
        while i > 0 and lines[i - 1].startswith("#"):
            i -= 1
            comment.insert(0, lines[i])
        self.assertIn("Shrouded Isles", " ".join(comment))
        self.assertIn("off", " ".join(comment))

    def test_handoff_lists_the_setting(self):
        handoff = read("HANDOFF.md")
        self.assertIn("`HEARTHDAOC_SI_START_CHOICE` (default `on`", handoff)
        self.assertIn("`HEARTHDAOC_SI_START_CHOICE=on`", handoff)  # what ./hdc update adds to an existing .env


class ServerDeclarationTests(unittest.TestCase):
    @unittest.skipUnless(os.path.exists(SCRIPT_CS), "needs source/server/GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs")
    def test_server_declares_the_property_with_the_same_description(self):
        with open(SCRIPT_CS, encoding="utf-8") as f:
            found = re.findall(r'\[ServerProperty\(\s*"([^"]*)",\s*"si_start_choice",\s*"([^"]*)",\s*(\w+)\s*\)\]', f.read())
        self.assertEqual(found, [("server", sp.SI_DESCRIPTION, "false")])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class ArrivalRowTests(unittest.TestCase):
    """The server looks these up with WorldMgr.GetTeleportLocation(realm, ":" + TeleportID): Type is
    empty and the key is case-sensitive. The first row per key wins, so there must be exactly one."""

    EXPECTED = [  # TeleportID, Realm, RegionID, X, Y, Z, Heading, Type
        ("Caer Gothwaite", 1, 51, 535518, 547214, 4800, 2105, ""),
        ("Aegirhamn", 2, 151, 293910, 356255, 3488, 1199, ""),
        ("Grove of Domnann", 3, 181, 423187, 440300, 5952, 3866, ""),
    ]

    def test_each_realm_has_exactly_one_arrival_row(self):
        conn = sqlite3.connect(pathlib.Path(TEST_WORLD).resolve().as_uri() + "?mode=ro", uri=True)
        try:
            for expected in self.EXPECTED:
                # TeleportID is COLLATE NOCASE, so this also finds rows that differ only in case, in any realm.
                found = conn.execute("SELECT TeleportID, Realm, RegionID, X, Y, Z, Heading, Type FROM Teleport "
                                     "WHERE TeleportID = ?", (expected[0],)).fetchall()
                with self.subTest(teleport=expected[0]):
                    self.assertEqual(found, [expected])
        finally:
            conn.close()


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_si_start_choice.py -v`

Expected: the world-data test already passes, because it pins rows that exist. The entrypoint tests fail because the entrypoint doesn't pass `--si-start-choice` yet, which Step 3 made required. The compose, `.env.example` and HANDOFF tests fail because the setting isn't there yet. Timing varies:
```
test_each_realm_has_exactly_one_arrival_row (tests.test_si_start_choice.ArrivalRowTests.test_each_realm_has_exactly_one_arrival_row) ... ok
test_a_bad_value_stops_the_start (tests.test_si_start_choice.EntrypointTests.test_a_bad_value_stops_the_start) ...
  test_a_bad_value_stops_the_start (tests.test_si_start_choice.EntrypointTests.test_a_bad_value_stops_the_start) (bad='maybe') ... FAIL
  test_a_bad_value_stops_the_start (tests.test_si_start_choice.EntrypointTests.test_a_bad_value_stops_the_start) (bad='') ... FAIL
test_off_in_any_case_turns_it_off (tests.test_si_start_choice.EntrypointTests.test_off_in_any_case_turns_it_off) ... FAIL
test_passes_the_setting_with_default_on (tests.test_si_start_choice.EntrypointTests.test_passes_the_setting_with_default_on) ... FAIL
test_server_declares_the_property_with_the_same_description (tests.test_si_start_choice.ServerDeclarationTests.test_server_declares_the_property_with_the_same_description) ... ok
test_compose_passes_the_setting_with_default_on (tests.test_si_start_choice.SettingDocsTests.test_compose_passes_the_setting_with_default_on) ... FAIL
test_env_example_documents_the_setting_default_on (tests.test_si_start_choice.SettingDocsTests.test_env_example_documents_the_setting_default_on) ... FAIL
test_handoff_lists_the_setting (tests.test_si_start_choice.SettingDocsTests.test_handoff_lists_the_setting) ... FAIL
...
AssertionError: '--si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}"' not found in 'python3 "$BIN/server_properties.py" --db "$DATA/world/opendaoc.sqlite3.db" --gm-only-commands "${HEARTHDAOC_GM_ONLY_COMMANDS-/tele;/tc}"'
...
- server_properties.py: error: the following arguments are required: --si-start-choice
...
AssertionError: Lists differ: [] != ['HEARTHDAOC_SI_START_CHOICE=on']
...
Ran 8 tests in 0.157s

FAILED (failures=7)
```
The declaration test must show `... ok`: it checks that Task 2's `[ServerProperty]` uses exactly `SI_DESCRIPTION`. If it fails, make Task 2's attribute description exactly the `SI_DESCRIPTION` text before going on.

- [ ] **Step 7: Pass the setting through the entrypoint and compose, and document it**

In `deploy/entrypoint.sh`, replace these two lines:

```bash
# Single-player shortcuts that need GM rights on a shared server (command_plvl_overrides).
python3 "$BIN/server_properties.py" --db "$DATA/world/opendaoc.sqlite3.db" --gm-only-commands "${HEARTHDAOC_GM_ONLY_COMMANDS-/tele;/tc}"
```

with:

```bash
# Single-player shortcuts that need GM rights on a shared server (command_plvl_overrides), and the
# Shrouded Isles start choice for new characters (si_start_choice). A bad value stops the start (exit 2).
python3 "$BIN/server_properties.py" --db "$DATA/world/opendaoc.sqlite3.db" --gm-only-commands "${HEARTHDAOC_GM_ONLY_COMMANDS-/tele;/tc}" \
    --si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}"
```

In `deploy/compose.yml`, under `environment:`, add the new line right after the `HEARTHDAOC_GM_ONLY_COMMANDS` line:

```yaml
      HEARTHDAOC_GM_ONLY_COMMANDS: ${HEARTHDAOC_GM_ONLY_COMMANDS-/tele;/tc}
      HEARTHDAOC_SI_START_CHOICE: ${HEARTHDAOC_SI_START_CHOICE-on}
```

In `deploy/.env.example`, add these four lines right after the line `HEARTHDAOC_GM_ONLY_COMMANDS="/tele;/tc"`:

```bash
# on = a new level-1 character of a classic race is asked once, a few seconds after it first enters the
# world, whether to begin in its realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn or the Grove of
# Domnann) or stay in its home village. off = nobody is asked. Case is ignored.
HEARTHDAOC_SI_START_CHOICE=on
```

In `deploy/HANDOFF.md`, section "6. Day-to-day", replace:

```markdown
Settings live in `.env` (see `.env.example`), e.g. `HEARTHDAOC_AUTOSAVE_MINUTES` (default 5) and
`HEARTHDAOC_GM_ONLY_COMMANDS` (default `/tele;/tc`: single-player teleports need GM rights); after
editing `.env`, `./hdc up` recreates the server with them.
```

with:

```markdown
Settings live in `.env` (see `.env.example`), e.g. `HEARTHDAOC_AUTOSAVE_MINUTES` (default 5),
`HEARTHDAOC_GM_ONLY_COMMANDS` (default `/tele;/tc`: single-player teleports need GM rights) and
`HEARTHDAOC_SI_START_CHOICE` (default `on`: a new level-1 character of a classic race is asked once
whether to begin in its realm's Shrouded Isles town; `off` turns the question off); after
editing `.env`, `./hdc up` recreates the server with them.
```

In `deploy/HANDOFF.md`, section "Upgrading to a new fork release", replace:

```markdown
`./hdc update <tag>` installs a specific release. New settings are added to `.env` with their defaults
(it lists them). If the release is for another upstream version, it stops before starting: then run
`./hdc upgrade-world` (it backs up, moves all progress into the new clean world, keeps bans and
permissions, and lists server settings to re-check in its report), then `./hdc up`.
```

with:

```markdown
`./hdc update <tag>` installs a specific release. New settings are added to `.env` with their defaults
(it lists them; for example, the release with the Shrouded Isles start choice adds
`HEARTHDAOC_SI_START_CHOICE=on`). If the release is for another upstream version, it stops before
starting: then run `./hdc upgrade-world` (it backs up, moves all progress into the new clean world,
keeps bans and permissions, and lists server settings to re-check in its report), then `./hdc up`.
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_si_start_choice.py -v`

Expected (timing varies):
```
test_each_realm_has_exactly_one_arrival_row (tests.test_si_start_choice.ArrivalRowTests.test_each_realm_has_exactly_one_arrival_row) ... ok
test_a_bad_value_stops_the_start (tests.test_si_start_choice.EntrypointTests.test_a_bad_value_stops_the_start) ... ok
test_off_in_any_case_turns_it_off (tests.test_si_start_choice.EntrypointTests.test_off_in_any_case_turns_it_off) ... ok
test_passes_the_setting_with_default_on (tests.test_si_start_choice.EntrypointTests.test_passes_the_setting_with_default_on) ... ok
test_server_declares_the_property_with_the_same_description (tests.test_si_start_choice.ServerDeclarationTests.test_server_declares_the_property_with_the_same_description) ... ok
test_compose_passes_the_setting_with_default_on (tests.test_si_start_choice.SettingDocsTests.test_compose_passes_the_setting_with_default_on) ... ok
test_env_example_documents_the_setting_default_on (tests.test_si_start_choice.SettingDocsTests.test_env_example_documents_the_setting_default_on) ... ok
test_handoff_lists_the_setting (tests.test_si_start_choice.SettingDocsTests.test_handoff_lists_the_setting) ... ok

----------------------------------------------------------------------
Ran 8 tests in 0.219s

OK
```
The declaration test must show `... ok`, not skipped.

- [ ] **Step 9: Run the whole deploy suite and check the tree**

Run: `bash -n deploy/entrypoint.sh && echo "entrypoint syntax ok"`

Expected: `entrypoint syntax ok`

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy`

Expected (takes about 30 s):
```
(one progress line of dots, with an s for each skip)
----------------------------------------------------------------------
Ran 137 tests in 27.145s

OK (skipped=5)
```
The five skips are the PowerShell bundle test (no `pwsh`) and the four `test_world_admin` upgrade tests (they also need `HDC_TOOLS`). No test may fail.

Run: `git status --short --untracked-files=all`

Expected:
```
 M deploy/.env.example
 M deploy/HANDOFF.md
 M deploy/bin/server_properties.py
 M deploy/compose.yml
 M deploy/entrypoint.sh
 M deploy/tests/test_server_properties.py
?? deploy/tests/test_si_start_choice.py
```

- [ ] **Step 10: Commit**

```bash
git add deploy/bin/server_properties.py deploy/entrypoint.sh deploy/compose.yml deploy/.env.example deploy/HANDOFF.md deploy/tests/test_server_properties.py deploy/tests/test_si_start_choice.py
git commit -q -m "feat(deploy): HEARTHDAOC_SI_START_CHOICE sets the Shrouded Isles start choice

server_properties.py takes --si-start-choice on|off (case ignored) and
writes the si_start_choice server property as exactly True or False,
with the server's own description and default; any other value stops
the start with exit 2 before anything is written. The entrypoint and
compose pass the setting with default on, .env.example and HANDOFF
document it, and hdc update adds it to an existing .env as on.

Tests run the entrypoint's own command against a scratch world, check
that the server script declares the same description, and pin the three
Teleport rows the server reads in a real world (HDC_TEST_WORLD).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git show --stat --format=%s HEAD
```

Expected:
```
feat(deploy): HEARTHDAOC_SI_START_CHOICE sets the Shrouded Isles start choice

 deploy/.env.example                    |   4 +
 deploy/HANDOFF.md                      |  13 ++--
 deploy/bin/server_properties.py        |  39 ++++++++--
 deploy/compose.yml                     |   1 +
 deploy/entrypoint.sh                   |   6 +-
 deploy/tests/test_server_properties.py |  74 +++++++++++++++++-
 deploy/tests/test_si_start_choice.py   | 135 +++++++++++++++++++++++++++++++++
 7 files changed, 255 insertions(+), 17 deletions(-)
```

---

### Task 4: In-game verification (owner) and record

**Files:**
- Create: `docs/fork/verification/sub3-ingame.md`

**Interfaces:**
- Consumes: a server running this branch's image. For the owner, that means the release from merging the PR, then `./hdc update`. Before merging, a locally built image or a test server can be used instead. With `HEARTHDAOC_SI_START_CHOICE` unset, the server has the default `on`.
- Produces: the verification record that closes issue #40.

The owner does this task in the real client. The agent writes the record from the owner's reports.

- [ ] **Step 1: Check that the server loaded the destinations**

Run (on the server): `docker logs hearthdaoc-server 2>&1 | grep -i 'shrouded isles start'`
Expected: no warning about a missing destination. Any warning names the realm whose Teleport row was not found. In that case, check the world's Teleport rows before continuing.

- [ ] **Step 2: Ask the owner to run the in-game checks** (spec section 4). Use a normal player account. Report each check as pass or fail, with what was seen:

1. **Accept:** a new Briton gets the question a few seconds after entering, accepts, arrives in Caer Gothwaite, and reads "Welcome to Caer Gothwaite, in the Shrouded Isles. You are bound here." After dying, or using `/release`, it returns there.
2. **Decline:** a new Troll declines and stays home. It is not asked at its next login.
3. **SI race:** a new Inconnu is never asked.
4. **Logout:** a new character logs out with the question open. It is asked again at its next login.
5. **Button labels:** note the labels on the two buttons.
6. **Other windows:** check that the question is visible next to the patch-notes and starter-help windows. Note what pressing Escape does.
7. **Setting off:** with `HEARTHDAOC_SI_START_CHOICE=off` in `.env` and a restart (`./hdc up`), a new character is not asked. Afterwards, set it back to `on` and restart again.

- [ ] **Step 3: Record the results**

Create `docs/fork/verification/sub3-ingame.md` with one row per check (check, expected, actual, pass/fail), plus the server release used. A failure goes back to the task that owns it before the PR is merged.

- [ ] **Step 4: Commit**

```bash
git add docs/fork/verification/sub3-ingame.md
git commit -q -m "docs(fork): in-game verification of the Shrouded Isles start choice

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
