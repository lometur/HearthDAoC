# Developing Offline DAoC

This guide is for people (and AI assistants) who change the code. Players only need
[PLAY.md](PLAY.md).

## Layout

| Where | What |
|---|---|
| `source/server` | OpenDAoC-based server (`Dawn of Light.sln`): game logic, companion bots, autonomous gamebots, tests |
| `source/tools/OfflineDaoc.Launcher` | Windows launcher (server control, bot creation, dashboards) with tests in `OfflineDaoc.Launcher.Tests` |
| `source/tools/OfflineDaoc.ProgressImport` | Progress transfer tool |
| `source/tools/build_release_035.py`, `assemble_release_035.py`, `seal_release_035.py`, `smoke_release_035.py` | Build, assemble, seal and smoke-test the public package |
| `source/development-tools` | Navigation mesh builder and pathing source |
| `tools/pet-art`, `tools/asset-tool` | Client art pipeline and the MPK and texture helpers |
| `tools/claude-version` | Small database and client helpers used since 0.33 |

A playable folder has:
- `runtime/server`: server binaries plus `navmesh/`
- `runtime/data/opendaoc.sqlite3.db`: world plus saves
- `runtime/client-opendaoc/app`: the game client
- `runtime/OfflineDAoC.exe`: the launcher

## Build and test

You need the **.NET 10 SDK** on Windows. The playable download bundles only the runtime.

```bash
cp source/server/CoreServer/config/serverconfig.example.xml source/server/CoreServer/config/serverconfig.xml
dotnet build "source/server/Dawn of Light.sln" -c Release
dotnet test source/server/Tests/Tests.csproj -c Release
dotnet test source/tools/OfflineDaoc.Launcher.Tests/OfflineDaoc.Launcher.Tests.csproj -c Release
```

- `serverconfig.xml` is local and ignored by git. The build only needs it to exist.
- Some launcher tests expect that no local DAoC server is running.
- At 0.35: 2,725 server tests and 105 launcher tests pass.

## Deploy a build into a playable folder

1. **Stop everything:** the launcher, the game and the server.
2. **Back up the files you'll replace.**
3. **Copy the server build:** `source/server/Release/lib/GameServer.dll` (and its `.pdb`) goes to
   `runtime/server`, `runtime/server/lib` and `runtime/server/win-x64`.
4. **Copy the launcher build** if you changed it: the `OfflineDAoC.*` files from
   `source/tools/OfflineDaoc.Launcher/bin/Release/net10.0-windows` go to `runtime/`.
5. **Test in game.** Automated tests are not a substitute for an in-game check.

## Editions and the custom class

- **The switch:** the server setting `classes / enable_sluaghbinder`
  (`ServerProperties.Properties.ENABLE_SLUAGHBINDER`) is on by default.
- **When it's off:**
  - `AutonomousBotIdentityGenerator` never rolls or lists the Sluaghbinder.
  - The launcher's bot batches skip class 63.
  - Character creation treats the Hibernian Mauler slot as the disabled native class again.
- **The 0.35 database** sets the switch off and removes the Sluaghbinder trainer, the wisp and the
  class's skill rows. The 0.35 client (SHA-256 `f55ed6b0…`) is the 0.35b client without its two
  Sluaghbinder patches: the normal v0.32 `game.dll` (`67dcf68a…`) with
  `patch_bounty_map_client.py`, `patch_quest_marker_range_client.py`, `patch_classic_warmap_client.py`
  and `patch_quest_journal_button_client.py` applied, in that order.
- **0.35b** uses the Sluaghbinder client `game.dll` (`e1d471bb…`), which relabels the Mauler slot.
- **The launcher** reads the same switch and shows VERSION 0.35b or VERSION 0.35.

## Building the public package

`source/tools/build_release_035.py` takes a 1:1 snapshot of a development install and produces a
clean package.

What it copies and changes:
- It copies the runtime without logs, backups, accounts or run-state files.
- It empties every saved-progress table and resets keeps, relics, houses and guild earnings.
- It sets the public defaults: zero bots, GM off, 1× XP and automatic account creation.
- It installs the release server and launcher builds.
- It bundles the .NET runtime.
- It writes the 0.35 edition files.

Checks:
- Everything it copies is recorded with a hash.
- `assemble_release_035.py` adds the player files, docs, source and tools, and
  `seal_release_035.py` hashes every file, zips the package, re-checks every zip entry and splits
  it into release parts with their download manifests.
- `smoke_release_035.py` then starts the server with only the bundled .NET and logs in through
  `connect.exe`, confirming that a fresh account is created.

## Sharing safely

- **Never commit a played database.** It contains accounts, characters, bot rosters, inventories
  and economy history. Also keep `account.txt`, logs, dumps and backups out of commits.
- **Review before pushing.** Run `git diff --cached` before every push; `.gitignore` is a safety
  net only.
- **Local only:** the default setup is for one PC. Don't expose it to the internet as-is.
