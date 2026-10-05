> [!IMPORTANT]
> These scripts belong to **`lometur/OfflineDAoC`, an unofficial fork** of
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC). They connect you to a private
> central OfflineDAoC server; for the official single-player game, use the upstream project.

# Playing on the central server

Ask the server owner for two things first: the **server address** (for example `192.168.1.64:10301`)
and the **edition** (`classic` or `b`). Your client must use the same edition as the server.

Use a password you use nowhere else: the server stores passwords with a weak, unsalted hash, and the
password appears on the game's command line.

## Linux (Steam / Proton)

You need Steam with **Proton Experimental**, plus `python3` and `rsync`, and a 1.127 client folder
from the free [OpenDAoC installer](https://www.opendaoc.com/docs/client/) (it is only read).

1. Download and unpack `offlinedaoc-client-<version>.zip` from the fork's releases.
2. Run:
   `./setup.sh --server 192.168.1.64:10301 --edition classic --base-client "/path/to/your/1.127 client"`
   This builds a separate client in `~/Games/OfflineDAoC-Central` and downloads about 45 MB of
   OfflineDAoC files, each checked against the official release.
3. Steam → Games → **Add a Non-Steam Game** → Browse → `~/Games/OfflineDAoC-Central/play.sh`.
   Leave "Force the use of a specific Steam Play compatibility tool" **unchecked**. Any launch
   options must end with `%command%`.
4. Press Play. The first time, it asks for an account name and password. While the server allows it,
   your first login creates the account.

## Windows

1. Install the **official** OfflineDAoC release for the server's edition (upstream's
   `DOWNLOAD-AND-PLAY-v0.34.cmd` for `classic`, `DOWNLOAD-AND-PLAY-v0.34b.cmd` for `b`).
2. Turn on the Windows feature **.NET Framework 3.5 (includes .NET 2.0 and 3.0)**; the game's
   `connect.exe` needs it.
3. Copy `connect-central.bat` into the install's `runtime\client-opendaoc\app` folder and run it.
   It asks for the server address, account and password once and saves them in `central-server.cfg`.
   Don't use OfflineDAoC's own launcher for this; its "Enter Realm" only connects to your own PC.

## Character keeps running on its own (Linux)

On Proton 10 and later, a movement key can get "stuck" after zoning or certain key presses.
Workarounds: remove the **Run Lock 2** key binding (NumLock) in the game's keyboard options, and set
the Steam launch options of the game to `XMODIFIERS=@im=none %command%`. Tapping W once releases it.
