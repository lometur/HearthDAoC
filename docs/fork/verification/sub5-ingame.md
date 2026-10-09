# Sub-project 5: in-game verification (classic battlegrounds 15–35)

> Note (2026-10-08): this checked v1 on the 0.34 world. Upstream 0.35 changed the central keeps of Abermenai
> and Murdaigean (section 7 of the [design](../specs/2026-10-07-classic-battlegrounds-design.md)); its
> in-game check still applies.

Date: 2026-10-07. Tested by the owner on the LAN server with the HearthDAoC client.

- Server: `v0.34b-hearth.12` (PR #77), installed with `./hdc update`. The battleground fix ran at the first start after the update.
- Client: the owner's patched classic client (sub-project 2).

## Server log of the first start

The owner's log showed:

- the summary line, `Classic battlegrounds: Abermenai 15-19 up to 1L2, Thidranki 20-24 up to 1L3, Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9`;
- one `Battlegrounds:` line for each of the six steps:
  - limits;
  - names and XP;
  - keep levels: Thidranki Faste base level 24, Caer Caledon base level 35, 4 gates' health;
  - portal keep guards: 34 each in Abermenai and Murdaigean;
  - central keeps: Dun Abermenai is keep 32 and Dun Murdaigean keep 33, each with 12 guards and both gates closed at full health;
  - Atlas leftovers: 15 training dummies, 3 Void Merchants and the stray Wizard archived in `fork_removed_mobs`;
- no `Classic battlegrounds: not applied` line and no `Could not find quest` line.

## In game

| # | Check | Expected | Actual | Result |
|---|---|---|---|---|
| 1 | Abermenai and Murdaigean | A character of the right level arrives beside its portal keep; the guards and hastener are there; `/ck` shows "Dun Abermenai: None" and "Dun Murdaigean: None" | Owner: "all looks good" | Pass |
| 2 | New central keeps | Casters on the walls, fighters at the gate, lord inside; the gate is closed and can be broken; guards about 21 / 31 and lord about 24 / 36; Dun Murdaigean's gate faces the Hibernia portal keep | Owner: "all looks good" | Pass |
| 3 | Capture | The keep goes to the killer's realm; the guards come back at the same levels; the gates are back at normal full health | Owner: "all looks good" | Pass |
| 4 | Thidranki | Level 20–24 gets in; guards about 26 and lord about 31; no dummies, Void Merchants or Pazz | Owner: "all looks good" | Pass |
| 5 | Caledonia | Level 30 gets in and level 36 is refused with the reason; `/who` shows Caledonia; guards about 37 and lord about 44 | Owner: "all looks good" | Pass |
| 6 | Over the limit | After `/quit` the character reaches the character screen and logs in at its bind point, and the porter refuses it; after a link death it is moved, with the message, at the next login; the same for a Midgard character | Owner: "all looks good" | Pass |
| 7 | Death | Release in a battleground goes to the bind point | Owner: "all looks good" | Pass |
| 8 | Realm point cap | With 350 or more realm points, the porter refuses Thidranki and explains why, once per ceremony | Not tested in game | Not verified |

The owner reported the checks together as "all looks good, but i did not verify rp cap". They were not itemised per check.

The realm point cap was not tested in game. The decision behind it is covered by `UT_ClassicBattlegrounds` in CI: the porter's destination and refusal text at each cap, and the over-the-limit rule. The owner chose to move on without the in-game check. No failure goes back to a task.

During testing, the owner found that `/door kill` throws a NullReferenceException on a keep gate. The command only handles ordinary doors (`GameDoor`) and leaves its door empty for a `GameKeepDoor`. This is an upstream bug that predates this sub-project. It is not tracked yet, by the owner's choice.
