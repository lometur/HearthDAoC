# Sub-project 2: classic character creation (design)

Status: approved by the owner. Date: 2026-10-06. Fork: `lometur/HearthDAoC`.
Issues: #39 (classic stat points), #55 (base classes at creation), #54 (splash rebrand).
Branch: `sub2-classic-creation`. Release: merging the sub-project PR publishes the next release (PR #70: merging is releasing).

## 1. Goal

Character creation should feel like classic Dark Age of Camelot:

- You pick a **base class**, such as Fighter, Mage, Viking or Guardian. You choose your full class at your trainer at level 5.
- You **place all 30 stat points yourself**. Nothing is assigned for you.
- The loading splash says **Hearth DAoC**.

### Non-goals
- Any server change. The server already accepts base classes, promotes at level 5 and enforces the classic 30-point rules. Prerequisite: the Disciple fix (#63, PR #64), so a client sending class 20 isn't refused.
- The 0.34b ("b", Sluaghbinder) edition. Its `game.dll` differs, and the applier refuses it with a clear message.
- Translations. The new texts are English only.
- Changing existing characters.

### Decisions (owner, 2026-10-06)

| Topic | Decision |
|---|---|
| Stat flow | Classic: start at race base stats with 30 points to place. No auto-assign. Creation can't be finished until all 30 are placed. |
| Class list | The classic base classes only, each with a description that names what it becomes at level 5. |
| Races per base class | No dead ends: only races that can become at least one of the class's full classes on this server. Classic and Shrouded Isles races only. |
| Splash | Re-letter OfflineDAoC's splash art to "HEARTH DAoC", keeping "Classic + Shrouded Isles", and credit OfflineDAoC. Claude can't paint new art. |
| Platforms | Linux and Windows. |
| Delivery | Approach A. The fork ships patch data and our own files, applied to each player's own client. EA files are never distributed. |

## 2. What players see

**Splash.** The 8 loading images show the re-lettered splash.

**Class step.** Only base classes are listed. Each description ends with the full classes it leads to on this server.

| Realm | Base class (id) | Becomes at level 5 | Races offered | Highlighted stats |
|---|---|---|---|---|
| Albion | Fighter (14) | Armsman, Mercenary, Paladin, Reaver | Briton, Avalonian, Highlander, Saracen, Inconnu | STR, CON, DEX |
| Albion | Elementalist (15) | Theurgist, Wizard | Briton, Avalonian | INT, DEX, QUI |
| Albion | Acolyte (16) | Cleric, Friar | Briton, Avalonian, Highlander | PIE, CON, DEX |
| Albion | Rogue (17) | Infiltrator, Minstrel, Scout | Briton, Highlander, Saracen, Inconnu | DEX, QUI, STR |
| Albion | Mage (18) | Cabalist, Sorcerer | Briton, Avalonian, Saracen, Inconnu | INT, DEX, QUI |
| Albion | Disciple (20) | Necromancer | Briton, Saracen, Inconnu | INT, DEX, QUI |
| Midgard | Viking (35) | Berserker, Savage, Skald, Thane, Warrior | Norseman, Troll, Dwarf, Kobold, Valkyn | STR, CON, DEX |
| Midgard | Mystic (36) | Bonedancer, Runemaster, Spiritmaster | Norseman, Troll, Dwarf, Kobold, Valkyn | PIE, DEX, QUI |
| Midgard | Seer (37) | Healer, Shaman | Norseman, Troll, Dwarf, Kobold | PIE, CON, DEX |
| Midgard | Rogue (38) | Hunter, Shadowblade | Norseman, Dwarf, Kobold, Valkyn | DEX, QUI, STR |
| Hibernia | Magician (51) | Eldritch, Enchanter, Mentalist | Celt, Elf, Lurikeen | INT, DEX, QUI |
| Hibernia | Guardian (52) | Blademaster, Champion, Hero | Celt, Firbolg, Elf, Lurikeen, Sylvan | STR, CON, DEX |
| Hibernia | Naturalist (53) | Bard, Druid, Warden | Celt, Firbolg, Sylvan | EMP, DEX, CON |
| Hibernia | Stalker (54) | Nightshade, Ranger | Celt, Elf, Lurikeen | DEX, QUI, STR |
| Hibernia | Forester (57) | Animist, Valewalker | Celt, Firbolg, Sylvan | INT, DEX, CON |

**How the table is derived.** The generator derives this table; it isn't typed by hand.
- **Base classes** are the server's starting base classes that aren't in the world's `disabled_classes` and that lead to at least one enabled full class.
- In the shipped classic world, `disabled_classes` is `20;33;34;39;58-62`. Disciple (20) is an upstream slip: Necromancer is a Shrouded Isles class (December 2002), and the setup tool's own label says it allows Classic and Shrouded Isles classes. HearthDAoC's `world_fixes.py` removes 20 at every start (#63, PR #64), which also adds the missing Saracen Disciple starting location. The rest disables Heretic, Valkyrie, Bainshee, Vampiir, Warlock and the Maulers, which matches 1.65 Classic + Shrouded Isles.
- **Races** for a base class are the union of its enabled full classes' `EligibleRaces`, limited to the 15 classic and Shrouded Isles races.
- **Highlighted stats** are the client's primary-stat highlights. With auto-assign gone they only colour the stats; they assign nothing.

**Race step.** Race buttons appear only for classic and Shrouded Isles races. Half Ogre, Frostalf, Shar and the Minotaurs are hidden.

**Stats.**
- Picking a race or class sets the race's base stats and **30 points to place**. The attributes window opens right away.
- The "Optimize" (auto) button is gone. Reset and the +/− buttons stay. Point costs are the classic ones: 1 per point up to +10, 2 up to +15, 3 above.
- **Continue** shows the client's own "You must use all your points!" message until all 30 are placed.

**Unchanged.**
- Bots.
- Existing characters.
- Unpatched clients, which still show the live screen. The server turns their final class into a base class through `start_as_base_class`.

**Sample description** (all 15 drafts are in the generator's data file and reviewed in the PR):
> Fighter. Albion's soldiers, trained in heavy armour and every kind of weapon. At level 5 your trainer makes you an Armsman, Mercenary, Paladin or Reaver.

## 3. Client changes (`game.dll`, classic 0.34, SHA-256 `67dcf68a…`)

All addresses and bytes come from the read-only investigation of 2026-10-06 and were re-checked against the shipped file. Everything here is creation code from the stock 1.127 client; OfflineDAoC's own changes to `game.dll` don't touch it.

| # | VA (file offset) | Original | New | Effect |
|---|---|---|---|---|
| P1 | `0x59C0B2` (`0x19C0B2`) | `8B 46 5C` | `EB 68 90` | Auto-assign (`0x59C086`) stops after its reset, leaving race base plus 30 unspent. This covers every caller: window open, race or class click, Optimize. |
| P2 | `0x59A853` (`0x19A853`), 28 bytes | `0F 85 93 02 00 00 80 BB 28 FA 00 00 00 0F 84 78 02 00 00 83 3D C8 BB 45 02 00 74 37` | `75 F6 83 3D C8 BB 45 02 00 75 11 80 BB 28 FA 00 00 00 0F 84 73 02 00 00 EB 39 90 90` | Customise-screen Continue checks unspent points for new characters too, using the existing "You must use all your points!" popup. |
| P3 | `0x59C574` (`0x19C574`) | `01` | `00` | The attributes dialog starts visible. |
| H | `0x5B0051` (`0x1B0051`) | `E8 36 43 00 00` | `E8 <rel32 to the cave>` | Hook after class registration. The cave makes the displaced call to `0x5B438C`. |
| S | new section `.hdcc` | (none) | our code and data (about 3 KB) | Appended after the last section, with section table, `SizeOfImage` and checksum updated. This is the same pattern as upstream's `patch_bot_map_client.py`. |

**What the cave does**, once per pregame open, with the registry in `ebx`:
1. Calls `0x5B438C`, the displaced call.
2. **Hides the final classes.** For each final class id in the list, it sets the class object's availability mask (`+0x30`) to 0. The list holds all 47 registered final classes, including the Hibernian Mauler/Sluaghbinder slot 62.
3. **Hides the later races.** It sets the race mask (`+0x54`) to 0 for races 16–21.
4. **Registers the base classes** through `0x5B01A3`, replaying a stock registration block: race vector via `0x5B4A65`, copy via `0x520E28`, free via `0x45BDC3`. Each base class gets its name id and string pointer reused from `.rdata`, expansion 0, trial 1, mask 7, gender 0, its new description (description id 0, so the built-in text is shown), its 3 highlighted stats and its race ids.

**Constraint.** All 16 class buttons and both gender buttons must stay in `character_creation.xml`, because the client reads them without a null check. Unused slots are hidden by the client.

**Pregame layout edit.** `character_customize_stats.xml` drops the Optimize `ButtonDef` (ControlId 1021). If in-game testing shows that removing it is unsafe, the fallback is to move it off-screen.

**Splash.** `pregame/splash.mpk` is replaced. It holds `splash1.tga`–`splash8.tga`, each a 1024×768 uncompressed 32-bit TGA with a bottom-left origin; RLE-compressed images render black. The source image is our re-lettered PNG.

## 4. Patch set and generator

Everything sub-project 2 adds to the client lives in `client/patches/`.

| Path | What |
|---|---|
| `classic-creation.json` | The patch set: per target file, its expected SHA-256 before and after, and the operations (below). Generated and committed. |
| `src/baseclass.asm` | Cave source (nasm, `-f bin`; the origin is filled in by the generator). |
| `src/base_classes.py` | Hand-written data: description text and highlighted stats per base class. |
| `build.py` | Generator. Reads the server's class files (`EligibleRaces`, base mapping), the edition's clean-world `disabled_classes` (fetched through `odaoc_fetch.py`, verified), `src/`, and the real `game.dll` and pregame files (fetched the same way). Builds the cave with nasm and writes `classic-creation.json`. Running it twice gives identical output. |
| `branding/splash.png` | Re-lettered splash (our work, crediting OfflineDAoC's art). |
| `branding/reletter_splash.py` | Re-lettering script (Pillow) from OfflineDAoC's splash, with the font's URL and SHA-256. The font is Cinzel, SIL Open Font License. |
| `splash.mpk` | Our splash archive, built once from `splash.png` with `branding/build_splash_mpk.py` and upstream's `OfflineDaoc.Mpk` tool, and committed. |
| `apply_patches.py` | Linux applier. |
| `../windows/patch-client.ps1`, `../windows/patch-client.bat` | Windows applier, and a double-clickable launcher that runs it with `-ExecutionPolicy Bypass`. |

**Operations** in `classic-creation.json`:
- `replace`: offset, original hex, new hex.
- `append`: hex added at the end of the file. Used for the new section's raw data; the header changes are `replace` operations.
- `text-replace`: an exact string replaced once.
- `file`: a whole file copied from the bundle. Used for the committed `splash.mpk`. Its expected result is that file's SHA-256, pinned like every other "after" hash. (The format also accepts "equals the bundled file", but a `splash.mpk` rebuilt for each release would carry new timestamps, so a new hash, and a client patched by one release would be unknown to the next.)

**Applier rules** (identical in Python and PowerShell):
1. **Already patched:** if a target's hash equals its "after" hash, skip it.
2. **Unknown file:** if it equals neither the "before" nor the "after" hash, refuse it and change nothing. Message: "This client file isn't the one this HearthDAoC release supports (e.g. the b edition or a newer upstream client); the stock screen stays." Exit code 3. The game remains playable.
3. **Otherwise:**
   - Back up the original once, as `<file>.hearthdaoc-orig`.
   - Apply the operations to a temporary copy and verify its "after" hash.
   - Replace the file atomically.
4. **`--restore` / `-Restore`:** put back every `.hearthdaoc-orig`, after verifying each backup's hash.
5. **Scope:** only paths inside the client folder, from a fixed list. Nothing outside it is touched.

**Delivery.**
- **Linux:** `setup.sh` runs `apply_patches.py` after fetching and verifying the client files. Re-running `setup.sh`, or `apply_patches.py --client ~/Games/HearthDAoC/client`, patches an existing client.
- **Windows:** the client bundle's `windows/` folder carries `patch-client.bat` and `patch-client.ps1`. The player puts them next to `connect-hearthdaoc.bat` and runs them once, and again after anything restores the original files.
- **Bundles:** the client bundle adds `patches/` (the patch set, `apply_patches.py`, `splash.mpk`). `deploy/build_bundles.sh` copies the committed `splash.mpk` and builds nothing when its SHA-256 isn't the one the patch set pins. It doesn't build `splash.mpk`: a rebuilt MPK carries new timestamps, so its hash cannot be pinned. CI's release job needs no .NET; the MPK tool is used only when the splash changes, before the commit.

## 5. Testing

**Automated, test-first, in CI:**
- **Appliers (Python and PowerShell, same cases):**
  - each operation type on fixture files;
  - "before" and "after" hash checks;
  - refusing an unknown file with exit 3 and nothing changed;
  - skipping an already-patched file;
  - backup once, restore, atomic write;
  - the path allow-list.
  - GitHub's Ubuntu runners include `pwsh`.
- **Generator:**
  - every base class leads to at least one enabled full class;
  - every offered race can become at least one of them;
  - only classic and Shrouded Isles races appear;
  - every base class has a description and three stats;
  - the output is reproducible: a rebuild equals the committed `classic-creation.json`.
- **Real files.** CI fetches the classic `game.dll` and pregame files from the pinned upstream release (verified, during the run only, never published), then checks:
  - applying the patch set gives the exact "after" hash;
  - the PE is still valid: sections, `SizeOfImage`, entry point and imports intact;
  - the hook disassembles to a call into `.hdcc`;
  - the edited XML still parses.

**In game, before release** (owner on Linux; a Windows friend later):
1. Each realm lists only its base classes, with descriptions. The splash says HEARTH DAoC.
2. Race buttons match each class. Later races are hidden.
3. Stats start at race base with 30 to place. Optimize is gone. Continue is blocked until 0 are left.
4. Create one character per race. Each is accepted and stored with the base class id and the placed stats (`hdc account list` and the database). This also confirms that client and server base stats agree.
5. Reach level 5 and train into a full class.
6. Customising an existing character from character select works (or the screen isn't reachable).
7. `apply_patches.py --restore` gives back the stock screen.

## 6. Risks and fallbacks

| Risk | Mitigation or fallback |
|---|---|
| A patch error crashes the client | Hashes are verified before and after. `--restore` undoes it. The release is gated on the in-game checks. |
| Customising an existing character hides a promoted character's class | The investigation couldn't confirm this one. Fallback: in the cave, clear the masks only when not in customise mode (`ctx+0xFA28`), or skip hiding when customising. |
| The points popup doesn't render on the customise screen | Fallback: keep P2's block but show the message through the existing name-error popup path. |
| P3 or the Optimize removal misbehaves | Both are optional: drop P3, or move Optimize off-screen. |
| Upstream ships a new client | Its hash won't match, so players get the stock screen. Regenerating the patch set joins the upstream-sync checklist (#50). |
| The owner changes `disabled_classes` later | The client list is generated for the shipped world. A newly disabled base class would be refused by the server at creation. Regenerating the patch set fixes it; `FORK.md` notes this. |
| On Windows, OfflineDAoC's launcher restores files | Re-run `patch-client.bat` (documented in `client/README.md`). |

## 7. Documentation and bookkeeping

- `docs/fork/FORK.md` gets a "Client patches" table: what is patched, where, why, and how to regenerate. The upstream-sync steps gain "rebuild the patch set".
- `client/README.md`: what the classic creation screen does, and the Windows patch step.
- The release notes are generated from the merged PRs (`--generate-notes`); the release's `--notes` text credits the splash art.
- Credit for the splash artwork goes to OfflineDAoC, in `client/README.md`, `FORK.md` and the release notes.

## 8. Sequence

1. Stat flow (P1–P3, Optimize), with the applier and patch-set format, in Python and PowerShell.
2. Base classes: the cave, the generator from server data, descriptions.
3. Splash.
4. Bundles, CI and docs.
5. In-game checks by the owner, then merge.

The investigation estimates about 2–3 days in total.
