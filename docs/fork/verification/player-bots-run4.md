# Player-bot prototype: run 4 on your server

For the owner (#137). Runs 1-3 measured 30 player bots on our PC. Run 4 measures them once on your server, beside
your real roster of GameBots, on a **second, throwaway server**: its own folder, its own container and its own copy of
today's world. Your live server keeps running and nothing in it changes. The copy is deleted at the end.

- Image: `ghcr.io/lometur/hearthdaoc:proto-74e7b49` (built by CI from commit `74e7b49d6b0f8cfc88f4bc513ad747469b413f62`
  on the branch `proto/player-bots`; it holds no EA files).
- Time: about 15 minutes of setup, then 62 minutes of waiting, then 5 minutes to collect and clean up.
- Needs: 8 GB of free memory beside the live server, 6 GB of free disk, ports 10393 and 10493 free.

What it measures: 30 player bots (10 a realm) log in to the copy of your world and play for an hour, with the bot CPU
meter on. Your GameBots log in as usual. The numbers come from minutes 20-50.

## The steps

```bash
# 0. Checks: nothing changes
ss -ltnu | grep -E ':(10393|10493)\b' || echo "ports free"
free -g      # "available" 8 or more: the live server keeps its ~6.5 GB
df -h "$(docker info -f '{{.DockerRootDir}}')"      # 6 GB free or more
LIVEVOL=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Name}}{{end}}{{end}}' hearthdaoc-server); echo "$LIVEVOL"
grep -E '^(HEARTHDAOC_CPUS|HEARTHDAOC_EDITION|HEARTHDAOC_MEM_LIMIT|HEARTHDAOC_UID|HEARTHDAOC_GID|TZ)=' ~/hearthdaoc/.env
# 1. A folder of its own, with compose.yml and hdc from the prototype's exact commit
mkdir ~/hearthdaoc-proto && cd ~/hearthdaoc-proto
for f in compose.yml hdc; do curl -fsSLO "https://raw.githubusercontent.com/lometur/HearthDAoC/74e7b49d6b0f8cfc88f4bc513ad747469b413f62/deploy/$f"; done
chmod +x hdc
cat > .env <<'EOF'
HEARTHDAOC_IMAGE=ghcr.io/lometur/hearthdaoc
HEARTHDAOC_TAG=proto-74e7b49
HEARTHDAOC_PROJECT=hearthdaoc-proto
HEARTHDAOC_CONTAINER=hearthdaoc-proto-server
HEARTHDAOC_VOLUME=hearthdaoc-proto-data
COMPOSE_PROJECT_NAME=hearthdaoc-proto
HEARTHDAOC_LISTEN_IP=127.0.0.1
HEARTHDAOC_PORT=10393
HEARTHDAOC_UDP_PORT=10493
HEARTHDAOC_AUTO_ACCOUNTS=False
HEARTHDAOC_SI_START_CHOICE=off
HEARTHDAOC_BOT_CPU=on
HEARTHDAOC_PLAYER_BOTS=30
EOF
grep -E '^(HEARTHDAOC_CPUS|HEARTHDAOC_EDITION|HEARTHDAOC_MEM_LIMIT|HEARTHDAOC_UID|HEARTHDAOC_GID|TZ)=' ~/hearthdaoc/.env >> .env
grep -c -E '^HEARTHDAOC_(PROJECT|CONTAINER|VOLUME)=hearthdaoc-proto' .env    # MUST print 3; otherwise stop: hdc would act on the live server
# 2. The test volume: the live navmeshes and a fresh copy of the live world. The live volume is mounted read-only.
docker volume create --label com.docker.compose.project=hearthdaoc-proto --label com.docker.compose.volume=data hearthdaoc-proto-data
(cd ~/hearthdaoc && ./hdc backup)                  # prints /data/backups/world-<stamp>-manual.db
B=world-<stamp>-manual.db                          # the name it printed
docker run --rm --network none --user 1000:1000 -v "$LIVEVOL":/live:ro -v hearthdaoc-proto-data:/data \
  --entrypoint sh ghcr.io/lometur/hearthdaoc:proto-74e7b49 -c \
  "mkdir -p /data/navmesh /data/backups && cp -r /live/navmesh/. /data/navmesh/ && cp /live/backups/$B /data/backups/$B \
   && if [ -f /live/bot-goals.json ]; then cp /live/bot-goals.json /data/; fi"
docker exec hearthdaoc-server rm /data/backups/$B  # the live server's copy is no longer needed
# 3. The world: init checks the copied navmeshes (no download) and builds the clean world, then the restore
./hdc init                                          # ends "World ready."
./hdc restore $B                                    # "Restored world-<stamp>-manual.db. ..."
docker run --rm --network none --user 1000:1000 -v hearthdaoc-proto-data:/data --entrypoint sqlite3 \
  ghcr.io/lometur/hearthdaoc:proto-74e7b49 /data/world/opendaoc.sqlite3.db \
  "UPDATE ServerProperty SET Value='False' WHERE \`Key\`='Discord_Webhook_Active'"
# 4. Start. Then do not log in to this server until step 5 is done.
./hdc up
#    Repeat this every minute until it prints one line with the time the bots were planned (the world loads first):
docker exec hearthdaoc-proto-server sh -c 'grep -h "HEARTHDAOC_PLAYER_BOTS plan" /data/logs/server*'
# 5. 62 minutes after that line's time: collect
mkdir -p ~/proto-run4 && docker cp hearthdaoc-proto-server:/data/logs/bot-cpu.csv ~/proto-run4/
docker exec hearthdaoc-proto-server sh -c 'cat $(ls -tr /data/logs/server*)' \
  | grep -E 'BOT_CPU|PLAYER_BOT|SERVER_WORK|SERVER_MEMORY |Saving completed|(players|autonomousBots): [0-9]+ in|Speed hack|Position update timeout|Hard timeout|Kicking inactive|same session ID|entity manager|Linkdead player|Critical error|NotImplementedException|NullReferenceException' > ~/proto-run4/lines.log
#    Attach bot-cpu.csv and lines.log to a comment on #137 (or zip them).
# 6. Optional: look in game. Only if you set HEARTHDAOC_LISTEN_IP to this server's LAN address in step 1 (see below).
# 7. Remove everything (and the ufw rules, if you added any)
./hdc down && docker volume rm hearthdaoc-proto-data && docker rmi ghcr.io/lometur/hearthdaoc:proto-74e7b49
cd ~ && rm -rf ~/hearthdaoc-proto ~/proto-run4
```

## Notes

- **Never** run `./hdc update`, `new-world` or `upgrade-world` in `~/hearthdaoc-proto`, and never a bare
  `docker compose` there: `compose.yml` line 2 names the live project. `./hdc` uses the names in this folder's `.env`.
- `--user 1000:1000` is the default UID. If your live `.env` sets `HEARTHDAOC_UID`/`GID`, use those numbers.
- `restore` refuses a world of another edition. That is why step 1 copies `HEARTHDAOC_EDITION`.
- Your GameBots log in over the first 15 minutes. Minutes 20-50 after the `plan` line are the numbers, and the memory
  line comes at minute 51. `./hdc up` returns within 20 seconds, before the world has loaded, so the 62 minutes count
  from the `plan` line, not from `up`.
- **Looking in game (optional):** in step 1, put this server's LAN address in `HEARTHDAOC_LISTEN_IP` instead of
  `127.0.0.1`. If ufw is active, add LAN-only rules for 10393/tcp and 10493/udp as in HANDOFF.md step 4. Point a client
  at `<LAN address>:10393` after step 5; `/who` lists the player bots (`Pbot...`). Look only: logging in during the
  hour would change the numbers.
- The ghcr tag can be deleted afterwards (GitHub → Packages → hearthdaoc → proto-74e7b49).

## Result

- Date:
- Anything odd (a step that printed something unexpected, the bots not all `playing=30`):
