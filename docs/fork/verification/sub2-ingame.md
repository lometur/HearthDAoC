# Sub-project 2: in-game verification (classic character creation)

Date: 2026-10-06. Tested by the owner on their Linux PC (Steam / Proton), with a normal player account.

- Client: OfflineDAoC 0.34 classic (`game.dll` SHA-256 `67dcf68a…`), patched from branch
  `sub2-classic-creation` at `90e8b3f` with `client/patches/apply_patches.py`. Patched `game.dll` `1f6869d4…`,
  `splash.mpk` `472bb9db…`.
- Server: HearthDAoC `v0.34b-hearth.7` on the owner's LAN server. The sub-project changes nothing on the server;
  it needs the Disciple fix from `v0.34b-hearth.5`.

| # | Check | Expected | Actual | Result |
|---|---|---|---|---|
| 1 | Splash and class list | HEARTH DAoC splash; Albion 6, Midgard 4, Hibernia 5 base classes, each ending "At level 5 your trainer makes you …" | Owner: "all looks good", no issues noticed | Pass |
| 2 | Races per class | Only the races of the class's full classes; Half Ogre, Frostalf, Shar and Minotaurs not shown | Owner: "all looks good", no issues noticed | Pass |
| 3 | Classic stat flow | Race base stats plus 30 points; attributes window open; no Optimize; Reset and +/− work; "You must use all your points!" until all 30 are placed | Owner: "all looks good", no issues noticed | Pass |
| 4 | Characters are accepted | Each race and base class pick is accepted by the server | Owner created characters with no refusal. Not itemised per race; every client race list is also pinned against the server's base-class `EligibleRaces` by `client/patches/tests/test_classdata.py` | Pass |
| 5 | Promotion | A base-class character trains into a full class at level 5 | Briton Fighter reached level 5 and became an Armsman | Pass |
| 6 | Customise an already-promoted character | Character select → customise does not crash | A level 10 character created before the patch was customised successfully | Pass |
| 7 | Second race-list window | The class/race builder is also called from another window (`game.dll` 0x5A53FA, a race list that includes "Horses"); note anything odd | Owner saw no such window and nothing odd | Not observed |
| 8 | Restore | `apply_patches.py --restore` brings back the stock files; patching again works | Restore printed `Restored:` for all three files; their SHA-256 values were the stock ones (`67dcf68a…`, `08e6f7c3…`, `f24460d2…`) and no backup remained; patching again printed `Patched:` ×3 and `--check` reported all three patched. The stock screen follows from the byte-identical originals; it was not re-checked in game | Pass |

No failures. Nothing goes back to the tasks or to the spec's section 6 fallbacks.
