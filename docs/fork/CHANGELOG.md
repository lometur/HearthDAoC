# HearthDAoC changelog

Releases of the fork up to v0.34b-hearth.6, and the sync with upstream 0.35 (newest first). Other later
releases list their changes on the [releases page](https://github.com/lometur/HearthDAoC/releases). Upstream's
own changes are in CHANGELOG.md.

## v0.35b-hearth.1 (the sync with upstream 0.35)

The first release on upstream OfflineDAoC v0.35b "Claude Takeover III" (#50).

- feat(upstream): OfflineDAoC 0.35: 448 classic quests with red map markers and a Quest Guide, the monster
  populations of the period, Summoner's Hall and Darkness Falls raid events, keep and relic sieges with
  siege engines, battlegrounds, the classic frontier war map, and better gamebots
- feat(server): the town teleporters' new [Battlegrounds] choice keeps the classic realm rank caps (1L2,
  1L3, 1L5, 1L9), with the frontier porter's words; the hidden whisper path (`GetBGPK`) has them again too
- feat(server): bots follow the same caps: a bot at or over a battleground's cap gets no battleground goal
  and is not sent in; one already inside may stay until it leaves or dies
- feat(deploy): Dun Abermenai and Dun Murdaigean are upstream's new central keeps, with their 26 and 27
  renegade guards, levelled as before (guards 21 and 31, lords 24 and 36), plus six wall casters and a
  hastener each; after a capture the keep goes back to level 1 and its gates to full health. The hastener
  stands beside the outer gate and hastes only the realm that holds the keep. Upstream's battleground
  monsters and Siege Masters stay (world fix `classic-battlegrounds-v2`)
- feat(deploy): upstream's quest data files (`classic-quests.json`, `classic-quest-guides.json`) are
  downloaded from the release at the first start of a new upstream version, checked, and kept in the data
  volume, so quest markers, the Quest Guide and quest event monsters work on the server
- feat(deploy): `hdc update` upgrades the world itself when a release is for another upstream version (from
  the update after this one); `hdc update` no longer exits without a message when there is no world yet
- feat(tools): `hdc bot-goals` shows and sets upstream's new Battlegrounds share (an optional fourth number,
  kept when left out)
- feat(client): the client patch set supports only the OfflineDAoC 0.35 classic client

To update:

- **Owner:** `./hdc update`. The 0.34b `hdc` installs this release, but it does not upgrade the world: it
  stops before starting and says so. Then run `./hdc upgrade-world` and `./hdc up`, once. All progress
  moves into the 0.35 world, and the report names the server settings to re-check. The first start
  downloads about 570 MB of changed navmeshes and the two quest data files. Then set the bot goals split in
  `deploy/HANDOFF.md` (Bot goals) with `./hdc bot-goals set`; upstream's default sends no bots to the
  battlegrounds.
- **Players:** update to the 0.35 classic client. On Linux, run the new client bundle's `setup.sh` again
  (it downloads about 3.4 MB). On Windows, install upstream's `DOWNLOAD-AND-PLAY-v0.35.cmd`, then use the
  new bundle's `connect-hearthdaoc.bat`. The launchers leave a 0.34 `game.dll` alone, so until then the game
  starts with the standard creation screen. The 0.35 war map, quest markers and Quest Guide button also need
  the 0.35 client.

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

