# Sub-project 3: Shrouded Isles start choice (design)

Status: approved by the owner. Date: 2026-10-06. Fork: `lometur/HearthDAoC`.
Issue: #40 (start-location choice). Branch: `sub3-si-start-choice`. Release: merging the PR
publishes the next release (PR #70); the server picks it up with `./hdc update`.

## 1. Goal

A new character of a classic race chooses where its journey begins: its usual home village, as today,
or its realm's Shrouded Isles town. This was possible in the Shrouded Isles era that HearthDAoC
emulates. The owner asked for "the starting place selection as it was in SI".

### Decisions (owner, 2026-10-06)

| Topic | Decision |
|---|---|
| What can be chosen | Home or Shrouded Isles. A classic race (Briton, Avalonian, Highlander, Saracen, Norseman, Troll, Dwarf, Kobold, Celt, Firbolg, Elf, Lurikeen) chooses between its usual home village and its realm's SI town: Caer Gothwaite, Aegirhamn or the Grove of Domnann. The SI races (Inconnu, Valkyn, Sylvan) start in the Shrouded Isles as today, with no question. |
| How it is chosen | A two-button question from the server (the client's own accept/decline dialog) on the character's first entry into the world. No client patch. |
| Not chosen | Reviving the client's dormant two-panel "starting location" screen (option A), or a toggle on the customise screen (option C). Both need a large new `game.dll` patch, would work for patched clients only, and need the appliers to upgrade clients patched by sub-project 2. |
| Who is affected | Only new characters, as defined in section 2. Existing characters keep where they are. Bots keep upstream's spread of starting places. |
| Default | On for HearthDAoC, with a setting to turn it off. |

### Non-goals

- A free choice among all of a realm's towns.
- Reviving the client's starting-location screen. That remains possible later as a cosmetic upgrade, and #40 keeps the notes.
- Moving existing characters, or changing where the SI races or the bots start.
- Repairing the unrelated start-table oddities the investigation found: Sylvan Magician row #553 is about 19,000 units from Domnann, and row #124 is an impossible Sylvan Paladin in Albion. These are left for their own issue.

## 2. What players see

1. A new character is created as today and enters the world in its usual home village. It is placed
   by race and base class, as now.
2. If the character qualifies, a few seconds after it has finished loading, the client's two-button
   dialog asks, for its realm (the text names no button, because the client labels them itself;
   probably Accept and Decline):
   - Albion: "Begin your journey in the Shrouded Isles, at Caer Gothwaite? Decline to stay here."
   - Midgard: "Begin your journey in the Shrouded Isles, at Aegirhamn? Decline to stay here."
   - Hibernia: "Begin your journey in the Shrouded Isles, at the Grove of Domnann? Decline to stay here."

   Texts stay under 100 characters, plain CP1252, with no line break (the client wraps them).
3. **Accept** moves the character to that town and binds it there, so death and `/release` bring it
   back there. The town has a trainer for every base class of its realm. The character is told where
   it is.
4. **Decline** changes nothing. The character stays home, bound there as today.
5. The answer is saved for the character, and the question is never asked again. If the dialog goes
   away without the player's answer, the question comes back at the next login, as long as the
   character is still level 1. That covers logging out, the server replacing the dialog with another
   one (section 3), a failed move, or the character being dead when it answers. Once the character
   reaches level 2, the question is gone for good. Leaving to the character screen and entering again
   counts as a new login.

**A character qualifies when all of these hold:**

- the setting is on;
- the character is level 1;
- its race is a classic race, ids 1-12. That excludes Inconnu (13), Valkyn (14) and Sylvan (15), and races 16-21, which a GM could still create.
- it isn't already in its realm's Shrouded Isles: region 51 (Albion's), 151 (Midgard's) or 181 (Hibernia's). That covers, for example, Saracen Disciples, which the world fixes start in Caer Gothwaite, and Sluaghbinder novices when that class is enabled.
- it has no saved answer;
- the destination for its realm is known (section 3).

## 3. How it works

### Server: one new script plus a small decision class

Everything is in new fork-owned files. No upstream file changes.

- **Decision logic** (`SiStartChoice`, a plain class that unit tests can drive without a server):
  - It takes the setting, level, race, realm, current region, saved answer, whether the player is alive, and the destinations in through parameters. It never reads `GameServer`, `WorldMgr` or `ServerProperties` itself, so tests need no running server.
  - It decides whether to ask, and holds the per-realm destination names and texts.
  - It turns a response into an outcome:
    - **accept**: response `0x01` while the dialog is no longer pending. The outcome is move, rebind, then save `yes`.
    - **decline**: any other response from the player. The outcome is save `no`.
    - **superseded**: the callback runs while our dialog is still the player's pending one. The server does this with `0x00` when it sends another dialog (`PacketLib168.SendCustomDialog`), and a real click clears the pending callback first (`DialogResponseHandler`). The outcome is that nothing is saved.
    - **not answerable now**: the character is dead, or no longer qualifies. Nothing is saved.
- **Event wiring** (a game-event script in `source/server/GameServer/scripts/`, registered like `BountyMasterRuntime`):
  - **Subscribing:** a `[ScriptLoadedEvent]` method adds a handler for `GamePlayerEvent.GameEntered` with `GameEventMgr.AddHandler`. That event fires once per login, in `PlayerInitRequestHandler`. Only a `GamePlayer` sender is handled; bots are `GameBot`, a `GameNPC`.
  - **Delay:** when the character qualifies, it waits about two seconds with an `ECSGameTimer` (five at first; the owner found that slow in game), as `LostStoneOfArawn` does. The delay is there because `GameEntered` fires before the server sends "player init finished", the patch-notes window and the starter help.
  - **When the timer fires:** it sends nothing unless the player is still active and playing on the same client, and still qualifies. PlayerInit can still move a player after `GameEntered`.
  - **Asking:** it then asks with `Out.SendCustomDialog(text, callback)`.
  - **Callback:** it checks qualification again before acting.
- **Moving and binding** (on accept):
  - **Move:** the character is moved with `MoveTo(region, x, y, z, heading)`.
  - **Bind:** its bind point is set from the destination values (`BindRegion`, `BindXpos`, `BindYpos`, `BindZpos`, `BindHeading`), as `GamePlayer.Bind()` does.
  - **Save:** the character is saved at once with `GameServer.Database.SaveObject(player.DBCharacter)`.
  - **Failed move:** if `MoveTo` fails, nothing is bound or saved, the player is told, and the question comes back at the next login.
- **Saving the answer:**
  - **Where:** the answer goes in the per-character custom parameter table (`DbCoreCharacterXCustomParam`) under the key `hearthdaoc_si_start`, with the value `yes` or `no`.
  - **When:** it is written at once, after the move, rebind and save on accept, following the existing pattern in `BountyQuest.RememberCompletedTarget`.
  - **How:** select the row by character id and key, then add a new row or update the existing one. There is only one write path, so there are never two rows. The answer is read the same way, by selecting.
  - **Unchanged:** no new table, no schema change, no change to character creation.
- **Destinations:** at script load, the server reads the realm's SI town arrival point from the world's `Teleport` rows. These are the rows the SI towns' own teleporters use (`AlbionSITeleporter`, `MidgardSITeleporter`, `HiberniaSITeleporter`). The lookup is `WorldMgr.GetTeleportLocation(realm, ":" + TeleportID)`: Type is empty and the key is case-sensitive.

  | Realm | Destination | Teleport row | Region | X | Y | Z | Heading |
  |---|---|---|---|---|---|---|---|
  | Albion | Caer Gothwaite | `Caer Gothwaite`, realm 1 | 51 | 535518 | 547214 | 4800 | 2105 |
  | Midgard | Aegirhamn | `Aegirhamn`, realm 2 | 151 | 293910 | 356255 | 3488 | 1199 |
  | Hibernia | Grove of Domnann | `Grove of Domnann`, realm 3 | 181 | 423187 | 440300 | 5952 | 3866 |

  If a realm's row is missing, that realm's characters are not asked, and the server logs one warning at load.
- **Setting:** a bool server property `si_start_choice` in the `server` category.
  - **Declaration:** it is declared in the new script file with `[ServerProperty]`, as upstream's startup scripts declare theirs (for example `start_as_base_class` in `StartAsBaseClass.cs`).
  - **Code default:** off, so the script is neutral and can be offered upstream.
  - **HearthDAoC default:** on, through `.env`: `HEARTHDAOC_SI_START_CHOICE` (`on` or `off`, case ignored, default `on`).
  - **Writing it:** `deploy/bin/server_properties.py` writes it before every start as exactly `True` or `False`, with the property's own description. It does this like `HEARTHDAOC_GM_ONLY_COMMANDS` and `command_plvl_overrides`. Any other value is refused with exit 2.
  - **Docs:** `compose.yml`, `.env.example` and HANDOFF list the setting.
- **Starting level:** if the world's `starting_level` is raised above 1, new characters are not level 1 and nobody is asked. HearthDAoC's worlds use 1.

### Effect on existing players

- **Existing characters** above level 1 are untouched, and a level-1 character that already left town is still offered the choice.
- **Characters created before the update that are still level 1** get the question at their next login, which is the intended choice.
- **Clients:** nothing changes on the client.

## 4. Testing

- **Server unit tests** (C#, `UT_SiStartChoice`, like `UT_CommandPrivLevelOverrides`). The CI step "Server unit tests for the fork's server changes" gets its `--filter` extended to `FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice`, and `deploy/tests/test_workflows.py` pins that filter. For `SiStartChoice`, these cases:
  - asked for a level-1 classic race in its home region;
  - not asked when:
    - the setting is off;
    - the character is level 2 or above;
    - it is an SI race, or race 16-21;
    - it is inside an SI region;
    - it has a saved `yes` or `no`;
    - the realm has no destination;
  - accept yields the realm's destination, then saves `yes`;
  - decline saves `no` and does not move;
  - a superseded dialog saves nothing;
  - an answer while dead saves nothing;
  - qualification is checked again when the timer fires and when the callback runs.
- **Deploy tests** (Python) for `server_properties.py`:
  - `on` and `off`, in any case, map to exactly `True` and `False`, written with the property's own description;
  - a bad value exits 2;
  - the value is only written when it changes;
  - the entrypoint passes the setting.
- **Real world data:** with `HDC_TEST_WORLD`, the three Teleport rows exist with exactly the TeleportIDs, realms and values in section 3, with an empty Type and no duplicates.
- **In game:** the owner checks, recorded in `docs/fork/verification/sub3-ingame.md`:
  - a new Briton accepts, arrives in Caer Gothwaite, and is bound there (`/release` after a death, or the bind stone);
  - a new Troll declines and stays home;
  - an Inconnu gets no question;
  - logging out with the dialog open re-asks at the next login;
  - note the button labels; check that the question is visible next to the patch-notes and starter-help windows; check what pressing Escape does;
  - with `HEARTHDAOC_SI_START_CHOICE=off`, nobody is asked.

## 5. Documentation and bookkeeping

- **`docs/fork/FORK.md`:** add a row to the server-code changes table. It gives the new files, the reason and "upstream candidate: off by default". The upstream files touched are none.
- **`client/README.md`** (player guide): one paragraph in the classic creation section about the question on first entry.
- **`deploy/HANDOFF.md` and `.env.example`:** the new setting.
- **Issue #40:** closed when the PR is merged, with a note that the server-side question was chosen over the client screen, and where the client-screen notes live.
- **Upstream:** a candidate under the upstream contribution policy, as a small PR of the script, the decision class and the property (off by default), once the owner wants to send the next upstream PR.

## 6. Risks

| Risk | Handling |
|---|---|
| The dialog arrives before the client can show it | It is sent after a short delay once the player has entered the game. The in-game check confirms it. If needed, the delay is a single constant. |
| The player is moved while still loading or zoning | The move happens only in the dialog callback, after the player clicks. |
| A destination row changes in a future world data update | It is read from the data at load. A missing row turns the question off for that realm, with a warning, and the real-world test pins the rows. |
| An upstream sync changes `PlayerInitRequestHandler` or the event | Only public upstream API is used: the "game entered" event, `SendCustomDialog`, `MoveTo`, `WorldMgr.GetTeleportLocation` and the custom-parameter table. No upstream file is edited. |
| Another dialog replaces ours | Recognised as "superseded", so nothing is saved and the question comes back. A click on a replaced dialog goes to whichever callback is pending (upstream behaviour). |
| A crash between the answer and the move | On accept, the move, the rebind and the character save happen before the answer is written. |
