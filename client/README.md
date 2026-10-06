> [!IMPORTANT]
> These scripts belong to **HearthDAoC (`lometur/HearthDAoC`), an unofficial fork** of
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC). They connect you to a private
> HearthDAoC server; for the official single-player game, use the upstream project.

# HearthDAoC player guide

This bundle connects your copy of the game client (OfflineDAoC's) to a HearthDAoC server, and gives
it a classic character creation screen.

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
- `python3` and `rsync` (on Ubuntu or Debian: `sudo apt install python3 rsync`).
- `zenity`, if you want to type your login in a window when you start the game from Steam. Without
  it, you start the game once from a terminal (see Linux quick start).
- A 1.127 client folder from the free [OpenDAoC installer](https://www.opendaoc.com/docs/client/).
  It is only read, never changed. You need room for a second copy of it.

**Windows**
- The **official** OfflineDAoC release for the server's edition: upstream's
  `DOWNLOAD-AND-PLAY-v0.34.cmd` for `classic`, `DOWNLOAD-AND-PLAY-v0.34b.cmd` for `b`.
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
   HearthDAoC's patches, see Troubleshooting: the game still works.
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
then works with the standard creation screen.

## What happens at each launch

Each time you start the game, `play.sh` (Linux) or `connect-hearthdaoc.bat` (Windows) checks
HearthDAoC's patches just before the game starts:

- A client that is already patched is only checked; nothing is written.
- If something put an original file back (for example a repair of the install, or OfflineDAoC's own
  launcher), that file is patched again.
- With a client the patches don't support (for example the `b` edition), nothing is changed and the
  game starts with the standard creation screen. `play.sh` says so in one line;
  `connect-hearthdaoc.bat` prints "Not patched: …".
- If patching fails, you get a warning and the game **still starts**, maybe with the standard
  creation screen. On Linux, when you start from Steam, the warning also shows in a window (with
  `zenity`) or as a notification (with `notify-send`). On Windows, the window waits for a key before
  the game starts. See Troubleshooting.

Then the game starts and connects to the server.

## Updating to a new release

- **Linux:** download and unpack the new `hearthdaoc-client-<version>.zip`, and run its `setup.sh`
  with the same options as before. It rebuilds `~/Games/HearthDAoC/client`, replaces
  `~/Games/HearthDAoC/patches` with the new release's patches, applies them and writes a new
  `play.sh`. Your saved login and your Steam shortcut stay.
- **Windows:** unpack the new zip and copy everything in its `windows` folder into
  `runtime\client-opendaoc\app` again, replacing the old files. Your `hearthdaoc.cfg` stays. The next
  start of `connect-hearthdaoc.bat` applies the new release's patches.

## Going back to the standard creation screen

Put the original files back, then remove the patches so that the game stops applying them at each
launch.

- **Linux:** in a terminal, run
  `python3 ~/Games/HearthDAoC/patches/apply_patches.py --client ~/Games/HearthDAoC/client --restore`
  and then `rm -r ~/Games/HearthDAoC/patches`. To get classic creation back, run `setup.sh` again.
- **Windows:** open a Command Prompt in `runtime\client-opendaoc\app` (in File Explorer, type `cmd`
  in that folder's address bar and press Enter), run `patch-client.bat -Restore`, and then delete
  `patch-client.ps1` and the `patches` folder from that folder. To get classic creation back, copy
  them from the bundle's `windows` folder again.

The restore puts an original back only over the patched file. If a file has changed since it was
patched (for example a newer client was installed), it says so and restores nothing.

If you set up the Linux client with `--dest`, use that folder instead of `~/Games/HearthDAoC`.

## Troubleshooting

**"Not patched", or the standard creation screen.** HearthDAoC's patches support only the
OfflineDAoC 0.34 `classic` client. Any other client, such as the `b` edition or a newer upstream
client, keeps the standard creation screen and plays normally.

**Windows: a warning at every start that the client patches could not be applied.** Usually
Windows doesn't let you change files in your OfflineDAoC folder, for example because it is under
`C:\Program Files`. Right-click `patch-client.bat` and choose **Run as administrator**, once. Later
starts only check the patched files, which needs no administrator. If the warning comes back after
something put the original files back, do it again.

**Linux: a warning that the client patches could not be applied.** Run `~/Games/HearthDAoC/play.sh`
in a terminal to see the message. Running `setup.sh` again from the bundle installs the patches
again.

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
