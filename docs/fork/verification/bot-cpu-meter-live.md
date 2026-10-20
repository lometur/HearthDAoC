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
1-5% of one core; if `tps` stays below 30.0 for several minutes, or the `SERVER_WORK` lines' `overBudget` rises, turn
it off with `/botcpu off`.

## What you are testing

The checks prove four things: the update leaves the meter off and changes nothing (check 1); turned on, it writes
sensible numbers every minute, the same in the log, in chat and in the CSV (checks 2-6); it costs little (check 7);
and it turns off by command, by its timer and at a server stop, and on at start from `.env` (checks 8-9). Check 10
collects the numbers #137 needs.

Before this release the meter ran on a test server, started with `docker run` (not `./hdc update` or `./hdc up`): off
by default, then on from its setting at start, first with no bots, then while about 250 GameBots logged in, and stopped
from outside. There the `gamebot`, `monster`, `unattributed`, `BOT_CPU_TOP` and `BOT_CPU_PROCESS` lines, the CSV, the
on line at start and the off line at the stop came out as this guide says. No game client logged in there, and the
GameBots never settled with the meter off, so the `player` line, `BOT_CPU_COMPARE`, the in-game `/botcpu` replies and
its timer have passed only the automated tests, and the two overhead comparisons (before and after the update, meter
off and on) have not run at all: checks 1, 2, 3, 5, 7, 8 and 9 are their first real run. If something looks off there,
treat it as a possible bug and post it on #137.

| Check | What it proves | Pass | Fail |
|---|---|---|---|
| 1. The update | The release adds the setting, off, and a default start changes nothing | `.env` has `HEARTHDAOC_BOT_CPU=off`; "Bot CPU meter: off" once in the start output; no meter line in the log; `SERVER_WORK` as before | "on" anywhere, a `BOT_CPU_STATE` line, or no "Bot CPU meter" line |
| 2. Turn it on | `/botcpu` works for a GM | The two replies quoted in the check | "No such command (/botcpu)", or only the usage lines |
| 3. The lines | It writes a block of lines every minute | A block each minute, with `gamebot` `n` about your bot count | No lines after 2 minutes; `BOT_CPU_ERROR` or `off source=error` |
| 4. Sanity | Its numbers agree with the kernel's | Usually `chargedPct` < `loopThreadsCpuPct` < `processCpuPct` | `chargedPct` higher while the waits are small; `-` on the minutes of a running server |
| 5. In chat | Chat shows the same minute as the log | The same numbers as the log's last block | Other numbers for the same minute |
| 6. The CSV | The CSV gets its rows | A header, then a row per minute and kind | No file, or no new rows |
| 7. Overhead | It costs little | `samples` about 1,800 a minute, `overBudget` not higher, `avgMsSum` within the estimate | `samples` falls or `overBudget` rises while on; `avgMsSum` up by more than the estimate is a softer sign, as bot activity moves |
| 8. Off | Off stops it at once and cleanly | The reply, the last partial minute (or none under 5 s), the off line, then nothing | Lines keep coming |
| 9. On at start | The `.env` switch works | "Bot CPU meter: on", `on source=property`, lines with no command | No lines, or "off" in the start output |
| 10. For #137 | Nothing by itself: it collects the GameBot baseline | | |

## Before you start

- **Measure on a settled server.** After any start (`./hdc update`, `./hdc up`), wait until `./hdc status` says
  running (healthy), then 30 minutes more. The GameBots log in over the first 15 minutes (only a third are in after
  5) and settle by 20 (the spec's section 7), so `gamebot` `n` is low before that, and check 7 needs the next 10
  minutes with the meter off.
- **The meter's faults.** This prints nothing while all is well:

  ```bash
  docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep -E "BOT_CPU_ERROR|Bot CPU meter:"
  ```

  `BOT_CPU_ERROR` turns the meter off; its two warnings (a CSV write that failed, a game-loop call it could not
  measure) start with "Bot CPU meter:", so `grep BOT_CPU` does not show them. Post anything it prints on #137; for a
  `BOT_CPU_ERROR`, add `-A30` after `grep` to bring its stack trace.

## Reading the server log

`./hdc logs` keeps only the last 200 lines, so read the log files themselves, on the server, from `~/hearthdaoc`. The
server starts a new log file at midnight (and at 100 MB) and keeps a week of them, so every command here reads them
all, oldest first:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -40
```

Each line starts with its date and time (`MM/dd HH:mm:ss`), in the server's local time, as `/botcpu` shows times. Each
start also logs one line that loads `/botcpu` for GMs, `Command - '&botcpu' - ... required plvl:2`: it names the
BOT_CPU lines, so `grep BOT_CPU` shows it too, but it is not a line of the meter.

| Line | Written | What to read |
|---|---|---|
| `BOT_CPU` | every minute, one per kind with anything charged, in the kind order above | `n` (the kind's average count), `msPerBotSec` (the average CPU per bot, in ms per second: 1.0 is 0.1% of one core), `msSec` (the whole kind), and after the `\|` the stages, per bot; `unattributed` has `n=-`, and its stages are ms per second for the whole kind |
| `BOT_CPU_TOP` | every minute, one per bot kind with a bot charged | the 5 costliest bots, `rank=name:ms per s`, with each GameBot's goal |
| `BOT_CPU_PROCESS` | every minute | the whole process: `tps`, `processCpuPct`, `loopThreadsCpuPct`, `chargedPct`, `callsSec`, `loopWaitMs`, `throttledMs`, `gcPauseMs` and more; `-` for a value the platform can't give, and for the four thread values (`loopThreadsCpuPct`, `offLoopCpuPct`, `campThreadCpuPct`, `loopWaitMs`) in the window a normal server stop writes, as those threads have ended |
| `BOT_CPU_COMPARE` | every minute, for `player/gamebot` while both have `n` of 1.0 or more (also `playerbot/gamebot` and `piloted/gamebot`, which have no bots today) | `cpu`, the ratio of the two kinds' `msPerBotSec`; `alloc`, `calls` and `usPerCall` |
| `BOT_CPU_STATE` | each time it turns on or off, or its stop time changes | `on source=property` (at start), `on source=command by=<GM> minutes=<m or ->`, `off source=command by=<GM>`, `off source=timer`, `off source=stop` (server stop), `off source=error` |
| `BOT_CPU_ERROR` | a fault in the meter's report, just before `off source=error` (a Warn line) | what failed: post it on #137. The server goes on, and `/botcpu on` starts the meter again |

Every field is explained in the spec's section 2.1, and section 7 says how to read the numbers fairly. To read a few
minutes only, add a time filter right after the `grep`, with the month/day and hour:minute, for example from 14:00 to
14:10 on 21 October: `| awk '$0 >= "10/21 14:00" && $0 < "10/21 14:10"'`.

## The checks

### 1. The update

`./hdc update` lists `HEARTHDAOC_BOT_CPU` among the new settings added to `.env`, and `.env` has
`HEARTHDAOC_BOT_CPU=off`. The update returns before the server has loaded: wait until `./hdc status` says running
(healthy). Then the container's start output has "Bot CPU meter: off" once (the server property's row is new), and the
server log has no line of the meter:

```bash
grep HEARTHDAOC_BOT_CPU ~/hearthdaoc/.env
docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep "Command - '&botcpu'" | tail -1
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | grep -v "Command - '&botcpu'"
```

The third shows today's start loading `/botcpu` for GMs (`required plvl:2`): the new release is running. The fourth
prints nothing: it leaves out that line, which names the BOT_CPU lines in its description.

`SERVER_WORK` as before: 30 minutes after the start, run check 7's command on the 10 minutes before the update and on
the last 10 minutes. Pass if `samples` is about 1,800 a minute in both and `overBudget` is no higher.

Result:

### 2. Turn it on in game

At least 30 minutes after the server is up (see "Before you start"). Spend the last 10 of them where you will be while
the meter is on (logged out, or in the same quiet spot): check 7 compares those minutes with the meter's.

In game, as GM: `/botcpu` says "Bot CPU meter: off. /botcpu on [minutes] turns it on." Then `/botcpu on 30`, which
says "Bot CPU meter on for 30 minutes (until 14:32): BOT_CPU lines every minute in the server log, rows in
logs/bot-cpu.csv." (with your own time, the server's local time). Note the time you turned it on, for check 7.

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
`BOT_CPU_PROCESS` with `tps` at 30.0 (29.9 now and then is fine), and `BOT_CPU_COMPARE pair=player/gamebot` while you
are in game. `companion` has a line while players use companions; `playerbot` and `piloted` have none. The `player`
and `BOT_CPU_COMPARE` lines stop while you are logged out or at the character screen: that is right.

Result:

### 4. Sanity

On the `BOT_CPU_PROCESS` lines, usually `chargedPct` < `loopThreadsCpuPct` < `processCpuPct`; one odd minute is not a
fail. If `chargedPct` is higher, `loopWaitMs`, `throttledMs` or `gcPauseMs` is large too: the wall times are inflated
(the spec's section 3.9). `gamebot` `msSec` / 10 is its share of one core in %. `loopThreadsCpuPct` as `-` on the
minutes of a running server means the kernel gives no scheduler statistics (3.9): say so on #137. After a normal stop
(`./hdc stop`, `./hdc up`, `./hdc update`, `/shutdown`), the block just before `BOT_CPU_STATE off source=stop` can
have `-` there, as the loop's threads have ended: that is not a fault, whatever its `win=`. It is usually the
part-minute the stop writes (`win=` under 60), but `win=60` or `61` when the stop came as a minute ended; a stop
within 5 s of a minute's lines writes no part-minute.

Confirm a large `gcPauseMs` with the `SERVER_GC_PAUSE` lines of the same minutes. Those lines list only pauses of
250 ms or more; shorter pauses add up in the `pauseTotalMs` of the `SERVER_MEMORY` line that covers them (every 5
minutes):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep SERVER_GC_PAUSE | tail -20
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep "SERVER_MEMORY " | tail -3
```

Result:

### 5. In chat

`/botcpu` shows the same last minute as the log's last `BOT_CPU` lines: a status line (`on since ...`, `by command`,
the minutes on, `stops at ...`), the last minute's process line, one line per kind (`gamebot n=...: ... ms/s per bot`)
and the totals since on. Chat shows whole percentages where the log has one decimal, and if a minute closes between
your log read and the command, the two show different minutes. `/botcpu top` lists 5 GameBots with their goals and
what they are doing, such as `1. Aldric (34 Cleric, region 163): 7.412 ms/s, 880 calls/s, RvR, "..."`.

Result:

### 6. The CSV

```bash
docker exec hearthdaoc-server head -1 /data/logs/bot-cpu.csv
docker exec hearthdaoc-server tail -5 /data/logs/bot-cpu.csv
```

The first prints the header, `utc,win_s,tps,cores,process_cpu_pct,...,Quests,Pets,Other`. The second prints the last
rows: one per minute and kind. Their first cell, `utc`, is the window's end in UTC, not the log's local time. To
chart it, copy it out with `docker cp hearthdaoc-server:/data/logs/bot-cpu.csv .`; its stage columns are whole-kind
totals, not per bot.

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

It prints one line a minute. Each line is stamped with the minute its lines were written, so the line of the minute
you turned it on is half before and half on: leave it out. Pass if `samples` stays at about 1,800 a minute (30 ticks a
second), `overBudget` does not rise, and the stages' `avgMs`, added up (`avgMsSum`), rise by no more than `callsSec` ×
100 ns ÷ 30 per tick (est.; `callsSec` is on the `BOT_CPU_PROCESS` lines: at `callsSec=212534` that is about 0.7 ms).
Bot activity moves from minute to minute, so this is a rough check.

Result:

### 8. Off

`/botcpu off` says "Bot CPU meter off. The last 37 s are being written." (with its own seconds). Then one partial
window (its `win=` is those seconds, or one more), then `BOT_CPU_STATE off source=command`, then no more lines; a
minute later the same command still ends with the `off` line:

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -10
```

Within 5 s of a minute's lines the reply is "Bot CPU meter off. The last 3 s were too short to write." instead: then
no partial window comes, and the `off` line follows the last full minute. That is a pass too.

With `on 30` left running instead, `off source=timer` comes by itself after 30 minutes, right after the 30th minute's
lines. If those 30 minutes from check 2 have already passed, `/botcpu off` says "Bot CPU meter is already off." and the
last line is `off source=timer`: that passes the timer part. For the off part, `/botcpu on`, wait a minute, then
`/botcpu off`.

Result:

### 9. On at start

In `~/hearthdaoc/.env`, set `HEARTHDAOC_BOT_CPU=on`, then `./hdc up`. "Bot CPU meter: on" is in the container's start
output, the server log has `BOT_CPU_STATE on source=property`, and the lines come every minute with no command;
`/botcpu` says "by property":

```bash
docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -20
```

`gamebot` `n` is low again for the first 15 minutes after this start. `/botcpu off` stops it until the next start. Set
it back to `HEARTHDAOC_BOT_CPU=off` and `./hdc up`: the start output says "Bot CPU meter: off". If the meter was still
on, the stop ends with `BOT_CPU_STATE off source=stop`; the block just before it can have `-` for the four thread
values (check 4).

Result:

### 10. For #137

Post a few windows (the `gamebot`, `player` and `BOT_CPU_PROCESS` lines) on #137, with the autosave lines of the same
period (the spec's section 7). The autosave's counts have no time of their own: this gives each the time of its
"Saving completed" line, and the last `awk` keeps the period posted (put in your own; here 14:00 to 14:30 on 21
October):

```bash
docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | awk '/Saving completed in/ { t = $1 " " $2 } /(players|autonomousBots): [0-9]+ in/ { print t " " $0 }' | awk '$0 >= "10/21 14:00" && $0 < "10/21 14:30"'
```

They are the GameBot baseline the player-bot prototype will be compared with.

Result:
