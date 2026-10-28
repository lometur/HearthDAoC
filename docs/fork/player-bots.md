# Player bots (#137)

Player bots are real player characters (`GamePlayer`) that the server plays itself. Each has a real account and
character, logs in like a player (its login is announced like anyone's), and is driven on the server by
`scripts/hearthdaoc/playerbots/`. No game client is involved: its client has no socket.

They are **not** the GameBots and do not replace them. They came from issue #137, to measure what it would cost to make
the bots real players instead of monsters (a GameBot is a `GameNPC`). They are a **test-world tool, off by default**.

## Turning them on (test worlds only)

`HEARTHDAOC_PLAYER_BOTS` in `.env` (then `./hdc up`):

| Value | Meaning |
|---|---|
| `off` (default) | Nothing: no account, no character, no log line. |
| `N` | N bots, split across the realms (Albion gets the first extra), at most 450. |
| `alb:N,mid:N,hib:N` | That many per realm, 0-150 each. |
| `0` | No bots, but test-world mode: an empty run to compare with. |

Any valid value other than `off`, even `0`, puts the server in **test-world mode** for that run:
- Discord posts are off (in memory only; the world's setting is unchanged).
- A status line is logged every minute.
- At minute 51 the server forces one full memory collection, to log the memory each bot holds. The game loop pauses for it
  (estimated 0.2-2 s).

So don't turn them on on a world people play on.

The accounts are `hdbot-alb-01`, `hdbot-mid-01` and so on. No game client can log into them or create them: the `-` is
refused at login. Without a spots file, each bot is a level-1 base-class character at its realm's start.

Two more settings are read only when bots are on:
- `HEARTHDAOC_PLAYER_BOT_WAKE`: `gamebot` (the default: the lever, below) or `player`.
- `HEARTHDAOC_PLAYER_BOT_SPOTS`: a file in the data volume, for example `/data/player-bot-spots.txt`. Each line is
  `realm|region|x|y[|z[|level|class|race]]` and places one bot there, and the bot keeps that spot.
  - A line with a level, class and race makes the bot at that level. It is not levelled through play: its specs come
    from its class's GameBot plan, and it gets gear by the GameBot rules at its first login. It spends every point a
    player has, the half levels' points from 41 on too: the points its plan's lines cannot take, now or at a later
    level, go to the lines the GameBot plan leaves at 1 until 50 (a Warrior's Thrown Weapons, a Bonedancer's second
    line), as a player spends them. Two changes to the plan are the player bots' own: a Nurture or Regrowth Druid
    trains Nature first, to 20 (by level 30), for a pet near its level (the GameBot plan leaves Nature at 1, a level-7
    pet, until 50); a Suppression Spiritmaster raises Suppression from the start (the GameBot plan leaves it at 1
    until 50).
  - A bad value or an unreadable file keeps the bots off, with a `HEARTHDAOC_PLAYER_BOTS error` line.

## What they do

Each bot thinks every 250 ms, a thin copy of the `/bot` pilot's think:
- It roams within 4,000 units of its start and hunts monsters within 5,400 that con at most yellow. Each death lowers
  that con by one step, by two at most.
- It rests after each kill, and whenever below a GameBot's rest thresholds (70% health, 45% power, 35% endurance). A
  rest stops at full health, 60% endurance, and full power, or 70% power for a caster (Wizard, Sorcerer, Runemaster,
  Eldritch, Mentalist); the quest bot's rests before an errand's walk stop there too.
- A bot with a heal heals itself below 60% health, whatever its power. Out of a fight it also heals itself below 85%
  before it sits, if half its power is left after the heal. A caster doesn't start a cast that a hit would interrupt,
  and uses Quickcast once a monster's hits interrupt it. Its own swing doesn't block its next cast: with a spell to
  cast, it stops swinging until its own swing's interrupt runs out, and it doesn't swing between casts (an instant
  spell does not mind). Between casts it keeps its spell range. A pet class summons its pet and sends it in, then
  holds its spells until the monster turns on the pet, 4 s at most, and never while the bot is being hit. A Theurgist
  casts a pet at its target at each cast choice while it can cast one, as a GameBot does, and its damage spell only
  when it can't (the target out of the pet's range or view, or a pet just refused).
- A caster (Wizard, Sorcerer, Runemaster, Eldritch, Mentalist) nukes a monster coming at it while there is time for
  a nuke and a control spell after it. Then it stuns, roots or mezzes it, in that order of choice, if the cast ends
  before the monster reaches it. In melee it uses Quickcast for the control spell. A rooted or mezzed monster near it
  would break free at the next nuke, so it first steps back to range, on a spot clear of other aggressive monsters,
  within its spell's range. It steps back only while it can cast: with hits blocking its spells and no Quickcast
  ready, or with too little power, it fights in melee as before. Only single-target spells the bot has, that the
  monster isn't immune to; a mez only on a monster above 75% health.
- An Animist pulls from beside its turret, so the monster comes into the turret's reach. When the turret can't reach
  the spot it pulls from, it releases the turret and plants a new one near the target, if the summon is ready and it
  has the power. Its bombers count as damage spells only when their payload does damage, so it no longer casts the
  root bombers (Spreading Vines and the like) as nukes.
- Each cast it picks the damage spell ready now: its bolt when off its recast, else its best single-target spell,
  else an area one, never an area one while another monster stands in its radius. It drops the low ranks a GameBot
  drops. With none ready (a Shaman's two 20-s spells both on recast), it swings until one is.
- Out of a fight it keeps its class's own self-buffs up, one cast every 5 s at most: armour factor, absorb, bladeturn,
  ablative, damage shield, damage add, stat buffs and regeneration buffs, and one chant that costs no power each pulse
  (so no resist chant: the Paladin's cost power each pulse). It uses the buffs' real power, concentration and recast;
  the server's own checks decide the rest.
- It picks a monster with no aggressive neighbours. After deaths it moves to an easier camp. It avoids what killed it
  for 15 minutes.
- A monster within 2,000 units that has the bot as its target counts as an attacker before its first hit, so the bot
  stands up and fights it first. Before it sits, a bot within reach of an aggressive monster from its last search
  walks to the nearest spot out of that reach, 1,200 units away at most.
- After a release it first steps out of the reach of every monster round it that would aggro. If it died at its camp,
  it then plans a new camp from where it died, one it can walk to from there, before it walks back. It takes no camp
  it died at in the last 15 minutes while another camp that cons yellow or easier is left. From its third death with
  no XP in between it waits 30-120 s out there, as a GameBot does, and it waits out the resurrection illness there
  too. It pulls nothing until then, but fights back. No hold for a camp lasts over 60 s: then it hunts where it is.
- It walks on the navmesh. A bot made at a level from a spots line also trains on each level-up.
- In a run of 3 bots or fewer, `hdbot-alb-01` also does quest 20206 (Immediate Resolution) and its class promotion.

What they don't do yet is under [Known shortcomings](#known-shortcomings).

## Known shortcomings

Against the GameBots (#167), and between them and a live world. Each line goes when its fix lands.

Playing:
- **Deaths:** on v0.37b-hearth.9 (`st-layers5`: 15 minutes, 390 level-matched bots) they died 9.5 times per bot-hour,
  against about 1 for the PvE GameBots: melee 5.1, casters 21.2, healers 9.4, pet classes 10.8, archers 8.2. The
  buffs, casting and pet rules above are the first step. Still open:
  - Walks don't look ahead for monsters on the route.
- **Resting:** 43% of their time on `st-layers5`. Most of it is the real cost of player regeneration.
- **Not done at all:** they don't group, buff others, take part in RvR or battlegrounds, stealth, use realm abilities
  or ride horses (#152, #167).
- **Ranged and songs:** they can't shoot or sing. Archers fight in melee, and Bards, Minstrels and Skalds have no
  songs. The level-matched kit gives no bows or instruments.
- **Trips:** they don't travel to sell, train or buy gear (#157).

Going live, without the test-world side effects:
- **No live mode:** any value but `off` puts the server in test-world mode (above). hdc will manage both kinds of bots
  and run player bots live (#171).
- **Training:** a bot spends its spec points on level-up only when it was made from a spots line with a level, class
  and race. Without one it never trains.
- **Deaths cost more live:**
  - A PvE death costs XP, and again at release unless the bot prays at its grave. It also costs constitution.
  - GameBots lose nothing.
  - At level 45 a player bot breaks even at about 0.2-0.7 deaths per hour, so live it must die about as rarely as
    the GameBots.
- **Bind and prayer:**
  - A spots bot is bound at its spot.
  - Live, a bot needs to bind at a bind stone near its camp, and to pray at its grave when it's safe.
- **Gear:**
  - A bot has only what it starts with, or the level-matched kit, and no jewellery.
  - Whether it loots and wears upgrades isn't checked.
- **Camps:** the test spots were picked to avoid orange and named monsters. A live bot's choice of camp has no such
  filter.

## The lever and the quest rule

A real player wakes every NPC within 6,000 units of it: their brains start and they move, roam and fight. A GameBot
wakes only the aggressive monsters whose aggro range it is in. With `HEARTHDAOC_PLAYER_BOT_WAKE=gamebot` (the default)
a player bot does what a GameBot does: its own client updates wake nothing (one marked line in `ClientService.cs`).
Instead, the bot wakes:
- what a GameBot would, every 1.25-1.75 s;
- its target, the monsters fighting it, each NPC it talks to and its own pet;
- the NPCs its current quests name, within 6,000 units (the owner's rule, 2026-10-22). These are each data quest's
  current step target, the names its classic stage adds (issuer, item giver, custom target, drop monsters) and each
  reward quest's giver.

NPCs near humans are woken by the humans, as always.

Not woken yet:
- the NPCs a scripted quest names in its own code;
- a bot's other pets (turrets, bombers, minions);
- a porter before the bot whispers to it.

## What they cost (measured on our PC, #137)

At the live GameBots' levels, classes and races (390 bots, minutes 20-50 of one-hour runs, PvE hunting only;
[#137 comment](https://github.com/lometur/HearthDAoC/issues/137#issuecomment-6441658806)):

| | Server CPU the bots add | vs the live server's 390 GameBots (estimate) |
|---|---|---|
| Lever on | 206 ms per second | 0.44x (0.38-0.57); 0.46x (0.39-0.61) with an estimate for missing pets |
| Lever off | 519 ms per second | 1.10x (0.96-1.44) |

Read these with their caveats:
- The bot figures are measured. The ratios are estimates: the 470 ms/s they are compared with is an estimate (410-540)
  of what the live GameBots cost on our PC.
- **PvE hunting only.** About 37% of the live GameBots were in RvR or battlegrounds, which isn't modelled and may cost
  more.
- **They are not yet GameBot-like.** They die 8-9 times per bot-hour; the live PvE GameBots die about once, and casters
  are worst (17-27). They spend about 43% of their time sitting to recover. Sitting is cheap, so the ratios are more
  likely low than high.
- A GameBot is a monster to the game: its armour comes from a monster formula, and it regains at least 10% a second once
  2 seconds out of a fight. Player bots use player rules for both, on purpose.

## Walking

A bot walks with upstream's `PlayerTravelPath` corridor cursor (`PlayerBotPathMover`). Twenty-one marked edits there
reach only player bots: `/travel` isn't registered, and `/grind` uses only `TryBuildCorridor`. If `/travel` is ever
registered, test it in game. The mover:
- starts each step from the exact spot it last put the bot on. The game stores positions in whole units, and by
  Olorustos the rounded spot lay off the navmesh, so the quest bot stood there for 14 minutes.
  `UT_PlayerBotWalkReplay` replays that walk on the installed navmesh.
- judges arrival as the driver judges range: the whole-unit distance cut to an int.
- plans the corridor again from where the bot stands after a second without a step, keeping the goal; after 3 seconds
  the walk ends blocked. The stalls left after #168 mostly had the corridor's next corner on another level than the bot
  (a ramp above it), while a fresh corridor from the same spot walked on. It does this at most twice a walk, as the
  corridor's own progress check gives up after two: a bot whose fresh corridors each led to another stall would walk
  to and fro for good. Once a goal was too few: bots stalled again where a fresh corridor walked on. A walk ends when
  it arrives or is blocked, or when its steps pause for more than a second; a goal that jumps more than 64 units gets
  two replans of its own, but the still steps count on, so a bot whose target runs on still ends blocked.
- passes a corner within reach (32 units across) only with the next corner in sight, or standing on it, as the NPC
  pathfinder passes its nodes. In Vendo Caverns a corridor turned round a wall by two corners 13 and 20 units from the
  bot; both counted as reached, so it walked at the next corner, 100 units off behind the wall, and every fresh
  corridor did the same.
- takes a step again from the floor under it, half a unit on toward the step, when its surface moves stop where they
  start. From a spot on a polygon edge Detour can take a polygon that leads nowhere: in Snowdonia a bot stood for
  minutes where 117 of 507 starts within 0.06 units of its spot didn't move, though its logged spot walked on offline.
  Further down the same walk it floated 19 units over the floor after a corridor-line step, and no move started there
  either.
- follows the corridor line, as a GameBot walks its path, when a step brings it no closer to the next corner, where the
  mesh is open and the surface moves still find it (#166). Detour's corners take the coarse polygons' height, which
  on steep ground lies far off the floor: bots stood for the rest of a run beside a corner 70 units below them, or
  under one 123 above.
- goes up or down a floor link of its corridor along the line, from the link's foot to its top, whatever the surface
  moves find: two corners at most 64 units apart across but more than 64 apart in height. In Tir na Nog the mesh joins
  two stacked floors 167 units apart over 41, with no floor between, and half way up the surface moves found a middle
  floor beside the link: the bot stood on its edge for minutes. A link starts only from its foot, where the bot reached
  the corner before: Detour's line of sight is 2D and cannot tell a link from two floors stacked, so a bot on another
  floor than its corridor is not taken through a floor.
- keeps to its own floor where the mesh joins two floors at a tile seam (#166). When tiles join at load, Detour links a
  seam edge to any edge across the seam within two climbs (57.6 units) at either end. In Jordheim eight small floors
  walled in under the city's ramps are linked that way to the ramps 70-133 units above them, and to nothing else.
  Detour's surface moves are 2D: from a ramp they stepped down onto the first linked polygon under their target, the
  floor below, and the corridor back out hid the climb in one corner, so bots stood there for minutes (one for nearly 3,
  to the end of a run). The player bots now use two queries added to the server's Detour library, which treat a seam
  link whose floors lie more than a climb (28.8 units) apart at both ends of its portal as a change of floor: the
  surface moves don't take it, and the corridor gives it as two corners at one spot, one on each floor. A floor is the
  polygon's detail surface, not its vertices' height, which on steep ground lies tens of units off it, and the corridor
  puts each of its corners on a floor (a corner 47 units over a walled-in floor took a bot onto that floor): its
  polygon's, or, where the floors on the two sides of the corner part, the floor before it, which the bot reaches it on.
  At such seams in Gripklosa one tile's floor drops 170 units where the other's doesn't, and the corner sank into the
  slope; left at the vertices' height, corners lay 47-65 units off every floor, and a bot went back and forth under one;
  and on the floor after it, a corner on a ledge 81 units over the slope that led up to it took bots onto a piece of the
  ledge with no way on. An off-mesh jump's landing keeps its own floor: a jump straight up or down lands on the
  take-off's spot, and the floor before it, the take-off's, dropped the landing from the corridor (12 such jumps in
  three zones). The bot goes straight from where it reached the first corner (up to 32 units across) to the
  second, through the ramp if need be, as on a floor link, and steps straight at its goal only once it stands on the
  second. That corner counts as reached only within 24 units of its height and, once the bot is on the climb, only at
  the corner itself: on the way down, 22.6 units above the foot, a bot took it as reached and walked back onto the ramp
  it came from. Within 300 units of its goal the bot takes the corridor instead of the straight line when the line runs
  more than 24 units under the floor, or as far over it beyond the goal's own height over its floor, or meets a wall or
  a link to another floor, before the bot would stop: the line of sight is 2D and sees through such a link, and there
  direct and corridor steps took turns, back and forth for about 15 seconds, until the churn watch planned the walk
  again. Off a ledge in Gripklosa the line floated bots over the drop, and the next corridor step took them back up onto
  the ledge, for good. With no corridor the walk ends: the line would take the bot through the floor. A walk whose
  corridor ends where the bot stands, its goal off that floor (a roam goal on a floor straight under the bot, with no
  way down), ends at once: 6 such walks stood 3 seconds each and ended stalled. Where the line runs over a drop only,
  though, the bot takes it in both cases, as before: offline, 38 walks the line took to their goals ended without it,
  37 of them at once, most in Tir na Nog and Jordheim. Such links are rare (in the nine zones replayed, 0-158 of
  1,186-202,142 seam links), and NPCs and GameBots keep Detour's own queries.
- within 300 units of its goal, walks straight at it only with a clear line of sight, or within a corner's reach (32
  units across), where the corridor has dropped its last corner; otherwise it follows the corridor (a bot crept along
  a wall 267 units from its target).
- when a walk fails or stalls, steps onto the nearest floor within 300 units that leads on, as a GameBot does, with one
  search every 5 seconds at most: bots stood off the mesh, 79-88 units under the floor or 84 above it, where every step
  lost the walkable surface, and one stood 161 units over the floor in Snowdonia, where every step stood still. A bot
  also steps onto the floor straight above or below it when that floor lies beyond the surface moves' reach (64 units),
  even where Detour's 64-unit box finds it, because the floor's polygon reaches down there: in Domnann a bot respawned
  124 units under the floor, and every walk from the spot ended.

`UT_PlayerBotWalkReplay` replays each of these walks on the installed navmesh but the replan's: offline, a fresh
corridor from those stalls walks on, and the corridor that stalled can't be rebuilt there. A fake-mesh test covers it.

**The trade-off:** on the corridor line a bot on steep ground can sink into the slope or float above it for a step or
two (about 44 units at one Snowdonia spot), and on a floor link it floats up or down the link (167 units in Tir na Nog,
70-133 out of Jordheim's walled-in floors, through the ramp above them). GameBots do the same, but a player bot should
look like a player: worth a look in game near a steep hill and in Tir na Nog. A floor step is a visible jump: 14 in
one run, of 50-198 units. Off a ledge within 300 units of its goal a bot now drops to the floor below in a step or
two, where it floated down the straight line before, and it keeps to the floor where the line runs high over lower
ground: offline, 25 of 41,297 walks took 12-66 more steps, and the walks by Jordheim's links 3.6% more in all.

Known gaps:
- **Stalls are rare.** In 15-minute runs of 390 hunting bots, 16 walks stalled with #169 (2,421 before #168's fixes,
  counting each blocked line's `count=`), 15 of them one bot on a floor under a Jordheim ramp, and 1-7 in each of four
  runs with the layer-aware queries, none in Jordheim. In the last run 44 walks of 13 bots ended blocked: 31 were walks
  to a target with no path (next gap), which `main`'s code finds no path to either, and 9 lost the walkable surface and
  stepped back onto a floor, 4 of them one bot by Tir na Nog's stacked floors, as in every run.
- **Unreachable targets come back.** A bot skips a target it found no path to for 30 seconds, then may pick it again:
  in one run a bot tried the same monster 51 times between its fights. A GameBot's target and camp choice is part of
  the parity work (#167).
- A Tir na Nog bot stalled at one spot (21263, 35215) in most runs, though the same step replayed offline walks on
  (same navmesh, same Detour library). It is likely the polygon-edge case above: the log rounds the spot, and starts a
  few hundredths of a unit apart can differ there. In the last four runs none stalled there; one stalled once beside
  the foot of the jump link by (24081, 33710), where a fresh corridor from the spot walks on (the stall line's replay).
- Within 300 units of its goal, in the open, a bot takes the plain surface move, which keeps the height of the straight
  line to the goal. Grounded steps there would also need arrival judged by the floor, for targets that hover or swim
  above it: on the Isle of Glass a bot floated up toward a corpse light 173 units over the floor, lost the floor about
  100 units up, and its walks ended there (`main` does the same).

## Log lines

All start `PLAYER_BOT_`:
- `DRIVER`, every minute: counts and time per bot;
- `WAKE`, every minute, by reason: `aggro`, `targetsBot`, `heldAggro`, `target`, `attacker`, `talk`, `pet`, `quest`.
  An NPC counts once per think, under its first reason: target, attacker or pet, then quest, then the scan's (an
  aggressive monster a quest names counts as `quest`);
- `FIGHT`, `DEATH`, `RELOCATE`, `IDLE`, `BLOCKED`: one per event. A stalled walk's `BLOCKED` line ends with `stall:`,
  the bot's last step and state, and `queries:`, the stalled step's own navmesh queries and a fresh corridor's step.
  `FIGHT` ends with `buffs=` (its self-buffs up when the fight began), `petCasts=` (a Theurgist's pets cast at the
  target), `waits=` (the thinks a cast waited for its own swing's interrupt), `ctl=` (a caster's control casts) and
  `kites=` (its steps back). `DEATH` ends with `again=` (no kill since its last release), `sinceRelease=` (the seconds
  since that release, -1 if none since login) and `ill=` (the resurrection illness on when the fatal fight began).
  `BLOCKED`'s `walk=` is `target`, `roam`, `clear` (the step out after a release), `rest`, `kite` or `turret`;
- `REST`: one per rest, when the bot stands: `sec=`, the seconds it sat, and `why=`: `recovered`, `hit`, `incoming` (a
  monster coming at it), `heal`, `pet` or `buff` (a cast on itself), `died`, or `other`;
- `FLOOR_STEP`: a step onto the nearest floor, with where from and to and why;
- `REPLAN`: a stalled walk planned again, with where, the goal, the count in that walk and the stall's first step;
- `ERRAND`: the quest bot's steps;
- `SPOT`, `KIT`, `RETRAIN`: start spots and level-matched bots; `TALLY`, every few minutes, a line per bot.

The bot CPU meter (`/botcpu`) charges them to its own kind, `playerbot`, and their pets with them.

## Tests

`Tests/UnitTests/UT_HeadlessClient.cs`, `UT_PlayerBotPlan.cs`, `UT_PlayerBotDriver.cs`, `UT_PlayerBotErrands.cs`,
`UT_PlayerBotWake.cs`, `UT_PlayerBotSpots.cs` and `UT_PlayerBotKit.cs` run in CI. `UT_PlayerBotWalkReplay.cs` runs only
when asked for. It needs `OFFLINE_DAOC_NAV_ROOT`: a folder holding `navmesh/zone008.nav`, `zone012.nav`, `zone014.nav`,
`zone120.nav`, `zone126.nav`, `zone152.nav`, `zone181.nav`, `zone209.nav` and `zone250.nav` (each from
`tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/server/navmesh/zone008.nav
FOLDER/navmesh/zone008.nav`, and so on) and under `lib/` the Detour library built from `source/server/Pathing/Detour`,
as `deploy/Dockerfile` builds it: without the layer-aware queries the Jordheim and jump-link replays fail.
