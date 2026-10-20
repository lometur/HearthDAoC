using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's server side (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). The
// switch (section 4): the bot_cpu_meter server property, off in code, is set from HEARTHDAOC_BOT_CPU at every start
// (4.2) and read once, at start: on, the meter starts with the server (4.1). A GM's /botcpu turns the meter on or off
// until the next start and never writes the property (4.3). The process readers (3.9): the /proc and cgroup parsers,
// pure functions of a file's text, their reads, the process sample and a window's process values. The CSV (2.3):
// logs/bot-cpu.csv, its header check and its rotation to bot-cpu.csv.1 above 50 MB. The guarded log writer (3.7): a
// failed CSV write logs one warning and never escapes. BotCpuMeter is the hot path; BotCpuReport makes the numbers and
// the text.
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
    /// the first camp planning (CPP:52).
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

            // No camp thread gives 0, but only once the loop threads show the readers work: a server always has loop
            // threads, so none found means the threads could not be told or read (no schedstat files, unreadable comm
            // files), the camp thread's included.
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
