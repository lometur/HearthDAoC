# HearthDAoC changelog

Releases of the fork up to v0.34b-hearth.6 (newest first). Later releases list their changes on the
[releases page](https://github.com/lometur/HearthDAoC/releases). Upstream's own changes are in CHANGELOG.md.

## v0.34b-hearth.6 (2026-10-06)

- fix(deploy): server waits for database locks instead of crashing

## v0.34b-hearth.5 (2026-10-06)

- ci(release): bring skipped release PRs back after a release is published
- fix(deploy): enable Disciple and give Saracen Disciples a starting location

## v0.34b-hearth.4 (2026-10-06)

- fix(release): changelog skips only the release PRs' own commits
- feat(deploy): hdc update installs a release in one command
- ci(release): release PR after merges; merging it publishes the release
- chore(release): docs name the current release; CI refuses stale ones

