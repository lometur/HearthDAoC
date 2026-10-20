# Bot CPU meter: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The owner can see on the live server, at any time, the average CPU each kind of bot costs, and compare
today's GameBots with the player bots the prototype will add (issue #137). Every minute while the meter is on, the
server log gets one `BOT_CPU` line per kind (`gamebot`, `companion`, `playerbot`, `piloted`, `player`, `monster`,
`unattributed`; pets count with their owner's kind): the average CPU milliseconds per bot per second, split by
game-loop stage, with calls, allocation and the spread across bots; then the 5 costliest bots of each bot kind
(`BOT_CPU_TOP`), a whole-process line for cross-checking (`BOT_CPU_PROCESS`) and a ratio line for each pair of kinds
that can be compared (`BOT_CPU_COMPARE`). The same windows go to `/data/logs/bot-cpu.csv`. A GM's `/botcpu` turns
the meter on or off until the next start and shows the last minute in chat; `HEARTHDAOC_BOT_CPU` (default `off`)
starts it with the server. Off, the meter costs one bool read per `GameLoop.ExecuteForEach` call.

**Architecture:** `BotCpuMeter.cs` is the hot path. Hook H1 in `GameLoop.ExecuteForEach` hands it every pool call while
`Enabled`; it reads the stage from `GameLoop.ActiveService` once per call and wraps the callback, and the wrapper times
each item and charges it to its owner's kind and stage in the thread's `[ThreadStatic]` slot (plain adds, no lock) and,
for a bot kind, to that bot's padded counter in a per-run `ConditionalWeakTable`. The owner is read through a static
generic reader per item type, a pet's owners are walked at most 8 steps, and the pilot check is a parameter of the pure
core. Hook H6 scopes `NpcService.Tick`'s GameBot fleet planning, so that work is charged to `gamebot / Fleet`.
`BotCpuReport.cs` is pure: plain values in; every figure, line, CSV row and chat reply out, in the invariant culture.
`BotCpuScript.cs` is the report side, off the loop: the `[ServerProperty]` switch, the started and stopped handlers, one
report lock, a first-in-first-out job queue run through `ThreadPool.QueueUserWorkItem`, the 1-second timer that samples
the GameBot registry and closes 60-second windows, the `/proc` and cgroup readers (pure parsers of a file's text, plus
the reads), the CSV with its header check and rotation, and a catch around every step that turns the meter off with
`BOT_CPU_ERROR` rather than let an exception reach the server. `BotCpuCommand.cs` is `/botcpu`: it reads the result the
report published and queues jobs; on the loop, only `off` touches the meter (`BotCpuMeter.Disable()`). Upstream gets
five marked lines in two files (GL 2, NS 3); `HEARTHDAOC_BOT_CPU` reaches the property through
`deploy/bin/server_properties.py` at every start.

**Tech Stack:** C# (.NET 10, NUnit 4) for the server and its unit tests; Python 3 `unittest` source and deploy checks
in `deploy/tests`; GitHub Actions CI (`.github/workflows/server-image.yml`).

**Spec:** `docs/fork/specs/2026-10-14-bot-cpu-meter-design.md` (approved by the owner 2026-10-18 with the default of
every ruling; its cites were refreshed at main 5b5577a, v0.37b-hearth.1, in 439719f). Read it with this plan: it is
the source of truth, and the plan changes no design (where it leaves a point open, the reading taken is listed under
"Spec readings for the owner"). Every code block below was built and tested in a scratch copy of this branch before
the plan was written, task by task with TDD as the steps say, and each task was reviewed; every `Expected:` is the
output seen there. The tested commits are on the local branch `plan/bot-cpu-meter-build` in
`~/tmp/hearthdaoc-cpu-plan/build`, ending at 79236c9c78a08d2d8ff65b96ab545f43dc77ea7b: 11 task commits, 9 review
fixes and three last fixes of test gaps, which this plan folds into one commit per task (c28fae7's 5 test cases are
in Tasks 8 and 9; 54b35eb's posted-action test, which replaces Task 2's by reflection, and its `PopulationForBrainTick`
check are in Task 3, and its camp planner's thread name check, with 79236c9's, in Task 4). 345 new unit test cases
(`UT_BotCpuMeter` 124, `UT_BotCpuReport` 77, `UT_BotCpuScript` 144) and 29 deploy tests (`test_bot_cpu.py`); CI's
fork filter 989, the whole C# project 3,777, `deploy/tests` 773 OK (skipped=10). Each task's section was then dry-run
as written in a fresh copy (Tasks 1-7 each from the build's commit before it, Tasks 8-11 in sequence in one copy from
Task 7's commit), where every `Expected:` matched (only build warning totals vary) and the last tree is 79236c9's,
byte for byte (tree d9aa46f3).

## Global Constraints

- Work in `~/github/HearthDAoC-cpu`, branch `bot-cpu-meter` (main 5b5577a, v0.37b-hearth.1, merged in at dcc198e; the
  spec refreshed at 439719f; plus this plan's commit). Never commit to `main`; never push to or open anything on
  `shadowofze/OfflineDAoC` or OpenDAoC: nothing goes upstream (paused by the owner). Implementers don't push or open
  PRs; the controller does at the end. Implementers never dispatch subagents. Nobody contacts the owner's server; the
  live checks (Task 11's guide) are the owner's.
- The release notes' "To update" (spec 8) are the controller's, as for every release: the PR body gets a
  `## To update` section, and after the owner merges and CI publishes the release, the controller adds it to that
  release's notes, before `## What's Changed`: "Owner: `./hdc update` (it adds `HEARTHDAOC_BOT_CPU=off` to `.env`);
  nothing changes, the meter is off. To use it: `/botcpu on 30` in game, or `HEARTHDAOC_BOT_CPU=on` in `.env` and
  `./hdc up`. Players: nothing to do." No task writes it.
- Run every command, edit block and `git add` from the repository root, `~/github/HearthDAoC-cpu`, after checking that
  `git branch --show-current` prints `bot-cpu-meter` (if it prints another name, `git switch bot-cpu-meter`; if it
  prints nothing, you are in a detached checkout: stop and ask the controller).
- Every commit message is `type(scope): a plain lower-case sentence`, a wrapped prose body, and ends with the line
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The scope is `cpu-meter`. One commit per task, with the
  message its last step gives (`feat` for Tasks 1-10, `docs` for Task 11).
- **Line endings and BOMs stay exactly as they are** (spec 5.2 and 6.2 "Endings", measured at 439719f):
  - GL (`source/server/GameServer/Managers/GameLoop/GameLoop.cs`, OpenDAoC core) is CRLF with ten LF-only lines
    (39-41, 54-55, 125-126, 135-137) and no BOM; NS (`source/server/GameServer/ECS-Services/NpcService.cs`) has a BOM
    and CRLF throughout. Both stay so line by line, and the hook lines end in CRLF, like the upstream lines beside
    them. `deploy/tests/test_bot_cpu.py` checks it with `endings.check` (`deploy/tests/endings.py`) and the BOM
    pinned, against upstream's files at the `v0.37b` tag of `deploy/upstream.lock`; this worktree shares that tag with
    `~/github/HearthDAoC` (`git tag -l v0.37b` prints it). In a shallow clone the check fails on GL.
  - Every other existing file this plan edits (deploy, CI, deploy tests, docs) is LF, with no BOM and no tab.
  - New files are LF, no BOM, spaces, no tab, even though `source/server/.editorconfig` says CRLF and block-scoped: the
    four fork files `scripts/hearthdaoc/BotCpuMeter.cs`, `BotCpuReport.cs`, `BotCpuScript.cs`, `BotCpuCommand.cs`
    (file-scoped `namespace DOL.GS.HearthDAoC;`), the three `Tests/UnitTests/UT_BotCpu*.cs` (`namespace
    DOL.GS.Tests;`), `deploy/tests/test_bot_cpu.py` and `docs/fork/verification/bot-cpu-meter-live.md`. Each C# file
    has a `// HearthDAoC:` header comment naming the spec after its usings, like `scripts/hearthdaoc/RpTimerWait.cs`
    and `RpTimerWaitScript.cs`. Create them with the Write tool or a quoted heredoc exactly as given, each ending
    with one line end after its last line (a quoted heredoc adds it; with the Write tool, end the content with a
    newline). Each task's form check prints it as `True` (a last line end).
- Edit existing files **only** with the helper below, which keeps both the BOM and each line's ending; `git diff
  --stat` must show only your lines. Create it once, before Task 1, at
  `/home/farral/tmp/hearthdaoc-cpu-plan/hdc-edit.py` (outside the repository; every step calls it as
  `python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py FILE <<'EDIT'`). If the file is already there, it must be
  byte-identical to this block, which every build and dry run used (its docstring's `/tmp/hdc-edit.py` is only the
  usage example). The input holds one or more blocks, each `<<<<<<< OLD`, the old text, `=======`, the new text,
  `>>>>>>> NEW`, with LF line ends (matched as written, and as CRLF lines when the file has any); each old text must
  occur exactly once, or nothing is written. Expected after each run: `edited FILE (N block(s))`.

```python
#!/usr/bin/env python3
"""Replaces text in one file, keeping its BOM and each line's ending (CRLF or LF).

Usage: python3 /tmp/hdc-edit.py [--inline] FILE <<'EDIT' ... EDIT
The input holds one or more blocks, each:
<<<<<<< OLD
the old text (LF line ends; matched as written, and as CRLF lines when the file has any)
=======
the new text (written with the line ends of the lines it replaces)
>>>>>>> NEW
Each old text must occur exactly once in the file, as LF or as CRLF lines; otherwise nothing is written and the run
stops. A file of mixed endings (upstream's ClassicQuests.cs: 14 CRLF lines in an LF file) keeps them: a block matches
the lines it was written for and its new text takes their ending. With --inline, each block's old and new text lose
their last line end, so a block can change text inside one long line (deploy/hearthdaoc-quests.json's "_about").
"""
import sys

args = sys.argv[1:]
inline = args[:1] == ["--inline"]
if inline:
    args = args[1:]
if len(args) != 1:
    sys.exit("usage: hdc-edit.py [--inline] FILE")
path = args[0]
with open(path, "rb") as f:
    raw = f.read()
blocks = []
for part in sys.stdin.read().split("<<<<<<< OLD\n")[1:]:
    old, rest = part.split("=======\n", 1)
    new = rest.split(">>>>>>> NEW\n", 1)[0]
    if inline:
        old, new = old[:-1] if old.endswith("\n") else old, new[:-1] if new.endswith("\n") else new
    blocks.append((old, new))
if not blocks:
    sys.exit("no edit blocks")
for old, new in blocks:
    o, n = old.encode("utf-8"), new.encode("utf-8")
    forms = [(o, n)]
    if b"\r\n" in raw and b"\n" in o:
        forms.append((o.replace(b"\n", b"\r\n"), n.replace(b"\n", b"\r\n")))
    found = [(fo, fn) for fo, fn in forms if raw.count(fo)]
    count = sum(raw.count(fo) for fo, _ in forms)
    if count != 1:
        sys.exit(f"{path}: old text found {count} times: {old[:80]!r}")
    fo, fn = found[0]
    raw = raw.replace(fo, fn)
with open(path, "wb") as f:
    f.write(raw)
print(f"edited {path} ({len(blocks)} block(s))")
```

- No `InternalsVisibleTo`: the tests reach private or internal seams (the meter's hooks, the report side's fields,
  upstream's private members) by reflection, as the fork's tests already do.
- Test commands (each run in the scratch build and the dry runs while writing the plan):
  - Unit tests need `source/server/CoreServer/config/serverconfig.xml` (gitignored; never commit it). Once, if it is
    missing: `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`.
  - Build only: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
    (Expected: `Build succeeded.` and `0 Error(s)`; the warning total varies between builds and is not checked).
  - One class: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_BotCpuScript."`
    (the same with `UT_BotCpuMeter.` or `UT_BotCpuReport.`; the trailing dot keeps the name from matching a longer
    one). The three classes together: `--filter "FullyQualifiedName~UT_BotCpu"`.
  - CI's fork filter, taken from the workflow as it stands:
    `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
  - The whole C# project: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
  - Always give the `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` prefix: the owner's `~/.bashrc` exports `1`, under which
    `UT_BotCpuReport`'s `de-DE` text tests (and 9 existing `UT_ClassicBattlegrounds` tests) fail; CI's step sets `0`
    in its `env:`.
  - One deploy test file: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py` (the same
    with `-p test_workflows.py`, `-p test_server_properties.py` and the others a step names).
  - The whole deploy suite with the world tests, as CI's step runs it, on the 0.37b test data at
    `~/tmp/hearthdaoc-sync037/td`:
    `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
    (about three minutes). `env.sh` sets `TD` and defines `t`, which runs one file with the three variables
    (`. ~/tmp/hearthdaoc-sync037/td/env.sh && t test_container_lines`); it exports nothing, so the whole run sets the
    three itself. Without them most world tests skip.
  - Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is locked" when the
    machine is busy. Never run two `dotnet` builds or test runs in the same worktree at once, and run the suites one
    after the other; if it fails, rerun it alone with
    `--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` (Expected:
    `Passed:     1`) and say so.
- `UT_BotCpuMeter` and `UT_BotCpuScript` are `[TestFixture, NonParallelizable]` and build game objects without their
  constructor. `GameObject`'s type initializer reads the database (`FillDataQuestCache`), so their `[SetUp]` first
  loads an `EmptyServer` test double, as `UT_RpTimerWaitScript` does (Task 1 gives the code); without it the first
  game object throws `TypeInitializationException` when the class runs alone.
- The spec's values (owner-approved; do not change them):
  - The meter is off by default. Property `bot_cpu_meter`, category `server`, declared in `BotCpuScript` as `public
    static bool BOT_CPU_METER`, code default `false`, description "Measure the game loop's CPU time per kind of bot
    and player and write BOT_CPU lines to the server log every minute; a GM's /botcpu turns it on or off until the
    next start" (the same string in `server_properties.py`). The server reads it once, at start; `/botcpu on|off`
    overrides it until the next start and never writes it.
  - `HEARTHDAOC_BOT_CPU`: `on` or `off`, case ignored, default `off` at every step (`.env`, `compose.yml`,
    `entrypoint.sh`); a bad value stops the start with exit 2 and "ERROR: HEARTHDAOC_BOT_CPU must be on or off, got
    '…'" before any write. `server_properties.py` prints "Bot CPU meter: on" (or "off") when it changes the row.
  - The log lines (`BOT_CPU`, `BOT_CPU_TOP`, `BOT_CPU_PROCESS`, `BOT_CPU_COMPARE`, `BOT_CPU_STATE`, `BOT_CPU_ERROR`),
    the CSV's header and rows and the chat replies are the spec's 2.1, 2.3 and 2.4, exactly; Tasks 6, 8, 9 and 10
    give their code.
  - Upstream: H1 (GL, a comment and the guard line) and H6 (NS, a comment and `BeginFleet`, then `EndFleet` with a
    trailing comment), each marked `// HearthDAoC:`; `BotCpu` appears in no other file under `GameServer/` outside
    `scripts/hearthdaoc/`.
- Out of scope (state it, don't build it): the player-bot prototype; `/botcpu heap` and retained heap per bot (spec
  4.5); hooks H4, H5 and H8 (3.6); charging effects to their caster; wrapping off-loop work such as the ClassicQuests
  refresh; exporting the numbers elsewhere (spec 10).

## Review Focus

The spec's test list leaves these inputs open; a person using the meter would expect the behaviour given. Each is
pinned by a test in the task named:

1. **Arguments in another case** (`/botcpu ON 30`, `/botcpu top GameBot`): accepted, case ignored, as
   `HEARTHDAOC_BOT_CPU` ignores case. Task 10: `UT_BotCpuScript.OnInAnotherCaseIsAccepted` (`ON 30` and `On 30`, then
   `Reset` and `OFF`) and `ATopKindInAnotherCaseIsAccepted` (`top GameBot`, `TOP GAMEBOT`, `Top gamebot`, then
   `top Companion`).
2. **The CSV's folder missing or not writable** (a launcher build whose `logs` folder does not exist yet, a read-only
   volume): the folder is created when missing; a write that still fails logs one warning and the meter goes on, with
   the next minute's rows written once the folder is writable. Task 8, in `UT_BotCpuScript`:
   `TheMissingFolderIsCreatedAndTheNewFileStartsWithTheHeader`,
   `AFolderThatCannotBeCreatedLogsOneWarningAndTheNextRowsAreWrittenOnceItCan` (a file holds the folder's name) and
   `AFolderThatIsNotWritableLogsOneWarningAndTheNextRowsAreWrittenOnceItIs` (a read-only folder; Inconclusive on
   Windows or as root), with `AFolderWhereTheFileShouldBeLogsOneWarning` and `ANonIoExceptionLogsOneWarningToo` for
   a failure that is not an `IOException`.
3. **A bot that leaves the world mid-window** (its counter collected, or simply absent from the next listing): the
   window, the spread and the top 5 are still written from the bots present, with no exception and no stale entry.
   Task 5: `UT_BotCpuReport.ABotAbsentFromThisListingLeavesNoStaleEntry` (a bot present in the previous listing and
   absent from this one); Task 9: `UT_BotCpuScript.ABotThatLeavesTheWorldMidWindowLeavesNoStaleEntry` (its counter
   removed from the per-bot table, through a tick).
4. **No platform values at all** (Windows, or every `/proc` and cgroup read failing): the process line shows `-` for
   those fields and the kind lines, the top lines and the CSV rows are still written. Task 9:
   `UT_BotCpuScript.ATickWithEveryPlatformReaderFailingStillWritesTheKindAndTopLinesAndTheRows` (`/proc` and the
   cgroup file at missing paths; the CSV's cells for those fields empty; the meter still on).
5. **A burst of commands inside one second** (on, off, on, off): one `BOT_CPU_STATE` line per change in order, no
   window written twice, the meter ends off with its timer disposed. Task 9:
   `UT_BotCpuScript.ABurstOfCommandsWritesOneStateLinePerChangeAndEndsOffWithItsTimerDisposed` (the four work items
   on the real thread pool: exactly the four state lines, no job left, both timers disposed, the meter off).

## Spec readings for the owner

Where the spec leaves a point open or says two things, the build settled it without a design change. **Confirm**
marks the ones that change what the owner sees; the others need no answer (the spec's text could be tidied later).

| Spec | What it leaves open or says differently | What the plan does | Owner |
|---|---|---|---|
| 2.1, 2.3, 3.1 | No text for the two warnings (the setup fallback, a failed CSV write) | Both start `Bot CPU meter:`, with no `BOT_CPU_` token, so neither is mistaken for `BOT_CPU_ERROR` (which means off); the owner's `grep BOT_CPU` does not show them. A prefix such as `BOT_CPU_CSV_FAILED` would add a line type to 2.1 (Tasks 3, 8) | **Confirm** |
| 3.1, 5.1 | "One warning" from the setup fallback: once a process or once a run? | Once a run, reset by `Enable()`, since the log keeps 7 days; once a process is a one-line change (Task 3) | **Confirm** |
| 2.4, 6.5 | Check 1 says a default start leaves no `BOT_CPU` line, but `/botcpu`'s load line at every start holds `BOT_CPU` (its description: "(BOT_CPU lines in the server log)") | The guide's check 1 adds `grep -v "Command - '&botcpu'"`; the smoke test greps `BOT_CPU_STATE` (Tasks 10, 11) | **Confirm** |
| 2.1 | The sample lines are not in tick order (player `ClientBegin` before `Timer`, monster `Movement` before `Effect`) | Follows the rule: tick order, the `BotCpuStage` and CSV order (Task 6) | **Confirm** |
| 2.4 | The since-on sample lists gamebot and player only; its rule ("each kind with bot-seconds above 0") includes monster | Follows the rule: `... gamebot 1.792, player 2.020, monster 0.052 ms/s per bot.` (Task 6) | **Confirm** |
| 2.1 | Whether the top 5's rank and the compare line use raw values or the values the lines show | As shown: rank by the 3-decimal ms, ties by name; a pair needs `n` of at least 1.0 as shown (0.96 counts; 0.95 shows as 0.9 and does not); ratios of the shown fields; a gamebot value that shows 0 gives `-` (Task 5) | **Confirm** |
| 2.2, 3.7 | An off command's final window ends at the command, but `/proc` is never read on the loop | `win` and the kind lines end at the command; `BOT_CPU_PROCESS` rates divide by their own samples' interval, and `gcPauseMs`, `gen2`, `loopWaitMs`, `throttledMs` count up to the off job, normally milliseconds later (Task 9) | **Confirm** |
| 2.3, 6.1 | 2.3 renames an "empty" file to `bot-cpu.csv.1`; 6.1 lists "an empty file gets the header" apart | Follows 6.1: an empty file gets the header in place and `bot-cpu.csv.1` is kept, so up to 50 MB of history is not replaced by nothing (Task 8) | **Confirm** |
| 3.9 | `campThreadCpuPct` when no `CampSnapshotPlanner` thread exists (it is made lazily: at start, and with GameBots off) | `0.0`; `-` stays for a value the platform can't give. A thread that starts in the window counts from zero (Task 8) | **Confirm** |
| 3.8 | "Bot-seconds = n × win", where 2.1 (`msPerBotSec = msSec / n`) and 2.2 need the exact length | `n` × the window's exact length: a 61.4 s window is `win=61` and divides by 61.4 (Task 5) | No |
| 2.1, 6.1 | A bot kind with bot-seconds above 0 but no bot charged: 6.1's "no bot gives no fields" | `p50=- p95=- max=-` on the line, an empty CSV cell; player, monster and unattributed never carry them (Tasks 5, 6) | No |
| 2.1 | "No part after `\|`" | No ` \| ` at all, also when every stage rounds to 0.000 (Task 6) | No |
| 5.1 | "The stage map [is] created by the first `Enable()`" | `StageOf` is a switch expression: nothing to create, and it works before any `Enable()` (Task 1) | No |
| 2.5, 3.5 | "With no owner, inside the scope": a non-living owner or a pet cycle? | Anything that would be unattributed goes to `gamebot / Fleet` inside the scope; only `int` items run there today (Task 2) | No |
| 3.2 | Whether an item whose callback throws is charged | Not charged; the exception reaches the service as today (Task 3) | No |
| 2.4 | The stop time across a summer-time change | The local time of the instant the run stops, through UTC; "the first lines come at" adds 60 s in local time, wrong only for a minute across the change (Tasks 9, 10) | No |

## Files

`git diff --stat 439719f 79236c9c78a08d2d8ff65b96ab545f43dc77ea7b` in the build: 24 files changed, 9,676 insertions,
22 deletions.

| File | Task | Responsibility |
|---|---|---|
| `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs` (new) | 1-4 | The hot path: `IPlayerBotClient`, `BotCpuKind`, `BotCpuStage`, `StageOf`, `KindOf` (1); `BotCpuCharge`, `IsBotKind`, `Classify` (2); `Enabled`, `Enable()`, `Disable()`, the run number, the slots, `BotCpuTotals`, `BotCpuCounter`, the per-bot table, `TakeSnapshot()`, `ExecuteForEach` and its wrapper, the setup fallback (3); `BeginFleet`, `EndFleet` and the nested pool call's timing (4) |
| `source/server/Tests/UnitTests/UT_BotCpuMeter.cs` (new) | 1-4 | 124 cases: kinds and stages (1), each item's charge (2), the wrapper, runs, slots and counters (3), the fleet scope (4) |
| `source/server/GameServer/Managers/GameLoop/GameLoop.cs` (GL; CRLF with ten LF-only lines, no BOM) | 3 | Hook H1 in `ExecuteForEach`: a comment and the guard line |
| `source/server/GameServer/ECS-Services/NpcService.cs` (NS; BOM, CRLF) | 4 | Hook H6 in `Tick`: a comment and `BeginFleet`, then `EndFleet` with a trailing comment |
| `source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs` (new) | 5, 6 | Pure: the registry samples, a listed bot, `n` and bot-seconds, every figure of a window, the spread, the top 5, the compare pairs, the totals since on (5); the text of every line, the CSV header and rows and every chat reply, in the invariant culture (6) |
| `source/server/Tests/UnitTests/UT_BotCpuReport.cs` (new) | 5, 6 | 77 cases: the numbers (5), the text, under `de-DE` (6) |
| `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs` (new) | 7, 8, 9 | The `bot_cpu_meter` property (7); the guarded log writer, the `/proc` and cgroup parsers and reads, the process sample and window, the CSV append with its header check and 50 MB rotation (8); the report side: the lock, the job queue, runs, the 1-second timer, windows, the stop time, errors, the started and stopped handlers, the published `BotCpuResult` (9) |
| `source/server/Tests/UnitTests/UT_BotCpuScript.cs` (new) | 7-10 | 144 cases: the property (7), the readers and the CSV (8), the report side (9), `/botcpu` (10) |
| `source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs` (new) | 10 | `/botcpu` for GMs (spec 2.4): reads the published result, `Disable()` for `off`, queues jobs |
| `deploy/bin/server_properties.py`, `deploy/entrypoint.sh`, `deploy/compose.yml`, `deploy/.env.example` | 7 | `HEARTHDAOC_BOT_CPU` (on or off, default off) to `bot_cpu_meter`, at every start |
| `deploy/HANDOFF.md` | 7, 11 | The setting and the `./hdc update` line (7); the Day-to-day "Bot CPU meter" paragraph (11) |
| `deploy/tests/test_server_properties.py`, `deploy/tests/test_relic_pad.py`, `deploy/tests/test_rp_timer_wait.py`, `deploy/tests/test_si_start_choice.py` | 7 | Their `server_properties.py` calls gain `--bot-cpu off`; a new world's output gains "Bot CPU meter: off" |
| `deploy/tests/test_bot_cpu.py` (new) | 3, 4, 7, 10, 11 | 29 tests: H1, the upstream facts and GL's endings (3); H6, the nesting, the camp planner's thread name, `BotCpu` in no other upstream file, NS's endings (4); the switch's deploy checks (7); `smoke.sh`'s two lines (10); the guides' per-line rule, the record and the new files' endings (11) |
| `deploy/tests/smoke.sh` | 10 | `&botcpu` loaded at plvl 2, and no `BOT_CPU_STATE` line after a default start |
| `.github/workflows/server-image.yml`, `deploy/tests/test_workflows.py` | 1, 5, 7 | CI's fork filter runs `UT_BotCpuMeter` (1), `UT_BotCpuReport` (5) and `UT_BotCpuScript` (7) |
| `docs/fork/FORK.md` | 11 | The meter's row, after the realm point waits row |
| `docs/fork/verification/bot-cpu-meter-live.md` (new) | 11 | The owner's checks on the live server (spec 6.5), with log reads by 6.2's rule |

## Baseline and totals

At 439719f (measured 2026-10-19, before Task 1): build `0 Error(s)`; CI's fork filter `Passed:   644`; the whole C#
project `Passed:  3432`; the deploy suite with the world `Ran 744 tests`, `OK (skipped=10)`.

After each task (its new unit cases, then the totals its Step 6 expects; `-` for a file not made yet, or where the
task does not run the whole deploy suite because it adds no deploy test):

| Task | New unit cases | `test_bot_cpu.py` | CI's fork filter | Whole C# | Deploy suite |
|---|---|---|---|---|---|
| 1 | 44 | - | 688 | 3,476 | 744 |
| 2 | 49 | - | 737 | 3,525 | 744 |
| 3 | 19 (22 new, 3 of Task 2's replaced) | 6 | 756 | 3,544 | 750 |
| 4 | 12 | 10 | 768 | 3,556 | 754 |
| 5 | 50 | 10 | 818 | 3,606 | 754 |
| 6 | 27 | 10 | 845 | 3,633 | - |
| 7 | 1 | 21 | 846 | 3,634 | 765 |
| 8 | 47 | 21 | 893 | 3,681 | - |
| 9 | 61 | 21 | 954 | 3,742 | - |
| 10 | 35 | 24 | 989 | 3,777 | 768 |
| 11 | 0 | 29 | 989 | 3,777 | 773 |

At the end: build `0 Error(s)`; `UT_BotCpu` 345 (`UT_BotCpuMeter` 124, `UT_BotCpuReport` 77, `UT_BotCpuScript` 144);
CI's fork filter 989; the whole C# project 3,777; the deploy suite 773, `OK (skipped=10)`; `test_workflows.py` 18 as
before. Under heavy load (a busy loop on every core and a process allocating 256 MB over and over),
`UT_BotCpuScript` passed 15 runs of 15.

---

### Task 1: Kinds, stages and the owner's kind

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs` (the player-bot marker, the kinds, the stages,
  `StageOf` and `KindOf`; Tasks 2-4 add the rest of the hot path to `BotCpuMeter`)
- Create: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs` (the test double server, the object builders and 20
  tests, 44 cases)
- Modify: `.github/workflows/server-image.yml:119` (CI runs the new class)
- Modify: `deploy/tests/test_workflows.py:279`, `:309` (the pinned filter and its list of names)
- Test: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs`, `deploy/tests/test_workflows.py`

**Spec:** 2.5 (the kinds, the player-bot contract), 3.3 ("Kind of an owner"), 3.4 (the stages and their profile
keys), 5.1 (no static initializer), 6.1 (the classification rows about owners, the stage rows), 6.4 (`UT_BotCpuMeter`
in CI).

**Interfaces:**
- Consumes: no earlier task. Upstream, read only (paths under `source/server/GameServer/`):
  `GameBot.IsAutonomousWorldBot` (`bots/GameBot.cs:47`), `GamePlayer.Client`, `BotDummyClient`
  (`bots/BotDummyClient.cs:6`), `GameNPC.Brain` (`gameobjects/GameNPC.cs:2244-2253`, no lock),
  `IControlledBrain.Owner` (`ai/brain/IControlledBrain.cs:67`) and
  `public static bool AutonomousPlayerPilot.IsActive(GamePlayer player)`
  (`bots/autonomous/AutonomousPlayerPilot.cs:89`).
  The tests also use `GameServer.LoadTestDouble(GameServer)` (`GameServer.cs:253`), `NormalServerRules`,
  `IObjectDatabase`, and by reflection `AutonomousPlayerPilot`'s private `Pilots` table
  (`bots/autonomous/AutonomousPlayerPilot.cs:32`) and nested `PilotState`, `GameNPC.m_ownBrain` and `m_brains`
  (`gameobjects/GameNPC.cs:2234`, :2239), `ControlledMobBrain.m_owner`
  (`ai/brain/ControlledMob/ControlledMobBrain.cs:38`), `GamePlayer.m_client` (`gameobjects/GamePlayer.cs:58`) and
  `GameBot`'s backing fields `<IsAutonomousWorldBot>k__BackingField` and `<Owner>k__BackingField`
  (`bots/GameBot.cs:47`, :38).
- Produces, in `namespace DOL.GS.HearthDAoC` (file `scripts/hearthdaoc/BotCpuMeter.cs`):
  - `public interface IPlayerBotClient { }`, the player-bot contract (2.5).
  - `public enum BotCpuKind { Gamebot, Companion, Playerbot, Piloted, Player, Monster, Unattributed }`: values 0-6 in
    the report's kind order. The text name is the member name in lower case. The bot kinds (3.10) are the first four,
    so `kind <= BotCpuKind.Piloted`.
  - `public enum BotCpuStage { GameLoop, Timer, ClientBegin, Npc, Fleet, Attack, Casting, Effect, EffectListBegin,
    EffectListEnd, Movement, Crafting, Reaper, Zone, ClientEnd, Quests, Pets, Other }`: values 0-17 in the CSV's column
    order (2.3). The text name is the member name.
  - `public static class BotCpuMeter`, with no static field initializer and no static constructor, holding:
    - `public const int PetWalkSteps = 8;`
    - `public static BotCpuStage StageOf(string profileKey)`: the 17 profile keys of 3.4 by exact ordinal match; any
      other key, `""` or null gives `Other`. It never returns `Fleet` or `Pets`.
    - `public static BotCpuKind KindOf(GameObject owner, Func<GamePlayer, bool> isPiloted, out GameObject charged,
      out bool pet)`: the pure core. `charged` is the object whose kind is returned (for a pet, the walk's end) for
      every kind, and null only for `Unattributed`, so Task 2's `Classify` sets its `Bot` from it for the bot kinds
      only. `pet` is true when the walk followed at least one owner and resolved. A null `isPiloted` counts as false.
      It reads `GameNPC.Brain` and `Owner` with no lock and can throw on the live server, so its callers run it inside
      the `try` of 3.2 step 1.
    - `public static BotCpuKind KindOf(GameObject owner, out GameObject charged, out bool pet)`: the core with
      `AutonomousPlayerPilot.IsActive`; it allocates nothing once warm.
  - In `UT_BotCpuMeter`, for later tasks: `[SetUp] SaveGlobals()` and `[TearDown] RestoreGlobals()` (the test double
    server; Task 3 extends them for `_threadPool`, `ActiveService` and `Disable()`), `Make<T>()`,
    `Set(Type type, object target, string name, object value)`, `Npc(ABrain brain)`, `PetBrain(GameLiving owner)`,
    `Pet(GameLiving owner)`, `Player(GameClient client)`, `Bot(bool autonomous, GamePlayer owner = null)`, the nested
    `TestDummyClient : BotDummyClient` and `TestPlayerBotClient : GameClient, IPlayerBotClient`, `NotPiloted`,
    `Piloted` and `AssertKind(GameObject owner, BotCpuKind kind, GameObject charged, bool pet, bool piloted = false)`.

- [ ] **Step 1: Write the failing tests.** Create `source/server/Tests/UnitTests/UT_BotCpuMeter.cs` with exactly the
code below. `GameObject`'s type initializer reads the database (`FillDataQuestCache`), so `SaveGlobals` loads an
`EmptyServer` test double before any game object is made, as `UT_RpTimerWaitScript` does; without it, 20 of the 44
cases fail with a `TypeInitializationException` when the class runs alone. The game objects are built without their
constructors (`Make<T>`) and their private fields are set by reflection (`Set`). A pet is a `GameNPC` whose own brain
is a `ControlledMobBrain` with `m_owner` set. A `GameBot` gets a real `BotBrain`, itself an `IControlledBrain`, and its
`IsAutonomousWorldBot` and `Owner` backing fields. The profile keys are built with `nameof` of the real services, so an
upstream rename breaks the test build instead of mapping quietly to `Other`. `TheRealPilotCheckIsAutonomousPlayerPilot`
registers a pilot the way `AutonomousPlayerPilot.Enable` does, with an entry in its private `Pilots` table, and removes
it in a `finally`.

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.HearthDAoC;
using DOL.GS.Keeps;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.1). While it is on,
// each game-loop callback is charged to the kind of its owner and to its stage. These tests pin the kinds and stages
// in their report order (2.5, 2.3), the stage of each profile key (3.4) and the kind of each owner, pets included
// (3.3), with game objects built without their constructors and their fields set by reflection.
[TestFixture, NonParallelizable]
public sealed class UT_BotCpuMeter
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    // GameObject's type initializer reads the database (FillDataQuestCache): a test double server with an empty
    // database, as UT_RpTimerWaitScript does, before any game object is made.
    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    public class EmptyReads : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            Type result = method.ReturnType;
            if (result.IsGenericType && typeof(IEnumerable).IsAssignableFrom(result))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GetGenericArguments()[0]));
            return null;
        }
    }

    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();

    private GameServer _savedServer;

    [SetUp]
    public void SaveGlobals()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }

    [TearDown]
    public void RestoreGlobals() => GameServer.LoadTestDouble(_savedServer);

    // A client of the prototype's first kind: derived from BotDummyClient (2.5).
    private sealed class TestDummyClient : BotDummyClient { }

    // A client of the contract's kind: a plain GameClient marked with IPlayerBotClient (2.5).
    private sealed class TestPlayerBotClient : GameClient, IPlayerBotClient
    {
        public TestPlayerBotClient() : base((Socket)null) { }
    }

    private static T Make<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Set(Type type, object target, string name, object value) =>
        type.GetField(name, Hidden)!.SetValue(target, value);

    private static GameNPC Npc(ABrain brain)
    {
        GameNPC npc = Make<GameNPC>();
        Set(typeof(GameNPC), npc, "m_brains", new ArrayList());
        Set(typeof(GameNPC), npc, "m_ownBrain", brain);
        return npc;
    }

    private static ControlledMobBrain PetBrain(GameLiving owner)
    {
        ControlledMobBrain brain = Make<ControlledMobBrain>();
        Set(typeof(ControlledMobBrain), brain, "m_owner", owner);
        return brain;
    }

    private static GameNPC Pet(GameLiving owner) => Npc(PetBrain(owner));

    private static GamePlayer Player(GameClient client)
    {
        GamePlayer player = Make<GamePlayer>();
        Set(typeof(GamePlayer), player, "m_client", client);
        return player;
    }

    // A GameBot with its BotBrain (itself an IControlledBrain, bots/BotBrain.cs:21) and, for a companion, the player
    // it serves: the walk must stop at the bot, not go on to that player.
    private static GameBot Bot(bool autonomous, GamePlayer owner = null)
    {
        GameBot bot = Make<GameBot>();
        Set(typeof(GameNPC), bot, "m_brains", new ArrayList());
        Set(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", autonomous);
        Set(typeof(GameBot), bot, "<Owner>k__BackingField", owner);
        Set(typeof(GameNPC), bot, "m_ownBrain", new BotBrain { Body = bot });
        return bot;
    }

    private static bool NotPiloted(GamePlayer player) => false;

    private static bool Piloted(GamePlayer player) => true;

    private static void AssertKind(GameObject owner, BotCpuKind kind, GameObject charged, bool pet,
        bool piloted = false)
    {
        BotCpuKind found = BotCpuMeter.KindOf(owner, piloted ? Piloted : NotPiloted, out GameObject foundCharged,
            out bool foundPet);
        Assert.That(found, Is.EqualTo(kind));
        Assert.That(foundCharged, Is.SameAs(charged));
        Assert.That(foundPet, Is.EqualTo(pet));
    }

    [Test]
    public void TheKindsAreInTheReportOrderAndTheirNamesAreLowerCase()
    {
        Assert.That(Enum.GetNames<BotCpuKind>().Select(name => name.ToLowerInvariant()),
            Is.EqualTo(new[] { "gamebot", "companion", "playerbot", "piloted", "player", "monster", "unattributed" }));
        Assert.That(Enum.GetValues<BotCpuKind>().Select(kind => (int)kind), Is.EqualTo(Enumerable.Range(0, 7)));
    }

    [Test]
    public void TheStagesAreTheCsvStageColumnsInOrder()
    {
        // The stage columns of the CSV header (2.3).
        string[] columns = ("GameLoop,Timer,ClientBegin,Npc,Fleet,Attack,Casting,Effect,EffectListBegin," +
            "EffectListEnd,Movement,Crafting,Reaper,Zone,ClientEnd,Quests,Pets,Other").Split(',');
        Assert.That(Enum.GetNames<BotCpuStage>(), Is.EqualTo(columns));
        Assert.That(Enum.GetValues<BotCpuStage>().Select(stage => (int)stage), Is.EqualTo(Enumerable.Range(0, 18)));
    }

    // The profile key is the service's class name (GameServiceBase.ServiceName), plus .BeginTick or .EndTick for those
    // steps (GameLoop.BuildTickSequence).
    [TestCase(nameof(GameLoopService), BotCpuStage.GameLoop)]
    [TestCase(nameof(TimerService), BotCpuStage.Timer)]
    [TestCase(nameof(ClientService) + "." + nameof(ClientService.BeginTick), BotCpuStage.ClientBegin)]
    [TestCase(nameof(NpcService), BotCpuStage.Npc)]
    [TestCase(nameof(AttackService), BotCpuStage.Attack)]
    [TestCase(nameof(CastingService), BotCpuStage.Casting)]
    [TestCase(nameof(EffectService), BotCpuStage.Effect)]
    [TestCase(nameof(EffectListService) + "." + nameof(EffectListService.BeginTick), BotCpuStage.EffectListBegin)]
    [TestCase(nameof(EffectListService) + "." + nameof(EffectListService.EndTick), BotCpuStage.EffectListEnd)]
    [TestCase(nameof(MovementService), BotCpuStage.Movement)]
    [TestCase(nameof(CraftingService), BotCpuStage.Crafting)]
    [TestCase(nameof(ReaperService), BotCpuStage.Reaper)]
    [TestCase(nameof(ZoneService), BotCpuStage.Zone)]
    [TestCase(nameof(ClientService) + "." + nameof(ClientService.EndTick), BotCpuStage.ClientEnd)]
    [TestCase(nameof(DailyQuestService), BotCpuStage.Quests)]
    [TestCase(nameof(WeeklyQuestService), BotCpuStage.Quests)]
    [TestCase(nameof(MonthlyQuestService), BotCpuStage.Quests)]
    public void EachProfileKeyHasItsStage(string profileKey, BotCpuStage stage) =>
        Assert.That(BotCpuMeter.StageOf(profileKey), Is.EqualTo(stage));

    [TestCase("SomeNewService")]
    [TestCase("npcservice")]
    [TestCase("")]
    [TestCase(null)]
    public void AnUnknownMissingOrEmptyProfileKeyIsOther(string profileKey) =>
        Assert.That(BotCpuMeter.StageOf(profileKey), Is.EqualTo(BotCpuStage.Other));

    [Test]
    public void TheMeterHasNoStaticInitializer() =>
        Assert.That(typeof(BotCpuMeter).TypeInitializer, Is.Null, "nothing in BotCpuMeter may run while off (5.1)");

    [TestCase(true, BotCpuKind.Gamebot)]
    [TestCase(false, BotCpuKind.Companion)]
    public void AGameBotIsGamebotWhenAutonomousElseCompanion(bool autonomous, BotCpuKind kind)
    {
        GameBot bot = Bot(autonomous, autonomous ? null : Player(Make<GameClient>()));
        AssertKind(bot, kind, bot, pet: false);
    }

    [TestCase(typeof(TestDummyClient), false)]
    [TestCase(typeof(TestDummyClient), true)]
    [TestCase(typeof(TestPlayerBotClient), false)]
    [TestCase(typeof(TestPlayerBotClient), true)]
    public void APlayerWithABotClientIsPlayerbot(Type client, bool piloted)
    {
        GamePlayer player = Player((GameClient)RuntimeHelpers.GetUninitializedObject(client));
        AssertKind(player, BotCpuKind.Playerbot, player, pet: false, piloted);
    }

    [TestCase(false, BotCpuKind.Player)]
    [TestCase(true, BotCpuKind.Piloted)]
    public void APlayerWithAPlainClientIsPilotedWhenThePilotCheckSaysSoElsePlayer(bool piloted, BotCpuKind kind)
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertKind(player, kind, player, pet: false, piloted);
    }

    [Test]
    public void ThePilotCheckIsAskedAboutThatPlayer()
    {
        GamePlayer player = Player(Make<GameClient>());
        GamePlayer asked = null;
        BotCpuMeter.KindOf(player, p => { asked = p; return false; }, out _, out _);
        Assert.That(asked, Is.SameAs(player));
    }

    [Test]
    public void TheRealPilotCheckIsAutonomousPlayerPilot()
    {
        GamePlayer player = Player(Make<GameClient>());
        Assert.That(BotCpuMeter.KindOf(player, out GameObject charged, out bool pet), Is.EqualTo(BotCpuKind.Player));
        Assert.That(charged, Is.SameAs(player));
        Assert.That(pet, Is.False);

        // A pilot as AutonomousPlayerPilot.Enable registers one: an entry in its Pilots table, which IsActive reads
        // (bots/autonomous/AutonomousPlayerPilot.cs:32, :89).
        IDictionary pilots = (IDictionary)typeof(AutonomousPlayerPilot)
            .GetField("Pilots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        pilots[player] = RuntimeHelpers.GetUninitializedObject(
            typeof(AutonomousPlayerPilot).GetNestedType("PilotState", BindingFlags.NonPublic)!);
        try
        {
            Assert.That(BotCpuMeter.KindOf(player, out charged, out pet), Is.EqualTo(BotCpuKind.Piloted));
            Assert.That(charged, Is.SameAs(player));
        }
        finally
        {
            pilots.Remove(player);
        }
    }

    [Test]
    public void TheRealPilotCheckAllocatesNothing()
    {
        GamePlayer player = Player(Make<GameClient>());
        BotCpuMeter.KindOf(player, out _, out _); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuMeter.KindOf(player, out _, out _);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void AnyOtherLivingIsMonster()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        AssertKind(monster, BotCpuKind.Monster, monster, pet: false);
        GameNPC brainless = Npc(null);
        AssertKind(brainless, BotCpuKind.Monster, brainless, pet: false);
        GameKeepDoor door = Make<GameKeepDoor>();
        AssertKind(door, BotCpuKind.Monster, door, pet: false);
    }

    [Test]
    public void AControlledBrainWithNoOwnerIsMonster()
    {
        GameNPC npc = Pet(null);
        AssertKind(npc, BotCpuKind.Monster, npc, pet: false);
    }

    [Test]
    public void APetOfAPlayerIsPlayer()
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertKind(Pet(player), BotCpuKind.Player, player, pet: true);
        AssertKind(Pet(player), BotCpuKind.Piloted, player, pet: true, piloted: true);
    }

    [Test]
    public void APetOfAPetOfAnAutonomousGameBotIsGamebot()
    {
        GameBot bot = Bot(autonomous: true);
        AssertKind(Pet(Pet(bot)), BotCpuKind.Gamebot, bot, pet: true);
    }

    [Test]
    public void APetOfACompanionIsCompanionNotTheCompanionsPlayer()
    {
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        AssertKind(Pet(companion), BotCpuKind.Companion, companion, pet: true);
    }

    [Test]
    public void APetOfAMonsterIsMonster()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        AssertKind(Pet(monster), BotCpuKind.Monster, monster, pet: true);
    }

    [Test]
    public void TheWalkFollowsEightOwnersAndNoMore()
    {
        GamePlayer player = Player(Make<GameClient>());
        GameLiving owner = player;
        for (int i = 0; i < 8; i++)
            owner = Pet(owner);
        AssertKind(owner, BotCpuKind.Player, player, pet: true);
        AssertKind(Pet(owner), BotCpuKind.Unattributed, null, pet: false);
    }

    [Test]
    public void APetCycleIsUnattributed()
    {
        GameNPC a = Npc(null);
        GameNPC b = Pet(a);
        Set(typeof(GameNPC), a, "m_ownBrain", PetBrain(b));
        AssertKind(a, BotCpuKind.Unattributed, null, pet: false);
    }

    [Test]
    public void NoOwnerOrANonLivingOwnerIsUnattributed()
    {
        AssertKind(null, BotCpuKind.Unattributed, null, pet: false);
        AssertKind(Make<GameStaticItem>(), BotCpuKind.Unattributed, null, pet: false);
        Assert.That(BotCpuMeter.KindOf(null, out GameObject charged, out bool pet),
            Is.EqualTo(BotCpuKind.Unattributed));
        Assert.That(charged, Is.Null);
        Assert.That(pet, Is.False);
    }
}
```

Then pin the new class in the workflow check: its copy of CI's filter string, and its list of names (each must be a
file holding `public sealed class UT_X`). CI's own filter follows in Step 3.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_workflows.py <<'EDIT'
<<<<<<< OLD
                     '|FullyQualifiedName~UT_QuestGrants"')
=======
                     '|FullyQualifiedName~UT_QuestGrants'
                     '|FullyQualifiedName~UT_BotCpuMeter"')
>>>>>>> NEW
<<<<<<< OLD
                                 "UT_QuestGrants"])
=======
                                 "UT_QuestGrants",
                                 "UT_BotCpuMeter"])
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_workflows.py (2 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 26 errors, all in `UT_BotCpuMeter.cs`. Each error line starts with the
file's absolute path and ends with the project's. The first five, in the order seen:

```
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(114,54): error CS0246: The type or namespace name 'BotCpuKind' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(161,62): error CS0246: The type or namespace name 'BotCpuStage' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(177,79): error CS0246: The type or namespace name 'BotCpuKind' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(195,97): error CS0246: The type or namespace name 'BotCpuKind' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(64,60): error CS0246: The type or namespace name 'IPlayerBotClient' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

Then 21 `error CS0103` lines: `The name 'BotCpuStage' does not exist in the current context` 17 times (lines 144-160,
the profile-key cases) and `The name 'BotCpuKind' does not exist in the current context` 4 times (lines 175-176 and
193-194). The summary (the warning total varies between builds):

```
Build FAILED.
    821 Warning(s)
    26 Error(s)
```

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `FAIL: test_the_fork_server_unit_tests_run_with_the_pinned_filter` with an `AssertionError` that the
new filter string is not found in CI's step (the workflow does not name `UT_BotCpuMeter` yet), then `Ran 18 tests`
and `FAILED (failures=1)`. `test_every_name_in_the_filter_is_a_test_class` passes already, since
`UT_BotCpuMeter.cs` exists.

- [ ] **Step 3: Implement.** Create `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs` with exactly the code
below. `StageOf` is a switch expression with exact, case-sensitive matches: there is no map to build and no static
state, so it works with the meter off. The `KindOf` core tests the owner in the order of 3.3. The `GameBot` case comes
first because a bot's `BotBrain` is itself an `IControlledBrain`. The bot-client test comes before the pilot check, so
a player with a bot client is `Playerbot` even when the pilot check says true. A step of the pet walk is one `Owner`
followed: a chain of 8 owners resolves, and when a 9th would be needed (a longer chain, or a cycle) the result is
`Unattributed` with `charged` null and `pet` false. A pet whose walk ends at an NPC without a controlled brain is
`Monster` with `pet` true; a controlled brain with a null `Owner` is no pet, so it is `Monster` with `pet` false. The
`default` case only ever meets the first owner, since every owner the walk follows is a `GameLiving`. The short
`KindOf` passes the method group `AutonomousPlayerPilot.IsActive`: the compiler caches that delegate in a class of its
own, so `BotCpuMeter` keeps no type initializer and a call allocates nothing once warm. 5.1 says "the stage map" is
created by the first `Enable()`; with a switch there is no map, which keeps 5.1's intent (nothing runs or allocates
while off).

```csharp
using System;
using DOL.AI.Brain;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's hot path (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). While the
// meter is on, each game-loop callback is timed and charged to the kind of its owner (2.5, 3.3) and to its stage (3.4),
// so the owner can compare the average CPU of each kind of bot. BotCpuMeter has no static field initializer and no
// static constructor (5.1): nothing in it runs or allocates while the meter is off.

/// <summary>
/// The player-bot contract (2.5): a <see cref="GamePlayer"/> whose client implements this marker is counted as
/// <see cref="BotCpuKind.Playerbot"/>, with no meter change. Deriving the client from <see cref="BotDummyClient"/>
/// also works.
/// </summary>
public interface IPlayerBotClient { }

/// <summary>What an item is charged to (2.5), in the report's kind order. The text name is the lower-case member name.
/// </summary>
public enum BotCpuKind
{
    /// <summary>A <see cref="GameBot"/> with <see cref="GameBot.IsAutonomousWorldBot"/>.</summary>
    Gamebot,
    /// <summary>Any other <see cref="GameBot"/>: companions and temporary helpers.</summary>
    Companion,
    /// <summary>A <see cref="GamePlayer"/> whose client is an <see cref="IPlayerBotClient"/> or a
    /// <see cref="BotDummyClient"/>.</summary>
    Playerbot,
    /// <summary>A <see cref="GamePlayer"/> the <c>/bot</c> pilot drives (<see cref="AutonomousPlayerPilot.IsActive"/>).
    /// </summary>
    Piloted,
    /// <summary>Any other <see cref="GamePlayer"/>.</summary>
    Player,
    /// <summary>Any other living: monsters, guards, merchants, keep doors.</summary>
    Monster,
    /// <summary>No owner, an owner that is not living, or a pet walk longer than
    /// <see cref="BotCpuMeter.PetWalkSteps"/>.</summary>
    Unattributed,
}

/// <summary>
/// A game-loop stage (3.4), in the CSV's column order (2.3). The text name is the member name. <see cref="Fleet"/> is
/// the GameBot fleet planning scope (3.5) and <see cref="Pets"/> the work of pets in any stage: neither has a profile
/// key.
/// </summary>
public enum BotCpuStage
{
    GameLoop,
    Timer,
    ClientBegin,
    Npc,
    Fleet,
    Attack,
    Casting,
    Effect,
    EffectListBegin,
    EffectListEnd,
    Movement,
    Crafting,
    Reaper,
    Zone,
    ClientEnd,
    Quests,
    Pets,
    Other,
}

/// <summary>The meter's hot path: the stage of a profile key and the kind of an owner.</summary>
public static class BotCpuMeter
{
    /// <summary>The most owners the pet walk follows (3.3); a longer walk, a cycle, is
    /// <see cref="BotCpuKind.Unattributed"/>.</summary>
    public const int PetWalkSteps = 8;

    /// <summary>
    /// The stage of a <see cref="GameLoop.ActiveService"/> profile key (3.4): the service's class name, plus
    /// <c>.BeginTick</c> or <c>.EndTick</c> for those steps. An unknown, missing or empty key is
    /// <see cref="BotCpuStage.Other"/>. A switch, so there is no map to build and nothing runs before it is called.
    /// </summary>
    public static BotCpuStage StageOf(string profileKey) => profileKey switch
    {
        "GameLoopService" => BotCpuStage.GameLoop,
        "TimerService" => BotCpuStage.Timer,
        "ClientService.BeginTick" => BotCpuStage.ClientBegin,
        "NpcService" => BotCpuStage.Npc,
        "AttackService" => BotCpuStage.Attack,
        "CastingService" => BotCpuStage.Casting,
        "EffectService" => BotCpuStage.Effect,
        "EffectListService.BeginTick" => BotCpuStage.EffectListBegin,
        "EffectListService.EndTick" => BotCpuStage.EffectListEnd,
        "MovementService" => BotCpuStage.Movement,
        "CraftingService" => BotCpuStage.Crafting,
        "ReaperService" => BotCpuStage.Reaper,
        "ZoneService" => BotCpuStage.Zone,
        "ClientService.EndTick" => BotCpuStage.ClientEnd,
        "DailyQuestService" or "WeeklyQuestService" or "MonthlyQuestService" => BotCpuStage.Quests,
        _ => BotCpuStage.Other,
    };

    /// <summary>The kind of an owner (3.3), with the real pilot check, <see cref="AutonomousPlayerPilot.IsActive"/>.
    /// </summary>
    public static BotCpuKind KindOf(GameObject owner, out GameObject charged, out bool pet) =>
        KindOf(owner, AutonomousPlayerPilot.IsActive, out charged, out pet);

    /// <summary>
    /// The kind of an owner (3.3), tested in this order: a <see cref="GameBot"/>; a <see cref="GamePlayer"/>, asking
    /// <paramref name="isPiloted"/> only when its client is no bot client; a pet, walked up its owners to the first
    /// that is not a pet, following at most <see cref="PetWalkSteps"/> of them; any other living; anything else or
    /// none. <paramref name="charged"/> is the object whose kind it is (for a pet, the walk's end), null when
    /// <see cref="BotCpuKind.Unattributed"/>; <paramref name="pet"/> says the walk followed at least one owner to get
    /// there. Upstream's <c>GetLivingOwner</c> is not used: it gives a companion's pets to its player and can throw.
    /// </summary>
    public static BotCpuKind KindOf(GameObject owner, Func<GamePlayer, bool> isPiloted, out GameObject charged,
        out bool pet)
    {
        GameObject current = owner;
        pet = false;
        for (int steps = 0; ; steps++)
        {
            switch (current)
            {
                case GameBot bot: // First: a GameBot's BotBrain is itself an IControlledBrain (bots/BotBrain.cs:21).
                    charged = bot;
                    return bot.IsAutonomousWorldBot ? BotCpuKind.Gamebot : BotCpuKind.Companion;
                case GamePlayer player:
                    charged = player;
                    if (player.Client is IPlayerBotClient or BotDummyClient)
                        return BotCpuKind.Playerbot;
                    return isPiloted != null && isPiloted(player) ? BotCpuKind.Piloted : BotCpuKind.Player;
                case GameNPC npc when npc.Brain is IControlledBrain { Owner: { } next }:
                    if (steps == PetWalkSteps)
                    {
                        charged = null;
                        pet = false;
                        return BotCpuKind.Unattributed;
                    }

                    current = next;
                    pet = true;
                    break;
                case GameLiving living:
                    charged = living;
                    return BotCpuKind.Monster;
                default: // none, or not living: only the first owner, as every owner a walk follows is living
                    charged = null;
                    return BotCpuKind.Unattributed;
            }
        }
    }
}
```

CI runs the new class:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py .github/workflows/server-image.yml <<'EDIT'
<<<<<<< OLD
|FullyQualifiedName~UT_QuestGrants"
=======
|FullyQualifiedName~UT_QuestGrants|FullyQualifiedName~UT_BotCpuMeter"
>>>>>>> NEW
EDIT
```

Expected: `edited .github/workflows/server-image.yml (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (635 and 687
were seen while writing the plan); a `--no-incremental` build shows no warning that names `BotCpuMeter.cs` or
`UT_BotCpuMeter.cs`.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: `Passed!  - Failed:     0, Passed:    44, Skipped:     0, Total:    44` (20 tests, 44 cases; exit code 0).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

- [ ] **Step 5: Check the new files' form.** Both are LF, with no BOM, no tab and no line over 120 characters:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs False 0 0 True 120
source/server/Tests/UnitTests/UT_BotCpuMeter.cs False 0 0 True 119
```

- [ ] **Step 6: Run the wider checks.**

CI's fork filter (the grep takes it from the workflow, so it includes `UT_BotCpuMeter` now):

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   688, Skipped:     0, Total:   688` (644 before this task, and this
task's 44).

The whole C# project:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3476, Skipped:     0, Total:  3476` (3432 before this task, and
44; about 30 seconds). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with
"database is locked" when the machine is busy (one review run showed `Failed:     1, Passed:  3475`); rerun it alone
with `--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect
`Passed:     1`.

The whole deploy suite with the world tests (about three minutes; run it after the unit tests, not at the same time):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 744 tests` and `OK (skipped=10)`, as before this task (`test_workflows.py` changes two expected values,
not its test count).

Run: `git status --short`
Expected: only these four lines (`serverconfig.xml` is ignored, so it does not show):

```
 M .github/workflows/server-image.yml
 M deploy/tests/test_workflows.py
?? source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs
?? source/server/Tests/UnitTests/UT_BotCpuMeter.cs
```

Run: `git diff --stat`
Expected (the two new files are not tracked yet):

```
 .github/workflows/server-image.yml | 2 +-
 deploy/tests/test_workflows.py     | 6 ++++--
 2 files changed, 5 insertions(+), 3 deletions(-)
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -F - <<'EOF'
feat(cpu-meter): kinds, stages and the kind of an owner

BotCpuMeter.cs starts the bot CPU meter's hot path (spec 2.5, 3.3, 3.4): the IPlayerBotClient marker of
the player-bot contract, the seven kinds in the report's order, the 18 stages in the CSV's column order,
StageOf for a game-loop profile key, and KindOf for an owner. KindOf walks a pet's owners itself, at most
8 of them, so a companion's pets stay companion and a cycle is unattributed; its pure core takes the
pilot check as a parameter, and the other KindOf passes AutonomousPlayerPilot.IsActive. BotCpuMeter has
no static initializer, so nothing in it runs while the meter is off (5.1). UT_BotCpuMeter pins all of
this and joins CI's fork filter and test_workflows.py (6.4).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `4 files changed, 478 insertions(+), 3 deletions(-)`.

Review: approved with no code change; its four Minor findings shaped only the notes above (`StageOf` is a switch, not
5.1's "stage map"; a walk over 8 steps keeps `pet` false, so Task 2 charges it under the service's stage, not `Pets`;
`KindOf` can throw from `GameNPC.Brain`'s unlocked read, so Tasks 2 and 3 call it inside 3.2 step 1's `try`; the null
`isPiloted` guard has no test of its own).

### Task 2: What each item charges

**Files:**
- Modify: `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs:1-2` (the `using` lines), `:68` (the class
  summary; `BotCpuCharge` goes in above it), `:144-150` (the class's end; `IsBotKind`, `Classify` and their private
  helpers go in after `KindOf`)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs:20-21` (the header comment), `:320-323` (the class's end;
  the helpers and 23 tests, 49 cases)
- Test: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs`

**Spec:** 3.3 (the owner reader chosen once per item type, the item table, the Reaper rule, the GameClient rule), 3.5
(an item with no owner inside the fleet scope is `gamebot` / `Fleet`), 3.8 (presence), 3.10 (the bot kinds have a
per-bot counter), 6.1 (the classification rows that `Classify` alone can test; the rows that need the wrapper are
Task 3's and Task 4's).

**Interfaces:**
- Consumes: Task 1's `BotCpuKind`, `BotCpuStage` and the pure core `public static BotCpuKind
  BotCpuMeter.KindOf(GameObject owner, Func<GamePlayer, bool> isPiloted, out GameObject charged, out bool pet)`
  (`charged` is null only for `Unattributed`; a null `isPiloted` counts as false). In the tests, Task 1's
  `SaveGlobals`/`RestoreGlobals` (the test double server), `Make<T>()`, `Set(Type type, object target, string name,
  object value)`, `Npc(ABrain brain)`, `PetBrain(GameLiving owner)`, `Pet(GameLiving owner)`,
  `Player(GameClient client)`, `Bot(bool autonomous, GamePlayer owner = null)`, `TestDummyClient`,
  `TestPlayerBotClient`, `NotPiloted` and `Piloted`. Upstream, read only (paths under `source/server/GameServer/`),
  the owner members of 3.3's table:
  - `ABrain.Body` (`ai/ABrain.cs:15`, virtual)
  - `AttackComponent.owner` (`ECS-Components/AttackComponent.cs:22`, a public field)
  - `Owner`, a get-only auto-property, of `CastingComponent` (`ECS-Components/CastingComponent.cs:28`),
    `EffectListComponent` (`ECS-Components/EffectListComponent.cs:34`), `MovementComponent`
    (`ECS-Components/MovementComponent.cs:15`), `CraftComponent` (`ECS-Components/CraftComponent.cs:8`, a
    `GamePlayer`) and `ECSGameTimer` (`ECS-Services/TimerService.cs:75`, a `GameObject`)
  - `ECSGameEffect.Owner` (`ECS-Effects/ECSGameEffect.cs:24`, a public field of an abstract class)
  - `SubZoneObject.Node` (`world/SubZone.cs:86`, a `LinkedListNode<GameObject>`)
  - `LivingBeingKilled.Killed` and `Killer` (`ECS-Services/ReaperService.cs:84-85`)
  - `GameClient.ClientState` (`GameClient.cs:36`), `Player` (:56) and `IsPlaying` (:79), and
    `GameObject.ObjectState` (`gameobjects/GameObject.cs:63`)

  The tests also use `ECSGameAbilityEffect` (`ECS-Effects/ECSGameAbilityEffect.cs:6`) and `NpcMovementComponent`
  (`ECS-Components/NpcMovementComponent.cs:12`) as derived item types, `StandardMobBrain` and `GameStaticItem`. By
  reflection they set the backing fields `<Owner>k__BackingField`, `<Node>k__BackingField`,
  `<Killed>k__BackingField`, `<Killer>k__BackingField`, `<ClientState>k__BackingField` and
  `<Player>k__BackingField`, and reach `GameServiceBase`'s private nested `PostedAction` (GSB:94).
- Produces, in `namespace DOL.GS.HearthDAoC` (file `scripts/hearthdaoc/BotCpuMeter.cs`):
  - `public readonly record struct BotCpuCharge(BotCpuKind Kind, BotCpuStage Stage, GameObject Bot, bool Presence)`,
    after `BotCpuStage`. `Kind` and `Stage` pick the slot cell. `Bot` is the owner whose per-bot counter is charged:
    the owner for a bot kind, and null for `Player`, `Monster`, `Unattributed` and `Fleet`. `Presence` is true when the
    item adds one to its kind's presence count.
  - `public static bool BotCpuMeter.IsBotKind(BotCpuKind kind)`: true for `Gamebot`, `Companion`, `Playerbot` and
    `Piloted`, the kinds with a per-bot counter, a spread and a top 5 (3.10).
  - `public static BotCpuCharge BotCpuMeter.Classify<T>(T item, BotCpuStage stage, bool inFleet,
    Func<GamePlayer, bool> isPiloted)`. Its rules, in order:
    - the owner is read by `ItemOwner<T>.Read`, or is none when the type has no reader;
    - the kind is the pure `KindOf(owner, isPiloted, out charged, out pet)`;
    - a pet's item goes to stage `Pets`; else, with `inFleet`, an `Unattributed` item becomes `Gamebot` / `Fleet`;
    - `Presence` is `stage == ItemOwner<T>.PresenceStage` and a kind that is neither `Gamebot` nor `Unattributed`;
    - `Bot` is `charged` for a bot kind, else null (a `Fleet` item's `charged` is null).

    It has no `try` of its own. It throws only if a game object's member throws (`Body` is virtual), so Task 3's
    wrapper calls it inside 3.2 step 1's `try`.
  - Private to `BotCpuMeter`: `ItemOwner<T>` (`internal static readonly Func<T, GameObject> Read` and
    `internal static readonly BotCpuStage? PresenceStage`: `Npc` for an `ABrain`, `ClientBegin` for a `GameClient`,
    else null), `Is<TItem>(Type type)`, `OwnerReader(Type type)` (3.3's table as a type switch),
    `KillOwner(LivingBeingKilled kill)` (the Reaper rule) and `ClientOwner(GameClient client)` (the GameClient rule).
  - In `UT_BotCpuMeter`, for later tasks: `OwnerField`, `Charge<T>(T item, BotCpuStage stage, bool inFleet = false,
    bool piloted = false)`, `ChargeAs(Type itemType, object item, BotCpuStage stage)`,
    `AssertCharge(BotCpuCharge charge, BotCpuKind kind, BotCpuStage stage, GameObject bot, bool presence,
    string item = "")`, `Owned<T>(string member, object owner, Type made = null)`, `BrainOf(GameNPC npc)`,
    `Kill(GameLiving killed, GameObject killer)`, `Client(GameClient.eClientState state,
    GameObject.eObjectState? playerState, Type made = null)` and `AssertGamebot<T>(T item, BotCpuStage stage,
    GameBot bot)`.
  - What Task 3 relies on: its wrapper uses `(int)charge.Kind` and `(int)charge.Stage` as slot indices,
    `charge.Presence` for the kind's presence count, and `charge.Bot` (null: no counter) as the per-bot table's key.
    Tasks 5 and 9 can use `IsBotKind` for the bot kinds' spread and top 5.

- [ ] **Step 1: Write the failing tests.** Add them at the end of the class in `UT_BotCpuMeter.cs`, after Task 1's
last test, and widen the header comment to name what they pin. `Charge<T>` calls `Classify` with Task 1's
`NotPiloted` or `Piloted`. `ChargeAs` calls `Classify<T>` by reflection, for an item type the test can only name at
run time: the private `PostedAction`, and the eleven types of the null-item rows. `AssertCharge` checks the four
fields one by one, `Bot` with `Is.SameAs`, so a failure message never calls `ToString` on a game object built without
its constructor. `Owned<T>` makes an item without its constructor, as `T` or as a concrete type derived from it
(`ECSGameEffect` is abstract), and sets its owner member by name: a public field (`AttackComponent.owner`,
`ECSGameEffect.Owner`) or a property's backing field (`OwnerField`). Task 1's `Npc` sets only `m_ownBrain`, so
`BrainOf` also sets the brain's `Body`, as `GameNPC.SetOwnBrain` does (`gameobjects/GameNPC.cs:2272`). `Client` makes
a client without its constructor and sets its state and player through their backing fields, so neither setter runs
(each notifies `GameEventMgr`, `GameClient.cs:52`, :73). `ClassifyAllocatesNothing` classifies an item of each of four
types once, so each type's reader is chosen, then checks that a second round allocates no byte: the `int` is never
boxed, and no delegate is made per call.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
// in their report order (2.5, 2.3), the stage of each profile key (3.4) and the kind of each owner, pets included
// (3.3), with game objects built without their constructors and their fields set by reflection.
=======
// in their report order (2.5, 2.3), the stage of each profile key (3.4), the kind of each owner, pets included (3.3),
// and what each item charges: its kind, its stage, the bot whose counter it adds to and its presence (3.3, 3.8), with
// game objects built without their constructors and their fields set by reflection.
>>>>>>> NEW
<<<<<<< OLD
        Assert.That(charged, Is.Null);
        Assert.That(pet, Is.False);
    }
}
=======
        Assert.That(charged, Is.Null);
        Assert.That(pet, Is.False);
    }

    // What each item charges (3.3 item table, 3.8 presence): Classify reads the owner its item type names, then the
    // kind of that owner; a pet's work goes to Pets, and an item with no owner inside the fleet scope to gamebot /
    // Fleet.

    private const string OwnerField = "<Owner>k__BackingField";

    private static BotCpuCharge Charge<T>(T item, BotCpuStage stage, bool inFleet = false, bool piloted = false) =>
        BotCpuMeter.Classify(item, stage, inFleet, piloted ? Piloted : NotPiloted);

    // Classify<T> for an item type the test can only name at run time (a private one, or one of a list).
    private static BotCpuCharge ChargeAs(Type itemType, object item, BotCpuStage stage) =>
        (BotCpuCharge)typeof(BotCpuMeter).GetMethod(nameof(BotCpuMeter.Classify))!.MakeGenericMethod(itemType)
            .Invoke(null, [item, stage, false, (Func<GamePlayer, bool>)NotPiloted])!;

    private static void AssertCharge(BotCpuCharge charge, BotCpuKind kind, BotCpuStage stage, GameObject bot,
        bool presence, string item = "")
    {
        Assert.That(charge.Kind, Is.EqualTo(kind), item + " kind");
        Assert.That(charge.Stage, Is.EqualTo(stage), item + " stage");
        Assert.That(charge.Bot, Is.SameAs(bot), item + " bot");
        Assert.That(charge.Presence, Is.EqualTo(presence), item + " presence");
    }

    // An item of type T, made as T or as a concrete type derived from it, with its owner member set.
    private static T Owned<T>(string member, object owner, Type made = null)
    {
        T item = (T)RuntimeHelpers.GetUninitializedObject(made ?? typeof(T));
        typeof(T).GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(item, owner);
        return item;
    }

    // The brain an NpcService batch holds for an NPC, its Body set as GameNPC.SetOwnBrain sets it.
    private static ABrain BrainOf(GameNPC npc)
    {
        ABrain brain = npc.Brain;
        brain.Body = npc;
        return brain;
    }

    private static LivingBeingKilled Kill(GameLiving killed, GameObject killer)
    {
        LivingBeingKilled kill = Owned<LivingBeingKilled>("<Killed>k__BackingField", killed);
        Set(typeof(LivingBeingKilled), kill, "<Killer>k__BackingField", killer);
        return kill;
    }

    // A client as ClientService holds it, with a player in the given object state, or none; the state and the player
    // are set without their setters' events.
    private static GameClient Client(GameClient.eClientState state, GameObject.eObjectState? playerState,
        Type made = null)
    {
        GameClient client = Owned<GameClient>("<ClientState>k__BackingField", state, made);
        if (playerState is { } objectState)
        {
            GamePlayer player = Player(client);
            player.ObjectState = objectState;
            Set(typeof(GameClient), client, "<Player>k__BackingField", player);
        }

        return client;
    }

    [Test]
    public void TheChargeIsAReadonlyStruct()
    {
        Assert.That(typeof(BotCpuCharge).IsValueType, Is.True);
        Assert.That(typeof(BotCpuCharge).IsDefined(typeof(IsReadOnlyAttribute), false), Is.True);
    }

    [TestCase(BotCpuKind.Gamebot, true)]
    [TestCase(BotCpuKind.Companion, true)]
    [TestCase(BotCpuKind.Playerbot, true)]
    [TestCase(BotCpuKind.Piloted, true)]
    [TestCase(BotCpuKind.Player, false)]
    [TestCase(BotCpuKind.Monster, false)]
    [TestCase(BotCpuKind.Unattributed, false)]
    public void TheBotKindsAreTheFourWithPerBotCounters(BotCpuKind kind, bool bot) =>
        Assert.That(BotCpuMeter.IsBotKind(kind), Is.EqualTo(bot));

    // GameBots' n comes from the registry, companions' from their live brains (3.8).
    [TestCase(true, BotCpuKind.Gamebot, false)]
    [TestCase(false, BotCpuKind.Companion, true)]
    public void AGameBotsBrainIsChargedToTheBotAndCountsPresenceOnlyForACompanion(bool autonomous, BotCpuKind kind,
        bool presence)
    {
        GameBot bot = Bot(autonomous, autonomous ? null : Player(Make<GameClient>()));
        AssertCharge(Charge(bot.Brain, BotCpuStage.Npc), kind, BotCpuStage.Npc, bot, presence);
    }

    [Test]
    public void AMonstersBrainIsMonsterAndCountsPresence()
    {
        AssertCharge(Charge(BrainOf(Npc(Make<StandardMobBrain>())), BotCpuStage.Npc), BotCpuKind.Monster,
            BotCpuStage.Npc, null, presence: true);
        // A controlled brain with no owner is no pet.
        AssertCharge(Charge(BrainOf(Pet(null)), BotCpuStage.Npc), BotCpuKind.Monster, BotCpuStage.Npc, null,
            presence: true);
    }

    [Test]
    public void APetsBrainIsItsOwnersKindUnderPetsWithTheOwnersCounterAndNoPresence()
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertCharge(Charge(BrainOf(Pet(player)), BotCpuStage.Npc), BotCpuKind.Player, BotCpuStage.Pets, null,
            presence: false);
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(BrainOf(Pet(Pet(bot))), BotCpuStage.Npc), BotCpuKind.Gamebot, BotCpuStage.Pets, bot,
            presence: false);
        GameBot companion = Bot(autonomous: false, player);
        AssertCharge(Charge(BrainOf(Pet(companion)), BotCpuStage.Npc), BotCpuKind.Companion, BotCpuStage.Pets,
            companion, presence: false);
    }

    [Test]
    public void APetsWorkInAnyStageIsPets()
    {
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(Owned<AttackComponent>("owner", Pet(bot)), BotCpuStage.Attack), BotCpuKind.Gamebot,
            BotCpuStage.Pets, bot, presence: false);
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, Pet(bot)), BotCpuStage.Timer), BotCpuKind.Gamebot,
            BotCpuStage.Pets, bot, presence: false);
    }

    [Test]
    public void APetCycleIsUnattributedInItsStage()
    {
        GameNPC a = Npc(null);
        GameNPC b = Pet(a);
        Set(typeof(GameNPC), a, "m_ownBrain", PetBrain(b));
        AssertCharge(Charge(BrainOf(a), BotCpuStage.Npc), BotCpuKind.Unattributed, BotCpuStage.Npc, null,
            presence: false);
    }

    [TestCase(typeof(TestDummyClient))]
    [TestCase(typeof(TestPlayerBotClient))]
    public void TheAttackComponentOfAPlayerWithABotClientIsPlayerbotWithItsCounter(Type client)
    {
        GamePlayer player = Player((GameClient)RuntimeHelpers.GetUninitializedObject(client));
        AssertCharge(Charge(Owned<AttackComponent>("owner", player), BotCpuStage.Attack), BotCpuKind.Playerbot,
            BotCpuStage.Attack, player, presence: false);
    }

    [TestCase(false, BotCpuKind.Player)]
    [TestCase(true, BotCpuKind.Piloted)]
    public void TheAttackComponentOfAPlayerWithAPlainClientIsPilotedWithItsCounterElsePlayer(bool piloted,
        BotCpuKind kind)
    {
        GamePlayer player = Player(Make<GameClient>());
        AssertCharge(Charge(Owned<AttackComponent>("owner", player), BotCpuStage.Attack, piloted: piloted), kind,
            BotCpuStage.Attack, piloted ? player : null, presence: false);
    }

    private static void AssertGamebot<T>(T item, BotCpuStage stage, GameBot bot) =>
        AssertCharge(Charge(item, stage), BotCpuKind.Gamebot, stage, bot, presence: false, typeof(T).Name);

    [Test]
    public void EachItemTypeIsChargedToItsOwner()
    {
        GameBot bot = Bot(autonomous: true);
        GamePlayer crafter = Player((GameClient)RuntimeHelpers.GetUninitializedObject(typeof(TestDummyClient)));
        Assert.Multiple(() =>
        {
            AssertGamebot(Owned<AttackComponent>("owner", bot), BotCpuStage.Attack, bot);
            AssertGamebot(Owned<CastingComponent>(OwnerField, bot), BotCpuStage.Casting, bot);
            AssertGamebot(Owned<ECSGameEffect>("Owner", bot, typeof(ECSGameAbilityEffect)), BotCpuStage.Effect, bot);
            AssertGamebot(Owned<EffectListComponent>(OwnerField, bot), BotCpuStage.EffectListBegin, bot);
            AssertGamebot(Owned<EffectListComponent>(OwnerField, bot), BotCpuStage.EffectListEnd, bot);
            AssertGamebot(Owned<MovementComponent>(OwnerField, bot), BotCpuStage.Movement, bot);
            AssertGamebot(Owned<ECSGameTimer>(OwnerField, bot), BotCpuStage.Timer, bot);
            AssertGamebot(Owned<SubZoneObject>("<Node>k__BackingField", new LinkedListNode<GameObject>(bot)),
                BotCpuStage.Zone, bot);
            // A craft component's owner is a player.
            AssertCharge(Charge(Owned<CraftComponent>(OwnerField, crafter), BotCpuStage.Crafting),
                BotCpuKind.Playerbot, BotCpuStage.Crafting, crafter, presence: false, nameof(CraftComponent));
        });
    }

    [Test]
    public void AnItemTypeDerivedFromAKnownOneIsReadAsThatOne()
    {
        var brain = (StandardMobBrain)BrainOf(Npc(Make<StandardMobBrain>()));
        AssertCharge(Charge(brain, BotCpuStage.Npc), BotCpuKind.Monster, BotCpuStage.Npc, null, presence: true);
        GameBot bot = Bot(autonomous: true);
        var movement = (NpcMovementComponent)Owned<MovementComponent>(OwnerField, bot, typeof(NpcMovementComponent));
        AssertCharge(Charge(movement, BotCpuStage.Movement), BotCpuKind.Gamebot, BotCpuStage.Movement, bot,
            presence: false);
    }

    [Test]
    public void ATimerWithNoOwnerOrANonLivingOwnerIsUnattributedInTimer()
    {
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, null), BotCpuStage.Timer), BotCpuKind.Unattributed,
            BotCpuStage.Timer, null, presence: false);
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, Make<GameStaticItem>()), BotCpuStage.Timer),
            BotCpuKind.Unattributed, BotCpuStage.Timer, null, presence: false);
    }

    // XP, loot and quest credit run for a killer of a bot or player kind (3.3, the Reaper rule).
    [Test]
    public void AKillIsChargedToAKillerOfABotOrPlayerKind()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        GameBot bot = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        GamePlayer playerBot = Player((GameClient)RuntimeHelpers.GetUninitializedObject(typeof(TestPlayerBotClient)));
        AssertCharge(Charge(Kill(monster, bot), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Reaper, bot,
            presence: false);
        AssertCharge(Charge(Kill(monster, Pet(bot)), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Pets, bot,
            presence: false);
        AssertCharge(Charge(Kill(monster, companion), BotCpuStage.Reaper), BotCpuKind.Companion, BotCpuStage.Reaper,
            companion, presence: false);
        AssertCharge(Charge(Kill(monster, playerBot), BotCpuStage.Reaper), BotCpuKind.Playerbot, BotCpuStage.Reaper,
            playerBot, presence: false);
        AssertCharge(Charge(Kill(monster, player), BotCpuStage.Reaper, piloted: true), BotCpuKind.Piloted,
            BotCpuStage.Reaper, player, presence: false);
        AssertCharge(Charge(Kill(monster, player), BotCpuStage.Reaper), BotCpuKind.Player, BotCpuStage.Reaper, null,
            presence: false);
    }

    [Test]
    public void AKillByAMonsterByNothingOrByAnObjectIsChargedToTheKilled()
    {
        GameNPC monster = Npc(Make<StandardMobBrain>());
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(Kill(bot, monster), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Reaper, bot,
            presence: false);
        AssertCharge(Charge(Kill(monster, null), BotCpuStage.Reaper), BotCpuKind.Monster, BotCpuStage.Reaper, null,
            presence: false);
        // A monster's pet is no bot or player kind either; the killed is no pet, so the stage stays Reaper.
        AssertCharge(Charge(Kill(bot, Pet(monster)), BotCpuStage.Reaper), BotCpuKind.Gamebot, BotCpuStage.Reaper,
            bot, presence: false);
        AssertCharge(Charge(Kill(bot, Make<GameStaticItem>()), BotCpuStage.Reaper), BotCpuKind.Gamebot,
            BotCpuStage.Reaper, bot, presence: false);
        AssertCharge(Charge(Kill(Player(Make<GameClient>()), monster), BotCpuStage.Reaper), BotCpuKind.Player,
            BotCpuStage.Reaper, null, presence: false);
    }

    // A client has an owner only while it IsPlaying (Playing or Linkdead) with an Active player (3.3); in
    // ClientService.BeginTick such a client counts presence (3.8).
    [TestCase(GameClient.eClientState.Playing, null, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.CharScreen, GameObject.eObjectState.Active, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.WorldEnter, GameObject.eObjectState.Active, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.Playing, GameObject.eObjectState.Inactive, BotCpuKind.Unattributed, false)]
    [TestCase(GameClient.eClientState.Playing, GameObject.eObjectState.Active, BotCpuKind.Player, true)]
    [TestCase(GameClient.eClientState.Linkdead, GameObject.eObjectState.Active, BotCpuKind.Player, true)]
    public void AClientInClientBeginIsItsPlayersOnlyWhilePlayingWithAnActivePlayer(GameClient.eClientState state,
        GameObject.eObjectState? player, BotCpuKind kind, bool presence)
    {
        AssertCharge(Charge(Client(state, player), BotCpuStage.ClientBegin), kind, BotCpuStage.ClientBegin, null,
            presence);
    }

    [Test]
    public void APlayingPlayerBotOrPilotedClientCountsPresenceAndChargesItsPlayersCounter()
    {
        GameClient playerBot = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active,
            typeof(TestPlayerBotClient));
        AssertCharge(Charge(playerBot, BotCpuStage.ClientBegin), BotCpuKind.Playerbot, BotCpuStage.ClientBegin,
            playerBot.Player, presence: true);
        GameClient piloted = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active);
        AssertCharge(Charge(piloted, BotCpuStage.ClientBegin, piloted: true), BotCpuKind.Piloted,
            BotCpuStage.ClientBegin, piloted.Player, presence: true);
    }

    [Test]
    public void AClientInClientEndIsChargedButNeverCounted()
    {
        GameClient client = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active);
        AssertCharge(Charge(client, BotCpuStage.ClientEnd), BotCpuKind.Player, BotCpuStage.ClientEnd, null,
            presence: false);
    }

    [Test]
    public void AnIntIsUnattributedOutsideAFleetScopeAndGamebotFleetInsideOne()
    {
        AssertCharge(Charge(7, BotCpuStage.Npc), BotCpuKind.Unattributed, BotCpuStage.Npc, null, presence: false);
        AssertCharge(Charge(7, BotCpuStage.Npc, inFleet: true), BotCpuKind.Gamebot, BotCpuStage.Fleet, null,
            presence: false);
    }

    [Test]
    public void InsideAFleetScopeAnItemWithAnOwnerIsChargedAsUsual()
    {
        GameBot bot = Bot(autonomous: true);
        AssertCharge(Charge(bot.Brain, BotCpuStage.Npc, inFleet: true), BotCpuKind.Gamebot, BotCpuStage.Npc, bot,
            presence: false);
        AssertCharge(Charge(BrainOf(Npc(Make<StandardMobBrain>())), BotCpuStage.Npc, inFleet: true),
            BotCpuKind.Monster, BotCpuStage.Npc, null, presence: true);
        AssertCharge(Charge(BrainOf(Pet(bot)), BotCpuStage.Npc, inFleet: true), BotCpuKind.Gamebot, BotCpuStage.Pets,
            bot, presence: false);
        // An owner that is not living is none: the fleet's.
        AssertCharge(Charge(Owned<ECSGameTimer>(OwnerField, Make<GameStaticItem>()), BotCpuStage.Timer, inFleet: true),
            BotCpuKind.Gamebot, BotCpuStage.Fleet, null, presence: false);
    }

    [TestCase(BotCpuStage.GameLoop)]
    [TestCase(BotCpuStage.Npc)]
    [TestCase(BotCpuStage.Quests)]
    public void APostedActionIsUnattributedInTheStageItWasPostedTo(BotCpuStage stage)
    {
        // GameServiceBase.PostedAction is private (3.6), so Classify<PostedAction> is reached by reflection.
        Type posted = typeof(GameServiceBase).GetNestedType("PostedAction", BindingFlags.NonPublic)!;
        AssertCharge(ChargeAs(posted, RuntimeHelpers.GetUninitializedObject(posted), stage), BotCpuKind.Unattributed,
            stage, null, presence: false);
    }

    [Test]
    public void AnItemTypeTheMeterDoesNotKnowIsUnattributed()
    {
        AssertCharge(Charge("text", BotCpuStage.Other), BotCpuKind.Unattributed, BotCpuStage.Other, null,
            presence: false);
        AssertCharge(Charge(new object(), BotCpuStage.Other), BotCpuKind.Unattributed, BotCpuStage.Other, null,
            presence: false);
        // A game object as the item is not in 3.3's table either.
        AssertCharge(Charge<GameObject>(Bot(autonomous: true), BotCpuStage.Other), BotCpuKind.Unattributed,
            BotCpuStage.Other, null, presence: false);
    }

    [TestCase(typeof(ABrain))]
    [TestCase(typeof(AttackComponent))]
    [TestCase(typeof(CastingComponent))]
    [TestCase(typeof(ECSGameEffect))]
    [TestCase(typeof(EffectListComponent))]
    [TestCase(typeof(MovementComponent))]
    [TestCase(typeof(CraftComponent))]
    [TestCase(typeof(ECSGameTimer))]
    [TestCase(typeof(SubZoneObject))]
    [TestCase(typeof(LivingBeingKilled))]
    [TestCase(typeof(GameClient))]
    public void ANullItemIsUnattributed(Type itemType) =>
        AssertCharge(ChargeAs(itemType, null, BotCpuStage.Other), BotCpuKind.Unattributed, BotCpuStage.Other, null,
            presence: false);

    [Test]
    public void ClassifyAllocatesNothing()
    {
        GameBot bot = Bot(autonomous: true);
        ABrain brain = bot.Brain;
        GameClient client = Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active);
        LivingBeingKilled kill = Kill(Npc(Make<StandardMobBrain>()), Pet(bot));
        Func<GamePlayer, bool> isPiloted = NotPiloted;

        void ClassifyEach()
        {
            BotCpuMeter.Classify(7, BotCpuStage.Npc, true, isPiloted); // an int is never boxed
            BotCpuMeter.Classify(brain, BotCpuStage.Npc, false, isPiloted);
            BotCpuMeter.Classify(client, BotCpuStage.ClientBegin, false, isPiloted);
            BotCpuMeter.Classify(kill, BotCpuStage.Reaper, false, isPiloted);
        }

        ClassifyEach(); // warm-up: each item type's owner reader is chosen on its first use
        long before = GC.GetAllocatedBytesForCurrentThread();
        ClassifyEach();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (2 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 3 errors, all the same and all in the new helpers' signatures. Each error
line starts with the file's absolute path and ends with the project's:

```
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(331,20): error CS0246: The type or namespace name 'BotCpuCharge' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(335,20): error CS0246: The type or namespace name 'BotCpuCharge' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(339,38): error CS0246: The type or namespace name 'BotCpuCharge' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The compiler stops at the missing type, so the missing `Classify` and `IsBotKind` are not reported. The summary (the
warning total varies between builds of the same tree; 821 and 608 were seen):

```
Build FAILED.
    3 Error(s)
```

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: the same three errors, no test runs, exit code 1.

- [ ] **Step 3: Implement.** Three edits in `BotCpuMeter.cs`: `using DOL.AI;` for `ABrain`; `BotCpuCharge` between
`BotCpuStage` and the class, whose summary now names its third job; and at the class's end, after `KindOf`,
`IsBotKind`, `Classify` and their private helpers.

The owner reader of each item type is chosen once, in the static generic class `ItemOwner<T>`. Its two static
readonly fields are set the first time `Classify<T>` runs for that `T`, which happens only while the meter is on, so
`BotCpuMeter` itself still has no static initializer (5.1). `OwnerReader` tests the type against 3.3's table, in
order, with `IsAssignableFrom`, and returns a `Func<TableType, GameObject>`. The cast to `Func<T, GameObject>` works
for a `T` derived from the table's type, because `Func` is contravariant in its argument: a `StandardMobBrain` is read
as an `ABrain`. A value type such as `int` matches no table type, so its reader is null and `owner?.Invoke(item)`
never boxes it. Each reader uses `?.`, so a null item has no owner, with no `item is null` test in generic code (which
might box under the Debug JIT). The lambdas are `static`, so the compiler caches them; `KillOwner` and `ClientOwner`
become a delegate once per item type.

`KillOwner` asks `KindOf` with no pilot check: `Piloted` and `Player` are both killer kinds, so the check changes
nothing. It returns the killer itself, not the end of its pet walk, and `Classify` walks it again, so a kill by a
GameBot's pet is `gamebot` / `Pets` (6.1). `ClientOwner` gives the player only while the client `IsPlaying` (Playing
or Linkdead) and the player is Active: a client loading (`WorldEnter`) or back at the character screen keeps an old
`Player` and has no owner. Presence is decided in one place, and only in the item type's own stage: a pet's item (now
`Pets`), fleet work (`Fleet`) and a client in `ClientEnd` never count. Nor does a gamebot, whose `n` comes from the
registry (3.8), nor unattributed, which has none. A controlled brain with no owner is no pet, so it counts as a
monster.

Two readings to know. `Classify` does not test `brain.IsActive`. A brain that stops being active (dead, out of the
world or out of sight) stays in NpcService's list and counts each tick until its next due think stops it (NS:61-66),
at most `ThinkInterval` later (2,500 ms by default, `ai/ABrain.cs:16`), unless `RemoveFromWorld` stops it sooner
(`gameobjects/GameNPC.cs:2152`). That follows 3.8, which names NS:63-66 as the way a dead brain leaves the count; the
few extra ticks are small against thousands of brains, and a test of `IsActive` would add a virtual call and a
visibility check to every brain item. Inside the fleet scope, every item that would be unattributed goes to
`gamebot` / `Fleet`: no owner, a non-living owner or a pet cycle. 3.5 says "with no owner"; this task reads it as
"unattributed", and `InsideAFleetScopeAnItemWithAnOwnerIsChargedAsUsual` pins that reading (a timer whose owner is a
`GameStaticItem`). Only `int` items run inside the scope (CPP:22, :81), so the two readings never differ on the live
server.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
using System;
using DOL.AI.Brain;
=======
using System;
using DOL.AI;
using DOL.AI.Brain;
>>>>>>> NEW
<<<<<<< OLD
/// <summary>The meter's hot path: the stage of a profile key and the kind of an owner.</summary>
=======
/// <summary>
/// What one game-loop item is charged to (3.2 step 1): the <paramref name="Kind"/> and <paramref name="Stage"/> of its
/// slot cell, the <paramref name="Bot"/> whose per-bot counter it adds to (3.10), and whether it counts one toward its
/// kind's <c>n</c> (<paramref name="Presence"/>, 3.8).
/// </summary>
/// <param name="Kind">The kind of the item's owner (3.3).</param>
/// <param name="Stage">The stage of the call, <see cref="BotCpuStage.Pets"/> for a pet's item, or
/// <see cref="BotCpuStage.Fleet"/> for an item with no owner inside the fleet scope (3.5).</param>
/// <param name="Bot">The owner, for a bot kind (<see cref="BotCpuMeter.IsBotKind"/>); null for any other kind and for
/// <see cref="BotCpuStage.Fleet"/>, which counts for no bot.</param>
/// <param name="Presence">A brain in <see cref="BotCpuStage.Npc"/> or a client in
/// <see cref="BotCpuStage.ClientBegin"/>, not a pet, of a kind whose <c>n</c> is its presence: companion, monster and
/// the three player kinds.</param>
public readonly record struct BotCpuCharge(BotCpuKind Kind, BotCpuStage Stage, GameObject Bot, bool Presence);

/// <summary>The meter's hot path: the stage of a profile key, the kind of an owner and what each item charges.
/// </summary>
>>>>>>> NEW
<<<<<<< OLD
                default: // none, or not living: only the first owner, as every owner a walk follows is living
                    charged = null;
                    return BotCpuKind.Unattributed;
            }
        }
    }
}
=======
                default: // none, or not living: only the first owner, as every owner a walk follows is living
                    charged = null;
                    return BotCpuKind.Unattributed;
            }
        }
    }

    /// <summary>The kinds whose bots have a per-bot counter, a spread and a top 5 (3.10): gamebot, companion,
    /// playerbot and piloted.</summary>
    public static bool IsBotKind(BotCpuKind kind) =>
        kind is BotCpuKind.Gamebot or BotCpuKind.Companion or BotCpuKind.Playerbot or BotCpuKind.Piloted;

    /// <summary>
    /// What one item of a game-loop call is charged to (3.3, 3.8). Its owner is read as its type
    /// <typeparamref name="T"/> says (3.3's table; a posted action, an <c>int</c> or any other type has none), then the
    /// kind of that owner by the pure
    /// <see cref="KindOf(GameObject, Func{GamePlayer, bool}, out GameObject, out bool)"/> with
    /// <paramref name="isPiloted"/>. A pet's item goes to <see cref="BotCpuStage.Pets"/>, whatever
    /// <paramref name="stage"/>; an item with no owner inside the fleet scope (<paramref name="inFleet"/>, 3.5) to
    /// <see cref="BotCpuKind.Gamebot"/> / <see cref="BotCpuStage.Fleet"/>. Called before the item's callback runs, so
    /// the owner is read while the item is intact; it throws only if a game object's member does (<c>Body</c> is
    /// virtual), and the wrapper's <c>try</c> charges such an item to unattributed (3.2).
    /// </summary>
    public static BotCpuCharge Classify<T>(T item, BotCpuStage stage, bool inFleet, Func<GamePlayer, bool> isPiloted)
    {
        Func<T, GameObject> owner = ItemOwner<T>.Read;
        BotCpuKind kind = KindOf(owner?.Invoke(item), isPiloted, out GameObject charged, out bool pet);
        if (pet)
            stage = BotCpuStage.Pets;
        else if (inFleet && kind == BotCpuKind.Unattributed)
        {
            kind = BotCpuKind.Gamebot;
            stage = BotCpuStage.Fleet;
        }

        // Presence (3.8) only in the item type's own stage, so a pet's item (now Pets) and fleet work never count; nor
        // a gamebot, whose n comes from the registry, nor unattributed, which has none.
        bool presence = stage == ItemOwner<T>.PresenceStage &&
            kind is not (BotCpuKind.Gamebot or BotCpuKind.Unattributed);
        return new BotCpuCharge(kind, stage, IsBotKind(kind) ? charged : null, presence);
    }

    /// <summary>
    /// The owner reader of item type <typeparamref name="T"/> (3.3) and the stage in which its items count presence
    /// (3.8), chosen once per type when an item of it is first classified: only while the meter is on (5.1). A reader
    /// of a type also reads the types derived from it (<see cref="Func{T, TResult}"/> is contravariant); a value type
    /// has none, so it is never boxed.
    /// </summary>
    private static class ItemOwner<T>
    {
        internal static readonly Func<T, GameObject> Read = (Func<T, GameObject>)OwnerReader(typeof(T));

        internal static readonly BotCpuStage? PresenceStage =
            Is<ABrain>(typeof(T)) ? BotCpuStage.Npc : Is<GameClient>(typeof(T)) ? BotCpuStage.ClientBegin : null;
    }

    private static bool Is<TItem>(Type type) => typeof(TItem).IsAssignableFrom(type);

    // The owner of each item type in 3.3's table; each reader gives none for a null item.
    private static Delegate OwnerReader(Type type) => type switch
    {
        _ when Is<ABrain>(type) => (Func<ABrain, GameObject>)(static brain => brain?.Body),
        _ when Is<AttackComponent>(type) => (Func<AttackComponent, GameObject>)(static attack => attack?.owner),
        _ when Is<CastingComponent>(type) => (Func<CastingComponent, GameObject>)(static casting => casting?.Owner),
        _ when Is<ECSGameEffect>(type) => (Func<ECSGameEffect, GameObject>)(static effect => effect?.Owner),
        _ when Is<EffectListComponent>(type) => (Func<EffectListComponent, GameObject>)(static list => list?.Owner),
        _ when Is<MovementComponent>(type) => (Func<MovementComponent, GameObject>)(static move => move?.Owner),
        _ when Is<CraftComponent>(type) => (Func<CraftComponent, GameObject>)(static craft => craft?.Owner),
        _ when Is<ECSGameTimer>(type) => (Func<ECSGameTimer, GameObject>)(static timer => timer?.Owner),
        _ when Is<SubZoneObject>(type) => (Func<SubZoneObject, GameObject>)(static node => node?.Node?.Value),
        _ when Is<LivingBeingKilled>(type) => (Func<LivingBeingKilled, GameObject>)KillOwner,
        _ when Is<GameClient>(type) => (Func<GameClient, GameObject>)ClientOwner,
        _ => null,
    };

    // The Reaper rule (3.3): XP, loot and quest credit run for the killer, so a kill is its killer's when the killer's
    // kind, after the pet walk, is a bot or player kind; otherwise the killed's. Piloted or not changes nothing here,
    // so no pilot check is asked; Classify then walks the killer again, for its kind and its Pets stage.
    private static GameObject KillOwner(LivingBeingKilled kill)
    {
        if (kill == null)
            return null;

        GameObject killer = kill.Killer;
        return KindOf(killer, null, out _, out _) is BotCpuKind.Gamebot or BotCpuKind.Companion or
            BotCpuKind.Playerbot or BotCpuKind.Piloted or BotCpuKind.Player ? killer : kill.Killed;
    }

    // A client's owner (3.3): its player while the client IsPlaying (Playing or Linkdead) and the player is Active;
    // none while it loads (WorldEnter) or at the character screen, where it keeps its old player.
    private static GameObject ClientOwner(GameClient client)
    {
        GamePlayer player = client?.Player;
        return player != null && client.IsPlaying && player.ObjectState == GameObject.eObjectState.Active
            ? player
            : null;
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs (3 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (635 was
seen); a `--no-incremental` build shows no warning that names `BotCpuMeter.cs` or `UT_BotCpuMeter.cs`.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: `Passed!  - Failed:     0, Passed:    93, Skipped:     0, Total:    93` (exit code 0). 93 = Task 1's 44 + this
task's 49: 16 `[Test]` methods and 33 `[TestCase]` rows (`TheBotKindsAreTheFourWithPerBotCounters` 7,
`AGameBotsBrainIsChargedToTheBotAndCountsPresenceOnlyForACompanion` 2, the two `TheAttackComponentOfAPlayer...` tests
2 each, `AClientInClientBeginIsItsPlayersOnlyWhilePlayingWithAnActivePlayer` 6,
`APostedActionIsUnattributedInTheStageItWasPostedTo` 3, `ANullItemIsUnattributed` 11).

- [ ] **Step 5: Check the files' form.** Both stay LF, with no BOM, no tab and no line over 120 characters:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs False 0 0 True 120
source/server/Tests/UnitTests/UT_BotCpuMeter.cs False 0 0 True 119
```

- [ ] **Step 6: Run the wider checks.**

CI's fork filter (it already names `UT_BotCpuMeter`, from Task 1; this task changes no workflow or deploy file):

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   737, Skipped:     0, Total:   737` (688 after Task 1, and this task's
49).

The whole C# project:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3525, Skipped:     0, Total:  3525` (3476 after Task 1, and
49; about 45 seconds of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with
"database is locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

The whole deploy suite with the world tests (about three minutes; run it after the unit tests, not at the same time):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 744 tests` and `OK (skipped=10)`, as after Task 1.

Run: `git status --short`
Expected: only these two lines:

```
 M source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs
 M source/server/Tests/UnitTests/UT_BotCpuMeter.cs
```

Run: `git diff --numstat`
Expected: two lines, 109 lines added and 1 deleted in `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs`,
361 added and 2 deleted in `source/server/Tests/UnitTests/UT_BotCpuMeter.cs` (470 and 3 in all).

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs
git commit -F - <<'EOF'
feat(cpu-meter): what each item charges

BotCpuMeter.Classify says what one game-loop item is charged to (spec 3.3, 3.8): a BotCpuCharge, a
readonly struct with the kind, the stage, the bot whose per-bot counter it adds to (a bot kind's owner,
else none) and its presence. The owner is read as 3.3's table says for the item's type, chosen once per
type in a static generic class, so an int is never boxed and an unknown type or a posted action has
none. A kill is its killer's when the killer, after the pet walk, is of a bot or player kind, else the
killed's; a client is its player's only while it is playing with an Active player. A pet's item goes to
Pets, and an item with no owner inside the fleet scope to gamebot / Fleet. Companion and monster brains
in Npc and playing clients in ClientBegin count presence. UT_BotCpuMeter pins every row (6.1).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `2 files changed, 470 insertions(+), 3 deletions(-)`.

Review: approved with no code change; its four Minor findings shaped only the notes above (a brain that stops being
active counts until its next due think, at most `ThinkInterval`, not only in the tick where it stops; inside the
fleet scope every unattributed item, not only one with no owner, goes to `Fleet`) and left two things to later tasks:
the 6.1 rows that need the wrapper (the posted action through `ProcessPostedActionsParallel` and `ActiveService` to
stage through the wrapper are Task 3's, whose posted-action test replaces this task's reflection one, `int` items in a
real `BeginFleet` scope Task 4's), and an optional test that an owned item of another type in `Npc` or `ClientBegin`
counts no presence (today only the `ClientEnd` row pins the item-type half of the presence rule).

### Task 3: The slots, the wrapper and hook H1

**Files:**
- Modify: `source/server/GameServer/Managers/GameLoop/GameLoop.cs:63-65` (hook H1 in `ExecuteForEach`, before :65;
  CRLF with ten LF-only lines, no BOM)
- Modify: `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs:1-3` (the usings), `:9-10` (the header
  comment), `:84-86` (`BotCpuTotals` and `BotCpuCounter` go in before the class, whose summary grows), `:90-92` (the
  switch, the runs, the slots, `ExecuteForEach` and the wrapper go in after `PetWalkSteps`, before `StageOf`)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs:3-8` (the usings), `:22-23` (the header comment), `:47-59`
  (`SaveGlobals` and `RestoreGlobals`), `:334` (`ChargeAs`'s comment), `:622-632` (Task 2's posted-action test, by
  reflection, goes), `:679-682` (the class's end: the helpers and 19 tests, 22 cases, the posted action's among them)
- Create: `deploy/tests/test_bot_cpu.py` (the source checks of 6.2 for H1, the upstream facts and GL's endings; Tasks
  4, 7, 10 and 11 add to it)
- Test: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs`, `deploy/tests/test_bot_cpu.py`

**Spec:** 3.1 (H1, the setup fallback, an empty list passed through), 3.2 (the wrapper's steps 1-6), 3.7 ("Slots", a
window's steps 1-2, `Enable()`'s order), 3.10 (the counters), 3.11 ("Off"), 5.1 (no static initializer, one warning
at most), 5.2 (H1), 6.1 ("Off = no wrap", "On", "Runs", "Setup fallback", the "Classification table"'s posted action,
"Owner read first", "Never throws", "Per-bot counters", "Allocation"; the fleet parts are Task 4's), 6.2 (H1, the
upstream facts of GL, the pool, 3.3 and 3.8's gamebot n, GL's endings by `endings.check`).

**Interfaces:**
- Consumes: Task 1's `BotCpuKind` (values 0-6), `BotCpuStage` (values 0-17) and `public static BotCpuStage
  BotCpuMeter.StageOf(string profileKey)`; Task 2's `BotCpuCharge` (`Kind`, `Stage`, `Bot`, `Presence`) and `public
  static BotCpuCharge BotCpuMeter.Classify<T>(T item, BotCpuStage stage, bool inFleet, Func<GamePlayer, bool>
  isPiloted)`, which has no `try` of its own. Upstream, read only (paths under `source/server/`):
  - `GameLoop.ActiveService` (`GameServer/Managers/GameLoop/GameLoop.cs:25`, set at :125 before the stage runs) and
    GL's `private static GameLoopThreadPool _threadPool;` (GL:18)
  - `public abstract void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action);` and `public abstract
    void Dispose();` of `GameLoopThreadPool` (`GameServer/Managers/GameLoop/GameLoopThreadPool.cs:19`, :21)
  - `public static bool AutonomousPlayerPilot.IsActive(GamePlayer player)`
    (`GameServer/bots/autonomous/AutonomousPlayerPilot.cs:89`)
  - `GameServiceBase`'s `public void Post<TState>(Action<TState> action, TState state)`
    (`GameServer/ECS-Services/GameServiceBase.cs:27`) and `protected void ProcessPostedActionsParallel()` (:51),
    which hands its private `PostedAction` items to `GameLoop.ExecuteForEach` (:62)
  - `public static int AutonomousBotRegistry.PopulationForBrainTick`
    (`GameServer/bots/autonomous/AutonomousBotRegistry.cs:21`), for `test_bot_cpu.py` only
  - `public static Logger LoggerManager.Create(Type type)` (`CoreBase/Logging/Logger/LoggerManager.cs:86`; a
    `NullLogger` until logging is set up, so in the test host) and `Logger.IsWarnEnabled`, `Logger.Warn(string)`

  In the tests, Task 1's `SaveGlobals`/`RestoreGlobals` (extended here), `Make<T>()`, `Npc(ABrain brain)`,
  `Pet(GameLiving owner)`, `Player(GameClient client)` and `Bot(bool autonomous, GamePlayer owner = null)`, and Task
  2's `BrainOf(GameNPC npc)` and `Client(GameClient.eClientState state, GameObject.eObjectState? playerState, Type
  made = null)`; Task 2's `APostedActionIsUnattributedInTheStageItWasPostedTo` goes. In `test_bot_cpu.py`,
  `deploy/tests/endings.py`'s `check(test, path)` (:125-129) and `BOM` (:26).
- Produces, in `namespace DOL.GS.HearthDAoC` (file `scripts/hearthdaoc/BotCpuMeter.cs`; Task 1's and Task 2's members
  are unchanged):
  - `public sealed class BotCpuTotals`, the slot layout and the summed raw totals:
    - `public const int KindCount = 7;`, `StageCount = 18;`, `SlotPadding = 8;` and `SlotLength = 401;` (8 + 7 × 18
      × 3 + 7 + 8)
    - `public static int CellIndex(BotCpuKind kind, BotCpuStage stage)` (8 + (kind × 18 + stage) × 3: the ticks;
      the calls are at +1, the bytes at +2) and `public static int PresenceIndex(BotCpuKind kind)` (8 + 378 + kind)
    - `public long Ticks(BotCpuKind, BotCpuStage)`, `Calls(BotCpuKind, BotCpuStage)`, `Bytes(BotCpuKind,
      BotCpuStage)` and `Presence(BotCpuKind)`
    - `public void Add(BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)` and `public void
      AddPresence(BotCpuKind kind, long count)`, so the report builds totals from plain values (the object is mutable
      and owned by the report side)
    - `public static BotCpuTotals Sum(IEnumerable<long[]> slots)`: pure; it reads each value with `Volatile.Read` and
      never writes a slot. Task 5 can build slots as `new long[BotCpuTotals.SlotLength]` filled through `CellIndex`
      and `PresenceIndex`. The difference of two totals (a negative one counting as 0) is Task 5's.
  - `[StructLayout(LayoutKind.Explicit)] public sealed class BotCpuCounter`: `[FieldOffset(64)] public long Ticks;`
    and `[FieldOffset(72)] public long Calls;` (hot, added with `Interlocked.Add`), `[FieldOffset(144)] public long
    LastTicks;` and `[FieldOffset(152)] public long LastCalls;` (written by the report only), `[FieldOffset(160)]
    public readonly BotCpuKind Kind;`, and `public BotCpuCounter(BotCpuKind kind)`. One counter allocates 184 bytes.
  - In `public static class BotCpuMeter`:
    - `public static bool Enabled;`, a plain field with no initializer, written with `Volatile.Write` by `Enable` and
      `Disable` only.
    - `public static int RunNumber { get; }`: the run counter by `Volatile.Read`; 0 before the first run.
    - `public static int Enable()`: makes the slot list the first time (`CompareExchange`), writes a new
      `ConditionalWeakTable`, resets the per-run warning flag, counts the run up with `Interlocked.Increment`, then
      `Volatile.Write(ref Enabled, true)`. It returns the new run's number. It takes no lock: Task 9 calls it under the
      report lock.
    - `public static void Disable()`: `Volatile.Write(ref Enabled, false)` and nothing else. The slots and the per-bot
      table stay for the report's last window.
    - `public static IEnumerable<KeyValuePair<GameObject, BotCpuCounter>> Counters()`: the current (or last) run's
      table, or an empty array before the first run. It can be enumerated while the loop adds entries.
    - `public static BotCpuTotals TakeSnapshot()`: the slot references copied under `lock (_slots)`, then
      `BotCpuTotals.Sum`. All zeros before the first `Enable()`.
    - `public static void ExecuteForEach<T>(GameLoopThreadPool pool, List<T> items, int toExclusive, Action<T>
      action)`: hook H1's call while the meter is on.
    - Private, reached by the tests through reflection: `List<long[]> _slots` (the list is its own lock),
      `[ThreadStatic] long[] _threadSlot`, `ConditionalWeakTable<GameObject, BotCpuCounter> _counters`, `int _run`,
      `int _setupWarned` and `Func<string, BotCpuStage> _stageHook` (6.1's "Setup fallback" test hook; null on the
      server). Also private: `sealed class Metered<T>` (`Metered(Action<T> action, BotCpuStage stage,
      ConditionalWeakTable<GameObject, BotCpuCounter> counters)`, `internal void Run(T item)`), `long[]
      RegisterSlot()` and `void WarnSetup(Exception exception)`.
  - Upstream: H1, two CRLF lines in `GameLoop.ExecuteForEach<T>` before the unchanged pool call.
  - What Task 4 relies on: it adds the fleet flag and the run check. It reads the flag in `ExecuteForEach`'s setup,
    passes it into `Metered<T>` (today `Run` passes `false` as `Classify`'s `inFleet`), and times the nested pool call
    in a `finally` around `pool.ExecuteForEach(items, toExclusive, run);`.
  - In `UT_BotCpuMeter`, for later tasks: `RecordingPool` (fields `Given` and `Calls`), the fixture field `_pool`,
    `ThrowingBrain`, `Charged(BotCpuTotals before, BotCpuTotals after)` with `Ticks`, `Calls`, `Bytes`, `Presence`,
    `AllCalls` and `AllPresence`, `Since(BotCpuTotals before)`, `Values(BotCpuTotals totals)`, `SlotCount()`,
    `OnNewThread(Action work)`, `Spin(long ticks)`, the `BindingFlags` `HiddenStatic`, the static `byte[] _sink` (a
    callback's allocation lands there) and the static `FieldInfo`s `ThreadPoolField`, `StageHookField`, `SlotsField`
    and `ThreadSlotField`. In `test_bot_cpu.py`: the path constants (`ROOT`, `GAME_SERVER`, `FORK`, `METER_CS`, `LOOP`,
    `GL`, `POOL`, `MT`), H1's guard line `GUARD`, `source`, `between`, `marked` and `type_body`.

- [ ] **Step 1: Write the failing tests.** First the fixture and the helpers in `UT_BotCpuMeter.cs`: three more usings,
the header comment, the saved globals and, at the class's end, the helpers of the new tests. `SaveGlobals` now also
saves GL's private `_threadPool` (GL:18) and `GameLoop.ActiveService`, then puts a `RecordingPool` in the pool's
place, so every test drives the real `GameLoop.ExecuteForEach` and so the real H1. `RestoreGlobals` turns the meter
off and clears the setup's test hook before it restores the rest, so a failing test never leaves either for the next
(6.1). The fork uses no `InternalsVisibleTo`, so the four `FieldInfo`s reach the private statics by reflection.
`RecordingPool` keeps the delegate it was given and runs the items in order on the calling thread. `ThrowingBrain`
overrides the virtual `Body` (`ai/ABrain.cs:15`) with a getter that throws. `Charged` reads each value as the
difference of two snapshots: slots are never zeroed, so a test never depends on what an earlier test charged.
`SlotCount` takes the slot list's own lock, as the meter does. `OnNewThread` runs work on a fresh thread, which has
no slot yet, and returns what it threw. `Spin` busy-waits a number of `Stopwatch` ticks. `_sink` is a static field,
so the JIT cannot drop the array an allocating callback makes.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI;
=======
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DOL.AI;
>>>>>>> NEW
<<<<<<< OLD
// game objects built without their constructors and their fields set by reflection.
[TestFixture, NonParallelizable]
=======
// game objects built without their constructors and their fields set by reflection. Then the real hook H1 (3.1, 3.2,
// 3.7, 3.10, 3.11): GameLoop's pool is a recording pool, and while the meter is on each item runs once and is charged
// to its thread's slot and its bot's counter; off, the pool gets the caller's own delegate.
[TestFixture, NonParallelizable]
>>>>>>> NEW
<<<<<<< OLD
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();

    private GameServer _savedServer;

    [SetUp]
    public void SaveGlobals()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }

    [TearDown]
    public void RestoreGlobals() => GameServer.LoadTestDouble(_savedServer);
=======
    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();

    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly FieldInfo ThreadPoolField = typeof(GameLoop).GetField("_threadPool", HiddenStatic)!;
    private static readonly FieldInfo StageHookField = typeof(BotCpuMeter).GetField("_stageHook", HiddenStatic)!;
    private static readonly FieldInfo SlotsField = typeof(BotCpuMeter).GetField("_slots", HiddenStatic)!;
    private static readonly FieldInfo ThreadSlotField = typeof(BotCpuMeter).GetField("_threadSlot", HiddenStatic)!;

    private GameServer _savedServer;
    private GameLoopThreadPool _savedPool;
    private string _savedActiveService;
    private RecordingPool _pool;

    [SetUp]
    public void SaveGlobals()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        // Hook H1 hands each call to GameLoop's private pool (GL:18): a recording pool here, and the stage is
        // GameLoop.ActiveService, which each test sets (6.1).
        _savedPool = (GameLoopThreadPool)ThreadPoolField.GetValue(null);
        _savedActiveService = GameLoop.ActiveService;
        _pool = new RecordingPool();
        ThreadPoolField.SetValue(null, _pool);
    }

    // A failing test never leaves the meter on, or the setup's test hook set, for the next (6.1).
    [TearDown]
    public void RestoreGlobals()
    {
        BotCpuMeter.Disable();
        StageHookField.SetValue(null, null);
        ThreadPoolField.SetValue(null, _savedPool);
        GameLoop.ActiveService = _savedActiveService;
        GameServer.LoadTestDouble(_savedServer);
    }
>>>>>>> NEW
<<<<<<< OLD
        ClassifyEach();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
=======
        ClassifyEach();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    // The slots, the wrapper and hook H1 (3.1, 3.2, 3.7, 3.10, 3.11): each test calls the real GameLoop.ExecuteForEach,
    // whose pool keeps the delegate it was given, and reads what was charged as the difference of two snapshots.

    // A test subclass of GameLoopThreadPool that keeps the delegate it was given and runs the items in order, on the
    // calling thread (6.1).
    private sealed class RecordingPool : GameLoopThreadPool
    {
        public Delegate Given;
        public int Calls;

        public override void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            Calls++;
            Given = action;
            int count = Math.Min(items.Count, toExclusive);
            for (int i = 0; i < count; i++)
                action(items[i]);
        }

        public override void Dispose() { }
    }

    // A brain whose Body getter throws (Body is virtual, ai/ABrain.cs:15).
    private sealed class ThrowingBrain : ABrain
    {
        public override GameNPC Body
        {
            get => throw new InvalidOperationException("Body");
            set { }
        }

        public override void Think() { }

        public override void KillFSM() { }
    }

    // What was charged between two snapshots.
    private sealed class Charged(BotCpuTotals before, BotCpuTotals after)
    {
        public long Ticks(BotCpuKind kind, BotCpuStage stage) => after.Ticks(kind, stage) - before.Ticks(kind, stage);

        public long Calls(BotCpuKind kind, BotCpuStage stage) => after.Calls(kind, stage) - before.Calls(kind, stage);

        public long Bytes(BotCpuKind kind, BotCpuStage stage) => after.Bytes(kind, stage) - before.Bytes(kind, stage);

        public long Presence(BotCpuKind kind) => after.Presence(kind) - before.Presence(kind);

        public long AllCalls =>
            Enum.GetValues<BotCpuKind>().Sum(kind => Enum.GetValues<BotCpuStage>().Sum(stage => Calls(kind, stage)));

        public long AllPresence => Enum.GetValues<BotCpuKind>().Sum(Presence);
    }

    private static Charged Since(BotCpuTotals before) => new(before, BotCpuMeter.TakeSnapshot());

    // Every value of a snapshot: per kind its stages' ticks, calls and bytes, then its presence.
    private static long[] Values(BotCpuTotals totals) =>
        Enum.GetValues<BotCpuKind>().SelectMany(kind => Enum.GetValues<BotCpuStage>()
            .SelectMany(stage =>
                new[] { totals.Ticks(kind, stage), totals.Calls(kind, stage), totals.Bytes(kind, stage) })
            .Append(totals.Presence(kind))).ToArray();

    private static int SlotCount()
    {
        var slots = (List<long[]>)SlotsField.GetValue(null);
        if (slots == null)
            return 0;
        lock (slots)
            return slots.Count;
    }

    // Runs work on a new thread, as a game-loop worker would, and returns what it threw, if anything.
    private static Exception OnNewThread(Action work)
    {
        Exception failure = null;
        Thread thread = new(() =>
        {
            try { work(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        thread.Join();
        return failure;
    }

    private static void Spin(long ticks)
    {
        long end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end) { }
    }

    private static byte[] _sink;
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (4 block(s))`.

Then the switch, off, on and runs (3.1, 3.7, 3.11). `TheSwitchIsAPlainPublicStaticBoolOffByDefault` asks for a
field, not a property, so H1 is one bool read. The first off test calls `Enable()` and `Disable()` first, so the slot
list and a per-bot table exist, as on a server that was on before; it runs the call on a new thread, which must not
get a slot. The on test makes two calls in two stages and checks the six charges and the three presence counts: the
gamebot's brain counts no presence (its `n` is the registry's), and the player's pet goes to `Pets`. A call with
nothing to run (an empty list, or `toExclusive` 0) still reaches the pool, with the caller's own delegate. The runs
test checks that `Disable()` keeps the closed run's table for the report's last window, and that the next `Enable()`
gives the next number and an empty table while the slots keep their totals.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
    private static byte[] _sink;
}
=======
    private static byte[] _sink;

    [Test]
    public void TheSwitchIsAPlainPublicStaticBoolOffByDefault()
    {
        FieldInfo enabled = typeof(BotCpuMeter).GetField(nameof(BotCpuMeter.Enabled));
        Assert.That(enabled, Is.Not.Null, "a field, not a property: H1 costs one bool read");
        Assert.That(enabled!.IsPublic && enabled.IsStatic && !enabled.IsInitOnly && !enabled.IsLiteral, Is.True);
        Assert.That(enabled.FieldType, Is.EqualTo(typeof(bool)));
        Assert.That(BotCpuMeter.Enabled, Is.False);
    }

    [Test]
    public void OffTheGameLoopHandsThePoolTheCallersDelegateAndMetersNothing()
    {
        BotCpuMeter.Enable();
        BotCpuMeter.Disable();
        GameLoop.ActiveService = nameof(NpcService);
        List<ABrain> brains = [Bot(autonomous: true).Brain, BrainOf(Npc(Make<StandardMobBrain>()))];
        int ran = 0;
        Action<ABrain> action = _ => ran++;
        int slots = SlotCount();
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        object threadSlot = "not read";

        // On a new thread, which has no slot yet: an off call must not make one.
        Exception failure = OnNewThread(() =>
        {
            GameLoop.ExecuteForEach(brains, brains.Count, action);
            threadSlot = ThreadSlotField.GetValue(null);
        });

        Assert.That(failure, Is.Null);
        Assert.That(_pool.Given, Is.SameAs(action));
        Assert.That(ran, Is.EqualTo(2));
        Assert.That(threadSlot, Is.Null, "no slot for the thread");
        Assert.That(SlotCount(), Is.EqualTo(slots));
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));
    }

    [Test]
    public void OffTheGameLoopAllocatesNothing()
    {
        List<ABrain> brains = [Bot(autonomous: true).Brain];
        Action<ABrain> action = static _ => { };
        GameLoop.ExecuteForEach(brains, brains.Count, action); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        GameLoop.ExecuteForEach(brains, brains.Count, action);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void OnEachItemRunsOnceAndChargesOneCallToItsKindAndStage()
    {
        BotCpuMeter.Enable();
        GameBot bot = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        List<ABrain> brains =
            [bot.Brain, companion.Brain, BrainOf(Npc(Make<StandardMobBrain>())), BrainOf(Pet(player))];
        List<GameClient> clients =
        [
            Client(GameClient.eClientState.Playing, GameObject.eObjectState.Active),
            Client(GameClient.eClientState.CharScreen, GameObject.eObjectState.Active),
        ];
        Dictionary<object, int> runs = new();
        Action<ABrain> think = brain => runs[brain] = runs.GetValueOrDefault(brain) + 1;
        Action<GameClient> begin = client => runs[client] = runs.GetValueOrDefault(client) + 1;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ActiveService = nameof(NpcService);
        GameLoop.ExecuteForEach(brains, brains.Count, think);
        Assert.That(_pool.Given, Is.Not.SameAs(think));
        GameLoop.ActiveService = nameof(ClientService) + "." + nameof(ClientService.BeginTick);
        GameLoop.ExecuteForEach(clients, clients.Count, begin);
        Assert.That(_pool.Given, Is.Not.SameAs(begin));

        Charged charged = Since(before);
        Assert.That(brains.Select(brain => runs.GetValueOrDefault(brain)), Is.EqualTo(new[] { 1, 1, 1, 1 }));
        Assert.That(clients.Select(client => runs.GetValueOrDefault(client)), Is.EqualTo(new[] { 1, 1 }));
        Assert.Multiple(() =>
        {
            Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Npc), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Companion, BotCpuStage.Npc), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Monster, BotCpuStage.Npc), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Player, BotCpuStage.Pets), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Player, BotCpuStage.ClientBegin), Is.EqualTo(1));
            Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.ClientBegin), Is.EqualTo(1));
            Assert.That(charged.AllCalls, Is.EqualTo(6));
            Assert.That(charged.Presence(BotCpuKind.Companion), Is.EqualTo(1));
            Assert.That(charged.Presence(BotCpuKind.Monster), Is.EqualTo(1));
            Assert.That(charged.Presence(BotCpuKind.Player), Is.EqualTo(1));
            Assert.That(charged.AllPresence, Is.EqualTo(3));
        });
    }

    [Test]
    public void OnACallWithNothingToRunIsPassedThroughUnwrapped()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        Action<int> action = _ => Assert.Fail("no item runs");
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        GameLoop.ExecuteForEach(new List<int>(), 0, action);
        Assert.That(_pool.Given, Is.SameAs(action), "an empty list");
        _pool.Given = null;
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 0, action);
        Assert.That(_pool.Given, Is.SameAs(action), "nothing below toExclusive");
        Assert.That(_pool.Calls, Is.EqualTo(2));
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));
    }

    [Test]
    public void EachRunHasTheNextNumberAndAnEmptyPerBotTableWhileTheSlotsKeepTheirTotals()
    {
        int first = BotCpuMeter.Enable();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(first));
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        GameLoop.ExecuteForEach(new List<ABrain> { bot.Brain }, 1, static _ => { });
        Assert.That(BotCpuMeter.Counters().Select(pair => pair.Key), Is.EqualTo(new[] { bot }));
        BotCpuTotals firstRun = BotCpuMeter.TakeSnapshot();

        BotCpuMeter.Disable();
        Assert.That(BotCpuMeter.Enabled, Is.False);
        // Off, the report still reads the closed run's counters for its last window.
        Assert.That(BotCpuMeter.Counters().Select(pair => pair.Key), Is.EqualTo(new[] { bot }));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(first));

        int second = BotCpuMeter.Enable();
        Assert.That(second, Is.EqualTo(first + 1));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(second));
        Assert.That(BotCpuMeter.Counters(), Is.Empty);
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(firstRun)), "slots are never zeroed");
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (1 block(s))`.

Then what never fails (3.1, 3.2). The setup fallback test sets the private `_stageHook` to a function that throws,
then runs the same call twice: each time every item runs once with the caller's delegate, and nothing is charged. The
setup's warning is not tested: the test host never sets up logging, so `LoggerManager.Create` gives a `NullLogger`.
A null or empty `ActiveService` gives `Other`. In the owner test each callback sets its own brain's `Body` to null,
and the items are still charged to their kinds, because the owner is read before the callback. A `ThrowingBrain`'s
item is unattributed, runs once and gets no counter. A callback's exception reaches the caller as the same object.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(firstRun)), "slots are never zeroed");
    }
}
=======
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(firstRun)), "slots are never zeroed");
    }

    [Test]
    public void WhenTheSetupThrowsEveryItemRunsOnceUnmeteredWithTheCallersDelegate()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        StageHookField.SetValue(null, (Func<string, BotCpuStage>)(_ => throw new InvalidOperationException("stage")));
        List<ABrain> brains = [Bot(autonomous: true).Brain, BrainOf(Npc(Make<StandardMobBrain>()))];
        Dictionary<ABrain, int> runs = new();
        Action<ABrain> action = brain => runs[brain] = runs.GetValueOrDefault(brain) + 1;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, action));
        Assert.That(_pool.Given, Is.SameAs(action));
        Assert.That(brains.Select(brain => runs.GetValueOrDefault(brain)), Is.EqualTo(new[] { 1, 1 }));
        // Again: the warning is not written twice, and the call runs the same way.
        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, action));
        Assert.That(brains.Select(brain => runs.GetValueOrDefault(brain)), Is.EqualTo(new[] { 2, 2 }));
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ANullOrEmptyActiveServiceIsOther(string activeService)
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = activeService;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { }));
        Assert.That(Since(before).Calls(BotCpuKind.Unattributed, BotCpuStage.Other), Is.EqualTo(2));
    }

    [Test]
    public void TheOwnerIsReadBeforeTheCallback()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        List<ABrain> brains = [bot.Brain, BrainOf(Npc(Make<StandardMobBrain>()))];
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(brains, brains.Count, static brain => brain.Body = null);

        Charged charged = Since(before);
        Assert.That(brains.Select(brain => brain.Body), Is.All.Null, "each callback ran");
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Npc), Is.EqualTo(1));
        Assert.That(charged.Calls(BotCpuKind.Monster, BotCpuStage.Npc), Is.EqualTo(1));
        Assert.That(charged.Presence(BotCpuKind.Monster), Is.EqualTo(1));
        Assert.That(BotCpuMeter.Counters().Single(pair => pair.Key == bot).Value.Calls, Is.EqualTo(1));
    }

    [Test]
    public void AnItemWhoseOwnerThrowsIsUnattributedAndStillRunsOnce()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        List<ABrain> brains = [Make<ThrowingBrain>()];
        int ran = 0;
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        Assert.DoesNotThrow(() => GameLoop.ExecuteForEach(brains, brains.Count, _ => ran++));

        Charged charged = Since(before);
        Assert.That(ran, Is.EqualTo(1));
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(1));
        Assert.That(charged.AllCalls, Is.EqualTo(1));
        Assert.That(charged.AllPresence, Is.Zero);
        Assert.That(BotCpuMeter.Counters(), Is.Empty);
    }

    [Test]
    public void AnExceptionFromACallbackReachesTheCallerUnchanged()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(TimerService);
        InvalidOperationException thrown = new("callback");
        Exception caught = Assert.Throws<InvalidOperationException>(() =>
            GameLoop.ExecuteForEach(new List<int> { 1 }, 1, _ => throw thrown));
        Assert.That(caught, Is.SameAs(thrown));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (1 block(s))`.

Then the posted action, 6.1's table row that Task 2 could reach only by reflection, which a meter that left posted
actions unwrapped would still pass. With H1 it runs through the real service path. `PostingService` is a
`GameServiceBase` whose `Tick` runs what was posted to it, as `NpcService.Tick` does first (NS:27), and
`ProcessPostedActionsParallel` hands the private `PostedAction` items to the real `GameLoop.ExecuteForEach` (GSB:62).
Each case posts two actions, one with a bot as its state, sets `ActiveService` as the game loop's tick does, and ticks
the service: each action runs once, the pool gets the meter's wrapper, and both are charged `unattributed` in the
service's stage, with no presence and no bot's counter (3.6). The run drops Task 2's reflection test, narrows
`ChargeAs`'s comment to what is left (the null-item rows), and puts the new test after
`ANullOrEmptyActiveServiceIsOther`.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
    [TestCase(BotCpuStage.GameLoop)]
    [TestCase(BotCpuStage.Npc)]
    [TestCase(BotCpuStage.Quests)]
    public void APostedActionIsUnattributedInTheStageItWasPostedTo(BotCpuStage stage)
    {
        // GameServiceBase.PostedAction is private (3.6), so Classify<PostedAction> is reached by reflection.
        Type posted = typeof(GameServiceBase).GetNestedType("PostedAction", BindingFlags.NonPublic)!;
        AssertCharge(ChargeAs(posted, RuntimeHelpers.GetUninitializedObject(posted), stage), BotCpuKind.Unattributed,
            stage, null, presence: false);
    }

    [Test]
    public void AnItemTypeTheMeterDoesNotKnowIsUnattributed()
=======
    [Test]
    public void AnItemTypeTheMeterDoesNotKnowIsUnattributed()
>>>>>>> NEW
<<<<<<< OLD
    // Classify<T> for an item type the test can only name at run time (a private one, or one of a list).
=======
    // Classify<T> for an item type the test names at run time (one of a list).
>>>>>>> NEW
<<<<<<< OLD
        Assert.That(Since(before).Calls(BotCpuKind.Unattributed, BotCpuStage.Other), Is.EqualTo(2));
    }

=======
        Assert.That(Since(before).Calls(BotCpuKind.Unattributed, BotCpuStage.Other), Is.EqualTo(2));
    }

    // A service as upstream's are: its Tick runs what was posted to it, as NpcService.Tick does first.
    private sealed class PostingService : GameServiceBase
    {
        public override void Tick() => ProcessPostedActionsParallel();
    }

    // Posted actions reach the meter through their service's own GameLoop.ExecuteForEach call
    // (ECS-Services/GameServiceBase.cs:62), over PostedAction items, which are private: the meter cannot read who
    // posted them or for whom (3.6). Each is unattributed in the stage the game loop set for the service, even one
    // whose state is a bot.
    [TestCase(nameof(GameLoopService), BotCpuStage.GameLoop)]
    [TestCase(nameof(NpcService), BotCpuStage.Npc)]
    [TestCase(nameof(DailyQuestService), BotCpuStage.Quests)]
    public void APostedActionIsUnattributedInItsServicesStage(string profileKey, BotCpuStage stage)
    {
        BotCpuMeter.Enable();
        PostingService service = new();
        GameBot bot = Bot(autonomous: true);
        List<object> ran = [];
        service.Post(state => ran.Add(state), bot);
        service.Post(state => ran.Add(state), 7);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ActiveService = profileKey; // as GameLoop's tick sets it before the service's step
        service.Tick();

        Charged charged = Since(before);
        Assert.Multiple(() =>
        {
            Assert.That(ran, Is.EqualTo(new object[] { bot, 7 }), "each action ran once");
            Assert.That(_pool.Calls, Is.EqualTo(1));
            Assert.That(_pool.Given.Method.DeclaringType, Is.Not.EqualTo(typeof(GameServiceBase)),
                "the pool got the meter's wrapper, not the service's own callback");
            Assert.That(charged.Calls(BotCpuKind.Unattributed, stage), Is.EqualTo(2));
            Assert.That(charged.AllCalls, Is.EqualTo(2), "nothing else");
            Assert.That(charged.AllPresence, Is.Zero);
            Assert.That(BotCpuMeter.Counters(), Is.Empty, "no bot's counter");
        });
    }

>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (3 block(s))`.

Last, the counters, the allocations and the slots (3.7, 3.10, 3.11). In the per-bot test each of six brains spins
0.1 ms: the first gamebot gets its own brain and its pet's, a player's pet goes to no counter (a player is no bot
kind), and the two gamebots' counter ticks equal the slots' gamebot ticks in `Npc` and `Pets`. The per-call test warms
up with a list of 60 items, so the thread's slot, the owner readers and each bot's counter exist; after that a call of
1 item and a call of 60 allocate the same, more than 0 (the wrapper and its delegate). The slot test runs one call on
a second thread and finds its slot in the list and in the sum. The layout tests need no call: 385 distinct indexes
between the 8 longs of padding at each end, `Sum` of two slots made by hand, and the counter's field offsets and size.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(caught, Is.SameAs(thrown));
    }
}
=======
        Assert.That(caught, Is.SameAs(thrown));
    }

    [Test]
    public void EachBotsItemsAddToItsOwnCounterAndAPetsToItsOwners()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot first = Bot(autonomous: true);
        GameBot second = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        GameBot companion = Bot(autonomous: false, player);
        List<ABrain> brains =
        [
            first.Brain, second.Brain, BrainOf(Pet(first)), companion.Brain, BrainOf(Pet(player)),
            BrainOf(Npc(Make<StandardMobBrain>())),
        ];
        long spin = Stopwatch.Frequency / 10_000; // 0.1 ms
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(brains, brains.Count, _ => Spin(spin));

        Charged charged = Since(before);
        Dictionary<GameObject, BotCpuCounter> counters =
            BotCpuMeter.Counters().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.That(counters.Keys, Is.EquivalentTo(new GameObject[] { first, second, companion }), "bots only");
        Assert.Multiple(() =>
        {
            Assert.That(counters[first].Calls, Is.EqualTo(2), "its brain and its pet's");
            Assert.That(counters[second].Calls, Is.EqualTo(1));
            Assert.That(counters[companion].Calls, Is.EqualTo(1));
            Assert.That(counters[first].Ticks, Is.GreaterThanOrEqualTo(2 * spin));
            Assert.That(counters[second].Ticks, Is.GreaterThanOrEqualTo(spin));
            Assert.That(counters[first].Kind, Is.EqualTo(BotCpuKind.Gamebot));
            Assert.That(counters[companion].Kind, Is.EqualTo(BotCpuKind.Companion));
            Assert.That(counters.Values.Select(counter => counter.LastTicks + counter.LastCalls), Is.All.Zero,
                "written by the report only");
            // A bot's ticks are those its items added to the slot.
            Assert.That(counters[first].Ticks + counters[second].Ticks,
                Is.EqualTo(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Npc) +
                    charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Pets)));
            Assert.That(counters[companion].Ticks, Is.EqualTo(charged.Ticks(BotCpuKind.Companion, BotCpuStage.Npc)));
        });
    }

    [Test]
    public void AnItemThatAllocatesChargesItsBytesToItsKindAndAnIntListChargesNone()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(new List<ABrain> { bot.Brain }, 1, static _ => _sink = new byte[1000]);
        GameLoop.ExecuteForEach(new List<int> { 1, 2, 3 }, 3, static _ => { });

        Charged charged = Since(before);
        Assert.That(charged.Bytes(BotCpuKind.Gamebot, BotCpuStage.Npc), Is.GreaterThanOrEqualTo(1000));
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(3));
        Assert.That(charged.Bytes(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.Zero);
    }

    [Test]
    public void TheMeterAllocatesForEachCallNotForEachItem()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        GameBot bot = Bot(autonomous: true);
        GamePlayer player = Player(Make<GameClient>());
        List<ABrain> one = [bot.Brain];
        List<ABrain> many =
            [.. Enumerable.Range(0, 20).SelectMany(_ => new[] { bot.Brain, BrainOf(Pet(bot)), BrainOf(Pet(player)) })];
        Action<ABrain> action = static _ => { };

        long Cost(List<ABrain> items)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            GameLoop.ExecuteForEach(items, items.Count, action);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Cost(many); // warm-up: the thread's slot, the owner readers, each bot's counter
        long perCall = Cost(one);
        Assert.That(perCall, Is.GreaterThan(0), "one wrapper and its delegate");
        Assert.That(Cost(many), Is.EqualTo(perCall));
    }

    [Test]
    public void ASnapshotAddsUpTheSlotOfEveryThreadThatRanAnItem()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(TimerService);
        GameLoop.ExecuteForEach(new List<int> { 1 }, 1, static _ => { }); // this thread's slot, if it had none
        int slots = SlotCount();
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();
        object otherSlot = null;

        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        Exception failure = OnNewThread(() =>
        {
            GameLoop.ExecuteForEach(new List<int> { 3 }, 1, static _ => { });
            otherSlot = ThreadSlotField.GetValue(null);
        });

        Assert.That(failure, Is.Null);
        Assert.That(Since(before).Calls(BotCpuKind.Unattributed, BotCpuStage.Timer), Is.EqualTo(3));
        Assert.That(SlotCount(), Is.EqualTo(slots + 1), "the new thread registered its slot");
        var registered = (List<long[]>)SlotsField.GetValue(null);
        lock (registered!)
            Assert.That(registered, Has.Some.SameAs(otherSlot));
        Assert.That(((long[])otherSlot)!.Length, Is.EqualTo(BotCpuTotals.SlotLength));
    }

    [Test]
    public void ASlotHasEachCellAndPresenceCountOnceBetween64BytesOfPaddingAtEachEnd()
    {
        Assert.That(BotCpuTotals.SlotLength, Is.EqualTo(401));
        List<int> indexes = [];
        foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
        {
            foreach (BotCpuStage stage in Enum.GetValues<BotCpuStage>())
                indexes.AddRange(Enumerable.Range(BotCpuTotals.CellIndex(kind, stage), 3));
            indexes.Add(BotCpuTotals.PresenceIndex(kind));
        }

        Assert.That(indexes, Is.Unique);
        Assert.That(indexes, Has.Count.EqualTo(385));
        Assert.That(indexes.Min(), Is.EqualTo(8));
        Assert.That(indexes.Max(), Is.EqualTo(401 - 8 - 1));
    }

    [Test]
    public void TheTotalsAreTheSumsOfTheSlotsAndReadingThemChangesNoSlot()
    {
        long[] a = new long[BotCpuTotals.SlotLength];
        long[] b = new long[BotCpuTotals.SlotLength];
        int cell = BotCpuTotals.CellIndex(BotCpuKind.Monster, BotCpuStage.Movement);
        (a[cell], a[cell + 1], a[cell + 2]) = (5, 2, 300);
        (b[cell], b[cell + 1], b[cell + 2]) = (7, 1, 0);
        a[BotCpuTotals.PresenceIndex(BotCpuKind.Monster)] = 30;
        b[BotCpuTotals.PresenceIndex(BotCpuKind.Monster)] = 12;
        long[] aBefore = (long[])a.Clone();
        long[] bBefore = (long[])b.Clone();

        BotCpuTotals totals = BotCpuTotals.Sum([a, b]);

        Assert.That(a, Is.EqualTo(aBefore));
        Assert.That(b, Is.EqualTo(bBefore));
        Assert.That(totals.Ticks(BotCpuKind.Monster, BotCpuStage.Movement), Is.EqualTo(12));
        Assert.That(totals.Calls(BotCpuKind.Monster, BotCpuStage.Movement), Is.EqualTo(3));
        Assert.That(totals.Bytes(BotCpuKind.Monster, BotCpuStage.Movement), Is.EqualTo(300));
        Assert.That(totals.Presence(BotCpuKind.Monster), Is.EqualTo(42));
        Assert.That(Values(totals).Sum(), Is.EqualTo(12 + 3 + 300 + 42), "nothing else");

        // The report builds its windows from plain values.
        BotCpuTotals built = new();
        built.Add(BotCpuKind.Monster, BotCpuStage.Movement, 12, 3, 300);
        built.AddPresence(BotCpuKind.Monster, 42);
        Assert.That(Values(built), Is.EqualTo(Values(totals)));
        Assert.That(Values(new BotCpuTotals()), Is.All.Zero);
    }

    [Test]
    public void ACounterKeepsItsHotFieldsBetween64BytesOfPaddingAndIsSmall()
    {
        Assert.That(typeof(BotCpuCounter).IsExplicitLayout, Is.True);
        FieldInfo[] fields =
            typeof(BotCpuCounter).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        int Offset(FieldInfo field) => field.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
        int ticks = Offset(typeof(BotCpuCounter).GetField(nameof(BotCpuCounter.Ticks))!);
        int calls = Offset(typeof(BotCpuCounter).GetField(nameof(BotCpuCounter.Calls))!);
        Assert.That(ticks, Is.GreaterThanOrEqualTo(64));
        Assert.That(calls, Is.EqualTo(ticks + 8));
        IEnumerable<FieldInfo> cold =
            fields.Where(field => field.Name is not (nameof(BotCpuCounter.Ticks) or nameof(BotCpuCounter.Calls)));
        Assert.That(cold.Select(Offset), Is.All.GreaterThanOrEqualTo(calls + 8 + 64));

        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuCounter counter = new(BotCpuKind.Companion);
        long size = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(size, Is.LessThan(200));
        Assert.That(counter.Kind, Is.EqualTo(BotCpuKind.Companion));
        Assert.That(new[] { counter.Ticks, counter.Calls, counter.LastTicks, counter.LastCalls }, Is.All.Zero);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (1 block(s))`.

Create `deploy/tests/test_bot_cpu.py` with exactly the code below (LF, no BOM: the Write tool, or a quoted heredoc).
`source`, `between` and `marked` are copied from `deploy/tests/test_rp_timer_wait.py:49-66`. `type_body` is new: it
cuts a type's text from its declaration up to the next type declared at the same indentation, so each member of 3.3
is found inside its own type (`ECS-Services/TimerService.cs` holds several classes, and `ECSGameTimer`'s body stops
before `ECSGameTimerWrapperBase`). `HookTests` pins H1: in the method, the comment, the guard and upstream's call, and
nothing else. `UpstreamFactTests` pins what the meter mirrors (6.2): `ActiveService` set before `TickAction`, the
profile-key rule (GL:169), the 17 `AddStep` lines in their order with each key in `StageOf`'s switch,
`THREAD_NAME`, `_threadPool`, the pool's abstract `ExecuteForEach`, MT's worker name, 19 owner and kind members of
3.3, and `PopulationForBrainTick`, the gamebot n of 3.8 that the report reads (Task 9). These four pass from the
start. `EndingsTests` checks GL against upstream's file at `deploy/upstream.lock`'s tag by `endings.check`, pins the
BOM off, and wants exactly two lines naming `HearthDAoC`, each ending in CR. The 6.2 facts this task does not check
come later: H6, the nesting checks, `BotCpu` in no other upstream file and the camp planner's thread name,
`"CampSnapshotPlanner"` (CPP:52) with the line of `BackgroundSnapshotBuilder` that gives it to the thread (Task 4,
which adds the `CPP` path), and the new files' endings (Task 11).

```python
"""The bot CPU meter (spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.2).

While the meter is on, each game-loop callback is timed and charged to the kind of its owner and to its stage. The hot
path is unit-tested (UT_BotCpuMeter), through the real hook H1 in GameLoop.ExecuteForEach; the hook's shape in the
upstream file, the upstream facts the meter mirrors (the stage of each profile key, the pool's entry point, the member
each item's owner is read from) and the upstream file's line endings need the source, so these check it.
"""
import os
import re
import unittest

from tests import endings

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
FORK = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
LOOP = os.path.join(GAME_SERVER, "Managers", "GameLoop")
GL = os.path.join(LOOP, "GameLoop.cs")
POOL = os.path.join(LOOP, "GameLoopThreadPool.cs")
MT = os.path.join(LOOP, "GameLoopThreadPoolMultiThreaded.cs")

# Hook H1 (spec 3.1): while the meter is on, it gets the pool call; off, one bool read before the unchanged call.
GUARD = ("if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, "
         "action); return; }")
POOL_CALL = "_threadPool.ExecuteForEach(items, toExclusive, action);"


def source(path):
    """A C# source file's text (upstream files may start with a BOM)."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


def between(text, start, end):
    """The text from the first start up to the first end after it."""
    body = text[text.index(start):]
    return body[:body.index(end)]


def marked(test, body, call):
    """The body calls the fork once, with a // HearthDAoC: comment on the line above the call."""
    test.assertEqual(body.count(call), 1, call)
    above = body[:body.index(call)].rsplit("\n", 2)[-2]
    test.assertIn("// HearthDAoC:", above, call)


def type_body(text, header):
    """A type's text, from its declaration up to the next type declared at the same indentation (or the file's end)."""
    start = text.index(header)
    indent = text[text.rfind("\n", 0, start) + 1:start]
    following = re.compile(r"\n" + re.escape(indent) + r"\S[^\n]*\b(class|interface|struct|record|enum)\b")
    found = following.search(text, start + len(header))
    return text[start:found.start() if found else len(text)]


class HookTests(unittest.TestCase):
    def test_h1_hands_the_pool_call_to_the_meter_while_it_is_on(self):
        body = between(source(GL),
                       "public static void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)",
                       "\n        }")
        marked(self, body, GUARD)
        statements = [line.strip() for line in body.split("\n")[2:]]
        # The comment, the guard, then upstream's call, unchanged: nothing else in the method.
        self.assertEqual(statements[1:], [GUARD, POOL_CALL])
        self.assertTrue(statements[0].startswith("// HearthDAoC: the per-kind CPU meter (#137)"), statements[0])


class UpstreamFactTests(unittest.TestCase):
    """What the meter mirrors from upstream (spec 6.2): a change upstream fails here."""

    # GameLoop.BuildTickSequence's steps in their order: the stages of spec 3.4.
    STEPS = [("GameLoopService", "Tick"), ("TimerService", "Tick"), ("ClientService", "BeginTick"),
             ("NpcService", "Tick"), ("AttackService", "Tick"), ("CastingService", "Tick"), ("EffectService", "Tick"),
             ("EffectListService", "BeginTick"), ("EffectListService", "EndTick"), ("MovementService", "Tick"),
             ("CraftingService", "Tick"), ("ReaperService", "Tick"), ("ZoneService", "Tick"),
             ("ClientService", "EndTick"), ("DailyQuestService", "Tick"), ("WeeklyQuestService", "Tick"),
             ("MonthlyQuestService", "Tick")]

    def test_the_game_loop_names_each_stage_before_it_runs(self):
        gl = source(GL)
        self.assertIn("public static string ActiveService { get; set; }", gl)
        callback = between(gl, "static void TickCallback(object state)", "\n            }\n")
        self.assertLess(callback.index("ActiveService = tickState.ProfileKey;"),
                        callback.index("tickState.TickAction();"))
        # The profile key: the service's name, plus .BeginTick or .EndTick for those steps (GL:169).
        self.assertIn('string profileKey = methodName == nameof(IGameService.Tick) ? service.ServiceName : '
                      '$"{service.ServiceName}.{methodName}";', gl)

    def test_the_stages_are_the_17_steps_in_their_order_and_the_meter_maps_each_key(self):
        steps = re.findall(r"^ *AddStep\((\w+)\.Instance, \1\.Instance\.(\w+)\);\r?$", source(GL), re.M)
        self.assertEqual(steps, self.STEPS)
        stage_of = between(source(METER_CS), "public static BotCpuStage StageOf(string profileKey)", "};")
        for service, method in steps:
            key = service if method == "Tick" else f"{service}.{method}"
            self.assertIn(f'"{key}"', stage_of, key)

    def test_the_game_loop_hands_each_call_to_its_pool(self):
        gl = source(GL)
        self.assertIn('public const string THREAD_NAME = "GameLoop";', gl)
        self.assertIn("private static GameLoopThreadPool _threadPool;", gl)
        self.assertIn("public abstract void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action);",
                      source(POOL))
        # The loop threads' names, which the process line reads (spec 3.9).
        self.assertIn('Name = $"{GameLoop.THREAD_NAME}_Worker_{i}",', source(MT))

    # The member each item's owner is read from (spec 3.3), and what the kind of an owner tests: (file, type, member).
    OWNERS = [
        ("ai/ABrain.cs", "public abstract class ABrain", "public virtual GameNPC Body { get; set; }"),
        ("ECS-Components/AttackComponent.cs", "public class AttackComponent", "public GameLiving owner;"),
        ("ECS-Components/CastingComponent.cs", "public class CastingComponent", "public GameLiving Owner { get; }"),
        ("ECS-Effects/ECSGameEffect.cs", "public abstract class ECSGameEffect", "public GameLiving Owner;"),
        ("ECS-Components/EffectListComponent.cs", "public class EffectListComponent",
         "public GameLiving Owner { get; }"),
        ("ECS-Components/MovementComponent.cs", "public class MovementComponent", "public GameLiving Owner { get; }"),
        ("ECS-Components/CraftComponent.cs", "public class CraftComponent", "public GamePlayer Owner { get; }"),
        ("ECS-Services/TimerService.cs", "public class ECSGameTimer", "public GameObject Owner { get; }"),
        ("world/SubZone.cs", "public class SubZoneObject", "public LinkedListNode<GameObject> Node { get; }"),
        ("ECS-Services/ReaperService.cs", "public class LivingBeingKilled",
         "public GameLiving Killed { get; private set; }"),
        ("ECS-Services/ReaperService.cs", "public class LivingBeingKilled",
         "public GameObject Killer { get; private set; }"),
        ("GameClient.cs", "public class GameClient", "public GamePlayer Player\n"),
        ("GameClient.cs", "public class GameClient",
         "public bool IsPlaying => ClientState is eClientState.Playing or eClientState.Linkdead;"),
        ("bots/GameBot.cs", "public class GameBot : GameNPC", "public bool IsAutonomousWorldBot { get; private set; }"),
        ("bots/BotDummyClient.cs", "public class BotDummyClient : GameClient", "public BotDummyClient()"),
        ("ai/brain/IControlledBrain.cs", "public interface IControlledBrain", "GameLiving Owner { get; }"),
        ("bots/autonomous/AutonomousPlayerPilot.cs", "public static class AutonomousPlayerPilot",
         "public static bool IsActive(GamePlayer player)"),
        ("gameobjects/GameNPC.cs", "public class GameNPC : GameLiving", "public ABrain Brain\n"),
        # A posted action has no owner the meter can read; if upstream opens it, H5 becomes cheap (spec 3.6).
        ("ECS-Services/GameServiceBase.cs", "public abstract class GameServiceBase",
         "private sealed class PostedAction"),
        # The gamebot n: the registry's count of GameBots in the world, which the report reads every second (spec 3.8).
        ("bots/autonomous/AutonomousBotRegistry.cs", "public static class AutonomousBotRegistry",
         "public static int PopulationForBrainTick =>"),
    ]

    def test_each_owner_is_read_from_the_member_the_meter_names(self):
        for path, header, member in self.OWNERS:
            with self.subTest(path=path, member=member):
                self.assertIn(member, type_body(source(os.path.join(GAME_SERVER, *path.split("/"))), header))


class EndingsTests(unittest.TestCase):
    def test_the_game_loop_keeps_upstreams_endings_and_no_bom(self):
        # GL is CRLF with ten LF-only lines and no BOM (spec 5.2); the hook's lines end CRLF, as the lines beside them.
        endings.check(self, GL)
        with open(GL, "rb") as f:
            raw = f.read()
        self.assertFalse(raw.startswith(endings.BOM))
        hooks = [line for line in raw.split(b"\n") if b"HearthDAoC" in line]
        self.assertEqual(len(hooks), 2)
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 4 errors, all the same and all in the new helpers: `Charged`'s primary
constructor (line 737, twice), `Since` (753) and `Values` (756). Each error line starts with the file's absolute path
and ends with the project's:

```
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(753,34): error CS0246: The type or namespace name 'BotCpuTotals' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(756,34): error CS0246: The type or namespace name 'BotCpuTotals' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(737,34): error CS0246: The type or namespace name 'BotCpuTotals' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(737,55): error CS0246: The type or namespace name 'BotCpuTotals' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The compiler stops at the missing type, so the missing `Enabled`, `Enable`, `TakeSnapshot` and the rest are not
reported. The summary (the warning total varies between builds of the same tree; 821 was seen):

```
Build FAILED.
    4 Error(s)
```

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: the progress line `FF....`, then two failures, since GL has no hook yet:

```
FAIL: test_the_game_loop_keeps_upstreams_endings_and_no_bom (tests.test_bot_cpu.EndingsTests.test_the_game_loop_keeps_upstreams_endings_and_no_bom)
AssertionError: 0 != 2
FAIL: test_h1_hands_the_pool_call_to_the_meter_while_it_is_on (tests.test_bot_cpu.HookTests.test_h1_hands_the_pool_call_to_the_meter_while_it_is_on)
AssertionError: 0 != 1 : if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, action); return; }
```

then `Ran 6 tests` and `FAILED (failures=2)`. The four `UpstreamFactTests` pass already: they pin upstream as it is.

- [ ] **Step 3: Implement.** `BotCpuMeter.cs` first, in two runs, then H1 in GL.

The first run adds the usings, widens the header comment, and puts the two new types between `BotCpuCharge` and the
class, whose summary now names its new jobs. `BotCpuTotals` owns the slot layout: `CellIndex` and `PresenceIndex`
give a value's index in a slot, so the wrapper that adds and the sum that reads share one layout. Its values live in
one array laid out as a slot, padding included, so `Sum` adds index by index. `Sum` reads with `Volatile.Read`, as a
loop thread may be adding to the slot, and writes nothing to it. `BotCpuCounter` is a class with explicit field
offsets, as the pool's own `WorkState` is (MT:45-50): `Ticks` at 64 and `Calls` at 72, then 64 bytes of padding, then
the report's `LastTicks` and `LastCalls` and the `Kind` from 144. A counter allocates 184 bytes, under 3.10's 200.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
using System;
using DOL.AI;
using DOL.AI.Brain;
=======
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Logging;
>>>>>>> NEW
<<<<<<< OLD
// so the owner can compare the average CPU of each kind of bot. BotCpuMeter has no static field initializer and no
// static constructor (5.1): nothing in it runs or allocates while the meter is off.
=======
// so the owner can compare the average CPU of each kind of bot. Hook H1 in GameLoop.ExecuteForEach hands each call to
// BotCpuMeter.ExecuteForEach while the meter is on (3.1). BotCpuMeter has no static field initializer and no static
// constructor (5.1): nothing in it runs or allocates while the meter is off.
>>>>>>> NEW
<<<<<<< OLD
/// <summary>The meter's hot path: the stage of a profile key, the kind of an owner and what each item charges.
/// </summary>
public static class BotCpuMeter
=======
/// <summary>
/// Raw totals of the per-thread slots (3.7): for each kind and stage the <see cref="Stopwatch"/> ticks, calls and bytes
/// charged, and for each kind its presence count (3.8). A slot is a <c>long[]</c> of <see cref="SlotLength"/> longs:
/// the 7 × 18 cells of 3 values (ticks, calls, bytes) from <see cref="CellIndex"/>, then the 7 presence counts at
/// <see cref="PresenceIndex"/>, with <see cref="SlotPadding"/> unused longs at each end, so two threads' slots never
/// share a cache line. <see cref="BotCpuMeter.TakeSnapshot"/> sums every slot into one of these; a window is the
/// difference of two (3.7).
/// </summary>
public sealed class BotCpuTotals
{
    /// <summary>The kinds (2.5).</summary>
    public const int KindCount = 7;
    /// <summary>The stages (3.4).</summary>
    public const int StageCount = 18;
    /// <summary>The unused longs at each end of a slot: 64 bytes.</summary>
    public const int SlotPadding = 8;
    /// <summary>7 kinds × 18 stages × 3 values and 7 presence counts, 385 longs, between the padding: 401 longs, about
    /// 3.2 KB.</summary>
    public const int SlotLength = SlotPadding + KindCount * StageCount * 3 + KindCount + SlotPadding;

    private const int PresenceStart = SlotPadding + KindCount * StageCount * 3;

    // The values, laid out as in a slot.
    private readonly long[] _values = new long[SlotLength];

    /// <summary>The index of the ticks of [<paramref name="kind"/>, <paramref name="stage"/>] in a slot; its calls are
    /// at the next index and its bytes at the one after.</summary>
    public static int CellIndex(BotCpuKind kind, BotCpuStage stage) =>
        SlotPadding + ((int)kind * StageCount + (int)stage) * 3;

    /// <summary>The index of the presence count of <paramref name="kind"/> in a slot.</summary>
    public static int PresenceIndex(BotCpuKind kind) => PresenceStart + (int)kind;

    public long Ticks(BotCpuKind kind, BotCpuStage stage) => _values[CellIndex(kind, stage)];

    public long Calls(BotCpuKind kind, BotCpuStage stage) => _values[CellIndex(kind, stage) + 1];

    public long Bytes(BotCpuKind kind, BotCpuStage stage) => _values[CellIndex(kind, stage) + 2];

    public long Presence(BotCpuKind kind) => _values[PresenceIndex(kind)];

    /// <summary>Adds to the [<paramref name="kind"/>, <paramref name="stage"/>] cell: the report builds its windows
    /// from plain values.</summary>
    public void Add(BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)
    {
        int cell = CellIndex(kind, stage);
        _values[cell] += ticks;
        _values[cell + 1] += calls;
        _values[cell + 2] += bytes;
    }

    /// <summary>Adds to the presence count of <paramref name="kind"/>.</summary>
    public void AddPresence(BotCpuKind kind, long count) => _values[PresenceIndex(kind)] += count;

    /// <summary>
    /// The sums of <paramref name="slots"/> (3.7, a window's step 2): each value read with
    /// <see cref="Volatile.Read(ref long)"/>, as its thread may be adding to it (aligned 64-bit reads are atomic on the
    /// 64-bit server). A slot is never written.
    /// </summary>
    public static BotCpuTotals Sum(IEnumerable<long[]> slots)
    {
        BotCpuTotals totals = new();
        foreach (long[] slot in slots)
        {
            for (int i = SlotPadding; i < SlotLength - SlotPadding; i++)
                totals._values[i] += Volatile.Read(ref slot[i]);
        }

        return totals;
    }
}

/// <summary>
/// One bot's counter (3.10): the <see cref="Stopwatch"/> ticks and the calls charged to the bot, added with
/// <see cref="Interlocked.Add(ref long, long)"/> by the loop threads (one bot's effects can run on two threads at
/// once), and the values the report last read, written by the report only. Counters are small objects allocated one
/// after another, so the two hot fields sit between 64 bytes of padding on each side (explicit field offsets, as the
/// pool's own <c>WorkState</c> does, MT:45-50): two bots' counters never share a cache line. Under 200 bytes a bot.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public sealed class BotCpuCounter
{
    /// <summary>The ticks charged to the bot.</summary>
    [FieldOffset(64)] public long Ticks;
    /// <summary>The callbacks charged to the bot.</summary>
    [FieldOffset(72)] public long Calls;
    /// <summary>The <see cref="Ticks"/> the report read last.</summary>
    [FieldOffset(144)] public long LastTicks;
    /// <summary>The <see cref="Calls"/> the report read last.</summary>
    [FieldOffset(152)] public long LastCalls;
    /// <summary>The bot's kind when its counter was made.</summary>
    [FieldOffset(160)] public readonly BotCpuKind Kind;

    public BotCpuCounter(BotCpuKind kind) => Kind = kind;
}

/// <summary>The meter's hot path: the switch and its runs, the wrapper hook H1 hands each call to, the per-thread slots
/// and the per-bot counters it charges, the stage of a profile key, the kind of an owner and what each item charges.
/// </summary>
public static class BotCpuMeter
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs (3 block(s))`.

The second run adds the switch, the runs, the slots, `ExecuteForEach` and the wrapper after `PetWalkSteps`. Every new
static is a plain field with no initializer, so `BotCpuMeter` still has no static initializer and nothing runs while
off (5.1): `Enabled` is false by default, the slot list is made by the first `Enable()`, and the logger is made inside
`WarnSetup`. `Enable()` makes the slot list, a new per-bot table and the next run number before it sets `Enabled`, so
no wrapper runs before they exist (3.7). The slot list is also its own lock: there is no stage map to guard, since
Task 1 made `StageOf` a switch.

`ExecuteForEach` runs once per call, on the game-loop thread. Its `try` covers only the setup, never the pool call.
It passes the call through with the caller's delegate when there is nothing to run (a null list, or
`Math.Min(items.Count, toExclusive) <= 0`) or no per-bot table (`Enabled` set without `Enable()`, which only a test
can do); neither case logs. Otherwise it reads `GameLoop.ActiveService` once, takes the stage from `_stageHook` when a
test set it, else from `StageOf`, and checks the stage's index, as 3.2 asks ("the stage index is checked when the
wrapper is built"); the `uint` cast also catches a negative value. The wrapper is one `Metered<T>` object and the
delegate of its `Run`: two allocations a call, none per item (3.1). On an exception the pool gets the caller's
delegate, after `WarnSetup`.

`Metered<T>.Run` is 3.2's steps 1-6. Step 1 is inside a `try`: this thread's slot (`RegisterSlot` on its first item),
`Classify` with `inFleet` false (Task 4 adds the fleet flag) and `AutonomousPlayerPilot.IsActive` as the pilot check,
the cell, the presence index, and for a bot its counter. `GetOrAdd` takes a `static` lambda and the kind as its
argument, so a lookup for a bot that has a counter allocates nothing. An exception charges the item to
`unattributed` in the call's stage, with no presence and no counter; with no slot, the item is not charged at all.
The callback runs outside the `try`, between the two reads of the clock and of the thread's allocated bytes. Steps 5
and 6 are plain adds to the thread's own slot and `Interlocked.Add` on the counter: neither can throw. `WarnSetup`
writes at most one warning a run (`Interlocked.Exchange` on a flag that `Enable()` resets), and its log call sits in
its own `try`, since NLog here throws from log calls (`throwExceptions="true"`,
`source/server/GameServer/config/logconfig.xml:2`).

Three readings to know. 5.1 says "one warning at most" without saying per run or per process: this task logs at most
one a run, because logs rotate daily and keep 7 days, so a once-per-process warning could be gone while a later run
is still unmetered (dropping the reset in `Enable()` makes it once per process). The spec gives the warning no text:
it is plain text with no `BOT_CPU_` token, because 2.1 ties `BOT_CPU_ERROR` to a following `off source=error`, so the
owner's `grep BOT_CPU` does not show it. And 3.2 does not say whether an item whose callback throws is charged: with
no `finally`, steps 4-6 are skipped, so it is not, and the exception reaches the service unchanged
(`AnExceptionFromACallbackReachesTheCallerUnchanged`); it then stops the server as today (GL:95-101).

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
    public const int PetWalkSteps = 8;

    /// <summary>
=======
    public const int PetWalkSteps = 8;

    /// <summary>
    /// The switch (3.11, 4.4): a plain static bool, false by default, which hook H1 reads on every
    /// <c>GameLoop.ExecuteForEach</c> call, so off costs one bool read. Written with
    /// <see cref="Volatile.Write(ref bool, bool)"/> by <see cref="Enable"/> and <see cref="Disable"/> only.
    /// </summary>
    public static bool Enabled;

    // The slot of every thread that ran a metered item, under the list's own lock (3.7): made by the first Enable and
    // kept. A slot is never removed or zeroed; a restarted worker gets a new one and the old one keeps its totals.
    private static List<long[]> _slots;

    // This thread's slot, written only by this thread, with plain adds; null until its first metered item.
    [ThreadStatic] private static long[] _threadSlot;

    // The run's per-bot counters (3.10), a new table at each Enable; an entry goes with its bot.
    private static ConditionalWeakTable<GameObject, BotCpuCounter> _counters;

    // The run number, counted up by Enable (3.7).
    private static int _run;

    // 1 once the setup fallback has written its warning in this run (3.1, 5.1).
    private static int _setupWarned;

    // A test hook (6.1, "Setup fallback"): set, it replaces StageOf in a call's setup. Null on the server.
    private static Func<string, BotCpuStage> _stageHook;

    /// <summary>The number of the current run, or of the last one once off; 0 before the first.</summary>
    public static int RunNumber => Volatile.Read(ref _run);

    /// <summary>
    /// Turns the meter on, for the report side, in an open job (3.7): first the slot list (the first time), a new
    /// per-bot table and the next run number, so no wrapper runs before they exist, then <see cref="Enabled"/>.
    /// Returns the new run's number.
    /// </summary>
    public static int Enable()
    {
        if (Volatile.Read(ref _slots) == null)
            Interlocked.CompareExchange(ref _slots, new List<long[]>(), null);

        Volatile.Write(ref _counters, new ConditionalWeakTable<GameObject, BotCpuCounter>());
        Volatile.Write(ref _setupWarned, 0);
        int run = Interlocked.Increment(ref _run);
        Volatile.Write(ref Enabled, true);
        return run;
    }

    /// <summary>
    /// Turns the meter off (3.7, 4.4): the next <c>GameLoop.ExecuteForEach</c> call runs unwrapped. Wrappers already
    /// in flight still add their items. The slots and the per-bot table stay, for the report's last window.
    /// </summary>
    public static void Disable() => Volatile.Write(ref Enabled, false);

    /// <summary>
    /// The per-bot counters of the current run, or of the last one once off (3.10), for the report: each bot charged
    /// in the run, while it lives, with its counter. Empty before the first run.
    /// </summary>
    public static IEnumerable<KeyValuePair<GameObject, BotCpuCounter>> Counters() =>
        Volatile.Read(ref _counters) ?? (IEnumerable<KeyValuePair<GameObject, BotCpuCounter>>)[];

    /// <summary>
    /// The sums of every thread's slot (3.7, a window's steps 1-2): the slot list copied under its lock, then each
    /// value read with <see cref="Volatile.Read(ref long)"/>, without a lock. Never writes a slot. All zero before the
    /// first run.
    /// </summary>
    public static BotCpuTotals TakeSnapshot()
    {
        List<long[]> slots = Volatile.Read(ref _slots);
        if (slots == null)
            return new BotCpuTotals();

        long[][] copy;
        lock (slots)
            copy = slots.ToArray();
        return BotCpuTotals.Sum(copy);
    }

    /// <summary>
    /// Hook H1's call while the meter is on (3.1). On the game-loop thread, once per call: reads the stage from
    /// <see cref="GameLoop.ActiveService"/>, builds one wrapper that carries it and the run's per-bot table to the
    /// worker threads, and hands the wrapper to the pool's own <see cref="GameLoopThreadPool.ExecuteForEach{T}"/>. A
    /// call with nothing to run is passed through unwrapped. The setup never throws: on an exception, after one
    /// warning, the pool gets the caller's own delegate, so each item still runs once, unmetered. An exception from the
    /// pool or a callback reaches the caller unchanged.
    /// </summary>
    public static void ExecuteForEach<T>(GameLoopThreadPool pool, List<T> items, int toExclusive, Action<T> action)
    {
        Action<T> run = action;
        try
        {
            // No table yet: Enabled was set without Enable, and nothing could be charged to a bot.
            ConditionalWeakTable<GameObject, BotCpuCounter> counters = Volatile.Read(ref _counters);
            if (items != null && Math.Min(items.Count, toExclusive) > 0 && counters != null)
            {
                string profileKey = GameLoop.ActiveService;
                BotCpuStage stage = _stageHook != null ? _stageHook(profileKey) : StageOf(profileKey);
                if ((uint)stage >= BotCpuTotals.StageCount)
                    stage = BotCpuStage.Other;
                run = new Metered<T>(action, stage, counters).Run;
            }
        }
        catch (Exception exception)
        {
            run = action;
            WarnSetup(exception);
        }

        pool.ExecuteForEach(items, toExclusive, run);
    }

    // The wrapper of one call (3.2), made on the game-loop thread with the call's stage and the run's per-bot table:
    // the worker threads read neither GameLoop.ActiveService nor a static.
    private sealed class Metered<T>
    {
        private readonly Action<T> _action;
        private readonly BotCpuStage _stage;
        private readonly ConditionalWeakTable<GameObject, BotCpuCounter> _counters;

        internal Metered(Action<T> action, BotCpuStage stage, ConditionalWeakTable<GameObject, BotCpuCounter> counters)
        {
            _action = action;
            _stage = stage;
            _counters = counters;
        }

        internal void Run(T item)
        {
            // 1. Before the callback, inside a try: this thread's slot on its first item, the owner, kind and stage,
            // and a bot's counter. An exception charges the item to unattributed, or, with no slot, leaves it
            // uncharged.
            long[] slot = null;
            int cell;
            int presence = -1;
            BotCpuCounter counter = null;
            try
            {
                slot = _threadSlot ?? RegisterSlot();
                BotCpuCharge charge = Classify(item, _stage, false, AutonomousPlayerPilot.IsActive);
                cell = BotCpuTotals.CellIndex(charge.Kind, charge.Stage);
                if (charge.Presence)
                    presence = BotCpuTotals.PresenceIndex(charge.Kind);
                if (charge.Bot != null)
                    counter = _counters.GetOrAdd(charge.Bot, static (_, kind) => new BotCpuCounter(kind), charge.Kind);
            }
            catch (Exception)
            {
                cell = BotCpuTotals.CellIndex(BotCpuKind.Unattributed, _stage);
                presence = -1;
                counter = null;
            }

            // 2-4. The callback, timed, with what it allocates; outside the try, so its exceptions reach the service as
            // today.
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            _action(item);
            long ticks = Stopwatch.GetTimestamp() - start;
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

            // 5-6. Plain adds to this thread's own slot, Interlocked ones to the bot's counter: neither can throw.
            if (slot != null)
            {
                slot[cell] += ticks;
                slot[cell + 1]++;
                slot[cell + 2] += bytes;
                if (presence >= 0)
                    slot[presence]++;
            }

            if (counter != null)
            {
                Interlocked.Add(ref counter.Ticks, ticks);
                Interlocked.Add(ref counter.Calls, 1);
            }
        }
    }

    // A thread's first metered item (3.7): its slot, added to the list under the list's lock, once per thread.
    private static long[] RegisterSlot()
    {
        long[] slot = new long[BotCpuTotals.SlotLength];
        List<long[]> slots = Volatile.Read(ref _slots);
        lock (slots)
            slots.Add(slot);
        _threadSlot = slot;
        return slot;
    }

    // The setup fallback's warning (3.1): at most one a run (5.1), its log call in its own try, as NLog here throws
    // from log calls. The logger is made here, not in a static field, so BotCpuMeter keeps no static initializer.
    private static void WarnSetup(Exception exception)
    {
        if (Interlocked.Exchange(ref _setupWarned, 1) != 0)
            return;

        try
        {
            Logger log = LoggerManager.Create(typeof(BotCpuMeter));
            if (log.IsWarnEnabled)
                log.Warn("Bot CPU meter: the setup of a game-loop call failed, so its items ran unmetered (later " +
                    $"failures in this run are not logged): {exception}");
        }
        catch (Exception)
        {
            // Nothing: the meter's own faults never reach the game loop.
        }
    }

    /// <summary>
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs (1 block(s))`.

Build and run the class now, before H1: the tests that need the hook to hand calls to the meter still fail.

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)` (636 warnings were seen).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: `Failed!  - Failed:    13, Passed:    99, Skipped:     0, Total:   112` (exit code 1). The thirteen, each
with the pool still given the caller's delegate, so nothing is charged:

- `OnEachItemRunsOnceAndChargesOneCallToItsKindAndStage`: ``Expected: not same as <System.Action`1[DOL.AI.ABrain]>``,
  ``But was:  <System.Action`1[DOL.AI.ABrain]>``.
- `TheOwnerIsReadBeforeTheCallback`, `AnItemWhoseOwnerThrowsIsUnattributedAndStillRunsOnce`: `Expected: 1`,
  `But was:  0`.
- `ANullOrEmptyActiveServiceIsOther(null)` and `ANullOrEmptyActiveServiceIsOther("")`: `Expected: 2`, `But was:  0`.
- `APostedActionIsUnattributedInItsServicesStage`, its three cases, each with three failures in one: `the pool got the
  meter's wrapper, not the service's own callback` (`Expected: not equal to <DOL.GS.GameServiceBase>`,
  `But was:  <DOL.GS.GameServiceBase>`), then `Expected: 2`, `But was:  0` for the stage's calls and again for
  `nothing else`.
- `ASnapshotAddsUpTheSlotOfEveryThreadThatRanAnItem`: `Expected: 3`, `But was:  0`.
- `AnItemThatAllocatesChargesItsBytesToItsKindAndAnIntListChargesNone`: `Expected: greater than or equal to 1000`,
  `But was:  0`.
- `EachBotsItemsAddToItsOwnCounterAndAPetsToItsOwners`: `bots only`, `Expected: equivalent to` the three bots (NUnit
  shows each as `<! NullReferenceException was thrown by GameBot.ToString() !>`, as they are built without their
  constructors), `But was:  <empty>`.
- `EachRunHasTheNextNumberAndAnEmptyPerBotTableWhileTheSlotsKeepTheirTotals`: `Values differ at index [0]` (the table
  is empty).
- `TheMeterAllocatesForEachCallNotForEachItem`: `one wrapper and its delegate`, `Expected: greater than 0`.

Nine of the 22 new cases pass without H1. The switch, the off tests and the layout tests do not depend on it. The
setup fallback, the call with nothing to run and the callback's exception pass because without the hook the pool
always gets the caller's delegate; the on test, which fails without H1 and passes with it, is what gives them meaning.

Now H1. GL is CRLF with ten LF-only lines and no BOM (5.2); the helper writes the new lines with CRLF, the ending of
the lines beside them, and keeps the rest. The two lines are exactly 3.1's: the comment, then the guard, before
upstream's unchanged pool call. `HearthDAoC.BotCpuMeter` resolves from GL's `namespace DOL.GS`.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/Managers/GameLoop/GameLoop.cs <<'EDIT'
<<<<<<< OLD
        public static void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            _threadPool.ExecuteForEach(items, toExclusive, action);
=======
        public static void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            // HearthDAoC: the per-kind CPU meter (#137) times each callback while it is on; off, this is one bool read.
            if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, action); return; }
            _threadPool.ExecuteForEach(items, toExclusive, action);
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/Managers/GameLoop/GameLoop.cs (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (608 to 641
were seen). A `--no-incremental` build shows one warning that names a file this task changes, since only the tests
assign the hook, by reflection (683 warnings in all were seen):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs(220,46): warning CS0649: Field 'BotCpuMeter._stageHook' is never assigned to, and will always have its default value null [source/server/GameServer/GameServer.csproj]
```

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: `Passed!  - Failed:     0, Passed:   112, Skipped:     0, Total:   112` (exit code 0). 112 = Task 2's 93,
less the 3 cases of its posted-action test, + this task's 22: 19 tests, `ANullOrEmptyActiveServiceIsOther` with 2
cases and `APostedActionIsUnattributedInItsServicesStage` with 3. The class takes well under a second; four runs in a
row all passed.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 6 tests` and `OK`.

- [ ] **Step 5: Check the files' form.** The three fork files stay LF, with no BOM, no tab and no line over 120
characters:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs deploy/tests/test_bot_cpu.py <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs False 0 0 True 120
source/server/Tests/UnitTests/UT_BotCpuMeter.cs False 0 0 True 120
deploy/tests/test_bot_cpu.py False 0 0 True 120
```

GL's two new lines end in CRLF (`EndingsTests` checks the same):

Run: `git diff source/server/GameServer/Managers/GameLoop/GameLoop.cs | cat -A | grep '^+ '`
Expected: the two hook lines, each ending `^M$`:

```
+            // HearthDAoC: the per-kind CPU meter (#137) times each callback while it is on; off, this is one bool read.^M$
+            if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, action); return; }^M$
```

- [ ] **Step 6: Run the wider checks.**

CI's fork filter (it already names `UT_BotCpuMeter`, from Task 1). CI's deploy step runs `python3 -m unittest
discover -s deploy/tests -t deploy -v` (`.github/workflows/server-image.yml:94`), so it finds `test_bot_cpu.py` with
no workflow change.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   756, Skipped:     0, Total:   756` (737 after Task 2, and this task's
19: 22 new cases, 3 of Task 2's replaced).

The whole C# project:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3544, Skipped:     0, Total:  3544` (3525 after Task 2, and 19; about 30
seconds of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_endings.py`
Expected: `Ran 11 tests` and `OK`.

The whole deploy suite with the world tests (about three minutes; run it after the unit tests, not at the same time):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 750 tests` and `OK (skipped=10)` (744 after Task 2, and `test_bot_cpu.py`'s 6).

Run: `git status --short`
Expected: only these four lines (`serverconfig.xml` is ignored, so it does not show):

```
 M source/server/GameServer/Managers/GameLoop/GameLoop.cs
 M source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs
 M source/server/Tests/UnitTests/UT_BotCpuMeter.cs
?? deploy/tests/test_bot_cpu.py
```

Run: `git diff --numstat`
Expected (the new file is not tracked yet):

```
2	0	source/server/GameServer/Managers/GameLoop/GameLoop.cs
314	3	source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs
561	14	source/server/Tests/UnitTests/UT_BotCpuMeter.cs
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/Managers/GameLoop/GameLoop.cs source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs deploy/tests/test_bot_cpu.py
git commit -F - <<'EOF'
feat(cpu-meter): the slots, the wrapper and hook h1

Hook H1 in GameLoop.ExecuteForEach hands each call to BotCpuMeter.ExecuteForEach while the meter is on
(spec 3.1); off, it costs one bool read and the pool gets the caller's own delegate. The meter reads the
stage once per call and gives the pool one wrapper, which reads each item's owner before its callback
runs, times the callback, counts what it allocates, and adds both with one call to its thread's slot
(3.2, 3.7) and, for a bot, to its counter in the run's ConditionalWeakTable (3.10). A failed setup runs
the call with the caller's delegate after at most one warning a run; a failed owner read charges
unattributed. Enable makes the slot list, a new per-bot table and the next run number before it sets
Enabled, and TakeSnapshot sums the slots into BotCpuTotals. UT_BotCpuMeter drives the real H1 through a
recording pool (6.1); test_bot_cpu.py checks the hook, the upstream facts the meter mirrors and
GameLoop.cs's endings (6.2).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `4 files changed, 1037 insertions(+), 17 deletions(-)` and `create mode 100644 deploy/tests/test_bot_cpu.py`.

Review: approved with no code change; its three Minor findings changed no line. `OffTheGameLoopAllocatesNothing`
catches an H1 that ignores `Enabled` only when an earlier test has called `Enable()` (the first off test catches it
alone, so coverage holds; Task 4, which extends this test for `BeginFleet`/`EndFleet`, can start it with
`BotCpuMeter.Enable(); BotCpuMeter.Disable();`). No test reaches the stage range check, which only the test hook can
trip (a case with `_stageHook` set to `_ => (BotCpuStage)99` would). The 6.2 facts this task leaves (Step 1) are
pinned in Tasks 4 and 11. Two gaps found after Task 11 are closed here with no production change (the build's
test-only commit 54b35eb): Task 2's posted-action test reached `Classify<PostedAction>` by reflection, so a meter that
left posted actions unwrapped still passed it, and nothing checked `PopulationForBrainTick`, the gamebot n (3.8).
`APostedActionIsUnattributedInItsServicesStage` fails under a posted action charged to `Other`, to a kind, or run
unwrapped, and the `OWNERS` subtest under the member renamed.

### Task 4: The fleet scope and hook H6

**Files:**
- Modify: `source/server/GameServer/ECS-Services/NpcService.cs:27-31` (hook H6: two lines after :27, one after :31;
  BOM and CRLF throughout)
- Modify: `source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs:15-17` (the header comment), `:187-188` (the
  class summary), `:220-222` (the fleet scope's fields go in after `_stageHook`), `:270-272` (`BeginFleet`, `EndFleet`
  and their cores go in after `TakeSnapshot`, before `ExecuteForEach`), `:273-303` (`ExecuteForEach`'s summary and
  body), `:305-318` (`Metered<T>`'s comment, fields and constructor), `:332` (`Classify`'s `inFleet`)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs:27` (the header comment), `:58` (`InFleetField` goes in
  after `ThreadSlotField`), `:78-83` (`RestoreGlobals`), `:1227-1229` (the class's end: the helpers and 11 tests, 12
  cases)
- Modify: `deploy/tests/test_bot_cpu.py:3-6` (the docstring), `:21-26` (the NS, CPP and H6 constants), `:54-57`
  (`upstream_lines` goes in after `type_body`), `:66-69` (two `HookTests`), `:104-105` (the camp planner's thread
  name goes in after MT's worker name), `:137-140` (one `UpstreamFactTests` check after `OWNERS`), `:155-159` (one
  `EndingsTests` check)
- Test: `source/server/Tests/UnitTests/UT_BotCpuMeter.cs`, `deploy/tests/test_bot_cpu.py`

**Spec:** 3.5 (H6, the flag, off inside the scope, the double-count rule), 5.1 (H6 inlines), 5.2 (H6), 6.1 ("Off = no
wrap" and "Allocation" for the scope, "The fleet rule", "The fleet scope and off", "Per-bot counters": `Fleet` to
none), 6.2 (H6, `BotCpu` in no other file, the nesting stays inside the scope, the camp planner's thread name
`"CampSnapshotPlanner"`, NS's endings).

**Interfaces:**
- Consumes: Task 1's `BotCpuKind.Gamebot` and `BotCpuStage.Fleet`; Task 2's `public static BotCpuCharge
  BotCpuMeter.Classify<T>(T item, BotCpuStage stage, bool inFleet, Func<GamePlayer, bool> isPiloted)`, which, with
  `inFleet` true, charges an item it would call `Unattributed` (an `int` has no owner) to `Gamebot` / `Fleet` with
  `Bot` null and no presence; Task 3's `public static bool BotCpuMeter.Enabled`, `Enable()` (the next run number),
  `Disable()`, `TakeSnapshot()`, `Counters()`, `ExecuteForEach<T>(GameLoopThreadPool pool, List<T> items, int
  toExclusive, Action<T> action)`, the private `int _run`, `[ThreadStatic] long[] _threadSlot`, `long[]
  RegisterSlot()` and `sealed class Metered<T>`, and `public static int BotCpuTotals.CellIndex(BotCpuKind kind,
  BotCpuStage stage)` (the ticks; the calls at +1, the bytes at +2). Upstream, read only (paths under
  `source/server/GameServer/`):
  - `NpcService.Tick` (`ECS-Services/NpcService.cs:25-36`): `ProcessPostedActionsParallel();` at :27, the four
    planning calls at :28-31, then `ServiceObjectStore.UpdateAndGetAll<ABrain>` at :36
  - the camp planning's one nested call, `GameLoop.ExecuteForEach(ProjectionWork, ProjectionWork.Count,
    ProjectCamp);` (`bots/autonomous/AutonomousCampPlanningPipeline.cs:81`, CPP), over `private static readonly
    List<int> ProjectionWork = new(128);` (CPP:22), reached from `public static void PrepareCampPlanningTick()`
    (CPP:38)
  - the camp planner's thread name, `_campBuilder ??= new("CampSnapshotPlanner", BuildCampCatalogDraft);` (CPP:52),
    which `BackgroundSnapshotBuilder`'s constructor gives its thread as it is: `_worker = new Thread(Run) { Name =
    name, IsBackground = true };` (`Managers/GameLoop/BackgroundSnapshotBuilder.cs:28`)

  In the tests, Task 3's `HiddenStatic`, `ThreadPoolField`, `RecordingPool` (the fixture's `_pool`), `Charged`
  (`Ticks`, `Calls`, `Bytes`, `AllCalls`), `Since(BotCpuTotals before)`, `Values(BotCpuTotals totals)`,
  `OnNewThread(Action work)`, `Spin(long ticks)` and `_sink`. In `test_bot_cpu.py`, Task 3's `GAME_SERVER`, `FORK`,
  `LOOP`, `MT`, `GL`, `GUARD`, `source`, `between` and `marked`, and `deploy/tests/endings.py`'s `check(test, path)`
  (:125-129) and `BOM` (:26).
- Produces, in `public static class BotCpuMeter` (namespace `DOL.GS.HearthDAoC`; the public members of Tasks 1-3
  keep their signatures):
  - `[MethodImpl(MethodImplOptions.AggressiveInlining)] public static long BeginFleet()`: off, 0; on, the scope's
    `Stopwatch` start, never 0.
  - `[MethodImpl(MethodImplOptions.AggressiveInlining)] public static void EndFleet(long start)`: nothing for 0.
  - Private: `long BeginFleetCore()` and `void EndFleetCore(long start)`, both
    `[MethodImpl(MethodImplOptions.NoInlining)]`, and the game-loop thread's fields `bool _inFleet`, `int _fleetRun`,
    `long _fleetBytes`, `long _fleetNestedTicks` and `long _fleetNestedBytes`.
  - Changed: `ExecuteForEach<T>` times a call inside this run's scope, with its bytes; `Metered<T>`'s constructor is
    now `Metered(Action<T> action, BotCpuStage stage, bool fleet, ConditionalWeakTable<GameObject, BotCpuCounter>
    counters)`, and `Run` passes `_fleet` as `Classify`'s `inFleet`.
  - What later tasks rely on: a scope is one call in the `Gamebot` × `Fleet` cell of the game-loop thread's slot, with
    no per-bot counter, so the report (Tasks 5 and 9) reads it from `BotCpuTotals` as any other cell.
  - Upstream: H6, three CRLF lines in `NpcService.Tick`.
  - In `UT_BotCpuMeter`: `InFleetField`, `InFleet`, `Millisecond` and `WorkerPool`; `RestoreGlobals` also clears
    `_inFleet`. In `test_bot_cpu.py`: the constants `NS`, `CPP`, `BEGIN_FLEET`, `END_FLEET` and `FLEET_PLANNING`, and
    `upstream_lines(pattern)`, which returns `{path under GameServer/ with '/': [each matching line, stripped]}` for
    every file outside `scripts/hearthdaoc/`. Task 11 extends `EndingsTests`.

- [ ] **Step 1: Write the failing tests.** First the fixture and the helpers in `UT_BotCpuMeter.cs`: the header
comment, a fifth `FieldInfo`, the TearDown and, at the class's end, the section comment and three helpers.
`RestoreGlobals` now also clears `_inFleet`, so a failing test never leaves a scope open for the next. `Millisecond`
is one millisecond in `Stopwatch` ticks. `InFleet` reads the private flag. `WorkerPool` runs the items on a new
thread (Task 3's `OnNewThread`) and waits for it, as the multi-threaded pool's workers do, and rethrows what an item
threw.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
// to its thread's slot and its bot's counter; off, the pool gets the caller's own delegate.
[TestFixture, NonParallelizable]
=======
// to its thread's slot and its bot's counter; off, the pool gets the caller's own delegate. Then hook H6's fleet scope
// (3.5, 5.1): the GameBot fleet planning's own work and its nested call's items go to gamebot / Fleet, counted once.
[TestFixture, NonParallelizable]
>>>>>>> NEW
<<<<<<< OLD
    private static readonly FieldInfo ThreadSlotField = typeof(BotCpuMeter).GetField("_threadSlot", HiddenStatic)!;
=======
    private static readonly FieldInfo ThreadSlotField = typeof(BotCpuMeter).GetField("_threadSlot", HiddenStatic)!;
    private static readonly FieldInfo InFleetField = typeof(BotCpuMeter).GetField("_inFleet", HiddenStatic)!;
>>>>>>> NEW
<<<<<<< OLD
    // A failing test never leaves the meter on, or the setup's test hook set, for the next (6.1).
    [TearDown]
    public void RestoreGlobals()
    {
        BotCpuMeter.Disable();
        StageHookField.SetValue(null, null);
=======
    // A failing test never leaves the meter on, a fleet scope open or the setup's test hook set, for the next (6.1).
    [TearDown]
    public void RestoreGlobals()
    {
        BotCpuMeter.Disable();
        InFleetField.SetValue(null, false);
        StageHookField.SetValue(null, null);
>>>>>>> NEW
<<<<<<< OLD
        Assert.That(new[] { counter.Ticks, counter.Calls, counter.LastTicks, counter.LastCalls }, Is.All.Zero);
    }
}
=======
        Assert.That(new[] { counter.Ticks, counter.Calls, counter.LastTicks, counter.LastCalls }, Is.All.Zero);
    }

    // The fleet scope and hook H6 (3.5, 5.1): NpcService.Tick runs the GameBot fleet planning between BeginFleet and
    // EndFleet, as these tests do. The scope's own work and the items of its nested call (the camp planning's int
    // items, which have no owner) go to gamebot / Fleet, and the nested call's time is taken out of the scope's, so
    // nothing is counted twice.

    private static readonly long Millisecond = Stopwatch.Frequency / 1000;

    private static bool InFleet => (bool)InFleetField.GetValue(null)!;

    // A pool that runs the items on a thread of its own and waits for it, as the multi-threaded pool's workers do.
    private sealed class WorkerPool : GameLoopThreadPool
    {
        public override void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            Exception failure = OnNewThread(() =>
            {
                int count = Math.Min(items.Count, toExclusive);
                for (int i = 0; i < count; i++)
                    action(items[i]);
            });
            if (failure != null)
                throw failure;
        }

        public override void Dispose() { }
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (4 block(s))`.

Then H6's inlining and off (5.1; 6.1 "Off = no wrap" and "Allocation"). `TheFleetScopesCallsInline` checks the
`AggressiveInlining` flag of each public call. The first off test calls `Enable()` and `Disable()` first, as on a
server that was on before. It then turns the meter on inside a scope begun while off: the nested `int` items are
charged as outside any scope (`unattributed`, `Npc`), and `EndFleet(0)` adds nothing. The two allocation tests warm up
first; on, the warm-up gives the thread its slot.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
        public override void Dispose() { }
    }
}
=======
        public override void Dispose() { }
    }

    [TestCase(nameof(BotCpuMeter.BeginFleet))]
    [TestCase(nameof(BotCpuMeter.EndFleet))]
    public void TheFleetScopesCallsInline(string name)
    {
        MethodInfo method = typeof(BotCpuMeter).GetMethod(name)!;
        Assert.That(method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining,
            Is.EqualTo(MethodImplAttributes.AggressiveInlining), "off, H6 is a bool read and a test of a local (5.1)");
    }

    [Test]
    public void OffBeginFleetReturnsZeroAndEndFleetOfZeroChargesNothing()
    {
        BotCpuMeter.Enable();
        BotCpuMeter.Disable();
        GameLoop.ActiveService = nameof(NpcService);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();

        Assert.That(fleetStart, Is.Zero);
        Assert.That(InFleet, Is.False);
        BotCpuMeter.EndFleet(fleetStart);
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)));

        // Turned on inside a scope begun while off: its nested items are charged as outside any scope, and
        // EndFleet(0) charges nothing.
        BotCpuMeter.Enable();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);
        Charged charged = Since(before);
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(2));
        Assert.That(charged.AllCalls, Is.EqualTo(2));
    }

    [Test]
    public void OffTheFleetScopeAllocatesNothing()
    {
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet()); // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet());
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void OnTheFleetScopeItselfAllocatesNothing()
    {
        BotCpuMeter.Enable();
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet()); // warm-up: this thread's slot, if it had none
        long before = GC.GetAllocatedBytesForCurrentThread();
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet());
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (1 block(s))`.

Then the fleet rule (6.1). The scope spins about 1 ms and runs a nested call of 3 `int` items that spin about 2 ms
each, which the recording pool runs inline. `Fleet` must hold at least the items' 6 ms and at most the scope's wall
time plus 5%: counted twice it would be about 13 ms. It has 4 calls, the 3 items and the scope, and no bot has a
counter. The bytes test does the same with 1,000-byte arrays, after one warm-up call outside the scope so the first
call's own allocations stay out: at least 4,000 bytes, and less than the 7,000 a double count gives. The worker test
gives GL a `WorkerPool`, so the nested items run on another thread than the one that began the scope, and they still
go to `Fleet`.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet());
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
=======
        BotCpuMeter.EndFleet(BotCpuMeter.BeginFleet());
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void TheFleetIsTheScopesOwnWorkPlusItsNestedItemsCountedOnce()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        List<int> projections = [1, 2, 3];
        Action<int> project = static _ => Spin(2 * Millisecond);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        // As NpcService.Tick: the scope's own serial work (about 1 ms), and the camp planning's nested call over its
        // int items (3 × about 2 ms), which the recording pool runs inline.
        long wallStart = Stopwatch.GetTimestamp();
        long fleetStart = BotCpuMeter.BeginFleet();
        Assert.That(InFleet, Is.True);
        Spin(Millisecond);
        GameLoop.ExecuteForEach(projections, projections.Count, project);
        BotCpuMeter.EndFleet(fleetStart);
        long wall = Stopwatch.GetTimestamp() - wallStart;

        Charged charged = Since(before);
        long fleet = charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet);
        Assert.Multiple(() =>
        {
            Assert.That(fleetStart, Is.Not.Zero);
            Assert.That(InFleet, Is.False, "EndFleet clears the flag");
            Assert.That(fleet, Is.GreaterThanOrEqualTo(6 * Millisecond), "the nested items' own times");
            // Counted twice (the nested call inside the scope's time, and its items again) it would be about 13 ms.
            Assert.That(fleet, Is.LessThanOrEqualTo(wall * 1.05), "no more than the scope's wall time");
            Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(4), "3 items and the scope");
            Assert.That(charged.AllCalls, Is.EqualTo(4), "nothing else");
            Assert.That(BotCpuMeter.Counters(), Is.Empty, "Fleet counts for no bot");
        });
    }

    [Test]
    public void TheFleetsBytesAreTheScopesOwnPlusItsNestedItemsCountedOnce()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        List<int> projections = [1, 2, 3];
        Action<int> project = static _ => _sink = new byte[1000];
        GameLoop.ExecuteForEach(new List<int> { 0 }, 1, project); // warm-up, outside any scope
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        _sink = new byte[1000];
        GameLoop.ExecuteForEach(projections, projections.Count, project);
        BotCpuMeter.EndFleet(fleetStart);

        long bytes = Since(before).Bytes(BotCpuKind.Gamebot, BotCpuStage.Fleet);
        Assert.That(bytes, Is.GreaterThanOrEqualTo(4 * 1000), "the scope's own 1,000 and the 3 items' 1,000 each");
        // Counted twice, the items' 3,000 bytes would be in the scope's bytes too.
        Assert.That(bytes, Is.LessThan(4 * 1000 + 3 * 1000));
    }

    [Test]
    public void ANestedItemOnAWorkerThreadIsStillFleet()
    {
        // Only the game-loop thread reads the flag: the wrapper carries it to the worker threads (3.1).
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        ThreadPoolField.SetValue(null, new WorkerPool());
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);

        Charged charged = Since(before);
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(3), "2 items and the scope");
        Assert.That(charged.AllCalls, Is.EqualTo(3));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (1 block(s))`.

Then off inside the scope, a nested call that throws and a stale scope (3.5; 6.1 "The fleet scope and off"). Off
inside the scope: `EndFleet` clears the flag and charges nothing; in the next run an `int` batch outside a scope is
`unattributed`, and the next scope is `Fleet` again. Off and on inside the scope: the flag is still set, but for the
closed run, so the new run's nested items are `unattributed` and the scope is dropped. The throwing test first throws
once outside the scope, as a warm-up: when the test's throw was the process's first, its unwinding took about 5.6 ms
and the scope was charged about 6.7 ms; after the warm-up, about 1.5 ms, well under the test's 20 ms bound. Its
nested item spins 20 ms and throws. The item is charged to nothing, as its callback did
not return; the caller gets the same exception object; and the scope is charged less than 20 ms, so the nested call's
time was taken out. The stale-scope test begins a scope that never ends, with a 5 ms nested call, then begins
another. `Fleet` holds the item's 5 ms and the new scope's own 1 ms; without the reset of the nested sums, the new
scope would be charged its 1 ms less the stale 5 ms. `EndFleet(stale)` after it charges nothing, as the flag is
clear.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(charged.AllCalls, Is.EqualTo(3));
    }
}
=======
        Assert.That(charged.AllCalls, Is.EqualTo(3));
    }

    [Test]
    public void OffInsideTheScopeDropsItAndClearsTheFlagAndTheNextRunStartsClean()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        BotCpuMeter.Disable(); // /botcpu off, inside the scope
        BotCpuMeter.EndFleet(fleetStart);

        Assert.That(InFleet, Is.False, "a scope cut by off never leaves the flag set");
        Assert.That(Values(BotCpuMeter.TakeSnapshot()), Is.EqualTo(Values(before)), "the scope is not charged");

        // The next run: an int batch outside any scope is unattributed, and the next scope is Fleet again.
        BotCpuMeter.Enable();
        before = BotCpuMeter.TakeSnapshot();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        Charged outside = Since(before);
        Assert.That(outside.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(2));
        Assert.That(outside.AllCalls, Is.EqualTo(2));

        before = BotCpuMeter.TakeSnapshot();
        fleetStart = BotCpuMeter.BeginFleet();
        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);
        Charged inside = Since(before);
        Assert.That(inside.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(3), "2 items and the scope");
        Assert.That(inside.AllCalls, Is.EqualTo(3));
    }

    [Test]
    public void OffAndOnInsideTheScopeDropsItWithItsNestedItems()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        long fleetStart = BotCpuMeter.BeginFleet();
        BotCpuMeter.Disable();
        BotCpuMeter.Enable(); // off, then on: a new run, inside the old run's scope
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        GameLoop.ExecuteForEach(new List<int> { 1, 2 }, 2, static _ => { });
        BotCpuMeter.EndFleet(fleetStart);

        // The scope belongs to the closed run: neither its own work nor its nested items are the new run's Fleet.
        Charged charged = Since(before);
        Assert.That(InFleet, Is.False);
        Assert.That(charged.Calls(BotCpuKind.Unattributed, BotCpuStage.Npc), Is.EqualTo(2));
        Assert.That(charged.AllCalls, Is.EqualTo(2));
    }

    [Test]
    public void ANestedCallThatThrowsStillAddsItsTimeToTheScopeAndTheExceptionReachesTheCaller()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        InvalidOperationException thrown = new("projection");
        // Warm-up, outside any scope: a process's first exception takes milliseconds to unwind.
        Assert.Throws<InvalidOperationException>(() =>
            GameLoop.ExecuteForEach(new List<int> { 1 }, 1, _ => throw new InvalidOperationException("warm-up")));
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        long fleetStart = BotCpuMeter.BeginFleet();
        Spin(Millisecond);
        Exception caught = Assert.Throws<InvalidOperationException>(() =>
            GameLoop.ExecuteForEach(new List<int> { 1 }, 1, _ =>
            {
                Spin(20 * Millisecond);
                throw thrown;
            }));
        BotCpuMeter.EndFleet(fleetStart);

        Charged charged = Since(before);
        Assert.That(caught, Is.SameAs(thrown), "unchanged");
        // The item that threw is charged to nothing (its callback did not return), and the nested call's 20 ms are
        // still taken out of the scope: the scope's own work is about 1 ms, not 21.
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(1), "the scope only");
        Assert.That(charged.AllCalls, Is.EqualTo(1));
        Assert.That(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.GreaterThanOrEqualTo(Millisecond));
        Assert.That(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.LessThan(20 * Millisecond));
    }

    [Test]
    public void BeginFleetReplacesAStaleScope()
    {
        BotCpuMeter.Enable();
        GameLoop.ActiveService = nameof(NpcService);
        BotCpuTotals before = BotCpuMeter.TakeSnapshot();

        // A scope whose EndFleet never ran (on the server, a throw there stops it), with a nested call of about 5 ms.
        long stale = BotCpuMeter.BeginFleet();
        GameLoop.ExecuteForEach(new List<int> { 1 }, 1, static _ => Spin(5 * Millisecond));
        long fleetStart = BotCpuMeter.BeginFleet();
        Spin(Millisecond);
        BotCpuMeter.EndFleet(fleetStart);
        BotCpuMeter.EndFleet(stale); // the flag is already clear: nothing

        Charged charged = Since(before);
        Assert.That(charged.Calls(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.EqualTo(2), "the item and the new scope");
        // The new scope's own 1 ms, not less the stale scope's 5 ms nested call.
        Assert.That(charged.Ticks(BotCpuKind.Gamebot, BotCpuStage.Fleet), Is.GreaterThanOrEqualTo(6 * Millisecond));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuMeter.cs (1 block(s))`.

Then the source checks of 6.2 in `test_bot_cpu.py`. The docstring now names H6. NS, the camp planning (CPP) and H6's
lines become constants. `upstream_lines(pattern)` walks every file under `GameServer/` outside the fork's folder, not
only `.cs` files, as the spec says "no other file". The build output is outside `GameServer/` (`GameServer.csproj`
puts it under `..\build\` and `..\$(Configuration)\`), so nothing needs skipping. The checks:

- `test_h6_scopes_the_gamebot_fleet_planning`: the order in `Tick`. The posted actions, the comment, `BeginFleet`, the
  four planning calls, `EndFleet` with its trailing mark, then the brains.
- `test_the_meter_is_in_no_other_upstream_file`: the exact upstream lines, GL's guard and NS's two calls.
- `test_the_nested_call_stays_inside_the_fleet_scope`: CPP's one nested call, over the `List<int>` whose items have no
  owner (which is what sends them to `Fleet`), and `PrepareCampPlanningTick` defined in CPP and called only in NS.
- `test_the_npc_service_keeps_its_bom_and_crlf`: NS by `endings.check` against upstream, its BOM, CRLF throughout and
  the three hook lines.
- Task 3's `test_the_game_loop_hands_each_call_to_its_pool`, after MT's worker name: the camp planner's thread name as
  CPP:52 hands it to its builder, and the line of `BackgroundSnapshotBuilder` that gives the thread that name as it is
  (`Managers/GameLoop/BackgroundSnapshotBuilder.cs:28`). The process line finds the thread by it (Task 8): renamed,
  `campThreadCpuPct` would read 0.0 and nothing else would fail.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_bot_cpu.py <<'EDIT'
<<<<<<< OLD
While the meter is on, each game-loop callback is timed and charged to the kind of its owner and to its stage. The hot
path is unit-tested (UT_BotCpuMeter), through the real hook H1 in GameLoop.ExecuteForEach; the hook's shape in the
upstream file, the upstream facts the meter mirrors (the stage of each profile key, the pool's entry point, the member
each item's owner is read from) and the upstream file's line endings need the source, so these check it.
=======
While the meter is on, each game-loop callback is timed and charged to the kind of its owner and to its stage. The hot
path is unit-tested (UT_BotCpuMeter), through the real hook H1 in GameLoop.ExecuteForEach and hook H6's fleet scope;
the hooks' shape in the upstream files, the upstream facts the meter mirrors (the stage of each profile key, the pool's
entry point, the member each item's owner is read from, the one nested call inside the fleet scope) and the upstream
files' line endings need the source, so these check it.
>>>>>>> NEW
<<<<<<< OLD
MT = os.path.join(LOOP, "GameLoopThreadPoolMultiThreaded.cs")

# Hook H1 (spec 3.1): while the meter is on, it gets the pool call; off, one bool read before the unchanged call.
GUARD = ("if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, "
         "action); return; }")
POOL_CALL = "_threadPool.ExecuteForEach(items, toExclusive, action);"
=======
MT = os.path.join(LOOP, "GameLoopThreadPoolMultiThreaded.cs")
NS = os.path.join(GAME_SERVER, "ECS-Services", "NpcService.cs")
CPP = os.path.join(GAME_SERVER, "bots", "autonomous", "AutonomousCampPlanningPipeline.cs")

# Hook H1 (spec 3.1): while the meter is on, it gets the pool call; off, one bool read before the unchanged call.
GUARD = ("if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, "
         "action); return; }")
POOL_CALL = "_threadPool.ExecuteForEach(items, toExclusive, action);"

# Hook H6 (spec 3.5): NpcService.Tick's GameBot fleet planning, between the posted actions and the brains, in a scope
# charged to gamebot / Fleet.
BEGIN_FLEET = "long fleetStart = HearthDAoC.BotCpuMeter.BeginFleet();"
END_FLEET = "HearthDAoC.BotCpuMeter.EndFleet(fleetStart); // HearthDAoC: end of the fleet scope."
FLEET_PLANNING = ["AutonomousBotRegistry.PrepareBrainTick();", "AutonomousWorldBotController.PrepareRvrPlanningTick();",
                  "AutonomousBotGroupCoordinator.PrepareCoordinatorTick();",
                  "AutonomousWorldBotController.PrepareCampPlanningTick();"]
>>>>>>> NEW
<<<<<<< OLD
    return text[start:found.start() if found else len(text)]


class HookTests(unittest.TestCase):
=======
    return text[start:found.start() if found else len(text)]


def upstream_lines(pattern):
    """{path under GameServer/: [each line matching the pattern, stripped]} over every file under GameServer/ outside
    the fork's scripts/hearthdaoc/ folder."""
    found = {}
    for folder, _, names in os.walk(GAME_SERVER):
        if folder == FORK or folder.startswith(FORK + os.sep):
            continue
        for name in names:
            path = os.path.join(folder, name)
            with open(path, "rb") as f:
                text = f.read().decode("utf-8", errors="replace")
            hits = [line.strip() for line in text.splitlines() if re.search(pattern, line)]
            if hits:
                found[os.path.relpath(path, GAME_SERVER).replace(os.sep, "/")] = hits
    return found


class HookTests(unittest.TestCase):
>>>>>>> NEW
<<<<<<< OLD
        self.assertTrue(statements[0].startswith("// HearthDAoC: the per-kind CPU meter (#137)"), statements[0])


class UpstreamFactTests(unittest.TestCase):
=======
        self.assertTrue(statements[0].startswith("// HearthDAoC: the per-kind CPU meter (#137)"), statements[0])

    def test_h6_scopes_the_gamebot_fleet_planning(self):
        tick = between(source(NS), "public override void Tick()", "GameLoop.ExecuteForEach(")
        marked(self, tick, BEGIN_FLEET)
        statements = [line.strip() for line in tick.split("\n")]
        first = statements.index("ProcessPostedActionsParallel();")
        # The posted actions, the comment, BeginFleet, the four planning calls in their order, EndFleet with its
        # trailing mark: nothing else in the scope.
        self.assertTrue(statements[first + 1].startswith("// HearthDAoC: the per-kind CPU meter (#137)"),
                        statements[first + 1])
        self.assertEqual(statements[first + 2:first + 8], [BEGIN_FLEET, *FLEET_PLANNING, END_FLEET])
        for call in FLEET_PLANNING + [END_FLEET]:
            self.assertEqual(tick.count(call), 1, call)
        # Then the brains.
        self.assertLess(tick.index(END_FLEET), tick.index("ServiceObjectStore.UpdateAndGetAll<ABrain>"))

    def test_the_meter_is_in_no_other_upstream_file(self):
        # The upstream lines are H1's guard in GL and H6's two calls in NS (spec 5.2), nothing else.
        self.assertEqual(upstream_lines("BotCpu"), {
            "Managers/GameLoop/GameLoop.cs": [GUARD],
            "ECS-Services/NpcService.cs": [BEGIN_FLEET, END_FLEET],
        })


class UpstreamFactTests(unittest.TestCase):
>>>>>>> NEW
<<<<<<< OLD
        # The loop threads' names, which the process line reads (spec 3.9).
        self.assertIn('Name = $"{GameLoop.THREAD_NAME}_Worker_{i}",', source(MT))
=======
        # The loop threads' names, which the process line reads (spec 3.9).
        self.assertIn('Name = $"{GameLoop.THREAD_NAME}_Worker_{i}",', source(MT))
        # The process line finds the camp planner's thread by this name; renamed, campThreadCpuPct would read 0.0.
        self.assertIn('_campBuilder ??= new("CampSnapshotPlanner", BuildCampCatalogDraft);', source(CPP))
        # Its builder names its thread with that name as it is (Managers/GameLoop/BackgroundSnapshotBuilder.cs:28).
        self.assertIn("_worker = new Thread(Run) { Name = name, IsBackground = true };",
                      source(os.path.join(LOOP, "BackgroundSnapshotBuilder.cs")))
>>>>>>> NEW
<<<<<<< OLD
         "public static int PopulationForBrainTick =>"),
    ]

    def test_each_owner_is_read_from_the_member_the_meter_names(self):
=======
         "public static int PopulationForBrainTick =>"),
    ]

    def test_the_nested_call_stays_inside_the_fleet_scope(self):
        # The camp planning runs the scope's one nested call (spec 3.5), over int items, which have no owner and so go
        # to gamebot / Fleet; it is reached only from H6's scope in NpcService.Tick.
        cpp = source(CPP)
        self.assertEqual(cpp.count("GameLoop.ExecuteForEach("), 1)
        self.assertIn("GameLoop.ExecuteForEach(ProjectionWork, ProjectionWork.Count, ProjectCamp);", cpp)
        self.assertIn("private static readonly List<int> ProjectionWork = new(128);", cpp)
        self.assertEqual(upstream_lines(r"\bPrepareCampPlanningTick\b"), {
            "bots/autonomous/AutonomousCampPlanningPipeline.cs": ["public static void PrepareCampPlanningTick()"],
            "ECS-Services/NpcService.cs": ["AutonomousWorldBotController.PrepareCampPlanningTick();"],
        })

    def test_each_owner_is_read_from_the_member_the_meter_names(self):
>>>>>>> NEW
<<<<<<< OLD
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)


if __name__ == "__main__":
=======
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)

    def test_the_npc_service_keeps_its_bom_and_crlf(self):
        # NS has a BOM and CRLF throughout (spec 5.2); the hook's three lines end CRLF too.
        endings.check(self, NS)
        with open(NS, "rb") as f:
            raw = f.read()
        self.assertTrue(raw.startswith(endings.BOM))
        self.assertEqual(raw.count(b"\n"), raw.count(b"\r\n"), "CRLF throughout")
        hooks = [line for line in raw.split(b"\n") if b"HearthDAoC" in line]
        self.assertEqual(len(hooks), 3)
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)


if __name__ == "__main__":
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_bot_cpu.py (7 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 2 errors, at the two `nameof` in `TheFleetScopesCallsInline`'s
attributes. Each error line starts with the file's absolute path and ends with the project's:

```
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(1260,34): error CS0117: 'BotCpuMeter' does not contain a definition for 'BeginFleet' [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuMeter.cs(1261,34): error CS0117: 'BotCpuMeter' does not contain a definition for 'EndFleet' [source/server/Tests/Tests.csproj]
```

The compiler stops at the attributes, so the uses in the test bodies are not reported. The summary (the warning total
varies between builds of the same tree; 822 was seen):

```
Build FAILED.
    2 Error(s)
```

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: the progress line `.F.FF.....`, then three failures, since NS has no hook yet:

```
FAIL: test_the_npc_service_keeps_its_bom_and_crlf (tests.test_bot_cpu.EndingsTests.test_the_npc_service_keeps_its_bom_and_crlf)
AssertionError: 0 != 3
FAIL: test_h6_scopes_the_gamebot_fleet_planning (tests.test_bot_cpu.HookTests.test_h6_scopes_the_gamebot_fleet_planning)
AssertionError: 0 != 1 : long fleetStart = HearthDAoC.BotCpuMeter.BeginFleet();
FAIL: test_the_meter_is_in_no_other_upstream_file (tests.test_bot_cpu.HookTests.test_the_meter_is_in_no_other_upstream_file)
AssertionError: {'Man[155 chars]; }']} != {'Man[155 chars]; }'], 'ECS-Services/NpcService.cs': ['long fl[132 chars]e.']}
```

The last one's diff names the missing entry, NS's two lines. Then `Ran 10 tests` and `FAILED (failures=3)`.
`test_the_nested_call_stays_inside_the_fleet_scope` and the camp planner's thread name pass already: they pin upstream
as it is.

- [ ] **Step 3: Implement.** `BotCpuMeter.cs` first, in two runs, then H6 in NS.

The first run widens the header comment and the class summary, adds the scope's five fields after `_stageHook`, and
puts `BeginFleet`, `EndFleet` and their cores between `TakeSnapshot` and `ExecuteForEach`. The two public calls are
5.1's inlined guards: off, H6 is a bool read and a test of a local. The cores carry `NoInlining`, so the inlined code
stays that small. `BeginFleetCore` zeroes the nested sums of a stale scope (one whose `EndFleet` never ran), then
notes the run, the thread's allocated bytes and the time, last, so the meter's own work stays out of the scope.
Setting the flag is enough, so it does not clear it first. It returns 1 for a time of 0, since `EndFleet` takes 0 for
"no scope". `EndFleetCore` reads its time and bytes first, then always clears the flag. It charges only when the flag
was set, the meter is still on and the run is the scope's: off inside the scope, or off and on, drops it, and a second
`EndFleet` with the same start charges nothing. The charge is one call to `Gamebot` / `Fleet` in this thread's slot,
with the scope's time and bytes less the nested calls', and no per-bot counter. The slot registration sits in a
`try`, so nothing throws into `NpcService.Tick`. There is no clamp at 0: the nested calls always fall inside the
scope.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
// so the owner can compare the average CPU of each kind of bot. Hook H1 in GameLoop.ExecuteForEach hands each call to
// BotCpuMeter.ExecuteForEach while the meter is on (3.1). BotCpuMeter has no static field initializer and no static
// constructor (5.1): nothing in it runs or allocates while the meter is off.
=======
// so the owner can compare the average CPU of each kind of bot. Hook H1 in GameLoop.ExecuteForEach hands each call to
// BotCpuMeter.ExecuteForEach while the meter is on (3.1), and hook H6 in NpcService.Tick scopes the GameBot fleet
// planning with BeginFleet and EndFleet (3.5). BotCpuMeter has no static field initializer and no static constructor
// (5.1): nothing in it runs or allocates while the meter is off.
>>>>>>> NEW
<<<<<<< OLD
/// <summary>The meter's hot path: the switch and its runs, the wrapper hook H1 hands each call to, the per-thread slots
/// and the per-bot counters it charges, the stage of a profile key, the kind of an owner and what each item charges.
=======
/// <summary>The meter's hot path: the switch and its runs, the wrapper hook H1 hands each call to, the fleet scope of
/// hook H6, the per-thread slots and the per-bot counters they charge, the stage of a profile key, the kind of an owner
/// and what each item charges.
>>>>>>> NEW
<<<<<<< OLD
    private static Func<string, BotCpuStage> _stageHook;

    /// <summary>The number of the current run, or of the last one once off; 0 before the first.</summary>
=======
    private static Func<string, BotCpuStage> _stageHook;

    // The fleet scope (3.5), read and written by the game-loop thread only: the "in fleet" flag, the run the scope
    // began in, that thread's allocated bytes when it began, and the time and bytes of the nested calls inside it.
    private static bool _inFleet;
    private static int _fleetRun;
    private static long _fleetBytes;
    private static long _fleetNestedTicks;
    private static long _fleetNestedBytes;

    /// <summary>The number of the current run, or of the last one once off; 0 before the first.</summary>
>>>>>>> NEW
<<<<<<< OLD
        return BotCpuTotals.Sum(copy);
    }

    /// <summary>
    /// Hook H1's call while the meter is on (3.1). On the game-loop thread, once per call: reads the stage from
=======
        return BotCpuTotals.Sum(copy);
    }

    /// <summary>
    /// Hook H6's start (3.5, 5.1): off, 0 and nothing else; on, the fleet scope begins (<see cref="EndFleet"/> ends
    /// it). <c>NpcService.Tick</c> calls it on the game-loop thread before the GameBot fleet planning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long BeginFleet() => Enabled ? BeginFleetCore() : 0;

    /// <summary>
    /// Hook H6's end (3.5, 5.1): with the <paramref name="start"/> <see cref="BeginFleet"/> returned, ends the scope;
    /// with 0 (begun while off) does nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndFleet(long start)
    {
        if (start != 0)
            EndFleetCore(start);
    }

    // The scope begins (3.5): a stale scope (its EndFleet never ran) is dropped with its nested sums; the run is
    // noted, then this thread's allocated bytes and the time, last, so the meter's own work stays out of the scope.
    // The flag goes on for the calls inside it. Returns the time, never 0, which EndFleet takes for "no scope".
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long BeginFleetCore()
    {
        _fleetNestedTicks = 0;
        _fleetNestedBytes = 0;
        _fleetRun = Volatile.Read(ref _run);
        _fleetBytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        _inFleet = true;
        return start != 0 ? start : 1;
    }

    // The scope ends (3.5): the flag goes off whatever Enabled is now, so a scope cut by off never leaves it set. The
    // scope is charged only while the meter is still on and in the run it began in (off, or off and on, inside it
    // drops it): gamebot / Fleet, one call, with its time and this thread's bytes less the nested calls', whose items
    // the wrappers charged already. Fleet counts for no bot. Never throws into the loop: with no slot the scope is left
    // uncharged, as an item is (3.2).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EndFleetCore(long start)
    {
        long ticks = Stopwatch.GetTimestamp() - start - _fleetNestedTicks;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - _fleetBytes - _fleetNestedBytes;
        bool charge = _inFleet && Volatile.Read(ref Enabled) && _fleetRun == Volatile.Read(ref _run);
        _inFleet = false;
        if (!charge)
            return;

        long[] slot;
        try
        {
            slot = _threadSlot ?? RegisterSlot();
        }
        catch (Exception)
        {
            return;
        }

        int cell = BotCpuTotals.CellIndex(BotCpuKind.Gamebot, BotCpuStage.Fleet);
        slot[cell] += ticks;
        slot[cell + 1]++;
        slot[cell + 2] += bytes;
    }

    /// <summary>
    /// Hook H1's call while the meter is on (3.1). On the game-loop thread, once per call: reads the stage from
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs (4 block(s))`.

The second run changes `ExecuteForEach` and the wrapper. In the setup of a call with items, `nested = _inFleet &&
_fleetRun == Volatile.Read(ref _run)`: a flag left by a scope of an earlier run is not this run's. A nested call
reads the bytes and the time before its wrapper is built, so the meter's setup (the stage, the wrapper and its
delegate, about 100 bytes) is part of the nested time; that is taken out of the scope and charged to no kind, as 3.2
says of the meter's own work. The pool call then runs in a `try`/`finally` that adds its time and bytes to the
scope's nested sums whether it returns or throws; the `finally` catches nothing, so the exception reaches the caller.
A call with nothing to run is not timed. A setup exception sets `nested` back to false: the items, run unmetered with
the caller's delegate, stay in the scope's own work and are charged once to `Fleet`. `Metered<T>` takes the bool as
`fleet` and passes it to `Classify` as `inFleet`. The wrapper carries this resolved bool, "inside this run's scope",
not the flag and the run number that 3.1 names: the run is already tied to the wrapper by the run's per-bot table, and
a flag of an earlier run then sends neither the scope nor its items to the new run's `Fleet`.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs <<'EDIT'
<<<<<<< OLD
    /// Hook H1's call while the meter is on (3.1). On the game-loop thread, once per call: reads the stage from
    /// <see cref="GameLoop.ActiveService"/>, builds one wrapper that carries it and the run's per-bot table to the
    /// worker threads, and hands the wrapper to the pool's own <see cref="GameLoopThreadPool.ExecuteForEach{T}"/>. A
    /// call with nothing to run is passed through unwrapped. The setup never throws: on an exception, after one
    /// warning, the pool gets the caller's own delegate, so each item still runs once, unmetered. An exception from the
    /// pool or a callback reaches the caller unchanged.
    /// </summary>
    public static void ExecuteForEach<T>(GameLoopThreadPool pool, List<T> items, int toExclusive, Action<T> action)
    {
        Action<T> run = action;
        try
        {
            // No table yet: Enabled was set without Enable, and nothing could be charged to a bot.
            ConditionalWeakTable<GameObject, BotCpuCounter> counters = Volatile.Read(ref _counters);
            if (items != null && Math.Min(items.Count, toExclusive) > 0 && counters != null)
            {
                string profileKey = GameLoop.ActiveService;
                BotCpuStage stage = _stageHook != null ? _stageHook(profileKey) : StageOf(profileKey);
                if ((uint)stage >= BotCpuTotals.StageCount)
                    stage = BotCpuStage.Other;
                run = new Metered<T>(action, stage, counters).Run;
            }
        }
        catch (Exception exception)
        {
            run = action;
            WarnSetup(exception);
        }

        pool.ExecuteForEach(items, toExclusive, run);
    }
=======
    /// Hook H1's call while the meter is on (3.1). On the game-loop thread, once per call: reads the stage from
    /// <see cref="GameLoop.ActiveService"/> and whether the call is inside this run's fleet scope (3.5), builds one
    /// wrapper that carries both and the run's per-bot table to the worker threads, and hands the wrapper to the pool's
    /// own <see cref="GameLoopThreadPool.ExecuteForEach{T}"/>. A call inside the scope is timed on this thread, with
    /// its bytes, from before its wrapper is built to the pool's return or throw, barrier wait included, and that is
    /// taken out of the scope's own work, as its items are charged by the wrapper. A call with nothing to run is passed
    /// through unwrapped. The setup never throws: on an exception, after one warning, the pool gets the caller's own
    /// delegate, so each item still runs once, unmetered (inside the scope, as part of its own work). An exception from
    /// the pool or a callback reaches the caller unchanged.
    /// </summary>
    public static void ExecuteForEach<T>(GameLoopThreadPool pool, List<T> items, int toExclusive, Action<T> action)
    {
        Action<T> run = action;
        bool nested = false;
        long start = 0;
        long bytes = 0;
        try
        {
            // No table yet: Enabled was set without Enable, and nothing could be charged to a bot.
            ConditionalWeakTable<GameObject, BotCpuCounter> counters = Volatile.Read(ref _counters);
            if (items != null && Math.Min(items.Count, toExclusive) > 0 && counters != null)
            {
                // A flag left by a scope of an earlier run is not this run's (3.5).
                nested = _inFleet && _fleetRun == Volatile.Read(ref _run);
                if (nested)
                {
                    bytes = GC.GetAllocatedBytesForCurrentThread();
                    start = Stopwatch.GetTimestamp();
                }

                string profileKey = GameLoop.ActiveService;
                BotCpuStage stage = _stageHook != null ? _stageHook(profileKey) : StageOf(profileKey);
                if ((uint)stage >= BotCpuTotals.StageCount)
                    stage = BotCpuStage.Other;
                run = new Metered<T>(action, stage, nested, counters).Run;
            }
        }
        catch (Exception exception)
        {
            run = action;
            nested = false;
            WarnSetup(exception);
        }

        if (!nested)
        {
            pool.ExecuteForEach(items, toExclusive, run);
            return;
        }

        try
        {
            pool.ExecuteForEach(items, toExclusive, run);
        }
        finally
        {
            _fleetNestedTicks += Stopwatch.GetTimestamp() - start;
            _fleetNestedBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
    }
>>>>>>> NEW
<<<<<<< OLD
    // The wrapper of one call (3.2), made on the game-loop thread with the call's stage and the run's per-bot table:
    // the worker threads read neither GameLoop.ActiveService nor a static.
    private sealed class Metered<T>
    {
        private readonly Action<T> _action;
        private readonly BotCpuStage _stage;
        private readonly ConditionalWeakTable<GameObject, BotCpuCounter> _counters;

        internal Metered(Action<T> action, BotCpuStage stage, ConditionalWeakTable<GameObject, BotCpuCounter> counters)
        {
            _action = action;
            _stage = stage;
            _counters = counters;
        }
=======
    // The wrapper of one call (3.2), made on the game-loop thread with the call's stage, whether it is inside the
    // fleet scope, and the run's per-bot table: the worker threads read neither GameLoop.ActiveService nor a static.
    private sealed class Metered<T>
    {
        private readonly Action<T> _action;
        private readonly BotCpuStage _stage;
        private readonly bool _fleet;
        private readonly ConditionalWeakTable<GameObject, BotCpuCounter> _counters;

        internal Metered(Action<T> action, BotCpuStage stage, bool fleet,
            ConditionalWeakTable<GameObject, BotCpuCounter> counters)
        {
            _action = action;
            _stage = stage;
            _fleet = fleet;
            _counters = counters;
        }
>>>>>>> NEW
<<<<<<< OLD
                BotCpuCharge charge = Classify(item, _stage, false, AutonomousPlayerPilot.IsActive);
=======
                BotCpuCharge charge = Classify(item, _stage, _fleet, AutonomousPlayerPilot.IsActive);
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs (3 block(s))`.

The unit tests call `BeginFleet` and `EndFleet` themselves, as H6 does, so they pass now, before NS changes.

Now H6, exactly 3.5's lines: the comment and `BeginFleet` after `ProcessPostedActionsParallel();` (NS:27), and
`EndFleet` with its trailing mark after `PrepareCampPlanningTick();` (NS:31), as the quest services' marks are
(`ECS-Services/DailyQuestService.cs:77`). NS has a BOM and CRLF throughout: the helper keeps the BOM and writes the
new lines with CRLF. `HearthDAoC.BotCpuMeter` resolves from NS's `namespace DOL.GS`.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/ECS-Services/NpcService.cs <<'EDIT'
<<<<<<< OLD
            ProcessPostedActionsParallel();
            AutonomousBotRegistry.PrepareBrainTick();
            AutonomousWorldBotController.PrepareRvrPlanningTick();
            AutonomousBotGroupCoordinator.PrepareCoordinatorTick();
            AutonomousWorldBotController.PrepareCampPlanningTick();
            int lastValidIndex;
=======
            ProcessPostedActionsParallel();
            // HearthDAoC: the per-kind CPU meter (#137) charges the GameBot fleet planning below to gamebot, stage Fleet.
            long fleetStart = HearthDAoC.BotCpuMeter.BeginFleet();
            AutonomousBotRegistry.PrepareBrainTick();
            AutonomousWorldBotController.PrepareRvrPlanningTick();
            AutonomousBotGroupCoordinator.PrepareCoordinatorTick();
            AutonomousWorldBotController.PrepareCampPlanningTick();
            HearthDAoC.BotCpuMeter.EndFleet(fleetStart); // HearthDAoC: end of the fleet scope.
            int lastValidIndex;
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/ECS-Services/NpcService.cs (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (636 was
seen).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: `Passed!  - Failed:     0, Passed:   124, Skipped:     0, Total:   124` (exit code 0). 124 = Task 3's 112 +
this task's 12: 11 tests, `TheFleetScopesCallsInline` with 2 cases. The class takes well under a second when the
machine is idle. The timing tests passed 8 runs in a row, 3 of them with 12 busy-loop shells running (about 1 s
then).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 10 tests` and `OK`.

- [ ] **Step 5: Check the files' form.** The three fork files stay LF, with no BOM, no tab and no line over 120
characters:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs deploy/tests/test_bot_cpu.py <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs False 0 0 True 120
source/server/Tests/UnitTests/UT_BotCpuMeter.cs False 0 0 True 120
deploy/tests/test_bot_cpu.py False 0 0 True 120
```

NS keeps its BOM, and its three new lines end in CRLF (`EndingsTests` checks the same):

Run: `head -c 3 source/server/GameServer/ECS-Services/NpcService.cs | od -An -tx1`
Expected: ` ef bb bf`.

Run: `git diff source/server/GameServer/ECS-Services/NpcService.cs | cat -A | grep '^+ '`
Expected: the three hook lines, each ending `^M$`:

```
+            // HearthDAoC: the per-kind CPU meter (#137) charges the GameBot fleet planning below to gamebot, stage Fleet.^M$
+            long fleetStart = HearthDAoC.BotCpuMeter.BeginFleet();^M$
+            HearthDAoC.BotCpuMeter.EndFleet(fleetStart); // HearthDAoC: end of the fleet scope.^M$
```

- [ ] **Step 6: Run the wider checks.**

CI's fork filter (it already names `UT_BotCpuMeter`, from Task 1):

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   768, Skipped:     0, Total:   768` (756 after Task 3, and this task's
12).

The whole C# project, since `NpcService.Tick` changed:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3556, Skipped:     0, Total:  3556` (3544 after Task 3, and 12; about 30
seconds of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_endings.py`
Expected: `Ran 11 tests` and `OK`.

The whole deploy suite with the world tests (about three minutes; run it after the unit tests, not at the same time):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 754 tests` and `OK (skipped=10)` (750 after Task 3, and `test_bot_cpu.py`'s 4 new tests).

Run: `git status --short`
Expected: only these four lines (`serverconfig.xml` is ignored, so it does not show):

```
 M deploy/tests/test_bot_cpu.py
 M source/server/GameServer/ECS-Services/NpcService.cs
 M source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs
 M source/server/Tests/UnitTests/UT_BotCpuMeter.cs
```

Run: `git diff --numstat`
Expected:

```
82	3	deploy/tests/test_bot_cpu.py
3	0	source/server/GameServer/ECS-Services/NpcService.cs
121	15	source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs
261	2	source/server/Tests/UnitTests/UT_BotCpuMeter.cs
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/ECS-Services/NpcService.cs source/server/GameServer/scripts/hearthdaoc/BotCpuMeter.cs source/server/Tests/UnitTests/UT_BotCpuMeter.cs deploy/tests/test_bot_cpu.py
git commit -F - <<'EOF'
feat(cpu-meter): the fleet scope and hook h6

Hook H6 in NpcService.Tick puts the GameBot fleet planning (population sampling, RvR planning, group
coordination and camp planning) between BotCpuMeter.BeginFleet and EndFleet, so that work is charged to
gamebot, stage Fleet, instead of to nothing (spec 3.5). Off, both calls inline to a bool read and a test
of a local (5.1). On, BeginFleet notes the run, the thread's allocated bytes and the time and sets the
in-fleet flag; the setup of each call reads the flag once, for this run only, and the wrapper carries it
to the workers, so an item with no owner inside the scope, such as the camp planning's int items, goes to
gamebot / Fleet. The nested pool call is timed on the game-loop thread with its bytes, returned or
thrown, and EndFleet charges one call with the scope's time and bytes less the nested calls', so nothing
is counted twice and Fleet counts for no bot. EndFleet always clears the flag and drops a scope cut by
off, or by off and on. UT_BotCpuMeter pins the fleet rule, off, the stale scope and a throwing nested
call (6.1); test_bot_cpu.py checks H6's order, that the meter is in no other upstream file, that the
camp planning's one nested call is reached only from the scope, and NpcService.cs's BOM and CRLF (6.2).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `4 files changed, 467 insertions(+), 20 deletions(-)`.

Review: approved with no code change; its five Minor findings changed no line, and one shaped this section (Step 3
says why the wrapper carries a resolved bool, not 3.1's flag and run number). Left open: no test reaches the setup
fallback inside a scope (`nested = false` in the catch; a case with `_stageHook` throwing inside a scope over one
2 ms `int` item, expecting one `Fleet` call of the scope with at least 2 ms, would pin it); the worker test cannot
tell the wrapper's bool from the static flag, which a worker could also read; `OffTheFleetScopeAllocatesNothing` relies
on the TearDown for off and does not assert it; and the `PrepareCampPlanningTick` check skips the fork's folder. A gap
found after Task 11 is closed here with no production change (the build's test-only commits 54b35eb and 79236c9):
nothing pinned the camp planner's thread name, so a rename at CPP:52, or a `BackgroundSnapshotBuilder` that gave its
thread another name or none, would read `campThreadCpuPct=0.0` with every test passing. The two checks after MT's
worker name fail under each of those mutations.

### Task 5: The report's numbers

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs` (the registry samples, a listed bot, the
  spread, a top entry, a compare pair, one kind's figures, the window, the totals since on, and the static
  `BotCpuReport`)
- Create: `source/server/Tests/UnitTests/UT_BotCpuReport.cs` (the helpers and 33 tests, 50 cases)
- Modify: `.github/workflows/server-image.yml:119` (CI's fork filter gains `UT_BotCpuReport`)
- Modify: `deploy/tests/test_workflows.py:280` (`SERVER_UNIT_TESTS`' last line), `:311` (the expected names' last
  line)
- Test: `source/server/Tests/UnitTests/UT_BotCpuReport.cs`, `deploy/tests/test_workflows.py`

**Spec:** 2.1 (the kind line's fields and the `-` rules, `BOT_CPU_TOP`'s ranking and names, the process line's `tps`,
`chargedPct` and `callsSec`, `BOT_CPU_COMPARE`'s pairs and ratios), 2.2 (the window's length, a final window under
5 s, no registry sample), 2.4 (the totals since on or reset), 3.8 (`n`, bot-seconds), 3.10 (a bot's window, the
spread, the top 5), 6.1 `UT_BotCpuReport` (snapshot difference, arithmetic, nothing to divide by, window length, since
on, spread, top 5, compare), Review Focus 3 (a bot present in the previous listing and absent from this one), 6.4
(`UT_BotCpuReport` in CI's filter and `test_workflows.py`). The lines' text, the CSV and the chat replies are Task 6's.

**Interfaces:**
- Consumes, in `namespace DOL.GS.HearthDAoC` (`scripts/hearthdaoc/BotCpuMeter.cs` as Task 4 left it; this task does
  not change it):
  - Task 1's `public enum BotCpuKind` (:29-47, kind order `Gamebot`, `Companion`, `Playerbot`, `Piloted`, `Player`,
    `Monster`, `Unattributed`) and `public enum BotCpuStage` (:55-75, 18 stages in CSV order).
  - Task 2's `public static bool BotCpuMeter.IsBotKind(BotCpuKind kind)` (:587-588): true for `Gamebot`, `Companion`,
    `Playerbot` and `Piloted`.
  - Task 3's `public sealed class BotCpuTotals` (:100-162): `KindCount` (7), `StageCount` (18), `SlotLength` (401),
    `static int CellIndex(BotCpuKind, BotCpuStage)` (the ticks; the calls at +1, the bytes at +2), `static int
    PresenceIndex(BotCpuKind)`, `long Ticks(BotCpuKind, BotCpuStage)`, `Calls(...)`, `Bytes(...)`,
    `long Presence(BotCpuKind)`, `void Add(BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)`,
    `void AddPresence(BotCpuKind kind, long count)` and `static BotCpuTotals Sum(IEnumerable<long[]> slots)`; and
    `public sealed class BotCpuCounter` (:171-186): the public fields `long Ticks` and `long Calls` (the loop
    threads'), `long LastTicks` and `long LastCalls` (the report's), `readonly BotCpuKind Kind`, and the constructor
    `BotCpuCounter(BotCpuKind kind)`.
- Produces, all new, in `scripts/hearthdaoc/BotCpuReport.cs` (namespace `DOL.GS.HearthDAoC`):
  - `public readonly record struct BotCpuSamples(long Sum, int Count, int? Last)` with `BotCpuSamples Add(int sample)`,
    `BotCpuSamples NextWindow()` (no sample yet, the last one kept) and `double Average` (the average, else `Last`,
    else 0): GameBot `n` (3.8, 2.2).
  - `public sealed record BotCpuBot(BotCpuKind Kind, string Name, int Level, string ClassName, int Region, string
    Goal, string Activity, long Ticks, long Calls)`: a bot as the report lists it, with its window's ticks and calls;
    `Goal` and `Activity` are null without a `PersistentRecord`.
  - `public readonly record struct BotCpuSpread(double P50, double P95, double Max)`.
  - `public sealed record BotCpuTopEntry(int Rank, BotCpuBot Bot, double MsSec, double CallsSec)` (rank from 1).
  - `public sealed record BotCpuCompare(BotCpuKind Kind, double N, double GamebotN, double? Cpu, double? Alloc,
    double? Calls, double? UsPerCall)`: the ratios unrounded, null for `-`.
  - `public sealed class BotCpuKindWindow` (internal constructor): `Kind`; `double? N` (null for unattributed);
    `double BotSeconds` (`N` × the exact seconds, 0 when `N` is null or not above 0); `long Ticks`, `Calls`, `Bytes`
    (the whole kind, every stage); `double ChargedMs`, `MsSec`, `CallsSec`, `AllocKBSec`; `double? MsPerBotSec`,
    `CallsPerBotSec`, `AllocKBPerBotSec` (null with no bot-seconds); `double? UsPerCall` (null with no calls);
    `BotCpuSpread? Spread` (null when no bot of the kind was charged, and for a kind that is not a bot kind); `double?
    P50`, `P95`, `Max` (null with no bot-seconds or no spread); `IReadOnlyList<double> StageValues` (by `(int)stage`:
    the line's values, per bot per s, or per s for unattributed; null means no part after `|`; stages that round to
    0.000 are still in it); `IReadOnlyList<double> StageMsSec` (the CSV's 18 stage columns, the whole kind per s); and
    `internal bool HasLine`.
  - `public sealed class BotCpuWindow` (internal constructor): `double Seconds` (exact), `int Win` (rounded, at least
    1), `double GameTicks`, `Tps`, `ChargedPct`, `CallsSec`; `IReadOnlyList<BotCpuKindWindow> Kinds` (the kinds with a
    line, in kind order); `IReadOnlyList<BotCpuCompare> Compares` (playerbot, piloted, player); `BotCpuKindWindow
    Kind(BotCpuKind kind)` (null when the kind writes no line); `IReadOnlyList<BotCpuTopEntry> Top(BotCpuKind kind)`
    (at most 5; empty when none of the kind's bots was charged, and for a kind that is not a bot kind).
  - `public sealed class BotCpuSince`, immutable: `static BotCpuSince Start(bool reset)`, `bool Reset`, `int Windows`,
    `double Seconds`, `double Minutes`, `double ChargedMs(BotCpuKind)`, `double BotSeconds(BotCpuKind)`, `double?
    MsPerBotSec(BotCpuKind)` (null with no bot-seconds) and `BotCpuSince Add(BotCpuWindow window)` (a null window
    adds nothing and returns the same totals).
  - `public static class BotCpuReport`: `const double MinimumWindowSeconds = 5`, `const int TopCount = 5`, `const
    double CompareMinimumN = 1.0`; `static BotCpuTotals Difference(BotCpuTotals later, BotCpuTotals earlier)`;
    `static (long Ticks, long Calls) TakeCounterWindow(BotCpuCounter counter)`; `static double Round(double value,
    int decimals)`; `static BotCpuSpread? Spread(IEnumerable<double> values)`; `static string TopName(string name)`;
    `static BotCpuWindow Window(BotCpuTotals counts, double seconds, long frequency, long gameTimeChange, double
    tickDuration, BotCpuSamples gamebots, IEnumerable<BotCpuBot> bots)` (null when `!(seconds >= 5)`; past that it
    throws `ArgumentNullException` for null `counts` and `ArgumentOutOfRangeException` for `frequency` of 0 or less).
  - What later tasks rely on:
    - Task 6 formats from these objects. `Round(v, d).ToString("F" + d, CultureInfo.InvariantCulture)` and
      `v.ToString("F" + d, CultureInfo.InvariantCulture)` give the same text. It uses `TopName` for `BOT_CPU_TOP`,
      `StageValues == null` for "no part after `|`", `StageMsSec` for the CSV's stage columns, `CallsSec` and
      `AllocKBSec` for unattributed's line and every CSV row, and the window's `Tps`, `ChargedPct` and `CallsSec` for
      the process line (its platform values are Task 8's). `BotCpuSince.Reset` picks "Since on" or "Since reset", and
      `Minutes` gives `N`. A bot kind with bot-seconds but no spread (`Spread` null, `BotSeconds` above 0) is Task 6's
      to write as `p50=- p95=- max=-` or without those fields: 2.1's `-` rule covers only no bot-seconds, and 6.1
      says "no bot gives no fields".
    - Task 9 keeps the previous `BotCpuTotals` and calls `Difference(BotCpuMeter.TakeSnapshot(), previous)`. It keeps
      one `BotCpuSamples` per run: `Add(AutonomousBotRegistry.PopulationForBrainTick)` each second and `NextWindow()`
      at each close. For each entry of `BotCpuMeter.Counters()` it calls `TakeCounterWindow(counter)` and builds a
      `BotCpuBot` from the bot (`Name`, `Level`, `GameBot.ClassName` or a player's `CharacterClass?.Name`, the region
      read once as `CurrentRegion?.ID ?? 0`, its `PersistentRecord`'s `ObjectiveKind` and `Activity` or null) with
      `counter.Kind` and the window's ticks and calls. `Window(...)` returning null is a final window too short to
      write. Since on: `Start(false)` at on, `Start(true)` at reset, then `since = since.Add(window)`. Its own tests
      check that the open window is not in the published totals.
    - Task 6 (no `BOT_CPU_TOP` line) and Task 10 ("No gamebot was charged in the last minute.") key on
      `Top(kind).Count == 0`. Task 10 uses `MinimumWindowSeconds` for "The last 3 s were too short to write."
  - In `UT_BotCpuReport`, for later tasks: `Ticks(double ms, long frequency = Linux)`, `Charge(counts, kind, stage,
    double ms, long calls, long bytes = 0, long frequency = Linux)`, `Samples(params int[])`, `Close(counts, double
    seconds = 60, long frequency = Linux, BotCpuSamples gamebots = default, IEnumerable<BotCpuBot> bots = null, long?
    gameTimeChange = null)`, `WholeWindow` (1,800 ticks), `Tolerance` and the kind constants (`Gamebot` to
    `Unattributed`).

- [ ] **Step 1: Write the failing tests.** Create `source/server/Tests/UnitTests/UT_BotCpuReport.cs` with exactly the
code below (LF, no BOM: the Write tool, or a quoted heredoc). It is a plain `[TestFixture]`: `BotCpuReport` touches no
shared state, so the class needs no `NonParallelizable`. `Close` closes a window as the report does, with game time
grown by the window's length (60 s at `TickMs` is 1,800 ticks, 30.0 tps) unless a test gives the change. `Bot` lists a
bot worth `msSec` ms per s in a 60 s window, with 1,800 calls unless given; a bot with 0 calls is listed but was not
charged in the window. The snapshot tests fill slots as the wrapper does, through Task 3's `CellIndex` and
`PresenceIndex`, sum them with `BotCpuTotals.Sum`, and compare the totals by name with `NonZero`.
`EachKindsFiguresComeFromItsRawCounts` runs at `Stopwatch.Frequency` 1e9 (Linux) and 1e7 (Windows). `Numbers` walks
every public property of a window by reflection, and the top 5 of each kind, so `NoFigureIsNaNOrInfinity` sees every
`double` the report gives. `TwoWindows`' first window is 61.4 s (`win=61`), so the since-on tests tell the exact
lengths from the wins. In `TheTopFiveAreRankedByMsThenByName`, Brennos (6.0196) and Cathal (6.0204) both show 6.020,
so they are in name order; Gorm and Halvard have no call in the window, and counting them would move `p50` from 5.104
to 4.950. In `ABotNotChargedInTheWindowIsLeftOutOfTheSpreadAndTheTop`, Ivar's one call took under one tick: charged,
at 0 ms per s. `SampleCounts` builds 2.1's own sample kind lines (gamebot 1.840, 412.3, 4.46, 31.2; player 2.104,
390.0, 5.39, 60.4), whose compare line is `cpu=1.14x alloc=1.94x calls=0.95x usPerCall=1.21x`. The `Round` cases pin
what .NET 10's invariant `"F"` format does: it rounds the double's exact value, so 1.0005 (just under the midpoint in
binary) gives 1.0, and an exact binary midpoint (0.125, 60.5, 412.25) goes to the even digit.

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.GS.HearthDAoC;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter's report (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.1). Once a
// minute the report takes the difference of two snapshots of the per-thread slots (3.7) and turns it, with the window's
// exact length, the game time and the registry samples, into each kind's figures (2.1, 2.2, 3.8), the spread and the
// top 5 of each bot kind from the per-bot counters (3.10), the compare pairs (2.1) and the totals since on (2.4).
// BotCpuReport works on plain values only, with no server state, so these tests drive it directly.
[TestFixture]
public sealed class UT_BotCpuReport
{
    // Stopwatch.Frequency on Linux (1 ns ticks) and on Windows (100 ns ticks).
    private const long Linux = 1_000_000_000;
    private const long Windows = 10_000_000;

    // GameLoop.TickDuration at the server's 30 ticks a second: a 60 s window at 30.0 tps is 1,800 ticks (3.8).
    private const double TickMs = 1000.0 / 30;
    private const long WholeWindow = 1_800;

    private const double Tolerance = 1e-9;

    private const BotCpuKind Gamebot = BotCpuKind.Gamebot;
    private const BotCpuKind Companion = BotCpuKind.Companion;
    private const BotCpuKind Playerbot = BotCpuKind.Playerbot;
    private const BotCpuKind Piloted = BotCpuKind.Piloted;
    private const BotCpuKind Player = BotCpuKind.Player;
    private const BotCpuKind Monster = BotCpuKind.Monster;
    private const BotCpuKind Unattributed = BotCpuKind.Unattributed;

    // The Stopwatch ticks of ms milliseconds.
    private static long Ticks(double ms, long frequency = Linux) => (long)Math.Round(ms * frequency / 1000);

    // As a window's difference holds them: ms of time, calls and bytes charged to [kind, stage].
    private static void Charge(BotCpuTotals counts, BotCpuKind kind, BotCpuStage stage, double ms, long calls,
        long bytes = 0, long frequency = Linux) => counts.Add(kind, stage, Ticks(ms, frequency), calls, bytes);

    // The registry samples of a window, one a second (3.8).
    private static BotCpuSamples Samples(params int[] samples) =>
        samples.Aggregate(new BotCpuSamples(), (sum, sample) => sum.Add(sample));

    // A window as the report closes it, with game time grown by the window's length unless given.
    private static BotCpuWindow Close(BotCpuTotals counts, double seconds = 60, long frequency = Linux,
        BotCpuSamples gamebots = default, IEnumerable<BotCpuBot> bots = null, long? gameTimeChange = null) =>
        BotCpuReport.Window(counts, seconds, frequency, gameTimeChange ?? (long)Math.Round(seconds * 1000), TickMs,
            gamebots, bots ?? []);

    // A bot as the report lists it (3.10): its ticks and calls in a 60 s window, worth msSec ms per s.
    private static BotCpuBot Bot(BotCpuKind kind, string name, double msSec, long calls = WholeWindow) =>
        new(kind, name, 34, "Cleric", 163, "Rvr", "Hunting", Ticks(msSec * 60), calls);

    // A spread computed from the counts: its three values, within the tolerance.
    private static void AssertSpread(BotCpuSpread? spread, double p50, double p95, double max)
    {
        Assert.That(spread, Is.Not.Null);
        Assert.That(spread.Value.P50, Is.EqualTo(p50).Within(Tolerance));
        Assert.That(spread.Value.P95, Is.EqualTo(p95).Within(Tolerance));
        Assert.That(spread.Value.Max, Is.EqualTo(max).Within(Tolerance));
    }

    // ---- The snapshot difference (3.7, a window's steps 1-4)

    private static long[] Slot() => new long[BotCpuTotals.SlotLength];

    // As the wrapper adds to its thread's slot (3.2, step 5).
    private static void Add(long[] slot, BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)
    {
        int cell = BotCpuTotals.CellIndex(kind, stage);
        slot[cell] += ticks;
        slot[cell + 1] += calls;
        slot[cell + 2] += bytes;
    }

    // Every value of the totals that is not zero, by kind, stage and value.
    private static Dictionary<string, long> NonZero(BotCpuTotals totals)
    {
        Dictionary<string, long> values = [];
        void Put(string name, long value)
        {
            if (value != 0)
                values[name] = value;
        }

        foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
        {
            foreach (BotCpuStage stage in Enum.GetValues<BotCpuStage>())
            {
                Put($"{kind}/{stage}/ticks", totals.Ticks(kind, stage));
                Put($"{kind}/{stage}/calls", totals.Calls(kind, stage));
                Put($"{kind}/{stage}/bytes", totals.Bytes(kind, stage));
            }

            Put($"{kind}/presence", totals.Presence(kind));
        }

        return values;
    }

    [Test]
    public void TheWindowHoldsOnlyTheAddsBetweenTwoSnapshots()
    {
        long[] one = Slot();
        long[] two = Slot();
        Add(one, Gamebot, BotCpuStage.Npc, 1_000, 2, 64);
        Add(two, Player, BotCpuStage.ClientEnd, 500, 1, 0);
        two[BotCpuTotals.PresenceIndex(Player)] += 3;
        BotCpuTotals first = BotCpuTotals.Sum([one, two]);

        Add(one, Gamebot, BotCpuStage.Npc, 400, 1, 32);
        Add(two, Monster, BotCpuStage.Npc, 70, 1, 0);
        two[BotCpuTotals.PresenceIndex(Player)] += 2;
        BotCpuTotals second = BotCpuTotals.Sum([one, two]);

        Assert.That(NonZero(BotCpuReport.Difference(second, first)), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["Gamebot/Npc/ticks"] = 400, ["Gamebot/Npc/calls"] = 1, ["Gamebot/Npc/bytes"] = 32,
            ["Monster/Npc/ticks"] = 70, ["Monster/Npc/calls"] = 1,
            ["Player/presence"] = 2,
        }));
    }

    [Test]
    public void ASlotThatAppearsBetweenSnapshotsCountsFromZero()
    {
        long[] one = Slot();
        Add(one, Gamebot, BotCpuStage.Npc, 1_000, 2, 64);
        BotCpuTotals first = BotCpuTotals.Sum([one]);

        // A worker the watchdog restarted, or a thread's first metered item: a new slot, in the list from then on.
        long[] restarted = Slot();
        Add(restarted, Companion, BotCpuStage.Attack, 300, 3, 96);
        restarted[BotCpuTotals.PresenceIndex(Companion)] += 4;
        Add(one, Gamebot, BotCpuStage.Npc, 10, 1, 0);
        BotCpuTotals second = BotCpuTotals.Sum([one, restarted]);

        Assert.That(NonZero(BotCpuReport.Difference(second, first)), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["Gamebot/Npc/ticks"] = 10, ["Gamebot/Npc/calls"] = 1,
            ["Companion/Attack/ticks"] = 300, ["Companion/Attack/calls"] = 3, ["Companion/Attack/bytes"] = 96,
            ["Companion/presence"] = 4,
        }));
    }

    [Test]
    public void ANegativeDifferenceCountsAsZero()
    {
        long[] one = Slot();
        long[] two = Slot();
        Add(one, Gamebot, BotCpuStage.Npc, 1_000, 2, 64);
        Add(two, Player, BotCpuStage.ClientEnd, 500, 1, 8);
        two[BotCpuTotals.PresenceIndex(Player)] += 3;
        BotCpuTotals first = BotCpuTotals.Sum([one, two]);

        // Totals that went down (a slot gone from the list, which the meter never does): 0, never negative.
        Add(one, Gamebot, BotCpuStage.Npc, 40, 1, 0);
        BotCpuTotals second = BotCpuTotals.Sum([one]);

        Assert.That(NonZero(BotCpuReport.Difference(second, first)), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["Gamebot/Npc/ticks"] = 40, ["Gamebot/Npc/calls"] = 1,
        }));
    }

    [Test]
    public void ReadingNeverChangesASlot()
    {
        long[] one = Slot();
        long[] two = Slot();
        Add(one, Gamebot, BotCpuStage.Fleet, 1_000, 1, 64);
        Add(two, Monster, BotCpuStage.Npc, 500, 7, 8);
        two[BotCpuTotals.PresenceIndex(Monster)] += 7;
        long[] oneBefore = (long[])one.Clone();
        long[] twoBefore = (long[])two.Clone();

        BotCpuTotals first = BotCpuTotals.Sum([one, two]);
        BotCpuTotals second = BotCpuTotals.Sum([one, two]);
        BotCpuTotals window = BotCpuReport.Difference(second, first);

        Assert.That(one, Is.EqualTo(oneBefore));
        Assert.That(two, Is.EqualTo(twoBefore));
        Assert.That(NonZero(window), Is.Empty);
        Assert.That(NonZero(first), Is.EquivalentTo(NonZero(second)));
    }

    // ---- The arithmetic (2.1, 3.8)

    [TestCase(Linux)]
    [TestCase(Windows)]
    public void EachKindsFiguresComeFromItsRawCounts(long frequency)
    {
        BotCpuTotals counts = new();
        // 240 GameBots by the registry, 14,400 ms in 60 s: 1.0 ms per bot per s, the Fleet scope included.
        Charge(counts, Gamebot, BotCpuStage.Npc, 12_000, 1_200_000, 40_000_000, frequency);
        Charge(counts, Gamebot, BotCpuStage.Fleet, 2_400, 240_000, 4_236_800, frequency);
        // One player playing the whole window: 1,800 presences in 1,800 ticks.
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientEnd, 60, 20_000, 3_000_000, frequency);
        Charge(counts, Player, BotCpuStage.Attack, 30, 2_000, 686_400, frequency);
        Charge(counts, Player, BotCpuStage.Pets, 30, 2_000, 0, frequency);
        // 1,830 live monster brains every tick.
        counts.AddPresence(Monster, 1_830 * WholeWindow);
        Charge(counts, Monster, BotCpuStage.Npc, 5_760, 6_588_000, 2_635_776, frequency);
        // Ownerless timers.
        Charge(counts, Unattributed, BotCpuStage.Timer, 72.6, 132_600, 5_406_720, frequency);

        BotCpuWindow window = Close(counts, frequency: frequency, gamebots: Samples(236, 240, 244));

        Assert.That(window.Seconds, Is.EqualTo(60));
        Assert.That(window.Win, Is.EqualTo(60));
        Assert.That(window.GameTicks, Is.EqualTo(1_800).Within(Tolerance));
        Assert.That(window.Tps, Is.EqualTo(30.0).Within(Tolerance));
        Assert.That(window.CallsSec, Is.EqualTo((1_440_000 + 24_000 + 6_588_000 + 132_600) / 60.0).Within(Tolerance));
        // All time charged to all kinds, unattributed included: 20,352.6 ms in 60 s, as % of one core.
        Assert.That(window.ChargedPct, Is.EqualTo(20_352.6 / 60 / 10).Within(Tolerance));
        Assert.That(window.Kinds.Select(kind => kind.Kind),
            Is.EqualTo(new[] { Gamebot, Player, Monster, Unattributed }));

        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        Assert.That(gamebot.N, Is.EqualTo(240).Within(Tolerance));
        Assert.That(gamebot.BotSeconds, Is.EqualTo(240 * 60).Within(Tolerance));
        Assert.That(gamebot.ChargedMs, Is.EqualTo(14_400).Within(Tolerance));
        Assert.That(gamebot.MsSec, Is.EqualTo(240).Within(Tolerance));
        Assert.That(gamebot.MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(gamebot.CallsPerBotSec, Is.EqualTo(100.0).Within(Tolerance));
        Assert.That(gamebot.UsPerCall, Is.EqualTo(10.0).Within(Tolerance));
        Assert.That(gamebot.AllocKBPerBotSec, Is.EqualTo(3.0).Within(Tolerance));
        Assert.That(gamebot.CallsSec, Is.EqualTo(24_000).Within(Tolerance));
        Assert.That(gamebot.AllocKBSec, Is.EqualTo(720).Within(Tolerance));

        BotCpuKindWindow player = window.Kind(Player);
        Assert.That(player.N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(player.BotSeconds, Is.EqualTo(60).Within(Tolerance));
        Assert.That(player.MsSec, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(player.MsPerBotSec, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(player.CallsPerBotSec, Is.EqualTo(400.0).Within(Tolerance));
        Assert.That(player.UsPerCall, Is.EqualTo(5.0).Within(Tolerance));
        Assert.That(player.AllocKBPerBotSec, Is.EqualTo(60.0).Within(Tolerance));

        BotCpuKindWindow monster = window.Kind(Monster);
        Assert.That(monster.N, Is.EqualTo(1_830).Within(Tolerance));
        Assert.That(monster.MsSec, Is.EqualTo(96.0).Within(Tolerance));
        Assert.That(monster.MsPerBotSec, Is.EqualTo(5_760.0 / (1_830 * 60)).Within(Tolerance));
        Assert.That(monster.CallsPerBotSec, Is.EqualTo(60.0).Within(Tolerance));
        Assert.That(monster.UsPerCall, Is.EqualTo(5_760_000.0 / 6_588_000).Within(Tolerance));
        Assert.That(monster.AllocKBPerBotSec, Is.EqualTo(2_574.0 / (1_830 * 60)).Within(Tolerance));

        // Unattributed has no n: calls and allocation per second instead of per bot (2.1).
        BotCpuKindWindow unattributed = window.Kind(Unattributed);
        Assert.That(unattributed.N, Is.Null);
        Assert.That(unattributed.BotSeconds, Is.EqualTo(0));
        Assert.That(unattributed.MsSec, Is.EqualTo(1.21).Within(Tolerance));
        Assert.That(unattributed.CallsSec, Is.EqualTo(2_210.0).Within(Tolerance));
        Assert.That(unattributed.AllocKBSec, Is.EqualTo(88.0).Within(Tolerance));
        Assert.That(unattributed.UsPerCall, Is.EqualTo(72_600.0 / 132_600).Within(Tolerance));
        Assert.That(unattributed.MsPerBotSec, Is.Null);
        Assert.That(unattributed.CallsPerBotSec, Is.Null);
        Assert.That(unattributed.AllocKBPerBotSec, Is.Null);
    }

    [Test]
    public void OneMsPerSecondFor240BotsIs24PercentOfOneCore()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 14_400, 432_000);

        BotCpuWindow window = Close(counts, gamebots: Samples(240));

        Assert.That(window.Kind(Gamebot).MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).MsSec, Is.EqualTo(240).Within(Tolerance));
        Assert.That(window.ChargedPct, Is.EqualTo(24.0).Within(Tolerance));
    }

    [Test]
    public void PresenceGivesNOverTheWindowsGameTicks()
    {
        BotCpuTotals counts = new();
        counts.AddPresence(Companion, 2 * 900); // two companions, each half the window
        counts.AddPresence(Playerbot, 900);
        counts.AddPresence(Piloted, 2 * WholeWindow);
        foreach (BotCpuKind kind in new[] { Companion, Playerbot, Piloted })
            Charge(counts, kind, BotCpuStage.Npc, 6, 1_800);

        BotCpuWindow window = Close(counts);
        Assert.That(window.Kind(Companion).N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Playerbot).N, Is.EqualTo(0.5).Within(Tolerance));
        Assert.That(window.Kind(Piloted).N, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(window.Kind(Piloted).MsPerBotSec, Is.EqualTo(6.0 / (2 * 60)).Within(Tolerance));

        // A loop running late: game time grew by 30 s in a 60 s window, 900 ticks, 15.0 tps. n divides by the ticks.
        BotCpuWindow late = Close(counts, gameTimeChange: 30_000);
        Assert.That(late.GameTicks, Is.EqualTo(900).Within(Tolerance));
        Assert.That(late.Tps, Is.EqualTo(15.0).Within(Tolerance));
        Assert.That(late.Kind(Companion).N, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(late.Kind(Companion).BotSeconds, Is.EqualTo(120).Within(Tolerance));
    }

    [Test]
    public void AStagesValueIsPerBotOnTheLineAndForTheWholeKindInTheCsv()
    {
        BotCpuTotals counts = new();
        counts.AddPresence(Player, 2 * WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientBegin, 18, 3_600);
        Charge(counts, Player, BotCpuStage.ClientEnd, 157.2, 3_600);
        Charge(counts, Player, BotCpuStage.Pets, 0.0006, 1);
        Charge(counts, Unattributed, BotCpuStage.Npc, 413.4, 60_000);
        Charge(counts, Unattributed, BotCpuStage.Zone, 35.88, 6_000);

        BotCpuWindow window = Close(counts);
        BotCpuKindWindow player = window.Kind(Player);
        // The line's stage values: ms per bot per s, adding up to msPerBotSec (2.1).
        Assert.That(player.StageValues[(int)BotCpuStage.ClientBegin], Is.EqualTo(0.15).Within(Tolerance));
        Assert.That(player.StageValues[(int)BotCpuStage.ClientEnd], Is.EqualTo(1.31).Within(Tolerance));
        Assert.That(player.StageValues[(int)BotCpuStage.Timer], Is.EqualTo(0));
        Assert.That(player.StageValues.Sum(), Is.EqualTo(player.MsPerBotSec).Within(Tolerance));
        // The CSV's stage columns: the whole kind's raw stage ticks ÷ the exact window length, ms per s (2.3).
        Assert.That(player.StageMsSec[(int)BotCpuStage.ClientBegin], Is.EqualTo(0.3).Within(Tolerance));
        Assert.That(player.StageMsSec[(int)BotCpuStage.ClientEnd], Is.EqualTo(2.62).Within(Tolerance));
        Assert.That(player.StageMsSec[(int)BotCpuStage.Pets], Is.EqualTo(0.00001).Within(Tolerance));
        Assert.That(player.StageMsSec.Sum(), Is.EqualTo(player.MsSec).Within(Tolerance));
        Assert.That(player.StageMsSec, Has.Count.EqualTo(BotCpuTotals.StageCount));

        // Unattributed's line has its stage values per s, the same as its CSV columns, adding up to msSec.
        BotCpuKindWindow unattributed = window.Kind(Unattributed);
        Assert.That(unattributed.StageValues[(int)BotCpuStage.Npc], Is.EqualTo(6.89).Within(Tolerance));
        Assert.That(unattributed.StageValues, Is.EqualTo(unattributed.StageMsSec));
        Assert.That(unattributed.StageValues.Sum(), Is.EqualTo(unattributed.MsSec).Within(Tolerance));
    }

    [Test]
    public void AKindWithNothingChargedAndNoNHasNoLine()
    {
        BotCpuTotals counts = new();
        Charge(counts, Player, BotCpuStage.Timer, 1, 30);
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Unattributed, BotCpuStage.Timer, 1, 30);

        // GameBots in the registry but none charged still have a line; companion, playerbot, piloted and monster none.
        BotCpuWindow window = Close(counts, gamebots: Samples(12));
        Assert.That(window.Kinds.Select(kind => kind.Kind), Is.EqualTo(new[] { Gamebot, Player, Unattributed }));
        foreach (BotCpuKind kind in new[] { Companion, Playerbot, Piloted, Monster })
            Assert.That(window.Kind(kind), Is.Null, kind.ToString());

        Assert.That(Close(new BotCpuTotals()).Kinds, Is.Empty);
    }

    // ---- Nothing to divide by (2.1)

    [Test]
    public void ChargedTimeWithNoBotSecondsGivesNoPerBotFieldAndNoStageValues()
    {
        BotCpuTotals counts = new();
        // A player bot whose client is not registered in ClientService: charged, never present (2.5, section 9).
        Charge(counts, Playerbot, BotCpuStage.Timer, 30, 900, 2_048);
        // GameBots charged before the registry ever gave a sample.
        Charge(counts, Gamebot, BotCpuStage.Npc, 60, 1_800);

        BotCpuWindow window = Close(counts, bots: [Bot(Playerbot, "Proto", 0.5, 900)]);

        foreach (BotCpuKind kind in new[] { Playerbot, Gamebot })
        {
            BotCpuKindWindow figures = window.Kind(kind);
            Assert.That(figures.N, Is.EqualTo(0), kind.ToString());
            Assert.That(figures.BotSeconds, Is.EqualTo(0), kind.ToString());
            Assert.That(figures.MsPerBotSec, Is.Null, kind.ToString());
            Assert.That(figures.CallsPerBotSec, Is.Null, kind.ToString());
            Assert.That(figures.AllocKBPerBotSec, Is.Null, kind.ToString());
            Assert.That(figures.P50, Is.Null, kind.ToString());
            Assert.That(figures.P95, Is.Null, kind.ToString());
            Assert.That(figures.Max, Is.Null, kind.ToString());
            Assert.That(figures.StageValues, Is.Null, kind.ToString());
        }

        // msSec still shows the cost, usPerCall has its calls, and the CSV keeps the stage totals.
        BotCpuKindWindow playerbot = window.Kind(Playerbot);
        Assert.That(playerbot.MsSec, Is.EqualTo(0.5).Within(Tolerance));
        Assert.That(playerbot.UsPerCall, Is.EqualTo(30_000.0 / 900).Within(Tolerance));
        Assert.That(playerbot.StageMsSec[(int)BotCpuStage.Timer], Is.EqualTo(0.5).Within(Tolerance));
        // Its bot was charged: a spread and a top line, though the line's p50, p95 and max are - (2.1).
        AssertSpread(playerbot.Spread, 0.5, 0.5, 0.5);
        Assert.That(window.Top(Playerbot).Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Proto" }));
    }

    [Test]
    public void NoCallsGiveNoUsPerCall()
    {
        // 240 GameBots in the registry and nothing charged: a line of zeros, but no cost per call.
        BotCpuWindow window = Close(new BotCpuTotals(), gamebots: Samples(240));

        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        Assert.That(gamebot.UsPerCall, Is.Null);
        Assert.That(gamebot.MsSec, Is.EqualTo(0));
        Assert.That(gamebot.MsPerBotSec, Is.EqualTo(0));
        Assert.That(gamebot.CallsPerBotSec, Is.EqualTo(0));
        Assert.That(gamebot.AllocKBPerBotSec, Is.EqualTo(0));
        // No bot charged: no spread.
        Assert.That(gamebot.Spread, Is.Null);
        Assert.That(gamebot.P50, Is.Null);
        Assert.That(window.Top(Gamebot), Is.Empty);
    }

    // Every number in the window and everything it holds, through its public properties and the top 5 of each kind.
    private static IEnumerable<double> Numbers(object value)
    {
        switch (value)
        {
            case null or string:
                yield break;
            case double number:
                yield return number;
                yield break;
            case IEnumerable list:
                foreach (object item in list)
                {
                    foreach (double number in Numbers(item))
                        yield return number;
                }

                yield break;
        }

        Type type = value.GetType();
        if (type.Namespace != typeof(BotCpuReport).Namespace)
            yield break;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            foreach (double number in Numbers(property.GetValue(value)))
                yield return number;
        }

        if (value is BotCpuWindow window)
        {
            foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
            {
                foreach (double number in Numbers(window.Top(kind)))
                    yield return number;
            }
        }
    }

    [Test]
    public void NoFigureIsNaNOrInfinity()
    {
        BotCpuTotals counts = new();
        foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
        {
            counts.AddPresence(kind, 5);
            Charge(counts, kind, BotCpuStage.Npc, 3, 0); // time with no calls, which no wrapper adds
            counts.Add(kind, BotCpuStage.Zone, 0, 4, 100); // calls with no time
        }

        BotCpuBot[] bots = [Bot(Gamebot, "Idle", 0, 0), Bot(Companion, "Zero", 0), Bot(Playerbot, "Proto", 2)];
        BotCpuWindow[] windows =
        [
            Close(new BotCpuTotals(), seconds: BotCpuReport.MinimumWindowSeconds),
            Close(counts, bots: bots),
            Close(counts, gamebots: Samples(0), bots: bots, gameTimeChange: 0), // game time stood still
            Close(counts, seconds: 1e6, frequency: Windows, gamebots: Samples(3), bots: bots, gameTimeChange: -5),
        ];

        foreach (BotCpuWindow window in windows)
        {
            double[] numbers = Numbers(window).ToArray();
            Assert.That(numbers, Is.Not.Empty);
            Assert.That(numbers.Where(number => !double.IsFinite(number)), Is.Empty);
        }

        Assert.That(windows[2].Kind(Monster).N, Is.EqualTo(0));
        Assert.That(windows[2].Tps, Is.EqualTo(0));
    }

    // ---- The window's length (2.2)

    [Test]
    public void AWindowOf61_4SecondsDividesBy61_4AndIsWin61()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 240 * 61.4, 61_400);
        counts.AddPresence(Player, 1_842);
        Charge(counts, Player, BotCpuStage.ClientEnd, 61.4, 614);

        BotCpuWindow window = Close(counts, seconds: 61.4, gamebots: Samples(240));

        Assert.That(window.Seconds, Is.EqualTo(61.4));
        Assert.That(window.Win, Is.EqualTo(61));
        Assert.That(window.GameTicks, Is.EqualTo(1_842).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).MsSec, Is.EqualTo(240).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).CallsSec, Is.EqualTo(1_000).Within(Tolerance));
        Assert.That(window.Kind(Player).N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Player).MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(Close(counts, seconds: 59.6).Win, Is.EqualTo(60));
    }

    [TestCase(0.4)]
    [TestCase(4.99)]
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(double.NaN)]
    public void AFinalWindowUnder5SecondsWritesNothing(double seconds)
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 3, 30);

        Assert.That(Close(counts, seconds: seconds, gamebots: Samples(240)), Is.Null);
    }

    [Test]
    public void AFinalWindowOf5SecondsIsWritten()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 3, 30);

        BotCpuWindow window = Close(counts, seconds: 5, gamebots: Samples(240));
        Assert.That(window.Win, Is.EqualTo(5));
        Assert.That(window.Kind(Gamebot).MsSec, Is.EqualTo(0.6).Within(Tolerance));
    }

    [Test]
    public void AWindowWithNoRegistrySampleUsesTheLastOne()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 4_800, 48_000);

        // The previous window's samples were 238 then 240; this one has none.
        BotCpuSamples none = Samples(238, 240).NextWindow();
        Assert.That(none.Count, Is.EqualTo(0));

        BotCpuKindWindow gamebot = Close(counts, seconds: 5, gamebots: none).Kind(Gamebot);
        Assert.That(gamebot.N, Is.EqualTo(240));
        Assert.That(gamebot.MsPerBotSec, Is.EqualTo(4_800.0 / (240 * 5)).Within(Tolerance));

        // The next window's samples count alone, and with no sample ever n is 0.
        Assert.That(none.Add(100).Add(102).Average, Is.EqualTo(101));
        Assert.That(new BotCpuSamples().Average, Is.EqualTo(0));
    }

    // ---- Since on (2.4)

    // A first window of 61.4 s (win=61): 240 GameBots at 1.0 ms per bot per s and one player at 2.0. Then 60 s: 200
    // GameBots at 2.0 and no player.
    private static (BotCpuWindow First, BotCpuWindow Second) TwoWindows()
    {
        BotCpuTotals first = new();
        Charge(first, Gamebot, BotCpuStage.Npc, 14_736, 432_000);
        first.AddPresence(Player, 1_842);
        Charge(first, Player, BotCpuStage.ClientEnd, 122.8, 3_600);
        Charge(first, Unattributed, BotCpuStage.Timer, 600, 3_600);

        BotCpuTotals second = new();
        Charge(second, Gamebot, BotCpuStage.Npc, 24_000, 360_000);
        Charge(second, Unattributed, BotCpuStage.Timer, 600, 3_600);

        return (Close(first, seconds: 61.4, gamebots: Samples(240)), Close(second, gamebots: Samples(200)));
    }

    [Test]
    public void SinceOnIsTheChargedMsOverTheBotSecondsOfTheWindowsWritten()
    {
        (BotCpuWindow first, BotCpuWindow second) = TwoWindows();

        BotCpuSince since = BotCpuSince.Start(reset: false).Add(first).Add(second);

        Assert.That(since.Reset, Is.False);
        Assert.That(since.Windows, Is.EqualTo(2));
        // The windows' exact lengths, not their wins (61 + 60).
        Assert.That(first.Win, Is.EqualTo(61));
        Assert.That(since.Seconds, Is.EqualTo(121.4).Within(Tolerance));
        Assert.That(since.Minutes, Is.EqualTo(121.4 / 60).Within(Tolerance));
        Assert.That(since.ChargedMs(Gamebot), Is.EqualTo(38_736).Within(Tolerance));
        Assert.That(since.BotSeconds(Gamebot), Is.EqualTo(240 * 61.4 + 200 * 60).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Gamebot), Is.EqualTo(38_736.0 / 26_736).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Player), Is.EqualTo(2.0).Within(Tolerance));
        // Only kinds with bot-seconds: never unattributed, nor a kind that was never present.
        Assert.That(since.MsPerBotSec(Unattributed), Is.Null);
        Assert.That(since.MsPerBotSec(Companion), Is.Null);
    }

    [Test]
    public void AfterResetOnlyTheLaterWindowsCount()
    {
        (BotCpuWindow _, BotCpuWindow second) = TwoWindows();

        BotCpuSince since = BotCpuSince.Start(reset: true).Add(second);

        Assert.That(since.Reset, Is.True);
        Assert.That(since.Windows, Is.EqualTo(1));
        Assert.That(since.Minutes, Is.EqualTo(1).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Gamebot), Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Player), Is.Null);
    }

    [Test]
    public void ThePartialWindowIsNotCounted()
    {
        (BotCpuWindow first, BotCpuWindow second) = TwoWindows();
        BotCpuSince start = BotCpuSince.Start(reset: false);
        Assert.That(start.Windows, Is.EqualTo(0));
        Assert.That(start.MsPerBotSec(Gamebot), Is.Null);

        BotCpuSince since = start.Add(first).Add(second);
        // The open window counts only once it is written; a final window under 5 s never is (its counts are dropped).
        BotCpuTotals open = new();
        Charge(open, Gamebot, BotCpuStage.Npc, 9_999, 99);
        BotCpuWindow tooShort = Close(open, seconds: 3, gamebots: Samples(240));
        Assert.That(tooShort, Is.Null);

        BotCpuSince after = since.Add(tooShort);
        Assert.That(after.Windows, Is.EqualTo(2));
        Assert.That(after.Seconds, Is.EqualTo(121.4).Within(Tolerance));
        Assert.That(after.MsPerBotSec(Gamebot), Is.EqualTo(38_736.0 / 26_736).Within(Tolerance));
        // Adding never changes the totals it started from.
        Assert.That(start.Windows, Is.EqualTo(0));
        Assert.That(start.ChargedMs(Gamebot), Is.EqualTo(0));
    }

    // ---- The spread (3.10)

    [Test]
    public void TheSpreadOfOneToTwentyIsP50TenP95NineteenMaxTwenty()
    {
        double[] values = Enumerable.Range(1, 20).Select(value => (double)value).Reverse().ToArray();

        Assert.That(BotCpuReport.Spread(values), Is.EqualTo(new BotCpuSpread(10, 19, 20)));
    }

    [Test]
    public void OneBotGivesAllThreeEqual() =>
        Assert.That(BotCpuReport.Spread([4.25]), Is.EqualTo(new BotCpuSpread(4.25, 4.25, 4.25)));

    [Test]
    public void NoBotGivesNoSpread()
    {
        Assert.That(BotCpuReport.Spread([]), Is.Null);
        Assert.That(BotCpuReport.Spread(null), Is.Null);
    }

    // ---- The top 5 (3.10)

    [Test]
    public void TheTopFiveAreRankedByMsThenByName()
    {
        BotCpuBot aldric = Bot(Gamebot, "Aldric", 7.412, 52_800);
        BotCpuBot[] bots =
        [
            Bot(Gamebot, "Eirik", 4.950), Bot(Gamebot, "Fenn", 1.0), Bot(Gamebot, "Cathal", 6.0204), aldric,
            Bot(Gamebot, "Gorm", 0, 0), Bot(Gamebot, "Brennos", 6.0196), Bot(Gamebot, "Dagny", 5.104),
            Bot(Gamebot, "Halvard", 0, 0), Bot(Companion, "Hilda", 9.0),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 60_000, 600_000);
        Charge(counts, Companion, BotCpuStage.Npc, 540, 1_800);
        counts.AddPresence(Companion, WholeWindow);

        BotCpuWindow window = Close(counts, gamebots: Samples(240), bots: bots);

        IReadOnlyList<BotCpuTopEntry> top = window.Top(Gamebot);
        // Brennos and Cathal are both 6.020 at the line's 3 decimals: a tie, so by name, though Cathal's ms is higher.
        Assert.That(top.Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Aldric", "Brennos", "Cathal", "Dagny", "Eirik" }));
        Assert.That(top.Select(entry => entry.Rank), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(top[0].Bot, Is.SameAs(aldric));
        Assert.That(top[0].MsSec, Is.EqualTo(7.412).Within(Tolerance));
        Assert.That(top[0].CallsSec, Is.EqualTo(880).Within(Tolerance));
        Assert.That(top[2].MsSec, Is.EqualTo(6.0204).Within(Tolerance));

        // The spread is over the six GameBots charged in the window, nearest rank: p50 at rank 3 of 6, p95 and max at
        // rank 6. Gorm and Halvard, listed with no call in it, are left out: counted, p50 would be 4.950, rank 4 of 8.
        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        AssertSpread(gamebot.Spread, 5.104, 7.412, 7.412);
        Assert.That(gamebot.P50, Is.EqualTo(5.104).Within(Tolerance));
        Assert.That(gamebot.P95, Is.EqualTo(7.412).Within(Tolerance));
        Assert.That(gamebot.Max, Is.EqualTo(7.412).Within(Tolerance));

        Assert.That(window.Top(Companion).Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Hilda" }));
        Assert.That(window.Kind(Companion).Max, Is.EqualTo(9.0).Within(Tolerance));
    }

    [Test]
    public void FewerThanFiveBotsGiveFewerEntries()
    {
        // A player charged in the window, listed with the bots: the per-bot table only holds bot kinds (3.10), so the
        // report leaves out any other kind it is given.
        BotCpuBot[] bots = [Bot(Companion, "Hilda", 2.0), Bot(Player, "Gm", 4.0), Bot(Companion, "Ivar", 3.0)];
        BotCpuTotals counts = new();
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientEnd, 240, 1_800);

        BotCpuWindow window = Close(counts, bots: bots);

        Assert.That(window.Top(Companion).Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Ivar", "Hilda" }));
        Assert.That(window.Top(Gamebot), Is.Empty);
        Assert.That(window.Top(Playerbot), Is.Empty);
        Assert.That(window.Top(Piloted), Is.Empty);
        // Only the bot kinds have a top 5 and a spread.
        Assert.That(window.Top(Player), Is.Empty);
        Assert.That(window.Kind(Player).Spread, Is.Null);
        Assert.That(window.Kind(Player).P50, Is.Null);
        Assert.That(window.Top(Unattributed), Is.Empty);
    }

    [Test]
    public void ABotNotChargedInTheWindowIsLeftOutOfTheSpreadAndTheTop()
    {
        // Gorm and Jorun were charged in an earlier window, so the per-bot table still lists them, with no call in this
        // one. Ivar's one call took under one Stopwatch tick: charged at least once, at 0 ms per s (3.10).
        BotCpuBot[] bots =
        [
            Bot(Gamebot, "Gorm", 0, 0),
            Bot(Companion, "Hilda", 2.0), Bot(Companion, "Ivar", 0, 1), Bot(Companion, "Jorun", 0, 0),
            Bot(Companion, "Kari", 3.0),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Companion, BotCpuStage.Npc, 300, 3_601);
        counts.AddPresence(Companion, 4 * WholeWindow);

        BotCpuWindow window = Close(counts, gamebots: Samples(240), bots: bots);

        // No GameBot charged: no spread, so P50, P95 and Max are null though gamebot has bot-seconds, and no top, so no
        // BOT_CPU_TOP line for gamebot and "No gamebot was charged in the last minute." (2.1, 2.4).
        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        Assert.That(gamebot.BotSeconds, Is.EqualTo(240 * 60).Within(Tolerance));
        Assert.That(gamebot.Spread, Is.Null);
        Assert.That(gamebot.P50, Is.Null);
        Assert.That(gamebot.P95, Is.Null);
        Assert.That(gamebot.Max, Is.Null);
        Assert.That(window.Top(Gamebot), Is.Empty);

        // Companion: Kari, Hilda and Ivar, not Jorun. Over those three p50 is 2.000 at rank 2; with Jorun's 0 it would
        // be 0.000.
        IReadOnlyList<BotCpuTopEntry> top = window.Top(Companion);
        Assert.That(top.Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Kari", "Hilda", "Ivar" }));
        Assert.That(top[2].MsSec, Is.EqualTo(0));
        Assert.That(top[2].CallsSec, Is.EqualTo(1 / 60.0).Within(Tolerance));
        AssertSpread(window.Kind(Companion).Spread, 2.0, 3.0, 3.0);
    }

    [TestCase("Aldric", "Aldric")]
    [TestCase("Sir Aldric", "Sir_Aldric")]
    [TestCase("a:b c:", "a_b_c_")]
    [TestCase("", "-")]
    [TestCase(null, "-")]
    public void ASpaceOrColonInATopNameBecomesAnUnderscore(string name, string expected) =>
        Assert.That(BotCpuReport.TopName(name), Is.EqualTo(expected));

    [Test]
    public void ACountersWindowIsItsAddsSinceTheReportLastReadIt()
    {
        BotCpuCounter counter = new(Gamebot) { Ticks = 1_000, Calls = 20 };

        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((1_000L, 20L)));
        Assert.That((counter.LastTicks, counter.LastCalls), Is.EqualTo((1_000L, 20L)));
        // The loop threads' values are never written by the report.
        Assert.That((counter.Ticks, counter.Calls), Is.EqualTo((1_000L, 20L)));

        counter.Ticks += 300;
        counter.Calls += 3;
        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((300L, 3L)));
        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((0L, 0L)));

        // Values that went down, which the loop threads never make: 0, never negative.
        counter.LastTicks = 5_000;
        counter.LastCalls = 50;
        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((0L, 0L)));
    }

    // A bot as the report lists it from the per-bot table, with its window's ticks and calls.
    private static BotCpuBot Listed(BotCpuCounter counter, string name)
    {
        (long ticks, long calls) = BotCpuReport.TakeCounterWindow(counter);
        return new BotCpuBot(counter.Kind, name, 50, "Armsman", 163, null, null, ticks, calls);
    }

    [Test]
    public void ABotAbsentFromThisListingLeavesNoStaleEntry()
    {
        BotCpuCounter aldric = new(Gamebot);
        BotCpuCounter brennos = new(Gamebot);
        BotCpuCounter cathal = new(Gamebot);
        aldric.Ticks += Ticks(60);
        brennos.Ticks += Ticks(600);
        cathal.Ticks += Ticks(120);
        aldric.Calls += 1_800;
        brennos.Calls += 1_800;
        cathal.Calls += 1_800;

        BotCpuWindow first = Close(new BotCpuTotals(), gamebots: Samples(3),
            bots: [Listed(aldric, "Aldric"), Listed(brennos, "Brennos"), Listed(cathal, "Cathal")]);
        Assert.That(first.Top(Gamebot).Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Brennos", "Cathal", "Aldric" }));

        // Brennos runs on, then leaves the world: its counter is collected, or simply absent from the next listing.
        aldric.Ticks += Ticks(300);
        brennos.Ticks += Ticks(6_000);
        cathal.Ticks += Ticks(30);
        aldric.Calls += 1_800;
        brennos.Calls += 1_800;
        cathal.Calls += 1_800;

        BotCpuWindow second = Close(new BotCpuTotals(), gamebots: Samples(2),
            bots: [Listed(aldric, "Aldric"), Listed(cathal, "Cathal")]);

        // The window, the spread and the top 5 come from the bots present, with only their adds since the last read.
        IReadOnlyList<BotCpuTopEntry> top = second.Top(Gamebot);
        Assert.That(top.Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Aldric", "Cathal" }));
        Assert.That(top.Select(entry => entry.MsSec), Is.EqualTo(new[] { 5.0, 0.5 }).Within(Tolerance));
        AssertSpread(second.Kind(Gamebot).Spread, 0.5, 5.0, 5.0);
    }

    // ---- The compare pairs (2.1)

    // The kind lines of 2.1's sample, whose BOT_CPU_COMPARE line is cpu=1.14x alloc=1.94x calls=0.95x usPerCall=1.21x:
    // 240 GameBots at msPerBotSec 1.840, callsPerBotSec 412.3, usPerCall 4.46 and allocKBPerBotSec 31.2, and one player
    // at 2.104, 390.0, 5.39 and 60.4.
    private static BotCpuTotals SampleCounts()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 1.840 * 240 * 60, 5_937_120, 460_062_720);
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientEnd, 2.104 * 60, 23_400, 3_710_976);
        return counts;
    }

    [Test]
    public void ACompareIsWrittenOnlyForTheThreePairsWithBothNAtLeastOne()
    {
        BotCpuTotals counts = SampleCounts();
        (BotCpuKind Kind, long Presence)[] others = [(Playerbot, 1_800), (Piloted, 1_620), (Companion, 5_400)];
        foreach ((BotCpuKind kind, long presence) in others)
        {
            counts.AddPresence(kind, presence);
            Charge(counts, kind, BotCpuStage.Timer, 60, 1_800);
        }

        BotCpuWindow window = Close(counts, gamebots: Samples(240));
        // playerbot n=1.0 and player n=1.0 with gamebot n=240.0; piloted n=0.9 is under 1.0; companion is no pair.
        Assert.That(window.Compares.Select(compare => compare.Kind), Is.EqualTo(new[] { Playerbot, Player }));
        Assert.That(window.Compares.Select(compare => compare.GamebotN), Is.All.EqualTo(240).Within(Tolerance));
        Assert.That(window.Compares[0].N, Is.EqualTo(1.0).Within(Tolerance));

        // n is compared as the line shows it, to 1 decimal: 0.96 is n=1.0.
        BotCpuTotals almost = SampleCounts();
        almost.AddPresence(Playerbot, 1_728);
        Charge(almost, Playerbot, BotCpuStage.Timer, 60, 1_800);
        Assert.That(Close(almost, gamebots: Samples(240)).Compares.Select(compare => compare.Kind),
            Is.EqualTo(new[] { Playerbot, Player }));

        // GameBots under 1.0: no pair at all.
        Assert.That(Close(SampleCounts(), gamebots: Samples(0, 1)).Compares, Is.Empty);
    }

    [Test]
    public void AllThreePairsAreWrittenInTheirOrder()
    {
        BotCpuTotals counts = SampleCounts();
        // One player bot, one piloted player and three companions, each present the whole window. Piloted is at twice
        // gamebot's msPerBotSec (3.680), callsPerBotSec (824.6) and allocKBPerBotSec (62.4), at usPerCall 4.46.
        counts.AddPresence(Playerbot, WholeWindow);
        Charge(counts, Playerbot, BotCpuStage.Timer, 60, 1_800);
        counts.AddPresence(Piloted, WholeWindow);
        Charge(counts, Piloted, BotCpuStage.ClientEnd, 3.680 * 60, 49_476, 3_833_856);
        counts.AddPresence(Companion, 3 * WholeWindow);
        Charge(counts, Companion, BotCpuStage.Npc, 60, 1_800);

        BotCpuWindow window = Close(counts, gamebots: Samples(240));

        // The three pairs in 2.1's order: playerbot/gamebot, piloted/gamebot, player/gamebot. Companion is no pair.
        Assert.That(window.Compares.Select(compare => compare.Kind), Is.EqualTo(new[] { Playerbot, Piloted, Player }));
        Assert.That(window.Compares.Select(compare => compare.N), Is.All.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Compares.Select(compare => compare.GamebotN), Is.All.EqualTo(240).Within(Tolerance));
        BotCpuCompare piloted = window.Compares[1];
        Assert.That(piloted.Cpu, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(piloted.Alloc, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(piloted.Calls, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(piloted.UsPerCall, Is.EqualTo(1.0).Within(Tolerance));
    }

    [Test]
    public void CompareRatiosAreOfTheKindLinesFigures()
    {
        BotCpuWindow window = Close(SampleCounts(), gamebots: Samples(240));
        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        BotCpuKindWindow player = window.Kind(Player);
        Assert.That(BotCpuReport.Round(gamebot.MsPerBotSec.Value, 3), Is.EqualTo(1.840));
        Assert.That(BotCpuReport.Round(gamebot.CallsPerBotSec.Value, 1), Is.EqualTo(412.3));
        Assert.That(BotCpuReport.Round(gamebot.UsPerCall.Value, 2), Is.EqualTo(4.46));
        Assert.That(BotCpuReport.Round(gamebot.AllocKBPerBotSec.Value, 1), Is.EqualTo(31.2));
        Assert.That(BotCpuReport.Round(player.UsPerCall.Value, 2), Is.EqualTo(5.39));

        BotCpuCompare compare = window.Compares.Single();
        Assert.That(compare.Kind, Is.EqualTo(Player));
        Assert.That(compare.N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(compare.GamebotN, Is.EqualTo(240).Within(Tolerance));
        // The ratios of the fields as the kind lines show them, written with 2 decimals.
        Assert.That(compare.Cpu, Is.EqualTo(2.104 / 1.840).Within(Tolerance));
        Assert.That(compare.Alloc, Is.EqualTo(60.4 / 31.2).Within(Tolerance));
        Assert.That(compare.Calls, Is.EqualTo(390.0 / 412.3).Within(Tolerance));
        Assert.That(compare.UsPerCall, Is.EqualTo(5.39 / 4.46).Within(Tolerance));
        Assert.That(new[] { compare.Cpu, compare.Alloc, compare.Calls, compare.UsPerCall }
            .Select(ratio => BotCpuReport.Round(ratio.Value, 2)), Is.EqualTo(new[] { 1.14, 1.94, 0.95, 1.21 }));
    }

    [Test]
    public void AGamebotValueOfZeroOrADashGivesADash()
    {
        // GameBots in the registry with nothing charged: msPerBotSec, callsPerBotSec and allocKBPerBotSec 0, and
        // usPerCall -.
        BotCpuTotals idle = new();
        idle.AddPresence(Player, WholeWindow);
        Charge(idle, Player, BotCpuStage.ClientEnd, 120, 3_600, 1_024);
        BotCpuCompare compare = Close(idle, gamebots: Samples(240)).Compares.Single();
        Assert.That(compare.Cpu, Is.Null);
        Assert.That(compare.Alloc, Is.Null);
        Assert.That(compare.Calls, Is.Null);
        Assert.That(compare.UsPerCall, Is.Null);

        // A gamebot value that shows as 0.000 is 0 too; a player with no calls has usPerCall -.
        BotCpuTotals tiny = new();
        Charge(tiny, Gamebot, BotCpuStage.Npc, 0.0004 * 240 * 60, 432_000, 1_024 * 240 * 60);
        tiny.AddPresence(Player, WholeWindow);
        compare = Close(tiny, gamebots: Samples(240)).Compares.Single();
        Assert.That(compare.Cpu, Is.Null);
        Assert.That(compare.Alloc, Is.EqualTo(0));
        Assert.That(compare.Calls, Is.EqualTo(0));
        Assert.That(compare.UsPerCall, Is.Null);
    }

    // Round gives a value as the report writes it with that many decimals (2.1), so a rank, a threshold or a ratio
    // taken from it agrees with the line. The invariant fixed-point text rounds the double's exact value: 1.0005 is
    // just under the midpoint, and an exact midpoint (0.125, 60.5, 412.25) goes to the even digit.
    [TestCase(1.8404, 3, 1.84)]
    [TestCase(0.0004, 3, 0)]
    [TestCase(0.96, 1, 1.0)]
    [TestCase(1.0005, 3, 1.0)]
    [TestCase(0.125, 2, 0.12)]
    [TestCase(0.375, 2, 0.38)]
    [TestCase(60.5, 0, 60)]
    [TestCase(61.5, 0, 62)]
    [TestCase(412.25, 1, 412.2)]
    public void RoundGivesTheValueAsTheReportWritesIt(double value, int decimals, double expected) =>
        Assert.That(BotCpuReport.Round(value, decimals), Is.EqualTo(expected));
}
```

Then pin the new class in the workflow check: its copy of CI's filter string, and its list of names (each must be a
file holding `public sealed class UT_X`). CI's own filter follows in Step 3.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_workflows.py <<'EDIT'
<<<<<<< OLD
                     '|FullyQualifiedName~UT_BotCpuMeter"')
=======
                     '|FullyQualifiedName~UT_BotCpuMeter|FullyQualifiedName~UT_BotCpuReport"')
>>>>>>> NEW
<<<<<<< OLD
                                 "UT_BotCpuMeter"])
=======
                                 "UT_BotCpuMeter", "UT_BotCpuReport"])
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_workflows.py (2 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 9 errors, all `CS0246` in `UT_BotCpuReport.cs`: none of the report's
types exists yet. Each error line starts with the file's absolute path and ends with the project's. In the order
seen:

```
source/server/Tests/UnitTests/UT_BotCpuReport.cs(45,20): error CS0246: The type or namespace name 'BotCpuSamples' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(50,9): error CS0246: The type or namespace name 'BotCpuSamples' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(50,55): error CS0246: The type or namespace name 'BotCpuBot' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(49,20): error CS0246: The type or namespace name 'BotCpuWindow' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(55,20): error CS0246: The type or namespace name 'BotCpuBot' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(59,38): error CS0246: The type or namespace name 'BotCpuSpread' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(551,21): error CS0246: The type or namespace name 'BotCpuWindow' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(551,41): error CS0246: The type or namespace name 'BotCpuWindow' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(777,20): error CS0246: The type or namespace name 'BotCpuBot' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The summary (the warning total varies between builds; 822 was seen):

```
Build FAILED.
    822 Warning(s)
    9 Error(s)
```

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: the progress line `.................F`, then `FAIL: test_the_fork_server_unit_tests_run_with_the_pinned_filter`
with an `AssertionError` that the new filter string (ending
`|FullyQualifiedName~UT_BotCpuMeter|FullyQualifiedName~UT_BotCpuReport"`) is not found in CI's step `name: Server unit
tests for the fork's server changes`, then `Ran 18 tests` and `FAILED (failures=1)`.
`test_every_name_in_the_filter_is_a_test_class` passes already, since `UT_BotCpuReport.cs` exists.

- [ ] **Step 3: Implement.** Create `source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs` with exactly the
code below (LF, no BOM). What it decides, in the order of the file:
  - `BotCpuSamples` is one window's registry samples. `NextWindow` keeps the last sample, so a window with none uses
    it (2.2); with no sample ever, GameBot `n` is 0 and the per-bot fields are `-` through the bot-seconds rule.
  - `BotCpuKindWindow` divides by the bot-seconds, `n` × the window's exact length. 3.8 says `n × win`, but 2.1
    (`msPerBotSec = msSec / n`) and 2.2 (every per-second figure divides by the exact length) need the exact length,
    so that is what it uses. The line's stage values are per bot, and null with no bot-seconds; unattributed's are per
    second, the same list as its CSV columns. The CSV's columns come from the raw stage ticks, not from the rounded
    per-bot values. A stage that rounds to 0.000 stays in the list; the text leaves it out.
  - A kind has a line when any of its ticks, calls or bytes is above 0, or its `n` is. `BotCpuWindow.Kinds` holds only
    those, and `Kind(kind)` gives null for the others. `ChargedPct` is all kinds' ms per s ÷ 10, since 1,000 ms a
    second is 100% of one core.
  - `BotCpuSince.Add(null)` returns the same totals, so the script can write `since = since.Add(window)` and a final
    window too short to write drops out. Its length is the windows' exact seconds, not their wins.
  - `Difference` is built only from `BotCpuTotals`' public members, so `BotCpuMeter.cs` is not edited. A slot that
    appeared between the snapshots counts from zero, since the earlier totals never held it.
  - `TakeCounterWindow` is the per-bot half of the difference. It reads the loop threads' `Ticks` and `Calls` with
    `Volatile.Read` and writes only the report's `LastTicks` and `LastCalls`. A bot absent from a listing leaves no
    state behind: its last values live on its own counter (Review Focus 3).
  - `Round` is the value's invariant `"F"` text read back, so a rank, a threshold or a ratio taken from it agrees with
    what the lines show. The spec gives no midpoint rule; this one sends an exact midpoint to the even digit, so a
    60.5 s window is `win=60`.
  - `Spread` takes the nearest ranks ⌈0.50·m⌉ and ⌈0.95·m⌉ in integers: `(50 * m + 99) / 100` is ⌈50·m / 100⌉.
  - `TopName` gives `-` for a null or empty name, the spec's missing marker, so a `BOT_CPU_TOP` entry is never empty.
  - `Window` returns null when `!(seconds >= 5)`, which also catches NaN. Presence `n` is 0 when game time did not
    move, which keeps NaN out. Only the listed bots of a bot kind with `Calls > 0` go into the spread and the top:
    "charged at least once" (3.10), so a call that took under one `Stopwatch` tick counts. The top is ranked by the
    per-bot ms rounded to 3 decimals, as the line shows it, then by name with `StringComparer.Ordinal`: two bots that
    both show 6.020 are in name order, and the order never depends on the server's culture.
  - A compare pair needs both kinds' `n` of at least 1.0 as the line shows it (1 decimal, so 0.96 counts). Each ratio
    divides the fields rounded to their lines' decimals (`msPerBotSec` 3, `allocKBPerBotSec` 1, `callsPerBotSec` 1,
    `usPerCall` 2), and a gamebot value that rounds to 0 gives null. That reproduces 2.1's sample compare line exactly.
    The ratio itself is left unrounded for the text.

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's report (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). Once a minute
// the report side takes the difference of two snapshots of the per-thread slots (3.7) and of each bot's counter (3.10),
// and this file turns it, with the window's exact length, the game time and the registry samples, into the report's
// numbers: each kind's figures with the - rules of 2.1, n and bot-seconds (3.8), the spread and the top 5 of each bot
// kind (3.10), the compare pairs (2.1) and the totals since on (2.4). Plain values in and out, no server state, so it
// is unit-tested without a server (UT_BotCpuReport). BotCpuMeter is the hot path that fills the slots and counters;
// BotCpuScript reads the server's state and writes the lines.

/// <summary>
/// The registry samples of one window (3.8): <c>AutonomousBotRegistry.PopulationForBrainTick</c>, read once a second.
/// GameBot <c>n</c> is their average, or the last sample when the window has none (2.2).
/// </summary>
/// <param name="Sum">The samples' sum in this window.</param>
/// <param name="Count">The samples in this window.</param>
/// <param name="Last">The latest sample of the run, from this window or an earlier one; null before the first.</param>
public readonly record struct BotCpuSamples(long Sum, int Count, int? Last)
{
    /// <summary>These samples and one more.</summary>
    public BotCpuSamples Add(int sample) => new(Sum + sample, Count + 1, sample);

    /// <summary>The next window's samples: none yet, with this window's last sample kept.</summary>
    public BotCpuSamples NextWindow() => new(0, 0, Last);

    /// <summary>GameBot <c>n</c>: the samples' average, else the last sample, else 0.</summary>
    public double Average => Count > 0 ? (double)Sum / Count : Last ?? 0;
}

/// <summary>
/// One bot of a bot kind as the report lists it from the per-bot table (3.10): what the report shows of it and its
/// counter's ticks and calls in the window (<see cref="BotCpuReport.TakeCounterWindow"/>).
/// </summary>
/// <param name="Kind">Its counter's kind: gamebot, companion, playerbot or piloted.</param>
/// <param name="Name">Its name.</param>
/// <param name="Level">Its level.</param>
/// <param name="ClassName">Its class (<c>GameBot.ClassName</c>).</param>
/// <param name="Region">Its region.</param>
/// <param name="Goal">A GameBot's <c>PersistentRecord.ObjectiveKind</c>; null without a record or for another
/// kind.</param>
/// <param name="Activity">A GameBot's <c>PersistentRecord.Activity</c>; null without a record or for another
/// kind.</param>
/// <param name="Ticks">The <see cref="System.Diagnostics.Stopwatch"/> ticks charged to it in the window.</param>
/// <param name="Calls">The callbacks charged to it in the window.</param>
public sealed record BotCpuBot(BotCpuKind Kind, string Name, int Level, string ClassName, int Region, string Goal,
    string Activity, long Ticks, long Calls);

/// <summary>The spread of a bot kind's per-bot ms per s (3.10), nearest rank.</summary>
public readonly record struct BotCpuSpread(double P50, double P95, double Max);

/// <summary>One of a bot kind's 5 costliest bots in a window (3.10): its rank from 1, the bot, its ms per s and its
/// callbacks per s.</summary>
public sealed record BotCpuTopEntry(int Rank, BotCpuBot Bot, double MsSec, double CallsSec);

/// <summary>
/// A compare pair (2.1): <paramref name="Kind"/> (playerbot, piloted or player) against gamebot, both with <c>n</c> of
/// at least 1.0. Each ratio is null (<c>-</c>) when either value is <c>-</c> or the gamebot value is 0.
/// </summary>
/// <param name="Kind">The pair's first kind.</param>
/// <param name="N">Its <c>n</c>.</param>
/// <param name="GamebotN">Gamebot's <c>n</c>.</param>
/// <param name="Cpu">The ratio of the <c>msPerBotSec</c>s.</param>
/// <param name="Alloc">The ratio of the <c>allocKBPerBotSec</c>s.</param>
/// <param name="Calls">The ratio of the <c>callsPerBotSec</c>s: items per bot, not thinks.</param>
/// <param name="UsPerCall">The ratio of the <c>usPerCall</c>s.</param>
public sealed record BotCpuCompare(BotCpuKind Kind, double N, double GamebotN, double? Cpu, double? Alloc,
    double? Calls, double? UsPerCall);

/// <summary>
/// One kind's figures in a window (2.1, 2.3): what its <c>BOT_CPU</c> line and CSV row show. A per-bot figure is null
/// (<c>-</c>) when the kind's bot-seconds are 0, and <see cref="UsPerCall"/> when its calls are 0: never NaN, Infinity
/// or a 0 that stands for nothing to divide by.
/// </summary>
public sealed class BotCpuKindWindow
{
    internal BotCpuKindWindow(BotCpuKind kind, double? n, BotCpuTotals counts, double seconds, long frequency,
        BotCpuSpread? spread)
    {
        Kind = kind;
        N = n;
        BotSeconds = n > 0 ? n.Value * seconds : 0;

        double[] stageMsSec = new double[BotCpuTotals.StageCount];
        double[] perBot = BotSeconds > 0 ? new double[BotCpuTotals.StageCount] : null;
        for (int stage = 0; stage < BotCpuTotals.StageCount; stage++)
        {
            long ticks = counts.Ticks(kind, (BotCpuStage)stage);
            Ticks += ticks;
            Calls += counts.Calls(kind, (BotCpuStage)stage);
            Bytes += counts.Bytes(kind, (BotCpuStage)stage);
            double ms = ticks * 1000.0 / frequency;
            stageMsSec[stage] = ms / seconds;
            if (perBot != null)
                perBot[stage] = ms / BotSeconds;
        }

        ChargedMs = Ticks * 1000.0 / frequency;
        MsSec = ChargedMs / seconds;
        CallsSec = Calls / seconds;
        AllocKBSec = Bytes / 1024.0 / seconds;
        UsPerCall = Calls > 0 ? Ticks * 1_000_000.0 / frequency / Calls : null;
        if (BotSeconds > 0)
        {
            MsPerBotSec = ChargedMs / BotSeconds;
            CallsPerBotSec = Calls / BotSeconds;
            AllocKBPerBotSec = Bytes / 1024.0 / BotSeconds;
        }

        Spread = spread;
        StageMsSec = stageMsSec;
        // Unattributed's line has its stage values per second, the same as its CSV columns (2.1).
        StageValues = kind == BotCpuKind.Unattributed ? stageMsSec : perBot;
    }

    public BotCpuKind Kind { get; }

    /// <summary>The average count of the kind over the window (3.8); null (<c>-</c>) for unattributed.</summary>
    public double? N { get; }

    /// <summary><see cref="N"/> × the window's exact length (2.2), not the rounded <c>win</c>: every per-bot figure
    /// divides by it (3.8).</summary>
    public double BotSeconds { get; }

    /// <summary>The raw <see cref="System.Diagnostics.Stopwatch"/> ticks charged to the kind, every stage.</summary>
    public long Ticks { get; }

    /// <summary>The callbacks charged to the kind.</summary>
    public long Calls { get; }

    /// <summary>The bytes allocated inside the kind's callbacks.</summary>
    public long Bytes { get; }

    /// <summary>All the ms charged to the kind in the window, for the totals since on (2.4).</summary>
    public double ChargedMs { get; }

    /// <summary><c>msSec</c>: the ms charged per second of window.</summary>
    public double MsSec { get; }

    /// <summary><c>callsSec</c>, unattributed's line and every CSV row: callbacks per second of window.</summary>
    public double CallsSec { get; }

    /// <summary><c>allocKBSec</c>, unattributed's line and every CSV row: KB allocated per second of window.</summary>
    public double AllocKBSec { get; }

    /// <summary><c>msPerBotSec</c>, <c>msSec / n</c>: the number the owner asked for.</summary>
    public double? MsPerBotSec { get; }

    /// <summary><c>callsPerBotSec</c>: the callbacks per bot-second, counting idle visits.</summary>
    public double? CallsPerBotSec { get; }

    /// <summary><c>usPerCall</c>: the charged µs per callback.</summary>
    public double? UsPerCall { get; }

    /// <summary><c>allocKBPerBotSec</c>: the KB (1,024 bytes) allocated per bot-second.</summary>
    public double? AllocKBPerBotSec { get; }

    /// <summary>The spread of the kind's bots charged in the window (3.10); null with none, and for a kind that is no
    /// bot kind.</summary>
    public BotCpuSpread? Spread { get; }

    /// <summary><c>p50</c> of the line: null with no bot-seconds (<c>-</c>, 2.1) or no spread (3.10).</summary>
    public double? P50 => BotSeconds > 0 ? Spread?.P50 : null;

    /// <summary><c>p95</c> of the line: null with no bot-seconds (<c>-</c>, 2.1) or no spread (3.10).</summary>
    public double? P95 => BotSeconds > 0 ? Spread?.P95 : null;

    /// <summary><c>max</c> of the line: null with no bot-seconds (<c>-</c>, 2.1) or no spread (3.10).</summary>
    public double? Max => BotSeconds > 0 ? Spread?.Max : null;

    /// <summary>
    /// The line's stage values, by <see cref="BotCpuStage"/>: ms per bot per s, adding up to <see cref="MsPerBotSec"/>,
    /// or ms per s for unattributed, adding up to <see cref="MsSec"/>. Null with no bot-seconds: the line then has no
    /// part after <c>|</c> (2.1).
    /// </summary>
    public IReadOnlyList<double> StageValues { get; }

    /// <summary>The CSV's stage columns, by <see cref="BotCpuStage"/>: the whole kind's raw stage ticks ÷ the exact
    /// window length, ms per s, for every kind (2.3).</summary>
    public IReadOnlyList<double> StageMsSec { get; }

    /// <summary>A kind with nothing charged and <c>n</c> 0 writes no line or row (2.1).</summary>
    internal bool HasLine => Ticks > 0 || Calls > 0 || Bytes > 0 || N > 0;
}

/// <summary>
/// One window's numbers (2.1, 2.2): its length, the game ticks, the charged share of the process line, the kinds that
/// write a line, the top 5 of each bot kind and the compare pairs. The process values read from the platform (3.9) are
/// not here.
/// </summary>
public sealed class BotCpuWindow
{
    private readonly BotCpuKindWindow[] _kinds;
    private readonly IReadOnlyList<BotCpuTopEntry>[] _top;

    internal BotCpuWindow(double seconds, double gameTicks, BotCpuKindWindow[] kinds,
        IReadOnlyList<BotCpuTopEntry>[] top, IReadOnlyList<BotCpuCompare> compares)
    {
        Seconds = seconds;
        Win = Math.Max(1, (int)BotCpuReport.Round(seconds, 0));
        GameTicks = gameTicks;
        Tps = gameTicks / seconds;
        _kinds = kinds;
        _top = top;
        Kinds = kinds.Where(kind => kind.HasLine).ToArray();
        double allMsSec = kinds.Sum(kind => kind.MsSec);
        ChargedPct = allMsSec / 10; // 1,000 ms a second is 100% of one core
        CallsSec = kinds.Sum(kind => kind.CallsSec);
        Compares = compares;
    }

    /// <summary>The window's exact <see cref="System.Diagnostics.Stopwatch"/> length, which every per-second figure
    /// divides by.</summary>
    public double Seconds { get; }

    /// <summary><c>win</c>: <see cref="Seconds"/> rounded, at least 1.</summary>
    public int Win { get; }

    /// <summary>The game-loop ticks in the window: the change of game time ÷ the tick's duration (3.8).</summary>
    public double GameTicks { get; }

    /// <summary><c>tps</c>: game-loop ticks per second.</summary>
    public double Tps { get; }

    /// <summary><c>chargedPct</c>: all time charged to all kinds, unattributed included, as % of one core.</summary>
    public double ChargedPct { get; }

    /// <summary><c>callsSec</c> of the process line: all metered callbacks per second.</summary>
    public double CallsSec { get; }

    /// <summary>The kinds that write a line and a CSV row, in kind order (2.5).</summary>
    public IReadOnlyList<BotCpuKindWindow> Kinds { get; }

    /// <summary>The compare pairs written, in the order playerbot, piloted, player (2.1).</summary>
    public IReadOnlyList<BotCpuCompare> Compares { get; }

    /// <summary>The figures of <paramref name="kind"/>, or null when it writes no line.</summary>
    public BotCpuKindWindow Kind(BotCpuKind kind)
    {
        BotCpuKindWindow figures = _kinds[(int)kind];
        return figures.HasLine ? figures : null;
    }

    /// <summary>The top 5 of <paramref name="kind"/>, most costly first; empty when none of its bots was charged, and
    /// for a kind that is no bot kind.</summary>
    public IReadOnlyList<BotCpuTopEntry> Top(BotCpuKind kind) => _top[(int)kind];
}

/// <summary>
/// The totals since on, or since reset (2.4): per kind, the charged ms and the bot-seconds of every window written
/// since then, and those windows' length. Immutable: <see cref="Add"/> gives new totals.
/// </summary>
public sealed class BotCpuSince
{
    private readonly double[] _chargedMs;
    private readonly double[] _botSeconds;

    private BotCpuSince(bool reset, int windows, double seconds, double[] chargedMs, double[] botSeconds)
    {
        Reset = reset;
        Windows = windows;
        Seconds = seconds;
        _chargedMs = chargedMs;
        _botSeconds = botSeconds;
    }

    /// <summary>No window yet: at on, or at <c>/botcpu reset</c> (<paramref name="reset"/> true).</summary>
    public static BotCpuSince Start(bool reset) =>
        new(reset, 0, 0, new double[BotCpuTotals.KindCount], new double[BotCpuTotals.KindCount]);

    /// <summary>True after a reset: the chat line says "Since reset".</summary>
    public bool Reset { get; }

    /// <summary>The windows written; with none the chat line is left out.</summary>
    public int Windows { get; }

    /// <summary>Those windows' exact length.</summary>
    public double Seconds { get; }

    /// <summary>Those windows' length in minutes, the chat line's <c>N</c>.</summary>
    public double Minutes => Seconds / 60;

    public double ChargedMs(BotCpuKind kind) => _chargedMs[(int)kind];

    public double BotSeconds(BotCpuKind kind) => _botSeconds[(int)kind];

    /// <summary>The kind's charged ms ÷ its bot-seconds over the windows; null when it has no bot-seconds.</summary>
    public double? MsPerBotSec(BotCpuKind kind) =>
        _botSeconds[(int)kind] > 0 ? _chargedMs[(int)kind] / _botSeconds[(int)kind] : null;

    /// <summary>These totals and a written window; a null window (a final window too short to write, 2.2) adds
    /// nothing.</summary>
    public BotCpuSince Add(BotCpuWindow window)
    {
        if (window == null)
            return this;

        double[] chargedMs = (double[])_chargedMs.Clone();
        double[] botSeconds = (double[])_botSeconds.Clone();
        foreach (BotCpuKindWindow kind in window.Kinds)
        {
            chargedMs[(int)kind.Kind] += kind.ChargedMs;
            botSeconds[(int)kind.Kind] += kind.BotSeconds;
        }

        return new BotCpuSince(Reset, Windows + 1, Seconds + window.Seconds, chargedMs, botSeconds);
    }
}

/// <summary>
/// The report's numbers (2.1, 2.2, 2.4, 3.8, 3.10): plain values in and out, no server state.
/// </summary>
public static class BotCpuReport
{
    /// <summary>A final window shorter than this writes no lines or rows, and its counts are dropped (2.2).</summary>
    public const double MinimumWindowSeconds = 5;

    /// <summary>The bots in a kind's top (3.10).</summary>
    public const int TopCount = 5;

    /// <summary>A compare pair is written when both kinds have <c>n</c> of at least this, as the line shows it (2.1).
    /// </summary>
    public const double CompareMinimumN = 1.0;

    // The compare pairs, in their order (2.1); the other kind of each is gamebot.
    private static readonly BotCpuKind[] ComparedKinds = [BotCpuKind.Playerbot, BotCpuKind.Piloted, BotCpuKind.Player];

    /// <summary>
    /// A window's counts (3.7, step 3): <paramref name="later"/> less <paramref name="earlier"/>, value by value, a
    /// negative difference counted as 0. A slot that appeared between the two counts from zero, as the earlier totals
    /// never held it.
    /// </summary>
    public static BotCpuTotals Difference(BotCpuTotals later, BotCpuTotals earlier)
    {
        ArgumentNullException.ThrowIfNull(later);
        ArgumentNullException.ThrowIfNull(earlier);
        BotCpuTotals window = new();
        for (int k = 0; k < BotCpuTotals.KindCount; k++)
        {
            BotCpuKind kind = (BotCpuKind)k;
            for (int s = 0; s < BotCpuTotals.StageCount; s++)
            {
                BotCpuStage stage = (BotCpuStage)s;
                window.Add(kind, stage, Positive(later.Ticks(kind, stage) - earlier.Ticks(kind, stage)),
                    Positive(later.Calls(kind, stage) - earlier.Calls(kind, stage)),
                    Positive(later.Bytes(kind, stage) - earlier.Bytes(kind, stage)));
            }

            window.AddPresence(kind, Positive(later.Presence(kind) - earlier.Presence(kind)));
        }

        return window;
    }

    private static long Positive(long value) => Math.Max(0, value);

    /// <summary>
    /// A bot's ticks and calls since the report last read its counter (3.10), a negative difference counted as 0; the
    /// values read become the counter's <see cref="BotCpuCounter.LastTicks"/> and
    /// <see cref="BotCpuCounter.LastCalls"/>, which only the report writes. The loop threads'
    /// <see cref="BotCpuCounter.Ticks"/> and <see cref="BotCpuCounter.Calls"/> are only read. A bot gone from the table
    /// is simply not listed: its counter goes with it and leaves nothing behind.
    /// </summary>
    public static (long Ticks, long Calls) TakeCounterWindow(BotCpuCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        long ticks = Volatile.Read(ref counter.Ticks);
        long calls = Volatile.Read(ref counter.Calls);
        (long Ticks, long Calls) window = (Positive(ticks - counter.LastTicks), Positive(calls - counter.LastCalls));
        counter.LastTicks = ticks;
        counter.LastCalls = calls;
        return window;
    }

    /// <summary>
    /// <paramref name="value"/> as the report writes it with <paramref name="decimals"/> decimals (2.1): its
    /// invariant-culture fixed-point text, read back. So a rank, a threshold or a ratio taken from it agrees with what
    /// the lines show. That text rounds the double's exact value, and an exact midpoint to the even digit (0.125 is
    /// 0.12).
    /// </summary>
    public static double Round(double value, int decimals) =>
        double.Parse(value.ToString("F" + decimals, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// The spread of per-bot values (3.10), nearest rank: sorted ascending, <c>p50</c> is the value at rank ⌈0.50·m⌉,
    /// <c>p95</c> at ⌈0.95·m⌉ and <c>max</c> the largest. Null with no value.
    /// </summary>
    public static BotCpuSpread? Spread(IEnumerable<double> values)
    {
        double[] sorted = values?.ToArray() ?? [];
        if (sorted.Length == 0)
            return null;

        Array.Sort(sorted);
        int m = sorted.Length;
        // Ranks from 1, in integers: ⌈50·m / 100⌉ and ⌈95·m / 100⌉.
        return new BotCpuSpread(sorted[(50 * m + 99) / 100 - 1], sorted[(95 * m + 99) / 100 - 1], sorted[m - 1]);
    }

    /// <summary>A bot's name as <c>BOT_CPU_TOP</c> writes it (2.1): a space or <c>:</c> becomes <c>_</c>; none is
    /// <c>-</c>.</summary>
    public static string TopName(string name) =>
        string.IsNullOrEmpty(name) ? "-" : name.Replace(' ', '_').Replace(':', '_');

    /// <summary>
    /// One window's numbers (2.1, 2.2, 3.8, 3.10), or null for a window shorter than
    /// <see cref="MinimumWindowSeconds"/>, which writes nothing.
    /// </summary>
    /// <param name="counts">The window's counts, from <see cref="Difference"/>.</param>
    /// <param name="seconds">The window's exact <see cref="System.Diagnostics.Stopwatch"/> length.</param>
    /// <param name="frequency"><see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <param name="gameTimeChange">The change of <c>GameLoop.GameLoopTime</c> over the window, ms.</param>
    /// <param name="tickDuration"><c>GameLoop.TickDuration</c>, ms.</param>
    /// <param name="gamebots">The window's registry samples, for GameBot <c>n</c>.</param>
    /// <param name="bots">The bots listed from the per-bot table, with their window's ticks and calls.</param>
    public static BotCpuWindow Window(BotCpuTotals counts, double seconds, long frequency, long gameTimeChange,
        double tickDuration, BotCpuSamples gamebots, IEnumerable<BotCpuBot> bots)
    {
        if (!(seconds >= MinimumWindowSeconds))
            return null;

        ArgumentNullException.ThrowIfNull(counts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        double gameTicks = gameTimeChange > 0 && tickDuration > 0 ? gameTimeChange / tickDuration : 0;
        // The bots charged at least once in the window (3.10); a bot present but never charged is left out.
        BotCpuBot[] charged = bots?.Where(bot => bot != null && bot.Calls > 0 && BotCpuMeter.IsBotKind(bot.Kind))
            .ToArray() ?? [];

        BotCpuKindWindow[] kinds = new BotCpuKindWindow[BotCpuTotals.KindCount];
        IReadOnlyList<BotCpuTopEntry>[] top = new IReadOnlyList<BotCpuTopEntry>[BotCpuTotals.KindCount];
        for (int k = 0; k < BotCpuTotals.KindCount; k++)
        {
            BotCpuKind kind = (BotCpuKind)k;
            double? n = kind switch
            {
                BotCpuKind.Unattributed => null,
                BotCpuKind.Gamebot => gamebots.Average,
                // Presence: one per client or live brain per tick, over the ticks (3.8).
                _ => gameTicks > 0 ? counts.Presence(kind) / gameTicks : 0,
            };

            // Per bot: its ticks as ms per second of the window. The top is ranked by that value as the line shows it,
            // 3 decimals, so bots that show the same value are in name order (2.1).
            BotCpuTopEntry[] perBot = charged.Where(bot => bot.Kind == kind)
                .Select(bot => new BotCpuTopEntry(0, bot, bot.Ticks * 1000.0 / frequency / seconds,
                    bot.Calls / seconds))
                .ToArray();
            top[k] = perBot.OrderByDescending(entry => Round(entry.MsSec, 3))
                .ThenBy(entry => entry.Bot.Name, StringComparer.Ordinal)
                .Take(TopCount)
                .Select((entry, index) => entry with { Rank = index + 1 })
                .ToArray();
            kinds[k] = new BotCpuKindWindow(kind, n, counts, seconds, frequency,
                Spread(perBot.Select(entry => entry.MsSec)));
        }

        List<BotCpuCompare> compares = [];
        BotCpuKindWindow gamebot = kinds[(int)BotCpuKind.Gamebot];
        foreach (BotCpuKind kind in ComparedKinds)
        {
            BotCpuKindWindow other = kinds[(int)kind];
            if (!(Round(other.N ?? 0, 1) >= CompareMinimumN && Round(gamebot.N ?? 0, 1) >= CompareMinimumN))
                continue;

            // The fields with the decimals their lines show (2.1).
            compares.Add(new BotCpuCompare(kind, other.N.Value, gamebot.N.Value,
                Ratio(other.MsPerBotSec, gamebot.MsPerBotSec, 3),
                Ratio(other.AllocKBPerBotSec, gamebot.AllocKBPerBotSec, 1),
                Ratio(other.CallsPerBotSec, gamebot.CallsPerBotSec, 1),
                Ratio(other.UsPerCall, gamebot.UsPerCall, 2)));
        }

        return new BotCpuWindow(seconds, gameTicks, kinds, top, compares);
    }

    // A compare ratio of two fields as their lines show them: - when either is - or the gamebot value is 0 (2.1).
    private static double? Ratio(double? value, double? gamebot, int decimals)
    {
        if (value == null || gamebot == null)
            return null;

        double divisor = Round(gamebot.Value, decimals);
        return divisor == 0 ? null : Round(value.Value, decimals) / divisor;
    }
}
```

CI runs the new class (the filter's `~UT_BotCpuMeter` does not match `UT_BotCpuReport`, so it needs its own name):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py .github/workflows/server-image.yml <<'EDIT'
<<<<<<< OLD
|FullyQualifiedName~UT_BotCpuMeter"
=======
|FullyQualifiedName~UT_BotCpuMeter|FullyQualifiedName~UT_BotCpuReport"
>>>>>>> NEW
EDIT
```

Expected: `edited .github/workflows/server-image.yml (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (636 was
seen).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuReport."`
Expected: `Passed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50` (33 tests, 50 cases; exit code 0).

The same class under the `de-DE` culture, whose decimal separator is a comma: `Round` and the name order do not
change, and only the summary's language does.

Run: `LC_ALL=de_DE.UTF-8 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuReport."`
Expected: `Bestanden!   : Fehler:     0, erfolgreich:    50, übersprungen:     0, gesamt:    50`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

- [ ] **Step 5: Check the files' form.** The two new files are LF, with no BOM, no tab and no line over 120
characters; the two edited files stay LF (the workflow's longest line is the filter line, 884 characters before this
task):

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs source/server/Tests/UnitTests/UT_BotCpuReport.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs False 0 0 True 120
source/server/Tests/UnitTests/UT_BotCpuReport.cs False 0 0 True 120
.github/workflows/server-image.yml False 0 0 True 919
deploy/tests/test_workflows.py False 0 0 True 182
```

- [ ] **Step 6: Run the wider checks.**

CI's fork filter, now with `UT_BotCpuReport`:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   818, Skipped:     0, Total:   818` (768 after Task 4, and this task's
50; nothing runs twice).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3606, Skipped:     0, Total:  3606` (3556 after Task 4, and 50; about 30
seconds of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 10 tests` and `OK` (unchanged: the new files' endings check is Task 11's).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_endings.py`
Expected: `Ran 11 tests` and `OK`.

The whole deploy suite with the world tests (about three minutes; run it after the unit tests, not at the same time):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 754 tests` and `OK (skipped=10)` (as after Task 4: this task adds no deploy test).

Run: `git status --short`
Expected: only these four lines (`serverconfig.xml` is ignored, so it does not show):

```
 M .github/workflows/server-image.yml
 M deploy/tests/test_workflows.py
?? source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs
?? source/server/Tests/UnitTests/UT_BotCpuReport.cs
```

Run: `git diff --numstat`
Expected (the new files are not tracked yet):

```
1	1	.github/workflows/server-image.yml
2	2	deploy/tests/test_workflows.py
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs source/server/Tests/UnitTests/UT_BotCpuReport.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -F - <<'EOF'
feat(cpu-meter): the report's numbers

BotCpuReport.cs holds the report's numbers, plain values in and out with no server state (spec 5.1).
Difference takes a window's counts from two snapshots of the slots, a negative difference counted as 0,
and TakeCounterWindow a bot's ticks and calls since the report last read its counter, so a bot gone from
the table leaves nothing behind (3.7, 3.10). Window turns them, with the window's exact length, the
game-time change and the registry samples, into each kind's figures: n from the registry for gamebot and
from presence over the game ticks for the others, bot-seconds, msSec and the per-bot fields, null for -
when there is nothing to divide by, the line's per-bot stage values and the CSV's whole-kind stage
columns (2.1, 2.3, 3.8). A window under 5 s writes nothing, and one with no registry sample uses the last
(2.2). Each bot kind gets the nearest-rank spread and the top 5 of its bots charged in the window, and a
compare pair is written when both kinds have n of at least 1.0 (2.1, 3.10). BotCpuSince keeps the totals
since on or reset (2.4). Ranks, the n threshold and the compare ratios take the values as the lines
write them, so they agree with what the owner reads. UT_BotCpuReport pins all of this (6.1) and joins
CI's fork filter and test_workflows.py (6.4).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `4 files changed, 1445 insertions(+), 3 deletions(-)`, `create mode 100644
source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs` and `create mode 100644
source/server/Tests/UnitTests/UT_BotCpuReport.cs`.

Review: two fix rounds shaped this section. Round 1 (Important: nothing pinned 3.10's "charged at least once") gave
`ABotNotChargedInTheWindowIsLeftOutOfTheSpreadAndTheTop`, the idle Halvard and `BotSeconds`' summary; round 2
(Important: no test wrote the piloted pair) gave `AllThreePairsAreWrittenInTheirOrder`, the charged player in
`FewerThanFiveBotsGiveFewerEntries`, `TwoWindows`' 61.4 s first window and the neutral `P50`/`P95`/`Max` summaries.
The approving review left five Minor gaps open (p95's ceiling against rounding, as values 1 to 12 would show; the
gamebot side of the compare threshold; per-bot values at frequency 1e7; the ordinal tie order; `TopName`'s `-` for no
name, this task's own rule), and two spec issues stand for the owner: 3.8's "`n × win`" should read "n × the window's
exact length (2.2)", and 2.1's compare is read "as the line shows it" for the `n` threshold, the ratios and "the
gamebot value is 0".

### Task 6: The report's text

**Files:**
- Modify: `source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs:4-5` (the usings gain `System.Text`), `:13-15`
  (the header comment), `:316-319` (`BotCpuProcess` and `BotCpuOffSource` before the class, the class summary,
  `WindowSeconds`), `:489-491` (the text, at the end of the class)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuReport.cs:3-4` (the usings gain `System.Globalization`), `:14` (the
  header comment), `:950-951` (three new sections at the end of the class: 22 tests, 27 cases)
- Test: `source/server/Tests/UnitTests/UT_BotCpuReport.cs`

`UT_BotCpuReport` is in CI's filter and `deploy/tests/test_workflows.py` since Task 5, so neither changes.

**Spec:** 2.1 (every line, its exact text: the kind lines, `BOT_CPU_TOP`, `BOT_CPU_PROCESS`, `BOT_CPU_COMPARE`,
`BOT_CPU_STATE`, `BOT_CPU_ERROR`; the invariant culture; `-`, never `NaN` or `Infinity`), 2.2 (60 s windows; a final
window under 5 s in the reply to `/botcpu off`), 2.3 (the header and a row; the file itself is Task 8's), 2.4 (the
chat text of `/botcpu` and every reply of its table; the command is Task 10's), 3.9 (a process value the platform
can't give is `-`), 6.1 `UT_BotCpuReport` (Lines, Chat, CSV).

**Interfaces:**
- Consumes, in `namespace DOL.GS.HearthDAoC`:
  - Task 5's `scripts/hearthdaoc/BotCpuReport.cs`: `BotCpuWindow` (:196-252: `int Win`, `double Tps`, `ChargedPct`,
    `CallsSec`, `IReadOnlyList<BotCpuKindWindow> Kinds` (the kinds with a line, in kind order),
    `IReadOnlyList<BotCpuCompare> Compares`, `BotCpuKindWindow Kind(BotCpuKind)`, `IReadOnlyList<BotCpuTopEntry>
    Top(BotCpuKind)`); `BotCpuKindWindow` (:80-189: `Kind`, `double? N`, `double MsSec`, `CallsSec`, `AllocKBSec`,
    `double? MsPerBotSec`, `CallsPerBotSec`, `UsPerCall`, `AllocKBPerBotSec`, `P50`, `P95`, `Max`,
    `IReadOnlyList<double> StageValues` (null means no part after `|`), `IReadOnlyList<double> StageMsSec`);
    `BotCpuBot(BotCpuKind Kind, string Name, int Level, string ClassName, int Region, string Goal, string Activity,
    long Ticks, long Calls)` (:51-52); `BotCpuTopEntry(int Rank, BotCpuBot Bot, double MsSec, double CallsSec)` (:59);
    `BotCpuCompare(BotCpuKind Kind, double N, double GamebotN, double? Cpu, double? Alloc, double? Calls, double?
    UsPerCall)` (:72-73); `BotCpuSince` (:258-313: `Start(bool reset)`, `Reset`, `Windows`, `Minutes`, `double?
    MsPerBotSec(BotCpuKind)`, `Add(BotCpuWindow)`); `BotCpuReport.MinimumWindowSeconds` (:321, 5), `Round(double
    value, int decimals)` (:386-387), `TopName(string name)` (:407-408) and `Window(...)` (:421-422).
  - Tasks 1-3's `scripts/hearthdaoc/BotCpuMeter.cs`: `BotCpuKind` (:29) and `BotCpuStage` (:55), whose member names
    are the text names; `BotCpuMeter.IsBotKind(BotCpuKind)` (:587-588); `BotCpuTotals.KindCount` (:103), `StageCount`
    (:105), `Add(BotCpuKind, BotCpuStage, long ticks, long calls, long bytes)` (:135) and `AddPresence(BotCpuKind,
    long)` (:144).
  - Task 5's test helpers in `UT_BotCpuReport.cs`: `Ticks(double ms, long frequency = Linux)` (:38), `Charge(counts,
    kind, stage, double ms, long calls, long bytes = 0, long frequency = Linux)` (:41-42), `Samples(params int[])`
    (:45-46), `Close(counts, double seconds = 60, long frequency = Linux, BotCpuSamples gamebots = default,
    IEnumerable<BotCpuBot> bots = null, long? gameTimeChange = null)` (:49-52), `WholeWindow` (:25, 1,800 ticks),
    `Tolerance` (:27) and the kind constants (:29-35).
- Produces, all in `scripts/hearthdaoc/BotCpuReport.cs` (namespace `DOL.GS.HearthDAoC`):
  - `public sealed record BotCpuProcess(int Cores, double? ProcessCpuPct, double? LoopThreadsCpuPct, double?
    CampThreadCpuPct, double? LoopWaitMs, double? ThrottledMs, double? GcPauseMs, long? Gen2, double? AllocMBSec,
    double? HeapMB, double? RssMB)` with `double? OffLoopCpuPct` (`Round(Round(ProcessCpuPct, 1) -
    Round(LoopThreadsCpuPct, 1), 1)`, null when either is null). Units: % of one core, ms, MB, MB/s. Null (and NaN or
    an infinity) is `-` on the line and an empty CSV cell.
  - `public enum BotCpuOffSource { Command, Timer, Stop, Error }`, written as its lower-case name.
  - In `public static class BotCpuReport`:
    - `const double WindowSeconds = 60`; `const string StateOnByPropertyLine = "BOT_CPU_STATE on source=property"`;
      `const string CsvHeader` (2.3's header); `const string OffStatusText = "Bot CPU meter: off. /botcpu on
      [minutes] turns it on."`; `const string AlreadyOffText = "Bot CPU meter is already off."`; `const string
      ResetText = "Bot CPU totals reset."`.
    - `static string KindName(BotCpuKind kind)`: the lower-case member name.
    - `static IReadOnlyList<string> Lines(BotCpuWindow window, BotCpuProcess process)`: the kind lines, the
      `BOT_CPU_TOP` lines in kind order, `BOT_CPU_PROCESS`, then the `BOT_CPU_COMPARE` lines. It throws
      `ArgumentNullException` for a null window.
    - `static string KindLine(BotCpuWindow window, BotCpuKindWindow kind)`, `static string TopLine(BotCpuWindow window,
      BotCpuKind kind)` (null when the kind has no top), `static string ProcessLine(BotCpuWindow window, BotCpuProcess
      process)` (a null process gives `-` for every platform field, `cores` too), `static string
      CompareLine(BotCpuWindow window, BotCpuCompare compare)`.
    - `static string StateOnByCommandLine(string by, int? minutes)` (null minutes give `minutes=-`), `static string
      StateOffLine(BotCpuOffSource source, string by = null)` (`by` only for `Command`), `static string
      ErrorLine(string step, Exception exception)` (`"BOT_CPU_ERROR <step>: "` + `exception.ToString()`).
    - `static IReadOnlyList<string> CsvRows(BotCpuWindow window, BotCpuProcess process, DateTime end)`: one row per
      entry of `window.Kinds`; `end` is the window's end in UTC. It throws `ArgumentNullException` for a null window.
      `static string CsvUtc(DateTime end)`: `yyyy-MM-ddTHH:mm:ssZ`, a `Local` time turned into UTC first.
    - `static IReadOnlyList<string> StatusText(DateTime onSince, bool byCommand, DateTime? stopsAt, DateTime now,
      BotCpuWindow last, BotCpuProcess process, BotCpuSince since)`: a null `last` gives the status line and
      `NoWindowYetText`; the times are the server's local time.
    - `static string NoWindowYetText(DateTime onSince)` (the first lines come at `onSince + WindowSeconds`, `HH:mm`);
      `static string OnText()` and `static string OnText(int minutes, DateTime stopsAt)` (on from off);
      `static string AlreadyOnText(DateTime? stopsAt)` (on while on; null is no stop time); `static string
      OffText(double openSeconds)` (off while on; "are being written" when `openSeconds >= MinimumWindowSeconds`, the
      seconds whole and rounded down).
    - `static IReadOnlyList<string> TopText(BotCpuWindow last, BotCpuKind kind)`: the top lines, or `["No <kind> was
      charged in the last minute."]`; it throws `ArgumentNullException` for a null `last`.
  - What later tasks rely on:
    - Task 8 writes `CsvHeader` and `CsvRows(window, process, end)` with the window's end in UTC.
    - Task 9 builds one `BotCpuProcess` per window, with null for any reader that fails (Review Focus 4). It logs
      `Lines(window, process)`, the `BOT_CPU_STATE` lines and `ErrorLine(step, exception)` before
      `StateOffLine(BotCpuOffSource.Error)`. `Window(...)` returns null for a final window under 5 s, and `Lines` and
      `CsvRows` throw on null, so Task 9 checks for null and writes no lines or rows for it.
    - Task 10 picks the reply: `OffStatusText` when off; `StatusText` or `NoWindowYetText` when on; `TopText` after a
      window, `NoWindowYetText` before one; `OnText` or `AlreadyOnText`, with the same stop time it gives the queued
      job; `OffText` or `AlreadyOffText`; `ResetText`. `TopText` answers any kind, so Task 10 refuses a kind that is
      not a bot kind itself (the syntax lines of its `[CmdAttribute]`).

- [ ] **Step 1: Write the failing tests.** Three runs of the edit helper on
`source/server/Tests/UnitTests/UT_BotCpuReport.cs`, one per new section.

First the `using`, the header sentence and the lines section (2.1). The text tests run under `[SetCulture("de-DE")]`,
whose decimal separator is a comma, and each starts with `AssertTheCultureIsGerman`, which fails a run in which that
culture is not really in force. `SpecSample` rebuilds 2.1's sample window from raw counts: `ChargeStages` charges each
stage its line value × the kind's bot-seconds, the calls and bytes go in with 0 ticks, and `Samples(238, 239, 238,
239, 238)` gives `n` 238.4. Gamebot's `Npc` is charged at 0.9197 and monster's stages a little over the values shown,
so that `msSec` is 438.6 and 96.0, as the sample says. `SpecSampleBots` lists 238 GameBots: the sample's top 5 and 233
more, placed so that `p50` (rank 119 of 238) is 1.620 and `p95` (rank 227) is 3.904. The expected strings were
computed apart from the code, with Python's `Decimal` from the raw counts, and no value lies near a rounding midpoint.
They are 2.1's sample word for word but for the stage order: the sample's player line has `ClientBegin` before `Timer`
and its monster line `Movement` before `Effect`, where 2.1's table, 3.4 and 6.1 put the stages in tick order.
`ShortWindow` is 61.4 s (`win=61`): a player bot charged but never present, so `n` 0, and unattributed work.
`NoPlatformValues` is a platform that gives none of the values it reads, as on Windows (3.9).

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
using System.Collections.Generic;
using System.Linq;
=======
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
>>>>>>> NEW
<<<<<<< OLD
// top 5 of each bot kind from the per-bot counters (3.10), the compare pairs (2.1) and the totals since on (2.4).
=======
// top 5 of each bot kind from the per-bot counters (3.10), the compare pairs (2.1) and the totals since on (2.4), then
// writes them: the log lines (2.1), the CSV rows (2.3) and the chat of /botcpu (2.4), in the invariant culture.
>>>>>>> NEW
<<<<<<< OLD
        Assert.That(BotCpuReport.Round(value, decimals), Is.EqualTo(expected));
}
=======
        Assert.That(BotCpuReport.Round(value, decimals), Is.EqualTo(expected));

    // ---- The lines (2.1)

    // The report writes in the invariant culture, a . decimal point whatever the server's culture (2.1). The text tests
    // run under de-DE, whose decimal separator is a comma. This guard fails a run in which that culture is not really
    // in force: with invariant globalization every culture formats as the invariant one, and the tests would prove
    // nothing.
    private static void AssertTheCultureIsGerman() =>
        Assert.That(1.5.ToString(), Is.EqualTo("1,5"), "the text tests must run under de-DE");

    // Charges each stage its value × perSeconds ms: a per-bot value over the kind's bot-seconds, or, for unattributed,
    // a per-second value over the window's length.
    private static void ChargeStages(BotCpuTotals counts, BotCpuKind kind, double perSeconds,
        params (BotCpuStage Stage, double Value)[] stages)
    {
        foreach ((BotCpuStage stage, double value) in stages)
            Charge(counts, kind, stage, value * perSeconds, 0);
    }

    // 2.1's sample window: 238.4 GameBots by the registry, one player, 1,830.2 live monster brains a tick and the
    // unattributed work, in 60 s at 30.0 ticks a second. Each stage is charged at its value on the line; gamebot's Npc
    // is 0.9197 and monster's stages are 0.00011-0.00012 over the values shown, so that msSec is 438.6 and 96.0 as the
    // lines say. The calls and bytes give the per-bot figures of the sample (and its process line's callsSec=212534).
    private static BotCpuWindow SpecSample()
    {
        BotCpuTotals counts = new();
        ChargeStages(counts, Gamebot, 238.4 * 60, (BotCpuStage.Timer, 0.041), (BotCpuStage.Npc, 0.9197),
            (BotCpuStage.Fleet, 0.118), (BotCpuStage.Attack, 0.210), (BotCpuStage.Casting, 0.140),
            (BotCpuStage.Effect, 0.081), (BotCpuStage.EffectListBegin, 0.050), (BotCpuStage.EffectListEnd, 0.040),
            (BotCpuStage.Movement, 0.150), (BotCpuStage.Reaper, 0.020), (BotCpuStage.Zone, 0.010),
            (BotCpuStage.Pets, 0.060));
        counts.Add(Gamebot, BotCpuStage.Npc, 0, 5_897_537, 456_995_635);

        // The player's stages in the order of 2.1's sample; its line puts them in tick order.
        counts.AddPresence(Player, WholeWindow);
        ChargeStages(counts, Player, 60, (BotCpuStage.ClientBegin, 0.310), (BotCpuStage.Timer, 0.022),
            (BotCpuStage.Attack, 0.201), (BotCpuStage.Movement, 0.090), (BotCpuStage.ClientEnd, 1.311),
            (BotCpuStage.Pets, 0.170));
        counts.Add(Player, BotCpuStage.ClientEnd, 0, 23_400, 3_710_976);

        counts.AddPresence(Monster, 3_294_360);
        ChargeStages(counts, Monster, 1_830.2 * 60, (BotCpuStage.Npc, 0.03112), (BotCpuStage.Attack, 0.00811),
            (BotCpuStage.Movement, 0.01111), (BotCpuStage.Effect, 0.00211));
        counts.Add(Monster, BotCpuStage.Npc, 0, 6_698_532, 44_978_995);

        ChargeStages(counts, Unattributed, 60, (BotCpuStage.GameLoop, 0.402), (BotCpuStage.Timer, 1.210),
            (BotCpuStage.Npc, 6.890), (BotCpuStage.Zone, 0.598));
        counts.Add(Unattributed, BotCpuStage.Npc, 0, 132_600, 5_406_720);

        return Close(counts, gamebots: Samples(238, 239, 238, 239, 238), bots: SpecSampleBots());
    }

    // The 238 GameBots of the sample, all charged in the window: its top 5 with their goals, and 233 more placed so
    // that p50 (rank 119 of 238) is 1.620 and p95 (rank 227) is 3.904.
    private static BotCpuBot[] SpecSampleBots()
    {
        List<BotCpuBot> bots =
        [
            new(Gamebot, "Aldric", 34, "Cleric", 163, "Rvr", "Riding stablemaster horse route", Ticks(7.412 * 60),
                52_800),
            new(Gamebot, "Brennos", 50, "Armsman", 1, "Battleground", "Choosing first live goal", Ticks(6.020 * 60),
                45_000),
            new(Gamebot, "Cathal", 42, "Wizard", 1, "GroupPve", "Defeated; reassessing target difficulty",
                Ticks(5.881 * 60), 43_200),
            new(Gamebot, "Dagny", 47, "Paladin", 163, "Rvr", "Recovered to safe capital spawn", Ticks(5.104 * 60),
                36_000),
            new(Gamebot, "Eirik", 20, "Scout", 1, "SoloPve", "Queued after a failed world transfer", Ticks(4.950 * 60),
                30_000),
        ];
        for (int rank = 1; rank <= 233; rank++)
        {
            double msSec = rank switch
            {
                <= 118 => 1 + rank / 1000.0,
                119 => 1.620,
                <= 226 => 2 + (rank - 120) / 100.0,
                227 => 3.904,
                _ => 4 + (rank - 228) / 10.0,
            };
            bots.Add(new BotCpuBot(Gamebot, $"Bot{rank:000}", 30, "Armsman", 1, "SoloPve", "Roaming",
                Ticks(msSec * 60), 1_800));
        }

        return bots.ToArray();
    }

    // The sample's process values (3.9).
    private static readonly BotCpuProcess SpecSampleProcess = new(Cores: 4, ProcessCpuPct: 148.2,
        LoopThreadsCpuPct: 121.0, CampThreadCpuPct: 3.1, LoopWaitMs: 212, ThrottledMs: 0, GcPauseMs: 14, Gen2: 0,
        AllocMBSec: 41.2, HeapMB: 2_210, RssMB: 3_480);

    // A window of 61.4 s (win=61): a player bot charged but never present (its client is not registered in
    // ClientService, 2.5), so n 0, and unattributed work.
    private static BotCpuWindow ShortWindow()
    {
        BotCpuTotals counts = new();
        Charge(counts, Playerbot, BotCpuStage.Timer, 30, 900, 2_048);
        Charge(counts, Unattributed, BotCpuStage.Timer, 72.6, 3_600);
        return Close(counts, seconds: 61.4);
    }

    // A platform that gives none of the values it reads (Windows, or every /proc and cgroup read failing, 3.9).
    private static readonly BotCpuProcess NoPlatformValues = new(4, null, null, null, null, null, null, null, null,
        null, null);

    [Test]
    [SetCulture("de-DE")]
    public void TheSampleWindowsLinesAreTheSpecs()
    {
        AssertTheCultureIsGerman();

        // 2.1's sample word for word, in its order: the kind lines, BOT_CPU_TOP, BOT_CPU_PROCESS, BOT_CPU_COMPARE. Only
        // the stage order differs: the sample's player line has ClientBegin before Timer and its monster line Movement
        // before Effect, where the lines are in tick order (2.1, 3.4).
        Assert.That(BotCpuReport.Lines(SpecSample(), SpecSampleProcess), Is.EqualTo(new[]
        {
            "BOT_CPU win=60 kind=gamebot n=238.4 msSec=438.6 msPerBotSec=1.840 callsPerBotSec=412.3 usPerCall=4.46 " +
            "allocKBPerBotSec=31.2 p50=1.620 p95=3.904 max=7.412 | Timer=0.041 Npc=0.920 Fleet=0.118 Attack=0.210 " +
            "Casting=0.140 Effect=0.081 EffectListBegin=0.050 EffectListEnd=0.040 Movement=0.150 Reaper=0.020 " +
            "Zone=0.010 Pets=0.060",
            "BOT_CPU win=60 kind=player n=1.0 msSec=2.1 msPerBotSec=2.104 callsPerBotSec=390.0 usPerCall=5.39 " +
            "allocKBPerBotSec=60.4 | Timer=0.022 ClientBegin=0.310 Attack=0.201 Movement=0.090 ClientEnd=1.311 " +
            "Pets=0.170",
            "BOT_CPU win=60 kind=monster n=1830.2 msSec=96.0 msPerBotSec=0.052 callsPerBotSec=61.0 usPerCall=0.86 " +
            "allocKBPerBotSec=0.4 | Npc=0.031 Attack=0.008 Effect=0.002 Movement=0.011",
            "BOT_CPU win=60 kind=unattributed n=- msSec=9.1 callsSec=2210.0 usPerCall=4.12 allocKBSec=88.0 | " +
            "GameLoop=0.402 Timer=1.210 Npc=6.890 Zone=0.598",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:7.412:Rvr 2=Brennos:6.020:Battleground 3=Cathal:5.881:GroupPve " +
            "4=Dagny:5.104:Rvr 5=Eirik:4.950:SoloPve",
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 " +
            "campThreadCpuPct=3.1 chargedPct=54.6 callsSec=212534 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480",
            "BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/238.4 cpu=1.14x alloc=1.94x calls=0.95x usPerCall=1.21x",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void AKindLinesStageValuesAddUpToItsMsPerBotSec()
    {
        AssertTheCultureIsGerman();
        BotCpuWindow window = SpecSample();

        foreach (BotCpuKindWindow kind in window.Kinds)
        {
            string[] parts = BotCpuReport.KindLine(window, kind).Split(" | ");
            Assert.That(parts, Has.Length.EqualTo(2), kind.Kind.ToString());
            Dictionary<string, string> fields = parts[0].Split(' ').Skip(1)
                .ToDictionary(field => field.Split('=')[0], field => field.Split('=')[1]);
            double[] stages = parts[1].Split(' ')
                .Select(stage => double.Parse(stage.Split('=')[1], CultureInfo.InvariantCulture)).ToArray();

            // Each value is rounded to 3 decimals, and the total to its own (msSec has 1 for unattributed).
            bool unattributed = kind.Kind == Unattributed;
            double total = double.Parse(fields[unattributed ? "msSec" : "msPerBotSec"], CultureInfo.InvariantCulture);
            double rounding = (unattributed ? 0.05 : 0.0005) + stages.Length * 0.0005;
            Assert.That(stages.Sum(), Is.EqualTo(total).Within(rounding), kind.Kind.ToString());
        }
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStagesAreInTickOrderAndAStageThatRoundsToZeroIsLeftOut()
    {
        AssertTheCultureIsGerman();
        BotCpuTotals counts = new();
        counts.AddPresence(Player, WholeWindow);
        // Every stage at (its index + 1) × 0.01 ms per bot per s, charged last stage first, but Crafting at 0.0004,
        // which the line shows as 0.000, and Quests not charged at all.
        foreach (BotCpuStage stage in Enum.GetValues<BotCpuStage>().Reverse())
        {
            double perBot = stage switch
            {
                BotCpuStage.Crafting => 0.0004,
                BotCpuStage.Quests => 0,
                _ => ((int)stage + 1) * 0.01,
            };
            if (perBot > 0)
                Charge(counts, Player, stage, perBot * 60, 30);
        }

        BotCpuWindow window = Close(counts);

        Assert.That(BotCpuReport.KindLine(window, window.Kind(Player)), Is.EqualTo(
            "BOT_CPU win=60 kind=player n=1.0 msSec=1.4 msPerBotSec=1.430 callsPerBotSec=8.5 usPerCall=168.28 " +
            "allocKBPerBotSec=0.0 | GameLoop=0.010 Timer=0.020 ClientBegin=0.030 Npc=0.040 Fleet=0.050 Attack=0.060 " +
            "Casting=0.070 Effect=0.080 EffectListBegin=0.090 EffectListEnd=0.100 Movement=0.110 Reaper=0.130 " +
            "Zone=0.140 ClientEnd=0.150 Pets=0.170 Other=0.180"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void NothingToDivideByIsADashAndALineWithNoStageValueHasNoBar()
    {
        AssertTheCultureIsGerman();

        // n 0 with time charged: every per-bot field is -, and no part after |; usPerCall has its calls (2.1).
        BotCpuWindow window = ShortWindow();
        Assert.That(BotCpuReport.KindLine(window, window.Kind(Playerbot)), Is.EqualTo(
            "BOT_CPU win=61 kind=playerbot n=0.0 msSec=0.5 msPerBotSec=- callsPerBotSec=- usPerCall=33.33 " +
            "allocKBPerBotSec=- p50=- p95=- max=-"));
        Assert.That(BotCpuReport.KindLine(window, window.Kind(Unattributed)), Is.EqualTo(
            "BOT_CPU win=61 kind=unattributed n=- msSec=1.2 callsSec=58.6 usPerCall=20.17 allocKBSec=0.0 | " +
            "Timer=1.182"));

        // A kind that is no bot kind has no spread fields, - or not: a player charged at ClientEnd, never present.
        BotCpuTotals player = new();
        Charge(player, Player, BotCpuStage.ClientEnd, 6, 60);
        BotCpuWindow neverPresent = Close(player);
        Assert.That(BotCpuReport.KindLine(neverPresent, neverPresent.Kind(Player)), Is.EqualTo(
            "BOT_CPU win=60 kind=player n=0.0 msSec=0.1 msPerBotSec=- callsPerBotSec=- usPerCall=100.00 " +
            "allocKBPerBotSec=-"));

        // GameBots in the registry with nothing charged: zeros, usPerCall - with no call, and every stage 0.000, so no
        // part after | either.
        BotCpuWindow idle = Close(new BotCpuTotals(), gamebots: Samples(240));
        Assert.That(BotCpuReport.KindLine(idle, idle.Kind(Gamebot)), Is.EqualTo(
            "BOT_CPU win=60 kind=gamebot n=240.0 msSec=0.0 msPerBotSec=0.000 callsPerBotSec=0.0 usPerCall=- " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=-"));

        // GameBots charged but none of them listed as charged in the window: no spread, so p50, p95 and max are -.
        BotCpuTotals unlisted = new();
        Charge(unlisted, Gamebot, BotCpuStage.Npc, 2_400, 48_000);
        BotCpuWindow noSpread = Close(unlisted, gamebots: Samples(240));
        Assert.That(BotCpuReport.KindLine(noSpread, noSpread.Kind(Gamebot)), Is.EqualTo(
            "BOT_CPU win=60 kind=gamebot n=240.0 msSec=40.0 msPerBotSec=0.167 callsPerBotSec=3.3 usPerCall=50.00 " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=- | Npc=0.167"));

        // No NaN or Infinity in any line.
        foreach (BotCpuWindow each in new[] { window, neverPresent, idle, noSpread })
        {
            Assert.That(string.Join('\n', BotCpuReport.Lines(each, NoPlatformValues)),
                Does.Not.Contain("NaN").And.Not.Contain("Infinity").And.Not.Contain("∞"));
        }
    }

    [Test]
    [SetCulture("de-DE")]
    public void AKindWithNothingWritesNoLine()
    {
        AssertTheCultureIsGerman();

        // Companion, playerbot, piloted and monster have nothing in the sample: 4 kind lines of 7.
        Assert.That(BotCpuReport.Lines(SpecSample(), SpecSampleProcess).Where(line => line.StartsWith("BOT_CPU ")),
            Has.Exactly(4).Items);

        // A window with nothing at all has its process line only.
        Assert.That(BotCpuReport.Lines(Close(new BotCpuTotals()), SpecSampleProcess), Is.EqualTo(new[]
        {
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 " +
            "campThreadCpuPct=3.1 chargedPct=0.0 callsSec=0 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void ATopLineGivesTheGoalOfGameBotsOnly()
    {
        AssertTheCultureIsGerman();
        BotCpuBot[] bots =
        [
            // No PersistentRecord: no goal, -.
            new(Gamebot, "Sir Fenn", 34, "Cleric", 163, null, null, Ticks(2.0 * 60), 1_800),
            // A companion's name, as GameBot gives a helper with none (bots/GameBot.cs:2279).
            new(Companion, "Gm's Cleric Bot", 40, "Cleric", 1, null, null, Ticks(1.5 * 60), 3_600),
            new(Companion, "Hilda", 34, "Cleric", 163, null, null, Ticks(9.0 * 60), 1_800),
            new(Gamebot, "Odd:Goal", 34, "Cleric", 163, "", null, Ticks(1.0 * 60), 1_800),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 180, 3_600);
        Charge(counts, Companion, BotCpuStage.Npc, 630, 5_400);
        counts.AddPresence(Companion, 2 * WholeWindow);

        BotCpuWindow window = Close(counts, gamebots: Samples(2), bots: bots);

        Assert.That(BotCpuReport.TopLine(window, Gamebot),
            Is.EqualTo("BOT_CPU_TOP win=60 kind=gamebot 1=Sir_Fenn:2.000:- 2=Odd_Goal:1.000:-"));
        Assert.That(BotCpuReport.TopLine(window, Companion),
            Is.EqualTo("BOT_CPU_TOP win=60 kind=companion 1=Hilda:9.000 2=Gm's_Cleric_Bot:1.500"));
        // No bot of the kind charged: no line.
        Assert.That(BotCpuReport.TopLine(window, Playerbot), Is.Null);
        Assert.That(BotCpuReport.TopLine(window, Player), Is.Null);

        // The top lines come after the kind lines, in kind order, and before the process line.
        Assert.That(BotCpuReport.Lines(window, NoPlatformValues).Select(line => line.Split(' ')[0] + " " +
                line.Split(' ')[2]),
            Is.EqualTo(new[]
            {
                "BOT_CPU kind=gamebot", "BOT_CPU kind=companion", "BOT_CPU_TOP kind=gamebot",
                "BOT_CPU_TOP kind=companion", "BOT_CPU_PROCESS tps=30.0",
            }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void AProcessValueThePlatformCannotGiveIsADash()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.ProcessLine(ShortWindow(), NoPlatformValues), Is.EqualTo(
            "BOT_CPU_PROCESS win=61 tps=30.0 cores=4 processCpuPct=- loopThreadsCpuPct=- offLoopCpuPct=- " +
            "campThreadCpuPct=- chargedPct=0.2 callsSec=73 loopWaitMs=- throttledMs=- gcPauseMs=- gen2=- " +
            "allocMBSec=- heapMB=- rssMB=-"));

        // A schedstat that reads 0 (3.9): the loop threads' fields are -, and offLoopCpuPct with them.
        BotCpuProcess noSchedstat = SpecSampleProcess with
        {
            LoopThreadsCpuPct = null, CampThreadCpuPct = null, LoopWaitMs = null,
        };
        Assert.That(noSchedstat.OffLoopCpuPct, Is.Null);
        Assert.That(BotCpuReport.ProcessLine(SpecSample(), noSchedstat), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=- offLoopCpuPct=- " +
            "campThreadCpuPct=- chargedPct=54.6 callsSec=212534 loopWaitMs=- throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480"));

        // A value that is not a number is never written as one: -.
        BotCpuProcess notANumber = SpecSampleProcess with
        {
            ProcessCpuPct = double.NaN, GcPauseMs = double.PositiveInfinity, RssMB = double.NegativeInfinity,
        };
        Assert.That(BotCpuReport.ProcessLine(SpecSample(), notANumber), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=- loopThreadsCpuPct=121.0 offLoopCpuPct=- " +
            "campThreadCpuPct=3.1 chargedPct=54.6 callsSec=212534 loopWaitMs=212 throttledMs=0 gcPauseMs=- gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=-"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void OffLoopIsTheDifferenceOfTheTwoValuesTheLineShows()
    {
        AssertTheCultureIsGerman();
        // 148.24 and 120.96 show as 148.2 and 121.0: off the loop is 27.2 as the owner subtracts them, not 27.3.
        BotCpuProcess process = SpecSampleProcess with { ProcessCpuPct = 148.24, LoopThreadsCpuPct = 120.96 };

        Assert.That(process.OffLoopCpuPct, Is.EqualTo(27.2).Within(Tolerance));
        Assert.That(BotCpuReport.ProcessLine(SpecSample(), process),
            Does.Contain(" processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 "));
    }

    [Test]
    [SetCulture("de-DE")]
    public void ACompareRatioIsADashWhenEitherValueIsOrTheGamebotValueIsZero()
    {
        AssertTheCultureIsGerman();

        // GameBots in the registry with nothing charged: their values are 0, or - for usPerCall.
        BotCpuTotals idle = new();
        idle.AddPresence(Player, WholeWindow);
        Charge(idle, Player, BotCpuStage.ClientEnd, 120, 3_600, 1_024);
        BotCpuWindow window = Close(idle, gamebots: Samples(240));
        Assert.That(BotCpuReport.CompareLine(window, window.Compares.Single()), Is.EqualTo(
            "BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/240.0 cpu=- alloc=- calls=- usPerCall=-"));

        // A gamebot value that shows as 0.000 is 0; a player value of 0 gives 0.00x; a player with no calls has
        // usPerCall -.
        BotCpuTotals tiny = new();
        Charge(tiny, Gamebot, BotCpuStage.Npc, 0.0004 * 240 * 60, 432_000, 1_024 * 240 * 60);
        tiny.AddPresence(Player, WholeWindow);
        window = Close(tiny, gamebots: Samples(240));
        Assert.That(BotCpuReport.CompareLine(window, window.Compares.Single()), Is.EqualTo(
            "BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/240.0 cpu=- alloc=0.00x calls=0.00x usPerCall=-"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStateAndErrorLinesAreTheSpecs()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.StateOnByPropertyLine, Is.EqualTo("BOT_CPU_STATE on source=property"));
        Assert.That(BotCpuReport.StateOnByCommandLine("Aldwin", 30),
            Is.EqualTo("BOT_CPU_STATE on source=command by=Aldwin minutes=30"));
        Assert.That(BotCpuReport.StateOnByCommandLine("Aldwin", 1440),
            Is.EqualTo("BOT_CPU_STATE on source=command by=Aldwin minutes=1440"));
        Assert.That(BotCpuReport.StateOnByCommandLine("Aldwin", null),
            Is.EqualTo("BOT_CPU_STATE on source=command by=Aldwin minutes=-"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Command, "Aldwin"),
            Is.EqualTo("BOT_CPU_STATE off source=command by=Aldwin"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Timer), Is.EqualTo("BOT_CPU_STATE off source=timer"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Stop), Is.EqualTo("BOT_CPU_STATE off source=stop"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Error), Is.EqualTo("BOT_CPU_STATE off source=error"));

        Assert.That(BotCpuReport.ErrorLine("tick", new InvalidOperationException("no slot list")),
            Is.EqualTo("BOT_CPU_ERROR tick: System.InvalidOperationException: no slot list"));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuReport.cs (3 block(s))`.

Then the CSV section (2.3). The header is a constant, so its test runs in the default culture, and so does the `utc`
cell's. The sample's rows show that a stage column is the kind's raw ticks ÷ the window: gamebot's `Npc` is 219.256
ms per s, where the line's 0.920 per bot × 238.4 would give 219.328. The short window divides by 61.4 s: 72.6 ms of
timers are 1.182 ms per s, not 1.190.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
            Is.EqualTo("BOT_CPU_ERROR tick: System.InvalidOperationException: no slot list"));
    }
}
=======
            Is.EqualTo("BOT_CPU_ERROR tick: System.InvalidOperationException: no slot list"));
    }

    // ---- The CSV (2.3)

    [Test]
    public void TheCsvHeaderIsTheSpecs()
    {
        Assert.That(BotCpuReport.CsvHeader, Is.EqualTo(
            "utc,win_s,tps,cores,process_cpu_pct,loop_threads_cpu_pct,off_loop_cpu_pct,camp_thread_cpu_pct," +
            "charged_pct,all_calls_sec,loop_wait_ms,throttled_ms,gc_pause_ms,gen2,alloc_mb_sec,heap_mb,rss_mb,kind,n," +
            "ms_sec,calls_sec,alloc_kb_sec,ms_per_bot_sec,calls_per_bot_sec,us_per_call,alloc_kb_per_bot_sec,p50,p95," +
            "max,GameLoop,Timer,ClientBegin,Npc,Fleet,Attack,Casting,Effect,EffectListBegin,EffectListEnd,Movement," +
            "Crafting,Reaper,Zone,ClientEnd,Quests,Pets,Other"));
        // Its stage columns are the stages, in their order.
        Assert.That(BotCpuReport.CsvHeader.Split(',').TakeLast(BotCpuTotals.StageCount),
            Is.EqualTo(Enum.GetNames<BotCpuStage>()));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheSampleWindowsRowsCarryItsProcessValuesAndEachKindsWholeStageTotals()
    {
        AssertTheCultureIsGerman();
        string window = "2026-10-18T19:02:10Z,60,30.0,4,148.2,121.0,27.2,3.1,54.6,212534,212,0,14,0,41.2,2210,3480,";

        IReadOnlyList<string> rows = BotCpuReport.CsvRows(SpecSample(), SpecSampleProcess,
            new DateTime(2026, 10, 18, 19, 2, 10, DateTimeKind.Utc));

        // The stage columns are the kind's raw stage ticks ÷ the window: gamebot's Npc is 13,155.3888 ms in 60 s,
        // 219.256 ms per s, where the line's rounded 0.920 per bot × n 238.4 would give 219.328 (2.3). Player and
        // monster have no p50, p95 or max; unattributed no n and no per-bot field: empty cells.
        Assert.That(rows, Is.EqualTo(new[]
        {
            window + "gamebot,238.4,438.6,98292.3,7438.1,1.840,412.3,4.46,31.2,1.620,3.904,7.412," +
            "0.000,9.774,0.000,219.256,28.131,50.064,33.376,19.310,11.920,9.536,35.760,0.000,4.768,2.384,0.000,0.000," +
            "14.304,0.000",
            window + "player,1.0,2.1,390.0,60.4,2.104,390.0,5.39,60.4,,,," +
            "0.000,0.022,0.310,0.000,0.000,0.201,0.000,0.000,0.000,0.000,0.090,0.000,0.000,0.000,1.311,0.000,0.170," +
            "0.000",
            window + "monster,1830.2,96.0,111642.2,732.1,0.052,61.0,0.86,0.4,,,," +
            "0.000,0.000,0.000,56.956,0.000,14.843,0.000,3.862,0.000,0.000,20.334,0.000,0.000,0.000,0.000,0.000," +
            "0.000,0.000",
            window + "unattributed,,9.1,2210.0,88.0,,,4.12,,,,," +
            "0.402,1.210,0.000,6.890,0.000,0.000,0.000,0.000,0.000,0.000,0.000,0.000,0.000,0.598,0.000,0.000,0.000," +
            "0.000",
        }));
        int columns = BotCpuReport.CsvHeader.Split(',').Length;
        Assert.That(rows.Select(row => row.Split(',').Length), Is.All.EqualTo(columns));
    }

    [Test]
    [SetCulture("de-DE")]
    public void ADashIsAnEmptyCellAndAKindWithNoNKeepsItsStageTotals()
    {
        AssertTheCultureIsGerman();
        string window = "2026-10-18T19:03:11Z,61,30.0,4,,,,,0.2,73,,,,,,,,";
        string zeros = string.Join(',', Enumerable.Repeat("0.000", 16));

        // A 61.4 s window divides by 61.4 (win_s 61): unattributed's 72.6 ms of timers are 1.182 ms per s, not 1.190.
        IReadOnlyList<string> rows = BotCpuReport.CsvRows(ShortWindow(), NoPlatformValues,
            new DateTime(2026, 10, 18, 19, 3, 11, DateTimeKind.Utc));

        Assert.That(rows, Is.EqualTo(new[]
        {
            window + "playerbot,0.0,0.5,14.7,0.0,,,33.33,,,,,0.000,0.489," + zeros,
            window + "unattributed,,1.2,58.6,0.0,,,20.17,,,,,0.000,1.182," + zeros,
        }));
        int columns = BotCpuReport.CsvHeader.Split(',').Length;
        Assert.That(rows.Select(row => row.Split(',').Length), Is.All.EqualTo(columns));
    }

    [Test]
    public void TheUtcCellIsTheWindowsEndToTheSecond() =>
        Assert.That(BotCpuReport.CsvUtc(new DateTime(2026, 10, 18, 19, 2, 10, 750, DateTimeKind.Utc)),
            Is.EqualTo("2026-10-18T19:02:10Z"));
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuReport.cs (1 block(s))`.

Last, the chat section (2.4). `OnAt` is when the sample's meter turned on, 14:02:05 in the server's local time.
`SinceTheSample` adds 11 earlier windows, at 1.7877 ms per bot per s for gamebot and 2.0124 for player, before the
sample, so the totals since on are 1.792 and 2.020, as 2.4's sample says. 2.4's rule puts every kind with bot-seconds
above 0 in that line, and monster has them in the sample, so the expected line ends `monster 0.052 ms/s per bot.`,
where 2.4's sample stops at player. `OffSaysWhetherTheLastSecondsAreWritten` has no decimal in its text, so it runs in
the default culture; its 4.99 case shows that the seconds are rounded down and that the 5 s test is on the exact
value.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
    public void TheUtcCellIsTheWindowsEndToTheSecond() =>
        Assert.That(BotCpuReport.CsvUtc(new DateTime(2026, 10, 18, 19, 2, 10, 750, DateTimeKind.Utc)),
            Is.EqualTo("2026-10-18T19:02:10Z"));
}
=======
    public void TheUtcCellIsTheWindowsEndToTheSecond() =>
        Assert.That(BotCpuReport.CsvUtc(new DateTime(2026, 10, 18, 19, 2, 10, 750, DateTimeKind.Utc)),
            Is.EqualTo("2026-10-18T19:02:10Z"));

    // ---- The chat (2.4)

    // When the sample's meter was turned on, in the server's local time.
    private static readonly DateTime OnAt = new(2026, 10, 18, 14, 2, 5);

    // The totals since on for the sample status: 11 windows before 2.1's sample with 238.4 GameBots at 1.7877 ms per
    // bot per s and one player at 2.0124, so that with the sample they are gamebot 1.792 and player 2.020 (2.4's
    // sample); monster has bot-seconds only in the sample, 0.052.
    private static BotCpuSince SinceTheSample(bool reset)
    {
        BotCpuTotals earlier = new();
        Charge(earlier, Gamebot, BotCpuStage.Npc, 1.7877 * 238.4 * 60, 5_900_000);
        earlier.AddPresence(Player, WholeWindow);
        Charge(earlier, Player, BotCpuStage.ClientEnd, 2.0124 * 60, 23_400);
        BotCpuWindow before = Close(earlier, gamebots: Samples(238, 239, 238, 239, 238));

        BotCpuSince since = BotCpuSince.Start(reset);
        for (int window = 0; window < 11; window++)
            since = since.Add(before);

        return since.Add(SpecSample());
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStatusOfTheSampleWindowIsTheSpecs()
    {
        AssertTheCultureIsGerman();

        // 2.4's sample word for word, but for the since-on line: it gives every kind with bot-seconds above 0 (2.4),
        // and monster has them, so monster is there too. 12 min 15 s after on: 12 min, and 12 windows written.
        IReadOnlyList<string> status = BotCpuReport.StatusText(OnAt, byCommand: true, stopsAt: OnAt.AddMinutes(30),
            now: OnAt.AddMinutes(12.25), SpecSample(), SpecSampleProcess, SinceTheSample(reset: false));
        Assert.That(status, Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by command, 12 min; stops at 14:32).",
            "Last minute (60 s, 30.0 ticks/s): process 148% of one core (4 cores), 55% charged to kinds, 27% off the " +
            "loop.",
            "gamebot n=238.4: 1.840 ms/s per bot (438.6 ms/s in all); most in Npc 0.920",
            "player n=1.0: 2.104 ms/s per bot (2.1 ms/s in all); most in ClientEnd 1.311",
            "monster n=1830.2: 0.052 ms/s per bot (96.0 ms/s in all); most in Npc 0.031",
            "unattributed: 9.1 ms/s; most in Npc 6.890",
            "Since on (12 min): gamebot 1.792, player 2.020, monster 0.052 ms/s per bot.",
        }));

        // After /botcpu reset the last line says so.
        Assert.That(BotCpuReport.StatusText(OnAt, byCommand: true, stopsAt: OnAt.AddMinutes(30),
                now: OnAt.AddMinutes(12.25), SpecSample(), SpecSampleProcess, SinceTheSample(reset: true)).Last(),
            Is.EqualTo("Since reset (12 min): gamebot 1.792, player 2.020, monster 0.052 ms/s per bot."));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStatusBeforeTheFirstWindowSaysWhenItComes()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.StatusText(OnAt, byCommand: false, stopsAt: null, now: OnAt.AddSeconds(35), null,
            null, BotCpuSince.Start(reset: false)), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by property, 0 min; no stop time).",
            "No full minute yet; the first lines come at 14:03.",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStatusShowsADashAsTheLinesDo()
    {
        AssertTheCultureIsGerman();
        BotCpuWindow window = ShortWindow();

        // No platform values; a player bot with n 0 has no per-bot value and no stage; no kind has bot-seconds, so
        // there is no since-on line.
        Assert.That(BotCpuReport.StatusText(OnAt, byCommand: false, stopsAt: null, now: OnAt.AddSeconds(85), window,
            NoPlatformValues, BotCpuSince.Start(reset: false).Add(window)), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by property, 1 min; no stop time).",
            "Last minute (61 s, 30.0 ticks/s): process - of one core (4 cores), 0% charged to kinds, - off the loop.",
            "playerbot n=0.0: - ms/s per bot (0.5 ms/s in all); most in -",
            "unattributed: 1.2 ms/s; most in Timer 1.182",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void EachReplyIsTheSpecs()
    {
        AssertTheCultureIsGerman();
        const string where = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

        Assert.That(BotCpuReport.OffStatusText, Is.EqualTo("Bot CPU meter: off. /botcpu on [minutes] turns it on."));
        Assert.That(BotCpuReport.NoWindowYetText(OnAt),
            Is.EqualTo("No full minute yet; the first lines come at 14:03."));
        Assert.That(BotCpuReport.OnText(30, OnAt.AddMinutes(30)),
            Is.EqualTo("Bot CPU meter on for 30 minutes (until 14:32)" + where));
        Assert.That(BotCpuReport.OnText(), Is.EqualTo("Bot CPU meter on until /botcpu off or the next start" + where));
        Assert.That(BotCpuReport.AlreadyOnText(OnAt.AddMinutes(30)),
            Is.EqualTo("Bot CPU meter already on; it now stops at 14:32."));
        Assert.That(BotCpuReport.AlreadyOnText(null),
            Is.EqualTo("Bot CPU meter already on; it now runs until /botcpu off or the next start."));
        Assert.That(BotCpuReport.OffText(37.6), Is.EqualTo("Bot CPU meter off. The last 37 s are being written."));
        Assert.That(BotCpuReport.OffText(3.2), Is.EqualTo("Bot CPU meter off. The last 3 s were too short to write."));
        Assert.That(BotCpuReport.AlreadyOffText, Is.EqualTo("Bot CPU meter is already off."));
        Assert.That(BotCpuReport.ResetText, Is.EqualTo("Bot CPU totals reset."));
        Assert.That(BotCpuReport.TopText(SpecSample(), Companion),
            Is.EqualTo(new[] { "No companion was charged in the last minute." }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheMinutesAndTimesOfTheReplies()
    {
        AssertTheCultureIsGerman();
        const string where = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

        // 1 to 1440 minutes (2.4), no digit grouping; one minute is singular.
        Assert.That(BotCpuReport.OnText(1, OnAt.AddMinutes(1)),
            Is.EqualTo("Bot CPU meter on for 1 minute (until 14:03)" + where));
        Assert.That(BotCpuReport.OnText(1440, OnAt.AddMinutes(1440)),
            Is.EqualTo("Bot CPU meter on for 1440 minutes (until 14:02)" + where));
        // The 24-hour clock, with its leading zero.
        Assert.That(BotCpuReport.NoWindowYetText(new DateTime(2026, 10, 18, 8, 59, 30)),
            Is.EqualTo("No full minute yet; the first lines come at 09:00."));
        Assert.That(BotCpuReport.AlreadyOnText(new DateTime(2026, 10, 18, 23, 7, 0)),
            Is.EqualTo("Bot CPU meter already on; it now stops at 23:07."));
    }

    // The last seconds at off are written from 5 s (2.2), in whole seconds.
    [TestCase(4.99, "Bot CPU meter off. The last 4 s were too short to write.")]
    [TestCase(5.0, "Bot CPU meter off. The last 5 s are being written.")]
    [TestCase(59.9, "Bot CPU meter off. The last 59 s are being written.")]
    [TestCase(0, "Bot CPU meter off. The last 0 s were too short to write.")]
    [TestCase(-1, "Bot CPU meter off. The last 0 s were too short to write.")]
    [TestCase(double.NaN, "Bot CPU meter off. The last 0 s were too short to write.")]
    public void OffSaysWhetherTheLastSecondsAreWritten(double seconds, string reply) =>
        Assert.That(BotCpuReport.OffText(seconds), Is.EqualTo(reply));

    [Test]
    [SetCulture("de-DE")]
    public void TopListsTheFiveCostliestGameBotsWithTheirGoalAndActivity()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.TopText(SpecSample(), Gamebot), Is.EqualTo(new[]
        {
            "1. Aldric (34 Cleric, region 163): 7.412 ms/s, 880 calls/s, Rvr, \"Riding stablemaster horse route\"",
            "2. Brennos (50 Armsman, region 1): 6.020 ms/s, 750 calls/s, Battleground, \"Choosing first live goal\"",
            "3. Cathal (42 Wizard, region 1): 5.881 ms/s, 720 calls/s, GroupPve, " +
            "\"Defeated; reassessing target difficulty\"",
            "4. Dagny (47 Paladin, region 163): 5.104 ms/s, 600 calls/s, Rvr, \"Recovered to safe capital spawn\"",
            "5. Eirik (20 Scout, region 1): 4.950 ms/s, 500 calls/s, SoloPve, \"Queued after a failed world transfer\"",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TopShowsADashAndEmptyQuotesForAGameBotWithNoRecord()
    {
        AssertTheCultureIsGerman();
        BotCpuBot[] bots =
        [
            new(Gamebot, "Sir Fenn", 34, "Cleric", 163, null, null, Ticks(2.0 * 60), 1_800),
            new(Companion, "Hilda", 34, "Cleric", 163, null, null, Ticks(9.0 * 60), 1_800),
            new(Companion, "Gm's Cleric Bot", 40, "Cleric", 1, null, null, Ticks(1.5 * 60), 3_600),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 120, 1_800);
        Charge(counts, Companion, BotCpuStage.Npc, 630, 5_400);

        BotCpuWindow window = Close(counts, gamebots: Samples(1), bots: bots);

        // The goal and activity as the slow-turn warning reads them, with ?. (NS:81): -, and "" (2.4).
        Assert.That(BotCpuReport.TopText(window, Gamebot),
            Is.EqualTo(new[] { "1. Sir Fenn (34 Cleric, region 163): 2.000 ms/s, 30 calls/s, -, \"\"" }));
        // The other bot kinds have no goal or activity; the name is the bot's own, not the log line's.
        Assert.That(BotCpuReport.TopText(window, Companion), Is.EqualTo(new[]
        {
            "1. Hilda (34 Cleric, region 163): 9.000 ms/s, 30 calls/s",
            "2. Gm's Cleric Bot (40 Cleric, region 1): 1.500 ms/s, 60 calls/s",
        }));
        Assert.That(BotCpuReport.TopText(window, Playerbot),
            Is.EqualTo(new[] { "No playerbot was charged in the last minute." }));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuReport.cs (1 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 2 errors, both `CS0246` for `BotCpuProcess`, the type of the two process
fixtures. The compiler stops at the missing type before it reaches the missing `BotCpuReport` members. Each error line
starts with the file's absolute path and ends with the project's:

```
source/server/Tests/UnitTests/UT_BotCpuReport.cs(1040,29): error CS0246: The type or namespace name 'BotCpuProcess' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuReport.cs(1055,29): error CS0246: The type or namespace name 'BotCpuProcess' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The summary (the warning total varies between builds; 609 was seen):

```
Build FAILED.
    609 Warning(s)
    2 Error(s)
```

- [ ] **Step 3: Implement.** Five runs of the edit helper on
`source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs`.

First the `using`, the header comment, the two new types and `WindowSeconds`. `BotCpuProcess` carries a window's
process values (3.9); Tasks 8 and 9 build one per window. Every value but `Cores` is nullable
(`Environment.ProcessorCount` cannot fail), so any reader that fails gives `-`. `OffLoopCpuPct` is the difference of
the two values as the line shows them, 1 decimal: 148.24 and 120.96 show as 148.2 and 121.0 and give 27.2, the
owner's own subtraction, not 27.3. Task 5's compare ratios take their values the same way. `BotCpuOffSource` is the
`source=` of the off line, written as its lower-case name.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
using System.Linq;
using System.Threading;
=======
using System.Linq;
using System.Text;
using System.Threading;
>>>>>>> NEW
<<<<<<< OLD
// kind (3.10), the compare pairs (2.1) and the totals since on (2.4). Plain values in and out, no server state, so it
// is unit-tested without a server (UT_BotCpuReport). BotCpuMeter is the hot path that fills the slots and counters;
// BotCpuScript reads the server's state and writes the lines.
=======
// kind (3.10), the compare pairs (2.1) and the totals since on (2.4). It also makes their exact text, in the invariant
// culture: every log line (2.1), the CSV's header and rows (2.3) and every chat line of /botcpu (2.4). Plain values in
// and out, no server state, so it is unit-tested without a server (UT_BotCpuReport). BotCpuMeter is the hot path that
// fills the slots and counters; BotCpuScript reads the server's state and writes the lines.
>>>>>>> NEW
<<<<<<< OLD
/// The report's numbers (2.1, 2.2, 2.4, 3.8, 3.10): plain values in and out, no server state.
/// </summary>
public static class BotCpuReport
{
=======
/// A window's process values read from the platform (2.1 <c>BOT_CPU_PROCESS</c>, 3.9): over the window, or at its end
/// for the two memory values. Null is a value the platform can't give (Windows, a file missing in the container, a
/// schedstat that reads 0): <c>-</c> on the line, an empty cell in the CSV. <c>tps</c>, <c>chargedPct</c> and
/// <c>callsSec</c> come from the window (<see cref="BotCpuWindow"/>).
/// </summary>
/// <param name="Cores"><c>cores</c>: <see cref="Environment.ProcessorCount"/>.</param>
/// <param name="ProcessCpuPct"><c>processCpuPct</c>: the whole process, % of one core.</param>
/// <param name="LoopThreadsCpuPct"><c>loopThreadsCpuPct</c>: the game-loop threads, % of one core.</param>
/// <param name="CampThreadCpuPct"><c>campThreadCpuPct</c>: the camp planner thread, % of one core.</param>
/// <param name="LoopWaitMs"><c>loopWaitMs</c>: the loop threads' time ready to run but waiting for a CPU, ms.</param>
/// <param name="ThrottledMs"><c>throttledMs</c>: the container's CPU throttling, ms.</param>
/// <param name="GcPauseMs"><c>gcPauseMs</c>: GC pause time, ms.</param>
/// <param name="Gen2"><c>gen2</c>: full collections.</param>
/// <param name="AllocMBSec"><c>allocMBSec</c>: all allocation in the process, MB per s.</param>
/// <param name="HeapMB"><c>heapMB</c>: the managed heap, <c>GC.GetTotalMemory(false)</c>, MB.</param>
/// <param name="RssMB"><c>rssMB</c>: resident memory, MB.</param>
public sealed record BotCpuProcess(int Cores, double? ProcessCpuPct, double? LoopThreadsCpuPct,
    double? CampThreadCpuPct, double? LoopWaitMs, double? ThrottledMs, double? GcPauseMs, long? Gen2,
    double? AllocMBSec, double? HeapMB, double? RssMB)
{
    /// <summary><c>offLoopCpuPct</c>: <see cref="ProcessCpuPct"/> − <see cref="LoopThreadsCpuPct"/> as the line shows
    /// them, 1 decimal, so it is the owner's own subtraction of the two; null when either is.</summary>
    public double? OffLoopCpuPct => ProcessCpuPct is double process && LoopThreadsCpuPct is double loop
        ? BotCpuReport.Round(BotCpuReport.Round(process, 1) - BotCpuReport.Round(loop, 1), 1)
        : null;
}

/// <summary>Why the meter turned off, as its <c>BOT_CPU_STATE off</c> line says (2.1): a GM's <c>/botcpu off</c>, the
/// stop time of <c>/botcpu on [minutes]</c>, the server's stop, or a fault in the report (3.7).</summary>
public enum BotCpuOffSource
{
    Command,
    Timer,
    Stop,
    Error,
}

/// <summary>
/// The report's numbers (2.1, 2.2, 2.4, 3.8, 3.10) and their text (2.1, 2.3, 2.4): plain values in and out, no server
/// state. The text is in the invariant culture, a <c>.</c> decimal point whatever the server's culture.
/// </summary>
public static class BotCpuReport
{
    /// <summary>A window's length: 60 s, counted from the moment the meter turns on (2.2).</summary>
    public const double WindowSeconds = 60;

>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs (3 block(s))`.

Then the log lines (2.1). A bot kind always carries `p50`, `p95` and `max`: with bot-seconds but no bot charged (no
spread), they are `p50=- p95=- max=-`. Player, monster and unattributed never carry them. A line with no stage value
shown, because its bot-seconds are 0 or every stage rounds to 0.000, has no ` | ` at all. `BOT_CPU_TOP`'s goal goes
through `TopName` as the name does (none is `-`, a space or `:` is `_`), so the line's parts stay separable.
`ErrorLine` writes `exception.ToString()`: the type, the message and the stack.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
        return divisor == 0 ? null : Round(value.Value, decimals) / divisor;
    }
}
=======
        return divisor == 0 ? null : Round(value.Value, decimals) / divisor;
    }

    // ---- The text (2.1, 2.3, 2.4)

    /// <summary>The <c>BOT_CPU_STATE</c> line of a meter the <c>bot_cpu_meter</c> property starts (2.1, 4.1).</summary>
    public const string StateOnByPropertyLine = "BOT_CPU_STATE on source=property";

    /// <summary>The CSV's header (2.3): the process columns, the kind columns, then the stages in their order.
    /// </summary>
    public const string CsvHeader =
        "utc,win_s,tps,cores,process_cpu_pct,loop_threads_cpu_pct,off_loop_cpu_pct,camp_thread_cpu_pct,charged_pct," +
        "all_calls_sec,loop_wait_ms,throttled_ms,gc_pause_ms,gen2,alloc_mb_sec,heap_mb,rss_mb," +
        "kind,n,ms_sec,calls_sec,alloc_kb_sec,ms_per_bot_sec,calls_per_bot_sec,us_per_call,alloc_kb_per_bot_sec," +
        "p50,p95,max,GameLoop,Timer,ClientBegin,Npc,Fleet,Attack,Casting,Effect,EffectListBegin,EffectListEnd," +
        "Movement,Crafting,Reaper,Zone,ClientEnd,Quests,Pets,Other";

    /// <summary><c>/botcpu</c> while the meter is off, and <c>/botcpu top</c> and <c>/botcpu reset</c> too: the off
    /// text (2.4).</summary>
    public const string OffStatusText = "Bot CPU meter: off. /botcpu on [minutes] turns it on.";

    /// <summary><c>/botcpu off</c> while the meter is off (2.4).</summary>
    public const string AlreadyOffText = "Bot CPU meter is already off.";

    /// <summary><c>/botcpu reset</c> while the meter is on (2.4).</summary>
    public const string ResetText = "Bot CPU totals reset.";

    private const string UntilOff = "until /botcpu off or the next start";
    private const string WhereTheLinesGo = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

    /// <summary>A kind's text name (2.5): its member name in lower case.</summary>
    public static string KindName(BotCpuKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>
    /// Every log line of a window, in their order (2.1): a <c>BOT_CPU</c> line per kind that has one, in kind order;
    /// a <c>BOT_CPU_TOP</c> line per bot kind with a bot charged; <c>BOT_CPU_PROCESS</c>; a <c>BOT_CPU_COMPARE</c> line
    /// per pair written.
    /// </summary>
    public static IReadOnlyList<string> Lines(BotCpuWindow window, BotCpuProcess process)
    {
        ArgumentNullException.ThrowIfNull(window);
        List<string> lines = [];
        foreach (BotCpuKindWindow kind in window.Kinds)
            lines.Add(KindLine(window, kind));

        for (int k = 0; k < BotCpuTotals.KindCount; k++)
        {
            string top = TopLine(window, (BotCpuKind)k);
            if (top != null)
                lines.Add(top);
        }

        lines.Add(ProcessLine(window, process));
        foreach (BotCpuCompare compare in window.Compares)
            lines.Add(CompareLine(window, compare));

        return lines;
    }

    /// <summary>
    /// A kind's <c>BOT_CPU</c> line (2.1). Unattributed has <c>n=-</c>, and <c>callsSec</c> and <c>allocKBSec</c> in
    /// place of the per-bot fields; only the bot kinds have <c>p50</c>, <c>p95</c> and <c>max</c>. After <c>|</c>, the
    /// stage values in tick order, those that round to 0.000 left out; with none, the line has no <c>|</c> part.
    /// </summary>
    public static string KindLine(BotCpuWindow window, BotCpuKindWindow kind)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(kind);
        StringBuilder line = new StringBuilder("BOT_CPU win=").Append(Text(window.Win))
            .Append(" kind=").Append(KindName(kind.Kind))
            .Append(" n=").Append(Text(kind.N, 1))
            .Append(" msSec=").Append(Text(kind.MsSec, 1));
        if (kind.Kind == BotCpuKind.Unattributed)
        {
            line.Append(" callsSec=").Append(Text(kind.CallsSec, 1))
                .Append(" usPerCall=").Append(Text(kind.UsPerCall, 2))
                .Append(" allocKBSec=").Append(Text(kind.AllocKBSec, 1));
        }
        else
        {
            line.Append(" msPerBotSec=").Append(Text(kind.MsPerBotSec, 3))
                .Append(" callsPerBotSec=").Append(Text(kind.CallsPerBotSec, 1))
                .Append(" usPerCall=").Append(Text(kind.UsPerCall, 2))
                .Append(" allocKBPerBotSec=").Append(Text(kind.AllocKBPerBotSec, 1));
            if (BotCpuMeter.IsBotKind(kind.Kind))
            {
                line.Append(" p50=").Append(Text(kind.P50, 3))
                    .Append(" p95=").Append(Text(kind.P95, 3))
                    .Append(" max=").Append(Text(kind.Max, 3));
            }
        }

        string separator = " | ";
        foreach ((BotCpuStage stage, string value) in ShownStages(kind))
        {
            line.Append(separator).Append(stage.ToString()).Append('=').Append(value);
            separator = " ";
        }

        return line.ToString();
    }

    /// <summary>
    /// A bot kind's <c>BOT_CPU_TOP</c> line (2.1): <c>rank=name:ms per s</c>, a space or <c>:</c> in the name made
    /// <c>_</c>, and for GameBots the goal (<c>PersistentRecord.ObjectiveKind</c>, written as the name is) or <c>-</c>.
    /// Null when none of the kind's bots was charged in the window.
    /// </summary>
    public static string TopLine(BotCpuWindow window, BotCpuKind kind)
    {
        ArgumentNullException.ThrowIfNull(window);
        IReadOnlyList<BotCpuTopEntry> top = window.Top(kind);
        if (top.Count == 0)
            return null;

        StringBuilder line = new StringBuilder("BOT_CPU_TOP win=").Append(Text(window.Win))
            .Append(" kind=").Append(KindName(kind));
        foreach (BotCpuTopEntry entry in top)
        {
            line.Append(' ').Append(Text(entry.Rank)).Append('=').Append(TopName(entry.Bot.Name))
                .Append(':').Append(Text(entry.MsSec, 3));
            if (kind == BotCpuKind.Gamebot)
                line.Append(':').Append(TopName(entry.Bot.Goal));
        }

        return line.ToString();
    }

    /// <summary>The <c>BOT_CPU_PROCESS</c> line (2.1); a value the platform can't give is <c>-</c> (3.9).</summary>
    public static string ProcessLine(BotCpuWindow window, BotCpuProcess process)
    {
        ArgumentNullException.ThrowIfNull(window);
        StringBuilder line = new StringBuilder("BOT_CPU_PROCESS win=").Append(Text(window.Win));
        foreach ((string name, string value) in ProcessFields(window, process))
            line.Append(' ').Append(name).Append('=').Append(value);

        return line.ToString();
    }

    // The process line's fields after win, in its order, which is also the CSV's (2.1, 2.3).
    private static (string Name, string Value)[] ProcessFields(BotCpuWindow window, BotCpuProcess process) =>
    [
        ("tps", Text(window.Tps, 1)),
        ("cores", Text(process?.Cores)),
        ("processCpuPct", Text(process?.ProcessCpuPct, 1)),
        ("loopThreadsCpuPct", Text(process?.LoopThreadsCpuPct, 1)),
        ("offLoopCpuPct", Text(process?.OffLoopCpuPct, 1)),
        ("campThreadCpuPct", Text(process?.CampThreadCpuPct, 1)),
        ("chargedPct", Text(window.ChargedPct, 1)),
        ("callsSec", Text(window.CallsSec, 0)),
        ("loopWaitMs", Text(process?.LoopWaitMs, 0)),
        ("throttledMs", Text(process?.ThrottledMs, 0)),
        ("gcPauseMs", Text(process?.GcPauseMs, 0)),
        ("gen2", Text(process?.Gen2)),
        ("allocMBSec", Text(process?.AllocMBSec, 1)),
        ("heapMB", Text(process?.HeapMB, 0)),
        ("rssMB", Text(process?.RssMB, 0)),
    ];

    /// <summary>A <c>BOT_CPU_COMPARE</c> line (2.1): each ratio with 2 decimals and <c>x</c>, or <c>-</c>.</summary>
    public static string CompareLine(BotCpuWindow window, BotCpuCompare compare)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(compare);
        return "BOT_CPU_COMPARE win=" + Text(window.Win) + " pair=" + KindName(compare.Kind) + "/gamebot n=" +
            Text(compare.N, 1) + "/" + Text(compare.GamebotN, 1) + " cpu=" + RatioText(compare.Cpu) + " alloc=" +
            RatioText(compare.Alloc) + " calls=" + RatioText(compare.Calls) + " usPerCall=" +
            RatioText(compare.UsPerCall);
    }

    /// <summary>The <c>BOT_CPU_STATE</c> line of a GM's <c>/botcpu on [minutes]</c>, also written when it changes the
    /// stop time of a running meter (2.1); no minutes is <c>-</c>.</summary>
    public static string StateOnByCommandLine(string by, int? minutes) =>
        "BOT_CPU_STATE on source=command by=" + Who(by) + " minutes=" + Text(minutes);

    /// <summary>The <c>BOT_CPU_STATE off</c> line (2.1); <paramref name="by"/>, the GM, only for
    /// <see cref="BotCpuOffSource.Command"/>.</summary>
    public static string StateOffLine(BotCpuOffSource source, string by = null) =>
        source == BotCpuOffSource.Command
            ? "BOT_CPU_STATE off source=command by=" + Who(by)
            : "BOT_CPU_STATE off source=" + source.ToString().ToLowerInvariant();

    /// <summary>The Warn line before <c>off source=error</c> (2.1, 3.7): the step that failed and the exception.
    /// </summary>
    public static string ErrorLine(string step, Exception exception) =>
        "BOT_CPU_ERROR " + step + ": " + exception;
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs (1 block(s))`.

Then the CSV's rows (2.3). Each row starts with the `utc` cell, `win_s` and the process line's fields after `win`,
whose order is the CSV's; then the kind, its figures and its 18 stage columns. A `-` is an empty cell. `CsvUtc` cuts
the time to the second.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
    public static string ErrorLine(string step, Exception exception) =>
        "BOT_CPU_ERROR " + step + ": " + exception;
}
=======
    public static string ErrorLine(string step, Exception exception) =>
        "BOT_CPU_ERROR " + step + ": " + exception;

    /// <summary>
    /// A window's CSV rows (2.3), one per kind line in kind order: the window's process values on each row, then the
    /// kind's figures, then its stage columns, which are the whole kind's raw stage ticks ÷ the exact window length
    /// (3 decimals, 0.000 for zero), never left out. A <c>-</c> of the line, and a field the kind has no value for, is
    /// an empty cell.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="process">Its process values.</param>
    /// <param name="end">The window's end, in UTC: the <c>utc</c> cell.</param>
    public static IReadOnlyList<string> CsvRows(BotCpuWindow window, BotCpuProcess process, DateTime end)
    {
        ArgumentNullException.ThrowIfNull(window);
        StringBuilder start = new StringBuilder(CsvUtc(end)).Append(',').Append(Text(window.Win));
        foreach ((string _, string value) in ProcessFields(window, process))
            start.Append(',').Append(Cell(value));

        List<string> rows = [];
        foreach (BotCpuKindWindow kind in window.Kinds)
        {
            StringBuilder row = new StringBuilder().Append(start).Append(',').Append(KindName(kind.Kind));
            string[] figures =
            [
                Text(kind.N, 1), Text(kind.MsSec, 1), Text(kind.CallsSec, 1), Text(kind.AllocKBSec, 1),
                Text(kind.MsPerBotSec, 3), Text(kind.CallsPerBotSec, 1), Text(kind.UsPerCall, 2),
                Text(kind.AllocKBPerBotSec, 1), Text(kind.P50, 3), Text(kind.P95, 3), Text(kind.Max, 3),
            ];
            foreach (string figure in figures)
                row.Append(',').Append(Cell(figure));
            foreach (double stage in kind.StageMsSec)
                row.Append(',').Append(Cell(Text(stage, 3)));

            rows.Add(row.ToString());
        }

        return rows;
    }

    /// <summary>The CSV's <c>utc</c> cell (2.3): <paramref name="end"/>, in UTC, to the second, as
    /// <c>2026-10-18T19:02:10Z</c>. A local time is turned into UTC first.</summary>
    public static string CsvUtc(DateTime end) =>
        (end.Kind == DateTimeKind.Local ? end.ToUniversalTime() : end)
        .ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs (1 block(s))`.

Then the chat (2.4). The times are `HH:mm` on the 24-hour clock, with a leading zero (09:00), from the local
`DateTime` the caller passes. Minutes and seconds are whole and rounded down: 12 min 15 s after on shows 12, and
`OffText(4.99)` says "The last 4 s were too short to write", which keeps "under 5 s" true. A count of 1 is singular:
"on for 1 minute", "(1 core)". A `-` in a percentage is written without `%`: "process - of one core", "- off the
loop", and "(- cores)" with no process values. The since-on line is left out with no window written, and also when no
kind has bot-seconds. `TopText` shows the goal as it is (`-` when null or empty) and the activity in quotes (`""` when
null), as the slow-turn warning logs them (NS:81).

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
        (end.Kind == DateTimeKind.Local ? end.ToUniversalTime() : end)
        .ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
=======
        (end.Kind == DateTimeKind.Local ? end.ToUniversalTime() : end)
        .ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>/botcpu</c> while the meter is on (2.4): the status line, then, before the first window, when its lines come;
    /// after one, the last minute's line, a line per kind that has a kind line, in kind order, and the totals since
    /// on or reset, left out with no window written or no kind with bot-seconds. The times are the server's local
    /// time; the minutes are whole minutes, rounded down.
    /// </summary>
    /// <param name="onSince">When the run opened: the first window starts there.</param>
    /// <param name="byCommand">Whether a GM's command turned it on, else the property.</param>
    /// <param name="stopsAt">Its stop time; null for none.</param>
    /// <param name="now">The time of the command.</param>
    /// <param name="last">The last window written; null before the first.</param>
    /// <param name="process">That window's process values.</param>
    /// <param name="since">The totals since on or reset.</param>
    public static IReadOnlyList<string> StatusText(DateTime onSince, bool byCommand, DateTime? stopsAt, DateTime now,
        BotCpuWindow last, BotCpuProcess process, BotCpuSince since)
    {
        List<string> lines =
        [
            "Bot CPU meter: on since " + Clock(onSince) + " (" + (byCommand ? "by command" : "by property") + ", " +
            Text(Whole((now - onSince).TotalMinutes)) + " min; " +
            (stopsAt is DateTime stop ? "stops at " + Clock(stop) : "no stop time") + ").",
        ];
        if (last == null)
        {
            lines.Add(NoWindowYetText(onSince));
            return lines;
        }

        lines.Add("Last minute (" + Text(last.Win) + " s, " + Text(last.Tps, 1) + " ticks/s): process " +
            Percent(process?.ProcessCpuPct) + " of one core (" + Count(process?.Cores, "core") + "), " +
            Percent(last.ChargedPct) + " charged to kinds, " + Percent(process?.OffLoopCpuPct) + " off the loop.");
        foreach (BotCpuKindWindow kind in last.Kinds)
        {
            lines.Add(kind.Kind == BotCpuKind.Unattributed
                ? "unattributed: " + Text(kind.MsSec, 1) + " ms/s; most in " + MostIn(kind)
                : KindName(kind.Kind) + " n=" + Text(kind.N, 1) + ": " + Text(kind.MsPerBotSec, 3) +
                  " ms/s per bot (" + Text(kind.MsSec, 1) + " ms/s in all); most in " + MostIn(kind));
        }

        string sinceLine = SinceText(since);
        if (sinceLine != null)
            lines.Add(sinceLine);

        return lines;
    }

    /// <summary><c>/botcpu</c> and <c>/botcpu top</c> before the first window (2.4): when its lines come, a window's
    /// length after <paramref name="onSince"/>.</summary>
    public static string NoWindowYetText(DateTime onSince) =>
        "No full minute yet; the first lines come at " + Clock(onSince.AddSeconds(WindowSeconds)) + ".";

    /// <summary><c>/botcpu on</c> with no minutes while the meter is off (2.4).</summary>
    public static string OnText() => "Bot CPU meter on " + UntilOff + WhereTheLinesGo;

    /// <summary><c>/botcpu on minutes</c> while the meter is off (2.4), stopping at <paramref name="stopsAt"/>.
    /// </summary>
    public static string OnText(int minutes, DateTime stopsAt) =>
        "Bot CPU meter on for " + Count(minutes, "minute") + " (until " + Clock(stopsAt) + ")" + WhereTheLinesGo;

    /// <summary><c>/botcpu on [minutes]</c> while the meter is on (2.4): its new stop time, or none.</summary>
    public static string AlreadyOnText(DateTime? stopsAt) =>
        "Bot CPU meter already on; it now " +
        (stopsAt is DateTime stop ? "stops at " + Clock(stop) : "runs " + UntilOff) + ".";

    /// <summary><c>/botcpu off</c> while the meter is on (2.4), <paramref name="openSeconds"/> into the open window,
    /// in whole seconds rounded down: written from <see cref="MinimumWindowSeconds"/>, as the final window is (2.2).
    /// </summary>
    public static string OffText(double openSeconds) =>
        "Bot CPU meter off. The last " + Text(Whole(openSeconds)) +
        (openSeconds >= MinimumWindowSeconds ? " s are being written." : " s were too short to write.");

    /// <summary>
    /// <c>/botcpu top [kind]</c> once a window is written (2.4): a line per bot of the kind's top 5, or the line that
    /// none was charged. GameBots add their goal and activity, <c>-</c> and <c>""</c> without a
    /// <c>PersistentRecord</c>, as the slow-turn warning gives them (NS:81).
    /// </summary>
    public static IReadOnlyList<string> TopText(BotCpuWindow last, BotCpuKind kind)
    {
        ArgumentNullException.ThrowIfNull(last);
        IReadOnlyList<BotCpuTopEntry> top = last.Top(kind);
        if (top.Count == 0)
            return ["No " + KindName(kind) + " was charged in the last minute."];

        List<string> lines = [];
        foreach (BotCpuTopEntry entry in top)
        {
            BotCpuBot bot = entry.Bot;
            string line = Text(entry.Rank) + ". " + bot.Name + " (" + Text(bot.Level) + " " + bot.ClassName +
                ", region " + Text(bot.Region) + "): " + Text(entry.MsSec, 3) + " ms/s, " + Text(entry.CallsSec, 0) +
                " calls/s";
            if (kind == BotCpuKind.Gamebot)
                line += ", " + (string.IsNullOrEmpty(bot.Goal) ? "-" : bot.Goal) + ", \"" + bot.Activity + "\"";

            lines.Add(line);
        }

        return lines;
    }

    // The totals since on or reset (2.4): each kind with bot-seconds, in kind order; null with no window written or
    // no such kind.
    private static string SinceText(BotCpuSince since)
    {
        if (since == null || since.Windows == 0)
            return null;

        List<string> kinds = [];
        for (int k = 0; k < BotCpuTotals.KindCount; k++)
        {
            if (since.MsPerBotSec((BotCpuKind)k) is double msPerBotSec)
                kinds.Add(KindName((BotCpuKind)k) + " " + Text(msPerBotSec, 3));
        }

        return kinds.Count == 0
            ? null
            : (since.Reset ? "Since reset (" : "Since on (") + Text(Whole(since.Minutes)) + " min): " +
              string.Join(", ", kinds) + " ms/s per bot.";
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs (1 block(s))`.

Last, the helpers the lines and the chat share. `Text` writes every number in the invariant culture, and null, NaN
and the infinities as `-`, so no line shows `NaN` or `Infinity` (2.1). "Most in" is the largest stage value the line
shows, the first in tick order on a tie, and `-` when none shows.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs <<'EDIT'
<<<<<<< OLD
            : (since.Reset ? "Since reset (" : "Since on (") + Text(Whole(since.Minutes)) + " min): " +
              string.Join(", ", kinds) + " ms/s per bot.";
    }
}
=======
            : (since.Reset ? "Since reset (" : "Since on (") + Text(Whole(since.Minutes)) + " min): " +
              string.Join(", ", kinds) + " ms/s per bot.";
    }

    // A line's stage values as it shows them, in tick order (3.4), without those that round to 0.000.
    private static IEnumerable<(BotCpuStage Stage, string Value)> ShownStages(BotCpuKindWindow kind)
    {
        if (kind.StageValues == null)
            yield break;

        for (int stage = 0; stage < kind.StageValues.Count; stage++)
        {
            string value = Text(kind.StageValues[stage], 3);
            if (value is not ("0.000" or "-"))
                yield return ((BotCpuStage)stage, value);
        }
    }

    // A chat line's "most in": the largest stage value the line shows, the first in tick order on a tie; - with none.
    private static string MostIn(BotCpuKindWindow kind)
    {
        BotCpuStage? most = null;
        foreach ((BotCpuStage stage, string _) in ShownStages(kind))
        {
            if (most == null || kind.StageValues[(int)stage] > kind.StageValues[(int)most.Value])
                most = stage;
        }

        return most is BotCpuStage largest ? largest + " " + Text(kind.StageValues[(int)largest], 3) : "-";
    }

    // A value as the text writes it: fixed-point with that many decimals in the invariant culture; - for none, and
    // for NaN or an infinity, which the text never writes (2.1).
    private static string Text(double? value, int decimals) => value is double number && double.IsFinite(number)
        ? number.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        : "-";

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(long? value) => value is long number ? Text(number) : "-";

    // A CSV cell: the text's value, empty for - (2.3).
    private static string Cell(string text) => text == "-" ? string.Empty : text;

    // A compare ratio: 2 decimals and x, or - (2.1).
    private static string RatioText(double? ratio) => ratio is double value && double.IsFinite(value)
        ? Text(value, 2) + "x"
        : "-";

    // A chat percentage, rounded to a whole number, or - (2.4).
    private static string Percent(double? value) => value is double number && double.IsFinite(number)
        ? Text(number, 0) + "%"
        : "-";

    // A count and its unit, singular for 1: 1 minute, 30 minutes, 4 cores; - cores for none.
    private static string Count(long? count, string unit) =>
        Text(count) + " " + unit + (count == 1 ? string.Empty : "s");

    // Whole units of a span, rounded down; 0 for a negative span or one that is not a number.
    private static long Whole(double value) => double.IsFinite(value) && value > 0 ? (long)Math.Floor(value) : 0;

    // A time of day in chat: the 24-hour clock (2.4).
    private static string Clock(DateTime time) => time.ToString("HH':'mm", CultureInfo.InvariantCulture);

    // The GM of a BOT_CPU_STATE line; - for none.
    private static string Who(string by) => string.IsNullOrEmpty(by) ? "-" : by;
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (636
was seen).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuReport."`
Expected: `Passed!  - Failed:     0, Passed:    77, Skipped:     0, Total:    77` (Task 5's 50 cases and this task's 27; exit code 0).

- [ ] **Step 5: Check that the culture tests cannot pass by accident.** In globalization-invariant mode, which the
owner's `~/.bashrc` exports, `de-DE` does not exist, so each `[SetCulture("de-DE")]` test fails rather than passing
with a `.` it never had to choose.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuReport." 2>&1 | grep -E 'CultureNotFoundException|^(Passed|Failed)!' | uniq -c`
Expected (exit code 1 from `dotnet test`; the duration varies):

```
     19    System.Globalization.CultureNotFoundException : Only the invariant culture is supported in globalization-invariant mode. See https://aka.ms/GlobalizationInvariantMode for more information. (Parameter 'name')
      1 Failed!  - Failed:    19, Passed:    58, Skipped:     0, Total:    77, Duration: 99 ms - Tests.dll (net10.0)
```

The 19 `de-DE` tests fail, each with that exception, and the 58 others pass. The tests are meant to run with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0`, as CI's step does.

- [ ] **Step 6: Check the files' form.** The two files stay LF, with no BOM, no tab and no line over 120 characters:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs source/server/Tests/UnitTests/UT_BotCpuReport.cs <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs False 0 0 True 120
source/server/Tests/UnitTests/UT_BotCpuReport.cs False 0 0 True 120
```

- [ ] **Step 7: Run the wider checks.**

The meter's own class, which this task does not touch:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuMeter."`
Expected: `Passed!  - Failed:     0, Passed:   124, Skipped:     0, Total:   124`.

CI's fork filter:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   845, Skipped:     0, Total:   845` (818 after Task 5, and this task's 27).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3633, Skipped:     0, Total:  3633` (3606 after Task 5, and 27). Upstream's
`PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is locked" when the machine is
busy; rerun it alone with `--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and
expect `Passed:     1`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 10 tests` and `OK` (unchanged).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK` (unchanged: the class was already in CI's filter).

Run: `git status --short`
Expected: only these two lines (`serverconfig.xml` is ignored, so it does not show):

```
 M source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs
 M source/server/Tests/UnitTests/UT_BotCpuReport.cs
```

Run: `git diff --numstat`
Expected:

```
456	4	source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs
648	1	source/server/Tests/UnitTests/UT_BotCpuReport.cs
```

- [ ] **Step 8: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs source/server/Tests/UnitTests/UT_BotCpuReport.cs
git commit -F - <<'EOF'
feat(cpu-meter): the report's text

BotCpuReport now writes everything the meter shows, in the invariant culture (spec 2.1, 2.3, 2.4). Lines gives a
window's log lines in their order: a BOT_CPU line per kind with the fields and decimals of 2.1 (unattributed with
n=-, callsSec and allocKBSec; p50, p95 and max for the bot kinds only; - where there is nothing to divide by; the
stage values in tick order, those at 0.000 left out, and no | part when none is left), a BOT_CPU_TOP line per bot
kind with a bot charged (the GameBot goal or -), BOT_CPU_PROCESS (a value the platform can't give is -), then the
compare pairs. BotCpuProcess carries the process values of 3.9, null for -, and gives offLoopCpuPct as the
difference of the two values the line shows. The BOT_CPU_STATE and BOT_CPU_ERROR lines, the CSV's header and rows
(the process values on each row, an empty cell for -, the stage columns from the raw ticks over the exact window)
and every chat text of /botcpu (the status, the last minute, the totals since on or reset, each reply of 2.4's
table and the top lines) are made here too, so the tests pin them. UT_BotCpuReport checks them under de-DE against
2.1's and 2.4's samples rebuilt from raw counts, with the lines' stages in tick order where 2.1's sample differs,
and monster in the since-on line, as 2.4's rule gives (6.1).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `2 files changed, 1104 insertions(+), 5 deletions(-)`.

Review: approved with no code change, so this section is the tested commit as it was built. Its four Minor findings
changed no line and are left for the owner and later tasks: the singular "(1 core)" and `p50=- p95=- max=-` for a bot
kind with no spread are readings of 2.4 and 6.1 to confirm; `Lines` and `CsvRows` throw on the null window of a final
window under 5 s, so Task 9 must check for it; `offLoopCpuPct` can come out a little negative from read skew, and the
chat would then show `-0%`; and the Local-to-UTC branch of `CsvUtc`, a `th-TH` calendar case and `by=-` have no test.
Three spec issues stand: 2.1's sample lines are not in tick order (player `Timer` before `ClientBegin`, monster
`Effect` before `Movement`), 2.4's since-on sample leaves out monster though its rule includes it, and 2.1's "no part
after `|`" is read as no ` | ` at all.

### Task 7: The switch

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs` (the header comment, the class and the
  `[ServerProperty]` field; Tasks 8 and 9 add the rest)
- Create: `source/server/Tests/UnitTests/UT_BotCpuScript.cs` (one test; later tasks add theirs)
- Modify: `deploy/bin/server_properties.py:12-13` (the docstring's paragraph), `:41` (the constants go in after
  `RP_WAIT_DEFAULT`), `:72` (`bot_cpu_value` goes in after `bot_rp_timer_wait_value`), `:103` (the argument), `:108`
  (the check), `:117` (the write and its line go in after the realm point one's)
- Modify: `deploy/entrypoint.sh:27-31` (the comment and the `server_properties.py` call)
- Modify: `deploy/compose.yml:25` (the new line goes in after it)
- Modify: `deploy/.env.example:26` (the block goes in after it)
- Modify: `deploy/HANDOFF.md:92-93` (the settings paragraph), `:273` (the `./hdc update` sentence)
- Modify: `.github/workflows/server-image.yml:119` (CI's fork filter gains `UT_BotCpuScript`)
- Modify: `deploy/tests/test_bot_cpu.py:1` (the docstring's first line), `:7-11` (a docstring paragraph and the
  imports), `:15-18` (the deploy paths, the imports of `server_properties` and `test_server_properties`, and
  `SCRIPT_CS`), `:37-40` (`read` goes in before `source`), `:210-213` (four classes go in before `EndingsTests`)
- Modify: `deploy/tests/test_server_properties.py:103-124` and `:142-144` (every CLI call but the missing-argument
  test's gains `"--bot-cpu", "off"`; the two new-world outputs gain the meter's line)
- Modify: `deploy/tests/test_relic_pad.py:62-63`, `deploy/tests/test_rp_timer_wait.py:121`,
  `deploy/tests/test_si_start_choice.py:61`
- Modify: `deploy/tests/test_workflows.py:280` (`SERVER_UNIT_TESTS`' last line), `:311` (the expected names' last
  line)
- Test: `source/server/Tests/UnitTests/UT_BotCpuScript.cs`, `deploy/tests/test_bot_cpu.py`,
  `deploy/tests/test_server_properties.py`, `deploy/tests/test_relic_pad.py`, `deploy/tests/test_rp_timer_wait.py`,
  `deploy/tests/test_si_start_choice.py`, `deploy/tests/test_workflows.py`

**Spec:** 4.1 (the property only; the started and stopped handlers are Task 9's), 4.2 (the whole chain, `.env.example`
and `HANDOFF.md`), 6.1 `UT_BotCpuScript` (the switch), 6.3 (every item but the smoke test lines, which are Task 10's,
with the existing tests that change, `deploy/tests/test_relic_pad.py:62-63` among them), 6.4 (`UT_BotCpuScript` in
CI's filter and `test_workflows.py`).

**Interfaces:**
- Consumes:
  - `ServerPropertyAttribute` (`DOL.GS.ServerProperties`: `Category`, `Key`, `Description`, `DefaultValue`), read the
    way the server reads it (`ServerProperties.AllDomainProperties`: `GetFields()`, then the static ones).
  - In `deploy/bin/server_properties.py`: `SI_VALUES` (:34), `PropertyError` (:44), `set_property(db, key, value,
    category="server", description=OVERRIDES_DESCRIPTION, default="")` (:75, True when it changed the row), and the
    keys `OVERRIDES_KEY` (:22), `SI_KEY` (:30) and `RP_WAIT_KEY` (:38).
  - In `deploy/tests/test_server_properties.py`: `SCHEMA` (:16-19), `row(db, key)` (:28-32: `(Category, Description,
    DefaultValue, Value)` or None) and `run_cli(db, *args)` (:35-37: runs `server_properties.py --db db *args`).
  - In `deploy/tests/test_bot_cpu.py` (Tasks 3-4): `ROOT`, `GAME_SERVER`, `FORK`, `source(path)` (:40-43) and
    `between(text, start, end)` (:46-49).
- Produces:
  - In `namespace DOL.GS.HearthDAoC`, `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs`:
    `public static class BotCpuScript` with
    `[ServerProperty("server", "bot_cpu_meter", "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log every minute; a GM's /botcpu turns it on or off until the next start", false)] public static bool BOT_CPU_METER;`.
    Its only using is `DOL.GS.ServerProperties`. Tasks 8 and 9 add their members after the field; Task 9's started
    and stopped handlers read `BOT_CPU_METER`.
  - In `namespace DOL.GS.Tests`, `[TestFixture, NonParallelizable] public sealed class UT_BotCpuScript` with `[Test]
    public void TheSwitchIsAPublicStaticServerPropertyOffInCode()`; its usings are `System`, `System.Reflection`,
    `DOL.GS.HearthDAoC`, `DOL.GS.ServerProperties` and `NUnit.Framework`. Later tasks add their usings and tests.
  - In `deploy/bin/server_properties.py`: `CPU_KEY = "bot_cpu_meter"`, `CPU_DESCRIPTION` (the same description),
    `CPU_DEFAULT = "False"`, `def bot_cpu_value(spec)` (`'on'` -> `'True'`, `'off'` -> `'False'`, any case; anything
    else raises `PropertyError("HEARTHDAOC_BOT_CPU must be on or off, got {spec!r}")`), and the required argument
    `--bot-cpu`, declared last, checked before any write, printing `Bot CPU meter: on` (or `off`) when it changes the
    row.
  - `HEARTHDAOC_BOT_CPU`, default `off`, in `deploy/compose.yml`, `deploy/entrypoint.sh` and `deploy/.env.example`.
  - In `deploy/tests/test_bot_cpu.py`, for later tasks: `DEPLOY`, `BIN` (on `sys.path`), `sp` (`server_properties`),
    `SCHEMA`, `row`, `run_cli`, `SCRIPT_CS` and `read(name)` (a file under `deploy/`, UTF-8); the classes
    `SwitchValueTests`, `EntrypointTests` (`setUp`, `tearDown`, `run_entrypoint_line(**env)`), `SettingDocsTests`
    (`ENV_EXAMPLE`) and `ServerDeclarationTests`. `HookTests`, `UpstreamFactTests`, `EndingsTests` and the helpers
    `source`, `between`, `marked`, `type_body` and `upstream_lines` do not change.

- [ ] **Step 1: Write the failing tests.** Create `source/server/Tests/UnitTests/UT_BotCpuScript.cs` with exactly the
code below (LF, no BOM: the Write tool, or a quoted heredoc). The test reads the switch back the way the server finds
it: `ServerProperties.AllDomainProperties` reads `GetFields()`, so only public fields, then keeps the static ones. The
fixture is `NonParallelizable` from the start: the tests later tasks add to it touch the meter's shared state
(`Enabled`, the queueing hook). The test assembly sets no `Parallelizable`, so it changes nothing today.

```csharp
using System;
using System.Reflection;
using DOL.GS.HearthDAoC;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter's switch (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 4.1, 6.1).
// BotCpuScript holds the bot_cpu_meter server property, which HEARTHDAOC_BOT_CPU sets at every start (4.2) and the
// server reads once, at start, to turn the meter on with it. Off in code, as the fork's other switches are.
[TestFixture, NonParallelizable]
public sealed class UT_BotCpuScript
{
    [Test]
    public void TheSwitchIsAPublicStaticServerPropertyOffInCode()
    {
        // The server finds a property only on a public static field (ServerProperties.AllDomainProperties reads
        // GetFields(), then IsStatic). HearthDAoC turns it on through HEARTHDAOC_BOT_CPU; the description must equal
        // deploy/bin/server_properties.py's CPU_DESCRIPTION (deploy/tests/test_bot_cpu.py).
        FieldInfo field = Array.Find(typeof(BotCpuScript).GetFields(), f => f.Name == "BOT_CPU_METER");
        Assert.That(field, Is.Not.Null);
        Assert.That(field.IsStatic, Is.True);
        Assert.That(field.FieldType, Is.EqualTo(typeof(bool)));
        ServerPropertyAttribute property = field.GetCustomAttribute<ServerPropertyAttribute>();
        Assert.That(property, Is.Not.Null);
        Assert.That(property.Category, Is.EqualTo("server"));
        Assert.That(property.Key, Is.EqualTo("bot_cpu_meter"));
        Assert.That(property.DefaultValue, Is.TypeOf<bool>().And.EqualTo(false));
        Assert.That(property.Description, Is.EqualTo(
            "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log " +
            "every minute; a GM's /botcpu turns it on or off until the next start"));
    }
}
```

Then the deploy tests of 6.3 in `deploy/tests/test_bot_cpu.py`, in five blocks. The first three widen the docstring,
add the imports the entrypoint run needs, and put `deploy/bin` on `sys.path`, so the file imports `server_properties`
and reuses `test_server_properties.py`'s `SCHEMA`, `row` and `run_cli`. `SCRIPT_CS` is the new C# file. The fourth adds
`read`, as `test_rp_timer_wait.py` has it. The fifth adds four classes before `EndingsTests`, copied from
`test_rp_timer_wait.py:76-161` with the meter's names:
  - `SwitchValueTests`: the value check (6 values in any case, 11 refused, each with the exact message), the key and
    default, a description with no `"` or `\` (so the C# string and the Python one can be compared as text), and the
    argument itself. A run without `--bot-cpu` stops with exit 2 and writes nothing, and a run without the last three
    arguments names them in declaration order, which pins `--bot-cpu` as declared last (so
    `test_server_properties.py`'s missing-argument test still finds `--si-start-choice` first).
  - `EntrypointTests` runs the entrypoint's own `server_properties.py` command in bash against a scratch world (the
    continuation lines joined, `HEARTHDAOC_*` cleared from the environment). With nothing set, a new world gets
    `False` and the line `Bot CPU meter: off` once; a second start prints nothing. `ON` writes `True`, and the next
    default start says `off` again (4.3: the next start decides again). `maybe` and `""` stop the start with exit 2
    and the exact message, and no row at all is written, since every value is checked before any write.
  - `SettingDocsTests`: compose's line right after the realm point one; `.env.example`'s block word for word as 4.2
    gives it, right after `HEARTHDAOC_BOT_RP_TIMER_WAIT=on`; and in `HANDOFF.md`, the setting inside the settings
    paragraph and `HEARTHDAOC_BOT_CPU=off` inside the `./hdc update` paragraph.
  - `ServerDeclarationTests`: the C# attribute, followed by `public static bool BOT_CPU_METER;`, has the key, the
    category `server`, the default `false` and `CPU_DESCRIPTION` itself.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_bot_cpu.py <<'EDIT'
<<<<<<< OLD
"""The bot CPU meter (spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.2).
=======
"""The bot CPU meter (spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.2, 6.3).
>>>>>>> NEW
<<<<<<< OLD
files' line endings need the source, so these check it.
"""
import os
import re
import unittest
=======
files' line endings need the source, so these check it.

The switch goes .env -> compose.yml -> entrypoint.sh -> server_properties.py -> the bot_cpu_meter server property,
default off at every step (hdc update appends the new .env.example line to an existing .env); the server reads it once,
at start (4.1, 4.2).
"""
import os
import re
import shlex
import sqlite3
import subprocess
import sys
import tempfile
import unittest
>>>>>>> NEW
<<<<<<< OLD
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
FORK = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
=======
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEPLOY = os.path.join(ROOT, "deploy")
BIN = os.path.join(DEPLOY, "bin")
sys.path.insert(0, BIN)

import server_properties as sp  # noqa: E402
from tests.test_server_properties import SCHEMA, row, run_cli  # noqa: E402

GAME_SERVER = os.path.join(ROOT, "source", "server", "GameServer")
FORK = os.path.join(GAME_SERVER, "scripts", "hearthdaoc")
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
SCRIPT_CS = os.path.join(FORK, "BotCpuScript.cs")
>>>>>>> NEW
<<<<<<< OLD
                  "AutonomousWorldBotController.PrepareCampPlanningTick();"]


def source(path):
=======
                  "AutonomousWorldBotController.PrepareCampPlanningTick();"]


def read(name):
    """A file of deploy/."""
    with open(os.path.join(DEPLOY, name), encoding="utf-8") as f:
        return f.read()


def source(path):
>>>>>>> NEW
<<<<<<< OLD
                self.assertIn(member, type_body(source(os.path.join(GAME_SERVER, *path.split("/"))), header))


class EndingsTests(unittest.TestCase):
=======
                self.assertIn(member, type_body(source(os.path.join(GAME_SERVER, *path.split("/"))), header))


class SwitchValueTests(unittest.TestCase):
    def test_on_and_off_in_any_case_become_true_and_false(self):
        for spec, expected in (("on", "True"), ("ON", "True"), ("On", "True"),
                               ("off", "False"), ("OFF", "False"), ("oFf", "False")):
            with self.subTest(spec=spec):
                self.assertEqual(sp.bot_cpu_value(spec), expected)

    def test_other_values_are_refused(self):
        for bad in ("", "yes", "no", "true", "false", "1", "0", "none", "onn", " on", "off "):
            with self.subTest(bad=bad), self.assertRaisesRegex(
                    sp.PropertyError, f"^HEARTHDAOC_BOT_CPU must be on or off, got {re.escape(repr(bad))}$"):
                sp.bot_cpu_value(bad)

    def test_the_property_matches_the_server_declaration(self):
        # [ServerProperty("server", "bot_cpu_meter", <description>, false)]: off in code, "False" in the table (4.1).
        self.assertEqual((sp.CPU_KEY, sp.CPU_DEFAULT), ("bot_cpu_meter", "False"))
        self.assertNotIn('"', sp.CPU_DESCRIPTION)
        self.assertNotIn("\\", sp.CPU_DESCRIPTION)

    def test_the_setting_is_required_and_declared_last(self):
        # A start without it stops before any write; argparse names the missing arguments in declaration order, so
        # test_server_properties.py's missing-argument test still finds --si-start-choice first (6.3).
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "world.db")
            with sqlite3.connect(db) as c:
                c.execute(SCHEMA)
            r = run_cli(db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on")
            self.assertEqual((r.returncode, r.stdout), (2, ""))
            self.assertIn("the following arguments are required: --bot-cpu\n", r.stderr)
            self.assertIsNone(row(db, sp.OVERRIDES_KEY))
            self.assertIsNone(row(db, sp.CPU_KEY))
            r = run_cli(db, "--gm-only-commands", "/tele;/tc")
            self.assertIn("the following arguments are required: --si-start-choice, --bot-rp-timer-wait, --bot-cpu\n",
                          r.stderr)


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

    def test_passes_the_setting_with_default_off(self):
        self.assertIn('--bot-cpu "${HEARTHDAOC_BOT_CPU-off}"', self.command)
        r = self.run_entrypoint_line()
        # A new world has no row, so its first start writes False and says so, once (4.2).
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\n"
                             "Bot realm point timer wait: on\nBot CPU meter: off\n", ""))
        self.assertEqual(row(self.db, sp.CPU_KEY), ("server", sp.CPU_DESCRIPTION, "False", "False"))
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "", ""))

    def test_on_in_any_case_turns_it_on_until_the_next_start_says_off(self):
        r = self.run_entrypoint_line(HEARTHDAOC_BOT_CPU="ON")
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\n"
                             "Bot realm point timer wait: on\nBot CPU meter: on\n", ""))
        self.assertEqual(row(self.db, sp.CPU_KEY), ("server", sp.CPU_DESCRIPTION, "False", "True"))
        r = self.run_entrypoint_line()
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "Bot CPU meter: off\n", ""))
        self.assertEqual(row(self.db, sp.CPU_KEY)[3], "False")

    def test_a_bad_value_stops_the_start(self):
        for bad in ("maybe", ""):
            with self.subTest(bad=bad):
                r = self.run_entrypoint_line(HEARTHDAOC_BOT_CPU=bad)
                self.assertEqual((r.returncode, r.stdout), (2, ""))
                self.assertEqual(r.stderr, f"ERROR: HEARTHDAOC_BOT_CPU must be on or off, got {bad!r}\n")
                # Checked before any write: nothing at all.
                for key in (sp.CPU_KEY, sp.OVERRIDES_KEY, sp.SI_KEY, sp.RP_WAIT_KEY):
                    self.assertIsNone(row(self.db, key), key)


class SettingDocsTests(unittest.TestCase):
    def test_compose_passes_the_setting_with_default_off(self):
        environment = read("compose.yml").split("\n    environment:\n", 1)[1].split("\n    volumes:\n", 1)[0]
        self.assertIn("\n      HEARTHDAOC_BOT_RP_TIMER_WAIT: ${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}\n"
                      "      HEARTHDAOC_BOT_CPU: ${HEARTHDAOC_BOT_CPU-off}\n", environment + "\n")

    # The setting in .env.example, under its comment, after the realm point waits' line (spec 4.2).
    ENV_EXAMPLE = """\
HEARTHDAOC_BOT_RP_TIMER_WAIT=on
# on = the server measures the game loop's CPU per kind of bot and player and writes BOT_CPU lines to the server log
# every minute, and rows to /data/logs/bot-cpu.csv; it costs about 1-5% of one core while on. off = nothing is
# measured. A GM can also turn it on or off in game with /botcpu, until the next start. Case is ignored.
HEARTHDAOC_BOT_CPU=off
"""

    def test_env_example_documents_the_setting_default_off(self):
        example = read(".env.example")
        self.assertEqual([line for line in example.splitlines() if line.startswith("HEARTHDAOC_BOT_CPU")],
                         ["HEARTHDAOC_BOT_CPU=off"])
        self.assertIn("\n" + self.ENV_EXAMPLE, example)

    def test_handoff_lists_the_setting(self):
        handoff = read("HANDOFF.md")
        settings = between(handoff, "Settings live in `.env`", "`./hdc up` recreates the server with them.")
        self.assertIn("`HEARTHDAOC_BOT_CPU` (default `off`", settings)
        update = between(handoff, "`./hdc update <tag>` installs a specific release.", "It always backs up the world")
        self.assertIn("`HEARTHDAOC_BOT_CPU=off`", update)  # what ./hdc update adds to an existing .env


class ServerDeclarationTests(unittest.TestCase):
    def test_server_declares_the_property_with_the_same_description(self):
        found = re.findall(r'\[ServerProperty\(\s*"([^"]*)",\s*"bot_cpu_meter",\s*"([^"]*)",\s*(\w+)\s*\)\]\s*'
                           r'public static bool BOT_CPU_METER;', source(SCRIPT_CS))
        self.assertEqual(found, [("server", sp.CPU_DESCRIPTION, "false")])


class EndingsTests(unittest.TestCase):
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_bot_cpu.py (5 block(s))`.

Then the existing deploy tests follow the new required argument (6.3). In `test_server_properties.py`, every CLI call
gains `"--bot-cpu", "off"`, `off` as written even where the call's other values are `OFF`, except
`test_cli_needs_the_si_start_choice` (:130-133), whose `assertIn` still finds `--si-start-choice` first. The two runs
on a world with no `bot_cpu_meter` row (:112 and :143) expect its line; the second runs on the same world (:115, :121)
and the bad-value test print nothing new, so their expected output stays:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_server_properties.py <<'EDIT'
<<<<<<< OLD
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        self.assertEqual(run_cli(self.db, "--gm-only-commands", "tele", "--si-start-choice", "on",
                                 "--bot-rp-timer-wait", "on").returncode, 2)
=======
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on",
                    "--bot-cpu", "off")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(value(self.db, sp.OVERRIDES_KEY), "/tele=2;/tc=2")
        self.assertEqual(run_cli(self.db, "--gm-only-commands", "tele", "--si-start-choice", "on",
                                 "--bot-rp-timer-wait", "on", "--bot-cpu", "off").returncode, 2)
>>>>>>> NEW
<<<<<<< OLD
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "ON", "--bot-rp-timer-wait", "on")
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: none\nShrouded Isles start choice: on\nBot realm point timer wait: on\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "Off", "--bot-rp-timer-wait", "on")
=======
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "ON", "--bot-rp-timer-wait", "on",
                    "--bot-cpu", "off")
        self.assertEqual((r.returncode, r.stdout, r.stderr),
                         (0, "GM-only commands: none\nShrouded Isles start choice: on\nBot realm point timer wait: on\n"
                             "Bot CPU meter: off\n", ""))
        self.assertEqual(row(self.db, sp.SI_KEY), ("server", sp.SI_DESCRIPTION, "False", "True"))
        r = run_cli(self.db, "--gm-only-commands", "none", "--si-start-choice", "Off", "--bot-rp-timer-wait", "on",
                    "--bot-cpu", "off")
>>>>>>> NEW
<<<<<<< OLD
        run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "off", "--bot-rp-timer-wait", "off")
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "OFF", "--bot-rp-timer-wait", "OFF")
=======
        run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "off", "--bot-rp-timer-wait", "off",
                "--bot-cpu", "off")
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "OFF", "--bot-rp-timer-wait", "OFF",
                    "--bot-cpu", "off")
>>>>>>> NEW
<<<<<<< OLD
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "yes", "--bot-rp-timer-wait", "on")
=======
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "yes", "--bot-rp-timer-wait", "on",
                    "--bot-cpu", "off")
>>>>>>> NEW
<<<<<<< OLD
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on")
        self.assertEqual((r.returncode, r.stdout), (0, "Shrouded Isles start choice: on\nBot realm point timer wait: on\n"),
                         r.stderr)
=======
        r = run_cli(self.db, "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on",
                    "--bot-cpu", "off")
        self.assertEqual((r.returncode, r.stdout),
                         (0, "Shrouded Isles start choice: on\nBot realm point timer wait: on\nBot CPU meter: off\n"),
                         r.stderr)
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_server_properties.py (5 block(s))`.

`test_relic_pad.py`'s start (the entrypoint's world steps, which CI runs with `HDC_TEST_WORLD`) passes the setting
too; without it argparse exits 2 and :65 fails:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_relic_pad.py <<'EDIT'
<<<<<<< OLD
                            "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on"],
=======
                            "--gm-only-commands", "/tele;/tc", "--si-start-choice", "on", "--bot-rp-timer-wait", "on",
                            "--bot-cpu", "off"],
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_relic_pad.py (1 block(s))`.

The entrypoint runs of `test_rp_timer_wait.py` and `test_si_start_choice.py` on an empty world print the new line too
(their second runs and bad-value runs stay as they are):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_rp_timer_wait.py <<'EDIT'
<<<<<<< OLD
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\nBot realm point timer wait: on\n", ""))
=======
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\nBot realm point timer wait: on\n"
                             "Bot CPU meter: off\n", ""))
>>>>>>> NEW
EDIT
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_si_start_choice.py <<'EDIT'
<<<<<<< OLD
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\nBot realm point timer wait: on\n", ""))
=======
                         (0, "GM-only commands: /tele;/tc\nShrouded Isles start choice: on\nBot realm point timer wait: on\n"
                             "Bot CPU meter: off\n", ""))
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_rp_timer_wait.py (1 block(s))` and `edited deploy/tests/test_si_start_choice.py
(1 block(s))`.

Last, pin the new class in the workflow check: its copy of CI's filter string, and its list of names (each must be a
file holding `public sealed class UT_X`, which the new test file is). The three meter names share one line, one line
per feature as the realm point waits' pair. CI's own filter follows in Step 3.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_workflows.py <<'EDIT'
<<<<<<< OLD
                     '|FullyQualifiedName~UT_BotCpuMeter|FullyQualifiedName~UT_BotCpuReport"')
=======
                     '|FullyQualifiedName~UT_BotCpuMeter|FullyQualifiedName~UT_BotCpuReport|FullyQualifiedName~UT_BotCpuScript"')
>>>>>>> NEW
<<<<<<< OLD
                                 "UT_BotCpuMeter", "UT_BotCpuReport"])
=======
                                 "UT_BotCpuMeter", "UT_BotCpuReport", "UT_BotCpuScript"])
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_workflows.py (2 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 1 error, `CS0246` at `typeof(BotCpuScript)` in the test. The error line
starts with the file's absolute path and ends with the project's:

```
source/server/Tests/UnitTests/UT_BotCpuScript.cs(21,45): error CS0246: The type or namespace name 'BotCpuScript' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The summary (the warning total varies between builds; 849 was seen):

```
Build FAILED.
    849 Warning(s)
    1 Error(s)
```

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: the progress line `..FFFF...EFFFEEEEEEEEEEEEEEEEEEF.....`, then `Ran 21 tests` and `FAILED (failures=8,
errors=19)`. The errors, counted per subtest:
  - 17 `AttributeError: module 'server_properties' has no attribute 'bot_cpu_value'` (`SwitchValueTests`' two value
    tests, 6 + 11 subtests);
  - 1 `AttributeError: module 'server_properties' has no attribute 'CPU_KEY'`
    (`test_the_property_matches_the_server_declaration`);
  - 1 `FileNotFoundError` naming `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs`
    (`test_server_declares_the_property_with_the_same_description`).

The failures:
  - `test_the_setting_is_required_and_declared_last`: `AssertionError: Tuples differ: (0, '') != (2, '')`, since the
    CLI does not ask for `--bot-cpu` yet;
  - `EntrypointTests`, 4 failures: `test_passes_the_setting_with_default_off` (`'--bot-cpu
    "${HEARTHDAOC_BOT_CPU-off}"' not found in` the entrypoint's command), the `ON` test (`Tuples differ`: the output
    has no `Bot CPU meter: on` line), and `test_a_bad_value_stops_the_start` once per subtest, `maybe` and `''`, with
    `AssertionError: Tuples differ: (0, 'GM-only commands: /tele;/tc\nShrouded[54 chars]n\n') != (2, '')`, since
    nothing reads the variable yet;
  - `SettingDocsTests`' three: compose's two lines not found in its environment, `.env.example`'s `AssertionError:
    Lists differ: [] != ['HEARTHDAOC_BOT_CPU=off']`, and HANDOFF's ``'`HEARTHDAOC_BOT_CPU` (default `off`' not
    found``.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_server_properties.py`
Expected: the progress line `.......F.FFF..s`, then `FAIL:` for `test_cli`, `test_cli_prints_nothing_when_nothing_changes`,
`test_cli_refuses_a_bad_si_start_choice_and_writes_nothing` and `test_cli_writes_the_si_start_choice_as_true_or_false`,
each because the CLI stops with exit code 2 and `server_properties.py: error: unrecognized arguments: --bot-cpu off`,
then `Ran 15 tests` and `FAILED (failures=4, skipped=1)` (`test_shipped_world` skips without `HDC_TEST_WORLD`).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_rp_timer_wait.py`
Expected: `FAIL: test_passes_the_setting_with_default_on (tests.test_rp_timer_wait.EntrypointTests.test_passes_the_setting_with_default_on)`
with `AssertionError: Tuples differ: (0, '[57 chars] on\nBot realm point timer wait: on\n', '') != (0, '[57
chars] on\nBot realm point timer wait: on\nBot CPU meter: off\n', '')`, then `Ran 30 tests` and `FAILED
(failures=1)`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_si_start_choice.py`
Expected: `FAIL: test_passes_the_setting_with_default_on (tests.test_si_start_choice.EntrypointTests.test_passes_the_setting_with_default_on)`
with the same `Tuples differ` line, then `Ran 8 tests` and `FAILED (failures=1, skipped=1)`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: the progress line `.................F`, then `FAIL:
test_the_fork_server_unit_tests_run_with_the_pinned_filter` with an `AssertionError` that the new filter string (ending
`|FullyQualifiedName~UT_BotCpuReport|FullyQualifiedName~UT_BotCpuScript"`) is not found in CI's step `name: Server
unit tests for the fork's server changes`, then `Ran 18 tests` and `FAILED (failures=1)`.
`test_every_name_in_the_filter_is_a_test_class` passes already, since `UT_BotCpuScript.cs` exists.

The relic pad's start and the shipped world's run need the test world (without it, `test_relic_pad.py` gives `Ran 3
tests` and `OK (skipped=3)`). `t` runs one file with the world variables:

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && t test_relic_pad`
Expected: the progress line `F..`, then `FAIL: test_a_start_leaves_it_at_eight
(tests.test_relic_pad.RelicPadWorldTests.test_a_start_leaves_it_at_eight)` with `AssertionError: 2 != 0 : usage:
server_properties.py [-h] --db DB --gm-only-commands GM_ONLY_COMMANDS` and `server_properties.py: error: unrecognized
arguments: --bot-cpu off`, then `Ran 3 tests` and `FAILED (failures=1)`.

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && t test_server_properties`
Expected: the progress line `.......F.FFF..F`: the four failures above and `test_shipped_world`, with `AssertionError:
Tuples differ: (2, '') != (0, 'Shrouded Isles start choice: on\nBot [45 chars]f\n')`, then `Ran 15 tests` and
`FAILED (failures=5)`.

- [ ] **Step 3: Implement.** Create `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs` with exactly the
code below (LF, no BOM). It holds the switch only. A world with no `bot_cpu_meter` row gets one from the attribute at
the server's start, with default and value `False` (off); `server_properties.py` gives a row it creates the same
default (`CPU_DEFAULT`). The header comment says the class's whole role (4.1-4.3), so Tasks 8 and 9 only add members:

```csharp
using DOL.GS.ServerProperties;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's switch and server side (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md,
// section 4). The bot_cpu_meter server property, off in code, is set from HEARTHDAOC_BOT_CPU at every start (4.2) and
// read once, at start: on, the meter starts with the server (4.1). A GM's /botcpu turns the meter on or off until the
// next start and never writes the property (4.3). BotCpuMeter is the hot path; BotCpuReport makes the numbers and the
// text.
public static class BotCpuScript
{
    [ServerProperty("server", "bot_cpu_meter",
        "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log every minute; a GM's /botcpu turns it on or off until the next start",
        false)]
    public static bool BOT_CPU_METER;
}
```

`server_properties.py` gets the docstring's paragraph, the declaration's constants (after the realm point ones, under
a comment naming the C# file that must match), the value check (it reuses `SI_VALUES`, as `bot_rp_timer_wait_value`
does), the required argument after `--bot-rp-timer-wait`, the check with the others before any write, and the write
with its line, after the realm point one:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/bin/server_properties.py <<'EDIT'
<<<<<<< OLD
just as it is worth realm points again.
Only writes a property when its value differs, and prints a line for each one it changed.
=======
just as it is worth realm points again.
HEARTHDAOC_BOT_CPU (on or off, case ignored) becomes the bot_cpu_meter property (True or False): the bot CPU
meter then starts with the server, measures the game loop's CPU time per kind of bot and player and writes
BOT_CPU lines to the server log every minute. The server reads it once, at start; a GM's /botcpu turns the
meter on or off until the next start.
Only writes a property when its value differs, and prints a line for each one it changed.
>>>>>>> NEW
<<<<<<< OLD
RP_WAIT_DEFAULT = "False"  # the code default (off), as the server itself writes it
=======
RP_WAIT_DEFAULT = "False"  # the code default (off), as the server itself writes it

# Must match the server's [ServerProperty("server", "bot_cpu_meter", CPU_DESCRIPTION, false)] in
# source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (deploy/tests/test_bot_cpu.py checks).
CPU_KEY = "bot_cpu_meter"
CPU_DESCRIPTION = ("Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server "
                   "log every minute; a GM's /botcpu turns it on or off until the next start")
CPU_DEFAULT = "False"  # the code default (off), as the server itself writes it
>>>>>>> NEW
<<<<<<< OLD
        raise PropertyError(f"HEARTHDAOC_BOT_RP_TIMER_WAIT must be on or off, got {spec!r}") from None
=======
        raise PropertyError(f"HEARTHDAOC_BOT_RP_TIMER_WAIT must be on or off, got {spec!r}") from None


def bot_cpu_value(spec):
    """'on' -> 'True', 'off' -> 'False', in any case; anything else is refused."""
    try:
        return SI_VALUES[spec.lower()]
    except KeyError:
        raise PropertyError(f"HEARTHDAOC_BOT_CPU must be on or off, got {spec!r}") from None
>>>>>>> NEW
<<<<<<< OLD
    ap.add_argument("--bot-rp-timer-wait", required=True, help="on or off (case ignored)")
=======
    ap.add_argument("--bot-rp-timer-wait", required=True, help="on or off (case ignored)")
    ap.add_argument("--bot-cpu", required=True, help="on or off (case ignored)")
>>>>>>> NEW
<<<<<<< OLD
        rp_wait = bot_rp_timer_wait_value(a.bot_rp_timer_wait)
=======
        rp_wait = bot_rp_timer_wait_value(a.bot_rp_timer_wait)
        cpu = bot_cpu_value(a.bot_cpu)
>>>>>>> NEW
<<<<<<< OLD
        print(f"Bot realm point timer wait: {a.bot_rp_timer_wait.lower()}")
=======
        print(f"Bot realm point timer wait: {a.bot_rp_timer_wait.lower()}")
    if set_property(a.db, CPU_KEY, cpu, description=CPU_DESCRIPTION, default=CPU_DEFAULT):
        print(f"Bot CPU meter: {a.bot_cpu.lower()}")
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/bin/server_properties.py (6 block(s))`.

The entrypoint passes it on a new continuation line, default off (`EntrypointTests` join continuation lines before they
look for the command), and the comment above names `bot_cpu_meter`:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/entrypoint.sh <<'EDIT'
<<<<<<< OLD
# Single-player shortcuts that need GM rights on a shared server (command_plvl_overrides), the Shrouded
# Isles start choice for new characters (si_start_choice), and released gamebots waiting until they are worth
# realm points again (bot_rp_timer_wait). A bad value stops the start (exit 2).
python3 "$BIN/server_properties.py" --db "$DATA/world/opendaoc.sqlite3.db" --gm-only-commands "${HEARTHDAOC_GM_ONLY_COMMANDS-/tele;/tc}" \
    --si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}" --bot-rp-timer-wait "${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}"
=======
# Single-player shortcuts that need GM rights on a shared server (command_plvl_overrides), the Shrouded
# Isles start choice for new characters (si_start_choice), released gamebots waiting until they are worth
# realm points again (bot_rp_timer_wait), and the bot CPU meter, on from the start or not (bot_cpu_meter).
# A bad value stops the start (exit 2).
python3 "$BIN/server_properties.py" --db "$DATA/world/opendaoc.sqlite3.db" --gm-only-commands "${HEARTHDAOC_GM_ONLY_COMMANDS-/tele;/tc}" \
    --si-start-choice "${HEARTHDAOC_SI_START_CHOICE-on}" --bot-rp-timer-wait "${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}" \
    --bot-cpu "${HEARTHDAOC_BOT_CPU-off}"
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/entrypoint.sh (1 block(s))`.

Compose passes only the variables it lists (`deploy/compose.yml:13-27`), so the setting gets its line, default off:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/compose.yml <<'EDIT'
<<<<<<< OLD
      HEARTHDAOC_BOT_RP_TIMER_WAIT: ${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}
=======
      HEARTHDAOC_BOT_RP_TIMER_WAIT: ${HEARTHDAOC_BOT_RP_TIMER_WAIT-on}
      HEARTHDAOC_BOT_CPU: ${HEARTHDAOC_BOT_CPU-off}
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/compose.yml (1 block(s))`.

`./hdc update` appends every `KEY=` line of `.env.example` that an existing `.env` lacks (`deploy/hdc:128-139`), so
servers set up before get it off. The block is 4.2's, word for word:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/.env.example <<'EDIT'
<<<<<<< OLD
HEARTHDAOC_BOT_RP_TIMER_WAIT=on
=======
HEARTHDAOC_BOT_RP_TIMER_WAIT=on
# on = the server measures the game loop's CPU per kind of bot and player and writes BOT_CPU lines to the server log
# every minute, and rows to /data/logs/bot-cpu.csv; it costs about 1-5% of one core while on. off = nothing is
# measured. A GM can also turn it on or off in game with /botcpu, until the next start. Case is ignored.
HEARTHDAOC_BOT_CPU=off
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/.env.example (1 block(s))`.

`HANDOFF.md`'s settings paragraph and its `./hdc update` sentence. No line that names `HEARTHDAOC_BOT_CPU` mentions
docker or `server.log`, so Task 11's per-line guide check does not take these lines for a read command:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/HANDOFF.md <<'EDIT'
<<<<<<< OLD
at a safe spot and comes back just as it is worth realm points again; `off` sends it straight back); after
editing `.env`, `./hdc up` recreates the server with them.
=======
at a safe spot and comes back just as it is worth realm points again; `off` sends it straight back) and
`HEARTHDAOC_BOT_CPU` (default `off`: `on` starts the bot CPU meter with the server, which every minute writes
the game loop's CPU per kind of bot and player to the server log; a GM's `/botcpu` turns it on or off in game
until the next start); after editing `.env`, `./hdc up` recreates the server with them.
>>>>>>> NEW
<<<<<<< OLD
`HEARTHDAOC_SI_START_CHOICE=on`, and the one with the realm point waits adds `HEARTHDAOC_BOT_RP_TIMER_WAIT=on`).
=======
`HEARTHDAOC_SI_START_CHOICE=on`, the one with the realm point waits adds `HEARTHDAOC_BOT_RP_TIMER_WAIT=on`, and
the one with the bot CPU meter adds `HEARTHDAOC_BOT_CPU=off`).
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/HANDOFF.md (2 block(s))`.

CI runs the new class (the filter's `~UT_BotCpuMeter` and `~UT_BotCpuReport` do not match `UT_BotCpuScript`, so it
needs its own name):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py .github/workflows/server-image.yml <<'EDIT'
<<<<<<< OLD
|FullyQualifiedName~UT_BotCpuReport"
=======
|FullyQualifiedName~UT_BotCpuReport|FullyQualifiedName~UT_BotCpuScript"
>>>>>>> NEW
EDIT
```

Expected: `edited .github/workflows/server-image.yml (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (636 was
seen).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuScript."`
Expected: `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1` (exit code 0).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 21 tests` and `OK` (Task 4's 10 and this task's 11).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_server_properties.py`
Expected: `Ran 15 tests` and `OK (skipped=1)`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_rp_timer_wait.py`
Expected: `Ran 30 tests` and `OK`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_si_start_choice.py`
Expected: `Ran 8 tests` and `OK (skipped=1)`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

With the test world, the three files whose world tests run `server_properties.py`:

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && t test_relic_pad; t test_server_properties; t test_si_start_choice`
Expected: `Ran 3 tests` and `OK`, then `Ran 15 tests` and `OK`, then `Ran 8 tests` and `OK`.

- [ ] **Step 5: Check the files' form.** The two new files and `test_bot_cpu.py` are LF, with no BOM and no tab. The
description stays one string literal on one line (181 characters), as in `RpTimerWaitScript.cs`:
`ServerDeclarationTests` reads it as one literal. Every other line is 120 characters at most:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs deploy/tests/test_bot_cpu.py <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs False 0 0 True 181
source/server/Tests/UnitTests/UT_BotCpuScript.cs False 0 0 True 118
deploy/tests/test_bot_cpu.py False 0 0 True 120
```

The edited deploy files were LF and stay so, with no trailing space:

Run: `git diff --check`
Expected: no output (exit code 0).

- [ ] **Step 6: Run the wider checks.**

CI's fork filter, now with `UT_BotCpuScript`:

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   846, Skipped:     0, Total:   846` (845 after Task 6, and this task's 1).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3634, Skipped:     0, Total:  3634` (3633 after Task 6, and 1; about 40
seconds of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

The whole deploy suite with the world tests (about three minutes; run it after the unit tests, not at the same time):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 765 tests` and `OK (skipped=10)` (754 after Task 6, and `test_bot_cpu.py`'s 11 new tests).

Run: `git status --short`
Expected: only these lines (`serverconfig.xml` is ignored, so it does not show):

```
 M .github/workflows/server-image.yml
 M deploy/.env.example
 M deploy/HANDOFF.md
 M deploy/bin/server_properties.py
 M deploy/compose.yml
 M deploy/entrypoint.sh
 M deploy/tests/test_bot_cpu.py
 M deploy/tests/test_relic_pad.py
 M deploy/tests/test_rp_timer_wait.py
 M deploy/tests/test_server_properties.py
 M deploy/tests/test_si_start_choice.py
 M deploy/tests/test_workflows.py
?? source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs
?? source/server/Tests/UnitTests/UT_BotCpuScript.cs
```

Run: `git diff --numstat`
Expected (the new files are not tracked yet):

```
1	1	.github/workflows/server-image.yml
4	0	deploy/.env.example
6	3	deploy/HANDOFF.md
23	0	deploy/bin/server_properties.py
1	0	deploy/compose.yml
5	3	deploy/entrypoint.sh
150	1	deploy/tests/test_bot_cpu.py
2	1	deploy/tests/test_relic_pad.py
2	1	deploy/tests/test_rp_timer_wait.py
19	10	deploy/tests/test_server_properties.py
2	1	deploy/tests/test_si_start_choice.py
2	2	deploy/tests/test_workflows.py
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs deploy/bin/server_properties.py deploy/entrypoint.sh deploy/compose.yml deploy/.env.example deploy/HANDOFF.md .github/workflows/server-image.yml deploy/tests/test_bot_cpu.py deploy/tests/test_server_properties.py deploy/tests/test_relic_pad.py deploy/tests/test_rp_timer_wait.py deploy/tests/test_si_start_choice.py deploy/tests/test_workflows.py
git commit -F - <<'EOF'
feat(cpu-meter): the switch

BotCpuScript.cs declares the meter's switch, the bot_cpu_meter server property (category server, a public static
bool, false in code), with the description of spec 4.1. HEARTHDAOC_BOT_CPU sets it at every start, default off at
every step, as HEARTHDAOC_BOT_RP_TIMER_WAIT does (4.2): compose.yml passes it, entrypoint.sh hands it to
server_properties.py as --bot-cpu, and server_properties.py takes on or off in any case, refuses anything else with
exit 2 before any write, and prints "Bot CPU meter: on" or "off" when it changes the row, so a new world's first
start prints "Bot CPU meter: off" once. .env.example documents it after the realm point waits, and HANDOFF.md lists
it with what ./hdc update adds. The started and stopped handlers that read the property come later (4.1, 4.3).
test_bot_cpu.py checks the chain end to end (6.3); the existing CLI calls in test_server_properties.py and
test_relic_pad.py pass --bot-cpu off, and the new-world outputs gain the meter's line. UT_BotCpuScript pins the
declaration (6.1) and joins CI's fork filter and test_workflows.py (6.4).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `14 files changed, 267 insertions(+), 23 deletions(-)`, `create mode 100644
source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs` and `create mode 100644
source/server/Tests/UnitTests/UT_BotCpuScript.cs`.

Review: approved with no code change, so this section is the tested commit as it was built. One of its three Minor
findings shaped this section: Step 1 gives the reason for `NonParallelizable` on `UT_BotCpuScript`, which 6.1 asks
only of `UT_BotCpuMeter`. The other two changed no line and are left for the owner: the second `run_cli` call in
`test_the_setting_is_required_and_declared_last` checks only stderr, not exit 2 and no write, and `HANDOFF.md`'s
settings sentence now chains three `and`s (a rewording must keep the tested substrings, and keep docker and
`server.log` off the lines that name `HEARTHDAOC_BOT_CPU`).

### Task 8: The process readers and the CSV file

**Files:**
- Modify: `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs:1` (the usings), `:5-9` (the header comment
  names the readers, the CSV and the guarded log writer), `:15-16` (the log writer, the readers, a window's process
  values and the CSV go in after `BOT_CPU_METER`; four types go in after the class)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuScript.cs:1-5` (the usings), `:11` (the header comment's last line),
  `:33-34` (47 test cases at the class's end, in 38 methods, with their hooks, `[SetUp]`, `[TearDown]` and helpers)
- Test: `source/server/Tests/UnitTests/UT_BotCpuScript.cs`

No deploy file and no CI filter change: Task 7 put `UT_BotCpuScript` in CI's fork filter and `test_workflows.py`.

**Spec:** 2.3 (the file's rules, the rotation, a failed write), 3.9 (the sources, the re-listing of threads and the
schedstat-0 rule), 3.7 ("No exception escapes" for the CSV: its own catch, and log calls in their own `try`), 6.1
`UT_BotCpuScript` (the readers, the CSV, and "a CSV write that throws a non-I/O exception logs one warning and the
meter goes on"), Review Focus 2 (the CSV's folder missing or not writable).

**Interfaces:**
- Consumes:
  - Task 1, `BotCpuMeter.cs`: `BotCpuKind` (:29) and `BotCpuStage` (:55), in the tests.
  - Task 3, `BotCpuMeter.cs`: `public static bool Enabled` (:203), `public static int Enable()` (:240),
    `public static void Disable()` (:256); `public sealed class BotCpuTotals` (:100) with
    `Add(BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)` (:135).
  - Tasks 5 and 6, `BotCpuReport.cs`: `public readonly record struct BotCpuSamples(long Sum, int Count, int? Last)`
    with `Add(int sample)` (:26, :29); `public sealed class BotCpuWindow` (:198);
    `public sealed record BotCpuProcess(int Cores, double? ProcessCpuPct, double? LoopThreadsCpuPct,
    double? CampThreadCpuPct, double? LoopWaitMs, double? ThrottledMs, double? GcPauseMs, long? Gen2,
    double? AllocMBSec, double? HeapMB, double? RssMB)` with `OffLoopCpuPct` (:334);
    `BotCpuReport.Window(BotCpuTotals counts, double seconds, long frequency, long gameTimeChange, double tickDuration,
    BotCpuSamples gamebots, IEnumerable<BotCpuBot> bots)` (:465); `Lines(BotCpuWindow window, BotCpuProcess process)`
    (:571, for Task 9); `public const string CsvHeader` (:543);
    `ProcessLine(BotCpuWindow window, BotCpuProcess process)` (:661);
    `CsvRows(BotCpuWindow window, BotCpuProcess process, DateTime end)` (:728).
  - Task 7: `public static class BotCpuScript` with `BOT_CPU_METER`, and `[TestFixture, NonParallelizable] public
    sealed class UT_BotCpuScript` with its one test.
  - Upstream: `GameLoop.THREAD_NAME` (`"GameLoop"`, `Managers/GameLoop/GameLoop.cs:15`); `DOL.Logging.Logger`
    (`IsInfoEnabled`, `IsWarnEnabled`, `Info(string)`, `Warn(string)`) and `LoggerManager.Create(Type)`
    (`source/server/CoreBase/Logging/Logger/LoggerManager.cs:86`); `ELogLevel`
    (`source/server/CoreBase/Logging/Enums/ELogLevel.cs:3`).
- Produces, in `namespace DOL.GS.HearthDAoC`, `BotCpuScript.cs`:
  - In `public static class BotCpuScript`:
    - `public static BotCpuSchedstat? ParseSchedstat(string text)`: fields 1 and 2 in ns; null for null, empty or
      non-numeric text, and for a first field of 0 (`"0 0 0"` is `-`).
    - `public static BotCpuThreadRole ThreadRoleOf(string comm)`: a trailing `'\n'` trimmed; `GameLoop` and anything
      starting `GameLoop_Worker` are `Loop`, anything starting `CampSnapshotPla` is `Camp`, everything else (null
      included) is `Other`.
    - `public static long? ParseThrottledUsec(string cpuStat)` and `public static long? ParseVmRssKB(string status)`.
    - `public static BotCpuProcessSample TakeProcessSample()`: never throws for a missing or unreadable file.
    - `public static BotCpuProcess ProcessWindow(BotCpuProcessSample earlier, BotCpuProcessSample later,
      double seconds)`: `ArgumentNullException` for a null sample.
    - `public const string CsvFileName = "bot-cpu.csv"`, `public const long CsvMaxBytes = 50 * BytesPerMB`
      (52,428,800), `public static string CsvPath` (`AppContext.BaseDirectory/logs/bot-cpu.csv`).
    - `public static bool AppendCsv(string path, IReadOnlyList<string> rows)`: true when written; never throws.
    - Private, for Task 9 and the tests (by reflection): `static readonly Logger Log`; `static void
      WriteLog(ELogLevel level, string line)`, which may throw (Task 9's window and state lines, so a throwing log
      call reaches the tick's catch); `static void TryWriteLog(ELogLevel level, Func<string> line)`, which never
      throws (Task 9's `BOT_CPU_ERROR` and `BOT_CPU_STATE off source=error` lines: the error line as
      `TryWriteLog(ELogLevel.Warning, () => ...)`, the off line at `ELogLevel.Info`); and the test hooks
      `static Action<ELogLevel, string> _logHook`, `static string _procSelf = "/proc/self"`, `static string
      _cgroupCpuStat = "/sys/fs/cgroup/cpu.stat"` and `static Action<FileStream, byte[]> _csvWriteHook`, all null or
      default on the server. There is no hook for the CSV's path: Task 9's tick tests need one, or must not reach
      `AppendCsv(CsvPath, ...)`.
  - After the class: `public enum BotCpuThreadRole { Other, Loop, Camp }`,
    `public readonly record struct BotCpuSchedstat(long CpuNs, long WaitNs)`,
    `public readonly record struct BotCpuThreadSample(BotCpuThreadRole Role, BotCpuSchedstat? Schedstat)` and
    `public sealed record BotCpuProcessSample(int Cores, TimeSpan? ProcessCpu,
    IReadOnlyDictionary<int, BotCpuThreadSample> Threads, long? ThrottledUsec, TimeSpan GcPause, int Gen2,
    long AllocatedBytes, long HeapBytes, long? RssKB)` (`Threads` holds only loop and camp threads, and is null when
    `/proc/self/task` cannot be listed).
  - Task 9 takes a sample at a run's open and at each window's close, then `ProcessWindow(previous, current, <seconds
    between the two samples>)` gives the `BotCpuProcess` for `BotCpuReport.Lines` and `CsvRows` (each process rate is
    over its own two samples, not the window's length; see Task 9); Review Focus 4 ("every reader failing") points
    `_procSelf` and `_cgroupCpuStat` at a missing folder.
  - In `UT_BotCpuScript`, for later tasks: the static `FieldInfo`s `ProcSelfHook`, `CgroupCpuStatHook`, `LogHook` and
    `CsvWriteHook`; the fixture's `_logs` (each line the log hook gets, as `(ELogLevel Level, string Line)`),
    `_folder` (a new temporary folder per test), `_procSelf` (the hook's value, put back at tear-down);
    `SampleThread(string proc, string tid, string comm, string schedstat)` (a thread's folder under `proc/task`) and
    `Csv` (`bot-cpu.csv` under `_folder`'s `logs`).

- [ ] **Step 1: Write the failing tests.** All of them go in `UT_BotCpuScript.cs`, in five edits. Each old text occurs
once in the file as the edit before it leaves it. First the usings (the readers' types, files, threads and
`ELogLevel`) and one more sentence in the header comment:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
using System;
using System.Reflection;
using DOL.GS.HearthDAoC;
using DOL.GS.ServerProperties;
using NUnit.Framework;
=======
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using DOL.GS.HearthDAoC;
using DOL.GS.ServerProperties;
using DOL.Logging;
using NUnit.Framework;
>>>>>>> NEW
<<<<<<< OLD
// server reads once, at start, to turn the meter on with it. Off in code, as the fork's other switches are.
=======
// server reads once, at start, to turn the meter on with it. Off in code, as the fork's other switches are. It also
// reads the process values of the BOT_CPU_PROCESS line (3.9), tested here as parsers of sample text and on sample
// files, and appends the CSV (2.3), tested in a folder of each test's own.
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (2 block(s))`.

The hooks, the fixture's setup and the parsers. The process sample reads two paths and the meter logs through a hook;
all are private static fields of `BotCpuScript`, set by reflection as `BotCpuMeter._stageHook` is
(`BotCpuMeter.cs:222`), since the fork uses no `InternalsVisibleTo`. Each test gets its own folder under the temp path.
`[SetUp]` keeps the two paths and sends the log lines to `_logs`. `[TearDown]` puts the paths back, clears the log and
write hooks, turns the meter off (two CSV tests turn it on), and makes every folder writable again before it deletes
the test's folder. The parsers are tested on 6.1's sample text; a missing file reaches a parser as null, so null is a
case too:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
            "every minute; a GM's /botcpu turns it on or off until the next start"));
    }
}
=======
            "every minute; a GM's /botcpu turns it on or off until the next start"));
    }

    // ---- The test hooks (6.1): where the process sample reads, and where the log lines go

    private static readonly FieldInfo ProcSelfHook =
        typeof(BotCpuScript).GetField("_procSelf", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo CgroupCpuStatHook =
        typeof(BotCpuScript).GetField("_cgroupCpuStat", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo LogHook =
        typeof(BotCpuScript).GetField("_logHook", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo CsvWriteHook =
        typeof(BotCpuScript).GetField("_csvWriteHook", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly List<(ELogLevel Level, string Line)> _logs = [];
    private string _folder;
    private object _procSelf;
    private object _cgroupCpuStat;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "bot-cpu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _procSelf = ProcSelfHook.GetValue(null);
        _cgroupCpuStat = CgroupCpuStatHook.GetValue(null);
        _logs.Clear();
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) => _logs.Add((level, line))));
    }

    [TearDown]
    public void TearDown()
    {
        ProcSelfHook.SetValue(null, _procSelf);
        CgroupCpuStatHook.SetValue(null, _cgroupCpuStat);
        LogHook.SetValue(null, null);
        CsvWriteHook.SetValue(null, null);
        BotCpuMeter.Disable();
        // A failing test may leave a folder read-only.
        foreach (string folder in Directory.GetDirectories(_folder, "*", SearchOption.AllDirectories))
            SetMode(folder, ReadWrite);
        Directory.Delete(_folder, true);
    }

    private const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode ReadWrite = ReadOnly | UnixFileMode.UserWrite;

    private static void SetMode(string folder, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, mode);
    }

    // ---- The readers, as parsers of a file's text (3.9)

    [Test]
    public void SchedstatGivesTheTimeOnACpuAndTheWaitOnARunQueueInNs() =>
        // /proc/self/task/<tid>/schedstat: ns on a CPU, ns waiting on a run queue, timeslices.
        Assert.That(BotCpuScript.ParseSchedstat("123 456 7\n"), Is.EqualTo(new BotCpuSchedstat(123, 456)));

    [Test]
    public void ASchedstatThatReadsZeroIsADash()
    {
        // Where the kernel keeps no scheduler statistics the file still exists and reads 0 0 0: a first field of 0
        // for a running thread counts as unavailable, not as 0 (3.9).
        Assert.That(BotCpuScript.ParseSchedstat("0 0 0\n"), Is.Null);
        Assert.That(BotCpuScript.ParseSchedstat("0 456 7\n"), Is.Null);
    }

    // Linux cuts a thread's name to 15 characters in comm: GameLoop_Worker_<n> is GameLoop_Worker, and the watchdog,
    // the busy-wait threshold thread and the camp planner (CampSnapshotPlanner, CPP:52) are cut too.
    [TestCase("GameLoop\n", BotCpuThreadRole.Loop)]
    [TestCase("GameLoop_Worker\n", BotCpuThreadRole.Loop)]
    [TestCase("GameLoop_Watchd\n", BotCpuThreadRole.Other)]
    [TestCase("GameLoop_BusyWa\n", BotCpuThreadRole.Other)]
    [TestCase("CampSnapshotPla\n", BotCpuThreadRole.Camp)]
    [TestCase(".NET TP Worker\n", BotCpuThreadRole.Other)]
    public void CommNamesTheLoopThreadsAndTheCampThread(string comm, BotCpuThreadRole role) =>
        Assert.That(BotCpuScript.ThreadRoleOf(comm), Is.EqualTo(role));

    [Test]
    public void CpuStatGivesThrottledUsec() =>
        // /sys/fs/cgroup/cpu.stat, cgroup v2, as the container has it.
        Assert.That(BotCpuScript.ParseThrottledUsec(
            "usage_usec 553362452000\nuser_usec 467290902000\nsystem_usec 86071550000\nnr_periods 1200\n" +
            "nr_throttled 3\nthrottled_usec 250000\nnr_bursts 0\nburst_usec 0\n"), Is.EqualTo(250_000));

    [Test]
    public void StatusGivesVmRssInKB() =>
        Assert.That(BotCpuScript.ParseVmRssKB(
            "Name:\tGameServer\nState:\tS (sleeping)\nVmHWM:\t 3600000 kB\nVmRSS:\t 3563520 kB\n" +
            "RssAnon:\t 3400000 kB\n"),
            Is.EqualTo(3_563_520));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a number\n")]
    public void AnEmptyMissingOrUnreadableFileIsADash(string text)
    {
        // A missing file is read as null (BotCpuScript's reads), so every parser gives - for null too.
        Assert.That(BotCpuScript.ParseSchedstat(text), Is.Null);
        Assert.That(BotCpuScript.ThreadRoleOf(text), Is.EqualTo(BotCpuThreadRole.Other));
        Assert.That(BotCpuScript.ParseThrottledUsec(text), Is.Null);
        Assert.That(BotCpuScript.ParseVmRssKB(text), Is.Null);
    }

    [Test]
    public void AFileWithoutItsFieldIsADash()
    {
        // cgroup v1, or no cpu controller: no throttled_usec; a status with no VmRSS.
        Assert.That(BotCpuScript.ParseThrottledUsec("usage_usec 553362452000\nnr_throttled 3\n"), Is.Null);
        Assert.That(BotCpuScript.ParseVmRssKB("Name:\tGameServer\nVmHWM:\t 3600000 kB\n"), Is.Null);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

The reads and the process sample (3.9). `SampleThread` makes a thread's folder under a fake `proc/task`; a thread with
no `schedstat` file left between its two reads. `CommFilesWithNoSchedstatFilesGiveDashes` is a kernel that keeps no
schedstat files: the four schedstat fields are `-`, the camp thread's included, not 0.
`TheSampleReadsTheProcessCpuAndTheGcFromTheRuntime` reads the runtime's own values. It spins until the process has used
100 ms more CPU (with a 10 s cap), so the two samples bracket the spin even on a busy machine, and it keeps 8 MB alive
through a full, blocking collection; `Assert.Multiple` reports each of the five reads.
`TheSampleCountsOnlyFullCollectionsAndReadsTheHeapWithNoCollectionOfItsOwn` tells the GC's sources apart (2.1, 3.9,
4.5). One full collection, then five rounds of gen1 and gen0 collections, put the gen0 and gen1 counts at least 3 above
the gen2 count, so a sample's gen2 is `GC.CollectionCount(2)` only. Three samples raise the full count by less than 3,
so a sample forces no collection. A 16 MB array kept after them raises the heap by 12 MB or more at once:
`GC.GetTotalMemory(false)` counts it, `HeapSizeBytes` (the heap the last collection left) does not, and the 4 MB margin
covers garbage that a collection the runtime starts meanwhile takes back.
`OnLinuxTheSampleFindsALoopThreadByItsName` reads the test process's real `/proc`, and is Inconclusive off Linux:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(BotCpuScript.ParseVmRssKB("Name:\tGameServer\nVmHWM:\t 3600000 kB\n"), Is.Null);
    }
}
=======
        Assert.That(BotCpuScript.ParseVmRssKB("Name:\tGameServer\nVmHWM:\t 3600000 kB\n"), Is.Null);
    }

    // ---- The reads and the process sample (3.9)

    // A thread's folder under proc/task, with its comm and its schedstat (none: the thread left between the reads).
    private static void SampleThread(string proc, string tid, string comm, string schedstat)
    {
        string folder = Path.Combine(proc, "task", tid);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "comm"), comm);
        if (schedstat != null)
            File.WriteAllText(Path.Combine(folder, "schedstat"), schedstat);
    }

    [Test]
    public void TheSampleReadsTheLoopAndCampThreadsTheMemoryAndTheThrottling()
    {
        string proc = Path.Combine(_folder, "proc");
        SampleThread(proc, "101", "GameLoop\n", "31000000000 150000000 900\n");
        SampleThread(proc, "102", "GameLoop_Worker\n", "43100000000 112000000 800\n");
        SampleThread(proc, "103", "GameLoop_Watchd\n", "5000 5000 5\n");
        SampleThread(proc, "104", "CampSnapshotPla\n", "2000000000 0 30\n");
        SampleThread(proc, "105", ".NET TP Worker\n", "7000 7000 7\n");
        SampleThread(proc, "106", "GameLoop_Worker\n", null);
        SampleThread(proc, "107", "GameLoop_Worker\n", "0 0 0\n");
        Directory.CreateDirectory(Path.Combine(proc, "task", "not-a-thread"));
        File.WriteAllText(Path.Combine(proc, "status"), "Name:\tGameServer\nVmRSS:\t 3563520 kB\n");
        string cpuStat = Path.Combine(_folder, "cpu.stat");
        File.WriteAllText(cpuStat, "usage_usec 553362452000\nnr_throttled 3\nthrottled_usec 250000\n");
        ProcSelfHook.SetValue(null, proc);
        CgroupCpuStatHook.SetValue(null, cpuStat);

        BotCpuProcessSample sample = BotCpuScript.TakeProcessSample();

        // Only the loop and camp threads are kept; 106 left between its two reads and is dropped; 107 reads 0 0 0.
        Assert.That(sample.Threads.Keys, Is.EquivalentTo(new[] { 101, 102, 104, 107 }));
        Assert.That(sample.Threads[101], Is.EqualTo(LoopThread(31_000_000_000, 150_000_000)));
        Assert.That(sample.Threads[102], Is.EqualTo(LoopThread(43_100_000_000, 112_000_000)));
        Assert.That(sample.Threads[104], Is.EqualTo(CampThread(2_000_000_000)));
        Assert.That(sample.Threads[107], Is.EqualTo(new BotCpuThreadSample(BotCpuThreadRole.Loop, null)));
        Assert.That(sample.RssKB, Is.EqualTo(3_563_520));
        Assert.That(sample.ThrottledUsec, Is.EqualTo(250_000));
        Assert.That(sample.Cores, Is.EqualTo(Environment.ProcessorCount));
        Assert.That(sample.ProcessCpu, Is.Not.Null);
    }

    [Test]
    public void CommFilesWithNoSchedstatFilesGiveDashes()
    {
        // A kernel that keeps no schedstat files (3.9: est. on the owner's kernel): the threads are named but cannot
        // be read, so the four schedstat fields are -, the camp thread's too, not 0 (2.1: a file missing in the
        // container is -).
        string proc = Path.Combine(_folder, "proc");
        SampleThread(proc, "101", "GameLoop\n", null);
        SampleThread(proc, "102", "GameLoop_Worker\n", null);
        SampleThread(proc, "104", "CampSnapshotPla\n", null);
        ProcSelfHook.SetValue(null, proc);

        BotCpuProcess process = BotCpuScript.ProcessWindow(BotCpuScript.TakeProcessSample(),
            BotCpuScript.TakeProcessSample(), 60);

        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.ProcessCpuPct, Is.Not.Null);
    }

    [Test]
    public void NoProcOrCgroupFilesGiveDashesAndNoFault()
    {
        // Windows, or a container without them: - for those fields, and the other values still given (3.9).
        ProcSelfHook.SetValue(null, Path.Combine(_folder, "no-proc"));
        CgroupCpuStatHook.SetValue(null, Path.Combine(_folder, "no-cgroup", "cpu.stat"));

        BotCpuProcessSample earlier = BotCpuScript.TakeProcessSample();
        BotCpuProcessSample later = BotCpuScript.TakeProcessSample();
        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(later.Threads, Is.Null);
        Assert.That(later.RssKB, Is.Null);
        Assert.That(later.ThrottledUsec, Is.Null);
        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.ThrottledMs, Is.Null);
        Assert.That(process.RssMB, Is.Null);
        Assert.That(process.Cores, Is.EqualTo(Environment.ProcessorCount));
        Assert.That(process.ProcessCpuPct, Is.Not.Null);
        Assert.That(later.AllocatedBytes, Is.GreaterThan(0));
        Assert.That(later.HeapBytes, Is.GreaterThan(0));
    }

    private static TimeSpan CurrentProcessCpu()
    {
        using Process process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    [Test]
    public void TheSampleReadsTheProcessCpuAndTheGcFromTheRuntime()
    {
        // 3.9's sources, on the test's own process: Process.TotalProcessorTime, GC.GetTotalPauseDuration(),
        // GC.CollectionCount(2), all allocation in the process and GC.GetTotalMemory(false). Between two samples the
        // process spins 100 ms of CPU, and the test allocates 8 MB and keeps it through a full, blocking collection.
        BotCpuProcessSample earlier = BotCpuScript.TakeProcessSample();
        TimeSpan spinStart = CurrentProcessCpu();
        Stopwatch wall = Stopwatch.StartNew();
        while (CurrentProcessCpu() - spinStart < TimeSpan.FromMilliseconds(100) && wall.Elapsed.TotalSeconds < 10)
        {
            // Spin.
        }

        byte[] kept = new byte[8 * MB];
        GC.Collect(2, GCCollectionMode.Forced, true);
        BotCpuProcessSample later = BotCpuScript.TakeProcessSample();

        Assert.That(earlier.ProcessCpu, Is.Not.Null);
        Assert.That(later.ProcessCpu, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(later.ProcessCpu.Value - earlier.ProcessCpu.Value,
                Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100)));
            Assert.That(later.Gen2 - earlier.Gen2, Is.GreaterThanOrEqualTo(1));
            Assert.That(later.GcPause - earlier.GcPause, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(later.AllocatedBytes - earlier.AllocatedBytes, Is.GreaterThanOrEqualTo(8 * MB));
            Assert.That(later.HeapBytes, Is.GreaterThanOrEqualTo(8 * MB));
        });
        GC.KeepAlive(kept);
    }

    [Test]
    public void TheSampleCountsOnlyFullCollectionsAndReadsTheHeapWithNoCollectionOfItsOwn()
    {
        // The reads behind gen2 and heapMB (2.1, 3.9), and no forced collection (2.1, 4.5). gen2 is
        // GC.CollectionCount(2): a full collection raises the gen0 and gen1 counts too, and the test's own gen1 and
        // gen0 collections put those at least 3 above it, so a sample that read either is out of range. Three samples
        // raise the full count by less than 3: none forces a collection. heapMB is GC.GetTotalMemory(false), which
        // counts what was allocated since the last collection, so the kept 16 MB shows at once; the heap that
        // collection left (GCMemoryInfo.HeapSizeBytes) would not show it. The full collection first leaves little
        // garbage, so one the runtime starts meanwhile takes back at most a few MB the first sample counted.
        GC.Collect(2, GCCollectionMode.Forced, true);
        for (int i = 0; i < 5; i++)
        {
            GC.Collect(1, GCCollectionMode.Forced, true);
            GC.Collect(0, GCCollectionMode.Forced, true);
        }

        int fullBefore = GC.CollectionCount(2);
        BotCpuProcessSample first = BotCpuScript.TakeProcessSample();
        BotCpuScript.TakeProcessSample();
        BotCpuScript.TakeProcessSample();
        int fullAfter = GC.CollectionCount(2);
        byte[] kept = new byte[16 * MB];
        BotCpuProcessSample fourth = BotCpuScript.TakeProcessSample();

        Assert.Multiple(() =>
        {
            Assert.That(first.Gen2, Is.InRange(fullBefore, fullAfter));
            Assert.That(fullAfter - fullBefore, Is.LessThan(3));
            Assert.That(fourth.HeapBytes - first.HeapBytes, Is.GreaterThanOrEqualTo(12 * MB));
        });
        GC.KeepAlive(kept);
    }

    [Test]
    public void OnLinuxTheSampleFindsALoopThreadByItsName()
    {
        // The real /proc of the test's own process: a thread named as the pool names its workers (MT:100).
        Assume.That(OperatingSystem.IsLinux(), "/proc is Linux's");
        int before = BotCpuScript.TakeProcessSample().Threads.Values.Count(t => t.Role == BotCpuThreadRole.Loop);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim stop = new();
        Thread worker = new(() =>
        {
            started.Set();
            stop.Wait();
        }) { Name = $"{GameLoop.THREAD_NAME}_Worker_7", IsBackground = true };
        worker.Start();
        started.Wait();
        try
        {
            BotCpuProcessSample sample = BotCpuScript.TakeProcessSample();
            Assert.That(sample.Threads.Values.Count(t => t.Role == BotCpuThreadRole.Loop), Is.EqualTo(before + 1));
            Assert.That(sample.RssKB, Is.GreaterThan(0));
        }
        finally
        {
            stop.Set();
            worker.Join();
        }
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

A window's process values, from samples built by hand (2.1 `BOT_CPU_PROCESS`, 3.9).
`TheProcessValuesAreTheWindowsDifferences` rebuilds 2.1's sample (148.2, 121.0, 27.2, 3.1, 212, 14, 41.2, 2210, 3480)
and checks the whole line through Task 6's `ProcessLine`. 2.1's camp thread never waits, so
`TheCampThreadsWaitOnARunQueueIsNotInLoopWaitMs` is the same window with the camp thread waiting 500 ms: `loopWaitMs`
is still the loop threads' 212 (`CampThread` takes the wait as an optional parameter, 0 by default). 2.1's sample has
no throttling and no full collection, so `TheThrottlingIsInMsAndTheFullCollectionsAreCounted` moves both. Then the
thread rules: a thread that left is dropped; a new one counts from zero, and so does a tid that was the camp thread's
and is now a loop thread's; a negative difference counts as 0; a loop thread that reads 0, no loop thread, and threads
listed at one end only give `-`. A camp thread not found is 0 only when the loop threads were read, since it starts
with the first camp planning (CPP:52):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
            worker.Join();
        }
    }
}
=======
            worker.Join();
        }
    }

    // ---- A window's process values, from two samples (2.1 BOT_CPU_PROCESS, 3.9)

    private const long MB = 1_048_576;

    private static BotCpuThreadSample LoopThread(long cpuNs, long waitNs) =>
        new(BotCpuThreadRole.Loop, new BotCpuSchedstat(cpuNs, waitNs));

    private static BotCpuThreadSample CampThread(long cpuNs, long waitNs = 0) =>
        new(BotCpuThreadRole.Camp, new BotCpuSchedstat(cpuNs, waitNs));

    private static TimeSpan Ms(double ms) => TimeSpan.FromTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));

    // A sample with the process CPU and the threads given, and nothing else moving.
    private static BotCpuProcessSample Sample(double processCpuMs, Dictionary<int, BotCpuThreadSample> threads) =>
        new(4, Ms(processCpuMs), threads, 0, TimeSpan.Zero, 0, 0, 0, 0);

    [Test]
    public void TheProcessValuesAreTheWindowsDifferences()
    {
        // 2.1's sample, from two samples 60 s apart: the loop threads ran 72.6 s and waited 212 ms, the camp thread
        // 1.86 s, the process 88.92 s.
        BotCpuProcessSample earlier = new(4, Ms(10_000),
            new Dictionary<int, BotCpuThreadSample>
            {
                [101] = LoopThread(1_000_000_000, 50_000_000),
                [102] = LoopThread(500_000_000, 0),
                [104] = CampThread(140_000_000),
            },
            ThrottledUsec: 5_000_000, GcPause: Ms(100), Gen2: 3, AllocatedBytes: 1_000_000_000, HeapBytes: 100 * MB,
            RssKB: 1024);
        BotCpuProcessSample later = new(4, Ms(10_000 + 88_920),
            new Dictionary<int, BotCpuThreadSample>
            {
                [101] = LoopThread(31_000_000_000, 150_000_000),
                [102] = LoopThread(43_100_000_000, 112_000_000),
                [104] = CampThread(2_000_000_000),
            },
            ThrottledUsec: 5_000_000, GcPause: Ms(114), Gen2: 3, AllocatedBytes: 1_000_000_000 + 2472 * MB,
            HeapBytes: 2210 * MB, RssKB: 3480 * 1024);

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.Cores, Is.EqualTo(4));
        Assert.That(process.ProcessCpuPct, Is.EqualTo(148.2).Within(1e-9));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(121.0).Within(1e-9));
        Assert.That(process.OffLoopCpuPct, Is.EqualTo(27.2).Within(1e-9));
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(3.1).Within(1e-9));
        Assert.That(process.LoopWaitMs, Is.EqualTo(212).Within(1e-9));
        Assert.That(process.ThrottledMs, Is.EqualTo(0));
        Assert.That(process.GcPauseMs, Is.EqualTo(14).Within(1e-9));
        Assert.That(process.Gen2, Is.EqualTo(0));
        Assert.That(process.AllocMBSec, Is.EqualTo(41.2).Within(1e-9));
        Assert.That(process.HeapMB, Is.EqualTo(2210));
        Assert.That(process.RssMB, Is.EqualTo(3480));

        // As the line writes them; tps, chargedPct and callsSec come from the window.
        BotCpuWindow window = BotCpuReport.Window(new BotCpuTotals(), 60, 1_000_000_000, 60_000, 1000.0 / 30,
            default, []);
        Assert.That(BotCpuReport.ProcessLine(window, process), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 " +
            "campThreadCpuPct=3.1 chargedPct=0.0 callsSec=0 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480"));
    }

    [Test]
    public void TheCampThreadsWaitOnARunQueueIsNotInLoopWaitMs()
    {
        // 3.9: loopWaitMs is the schedstat second field of the loop threads only. 2.1's sample has a camp thread
        // that never waits, so the same window with the camp thread waiting 500 ms: loopWaitMs is still the loop
        // threads' 212 ms, and the camp thread's CPU is still its 1.86 s.
        BotCpuProcessSample earlier = Sample(0, new() { [101] = LoopThread(1_000_000_000, 50_000_000),
            [102] = LoopThread(500_000_000, 0), [104] = CampThread(140_000_000, 0) });
        BotCpuProcessSample later = Sample(0, new() { [101] = LoopThread(31_000_000_000, 150_000_000),
            [102] = LoopThread(43_100_000_000, 112_000_000), [104] = CampThread(2_000_000_000, 500_000_000) });

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopWaitMs, Is.EqualTo(212).Within(1e-9));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(121.0).Within(1e-9));
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(3.1).Within(1e-9));
    }

    [Test]
    public void TheThrottlingIsInMsAndTheFullCollectionsAreCounted()
    {
        // 2.1's sample has neither, so a window with both: cpu.stat's throttled_usec rose 250,000 µs, which is 250 ms
        // (2.1: throttledMs is ms), and two full collections ran.
        BotCpuProcessSample earlier = new(4, Ms(0), null, ThrottledUsec: 5_000_000, GcPause: TimeSpan.Zero, Gen2: 3,
            AllocatedBytes: 0, HeapBytes: 0, RssKB: 0);
        BotCpuProcessSample later = new(4, Ms(0), null, ThrottledUsec: 5_250_000, GcPause: TimeSpan.Zero, Gen2: 5,
            AllocatedBytes: 0, HeapBytes: 0, RssKB: 0);

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.ThrottledMs, Is.EqualTo(250).Within(1e-9));
        Assert.That(process.Gen2, Is.EqualTo(2));
    }

    [Test]
    public void AThreadThatLeftIsDroppedAndANewOneCountsFromZero()
    {
        // Threads are re-listed every minute (3.9): 102 left in the window, and 103, a worker the watchdog restarted
        // (or a loop thread that started after the meter, as at the property's start), ran only in it.
        BotCpuProcessSample earlier = Sample(0, new() { [101] = LoopThread(10_000_000_000, 1_000_000),
            [102] = LoopThread(20_000_000_000, 2_000_000) });
        BotCpuProcessSample later = Sample(0, new() { [101] = LoopThread(16_000_000_000, 3_000_000),
            [103] = LoopThread(6_000_000_000, 4_000_000) });

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(20.0).Within(1e-9)); // 6 s + 6 s over 60 s
        Assert.That(process.LoopWaitMs, Is.EqualTo(6).Within(1e-9));

        // A tid that was the camp thread's and is a new loop thread's: the camp thread left, and the loop thread
        // counts from zero, its whole 3 s, not its difference from the camp thread's 9 s.
        earlier = Sample(0, new() { [101] = LoopThread(10_000_000_000, 1_000_000),
            [104] = CampThread(9_000_000_000) });
        later = Sample(0, new() { [101] = LoopThread(16_000_000_000, 3_000_000),
            [104] = LoopThread(3_000_000_000, 2_000_000) });

        process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(15.0).Within(1e-9)); // 6 s + 3 s over 60 s
        Assert.That(process.LoopWaitMs, Is.EqualTo(4).Within(1e-9));
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(0));
    }

    [Test]
    public void ANegativeDifferenceCountsAsZero()
    {
        // A tid the kernel gave to a new loop thread, and counters read lower than before.
        BotCpuProcessSample earlier = new(4, Ms(9_000),
            new Dictionary<int, BotCpuThreadSample> { [101] = LoopThread(50_000_000_000, 9_000_000) },
            ThrottledUsec: 9_000, GcPause: Ms(50), Gen2: 2, AllocatedBytes: 5_000, HeapBytes: 0, RssKB: 0);
        BotCpuProcessSample later = new(4, Ms(5_000),
            new Dictionary<int, BotCpuThreadSample> { [101] = LoopThread(6_000_000_000, 4_000_000) },
            ThrottledUsec: 1_000, GcPause: Ms(40), Gen2: 1, AllocatedBytes: 1_000, HeapBytes: 0, RssKB: 0);

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.ProcessCpuPct, Is.EqualTo(0));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(0));
        Assert.That(process.LoopWaitMs, Is.EqualTo(0));
        Assert.That(process.ThrottledMs, Is.EqualTo(0));
        Assert.That(process.GcPauseMs, Is.EqualTo(0));
        Assert.That(process.Gen2, Is.EqualTo(0));
        Assert.That(process.AllocMBSec, Is.EqualTo(0));
    }

    [Test]
    public void ALoopThreadWhoseSchedstatReadsZeroMakesTheFourSchedstatFieldsDashes()
    {
        // 3.9: loopThreadsCpuPct, offLoopCpuPct, campThreadCpuPct and loopWaitMs are then -, not 0.
        BotCpuProcessSample earlier = Sample(0, new() { [101] = LoopThread(10_000_000_000, 1_000_000),
            [104] = CampThread(1_000_000_000) });
        BotCpuProcessSample later = Sample(60_000, new() { [101] = new(BotCpuThreadRole.Loop, null),
            [104] = CampThread(2_000_000_000) });

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.ProcessCpuPct, Is.EqualTo(100.0).Within(1e-9));

        // A camp thread that reads 0 makes only its own field -.
        later = Sample(60_000, new() { [101] = LoopThread(16_000_000_000, 1_000_000),
            [104] = new(BotCpuThreadRole.Camp, null) });
        process = BotCpuScript.ProcessWindow(earlier, later, 60);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(10.0).Within(1e-9));
    }

    [Test]
    public void NoLoopThreadMakesTheFourSchedstatFieldsDashesAndNoCampThreadIsZero()
    {
        // The camp planner thread starts with the first camp planning (CPP:52), so a server may have none: it then
        // costs nothing. A server always has its loop threads, so none found means they could not be told or read
        // (no schedstat files, unreadable comm files), and then the camp thread could not be either.
        BotCpuProcessSample loopOnly = Sample(0, new() { [101] = LoopThread(10_000_000_000, 0) });
        BotCpuProcess process = BotCpuScript.ProcessWindow(loopOnly, loopOnly, 60);
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(0));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(0));

        BotCpuProcessSample none = Sample(0, new());
        process = BotCpuScript.ProcessWindow(none, none, 60);
        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
    }

    [Test]
    public void ThreadsListedAtOnlyOneEndOfTheWindowGiveDashes()
    {
        // Without the earlier listing, every thread would count its whole life in the window.
        BotCpuProcessSample listed = Sample(0, new() { [101] = LoopThread(10_000_000_000, 0),
            [104] = CampThread(1_000_000_000) });
        foreach ((BotCpuProcessSample earlier, BotCpuProcessSample later) in new[]
            { (Sample(0, null), listed), (listed, Sample(0, null)) })
        {
            BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);
            Assert.That(process.LoopThreadsCpuPct, Is.Null);
            Assert.That(process.CampThreadCpuPct, Is.Null);
            Assert.That(process.LoopWaitMs, Is.Null);
        }
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

The CSV (2.3, 3.7, Review Focus 2), each test under its own `logs` folder. `CsvOf` makes a CSV of an exact length: the
header, then zeros (sparse where the file system allows it), and a line end as its last byte. The failures use real
file-system conditions where they can: a file holding the `logs` folder's name (an `IOException`, which works as root
too), a read-only folder (an `UnauthorizedAccessException`; Inconclusive on Windows and as root, since root writes to
a read-only folder) and a folder in the file's place (an `UnauthorizedAccessException` too). The write hook gives what
a file system can't: an `InvalidOperationException`, which `bot-ai-delay.json`'s narrower catch
(`GameLoopWorkMetrics.cs:102`) would let through; a write cut in half, as on a full disk; and an exception whose
`ToString` throws. `TheBytesReachTheFileInsideTheWrite` reads the file's length inside the write, which a buffered
stream fails. An empty file gets the header in place and the older `.1` stays, as 6.1 lists it apart from the renames:
renaming it would replace up to 50 MB of history with nothing.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
            Assert.That(process.LoopWaitMs, Is.Null);
        }
    }
}
=======
            Assert.That(process.LoopWaitMs, Is.Null);
        }
    }

    // ---- The CSV file (2.3)

    private string Csv => Path.Combine(_folder, "logs", "bot-cpu.csv");

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\n"));

    // A CSV of length bytes: the header line, then zeros (sparse where the file system allows it), and a line end as
    // its last byte, as the last row's.
    private static void CsvOf(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using FileStream stream = new(path, FileMode.Create);
        stream.Write(Encoding.UTF8.GetBytes(Lines(BotCpuReport.CsvHeader)));
        stream.SetLength(length);
        stream.Seek(-1, SeekOrigin.End);
        stream.WriteByte((byte)'\n');
    }

    private void AssertOneWarning(string exception)
    {
        Assert.That(_logs, Has.Count.EqualTo(1));
        Assert.That(_logs[0].Level, Is.EqualTo(ELogLevel.Warning));
        Assert.That(_logs[0].Line, Does.StartWith("Bot CPU meter: ").And.Contain(Csv).And.Contain(exception));
    }

    [Test]
    public void TheCsvIsBotCpuCsvInTheLogsFolderOfTheServersFolder() =>
        // In the image that logs folder is the volume's, /data/logs (deploy/entrypoint.sh:66).
        Assert.That(BotCpuScript.CsvPath, Is.EqualTo(Path.Combine(AppContext.BaseDirectory, "logs", "bot-cpu.csv")));

    [Test]
    public void TheMissingFolderIsCreatedAndTheNewFileStartsWithTheHeader()
    {
        // A launcher build whose logs folder does not exist yet (Review Focus 2).
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1", "row 2"]), Is.True);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 3"]), Is.True);

        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1", "row 2", "row 3")));
        Assert.That(File.Exists(Csv + ".1"), Is.False);
        Assert.That(_logs, Is.Empty);
    }

    [Test]
    public void AWindowsRowsAreAppendedAsUtf8LinesWithNoBom()
    {
        BotCpuTotals counts = new();
        counts.Add(BotCpuKind.Gamebot, BotCpuStage.Npc, 1_000_000_000, 1_800, 4_096);
        counts.Add(BotCpuKind.Unattributed, BotCpuStage.Timer, 500_000_000, 900, 0);
        BotCpuWindow window = BotCpuReport.Window(counts, 60, 1_000_000_000, 60_000, 1000.0 / 30,
            new BotCpuSamples().Add(240), []);
        IReadOnlyList<string> rows = BotCpuReport.CsvRows(window, null,
            new DateTime(2026, 10, 18, 19, 2, 10, DateTimeKind.Utc));

        Assert.That(BotCpuScript.AppendCsv(Csv, rows), Is.True);

        byte[] bytes = File.ReadAllBytes(Csv);
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.That(bytes, Has.No.Member((byte)'\r'));
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(File.ReadAllLines(Csv), Is.EqualTo(new[] { BotCpuReport.CsvHeader }.Concat(rows)));
    }

    [Test]
    public void AnEmptyFileGetsTheHeaderAndTheOldFileIsKept()
    {
        // An empty file holds nothing to keep, so it does not replace bot-cpu.csv.1.
        Directory.CreateDirectory(Path.GetDirectoryName(Csv));
        File.WriteAllText(Csv, "");
        File.WriteAllText(Csv + ".1", "older\n");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));
        Assert.That(File.ReadAllText(Csv + ".1"), Is.EqualTo("older\n"));
    }

    // An older release's columns: other ones, or this header and one more (a release that later dropped a column).
    [TestCase("utc,win_s,kind,n,ms_sec")]
    [TestCase(BotCpuReport.CsvHeader + ",Extra")]
    public void AFileWhoseFirstLineIsAnotherHeaderIsRenamedDotOne(string header)
    {
        // The file replaces an older bot-cpu.csv.1, and a new file starts.
        string old = Lines(header, "2026-10-01T10:00:00Z,60,gamebot,240.0,438.6");
        Directory.CreateDirectory(Path.GetDirectoryName(Csv));
        File.WriteAllText(Csv, old);
        File.WriteAllText(Csv + ".1", "older\n");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(File.ReadAllText(Csv + ".1"), Is.EqualTo(old));
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));
    }

    [Test]
    public void AFileOver50MBIsRenamedDotOneBeforeTheNextAppend()
    {
        Assert.That(BotCpuScript.CsvMaxBytes, Is.EqualTo(50 * MB));
        CsvOf(Csv, BotCpuScript.CsvMaxBytes + 1);
        File.WriteAllText(Csv + ".1", "older\n");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(new FileInfo(Csv + ".1").Length, Is.EqualTo(BotCpuScript.CsvMaxBytes + 1));
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));
    }

    [Test]
    public void AFileOfExactly50MBIsAppendedTo()
    {
        CsvOf(Csv, BotCpuScript.CsvMaxBytes);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(File.Exists(Csv + ".1"), Is.False);
        Assert.That(new FileInfo(Csv).Length, Is.EqualTo(BotCpuScript.CsvMaxBytes + "row 1\n".Length));
    }

    [Test]
    public void AFolderThatCannotBeCreatedLogsOneWarningAndTheNextRowsAreWrittenOnceItCan()
    {
        // Review Focus 2: a file holds the logs folder's name, so the folder cannot be made (an IOException).
        string logs = Path.GetDirectoryName(Csv);
        File.WriteAllText(logs, "not a folder");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.IO.IOException");

        File.Delete(logs);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
        Assert.That(_logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void AFolderThatIsNotWritableLogsOneWarningAndTheNextRowsAreWrittenOnceItIs()
    {
        // Review Focus 2: a read-only folder; the file cannot be created there (not an IOException).
        Assume.That(!OperatingSystem.IsWindows(), "Unix file modes");
        string logs = Path.GetDirectoryName(Csv);
        Directory.CreateDirectory(logs);
        SetMode(logs, ReadOnly);
        bool created;
        try
        {
            File.WriteAllText(Path.Combine(logs, "probe"), "");
            created = true;
        }
        catch (UnauthorizedAccessException)
        {
            created = false;
        }

        Assume.That(created, Is.False, "root writes to a read-only folder");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.UnauthorizedAccessException");

        SetMode(logs, ReadWrite);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
        Assert.That(_logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void ANonIoExceptionLogsOneWarningToo()
    {
        // 2.3, 3.7: a failed write logs one warning whatever the exception, not only an I/O one (bot-ai-delay.json
        // catches IOException and UnauthorizedAccessException only, GameLoopWorkMetrics.cs:102), and the meter goes
        // on: an exception that escaped would turn it off with source=error.
        BotCpuMeter.Enable();
        CsvWriteHook.SetValue(null,
            (Action<FileStream, byte[]>)((_, _) => throw new InvalidOperationException("not I/O")));

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.InvalidOperationException: not I/O");
        Assert.That(BotCpuMeter.Enabled, Is.True);

        CsvWriteHook.SetValue(null, null);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
        Assert.That(_logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void AFolderWhereTheFileShouldBeLogsOneWarning()
    {
        // A folder in the file's place gives an UnauthorizedAccessException, which is not an IOException.
        BotCpuMeter.Enable();
        Directory.CreateDirectory(Csv);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.UnauthorizedAccessException");
        Assert.That(BotCpuMeter.Enabled, Is.True);

        Directory.Delete(Csv);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
    }

    [Test]
    public void AWarningWhoseLogCallThrowsDoesNotEscape()
    {
        // NLog here throws from log calls (3.7): the warning sits in its own try, so the meter goes on.
        LogHook.SetValue(null, (Action<ELogLevel, string>)((_, _) => throw new InvalidOperationException("log")));
        Directory.CreateDirectory(Csv);

        bool written = true;
        Assert.That(() => written = BotCpuScript.AppendCsv(Csv, ["row 1"]), Throws.Nothing);
        Assert.That(written, Is.False);
    }

    // An exception whose text cannot be made.
    private sealed class ExceptionWhoseTextThrows : IOException
    {
        public override string ToString() => throw new InvalidOperationException("text");
    }

    [Test]
    public void AWarningWhoseExceptionTextThrowsDoesNotEscape()
    {
        // The warning carries the exception's text, so it is made inside the guarded writer's try too (3.7).
        CsvWriteHook.SetValue(null, (Action<FileStream, byte[]>)((_, _) => throw new ExceptionWhoseTextThrows()));

        bool written = true;
        Assert.That(() => written = BotCpuScript.AppendCsv(Csv, ["row 1"]), Throws.Nothing);
        Assert.That(written, Is.False);
    }

    // A write cut short, as on a full disk: part of the bytes reach the file, then the write throws.
    private static readonly Action<FileStream, byte[]> HalfThenNoSpace = (stream, bytes) =>
    {
        stream.Write(bytes, 0, bytes.Length / 2);
        throw new IOException("No space left on device");
    };

    [Test]
    public void AWriteCutShortLeavesTheFileAsItWas()
    {
        // 2.3 loses the failed window's rows, not the next: left in the file, the cut row would have no line end
        // and the next window's first row would join it on one line.
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);
        CsvWriteHook.SetValue(null, HalfThenNoSpace);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2 of a window", "row 3 of a window"]), Is.False);
        AssertOneWarning("System.IO.IOException: No space left on device");
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));

        CsvWriteHook.SetValue(null, null);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 4"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1", "row 4")));
    }

    [Test]
    public void AHeaderCutShortAfterARotationKeepsTheOldFile()
    {
        // Left in the new file, a cut header would read as another release's at the next append and be renamed .1,
        // over the 50 MB the rotation had just kept there.
        CsvOf(Csv, BotCpuScript.CsvMaxBytes + 1);
        CsvWriteHook.SetValue(null, HalfThenNoSpace);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        Assert.That(new FileInfo(Csv).Length, Is.EqualTo(0));

        CsvWriteHook.SetValue(null, null);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(new FileInfo(Csv + ".1").Length, Is.EqualTo(BotCpuScript.CsvMaxBytes + 1));
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
    }

    [Test]
    public void TheBytesReachTheFileInsideTheWrite()
    {
        // Taking a cut write back needs it: a buffered stream would hold a window's few hundred bytes until Dispose,
        // outside the try that undoes the write, and a full disk would then leave the cut row in the file.
        int written = 0;
        long inside = -1;
        CsvWriteHook.SetValue(null, (Action<FileStream, byte[]>)((stream, bytes) =>
        {
            stream.Write(bytes);
            written = bytes.Length;
            inside = new FileInfo(stream.Name).Length;
        }));

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(written, Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1").Length));
        Assert.That(inside, Is.EqualTo(written));
    }

    // A last line with no line end: the header alone, as a hand edit may leave it, or a row cut short whose taking
    // back failed.
    [TestCase(BotCpuReport.CsvHeader)]
    [TestCase(BotCpuReport.CsvHeader + "\nrow 1\nrow 2 cut sh")]
    public void ALastLineWithNoLineEndIsEndedBeforeTheNextRows(string old)
    {
        // Left as it is, the next window's first row would join it on one line.
        Directory.CreateDirectory(Path.GetDirectoryName(Csv));
        File.WriteAllText(Csv, old);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 3"]), Is.True);

        Assert.That(File.ReadAllText(Csv), Is.EqualTo(old + "\nrow 3\n"));
        Assert.That(File.Exists(Csv + ".1"), Is.False);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 11 errors, all in the test file and all about the missing types: `CS0103`
for `BotCpuThreadRole` in the six `[TestCase]` attributes of `CommNamesTheLoopThreadsAndTheCampThread` (lines
112-117), and `CS0246` for `BotCpuThreadRole` (118), `BotCpuThreadSample` (351, 354, 360) and `BotCpuProcessSample`
(360). The compiler stops at the unresolved types, so the missing members (`ParseSchedstat`, `TakeProcessSample`,
`ProcessWindow`, `AppendCsv`, `CsvPath`, `CsvMaxBytes`) are not listed. Each error line starts with the file's
absolute path and ends with the project's; the first and the last by position (the compiler prints them in another
order):

```
source/server/Tests/UnitTests/UT_BotCpuScript.cs(112,29): error CS0103: The name 'BotCpuThreadRole' does not exist in the current context [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuScript.cs(360,84): error CS0246: The type or namespace name 'BotCpuThreadSample' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The summary (the warning total varies between builds; 822 was seen):

```
Build FAILED.
    822 Warning(s)
    11 Error(s)
```

- [ ] **Step 3: Implement.** All of it goes in `BotCpuScript.cs`, in five edits. First the usings, and the header
comment, which now names what this task adds:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
using DOL.GS.ServerProperties;
=======
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DOL.GS.ServerProperties;
using DOL.Logging;
>>>>>>> NEW
<<<<<<< OLD
// HearthDAoC: the bot CPU meter's switch and server side (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md,
// section 4). The bot_cpu_meter server property, off in code, is set from HEARTHDAOC_BOT_CPU at every start (4.2) and
// read once, at start: on, the meter starts with the server (4.1). A GM's /botcpu turns the meter on or off until the
// next start and never writes the property (4.3). BotCpuMeter is the hot path; BotCpuReport makes the numbers and the
// text.
=======
// HearthDAoC: the bot CPU meter's server side (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). The
// switch (section 4): the bot_cpu_meter server property, off in code, is set from HEARTHDAOC_BOT_CPU at every start
// (4.2) and read once, at start: on, the meter starts with the server (4.1). A GM's /botcpu turns the meter on or off
// until the next start and never writes the property (4.3). The process readers (3.9): the /proc and cgroup parsers,
// pure functions of a file's text, their reads, the process sample and a window's process values. The CSV (2.3):
// logs/bot-cpu.csv, its header check and its rotation to bot-cpu.csv.1 above 50 MB. The guarded log writer (3.7): a
// failed CSV write logs one warning and never escapes. BotCpuMeter is the hot path; BotCpuReport makes the numbers and
// the text.
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (2 block(s))`.

The log writer (2.1, 3.7), after the switch. `WriteLog` writes one line, at Info or at Warning, and may throw: NLog
here throws from log calls (`throwExceptions="true"`, `source/server/GameServer/config/logconfig.xml:2`). Task 9's
window and state lines use it, so a throwing log call reaches the tick's catch. `TryWriteLog` is 3.7's guarded writer,
for the lines written once something has failed. It takes a `Func<string>` and makes the line inside its own `try`:
the line carries an exception's text, and making that can throw. The build gains a CS0649 warning for `_logHook`
(and one for `_csvWriteHook` below), as for `BotCpuMeter._stageHook`, since only reflection sets them:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
    public static bool BOT_CPU_METER;
}
=======
    public static bool BOT_CPU_METER;

    // ---- The log lines (2.1, 3.7)

    private static readonly Logger Log = LoggerManager.Create(typeof(BotCpuScript));

    // A test hook (6.1): set, the meter's log lines go to it instead of the server log. Null on the server.
    private static Action<ELogLevel, string> _logHook;

    // One line in the server log, at Info (the window and state lines) or Warning (BOT_CPU_ERROR, a failed CSV write).
    // It may throw: NLog here throws from log calls (3.7).
    private static void WriteLog(ELogLevel level, string line)
    {
        Action<ELogLevel, string> hook = _logHook;
        if (hook != null)
            hook(level, line);
        else if (level == ELogLevel.Warning)
        {
            if (Log.IsWarnEnabled)
                Log.Warn(line);
        }
        else if (Log.IsInfoEnabled)
            Log.Info(line);
    }

    // The guarded log writer (3.7): the line made and written in its own try, whose catch does nothing, for the lines
    // written once something has failed. The line is made inside the try too: it carries an exception's text, and
    // making that can throw.
    private static void TryWriteLog(ELogLevel level, Func<string> line)
    {
        try
        {
            WriteLog(level, line());
        }
        catch (Exception)
        {
            // Nothing: the meter's faults never reach the server.
        }
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

The readers and the process sample (3.9), with the four types they return after the class. Each parser is a pure
function of a file's text, and null text (a missing file) gives null, which the line writes as `-`. Numbers are digits
only (`NumberStyles.None`, invariant culture). `ParseSchedstat` takes a first field of 0 as unavailable: where the
kernel keeps no scheduler statistics the file still exists and reads `0 0 0`. `ThreadRoleOf` matches `comm` as Linux
cuts it to 15 characters: `GameLoop`, `GameLoop_Worker` (the pool's `GameLoop_Worker_<n>`, MT:100) and
`CampSnapshotPla` (CPP:52); the watchdog's `GameLoop_Watchd` and the pacer's `GameLoop_BusyWa` are `Other`.
`TakeProcessSample` never throws for a file: `ReadText` gives null for a missing or unreadable one, and `ReadThreads`
gives null when the task folder can't be listed. A thread whose `schedstat` is gone by the time it is read has left,
so it is not listed; one whose file reads 0 or garbage is listed with a null schedstat. The heap is
`GC.GetTotalMemory(false)`, with no forced collection:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
            // Nothing: the meter's faults never reach the server.
        }
    }
}
=======
            // Nothing: the meter's faults never reach the server.
        }
    }

    // ---- The process readers (3.9)

    // The camp planner thread's name, CampSnapshotPlanner (CPP:52), as Linux's comm holds it: cut to 15 characters.
    private const string CampThreadComm = "CampSnapshotPla";

    // Where the process sample reads; test hooks (6.1) point them at sample files, or at nothing.
    private static string _procSelf = "/proc/self";
    private static string _cgroupCpuStat = "/sys/fs/cgroup/cpu.stat";

    /// <summary>
    /// A thread's <c>/proc/self/task/&lt;tid&gt;/schedstat</c> (3.9): its first field, ns on a CPU, and its second,
    /// ns waiting on a run queue. Null (<c>-</c>) for an empty or missing file, text that is not two numbers, or a
    /// first field of 0: where the kernel keeps no scheduler statistics the file still exists and reads <c>0 0 0</c>.
    /// </summary>
    public static BotCpuSchedstat? ParseSchedstat(string text)
    {
        string[] fields = text?.Split((char[])null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return fields.Length >= 2 && Number(fields[0]) is long cpuNs && cpuNs > 0 && Number(fields[1]) is long waitNs
            ? new BotCpuSchedstat(cpuNs, waitNs)
            : null;
    }

    /// <summary>
    /// What a thread is to the process line, from its <c>/proc/self/task/&lt;tid&gt;/comm</c> (3.9), which Linux cuts
    /// to 15 characters: <c>GameLoop</c> and <c>GameLoop_Worker</c> (the pool's <c>GameLoop_Worker_&lt;n&gt;</c>,
    /// MT:100) are the loop threads, <c>CampSnapshotPla</c> the camp planner; <c>GameLoop_Watchd</c>,
    /// <c>GameLoop_BusyWa</c> and every other thread are left out.
    /// </summary>
    public static BotCpuThreadRole ThreadRoleOf(string comm)
    {
        string name = comm?.TrimEnd('\n') ?? string.Empty;
        if (name == GameLoop.THREAD_NAME || name.StartsWith(GameLoop.THREAD_NAME + "_Worker", StringComparison.Ordinal))
            return BotCpuThreadRole.Loop;

        return name.StartsWith(CampThreadComm, StringComparison.Ordinal)
            ? BotCpuThreadRole.Camp
            : BotCpuThreadRole.Other;
    }

    /// <summary>The <c>throttled_usec</c> of <c>/sys/fs/cgroup/cpu.stat</c> (cgroup v2, 3.9); null (<c>-</c>) without
    /// it.</summary>
    public static long? ParseThrottledUsec(string cpuStat) => NumberAfter(cpuStat, "throttled_usec");

    /// <summary>The <c>VmRSS</c> of <c>/proc/self/status</c>, in kB (3.9); null (<c>-</c>) without it.</summary>
    public static long? ParseVmRssKB(string status) => NumberAfter(status, "VmRSS:");

    // The number after key on its line of a "key value" file; null without it.
    private static long? NumberAfter(string text, string key)
    {
        foreach (string line in text?.Split('\n') ?? [])
        {
            string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == key)
                return Number(fields[1]);
        }

        return null;
    }

    // Digits only: no sign, space or separator.
    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    // A file's text; null when it is missing or cannot be read, which is not a fault (3.7): its fields are -.
    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The process values now (3.9), for a window's start or end: the process's CPU, each loop and camp thread's
    /// schedstat by tid, the container's throttling, the GC's pauses, full collections, allocation and heap, and the
    /// resident memory. A value the platform can't give (Windows, a file missing in the container) is null; nothing
    /// here throws for it.
    /// </summary>
    public static BotCpuProcessSample TakeProcessSample()
    {
        string procSelf = _procSelf;
        return new BotCpuProcessSample(Environment.ProcessorCount, ProcessCpu(),
            ReadThreads(Path.Combine(procSelf, "task")), ParseThrottledUsec(ReadText(_cgroupCpuStat)),
            GC.GetTotalPauseDuration(), GC.CollectionCount(2), GC.GetTotalAllocatedBytes(false),
            GC.GetTotalMemory(false), ParseVmRssKB(ReadText(Path.Combine(procSelf, "status"))));
    }

    // Process.TotalProcessorTime; null when the platform can't give it.
    private static TimeSpan? ProcessCpu()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The loop and camp threads by tid, listed again at each sample; null when the task folder can't be listed. A
    // thread whose schedstat is gone by the time it is read has left: it is not listed.
    private static Dictionary<int, BotCpuThreadSample> ReadThreads(string taskFolder)
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(taskFolder);
        }
        catch (Exception)
        {
            return null;
        }

        Dictionary<int, BotCpuThreadSample> threads = [];
        foreach (string folder in folders)
        {
            if (Number(Path.GetFileName(folder)) is not long tid || tid > int.MaxValue)
                continue;

            BotCpuThreadRole role = ThreadRoleOf(ReadText(Path.Combine(folder, "comm")));
            if (role == BotCpuThreadRole.Other)
                continue;

            string schedstat = ReadText(Path.Combine(folder, "schedstat"));
            if (schedstat != null)
                threads[(int)tid] = new BotCpuThreadSample(role, ParseSchedstat(schedstat));
        }

        return threads;
    }
}

/// <summary>What a thread is to the process line (3.9), from its <c>comm</c> name.</summary>
public enum BotCpuThreadRole
{
    /// <summary>Any other thread: not read further.</summary>
    Other,
    /// <summary>The <c>GameLoop</c> thread or a <c>GameLoop_Worker_&lt;n&gt;</c> (MT:100): <c>loopThreadsCpuPct</c>
    /// and <c>loopWaitMs</c>.</summary>
    Loop,
    /// <summary>The <c>CampSnapshotPlanner</c> thread (CPP:52): <c>campThreadCpuPct</c>.</summary>
    Camp,
}

/// <summary>A thread's <c>schedstat</c> (3.9): ns on a CPU and ns waiting on a run queue.</summary>
public readonly record struct BotCpuSchedstat(long CpuNs, long WaitNs);

/// <summary>A loop or camp thread in a process sample (3.9): its role and its schedstat, null when that reads 0 or
/// cannot be parsed (<c>-</c>).</summary>
public readonly record struct BotCpuThreadSample(BotCpuThreadRole Role, BotCpuSchedstat? Schedstat);

/// <summary>
/// The process values at one instant (3.9), taken at a window's start and end by
/// <see cref="BotCpuScript.TakeProcessSample"/>; <see cref="BotCpuScript.ProcessWindow"/> turns two into the window's
/// <see cref="BotCpuProcess"/>. Null is a value the platform can't give.
/// </summary>
/// <param name="Cores"><see cref="Environment.ProcessorCount"/>.</param>
/// <param name="ProcessCpu"><see cref="Process.TotalProcessorTime"/>.</param>
/// <param name="Threads">The loop and camp threads by tid; null when <c>/proc/self/task</c> can't be listed.</param>
/// <param name="ThrottledUsec"><c>throttled_usec</c> of the container's <c>cpu.stat</c>.</param>
/// <param name="GcPause"><see cref="GC.GetTotalPauseDuration"/>.</param>
/// <param name="Gen2"><c>GC.CollectionCount(2)</c>.</param>
/// <param name="AllocatedBytes"><c>GC.GetTotalAllocatedBytes(false)</c>: all allocation in the process.</param>
/// <param name="HeapBytes"><c>GC.GetTotalMemory(false)</c>, as <c>SERVER_STALL</c> reads it; no forced collection.
/// </param>
/// <param name="RssKB"><c>VmRSS</c> of <c>/proc/self/status</c>, kB.</param>
public sealed record BotCpuProcessSample(int Cores, TimeSpan? ProcessCpu,
    IReadOnlyDictionary<int, BotCpuThreadSample> Threads, long? ThrottledUsec, TimeSpan GcPause, int Gen2,
    long AllocatedBytes, long HeapBytes, long? RssKB);
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

A window's process values. The threads are those of the later listing. A tid not listed before, or listed with
another role, started in the window and counts from zero; the property's start needs this, since the run opens before
the loop threads exist (4.1, `GameServer.cs:510`, :517). A thread that left is dropped. `loopThreadsCpuPct`,
`loopWaitMs` and `campThreadCpuPct` are null (`-`) when a loop thread reads 0, when no loop thread is found (a running
server always has one, so none found means the threads could not be told or read), and when the threads are listed at
one end only. With the loop threads read, a camp thread not found is 0. `loopWaitMs` and `throttledMs` are totals over
the window, and a MB is 1,048,576 bytes:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        return threads;
    }
}
=======
        return threads;
    }

    /// <summary>
    /// A window's process values (2.1 <c>BOT_CPU_PROCESS</c>, 3.9): the differences of two samples
    /// <paramref name="seconds"/> apart, a negative difference counted as 0, and the memory at the end. The loop and
    /// camp threads are those of the <paramref name="later"/> listing: a thread that left in the window is dropped, and
    /// one that started in it counts from zero. A loop thread whose schedstat reads 0 makes
    /// <c>loopThreadsCpuPct</c>, <c>offLoopCpuPct</c>, <c>campThreadCpuPct</c> and <c>loopWaitMs</c> <c>-</c>; so do
    /// no loop thread and threads listed at one end only. With loop threads read, no camp thread is 0: it starts with
    /// the first camp planning (CPP:52).
    /// </summary>
    public static BotCpuProcess ProcessWindow(BotCpuProcessSample earlier, BotCpuProcessSample later, double seconds)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        double? loopThreadsCpuPct = null;
        double? loopWaitMs = null;
        double? campThreadCpuPct = null;
        if (earlier.Threads != null && later.Threads != null)
        {
            int loopThreads = 0;
            bool loopUnread = false;
            bool campUnread = false;
            long loopNs = 0;
            long waitNs = 0;
            long campNs = 0;
            foreach ((int tid, BotCpuThreadSample thread) in later.Threads)
            {
                if (thread.Role == BotCpuThreadRole.Other)
                    continue;

                bool loop = thread.Role == BotCpuThreadRole.Loop;
                if (loop)
                    loopThreads++;

                // A thread not listed before, or whose tid was another thread's, started in the window.
                BotCpuSchedstat? start = new BotCpuSchedstat(0, 0);
                if (earlier.Threads.TryGetValue(tid, out BotCpuThreadSample before) && before.Role == thread.Role)
                    start = before.Schedstat;

                if (thread.Schedstat is not BotCpuSchedstat end || start is not BotCpuSchedstat from)
                {
                    loopUnread |= loop;
                    campUnread |= !loop;
                    continue;
                }

                if (loop)
                {
                    loopNs += Positive(end.CpuNs - from.CpuNs);
                    waitNs += Positive(end.WaitNs - from.WaitNs);
                }
                else
                    campNs += Positive(end.CpuNs - from.CpuNs);
            }

            if (loopThreads > 0 && !loopUnread)
            {
                loopThreadsCpuPct = PercentOfOneCore(loopNs / 1e6, seconds);
                loopWaitMs = waitNs / 1e6;
            }

            // No camp thread gives 0, but only once the loop threads show the readers work: a server always has loop
            // threads, so none found means the threads could not be told or read (no schedstat files, unreadable comm
            // files), the camp thread's included.
            if (loopThreads > 0 && !loopUnread && !campUnread)
                campThreadCpuPct = PercentOfOneCore(campNs / 1e6, seconds);
        }

        double? processCpuPct = earlier.ProcessCpu is TimeSpan processStart && later.ProcessCpu is TimeSpan processEnd
            ? PercentOfOneCore(Math.Max(0, (processEnd - processStart).TotalMilliseconds), seconds)
            : null;
        double? throttledMs = earlier.ThrottledUsec is long throttledStart && later.ThrottledUsec is long throttledEnd
            ? Positive(throttledEnd - throttledStart) / 1000.0
            : null;
        double? allocMBSec = seconds > 0
            ? Positive(later.AllocatedBytes - earlier.AllocatedBytes) / (double)BytesPerMB / seconds
            : null;

        return new BotCpuProcess(later.Cores, processCpuPct, loopThreadsCpuPct, campThreadCpuPct, loopWaitMs,
            throttledMs, Math.Max(0, (later.GcPause - earlier.GcPause).TotalMilliseconds),
            Positive(later.Gen2 - earlier.Gen2), allocMBSec, later.HeapBytes / (double)BytesPerMB,
            later.RssKB / 1024.0);
    }

    private const long BytesPerMB = 1_048_576;

    private static long Positive(long value) => Math.Max(0, value);

    // ms of CPU over the window as % of one core: 1,000 ms a second is 100%; null for a window with no length.
    private static double? PercentOfOneCore(double ms, double seconds) => seconds > 0 ? ms / seconds / 10 : null;
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

The CSV (2.3), at the class's end. `CsvPath` is `logs/bot-cpu.csv` under `AppContext.BaseDirectory`, the folder the
image links to `/data/logs` (`deploy/entrypoint.sh:68` since Task 7; the comment keeps the spec's `:66`). `AppendCsv`
checks the file before every append, so before the first one after on too: a file above `CsvMaxBytes` (more than
52,428,800 bytes; exactly 50 MB is appended to), or whose first line is not exactly `BotCpuReport.CsvHeader`, is
renamed `.1`, replacing an older one. `StartsWithHeader` reads no more than the header's length and two characters, so
a 50 MB file is never read whole. The header (when the file is new or empty) and the rows go in one unbuffered write
(`BufferSize = 0`), so the bytes reach the file inside the inner `try`, and a failed write sets the file back to its
length before it, which needs no space. A kept file whose last line has no line end (a hand edit, or a cut write whose
taking back failed) gets one before the rows. The outer catch takes every exception, logs one Warning through
`TryWriteLog` and returns false; it never touches `BotCpuMeter.Enabled`:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
    private static double? PercentOfOneCore(double ms, double seconds) => seconds > 0 ? ms / seconds / 10 : null;
}
=======
    private static double? PercentOfOneCore(double ms, double seconds) => seconds > 0 ? ms / seconds / 10 : null;

    // ---- The CSV file (2.3)

    /// <summary>The CSV's name, in the server's <c>logs</c> folder.</summary>
    public const string CsvFileName = "bot-cpu.csv";

    /// <summary>Above this the CSV is renamed <c>bot-cpu.csv.1</c> before the next append (2.3): 50 MB, about 20 days
    /// of continuous running.</summary>
    public const long CsvMaxBytes = 50 * BytesPerMB;

    private static readonly UTF8Encoding Utf8 = new(false);

    // A test hook (6.1): set, it writes the window's bytes to the CSV in place of the stream's own Write, so a test can
    // cut a write short as a full disk does. Null on the server.
    private static Action<FileStream, byte[]> _csvWriteHook;

    /// <summary><c>logs/bot-cpu.csv</c> in the server's folder (<see cref="AppContext.BaseDirectory"/>): in the image,
    /// <c>/data/logs/bot-cpu.csv</c>, as that <c>logs</c> folder is the volume's (<c>deploy/entrypoint.sh:66</c>).
    /// </summary>
    public static string CsvPath => Path.Combine(AppContext.BaseDirectory, "logs", CsvFileName);

    /// <summary>
    /// Appends a window's <paramref name="rows"/> to the CSV at <paramref name="path"/> (2.3), as UTF-8 lines ending in
    /// LF, with no BOM. Its folder is created when missing. Before the append, a file above
    /// <see cref="CsvMaxBytes"/>, or whose first line is not <see cref="BotCpuReport.CsvHeader"/> (an older release's
    /// columns), is renamed <c>.1</c>, replacing an older one; a new, missing or empty file starts with the header, and
    /// a last line left with no line end is ended first. The check runs before every append, the first after the meter
    /// turns on among them. Never throws: a failed write, whatever the exception, logs one warning and returns false,
    /// and its rows are lost; a write cut short is taken back, so the file is left as it was. The meter goes on, and
    /// the next window's append tries again.
    /// </summary>
    public static bool AppendCsv(string path, IReadOnlyList<string> rows)
    {
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // An empty file is not renamed: it holds nothing to keep, and renaming it would replace the older .1 with
            // nothing. It gets the header in place, as a missing file does.
            FileInfo file = new(path);
            bool lineEnded = true;
            if (file.Exists && file.Length > 0)
            {
                if (file.Length > CsvMaxBytes || !StartsWithHeader(path))
                    File.Move(path, path + ".1", true);
                else
                    lineEnded = EndsWithLineEnd(path);
            }

            // Unbuffered, so the bytes reach the file inside the write below, where a failure can be undone, and not in
            // Dispose.
            using FileStream stream = new(path, new FileStreamOptions
                { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, BufferSize = 0 });
            long length = stream.Length;
            StringBuilder text = new();
            if (length == 0)
                text.Append(BotCpuReport.CsvHeader).Append('\n');
            else if (!lineEnded)
                text.Append('\n');
            foreach (string row in rows)
                text.Append(row).Append('\n');

            byte[] bytes = Utf8.GetBytes(text.ToString());
            try
            {
                Action<FileStream, byte[]> hook = _csvWriteHook;
                if (hook != null)
                    hook(stream, bytes);
                else
                    stream.Write(bytes);
            }
            catch (Exception)
            {
                // A write cut short, on a full disk say, leaves part of a row or of the header with no line end: the
                // next row would join it, and a cut header would read as another release's at the next append and
                // be renamed .1 over the old file. So the file goes back to its length before the write, which needs
                // no space; the write's own exception is the one reported.
                try
                {
                    stream.SetLength(length);
                }
                catch (Exception)
                {
                    // The file keeps the cut part; the next append ends its line first.
                }

                throw;
            }

            return true;
        }
        catch (Exception exception)
        {
            TryWriteLog(ELogLevel.Warning, () => $"Bot CPU meter: could not write this window's rows to {path}, so " +
                $"they are lost; the meter goes on: {exception}");
            return false;
        }
    }

    // Whether the file's first line is the CSV's header; reads no more than the header and its line end. A file that
    // is the header alone, with no line end, is kept: EndsWithLineEnd ends it before the rows.
    private static bool StartsWithHeader(string path)
    {
        char[] start = new char[BotCpuReport.CsvHeader.Length + 2];
        int read;
        using (StreamReader reader = new(path, Utf8, true))
            read = reader.ReadBlock(start, 0, start.Length);

        string text = new(start, 0, read);
        int end = text.IndexOf('\n');
        return (end >= 0 ? text[..end] : text).TrimEnd('\r') == BotCpuReport.CsvHeader;
    }

    // Whether the file's last byte is a line end. A hand edit, or a write cut short whose taking back failed, can leave
    // a last line with none, and the next window's first row would join it.
    private static bool EndsWithLineEnd(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return true;

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree; this task
adds two CS0649 warnings, on `_logHook` and `_csvWriteHook`.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuScript."`
Expected: `Passed!  - Failed:     0, Passed:    48, Skipped:     0, Total:    48` (exit code 0): Task 7's 1 and this
task's 47. Run as root or off Linux, `AFolderThatIsNotWritable...` and `OnLinuxTheSampleFindsALoopThreadByItsName`
are Inconclusive instead; CI's ubuntu-24.04 runner runs them as a non-root user.

- [ ] **Step 5: Check the files' form.** Both files are LF, with no BOM and no tab. The only line over 120 characters
is still Task 7's description string (181 characters), which `ServerDeclarationTests` reads as one literal:

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs False 0 0 True 181
source/server/Tests/UnitTests/UT_BotCpuScript.cs False 0 0 True 119
```

Run: `git diff --check`
Expected: no output (exit code 0).

- [ ] **Step 6: Run the wider checks.**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpu"`
Expected: `Passed!  - Failed:     0, Passed:   249, Skipped:     0, Total:   249` (the meter's three classes).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   893, Skipped:     0, Total:   893` (846 after Task 7, and this
task's 47).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3681, Skipped:     0, Total:  3681` (3634 after Task 7, and 47;
about half a minute of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with
"database is locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

No deploy file changed, so of the deploy tests only the meter's own and the workflow check run:

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 21 tests` and `OK`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

Run: `git status --short`
Expected: only these lines (`serverconfig.xml` is ignored, so it does not show):

```
 M source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs
 M source/server/Tests/UnitTests/UT_BotCpuScript.cs
```

Run: `git diff --numstat`
Expected:

```
445	5	source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs
827	1	source/server/Tests/UnitTests/UT_BotCpuScript.cs
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs
git commit -F - <<'EOF'
feat(cpu-meter): the process readers and the csv file

BotCpuScript now reads the process values of the BOT_CPU_PROCESS line and writes the CSV (spec 2.3, 3.9). The /proc
and cgroup readers are pure parsers of a file's text: schedstat gives a thread's ns on a CPU and ns waiting on a run
queue, and a first field of 0 is - (a kernel that keeps no scheduler statistics); comm tells the GameLoop and
GameLoop_Worker threads (Linux cuts names to 15 characters) and CampSnapshotPla from the watchdog, the busy-wait thread
and the rest; cpu.stat gives throttled_usec and /proc/self/status VmRSS; an empty or missing file is -.
TakeProcessSample reads them with the process CPU and the GC's pauses, full collections, allocation and heap, and never
throws for a file it cannot read. ProcessWindow turns two samples into a window's BotCpuProcess: a negative difference
counts as 0, a thread that left in the window is dropped, and one that started in it, or took a tid of another role,
counts from zero. A loop thread whose schedstat reads 0, no loop thread, or threads listed at one end only make the
four schedstat fields -; with the loop threads read, no camp thread is 0.

AppendCsv creates the logs folder when missing, renames the file bot-cpu.csv.1 above 50 MB or when its first line is not
exactly this header, starts a new or empty file with the header (an empty file holds nothing to keep, so the older .1
stays), ends a last line left with no line end, and appends the rows as UTF-8 lines ending in LF. The write is
unbuffered, so a write cut short, on a full disk say, is taken back and the file is left as it was. A failed write,
whatever the exception, logs one warning through the guarded log writer, which makes its line inside its own try, and
the meter goes on: the next window's rows are written once the folder is writable (Review Focus 2). UT_BotCpuScript pins
the parsers on sample text, the reads on sample files, on the test's own /proc and on the runtime's own values (gen2
counts only full collections, heapMB is GC.GetTotalMemory(false), and a sample forces no collection), a window's values
with 2.1's sample (the camp thread's wait stays out of loopWaitMs), and the CSV's rules and failures (6.1, 3.7).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `2 files changed, 1272 insertions(+), 6 deletions(-)`.

Review: three fix rounds shaped this section, and the steps above show their final code. Round 1 (Important: no camp
thread gave 0 when no thread could be read) gave the `!loopUnread && !campUnread` rule and
`CommFilesWithNoSchedstatFilesGiveDashes`. Its Minors gave the unbuffered write that is taken back when cut short
(`_csvWriteHook` and the two cut-write tests), `TryWriteLog`'s `Func<string>`
(`AWarningWhoseExceptionTextThrowsDoesNotEscape`) and the `Enabled` assert with `Disable()` in `[TearDown]`. Round 2
(Important: no test moved `throttledMs` or `gen2`) gave `TheThrottlingIsInMsAndTheFullCollectionsAreCounted`, the reused
tid case, the `CsvHeader + ",Extra"` case and the header comment. Round 3 (Important: no non-I/O exception, and no read
of the runtime's values) gave the `InvalidOperationException` hook test, `AFolderWhereTheFileShouldBeLogsOneWarning`,
`TheSampleReadsTheProcessCpuAndTheGcFromTheRuntime`, `TheBytesReachTheFileInsideTheWrite`, `EndsWithLineEnd` with
`ALastLineWithNoLineEndIsEndedBeforeTheNextRows`, and the LF that ends `CsvOf`. The last review was not approved. Its
two Important gaps were test gaps, and two tests now close them, with no production change (the build's test-only commit
c28fae7, after Task 11): `TheSampleCountsOnlyFullCollectionsAndReadsTheHeapWithNoCollectionOfItsOwn` tells
`GC.CollectionCount(2)` from the gen0 and gen1 counts, and `GC.GetTotalMemory(false)` from `HeapSizeBytes` and from a
forced `GetTotalMemory(true)`; `TheCampThreadsWaitOnARunQueueIsNotInLoopWaitMs`, with `CampThread`'s optional wait,
holds the camp thread's wait out of `loopWaitMs`. Each fails under the mutation it pins, and every older test passes
under it. Its Minors changed no line and are left for the owner: a loop thread unreadable at the window's start only is
not pinned, and `ProcessWindow` (a pure figure of 2.1, which 5.1 gives to `BotCpuReport`) stays next to the reads in
`BotCpuScript`, as the brief puts the process sample in this task. The code's `deploy/entrypoint.sh:66` (the spec's
cite, in `CsvPath`'s comment and in `TheCsvIsBotCpuCsvInTheLogsFolderOfTheServersFolder`) is :68 after Task 7. Three
spec issues stand: 2.3 lists an empty file with the files renamed `.1`, and the build gives it the header in place and
keeps the older `.1`; 3.9 does not say what `campThreadCpuPct` is with no camp thread (0 here, once the loop threads
were read); and the spec gives no text for the CSV's warning, so `Bot CPU meter: could not write ...` is not found by
2.2's `grep BOT_CPU`.

### Task 9: The report side

**Files:**
- Modify: `source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs:1-8` (the usings), `:18-19` (the header
  comment's end names the report side), `:415-418` (the report side goes in after `EndsWithLineEnd`, at the class's
  end; `BotCpuResult` goes in after the class)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuScript.cs:1-12` (the usings), `:20` (the header comment's last line),
  `:58` (four saved values after `_cgroupCpuStat`), `:68-73` (`[SetUp]`'s end and `[TearDown]`'s start), `:857-860`
  (61 test cases at the class's end, in 49 methods, with their hooks, test doubles and helpers)
- Test: `source/server/Tests/UnitTests/UT_BotCpuScript.cs`

No deploy file and no CI filter change: Task 7 put `UT_BotCpuScript` in CI's fork filter and `test_workflows.py`.

**Spec:** 3.7 (the report side; one at a time: the report lock, the job queue, runs, the published result; no exception
escapes; a window; on, off and reset), 3.8 (the registry sampled each second, the window closed at the first tick at or
after its 60 s), 4.1 (the started and stopped handlers), 4.3 (the commands never write the property; the stop time),
4.4 (who writes `Enabled`), 6.1 `UT_BotCpuScript` (off and on from a command, one at a time, no exception escapes, the
stop time), Review Focus 3 (a bot that leaves the world mid-window, through a tick), 4 (a tick with every platform
reader failing) and 5 (a burst of commands inside one second).

**Interfaces:**
- Consumes:
  - Task 3, `BotCpuMeter.cs`: `public static bool Enabled` (:203), `public static int RunNumber` (:233),
    `public static int Enable()` (:240), `public static void Disable()` (:256),
    `public static IEnumerable<KeyValuePair<GameObject, BotCpuCounter>> Counters()` (:262),
    `public static BotCpuTotals TakeSnapshot()` (:270). In the tests: `BotCpuTotals.SlotLength` (:110),
    `BotCpuTotals.CellIndex(BotCpuKind kind, BotCpuStage stage)` (:119), `public sealed class BotCpuCounter` (:172)
    with `Ticks`, `Calls`, `Kind` and `BotCpuCounter(BotCpuKind kind)`, and the private `_slots` (:207) and
    `_counters` (:213), by reflection.
  - Task 5, `BotCpuReport.cs`: `BotCpuSamples.Add(int sample)` (:29) and `NextWindow()` (:32);
    `public sealed record BotCpuBot(BotCpuKind Kind, string Name, int Level, string ClassName, int Region, string Goal,
    string Activity, long Ticks, long Calls)` (:53); `BotCpuWindow` (:198) with `Win` (:224) and
    `Top(BotCpuKind kind)` (:253); `BotCpuSince` (:260) with `Start(bool reset)` (:275), `Reset`, `Windows`,
    `MsPerBotSec(BotCpuKind kind)` and `Add(BotCpuWindow window)` (:300); `MinimumWindowSeconds` (5, :365);
    `Difference(BotCpuTotals later, BotCpuTotals earlier)` (:382); `TakeCounterWindow(BotCpuCounter counter)` (:413);
    `Window(BotCpuTotals counts, double seconds, long frequency, long gameTimeChange, double tickDuration,
    BotCpuSamples gamebots, IEnumerable<BotCpuBot> bots)` (:465), null under 5 s.
  - Task 6, `BotCpuReport.cs`: `public enum BotCpuOffSource { Command, Timer, Stop, Error }` (:347), `WindowSeconds`
    (60, :362), `StateOnByPropertyLine` (:539), `Lines(BotCpuWindow window, BotCpuProcess process)` (:571),
    `StateOnByCommandLine(string by, int? minutes)` (:704), `StateOffLine(BotCpuOffSource source, string by = null)`
    (:709), `ErrorLine(string step, Exception exception)` (:716), `CsvRows(BotCpuWindow window, BotCpuProcess process,
    DateTime end)` (:728); in the tests, `CsvHeader` (:543), `CsvUtc(DateTime end)` (:758) and
    `OffText(double openSeconds)` (:829).
  - Task 7, `BotCpuScript.cs`: `public static bool BOT_CPU_METER` (:25).
  - Task 8, `BotCpuScript.cs`: `WriteLog(ELogLevel level, string line)` (:36, may throw) and
    `TryWriteLog(ELogLevel level, Func<string> line)` (:53, never throws); `TakeProcessSample()` (:147);
    `ProcessWindow(BotCpuProcessSample earlier, BotCpuProcessSample later, double seconds)` (:211); `CsvPath` (:311);
    `AppendCsv(string path, IReadOnlyList<string> rows)` (:323). In `UT_BotCpuScript`: `ProcSelfHook`,
    `CgroupCpuStatHook`, `LogHook`, `CsvWriteHook`, `_logs`, `_folder`, `SampleThread(string proc, string tid,
    string comm, string schedstat)` (:158) and `Csv` (:558).
  - Upstream: `GameLoop.GameLoopTime` and `GameLoop.TickDuration` (`Managers/GameLoop/GameLoop.cs:23-24`, private
    setters the tests call by reflection); `AutonomousBotRegistry.PopulationForBrainTick` (ABR:21, its field
    `_populationForBrainTick` at :14); `GameBot.ClassName` (`bots/GameBot.cs:41`), `IsAutonomousWorldBot` (:47) and
    `PersistentRecord` (:54); `OfflineWorldBotRecord.Activity` and `ObjectiveKind`
    (`bots/autonomous/AutonomousBotEconomy.cs:928`, :965); `GamePlayer.CharacterClass`
    (`gameobjects/GamePlayer.cs:2639`); `GameObject.CurrentRegion` (`gameobjects/GameObject.cs:79`);
    `GameServerStartedEventAttribute` and `GameServerStoppedEventAttribute`
    (`events/server/GameServerStartedEventAttribute.cs:28`, `GameServerStoppedEventAttribute.cs:28`);
    `GameServer.LoadTestDouble` (`GameServer.cs:253`).
- Produces, in `namespace DOL.GS.HearthDAoC`, `BotCpuScript.cs` (lines at this task's commit):
  - `public const int TickMilliseconds = 1000;` (:428)
  - `public static BotCpuResult Result { get; }` (:514): a `Volatile.Read` of the last result published; never null,
    `BotCpuResult.Off` when no run is open.
  - `public static DateTime LocalNow()` (:518): `DateTime.Now`, or the test hook's.
  - `public static DateTime? CommandOn(string by, int? minutes)` (:539): queues the on job and returns the stop time,
    the local time of the instant `minutes` from now (null for none). It does not check `minutes`: Task 10 checks
    1-1440. `Enabled` is unchanged until the job runs.
  - `public static double? CommandOff(string by)` (:556): `BotCpuMeter.Disable()`, the command's instant, then the off
    job queued. Returns the final window's seconds as the job measures them (from `Result.WindowStartTimestamp` to the
    command), so Task 10 passes it to `BotCpuReport.OffText` unchanged; below 0 when a tick started the open window
    after the command (the job then writes none, and `OffText` says 0 s); null when `Result` is off
    (`AlreadyOffText`).
  - `public static void CommandReset()` (:567): queues the reset job.
  - `[GameServerStartedEvent] public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)` (:575)
    and `[GameServerStoppedEvent] public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)`
    (:587).
  - After the class: `public sealed record BotCpuResult(bool On, int Run, bool ByCommand, DateTime OnSince,
    DateTime? StopsAt, DateTime WindowStart, long WindowStartTimestamp, BotCpuWindow Last, BotCpuProcess LastProcess,
    BotCpuSince Since)` (:900) with `public static BotCpuResult Off { get; }` (:904). Times are local; `Last` is null
    before the first window.
  - Private, reached by the tests through reflection: the hooks `_queueHook` (`Action<WaitCallback>`), `_timerHook`
    (`Func<TimerCallback, object, IDisposable>`), `_nowHook` (`Func<DateTime>`), `_timestampHook` (`Func<long>`),
    `_zoneHook` (`TimeZoneInfo`) and `_csvFile` (`string`), all null on the server; `ReportLock`, `Jobs`, `_open`,
    `_result`, and the open run's `Timer`. `BOT_CPU_ERROR`'s step names are `on`, `off`, `reset`, `tick`, `start` and
    `stop`.
  - Task 10 (`/botcpu`) reads `Result` for every reply: `OnText(m, stopsAt)` with the time `CommandOn` returns;
    `StatusText(r.OnSince, r.ByCommand, r.StopsAt, BotCpuScript.LocalNow(), r.Last, r.LastProcess, r.Since)`;
    `TopText(r.Last, kind)`, or `NoWindowYetText(r.OnSince)` while `r.Last` is null; `OffText` with `CommandOff`'s
    value. It checks the minutes and the kind before it calls a command. A Task 10 test that runs commands on the real
    thread pool does as `TheRealThreadPoolRunsEachCommandsJob`: one command at a time, wait for its line, then take the
    report lock once.
  - In `UT_BotCpuScript`, for later tasks: the `BindingFlags` `Hidden`, the static `FieldInfo`s `QueueHook` and
    `ReportLockField`, the constants `Gm` (`"Aldwin"`), `Gamebot`, `Npc`, `OnLine` and `OffLine`, the fixture's
    `_queued` and `_timers`, `At(int hour, int minute, int second, int ms = 0)`, `Advance(double seconds)`,
    `Seconds(int count)`, `RunQueued()`, `TurnOn(int? minutes = null)`, `QueuedJobs`, `Population(int bots)`,
    `Slot()`, `Charge(long[] slot, BotCpuKind kind, BotCpuStage stage, double ms, long calls)`, `ChargeBot(GameObject
    bot, double ms, long calls, BotCpuKind kind = Gamebot)`, `Bot(string name)`, `PutInRegion(GameObject obj, ushort
    id)`, `Logged()`, `AssertOff()`, `GamebotLine(int win, string msSec, string msPerBotSec, string callsPerBotSec)`
    and `ReturnsWithTheLockHeld(Action command)`. `[SetUp]` starts the test clock at 14:02:00 local.

Two readings of 3.7 decide the code. The off and reset jobs act on the run open when they run, not on the run open at
the command; a tick carries its own run as its timer's state and does nothing unless that run is still the open one.
That is how "on then off" queued together ends off, and why an off for a run already closed writes nothing. And an off
command's final window ends at the command's instant, when metering stopped, not when its job runs: the command takes
the instant on its three clocks (no file, `/proc` read or log write), and the reply and the job agree on the 5 s rule.

- [ ] **Step 1: Write the failing tests.** All of them go in `UT_BotCpuScript.cs`, in ten edits. Each old text occurs
once in the file as the edit before it leaves it. First the usings (`IEnumerable` and `RuntimeHelpers` for the test
double server, the database and server rules it needs, `DOL.Events` for the handlers' attributes, `DOL.GS.PlayerClass`
for a player bot's class) and the header comment's last lines:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using DOL.GS.HearthDAoC;
using DOL.GS.ServerProperties;
using DOL.Logging;
using NUnit.Framework;
=======
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DOL.Database;
using DOL.Events;
using DOL.GS.HearthDAoC;
using DOL.GS.PlayerClass;
using DOL.GS.ServerProperties;
using DOL.GS.ServerRules;
using DOL.Logging;
using NUnit.Framework;
>>>>>>> NEW
<<<<<<< OLD
// files, and appends the CSV (2.3), tested in a folder of each test's own.
=======
// files, and appends the CSV (2.3), tested in a folder of each test's own. Then the report side (3.7, 3.8, 4.1, 4.3):
// a command only turns the meter off or queues a job; the jobs, the 1-second timer's ticks and the server's started and
// stopped handlers run one at a time under the report lock, write each window once and never let an exception out.
// The tests hold the queued work items, tick a timer of their own and move a clock of their own (test hooks).
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (2 block(s))`.

Then the saved values, `[SetUp]` and `[TearDown]`. The log hook now takes a lock: the burst and the real timer write
from other threads. `[SetUp]` installs the report side's hooks: the queued work items are held in `_queued`, each timer
is a `TestTimer` the test ticks, the clock (`_now`, `_timestamp`) moves only when the test moves it, and the CSV goes in
the test's folder. It loads a test double server with an empty database, as `UT_BotCpuMeter` does, since
`GameObject`'s type initializer reads the database when a test makes a `GameBot`. It sets the game time, the tick
duration (30 ticks a second) and the registry's population, which `[TearDown]` restores. `[TearDown]` first disposes
any real timer a test left (the open run's, or the real-timer test's own) and waits for its callbacks, then takes the
report lock once, so no tick or work item of this test runs into the next; then it clears the static open run, result
and job queue:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
    private object _cgroupCpuStat;
=======
    private object _cgroupCpuStat;
    private GameServer _savedServer;
    private long _savedGameLoopTime;
    private double _savedTickDuration;
    private int _savedPopulation;
>>>>>>> NEW
<<<<<<< OLD
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) => _logs.Add((level, line))));
    }

    [TearDown]
    public void TearDown()
    {
=======
        // Locked: the burst test's jobs run on the thread pool.
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            lock (_logs)
                _logs.Add((level, line));
        }));

        // The report side's hooks: the queued work items are held, the 1-second timer is the test's own, the clock
        // moves only when the test moves it, and the CSV is in the test's folder.
        _queued.Clear();
        _timers.Clear();
        _now = new DateTime(2026, 10, 18, 14, 2, 0, DateTimeKind.Local);
        _timestamp = 1_000 * Stopwatch.Frequency;
        QueueHook.SetValue(null, (Action<WaitCallback>)(work => _queued.Add(work)));
        TimerHook.SetValue(null, (Func<TimerCallback, object, IDisposable>)((callback, state) =>
        {
            TestTimer timer = new(callback, state);
            lock (_timers)
                _timers.Add(timer);
            return timer;
        }));
        NowHook.SetValue(null, (Func<DateTime>)(() => _now));
        TimestampHook.SetValue(null, (Func<long>)(() => _timestamp));
        CsvFileHook.SetValue(null, Csv);

        // A GameBot is made without its constructor, but GameObject's type initializer reads the database: a test
        // double server with an empty one, as UT_BotCpuMeter does. The game time grows by the test's clock at 30
        // ticks a second, and the registry's sampled population is the test's (3.8).
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        _savedGameLoopTime = GameLoop.GameLoopTime;
        _savedTickDuration = GameLoop.TickDuration;
        _savedPopulation = (int)PopulationField.GetValue(null);
        GameLoopTimeProperty.SetValue(null, 5_000_000L);
        TickDurationProperty.SetValue(null, 1000.0 / 30);
        Population(0);
        BotCpuScript.BOT_CPU_METER = false;
    }

    [TearDown]
    public void TearDown()
    {
        // No tick of a real timer runs into the next test: the open run's, which a failing test leaves ticking, and
        // the real-timer test's own are disposed and their callbacks waited for. A callback queued after Dispose finds
        // its timer disposed and never runs; one already running holds the report lock, taken here once.
        if (OpenRunTimer() is System.Threading.Timer open)
            _realTimers.Add(open);
        foreach (System.Threading.Timer timer in _realTimers)
            timer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        _realTimers.Clear();
        lock (ReportLockField.GetValue(null)!)
        {
            // Nothing: the lock is only waited for.
        }

        // No run, job or result is left for the next test.
        OpenRunField.SetValue(null, null);
        ResultField.SetValue(null, BotCpuResult.Off);
        object jobs = JobsField.GetValue(null);
        jobs.GetType().GetMethod("Clear")!.Invoke(jobs, null);
        QueueHook.SetValue(null, null);
        TimerHook.SetValue(null, null);
        NowHook.SetValue(null, null);
        TimestampHook.SetValue(null, null);
        ZoneHook.SetValue(null, null);
        CsvFileHook.SetValue(null, null);
        BotCpuScript.BOT_CPU_METER = false;
        GameLoopTimeProperty.SetValue(null, _savedGameLoopTime);
        TickDurationProperty.SetValue(null, _savedTickDuration);
        Population(_savedPopulation);
        GameServer.LoadTestDouble(_savedServer);

>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (2 block(s))`.

Then the hooks and helpers, at the class's end. The tests reach the script's private members by reflection (no
`InternalsVisibleTo`), and GameLoop's two properties through their private setters. `EmptyServer` and `EmptyReads` are
`UT_BotCpuMeter`'s test double: a `DispatchProxy` database whose every read returns an empty list or null.
`TestTimer.Tick()` calls the report's callback with its run; a disposed one can still tick, as a real timer's callback
already queued can. `Advance(seconds)` moves the local time, the Stopwatch timestamp and the game time together (1,000
ms of game time a second: 30 ticks of 1000/30 ms); `Second()` advances one second and ticks the latest timer;
`RunQueued()` runs the held work items in order. `Slot()` adds a slot of the test's own to the meter's slot list, which
exists once the meter was on, and `Charge` and `ChargeBot` add to it and to a bot's counter as the wrapper does.
`Bot<T>(name)` makes a `GameBot` without its constructor (level 34, a Cleric, an autonomous world bot).
`BotWhoseNameThrows` makes the top's read of a bot throw, and `BotInACollapsingInstance` gives its region once, then
null. `Logged()` cuts each `BOT_CPU_PROCESS` line to its first two fields, since its values are the test process's own:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(old + "\nrow 3\n"));
        Assert.That(File.Exists(Csv + ".1"), Is.False);
    }
}
=======
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(old + "\nrow 3\n"));
        Assert.That(File.Exists(Csv + ".1"), Is.False);
    }

    // ---- The report side (3.7, 3.8, 4.1, 4.3, 4.4): its test hooks and the meter's state it reads

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo QueueHook = typeof(BotCpuScript).GetField("_queueHook", HiddenStatic)!;
    private static readonly FieldInfo TimerHook = typeof(BotCpuScript).GetField("_timerHook", HiddenStatic)!;
    private static readonly FieldInfo NowHook = typeof(BotCpuScript).GetField("_nowHook", HiddenStatic)!;
    private static readonly FieldInfo TimestampHook = typeof(BotCpuScript).GetField("_timestampHook", HiddenStatic)!;
    private static readonly FieldInfo ZoneHook = typeof(BotCpuScript).GetField("_zoneHook", HiddenStatic)!;
    private static readonly FieldInfo CsvFileHook = typeof(BotCpuScript).GetField("_csvFile", HiddenStatic)!;
    private static readonly FieldInfo OpenRunField = typeof(BotCpuScript).GetField("_open", HiddenStatic)!;
    private static readonly FieldInfo ResultField = typeof(BotCpuScript).GetField("_result", HiddenStatic)!;
    private static readonly FieldInfo JobsField = typeof(BotCpuScript).GetField("Jobs", HiddenStatic)!;
    private static readonly FieldInfo ReportLockField = typeof(BotCpuScript).GetField("ReportLock", HiddenStatic)!;
    private static readonly FieldInfo SlotsField = typeof(BotCpuMeter).GetField("_slots", HiddenStatic)!;
    private static readonly FieldInfo CountersField = typeof(BotCpuMeter).GetField("_counters", HiddenStatic)!;
    private static readonly FieldInfo PopulationField =
        typeof(AutonomousBotRegistry).GetField("_populationForBrainTick", HiddenStatic)!;
    private static readonly PropertyInfo GameLoopTimeProperty =
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime))!;
    private static readonly PropertyInfo TickDurationProperty =
        typeof(GameLoop).GetProperty(nameof(GameLoop.TickDuration))!;

    private const string Gm = "Aldwin";
    private const BotCpuKind Gamebot = BotCpuKind.Gamebot;
    private const BotCpuStage Npc = BotCpuStage.Npc;
    private const string OnLine = "BOT_CPU_STATE on source=command by=Aldwin minutes=-";
    private const string OffLine = "BOT_CPU_STATE off source=command by=Aldwin";

    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    public class EmptyReads : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            Type result = method.ReturnType;
            if (result.IsGenericType && typeof(IEnumerable).IsAssignableFrom(result))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GetGenericArguments()[0]));
            return null;
        }
    }

    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();

    // The work items the commands queue (3.7), held until the test runs them.
    private readonly List<WaitCallback> _queued = [];

    // The 1-second timers the report made, in order: a tick is the test's call; a disposed timer can still tick once,
    // as a real timer's callback already queued does.
    private readonly List<TestTimer> _timers = [];

    private sealed class TestTimer(TimerCallback callback, object state) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Tick() => callback(state);

        public void Dispose() => Disposed = true;
    }

    // The report's own System.Threading.Timers a test made (no timer hook): TearDown waits for their callbacks.
    private readonly List<System.Threading.Timer> _realTimers = [];

    // The open run's timer, or null with no run open.
    private static IDisposable OpenRunTimer()
    {
        object run = OpenRunField.GetValue(null);
        return (IDisposable)run?.GetType().GetField("Timer", Hidden)!.GetValue(run);
    }

    // The report side's clock: the server's local time and the Stopwatch timestamp, moved together.
    private DateTime _now;
    private long _timestamp;

    private static DateTime At(int hour, int minute, int second, int ms = 0) =>
        new(2026, 10, 18, hour, minute, second, ms, DateTimeKind.Local);

    // The clock and the game time move on by seconds, at 30 game ticks a second (3.8); no timer ticks.
    private void Advance(double seconds)
    {
        _now = _now.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        _timestamp += (long)Math.Round(seconds * Stopwatch.Frequency);
        GameLoopTimeProperty.SetValue(null, GameLoop.GameLoopTime + (long)Math.Round(seconds * 1000));
    }

    private TestTimer LastTimer
    {
        get
        {
            lock (_timers)
                return _timers[^1];
        }
    }

    // One second of the meter: the clock moves on, then the latest timer ticks.
    private void Second()
    {
        Advance(1);
        LastTimer.Tick();
    }

    private void Seconds(int count)
    {
        for (int i = 0; i < count; i++)
            Second();
    }

    // The thread pool runs the held work items, in the order they were queued.
    private void RunQueued()
    {
        WaitCallback[] work = [.. _queued];
        _queued.Clear();
        foreach (WaitCallback item in work)
            item(null);
    }

    // A GM's /botcpu on, and its job run.
    private void TurnOn(int? minutes = null)
    {
        BotCpuScript.CommandOn(Gm, minutes);
        RunQueued();
    }

    private static int QueuedJobs
    {
        get
        {
            object jobs = JobsField.GetValue(null);
            return (int)jobs.GetType().GetProperty("Count")!.GetValue(jobs)!;
        }
    }

    private static void Population(int bots) => PopulationField.SetValue(null, bots);

    // A slot of the test's own, registered as a loop thread's is (3.7): the meter's slot list exists once it was on.
    private static long[] Slot()
    {
        long[] slot = new long[BotCpuTotals.SlotLength];
        List<long[]> slots = (List<long[]>)SlotsField.GetValue(null);
        lock (slots)
            slots.Add(slot);
        return slot;
    }

    private static long StopwatchTicks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);

    // As the wrapper adds to its thread's slot (3.2, step 5): ms of time and calls charged to [kind, stage].
    private static void Charge(long[] slot, BotCpuKind kind, BotCpuStage stage, double ms, long calls)
    {
        int cell = BotCpuTotals.CellIndex(kind, stage);
        slot[cell] += StopwatchTicks(ms);
        slot[cell + 1] += calls;
    }

    // As the wrapper adds to a bot's counter in the run's per-bot table (3.10), made with the bot's kind.
    private static void ChargeBot(GameObject bot, double ms, long calls, BotCpuKind kind = Gamebot)
    {
        ConditionalWeakTable<GameObject, BotCpuCounter> table =
            (ConditionalWeakTable<GameObject, BotCpuCounter>)CountersField.GetValue(null);
        BotCpuCounter counter = table.GetValue(bot, _ => new BotCpuCounter(kind));
        counter.Ticks += StopwatchTicks(ms);
        counter.Calls += calls;
    }

    // An autonomous GameBot as the top lists it: its name, level 34, a Cleric, no region and no PersistentRecord.
    private static T Bot<T>(string name) where T : GameBot
    {
        T bot = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(bot, name);
        typeof(GameObject).GetField("m_level", Hidden)!.SetValue(bot, (byte)34);
        typeof(GameBot).GetField("<ClassName>k__BackingField", Hidden)!.SetValue(bot, "Cleric");
        typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", Hidden)!.SetValue(bot, true);
        return bot;
    }

    private static GameBot Bot(string name) => Bot<GameBot>(name);

    // A region of that id, made without its constructor.
    private static Region RegionOf(ushort id)
    {
        Region region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
        typeof(Region).GetField("m_regionData", Hidden)!.SetValue(region, new RegionData { Id = id });
        return region;
    }

    // An object put in a region of that id, as the top reads it (CurrentRegion).
    private static void PutInRegion(GameObject obj, ushort id) =>
        typeof(GameObject).GetField("<CurrentRegion>k__BackingField", Hidden)!.SetValue(obj, RegionOf(id));

    // A bot whose name cannot be read: the top reads another thread's objects (3.10). Made without its constructor.
    private sealed class BotWhoseNameThrows : GameBot
    {
        private BotWhoseNameThrows() : base((OfflineWorldBotRecord)null) { }

        public override string Name
        {
            get => throw new InvalidOperationException("name");
            set { }
        }
    }

    // A bot in an instance that collapses as the top reads it: Region.OnCollapse sets the region of each object in it
    // to null on the loop (world/Region.cs:270), here once the top has read it once. Made without its constructor.
    private sealed class BotInACollapsingInstance : GameBot
    {
        private BotInACollapsingInstance() : base((OfflineWorldBotRecord)null) { }

        internal Region Collapsing;

        public override Region CurrentRegion
        {
            get => Interlocked.Exchange(ref Collapsing, null);
            set { }
        }
    }

    // The meter's log lines, in order; a process line cut to its first two fields, as its values are the test
    // process's own.
    private List<string> Logged()
    {
        lock (_logs)
        {
            return _logs.Select(log => log.Line.StartsWith("BOT_CPU_PROCESS ", StringComparison.Ordinal)
                ? string.Join(' ', log.Line.Split(' ').Take(2))
                : log.Line).ToList();
        }
    }

    private string ProcessLine()
    {
        lock (_logs)
            return _logs.Single(log => log.Line.StartsWith("BOT_CPU_PROCESS ", StringComparison.Ordinal)).Line;
    }

    private bool AllTimersDisposed()
    {
        lock (_timers)
            return _timers.All(timer => timer.Disposed);
    }

    // 3.7: the first exception turns the meter off: Enabled false, the timer disposed, the run closed with no final
    // window, one Warn line BOT_CPU_ERROR <step>: <exception>, then BOT_CPU_STATE off source=error, and no more.
    private void AssertTurnedOffByError(string step, string exception)
    {
        List<(ELogLevel Level, string Line)> logs;
        lock (_logs)
            logs = [.. _logs];
        Assert.That(logs.Where(log => log.Line.StartsWith("BOT_CPU_ERROR", StringComparison.Ordinal)),
            Has.Exactly(1).Items);
        Assert.That(logs[^2].Level, Is.EqualTo(ELogLevel.Warning));
        Assert.That(logs[^2].Line, Does.StartWith($"BOT_CPU_ERROR {step}: {exception}"));
        Assert.That(logs[^1], Is.EqualTo((ELogLevel.Info, "BOT_CPU_STATE off source=error")));
        Assert.That(logs.Where(log => log.Line.StartsWith("BOT_CPU win=", StringComparison.Ordinal)), Is.Empty);
        AssertOff();
    }

    // Off: Enabled false, no run open, every timer disposed, and the published result says so.
    private void AssertOff()
    {
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(OpenRunField.GetValue(null), Is.Null);
        Assert.That(AllTimersDisposed(), Is.True);
        Assert.That(BotCpuScript.Result.On, Is.False);
    }

    // The gamebot line of a window with 3 GameBots registered and ms charged to it in Npc at 30 calls a bot-second,
    // no bot charged.
    private static string GamebotLine(int win, string msSec, string msPerBotSec, string callsPerBotSec) =>
        $"BOT_CPU win={win} kind=gamebot n=3.0 msSec={msSec} msPerBotSec={msPerBotSec} " +
        $"callsPerBotSec={callsPerBotSec} usPerCall=33.33 allocKBPerBotSec=0.0 p50=- p95=- max=- | Npc={msPerBotSec}";
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

Off and on from a command (6.1). A command must do nothing but `Disable` (for off) and queue: with `_procSelf` null, a
`/proc` read would throw out of the command, so a command that returns did none; no log line and no CSV are checked
directly. `TheCommandsNeverWriteTheProperty` runs off, on, on 30, reset and off with the property on and off (4.3). The
off command's final window ends at the command, 30 s in, though its job runs 10 s later; the reply's seconds and the job
agree under 5 s; and a job that runs late divides the process rates by the time between their own two samples (20 s of
the loop thread's CPU over 40 s is 50% of a core):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        $"BOT_CPU win={win} kind=gamebot n=3.0 msSec={msSec} msPerBotSec={msPerBotSec} " +
        $"callsPerBotSec={callsPerBotSec} usPerCall=33.33 allocKBPerBotSec=0.0 p50=- p95=- max=- | Npc={msPerBotSec}";
}
=======
        $"BOT_CPU win={win} kind=gamebot n=3.0 msSec={msSec} msPerBotSec={msPerBotSec} " +
        $"callsPerBotSec={callsPerBotSec} usPerCall=33.33 allocKBPerBotSec=0.0 p50=- p95=- max=- | Npc={msPerBotSec}";

    // ---- Off and on from a command (3.7, 6.1)

    [Test]
    public void OffFromACommandTurnsTheMeterOffAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        TurnOn();
        lock (_logs)
            _logs.Clear();
        // A /proc read would throw here: the process sample has no folder to read.
        ProcSelfHook.SetValue(null, null);

        BotCpuScript.CommandOff(Gm);

        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(BotCpuScript.Result.On, Is.True, "the report has not run the job yet");

        // The job, on the report side: it closes the run, whose last 0 s are too short to write (2.2).
        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { OffLine }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
        AssertOff();
    }

    [Test]
    public void OnFromACommandLeavesEnabledAsItIsAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        ProcSelfHook.SetValue(null, null);

        DateTime? stopsAt = BotCpuScript.CommandOn(Gm, 30);

        Assert.That(stopsAt, Is.EqualTo(At(14, 32, 0)));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(_timers, Is.Empty);
        Assert.That(BotCpuScript.Result.On, Is.False);

        // The job opens the run: Enabled goes true only there (3.7).
        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.On, Is.True);
        Assert.That(result.Run, Is.EqualTo(BotCpuMeter.RunNumber));
        Assert.That(result.ByCommand, Is.True);
        Assert.That(result.OnSince, Is.EqualTo(At(14, 2, 0)));
        Assert.That(result.StopsAt, Is.EqualTo(At(14, 32, 0)));
        Assert.That(result.WindowStart, Is.EqualTo(At(14, 2, 0)));
        Assert.That(result.Last, Is.Null);
        Assert.That(_timers, Has.Count.EqualTo(1));

        // On a running meter: Enabled stays true, one job queued.
        ProcSelfHook.SetValue(null, null);
        BotCpuScript.CommandOn(Gm, null);
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Has.Count.EqualTo(1));
    }

    [Test]
    public void ResetFromACommandOnlyQueuesAJob()
    {
        TurnOn();
        BotCpuScript.CommandReset();

        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(BotCpuScript.Result.Since.Reset, Is.False, "the report has not run the job yet");
        RunQueued();
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }), "reset writes no line");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheCommandsNeverWriteTheProperty(bool property)
    {
        // 4.3: /botcpu on and off override the property until the next start; they never write it. Off, then on, a
        // new stop time, reset and off, each job run, on a meter the property started or left off.
        BotCpuScript.BOT_CPU_METER = property;
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);

        foreach (Action command in new Action[]
        {
            () => BotCpuScript.CommandOff(Gm), () => BotCpuScript.CommandOn(Gm, null),
            () => BotCpuScript.CommandOn(Gm, 30), BotCpuScript.CommandReset, () => BotCpuScript.CommandOff(Gm),
        })
        {
            command();
            RunQueued();
            Assert.That(BotCpuScript.BOT_CPU_METER, Is.EqualTo(property));
        }

        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU_STATE ", StringComparison.Ordinal)),
            Is.EqualTo(property ? 5 : 3));
        AssertOff();
    }

    [Test]
    public void TheFinalWindowEndsWhenTheOffCommandStoppedTheMeter()
    {
        // 2.2, 3.7: off stops metering at the command. Its job, run 10 s later here, ends the final window at the
        // command's instant: 30 s on each clock (the window's length, its game ticks, its CSV time), the seconds the
        // command gives for its reply.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 90, 2_700);
        Seconds(30);

        double? seconds = BotCpuScript.CommandOff(Gm);
        Advance(10);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(30, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=30", OffLine }));
        Assert.That(ProcessLine(), Does.StartWith("BOT_CPU_PROCESS win=30 tps=30.0 "));
        Assert.That(File.ReadAllLines(Csv)[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 2, 30)) + ",30,30.0,"));
        Assert.That(seconds, Is.EqualTo(30).Within(1e-9));
        AssertOff();
    }

    [Test]
    public void TheOffReplyAndItsJobAgreeOnAWindowUnderFiveSeconds()
    {
        // 2.2, 2.4: 4.9 s into the window the command's seconds are under 5 s ("too short to write"); its job, run
        // after the 5 s mark, writes no window either.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 90, 2_700);
        Seconds(4);
        Advance(0.9);

        double? seconds = BotCpuScript.CommandOff(Gm);
        Advance(1);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine }));
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(seconds, Is.EqualTo(4.9).Within(1e-9));
        AssertOff();
    }

    [Test]
    public void AnOffJobThatRunsLateGivesTheProcessValuesOverTheirOwnSeconds()
    {
        // 2.2, 3.9: the final window ends at the off command, 30 s in, but its process values can only be read when its
        // job runs, off the loop, here 10 s later. Their rates divide by the time between their own two samples: the
        // loop thread's 20 s of CPU over those 40 s is 50% of one core, not 66.7% over the window's 30 s.
        string proc = Path.Combine(_folder, "proc");
        SampleThread(proc, "101", "GameLoop\n", "10000000000 0 1\n");
        ProcSelfHook.SetValue(null, proc);
        TurnOn();
        Seconds(30);

        BotCpuScript.CommandOff(Gm);
        Advance(10);
        SampleThread(proc, "101", "GameLoop\n", "30000000000 0 2\n");
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=30", OffLine }));
        Assert.That(ProcessLine(), Does.Contain(" loopThreadsCpuPct=50.0 "));
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

A window and reset (3.7, 3.8). `AWindowIsWrittenAtTheFirstTickAtOrAfterItsSixtySeconds` pins the boundary (nothing at
59 ticks, the window at the 60th), the kind, top and process lines, the CSV row with the window's end in UTC and the
published result. `TheTopListsEachBotWithItsCountersKindAndWhatTheChatShowsOfIt` pins every field the top reads: a
`GameBot` with a `PersistentRecord` (its line ends `:Rvr`), one without, a companion, and a `GamePlayer` player bot
whose class is its character class, each under its counter's kind. Review Focus 3 removes a bot's counter mid-window,
Review Focus 4 points `/proc` and the cgroup file at missing paths, both through a tick. A final window with no tick
keeps the last registry sample (2.2):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(ProcessLine(), Does.Contain(" loopThreadsCpuPct=50.0 "));
        AssertOff();
    }
}
=======
        Assert.That(ProcessLine(), Does.Contain(" loopThreadsCpuPct=50.0 "));
        AssertOff();
    }

    // ---- A window (3.7, 3.8)

    [Test]
    public void AWindowIsWrittenAtTheFirstTickAtOrAfterItsSixtySeconds()
    {
        // 3 GameBots registered; 180 ms charged to gamebot in Npc over 5,400 calls; Aldric, Brennos and Cathal charged
        // 90, 60 and 30 ms.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        GameBot aldric = Bot("Aldric");
        GameBot brennos = Bot("Brennos");
        GameBot cathal = Bot("Cathal");
        ChargeBot(aldric, 90, 1_800);
        ChargeBot(brennos, 60, 1_800);
        ChargeBot(cathal, 30, 1_800);

        Seconds(59);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }));

        Second();
        // The slots' adds since on, the registry sampled each second, the game ticks, the bots of the per-bot table.
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine,
            "BOT_CPU win=60 kind=gamebot n=3.0 msSec=3.0 msPerBotSec=1.000 callsPerBotSec=30.0 usPerCall=33.33 " +
            "allocKBPerBotSec=0.0 p50=1.000 p95=1.500 max=1.500 | Npc=1.000",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:1.500:- 2=Brennos:1.000:- 3=Cathal:0.500:-",
            "BOT_CPU_PROCESS win=60",
        }));
        Assert.That(ProcessLine(),
            Does.StartWith($"BOT_CPU_PROCESS win=60 tps=30.0 cores={Environment.ProcessorCount} ")
                .And.Contain(" chargedPct=0.3 callsSec=90 "));

        // Its CSV row, with the window's end in UTC.
        string[] csv = File.ReadAllLines(Csv);
        Assert.That(csv, Has.Length.EqualTo(2));
        Assert.That(csv[0], Is.EqualTo(BotCpuReport.CsvHeader));
        Assert.That(csv[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 3, 0)) + ",60,30.0,"));
        Assert.That(csv[1], Does.EndWith(",gamebot,3.0,3.0,90.0,0.0,1.000,30.0,33.33,0.0,1.000,1.500,1.500," +
            "0.000,0.000,0.000,3.000" + string.Concat(Enumerable.Repeat(",0.000", 14))));

        // Published for /botcpu: the last window, the totals since on and the open window's start.
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.On, Is.True);
        Assert.That(result.Last.Win, Is.EqualTo(60));
        Assert.That(result.Last.Top(Gamebot).Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Aldric", "Brennos", "Cathal" }));
        Assert.That(result.LastProcess, Is.Not.Null);
        Assert.That(result.Since.Windows, Is.EqualTo(1));
        Assert.That(result.Since.MsPerBotSec(Gamebot), Is.EqualTo(1.0).Within(1e-9));
        Assert.That(result.WindowStart, Is.EqualTo(At(14, 3, 0)));
        Assert.That(result.OnSince, Is.EqualTo(At(14, 2, 0)));
        GC.KeepAlive(new[] { aldric, brennos, cathal });
    }

    [Test]
    public void ABotThatLeavesTheWorldMidWindowLeavesNoStaleEntry()
    {
        // Review Focus 3, through a tick: Brennos is charged in both windows, then leaves the world; its counter is
        // collected, or it is simply absent from the next listing.
        Population(3);
        TurnOn();
        GameBot aldric = Bot("Aldric");
        GameBot brennos = Bot("Brennos");
        GameBot cathal = Bot("Cathal");
        ChargeBot(aldric, 60, 1_800);
        ChargeBot(brennos, 600, 1_800);
        ChargeBot(cathal, 120, 1_800);
        Seconds(60);
        Assert.That(BotCpuScript.Result.Last.Top(Gamebot).Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Brennos", "Cathal", "Aldric" }));

        ChargeBot(aldric, 300, 1_800);
        ChargeBot(brennos, 6_000, 1_800);
        ChargeBot(cathal, 30, 1_800);
        ((ConditionalWeakTable<GameObject, BotCpuCounter>)CountersField.GetValue(null)).Remove(brennos);
        Population(2);
        Assert.That(() => Seconds(60), Throws.Nothing);

        // The window, the spread and the top 5 are written from the bots present, with their adds since the last read.
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU_TOP ", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            "BOT_CPU_TOP win=60 kind=gamebot 1=Brennos:10.000:- 2=Cathal:2.000:- 3=Aldric:1.000:-",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:5.000:- 2=Cathal:0.500:-",
        }));
        // n is this window's registry samples only: 2 GameBots now, 3 before.
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU win=", StringComparison.Ordinal)).Last(),
            Does.StartWith("BOT_CPU win=60 kind=gamebot n=2.0 ").And.Contain(" p50=0.500 p95=5.000 max=5.000"));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        GC.KeepAlive(new[] { aldric, brennos, cathal });
    }

    [Test]
    public void TheTopListsEachBotWithItsCountersKindAndWhatTheChatShowsOfIt()
    {
        // 3.10, 2.1 BOT_CPU_TOP and 2.4 /botcpu top: each bot of the per-bot table under its counter's kind, with its
        // name, level, class, region, and for a GameBot with a PersistentRecord its goal and activity. Aldric has a
        // record and a region; Brennos neither; Wynn is a companion (a GameBot that is no autonomous world bot) and
        // Tamsin a player bot, a GamePlayer, whose class is its character class.
        Population(3);
        TurnOn();
        GameBot aldric = Bot("Aldric");
        typeof(GameBot).GetField("<PersistentRecord>k__BackingField", Hidden)!.SetValue(aldric,
            new OfflineWorldBotRecord { ObjectiveKind = "Rvr", Activity = "Guarding Caer Benowyc" });
        PutInRegion(aldric, 163);
        GameBot brennos = Bot("Brennos");
        GameBot wynn = Bot("Wynn");
        typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", Hidden)!.SetValue(wynn, false);
        GamePlayer tamsin = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(tamsin, "Tamsin");
        typeof(GameObject).GetField("m_level", Hidden)!.SetValue(tamsin, (byte)20);
        typeof(GamePlayer).GetField("m_characterClass", Hidden)!.SetValue(tamsin, new ClassFriar());
        PutInRegion(tamsin, 1);
        ChargeBot(aldric, 90, 1_800);
        ChargeBot(brennos, 60, 1_800);
        ChargeBot(wynn, 30, 1_800, BotCpuKind.Companion);
        ChargeBot(tamsin, 120, 1_800, BotCpuKind.Playerbot);

        Seconds(60);

        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU_TOP ", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:1.500:Rvr 2=Brennos:1.000:-",
            "BOT_CPU_TOP win=60 kind=companion 1=Wynn:0.500",
            "BOT_CPU_TOP win=60 kind=playerbot 1=Tamsin:2.000",
        }));
        BotCpuWindow last = BotCpuScript.Result.Last;
        Assert.That(last.Top(Gamebot).Select(entry => entry.Bot), Is.EqualTo(new[]
        {
            new BotCpuBot(Gamebot, "Aldric", 34, "Cleric", 163, "Rvr", "Guarding Caer Benowyc", StopwatchTicks(90),
                1_800),
            new BotCpuBot(Gamebot, "Brennos", 34, "Cleric", 0, null, null, StopwatchTicks(60), 1_800),
        }));
        Assert.That(last.Top(BotCpuKind.Companion).Select(entry => entry.Bot), Is.EqualTo(new[]
            { new BotCpuBot(BotCpuKind.Companion, "Wynn", 34, "Cleric", 0, null, null, StopwatchTicks(30), 1_800) }));
        Assert.That(last.Top(BotCpuKind.Playerbot).Select(entry => entry.Bot), Is.EqualTo(new[]
            { new BotCpuBot(BotCpuKind.Playerbot, "Tamsin", 20, "Friar", 1, null, null, StopwatchTicks(120), 1_800) }));
        Assert.That(last.Top(BotCpuKind.Piloted), Is.Empty);
        GC.KeepAlive(new GameObject[] { aldric, brennos, wynn, tamsin });
    }

    [Test]
    public void ABotWhoseInstanceCollapsesAsTheTopReadsItIsListedWithTheRegionItRead()
    {
        // 3.10: the top reads another thread's objects without a lock, a value a moment old being harmless. Eldrin's
        // instance collapses on the loop while the top reads it: the region it read is listed, and the meter goes on.
        Population(1);
        TurnOn();
        BotInACollapsingInstance eldrin = Bot<BotInACollapsingInstance>("Eldrin");
        eldrin.Collapsing = RegionOf(163);
        ChargeBot(eldrin, 90, 1_800);

        Seconds(60);

        Assert.That(Logged(), Has.None.StartWith("BOT_CPU_ERROR"));
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU_TOP ", StringComparison.Ordinal)),
            Is.EqualTo(new[] { "BOT_CPU_TOP win=60 kind=gamebot 1=Eldrin:1.500:-" }));
        Assert.That(BotCpuScript.Result.Last.Top(Gamebot).Select(entry => entry.Bot), Is.EqualTo(new[]
            { new BotCpuBot(Gamebot, "Eldrin", 34, "Cleric", 163, null, null, StopwatchTicks(90), 1_800) }));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        GC.KeepAlive(eldrin);
    }

    [Test]
    public void ATickWithEveryPlatformReaderFailingStillWritesTheKindAndTopLinesAndTheRows()
    {
        // Review Focus 4: no /proc and no cgroup (Windows, or every read failing): those fields are - and the rest is
        // written as usual.
        ProcSelfHook.SetValue(null, Path.Combine(_folder, "no-proc"));
        CgroupCpuStatHook.SetValue(null, Path.Combine(_folder, "no-cgroup", "cpu.stat"));
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        GameBot aldric = Bot("Aldric");
        ChargeBot(aldric, 90, 1_800);

        Seconds(60);

        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine,
            "BOT_CPU win=60 kind=gamebot n=3.0 msSec=3.0 msPerBotSec=1.000 callsPerBotSec=30.0 usPerCall=33.33 " +
            "allocKBPerBotSec=0.0 p50=1.500 p95=1.500 max=1.500 | Npc=1.000",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:1.500:-",
            "BOT_CPU_PROCESS win=60",
        }));
        Assert.That(ProcessLine(), Does.Contain(" loopThreadsCpuPct=- offLoopCpuPct=- campThreadCpuPct=- ")
            .And.Contain(" loopWaitMs=- throttledMs=- ").And.EndWith(" rssMB=-"));
        string[] csv = File.ReadAllLines(Csv);
        Assert.That(csv, Has.Length.EqualTo(2));
        string[] header = csv[0].Split(',');
        string[] row = csv[1].Split(',');
        foreach (string column in new[] { "loop_threads_cpu_pct", "off_loop_cpu_pct", "camp_thread_cpu_pct",
            "loop_wait_ms", "throttled_ms", "rss_mb" })
            Assert.That(row[Array.IndexOf(header, column)], Is.Empty, column);
        Assert.That(row[Array.IndexOf(header, "kind")], Is.EqualTo("gamebot"));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        GC.KeepAlive(aldric);
    }

    [Test]
    public void AFinalWindowWithNoTickKeepsTheLastRegistrySample()
    {
        // 2.2: GameBot n in a window with no registry sample uses the last sample. The minute's tick samples 3; then
        // the registry reads 5, but no tick samples it before the off job closes the last 10 s: n is still 3.
        Population(3);
        TurnOn();
        Seconds(60);
        Population(5);
        Advance(10);

        BotCpuScript.CommandOff(Gm);
        RunQueued();

        string window = "kind=gamebot n=3.0 msSec=0.0 msPerBotSec=0.000 callsPerBotSec=0.0 usPerCall=- " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=-";
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine, "BOT_CPU win=60 " + window, "BOT_CPU_PROCESS win=60",
            "BOT_CPU win=10 " + window, "BOT_CPU_PROCESS win=10", OffLine,
        }));
    }

    [Test]
    public void ResetStartsTheTotalsSinceOnAgainAndTheWindowsGoOn()
    {
        Population(3);
        TurnOn();
        long[] slot = Slot();
        Charge(slot, Gamebot, Npc, 180, 5_400);
        Seconds(60);
        Charge(slot, Gamebot, Npc, 540, 16_200);
        Seconds(60);
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(2));
        Assert.That(BotCpuScript.Result.Since.MsPerBotSec(Gamebot), Is.EqualTo(2.0).Within(1e-9));

        BotCpuScript.CommandReset();
        RunQueued();
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(0));

        Charge(slot, Gamebot, Npc, 900, 27_000);
        Seconds(60);
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(1));
        Assert.That(BotCpuScript.Result.Since.MsPerBotSec(Gamebot), Is.EqualTo(5.0).Within(1e-9));
        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU win=", StringComparison.Ordinal)), Is.EqualTo(3));
    }

    [Test]
    public void ResetWithTheMeterOffDoesNothing()
    {
        BotCpuScript.CommandReset();
        RunQueued();

        Assert.That(Logged(), Is.Empty);
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

One at a time (3.7, 6.1), the order of ticks and jobs. `_timers[0].Tick()` plays a tick that waited, after the run it
belongs to closed. `AnOffWhoseMinuteATickClosedBeforeItsJobWasQueuedEndsTheRunWithNoFinalWindow` wraps the Stopwatch
hook so the minute's tick runs inside `CommandOff`, between the command's instant and its job's place in the queue.
`AnOffQueuedAsTheStopTimeClosesTheRunDoesNothing` calls `CommandOff` from the log hook when the tick writes
`off source=timer`, after that tick ran the jobs waiting:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(Logged(), Is.Empty);
        AssertOff();
    }
}
=======
        Assert.That(Logged(), Is.Empty);
        AssertOff();
    }

    // ---- One at a time (3.7, 6.1)

    [Test]
    public void AMinuteTickThenAnOffJobPutTheCountsInOneWindow()
    {
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);

        Seconds(60); // the minute's tick closes the window
        BotCpuScript.CommandOff(Gm);
        RunQueued(); // the off job: its last 0 s are too short to write (2.2)
        // A tick that waited, even a minute, finds the run closed: no window after off.
        Advance(60);
        _timers[0].Tick();

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void AnOffJobThenAWaitingTickPutTheCountsInOneWindowAndNothingAfterOff()
    {
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);

        Seconds(59);
        Advance(1);
        BotCpuScript.CommandOff(Gm);
        RunQueued(); // the off job writes the whole minute as the final window
        _timers[0].Tick(); // the minute's tick, already waiting, finds the run closed and writes nothing
        Advance(60);
        _timers[0].Tick(); // so does one that waited a minute more

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void ATickThatTakesTheLockFirstRunsTheJobsWaitingBeforeItsOwnWork()
    {
        // 3.7: whoever holds the lock first runs every job waiting, in order, then its own work.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);

        Seconds(59);
        Advance(1);
        BotCpuScript.CommandOff(Gm); // its work item has not run yet
        _timers[0].Tick(); // the tick runs the off job, then finds its run closed
        Assert.That(QueuedJobs, Is.EqualTo(0));
        RunQueued(); // the work item finds no job

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        AssertOff();
    }

    [Test]
    public void OffThenOnQueuedTogetherRunInOrder()
    {
        Population(3);
        TurnOn();
        int first = BotCpuMeter.RunNumber;
        long[] slot = Slot();
        Charge(slot, Gamebot, Npc, 180, 5_400); // the first run's
        Seconds(30);

        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOn(Gm, null);
        Assert.That(_queued, Has.Count.EqualTo(2));
        RunQueued(); // the first work item runs both jobs, in order; the second finds none

        // A final window, then a new run whose first window holds only its own adds.
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(first + 1));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(_timers, Has.Count.EqualTo(2));
        Assert.That(_timers[0].Disposed, Is.True);
        Assert.That(_timers[1].Disposed, Is.False);
        Charge(slot, Gamebot, Npc, 60, 1_800); // the second run's
        Seconds(60);
        // The first run's timer, disposed, ticks once more a minute later: it is not the open run's, so nothing.
        Advance(60);
        _timers[0].Tick();
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine, GamebotLine(30, "6.0", "2.000", "60.0"), "BOT_CPU_PROCESS win=30", OffLine,
            OnLine, GamebotLine(60, "1.0", "0.333", "10.0"), "BOT_CPU_PROCESS win=60",
        }));
        Assert.That(BotCpuScript.Result.Run, Is.EqualTo(first + 1));
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(1));
    }

    [Test]
    public void OnThenOffQueuedTogetherEndOff()
    {
        BotCpuScript.CommandOn(Gm, 30);
        Advance(1);
        // The published result is still off: no open window for the reply. The run its on job opens comes after
        // this off's instant, so its close, not the command, ends it.
        Assert.That(BotCpuScript.CommandOff(Gm), Is.Null);
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Advance(1);

        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OffLine }));
        Assert.That(_timers, Has.Count.EqualTo(1));
        AssertOff();
    }

    [Test]
    public void AnOffFromBeforeItsRunOpenedEndsTheRunAtItsClose()
    {
        // 3.7: on then off queued together; the off's instant comes before the run the on job opens, so that run is
        // metered until its close. Here the on job takes 10 s after its window starts (the timer hook moves the clock):
        // the final window is those 10 s, not the -1 s back to the command.
        Func<TimerCallback, object, IDisposable> timers =
            (Func<TimerCallback, object, IDisposable>)TimerHook.GetValue(null);
        TimerHook.SetValue(null, (Func<TimerCallback, object, IDisposable>)((callback, state) =>
        {
            Advance(10);
            return timers!(callback, state);
        }));
        BotCpuScript.CommandOn(Gm, null);
        Advance(1);
        BotCpuScript.CommandOff(Gm);
        Advance(1);

        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }

    [Test]
    public void AnOffWhoseMinuteATickClosedBeforeItsJobWasQueuedEndsTheRunWithNoFinalWindow()
    {
        // 3.7, 2.2: the minute's tick runs between the off command's instant and its job's place in the queue, so it
        // finds no job and starts a window after metering stopped. The job, run 10 s later, ends the run at the
        // command's instant, before that window started: only the off line follows the minute's lines, as the
        // command's seconds say ("too short to write"), not 10 s of a meter already off.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        Seconds(59);
        // The command reads the Stopwatch for its instant at 59 s; the tick at 60 s comes right after.
        Func<long> stopwatch = (Func<long>)TimestampHook.GetValue(null);
        bool ticked = false;
        TimestampHook.SetValue(null, (Func<long>)(() =>
        {
            long timestamp = stopwatch!();
            if (!ticked)
            {
                ticked = true;
                Second();
            }

            return timestamp;
        }));

        double? seconds = BotCpuScript.CommandOff(Gm);
        Advance(10);
        RunQueued();

        Assert.That(ticked, Is.True);
        Assert.That(seconds, Is.LessThan(BotCpuReport.MinimumWindowSeconds));
        Assert.That(BotCpuReport.OffText(seconds!.Value),
            Is.EqualTo("Bot CPU meter off. The last 0 s were too short to write."));
        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void TwoOffsQueuedTogetherCloseTheRunOnce()
    {
        // 3.7: an off job for a run already closed does nothing; 2.1: one BOT_CPU_STATE line per change. A GM types
        // /botcpu off twice before the first job runs.
        TurnOn();
        Seconds(10);

        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOff(Gm);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }

    [Test]
    public void AnOffQueuedAsTheStopTimeClosesTheRunDoesNothing()
    {
        // 3.7: a GM's off reaches the queue while the tick that reached the stop time is closing the run, after the
        // tick ran the jobs waiting: its job, run later, finds the run closed.
        Action<ELogLevel, string> log = (Action<ELogLevel, string>)LogHook.GetValue(null);
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            log!(level, line);
            if (line == "BOT_CPU_STATE off source=timer")
                BotCpuScript.CommandOff(Gm);
        }));
        TurnOn(1);

        Seconds(60);
        Assert.That(QueuedJobs, Is.EqualTo(1));
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[]
        {
            "BOT_CPU_STATE on source=command by=Aldwin minutes=1", "BOT_CPU_PROCESS win=60",
            "BOT_CPU_STATE off source=timer",
        }));
        AssertOff();
    }

    [Test]
    public void AnOffWithTheMeterOffDoesNothing()
    {
        Assert.That(BotCpuScript.CommandOff(Gm), Is.Null);
        RunQueued();

        Assert.That(Logged(), Is.Empty);
        Assert.That(_timers, Is.Empty);
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

One at a time, on other threads. The burst (Review Focus 5) runs its four work items on the real thread pool and waits
for all four on a `CountdownEvent`, never disposed, so a late `Signal` cannot throw. The lock tests hold the report lock
on a thread of their own and run the tick, the work item, the commands or a handler on a `Task` with a timeout, so a
regression fails an assert instead of hanging the run. `TheRealTimerTicksEverySecondUntilTheRunCloses` removes the
timer hook and waits for two windows of a real `System.Threading.Timer`, the second no sooner than 1.9 s after on, so
a one-shot timer or a shorter period fails it (about 2 s). `TheRealThreadPoolRunsEachCommandsJob` removes the queue
hook: each command's line comes with no tick, and the test takes the report lock after each, so no work item is left:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(_timers, Is.Empty);
        AssertOff();
    }
}
=======
        Assert.That(_timers, Is.Empty);
        AssertOff();
    }

    [Test]
    public void ABurstOfCommandsWritesOneStateLinePerChangeAndEndsOffWithItsTimerDisposed()
    {
        // Review Focus 5: on, off, on, off inside one second, the work items on the real thread pool. The test waits
        // for all four to end, so none is left to run a job of the next test. The count is never disposed: a work
        // item that ends after a failed wait still signals it.
        CountdownEvent ended = new(4);
        QueueHook.SetValue(null, (Action<WaitCallback>)(work => ThreadPool.QueueUserWorkItem(state =>
        {
            try
            {
                work(state);
            }
            finally
            {
                ended.Signal();
            }
        })));

        BotCpuScript.CommandOn(Gm, null);
        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOn(Gm, null);
        BotCpuScript.CommandOff(Gm);

        Assert.That(ended.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine, OnLine, OffLine }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(_timers, Has.Count.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void ATickThatFindsTheLockHeldSkipsItsSecond()
    {
        // 3.7: a tick takes the lock with Monitor.TryEnter and skips that second while it is held: its registry sample
        // of 100 is not taken, so the window's n is the other 59 seconds' 200.
        TurnOn();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        Population(100);
        Advance(1);
        // On a thread of its own, so a tick that waited for the lock fails here rather than hanging the test.
        System.Threading.Tasks.Task tick = System.Threading.Tasks.Task.Run(() => LastTimer.Tick());
        bool returned = tick.Wait(TimeSpan.FromSeconds(10));
        release.Set();
        holder.Join();
        tick.Wait();
        Assert.That(returned, Is.True, "the tick waited for the lock");

        Population(200);
        Seconds(59);

        Assert.That(Logged()[1], Does.StartWith("BOT_CPU win=60 kind=gamebot n=200.0 "));
    }

    [Test]
    public void AQueuedWorkItemWaitsForTheLock()
    {
        // 3.7: the queued work item takes the lock, waiting for it, where a tick would skip its second.
        TurnOn();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        bool offReturned = ReturnsWithTheLockHeld(() => BotCpuScript.CommandOff(Gm));

        System.Threading.Tasks.Task work = System.Threading.Tasks.Task.Run(RunQueued);
        bool waited = !work.Wait(TimeSpan.FromMilliseconds(200));
        int linesWhileHeld = Logged().Count;
        release.Set();
        holder.Join();
        work.Wait();

        Assert.That(offReturned, Is.True, "the command waited for the lock");
        Assert.That(waited, Is.True, "the work item did not wait for the lock");
        Assert.That(linesWhileHeld, Is.EqualTo(1));
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine }));
        AssertOff();
    }

    // A command on a thread of its own while another thread holds the report lock: whether it returned. The game-loop
    // thread never takes the lock (3.7); a command that did would wait, and fail the test here rather than hang it.
    private static bool ReturnsWithTheLockHeld(Action command) =>
        System.Threading.Tasks.Task.Run(command).Wait(TimeSpan.FromSeconds(10));

    [Test]
    public void NoCommandTakesTheReportLock()
    {
        // 3.7: a command only turns the meter off or queues a job, on its game-loop thread, which never takes the
        // report lock: held by another thread, on, reset and off each return at once, their jobs left waiting.
        TurnOn();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        bool onReturned = ReturnsWithTheLockHeld(() => BotCpuScript.CommandOn(Gm, 30));
        bool resetReturned = ReturnsWithTheLockHeld(BotCpuScript.CommandReset);
        bool offReturned = ReturnsWithTheLockHeld(() => BotCpuScript.CommandOff(Gm));
        release.Set();
        holder.Join();

        Assert.That(onReturned, Is.True, "on waited for the lock");
        Assert.That(resetReturned, Is.True, "reset waited for the lock");
        Assert.That(offReturned, Is.True, "off waited for the lock");
        Assert.That(QueuedJobs, Is.EqualTo(3));
        Assert.That(BotCpuMeter.Enabled, Is.False, "off stops metering at once");
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OffLine }));
        AssertOff();
    }

    [TestCase("start")]
    [TestCase("stop")]
    public void TheStartedAndStoppedHandlersWaitForTheLock(string step)
    {
        // 4.1, 3.7: the handlers run their job at once on their own thread, under the report lock: held by another
        // thread, the handler waits for it.
        if (step == "start")
            BotCpuScript.BOT_CPU_METER = true;
        else
            TurnOn();
        lock (_logs)
            _logs.Clear();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();

        System.Threading.Tasks.Task handler = System.Threading.Tasks.Task.Run(() =>
        {
            if (step == "start")
                BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
            else
                BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);
        });
        bool waited = !handler.Wait(TimeSpan.FromMilliseconds(200));
        int linesWhileHeld = Logged().Count;
        release.Set();
        holder.Join();
        handler.Wait();

        Assert.That(waited, Is.True, "the handler did not wait for the lock");
        Assert.That(linesWhileHeld, Is.EqualTo(0));
        Assert.That(Logged(), Is.EqualTo(new[]
            { step == "start" ? BotCpuReport.StateOnByPropertyLine : "BOT_CPU_STATE off source=stop" }));
        Assert.That(BotCpuMeter.Enabled, Is.EqualTo(step == "start"));
    }

    [Test]
    public void TheRealTimerTicksEverySecondUntilTheRunCloses()
    {
        // No timer hook: the report makes its own System.Threading.Timer, ticking every second (3.7, 3.8). A tick
        // once the clock has passed the window's 60 s closes the window, so two windows take two ticks: a one-shot
        // timer stops at the first. The second can come no sooner than the second tick, 2 s after on: a shorter
        // period writes it sooner.
        Assert.That(BotCpuScript.TickMilliseconds, Is.EqualTo(1000));
        TimerHook.SetValue(null, null);
        Stopwatch sinceOn = new();
        List<TimeSpan> writtenAt = [];
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            lock (_logs)
            {
                _logs.Add((level, line));
                writtenAt.Add(sinceOn.Elapsed);
            }
        }));
        Population(3);
        sinceOn.Start();
        TurnOn();
        _realTimers.Add((System.Threading.Timer)OpenRunTimer());

        Advance(60);
        Assert.That(SpinWait.SpinUntil(() => Logged().Count >= 3, TimeSpan.FromSeconds(10)), Is.True,
            "the first window");
        Advance(60);
        Assert.That(SpinWait.SpinUntil(() => Logged().Count >= 5, TimeSpan.FromSeconds(10)), Is.True,
            "the second window");
        BotCpuScript.CommandOff(Gm);
        // The off job, run here or by a tick that took the lock first: either way it is done once this returns.
        RunQueued();

        string window = "kind=gamebot n=3.0 msSec=0.0 msPerBotSec=0.000 callsPerBotSec=0.0 usPerCall=- " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=-";
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine, "BOT_CPU win=60 " + window, "BOT_CPU_PROCESS win=60", "BOT_CPU win=60 " + window,
            "BOT_CPU_PROCESS win=60", OffLine,
        }));
        lock (_logs)
            Assert.That(writtenAt[3], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9)), "the second window's tick");
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(BotCpuScript.Result.On, Is.False);
    }

    [Test]
    public void TheRealThreadPoolRunsEachCommandsJob()
    {
        // No queue hook: a command queues its work item on the thread pool itself (3.7). With the meter off no run
        // has a timer, so only that work item runs the on job; the off job too runs with no tick. One command at a
        // time: the test waits for its line, then for the report lock, which its work item holds until it has run
        // every job waiting, so no work item is left to run a job of the next command or of a later test.
        QueueHook.SetValue(null, null);
        object reportLock = ReportLockField.GetValue(null);

        BotCpuScript.CommandOn(Gm, null);
        Assert.That(SpinWait.SpinUntil(() => BotCpuScript.Result.On, TimeSpan.FromSeconds(10)), Is.True, "on");
        lock (reportLock)
        {
            // Nothing: the lock is only waited for.
        }

        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }));

        BotCpuScript.CommandOff(Gm);
        Assert.That(SpinWait.SpinUntil(() => !BotCpuScript.Result.On, TimeSpan.FromSeconds(10)), Is.True, "off");
        lock (reportLock)
        {
            // Nothing: the lock is only waited for.
        }

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(_timers, Has.Count.EqualTo(1));
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

No exception escapes (3.7, 6.1). A reader throws in two ways: `_procSelf` null (`Path.Combine` throws an
`ArgumentNullException` in `TakeProcessSample`) and `BotWhoseNameThrows`. A writer throws from the log hook, as NLog
does here. `WithEveryLogCallThrowingTheJobAfterAFailedOneStillRuns` queues an off job and an on job, which one work
item runs: the off job fails and cannot log, and the on job still runs, so the catch never rethrows.
`BotCpuErrorNamesTheJobOrHandlerThatFailed` makes only the step's own state line throw, so the `BOT_CPU_ERROR` and
`off source=error` lines are written. `ABotCpuErrorThatCannotBeWrittenStillLeavesTheOffLine` makes the window's first
line throw, then the `BOT_CPU_ERROR` line's log call (`line`) or the text of the exception it carries (`text`, Task 8's
`ExceptionWhoseTextThrows`). The off line is still written, so each of the two lines has a `try` of its own, and the
error line is made inside its own:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(_timers, Has.Count.EqualTo(1));
        AssertOff();
    }
}
=======
        Assert.That(_timers, Has.Count.EqualTo(1));
        AssertOff();
    }

    // ---- No exception escapes (3.7, 6.1)

    [TestCase("process")]
    [TestCase("bot")]
    public void ATickWhoseReaderThrowsReturnsNormallyAndTurnsTheMeterOff(string reader)
    {
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        GameBot bot = null;
        if (reader == "process")
            ProcSelfHook.SetValue(null, null); // the process sample's reads throw
        else
            ChargeBot(bot = Bot<BotWhoseNameThrows>("Aldric"), 90, 1_800); // the top's read of a bot throws

        Seconds(59);
        Assert.That(() => Second(), Throws.Nothing);

        AssertTurnedOffByError("tick", reader == "process"
            ? "System.ArgumentNullException"
            : "System.InvalidOperationException: name");
        Assert.That(File.Exists(Csv), Is.False);
        // A tick already waiting finds the run closed.
        int lines = Logged().Count;
        _timers[0].Tick();
        Assert.That(Logged(), Has.Count.EqualTo(lines));
        GC.KeepAlive(bot);
    }

    [Test]
    public void ATickWhoseWriterThrowsReturnsNormallyAndTurnsTheMeterOff()
    {
        // NLog here throws from log calls (3.7): the window's first line throws.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            if (line.StartsWith("BOT_CPU win=", StringComparison.Ordinal))
                throw new InvalidOperationException("log");
            lock (_logs)
                _logs.Add((level, line));
        }));

        Seconds(59);
        Assert.That(() => Second(), Throws.Nothing);

        AssertTurnedOffByError("tick", "System.InvalidOperationException: log");
        Assert.That(File.Exists(Csv), Is.False, "the run closed with no final window");

        // The server goes on: /botcpu on starts a new run (section 9).
        int run = BotCpuMeter.RunNumber;
        TurnOn();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(run + 1));
        Assert.That(Logged()[^1], Is.EqualTo(OnLine));
    }

    [TestCase("tick")]
    [TestCase("off")]
    [TestCase("start")]
    [TestCase("stop")]
    public void WithEveryLogCallThrowingNothingEscapesAndTheMeterEndsOff(string step)
    {
        Population(3);
        if (step == "start")
            BotCpuScript.BOT_CPU_METER = true;
        else
            TurnOn();

        LogHook.SetValue(null, (Action<ELogLevel, string>)((_, _) => throw new InvalidOperationException("log")));
        TestDelegate run = step switch
        {
            "tick" => () => Seconds(60),
            "off" => () =>
            {
                BotCpuScript.CommandOff(Gm);
                RunQueued();
            },
            "start" => () => BotCpuScript.OnServerStarted(null, null, EventArgs.Empty),
            _ => () => BotCpuScript.OnServerStopped(null, null, EventArgs.Empty),
        };

        Assert.That(run, Throws.Nothing);
        AssertOff();
    }

    [Test]
    public void WithEveryLogCallThrowingTheJobAfterAFailedOneStillRuns()
    {
        // 3.7: the catch never rethrows. The off job's line throws, then its BOT_CPU_ERROR and off source=error lines
        // too. The outer catches would hide a catch that threw, but not the jobs it left waiting: the on job queued
        // after the off runs from the same work item, opens the next run and fails the same way.
        TurnOn();
        int run = BotCpuMeter.RunNumber;
        LogHook.SetValue(null, (Action<ELogLevel, string>)((_, _) => throw new InvalidOperationException("log")));
        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOn(Gm, null);
        Assert.That(_queued, Has.Count.EqualTo(2));

        Assert.That(() => _queued[0](null), Throws.Nothing);

        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(run + 1));
        AssertOff();
    }

    [TestCase("on")]
    [TestCase("off")]
    [TestCase("start")]
    [TestCase("stop")]
    public void BotCpuErrorNamesTheJobOrHandlerThatFailed(string step)
    {
        // 2.1, 3.7: BOT_CPU_ERROR <step> names what failed. Only the step's own state line throws, so the error
        // and off source=error lines are written.
        string failing = step switch
        {
            "on" => OnLine,
            "off" => OffLine,
            "start" => BotCpuReport.StateOnByPropertyLine,
            _ => "BOT_CPU_STATE off source=stop",
        };
        if (step is "off" or "stop")
            TurnOn();
        BotCpuScript.BOT_CPU_METER = step == "start";
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            if (line == failing)
                throw new InvalidOperationException("log");
            lock (_logs)
                _logs.Add((level, line));
        }));

        switch (step)
        {
            case "on":
                BotCpuScript.CommandOn(Gm, null);
                RunQueued();
                break;
            case "off":
                BotCpuScript.CommandOff(Gm);
                RunQueued();
                break;
            case "start":
                BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
                break;
            default:
                BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);
                break;
        }

        AssertTurnedOffByError(step, "System.InvalidOperationException: log");
    }

    [TestCase("line")]
    [TestCase("text")]
    public void ABotCpuErrorThatCannotBeWrittenStillLeavesTheOffLine(string fault)
    {
        // 3.7: BOT_CPU_ERROR and off source=error each sit in a try of their own, and the error line is made inside
        // its own: the text of the exception it carries can throw. The window's first line throws; then the error
        // line's log call throws ("line"), or the text of the exception ("text"). The off line is still written.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        Exception failure = fault == "line" ? new InvalidOperationException("log") : new ExceptionWhoseTextThrows();
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            if (line.StartsWith("BOT_CPU win=", StringComparison.Ordinal))
                throw failure;
            if (line.StartsWith("BOT_CPU_ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException("log");
            lock (_logs)
                _logs.Add((level, line));
        }));

        Seconds(59);
        Assert.That(() => Second(), Throws.Nothing);

        List<(ELogLevel Level, string Line)> logs;
        lock (_logs)
            logs = [.. _logs];
        Assert.That(logs, Is.EqualTo(new[]
            { (ELogLevel.Info, OnLine), (ELogLevel.Info, "BOT_CPU_STATE off source=error") }));
        AssertOff();
    }

    [Test]
    public void ACsvWriteThatThrowsANonIoExceptionLogsOneWarningAndTheMeterGoesOn()
    {
        Population(3);
        TurnOn();
        long[] slot = Slot();
        Charge(slot, Gamebot, Npc, 180, 5_400);
        CsvWriteHook.SetValue(null,
            (Action<FileStream, byte[]>)((_, _) => throw new InvalidOperationException("not I/O")));

        Seconds(60);

        List<(ELogLevel Level, string Line)> warnings;
        lock (_logs)
            warnings = _logs.Where(log => log.Level == ELogLevel.Warning).ToList();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].Line, Does.StartWith("Bot CPU meter: could not write")
            .And.Contain("System.InvalidOperationException: not I/O"));
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU", StringComparison.Ordinal)),
            Is.EqualTo(new[] { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60" }));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.On, Is.True);
        Assert.That(BotCpuScript.Result.Last.Win, Is.EqualTo(60));

        // The next minute's rows are written.
        CsvWriteHook.SetValue(null, null);
        Charge(slot, Gamebot, Npc, 180, 5_400);
        Seconds(60);
        string[] csv = File.ReadAllLines(Csv);
        Assert.That(csv, Has.Length.EqualTo(2));
        Assert.That(csv[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 4, 0)) + ",60,"));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

The stop time (4.3, 6.1). In `AStopAt1432StopsAtTheFirstSecondAtOrAfterIt` the timer ticks half a second into each
second. `AStopTimeRunsItsMinutesAcrossAClockChange` moves only the local time an hour, ten minutes in.
`SummerTimeZone` is a custom zone with US Central's summer time, given through `_zoneHook`, so no test changes the
machine's zone. `OnTheServersOwnClocksTheStopTimeIsItsMinutesOnInUtcAndTheRunStartsAtItsJob` removes the clock and
Stopwatch hooks and uses `FiveHoursAheadZone`, so a local time taken for UTC is 5 h off even on a UTC machine:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(csv, Has.Length.EqualTo(2));
        Assert.That(csv[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 4, 0)) + ",60,"));
    }
}
=======
        Assert.That(csv, Has.Length.EqualTo(2));
        Assert.That(csv[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 4, 0)) + ",60,"));
    }

    // ---- The stop time (4.3, 6.1)

    [Test]
    public void AStopAt1432StopsAtTheFirstSecondAtOrAfterIt()
    {
        // on 30 at 14:02:00 stops at 14:32:00; the timer ticks half a second into each second.
        Assert.That(BotCpuScript.CommandOn(Gm, 30), Is.EqualTo(At(14, 32, 0)));
        RunQueued();
        Advance(0.5);

        Seconds(1_799);
        Assert.That(_now, Is.EqualTo(At(14, 31, 59, 500)));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.On, Is.True);

        Second();
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        // The minute closed at 14:32:00.5 is the 30th; the final window that follows it is 0 s long, not written.
        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU_PROCESS ", StringComparison.Ordinal)),
            Is.EqualTo(30));
        Assert.That(Logged()[^2], Is.EqualTo("BOT_CPU_PROCESS win=60"));
        AssertOff();
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void AStopTimeRunsItsMinutesAcrossAClockChange(int hours)
    {
        // 4.3: on 30 turns itself off after 30 minutes. Ten minutes in, the server's local clock moves an hour back
        // (summer time ends) or ahead (it begins): the stop time shown stays 14:32, and the meter still stops 30
        // minutes after on, not an hour later or at once.
        Assert.That(BotCpuScript.CommandOn(Gm, 30), Is.EqualTo(At(14, 32, 0)));
        RunQueued();
        Seconds(600);
        _now = _now.AddHours(hours);

        Seconds(1_199);
        Assert.That(BotCpuScript.Result.On, Is.True);
        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(At(14, 32, 0)));

        Second();
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }

    // A server's time zone with summer time: UTC-6, and UTC-5 from the second Sunday of March at 02:00 to the first
    // Sunday of November at 02:00, as US Central time.
    private static readonly TimeZoneInfo SummerTimeZone = TimeZoneInfo.CreateCustomTimeZone("HearthDAoC test",
        TimeSpan.FromHours(-6), "HearthDAoC test", "HearthDAoC test standard", "HearthDAoC test summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2,
                    DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1,
                    DayOfWeek.Sunday)),
        ]);

    [TestCase(2026, 11, 1, 0, 50, 90, 1, 20)] // summer time ends at 02:00, the clock back to 01:00: not 02:20
    [TestCase(2027, 3, 14, 1, 30, 60, 3, 30)] // summer time begins at 02:00, the clock on to 03:00: not 02:30
    public void AStopTimeShowsTheLocalTimeTheRunStopsAtAcrossAClockChange(int year, int month, int day, int hour,
        int minute, int minutes, int stopHour, int stopMinute)
    {
        // 4.3, 2.4: on <minutes> stops after that many minutes, and the reply and the status show when, in the server's
        // local time: that many minutes on in UTC, shown in the local time of then.
        ZoneHook.SetValue(null, SummerTimeZone);
        _now = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
        DateTime stopsAt = new(year, month, day, stopHour, stopMinute, 0, DateTimeKind.Local);

        Assert.That(BotCpuScript.CommandOn(Gm, minutes), Is.EqualTo(stopsAt));
        RunQueued();

        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(stopsAt));
    }

    // A server's time zone 5 hours ahead of UTC, with no summer time: a local time taken for UTC is 5 hours off here.
    private static readonly TimeZoneInfo FiveHoursAheadZone = TimeZoneInfo.CreateCustomTimeZone("HearthDAoC test +5",
        TimeSpan.FromHours(5), "HearthDAoC test +5", "HearthDAoC test +5");

    [Test]
    public void OnTheServersOwnClocksTheStopTimeIsItsMinutesOnInUtcAndTheRunStartsAtItsJob()
    {
        // 4.3, 2.4, 2.2: the server's clocks, not the test's (no local time or Stopwatch hook). on 30 shows the time
        // 30 minutes on in UTC, in the server's zone (here the zone hook's, as the stop time's way through UTC reads
        // it); the run opens at its job's local time (DateTime.Now) and Stopwatch timestamp.
        NowHook.SetValue(null, null);
        TimestampHook.SetValue(null, null);
        ZoneHook.SetValue(null, FiveHoursAheadZone);

        DateTime earliest = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddMinutes(30), FiveHoursAheadZone);
        DateTime? stopsAt = BotCpuScript.CommandOn(Gm, 30);
        DateTime latest = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddMinutes(30), FiveHoursAheadZone);
        DateTime localBefore = DateTime.Now;
        long timestampBefore = Stopwatch.GetTimestamp();
        RunQueued();
        long timestampAfter = Stopwatch.GetTimestamp();
        DateTime localAfter = DateTime.Now;

        Assert.That(stopsAt, Is.InRange(earliest, latest));
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.StopsAt, Is.EqualTo(stopsAt));
        Assert.That(result.OnSince, Is.InRange(localBefore, localAfter));
        Assert.That(result.WindowStartTimestamp, Is.InRange(timestampBefore, timestampAfter));
    }

    [Test]
    public void OnWithNoMinutesOnARunningMeterClearsTheStopTime()
    {
        TurnOn(30);
        Seconds(10);

        BotCpuScript.CommandOn(Gm, null);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OnLine }));
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);
        Assert.That(_timers, Has.Count.EqualTo(1));
        Advance(30 * 60);
        Second();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.On, Is.True);
    }

    [Test]
    public void On30OnARunningMeterWritesTheOnLineAgainAndStopsThere()
    {
        TurnOn();
        int run = BotCpuMeter.RunNumber;
        Seconds(10);

        DateTime? stopsAt = BotCpuScript.CommandOn(Gm, 30);
        Assert.That(BotCpuMeter.Enabled, Is.True);
        RunQueued();

        Assert.That(stopsAt, Is.EqualTo(At(14, 32, 10)));
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(run), "no new run");
        Assert.That(_timers, Has.Count.EqualTo(1));
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.StopsAt, Is.EqualTo(At(14, 32, 10)));
        Assert.That(result.OnSince, Is.EqualTo(At(14, 2, 0)));
        Assert.That(result.Run, Is.EqualTo(run));

        Advance(30 * 60 - 1);
        Second();
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

The started and stopped handlers (4.1): their attributes, the property on and off, the partial window and
`off source=stop`, and a GM's off waiting when the server stops:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }
}
=======
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }

    // ---- The started and stopped handlers (4.1)

    [Test]
    public void ThePropertyOnStartsTheMeterWithTheServerAndItsStopWritesThePartialWindow()
    {
        Assert.That(typeof(BotCpuScript).GetMethod(nameof(BotCpuScript.OnServerStarted))!
            .GetCustomAttribute<GameServerStartedEventAttribute>(), Is.Not.Null);
        Assert.That(typeof(BotCpuScript).GetMethod(nameof(BotCpuScript.OnServerStopped))!
            .GetCustomAttribute<GameServerStoppedEventAttribute>(), Is.Not.Null);
        BotCpuScript.BOT_CPU_METER = true;
        Population(3);

        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);

        // At once, on the event's own thread: nothing queued.
        Assert.That(_queued, Is.Empty);
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=property" }));
        Assert.That(BotCpuScript.Result.On, Is.True);
        Assert.That(BotCpuScript.Result.ByCommand, Is.False);
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);

        Charge(Slot(), Gamebot, Npc, 90, 2_700);
        Seconds(30);
        BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);

        Assert.That(_queued, Is.Empty);
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            "BOT_CPU_STATE on source=property", GamebotLine(30, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=30",
            "BOT_CPU_STATE off source=stop",
        }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void ThePropertyOffStartsNothing()
    {
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
        BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);

        Assert.That(Logged(), Is.Empty);
        Assert.That(_timers, Is.Empty);
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(BotCpuScript.Result.On, Is.False);
    }

    [Test]
    public void AStopTimeAlsoStopsAMeterThePropertyStarted()
    {
        // 4.3: /botcpu on <minutes> turns itself off, whether the property or a command started the meter.
        BotCpuScript.BOT_CPU_METER = true;
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
        BotCpuScript.CommandOn(Gm, 1);
        RunQueued();
        Assert.That(BotCpuScript.Result.ByCommand, Is.False, "the meter was turned on by the property");

        Seconds(60);

        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }

    [Test]
    public void TheStopRunsAGmsOffWaitingFirst()
    {
        // The stopped handler runs the jobs waiting before its own work: the run closes by the GM's off.
        TurnOn();
        Seconds(10);
        BotCpuScript.CommandOff(Gm);

        BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 112 errors, all in the test file and all about the missing API:
`CS0117` "'BotCpuScript' does not contain a definition for" `Result` (37), `CommandOff` (28), `CommandOn` (21),
`OnServerStarted` (8), `OnServerStopped` (7), `CommandReset` (5) and `TickMilliseconds` (1), and `CS0246` (4) and
`CS0103` (1) for `BotCpuResult`. The private hooks are reached by reflection, so they are not listed. Each error line
starts with the file's absolute path and ends with the project's; the first and the last:

```
source/server/Tests/UnitTests/UT_BotCpuScript.cs(138,36): error CS0103: The name 'BotCpuResult' does not exist in the current context [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuScript.cs(2588,22): error CS0117: 'BotCpuScript' does not contain a definition for 'OnServerStopped' [source/server/Tests/Tests.csproj]
```

The summary (the warning total varies between builds; 851 was seen):

```
Build FAILED.
    851 Warning(s)
    112 Error(s)
```

- [ ] **Step 3: Implement.** All of it goes in `BotCpuScript.cs`, in six edits. First the usings (the job queue, the
thread pool and the timer, the handlers' `DOLEvent`), and the header comment, which now names the report side:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DOL.GS.ServerProperties;
using DOL.Logging;
=======
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DOL.Events;
using DOL.GS.ServerProperties;
using DOL.Logging;
>>>>>>> NEW
<<<<<<< OLD
// failed CSV write logs one warning and never escapes. BotCpuMeter is the hot path; BotCpuReport makes the numbers and
// the text.
=======
// failed CSV write logs one warning and never escapes. The report side (3.7, 3.8, 4.1, 4.3, 4.4): a command only turns
// the meter off or queues a job; the jobs, the 1-second timer and the server's started and stopped handlers run one at
// a time under the report lock, off the game loop: runs, windows, the stop time, the published result, and a catch that
// turns the meter off at the first fault. BotCpuMeter is the hot path; BotCpuReport makes the numbers and the text.
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (2 block(s))`.

The report side's state and types, after `EndsWithLineEnd`. The lock, the queue, the open run and the published result
are static; only the result is read off the lock, by `/botcpu`, through `Volatile`. `Instant` is one moment on the
three clocks a window uses: the Stopwatch timestamp (its length, 2.2), the local time (the chat and the CSV) and the
game time (its ticks, 3.8). `StopTime` keeps the stop as a local time to show and a Stopwatch timestamp to check, so a
summer-time change neither shortens nor lengthens the run; the time shown is the minutes added in UTC, turned into the
server's zone. `MeterRun` is one run, from its `Enable` to its close; `OnSinceTimestamp` is its first window's start,
which an off command's instant is compared with. The class's closing brace follows:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }
}
=======
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    // ---- The report side (3.7, 3.8, 4.1, 4.3, 4.4)

    /// <summary>The report's timer ticks once a second (3.7, 3.8): it samples the registry, closes a window at the
    /// first tick at or after its 60 s, and checks the stop time.</summary>
    public const int TickMilliseconds = 1000;

    // Every change to the report's state runs under this lock (3.7), taken only on the report side: by a queued work
    // item (waiting for it), a timer tick (skipping its second while it is held) and the server's started and stopped
    // handlers. The game-loop thread never takes it.
    private static readonly object ReportLock = new();

    // The jobs the commands queue, first in first out (3.7); whoever holds the lock runs every job waiting, in order.
    private static readonly ConcurrentQueue<Job> Jobs = new();

    // The open run, or null: read and written under the report lock only.
    private static MeterRun _open;

    // The last result the report published, swapped in whole (3.7); /botcpu reads only this.
    private static BotCpuResult _result = BotCpuResult.Off;

    // Test hooks (6.1), null on the server: the queued work items (in place of ThreadPool.QueueUserWorkItem), the
    // 1-second timer (in place of a System.Threading.Timer), the server's local time, the Stopwatch timestamp, the
    // server's time zone (in place of TimeZoneInfo.Local, for a stop time's way through UTC) and the CSV's path.
    private static Action<WaitCallback> _queueHook;
    private static Func<TimerCallback, object, IDisposable> _timerHook;
    private static Func<DateTime> _nowHook;
    private static Func<long> _timestampHook;
    private static TimeZoneInfo _zoneHook;
    private static string _csvFile;

    // A job of the report side: what a command asked for, run under the report lock in its turn, and its step name for
    // BOT_CPU_ERROR.
    private sealed record Job(string Step, Action Work);

    // An instant on the report's three clocks: the Stopwatch timestamp a window is measured with (2.2), the server's
    // local time it is shown in, and the game time its ticks are counted from (3.8). A window starts and ends at one.
    private readonly record struct Instant(long Timestamp, DateTime Time, long GameTime)
    {
        internal static Instant Now() => new(BotCpuScript.Timestamp(), LocalNow(), GameLoop.GameLoopTime);
    }

    // A stop time (4.3): the server's local time it shows, and the Stopwatch timestamp the timer checks, so the run
    // stops after its minutes even when the local clock moves meanwhile (summer time begins or ends).
    private readonly record struct StopTime(DateTime Time, long Timestamp)
    {
        // The stop time <minutes> from now: shown as the local time of the instant it stops, that many minutes on in
        // UTC, so a change of the local clock before then shows the time the run really stops at.
        internal static StopTime In(int minutes)
        {
            TimeZoneInfo zone = _zoneHook ?? TimeZoneInfo.Local;
            Func<DateTime> now = _nowHook;
            DateTime utc = now == null
                ? DateTime.UtcNow
                : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(now(), DateTimeKind.Unspecified), zone);
            return new StopTime(TimeZoneInfo.ConvertTimeFromUtc(utc.AddMinutes(minutes), zone),
                BotCpuScript.Timestamp() + minutes * 60L * Stopwatch.Frequency);
        }
    }

    // A run (3.7): from its Enable to its close, with its number, how and when it turned on, its stop time, its timer,
    // its open window and its totals since on or reset. Read and written under the report lock only.
    private sealed class MeterRun
    {
        internal int Number;
        internal bool ByCommand;
        internal DateTime OnSince;
        // The same instant as a Stopwatch timestamp: its first window's start, which an off command's instant is
        // compared with (3.7).
        internal long OnSinceTimestamp;
        internal StopTime? StopsAt;
        internal IDisposable Timer;

        // The open window (3.7): its start, the slots' totals then (its baseline), the process values then and the
        // Stopwatch timestamp they were taken at, and the registry's samples so far (3.8).
        internal Instant WindowStart;
        internal BotCpuTotals Baseline;
        internal BotCpuProcessSample ProcessStart;
        internal long ProcessTaken;
        internal BotCpuSamples Samples;

        // The last window written, its process values, and the totals since on or reset (2.4).
        internal BotCpuWindow Last;
        internal BotCpuProcess LastProcess;
        internal BotCpuSince Since;
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

The members Task 10 calls. A command never takes the lock. `CommandOn` makes the stop time at the command and carries
it in the job, so the reply and the stop use one time. `CommandOff` calls `Disable`, takes the command's `Instant`,
reads the published result once, then queues; its value is the final window's length as the job will measure it. The
handlers run at once, under the lock, through `RunNow`; the started one opens a run only when the property is on and no
run is open:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        internal BotCpuSince Since;
    }
=======
        internal BotCpuSince Since;
    }

    /// <summary>
    /// The last result the report published (3.7): an immutable object, swapped in whole at each change (on, a window,
    /// reset, a new stop time, off). <c>/botcpu</c> reads only this and never builds a report. Never null.
    /// </summary>
    public static BotCpuResult Result => Volatile.Read(ref _result);

    /// <summary>The server's local time (<c>TZ</c>), as the chat shows it (2.4), the stop time included (4.3).
    /// </summary>
    public static DateTime LocalNow() => _nowHook?.Invoke() ?? DateTime.Now;

    // The Stopwatch timestamp a window is measured with (2.2).
    private static long Timestamp() => _timestampHook?.Invoke() ?? Stopwatch.GetTimestamp();

    private static double SecondsBetween(long start, long end) => (end - start) / (double)Stopwatch.Frequency;

    // Where the windows' rows go: logs/bot-cpu.csv (2.3).
    private static string CsvFile => _csvFile ?? CsvPath;

    /// <summary>
    /// <c>/botcpu on [minutes]</c>, on its game-loop thread (3.7): queues the on job and returns at once, with no file,
    /// <c>/proc</c> read or log write. <see cref="BotCpuMeter.Enabled"/> is unchanged until the job runs. The job opens
    /// a run when none is open (<c>BOT_CPU_STATE on source=command</c>), or sets or clears the stop time of the running
    /// one and writes that line again (4.3). Returns the stop time, <paramref name="minutes"/> from now, or null for
    /// none, so the reply shows the time the job uses: the local time of the instant it stops, so across a change of
    /// the local clock (summer time) it shows the time the run really stops at. The timer checks it as that many
    /// minutes of the Stopwatch, so such a change neither shortens nor lengthens the run.
    /// </summary>
    /// <param name="by">The GM's name, for the line.</param>
    /// <param name="minutes">1-1440 (the command checks it), or null: until off or the next start.</param>
    public static DateTime? CommandOn(string by, int? minutes)
    {
        StopTime? stopsAt = minutes is int stopIn ? StopTime.In(stopIn) : null;
        Queue("on", () => OnJob(by, minutes, stopsAt));
        return stopsAt?.Time;
    }

    /// <summary>
    /// <c>/botcpu off</c>, on its game-loop thread (3.7, 4.4): <see cref="BotCpuMeter.Disable"/>, so metering stops at
    /// once, then the off job queued; no file, <c>/proc</c> read or log write. The job closes the open run, if any:
    /// its final window, then <c>BOT_CPU_STATE off source=command</c>. The final window ends at this command's
    /// instant, when metering stopped, not when the job runs. Returns that window's length in seconds, measured from
    /// the published open window's start to that instant as the job measures it, so the reply and the job agree on
    /// whether it is written (5 s, 2.2): below 0 when a tick started the open window after that instant, and the job
    /// then writes none. Null when the published result is off.
    /// </summary>
    /// <param name="by">The GM's name, for the line.</param>
    public static double? CommandOff(string by)
    {
        BotCpuMeter.Disable();
        Instant stopped = Instant.Now();
        BotCpuResult result = Result;
        Queue("off", () => OffJob(by, stopped));
        return result.On ? SecondsBetween(result.WindowStartTimestamp, stopped.Timestamp) : null;
    }

    /// <summary><c>/botcpu reset</c>, on its game-loop thread (3.7): queues the reset job, which starts the open run's
    /// totals since on again ("Since reset"); the windows go on. With no run open the job does nothing.</summary>
    public static void CommandReset() => Queue("reset", ResetJob);

    /// <summary>
    /// The property's start (4.1): read once, before the game loop starts (<c>GameServer.cs:510</c>, :517), so the
    /// first tick is metered. On, a run opens at once on this thread, under the report lock, not queued:
    /// <c>BOT_CPU_STATE on source=property</c>. Nothing escapes to the server's start.
    /// </summary>
    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args) => RunNow("start", () =>
    {
        if (BOT_CPU_METER && _open == null)
            Open(false, null, BotCpuReport.StateOnByPropertyLine);
    });

    /// <summary>
    /// The server's stop (4.1), after <c>GameLoop.Exit</c> (<c>GameServer.cs:1013</c>, :1015): the jobs waiting, then
    /// the open run closed at once on this thread, under the report lock: its partial window, then
    /// <c>BOT_CPU_STATE off source=stop</c>. Nothing escapes to the server's stop.
    /// </summary>
    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args) => RunNow("stop", () =>
    {
        if (_open is MeterRun run)
            Close(run, BotCpuOffSource.Stop, null);
    });
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

The queue, the lock and the catch. `Queue` hands the work item to the queue hook in tests, else to
`ThreadPool.QueueUserWorkItem`. The work item waits for the lock; `OnTimer` uses `Monitor.TryEnter` and skips its
second when the lock is held; whoever holds it runs `RunJobs` first. Every job, tick and handler step runs in
`Guarded(step, ...)`, so a fault in one job closes the run and the jobs after it still run (an on after it opens a new
run). `Fail` disables, clears the open run and publishes `Off` first, disposes the timer in its own `try`, then writes
the two lines through Task 8's `TryWriteLog`, each guarded. The outer catches only keep an exception out of a
thread-pool callback, a timer callback or the server's start and stop:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        if (_open is MeterRun run)
            Close(run, BotCpuOffSource.Stop, null);
    });
=======
        if (_open is MeterRun run)
            Close(run, BotCpuOffSource.Stop, null);
    });

    // A command's job (3.7): into the queue, then a work item for the thread pool, as PublishBotAiDelay does
    // (GameLoopWorkMetrics.cs:98-104).
    private static void Queue(string step, Action work)
    {
        Jobs.Enqueue(new Job(step, work));
        Action<WaitCallback> hook = _queueHook;
        if (hook != null)
            hook(RunQueuedJobs);
        else
            ThreadPool.QueueUserWorkItem(RunQueuedJobs);
    }

    // A queued work item (3.7): it waits for the lock, then runs every job waiting, in order. An earlier item may
    // already have run its job: it then finds none.
    private static void RunQueuedJobs(object state)
    {
        try
        {
            lock (ReportLock)
                RunJobs();
        }
        catch (Exception)
        {
            // Nothing: each job has its own catch; an exception that escaped a thread-pool callback would end the
            // process (3.7).
        }
    }

    // A server event's job (4.1): at once, on the event's own thread, under the lock, after the jobs waiting.
    private static void RunNow(string step, Action work)
    {
        try
        {
            lock (ReportLock)
            {
                RunJobs();
                Guarded(step, work);
            }
        }
        catch (Exception)
        {
            // Nothing: an exception here never reaches the server's start or stop (4.1).
        }
    }

    // The timer's tick (3.7, 3.8): it skips its second while the lock is held. Holding it, it runs every job waiting,
    // then its own work, for its own run: a tick of a run already closed (a tick that waited, or a disposed timer's
    // last) does nothing.
    private static void OnTimer(object state)
    {
        try
        {
            if (!Monitor.TryEnter(ReportLock))
                return;

            try
            {
                RunJobs();
                Guarded("tick", () => Tick(state as MeterRun));
            }
            finally
            {
                Monitor.Exit(ReportLock);
            }
        }
        catch (Exception)
        {
            // Nothing: an exception that escaped a timer callback would end the process (3.7).
        }
    }

    // Every job waiting, in order, each in its own catch (3.7). Under the report lock.
    private static void RunJobs()
    {
        while (Jobs.TryDequeue(out Job job))
            Guarded(job.Step, job.Work);
    }

    // A step of the report side inside the catch of 3.7: its first exception turns the meter off. It never rethrows.
    private static void Guarded(string step, Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            Fail(step, exception);
        }
    }

    // The catch of 3.7: Enabled false, the timer disposed, the run closed with no final window (its counts are
    // dropped), one Warn line BOT_CPU_ERROR <step>: <exception>, then BOT_CPU_STATE off source=error, each log call in
    // its own try whose catch does nothing. Never throws. Under the report lock.
    private static void Fail(string step, Exception exception)
    {
        BotCpuMeter.Disable();
        MeterRun run = _open;
        _open = null;
        Volatile.Write(ref _result, BotCpuResult.Off);
        try
        {
            run?.Timer?.Dispose();
        }
        catch (Exception)
        {
            // Nothing: the run is closed whatever its timer does.
        }

        TryWriteLog(ELogLevel.Warning, () => BotCpuReport.ErrorLine(step, exception));
        TryWriteLog(ELogLevel.Info, () => BotCpuReport.StateOffLine(BotCpuOffSource.Error));
    }
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

The jobs, a run's opening, its timer and its ticks. An on job on a running meter only sets or clears the stop time and
writes its line again: the run's number, `ByCommand` and `OnSince` stay, and no new timer is made. `OffJob` ends the
final window at the command's instant, unless the command came before this run opened (an off queued before the on job
that opened it): then the close ends it now. `Open` calls `Enable` before it takes the baseline, so wrappers still in
flight from an earlier run land before it. `Tick` samples the registry before it checks the window, so the closing
tick's sample counts in the window it closes, then checks the stop time on the Stopwatch:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        TryWriteLog(ELogLevel.Info, () => BotCpuReport.StateOffLine(BotCpuOffSource.Error));
    }
=======
        TryWriteLog(ELogLevel.Info, () => BotCpuReport.StateOffLine(BotCpuOffSource.Error));
    }

    // The on job (3.7, 4.3): with a run open, its new stop time (or none) and the on line again; else a new run.
    private static void OnJob(string by, int? minutes, StopTime? stopsAt)
    {
        string line = BotCpuReport.StateOnByCommandLine(by, minutes);
        if (_open is not MeterRun run)
        {
            Open(true, stopsAt, line);
            return;
        }

        run.StopsAt = stopsAt;
        WriteLog(ELogLevel.Info, line);
        Publish(run);
    }

    // The off job (3.7): closes the open run, its final window ending at the command's instant, when metering stopped;
    // with no run, nothing. A command that came before this run opened (an off queued before the on job that opened
    // it) did not stop this run: its close does, now. A command that came after it opened but before its open window
    // started (a tick closed the minute between the command's instant and its job's place in the queue) leaves a
    // final window of negative length: none is written, as metering stopped before that window started.
    private static void OffJob(string by, Instant stopped)
    {
        if (_open is MeterRun run)
            Close(run, BotCpuOffSource.Command, by, stopped.Timestamp >= run.OnSinceTimestamp ? stopped : null);
    }

    // The reset job (2.4): the open run's totals since on start again; the windows go on. With no run, nothing.
    private static void ResetJob()
    {
        if (_open is not MeterRun run)
            return;

        run.Since = BotCpuSince.Start(true);
        Publish(run);
    }

    // A run opens (3.7): Enable first (the slot list the first time, a new per-bot table and the next run number, then
    // Enabled), then a fresh baseline and the first window's start, so counts from an earlier run never leak in; then
    // its timer, its on line and its result. Under the report lock.
    private static void Open(bool byCommand, StopTime? stopsAt, string line)
    {
        MeterRun run = new()
        {
            Number = BotCpuMeter.Enable(),
            ByCommand = byCommand,
            StopsAt = stopsAt,
            Since = BotCpuSince.Start(false),
        };
        BotCpuTotals baseline = BotCpuMeter.TakeSnapshot();
        BotCpuProcessSample process = TakeProcessSample();
        Instant start = Instant.Now();
        StartWindow(run, baseline, process, start.Timestamp, start, new BotCpuSamples());
        run.OnSince = start.Time;
        run.OnSinceTimestamp = start.Timestamp;
        _open = run;
        run.Timer = NewTimer(run);
        WriteLog(ELogLevel.Info, line);
        Publish(run);
    }

    // The run's 1-second System.Threading.Timer (3.7), made when the run opens, with the run as its state.
    private static IDisposable NewTimer(MeterRun run)
    {
        Func<TimerCallback, object, IDisposable> hook = _timerHook;
        return hook != null
            ? hook(OnTimer, run)
            : new System.Threading.Timer(OnTimer, run, TickMilliseconds, TickMilliseconds);
    }

    // A window starts (3.7): its baseline, the process values and when they were taken, its instant and the samples so
    // far.
    private static void StartWindow(MeterRun run, BotCpuTotals baseline, BotCpuProcessSample process,
        long processTaken, Instant start, BotCpuSamples samples)
    {
        run.Baseline = baseline;
        run.ProcessStart = process;
        run.ProcessTaken = processTaken;
        run.WindowStart = start;
        run.Samples = samples;
    }

    // A second of the run (3.8, 4.3): the registry sampled, the window closed at the first tick at or after its 60 s,
    // then the stop time checked on the Stopwatch: the first tick at or after it closes the run (off source=timer).
    private static void Tick(MeterRun run)
    {
        if (run == null || run != _open)
            return;

        run.Samples = run.Samples.Add(AutonomousBotRegistry.PopulationForBrainTick);
        Instant now = Instant.Now();
        if (SecondsBetween(run.WindowStart.Timestamp, now.Timestamp) >= BotCpuReport.WindowSeconds)
        {
            WriteWindow(run, now);
            Publish(run);
        }

        if (run.StopsAt is StopTime stop && now.Timestamp >= stop.Timestamp)
            Close(run, BotCpuOffSource.Timer, null);
    }
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

A window, the top's bots, a close, the published result, and `BotCpuResult` after the class. `WriteWindow` reads
`GameLoop.TickDuration` at the window's end: the started event comes before `GameLoop.Init` sets it
(`GameServer.cs:510`, :517), so it is 0 at the property's start. It takes the process sample's own timestamp and
divides the process rates by the time between the two samples, which for an off command's final window runs to its
job. A window under 5 s (`Window` returns null) writes nothing, and a window with no kind line has no CSV row, so no
file is made. `ListBots` reads each field of a bot once: `CurrentRegion?.ID`, not `CurrentRegionID`, which reads the
region twice while an instance's collapse can set it to null on the loop (`world/Region.cs:270`). The old text takes
in the class's closing brace; the new text closes the class after `Publish` and ends with `BotCpuResult`:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        if (run.StopsAt is StopTime stop && now.Timestamp >= stop.Timestamp)
            Close(run, BotCpuOffSource.Timer, null);
    }
}
=======
        if (run.StopsAt is StopTime stop && now.Timestamp >= stop.Timestamp)
            Close(run, BotCpuOffSource.Timer, null);
    }

    // A window (3.7, 3.8), under the lock, from its start to its end instant: the slots' totals less its baseline (the
    // slot list copied under its lock, each value read with Volatile.Read), each bot's counter since the report last
    // read it, the process values, the game ticks and the registry samples. A window of 5 s or more writes its lines,
    // then its CSV rows, whose failure only loses them (2.3), and counts in the totals since on; a shorter final window
    // writes nothing (2.2). The next window starts at its end, with these totals as its baseline. The process values
    // are read here, off the loop: a final window that ends at an off command's instant has them up to its job,
    // moments later, so each of their rates divides by the time between their own two samples, not by the window's.
    private static void WriteWindow(MeterRun run, Instant end)
    {
        BotCpuTotals totals = BotCpuMeter.TakeSnapshot();
        BotCpuProcessSample process = TakeProcessSample();
        long processTaken = Timestamp();
        double seconds = SecondsBetween(run.WindowStart.Timestamp, end.Timestamp);
        BotCpuWindow window = BotCpuReport.Window(BotCpuReport.Difference(totals, run.Baseline), seconds,
            Stopwatch.Frequency, end.GameTime - run.WindowStart.GameTime, GameLoop.TickDuration, run.Samples,
            ListBots());
        BotCpuProcess values = ProcessWindow(run.ProcessStart, process, SecondsBetween(run.ProcessTaken, processTaken));
        StartWindow(run, totals, process, processTaken, end, run.Samples.NextWindow());
        if (window == null)
            return;

        foreach (string line in BotCpuReport.Lines(window, values))
            WriteLog(ELogLevel.Info, line);

        IReadOnlyList<string> rows = BotCpuReport.CsvRows(window, values, end.Time);
        if (rows.Count > 0)
            AppendCsv(CsvFile, rows);

        run.Last = window;
        run.LastProcess = values;
        run.Since = run.Since.Add(window);
    }

    // The bots of the run's per-bot table (3.10), with their counters' ticks and calls since the report last read
    // them. A bot that left the world is gone from the table once its counter is collected, or simply not listed:
    // either way nothing of it stays behind. These are reads of another thread's objects without a lock: a name or an
    // activity a moment old is harmless. Each is read once: the region is set to null on the loop when an instance
    // collapses (world/Region.cs:270), so CurrentRegionID, which reads it twice, could find it gone at its second read.
    private static List<BotCpuBot> ListBots()
    {
        List<BotCpuBot> bots = [];
        foreach ((GameObject owner, BotCpuCounter counter) in BotCpuMeter.Counters())
        {
            (long ticks, long calls) = BotCpuReport.TakeCounterWindow(counter);
            OfflineWorldBotRecord record = (owner as GameBot)?.PersistentRecord;
            string className = owner switch
            {
                GameBot bot => bot.ClassName,
                GamePlayer player => player.CharacterClass?.Name,
                _ => null,
            };
            bots.Add(new BotCpuBot(counter.Kind, owner.Name, owner.Level, className, owner.CurrentRegion?.ID ?? 0,
                record?.ObjectiveKind, record?.Activity, ticks, calls));
        }

        return bots;
    }

    // A run closes (3.7): Enabled false, then its final window, to the instant metering stopped (an off command's, else
    // now), then the BOT_CPU_STATE off line, then its timer disposed, all under the lock. A tick of this run waiting
    // for the lock then finds it closed.
    private static void Close(MeterRun run, BotCpuOffSource source, string by, Instant? end = null)
    {
        BotCpuMeter.Disable();
        WriteWindow(run, end ?? Instant.Now());
        WriteLog(ELogLevel.Info, BotCpuReport.StateOffLine(source, by));
        run.Timer?.Dispose();
        _open = null;
        Volatile.Write(ref _result, BotCpuResult.Off);
    }

    // The open run's result, swapped in whole (3.7).
    private static void Publish(MeterRun run) => Volatile.Write(ref _result, new BotCpuResult(true, run.Number,
        run.ByCommand, run.OnSince, run.StopsAt?.Time, run.WindowStart.Time, run.WindowStart.Timestamp, run.Last,
        run.LastProcess, run.Since));
}

/// <summary>
/// What the report published last (3.7), for <c>/botcpu</c>: immutable, and swapped in whole at each change (on, a
/// window, reset, a new stop time, off), so the command reads one consistent state and never builds a report.
/// </summary>
/// <param name="On">Whether a run is open; when false, nothing else is set.</param>
/// <param name="Run">The open run's number (<see cref="BotCpuMeter.RunNumber"/>).</param>
/// <param name="ByCommand">Whether a GM's <c>/botcpu on</c> opened it, else the property (2.4).</param>
/// <param name="OnSince">When it opened, the server's local time.</param>
/// <param name="StopsAt">Its stop time, local, as the chat shows it; null for none (4.3).</param>
/// <param name="WindowStart">When the open window started, local: its lines come a window's length later (2.4).
/// </param>
/// <param name="WindowStartTimestamp">The same instant as a <see cref="Stopwatch"/> timestamp, the clock a window
/// is measured with (2.2): <see cref="BotCpuScript.CommandOff"/> gives the seconds since then for
/// <c>/botcpu off</c>'s reply (2.4).</param>
/// <param name="Last">The last window written, with the top 5 of each bot kind; null before the first.</param>
/// <param name="LastProcess">That window's process values.</param>
/// <param name="Since">The totals since on or reset (2.4).</param>
public sealed record BotCpuResult(bool On, int Run, bool ByCommand, DateTime OnSince, DateTime? StopsAt,
    DateTime WindowStart, long WindowStartTimestamp, BotCpuWindow Last, BotCpuProcess LastProcess, BotCpuSince Since)
{
    /// <summary>The meter off: no run open.</summary>
    public static BotCpuResult Off { get; } = new(false, 0, false, default, null, default, 0, null, null, null);
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (644 was
seen); this task adds six CS0649 warnings, on `_queueHook`, `_timerHook`, `_nowHook`, `_timestampHook`, `_zoneHook`
and `_csvFile`, as Task 8's hooks have.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuScript."`
Expected: `Passed!  - Failed:     0, Passed:   109, Skipped:     0, Total:   109` (exit code 0): Tasks 7 and 8's 48
and this task's 61, in about 3 s. Run as root or off Linux, Task 8's two tests are Inconclusive, as that task says.

- [ ] **Step 5: Check the files' form.** Both files are LF, with no BOM and no tab. The only line over 120 characters
is still Task 7's description string (181 characters):

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs False 0 0 True 181
source/server/Tests/UnitTests/UT_BotCpuScript.cs False 0 0 True 120
```

Run: `git diff --check`
Expected: no output (exit code 0).

- [ ] **Step 6: Run the wider checks.**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpu"`
Expected: `Passed!  - Failed:     0, Passed:   310, Skipped:     0, Total:   310` (the meter's three classes: 249 after
Task 8, and 61).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   954, Skipped:     0, Total:   954` (893 after Task 8, and this task's
61).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3742, Skipped:     0, Total:  3742` (3681 after Task 8, and 61; about half
a minute of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

No deploy file changed, so of the deploy tests only the meter's own and the workflow check run:

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 21 tests` and `OK`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

Run: `git status --short`
Expected: only these lines (`serverconfig.xml` is ignored, so it does not show):

```
 M source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs
 M source/server/Tests/UnitTests/UT_BotCpuScript.cs
```

Run: `git diff --numstat`
Expected:

```
489	2	source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs
1735	2	source/server/Tests/UnitTests/UT_BotCpuScript.cs
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuScript.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs
git commit -F - <<'EOF'
feat(cpu-meter): the report side

BotCpuScript now runs the meter's report side (spec 3.7, 3.8, 4.1, 4.3, 4.4), off the game loop. A GM's command does
only this on its game-loop thread: CommandOff calls BotCpuMeter.Disable and takes its instant; then CommandOn,
CommandOff and CommandReset put a job in a first-in-first-out queue and call ThreadPool.QueueUserWorkItem, with no
file, /proc read, log write or lock. Every change runs under one report lock: a queued work item waits for it, a tick
of the run's 1-second timer skips its second while it is held, and whoever holds it first runs every job waiting, in
order, then its own work. The started handler opens a run at once when bot_cpu_meter is on (BOT_CPU_STATE on
source=property), and the stopped handler runs the jobs waiting, then closes the open run (off source=stop), both on
their own thread, not queued.

A run opens with Enable, then a fresh baseline, the process sample, its instant and its timer. Each tick samples the
registry; the first tick at or after the window's 60 s writes the window (the slots less the baseline, each bot's
counter since its last read, the process values over their own two samples, the game ticks), its lines, then its CSV
rows, and the next window starts there with the last registry sample. The stop time is checked on the Stopwatch and
shown as the local time of the minutes added in UTC, so a summer-time change moves neither; the first tick at or after
it closes the run (off source=timer). An on job on a running meter sets or clears the stop time and writes the on line
again. Closing writes the final window (none under 5 s), the off line, then disposes the timer, so a tick that waited
finds the run closed. An off command's final window ends at the command's instant, and CommandOff returns its seconds,
so /botcpu off's reply and the job agree on the 5 s rule. The off and reset jobs act on the run open when they run, so
on then off queued together end off and an off for a run already closed writes nothing. Every tick, job and handler
runs inside a catch: the first exception turns the meter off with no final window, then BOT_CPU_ERROR <step> and off
source=error, each log call in a try of its own and the error line made inside it, and the catch never rethrows, so
the jobs after a failed one still run; a failed CSV write only loses its rows. The top reads a bot's region once, as an
instance's collapse can set it to null. BotCpuResult, swapped in whole at each change, is what /botcpu reads.

UT_BotCpuScript holds the queued work items, ticks a timer of its own and moves its own clocks (test hooks), and pins
off and on from a command, one at a time in both orders, no exception escaping a tick, a job or a handler, even when the
error line cannot be written, the stop time and the handlers (6.1), a bot that leaves mid-window (Review Focus 3), a
tick with every platform reader failing (4) and a burst of on, off, on, off on the real thread pool (5). One test each
runs the real timer and the real thread pool.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `2 files changed, 2224 insertions(+), 4 deletions(-)`.

Review: four reviews and three fix rounds (da9a734, 2d20f62, 052ce4a after the feat commit f07d680) shaped this section,
and the steps above show their final code. Review 1 (Important: neither the real timer's period nor the top's fields
were pinned) gave the `TickMilliseconds` assert, two real windows with their 1.9 s lower bound, and
`TheTopListsEachBotWithItsCountersKindAndWhatTheChatShowsOfIt`. Its Minors gave `[TearDown]`'s wait for real timers and
the burst's `CountdownEvent`, the handlers' lock test, `BOT_CPU_ERROR`'s step names, the last registry sample's test,
the stop time on the Stopwatch (`StopTime`, the clock-change test) and the off command's instant (`Instant`,
`CommandOff` returning `double?`, `BotCpuResult.WindowStartTimestamp`, three tests). Review 2 (Important: no off job for
a closed run) gave `TwoOffsQueuedTogetherCloseTheRunOnce`, `AnOffQueuedAsTheStopTimeClosesTheRunDoesNothing` and
`AnOffWithTheMeterOffDoesNothing`; its Minors gave the process rates over their own samples (`ProcessTaken`), the region
read once, the stop time shown through UTC (`_zoneHook`, `SummerTimeZone`), `TheCommandsNeverWriteTheProperty`, and
`NoCommandTakesTheReportLock` with `ReturnsWithTheLockHeld`. Review 3 (Important: `ThreadPool.QueueUserWorkItem` never
ran under test) gave `TheRealThreadPoolRunsEachCommandsJob`; its Minors gave the server's own clocks test with
`FiveHoursAheadZone`, and `OffJob`'s compare with `OnSinceTimestamp` with its test. The last review was not approved.
Its Important gap was a test gap, and two tests now close it, with no production change (the build's test-only commit
c28fae7, after Task 11). `ABotCpuErrorThatCannotBeWrittenStillLeavesTheOffLine` holds that each of `Fail`'s two log
lines sits in its own `try` and that the error line is made inside its own;
`WithEveryLogCallThrowingTheJobAfterAFailedOneStillRuns` holds that `Fail` never rethrows. Each of the three mutations
(both lines in one `try`, the error line built outside `TryWriteLog`, an unguarded off line) passed the 104 tests before
and fails one of these now. Its Minors changed no line and are left for the owner: a `Throws.Nothing` around a tick
cannot fail, as `OnTimer` catches everything (the `Enabled` and log asserts are the check), and the fix commits'
subjects were long. Two spec issues stand: an off command's final window has `win=` to the command but its process
values to the job (rates over their own two samples; `gcPauseMs`, `gen2`, `loopWaitMs` and `throttledMs` counted to the
job), against the letter of 2.2; and Task 10's "the first lines come at" time may need the same way through UTC across a
summer-time change.

### Task 10: `/botcpu`

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs` (the command handler)
- Modify: `source/server/Tests/UnitTests/UT_BotCpuScript.cs:13-14` (the `DOL.GS.PacketHandler` using goes in between
  them), `:29` (the header comment's last line), `:2590-2593` (the `/botcpu` tests go in at the class's end: 35 test
  cases in 18 methods, with their constants and helpers)
- Modify: `deploy/tests/smoke.sh:52` (three lines go in after the GM-only group's echo; `/epic`'s line is :51)
- Modify: `deploy/tests/test_bot_cpu.py:11-12` (the docstring's last sentence), `:359-362` (`COMMAND_LOADED` and
  `SmokeTests` go in before `EndingsTests`)
- Test: `source/server/Tests/UnitTests/UT_BotCpuScript.cs`, `deploy/tests/test_bot_cpu.py`

No CI filter or `test_workflows.py` change: the command's tests are in `UT_BotCpuScript`, which Task 7 put in both
(6.1, 6.4).

**Spec:** 2.4 (the command: the attribute, `/bot`, every reply of the table, the status and top lines), 3.7 (the
command's side: it reads the published result and only turns the meter off or queues a job), 4.3 (the commands never
write the property), 6.1 `UT_BotCpuScript` (the command: the exact attribute, minutes 0, 1441 and `x` refused, a kind
that is not a bot kind refused, `&botcpu` the only name that starts with `&bot`; off and on from a command queue one
job before any file, `/proc` read or log write), 6.3 (the smoke test's two lines), Review Focus 1 (arguments in another
case).

**Interfaces:**
- Consumes:
  - Task 1, `BotCpuMeter.cs`: `public enum BotCpuKind` (:29), of which `Gamebot`, `Companion`, `Playerbot` and
    `Piloted` are the bot kinds (2.5). Task 3, in the tests: `public static bool Enabled` (:203).
  - Task 6, `BotCpuReport.cs` (every reply's text): `public const string OffStatusText` (:552), `AlreadyOffText`
    (:555) and `ResetText` (:558); `public static IReadOnlyList<string> StatusText(DateTime onSince, bool byCommand,
    DateTime? stopsAt, DateTime now, BotCpuWindow last, BotCpuProcess process, BotCpuSince since)` (:775);
    `public static string NoWindowYetText(DateTime onSince)` (:810), `OnText()` (:814), `OnText(int minutes,
    DateTime stopsAt)` (:818), `AlreadyOnText(DateTime? stopsAt)` (:822) and `OffText(double openSeconds)` (:829);
    `public static IReadOnlyList<string> TopText(BotCpuWindow last, BotCpuKind kind)` (:838).
  - Task 9, `BotCpuScript.cs`: `public static BotCpuResult Result` (:514), `public static DateTime LocalNow()` (:518),
    `public static DateTime? CommandOn(string by, int? minutes)` (:539), `public static double? CommandOff(string by)`
    (:556), `public static void CommandReset()` (:567); `BotCpuResult` (:900) with `On`, `ByCommand`, `OnSince`,
    `StopsAt`, `Last`, `LastProcess` and `Since`. In the tests: Task 7's `BOT_CPU_METER` (:30) and Task 9's
    `OnServerStarted(DOLEvent e, object sender, EventArgs args)` (:575).
  - In `UT_BotCpuScript` (Tasks 8 and 9, lines at this task's start): `ProcSelfHook` (:55), `_logs` (:64),
    `_procSelf` (:66), `Csv` (:637), `Hidden` (:942), `QueueHook` (:945), `ReportLockField` (:954), `Gm` (`"Aldwin"`,
    :964), `Gamebot` (:965), `Npc` (:966), `OnLine` (:967), `OffLine` (:968), `_queued` (:990), `_timers` (:994),
    `At(int hour, int minute, int second, int ms = 0)` (:1019), `Advance(double seconds)` (:1023),
    `Seconds(int count)` (:1046), `RunQueued()` (:1053), `TurnOn(int? minutes = null)` (:1062), `QueuedJobs` (:1068),
    `Population(int bots)` (:1077), `Slot()` (:1080), `Charge(long[] slot, BotCpuKind kind, BotCpuStage stage,
    double ms, long calls)` (:1092), `ChargeBot(GameObject bot, double ms, long calls, BotCpuKind kind = Gamebot)`
    (:1100), `Bot(string name)` (:1120), `PutInRegion(GameObject obj, ushort id)` (:1131), `Logged()` (:1163),
    `AssertOff()` (:1202), `GamebotLine(int win, string msSec, string msPerBotSec, string callsPerBotSec)` (:1212)
    and `ReturnsWithTheLockHeld(Action command)` (:1983). `[SetUp]` starts the test clock at 14:02:00 local (:92).
  - In `deploy/tests/test_bot_cpu.py` (Task 7): `read(name)` (a file under `deploy/`, UTF-8), which `SmokeTests`
    reads `tests/smoke.sh` with.
  - Upstream: `AbstractCommandHandler` (`commands/AbstractCommandHandler.cs:6`) with `DisplaySyntax(GameClient client)`
    (:69), which sends the attribute's description and usage lines, and `DisplayMessage(GameClient client, string
    message, params object[] objs)` (:60), which runs `string.Format` (the handler does not use it); `ICommandHandler`
    (`commands/ICommandHandler.cs:27`); `CmdAttribute` (`commands/CmdAttribute.cs:29`: `Cmd`, `Aliases`, `Header`,
    `Level`, `Description`, `Usage`); `ChatUtil.SendSystemMessage(GameClient target, string message)`
    (`gameutils/ChatUtil.cs:24`); `ScriptMgr.GuessCommand` (`gameutils/ScriptMgr.cs:109-125`), the command's log line at
    load (:241) and `pars[0] = myCommand.m_cmd;` (:373). In the tests: `IPacketLib.SendMessage(string msg, eChatType
    type, eChatLoc loc)` (`packets/Server/IPacketLib.cs:628`), `GameClient.ClientState` (`GameClient.cs:36`), `Player`
    (:56), `Out` (:90) and `IsPlaying` (:79), and `DOL.UnitTests.PetTestLanguageScope`
    (`Tests/UnitTests/PetTestLanguageScope.cs:10`).
- Produces, in `namespace DOL.GS.HearthDAoC`, `BotCpuCommand.cs` (lines at this task's commit):
  - `public sealed class BotCpuCommandHandler : AbstractCommandHandler, ICommandHandler` (:23), with the spec's 2.4
    `[CmdAttribute]` byte for byte (:14-22): `&botcpu`, `ePrivLevel.GM`, no alias, no header.
  - `public const int MaxMinutes = 1440;` (:26).
  - `public void OnCommand(GameClient client, string[] args)` (:28): `Run(client.Player?.Name, args)`; null gives
    `DisplaySyntax(client)`, otherwise each line goes out through `ChatUtil.SendSystemMessage(client, line)`.
  - `public static IReadOnlyList<string> Run(string gm, string[] args)` (:51): the reply's lines, or null for the
    syntax lines. `args[0]` is the registered name and is not read.
  - Private: `Minutes(string)` -> `int?` and `BotKind(string)` -> `BotCpuKind?`.
  - For Task 11: `BotCpuCommand.cs` is the fourth new file for the endings check (LF, no BOM, no tab, checked in
    Step 5). `test_bot_cpu.py` has `COMMAND_LOADED` and `SmokeTests` before `EndingsTests`.

Four readings of 2.4 and 3.7 decide the code. The "state" of 2.4's table is the published `BotCpuScript.Result`, and
`/botcpu on` reads `Result.On` before `CommandOn` queues its job, because the real thread pool can run the job before
the reply is made. `/botcpu off` always calls `CommandOff`, even when the published result is off: `CommandOff` turns
the meter off and queues the off job, so an on queued just before still ends off (3.7); its reply is the already-off
text when `CommandOff` returns null. `/botcpu reset` with the meter off gives the off text and queues nothing (the spec
only says its job would do nothing). And "anything else" includes a word too many (`on 30 x`, `off now`, `reset x`,
`top gamebot x`); minutes are digits only, 1-1440 (a sign or a space is refused, as in `-5` or `+30`, and `030` is
30); a kind is one of the four bot kinds by name, in any case (`player`, `monster` and `unattributed` are refused: an
explicit switch, as `Enum.TryParse` would also take numbers).

- [ ] **Step 1: Write the failing tests.** The C# tests go in `UT_BotCpuScript.cs`, in seven edits. Each old text
occurs once in the file as the edit before it leaves it (each later edit goes in before the class's closing brace).
First the using (`IPacketLib`, for the GM client of the last test) and the header comment's last lines:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
using DOL.GS.HearthDAoC;
using DOL.GS.PlayerClass;
=======
using DOL.GS.HearthDAoC;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
>>>>>>> NEW
<<<<<<< OLD
// The tests hold the queued work items, tick a timer of their own and move a clock of their own (test hooks).
=======
// The tests hold the queued work items, tick a timer of their own and move a clock of their own (test hooks). Last, the
// GM's /botcpu (2.4): its attribute, its replies from the result the report published, the arguments it refuses in any
// state, and its changes, which only turn the meter off or queue a job.
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (2 block(s))`.

Then the section's constants and helper, and the attribute's two tests (6.1). `BotCpu(words)` calls the handler's
`Run` as ScriptMgr does: the registered name first (`gameutils/ScriptMgr.cs:373` puts it there whatever was typed),
then the words split on spaces. It returns the reply's lines, or null for the syntax lines. The fourth usage line is
split in two strings to keep the test file within 120 columns. `BotCpuIsTheOnlyCommandNameThatStartsWithBot` reads
every `[CmdAttribute]` in the GameServer assembly, among which `ScriptMgr.GuessCommand` (:109-125) guesses a prefix:
a GM's `/bot` runs `/botcpu` only while no other name or alias starts with `&bot`.

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }
}
=======
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }

    // ---- /botcpu (2.4, 3.7, 6.1)

    // The command's description and syntax lines (2.4), which a GM sees for arguments it refuses.
    private const string CommandDescription =
        "HearthDAoC: measure the game loop's CPU per kind of bot (BOT_CPU lines in the server log)";

    private static readonly string[] CommandUsage =
    [
        "/botcpu - status, the last minute and the totals since on",
        "/botcpu on [minutes] - start, for 1-1440 minutes, or until off or the next start",
        "/botcpu off - stop and write the last partial minute",
        "/botcpu top [gamebot|companion|playerbot|piloted] - the 5 costliest bots of the last minute " +
        "(gamebot if none given)",
        "/botcpu reset - start the totals since on again",
    ];

    private const string OffStatus = "Bot CPU meter: off. /botcpu on [minutes] turns it on.";
    private const string WhereTheLinesGo = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

    // /botcpu with these words after it, typed by the GM: its reply's lines, or null for the syntax lines.
    private static IReadOnlyList<string> BotCpu(string words = "") =>
        BotCpuCommandHandler.Run(Gm, words.Length == 0 ? ["&botcpu"] : ["&botcpu", .. words.Split(' ')]);

    [Test]
    public void BotCpuIsAGmCommandWithTheSpecsAttribute()
    {
        // 2.4: &botcpu, for GMs, with no alias, found by ScriptMgr as a command handler of the GameServer assembly.
        Type handler = typeof(BotCpuCommandHandler);
        CmdAttribute[] attributes = handler.GetCustomAttributes<CmdAttribute>(false).ToArray();
        Assert.That(attributes, Has.Length.EqualTo(1));
        Assert.That(attributes[0].Cmd, Is.EqualTo("&botcpu"));
        Assert.That(attributes[0].Aliases, Is.Null);
        Assert.That(attributes[0].Header, Is.Null);
        Assert.That(attributes[0].Level, Is.EqualTo((uint)ePrivLevel.GM));
        Assert.That(attributes[0].Description, Is.EqualTo(CommandDescription));
        Assert.That(attributes[0].Usage, Is.EqualTo(CommandUsage));
        Assert.That(handler.GetInterface("DOL.GS.Commands.ICommandHandler"), Is.Not.Null);
        Assert.That(handler.Assembly, Is.SameAs(typeof(GameServer).Assembly));
    }

    [Test]
    public void BotCpuIsTheOnlyCommandNameThatStartsWithBot()
    {
        // 2.4: a command resolves its exact name first, then the one command the caller may use whose name or alias
        // starts with what was typed, in any case (ScriptMgr.GuessCommand). /bot is not registered (section 1), so a
        // GM's /bot runs /botcpu: on purpose, as long as no other command name or alias starts with &bot.
        string[] names = typeof(GameServer).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<CmdAttribute>(false))
            .SelectMany(attribute => new[] { attribute.Cmd }.Concat(attribute.Aliases ?? []))
            .Where(name => name.StartsWith("&bot", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.That(names, Is.EqualTo(new[] { "&botcpu" }));
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

Off and on from a command (6.1). `[SetUp]`'s hook holds the queued work item. With `_procSelf` null, a `/proc` read
would throw out of the command (`Path.Combine(null, "task")` in the process sample), so "before any `/proc` read" is
checked; no log line and no CSV file check the rest. Each test then runs the job and sees the report side do its work:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(names, Is.EqualTo(new[] { "&botcpu" }));
    }
}
=======
        Assert.That(names, Is.EqualTo(new[] { "&botcpu" }));
    }

    [Test]
    public void BotCpuOffTurnsTheMeterOffAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        // 6.1, 3.7: on its game-loop thread the command only turns the meter off and queues the job; its reply comes
        // from the published result, 37 s into the open window.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 111, 3_330);
        Seconds(37);
        lock (_logs)
            _logs.Clear();
        // A /proc read would throw here: the process sample has no folder to read.
        ProcSelfHook.SetValue(null, null);

        IReadOnlyList<string> reply = BotCpu("off");

        Assert.That(reply, Is.EqualTo(new[] { "Bot CPU meter off. The last 37 s are being written." }));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(BotCpuScript.Result.On, Is.True, "the report has not run the job yet");

        // The job, on the report side: the last 37 s, then the GM's off line.
        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[]
            { GamebotLine(37, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=37", OffLine }));
        AssertOff();
    }

    [Test]
    public void BotCpuOnLeavesEnabledAsItIsAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        // 6.1, 3.7: Enabled goes true only in the on job, on the report side.
        ProcSelfHook.SetValue(null, null);

        IReadOnlyList<string> reply = BotCpu("on 30");

        Assert.That(reply, Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(_timers, Is.Empty);

        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));

        // On a running meter: Enabled stays true, one job queued, still before any file, /proc read or log write.
        Seconds(10);
        ProcSelfHook.SetValue(null, null);
        reply = BotCpu("on");
        Assert.That(reply,
            Is.EqualTo(new[] { "Bot CPU meter already on; it now runs until /botcpu off or the next start." }));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Has.Count.EqualTo(1));

        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(Logged()[^1], Is.EqualTo(OnLine));
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

Each reply of the 2.4 table, with its exact text. The test clock starts at 14:02:00, so a 30-minute stop is 14:32 and
the first window's lines come at 14:03. The status's last-minute line holds the test process's own process values
(its `offLoopCpuPct` is `-`, as the test process has no loop threads), so the test checks that line's start and end,
and the whole reply against `StatusText` made from the same result. A run the property started shows `by property`
and `no stop time`. `BotCpuOffRightAfterOnIsStillQueuedAndEndsOff` sends on and then off before the on job runs: the
reply says the meter is already off, as the published result still is, but the off job is queued all the same and
closes the run the on job opens. The top lines use Task 9's `Bot(name)` (level 34, a Cleric, no region) and set a
`PersistentRecord` by reflection, as Task 9's top test does:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);
    }
}
=======
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);
    }

    [Test]
    public void BotCpuShowsTheStatusOfThePublishedResult()
    {
        // 2.4: off; on with no window yet; on with a window, the status line, the last minute, its kind lines and the
        // totals since on, made by BotCpuReport from the result the report published. None queues a job.
        Assert.That(BotCpu(), Is.EqualTo(new[] { OffStatus }));

        Population(3);
        TurnOn(30);
        Assert.That(BotCpu(), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by command, 0 min; stops at 14:32).",
            "No full minute yet; the first lines come at 14:03.",
        }));

        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        Seconds(60);
        Advance(30);
        IReadOnlyList<string> status = BotCpu();
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(status, Is.EqualTo(BotCpuReport.StatusText(result.OnSince, result.ByCommand, result.StopsAt,
            At(14, 3, 30), result.Last, result.LastProcess, result.Since)));
        Assert.That(status, Has.Count.EqualTo(4));
        Assert.That(status[0], Is.EqualTo("Bot CPU meter: on since 14:02 (by command, 1 min; stops at 14:32)."));
        // The process values are the test process's own; it has no loop threads, so its off-the-loop share is -.
        Assert.That(status[1],
            Does.StartWith("Last minute (60 s, 30.0 ticks/s): process ").And.EndWith(" off the loop."));
        Assert.That(status[2], Is.EqualTo("gamebot n=3.0: 1.000 ms/s per bot (3.0 ms/s in all); most in Npc 1.000"));
        Assert.That(status[3], Is.EqualTo("Since on (1 min): gamebot 1.000 ms/s per bot."));
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        BotCpu("off");
        RunQueued();
        Assert.That(BotCpu(), Is.EqualTo(new[] { OffStatus }));
    }

    [Test]
    public void BotCpuShowsARunThePropertyStartedAsByPropertyWithNoStopTime()
    {
        // 2.4: the status line's other variants, through the command: by property, no stop time.
        BotCpuScript.BOT_CPU_METER = true;
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);

        Assert.That(BotCpu(), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by property, 0 min; no stop time).",
            "No full minute yet; the first lines come at 14:03.",
        }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
    }

    [Test]
    public void BotCpuOnRepliesWithItsStopTimeOrNone()
    {
        // 2.4: on and on <minutes>, with the meter off, then on.
        Assert.That(BotCpu("on"),
            Is.EqualTo(new[] { "Bot CPU meter on until /botcpu off or the next start" + WhereTheLinesGo }));
        RunQueued();
        Seconds(10);
        Assert.That(BotCpu("on 30"), Is.EqualTo(new[] { "Bot CPU meter already on; it now stops at 14:32." }));
        RunQueued();
        Assert.That(BotCpu("on"),
            Is.EqualTo(new[] { "Bot CPU meter already on; it now runs until /botcpu off or the next start." }));
        RunQueued();
        Assert.That(Logged(),
            Is.EqualTo(new[] { OnLine, "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OnLine }));

        BotCpu("off");
        RunQueued();
        Assert.That(BotCpu("on 30"),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
    }

    [TestCase("on 1", "Bot CPU meter on for 1 minute (until 14:03)", "1")]
    [TestCase("on 1440", "Bot CPU meter on for 1440 minutes (until 14:02)", "1440")]
    public void TheMinutesRunFromOneToADay(string words, string reply, string minutes)
    {
        // 2.4: 1-1440 minutes; 1440 stops at the same time the next day.
        Assert.That(BotCpu(words), Is.EqualTo(new[] { reply + WhereTheLinesGo }));
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=" + minutes }));
        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(At(14, 2, 0).AddMinutes(int.Parse(minutes))));
    }

    [TestCase(37, "Bot CPU meter off. The last 37 s are being written.")]
    [TestCase(3, "Bot CPU meter off. The last 3 s were too short to write.")]
    public void BotCpuOffRepliesWithTheLastSecondsOrThatItIsAlreadyOff(int seconds, string reply)
    {
        // 2.4, 2.2: the open window's seconds, written from 5 s; then, off, a second off changes nothing.
        TurnOn();
        Seconds(seconds);

        Assert.That(BotCpu("off"), Is.EqualTo(new[] { reply }));
        RunQueued();
        AssertOff();

        Assert.That(BotCpu("off"), Is.EqualTo(new[] { "Bot CPU meter is already off." }));
        RunQueued();
        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU_STATE ", StringComparison.Ordinal)),
            Is.EqualTo(2));
        Assert.That(Logged()[^1], Is.EqualTo(OffLine));
        AssertOff();
    }

    [Test]
    public void BotCpuOffRightAfterOnIsStillQueuedAndEndsOff()
    {
        // 3.7: on then off ends off. The on job has not run, so the published result is still off and the reply says
        // so; the off is queued all the same, after the on, and its job closes the run the on job opens.
        Assert.That(BotCpu("on 30"),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));

        Assert.That(BotCpu("off"), Is.EqualTo(new[] { "Bot CPU meter is already off." }));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(QueuedJobs, Is.EqualTo(2));

        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OffLine }));
        AssertOff();
    }

    [Test]
    public void BotCpuTopListsTheLastMinutesCostliestBotsOfABotKind()
    {
        // 2.4: off, the off text; on with no window yet, when the first lines come; then the top of the last window,
        // for gamebot when no kind is given, or that no bot of the kind was charged. None queues a job.
        Assert.That(BotCpu("top"), Is.EqualTo(new[] { OffStatus }));

        Population(2);
        TurnOn();
        Assert.That(BotCpu("top companion"),
            Is.EqualTo(new[] { "No full minute yet; the first lines come at 14:03." }));

        GameBot aldric = Bot("Aldric");
        typeof(GameBot).GetField("<PersistentRecord>k__BackingField", Hidden)!.SetValue(aldric,
            new OfflineWorldBotRecord { ObjectiveKind = "Rvr", Activity = "Guarding Caer Benowyc" });
        PutInRegion(aldric, 163);
        GameBot brennos = Bot("Brennos");
        ChargeBot(aldric, 90, 1_800);
        ChargeBot(brennos, 60, 1_800);
        Seconds(60);

        string[] gamebots =
        [
            "1. Aldric (34 Cleric, region 163): 1.500 ms/s, 30 calls/s, Rvr, \"Guarding Caer Benowyc\"",
            "2. Brennos (34 Cleric, region 0): 1.000 ms/s, 30 calls/s, -, \"\"",
        ];
        Assert.That(BotCpu("top"), Is.EqualTo(gamebots));
        Assert.That(BotCpu("top gamebot"), Is.EqualTo(gamebots));
        foreach (string kind in new[] { "companion", "playerbot", "piloted" })
            Assert.That(BotCpu("top " + kind), Is.EqualTo(new[] { $"No {kind} was charged in the last minute." }));
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        BotCpu("off");
        RunQueued();
        Assert.That(BotCpu("top"), Is.EqualTo(new[] { OffStatus }));
        GC.KeepAlive(new[] { aldric, brennos });
    }

    [Test]
    public void BotCpuResetStartsTheTotalsAgainOnlyWhileOn()
    {
        // 2.4: off, the off text and no job; on, the totals since on start again in the job.
        Assert.That(BotCpu("reset"), Is.EqualTo(new[] { OffStatus }));
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        TurnOn();
        Assert.That(BotCpu("reset"), Is.EqualTo(new[] { "Bot CPU totals reset." }));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(BotCpuScript.Result.Since.Reset, Is.False, "the report has not run the job yet");
        RunQueued();
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(BotCpuMeter.Enabled, Is.True);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

The refused arguments, each with the meter off and then on: the syntax lines (null), no job queued, nothing turned
off, the stop time and the log unchanged. Then the words and the kinds in another case (Review Focus 1):

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        Assert.That(BotCpuMeter.Enabled, Is.True);
    }
}
=======
        Assert.That(BotCpuMeter.Enabled, Is.True);
    }

    [TestCase("x")]
    [TestCase("on 0")]
    [TestCase("on 1441")]
    [TestCase("on x")]
    [TestCase("on -5")]
    [TestCase("on 30 x")]
    [TestCase("off now")]
    [TestCase("top monster")]
    [TestCase("top player")]
    [TestCase("top unattributed")]
    [TestCase("top x")]
    [TestCase("top gamebot x")]
    [TestCase("reset x")]
    public void RefusedArgumentsGetTheSyntaxLinesAndChangeNothing(string words)
    {
        // 2.4: anything else, a kind that is not a bot kind, minutes out of range: the syntax lines, the meter off or
        // on, with nothing turned off and no job queued.
        Assert.That(BotCpu(words), Is.Null);
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        Population(3);
        TurnOn(30);
        Seconds(60);
        List<string> logged = Logged();
        Assert.That(BotCpu(words), Is.Null);
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(At(14, 32, 0)));
        Assert.That(Logged(), Is.EqualTo(logged));
    }

    [TestCase("ON 30")]
    [TestCase("On 30")]
    public void OnInAnotherCaseIsAccepted(string words)
    {
        // Review Focus 1: case is ignored, as HEARTHDAOC_BOT_CPU ignores it.
        Assert.That(BotCpu(words),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));
        Assert.That(BotCpu("Reset"), Is.EqualTo(new[] { "Bot CPU totals reset." }));
        Assert.That(BotCpu("OFF"), Is.EqualTo(new[] { "Bot CPU meter off. The last 0 s were too short to write." }));
        RunQueued();
        AssertOff();
    }

    [TestCase("top GameBot")]
    [TestCase("TOP GAMEBOT")]
    [TestCase("Top gamebot")]
    public void ATopKindInAnotherCaseIsAccepted(string words)
    {
        // Review Focus 1: case is ignored, as HEARTHDAOC_BOT_CPU ignores it.
        Population(1);
        TurnOn();
        GameBot aldric = Bot("Aldric");
        ChargeBot(aldric, 90, 1_800);
        Seconds(60);

        Assert.That(BotCpu(words),
            Is.EqualTo(new[] { "1. Aldric (34 Cleric, region 0): 1.500 ms/s, 30 calls/s, -, \"\"" }));
        Assert.That(BotCpu("top Companion"), Is.EqualTo(new[] { "No companion was charged in the last minute." }));
        GC.KeepAlive(aldric);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

3.7 through the command. In `TheRepliesSpeakOfTheStateBeforeTheirJobRuns` the queue hook runs each job inside its
queueing, as the thread pool may: `on 30` must still say the meter was off, and `off` gives the last seconds of the run
it ends. `NoBotCpuReplyWaitsForTheReportLock` holds the report lock on another thread and sends eight commands; Task
9's `ReturnsWithTheLockHeld` tells whether each one returned. `reset`, `on 30`, `on` and `off` queue a job each, so 4
are queued:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        GC.KeepAlive(aldric);
    }
}
=======
        GC.KeepAlive(aldric);
    }

    [Test]
    public void TheRepliesSpeakOfTheStateBeforeTheirJobRuns()
    {
        // 3.7: a command's job can run on the thread pool before the command replies. Here each job runs inside its
        // queueing: on still says the meter was off, and off gives the last seconds of the run it ends.
        QueueHook.SetValue(null, (Action<WaitCallback>)(work => work(null)));

        Assert.That(BotCpu("on 30"),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
        Assert.That(BotCpuScript.Result.On, Is.True);
        Seconds(37);
        Assert.That(BotCpu("off"), Is.EqualTo(new[] { "Bot CPU meter off. The last 37 s are being written." }));
        AssertOff();
    }

    [Test]
    public void NoBotCpuReplyWaitsForTheReportLock()
    {
        // 2.4, 3.7: the command never builds a report: it reads the result the report published and, for a change,
        // queues a job. With the report lock held by another thread, each reply comes at once.
        Population(3);
        TurnOn();
        Seconds(60);
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        List<string> waited = [];
        foreach (string words in new[] { "", "top", "top companion", "reset", "on 30", "on", "off", "x" })
        {
            if (!ReturnsWithTheLockHeld(() => BotCpu(words)))
                waited.Add(words);
        }

        release.Set();
        holder.Join();

        Assert.That(waited, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(4));
        RunQueued();
        AssertOff();
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

Last, the handler itself, through a GM client of the test's own. `ChatRecorder` is an `IPacketLib` made by
`DispatchProxy` that keeps each `SendMessage` line and returns a default value for every other call; it is public, as
`DispatchProxy.Create` needs. `GmClient` makes a `GamePlayer` and a `GameClient` without their constructors and sets
only what the handler and `DisplaySyntax` read: the name, the client state `Playing` (`DisplaySyntax` returns at once
unless `IsPlaying`) and the player. `DisplaySyntax` sends the attribute's lines through `LanguageMgr`, which needs a
translation table: the pet tests' `PetTestLanguageScope` gives it an empty one, so each line comes back as it is. A
bot's activity with braces checks that the reply is not sent through `DisplayMessage`, whose `string.Format` throws on
`{the bridge}`:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py source/server/Tests/UnitTests/UT_BotCpuScript.cs <<'EDIT'
<<<<<<< OLD
        RunQueued();
        AssertOff();
    }
}
=======
        RunQueued();
        AssertOff();
    }

    // A GM's chat: each line the server sends the client.
    public class ChatRecorder : DispatchProxy
    {
        public readonly List<string> Lines = [];

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IPacketLib.SendMessage))
                Lines.Add((string)args[0]);
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType)
                : null;
        }
    }

    // A GM in game, the test's Gm, made without the constructors, whose chat the test reads.
    private static GameClient GmClient(out ChatRecorder chat)
    {
        GamePlayer gm = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(gm, Gm);
        GameClient client = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
        typeof(GameClient).GetField("<ClientState>k__BackingField", Hidden)!
            .SetValue(client, GameClient.eClientState.Playing);
        typeof(GameClient).GetField("<Player>k__BackingField", Hidden)!.SetValue(client, gm);
        client.Out = DispatchProxy.Create<IPacketLib, ChatRecorder>();
        chat = (ChatRecorder)client.Out;
        return client;
    }

    [Test]
    public void TheCommandSendsItsReplyAsItIsOrTheSyntaxLinesToTheGm()
    {
        // 2.4: the handler sends the reply's lines, with the GM's character as by=, or the attribute's syntax lines
        // for arguments it refuses. The syntax lines go through LanguageMgr, which gives an untranslated line back as
        // it is: the pet tests' language scope gives it no language files.
        using var language = new DOL.UnitTests.PetTestLanguageScope();
        GameClient client = GmClient(out ChatRecorder chat);
        BotCpuCommandHandler handler = new();

        handler.OnCommand(client, ["&botcpu", "on", "0"]);
        Assert.That(chat.Lines, Is.EqualTo(new[] { CommandDescription }.Concat(CommandUsage)));
        Assert.That(_queued, Is.Empty);

        chat.Lines.Clear();
        handler.OnCommand(client, ["&botcpu"]);
        Assert.That(chat.Lines, Is.EqualTo(new[] { OffStatus }));

        // A change, with the GM's character as by=. ScriptMgr puts the registered name first whatever was typed
        // (gameutils/ScriptMgr.cs:373), so a GM's /bot reaches the handler as this; that /bot resolves to /botcpu
        // (2.4) is BotCpuIsTheOnlyCommandNameThatStartsWithBot's.
        chat.Lines.Clear();
        handler.OnCommand(client, ["&botcpu", "on"]);
        Assert.That(chat.Lines,
            Is.EqualTo(new[] { "Bot CPU meter on until /botcpu off or the next start" + WhereTheLinesGo }));
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }));

        // Sent as it is: a bot's activity holds braces, which a format would take for its arguments.
        Population(1);
        GameBot aldric = Bot("Aldric");
        typeof(GameBot).GetField("<PersistentRecord>k__BackingField", Hidden)!.SetValue(aldric,
            new OfflineWorldBotRecord { ObjectiveKind = "Rvr", Activity = "Holding {0} at {the bridge}" });
        ChargeBot(aldric, 90, 1_800);
        Seconds(60);
        chat.Lines.Clear();
        handler.OnCommand(client, ["&botcpu", "top"]);
        Assert.That(chat.Lines, Is.EqualTo(new[]
            { "1. Aldric (34 Cleric, region 0): 1.500 ms/s, 30 calls/s, Rvr, \"Holding {0} at {the bridge}\"" }));
        GC.KeepAlive(aldric);
    }
}
>>>>>>> NEW
EDIT
```

Expected: `edited source/server/Tests/UnitTests/UT_BotCpuScript.cs (1 block(s))`.

Then the smoke test's checks (6.3). `smoke.sh` needs the CI image, so `test_bot_cpu.py` pins its two lines. The
tests find them by `&botcpu` or `BOT_CPU`, check that they are plain `grep -q` lines on `"$T/server.log"` after
`/epic`'s (not `logs_have` calls or a loop, which `deploy/tests/test_container_lines.py:44-46` would read as world-fix
lines), and run them in bash on sample logs, with a `fail()` like `smoke.sh`'s. `COMMAND_LOADED` is the line
`gameutils/ScriptMgr.cs:241` writes at load, in the layout of `config/logconfig.xml:20`. The docstring gains a
sentence, and the class goes in before `EndingsTests`:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_bot_cpu.py <<'EDIT'
<<<<<<< OLD
at start (4.1, 4.2).
"""
=======
at start (4.1, 4.2). The image's smoke test checks that a default start loads /botcpu for GMs only and leaves the
meter off (6.3); its two lines are run here on sample server logs.
"""
>>>>>>> NEW
<<<<<<< OLD
        self.assertEqual(found, [("server", sp.CPU_DESCRIPTION, "false")])


class EndingsTests(unittest.TestCase):
=======
        self.assertEqual(found, [("server", sp.CPU_DESCRIPTION, "false")])


# The server log's line for /botcpu at start (gameutils/ScriptMgr.cs:241, config/logconfig.xml:20): the command, the
# attribute's description and the plvl.
COMMAND_LOADED = ("10/20 14:02:00 | DBG | DOL.GS.ScriptMgr | ScriptMgr: Command - '&botcpu' - (HearthDAoC: measure the "
                  "game loop's CPU per kind of bot (BOT_CPU lines in the server log)) required plvl:2")


class SmokeTests(unittest.TestCase):
    """deploy/tests/smoke.sh's checks of the meter after the image's first, default start (spec 6.3)."""

    def smoke_lines(self):
        """smoke.sh's lines that check the meter."""
        return [line for line in read(os.path.join("tests", "smoke.sh")).splitlines()
                if "&botcpu" in line or "BOT_CPU" in line]

    def run_smoke_lines(self, log):
        """The lines, with a fail() like smoke.sh's (no docker logs), on a copy of the server log that holds log:
        (exit code, stderr)."""
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "server.log"), "w", encoding="utf-8") as f:
                f.write(log)
            script = "\n".join(["set -euo pipefail", 'fail() { echo "FAIL: $*" >&2; exit 1; }',
                                f"T={shlex.quote(tmp)}", *self.smoke_lines(), ""])
            r = subprocess.run(["bash", "-c", script], capture_output=True, text=True)
            return r.returncode, r.stderr

    def test_the_smoke_test_has_two_plain_grep_lines_after_epics(self):
        # Plain grep -q lines on the server log smoke.sh copied, not logs_have calls or a loop:
        # test_container_lines.py reads every logs_have string and smoke.sh's first loop as world-fix lines.
        lines = self.smoke_lines()
        self.assertEqual(len(lines), 2, lines)
        for line in lines:
            self.assertRegex(line, r'^(if )?grep -q "[^"]+" "\$T/server\.log"', line)
            self.assertNotIn("logs_have", line)
        smoke = read(os.path.join("tests", "smoke.sh"))
        self.assertLess(smoke.index("grep -q \"Command - '&epic' .* required plvl:2\""), smoke.index(lines[0]))

    def test_a_default_start_passes_with_botcpu_for_gms_and_no_state_line(self):
        log = f"10/20 14:01:59 | INF | DOL.GS.GameServer | Starting\n{COMMAND_LOADED}\n"
        self.assertEqual(self.run_smoke_lines(log), (0, ""))

    def test_botcpu_missing_or_open_to_players_or_a_meter_on_fails(self):
        for log in ("", COMMAND_LOADED.replace("plvl:2", "plvl:1") + "\n",
                    COMMAND_LOADED + "\n10/20 14:02:05 | INF | DOL.GS.HearthDAoC.BotCpuScript | "
                                     "BOT_CPU_STATE on source=property\n"):
            with self.subTest(log=log):
                code, stderr = self.run_smoke_lines(log)
                self.assertEqual(code, 1, stderr)
                self.assertTrue(stderr.startswith("FAIL: "), stderr)


class EndingsTests(unittest.TestCase):
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_bot_cpu.py (2 block(s))`.

- [ ] **Step 2: Run them to see them fail.** The unit tests need `source/server/CoreServer/config/serverconfig.xml`
(ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: the build fails (exit code 1) with 3 errors, all where the test file names the handler: `CS0103` at the
`BotCpu` helper's call to `Run`, and `CS0246` at `typeof(BotCpuCommandHandler)` and at the handler test's `new()`. Each error line starts with the file's absolute path and ends with the project's:

```
source/server/Tests/UnitTests/UT_BotCpuScript.cs(2618,9): error CS0103: The name 'BotCpuCommandHandler' does not exist in the current context [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuScript.cs(2624,31): error CS0246: The type or namespace name 'BotCpuCommandHandler' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
source/server/Tests/UnitTests/UT_BotCpuScript.cs(3055,9): error CS0246: The type or namespace name 'BotCpuCommandHandler' could not be found (are you missing a using directive or an assembly reference?) [source/server/Tests/Tests.csproj]
```

The summary (the warning total varies between builds; 857 was seen):

```
Build FAILED.
    857 Warning(s)
    3 Error(s)
```

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: the progress line `.............FFFF.........`, then four failures, all in `SmokeTests` (`smoke.sh` has no
meter line yet). `test_botcpu_missing_or_open_to_players_or_a_meter_on_fails` fails once per subtest, each with
`AssertionError: 0 != 1 :` (no line fails the bad log): `(log='')`, the log whose `&botcpu` line ends `required
plvl:1`, and the log with a `BOT_CPU_STATE on source=property` line. The fourth:

```
FAIL: test_the_smoke_test_has_two_plain_grep_lines_after_epics (tests.test_bot_cpu.SmokeTests.test_the_smoke_test_has_two_plain_grep_lines_after_epics)
AssertionError: 0 != 2 : []
```

then `Ran 24 tests` and `FAILED (failures=4)`. `test_a_default_start_passes_with_botcpu_for_gms_and_no_state_line`
passes already, as no line fails with no lines; the line count is the test that pins that the lines exist.

- [ ] **Step 3: Implement.** Create `source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs` with exactly the
code below (LF, no BOM: the Write tool, or a quoted heredoc). The attribute is the spec's 2.4 block byte for byte; its
top usage line is the only line over 120 columns. `Run` holds the logic and returns the reply's lines, or null for the
syntax lines, so the tests reach it without a client; `OnCommand` is a thin shell around it. Words and kinds are
lowered with the invariant culture and minutes are parsed with `NumberStyles.None` in the invariant culture, so the
rules do not depend on the server's culture. The reply's lines go out through `ChatUtil.SendSystemMessage`, not
`DisplayMessage`, whose `string.Format` throws on a brace in a GameBot's activity:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using DOL.GS.Commands;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: /botcpu, the GM's command for the bot CPU meter (#137; spec
// docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 2.4, 3.7, 4.3). It runs on its game-loop thread, so it never
// builds a report and never touches a file, /proc or the log: it reads the last result the report published
// (BotCpuScript.Result) and, for a change, calls BotCpuScript, which turns the meter off at once for off and queues a
// job for the report side. Every reply is made in BotCpuReport. Its words and kinds are read in any case, as
// HEARTHDAOC_BOT_CPU is. For a GM, /bot runs this command: no other command name starts with &bot.
[CmdAttribute(
    "&botcpu",
    ePrivLevel.GM,
    "HearthDAoC: measure the game loop's CPU per kind of bot (BOT_CPU lines in the server log)",
    "/botcpu - status, the last minute and the totals since on",
    "/botcpu on [minutes] - start, for 1-1440 minutes, or until off or the next start",
    "/botcpu off - stop and write the last partial minute",
    "/botcpu top [gamebot|companion|playerbot|piloted] - the 5 costliest bots of the last minute (gamebot if none given)",
    "/botcpu reset - start the totals since on again")]
public sealed class BotCpuCommandHandler : AbstractCommandHandler, ICommandHandler
{
    /// <summary>The most minutes <c>/botcpu on</c> takes (2.4): a day.</summary>
    public const int MaxMinutes = 1440;

    public void OnCommand(GameClient client, string[] args)
    {
        IReadOnlyList<string> reply = Run(client.Player?.Name, args);
        if (reply == null)
        {
            DisplaySyntax(client);
            return;
        }

        // Sent as they are, not through DisplayMessage, which formats its text: a bot's activity may hold a brace.
        foreach (string line in reply)
            ChatUtil.SendSystemMessage(client, line);
    }

    /// <summary>
    /// <c>/botcpu</c> with <paramref name="args"/> (the first is the command's registered name, <c>&amp;botcpu</c>,
    /// which ScriptMgr puts there whatever was typed; it is not read): does what it asks and returns the lines of its
    /// reply (2.4), or null for arguments it refuses (another word, a kind that is not a bot kind, minutes out of
    /// 1-1440, a word too many), which get the syntax lines and change nothing. Reads only the result the report
    /// published; a change only turns the meter off or queues a job (3.7).
    /// </summary>
    /// <param name="gm">The GM's name, for the <c>BOT_CPU_STATE</c> line.</param>
    /// <param name="args">The command's words, as ScriptMgr splits them.</param>
    public static IReadOnlyList<string> Run(string gm, string[] args)
    {
        if (args.Length <= 1)
            return Status();

        if (args.Length > 3)
            return null;

        string value = args.Length == 3 ? args[2] : null;
        return args[1].ToLowerInvariant() switch
        {
            "on" when value == null => On(gm, null),
            "on" when Minutes(value) is int minutes => On(gm, minutes),
            "off" when value == null => Off(gm),
            "top" when value == null => Top(BotCpuKind.Gamebot),
            "top" when BotKind(value) is BotCpuKind kind => Top(kind),
            "reset" when value == null => Reset(),
            _ => null,
        };
    }

    // 1-1440, in digits only; null for anything else.
    private static int? Minutes(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) &&
        minutes is >= 1 and <= MaxMinutes
            ? minutes
            : null;

    // A bot kind by its name (2.5), in any case; null for another kind or word.
    private static BotCpuKind? BotKind(string text) => text.ToLowerInvariant() switch
    {
        "gamebot" => BotCpuKind.Gamebot,
        "companion" => BotCpuKind.Companion,
        "playerbot" => BotCpuKind.Playerbot,
        "piloted" => BotCpuKind.Piloted,
        _ => null,
    };

    // /botcpu: off, the off text; on, the status, the last minute and the totals since on or reset.
    private static IReadOnlyList<string> Status()
    {
        BotCpuResult result = BotCpuScript.Result;
        return result.On
            ? BotCpuReport.StatusText(result.OnSince, result.ByCommand, result.StopsAt, BotCpuScript.LocalNow(),
                result.Last, result.LastProcess, result.Since)
            : [BotCpuReport.OffStatusText];
    }

    // /botcpu on [minutes]: the job queued. The reply speaks of the state published before it was queued, as the job
    // can run on the thread pool before this returns.
    private static IReadOnlyList<string> On(string gm, int? minutes)
    {
        bool on = BotCpuScript.Result.On;
        DateTime? stopsAt = BotCpuScript.CommandOn(gm, minutes);
        if (on)
            return [BotCpuReport.AlreadyOnText(stopsAt)];

        return [minutes is int stopIn && stopsAt is DateTime stop
            ? BotCpuReport.OnText(stopIn, stop)
            : BotCpuReport.OnText()];
    }

    // /botcpu off: metering stops at once and the job is queued, whatever the published state, so an on queued just
    // before still ends off (3.7). The reply gives the open window's seconds, or that the meter is already off.
    private static IReadOnlyList<string> Off(string gm) =>
        [BotCpuScript.CommandOff(gm) is double seconds ? BotCpuReport.OffText(seconds) : BotCpuReport.AlreadyOffText];

    // /botcpu top [kind]: off, the off text; before the first window, when its lines come; then the kind's top 5.
    private static IReadOnlyList<string> Top(BotCpuKind kind)
    {
        BotCpuResult result = BotCpuScript.Result;
        if (!result.On)
            return [BotCpuReport.OffStatusText];

        return result.Last == null
            ? [BotCpuReport.NoWindowYetText(result.OnSince)]
            : BotCpuReport.TopText(result.Last, kind);
    }

    // /botcpu reset: on, the job queued; off, the off text and nothing queued.
    private static IReadOnlyList<string> Reset()
    {
        if (!BotCpuScript.Result.On)
            return [BotCpuReport.OffStatusText];

        BotCpuScript.CommandReset();
        return [BotCpuReport.ResetText];
    }
}
```

Then `smoke.sh`'s two lines, with an echo of their own, after the GM-only group's echo. Both read the server log copy
taken at :47, after the server listens. The started handler runs before that (`GameServer.cs:510` notifies `Started`
before `base.Start()` logs "now listening"), so a meter left on by a default start would have its `BOT_CPU_STATE on`
line in the copy:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/smoke.sh <<'EDIT'
<<<<<<< OLD
echo "ok - single-player teleports are GM-only, companions stay open"
=======
echo "ok - single-player teleports are GM-only, companions stay open"
grep -q "Command - '&botcpu' .* required plvl:2" "$T/server.log" || fail "/botcpu is not GM-only"
if grep -q "BOT_CPU_STATE" "$T/server.log"; then fail "the bot CPU meter is on after a default start"; fi
echo "ok - /botcpu is GM-only and a default start leaves the bot CPU meter off"
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/smoke.sh (1 block(s))`.

- [ ] **Step 4: Run them to see them pass.**

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (644 was
seen); no warning names `BotCpuCommand.cs` or `UT_BotCpuScript.cs`.

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpuScript."`
Expected: `Passed!  - Failed:     0, Passed:   144, Skipped:     0, Total:   144` (exit code 0): Tasks 7-9's 109
and this task's 35, in about 3 s.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 24 tests` and `OK` (21 after Task 9, and `SmokeTests`' 3).

- [ ] **Step 5: Check the files' form.** The new file is LF, ASCII, with no BOM and no tab; the changed files keep
theirs. The new file's only line over 120 columns is the spec's top usage line (122):

```bash
python3 - source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs deploy/tests/test_bot_cpu.py deploy/tests/smoke.sh <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    longest = max(len(line) for line in raw.decode("utf-8").split("\n"))
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), longest)
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, longest line):

```
source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs False 0 0 True 122
source/server/Tests/UnitTests/UT_BotCpuScript.cs False 0 0 True 120
deploy/tests/test_bot_cpu.py False 0 0 True 120
deploy/tests/smoke.sh False 0 0 True 178
```

`smoke.sh`'s 178 is an older line (:101, the restart's `world-fixes.log` check); its three new lines
are 97, 105 and 79 columns.

Run: `LC_ALL=C grep -c -P '[^\x00-\x7F]' source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs`
Expected: `0` (the file is ASCII; grep's exit code is 1).

Run: `bash -n deploy/tests/smoke.sh && echo syntax-ok`
Expected: `syntax-ok`.

Run: `git diff --check`
Expected: no output (exit code 0).

- [ ] **Step 6: Run the wider checks.**

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build --filter "FullyQualifiedName~UT_BotCpu"`
Expected: `Passed!  - Failed:     0, Passed:   345, Skipped:     0, Total:   345` (the meter's three classes: 310 after
Task 9, and 35).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   989, Skipped:     0, Total:   989` (954 after Task 9, and this task's
35).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3777, Skipped:     0, Total:  3777` (3742 after Task 9, and 35; about half
a minute of tests). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy; rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

`smoke.sh` changed, so the deploy test that reads its world-fix lines runs too, then the whole deploy suite with the
world. `~/tmp/hearthdaoc-sync037/td/env.sh` sets `TD` and defines `t`, which runs one file with `HDC_TEST_WORLD`,
`HDC_CLASSIC_QUESTS` and `HDC_TOOLS`; it exports nothing, so the whole run sets the three itself:

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && t test_container_lines`
Expected: `Ran 1 test` and `OK`.

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 768 tests` and `OK (skipped=10)` (about three minutes). Without the three variables most world tests
skip (`OK (skipped=220)` was seen).

Run: `git status --short`
Expected: only these lines (`serverconfig.xml` is ignored, so it does not show):

```
 M deploy/tests/smoke.sh
 M deploy/tests/test_bot_cpu.py
 M source/server/Tests/UnitTests/UT_BotCpuScript.cs
?? source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs
```

Run: `git diff --numstat`
Expected:

```
3	0	deploy/tests/smoke.sh
52	1	deploy/tests/test_bot_cpu.py
496	1	source/server/Tests/UnitTests/UT_BotCpuScript.cs
```

- [ ] **Step 7: Commit.**

```bash
git add source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs source/server/Tests/UnitTests/UT_BotCpuScript.cs deploy/tests/smoke.sh deploy/tests/test_bot_cpu.py
git commit -F - <<'EOF'
feat(cpu-meter): the /botcpu command

BotCpuCommand.cs adds /botcpu (spec 2.4): &botcpu, for GMs, with the spec's attribute. It runs on its game-loop thread
and never builds a report: the status and top read only the result the report published, and on, off and reset call
BotCpuScript, which turns the meter off at once for off and queues a job for the report side, with no file, /proc
read or log write (3.7). Every reply is BotCpuReport's text. On reads the published state before its job is queued, as
the thread pool can run the job before the command replies; off is always queued, so an on queued just before still
ends off, and replies with the open window's seconds or that the meter is already off; reset queues a job only while
the meter is on. Words and bot kinds are read in any case, as HEARTHDAOC_BOT_CPU is (Review Focus 1). Another word, a
kind that is not a bot kind, minutes outside 1-1440 or a word too many get the syntax lines and change nothing. The
reply's lines are sent as they are, not through DisplayMessage, whose format throws on a brace in a bot's activity.

UT_BotCpuScript pins the attribute; that &botcpu is the only command name or alias in the GameServer assembly that
starts with &bot, so a GM's /bot runs it; off and on queueing one job before any file, /proc read or log write (6.1);
each reply of the 2.4 table, with a run the property started; the refused arguments with the meter off and on; on,
off, reset and top in another case; replies made from the state before their job runs; an off sent right after an on,
which is still queued, so the meter ends off; no reply waiting for the report lock; and the handler's chat, reply or
syntax lines, through a GM client of the test's own. smoke.sh checks that a default start loads /botcpu at plvl 2 and
writes no BOT_CPU_STATE line (6.3), in two plain grep lines, which test_bot_cpu.py runs on sample logs.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `4 files changed, 690 insertions(+), 2 deletions(-)` and `create mode 100644
source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs`.

Review: one fix round (7be46c6, after the feat commit eb21389) shaped this section, and the steps above show its final
code. The first review's Important finding (no test held that `/botcpu off` still queues its job when the published
result is off; a handler that skipped it passed all 137 cases) gave `BotCpuOffRightAfterOnIsStillQueuedAndEndsOff`.
Its Minors gave `BotCpuShowsARunThePropertyStartedAsByPropertyWithNoStopTime` (the status's `by property` and `no stop
time` through the handler), `Run`'s comment and the handler test's `&botcpu` in place of `&bot` (ScriptMgr puts the
registered name first, so `&bot` never reaches a handler), and `run_smoke_lines`' docstring (its `fail()` is like
`smoke.sh`'s, without the docker logs). The second review approved; its one Minor (the status's last-minute line is
checked by its start and end only, though equality with `StatusText` pins the wiring) changed no line.

### Task 11: The record and the guides

**Files:**
- Modify: `deploy/tests/test_bot_cpu.py:12-13` (the docstring gains a last paragraph), `:35-36` (`REPORT_CS` goes in
  after `METER_CS`; `COMMAND_CS` and `UNIT_TESTS` after `SCRIPT_CS`), `:433-438` (a test at the end of `EndingsTests`,
  then `GuideTests`, before the main guard)
- Modify: `docs/fork/FORK.md:62-64` (the meter's row goes in after the realm point waits row, :62, before the blank
  line and `## Client patches`; LF, no BOM)
- Modify: `deploy/HANDOFF.md:113-115` (the `**Bot CPU meter.**` paragraph goes in under `## 6. Day-to-day` (:78), after
  Bot goals' last line, :113, and before `**Duplicate townspeople.**`, :115; LF, no BOM)
- Create: `docs/fork/verification/bot-cpu-meter-live.md` (the owner's live checks; LF, no BOM, no tab)
- Test: `deploy/tests/test_bot_cpu.py`

No C# change, and no CI filter or `test_workflows.py` change: the task adds documents and the deploy tests that pin
them.

**Spec:** 8 (the FORK.md row, HANDOFF.md's Day-to-day paragraph, the live guide), 6.5 (the guide's ten checks), 6.2
"The guides" (every read of the meter's lines takes every server log file, line by line) and "Endings" (the four new
files, the three test files and `test_bot_cpu.py` are LF without BOM or tabs), 2.2 (the read command), 2.3 (the CSV),
2.4 (`/botcpu`).

**Interfaces:**
- Consumes:
  - In `deploy/tests/test_bot_cpu.py` (lines at this task's start): `ROOT` (:25), `DEPLOY` (:26), `FORK` (:34),
    `METER_CS` (:35) and `SCRIPT_CS` (:36); `source(path)` (:64), which reads with `utf-8-sig`, and
    `between(text, start, end)` (:70); `endings` (:23), whose `BOM = b"\xef\xbb\xbf"` is at
    `deploy/tests/endings.py:26`; `class EndingsTests` (:413) and the main guard (:438).
  - The seven files the endings test reads, under `source/server/GameServer/scripts/hearthdaoc/`: `BotCpuMeter.cs`
    (Task 1), `BotCpuReport.cs` (Task 5), `BotCpuScript.cs` (Task 7), `BotCpuCommand.cs` (Task 10); under
    `source/server/Tests/UnitTests/`: `UT_BotCpuMeter.cs` (Task 1), `UT_BotCpuReport.cs` (Task 5),
    `UT_BotCpuScript.cs` (Task 7).
  - What the documents quote, as the earlier tasks built it: `BotCpuReport.OffStatusText` (`BotCpuReport.cs:552`),
    `OnText(int minutes, DateTime stopsAt)` (:818) and `OffText(double openSeconds)` (:829), the replies of check 2
    and check 8 (Task 6); `/botcpu`'s words `on [minutes]` (1-1440), `off`, `top [gamebot|companion|playerbot|piloted]`
    and `reset` (`BotCpuCommand.cs:14-22`, Task 10); `server_properties.py`'s `Bot CPU meter: <on|off>`, printed only
    when it changes the row (`deploy/bin/server_properties.py:139-140`, Task 7); HANDOFF.md's settings line for
    `HEARTHDAOC_BOT_CPU` (:93-95, Task 7); the `/botcpu` load line that `gameutils/ScriptMgr.cs:241` writes at every
    start, `ScriptMgr: Command - '&botcpu' - (HearthDAoC: measure the game loop's CPU per kind of bot (BOT_CPU lines in
    the server log)) required plvl:2` (Task 10).
- Produces (this is the last task: nothing later depends on these names), in `deploy/tests/test_bot_cpu.py` (lines at
  this task's commit):
  - `REPORT_CS = os.path.join(FORK, "BotCpuReport.cs")` (:40), `COMMAND_CS = os.path.join(FORK, "BotCpuCommand.cs")`
    (:42) and `UNIT_TESTS = os.path.join(ROOT, "source", "server", "Tests", "UnitTests")` (:43).
  - `EndingsTests.test_the_fork_files_are_lf_without_bom_or_tabs(self)` (:444): one subtest per file, failing with
    `a CR`, `a BOM` or `a tab`.
  - `class GuideTests(unittest.TestCase)` (:457) with the class attributes `HANDOFF`, `GUIDE`, `FORK_MD` and
    `EVERY_LOG = "docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)'"`, and four tests:
    `test_every_read_of_the_meters_lines_takes_every_server_log_file`,
    `test_the_handoff_tells_how_to_use_the_meter_under_day_to_day`,
    `test_the_live_guide_has_the_ten_checks_of_the_spec_each_with_a_result` and
    `test_fork_md_records_the_meter_after_the_realm_point_waits`.
  - The FORK.md row (:63), the HANDOFF.md paragraph (:115-131) and the guide (195 lines), byte for byte as Step 3
    gives them.

Four readings decide the tests and the documents. A line reads the server log when it names the log files,
`/data/logs/server`: that covers `server.log` and its dated older files. `./hdc logs` and `docker logs` are not reads:
2.2 offers `./hdc logs` as a live view, and 6.5 calls `docker logs` the container's start output. Each of the two files
must hold at least one read, so a file with none does not pass. Second, spec 8's content is pinned as well as 6.2's
rule: the Day-to-day paragraph's `/botcpu` words, the exact read command of 2.2 and the CSV; the guide's checks 1-10
in order, each with a `Result:` line, with the exact reads of check 3 and check 10; and the FORK.md row right after
the realm point waits row. Third, the endings list is 6.2's: the four new files, the three test files and
`test_bot_cpu.py`; the guide is not on it (Step 5 checks it). Fourth, 6.5's check 1 says the server log has no
`BOT_CPU` line after a default start, but the line that loads `/botcpu` at every start holds `BOT_CPU` in the
command's description (`gameutils/ScriptMgr.cs:241` writes it at Debug, which `config/logconfig.xml:120-122` sends
to `server.log`). The design is unchanged: the guide's check 1 leaves that line out with
`grep -v "Command - '&botcpu'"` and says why.

- [ ] **Step 1: Write the failing tests.** Three edits to `deploy/tests/test_bot_cpu.py` in one run of the helper. Each
old text occurs once in the file as Task 10 left it. The first adds the docstring's last paragraph; the second adds
the paths of the two new files the earlier tests did not need, and of the unit test folder.

The third adds the endings test at the end of `EndingsTests` and the class `GuideTests` before the main guard. The
endings test opens each file as bytes, one subtest per file, so one run names every bad file; its messages are short
(`a CR`, `a BOM`, `a tab`) rather than the file's bytes. In `GuideTests`, `EVERY_LOG` is the read of 2.2. The per-line
test keeps the lines that hold `BOT_CPU` and `/data/logs/server`, wants at least one, wants `EVERY_LOG` on each, and
wants no `BOT_CPU` line that names `/data/logs/server.log`. The HANDOFF test takes the text from `**Bot CPU meter.**`
to the first blank line, inside `## 6. Day-to-day` (which ends at `## Rollback`), so the paragraph's code block must
follow its text with no blank line. The guide test splits the guide at each `### <n>. ` heading and wants 1 to 10 in
order, a line starting `Result:` in each, check 3's read with `tail -20` and check 10's autosave read. The FORK.md test
takes the table rows (lines starting `| `) and wants the row right after the realm point waits row to name the four
new files, the three test files, GameLoop.cs, NpcService.cs, `test_bot_cpu.py` and the spec link:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/tests/test_bot_cpu.py <<'EDIT'
<<<<<<< OLD
meter off (6.3); its two lines are run here on sample server logs.
"""
=======
meter off (6.3); its two lines are run here on sample server logs.

The record and the guides (8): the row in docs/fork/FORK.md, HANDOFF.md's Day-to-day paragraph and the owner's live
checks (docs/fork/verification/bot-cpu-meter-live.md), whose reads of the meter's lines take every server log file,
oldest first, line by line (6.2).
"""
>>>>>>> NEW
<<<<<<< OLD
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
SCRIPT_CS = os.path.join(FORK, "BotCpuScript.cs")
=======
METER_CS = os.path.join(FORK, "BotCpuMeter.cs")
REPORT_CS = os.path.join(FORK, "BotCpuReport.cs")
SCRIPT_CS = os.path.join(FORK, "BotCpuScript.cs")
COMMAND_CS = os.path.join(FORK, "BotCpuCommand.cs")
UNIT_TESTS = os.path.join(ROOT, "source", "server", "Tests", "UnitTests")
>>>>>>> NEW
<<<<<<< OLD
        self.assertEqual(len(hooks), 3)
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)


if __name__ == "__main__":
=======
        self.assertEqual(len(hooks), 3)
        for line in hooks:
            self.assertTrue(line.endswith(b"\r"), line)

    def test_the_fork_files_are_lf_without_bom_or_tabs(self):
        # The four new files, the three test files and this one (spec 5.1, 6.2), as test_rp_timer_wait.py's.
        for path in (METER_CS, REPORT_CS, SCRIPT_CS, COMMAND_CS, os.path.join(UNIT_TESTS, "UT_BotCpuMeter.cs"),
                     os.path.join(UNIT_TESTS, "UT_BotCpuReport.cs"), os.path.join(UNIT_TESTS, "UT_BotCpuScript.cs"),
                     os.path.abspath(__file__)):
            with self.subTest(path=path):
                with open(path, "rb") as f:
                    raw = f.read()
                self.assertFalse(b"\r" in raw, "a CR")
                self.assertFalse(raw.startswith(endings.BOM), "a BOM")
                self.assertFalse(b"\t" in raw, "a tab")


class GuideTests(unittest.TestCase):
    """The record and the guides (spec 8)."""

    HANDOFF = os.path.join(DEPLOY, "HANDOFF.md")
    GUIDE = os.path.join(ROOT, "docs", "fork", "verification", "bot-cpu-meter-live.md")
    FORK_MD = os.path.join(ROOT, "docs", "fork", "FORK.md")
    # Every server log file, oldest first: the server starts a new one at midnight and at 100 MB and keeps 7 days
    # (config/logconfig.xml), so a read of server.log alone loses the day's earlier lines (spec 2.2).
    EVERY_LOG = "docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)'"

    def test_every_read_of_the_meters_lines_takes_every_server_log_file(self):
        # Line by line, not file by file as test_rp_timer_wait.py's: HANDOFF.md's start checks read server.log alone,
        # which holds the last start's lines (6.2). A line that reads the server log names its files.
        for path in (self.HANDOFF, self.GUIDE):
            with self.subTest(path=path):
                lines = source(path).splitlines()
                reads = [line for line in lines if "BOT_CPU" in line and "/data/logs/server" in line]
                self.assertTrue(reads, "no read of the meter's lines")
                for line in reads:
                    self.assertIn(self.EVERY_LOG, line)
                self.assertEqual([line for line in lines if "BOT_CPU" in line and "/data/logs/server.log" in line], [])

    def test_the_handoff_tells_how_to_use_the_meter_under_day_to_day(self):
        # /botcpu, the read command of 2.2 and the CSV (spec 8).
        day_to_day = between(source(self.HANDOFF), "## 6. Day-to-day", "## Rollback")
        paragraph = between(day_to_day, "**Bot CPU meter.**", "\n\n")
        for told in ("`/botcpu`", "`/botcpu on [minutes]`", "`/botcpu off`", "`/botcpu top", "`/botcpu reset`",
                     self.EVERY_LOG + " | grep BOT_CPU | tail -40", "/data/logs/bot-cpu.csv"):
            self.assertIn(told, paragraph)

    def test_the_live_guide_has_the_ten_checks_of_the_spec_each_with_a_result(self):
        # The owner's checks on the live server (spec 6.5), in its order, each with a line for the owner's result.
        checks = re.split(r"^### (?=\d+\. )", source(self.GUIDE), flags=re.M)[1:]
        self.assertEqual([check.split(".", 1)[0] for check in checks], [str(n) for n in range(1, 11)])
        for check in checks:
            with self.subTest(check=check.split("\n", 1)[0]):
                self.assertRegex(check, r"(?m)^Result:")
        # Check 3's read of the meter's lines and check 10's of the autosave lines, as 6.5 gives them.
        self.assertIn(self.EVERY_LOG + " | grep BOT_CPU | tail -20", checks[2])
        self.assertIn(self.EVERY_LOG + ' | grep -E "(players|autonomousBots): [0-9]+ in" | tail -6', checks[9])

    def test_fork_md_records_the_meter_after_the_realm_point_waits(self):
        # The four new files, the five marked lines in GL and NS, and the tests (spec 8), for upstream syncs.
        rows = [line for line in source(self.FORK_MD).splitlines() if line.startswith("| ")]
        waits = next(n for n, line in enumerate(rows) if line.startswith("| Gamebots come back worth realm points"))
        meter = rows[waits + 1]
        files = [f"`GameServer/scripts/hearthdaoc/BotCpu{n}.cs`" for n in ("Meter", "Report", "Script", "Command")]
        files += [f"`Tests/UnitTests/UT_BotCpu{n}.cs`" for n in ("Meter", "Report", "Script")]
        files += ["`GameServer/Managers/GameLoop/GameLoop.cs`", "`GameServer/ECS-Services/NpcService.cs`"]
        for named in files + ["`deploy/tests/test_bot_cpu.py`", "(specs/2026-10-14-bot-cpu-meter-design.md)"]:
            self.assertIn(named, meter)


if __name__ == "__main__":
>>>>>>> NEW
EDIT
```

Expected: `edited deploy/tests/test_bot_cpu.py (3 block(s))`.

- [ ] **Step 2: Run them to see them fail.**

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: the progress line `......FEFEE...................`, then three errors and two failures, all in `GuideTests`
(each path starts with the worktree's absolute path, shown here as `<repo>`):

```
ERROR: test_every_read_of_the_meters_lines_takes_every_server_log_file (tests.test_bot_cpu.GuideTests.test_every_read_of_the_meters_lines_takes_every_server_log_file) (path='<repo>/docs/fork/verification/bot-cpu-meter-live.md')
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/docs/fork/verification/bot-cpu-meter-live.md'
ERROR: test_the_handoff_tells_how_to_use_the_meter_under_day_to_day (tests.test_bot_cpu.GuideTests.test_the_handoff_tells_how_to_use_the_meter_under_day_to_day)
ValueError: substring not found
ERROR: test_the_live_guide_has_the_ten_checks_of_the_spec_each_with_a_result (tests.test_bot_cpu.GuideTests.test_the_live_guide_has_the_ten_checks_of_the_spec_each_with_a_result)
FileNotFoundError: [Errno 2] No such file or directory: '<repo>/docs/fork/verification/bot-cpu-meter-live.md'
FAIL: test_every_read_of_the_meters_lines_takes_every_server_log_file (tests.test_bot_cpu.GuideTests.test_every_read_of_the_meters_lines_takes_every_server_log_file) (path='<repo>/deploy/HANDOFF.md')
AssertionError: [] is not true : no read of the meter's lines
FAIL: test_fork_md_records_the_meter_after_the_realm_point_waits (tests.test_bot_cpu.GuideTests.test_fork_md_records_the_meter_after_the_realm_point_waits)
AssertionError: '`GameServer/scripts/hearthdaoc/BotCpuMeter.cs`' not found in '| What is patched | Where | Why |'
```

then `Ran 29 tests` and `FAILED (failures=2, errors=3)`. The guide does not exist yet; HANDOFF.md has no
`**Bot CPU meter.**` paragraph (the `ValueError` comes from `between`) and no read of the meter's lines; and the row
after the realm point waits row is the header of the next table, under `## Client patches`.

The endings test passes already: Tasks 1-10 wrote the seven files LF with no BOM or tab. To see it catch a bad file,
put a tab at the end of `BotCpuCommand.cs`, a CR on line 1 of `UT_BotCpuReport.cs` and a BOM before
`BotCpuReport.cs`, run it alone, then restore the three files from git:

```bash
printf '\t' >> source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs
sed -i '1s/$/\r/' source/server/Tests/UnitTests/UT_BotCpuReport.cs
sed -i '1s/^/\xef\xbb\xbf/' source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs
python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py -k test_the_fork_files_are_lf_without_bom_or_tabs
git checkout -- source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs source/server/Tests/UnitTests/UT_BotCpuReport.cs source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs
git status --short
```

Expected: the progress line `FFF`, then three failures of `test_the_fork_files_are_lf_without_bom_or_tabs`, one per
file, each with its message:

```
FAIL: test_the_fork_files_are_lf_without_bom_or_tabs (tests.test_bot_cpu.EndingsTests.test_the_fork_files_are_lf_without_bom_or_tabs) (path='<repo>/source/server/GameServer/scripts/hearthdaoc/BotCpuReport.cs')
AssertionError: True is not false : a BOM
FAIL: test_the_fork_files_are_lf_without_bom_or_tabs (tests.test_bot_cpu.EndingsTests.test_the_fork_files_are_lf_without_bom_or_tabs) (path='<repo>/source/server/GameServer/scripts/hearthdaoc/BotCpuCommand.cs')
AssertionError: True is not false : a tab
FAIL: test_the_fork_files_are_lf_without_bom_or_tabs (tests.test_bot_cpu.EndingsTests.test_the_fork_files_are_lf_without_bom_or_tabs) (path='<repo>/source/server/Tests/UnitTests/UT_BotCpuReport.cs')
AssertionError: True is not false : a CR
```

then `Ran 1 test` and `FAILED (failures=3)`. After the checkout, `git status --short` prints only
` M deploy/tests/test_bot_cpu.py`: the three files are as the earlier tasks committed them.

- [ ] **Step 3: Implement.** First the FORK.md row. The server-code table has four cells per row (Change | Files |
Why | Upstream); the new row goes right after the realm point waits row, before the blank line and `## Client
patches`. Beyond spec 8's list (the four new files, the five marked lines in GL and NS, the tests), it names the
`HEARTHDAOC_BOT_CPU` chain, smoke.sh's two lines and the guide, as the realm point waits row names its own. Its Upstream
cell says fork-only (spec 10: contributions are paused) and what a sync must keep, the 17 tick steps and each item
type's owner member, which `UpstreamFactTests` (Task 3) checks:

```bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py docs/fork/FORK.md <<'EDIT'
<<<<<<< OLD
a candidate later, only with the owner's go-ahead |

## Client patches
=======
a candidate later, only with the owner's go-ahead |
| Bot CPU meter: the game loop's CPU per kind of bot and player; `/botcpu` (GM only); `bot_cpu_meter` server property, default off | `GameServer/scripts/hearthdaoc/BotCpuMeter.cs` (new: the hot path: `Enabled`, the per-callback wrapper and the fleet scope, the per-thread slots, the kind and stage of each item, the per-bot counters, `IPlayerBotClient`), `GameServer/scripts/hearthdaoc/BotCpuReport.cs` (new: the report's numbers and the text of every log line, CSV row and chat reply, from plain values), `GameServer/scripts/hearthdaoc/BotCpuScript.cs` (new: the property, the start and stop handlers, the report side off the game loop with its lock, job queue and 1-second timer, the `/proc` and cgroup readers, the log lines and `logs/bot-cpu.csv`), `GameServer/scripts/hearthdaoc/BotCpuCommand.cs` (new: `/botcpu`); five lines, each marked: `GameServer/Managers/GameLoop/GameLoop.cs` (`ExecuteForEach`: a comment and the guard that hands the pool call to the meter while it is on, hook H1) and `GameServer/ECS-Services/NpcService.cs` (`Tick`: a comment, `BeginFleet` and `EndFleet` around the GameBot fleet planning, hook H6); `HEARTHDAOC_BOT_CPU` in `deploy/bin/server_properties.py`, `deploy/entrypoint.sh`, `deploy/compose.yml`, `deploy/.env.example`; tests `Tests/UnitTests/UT_BotCpuMeter.cs`, `Tests/UnitTests/UT_BotCpuReport.cs`, `Tests/UnitTests/UT_BotCpuScript.cs`, source and deploy checks `deploy/tests/test_bot_cpu.py`, two lines in `deploy/tests/smoke.sh` (`/botcpu` for GMs only, no `BOT_CPU_STATE` after a default start); the owner's live checks `docs/fork/verification/bot-cpu-meter-live.md` | The owner asked (2026-10-14, #137): "is there a way in the server we can note the average cpu per bot and separately the average cpu for this new playerpilot bot so that we can easily compare and contrast?", as a PR of its own, off by default and usable on the live server ([spec](specs/2026-10-14-bot-cpu-meter-design.md)). While it is on, each game-loop callback is timed and charged to its stage and to the kind of its owner, read before the callback runs: `gamebot`, `companion`, `playerbot`, `piloted`, `player`, `monster` or `unattributed`; a pet counts for its owner's kind (stage `Pets`), the GameBot fleet planning for `gamebot` (stage `Fleet`). Every minute the server log gets a `BOT_CPU` line per kind (`msPerBotSec`, the average CPU per bot), `BOT_CPU_TOP`, `BOT_CPU_PROCESS` and `BOT_CPU_COMPARE`, and `logs/bot-cpu.csv` a row per kind (50 MB plus one old file). Only the timing runs on the loop: the report runs on the thread pool, one step at a time, and a fault in it turns the meter off (`BOT_CPU_ERROR`, `BOT_CPU_STATE off source=error`) and never reaches the server. Off, H1 is one bool read per `ExecuteForEach` call, and H6 a bool read and a test of a local. A player bot counts as `playerbot` when its client implements `IPlayerBotClient` or derives from `BotDummyClient`, and in `n` when it is registered in `ClientService`. The numbers are wall time inside callbacks: work off the loop (timers, the camp planner thread, saves, GC) shows only in the process line. For a GM, `/bot` resolves to `/botcpu` (`/bot` itself is not registered) | Fork-only: five marked lines (GameLoop 2, NpcService 3). The meter also mirrors upstream's 17 tick steps (its stages) and the member each item type's owner is read from; `deploy/tests/test_bot_cpu.py` fails when a sync moves the hooks or changes either. Nothing goes upstream (contributions are paused) |

## Client patches
>>>>>>> NEW
EDIT
```

Expected: `edited docs/fork/FORK.md (1 block(s))`.

Then HANDOFF.md's paragraph, after Bot goals so the two owner tools are together and the world-fix paragraphs that
the section's intro calls "below" stay together. It gives `/botcpu` and each of its words, that a GM's `/bot` runs
`/botcpu` (no other command starts with `&bot`: 2.4; ruling 29 documents it rather than blocking it), the lines it
writes, the read command of 2.2, and the CSV of 2.3 (`cat`, `docker cp`, the `.1` file above 50 MB). Its code block
follows its text with no blank line, as in the "Quest rewards handed once" paragraph (:214), so the paragraph runs to
the next blank line:

````bash
python3 ~/tmp/hearthdaoc-cpu-plan/hdc-edit.py deploy/HANDOFF.md <<'EDIT'
<<<<<<< OLD
way, then `./hdc up`.

**Duplicate townspeople.** Upstream's world has two copies of Ley Manton and of Tria Ellowis at the same
=======
way, then `./hdc up`.

**Bot CPU meter.** It measures the game loop's CPU per kind of bot and player, to compare what each kind costs. It is
off unless `HEARTHDAOC_BOT_CPU=on` or a GM turns it on in game, and while on it costs about 1-5% of one core. As a GM:
`/botcpu` shows its state, the last minute and the totals since on; `/botcpu on [minutes]` turns it on, for 1-1440
minutes or until `/botcpu off` or the next start; `/botcpu off` stops it and writes the last partial minute;
`/botcpu top [gamebot|companion|playerbot|piloted]` lists the 5 costliest bots of the last minute; `/botcpu reset`
starts the totals since on again. For a GM, `/bot` is short for `/botcpu`, so `/bot on` turns the meter on. Every
minute while it is on, the server log gets one `BOT_CPU` line per kind (`msPerBotSec` is the average CPU per bot, in
ms per second), the 5 costliest bots of each bot kind (`BOT_CPU_TOP`) and a whole-process line (`BOT_CPU_PROCESS`);
`BOT_CPU_STATE` lines say when it turned on and off. The server starts a new log file every day and keeps a week of
them, so read them all, oldest first (`./hdc logs` shows only the last 200 lines, then the new ones as they come):
```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -40
```
The same minutes go to `/data/logs/bot-cpu.csv`, one row per minute and kind, for charts (its stage columns are
whole-kind totals; above 50 MB it becomes `bot-cpu.csv.1`): `docker exec hearthdaoc-server cat /data/logs/bot-cpu.csv`,
or `docker cp hearthdaoc-server:/data/logs/bot-cpu.csv .` to copy it out. The owner's checks on the live server are in
`docs/fork/verification/bot-cpu-meter-live.md`.

**Duplicate townspeople.** Upstream's world has two copies of Ley Manton and of Tria Ellowis at the same
>>>>>>> NEW
EDIT
````

Expected: `edited deploy/HANDOFF.md (1 block(s))`.

Last, the guide, in the layout of `docs/fork/verification/rp-timer-waits-ingame.md`: an intro with Date and Release
lines and the risk note of spec 9 (turn the meter off if `tps` drops below 30.0 or `overBudget` rises), "Reading the
server log" with the read command and a table of the six line types, then `### 1.` to `### 10.` following 6.5, each
with its commands and a `Result:` line. Every read of the server log uses the every-file command. Check 1 leaves out
the start's `/botcpu` load line (see above). Check 4 adds the `SERVER_MEMORY` read for `pauseTotalMs`. Check 7's
second `awk` adds up each minute's `SERVER_WORK` lines (`Managers/GameLoop/GameLoopWorkMetrics.cs:67` writes one per
stage, 17 a minute, with `samples=`, `avgMs=` and `overBudget=`); it gives the same sums with mawk, gawk and awk.
Create it with exactly this (the quoted heredoc keeps every `$`, backtick and backslash as written; the file is 195
lines, UTF-8 with two non-ASCII signs, `×` and `÷`, on line 150, and ends with one newline):

````bash
cat > docs/fork/verification/bot-cpu-meter-live.md <<'GUIDE'
# Bot CPU meter: checks on the live server

For the owner, after the release with the bot CPU meter is installed (`./hdc update`). The design is in the
[spec](../specs/2026-10-14-bot-cpu-meter-design.md); these are its checks (section 6.5). Write what you saw on each
check's "Result:" line: pass or fail, and anything odd. Log lines pasted in are welcome.

- Date:
- Release (`HEARTHDAOC_TAG` in `~/hearthdaoc/.env`):

What it does: while the meter is on, the server times each game-loop callback and charges it to the kind of its owner:
`gamebot` (the autonomous GameBots), `companion` (the other GameBots: companions and the `/spawn`, `/grind` and `/raid`
helpers), `playerbot` and `piloted` (none on the live server today), `player` (you and any real players), `monster`
(every other NPC, guards and merchants included, and keep doors) and `unattributed` (work with no owner). A pet counts
for its owner's kind. Every minute it writes the lines below to the server log, and one row per kind to
`/data/logs/bot-cpu.csv`. It is off by default: nothing changes until a GM turns it on. While on it costs an estimated
1-5% of one core; if `tps` falls below 30.0, or the `SERVER_WORK` lines' `overBudget` rises, turn it off with
`/botcpu off`.

## Reading the server log

`./hdc logs` keeps only the last 200 lines, so read the log files themselves, on the server, from `~/hearthdaoc`. The
server starts a new log file at midnight (and at 100 MB) and keeps a week of them, so every command here reads them
all, oldest first:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -40
```

Each line starts with its date and time (`MM/dd HH:mm:ss`), in the server's local time, as `/botcpu` shows times.

| Line | Written | What to read |
|---|---|---|
| `BOT_CPU` | every minute, one per kind with anything charged, in the kind order above | `n` (the kind's average count), `msPerBotSec` (the average CPU per bot, in ms per second: 1.0 is 0.1% of one core), `msSec` (the whole kind), and after the `\|` the stages, per bot; `unattributed` has `n=-`, and its stages are ms per second for the whole kind |
| `BOT_CPU_TOP` | every minute, one per bot kind with a bot charged | the 5 costliest bots, `rank=name:ms per s`, with each GameBot's goal |
| `BOT_CPU_PROCESS` | every minute | the whole process: `tps`, `processCpuPct`, `loopThreadsCpuPct`, `chargedPct`, `callsSec`, `loopWaitMs`, `throttledMs`, `gcPauseMs` and more; `-` for a value the platform can't give |
| `BOT_CPU_COMPARE` | every minute, for `player/gamebot` while both have `n` of 1.0 or more (also `playerbot/gamebot` and `piloted/gamebot`, which have no bots today) | `cpu`, the ratio of the two kinds' `msPerBotSec`; `alloc`, `calls` and `usPerCall` |
| `BOT_CPU_STATE` | each time it turns on or off, or its stop time changes | `on source=property` (at start), `on source=command by=<GM> minutes=<m or ->`, `off source=command by=<GM>`, `off source=timer`, `off source=stop` (server stop), `off source=error` |
| `BOT_CPU_ERROR` | a fault in the meter's report, just before `off source=error` (a Warn line) | what failed: post it on #137. The server goes on, and `/botcpu on` starts the meter again |

Every field is explained in the spec's section 2.1, and section 7 says how to read the numbers fairly. To read a few
minutes only, add a time filter right after the `grep`, with the month/day and hour:minute, for example from 14:00 to
14:10 on 21 October: `| awk '$0 >= "10/21 14:00" && $0 < "10/21 14:10"'`.

## The checks

### 1. The update

`./hdc update` lists `HEARTHDAOC_BOT_CPU` among the new settings added to `.env`, and `.env` has
`HEARTHDAOC_BOT_CPU=off`. The container's start output has "Bot CPU meter: off" once (the server property's row is
new). The server log has no line of the meter, and `SERVER_WORK` looks as before:

```bash
grep HEARTHDAOC_BOT_CPU ~/hearthdaoc/.env
docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | grep -v "Command - '&botcpu'"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_WORK | tail -17
```

The third prints nothing: it leaves out the start's line that loads `/botcpu` for GMs,
`Command - '&botcpu' - ... required plvl:2`, whose description names the BOT_CPU lines. The fourth shows the last
minute's `SERVER_WORK` lines, one per stage.

Result:

### 2. Turn it on in game

In game, as GM: `/botcpu` says "Bot CPU meter: off. /botcpu on [minutes] turns it on." Then `/botcpu on 30`, which
says "Bot CPU meter on for 30 minutes (until 14:32): BOT_CPU lines every minute in the server log, rows in
logs/bot-cpu.csv." (with your own time). Note the time you turned it on, for check 7.

To keep your character from waking monsters and speeding up GameBots near you (the spec's section 7), go somewhere
quiet, or go to the character screen or quit the game: both take you out of `player`. Quit from the game's menu: a
client that is killed leaves your character link-dead in the world for 60 s, still counted. The meter runs on.

Result:

### 3. The lines

After 2-3 minutes:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -20
```

Once: a `BOT_CPU_STATE on source=command` line. Then each minute: `gamebot` with `n` close to your bot count (about
240), `player` with `n` 1.0 while you are in game, `monster`, `unattributed`, a `BOT_CPU_TOP` for `gamebot`,
`BOT_CPU_PROCESS` with `tps=30.0`, and `BOT_CPU_COMPARE pair=player/gamebot` while you are in game. `companion` has a
line while players use companions; `playerbot` and `piloted` have none.

Result:

### 4. Sanity

On the `BOT_CPU_PROCESS` lines, usually `chargedPct` < `loopThreadsCpuPct` < `processCpuPct`. If `chargedPct` is
higher, `loopWaitMs`, `throttledMs` or `gcPauseMs` is large too: the wall times are inflated (the spec's section 3.9).
`gamebot` `msSec` / 10 is its share of one core in %. `loopThreadsCpuPct` as `-` means the kernel gives no scheduler
statistics (3.9): say so on #137.

Confirm a large `gcPauseMs` with the `SERVER_GC_PAUSE` lines of the same minutes. Those lines list only pauses of
250 ms or more; shorter pauses add up in the `pauseTotalMs` of the `SERVER_MEMORY` line that covers them (every 5
minutes):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_GC_PAUSE | tail -20
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_MEMORY | tail -3
```

Result:

### 5. In chat

`/botcpu` shows the same last minute as the log's last `BOT_CPU` lines: a status line (`on since ...`, `by command`,
`stops at ...`), the last minute's process line, one line per kind (`gamebot n=...: ... ms/s per bot`) and the totals
since on. `/botcpu top` lists 5 GameBots with their goals and what they are doing, such as
`1. Aldric (34 Cleric, region 163): 7.412 ms/s, 880 calls/s, Rvr, "..."`.

Result:

### 6. The CSV

```bash
docker exec hearthdaoc-server tail -5 /data/logs/bot-cpu.csv
```

A header the first time, then rows: one per minute and kind. To chart it, copy it out with
`docker cp hearthdaoc-server:/data/logs/bot-cpu.csv .`; its stage columns are whole-kind totals, not per bot.

Result:

### 7. Overhead

Compare the `SERVER_WORK` lines of the 10 minutes before you turned the meter on with 10 minutes on. This adds up each
minute's lines (one per stage); put your own times of check 2 in its first `awk` (here the meter went on at 14:00 on
21 October):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_WORK |
  awk '$0 >= "10/21 13:50" && $0 < "10/21 14:10"' |
  awk '{ m = $1 " " substr($2, 1, 5)
         for (i = 1; i <= NF; i++) {
           split($i, f, "=")
           if (f[1] == "samples" && f[2] > ticks[m]) ticks[m] = f[2]
           if (f[1] == "avgMs") ms[m] += f[2]
           if (f[1] == "overBudget") over[m] += f[2]
         } }
       END { for (m in ms) printf "%s samples=%d avgMsSum=%.3f overBudget=%d\n", m, ticks[m], ms[m], over[m] }' | sort
```

Pass if `samples` stays at about 1,800 a minute (30 ticks a second), `overBudget` does not rise, and the stages'
`avgMs`, added up (`avgMsSum`), rise by no more than `callsSec` × 100 ns ÷ 30 per tick (est.; `callsSec` is on the
`BOT_CPU_PROCESS` lines: at `callsSec=212534` that is about 0.7 ms). Bot activity moves from minute to minute, so this
is a rough check.

Result:

### 8. Off

`/botcpu off` says "Bot CPU meter off. The last 37 s are being written." (with its own seconds). Then one partial
window, then `BOT_CPU_STATE off source=command`, then no more lines; a minute later the same command still ends with
the `off` line:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -10
```

With `on 30` left running instead, `off source=timer` comes by itself after 30 minutes.

Result:

### 9. On at start

In `~/hearthdaoc/.env`, set `HEARTHDAOC_BOT_CPU=on`, then `./hdc up`. "Bot CPU meter: on" is in the container's start
output, the server log has `BOT_CPU_STATE on source=property`, and the lines come every minute with no command:

```bash
docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -20
```

`/botcpu off` stops it until the next start. Set it back to `HEARTHDAOC_BOT_CPU=off` and `./hdc up`.

Result:

### 10. For #137

Post a few windows (the `gamebot`, `player` and `BOT_CPU_PROCESS` lines) on #137, with the autosave lines of the same
period (the spec's section 7):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep -E "(players|autonomousBots): [0-9]+ in" | tail -6
```

They are the GameBot baseline the player-bot prototype will be compared with.

Result:
GUIDE
````

The guide's commands run on the owner's server, from `~/hearthdaoc`; nothing in this plan runs them.

- [ ] **Step 4: Run them to see them pass.**

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py`
Expected: `Ran 29 tests` and `OK` (24 after Task 10, and this task's 5).

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_bot_cpu.py -k GuideTests -k test_the_fork_files -v`
Expected: the five new tests, each `... ok`, then `Ran 5 tests` and `OK`:

```
test_the_fork_files_are_lf_without_bom_or_tabs (tests.test_bot_cpu.EndingsTests.test_the_fork_files_are_lf_without_bom_or_tabs) ... ok
test_every_read_of_the_meters_lines_takes_every_server_log_file (tests.test_bot_cpu.GuideTests.test_every_read_of_the_meters_lines_takes_every_server_log_file) ... ok
test_fork_md_records_the_meter_after_the_realm_point_waits (tests.test_bot_cpu.GuideTests.test_fork_md_records_the_meter_after_the_realm_point_waits) ... ok
test_the_handoff_tells_how_to_use_the_meter_under_day_to_day (tests.test_bot_cpu.GuideTests.test_the_handoff_tells_how_to_use_the_meter_under_day_to_day) ... ok
test_the_live_guide_has_the_ten_checks_of_the_spec_each_with_a_result (tests.test_bot_cpu.GuideTests.test_the_live_guide_has_the_ten_checks_of_the_spec_each_with_a_result) ... ok
```

- [ ] **Step 5: Check the files' form.** The guide is LF with no BOM and no tab; the three changed files keep theirs
(LF, no BOM, no tab):

```bash
python3 - docs/fork/verification/bot-cpu-meter-live.md docs/fork/FORK.md deploy/HANDOFF.md deploy/tests/test_bot_cpu.py <<'EOF'
import sys
for path in sys.argv[1:]:
    raw = open(path, "rb").read()
    print(path, raw.startswith(b"\xef\xbb\xbf"), raw.count(b"\r"), raw.count(b"\t"), raw.endswith(b"\n"), raw.count(b"\n"))
EOF
```

Expected (BOM, carriage returns, tabs, a last line end, line count):

```
docs/fork/verification/bot-cpu-meter-live.md False 0 0 True 195
docs/fork/FORK.md False 0 0 True 347
deploy/HANDOFF.md False 0 0 True 355
deploy/tests/test_bot_cpu.py False 0 0 True 511
```

Run: `git diff --check`
Expected: no output (exit code 0).

- [ ] **Step 6: Run the wider checks.** No C# changed, so the C# counts are Task 10's. The unit tests need
`source/server/CoreServer/config/serverconfig.xml` (ignored; never commit it). If it is missing, run:
`cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml`

Run: `dotnet build source/server/Tests/Tests.csproj --nologo -v q -clp:ErrorsOnly`
Expected: `Build succeeded.` and `0 Error(s)`. The warning total varies between builds of the same tree (644 was
seen).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --filter "$(grep -o 'FullyQualifiedName~UT_CommandPrivLevelOverrides[^"]*' .github/workflows/server-image.yml)" 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:   989, Skipped:     0, Total:   989`
(Task 10's count; 644 at 439719f).

Run: `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 dotnet test source/server/Tests/Tests.csproj --nologo --no-build 2>&1 | grep -E '^(Passed|Failed)!'`
Expected: `Passed!  - Failed:     0, Passed:  3777, Skipped:     0, Total:  3777`
(3432 at 439719f). Upstream's `PooledWalReadersAndWriters_CanOpenConnectionsConcurrently` can fail with "database is
locked" when the machine is busy: a run where it fails gives `Failed!  - Failed:     1, Passed:  3776, Skipped:     0,
Total:  3777` (the first run while this plan was written did). Rerun it alone with
`--filter "FullyQualifiedName~PooledWalReadersAndWriters_CanOpenConnectionsConcurrently"` and expect `Passed:     1`,
then the whole project again, which gave the line above.

Run: `python3 -B -m unittest discover -s deploy/tests -t deploy -p test_workflows.py`
Expected: `Ran 18 tests` and `OK`.

The whole deploy suite with the world (`~/tmp/hearthdaoc-sync037/td/env.sh` sets `TD` and exports nothing, so the run
sets the three variables itself):

Run: `. ~/tmp/hearthdaoc-sync037/td/env.sh && HDC_TEST_WORLD="$TD/worlds/v0.37b.db" HDC_CLASSIC_QUESTS="$TD/q037/classic-quests.json" HDC_TOOLS="$TD/tools" python3 -m unittest discover -s deploy/tests -t deploy`
Expected: `Ran 773 tests` and `OK (skipped=10)` (768 after Task 10, and this task's 5; about three minutes; 744 at
439719f).

Run: `git status --short`
Expected: only these lines (`serverconfig.xml` is ignored, so it does not show):

```
 M deploy/HANDOFF.md
 M deploy/tests/test_bot_cpu.py
 M docs/fork/FORK.md
?? docs/fork/verification/bot-cpu-meter-live.md
```

Run: `git diff --numstat`
Expected (the new guide is not tracked until Step 7's `git add`):

```
18	0	deploy/HANDOFF.md
72	0	deploy/tests/test_bot_cpu.py
1	0	docs/fork/FORK.md
```

- [ ] **Step 7: Commit.**

```bash
git add docs/fork/FORK.md deploy/HANDOFF.md docs/fork/verification/bot-cpu-meter-live.md deploy/tests/test_bot_cpu.py
git commit -F - <<'EOF'
docs(cpu-meter): the fork record, the handoff's day-to-day paragraph and the owner's live checks

FORK.md gains the meter's row after the realm point waits (spec 8): the four new files, the five marked lines in
GameLoop.cs and NpcService.cs, the switch's deploy chain and the tests, with what a sync must keep (the 17 tick steps
and each item type's owner member, which test_bot_cpu.py checks). HANDOFF.md's Day-to-day has a Bot CPU meter
paragraph: /botcpu and its words, that a GM's /bot runs it, the read command of 2.2 and the CSV.
docs/fork/verification/bot-cpu-meter-live.md holds the ten checks of 6.5 with a Result line each for the owner.

test_bot_cpu.py checks, line by line in HANDOFF.md and the guide, that every line naming BOT_CPU and the server log's
files reads them all with docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)', and that no such
line reads /data/logs/server.log, which HANDOFF.md's start checks still read alone (6.2). It also pins the Day-to-day
paragraph, the guide's ten checks in order with check 3's and check 10's reads as 6.5 gives them, the FORK.md row,
and that the four new files, the three test files and test_bot_cpu.py are LF without a BOM or tabs (6.2).

Check 1 of the guide leaves out the start's line that loads /botcpu: its description names the BOT_CPU lines, so a
plain grep BOT_CPU after a default start finds it.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Expected: `4 files changed, 286 insertions(+)` and `create mode 100644 docs/fork/verification/bot-cpu-meter-live.md`.

Review: one review approved the task as committed (3b48180), so no fix round shaped it; its three Minor findings changed
no line and stay open for the owner: the per-line test counts a read only when it names `/data/logs/server`, so a
`BOT_CPU` read of `/data/logs/*` or of `docker logs` would pass it; the guide's table says a `BOT_CPU` line is written
for a kind "with anything charged", where 2.1 and `HasLine` (`BotCpuReport.cs:190`) also write one for a kind with
bots present; and the FORK.md row puts a player bot in `n` when it is registered in `ClientService`, where 2.5 and 3.8
also want it playing.
