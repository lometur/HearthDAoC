# About this fork

HearthDAoC (`lometur/HearthDAoC`) is an unofficial fork of [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).
It runs OfflineDAoC as a central multiplayer server and keeps its own changes small and additive,
so upstream updates merge cleanly.

The fork was renamed from its working title ("OfflineDAoC central server fork") to HearthDAoC on
2026-10-05, before its first release. The sub-project 1 spec, plan and verification record keep the
old names (`odc`, `OFFLINEDAOC_*`, `offlinedaoc-*`) as written at the time.

## What the fork changes

| Change | Paths | Upstream files touched |
|---|---|---|
| Fork banner | `README.md` (top lines), `.github/README.md` | `README.md` |
| Container deployment | `deploy/`, `.dockerignore`, `.github/workflows/server-image.yml` | none |
| Linux admin CLIs | `tools/linux/` (link upstream sources; see each project file) | none |
| Bot goals with upstream's Battlegrounds column (`hdc bot-goals`) | `tools/linux/bot-goals/Program.cs`, `deploy/hdc` | none: `set` takes Battlegrounds as an optional fourth number and keeps it when left out; the owner's split is in `deploy/HANDOFF.md` (Bot goals), set by hand, not by default |
| Server data files from the release (`classic-quests.json`, `classic-quest-guides.json`) | `deploy/upstream.lock` (`server_prefix`, `server_files`), `deploy/bin/init_world.py`, `deploy/entrypoint.sh` | none: not in the image. The first start of an upstream version downloads them, checked against the release manifest, into `/data/server-files/<upstream version>/`, and every start links them into `/app/server`. `classic-otd.json` is left out: nothing reads it |
| World upgrade in `hdc update` | `deploy/hdc`, `deploy/bin/world_admin.py` | none: a release for another upstream version runs `./hdc upgrade-world` (the new release's own) before it starts the server |
| What a world upgrade keeps besides upstream's progress tables, and `hdc carry-rvr` | `deploy/bin/world_admin.py`, `deploy/bin/carry_rvr.py`, `deploy/hdc` | none: upstream's import engine copies only the progress tables of `progress-policy.json` and clears `Ban`, `SinglePermission` and `KeepCaptureLog`. `upgrade-world` then copies bans and single permissions, and the RvR state that came from play, so upstream's own changes to untouched keeps and relics stay: for keeps in play in the old world (Realm is not OriginalRealm, or claimed; matched by Name and Region, not KeepID), `Keep` Realm, Level and ClaimedGuildName, Health and State of their `Door` rows (both from the old row; Health at most the door's full health in the new world at the carried Level, by the server's formula), and their `KeepHookPointItem` rows (with the new KeepID); for relics away from home, `Relic` position and holder; and `KeepCaptureLog`. A table or needed column missing in either world leaves that part out, with a note in the report. `hdc carry-rvr <archive>` copies the same state from an archived world into the live one, after a backup, leaving out a claim whose guild holds another keep there. `battlegrounds.py` step 3 lowers a central keep's gates to Level 1's full health when it puts the keep back to Level 1 |
| Player setup, and Linux client updates at launch (see Client updates) | `client/`, `deploy/build_bundles.sh` (the bundle's `VERSION`) | none |
| Classic character creation and splash (client patch set) | `client/patches/`, `client/windows/patch-client.*` | none: patches each player's own client files (see Client patches) |
| Leveling spawns (owner's choice) | `deploy/bin/spawns.py`, `hdc spawns` | none: restores rows upstream's setup archived in `offline_classic165_removed_mobs` |
| Classic battlegrounds 15-35, world data (once per world, marker `classic-battlegrounds-v2`) | `deploy/bin/battlegrounds.py` (run by `deploy/bin/world_fixes.py`) | none: on the 0.35 world it levels all four central keeps (upstream's Dun Abermenai and Dun Murdaigean included) and their gates, adds the portal keep guards, and six wall casters and a hastener in each of upstream's two keeps, and removes the Atlas leftovers ([spec](specs/2026-10-07-classic-battlegrounds-design.md), section 7) |
| Design docs | `docs/fork/` | none |

Server code under `source/server` was unchanged in sub-project 1. Every later server-code change is
listed here, so upstream syncs can account for it:

| Change | Files | Why | Upstream |
|---|---|---|---|
| `/rp off` at any level | `GameServer/commands/playercommands/rp.cs` (the level check removed, marked `// HearthDAoC:`), source check `deploy/tests/test_player_commands.py` | A player under a battleground's realm point cap can stop gaining realm points and stay in (owner 2026-10-09); OpenDAoC allowed it only from level 40. A quest that gives realm points can't be finished while it is off (upstream's rule) | Fork-only |
| `command_plvl_overrides` server property (e.g. `/tele=2;/tc=2`) | `GameServer/gameutils/ScriptMgr.cs` (`CommandPrivLevel`, applied in `LoadCommands`), `GameServer/serverproperty/ServerProperties.cs`, test `Tests/UnitTests/UT_CommandPrivLevelOverrides.cs` | Makes single-player teleports GM-only on a shared server (#38); set from `HEARTHDAOC_GM_ONLY_COMMANDS` | Candidate: generic, off by default |
| Shrouded Isles start choice: `si_start_choice` server property | `GameServer/scripts/hearthdaoc/SiStartChoice.cs` (the decisions), `GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs` (the property and the game wiring), test `Tests/UnitTests/UT_SiStartChoice.cs`. Upstream files touched: none | A new character of a classic race is asked once, a few seconds after its first entry into the world, whether to begin in its realm's Shrouded Isles town (#40, [spec](specs/2026-10-06-shrouded-isles-start-choice-design.md)); set from `HEARTHDAOC_SI_START_CHOICE` (default on) | Candidate: off by default |
| Classic battlegrounds: porter levels and caps, realm rank caps on every way in (players and bots), over-limit moves, keep level after a capture | `GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs` (the decisions), `GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs` (the hooks and the game wiring), test `Tests/UnitTests/UT_ClassicBattlegrounds.cs`, source checks `deploy/tests/test_battlegrounds.py`. Upstream files touched, each hook marked `// HearthDAoC:` (in `OFTeleporters.cs`, by the `HearthDAoC.` namespace in the call, since the source check wants each block to be the call and `break;` only): `GameServer/scripts/teleporters/OFTeleporters.cs` (three blocks: the frontier porter), `GameServer/scripts/teleporters/BattlegroundTeleportOptions.cs` (one block: the town teleporters' [Battlegrounds] choice), `GameServer/keeps/KeepManager.cs` (the cap in `GetBGPK`), `GameServer/bots/autonomous/AutonomousBotGoalPolicy.cs` (one: `ReconcileSavedAssignment`, the new goal for a saved record), `GameServer/bots/autonomous/AutonomousObjectiveAssignments.cs` (two: `CanTakeBattleground`, `Assign`), `GameServer/bots/autonomous/AutonomousWorldBotController.Battleground.cs` (one: `ExecuteBattleground`), `GameServer/bots/autonomous/AutonomousWorldBotController.cs` (two: the camp offer in `SelectCamp`, `TravelAcrossRegions`; both check every member of a shared party), and the twelve battleground quest files in `GameServer/scripts/quests/BattlegroundQuests/` (Thidranki and Caledonia), deleted (nothing left to mark; the source check finds no class of theirs) | The four classic battlegrounds for levels 15-35 with their realm rank caps, the porter's reasons, over-limit characters at their bind point, no Atlas daily quests (#76, [spec](specs/2026-10-07-classic-battlegrounds-design.md)). Since upstream 0.35 (#50), the caps hold on every way in: the frontier porter, the town teleporters' [Battlegrounds] choice (the porter's refusal text; game masters are exempt, as from upstream's level check) and `GetBGPK`. A gamebot at or over a battleground's cap gets no battleground goal for it and is never sent in; one already inside stays until it leaves or dies. Upstream's `BattlegroundBrackets` still says level is the only limit: the fork adds the caps | Fork-only. The "Svasud Faste" fix that was a candidate is upstream's own since 0.35 |

## Client patches

The patch set `client/patches/classic-creation.json` gives the OfflineDAoC 0.35 classic client a classic
character creation screen and the HearthDAoC loading splash (sub-project 2, see
[its spec](specs/2026-10-06-classic-character-creation-design.md)). It holds only SHA-256 hashes, byte and text edits
and our own code, never an EA file. The launchers apply it at every launch, just before the game starts, so
files that something put back (a repair, OfflineDAoC's own launcher) are patched again:

- Linux: `setup.sh` installs the bundle's `patches/` (`apply_patches.py`, `patchset.py`, `classic-creation.json`,
  `splash.mpk`) as `<dest>/patches`, a fresh copy on every run, and applies it from there. `play.sh` runs
  `<dest>/patches/apply_patches.py` before every launch.
- Windows: `connect-hearthdaoc.bat` runs `patch-client.ps1` (`client/windows/`, same rules as the Linux
  applier) from its own folder before every start of `connect.exe`. `patch-client.bat` runs it by hand, for
  example `-Restore`, or once as administrator when the install is under Program Files.

An already patched client is only read. Exit 3 (a client file the patch set doesn't know) starts the game with
the standard creation screen; any other failure warns and still starts it. Without the installed patches
(`<dest>/patches`, or `patch-client.ps1` next to the .bat) the launchers don't patch: that is how a player opts
out, after a restore. Both appliers refuse any other client file, such as the b edition's `game.dll` or an older
or newer upstream client's, and then change nothing. Both keep each original as `<file>.hearthdaoc-orig` and put it back with `--restore` /
`-Restore`, but only over the patched file: when a file has changed since it was patched (for example a newer
upstream client), the restore says so and changes nothing.

| What is patched | Where | Why |
|---|---|---|
| Auto-assign stops after its reset: race base stats and 30 points to place (P1) | `game.dll`, VA `0x59C0B2` | Classic stat points (#39) |
| Continue checks unspent points for new characters too (P2) | `game.dll`, VA `0x59A853` (28 bytes) | Creation can't finish until all 30 are placed (#39) |
| The attributes window starts open (P3) | `game.dll`, VA `0x59C574` | The points are placed right away (#39) |
| A hook calls our code cave after class registration | `game.dll`, VA `0x5B0051`; a new last section `.hdcc` (with the section count, `SizeOfCode`, `SizeOfImage` and checksum), after upstream's `.bounty` in 0.35, at VA `0x248C000` | The cave hides the full classes and the races after Shrouded Isles, and registers the 15 base classes with their descriptions (#55) |
| The Optimize button is removed | `pregame/character_customize_stats.xml` (ControlId 1021) | No auto-assign (#39) |
| The loading splash is replaced by our `splash.mpk` | `pregame/splash.mpk` | HEARTH DAoC lettering (#54) |

Sources, in `client/patches/`: `build.py` (the generator: stat-flow bytes, XML edit, cave section and hook),
`src/baseclass.asm` (the cave, nasm), `classdata.py` (base classes and races from the server's class files and
the world's `disabled_classes`), `src/base_classes.py` (descriptions and highlighted stats), `branding/` (the
splash) and the Linux applier (`apply_patches.py`, `patchset.py`).

The splash is OfflineDAoC's artwork (upstream keeps it as
`source/tools/OfflineDaoc.Launcher/Assets/offline-daoc-client-splash.mpk`), re-lettered "HEARTH DAoC" in Cinzel
(SIL Open Font License) by `branding/reletter_splash.py`. Credit for the art goes to OfflineDAoC.
`client/patches/splash.mpk` is committed, and the patch set pins its SHA-256 as the splash entry's `after`. It is
not built at release time: an MPK packed again by upstream's MPK tool carries new timestamps, so a new hash, and
a client patched by one release would then be unknown to the next release's appliers. `deploy/build_bundles.sh`
copies the committed file into the client bundle and builds nothing when its hash isn't the pinned one, so
releases need no .NET.

**Changing the splash.** Re-letter `branding/splash.png` (`branding/reletter_splash.py`), build `splash.mpk`
from it with `branding/build_splash_mpk.py` (needs upstream's MPK tool, `source/tools/OfflineDaoc.Mpk`, and the
.NET 10 SDK), rebuild `classic-creation.json` (below), and commit `splash.png`, `splash.mpk` and the JSON
together. CI checks that they agree: `client/patches/tests/test_splash.py` fails when `splash.mpk` doesn't
hold `splash.png`, and the rebuild check fails when the JSON doesn't pin `splash.mpk`.

```bash
dotnet build source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj -c Release
python3 client/patches/branding/build_splash_mpk.py --mpk-tool source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll
```

The base-class list is generated for the shipped classic world's `disabled_classes`, with Disciple enabled as
`deploy/bin/world_fixes.py` does. On a server that disables more classes, a base class whose full classes are
all disabled is still offered, and the server refuses it at creation. Rebuilding the patch set with that
world's database (`--world-db`) fixes it.

**Rebuilding the patch set.** `classic-creation.json` is generated, never edited by hand. Rebuild it after a
change to anything above, to the server's class files or to `deploy/upstream.lock`. CI rebuilds it on every run
from the pinned release's files and fails ("Client patch set matches a rebuild") when the committed file
differs. A change to the generator alone (`build.py`, `pe.py`, `classdata.py`, `src/`,
`branding/reletter_splash.py`) or to upstream's MPK tool makes no release; the rebuilt `classic-creation.json`,
`branding/splash.png` or `splash.mpk` does. You need nasm (`sudo apt install nasm`); the rebuild uses the
committed `splash.mpk`. From the repository root:

```bash
c="$(mktemp -d)"  # EA files from the pinned release, verified; never commit or share them
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract editions/0.35-no-custom-class/runtime/client-opendaoc/app/game.dll "$c/game.dll"
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/client-opendaoc/app/pregame/character_customize_stats.xml "$c/pregame/character_customize_stats.xml"
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/client-opendaoc/app/pregame/splash.mpk "$c/pregame/splash.mpk"
python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract editions/0.35-no-custom-class/runtime/data/opendaoc.sqlite3.db "$c/world.db"
python3 client/patches/build.py --client "$c" --world-db "$c/world.db" --server-src source/server \
  --splash-mpk client/patches/splash.mpk --out client/patches/classic-creation.json
HDC_CLIENT_FILES="$c" HDC_TEST_WORLD="$c/world.db" python3 -m unittest discover -s client/patches/tests -t client/patches
rm -rf "$c"
```

The archive paths are the lock's `editions.classic` and `client_prefix` (the world database is about 90 MB).
The tests skip the real-file cases without `HDC_CLIENT_FILES` and `HDC_TEST_WORLD`, the MPK cases without
`HDC_MPK_TOOL` (the `OfflineDaoc.Mpk.dll` built for the splash above) and the PowerShell cases without `pwsh`
(or `HDC_PWSH`). CI sets all of them; `ClientPatchWorkflowTests` in `deploy/tests/test_workflows.py` (needs
ruby) keep the workflow that way. `setup.sh` run from a checkout installs `client/patches/` (with the committed
`splash.mpk`) as `<dest>/patches` and applies it from there.

If the pinned release's classic `game.dll` changes, `build.py` refuses it until the patch sites in `build.py`,
`classdata.py` and `src/baseclass.asm` are found again in the new file. Until then CI fails and players with
the new client keep the standard creation screen. A new upstream section moves `.hdcc` to a new address (0.35
added `.bounty`): `ORG` in `client/patches/tests/test_cave.py`, the section list in `test_pe.py` and the header
offsets in `test_build.py` pin it, so update them with it. Players must then update their client: a `game.dll`
of the old release, patched or not, is unknown to the new patch set (see the CHANGELOG for 0.35).

**Changing the `game.dll` patch later.** Players' clients stay patched by the release they had, and the
launchers apply the new release's patch set at the next launch. A `game.dll` patched by an older patch set is
neither "before" nor "after" for the new one, so both appliers would refuse it (exit 3, nothing changed: the
player keeps the old patch and is told the client isn't supported), and a restore would refuse it as changed
since it was patched. The same holds for any patched file whose "after" changes. Such a change must also teach
both appliers to upgrade: recognise the older patched file, put its verified backup back, then apply the new
set.

## Client updates

Linux clients update at launch, after asking the player. Windows players still copy the new files by hand.

- `deploy/build_bundles.sh` writes the release tag into the client bundle's `VERSION`, and the client's content
  ID into `CONTENT_ID` and beside the bundle as the release asset `hearthdaoc-client-<tag>.content-id`: the
  SHA-256 of the bundled files' `sha256sum` lines in path order, without `VERSION`. Two releases of the same
  client have the same ID.
- `setup.sh` saves its options (`--server`, `--edition`, `--base-client`, made absolute), that tag and that
  content ID (`content_id`, empty from a checkout or an older bundle) in
  `<dest>/hearthdaoc-client.conf`, last and by a rename, so a failed setup keeps the previous release. From a
  checkout there is no `VERSION`, the tag is empty and `play.sh` never checks. It also writes `play.sh` as
  `play.sh.new` and renames it: a running `play.sh` that updates itself goes on reading its own file.
- Over an installed client, `setup.sh` builds the new one in `<dest>/client.new` (hard links to the old
  client, or a full copy where hard links fail) and keeps the old patches in `<dest>/patches.old`. It swaps
  them in only when everything worked; a failed setup, say a download that stops, leaves the install as it
  was. rsync, `odaoc_fetch.py` and the patches replace files by a rename and never write into them, so the
  hard-linked old client stays as it was. A first setup builds `<dest>/client` in place.
- At each launch, before the login, `play.sh` reads that file (never sources it) and asks GitHub's releases
  page (`.../releases/latest`, as `hdc update` does) for the newest tag, for 5 seconds at most. For tests,
  `HEARTHDAOC_RELEASES_URL` points `play.sh` (and `hdc`) at a local server instead; players never set it. It
  offers a newer tag (`sort -V`, with 0.35 before 0.35b) with a zenity or terminal question, unless that
  release's `.content-id` (fetched for 5 seconds at most) equals the saved `content_id`: a release that changes
  only the server plays on with one line on stderr. Without an ID to compare, the release is offered. Yes downloads
  `hearthdaoc-client-<tag>.zip` into `<dest>/.update.XXXXXX`, checks its `setup.sh` and that `VERSION` says
  the tag, runs that `setup.sh` with the saved options and `--dest <dest>`, then starts the new `play.sh` with
  `HEARTHDAOC_NO_UPDATE=1`. A failure warns, keeps the saved tag and plays the installed release. Before each
  update, it removes `.update.*` folders left by one cut short (a power cut). `HEARTHDAOC_NO_UPDATE=1` turns
  the check off.

So every client bundle must keep `hearthdaoc-client-<tag>/setup.sh` and `VERSION`, and every `setup.sh` must
accept the options of the earlier ones: the players' `play.sh` runs it with them. Every `setup.sh` must also
change an install only once everything worked: after a failed update, `play.sh` tells the player that the
installed release starts. `UpdateRoundTripTests` in `client/tests/test_play.py` updates a client set up by
`setup.sh` with a bundle built by `build_bundles.sh`, and fails one halfway. Every release offers a client
update, even one that changed only the server.

## Syncing with upstream

1. Sync through a PR, not GitHub's **Sync fork** button: that commits straight to `main`, and merging is
   releasing (below), so it would publish before `deploy/upstream.lock` is updated. Merge the release tag,
   not `upstream/main`: upstream's `main` can be behind its release (for 0.35b it was 33 commits behind), and
   the code must be the release's code, the commit the lock pins.
   `git fetch upstream --tags && git switch -c sync/<version> origin/main && git merge v<version>`
2. If upstream published a new release, update `deploy/upstream.lock` (version, tag, commit, part
   sizes and hashes, manifest SHA-256, edition paths, `server_files`) from the new release's
   `download-manifest.json` and `PACKAGE MANIFEST.sha256`, on the same branch. `ServerFilesLockTests`
   (`deploy/tests/test_init_world.py`) fails when the server source names a `classic-*.json` file that the
   lock does not list, or the other way round.
3. Rebuild the client patch set (see Client patches above) and commit it on the same branch if it changed.
4. Check the fork's world fixes and hooks against the new release. Run the tests with `HDC_TEST_WORLD` set
   to the new clean classic world: `battlegrounds.py` changes a row only while it holds the value it
   expects, so a changed row fails the real-data tests, and the fix then needs a new version and marker.
   The source checks in `deploy/tests/test_battlegrounds.py` list every fork hook in upstream files and
   every `TravelToBattleground` caller (the bots' only way in); a new one fails them until it is reviewed.
   Update the counts `deploy/HANDOFF.md` expects (navmeshes, restored spawns).
5. Push and open a PR; its CI builds and tests the image.
6. Merging it releases `v<upstream-version>-hearth.<n>` (a new upstream version restarts at `.1`).
7. On the server: `./hdc update` (`deploy/HANDOFF.md` → "Upgrading"). It backs up first, and for a release
   of another upstream version it upgrades the world itself. The `hdc` of the release before runs that
   update, so this starts with the update after 0.35b; from 0.34b to 0.35b, run `./hdc upgrade-world` and
   `./hdc up` by hand.

## Releases

Merging is releasing (`.github/workflows/server-image.yml`):

1. Every PR runs the full suite (docs-only PRs run nothing).
2. Every push to `main` runs the full suite on that commit (a docs-only merge runs nothing). If files that ship changed since the last
   release (`deploy/release_tag.py next`: what `deploy/Dockerfile` copies and `deploy/build_bundles.sh`
   bundles), the same run then publishes the next `v<upstream-version>-hearth.<n>` from that commit: the
   image, the tag and the GitHub release with both bundles and notes generated from the merged PRs.
   Docs, tests, CI and `release_tag.py` changes make no release. A failing test publishes nothing.
3. About 8 minutes after the merge, on the server: `./hdc update`. Linux players are offered the new
   client at their next launch (see Client updates).

The repository names no release: `deploy/build_bundles.sh` stamps the tag into the bundle's
`.env.example`, which is where `./hdc update` reads it, and HANDOFF's install step looks up the latest
release. Runs on `main` wait for each other, so releases are made one at a time. A run for a commit that
is older than an existing release publishes nothing, so re-running an old run cannot ship older code.

- To hold releases back (main still builds and tests), set the repository variable `HOLD_RELEASES` to
  `1`. Delete it, then merge the next PR or use Actions → server-image → Run workflow on `main`.
- If a release failed, a run on `main` was cancelled, or a merge made no release, fix it and merge, or
  use Run workflow on `main`: it releases everything shipped since the last release.
- Do not push `v*-hearth.*` tags by hand: nothing builds them, and the next release counts from them.
- Merging and Run workflow on `main` publish to the servers, so only the owner does either.

Each release `v<upstream-version>-hearth.<n>` publishes the image `ghcr.io/lometur/hearthdaoc:<tag>` and
attaches two assets: `hearthdaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `hdc`, handoff) and
`hearthdaoc-client-<tag>.zip` (player scripts, the client patch set with both appliers, our `splash.mpk`, and
`VERSION`, the tag).
Neither contains EA game files. The release notes credit OfflineDAoC for the splash art.
