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
    from its class's GameBot plan, and it gets gear by the GameBot rules at its first login.
  - A bad value or an unreadable file keeps the bots off, with a `HEARTHDAOC_PLAYER_BOTS error` line.

## What they do

Each bot thinks every 250 ms, a thin copy of the `/bot` pilot's think:
- It roams within 4,000 units of its start and hunts monsters within 5,400 that con at most yellow. Each death lowers
  that con by one step, by two at most.
- It rests to full after each kill, and whenever below a GameBot's rest thresholds (70% health, 45% power, 35%
  endurance).
- A bot with a heal heals itself below 60% health. A caster doesn't start a cast that a hit would interrupt, and uses
  Quickcast once a monster reaches it. A pet class summons its pet and sends it in.
- It picks a monster with no aggressive neighbours. After deaths it moves to an easier camp. It avoids what killed it
  for 15 minutes.
- It walks on the navmesh. A bot made at a level from a spots line also trains on each level-up.
- In a run of 3 bots or fewer, `hdbot-alb-01` also does quest 20206 (Immediate Resolution) and its class promotion.

They don't group, take part in RvR or battlegrounds, buff or sing, use bows, stealth, use realm abilities, ride horses,
or travel to sell or train.

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

A bot walks with upstream's `PlayerTravelPath` corridor cursor (`PlayerBotPathMover`). Eleven marked edits there reach
only player bots: `/travel` isn't registered, and `/grind` uses only `TryBuildCorridor`. If `/travel` is ever
registered, test it in game. The mover:
- starts each step from the exact spot it last put the bot on. The game stores positions in whole units, and by
  Olorustos the rounded spot lay off the navmesh, so the quest bot stood there for 14 minutes.
  `UT_PlayerBotWalkReplay` replays that walk on the installed navmesh.
- judges arrival as the driver judges range: the whole-unit distance cut to an int.
- plans the corridor again from where the bot stands after a second without a step, keeping the goal; after 3 seconds
  the walk ends blocked. The stalls left after #168 mostly had the corridor's next corner on another level than the bot
  (a ramp above it), while a fresh corridor from the same spot walked on. It does this at most twice a goal, as the
  corridor's own progress check gives up after two: a bot whose fresh corridors each led to another stall would walk
  to and fro for good. Once a goal was too few: bots stalled again where a fresh corridor walked on.
- passes a corner within reach (32 units across) only with the next corner in sight, or standing on it, as the NPC
  pathfinder passes its nodes. In Vendo Caverns a corridor turned round a wall by two corners 13 and 20 units from the
  bot; both counted as reached, so it walked at the next corner, 100 units off behind the wall, and every fresh
  corridor did the same.
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
- within 300 units of its goal, walks straight at it only with a clear line of sight, or within a corner's reach (32
  units across), where the corridor has dropped its last corner; otherwise it follows the corridor (a bot crept along
  a wall 267 units from its target).
- when a walk fails or stalls, steps onto the nearest floor within 300 units that leads on, as a GameBot does, with one
  search every 5 seconds at most: bots stood off the mesh, 79-88 units under the floor or 84 above it, where every step
  lost the walkable surface, and one stood 161 units over the floor in Snowdonia, where every step stood still.

`UT_PlayerBotWalkReplay` replays each of these walks on the installed navmesh.

**The trade-off:** on the corridor line a bot on steep ground can sink into the slope or float above it for a step or
two (about 44 units at one Snowdonia spot), and on a floor link it floats up or down the link (167 units in Tir na
Nog). GameBots do the same, but a player bot should look like a player: worth a look in game near a steep hill and in
Tir na Nog. A floor step is a visible jump: 14 in one run, of 50-198 units.

Known gaps:
- **Brief stalls remain.** In a 15-minute run of 390 hunting bots, 20 bots logged 32 blocked walks (306 from 86 bots
  before these fixes). No bot was blocked 3 or more times at one spot, and none was stuck as the run ended (16 before).
  Most of the 14 stalls had one shape: the corridor's next corner lay on another level than the bot (a ramp above
  it), while a fresh corridor from the same spot walks on. The mover now plans again after a second (above): LIVE_REPLAN.
- A Tir na Nog bot stalls at one spot (21263, 35215) in most runs, but the same step replayed offline walks on (same
  navmesh, same Detour library). Live, the stall line's queries put the nearest mesh 5 units from the bot, so the floor
  step after a stall (above) should move it; why the mesh differs live isn't known.
- Within 300 units of its goal, in the open, a bot takes the plain surface move, which keeps the height of the straight
  line to the goal. Grounded steps there would also need arrival judged by the floor, for targets that hover or swim
  above it.

## Log lines

All start `PLAYER_BOT_`:
- `DRIVER`, every minute: counts and time per bot;
- `WAKE`, every minute, by reason: `aggro`, `targetsBot`, `heldAggro`, `target`, `attacker`, `talk`, `pet`, `quest`.
  An NPC counts once per think, under its first reason: target, attacker or pet, then quest, then the scan's (an
  aggressive monster a quest names counts as `quest`);
- `FIGHT`, `DEATH`, `RELOCATE`, `IDLE`, `BLOCKED`: one per event. A stalled walk's `BLOCKED` line ends with `stall:`,
  the bot's last step and state, and `queries:`, the stalled step's own navmesh queries and a fresh corridor's step;
- `FLOOR_STEP`: a step onto the nearest floor, with where from and to and why;
- `REPLAN`: a stalled walk planned again, with where, the goal, the count for that goal and the stall's first step;
- `ERRAND`: the quest bot's steps;
- `SPOT`, `KIT`, `RETRAIN`: start spots and level-matched bots; `TALLY`, every few minutes, a line per bot.

The bot CPU meter (`/botcpu`) charges them to its own kind, `playerbot`, and their pets with them.

## Tests

`Tests/UnitTests/UT_HeadlessClient.cs`, `UT_PlayerBotPlan.cs`, `UT_PlayerBotDriver.cs`, `UT_PlayerBotErrands.cs`,
`UT_PlayerBotWake.cs`, `UT_PlayerBotSpots.cs` and `UT_PlayerBotKit.cs` run in CI. `UT_PlayerBotWalkReplay.cs` runs only
when asked for. It needs `OFFLINE_DAOC_NAV_ROOT`: a folder holding `navmesh/zone008.nav`, `zone012.nav`,
`zone152.nav`, `zone209.nav` and `zone250.nav` (each from `tools/linux/odaoc_fetch.py --lock deploy/upstream.lock
extract runtime/server/navmesh/zone008.nav FOLDER/navmesh/zone008.nav`, and so on) and the Detour library under `lib/`.
