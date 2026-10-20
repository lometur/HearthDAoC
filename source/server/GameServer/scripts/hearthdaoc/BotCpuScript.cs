using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DOL.Events;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's server side (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). The
// switch (section 4): the bot_cpu_meter server property, off in code, is set from HEARTHDAOC_BOT_CPU at every start
// (4.2) and read once, at start: on, the meter starts with the server (4.1). A GM's /botcpu turns the meter on or off
// until the next start and never writes the property (4.3). The process readers (3.9): the /proc and cgroup parsers,
// pure functions of a file's text, their reads, the process sample and a window's process values. The CSV (2.3):
// logs/bot-cpu.csv, its header check and its rotation to bot-cpu.csv.1 above 50 MB. The guarded log writer (3.7): a
// failed CSV write logs one warning and never escapes. The report side (3.7, 3.8, 4.1, 4.3, 4.4): a command only turns
// the meter off or queues a job; the jobs, the 1-second timer and the server's started and stopped handlers run one at
// a time under the report lock, off the game loop: runs, windows, the stop time, the published result, and a catch that
// turns the meter off at the first fault. BotCpuMeter is the hot path; BotCpuReport makes the numbers and the text.
public static class BotCpuScript
{
    [ServerProperty("server", "bot_cpu_meter",
        "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log every minute; a GM's /botcpu turns it on or off until the next start",
        false)]
    public static bool BOT_CPU_METER;

    // ---- The log lines (2.1, 3.7)

    private static readonly Logger Log = LoggerManager.Create(typeof(BotCpuScript));

    // A test hook (6.1): set, the meter's log lines go to it instead of the server log. Null on the server.
    private static Action<ELogLevel, string> _logHook;

    // One line in the server log, at Info (the window and state lines) or Warning (BOT_CPU_ERROR, a failed CSV write).
    // It may throw: NLog here throws from log calls (3.7).
    private static void WriteLog(ELogLevel level, string line)
    {
        Action<ELogLevel, string> hook = _logHook;
        if (hook != null)
            hook(level, line);
        else if (level == ELogLevel.Warning)
        {
            if (Log.IsWarnEnabled)
                Log.Warn(line);
        }
        else if (Log.IsInfoEnabled)
            Log.Info(line);
    }

    // The guarded log writer (3.7): the line made and written in its own try, whose catch does nothing, for the lines
    // written once something has failed. The line is made inside the try too: it carries an exception's text, and
    // making that can throw.
    private static void TryWriteLog(ELogLevel level, Func<string> line)
    {
        try
        {
            WriteLog(level, line());
        }
        catch (Exception)
        {
            // Nothing: the meter's faults never reach the server.
        }
    }

    // ---- The process readers (3.9)

    // The camp planner thread's name, CampSnapshotPlanner (CPP:52), as Linux's comm holds it: cut to 15 characters.
    private const string CampThreadComm = "CampSnapshotPla";

    // Where the process sample reads; test hooks (6.1) point them at sample files, or at nothing.
    private static string _procSelf = "/proc/self";
    private static string _cgroupCpuStat = "/sys/fs/cgroup/cpu.stat";

    /// <summary>
    /// A thread's <c>/proc/self/task/&lt;tid&gt;/schedstat</c> (3.9): its first field, ns on a CPU, and its second,
    /// ns waiting on a run queue. Null (<c>-</c>) for an empty or missing file, text that is not two numbers, or a
    /// first field of 0: where the kernel keeps no scheduler statistics the file still exists and reads <c>0 0 0</c>.
    /// </summary>
    public static BotCpuSchedstat? ParseSchedstat(string text)
    {
        string[] fields = text?.Split((char[])null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return fields.Length >= 2 && Number(fields[0]) is long cpuNs && cpuNs > 0 && Number(fields[1]) is long waitNs
            ? new BotCpuSchedstat(cpuNs, waitNs)
            : null;
    }

    /// <summary>
    /// What a thread is to the process line, from its <c>/proc/self/task/&lt;tid&gt;/comm</c> (3.9), which Linux cuts
    /// to 15 characters: <c>GameLoop</c> and <c>GameLoop_Worker</c> (the pool's <c>GameLoop_Worker_&lt;n&gt;</c>,
    /// MT:100) are the loop threads, <c>CampSnapshotPla</c> the camp planner; <c>GameLoop_Watchd</c>,
    /// <c>GameLoop_BusyWa</c> and every other thread are left out.
    /// </summary>
    public static BotCpuThreadRole ThreadRoleOf(string comm)
    {
        string name = comm?.TrimEnd('\n') ?? string.Empty;
        if (name == GameLoop.THREAD_NAME || name.StartsWith(GameLoop.THREAD_NAME + "_Worker", StringComparison.Ordinal))
            return BotCpuThreadRole.Loop;

        return name.StartsWith(CampThreadComm, StringComparison.Ordinal)
            ? BotCpuThreadRole.Camp
            : BotCpuThreadRole.Other;
    }

    /// <summary>The <c>throttled_usec</c> of <c>/sys/fs/cgroup/cpu.stat</c> (cgroup v2, 3.9); null (<c>-</c>) without
    /// it.</summary>
    public static long? ParseThrottledUsec(string cpuStat) => NumberAfter(cpuStat, "throttled_usec");

    /// <summary>The <c>VmRSS</c> of <c>/proc/self/status</c>, in kB (3.9); null (<c>-</c>) without it.</summary>
    public static long? ParseVmRssKB(string status) => NumberAfter(status, "VmRSS:");

    // The number after key on its line of a "key value" file; null without it.
    private static long? NumberAfter(string text, string key)
    {
        foreach (string line in text?.Split('\n') ?? [])
        {
            string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == key)
                return Number(fields[1]);
        }

        return null;
    }

    // Digits only: no sign, space or separator.
    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    // A file's text; null when it is missing or cannot be read, which is not a fault (3.7): its fields are -.
    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The process values now (3.9), for a window's start or end: the process's CPU, each loop and camp thread's
    /// schedstat by tid, the container's throttling, the GC's pauses, full collections, allocation and heap, and the
    /// resident memory. A value the platform can't give (Windows, a file missing in the container) is null; nothing
    /// here throws for it.
    /// </summary>
    public static BotCpuProcessSample TakeProcessSample()
    {
        string procSelf = _procSelf;
        return new BotCpuProcessSample(Environment.ProcessorCount, ProcessCpu(),
            ReadThreads(Path.Combine(procSelf, "task")), ParseThrottledUsec(ReadText(_cgroupCpuStat)),
            GC.GetTotalPauseDuration(), GC.CollectionCount(2), GC.GetTotalAllocatedBytes(false),
            GC.GetTotalMemory(false), ParseVmRssKB(ReadText(Path.Combine(procSelf, "status"))));
    }

    // Process.TotalProcessorTime; null when the platform can't give it.
    private static TimeSpan? ProcessCpu()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The loop and camp threads by tid, listed again at each sample; null when the task folder can't be listed. A
    // thread whose schedstat is gone by the time it is read has left: it is not listed.
    private static Dictionary<int, BotCpuThreadSample> ReadThreads(string taskFolder)
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(taskFolder);
        }
        catch (Exception)
        {
            return null;
        }

        Dictionary<int, BotCpuThreadSample> threads = [];
        foreach (string folder in folders)
        {
            if (Number(Path.GetFileName(folder)) is not long tid || tid > int.MaxValue)
                continue;

            BotCpuThreadRole role = ThreadRoleOf(ReadText(Path.Combine(folder, "comm")));
            if (role == BotCpuThreadRole.Other)
                continue;

            string schedstat = ReadText(Path.Combine(folder, "schedstat"));
            if (schedstat != null)
                threads[(int)tid] = new BotCpuThreadSample(role, ParseSchedstat(schedstat));
        }

        return threads;
    }

    /// <summary>
    /// A window's process values (2.1 <c>BOT_CPU_PROCESS</c>, 3.9): the differences of two samples
    /// <paramref name="seconds"/> apart, a negative difference counted as 0, and the memory at the end. The loop and
    /// camp threads are those of the <paramref name="later"/> listing: a thread that left in the window is dropped, and
    /// one that started in it counts from zero. A loop thread whose schedstat reads 0 makes
    /// <c>loopThreadsCpuPct</c>, <c>offLoopCpuPct</c>, <c>campThreadCpuPct</c> and <c>loopWaitMs</c> <c>-</c>; so do
    /// no loop thread and threads listed at one end only. With loop threads read, no camp thread is 0: it starts with
    /// the first camp planning (CPP:52). The window a server stop writes has no loop thread: the server joins the game
    /// loop's threads and the camp thread before the stopped event (GameServer.cs:1013-1015), so its four are <c>-</c>.
    /// </summary>
    public static BotCpuProcess ProcessWindow(BotCpuProcessSample earlier, BotCpuProcessSample later, double seconds)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        double? loopThreadsCpuPct = null;
        double? loopWaitMs = null;
        double? campThreadCpuPct = null;
        if (earlier.Threads != null && later.Threads != null)
        {
            int loopThreads = 0;
            bool loopUnread = false;
            bool campUnread = false;
            long loopNs = 0;
            long waitNs = 0;
            long campNs = 0;
            foreach ((int tid, BotCpuThreadSample thread) in later.Threads)
            {
                if (thread.Role == BotCpuThreadRole.Other)
                    continue;

                bool loop = thread.Role == BotCpuThreadRole.Loop;
                if (loop)
                    loopThreads++;

                // A thread not listed before, or whose tid was another thread's, started in the window.
                BotCpuSchedstat? start = new BotCpuSchedstat(0, 0);
                if (earlier.Threads.TryGetValue(tid, out BotCpuThreadSample before) && before.Role == thread.Role)
                    start = before.Schedstat;

                if (thread.Schedstat is not BotCpuSchedstat end || start is not BotCpuSchedstat from)
                {
                    loopUnread |= loop;
                    campUnread |= !loop;
                    continue;
                }

                if (loop)
                {
                    loopNs += Positive(end.CpuNs - from.CpuNs);
                    waitNs += Positive(end.WaitNs - from.WaitNs);
                }
                else
                    campNs += Positive(end.CpuNs - from.CpuNs);
            }

            if (loopThreads > 0 && !loopUnread)
            {
                loopThreadsCpuPct = PercentOfOneCore(loopNs / 1e6, seconds);
                loopWaitMs = waitNs / 1e6;
            }

            // No camp thread gives 0, but only once the loop threads show the readers work: a running server always
            // has loop threads, so none found means the threads could not be told or read (no schedstat files,
            // unreadable comm files), the camp thread's included, or that they have ended (a server stop).
            if (loopThreads > 0 && !loopUnread && !campUnread)
                campThreadCpuPct = PercentOfOneCore(campNs / 1e6, seconds);
        }

        double? processCpuPct = earlier.ProcessCpu is TimeSpan processStart && later.ProcessCpu is TimeSpan processEnd
            ? PercentOfOneCore(Math.Max(0, (processEnd - processStart).TotalMilliseconds), seconds)
            : null;
        double? throttledMs = earlier.ThrottledUsec is long throttledStart && later.ThrottledUsec is long throttledEnd
            ? Positive(throttledEnd - throttledStart) / 1000.0
            : null;
        double? allocMBSec = seconds > 0
            ? Positive(later.AllocatedBytes - earlier.AllocatedBytes) / (double)BytesPerMB / seconds
            : null;

        return new BotCpuProcess(later.Cores, processCpuPct, loopThreadsCpuPct, campThreadCpuPct, loopWaitMs,
            throttledMs, Math.Max(0, (later.GcPause - earlier.GcPause).TotalMilliseconds),
            Positive(later.Gen2 - earlier.Gen2), allocMBSec, later.HeapBytes / (double)BytesPerMB,
            later.RssKB / 1024.0);
    }

    private const long BytesPerMB = 1_048_576;

    private static long Positive(long value) => Math.Max(0, value);

    // ms of CPU over the window as % of one core: 1,000 ms a second is 100%; null for a window with no length.
    private static double? PercentOfOneCore(double ms, double seconds) => seconds > 0 ? ms / seconds / 10 : null;

    // ---- The CSV file (2.3)

    /// <summary>The CSV's name, in the server's <c>logs</c> folder.</summary>
    public const string CsvFileName = "bot-cpu.csv";

    /// <summary>Above this the CSV is renamed <c>bot-cpu.csv.1</c> before the next append (2.3): 50 MB, about 20 days
    /// of continuous running.</summary>
    public const long CsvMaxBytes = 50 * BytesPerMB;

    private static readonly UTF8Encoding Utf8 = new(false);

    // A test hook (6.1): set, it writes the window's bytes to the CSV in place of the stream's own Write, so a test can
    // cut a write short as a full disk does. Null on the server.
    private static Action<FileStream, byte[]> _csvWriteHook;

    /// <summary><c>logs/bot-cpu.csv</c> in the server's folder (<see cref="AppContext.BaseDirectory"/>): in the image,
    /// <c>/data/logs/bot-cpu.csv</c>, as that <c>logs</c> folder is the volume's (<c>deploy/entrypoint.sh:66</c>).
    /// </summary>
    public static string CsvPath => Path.Combine(AppContext.BaseDirectory, "logs", CsvFileName);

    /// <summary>
    /// Appends a window's <paramref name="rows"/> to the CSV at <paramref name="path"/> (2.3), as UTF-8 lines ending in
    /// LF, with no BOM. Its folder is created when missing. Before the append, a file above
    /// <see cref="CsvMaxBytes"/>, or whose first line is not <see cref="BotCpuReport.CsvHeader"/> (an older release's
    /// columns), is renamed <c>.1</c>, replacing an older one; a new, missing or empty file starts with the header, and
    /// a last line left with no line end is ended first. The check runs before every append, the first after the meter
    /// turns on among them. Never throws: a failed write, whatever the exception, logs one warning and returns false,
    /// and its rows are lost; a write cut short is taken back, so the file is left as it was. The meter goes on, and
    /// the next window's append tries again.
    /// </summary>
    public static bool AppendCsv(string path, IReadOnlyList<string> rows)
    {
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // An empty file is not renamed: it holds nothing to keep, and renaming it would replace the older .1 with
            // nothing. It gets the header in place, as a missing file does.
            FileInfo file = new(path);
            bool lineEnded = true;
            if (file.Exists && file.Length > 0)
            {
                if (file.Length > CsvMaxBytes || !StartsWithHeader(path))
                    File.Move(path, path + ".1", true);
                else
                    lineEnded = EndsWithLineEnd(path);
            }

            // Unbuffered, so the bytes reach the file inside the write below, where a failure can be undone, and not in
            // Dispose.
            using FileStream stream = new(path, new FileStreamOptions
                { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, BufferSize = 0 });
            long length = stream.Length;
            StringBuilder text = new();
            if (length == 0)
                text.Append(BotCpuReport.CsvHeader).Append('\n');
            else if (!lineEnded)
                text.Append('\n');
            foreach (string row in rows)
                text.Append(row).Append('\n');

            byte[] bytes = Utf8.GetBytes(text.ToString());
            try
            {
                Action<FileStream, byte[]> hook = _csvWriteHook;
                if (hook != null)
                    hook(stream, bytes);
                else
                    stream.Write(bytes);
            }
            catch (Exception)
            {
                // A write cut short, on a full disk say, leaves part of a row or of the header with no line end: the
                // next row would join it, and a cut header would read as another release's at the next append and
                // be renamed .1 over the old file. So the file goes back to its length before the write, which needs
                // no space; the write's own exception is the one reported.
                try
                {
                    stream.SetLength(length);
                }
                catch (Exception)
                {
                    // The file keeps the cut part; the next append ends its line first.
                }

                throw;
            }

            return true;
        }
        catch (Exception exception)
        {
            TryWriteLog(ELogLevel.Warning, () => $"Bot CPU meter: could not write this window's rows to {path}, so " +
                $"they are lost; the meter goes on: {exception}");
            return false;
        }
    }

    // Whether the file's first line is the CSV's header; reads no more than the header and its line end. A file that
    // is the header alone, with no line end, is kept: EndsWithLineEnd ends it before the rows.
    private static bool StartsWithHeader(string path)
    {
        char[] start = new char[BotCpuReport.CsvHeader.Length + 2];
        int read;
        using (StreamReader reader = new(path, Utf8, true))
            read = reader.ReadBlock(start, 0, start.Length);

        string text = new(start, 0, read);
        int end = text.IndexOf('\n');
        return (end >= 0 ? text[..end] : text).TrimEnd('\r') == BotCpuReport.CsvHeader;
    }

    // Whether the file's last byte is a line end. A hand edit, or a write cut short whose taking back failed, can leave
    // a last line with none, and the next window's first row would join it.
    private static bool EndsWithLineEnd(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return true;

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    // ---- The report side (3.7, 3.8, 4.1, 4.3, 4.4)

    /// <summary>The report's timer ticks once a second (3.7, 3.8): it samples the registry, closes a window at the
    /// first tick at or after its 60 s, and checks the stop time.</summary>
    public const int TickMilliseconds = 1000;

    // Every change to the report's state runs under this lock (3.7), taken only on the report side: by a queued work
    // item (waiting for it), a timer tick (skipping its second while it is held) and the server's started and stopped
    // handlers. The game-loop thread never takes it.
    private static readonly object ReportLock = new();

    // The jobs the commands queue, first in first out (3.7); whoever holds the lock runs every job waiting, in order.
    private static readonly ConcurrentQueue<Job> Jobs = new();

    // The open run, or null: read and written under the report lock only.
    private static MeterRun _open;

    // The last result the report published, swapped in whole (3.7); /botcpu reads only this.
    private static BotCpuResult _result = BotCpuResult.Off;

    // Test hooks (6.1), null on the server: the queued work items (in place of ThreadPool.QueueUserWorkItem), the
    // 1-second timer (in place of a System.Threading.Timer), the server's local time, the Stopwatch timestamp, the
    // server's time zone (in place of TimeZoneInfo.Local, for a stop time's way through UTC) and the CSV's path.
    private static Action<WaitCallback> _queueHook;
    private static Func<TimerCallback, object, IDisposable> _timerHook;
    private static Func<DateTime> _nowHook;
    private static Func<long> _timestampHook;
    private static TimeZoneInfo _zoneHook;
    private static string _csvFile;

    // A job of the report side: what a command asked for, run under the report lock in its turn, and its step name for
    // BOT_CPU_ERROR.
    private sealed record Job(string Step, Action Work);

    // An instant on the report's three clocks: the Stopwatch timestamp a window is measured with (2.2), the server's
    // local time it is shown in, and the game time its ticks are counted from (3.8). A window starts and ends at one.
    private readonly record struct Instant(long Timestamp, DateTime Time, long GameTime)
    {
        internal static Instant Now() => new(BotCpuScript.Timestamp(), LocalNow(), GameLoop.GameLoopTime);
    }

    // A stop time (4.3): the server's local time it shows, and the Stopwatch timestamp the timer checks, so the run
    // stops after its minutes even when the local clock moves meanwhile (summer time begins or ends).
    private readonly record struct StopTime(DateTime Time, long Timestamp)
    {
        // The stop time <minutes> from now: shown as the local time of the instant it stops, that many minutes on in
        // UTC, so a change of the local clock before then shows the time the run really stops at.
        internal static StopTime In(int minutes)
        {
            TimeZoneInfo zone = _zoneHook ?? TimeZoneInfo.Local;
            Func<DateTime> now = _nowHook;
            DateTime utc = now == null
                ? DateTime.UtcNow
                : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(now(), DateTimeKind.Unspecified), zone);
            return new StopTime(TimeZoneInfo.ConvertTimeFromUtc(utc.AddMinutes(minutes), zone),
                BotCpuScript.Timestamp() + minutes * 60L * Stopwatch.Frequency);
        }
    }

    // A run (3.7): from its Enable to its close, with its number, how and when it turned on, its stop time, its timer,
    // its open window and its totals since on or reset. Read and written under the report lock only.
    private sealed class MeterRun
    {
        internal int Number;
        internal bool ByCommand;
        internal DateTime OnSince;
        // The same instant as a Stopwatch timestamp: its first window's start, which an off command's instant is
        // compared with (3.7).
        internal long OnSinceTimestamp;
        internal StopTime? StopsAt;
        internal IDisposable Timer;

        // The open window (3.7): its start, the slots' totals then (its baseline), the process values then and the
        // Stopwatch timestamp they were taken at, and the registry's samples so far (3.8).
        internal Instant WindowStart;
        internal BotCpuTotals Baseline;
        internal BotCpuProcessSample ProcessStart;
        internal long ProcessTaken;
        internal BotCpuSamples Samples;

        // The last window written, its process values, and the totals since on or reset (2.4).
        internal BotCpuWindow Last;
        internal BotCpuProcess LastProcess;
        internal BotCpuSince Since;
    }

    /// <summary>
    /// The last result the report published (3.7): an immutable object, swapped in whole at each change (on, a window,
    /// reset, a new stop time, off). <c>/botcpu</c> reads only this and never builds a report. Never null.
    /// </summary>
    public static BotCpuResult Result => Volatile.Read(ref _result);

    /// <summary>The server's local time (<c>TZ</c>), as the chat shows it (2.4), the stop time included (4.3).
    /// </summary>
    public static DateTime LocalNow() => _nowHook?.Invoke() ?? DateTime.Now;

    // The Stopwatch timestamp a window is measured with (2.2).
    private static long Timestamp() => _timestampHook?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>The time since <paramref name="timestamp"/> on the Stopwatch a window is measured with (2.2): a change
    /// of the server's local clock (summer time) does not move it, so <c>/botcpu</c>'s minutes on stay true (2.4).
    /// </summary>
    public static TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromSeconds(SecondsBetween(timestamp, Timestamp()));

    private static double SecondsBetween(long start, long end) => (end - start) / (double)Stopwatch.Frequency;

    // Where the windows' rows go: logs/bot-cpu.csv (2.3).
    private static string CsvFile => _csvFile ?? CsvPath;

    /// <summary>
    /// <c>/botcpu on [minutes]</c>, on its game-loop thread (3.7): queues the on job and returns at once, with no file,
    /// <c>/proc</c> read or log write. <see cref="BotCpuMeter.Enabled"/> is unchanged until the job runs. The job opens
    /// a run when none is open (<c>BOT_CPU_STATE on source=command</c>), or sets or clears the stop time of the running
    /// one and writes that line again (4.3). Returns the stop time, <paramref name="minutes"/> from now, or null for
    /// none, so the reply shows the time the job uses: the local time of the instant it stops, so across a change of
    /// the local clock (summer time) it shows the time the run really stops at. The timer checks it as that many
    /// minutes of the Stopwatch, so such a change neither shortens nor lengthens the run.
    /// </summary>
    /// <param name="by">The GM's name, for the line.</param>
    /// <param name="minutes">1-1440 (the command checks it), or null: until off or the next start.</param>
    public static DateTime? CommandOn(string by, int? minutes)
    {
        StopTime? stopsAt = minutes is int stopIn ? StopTime.In(stopIn) : null;
        Queue("on", () => OnJob(by, minutes, stopsAt));
        return stopsAt?.Time;
    }

    /// <summary>
    /// <c>/botcpu off</c>, on its game-loop thread (3.7, 4.4): <see cref="BotCpuMeter.Disable"/>, so metering stops at
    /// once, then the off job queued; no file, <c>/proc</c> read or log write. The job closes the open run, if any:
    /// its final window, then <c>BOT_CPU_STATE off source=command</c>. The final window ends at this command's
    /// instant, when metering stopped, not when the job runs. Returns that window's length in seconds, measured from
    /// the published open window's start to that instant as the job measures it, so the reply and the job agree on
    /// whether it is written (5 s, 2.2): below 0 when a tick started the open window after that instant, and the job
    /// then writes none. Null when the published result is off.
    /// </summary>
    /// <param name="by">The GM's name, for the line.</param>
    public static double? CommandOff(string by)
    {
        BotCpuMeter.Disable();
        Instant stopped = Instant.Now();
        BotCpuResult result = Result;
        Queue("off", () => OffJob(by, stopped));
        return result.On ? SecondsBetween(result.WindowStartTimestamp, stopped.Timestamp) : null;
    }

    /// <summary><c>/botcpu reset</c>, on its game-loop thread (3.7): queues the reset job, which starts the open run's
    /// totals since on again ("Since reset"); the windows go on. With no run open the job does nothing.</summary>
    public static void CommandReset() => Queue("reset", ResetJob);

    /// <summary>
    /// The property's start (4.1): read once, before the game loop starts (<c>GameServer.cs:510</c>, :517), so the
    /// first tick is metered. On, a run opens at once on this thread, under the report lock, not queued:
    /// <c>BOT_CPU_STATE on source=property</c>. Nothing escapes to the server's start.
    /// </summary>
    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args) => RunNow("start", () =>
    {
        if (BOT_CPU_METER && _open == null)
            Open(false, null, BotCpuReport.StateOnByPropertyLine);
    });

    /// <summary>
    /// The server's stop (4.1), after <c>GameLoop.Exit</c> (<c>GameServer.cs:1013</c>, :1015): the jobs waiting, then
    /// the open run closed at once on this thread, under the report lock: its partial window, then
    /// <c>BOT_CPU_STATE off source=stop</c>. Nothing escapes to the server's stop.
    /// </summary>
    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args) => RunNow("stop", () =>
    {
        if (_open is MeterRun run)
            Close(run, BotCpuOffSource.Stop, null);
    });

    // A command's job (3.7): into the queue, then a work item for the thread pool, as PublishBotAiDelay does
    // (GameLoopWorkMetrics.cs:98-104).
    private static void Queue(string step, Action work)
    {
        Jobs.Enqueue(new Job(step, work));
        Action<WaitCallback> hook = _queueHook;
        if (hook != null)
            hook(RunQueuedJobs);
        else
            ThreadPool.QueueUserWorkItem(RunQueuedJobs);
    }

    // A queued work item (3.7): it waits for the lock, then runs every job waiting, in order. An earlier item may
    // already have run its job: it then finds none.
    private static void RunQueuedJobs(object state)
    {
        try
        {
            lock (ReportLock)
                RunJobs();
        }
        catch (Exception)
        {
            // Nothing: each job has its own catch; an exception that escaped a thread-pool callback would end the
            // process (3.7).
        }
    }

    // A server event's job (4.1): at once, on the event's own thread, under the lock, after the jobs waiting.
    private static void RunNow(string step, Action work)
    {
        try
        {
            lock (ReportLock)
            {
                RunJobs();
                Guarded(step, work);
            }
        }
        catch (Exception)
        {
            // Nothing: an exception here never reaches the server's start or stop (4.1).
        }
    }

    // The timer's tick (3.7, 3.8): it skips its second while the lock is held. Holding it, it runs every job waiting,
    // then its own work, for its own run: a tick of a run already closed (a tick that waited, or a disposed timer's
    // last) does nothing.
    private static void OnTimer(object state)
    {
        try
        {
            if (!Monitor.TryEnter(ReportLock))
                return;

            try
            {
                RunJobs();
                Guarded("tick", () => Tick(state as MeterRun));
            }
            finally
            {
                Monitor.Exit(ReportLock);
            }
        }
        catch (Exception)
        {
            // Nothing: an exception that escaped a timer callback would end the process (3.7).
        }
    }

    // Every job waiting, in order, each in its own catch (3.7). Under the report lock.
    private static void RunJobs()
    {
        while (Jobs.TryDequeue(out Job job))
            Guarded(job.Step, job.Work);
    }

    // A step of the report side inside the catch of 3.7: its first exception turns the meter off. It never rethrows.
    private static void Guarded(string step, Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            Fail(step, exception);
        }
    }

    // The catch of 3.7: Enabled false, the timer disposed, the run closed with no final window (its counts are
    // dropped), one Warn line BOT_CPU_ERROR <step>: <exception>, then BOT_CPU_STATE off source=error, each log call in
    // its own try whose catch does nothing. Never throws. Under the report lock.
    private static void Fail(string step, Exception exception)
    {
        BotCpuMeter.Disable();
        MeterRun run = _open;
        _open = null;
        Volatile.Write(ref _result, BotCpuResult.Off);
        try
        {
            run?.Timer?.Dispose();
        }
        catch (Exception)
        {
            // Nothing: the run is closed whatever its timer does.
        }

        TryWriteLog(ELogLevel.Warning, () => BotCpuReport.ErrorLine(step, exception));
        TryWriteLog(ELogLevel.Info, () => BotCpuReport.StateOffLine(BotCpuOffSource.Error));
    }

    // The on job (3.7, 4.3): with a run open, its new stop time (or none) and the on line again; else a new run.
    private static void OnJob(string by, int? minutes, StopTime? stopsAt)
    {
        string line = BotCpuReport.StateOnByCommandLine(by, minutes);
        if (_open is not MeterRun run)
        {
            Open(true, stopsAt, line);
            return;
        }

        run.StopsAt = stopsAt;
        WriteLog(ELogLevel.Info, line);
        Publish(run);
    }

    // The off job (3.7): closes the open run, its final window ending at the command's instant, when metering stopped;
    // with no run, nothing. A command that came before this run opened (an off queued before the on job that opened
    // it) did not stop this run: its close does, now. A command that came after it opened but before its open window
    // started (a tick closed the minute between the command's instant and its job's place in the queue) leaves a
    // final window of negative length: none is written, as metering stopped before that window started.
    private static void OffJob(string by, Instant stopped)
    {
        if (_open is MeterRun run)
            Close(run, BotCpuOffSource.Command, by, stopped.Timestamp >= run.OnSinceTimestamp ? stopped : null);
    }

    // The reset job (2.4): the open run's totals since on start again; the windows go on. With no run, nothing.
    private static void ResetJob()
    {
        if (_open is not MeterRun run)
            return;

        run.Since = BotCpuSince.Start(true);
        Publish(run);
    }

    // A run opens (3.7): Enable first (the slot list the first time, a new per-bot table and the next run number, then
    // Enabled), then a fresh baseline and the first window's start, so counts from an earlier run never leak in; then
    // its timer, its on line and its result. Under the report lock.
    private static void Open(bool byCommand, StopTime? stopsAt, string line)
    {
        MeterRun run = new()
        {
            Number = BotCpuMeter.Enable(),
            ByCommand = byCommand,
            StopsAt = stopsAt,
            Since = BotCpuSince.Start(false),
        };
        BotCpuTotals baseline = BotCpuMeter.TakeSnapshot();
        BotCpuProcessSample process = TakeProcessSample();
        Instant start = Instant.Now();
        StartWindow(run, baseline, process, start.Timestamp, start, new BotCpuSamples());
        run.OnSince = start.Time;
        run.OnSinceTimestamp = start.Timestamp;
        _open = run;
        run.Timer = NewTimer(run);
        WriteLog(ELogLevel.Info, line);
        Publish(run);
    }

    // The run's 1-second System.Threading.Timer (3.7), made when the run opens, with the run as its state.
    private static IDisposable NewTimer(MeterRun run)
    {
        Func<TimerCallback, object, IDisposable> hook = _timerHook;
        return hook != null
            ? hook(OnTimer, run)
            : new System.Threading.Timer(OnTimer, run, TickMilliseconds, TickMilliseconds);
    }

    // A window starts (3.7): its baseline, the process values and when they were taken, its instant and the samples so
    // far.
    private static void StartWindow(MeterRun run, BotCpuTotals baseline, BotCpuProcessSample process,
        long processTaken, Instant start, BotCpuSamples samples)
    {
        run.Baseline = baseline;
        run.ProcessStart = process;
        run.ProcessTaken = processTaken;
        run.WindowStart = start;
        run.Samples = samples;
    }

    // A second of the run (3.8, 4.3): the registry sampled, the window closed at the first tick at or after its 60 s,
    // then the stop time checked on the Stopwatch: the first tick at or after it closes the run (off source=timer).
    private static void Tick(MeterRun run)
    {
        if (run == null || run != _open)
            return;

        run.Samples = run.Samples.Add(AutonomousBotRegistry.PopulationForBrainTick);
        Instant now = Instant.Now();
        if (SecondsBetween(run.WindowStart.Timestamp, now.Timestamp) >= BotCpuReport.WindowSeconds)
        {
            WriteWindow(run, now);
            Publish(run);
        }

        if (run.StopsAt is StopTime stop && now.Timestamp >= stop.Timestamp)
            Close(run, BotCpuOffSource.Timer, null);
    }

    // A window (3.7, 3.8), under the lock, from its start to its end instant: the slots' totals less its baseline (the
    // slot list copied under its lock, each value read with Volatile.Read), each bot's counter since the report last
    // read it, the process values, the game ticks and the registry samples. A window of 5 s or more writes its lines,
    // then its CSV rows, whose failure only loses them (2.3), and counts in the totals since on; a shorter final window
    // writes nothing (2.2). The next window starts at its end, with these totals as its baseline. The process values
    // are read here, off the loop: a final window that ends at an off command's instant has them up to its job,
    // moments later, so each of their rates divides by the time between their own two samples, not by the window's.
    private static void WriteWindow(MeterRun run, Instant end)
    {
        BotCpuTotals totals = BotCpuMeter.TakeSnapshot();
        BotCpuProcessSample process = TakeProcessSample();
        long processTaken = Timestamp();
        double seconds = SecondsBetween(run.WindowStart.Timestamp, end.Timestamp);
        BotCpuWindow window = BotCpuReport.Window(BotCpuReport.Difference(totals, run.Baseline), seconds,
            Stopwatch.Frequency, end.GameTime - run.WindowStart.GameTime, GameLoop.TickDuration, run.Samples,
            ListBots());
        BotCpuProcess values = ProcessWindow(run.ProcessStart, process, SecondsBetween(run.ProcessTaken, processTaken));
        StartWindow(run, totals, process, processTaken, end, run.Samples.NextWindow());
        if (window == null)
            return;

        foreach (string line in BotCpuReport.Lines(window, values))
            WriteLog(ELogLevel.Info, line);

        IReadOnlyList<string> rows = BotCpuReport.CsvRows(window, values, end.Time);
        if (rows.Count > 0)
            AppendCsv(CsvFile, rows);

        run.Last = window;
        run.LastProcess = values;
        run.Since = run.Since.Add(window);
    }

    // The bots of the run's per-bot table (3.10), with their counters' ticks and calls since the report last read
    // them. A bot that left the world is gone from the table once its counter is collected, or simply not listed:
    // either way nothing of it stays behind. These are reads of another thread's objects without a lock: a name or an
    // activity a moment old is harmless. Each is read once: the region is set to null on the loop when an instance
    // collapses (world/Region.cs:270), so CurrentRegionID, which reads it twice, could find it gone at its second read.
    private static List<BotCpuBot> ListBots()
    {
        List<BotCpuBot> bots = [];
        foreach ((GameObject owner, BotCpuCounter counter) in BotCpuMeter.Counters())
        {
            (long ticks, long calls) = BotCpuReport.TakeCounterWindow(counter);
            OfflineWorldBotRecord record = (owner as GameBot)?.PersistentRecord;
            string className = owner switch
            {
                GameBot bot => bot.ClassName,
                GamePlayer player => player.CharacterClass?.Name,
                _ => null,
            };
            bots.Add(new BotCpuBot(counter.Kind, owner.Name, owner.Level, className, owner.CurrentRegion?.ID ?? 0,
                record?.ObjectiveKind, record?.Activity, ticks, calls));
        }

        return bots;
    }

    // A run closes (3.7): Enabled false, then its final window, to the instant metering stopped (an off command's, else
    // now), then the BOT_CPU_STATE off line, then its timer disposed, all under the lock. A tick of this run waiting
    // for the lock then finds it closed.
    private static void Close(MeterRun run, BotCpuOffSource source, string by, Instant? end = null)
    {
        BotCpuMeter.Disable();
        WriteWindow(run, end ?? Instant.Now());
        WriteLog(ELogLevel.Info, BotCpuReport.StateOffLine(source, by));
        run.Timer?.Dispose();
        _open = null;
        Volatile.Write(ref _result, BotCpuResult.Off);
    }

    // The open run's result, swapped in whole (3.7).
    private static void Publish(MeterRun run) => Volatile.Write(ref _result, new BotCpuResult(true, run.Number,
        run.ByCommand, run.OnSince, run.OnSinceTimestamp, run.StopsAt?.Time, run.WindowStart.Time,
        run.WindowStart.Timestamp, run.Last,
        run.LastProcess, run.Since));
}

/// <summary>
/// What the report published last (3.7), for <c>/botcpu</c>: immutable, and swapped in whole at each change (on, a
/// window, reset, a new stop time, off), so the command reads one consistent state and never builds a report.
/// </summary>
/// <param name="On">Whether a run is open; when false, nothing else is set.</param>
/// <param name="Run">The open run's number (<see cref="BotCpuMeter.RunNumber"/>).</param>
/// <param name="ByCommand">Whether a GM's <c>/botcpu on</c> opened it, else the property (2.4).</param>
/// <param name="OnSince">When it opened, the server's local time.</param>
/// <param name="OnSinceTimestamp">The same instant as a <see cref="Stopwatch"/> timestamp: <c>/botcpu</c>'s minutes
/// on count from it, through <see cref="BotCpuScript.ElapsedSince"/>, so summer time does not change them (2.4).
/// </param>
/// <param name="StopsAt">Its stop time, local, as the chat shows it; null for none (4.3).</param>
/// <param name="WindowStart">When the open window started, local: its lines come a window's length later (2.4).
/// </param>
/// <param name="WindowStartTimestamp">The same instant as a <see cref="Stopwatch"/> timestamp, the clock a window
/// is measured with (2.2): <see cref="BotCpuScript.CommandOff"/> gives the seconds since then for
/// <c>/botcpu off</c>'s reply (2.4).</param>
/// <param name="Last">The last window written, with the top 5 of each bot kind; null before the first.</param>
/// <param name="LastProcess">That window's process values.</param>
/// <param name="Since">The totals since on or reset (2.4).</param>
public sealed record BotCpuResult(bool On, int Run, bool ByCommand, DateTime OnSince, long OnSinceTimestamp,
    DateTime? StopsAt, DateTime WindowStart, long WindowStartTimestamp, BotCpuWindow Last, BotCpuProcess LastProcess,
    BotCpuSince Since)
{
    /// <summary>The meter off: no run open.</summary>
    public static BotCpuResult Off { get; } = new(false, 0, false, default, 0, null, default, 0, null, null, null);
}

/// <summary>What a thread is to the process line (3.9), from its <c>comm</c> name.</summary>
public enum BotCpuThreadRole
{
    /// <summary>Any other thread: not read further.</summary>
    Other,
    /// <summary>The <c>GameLoop</c> thread or a <c>GameLoop_Worker_&lt;n&gt;</c> (MT:100): <c>loopThreadsCpuPct</c>
    /// and <c>loopWaitMs</c>.</summary>
    Loop,
    /// <summary>The <c>CampSnapshotPlanner</c> thread (CPP:52): <c>campThreadCpuPct</c>.</summary>
    Camp,
}

/// <summary>A thread's <c>schedstat</c> (3.9): ns on a CPU and ns waiting on a run queue.</summary>
public readonly record struct BotCpuSchedstat(long CpuNs, long WaitNs);

/// <summary>A loop or camp thread in a process sample (3.9): its role and its schedstat, null when that reads 0 or
/// cannot be parsed (<c>-</c>).</summary>
public readonly record struct BotCpuThreadSample(BotCpuThreadRole Role, BotCpuSchedstat? Schedstat);

/// <summary>
/// The process values at one instant (3.9), taken at a window's start and end by
/// <see cref="BotCpuScript.TakeProcessSample"/>; <see cref="BotCpuScript.ProcessWindow"/> turns two into the window's
/// <see cref="BotCpuProcess"/>. Null is a value the platform can't give.
/// </summary>
/// <param name="Cores"><see cref="Environment.ProcessorCount"/>.</param>
/// <param name="ProcessCpu"><see cref="Process.TotalProcessorTime"/>.</param>
/// <param name="Threads">The loop and camp threads by tid; null when <c>/proc/self/task</c> can't be listed.</param>
/// <param name="ThrottledUsec"><c>throttled_usec</c> of the container's <c>cpu.stat</c>.</param>
/// <param name="GcPause"><see cref="GC.GetTotalPauseDuration"/>.</param>
/// <param name="Gen2"><c>GC.CollectionCount(2)</c>.</param>
/// <param name="AllocatedBytes"><c>GC.GetTotalAllocatedBytes(false)</c>: all allocation in the process.</param>
/// <param name="HeapBytes"><c>GC.GetTotalMemory(false)</c>, as <c>SERVER_STALL</c> reads it; no forced collection.
/// </param>
/// <param name="RssKB"><c>VmRSS</c> of <c>/proc/self/status</c>, kB.</param>
public sealed record BotCpuProcessSample(int Cores, TimeSpan? ProcessCpu,
    IReadOnlyDictionary<int, BotCpuThreadSample> Threads, long? ThrottledUsec, TimeSpan GcPause, int Gen2,
    long AllocatedBytes, long HeapBytes, long? RssKB);
