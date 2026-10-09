# Sub-project 4: in-game test guide (the epic chains)

For the owner, after the release with sub-project 4 is installed (`./hdc update`). The world fix runs at the first
start: the server log has a line starting `Epic chains: Guild of Shadows 60 links`.

## GM commands used

| Command | Does |
|---|---|
| `/epic` | Lists your target's (or your own) epic chain and each step's state. |
| `/epic done <level>` | Marks every step below `<level>` finished (the classic 7 and 11 unless a Shrouded Isles version is finished). |
| `/epic goto` | Jumps to the current stage's map marker, or to the next step's giver. |
| `/epic reset` | Removes every step of the chain, active and finished. |
| `/player level <n>` | Sets your target's level (target yourself to level yourself). |
| `/jump to <x> <y> <z> <region>` | Teleports you to a spot (the spec's coordinates are zone-local; `/epic goto` is easier). |
| `/item create <template id>` | Makes an item, to check a reward's stats. |

Use a character of one of the five classes: Infiltrator, Mercenary, Cabalist, Necromancer or Reaver.

## 1. One class through the whole chain

1. `/player level 7`, then `/epic`: 7 and 7 SI say "can take", Lady Aelawen's Supply Run "offered to no one".
2. Take the classic 7 from your Camelot trainer (Master Edric, Master Arenis, Magus Isen, Yulia or Peze). `/epic`:
   7 SI is now "closed by …". Play it through, or `/epic goto` from stage to stage. At the end you get your
   reward, 5,500 XP and 7 silver.
3. For each later step: `/player level <step>`, `/epic goto` to the giver, take it, play it (or skip ahead with
   `/epic done <next step>`), and check the reward, the XP and the coin. Spot checks:
   - 15 is offered only after 11; 30 only after 25; 40 only after 30.
   - At 40, Rhodri's list has only weapons that exist; each one you whisper is given.
   - 45 only after 43, and 48 only after 45.
4. At 50: Captain Rhodri offers "Lord of Deceit". Lord Elidyn stands in the Ellyll ruins on the hill in the Pennine
   Mountains with his guards (the map marks the spot). Kill him (a group fight), go back to Rhodri with six free
   backpack slots: six armour pieces and 50 silver. With fewer free slots he says so and waits.
5. `/epic`: every step "finished". `/epic reset` clears it.

## 2. The Shrouded Isles route

On a new character (or after `/epic reset`): level 7, take Strange Beings from Carys in Caer Gothwaite. The classic 7
is closed. At 11, Shades and Shadows from Carys, then 15 from your Camelot trainer.

## 3. Other checks

- With `/xp off`, a quest that gives XP can't be finished (upstream's rule): "Your XP is turned off, you must turn it
  on to complete this quest!".
- Another line: a Defenders of Albion character (Armsman, Scout, Friar or Theurgist) at 48 with only 43 finished
  isn't offered "Feast of the Decadent" 48 (`/epic` lists the 45's quest numbers it waits for).
- `/item create` each class's new rewards (the `cq_alb_…` ids in `deploy/bin/epic_chains_data.json`) and check the
  stats against `docs/fork/specs/2026-10-09-epic-chains-design.md`, section 4.
- Class locks: game masters skip them, so use a player account. Give a Mercenary an Infiltrator's reward (for example
  drop the Ring of Shades and pick it up with the Mercenary) and try to wear it: "Your class cannot use this item!".

Results go in `docs/fork/verification/sub4-ingame.md`.
