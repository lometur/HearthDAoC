# Handoff: deploy the OfflineDAoC central server

For the Claude session on the server machine. This deploys `lometur/OfflineDAoC` (an unofficial
fork of shadowofze/OfflineDAoC) next to the existing OpenDAoC server.

**Do not modify, stop, restart or recreate any OpenDAoC container, volume, network or compose
project.** Everything here uses the compose project `offlinedaoc`, the container
`offlinedaoc-server`, the volume `offlinedaoc-data` and ports 10301/tcp + 10401/udp.

## 1. Pre-checks (report each result to the owner)

```bash
docker --version && docker compose version          # Compose v2 required
free -g                                              # need >= 10 GB available next to OpenDAoC
ss -ltnu | grep -E ':(10301|10401)\b' || echo free   # both ports must be free
docker ps --format '{{.Names}}' | grep -i opendaoc   # note OpenDAoC's containers; leave them alone
```
If less than 10 GB of RAM is available, lower `OFFLINEDAOC_MEM_LIMIT` in step 2 and tell the owner.

## 2. Install

```bash
mkdir -p ~/offlinedaoc && cd ~/offlinedaoc
gh release download v0.34b-fork.1 --repo lometur/OfflineDAoC --pattern 'offlinedaoc-deploy-*.tar.gz'
tar xzf offlinedaoc-deploy-v0.34b-fork.1.tar.gz
cp .env.example .env
```
Edit `.env`: set `TZ` to the owner's timezone; keep `OFFLINEDAOC_EDITION=classic` unless the owner
says otherwise. Then pull: `docker compose -p offlinedaoc -f compose.yml --env-file .env pull`.

## 3. World data (choose one)

- **Shortcut (preferred):** ask the owner to copy their navmeshes over, by running this on THEIR PC:
  `rsync -a ~/Games/OfflineDAoC/runtime/server/navmesh/ <user>@<this machine>:offlinedaoc/seed-navmesh/`
  then: `./odc init --seed-navmesh ~/offlinedaoc/seed-navmesh` (verifies every file; downloads only the
  world database, ~30 MB). Afterwards `rm -rf ~/offlinedaoc/seed-navmesh`.
- **Download:** `./odc init` (about 600 MB from GitHub, verified, resumable).

## 4. Start and firewall

```bash
./odc up
sudo ufw allow from <LAN subnet, e.g. 192.168.1.0/24> to any port 10301 proto tcp
sudo ufw allow from <LAN subnet> to any port 10401 proto udp
```
(If the machine uses another firewall, add the equivalent LAN-only rules and tell the owner.)

## 5. Verify (report each)

```bash
sleep 90 && ./odc status                       # running (healthy), edition classic, navmesh True
docker exec offlinedaoc-server grep -c "Loading NavMesh successful" /data/logs/server.log   # 99
docker exec offlinedaoc-server grep "Server is now listening" /data/logs/server.log | tail -1   # 0.0.0.0:10301
docker ps --format '{{.Names}} {{.Status}}' | grep -i opendaoc   # OpenDAoC still up, unchanged
```
Then ask the owner to connect from their PC (`~/Games/OfflineDAoC-Central/play.sh` after running
`setup.sh --server <this machine's LAN IP>:10301 ...`) and confirm the login works.

## 6. Day-to-day

`./odc help` lists everything: status, logs, add-bots, accounts, bot goals, backups, restore,
new-world, upgrade-world, auto-accounts. Backups run daily into the volume (keep 7).
Before remote players join: `./odc auto-accounts off` and create their accounts with
`./odc account create <name> <password>`.

## Rollback

`./odc down` stops and removes the container; the world stays in the `offlinedaoc-data` volume.
To remove everything: `./odc down && docker volume rm offlinedaoc-data` (deletes the world).

## Upgrading to a new fork release

```bash
./odc backup && ./odc stop
sed -i 's/^OFFLINEDAOC_TAG=.*/OFFLINEDAOC_TAG=<new tag>/' .env
docker compose -p offlinedaoc -f compose.yml --env-file .env pull
./odc upgrade-world        # only if the release notes say the upstream version changed
./odc up
```
