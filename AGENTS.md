# Offline DAoC — instructions for AI assistants and developers

Read this first, then [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). This file is also loaded by
Claude Code as `CLAUDE.md`.

## What this is

Offline DAoC is a single-player DAoC server (OpenDAoC fork, .NET 10, SQLite) with autonomous
gamebots, companion bots and a Windows launcher. The current release is **0.35 "Claude
Takeover III"**. It comes in two editions from one build:
- **0.35b:** the custom Hibernian Sluaghbinder class is on.
- **0.35:** the class is off. The server setting `classes / enable_sluaghbinder` is `False`, and
  the client is the 0.35b `game.dll` without its two Sluaghbinder patches.

## Layout

| Path | What it is |
|---|---|
| `source/server` | Game server, all bot AI, tests (`Tests/`). The solution is `Dawn of Light.sln`. |
| `source/server/GameServer/bots` | Companion bots, class AI, bot brains |
| `source/server/GameServer/bots/autonomous` | Autonomous gamebots: goals, camps, groups, travel, watchdog |
| `source/tools/OfflineDaoc.Launcher` | Launcher (the in-game version label is `MainForm.DisplayVersion`) |
| `source/tools/OfflineDaoc.ProgressImport` | Progress transfer tool (`progress-policy.json` lists the saved-progress tables) |
| `source/tools/build_release_035.py` | Builds the clean playable package from a local install (then `assemble_release_035.py` and `seal_release_035.py`) |
| `source/tools/smoke_release_035.py` | Starts a package's server and logs in, as a player would |
| `tools/pet-art` | Art pipeline for the private pet models and skins, pet weapons and spell effects. Each install step has a rollback. |
| `docs/` | Player and developer guides; `docs/history/` holds earlier versions' notes |

In a playable folder:
- `runtime/server`: the server binaries and navigation meshes
- `runtime/data/opendaoc.sqlite3.db`: world data plus the player's saves
- `runtime/client-opendaoc/app`: the game client

## Rules

- **Protect player data.**
  - Never commit or publish a played database, `account.txt`, logs, bot rosters, backups or
    credentials.
  - Public packages are built only by `build_release_035.py`, which empties every saved-progress
    table and verifies it.
- **Keep the owner's gameplay rules.** Preserve these unless the owner asks otherwise:
  - real loot, inventories, coins and equipment upgrades
  - the Realm Exchange
  - stablemaster routes
  - selling only at natural task breaks
  - gamebots start without armor and earn it
  - Darkness Falls is open to solo bots
- **Tell bot types apart.** Real players, companion bots and autonomous gamebots are different.
  Check which one a change affects.
- **Don't start or deploy without permission.**
  - Never start a server or copy builds over a running install without asking.
  - Only one server can use port 10300 at a time.
- **Build and test separately.** Deploy only tested outputs, with a backup of what you replace:
  `GameServer.dll` goes to `runtime/server`, `runtime/server/lib` and `runtime/server/win-x64`.
- **Client files:**
  - Keep hash guards on any binary patch; never patch an unverified `game.dll`.
  - Private art is added under new IDs; stock models, skins and effects are never edited in place.
  - `tools/pet-art` shows the pattern: byte copies of NIFs with same-length texture renames, and
    new catalog rows.
- **Navigation:** don't rebuild or replace navmeshes globally to fix one route without evidence.
- **Be honest in reports.** Report automated tests separately from real in-game checks. Don't
  describe Darkness Falls raid AI, Legion or the hardest level 70+ encounters as implemented.
- **Keep the old releases.** v0.3–v0.34b stay available as legacy releases, and their branches
  and tags are not rewritten.

## Useful facts

- **Edition switch:**
  - The switch is `ServerProperties.Properties.ENABLE_SLUAGHBINDER`. It defaults to on, is read
    from the database and is honored by the bot generator, character creation and launcher bot
    batches.
  - The Sluaghbinder uses the client's Hibernian Mauler slot (class 62), and the novice is stored
    as Acolyte (16) until promotion to class 63.
- **Item model chain:** `objects.csv` model ID → `items.csv` row → `items/<nif>`.
  - Shields that carry an emblem take their base texture from a `pskins.csv` override.
  - `tools/pet-art/nif4_geom.py` reads the old 4.x item NIFs that pyffi can't.
- **Client art, spells and sounds:** read [docs/CLIENT-MODDING-GUIDE.txt](docs/CLIENT-MODDING-GUIDE.txt)
  before changing models, meshes, skins, spell effects, icons or NPC sounds. It lists the client's
  catalog rules (no blank lines, terminators, Expansion Only, ID order), confirmed and unconfirmed
  limits, every tool, and past mistakes.
- **Helmets with no face:** see [docs/HELMET-FACE-FIX.md](docs/HELMET-FACE-FIX.md) and
  `tools/claude-version/helmet_face_check.py`.
- **Server settings:** Offline-specific settings are in the database tables
  `offline_population_settings` and `offline_local_options`, and in `runtime/server/bot-goals.json`.

The owner may customize their fork's rules. These are safety defaults, not a ban on changes the
owner asks for.
