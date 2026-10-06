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
4. Release it (see Releases below) as `v<upstream-version>-hearth.<n>`.
5. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".

## Releases

Releases are automatic, but only happen when you merge a release PR:

1. After each merge to `main`, `.github/workflows/release-pr.yml` checks whether files that end up in the
   image or the bundles changed since the last release (`deploy/release_tag.py plan`). If so, it opens or
   updates one PR, "Release v<upstream-version>-hearth.<n>": it bumps `HEARTHDAOC_TAG` in
   `deploy/.env.example` and the install step in `deploy/HANDOFF.md`, and adds the release's entry to
   `docs/fork/CHANGELOG.md` from the commit messages. Docs-only merges make no release.
2. Merging that PR is the release: the build on `main` runs every test, sees the docs naming a tag that
   isn't published yet (`release_tag.py pending`), checks it, then publishes the image, creates the tag
   and the GitHub release with both bundles. A failing test publishes nothing.
3. On the server: `./hdc update`.

A new upstream version in `deploy/upstream.lock` restarts the numbering at `-hearth.1`. Pushing a tag by
hand still works (CI checks the docs name it). PRs opened by the workflow get no CI run of their own
(a GitHub rule); the build on `main` after the merge is the check. The workflow needs the repository
setting "Allow GitHub Actions to create and approve pull requests".

Tags `v0.34b-hearth.N` publish `ghcr.io/lometur/hearthdaoc:v0.34b-hearth.N` and attach two assets:
`hearthdaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `hdc`, handoff) and
`hearthdaoc-client-<tag>.zip` (player scripts). Neither contains EA game files.
