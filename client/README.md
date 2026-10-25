> [!IMPORTANT]
> These scripts belong to **HearthDAoC (`lometur/HearthDAoC`), an unofficial fork** of
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC). They connect you to a private
> HearthDAoC server; for the official single-player game, use the upstream project.

# HearthDAoC player guide

This bundle connects your copy of the game client (OfflineDAoC's) to a HearthDAoC server, and gives
it a classic character creation screen, a window layout for new characters, and the Shrouded Isles
portals where they stood in the classic game.

## Before you start

Ask the server owner for two things: the **server address** (for example `192.168.1.64:10301`) and
the **edition** (`classic` or `b`). Your client must use the same edition as the server. Classic
character creation needs the `classic` edition.

Use a password you use nowhere else: the server stores passwords with a weak, unsalted hash, the
password appears on the game's command line, and it is saved on your PC as plain text
(`account.txt` on Linux, `hearthdaoc.cfg` on Windows).

## What you need

**Linux**
- Steam, with **Proton Experimental** installed (Steam → Library → Tools).
- `python3`, `rsync` and `curl` (on Ubuntu or Debian: `sudo apt install python3 rsync curl`).
  `play.sh` uses `curl` to look for new releases.
- `zenity`, if you want to type your login and answer update questions in a window when you start the
  game from Steam. Without it, you start the game from a terminal for those (see Linux quick start and
  Updating to a new release).
- A 1.127 client folder from the free [OpenDAoC installer](https://www.opendaoc.com/docs/client/).
  It is only read, never changed. You need room for a second copy of it. Keep it where it is: every
  update copies it again.

**Windows**
- The **official** OfflineDAoC release for the server's edition: for `classic`, upstream's
  `Download-OfflineDAoC-v0.37.zip` (on its v0.37 release page), and in it `GET-OFFLINE-DAOC-v0.37.cmd`; for `b`,
  `Download-OfflineDAoC-v0.37b.zip` and `GET-OFFLINE-DAOC-v0.37b.cmd`. Choose 2 (New install), or 1 to update
  an OfflineDAoC 0.36 install. Not the `c` edition: HearthDAoC doesn't support its new client. An OfflineDAoC 0.35
  install (upstream's `DOWNLOAD-AND-PLAY-v0.35.cmd` or `DOWNLOAD-AND-PLAY-v0.35b.cmd`) also works: the client
  files the classic creation and default layout patches change are the same in 0.35 and 0.37. Whether 0.35's
  zone files and Albion portal model are those of 0.36 and 0.37 hasn't been checked; if they aren't, the two
  Shrouded Isles portal patches change nothing, say "Not patched", and the game starts as before (see
  Troubleshooting).
- The Windows feature **.NET Framework 3.5 (includes .NET 2.0 and 3.0)**, turned on; the game's
  `connect.exe` needs it.

## Linux quick start (Steam / Proton)

1. Download `hearthdaoc-client-<version>.zip` from [HearthDAoC releases](https://github.com/lometur/HearthDAoC/releases)
   and unpack it. It creates a `hearthdaoc-client-<version>` folder that holds `setup.sh`. Open a
   terminal in that folder.
2. Run, with your server's address and edition:
   `./setup.sh --server 192.168.1.64:10301 --edition classic --base-client "/path/to/your/1.127 client"`
   This builds a separate client in `~/Games/HearthDAoC`. It downloads about 45 MB of OfflineDAoC
   files, each checked against the official release, installs HearthDAoC's patches in
   `~/Games/HearthDAoC/patches` and applies them. If it warns that the client was set up without
   one of HearthDAoC's patches, see Troubleshooting: the game still works. It saves these options in
   `~/Games/HearthDAoC/hearthdaoc-client.conf`, for updates.
3. In Steam: Games → **Add a Non-Steam Game** → Browse → `~/Games/HearthDAoC/play.sh`.
   Leave "Force the use of a specific Steam Play compatibility tool" **unchecked**. Any launch
   options must end with `%command%`.
4. Press Play. The first time, it asks for an account name (letters and digits) and a password (no
   spaces), in a window if `zenity` is installed. Without `zenity`, run `~/Games/HearthDAoC/play.sh`
   once from a terminal to enter them. While the server allows it, your first login creates the
   account. The first start also prepares Proton, which takes a little longer.

## Windows quick start

1. Install the official OfflineDAoC release for the server's edition, and turn on .NET Framework 3.5
   (see What you need).
2. Download `hearthdaoc-client-<version>.zip` from [HearthDAoC releases](https://github.com/lometur/HearthDAoC/releases)
   and unpack it.
3. Copy everything in its `windows` folder (`connect-hearthdaoc.bat`, `patch-client.bat`,
   `patch-client.ps1` and the `patches` folder) into your OfflineDAoC install's
   `runtime\client-opendaoc\app` folder, the one with `connect.exe`.
4. Double-click `connect-hearthdaoc.bat` to play. The first time, it asks for the server address,
   your account name and password, and saves them in `hearthdaoc.cfg` next to it. Passwords may not
   contain spaces, double quotes or `%`. While the server allows it, your first login creates the
   account.

There is nothing else to run: `connect-hearthdaoc.bat` applies HearthDAoC's patches itself. Don't
use OfflineDAoC's own launcher to play here; its "Enter Realm" only connects to your own PC.

## Classic character creation

With the `classic` edition, creating a character works as in classic Dark Age of Camelot:

- You pick a **base class**, such as Fighter, Mage, Viking or Guardian. Its description ends with
  the classes it leads to: at **level 5, your trainer makes you one of them**.
- Each base class offers only the races that can become one of those classes. Half Ogre, Frostalf,
  Shar and the Minotaurs aren't offered.
- You start at your race's base stats with **30 points to place** yourself. The attributes window
  opens right away, the Optimize button is gone, and Continue says "You must use all your points!"
  until all 30 are placed.
- The loading screens say **HEARTH DAoC**.

The patch changes only `game.dll` and two files in the client's `pregame` folder
(`character_customize_stats.xml` and `splash.mpk`), and keeps each original next to it as
`<file>.hearthdaoc-orig`. It changes nothing in any other client, such as the `b` edition: the game
then works with the standard creation screen. The default layout (below) is a patch of its own.

**Where you start.** A new character of a classic race, such as a Briton, Troll or Celt (not an
Inconnu, Valkyn, Sylvan, Half Ogre, Frostalf, Shar or Minotaur), first enters the world in its usual home village. A few seconds later, a
two-button question asks whether to begin your journey in your realm's Shrouded Isles town instead:
Caer Gothwaite, Aegirhamn or the Grove of Domnann. Accept takes you there and makes it your bind
point, where you return after a death; Decline keeps you at home. Each character is asked only once.
If the question goes away unanswered, for example because you logged out, it comes back at your next
login while the character is still level 1. The question comes from the server, not from the patch,
so it also appears with the standard creation screen, unless the server has turned it off.

## The window layout of new characters

When the game runs at 1920x1080 (the screen size in the game's options), a new character starts with the
window layout of the server owner's character Charles Martel:

- The first two quickbars are stacked at the top left; the third is hidden.
- The Main and Combat chat windows sit side by side along the bottom.
- The group window is at the bottom left and the character sheet at the top right. The portrait and
  vitals, the clock, the lag meter and the compass are together at the bottom right.
- The menu bar and the floating target window are hidden.

Its quickbars are empty, as before: hotkeys point at a character's own spells, styles and items. At
another screen size, the windows start where the game puts them. Every new character, at any screen
size, also starts with that character's name plate settings (its chat, camera and tooltip settings are
the game's own). Key bindings and graphics settings, which the game keeps for all your characters
together, don't change.

**Your characters keep their own layout.** The game saves a character's layout in its own settings file
(`<Name>-5.ini`) when the character leaves the world, and uses that file from then on; HearthDAoC never
changes it. So the characters you have keep their layout, and a new character keeps whatever you move
in its first session. Only a character without a settings file, a new one, starts with this layout. (A
new character with the first name of one you deleted gets the old character's file: the game doesn't
delete it.)

The patch adds the layout at the top of the client's `default.ini`, the file the game takes a new
character's layout from; the game's own layouts for every screen size stay below it. It keeps the
original as `default.ini.hearthdaoc-orig`. It is a patch of its own: the `b` edition gets it too, and
classic creation doesn't need it.

**To keep the game's standard layout for new characters,** put the original `default.ini` back and
remove the layout patch, so that the game stops applying it at each launch:

- **Linux:** in a terminal, run
  `python3 ~/Games/HearthDAoC/patches/apply_patches.py --client ~/Games/HearthDAoC/client --patchset ~/Games/HearthDAoC/patches/default-layout.json --restore`
  and then `rm ~/Games/HearthDAoC/patches/default-layout.json`.
- **Windows:** open a Command Prompt in `runtime\client-opendaoc\app` (see Going back to the standard
  creation screen) and run `patch-client.bat -PatchSet patches\default-layout.json -Restore`. Only when it
  printed `Restored: default.ini`, delete `default-layout.json` from the `patches` folder.

Updating to a new release brings the layout back; repeat these steps after the update.

## The Shrouded Isles portals

The portals to the Shrouded Isles stand where they stood in the classic game, before patch 1.74 (2005)
moved them outside the capitals, near Cotswold, Mularn and Mag Mell. Walk into the portal:

| Realm | The portal | It takes you to |
|---|---|---|
| Albion | the Avalon Marsh pier near Adribard's Retreat, /loc 44246,18014 | the Isle of Glass, by Caer Gothwaite's harbour |
| Midgard | Gotar, north-east of Nalliten, /loc 50635,33083 | Aegir's Landing |
| Hibernia | Shannon Estuary, across the lake from Connla, /loc 23136,30432 | the Grove of Domnann |

The portal on the Isles side takes you back to the same spot. The town teleporters still offer the
Shrouded Isles too. Near Cotswold, Mularn and Mag Mell, where OfflineDAoC put portals, there is
nothing any more. The in-game map still names a portal at those three spots: the map is the game's
own, and these patches don't change it.

The Albion portal (on the Isles side, at Caer Gothwaite) has its 2002 look, with purple flames, like
the Midgard and Hibernia ones. The look is the same for every player, and there is no setting for it.
If the server owner chooses the later (2006) look, a HearthDAoC release puts it back for everyone: on
Linux when you accept the update, on Windows at the first start after you copy the release's files.
There is nothing else to do.

These are two patches of their own, which every edition gets:

- **The portal fix** (`si-portals.json`) takes the six lines OfflineDAoC added for its town portals out
  of three zone files (`zones/Zone000/csv000.mpk`, `zones/zone100/csv100.mpk` and
  `zones/zone200/csv200.mpk`), and keeps each original as `<file>.hearthdaoc-orig`. The new files are
  larger (`csv000.mpk` goes from 25 KB to 91 KB): the changed parts are written without compression,
  the one way that gives the same bytes on Linux and Windows.
- **The portal look** (`si-portal-art.json`) moves `Newtowns/zones/Nifs/bavteleporter.npk`, the 2006
  model, aside as `bavteleporter.npk.hearthdaoc-orig`, so the game uses its own 2002 model.

The server doesn't need them: without them (a client from before them), you still see OfflineDAoC's
portals in the towns, but they take you nowhere; the portals above work all the same.

**To take one out,** put its originals back and remove it, so that the game stops applying it at each
launch (here the portal fix; for the portal look, use `si-portal-art.json`):

- **Linux:** in a terminal, run
  `python3 ~/Games/HearthDAoC/patches/apply_patches.py --client ~/Games/HearthDAoC/client --patchset ~/Games/HearthDAoC/patches/si-portals.json --restore`
  and then `rm ~/Games/HearthDAoC/patches/si-portals.json`.
- **Windows:** open a Command Prompt in `runtime\client-opendaoc\app` (see Going back to the standard
  creation screen) and run `patch-client.bat -PatchSet patches\si-portals.json -Restore`. Only when it
  printed `Restored:` for each file, delete `si-portals.json` from the `patches` folder.

Updating to a new release brings it back; repeat these steps after the update.

## Battlegrounds

The four battlegrounds work as in the classic game. Get the free **Battlegrounds Medallion of
Passage** from your realm's medallion merchant (Sall Fadri at Castle Sauvage, Gwulla at Svasud Faste,
Araisa at Druim Ligen), wear it in the Mythical slot and stand by the frontier porter (Master Visur,
Stor Gothi Annark or Glasny) when it casts, about every two minutes. The porter sends you to the
battleground for your level, beside your realm's portal keep there, and the medallion is used up:

| Battleground | Levels | Highest realm rank |
|---|---|---|
| Abermenai | 15 to 19 | 1L2 (under 125 realm points) |
| Thidranki | 20 to 24 | 1L3 (under 350) |
| Murdaigean | 25 to 29 | 1L5 (under 1,375) |
| Caledonia | 30 to 35 | 1L9 (under 7,125) |

If your level or realm points don't fit, the porter says why in the chat window and you keep the
medallion. Each battleground has a portal keep for each realm and a central keep with guards and a
lord; kill the lord to take the keep for your realm.

Going past a limit inside changes nothing at once: you may stay until you log out or die. You are then
at your bind point (after a logout, at your realm's home portal keep instead if your bind point is in a
battleground), and the porter won't send you back. A character left inside after a lost connection is
moved a moment after its next login, with a message saying why. No battleground has a zone experience
bonus.

## What happens at each launch

On Linux, `play.sh` first looks for a newer HearthDAoC release, and asks you before it updates (see
Updating to a new release).

Each time you start the game, `play.sh` (Linux) or `connect-hearthdaoc.bat` (Windows) checks
HearthDAoC's four patches just before the game starts, each on its own: classic creation, the default
layout, the Shrouded Isles portal fix, then the portal look.

- A client that is already patched is only checked; nothing is written.
- If something put an original file back (for example a repair of the install, or OfflineDAoC's own
  launcher), that file is patched again.
- When a patch doesn't support your client (classic creation with the `b` edition, for example), it
  changes nothing and the game starts without it, with the standard creation screen or the standard
  layout for new characters. `play.sh` says so in one line; `connect-hearthdaoc.bat` prints
  "Not patched: …". The other patches are still applied.
- When a release takes a patch back (for example the 2006 portal look, see The Shrouded Isles
  portals), the next start puts that patch's original files back, once.
- If a patch fails, you get a warning and the game **still starts**, maybe without that patch. On
  Linux, when you start from Steam, the warning also shows in a window (with `zenity`) or as a
  notification (with `notify-send`). On Windows, the window waits for a key before the game starts.
  See Troubleshooting.

Then the game starts and connects to the server.

## Updating to a new release

**Linux.** Each time you start the game, `play.sh` asks GitHub for the newest HearthDAoC release. When
it is newer than yours, it asks: "HearthDAoC <version> is out (you have <version>). Update the client
now?" From Steam, the question is a window (with `zenity`); in a terminal, answer `y` or `n`.
A release that changes only the server isn't offered: your client is already the same, and the game
just starts.

- **Yes** downloads the new `hearthdaoc-client-<version>.zip` (about 27 MB) and runs its `setup.sh`
  with the options you gave `setup.sh` the first time (saved in
  `~/Games/HearthDAoC/hearthdaoc-client.conf`). From Steam, a window shows its progress. It rebuilds
  `~/Games/HearthDAoC/client` (about 45 MB more to download), replaces `~/Games/HearthDAoC/patches`
  with the new release's patches, applies them and writes a new `play.sh`, which then starts the
  game. It builds the new client next to the old one and swaps it in only when everything worked.
  Your saved login and your Steam shortcut stay.
- **No** starts the game as it is. The next start asks again.
- If the update fails, you get a warning that says what failed (in a window, from Steam), and the
  game starts with the release you have, unchanged. The next start offers the update again. See
  Troubleshooting.
- Offline, or when GitHub doesn't answer within 5 seconds, the game just starts.
- Without a terminal and without `zenity`, `play.sh` can't ask. It warns that an update is out, and
  starts the game. Run `~/Games/HearthDAoC/play.sh` from a terminal to answer.
- To turn the check off, start the game with `HEARTHDAOC_NO_UPDATE=1`: in Steam, set the game's
  launch options to `HEARTHDAOC_NO_UPDATE=1 %command%`; in a terminal, run
  `HEARTHDAOC_NO_UPDATE=1 ~/Games/HearthDAoC/play.sh`.

A client set up by an older bundle doesn't look for updates: run `setup.sh` from this bundle (or a
newer one) once more, with the same options as before. From then on, `play.sh` offers each new
release. You can always update by hand the same way: download and unpack the new zip, and run its
`setup.sh` with the same options as before.

**Windows.** Unpack the new zip and copy everything in its `windows` folder into
`runtime\client-opendaoc\app` again, replacing the old files. Your `hearthdaoc.cfg` stays. The next
start of `connect-hearthdaoc.bat` applies the new release's patches.

If you went back to the standard creation screen (below), or took out the layout for new characters or
a Shrouded Isles portal patch (above), updating turns them on again: repeat those steps after the
update.

## Going back to the standard creation screen

Put the original files back, then remove the classic creation patch so that the game stops applying
it at each launch. The default layout for new characters (above) stays.

- **Linux:** in a terminal, run
  `python3 ~/Games/HearthDAoC/patches/apply_patches.py --client ~/Games/HearthDAoC/client --restore`
  and then `rm ~/Games/HearthDAoC/patches/classic-creation.json`. To get classic creation back, run
  `setup.sh` again.
- **Windows:** open a Command Prompt in `runtime\client-opendaoc\app` (in File Explorer, type `cmd`
  in that folder's address bar and press Enter) and run
  `patch-client.bat -PatchSet patches\classic-creation.json -Restore`. If your OfflineDAoC is under
  `C:\Program Files`, open the Command Prompt as administrator instead (Start, type `cmd`, choose
  **Run as administrator**, then `cd /d "<your app folder>"`). Only when it printed `Restored:` for
  each file, delete `classic-creation.json` from the `patches` folder. To get classic creation back,
  copy it from the bundle's `windows\patches` folder again.

To put back the originals of all four patches at once, run `patch-client.bat -Restore` on Windows; on
Linux, run the restore command once for each patch (with each `--patchset`). The next start applies
again every patch whose file is still in the `patches` folder.

The restore puts an original back only over the patched file. If a file has changed since it was
patched (for example a newer client was installed), it says so and restores nothing.

If you set up the Linux client with `--dest`, use that folder instead of `~/Games/HearthDAoC`.

## Troubleshooting

**"Not patched", or the standard creation screen.** HearthDAoC's classic creation patches support
only the OfflineDAoC `classic` client of 0.35 to 0.37 (the same files). Any other client, such as the
`b` edition or an older or newer upstream client, keeps the standard creation screen and plays normally.

**"Not patched: default.ini", or new characters with the standard layout.** The default layout
supports the `default.ini` of both OfflineDAoC editions of 0.35 to 0.37. A `default.ini` changed by hand, or an
older or newer upstream client's, isn't patched: new characters start with that file's layout, and the
game plays normally. To get the layout, put OfflineDAoC's file back (on Linux, run `setup.sh` again).

**"Not patched: zones/…/csv000.mpk" (or `csv100.mpk`, `csv200.mpk`, `bavteleporter.npk`), or portal
art still near Cotswold, Mularn or Mag Mell.** The Shrouded Isles portal patches support the zone files
and Albion portal model of OfflineDAoC 0.36 and 0.37 (both editions; 0.35's haven't been compared).
Another client's files aren't changed, and the game plays normally: the town portal art stays but takes
you nowhere, and the portals at the classic spots work anyway. To get the patches, put OfflineDAoC's
files back (on Linux, run `setup.sh` again).

**Windows: a warning at every start that one of HearthDAoC's patches could not be applied.** Usually
Windows doesn't let you change files in your OfflineDAoC folder, for example because it is under
`C:\Program Files`. Right-click `patch-client.bat` and choose **Run as administrator**, once. Later
starts only check the patched files, which needs no administrator. If the warning comes back after
something put the original files back, do it again. Simpler in the long run: install OfflineDAoC in a
folder you own, such as `C:\Games\OfflineDAoC`, where nothing needs administrator.

**Linux: a warning that one of HearthDAoC's patches could not be applied.** Run
`~/Games/HearthDAoC/play.sh` in a terminal to see the message. Running `setup.sh` again from the bundle installs the patches
again.

**Linux: the update failed.** The warning says what failed; run `~/Games/HearthDAoC/play.sh` in a
terminal to see all of the output. The game started with the release you have, and the next start
offers the update again. Often the internet connection dropped, or your 1.127 client folder (the
`--base-client` you gave `setup.sh`) was moved or deleted: every update copies it again. If it moved,
download the new release's zip and run its `setup.sh` by hand with the new `--base-client` path; the
next updates use that one.

**Wrong account or password saved.** Delete `~/Games/HearthDAoC/account.txt` (Linux) or
`hearthdaoc.cfg` next to `connect-hearthdaoc.bat` (Windows), and start the game again: it asks
again.

**Character keeps running on its own (Linux).** On Proton 10 and later, a movement key can get
"stuck" after zoning or certain key presses. Workarounds: remove the **Run Lock 2** key binding
(NumLock) in the game's keyboard options, and set the Steam launch options of the game to
`XMODIFIERS=@im=none %command%`. Tapping W once releases it.

## Credits

The loading splash is OfflineDAoC's artwork ([shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC)),
re-lettered "HEARTH DAoC" in the Cinzel font (SIL Open Font License). Credit for the art goes to
OfflineDAoC.
