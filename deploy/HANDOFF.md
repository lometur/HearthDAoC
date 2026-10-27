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
- **Download:** `./hdc init` (about 630 MB from GitHub, verified, resumable).

Then bring back the leveling monsters upstream archived (the owner chose levels 1-20, OpenDAoC's
density; takes a few seconds and is re-applied automatically by new-world and upgrade-world):
`./hdc spawns restore --max-level 20` and check `./hdc spawns status` (expect `restored` about 12,900).
It leaves out an archived monster within 1000 units of a quest's kill target named like it but for case (an
"arawnite messenger" beside level 20's "Arawnite Messenger": quests match the exact name, so killing it counted
for nothing). A world restored before this was fixed loses those twins at its next start, and `./hdc fixes` says
`Restored spawns: N twins of quest targets removed ...`; they stay in upstream's archive, as after
`./hdc spawns undo`.

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
docker exec hearthdaoc-server grep -c "Loading NavMesh successful" /data/logs/server.log   # 103 (upstream 0.35b to 0.37b)
docker exec hearthdaoc-server grep "Server is now listening" /data/logs/server.log | tail -1   # 0.0.0.0:10301
docker exec hearthdaoc-server sqlite3 /data/world/opendaoc.sqlite3.db "SELECT DISTINCT Port FROM Regions"   # 10401
docker ps --format '{{.Names}} {{.Status}}' | grep -i opendaoc   # OpenDAoC still up, unchanged
```
Then ask the owner to connect from their PC (`~/Games/HearthDAoC/play.sh` after running
`setup.sh --server <this machine's LAN IP>:10301 ...`) and confirm the login works.

## 6. Day-to-day

`./hdc help` lists everything: status, logs, fixes, add-bots, accounts, bot goals, backups, restore,
new-world, upgrade-world, carry-rvr, auto-accounts, spawns. `./hdc fixes` shows what the world fixes below did at
the last start, running or not: each start appends their lines, under a line with the time and the release, to
`/data/logs/world-fixes.log`, which keeps the last 20 starts (`./hdc logs` shows only the last 200 lines, long past
them). Backups run daily into
the volume (keep 7). The copies taken before add-bots keep their newest 3; the copies taken before restore, upgrade
and carry-rvr are kept until you remove them (`./hdc backups` lists them; they are in /data/backups).
Settings live in `.env` (see `.env.example`), e.g. `HEARTHDAOC_AUTOSAVE_MINUTES` (default 5),
`HEARTHDAOC_GM_ONLY_COMMANDS` (default `/tele;/tc`: single-player teleports need GM rights) and
`HEARTHDAOC_SI_START_CHOICE` (default `on`: a new level-1 character of a classic race is asked once
whether to begin in its realm's Shrouded Isles town; `off` turns the question off) and
`HEARTHDAOC_BOT_RP_TIMER_WAIT` (default `on`: a gamebot released after a death in RvR or a battleground waits
at a safe spot and comes back just as it is worth realm points again; `off` sends it straight back) and
`HEARTHDAOC_BOT_CPU` (default `off`: `on` starts the bot CPU meter with the server, which every minute writes
the game loop's CPU per kind of bot and player to the server log; a GM's `/botcpu` turns it on or off in game
until the next start) and `HEARTHDAOC_PLAYER_BOTS` (default `off`: player bots, real characters the server plays
itself, for test worlds only; any valid value other than `off`, even `0`, also turns Discord posts off for the run and
pauses the game loop once at minute 51, so leave it `off` on the world people play on); after editing `.env`,
`./hdc up` recreates the server with them.
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

**Bot CPU meter.** It measures the game loop's CPU per kind of bot and player, to compare what each kind costs. It is
off unless `HEARTHDAOC_BOT_CPU=on` or a GM turns it on in game, and while on it costs about 1-5% of one core. As a GM:
`/botcpu` shows its state, the last minute and the totals since on; `/botcpu on [minutes]` turns it on, for 1-1440
minutes or until `/botcpu off` or the next start; `/botcpu off` stops it and writes the last partial minute;
`/botcpu top [gamebot|companion|playerbot|piloted]` lists the 5 costliest bots of the last minute; `/botcpu reset`
starts the totals since on again. For a GM, `/bot` is short for `/botcpu`, so `/bot on` turns the meter on. Every
minute while it is on, the server log gets one `BOT_CPU` line per kind (`msPerBotSec` is the average CPU per bot, in
ms per second), the 5 costliest bots of each bot kind (`BOT_CPU_TOP`) and a whole-process line (`BOT_CPU_PROCESS`);
`BOT_CPU_STATE` lines say when it turned on and off. The server starts a new log file every day and keeps a week of
them, so read them all, oldest first (`./hdc logs` shows only the last 200 lines, then the new ones as they come):
```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -40
```
The same minutes go to `/data/logs/bot-cpu.csv`, one row per minute and kind, for charts (its stage columns are
whole-kind totals; above 50 MB it becomes `bot-cpu.csv.1`): `docker exec hearthdaoc-server cat /data/logs/bot-cpu.csv`,
or `docker cp hearthdaoc-server:/data/logs/bot-cpu.csv .` to copy it out. The owner's checks on the live server are in
`docs/fork/verification/bot-cpu-meter-live.md`.

**Duplicate townspeople.** Upstream's world has two copies of Ley Manton and of Tria Ellowis at the same
spot (the merchant and a plain NPC), and two Master Graents, the fighter trainer, in Prydwen Keep: one in the
keep's middle structure, by the healer and the town crier, and one among the merchants just outside. It also
has a hostile level-49 "commander" standing on the friendly Albion Commander by Camelot's north gate, which
attacks Albion players. At every start, a plain copy standing exactly on its merchant is removed, and so are
the Master Graent outside and the hostile commander (only those rows, while they are still at their spots and
the one that stays is there); each is kept in `fork_removed_mobs` (FixId `duplicate-townspeople`).
`./hdc fixes` shows `Duplicate townspeople removed, archived in fork_removed_mobs: ...` with their names; a
world that lost the others at an earlier start shows only `commander`. Nothing else is touched, and a world
without the copies is left alone.

**Classic battlegrounds.** At its first start, a world gets the classic battlegrounds (Abermenai 15-19,
Thidranki 20-24, Murdaigean 25-29, Caledonia 30-35); `./hdc fixes` shows each change on a line starting
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

**Quest texts.** Upstream left most of the Guild of Shadows chain's dialogue (levels 7 to 50) empty or filled
with walkthrough notes. Its quest generator also lost the line that offers some classic quests (level 5's "Cleric
Mulgrut": Hugh Gallen showed only his wares) and wrote `<Player>`, the player's own name in game, where the period
named someone else or said a plain word ("Well", Wariel's "What is your [name]?"). It also kept the class the
period guides' authors were called by ("Good luck to thee Theurgist.") and the guides' placeholders for it
("(class name)"), which every class read: they are now `<Class>`, which the server replaces with the player's class
(New Crest's accept keyword, which must match as it is, is each trainer's own class). The guides' placeholder for the
player's name ("(your name here)") is `<Player>`, and so is The Lost Stone's whisper keyword (upstream's "<your
name>", which no whisper matched: the server now matches a whisper keyword holding `<Player>` as the player reads it,
their own name, without case). Their placeholder for the player's race ("(your characters race)", at Mystemas) is
`<Race>`. The web page upstream scraped after Mystemas's last line, and the editors' notes on
Allakhazam in two kill tasks' journals, are gone. After each start, `./hdc fixes`
shows `Quest dialogue: N quests rewritten` when the texts of
the chain's quests (every class's version) or of those classic quests (`deploy/bin/quest_dialogue.json`) were
changed, and nothing when they are up to date: `Quest dialogue: 145 quests rewritten` on a new world, and
`Quest dialogue: 86 quests rewritten` (the classic quests, and the six Guild of Shadows quests whose texts changed
after hearth.13) at the first start after an update from hearth.9, hearth.10 or hearth.11, and `Quest dialogue: 6
quests rewritten` after one from hearth.12 or hearth.13 (a world an older release started also gets the chain's quests
rewritten). Besides the texts (accept keyword, description, journal steps and
what the NPCs say), the file can set a quest's step items, step types and turn-in items, but never its number of
stages: a quest whose stages differ from the file's is kept. It can also set a quest's giver (`StartName`) and the
quests it needs (`QuestDependency`): the Shrouded Isles 7 and 11 ("Strange Beings", "Shades and Shadows") are given
by each class's own trainer in Caer Gothwaite (upstream named the Necromancer trainer Carys for every class), and
each 11 needs the 7 of its own branch, so a character who took the Shrouded Isles 7 goes on with the Shrouded Isles
11 and one who took Camelot's with Camelot's, as in the period. There is no marker: a quest is rewritten only
while its texts
still hold upstream's or an earlier version of this file's, so a quest whose text you changed yourself is kept,
and a later release with revised text reaches a world that had the earlier one. Kept quests are named at every
start (`M left as they are (their text differs from upstream's and this file's): 21500, ...`): your own edits,
or, after an upstream upgrade, quests whose upstream text changed, which keep upstream's text until the file is
updated. If it fails, the start log says `Quest dialogue: not applied (...)`, the quests keep the
text they have, and it tries again at the next start. The NPCs' chat lines (the chain's, and the classic
quests' lost lines, names, words and class words) are in `hearthdaoc-quests.json` (`Chat`), which replaces upstream's line for the same NPC
and keyword. To revise the text: edit it in `quest_dialogue.json`, run
`python3 deploy/bin/quest_dialogue.py --seal` (it appends the digest of the file's own text to each quest's guard
list) and commit; a test fails until you do. Every committed version of the text is then guarded, so a later
revision reaches a world that holds any earlier committed version. Only for a new entry, or when an entry's set of
columns changes, is `python3 deploy/bin/quest_dialogue.py --digests <world.db> [--current]` still needed (it prints
the digests of the text a world holds, which is upstream's only on a clean world).

**Quest camps.** Upstream archived some quest camps with its post-legacy spawns; the fork copies four back, once per
world (`deploy/bin/quest_camps.json`): Delfina's Tylwyth Teg rangers (level 43), the isolationist camp of the level-45
courier, the Ellyll Seer's village (level 48) and Lieutenant Grimarth at Sir Dillus's tower. The first start after
the update shows `Quest camps: copied back from upstream's archive: Delfina's camp (22), the isolationist camp (39),
the Ellyll Seer's village (30), Lieutenant Grimarth (1)` in `./hdc fixes`; later starts say nothing, and rows you
remove with `/mob remove` stay removed. The same file adds Lieutenant Cian beside Captain Rhodri on Snowdonia
Fortress's north wall and archives two rows the period didn't have (the generated "Bline" beside the merchant Bline
Tengit, and the standing Martley, who now appears at the neophytes' camp). Four of these camps are linked: attack
Delfina, Sir Dillus, the isolationist courier or the Savant and their companions join in (`Links` in
`hearthdaoc-quests.json`; remove a group there to switch it off). It also gives level 5 "Cleric Mulgrut"'s Mulgrut
Maggot his escort (two decayed zombies and a putrid zombie that follow him and fight with him: `Fellows` in
`hearthdaoc-quests.json`) and his walk (the path `HDC_Mulgrut_CemeteryToMenhir`: around the Camelot Hills cemetery,
out to the tall stone on the hill north-east and back). The first start after that update adds `added: ..., Mulgrut
Maggot's first decayed zombie, Mulgrut Maggot's second decayed zombie, Mulgrut Maggot's putrid zombie; paths added:
Mulgrut Maggot's walk (12 points)` to the line. A walk whose PathID is taken, or a walker already on another path, is
named there (`not added: ...`, `left on another path: ...`). To stop his walk, clear his PathID with the server
stopped; he then roams up to 500 around his spawn in the cemetery, the walk is not put back, and the start log stays
quiet. Not with `/mob path` and no name: that also saves where he stands and the way he faces as his spawn, so done
while he walks it moves his spawn for good, and every start then names him as left as he is. With your
`HEARTHDAOC_TAG`:
```bash
./hdc stop
docker run --rm --network none --user 1000:1000 -v hearthdaoc-data:/data --entrypoint sqlite3 \
  ghcr.io/lometur/hearthdaoc:<HEARTHDAOC_TAG> /data/world/opendaoc.sqlite3.db \
  "UPDATE Mob SET PathID='' WHERE Mob_ID='d5913594-869f-5d90-bcea-fce428f63926'"
./hdc up
```

**Mob fixes.** Period corrections to upstream's monsters, items and quests, from the quest archives: level 11's thieves
Frund and Agisthil are level 10, Frund stands at the red dwarf camp beside Agisthil, "Zombie Problem"'s Cloudy Green
Stones sell for 50 copper, and ten classic quests that handed their reward twice (two Belts of Animation from "Cleric
Mulgrut", two stones per hog tail, four flint weapons per chert) hand it once, and the Guild of Shadows level 43 comes
from each class's own trainer, not the Cabalist trainer Magus Agyfen; the rest of the Guild of Shadows epic as the
period had it: Sir Dillus in the renegade tower, Delfina a level-45 Tylwyth Teg huntress with two rangers by her fire,
the isolationist courier in his camp, period levels, Master Eadig for the Infiltrator's chain, Magus Agyfen for the
Cabalist's 40, and the item icons; Mulgrut Maggot back in the Camelot Hills cemetery, attacking on sight; and the
three Shrouded Isles entrances (zone points 153, 165 and 167) crossed at their 1.65 portals on the Avalon Marsh pier, in
Gotar and in Shannon Estuary, where bots, `/travel` and `/stables` now go, not at the town spots in Cotswold, Mularn
and Mag Mell (`deploy/bin/mob_fixes.json`; each entry says why, with its source). After each start, `./hdc fixes` shows `Mob fixes: N corrected: ...` when rows were
changed, and nothing when they are up to date. There is no marker: a row (a `Mob` row, the `NpcTemplate` rows of
a template, an `ItemTemplate` row, a `DataQuest` row or the `ZonePoint` rows of an Id) changes only while it still holds every value upstream shipped, so a monster you changed yourself is
kept; kept rows are named at every start (`M left as they are (they hold neither upstream's values nor this
file's): Frund (Mob 3ce2...)`). The entries that together decide one quest's rewards (say, a carried-only copy
made the reward and the quest's second copy dropped) form a group, which changes only whole: if one of its rows
holds neither value, none of them changes, so a quest never hands two real rewards, or none, and the start log
names the group at every start (`1 group left as it is (...): Strange Bedfellows' reward (Perfect Life Stone
(ItemTemplate cq_perfect_life_stone))`). The three Shrouded Isles entrances are one group too: if you moved one
yourself, none of them moves. If it fails (for example, a column the file names is not in the table), the start
log says `Mob fixes: not applied (...)`, the rows keep the values they have, and it tries again at the next
start.

**Quest rewards handed once.** Seven classic quest rewards are handed during the quest, by an NPC or as a named
monster's drop (upstream's grants): Mystemas's Perfect Life Stone in "Strange Bedfellows", Lilib's Pious Ring in both
versions of "Darksong's Dirge", Jytal's Ring of Spun Silk and Besje's Herbalist's Hauberk of Ivy in "Dark Places of
the Soul", the bracer Scurceol Hyrde drops in "Ogre's Might" and Hilde's Brack's Hammer in "Brack: Rollo's Story".
Each is handed once per character and quest, also when it is put in the account vault, sold or destroyed, or the
quest is given up and taken again: a row of the character's in `DOLCharactersXCustomParam`, KeyName
`hearthdaoc_quest_grant`, remembers it (its value is `<quest ID>:<item>`, such as `20975:cq_perfect_life_stone`). To
hand one again, a GM can make the item (`/item create cq_perfect_life_stone`) and trade it, or delete that row, but
only with the server stopped: the running server reads a character's rows once and keeps them in memory, so a row
deleted while it runs hands nothing until the next start. With your `HEARTHDAOC_TAG` and the character's name:
```bash
./hdc stop
docker run --rm --network none --user 1000:1000 -v hearthdaoc-data:/data --entrypoint sqlite3 \
  ghcr.io/lometur/hearthdaoc:<HEARTHDAOC_TAG> /data/world/opendaoc.sqlite3.db \
  "DELETE FROM DOLCharactersXCustomParam WHERE KeyName='hearthdaoc_quest_grant' AND Value='20975:cq_perfect_life_stone'
   AND DOLCharactersObjectId=(SELECT DOLCharacters_ID FROM DOLCharacters WHERE Name='<character>')"
./hdc up
```

**Route fixes.** A stable horse rides in a straight line from one point of its route to the next, and most routes
crossed the Magical Bridge north of Camelot, the Prydwen Keep bridge (PKB) and nine other bridges on
bank-to-bank stretches, up to 456 under the walkway, so the horse swam in the river below. The start adds points along
each bridge's walkway, 15 over it, to 138 crossings of 59 horse routes in Albion, Midgard and Hibernia
(`deploy/bin/route_fixes.json`; the heights were measured on a client, and only the numbers ship). The first start
shows `Route fixes: 138 bridge crossings of 59 horse routes given points on the deck: ...`, and later starts nothing.
There is no marker: a crossing gets its points only while its two route points still follow each other at
upstream's values (or hold this fix's own points, which a later release may re-tune), so a route you changed
yourself is kept and named at every start (`N left as they are (the route
no longer has that segment): ...`). If it fails, the start log says `Route fixes: not applied (...)` and the routes
keep their points. Bots ride the same routes, so their horses stay on the bridges too.

**Start towns.** In the period a new character's race and base class fixed its starting town, beside its base-class
trainer. Upstream gets 9 of the 56 starts wrong, and the start moves them: a Briton Mage starts at Adribard's Retreat
(Avalon Marsh, not Cotswold), a Dwarf Seer in Vasudheim, a Kobold Seer in Haggerfel, a Norseman Seer at Fort Atla and an
Elf Stalker in Mag Mell; and the Shrouded Isles base classes of the classic races start only in their Shrouded Isles
city, as before patch 1.72: the Briton Disciple in Caer Gothwaite, and the Celt and Firbolg Forester in the Grove of
Domnann. So does the Troll Mystic, which can only become a Bonedancer, in Aegirhamn (on one source, Allakhazam 2004).
Those four are not asked the first-login Shrouded Isles question, which skips anyone already there. Four final-class
rows follow (Highlander Scout to Ludlow, Elf Blademaster and Champion to Howth, Norseman Healer to Fort Atla): the bots
start from them, and the server sends a promoted character there if it has to repair a broken bind point. Each spot is copied from an upstream row that already
stands in that town (`deploy/bin/start_towns.json`, with each move's dated sources). Only new characters and bots are
affected: existing characters keep their location and bind point. The first start shows `Start towns: 13 starting
locations moved to the period's towns: ...`, and later starts nothing. There is no marker: a row moves only while it is
still at upstream's spot (region, X, Y, Z and heading), so a start you moved or turned yourself is kept and named at
every start (`N left as they are (at neither upstream's spot nor the period's): Briton Mage (StartupLocation 523)`). If
it fails, the start log says `Start towns: not applied (...)` and the starting locations stay where they are.

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
`HEARTHDAOC_SI_START_CHOICE=on`, the one with the realm point waits adds `HEARTHDAOC_BOT_RP_TIMER_WAIT=on`, and
the one with the bot CPU meter adds `HEARTHDAOC_BOT_CPU=off`).
It always backs up the world first (`-pre-update` in `./hdc backups`).

**A release for another upstream version** (the version at the start of its tag changes, for example from
0.35b to 0.36b).
`./hdc update` upgrades the world itself, with the same steps as `./hdc upgrade-world`:
- it backs up again (`-pre-upgrade`), downloads the new version's clean world (about 37 MB for 0.37b:
  upstream ships it as the 0.36b world plus an update, which the upgrade applies) and moves all progress into it,
  keeping bans and permissions;
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
- the first start on the new version downloads the navmeshes that changed and the new version's server data
  files (about 570 MB for 0.35b; about 3.2 MB for 0.37b, whose zone navmeshes are 0.35b's: only its road map and
  pocket list are new). `./hdc logs` shows the progress; report the new "Loading NavMesh successful" count
  (step 5) to the owner.

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
