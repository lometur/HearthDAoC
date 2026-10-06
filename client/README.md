> [!IMPORTANT]
> These scripts belong to **HearthDAoC (`lometur/HearthDAoC`), an unofficial fork** of
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC). They connect you to a private
> HearthDAoC server; for the official single-player game, use the upstream project.

# Playing on the central server

Ask the server owner for two things first: the **server address** (for example `192.168.1.64:10301`)
and the **edition** (`classic` or `b`). Your client must use the same edition as the server.

Use a password you use nowhere else: the server stores passwords with a weak, unsalted hash, and the
password appears on the game's command line.

## Classic character creation

HearthDAoC patches your own copy of the client so that creating a character works as in classic Dark
Age of Camelot. This needs the `classic` edition (OfflineDAoC 0.34 classic).

- You pick a **base class**, such as Fighter, Mage, Viking or Guardian. Its description names the
  classes it leads to; your trainer makes you one of them at level 5.
- Each base class offers only the races that can become one of those classes. Half Ogre, Frostalf,
  Shar and the Minotaurs aren't offered.
- You start at your race's base stats with **30 points to place** yourself. The Optimize button is
  gone, and Continue says "You must use all your points!" until all 30 are placed.
- The loading screens say **HEARTH DAoC**.

The patch changes only `game.dll` and two files in `pregame`, and keeps each original as
`<file>.hearthdaoc-orig`. With any other client, such as the `b` edition, it changes nothing and
says so. The game then works with the standard creation screen.

## Linux (Steam / Proton)

You need Steam with **Proton Experimental**, plus `python3` and `rsync`, and a 1.127 client folder
from the free [OpenDAoC installer](https://www.opendaoc.com/docs/client/) (it is only read).

1. Download `hearthdaoc-client-<version>.zip` from [HearthDAoC releases](https://github.com/lometur/HearthDAoC/releases) and unpack it;
   it creates a `hearthdaoc-client-<version>` folder that holds `setup.sh`. Run the next command in that folder.
2. Run:
   `./setup.sh --server 192.168.1.64:10301 --edition classic --base-client "/path/to/your/1.127 client"`
   This builds a separate client in `~/Games/HearthDAoC`, downloads about 45 MB of OfflineDAoC
   files, each checked against the official release, and applies HearthDAoC's client patches (see
   Classic character creation). If it warns that the client was set up without HearthDAoC's patches,
   your client isn't the one this release supports (for example the `b` edition); the game still
   works, with the standard creation screen.
3. Steam → Games → **Add a Non-Steam Game** → Browse → `~/Games/HearthDAoC/play.sh`.
   Leave "Force the use of a specific Steam Play compatibility tool" **unchecked**. Any launch
   options must end with `%command%`.
4. Press Play. The first time, it asks for an account name and password. While the server allows it,
   your first login creates the account.

To go back to the standard creation screen, run
`python3 patches/apply_patches.py --client ~/Games/HearthDAoC/client --restore` in the
`hearthdaoc-client-<version>` folder. The same command without `--restore`, or running `setup.sh`
again, patches the client again.

## Windows

1. Install the **official** OfflineDAoC release for the server's edition (upstream's
   `DOWNLOAD-AND-PLAY-v0.34.cmd` for `classic`, `DOWNLOAD-AND-PLAY-v0.34b.cmd` for `b`).
2. Turn on the Windows feature **.NET Framework 3.5 (includes .NET 2.0 and 3.0)**; the game's
   `connect.exe` needs it.
3. Copy everything in the bundle's `windows` folder into the install's `runtime\client-opendaoc\app`
   folder: `connect-hearthdaoc.bat`, `patch-client.bat`, `patch-client.ps1` and the `patches` folder.
4. Double-click `patch-client.bat` for the classic character creation screen and the HEARTH DAoC
   loading screens (`classic` edition). It says what it patched and waits for a key. Run it again
   whenever something puts the original files back (OfflineDAoC's own launcher or a repair of the
   install may). To go back to the standard screen, open a Command Prompt in that folder and run
   `patch-client.bat -Restore`.
5. Run `connect-hearthdaoc.bat`.
   It asks for the server address, account and password once and saves them in `hearthdaoc.cfg`.
   Passwords may not contain spaces, double quotes or `%`.
   Don't use OfflineDAoC's own launcher for this; its "Enter Realm" only connects to your own PC.

## Character keeps running on its own (Linux)

On Proton 10 and later, a movement key can get "stuck" after zoning or certain key presses.
Workarounds: remove the **Run Lock 2** key binding (NumLock) in the game's keyboard options, and set
the Steam launch options of the game to `XMODIFIERS=@im=none %command%`. Tapping W once releases it.

## Credits

The loading splash is OfflineDAoC's artwork ([shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC)),
re-lettered "HEARTH DAoC" in the Cinzel font (SIL Open Font License). Credit for the art goes to
OfflineDAoC.
