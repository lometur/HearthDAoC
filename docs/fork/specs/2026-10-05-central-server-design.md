# Sub-project 1: Fork and central multiplayer server — design

Status: approved in conversation 2026-10-05; awaiting review of this written spec.
Fork: `lometur/OfflineDAoC` (GitHub fork of `shadowofze/OfflineDAoC`). Upstream base: tag `v0.34b`, commit `169c9b79066d8818f3ddb0ba8b49551801a3319e`.

## 1. Goal

Run OfflineDAoC, with its autonomous AI players, as a central multiplayer server on the owner's
server machine, in Docker like the existing OpenDAoC deployment, so several people on the home LAN
can play together in one world. The fork stays a thin layer over upstream OfflineDAoC, which keeps
providing the bulk of the game.

This is the first of three sub-projects in the same fork, each with its own spec, plan and release:

1. **This spec:** fork, server image, deployment, admin tooling, player clients.
2. Classic character creation: players allocate their own stat points (later spec).
3. SI-style start-location choice at character creation (later spec).

### What the owner asked for

- A central OfflineDAoC server for multiplayer, on the server hardware, in Docker.
- A fresh world; no migration from the local install.
- A fork tracking upstream, with minor changes; changes and deployment tracked in the repo.
- The fork's README must say clearly, at the top, that it is not the original OfflineDAoC and link to it.
- The container must keep to its own scope and not interfere with other containers.

### Decisions made during design

| Topic | Decision |
|---|---|
| Players | Home LAN first; remote friends later through a VPN (Tailscale/ZeroTier), without redesign |
| Coexistence | Runs alongside the existing OpenDAoC server on the same machine |
| Repo | Public GitHub fork `lometur/OfflineDAoC` |
| Image delivery | GitHub Actions builds `ghcr.io/lometur/offlinedaoc`; the server pulls pinned tags |
| Client OS | Linux (Steam/Proton) and Windows |
| Accounts | Auto-created at first login during the LAN phase; one setting turns that off before going remote |
| Edition | Configurable (`classic` = 0.34, `b` = 0.34b with the Sluaghbinder class), fixed per world |
| Structure | Approach A: thin, additive fork; upstream server code unchanged in this sub-project |
| Networking | Host networking on dedicated ports; everything else isolated |

### Non-goals for this sub-project

- Character-creation changes (sub-projects 2 and 3).
- Internet exposure without a VPN.
- Migrating the owner's local OfflineDAoC world.
- A Windows equivalent of the selective client download (Windows players use the official release).
- Hosting any EA game files in the fork.

## 2. Repo layout and upstream sync

All fork additions live in new paths, so GitHub's "Sync fork" merges upstream cleanly:

| Path | Contents |
|---|---|
| `deploy/` | `Dockerfile`, `compose.yml`, entrypoint, world-data init, config template, `odc` admin command, `HANDOFF.md` |
| `deploy/upstream.lock` | Pinned upstream release (`v0.34b`), release asset names and sizes, and the SHA-256 of upstream's `PACKAGE MANIFEST.sha256` |
| `client/` | `README.md`, `linux/setup.sh` (+ the `play.sh` it writes), `windows/connect-central.bat` |
| `tools/linux/` | Linux CLIs over upstream code: `offline-bots` (launcher bot creation), `bot-goals` (launcher goals tab), `progress-import` (official import engine), plus a new `accounts` CLI |
| `.github/workflows/server-image.yml` | Build, test and publish the image |
| `.github/README.md` | Fork README shown on the repo page (see below) |
| `docs/fork/` | `FORK.md` (what the fork changes, how to sync, how to deploy) and `specs/` |

**"Not the original" notice.** `.github/README.md` (shown by GitHub instead of the root README)
opens with a banner: this is `lometur/OfflineDAoC`, an unofficial fork of
[shadowofze/OfflineDAoC](https://github.com/shadowofze/OfflineDAoC); it is not the original project;
for the official game, releases and support go there. It then describes the fork's additions and
links to upstream's README. The same short banner is added as the first lines of the root
`README.md`, which is the only upstream file this sub-project edits.

**Upstream sync.** Our work is on `main` next to upstream's history. To take a new upstream release:
"Sync fork", bump `deploy/upstream.lock`, let CI build, smoke-test, then deploy (see §9 for
the world upgrade). Server-code changes in later sub-projects stay small and are listed in
`docs/fork/FORK.md` so conflicts are easy to spot.

**Releases.** Tags `v0.34b-fork.N` publish `ghcr.io/lometur/offlinedaoc:v0.34b-fork.N`. The server
always runs a pinned tag. GitHub Actions must be enabled once in the fork's settings (forks start
with Actions disabled).

## 3. Server image (`deploy/Dockerfile`)

- **Build stage** (`mcr.microsoft.com/dotnet/sdk:10.0`):
  - `dotnet build source/server/DOLLinux.sln -c Release`, with a placeholder loopback
    `serverconfig.xml` in place (the build only needs one to exist; the real one is generated at start).
    Never `serverconfig.example.xml`, which listens on all interfaces with UPnP on.
  - Native pathfinding: `cmake` + `g++` build of `source/server/Pathing/Detour`
    (`-DCMAKE_BUILD_TYPE=Release -DSOVERSION=1 -DLIB_VERSION=1.0.0`), installed as
    `lib/Detour.so`. Not `source/development-tools/.../Detour`, which lacks `MoveAlongSurfaceGrounded`.
  - The `tools/linux/` CLIs.
- **Runtime stage** (`mcr.microsoft.com/dotnet/aspnet:10.0`, Debian): glibc is required by the
  bundled `SQLite.Interop.dll`, which rules out the Alpine base upstream OpenDAoC uses. Adds
  `sqlite3` and `python3`. Sets `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` explicitly (the server
  crashes in invariant mode: `en-US is an invalid culture identifier`).
- Layout: `/app/server` (server build, `lib/Detour.so`), `/app/tools`, `/app/bin` (scripts).
- No world data in the image. Expected size: about 400–500 MB.

## 4. World data and first start

All state lives in one named volume mounted at `/data`:

```
/data/world/opendaoc.sqlite3.db   world + progress (SQLite, WAL)
/data/navmesh/zoneNNN.nav         99 navmeshes, 2.13 GB
/data/bot-goals.json              optional; absent = upstream defaults
/data/logs/                       server logs
/data/backups/                    database snapshots
/data/world.json                  upstream version + edition this world was created from
```

The server's expected paths point into `/data` through the generated config (database, logs) and
symlinks created by the entrypoint (`/app/server/navmesh`, `/app/server/bot-goals.json`).

**First start with an empty volume.** `init-world` downloads from the pinned upstream release using
HTTP range requests (the method proven on the owner's PC with `odaoc_fetch.py`):

- the clean world database for `OFFLINEDAOC_EDITION` (`classic`: `editions/0.34-no-custom-class/runtime/data/opendaoc.sqlite3.db`; `b`: `runtime/data/opendaoc.sqlite3.db`);
- the 99 navmeshes.

About 600 MB once; resumable.

**Chain of trust.** `deploy/upstream.lock` (in git) pins the SHA-256 of upstream's
`PACKAGE MANIFEST.sha256`. That file is fetched first and checked against the pin; every other file
is checked against its CRC32 in the zip directory and its SHA-256 in the manifest. Any mismatch
aborts before anything is used.

**Edition is fixed per world.** `world.json` records version and edition. If `OFFLINEDAOC_EDITION`
later differs, the container refuses to start instead of mixing worlds; a new world must be created
deliberately (`odc` command, §6).

**Shortcut.** The handoff may copy the already-verified navmeshes from the owner's PC
(`~/Games/OfflineDAoC/runtime/server/navmesh/`) into the volume instead of downloading them;
`init-world` still verifies them against the manifest.

## 5. Runtime, networking and isolation

**Entrypoint** (`deploy/entrypoint.sh`):

1. Run `init-world` if `/data/world.json` is missing; check the edition matches.
2. Generate `/app/server/config/serverconfig.xml` from environment variables:

   | Variable | Default | Config key |
   |---|---|---|
   | `OFFLINEDAOC_LISTEN_IP` | `0.0.0.0` | `IP`, `RegionIP`, `UdpIP` |
   | `OFFLINEDAOC_PORT` | `10301` | `Port` |
   | `OFFLINEDAOC_UDP_PORT` | `10401` | `RegionPort`, `UdpPort` |
   | `OFFLINEDAOC_AUTO_ACCOUNTS` | `True` | `AutoAccountCreation` |
   | `OFFLINEDAOC_SERVER_NAME` | `OfflineDAoC (lometur fork)` | `ServerName` |
   | fixed | `False` | `EnableUPnP`, `DetectRegionIP`, `MetricsEnabled` |
   | fixed | `/data/world/...` | `DBType SQLITE`, `DBConnectionString` (WAL, as shipped) |

3. Start `dotnet CoreServer.dll` with stdin from a named pipe. On SIGTERM/SIGINT the entrypoint
   writes `exit` to the pipe and waits for the process to finish saving. The server has no signal
   handler, so a plain kill skips the final save; and with stdin closed its console loop busy-spins.
   `stop_grace_period: 120s`.
4. Daily database backup loop: `sqlite3 .backup` into `/data/backups`, keeping
   `OFFLINEDAOC_BACKUP_KEEP` (default 7).

**Networking: host networking** (`network_mode: host`) on ports **10301/tcp and 10401/udp**, next to
OpenDAoC's 10300/10400. Reason: for 1.124+ clients the server tells the client which address to use
for UDP, substituting the socket's local address for private ranges. In a Docker bridge network
that is the container's 172.x address, unreachable for players. On the host network it is the real
LAN address, or the VPN address once remote friends connect, with no reconfiguration.

**Host firewall.** Allow 10301/tcp and 10401/udp from the LAN subnet only (exact `ufw` rules in the
handoff); widen to the VPN interface later.

**Isolation.**

- Compose project `name: offlinedaoc`: every container, volume and network is prefixed `offlinedaoc-`; `docker compose down` touches nothing else.
- Own named volume `offlinedaoc-data`; no shared volumes or bind mounts into other projects.
- Resource limits: `mem_limit` (default 10 GB) and `cpus` (default 4), configurable.
- Non-root user (UID/GID configurable), `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]`, no Docker socket, not privileged.
- Health check: TCP `OFFLINEDAOC_PORT` (default 10301) listening. `restart: unless-stopped`.

## 6. Admin tooling (`deploy/odc`)

A host-side command in the deployment folder; it wraps `docker exec` / `docker compose run`.

| Command | Purpose | Server state |
|---|---|---|
| `odc status` | server up, version and edition, players online, bots online | any |
| `odc add-bots <alb\|mid\|hib> <1\|10\|100> <1\|50>` | launcher bot creation (tested); backup first | any |
| `odc account create <name> <password>` | admin-made account, server's password hashing | any |
| `odc account list` | accounts, PrivLevel, last login | any |
| `odc account plvl <name> <1\|2\|3>` | GM/admin rights | player logged out (checked) |
| `odc bot-goals show\|set\|reset\|import` | launcher goals tab (tested) | stopped for writes |
| `odc backup` | consistent snapshot | any |
| `odc restore <file>` | roll back the database | stopped |
| `odc new-world [--edition E]` | archive the current world, create a fresh one | stopped |
| `odc upgrade-world <version>` | §9 upstream version bump | stopped |
| `odc auto-accounts on\|off` | toggle `OFFLINEDAOC_AUTO_ACCOUNTS` in the compose env file and restart | restarts |

**"Stopped" is enforced by `odc`** by checking the server container is not running. The upstream
import engine only checks port 10300 (unused here), and the `bot-goals` CLI gets a configurable port
instead of its hard-coded 10300.

Server-console commands other than `exit` fail in this OpenDAoC base (the console client has no
account), so administration uses `odc` and in-game GM commands.

**New `accounts` CLI** (`tools/linux/accounts`): creates accounts with upstream's password hashing
(`ImportEngine.HashPassword`: `##` + MD5 of UTF-16BE), lists accounts, sets PrivLevel. Creating while
the server runs is safe (the server reads an account at login). Changing PrivLevel of a logged-in
player is refused, because logging out saves the in-memory account over the change.

## 7. Player clients

Each player needs the base 1.127 client (free OpenDAoC installer) plus OfflineDAoC's client files
for the server's edition (patched `game.dll` and about 80 files). The fork ships only scripts,
attached to each fork release as a small zip.

**Linux (Steam/Proton)**: `client/linux/setup.sh --server HOST:10301 --edition classic|b --base-client PATH`

1. Creates a separate client folder (the player's normal OpenDAoC client is not modified).
2. Copies the base client into it.
3. Downloads the edition's client files from upstream's pinned release with the verified
   selective method (about 45 MB) and overlays them.
4. Writes `play.sh` with the server address. On first run it asks for account and password
   (zenity, or terminal) and stores them in a private file. It runs the client through Proton
   Experimental in its own prefix, inside the Steam Linux Runtime, with full paths
   (`Z:\...\game.dll`), as proven by `connect-proton.sh` on the owner's PC.

Added to Steam as a non-Steam game without a forced compatibility tool. Needs Steam with Proton,
`python3` and `curl`.

**Windows**: install upstream's official OfflineDAoC release for the server's edition and enable the
.NET Framework 3.5 Windows feature; place `client/windows/connect-central.bat` in the release's
`runtime\client-opendaoc\app` folder. It runs `connect.exe game.dll HOST:10301 <account> <password>`,
asking for the login on first use. Upstream's launcher is not used ("Enter Realm" targets 127.0.0.1).

**Edition match.** Client and server must use the same edition; setup takes it explicitly and the
README says to ask the server owner.

**`client/README.md`** covers both setups, "use a password you use nowhere else" (passwords are
stored as unsalted MD5), adding to Steam, and the stuck-running workaround on Proton 10+
(disable the "Run Lock 2" NumLock binding; launch option `XMODIFIERS=@im=none %command%`).

## 8. Error handling

- `init-world`: any download or checksum failure aborts startup with the failing file named; partial downloads resume on the next start.
- Config generation: invalid port or edition values abort startup with a clear message.
- Edition mismatch with an existing world: refuse to start (§4).
- Server process exits on its own: the container exits non-zero and Docker restarts it (`unless-stopped`); logs stay in `/data/logs`.
- `odc` destructive commands (`restore`, `new-world`, `upgrade-world`) refuse while the server runs and always take a backup first.

## 9. Updates

- **Fork-only updates on the same upstream version** (rebuilds, sub-projects 2 and 3): deploy the new image tag; `/data` is untouched; no migration. The server creates missing tables at startup; whether it also adds new columns to existing SQLite tables is checked during planning, and a scripted migration is added if a later change needs one.
- **Upstream version bumps:** upstream ships a new clean world with content fixes, and its progress importer moves saved progress into it. `odc upgrade-world <version>`: stop, back up, fetch the new clean world (verified), run the official import engine from the old world into it, check row counts, swap only on success. The old world stays as a rollback. Before upgrading, `deploy/upstream.lock` and the image move to the new version, including that version's import engine.

## 10. Testing and verification

**CI (every build)**

- Image builds; `tools/linux` CLIs compile; `nm -D` shows all 14 Detour exports.
- Container smoke test with a world-database-only volume (navmeshes skipped with `OFFLINEDAOC_SKIP_NAVMESH=1`, CI only):
  1. server reaches "listening" on the configured port;
  2. generated config has the expected values;
  3. `docker stop` produces a clean save (log shows `Stopped`);
  4. a restart comes back with the data.

**Full test on the owner's PC before handoff**

1. `docker compose up` with the real world (navmesh shortcut).
2. Connect with `client/linux/setup.sh` + `play.sh` to `localhost:10301`.
3. Verify: account auto-creation, bots logging in and playing, every `odc` command, stop/save/restart keeping the character, progress import into a fresh world.

**Done means**

1. Two clients on the LAN (the owner's PC and one other machine) logged in at the same time, seeing each other and the bots.
2. A server restart keeps everyone's progress.
3. Backup and restore work.
4. The existing OpenDAoC server keeps running, untouched.

## 11. Handoff (`deploy/HANDOFF.md`)

Written for the Claude session on the server machine:

- **Pre-checks:** Docker and Compose versions, free RAM (target ≥ 10 GB available next to OpenDAoC), ports 10301/10401 free, and an explicit instruction not to modify, stop or recreate the OpenDAoC containers, volumes or networks.
- **Deploy:** create the deployment folder; get `compose.yml`, `odc` and `.env` from the release; pull the pinned image; optionally copy navmeshes from the owner's PC; first start; firewall rules.
- **Verify:** expected log lines (`Server is now listening ... :10301`, `Loading NavMesh successful` ×99), health status, `odc status`, and a test login from the owner's PC.
- **Rollback:** `docker compose -p offlinedaoc down` (volume kept).

## 12. Risks and open questions

- **Server RAM** is unknown; the handoff's first step measures it.
- **SQLite schema evolution** for fork-only changes (column additions) is unverified (§9).
- **Process checks in the upstream import engine** are Windows-oriented; `odc` adds container-level checks.
- **Client stuck-running on Proton 10+** is a client-side Wine/IBus/NumLock issue; documented workarounds only.
- **Shallow local clone:** the working copy was cloned with `--depth 1`; unshallow or re-clone from the fork before pushing if GitHub rejects the push.
- **Upstream asset changes:** if upstream replaces release assets, checksum verification fails safely; `upstream.lock` must then be updated deliberately.
- **Licensing:** GPL-3.0; the fork publishes all source. No EA client files are hosted by the fork.
