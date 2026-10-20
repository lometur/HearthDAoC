# Bot CPU meter: checks on the live server

For the owner, after the release with the bot CPU meter is installed (`./hdc update`). The design is in the
[spec](../specs/2026-10-14-bot-cpu-meter-design.md); these are its checks (section 6.5). Write what you saw on each
check's "Result:" line: pass or fail, and anything odd. Log lines pasted in are welcome.

- Date:
- Release (`HEARTHDAOC_TAG` in `~/hearthdaoc/.env`):

What it does: while the meter is on, the server times each game-loop callback and charges it to the kind of its owner:
`gamebot` (the autonomous GameBots), `companion` (the other GameBots: companions and the `/spawn`, `/grind` and `/raid`
helpers), `playerbot` and `piloted` (none on the live server today), `player` (you and any real players), `monster`
(every other NPC, guards and merchants included, and keep doors) and `unattributed` (work with no owner). A pet counts
for its owner's kind. Every minute it writes the lines below to the server log, and one row per kind to
`/data/logs/bot-cpu.csv`. It is off by default: nothing changes until a GM turns it on. While on it costs an estimated
1-5% of one core; if `tps` falls below 30.0, or the `SERVER_WORK` lines' `overBudget` rises, turn it off with
`/botcpu off`.

## Reading the server log

`./hdc logs` keeps only the last 200 lines, so read the log files themselves, on the server, from `~/hearthdaoc`. The
server starts a new log file at midnight (and at 100 MB) and keeps a week of them, so every command here reads them
all, oldest first:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -40
```

Each line starts with its date and time (`MM/dd HH:mm:ss`), in the server's local time, as `/botcpu` shows times.

| Line | Written | What to read |
|---|---|---|
| `BOT_CPU` | every minute, one per kind with anything charged, in the kind order above | `n` (the kind's average count), `msPerBotSec` (the average CPU per bot, in ms per second: 1.0 is 0.1% of one core), `msSec` (the whole kind), and after the `\|` the stages, per bot; `unattributed` has `n=-`, and its stages are ms per second for the whole kind |
| `BOT_CPU_TOP` | every minute, one per bot kind with a bot charged | the 5 costliest bots, `rank=name:ms per s`, with each GameBot's goal |
| `BOT_CPU_PROCESS` | every minute | the whole process: `tps`, `processCpuPct`, `loopThreadsCpuPct`, `chargedPct`, `callsSec`, `loopWaitMs`, `throttledMs`, `gcPauseMs` and more; `-` for a value the platform can't give |
| `BOT_CPU_COMPARE` | every minute, for `player/gamebot` while both have `n` of 1.0 or more (also `playerbot/gamebot` and `piloted/gamebot`, which have no bots today) | `cpu`, the ratio of the two kinds' `msPerBotSec`; `alloc`, `calls` and `usPerCall` |
| `BOT_CPU_STATE` | each time it turns on or off, or its stop time changes | `on source=property` (at start), `on source=command by=<GM> minutes=<m or ->`, `off source=command by=<GM>`, `off source=timer`, `off source=stop` (server stop), `off source=error` |
| `BOT_CPU_ERROR` | a fault in the meter's report, just before `off source=error` (a Warn line) | what failed: post it on #137. The server goes on, and `/botcpu on` starts the meter again |

Every field is explained in the spec's section 2.1, and section 7 says how to read the numbers fairly. To read a few
minutes only, add a time filter right after the `grep`, with the month/day and hour:minute, for example from 14:00 to
14:10 on 21 October: `| awk '$0 >= "10/21 14:00" && $0 < "10/21 14:10"'`.

## The checks

### 1. The update

`./hdc update` lists `HEARTHDAOC_BOT_CPU` among the new settings added to `.env`, and `.env` has
`HEARTHDAOC_BOT_CPU=off`. The container's start output has "Bot CPU meter: off" once (the server property's row is
new). The server log has no line of the meter, and `SERVER_WORK` looks as before:

```bash
grep HEARTHDAOC_BOT_CPU ~/hearthdaoc/.env
docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | grep -v "Command - '&botcpu'"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_WORK | tail -17
```

The third prints nothing: it leaves out the start's line that loads `/botcpu` for GMs,
`Command - '&botcpu' - ... required plvl:2`, whose description names the BOT_CPU lines. The fourth shows the last
minute's `SERVER_WORK` lines, one per stage.

Result:

### 2. Turn it on in game

In game, as GM: `/botcpu` says "Bot CPU meter: off. /botcpu on [minutes] turns it on." Then `/botcpu on 30`, which
says "Bot CPU meter on for 30 minutes (until 14:32): BOT_CPU lines every minute in the server log, rows in
logs/bot-cpu.csv." (with your own time). Note the time you turned it on, for check 7.

To keep your character from waking monsters and speeding up GameBots near you (the spec's section 7), go somewhere
quiet, or go to the character screen or quit the game: both take you out of `player`. Quit from the game's menu: a
client that is killed leaves your character link-dead in the world for 60 s, still counted. The meter runs on.

Result:

### 3. The lines

After 2-3 minutes:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -20
```

Once: a `BOT_CPU_STATE on source=command` line. Then each minute: `gamebot` with `n` close to your bot count (about
240), `player` with `n` 1.0 while you are in game, `monster`, `unattributed`, a `BOT_CPU_TOP` for `gamebot`,
`BOT_CPU_PROCESS` with `tps=30.0`, and `BOT_CPU_COMPARE pair=player/gamebot` while you are in game. `companion` has a
line while players use companions; `playerbot` and `piloted` have none.

Result:

### 4. Sanity

On the `BOT_CPU_PROCESS` lines, usually `chargedPct` < `loopThreadsCpuPct` < `processCpuPct`. If `chargedPct` is
higher, `loopWaitMs`, `throttledMs` or `gcPauseMs` is large too: the wall times are inflated (the spec's section 3.9).
`gamebot` `msSec` / 10 is its share of one core in %. `loopThreadsCpuPct` as `-` means the kernel gives no scheduler
statistics (3.9): say so on #137.

Confirm a large `gcPauseMs` with the `SERVER_GC_PAUSE` lines of the same minutes. Those lines list only pauses of
250 ms or more; shorter pauses add up in the `pauseTotalMs` of the `SERVER_MEMORY` line that covers them (every 5
minutes):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_GC_PAUSE | tail -20
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_MEMORY | tail -3
```

Result:

### 5. In chat

`/botcpu` shows the same last minute as the log's last `BOT_CPU` lines: a status line (`on since ...`, `by command`,
`stops at ...`), the last minute's process line, one line per kind (`gamebot n=...: ... ms/s per bot`) and the totals
since on. `/botcpu top` lists 5 GameBots with their goals and what they are doing, such as
`1. Aldric (34 Cleric, region 163): 7.412 ms/s, 880 calls/s, Rvr, "..."`.

Result:

### 6. The CSV

```bash
docker exec hearthdaoc-server tail -5 /data/logs/bot-cpu.csv
```

A header the first time, then rows: one per minute and kind. To chart it, copy it out with
`docker cp hearthdaoc-server:/data/logs/bot-cpu.csv .`; its stage columns are whole-kind totals, not per bot.

Result:

### 7. Overhead

Compare the `SERVER_WORK` lines of the 10 minutes before you turned the meter on with 10 minutes on. This adds up each
minute's lines (one per stage); put your own times of check 2 in its first `awk` (here the meter went on at 14:00 on
21 October):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_WORK |
  awk '$0 >= "10/21 13:50" && $0 < "10/21 14:10"' |
  awk '{ m = $1 " " substr($2, 1, 5)
         for (i = 1; i <= NF; i++) {
           split($i, f, "=")
           if (f[1] == "samples" && f[2] > ticks[m]) ticks[m] = f[2]
           if (f[1] == "avgMs") ms[m] += f[2]
           if (f[1] == "overBudget") over[m] += f[2]
         } }
       END { for (m in ms) printf "%s samples=%d avgMsSum=%.3f overBudget=%d\n", m, ticks[m], ms[m], over[m] }' | sort
```

Pass if `samples` stays at about 1,800 a minute (30 ticks a second), `overBudget` does not rise, and the stages'
`avgMs`, added up (`avgMsSum`), rise by no more than `callsSec` × 100 ns ÷ 30 per tick (est.; `callsSec` is on the
`BOT_CPU_PROCESS` lines: at `callsSec=212534` that is about 0.7 ms). Bot activity moves from minute to minute, so this
is a rough check.

Result:

### 8. Off

`/botcpu off` says "Bot CPU meter off. The last 37 s are being written." (with its own seconds). Then one partial
window, then `BOT_CPU_STATE off source=command`, then no more lines; a minute later the same command still ends with
the `off` line:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -10
```

With `on 30` left running instead, `off source=timer` comes by itself after 30 minutes.

Result:

### 9. On at start

In `~/hearthdaoc/.env`, set `HEARTHDAOC_BOT_CPU=on`, then `./hdc up`. "Bot CPU meter: on" is in the container's start
output, the server log has `BOT_CPU_STATE on source=property`, and the lines come every minute with no command:

```bash
docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -20
```

`/botcpu off` stops it until the next start. Set it back to `HEARTHDAOC_BOT_CPU=off` and `./hdc up`.

Result:

### 10. For #137

Post a few windows (the `gamebot`, `player` and `BOT_CPU_PROCESS` lines) on #137, with the autosave lines of the same
period (the spec's section 7):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep -E "(players|autonomousBots): [0-9]+ in" | tail -6
```

They are the GameBot baseline the player-bot prototype will be compared with.

Result:
