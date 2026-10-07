# Classic Battlegrounds Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the four classic battlegrounds work as they did in the Shrouded Isles era. Abermenai is
15–19 up to 1L2, Thidranki 20–24 up to 1L3, Murdaigean 25–29 up to 1L5 and Caledonia 30–35 up to 1L9. Each
has three realm portal keeps and a capturable central keep, the porter explains its refusals, characters
over a limit wake at their bind point, and Atlas's extras are gone.

**Architecture:**
- **Decision class (`ClassicBattlegrounds`):** a plain class with every rule (brackets, porter decision and
  texts, once-per-ceremony, over the limit, where to go, keep level). Unit tests drive it without a server.
- **Wiring script (`ClassicBattlegroundsScript`):**
  - the porter hook `PorterDestination`, called from three lines in upstream's `OFTeleporters.cs`;
  - handlers for `GamePlayerEvent.Quit`, `GamePlayerEvent.GameEntered` and `KeepEvent.KeepTaken`.
- **Upstream code changes, small and listed:**
  - the three porter call lines;
  - one word in `KeepManager.cs` ("Svasud Faste");
  - the twelve Atlas battleground quest files, deleted.
- **World data (`deploy/bin/battlegrounds.py`):** called by `world_fixes.py` before every start. It runs once
  per world under one savepoint and records the marker `classic-battlegrounds-v1`.

**Tech Stack:**
- C# (.NET 10, the OpenDAoC/DOL server) with NUnit 4;
- Python 3.10+ standard library (`sqlite3`) for the deploy scripts and tests (the image has 3.12);
- bash;
- GitHub Actions.

**Spec:** `docs/fork/specs/2026-10-07-classic-battlegrounds-design.md` (approved; branch `sub5-battlegrounds`).

## Global Constraints

- **Where the code lives:**
  - New fork files:
    - `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs`
    - `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs`
    - `source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs`
    - `deploy/bin/battlegrounds.py`
    - `deploy/tests/test_battlegrounds.py`
  - Upstream files touched, and nothing else upstream:
    1. `scripts/teleporters/OFTeleporters.cs`: each of the three `case BattlegroundsID:` blocks (Albion
       `:231-273`, Midgard `:390-431`, Hibernia `:547-588`) becomes exactly
       `PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);` plus its
       `break;`. Nothing else changes, the usings included (lines 1-8 have no `using DOL.GS.HearthDAoC;`).
       `HearthDAoC` resolves through the enclosing `DOL.GS` namespace.
    2. `keeps/KeepManager.cs:786`: `"Svasudheim Faste"` becomes `"Svasud Faste"` (one word).
    3. Deleted: the twelve files in `scripts/quests/BattlegroundQuests/`:
       - `Caledonia/`: `CaleKeepCaptureAlb.cs`, `CaleKeepCaptureHib.cs`, `CaleKeepCaptureMid.cs`,
         `CaleKillQuestAlb.cs`, `CaleKillQuestHib.cs`, `CaleKillQuestMid.cs`;
       - `Thidranki/`: `ThidKeepCaptureAlb.cs`, `ThidKeepCaptureHib.cs`, `ThidKeepCaptureMid.cs`,
         `ThidKillQuestAlb.cs`, `ThidKillQuestHib.cs`, `ThidKillQuestMid.cs`.

       The two `<Folder Include=...>` lines in `GameServer.csproj` (`:79-80`) stay.
- **Line endings:**
  - `OFTeleporters.cs` is UTF-8 with a BOM and mixed line endings (794 CRLF, 25 LF). The three blocks are
    CRLF, so the new lines are CRLF too, and the BOM stays.
  - `KeepManager.cs` is ASCII with CRLF; a `sed` word swap keeps that.
  - The new C# files are ASCII with LF, use a file-scoped namespace `DOL.GS.HearthDAoC` (tests:
    `DOL.GS.Tests`), and follow `SiStartChoice.cs` / `UT_SiStartChoice.cs`.
- **The four battlegrounds (§2.1):**

  | | Abermenai | Thidranki | Murdaigean | Caledonia |
  |---|---|---|---|---|
  | Region | 253 | 252 | 251 | 250 |
  | Levels | 15–19 | 20–24 | 25–29 | 30–35 |
  | MaxRealmLevel (must be below) | 3 | 4 | 6 | 10 |
  | Highest rank, cap | 1L2, under 125 RP | 1L3, under 350 | 1L5, under 1,375 | 1L9, under 7,125 |
  | Portal keeps (KeepID) | 35, 36, 37 | 12, 13, 14 | 41, 42, 43 | 38, 39, 40 |
  | Central keep | Dun Abermenai, #32 (new) | Thidranki Faste, #11 | Dun Murdaigean, #33 (new) | Caer Caledon, #31 |
  | Keep BaseLevel | 19 | 24 (was 26) | 29 | 35 (was 46) |
  | Guard / lord level | 21 / 24 | 26 / 31 | 31 / 36 | 37 / 44 |
  | Zone `Experience` | 0 | 0 (was 50) | 0 | 0 (was 50) |

  - Realm level is (rank − 1) × 10 + level.
  - `REALMPOINTS_FOR_LEVEL` (`gameobjects/GamePlayer.cs:3851`): 3 = 125, 4 = 350, 6 = 1375, 10 = 7125.
  - MaxRealmLevel 0 means no cap.
- **Battleground rows (§3.2 step 1):**
  - They are matched by `RegionID`, and a row changes only while it has exactly upstream's values, label
    included:
    - 253: "Abermenai (Level 15-19)"; 15–19; 2 → "Abermenai (Level 15-19 - RR1L2)"; 15–19; 3
    - 252: "Thidranki (Level 20-24 - RR2L0)"; 20–24; 10 → "Thidranki (Level 20-24 - RR1L3)"; 20–24; 4
    - 251: "Murdaigean (Level 25-29)"; 25–29; 5 → "Murdaigean (Level 25-29 - RR1L5)"; 25–29; 6
    - 250: "Caledonia (Level 34-39 - RR3L5)"; 30–34; 25 → "Caledonia (Level 30-35 - RR1L9)"; 30–35; 10
  - Region 165 (Cathal Valley, 45–49, 45) is never touched.
- **Landing spots** (today's, the same in every battleground, `OFTeleporters.cs:233-270, 392-428, 549-585`):
  - Albion 38113, 53507, 4160, heading 3268;
  - Midgard 53568, 23643, 4530, heading 0;
  - Hibernia 17367, 18248, 4320, heading 0.
- **Porter texts (§2.3)**, said with `porter.SayTo(player, eChatLoc.CL_ChatWindow, text)`. The chat shows
  `<porter> says, "<text>"`. The medallion stays on.
  - Below the lowest MinLevel: "The battlegrounds are for levels 15 to 35. Come back when you reach level 15."
  - Above the highest MaxLevel: "You have outgrown the battlegrounds, which are for levels 15 to 35."
  - Too many realm points: "Thidranki is for realm rank 1L3 and below, under 350 realm points. You have 412,
    so I cannot send you there."
  - A level with no row: "No battleground on this server takes level 22."
  - The levels, ranks and caps come from the rows. "15" is the lowest MinLevel and "35" the highest
    MaxLevel. The rank is `RankLabel(MaxRealmLevel − 1)`, where `RankLabel(rl)` = `(rl / 10 + 1) + "L" + (rl % 10)`.
- **Once per ceremony:**
  - A refusal is said only when none was said to that player before, or the last one was 30 seconds or more
    ago.
  - The time of the last refusal said is kept in `player.TempProperties`, key `hdc_bg_porter_refused`, as
    `GameLoop.GameLoopTime` (ms).
  - The porter's callback runs twice per ceremony (`OFTeleporters.cs:113-117, 169-170`, at 5 s and 15 s),
    and a ceremony comes every 120 s.
- **Login message (§2.5):** "You have outgrown Thidranki (levels 20 to 24, realm rank 1L3 and below), so you
  are back at your bind point." It goes to the system window, 1 second after `GameEntered`, only while
  `teleport_login_bg_level_exceeded` is on (`ServerProperties.Properties.TELEPORT_LOGIN_BG_LEVEL_EXCEEDED`,
  `serverproperty/ServerProperties.cs:1621`, True in the shipped worlds).
- **Over the limit:** all of these hold:
  - the account's privilege level is 1;
  - the region is one of 250–253 and has a row;
  - Level > MaxLevel, or MaxRealmLevel > 0 and realm level ≥ MaxRealmLevel.

  Being below MinLevel is not over the limit.
- **Where an over-limit character goes:**
  - To its bind point when the bind region exists, has a zone at the bind X and Y, and is not one of
    250–253.
  - Otherwise to its home portal keep, through `GameServer.KeepManager.ExitBattleground(player)`: Castle
    Sauvage, Svasud Faste or Druim Ligen.
- **Keep level after a capture:** for a keep in 250–253 that is not a portal keep and whose Level is not 1,
  run `keep.ChangeLevel(1)`, then `keep.SaveIntoDatabase()`.
- **World data:**
  - **Names:** the marker table is `fork_world_fixes (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)`
    and the marker `classic-battlegrounds-v1`. The archive table is `fork_removed_mobs` (the `Mob` columns
    plus `FixId`, `RemovedUtc`). The savepoint is `classic_battlegrounds`.
  - **Failure:** the module returns exactly one line, "Classic battlegrounds: not applied (<error>); the
    battlegrounds stay as upstream ships them". `world_fixes.py` still commits its own fixes and exits 0.
  - **Tables it needs:** `Battleground`, `Keep`, `Mob`, `Door`, `Zones`, `Regions`, `Quest`. If one is
    missing, the module does nothing and writes no marker.
  - **New central keep rows:**
    - Dun Abermenai: KeepID 32, Region 253, 33383, 38627, 3720, heading 3858, BaseLevel 19,
      `Keep_ID` `hdc-bg253-dun-abermenai`.
    - Dun Murdaigean: KeepID 33, Region 251, 33113, 38138, 3720, heading 1583, BaseLevel 29,
      `Keep_ID` `hdc-bg251-dun-murdaigean`.
    - Both rows: Level 1; Realm and OriginalRealm 0; the three DifficultyLevel columns 1; KeepType and
      SkinType 0; ClaimedGuildName ''; CreateInfo `HearthDAoC classic-battlegrounds-v1`.
    - The KeepIDs are the first free IDs from 32.
  - **New `Mob_ID`s:**
    - copied portal keep rows: `hdc-bg<region>-pk-<source Mob_ID>`;
    - moved central rows: `hdc-bg<region>-ck-<source Mob_ID>`;
    - fighters: `hdc-bg<region>-ck-fighter-1` to `-4`;
    - the lord: `hdc-bg<region>-ck-lord`.
  - **Door values:**

    | Doors | Health | State |
    |---|---|---|
    | `253000301`/`302` | 2,545 → 3,800 | 0 → 1 |
    | `251000301`/`302` | 2,545 → 5,800 | 0 → 1 |
    | `252000301`/`302` | 5,200 → 4,800 | unchanged |
    | `250000301`/`302` | 9,200 → 7,000 | unchanged |

    Each value changes only while it still holds the old one.
  - **Stray Wizard:** it is removed only while it is exactly `caledon-guard-25`, `DOL.GS.Keeps.GuardStaticCaster`,
    "Wizard", realm 1, level 48, at 33185, 37386, 3722 in region 250.
- **What stays out of scope (§1 Non-goals):**
  - Cathal Valley (165), New Frontiers Thidranki (238) and `BGTeleporter`;
  - restoring the archived battleground monsters (#47);
  - bindstones, Siege Masters, the Medal of Valor, keep claiming (`allow_bg_claim` stays False) and capture
    realm points (still 0);
  - the 1.70 realm-point stop;
  - `Region.IsRvR`;
  - the `RegionChangeRequestHandler` `&&` check and release's `<=` test (both noted in #49, unchanged);
  - Thidranki's 11 "New Object" statics (they stay);
  - the client's texts (`loading.dat` still says "Caledon");
  - `AtlasROGManager.cs`, `GamePlayer.CleanupOnDisconnect`, `bg_release_to_portal_keep` (stays False) and
    the `GameServer.csproj` `<Folder>` lines.
- **CI filter:** `FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds`,
  pinned in `deploy/tests/test_workflows.py`.
- **Workflow:**
  - Work on branch `sub5-battlegrounds` in a clone. Add `source/server/build/` and
    `source/server/CoreServer/config/serverconfig.xml` to `.git/info/exclude`. Before any `dotnet` command,
    run `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml` and
    `export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0`.
  - The world is `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db` (`HDC_TEST_WORLD`), read-only:
    tests copy it first.
  - Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Never push; the owner merges the PR.

## Review Focus

1. **A player with the medallion is turned away and doesn't know why, or hears it twice.** For example, a
   level 22 character with 412 RP at Master Visur. Expected: one "Thidranki is for realm rank 1L3 and
   below…" per ceremony (not again at the second port 10 s later), and the medallion stays. Pinned by
   Task 1 `RealmPointCapsAtEachEdge` and `ARefusalIsSaidOncePerCeremony`, and by Task 2's wiring
   (TempProperties key; the time is stored only when said). Checked in game in Task 5 (Thidranki).
2. **A character that outlevels a battleground and link-dies inside it logs in still inside.** This
   includes a Midgard character. Expected: about 1 s after login it is at its bind point with the outgrown
   message. If its bind point is in a battleground or invalid, it goes to its home portal keep (Svasud
   Faste for Midgard). Pinned by Task 1 `OverTheLimitByLevelOrRealmLevel` and
   `GoesToTheBindPointUnlessItIsMissingZonelessOrInABattleground`, and Task 2
   `test_keep_manager_names_svasud_faste`. Checked in game in Task 5 (over the limit: `/quit`, link death,
   Midgard).
3. **The battleground fix fails on the owner's live world** (a SQL error or an unexpected schema).
   Expected:
   - the server still starts;
   - the start log has the "not applied" line;
   - nothing of the fix stays (no fork tables, no marker, no half-made keep);
   - the other world fixes are committed;
   - the next start tries again.

   Pinned by Task 3 `test_a_failing_step_rolls_back_everything` (every step) and
   `test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails`.
4. **After a capture, the central keep's guards come back about five levels higher** (keep level 4 from
   `starting_keep_level`). Expected: they come back at the same levels, and `/ck` shows the new owner.
   Pinned by Task 1 `ACentralKeepAboveLevelOneGoesBackToOne` and
   `NoKeepLevelChangeForAPortalKeepLevelOneOrAnotherRegion`, and by Task 2's `KeepTaken` handler. Checked
   in game in Task 5 (capture).
5. **The new central keeps' guards or doors are misplaced.** That would be casters inside walls or
   floating, a lord out of reach, or doors that load as plain doors and stay open. Expected: every row on
   the spec coordinates, every guard in its own keep area only, the four central doors in the keep area,
   closed and at full health. Pinned by Task 4 `BattlegroundGeometryTests`, `test_door_check`,
   `test_new_guards_stand_in_their_keep_area` and `test_new_central_keeps`. Checked in game in Task 5 (each
   new central keep).

Two more, for the source checks: an upstream sync that brings back the old porter blocks, "Svasudheim" or
the quest files fails CI (Task 2 `ClassicBattlegroundSourceTests`). A re-run after the owner deletes the
marker doubles nothing (Task 4 `test_a_removed_keep_comes_back_without_doubling_guards`).

## Decisions the plan makes where the spec is silent

Reviewers: check these.

1. **Fallback message.** When the character goes to its home portal keep instead of its bind point, the
   login text ends "so you are back at your realm's portal keep." (The spec gives only the bind-point
   text.)
2. **A failed bind move.** If the bind `MoveTo` returns false, the script falls back to
   `ExitBattleground`.
3. **Exceptions in the porter hook.** `PorterDestination` catches and logs any exception and returns null,
   so one player's error doesn't stop the porter's loop.
4. **A load log line.** `ClassicBattlegrounds.Summary` (§3.1) gives Task 5 a log check, like the Shrouded
   Isles start choice's "ready" line.
5. **Number format.** Realm points in the refusal use invariant "N0" (350, 1,375, 7,125), matching the
   spec's own number style, never the server locale.
6. **When the refusal time is stored.** It is stored only when the refusal is said, so "the last" means
   the last one said.
7. **The report line texts** in §3.4. The spec only says one line per step.
8. **Missing rows in step 5.** Step 5 skips a region whose source, template or central door rows are
   missing (no line), and the marker is still written, as for any owner-edited world.
9. **`LastTimeRowUpdated`.** Every row the module changes or adds gets `now`, as `world_fixes.py` does.
10. **The keep-level save.** The `KeepTaken` handler keeps the spec's explicit `SaveIntoDatabase()`,
    although `ChangeLevel` already saves (`AbstractGameKeep.cs:832`). It is a harmless second save.
11. **Where the source check looks.** The twelve class names are looked for only in `source/`, because
    `deploy/bin/battlegrounds.py` and its tests name them as data.

## Prerequisites

- **Branch:** `sub5-battlegrounds` holds `main` and the approved spec.
- **Tools:** the .NET 10 SDK (server build and unit tests), Python 3.10+, `ruby` (optional, for the YAML checks in `deploy/tests/test_workflows.py`).
- **World data:** the clean classic world, used read-only as `HDC_TEST_WORLD` (`~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`). The real-data tests copy it before changing anything.
- **Ignored build output:** add `source/server/build/` to `.git/info/exclude` so `git status` stays clean after a server build.

## Task split and file structure

Run the tasks in order (1 → 2 → 3 → 4 → 5). Tasks 2 and 3 both write `deploy/tests/test_battlegrounds.py`,
and Task 4 builds on Task 3's module, so they don't run in parallel.

| # | Task | Files | Depends on |
|---|---|---|---|
| 1 | Decision class, its unit tests, CI filter and pin | `GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs` (create), `Tests/UnitTests/UT_ClassicBattlegrounds.cs` (create), `.github/workflows/server-image.yml` (1 line), `deploy/tests/test_workflows.py` (constant + names list) | — |
| 2 | Wiring script, upstream edits, source checks, server docs | `GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs` (create), `scripts/teleporters/OFTeleporters.cs` (3 blocks), `keeps/KeepManager.cs` (1 word), the 12 quest files (delete), `deploy/tests/test_battlegrounds.py` (create: source checks only), `docs/fork/FORK.md` (server-code row), `client/README.md` (new "Battlegrounds" section) | 1 |
| 3 | World data, part 1 | `deploy/bin/battlegrounds.py` (create: framework, steps 1, 2, 3, 6), `deploy/bin/world_fixes.py` (call + docstring item 4), `deploy/entrypoint.sh` (comment), `deploy/tests/test_battlegrounds.py` (unit + real-data classes), `deploy/tests/test_world_fixes.py` (`test_shipped_world`: 3 → 7 lines), `docs/fork/FORK.md` ("What the fork changes" row), `deploy/HANDOFF.md` | 2 (test file exists) |
| 4 | World data, part 2: Abermenai and Murdaigean | `deploy/bin/battlegrounds.py` (steps 4, 5, geometry), `deploy/tests/test_battlegrounds.py` (geometry, step 4/5 unit cases, real-data checks), `deploy/tests/test_world_fixes.py` (7 → 9 lines) | 3 |
| 5 | Owner's in-game verification | `docs/fork/verification/sub5-ingame.md` (create) | 1–4, a server running the branch's image |

**Adjustments to the suggested shape, with reasons:**
- **The source checks file.** Task 2 creates `deploy/tests/test_battlegrounds.py` with the source checks
  only. The spec puts the source checks in the same file as the world-data tests (§4), and Task 2 is the
  task that makes them pass. Task 3 then adds the `import battlegrounds` line and its classes.
- **The player guide.** `client/README.md` goes with Task 2, because it describes behaviour the server
  code makes. The world-data docs (the FORK.md world row, the `world_fixes.py` docstring, the entrypoint
  comment and HANDOFF) go with Task 3.
- **`test_shipped_world`.** It changes twice: Task 3 makes it 7 lines (3 + steps 1, 2, 3, 6) and Task 4
  makes it 9 (+ steps 4, 5). That way each task's suite is green at its own commit.
- **Not a task.** The release-notes line (§5), the three #49 entries and closing #76 go in the PR body and
  the issues, which the main session handles when it opens the PR. The plan file itself is committed by the
  main session.

---

### Task 1: ClassicBattlegrounds decision class, its unit tests and the CI filter

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs` (the decision class; plain C#, compiled into `GameServer.dll` like every file under `GameServer/`)
- Test: `source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs` (NUnit 4, namespace `DOL.GS.Tests`, like `UT_SiStartChoice.cs`)
- Modify: `.github/workflows/server-image.yml` (one line, `:113`: the `--filter` of the step "Server unit tests for the fork's server changes")
- Modify: `deploy/tests/test_workflows.py` (in `ServerUnitTestWorkflowTests`: the `SERVER_UNIT_TESTS` constant and the `names` list)

**Interfaces:**
- Consumes: nothing. This is the first task. `ClassicBattlegrounds.cs` uses only `System`, `System.Collections.Generic`, `System.Collections.ObjectModel` and `System.Linq`. It never reads `GameServer`, `WorldMgr`, `ServerProperties` or `GamePlayer`, so the tests need no running server. The numbers in its texts are formatted with `FormattableString.Invariant`, so the server's culture never changes them.
- Produces, in `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs`, namespace `DOL.GS.HearthDAoC`. The file is ASCII with LF line endings and a file-scoped namespace, like `SiStartChoice.cs`.
  - `public sealed record BattlegroundBracket(ushort Region, string Name, byte MinLevel, byte MaxLevel, byte MaxRealmLevel, long RealmPointCap);` One battleground row. Task 2 builds one per row from `GameServer.KeepManager.GetBattleground(region)`, with `Name = Names[region]` and `RealmPointCap = REALMPOINTS_FOR_LEVEL[MaxRealmLevel]` (0 when `MaxRealmLevel` is 0).
  - `public sealed record BattlegroundLanding(string Name, ushort Region, int X, int Y, int Z, ushort Heading);` Task 2 turns it into a `GameLocation`.
  - `public sealed record PorterDecision(BattlegroundLanding Destination, string Refusal);` Exactly one of the two is non-null, or both are null when the porter would send a realm that has no landing spot (nothing is said then).
  - `public static class ClassicBattlegrounds`:
    - Constants: `RefusedAtKey = "hdc_bg_porter_refused"` (the `TempProperties` key), `RefusalQuietMs = 30_000` (`long`), `LoginCheckDelayMs = 1000` (`int`).
    - `Regions`: `IReadOnlyList<ushort>` 253, 252, 251, 250, in that order. `Names`: `IReadOnlyDictionary<ushort, string>` 253 "Abermenai", 252 "Thidranki", 251 "Murdaigean", 250 "Caledonia". Both are read-only wrappers.
    - `IsBattleground(int region)`: 250 to 253.
    - `RankLabel(int realmLevel)`: `(realmLevel / 10 + 1) + "L" + (realmLevel % 10)`: 2 is "1L2", 10 is "2L0".
    - `Landing(int realm, BattlegroundBracket bracket)`: realm 1 gives `(bracket.Name, bracket.Region, 38113, 53507, 4160, 3268)`, realm 2 `(…, 53568, 23643, 4530, 0)`, realm 3 `(…, 17367, 18248, 4320, 0)`; any other realm gives null.
    - `Porter(int level, int realmLevel, long realmPoints, int realm, IReadOnlyList<BattlegroundBracket> brackets)`:
      - The bracket is the first, in list order, whose `MinLevel`..`MaxLevel` holds the level. It goes (`Destination = Landing(realm, bracket)`) when `MaxRealmLevel` is 0 or the realm level is below it. Otherwise the refusal is "`<Name>` is for realm rank `<RankLabel(MaxRealmLevel - 1)>` and below, under `<cap N0>` realm points. You have `<points N0>`, so I cannot send you there."
      - With no bracket for the level: below the lowest `MinLevel`, "The battlegrounds are for levels `<lowest>` to `<highest>`. Come back when you reach level `<lowest>`."; above the highest `MaxLevel`, "You have outgrown the battlegrounds, which are for levels `<lowest>` to `<highest>`."; otherwise, or with no brackets at all, "No battleground on this server takes level `<level>`."
      - A refusal is given for any realm; only a go needs the realm's landing spot.
    - `ShouldSayRefusal(long now, long? lastSaid)`: `lastSaid == null || now - lastSaid >= RefusalQuietMs`. Task 2 stores `GameLoop.GameLoopTime` under `RefusedAtKey` only when it says a refusal.
    - `IsOverLimit(uint privLevel, int region, int level, int realmLevel, IReadOnlyList<BattlegroundBracket> brackets, out BattlegroundBracket bracket)`: `privLevel == 1`, `IsBattleground(region)`, a bracket with that `Region` exists, and `level > MaxLevel || (MaxRealmLevel > 0 && realmLevel >= MaxRealmLevel)`. `bracket` is that row when the result is true, and null whenever it is false.
    - `GoesToBind(bool bindRegionExists, bool bindHasZone, int bindRegion)`: `bindRegionExists && bindHasZone && !IsBattleground(bindRegion)`. True means the bind point; false means `KeepManager.ExitBattleground`.
    - `OutgrownMessage(BattlegroundBracket bracket, bool atBind)`: "You have outgrown Thidranki (levels 20 to 24, realm rank 1L3 and below), so you are back at your bind point." With `atBind` false it ends "so you are back at your realm's portal keep." With `MaxRealmLevel` 0 the brackets hold only "(levels 20 to 24)".
    - `ShouldResetKeepLevel(int region, bool isPortalKeep, int level)`: `IsBattleground(region) && !isPortalKeep && level != 1`.
    - `Summary(IReadOnlyList<BattlegroundBracket> brackets)`: over `Regions` in order, "Classic battlegrounds: Abermenai 15-19 up to 1L2, Thidranki 20-24 up to 1L3, Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9". A row with `MaxRealmLevel` 0 reads "`<Name>` `<min>`-`<max>` with no realm rank cap"; a region without a row reads "`<Name>` off (no battleground row)".
- Produces, for CI:
  - the step "Server unit tests for the fork's server changes" runs `--filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds"`;
  - `deploy/tests/test_workflows.py` pins that exact command line as `SERVER_UNIT_TESTS`, and its `names` list is `["UT_CommandPrivLevelOverrides", "UT_SiStartChoice", "UT_ClassicBattlegrounds"]`. The existing test then checks that `UT_ClassicBattlegrounds.cs` holds `public sealed class UT_ClassicBattlegrounds`, because `dotnet test` exits 0 when a filter matches no test.

All commands run from the repository root, on branch `sub5-battlegrounds`.

**Prerequisites (once per clone):**

The test build writes into `source/server/build/` (the Tests project's obj and lib folders), which only a local exclude ignores. The build needs the placeholder config, as in CI; `serverconfig.xml` is already ignored by `source/server/CoreServer/config/.gitignore`, so its exclude line is only a second guard. Set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` in the shell, as CI does:

```bash
grep -qxF 'source/server/build/' .git/info/exclude || echo 'source/server/build/' >> .git/info/exclude
grep -qxF 'source/server/CoreServer/config/serverconfig.xml' .git/info/exclude || echo 'source/server/CoreServer/config/serverconfig.xml' >> .git/info/exclude
cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
git status --short
```

Expected: no output from `git status --short`.

- [ ] **Step 1: Write the failing test**

Create `source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs`. It has one test method per case in the spec's list of server unit tests (section 4), 15 in all:

```csharp
using System.Collections.Generic;
using System.Linq;
using DOL.GS.HearthDAoC;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the classic battlegrounds. A frontier porter sends a character wearing the battlegrounds
// medallion to the battleground for its level (Abermenai 15-19, Thidranki 20-24, Murdaigean 25-29,
// Caledonia 30-35) while its realm level is under that battleground's cap, and says why when it won't.
// ClassicBattlegrounds holds every decision; the game wiring (ClassicBattlegroundsScript) only feeds it the
// battleground rows and the character's state.
[TestFixture]
public sealed class UT_ClassicBattlegrounds
{
    private const int Albion = (int)eRealm.Albion, Midgard = (int)eRealm.Midgard, Hibernia = (int)eRealm.Hibernia;
    private const uint Player = (uint)ePrivLevel.Player, Gm = (uint)ePrivLevel.GM, Admin = (uint)ePrivLevel.Admin;

    // The four battleground rows after the world fix. The cap is REALMPOINTS_FOR_LEVEL[MaxRealmLevel].
    private static List<BattlegroundBracket> Classic()
    {
        return new List<BattlegroundBracket>
        {
            new(253, "Abermenai", 15, 19, 3, 125),
            new(252, "Thidranki", 20, 24, 4, 350),
            new(251, "Murdaigean", 25, 29, 6, 1375),
            new(250, "Caledonia", 30, 35, 10, 7125),
        };
    }

    private static List<BattlegroundBracket> Without(int region)
    {
        return Classic().Where(b => b.Region != region).ToList();
    }

    // The classic rows, with this region's row uncapped (MaxRealmLevel 0).
    private static List<BattlegroundBracket> Uncapped(int region)
    {
        return Classic().Select(b => b.Region == region ? b with { MaxRealmLevel = 0, RealmPointCap = 0 } : b).ToList();
    }

    private static BattlegroundBracket Row(int region)
    {
        return Classic().Single(b => b.Region == region);
    }

    // The porter's decision for a 1L1 Albion character with no realm points, with one thing changed per call.
    private static PorterDecision Porter(int level, int realmLevel = 1, long realmPoints = 0, int realm = Albion,
        IReadOnlyList<BattlegroundBracket> brackets = null)
    {
        return ClassicBattlegrounds.Porter(level, realmLevel, realmPoints, realm, brackets ?? Classic());
    }

    private static PorterDecision Refused(string text)
    {
        return new PorterDecision(null, text);
    }

    [Test]
    public void LevelsOutsideTheRangeAreRefusedWithTheirTexts()
    {
        foreach (int level in new[] { 1, 14 })
            Assert.That(Porter(level), Is.EqualTo(Refused(
                "The battlegrounds are for levels 15 to 35. Come back when you reach level 15.")), $"level {level}");

        foreach (int level in new[] { 36, 50 })
            Assert.That(Porter(level), Is.EqualTo(Refused(
                "You have outgrown the battlegrounds, which are for levels 15 to 35.")), $"level {level}");

        // The levels in the texts come from the rows: the lowest MinLevel and the highest MaxLevel.
        Assert.That(Porter(17, brackets: Without(253)), Is.EqualTo(Refused(
            "The battlegrounds are for levels 20 to 35. Come back when you reach level 20.")));
        Assert.That(Porter(30, brackets: Without(250)), Is.EqualTo(Refused(
            "You have outgrown the battlegrounds, which are for levels 15 to 29.")));
    }

    [TestCase(15, 253, "Abermenai")]
    [TestCase(19, 253, "Abermenai")]
    [TestCase(20, 252, "Thidranki")]
    [TestCase(24, 252, "Thidranki")]
    [TestCase(25, 251, "Murdaigean")]
    [TestCase(29, 251, "Murdaigean")]
    [TestCase(30, 250, "Caledonia")]
    [TestCase(35, 250, "Caledonia")]
    public void EachLevelGoesToItsBattleground(int level, int region, string name)
    {
        Assert.That(Porter(level), Is.EqualTo(new PorterDecision(
            new BattlegroundLanding(name, (ushort)region, 38113, 53507, 4160, 3268), null)));
    }

    // Realm level is (rank - 1) * 10 + level, so 2 is 1L2, and MaxRealmLevel means "must be below". The
    // realm points are written as in English whatever the server's culture (fr-FR would write 1 375).
    [TestCase(17, 253, 2, 124, 3, 125,
        "Abermenai is for realm rank 1L2 and below, under 125 realm points. You have 125, so I cannot send you there.")]
    [TestCase(22, 252, 3, 349, 4, 412,
        "Thidranki is for realm rank 1L3 and below, under 350 realm points. You have 412, so I cannot send you there.")]
    [TestCase(27, 251, 5, 1374, 6, 1375,
        "Murdaigean is for realm rank 1L5 and below, under 1,375 realm points. You have 1,375, so I cannot send you there.")]
    [TestCase(32, 250, 9, 7124, 10, 10000,
        "Caledonia is for realm rank 1L9 and below, under 7,125 realm points. You have 10,000, so I cannot send you there.")]
    [SetCulture("fr-FR")]
    public void RealmPointCapsAtEachEdge(int level, int region, int highestRealmLevel, long highestPoints,
        int refusedRealmLevel, long refusedPoints, string refusal)
    {
        PorterDecision goes = Porter(level, highestRealmLevel, highestPoints);
        Assert.That(goes.Refusal, Is.Null);
        Assert.That(goes.Destination.Region, Is.EqualTo(region));

        Assert.That(Porter(level, refusedRealmLevel, refusedPoints), Is.EqualTo(Refused(refusal)));
    }

    [Test]
    public void MaxRealmLevelZeroMeansNoCap()
    {
        List<BattlegroundBracket> brackets = Uncapped(252);

        PorterDecision decision = Porter(22, realmLevel: 50, realmPoints: 1_000_000, brackets: brackets);
        Assert.That(decision.Refusal, Is.Null);
        Assert.That(decision.Destination.Region, Is.EqualTo(252));

        // Over the limit only by level then.
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, 252, 24, 50, brackets, out _), Is.False);
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, 252, 25, 1, brackets, out _), Is.True);
    }

    [TestCase(253)]
    [TestCase(252)]
    [TestCase(251)]
    [TestCase(250)]
    public void EachRealmLandsBesideItsPortalKeep(int region)
    {
        BattlegroundBracket bracket = Row(region);
        var spots = new Dictionary<int, BattlegroundLanding>
        {
            [Albion] = new(bracket.Name, bracket.Region, 38113, 53507, 4160, 3268),
            [Midgard] = new(bracket.Name, bracket.Region, 53568, 23643, 4530, 0),
            [Hibernia] = new(bracket.Name, bracket.Region, 17367, 18248, 4320, 0),
        };

        foreach ((int realm, BattlegroundLanding spot) in spots)
        {
            Assert.That(ClassicBattlegrounds.Landing(realm, bracket), Is.EqualTo(spot), $"realm {realm}");
            Assert.That(Porter(bracket.MinLevel, realm: realm), Is.EqualTo(new PorterDecision(spot, null)), $"realm {realm}");
        }

        // Any other realm has no landing spot: the porter neither sends it nor says anything.
        foreach (int realm in new[] { 0, 4 })
        {
            Assert.That(ClassicBattlegrounds.Landing(realm, bracket), Is.Null, $"realm {realm}");
            Assert.That(Porter(bracket.MinLevel, realm: realm), Is.EqualTo(new PorterDecision(null, null)), $"realm {realm}");
        }
    }

    [Test]
    public void ALevelWithoutARowIsRefused()
    {
        Assert.That(Porter(22, brackets: Without(252)),
            Is.EqualTo(Refused("No battleground on this server takes level 22.")));

        foreach (int level in new[] { 1, 22, 50 })
            Assert.That(Porter(level, brackets: new List<BattlegroundBracket>()),
                Is.EqualTo(Refused($"No battleground on this server takes level {level}.")), $"level {level}");
    }

    // Level above MaxLevel, or realm level at or above MaxRealmLevel. Being below MinLevel is not over the limit.
    [TestCase(253, 20, 1, true)]
    [TestCase(253, 19, 3, true)]
    [TestCase(253, 19, 2, false)]
    [TestCase(253, 10, 1, false)]
    [TestCase(252, 25, 1, true)]
    [TestCase(252, 24, 4, true)]
    [TestCase(252, 24, 3, false)]
    [TestCase(252, 15, 1, false)]
    [TestCase(251, 30, 1, true)]
    [TestCase(251, 29, 6, true)]
    [TestCase(251, 29, 5, false)]
    [TestCase(250, 36, 1, true)]
    [TestCase(250, 35, 10, true)]
    [TestCase(250, 35, 9, false)]
    public void OverTheLimitByLevelOrRealmLevel(int region, int level, int realmLevel, bool over)
    {
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, region, level, realmLevel, Classic(),
            out BattlegroundBracket bracket), Is.EqualTo(over));
        Assert.That(bracket, Is.EqualTo(over ? Row(region) : null));
    }

    [Test]
    public void NotOverTheLimitForAGmOrOutsideTheFour()
    {
        foreach (uint privLevel in new[] { Gm, Admin })
            Assert.That(ClassicBattlegrounds.IsOverLimit(privLevel, 252, 50, 50, Classic(), out _), Is.False,
                $"privilege level {privLevel}");

        foreach (int region in new[] { 250, 251, 252, 253 })
            Assert.That(ClassicBattlegrounds.IsBattleground(region), Is.True, $"region {region}");

        // Cathal Valley has a battleground row too (45-49, under realm level 45), but it is not one of the four.
        List<BattlegroundBracket> brackets = Classic();
        brackets.Add(new BattlegroundBracket(165, "Cathal Valley", 45, 49, 45, 734250));
        foreach (int region in new[] { 1, 165, 238, 249, 254 })
        {
            Assert.That(ClassicBattlegrounds.IsBattleground(region), Is.False, $"region {region}");
            Assert.That(ClassicBattlegrounds.IsOverLimit(Player, region, 50, 50, brackets, out BattlegroundBracket bracket),
                Is.False, $"region {region}");
            Assert.That(bracket, Is.Null, $"region {region}");
        }

        // One of the four without its row has no limit to compare with.
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, 252, 50, 50, Without(252), out _), Is.False);
    }

    [TestCase(true, true, 1, true)]
    [TestCase(true, true, 100, true)]
    [TestCase(true, true, 200, true)]
    [TestCase(true, true, 165, true)]
    [TestCase(false, true, 1, false)]
    [TestCase(true, false, 1, false)]
    [TestCase(false, false, 1, false)]
    [TestCase(true, true, 250, false)]
    [TestCase(true, true, 251, false)]
    [TestCase(true, true, 252, false)]
    [TestCase(true, true, 253, false)]
    public void GoesToTheBindPointUnlessItIsMissingZonelessOrInABattleground(bool bindRegionExists, bool bindHasZone,
        int bindRegion, bool toBind)
    {
        Assert.That(ClassicBattlegrounds.GoesToBind(bindRegionExists, bindHasZone, bindRegion), Is.EqualTo(toBind));
    }

    // A capture resets a keep to starting_keep_level (4 in the shipped worlds).
    [TestCase(253, 4)]
    [TestCase(252, 4)]
    [TestCase(251, 2)]
    [TestCase(250, 10)]
    public void ACentralKeepAboveLevelOneGoesBackToOne(int region, int level)
    {
        Assert.That(ClassicBattlegrounds.ShouldResetKeepLevel(region, isPortalKeep: false, level), Is.True);
    }

    [TestCase(252, true, 4)]
    [TestCase(250, true, 1)]
    [TestCase(252, false, 1)]
    [TestCase(253, false, 1)]
    [TestCase(165, false, 4)]
    [TestCase(238, false, 4)]
    [TestCase(163, false, 4)]
    public void NoKeepLevelChangeForAPortalKeepLevelOneOrAnotherRegion(int region, bool isPortalKeep, int level)
    {
        Assert.That(ClassicBattlegrounds.ShouldResetKeepLevel(region, isPortalKeep, level), Is.False);
    }

    [TestCase(1, "1L1")]
    [TestCase(2, "1L2")]
    [TestCase(3, "1L3")]
    [TestCase(5, "1L5")]
    [TestCase(9, "1L9")]
    [TestCase(10, "2L0")]
    [TestCase(24, "3L4")]
    public void RankLabels(int realmLevel, string label)
    {
        Assert.That(ClassicBattlegrounds.RankLabel(realmLevel), Is.EqualTo(label));
    }

    [Test]
    public void ARefusalIsSaidOncePerCeremony()
    {
        Assert.That(ClassicBattlegrounds.RefusedAtKey, Is.EqualTo("hdc_bg_porter_refused"));
        Assert.That(ClassicBattlegrounds.RefusalQuietMs, Is.EqualTo(30_000));

        const long now = 1_000_000;
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, null), Is.True, "none said before");
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, now - 30_000), Is.True, "30 s ago");
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, now - 10_000), Is.False, "10 s ago");
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, now - 29_999), Is.False, "just under 30 s ago");

        // The porter casts every 120 s, and its callback runs 5 s and 15 s after each cast. The time is stored
        // only when the refusal is said.
        long? lastSaid = null;
        var said = new List<long>();
        foreach (long cast in new long[] { 0, 120_000, 240_000 })
        foreach (long run in new[] { cast + 5_000, cast + 15_000 })
        {
            if (!ClassicBattlegrounds.ShouldSayRefusal(run, lastSaid))
                continue;

            said.Add(run);
            lastSaid = run;
        }

        Assert.That(said, Is.EqualTo(new long[] { 5_000, 125_000, 245_000 }));
    }

    [Test]
    public void OutgrownMessages()
    {
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Row(252), atBind: true), Is.EqualTo(
            "You have outgrown Thidranki (levels 20 to 24, realm rank 1L3 and below), so you are back at your bind point."));
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Row(252), atBind: false), Is.EqualTo(
            "You have outgrown Thidranki (levels 20 to 24, realm rank 1L3 and below), so you are back at your realm's portal keep."));
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Row(250), atBind: true), Is.EqualTo(
            "You have outgrown Caledonia (levels 30 to 35, realm rank 1L9 and below), so you are back at your bind point."));
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Uncapped(252).Single(b => b.Region == 252), atBind: true),
            Is.EqualTo("You have outgrown Thidranki (levels 20 to 24), so you are back at your bind point."));

        // The script checks again, and sends the message, this long after GameEntered.
        Assert.That(ClassicBattlegrounds.LoginCheckDelayMs, Is.EqualTo(1000));
    }

    [Test]
    public void SummaryLine()
    {
        Assert.That(ClassicBattlegrounds.Regions, Is.EqualTo(new ushort[] { 253, 252, 251, 250 }));
        Assert.That(ClassicBattlegrounds.Names, Is.EqualTo(new Dictionary<ushort, string>
        {
            [253] = "Abermenai",
            [252] = "Thidranki",
            [251] = "Murdaigean",
            [250] = "Caledonia",
        }));

        Assert.That(ClassicBattlegrounds.Summary(Classic()), Is.EqualTo("Classic battlegrounds: Abermenai 15-19 up to 1L2, "
            + "Thidranki 20-24 up to 1L3, Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9"));

        // In region order whatever the order of the rows; a missing row and a row without a cap say so.
        List<BattlegroundBracket> edited = Uncapped(250).Where(b => b.Region != 252).Reverse().ToList();
        Assert.That(ClassicBattlegrounds.Summary(edited), Is.EqualTo("Classic battlegrounds: Abermenai 15-19 up to 1L2, "
            + "Thidranki off (no battleground row), Murdaigean 25-29 up to 1L5, Caledonia 30-35 with no realm rank cap"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_ClassicBattlegrounds"; echo "rc=$?"`

The run builds the whole server first. It took about 20 s here, with the NuGet packages already cached; the time varies.

Expected: the Tests project doesn't compile, because the three records and `ClassicBattlegrounds` don't exist yet. (The namespace `DOL.GS.HearthDAoC` does, from `SiStartChoice.cs`.) These 7 errors; the compiler may print them in another order. The output below leaves out the build warnings and shortens `<clone>/source/...` paths and the trailing `[...Tests.csproj]`:
```
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(20,25): error CS0246: The type or namespace name 'BattlegroundBracket' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(31,25): error CS0246: The type or namespace name 'BattlegroundBracket' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(37,25): error CS0246: The type or namespace name 'BattlegroundBracket' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(42,20): error CS0246: The type or namespace name 'BattlegroundBracket' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(49,23): error CS0246: The type or namespace name 'BattlegroundBracket' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(48,20): error CS0246: The type or namespace name 'PorterDecision' could not be found (are you missing a using directive or an assembly reference?)
source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs(54,20): error CS0246: The type or namespace name 'PorterDecision' could not be found (are you missing a using directive or an assembly reference?)
rc=1
```

- [ ] **Step 3: Write the minimal implementation**

Create `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs` (the folder already holds `SiStartChoice.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the classic battlegrounds, Abermenai (15-19), Thidranki (20-24), Murdaigean (25-29) and
// Caledonia (30-35), each with a realm rank cap from its battleground row. This class holds every decision:
// where the frontier porter sends a character wearing the battlegrounds medallion, or why it won't and
// whether it says so now; when a character is over a battleground's limit and where it goes then; and a
// central keep's level after a capture. It reads nothing from the running server (GameServer, WorldMgr,
// ServerProperties, GamePlayer), so unit tests drive it directly; ClassicBattlegroundsScript feeds it the
// battleground rows and the character's state and carries out the outcome.

// One battleground's row. RealmPointCap is REALMPOINTS_FOR_LEVEL[MaxRealmLevel] (0 when MaxRealmLevel is 0);
// the script computes it, so this file never reads GamePlayer.
public sealed record BattlegroundBracket(ushort Region, string Name, byte MinLevel, byte MaxLevel,
    byte MaxRealmLevel, long RealmPointCap);

// Where the porter sends a realm in a battleground.
public sealed record BattlegroundLanding(string Name, ushort Region, int X, int Y, int Z, ushort Heading);

// Exactly one of the two is non-null, or both are null (a realm with no landing spot: nothing said).
public sealed record PorterDecision(BattlegroundLanding Destination, string Refusal);

public static class ClassicBattlegrounds
{
    public const string RefusedAtKey = "hdc_bg_porter_refused";   // player.TempProperties key
    public const long RefusalQuietMs = 30_000;                    // "within the last 30 seconds"
    public const int LoginCheckDelayMs = 1000;                    // GameEntered -> check

    // 253, 252, 251, 250, in that order (also the order of log and report lists).
    public static readonly IReadOnlyList<ushort> Regions = new ReadOnlyCollection<ushort>(new ushort[] { 253, 252, 251, 250 });

    public static readonly IReadOnlyDictionary<ushort, string> Names = new ReadOnlyDictionary<ushort, string>(
        new Dictionary<ushort, string>
        {
            [253] = "Abermenai",
            [252] = "Thidranki",
            [251] = "Murdaigean",
            [250] = "Caledonia",
        });

    public static bool IsBattleground(int region)
    {
        return region >= 250 && region <= 253;
    }

    // Realm level is (rank - 1) * 10 + level: 2 -> "1L2", 9 -> "1L9", 10 -> "2L0".
    public static string RankLabel(int realmLevel)
    {
        return FormattableString.Invariant($"{realmLevel / 10 + 1}L{realmLevel % 10}");
    }

    // Today's landing spots beside each realm's portal keep, the same in every battleground
    // (OFTeleporters.cs). Any other realm has none.
    public static BattlegroundLanding Landing(int realm, BattlegroundBracket bracket)
    {
        return realm switch
        {
            1 => new BattlegroundLanding(bracket.Name, bracket.Region, 38113, 53507, 4160, 3268),
            2 => new BattlegroundLanding(bracket.Name, bracket.Region, 53568, 23643, 4530, 0),
            3 => new BattlegroundLanding(bracket.Name, bracket.Region, 17367, 18248, 4320, 0),
            _ => null,
        };
    }

    // The battleground whose MinLevel..MaxLevel holds the level, if the realm level is below its
    // MaxRealmLevel (0: no cap); otherwise the reason, with the levels, ranks and caps of the rows.
    public static PorterDecision Porter(int level, int realmLevel, long realmPoints, int realm,
        IReadOnlyList<BattlegroundBracket> brackets)
    {
        BattlegroundBracket bracket = brackets.FirstOrDefault(b => level >= b.MinLevel && level <= b.MaxLevel);

        if (bracket != null)
        {
            if (bracket.MaxRealmLevel == 0 || realmLevel < bracket.MaxRealmLevel)
                return new PorterDecision(Landing(realm, bracket), null);

            string rank = RankLabel(bracket.MaxRealmLevel - 1);
            return Refuse($"{bracket.Name} is for realm rank {rank} and below, under {bracket.RealmPointCap:N0} realm points. You have {realmPoints:N0}, so I cannot send you there.");
        }

        if (brackets.Count > 0)
        {
            int lowest = brackets.Min(b => b.MinLevel);
            int highest = brackets.Max(b => b.MaxLevel);

            if (level < lowest)
                return Refuse($"The battlegrounds are for levels {lowest} to {highest}. Come back when you reach level {lowest}.");

            if (level > highest)
                return Refuse($"You have outgrown the battlegrounds, which are for levels {lowest} to {highest}.");
        }

        return Refuse($"No battleground on this server takes level {level}.");
    }

    // Numbers as in English (350, 1,375, 7,125), whatever the server's culture.
    private static PorterDecision Refuse(FormattableString text)
    {
        return new PorterDecision(null, FormattableString.Invariant(text));
    }

    // lastSaid is the GameLoopTime of the last refusal said to this player, or null if none. The porter's
    // callback runs twice per ceremony (5 s and 15 s after the cast) and casts every 120 s, so this says a
    // refusal once per ceremony.
    public static bool ShouldSayRefusal(long now, long? lastSaid)
    {
        return lastSaid == null || now - lastSaid.Value >= RefusalQuietMs;
    }

    // A player's character (privilege level 1) in one of the four battlegrounds, above its MaxLevel or at or
    // above its MaxRealmLevel (0: no cap). Being below MinLevel is not over the limit. bracket is that
    // battleground's row when this returns true, and null otherwise.
    public static bool IsOverLimit(uint privLevel, int region, int level, int realmLevel,
        IReadOnlyList<BattlegroundBracket> brackets, out BattlegroundBracket bracket)
    {
        bracket = null;

        if (privLevel != 1 || !IsBattleground(region))
            return false;

        BattlegroundBracket row = brackets.FirstOrDefault(b => b.Region == region);

        if (row == null)
            return false;

        if (level <= row.MaxLevel && (row.MaxRealmLevel == 0 || realmLevel < row.MaxRealmLevel))
            return false;

        bracket = row;
        return true;
    }

    // true: the bind point. false: the realm's home portal keep (KeepManager.ExitBattleground).
    public static bool GoesToBind(bool bindRegionExists, bool bindHasZone, int bindRegion)
    {
        return bindRegionExists && bindHasZone && !IsBattleground(bindRegion);
    }

    // The login message for a character moved out of a battleground it has outgrown.
    public static string OutgrownMessage(BattlegroundBracket bracket, bool atBind)
    {
        string limits = FormattableString.Invariant($"levels {bracket.MinLevel} to {bracket.MaxLevel}");

        if (bracket.MaxRealmLevel > 0)
            limits += $", realm rank {RankLabel(bracket.MaxRealmLevel - 1)} and below";

        string place = atBind ? "your bind point" : "your realm's portal keep";
        return $"You have outgrown {bracket.Name} ({limits}), so you are back at {place}.";
    }

    // A capture resets a keep to starting_keep_level; a battleground's central keep goes back to level 1.
    public static bool ShouldResetKeepLevel(int region, bool isPortalKeep, int level)
    {
        return IsBattleground(region) && !isPortalKeep && level != 1;
    }

    // The load log line, over Regions in order.
    public static string Summary(IReadOnlyList<BattlegroundBracket> brackets)
    {
        IEnumerable<string> parts = Regions.Select(region =>
        {
            BattlegroundBracket bracket = brackets.FirstOrDefault(b => b.Region == region);

            if (bracket == null)
                return $"{Names[region]} off (no battleground row)";

            string levels = FormattableString.Invariant($"{bracket.Name} {bracket.MinLevel}-{bracket.MaxLevel}");
            return bracket.MaxRealmLevel == 0
                ? $"{levels} with no realm rank cap"
                : $"{levels} up to {RankLabel(bracket.MaxRealmLevel - 1)}";
        });

        return "Classic battlegrounds: " + string.Join(", ", parts);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_ClassicBattlegrounds" 2>&1 | tail -1`

Expected: all 66 cases pass. That is 15 test methods; the `[TestCase]` rows count separately. The build took about 20 s here, and the duration varies:
```
Passed!  - Failed:     0, Passed:    66, Skipped:     0, Total:    66, Duration: 45 ms - Tests.dll (net10.0)
```

`RealmPointCapsAtEachEdge` runs under the fr-FR culture (`[SetCulture("fr-FR")]`). If `Refuse` formatted with the current culture instead of `FormattableString.Invariant`, its Murdaigean and Caledonia cases would fail with "under 1 375 realm points" and "You have 10 000".

- [ ] **Step 5: Write the failing CI pin test**

In `deploy/tests/test_workflows.py`, replace the two lines of the `SERVER_UNIT_TESTS` constant (`:221-222`):

```python
SERVER_UNIT_TESTS = ("dotnet test source/server/Tests/Tests.csproj --nologo --filter "
                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"')
```

with these three:

```python
SERVER_UNIT_TESTS = ("dotnet test source/server/Tests/Tests.csproj --nologo --filter "
                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice'
                     '|FullyQualifiedName~UT_ClassicBattlegrounds"')
```

Then, in `test_every_name_in_the_filter_is_a_test_class`, replace the line

```python
        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice"])
```

with

```python
        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice", "UT_ClassicBattlegrounds"])
```

Run: `git diff -U0 deploy/tests/test_workflows.py | grep '^[-+] '`

Expected:
```
-                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"')
+                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice'
+                     '|FullyQualifiedName~UT_ClassicBattlegrounds"')
-        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice"])
+        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice", "UT_ClassicBattlegrounds"])
```

- [ ] **Step 6: Run the pin test to verify it fails**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_workflows.py -k ServerUnitTestWorkflowTests -v`

Expected: the class check passes, because `UT_ClassicBattlegrounds.cs` exists since Step 1. The filter check fails, because the workflow still runs only the first two classes:
```
test_every_name_in_the_filter_is_a_test_class (tests.test_workflows.ServerUnitTestWorkflowTests.test_every_name_in_the_filter_is_a_test_class) ... ok
test_the_fork_server_unit_tests_run_with_the_pinned_filter (tests.test_workflows.ServerUnitTestWorkflowTests.test_the_fork_server_unit_tests_run_with_the_pinned_filter) ... FAIL

======================================================================
FAIL: test_the_fork_server_unit_tests_run_with_the_pinned_filter (tests.test_workflows.ServerUnitTestWorkflowTests.test_the_fork_server_unit_tests_run_with_the_pinned_filter)
----------------------------------------------------------------------
Traceback (most recent call last):
  File "<clone>/deploy/tests/test_workflows.py", line 237, in test_the_fork_server_unit_tests_run_with_the_pinned_filter
    self.assertIn(SERVER_UNIT_TESTS + "\n", found[0])
AssertionError: 'dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds"\n' not found in 'name: Server unit tests for the fork\'s server changes\n        env:\n          DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: "0"\n        run: |\n          # The server build copies config/serverconfig.xml; use the same loopback placeholder as the image build.\n          cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml\n          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"\n'

----------------------------------------------------------------------
Ran 2 tests in 0.001s

FAILED (failures=1)
```

- [ ] **Step 7: Extend the CI filter**

```bash
sed -i 's/--filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"$/--filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds"/' .github/workflows/server-image.yml
git diff --stat .github/workflows/server-image.yml
git diff -U0 .github/workflows/server-image.yml | grep '^[-+] '
```

Expected:
```
 .github/workflows/server-image.yml | 2 +-
 1 file changed, 1 insertion(+), 1 deletion(-)
-          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice"
+          dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds"
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_workflows.py -v 2>&1 | grep -E 'ServerUnitTestWorkflowTests|^Ran|^OK|FAIL'`

Expected: the whole workflow test file passes, 16 tests; the time varies. The `ClientPatchWorkflowTests` need `ruby`; without it they are skipped and the last line reads `OK (skipped=6)`:
```
test_every_name_in_the_filter_is_a_test_class (tests.test_workflows.ServerUnitTestWorkflowTests.test_every_name_in_the_filter_is_a_test_class) ... ok
test_the_fork_server_unit_tests_run_with_the_pinned_filter (tests.test_workflows.ServerUnitTestWorkflowTests.test_the_fork_server_unit_tests_run_with_the_pinned_filter) ... ok
Ran 16 tests in 0.890s
OK
```

Then run the CI step's own command, with the three fork test classes:

Run: `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml && dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds" 2>&1 | tail -1`

Expected: 3 `UT_CommandPrivLevelOverrides` cases, 52 `UT_SiStartChoice` cases and the 66 new ones; the duration varies:
```
Passed!  - Failed:     0, Passed:   121, Skipped:     0, Total:   121, Duration: 66 ms - Tests.dll (net10.0)
```

Run: `git status --short --untracked-files=all`

Expected: only this task's four files; the build output is ignored:
```
 M .github/workflows/server-image.yml
 M deploy/tests/test_workflows.py
?? source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs
?? source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs
```

- [ ] **Step 9: Commit**

```bash
git add source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs .github/workflows/server-image.yml deploy/tests/test_workflows.py
git commit -q -m "feat(server): ClassicBattlegrounds decides the porter and battleground limits

ClassicBattlegrounds holds every decision of the classic battlegrounds and
reads nothing from the running server, so unit tests drive it directly:
the battleground for a level and the realm level cap from the rows
(Abermenai 15-19 up to 1L2, Thidranki 20-24 up to 1L3, Murdaigean 25-29 up
to 1L5, Caledonia 30-35 up to 1L9; MaxRealmLevel 0 means no cap), each
realm's landing spot, the porter's four refusal texts, saying a refusal
once per ceremony, over the limit (players only, the four regions, above
MaxLevel or at MaxRealmLevel), bind point or home portal keep, a central
keep back to level 1 after a capture, the login message and the load line.

UT_ClassicBattlegrounds covers each case of the spec's test list. CI runs it
next to UT_CommandPrivLevelOverrides and UT_SiStartChoice, and
test_workflows.py pins the extended filter.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline -1 && git status --short --untracked-files=all
```

Expected: the new commit (the hash differs) and a clean tree:
```
18239c3 feat(server): ClassicBattlegrounds decides the porter and battleground limits
```

---

### Task 2: ClassicBattlegroundsScript, the three upstream edits, the source checks and the docs

**Files:**
- Create: `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs` (the porter hook and the game wiring; compiled into `GameServer.dll` like every file under `GameServer/`)
- Modify: `source/server/GameServer/scripts/teleporters/OFTeleporters.cs` (upstream: each of the three `case BattlegroundsID:` blocks becomes one call)
- Modify: `source/server/GameServer/keeps/KeepManager.cs` (upstream, `:786`: one word)
- Delete: the twelve files in `source/server/GameServer/scripts/quests/BattlegroundQuests/` (upstream: six in `Caledonia/`, six in `Thidranki/`)
- Test: `deploy/tests/test_battlegrounds.py` (create, with the source checks only; Task 3 adds the world-data tests)
- Modify: `docs/fork/FORK.md` (one row in the server-code changes table)
- Modify: `client/README.md` (a new section, "Battlegrounds")

**Interfaces:**
- Consumes, from Task 1 (`ClassicBattlegrounds.cs`, namespace `DOL.GS.HearthDAoC`): the records `BattlegroundBracket`, `BattlegroundLanding` and `PorterDecision`, and from `ClassicBattlegrounds` the members `RefusedAtKey`, `LoginCheckDelayMs`, `Regions`, `Names`, `Porter(...)`, `ShouldSayRefusal(...)`, `IsOverLimit(...)`, `GoesToBind(...)`, `OutgrownMessage(...)`, `ShouldResetKeepLevel(...)` and `Summary(...)`.
- Produces, in `ClassicBattlegroundsScript.cs` (namespace `DOL.GS.HearthDAoC`, file-scoped, LF line endings, plain ASCII):
  - `public static class ClassicBattlegroundsScript`
  - `public static GameLocation PorterDestination(GameNPC porter, GamePlayer player)`. It builds the brackets, asks `ClassicBattlegrounds.Porter` and returns the landing spot as a `GameLocation`, or null. On a refusal it returns null, and when `ShouldSayRefusal(GameLoop.GameLoopTime, <the stored time>)` allows it, it says the text with `porter.SayTo(player, eChatLoc.CL_ChatWindow, text)` and stores `GameLoop.GameLoopTime` (a `long`) under `RefusedAtKey` in `player.TempProperties`. Any exception is logged and returns null, so the porter goes on with the other players.
  - `[ScriptLoadedEvent] public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)`: logs `ClassicBattlegrounds.Summary(...)` at Info and adds the three handlers.
  - `[ScriptUnloadedEvent] public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)`: removes them.
  - Everything else is private:
    - `OnQuit`: a `GamePlayer` sender that is over the limit is moved out (`MoveOut`).
    - `OnGameEntered`: while `teleport_login_bg_level_exceeded` is on, a `GamePlayer` sender that is over the limit gets an `ECSGameTimer` of `LoginCheckDelayMs` (1000 ms).
    - `MoveOutIfStillOver(player, region)`: the timer callback. It acts only if the player's `ObjectState` is `Active`, `Client.Player` is still this player, `Client.ClientState` is `Playing`, the region is unchanged and the character is still over the limit. Then it moves the character out and sends `OutgrownMessage(bracket, atBind)` to the system window. It returns 0, so the timer runs once.
    - `OnKeepTaken`: for `args is KeepEventArgs { Keep: { } keep }` (the sender is null) and `ShouldResetKeepLevel(keep.Region, keep.IsPortalKeep, keep.Level)`, it runs `keep.ChangeLevel(1)` and `keep.SaveIntoDatabase()`.
    - `Brackets()`: one `BattlegroundBracket` per region in `ClassicBattlegrounds.Regions` that has a row in `GameServer.KeepManager.GetBattleground(region)`, with the cap `GamePlayer.REALMPOINTS_FOR_LEVEL[MaxRealmLevel]` (0 when `MaxRealmLevel` is 0; an index past the table's end takes its last entry).
    - `OverLimit(player, out bracket)`: `ClassicBattlegrounds.IsOverLimit` with the account's privilege level (0 when the client has no account), the region, the level, the realm level and `Brackets()`.
    - `MoveOut(player)`: the bind point when `GoesToBind` says so and `player.MoveTo` succeeds, otherwise `GameServer.KeepManager.ExitBattleground(player)`. It returns true only for the bind point.

    `MoveOutIfStillOver` and `OverLimit` are additions to the plan's fixed interface (the timer callback and a helper shared by three callers); the fixed names are unchanged.
  - Log lines: at Info `Classic battlegrounds: Abermenai 15-19 up to 1L2, Thidranki 20-24 up to 1L3, Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9` (on the shipped worlds once Task 3's world fix has run; before it, upstream's rows). The errors and warnings all start with `Classic battlegrounds:`.
- Produces, upstream:
  - `OFTeleporters.cs`: each `case BattlegroundsID:` block is exactly `case BattlegroundsID:` (32 spaces), `PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);` and `break;` (36 spaces), with CRLF endings. The BOM and every other line stay.
  - `KeepManager.cs:786`: `case eRealm.Midgard: location = "Svasud Faste"; break;`
  - The twelve battleground quest files are gone. The `<Folder Include=...>` lines in `GameServer.csproj` stay.
- Produces, `deploy/tests/test_battlegrounds.py`: the names `HERE`, `DEPLOY`, `ROOT`, `SOURCE`, `GAME_SERVER`, `OF_TELEPORTERS`, `KEEP_MANAGER`, `BATTLEGROUND_QUESTS`, `PORTER_CALL`, `QUEST_CLASS_NAMES`, the helper `cs_files(top)` and the class `ClassicBattlegroundSourceTests` with `test_porter_blocks_call_the_fork`, `test_keep_manager_names_svasud_faste` and `test_battleground_quests_are_gone`. Task 3 adds `import sys`, `BIN`, `sys.path.insert(0, BIN)` and `import battlegrounds` to the header, and its classes after this one.
- Produces, docs: a `docs/fork/FORK.md` row ("Candidate: the "Svasud Faste" fix; the rest is fork-only") and a `## Battlegrounds` section in `client/README.md`, just before `## What happens at each launch`.

**Notes (checked against the source at the Task 1 commit; paths under `source/server/GameServer/` unless they say otherwise):**
- **The porter.** `OFTeleporter` (`scripts/teleporters/OFTeleporters.cs:64`) is in `namespace DOL.GS.Scripts` and has no `using DOL.GS.HearthDAoC;` (lines 1-8), so the call is written `HearthDAoC.ClassicBattlegroundsScript...`; `HearthDAoC` resolves through the enclosing `DOL.GS` namespace.
  - `CastTimerCallback` goes through every player within 500 units (`:196`) with `GameLocation PortLocation = null;` (`:198`) and reads the Mythical slot (`:199`). It removes the medallion and moves the player only when `PortLocation != null` (`:679-684`). So a null from `PorterDestination` keeps the medallion on.
  - Two timers share that callback (`:113-117`) and start 5 and 15 seconds after the cast (`:169-170`), hence "once per ceremony".
  - The blocks are Albion `:231-273`, Midgard `:390-431` and Hibernia `:547-588`, each from `case BattlegroundsID:` to the `}` line (32 spaces) before `case DarknessFallsID:`. The file is UTF-8 with a BOM and mixed line endings: 794 CRLF and 25 LF. The three blocks are all CRLF.
- **Saying the refusal.** `GameNPC.SayTo(GamePlayer target, eChatLoc loc, string message, bool announce = true)` (`gameobjects/GameNPC.cs:2670`) turns the porter to the player and, for `CL_ChatWindow`, sends `CT_Say` to the chat window (`:2687-2689`), formatted `{0} says, "{1}"` (`language/EN/GameObjects/GameNPC.txt:20`). Only the target sees it.
- **The refusal time.** `GameLiving.TempProperties` is a `PropertyCollection` (`gameobjects/GameLiving.cs:2488`). `GetProperty<T>(key, @default)` (`gameutils/PropertyCollection.cs:20`) returns the default when the key is missing or `value is not T` (`:43`); a boxed `long` matches `long?`, so `GetProperty<long?>(key, null)` gives the stored time or null. `SetProperty(key, value)` is at `:54`. `GameLoop.GameLoopTime` is a static `long` in milliseconds (`Managers/GameLoop/GameLoop.cs:24`).
- **Battleground rows.** `GameServer.KeepManager` (`GameServer.cs:189`) is an `IKeepManager`; `GetBattleground(ushort)` and `ExitBattleground(GamePlayer)` are at `keeps/IKeepManager.cs:58-59` and `keeps/KeepManager.cs:770-778` and `:780-796`. The rows are read once, when the Keep Manager starts (`GameServer.cs:447`, `KeepManager.cs:62`, `:765-768`), before the scripts' `ScriptEvent.Loaded` (`GameServer.cs:502`).
  - `DbBattleground.RegionID` is a `ushort`, `MinLevel`, `MaxLevel` and `MaxRealmLevel` are `byte`s (`source/server/CoreDatabase/Tables/DbBattleground.cs:20, 37, 54, 71`).
  - `GamePlayer.REALMPOINTS_FOR_LEVEL` (`gameobjects/GamePlayer.cs:3851`) is a `long[]` with entries for realm levels 0 to 130; entry 0 is 0. `RealmLevel` stops at its last entry (`:3704`).
- **`ExitBattleground` and the typo.** It moves Albion to the Teleport row "Castle Sauvage", Midgard to "Svasudheim Faste" (`:786`) and Hibernia to "Druim Ligen", and does nothing when the row is missing. The world's Midgard row is "Svasud Faste" (`atlas_mid_svasud_faste`), so today it never moves a Midgard character. Its `MoveTo` goes to regions 1, 100 or 200, never a battleground.
- **The character.** `GameObject.Realm` (`gameobjects/GameObject.cs:78`), `CurrentRegionID` (`:80-84`, with a setter) and `Level` (`:259`); `GamePlayer.RealmPoints` (`gameobjects/GamePlayer.cs:3527`), `RealmLevel` (`:3554`), the int properties `BindRegion`, `BindXpos`, `BindYpos`, `BindZpos` and `BindHeading` (`:605-650`) and `Client` (`:231`). `GameClient.Account` (`GameClient.cs:25`) and `DbAccount.PrivLevel` (a `uint`, `source/server/CoreDatabase/Tables/DbAccount.cs:137`); `ePrivLevel.Player` is 1 (`Enums/ePrivLevel.cs:30`).
- **Moving.** `GamePlayer.MoveTo(ushort regionID, int x, int y, int z, ushort heading)` (`gameobjects/GamePlayer.cs:7914`) returns false when the region is missing or zoning there isn't allowed (`:7929`), or when it has no zone at x, y. A move to another region sets `CurrentRegionID` at once (`:7966`), so "still in the region" after `MoveOut` means nothing moved the character. `WorldMgr.GetRegion(ushort)` is at `world/WorldMgr.cs:672` and `Region.GetZone(int, int)` at `world/Region.cs:1171`.
- **Events.** `GameEventMgr.AddHandler(DOLEvent, DOLEventHandler)` and `RemoveHandler` are at `events/GameEventMgr.cs:84` and `:154`. A handler that throws is logged and the chain goes on (`events/DOLEventHandlerChain.cs:87-93`).
  - `GamePlayerEvent.Quit` (`events/gameobjects/GamePlayerEvent.cs:81`) is raised with the player as sender by `GamePlayer.Quit` just before `Delete()` (`gameobjects/GamePlayer.cs:1101-1103`), whose `CleanupOnDisconnect` (`:913`) holds upstream's logout check (`:984-990`). That check tests `RealmLevel >= MaxRealmLevel` even when MaxRealmLevel is 0, and calls `ExitBattleground`.
  - `GamePlayerEvent.GameEntered` (`:53`) is raised with the player as sender inside PlayerInit (`packets/Client/168/PlayerInitRequestHandler.cs:41`), before "player init finished". Upstream's own login check (`:85-86`, `:154-173`) needs `CurrentRegion.IsRvR` (`:156`), which leaves out 250-253 (`world/Region.cs:304`).
  - `KeepEvent.KeepTaken` (`events/keep/KeepEvent.cs:54`) is raised with no sender and `new KeepEventArgs(this)` at the end of `AbstractGameKeep.Reset` (`keeps/AbstractGameKeep.cs:1047`, `:1115`). `KeepEventArgs` (`events/keep/KeepEventArgs.cs`, namespace `DOL.Events`) has `Keep` (`:59`).
  - Bots are `GameBot`, a `GameNPC` (`bots/GameBot.cs:32`), so `sender is GamePlayer` leaves them out.
- **The keep.** `AbstractGameKeep.ChangeLevel(byte)` (`keeps/AbstractGameKeep.cs:801`) re-levels the guards, patrols and doors and saves itself (`:832`); `SaveIntoDatabase()` is at `:553`, `IsPortalKeep` at `:98`, `Level` (byte) at `:270` and `Region` (ushort) at `:343`. Its guild message goes nowhere for a keep without a guild (`keeps/Managers/Guild Manager.cs:29-32`), as battleground keeps are (`allow_bg_claim` is False).
- **Timer.** `new ECSGameTimer(GameObject, ECSTimerCallback, int)` starts at once (`ECS-Services/TimerService.cs:96`); the callback returns the next interval, and 0 stops it (delegate at `:73`). A callback that throws gets its `GamePlayer` owner kicked to the character screen (`TimerService.cs:64-66`, `ECS-Services/GameServiceUtils.cs:37-39`, `:73-84`), so `MoveOutIfStillOver` catches everything.
- **States and messages.** `GameObject.eObjectState.Active` (`gameobjects/GameObject.cs:37`) and `ObjectState` (`:63`); `GameClient.eClientState.Playing`, `ClientState` (`GameClient.cs:36`) and `Player` (`:56`). `eChatType` and `eChatLoc` are in `DOL.GS.PacketHandler`. `Properties.TELEPORT_LOGIN_BG_LEVEL_EXCEEDED` is `serverproperty/ServerProperties.cs:1621` (True in the shipped worlds).
- **Script events and logger.** `[ScriptLoadedEvent]` and `[ScriptUnloadedEvent]` are found on public static methods of `GameServer.dll`, as for `SiStartChoiceScript`. The logger is `LoggerManager.Create(typeof(...))` from `DOL.Logging`, as there.
- **The quest files.** Each of the twelve declares one class of the same name (namespaces `DOL.GS.DailyQuest.Albion`, `.Hibernia`, `.Midgard`), and nothing else under `source/` names them. Other files keep those three namespaces alive, so no `using` breaks. They had no build warnings.

All commands run from the repository root, on branch `sub5-battlegrounds`, with Task 1 committed.

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

- [ ] **Step 1: Write the failing source checks**

Create `deploy/tests/test_battlegrounds.py`. It holds only the source checks for now; Task 3 adds the world-data tests to the same file:

```python
"""The classic battlegrounds: Abermenai 15-19, Thidranki 20-24, Murdaigean 25-29 and Caledonia 30-35.

ClassicBattlegroundSourceTests check the fork's small edits to upstream server code, so that an upstream
sync that brings the old code back fails CI: the frontier porter's battleground blocks call the fork,
KeepManager names the Midgard teleport "Svasud Faste", and Atlas's battleground daily quests stay deleted.
"""
import os
import re
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.dirname(HERE)
ROOT = os.path.dirname(DEPLOY)
SOURCE = os.path.join(ROOT, "source")
GAME_SERVER = os.path.join(SOURCE, "server", "GameServer")
OF_TELEPORTERS = os.path.join(GAME_SERVER, "scripts", "teleporters", "OFTeleporters.cs")
KEEP_MANAGER = os.path.join(GAME_SERVER, "keeps", "KeepManager.cs")
BATTLEGROUND_QUESTS = os.path.join(GAME_SERVER, "scripts", "quests", "BattlegroundQuests")

PORTER_CALL = "PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);"
# Atlas's daily quests for Caledonia 34-39 and Thidranki 20-24, whose scripts also made the Pazz NPCs.
QUEST_CLASS_NAMES = (
    "CaleKeepCaptureAlb", "CaleKeepCaptureHib", "CaleKeepCaptureMid",
    "CaleKillQuestAlb", "CaleKillQuestHib", "CaleKillQuestMid",
    "ThidKeepCaptureAlb", "ThidKeepCaptureHib", "ThidKeepCaptureMid",
    "ThidKillQuestAlb", "ThidKillQuestHib", "ThidKillQuestMid",
)


def cs_files(top):
    """Every .cs file under top, as paths relative to the repository root, sorted."""
    found = []
    for folder, _, names in os.walk(top):
        found.extend(os.path.relpath(os.path.join(folder, name), ROOT) for name in names if name.endswith(".cs"))
    return sorted(found)


class ClassicBattlegroundSourceTests(unittest.TestCase):
    def test_porter_blocks_call_the_fork(self):
        # The file starts with a BOM, and its line endings are mixed (CRLF and LF).
        with open(OF_TELEPORTERS, encoding="utf-8-sig") as f:
            text = f.read()
        lines = text.splitlines()
        starts = [i for i, line in enumerate(lines) if line.strip() == "case BattlegroundsID:"]
        self.assertEqual(len(starts), 3, "one battlegrounds block per realm")

        for start in starts:
            body = []
            for line in lines[start + 1:]:
                if line.strip().startswith("case "):
                    break
                if line.strip():
                    body.append(line.strip())
            self.assertEqual(body, [PORTER_CALL, "break;"], f"the block at line {start + 1}")

        # Atlas's caps: Thidranki under 7,125 realm points, Caledonia under 122,500.
        self.assertNotIn("7125", text)
        self.assertNotIn("122500", text)

    def test_keep_manager_names_svasud_faste(self):
        # ExitBattleground looks the realm's home portal keep up by TeleportID; the world's row is "Svasud Faste".
        with open(KEEP_MANAGER, encoding="utf-8") as f:
            text = f.read()
        midgard = [line.strip() for line in text.splitlines() if "case eRealm.Midgard: location =" in line]
        self.assertEqual(midgard, ['case eRealm.Midgard: location = "Svasud Faste"; break;'])
        self.assertNotIn("Svasudheim", text)

    def test_battleground_quests_are_gone(self):
        self.assertEqual(cs_files(BATTLEGROUND_QUESTS), [])

        # deploy/ is left out: the world fix names the classes as data, to delete their saved quests.
        names = re.compile(r"\b(?:" + "|".join(QUEST_CLASS_NAMES) + r")\b")
        naming = []
        for path in cs_files(SOURCE):
            with open(os.path.join(ROOT, path), encoding="utf-8", errors="replace") as f:
                if names.search(f.read()):
                    naming.append(path)
        self.assertEqual(naming, [])


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the source checks to verify they fail**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_battlegrounds.py -v; echo "rc=$?"`

Expected: all three fail against the untouched upstream files. The paths are shortened to `<clone>`, and the time varies:
```
test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone) ... FAIL
test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste) ... FAIL
test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork) ... FAIL

======================================================================
FAIL: test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone)
----------------------------------------------------------------------
Traceback (most recent call last):
  File "<clone>/deploy/tests/test_battlegrounds.py", line 69, in test_battleground_quests_are_gone
    self.assertEqual(cs_files(BATTLEGROUND_QUESTS), [])
AssertionError: Lists differ: ['source/server/GameServer/scripts/quests/[1069 chars].cs'] != []

First list contains 12 additional elements.
First extra element 0:
'source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureAlb.cs'

Diff is 1157 characters long. Set self.maxDiff to None to see it.

======================================================================
FAIL: test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste)
----------------------------------------------------------------------
Traceback (most recent call last):
  File "<clone>/deploy/tests/test_battlegrounds.py", line 65, in test_keep_manager_names_svasud_faste
    self.assertEqual(midgard, ['case eRealm.Midgard: location = "Svasud Faste"; break;'])
AssertionError: Lists differ: ['case eRealm.Midgard: location = "Svasudheim Faste"; break;'] != ['case eRealm.Midgard: location = "Svasud Faste"; break;']

First differing element 0:
'case eRealm.Midgard: location = "Svasudheim Faste"; break;'
'case eRealm.Midgard: location = "Svasud Faste"; break;'

- ['case eRealm.Midgard: location = "Svasudheim Faste"; break;']
?                                          ----

+ ['case eRealm.Midgard: location = "Svasud Faste"; break;']

======================================================================
FAIL: test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork)
----------------------------------------------------------------------
Traceback (most recent call last):
  File "<clone>/deploy/tests/test_battlegrounds.py", line 54, in test_porter_blocks_call_the_fork
    self.assertEqual(body, [PORTER_CALL, "break;"], f"the block at line {start + 1}")
AssertionError: Lists differ: ['{', '// if (player.Level is >= 15 and <= [873 chars] '}'] != ['PortLocation = HearthDAoC.ClassicBattlegr[51 chars]ak;']

First differing element 0:
'{'
'PortLocation = HearthDAoC.ClassicBattlegr[40 chars]er);'

First list contains 37 additional elements.
First extra element 2:
'// {'

Diff is 1201 characters long. Set self.maxDiff to None to see it. : the block at line 231

----------------------------------------------------------------------
Ran 3 tests in 0.003s

FAILED (failures=3)
rc=1
```

- [ ] **Step 3: Make the three upstream edits**

First, the porter. A byte-level replace keeps the BOM and the CRLF endings, and it stops unless it finds exactly three blocks:

```bash
python3 - <<'EOF'
import re

path = "source/server/GameServer/scripts/teleporters/OFTeleporters.cs"
with open(path, "rb") as f:
    data = f.read()

# Each realm's block, from "case BattlegroundsID:" to the "}" (32 spaces) before "case DarknessFallsID:".
block = re.compile(rb"(?P<indent> {32})case BattlegroundsID:\r\n {32}\{\r\n.*?\r\n {32}\}\r\n(?= {32}case DarknessFallsID:)",
                   re.DOTALL)
call = (rb"\g<indent>case BattlegroundsID:\r\n"
        rb"\g<indent>    PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);\r\n"
        rb"\g<indent>    break;\r\n")
data, count = block.subn(call, data)
assert count == 3, count

with open(path, "wb") as f:
    f.write(data)
crlf = data.count(b"\r\n")
print("blocks:", count, "BOM:", data.startswith(b"\xef\xbb\xbf"), "CRLF:", crlf, "LF:", data.count(b"\n") - crlf)
EOF
```

Expected: the three blocks lost 118 CRLF lines (40 + 39 + 39), and the 25 LF lines are untouched:
```
blocks: 3 BOM: True CRLF: 676 LF: 25
```

Then the Midgard teleport name, and the twelve quest files. `sed` keeps the file's CRLF endings. `git rm -r` takes the whole `BattlegroundQuests` folder, which holds exactly the twelve files:

```bash
sed -i 's/"Svasudheim Faste"/"Svasud Faste"/' source/server/GameServer/keeps/KeepManager.cs
git rm -q -r source/server/GameServer/scripts/quests/BattlegroundQuests
```

Run:
```bash
grep -n -A2 'case BattlegroundsID:' source/server/GameServer/scripts/teleporters/OFTeleporters.cs | tr -d '\r'
grep -n 'Svasud' source/server/GameServer/keeps/KeepManager.cs | tr -d '\r\t'
git diff --stat
git status --short
file source/server/GameServer/scripts/teleporters/OFTeleporters.cs source/server/GameServer/keeps/KeepManager.cs
```

Expected: the three blocks are one call each. `KeepManager.cs:786` names "Svasud Faste"; line 683 is an upstream comment that already spelled it that way. `git diff --stat` counts only the two edited files (the deletions are already staged). Both files keep their encoding and line endings:
```
231:                                case BattlegroundsID:
232-                                    PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);
233-                                    break;
--
350:                                case BattlegroundsID:
351-                                    PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);
352-                                    break;
--
468:                                case BattlegroundsID:
469-                                    PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);
470-                                    break;
683:case 3: // Svasud Faste.
786:case eRealm.Midgard: location = "Svasud Faste"; break;
 source/server/GameServer/keeps/KeepManager.cs      |   2 +-
 .../scripts/teleporters/OFTeleporters.cs           | 124 +--------------------
 2 files changed, 4 insertions(+), 122 deletions(-)
 M source/server/GameServer/keeps/KeepManager.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureAlb.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureHib.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureMid.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKillQuestAlb.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKillQuestHib.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKillQuestMid.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKeepCaptureAlb.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKeepCaptureHib.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKeepCaptureMid.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKillQuestAlb.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKillQuestHib.cs
D  source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKillQuestMid.cs
 M source/server/GameServer/scripts/teleporters/OFTeleporters.cs
?? deploy/tests/test_battlegrounds.py
source/server/GameServer/scripts/teleporters/OFTeleporters.cs: Unicode text, UTF-8 (with BOM) text, with CRLF, LF line terminators
source/server/GameServer/keeps/KeepManager.cs:                 ASCII text, with CRLF line terminators
```

The server doesn't build again until Step 5 adds `ClassicBattlegroundsScript`; Step 6 builds it.

- [ ] **Step 4: Run the source checks to verify they pass**

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_battlegrounds.py -v`

Expected (the time varies):
```
test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone) ... ok
test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste) ... ok
test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork) ... ok

----------------------------------------------------------------------
Ran 3 tests in 0.572s

OK
```

- [ ] **Step 5: Write the game wiring**

The wiring needs a running server and a client, so it has no unit test. Its decisions are the ones Task 1 tests, and the owner checks it in game (Task 5). Here it must build cleanly (Step 6) and use only the calls listed in the notes (Step 8).

Create `source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs`:

```csharp
using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the game wiring of the classic battlegrounds. The frontier porter (OFTeleporter) asks
// PorterDestination where a character wearing the battlegrounds medallion goes; a character over its
// battleground's limit is moved out at logout and, after a link death or a crash, a moment after its next
// login; and a captured central keep goes back to level 1. ClassicBattlegrounds makes every decision; this
// class reads the battleground rows and the character's state and carries out the outcome.
public static class ClassicBattlegroundsScript
{
    private static readonly Logger Log = LoggerManager.Create(typeof(ClassicBattlegroundsScript));

    // Called by OFTeleporter's CastTimerCallback for a player wearing battlegrounds_necklace. Returns the
    // destination (the caller removes the medallion and moves the player), or null (the medallion stays).
    // The callback runs twice per ceremony, so a refusal is said only when ShouldSayRefusal allows it, and
    // the time is stored only when it is said.
    public static GameLocation PorterDestination(GameNPC porter, GamePlayer player)
    {
        try
        {
            PorterDecision decision = ClassicBattlegrounds.Porter(player.Level, player.RealmLevel, player.RealmPoints,
                (int)player.Realm, Brackets());

            if (decision.Refusal != null)
            {
                long now = GameLoop.GameLoopTime;
                long? lastSaid = player.TempProperties.GetProperty<long?>(ClassicBattlegrounds.RefusedAtKey, null);

                if (ClassicBattlegrounds.ShouldSayRefusal(now, lastSaid))
                {
                    porter.SayTo(player, eChatLoc.CL_ChatWindow, decision.Refusal);
                    player.TempProperties.SetProperty(ClassicBattlegrounds.RefusedAtKey, now);
                }

                return null;
            }

            BattlegroundLanding landing = decision.Destination;
            return landing == null
                ? null
                : new GameLocation(landing.Name, landing.Region, landing.X, landing.Y, landing.Z, landing.Heading);
        }
        catch (Exception ex)
        {
            // The porter goes through every player in range; one player's error must not stop the others.
            Log.Error($"Classic battlegrounds: the porter could not decide for {player?.Name}", ex);
            return null;
        }
    }

    // The Keep Manager loads the battleground rows before the scripts' Loaded event (GameServer.Start).
    [ScriptLoadedEvent]
    public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
    {
        try
        {
            Log.Info(ClassicBattlegrounds.Summary(Brackets()));
        }
        catch (Exception ex)
        {
            Log.Warn("Classic battlegrounds: the battleground rows could not be read: " + ex.Message);
        }

        GameEventMgr.AddHandler(GamePlayerEvent.Quit, OnQuit);
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, OnGameEntered);
        GameEventMgr.AddHandler(KeepEvent.KeepTaken, OnKeepTaken);
    }

    [ScriptUnloadedEvent]
    public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, OnQuit);
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, OnGameEntered);
        GameEventMgr.RemoveHandler(KeepEvent.KeepTaken, OnKeepTaken);
    }

    // GamePlayer.Quit sends this before Delete() runs upstream's own logout check (CleanupOnDisconnect),
    // which then finds the character outside the battleground. After /quit the character is saved, so the
    // move sticks; after a link death it was saved before, and the login check moves it instead. Bots are
    // GameBot, a GameNPC, so they never get here. A handler that throws is logged by the event chain.
    private static void OnQuit(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player && OverLimit(player, out _))
            MoveOut(player);
    }

    // Upstream's own login check skips the battlegrounds (Region.IsRvR leaves out 250-253), so this one runs
    // while upstream's switch for it, teleport_login_bg_level_exceeded, is on. GameEntered fires inside
    // PlayerInit, before "player init finished", hence the delay.
    private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player
            || !Properties.TELEPORT_LOGIN_BG_LEVEL_EXCEEDED
            || !OverLimit(player, out _))
            return;

        ushort region = player.CurrentRegionID;
        new ECSGameTimer(player, timer => MoveOutIfStillOver(player, region), ClassicBattlegrounds.LoginCheckDelayMs);
    }

    // Only if the player is still in the game on the same client, still in that battleground and still over
    // its limit.
    private static int MoveOutIfStillOver(GamePlayer player, ushort region)
    {
        try
        {
            GameClient client = player.Client;
            if (player.ObjectState != GameObject.eObjectState.Active
                || client.Player != player
                || client.ClientState != GameClient.eClientState.Playing
                || player.CurrentRegionID != region
                || !OverLimit(player, out BattlegroundBracket bracket))
                return 0;

            bool atBind = MoveOut(player);

            // MoveOut only sends a character outside 250-253, so one still here was moved nowhere (the bind
            // point move failed and ExitBattleground found no Teleport row): no message, only a warning.
            if (player.CurrentRegionID == region)
                Log.Warn($"Classic battlegrounds: could not move {player.Name} out of region {region}");
            else
                player.Out.SendMessage(ClassicBattlegrounds.OutgrownMessage(bracket, atBind), eChatType.CT_System,
                    eChatLoc.CL_SystemWindow);
        }
        catch (Exception ex)
        {
            // A timer that throws sends its owner to the character screen, so this one only logs.
            Log.Error($"Classic battlegrounds: could not move {player.Name} out of the battleground", ex);
        }

        return 0;
    }

    // A capture resets the keep to starting_keep_level (AbstractGameKeep.Reset) and then raises this, with no
    // sender. A battleground's central keep goes back to level 1, so its guards keep their levels.
    private static void OnKeepTaken(DOLEvent e, object sender, EventArgs args)
    {
        if (args is not KeepEventArgs { Keep: { } keep }
            || !ClassicBattlegrounds.ShouldResetKeepLevel(keep.Region, keep.IsPortalKeep, keep.Level))
            return;

        keep.ChangeLevel(1);
        keep.SaveIntoDatabase();
    }

    // The battleground rows the Keep Manager loaded at start, in ClassicBattlegrounds.Regions order. The cap
    // is the realm points of MaxRealmLevel: REALMPOINTS_FOR_LEVEL[MaxRealmLevel], 0 without a cap. A
    // MaxRealmLevel past the table's end can't be reached, since RealmLevel stops at its last entry.
    private static List<BattlegroundBracket> Brackets()
    {
        List<BattlegroundBracket> brackets = new();
        long[] points = GamePlayer.REALMPOINTS_FOR_LEVEL;

        foreach (ushort region in ClassicBattlegrounds.Regions)
        {
            DbBattleground row = GameServer.KeepManager.GetBattleground(region);
            if (row == null)
                continue;

            long cap = row.MaxRealmLevel == 0 ? 0 : points[Math.Min((int)row.MaxRealmLevel, points.Length - 1)];
            brackets.Add(new BattlegroundBracket(region, ClassicBattlegrounds.Names[region], row.MinLevel,
                row.MaxLevel, row.MaxRealmLevel, cap));
        }

        return brackets;
    }

    // The character's state now. A character without an account counts as privilege level 0, never over.
    private static bool OverLimit(GamePlayer player, out BattlegroundBracket bracket)
    {
        return ClassicBattlegrounds.IsOverLimit(player.Client?.Account?.PrivLevel ?? 0, player.CurrentRegionID,
            player.Level, player.RealmLevel, Brackets(), out bracket);
    }

    // To the bind point when it is a real place outside the battlegrounds, otherwise (or when that move
    // fails) to the realm's home portal keep: Castle Sauvage, Svasud Faste or Druim Ligen. Returns whether
    // the character went to its bind point.
    private static bool MoveOut(GamePlayer player)
    {
        Region bindRegion = WorldMgr.GetRegion((ushort)player.BindRegion);
        bool hasZone = bindRegion?.GetZone(player.BindXpos, player.BindYpos) != null;

        if (ClassicBattlegrounds.GoesToBind(bindRegion != null, hasZone, player.BindRegion)
            && player.MoveTo((ushort)player.BindRegion, player.BindXpos, player.BindYpos, player.BindZpos,
                (ushort)player.BindHeading))
            return true;

        GameServer.KeepManager.ExitBattleground(player);
        return false;
    }
}
```

- [ ] **Step 6: Build the server as the image does**

`deploy/Dockerfile` builds the server with `dotnet build source/server/DOLLinux.sln -c Release`. `--no-incremental` recompiles every project, so the warning count is the full one.

Run: `dotnet build source/server/DOLLinux.sln -c Release --nologo --no-incremental 2>&1 | grep -E "scripts/hearthdaoc/|GameServer -> |Warning\(s\)|Error\(s\)"; echo "rc=${PIPESTATUS[0]}"`

The build took 20 to 25 s here; the time varies.

Expected: no warning or error line names a file under `scripts/hearthdaoc/`. All the warnings are upstream's: the Task 1 commit alone gives the same 639, with the same codes on the same lines, except that two `CS0618` warnings in `OFTeleporters.cs` move up 118 lines (from `:795` and `:797` to `:677` and `:679`). The deleted quest files had none. The count may differ with another SDK. The path is shortened:
```
  GameServer -> <clone>/source/server/Release/lib/GameServer.dll
    639 Warning(s)
    0 Error(s)
rc=0
```

- [ ] **Step 7: Run the tests again**

Run the CI step's own command, with the three fork test classes:

Run: `cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml && dotnet test source/server/Tests/Tests.csproj --nologo --filter "FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds" 2>&1 | tail -1`

Expected: the same 121 cases as at the end of Task 1 (3 + 52 + 66); the duration varies:
```
Passed!  - Failed:     0, Passed:   121, Skipped:     0, Total:   121, Duration: 68 ms - Tests.dll (net10.0)
```

Then the source checks once more. The builds have now written their output under `source/server/` (`build/`, `Release/`), and the check that walks `source/` for the quest class names still passes:

Run: `python3 -m unittest discover -s deploy/tests -t deploy -p test_battlegrounds.py 2>&1 | tail -1`

Expected:
```
OK
```

- [ ] **Step 8: Static checks**

First, list every upstream member the script uses. Comments are stripped first, so only code counts:

Run: `sed 's#//.*##' source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs | grep -oE "\b(GameEventMgr|GamePlayerEvent|KeepEvent|GameLoop|Properties|WorldMgr|GameServer\.KeepManager|GamePlayer|GameObject\.eObjectState|GameClient\.eClientState|player\.Out|player\.TempProperties|player\.Client\?\.Account\?|player|client|porter|keep|row|bindRegion\?)\.\w+|new [A-Z]\w+|KeepEventArgs \{ Keep" | LC_ALL=C sort | uniq -c`

Expected: only the members in the notes above (plus `new BattlegroundBracket` from Task 1). `player?.Name` in the porter's error log is the same `Name` as `player.Name`:
```
      1 GameClient.eClientState.Playing
      3 GameEventMgr.AddHandler
      3 GameEventMgr.RemoveHandler
      1 GameLoop.GameLoopTime
      1 GameObject.eObjectState.Active
      1 GamePlayer.REALMPOINTS_FOR_LEVEL
      2 GamePlayerEvent.GameEntered
      2 GamePlayerEvent.Quit
      1 GameServer.KeepManager.ExitBattleground
      1 GameServer.KeepManager.GetBattleground
      2 KeepEvent.KeepTaken
      1 KeepEventArgs { Keep
      1 Properties.TELEPORT_LOGIN_BG_LEVEL_EXCEEDED
      1 WorldMgr.GetRegion
      1 bindRegion?.GetZone
      1 client.ClientState
      1 client.Player
      1 keep.ChangeLevel
      1 keep.IsPortalKeep
      1 keep.Level
      1 keep.Region
      1 keep.SaveIntoDatabase
      1 new BattlegroundBracket
      1 new ECSGameTimer
      1 new GameLocation
      1 player.BindHeading
      3 player.BindRegion
      2 player.BindXpos
      2 player.BindYpos
      1 player.BindZpos
      1 player.Client
      1 player.Client?.Account?.PrivLevel
      4 player.CurrentRegionID
      2 player.Level
      1 player.MoveTo
      2 player.Name
      1 player.ObjectState
      1 player.Out.SendMessage
      1 player.Realm
      2 player.RealmLevel
      1 player.RealmPoints
      1 player.TempProperties.GetProperty
      1 player.TempProperties.SetProperty
      1 porter.SayTo
      1 row.MaxLevel
      3 row.MaxRealmLevel
      1 row.MinLevel
```

Last, stage this task's server change and its test, and list what the branch changes under `source/server`. `0a4e730` is the `main` commit this branch starts from:

```bash
git add source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs source/server/GameServer/scripts/teleporters/OFTeleporters.cs source/server/GameServer/keeps/KeepManager.cs deploy/tests/test_battlegrounds.py
git diff --cached --name-status 0a4e730 -- source/server
file source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs
```

Expected: the two new fork files and Task 1's test (`A`), the two edited upstream files (`M`) and the twelve deleted quest files (`D`), nothing else; and the script is plain ASCII with LF line endings (`file` would add "with CRLF line terminators" otherwise):
```
M	source/server/GameServer/keeps/KeepManager.cs
A	source/server/GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs
A	source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureAlb.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureHib.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKeepCaptureMid.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKillQuestAlb.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKillQuestHib.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Caledonia/CaleKillQuestMid.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKeepCaptureAlb.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKeepCaptureHib.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKeepCaptureMid.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKillQuestAlb.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKillQuestHib.cs
D	source/server/GameServer/scripts/quests/BattlegroundQuests/Thidranki/ThidKillQuestMid.cs
M	source/server/GameServer/scripts/teleporters/OFTeleporters.cs
A	source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs
source/server/GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs: ASCII text
```

- [ ] **Step 9: Commit the server change**

```bash
git commit -q -m "feat(server): wire the classic battlegrounds into the porter and the game

ClassicBattlegroundsScript wires ClassicBattlegrounds into the game. The
frontier porter's three battleground blocks in OFTeleporters.cs now call
PorterDestination, which reads the battleground rows the Keep Manager
loaded, sends a character to its level's battleground while its realm
level is under that row's cap, and otherwise says why, once per ceremony
(the time of the last refusal said is kept in TempProperties). The
medallion stays on a refusal. Handlers for GamePlayerEvent.Quit and, while
teleport_login_bg_level_exceeded is on, GamePlayerEvent.GameEntered (one
second later) move an over-limit character to its bind point, or to its
realm's home portal keep when the bind point is missing, zoneless or in a
battleground; the login move also says why. KeepEvent.KeepTaken sets a
captured central keep back to level 1, so its guards keep their levels.
The load log has one summary line.

KeepManager.ExitBattleground looked for the teleport Svasudheim Faste, but
the world's row is Svasud Faste, so Midgard characters were never moved.
Atlas's battleground daily quests (Caledonia 34-39, Thidranki 20-24) are
deleted with the scripts that made their Pazz NPCs. Source checks in
deploy/tests/test_battlegrounds.py fail if an upstream sync brings any of
it back.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline -1 && git status --short --untracked-files=all
```

Expected: the new commit (the hash differs). Nothing else is changed yet, so the status prints nothing:
```
5f4b137 feat(server): wire the classic battlegrounds into the porter and the game
```

- [ ] **Step 10: Document the change**

In `docs/fork/FORK.md`, add this row to the server-code changes table, right after the Shrouded Isles start choice row (the table's last row, just above `## Client patches`). It is one line:

```markdown
| Classic battlegrounds: porter levels and caps, over-limit moves, keep level after a capture | `GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs` (the decisions), `GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs` (the porter hook and the game wiring), test `Tests/UnitTests/UT_ClassicBattlegrounds.cs`, source checks `deploy/tests/test_battlegrounds.py`. Upstream files touched: `GameServer/scripts/teleporters/OFTeleporters.cs` (three blocks), `GameServer/keeps/KeepManager.cs` (one word), and the twelve battleground quest files in `GameServer/scripts/quests/BattlegroundQuests/` (Thidranki and Caledonia), deleted | The four classic battlegrounds for levels 15-35 with their realm rank caps, the porter's reasons, over-limit characters at their bind point, no Atlas daily quests (#76, [spec](specs/2026-10-07-classic-battlegrounds-design.md)) | Candidate: the "Svasud Faste" fix; the rest is fork-only |
```

In `client/README.md`, add this section just before `## What happens at each launch`, after the paragraph that ends "unless the server has turned it off.". Leave one blank line before and after it:

```markdown
## Battlegrounds

The four battlegrounds work as in the classic game. Get the free **Battlegrounds Medallion of
Passage** from your realm's medallion merchant (Sall Fadri at Castle Sauvage, Gwulla at Svasud Faste,
Araisa at Druim Ligen), wear it in the Mythical slot and stand by the frontier porter (Master Visur,
Stor Gothi Annark or Glasny) when it casts, about every two minutes. The porter sends you to the
battleground for your level, beside your realm's portal keep there, and the medallion is used up:

| Battleground | Levels | Highest realm rank |
|---|---|---|
| Abermenai | 15 to 19 | 1L2 (under 125 realm points) |
| Thidranki | 20 to 24 | 1L3 (under 350) |
| Murdaigean | 25 to 29 | 1L5 (under 1,375) |
| Caledonia | 30 to 35 | 1L9 (under 7,125) |

If your level or realm points don't fit, the porter says why in the chat window and you keep the
medallion. Each battleground has a portal keep for each realm and a central keep with guards and a
lord; kill the lord to take the keep for your realm.

Going past a limit inside changes nothing at once: you may stay until you log out or die. You are then
at your bind point (or at your realm's home portal keep, if your bind point is in a battleground), and
the porter won't send you back. A character left inside after a lost connection is moved a moment
after its next login, with a message saying why. No battleground has a zone experience bonus.
```

Run: `git diff --stat`

Expected:
```
 client/README.md  | 24 ++++++++++++++++++++++++
 docs/fork/FORK.md |  1 +
 2 files changed, 25 insertions(+)
```

- [ ] **Step 11: Commit the docs**

```bash
git add docs/fork/FORK.md client/README.md
git commit -q -m "docs(fork): list the classic battleground server changes and tell players

FORK.md lists the new server files and the upstream files the classic
battlegrounds touch: the porter's three battleground blocks, one word in
KeepManager and the twelve deleted battleground quest files (the Svasud
Faste fix is an upstream candidate). The player guide explains the
medallion and the porter, the four level ranges and realm rank caps, what
happens once over a limit, and that there is no zone experience bonus.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git log --oneline -3 && git status --short --untracked-files=all
```

Expected: this task's two commits on top of Task 1's (the hashes differ), and a clean tree:
```
200e712 docs(fork): list the classic battleground server changes and tell players
5f4b137 feat(server): wire the classic battlegrounds into the porter and the game
9cd5e98 feat(server): ClassicBattlegrounds decides the porter and battleground limits
```

---

### Task 3: World data, part 1: classic limits, names, keep levels and Atlas's leftovers

**Files:**
- Create: `deploy/bin/battlegrounds.py` (the world data fix: the framework and steps 1, 2, 3 and 6)
- Modify: `deploy/bin/world_fixes.py` (`import battlegrounds`, the call as the last statement inside `with conn:`, docstring item 4)
- Modify: `deploy/entrypoint.sh` (the comment above the `world_fixes.py` call)
- Modify: `docs/fork/FORK.md` ("What the fork changes": one row)
- Modify: `deploy/HANDOFF.md` (section "6. Day-to-day": a "Classic battlegrounds" paragraph)
- Test: `deploy/tests/test_battlegrounds.py` (Task 2's file gets the `import battlegrounds` line, a scratch world, `BattlegroundFixTests` and `BattlegroundShippedWorldTests`)
- Test: `deploy/tests/test_world_fixes.py` (`test_shipped_world` checks the full list of 7 lines)

**Interfaces:**
- Consumes, from Task 2:
  - `deploy/tests/test_battlegrounds.py`, which holds `ClassicBattlegroundSourceTests` (`test_porter_blocks_call_the_fork`, `test_keep_manager_names_svasud_faste`, `test_battleground_quests_are_gone`) and does not import `battlegrounds`;
  - Task 2's three upstream edits, so those source checks pass.

  Nothing from Task 1 is used. This task changes only Python, shell and docs, and runs no `dotnet` command.
- Consumes, from `deploy/bin/world_fixes.py`: `apply(db)` opens one connection and makes its three fixes inside one `with conn:` block (`:64-87`); `_now()` (`:93`) returns UTC time as `"%Y-%m-%d %H:%M:%S"`. From `deploy/tests/test_world_fixes.py`: `SCHEMA` (`:16`), the `CREATE TABLE`s of `ServerProperty` and `StartupLocation`.
- Produces, in `deploy/bin/battlegrounds.py`. The file is a module that `world_fixes.py` imports, so it has no shebang and stays mode 100644. The script's own directory is on `sys.path` when `world_fixes.py` runs, and the tests insert `deploy/bin`.
  - The constants `FIX_ID`, `MARKER_TABLE`, `ARCHIVE_TABLE`, `SAVEPOINT`, `NEEDED_TABLES`, `NOT_APPLIED`, `NAMES`, `ORDER`, `BATTLEGROUND_ROWS`, `QUEST_CLASSES`, `DUMMY_CLASSES`, `VOID_MERCHANT_CLASS` and `STRAY_WIZARD`, with the values in Global Constraints. The steps also use `XP_BONUS_ZONES` (252, 250), `KEEP_LEVELS` (KeepID → region, name, upstream and classic `BaseLevel`), `GATE_HEALTH` (`Door.InternalID` → upstream and new `Health`) and `IN_BATTLEGROUNDS` (`"Region BETWEEN 250 AND 253"`).
  - `apply(conn, now=None) -> list[str]`:
    1. If a table of `NEEDED_TABLES` is missing (case ignored), it returns `[]` and writes nothing.
    2. If the marker row is there, it returns `[]`.
    3. Otherwise it runs `SAVEPOINT classic_battlegrounds`, then `CREATE TABLE IF NOT EXISTS` for `fork_world_fixes` and for `fork_removed_mobs`. The archive table has the `Mob` columns from `PRAGMA table_info`, each with its declared type, plus `FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL`. Then it runs each of `STEPS`, inserts the marker row `(FIX_ID, now)` and runs `RELEASE`.
    4. On any exception it runs `ROLLBACK TO` and `RELEASE`, and returns only `NOT_APPLIED.format(str(e) or type(e).__name__)`.

    `now` defaults to `_now()`, which has the format of `world_fixes._now()`. Every row the fix changes gets `LastTimeRowUpdated = now`, and `AppliedUtc` and `RemovedUtc` are `now` too. Inside an open transaction (world_fixes.py's), nothing here commits. On a connection with no open transaction, the savepoint is the transaction, and its `RELEASE` commits it.
  - `STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step6_atlas_leftovers)`. Each step takes `(conn, now)` and returns its line, or `None` when it changed nothing. Task 4 inserts `_step4_portal_keep_guards` and `_step5_central_keeps` between `_step3_keep_levels` and `_step6_atlas_leftovers`.
  - Private helpers: `_archive(conn, now, where, params)` copies the matching `Mob` rows into `fork_removed_mobs`, naming only the columns both tables have, then deletes them and returns how many; `_count(n, one, many)`; `_now()`.
  - On the shipped world the result is these 4 lines, in this order. Task 4 puts its two lines between the third and the fourth.
    ```
    Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia
    Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia
    Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, 4 gates' health)
    Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, 3 Void Merchants, the stray Wizard)
    ```
- Produces, in `deploy/bin/world_fixes.py`: `changes.extend(battlegrounds.apply(conn, _now()))` is the last statement inside `with conn:`. So `world_fixes.py` prints the battleground lines after its own, commits its own fixes, and exits 0 even when the battleground fix fails.
- Produces, in `deploy/tests/test_battlegrounds.py`, for Task 4:
  - `make_world(path)` builds the scratch world from `WORLD_SCHEMA` and `SEED`. Its `Keep` table has every column of the real one, so Task 4 can add `Keep` rows without changing it.
  - The helpers `apply_fix(path, now=NOW)`, `query`, `execute`, `dump` and `marks`, and the constants `NOW`, `LATER`, `INJECTED` and `REMOVED`.
  - `BattlegroundFixTests.LINES` and `BattlegroundFixTests.FAILURES` (one injected failure per step). `test_a_failing_step_rolls_back_everything` first checks that `FAILURES` names exactly the steps in `STEPS`, so Task 4 adds a failure for each step it adds.
  - `BattlegroundShippedWorldTests.LINES`, and its `setUpClass`, which copies `HDC_TEST_WORLD` to `cls.db` in a temporary directory and applies the fix once.

All commands run from the repository root, on branch `sub5-battlegrounds`.

**Prerequisites:**
- Tasks 1 and 2 are done. Task 1 already excluded the build output; these lines do nothing when they are present:
  ```bash
  grep -qxF 'source/server/build/' .git/info/exclude || echo 'source/server/build/' >> .git/info/exclude
  grep -qxF 'source/server/CoreServer/config/serverconfig.xml' .git/info/exclude || echo 'source/server/CoreServer/config/serverconfig.xml' >> .git/info/exclude
  ```
- The real-world tests need the clean classic world, `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`, passed as `HDC_TEST_WORLD`. The tests only read it; they copy it into a temporary directory before applying anything. Without it, those tests are skipped.

- [ ] **Step 1: Write the failing tests**

Replace the whole of `deploy/tests/test_battlegrounds.py` with the content below. Task 2's source checks are kept exactly as Task 2 wrote them: `SOURCE`, `GAME_SERVER`, `OF_TELEPORTERS`, `KEEP_MANAGER`, `BATTLEGROUND_QUESTS`, `PORTER_CALL`, `QUEST_CLASS_NAMES`, `cs_files(top)` and `ClassicBattlegroundSourceTests` with its three tests and their messages. These parts are new:
- the docstring;
- the imports of `pathlib`, `shutil`, `sqlite3`, `subprocess`, `sys` and `tempfile`;
- `BIN` and its `sys.path` line, `import battlegrounds` and the `SCHEMA` import from `tests.test_world_fixes` (as `WORLD_FIXES_SCHEMA`);
- `TEST_WORLD`, and everything from `NOW` on.

```python
"""The classic battlegrounds (sub-project 5): the fork's upstream source edits and the world data fix.

An upstream sync that brings back the old porter blocks, "Svasudheim Faste" or Atlas's battleground
quest files fails the source checks. The world data tests run deploy/bin/battlegrounds.py on a scratch
world (make_world) and, with HDC_TEST_WORLD, on a copy of a clean classic world.
"""
import os
import pathlib
import re
import shutil
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

import battlegrounds  # noqa: E402
from tests.test_world_fixes import SCHEMA as WORLD_FIXES_SCHEMA  # noqa: E402

TEST_WORLD = os.environ.get("HDC_TEST_WORLD")
SOURCE = os.path.join(ROOT, "source")
GAME_SERVER = os.path.join(SOURCE, "server", "GameServer")
OF_TELEPORTERS = os.path.join(GAME_SERVER, "scripts", "teleporters", "OFTeleporters.cs")
KEEP_MANAGER = os.path.join(GAME_SERVER, "keeps", "KeepManager.cs")
BATTLEGROUND_QUESTS = os.path.join(GAME_SERVER, "scripts", "quests", "BattlegroundQuests")

PORTER_CALL = "PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);"
# Atlas's daily quests for Caledonia 34-39 and Thidranki 20-24, whose scripts also made the Pazz NPCs.
QUEST_CLASS_NAMES = (
    "CaleKeepCaptureAlb", "CaleKeepCaptureHib", "CaleKeepCaptureMid",
    "CaleKillQuestAlb", "CaleKillQuestHib", "CaleKillQuestMid",
    "ThidKeepCaptureAlb", "ThidKeepCaptureHib", "ThidKeepCaptureMid",
    "ThidKillQuestAlb", "ThidKillQuestHib", "ThidKillQuestMid",
)


def cs_files(top):
    """Every .cs file under top, as paths relative to the repository root, sorted."""
    found = []
    for folder, _, names in os.walk(top):
        found.extend(os.path.relpath(os.path.join(folder, name), ROOT) for name in names if name.endswith(".cs"))
    return sorted(found)


class ClassicBattlegroundSourceTests(unittest.TestCase):
    def test_porter_blocks_call_the_fork(self):
        # The file starts with a BOM, and its line endings are mixed (CRLF and LF).
        with open(OF_TELEPORTERS, encoding="utf-8-sig") as f:
            text = f.read()
        lines = text.splitlines()
        starts = [i for i, line in enumerate(lines) if line.strip() == "case BattlegroundsID:"]
        self.assertEqual(len(starts), 3, "one battlegrounds block per realm")

        for start in starts:
            body = []
            for line in lines[start + 1:]:
                if line.strip().startswith("case "):
                    break
                if line.strip():
                    body.append(line.strip())
            self.assertEqual(body, [PORTER_CALL, "break;"], f"the block at line {start + 1}")

        # Atlas's caps: Thidranki under 7,125 realm points, Caledonia under 122,500.
        self.assertNotIn("7125", text)
        self.assertNotIn("122500", text)

    def test_keep_manager_names_svasud_faste(self):
        # ExitBattleground looks the realm's home portal keep up by TeleportID; the world's row is "Svasud Faste".
        with open(KEEP_MANAGER, encoding="utf-8") as f:
            text = f.read()
        midgard = [line.strip() for line in text.splitlines() if "case eRealm.Midgard: location =" in line]
        self.assertEqual(midgard, ['case eRealm.Midgard: location = "Svasud Faste"; break;'])
        self.assertNotIn("Svasudheim", text)

    def test_battleground_quests_are_gone(self):
        self.assertEqual(cs_files(BATTLEGROUND_QUESTS), [])

        # deploy/ is left out: the world fix names the classes as data, to delete their saved quests.
        names = re.compile(r"\b(?:" + "|".join(QUEST_CLASS_NAMES) + r")\b")
        naming = []
        for path in cs_files(SOURCE):
            with open(os.path.join(ROOT, path), encoding="utf-8", errors="replace") as f:
                if names.search(f.read()):
                    naming.append(path)
        self.assertEqual(naming, [])


NOW = "2026-10-07 12:00:00"
LATER = "2026-10-08 12:00:00"
INJECTED = "CREATE TRIGGER injected {} BEGIN SELECT RAISE(ABORT, 'injected failure'); END"

# The scratch world: the tables battlegrounds.py needs, trimmed to the columns it uses (Keep in full),
# with rows copied from the clean classic world (clean-classic-0.34.db) unless a comment says otherwise.
WORLD_SCHEMA = [
    "CREATE TABLE Battleground (RegionID INT NOT NULL DEFAULT 0, MinLevel INT NOT NULL DEFAULT 0, "
    "MaxLevel INT NOT NULL DEFAULT 0, MaxRealmLevel INT NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Battleground_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Battleground_ID))",
    "CREATE TABLE Zones (ZoneID INT NOT NULL DEFAULT 0, RegionID INT NOT NULL DEFAULT 0, "
    "Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, Experience INT NOT NULL DEFAULT 0, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (ZoneID))",
    "CREATE TABLE Regions (RegionID INT NOT NULL DEFAULT 0, Description TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', PRIMARY KEY (RegionID))",
    "CREATE TABLE Keep (KeepID INT NOT NULL DEFAULT 0, Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, "
    "Region INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, "
    "Heading INT NOT NULL DEFAULT 0, Realm INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, "
    "ClaimedGuildName TEXT DEFAULT NULL COLLATE NOCASE, AlbionDifficultyLevel INT NOT NULL DEFAULT 0, "
    "MidgardDifficultyLevel INT NOT NULL DEFAULT 0, HiberniaDifficultyLevel INT NOT NULL DEFAULT 0, "
    "OriginalRealm INT NOT NULL DEFAULT 0, KeepType INT NOT NULL DEFAULT 0, BaseLevel INT NOT NULL DEFAULT 0, "
    "SkinType INT NOT NULL DEFAULT 0, CreateInfo VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Keep_ID VARCHAR(255) DEFAULT NULL COLLATE NOCASE, PRIMARY KEY (KeepID))",
    "CREATE TABLE Mob (ClassType TEXT DEFAULT NULL COLLATE NOCASE, Name VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "X INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, Z INT NOT NULL DEFAULT 0, Heading INT NOT NULL DEFAULT 0, "
    "Region INT NOT NULL DEFAULT 0, Model INT NOT NULL DEFAULT 0, Level INT NOT NULL DEFAULT 0, "
    "Realm INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Mob_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Mob_ID))",
    "CREATE TABLE Door (Z INT NOT NULL DEFAULT 0, Y INT NOT NULL DEFAULT 0, X INT NOT NULL DEFAULT 0, "
    "Heading INT NOT NULL DEFAULT 0, InternalID INT NOT NULL DEFAULT 0, Health INT NOT NULL DEFAULT 0, "
    "State INT NOT NULL DEFAULT 0, LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Door_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Door_ID))",
    "CREATE TABLE Quest (Name TEXT NOT NULL DEFAULT '' COLLATE NOCASE, Step INT NOT NULL DEFAULT 0, "
    "Character_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, "
    "LastTimeRowUpdated DATETIME NOT NULL DEFAULT '2000-01-01 00:00:00', "
    "Quest_ID VARCHAR(255) NOT NULL DEFAULT '' COLLATE NOCASE, PRIMARY KEY (Quest_ID))",
]
SEED = {
    "Battleground (RegionID, MinLevel, MaxLevel, MaxRealmLevel, Battleground_ID)": [
        (165, 45, 49, 45, "Cathal Valley (Level 45-49)"),
        (250, 30, 34, 25, "Caledonia (Level 34-39 - RR3L5)"),
        (251, 25, 29, 5, "Murdaigean (Level 25-29)"),
        (252, 20, 24, 10, "Thidranki (Level 20-24 - RR2L0)"),
        (253, 15, 19, 2, "Abermenai (Level 15-19)"),
    ],
    "Zones (ZoneID, RegionID, Name, Experience, LastTimeRowUpdated)": [
        (250, 250, "Caledon", 50, "2017-01-08 18:51:19"),
        (251, 251, "Murdaigean", 0, "2017-01-08 18:51:19"),
        (252, 252, "Thidranki", 50, "2017-01-08 18:51:19"),
        (253, 253, "Abermenai", 0, "2017-01-08 18:51:19"),
    ],
    "Regions (RegionID, Description, LastTimeRowUpdated)": [
        (250, "Caledon", "2017-01-08 18:50:36"),
        (251, "Murdaigean", "2017-01-08 18:50:36"),
        (252, "Thidranki", "2017-01-08 18:50:36"),
        (253, "Abermenai", "2017-01-08 18:50:36"),
    ],
    "Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
    "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, CreateInfo, "
    "LastTimeRowUpdated, Keep_ID)": [
        (11, "Thidranki Faste", 252, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 26, 0, "Atlas BG",
         "2022-10-09 21:09:42", "11"),
        (31, "Caer Caledon", 250, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 46, 0, "Atlas BG",
         "2022-08-18 17:36:33", "31"),
    ],
    "Door (Z, Y, X, Heading, InternalID, Health, State, LastTimeRowUpdated, Door_ID)": [
        (3783, 39237, 32673, 2665, 250000301, 9200, 1, "2023-06-15 11:03:20", "55c128b4-5410-49a3-b7de-cf288dd244ec"),
        (3914, 37634, 33192, 1599, 250000302, 9200, 1, "2023-06-15 11:03:20", "cbad55f4-2e57-4533-b083-a92357043c6a"),
        (3720, 38275, 34333, 1024, 252000301, 5200, 1, "2023-06-15 11:03:20", "2b95f0c0-f9a8-493d-ac4f-11d89d0809e7"),
        (3720, 38180, 32654, 1030, 252000302, 5200, 1, "2023-06-15 11:03:20", "91ce710c-043d-4c17-aaad-26b5046df3d0"),
    ],
    "Mob (ClassType, Name, X, Y, Z, Heading, Region, Model, Level, Realm, LastTimeRowUpdated, Mob_ID)": [
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 18826, 17862, 4320, 447, 252, 34, 24, 0, "2022-08-16 17:54:56",
         "08cf170a-3774-42c2-9816-934f78d5bd4e"),
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 54584, 24735, 4320, 1175, 252, 34, 24, 0, "2022-08-16 17:53:38",
         "120a1ccf-94e8-4420-bc72-05ec20390e44"),
        ("DOL.GS.HitbackDummy", "Hitback Dummy - Right Click to Reset", 37678, 53068, 3944, 1008, 252, 34, 24, 0,
         "2022-08-16 19:19:47", "853682fd-1de2-4100-adb5-02f05c8ed7d1"),
        # Atlas put Heal Dummies only outside the battlegrounds; this one (from region 1) is put in 250 so
        # the test covers the class.
        ("DOL.GS.HealDummy", "Heal Dummy", 583988, 476486, 2600, 4088, 250, 34, 50, 0, "2021-12-28 22:03:39",
         "7b98ecad-244c-4edc-a503-1f5def9935e3"),
        # A dummy outside the battlegrounds stays.
        ("DOL.GS.DPSDummy", "Total: 0 DPS: 0", 584452, 476256, 2600, 4, 1, 34, 10, 0, "2021-12-28 22:02:30",
         "125d80ca-f7b0-4b13-8340-d14d1c306a94"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 53441, 25129, 4312, 2617, 252, 2212, 75, 2,
         "2022-08-12 00:15:42", "27d30f96-e238-482d-b359-fc5387589108"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 36776, 52400, 3944, 3072, 250, 2212, 75, 1,
         "2022-08-12 04:56:01", "2ae9038a-72f0-4400-bbb1-53574dd5dfe9"),
        ("DOL.GS.Scripts.RPTradeInMerchant", "Void Merchant", 18933, 18185, 4320, 976, 252, 2212, 75, 3,
         "2022-08-12 00:15:11", "ecb08ffb-cf86-47f1-a53b-28581a48666b"),
        ("DOL.GS.Keeps.GuardStaticCaster", "Wizard", 33185, 37386, 3722, 2005, 250, 32, 48, 1, "2026-04-05 05:21:17",
         "caledon-guard-25"),
        # A Caer Caledon guard stays.
        ("DOL.GS.Keeps.GuardStaticCaster", "Renegade Runemaster", 32475, 38015, 4106, 1539, 250, 507, 38, 0,
         "2022-06-21 20:04:28", "caledon-guard-23"),
    ],
    # The clean world has no Quest rows; these stand for characters that took Atlas's daily quests.
    "Quest (Name, Step, Character_ID, Quest_ID)": [
        ("DOL.GS.DailyQuest.Albion.ThidKillQuestAlb", 1, "char-alb", "quest-thid"),
        ("DOL.GS.DailyQuest.Hibernia.CaleKillQuestMid", 2, "char-mid", "quest-cale"),
        ("DOL.GS.DailyQuest.Hibernia.CaptureKeepQuestHib", 1, "char-hib", "quest-frontier"),  # not a battleground quest
    ],
}
REMOVED = ("08cf170a-3774-42c2-9816-934f78d5bd4e", "120a1ccf-94e8-4420-bc72-05ec20390e44",
           "27d30f96-e238-482d-b359-fc5387589108", "2ae9038a-72f0-4400-bbb1-53574dd5dfe9",
           "7b98ecad-244c-4edc-a503-1f5def9935e3", "853682fd-1de2-4100-adb5-02f05c8ed7d1",
           "caledon-guard-25", "ecb08ffb-cf86-47f1-a53b-28581a48666b")


def query(path, sql, params=()):
    conn = sqlite3.connect(path)
    try:
        return conn.execute(sql, params).fetchall()
    finally:
        conn.close()


def execute(path, *statements):
    conn = sqlite3.connect(path)
    try:
        with conn:
            for statement in statements:
                conn.execute(statement)
    finally:
        conn.close()


def dump(path):
    conn = sqlite3.connect(path)
    try:
        return list(conn.iterdump())
    finally:
        conn.close()


def make_world(path):
    execute(path, *WORLD_SCHEMA)
    conn = sqlite3.connect(path)
    try:
        with conn:
            for table, rows in SEED.items():
                conn.executemany(f"INSERT INTO {table} VALUES ({', '.join('?' * len(rows[0]))})", rows)
    finally:
        conn.close()


def apply_fix(path, now=NOW):
    """battlegrounds.apply inside a transaction, as world_fixes.py calls it."""
    conn = sqlite3.connect(path)
    try:
        with conn:
            return battlegrounds.apply(conn, now)
    finally:
        conn.close()


def marks(n):
    return ", ".join("?" * n)


class BattlegroundFixTests(unittest.TestCase):
    LINES = [
        "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
        "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
        "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
        "4 gates' health)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
        "3 Void Merchants, the stray Wizard); 2 saved battleground daily quests deleted",
    ]
    # Where each step is made to fail: on the last statement it runs, so the steps before it, and that
    # step's own earlier statements, have already changed rows.
    FAILURES = {
        "_step1_battleground_rows": "BEFORE UPDATE ON Battleground WHEN OLD.RegionID = 250",
        "_step2_names_and_xp": "BEFORE UPDATE OF Experience ON Zones WHEN OLD.ZoneID = 250",
        "_step3_keep_levels": "BEFORE UPDATE ON Door WHEN OLD.InternalID = 250000302",
        "_step6_atlas_leftovers": "BEFORE DELETE ON Quest",
    }

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = self.world("world.db")

    def tearDown(self):
        self.tmp.cleanup()

    def world(self, name):
        path = os.path.join(self.tmp.name, name)
        make_world(path)
        return path

    def q(self, sql, params=()):
        return query(self.db, sql, params)

    def run_world_fixes(self):
        return subprocess.run([sys.executable, os.path.join(BIN, "world_fixes.py"), "--db", self.db],
                              capture_output=True, text=True)

    def test_each_step_changes_upstream_values_and_reports(self):
        execute(self.db, "UPDATE Keep SET Level=4 WHERE KeepID=31")  # a capture on this world left it at 4
        self.assertEqual(apply_fix(self.db), [
            "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
            "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
            "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
            "Caer Caledon back to level 1, 4 gates' health)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
            "3 Void Merchants, the stray Wizard); 2 saved battleground daily quests deleted",
        ])
        self.assertEqual(self.q("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel, LastTimeRowUpdated "
                                "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45, "2000-01-01 00:00:00"),
            (250, "Caledonia (Level 30-35 - RR1L9)", 30, 35, 10, NOW),
            (251, "Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6, NOW),
            (252, "Thidranki (Level 20-24 - RR1L3)", 20, 24, 4, NOW),
            (253, "Abermenai (Level 15-19 - RR1L2)", 15, 19, 3, NOW),
        ])
        self.assertEqual(self.q("SELECT ZoneID, Name, Experience, LastTimeRowUpdated FROM Zones ORDER BY ZoneID"), [
            (250, "Caledonia", 0, NOW), (251, "Murdaigean", 0, "2017-01-08 18:51:19"),
            (252, "Thidranki", 0, NOW), (253, "Abermenai", 0, "2017-01-08 18:51:19"),
        ])
        self.assertEqual(self.q("SELECT RegionID, Description, LastTimeRowUpdated FROM Regions ORDER BY RegionID"), [
            (250, "Caledonia", NOW), (251, "Murdaigean", "2017-01-08 18:50:36"),
            (252, "Thidranki", "2017-01-08 18:50:36"), (253, "Abermenai", "2017-01-08 18:50:36"),
        ])
        self.assertEqual(self.q("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep ORDER BY KeepID"),
                         [(11, 24, 1, NOW), (31, 35, 1, NOW)])
        self.assertEqual(self.q("SELECT InternalID, Health, State, LastTimeRowUpdated FROM Door ORDER BY InternalID"), [
            (250000301, 7000, 1, NOW), (250000302, 7000, 1, NOW), (252000301, 4800, 1, NOW), (252000302, 4800, 1, NOW),
        ])
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"),
                         [("125d80ca-f7b0-4b13-8340-d14d1c306a94",), ("caledon-guard-23",)])
        self.assertEqual(self.q("SELECT Name FROM Quest"), [("DOL.GS.DailyQuest.Hibernia.CaptureKeepQuestHib",)])
        self.assertEqual(self.q("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", NOW)])

    def test_second_run_changes_nothing(self):
        self.assertEqual(apply_fix(self.db), self.LINES)
        before = dump(self.db)
        self.assertEqual(apply_fix(self.db, LATER), [])
        self.assertEqual(dump(self.db), before)

    def test_a_failing_step_rolls_back_everything(self):
        self.assertEqual([step.__name__ for step in battlegrounds.STEPS], list(self.FAILURES))
        for step, when in self.FAILURES.items():
            with self.subTest(step=step):
                db = self.world(f"{step}.db")
                execute(db, INJECTED.format(when))
                before = dump(db)
                self.assertEqual(apply_fix(db), [
                    "Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them"])
                self.assertEqual(dump(db), before)  # no change, no fork table, no marker
                execute(db, "DROP TRIGGER injected")
                self.assertEqual(apply_fix(db), self.LINES)  # so the next start tries again

    def test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails(self):
        execute(self.db, *WORLD_FIXES_SCHEMA,
                "INSERT INTO ServerProperty (Category, `Key`, Value) VALUES ('classes', 'disabled_classes', '20;33')",
                INJECTED.format("BEFORE UPDATE ON Keep"))
        r = self.run_world_fixes()
        self.assertEqual((r.returncode, r.stderr), (0, ""))
        self.assertEqual(r.stdout.splitlines(), [
            "Disciple (Necromancer's base class) enabled: disabled_classes 20;33 -> 33",
            "Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them",
        ])
        self.assertEqual(self.q("SELECT Value FROM ServerProperty WHERE `Key`='disabled_classes'"), [("33",)])
        self.assertEqual(self.q("SELECT name FROM sqlite_master WHERE name LIKE 'fork%'"), [])
        self.assertEqual(self.q("SELECT MaxRealmLevel FROM Battleground WHERE RegionID=252"), [(10,)])
        self.assertEqual(self.q("SELECT Name FROM Zones WHERE ZoneID=250"), [("Caledon",)])
        execute(self.db, "DROP TRIGGER injected")
        r = self.run_world_fixes()
        self.assertEqual((r.returncode, r.stderr, r.stdout.splitlines()), (0, "", self.LINES))

    def test_owner_values_are_kept(self):
        execute(self.db,
                "UPDATE Battleground SET MaxRealmLevel=7 WHERE RegionID=252",
                "UPDATE Zones SET Name='Caledonia Fields' WHERE ZoneID=250",
                "UPDATE Regions SET Description='Caledonia Fields' WHERE RegionID=250",
                "UPDATE Zones SET Experience=25 WHERE ZoneID=252",
                "UPDATE Keep SET BaseLevel=30 WHERE KeepID=11",
                "UPDATE Door SET Health=6000 WHERE InternalID=252000301",
                "UPDATE Mob SET Level=50 WHERE Mob_ID='caledon-guard-25'",
                # The owner removed all but one dummy and one Void Merchant; one saved quest is left.
                "DELETE FROM Mob WHERE Mob_ID IN ('08cf170a-3774-42c2-9816-934f78d5bd4e', "
                "'120a1ccf-94e8-4420-bc72-05ec20390e44', '853682fd-1de2-4100-adb5-02f05c8ed7d1', "
                "'27d30f96-e238-482d-b359-fc5387589108', 'ecb08ffb-cf86-47f1-a53b-28581a48666b')",
                "DELETE FROM Quest WHERE Quest_ID='quest-cale'")
        self.assertEqual(apply_fix(self.db), [
            "Battlegrounds: classic level and realm rank limits for Abermenai, Murdaigean, Caledonia",
            "Battlegrounds: no zone XP bonus in Caledonia",
            "Battlegrounds: keep levels for the ranges (Caer Caledon base level 35, 3 gates' health)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (1 training dummy, "
            "1 Void Merchant); 1 saved battleground daily quest deleted",
        ])
        self.assertEqual(self.q("SELECT Battleground_ID, MaxRealmLevel FROM Battleground WHERE RegionID=252"),
                         [("Thidranki (Level 20-24 - RR2L0)", 7)])
        self.assertEqual(self.q("SELECT Name, Experience FROM Zones WHERE ZoneID IN (250, 252) ORDER BY ZoneID"),
                         [("Caledonia Fields", 0), ("Thidranki", 25)])
        self.assertEqual(self.q("SELECT Description FROM Regions WHERE RegionID=250"), [("Caledonia Fields",)])
        self.assertEqual(self.q("SELECT BaseLevel FROM Keep WHERE KeepID=11"), [(30,)])
        self.assertEqual(self.q("SELECT Health FROM Door WHERE InternalID=252000301"), [(6000,)])
        self.assertEqual(self.q("SELECT Name, Level FROM Mob WHERE Mob_ID='caledon-guard-25'"), [("Wizard", 50)])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v1",)])

    def test_removed_rows_are_archived(self):
        mob = [(name, declared) for _, name, declared, *_ in self.q('PRAGMA table_info("Mob")')]
        names = ", ".join(name for name, _ in mob)
        rows = self.q(f"SELECT {names} FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))}) ORDER BY Mob_ID", REMOVED)
        self.assertEqual(len(rows), 8)
        apply_fix(self.db)
        self.assertEqual(self.q(f"SELECT Mob_ID FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))})", REMOVED), [])
        self.assertEqual(self.q(f"SELECT {names}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v1", NOW) for row in rows])
        self.assertEqual([(name, declared) for _, name, declared, *_ in self.q('PRAGMA table_info("fork_removed_mobs")')],
                         mob + [("FixId", "TEXT"), ("RemovedUtc", "TEXT")])

        # An archive table made before Mob gained a column (the server adds new columns to a world's tables
        # at start) still takes the rows, in the columns both tables have.
        db = self.world("older-archive.db")
        execute(db, "CREATE TABLE fork_removed_mobs (ClassType TEXT, Name VARCHAR(255), Region INT, "
                    "Mob_ID VARCHAR(255), FixId TEXT NOT NULL, RemovedUtc TEXT NOT NULL)")
        older = "ClassType, Name, Region, Mob_ID"
        rows = query(db, f"SELECT {older} FROM Mob WHERE Mob_ID IN ({marks(len(REMOVED))}) ORDER BY Mob_ID", REMOVED)
        apply_fix(db)
        self.assertEqual(query(db, f"SELECT {older}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v1", NOW) for row in rows])

    def test_no_marker_without_the_needed_tables(self):
        for table in battlegrounds.NEEDED_TABLES:
            with self.subTest(table=table):
                db = self.world(f"no-{table}.db")
                execute(db, f"DROP TABLE {table}")
                before = dump(db)
                self.assertEqual(apply_fix(db), [])
                self.assertEqual(dump(db), before)


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class BattlegroundShippedWorldTests(unittest.TestCase):
    """The clean classic world before the fix (read-only), and a copy of it after one run."""

    LINES = [
        "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
        "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
        "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
        "4 gates' health)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
        "3 Void Merchants, the stray Wizard)",
    ]
    LEFTOVERS = ("(ClassType IN ('DOL.GS.DPSDummy', 'DOL.GS.HitbackDummy', 'DOL.GS.HealDummy', "
                 "'DOL.GS.Scripts.RPTradeInMerchant') AND Region BETWEEN 250 AND 253) OR Mob_ID='caledon-guard-25'")
    GATES = "InternalID IN (250000301, 250000302, 252000301, 252000302)"

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "world.db")
        shutil.copyfile(TEST_WORLD, cls.db)
        cls.lines = apply_fix(cls.db)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def before(self, sql, params=()):
        conn = sqlite3.connect(pathlib.Path(TEST_WORLD).resolve().as_uri() + "?mode=ro", uri=True)
        try:
            return conn.execute(sql, params).fetchall()
        finally:
            conn.close()

    def after(self, sql, params=()):
        return query(self.db, sql, params)

    def test_world_holds_what_the_steps_expect(self):
        self.assertEqual(self.before("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel "
                                     "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45),
            (250, "Caledonia (Level 34-39 - RR3L5)", 30, 34, 25),
            (251, "Murdaigean (Level 25-29)", 25, 29, 5),
            (252, "Thidranki (Level 20-24 - RR2L0)", 20, 24, 10),
            (253, "Abermenai (Level 15-19)", 15, 19, 2),
        ])
        self.assertEqual(self.before("SELECT ZoneID, RegionID, Name, Experience FROM Zones WHERE RegionID BETWEEN 250 AND 253 "
                                     "ORDER BY ZoneID"), [
            (250, 250, "Caledon", 50), (251, 251, "Murdaigean", 0), (252, 252, "Thidranki", 50), (253, 253, "Abermenai", 0),
        ])
        self.assertEqual(self.before("SELECT RegionID, Description FROM Regions WHERE RegionID BETWEEN 250 AND 253 "
                                     "ORDER BY RegionID"),
                         [(250, "Caledon"), (251, "Murdaigean"), (252, "Thidranki"), (253, "Abermenai")])
        self.assertEqual(self.before("SELECT KeepID, Name, Region, BaseLevel, Level FROM Keep WHERE KeepID IN (11, 31) "
                                     "ORDER BY KeepID"),
                         [(11, "Thidranki Faste", 252, 26, 1), (31, "Caer Caledon", 250, 46, 1)])
        self.assertEqual(self.before(f"SELECT InternalID, Health, State FROM Door WHERE {self.GATES} ORDER BY InternalID"), [
            (250000301, 9200, 1), (250000302, 9200, 1), (252000301, 5200, 1), (252000302, 5200, 1),
        ])
        self.assertEqual(self.before("SELECT ClassType, Region, COUNT(*) FROM Mob WHERE ClassType IN ('DOL.GS.DPSDummy', "
                                     "'DOL.GS.HitbackDummy', 'DOL.GS.HealDummy') AND Region BETWEEN 250 AND 253 "
                                     "GROUP BY ClassType, Region ORDER BY ClassType"),
                         [("DOL.GS.DPSDummy", 252, 9), ("DOL.GS.HitbackDummy", 252, 6)])
        self.assertEqual(self.before("SELECT Mob_ID, Region, Realm FROM Mob WHERE ClassType='DOL.GS.Scripts.RPTradeInMerchant' "
                                     "AND Region BETWEEN 250 AND 253 ORDER BY Mob_ID"), [
            ("27d30f96-e238-482d-b359-fc5387589108", 252, 2),
            ("2ae9038a-72f0-4400-bbb1-53574dd5dfe9", 250, 1),
            ("ecb08ffb-cf86-47f1-a53b-28581a48666b", 252, 3),
        ])
        self.assertEqual(self.before("SELECT Mob_ID, ClassType, Name, Realm, Level, X, Y, Z, Region FROM Mob "
                                     "WHERE Mob_ID='caledon-guard-25'"),
                         [("caledon-guard-25", "DOL.GS.Keeps.GuardStaticCaster", "Wizard", 1, 48, 33185, 37386, 3722, 250)])
        self.assertEqual(self.before("SELECT COUNT(*) FROM Quest"), [(0,)])
        # KeepManager.ExitBattleground moves an over-limit character to its realm's portal keep by these
        # TeleportIDs; the fork fixed "Svasudheim Faste" to "Svasud Faste".
        self.assertEqual(self.before("SELECT TeleportID, Realm, RegionID, COUNT(*) FROM Teleport WHERE TeleportID IN "
                                     "('Castle Sauvage', 'Svasud Faste', 'Druim Ligen', 'Svasudheim Faste') "
                                     "GROUP BY TeleportID, Realm, RegionID ORDER BY TeleportID"),
                         [("Castle Sauvage", 1, 1, 2), ("Druim Ligen", 3, 200, 2), ("Svasud Faste", 2, 100, 2)])

    def test_after_the_fix(self):
        self.assertEqual(self.lines, self.LINES)
        self.assertEqual(self.after("SELECT RegionID, Battleground_ID, MinLevel, MaxLevel, MaxRealmLevel, LastTimeRowUpdated "
                                    "FROM Battleground ORDER BY RegionID"), [
            (165, "Cathal Valley (Level 45-49)", 45, 49, 45, "2000-01-01 00:00:00"),
            (250, "Caledonia (Level 30-35 - RR1L9)", 30, 35, 10, NOW),
            (251, "Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6, NOW),
            (252, "Thidranki (Level 20-24 - RR1L3)", 20, 24, 4, NOW),
            (253, "Abermenai (Level 15-19 - RR1L2)", 15, 19, 3, NOW),
        ])
        self.assertEqual(self.after("SELECT ZoneID, Name, Experience FROM Zones WHERE RegionID BETWEEN 250 AND 253 "
                                    "ORDER BY ZoneID"),
                         [(250, "Caledonia", 0), (251, "Murdaigean", 0), (252, "Thidranki", 0), (253, "Abermenai", 0)])
        self.assertEqual(self.after("SELECT Description FROM Regions WHERE RegionID=250"), [("Caledonia",)])
        self.assertEqual(self.after("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep WHERE KeepID IN (11, 31) "
                                    "ORDER BY KeepID"), [(11, 24, 1, NOW), (31, 35, 1, NOW)])
        self.assertEqual(self.after(f"SELECT InternalID, Health, State FROM Door WHERE {self.GATES} ORDER BY InternalID"), [
            (250000301, 7000, 1), (250000302, 7000, 1), (252000301, 4800, 1), (252000302, 4800, 1),
        ])
        for sql in ("SELECT * FROM Keep WHERE KeepID NOT IN (11, 31) ORDER BY KeepID",
                    f"SELECT * FROM Door WHERE NOT {self.GATES} ORDER BY Door_ID"):
            with self.subTest(unchanged=sql):
                self.assertEqual(self.after(sql), self.before(sql))
        self.assertEqual(self.after(f"SELECT COUNT(*) FROM Mob WHERE {self.LEFTOVERS}"), [(0,)])
        mob = ", ".join(name for _, name, *_ in self.before('PRAGMA table_info("Mob")'))
        removed = self.before(f"SELECT {mob} FROM Mob WHERE {self.LEFTOVERS} ORDER BY Mob_ID")
        self.assertEqual(len(removed), 19)
        self.assertEqual(self.after(f"SELECT {mob}, FixId, RemovedUtc FROM fork_removed_mobs ORDER BY Mob_ID"),
                         [row + ("classic-battlegrounds-v1", NOW) for row in removed])
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob")[0][0], self.before("SELECT COUNT(*) FROM Mob")[0][0] - 19)
        self.assertEqual(self.after("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", NOW)])

    def test_second_run_changes_nothing(self):
        conn = sqlite3.connect(self.db)
        try:
            with conn:
                self.assertEqual(battlegrounds.apply(conn, LATER), [])
            self.assertEqual(conn.total_changes, 0)
        finally:
            conn.close()


if __name__ == "__main__":
    unittest.main()
```

What the new tests pin:
- **The scratch world** (`make_world`) has the seven tables the fix needs, trimmed to the columns it uses. Its rows are copied from the clean world, with three exceptions: the Heal Dummy is moved into region 250 so that class is covered, and the three `Quest` rows are made up, because the clean world has none.
- **`test_each_step_changes_upstream_values_and_reports`:** every step's line and every changed value. Caer Caledon's `Level` is set to 4 first, as a capture would leave it, so the "back to level 1" item is covered.
- **`test_a_failing_step_rolls_back_everything`:** a `RAISE(ABORT)` trigger on the last statement of each step, so the earlier steps and that step's earlier statements have already changed rows. The whole database, dumped, is the same afterwards: no change, no fork table, no marker. Once the trigger is gone, the next run applies.
- **`test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails`:** runs `world_fixes.py` itself. Its Disciple fix is committed, its exit code is 0, and its output ends with the "not applied" line. The next run applies the battlegrounds.
- **`test_owner_values_are_kept`:** a Battleground row, zone name, region description, `Experience`, `BaseLevel`, gate health and the Wizard that the owner changed stay as they are. The other rows still change, the counts read in the singular, and the marker is written.
- **`test_removed_rows_are_archived`:** every removed row is in `fork_removed_mobs` with all its columns, the fix id and the time. An archive table that has fewer columns than `Mob` still takes the rows, in the columns both have.
- **`BattlegroundShippedWorldTests`:** the clean world holds exactly what the steps expect. After one run on a copy, it has the classic values, the other keeps and doors are unchanged, and the 19 removed rows are archived as they were. A second run makes no change at all (`total_changes` is 0).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_battlegrounds.py -v`

Expected: the test module does not load, because `deploy/bin/battlegrounds.py` does not exist yet:
```
tests.test_battlegrounds (unittest.loader._FailedTest.tests.test_battlegrounds) ... ERROR
...
ImportError: Failed to import test module: tests.test_battlegrounds
...
ModuleNotFoundError: No module named 'battlegrounds'
...
Ran 1 test in 0.000s

FAILED (errors=1)
```

- [ ] **Step 3: Write the world data fix**

Create `deploy/bin/battlegrounds.py` (mode 100644, no shebang):

```python
"""The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era), in the world data.

world_fixes.py calls apply() inside its transaction, after its own fixes. The fix runs once per world:
it records the marker classic-battlegrounds-v1 in the fork table fork_world_fixes, and a world with the
marker is left alone, so changes the owner makes later stay. Every statement, the CREATE TABLE of the
two fork tables included, runs under one savepoint. If any step fails, the savepoint is rolled back
(no change, no fork table, no marker, so the next start tries again) and apply() returns only the
"not applied" line; world_fixes.py still commits its own fixes and the server starts.

Each step changes a value only while it still holds upstream's value, and adds one line to the result
when it changed something:
1. the Battleground rows get the classic level ranges and realm rank caps;
2. Caledon is shown as Caledonia, and no battleground keeps a zone XP bonus;
3. Thidranki Faste and Caer Caledon get base levels for their ranges and keep Level 1, and their gates
   the matching health;
6. Atlas's leftovers go: training dummies, Void Merchants and a stray Wizard (archived first in the
   fork table fork_removed_mobs), and saved quests of the deleted battleground daily quest classes.
"""
import datetime

FIX_ID = "classic-battlegrounds-v1"
MARKER_TABLE = "fork_world_fixes"
ARCHIVE_TABLE = "fork_removed_mobs"
SAVEPOINT = "classic_battlegrounds"
NEEDED_TABLES = ("Battleground", "Keep", "Mob", "Door", "Zones", "Regions", "Quest")
NOT_APPLIED = "Classic battlegrounds: not applied ({}); the battlegrounds stay as upstream ships them"
NAMES = {253: "Abermenai", 252: "Thidranki", 251: "Murdaigean", 250: "Caledonia"}
ORDER = (253, 252, 251, 250)

# Step 1, by RegionID: (label, MinLevel, MaxLevel, MaxRealmLevel) as upstream ships it, then classic.
# MaxRealmLevel means "must be below": 3 is up to 1L2 (under 125 realm points), 4 up to 1L3 (350),
# 6 up to 1L5 (1,375) and 10 up to 1L9 (7,125).
BATTLEGROUND_ROWS = {
    253: (("Abermenai (Level 15-19)", 15, 19, 2), ("Abermenai (Level 15-19 - RR1L2)", 15, 19, 3)),
    252: (("Thidranki (Level 20-24 - RR2L0)", 20, 24, 10), ("Thidranki (Level 20-24 - RR1L3)", 20, 24, 4)),
    251: (("Murdaigean (Level 25-29)", 25, 29, 5), ("Murdaigean (Level 25-29 - RR1L5)", 25, 29, 6)),
    250: (("Caledonia (Level 34-39 - RR3L5)", 30, 34, 25), ("Caledonia (Level 30-35 - RR1L9)", 30, 35, 10)),
}
# Step 2: the zones whose Experience is 50 upstream, in ORDER (251 and 253 are already 0).
XP_BONUS_ZONES = (252, 250)
# Step 3, by KeepID: (Region, name, upstream BaseLevel, classic BaseLevel).
KEEP_LEVELS = {11: (252, "Thidranki Faste", 26, 24), 31: (250, "Caer Caledon", 46, 35)}
# Their gates, by Door.InternalID: (upstream Health, the new BaseLevel x keep_doors_base_health 200).
GATE_HEALTH = {252000301: (5200, 4800), 252000302: (5200, 4800), 250000301: (9200, 7000), 250000302: (9200, 7000)}
# Step 6.
QUEST_CLASSES = (
    "DOL.GS.DailyQuest.Albion.CaleKeepCaptureAlb", "DOL.GS.DailyQuest.Hibernia.CaleKeepCaptureHib",
    "DOL.GS.DailyQuest.Midgard.CaleKeepCaptureMid", "DOL.GS.DailyQuest.Albion.CaleKillQuestAlb",
    "DOL.GS.DailyQuest.Hibernia.CaleKillQuestHib", "DOL.GS.DailyQuest.Hibernia.CaleKillQuestMid",
    "DOL.GS.DailyQuest.Albion.ThidKeepCaptureAlb", "DOL.GS.DailyQuest.Hibernia.ThidKeepCaptureHib",
    "DOL.GS.DailyQuest.Midgard.ThidKeepCaptureMid", "DOL.GS.DailyQuest.Albion.ThidKillQuestAlb",
    "DOL.GS.DailyQuest.Hibernia.ThidKillQuestHib", "DOL.GS.DailyQuest.Hibernia.ThidKillQuestMid",
)
DUMMY_CLASSES = ("DOL.GS.DPSDummy", "DOL.GS.HitbackDummy", "DOL.GS.HealDummy")
VOID_MERCHANT_CLASS = "DOL.GS.Scripts.RPTradeInMerchant"
STRAY_WIZARD = {"Mob_ID": "caledon-guard-25", "ClassType": "DOL.GS.Keeps.GuardStaticCaster", "Name": "Wizard",
                "Realm": 1, "Level": 48, "X": 33185, "Y": 37386, "Z": 3722, "Region": 250}
IN_BATTLEGROUNDS = "Region BETWEEN 250 AND 253"


def apply(conn, now=None):
    """Apply the classic battlegrounds once per world; returns one line per step that changed something.

    Inside an open transaction (world_fixes.py's), nothing here commits; on a connection with none open,
    the savepoint is the transaction, and its release commits it."""
    tables = {name.lower() for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if any(table.lower() not in tables for table in NEEDED_TABLES):
        return []
    if MARKER_TABLE in tables and conn.execute(f"SELECT 1 FROM {MARKER_TABLE} WHERE FixId=?", (FIX_ID,)).fetchone():
        return []
    now = now or _now()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        conn.execute(f"CREATE TABLE IF NOT EXISTS {MARKER_TABLE} (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)")
        mob = conn.execute('PRAGMA table_info("Mob")').fetchall()
        columns = ", ".join(f'"{name}" {declared}' for _, name, declared, *_ in mob)
        conn.execute(f"CREATE TABLE IF NOT EXISTS {ARCHIVE_TABLE} ({columns}, FixId TEXT NOT NULL, "
                     "RemovedUtc TEXT NOT NULL)")
        lines = []
        for step in STEPS:
            line = step(conn, now)
            if line:
                lines.append(line)
        conn.execute(f"INSERT INTO {MARKER_TABLE} (FixId, AppliedUtc) VALUES (?, ?)", (FIX_ID, now))
    except Exception as e:  # any failure: undo the whole fix, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_APPLIED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    return lines


def _step1_battleground_rows(conn, now):
    changed = []
    for region in ORDER:
        old, new = BATTLEGROUND_ROWS[region]
        cur = conn.execute("UPDATE Battleground SET Battleground_ID=?, MinLevel=?, MaxLevel=?, MaxRealmLevel=?, "
                           "LastTimeRowUpdated=? WHERE RegionID=? AND Battleground_ID=? AND MinLevel=? AND MaxLevel=? "
                           "AND MaxRealmLevel=?", (*new, now, region, *old))
        if cur.rowcount:
            changed.append(NAMES[region])
    return "Battlegrounds: classic level and realm rank limits for " + ", ".join(changed) if changed else None


def _step2_names_and_xp(conn, now):
    parts = []
    renamed = conn.execute("UPDATE Zones SET Name='Caledonia', LastTimeRowUpdated=? "
                           "WHERE ZoneID=250 AND Name='Caledon'", (now,)).rowcount
    renamed += conn.execute("UPDATE Regions SET Description='Caledonia', LastTimeRowUpdated=? "
                            "WHERE RegionID=250 AND Description='Caledon'", (now,)).rowcount
    if renamed:
        parts.append("Caledon is shown as Caledonia")
    no_bonus = []
    for zone in XP_BONUS_ZONES:
        if conn.execute("UPDATE Zones SET Experience=0, LastTimeRowUpdated=? WHERE ZoneID=? AND Experience=50",
                        (now, zone)).rowcount:
            no_bonus.append(NAMES[zone])
    if no_bonus:
        parts.append("no zone XP bonus in " + ", ".join(no_bonus))
    return "Battlegrounds: " + "; ".join(parts) if parts else None


def _step3_keep_levels(conn, now):
    items = []
    for keep_id, (region, name, old, new) in KEEP_LEVELS.items():
        if conn.execute("UPDATE Keep SET BaseLevel=?, LastTimeRowUpdated=? WHERE KeepID=? AND Region=? AND BaseLevel=?",
                        (new, now, keep_id, region, old)).rowcount:
            items.append(f"{name} base level {new}")
    for keep_id, (region, name, _, _) in KEEP_LEVELS.items():  # a capture on this world left it higher
        if conn.execute("UPDATE Keep SET Level=1, LastTimeRowUpdated=? WHERE KeepID=? AND Region=? AND Level>1",
                        (now, keep_id, region)).rowcount:
            items.append(f"{name} back to level 1")
    gates = 0
    for door, (old, new) in GATE_HEALTH.items():
        gates += conn.execute("UPDATE Door SET Health=?, LastTimeRowUpdated=? WHERE InternalID=? AND Health=?",
                              (new, now, door, old)).rowcount
    if gates:
        items.append(_count(gates, "gate's health", "gates' health"))
    return "Battlegrounds: keep levels for the ranges (" + ", ".join(items) + ")" if items else None


def _step6_atlas_leftovers(conn, now):
    dummies = _archive(conn, now, f"ClassType IN (?, ?, ?) AND {IN_BATTLEGROUNDS}", DUMMY_CLASSES)
    merchants = _archive(conn, now, f"ClassType=? AND {IN_BATTLEGROUNDS}", (VOID_MERCHANT_CLASS,))
    wizard = _archive(conn, now, " AND ".join(f"{column}=?" for column in STRAY_WIZARD), tuple(STRAY_WIZARD.values()))
    quests = conn.execute("DELETE FROM Quest WHERE Name IN (%s)" % ", ".join("?" * len(QUEST_CLASSES)),
                          QUEST_CLASSES).rowcount
    removed = []
    if dummies:
        removed.append(_count(dummies, "training dummy", "training dummies"))
    if merchants:
        removed.append(_count(merchants, "Void Merchant", "Void Merchants"))
    if wizard:
        removed.append("the stray Wizard")
    parts = []
    if removed:
        parts.append(f"Atlas leftovers archived in {ARCHIVE_TABLE} and removed (" + ", ".join(removed) + ")")
    if quests:
        parts.append(_count(quests, "saved battleground daily quest", "saved battleground daily quests") + " deleted")
    return "Battlegrounds: " + "; ".join(parts) if parts else None


STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step6_atlas_leftovers)


def _archive(conn, now, where, params):
    """Copy the Mob rows that match into the archive table, then delete them; returns how many."""
    archive = {name for _, name, *_ in conn.execute(f'PRAGMA table_info("{ARCHIVE_TABLE}")')}
    names = ", ".join(f'"{name}"' for _, name, *_ in conn.execute('PRAGMA table_info("Mob")') if name in archive)
    conn.execute(f"INSERT INTO {ARCHIVE_TABLE} ({names}, FixId, RemovedUtc) "
                 f"SELECT {names}, ?, ? FROM Mob WHERE {where}", (FIX_ID, now, *params))
    return conn.execute(f"DELETE FROM Mob WHERE {where}", params).rowcount


def _count(n, one, many):
    return f"{n} {one if n == 1 else many}"


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
```

- [ ] **Step 4: Run the tests to verify they pass, except the world_fixes.py one**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_battlegrounds.py -v`

Expected: 12 tests pass. `test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails` fails, because `world_fixes.py` does not call the fix yet (Step 7). Timing varies:
```
test_a_failing_step_rolls_back_everything (tests.test_battlegrounds.BattlegroundFixTests.test_a_failing_step_rolls_back_everything) ... ok
test_each_step_changes_upstream_values_and_reports (tests.test_battlegrounds.BattlegroundFixTests.test_each_step_changes_upstream_values_and_reports) ... ok
test_no_marker_without_the_needed_tables (tests.test_battlegrounds.BattlegroundFixTests.test_no_marker_without_the_needed_tables) ... ok
test_owner_values_are_kept (tests.test_battlegrounds.BattlegroundFixTests.test_owner_values_are_kept) ... ok
test_removed_rows_are_archived (tests.test_battlegrounds.BattlegroundFixTests.test_removed_rows_are_archived) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundFixTests.test_second_run_changes_nothing) ... ok
test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails (tests.test_battlegrounds.BattlegroundFixTests.test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails) ... FAIL
test_after_the_fix (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_after_the_fix) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_second_run_changes_nothing) ... ok
test_world_holds_what_the_steps_expect (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_world_holds_what_the_steps_expect) ... ok
test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone) ... ok
test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste) ... ok
test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork) ... ok
...
AssertionError: Lists differ: ["Dis[66 chars]> 33"] != ["Dis[66 chars]> 33", 'Classic battlegrounds: not applied (in[59 chars]hem']

Second list contains 1 additional elements.
First extra element 1:
'Classic battlegrounds: not applied (injected failure); the battlegrounds stay as upstream ships them'
...
Ran 13 tests in 4.139s

FAILED (failures=1)
```

- [ ] **Step 5: Make test_shipped_world check the full list of lines**

In `deploy/tests/test_world_fixes.py`, replace the first four lines of `test_shipped_world`:

```python
    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertEqual(len(wf.apply(self.db)), 3)
```

with:

```python
    @unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
    def test_shipped_world(self):
        shutil.copyfile(TEST_WORLD, self.db)
        self.assertEqual(wf.apply(self.db), [
            "Disciple (Necromancer's base class) enabled: disabled_classes 20;33;34;39;58-62 -> 33;34;39;58-62",
            "Saracen Disciples get a starting location (with the Inconnu Disciples, region 51)",
            "Welcome messages now name HearthDAoC (motd, starting_msg)",
            "Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia",
            "Battlegrounds: Caledon is shown as Caledonia; no zone XP bonus in Thidranki, Caledonia",
            "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
            "4 gates' health)",
            "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
            "3 Void Merchants, the stray Wizard)",
        ])
        self.assertEqual(self.q("SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v1",)])
```

The three assertions after them stay as they are.

- [ ] **Step 6: Run the test to verify it fails**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy -p test_world_fixes.py -v`

Expected: `test_shipped_world` fails, because `world_fixes.apply` returns only its own 3 lines. Timing varies:
```
test_keeps_other_separators_and_empty (tests.test_world_fixes.EnableClassesTests.test_keeps_other_separators_and_empty) ... ok
test_removes_a_single_id (tests.test_world_fixes.EnableClassesTests.test_removes_a_single_id) ... ok
test_splits_a_range (tests.test_world_fixes.EnableClassesTests.test_splits_a_range) ... ok
test_apply_enables_disciple_and_adds_the_saracen_start (tests.test_world_fixes.FixesTests.test_apply_enables_disciple_and_adds_the_saracen_start) ... ok
test_cli (tests.test_world_fixes.FixesTests.test_cli) ... ok
test_owner_choices_are_kept (tests.test_world_fixes.FixesTests.test_owner_choices_are_kept) ... ok
test_second_run_changes_nothing (tests.test_world_fixes.FixesTests.test_second_run_changes_nothing) ... ok
test_shipped_world (tests.test_world_fixes.FixesTests.test_shipped_world) ... FAIL
test_welcome_messages_name_hearthdaoc (tests.test_world_fixes.FixesTests.test_welcome_messages_name_hearthdaoc) ... ok
...
AssertionError: Lists differ: ["Dis[236 chars]msg)'] != ["Dis[236 chars]msg)', 'Battlegrounds: classic level and realm[403 chars]rd)']

Second list contains 4 additional elements.
First extra element 3:
'Battlegrounds: classic level and realm rank limits for Abermenai, Thidranki, Murdaigean, Caledonia'
...
Ran 9 tests in 0.538s

FAILED (failures=1)
```

- [ ] **Step 7: Call the fix from world_fixes.py**

In `deploy/bin/world_fixes.py`, replace the end of the docstring and the imports:

```python
3. The welcome messages players see (motd, starting_msg) name Offline DAoC and describe a world to play
   alone. Replace upstream's texts with HearthDAoC's.

All of them only apply when needed and leave anything the owner set themselves alone.
"""
import argparse
import datetime
import re
import sqlite3
import sys
```

with:

```python
3. The welcome messages players see (motd, starting_msg) name Offline DAoC and describe a world to play
   alone. Replace upstream's texts with HearthDAoC's.
4. The classic battlegrounds (levels 15 to 35, as in the Shrouded Isles era): battlegrounds.py, once per
   world (the marker classic-battlegrounds-v1 in fork_world_fixes). It runs last, under its own
   savepoint: if it fails, it undoes only itself and prints why, and the fixes above are still saved.

All of them only apply when needed and leave anything the owner set themselves alone.
"""
import argparse
import datetime
import re
import sqlite3
import sys

import battlegrounds
```

Then, in `apply()`, replace:

```python
            if renamed:
                changes.append("Welcome messages now name HearthDAoC (%s)" % ", ".join(renamed))
    finally:
```

with:

```python
            if renamed:
                changes.append("Welcome messages now name HearthDAoC (%s)" % ", ".join(renamed))
            changes.extend(battlegrounds.apply(conn, _now()))
    finally:
```

- [ ] **Step 8: Run both test files to verify they pass**

`PYTHONPATH=deploy` makes the `tests` package importable, as `-t deploy` does for `discover`.

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db PYTHONPATH=deploy python3 -B -m unittest -v tests.test_battlegrounds tests.test_world_fixes`

Expected (timing varies):
```
test_a_failing_step_rolls_back_everything (tests.test_battlegrounds.BattlegroundFixTests.test_a_failing_step_rolls_back_everything) ... ok
test_each_step_changes_upstream_values_and_reports (tests.test_battlegrounds.BattlegroundFixTests.test_each_step_changes_upstream_values_and_reports) ... ok
test_no_marker_without_the_needed_tables (tests.test_battlegrounds.BattlegroundFixTests.test_no_marker_without_the_needed_tables) ... ok
test_owner_values_are_kept (tests.test_battlegrounds.BattlegroundFixTests.test_owner_values_are_kept) ... ok
test_removed_rows_are_archived (tests.test_battlegrounds.BattlegroundFixTests.test_removed_rows_are_archived) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundFixTests.test_second_run_changes_nothing) ... ok
test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails (tests.test_battlegrounds.BattlegroundFixTests.test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails) ... ok
test_after_the_fix (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_after_the_fix) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_second_run_changes_nothing) ... ok
test_world_holds_what_the_steps_expect (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_world_holds_what_the_steps_expect) ... ok
test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone) ... ok
test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste) ... ok
test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork) ... ok
test_keeps_other_separators_and_empty (tests.test_world_fixes.EnableClassesTests.test_keeps_other_separators_and_empty) ... ok
test_removes_a_single_id (tests.test_world_fixes.EnableClassesTests.test_removes_a_single_id) ... ok
test_splits_a_range (tests.test_world_fixes.EnableClassesTests.test_splits_a_range) ... ok
test_apply_enables_disciple_and_adds_the_saracen_start (tests.test_world_fixes.FixesTests.test_apply_enables_disciple_and_adds_the_saracen_start) ... ok
test_cli (tests.test_world_fixes.FixesTests.test_cli) ... ok
test_owner_choices_are_kept (tests.test_world_fixes.FixesTests.test_owner_choices_are_kept) ... ok
test_second_run_changes_nothing (tests.test_world_fixes.FixesTests.test_second_run_changes_nothing) ... ok
test_shipped_world (tests.test_world_fixes.FixesTests.test_shipped_world) ... ok
test_welcome_messages_name_hearthdaoc (tests.test_world_fixes.FixesTests.test_welcome_messages_name_hearthdaoc) ... ok

----------------------------------------------------------------------
Ran 22 tests in 3.620s

OK
```
No test may be skipped: the three real-world tests and `test_shipped_world` must show `... ok`.

- [ ] **Step 9: Document the fix**

In `deploy/entrypoint.sh`, replace the comment above the `world_fixes.py` call:

```bash
# Fixes to upstream's classic world data (Disciple enabled, Saracen Disciple start, HearthDAoC welcome
# messages); see world_fixes.py.
```

with:

```bash
# Fixes to upstream's classic world data (Disciple enabled, Saracen Disciple start, HearthDAoC welcome
# messages) and, once per world, the classic battlegrounds; see world_fixes.py and battlegrounds.py. A
# battleground fix that fails undoes itself and says so, and the start goes on.
```

In `docs/fork/FORK.md`, table "What the fork changes", add a row right after the "Leveling spawns" row:

```markdown
| Classic battlegrounds 15-35, world data (once per world) | `deploy/bin/battlegrounds.py` (run by `deploy/bin/world_fixes.py`) | none |
```

In `deploy/HANDOFF.md`, section "6. Day-to-day", replace its last two lines:

```markdown
Before remote players join: `./hdc auto-accounts off` and create their accounts with
`./hdc account create <name> <password>`.
```

with:

```markdown
Before remote players join: `./hdc auto-accounts off` and create their accounts with
`./hdc account create <name> <password>`.

**Classic battlegrounds.** At its first start, a world gets the classic battlegrounds (Abermenai 15-19,
Thidranki 20-24, Murdaigean 25-29, Caledonia 30-35); `./hdc logs` shows each change on a line starting
`Battlegrounds:`. This runs once per world: the row `classic-battlegrounds-v1` in the world's
`fork_world_fixes` table records it, so later changes to the battlegrounds stay. Deleting that row makes
it run again at the next start, and the parts whose results are still there change nothing. The `Mob`
rows it removes (training dummies, Void Merchants, a stray Wizard) are kept in `fork_removed_mobs`; saved
Atlas battleground daily quests (`Quest` rows) are deleted, not archived. If it fails, the start log says
`Classic battlegrounds: not applied (...)`, the server starts with upstream's battlegrounds, and it tries
again at the next start. `./hdc new-world` and `./hdc upgrade-world` make a world without that row, so it
runs again there, and changes made in game to battleground keeps and guards are not carried over.
Battleground keep guard levels follow `keep_guard_level_multiplier` (1.6), which also sets the frontier
keeps' guards.
```

- [ ] **Step 10: Run the whole deploy suite and check the tree**

Run: `bash -n deploy/entrypoint.sh && echo "entrypoint syntax ok"`

Expected: `entrypoint syntax ok`

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy`

Expected (takes about 30 s):
```
.....................s..................................................................................................................ssss..........
----------------------------------------------------------------------
Ran 150 tests in 35.679s

OK (skipped=5)
```
The five skips are the PowerShell bundle test (no `pwsh`) and the four `test_world_admin` upgrade tests (they also need `HDC_TOOLS`). No test may fail.

Run: `git status --short --untracked-files=all`

Expected:
```
 M deploy/HANDOFF.md
 M deploy/bin/world_fixes.py
 M deploy/entrypoint.sh
 M deploy/tests/test_battlegrounds.py
 M deploy/tests/test_world_fixes.py
 M docs/fork/FORK.md
?? deploy/bin/battlegrounds.py
```

- [ ] **Step 11: Commit**

```bash
git add deploy/bin/battlegrounds.py deploy/bin/world_fixes.py deploy/entrypoint.sh deploy/HANDOFF.md docs/fork/FORK.md deploy/tests/test_battlegrounds.py deploy/tests/test_world_fixes.py
git commit -q -m "feat(deploy): classic battleground limits, names, keep levels, no Atlas leftovers

deploy/bin/battlegrounds.py runs from world_fixes.py once per world,
under one savepoint, and records classic-battlegrounds-v1 in the fork
table fork_world_fixes. It gives the Battleground rows the classic
ranges and caps (Abermenai 15-19 up to 1L2, Thidranki 20-24 up to 1L3,
Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9), shows Caledon
as Caledonia, removes the zone XP bonus, lowers Thidranki Faste's and
Caer Caledon's base levels to 24 and 35 with their gates' health,
archives and removes Atlas's training dummies, Void Merchants and the
stray Wizard, and deletes saved Atlas battleground daily quests. Each
step changes only upstream's values. If a step fails, the fix undoes
itself and says why, and world_fixes.py still commits its own fixes
and exits 0, so the server starts.

Tests cover each step on a scratch world, a rollback for a failure in
every step, world_fixes.py's exit code, owner values, the archive, a
world without the needed tables, and the clean classic world before
and after the fix (HDC_TEST_WORLD).

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git show --stat --format=%s HEAD
```

Expected:
```
feat(deploy): classic battleground limits, names, keep levels, no Atlas leftovers

 deploy/HANDOFF.md                  |  13 +
 deploy/bin/battlegrounds.py        | 180 ++++++++++++++
 deploy/bin/world_fixes.py          |   6 +
 deploy/entrypoint.sh               |   3 +-
 deploy/tests/test_battlegrounds.py | 480 +++++++++++++++++++++++++++++++++++++-
 deploy/tests/test_world_fixes.py   |  13 +-
 docs/fork/FORK.md                  |   1 +
 7 files changed, 690 insertions(+), 6 deletions(-)
```
The line counts of `deploy/tests/test_battlegrounds.py` are those for Task 2's source checks exactly as Step 1 shows them.

---

### Task 4: World data, part 2: keeps and guards for Abermenai and Murdaigean

**Files:**
- Modify: `deploy/bin/battlegrounds.py` (the move: `moved`, `facing`, `gate_spots`; steps 4 and 5; `STEPS`; the docstring)
- Test: `deploy/tests/test_battlegrounds.py` (scratch world rows for steps 4 and 5, `BattlegroundGeometryTests`, step 4 and 5 cases in `BattlegroundFixTests`, real-data checks in `BattlegroundShippedWorldTests`)
- Test: `deploy/tests/test_world_fixes.py` (`test_shipped_world` checks the full list of 9 lines)

**Interfaces:**
- Consumes, from Task 3:
  - in `deploy/bin/battlegrounds.py`: `apply(conn, now=None)` (the table check, the marker, the savepoint, the "not applied" line); `STEPS`; `NAMES`; `_count(n, one, many)`. Each step takes `(conn, now)` and returns its line, or `None` when it changed nothing.
  - in `deploy/tests/test_battlegrounds.py`: `make_world`, `WORLD_SCHEMA` (its `Keep` table has every column of the real one), `SEED`, `apply_fix`, `query`, `execute`, `marks`, `NOW`, `LATER`, `INJECTED`; `BattlegroundFixTests.LINES` and `FAILURES` (`test_a_failing_step_rolls_back_everything` checks that `FAILURES` names exactly the steps in `STEPS`); `BattlegroundShippedWorldTests.LINES`, `GATES`, `setUpClass` (one run on a copy of `HDC_TEST_WORLD`, at `NOW`), `before` (the original, read-only) and `after` (the copy).

  This task changes only Python and runs no `dotnet` command.
- Produces, in `deploy/bin/battlegrounds.py`:
  - The constants of the move: `P = (18048, 18176)`, `FLOOR_DROP = 600`, `MOVES = {253: ((33152, 38400), 30), 251: ((33408, 38272), 190)}` (C and degrees), `GATE_SIDE = 60` and `GATE_INSIDE = 150`.
  - The constants of steps 4 and 5: `PORTAL_KEEP_SOURCE = (252, (12, 13, 14), 4000)`, `KEEP_GUARD_CLASSES`, `CENTRAL_KEEPS` (region → Name, `Keep_ID`, `BaseLevel`; 253 first), `FIRST_KEEP_ID = 32`, `KEEP_CREATE_INFO = "HearthDAoC classic-battlegrounds-v1"`, `PORTAL_KEEP_HIB = 12`, `CENTRAL_SOURCES` (the 7 on-model rows), `FIGHTER_TEMPLATE`, `LORD_TEMPLATE` and `CENTRAL_DOORS` (region → outer and inner `Door.InternalID`, upstream and new `Health`).
  - `moved(region, x, y, z, heading) -> (x, y, z, heading)`: world = C + R(angle) · (p − P), rounded; Z − 600; heading + round(angle × 4096 / 360), modulo 4096.
  - `facing(frm, to) -> int`: `round(atan2(-dx, dy) * 4096 / 2π) % 4096`, the server's heading from `frm` towards `to`.
  - `gate_spots(outer, inner) -> list` of four `(x, y, z, heading)`: from the outer and inner door rows `(x, y, z)`, the two passage spots, then the two spots inside the inner door, at the inner door's Z, facing `facing(inner, outer)`.
  - `_step4_portal_keep_guards(conn, now)`: for 253, then 251, while the region has no `DOL.GS.Keeps.%` row, copies Thidranki's portal keep guards and hasteners (the rows of `KEEP_GUARD_CLASSES` in 252 within 4,000 of KeepID 12, 13 or 14) with `Region`, `Mob_ID` `hdc-bg<region>-pk-<source Mob_ID>` and `LastTimeRowUpdated` replaced.
  - `_step5_central_keeps(conn, now)`: for 253, then 251, skips the region when KeepID 12's row, one of the 9 source and template rows or one of its two central door rows is missing. Otherwise, when the region has no `Keep` row with `BaseLevel` under 100, it adds the `Keep` row (first free KeepID from 32) and the 12 central rows, each only if its `Mob_ID` is new. Then it closes the region's two central doors at full health, each value only while it still holds upstream's.
  - `_copy_mobs(conn, replace, where, params)`: inserts a copy of each matching `Mob` row (alias `m`), every column from `PRAGMA table_info("Mob")` as it is except those in `replace` (column → (SQL expression with one `?`, its value)); returns how many rows it added.
  - `STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step4_portal_keep_guards, _step5_central_keeps, _step6_atlas_leftovers)`.
  - On the shipped world the result is 6 lines. These two are new, between Task 3's third and fourth:
    ```
    Battlegrounds: portal keep guards and hasteners for Abermenai (34), Murdaigean (34)
    Battlegrounds: central keeps Dun Abermenai (keep 32, 12 guards), Dun Murdaigean (keep 33, 12 guards); 4 central doors closed at full health
    ```
    When only one keep is added, the part reads "central keep <name> (...)", and "1 guard" and "1 central door" are singular.
- Produces, for Task 5: these two lines in the server's start log at the first start of a world (`world_fixes.py` prints its 3 lines and then the 6).

All commands run from the repository root, on branch `sub5-battlegrounds`.

**Prerequisites:**
- Tasks 1 to 3 are done. Task 1 already excluded the build output; these lines do nothing when they are present:
  ```bash
  grep -qxF 'source/server/build/' .git/info/exclude || echo 'source/server/build/' >> .git/info/exclude
  grep -qxF 'source/server/CoreServer/config/serverconfig.xml' .git/info/exclude || echo 'source/server/CoreServer/config/serverconfig.xml' >> .git/info/exclude
  ```
- The real-world tests need the clean classic world, `~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db`, passed as `HDC_TEST_WORLD`. The tests only read it; they copy it into a temporary directory before applying anything. Without it, those tests are skipped.

- [ ] **Step 1: Write the failing geometry tests**

In `deploy/tests/test_battlegrounds.py`, replace the imports' first two lines:

```python
import os
import pathlib
```

with:

```python
import math
import os
import pathlib
```

Then add the class `BattlegroundGeometryTests` right before `BattlegroundShippedWorldTests`. Replace:

```python
@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class BattlegroundShippedWorldTests(unittest.TestCase):
```

with:

```python
class BattlegroundGeometryTests(unittest.TestCase):
    """The move onto a central keep model (spec 3.2, "The move") and the fighters' spots at its gate."""

    def test_p_maps_to_c(self):
        self.assertEqual(battlegrounds.moved(253, 18048, 18176, 4320, 0), (33152, 38400, 3720, 341))
        self.assertEqual(battlegrounds.moved(251, 18048, 18176, 4320, 0), (33408, 38272, 3720, 2162))

    def test_a_point_east_of_p_turns_by_the_angle(self):
        # P + (100, 0) lands 100 units from C (give or take the rounding to whole units), at 30 degrees in 253
        # and 190 in 251 (from +X towards +Y).
        for region, (cx, cy), spot, degrees in ((253, (33152, 38400), (33239, 38450, 3720, 341), 30),
                                                (251, (33408, 38272), (33310, 38255, 3720, 2162), 190)):
            with self.subTest(region=region):
                x, y, z, heading = battlegrounds.moved(region, 18148, 18176, 4320, 0)
                self.assertEqual((x, y, z, heading), spot)
                self.assertAlmostEqual(math.hypot(x - cx, y - cy), 100, delta=1)
                self.assertAlmostEqual(math.degrees(math.atan2(y - cy, x - cx)) % 360, degrees, delta=0.5)

    def test_headings_wrap_at_4096(self):
        self.assertEqual(battlegrounds.moved(253, 18048, 18176, 4320, 4000)[3], 245)  # 4000 + 341 - 4096
        self.assertEqual(battlegrounds.moved(251, 18048, 18176, 4320, 1934)[3], 0)  # 1934 + 2162 = 4096
        self.assertEqual(battlegrounds.moved(251, 18048, 18176, 4320, 4095)[3], 2161)
        # The server's headings: 0 towards +Y, 1024 towards -X, 2048 towards -Y, 3072 towards +X; a hair
        # short of +Y is 4095, not -1.
        self.assertEqual([battlegrounds.facing((0, 0), to) for to in ((0, 100), (-100, 0), (0, -100), (100, 0), (1, 1000))],
                         [0, 1024, 2048, 3072, 4095])

    def test_gate_spots_from_the_door_rows(self):
        # The outer (000301) and inner (000302) central door rows of 253, then 251, give the spec's fighter spots.
        self.assertEqual(battlegrounds.gate_spots((33849, 39604, 3737), (33659, 39059, 3720)), [
            (33697, 39351, 3720, 3877), (33811, 39312, 3720, 3877), (33553, 38937, 3720, 3877), (33666, 38898, 3720, 3877),
        ])
        self.assertEqual(battlegrounds.gate_spots((32337, 37404, 3724), (32698, 37833, 3720)), [
            (32563, 37580, 3720, 1592), (32472, 37657, 3720, 1592), (32840, 37909, 3720, 1592), (32749, 37986, 3720, 1592),
        ])


@unittest.skipUnless(TEST_WORLD, "needs HDC_TEST_WORLD (a clean classic world database)")
class BattlegroundShippedWorldTests(unittest.TestCase):
```

What the geometry tests pin:
- **`test_p_maps_to_c`:** the portal keep model's origin P lands on each central model's origin C, 600 lower, with the heading turned by +341 (253) and +2162 (251).
- **`test_a_point_east_of_p_turns_by_the_angle`:** the turn goes from +X towards +Y, by 30° in 253 and 190° in 251. With the opposite turn the point would land at −30° and 170°.
- **`test_headings_wrap_at_4096`:** turned headings and `facing` stay in 0 to 4095.
- **`test_gate_spots_from_the_door_rows`:** the four fighters' spots and heading in the spec's table (step 5, "Fighter 1, 2" and "Fighter 3, 4") come out of the two door rows.

- [ ] **Step 2: Run the geometry tests to verify they fail**

`PYTHONPATH=deploy` makes the `tests` package importable, as `-t deploy` does for `discover`.

Run: `PYTHONPATH=deploy python3 -B -m unittest -v tests.test_battlegrounds.BattlegroundGeometryTests`

Expected: all four fail, because `battlegrounds.py` has no `moved`, `facing` or `gate_spots` yet. There are five errors because one test has a subtest per region. Timing varies:
```
test_a_point_east_of_p_turns_by_the_angle (tests.test_battlegrounds.BattlegroundGeometryTests.test_a_point_east_of_p_turns_by_the_angle) ... 
  test_a_point_east_of_p_turns_by_the_angle (tests.test_battlegrounds.BattlegroundGeometryTests.test_a_point_east_of_p_turns_by_the_angle) (region=253) ... ERROR
  test_a_point_east_of_p_turns_by_the_angle (tests.test_battlegrounds.BattlegroundGeometryTests.test_a_point_east_of_p_turns_by_the_angle) (region=251) ... ERROR
test_gate_spots_from_the_door_rows (tests.test_battlegrounds.BattlegroundGeometryTests.test_gate_spots_from_the_door_rows) ... ERROR
test_headings_wrap_at_4096 (tests.test_battlegrounds.BattlegroundGeometryTests.test_headings_wrap_at_4096) ... ERROR
test_p_maps_to_c (tests.test_battlegrounds.BattlegroundGeometryTests.test_p_maps_to_c) ... ERROR
...
AttributeError: module 'battlegrounds' has no attribute 'moved'
AttributeError: module 'battlegrounds' has no attribute 'gate_spots'
...
Ran 4 tests in 0.002s

FAILED (errors=5)
```

- [ ] **Step 3: Write the move**

In `deploy/bin/battlegrounds.py`, replace:

```python
import datetime
```

with:

```python
import datetime
import math
```

Replace:

```python
IN_BATTLEGROUNDS = "Region BETWEEN 250 AND 253"
```

with:

```python
IN_BATTLEGROUNDS = "Region BETWEEN 250 AND 253"

# Step 5, the move (spec 3.2): a spot on Thidranki's Hibernia portal keep model goes onto a central keep
# model. P is the portal keep model's origin. By region, C is the central model's origin and the angle
# (degrees, from +X towards +Y) is how far that model is turned from the portal keep's. The central
# keep's floor is FLOOR_DROP lower (3720 against 4320, from the door rows).
P = (18048, 18176)
FLOOR_DROP = 600
MOVES = {253: ((33152, 38400), 30), 251: ((33408, 38272), 190)}
# The four fighters at a central keep's gate stand GATE_SIDE to either side of the line through its two
# doors: two in the passage between the doors, and two GATE_INSIDE inside the inner door.
GATE_SIDE = 60
GATE_INSIDE = 150
```

Then add the three functions right after `apply()`. Replace:

```python
    conn.execute(f"RELEASE {SAVEPOINT}")
    return lines
```

with:

```python
    conn.execute(f"RELEASE {SAVEPOINT}")
    return lines


def moved(region, x, y, z, heading):
    """A spot on Thidranki's Hibernia portal keep model, moved onto region's central keep model (spec 3.2,
    "The move"): turned about P by the region's angle and put at C, FLOOR_DROP lower, with the heading
    turned by the same angle. Returns whole units: (x, y, z, heading)."""
    (cx, cy), degrees = MOVES[region]
    angle = math.radians(degrees)
    dx, dy = x - P[0], y - P[1]
    return (round(cx + dx * math.cos(angle) - dy * math.sin(angle)),
            round(cy + dx * math.sin(angle) + dy * math.cos(angle)),
            z - FLOOR_DROP,
            (heading + round(degrees * 4096 / 360)) % 4096)


def facing(frm, to):
    """The heading (0 to 4095) of something at frm that faces to, as the server reckons headings
    (world/Point2D.cs GetHeading: 0 towards +Y, turning the same way as the angles in MOVES), rounded."""
    return round(math.atan2(-(to[0] - frm[0]), to[1] - frm[1]) * 4096 / (2 * math.pi)) % 4096


def gate_spots(outer, inner):
    """The four fighters' spots at a central keep's gate, from its outer and inner door rows (x, y, z): two
    in the passage between the doors, at its midpoint, then two GATE_INSIDE inside the inner door; each pair
    GATE_SIDE to either side of the gate's line, at the inner door's height, facing out through the gate."""
    length = math.hypot(outer[0] - inner[0], outer[1] - inner[1])
    ux, uy = (outer[0] - inner[0]) / length, (outer[1] - inner[1]) / length
    sx, sy = -uy * GATE_SIDE, ux * GATE_SIDE
    mx, my = (outer[0] + inner[0]) / 2, (outer[1] + inner[1]) / 2
    ix, iy = inner[0] - GATE_INSIDE * ux, inner[1] - GATE_INSIDE * uy
    heading = facing(inner, outer)
    return [(round(x), round(y), inner[2], heading)
            for x, y in ((mx + sx, my + sy), (mx - sx, my - sy), (ix + sx, iy + sy), (ix - sx, iy - sy))]
```

- [ ] **Step 4: Run the geometry tests to verify they pass**

Run: `PYTHONPATH=deploy python3 -B -m unittest -v tests.test_battlegrounds.BattlegroundGeometryTests`

Expected (timing varies):
```
test_a_point_east_of_p_turns_by_the_angle (tests.test_battlegrounds.BattlegroundGeometryTests.test_a_point_east_of_p_turns_by_the_angle) ... ok
test_gate_spots_from_the_door_rows (tests.test_battlegrounds.BattlegroundGeometryTests.test_gate_spots_from_the_door_rows) ... ok
test_headings_wrap_at_4096 (tests.test_battlegrounds.BattlegroundGeometryTests.test_headings_wrap_at_4096) ... ok
test_p_maps_to_c (tests.test_battlegrounds.BattlegroundGeometryTests.test_p_maps_to_c) ... ok

----------------------------------------------------------------------
Ran 4 tests in 0.000s

OK
```

- [ ] **Step 5: Write the failing tests for steps 4 and 5**

In `deploy/tests/test_battlegrounds.py`, make the edits below. The last one, for `test_shipped_world`, is in the other test file.

**The scratch world.** It gets Thidranki's three portal keeps and the Hibernia portal keeps of 253 and 251, the central doors of 253 and 251, and 13 of Thidranki's keep guard rows. All are copied from the clean world. Replace:

```python
        (31, "Caer Caledon", 250, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 46, 0, "Atlas BG",
         "2022-08-18 17:36:33", "31"),
    ],
```

with:

```python
        (31, "Caer Caledon", 250, 33089, 38271, 3720, 2915, 0, 1, "", 1, 1, 1, 0, 0, 46, 0, "Atlas BG",
         "2022-08-18 17:36:33", "31"),
        # Thidranki's portal keeps (steps 4 and 5), and the Hibernia portal keeps of 253 and 251: BaseLevel 255,
        # so they are not central keeps.
        (12, "Hibernia Portal Keep", 252, 18362, 18257, 4320, 3517, 3, 1, "", 1, 1, 1, 3, 0, 255, 0,
         "Kelt;/keep create 12 255 0 Hibernia Portal Keep", "2021-12-03 21:32:39", "5e26c703-7d47-4176-961f-d87c2adf1cd1"),
        (13, "Midgard Portal Keep", 252, 54053, 24680, 4320, 346, 2, 1, "", 1, 1, 1, 2, 0, 255, 0,
         "Kelt;/keep create 13 255 0 Midgard Portal Keep", "2021-12-03 21:44:42", "5c02edf9-3350-4e30-b6ae-f6d2577a89bf"),
        (14, "Albion Portal Keep", 252, 37301, 52362, 3944, 2035, 1, 1, "", 1, 1, 1, 1, 0, 255, 0,
         "Kelt;/keep create 14 255 0 Albion Portal Keep", "2021-12-03 21:46:26", "bd9d9b80-881d-44b9-afd9-ed9bac6a559d"),
        (35, "Hibernia Portal Keep", 253, 18362, 18257, 4320, 3517, 3, 1, "", 1, 1, 1, 3, 0, 255, 0, "HPK Abermenai",
         "2021-12-03 21:32:39", "35"),
        (41, "Hibernia Portal Keep", 251, 18362, 18257, 4320, 3517, 3, 1, "", 1, 1, 1, 3, 0, 255, 0, "HPK Murdaigean",
         "2021-12-03 21:32:39", "41"),
    ],
```

Replace:

```python
        (3720, 38180, 32654, 1030, 252000302, 5200, 1, "2023-06-15 11:03:20", "91ce710c-043d-4c17-aaad-26b5046df3d0"),
    ],
```

with:

```python
        (3720, 38180, 32654, 1030, 252000302, 5200, 1, "2023-06-15 11:03:20", "91ce710c-043d-4c17-aaad-26b5046df3d0"),
        # The central doors of 253 and 251 (step 5): open, at 2,545.
        (3737, 39604, 33849, 1864, 253000301, 2545, 0, "2022-05-29 14:20:20", "cb36d60f-d020-44ac-84bd-f1e7780d5422"),
        (3720, 39059, 33659, 3901, 253000302, 2545, 0, "2022-05-29 14:20:49", "45d1b8c3-4b8e-499f-9ed0-e5e6eadecccf"),
        (3724, 37404, 32337, 3642, 251000301, 2545, 0, "2022-05-29 14:21:53", "779977ba-fca8-4898-b797-936bd3f9627d"),
        (3720, 37833, 32698, 1589, 251000302, 2545, 0, "2022-05-29 14:21:35", "5b46ea74-78fd-4c74-b969-4ba635c6ceca"),
    ],
```

Replace:

```python
        ("DOL.GS.Keeps.GuardStaticCaster", "Renegade Runemaster", 32475, 38015, 4106, 1539, 250, 507, 38, 0,
         "2022-06-21 20:04:28", "caledon-guard-23"),
    ],
```

with:

```python
        ("DOL.GS.Keeps.GuardStaticCaster", "Renegade Runemaster", 32475, 38015, 4106, 1539, 250, 507, 38, 0,
         "2022-06-21 20:04:28", "caledon-guard-23"),
        # Thidranki's Hibernia portal keep (12): the hastener and six casters that stand on its model (step 5
        # moves them), the fighter that is step 5's fighter template, and its other fighter.
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 19075, 19035, 4320, 3547, 252, 408, 1, 0, "2022-05-29 21:49:07",
         "802a1b0a-f47e-47b9-a688-e401ad33e42f"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 16751, 18401, 4736, 946, 252, 408, 1, 0, "2022-05-29 21:48:07",
         "62f874d0-333b-475f-a044-109cb0bd74b6"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 17690, 19219, 4736, 519, 252, 408, 1, 0, "2022-05-29 21:48:16",
         "be8e2cbf-6569-4c46-a4aa-d84903a902fc"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 18069, 16884, 4736, 1861, 252, 408, 1, 0, "2022-05-29 21:48:51",
         "3a07da41-d088-4174-980f-1d5ad21fc334"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 18221, 19611, 4736, 3882, 252, 408, 1, 0, "2022-05-29 21:48:21",
         "2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 18598, 17320, 4736, 2478, 252, 408, 1, 0, "2022-05-29 21:48:46",
         "b05f95a5-9e55-4ddf-93d0-340336bc2e16"),
        ("DOL.GS.Keeps.GuardStaticCaster", "new mob", 19521, 18014, 4736, 2979, 252, 408, 1, 0, "2022-05-29 21:48:31",
         "f1f1d987-1b9a-421b-a8a1-9df423f118fe"),
        ("DOL.GS.Keeps.GuardFighter", "new mob", 18983, 20057, 4080, 3493, 252, 408, 1, 0, "2022-05-29 21:49:26",
         "b67eacce-2719-48a9-8be7-1dbf0c16b7d2"),
        ("DOL.GS.Keeps.GuardFighter", "new mob", 20133, 18787, 4022, 3507, 252, 408, 1, 0, "2022-05-29 21:49:34",
         "ccaf179f-6c9e-4d09-b375-254d4429ebc2"),
        # The hasteners of Thidranki's Midgard (13) and Albion (14) portal keeps.
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 53318, 25496, 4293, 225, 252, 408, 1, 0, "2022-05-29 21:45:14",
         "9a6e81bd-4024-48e1-8703-04ab84a72f8c"),
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 37196, 51612, 3948, 2009, 252, 408, 1, 0, "2022-05-29 21:43:12",
         "bde71996-1358-42da-a6a2-1a49c7c0adf1"),
        # Thidranki Faste's hastener and its lord (step 5's lord template): keep guards, but at no portal keep.
        ("DOL.GS.Keeps.FrontierHastener", "new mob", 34365, 38483, 3720, 3180, 252, 408, 1, 0, "2022-05-29 21:52:39",
         "d558473e-fc07-4a9b-804f-26127f296afd"),
        ("DOL.GS.Keeps.GuardLord", "new mob", 32296, 38267, 4592, 1068, 252, 408, 1, 0, "2022-05-29 21:50:18",
         "863582fc-af9c-4661-8e60-4d8b2985ad2a"),
    ],
```

**What steps 4 and 5 should make.** The rows come from the spec (3.2, step 5). Replace:

```python
           "caledon-guard-25", "ecb08ffb-cf86-47f1-a53b-28581a48666b")
```

with:

```python
           "caledon-guard-25", "ecb08ffb-cf86-47f1-a53b-28581a48666b")
# The scratch world's rows that step 4 copies: the keep guards and hasteners within 4,000 of Thidranki's
# portal keeps (not the Void Merchant or the dummy that stand there, nor Thidranki Faste's guards).
PORTAL_KEEP_GUARDS = ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", "3a07da41-d088-4174-980f-1d5ad21fc334",
                      "62f874d0-333b-475f-a044-109cb0bd74b6", "802a1b0a-f47e-47b9-a688-e401ad33e42f",
                      "9a6e81bd-4024-48e1-8703-04ab84a72f8c", "b05f95a5-9e55-4ddf-93d0-340336bc2e16",
                      "b67eacce-2719-48a9-8be7-1dbf0c16b7d2", "bde71996-1358-42da-a6a2-1a49c7c0adf1",
                      "be8e2cbf-6569-4c46-a4aa-d84903a902fc", "ccaf179f-6c9e-4d09-b375-254d4429ebc2",
                      "f1f1d987-1b9a-421b-a8a1-9df423f118fe")
FIGHTER = "b67eacce-2719-48a9-8be7-1dbf0c16b7d2"  # step 5's templates: a Hibernia portal keep fighter
LORD = "863582fc-af9c-4661-8e60-4d8b2985ad2a"  # and Thidranki Faste's lord
KEEP_COLUMNS = ("KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, AlbionDifficultyLevel, "
                "MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, KeepType, BaseLevel, SkinType, "
                "CreateInfo, LastTimeRowUpdated, Keep_ID")
# Step 5's Keep rows, with every value, after a run at NOW.
NEW_KEEPS = {
    253: (32, "Dun Abermenai", 253, 33383, 38627, 3720, 3858, 0, 1, "", 1, 1, 1, 0, 0, 19, 0,
          "HearthDAoC classic-battlegrounds-v1", NOW, "hdc-bg253-dun-abermenai"),
    251: (33, "Dun Murdaigean", 251, 33113, 38138, 3720, 1583, 0, 1, "", 1, 1, 1, 0, 0, 29, 0,
          "HearthDAoC classic-battlegrounds-v1", NOW, "hdc-bg251-dun-murdaigean"),
}
# And the 12 rows of each new keep: the end of the Mob_ID (after "hdc-bg<region>-ck-"), X, Y, Z, Heading.
CENTRAL_ROWS = {
    253: [
        ("802a1b0a-f47e-47b9-a688-e401ad33e42f", 33612, 39657, 3720, 3888),  # the hastener
        ("62f874d0-333b-475f-a044-109cb0bd74b6", 31916, 37946, 4136, 1287),  # the six casters
        ("be8e2cbf-6569-4c46-a4aa-d84903a902fc", 32320, 39124, 4136, 860),
        ("3a07da41-d088-4174-980f-1d5ad21fc334", 33816, 37292, 4136, 2202),
        ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", 32584, 39729, 4136, 127),
        ("b05f95a5-9e55-4ddf-93d0-340336bc2e16", 34056, 37934, 4136, 2819),
        ("f1f1d987-1b9a-421b-a8a1-9df423f118fe", 34509, 38996, 4136, 3320),
        ("fighter-1", 33697, 39351, 3720, 3877),  # in the gate passage
        ("fighter-2", 33811, 39312, 3720, 3877),
        ("fighter-3", 33553, 38937, 3720, 3877),  # inside the inner door
        ("fighter-4", 33666, 38898, 3720, 3877),
        ("lord", 33383, 38627, 3720, 3877),
    ],
    251: [
        ("802a1b0a-f47e-47b9-a688-e401ad33e42f", 32546, 37248, 3720, 1613),
        ("62f874d0-333b-475f-a044-109cb0bd74b6", 34724, 38276, 4136, 3108),
        ("be8e2cbf-6569-4c46-a4aa-d84903a902fc", 33942, 37307, 4136, 2681),
        ("3a07da41-d088-4174-980f-1d5ad21fc334", 33163, 39541, 4136, 4023),
        ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", 33487, 36829, 4136, 1948),
        ("b05f95a5-9e55-4ddf-93d0-340336bc2e16", 32718, 39019, 4136, 544),
        ("f1f1d987-1b9a-421b-a8a1-9df423f118fe", 31929, 38176, 4136, 1045),
        ("fighter-1", 32563, 37580, 3720, 1592),
        ("fighter-2", 32472, 37657, 3720, 1592),
        ("fighter-3", 32840, 37909, 3720, 1592),
        ("fighter-4", 32749, 37986, 3720, 1592),
        ("lord", 33113, 38138, 3720, 1592),
    ],
}
```

Then add two helpers after `marks`. Replace:

```python
def marks(n):
    return ", ".join("?" * n)
```

with:

```python
def marks(n):
    return ", ".join("?" * n)


def copies(q, region, kind, rows, now):
    """(actual, expected) for the Mob rows a step added to region whose Mob_ID starts "hdc-bg<region>-<kind>-".
    rows lists the expected ones as (end of the Mob_ID, template Mob_ID, (X, Y, Z, Heading) or None): each
    is its template row with that Mob_ID, Region region, LastTimeRowUpdated now and the spot, when given."""
    names = [name for _, name, *_ in q('PRAGMA table_info("Mob")')]
    prefix = f"hdc-bg{region}-{kind}-"
    actual = q(f"SELECT {', '.join(names)} FROM Mob WHERE Mob_ID LIKE ? ORDER BY Mob_ID", (prefix + "%",))
    expected = []
    for end, template, spot in rows:
        (row,) = q(f"SELECT {', '.join(names)} FROM Mob WHERE Mob_ID=?", (template,))
        new = dict(zip(names, row), Mob_ID=prefix + end, Region=region, LastTimeRowUpdated=now)
        if spot:
            new.update(zip(("X", "Y", "Z", "Heading"), spot))
        expected.append(tuple(new[name] for name in names))
    return actual, sorted(expected, key=lambda row: row[names.index("Mob_ID")])


def central_rows(region):
    """CENTRAL_ROWS[region] as copies() takes them: a moved row copies its source, a fighter FIGHTER, the lord LORD."""
    return [(end, LORD if end == "lord" else FIGHTER if end.startswith("fighter-") else end, spot)
            for end, *spot in CENTRAL_ROWS[region]]
```

**`BattlegroundFixTests`.** Its `LINES` gets the two new lines, and `FAILURES` a failure for each new step, on the last statement it runs (the first copy into 251, then 251's inner door). Replace:

```python
        "4 gates' health)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
```

with:

```python
        "4 gates' health)",
        "Battlegrounds: portal keep guards and hasteners for Abermenai (11), Murdaigean (11)",
        "Battlegrounds: central keeps Dun Abermenai (keep 32, 12 guards), Dun Murdaigean (keep 33, 12 guards); "
        "4 central doors closed at full health",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (4 training dummies, "
```

Replace:

```python
        "_step3_keep_levels": "BEFORE UPDATE ON Door WHEN OLD.InternalID = 250000302",
```

with:

```python
        "_step3_keep_levels": "BEFORE UPDATE ON Door WHEN OLD.InternalID = 250000302",
        "_step4_portal_keep_guards": "BEFORE INSERT ON Mob WHEN NEW.Region = 251",
        "_step5_central_keeps": "BEFORE UPDATE ON Door WHEN OLD.InternalID = 251000302",
```

In `test_each_step_changes_upstream_values_and_reports`, replace:

```python
            "Caer Caledon back to level 1, 4 gates' health)",
```

with:

```python
            "Caer Caledon back to level 1, 4 gates' health)",
            "Battlegrounds: portal keep guards and hasteners for Abermenai (11), Murdaigean (11)",
            "Battlegrounds: central keeps Dun Abermenai (keep 32, 12 guards), Dun Murdaigean (keep 33, 12 guards); "
            "4 central doors closed at full health",
```

Replace:

```python
        self.assertEqual(self.q("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep ORDER BY KeepID"),
                         [(11, 24, 1, NOW), (31, 35, 1, NOW)])
```

with:

```python
        self.assertEqual(self.q("SELECT KeepID, BaseLevel, Level, LastTimeRowUpdated FROM Keep ORDER BY KeepID"), [
            (11, 24, 1, NOW), (12, 255, 1, "2021-12-03 21:32:39"), (13, 255, 1, "2021-12-03 21:44:42"),
            (14, 255, 1, "2021-12-03 21:46:26"), (31, 35, 1, NOW), (32, 19, 1, NOW), (33, 29, 1, NOW),
            (35, 255, 1, "2021-12-03 21:32:39"), (41, 255, 1, "2021-12-03 21:32:39"),
        ])
        self.assertEqual(self.q(f"SELECT {KEEP_COLUMNS} FROM Keep WHERE KeepID IN (32, 33) ORDER BY KeepID"),
                         [NEW_KEEPS[253], NEW_KEEPS[251]])
```

Replace:

```python
            (250000301, 7000, 1, NOW), (250000302, 7000, 1, NOW), (252000301, 4800, 1, NOW), (252000302, 4800, 1, NOW),
        ])
```

with:

```python
            (250000301, 7000, 1, NOW), (250000302, 7000, 1, NOW), (251000301, 5800, 1, NOW), (251000302, 5800, 1, NOW),
            (252000301, 4800, 1, NOW), (252000302, 4800, 1, NOW), (253000301, 3800, 1, NOW), (253000302, 3800, 1, NOW),
        ])
```

Replace:

```python
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob ORDER BY Mob_ID"),
                         [("125d80ca-f7b0-4b13-8340-d14d1c306a94",), ("caledon-guard-23",)])
```

with:

```python
        # Apart from the keep guards of 251 to 253, only these two rows stay.
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob WHERE Region NOT IN (251, 252, 253) "
                                "OR ClassType NOT LIKE 'DOL.GS.Keeps.%' ORDER BY Mob_ID"),
                         [("125d80ca-f7b0-4b13-8340-d14d1c306a94",), ("caledon-guard-23",)])
        # Thidranki's 13 keep guard rows stay. 253 and 251 each get a copy of its 11 portal keep rows on the
        # same spots, and their new keep's 12 rows on the spec's spots, every other column from the template.
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Region=252"), [(13,)])
        for region in (253, 251):
            with self.subTest(region=region):
                self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Region=?", (region,)), [(23,)])
                self.assertEqual(*copies(self.q, region, "pk", [(m, m, None) for m in PORTAL_KEEP_GUARDS], NOW))
                self.assertEqual(*copies(self.q, region, "ck", central_rows(region), NOW))
```

In `test_owner_values_are_kept`, the owner has also put a keep guard row into 253, made a central keep of their own in 251 with KeepID 32, and closed a central door at another health. Replace:

```python
                "DELETE FROM Quest WHERE Quest_ID='quest-cale'")
```

with:

```python
                "DELETE FROM Quest WHERE Quest_ID='quest-cale'",
                "INSERT INTO Mob (ClassType, Name, X, Y, Z, Region, Mob_ID) VALUES ('DOL.GS.Keeps.GuardFighter', "
                "'new mob', 19000, 19000, 4320, 253, 'owner-guard')",
                "INSERT INTO Keep (KeepID, Name, Region, BaseLevel, Keep_ID) VALUES (32, 'Dun Murdaigean', 251, 25, "
                "'owner-keep')",
                "UPDATE Door SET Health=3000, State=1 WHERE InternalID=253000301")
```

Replace:

```python
            "Battlegrounds: keep levels for the ranges (Caer Caledon base level 35, 3 gates' health)",
```

with:

```python
            "Battlegrounds: keep levels for the ranges (Caer Caledon base level 35, 3 gates' health)",
            "Battlegrounds: portal keep guards and hasteners for Murdaigean (11)",
            "Battlegrounds: central keep Dun Abermenai (keep 33, 12 guards); 3 central doors closed at full health",
```

Replace:

```python
        self.assertEqual(self.q("SELECT Name, Level FROM Mob WHERE Mob_ID='caledon-guard-25'"), [("Wizard", 50)])
```

with:

```python
        self.assertEqual(self.q("SELECT Name, Level FROM Mob WHERE Mob_ID='caledon-guard-25'"), [("Wizard", 50)])
        # 253 gets no portal keep copies but its central keep, with the first free KeepID; 251 keeps the owner's.
        self.assertEqual(self.q("SELECT Mob_ID FROM Mob WHERE Region=253 AND Mob_ID NOT LIKE 'hdc-bg253-ck-%'"),
                         [("owner-guard",)])
        self.assertEqual(*copies(self.q, 253, "ck", central_rows(253), NOW))
        self.assertEqual(self.q("SELECT COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg251-ck-%'"), [(0,)])
        self.assertEqual(self.q("SELECT KeepID, Name, Region, BaseLevel FROM Keep WHERE KeepID IN (32, 33) "
                                "ORDER BY KeepID"), [(32, "Dun Murdaigean", 251, 25), (33, "Dun Abermenai", 253, 19)])
        self.assertEqual(self.q("SELECT Health, State FROM Door WHERE InternalID=253000301"), [(3000, 1)])
```

Then add two tests before `test_no_marker_without_the_needed_tables`. Replace:

```python
    def test_no_marker_without_the_needed_tables(self):
```

with:

```python
    def test_a_removed_keep_comes_back_without_doubling_guards(self):
        self.assertEqual(apply_fix(self.db), self.LINES)
        mobs = self.q("SELECT * FROM Mob ORDER BY Mob_ID")
        # The owner removes Dun Abermenai's Keep row and its lord, and deletes the marker.
        execute(self.db, "DELETE FROM Keep WHERE KeepID=32", "DELETE FROM Mob WHERE Mob_ID='hdc-bg253-ck-lord'",
                "DELETE FROM fork_world_fixes")
        self.assertEqual(apply_fix(self.db, LATER), ["Battlegrounds: central keep Dun Abermenai (keep 32, 1 guard)"])
        self.assertEqual(self.q(f"SELECT {KEEP_COLUMNS} FROM Keep WHERE KeepID=32"),
                         [NEW_KEEPS[253][:-2] + (LATER, "hdc-bg253-dun-abermenai")])
        # The lord is back as it was (the last two columns are LastTimeRowUpdated and Mob_ID), and no row is
        # there twice.
        self.assertEqual(self.q("SELECT * FROM Mob ORDER BY Mob_ID"),
                         [row[:-2] + (LATER, row[-1]) if row[-1] == "hdc-bg253-ck-lord" else row for row in mobs])
        self.assertEqual(self.q("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", LATER)])

    def test_no_central_keep_without_the_rows_it_is_made_from(self):
        # Without Thidranki's Hibernia portal keep row, one of the source or template rows, or one of its central
        # doors, step 5 leaves that region as it is: no Keep row, no central rows, its doors unchanged. The fix
        # still applies and writes its marker.
        unchanged = [(251000301, 2545, 0), (251000302, 2545, 0), (253000301, 2545, 0), (253000302, 2545, 0)]
        cases = [
            ("DELETE FROM Keep WHERE KeepID=12", [], unchanged, None),
            ("DELETE FROM Mob WHERE Mob_ID='f1f1d987-1b9a-421b-a8a1-9df423f118fe'", [], unchanged, None),
            (f"DELETE FROM Mob WHERE Mob_ID='{LORD}'", [], unchanged, None),
            ("DELETE FROM Door WHERE InternalID=253000302", [(32, 251)],
             [(251000301, 5800, 1), (251000302, 5800, 1), (253000301, 2545, 0)],
             "Battlegrounds: central keep Dun Murdaigean (keep 32, 12 guards); 2 central doors closed at full health"),
        ]
        for n, (statement, keeps, doors, line) in enumerate(cases):
            with self.subTest(statement):
                db = self.world(f"missing-{n}.db")
                execute(db, statement)
                lines = apply_fix(db)
                self.assertEqual([x for x in lines if "central" in x], [line] if line else [])
                self.assertEqual(query(db, "SELECT KeepID, Region FROM Keep WHERE Keep_ID LIKE 'hdc-%'"), keeps)
                self.assertEqual(query(db, "SELECT Region, COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg%-ck-%' "
                                           "GROUP BY Region"), [(251, 12)] if keeps else [])
                self.assertEqual(query(db, "SELECT InternalID, Health, State FROM Door WHERE InternalID IN "
                                           "(251000301, 251000302, 253000301, 253000302) ORDER BY InternalID"), doors)
                self.assertEqual(query(db, "SELECT FixId FROM fork_world_fixes"), [("classic-battlegrounds-v1",)])

    def test_no_marker_without_the_needed_tables(self):
```

**`BattlegroundShippedWorldTests`.** Its `LINES` gets the two new lines. Replace:

```python
        "4 gates' health)",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
```

with:

```python
        "4 gates' health)",
        "Battlegrounds: portal keep guards and hasteners for Abermenai (34), Murdaigean (34)",
        "Battlegrounds: central keeps Dun Abermenai (keep 32, 12 guards), Dun Murdaigean (keep 33, 12 guards); "
        "4 central doors closed at full health",
        "Battlegrounds: Atlas leftovers archived in fork_removed_mobs and removed (15 training dummies, "
```

Replace:

```python
    GATES = "InternalID IN (250000301, 250000302, 252000301, 252000302)"
```

with:

```python
    GATES = "InternalID IN (250000301, 250000302, 252000301, 252000302)"
    CENTRAL_DOORS = "InternalID IN (251000301, 251000302, 253000301, 253000302)"
    # Thidranki's portal keep guards and hasteners: the rows step 4 copies.
    PORTAL_KEEP_GUARDS = ("Region=252 AND ClassType IN ('DOL.GS.Keeps.FrontierHastener', 'DOL.GS.Keeps.GuardFighter', "
                          "'DOL.GS.Keeps.GuardStaticCaster') AND EXISTS (SELECT 1 FROM Keep k WHERE k.KeepID IN (12, 13, 14) "
                          "AND (Mob.X-k.X)*(Mob.X-k.X) + (Mob.Y-k.Y)*(Mob.Y-k.Y) <= 4000*4000)")
```

At the end of `test_world_holds_what_the_steps_expect`, replace:

```python
                         [("Castle Sauvage", 1, 1, 2), ("Druim Ligen", 3, 200, 2), ("Svasud Faste", 2, 100, 2)])
```

with:

```python
                         [("Castle Sauvage", 1, 1, 2), ("Druim Ligen", 3, 200, 2), ("Svasud Faste", 2, 100, 2)])
        # Step 4: Thidranki's 34 portal keep guards and hasteners. The Hibernia (12) and Midgard (13) portal keeps
        # have 1 hastener, 2 fighters and 8 casters each, and the Albion one (14) 1, 2 and 9. 251 and 253 have
        # no Mob rows.
        self.assertEqual(self.before(
            "SELECT k.KeepID, m.ClassType, COUNT(*) FROM Mob m JOIN Keep k ON k.KeepID IN (12, 13, 14) "
            "AND (m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= 4000*4000 WHERE m.Region=252 AND m.ClassType IN "
            "('DOL.GS.Keeps.FrontierHastener', 'DOL.GS.Keeps.GuardFighter', 'DOL.GS.Keeps.GuardStaticCaster') "
            "GROUP BY k.KeepID, m.ClassType ORDER BY k.KeepID, m.ClassType"), [
            (12, "DOL.GS.Keeps.FrontierHastener", 1), (12, "DOL.GS.Keeps.GuardFighter", 2),
            (12, "DOL.GS.Keeps.GuardStaticCaster", 8),
            (13, "DOL.GS.Keeps.FrontierHastener", 1), (13, "DOL.GS.Keeps.GuardFighter", 2),
            (13, "DOL.GS.Keeps.GuardStaticCaster", 8),
            (14, "DOL.GS.Keeps.FrontierHastener", 1), (14, "DOL.GS.Keeps.GuardFighter", 2),
            (14, "DOL.GS.Keeps.GuardStaticCaster", 9),
        ])
        self.assertEqual(self.before(f"SELECT COUNT(*) FROM Mob WHERE {self.PORTAL_KEEP_GUARDS}"), [(34,)])
        self.assertEqual(self.before("SELECT COUNT(*) FROM Mob WHERE Region IN (251, 253)"), [(0,)])
        # Step 5: the Keep row it moves; no central keep in 251 or 253, and KeepIDs 32 and 33 free.
        self.assertEqual(self.before("SELECT KeepID, Region, X, Y, Z, Heading, BaseLevel FROM Keep WHERE KeepID IN "
                                     "(12, 32, 33) OR (Region IN (251, 253) AND BaseLevel < 100)"),
                         [(12, 252, 18362, 18257, 4320, 3517, 255)])
        # The Hibernia portal keep's 7 rows on its model (the hastener on the floor, the casters on the walls) and
        # the two templates: all "new mob" placeholders at level 1, model 408.
        sources = [end for end, *_ in CENTRAL_ROWS[253][:7]] + [FIGHTER, LORD]
        self.assertEqual(self.before(f"SELECT DISTINCT Name, Level, Model FROM Mob WHERE Mob_ID IN ({marks(9)})", sources),
                         [("new mob", 1, 408)])
        self.assertEqual(self.before(f"SELECT Mob_ID, ClassType, Region, X, Y, Z, Heading FROM Mob "
                                     f"WHERE Mob_ID IN ({marks(9)}) ORDER BY Mob_ID", sources), [
            ("2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a", "DOL.GS.Keeps.GuardStaticCaster", 252, 18221, 19611, 4736, 3882),
            ("3a07da41-d088-4174-980f-1d5ad21fc334", "DOL.GS.Keeps.GuardStaticCaster", 252, 18069, 16884, 4736, 1861),
            ("62f874d0-333b-475f-a044-109cb0bd74b6", "DOL.GS.Keeps.GuardStaticCaster", 252, 16751, 18401, 4736, 946),
            ("802a1b0a-f47e-47b9-a688-e401ad33e42f", "DOL.GS.Keeps.FrontierHastener", 252, 19075, 19035, 4320, 3547),
            ("863582fc-af9c-4661-8e60-4d8b2985ad2a", "DOL.GS.Keeps.GuardLord", 252, 32296, 38267, 4592, 1068),
            ("b05f95a5-9e55-4ddf-93d0-340336bc2e16", "DOL.GS.Keeps.GuardStaticCaster", 252, 18598, 17320, 4736, 2478),
            ("b67eacce-2719-48a9-8be7-1dbf0c16b7d2", "DOL.GS.Keeps.GuardFighter", 252, 18983, 20057, 4080, 3493),
            ("be8e2cbf-6569-4c46-a4aa-d84903a902fc", "DOL.GS.Keeps.GuardStaticCaster", 252, 17690, 19219, 4736, 519),
            ("f1f1d987-1b9a-421b-a8a1-9df423f118fe", "DOL.GS.Keeps.GuardStaticCaster", 252, 19521, 18014, 4736, 2979),
        ])
        # The central doors (000301 outer, 000302 inner) of 251 and 253, open at 2,545, and their Hibernia portal
        # keep doors (041601, 041602), which the door check moves.
        self.assertEqual(self.before("SELECT InternalID, X, Y, Z, Heading, Health, State FROM Door WHERE InternalID IN "
                                     "(251000301, 251000302, 251041601, 251041602, 253000301, 253000302, 253041601, "
                                     "253041602) ORDER BY InternalID"), [
            (251000301, 32337, 37404, 3724, 3642, 2545, 0), (251000302, 32698, 37833, 3720, 1589, 2545, 0),
            (251041601, 19263, 18884, 4317, 1482, 51000, 1), (251041602, 18821, 18479, 4320, 3581, 51000, 1),
            (253000301, 33849, 39604, 3737, 1864, 2545, 0), (253000302, 33659, 39059, 3720, 3901, 2545, 0),
            (253041601, 19257, 18865, 4318, 1469, 51000, 1), (253041602, 18807, 18470, 4320, 3591, 51000, 1),
        ])
```

In `test_after_the_fix`, the two new Keep rows, the four central doors and the 92 new `Mob` rows are no longer "unchanged". Replace:

```python
        for sql in ("SELECT * FROM Keep WHERE KeepID NOT IN (11, 31) ORDER BY KeepID",
                    f"SELECT * FROM Door WHERE NOT {self.GATES} ORDER BY Door_ID"):
```

with:

```python
        for sql in ("SELECT * FROM Keep WHERE KeepID NOT IN (11, 31, 32, 33) ORDER BY KeepID",
                    f"SELECT * FROM Door WHERE NOT {self.GATES} AND NOT {self.CENTRAL_DOORS} ORDER BY Door_ID"):
```

Replace:

```python
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob")[0][0], self.before("SELECT COUNT(*) FROM Mob")[0][0] - 19)
```

with:

```python
        # 19 removed; 34 portal keep rows and 12 central rows added in each of 253 and 251.
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob")[0][0],
                         self.before("SELECT COUNT(*) FROM Mob")[0][0] - 19 + 2 * (34 + 12))
```

Replace:

```python
        self.assertEqual(self.after("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", NOW)])
```

with:

```python
        self.assertEqual(self.after("SELECT FixId, AppliedUtc FROM fork_world_fixes"), [("classic-battlegrounds-v1", NOW)])
        # Step 4: in 253 and 251, a copy of each of Thidranki's 34 portal keep rows, on the same spot.
        sources = [(m, m, None) for (m,) in self.before(f"SELECT Mob_ID FROM Mob WHERE {self.PORTAL_KEEP_GUARDS}")]
        self.assertEqual(len(sources), 34)
        for region in (253, 251):
            with self.subTest(region=region):
                self.assertEqual(*copies(self.after, region, "pk", sources, NOW))
```

Then add the three new real-data tests at the end of the class. Replace:

```python
            self.assertEqual(conn.total_changes, 0)
        finally:
            conn.close()
```

with:

```python
            self.assertEqual(conn.total_changes, 0)
        finally:
            conn.close()

    def test_new_central_keeps(self):
        self.assertEqual(self.after(f"SELECT {KEEP_COLUMNS} FROM Keep WHERE KeepID IN (32, 33) ORDER BY KeepID"),
                         [NEW_KEEPS[253], NEW_KEEPS[251]])
        for region, health in ((253, 3800), (251, 5800)):
            with self.subTest(region=region):
                self.assertEqual(*copies(self.after, region, "ck", central_rows(region), NOW))
                # The central doors: closed, at full health, and within the new keep's area (3,000).
                outer, inner = region * 1000000 + 301, region * 1000000 + 302
                doors = self.after("SELECT InternalID, X, Y, Health, State, LastTimeRowUpdated FROM Door "
                                   "WHERE InternalID IN (?, ?) ORDER BY InternalID", (outer, inner))
                self.assertEqual([(door, h, s, t) for door, _, _, h, s, t in doors],
                                 [(outer, health, 1, NOW), (inner, health, 1, NOW)])
                for _, x, y, *_ in doors:
                    self.assertLessEqual(math.hypot(x - NEW_KEEPS[region][3], y - NEW_KEEPS[region][4]), 3000)

    def test_new_guards_stand_in_their_keep_area(self):
        # The server gives every keep an area (keeps/KeepArea.cs): 4,000 around a portal keep (BaseLevel 100 or
        # more) and 3,000 around a central keep. A guard belongs to the keep whose area holds it. Every new row
        # stands in exactly one area of its region: a portal keep copy in that portal keep's, a central row in
        # the new keep's.
        area = ("k.Region=m.Region AND (m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= "
                "CASE WHEN k.BaseLevel >= 100 THEN 4000*4000 ELSE 3000*3000 END")
        self.assertEqual(self.after(f"SELECT m.Region, k.KeepID, substr(m.Mob_ID, 11, 2), COUNT(*) FROM Mob m "
                                    f"JOIN Keep k ON {area} WHERE m.Mob_ID LIKE 'hdc-bg%' "
                                    f"GROUP BY m.Region, k.KeepID, substr(m.Mob_ID, 11, 2) ORDER BY m.Region DESC, k.KeepID"), [
            (253, 32, "ck", 12), (253, 35, "pk", 11), (253, 36, "pk", 11), (253, 37, "pk", 12),
            (251, 33, "ck", 12), (251, 41, "pk", 11), (251, 42, "pk", 11), (251, 43, "pk", 12),
        ])
        self.assertEqual(self.after(f"SELECT m.Mob_ID FROM Mob m WHERE m.Mob_ID LIKE 'hdc-bg%' "
                                    f"AND (SELECT COUNT(*) FROM Keep k WHERE {area}) <> 1"), [])
        self.assertEqual(self.after("SELECT COUNT(*) FROM Mob WHERE Mob_ID LIKE 'hdc-bg%'"), [(92,)])

    def test_door_check(self):
        # Spec 3.2, "Check": each region's Hibernia portal keep doors, moved like the guards, land on its central
        # doors (outer 041601 on 000301, inner 041602 on 000302).
        for region in (253, 251):
            for portal, central in ((41601, 301), (41602, 302)):
                with self.subTest(region=region, door=central):
                    (door,) = self.before("SELECT X, Y, Z, Heading FROM Door WHERE InternalID=?", (region * 1000000 + portal,))
                    (target,) = self.before("SELECT X, Y, Z, Heading FROM Door WHERE InternalID=?",
                                            (region * 1000000 + central,))
                    x, y, z, heading = battlegrounds.moved(region, *door)
                    self.assertLessEqual(math.hypot(x - target[0], y - target[1]), 50)
                    self.assertLessEqual(abs(z - target[2]), 25)
                    self.assertLessEqual(abs((heading - target[3] + 2048) % 4096 - 2048), 60)
```

**`test_shipped_world`.** In `deploy/tests/test_world_fixes.py`, replace:

```python
            "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
            "4 gates' health)",
```

with:

```python
            "Battlegrounds: keep levels for the ranges (Thidranki Faste base level 24, Caer Caledon base level 35, "
            "4 gates' health)",
            "Battlegrounds: portal keep guards and hasteners for Abermenai (34), Murdaigean (34)",
            "Battlegrounds: central keeps Dun Abermenai (keep 32, 12 guards), Dun Murdaigean (keep 33, 12 guards); "
            "4 central doors closed at full health",
```

What the new tests pin:
- **The scratch world** now has the rows steps 4 and 5 read. They include rows those steps must leave alone: the Void Merchant and the training dummy that stand inside Thidranki's Hibernia portal keep area, which are not keep guards, and Thidranki Faste's hastener, a keep guard at no portal keep. The Hibernia portal keeps of 253 and 251 have `BaseLevel` 255, so they don't count as central keeps.
- **`test_each_step_changes_upstream_values_and_reports`:** both new lines, both new `Keep` rows with every value, the 11 copies per region with every column, and the 12 central rows per region on the spec's coordinates, every other column from their template. Only the four central doors change, to 3,800 and 5,800, closed.
- **`test_a_failing_step_rolls_back_everything`:** a failure in step 4 (the first copy into 251) or step 5 (251's inner door, its last statement) leaves the database exactly as it was.
- **`test_owner_values_are_kept`:** a region with a keep guard row gets no portal keep copies. A region with a central keep of the owner's gets no new keep or central rows. The new keep takes the first free KeepID from 32 (here 33), and a central door the owner changed stays.
- **`test_a_removed_keep_comes_back_without_doubling_guards`:** with the marker deleted after the owner removed Dun Abermenai's `Keep` row and its lord, the next run puts back the `Keep` row and only the lord. Every other row is unchanged, and the run does not fail.
- **`test_no_central_keep_without_the_rows_it_is_made_from`:** step 5 skips a region when a row it is made from is missing (KeepID 12, a source, a template or a central door). The other region still gets its keep, and the marker is written.
- **`BattlegroundShippedWorldTests`:**
  - **Before:** the clean world holds Thidranki's 34 portal keep rows by keep and class, KeepID 12's row, the 9 source and template rows and the 8 door rows the steps use. 251 and 253 have no `Mob` rows and no central keep, and KeepIDs 32 and 33 are free.
  - **After one run:** 34 copies per region with every column; both `Keep` rows with every value; the 12 central rows per region on the spec's coordinates; the central doors closed, at 3,800 and 5,800, within 3,000 of their keep.
  - **Keep areas:** every one of the 92 new rows stands in exactly one keep area, its own: 11, 11 and 12 at the three portal keeps and 12 at the central keep.
  - **Door check:** each region's Hibernia portal keep doors, moved, land within 50 (X, Y), 25 (Z) and 60 (heading) of its central doors.
  - **Unchanged:** every other `Keep` and `Door` row.
- **`test_shipped_world`:** `world_fixes.apply` on the clean world returns its 3 lines and the 6 battleground lines.

- [ ] **Step 6: Run the tests to verify they fail**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db PYTHONPATH=deploy python3 -B -m unittest -v tests.test_battlegrounds tests.test_world_fixes`

Expected: 11 tests fail, because steps 4 and 5 do not exist yet. Timing varies:
```
test_a_failing_step_rolls_back_everything (tests.test_battlegrounds.BattlegroundFixTests.test_a_failing_step_rolls_back_everything) ... FAIL
test_a_removed_keep_comes_back_without_doubling_guards (tests.test_battlegrounds.BattlegroundFixTests.test_a_removed_keep_comes_back_without_doubling_guards) ... FAIL
test_each_step_changes_upstream_values_and_reports (tests.test_battlegrounds.BattlegroundFixTests.test_each_step_changes_upstream_values_and_reports) ... FAIL
test_no_central_keep_without_the_rows_it_is_made_from (tests.test_battlegrounds.BattlegroundFixTests.test_no_central_keep_without_the_rows_it_is_made_from) ... 
  test_no_central_keep_without_the_rows_it_is_made_from (tests.test_battlegrounds.BattlegroundFixTests.test_no_central_keep_without_the_rows_it_is_made_from) [DELETE FROM Door WHERE InternalID=253000302] ... FAIL
test_no_marker_without_the_needed_tables (tests.test_battlegrounds.BattlegroundFixTests.test_no_marker_without_the_needed_tables) ... ok
test_owner_values_are_kept (tests.test_battlegrounds.BattlegroundFixTests.test_owner_values_are_kept) ... FAIL
test_removed_rows_are_archived (tests.test_battlegrounds.BattlegroundFixTests.test_removed_rows_are_archived) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundFixTests.test_second_run_changes_nothing) ... FAIL
test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails (tests.test_battlegrounds.BattlegroundFixTests.test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails) ... FAIL
test_a_point_east_of_p_turns_by_the_angle (tests.test_battlegrounds.BattlegroundGeometryTests.test_a_point_east_of_p_turns_by_the_angle) ... ok
test_gate_spots_from_the_door_rows (tests.test_battlegrounds.BattlegroundGeometryTests.test_gate_spots_from_the_door_rows) ... ok
test_headings_wrap_at_4096 (tests.test_battlegrounds.BattlegroundGeometryTests.test_headings_wrap_at_4096) ... ok
test_p_maps_to_c (tests.test_battlegrounds.BattlegroundGeometryTests.test_p_maps_to_c) ... ok
test_after_the_fix (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_after_the_fix) ... FAIL
test_door_check (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_door_check) ... ok
test_new_central_keeps (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_new_central_keeps) ... FAIL
test_new_guards_stand_in_their_keep_area (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_new_guards_stand_in_their_keep_area) ... FAIL
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_second_run_changes_nothing) ... ok
test_world_holds_what_the_steps_expect (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_world_holds_what_the_steps_expect) ... ok
test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone) ... ok
test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste) ... ok
test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork) ... ok
test_keeps_other_separators_and_empty (tests.test_world_fixes.EnableClassesTests.test_keeps_other_separators_and_empty) ... ok
test_removes_a_single_id (tests.test_world_fixes.EnableClassesTests.test_removes_a_single_id) ... ok
test_splits_a_range (tests.test_world_fixes.EnableClassesTests.test_splits_a_range) ... ok
test_apply_enables_disciple_and_adds_the_saracen_start (tests.test_world_fixes.FixesTests.test_apply_enables_disciple_and_adds_the_saracen_start) ... ok
test_cli (tests.test_world_fixes.FixesTests.test_cli) ... ok
test_owner_choices_are_kept (tests.test_world_fixes.FixesTests.test_owner_choices_are_kept) ... ok
test_second_run_changes_nothing (tests.test_world_fixes.FixesTests.test_second_run_changes_nothing) ... ok
test_shipped_world (tests.test_world_fixes.FixesTests.test_shipped_world) ... FAIL
test_welcome_messages_name_hearthdaoc (tests.test_world_fixes.FixesTests.test_welcome_messages_name_hearthdaoc) ... ok
...
FAIL: test_a_failing_step_rolls_back_everything (tests.test_battlegrounds.BattlegroundFixTests.test_a_failing_step_rolls_back_everything)
...
AssertionError: Lists differ: ['_st[70 chars]_step6_atlas_leftovers'] != ['_st[70 chars]_step4_portal_keep_guards', '_step5_central_ke[26 chars]ers']

First differing element 3:
'_step6_atlas_leftovers'
'_step4_portal_keep_guards'

Second list contains 2 additional elements.
First extra element 4:
'_step5_central_keeps'
...
Ran 31 tests in 3.946s

FAILED (failures=11)
```

- [ ] **Step 7: Write steps 4 and 5**

In `deploy/bin/battlegrounds.py`, the docstring describes the new steps. Replace:

```python
Each step changes a value only while it still holds upstream's value, and adds one line to the result
when it changed something:
```

with:

```python
Each step changes a value only while it still holds upstream's value, adds rows only where none of their
kind are there yet, and adds one line to the result when it changed something:
```

Replace:

```python
3. Thidranki Faste and Caer Caledon get base levels for their ranges and keep Level 1, and their gates
   the matching health;
```

with:

```python
3. Thidranki Faste and Caer Caledon get base levels for their ranges and keep Level 1, and their gates
   the matching health;
4. Abermenai and Murdaigean, which have no guards, get a copy of Thidranki's portal keep guards and
   hasteners (all four battlegrounds share one map and the same portal keep spots);
5. they also get a central keep each, Dun Abermenai and Dun Murdaigean, held by renegades. Its guards
   are Thidranki's Hibernia portal keep guards moved onto the central keep model (moved()), four
   fighters at its gate (gate_spots()) and a lord. They are added only in the run that adds the keep's
   Keep row, and only those not there yet. Its doors are closed at full health;
```

Add the constants of steps 4 and 5 right after `IN_BATTLEGROUNDS`. Replace:

```python
IN_BATTLEGROUNDS = "Region BETWEEN 250 AND 253"
```

with:

```python
IN_BATTLEGROUNDS = "Region BETWEEN 250 AND 253"

# Step 4: Thidranki's portal keep guards and hasteners are the Mob rows of these classes in this region
# within the portal keep area's radius (keeps/KeepArea.cs) of these KeepIDs.
PORTAL_KEEP_SOURCE = (252, (12, 13, 14), 4000)   # region, KeepIDs, radius
KEEP_GUARD_CLASSES = ("DOL.GS.Keeps.FrontierHastener", "DOL.GS.Keeps.GuardFighter", "DOL.GS.Keeps.GuardStaticCaster")
# Step 5, by region, in the order they are added: the central keep's Name, Keep_ID and BaseLevel (the top
# of the range). Its KeepID is the first free one from FIRST_KEEP_ID.
CENTRAL_KEEPS = {253: ("Dun Abermenai", "hdc-bg253-dun-abermenai", 19),
                 251: ("Dun Murdaigean", "hdc-bg251-dun-murdaigean", 29)}
FIRST_KEEP_ID = 32
KEEP_CREATE_INFO = "HearthDAoC classic-battlegrounds-v1"
PORTAL_KEEP_HIB = 12          # its Keep row is moved to make the central Keep row's X, Y, Z, Heading
# The Hibernia portal keep's rows that stand on its model, moved onto the central keep: its hastener (on
# the floor, Z 4320) and its six casters on the walls (Z 4736).
CENTRAL_SOURCES = ("802a1b0a-f47e-47b9-a688-e401ad33e42f",
                   "62f874d0-333b-475f-a044-109cb0bd74b6", "be8e2cbf-6569-4c46-a4aa-d84903a902fc",
                   "3a07da41-d088-4174-980f-1d5ad21fc334", "2fc59f4b-0b0d-4efc-bf3b-93a1b01e681a",
                   "b05f95a5-9e55-4ddf-93d0-340336bc2e16", "f1f1d987-1b9a-421b-a8a1-9df423f118fe")
FIGHTER_TEMPLATE = "b67eacce-2719-48a9-8be7-1dbf0c16b7d2"   # a Hibernia portal keep fighter
LORD_TEMPLATE = "863582fc-af9c-4661-8e60-4d8b2985ad2a"      # Thidranki Faste's lord
# The central doors, by region: outer and inner Door.InternalID, then Health as upstream ships it and the
# keep's full health (BaseLevel x keep_doors_base_health 200). Their State goes from 0 (open) to 1 (closed).
CENTRAL_DOORS = {253: (253000301, 253000302, 2545, 3800), 251: (251000301, 251000302, 2545, 5800)}
```

Add the two steps between steps 3 and 6. Replace:

```python
def _step6_atlas_leftovers(conn, now):
```

with:

```python
def _step4_portal_keep_guards(conn, now):
    source, keeps, radius = PORTAL_KEEP_SOURCE
    items = []
    for region in CENTRAL_KEEPS:
        if conn.execute("SELECT 1 FROM Mob WHERE Region=? AND ClassType LIKE 'DOL.GS.Keeps.%'", (region,)).fetchone():
            continue  # it has keep guards already: the owner's, or an earlier run's
        added = _copy_mobs(conn, {"Region": ("?", region), "Mob_ID": ("? || m.Mob_ID", f"hdc-bg{region}-pk-"),
                                  "LastTimeRowUpdated": ("?", now)},
                           "m.Region=? AND m.ClassType IN (?, ?, ?) AND EXISTS (SELECT 1 FROM Keep k WHERE "
                           "k.Region=m.Region AND k.KeepID IN (?, ?, ?) AND "
                           "(m.X-k.X)*(m.X-k.X) + (m.Y-k.Y)*(m.Y-k.Y) <= ?)",
                           (source, *KEEP_GUARD_CLASSES, *keeps, radius * radius))
        if added:
            items.append(f"{NAMES[region]} ({added})")
    return "Battlegrounds: portal keep guards and hasteners for " + ", ".join(items) if items else None


def _step5_central_keeps(conn, now):
    keeps, doors = [], 0
    for region, (name, keep_key, base_level) in CENTRAL_KEEPS.items():
        outer, inner, old_health, new_health = CENTRAL_DOORS[region]
        portal_keep = conn.execute("SELECT X, Y, Z, Heading FROM Keep WHERE KeepID=?", (PORTAL_KEEP_HIB,)).fetchone()
        spots = {door: conn.execute("SELECT X, Y, Z FROM Door WHERE InternalID=?", (door,)).fetchone()
                 for door in (outer, inner)}
        sources = {mob_id: conn.execute("SELECT X, Y, Z, Heading FROM Mob WHERE Mob_ID=?", (mob_id,)).fetchone()
                   for mob_id in CENTRAL_SOURCES + (FIGHTER_TEMPLATE, LORD_TEMPLATE)}
        if portal_keep is None or None in spots.values() or None in sources.values():
            continue  # a row the keep is made from is missing: leave this region as it is
        if not conn.execute("SELECT 1 FROM Keep WHERE Region=? AND BaseLevel<100", (region,)).fetchone():
            keep_id = FIRST_KEEP_ID
            while conn.execute("SELECT 1 FROM Keep WHERE KeepID=?", (keep_id,)).fetchone():
                keep_id += 1
            x, y, z, heading = moved(region, *portal_keep)
            conn.execute("INSERT INTO Keep (KeepID, Name, Region, X, Y, Z, Heading, Realm, Level, ClaimedGuildName, "
                         "AlbionDifficultyLevel, MidgardDifficultyLevel, HiberniaDifficultyLevel, OriginalRealm, "
                         "KeepType, BaseLevel, SkinType, CreateInfo, LastTimeRowUpdated, Keep_ID) "
                         "VALUES (?, ?, ?, ?, ?, ?, ?, 0, 1, '', 1, 1, 1, 0, 0, ?, 0, ?, ?, ?)",
                         (keep_id, name, region, x, y, z, heading, base_level, KEEP_CREATE_INFO, now, keep_key))
            gate = gate_spots(spots[outer], spots[inner])
            rows = [(f"hdc-bg{region}-ck-{mob_id}", mob_id, moved(region, *sources[mob_id]))
                    for mob_id in CENTRAL_SOURCES]
            rows += [(f"hdc-bg{region}-ck-fighter-{n}", FIGHTER_TEMPLATE, spot) for n, spot in enumerate(gate, 1)]
            rows.append((f"hdc-bg{region}-ck-lord", LORD_TEMPLATE, (x, y, z, gate[0][3])))  # facing the gate
            guards = 0
            for new_id, template, (gx, gy, gz, gh) in rows:
                guards += _copy_mobs(conn, {"Region": ("?", region), "Mob_ID": ("?", new_id),
                                            "LastTimeRowUpdated": ("?", now), "X": ("?", gx), "Y": ("?", gy),
                                            "Z": ("?", gz), "Heading": ("?", gh)},
                                     "m.Mob_ID=? AND NOT EXISTS (SELECT 1 FROM Mob WHERE Mob_ID=?)", (template, new_id))
            keeps.append(f"{name} (keep {keep_id}, {_count(guards, 'guard', 'guards')})")
        doors += conn.execute("UPDATE Door SET Health=CASE WHEN Health=? THEN ? ELSE Health END, "
                              "State=CASE WHEN State=0 THEN 1 ELSE State END, LastTimeRowUpdated=? "
                              "WHERE InternalID IN (?, ?) AND (Health=? OR State=0)",
                              (old_health, new_health, now, outer, inner, old_health)).rowcount
    parts = []
    if keeps:
        parts.append(("central keep " if len(keeps) == 1 else "central keeps ") + ", ".join(keeps))
    if doors:
        parts.append(_count(doors, "central door", "central doors") + " closed at full health")
    return "Battlegrounds: " + "; ".join(parts) if parts else None


def _step6_atlas_leftovers(conn, now):
```

Replace:

```python
STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step6_atlas_leftovers)
```

with:

```python
STEPS = (_step1_battleground_rows, _step2_names_and_xp, _step3_keep_levels, _step4_portal_keep_guards,
         _step5_central_keeps, _step6_atlas_leftovers)
```

Add the copy helper before `_count`. Replace:

```python
def _count(n, one, many):
```

with:

```python
def _copy_mobs(conn, replace, where, params):
    """Add a copy of each Mob row m that matches where. Every column is copied as it is, except those in
    replace, which maps a column to (an SQL expression with one ?, its value). Returns how many rows it added."""
    names = [name for _, name, *_ in conn.execute('PRAGMA table_info("Mob")')]
    select = ", ".join(replace[name][0] if name in replace else f'm."{name}"' for name in names)
    values = tuple(replace[name][1] for name in names if name in replace)
    columns = ", ".join(f'"{name}"' for name in names)
    return conn.execute(f"INSERT INTO Mob ({columns}) SELECT {select} FROM Mob m WHERE {where}",
                        values + tuple(params)).rowcount


def _count(n, one, many):
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db PYTHONPATH=deploy python3 -B -m unittest -v tests.test_battlegrounds tests.test_world_fixes`

Expected (timing varies):
```
test_a_failing_step_rolls_back_everything (tests.test_battlegrounds.BattlegroundFixTests.test_a_failing_step_rolls_back_everything) ... ok
test_a_removed_keep_comes_back_without_doubling_guards (tests.test_battlegrounds.BattlegroundFixTests.test_a_removed_keep_comes_back_without_doubling_guards) ... ok
test_each_step_changes_upstream_values_and_reports (tests.test_battlegrounds.BattlegroundFixTests.test_each_step_changes_upstream_values_and_reports) ... ok
test_no_central_keep_without_the_rows_it_is_made_from (tests.test_battlegrounds.BattlegroundFixTests.test_no_central_keep_without_the_rows_it_is_made_from) ... ok
test_no_marker_without_the_needed_tables (tests.test_battlegrounds.BattlegroundFixTests.test_no_marker_without_the_needed_tables) ... ok
test_owner_values_are_kept (tests.test_battlegrounds.BattlegroundFixTests.test_owner_values_are_kept) ... ok
test_removed_rows_are_archived (tests.test_battlegrounds.BattlegroundFixTests.test_removed_rows_are_archived) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundFixTests.test_second_run_changes_nothing) ... ok
test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails (tests.test_battlegrounds.BattlegroundFixTests.test_world_fixes_commits_its_own_fixes_and_exits_0_when_the_fix_fails) ... ok
test_a_point_east_of_p_turns_by_the_angle (tests.test_battlegrounds.BattlegroundGeometryTests.test_a_point_east_of_p_turns_by_the_angle) ... ok
test_gate_spots_from_the_door_rows (tests.test_battlegrounds.BattlegroundGeometryTests.test_gate_spots_from_the_door_rows) ... ok
test_headings_wrap_at_4096 (tests.test_battlegrounds.BattlegroundGeometryTests.test_headings_wrap_at_4096) ... ok
test_p_maps_to_c (tests.test_battlegrounds.BattlegroundGeometryTests.test_p_maps_to_c) ... ok
test_after_the_fix (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_after_the_fix) ... ok
test_door_check (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_door_check) ... ok
test_new_central_keeps (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_new_central_keeps) ... ok
test_new_guards_stand_in_their_keep_area (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_new_guards_stand_in_their_keep_area) ... ok
test_second_run_changes_nothing (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_second_run_changes_nothing) ... ok
test_world_holds_what_the_steps_expect (tests.test_battlegrounds.BattlegroundShippedWorldTests.test_world_holds_what_the_steps_expect) ... ok
test_battleground_quests_are_gone (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_battleground_quests_are_gone) ... ok
test_keep_manager_names_svasud_faste (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_keep_manager_names_svasud_faste) ... ok
test_porter_blocks_call_the_fork (tests.test_battlegrounds.ClassicBattlegroundSourceTests.test_porter_blocks_call_the_fork) ... ok
test_keeps_other_separators_and_empty (tests.test_world_fixes.EnableClassesTests.test_keeps_other_separators_and_empty) ... ok
test_removes_a_single_id (tests.test_world_fixes.EnableClassesTests.test_removes_a_single_id) ... ok
test_splits_a_range (tests.test_world_fixes.EnableClassesTests.test_splits_a_range) ... ok
test_apply_enables_disciple_and_adds_the_saracen_start (tests.test_world_fixes.FixesTests.test_apply_enables_disciple_and_adds_the_saracen_start) ... ok
test_cli (tests.test_world_fixes.FixesTests.test_cli) ... ok
test_owner_choices_are_kept (tests.test_world_fixes.FixesTests.test_owner_choices_are_kept) ... ok
test_second_run_changes_nothing (tests.test_world_fixes.FixesTests.test_second_run_changes_nothing) ... ok
test_shipped_world (tests.test_world_fixes.FixesTests.test_shipped_world) ... ok
test_welcome_messages_name_hearthdaoc (tests.test_world_fixes.FixesTests.test_welcome_messages_name_hearthdaoc) ... ok

----------------------------------------------------------------------
Ran 31 tests in 5.072s

OK
```
No test may be skipped: the six real-world tests and `test_shipped_world` must show `... ok`.

- [ ] **Step 9: Run the whole deploy suite and check the tree**

Run: `HDC_TEST_WORLD=~/Games/OfflineDAoC-archive/test-world/clean-classic-0.34.db python3 -B -m unittest discover -s deploy/tests -t deploy`

Expected (takes about 30 s):
```
..............................s..................................................................................................................ssss..........
----------------------------------------------------------------------
Ran 159 tests in 31.837s

OK (skipped=5)
```
The five skips are the PowerShell bundle test (no `pwsh`) and the four `test_world_admin` upgrade tests (they also need `HDC_TOOLS`). No test may fail.

Run: `git status --short --untracked-files=all`

Expected:
```
 M deploy/bin/battlegrounds.py
 M deploy/tests/test_battlegrounds.py
 M deploy/tests/test_world_fixes.py
```

- [ ] **Step 10: Commit**

```bash
git add deploy/bin/battlegrounds.py deploy/tests/test_battlegrounds.py deploy/tests/test_world_fixes.py
git commit -q -m "feat(deploy): keeps and guards for Abermenai and Murdaigean

battlegrounds.py gives the two empty battlegrounds what Thidranki has.
Step 4 copies Thidranki's 34 portal keep guards and hasteners to
Abermenai and Murdaigean, which share its map and its portal keep
spots, while those regions have no keep guards. Step 5 adds the
central keeps Dun Abermenai (keep 32, base level 19) and Dun
Murdaigean (keep 33, base level 29), held by renegades: the Hibernia
portal keep's hastener and six wall-top casters turned and moved onto
the central keep model, four fighters at the gate and a lord, and the
central doors closed at full health (3,800 and 5,800). The guards are
added only in the run that adds the Keep row, and only those not there
yet, so a run after the owner removed a keep and the marker brings the
keep back without doubling its guards.

Tests cover the move and the gate spots; each step's rows and line on
a scratch world; a rollback for a failure in steps 4 and 5; owner rows;
a region missing a row its keep is made from; a removed keep coming
back; and, on the clean classic world (HDC_TEST_WORLD), the 34 + 12
rows per region, both Keep rows, every new guard in its own keep area,
the central doors and the door check.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git show --stat --format=%s HEAD
```

Expected:
```
feat(deploy): keeps and guards for Abermenai and Murdaigean

 deploy/bin/battlegrounds.py        | 155 +++++++++++++++-
 deploy/tests/test_battlegrounds.py | 365 ++++++++++++++++++++++++++++++++++++-
 deploy/tests/test_world_fixes.py   |   3 +
 3 files changed, 512 insertions(+), 11 deletions(-)
```

---

### Task 5: In-game verification (owner) and record

**Files:**
- Create: `docs/fork/verification/sub5-ingame.md`

**Interfaces:**
- Consumes: a server running this branch's image. For the owner, that means the release from merging the PR, followed by `./hdc update`. The battleground fix runs once per world, at the first start after the update.
- Produces: the verification record that closes issue #76.

The owner does this task in the real client. The agent writes the record from the owner's reports.

- [ ] **Step 1: Check the server log of the first start after the update**

Run (on the server): `docker logs hearthdaoc-server 2>&1 | grep -E 'Classic battlegrounds|Battlegrounds:|Could not find quest'`

Expected:
- the summary line `Classic battlegrounds: Abermenai 15-19 up to 1L2, Thidranki 20-24 up to 1L3, Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9`;
- at this first start, one `Battlegrounds: …` line per fix step (limits, names and XP, keep levels, Atlas leftovers, portal keep guards, central keeps);
- no `Classic battlegrounds: not applied` line, and no `Could not find quest` line.

Also check that the log has no door or guard load warnings for regions 251 and 253.

- [ ] **Step 2: Ask the owner to run the in-game checks** (spec section 4). Use normal player accounts at the right levels; GM commands are fine for setting levels and realm points between checks. Report each check as pass or fail, with what was seen:

1. **Abermenai and Murdaigean:**
   - a level 15–19 character with the medallion arrives beside its portal keep (25–29 for Murdaigean);
   - the portal keep guards and the hastener are there;
   - `/ck` lists "Dun Abermenai: None" ("Dun Murdaigean: None").
2. **At each new central keep:**
   - the casters stand on the walls, neither floating nor inside a wall;
   - the fighters stand at the gate, and the lord stands inside on the floor, reachable once both doors are down;
   - the gate is closed and can be broken;
   - guards are about level 21 (31 in Murdaigean) and the lord about 24 (36);
   - Dun Murdaigean's gate faces the Hibernia portal keep.
3. **Capture:** killing the lord gives the keep to the realm, the guards come back at the same levels (not five higher), and `/ck` shows the new owner.
4. **Thidranki:**
   - level 20–24 with under 350 realm points gets in;
   - with 350 or more, the porter explains why not, once per ceremony (not again at the second port 10 seconds later);
   - guards are about 26 and the lord about 31;
   - there are no dummies, Void Merchants or Pazz.
5. **Caledonia:**
   - level 30 gets in, and level 36 is refused with the text;
   - `/who` shows Caledonia;
   - guards are about 37 and the lord about 44;
   - there is no Wizard, Void Merchant or Pazz.
6. **Over the limit:**
   - a character that reaches level 20 in Abermenai stays there;
   - after `/quit`, it logs in at its bind point, and the porter refuses it;
   - closing the client inside instead (a link death) gives the move and the message a moment after the next login;
   - one Midgard character does the same.
7. **Death:** release in a battleground goes to the bind point.

- [ ] **Step 3: Record the results**

Create `docs/fork/verification/sub5-ingame.md` in the format of `docs/fork/verification/sub3-ingame.md`: one row per check (check, expected, actual, pass/fail), plus the server release used. A failure goes back to the task that owns it before the issue is closed. The zone XP bonus is not checked in game; the real-data test checks `Experience` 0.

- [ ] **Step 4: Commit**

```bash
git add docs/fork/verification/sub5-ingame.md
git commit -q -m "docs(fork): in-game verification of the classic battlegrounds

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
