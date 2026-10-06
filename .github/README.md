> [!IMPORTANT]
> **This is HearthDAoC (`lometur/HearthDAoC`), an unofficial fork of [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).**
> It is **not** the original OfflineDAoC project. For the official game, releases and support, go to
> [shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC).

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
