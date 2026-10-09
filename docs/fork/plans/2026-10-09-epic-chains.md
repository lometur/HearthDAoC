# Epic chains on upstream's quests: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make upstream's Guild of Shadows epic chain playable 7→50 in order with every reward, add the real level-50
"Lord of Deceit", pin every guild line's steps in order, pay every classic quest's final XP and coin, and give GMs an
`/epic` test command.

**Architecture:** Small changes to upstream's quest engine (`DataQuest.cs`: final rewards, dependency entries by quest
ID; `ClassicQuests.cs`: a second quest data file) plus a fork GM command (`scripts/hearthdaoc/`), and one world fix
(`deploy/bin/epic_chains.py` with its data file) run at server start like `battlegrounds.py`. Upstream's quests, texts,
NPCs and givers stay.

**Tech Stack:** C# (.NET 10, NUnit) for the server, Python 3 (`sqlite3`, `unittest`) for the world fix and tests,
bash for the container tests.

**Spec:** `docs/fork/specs/2026-10-09-epic-chains-design.md` (approved by the owner 2026-10-09; read it with this plan).

## Global Constraints

- Repository `~/github/HearthDAoC`, branch `sub4-shadows-epic`. Never commit to `main`; never push to or open anything
  on `shadowofze/OfflineDAoC` or OpenDAoC. The PR is opened at the end with
  `gh api -X POST repos/lometur/HearthDAoC/pulls ...` (not `gh pr create`).
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Upstream C# files keep their line endings and BOM exactly: `quests/QuestsMgr/DataQuest.cs` is CRLF with a BOM;
  `quests/QuestsMgr/ClassicQuests.cs` is LF without a BOM; `scripts/quests/Albion/epic/Academy50.cs`,
  `scripts/quests/Hibernia/epic/Essence50.cs`, `scripts/quests/Midgard/epic/Mystic50.cs` and
  `scripts/quests/Midgard/epic/Viking50.cs` are CRLF without a BOM. Edit them with the Python snippets given (they
  read and write with `newline=""` and the right encoding), not with an editor that may convert endings. Every fork
  change in an upstream file carries a `// HearthDAoC:` comment.
- Fork C# code lives in `source/server/GameServer/scripts/hearthdaoc/`, namespace `DOL.GS.HearthDAoC`, LF line
  endings, as a pure decisions class (no `GameServer`, `WorldMgr`, `GamePlayer`) plus a script that wires it.
- Each new C# test class is `public sealed class UT_<Name>` on its own line (`deploy/tests/test_workflows.py` checks
  `public sealed class UT_<Name>\n`) and is appended to the CI filter in `.github/workflows/server-image.yml` and to
  `SERVER_UNIT_TESTS` and the expected list in `deploy/tests/test_workflows.py`, in the same commit.
- World fix: marker `epic-chains-v1` in `fork_world_fixes`; savepoint `epic_chains`; each change only while the row
  still holds upstream's 0.35b value; inserts only where the key is missing.
- New level-50 quest IDs: Infiltrator 990509, Mercenary 990511, Cabalist 990513, Necromancer 990512, Reaver 990519.
- Class IDs: Infiltrator 9, Mercenary 11, Cabalist 13, Necromancer 12, Reaver 19. Column order everywhere:
  Infiltrator, Mercenary, Cabalist, Necromancer, Reaver.
- No new spells. New item rows use upstream's `Id_nb`, `PackageID` "HearthDAoC".
- Test commands (run from the repository root):
  - Python: `python3 -m unittest discover -s deploy/tests -t deploy` with
    `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db` (read-only file: tests copy it).
  - C#: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "<the
    CI filter>"`. The build needs `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`
    once if that file is missing (CI does this).
- Docs: plain, short sentences; the owner reads them.

## Review Focus

1. **A character halfway through a closed Supply Run** (Lady Aelawen's version) must still be able to finish it: the
   world fix closes it with a dependency on itself, and `DataQuest` reads dependencies only when it offers a quest
   (`MaxLevel` would not do: the item hand-in checks it too). Task 5 adds a source check that pins where
   `DataQuest.cs` uses `m_questDependencies`.
2. **An upstream world that already fixed a row** (a later sync): the row is left as upstream has it, and the summary
   counts only what the fix changed. Task 5 tests a GoS row whose dependency was changed beforehand.
3. **Item rows that already exist** (upstream added one, or the owner edited it) are not overwritten. Task 6 tests a
   pre-existing reward row and a vest whose charges the owner set.
4. **Lord Elidyn already back in `Mob`** (a GM restored him, or an earlier partial run) is not duplicated. Task 7
   tests a camp row present before the fix.
5. **`/epic` on a character whose class has no linked chain** (any class outside the epic lines, or a non-player
   target): a clear message, nothing changed. Task 8 tests `ChainFor` returning nothing for such a class.

## Files

| File | What | Task |
|---|---|---|
| `source/server/GameServer/quests/QuestsMgr/DataQuest.cs` | final rewards from the finishing stage (`FinishRewardIndex`); dependency entries checked by `QuestDependencies` | 1, 2 |
| `source/server/GameServer/quests/QuestsMgr/QuestDependencies.cs` (new) | the dependency entry forms, pure | 2 |
| `source/server/GameServer/quests/QuestsMgr/ClassicQuests.cs` | reads `hearthdaoc-quests.json` too (`Merge`), `MarkerFor` | 3 |
| `deploy/hearthdaoc-quests.json` (new), `deploy/Dockerfile` | the fork's quest data, copied next to the server | 3 |
| `source/server/GameServer/scripts/quests/Albion/epic/Shadows50.cs` | deleted | 4 |
| the four level-50 quest files | one lookup line each | 4 |
| `deploy/bin/epic_chains.py`, `deploy/bin/epic_chains_data.json` (new) | the world fix and its data | 5, 6, 7 |
| `deploy/bin/world_fixes.py` | calls `epic_chains.apply` | 7 |
| `source/server/GameServer/scripts/hearthdaoc/EpicChain.cs`, `EpicCommand.cs` (new) | `/epic` | 8 |
| `source/server/Tests/UnitTests/UT_DataQuestFinishRewards.cs`, `UT_DataQuestDependency.cs`, `UT_ClassicQuestsExtra.cs`, `UT_EpicChain.cs` (new) | C# tests | 1, 2, 3, 8 |
| `deploy/tests/test_epic_chains.py` (new), `deploy/tests/test_world_fixes.py`, `deploy/tests/test_workflows.py`, `deploy/tests/smoke.sh` | Python and container tests | 3–8 |
| `.github/workflows/server-image.yml` | the C# test filter | 1, 2, 3, 8 |
| `docs/fork/FORK.md`, `docs/fork/verification/sub4-test-guide.md` (new) | docs | 9 |

---

### Task 1: Quests pay their final XP and coin

> **Removed after the final review** (commit 5e64422, owner's decision 2026-10-09): `FinishQuest` already pays a
> standard quest's finishing stage; this task changed only the reward-quest branch, which no quest uses.

`DataQuest.FinishQuest` pays the first entry of `RewardXP`, `RewardRP`, `RewardCLXP`, `RewardBP` and `RewardMoney`;
upstream's classic quests keep the final reward in the last entry, so it is never paid (spec 1, 3.1 "Final rewards").

**Files:**
- Modify: `source/server/GameServer/quests/QuestsMgr/DataQuest.cs` (add `FinishRewardIndex` before `FinishQuest(GameObject obj, bool checkCustomStep)`; five reads inside that method)
- Create: `source/server/Tests/UnitTests/UT_DataQuestFinishRewards.cs`
- Modify: `.github/workflows/server-image.yml:113`, `deploy/tests/test_workflows.py` (`SERVER_UNIT_TESTS`, `test_every_name_in_the_filter_is_a_test_class`)

**Interfaces:**
- Produces: `public static int DataQuest.FinishRewardIndex(int step, int entries)` (namespace `DOL.GS.Quests`).

- [ ] **Step 1: Write the failing test**

Create `source/server/Tests/UnitTests/UT_DataQuestFinishRewards.cs` (LF):

```csharp
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
```

Append the class to the CI filter. In `.github/workflows/server-image.yml`, the line

```
          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds"
```

becomes

```
          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds|FullyQualifiedName~UT_DataQuestFinishRewards"
```

In `deploy/tests/test_workflows.py`:

```python
SERVER_UNIT_TESTS = ("dotnet test source/server/Tests/Tests.csproj --nologo --filter "
                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice'
                     '|FullyQualifiedName~UT_ClassicBattlegrounds|FullyQualifiedName~UT_DataQuestFinishRewards"')
```

and in `test_every_name_in_the_filter_is_a_test_class`:

```python
        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice", "UT_ClassicBattlegrounds",
                                 "UT_DataQuestFinishRewards"])
```

- [ ] **Step 2: Run the test to see it fail**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_DataQuestFinishRewards"`
Expected: build error `'DataQuest' does not contain a definition for 'FinishRewardIndex'`.

- [ ] **Step 3: Implement**

Run this from the repository root (it keeps `DataQuest.cs`'s CRLF and BOM):

```bash
python3 - <<'PY'
path = "source/server/GameServer/quests/QuestsMgr/DataQuest.cs"
with open(path, encoding="utf-8-sig", newline="") as f:
    text = f.read()
assert "\r\n" in text
anchor = ("\t\t/// <summary>\r\n\t\t/// Finish the quest and update the player quest list\r\n\t\t/// </summary>\r\n"
          "\t\t/// <summary>\r\n\t\t/// Classic quests (goal 10)")
assert text.count(anchor) == 1, "FinishQuest's summary moved; find the anchor by hand"
method = (
    "\t\t/// <summary>\r\n"
    "\t\t/// HearthDAoC: the entry of a reward list (RewardXP, RewardRP, RewardCLXP, RewardBP, RewardMoney) that finishing\r\n"
    "\t\t/// the quest at <paramref name=\"step\"/> pays: the finishing stage's own entry, the last entry when the list is\r\n"
    "\t\t/// shorter (a single value pays that value), the first for a quest with no stage, and -1 for an empty list.\r\n"
    "\t\t/// Upstream paid the first entry, so the classic quests, which keep the final reward in the last entry, never\r\n"
    "\t\t/// paid it.\r\n"
    "\t\t/// </summary>\r\n"
    "\t\tpublic static int FinishRewardIndex(int step, int entries)\r\n"
    "\t\t{\r\n"
    "\t\t\tif (entries <= 0)\r\n"
    "\t\t\t\treturn -1;\r\n"
    "\t\t\treturn Math.Min(Math.Max(step, 1), entries) - 1;\r\n"
    "\t\t}\r\n\r\n")
text = text.replace(anchor, method + anchor, 1)
start = text.index("public virtual bool FinishQuest(GameObject obj, bool checkCustomStep)")
end = text.index("m_charQuest.Step = 0;", start)
body = text[start:end]
for name in ("XP", "RP", "CLXP", "BP"):
    old = f"reward{name} = m_reward{name}s[0];"
    assert body.count(old) == 1, old
    body = body.replace(old, f"reward{name} = m_reward{name}s[FinishRewardIndex(lastStep, m_reward{name}s.Count)]; // HearthDAoC: the finishing stage's entry")
old = "rewardMoney = m_rewardMoneys[0];"
assert body.count(old) == 1, old
body = body.replace(old, "rewardMoney = m_rewardMoneys[FinishRewardIndex(lastStep, m_rewardMoneys.Count)]; // HearthDAoC: the finishing stage's entry")
text = text[:start] + body + text[end:]
with open(path, "w", encoding="utf-8-sig", newline="") as f:
    f.write(text)
PY
```

Each of the five reads sits inside an `if (m_rewardXXXs.Count > 0)` block, so the index is never -1 there.
`lastStep` is the method's existing `int lastStep = Step;`.

- [ ] **Step 4: Run the tests**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_DataQuestFinishRewards"`
Expected: 7 passed.
Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_workflows.py'`
Expected: OK.
Check the file kept its endings: `python3 -c "b=open('source/server/GameServer/quests/QuestsMgr/DataQuest.cs','rb').read(); print(b[:3]==b'\xef\xbb\xbf', b.count(b'\r\n')==b.count(b'\n'))"` prints `True True`.

- [ ] **Step 5: Commit**

```bash
git add source/server/GameServer/quests/QuestsMgr/DataQuest.cs source/server/Tests/UnitTests/UT_DataQuestFinishRewards.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -m "fix(quests): finishing a data quest pays the finishing stage's XP and coin

DataQuest.FinishQuest paid the first entry of RewardXP, RewardRP, RewardCLXP,
RewardBP and RewardMoney. Upstream's classic quests keep the final reward in the
last entry, so 1,240 of 1,302 never paid their final XP and 419 their final
coin. One-stage and collection quests (single values) pay as before.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Quest dependencies by quest ID

**Files:**
- Create: `source/server/GameServer/quests/QuestsMgr/QuestDependencies.cs`
- Modify: `source/server/GameServer/quests/QuestsMgr/DataQuest.cs` (the `QuestDependency` parse in `ParseQuestData`, the dependency check in `CheckQuestQualification`)
- Create: `source/server/Tests/UnitTests/UT_DataQuestDependency.cs`
- Modify: `.github/workflows/server-image.yml:113`, `deploy/tests/test_workflows.py`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces (namespace `DOL.GS.Quests`, `public static class QuestDependencies`):
  - `bool TryParseIds(string entry, out int[] ids, out bool closes)`
  - `bool IsIdEntry(string entry)`
  - `bool IsValid(string entry)`
  - `bool IsMet(string entry, ICollection<string> finishedNames, ICollection<int> finishedIds, ICollection<int> activeIds)`
  - `bool AreMet(IEnumerable<string> entries, ICollection<string> finishedNames, ICollection<int> finishedIds, ICollection<int> activeIds)`

- [ ] **Step 1: Write the failing test**

Create `source/server/Tests/UnitTests/UT_DataQuestDependency.cs` (LF):

```csharp
using System.Collections.Generic;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: a data quest's QuestDependency entries. Upstream's form is a quest name; the fork adds quest IDs
// ("#20188", any one of "#20157/20469") and closing entries ("!#20478": not while that quest is active or finished),
// because upstream's epic chains reuse names (spec docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.1).
[TestFixture]
public sealed class UT_DataQuestDependency
{
    private static readonly List<string> Names = new() { "Regal Nobility", "Entry Into Tomorrow" };
    private static readonly HashSet<int> Finished = new() { 21306, 20157 };
    private static readonly HashSet<int> Active = new() { 20469 };

    private static bool Met(string entry) => QuestDependencies.IsMet(entry, Names, Finished, Active);

    [TestCase("Regal Nobility", true)]
    [TestCase("regal nobility", true)]          // names compare without case, as upstream did
    [TestCase("Hidden Insurrection", false)]
    [TestCase("#21306", true)]
    [TestCase("#21491", false)]
    [TestCase("#21491/21306", true)]            // any one of them
    [TestCase("#20469", false)]                 // active is not finished
    [TestCase("!#21324", true)]                 // neither active nor finished
    [TestCase("!#20157", false)]                // finished closes
    [TestCase("!#20469", false)]                // active closes too
    [TestCase("!#21324/20469", false)]
    [TestCase(" #21306 ", true)]                // spaces around an ID entry don't matter
    public void OneEntry(string entry, bool met) => Assert.That(Met(entry), Is.EqualTo(met));

    [TestCase("#")]
    [TestCase("#x")]
    [TestCase("!#")]
    [TestCase("#21306/")]
    [TestCase("#0")]
    [TestCase("#-5")]
    [TestCase("!#abc")]
    public void AMalformedIdEntryIsNeverMetAndNotValid(string entry)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Met(entry), Is.False);
            Assert.That(QuestDependencies.IsValid(entry), Is.False);
        });
    }

    [Test]
    public void EveryEntryMustBeMet()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuestDependencies.AreMet(new[] { "#21306", "!#21324" }, Names, Finished, Active), Is.True);
            Assert.That(QuestDependencies.AreMet(new[] { "#21306", "!#20469" }, Names, Finished, Active), Is.False);
            Assert.That(QuestDependencies.AreMet(new[] { "Regal Nobility", "#21491" }, Names, Finished, Active), Is.False);
            Assert.That(QuestDependencies.AreMet(new string[0], Names, Finished, Active), Is.True);
        });
    }

    [Test]
    public void ParsingGivesTheIdsAndTheForm()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuestDependencies.TryParseIds("#20157/20469", out int[] ids, out bool closes), Is.True);
            Assert.That(ids, Is.EqualTo(new[] { 20157, 20469 }));
            Assert.That(closes, Is.False);
            Assert.That(QuestDependencies.TryParseIds("!#20478", out ids, out closes), Is.True);
            Assert.That(ids, Is.EqualTo(new[] { 20478 }));
            Assert.That(closes, Is.True);
            Assert.That(QuestDependencies.TryParseIds("Lord of Deceit", out _, out _), Is.False);
            Assert.That(QuestDependencies.IsIdEntry("Lord of Deceit"), Is.False);
            Assert.That(QuestDependencies.IsValid("Lord of Deceit"), Is.True);
        });
    }
}
```

Append `|FullyQualifiedName~UT_DataQuestDependency` to the filter in `.github/workflows/server-image.yml` (after
`UT_DataQuestFinishRewards`), the same to `SERVER_UNIT_TESTS` in `deploy/tests/test_workflows.py`, and
`"UT_DataQuestDependency"` to the end of the expected list in `test_every_name_in_the_filter_is_a_test_class`.

- [ ] **Step 2: Run the test to see it fail**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_DataQuestDependency"`
Expected: build error `The name 'QuestDependencies' does not exist in the current context`.

- [ ] **Step 3: Implement `QuestDependencies`**

Create `source/server/GameServer/quests/QuestsMgr/QuestDependencies.cs` (LF):

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DOL.GS.Quests
{
    /// <summary>
    /// HearthDAoC: the forms of a data quest's QuestDependency entries ('|'-separated; every entry must be met):
    /// <list type="bullet">
    /// <item><c>Name</c>: a finished data quest has that name (upstream's form).</item>
    /// <item><c>#20188</c>: the data quest with that ID is finished.</item>
    /// <item><c>#20157/20469</c>: at least one of these is finished.</item>
    /// <item><c>!#20478</c> or <c>!#20478/21324</c>: none of these is active or finished.</item>
    /// </list>
    /// Upstream's epic chains reuse names (the Guild of Shadows 25 and 30 are both "Regal Nobility"), so a name can't
    /// say which step comes before; an ID can (spec docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.1).
    /// </summary>
    public static class QuestDependencies
    {
        /// <summary>Whether an entry is an ID entry: it starts with "#" or "!#" (spaces around it don't count).</summary>
        public static bool IsIdEntry(string entry)
        {
            string text = entry?.Trim() ?? string.Empty;
            return text.StartsWith("#", StringComparison.Ordinal) || text.StartsWith("!#", StringComparison.Ordinal);
        }

        /// <summary>The IDs of an ID entry, and whether it closes the quest (the "!#" form). False for a name and for a
        /// malformed ID entry (no ID, a part that isn't a whole number above 0).</summary>
        public static bool TryParseIds(string entry, out int[] ids, out bool closes)
        {
            ids = Array.Empty<int>();
            closes = false;
            string text = entry?.Trim() ?? string.Empty;
            bool closing;
            if (text.StartsWith("!#", StringComparison.Ordinal))
            {
                closing = true;
                text = text.Substring(2);
            }
            else if (text.StartsWith("#", StringComparison.Ordinal))
            {
                closing = false;
                text = text.Substring(1);
            }
            else
                return false;
            string[] parts = text.Split('/');
            var parsed = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]) || parsed[i] <= 0)
                    return false;
            }
            ids = parsed;
            closes = closing;
            return true;
        }

        /// <summary>Whether an entry can ever be met: a name that isn't blank, or a well-formed ID entry.</summary>
        public static bool IsValid(string entry) =>
            IsIdEntry(entry) ? TryParseIds(entry, out _, out _) : !string.IsNullOrWhiteSpace(entry);

        /// <summary>Whether one entry is met. Names compare without case, as upstream did; a malformed ID entry is
        /// never met.</summary>
        public static bool IsMet(string entry, ICollection<string> finishedNames, ICollection<int> finishedIds, ICollection<int> activeIds)
        {
            if (!IsIdEntry(entry))
            {
                if (string.IsNullOrWhiteSpace(entry))
                    return false;
                foreach (string name in finishedNames)
                {
                    if (string.Equals(name, entry, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            if (!TryParseIds(entry, out int[] ids, out bool closes))
                return false;
            foreach (int id in ids)
            {
                if (closes && (finishedIds.Contains(id) || activeIds.Contains(id)))
                    return false;
                if (!closes && finishedIds.Contains(id))
                    return true;
            }
            return closes;
        }

        /// <summary>Whether every entry is met (no entries: met).</summary>
        public static bool AreMet(IEnumerable<string> entries, ICollection<string> finishedNames, ICollection<int> finishedIds, ICollection<int> activeIds)
        {
            foreach (string entry in entries)
            {
                if (!IsMet(entry, finishedNames, finishedIds, activeIds))
                    return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 4: Use it in `DataQuest`**

Run from the repository root (keeps CRLF and the BOM):

```bash
python3 - <<'PY'
path = "source/server/GameServer/quests/QuestsMgr/DataQuest.cs"
with open(path, encoding="utf-8-sig", newline="") as f:
    text = f.read()

# 1. The check in CheckQuestQualification.
old_check = (
    "\t\t\t// check to see if this quest requires another to be done first\r\n"
    "\t\t\tif (m_questDependencies.Count > 0)\r\n"
    "\t\t\t{\r\n"
    "\t\t\t\tint numFound = 0;\r\n"
    "\r\n"
    "\t\t\t\tforeach (string str in m_questDependencies)\r\n"
    "\t\t\t\t{\r\n"
    "\t\t\t\t\tforeach (AbstractQuest quest in finishedQuests)\r\n"
    "\t\t\t\t\t{\r\n"
    "\t\t\t\t\t\tif (quest is DataQuest dataQuest && dataQuest.Name.ToLower() == str.ToLower())\r\n"
    "\t\t\t\t\t\t{\r\n"
    "\t\t\t\t\t\t\tnumFound++;\r\n"
    "\t\t\t\t\t\t\tbreak;\r\n"
    "\t\t\t\t\t\t}\r\n"
    "\t\t\t\t\t}\r\n"
    "\t\t\t\t}\r\n"
    "\r\n"
    "\t\t\t\tif (numFound < m_questDependencies.Count)\r\n"
    "\t\t\t\t\treturn false;\r\n"
    "\t\t\t}\r\n")
assert text.count(old_check) == 1, "the dependency check changed upstream; port it by hand"
new_check = (
    "\t\t\t// check to see if this quest requires another to be done first\r\n"
    "\t\t\t// HearthDAoC: an entry is a quest name or a quest ID form (QuestDependencies: \"#id\", \"#a/b\", \"!#a/b\").\r\n"
    "\t\t\tif (m_questDependencies.Count > 0)\r\n"
    "\t\t\t{\r\n"
    "\t\t\t\tvar finishedNames = new List<string>();\r\n"
    "\t\t\t\tvar finishedIds = new HashSet<int>();\r\n"
    "\t\t\t\tforeach (AbstractQuest quest in finishedQuests)\r\n"
    "\t\t\t\t{\r\n"
    "\t\t\t\t\tif (quest is DataQuest dataQuest)\r\n"
    "\t\t\t\t\t{\r\n"
    "\t\t\t\t\t\tfinishedNames.Add(dataQuest.Name);\r\n"
    "\t\t\t\t\t\tfinishedIds.Add(dataQuest.ID);\r\n"
    "\t\t\t\t\t}\r\n"
    "\t\t\t\t}\r\n"
    "\r\n"
    "\t\t\t\tvar activeIds = new HashSet<int>();\r\n"
    "\t\t\t\tforeach (AbstractQuest quest in player.QuestList.Keys)\r\n"
    "\t\t\t\t{\r\n"
    "\t\t\t\t\tif (quest is DataQuest dataQuest)\r\n"
    "\t\t\t\t\t\tactiveIds.Add(dataQuest.ID);\r\n"
    "\t\t\t\t}\r\n"
    "\r\n"
    "\t\t\t\tif (!QuestDependencies.AreMet(m_questDependencies, finishedNames, finishedIds, activeIds))\r\n"
    "\t\t\t\t\treturn false;\r\n"
    "\t\t\t}\r\n")
text = text.replace(old_check, new_check, 1)

# 2. The parse in ParseQuestData: say once per quest when an ID entry can never be met.
old_parse = (
    "\t\t\t\t\t\tif (str != string.Empty)\r\n"
    "\t\t\t\t\t\t{\r\n"
    "\t\t\t\t\t\t\tm_questDependencies.Add(str);\r\n"
    "\t\t\t\t\t\t}\r\n")
assert text.count(old_parse) == 1, "the dependency parse changed upstream; port it by hand"
new_parse = (
    "\t\t\t\t\t\tif (str != string.Empty)\r\n"
    "\t\t\t\t\t\t{\r\n"
    "\t\t\t\t\t\t\tm_questDependencies.Add(str);\r\n"
    "\t\t\t\t\t\t\t// HearthDAoC: an ID entry that doesn't parse is never met; say so once per quest.\r\n"
    "\t\t\t\t\t\t\tif (!QuestDependencies.IsValid(str) && s_badDependencyLogged.TryAdd(m_dataQuest.ID, true))\r\n"
    "\t\t\t\t\t\t\t\tlog.Error($\"DataQuest [{m_dataQuest.ID}] {m_dataQuest.Name}: dependency \\\"{str}\\\" is not valid and is never met\");\r\n"
    "\t\t\t\t\t\t}\r\n")
text = text.replace(old_parse, new_parse, 1)

# 3. The once-per-quest set, after the existing logger field.
old_log = "\t\tprivate static readonly Logging.Logger log = Logging.LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);\r\n"
assert text.count(old_log) == 1
text = text.replace(old_log, old_log +
    "\t\t// HearthDAoC: quests whose malformed dependency entry was already logged.\r\n"
    "\t\tprivate static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> s_badDependencyLogged = new();\r\n", 1)

with open(path, "w", encoding="utf-8-sig", newline="") as f:
    f.write(text)
PY
```

- [ ] **Step 5: Run the tests**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_DataQuestDependency|FullyQualifiedName~UT_DataQuestFinishRewards"`
Expected: all pass (21 + 7).
Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_workflows.py'` — OK.
The endings check from Task 1 step 4 prints `True True`.

- [ ] **Step 6: Commit**

```bash
git add source/server/GameServer/quests/QuestsMgr/QuestDependencies.cs source/server/GameServer/quests/QuestsMgr/DataQuest.cs source/server/Tests/UnitTests/UT_DataQuestDependency.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -m "feat(quests): data quest dependencies by quest ID

QuestDependency entries can name quests by ID: \"#20188\" (finished), \"#a/b\"
(any one finished) and \"!#a/b\" (closed while one is active or finished).
Upstream's epic chains reuse names, so a name can't say which step comes
first. Plain names work as before; a malformed ID entry is never met and is
logged once.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: HearthDAoC's own classic quest data file

**Files:**
- Modify: `source/server/GameServer/quests/QuestsMgr/ClassicQuests.cs` (constants and fields near `FileName`; `Reload()`; new `Merge`, `ReadExtra`, `MarkerFor`)
- Create: `deploy/hearthdaoc-quests.json`
- Modify: `deploy/Dockerfile` (one `COPY` line)
- Create: `source/server/Tests/UnitTests/UT_ClassicQuestsExtra.cs`
- Create: `deploy/tests/test_epic_chains.py` (its first test class)
- Modify: `.github/workflows/server-image.yml:113`, `deploy/tests/test_workflows.py`

**Interfaces:**
- Produces (in `DOL.GS.Quests.ClassicQuests`):
  - `public static Config Merge(Config upstream, Config extra)` — adds `extra`'s quests where `upstream` has no entry for
    the ID, and `extra`'s quest monster IDs; returns `upstream`. `extra` null: unchanged.
  - `public static Point MarkerFor(int questId, int step)` — the stage's map marker, or null.
  - the file `hearthdaoc-quests.json` next to the server (`/app/server/` in the image).

- [ ] **Step 1: Write the failing tests**

Create `source/server/Tests/UnitTests/UT_ClassicQuestsExtra.cs` (LF):

```csharp
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
```

Append `|FullyQualifiedName~UT_ClassicQuestsExtra` to the CI filter and `SERVER_UNIT_TESTS`, and `"UT_ClassicQuestsExtra"`
to the expected list, as in Task 2.

Create `deploy/tests/test_epic_chains.py`:

```python
"""The epic chains (sub-project 4): the fork's quest data file, the upstream source edits, and the world fix
deploy/bin/epic_chains.py.

Spec: docs/fork/specs/2026-10-09-epic-chains-design.md. The world data tests run on a copy of a clean classic world
(HDC_TEST_WORLD); the others need no world.
"""
import json
import os
import pathlib
import re
import shutil
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
EXTRA_QUESTS = os.path.join(DEPLOY, "hearthdaoc-quests.json")
DOCKERFILE = os.path.join(DEPLOY, "Dockerfile")
LORD_ELIDYN = "1f005bc1-27ae-40b0-bc42-1ec407a4aa34"
LEVEL_50_IDS = [990509, 990511, 990513, 990512, 990519]


def read(path):
    """A source file's text (upstream files may start with a BOM)."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


class ExtraQuestFileTests(unittest.TestCase):
    """deploy/hearthdaoc-quests.json: ClassicQuests reads it beside upstream's classic-quests.json (spec 3.2)."""

    def setUp(self):
        with open(EXTRA_QUESTS, encoding="utf-8") as f:
            self.extra = json.load(f)

    def test_it_marks_the_level_50_stages_and_lord_elidyn(self):
        self.assertEqual(sorted(int(k) for k in self.extra["Quests"]), sorted(LEVEL_50_IDS))
        for quest in self.extra["Quests"].values():
            steps = quest["Steps"]
            self.assertEqual(len(steps), 3)
            self.assertIsNone(steps[0])
            self.assertEqual(steps[1]["Marker"], {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032})
            self.assertEqual(steps[2]["Marker"], {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088})
        self.assertEqual(self.extra["QuestMonsterIds"], [LORD_ELIDYN])

    def test_the_image_puts_it_next_to_the_server(self):
        self.assertIn("COPY deploy/hearthdaoc-quests.json /app/server/hearthdaoc-quests.json\n", read(DOCKERFILE))


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_ClassicQuestsExtra"`
Expected: build error `'ClassicQuests' does not contain a definition for 'Merge'`.
Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'`
Expected: errors, `FileNotFoundError` for `deploy/hearthdaoc-quests.json`.

- [ ] **Step 3: Implement**

`ClassicQuests.cs` is LF. Run from the repository root:

```bash
python3 - <<'PY'
path = "source/server/GameServer/quests/QuestsMgr/ClassicQuests.cs"
with open(path, encoding="utf-8", newline="") as f:
    text = f.read()
assert "\r\n" not in text

old = '        private const string FileName = "classic-quests.json";\n'
assert text.count(old) == 1
text = text.replace(old, old +
    '        /// <summary>HearthDAoC: the fork\'s additions (the Guild of Shadows level-50 quests), read beside FileName.</summary>\n'
    '        private const string ExtraFileName = "hearthdaoc-quests.json";\n', 1)

old = '        private static DateTime _loadedWrite;\n'
assert text.count(old) == 1
text = text.replace(old, old + '        private static DateTime _loadedExtraWrite; // HearthDAoC\n', 1)

start = text.index("        public static void Reload()\n")
end = text.index("        private static void Refresh(object state)\n")
new_reload = '''        public static void Reload()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, FileName);
                if (!File.Exists(path)) return;
                DateTime write = File.GetLastWriteTimeUtc(path);
                // HearthDAoC: hearthdaoc-quests.json is read too; a change to either file reloads both.
                string extraPath = Path.Combine(AppContext.BaseDirectory, ExtraFileName);
                DateTime extraWrite = File.Exists(extraPath) ? File.GetLastWriteTimeUtc(extraPath) : DateTime.MinValue;
                if (write == _loadedWrite && extraWrite == _loadedExtraWrite) return;
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options);
                if (config == null) return;
                config.QuestMonsterIds = new HashSet<string>(config.QuestMonsterIds ?? new(), StringComparer.Ordinal);
                config = Merge(config, ReadExtra(extraPath, options));
                _config = config;
                _loadedWrite = write;
                _loadedExtraWrite = extraWrite;
                if (Log.IsInfoEnabled)
                    Log.Info($"CLASSIC_QUESTS loaded quests={config.Quests.Count} questMonsters={config.QuestMonsterIds.Count}");
            }
            catch (Exception ex)
            {
                Log.Error("Could not load classic-quests.json; classic quest markers and event spawns are off", ex);
            }
        }

        /// <summary>HearthDAoC: the fork's additions, or null (said once) when the file is missing or unreadable.</summary>
        private static Config ReadExtra(string path, JsonSerializerOptions options)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options);
                Log.Warn($"{ExtraFileName} not found; HearthDAoC's quest additions are off");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {ExtraFileName}; HearthDAoC's quest additions are off", ex);
            }
            return null;
        }

        /// <summary>
        /// HearthDAoC: adds the extra file's quests where upstream's file has no entry for the ID (upstream's entry wins),
        /// and its quest monsters; returns <paramref name="upstream"/>. A null extra file changes nothing.
        /// </summary>
        public static Config Merge(Config upstream, Config extra)
        {
            if (extra == null) return upstream;
            if (extra.Quests != null)
                foreach (KeyValuePair<int, QuestInfo> quest in extra.Quests)
                    upstream.Quests.TryAdd(quest.Key, quest.Value);
            if (extra.QuestMonsterIds != null)
                foreach (string mob in extra.QuestMonsterIds)
                    upstream.QuestMonsterIds.Add(mob);
            return upstream;
        }

        /// <summary>HearthDAoC: the map marker of a quest's stage (either file), or null. The GM's /epic goto uses it.</summary>
        public static Point MarkerFor(int questId, int step) =>
            _config.Quests.TryGetValue(questId, out QuestInfo info) && info.Steps != null && step > 0 && step < info.Steps.Count
                ? info.Steps[step]?.Marker
                : null;

'''
text = text[:start] + new_reload + text[end:]
with open(path, "w", encoding="utf-8", newline="") as f:
    f.write(text)
PY
```

(`QuestInfo.Steps` is a `List<StepInfo>` and `StepInfo.Marker` a `Point`, as `MarkerFor` uses them.)

Create `deploy/hearthdaoc-quests.json`:

```json
{
  "_about": "HearthDAoC's additions to upstream's classic-quests.json; ClassicQuests reads both. The Guild of Shadows level-50 \"Lord of Deceit\" quests (one per class, added by deploy/bin/epic_chains.py): stage 1 kill Lord Elidyn, stage 2 back to Captain Rhodri; Lord Elidyn is a quest monster gamebots leave alone. Spec: docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.2.",
  "Quests": {
    "990509": {"Steps": [null, {"Marker": {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032}}, {"Marker": {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088}}]},
    "990511": {"Steps": [null, {"Marker": {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032}}, {"Marker": {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088}}]},
    "990513": {"Steps": [null, {"Marker": {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032}}, {"Marker": {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088}}]},
    "990512": {"Steps": [null, {"Marker": {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032}}, {"Marker": {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088}}]},
    "990519": {"Steps": [null, {"Marker": {"Region": 1, "X": 568158, "Y": 404718, "Z": 5032}}, {"Marker": {"Region": 1, "X": 528239, "Y": 359818, "Z": 9088}}]}
  },
  "QuestMonsterIds": ["1f005bc1-27ae-40b0-bc42-1ec407a4aa34"]
}
```

In `deploy/Dockerfile`, after the line `COPY --from=build /src/source/server/Release /app/server`, add:

```dockerfile
COPY deploy/hearthdaoc-quests.json /app/server/hearthdaoc-quests.json
```

(`deploy/` is already a release path in `deploy/release_tag.py`, so a change to the file makes a release.)

- [ ] **Step 4: Run the tests**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_ClassicQuestsExtra|FullyQualifiedName~UT_ClassicQuestsConfig"`
Expected: all pass (upstream's `UT_ClassicQuestsConfig` still parses its sample).
Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'` and `-p 'test_workflows.py'` — OK.

- [ ] **Step 5: Commit**

```bash
git add source/server/GameServer/quests/QuestsMgr/ClassicQuests.cs source/server/Tests/UnitTests/UT_ClassicQuestsExtra.cs deploy/hearthdaoc-quests.json deploy/Dockerfile deploy/tests/test_epic_chains.py .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -m "feat(quests): ClassicQuests reads the fork's hearthdaoc-quests.json too

The fork's additions to upstream's classic-quests.json: map markers for the
Guild of Shadows level-50 quests and Lord Elidyn as a quest monster bots leave
alone. Upstream's entry wins on the same quest ID; a missing or broken file
is said once and changes nothing. The image copies it next to the server.
MarkerFor gives a stage's marker (for /epic goto).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The Defenders copy goes, and four level-50 NPC lookups

**Files:**
- Delete: `source/server/GameServer/scripts/quests/Albion/epic/Shadows50.cs`
- Modify (one line each, CRLF kept): `scripts/quests/Albion/epic/Academy50.cs:114`, `scripts/quests/Hibernia/epic/Essence50.cs:118`, `scripts/quests/Midgard/epic/Mystic50.cs:123`, `scripts/quests/Midgard/epic/Viking50.cs:169` (under `source/server/GameServer/`)
- Modify: `deploy/tests/test_epic_chains.py` (class `EpicSourceTests`)

**Interfaces:**
- Produces: the type `DOL.GS.Quests.Albion.Shadows_50` no longer exists (Task 7 migrates its saved rows).

- [ ] **Step 1: Write the failing tests**

Add to `deploy/tests/test_epic_chains.py`, before the `if __name__` line:

```python
# Each level-50 quest looks its NPC up at a spot and creates it at another when none is found there, so a copy a GM
# saved shows up as a second NPC at every start (spec 3.3). (file, NPC name, variable the quest creates it in)
LOOKUPS = (
    ("scripts/quests/Albion/epic/Academy50.cs", "Master Ferowl", "Ferowl"),
    ("scripts/quests/Hibernia/epic/Essence50.cs", "Brigit", "Brigit"),
    ("scripts/quests/Midgard/epic/Mystic50.cs", "Danica", "Danica"),
    ("scripts/quests/Midgard/epic/Viking50.cs", "Elizabeth", "Elizabeth"),
)


def lookup_and_creation(text, name, var):
    """((lookup X, Y), (creation X, Y)) of the NPC's block in a level-50 quest."""
    block = text[text.index(f'GetNPCsByName("{name}"'):]
    found = re.search(r"npc\.X == (\d+) && npc\.Y == (\d+)", block)
    x = re.search(rf"\b{var}\.X = (\d+);", block)
    y = re.search(rf"\b{var}\.Y = (\d+);", block)
    return (found.group(1), found.group(2)), (x.group(1), y.group(1))


class EpicSourceTests(unittest.TestCase):
    def test_the_defenders_copy_of_shadows_50_is_gone(self):
        self.assertFalse(os.path.exists(os.path.join(GAME_SERVER, "scripts", "quests", "Albion", "epic", "Shadows50.cs")))
        for path in pathlib.Path(GAME_SERVER).rglob("*.cs"):
            self.assertNotIn("class Shadows_50", read(str(path)), str(path))

    def test_each_level_50_quest_finds_its_npc_where_it_creates_it(self):
        for rel, name, var in LOOKUPS:
            with self.subTest(rel):
                found, made = lookup_and_creation(read(os.path.join(GAME_SERVER, rel)), name, var)
                self.assertEqual(found, made)

    def test_the_four_files_keep_their_crlf_endings(self):
        for rel, _name, _var in LOOKUPS:
            with open(os.path.join(GAME_SERVER, rel), "rb") as f:
                raw = f.read()
            self.assertEqual(raw.count(b"\r\n"), raw.count(b"\n"), rel)
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), rel)
```

- [ ] **Step 2: Run them to see them fail**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'`
Expected: `test_the_defenders_copy_of_shadows_50_is_gone` fails (the file exists) and
`test_each_level_50_quest_finds_its_npc_where_it_creates_it` fails four times, e.g. `('559690', '510258') != ('559461', '510653')`.

- [ ] **Step 3: Implement**

```bash
git rm -q source/server/GameServer/scripts/quests/Albion/epic/Shadows50.cs
python3 - <<'PY'
base = "source/server/GameServer/"
edits = {
    "scripts/quests/Albion/epic/Academy50.cs": ("npc.X == 559690 && npc.Y == 510258", "npc.X == 559461 && npc.Y == 510653"),
    "scripts/quests/Hibernia/epic/Essence50.cs": ("npc.X == 32927 && npc.Y == 32743", "npc.X == 33105 && npc.Y == 32909"),
    "scripts/quests/Midgard/epic/Mystic50.cs": ("npc.X == 802818 && npc.Y == 727413", "npc.X == 803559 && npc.Y == 723329"),
    "scripts/quests/Midgard/epic/Viking50.cs": ("npc.X == 802597 && npc.Y == 727896", "npc.X == 802808 && npc.Y == 727114"),
}
for rel, (old, new) in edits.items():
    with open(base + rel, encoding="utf-8", newline="") as f:
        text = f.read()
    assert text.count(old) == 1, rel
    # HearthDAoC: look the NPC up where the quest creates it (a GM-saved copy was a second NPC at every start).
    text = text.replace(old, new + " /* HearthDAoC: where the quest creates it */", 1)
    with open(base + rel, "w", encoding="utf-8", newline="") as f:
        f.write(text)
PY
```

Then check that nothing else referenced the deleted class: `grep -rn "Shadows_50" source/server --include=*.cs`
prints nothing.

- [ ] **Step 4: Run the tests and the build**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'` — OK.
Run: `dotnet build source/server/DOLLinux.sln -c Release --nologo -v q` — builds with no new errors.

- [ ] **Step 5: Commit**

```bash
git add -A source/server/GameServer/scripts/quests deploy/tests/test_epic_chains.py
git commit -m "fix(quests): drop the Defenders copy of Shadows_50; four level-50 NPC lookups

scripts/quests/Albion/epic/Shadows50.cs was the Defenders quest (Feast of the
Decadent) with the Guild of Shadows classes; the real level-50 step is a data
quest (world fix epic-chains-v1). Academy, Essence, Mystic and Viking 50 look
their NPC up where they create it, so a copy a GM saved is no longer a second
NPC at every start.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 5: The world fix: the chain's links, closures, XP and coin, rewards and texts

The first six steps of `deploy/bin/epic_chains.py` (spec 3.4, steps 1–6), its data file, and its tests. Task 6 adds
the items, Task 7 the level-50 quests and the old `Shadows_50` and wires the fix into `world_fixes.py`.

**Files:**
- Create: `deploy/bin/epic_chains.py`, `deploy/bin/epic_chains_data.json`
- Modify: `deploy/tests/test_epic_chains.py`

**Interfaces:**
- Consumes: the entry forms of Task 2 (`#a/b`, `!#a/b`), which this fix writes.
- Produces (module `epic_chains`):
  - `apply(conn, now=None, data=None) -> list[str]` — `[]` when a needed table is missing or the marker is there;
    else `[summary(counts)]`, or `[NOT_APPLIED.format(reason)]` after a rollback.
  - `load_data(path=DATA_FILE) -> dict`, `summary(counts: dict) -> str`, `pay_lists(stages: int, step: dict) -> (str, str)`
  - `STEPS`: a tuple of `step(conn, now, data) -> dict[str, int]`; Tasks 6 and 7 append to it.
  - constants `FIX_ID`, `MARKER_TABLE`, `NOT_APPLIED`, `CLASSIC`, `ARCHIVE`, `DATA_FILE`.
  - The data file's keys `classes` (name → class ID, in the column order) and `steps` (key → `ids` per class, `upstream`
    values, `links`, `xp`, `money`, `money_at`, `new`), `rewards`, `texts`. Tasks 6 and 7 add keys.

- [ ] **Step 1: Write the failing tests**

In `deploy/tests/test_epic_chains.py`, add `import unittest.mock` to the imports, and after the line
`sys.path.insert(0, BIN)` add:

```python
import epic_chains  # noqa: E402
```

Then add, before the `if __name__` line:

```python
CLASSIC = "DOL.GS.Quests.ClassicQuestStep"
NOW = "2026-10-09 12:00:00"
# The clean 0.35 world's summary line. Tasks 6 and 7 update it as they add steps.
SUMMARY = ("Epic chains: Guild of Shadows 60 links, 60 XP and coin, 4 Supply Runs closed, 2 rewards and 7 texts fixed; "
           "87 other links; 0 items added, 0 item fixes; 0 level-50 quests, Lord Elidyn's camp 0 restored; "
           "Shadows_50: 0 finished carried, 0 removed, 0 epic vests recharged")
# Every other line's steps pinned on the clean 0.35 world: (name, level) -> rows (spec 2.5).
EXPECTED_PINS = {
    ("A War of Old", 20): 2, ("A War of Old", 25): 2, ("A War of Old", 30): 2,
    ("An End to the Daggers", 43): 5, ("An End to the Daggers", 45): 5, ("An End to the Daggers", 48): 1,
    ("Feast of the Decadent", 45): 4, ("Feast of the Decadent", 48): 1,
    ("Hands Of Fate", 30): 4,
    ("Last Heir", 45): 5, ("Last Heir", 47): 5,
    ("Legend of the Lake", 20): 4, ("Legend of the Lake", 25): 4,
    ("Passage to Eternity", 45): 2, ("Passage to Eternity", 48): 2,
    ("Regal Nobility", 30): 1,
    ("Saving the Clan", 45): 3, ("Saving the Clan", 48): 1,
    ("Symbol of the Broken", 45): 3, ("Symbol of the Broken", 48): 3,
    ("Thane's Blood", 30): 6,
    ("The Desire of a God", 45): 2, ("The Desire of a God", 48): 1,
    ("The Moonstone Twin", 45): 4, ("The Moonstone Twin", 47): 1,
    ("The Red Dagger", 20): 5, ("The Red Dagger", 25): 6,
    ("The War Continues", 40): 1,
    ("Unnatural Powers", 47): 2,
}
FINALES = {"Feast of the Decadent", "Passage to Eternity", "Symbol of the Broken", "An End to the Daggers",
           "Saving the Clan", "The Desire of a God", "Last Heir", "The Moonstone Twin", "Lord of Deceit"}


def rows(conn, sql, params=()):
    """Query results as dicts."""
    cur = conn.execute(sql, params)
    columns = [d[0] for d in cur.description]
    return [dict(zip(columns, r)) for r in cur.fetchall()]


def entry_met(entry, finished_names, finished_ids, active_ids):
    """DataQuest's rule for one dependency entry (QuestDependencies.IsMet in C#), to walk the chains here."""
    text = entry.strip()
    if not text.startswith(("#", "!#")):
        return entry.lower() in {n.lower() for n in finished_names}
    closes = text.startswith("!#")
    parts = text[2 if closes else 1:].split("/")
    if not all(p.strip().isdigit() and int(p) > 0 for p in parts):
        return False
    ids = {int(p) for p in parts}
    if closes:
        return not ids & (set(finished_ids) | set(active_ids))
    return bool(ids & set(finished_ids))


def can_take(row, level, finished_ids, active_ids, names):
    """DataQuest.CheckQuestQualification for a chain row: the level range, not finished or active (MaxCount 1), and
    every dependency entry met. names: quest ID -> name, for name entries."""
    if not row["MinLevel"] <= level <= row["MaxLevel"] or row["ID"] in finished_ids or row["ID"] in active_ids:
        return False
    finished_names = [names[i] for i in finished_ids]
    return all(entry_met(e, finished_names, finished_ids, active_ids)
               for e in (row["QuestDependency"] or "").split("|") if e)


class DataQuestSourceTests(unittest.TestCase):
    def test_dataquest_reads_dependencies_only_when_offering(self):
        # The closed Supply Runs need themselves finished, which closes them for offers only
        # (CheckQuestQualification), so a character already on one can finish it (spec 3.4, step 3).
        text = read(os.path.join(GAME_SERVER, "quests", "QuestsMgr", "DataQuest.cs"))
        methods = set()
        for m in re.finditer(r"m_questDependencies", text):
            line = text[text.rindex("\n", 0, m.start()) + 1:text.index("\n", m.start())]
            if "new List<string>()" in line:
                continue  # the field itself
            heads = list(re.finditer(r"\n\t\t(?:public|protected|private|internal)[^\n(]*?\b(\w+)\s*\(", text[:m.start()]))
            methods.add(heads[-1].group(1))
        self.assertEqual(methods, {"CheckQuestQualification", "ParseQuestData"})


class EpicDataTests(unittest.TestCase):
    """deploy/bin/epic_chains_data.json is consistent with itself and the spec (no world needed)."""

    def setUp(self):
        self.data = epic_chains.load_data()

    def test_five_classes_in_the_spec_order(self):
        self.assertEqual(self.data["classes"], {"Infiltrator": 9, "Mercenary": 11, "Cabalist": 13, "Necromancer": 12,
                                                "Reaver": 19})

    def test_every_step_has_one_quest_per_class_and_links_to_known_steps(self):
        steps = self.data["steps"]
        self.assertEqual(list(steps), ["7", "7closed", "7si", "11", "11si", "15", "20", "25", "30", "40", "43", "45",
                                       "48", "50"])
        for key, step in steps.items():
            with self.subTest(key):
                self.assertEqual(len(step["ids"]), 5)
                for link in step.get("links", []):
                    for target in link.lstrip("!").split("/"):
                        self.assertIn(target, steps)
        self.assertEqual(steps["50"]["ids"], LEVEL_50_IDS)
        self.assertEqual([i is None for i in steps["7closed"]["ids"]], [False, False, False, True, False])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class EpicWorldTests(unittest.TestCase):
    """The fix on a copy of the clean classic world, applied once (spec 2 and 3.4)."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.conn = sqlite3.connect(cls.db)
        cls.before = {r["ID"]: r for r in rows(cls.conn, "SELECT * FROM DataQuest")}
        cls.data = epic_chains.load_data()
        cls.result = epic_chains.apply(cls.conn, NOW)
        cls.conn.commit()
        cls.after = {r["ID"]: r for r in rows(cls.conn, "SELECT * FROM DataQuest")}
        cls.names = {i: r["Name"] for i, r in cls.after.items()}

    @classmethod
    def tearDownClass(cls):
        cls.conn.close()
        cls.tmp.cleanup()

    def open_steps(self, col, finished=(), active=()):
        """The chain steps (data keys) a class (data column) can take at level 50."""
        steps = self.data["steps"]
        finished_ids = {steps[k]["ids"][col] for k in finished}
        active_ids = {steps[k]["ids"][col] for k in active}
        return [key for key, step in steps.items()
                if step["ids"][col] in self.after
                and can_take(self.after[step["ids"][col]], 50, finished_ids, active_ids, self.names)]

    def test_the_summary_line(self):
        self.assertEqual(self.result, [SUMMARY])
        self.assertEqual(self.conn.execute("SELECT FixId FROM fork_world_fixes").fetchall(), [("epic-chains-v1",)])

    def test_the_classic_route_opens_one_step_at_a_time(self):
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col), ["7", "7si"])
                done = []
                for key, opened in (("7", ["11", "11si"]), ("11", ["15"]), ("15", ["20"]), ("20", ["25"]),
                                    ("25", ["30"]), ("30", ["40"]), ("40", ["43"]), ("43", ["45"]), ("45", ["48"])):
                    done.append(key)
                    self.assertEqual(self.open_steps(col, done), opened, done)

    def test_the_shrouded_isles_route_reaches_15(self):
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, ["7si"]), ["11", "11si"])
                self.assertEqual(self.open_steps(col, ["7si", "11si"]), ["15"])
                self.assertEqual(self.open_steps(col, ["7", "11si"]), ["15"])

    def test_one_version_closes_the_other(self):
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, active=["7"]), [])
                self.assertEqual(self.open_steps(col, active=["7si"]), [])
                self.assertEqual(self.open_steps(col, ["7"], active=["11si"]), [])

    def test_the_closed_supply_runs_open_for_no_one_and_count_as_7(self):
        for col, name in enumerate(self.data["classes"]):
            closed = self.data["steps"]["7closed"]["ids"][col]
            if closed is None:
                continue  # the Necromancer has one Supply Run
            with self.subTest(name):
                self.assertNotIn("7closed", self.open_steps(col))
                self.assertEqual(self.open_steps(col, ["7closed"]), ["11", "11si"])
                self.assertEqual(self.after[closed]["QuestDependency"], f"#{closed}")
                same = lambda r: {k: v for k, v in r.items() if k not in ("QuestDependency", "LastTimeRowUpdated")}
                self.assertEqual(same(self.after[closed]), same(self.before[closed]))

    def test_30_cannot_be_skipped_and_48_needs_45(self):
        upto = ["7", "11", "15", "20", "25"]
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, upto), ["30"])
                self.assertEqual(self.open_steps(col, upto + ["30", "40", "43"]), ["45"])

    def test_every_other_line_is_pinned_to_its_step_before(self):
        gos = {i for step in self.data["steps"].values() for i in step["ids"] if i is not None}
        counts = {}
        for qid, old_row in self.before.items():
            row = self.after[qid]
            if qid in gos or row["QuestDependency"] == old_row["QuestDependency"]:
                continue
            key = (row["Name"], row["MinLevel"])
            counts[key] = counts.get(key, 0) + 1
            old = [e for e in (old_row["QuestDependency"] or "").split("|") if e]
            new = [e for e in row["QuestDependency"].split("|") if e]
            self.assertEqual(len(old), len(new), qid)
            for was, now in zip(old, new):
                if now == was:
                    continue
                targets = [self.after[int(t)] for t in now[1:].split("/")]
                self.assertEqual({t["Name"].lower() for t in targets}, {was.lower()}, qid)
                self.assertEqual(len({t["MinLevel"] for t in targets}), 1, qid)
                self.assertLess(targets[0]["MinLevel"], row["MinLevel"], qid)
        self.assertEqual(counts, EXPECTED_PINS)

    def test_finale_parts_need_the_part_before(self):
        for row in self.after.values():
            if row["Name"] not in FINALES or row["MinLevel"] <= 43 or CLASSIC not in (row["ClassType"] or ""):
                continue
            mine = epic_chains._classes(row["AllowedClasses"])
            lower = sorted({r["MinLevel"] for r in self.after.values()
                            if r["Name"] == row["Name"] and r["MinLevel"] < row["MinLevel"]
                            and epic_chains._shares(mine, epic_chains._classes(r["AllowedClasses"]))})
            with self.subTest(row["ID"]):
                if not lower:  # no earlier part for its classes (An End to the Daggers 45 for class 34): as upstream has it
                    self.assertEqual(row["QuestDependency"], self.before[row["ID"]]["QuestDependency"])
                    continue
                entries = row["QuestDependency"].split("|")
                self.assertEqual(len(entries), 1)
                self.assertTrue(entries[0].startswith("#"))
                self.assertEqual({self.after[int(t)]["MinLevel"] for t in entries[0][1:].split("/")}, {lower[-1]})

    def test_xp_and_coin_are_in_the_last_stage(self):
        expected = {"7": ("0|0|5500", "0|0|700"), "7si": ("0|0|0|0|5500", "0|0|0|0|700"),
                    "11": ("0|0|0|0|0|95000", "0|0|0|0|0|1100"), "11si": ("0|0|0|95000", "0|0|600|600"),
                    "15": ("0|0|0|180000", "0|0|0|1500"), "20": ("0|0|0|0|1230000", "0|0|0|0|2000"),
                    "25": ("0|0|0|5300000", "0|0|0|2500"), "30": ("0|0|21000000", "0|0|3000"),
                    "40": ("0|0|0|0|0|0|0|430000000", "0|0|0|0|0|0|0|4000"),
                    "43": ("0|0|0|0|0|0|0|1080000000", "0|0|0|0|0|0|0|0"),
                    "45": ("0|0|0|0|0|0|1560000000", "0|0|0|0|0|0|4500"), "48": ("0|0|2700000000", "0|0|4800")}
        for key, (xp, money) in expected.items():
            for qid in self.data["steps"][key]["ids"]:
                with self.subTest(key=key, quest=qid):
                    self.assertEqual((self.after[qid]["RewardXP"], self.after[qid]["RewardMoney"]), (xp, money))
        for qid in [i for i in self.data["steps"]["7closed"]["ids"] if i is not None]:
            self.assertEqual(self.after[qid]["RewardXP"], "0|0|3300")  # the closed versions keep upstream's values

    def test_one_necromancer_reward_per_version_of_11(self):
        self.assertEqual(self.after[20159]["FinalRewardItemTemplates"], "cq_alb_flayed_skin_necklace")
        self.assertEqual(self.after[20471]["FinalRewardItemTemplates"], "cq_alb_arawn_s_beads")

    def test_the_reaver_list_and_the_level_30_speech(self):
        reaver = self.after[20172]
        self.assertTrue(reaver["StepText"].endswith(
            "(Blood Encrusted Whip, Sap of Lost Will, Bloodletter, Flail of Fallen Graces)."))
        self.assertTrue(reaver["SourceText"].endswith(
            "[Blood Encrusted Whip], [Sap of Lost Will], [Bloodletter], [Flail of Fallen Graces]."))
        for qid in range(21489, 21495):
            with self.subTest(qid):
                text = self.after[qid]["SourceText"]
                self.assertIn("Let this reward be the start", text)
                self.assertIn("call upon the Guild of Shadows and thier most cunning <Class>!", text)
                self.assertNotIn("(", text)

    def test_nothing_else_in_the_quests_changes(self):
        touched = {"QuestDependency", "RewardXP", "RewardMoney", "FinalRewardItemTemplates", "StepText", "SourceText",
                   "LastTimeRowUpdated"}
        for qid, old in self.before.items():
            new = self.after[qid]
            for column, value in old.items():
                if column not in touched:
                    self.assertEqual(new[column], value, (qid, column))


def world_digest(conn):
    """Every row of the tables the fix touches, as one comparable value."""
    tables = ("DataQuest", "ItemTemplate", "Mob", "Quest", "CharacterXDataQuest", "Inventory")
    return {t: hash(tuple(conn.execute(f'SELECT * FROM "{t}" ORDER BY rowid').fetchall())) for t in tables}


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class EpicSafetyTests(unittest.TestCase):
    """Once per world, all or nothing, and never over a row upstream or the owner changed (spec 3.4)."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, self.db)
        self.conn = sqlite3.connect(self.db)

    def tearDown(self):
        self.conn.close()
        self.tmp.cleanup()

    def test_a_second_run_changes_nothing(self):
        self.assertEqual(len(epic_chains.apply(self.conn, NOW)), 1)
        self.conn.commit()
        before = world_digest(self.conn)
        self.assertEqual(epic_chains.apply(self.conn, NOW), [])
        self.assertEqual(world_digest(self.conn), before)

    def test_a_failing_step_changes_nothing(self):
        before = world_digest(self.conn)

        def boom(conn, now, data):
            raise RuntimeError("boom")

        with unittest.mock.patch.object(epic_chains, "STEPS", epic_chains.STEPS + (boom,)):
            self.assertEqual(epic_chains.apply(self.conn, NOW), [epic_chains.NOT_APPLIED.format("boom")])
        self.conn.commit()
        self.assertEqual(world_digest(self.conn), before)
        tables = {n for (n,) in self.conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        self.assertNotIn("fork_world_fixes", tables)

    def test_a_row_upstream_already_changed_is_left_alone(self):
        self.conn.execute("UPDATE DataQuest SET QuestDependency='Path of the Renegade' WHERE ID=21491")
        result = epic_chains.apply(self.conn, NOW)
        self.assertIn("Guild of Shadows 59 links", result[0])
        self.assertEqual(self.conn.execute("SELECT QuestDependency FROM DataQuest WHERE ID=21491").fetchone(),
                         ("Path of the Renegade",))

    def test_a_world_without_the_quest_tables_is_left_alone(self):
        empty = sqlite3.connect(":memory:")
        self.assertEqual(epic_chains.apply(empty, NOW), [])
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'`
Expected: `ModuleNotFoundError: No module named 'epic_chains'`.

- [ ] **Step 3: Write the data file**

Create `deploy/bin/epic_chains_data.json`:

```json
{
  "_about": "The Guild of Shadows chain for deploy/bin/epic_chains.py (spec docs/fork/specs/2026-10-09-epic-chains-design.md, sections 2 and 3.4). \"ids\": the quest of each class, in the order of \"classes\" (null: none). \"upstream\": the values upstream 0.35b ships; a row is changed only while it still has them. \"links\": the step's QuestDependency entries by step key (\"7/7closed/7si\": any one finished; \"!11si\": closed while it is active or finished). \"xp\" and \"money\" (copper) go in the last stage's entry; \"money_at\" adds coin at a stage. \"new\": rows the fix adds (step 8).",
  "classes": {"Infiltrator": 9, "Mercenary": 11, "Cabalist": 13, "Necromancer": 12, "Reaver": 19},
  "steps": {
    "7": {"ids": [21500, 21498, 21499, 20497, 21501], "upstream": {"QuestDependency": "", "RewardXP": "0|0|3300", "RewardMoney": "0|0|0"}, "links": ["!7si/7closed"], "xp": 5500, "money": 700},
    "7closed": {"ids": [21324, 21322, 21323, null, 21325], "upstream": {"QuestDependency": ""}},
    "7si": {"ids": [20478, 20476, 20477, 20480, 20479], "upstream": {"QuestDependency": "", "RewardXP": "0|0|0|0|6400", "RewardMoney": "0|0|0|0|700"}, "links": ["!7/7closed"], "xp": 5500, "money": 700},
    "11": {"ids": [20157, 20155, 20156, 20159, 20158], "upstream": {"QuestDependency": "", "RewardXP": "0|0|0|0|0|31920", "RewardMoney": "0|0|0|0|0|0"}, "links": ["7/7closed/7si", "!11si"], "xp": 95000, "money": 1100},
    "11si": {"ids": [20469, 20467, 20468, 20471, 20470], "upstream": {"QuestDependency": "", "RewardXP": "0|0|0|57960", "RewardMoney": "0|0|0|600"}, "links": ["7/7closed/7si", "!11"], "xp": 95000, "money": 600, "money_at": {"3": 600}},
    "15": {"ids": [20188, 20186, 20187, 20190, 20189], "upstream": {"QuestDependency": "Entry Into Tomorrow", "RewardXP": "0|0|0|151200", "RewardMoney": "0|0|0|0"}, "links": ["11/11si"], "xp": 180000, "money": 1500},
    "20": {"ids": [20455, 20453, 20454, 20457, 20456], "upstream": {"QuestDependency": "Rebellion Accepted", "RewardXP": "0|0|0|0|1033200", "RewardMoney": "0|0|0|0|0"}, "links": ["15"], "xp": 1230000, "money": 2000},
    "25": {"ids": [21306, 21304, 21305, 21308, 21307], "upstream": {"QuestDependency": "Path of the Renegade", "RewardXP": "0|0|0|1378000", "RewardMoney": "0|0|0|0"}, "links": ["20"], "xp": 5300000, "money": 2500},
    "30": {"ids": [21491, 21489, 21490, 21493, 21492], "upstream": {"QuestDependency": "Regal Nobility", "RewardXP": "0|0|60878593", "RewardMoney": "0|0|0"}, "links": ["25"], "xp": 21000000, "money": 3000},
    "40": {"ids": [20171, 20169, 20170, 20173, 20172], "upstream": {"QuestDependency": "Regal Nobility", "RewardXP": "0|0|0|52428800|0|52428800|0|52428800", "RewardMoney": "0|0|0|0|0|0|0|0"}, "links": ["30"], "xp": 430000000, "money": 4000},
    "43": {"ids": [20435, 20433, 20434, 20437, 20436], "upstream": {"QuestDependency": "Hidden Insurrection", "RewardXP": "0|0|0|0|0|0|0|1083932608", "RewardMoney": "0|0|0|0|0|0|0|0"}, "links": ["40"], "xp": 1080000000, "money": 0},
    "45": {"ids": [21297, 21295, 21296, 21299, 21298], "upstream": {"QuestDependency": "Lord of Deceit", "RewardXP": "0|0|0|0|0|1300000000|1300000000", "RewardMoney": "0|0|0|0|0|0|0"}, "links": ["43"], "xp": 1560000000, "money": 4500},
    "48": {"ids": [21482, 21480, 21481, 21484, 21483], "upstream": {"QuestDependency": "Lord of Deceit", "RewardXP": "0|0|2440223333", "RewardMoney": "0|0|0"}, "links": ["45"], "xp": 2700000000, "money": 4800},
    "50": {"ids": [990509, 990511, 990513, 990512, 990519], "new": true, "links": ["48"], "xp": 0, "money": 5000}
  },
  "rewards": [
    {"id": 20159, "upstream": "cq_alb_arawn_s_beads|cq_alb_flayed_skin_necklace", "value": "cq_alb_flayed_skin_necklace"},
    {"id": 20471, "upstream": "cq_alb_arawn_s_beads|cq_alb_flayed_skin_necklace", "value": "cq_alb_arawn_s_beads"}
  ],
  "texts": [
    {"ids": [20172], "column": "StepText", "replace": [["(Blood Encrusted Whip, Kiss of Death, Sap of Lost Will, Bloodletter, Flail of Fallen Graces, Blood Encrusted Flail).", "(Blood Encrusted Whip, Sap of Lost Will, Bloodletter, Flail of Fallen Graces)."]]},
    {"ids": [20172], "column": "SourceText", "replace": [["[Blood Encrusted Whip], [Kiss of Death], [Sap of Lost Will], [Bloodletter], [Flail of Fallen Graces], [Blood Encrusted Flail].", "[Blood Encrusted Whip], [Sap of Lost Will], [Bloodletter], [Flail of Fallen Graces]."]]},
    {"ids": [21489, 21490, 21491, 21492, 21493, 21494], "column": "SourceText", "replace": [["Let this (reward) be", "Let this reward be"], ["(prof name)", "the Guild of Shadows"], ["(class name)", "<Class>"]]}
  ]
}
```

- [ ] **Step 4: Write the fix**

Create `deploy/bin/epic_chains.py`:

```python
#!/usr/bin/env python3
"""The epic chains (sub-project 4) in the world data: upstream's classic epic quests, in order and complete.

Spec: docs/fork/specs/2026-10-09-epic-chains-design.md, section 3.4. world_fixes.py calls apply() inside its
transaction, after battlegrounds.apply(). The fix runs once per world: it records the marker epic-chains-v1 in the
fork table fork_world_fixes, and a world with the marker is left alone, so changes the owner makes later stay. Every
statement runs under one savepoint; if a step fails it is rolled back (no change, no marker, so the next start tries
again) and apply() returns only the "not applied" line, while world_fixes.py still commits its own fixes.

Each step changes a row only while it still holds upstream's 0.35b value, and inserts a row only where none with its
key exists, so a later upstream world that fixed something itself keeps its own fix, and the counts show only what
this fix did. The Guild of Shadows data (quest IDs, links, XP and coin, texts, items, the level-50 quests) is in
epic_chains_data.json, beside this file.
1. The Guild of Shadows links: QuestDependency of its 60 chain rows by quest ID (DataQuest's "#a/b": one of them is
   finished; "!#a/b": closed while one of them is active or finished).
2. Every other line: a classic quest's name dependency that is its own name, or that names quests at more than one
   level below it for its classes, is pinned to the IDs of those quests at the highest such level.
3. The other version of the Supply Run (Lady Aelawen's) is closed: it needs itself finished, so it is offered to no
   one, and a character already on it can finish it (DataQuest reads dependencies only when it offers a quest).
4. The chain's XP and coin, in the last stage's entry (the 11 SI also pays coin at stage 3).
5. Necromancer 11: one reward for each version.
6. Texts: the Reaver's level-40 list without the two weapons that don't exist; the level-30 speech's source tags.
"""
import datetime
import json
import os
import re

FIX_ID = "epic-chains-v1"
MARKER_TABLE = "fork_world_fixes"
SAVEPOINT = "epic_chains"
NEEDED_TABLES = ("DataQuest", "ItemTemplate", "Mob", "offline_classic165_removed_mobs", "Quest",
                 "CharacterXDataQuest", "DOLCharacters", "Inventory")
NOT_APPLIED = "Epic chains: not applied ({}); the quests stay as upstream ships them"
CLASSIC = "DOL.GS.Quests.ClassicQuestStep"
ARCHIVE = "offline_classic165_removed_mobs"
DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "epic_chains_data.json")
TEXT_COLUMNS = ("StepText", "SourceText", "TargetText", "Description", "FinishText")


def load_data(path=DATA_FILE):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def apply(conn, now=None, data=None):
    """Apply the epic chains once per world; returns the summary line, the "not applied" line, or nothing.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open, the
    savepoint is the transaction, and its release commits it."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if any(table.lower() not in tables for table in NEEDED_TABLES):
        return []
    if MARKER_TABLE in tables and conn.execute(f"SELECT 1 FROM {MARKER_TABLE} WHERE FixId=?", (FIX_ID,)).fetchone():
        return []
    now = now or _now()
    data = data if data is not None else load_data()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        conn.execute(f"CREATE TABLE IF NOT EXISTS {MARKER_TABLE} (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)")
        counts = {}
        for step in STEPS:
            counts.update(step(conn, now, data))
        conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (FIX_ID, now))
    except Exception as e:  # any failure: undo the whole fix, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    return [summary(counts)]


def summary(counts):
    """The start log's line. A count is what this fix changed (0 where upstream already had it)."""
    c = lambda key: counts.get(key, 0)  # noqa: E731
    return (f"Epic chains: Guild of Shadows {c('links')} links, {c('pay')} XP and coin, {c('closed')} Supply Runs "
            f"closed, {c('rewards')} rewards and {c('texts')} texts fixed; {c('pinned')} other links; "
            f"{c('items')} items added, {c('fixes')} item fixes; {c('level50')} level-50 quests, "
            f"Lord Elidyn's camp {c('camp')} restored; Shadows_50: {c('carried')} finished carried, "
            f"{c('removed')} removed, {c('vests')} epic vests recharged")


def _classes(allowed):
    """The class IDs of an AllowedClasses value ("9", "22|31", "1;6"); empty means every class."""
    return {int(part) for part in re.split(r"[|;,]", allowed or "") if part.strip().isdigit()}


def _shares(mine, theirs):
    return not mine or not theirs or bool(mine & theirs)


def _entry(steps, link, col):
    """A link ("7/7closed/7si", "!11si") as the class's QuestDependency entry ("#21500/21324/20478", "!#20469")."""
    closes = link.startswith("!")
    ids = [steps[key]["ids"][col] for key in link.lstrip("!").split("/")]
    return ("!#" if closes else "#") + "/".join(str(i) for i in ids if i is not None)


def _gos_links(conn, now, data):
    steps, changed = data["steps"], 0
    for step in steps.values():
        if step.get("new") or "links" not in step:
            continue
        for col, qid in enumerate(step["ids"]):
            if qid is None:
                continue
            value = "|".join(_entry(steps, link, col) for link in step["links"])
            changed += conn.execute(
                "UPDATE DataQuest SET QuestDependency=?, LastTimeRowUpdated=? WHERE ID=? AND IFNULL(QuestDependency,'')=?",
                (value, now, qid, step["upstream"]["QuestDependency"])).rowcount
    return {"links": changed}


def _pin_names(conn, now, data):
    gos = {qid for step in data["steps"].values() for qid in step["ids"] if qid is not None}
    quests = [r for r in conn.execute("SELECT ID, Name, MinLevel, AllowedClasses, IFNULL(QuestDependency,''), ClassType "
                                      "FROM DataQuest") if CLASSIC in (r[5] or "")]
    by_name = {}
    for quest in quests:
        by_name.setdefault(quest[1].lower(), []).append(quest)
    changed = 0
    for qid, name, level, allowed, dependency, _class_type in quests:
        entries = [e for e in dependency.split("|") if e]
        if qid in gos or not entries or any(e.strip().startswith(("#", "!#")) for e in entries):
            continue
        mine, pinned, any_pinned = _classes(allowed), [], False
        for entry in entries:
            before = [q for q in by_name.get(entry.lower(), [])
                      if q[0] != qid and q[2] < level and _shares(mine, _classes(q[3]))]
            levels = {q[2] for q in before}
            if before and (entry.lower() == name.lower() or len(levels) > 1):
                top = max(levels)
                pinned.append("#" + "/".join(str(q[0]) for q in sorted(before) if q[2] == top))
                any_pinned = True
            else:
                pinned.append(entry)
        if any_pinned:
            changed += conn.execute(
                "UPDATE DataQuest SET QuestDependency=?, LastTimeRowUpdated=? WHERE ID=? AND IFNULL(QuestDependency,'')=?",
                ("|".join(pinned), now, qid, dependency)).rowcount
    return {"pinned": changed}


def _close_supply_runs(conn, now, data):
    closed = data["steps"]["7closed"]
    changed = 0
    for qid in closed["ids"]:
        if qid is not None:
            changed += conn.execute(
                "UPDATE DataQuest SET QuestDependency=?, LastTimeRowUpdated=? WHERE ID=? AND IFNULL(QuestDependency,'')=?",
                (f"#{qid}", now, qid, closed["upstream"]["QuestDependency"])).rowcount
    return {"closed": changed}


def pay_lists(stages, step):
    """RewardXP and RewardMoney of a step with <stages> stages: its amounts in the last stage's entry, plus any
    "money_at" coin at its stage."""
    xp, money = [0] * stages, [0] * stages
    xp[-1], money[-1] = step["xp"], step["money"]
    for stage, amount in step.get("money_at", {}).items():
        money[int(stage) - 1] = amount
    return "|".join(map(str, xp)), "|".join(map(str, money))


def _pay(conn, now, data):
    changed = 0
    for step in data["steps"].values():
        if step.get("new") or "xp" not in step:
            continue
        upstream = step["upstream"]
        xp, money = pay_lists(len(upstream["RewardXP"].split("|")), step)
        for qid in step["ids"]:
            if qid is not None:
                changed += conn.execute(
                    "UPDATE DataQuest SET RewardXP=?, RewardMoney=?, LastTimeRowUpdated=? "
                    "WHERE ID=? AND IFNULL(RewardXP,'')=? AND IFNULL(RewardMoney,'')=?",
                    (xp, money, now, qid, upstream["RewardXP"], upstream["RewardMoney"])).rowcount
    return {"pay": changed}


def _rewards(conn, now, data):
    changed = 0
    for fix in data["rewards"]:
        changed += conn.execute(
            "UPDATE DataQuest SET FinalRewardItemTemplates=?, LastTimeRowUpdated=? WHERE ID=? "
            "AND IFNULL(FinalRewardItemTemplates,'')=?", (fix["value"], now, fix["id"], fix["upstream"])).rowcount
    return {"rewards": changed}


def _texts(conn, now, data):
    changed = set()
    for fix in data["texts"]:
        column = fix["column"]
        if column not in TEXT_COLUMNS:
            raise ValueError(f"unknown DataQuest text column {column}")
        for qid in fix["ids"]:
            row = conn.execute(f"SELECT {column} FROM DataQuest WHERE ID=?", (qid,)).fetchone()
            if row is None or row[0] is None or not all(old in row[0] for old, _new in fix["replace"]):
                continue
            text = row[0]
            for old, new in fix["replace"]:
                text = text.replace(old, new)
            conn.execute(f"UPDATE DataQuest SET {column}=?, LastTimeRowUpdated=? WHERE ID=?", (text, now, qid))
            changed.add(qid)
    return {"texts": len(changed)}


STEPS = (_gos_links, _pin_names, _close_supply_runs, _pay, _rewards, _texts)


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
```

- [ ] **Step 5: Run the tests**

Run: `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py' -v`
Expected: all pass. If `test_every_other_line_is_pinned_to_its_step_before` fails on the counts, print the counts
dict and compare it with `EXPECTED_PINS` line by line before changing anything: the expected numbers come from the
spec's table (2.5), checked against the clean world.

Run the whole suite: `HDC_TEST_WORLD=... python3 -m unittest discover -s deploy/tests -t deploy` — OK.

- [ ] **Step 6: Commit**

```bash
git add deploy/bin/epic_chains.py deploy/bin/epic_chains_data.json deploy/tests/test_epic_chains.py
git commit -m "feat(deploy): world fix epic-chains-v1: the chains' order, XP, coin and texts

The Guild of Shadows chain by quest ID (strict order, the Shrouded Isles
route, one version of 7 and 11 closing the other), Lady Aelawen's Supply Run
closed (it needs itself finished), every other line's steps pinned to the
step before (87 rows), the chain's XP and coin in the last stage, one
Necromancer reward per version of 11, and the Reaver's level-40 list and the
level-30 speech fixed. Not wired into world_fixes.py yet.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 6: The missing rewards and level-40 weapons, class locks, and fixes to the existing ones

Step 7 of the world fix (spec 4). The item values come from the archived Allakhazam item pages (every one of the 41
was found in the Web Archive), checked field by field against the spec's tables and the world's rows, and
cross-checked against the live Camelot Herald item database for the 13 items it still has (models, bonus levels, the
staves' charges and 90-second reuse); the research notes are summarised below.

**Files:**
- Modify: `deploy/bin/epic_chains_data.json` (keys `items`, `item_fixes`, `vests`)
- Modify: `deploy/bin/epic_chains.py` (`_items`; `STEPS`; `import uuid`)
- Modify: `deploy/tests/test_epic_chains.py`

**Interfaces:**
- Consumes: Task 5's `apply`, `STEPS`, `summary` (keys `items`, `armour`).
- Produces: data key `vests` (`ids`: the five `<Class>EpicVest` ids; `SpellID`; `Charges`) that Task 7 uses.

Notes from the research, for the reviewer:
- Row conventions as upstream's rewards: Realm 1, `AllowedClasses` the item's class (`;`-separated: the classes on
  the item page or the Herald, with the classes whose quest gives it; Spark of Midnight and Death's Touch "9;11"), droppable, not tradable, price 0, quality 100, condition and durability 50000, `MaxCount` 1, `PackSize` 1,
  `PackageID` "HearthDAoC", `Description` "Classic quest reward (<quest>)". `LevelRequirement` 40 on the weapons.
- Power is `MaxMana` (9) as flat points; focus bonuses are `Focus_*` (123 Spirit, 129 Body, 130 Matter, 139
  Painworking, 140 Deathsight, 141 Death Servant), as the world's other staves.
- Left-hand weapons (Arcing Bludgeoner, Spark of Midnight): `Item_Type` 11, `Hand` 2, as every left-hand weapon in
  the world.
- Procs and charges: spells 32117 (cold), 32118 (energy), 32119 (spirit), the level-40 77-damage family upstream's own
  Midgard level-40 weapons use. Procs: `ProcSpellID`, `ProcChance` 0 (the server's default chance). Charges:
  `SpellID`, `Charges` 10, `MaxCharges` 10.
- `item_fixes`: the nine level-50 armour fixes, then one entry per upstream reward (27): its class lock, plus, for 24
  of them, the fixes of spec 4.5 (bonuses, weapon and armour types, Albion models, procs, the Herald's models, bonus
  levels). Flail of Fallen Graces takes model 861 (the world's "flail" rows) over the more common 857 ("chains");
  Boots of the Fallen weight 3.2 lbs and 10% bonus, and the whip level requirement 40, from the in-game screenshots.
- From the live Herald: Vest of the Infamous Blade model 31, both Threaded Cloaks 443, the six caster staves 568 with
  `CanUseEvery` 90; bonus levels where the Allakhazam page shows none (Sleeves of Might 3, Light Chain Tunic 14, three
  staves 25). Other models follow upstream's matching rows (the one-handed weapons aren't on live any more).
- Rulings applied: Staff of Earth Channeling Hits 100 and 10 charges of energy damage; Staff of Spirit Consumption
  Spirit 43, Body 33, Matter 33, Hits 100 and 10 charges of spirit damage (spec 4.3); Staff of Cursed Bondage 10
  charges by analogy with its two siblings; "Shadowbinder's Mantle" keeps upstream's id `cq_alb_snowbinder_s_mantle`.
- The vests' charges: spell 31131 (75 AF, self, 10 minutes), 3 charges. On this server 31131 counts as a self-buff
  charge item (`GamePlayer.SelfBuffChargeIDs`): no reuse delay, at most `max_charge_items` such buffs at once, and
  the buff ends when the vest comes off.

- [ ] **Step 1: Write the failing tests**

In `deploy/tests/test_epic_chains.py` set `SUMMARY` to

```python
SUMMARY = ("Epic chains: Guild of Shadows 60 links, 60 XP and coin, 4 Supply Runs closed, 2 rewards and 7 texts fixed; "
           "87 other links; 41 items added, 36 item fixes; 0 level-50 quests, Lord Elidyn's camp 0 restored; "
           "Shadows_50: 0 finished carried, 0 removed, 0 epic vests recharged")
```

add after `FINALES`:

```python
# Upstream's level-40 lists (classic-quests.json, quests 20169-20173), less the two the fix drops for the Reaver.
WEAPON_CHOICES = {
    "Infiltrator": ["cq_alb_crackling_impaler", "cq_alb_death_s_touch", "cq_alb_death_dancer", "cq_alb_spark_of_midnight"],
    "Mercenary": ["cq_alb_crackling_impaler", "cq_alb_death_s_touch", "cq_alb_glitter", "cq_alb_spark", "cq_alb_dazzle",
                  "cq_alb_arcing_bludgeoner"],
    "Cabalist": ["cq_alb_staff_of_eternal_lifeforce", "cq_alb_staff_of_earth_channeling", "cq_alb_staff_of_spirit_consumption"],
    "Necromancer": ["cq_alb_staff_of_cursed_bondage", "cq_alb_staff_of_clouded_vision", "cq_alb_staff_of_ceaseless_agony"],
    "Reaver": ["cq_alb_blood_encrusted_whip", "cq_alb_sap_of_lost_will", "cq_alb_bloodletter", "cq_alb_flail_of_fallen_graces"],
}
```

add to `EpicWorldTests`:

```python
    def test_every_reward_and_weapon_choice_exists(self):
        def exists(item):
            return self.conn.execute("SELECT 1 FROM ItemTemplate WHERE Id_nb=?", (item,)).fetchone() is not None

        for key, step in self.data["steps"].items():
            if step.get("new"):
                continue
            for qid in step["ids"]:
                if qid is None:
                    continue
                for item in filter(None, (self.after[qid]["FinalRewardItemTemplates"] or "").split("|")):
                    with self.subTest(step=key, quest=qid, item=item):
                        self.assertTrue(exists(item))
        for name, items in WEAPON_CHOICES.items():
            for item in items:
                with self.subTest(name=name, item=item):
                    self.assertTrue(exists(item))

    def test_the_items_added_are_the_data_rows(self):
        import uuid
        for item in self.data["items"]:
            with self.subTest(item["Id_nb"]):
                row = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb=?", (item["Id_nb"],))[0]
                self.assertEqual({k: row[k] for k in item}, item)
                self.assertEqual(row["ItemTemplate_ID"], str(uuid.uuid5(uuid.NAMESPACE_URL, "hearthdaoc:item:" + item["Id_nb"])))
                self.assertEqual(row["LastTimeRowUpdated"], NOW)

    def test_the_item_fixes(self):
        # Every fix applied as the data says (the level-50 armour, upstream's broken rewards, the class locks).
        for fix in self.data["item_fixes"]:
            with self.subTest(fix["Id_nb"]):
                row = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb=?", (fix["Id_nb"],))[0]
                self.assertEqual({k: str(row[k]) for k in fix["set"]}, {k: str(v) for k, v in fix["set"].items()})
        vest = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb='MercenaryEpicVest'")[0]
        self.assertEqual((vest["Name"], vest["AllowedClasses"], vest["SpellID"], vest["Charges"]),
                         ("Hauberk of the Shadowy Embers", "11", 31131, 3))
        choker = rows(self.conn, "SELECT * FROM ItemTemplate WHERE Id_nb='cq_alb_choker_of_dark_deeds'")[0]
        self.assertEqual(sorted((choker[f"Bonus{i}Type"], choker[f"Bonus{i}"]) for i in range(1, 5)),
                         [(5, 4), (9, 3), (11, 1), (26, 2)])  # Int 4, Power 3, Body 1%, Death Servant +2

    def test_every_reward_is_locked_to_the_class_it_is_for(self):
        classes = self.data["classes"]
        given = {}
        for key, step in self.data["steps"].items():
            if step.get("new"):
                continue
            for qid, class_id in zip(step["ids"], self.data["classes"].values()):
                if qid is not None:
                    for item in filter(None, (self.after[qid]["FinalRewardItemTemplates"] or "").split("|")):
                        given.setdefault(item, set()).add(class_id)
        for name, items in WEAPON_CHOICES.items():
            for item in items:
                given.setdefault(item, set()).add(classes[name])
        self.assertEqual(len(given), 68)
        for item, class_ids in given.items():
            with self.subTest(item):
                allowed = self.conn.execute("SELECT AllowedClasses FROM ItemTemplate WHERE Id_nb=?", (item,)).fetchone()[0]
                self.assertNotIn(allowed, ("", "0"))
                self.assertTrue(class_ids <= {int(c) for c in allowed.split(";")}, allowed)
```

and to `EpicSafetyTests`:

```python
    def test_items_that_exist_are_left_alone(self):
        # Upstream added one meanwhile (or the owner edited it), and the owner set a vest's charges.
        self.conn.execute("INSERT INTO ItemTemplate (Id_nb, Name) VALUES ('cq_alb_ring_of_shades', 'Their Ring')")
        self.conn.execute("UPDATE ItemTemplate SET SpellID=999, Charges=1, MaxCharges=1 WHERE Id_nb='ReaverEpicVest'")
        result = epic_chains.apply(self.conn, NOW)
        self.assertIn("40 items added, 35 item fixes", result[0])
        self.assertEqual(self.conn.execute("SELECT Name FROM ItemTemplate WHERE Id_nb='cq_alb_ring_of_shades'").fetchone(),
                         ("Their Ring",))
        self.assertEqual(self.conn.execute("SELECT SpellID, Charges FROM ItemTemplate WHERE Id_nb='ReaverEpicVest'").fetchone(),
                         (999, 1))
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'`
Expected: `test_the_summary_line` fails (0 items added), the three new world tests fail with `KeyError: 'items'`
or missing rows, and `test_items_that_exist_are_left_alone` fails.

- [ ] **Step 3: Add the data**

In `deploy/bin/epic_chains_data.json`, add after the `texts` list (mind the commas). The `items` list, exactly:

```json
  "items": [
    {"Id_nb": "cq_alb_sleeves_of_might", "Name": "Sleeves of Might", "Level": 7, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 18, "SPD_ABS": 19, "Hand": 0, "Type_Damage": 0, "Object_Type": 34, "Item_Type": 28, "Color": 0, "Weight": 20, "Model": 83, "Bonus": 1, "Bonus1": 4, "Bonus2": 6, "Bonus3": 0, "Bonus4": 0, "Bonus1Type": 1, "Bonus2Type": 10, "Bonus3Type": 0, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 3, "LevelRequirement": 0, "Description": "Classic quest reward (Traveler's Way -- Supply Run)"},
    {"Id_nb": "cq_alb_silvered_cap_of_the_intuit", "Name": "Silvered Cap of the Intuit", "Level": 7, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 9, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 32, "Item_Type": 21, "Color": 0, "Weight": 10, "Model": 822, "Bonus": 1, "Bonus1": 1, "Bonus2": 3, "Bonus3": 6, "Bonus4": 1, "Bonus1Type": 21, "Bonus2Type": 2, "Bonus3Type": 5, "Bonus4Type": 9, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Traveler's Way -- Supply Run)"},
    {"Id_nb": "cq_alb_twisted_bone_bracelet", "Name": "Twisted Bone Bracelet", "Level": 7, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 33, "Color": 0, "Weight": 5, "Model": 598, "Bonus": 5, "Bonus1": 6, "Bonus2": 3, "Bonus3": 1, "Bonus4": 1, "Bonus1Type": 1, "Bonus2Type": 3, "Bonus3Type": 33, "Bonus4Type": 46, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 5, "LevelRequirement": 0, "Description": "Classic quest reward (Traveler's Way -- Supply Run)"},
    {"Id_nb": "cq_alb_jewel_of_grace", "Name": "Jewel of Grace", "Level": 7, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 24, "Color": 0, "Weight": 0, "Model": 118, "Bonus": 1, "Bonus1": 3, "Bonus2": 3, "Bonus3": 1, "Bonus4": 0, "Bonus1Type": 1, "Bonus2Type": 2, "Bonus3Type": 4, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Strange Beings)"},
    {"Id_nb": "cq_alb_necklace_of_greatness", "Name": "Necklace of Greatness", "Level": 7, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 29, "Color": 0, "Weight": 10, "Model": 101, "Bonus": 5, "Bonus1": 6, "Bonus2": 1, "Bonus3": 8, "Bonus4": 0, "Bonus1Type": 5, "Bonus2Type": 9, "Bonus3Type": 10, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 5, "LevelRequirement": 0, "Description": "Classic quest reward (Strange Beings)"},
    {"Id_nb": "cq_alb_temple_ring", "Name": "Temple Ring", "Level": 7, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 35, "Color": 0, "Weight": 5, "Model": 103, "Bonus": 1, "Bonus1": 3, "Bonus2": 4, "Bonus3": 3, "Bonus4": 1, "Bonus1Type": 1, "Bonus2Type": 3, "Bonus3Type": 6, "Bonus4Type": 33, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 5, "LevelRequirement": 0, "Description": "Classic quest reward (Strange Beings)"},
    {"Id_nb": "cq_alb_might", "Name": "Might", "Level": 11, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 33, "Color": 0, "Weight": 7, "Model": 598, "Bonus": 1, "Bonus1": 7, "Bonus2": 4, "Bonus3": 1, "Bonus4": 12, "Bonus1Type": 1, "Bonus2Type": 2, "Bonus3Type": 11, "Bonus4Type": 10, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 7, "LevelRequirement": 0, "Description": "Classic quest reward (Entry Into Tomorrow)"},
    {"Id_nb": "cq_alb_quickened_absorption_stone", "Name": "Quickened Absorption Stone", "Level": 11, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 24, "Color": 0, "Weight": 3, "Model": 118, "Bonus": 5, "Bonus1": 4, "Bonus2": 4, "Bonus3": 7, "Bonus4": 1, "Bonus1Type": 1, "Bonus2Type": 2, "Bonus3Type": 4, "Bonus4Type": 31, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 8, "LevelRequirement": 0, "Description": "Classic quest reward (Shades and Shadows)"},
    {"Id_nb": "cq_alb_lucky_pebble", "Name": "Lucky Pebble", "Level": 11, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 24, "Color": 0, "Weight": 2, "Model": 118, "Bonus": 5, "Bonus1": 6, "Bonus2": 6, "Bonus3": 6, "Bonus4": 1, "Bonus1Type": 1, "Bonus2Type": 2, "Bonus3Type": 4, "Bonus4Type": 18, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Shades and Shadows)"},
    {"Id_nb": "cq_alb_minding_ring", "Name": "Minding Ring", "Level": 11, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 35, "Color": 0, "Weight": 3, "Model": 103, "Bonus": 5, "Bonus1": 4, "Bonus2": 7, "Bonus3": 1, "Bonus4": 12, "Bonus1Type": 2, "Bonus2Type": 5, "Bonus3Type": 18, "Bonus4Type": 10, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Shades and Shadows)"},
    {"Id_nb": "cq_alb_reaver_s_stone", "Name": "Reaver's Stone", "Level": 11, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 24, "Color": 0, "Weight": 3, "Model": 118, "Bonus": 5, "Bonus1": 1, "Bonus2": 6, "Bonus3": 16, "Bonus4": 3, "Bonus1Type": 43, "Bonus2Type": 3, "Bonus3Type": 10, "Bonus4Type": 9, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 9, "LevelRequirement": 0, "Description": "Classic quest reward (Shades and Shadows)"},
    {"Id_nb": "cq_alb_sleeves_of_despair", "Name": "Sleeves of Despair", "Level": 15, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 34, "SPD_ABS": 27, "Hand": 0, "Type_Damage": 0, "Object_Type": 35, "Item_Type": 28, "Color": 0, "Weight": 50, "Model": 43, "Bonus": 10, "Bonus1": 7, "Bonus2": 7, "Bonus3": 7, "Bonus4": 1, "Bonus1Type": 1, "Bonus2Type": 4, "Bonus3Type": 6, "Bonus4Type": 33, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 10, "LevelRequirement": 0, "Description": "Classic quest reward (Rebellion Accepted)"},
    {"Id_nb": "cq_alb_soft_doeskin_boots", "Name": "Soft Doeskin Boots", "Level": 20, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 44, "SPD_ABS": 10, "Hand": 0, "Type_Damage": 0, "Object_Type": 33, "Item_Type": 23, "Color": 0, "Weight": 16, "Model": 138, "Bonus": 10, "Bonus1": 3, "Bonus2": 6, "Bonus3": 3, "Bonus4": 0, "Bonus1Type": 49, "Bonus2Type": 2, "Bonus3Type": 4, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 13, "LevelRequirement": 0, "Description": "Classic quest reward (Path of the Renegade)"},
    {"Id_nb": "cq_alb_heavy_pull_short_bow", "Name": "Heavy Pull Short Bow", "Level": 20, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 75, "SPD_ABS": 41, "Hand": 1, "Type_Damage": 3, "Object_Type": 5, "Item_Type": 13, "Color": 0, "Weight": 23, "Model": 569, "Bonus": 10, "Bonus1": 13, "Bonus2": 12, "Bonus3": 0, "Bonus4": 0, "Bonus1Type": 2, "Bonus2Type": 4, "Bonus3Type": 0, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 13, "LevelRequirement": 0, "Description": "Classic quest reward (Path of the Renegade)"},
    {"Id_nb": "cq_alb_staff_of_tainted_rage", "Name": "Staff of Tainted Rage", "Level": 20, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 78, "SPD_ABS": 44, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 30, "Model": 19, "Bonus": 10, "Bonus1": 18, "Bonus2": 18, "Bonus3": 22, "Bonus4": 8, "Bonus1Type": 140, "Bonus2Type": 139, "Bonus3Type": 141, "Bonus4Type": 9, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Path of the Renegade)"},
    {"Id_nb": "cq_alb_vest_of_the_infamous_blade", "Name": "Vest of the Infamous Blade", "Level": 25, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 54, "SPD_ABS": 10, "Hand": 0, "Type_Damage": 0, "Object_Type": 33, "Item_Type": 25, "Color": 0, "Weight": 30, "Model": 31, "Bonus": 15, "Bonus1": 4, "Bonus2": 4, "Bonus3": 4, "Bonus4": 0, "Bonus1Type": 28, "Bonus2Type": 1, "Bonus3Type": 4, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 18, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_light_chain_tunic", "Name": "Light Chain Tunic", "Level": 25, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 54, "SPD_ABS": 27, "Hand": 0, "Type_Damage": 0, "Object_Type": 35, "Item_Type": 25, "Color": 0, "Weight": 30, "Model": 41, "Bonus": 15, "Bonus1": 3, "Bonus2": 9, "Bonus3": 2, "Bonus4": 12, "Bonus1Type": 40, "Bonus2Type": 4, "Bonus3Type": 17, "Bonus4Type": 10, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 14, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_spirit_threaded_cloak", "Name": "Spirit Threaded Cloak", "Level": 25, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 26, "Color": 0, "Weight": 1, "Model": 443, "Bonus": 0, "Bonus1": 3, "Bonus2": 9, "Bonus3": 4, "Bonus4": 0, "Bonus1Type": 47, "Bonus2Type": 2, "Bonus3Type": 9, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 17, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_pain_threaded_cloak", "Name": "Pain Threaded Cloak", "Level": 25, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 26, "Color": 5, "Weight": 15, "Model": 443, "Bonus": 10, "Bonus1": 9, "Bonus2": 3, "Bonus3": 4, "Bonus4": 0, "Bonus1Type": 2, "Bonus2Type": 39, "Bonus3Type": 9, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 17, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_tunic_of_dark_suffering", "Name": "Tunic of Dark Suffering", "Level": 25, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 54, "SPD_ABS": 27, "Hand": 0, "Type_Damage": 0, "Object_Type": 35, "Item_Type": 25, "Color": 2, "Weight": 30, "Model": 41, "Bonus": 15, "Bonus1": 4, "Bonus2": 4, "Bonus3": 4, "Bonus4": 0, "Bonus1Type": 1, "Bonus2Type": 6, "Bonus3Type": 33, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_snowbinder_s_mantle", "Name": "Shadowbinder's Mantle", "Level": 30, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 26, "Color": 0, "Weight": 5, "Model": 57, "Bonus": 15, "Bonus1": 3, "Bonus2": 13, "Bonus3": 13, "Bonus4": 0, "Bonus1Type": 49, "Bonus2Type": 1, "Bonus3Type": 2, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 19, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_gauntlets_of_blinding_speed", "Name": "Gauntlets of Blinding Speed", "Level": 30, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 64, "SPD_ABS": 27, "Hand": 0, "Type_Damage": 0, "Object_Type": 35, "Item_Type": 22, "Color": 0, "Weight": 32, "Model": 44, "Bonus": 15, "Bonus1": 4, "Bonus2": 6, "Bonus3": 6, "Bonus4": 0, "Bonus1Type": 28, "Bonus2Type": 1, "Bonus3Type": 4, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 19, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_visage_of_death", "Name": "Visage of Death", "Level": 30, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 105, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 45, "Model": 19, "Bonus": 15, "Bonus1": 28, "Bonus2": 26, "Bonus3": 26, "Bonus4": 30, "Bonus1Type": 5, "Bonus2Type": 130, "Bonus3Type": 129, "Bonus4Type": 123, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 20, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_charred_staff_of_blight", "Name": "Charred Staff of Blight", "Level": 30, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 108, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 20, "Model": 19, "Bonus": 20, "Bonus1": 28, "Bonus2": 26, "Bonus3": 26, "Bonus4": 30, "Bonus1Type": 5, "Bonus2Type": 140, "Bonus3Type": 139, "Bonus4Type": 141, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 20, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_mantle_of_shadow", "Name": "Mantle of Shadow", "Level": 30, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 26, "Color": 43, "Weight": 20, "Model": 96, "Bonus": 15, "Bonus1": 3, "Bonus2": 9, "Bonus3": 9, "Bonus4": 4, "Bonus1Type": 40, "Bonus2Type": 3, "Bonus3Type": 6, "Bonus4Type": 19, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 19, "LevelRequirement": 0, "Description": "Classic quest reward (Regal Nobility)"},
    {"Id_nb": "cq_alb_ring_of_shades", "Name": "Ring of Shades", "Level": 43, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 35, "Color": 0, "Weight": 2, "Model": 103, "Bonus": 20, "Bonus1": 3, "Bonus2": 18, "Bonus3": 15, "Bonus4": 3, "Bonus1Type": 23, "Bonus2Type": 1, "Bonus3Type": 2, "Bonus4Type": 31, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 0, "Description": "Classic quest reward (Lord of Deceit)"},
    {"Id_nb": "cq_alb_ring_of_forbidden_rites", "Name": "Ring of Forbidden Rites", "Level": 43, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 35, "Color": 0, "Weight": 2, "Model": 103, "Bonus": 35, "Bonus1": 18, "Bonus2": 3, "Bonus3": 3, "Bonus4": 3, "Bonus1Type": 5, "Bonus2Type": 27, "Bonus3Type": 39, "Bonus4Type": 26, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 27, "LevelRequirement": 0, "Description": "Classic quest reward (Lord of Deceit)"},
    {"Id_nb": "cq_alb_jewelled_skull_ring", "Name": "Jewelled Skull Ring", "Level": 43, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 35, "Color": 0, "Weight": 2, "Model": 103, "Bonus": 35, "Bonus1": 18, "Bonus2": 15, "Bonus3": 3, "Bonus4": 3, "Bonus1Type": 1, "Bonus2Type": 3, "Bonus3Type": 33, "Bonus4Type": 46, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 27, "LevelRequirement": 0, "Description": "Classic quest reward (Lord of Deceit)"},
    {"Id_nb": "cq_alb_ignuixs_portable_shadow", "Name": "Ignuixs' Portable Shadow", "Level": 45, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 26, "Color": 0, "Weight": 20, "Model": 96, "Bonus": 35, "Bonus1": 3, "Bonus2": 18, "Bonus3": 18, "Bonus4": 6, "Bonus1Type": 49, "Bonus2Type": 3, "Bonus3Type": 4, "Bonus4Type": 12, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9", "PackageID": "HearthDAoC", "BonusLevel": 28, "LevelRequirement": 0, "Description": "Classic quest reward (Lord of Deceit)"},
    {"Id_nb": "cq_alb_warm_construct_cloak", "Name": "Warm Construct Cloak", "Level": 45, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 0, "SPD_ABS": 0, "Hand": 0, "Type_Damage": 0, "Object_Type": 41, "Item_Type": 26, "Color": 0, "Weight": 20, "Model": 57, "Bonus": 35, "Bonus1": 7, "Bonus2": 7, "Bonus3": 6, "Bonus4": 0, "Bonus1Type": 2, "Bonus2Type": 5, "Bonus3Type": 9, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 17, "LevelRequirement": 0, "Description": "Classic quest reward (Lord of Deceit)"},
    {"Id_nb": "cq_alb_spark", "Name": "Spark", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 140, "SPD_ABS": 36, "Hand": 0, "Type_Damage": 3, "Object_Type": 4, "Item_Type": 10, "Color": 0, "Weight": 20, "Model": 30, "Bonus": 30, "Bonus1": 4, "Bonus2": 4, "Bonus3": 7, "Bonus4": 0, "Bonus1Type": 28, "Bonus2Type": 50, "Bonus3Type": 2, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 32118, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)"},
    {"Id_nb": "cq_alb_arcing_bludgeoner", "Name": "Arcing Bludgeoner", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 27, "Hand": 2, "Type_Damage": 1, "Object_Type": 2, "Item_Type": 11, "Color": 0, "Weight": 18, "Model": 15, "Bonus": 30, "Bonus1": 2, "Bonus2": 2, "Bonus3": 2, "Bonus4": 9, "Bonus1Type": 25, "Bonus2Type": 28, "Bonus3Type": 40, "Bonus4Type": 4, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 32118, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "11", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)"},
    {"Id_nb": "cq_alb_spark_of_midnight", "Name": "Spark of Midnight", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 27, "Hand": 2, "Type_Damage": 2, "Object_Type": 3, "Item_Type": 11, "Color": 0, "Weight": 18, "Model": 1, "Bonus": 30, "Bonus1": 3, "Bonus2": 3, "Bonus3": 3, "Bonus4": 15, "Bonus1Type": 44, "Bonus2Type": 28, "Bonus3Type": 40, "Bonus4Type": 4, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 32118, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "9;11", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)"},
    {"Id_nb": "cq_alb_staff_of_eternal_lifeforce", "Name": "Staff of Eternal Lifeforce", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 30, "Model": 568, "Bonus": 30, "Bonus1": 100, "Bonus2": 43, "Bonus3": 33, "Bonus4": 33, "Bonus1Type": 10, "Bonus2Type": 129, "Bonus3Type": 130, "Bonus4Type": 123, "SpellID": 32117, "Charges": 10, "MaxCharges": 10, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)", "CanUseEvery": 90},
    {"Id_nb": "cq_alb_staff_of_earth_channeling", "Name": "Staff of Earth Channeling", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 30, "Model": 568, "Bonus": 20, "Bonus1": 100, "Bonus2": 43, "Bonus3": 33, "Bonus4": 33, "Bonus1Type": 10, "Bonus2Type": 130, "Bonus3Type": 129, "Bonus4Type": 123, "SpellID": 32118, "Charges": 10, "MaxCharges": 10, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)", "CanUseEvery": 90},
    {"Id_nb": "cq_alb_staff_of_spirit_consumption", "Name": "Staff of Spirit Consumption", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 30, "Model": 568, "Bonus": 20, "Bonus1": 100, "Bonus2": 43, "Bonus3": 33, "Bonus4": 33, "Bonus1Type": 10, "Bonus2Type": 123, "Bonus3Type": 129, "Bonus4Type": 130, "SpellID": 32119, "Charges": 10, "MaxCharges": 10, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "13", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)", "CanUseEvery": 90},
    {"Id_nb": "cq_alb_staff_of_cursed_bondage", "Name": "Staff of Cursed Bondage", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 20, "Model": 568, "Bonus": 30, "Bonus1": 100, "Bonus2": 43, "Bonus3": 33, "Bonus4": 33, "Bonus1Type": 10, "Bonus2Type": 141, "Bonus3Type": 140, "Bonus4Type": 139, "SpellID": 32118, "Charges": 10, "MaxCharges": 10, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)", "CanUseEvery": 90},
    {"Id_nb": "cq_alb_staff_of_clouded_vision", "Name": "Staff of Clouded Vision", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 20, "Model": 568, "Bonus": 30, "Bonus1": 100, "Bonus2": 43, "Bonus3": 33, "Bonus4": 33, "Bonus1Type": 10, "Bonus2Type": 140, "Bonus3Type": 141, "Bonus4Type": 139, "SpellID": 32119, "Charges": 10, "MaxCharges": 10, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)", "CanUseEvery": 90},
    {"Id_nb": "cq_alb_staff_of_ceaseless_agony", "Name": "Staff of Ceaseless Agony", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 40, "Hand": 1, "Type_Damage": 1, "Object_Type": 8, "Item_Type": 12, "Color": 0, "Weight": 20, "Model": 568, "Bonus": 30, "Bonus1": 100, "Bonus2": 43, "Bonus3": 33, "Bonus4": 33, "Bonus1Type": 10, "Bonus2Type": 139, "Bonus3Type": 140, "Bonus4Type": 141, "SpellID": 32117, "Charges": 10, "MaxCharges": 10, "ProcChance": 0, "ProcSpellID": 0, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "12", "PackageID": "HearthDAoC", "BonusLevel": 25, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)", "CanUseEvery": 90},
    {"Id_nb": "cq_alb_sap_of_lost_will", "Name": "Sap of Lost Will", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 34, "Hand": 0, "Type_Damage": 1, "Object_Type": 2, "Item_Type": 10, "Color": 0, "Weight": 32, "Model": 15, "Bonus": 30, "Bonus1": 5, "Bonus2": 4, "Bonus3": 0, "Bonus4": 0, "Bonus1Type": 25, "Bonus2Type": 40, "Bonus3Type": 0, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 32118, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)"},
    {"Id_nb": "cq_alb_bloodletter", "Name": "Bloodletter", "Level": 40, "Durability": 50000, "MaxDurability": 50000, "Condition": 50000, "MaxCondition": 50000, "Quality": 100, "DPS_AF": 141, "SPD_ABS": 35, "Hand": 0, "Type_Damage": 2, "Object_Type": 3, "Item_Type": 10, "Color": 0, "Weight": 23, "Model": 1, "Bonus": 30, "Bonus1": 5, "Bonus2": 4, "Bonus3": 0, "Bonus4": 0, "Bonus1Type": 44, "Bonus2Type": 40, "Bonus3Type": 0, "Bonus4Type": 0, "SpellID": 0, "Charges": 0, "MaxCharges": 0, "ProcChance": 0, "ProcSpellID": 32117, "IsPickable": 1, "IsDropable": 1, "CanDropAsLoot": 0, "IsTradable": 0, "Price": 0, "MaxCount": 1, "PackSize": 1, "Realm": 1, "AllowedClasses": "19", "PackageID": "HearthDAoC", "BonusLevel": 0, "LevelRequirement": 40, "Description": "Classic quest reward (Hidden Insurrection)"}
  ],
  "item_fixes": [
    {"Id_nb": "MercenaryEpicVest", "expect": {"Name": "Haurberk of the Shadowy Embers", "AllowedClasses": "0"}, "set": {"Name": "Hauberk of the Shadowy Embers", "AllowedClasses": "11"}},
    {"Id_nb": "InfiltratorEpicVest", "expect": {"SpellID": 0, "Charges": 0, "MaxCharges": 0}, "set": {"SpellID": 31131, "Charges": 3, "MaxCharges": 3}},
    {"Id_nb": "MercenaryEpicVest", "expect": {"SpellID": 0, "Charges": 0, "MaxCharges": 0}, "set": {"SpellID": 31131, "Charges": 3, "MaxCharges": 3}},
    {"Id_nb": "CabalistEpicVest", "expect": {"SpellID": 0, "Charges": 0, "MaxCharges": 0}, "set": {"SpellID": 31131, "Charges": 3, "MaxCharges": 3}},
    {"Id_nb": "NecromancerEpicVest", "expect": {"SpellID": 0, "Charges": 0, "MaxCharges": 0}, "set": {"SpellID": 31131, "Charges": 3, "MaxCharges": 3}},
    {"Id_nb": "ReaverEpicVest", "expect": {"SpellID": 0, "Charges": 0, "MaxCharges": 0}, "set": {"SpellID": 31131, "Charges": 3, "MaxCharges": 3}},
    {"Id_nb": "MercenaryEpicArms", "expect": {"Bonus1Type": 3, "Bonus1": 15, "Bonus2Type": 2, "Bonus2": 16}, "set": {"Bonus1": 16, "Bonus2": 15}},
    {"Id_nb": "InfiltratorEpicGloves", "expect": {"Bonus3Type": 31, "Bonus3": 3}, "set": {"Bonus3": 4}},
    {"Id_nb": "CabalistEpicBoots", "expect": {"Bonus2Type": 37, "Bonus2": 3}, "set": {"Bonus2": 4}},
    {"Id_nb": "cq_alb_amulet_of_feline_graces", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 8, "AllowedClasses": "9"}},
    {"Id_nb": "cq_alb_arawn_s_beads", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 9, "AllowedClasses": "12"}},
    {"Id_nb": "cq_alb_blood_encrusted_whip", "expect": {"Object_Type": 0, "Type_Damage": 0, "Weight": 0, "Model": 488, "ProcSpellID": 0, "AllowedClasses": "0", "LevelRequirement": 0}, "set": {"Object_Type": 24, "Type_Damage": 2, "Weight": 23, "Model": 859, "ProcSpellID": 32117, "AllowedClasses": "19", "LevelRequirement": 40}},
    {"Id_nb": "cq_alb_bone_chip_pin", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 5, "AllowedClasses": "12"}},
    {"Id_nb": "cq_alb_boneshaper_s_ring", "expect": {"Bonus1": 1, "Bonus2": 2, "Bonus3": 0, "Bonus3Type": 0, "Bonus4": 0, "Bonus4Type": 0, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Bonus1": 2, "Bonus2": 4, "Bonus3": 4, "Bonus3Type": 5, "Bonus4": 1, "Bonus4Type": 13, "BonusLevel": 12, "AllowedClasses": "13"}},
    {"Id_nb": "cq_alb_boneshaper_s_spine", "expect": {"Bonus1Type": 21, "Bonus2": 6, "Bonus2Type": 9, "Bonus3": 0, "Bonus3Type": 0, "Bonus4": 0, "Bonus4Type": 0, "DPS_AF": 0, "SPD_ABS": 45, "AllowedClasses": "0"}, "set": {"Bonus1Type": 130, "Bonus2": 18, "Bonus2Type": 129, "Bonus3": 22, "Bonus3Type": 123, "Bonus4": 8, "Bonus4Type": 9, "DPS_AF": 78, "SPD_ABS": 44, "AllowedClasses": "13"}},
    {"Id_nb": "cq_alb_bookworm_s_ring", "expect": {"Bonus4": 0, "Bonus4Type": 0, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Bonus4": 1, "Bonus4Type": 37, "BonusLevel": 6, "AllowedClasses": "13"}},
    {"Id_nb": "cq_alb_boots_of_the_fallen", "expect": {"Object_Type": 0, "DPS_AF": 0, "SPD_ABS": 0, "Model": 259, "AllowedClasses": "0", "Weight": 0, "Bonus": 0}, "set": {"Object_Type": 35, "DPS_AF": 44, "SPD_ABS": 27, "Model": 185, "AllowedClasses": "19", "Weight": 32, "Bonus": 10}},
    {"Id_nb": "cq_alb_bracer_of_strength", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 3, "AllowedClasses": "11"}},
    {"Id_nb": "cq_alb_choker_of_dark_deeds", "expect": {"Bonus1": 0, "Bonus1Type": 0, "Bonus2": 0, "Bonus2Type": 0, "Bonus3": 0, "Bonus3Type": 0, "Bonus4": 0, "Bonus4Type": 0, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Bonus1": 4, "Bonus1Type": 5, "Bonus2": 1, "Bonus2Type": 11, "Bonus3": 2, "Bonus3Type": 26, "Bonus4": 3, "Bonus4Type": 9, "BonusLevel": 10, "AllowedClasses": "12"}},
    {"Id_nb": "cq_alb_cloak_of_forgotten_curses", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 29, "AllowedClasses": "12"}},
    {"Id_nb": "cq_alb_cloak_of_murky_secrets", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 27, "AllowedClasses": "19"}},
    {"Id_nb": "cq_alb_cloak_of_the_shadowy_embers", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 27, "AllowedClasses": "11"}},
    {"Id_nb": "cq_alb_construct_ring", "expect": {"AllowedClasses": "0"}, "set": {"AllowedClasses": "13"}},
    {"Id_nb": "cq_alb_crackling_impaler", "expect": {"ProcSpellID": 0, "AllowedClasses": "0"}, "set": {"ProcSpellID": 32118, "AllowedClasses": "9;11"}},
    {"Id_nb": "cq_alb_darkstone", "expect": {"Model": 117, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Model": 118, "BonusLevel": 5, "AllowedClasses": "12"}},
    {"Id_nb": "cq_alb_dazzle", "expect": {"ProcSpellID": 0, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"ProcSpellID": 32118, "BonusLevel": 25, "AllowedClasses": "11"}},
    {"Id_nb": "cq_alb_death_dancer", "expect": {"ProcSpellID": 0, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"ProcSpellID": 32117, "BonusLevel": 25, "AllowedClasses": "9"}},
    {"Id_nb": "cq_alb_death_s_touch", "expect": {"ProcSpellID": 0, "AllowedClasses": "0"}, "set": {"ProcSpellID": 32117, "AllowedClasses": "9;11"}},
    {"Id_nb": "cq_alb_falconheaded_cloak_pin", "expect": {"Model": 117, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Model": 509, "BonusLevel": 5, "AllowedClasses": "9"}},
    {"Id_nb": "cq_alb_flail_of_fallen_graces", "expect": {"Object_Type": 0, "Model": 488, "ProcSpellID": 0, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Object_Type": 24, "Model": 861, "ProcSpellID": 32117, "BonusLevel": 27, "AllowedClasses": "19"}},
    {"Id_nb": "cq_alb_flayed_skin_necklace", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 7, "AllowedClasses": "12"}},
    {"Id_nb": "cq_alb_fury", "expect": {"BonusLevel": 0, "AllowedClasses": "0"}, "set": {"BonusLevel": 9, "AllowedClasses": "11"}},
    {"Id_nb": "cq_alb_gem_of_black_death", "expect": {"AllowedClasses": "0"}, "set": {"AllowedClasses": "19"}},
    {"Id_nb": "cq_alb_gem_of_shadowy_intentions", "expect": {"Model": 118, "BonusLevel": 0, "AllowedClasses": "0"}, "set": {"Model": 116, "BonusLevel": 9, "AllowedClasses": "9"}},
    {"Id_nb": "cq_alb_glitter", "expect": {"ProcSpellID": 0, "AllowedClasses": "0"}, "set": {"ProcSpellID": 32118, "AllowedClasses": "11"}},
    {"Id_nb": "cq_alb_ring_of_shadowy_embers", "expect": {"AllowedClasses": "0"}, "set": {"AllowedClasses": "11"}}
  ],
  "vests": {"ids": ["InfiltratorEpicVest", "MercenaryEpicVest", "CabalistEpicVest", "NecromancerEpicVest", "ReaverEpicVest"], "SpellID": 31131, "Charges": 3}
```

- [ ] **Step 4: Implement**

In `deploy/bin/epic_chains.py`, add `import uuid` to the imports, add line 7 to the docstring's list:

```
7. Items: the missing rewards and level-40 weapons, inserted where missing; fixes to existing rows (the level-50
   armour, upstream's broken rewards, every Guild of Shadows reward locked to its class).
```

add before `STEPS`:

```python
def _item_columns(conn, names):
    known = {row[1] for row in conn.execute('PRAGMA table_info("ItemTemplate")')}
    unknown = set(names) - known
    if unknown:
        raise ValueError(f"not ItemTemplate columns: {', '.join(sorted(unknown))}")
    return names


def _items(conn, now, data):
    added = 0
    for item in data["items"]:
        if conn.execute("SELECT 1 FROM ItemTemplate WHERE Id_nb=?", (item["Id_nb"],)).fetchone():
            continue
        # A stable ItemTemplate_ID, so the same item has the same row ID in every world.
        row = dict(item, ItemTemplate_ID=str(uuid.uuid5(uuid.NAMESPACE_URL, "hearthdaoc:item:" + item["Id_nb"])),
                   LastTimeRowUpdated=now)
        columns = ", ".join(f'"{c}"' for c in _item_columns(conn, list(row)))
        conn.execute(f"INSERT INTO ItemTemplate ({columns}) VALUES ({', '.join('?' * len(row))})", tuple(row.values()))
        added += 1
    fixed = 0
    for fix in data["item_fixes"]:
        expect = _item_columns(conn, list(fix["expect"]))
        current = conn.execute(f"SELECT {', '.join(expect)} FROM ItemTemplate WHERE Id_nb=?", (fix["Id_nb"],)).fetchone()
        if current is None or [str(v) for v in current] != [str(fix["expect"][c]) for c in expect]:
            continue
        sets = ", ".join(f"{c}=?" for c in _item_columns(conn, list(fix["set"])))
        conn.execute(f"UPDATE ItemTemplate SET {sets}, LastTimeRowUpdated=? WHERE Id_nb=?",
                     (*fix["set"].values(), now, fix["Id_nb"]))
        fixed += 1
    return {"items": added, "fixes": fixed}
```

and change `STEPS` to `(_gos_links, _pin_names, _close_supply_runs, _pay, _rewards, _texts, _items)`.

- [ ] **Step 5: Run the tests**

Run: `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py' -v`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add deploy/bin/epic_chains.py deploy/bin/epic_chains_data.json deploy/tests/test_epic_chains.py
git commit -m "feat(deploy): the Guild of Shadows rewards upstream never made, locked and fixed

30 class rewards and 11 level-40 weapons that upstream's quests name but the
world lacks, from the archived Allakhazam item pages, inserted where missing.
Every Guild of Shadows reward and weapon is locked to its class, as on live.
Fixes to existing rows: the level-50 armour (the Mercenary vest's name and
class, the five vests' charges, Mercenary arms, Infiltrator gloves, Cabalist
boots) and upstream's broken rewards (missing bonuses, weapon and armour
types, models, procs, bonus levels), from the item pages and the live Herald.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The level-50 quests, Lord Elidyn's camp, the old Shadows_50, and the wiring

Steps 8 and 9 of the world fix (spec 2.4, 2.6, 3.4), then `world_fixes.py` runs the fix at every start.

**Files:**
- Modify: `deploy/bin/epic_chains.py` (`_level50`, `_camp`, `_old_shadows_50`; `STEPS`; the docstring's list)
- Modify: `deploy/bin/epic_chains_data.json` (keys `level50`, `camp`, `old_quest`)
- Modify: `deploy/bin/world_fixes.py` (import, call, docstring item 5)
- Modify: `deploy/tests/test_epic_chains.py`, `deploy/tests/test_world_fixes.py` (`test_shipped_world`), `deploy/tests/smoke.sh`

**Interfaces:**
- Consumes: Task 5's `pay_lists`, `_entry`, `ARCHIVE`, `CLASSIC`, `STEPS`; Task 6's data key `vests` (`ids`,
  `Charges`); the armour rows `<Class>Epic{Helm,Vest,Arms,Gloves,Legs,Boots}` (already in the world).
- Produces: DataQuest rows 990509, 990511, 990513, 990512, 990519; `world_fixes.apply()` returns the epic chains'
  line after the battlegrounds' lines.

- [ ] **Step 1: Write the failing tests**

In `deploy/tests/test_epic_chains.py`, change `SUMMARY` to its final value:

```python
SUMMARY = ("Epic chains: Guild of Shadows 60 links, 60 XP and coin, 4 Supply Runs closed, 2 rewards and 7 texts fixed; "
           "87 other links; 41 items added, 36 item fixes; 5 level-50 quests, Lord Elidyn's camp 17 restored; "
           "Shadows_50: 0 finished carried, 0 removed, 0 epic vests recharged")
```

Add to `EpicWorldTests`:

```python
    def test_50_opens_after_48_and_closes_the_chain(self):
        upto = ["7", "11", "15", "20", "25", "30", "40", "43", "45"]
        for col, name in enumerate(self.data["classes"]):
            with self.subTest(name):
                self.assertEqual(self.open_steps(col, upto), ["48"])
                self.assertEqual(self.open_steps(col, upto + ["48"]), ["50"])
                self.assertEqual(self.open_steps(col, upto + ["48", "50"]), [])

    def test_the_level_50_quests(self):
        for col, (name, class_id) in enumerate(self.data["classes"].items()):
            qid = LEVEL_50_IDS[col]
            row = self.after[qid]
            with self.subTest(name):
                self.assertEqual(
                    {k: row[k] for k in ("Name", "StartType", "StartName", "StartRegionID", "AcceptText", "MaxCount",
                                         "MinLevel", "MaxLevel", "StepType", "TargetName", "RewardXP", "RewardMoney",
                                         "QuestDependency", "AllowedClasses", "ClassType")},
                    {"Name": "Lord of Deceit", "StartType": 0, "StartName": "Captain Rhodri", "StartRegionID": 1,
                     "AcceptText": "deceit", "MaxCount": 1, "MinLevel": 50, "MaxLevel": 50, "StepType": "0|5",
                     "TargetName": "Lord Elidyn;1|Captain Rhodri;1", "RewardXP": "0|0", "RewardMoney": "0|5000",
                     "QuestDependency": f"#{self.data['steps']['48']['ids'][col]}", "AllowedClasses": str(class_id),
                     "ClassType": CLASSIC})
                armour = row["FinalRewardItemTemplates"].split("|")
                self.assertEqual(armour, [f"{name}Epic{p}" for p in ("Helm", "Vest", "Arms", "Gloves", "Legs", "Boots")])
                for item in armour:
                    self.assertTrue(self.conn.execute("SELECT 1 FROM ItemTemplate WHERE Id_nb=?", (item,)).fetchone(), item)
                self.assertIn("[deceit]", row["Description"])
                self.assertEqual(len(row["StepText"].split("|")), 2)
                self.assertTrue(row["FinishText"])

    def test_lord_elidyns_camp_is_back(self):
        camp = rows(self.conn, f"SELECT * FROM Mob WHERE Region=1 AND (X-568158)*(X-568158)+(Y-404718)*(Y-404718) <= 2250000 "
                               f"AND lower(Name) IN ('lord elidyn', 'ellyll guard', 'ellyl hero')")
        names = sorted(r["Name"].lower() for r in camp)
        self.assertEqual(names, ["ellyl hero"] * 2 + ["ellyll guard"] * 14 + ["lord elidyn"])
        archived = {r["Mob_ID"]: r for r in rows(self.conn, "SELECT * FROM offline_classic165_removed_mobs")}
        for row in camp:
            self.assertEqual(row, {k: v for k, v in archived[row["Mob_ID"]].items() if k in row})  # copied as archived
        self.assertIn(LORD_ELIDYN, {r["Mob_ID"] for r in camp})

    def test_the_extra_quest_file_names_the_level_50_quests_and_lord_elidyn(self):
        with open(EXTRA_QUESTS, encoding="utf-8") as f:
            extra = json.load(f)
        self.assertEqual(sorted(int(k) for k in extra["Quests"]), sorted(self.data["steps"]["50"]["ids"]))
        self.assertTrue(self.conn.execute("SELECT 1 FROM Mob WHERE Mob_ID=?", (extra["QuestMonsterIds"][0],)).fetchone())
```

Add a new class:

```python
@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class EpicCharacterTests(unittest.TestCase):
    """Characters' old Shadows_50 and epic vests (spec 2.6, 3.4 step 9), and a camp row already back."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, self.db)
        self.conn = sqlite3.connect(self.db)
        for cid, name, cls in (("char-inf", "Shadowa", 9), ("char-rea", "Reavo", 19), ("char-mer", "Merco", 11)):
            self.conn.execute("INSERT INTO DOLCharacters (DOLCharacters_ID, Name, Class, Realm, Level) VALUES (?, ?, ?, 1, 50)",
                              (cid, name, cls))
        old = "DOL.GS.Quests.Albion.Shadows_50"
        for qid, cid, step in (("q1", "char-inf", -2), ("q2", "char-rea", -2), ("q3", "char-mer", 1)):
            self.conn.execute("INSERT INTO Quest (Quest_ID, Name, Step, Character_ID) VALUES (?, ?, ?, ?)", (qid, old, step, cid))
        self.conn.execute("INSERT INTO CharacterXDataQuest (Character_ID, DataQuestID, Step, Count) VALUES ('char-rea', 990519, 0, 1)")
        for iid, template, charges in (("i1", "ReaverEpicVest", 0), ("i2", "InfiltratorEpicVest", 2), ("i3", "ReaverEpicHelm", 0)):
            self.conn.execute("INSERT INTO Inventory (Inventory_ID, OwnerID, ITemplate_Id, SlotPosition, Charges) VALUES (?, 'char-rea', ?, 40, ?)",
                              (iid, template, charges))
        self.conn.commit()

    def tearDown(self):
        self.conn.close()
        self.tmp.cleanup()

    def test_a_finished_shadows_50_becomes_the_finished_level_50_step(self):
        result = epic_chains.apply(self.conn, NOW)
        self.assertIn("Shadows_50: 2 finished carried, 1 removed, 1 epic vests recharged", result[0])
        self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM Quest WHERE Name LIKE '%Shadows_50'").fetchone(), (0,))
        done = sorted(self.conn.execute("SELECT Character_ID, DataQuestID, Step, Count FROM CharacterXDataQuest").fetchall())
        self.assertEqual(done, [("char-inf", 990509, 0, 1), ("char-rea", 990519, 0, 1)])

    def test_only_empty_epic_vests_get_charges(self):
        epic_chains.apply(self.conn, NOW)
        charges = dict(self.conn.execute("SELECT Inventory_ID, Charges FROM Inventory WHERE Inventory_ID IN ('i1','i2','i3')"))
        self.assertEqual(charges, {"i1": 3, "i2": 2, "i3": 0})

    def test_a_camp_row_already_back_is_not_copied_twice(self):
        columns = ", ".join(f'"{name}"' for _, name, *_ in self.conn.execute('PRAGMA table_info("Mob")'))
        self.conn.execute(f"INSERT INTO Mob ({columns}) SELECT {columns} FROM offline_classic165_removed_mobs WHERE Mob_ID=?",
                          (LORD_ELIDYN,))
        result = epic_chains.apply(self.conn, NOW)
        self.assertIn("Lord Elidyn's camp 16 restored", result[0])
        self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM Mob WHERE Mob_ID=?", (LORD_ELIDYN,)).fetchone(), (1,))
```

In `deploy/tests/test_world_fixes.py`, `test_shipped_world`: add the summary as the last expected line of
`wf.apply(self.db)` and the marker:

```python
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
            "3 Void Merchants, the stray Wizard)",
            "Epic chains: Guild of Shadows 60 links, 60 XP and coin, 4 Supply Runs closed, 2 rewards and 7 texts "
            "fixed; 87 other links; 41 items added, 36 item fixes; 5 level-50 quests, Lord Elidyn's camp 17 restored; "
            "Shadows_50: 0 finished carried, 0 removed, 0 epic vests recharged",
        ])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes"),
                         [("classic-battlegrounds-v2",), ("epic-chains-v1",)])
```

In `deploy/tests/smoke.sh`, after the line `echo "ok - Disciple enabled and Saracen Disciples have a starting location"`:

```bash
logs_have "Epic chains: Guild of Shadows 60 links" || fail "the epic chains world fix did not run"
[[ "$(docker exec "$NAME" sqlite3 "$db" "SELECT COUNT(*) FROM DataQuest WHERE ID IN (990509, 990511, 990512, 990513, 990519)")" == 5 ]] \
    || fail "the level-50 Lord of Deceit quests are missing"
echo "ok - the epic chains world fix ran (level-50 quests in the world)"
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db python3 -m unittest discover -s deploy/tests -t deploy -p 'test_epic_chains.py'`
Expected: failures in `test_the_summary_line` (0 level-50 quests), the four new world tests, and `EpicCharacterTests`
(`KeyError: 'level50'` or the counts).

- [ ] **Step 3: Add the data**

In `deploy/bin/epic_chains_data.json`, add after `"texts": [...]` (mind the comma after the `texts` list):

```json
  "level50": {
    "name": "Lord of Deceit",
    "start_name": "Captain Rhodri",
    "start_region": 1,
    "accept": "deceit",
    "description": "<Player>, Sir Tilian's notes on the seer's journal leave no doubt. The hand behind the Ellyll and the Arawnites belongs to Lord Elidyn, the Lord of Deceit, who holds the old Ellyll ruins on the hill in the Pennine Mountains. Put an end to his [deceit].",
    "steps": ["Slay Lord Elidyn, the Lord of Deceit, in the Ellyll ruins on the hill in the Pennine Mountains.", "Return to Captain Rhodri at the Snowdonia border keep."],
    "step_types": [0, 5],
    "targets": ["Lord Elidyn;1", "Captain Rhodri;1"],
    "finish": "Lord Elidyn is dead, and his lies with him. Sir Bors will hear of this before nightfall. Take this armour, <Class>; the Guild of Shadows makes it only for its finest.",
    "armour": ["Helm", "Vest", "Arms", "Gloves", "Legs", "Boots"]
  },
  "camp": {"region": 1, "x": 568158, "y": 404718, "radius": 1500, "names": ["Lord Elidyn", "Ellyll guard", "ellyl hero"]},
  "old_quest": "DOL.GS.Quests.Albion.Shadows_50"
```

- [ ] **Step 4: Implement**

In `deploy/bin/epic_chains.py`, extend the docstring's numbered list with:

```
7. Items: the missing rewards and level-40 weapons, inserted where missing; fixes to existing rows (the level-50
   armour, upstream's broken rewards, every Guild of Shadows reward locked to its class).
8. The five level-50 quests ("Lord of Deceit", given by Captain Rhodri after the class's 48), and Lord Elidyn's camp
   copied back from upstream's archive (offline_classic165_removed_mobs), Mob_IDs kept.
9. Old Shadows_50 progress: a finished one becomes the character's finished level-50 step (no second armour set),
   any other is removed; epic vests in inventories with no charges get the template's.
```

(Line 7 is Task 6's; add it here if Task 6 didn't.) Then add before `STEPS`:

```python
def _level50(conn, now, data):
    quest, steps = data["level50"], data["steps"]
    step = steps["50"]
    stages = len(quest["steps"])
    xp, money = pay_lists(stages, step)
    empty = "|".join([""] * stages)
    added = 0
    for col, (class_name, class_id) in enumerate(data["classes"].items()):
        qid = step["ids"][col]
        if conn.execute("SELECT 1 FROM DataQuest WHERE ID=?", (qid,)).fetchone():
            continue
        armour = "|".join(f"{class_name}Epic{piece}" for piece in quest["armour"])
        dependency = "|".join(_entry(steps, link, col) for link in step["links"])
        conn.execute(
            "INSERT INTO DataQuest (ID, Name, StartType, StartName, StartRegionID, AcceptText, Description, SourceName, "
            "SourceText, StepType, StepText, StepItemTemplates, AdvanceText, TargetName, TargetText, CollectItemTemplate, "
            "MaxCount, MinLevel, MaxLevel, RewardMoney, RewardXP, RewardCLXP, RewardRP, RewardBP, "
            "OptionalRewardItemTemplates, FinalRewardItemTemplates, FinishText, QuestDependency, AllowedClasses, "
            "ClassType, LastTimeRowUpdated) "
            "VALUES (?, ?, 0, ?, ?, ?, ?, '', ?, ?, ?, ?, ?, ?, ?, ?, 1, 50, 50, ?, ?, '', '', '', '', ?, ?, ?, ?, ?, ?)",
            (qid, quest["name"], quest["start_name"], quest["start_region"], quest["accept"], quest["description"], empty,
             "|".join(map(str, quest["step_types"])), "|".join(quest["steps"]), empty, empty, "|".join(quest["targets"]),
             empty, empty, money, xp, armour, quest["finish"], dependency, str(class_id), CLASSIC, now))
        added += 1
    return {"level50": added, "camp": _camp(conn, data["camp"])}


def _camp(conn, camp):
    """Copy the camp's archived rows back into Mob (kept in the archive); rows already in Mob are skipped."""
    columns = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")'))
    names = [name.lower() for name in camp["names"]]
    x, y, radius = camp["x"], camp["y"], camp["radius"]
    return conn.execute(
        f"INSERT INTO Mob ({columns}) SELECT {columns} FROM {ARCHIVE} WHERE Region=? "
        f"AND (X-?)*(X-?) + (Y-?)*(Y-?) <= ? AND lower(Name) IN ({', '.join('?' * len(names))}) "
        f"AND Mob_ID NOT IN (SELECT Mob_ID FROM Mob)",
        (camp["region"], x, x, y, y, radius * radius, *names)).rowcount


def _old_shadows_50(conn, now, data):
    by_class = dict(zip(data["classes"].values(), data["steps"]["50"]["ids"]))
    carried = removed = 0
    for quest_id, character, step, class_id in conn.execute(
            "SELECT q.Quest_ID, q.Character_ID, q.Step, c.Class FROM Quest q "
            "LEFT JOIN DOLCharacters c ON c.DOLCharacters_ID = q.Character_ID WHERE q.Name=?",
            (data["old_quest"],)).fetchall():
        target = by_class.get(class_id)
        if step == -2 and target is not None:  # -2: finished (AbstractQuest.FinishQuest)
            if not conn.execute("SELECT 1 FROM CharacterXDataQuest WHERE Character_ID=? AND DataQuestID=?",
                                (character, target)).fetchone():
                conn.execute("INSERT INTO CharacterXDataQuest (Character_ID, DataQuestID, Step, Count, LastTimeRowUpdated) "
                             "VALUES (?, ?, 0, 1, ?)", (character, target, now))
            carried += 1
        else:
            removed += 1
        conn.execute("DELETE FROM Quest WHERE Quest_ID=?", (quest_id,))
    vests = data["vests"]
    recharged = conn.execute(
        f"UPDATE Inventory SET Charges=?, LastTimeRowUpdated=? "
        f"WHERE ITemplate_Id IN ({', '.join('?' * len(vests['ids']))}) AND Charges=0",
        (vests["Charges"], now, *vests["ids"])).rowcount
    return {"carried": carried, "removed": removed, "vests": recharged}
```

and change `STEPS` to:

```python
STEPS = (_gos_links, _pin_names, _close_supply_runs, _pay, _rewards, _texts, _items, _level50, _old_shadows_50)
```

In `deploy/bin/world_fixes.py`: next to `import battlegrounds` add `import epic_chains`; after
`changes.extend(battlegrounds.apply(conn, _now()))` add:

```python
            changes.extend(epic_chains.apply(conn, _now()))
```

and in the module docstring, after item 4, add:

```
5. The epic chains (sub-project 4): epic_chains.py, once per world (the marker epic-chains-v1), after the
   battlegrounds and under its own savepoint like them: upstream's Guild of Shadows chain in order and complete, the
   real level-50 "Lord of Deceit", and every guild line's steps in order.
```

- [ ] **Step 5: Run the tests**

Run: `HDC_TEST_WORLD=$HOME/Games/OfflineDAoC-archive/test-world/clean-classic-0.35.db python3 -m unittest discover -s deploy/tests -t deploy`
Expected: OK (the whole deploy suite, `test_world_fixes.test_shipped_world` included).
Run: `bash -n deploy/tests/smoke.sh` — no output.

- [ ] **Step 6: Commit**

```bash
git add deploy/bin/epic_chains.py deploy/bin/epic_chains_data.json deploy/bin/world_fixes.py deploy/tests/test_epic_chains.py deploy/tests/test_world_fixes.py deploy/tests/smoke.sh
git commit -m "feat(deploy): the real level-50 Lord of Deceit; world_fixes runs epic-chains-v1

Five data quests (990509, 990511, 990513, 990512, 990519) from Captain
Rhodri after the class's 48: kill Lord Elidyn, back to Rhodri for the six
armour pieces and 50 silver. Lord Elidyn, his 14 guards and 2 heroes come back
from upstream's archive. A finished old Shadows_50 becomes the finished
level-50 step; others are removed; empty epic vests get their charges.
world_fixes.py now runs the fix after the battlegrounds.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 8: `/epic`, the GM's chain tool

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/EpicChain.cs` (the decisions), `source/server/GameServer/scripts/hearthdaoc/EpicCommand.cs` (the command)
- Create: `source/server/Tests/UnitTests/UT_EpicChain.cs`
- Modify: `.github/workflows/server-image.yml:113`, `deploy/tests/test_workflows.py`, `deploy/tests/smoke.sh`

**Interfaces:**
- Consumes: `QuestDependencies` (Task 2), `ClassicQuests.MarkerFor` (Task 3).
- Produces (namespace `DOL.GS.HearthDAoC`): records `EpicQuest`, `EpicStep`, `EpicProgress`, enum `EpicStepState`,
  static class `EpicChain` (`ParseClasses`, `ParseDependencies`, `ChainFor`, `Steps`, `FinishBelow`,
  `ShroudedIslesRegions`), and the GM command `&epic`.

- [ ] **Step 1: Write the failing test**

Create `source/server/Tests/UnitTests/UT_EpicChain.cs` (LF):

```csharp
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

    // A Guild of Shadows chain cut short (with the world fix's links), a Defenders-like line mixing pinned IDs and a
    // name, and a quest with no links.
    private static readonly List<EpicQuest> Quests = new()
    {
        Q(21500, "Traveler's Way -- Supply Run", 7, "9", "!#20478/21324"),
        Q(21324, "Traveler's Way -- Supply Run", 7, "9", "#21324"),
        Q(20478, "Strange Beings", 7, "9", "!#21500/21324", region: 51),
        Q(20157, "Entry Into Tomorrow", 11, "9", "#21500/21324/20478|!#20469"),
        Q(20469, "Shades and Shadows", 11, "9", "#21500/21324/20478|!#20157", region: 51),
        Q(20188, "Rebellion Accepted", 15, "9", "#20157/20469"),
        Q(990509, "Lord of Deceit", 50, "9", "#20188"),
        Q(20001, "Some Errand", 5, "9", ""),
        Q(20401, "Legend of the Lake", 15, "2", ""),
        Q(20419, "Legend of the Lake", 20, "2", "#20401"),
        Q(20500, "Hands Of Fate", 30, "2", "#20419"),
        Q(20600, "Feast of the Decadent", 43, "2", "Hands Of Fate"),
        Q(20407, "Feast of the Decadent", 45, "2", "#20600"),
        Q(21287, "Feast of the Decadent", 48, "2|3|10|5", "#20407"),
    };

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
```

Append `|FullyQualifiedName~UT_EpicChain` to the CI filter and `SERVER_UNIT_TESTS`, and `"UT_EpicChain"` to the
expected list in `deploy/tests/test_workflows.py`, as in Task 2.

- [ ] **Step 2: Run the test to see it fail**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_EpicChain"`
Expected: build error `The type or namespace name 'EpicQuest' could not be found`.

- [ ] **Step 3: Implement the decisions**

Create `source/server/GameServer/scripts/hearthdaoc/EpicChain.cs` (LF):

```csharp
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
```

- [ ] **Step 4: Run the test**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_EpicChain"`
Expected: 7 passed.

- [ ] **Step 5: Implement the command**

Create `source/server/GameServer/scripts/hearthdaoc/EpicCommand.cs` (LF):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.Commands;
using DOL.GS.Quests;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: /epic, the GM's tool for testing an epic chain (spec docs/fork/specs/2026-10-09-epic-chains-design.md,
// section 3.3). EpicChain makes every decision; this class reads the classic data quests and the character's quests,
// and carries out "done", "goto" and "reset" on the character's loaded quest lists and the database together, so no
// relog is needed. It works on the GM's target if that is a player, otherwise on the GM.
[CmdAttribute(
    "&epic",
    ePrivLevel.GM,
    "HearthDAoC: show or change a character's epic chain (your target, or you)",
    "/epic - the chain and the state of each step",
    "/epic done <level> - mark every step below <level> finished",
    "/epic goto - go to the current stage's map marker, or to the next step's giver",
    "/epic reset - remove every step of the chain, active and finished")]
public sealed class EpicCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer target = client.Player.TargetObject as GamePlayer ?? client.Player;
        IReadOnlyList<EpicQuest> chain = EpicChain.ChainFor(LoadQuests(), target.CharacterClass.ID);
        if (chain.Count == 0)
        {
            DisplayMessage(client, $"{target.Name} ({target.CharacterClass.Name}) has no epic chain linked by quest IDs.");
            return;
        }
        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : string.Empty;
        if (sub == string.Empty)
            Show(client, target, chain);
        else if (sub == "done" && args.Length > 2 && int.TryParse(args[2], out int level))
            Done(client, target, chain, level);
        else if (sub == "goto")
            GoTo(client, target, chain);
        else if (sub == "reset")
            Reset(client, target, chain);
        else
            DisplaySyntax(client);
    }

    private static List<EpicQuest> LoadQuests()
    {
        var quests = new List<EpicQuest>();
        foreach (DbDataQuest row in GameServer.Database.SelectAllObjects<DbDataQuest>())
        {
            if (row.ClassType == null || !row.ClassType.Contains("ClassicQuestStep", StringComparison.Ordinal))
                continue;
            quests.Add(new EpicQuest(row.ID, row.Name, row.MinLevel, row.MaxLevel, row.StartRegionID,
                EpicChain.ParseClasses(row.AllowedClasses), EpicChain.ParseDependencies(row.QuestDependency)));
        }
        return quests;
    }

    private static EpicProgress ProgressOf(GamePlayer player)
    {
        var names = new List<string>();
        var ids = new HashSet<int>();
        foreach (AbstractQuest quest in player.GetFinishedQuests())
        {
            if (quest is DataQuest dataQuest)
            {
                names.Add(dataQuest.Name);
                ids.Add(dataQuest.ID);
            }
        }
        var active = new Dictionary<int, int>();
        foreach (AbstractQuest quest in player.QuestList.Keys)
        {
            if (quest is DataQuest dataQuest)
                active[dataQuest.ID] = dataQuest.Step;
        }
        return new EpicProgress(names, ids, active, player.Level);
    }

    private void Show(GameClient client, GamePlayer target, IReadOnlyList<EpicQuest> chain)
    {
        DisplayMessage(client, $"{target.Name}, level {target.Level} {target.CharacterClass.Name}:");
        foreach (EpicStep step in EpicChain.Steps(chain, ProgressOf(target)))
            DisplayMessage(client, $"  {step.Quest.MinLevel} {step.Quest.Name} ({step.Quest.Id}): {step.Detail}");
    }

    private void Done(GameClient client, GamePlayer target, IReadOnlyList<EpicQuest> chain, int level)
    {
        IReadOnlyList<int> ids = EpicChain.FinishBelow(chain, level, ProgressOf(target));
        foreach (int id in ids)
        {
            DbDataQuest row = GameServer.Database.FindObjectByKey<DbDataQuest>(id);
            if (row == null)
                continue;
            DataQuest active = target.QuestList.Keys.OfType<DataQuest>().FirstOrDefault(q => q.ID == id);
            if (active != null)
            {
                RemoveActive(target, active);
                active.DeleteFromDatabase();
            }
            var finished = new DbCharacterXDataQuest(target.QuestPlayerID, id) { Step = 0, Count = 1 };
            GameServer.Database.AddObject(finished);
            target.AddFinishedQuest(new DataQuest(target, row, finished));
        }
        target.Out.SendQuestListUpdate();
        DisplayMessage(client, ids.Count == 0
            ? $"Nothing below level {level} to mark for {target.Name}."
            : $"{target.Name}: marked finished {string.Join(", ", ids)}. See /epic.");
    }

    private void GoTo(GameClient client, GamePlayer target, IReadOnlyList<EpicQuest> chain)
    {
        var ids = new HashSet<int>(chain.Select(q => q.Id));
        DataQuest active = target.QuestList.Keys.OfType<DataQuest>().FirstOrDefault(q => ids.Contains(q.ID));
        if (active != null)
        {
            ClassicQuests.Point marker = ClassicQuests.MarkerFor(active.ID, active.Step);
            if (marker == null)
            {
                DisplayMessage(client, $"{active.Name} ({active.ID}) stage {active.Step} has no map marker.");
                return;
            }
            target.MoveTo(marker.Region, marker.X, marker.Y, marker.Z, target.Heading);
            DisplayMessage(client, $"{target.Name} is at the marker of {active.Name} ({active.ID}) stage {active.Step}.");
            return;
        }
        EpicQuest next = EpicChain.Steps(chain, ProgressOf(target)).FirstOrDefault(s => s.State == EpicStepState.CanTake)?.Quest;
        if (next == null)
        {
            DisplayMessage(client, $"{target.Name} has no step to take now; see /epic.");
            return;
        }
        DbDataQuest row = GameServer.Database.FindObjectByKey<DbDataQuest>(next.Id);
        GameNPC giver = row == null ? null : WorldMgr.GetNPCsByNameFromRegion(row.StartName, row.StartRegionID, target.Realm).FirstOrDefault();
        if (giver == null)
        {
            DisplayMessage(client, $"{row?.StartName} isn't in region {row?.StartRegionID}.");
            return;
        }
        target.MoveTo(giver.CurrentRegionID, giver.X, giver.Y, giver.Z, giver.Heading);
        DisplayMessage(client, $"{target.Name} is at {giver.Name}, who gives {next.Name} ({next.Id}).");
    }

    private void Reset(GameClient client, GamePlayer target, IReadOnlyList<EpicQuest> chain)
    {
        var ids = new HashSet<int>(chain.Select(q => q.Id));
        int last = chain[^1].Id;
        bool hadLast = ProgressOf(target).FinishedIds.Contains(last);
        foreach (DataQuest active in target.QuestList.Keys.OfType<DataQuest>().Where(q => ids.Contains(q.ID)).ToList())
        {
            RemoveActive(target, active);
            active.DeleteFromDatabase();
        }
        target.RemoveFinishedQuests(q => q is DataQuest dataQuest && ids.Contains(dataQuest.ID));
        target.Out.SendQuestListUpdate();
        DisplayMessage(client, $"{target.Name}: the chain is reset." +
            (hadLast ? $" That removed the finished {chain[^1].Name} ({last}): its reward can be given again." : string.Empty));
    }

    private static void RemoveActive(GamePlayer player, DataQuest quest)
    {
        if (player.QuestList.TryRemove(quest, out byte index))
        {
            player.AvailableQuestIndexes.Enqueue(index);
            player.Out.SendQuestRemove(index);
        }
    }
}
```

Check before building that these members exist as used (each `grep` prints a line):
`grep -n "public virtual GameObject TargetObject" source/server/GameServer/gameobjects/GameLiving.cs`,
`grep -n "public ConcurrentQueue<byte> AvailableQuestIndexes" source/server/GameServer/gameobjects/GamePlayer.cs`,
`grep -n "void SendQuestRemove(byte index)" source/server/GameServer/packets/Server/IPacketLib.cs`,
`grep -n "public static GameNPC\[\] GetNPCsByNameFromRegion" source/server/GameServer/world/WorldMgr.cs`,
`grep -n "public void RemoveFinishedQuests" source/server/GameServer/gameobjects/GamePlayer.cs`.

In `deploy/tests/smoke.sh`, after the line `grep -q "Command - '&spawn' .* required plvl:1" ...`, add:

```bash
grep -q "Command - '&epic' .* required plvl:2" "$T/server.log" || fail "/epic is not GM-only"
```

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build source/server/DOLLinux.sln -c Release --nologo -v q` — no new errors.
Run the whole fork filter: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds|FullyQualifiedName~UT_DataQuestFinishRewards|FullyQualifiedName~UT_DataQuestDependency|FullyQualifiedName~UT_ClassicQuestsExtra|FullyQualifiedName~UT_EpicChain"` — all pass.
Run: `python3 -m unittest discover -s deploy/tests -t deploy -p 'test_workflows.py'` — OK; `bash -n deploy/tests/smoke.sh`.

- [ ] **Step 7: Commit**

```bash
git add source/server/GameServer/scripts/hearthdaoc/EpicChain.cs source/server/GameServer/scripts/hearthdaoc/EpicCommand.cs source/server/Tests/UnitTests/UT_EpicChain.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py deploy/tests/smoke.sh
git commit -m "feat(server): /epic, the GM's tool for testing an epic chain

/epic lists the target's (or the GM's) chain and each step's state; /epic
done <level> marks every step below the level finished (the classic 7 and 11
unless a Shrouded Isles version is finished); /epic goto jumps to the current
stage's map marker or the next giver; /epic reset clears the chain. The chain
is found from the quests' dependency entries, so it works for every line the
world fix links.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Docs and the in-game test guide

**Files:**
- Modify: `docs/fork/FORK.md` (the second change table; a "World fixes" mention if the file has one)
- Create: `docs/fork/verification/sub4-test-guide.md`

`docs/fork/CHANGELOG.md` is not changed: since v0.34b-hearth.6 it points to the releases page for later releases (the
spec's mention of it predates that).

- [ ] **Step 1: FORK.md**

Add these rows at the end of the table whose header is `| Change | Files | Why | Upstream |`:

```markdown
| Classic quests pay their final XP and coin | `GameServer/quests/QuestsMgr/DataQuest.cs` (`FinishRewardIndex`, five reads in `FinishQuest`, marked `// HearthDAoC:`), test `Tests/UnitTests/UT_DataQuestFinishRewards.cs` | `FinishQuest` paid the first entry of the reward lists, but upstream's classic quests keep the final reward in the last one: 1,240 of 1,302 never paid their final XP, 419 their final coin (owner 2026-10-09: fix it for every quest) | Candidate, after the in-game run (ask the owner first) |
| Quest dependencies by quest ID (`#id`, `#a/b`, `!#a/b`) | `GameServer/quests/QuestsMgr/QuestDependencies.cs` (new), `GameServer/quests/QuestsMgr/DataQuest.cs` (`ParseQuestData`, `CheckQuestQualification`, marked), test `Tests/UnitTests/UT_DataQuestDependency.cs` | Upstream's epic chains reuse names, so a name can't say which step comes before (#73, [spec](specs/2026-10-09-epic-chains-design.md)) | Candidate, after the in-game run (ask the owner first) |
| HearthDAoC's quest data file | `GameServer/quests/QuestsMgr/ClassicQuests.cs` (`Reload`, `Merge`, `ReadExtra`, `MarkerFor`, marked), `deploy/hearthdaoc-quests.json` (copied next to the server by `deploy/Dockerfile`), test `Tests/UnitTests/UT_ClassicQuestsExtra.cs` | Map markers for the level-50 Lord of Deceit and Lord Elidyn on the gamebots' leave-alone list; upstream's entry wins on the same quest ID | Fork-only (upstream edits its own file) |
| The epic chains in the world data | `deploy/bin/epic_chains.py`, `deploy/bin/epic_chains_data.json`, run by `deploy/bin/world_fixes.py` once per world (marker `epic-chains-v1`), tests `deploy/tests/test_epic_chains.py` | The Guild of Shadows chain 7→50 in order with every reward and the real level-50 "Lord of Deceit" (quests 990509, 990511, 990513, 990512, 990519; Lord Elidyn's camp from upstream's archive); every guild line's steps in order (87 rows); the 41 missing rewards and weapons, every Guild of Shadows reward locked to its class, upstream's broken rewards fixed; the old `Shadows_50` progress carried over | The data changes are a candidate (as SQL), after the in-game run |
| Level-50 epic quest code | `GameServer/scripts/quests/Albion/epic/Shadows50.cs` deleted (the Defenders copy); one lookup line each in `GameServer/scripts/quests/Albion/epic/Academy50.cs`, `Hibernia/epic/Essence50.cs`, `Midgard/epic/Mystic50.cs`, `Midgard/epic/Viking50.cs` (marked); source checks in `deploy/tests/test_epic_chains.py` | The Guild of Shadows 50 was the Defenders quest; the four others made a second NPC at every start when a GM had saved one | Candidate, after the in-game run |
| `/epic` (GM only) | `GameServer/scripts/hearthdaoc/EpicChain.cs` (the decisions), `GameServer/scripts/hearthdaoc/EpicCommand.cs` (the command), test `Tests/UnitTests/UT_EpicChain.cs` | Testing an epic chain quickly: list, `done <level>`, `goto`, `reset` | Fork-only |
```

- [ ] **Step 2: The test guide**

Create `docs/fork/verification/sub4-test-guide.md`:

```markdown
# Sub-project 4: in-game test guide (the epic chains)

For the owner, after the release with sub-project 4 is installed (`./hdc update`). The world fix runs at the first
start: the server log has a line starting `Epic chains: Guild of Shadows 60 links`.

## GM commands used

| Command | Does |
|---|---|
| `/epic` | Lists your target's (or your own) epic chain and each step's state. |
| `/epic done <level>` | Marks every step below `<level>` finished (the classic 7 and 11 unless a Shrouded Isles version is finished). |
| `/epic goto` | Jumps to the current stage's map marker, or to the next step's giver. |
| `/epic reset` | Removes every step of the chain, active and finished. |
| `/player level <n>` | Sets your target's level (target yourself to level yourself). |
| `/item create <template id>` | Makes an item, to check a reward's stats. |

Use a character of one of the five classes: Infiltrator, Mercenary, Cabalist, Necromancer or Reaver.

## 1. One class through the whole chain

1. `/player level 7`, then `/epic`: 7 and 7 SI say "can take", Lady Aelawen's Supply Run "offered to no one".
2. Take the classic 7 from your Camelot trainer (Master Edric, Master Arenis, Magus Isen, Yulia or Peze). `/epic`:
   7 SI is now "closed by …". Play it through, or `/epic goto` from stage to stage. At the end you get your
   reward, 5,500 XP and 7 silver.
3. For each later step: `/player level <step>`, `/epic goto` to the giver, take it, play it (or skip ahead with
   `/epic done <next step>`), and check the reward, the XP and the coin. Spot checks:
   - 15 is offered only after 11; 30 only after 25; 40 only after 30.
   - At 40, Rhodri's list has only weapons that exist; each one you whisper is given.
   - 45 only after 43, and 48 only after 45.
4. At 50: Captain Rhodri offers "Lord of Deceit". Lord Elidyn stands in the Ellyll ruins on the hill in the Pennine
   Mountains with his guards (the map marks the spot). Kill him (a group fight), go back to Rhodri with six free
   backpack slots: six armour pieces and 50 silver. With fewer free slots he says so and waits.
5. `/epic`: every step "finished". `/epic reset` clears it.

## 2. The Shrouded Isles route

On a new character (or after `/epic reset`): level 7, take Strange Beings from Carys in Caer Gothwaite. The classic 7
is closed. At 11, Shades and Shadows from Carys, then 15 from your Camelot trainer.

## 3. Other checks

- Final XP of other quests: finish any classic quest that isn't an epic step; its last stage now gives XP.
- With `/xp off`, a quest that gives XP can't be finished (upstream's rule): "Your XP is turned off, you must turn it
  on to complete this quest!".
- Another line: a Defenders of Albion character (Armsman, Scout, Friar or Theurgist) at 48 with only 43 finished
  isn't offered "Feast of the Decadent" 48 (`/epic` says it needs the 45).
- `/item create` each class's new rewards (the `cq_alb_…` ids in `deploy/bin/epic_chains_data.json`) and check the
  stats against `docs/fork/specs/2026-10-09-epic-chains-design.md`, section 4.
- Class locks: game masters skip them, so use a player account. Give a Mercenary an Infiltrator's reward (for example
  drop the Ring of Shades and pick it up with the Mercenary) and try to wear it: "Your class cannot use this item!".

Results go in `docs/fork/verification/sub4-ingame.md`.
```

- [ ] **Step 3: Commit**

```bash
git add docs/fork/FORK.md docs/fork/verification/sub4-test-guide.md
git commit -m "docs(fork): the epic chains in FORK.md, and the in-game test guide

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Finishing

- [ ] Run everything once more: the deploy, client, client patches and tools Python suites (with `HDC_TEST_WORLD`),
  the fork's C# filter, and, with Docker, `docker build -f deploy/Dockerfile -t hearthdaoc:ci .`,
  `deploy/tests/smoke.sh hearthdaoc:ci` and `deploy/tests/hdc_integration.sh hearthdaoc:ci`; remove the image after.
- [ ] Push `sub4-shadows-epic` and open the PR on `lometur/HearthDAoC` with
  `gh api -X POST repos/lometur/HearthDAoC/pulls -f title=... -f head=sub4-shadows-epic -f base=main -F body=@<file>`.
  The body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Nothing goes upstream.
