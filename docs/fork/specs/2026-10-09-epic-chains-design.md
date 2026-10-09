# Sub-project 4: the epic chains, fixed on upstream's quests (design)

Status: the two design parts were approved by the owner in conversation on 2026-10-09; this written spec awaits the
owner's review. Date: 2026-10-09. Fork: `lometur/HearthDAoC`. Issue: #73. Branch: `sub4-shadows-epic`. Release:
merging the PR publishes the next release; the server picks it up with `./hdc update`, and the world fix runs at the
next start.

This spec replaces `docs/fork/specs/2026-10-08-shadows-epic-design.md` (commit a597447), which designed a chain engine
of our own. Upstream 0.35 now ships the Guild of Shadows chain 7–48 as data quests, so this design builds on upstream's
quests and fixes their gaps instead. The item research of the old spec is reused (section 4).

References are to HearthDAoC `main` after the 0.35b sync (`source/server/GameServer/` unless a path says otherwise),
to upstream's `classic-quests.json` of v0.35b, and to the clean classic world `clean-classic-0.35.db`.

## 1. Goal

Make the Guild of Shadows epic chain (Infiltrator, Mercenary, Cabalist, Necromancer, Reaver) playable from 7 to 50 in
order, with every reward, on upstream's quests; give it the real level-50 "Lord of Deceit"; and make every guild line's
steps go in order.

### What is wrong with upstream's chain (0.35b)

- **Order.** A data quest's `QuestDependency` names the quests it needs, and any finished quest of that name counts
  (`DataQuest.CheckQuestQualification`). Upstream reuses names: the Guild of Shadows 30 is also called "Regal Nobility",
  like 25, so 40 needs only 25 and 30 can be skipped; 43, 45 and 48 are all "Lord of Deceit" and 45 and 48 both need
  "Lord of Deceit", so after 43 they go in either order. The same happens in every guild line (section 2.5).
- **Unlinked steps.** 11 doesn't need 7. 15 needs the classic 11 ("Entry Into Tomorrow"), so the Shrouded Isles 11
  ("Shades and Shadows") leads nowhere.
- **Two Supply Runs.** Infiltrator, Mercenary, Cabalist and Reaver are offered two versions of the level-7 Supply Run.
- **Missing items.** 30 of the class rewards and 11 of the level-40 weapons have no `ItemTemplate` row, so those steps
  give nothing (section 4).
- **Level 50.** `scripts/quests/Albion/epic/Shadows50.cs` is still a copy of the Defenders quest ("Feast of the
  Decadent", Lidmann Halsey) with the class list changed; Lord Elidyn is archived.
- **XP** varies from 2% to 22% of a level between steps.

### Decisions (owner, 2026-10-07 to 2026-10-09)

| Topic | Decision |
|---|---|
| Approach | Keep upstream's quests, texts, NPCs and givers; fix the gaps with a little server code and a world fix. No chain engine of our own. |
| Scope | The whole Guild of Shadows line, 7→50. Every guild line gets the order fix; their missing rewards and level-50 quests stay for later sub-projects. Heretic (class 33, disabled) is left as upstream has it. |
| Order | Strict, forgiving: each step needs the one before and at least its level; no upper limit; a declined step is offered again. |
| Shrouded Isles steps | The Caer Gothwaite versions of 7 and 11 count as 7 and 11. Taking one version closes the other; either one unlocks the next step. |
| XP | A quarter of a level for 7 and 11 (both versions), a tenth of a level from 15 on, none at 50 (section 2.3). |
| Coin | What the sources give (7 SI: 7 silver; 11 SI: 6 silver twice; 43: none); every other step its level in silver. |
| Existing characters | Finished steps stay finished. A finished old `Shadows_50` counts as the finished 50 step (no second armour set). |
| Testing | CI tests, and an in-game test guide with GM commands, including a GM-only `/epic`. |
| Upstream | Nothing is sent upstream now. After the chain is implemented and tested in game, and if upstream hasn't fixed it meanwhile, the owner may offer it upstream; ask first. The upstream-able parts are separate commits (section 6). |

### Non-goals

- Our own chain engine, rewritten dialogue and the dialogue ledger of the old spec: upstream's texts stay, except the
  few fixes in section 3.4 and the new level-50 texts (section 2.4).
- Changing upstream's givers: one Camelot trainer per class (Master Edric, Master Arenis, Magus Isen, Yulia, Peze),
  Carys in Caer Gothwaite for both Shrouded Isles steps, Magus Agyfen for 43 and Captain Rhodri for 20, 25 and 30.
- Changing upstream's encounters, monsters, quest items, map markers, or the rewards upstream already has.
- The one-time level-40 weapon swap, and the froglords Lord Elidyn turns into heroes (old spec).
- Kiss of Death (no stats in any source) and the Blood Encrusted Flail (Heretic era).
- The other lines' missing rewards and level-50 quests.

## 2. What players see

### 2.1 The Guild of Shadows chain

Upstream's quest IDs, per class. "Closed" rows stay in the world but are offered to no one (section 3.4).

| Step | Name | Giver | Infiltrator | Mercenary | Cabalist | Necromancer | Reaver |
|---|---|---|---|---|---|---|---|
| 7 | Traveler's Way -- Supply Run | Camelot trainer | 21500 | 21498 | 21499 | 20497 | 21501 |
| 7 (closed) | the same, Lady Aelawen's version | | 21324 | 21322 | 21323 | — | 21325 |
| 7 SI | Strange Beings | Carys | 20478 | 20476 | 20477 | 20480 | 20479 |
| 11 | Entry Into Tomorrow | Camelot trainer | 20157 | 20155 | 20156 | 20159 | 20158 |
| 11 SI | Shades and Shadows | Carys | 20469 | 20467 | 20468 | 20471 | 20470 |
| 15 | Rebellion Accepted | Camelot trainer | 20188 | 20186 | 20187 | 20190 | 20189 |
| 20 | Path of the Renegade | Captain Rhodri | 20455 | 20453 | 20454 | 20457 | 20456 |
| 25 | Regal Nobility | Captain Rhodri | 21306 | 21304 | 21305 | 21308 | 21307 |
| 30 | Regal Nobility | Captain Rhodri | 21491 | 21489 | 21490 | 21493 | 21492 |
| 40 | Hidden Insurrection | Camelot trainer | 20171 | 20169 | 20170 | 20173 | 20172 |
| 43 | Lord of Deceit | Magus Agyfen | 20435 | 20433 | 20434 | 20437 | 20436 |
| 45 | Lord of Deceit | Camelot trainer | 21297 | 21295 | 21296 | 21299 | 21298 |
| 48 | Lord of Deceit | Camelot trainer | 21482 | 21480 | 21481 | 21484 | 21483 |
| 50 | Lord of Deceit (new) | Captain Rhodri | 990509 | 990511 | 990513 | 990512 | 990519 |

Each step needs the class's previous step in this order: 7 or 7 SI → 11 or 11 SI → 15 → 20 → 25 → 30 → 40 → 43 → 45
→ 48 → 50. A finished closed Supply Run counts as 7. One version of 7 (or of 11) closes the other while it is active or
finished. The new 50 IDs are fixed: `99` `05` and the class ID, outside upstream's range (20000–21600).

### 2.2 Rewards

- Every step except 40, 48 and 50 gives the class item upstream names; the 30 missing items are added (section 4.2).
- **Necromancer 11:** upstream gives both Flayed Skin Necklace and Arawn's Beads on both versions. The classic 11 gives
  Flayed Skin Necklace only, the SI 11 Arawn's Beads only (the old spec's ruling).
- **40:** Rhodri lists the class's weapons and the player whispers one (upstream's `choose` step). The missing weapons
  are added (section 4.3). Kiss of Death and Blood Encrusted Flail come off the Reaver's list (section 3.4).
- **50:** the class's six armour pieces (`<Class>Epic{Helm,Vest,Arms,Gloves,Legs,Boots}`, rows that already exist),
  with the fixes in section 4.4. Rhodri waits until six backpack slots are free (DataQuest's own rule).

### 2.3 XP and coin

XP is a share of the XP needed to go from the step's level to the next, `XPForLevel[L] − XPForLevel[L−1]`. DataQuest pays
`RewardXP` with `ForceGainExperience`, a fixed amount like every upstream quest (the server's `xp_rate` doesn't scale
it). The whole amount is paid on the step's last stage; earlier stages pay none. Coin is in `RewardMoney` (100 copper
to the silver), also on the last stage unless the table says otherwise.

| Step | XP | Coin |
|---|---|---|
| 7, 7 SI | 5,500 (¼ of 22,000) | 7 silver |
| 11 | 95,000 (¼ of 380,000) | 11 silver |
| 11 SI | 95,000 | 6 silver when Adam Glaze takes Martley's body (stage 3), 6 silver at the end |
| 15 | 180,000 | 15 silver |
| 20 | 1,230,000 | 20 silver |
| 25 | 5,300,000 | 25 silver |
| 30 | 21,000,000 | 30 silver |
| 40 | 430,000,000 | 40 silver |
| 43 | 1,080,000,000 | none |
| 45 | 1,560,000,000 | 45 silver |
| 48 | 2,700,000,000 | 48 silver |
| 50 | none | 50 silver |

The closed Supply Runs keep upstream's values.

### 2.4 Level 50: "Lord of Deceit"

Five new data quests, one per class, made like upstream's 48 rows (`ClassType` `DOL.GS.Quests.ClassicQuestStep`,
start type 0, `MaxCount` 1, levels 50–50, given by Captain Rhodri, region 1).

| Stage | Type | Target | Journal |
|---|---|---|---|
| 1 | Kill (0) | `Lord Elidyn;1` | Slay Lord Elidyn, the Lord of Deceit, in the Ellyll ruins on the hill in the Pennine Mountains. |
| 2 | InteractFinish (5) | `Captain Rhodri;1` | Return to Captain Rhodri at the Snowdonia border keep. |

- Offer (`Description`, keyword `AcceptText` "deceit"): "<Player>, Sir Tilian's notes on the seer's journal leave no
  doubt. The hand behind the Ellyll and the Arawnites belongs to Lord Elidyn, the Lord of Deceit, who holds the old
  Ellyll ruins on the hill in the Pennine Mountains. Put an end to his [deceit]."
- Finish (`FinishText`): "Lord Elidyn is dead, and his lies with him. Sir Bors will hear of this before
  nightfall. Take this armour, <Class>; the Guild of Shadows makes it only for its finest."
- Rewards: the six armour pieces, 50 silver, no XP. Dependency: the class's 48 step (section 3.1).
- These three texts are new (written in the style of upstream's lines). Nothing else in the chain is reworded.
- **Lord Elidyn's camp** comes back from upstream's archive `offline_classic165_removed_mobs`: every archived row in
  region 1 within 3,000 units of his spot (568158, 404718) named Lord Elidyn, Ellyll guard, ellyll guard, ellyll
  champion or ellyl hero (18 rows on the clean world: Lord Elidyn, 14 guards, 1 champion, 2 heroes), with their
  Mob_IDs. Lord Elidyn (template 60163397, level 59) is a group fight, as on live. He respawns like any named
  monster. The map shows markers for both stages, and bots leave him alone (section 3.2).

### 2.5 Every guild line: steps in order

The world fix pins name dependencies that upstream's naming made ambiguous. For every classic data quest (`ClassType`
`DOL.GS.Quests.ClassicQuestStep`) outside the Guild of Shadows table, each name in `QuestDependency` is replaced by the
IDs of the classic quests of that name that the step's classes can take, at the highest level below the step's own,
when the name is the step's own name or names such quests at more than one level. A name with no such quest, or with
quests at one level only, stays as it is. On the clean 0.35 world this changes 87 rows:

| Line | Steps pinned (rows) |
|---|---|
| Albion, Defenders: Legend of the Lake, Hands of Fate, Feast of the Decadent | 20 and 25 (8), 30 (4), 45 and 48 (5) |
| Albion, Church: Passage to Eternity | 45 and 48 (4) |
| Albion, Academy: Symbol of the Broken | 45 and 48 (6) |
| Albion, Heretic: Regal Nobility | 30 (1) |
| Midgard: The Red Dagger, An End to the Daggers, Thane's Blood | 20 and 25 (11), 43 (5), 45 and 48 (6), 30 (6) |
| Midgard: Saving the Clan, The Desire of a God | 45 and 48 (4), 45 and 48 (3) |
| Midgard: A War of Old, The War Continues | 20, 25 and 30 (6), 40 (1) |
| Hibernia: Last Heir, The Moonstone Twin, Unnatural Powers | 45 and 47 (10), 45 and 47 (5), 47 (2) |

For example, Defenders 30 (Hands of Fate) now needs Legend of the Lake 25 instead of any Legend of the Lake, and 48
needs 45. The test pins the 87 and the per-line counts; a later upstream world that changes them fails the test, and
the counts are updated after a look.

### 2.6 Existing characters

- Finished steps stay finished; the order rules apply only to offers. A character who did 45 and 48 out of order, or
  skipped 30, keeps what they did.
- A finished old `Shadows_50` becomes a finished 50 step of the character's class (section 3.4, step 9), so Rhodri
  never offers 50 again and there is no second armour set. Armour already owned picks up the fixes of section 4.4.
- An unfinished `Shadows_50` is removed; its journal entry is gone at the next login.
- A character in the middle of a closed Supply Run can still finish it.

## 3. How it works

### 3.1 Dependencies by quest ID (`quests/QuestsMgr/DataQuest.cs`, upstream file)

`QuestDependency` stays a `|`-separated list; every entry must be met. New entry forms, beside the plain name:

| Entry | Met when |
|---|---|
| `Name` | a finished data quest has that name (unchanged) |
| `#20188` | the data quest with that ID is finished |
| `#20157/20469` | at least one of these IDs is finished |
| `!#20478` or `!#20478/21324` | none of these IDs is active or finished |

- The rules live in one pure static method (entries plus the player's finished and active quest IDs → met or not), so
  they are unit tested without a server.
- An entry that doesn't parse (`#`, `#x`, `!#`) is never met, and the quest logs one error at load naming the entry.
  The world fix writes only valid entries, and the tests check them.
- Quests without these forms behave exactly as before.

### 3.2 HearthDAoC's own quest data (`quests/QuestsMgr/ClassicQuests.cs`, upstream file)

`ClassicQuests` also reads `hearthdaoc-quests.json` from the same folder as `classic-quests.json`, in the same format,
when it loads or reloads. Its `Quests` entries are added for IDs that upstream's file doesn't have (upstream's entry wins
on the same ID), and its `QuestMonsterIds` join upstream's. A missing or unreadable file logs one warning and changes
nothing. The file ships in the image (the Dockerfile copies it next to the server), not with upstream's downloads.

Contents: the five 50 rows' map markers (stage 1 at Lord Elidyn's spot, region 1, 568158, 404718, Z 5032; stage 2 at
Rhodri, 528239, 359818, Z 9088), and Lord Elidyn's Mob_ID `1f005bc1-27ae-40b0-bc42-1ec407a4aa34` as a quest monster,
so gamebots don't hunt him (`AutonomousAuditedCampPolicy.IsBotExcludedNpc`).

### 3.3 Other server code

- `scripts/quests/Albion/epic/Shadows50.cs` is deleted. `Defenders_50` keeps Lidmann Halsey and is unchanged.
- `scripts/quests/Albion/epic/Academy50.cs`, `scripts/quests/Hibernia/epic/Essence50.cs`,
  `scripts/quests/Midgard/epic/Mystic50.cs` and `scripts/quests/Midgard/epic/Viking50.cs` each look their quest NPC up
  (Master Ferowl, Brigit, Danica, Elizabeth) at a different X/Y from where they create it, so a copy saved by a GM
  shows up as a second NPC at every start. One lookup line each changes to the creation X/Y. CRLF line endings are kept.
- **`/epic` (GM only, fork code in `scripts/hearthdaoc/`).** It works on the GM's target if that is a player, otherwise
  on the GM. The chain of a character is the classic data quests their class can take that are linked by `#` entries
  (section 3.1) to its highest linked step, in level order; for other lines, name entries are followed too.

  | Command | Does |
  |---|---|
  | `/epic` | Lists the chain: each step's level, name, ID and state (finished, active at stage n, can take, closed, or what it waits for). |
  | `/epic done <level>` | Marks every chain step below `<level>` finished (the classic 7 and 11 unless an SI version is already finished; never a closed step), removes active ones among them, and saves. The step at `<level>` can then be taken. It doesn't change the level; use `/player level`. |
  | `/epic goto` | Teleports to the current stage's map marker (upstream's or section 3.2's); without an active step, to the next step's giver. |
  | `/epic reset` | Removes every chain step (active and finished) from the character, and says so when it removes a finished 50. |

  It changes the character's loaded quest lists and the database together, so no relog is needed.
- `.github/workflows/server-image.yml`: the C# test filter gains the new test classes.

### 3.4 World data: `deploy/bin/epic_chains.py`

Run from `world_fixes.py` at every start, after `battlegrounds.py`, like it: once per world (the marker
`epic-chains-v1` in `fork_world_fixes`), under its own savepoint, and each change made only while the row still holds
the value it expects (upstream's 0.35b value, or "missing" for an insert). If a step fails, only this fix rolls back,
the log says `Epic chains: not applied (<reason>)`, and the next start tries again. A world with the marker is left
alone, so the owner's later edits stay. A new world (and a world upgrade's fresh world) gets the fix again at its first
start; the fixed 50 IDs keep characters' progress valid across upgrades.

The Guild of Shadows changes are data, in `deploy/bin/epic_chains_data.json`: the dependencies of section 2.1, the XP
and coin of section 2.3, the reward and text fixes, the five 50 rows and the item rows of section 4. The same file is
what an upstream PR would turn into SQL.

| Step | Change | Rows (clean world) |
|---|---|---|
| 1 | Guild of Shadows dependencies, five classes (section 2.1): 7: `!#<7 SI>`; 7 SI: `!#<7>/<closed 7>`; 11: `#<7>/<closed 7>/<7 SI>` and `!#<11 SI>`; 11 SI: `#<7>/<closed 7>/<7 SI>` and `!#<11>`; 15: `#<11>/<11 SI>`; 20 to 50: `#<previous step>`. | 60 |
| 2 | Every other line (section 2.5). | 87 |
| 3 | The closed Supply Runs: `MaxLevel` 0, so their level check fails for everyone. | 4 |
| 4 | XP and coin (section 2.3). | 60 |
| 5 | Rewards: Necromancer 11 (classic: Flayed Skin Necklace; SI: Arawn's Beads). | 2 |
| 6 | Texts: Reaver 40 (20172) without Kiss of Death and Blood Encrusted Flail in its step text and Rhodri's list; the 30 step's finish speech (21489–21494) fills upstream's leftover source tags: "(reward)" → "reward", "(prof name)" → "the Guild of Shadows", "(class name)" → "<Class>". | 7 |
| 7 | Items: the 30 rewards and 11 weapons of section 4, inserted where the `Id_nb` is missing; the armour fixes of section 4.4 where the row still has the old value. | 41 + 5 |
| 8 | The five 50 rows (section 2.4) and Lord Elidyn's camp (18 rows copied from the archive into `Mob`, left in the archive). A camp row already back in `Mob` is skipped. | 5 + 18 |
| 9 | Old `Shadows_50` (`Quest` rows named `DOL.GS.Quests.Albion.Shadows_50`): a finished one (Step -2) becomes a `CharacterXDataQuest` row for the character's class's 50 ID (Step 0, Count 1) unless one exists, and is deleted; an unfinished one is deleted. Epic vests in inventories with 0 charges get the template's charges. | 0 on the clean world |

The start log gets one line, for example: `Epic chains: Guild of Shadows 60 links, 87 other links, 41 items added, 5
armour fixes, 5 level-50 quests, Lord Elidyn's camp 18 restored, Shadows_50: 0 finished carried, 0 unfinished removed`.

Notes:
- Restored camp rows aren't recorded in `fork_restored_mobs`, so `hdc spawns undo` leaves them.
- Upstream fixing something itself is handled by the expected values: a dependency upstream changed, or an item it
  added, is left as upstream has it, and the log line counts only what this fix changed.

## 4. Items

### 4.1 Rules for item values

- **Era:** the world's 1.65 era: values after the 1.63 item upgrade, Power as flat points.
- **Source:** the Allakhazam item page for every field. bdo was partly compiled from Warcry, so the two count as one
  source when they agree. The rulings below settle known conflicts.
- **Unknowns:** filled by analogy with sibling items; each one is listed in the plan.
- **Rows:** like upstream's existing rewards (`cq_alb_ring_of_shadowy_embers`): upstream's `Id_nb`, Realm 1,
  `AllowedClasses` 0, droppable, not tradable, price 0, quality 100, condition and durability 50000, `Description`
  "Classic quest reward (<quest name>)", and `PackageID` "HearthDAoC" so ours can be told apart. Level: the page's
  level, or the step's. Model and colour: from the page, otherwise an existing row of the same kind and look. Procs
  and charges: existing `Spell` rows with the same effect, type and value; no new spells. The plan lists every model
  and spell ID.

### 4.2 The 30 missing class rewards

| Step | Infiltrator | Mercenary | Cabalist | Necromancer | Reaver |
|---|---|---|---|---|---|
| 7 | | Sleeves of Might | Silvered Cap of the Intuit | | Twisted Bone Bracelet |
| 7 SI | Jewel of Grace | | Necklace of Greatness | | Temple Ring |
| 11 | | Might | | | |
| 11 SI | Quickened Absorption Stone | Lucky Pebble | Minding Ring | | Reaver's Stone |
| 15 | | | | | Sleeves of Despair |
| 20 | Soft Doeskin Boots | Heavy Pull Short Bow | | Staff of Tainted Rage | |
| 25 | Vest of the Infamous Blade | Light Chain Tunic | Spirit Threaded Cloak | Pain Threaded Cloak | Tunic of Dark Suffering |
| 30 | Shadowbinder's Mantle (id `cq_alb_snowbinder_s_mantle`) | Gauntlets of Blinding Speed | Visage of Death | Charred Staff of Blight | Mantle of Shadow |
| 43 | Ring of Shades | | | Ring of Forbidden Rites | Jewelled Skull Ring |
| 45 | Ignuixs' Portable Shadow | | Warm Construct Cloak | | |

| Item | Slot | Bonuses |
|---|---|---|
| Sleeves of Might | studded arms, AF 18 | Str 4, Hits 6 |
| Silvered Cap of the Intuit | cloth helm, AF 9 | Body +1, Dex 3, Int 6, Power 1 |
| Twisted Bone Bracelet | wrist | Str 6, Con 3, Flexible +1, Soulrending +1 |
| Jewel of Grace | jewel | Str 3, Dex 3, Qui 1 |
| Necklace of Greatness | neck | Int 6, Power 1, Hits 8 |
| Temple Ring | ring | Str 3, Con 4, Piety 3, Flexible +1 |
| Might | wrist | Str 7, Dex 4, Body 1%, Hits 12 |
| Quickened Absorption Stone | jewel | Str 4, Dex 4, Qui 7, Envenom +1 |
| Lucky Pebble | jewel | Str 6, Dex 6, Qui 6, Spirit 1% |
| Minding Ring | ring | Dex 4, Int 7, Spirit 1%, Hits 12 |
| Reaver's Stone | jewel | Shield +1, Con 6, Hits 16, Power 3 |
| Sleeves of Despair | chain arms, AF 34 | Str 7, Qui 7, Piety 7, Flexible +1 |
| Soft Doeskin Boots | leather feet, AF 44 | Stealth +3, Dex 6, Qui 3 |
| Heavy Pull Short Bow | short bow, DPS 7.5, 4.1 s | Dex 13, Qui 12 |
| Staff of Tainted Rage | 2H staff, DPS 7.8, 4.4 s | Deathsight focus 18, Painworking focus 18, Death Servant focus 22, Power 8 |
| Vest of the Infamous Blade | leather torso, AF 54 | Dual Wield +4, Str 4, Qui 4 |
| Light Chain Tunic | chain torso, AF 54 | Parry +3, Qui 9, Slash 2%, Hits 12 |
| Spirit Threaded Cloak | cloak | Spirit +3, Dex 9, Power 4 |
| Pain Threaded Cloak | cloak | Dex 9, Painworking +3, Power 4 |
| Tunic of Dark Suffering | chain torso, AF 54 | Str 4, Piety 4, Flexible +4 |
| Shadowbinder's Mantle | cloak | Stealth +3, Str 13, Dex 13 |
| Gauntlets of Blinding Speed | chain hands, AF 64 | Dual Wield +4, Str 6, Qui 6 |
| Visage of Death | 2H staff, DPS 10.5, 4.0 s | Int 28, Matter focus 26, Body focus 26, Spirit focus 30 |
| Charred Staff of Blight | 2H staff, DPS 10.8, 4.0 s | Int 28, Deathsight focus 26, Painworking focus 26, Death Servant focus 30 |
| Mantle of Shadow | hooded cloak | Parry +3, Con 9, Piety 9, Thrust 4% |
| Ring of Shades | ring | Critical Strike +3, Str 18, Dex 15, Envenom +3 |
| Ring of Forbidden Rites | ring | Int 18, Deathsight +3, Painworking +3, Death Servant +3 |
| Jewelled Skull Ring | ring | Str 18, Con 15, Flexible +3, Soulrending +3 |
| Ignuixs' Portable Shadow | cloak | Stealth +3, Con 18, Qui 18, Cold 6% |
| Warm Construct Cloak | cloak | Dex 7, Int 7, Power 6 |

Rulings (from the old spec's research):

| Item | Conflict | Ruling |
|---|---|---|
| Jewel of Grace | bdo gives a different item (a ring) | Allakhazam's Jewel of Grace. |
| Sleeves of Might | bdo gives higher values | Allakhazam. |
| Silvered Cap, Necklace of Greatness | bdo and one 2002/2003 report give pre-1.63 values | Allakhazam. |
| Soft Doeskin Boots | bdo and Warcry give Dex 4 | Allakhazam's Dex 6 (bdo copies Warcry; the page has the post-1.63 values). |
| Heavy Pull Short Bow | bdo DPS 7.4, Dex 9, Qui 6 | Allakhazam, which Warcry supports. |
| Light Chain Tunic | bdo lacks Parry | Allakhazam (Parry +3), which Warcry supports. |
| Visage of Death, Charred Staff of Blight | bdo 26/26/26 or another focus split | Allakhazam. |
| Temple Ring | bdo's copy is garbled | Allakhazam. |
| Shadowbinder's Mantle | upstream's id says "snowbinder" | The name from the item page; the plan re-checks it and keeps upstream's id. |
| Ignuixs' Portable Shadow | spelling | As on its item page. |

Upstream's 20 existing rewards stay as upstream made them.

### 4.3 The 11 missing level-40 weapons

Upstream's choice lists stay, minus the Reaver's two (section 3.4, step 6). Missing, by class: Mercenary: Spark,
Arcing Bludgeoner; Infiltrator: Spark of Midnight (Mercenary's list has Death's Touch instead, as upstream has it);
Cabalist: Staff of Eternal Lifeforce, Staff of Earth Channeling, Staff of Spirit Consumption; Necromancer: Staff of
Cursed Bondage, Staff of Clouded Vision, Staff of Ceaseless Agony; Reaver: Sap of Lost Will, Bloodletter.

Full fields (speed, DPS 14.0–14.1, damage type, the 77-damage proc or 10 charges, level) come from each item page.
Cabalist staves: Body, Matter or Spirit focus 43 with the other two at 33, Hits 100; Necromancer staves: Death Servant,
Deathsight or Painworking focus 43 with the other two at 33, Hits 100. Rulings:
- **Staff of Earth Channeling:** Hits 100 by analogy with its siblings (the page shows 33, bdo 100); 10 charges of an
  energy damage spell (Warcry's damage type; the charges by analogy with Eternal Lifeforce).
- **Staff of Spirit Consumption:** no Allakhazam focus line; bdo's Spirit 43, Body 33, Matter 33, Hits 100; 10 charges
  of a spirit damage spell (bdo's type; charges by analogy).

### 4.4 Level-50 armour fixes

| Row | Today | Fixed to | Why |
|---|---|---|---|
| MercenaryEpicVest | "Haurberk of the Shadowy Embers", `AllowedClasses` 0 | "Hauberk of the Shadowy Embers", 11 | typo; the only vest open to every class |
| the five vests | no charges | 3 charges of a self Shield (AF) 75 buff, 10 minutes | bdo, and Allakhazam for three of them; the same for the other two by analogy |
| MercenaryEpicArms | Con 15, Dex 16 | Con 16, Dex 15 | Allakhazam and bdo agree |
| InfiltratorEpicGloves | Envenom +3 | Envenom +4 | Allakhazam and bdo agree |
| CabalistEpicBoots | Matter +3 | Matter +4 | Allakhazam and bdo agree |

## 5. Testing

### 5.1 C# unit tests (CI)

Added to the CI filter.

- **`UT_DataQuestDependency`:** the entry forms of section 3.1 against finished and active ID sets: a plain name, one
  ID, any-of, closed-by, several entries together, malformed entries never met, and quests without the new forms
  unchanged.
- **`UT_ClassicQuestsExtra`:** merging an extra file: new IDs added, upstream's entry kept on the same ID, quest
  monster IDs joined, and a missing or broken file changing nothing.
- Source checks: `Shadows50.cs` stays deleted; each of the four lookup lines matches its creation line.

### 5.2 Python tests (CI, against the clean 0.35 world)

- **Walk the chain:** a small model of section 3.1 over the fixed world, for each of the five classes: with nothing
  finished only 7 and 7 SI can be taken; after each step exactly the next one opens (30 can't be skipped; 48 needs 45);
  the SI route reaches 15; one version of 7 or 11 closes the other; the closed Supply Runs open for no one and still
  count as 7; 50 opens after 48.
- **Every line:** the 87 pinned rows and the per-line counts of section 2.5; every 45 needs its 43 and every 48 (47)
  its 45; no other row's dependency changed.
- **Items:** every Guild of Shadows reward and every weapon choice resolves to an `ItemTemplate`; the 41 rows inserted
  have the fields of section 4.1 and the bonuses of sections 4.2 and 4.3; the armour fixes.
- **Level 50:** the five rows; Lord Elidyn's camp (18 rows, Mob_IDs equal to the archive's); `hearthdaoc-quests.json`
  names the five IDs and Lord Elidyn's Mob_ID.
- **Old `Shadows_50`:** synthetic rows (finished carried to the right class's 50 ID, an existing 50 row kept, unfinished
  removed); vest charges on a synthetic inventory row.
- **Safety:** a second run changes nothing; an injected failure in each step rolls back only this fix; a row the owner
  changed is left alone; every dependency the fix writes parses.

### 5.3 Container smoke and integration tests

After start: the `Epic chains:` log line with the clean-world counts, no DataQuest load errors for the changed rows,
and `/epic` registered as GM-only (`Command - '&epic' ... required plvl:2`, as the smoke test checks for `/tele`).

### 5.4 In game (owner, after merge)

The PR adds `docs/fork/verification/sub4-test-guide.md`: the GM commands used (each checked against this build:
`/player level`, `/jump`, `/epic`, `/item create`), then one class through the whole chain with `/epic done` and
`/epic goto`, the SI route at 7 and 11, the weapon choice at 40, Lord Elidyn and the armour with a full bag, and spot
checks for the other four classes' rewards with `/item create`. One step of another line (Defenders 48 before 45 is
refused). Results go in `docs/fork/verification/sub4-ingame.md`.

## 6. Upstream, later

Kept as separate, self-contained commits so they can be offered on their own: the dependency forms (3.1), the
`Shadows50.cs` removal with the level-50 rows, the four lookup fixes, and the data file of section 3.4. Nothing is sent
upstream now. After the owner's in-game run, check whether upstream has fixed the chains meanwhile, note candidates on
tracker #49, and ask the owner before opening anything.

## 7. Documentation

- `docs/fork/FORK.md`: the dependency forms, `hearthdaoc-quests.json`, `/epic`, the world fix, the upstream files
  touched or deleted.
- `docs/fork/CHANGELOG.md`: the release entry.
- `docs/fork/verification/sub4-test-guide.md` (section 5.4).

## 8. Risks

1. **Upstream's next world.** A new upstream version may renumber quests or change the rows the fix expects. The
   expected-value checks skip what changed, the tests fail on the clean world of the new version, and the sync updates
   the data file. The 50 IDs are ours and don't move.
2. **The generic order rule.** It reads every line's names; a line where upstream meant "any of these" would become
   stricter. The 87 rows are listed by the test, and the in-game check covers one other line.
3. **Closed by level.** The closed Supply Runs rely on DataQuest checking levels only when offering; the plan confirms
   that a character on one can finish it.
4. **Lord Elidyn's camp.** 18 restored monsters, up to level 60, on the old frontier where bots roam; bots skip Lord
   Elidyn but may fight his guards.
5. **New text.** Only the level-50 offer, journal and finish lines are new.
