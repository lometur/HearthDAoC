# Handoff: deploy the HearthDAoC server

For the Claude session on the server machine. This deploys HearthDAoC (`lometur/HearthDAoC`, an unofficial
fork of shadowofze/OfflineDAoC) next to the existing OpenDAoC server.

**Do not modify, stop, restart or recreate any OpenDAoC container, volume, network or compose
project.** Everything here uses the compose project `hearthdaoc`, the container
`hearthdaoc-server`, the volume `hearthdaoc-data` and ports 10301/tcp + 10401/udp.

## 1. Pre-checks (report each result to the owner)

```bash
docker --version && docker compose version          # Compose v2 required
free -g                                              # need >= 10 GB available next to OpenDAoC
ss -ltnu | grep -E ':(10301|10401)\b' || echo free   # both ports must be free
docker ps --format '{{.Names}}' | grep -i opendaoc   # note OpenDAoC's containers; leave them alone
```
If less than 10 GB of RAM is available, lower `HEARTHDAOC_MEM_LIMIT` in step 2 and tell the owner.

## 2. Install

```bash
mkdir -p ~/hearthdaoc && cd ~/hearthdaoc
tag="$(curl -fsSI https://github.com/lometur/HearthDAoC/releases/latest | tr -d '\r' | sed -n 's|^[Ll]ocation: .*/tag/||p')"
echo "Latest release: $tag"   # report it to the owner; empty means GitHub was not reachable
curl -fLO "https://github.com/lometur/HearthDAoC/releases/download/$tag/hearthdaoc-deploy-$tag.tar.gz"
tar xzf "hearthdaoc-deploy-$tag.tar.gz"
cp .env.example .env
```
Edit `.env`: set `TZ` to the owner's timezone; keep `HEARTHDAOC_EDITION=classic` unless the owner
says otherwise. Then pull: `docker compose -p hearthdaoc -f compose.yml --env-file .env pull`.
If the pull says `denied`, the image package is still private: tell the owner (GitHub → Packages →
hearthdaoc → Package settings → visibility Public) and wait.

## 3. World data (choose one)

- **Shortcut (preferred):** ask the owner to copy their navmeshes over, by running this on THEIR PC:
  `rsync -a ~/Games/OfflineDAoC/runtime/server/navmesh/ <user>@<this machine>:hearthdaoc/seed-navmesh/`
  then: `./hdc init --seed-navmesh ~/hearthdaoc/seed-navmesh` (verifies every file; downloads only the
  world database and the server data files, ~33 MB). Afterwards `rm -rf ~/hearthdaoc/seed-navmesh`.
- **Download:** `./hdc init` (about 600 MB from GitHub, verified, resumable).

Then bring back the leveling monsters upstream archived (the owner chose levels 1-20, OpenDAoC's
density; takes a few seconds and is re-applied automatically by new-world and upgrade-world):
`./hdc spawns restore --max-level 20` and check `./hdc spawns status` (expect `restored` about 12,900).

## 4. Start and firewall

```bash
./hdc up        # if the server refuses to start, this prints why (and ./hdc status repeats it)
sudo ufw status # report the result to the owner
```
Only if ufw is **active**, add LAN-only rules:
```bash
sudo ufw allow from <LAN subnet, e.g. 192.168.1.0/24> to any port 10301 proto tcp
sudo ufw allow from <LAN subnet> to any port 10401 proto udp
```
If ufw is inactive, do **not** enable it (that can cut off SSH and OpenDAoC); tell the owner instead. If the
machine uses another firewall, describe it to the owner rather than changing it.

## 5. Verify (report each)

```bash
sleep 90 && ./hdc status                       # running (healthy), edition classic, navmesh True
docker exec hearthdaoc-server grep -c "Loading NavMesh successful" /data/logs/server.log   # 103 (upstream 0.35b)
docker exec hearthdaoc-server grep "Server is now listening" /data/logs/server.log | tail -1   # 0.0.0.0:10301
docker exec hearthdaoc-server sqlite3 /data/world/opendaoc.sqlite3.db "SELECT DISTINCT Port FROM Regions"   # 10401
docker ps --format '{{.Names}} {{.Status}}' | grep -i opendaoc   # OpenDAoC still up, unchanged
```
Then ask the owner to connect from their PC (`~/Games/HearthDAoC/play.sh` after running
`setup.sh --server <this machine's LAN IP>:10301 ...`) and confirm the login works.

## 6. Day-to-day

`./hdc help` lists everything: status, logs, add-bots, accounts, bot goals, backups, restore,
new-world, upgrade-world, carry-rvr, auto-accounts, spawns. Backups run daily into the volume (keep 7). The
copies taken before add-bots keep their newest 3; the copies taken before restore, upgrade and carry-rvr are
kept until you remove them (`./hdc backups` lists them; they are in /data/backups).
Settings live in `.env` (see `.env.example`), e.g. `HEARTHDAOC_AUTOSAVE_MINUTES` (default 5),
`HEARTHDAOC_GM_ONLY_COMMANDS` (default `/tele;/tc`: single-player teleports need GM rights) and
`HEARTHDAOC_SI_START_CHOICE` (default `on`: a new level-1 character of a classic race is asked once
whether to begin in its realm's Shrouded Isles town; `off` turns the question off); after
editing `.env`, `./hdc up` recreates the server with them.
Before remote players join: `./hdc auto-accounts off` and create their accounts with
`./hdc account create <name> <password>`.

**Bot goals.** `./hdc bot-goals show` lists, for each level band, the share of bots given each goal: Solo
PvE, Group PvE, RvR and Battlegrounds (new in upstream 0.35; upstream's default is 0 everywhere). Each row
must add up to 100; levels 1-19 cannot have RvR and level 50 cannot have Battlegrounds. With the server
stopped, `./hdc bot-goals set <1-19|20-49|50> <solo> <group> <rvr> [<battlegrounds>]` changes one row
(leaving out Battlegrounds keeps its value); the server applies it at its next start. After the deploy,
set the owner's split, one row at a time:

| Levels | Solo PvE | Group PvE | RvR | Battlegrounds |
|---|---|---|---|---|
| 1-19 | 50 | 30 | 0 | 20 |
| 20-49 | 20 | 20 | 30 | 30 |
| 50 | 10 | 20 | 70 | 0 |

For example, `./hdc stop && ./hdc bot-goals set 20-49 20 20 30 30`, then the other two rows the same
way, then `./hdc up`.

**Duplicate townspeople.** Upstream's world has two copies of Ley Manton and of Tria Ellowis at the same
spot (the merchant and a plain NPC). At every start, a plain copy standing exactly on its merchant is removed
and kept in `fork_removed_mobs` (FixId `duplicate-townspeople`); `./hdc logs` shows `Duplicate townspeople
removed`. Nothing else is touched, and a world without the copies is left alone.

**Classic battlegrounds.** At its first start, a world gets the classic battlegrounds (Abermenai 15-19,
Thidranki 20-24, Murdaigean 25-29, Caledonia 30-35); `./hdc logs` shows each change on a line starting
`Battlegrounds:`. This runs once per world: the row `classic-battlegrounds-v2` in the world's
`fork_world_fixes` table records it, so later changes to the battlegrounds stay. Deleting that row makes
it run again at the next start, and the parts whose results are still there change nothing. The `Mob`
rows it removes (training dummies, Void Merchants, a stray Wizard) are kept in `fork_removed_mobs`; saved
Atlas battleground daily quests (`Quest` rows) are deleted, not archived. If it fails, the start log says
`Classic battlegrounds: not applied (...)`, the server starts with upstream's battlegrounds, and it tries
again at the next start. `./hdc new-world` and `./hdc upgrade-world` make a world without that row, so it
runs again there. A new world starts with the battleground keeps as upstream ships them. An upgraded world
keeps who holds the keeps in play and their gates' health (see Upgrading), but a central keep above level 1
goes back to level 1, its gates to level 1's full health at most, and changes made in game to the guards are
not carried over.
Battleground keep guard levels follow `keep_guard_level_multiplier` (1.6), which also sets the frontier
keeps' guards. The realm rank caps (1L2, 1L3, 1L5, 1L9) hold on every way in: the frontier porter, the town
teleporters' [Battlegrounds] choice, and for bots too.

**Guild of Shadows dialogue.** Upstream left most of the Guild of Shadows chain's dialogue (levels 7 to 50)
empty or filled with walkthrough notes. At every start, `./hdc logs` shows `Quest dialogue: N quests
rewritten` when the texts of the chain's quests (`deploy/bin/quest_dialogue.json`, every class's version) were
changed, and nothing when they are up to date. Besides the texts (accept keyword, description, journal steps and
what the NPCs say), the file can set a quest's step items, step types and turn-in items, but never its number of
stages: a quest whose stages differ from the file's is kept. There is no marker: a quest is rewritten only while its texts
still hold upstream's or an earlier version of this file's, so a quest whose text you changed yourself is kept,
and a later release with revised text reaches a world that had the earlier one. Kept quests are named at every
start (`M left as they are (their text differs from upstream's and this file's): 21500, ...`): your own edits,
or, after an upstream upgrade, quests whose upstream text changed, which keep upstream's text until the file is
updated. If it fails, the start log says `Quest dialogue: not applied (...)`, the quests keep the
text they have, and it tries again at the next start. The NPCs' chat lines are in `hearthdaoc-quests.json`
(`Chat`), which replaces upstream's line for the same NPC and keyword. To revise the text: edit it in `quest_dialogue.json`, run
`python3 deploy/bin/quest_dialogue.py --seal` (it appends the digest of the file's own text to each quest's guard
list) and commit; a test fails until you do. Every committed version of the text is then guarded, so a later
revision reaches a world that holds any earlier committed version. Only when an entry's set of columns changes
is `python3 deploy/bin/quest_dialogue.py --digests <world.db> [--current]` still needed (it prints the digests of
the text a world holds, which is upstream's only on a clean world).

**Mob fixes.** Period corrections to upstream's monsters, from the quest archives: level 11's thieves Frund and
Agisthil are level 10, and Frund stands at the red dwarf camp beside Agisthil (`deploy/bin/mob_fixes.json`; each
entry says why, with its source). At every start, `./hdc logs` shows `Mob fixes: N corrected: ...` when rows were
changed, and nothing when they are up to date. There is no marker: a row (a `Mob` row, or the `NpcTemplate` rows of
a template) changes only while it still holds every value upstream shipped, so a monster you changed yourself is
kept; kept rows are named at every start (`M left as they are (they hold neither upstream's values nor this
file's): Frund (Mob 3ce2...)`). If it fails (for example, a column the file names is not in the table), the start
log says `Mob fixes: not applied (...)`, the monsters keep the values they have, and it tries again at the next
start.

## Rollback

`./hdc down` stops and removes the container; the world stays in the `hearthdaoc-data` volume.
To remove everything: `./hdc down && docker volume rm hearthdaoc-data` (deletes the world).

## Upgrading to a new fork release

```bash
cd ~/hearthdaoc
./hdc update --check   # is there a newer release?
./hdc update           # back up, install the latest release, keep .env values, restart
```
`./hdc update <tag>` installs a specific release. New settings are added to `.env` with their defaults
(it lists them; for example, the release with the Shrouded Isles start choice adds
`HEARTHDAOC_SI_START_CHOICE=on`). It always backs up the world first (`-pre-update` in `./hdc backups`).

**A release for another upstream version** (the version at the start of its tag changes, for example from
0.35b to 0.36b).
`./hdc update` upgrades the world itself, with the same steps as `./hdc upgrade-world`:
- it backs up again (`-pre-upgrade`), downloads the new version's clean world (about 30 MB) and moves all
  progress into it, keeping bans and permissions;
- it keeps the RvR state that came from play too. A keep in play (held by another realm than its own, or
  claimed by a guild) keeps its realm, level and claiming guild, its doors' health and whether they are
  broken open (the health at most the door's full health in the new version at that level), and the items
  on its hookpoints. A relic away from home
  stays where it was taken, with the realm that holds it. The keep capture log is kept. Keeps nobody took
  or claimed, and relics at home, come as the new version ships them, so its own changes to them stay.
  Keeps are matched by name and region, so a keep the new version renumbers keeps its state; a keep only in
  one of the two worlds stays as the new world ships it. If the new version renamed or dropped a table or
  column this needs, that part is left out and the report says so;
- it writes a report of what it carried over (how many keeps and relics matched and were in play, and the
  keeps it could not match) and of the server settings to re-check, then starts the server and prints how
  to read the report:
  `docker exec hearthdaoc-server cat /data/archive/world-pre-upgrade-<time>/upgrade-report.txt`;
- the first start on the new version downloads the navmeshes that changed (about 570 MB for 0.35b) and
  the new version's server data files. `./hdc logs` shows the progress; report the new
  "Loading NavMesh successful" count (step 5) to the owner.

If the upgrade fails, the world stays as it was and the server is not started. Fix the cause it names,
then run `./hdc upgrade-world` and `./hdc up`. Or go back to the release you had: `./hdc update <old tag>`
(the message names it).

**From 0.34b to 0.35b.** The update to a 0.35b release is run by the 0.34b `hdc`, which does not upgrade
the world yet: it installs the release, stops before starting and says so. Then run `./hdc upgrade-world`
and `./hdc up`; the first start downloads about 570 MB of changed navmeshes. From the next upstream version
on, `./hdc update` does all of this itself.

**Keep and relic state from an archived world.** An upgrade by a release before this one left every keep,
gate and relic as the new version shipped them. `./hdc carry-rvr` lists the archived worlds (the
`world-pre-upgrade-<time>` one is the world before that upgrade). Then, with the server stopped:
```bash
./hdc stop && ./hdc carry-rvr world-pre-upgrade-<time> && ./hdc up
```
It backs up the world first (`-pre-carry-rvr` in `./hdc backups`), then copies the same state an upgrade
keeps from the archived world and prints the same report. The keeps in play and the relics away from home in
the archived world get the state they had there, so captures of them made since are undone; the other keeps
and relics stay as they are now. A guild claims one keep: a carried claim is left out (the keep still gets
its realm and level) when that guild holds another keep in the world now, and the report names it.
Hookpoint items and capture log entries are added to those already there. The 0.34b world's own Dun
Abermenai and Dun Murdaigean carry onto 0.35b's.

Without `./hdc update` (a deployment older than it), do it by hand:
```bash
cd ~/hearthdaoc && ./hdc backup && ./hdc stop
mkdir -p new && cd new
curl -fLO https://github.com/lometur/HearthDAoC/releases/download/<new tag>/hearthdaoc-deploy-<new tag>.tar.gz
tar xzf hearthdaoc-deploy-<new tag>.tar.gz && cp compose.yml hdc HANDOFF.md upstream.lock .. && cd ..
diff .env new/.env.example     # copy any new settings into .env (keep the owner's values)
sed -i 's/^HEARTHDAOC_TAG=.*/HEARTHDAOC_TAG=<new tag>/' .env
docker compose -p hearthdaoc -f compose.yml --env-file .env pull
./hdc upgrade-world   # only when the release is for another upstream version
./hdc up
```
