# Sub-project 5: classic battlegrounds 15–35 (design)

Status: draft for the owner's review; the decisions in section 1 are the owner's. Date: 2026-10-07.
Fork: `lometur/HearthDAoC`. Issue: #76. Branch: `sub5-battlegrounds`. Release: merging the PR publishes the
next release; the server picks it up with `./hdc update`.

File references are to HearthDAoC `main` at 0a4e730 (`source/server/GameServer/` unless a path says otherwise)
and to the clean classic world (`clean-classic-0.34.db`, the same battleground data as the pinned release).

## 1. Goal

Make the four classic battlegrounds work as they did in the Shrouded Isles era: Abermenai, Thidranki,
Murdaigean and Caledonia, each with its classic level range and realm-point cap, three realm portal keeps and
a capturable central keep, and none of Atlas's extras. Today only Thidranki and Caledonia can be reached, both
with Atlas's limits (Thidranki under 7,125 realm points, that is up to 1L9; Caledonia for levels 34–39 under
122,500, that is up to 3L4), and Abermenai and Murdaigean are empty and switched off.

### Decisions (owner, 2026-10-07)

| Topic | Decision |
|---|---|
| Scope | Abermenai (region 253), Thidranki (252), Murdaigean (251), Caledonia (250). Cathal Valley (165) and the New Frontiers Thidranki (238) stay as they are. |
| Limits | Abermenai 15–19, highest realm rank 1L2 (under 125 realm points). Thidranki 20–24, 1L3 (under 350). Murdaigean 25–29, 1L5 (under 1,375). Caledonia 30–35, 1L9 (under 7,125). |
| The empty battlegrounds | Abermenai and Murdaigean become complete, like Thidranki: guards and a hastener at each of the three portal keeps, and a capturable central keep with guards and a lord, levelled for the range. |
| Over the limit | Classic rules. The porter checks level and realm points when you port in. Once over a limit you may stay until you log out or die; then you are at your bind point, and the porter will not send you back. |
| Death | Release to the bind point, as today. |
| Atlas leftovers | Removed: the training dummies, the Void Merchants, the stray Wizard, Atlas's battleground daily quests and their Pazz NPCs (Thidranki 20–24 and Caledonia 34–39) and tokens, and the +50% XP bonus (all four battlegrounds the same: no bonus). |
| Keep and guard levels | Fit each range. Caer Caledon's guards (48) and lord (57) come down; Thidranki Faste's base level goes from 26 to 24; the new keeps are levelled for their ranges. |
| Names | "Caledon" is shown as Caledonia. Murdaigean keeps its spelling (patch notes 1.48–1.61, the client's `zones.dat` and our data all spell it that way). |
| Refusals | The porter tells the player why they can't go: level range or realm points. |
| Midgard logout bug | Fixed in the fork: `KeepManager` looks for a teleport "Svasudheim Faste", but the row is "Svasud Faste". Upstream candidate. |
| Left out | Restoring the archived battleground monsters (#47), bindstones in the battlegrounds, and keep claiming and capture realm points (unchanged). |

### Non-goals

- Cathal Valley (165), the New Frontiers Thidranki (238), and the GM-only "BG TELEPORTER" NPC in region 73
  (`scripts/teleporters/BGTeleporter.cs`, reachable by no zone point or teleport).
- Monsters in the battlegrounds. The 326 in Caledonia and 313 in Thidranki that upstream's setup archived stay
  archived; #47 holds that question.
- Bindstones, the 1.53 Siege Masters, the 1.60 Medal of Valor, keep claiming (`allow_bg_claim` stays False) and
  capture realm points (a capture still gives 0).
- The New Frontiers rule (1.70) that stops realm points at the cap. In the classic game they kept accruing
  inside, and upstream already does that (`serverrules/AbstractServerRules.cs:2099-2105`).
- Counting regions 250–253 as RvR regions (`world/Region.cs:304-330`, `IsRvR`). That flag also drives mounts,
  guild banners and other checks; the fork adds its own login check instead (section 3.3).
- Two upstream oddities found on the way, left alone and listed in #49: the zone-entry level check that can
  never trigger (`packets/Client/168/RegionChangeRequestHandler.cs:93-96`, `&&` instead of `||`; harmless, no
  zone point leads into a battleground), and release-to-portal-keep's `<=` test (`gameobjects/GamePlayer.cs:1455`,
  used only when `bg_release_to_portal_keep` is on).
- Thidranki's 11 "New Object" static items: not in the owner's list, so they stay. (Its Pazz NPCs and 20–24
  daily quests go, by the owner's decision of 2026-10-07; see 3.3.)
- The client's own texts. Its `zones.dat` already calls zone 250 "Caledonia", but its loading screen data
  (`loading.dat` in the client's `gamedata.mpk`, `[region250] name=Caledon`) still says "Caledon". Only the
  server's `Zones` and `Regions` rows change.

## 2. What players see

1. **Getting there is unchanged.** The free "Battlegrounds Medallion of Passage" comes from Sall Fadri (Castle
   Sauvage), Gwulla (Svasud Faste) or Araisa (Druim Ligen). Worn in the Mythical slot, it takes the player
   along when the realm's frontier porter (Master Visur, Stor Gothi Annark or Glasny, and the cross-realm
   porters) casts, every 120 seconds, within 500 units.
2. **Where the porter sends them.** Levels 15–19 go to Abermenai, 20–24 to Thidranki, 25–29 to Murdaigean and
   30–35 to Caledonia, if their realm points are under that battleground's cap. They arrive beside their own
   realm's portal keep, at today's spots. The medallion is used up.
3. **When it won't.** The porter says why in the chat window ("Master Visur says, ..."), once per ceremony,
   and the medallion stays on:
   - below level 15: "The battlegrounds are for levels 15 to 35. Come back when you reach level 15."
   - above level 35: "You have outgrown the battlegrounds, which are for levels 15 to 35."
   - too many realm points, for example: "Thidranki is for realm rank 1L3 and below, under 350 realm points.
     You have 412, so I cannot send you there."
   - a level with no battleground row (only if the owner deletes a row): "No battleground on this server
     takes level 22."

   The levels, ranks and caps in these texts come from the battleground rows, so they follow the data.
4. **Inside.**
   - Each portal keep has its guards and a hastener, all shown as level 255, as at every portal keep. In
     Abermenai and Murdaigean they are new.
   - The central keep (Dun Abermenai, Thidranki Faste, Dun Murdaigean, Caer Caledon) has renegade guards and a
     lord at the levels in 2.1. The new keeps start held by renegades, as the first battleground's middle keep
     did in Test 1.47.
   - Killing the lord gives the keep to the killer's realm; its guards come back at the same levels.
   - `/ck` lists the keeps and their owners; a keep held by renegades shows as "None" (for example "Dun
     Abermenai: None"). `/who` shows the zone as "Caledonia".
   - There are no dummies or Void Merchants, and no Pazz in Thidranki or Caledonia. No battleground has a zone XP bonus,
     even on a server that turns zone bonuses on (`enable_zone_bonuses`, off in the shipped worlds).
5. **Over the limit.** Levelling or earning realm points past the limit inside changes nothing at once.
   - **At logout**, the character is moved to its bind point.
   - **At the next login**, a character still inside a battleground it has outgrown (for example after a link
     death or a server crash) is moved to its bind point a moment after entering, with: "You have outgrown
     Thidranki (levels 20 to 24, realm rank 1L3 and below), so you are back at your bind point."
   - **After that**, the porter refuses it, with the reason. Midgard characters are moved too; today they never
     are.
6. **Death.** Release goes to the bind point, as today.
7. **Leaving.** Unchanged: the exit zone points lead to the home portal keeps.

### 2.1 The four battlegrounds after the change

| | Abermenai | Thidranki | Murdaigean | Caledonia |
|---|---|---|---|---|
| Region | 253 | 252 | 251 | 250 |
| Name shown | Abermenai | Thidranki | Murdaigean | Caledonia (was "Caledon") |
| Levels | 15–19 | 20–24 | 25–29 | 30–35 (porter was 34–39, row 30–34) |
| Highest realm rank, cap | 1L2, under 125 RP | 1L3, under 350 RP (was 7,125) | 1L5, under 1,375 RP | 1L9, under 7,125 RP (was 122,500) |
| Battleground row: MinLevel, MaxLevel, MaxRealmLevel | 15, 19, 3 (was 2) | 20, 24, 4 (was 10) | 25, 29, 6 (was 5) | 30, 35 (was 34), 10 (was 25) |
| Porter | on (was off) | on | on (was off) | on |
| Portal keeps (KeepID) | 35, 36, 37 | 12, 13, 14 | 41, 42, 43 | 38, 39, 40 |
| Portal keep guard rows | 34, new | 34 | 34, new | 34 |
| Central keep | Dun Abermenai, #32, new | Thidranki Faste, #11 | Dun Murdaigean, #33, new | Caer Caledon, #31 |
| Keep BaseLevel | 19 | 24 (was 26) | 29 | 35 (was 46) |
| Central guard rows | 12: 6 casters, 4 fighters, lord, hastener | 19 (unchanged) | 12, as Abermenai | 16 (the stray Wizard removed) |
| Guard / lord level | 21 / 24 | 26 / 31 (was 28 / 33) | 31 / 36 | 37 / 44 (was 48 / 57) |
| Zone XP bonus (`Zones.Experience`) | none | none (was +50%) | none | none (was +50%) |
| Removed | — | 15 training dummies, 2 Void Merchants, the six 20–24 daily quests and their Pazz NPCs | — | 1 Void Merchant, the stray Wizard, the six 34–39 daily quests and their Pazz NPCs |
| Kept as is | — | 11 "New Object" statics | — | — |
| Monsters, bindstones | none | none (#47) | none | none (#47) |

The +50% was paid only while `enable_zone_bonuses` is on (`gameobjects/GamePlayer.cs:4347`,
`bots/GameBot.cs:1564`), and that property is False in the shipped worlds, so nobody gets it today. Setting
`Experience` to 0 keeps the four battlegrounds the same on a server that turns it on.

## 3. How it works

### 3.1 Where each change lives

| Change | Where | Why there |
|---|---|---|
| Battleground rows, names, XP bonus, keep base levels | World data: new `deploy/bin/battlegrounds.py`, called by `deploy/bin/world_fixes.py` | It is world data; the fork fixes world data before every start, and upgraded or new worlds get it again |
| Portal keep guards and the central keeps of Abermenai and Murdaigean | World data, same module | Keeps and guards are `Keep` and `Mob` rows; no code is needed (Thidranki's work the same way) |
| Removing dummies, Void Merchants, the stray Wizard and saved battleground daily quests | World data, same module | They are rows. Removed `Mob` rows are archived in `fork_removed_mobs`, so they can be put back; the Thidranki and Caledonia `Quest` rows are deleted, since they hold only Atlas daily quest progress for classes that no longer exist |
| Porter levels, caps and refusal texts | Fork-owned `scripts/hearthdaoc/ClassicBattlegrounds.cs` (decisions) and `ClassicBattlegroundsScript.cs` (wiring), called from three lines in upstream's `OFTeleporters.cs` | The porter's medallion switch is private code inside upstream's class, with no hook; the decisions stay in fork files |
| Bind point at logout, login check, keep level after a capture | Fork-owned script, through upstream's events (`GamePlayerEvent.Quit`, `GamePlayerEvent.GameEntered`, `KeepEvent.KeepTaken`) | Public events, so no upstream file changes |
| "Svasud Faste" | Upstream `keeps/KeepManager.cs:786`, one word | The typo is in upstream code; it is also the upstream PR |
| Atlas's battleground daily quests (Thidranki 20–24, Caledonia 34–39) and their Pazz NPCs | The twelve upstream files in `scripts/quests/BattlegroundQuests/` (`Thidranki/*.cs` and `Caledonia/*.cs`) are deleted | They are compiled into the server and register themselves at load; no setting turns off one quest. The Pazz NPCs exist only in memory, made by these scripts at each start (3.2, step 6) |

### 3.2 World data: `deploy/bin/battlegrounds.py`

`world_fixes.apply()` calls `battlegrounds.apply(conn)` after its existing fixes, inside the same `with conn:`
block (`deploy/bin/world_fixes.py:64-87`). It returns one line per step that changed something, as the other
fixes do, and `world_fixes.py` prints them (`:97-103`).

**Failure.** `battlegrounds.apply(conn)` runs all its statements, including the `CREATE TABLE` of its two fork
tables, under `SAVEPOINT classic_battlegrounds`. (An explicit savepoint is needed: Python's `sqlite3` runs a
`CREATE TABLE` outside any transaction when none is open.) On any error it rolls back to the savepoint,
releases it, and returns the single line "Classic battlegrounds: not applied (<error>); the battlegrounds stay
as upstream ships them". So:
- the other fixes (Disciple, Saracen start, welcome messages) still commit;
- `world_fixes.py` exits 0 and the server starts (the entrypoint runs under `set -euo pipefail`,
  `deploy/entrypoint.sh:7`, so an exception would stop the start);
- no marker is written, so the next start tries again;
- until then the porter follows upstream's battleground rows: Abermenai and Murdaigean open, but without guards.
  The line in the server's start log says why.

**Once per world.**
- **Marker.** The fix records `classic-battlegrounds-v1` in a fork table
  `fork_world_fixes (FixId TEXT PRIMARY KEY, AppliedUtc TEXT NOT NULL)`, under the same savepoint as all its
  steps.
- **When the marker is there**, nothing runs, so changes the owner makes later stay: a guard removed in game is
  not added back, and a value set by hand is not reset.
- **Preconditions.** Each step also has its own: it changes a value only while it still holds upstream's
  value, and adds rows only where none of that kind exist. A world the owner edited before this release
  keeps those edits, and that step reports nothing.
- **Missing tables.** If the world lacks a table the fix needs (`Battleground`, `Keep`, `Mob`, `Door`, `Zones`,
  `Regions`, `Quest`), it does nothing and writes no marker.
- **New and upgraded worlds.** `hdc new-world` makes a fresh world, and `hdc upgrade-world` installs upstream's
  clean world and imports progress into it. Neither has the marker, so the fix runs at the next start.
- **Order.** The steps run in the order below, all under the one savepoint.

**Step 1, battleground rows.** Matched by `RegionID`. Each row is changed only while it has exactly
upstream's values, label included (the label, `Battleground_ID`, is the row's key; nothing else refers to it).

| Region | Upstream: label; MinLevel–MaxLevel; MaxRealmLevel | Classic |
|---|---|---|
| 253 | "Abermenai (Level 15-19)"; 15–19; 2 | "Abermenai (Level 15-19 - RR1L2)"; 15–19; 3 |
| 252 | "Thidranki (Level 20-24 - RR2L0)"; 20–24; 10 | "Thidranki (Level 20-24 - RR1L3)"; 20–24; 4 |
| 251 | "Murdaigean (Level 25-29)"; 25–29; 5 | "Murdaigean (Level 25-29 - RR1L5)"; 25–29; 6 |
| 250 | "Caledonia (Level 34-39 - RR3L5)"; 30–34; 25 | "Caledonia (Level 30-35 - RR1L9)"; 30–35; 10 |

MaxRealmLevel means "must be below". The server's realm level is (rank − 1) × 10 + level, so 3 is "below
1L3", that is under 125 realm points (`REALMPOINTS_FOR_LEVEL`, `gameobjects/GamePlayer.cs:3851`: 3 = 125,
4 = 350, 6 = 1,375, 10 = 7,125). Upstream's own checks read it that way: `keeps/KeepManager.cs:348` lets in
`RealmLevel < MaxRealmLevel`, and the logout check (`GamePlayer.cs:984-990`) moves `RealmLevel >= MaxRealmLevel`.

**Step 2, names and XP bonus.**
- `Zones` 250: `Name` 'Caledon' → 'Caledonia'.
- `Regions` 250: `Description` 'Caledon' → 'Caledonia'.
- `Zones` 250 and 252: `Experience` 50 → 0. 251 and 253 are already 0. (The bonus is paid only while
  `enable_zone_bonuses` is on, which is False in the shipped worlds; see 2.1.)

The server shows a zone's `Name` as its description (`world/WorldMgr.cs:358`), for example in `/who` and
the guild member list (`commands/playercommands/who.cs:266`, `guild.cs:1494`).

**Step 3, keep levels.**
- Thidranki Faste (KeepID 11): `BaseLevel` 26 → 24.
- Caer Caledon (31): `BaseLevel` 46 → 35.
- These two keeps: `Level` → 1 where a capture on this world left it higher. Section 3.3 keeps it at 1 from
  then on.
- Their gates' `Health` follows the new `BaseLevel` (a keep door's full health is `BaseLevel` ×
  `keep_doors_base_health`, 200, at keep Level 1: `propertycalc/MaxHealthCalculator.cs:84-96`): `252000301`/`302`
  5,200 → 4,800 and `250000301`/`302` 9,200 → 7,000, each only while it still has the old value. The server
  loads a door's health straight from its row (`gameobjects/GameDoorBase.cs:142`), so without this the gates
  would start above full health until the first hit.

**Step 4, portal keep guards for Abermenai and Murdaigean.**
- **Source rows.** Region 252 `Mob` rows of class `DOL.GS.Keeps.FrontierHastener`, `DOL.GS.Keeps.GuardFighter`
  or `DOL.GS.Keeps.GuardStaticCaster` that lie within 4,000 units (the portal keep area's radius,
  `keeps/KeepArea.cs:10`) of one of Thidranki's portal keeps (KeepID 12, 13, 14). That is 34 rows:
  - Albion's keep: 1 hastener, 2 fighters, 9 casters;
  - Midgard's and Hibernia's: 1 hastener, 2 fighters, 8 casters each.
- **Precondition.** The target region has no `DOL.GS.Keeps.*` rows yet. 251 and 253 have none today.
- **Copies.** Every column is copied unchanged (same X, Y, Z and heading), with `Region` 251 or 253,
  `Mob_ID` `hdc-bg<region>-pk-<source Mob_ID>`, and `LastTimeRowUpdated` set to now.
- **Why it fits.**
  - All four battlegrounds use one map: the same `terrain.pcx`, `offset.pcx` and `bound.csv` in the client's
    `zones/zone25x/dat25x.mpk`.
  - Their portal keep `Keep` rows have identical positions (35–37 and 41–43 match 12–14).
  - Caledonia's 34 portal keep rows already match Thidranki's.
- **Levels.** Nothing to rescale: everything at a portal keep, hasteners included, shows level 255
  (`keeps/Gameobjects/Guards/GameKeepGuard.cs:88-93`; `FrontierHastener` derives from `GameKeepGuard`,
  `Hastener.cs:29`).

**Step 5, central keeps for Abermenai and Murdaigean.**

*Keep rows.* Added only if the region has no `Keep` row with `BaseLevel` under 100.

| | Dun Abermenai | Dun Murdaigean |
|---|---|---|
| KeepID | 32 | 33 |
| Name | Dun Abermenai | Dun Murdaigean |
| Region | 253 | 251 |
| X, Y, Z | 33383, 38627, 3720 | 33113, 38138, 3720 |
| Heading | 3858 | 1583 |
| BaseLevel | 19 | 29 |
| Level | 1 | 1 |
| Realm, OriginalRealm | 0, 0 (renegades) | 0, 0 |
| Albion/Midgard/HiberniaDifficultyLevel | 1, 1, 1 | 1, 1, 1 |
| KeepType, SkinType | 0, 0 | 0, 0 |
| CreateInfo | `HearthDAoC classic-battlegrounds-v1` | the same |
| ClaimedGuildName | '' (empty, as on every battleground keep row) | '' |
| LastTimeRowUpdated | now | now |
| Keep_ID | `hdc-bg253-dun-abermenai` | `hdc-bg251-dun-murdaigean` |

- **IDs.** The KeepIDs are the first free IDs from 32 (32 and 33 in the shipped worlds).
- **Name.** The server names the keep's area after `Name` and uses it in `/ck` and the capture messages
  (`keeps/AbstractGameKeep.cs:441-455`, `scripts/commands/ck.cs:38`).
- **SkinType 0** makes the server create a `GameKeep` (99 would make a relic keep, `keeps/KeepManager.cs:84`),
  which the lord's level formula needs (3.4).
- **Keep area.** The server gives each keep a keep area of radius 3,000 around X, Y (`keeps/AbstractGameKeep.cs:434-461`).
  Guards and doors inside it belong to the keep (`GameKeepGuard.cs:462-490`, `gameutils/DoorMgr.cs:70-90`).

*Why not Thidranki Faste's guards.* The client puts a different keep model at the centre of each battleground
(fixture 3 in `fixtures.csv` of the client's `zones/zone25x/csv25x.mpk`):
- Caledonia: Albion `bfrontkeep2`;
- Thidranki: Norse `nfrontkeep2`;
- Murdaigean and Abermenai: Hibernian `Hfrontkeep`, at zone 25216, 30080 rotated 320°, and at 24960, 30208
  rotated 160°.

Thidranki Faste's 19 guard spots fit the Norse model only. The Hibernian model is the one every battleground
uses for its Hibernia portal keep (fixture 414, at zone 9856, 9984 rotated 130°). So the central guards are
Thidranki's Hibernia portal keep guards, rotated and moved onto the central model.

The client's older `dat25x.mpk` copy of `fixtures.csv` still has the pre-1.60 layout: every centre keep at
zone 25088, 30080 rotated 90° (`Hfrontkeep` in 251 and 253). The world's door rows fit the `csv25x.mpk`
layout within 41 units (see *Check* below) and miss the `dat25x.mpk` layout by 1,100 to 2,400 units, and
patch 1.60 describes the csv rotations ("Dun Murdaigean faces the Hibernia portal keep"), so the move uses
the csv layout. Fixture 414 is the same in both copies.

*The move.*
- **Position.** world = C + R(Δθ) · (p − P):
  - P = (18048, 18176), the portal keep model's origin (zone position plus the zone offset of 8192);
  - C = (33152, 38400) in 253 and (33408, 38272) in 251;
  - Δθ = 160° − 130° = +30° in 253, and 320° − 130° = +190° in 251, as standard angles from +X towards +Y;
  - results are rounded to whole units.
- **Height.** Z − 600: the central keep's floor is 3720 and the portal keep's 4320, from the door rows.
- **Heading.** Plus round(Δθ × 4096 / 360), modulo 4096: +341 in 253 and +2162 in 251. Headings and standard
  angles turn the same way (`world/Point2D.cs:107`).
- **Check.** Moving each region's own portal keep door rows (`<region>041601`, `041602`) this way lands within
  41 units of its central keep door rows (`<region>000301`, `000302`): 6.7 and 25.2 in 253, 40.1 (Murdaigean's
  outer door) and 6.1 in 251. Heights are within 20 and headings within 60 units of theirs (with the opposite
  turn, Abermenai's outer door would be 736 off). Dun Murdaigean's gate then faces the Hibernia portal keep,
  as patch 1.60 says.

*Which rows.*
- **Moved.** Only the rows that stand on the keep model:
  - the Hibernia portal keep's hastener (Mob `802a1b0a-…`, floor, Z 4320);
  - its six wall-top casters (Z 4736).

  Its two fighters and two other casters stand on the hillside outside the walls (Z 4022–4304), whose height
  differs at the centre, so they are not moved.
- **Added at the gate.** Four `GuardFighter`, placed from the central keep's own door rows (outer door
  `000301`, inner door `000302`), at the inner door's height (3720), facing out through the gate:
  - two in the passage between the doors, at its midpoint, 60 units to either side;
  - two 150 units inside the inner door, 60 units to either side.
- **Added for the lord.** One `GuardLord` at the Keep row's spot, facing the gate. That spot is where a GM
  stood to create Thidranki's Hibernia portal keep (`/keep create`, recorded in its `CreateInfo`), so it is
  open floor inside the model.
- **Column values.** Each row copies a template row of its class:
  - moved rows: their source;
  - fighters: the Hibernia portal keep fighter `b67eacce-…`;
  - lord: Thidranki Faste's lord `863582fc-…`.

  All are "new mob", model 408, level 1 placeholders. The server sets name, realm, model and level when it
  loads a keep guard (`GameKeepGuard.RefreshTemplate`, `:657-676`).
- **Mob_IDs.**
  - moved rows: `hdc-bg<region>-ck-<source Mob_ID>`;
  - fighters: `hdc-bg<region>-ck-fighter-1` to `-4`;
  - lord: `hdc-bg<region>-ck-lord`.
- **Precondition.** The 12 central rows are added only in the run that adds the region's `Keep` row (the same
  precondition), and each only if its `Mob_ID` does not exist yet. Step 4's test cannot guard them, since
  step 4 has already put `DOL.GS.Keeps.*` rows into the region. So if the owner deletes the marker after
  removing a new `Keep` row, the keep comes back without doubling its guards or failing on a duplicate key.

| Row (source) | Abermenai 253: X, Y, Z, heading | Murdaigean 251: X, Y, Z, heading |
|---|---|---|
| Hastener (`802a1b0a`) | 33612, 39657, 3720, 3888 | 32546, 37248, 3720, 1613 |
| Caster (`62f874d0`) | 31916, 37946, 4136, 1287 | 34724, 38276, 4136, 3108 |
| Caster (`be8e2cbf`) | 32320, 39124, 4136, 860 | 33942, 37307, 4136, 2681 |
| Caster (`3a07da41`) | 33816, 37292, 4136, 2202 | 33163, 39541, 4136, 4023 |
| Caster (`2fc59f4b`) | 32584, 39729, 4136, 127 | 33487, 36829, 4136, 1948 |
| Caster (`b05f95a5`) | 34056, 37934, 4136, 2819 | 32718, 39019, 4136, 544 |
| Caster (`f1f1d987`) | 34509, 38996, 4136, 3320 | 31929, 38176, 4136, 1045 |
| Fighter 1, 2 (gate passage) | 33697, 39351 and 33811, 39312; 3720, 3877 | 32563, 37580 and 32472, 37657; 3720, 1592 |
| Fighter 3, 4 (inside the inner door) | 33553, 38937 and 33666, 38898; 3720, 3877 | 32840, 37909 and 32749, 37986; 3720, 1592 |
| Lord | 33383, 38627, 3720, 3877 | 33113, 38138, 3720, 1592 |

*Doors.*
- **Keep doors.** The central doors `253000301`/`302` and `251000301`/`302` fall inside the new keep areas, so
  they load as keep doors.
- **Health.** 2,545 → `BaseLevel` × `keep_doors_base_health`: 3,800 for `253000301`/`302` and 5,800 for
  `251000301`/`302`, each only while it is still 2,545. Otherwise they would start at 67% and 44% and take
  hours to repair (5% every 30 minutes out of combat, `keeps/Gameobjects/GameKeepDoor.cs:580-589`).
- **Closed.** Their `State` goes from upstream's 0 to 1 (closed), only while it is 0, like the gates
  (`000301`/`000302`) of Thidranki Faste and Caer Caledon and every portal keep door. At their health they
  would load closed anyway: a keep door loads closed above 15% health and in its saved state below that
  (`keeps/Gameobjects/GameKeepDoor.cs:469-475`).

*Levels.* Guard rows hold no level that matters. Section 3.4 gives the levels that come from `BaseLevel`.

**Step 6, Atlas leftovers.**
- **Archived first.** The rows are copied into a fork table `fork_removed_mobs` (the `Mob` columns plus
  `FixId` and `RemovedUtc`), then deleted:
  - every `DOL.GS.DPSDummy`, `DOL.GS.HitbackDummy` and `DOL.GS.HealDummy` row in regions 250–253: 15, all in
    Thidranki (9 DPS, 6 hitback);
  - every `DOL.GS.Scripts.RPTradeInMerchant` ("Void Merchant") row in 250–253: 3. These are `2ae9038a-…`
    (Caledonia, Albion) and `27d30f96-…` and `ecb08ffb-…` (Thidranki, Midgard and Hibernia);
  - the stray Wizard, `caledon-guard-25`: a `GuardStaticCaster` named "Wizard", realm 1, level 48, at 33185,
    37386, 3722. It sits on a Thidranki caster's X and Y at Caledonia's floor height. It is removed only
    while it still has these values.
- **No Pazz rows.** The Pazz NPCs of Thidranki and Caledonia are not rows. The quest scripts make them at each start and call
  `SaveIntoDatabase()`, but that saves nothing for an NPC a script made (`LoadedFromScript` starts true,
  `gameobjects/GameNPC.cs:991`, and with no row yet it returns, `:1169-1175`). A world that has run with
  `load_quests` on has no `Pazz` row either. Deleting the twelve quest scripts (3.3) removes them.
- **Saved quests.** `Quest` rows of the twelve battleground quest classes are deleted, not archived. Active and
  finished quests share the table and keep their progress in the row (`GamePlayer.cs:10502-10512`), and with
  the classes gone nothing could use them again. The clean world has no `Quest` rows. The classes are:
  `DOL.GS.DailyQuest.Albion.CaleKeepCaptureAlb`, `DOL.GS.DailyQuest.Hibernia.CaleKeepCaptureHib`,
  `DOL.GS.DailyQuest.Midgard.CaleKeepCaptureMid`, `DOL.GS.DailyQuest.Albion.CaleKillQuestAlb`,
  `DOL.GS.DailyQuest.Hibernia.CaleKillQuestHib` and `DOL.GS.DailyQuest.Hibernia.CaleKillQuestMid` (sic);
  `DOL.GS.DailyQuest.Albion.ThidKeepCaptureAlb`, `DOL.GS.DailyQuest.Hibernia.ThidKeepCaptureHib`,
  `DOL.GS.DailyQuest.Midgard.ThidKeepCaptureMid`, `DOL.GS.DailyQuest.Albion.ThidKillQuestAlb`,
  `DOL.GS.DailyQuest.Hibernia.ThidKillQuestHib` and `DOL.GS.DailyQuest.Hibernia.ThidKillQuestMid` (sic).
  Without their classes, the server would log "Could not find quest" at each login of those characters
  (`quests/QuestsMgr/AbstractQuest.cs:76-95`).

### 3.3 Server code

**Fork-owned files**, in `GameServer/scripts/hearthdaoc/` (namespace `DOL.GS.HearthDAoC`, like the Shrouded
Isles start choice):

- **`ClassicBattlegrounds.cs`, the decisions.** A plain class that unit tests drive without a server. It never
  reads `GameServer`, `WorldMgr` or `ServerProperties`; everything comes in through parameters.
  - **The four battlegrounds:** regions 253, 252, 251 and 250 with their names. A bracket is a battleground's
    row: region, name, MinLevel, MaxLevel, MaxRealmLevel, and the realm-point cap
    (`REALMPOINTS_FOR_LEVEL[MaxRealmLevel]`).
  - **Porter decision.**
    - Inputs: level, realm level, realm points, realm and the brackets.
    - The bracket is the one whose MinLevel–MaxLevel holds the level.
    - **Go**, to that region at the realm's landing spot, when MaxRealmLevel is 0 (no cap, as in
      `KeepManager.GetBGPK`) or the realm level is below it.
    - **Refuse**, with one of the section 2 texts, otherwise. The "15 to 35" in them is the lowest MinLevel and
      the highest MaxLevel. A rank label is (realm level ÷ 10 + 1) "L" (realm level mod 10), so the highest
      allowed realm level, MaxRealmLevel − 1, reads 1L2, 1L3, 1L5 or 1L9.
  - **Landing spots**, today's (`OFTeleporters.cs:233-270`, `392-428`, `549-585`):
    - Albion 38113, 53507, 4160, heading 3268;
    - Midgard 53568, 23643, 4530;
    - Hibernia 17367, 18248, 4320.
  - **Over the limit:**
    - the account is a player's (privilege level 1);
    - the region is one of the four and has a row;
    - and Level > MaxLevel, or MaxRealmLevel > 0 and realm level ≥ MaxRealmLevel.

    This is upstream's logout test (`GamePlayer.cs:984-990`), except that MaxRealmLevel 0 means no cap.
  - **Where an over-limit character goes:**
    - the bind point, when its region exists, has a zone at the bind X and Y, and is not one of 250–253;
    - otherwise the realm's home portal keep, through upstream's `KeepManager.ExitBattleground` (Castle
      Sauvage, Svasud Faste or Druim Ligen).
  - **Keep level:** a keep in 250–253 that is not a portal keep and whose Level is not 1 goes back to 1.
- **`ClassicBattlegroundsScript.cs`, the wiring.**
  - **`PorterDestination(GameNPC porter, GamePlayer player)`.**
    - It builds the brackets from `GameServer.KeepManager.GetBattleground(region)` (rows read at server start)
      and asks `ClassicBattlegrounds`.
    - On a refusal it says the text with `porter.SayTo(player, eChatLoc.CL_ChatWindow, text)`
      (`gameobjects/GameNPC.cs:2670-2694`; the chat shows `Master Visur says, "..."`, from
      `language/EN/GameObjects/GameNPC.txt:20`) and returns null, so the medallion stays.
    - **Once per ceremony.** The porter's callback runs twice per ceremony: two timers share it
      (`OFTeleporters.cs:113-117`) and start 5 and 15 seconds after the cast (`:169-170`), and each run goes
      through every player within 500 units (`:196`). So `PorterDestination` stores the game time of a refusal
      in `player.TempProperties` (key `hdc_bg_porter_refused`) and says nothing when it refused that player
      within the last 30 seconds. The next ceremony, 120 seconds later, says it again. `ClassicBattlegrounds`
      decides this from the two times, so a unit test covers it.
    - Otherwise it returns the `GameLocation`.
  - **Handlers.** A `[ScriptLoadedEvent]` method adds three with `GameEventMgr.AddHandler`. Each one handles
    only a `GamePlayer` sender or a keep event; bots are `GameBot`, a `GameNPC`.
  - **`GamePlayerEvent.Quit`: the bind point at logout.** For an over-limit character it moves the character
    as above.
    - **Order.** `GamePlayer.Quit(true)` sends this event before `Delete()` runs `CleanupOnDisconnect`
      (`GamePlayer.cs:1099-1103`). Upstream's own check there, which would send the character to the home
      portal keep, then finds it outside the battleground.
    - **When it sticks.** On `/quit` the character is saved after (`GamePlayer.cs:822-832`), so the move
      sticks.
    - **Link death.** The character is saved before the quit (`GamePlayer.cs:1138-1145`), so the move is
      lost; the login check covers that case.
  - **`GamePlayerEvent.GameEntered`: the login check.**
    - **When.** `teleport_login_bg_level_exceeded` is on (True in the shipped worlds; upstream's own switch
      for this) and the character is over the limit.
    - **Delay.** It waits 1 second with an `ECSGameTimer`. The event fires inside PlayerInit, before "player
      init finished", and upstream's own login move also runs after that point.
    - **Then.** If the player is still active on the same client, still in that battleground and still over
      the limit, it gets the section 2 message in the system window and the same move.
    - **Why it is needed.** Upstream's login check (`packets/Client/168/PlayerInitRequestHandler.cs:85-86,
      154-173`) never runs here, because `Region.IsRvR` leaves out 250–253. It also compares the level with
      keep `BaseLevel`, not with the battleground row.
  - **`KeepEvent.KeepTaken`: keep level after a capture.**
    - **What.** For a battleground central keep whose Level is not 1, it runs `keep.ChangeLevel(1)`, which
      re-levels guards and doors, then `keep.SaveIntoDatabase()`.
    - **Why.** A capture resets a keep to `starting_keep_level`, 4 in the shipped worlds
      (`AbstractGameKeep.Reset`, `:1047-1056`), and the event fires at the end of that reset (`:1115`). At
      level 4 every guard would gain 4.8 levels (Level × `keep_guard_level_multiplier` 1.6): Thidranki
      Faste's guards would go from 26 to 31.
    - **When.** Guards killed by the reset respawn later and take the keep's level then.

**Upstream files changed** (listed in `docs/fork/FORK.md`):

- **`GameServer/scripts/teleporters/OFTeleporters.cs`.** In each of the three `case BattlegroundsID:` blocks
  (Albion `:231-273`, Midgard `:390-431`, Hibernia `:547-588`), the hard-coded levels and caps and Atlas's
  commented-out Abermenai and Murdaigean branches are replaced by
  `PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);`. The file is in
  `namespace DOL.GS.Scripts` and has no `using DOL.GS.HearthDAoC;` (lines 1-8), so the name is qualified;
  `HearthDAoC` resolves through the enclosing `DOL.GS` namespace. Nothing else in the file changes, the usings
  included.
- **`GameServer/keeps/KeepManager.cs:786`.** "Svasudheim Faste" becomes "Svasud Faste", the `TeleportID` of
  the rows `atlas_mid_svasud_faste` and `dc97a6d7-…`. Today `ExitBattleground` finds no row and moves no
  Midgard character. The fork needs it for the fallback above, and it is a one-line upstream PR.
- **Deleted:** the twelve files in `GameServer/scripts/quests/BattlegroundQuests/`, six in `Caledonia/` and six
  in `Thidranki/` (owner's decision, 2026-10-07: Thidranki's Pazz goes too).
  - They are Atlas's daily quests for Caledonia 34–39 ("[Daily] Caledonia Conquerer", "[Daily] Frontier
    Conquerer", "[Daily] Fen's New Friends") and Thidranki 20–24 ("[Daily] Thidranki Conquerer", "[Daily]
    Frontier Conquerer", "[Daily] Fen's New Friends"), and the code that creates their Pazz NPCs in regions
    250 and 252, in memory only (3.2, step 6).
    `CaleKillQuestMid` looks for an NPC named "Rey" (`CaleKillQuestMid.cs:68`), never finds one and makes
    another Midgard Pazz at each start; that goes too.
  - The `<Folder Include=...Caledonia\>` and `<Folder Include=...Thidranki\>` lines in `GameServer.csproj`
    stay; they are harmless, and keeping them leaves the project file untouched.
  - **Tokens.** `AtlasROGManager.GenerateBattlegroundToken` (`Managers/RandomObjectGeneration/AtlasROGManager.cs:135-162`)
    gives `L20RewardToken` (20–24) or `L35RewardToken` (34–39). Neither item template exists in the shipped
    worlds, so no token is ever given, and with these quests gone nothing asks for a token. That file
    stays as it is.

**Unchanged on purpose:**
- `GamePlayer.CleanupOnDisconnect`, the logout check;
- release to the bind point (`bg_release_to_portal_keep` stays False);
- realm points over the cap: still earned, without the rank-difference multiplier (`AbstractServerRules.cs:2099-2105`);
- capture realm points, claiming, `Region.IsRvR` and `BGTeleporter`.

### 3.4 Keep and guard levels

The server sets a central keep's guard levels from its `BaseLevel` and `Level` (`AbstractGameKeep.cs:837-884`).
The results are rounded down:

- guard = BaseLevel + 1 + Level × 1.6;
- lord = BaseLevel + (BaseLevel ÷ 10 + 1) × 2 + Level × 1.6 (the ÷ is integer division);
- the 1.6 is `keep_guard_level_multiplier`.

The keeps stay at Level 1 (step 3 and the capture handler). `BaseLevel` is the top of each range, as in DOL's
own New Frontiers battleground keeps (Dun Killaloe 19 for 15–19, Molvik Faste 39 for 35–39) and as upstream's
login check reads it.

| Keep | BaseLevel | Guards | Lord |
|---|---|---|---|
| Dun Abermenai | 19 | 21 | 24 |
| Thidranki Faste | 24 (was 26) | 26 (was 28) | 31 (was 33) |
| Dun Murdaigean | 29 | 31 | 36 |
| Caer Caledon | 35 (was 46) | 37 (was 48) | 44 (was 57) |

Guards are two levels above the top of the range and lords five to nine, so a group from the top of the range
can take the keep. Portal keep guards and hasteners stay at 255 (`GameKeepGuard.cs:88-93`); the central keeps'
hasteners are level 1 (`AbstractGameKeep.cs:862-866`).

### 3.5 Effect on existing players and on the update

- **Over the new limits.** Characters saved inside Thidranki or Caledonia over the new limits are moved to
  their bind point at their next login, with the message. That covers Thidranki with 350 or more realm points,
  and Caledonia at level 36 or above or with 7,125 or more. It includes Midgard characters that should have
  been moved before.
- **Within the limits.** Characters stay where they are.
- **Abermenai and Murdaigean.** Nobody can be inside them today.
- **The update itself.** `./hdc update` stops the server, which logs everyone out; the fix runs at the next
  start, before anyone logs in.
- **Keep owners.** Thidranki Faste and Caer Caledon keep their current owner realm; only their Level goes back
  to 1.
- **Clients.** Nothing changes on the client.

## 4. Testing

- **World data unit tests** (Python, new `deploy/tests/test_battlegrounds.py`, on a scratch schema with the
  columns the module uses):
  - each step changes upstream's values and reports its line; a second run changes nothing, because of the
    marker;
  - a failure in any step rolls back the whole battleground fix, the fork tables included (no change, no
    marker), returns only the "not applied" line, and leaves the other `world_fixes` changes committed and
    `world_fixes.py` exiting 0;
  - with the marker deleted after a first run and a new `Keep` row removed, a second run adds the `Keep` row
    back, adds no `Mob` row twice and does not fail;
  - the owner's values are kept: a Battleground row with other values, another BaseLevel, Experience or zone
    name, a region that already has a central keep or keep guard rows, and an edited stray Wizard;
  - removed rows are in `fork_removed_mobs` with the fix id;
  - the move: P maps to C, a point at P + (100, 0) lands at the expected angle, headings wrap at 4096;
  - no marker is written when a needed table is missing.
- **Real-data tests**, with `HDC_TEST_WORLD` on a copy of the clean world (CI's pinned world too):
  - **Before the fix,** the world holds exactly what the steps expect:
    - the four Battleground rows of step 1;
    - 'Caledon' and Experience 50;
    - BaseLevels 26 and 46;
    - Thidranki's 34 portal keep rows (3 hasteners, 6 fighters, 25 casters), the Hibernia portal keep's 7
      on-model rows and the template rows;
    - the eight central and portal keep door rows of 251 and 253, the four central ones (`000301`/`000302`)
      with Health 2,545 and State 0;
    - the gates of Thidranki Faste and Caer Caledon with Health 5,200 and 9,200;
    - KeepIDs 32 and 33 free;
    - the Teleport rows "Castle Sauvage", "Svasud Faste" and "Druim Ligen" and no "Svasudheim Faste";
    - 15 dummies, 3 Void Merchants and `caledon-guard-25`.
  - **After the fix,** the state of table 2.1:
    - rows, names, XP, BaseLevels and the two Keep rows with every value in 3.2, `Name` included;
    - in 251 and 253, 34 portal keep rows on Thidranki's positions and 12 central rows on the coordinates in
      3.2;
    - every new guard within its keep's area: 4,000 for a portal keep, 3,000 for a central keep, and in no
      other keep's area;
    - the four central doors within 3,000 units of their new keep, with State 1 and Health 3,800 (253) and
      5,800 (251); the gates of Thidranki Faste and Caer Caledon at 4,800 and 7,000;
    - no dummy or Void Merchant class left in 250–253, and the removed rows archived.
  - **Door check:** moving each region's portal keep door rows with that region's move lands within 50 units
    (X, Y) and 25 (Z) of its central door rows.
  - **Second run:** no change.
  - **Existing test:** `test_world_fixes.test_shipped_world` is updated for the new lines.
- **Source checks** (Python, same file), so that an upstream sync that brings the old code back fails CI:
  - `OFTeleporters.cs` has three `case BattlegroundsID:` blocks, each holding only the line
    `PortLocation = HearthDAoC.ClassicBattlegroundsScript.PorterDestination(this, player);` (and its
    `break;`), and no 7125 or 122500;
  - `KeepManager.cs` names "Svasud Faste" and not "Svasudheim Faste";
  - the `BattlegroundQuests` folders hold no `.cs` file, and no code names the twelve classes.
- **Server unit tests** (C#, `UT_ClassicBattlegrounds`, like `UT_SiStartChoice`), for `ClassicBattlegrounds`:
  - **Brackets:**
    - levels 14 and 36 are refused with their texts;
    - 15 and 19 go to Abermenai, 20 and 24 to Thidranki, 25 and 29 to Murdaigean, 30 and 35 to Caledonia.
  - **Caps at each edge:**
    - realm level 2 goes to Abermenai and 3 is refused, with 125 and the player's own points in the text;
    - the same at 3/4 for Thidranki, 5/6 for Murdaigean and 9/10 for Caledonia;
    - MaxRealmLevel 0 means no cap.
  - **Landing spots:** each realm's spot in each battleground.
  - **Missing row:** a level whose row is missing gets "No battleground on this server takes level N."
  - **Over the limit:**
    - true for Level > MaxLevel or realm level ≥ MaxRealmLevel;
    - false for a GM, and outside 250–253, Cathal Valley included.
  - **Where to go:** the bind point, unless it is missing, has no zone or is in 250–253; then the home portal
    keep.
  - **Keep level:** a central keep in 250–253 at Level 4 goes back to 1. Nothing happens for a portal keep,
    at Level 1, or in another region.
  - **Rank labels:** realm level 2 → 1L2, 3 → 1L3, 5 → 1L5 and 9 → 1L9.
  - **Refusal once per ceremony:** a refusal is said when there was none before or the last was 30 seconds
    or more ago, and not when it was 10 seconds ago.

  The CI step "Server unit tests for the fork's server changes" gets its filter extended to
  `FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice|FullyQualifiedName~UT_ClassicBattlegrounds`,
  and `deploy/tests/test_workflows.py` pins it.
- **In game**, checked by the owner and recorded in `docs/fork/verification/sub5-ingame.md`:
  - **Abermenai and Murdaigean:**
    - a level 15–19 character with the medallion arrives beside its portal keep;
    - the portal keep guards and the hastener are there;
    - `/ck` lists "Dun Abermenai: None" ("Dun Murdaigean: None"), that is, held by renegades.
  - **At each new central keep:**
    - the casters stand on the walls, neither floating nor inside a wall;
    - the fighters stand at the gate, and the lord inside on the floor, reachable once both doors are down;
    - the gate is closed and can be broken;
    - guards are about level 21 (31 in Murdaigean) and the lord about 24 (36);
    - Dun Murdaigean's gate faces the Hibernia portal keep.
  - **Capture:** killing the lord gives the keep to the realm; the guards come back at the same levels, not
    five higher; `/ck` shows the new owner.
  - **Thidranki:**
    - level 20–24 with under 350 realm points gets in;
    - with 350 or more, the porter explains why not, once per ceremony (not again at the second port 10
      seconds later);
    - guards are about 26 and the lord about 31;
    - there are no dummies, Void Merchants or Pazz.
  - **Caledonia:**
    - level 30 gets in (refused today), and level 36 is refused with the text;
    - `/who` shows Caledonia;
    - guards are about 37 and the lord about 44;
    - no Wizard, Void Merchant or Pazz.
  - **Over the limit:**
    - a character that dings 20 in Abermenai stays there;
    - after `/quit` it logs in at its bind point, and the porter refuses it;
    - closing the client inside instead (link death) gives the move and the message a moment after the next
      login;
    - one Midgard character does the same.
  - **Death:** release in a battleground goes to the bind point.
  - **Server log:** the battleground fix lines at the first start, no "Could not find quest" errors, and no
    door or guard load warnings for 251 and 253.
  - The zone XP bonus is not checked in game: it is paid only with `enable_zone_bonuses` on, which the
    shipped worlds leave off; the real-data test checks `Experience` 0.

## 5. Documentation and bookkeeping

- **`docs/fork/FORK.md`:**
  - "What the fork changes" gets a row: classic battleground world data, `deploy/bin/battlegrounds.py` (from
    `world_fixes.py`), upstream files touched: none.
  - The server-code table gets a row:
    - fork files: `scripts/hearthdaoc/ClassicBattlegrounds.cs`, `ClassicBattlegroundsScript.cs`, test
      `Tests/UnitTests/UT_ClassicBattlegrounds.cs`;
    - upstream files touched: `OFTeleporters.cs` (three blocks), `KeepManager.cs` (one word), and the twelve
      deleted battleground quest files (Thidranki and Caledonia);
    - why: #76 and this spec;
    - upstream: the "Svasud Faste" fix is a candidate; the rest is fork-only.
- **`deploy/bin/world_fixes.py`:** its docstring gets item 4, the battleground fix. So does the entrypoint
  comment above the `world_fixes.py` call.
- **`client/README.md`** (player guide): a short "Battlegrounds" section. It covers the medallion and porter,
  the four ranges and caps, what happens once over a limit, and that there is no XP bonus.
- **`deploy/HANDOFF.md`:**
  - the fix runs once per world, and the `fork_world_fixes` row `classic-battlegrounds-v1` records it;
  - deleting that row makes it run again at the next start, and steps whose results are still there change
    nothing;
  - removed `Mob` rows are in `fork_removed_mobs` (the Thidranki and Caledonia `Quest` rows are deleted, not archived);
  - if the fix fails, the start log says "Classic battlegrounds: not applied (...)", the server starts with
    upstream's battlegrounds, and the fix tries again at the next start;
  - battleground keep guard levels follow `keep_guard_level_multiplier` (1.6), which also sets the frontier
    keeps' guards.
- **Plan and verification:** `docs/fork/plans/2026-10-07-classic-battlegrounds.md` and
  `docs/fork/verification/sub5-ingame.md`.
- **Issues:**
  - **#76** is closed by the PR.
  - **#47** already holds the battleground monsters (comment of 2026-10-07).
  - **#49** gets three entries:
    - the "Svasud Faste" fix, as a one-line PR from upstream `main` under the upstream contribution policy;
    - the `RegionChangeRequestHandler` `&&` check, noted;
    - the release `<=` test, noted.
- **Release notes:** a line saying that characters left inside a battleground they no longer qualify for wake
  at their bind point at their next login.

## 6. Risks

| Risk | Handling |
|---|---|
| An upstream sync changes `OFTeleporters.cs`, `KeepManager.cs` or the deleted quest files | Small, listed edits: three call lines, one word, twelve deletions (a modify/delete conflict is resolved by keeping them deleted). The source checks fail CI if a sync brings the old code back. Everything else uses public events and the keep manager's public methods. |
| A new upstream world changes the rows the fix expects | The real-data tests run on the pinned world in CI. When a sync pins a world whose battleground rows, keeps or guard rows differ, they fail in that PR, and the fix is updated there. On a live world the step's precondition simply doesn't match, and that step changes nothing. |
| The battleground fix fails on a world | It rolls back only its own steps under its savepoint, prints "Classic battlegrounds: not applied (...)", and the server starts with the other fixes in place (3.2). The porter then follows upstream's battleground rows, so Abermenai and Murdaigean open without guards until a later start applies the fix. The unit tests cover the rollback. |
| A world upgrade or a new world | Upstream's clean world has no marker, so the fix runs again at the next start. Edits the owner made in game to battleground guards and keeps are lost, as with any world data in an upgrade; HANDOFF says so. |
| Characters inside a battleground during the update | Within the limits they stay. Over them they wake at the bind point at the next login, with the message (3.5). |
| The new central keeps' guard spots are derived, not measured | The move is checked against the door rows (within 41 units, at most 40.1 for Murdaigean's outer door) and pinned by a test. Fighters and lord are placed from door rows and a known floor spot; the in-game check covers each row. A misplaced row is fixed by a later fix (`classic-battlegrounds-v2`) that moves exactly that `Mob_ID`, and only while it is still at the v1 position. |
| Keep level after a capture | The `KeepTaken` handler sets it back to 1. If it failed, the keep would stay at level 4 and its guards about five levels higher until the next capture or a GM's `/keep level 1` there (`commands/gmcommands/keep.cs:2271-2295`, which calls `ChangeLevel`): nothing at a start lowers it, since step 3 runs once per world and the keep load lowers only a keep above `starting_keep_level` (`AbstractGameKeep.cs:535-538`). The in-game capture check covers it. |
| A bind point inside a battleground, or invalid | The character goes to its realm's home portal keep instead, which needs the "Svasud Faste" fix for Midgard. |
| Link-dead logouts | The logout move isn't saved, as upstream's isn't; the login check moves the character at its next login. |
| Levels depend on `keep_guard_level_multiplier` (1.6) and keep Level 1 | Changing that property shifts every keep's guards, frontier keeps included; HANDOFF names it. |
| Battleground rows are read once, at server start | Edits to the rows by hand need a restart, as today. |
| KeepIDs 32 and 33 taken by a future world | The fix takes the first free IDs from 32. The real-data test pins 32 and 33 on the pinned world. |
| Logging in near a renegade central keep | Upstream moves a player who logs in near an enemy keep to the bind point after the grace period (`PlayerInitRequestHandler.cs:82-83`). That now also applies near Dun Abermenai and Dun Murdaigean, as it does near Thidranki Faste today. |
