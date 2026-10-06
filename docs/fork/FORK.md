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
| Leveling spawns (owner's choice) | `deploy/bin/spawns.py`, `hdc spawns` | none: restores rows upstream's setup archived in `offline_classic165_removed_mobs` |
| Design docs | `docs/fork/` | none |

Server code under `source/server` was unchanged in sub-project 1. Every later server-code change is
listed here, so upstream syncs can account for it:

| Change | Files | Why | Upstream |
|---|---|---|---|
| `command_plvl_overrides` server property (e.g. `/tele=2;/tc=2`) | `GameServer/gameutils/ScriptMgr.cs` (`CommandPrivLevel`, applied in `LoadCommands`), `GameServer/serverproperty/ServerProperties.cs`, test `Tests/UnitTests/UT_CommandPrivLevelOverrides.cs` | Makes single-player teleports GM-only on a shared server (#38); set from `HEARTHDAOC_GM_ONLY_COMMANDS` | Candidate: generic, off by default |

## Syncing with upstream

1. On GitHub, click **Sync fork** (or `git fetch upstream && git merge upstream/main`).
2. If upstream published a new release, update `deploy/upstream.lock` (version, tag, commit, part
   sizes and hashes, manifest SHA-256, edition paths) from the new release's `download-manifest.json`
   and `PACKAGE MANIFEST.sha256`.
3. Push; CI builds and smoke-tests the image.
4. Tag `v<upstream-version>-hearth.<n>` to publish the image and release assets.
5. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".

## Releases

Tags `v0.34b-hearth.N` publish `ghcr.io/lometur/hearthdaoc:v0.34b-hearth.N` and attach two assets:
`hearthdaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `hdc`, handoff) and
`hearthdaoc-client-<tag>.zip` (player scripts). Neither contains EA game files.
