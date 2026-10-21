# Bot CPU meter: average CPU per bot, by kind (design)

Status: approved by the owner 2026-10-18, with the default of every ruling (section 11). The owner's decision of
2026-10-14 (section 1) set the scope.
Date: 2026-10-14 (the decision). Fork: `lometur/HearthDAoC`. Branch: `bot-cpu-meter`. Issue #137. Release: merging the
PR publishes the next release; the server picks it up with `./hdc update`, which adds `HEARTHDAOC_BOT_CPU=off` to
`.env`. Nothing changes until a GM turns the meter on. No world data changes. Effort: about 2.5-3 working days with the
owner's check (est.; the research gave 1.5-2 days for the meter, the command, the lines, the CSV and the unit tests,
README section 2.6; this spec adds the one-at-a-time report side, its fault handling and the exact chat replies).

References are to HearthDAoC `main` at 5b5577a (v0.37b-hearth.1), `source/server/GameServer/` unless a path says
otherwise. Shorthand: `GL` = `Managers/GameLoop/GameLoop.cs`, `MT` = `Managers/GameLoop/GameLoopThreadPoolMultiThreaded.cs`,
`NS` = `ECS-Services/NpcService.cs`, `CS` = `ECS-Services/ClientService.cs`, `GSB` = `ECS-Services/GameServiceBase.cs`,
`CPP` = `bots/autonomous/AutonomousCampPlanningPipeline.cs`, `ABR` = `bots/autonomous/AutonomousBotRegistry.cs`, `APP` =
`bots/autonomous/AutonomousPlayerPilot.cs`, `CMB` = `ai/brain/ControlledMob/ControlledMobBrain.cs`. Research:
`~/Games/DAoC/research/player-bots-2026-10-14/` on the owner's machine: `README.md` section 1 and its Verification
section, and `cpu-measurement.md` sections 2-6 ("cpu §n"); `cpu-bench/` holds the clock and wrapper benchmarks. Costs
marked (est.) come from code reading or those local benchmarks, not from a running server.

The references were refreshed on 2026-10-19 from 487b95e (v0.35b-hearth.20) to 5b5577a, after `main` took the
upstream 0.37b sync: the cites that moved, the facts the sync changed and its new memory diagnostics (research
`~/Games/DAoC/research/upstream-037-2026-10-18/performance.md` section 4). The design did not change.

## Read this first

**What the owner gets**

- Every minute while the meter is on, the server log gets one `BOT_CPU` line per kind: the average CPU milliseconds
  per bot per second, split by game-loop stage, with calls, allocation and the spread across bots. Kinds: `gamebot`,
  `companion`, `playerbot`, `piloted`, `player`, `monster`, `unattributed`; pets count with their owner's kind.
- Also each minute: the 5 costliest bots of each bot kind (`BOT_CPU_TOP`), a whole-process line for cross-checking
  (`BOT_CPU_PROCESS`), and a ratio line when two kinds can be compared (`BOT_CPU_COMPARE`).
- The same windows in a CSV, `/data/logs/bot-cpu.csv`, one row per minute and kind, for charts (its stage columns are
  whole-kind totals, 2.3).
- A GM command, `/botcpu`: `on [minutes]`, `off`, the last minute in chat, `top [kind]`, `reset`.
- A switch, `HEARTHDAOC_BOT_CPU` (on or off, default off), to start the meter with the server.
- **Your answer** to "the average cpu per bot and separately ... this new playerpilot bot": `msPerBotSec` on the
  `kind=gamebot` line today; when the prototype runs, the `kind=playerbot` line (its player bots) and the line
  `BOT_CPU_COMPARE pair=playerbot/gamebot`. `piloted` is your old `/bot` mode, which is switched off (section 1).
- **Cost:** while on, an est. 1.4-5.4% of one core (0.35-1.35% of the 4-CPU container, 3.11); off, one bool read per
  `GameLoop.ExecuteForEach` call (about 20 a tick).

**How it works**

- One marked upstream edit is required: `GameLoop.ExecuteForEach` (GL:63-66), the one path from every service to the
  game-loop threads. With the meter on, each callback is timed and charged to the kind of its owner. Off, the edit
  costs one bool read per call.
- One more marked edit (recommended, taken): a scope around the GameBot fleet planning in `NpcService.Tick`
  (NS:28-31), so that work is charged to `gamebot` instead of to nothing.
- The report is built off the loop; only the per-callback timing runs on it. Once a minute the report reads each
  thread's counters and takes the difference from the last minute. Its steps run one at a time, and no fault in them
  reaches the server: a failed CSV write loses one row, any other fault turns the meter off (3.7).

**What it does not do**

- It charges only work done in game-loop callbacks. Work on other threads (timers, the camp planner thread, saves, GC)
  is not charged to a kind; the process line shows how big that share is.
- No forced garbage collection: `/botcpu heap` is not in this PR (section 4.5).
- No player-bot prototype. The prototype (`proto/player-bots`) comes after and builds on this meter; its bots are
  recognised by their client type, so it needs no meter change (section 2.5).

**Upstream:** five lines in two files (GL 2, NS 3), each marked `// HearthDAoC:`. Four new files of our own.

**Rulings the owner answered** (2026-10-18: the default of each; the rest of section 11 is settled from the research
and the code check)

- Ruling 3: `/botcpu on` with no minutes runs until `off` or the next start (default), or for 60 minutes. A meter
  left on costs an est. 1.4-5.4% of one core until then.
- Ruling 4: no `/botcpu heap` on the live server (default); retained heap per bot is left to the prototype.
- Ruling 5: `/botcpu` is for GMs only (default), like `/diag`.
- Ruling 15: the prototype's client marks itself with `IPlayerBotClient` (default), or must derive from
  `BotDummyClient`.
- Ruling 21: the CSV is capped at 50 MB plus one old file, about 20 days of continuous running (default).
- Ruling 26: `piloted` is kept as a kind (default: cheap, ready if `/bot` returns), or folded into `player`, which saves
  the pilot lookup on each player-owned item.

## 1. Goal

The owner can see, on the live server at any time, the average CPU each kind of bot costs, and compare today's
GameBots with the player bots the prototype will add, measured the same way in the same run.

### The owner's words and decisions

- The ask (2026-10-14, issue #137): "is there a way in the server we can note the average cpu per bot and separately
  the average cpu for this new playerpilot bot so that we can easily compare and contrast?"
- The decision (2026-10-14, research question 2, answer b): keep the meter as its own merged PR, off by default
  (`HEARTHDAOC_BOT_CPU`), usable on the live server whenever the owner wants; the throwaway prototype comes after and
  builds on it.

### What the server measures today

- `SERVER_WORK`, every 60 s: the wall time of each of the 17 stages (`Managers/GameLoop/GameLoopWorkMetrics.cs:39-71`,
  the line at :67). Not per entity or kind: a stage's time is its slowest thread's path.
- `SERVER_MEMORY`, every 5 minutes: heap by generation, live data, allocation rate, collections, GC pauses, working
  set and the types that allocate most (`Managers/GameLoop/ServerMemoryDiagnostics.cs:154-161`); `SERVER_GC_PAUSE`,
  a Warn line for each GC pause of 250 ms or more (:30, :409-413). Process-wide: not per entity or kind.
- `/diag perf` and `/diag object` (`ECS-Debug/ECSDebug.cs:307-308`, :336-341): per stage, and live list sizes.
- The autosave log, per kind for free: `players: N in X ms` and `autonomousBots: N in X ms` (`GameServer.cs:1276`,
  :1283).
- Nothing gives CPU per entity or per kind (cpu §2).

### Where this spec departs from the research

The code check for this spec found these; each is settled below and can be changed.

- **`piloted` has no members on the live server.** `/bot` is not a registered command: `BotCommand` carries no
  `[CmdAttribute]` (`bots/BotCommand.cs:10`), and a unit test pins that (`source/server/Tests/UnitTests/UT_PlayerCommandAvailability.cs:22`).
  `AutonomousPlayerPilot.Enable` is reached only from `bots/BotCommand.cs:39` and, through `Toggle` (APP:91-100),
  from :18. The kind stays in the table (section 2.5), but hook H5, whose only use was the pilot's think, is not taken
  (3.6).
- **Logs rotate daily** (`config/logconfig.xml:19-27`), so the owner reads every `server*` file, oldest first, not
  `/data/logs/server.log` alone (2.2).
- **No `cpuToWall` field.** Hook H4 (a per-thread CPU clock in the thread pool) is not taken (3.6). The process line
  instead gives, once a minute and at no cost to the loop, the time the loop threads waited for a CPU, container
  throttling and GC pauses: the three things that make wall time exceed CPU time (3.9).
- **`tps` comes from game time**, not `GameLoop.GetAverageTps()`, which from another thread marshals its work onto the
  game loop (`Managers/GameLoop/GameLoopTickPacerStats.cs:46-51`).
- **The meter walks pet owners itself.** Upstream's `GetLivingOwner` (CMB:176-187) goes through `GetPlayerOwner`
  (CMB:178, :113-143), which sends a companion's pets to the companion's player (CMB:125-126) and can throw on an
  unknown owner (CMB:140). An exception in the wrapper would stop the server
  (`GL:95-101`; MT:316-321, :163-164). So the meter uses its own bounded walk, and its own code never throws (3.3).
- **The owner of each item is read before its callback runs.** A posted action is reset in the callback's `finally`
  and goes back to a pool another thread can take it from (GSB:77-82).
- **`monster` has an `n`:** the live monster brains, which shows how many monsters the players and bots keep awake,
  plus a few encounter controllers that always think (3.8).
- **Per-bot counters live in a `ConditionalWeakTable`**, not a new field on `GameBot` (hook H8 not taken, 3.6).
- **The player bots' contract is written down** (2.5): their client implements a marker interface,
  `IPlayerBotClient`, declared in `BotCpuMeter.cs`, so the research's `HeadlessClient : GameClient` (README section
  2.2) keeps its own real account; and they must be registered in `ClientService`.
- **Safe for live use**, as the owner decided, where the research called the meter a test-world tool (cpu §4.7). Hence
  no forced GC (4.5), a bounded CSV (2.3), no report work on the loop, and a report whose faults never reach the
  server (3.7).
- **Four new files, not two:** the pure report and formatting code is apart from the hot path, so it is unit-tested
  without a server, as the realm-point waits did (`scripts/hearthdaoc/RpTimerWait.cs` and `RpTimerWaitScript.cs`).

## 2. What the owner gets

### 2.1 The log lines

All lines are written once a minute while the meter is on, in this order: the kind lines (kind order of 2.5), the
`BOT_CPU_TOP` lines, `BOT_CPU_PROCESS`, then any `BOT_CPU_COMPARE` lines. Through the server's logger at Info level,
like `SERVER_WORK` (`Managers/GameLoop/GameLoopWorkMetrics.cs:67`), in the invariant culture (a `.` decimal point
whatever the server's culture). The numbers below are made up; only the shape is real.

```
BOT_CPU win=60 kind=gamebot n=238.4 msSec=438.6 msPerBotSec=1.840 callsPerBotSec=412.3 usPerCall=4.46 allocKBPerBotSec=31.2 p50=1.620 p95=3.904 max=7.412 | Timer=0.041 Npc=0.920 Fleet=0.118 Attack=0.210 Casting=0.140 Effect=0.081 EffectListBegin=0.050 EffectListEnd=0.040 Movement=0.150 Reaper=0.020 Zone=0.010 Pets=0.060
BOT_CPU win=60 kind=player n=1.0 msSec=2.1 msPerBotSec=2.104 callsPerBotSec=390.0 usPerCall=5.39 allocKBPerBotSec=60.4 | ClientBegin=0.310 Timer=0.022 Attack=0.201 Movement=0.090 ClientEnd=1.311 Pets=0.170
BOT_CPU win=60 kind=monster n=1830.2 msSec=96.0 msPerBotSec=0.052 callsPerBotSec=61.0 usPerCall=0.86 allocKBPerBotSec=0.4 | Npc=0.031 Attack=0.008 Movement=0.011 Effect=0.002
BOT_CPU win=60 kind=unattributed n=- msSec=9.1 callsSec=2210.0 usPerCall=4.12 allocKBSec=88.0 | GameLoop=0.402 Timer=1.210 Npc=6.890 Zone=0.598
BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:7.412:RvR 2=Brennos:6.020:Battleground 3=Cathal:5.881:GroupPve 4=Dagny:5.104:RvR 5=Eirik:4.950:SoloPve
BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 campThreadCpuPct=3.1 chargedPct=54.6 callsSec=212534 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 allocMBSec=41.2 heapMB=2210 rssMB=3480
BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/238.4 cpu=1.14x alloc=1.94x calls=0.95x usPerCall=1.21x
```

**Kind line (`BOT_CPU`)**

| Field | Meaning | Unit, rounding |
|---|---|---|
| `win` | the window's real length, measured with `Stopwatch` (about 60; shorter for the last window when the meter stops; 2.2) | s, integer |
| `kind` | section 2.5 | |
| `n` | average count of that kind over the window (3.8); `-` for `unattributed` | 1 decimal |
| `msSec` | all CPU charged to the kind, per second of window | ms/s, 1 decimal |
| `msPerBotSec` | `msSec / n`: **the number the owner asked for** | ms per bot per s, 3 decimals |
| `callsPerBotSec` | callbacks charged to the kind / bot-seconds, counting the every-tick visits of brains, timers and clients that find nothing due (NS:61, `ECS-Services/TimerService.cs:54`). It follows how many items (timers, effects, components) a kind has, not how often it thinks | 1 decimal |
| `usPerCall` | charged µs / those callbacks: the average cost of a visit, lower for a kind with many idle items | µs, 2 decimals |
| `allocKBPerBotSec` | bytes allocated inside the kind's callbacks / bot-seconds | KB (1,024 bytes), 1 decimal |
| `p50`, `p95`, `max` | spread of the per-bot ms/s across the kind's bots (3.10); only for `gamebot`, `companion`, `playerbot`, `piloted` | ms per bot per s, 3 decimals |
| after `\|` | one `Stage=value` per stage (3.4) in tick order, left out when it rounds to 0.000 | ms per bot per s, 3 decimals |

- 1.0 ms per bot per second is 0.1% of one core: 240 bots at 1.0 use 24% of one core.
- `unattributed` has no `n`: it shows `callsSec` and `allocKBSec` instead of the per-bot fields, and its stage values
  are ms per second.
- A line's stage values add up to its `msPerBotSec` (`msSec` for `unattributed`), within rounding: every charged item
  has one stage, and `Fleet` and `Pets` are stages.
- **`-` when there is nothing to divide by:** every per-bot field (`msPerBotSec`, `callsPerBotSec`,
  `allocKBPerBotSec`, `p50`, `p95`, `max`) is `-` when the kind's bot-seconds are 0, and the line then has no part
  after `|` (`msSec` still shows the cost; the CSV keeps the stage totals, 2.3). `usPerCall` is `-` when its calls are
  0. Never `NaN`, `Infinity` or 0. The chat lines (2.4) show the same `-`.
- A kind with nothing charged and `n` 0 writes no line. With the live server's roster that is usually `playerbot`
  and `piloted` (section 2.5).

**`BOT_CPU_TOP`**: one line per bot kind (`gamebot`, `companion`, `playerbot`, `piloted`) with any bot charged in the
window: `rank=name:ms per s`, ranked by ms (3 decimals), ties by name. For GameBots a third part is the goal,
`PersistentRecord.ObjectiveKind` (`bots/autonomous/AutonomousBotEconomy.cs:965`), or `-`. A space or `:` in a name
becomes `_`. The goal's activity text, the level and the region are in `/botcpu top` (2.4).

**`BOT_CPU_PROCESS`** (how each is read: 3.9)

| Field | Meaning | Unit |
|---|---|---|
| `tps` | game-loop ticks per second | 1 decimal |
| `cores` | `Environment.ProcessorCount` (the container's `cpus`, `deploy/compose.yml:38`) | integer |
| `processCpuPct` | the whole server process | % of one core, 1 decimal |
| `loopThreadsCpuPct` | the `GameLoop` thread and its `GameLoop_Worker_<n>` threads (MT:100), including the loop's own busy wait at the end of each tick | % of one core, 1 decimal |
| `offLoopCpuPct` | `processCpuPct - loopThreadsCpuPct`: timers, the camp planner thread, saves, GC, network | % of one core, 1 decimal |
| `campThreadCpuPct` | the `CampSnapshotPlanner` thread (CPP:52): one part of the GameBot-only work off the loop (3.9) | % of one core, 1 decimal |
| `chargedPct` | all time charged to all kinds, `unattributed` included | % of one core, 1 decimal |
| `callsSec` | all metered callbacks per second; overhead = this × about 100 ns (3.11) | integer |
| `loopWaitMs` | time the loop threads were ready to run but waited for a CPU | ms, integer |
| `throttledMs` | container CPU throttling | ms, integer |
| `gcPauseMs`, `gen2` | GC pause time and full collections in the window | ms, integer; count |
| `allocMBSec` | all allocation in the process; `-` in a window the runtime's counter stood still in (3.9) | MB/s, 1 decimal |
| `heapMB`, `rssMB` | managed heap (`GC.GetTotalMemory(false)`, as `SERVER_STALL` reads it, `Managers/GameLoop/GameLoopWorkMetrics.cs:93`) and resident memory; no forced collection | MB, integer |

A value the platform can't give (Windows, a file missing in the container, a schedstat that reads 0, or an
allocation counter that stood still, 3.9) is `-`.

`heapMB` is `GC.GetTotalMemory(false)`, as in `SERVER_STALL` `heapMB` and `SERVER_GC_PAUSE` `totalMemoryMB`
(`Managers/GameLoop/ServerMemoryDiagnostics.cs:413`), not `SERVER_MEMORY`'s `heapMB` (:155, the heap at the last
collection with its free space, `GCMemoryInfo.HeapSizeBytes`); for leaks, read `SERVER_MEMORY` `liveMB`, what the
last full collection left alive (:310-316).

**`BOT_CPU_COMPARE`**: one line for each of the pairs `playerbot/gamebot`, `piloted/gamebot` and `player/gamebot` whose
two kinds both have `n` of at least 1.0 in the window. `cpu`, `alloc`, `calls` and `usPerCall` are the ratios of
`msPerBotSec`, `allocKBPerBotSec`, `callsPerBotSec` and `usPerCall`, 2 decimals and `x`; `-` when either value is `-`
or the gamebot value is 0. `calls` compares items per bot (timers, effects, components), not think rates (the
`callsPerBotSec` row). On the live server only `player/gamebot` appears (while a GM is in game); read it with the
caveats of section 7.

**`BOT_CPU_STATE`**: one line each time the meter turns on or off, or its stop time changes:
`BOT_CPU_STATE on source=property` at start, `on source=command by=<GM> minutes=<m or ->` (also written when an `on`
changes the stop time of a running meter), `off source=command by=<GM>`, `off source=timer`, `off source=stop`
(server shutdown), `off source=error`. An `off` comes after the last window's lines (3.7). Before `off source=error`,
one Warn line `BOT_CPU_ERROR <step>: <exception>` says what failed.

### 2.2 How often, and where

- **Windows of 60 s,** counted from the moment the meter turns on. When it turns off, the report writes the partial
  window with its real `win`, off the loop (3.7), within about a second (est.).
- **Window length.** Every per-second figure divides by the window's exact `Stopwatch` length; `win` is that length
  rounded, at least 1. A final window shorter than 5 s writes no `BOT_CPU` lines or CSV rows, only the `BOT_CPU_STATE`
  line, and its counts are dropped. GameBot `n` in a window with no registry sample (3.8) uses the last sample.
- **In the server log**, `/data/logs/server.log` in the volume. It rotates every day and above 100 MB and keeps 7 days
  (`config/logconfig.xml:19-27`), so read every file, oldest first:

  ```bash
  docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -40
  ```

  `./hdc logs` starts at the last 200 lines (`deploy/hdc:183`) and also shows the lines as they come.

### 2.3 The CSV

`/data/logs/bot-cpu.csv`: the server writes `logs/bot-cpu.csv` next to its own folder (`AppContext.BaseDirectory`),
and in the image that `logs` folder is the volume's (`deploy/entrypoint.sh:66`); a Windows launcher build would write
it next to its own logs (est.). Read it with `docker exec hearthdaoc-server cat /data/logs/bot-cpu.csv` or copy it out with
`docker cp hearthdaoc-server:/data/logs/bot-cpu.csv .`.

- One row per window and kind, the same rows as the kind lines; the window's process values are repeated on each of
  its rows, so one table charts everything.
- Header. Before the first append after the meter turns on: if the file is missing, empty, or its first line is not
  this header (an older release's columns), any old file is renamed `bot-cpu.csv.1` (replacing an older one) and a
  new file starts with the header:

  ```
  utc,win_s,tps,cores,process_cpu_pct,loop_threads_cpu_pct,off_loop_cpu_pct,camp_thread_cpu_pct,charged_pct,all_calls_sec,loop_wait_ms,throttled_ms,gc_pause_ms,gen2,alloc_mb_sec,heap_mb,rss_mb,kind,n,ms_sec,calls_sec,alloc_kb_sec,ms_per_bot_sec,calls_per_bot_sec,us_per_call,alloc_kb_per_bot_sec,p50,p95,max,GameLoop,Timer,ClientBegin,Npc,Fleet,Attack,Casting,Effect,EffectListBegin,EffectListEnd,Movement,Crafting,Reaper,Zone,ClientEnd,Quests,Pets,Other
  ```

- `utc` is the window's end, `2026-10-18T19:02:10Z`. Numbers use the rounding of 2.1 and a `.` decimal point; a value
  that is `-` in the log is an empty cell. Columns the log's kind line does not carry: `calls_sec` and `alloc_kb_sec`
  have 1 decimal; a field the row's kind has no value for (`p50`, `p95`, `max` for `player`, `monster` and
  `unattributed`; `n` and the per-bot fields for `unattributed`) is an empty cell.
- **The stage columns are ms per second for the whole kind**, not per bot (divide by `n` for per bot): the kind's raw
  stage ticks ÷ the exact window length, 3 decimals, `0.000` when zero (never left out). They are computed from the
  raw ticks, not from the log's rounded per-bot values. That keeps one meaning for every row, `unattributed` and a
  kind with `n` 0 included, and stacks well in a chart. The log's stage values are per bot (2.1).
- Written off the game loop, appended once a minute. Above 50 MB the file is renamed `bot-cpu.csv.1` (replacing an
  older one) and a new file starts: about 20 days of continuous running (est., 2.5 MB a day; section 9, log volume).
  A failed write, whatever the exception (not only an I/O one, unlike `bot-ai-delay.json`,
  `Managers/GameLoop/GameLoopWorkMetrics.cs:102`), logs one warning and the meter goes on; the row is lost.

### 2.4 The GM command `/botcpu`

`&botcpu`, `ePrivLevel.GM`, a new HearthDAoC file in the pattern of `/epic` (`scripts/hearthdaoc/EpicCommand.cs:14-21`)
and `/diag` (`ECS-Debug/ECSDebug.cs:307-308`, also GM). Commands resolve an exact name first and guess prefixes only
among commands the caller may use (`gameutils/ScriptMgr.cs:109-125`), so players' abbreviations are not affected. For
a GM, `/bot ...` now runs `/botcpu ...`: no other command starts with `&bot`, and `/bot` itself is not registered
(section 1). `/bot` alone only shows the status; `/bot on` turns the meter on.

The attribute:

```csharp
[CmdAttribute(
    "&botcpu",
    ePrivLevel.GM,
    "HearthDAoC: measure the game loop's CPU per kind of bot (BOT_CPU lines in the server log)",
    "/botcpu - status, the last minute and the totals since on",
    "/botcpu on [minutes] - start, for 1-1440 minutes, or until off or the next start",
    "/botcpu off - stop and write the last partial minute",
    "/botcpu top [gamebot|companion|playerbot|piloted] - the 5 costliest bots of the last minute (gamebot if none given)",
    "/botcpu reset - start the totals since on again")]
```

| Command | State | Chat output (exact text; times are examples) |
|---|---|---|
| `/botcpu` | off | "Bot CPU meter: off. /botcpu on [minutes] turns it on." |
| `/botcpu` | on, no window yet | the status line, then "No full minute yet; the first lines come at 14:03." |
| `/botcpu` | on | the lines below |
| `/botcpu on 30` | off | "Bot CPU meter on for 30 minutes (until 14:32): BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv." |
| `/botcpu on` | off | "Bot CPU meter on until /botcpu off or the next start: BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv." |
| `/botcpu on 30` | on | "Bot CPU meter already on; it now stops at 14:32." |
| `/botcpu on` | on | "Bot CPU meter already on; it now runs until /botcpu off or the next start." |
| `/botcpu off` | on | "Bot CPU meter off. The last 37 s are being written." Under 5 s (2.2): "Bot CPU meter off. The last 3 s were too short to write." |
| `/botcpu off` | off | "Bot CPU meter is already off." |
| `/botcpu top [kind]` | on, a window written | one line per bot (below), at most 5, for `gamebot` when no kind is given; none of that kind charged: "No gamebot was charged in the last minute." |
| `/botcpu top [kind]` | off; on, no window yet | the off text; "No full minute yet; the first lines come at 14:03." |
| `/botcpu reset` | on; off | "Bot CPU totals reset."; the off text |
| anything else, a kind that is not a bot kind, minutes out of range | any | the syntax lines of the attribute |

- `on` starts the meter until `off`, the next start, or after `minutes`. On a running meter it sets or clears the
  stop time and writes `BOT_CPU_STATE on source=command by=<GM> minutes=<m or ->` again. `reset` starts the totals
  since on again; the windows go on.
- Times in chat are the server's local time (`TZ`, `deploy/compose.yml:27`), as in the server log
  (`config/logconfig.xml:20`); the CSV's `utc` is UTC.
- The command never builds a report: it reads the last result the report published (3.7) and, for a change, queues a
  job for the report and answers at once. The chat text is made in `BotCpuReport`, so the tests pin it.

`/botcpu`, on:

```
Bot CPU meter: on since 14:02 (by command, 12 min; stops at 14:32).
Last minute (60 s, 30.0 ticks/s): process 148% of one core (4 cores), 55% charged to kinds, 27% off the loop.
gamebot n=238.4: 1.840 ms/s per bot (438.6 ms/s in all); most in Npc 0.920
player n=1.0: 2.104 ms/s per bot (2.1 ms/s in all); most in ClientEnd 1.311
monster n=1830.2: 0.052 ms/s per bot (96.0 ms/s in all); most in Npc 0.031
unattributed: 9.1 ms/s; most in Npc 6.890
Since on (12 min): gamebot 1.792, player 2.020 ms/s per bot.
```

- The status line: `by command` or `by property`; `stops at <time>` or `no stop time`.
- The last-minute line: `Last minute (<win> s, <tps> ticks/s): process <processCpuPct>% of one core (<cores> cores),
  <chargedPct>% charged to kinds, <offLoopCpuPct>% off the loop.`, the percentages rounded to whole numbers.
- One line per kind that has a kind line in the last window, in kind order (2.5): `<kind> n=<n>: <msPerBotSec> ms/s
  per bot (<msSec> ms/s in all); most in <stage> <value>`, the largest stage value; for `unattributed`:
  `unattributed: <msSec> ms/s; most in <stage> <value>`. A `-` (2.1) shows as `-`.
- `Since on (N min)`, or `Since reset (N min)` after a `reset`: for each kind with bot-seconds above 0, in kind order,
  the sum of its charged ms ÷ the sum of its bot-seconds over the windows written since on or reset. The current
  partial window is not counted; `N` is those windows' length in minutes. With no window yet the line is left out.

`/botcpu top gamebot`, one line per bot: `1. Aldric (34 Cleric, region 163): 7.412 ms/s, 880 calls/s, RvR, "<activity>"`
(the goal and `PersistentRecord.Activity`, as the slow-turn warning logs them, NS:81; `-` and `""` when the bot has
no `PersistentRecord`). For the other bot kinds: `1. <name> (<level> <class>, region <region>): <ms> ms/s, <calls>
calls/s`. `calls/s` counts visits as in 2.1.

### 2.5 The kinds

| Owner (3.3) | Kind | On the live server |
|---|---|---|
| `GameBot` with `IsAutonomousWorldBot` (`bots/GameBot.cs:47`) | `gamebot` | about 240 |
| any other `GameBot`: companions and temporary helpers (`/spawn`, `/grind`, `/raid`) | `companion` | when players use them |
| `GamePlayer` whose `Client` implements `IPlayerBotClient` (a marker interface in `BotCpuMeter.cs`) or is a `BotDummyClient` or derives from it (`bots/BotDummyClient.cs:6-13`) | `playerbot` | none: nothing gives a `GamePlayer` such a client today (`BotDummyClient` is used only inside `GameBot`, `bots/GameBot.cs:2269`, :2348) |
| `GamePlayer` that `AutonomousPlayerPilot.IsActive` (APP:89) | `piloted` | none: `/bot` is not registered (section 1) |
| any other `GamePlayer` | `player` | the owner and any real players |
| any other living: NPCs (monsters, guards, merchants), keep doors (`gameobjects/GameDoorBase.cs:13`) | `monster` | live brains in the thousands (est.) |
| a pet: a `GameNPC` whose brain is an `IControlledBrain` with an owner | its owner's kind, stage `Pets` | |
| none, or not living (an item, a static object), or an item type the meter does not know | `unattributed` | posted work, login screens, ownerless timers |

**The player-bot contract** (for the prototype; this PR changes nothing for it): a player bot is recognised as
`playerbot` with no meter change if its client implements `IPlayerBotClient` (`public interface IPlayerBotClient { }`
in `DOL.GS.HearthDAoC`). Deriving from `BotDummyClient` also works, but its constructor gives every client a
throwaway `DbAccount` with `PrivLevel` Player and fires `AccountLoaded` (`bots/BotDummyClient.cs:8-13`,
`GameClient.cs:25-33`), while a player bot needs a real account of its own (README Verification row 19). It is
counted in `n` only if its client is registered in `ClientService` and playing (3.8), which the research's fair-run
rules ask for anyway (README section 2.4). A driver ticked by an `ECSGameTimer` it owns is charged to it by
TimerService with no hook (`ECS-Services/TimerService.cs:75`).

## 3. The design

### 3.1 Hook H1: `GameLoop.ExecuteForEach` (required)

Every service hands its per-entity work to the thread pool through this one method (GL:63-66): all 17 stages
(GL:146-162), posted actions (GSB:62) and the camp planner (CPP:81); 15 call sites in all. It becomes:

```csharp
        public static void ExecuteForEach<T>(List<T> items, int toExclusive, Action<T> action)
        {
            // HearthDAoC: the per-kind CPU meter (#137) times each callback while it is on; off, this is one bool read.
            if (HearthDAoC.BotCpuMeter.Enabled) { HearthDAoC.BotCpuMeter.ExecuteForEach(_threadPool, items, toExclusive, action); return; }
            _threadPool.ExecuteForEach(items, toExclusive, action);
        }
```

- Off: one static bool read, about 20 times a tick (est.); the pool gets the same delegate as today.
- On, the meter's `ExecuteForEach` reads the stage once from `GameLoop.ActiveService` (GL:25, set at :125 before the
  stage runs; every call comes from the game-loop thread: the pool holds one batch at a time in shared fields,
  MT:39-43) and the "in fleet" flag and run number of 3.5, also once, on the game-loop thread. It stores them in the
  wrapper, so worker threads never read the flag. It then calls the pool's own public `ExecuteForEach`
  (`Managers/GameLoop/GameLoopThreadPool.cs:19`) with the wrapper. It owns the pool call so it can time a nested call
  inside the fleet scope (3.5); that time is recorded in a `finally`, and an exception from the pool goes on to the
  caller unchanged. An empty list is passed through unwrapped.
- **The per-call setup never throws.** Reading the stage and the flag and building the wrapper run inside a `try`
  that covers only that setup, never the pool call. On any exception the meter logs one warning (the log call in its
  own `try`) and calls `pool.ExecuteForEach(items, toExclusive, action)` with the original delegate: nothing has run
  yet, so each item still runs exactly once, unmetered. An exception from a callback reaches the service exactly as
  today.
- The wrapper covers every path the pool has: parallel chunks (MT:310-322), the one-item path on the game-loop thread
  (MT:128-133) and the single-CPU pool (`Managers/GameLoop/GameLoopThreadPoolSingleThreaded.cs:10-16`).
- Its cost: one small wrapper object and delegate per call, about 600 a second (est.); nothing per item.

### 3.2 The wrapper, per item

1. **Find the owner, its kind and the stage** (3.3, 3.4), and for a bot its per-bot counter (3.10). All of this runs
   before the callback, inside a `try`, and so does the thread's first-use slot registration (3.7); any exception
   charges the item to `unattributed`, or, with no slot, leaves it uncharged. The meter's own code never throws into
   the loop, where an exception stops the server (GL:95-101).
2. Read `GC.GetAllocatedBytesForCurrentThread()` and `Stopwatch.GetTimestamp()`.
3. Run the callback, outside the `try`, so each service's own exception handling is unchanged.
4. Read both again.
5. Add the ticks, the bytes and one call to this thread's slot for `[kind, stage]` (3.7); for a presence item, add one
   to the kind's presence count (3.8).
6. For a bot, add the ticks and one call to its counter with `Interlocked.Add`.

- Steps 5 and 6 are only array adds and `Interlocked.Add` on a counter step 1 found (or none): they cannot throw. The
  kind index comes from the classifier's fixed set and the stage index is checked when the wrapper is built.
- Measured: 80 ns of overhead per item for this wrapper, with a per-bot counter held in a field
  (`cpu-bench/WrapperCost/Program.cs`, re-run in the research Verification row 3); 0 bytes allocated over 1,000,000
  `int` items. The benchmark runs on one thread, so contention between loop threads on the per-bot counters is not
  measured (est. small with the padding of 3.10). The `ConditionalWeakTable` lookup adds an est. 20-50 ns on
  bot-owned items, the pilot check an est. 10-20 ns on player-owned items: about 90-120 ns on average (est., 3.11).
- The meter's own time (steps 1, 5, 6) falls outside the timed part: it is charged to no kind; its size is `callsSec`
  × about 100 ns (3.11). The gap between `loopThreadsCpuPct` and `chargedPct` also holds the loop's busy wait, the
  barrier waits and the list updates (3.9), so it does not measure the overhead.

### 3.3 Owner and kind

The owner function is chosen once per item type `T` (a static generic cache), not tested per item. A value type such
as the camp planner's `int` (CPP:22) has no owner and is never boxed.

| Item (stage) | Owner |
|---|---|
| `ABrain` (Npc) | `Body` (`ai/ABrain.cs:15`) |
| `AttackComponent` (Attack) | `owner` (`ECS-Components/AttackComponent.cs:22`) |
| `CastingComponent` (Casting) | `Owner` (`ECS-Components/CastingComponent.cs:28`) |
| `ECSGameEffect` (Effect) | `Owner`, the living that carries it (`ECS-Effects/ECSGameEffect.cs:24`) |
| `EffectListComponent` (EffectListBegin, EffectListEnd) | `Owner` (`ECS-Components/EffectListComponent.cs:34`) |
| `MovementComponent` (Movement) | `Owner` (`ECS-Components/MovementComponent.cs:15`) |
| `CraftComponent` (Crafting) | `Owner` (`ECS-Components/CraftComponent.cs:8`) |
| `ECSGameTimer` (Timer) | `Owner`, may be null (`ECS-Services/TimerService.cs:75`) |
| `SubZoneObject` (Zone) | `Node.Value` (`world/SubZone.cs:86`) |
| `LivingBeingKilled` (Reaper) | the `Killer` when its kind, after the pet walk, is `gamebot`, `companion`, `playerbot`, `piloted` or `player`: XP, loot and quest credit run for the killer (`ECS-Services/ReaperService.cs:55`, :84-85). Otherwise `Killed` |
| `GameClient` (ClientBegin, ClientEnd) | `Player` (`GameClient.cs:56`) when `client.IsPlaying` (Playing or Linkdead, `GameClient.cs:79`) and `Player.ObjectState` is Active; otherwise none. A client back at the character screen keeps its old `Player` (only `LoadPlayer` sets it, `GameClient.cs:168`; `KickPlayerToCharScreen` leaves it, `ECS-Services/GameServiceUtils.cs:73-87`), and is still visited (CS:76-84) |
| a posted action, `int`, anything else | none |

**Kind of an owner,** tested in this order:

1. `GameBot`: `gamebot` if `IsAutonomousWorldBot`, else `companion`. First, because `BotBrain` is itself an
   `IControlledBrain` (`bots/BotBrain.cs:21`).
2. `GamePlayer`: `playerbot` if its `Client` is an `IPlayerBotClient` or a `BotDummyClient` (one type test each, est.
   a few ns); else `piloted` if `AutonomousPlayerPilot.IsActive` (APP:89, a dictionary read); else `player`.
3. `GameNPC` whose `Brain` (`gameobjects/GameNPC.cs:2244-2253`, no lock) is an `IControlledBrain` with an `Owner`
   (`ai/brain/IControlledBrain.cs:67`): a pet. The meter walks `Owner` up to the first owner that is not itself a pet
   (a `GameBot`, a `GamePlayer`, or an NPC without a controlled brain), at most 8 steps. The item is charged to that
   owner's kind under stage `Pets`, and to that owner's per-bot counter. A walk longer than 8 steps (a cycle) charges
   `unattributed`. Upstream's `GetLivingOwner` (CMB:176-187) is not used: it goes through `GetPlayerOwner`, which
   gives a companion's pets to the companion's player (CMB:125-126) and can throw (CMB:140).
4. Any other `GameLiving`: `monster`.
5. Anything else, or none: `unattributed`.

The pilot check is a parameter of the classifier's pure core, so the unit tests need no pilot.

### 3.4 Stages

The stage comes from `GameLoop.ActiveService`, the stage's profile key: the service's class name, plus `.BeginTick` or
`.EndTick` for those steps (GL:168-169; `ServiceName` is the class name, GSB:24).

| Column | Profile key | What lands there |
|---|---|---|
| `GameLoop` | `GameLoopService` | posted actions only (`ECS-Services/GameLoopService.cs:12-15`) |
| `Timer` | `TimerService` | `ECSGameTimer`s; the prototype's driver |
| `ClientBegin` | `ClientService.BeginTick` | a client's packets and commands, ping and idle checks (CS:70-111) |
| `Npc` | `NpcService` | brains: a GameBot's think (`BotBrain.Think`), monsters' AI |
| `Fleet` | none: the H6 scope inside `NpcService` (3.5) | GameBot fleet planning, charged to `gamebot` |
| `Attack`, `Casting`, `Effect` | `AttackService`, `CastingService`, `EffectService` | |
| `EffectListBegin`, `EffectListEnd` | `EffectListService.BeginTick`, `.EndTick` | |
| `Movement`, `Crafting`, `Reaper`, `Zone` | `MovementService`, `CraftingService`, `ReaperService`, `ZoneService` | |
| `ClientEnd` | `ClientService.EndTick` | `UpdateWorld` once a second and the sends (CS:113-148) |
| `Quests` | `DailyQuestService`, `WeeklyQuestService`, `MonthlyQuestService` | Daily and Weekly posted actions (`ECS-Services/DailyQuestService.cs:36`, `ECS-Services/WeeklyQuestService.cs:36`); Monthly processes none (`ECS-Services/MonthlyQuestService.cs:34-36`). Their rollover loops are serial (3.9) |
| `Pets` | any | pet-owned items, whatever the stage (3.3) |
| `Other` | any other key | a stage upstream adds later |

Posted actions stay in the stage of the service they were posted to, as `unattributed` (3.6).

### 3.5 Hook H6: the GameBot fleet planning scope (recommended, taken)

Before the brains, `NpcService.Tick` runs four serial calls on the game-loop thread: population sampling, RvR planning,
group coordination and camp planning (NS:28-31). They are GameBot work that belongs to no single bot. Without a scope
they are charged to nothing, and the GameBot average would leave them out. NS:27-32 becomes:

```csharp
            ProcessPostedActionsParallel();
            // HearthDAoC: the per-kind CPU meter (#137) charges the GameBot fleet planning below to gamebot, stage Fleet.
            long fleetStart = HearthDAoC.BotCpuMeter.BeginFleet();
            AutonomousBotRegistry.PrepareBrainTick();
            AutonomousWorldBotController.PrepareRvrPlanningTick();
            AutonomousBotGroupCoordinator.PrepareCoordinatorTick();
            AutonomousWorldBotController.PrepareCampPlanningTick();
            HearthDAoC.BotCpuMeter.EndFleet(fleetStart); // HearthDAoC: end of the fleet scope.
```

- `BeginFleet` reads `Enabled`; off, it returns 0 and `EndFleet(0)` does nothing. On, it clears any stale "in fleet"
  flag, notes the time, the thread's allocated bytes and the run number (3.7), and sets the flag. Only the game-loop
  thread reads or writes the flag; the wrapper carries its value to the workers (3.1).
- `EndFleet(start)` with `start` not 0 always clears the flag, whatever `Enabled` is now. It charges the scope only if
  the meter is still on and in the run the scope started in; if the meter turned off (or off and on) inside the
  scope, it drops it. So a scope cut by `off` never leaves the flag set, and the next run's first `int` batch outside
  a scope is not charged to `Fleet`.
- **The nested call.** Camp planning runs its own `ExecuteForEach` inside the scope (CPP:81, reached from NS:31 through
  CPP:38-47), over `int` items (CPP:22). The double-count rule:
  - each nested item is charged by the wrapper as usual; with no owner, inside the scope, it goes to `gamebot` /
    `Fleet`;
  - the meter times the whole nested pool call on the game-loop thread, from entering it to its return or throw (in a
    `finally`), barrier wait included, and sums those times (and that thread's bytes) for the scope;
  - `EndFleet` charges `gamebot` / `Fleet` one call with: the scope's time minus the nested calls' time, and the
    scope's bytes minus the nested calls' bytes;
  - so the fleet total is the scope's own serial work plus the nested items' own times. The barrier wait is not CPU
    and is charged to nothing; nothing is counted twice.
- If one of the four calls throws, the exception stops the server as today (GL:95-101); camp planning catches its own
  (CPP:40-47).
- `Fleet` is in `gamebot`'s average and its `Fleet` column, but in no single bot's spread or top 5 (3.10).

### 3.6 The other hooks: not taken

| Hook (cpu §4.5) | Ruling | Why |
|---|---|---|
| H5, posted actions (GSB:94-122) | **not taken.** Posted work goes to `unattributed` in its service's stage | `PostedAction` is private (GSB:94). Its one use was the `/bot` pilot's think, posted to NpcService (APP:226), and `/bot` is not registered (section 1). The prototype's driver ticks on a timer instead. If `/bot` is registered again, the pilot's think shows in `unattributed` / `Npc` until H5 is added (2 lines plus one for `PilotState`, APP:36). The v0.37b sync adds two GameBot jobs posted to NpcService: the ambient guild chat check once a minute (`bots/autonomous/AutonomousBotGuilds.cs:76`) and the guild roster check at each roster refresh, every 8-30 s (`bots/autonomous/AutonomousPopulationController.cs:338`, :326). They land in `unattributed` / `Npc`. Both return at once while no GameBot is in a guild (`bots/autonomous/AutonomousBotGuilds.cs:223`, :316). The ruling stands |
| H4, thread CPU clock per batch (MT:285-329, :128-133) | **not taken** | it needs the server's first libc P/Invoke (`clock_gettime`, Linux-only, so the Windows launcher build needs a fallback, cpu §3), an edit in the pool's hot loop, and costs an est. 3 ms a second (cpu §4.6). Once-a-minute reads of each loop thread's scheduler statistics give the same warning at no cost to the loop (3.9) |
| H8, a counter field on `GameBot` | **not taken.** A `ConditionalWeakTable` holds the per-bot counters | no edit to `bots/GameBot.cs`, an upstream file of 4,735 lines, LF since the v0.37b sync (it was CRLF; an H8 line would be LF too); an est. 20-50 ns per bot-owned item (cpu §4.5). If the measured overhead (`callsSec`, 3.11) matters, H8 is a one-property follow-up |

### 3.7 Per-thread slots, and the report off the loop

- **Slots.** Each game-loop thread has one `[ThreadStatic]` slot: a `long[]` of 7 kinds × 18 stages × 3 values (ticks,
  calls, bytes) plus 7 presence counts, 385 longs, with 8 unused longs at each end (401 longs, about 3.2 KB). Only its
  own thread writes it, with plain adds: no locks, and the padding keeps two threads' slots off a shared cache line.
  A thread registers its slot in a global list on first use (under the slot-list lock, once per thread, inside step
  1's `try`, 3.2). Slots are never removed or zeroed; a worker the watchdog restarts (MT:214-231) gets a new slot, and
  the old one keeps its totals.
- **The report side** is a 1-second `System.Threading.Timer`, created when a run opens and disposed when it closes,
  plus the jobs below. Nothing of it runs on the game loop.
- **One at a time.** Every change to the report's state (a window, on, off, reset, a new stop time, the stop time
  reached, server stop, an error) runs under one report lock, taken only on the report side.
  - A command (on, off, reset, a new stop time) does only this on its game-loop thread: for `off`,
    `BotCpuMeter.Disable()` (a `Volatile.Write` of `Enabled`, so metering stops at once); then it puts a job in a
    first-in-first-out queue (`ConcurrentQueue`) and calls `System.Threading.ThreadPool.QueueUserWorkItem`, as
    `PublishBotAiDelay` does (`Managers/GameLoop/GameLoopWorkMetrics.cs:98-104`). No file, `/proc` read or log write
    happens on the loop, and the game-loop thread never takes the report lock.
  - The queued work item takes the lock, waiting for it; a timer tick takes it with `Monitor.TryEnter` and skips that
    second if it is held. That replaces a guard flag like the pilot's (APP:206-220), which only keeps one tick from
    overlapping the next. Whoever holds the lock first runs every job waiting in the queue, in order, then its own
    work.
  - Each run has a number, counted up by `BotCpuMeter.Enable()`. A tick, or an off, reset or stop-time job, for a run
    that is already closed does nothing (an `on` job then opens a new run), and a closed run takes no more windows.
    Closing a run (off, the stop time, server stop) writes the final window, then the `BOT_CPU_STATE off` line, then
    disposes the timer, all under the lock; a tick already waiting finds the run closed and returns. So a window is
    never written twice, and no window line comes after its `off`.
  - `Enabled` goes true only in an open job, through `BotCpuMeter.Enable()`, which first makes the slot list (the
    first time), a new per-bot table and the next run number, so no wrapper runs before they exist. It goes false in
    an `off` command, in a close and on an error. Because the queue keeps order, `off` then `on` closes the old run
    before it opens the new one, and `on` then `off` ends off.
  - The command reads only the last result the report published: an immutable object (the last window, the top 5 of
    each bot kind, the since-on totals, the open window's start) that the report swaps in with `Volatile.Write`.
  - The started and stopped handlers (4.1) run their job at once on their own thread, under the lock, not queued:
    the started event comes before the loop starts (`GameServer.cs:510`, :517), the stopped event after
    `GameLoop.Exit` (:1013, :1015), so neither holds a tick.
- **No exception escapes the report.** An exception that escapes a thread-pool callback ends the process, and NLog
  here throws from log calls (`throwExceptions="true"`, `config/logconfig.xml:2`). So every timer tick, every queued
  job and both server event handlers run inside `try { ... } catch (Exception)`. On the first exception: `Enabled`
  false, the timer disposed, the run closed with no final window (its counts are dropped), one Warn line
  `BOT_CPU_ERROR <step>: <exception>`, then `BOT_CPU_STATE off source=error`; each of those log calls sits in its own
  `try` whose `catch` does nothing. It never rethrows. The CSV write has its own catch (2.3) and does not turn the
  meter off. A missing `/proc` file or a bot with no `PersistentRecord` is not a fault: it gives `-` (3.9, 2.4).
- **A window,** under the lock:
  1. take the slot list (under the slot-list lock, a copy of the references);
  2. read every value with `Volatile.Read` and add them up per kind and stage (aligned 64-bit reads are atomic on the
     64-bit server, est.);
  3. subtract the previous total: that is the window. The report never writes a slot;
  4. keep this total for the next window.
- **Not one instant.** The slots are read one after another without a lock. A callback that ends during the read
  lands in this window or the next, never in both and never lost: a few µs at a window's edge.
- **On, off and reset.** Turning on takes a fresh baseline total and starts a new per-bot table, so counts from an
  earlier run never leak in. Wrappers already in flight when the meter turns off still add their items; they fall
  before the next baseline. `reset` replaces only the "since on" baseline.
- **Its cost** (est.): reading 401 longs from each of the 4 loop threads (`cores=4`: the pool has
  `Environment.ProcessorCount` threads, the caller included, GL:83, MT:24-25, :71), plus any restarted worker's old
  slot, the per-bot counters, a few `/proc` files (about 13 µs each, cpu §3), formatting and one CSV append: a few ms a
  minute, on a thread-pool thread (in `offLoopCpuPct`).

### 3.8 Bot-seconds and `n`

The same timer ticks every second, under the report lock (3.7): it samples, and the first tick at or after the
window's 60 s closes the window.

| Kind | `n` = average count over the window, from |
|---|---|
| `gamebot` | `AutonomousBotRegistry.PopulationForBrainTick` (ABR:21), read every second: the registered GameBots in the world, refreshed once a second on the loop (ABR:23-34, :37-59). A corpse it keeps in the world (`bots/GameBot.cs:938`) still counts (est.) |
| `player`, `piloted`, `playerbot` | presence: the wrapper counts each `GameClient` item in `ClientService.BeginTick` (CS:59), one per client per tick, when the client has an owner by 3.3: `IsPlaying` (Playing or Linkdead) and an Active `Player`. Linkdead counts on purpose: the character stays in the world for 60 s (`gameobjects/GamePlayer.cs:893`, :42) and its timers, effects and movement still cost. A client loading (`WorldEnter`) or at the character screen does not count. `ClientService.EndTick` items are charged but never counted |
| `companion`, `monster` | presence: each brain whose body is that kind (not a pet) in `NpcService`, one per live brain per tick (NS:46; every live brain is visited every tick, NS:52-61). For monsters that is the live brains: the woken monsters, and the few encounter controllers (est.), hidden NPCs of the guild "DO NOT REMOVE!" that always think (`gameobjects/GameNPC.cs:700-707`) |
| `unattributed` | none |

- Presence `n` = presence count ÷ ticks in the window; ticks = the change of `GameLoop.GameLoopTime` ÷
  `GameLoop.TickDuration`, since game time grows by exactly one tick's duration per tick
  (`Managers/GameLoop/GameLoopTickPacer.cs:90`). Both are plain static reads (GL:23-24).
- **Bot-seconds** of a kind = `n × win`. Every per-bot figure divides by it.
- Companions and monsters count only while their brain is live: a dead one is not counted (`ai/ABrain.cs:17-18`;
  NS:63-66).

### 3.9 The process line: what is not measured, and the cross-check

**Not charged to any kind:**

- **Off the game loop:** thread-pool timers: the GameBot ones (the population controller,
  `bots/autonomous/AutonomousPopulationController.cs:69`; the two dashboards,
  `bots/autonomous/AutonomousBotDashboard.cs:40` and `bots/autonomous/AutonomousRvrDashboard.cs:51`; the map markers,
  `bots/autonomous/AutonomousBotMapMarkers.cs:60`; the realm-event records and controls,
  `bots/autonomous/RealmEventRecords.cs:44` and `bots/autonomous/RealmEventControls.cs:41`; the guild chat timer,
  `bots/autonomous/AutonomousBotGuilds.cs:76`, which only posts its work to the loop), the pilot's poll (APP:78) and the
  ClassicQuests refresh for each player every 5 s (`quests/QuestsMgr/ClassicQuests.cs:219`, :923); the camp planner
  thread (CPP:52); saves, bots' included (`GameServer.cs:1237-1298`, at the lowest priority, :1259; section 7 reads them
  per kind); network and database I/O; GC on its own threads (`source/server/CoreServer/CoreServer.csproj:20-21`); the
  GC event listener's dispatch thread (`Managers/GameLoop/ServerMemoryDiagnostics.cs:366-434`); the one-off `roads.json`
  load, queued at the first tick, on a thread-pool thread (`bots/autonomous/AutonomousZoneRoads.cs:64`); the meter's own
  report.
- **On the loop, outside callbacks:** each stage's list update (e.g. NS:36), barrier waits, the quest services' rollover
  loops over every client (`ECS-Services/DailyQuestService.cs:38-87` and its Weekly and Monthly equivalents), the
  minute's `SERVER_WORK` and `SERVER_STALL` reporting (`Managers/GameLoop/GameLoopWorkMetrics.cs:50-70`), the
  diagnostics' one-time start at the first tick (:54), `ServerMemoryDiagnostics.Minute()` once a minute (:59) and its
  `SERVER_MEMORY` report every 5 minutes (est. 1-2 ms on the loop, most of it the working-set read, which reads one
  `/proc` file per thread: `Managers/GameLoop/ServerMemoryDiagnostics.cs:153-156`; more on a test run with
  `memory-census.on`, :163-167), the loop's busy wait before the next tick
  (`Managers/GameLoop/GameLoopTickPacer.cs:69-83`), and the meter's own per-item work.
- **Inside a callback, charged to its owner:** posted actions run one by one while a load waits on a task
  (`Managers/GameLoop/GameLoopSynchronizationContext.cs:76-86`; the waits are `GameLoopAsyncHelper.Wait` and
  `GetResult`, :59-74, called from `gameobjects/GamePlayer.cs:10656`, `gameutils/GamePlayerInventory.cs:38` and
  `bots/BotInventory.cs:40`). They run inside the waiting code's timed span, so they are charged to the waiting
  callback's owner and stage, or to `Fleet` inside the fleet scope; a wait outside any callback or scope charges
  them to nothing.

**How the process line covers them:**

| Field | Source, read once a minute | Read it as |
|---|---|---|
| `processCpuPct` | `Process.TotalProcessorTime` | the whole cost; the base of a whole-process A/B (section 7) |
| `loopThreadsCpuPct` | `/proc/self/task/<tid>/schedstat` (est. on the owner's kernel: present on the research machine, cpu §3), first field, for the threads whose `/proc/self/task/<tid>/comm` is `GameLoop` or `GameLoop_Worker` (Linux cuts names to 15 characters; `GameLoop_Watchdog` and `GameLoop_BusyWaitThreshold`, `Managers/GameLoop/GameLoopThreadPoolWatchdog.cs:38` and `Managers/GameLoop/GameLoopTickPacer.cs:45`, are left out) | includes the busy wait, so an upper bound of on-loop work |
| `offLoopCpuPct` | the difference | what the meter cannot put on a kind |
| `campThreadCpuPct` | the same file (est. on the owner's kernel), for `CampSnapshotPla` | the camp planner thread only: add it to `gamebot` for a closer GameBot cost. The other GameBot timers (population controller, dashboards, map markers, realm-event records) and bot saves stay in `offLoopCpuPct`; the whole cost comes from whole-process runs (section 7) |
| `chargedPct` | the slots | on-loop work in callbacks, all kinds |
| `loopWaitMs` | schedstat (est. on the owner's kernel), second field (time spent waiting on a run queue), loop threads | preemption: wall time a callback may include. It can include throttled time (a throttled thread stays runnable, est.): do not add it to `throttledMs` |
| `throttledMs` | `/sys/fs/cgroup/cpu.stat`, `throttled_usec` (cgroup v2; present inside the container, est.) | container throttling |
| `gcPauseMs`, `gen2` | `GC.GetTotalPauseDuration()`, `GC.CollectionCount(2)` | GC pauses inside callbacks |
| `allocMBSec` | `GC.GetTotalAllocatedBytes(false)` | the GC's load, next to `gcPauseMs`; `-` while the counter stands still (below) |
| `tps` | `GameLoop.GameLoopTime` and `TickDuration` (3.8) | must be 30.0 for a fair minute |

- **Wall time, not pure CPU.** Each callback is timed by `Stopwatch`, so time a thread was preempted, throttled or
  paused by GC counts. When `loopWaitMs`, `throttledMs` and `gcPauseMs` are small against the window, wall ≈ CPU. When
  they are not, the absolute numbers are high, but both kinds run in the same batches at the same moments, so the
  ratio between them holds (est., cpu §4.6).
- Threads are re-listed every minute; a thread that left between two reads is dropped from that window, and a
  negative difference counts as 0. So after a normal server stop, the block just before
  `BOT_CPU_STATE off source=stop` can have `-` for `loopThreadsCpuPct`, `offLoopCpuPct`, `campThreadCpuPct` and
  `loopWaitMs`: the server joins the game loop's threads and the camp thread before its stopped event
  (`GameServer.cs:1013-1015`). That block is usually the part-minute the stop writes (`win=` under 60), but `win=60` or
  `61` when the stop came as a minute ended; a stop within 5 s of a minute's lines writes no part-minute. A stop from
  the game loop's own error catch (`GameLoop.cs:102`) runs on the `GameLoop` thread, which stays listed:
  `loopThreadsCpuPct` and `loopWaitMs` cover that thread alone, `campThreadCpuPct` is 0 (the camp thread was joined),
  and the ended workers' and camp thread's CPU is in `offLoopCpuPct`.
- **A schedstat that says nothing.** Where the kernel keeps no scheduler statistics, the file still exists and reads
  `0 0 0` (est.). A first field of 0 for a running loop thread therefore counts as unavailable: `loopThreadsCpuPct`,
  `offLoopCpuPct`, `campThreadCpuPct` and `loopWaitMs` are then `-`, not 0.
- **An allocation counter that stands still.** Under Server GC (`source/server/CoreServer/CoreServer.csproj:20`)
  the runtime's dynamic heap count (DATAS, on by default since .NET 9; the project does not turn it off) takes GC
  heaps out of service when the load eases and brings them back when it rises. .NET 10's
  `GC.GetTotalAllocatedBytes` adds up the heaps in service only, so taking heaps out lowers the sum by all they ever
  allocated, and the `false` read, which never returns less than it did before, stands still while the server
  allocates MBs a second (dotnet/runtime#122299; fixed for .NET 11 by #131069, not in .NET 10 as of 2026-10-20).
  It stands still until the heaps still in service have allocated as much again as the ones taken out, or until
  the heaps come back: minutes to hours (on the live server, 18:11-18:31 and from 19:06 for over an hour on
  2026-10-20). A running server always allocates, the meter's own report among it, so a window the counter did not
  move in has `allocMBSec` `-`, not 0.0; that is usually also the window the heaps went out in. When all the heaps
  come back, the window they come back in carries the `-` run's allocation as well: spread it over the run and that
  window. When only some come back, it carries only their part. When the counter moves again on its own first, the
  run's allocation is not counted, and the heaps' later return shows as one jump that was not allocated in that
  window. `GC.GetTotalAllocatedBytes(true)` is no cure: it adds up the same
  heaps, so it falls by gigabytes when they go and jumps back when they return, and it stops every managed thread
  for the read. Two reads the stall does not touch: the kind lines' allocation (exact, game-loop callbacks only)
  and `SERVER_MEMORY`'s `topAllocations`, from the runtime's allocation events
  (`Managers/GameLoop/ServerMemoryDiagnostics.cs:415-423`): its first entry's MB/min ÷ its % × 100 is the
  process's MB a minute. `SERVER_MEMORY`'s `allocMBperMin` reads the same counter (:129-131) and has the same
  fault; that file is upstream's and stays as it is.

### 3.10 Per-bot counters, the spread and the top 5

- For an owner of a bot kind (`gamebot`, `companion`, `playerbot`, `piloted`), step 1 of 3.2 gets its counter from a
  `ConditionalWeakTable<GameObject, BotCpuCounter>` (created on first use). The counter holds ticks and calls, added
  with `Interlocked.Add` (one bot's effects can run on two threads at once), and the last values the report read
  (written only by the report). Counters are small objects allocated one after another, so `BotCpuCounter` puts its
  two hot fields between 64 bytes of padding on each side (explicit field offsets, as the pool's own `WorkState`
  does, MT:45-50; under 200 bytes a bot): two bots' counters never share a cache line. The table never keeps a bot
  alive: an entry goes with its bot.
- **Per bot** in a window: its ticks since the last report, as ms per second of the window. Pets' work counts for
  their owner. `Fleet` counts for no bot.
- **Spread:** over the bots of the kind charged at least once in the window, nearest rank: sort ascending, `p50` =
  value at rank ⌈0.50·m⌉, `p95` = at ⌈0.95·m⌉, `max` = the largest. A bot present but never charged in a whole minute
  is left out (est. rare: every live GameBot brain is visited each tick).
- **Top 5:** the five largest, ties by name, read by enumerating the table: name, level, class (`GameBot.ClassName`),
  region, goal and activity (`PersistentRecord`, which can be null: `-`, as NS:81 reads it with `?.`). These are reads
  of another thread's objects without a lock: a name or activity a moment old is harmless. The report publishes them
  with the window (3.7); `/botcpu top` only reads that.

### 3.11 Overhead, and the "off" guarantee

**Off** (the default): `Enabled` is a static bool, false. H1 reads it about 20 times a tick and passes the same
delegate to the pool; H6's two calls inline to a read of it and a test of a local (5.1). No timer, no slot, no
table, no allocation, no file: `BotCpuMeter` has no static initializer to run (5.1), so even the first H1 call creates
nothing.

**On** (est.):

- About 90-120 ns per callback (3.2) × an est. 150,000-450,000 callbacks a second (cpu §4.7) = 14-54 ms of CPU a
  second: 1.4-5.4% of one core, or 0.35-1.35% of a 4-CPU container. `callsSec` in the process line gives the real
  count, so the real overhead is `callsSec` × about 100 ns.
- The metered stages run a little slower while it is on. A stage's work is split over the loop threads (MT:143-149),
  and `SERVER_WORK` records one wall time per stage (`Managers/GameLoop/GameLoopWorkMetrics.cs:39-48`), so a stage's
  `avgMs` rises by about its metered calls per tick × 100 ns ÷ the loop threads (est.), not by the whole overhead.
  The owner's check 7 (6.5) measures the real rise.
- Memory: 3.2 KB per loop thread, a counter per bot (est. under 200 bytes, well under 100 KB for 250 bots), about 600
  small wrapper allocations a second (est. 60 KB/s, against the server's own `allocMBSec`).
- The report: a few ms a minute off the loop (3.7).

## 4. The switch

### 4.1 The server property

- `bot_cpu_meter`, category `server`, a `public static bool BOT_CPU_METER` in `BotCpuScript`
  (`[ServerProperty]` fields are found only when public and static, `serverproperty/ServerProperties.cs:2578-2589`).
  Code default `false`, as `si_start_choice` and `bot_rp_timer_wait` (`scripts/hearthdaoc/SiStartChoiceScript.cs:19-22`,
  `scripts/hearthdaoc/RpTimerWaitScript.cs:16-19`).
- Description (the same string in C# and `server_properties.py`, no `"` or `\`): "Measure the game loop's CPU time per
  kind of bot and player and write BOT_CPU lines to the server log every minute; a GM's /botcpu turns it on or off
  until the next start".
- **Read once, at start.** A `[GameServerStartedEvent]` handler (the pattern of APP:74-79) reads it; properties are
  loaded before that event (`GameServer.cs:323`, :510) and the game loop starts after it (:517), so the first tick is
  metered. On: the meter starts and logs `BOT_CPU_STATE on source=property`. `&serverproperties refresh` does not turn
  a running meter on or off: use `/botcpu`.
- A `[GameServerStoppedEvent]` handler stops a running meter, writing the partial window and `off source=stop`.
- Both handlers do their work at once, on their own thread, under the report lock and inside the catch of 3.7: the
  started event comes before the loop starts and the stopped event after `GameLoop.Exit` (`GameServer.cs:1013`,
  :1015), so neither holds a tick, and an exception in them never reaches the server's start or stop.

### 4.2 `HEARTHDAOC_BOT_CPU`, default off

`.env` → `deploy/compose.yml` → `deploy/entrypoint.sh` → `deploy/bin/server_properties.py` → the property, at every
start, exactly as `HEARTHDAOC_BOT_RP_TIMER_WAIT` does (`deploy/compose.yml:25`, `deploy/entrypoint.sh:27-31`,
`deploy/bin/server_properties.py:36-41`, :67-72, :103, :108, :116-117; `deploy/.env.example:22-26`):

- `compose.yml`: `HEARTHDAOC_BOT_CPU: ${HEARTHDAOC_BOT_CPU-off}` after line 25. Compose passes only the variables it
  lists (`deploy/compose.yml:13-27`).
- `entrypoint.sh`: `--bot-cpu "${HEARTHDAOC_BOT_CPU-off}"` added to the `server_properties.py` call (:30-31), and the
  comment above it (:27-29) names `bot_cpu_meter`.
- `server_properties.py`: `CPU_KEY = "bot_cpu_meter"`, `CPU_DESCRIPTION`, `CPU_DEFAULT = "False"`; `bot_cpu_value(spec)`
  (`on` or `off`, case ignored; anything else: "HEARTHDAOC_BOT_CPU must be on or off, got …", exit 2 before any
  write); a new required `--bot-cpu`, declared after `--bot-rp-timer-wait`; it prints "Bot CPU meter: on" or "off"
  when it changes the row. A new world has no row, so its first start prints "Bot CPU meter: off" once. The module
  docstring gains a paragraph.
- `.env.example`, after the realm-point lines:

  ```
  # on = the server measures the game loop's CPU per kind of bot and player and writes BOT_CPU lines to the server log
  # every minute, and rows to /data/logs/bot-cpu.csv; it costs about 1-5% of one core while on. off = nothing is
  # measured. A GM can also turn it on or off in game with /botcpu, until the next start. Case is ignored.
  HEARTHDAOC_BOT_CPU=off
  ```

- `./hdc update` adds `HEARTHDAOC_BOT_CPU=off` to an existing `.env` (`deploy/hdc:128-139`).
- On at start: `HEARTHDAOC_BOT_CPU=on` in `~/hearthdaoc/.env`, then `./hdc up`.

### 4.3 `/botcpu on|off` at runtime

- `/botcpu on` and `/botcpu off` override the property **until the next start**; they never write the property. At the
  next start, `HEARTHDAOC_BOT_CPU` decides again.
- `/botcpu on <minutes>` turns itself off after that time (`off source=timer`), whether the meter was started by the
  property or by a command. The 1-second timer checks the stop time, under the report lock, and closes the run there
  (3.7).

### 4.4 The meter's state

`Enabled` is written with `Volatile.Write`: by an `off` command on its game-loop thread (nothing more: the rest is a
queued job, 3.7), and on the report side under the report lock: true in an open job (an `on` command's job, or the
started handler), false in a close (`off`, the stop time, the stopped handler) or on an error. A change is seen by the
next `ExecuteForEach` call, so within a tick (est.).

### 4.5 `/botcpu heap`: not in this PR

A forced, blocking, compacting full collection (cpu §6) pauses every game thread, est. from a fraction of a second to
seconds on a heap of several GB. On the live server that is a frozen world and possibly link-dead players. The
meter's promise is "safe any time", so this PR leaves it out. Memory per kind comes from `allocKBPerBotSec` (exact,
4-7 ns a read, cpu §3) and the process line's `heapMB` and `rssMB`, read without forcing anything. Retained heap per
bot belongs to the prototype's whole-process runs on a test world; its branch can add `/botcpu heap` there, behind
its own guard.

## 5. Code

### 5.1 New files (ours)

All in `scripts/hearthdaoc/`, `namespace DOL.GS.HearthDAoC`, LF, no BOM, each with a `// HearthDAoC:` header naming
this spec.

| File | Holds |
|---|---|
| `BotCpuMeter.cs` | the hot path: `Enabled`, `ExecuteForEach(pool, …)`, `BeginFleet`/`EndFleet`, the run number, the slots, the owner cache per item type, the classifier (3.3) with the pilot check as a parameter of its pure core, the per-bot table, `IPlayerBotClient`; `Enable()`, `Disable()` and `TakeSnapshot()` for the script and the tests. No timer, no file; one warning at most from the setup fallback (3.1) |
| `BotCpuReport.cs` | plain values in and out, no server state: the snapshot difference, `n` and bot-seconds, every figure of 2.1, the nearest-rank spread, the top 5, the compare pairs, the since-on totals, and the exact text of every line, CSV row and chat line (2.4) |
| `BotCpuScript.cs` | the switch (section 4), start and stop, the report side of 3.7 (the lock, the job queue, the run, the catch), the 1-second timer (sampling, the window, the stop time), the `/proc` and cgroup readers (as pure parsers of a file's text, plus the reads), the log lines and the CSV file with its header check and rotation |
| `BotCpuCommand.cs` | `/botcpu` (2.4): reads the published result, calls `BotCpuMeter.Disable()` for `off`, queues jobs (3.7) |

- **Nothing runs while off.** `BotCpuMeter` has no static field initializers and no static constructor: `Enabled` is
  a plain `public static bool` (false by default). The slot list and its lock and the stage map are created by the
  first `Enable()` and kept; each `Enable()` also makes a new per-bot table and the next run number, all before it
  sets `Enabled` (3.7); the owner caches are static generic classes first touched by a wrapper, so only while on.
- **H6 inlines.** `BeginFleet` is `[MethodImpl(MethodImplOptions.AggressiveInlining)] => Enabled ? BeginFleetCore() :
  0`, and `EndFleet` is an inlined `if (start != 0) EndFleetCore(start);`.

### 5.2 The upstream lines

| # | File | Where | Lines |
|---|---|---|---|
| H1 | GL (OpenDAoC core) | in `ExecuteForEach`, before :65 | a `// HearthDAoC:` comment and the guard line (3.1) |
| H6 | NS | after :27 and after :31 | a `// HearthDAoC:` comment and `BeginFleet`; `EndFleet` with a trailing `// HearthDAoC:` comment, as the quest services' marks do (`ECS-Services/DailyQuestService.cs:77`) (3.5) |

GL has CRLF lines with ten LF-only ones (39-41, 54-55, 125-126, 135-137) and no BOM; the new lines use CRLF like their
neighbours. NS has a BOM and CRLF throughout; both stay. Nothing else upstream changes.

## 6. Tests

### 6.1 C# unit tests (CI)

`Tests/UnitTests/UT_BotCpuMeter.cs`, `UT_BotCpuReport.cs` and `UT_BotCpuScript.cs`, namespace `DOL.GS.Tests`, each a
`public sealed class` in a file of its own name with a `// HearthDAoC:` header (as
`source/server/Tests/UnitTests/UT_ClassicBattlegrounds.cs:1-15`). Game objects are built without their constructors and
their fields set by reflection, as `source/server/Tests/UnitTests/UT_AutonomousBotRealmPointRewards.cs:50-69` does;
`GameLoop.GameLoopTime` is set by reflection as `UT_BonedancerSupportCastLifecycle.cs:387-388` does.

**`UT_BotCpuMeter`** (the hot path and the real H1), `[TestFixture, NonParallelizable]` as fixtures that touch shared
state are (`source/server/Tests/UnitTests/UT_RealmExchangeInterface.cs:14`): the test sets GL's private `_threadPool`
(GL:18) by reflection to a recording pool, a test subclass of `GameLoopThreadPool` that keeps the delegate it was
given and runs the items in order, and sets `GameLoop.ActiveService`. `[TearDown]` restores both and calls
`BotCpuMeter.Disable()`, so a failing test never leaves the meter on for the next. Counts are read as differences of
`TakeSnapshot()`, so tests do not depend on each other.

- **Off = no wrap:** `Enabled` is false by default; `GameLoop.ExecuteForEach` hands the pool the very same delegate
  (`ReferenceEquals`), creates no slot, and the snapshot does not change; `BeginFleet` returns 0 and `EndFleet(0)`
  charges nothing. After a warm-up call, `GameLoop.ExecuteForEach` and `BeginFleet`/`EndFleet` with the meter off
  leave `GC.GetAllocatedBytesForCurrentThread()` unchanged.
- **On:** the pool gets another delegate; each item runs exactly once; one call is charged per item to the right kind
  and stage; an empty list is passed through unwrapped.
- **Runs:** `Enable()`, adds, `Disable()`, `Enable()`: the second run has the next run number and an empty per-bot
  table; the slots keep their totals (the report's baseline does the rest, `UT_BotCpuScript`).
- **Setup fallback:** with a test hook that makes the stage lookup throw, `GameLoop.ExecuteForEach` still runs every
  item exactly once, the pool gets the original delegate, and nothing escapes; `ActiveService` null or empty gives
  `Other` and does not throw.
- **Classification table**, one row each:

  | Item | Expected kind, stage |
  |---|---|
  | brain of an autonomous `GameBot` | `gamebot`, `Npc` |
  | brain of a non-autonomous `GameBot` | `companion`, `Npc`, presence +1 |
  | `AttackComponent` of a `GamePlayer` whose client is a `BotDummyClient` subclass; whose client is a plain `GameClient` subclass implementing `IPlayerBotClient` | `playerbot`, `Attack`; the same |
  | the same with a plain `GameClient`, pilot check false | `player` |
  | the same, pilot check true (through the pure core) | `piloted` |
  | brain of a `GameNPC` with a plain brain | `monster`, `Npc`, presence +1 |
  | pet of a `GamePlayer` | `player`, `Pets`, no presence |
  | pet of a pet of an autonomous `GameBot` | `gamebot`, `Pets`, the bot's counter |
  | pet of a companion | `companion`, `Pets` (not the companion's player) |
  | a controlled brain with no owner | `monster` |
  | a pet cycle (A owns B, B owns A) | `unattributed` |
  | `ECSGameTimer` with no owner; with a non-living owner | `unattributed`, `Timer` |
  | `LivingBeingKilled`: a monster killed by a `GameBot`; by a `GameBot`'s pet; a `GameBot` killed by a monster; a monster killed by nothing | `gamebot`, `Reaper`; `gamebot`, `Pets`; `gamebot`; `monster` |
  | `GameClient` in `ClientService.BeginTick`: with no `Player`; in `CharScreen` with an old `Player`; in `WorldEnter` with a `Player`; `Playing` with an Active `Player`; `Linkdead` with an Active `Player` | `unattributed`; `unattributed`, no presence; `unattributed`, no presence; `player`, presence +1; `player`, presence +1 |
  | `GameClient` in `ClientService.EndTick`, `Playing` with an Active `Player` | `player`, `ClientEnd`, no presence |
  | `SubZoneObject` whose node holds a `GameBot` | `gamebot`, `Zone` |
  | `int` items outside a fleet scope; inside one | `unattributed`; `gamebot`, `Fleet` |
  | a posted action, through a test `GameServiceBase` subclass calling `ProcessPostedActionsParallel` | `unattributed`, in the stage set |
  | an unknown `ActiveService`; `Daily`/`Weekly`/`MonthlyQuestService` | `Other`; `Quests` |

- **Owner read first:** a test brain whose callback sets its own `Body` to null is still charged to its kind.
- **Never throws:** a test brain whose `Body` getter throws (`Body` is virtual, `ai/ABrain.cs:15`) is charged to
  `unattributed`, its callback still runs once, and nothing escapes the wrapper.
- **The fleet rule:** inside `BeginFleet`/`EndFleet`, the scope spins about 1 ms and runs a nested `ExecuteForEach`
  of 3 `int` items that spin about 2 ms each (the recording pool runs them inline). `Fleet` is at least 6 ms and no
  more than the scope's own wall time plus 5%; counted twice it would be about 13 ms.
- **The fleet scope and off:** `Disable()` inside the scope, then `EndFleet`: the flag is clear and the scope is not
  charged; after `Enable()`, an `int` batch outside any scope goes to `unattributed`, and the next scope to `Fleet`.
  A nested batch whose pool call throws still adds its time to the scope, and the exception reaches the caller
  unchanged.
- **Per-bot counters:** two bots' items add to their own counters; a pet's to its owner's; `Fleet` to none.
- **Allocation:** an item that allocates 1,000 bytes adds at least that much to its kind; an `int` list adds none.

**`UT_BotCpuReport`** (pure):

- **Snapshot difference:** two slots, a snapshot, more adds, a second snapshot: the window holds only the adds; a slot
  that appears between snapshots counts from zero; a negative difference counts as 0; reading never changes a slot.
- **Arithmetic:** from raw ticks (with `Stopwatch.Frequency` 1e9 and 1e7), calls, bytes, presence, the game-time change
  and the registry samples: `n`, bot-seconds, `msSec`, `msPerBotSec`, `callsPerBotSec`, `usPerCall`,
  `allocKBPerBotSec`; 1.0 ms/s for 240 bots is 24% of one core.
- **Nothing to divide by:** `n` 0 with time charged gives `-` for every per-bot field and no stage part; 0 calls give
  `usPerCall=-`; a compare pair with a `-` gives `-`; no `NaN` or `Infinity` anywhere.
- **Window length:** a 61.4 s window divides by 61.4 and prints `win=61`; a 0.4 s final window writes no lines or
  rows; a window with no registry sample uses the last one.
- **Since on:** two windows' totals; after `reset`, only the later one, and the label `Since reset`; the partial
  window is not counted.
- **Spread:** values 1 to 20 give `p50` 10, `p95` 19, `max` 20; one bot gives all three equal; no bot gives no
  fields.
- **Top 5:** order by ms, ties by name; a name with a space or `:` becomes `_`; fewer than 5 bots give fewer entries.
- **Lines:** the exact text of each line of 2.1 for a sample window, under the `de-DE` culture (a `.` decimal point
  still); stages in tick order, zeros left out; the stage values of a kind line sum to its `msPerBotSec`
  (`msSec` for `unattributed`) within rounding; a kind with nothing gives no line; `unattributed` has `n=-`,
  `callsSec`, `allocKBSec`; missing process values are `-`.
- **Chat:** the exact text of the `/botcpu` lines of 2.4 for the sample window, of each reply in the 2.4 table, and of
  the `top` lines, with a GameBot whose `PersistentRecord` is null.
- **Compare:** written only for the three pairs with both `n` ≥ 1.0; ratios with 2 decimals; a gamebot value of 0
  gives `-`.
- **CSV:** the exact header of 2.3; a row's stage columns are the kind's raw stage ticks ÷ the exact window length (3
  decimals, `0.000` for zero), not the rounded per-bot values × `n`; `-` becomes an empty cell; an `unattributed`
  row and a charged kind with `n` 0 have their stage totals and empty per-bot cells; the `utc` format.

**`UT_BotCpuScript`**:

- The switch: a public static field declared `[ServerProperty("server", "bot_cpu_meter", …, false)]`.
- The command: the exact `[CmdAttribute]` of 2.4; minutes 0, 1441 and `x` refused; a kind that is not a bot kind
  refused. `&botcpu` is the only command name or alias in the GameServer assembly that starts with `&bot`, so a GM's
  `/bot` guessing it (2.4) is deliberate.
- Off and on from a command: with the work-item queueing held by a test hook, `/botcpu off` returns with `Enabled`
  false and one job queued, and `/botcpu on` with `Enabled` unchanged and one job queued, both before any file,
  `/proc` read or log write.
- One at a time: a minute tick and an `off` job run back to back, in both orders: the counts land in one window, never
  two; a tick that runs after the close finds the run closed and writes nothing, so no window line follows `off`; off
  then on queued together run in order (a final window, then a new run whose first window holds only its own adds);
  on then off end with `Enabled` false.
- No exception escapes: a tick whose reader or writer throws returns normally, logs one `BOT_CPU_ERROR`, writes
  `BOT_CPU_STATE off source=error`, and leaves `Enabled` false; the same with a log call that throws; a CSV write
  that throws a non-I/O exception logs one warning and the meter goes on.
- The readers, as parsers of sample text: `schedstat` "123 456 7" gives 123 and 456 ns, and "0 0 0" gives `-`; `comm`
  `GameLoop` and `GameLoop_Worker` are loop threads, `GameLoop_Watchd` and `GameLoop_BusyWa` are not,
  `CampSnapshotPla` is the camp thread; `cpu.stat` gives `throttled_usec`; `/proc/self/status` gives `VmRSS`; an empty
  or missing file gives `-`.
- The process window: an allocation counter that did not move, or went back, gives `allocMBSec=-`, the window's
  other values still given; one that moved 6 MB in 60 s gives 0.1 (3.9).
- The stop time: a stop at 14:32 stops at the first second at or after it; `on` with no minutes clears it; `on 30` on
  a running meter writes the `on` line again.
- The CSV: a file over 50 MB is renamed `.1` (an older `.1` replaced) before the next append, and the new file gets
  the header; a file whose first line is another header is renamed `.1` the same way; an empty file gets the header.

**What the unit tests do not reach:** the real multi-threaded pool path (workers, chunks and the barrier: the
recording pool runs items inline), the real `/proc` and cgroup files of the container, the started and stopped
handlers' wiring, and the chat in game. The owner's checks cover them (6.5, steps 1, 3, 4, 5 and 9).

### 6.2 Source checks (Python, `deploy/tests/test_bot_cpu.py`)

With the helpers of `deploy/tests/test_rp_timer_wait.py:44-73` (`source`, `between`, `marked`):

- **H1:** in GL's `ExecuteForEach<T>`, the guard line once, with `// HearthDAoC:` on the line above, followed by the
  unchanged `_threadPool.ExecuteForEach(items, toExclusive, action);`.
- **H6:** in NS's `Tick`, in this order: `ProcessPostedActionsParallel();`, the commented `BeginFleet()`, the four
  `Prepare…Tick();` calls in their order, `EndFleet(fleetStart);` with its trailing `// HearthDAoC:`, then
  `ServiceObjectStore.UpdateAndGetAll<ABrain>`.
- `BotCpu` appears in no other file under `GameServer/` outside `scripts/hearthdaoc/`: one line in GL, two in NS.
- **Upstream facts the meter mirrors** (a change upstream fails here): GL's `public static string ActiveService { get;
  set; }`, `ActiveService = tickState.ProfileKey;` before `tickState.TickAction();`, the profile-key rule (GL:169), the
  17 `AddStep` lines in their order (the stage map), `public const string THREAD_NAME = "GameLoop";` and
  `private static GameLoopThreadPool _threadPool;`; `public abstract void ExecuteForEach<T>(List<T> items, int
  toExclusive, Action<T> action);` (`Managers/GameLoop/GameLoopThreadPool.cs:19`); the worker name
  `$"{GameLoop.THREAD_NAME}_Worker_{i}"` (MT:100) and `"CampSnapshotPlanner"` (CPP:52); each owner member of 3.3
  (`public GameLiving owner;`, `public GameObject Owner { get; }` in TimerService, `public GameLiving Owner;` in
  `ECSGameEffect`, `public LinkedListNode<GameObject> Node { get; }`, `public GameObject Killer { get; private set; }`,
  …); `public bool IsAutonomousWorldBot { get; private set; }`; `public class BotDummyClient : GameClient`;
  `public bool IsPlaying => ClientState is eClientState.Playing or eClientState.Linkdead;` (`GameClient.cs:79`);
  `GameLiving Owner { get; }` in `IControlledBrain`; `public static int PopulationForBrainTick`; `public static bool
  IsActive(GamePlayer player)`; `private sealed class PostedAction` (if upstream opens it, H5 becomes cheap: 3.6).
- **The nesting stays inside the scope:** CPP has exactly one `GameLoop.ExecuteForEach(`, and the only call of
  `AutonomousWorldBotController.PrepareCampPlanningTick()` is in NS.
- **Endings:** GL and NS keep upstream's endings line by line, by `endings.check` (`deploy/tests/endings.py:125-129`)
  with the BOM pinned, as `deploy/tests/test_quest_indicators.py:100-108` does: GL its ten LF-only lines and no BOM, NS
  its BOM and CRLF throughout, and the hook lines end in CRLF, the ending of the upstream lines beside them; the four
  new files, the three test files and `test_bot_cpu.py` are LF, without BOM or tabs (as
  `deploy/tests/test_rp_timer_wait.py:322-330`). `endings.check` compares with upstream's file at the tag or commit of
  `deploy/upstream.lock`, which CI's full clone has (`.github/workflows/server-image.yml:33`); in a shallow clone it
  fails on GL, a file upstream ships with both endings (`deploy/tests/endings.py:12-16`).
- **The guides:** in `deploy/HANDOFF.md` and in `docs/fork/verification/bot-cpu-meter-live.md` (section 8), every line
  that contains `BOT_CPU` and reads the server log uses
  `docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)'`, and no line contains both `BOT_CPU` and
  `/data/logs/server.log`. The check is per line, not per file as `deploy/tests/test_rp_timer_wait.py:333-344` is:
  `HANDOFF.md` already reads `server.log` alone for its start checks (`deploy/HANDOFF.md:70-71`).

### 6.3 Deploy tests (the switch), in the same file

Copying `deploy/tests/test_rp_timer_wait.py:76-161`:

- `bot_cpu_value`: `on`/`ON`/`On` give `True`, `off`/`OFF`/`oFf` give `False`; `""`, `yes`, `true`, `1`, `" on"` and
  the like are refused with the exact message.
- The entrypoint passes `--bot-cpu "${HEARTHDAOC_BOT_CPU-off}"`; with nothing set on a new world it writes `False` and
  prints "Bot CPU meter: off"; `ON` writes `True` and prints "Bot CPU meter: on"; `maybe` and `""` stop the start
  (exit 2) and write nothing at all.
- `compose.yml` passes it with default off; `.env.example` has exactly `HEARTHDAOC_BOT_CPU=off` under a comment that
  says what `on` and `off` do; `HANDOFF.md` lists it, with the `./hdc update` line.
- The C# description equals `server_properties.py`'s; key `bot_cpu_meter`; category `server`; default `False`.
- Existing tests that change:
  - every CLI call in `deploy/tests/test_server_properties.py:102-144` gains `"--bot-cpu", "off"`, except the
    missing-argument test at :130-133, whose `assertIn` still finds `--si-start-choice` first (argparse lists missing
    required arguments in declaration order, and `--bot-cpu` comes last);
  - the start in `deploy/tests/test_relic_pad.py:62-63` (the entrypoint's world steps, new in the 0.37b sync) gains
    `"--bot-cpu", "off"` too (CI runs it with `HDC_TEST_WORLD`); without it argparse exits 2 and :65 fails;
  - the expected stdout on a new world gains "Bot CPU meter: off\n" at `test_server_properties.py:112` and :143,
    `deploy/tests/test_rp_timer_wait.py:121` and `deploy/tests/test_si_start_choice.py:61`;
  - unchanged: second runs on the same world (`test_server_properties.py:115`, :121; `test_rp_timer_wait.py:127`;
    `test_si_start_choice.py:67`)
    and the bad-value tests (nothing is written).
- **Smoke test** (`deploy/tests/smoke.sh`, next to :51): `Command - '&botcpu' .* required plvl:2` in the server log,
  and no `BOT_CPU_STATE` line after a default start. Both are plain `grep -q` lines, not a `logs_have` call or a loop:
  `deploy/tests/test_container_lines.py:44-46` reads every `logs_have` string and the first `for line in ...; do` loop
  of `smoke.sh` as world-fix lines.

### 6.4 CI

Add `|FullyQualifiedName~UT_BotCpuMeter|FullyQualifiedName~UT_BotCpuReport|FullyQualifiedName~UT_BotCpuScript` to the
filter at `.github/workflows/server-image.yml:119`, and the same three names to `SERVER_UNIT_TESTS` and to the expected
list in `deploy/tests/test_workflows.py:267-279` and :297-312 (which also checks each file holds
`public sealed class UT_X`, or upstream's `public class UT_X`, :312). CI runs no fork test missing from that filter. The
deploy tests are found on their own (`.github/workflows/server-image.yml:94`).

### 6.5 The owner's checks on the live server, after the release

1. `./hdc update`: `.env` gains `HEARTHDAOC_BOT_CPU=off`. The container's start output has "Bot CPU meter: off" (once:
   the row is new): `docker logs hearthdaoc-server 2>&1 | grep "Bot CPU meter"`. The server log has no line of the
   meter: the only `BOT_CPU` match is the start's `Command - '&botcpu'` line, which names the BOT_CPU lines in its
   description (leave it out with `grep -v`). `SERVER_WORK` looks as before.
2. At least 30 minutes after the server is up (`./hdc status`: running (healthy)): the GameBots log in over the first
   15 minutes and settle by 20 (section 7), and check 7 needs the next 10 minutes with the meter off. In game, as GM:
   `/botcpu` says "Bot CPU meter: off. /botcpu on [minutes] turns it on." Then `/botcpu on 30`. To keep your
   character from waking monsters and speeding up GameBots near you (section 7), go somewhere quiet, or go to the
   character screen or quit the game: both take you out of `player` (3.8). Quit from the game's menu: a client that is
   killed leaves your character link-dead in the world for 60 s, still counted. The meter runs on.
3. After 2-3 minutes:

   ```bash
   docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | grep BOT_CPU | tail -20
   ```

   Once: a `BOT_CPU_STATE on source=command` line. Then each minute: `gamebot` with `n` close to your bot count
   (about 240), `player` with `n` 1.0 while you are in game, `monster`, `unattributed`, a `BOT_CPU_TOP` for `gamebot`,
   `BOT_CPU_PROCESS` with `tps` at 30.0 (29.9 now and then is fine), and `BOT_CPU_COMPARE pair=player/gamebot` while
   you are in game.
4. Sanity: usually `chargedPct` < `loopThreadsCpuPct` < `processCpuPct`. If `chargedPct` is higher, `loopWaitMs`,
   `throttledMs` or `gcPauseMs` is large too: the wall times are inflated (3.9). `gamebot` `msSec` / 10 is its share of
   one core in %. `loopThreadsCpuPct` as `-` on a minute's line while the server runs means the kernel gives no
   scheduler statistics (3.9): say so on #137. The window a normal server stop writes has `-` there (3.9).
   Confirm a large `gcPauseMs` with the `SERVER_GC_PAUSE` lines of the same minutes (check 3's command, with
   `grep SERVER_GC_PAUSE` for `grep BOT_CPU`). Those lines list only pauses of 250 ms or more (section 1); shorter
   pauses add up in the `pauseTotalMs` of the `SERVER_MEMORY` line that covers them (every 5 minutes).
5. `/botcpu` in chat shows the same last minute; `/botcpu top` lists 5 GameBots with their goals.
6. `docker exec hearthdaoc-server head -1 /data/logs/bot-cpu.csv` gives the header (2.3);
   `docker exec hearthdaoc-server tail -5 /data/logs/bot-cpu.csv` the last rows, one per minute and kind (`utc` is
   UTC, not the log's local time).
7. Overhead: compare the `SERVER_WORK` lines of the 10 minutes before with 10 minutes on. Pass if `samples` stays at
   about 1,800 a minute (30 ticks a second), `overBudget` does not rise, and the stages' `avgMs`, added up, rise by no
   more than `callsSec` × 100 ns ÷ 30 per tick (est.; at `callsSec=212534` that is about 0.7 ms). Bot activity moves
   from minute to minute, so this is a rough check.
8. `/botcpu off`: one partial window, then `BOT_CPU_STATE off source=command`, then no more lines. Under 5 s after a
   window, the reply says the last seconds were too short to write, and no partial window comes (2.2). With `on 30`
   left running, `off source=timer` comes by itself after 30 minutes; a `/botcpu off` after that says "Bot CPU meter
   is already off."
9. At start: `HEARTHDAOC_BOT_CPU=on`, `./hdc up`: "Bot CPU meter: on" in `docker logs hearthdaoc-server`,
   `BOT_CPU_STATE on source=property`, lines with no command. `/botcpu off` stops it until the next start. Set it back
   to `off` and `./hdc up`.
10. Post a few windows (the `gamebot`, `player` and `PROCESS` lines) on #137, with the autosave lines of the same
    period (section 7). The autosave's counts have no time of their own: this gives each the time of its "Saving
    completed" line, and the last `awk` keeps the period posted (here 14:00 to 14:30 on 21 October):

    ```bash
    docker exec hearthdaoc-server sh -c 'cat $(ls -tr /data/logs/server*)' | awk '/Saving completed in/ { t = $1 " " $2 } /(players|autonomousBots): [0-9]+ in/ { print t " " $0 }' | awk '$0 >= "10/21 14:00" && $0 < "10/21 14:30"'
    ```

    They are the GameBot baseline the prototype will be compared with.

## 7. Reading the numbers fairly

Short, for the live server; the prototype's full rules are in cpu §5 and README section 2.4.

- **Compare kinds inside the same windows**, over 30 or more one-minute windows, and their totals before their stages:
  a GameBot moves in `Movement`, the pilot (and a driver copied from it) moves inside its own think
  (APP:502-547).
- **`calls` is not a think rate.** Every brain, timer and client is visited every tick, and the callback itself checks
  whether anything is due (NS:61, `ECS-Services/TimerService.cs:54`, CS:136). So `callsPerBotSec` counts the items a
  kind has, about 30 a second each, and `usPerCall` is lower for a kind with many idle items. Compare `msPerBotSec`;
  a think rate per kind is out of scope (section 10).
- **Saving is off the loop,** so the meter does not see it. Per kind, from the autosave log every
  `HEARTHDAOC_AUTOSAVE_MINUTES` (`deploy/compose.yml:22`): save ms per bot = X / N from `players: N in X ms` and
  `autonomousBots: N in X ms` (`GameServer.cs:1276`, :1283), wall time with the database writes included. A player
  bot registered in `ClientService` saves with the players (`ECS-Services/ClientService.cs:599-607`), so `players`
  counts player bots and real players together.
- **One kind can cost in another's bucket:**
  - a player wakes every NPC within 6,000 units (`world/WorldMgr.cs:60`; CS:670-674;
    `gameobjects/GameNPC.cs:2029-2035`): the cost lands in `monster`, whose `n` also holds a few encounter controllers
    that always think (`gameobjects/GameNPC.cs:700-707`);
  - a GameBot that a player can see within 6,000 units thinks at its fastest rate (`bots/BotBrain.cs:2626-2640`): the
    cost lands in `gamebot`;
  - an NPC or GameBot that moves near a player pays for updating it (CS:704-713): the cost lands in the mover's kind;
  - both linger: a woken brain stays live 60 s after a player last saw it (`gameobjects/GameNPC.cs:36`), and a GameBot
    rechecks every 5-6.5 s (`bots/BotBrain.cs:2634`).

  So the `player/gamebot` line on the live server is a preview, not a fair comparison: a real player also has
  network work a player bot will not have.
- **`tps` must be 30.0.** If the loop runs late, game time slows and every per-second cost falls.
- **Skip the first 20 minutes after a restart:** the GameBot population ramps in over 15 minutes
  (`bots/autonomous/AutonomousPopulationProperties.cs:26-27`) and caches settle (est.).
- **Population pressure** above 300 GameBots slows their thinking (`bots/autonomous/AutonomousFidelityPolicy.cs:51`).
- **The whole cost per bot**, including what the meter cannot see, comes from comparing whole runs: `processCpuPct`
  with no bots, then N of a kind (cpu §5). That is the prototype's job.
- **Absolute ms differ between machines;** compare ratios.

## 8. Documentation

- `docs/fork/FORK.md`: a row in the server-code table after the realm-point waits (:62): the four new files, the five
  marked lines in GL and NS, the tests.
- `deploy/HANDOFF.md`: the setting in the settings paragraph (:87-93) and in the `./hdc update` paragraph (:271-273);
  a short "Bot CPU meter" paragraph under Day-to-day: `/botcpu`, the read command of 2.2, the CSV.
- `deploy/.env.example` (4.2).
- `docs/fork/verification/bot-cpu-meter-live.md`: the checks of 6.5 with the owner's results; its log reads follow the
  rule of 6.2.
- The release notes' "To update" (owner): `./hdc update`; nothing changes, the meter is off; to use it, `/botcpu on
  30` in game, or `HEARTHDAOC_BOT_CPU=on` and `./hdc up`. Players: nothing to do.

## 9. Risks and limits

- **Cross-kind costs** (section 7): the meter shows the first two through the `monster` `n` and its cost, and a
  GameBot kind that thinks more; the third needs runs with the kinds apart. They linger 5-60 s.
- **Wall, not CPU** (3.9): preemption, throttling and GC inflate absolute numbers; `loopWaitMs`, `throttledMs` and
  `gcPauseMs` say by how much at most. Ratios between kinds hold (est.).
- **Off-loop work is not charged** (3.9): `offLoopCpuPct` shows its size, `campThreadCpuPct` the camp planner's part;
  the other GameBot timers and bot saves stay in `offLoopCpuPct` (saves per kind: section 7). The 5-second
  ClassicQuests refresh is per player (`quests/QuestsMgr/ClassicQuests.cs:923`) and would matter for player bots;
  wrapping it is out of scope.
- **`calls` counts items, not thinks** (section 7): the `calls` ratio on a `COMPARE` line is easy to misread.
- **Effects count for the living that carries them,** not the caster: a damage-over-time a bot puts on a monster costs
  in `monster`. The same for every kind, so comparisons hold (cpu §7).
- **Overhead** while on: an est. 1.4-5.4% of one core (3.11). On a server already near 33 ms a tick, turning it on
  could push stages over budget; `tps` staying below 30.0 for several minutes or new `overBudget` counts in
  `SERVER_WORK` mean turn it off.
- **Log volume** on the live server: an est. 6-8 lines a minute at 200-450 characters, about 3-4 MB a day, inside a
  log that rotates daily and above 100 MB and keeps 7 days (`config/logconfig.xml:19-27`). Docker's own copy is bounded
  too (`deploy/compose.yml:31-35`). The CSV: about 2.5 MB a day, capped at 50 MB plus one old file.
- **`piloted` is always empty today** (section 1). If `/bot` comes back, its think lands in `unattributed` until H5.
- **`/bot` for a GM now runs `/botcpu`** (2.4): `/bot on`, from the old pilot habit, turns the meter on. It writes a
  `BOT_CPU_STATE` line and stops with `/botcpu off` (or `/bot off`).
- **The prototype's contract** (2.5): a player bot whose client neither implements `IPlayerBotClient` nor derives
  from `BotDummyClient` is counted as `player`; one not registered in `ClientService`, or not playing, has its costs
  charged with `n` 0 (`msPerBotSec=-`).
- **`allocMBSec` stalls on .NET 10** (3.9): `-` while the runtime's allocation counter stands still, minutes to
  hours, and a high minute after may carry them. The kind lines and `SERVER_MEMORY`'s `topAllocations` are not
  affected.
- **A report fault turns the meter off** (3.7): the log shows `BOT_CPU_ERROR` and `off source=error`, and the open
  window is lost. The server goes on; `/botcpu on` starts a new run.
- **Upstream churn:** five marked lines in GL (OpenDAoC's game loop) and NS. A sync that moves them fails the source
  checks or the build. The meter also mirrors stage names and owner members (6.2), which a sync can rename.
- **Not run on a server.** The overhead and every cost above come from code reading and local micro-benchmarks
  (cpu §3, README Verification rows 3-5). The first `BOT_CPU_PROCESS` lines firm them up.

## 10. Out of scope

- The player-bot prototype (`proto/player-bots`): the client with no socket, the driver, the spawner, promotion and the
  data quest, its runs and its image (README section 2).
- `/botcpu heap` and retained heap per bot (4.5).
- Hooks H4, H5 and H8 (3.6); the GameBot fidelity mix and a think rate per kind, which need a counter in `BotBrain`
  (cpu §4.6); monster cost by region; charging effects to their caster; wrapping off-loop work such as the
  ClassicQuests refresh.
- Exporting the numbers elsewhere (OpenTelemetry, `Metrics/MeterRegistry.cs`), a launcher card or a dashboard.
- Upstream: nothing goes upstream (contributions are paused).

## 11. Rulings

Every choice the research left open, as this spec settles it. The owner may change any. Rulings 3, 4, 5, 15, 21 and
26 were the owner's to answer: the defaults, 2026-10-18 (Read this first); the rest are settled from the research and
the code check.

1. **Names** (owner, 2026-10-14: its own PR, off by default)**:** property `bot_cpu_meter`, category `server`, field
   `BOT_CPU_METER` in `BotCpuScript`, code default false; `HEARTHDAOC_BOT_CPU` (on/off, case ignored, default off);
   `--bot-cpu`; "Bot CPU meter: on|off".
2. **The property is read once, at start.** `/botcpu on|off` overrides it until the next start and never writes it;
   `&serverproperties refresh` does not toggle a running meter.
3. **`/botcpu on [minutes]`** (owner: default, 2026-10-18), 1-1440, stops by itself; with no minutes it runs until `off` or the
   restart; on an already running meter it sets or clears the stop time and writes the `on` line again.
4. **`/botcpu heap` is not in the live PR** (owner: default, 2026-10-18; 4.5). The rest of the research's commands stay: no
   arguments, `on`, `off`, `top [kind]`, `reset`.
5. **`/botcpu` is GM** (owner: default, 2026-10-18; `ePrivLevel.GM`), like `/diag` and `/epic`.
6. **H1 hands the pool call to the meter** when on, so the meter can time nested calls; off, the pool call is
   unchanged.
7. **H6 is taken**, as two marked lines around NS:28-31, charging `gamebot` / `Fleet`, with the nesting rule of 3.5.
8. **H5 is not taken:** posted actions go to `unattributed` in their service's stage (3.6).
9. **H4 is not taken:** no `cpuToWall`; schedstat run-queue waits, cgroup throttling and GC pauses, read once a
   minute, take its place (3.9).
10. **H8 is not taken:** per-bot counters in a `ConditionalWeakTable` (3.10).
11. **The owner is read before the callback, and no meter code throws into the server.** On the loop: the per-item
    `try` charges `unattributed` (3.2), and a failed per-call setup falls back to the original delegate (3.1). On the
    report side: every timer tick, queued job and server event handler is caught; the first fault logs
    `BOT_CPU_ERROR` and turns the meter off (`off source=error`); a failed CSV write only loses its row (2.3, 3.7).
12. **Pets:** the meter's own walk to the first owner that is not a pet, at most 8 steps; a companion's pets count as
    `companion`; one `Pets` column per kind, not split by stage (3.3).
13. **Reaper:** the killer's kind when the killer (after the pet walk) is a bot or player kind, otherwise the killed's
    (3.3).
14. **`monster`** covers every living that is not a bot, a player or a pet (guards, merchants, keep doors); a
    non-living owner is `unattributed` (2.5).
15. **`playerbot`** (owner: default, 2026-10-18) = a `GamePlayer` whose client implements `IPlayerBotClient` or is a
    `BotDummyClient`; the prototype's client implements `IPlayerBotClient` (deriving from `BotDummyClient` also works)
    and is registered in `ClientService` (2.5).
16. **`n`:** GameBots from the registry sampled each second (corpses included, est.); player kinds from client
    presence in `ClientService.BeginTick`, counting a client that `IsPlaying` (Linkdead included) with an Active
    `Player`, so the character screen and loading count nothing; companions and monsters from live-brain presence in
    `NpcService` (3.3, 3.8).
17. **Windows** of 60 s from the moment the meter turns on, not clock minutes; a partial window at off, with its real
    `win`; per-second figures divide by the exact length; a final window under 5 s is dropped (2.2).
18. **Formats:** the fields, units and rounding of 2.1, invariant culture, `-` for a missing value and for anything
    divided by 0 bot-seconds or 0 calls; a kind with nothing gets no line; stages left out at 0.000.
19. **Top 5** as one `BOT_CPU_TOP` line per bot kind per window; spread and top over bots charged in the window,
    nearest rank, ties by name; `Fleet` in no bot's figures (3.10).
20. **Compare pairs:** `playerbot/gamebot`, `piloted/gamebot` and `player/gamebot`, when both have `n` ≥ 1.0; fields
    `cpu`, `alloc`, `calls`, `usPerCall`, with `calls` read as items per bot, not thinks (2.1, 7). No `think` ratio:
    the prototype's think is in `Timer` with its other timers, so it would not be like for like.
21. **CSV** (owner: default, 2026-10-18)**:** `/data/logs/bot-cpu.csv`, one row per window and kind, the window's process values
    repeated, stage columns as whole-kind totals from the raw ticks; renamed `.1` above 50 MB or when its header is
    not the current one.
22. **Process line:** the fields of 2.1; loop threads found by their `comm` names; the camp planner thread reported
    on its own; `tps` from game time; a schedstat of 0 reads as `-`, and so does an allocation counter that did not
    move (3.9).
23. **`BOT_CPU_STATE` lines** for every on and off (and a stop-time change), with the GM's name; `off source=error`
    after a `BOT_CPU_ERROR` line.
24. **File split:** `BotCpuMeter.cs`, `BotCpuReport.cs`, `BotCpuScript.cs`, `BotCpuCommand.cs`; tests
    `UT_BotCpuMeter`, `UT_BotCpuReport`, `UT_BotCpuScript` and `deploy/tests/test_bot_cpu.py`.
25. **Stage columns** as in 3.4: the 17 stages under short names, the three quest services merged into `Quests`, plus
    `Fleet`, `Pets` and `Other`.
26. **`piloted` is kept** (owner: default, 2026-10-18): cheap and ready if `/bot` returns; the other choice folds it into `player`
    and drops the pilot lookup on player-owned items (3.2).
27. **The report side runs one step at a time** under one lock, with a first-in-first-out job queue; a command on the
    game loop only turns `Enabled` off (for `off`) and queues a job, and reads the result the report published;
    `Enabled` goes on only in an open job; the started and stopped handlers run their job at once (3.7).
28. **The fleet scope** clears its flag whenever it ends and drops a scope cut by `off`; the wrapper carries the flag
    and the stage to the workers (3.1, 3.5).
29. **`/bot` for a GM resolves to `/botcpu`** (2.4), and is documented rather than blocked.
30. **Chat:** the exact replies of 2.4, times in the server's local time.
