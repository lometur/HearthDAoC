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
| Player setup | `client/` | none |
| Classic character creation and splash (client patch set) | `client/patches/`, `client/windows/patch-client.*` | none: patches each player's own client files (see Client patches) |
| Leveling spawns (owner's choice) | `deploy/bin/spawns.py`, `hdc spawns` | none: restores rows upstream's setup archived in `offline_classic165_removed_mobs` |
| Classic battlegrounds 15-35, world data (once per world) | `deploy/bin/battlegrounds.py` (run by `deploy/bin/world_fixes.py`) | none |
| Design docs | `docs/fork/` | none |

Server code under `source/server` was unchanged in sub-project 1. Every later server-code change is
listed here, so upstream syncs can account for it:

| Change | Files | Why | Upstream |
|---|---|---|---|
| `command_plvl_overrides` server property (e.g. `/tele=2;/tc=2`) | `GameServer/gameutils/ScriptMgr.cs` (`CommandPrivLevel`, applied in `LoadCommands`), `GameServer/serverproperty/ServerProperties.cs`, test `Tests/UnitTests/UT_CommandPrivLevelOverrides.cs` | Makes single-player teleports GM-only on a shared server (#38); set from `HEARTHDAOC_GM_ONLY_COMMANDS` | Candidate: generic, off by default |
| Shrouded Isles start choice: `si_start_choice` server property | `GameServer/scripts/hearthdaoc/SiStartChoice.cs` (the decisions), `GameServer/scripts/hearthdaoc/SiStartChoiceScript.cs` (the property and the game wiring), test `Tests/UnitTests/UT_SiStartChoice.cs`. Upstream files touched: none | A new character of a classic race is asked once, a few seconds after its first entry into the world, whether to begin in its realm's Shrouded Isles town (#40, [spec](specs/2026-10-06-shrouded-isles-start-choice-design.md)); set from `HEARTHDAOC_SI_START_CHOICE` (default on) | Candidate: off by default |
| Classic battlegrounds: porter levels and caps, over-limit moves, keep level after a capture | `GameServer/scripts/hearthdaoc/ClassicBattlegrounds.cs` (the decisions), `GameServer/scripts/hearthdaoc/ClassicBattlegroundsScript.cs` (the porter hook and the game wiring), test `Tests/UnitTests/UT_ClassicBattlegrounds.cs`, source checks `deploy/tests/test_battlegrounds.py`. Upstream files touched: `GameServer/scripts/teleporters/OFTeleporters.cs` (three blocks), `GameServer/keeps/KeepManager.cs` (one word), and the twelve battleground quest files in `GameServer/scripts/quests/BattlegroundQuests/` (Thidranki and Caledonia), deleted | The four classic battlegrounds for levels 15-35 with their realm rank caps, the porter's reasons, over-limit characters at their bind point, no Atlas daily quests (#76, [spec](specs/2026-10-07-classic-battlegrounds-design.md)) | Candidate: the "Svasud Faste" fix; the rest is fork-only |

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
out, after a restore. Both appliers refuse any other client file, such as the b edition's `game.dll`, and then
change nothing. Both keep each original as `<file>.hearthdaoc-orig` and put it back with `--restore` /
`-Restore`, but only over the patched file: when a file has changed since it was patched (for example a newer
upstream client), the restore says so and changes nothing.

| What is patched | Where | Why |
|---|---|---|
| Auto-assign stops after its reset: race base stats and 30 points to place (P1) | `game.dll`, VA `0x59C0B2` | Classic stat points (#39) |
| Continue checks unspent points for new characters too (P2) | `game.dll`, VA `0x59A853` (28 bytes) | Creation can't finish until all 30 are placed (#39) |
| The attributes window starts open (P3) | `game.dll`, VA `0x59C574` | The points are placed right away (#39) |
| A hook calls our code cave after class registration | `game.dll`, VA `0x5B0051`; a new last section `.hdcc` (with the section count, `SizeOfCode`, `SizeOfImage` and checksum) | The cave hides the full classes and the races after Shrouded Isles, and registers the 15 base classes with their descriptions (#55) |
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
the new client keep the standard creation screen.

**Changing the `game.dll` patch later.** Players' clients stay patched by the release they had, and the
launchers apply the new release's patch set at the next launch. A `game.dll` patched by an older patch set is
neither "before" nor "after" for the new one, so both appliers would refuse it (exit 3, nothing changed: the
player keeps the old patch and is told the client isn't supported), and a restore would refuse it as changed
since it was patched. The same holds for any patched file whose "after" changes. Such a change must also teach
both appliers to upgrade: recognise the older patched file, put its verified backup back, then apply the new
set.

## Syncing with upstream

1. Sync through a PR, not GitHub's **Sync fork** button: that commits straight to `main`, and merging is
   releasing (below), so it would publish before `deploy/upstream.lock` is updated.
   `git fetch upstream && git switch -c sync/<version> origin/main && git merge upstream/main`
2. If upstream published a new release, update `deploy/upstream.lock` (version, tag, commit, part
   sizes and hashes, manifest SHA-256, edition paths) from the new release's `download-manifest.json`
   and `PACKAGE MANIFEST.sha256`, on the same branch.
3. Rebuild the client patch set (see Client patches above) and commit it on the same branch if it changed.
4. Push and open a PR; its CI builds and tests the image.
5. Merging it releases `v<upstream-version>-hearth.<n>` (a new upstream version restarts at `.1`).
6. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".

## Releases

Merging is releasing (`.github/workflows/server-image.yml`):

1. Every PR runs the full suite (docs-only PRs run nothing).
2. Every push to `main` runs the full suite on that commit. If files that ship changed since the last
   release (`deploy/release_tag.py next`: what `deploy/Dockerfile` copies and `deploy/build_bundles.sh`
   bundles), the same run then publishes the next `v<upstream-version>-hearth.<n>` from that commit: the
   image, the tag and the GitHub release with both bundles and notes generated from the merged PRs.
   Docs, tests, CI and `release_tag.py` changes make no release. A failing test publishes nothing.
3. About 8 minutes after the merge, on the server: `./hdc update`.

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
`hearthdaoc-client-<tag>.zip` (player scripts, the client patch set with both appliers, and our `splash.mpk`).
Neither contains EA game files. The release notes credit OfflineDAoC for the splash art.
