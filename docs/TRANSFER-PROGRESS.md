# Move your progress to 0.35

You can bring everything from an older Offline DAoC into a new 0.35 folder:
- your **account**, so you log in with the same name
- your **characters**, with their levels, skills, quests and money
- their **items**, including inventory, equipment, bank and vault
- your **houses**
- your **guild's** earned state
- **all your bots**, with their levels, gear, money and progress

It works with **v0.3, v0.31, v0.31b, v0.32, v0.32b, v0.33, v0.33b, v0.34, v0.34b** and the **"new class test"**
builds.

The tool is **IMPORT PROGRESS FROM OLD OFFLINE DAOC.cmd**, and it's in every 0.35 folder.

## What's safe

- Your **old folder is only read**, never changed. You can keep playing it afterwards.
- The **new folder is backed up** before anything is replaced, into `runtime\progress-backups\`.
- **Every table is checked** after copying, row for row. If any check fails, nothing is installed.
- **Your new game version stays.** The new folder keeps its 0.35 world, rules and fixes. Only your
  saved progress comes across.
- **Rates and access reset:** XP goes back to 1× and GM access is turned off after an import.

## Step by step

1. **Install 0.35 first** into a new folder (see [PLAY.md](PLAY.md)). You don't need to start it.
2. **Close everything:** both launchers and both games. Stop both servers.
3. In the **new** folder, double-click **IMPORT PROGRESS FROM OLD OFFLINE DAOC.cmd**.
4. Click **Choose OLD folder…** and select your old Offline DAoC folder, the one containing
   `runtime`.
5. The tool shows what it found: the old version, and how many accounts, characters, bots and items
   there are.
6. Click **IMPORT PROGRESS** and confirm.
7. When it says the import completed, close the tool and start the new game with **START OFFLINE
   DAOC.cmd**.

Your login moves with you. The tool copies the old folder's `account.txt`, so ENTER REALM logs you
straight into your old account.

## Special cases

| Situation | What happens |
|---|---|
| The old folder has no `account.txt` | Your old account is kept under the same name with a **new password**, and the new `account.txt` is written for you. |
| The old save never had a player account (bots only) | The bots move over, and the new folder keeps its own fresh account. |
| You're importing into **0.35** (no custom class) from a save with a **Sluaghbinder character** | The import stops and nothing changes. Install **0.35b** and import there instead. |
| You're importing into **0.35** from a save with Sluaghbinder **bots** | The tool asks first. If you agree, every other bot comes across and the Sluaghbinder bots (with the items they carry) stay in the old folder. Choose 0.35b to keep them. |
| A character had an unfinished **0.33 bounty** | It becomes a normal bounty with the new kill count when that character logs in, and the kills so far are kept. The Bounty Master also offers **[update bounty]** once, free, to swap it for a new bounty at any difficulty. |
| The old save has something 0.35 doesn't use | It's listed in the import report instead of being dropped silently. |

## Undo an import

1. Close the game and stop the server.
2. Open the newest folder in `runtime\progress-backups\`.
3. Copy its `opendaoc.sqlite3.db` back into `runtime\data\`, and its `account.txt` (if there is
   one) back into `runtime\`.

Each import's `IMPORT RESULT.txt` in the same backup folder records what was transferred.

## For advanced users

The importer can also run without the window:

```bash
tools\ProgressImporter\OfflineDaoc.ProgressImport.exe --import "<old folder>" "<new folder>" --replace-progress [report.txt] [--leave-sluaghbinder-bots]
```

It writes a report and exits with code 0 on success. The saved-progress tables it moves are listed
in `tools\ProgressImporter\progress-policy.json`.
