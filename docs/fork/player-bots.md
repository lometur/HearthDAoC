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
    line), as a player spends them. Three changes to the plan are the player bots' own: a Nurture or Regrowth Druid
    trains Nature first, to 20 (by level 30), for a pet near its level (the GameBot plan leaves Nature at 1, a level-7
    pet, until 50); a Suppression Spiritmaster raises Suppression from the start (the GameBot plan leaves it at 1
    until 50); and every Warden takes the Battle Warden plan, the one with a weapon line (the GameBot's other two have
    none or a weak one). On a server that keeps its bots' characters, a Warden made before that keeps the specs it
    trained (training never lowers a line) and gets the weapon line from new points only, though its `KIT` line says
    `choice=BattleWarden`; the test legs make fresh characters. A Hand-to-Hand Savage wears a claw in each hand, as a
    GameBot Savage does (Midgard has few right-hand fist weapons, so it kept its starter axe); at the levels that have
    some, its right hand picks among them and the claws.
  - A bad value or an unreadable file keeps the bots off, with a `HEARTHDAOC_PLAYER_BOTS error` line.

## What they do

Each bot thinks every 250 ms, a thin copy of the `/bot` pilot's think:
- It roams within 4,000 units of its start and hunts monsters within 5,400 that con at most yellow. A Bard, Ranger,
  Healer, Mercenary, Nightshade, Blademaster or Mentalist hunts blue at most alone, as other bots do after a death:
  their yellow pulls were fatal one time in five or more (for them a first death leaves it at blue). With no blue camp
  in its zone it takes a yellow one as its fallback, as after a death. Each death lowers that con by one step, by two
  at most. For 5 minutes after a death to a yellow or harder monster it also hunts nothing that cons as high as its
  killer did, and takes no fallback camp that cons as high: a bot killed by a yellow hunts blue at most. A blue or green
  killer changes neither (the death's own step lowers the con), and once the 5 minutes are up a bot with only yellow
  camps round it takes one as its fallback again.
- It rests after each kill, and whenever below a GameBot's rest thresholds (70% health, 45% power, 35% endurance). A
  rest stops at full health, 80% endurance (enough to sprint away from a monster after a fight that leaves it styling
  less), and full power, or 90% power for a caster (Wizard, Sorcerer, Runemaster, Eldritch, Mentalist: under 50% power
  it regains power at half the rate, so a pull that ends below half costs more rest than the last 10%); the quest bot's
  rests before an errand's walk stop there too, and a Skald's Song of Rest and an endurance regeneration buff run to the
  same 80%.
- A bot with a heal heals itself below 60% health, whatever its power. Out of a fight it also heals itself below 85%
  before it sits, if half its power is left after the heal. A caster doesn't start a cast that a hit would interrupt,
  and uses Quickcast once a monster's hits interrupt it. Its own swing doesn't block its next cast: with a spell to
  cast, it stops swinging until its own swing's interrupt runs out, and it doesn't swing between casts (an instant
  spell does not mind). Between casts it keeps its spell range. A pet class summons its pet and sends it in, then
  holds its spells until the monster turns on the pet, 4 s at most, and never while the bot is being hit. A Theurgist
  casts a pet at its target at each cast choice while it can cast one, as a GameBot does, and its damage spell only
  when it can't (the target out of the pet's range or view, or a pet just refused).
- A caster (Wizard, Sorcerer, Runemaster, Eldritch, Mentalist) nukes a monster coming at it while there is time for a
  nuke and a control spell after it. Then it stuns, roots or mezzes it, in that order of choice, if the cast ends
  before the monster reaches it. In melee it uses Quickcast for the control spell. It skips the control while its
  nukes kill the monster before it arrives, at the rate the fight's nukes have taken its health so far (one nuke more
  with Quickcast ready or on, which goes to that nuke), if it has 60% health or more or the nuke it will cast next (the
  one it can pay for, or under Quickcast one it has any power for) kills the monster even at that spell's lowest
  damage, and no other monster on it. A green monster gets no control at all while the caster has 60% health or more
  and no other monster on it: it nukes it down, and keeps its power and Quickcast. A
  rooted or mezzed monster near it would break free at the next nuke, so it first steps back to range, on a spot clear
  of other aggressive monsters, within its spell's range; so it does from a stunned one while hits interrupt it. It
  tries each such spot in turn (straight away, then turned 30, 60 and 90 degrees either way), and one the navmesh puts
  too far above or below the target it tries once nearer in; in melee, if none works, it steps back 400 units straight
  away anyway, and never to a spot nearer the target than it stands. While its target is held it keeps its spell's
  range, even with hits blocking its spells or too little power (then its longest damage spell's range), and it never
  swings at a rooted or mezzed monster (its hit, or a miss, would free it): it waits instead. Not while another monster
  that isn't held hits it, and not for 5 s after a blocked step back: then it fights as before. Only single-target
  spells the bot has, that the monster isn't immune to by the server's own rules: a monster stunned or mezzed in the
  last minute takes the next one for half the time, then a quarter, and none under 1 s; and no root on a monster a slow
  holds that the root doesn't beat (the server refuses it: a damage-and-snare nuke's slow carries damage no root has),
  so it casts its next control, or none. A mez on its target only as the last escape, the monster about to reach it, and
  only on a monster above 75% health. Hit in melee by its target alone, a caster with a mez (a Mentalist) casts it
  rather than a stun that would end before its step back to range (a 6-s stun against about 6.4 s of walking from
  melee), so it walks out with it held. It puts no root or mez on a monster its own damage-over-time spell is ticking on
  (the next tick would free it), and for the break-off such a monster has no hold ready; so a root caster that put one
  on a green monster has no root for it for the spell's 20-24 s should the fight turn (the caster under 60% health, or
  an add). It casts its damage spell while it can pay that spell's cost as the server charges it (a focus caster 80-120%
  by its focus, double under Quickcast), keeping the cheapest of its controls' cost in hand while its target isn't held;
  with none to keep, a Quickcast spell goes at any power, as the server lets it. Its killing nuke under Quickcast (that
  nuke kills at its lowest damage, no other monster on it) keeps nothing in hand and goes with any power left. Every
  other class stops casting under 18% power.
- Any bot with a control spell (casters, healers, pet classes, a Skald or a Minstrel) also controls an add: the
  nearest other monster coming at it or hitting it that isn't held. A mez first, then a root, then a stun; no mez from
  a pet class (its pet would hit the add and free it), and no root on an add already in melee (it still hits from
  there). Songs that need an instrument don't count as controls (the bots carry none). It casts while the cast ends
  before the add reaches it and no hit interrupts it, else only with Quickcast, which goes to the add before the
  target. An instant control (a Skald's or a Minstrel's shout) goes at once, whatever the hits. A caster whose target
  reaches it first controls the target first, but only when it would cast that control now (ready, paid for, in range,
  a mez only as the last escape, and not on a green it nukes down). The add's control comes before the pet's lead and
  the caster's own step. It faces the add while that cast is under way, checks its own line of sight to it and keeps
  its target. After 3 casts that held nothing it stops trying on that add. A Theurgist with no control to cast casts
  its pets at an add hitting it. A mezzed or stunned add no longer counts as on the bot for the break-off, and a
  control it can cast at an add comes before one.
- An Animist pulls from beside its turret, so the monster comes into the turret's reach: with a damage turret
  (Arboreal) only while both its spell and its turret reach the monster from there, so the turret starts on it at
  once; with a snare or taunt turret (Creeping, Verdant), which slows or draws the monster on its way in, while its
  spell reaches the monster, or from its spell's reach toward the turret when the turret reaches that spot. Otherwise
  it releases the turret and plants a new one near the target, if the summon is ready and it has the power: a damage
  turret close enough, with room to spare for where the walk stops, that it reaches the monster and keeps. If it
  can't, it holds the pull up to 12 s and then leaves that monster for 30 s: it picks no fight with it then, but
  still fights it back if it attacks. Its bombers count as damage spells only when their payload does damage, so it no
  longer casts the root bombers (Spreading Vines and the like) as nukes.
- Each cast it picks the damage spell ready now. A caster (Wizard, Sorcerer, Runemaster, Eldritch, Mentalist) takes the
  single-target spell, its bolt included, that does the most damage per second of casting by the server's own numbers:
  the bot's spec in the spell's line (an unspecced line does about 60% of a specced one), the monster's level and its
  race's resist to the damage type, the land chance, and for a bolt the monster's armour. A damage-over-time spell
  counts the ticks that land before the monster would die, and goes only where it doesn't free a held monster: a stun
  caster's, or a fight with no control wanted (a green monster, the caster at 60% health or more and alone). It is never
  cast while it, or one it would only replace, ticks on that monster. A caster with a root doesn't take up a
  damage-and-snare spell its old order (below) didn't choose, since the server lets no root replace that slow. It drops
  a lower rank of a kind only for a higher one in the same line, so a Wizard, Eldritch or Runemaster with a bolt in two
  lines now casts both, each on its own 20-s recast, and a kept rank counts as a damage spell it can pay for (so it is
  out of power for the break-off a little later). Every other class: its bolt when off its recast, else its best
  single-target spell, else an area one; a caster's area ones come last too. Never an area one while another monster
  stands in its radius. It drops the low ranks a GameBot drops. With none ready (a Shaman's two 20-s spells both on
  recast), it swings until one is.
- Out of a fight it keeps its class's own self-buffs up: armour factor, absorb, bladeturn, ablative, damage shield,
  damage add, stat buffs and regeneration buffs, and one chant that costs no power each pulse (so no resist chant: the
  Paladin's cost power each pulse): the first of those kinds, but from a kill to the end of its rest a regeneration
  chant for what it is short of, so a Skald sings its Song of Rest as it sits and its combat chant again once rested.
  It looks every 5 s, and at once after a kill, before it sits, casting the missing ones back to back (a cast still
  under way at the monster it killed stops at the kill); never while it sits, until its rest is done, and a buff it
  casts as it gets up rested doesn't sit it down again. It uses the buffs' real power, concentration and recast; the
  server's own checks decide the rest. A buff under 2 minutes it no longer keeps up between fights: an instant one (a
  Reaver's Surge) goes up in a hard fight instead, at the start of one against a yellow or harder monster, or when a
  second monster comes on it; one with a cast time (a Spiritmaster's Benison) not at all yet, and a regeneration one
  (a Shaman's Frigg's Cure) only while what it regenerates is short of where the rest stops.
- It picks a monster with no aggressive neighbours, none within reach of the target, of its walk in, or of the spot
  it would fight from (its spell's range for a caster, else melee). After deaths it moves to an easier camp. It avoids
  what killed it for 15 minutes. Training dummies are no monsters: it neither plans camps at them nor pulls them.
- It swings and casts only at a target in sight, by the navmesh's line; a target hitting it in contact (within 250
  units) counts as in sight though that line says not, as a player sees what hits it. A target out of sight that
  fights neither it nor its pet, nor has the bot as its target (coming at it round an obstacle), is walked toward 300
  units at a time from where the bot stands (a caster's spell range round a cave's bend, say) until it is in sight or
  in contact. In contact, out of sight 5 s, it is dropped, and the bot's picks pass it over for 30 s, each such monster
  for its own 30 s (it still fights it back if it comes on and hits it).
- A monster its walk finds no path to it leaves, and for 30 s, each such monster for its own 30 s, neither its picks
  nor its fighting back take it (a hitter still keeps it from sitting).
- A monster within 2,000 units that has the bot as its target counts as an attacker before its first hit, so the bot
  stands up and fights it first. Before it sits, a bot within reach of an aggressive monster from its last search,
  or of the spawn spot of one of its last 3 kills in the last 150 s (where that monster respawns, 80 s after the kill
  in most of the world), walks to the nearest spot out of that reach, 1,200 units away at most. A walk there that is
  blocked takes the next nearest such spot at once, 3 walks in all; then it sits where it is.
- Losing, it breaks off: below 40% health with 2 or more monsters on it, or with 1 still on it after a kill in a fight
  that had 2 or more on it, within 3 s of that kill or in the whole fight it opens next. One monster breaks it off too
  when it is one the bot avoids (unless it was sent to a camp of them), when the bot is a caster and its target is above
  25%, or, for a bot that isn't a caster, when the race since the monster's first hit on it says the bot dies first with
  its target still above 10% (both sides' loss since that hit, read once the bot has lost 20% health since it, projects
  the target's health when the bot's runs out; a heal counts as less lost; a caster whose target is at 25% or less
  fights on for its killing nuke); not while a caster's control at that monster is under way, nor, when that monster is
  its target, while it is held or the caster would cast a control at it now or once a resisted one may go again (a
  target held at range is no hold on another monster hitting it). None of these while its target is down to 10% or less.
  A caster whose held target
  is above 25% with no damage spell it can pay for breaks off at any health, then rests: it takes no camp or names to
  avoid from that one and doesn't move camp. Only from monsters it can outrun, and only with a sprint whose lead takes
  it out of the fastest one's 200-unit reach: none faster than its run (191), or none as fast as its sprint (248) when
  its endurance holds the sprint long enough to keep the fastest off it for the 25 s a monster chases. Never from a
  monster with a snare, stun, mez or damage spell of 1,000 units' range or more. It stops its fight, sprints, and runs
  1,500-2,500 units straight away from them to a spot clear of other aggressive monsters that it can walk to, or with
  none, to the spot 1,500 units straight away from them if it can walk there; on again from there while one still chases
  it. Once the nearest chaser that isn't held is 400 units behind (more for one faster than its run: 25 s of what it
  gains), its sprint goes off, and on again when one comes within 300 units with its endurance over 10%. A spot it was
  blocked on it never takes again in that run. A mezzed, stunned or rooted monster still counts as after it. From 3 s
  into the run (time for its sprint to gain on one that started in contact), one not held that stays
  within 250 units of it for 2 s, attacking it in the last 2 s (a miss, block, parry or evade counts too), has caught
  it: it stops, casts a control at it (as at an add, with Quickcast while hit), faces it until the cast is done and runs
  on; with no control to cast it turns on the weakest monster in reach. From 30 s it is done once none is within 1,500
  units (done where another monster would aggro it, a chaser that gave up too, it runs on to its point first); still
  chased at 45 s, it turns and fights them out. With nowhere to run it tries again each second, 800 units out too (never
  into another aggressive monster's reach), and holds where it stands meanwhile: it turns when a chaser that isn't held
  comes within 250 units or one hits it while it holds, and a caster or pet class that can still pay for a damage spell
  turns at once, to open at range. While it holds it heals itself as it would have once the break-off ended, before the
  chaser reaches it. Afterwards it takes no camp there and avoids those monsters for 15 minutes, and plans
  a new camp from where it ran to before it looks for a new target. A break-off on the walk to its camp, more than 1,400
  units short of it, bars that camp too for 15 minutes.
- In a close fight (2 or more monsters on it now or earlier in the fight, a defence, the fight after a kill with adds,
  or a fight the race since the first hit on it projects it to end under 30% health) a bot keeps the endurance a sprint
  away from the fastest monster on it would need, plus 5: 31 against a monster no faster than its run, up to 41 against
  one of speed 204. Its styles, and for a class that isn't a caster or pet class its damage spells (5 endurance a cast),
  wait while they would take it under that; a cheaper style that leaves it goes instead. Heals don't wait, nor does a
  lifetap (a Reaver's Siphons, a Valewalker's Cycle of Life): it heals the bot too. Against a monster of speed 205 or
  more it keeps none, since no sprint it could keep would outrun it. It waits only while waiting can still buy a run:
  not once its endurance is under what that sprint needs (21 against a monster of speed 148-181, 26 against 182-197, 31
  against 198-202), since endurance doesn't come back in a fight, and not while its target, the only monster on it, is
  at 10% health or less, since a bot never breaks off from a target that low. With another monster on it, it keeps
  waiting there, for the run from that one at the kill. Out of a close fight it styles as before.
- It releases 10 s after a death, counted from when the server has handled the death, never before: a bow's or a bolt's
  killing hit lands in the bots' own phase of the game loop, and a release asked for then was undone by the server's
  death handling later in the same tick, which left the bot dead for 15 minutes. A release that didn't revive it is
  asked for again every 10 s.
- After a release it first steps out of the reach of every monster round it that would aggro, a keep guard of another
  realm or of none too. Such a guard also counts as an add when it picks a target, a spot to rest and where to run in a
  break-off, but it never pulls one. Wherever it died (at its camp or on a walk), it then plans a new camp from where it
  died, one it can walk to from there, before it walks back. Within 15 minutes of a death (a died, break-off or capped
  plan) it first takes a camp within 10,000 units of where it stands, in the zone it stands in, that it can walk to:
  one that fits its cap, else the safest that cons blue or easier, never one it died at; only with none it plans as
  before. That gain depends on where it binds: on the test spots each bot binds at its camp, so a near camp is there;
  a bot bound in a town gains little. It plans at once, while the release's 10-s immunity lasts: the plan counts the
  monsters it may not attack until then. With a camp planned and the immunity still on, no monster may attack it, so it
  walks out toward the camp instead of standing: sprinting, from spot to spot out of every aggressive monster's reach,
  each the one that leaves the least of the way, as far as the immunity left lets it get, and from a clear spot only to
  one nearer the camp. Its sprint goes off when that walk ends. It casts no self-buff and summons no pet until the
  immunity is over: it buffs where the walk ends. It takes no camp it died at in the last 15 minutes while another camp
  that cons yellow or easier is left, nor the camp it was walking to when it died more than 1,400 units short of it.
  From its third death with no XP in between it waits 30-120 s out there, as a GameBot does, and it waits out the
  resurrection illness there too. It pulls nothing until then, but fights back. No hold for a camp lasts over 60 s: then
  it hunts where it is.
- At login it also first steps out of the reach of every monster round it that would aggro, then hunts: no wait and
  no new camp. A caster rests first, after its login buffs, as after a kill; a life after a release needs no such rest
  (the release restores full power).
- It walks on the navmesh. A bot made at a level from a spots line also trains on each level-up.
- In a run of 3 bots or fewer, `hdbot-alb-01` also does quest 20206 (Immediate Resolution) and its class promotion.

What they don't do yet is under [Known shortcomings](#known-shortcomings).

## Known shortcomings

Against the GameBots (#167), and between them and a live world. Each line goes when its fix lands.

Playing:
- **Deaths:** over two legs of the same code (`st-deathsD2` and `st-deathsD2b`: 15 minutes each, 390 level-matched
  bots on the `spots-lm3` test spots) they die 1.4 times per bot-hour, against about 1 for the PvE GameBots: melee
  1.3, casters 1.4, healers 2.1, pet classes 0.9, archers 2.4. v0.37b-hearth.11 on the same spots (`st-hearth11` and
  `st-hearth11b`) died 1.8 times, casters 4.2. One leg is too short to judge a small change: the two legs of this
  code died 149 and 118 times. The older spot list put 30 bots where they couldn't hunt (by a battleground keep, in
  a dungeon); `spots-lm3` moves the 26 whose spot was the cause, so legs before `st-hearth11` (2.1 on `st-deathsC`,
  9.5 on v0.37b-hearth.9's `st-layers5`) don't compare one for one. Still open:
  - Break-offs that fail. A bot with too little endurance to sprint doesn't break off (81 and 66 refusals on the two
    legs). On `st-deathsD2`, 39 deaths came within a minute of a refusal, among them 19 of the 21 deaths to a second
    monster. A bot caught on the run, or with nowhere further to run, turns and fights and usually dies: 33 of 38
    within a minute, against 20 of 36 on v0.37b-hearth.11. Deaths within a minute of a break-off are 31% of
    break-offs, against 36%. The late start stays: `wouldBreak=` only logs a projected break-off.
  - Casters: their holds now last (their own swing ends 1% of controls, 14% on v0.37b-hearth.11), and deaths of a
    caster stuck in melee with one monster fell from 66 to 9 over two legs. A long held fight still runs their power
    low, most of all a Mentalist's long pull of a blue monster, now the largest kind of caster death. The log can't
    tell a control the server refused from one the monster resisted. A caster seldom controls the add: its Quickcast
    has usually gone on the target before the add comes.
  - After a release a bot dies again before its next kill 18% of the time, against 36% on v0.37b-hearth.11. A died
    plan bars the old camp, not the route to the new one, so a bot whose route passes the monster that killed it can
    die on the same stretch again.
  - For 5 minutes after a yellow or harder monster kills it, a bot takes no camp that cons as high. Where its level
    has no other camp (3 or 4 bots a leg), it waits at its bind, and a bind inside an aggressive camp is attacked.
  - A bot can stay minutes in a fight with a monster it can't see: on `st-deathsD2b` three bots fought one cluricaun
    for over 4 minutes, and that camp sticks on every leg, v0.37b-hearth.11's too.
  - A camp of training dummies counts as a camp: a bot sent there fights nothing.
  - Walks don't look ahead for monsters on the route.
- **Resting:** about half their time (51% over the two legs, 50% on v0.37b-hearth.11). Most of it is the real cost
  of player regeneration.
- **Not done at all:** they don't group, buff others, take part in RvR or battlegrounds, stealth, use realm abilities
  or ride horses (#152, #167).
- **Ranged and songs:** they can't shoot or play. Archers fight in melee, and Bards and Minstrels have no songs: theirs
  need an instrument (a Skald's chants don't). The level-matched kit gives no bows or instruments.
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
- **They are not yet GameBot-like.** They die 1.4 times per bot-hour (`st-deathsD2` and `st-deathsD2b`); the live
  PvE GameBots die about once. Healers (2.1) and archers (2.4) die most. They spend about half their time resting
  (51%). Sitting is cheap, so the ratios are more likely low than high.
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
  in one run a bot tried the same monster 51 times between its fights. Each such monster has its own 30 s: with one
  for all, a Thane on a pocket of Muspelheim's navmesh above its camp, with no path down, took two monsters in turn
  every 2 s for its last 3.5 minutes, four blocked walks a second. Nor did it move camp, which a bot does after 2
  minutes with no target reached: a step toward a monster out of sight counted as one reached, and that walk was
  blocked too. A step that ends blocked counts for nothing now. On a pocket the move may still only reach a monster on
  the pocket, and how the bot got onto it is open (likely a short direct step there). A GameBot's target and camp
  choice is part of the parity work (#167).
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
  target), `waits=` (the thinks a cast waited for its own swing's interrupt), `ctl=` (a caster's control casts),
  `kites=` (its steps back), `ctlBroken=` (the roots and mezzes on its target its own swing ended), `addCtl=` (its
  control casts at an add, in `ctl=` too), `addAt=` (the seconds into the fight a second monster first hit the bot or
  came at it; 0 when one was on it by the fight's first think in reach), `addHp=` (the bot's health then), `breakOff=`
  (1: a break-off dropped the fight), `engageDist=` (its distance to the target at that first think) and `crowd=` (the
  potential adds round the target and along the walk in when it was picked; -1 for a defence or a monster coming at
  it). The add is seen from the bot's attacker list each think (only a live monster on the bot, not the last fight's
  dead target) and, for a bot with a control or a Theurgist's pets, from a scan every 2 s. `DEATH` ends with
  `again=` (no kill since its last release), `sinceRelease=` (the seconds since that release, -1 if none since login)
  and `ill=` (the resurrection illness on when the fatal fight began), then the fatal fight's `ctl=`, `kites=`,
  `casts=`, `qc=`, `waits=`, `ctlBroken=`, `addCtl=`, `addAt=`, `addHp=` and `engageDist=` as `FIGHT` gives them (-1
  out of a fight), and `breakOff=` (1: it died running from a break-off; out of a fight then, as the break-off dropped
  it, so that fight's own fields are on its `FIGHT result=dropped breakOff=1` line). Both then give `heldSec=` (the
  seconds the target was held: stunned, mezzed or rooted), `ctlLanded=` and `ctlFailed=` (control casts that held or
  didn't: resisted, refused or interrupted; one still under way when the fight ends counts neither), `hpAtReach=` and
  `tHpAtReach=` (the bot's and its target's health at the first attack on the bot, a miss or block too; -1 if none),
  `rate=` (the health lost a second over the last 3 s; a gain is negative), `wouldBreak=` (the bot's health when a
  break-off projected from that rate would have fired, under 40% or under 6 s of the loss; logged only, -1 if never) and
  `walk=` (the walk on the think before the fight began: `none`, `hunt`, `anchor`, `roam`, `clear`, `rest`, `kite`,
  `breakoff` or `walkout`, the walk out toward the camp planned under the release's immunity). Out of a fight `DEATH`
  gives -1 for the fight's fields, `rate=` at the death and the walk on its last think alive; it gives `end=`
  (endurance). Both then give `kiteNone=range:clear:mesh:fit`, the fight's steps back that found no point, by why:
  `range` (the bot already stood at its spell's reach), `clear` (no point out of the other monsters' reach), `mesh`
  (none on the navmesh) or `fit` (none within the stop once on the navmesh's height); -1 each out of a fight.
  Then `dots=`, its damage-over-time casts, `qcKill=`, 1 when a cast went under Quickcast as the killing nuke, and
  `ctlMez=`, its mezzes at the target (-1 each out of a fight), then `raceLeft=`, the race's projection of the target's
  health at the bot's death (the one-monster break-off's, with 20% lost since the first hit), at the first think under
  40% with a read (-999: none, or out of a fight). `FIGHT` then gives `endStart=` and `end=`, its endurance at the
  fight's first think and at its end, and both give `styleHeld=`, the style choices a close fight's endurance reserve
  left unstyled though it could pay for a style, and `castHeld=`, the thinks the reserve held a damage spell back (-1
  each out of a fight). `FIGHT` then gives `sight=`, the sight checks its target hitting it in contact answered (the
  navmesh's line said no), and `blind=` (1: dropped after 5 s out of sight in contact, fighting neither the bot nor its
  pet, nor coming at it), and ends with `blindSteps=`, the steps it took toward its target out of sight.
  `BLOCKED`'s `walk=` is `target`, `roam`, `clear` (the step out after a release), `walkout`, `rest`, `kite`, `turret`
  or `breakoff`. `RELOCATE` after a death (`reason=died`) ends with `sinceRelease=`, the milliseconds since the release;
  after a break-off its `reason=` is `brokeoff`. Either ends with `barred=old` when the death or the break-off came on
  the walk to the camp it had, which the plan then leaves out. Any `RELOCATE` then ends with `capKill=`, the lowest con
  that killed the bot in the last 5 minutes, yellow or harder (a blue or green killer logs none), while there is one.
  Then `recent=1` on a plan made within 15 minutes of a death (a died, break-off or capped one), and `near=1` when the
  camp came from its near pass (within 10,000 units of where the bot stood). A break-off drops its fight (`FIGHT
  result=dropped`);
- `BREAKOFF`: one per break-off, when it ends: the bot's `hp=`, `end=` (endurance) and `attackers=` when it broke off,
  `sec=`, the seconds it ran (a run on to its point, done in another monster's reach, counts in it), and `outcome=`:
  `shook` (none still after it, or from 30 s none within 1,500 units), `fought` (still chased after 45 s, caught with no
  control to cast, or nowhere to run) or `died`; then `rate=`, the health it lost a second over the 3 s before it broke
  off, and `why=`, the rule that fired: `adds` (2 or more on it), `atkill` (1 still on it within 3 s of a kill with
  adds), `followup` (the fight after that kill), `avoided` (one it avoids), `caster` (a caster losing to one), `race` (a
  bot that isn't a caster losing the race to one) or `spent` (a caster out of power with its target held); then
  `sprintOff=`, the seconds into the run its sprint first went off (-1: never), and `endLeft=`, its endurance when the
  run ended; then `turn=`, why a fought one turned: `caught`, `nopoint` (nowhere to run, with a chaser not held within
  250 units, a hit while it held, or as a caster or pet class with power), `blocked` (the same after a blocked step) or
  `cap` (45 s), `-` otherwise; `holds=`, its one-second tries that found no point while it held; `speed=` and
  `chaserSpeed=`, its run speed (191 unencumbered, less when encumbered) and the fastest chaser's when it began (-1
  before they are read). A break-off that would fire but doesn't logs once a fight as `outcome=refused` with `sec=0` and
  the bot's `hp=`, `end=`, `attackers=` and `rate=` then, and `why=`: `control` (an add's control comes first), `speed`
  (a chaser as fast as its sprint), `sprint` (its sprint too short for the fastest), `ranged` (a chaser that harms from
  range) or `point` (nowhere to run), with `sprintOff=-1`, `endLeft=` its endurance then and its `speed=` and
  `chaserSpeed=` (-1 for `control`); it is no break-off, so leave it out of break-off counts. The fight it dropped
  (`FIGHT result=dropped breakOff=1`) counts the monsters on the bot then in its `adds=` (never fewer than
  `attackers=`), and with 2 or more and no add seen before, its `addAt=` and `addHp=` are the break-off's;
- `TURRET`: an Animist's new turret by its target (`step=replant`) or a monster it left because it couldn't plant
  one (`step=skip`), with the target's `con=`, its distance from the turret it had (`toTurret=`) and that turret's
  reach (`reach=`);
- `RELEASE`: each release the bot asks for, with `try=` (its count in this death: 2 or more means one didn't take) and
  `sinceDeath=` (the milliseconds since the death);
- `BACK`: at the first fight after a release, `sec=`, the seconds since the release, and of those `stepOut=` (the step
  out of reach), `hold=` (the back-off, the illness or the hold for the replan) and `walk=` (roaming, the walk back
  among it, and the walk out under the immunity); then `replan=`: `moved` (a camp planned since), `same` (that camp is
  the anchor it had, within 200 units), `none` (owed, none planned) or `notOwed`; then `immune=`, the seconds it walked
  while the release's immunity lasted (the step out and the walk out);
- `REST`: one per rest, when the bot stands: `sec=`, the seconds it sat, `why=`: `recovered`, `hit`, `incoming` (a
  monster coming at it), `heal`, `pet` or `buff` (a cast on itself before it had recovered; after, `recovered`),
  `died`, or `other`; then `hp=` and `mp=`, its health and power when it sat (`mp=100` with no power pool),
  `respawn=`, the spawn spots of its recent kills its rest spot was chosen against, `blocked=`, its walks to a rest spot
  that were blocked before it sat (3 at most), and `inReach=` (1: it sat within an aggressive monster's reach, or a
  recent kill's spawn spot's, as it read them before the walk), then `end=`, its endurance when it sat;
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
