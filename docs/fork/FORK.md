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

1. Sync through a PR, not GitHub's **Sync fork** button: that commits straight to `main`, and merging is
   releasing (below), so it would publish before `deploy/upstream.lock` is updated.
   `git fetch upstream && git switch -c sync/<version> origin/main && git merge upstream/main`
2. If upstream published a new release, update `deploy/upstream.lock` (version, tag, commit, part
   sizes and hashes, manifest SHA-256, edition paths) from the new release's `download-manifest.json`
   and `PACKAGE MANIFEST.sha256`, on the same branch.
3. Push and open a PR; its CI builds and tests the image.
4. Merging it releases `v<upstream-version>-hearth.<n>` (a new upstream version restarts at `.1`).
5. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".

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
`hearthdaoc-client-<tag>.zip` (player scripts). Neither contains EA game files.
