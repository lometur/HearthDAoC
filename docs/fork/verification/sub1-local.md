# Sub-project 1: local verification

Owner's PC (Linux Mint 22.3, Docker 29.8.1, Compose 2.29), 2026-10-05. Image `offlinedaoc:dev` built
from branch `sub1-central-server`; deployment in a separate folder using the release `compose.yml`,
`odc` and `.env.example` (`OFFLINEDAOC_TAG=dev`, `OFFLINEDAOC_IMAGE=offlinedaoc`, edition classic).
Client: `client/linux/setup.sh` with the OpenDAoC 1.127 client as base, Proton Experimental via Steam.

| # | Check | Command | Expected | Actual | Result |
|---|---|---|---|---|---|
| 1 | World from seed navmeshes | `odc init --seed-navmesh ~/Games/OfflineDAoC/runtime/server/navmesh` | 99 navmeshes reused, none downloaded | `navmeshes ready (99 files, 0 downloaded)` | pass |
| 2 | Start and health | `odc up`, `odc status` | running (healthy), classic, navmesh True | as expected | pass |
| 3 | Server log | grep in `/data/logs/server.log` | 99 navmeshes; listening on 0.0.0.0:10301 | `99`; `listening ... on 0.0.0.0:10301` | pass |
| 4 | Client setup | `setup.sh --server 127.0.0.1:10301 --edition classic ...` | verified client, classic `game.dll` | 82 files verified; `game.dll` 67dcf68a… | pass |
| 5 | First login creates the account | `play.sh`, new account `tester1` | account auto-created, character enters the world | `New account created: tester1`; character Elathi in game | pass |
| 6 | Bots while a player is online | `odc add-bots alb/mid/hib 10 1` | bots log in | 16/30 online after 3 min, 30/30 later | pass |
| 7 | Backup while running | `odc backup`, `odc backups` | new backup listed | listed | pass |
| 8 | Admin level refused while online | `odc account plvl tester1 2` | refusal | `ERROR: tester1 may be logged in ...` | pass |
| 9 | Account made by `odc` logs in | `odc account create Friend1 ...`, then login as Friend1 | login works (server's password hashing) | logged in, created a character | pass |
| 10 | Admin level once offline | `odc account plvl tester1 2` | accepted | `tester1 is now plvl 2` | pass |
| 11 | Clean stop | `odc stop` | `GameServer | Stopped` logged, exit 0 | logged; exit 0; took about 1 s with 30 bots | pass |
| 12 | Restart keeps everything | `odc up`, `odc status`, `odc account list` | accounts, characters, bots and plvl kept | 2 accounts, 2 characters, 30 bots, tester1 plvl 2 | pass |
| 13 | Bot goals while stopped | `odc bot-goals set 50 20 30 50`, `show` | saved and shown; linked at next start | saved; `/app/server/bot-goals.json -> /data/bot-goals.json` after start | pass |
| 14 | Upgrade through the real importer | `odc upgrade-world --same-version` | counts kept, old world archived | `{'Account': 2, 'DOLCharacters': 2, 'offline_world_bots': 30}`; archived | pass |
| 15 | Start after upgrade | `odc up`, `odc status` | healthy, same data, plvl restored | healthy after 21 s; same counts; tester1 plvl 2 | pass |

## Found and fixed during this run

- Backups left `.part-wal`/`.part-shm` files (copies inherited WAL mode) and were named in local time,
  so backups made by one-off admin containers (no TZ) sorted hours away from the server's. Fixed in
  `deploy/bin/backup.py` (standalone copies, UTC names) with two new unit tests; re-checked here: a new
  backup is a single file with a UTC name.

## Not covered here

- Two clients from different LAN machines at once, and the deployment on the server machine: see
  `deploy/HANDOFF.md` and the "Server deployment" section added after the handoff.
