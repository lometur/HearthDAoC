# About this fork

`lometur/OfflineDAoC` is an unofficial fork of [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).
It runs OfflineDAoC as a central multiplayer server and keeps its own changes small and additive,
so upstream updates merge cleanly.

## What the fork changes

| Change | Paths | Upstream files touched |
|---|---|---|
| Fork banner | `README.md` (top lines), `.github/README.md` | `README.md` |
| Container deployment | `deploy/`, `.dockerignore`, `.github/workflows/server-image.yml` | none |
| Linux admin CLIs | `tools/linux/` (link upstream sources; see each project file) | none |
| Player setup | `client/` | none |
| Design docs | `docs/fork/` | none |

Server code under `source/server` is unchanged in sub-project 1. Later sub-projects list every
server-code change in this table.

## Syncing with upstream

1. On GitHub, click **Sync fork** (or `git fetch upstream && git merge upstream/main`).
2. If upstream published a new release, update `deploy/upstream.lock` (version, tag, commit, part
   sizes and hashes, manifest SHA-256, edition paths) from the new release's `download-manifest.json`
   and `PACKAGE MANIFEST.sha256`.
3. Push; CI builds and smoke-tests the image.
4. Tag `v<upstream-version>-fork.<n>` to publish the image and release assets.
5. On the server, back up, then follow `deploy/HANDOFF.md` → "Upgrading".

## Releases

Tags `v0.34b-fork.N` publish `ghcr.io/lometur/offlinedaoc:v0.34b-fork.N` and attach two assets:
`offlinedaoc-deploy-<tag>.tar.gz` (compose file, `.env.example`, `odc`, handoff) and
`offlinedaoc-client-<tag>.zip` (player scripts). Neither contains EA game files.
