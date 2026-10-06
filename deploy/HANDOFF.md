# Handoff: deploy the HearthDAoC server

For the Claude session on the server machine. This deploys HearthDAoC (`lometur/HearthDAoC`, an unofficial
fork of shadowofze/OfflineDAoC) next to the existing OpenDAoC server.

**Do not modify, stop, restart or recreate any OpenDAoC container, volume, network or compose
project.** Everything here uses the compose project `hearthdaoc`, the container
`hearthdaoc-server`, the volume `hearthdaoc-data` and ports 10301/tcp + 10401/udp.

## 1. Pre-checks (report each result to the owner)

```bash
docker --version && docker compose version          # Compose v2 required
free -g                                              # need >= 10 GB available next to OpenDAoC
ss -ltnu | grep -E ':(10301|10401)\b' || echo free   # both ports must be free
docker ps --format '{{.Names}}' | grep -i opendaoc   # note OpenDAoC's containers; leave them alone
```
If less than 10 GB of RAM is available, lower `HEARTHDAOC_MEM_LIMIT` in step 2 and tell the owner.

## 2. Install

```bash
mkdir -p ~/hearthdaoc && cd ~/hearthdaoc
curl -fLO https://github.com/lometur/HearthDAoC/releases/download/v0.34b-hearth.1/hearthdaoc-deploy-v0.34b-hearth.1.tar.gz
tar xzf hearthdaoc-deploy-v0.34b-hearth.1.tar.gz
cp .env.example .env
```
Edit `.env`: set `TZ` to the owner's timezone; keep `HEARTHDAOC_EDITION=classic` unless the owner
says otherwise. Then pull: `docker compose -p hearthdaoc -f compose.yml --env-file .env pull`.
If the pull says `denied`, the image package is still private: tell the owner (GitHub → Packages →
hearthdaoc → Package settings → visibility Public) and wait.

## 3. World data (choose one)

- **Shortcut (preferred):** ask the owner to copy their navmeshes over, by running this on THEIR PC:
  `rsync -a ~/Games/OfflineDAoC/runtime/server/navmesh/ <user>@<this machine>:hearthdaoc/seed-navmesh/`
  then: `./hdc init --seed-navmesh ~/hearthdaoc/seed-navmesh` (verifies every file; downloads only the
  world database, ~30 MB). Afterwards `rm -rf ~/hearthdaoc/seed-navmesh`.
- **Download:** `./hdc init` (about 600 MB from GitHub, verified, resumable).

Then bring back the leveling monsters upstream archived (the owner chose levels 1-20, OpenDAoC's
density; takes a few seconds and is re-applied automatically by new-world and upgrade-world):
`./hdc spawns restore --max-level 20` and check `./hdc spawns status` (expect `restored` about 12,600).

## 4. Start and firewall

```bash
./hdc up        # if the server refuses to start, this prints why (and ./hdc status repeats it)
sudo ufw status # report the result to the owner
```
Only if ufw is **active**, add LAN-only rules:
```bash
sudo ufw allow from <LAN subnet, e.g. 192.168.1.0/24> to any port 10301 proto tcp
sudo ufw allow from <LAN subnet> to any port 10401 proto udp
```
If ufw is inactive, do **not** enable it (that can cut off SSH and OpenDAoC); tell the owner instead. If the
machine uses another firewall, describe it to the owner rather than changing it.

## 5. Verify (report each)

```bash
sleep 90 && ./hdc status                       # running (healthy), edition classic, navmesh True
docker exec hearthdaoc-server grep -c "Loading NavMesh successful" /data/logs/server.log   # 99
docker exec hearthdaoc-server grep "Server is now listening" /data/logs/server.log | tail -1   # 0.0.0.0:10301
docker ps --format '{{.Names}} {{.Status}}' | grep -i opendaoc   # OpenDAoC still up, unchanged
```
Then ask the owner to connect from their PC (`~/Games/HearthDAoC/play.sh` after running
`setup.sh --server <this machine's LAN IP>:10301 ...`) and confirm the login works.

## 6. Day-to-day

`./hdc help` lists everything: status, logs, add-bots, accounts, bot goals, backups, restore,
new-world, upgrade-world, auto-accounts, spawns. Backups run daily into the volume (keep 7). The copies
taken before add-bots keep their newest 3; the copies taken before restore and upgrade are kept until
you remove them (`./hdc backups` lists them; they are in /data/backups).
Before remote players join: `./hdc auto-accounts off` and create their accounts with
`./hdc account create <name> <password>`.

## Rollback

`./hdc down` stops and removes the container; the world stays in the `hearthdaoc-data` volume.
To remove everything: `./hdc down && docker volume rm hearthdaoc-data` (deletes the world).

## Upgrading to a new fork release

```bash
cd ~/hearthdaoc && ./hdc backup && ./hdc stop
mkdir -p new && cd new
curl -fLO https://github.com/lometur/HearthDAoC/releases/download/<new tag>/hearthdaoc-deploy-<new tag>.tar.gz
tar xzf hearthdaoc-deploy-<new tag>.tar.gz && cp compose.yml hdc HANDOFF.md upstream.lock .. && cd ..
diff .env new/.env.example     # copy any new settings into .env (keep the owner's values)
sed -i 's/^HEARTHDAOC_TAG=.*/HEARTHDAOC_TAG=<new tag>/' .env
docker compose -p hearthdaoc -f compose.yml --env-file .env pull
./hdc up
```
If `./hdc up` reports that the world is from another upstream version, run `./hdc stop`,
`./hdc upgrade-world` (it backs up, moves all progress into the new clean world, keeps bans and
permissions, and lists server settings to re-check in its report), then `./hdc up`. Remove `new/`.
