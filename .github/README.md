> **Disclaimer.** Dark Age of Camelot is the intellectual property of Electronic Arts and Broadsword
> Online Games. HearthDAoC (`lometur/HearthDAoC`) is an unofficial fork of
> [Offline DAoC](https://github.com/shadowofze/OfflineDAoC), not the original project. Neither project is
> affiliated with, endorsed by, or connected to Electronic Arts or Broadsword. The game client, textures,
> models and other assets they use belong to those companies, and both projects are built on top of that
> copyrighted work.
>
> This is a free, non-commercial fan project. It is not a product and it is not for sale. No donations
> are accepted, and the project owner has not made, and will not make, any money from it. The client
> files are the same publicly available ones every free shard uses, and this repository hosts none of
> them: setup takes them from Offline DAoC's own releases. The work original to Offline DAoC is the bot
> system and the server customizations; HearthDAoC adds its multiplayer server setup and its own changes
> on top. Because the code is open source, the owner cannot control what forks or other people do with it.
>
> This "time capsule" of the 2002-2003 game was never meant to compete with the current retail Dark Age
> of Camelot, which is a very different, modern experience. If that interests you, please visit the
> official site: [darkageofcamelot.com](https://www.darkageofcamelot.com/).

# HearthDAoC

HearthDAoC runs OfflineDAoC (Dark Age of Camelot with autonomous bot players, built on OpenDAoC)
as a **central multiplayer server** in Docker, so several people can play in one world together.
Upstream provides the game; this fork adds only deployment, admin tooling and player setup:

| Folder | What it adds |
|---|---|
| [`deploy/`](../deploy) | Docker image, compose file, `hdc` admin command, server handoff guide |
| [`tools/linux/`](../tools/linux) | Linux command-line versions of the launcher's bot, bot-goals and progress-import tools |
| [`client/`](../client) | Player setup for Linux (Steam/Proton) and Windows |
| [`docs/fork/`](../docs/fork) | What the fork changes, how it syncs with upstream, design specs and plans |

Everything else is upstream OfflineDAoC, unchanged; see the [upstream README](../README.md).
License: GPL-3.0, like upstream.
