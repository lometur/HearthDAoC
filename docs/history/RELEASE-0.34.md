# Offline DAoC 0.34 "Claude Takeover II"

0.34 continues from 0.33. It collects every fix and feature added since 0.33 in one complete
download: harder bounties, bots that parry, block and evade, smarter class play, steadier camps,
raids and RvR, Shrouded Isles reputation quests, and for the Sluaghbinder a level 50 armor set and
a new ghastly healer. Every single change is listed in the [changelog](../CHANGELOG.md).

It comes in two editions, from one download. They are the same game, and only the custom class
differs:
- **0.34b** includes the Hibernian **Sluaghbinder** class, for players and bots.
- **0.34** has the original Classic + SI class list only. The Hibernian Mauler slot is disabled,
  as in 0.32.

## Downloading and starting

- **One full download**, as in 0.33. The helper fetches about 5 GB in parts, checks every part and
  unpacks one ready-to-play folder.
- **Your own account, world and settings.** No account, characters, bots, houses or auction
  history are included. The server creates your account the first time you click ENTER REALM,
  and you create bots from the launcher buttons. GM is off and XP is 1×.
- **The launcher names your edition:** VERSION 0.34b or VERSION 0.34.
- **A fresh client settings profile** for 0.34, so it never picks up a 0.33 copy's settings.
- **Bundled .NET runtime.** Nothing needs installing except the Windows .NET Framework 3.5
  feature.
- **Bring your save.** The progress transfer tool moves your account, characters, items, money,
  houses and bots from 0.33 and 0.33b, and from every earlier version. Unfinished 0.33 bounties
  are converted when you log in. See [TRANSFER-PROGRESS.md](TRANSFER-PROGRESS.md).

## Bounty Masters: normal, hard and very hard

- Three kinds of leveling bounty: **normal** (monsters at your level), **hard** (about 6 levels
  above you) and **very hard** (about 12 above).
- Every difficulty needs 5, 10, 15 or 20 kills depending on your level range, instead of climbing
  to 50.
- Harder bounties pay more: 2, 4 or 8 bulbs of XP, and class items 1, 3 or 5 levels above you.
- Targets always have at least two spawns. Where a level has few monsters, the Bounty Master
  looks a level or two lower, never below +4 for hard or +10 for very hard, and says so.

## Bots fight like players

- **Parry, block and evade.** Gamebots and companion bots now use the player rules: Parry needs
  the specialization and a melee weapon, Evade needs the class ability, and Block needs a real
  shield. Tanks and evade classes are much harder to kill.
- **Class play:**
  - Shamans fight as hybrids, casting their nukes on recast and fighting in melee in between.
  - Animists plant their shrooms by role (permanent shroom, resist vents, then damage shrooms
    as needed) and stand inside shroom range.
  - Tank bots use instant taunts, which they used to ignore.
  - Bots and pets use every damage-over-time spell that stacks.
  - Melee bots no longer rubberband while chasing their target.
- **Companions:**
  - Pets keep one target in multi-enemy fights and finish their casts.
  - Pet heals go to the most injured ally.
  - Ranged companions walk up to distant targets.
  - Bots skip gear built on another realm's model, so it always fits.

## Gamebots: camps, travel, raids and RvR

- **Camps:**
  - Bots leave a camp whose pulls never reach their target.
  - They pick a new camp when theirs turns grey.
  - They choose camps by how many monsters are there.
  - They avoid camps (and routes) where solo bots keep dying.
- **Places bots no longer go:** a few hungry shriller and Cliffs of Moher spawns next to far
  higher monsters, the level 0 wiggle worms in Bog of Cullen, high flyers out of reach, and
  Summoner's Hall. Cliffs of Moher gets six safe spawns in their place.
- **Shrouded Isles towns** are always open to bots, so they can use the wyvern, dragonfly and
  gryphon routes.
- **Raids:**
  - Epic dungeon raids rejoin stranded members and set aside targets nobody can damage.
  - Dragons land properly, and the whole raid joins a ground fight.
  - An automatic level 50 raid goes in at 180 of 200 bots after 75 minutes.
- **RvR and sieges:**
  - RvR groups pick the leader closest to the border keep.
  - Siege bots walk keep routes in checked legs, so they no longer freeze.
- **Stability:**
  - A pet summoned at the edge of the map appears on its owner instead of crashing the summon.
  - Bot saves are spread evenly, so no bot goes hours unsaved.

## Shrouded Isles reputation

- A faction emissary in each realm offers a repeatable hunt for +10 reputation: Kzzirrak (Krrzck,
  Necht), Ysslith (Cryptos Mythicos, Caer Diogel) and Hrodvar Deepvow (The Remnants, Hagall).
- A faction stable master that turns you away tells you your standing and which emissary to see.
- Bounties never send you after monsters whose death costs reputation with these factions.

## Sluaghbinder (0.34b)

- **The Dubh Sluagh set,** the level 50 epic reward from Muirenn: a black gothic Celtic set with
  grave-green accents.
  - Seven armor pieces, plus the Cairnbreaker mace, the Cairnfire Aegis shield (with a 3D bone
    skull) and the Reaper of the Host scythe.
  - Level 51, quality 100, and matched to the best item in each slot.
  - It cannot be traded or dyed, and Muirenn replaces any lost piece.
- **The ghastly healer** replaces the zombie priest: a floating ghost with its own model, voice,
  casting animation and dagger. Its stats, spells and healing AI are unchanged.
- **Dullahan's Bulwark:**
  - Its taunts cost power and share one recast.
  - Cairn Ward is replaced by the Barrow Deflection parry buffs.
- **Spells:**
  - Spells cost power only.
  - Life drains play a drain animation and the Bane drains share one recast.
  - Bane-specced bots use both of their damage-over-time lines.
- **Looks:** green Cairn strength buffs, a plague spore cloud for the Bane damage over time, and
  "unholy aura" buff text.

## Everyone

- **Bonedancer:** the commander's skull shield shows, and debuffers wield the two-handed bone
  mace.
- **Charm menu:** every creature is tagged Caster, Archer or Melee, and creatures that cannot move
  are no longer offered.
- **Helmets:** the Celtic scale helm, the Norse leather cap and the other helmets on those meshes
  are visible with extension 2.
- **World:**
  - The invisible tendrils, chokers, shacklers and the Throttler show a vine effect.
  - The Jordheim and Camelot Realm Exchanges have moved to open spots.
  - The Pilus'Fury fire trap no longer breaks.
  - Leptus is always level 6.
  - The Cliffs of Moher phaeghoul is out of its tree.

## Limits and honest notes

- **Darkness Falls is still beta.**
  - Raid AI isn't implemented.
  - Legion, the hardest level 70+ encounters, unreachable flying targets and unverified routes are
    left out of bot goals.
- **Automated tests and in-game checks are separate.** Every change passed the full automated
  test suite. Some were also checked in game by the project owner, including the bounty
  difficulties, the bots' parry, block and evade, and the Dubh Sluagh set. Many bot fixes came
  from the logs of live runs with thousands of bots, but long play can still turn up rough edges.
- **Startup data warnings.** The server logs a few warnings at startup from the stock world data
  (for example missing poison spells). They were there before 0.33 and don't affect play.
- **One server at a time.** Only one local server can run at once, because every copy uses port
  10300.

## For developers

- **The edition switch** is the server setting `classes / enable_sluaghbinder`: on for 0.34b and
  by default, off in the 0.34 database.
- **Source:** see [DEVELOPMENT.md](DEVELOPMENT.md) and [LLM-QUICKSTART.md](LLM-QUICKSTART.md).
- **Client art:** the private art pipeline is in `tools/pet-art`, and
  [CLIENT-MODDING-GUIDE.txt](CLIENT-MODDING-GUIDE.txt) explains how models, skins, armor sets and
  spell effects are added to the client.
- **The scripts behind every database and client change** are listed at the end of the 0.34 entry
  in the [changelog](../CHANGELOG.md).
- **Older code:** every earlier release keeps its own branch and tag, for example
  `release/v0.33-claude-takeover` and `v0.33b`.
