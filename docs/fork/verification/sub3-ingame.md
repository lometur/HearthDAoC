# Sub-project 3: in-game verification (Shrouded Isles start choice)

Date: 2026-10-07. Tested by the owner on the LAN server with the HearthDAoC client.

- Server: `v0.34b-hearth.10` (PR #74), installed with `./hdc update`; `HEARTHDAOC_SI_START_CHOICE` at its default, `on`.
- Client: the owner's patched classic client (sub-project 2).

| # | Check | Expected | Actual | Result |
|---|---|---|---|---|
| 1 | Accept | A new Briton is asked a few seconds after entering, accepts, arrives in Caer Gothwaite and is bound there | Owner: "accepting brings you to si with a proper bind" | Pass |
| 2 | Decline | A new Troll declines and stays home; it is not asked again | Owner: "declining stays" | Pass |
| 3 | SI race | An Inconnu is never asked | Owner: "inconnu didn't ask" | Pass |
| 4 | Logout with the question open | It is asked again at the next login | Owner: "logging out with the question up brings it back up" | Pass |
| 5 | Timing | The question appears soon after entering | Owner: works, but wished it appeared sooner than the 5-second delay; shortened to 2 seconds in the follow-up PR | Pass, changed |

Not separately reported: the button labels, the server's start-up line, and the setting turned off. The owner summarised the rest as "all of it seems to work … working as intended". No failure goes back to a task.
