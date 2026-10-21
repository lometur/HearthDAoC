using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DOL.Database;
using DOL.Events;
using DOL.GS.HearthDAoC;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.ServerProperties;
using DOL.GS.ServerRules;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter's switch (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 4.1, 6.1).
// BotCpuScript holds the bot_cpu_meter server property, which HEARTHDAOC_BOT_CPU sets at every start (4.2) and the
// server reads once, at start, to turn the meter on with it. Off in code, as the fork's other switches are. It also
// reads the process values of the BOT_CPU_PROCESS line (3.9), tested here as parsers of sample text and on sample
// files, and appends the CSV (2.3), tested in a folder of each test's own. Then the report side (3.7, 3.8, 4.1, 4.3):
// a command only turns the meter off or queues a job; the jobs, the 1-second timer's ticks and the server's started and
// stopped handlers run one at a time under the report lock, write each window once and never let an exception out.
// The tests hold the queued work items, tick a timer of their own and move a clock of their own (test hooks). Last, the
// GM's /botcpu (2.4): its attribute, its replies from the result the report published, the arguments it refuses in any
// state, and its changes, which only turn the meter off or queue a job.
[TestFixture, NonParallelizable]
public sealed class UT_BotCpuScript
{
    [Test]
    public void TheSwitchIsAPublicStaticServerPropertyOffInCode()
    {
        // The server finds a property only on a public static field (ServerProperties.AllDomainProperties reads
        // GetFields(), then IsStatic). HearthDAoC turns it on through HEARTHDAOC_BOT_CPU; the description must equal
        // deploy/bin/server_properties.py's CPU_DESCRIPTION (deploy/tests/test_bot_cpu.py).
        FieldInfo field = Array.Find(typeof(BotCpuScript).GetFields(), f => f.Name == "BOT_CPU_METER");
        Assert.That(field, Is.Not.Null);
        Assert.That(field.IsStatic, Is.True);
        Assert.That(field.FieldType, Is.EqualTo(typeof(bool)));
        ServerPropertyAttribute property = field.GetCustomAttribute<ServerPropertyAttribute>();
        Assert.That(property, Is.Not.Null);
        Assert.That(property.Category, Is.EqualTo("server"));
        Assert.That(property.Key, Is.EqualTo("bot_cpu_meter"));
        Assert.That(property.DefaultValue, Is.TypeOf<bool>().And.EqualTo(false));
        Assert.That(property.Description, Is.EqualTo(
            "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log " +
            "every minute; a GM's /botcpu turns it on or off until the next start"));
    }

    // ---- The test hooks (6.1): where the process sample reads, and where the log lines go

    private static readonly FieldInfo ProcSelfHook =
        typeof(BotCpuScript).GetField("_procSelf", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo CgroupCpuStatHook =
        typeof(BotCpuScript).GetField("_cgroupCpuStat", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo LogHook =
        typeof(BotCpuScript).GetField("_logHook", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo CsvWriteHook =
        typeof(BotCpuScript).GetField("_csvWriteHook", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly List<(ELogLevel Level, string Line)> _logs = [];
    private string _folder;
    private object _procSelf;
    private object _cgroupCpuStat;
    private GameServer _savedServer;
    private long _savedGameLoopTime;
    private double _savedTickDuration;
    private int _savedPopulation;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "bot-cpu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _procSelf = ProcSelfHook.GetValue(null);
        _cgroupCpuStat = CgroupCpuStatHook.GetValue(null);
        _logs.Clear();
        // Locked: the burst test's jobs run on the thread pool.
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            lock (_logs)
                _logs.Add((level, line));
        }));

        // The report side's hooks: the queued work items are held, the 1-second timer is the test's own, the clock
        // moves only when the test moves it, and the CSV is in the test's folder.
        _queued.Clear();
        _timers.Clear();
        _now = new DateTime(2026, 10, 18, 14, 2, 0, DateTimeKind.Local);
        _timestamp = 1_000 * Stopwatch.Frequency;
        QueueHook.SetValue(null, (Action<WaitCallback>)(work => _queued.Add(work)));
        TimerHook.SetValue(null, (Func<TimerCallback, object, IDisposable>)((callback, state) =>
        {
            TestTimer timer = new(callback, state);
            lock (_timers)
                _timers.Add(timer);
            return timer;
        }));
        NowHook.SetValue(null, (Func<DateTime>)(() => _now));
        TimestampHook.SetValue(null, (Func<long>)(() => _timestamp));
        CsvFileHook.SetValue(null, Csv);

        // A GameBot is made without its constructor, but GameObject's type initializer reads the database: a test
        // double server with an empty one, as UT_BotCpuMeter does. The game time grows by the test's clock at 30
        // ticks a second, and the registry's sampled population is the test's (3.8).
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        _savedGameLoopTime = GameLoop.GameLoopTime;
        _savedTickDuration = GameLoop.TickDuration;
        _savedPopulation = (int)PopulationField.GetValue(null);
        GameLoopTimeProperty.SetValue(null, 5_000_000L);
        TickDurationProperty.SetValue(null, 1000.0 / 30);
        Population(0);
        BotCpuScript.BOT_CPU_METER = false;
    }

    [TearDown]
    public void TearDown()
    {
        // No tick of a real timer runs into the next test: the open run's, which a failing test leaves ticking, and
        // the real-timer test's own are disposed and their callbacks waited for. A callback queued after Dispose finds
        // its timer disposed and never runs; one already running holds the report lock, taken here once.
        if (OpenRunTimer() is System.Threading.Timer open)
            _realTimers.Add(open);
        foreach (System.Threading.Timer timer in _realTimers)
            timer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        _realTimers.Clear();
        lock (ReportLockField.GetValue(null)!)
        {
            // Nothing: the lock is only waited for.
        }

        // No run, job or result is left for the next test.
        OpenRunField.SetValue(null, null);
        ResultField.SetValue(null, BotCpuResult.Off);
        object jobs = JobsField.GetValue(null);
        jobs.GetType().GetMethod("Clear")!.Invoke(jobs, null);
        QueueHook.SetValue(null, null);
        TimerHook.SetValue(null, null);
        NowHook.SetValue(null, null);
        TimestampHook.SetValue(null, null);
        ZoneHook.SetValue(null, null);
        CsvFileHook.SetValue(null, null);
        BotCpuScript.BOT_CPU_METER = false;
        GameLoopTimeProperty.SetValue(null, _savedGameLoopTime);
        TickDurationProperty.SetValue(null, _savedTickDuration);
        Population(_savedPopulation);
        GameServer.LoadTestDouble(_savedServer);

        ProcSelfHook.SetValue(null, _procSelf);
        CgroupCpuStatHook.SetValue(null, _cgroupCpuStat);
        LogHook.SetValue(null, null);
        CsvWriteHook.SetValue(null, null);
        BotCpuMeter.Disable();
        // A failing test may leave a folder read-only.
        foreach (string folder in Directory.GetDirectories(_folder, "*", SearchOption.AllDirectories))
            SetMode(folder, ReadWrite);
        Directory.Delete(_folder, true);
    }

    private const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode ReadWrite = ReadOnly | UnixFileMode.UserWrite;

    private static void SetMode(string folder, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, mode);
    }

    // ---- The readers, as parsers of a file's text (3.9)

    [Test]
    public void SchedstatGivesTheTimeOnACpuAndTheWaitOnARunQueueInNs() =>
        // /proc/self/task/<tid>/schedstat: ns on a CPU, ns waiting on a run queue, timeslices.
        Assert.That(BotCpuScript.ParseSchedstat("123 456 7\n"), Is.EqualTo(new BotCpuSchedstat(123, 456)));

    [Test]
    public void ASchedstatThatReadsZeroIsADash()
    {
        // Where the kernel keeps no scheduler statistics the file still exists and reads 0 0 0: a first field of 0
        // for a running thread counts as unavailable, not as 0 (3.9).
        Assert.That(BotCpuScript.ParseSchedstat("0 0 0\n"), Is.Null);
        Assert.That(BotCpuScript.ParseSchedstat("0 456 7\n"), Is.Null);
    }

    // Linux cuts a thread's name to 15 characters in comm: GameLoop_Worker_<n> is GameLoop_Worker, and the watchdog,
    // the busy-wait threshold thread and the camp planner (CampSnapshotPlanner, CPP:52) are cut too.
    [TestCase("GameLoop\n", BotCpuThreadRole.Loop)]
    [TestCase("GameLoop_Worker\n", BotCpuThreadRole.Loop)]
    [TestCase("GameLoop_Watchd\n", BotCpuThreadRole.Other)]
    [TestCase("GameLoop_BusyWa\n", BotCpuThreadRole.Other)]
    [TestCase("CampSnapshotPla\n", BotCpuThreadRole.Camp)]
    [TestCase(".NET TP Worker\n", BotCpuThreadRole.Other)]
    public void CommNamesTheLoopThreadsAndTheCampThread(string comm, BotCpuThreadRole role) =>
        Assert.That(BotCpuScript.ThreadRoleOf(comm), Is.EqualTo(role));

    [Test]
    public void CpuStatGivesThrottledUsec() =>
        // /sys/fs/cgroup/cpu.stat, cgroup v2, as the container has it.
        Assert.That(BotCpuScript.ParseThrottledUsec(
            "usage_usec 553362452000\nuser_usec 467290902000\nsystem_usec 86071550000\nnr_periods 1200\n" +
            "nr_throttled 3\nthrottled_usec 250000\nnr_bursts 0\nburst_usec 0\n"), Is.EqualTo(250_000));

    [Test]
    public void StatusGivesVmRssInKB() =>
        Assert.That(BotCpuScript.ParseVmRssKB(
            "Name:\tGameServer\nState:\tS (sleeping)\nVmHWM:\t 3600000 kB\nVmRSS:\t 3563520 kB\n" +
            "RssAnon:\t 3400000 kB\n"),
            Is.EqualTo(3_563_520));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a number\n")]
    public void AnEmptyMissingOrUnreadableFileIsADash(string text)
    {
        // A missing file is read as null (BotCpuScript's reads), so every parser gives - for null too.
        Assert.That(BotCpuScript.ParseSchedstat(text), Is.Null);
        Assert.That(BotCpuScript.ThreadRoleOf(text), Is.EqualTo(BotCpuThreadRole.Other));
        Assert.That(BotCpuScript.ParseThrottledUsec(text), Is.Null);
        Assert.That(BotCpuScript.ParseVmRssKB(text), Is.Null);
    }

    [Test]
    public void AFileWithoutItsFieldIsADash()
    {
        // cgroup v1, or no cpu controller: no throttled_usec; a status with no VmRSS.
        Assert.That(BotCpuScript.ParseThrottledUsec("usage_usec 553362452000\nnr_throttled 3\n"), Is.Null);
        Assert.That(BotCpuScript.ParseVmRssKB("Name:\tGameServer\nVmHWM:\t 3600000 kB\n"), Is.Null);
    }

    // ---- The reads and the process sample (3.9)

    // A thread's folder under proc/task, with its comm and its schedstat (none: the thread left between the reads).
    private static void SampleThread(string proc, string tid, string comm, string schedstat)
    {
        string folder = Path.Combine(proc, "task", tid);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "comm"), comm);
        if (schedstat != null)
            File.WriteAllText(Path.Combine(folder, "schedstat"), schedstat);
    }

    [Test]
    public void TheSampleReadsTheLoopAndCampThreadsTheMemoryAndTheThrottling()
    {
        string proc = Path.Combine(_folder, "proc");
        SampleThread(proc, "101", "GameLoop\n", "31000000000 150000000 900\n");
        SampleThread(proc, "102", "GameLoop_Worker\n", "43100000000 112000000 800\n");
        SampleThread(proc, "103", "GameLoop_Watchd\n", "5000 5000 5\n");
        SampleThread(proc, "104", "CampSnapshotPla\n", "2000000000 0 30\n");
        SampleThread(proc, "105", ".NET TP Worker\n", "7000 7000 7\n");
        SampleThread(proc, "106", "GameLoop_Worker\n", null);
        SampleThread(proc, "107", "GameLoop_Worker\n", "0 0 0\n");
        Directory.CreateDirectory(Path.Combine(proc, "task", "not-a-thread"));
        File.WriteAllText(Path.Combine(proc, "status"), "Name:\tGameServer\nVmRSS:\t 3563520 kB\n");
        string cpuStat = Path.Combine(_folder, "cpu.stat");
        File.WriteAllText(cpuStat, "usage_usec 553362452000\nnr_throttled 3\nthrottled_usec 250000\n");
        ProcSelfHook.SetValue(null, proc);
        CgroupCpuStatHook.SetValue(null, cpuStat);

        BotCpuProcessSample sample = BotCpuScript.TakeProcessSample();

        // Only the loop and camp threads are kept; 106 left between its two reads and is dropped; 107 reads 0 0 0.
        Assert.That(sample.Threads.Keys, Is.EquivalentTo(new[] { 101, 102, 104, 107 }));
        Assert.That(sample.Threads[101], Is.EqualTo(LoopThread(31_000_000_000, 150_000_000)));
        Assert.That(sample.Threads[102], Is.EqualTo(LoopThread(43_100_000_000, 112_000_000)));
        Assert.That(sample.Threads[104], Is.EqualTo(CampThread(2_000_000_000)));
        Assert.That(sample.Threads[107], Is.EqualTo(new BotCpuThreadSample(BotCpuThreadRole.Loop, null)));
        Assert.That(sample.RssKB, Is.EqualTo(3_563_520));
        Assert.That(sample.ThrottledUsec, Is.EqualTo(250_000));
        Assert.That(sample.Cores, Is.EqualTo(Environment.ProcessorCount));
        Assert.That(sample.ProcessCpu, Is.Not.Null);
    }

    [Test]
    public void CommFilesWithNoSchedstatFilesGiveDashes()
    {
        // A kernel that keeps no schedstat files (3.9: est. on the owner's kernel): the threads are named but cannot
        // be read, so the four schedstat fields are -, the camp thread's too, not 0 (2.1: a file missing in the
        // container is -).
        string proc = Path.Combine(_folder, "proc");
        SampleThread(proc, "101", "GameLoop\n", null);
        SampleThread(proc, "102", "GameLoop_Worker\n", null);
        SampleThread(proc, "104", "CampSnapshotPla\n", null);
        ProcSelfHook.SetValue(null, proc);

        BotCpuProcess process = BotCpuScript.ProcessWindow(BotCpuScript.TakeProcessSample(),
            BotCpuScript.TakeProcessSample(), 60);

        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.ProcessCpuPct, Is.Not.Null);
    }

    [Test]
    public void NoProcOrCgroupFilesGiveDashesAndNoFault()
    {
        // Windows, or a container without them: - for those fields, and the other values still given (3.9).
        ProcSelfHook.SetValue(null, Path.Combine(_folder, "no-proc"));
        CgroupCpuStatHook.SetValue(null, Path.Combine(_folder, "no-cgroup", "cpu.stat"));

        BotCpuProcessSample earlier = BotCpuScript.TakeProcessSample();
        BotCpuProcessSample later = BotCpuScript.TakeProcessSample();
        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(later.Threads, Is.Null);
        Assert.That(later.RssKB, Is.Null);
        Assert.That(later.ThrottledUsec, Is.Null);
        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.ThrottledMs, Is.Null);
        Assert.That(process.RssMB, Is.Null);
        Assert.That(process.Cores, Is.EqualTo(Environment.ProcessorCount));
        Assert.That(process.ProcessCpuPct, Is.Not.Null);
        Assert.That(later.AllocatedBytes, Is.GreaterThan(0));
        Assert.That(later.HeapBytes, Is.GreaterThan(0));
    }

    private static TimeSpan CurrentProcessCpu()
    {
        using Process process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    [Test]
    public void TheSampleReadsTheProcessCpuAndTheGcFromTheRuntime()
    {
        // 3.9's sources, on the test's own process: Process.TotalProcessorTime, GC.GetTotalPauseDuration(),
        // GC.CollectionCount(2), all allocation in the process and GC.GetTotalMemory(false). Between two samples the
        // process spins 100 ms of CPU, and the test allocates 8 MB and keeps it through a full, blocking collection.
        BotCpuProcessSample earlier = BotCpuScript.TakeProcessSample();
        TimeSpan spinStart = CurrentProcessCpu();
        Stopwatch wall = Stopwatch.StartNew();
        while (CurrentProcessCpu() - spinStart < TimeSpan.FromMilliseconds(100) && wall.Elapsed.TotalSeconds < 10)
        {
            // Spin.
        }

        byte[] kept = new byte[8 * MB];
        GC.Collect(2, GCCollectionMode.Forced, true);
        BotCpuProcessSample later = BotCpuScript.TakeProcessSample();

        Assert.That(earlier.ProcessCpu, Is.Not.Null);
        Assert.That(later.ProcessCpu, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(later.ProcessCpu.Value - earlier.ProcessCpu.Value,
                Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100)));
            Assert.That(later.Gen2 - earlier.Gen2, Is.GreaterThanOrEqualTo(1));
            Assert.That(later.GcPause - earlier.GcPause, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(later.AllocatedBytes - earlier.AllocatedBytes, Is.GreaterThanOrEqualTo(8 * MB));
            Assert.That(later.HeapBytes, Is.GreaterThanOrEqualTo(8 * MB));
        });
        GC.KeepAlive(kept);
    }

    [Test]
    public void TheSampleCountsOnlyFullCollectionsAndReadsTheHeapWithNoCollectionOfItsOwn()
    {
        // The reads behind gen2 and heapMB (2.1, 3.9), and no forced collection (2.1, 4.5). gen2 is
        // GC.CollectionCount(2): a full collection raises the gen0 and gen1 counts too, and the test's own gen1 and
        // gen0 collections put those at least 3 above it, so a sample that read either is out of range. Three samples
        // raise the full count by less than 3: none forces a collection. heapMB is GC.GetTotalMemory(false), which
        // counts what was allocated since the last collection, so the kept 16 MB shows at once; the heap that
        // collection left (GCMemoryInfo.HeapSizeBytes) would not show it. The full collection first leaves little
        // garbage, so one the runtime starts meanwhile takes back at most a few MB the first sample counted.
        GC.Collect(2, GCCollectionMode.Forced, true);
        for (int i = 0; i < 5; i++)
        {
            GC.Collect(1, GCCollectionMode.Forced, true);
            GC.Collect(0, GCCollectionMode.Forced, true);
        }

        int fullBefore = GC.CollectionCount(2);
        BotCpuProcessSample first = BotCpuScript.TakeProcessSample();
        BotCpuScript.TakeProcessSample();
        BotCpuScript.TakeProcessSample();
        int fullAfter = GC.CollectionCount(2);
        byte[] kept = new byte[16 * MB];
        BotCpuProcessSample fourth = BotCpuScript.TakeProcessSample();

        Assert.Multiple(() =>
        {
            Assert.That(first.Gen2, Is.InRange(fullBefore, fullAfter));
            Assert.That(fullAfter - fullBefore, Is.LessThan(3));
            Assert.That(fourth.HeapBytes - first.HeapBytes, Is.GreaterThanOrEqualTo(12 * MB));
        });
        GC.KeepAlive(kept);
    }

    [Test]
    public void OnLinuxTheSampleFindsALoopThreadByItsName()
    {
        // The real /proc of the test's own process: a thread named as the pool names its workers (MT:100).
        Assume.That(OperatingSystem.IsLinux(), "/proc is Linux's");
        int before = BotCpuScript.TakeProcessSample().Threads.Values.Count(t => t.Role == BotCpuThreadRole.Loop);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim stop = new();
        Thread worker = new(() =>
        {
            started.Set();
            stop.Wait();
        }) { Name = $"{GameLoop.THREAD_NAME}_Worker_7", IsBackground = true };
        worker.Start();
        started.Wait();
        try
        {
            BotCpuProcessSample sample = BotCpuScript.TakeProcessSample();
            Assert.That(sample.Threads.Values.Count(t => t.Role == BotCpuThreadRole.Loop), Is.EqualTo(before + 1));
            Assert.That(sample.RssKB, Is.GreaterThan(0));
        }
        finally
        {
            stop.Set();
            worker.Join();
        }
    }

    // ---- A window's process values, from two samples (2.1 BOT_CPU_PROCESS, 3.9)

    private const long MB = 1_048_576;

    private static BotCpuThreadSample LoopThread(long cpuNs, long waitNs) =>
        new(BotCpuThreadRole.Loop, new BotCpuSchedstat(cpuNs, waitNs));

    private static BotCpuThreadSample CampThread(long cpuNs, long waitNs = 0) =>
        new(BotCpuThreadRole.Camp, new BotCpuSchedstat(cpuNs, waitNs));

    private static TimeSpan Ms(double ms) => TimeSpan.FromTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));

    // A sample with the process CPU and the threads given, and nothing else moving.
    private static BotCpuProcessSample Sample(double processCpuMs, Dictionary<int, BotCpuThreadSample> threads) =>
        new(4, Ms(processCpuMs), threads, 0, TimeSpan.Zero, 0, 0, 0, 0);

    [Test]
    public void TheProcessValuesAreTheWindowsDifferences()
    {
        // 2.1's sample, from two samples 60 s apart: the loop threads ran 72.6 s and waited 212 ms, the camp thread
        // 1.86 s, the process 88.92 s.
        BotCpuProcessSample earlier = new(4, Ms(10_000),
            new Dictionary<int, BotCpuThreadSample>
            {
                [101] = LoopThread(1_000_000_000, 50_000_000),
                [102] = LoopThread(500_000_000, 0),
                [104] = CampThread(140_000_000),
            },
            ThrottledUsec: 5_000_000, GcPause: Ms(100), Gen2: 3, AllocatedBytes: 1_000_000_000, HeapBytes: 100 * MB,
            RssKB: 1024);
        BotCpuProcessSample later = new(4, Ms(10_000 + 88_920),
            new Dictionary<int, BotCpuThreadSample>
            {
                [101] = LoopThread(31_000_000_000, 150_000_000),
                [102] = LoopThread(43_100_000_000, 112_000_000),
                [104] = CampThread(2_000_000_000),
            },
            ThrottledUsec: 5_000_000, GcPause: Ms(114), Gen2: 3, AllocatedBytes: 1_000_000_000 + 2472 * MB,
            HeapBytes: 2210 * MB, RssKB: 3480 * 1024);

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.Cores, Is.EqualTo(4));
        Assert.That(process.ProcessCpuPct, Is.EqualTo(148.2).Within(1e-9));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(121.0).Within(1e-9));
        Assert.That(process.OffLoopCpuPct, Is.EqualTo(27.2).Within(1e-9));
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(3.1).Within(1e-9));
        Assert.That(process.LoopWaitMs, Is.EqualTo(212).Within(1e-9));
        Assert.That(process.ThrottledMs, Is.EqualTo(0));
        Assert.That(process.GcPauseMs, Is.EqualTo(14).Within(1e-9));
        Assert.That(process.Gen2, Is.EqualTo(0));
        Assert.That(process.AllocMBSec, Is.EqualTo(41.2).Within(1e-9));
        Assert.That(process.HeapMB, Is.EqualTo(2210));
        Assert.That(process.RssMB, Is.EqualTo(3480));

        // As the line writes them; tps, chargedPct and callsSec come from the window.
        BotCpuWindow window = BotCpuReport.Window(new BotCpuTotals(), 60, 1_000_000_000, 60_000, 1000.0 / 30,
            default, []);
        Assert.That(BotCpuReport.ProcessLine(window, process), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 " +
            "campThreadCpuPct=3.1 chargedPct=0.0 callsSec=0 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480"));
    }

    [Test]
    public void TheCampThreadsWaitOnARunQueueIsNotInLoopWaitMs()
    {
        // 3.9: loopWaitMs is the schedstat second field of the loop threads only. 2.1's sample has a camp thread
        // that never waits, so the same window with the camp thread waiting 500 ms: loopWaitMs is still the loop
        // threads' 212 ms, and the camp thread's CPU is still its 1.86 s.
        BotCpuProcessSample earlier = Sample(0, new() { [101] = LoopThread(1_000_000_000, 50_000_000),
            [102] = LoopThread(500_000_000, 0), [104] = CampThread(140_000_000, 0) });
        BotCpuProcessSample later = Sample(0, new() { [101] = LoopThread(31_000_000_000, 150_000_000),
            [102] = LoopThread(43_100_000_000, 112_000_000), [104] = CampThread(2_000_000_000, 500_000_000) });

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopWaitMs, Is.EqualTo(212).Within(1e-9));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(121.0).Within(1e-9));
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(3.1).Within(1e-9));
    }

    [Test]
    public void TheThrottlingIsInMsAndTheFullCollectionsAreCounted()
    {
        // 2.1's sample has neither, so a window with both: cpu.stat's throttled_usec rose 250,000 µs, which is 250 ms
        // (2.1: throttledMs is ms), and two full collections ran.
        BotCpuProcessSample earlier = new(4, Ms(0), null, ThrottledUsec: 5_000_000, GcPause: TimeSpan.Zero, Gen2: 3,
            AllocatedBytes: 0, HeapBytes: 0, RssKB: 0);
        BotCpuProcessSample later = new(4, Ms(0), null, ThrottledUsec: 5_250_000, GcPause: TimeSpan.Zero, Gen2: 5,
            AllocatedBytes: 0, HeapBytes: 0, RssKB: 0);

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.ThrottledMs, Is.EqualTo(250).Within(1e-9));
        Assert.That(process.Gen2, Is.EqualTo(2));
    }

    [Test]
    public void AThreadThatLeftIsDroppedAndANewOneCountsFromZero()
    {
        // Threads are re-listed every minute (3.9): 102 left in the window, and 103, a worker the watchdog restarted
        // (or a loop thread that started after the meter, as at the property's start), ran only in it.
        BotCpuProcessSample earlier = Sample(0, new() { [101] = LoopThread(10_000_000_000, 1_000_000),
            [102] = LoopThread(20_000_000_000, 2_000_000) });
        BotCpuProcessSample later = Sample(0, new() { [101] = LoopThread(16_000_000_000, 3_000_000),
            [103] = LoopThread(6_000_000_000, 4_000_000) });

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(20.0).Within(1e-9)); // 6 s + 6 s over 60 s
        Assert.That(process.LoopWaitMs, Is.EqualTo(6).Within(1e-9));

        // A tid that was the camp thread's and is a new loop thread's: the camp thread left, and the loop thread
        // counts from zero, its whole 3 s, not its difference from the camp thread's 9 s.
        earlier = Sample(0, new() { [101] = LoopThread(10_000_000_000, 1_000_000),
            [104] = CampThread(9_000_000_000) });
        later = Sample(0, new() { [101] = LoopThread(16_000_000_000, 3_000_000),
            [104] = LoopThread(3_000_000_000, 2_000_000) });

        process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(15.0).Within(1e-9)); // 6 s + 3 s over 60 s
        Assert.That(process.LoopWaitMs, Is.EqualTo(4).Within(1e-9));
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(0));
    }

    [Test]
    public void ANegativeDifferenceCountsAsZero()
    {
        // A tid the kernel gave to a new loop thread, and counters read lower than before.
        BotCpuProcessSample earlier = new(4, Ms(9_000),
            new Dictionary<int, BotCpuThreadSample> { [101] = LoopThread(50_000_000_000, 9_000_000) },
            ThrottledUsec: 9_000, GcPause: Ms(50), Gen2: 2, AllocatedBytes: 5_000, HeapBytes: 0, RssKB: 0);
        BotCpuProcessSample later = new(4, Ms(5_000),
            new Dictionary<int, BotCpuThreadSample> { [101] = LoopThread(6_000_000_000, 4_000_000) },
            ThrottledUsec: 1_000, GcPause: Ms(40), Gen2: 1, AllocatedBytes: 1_000, HeapBytes: 0, RssKB: 0);

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.ProcessCpuPct, Is.EqualTo(0));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(0));
        Assert.That(process.LoopWaitMs, Is.EqualTo(0));
        Assert.That(process.ThrottledMs, Is.EqualTo(0));
        Assert.That(process.GcPauseMs, Is.EqualTo(0));
        Assert.That(process.Gen2, Is.EqualTo(0));

        // But not the allocation: a counter that went back is stuck, not a quiet window (3.9).
        Assert.That(process.AllocMBSec, Is.Null);
    }

    [Test]
    public void AnAllocationCounterThatDidNotMoveIsADashNotZero()
    {
        // 3.9: under Server GC's dynamic heap count (DATAS), .NET 10's GC.GetTotalAllocatedBytes(false) stands still
        // for minutes or hours after the GC takes heaps out of service, while the server allocates MBs a second
        // (dotnet/runtime#122299). A running server always allocates, so a counter that did not move is -, not 0.0;
        // the window's other values are still given.
        BotCpuProcessSample earlier = new(4, Ms(1_000), null, ThrottledUsec: null, GcPause: Ms(100), Gen2: 3,
            AllocatedBytes: 7_000_000_000, HeapBytes: 100 * MB, RssKB: null);
        BotCpuProcessSample stuck = earlier with { ProcessCpu = Ms(61_000), GcPause = Ms(211), HeapBytes = 120 * MB };

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, stuck, 60);

        Assert.That(process.AllocMBSec, Is.Null);
        Assert.That(process.ProcessCpuPct, Is.EqualTo(100.0).Within(1e-9));
        Assert.That(process.GcPauseMs, Is.EqualTo(111).Within(1e-9));
        Assert.That(process.HeapMB, Is.EqualTo(120));
        BotCpuWindow window = BotCpuReport.Window(new BotCpuTotals(), 60, 1_000_000_000, 60_000, 1000.0 / 30,
            default, []);
        Assert.That(BotCpuReport.ProcessLine(window, process), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=100.0 loopThreadsCpuPct=- offLoopCpuPct=- " +
            "campThreadCpuPct=- chargedPct=0.0 callsSec=0 loopWaitMs=- throttledMs=- gcPauseMs=111 gen2=0 " +
            "allocMBSec=- heapMB=120 rssMB=-"));

        // Any move is a rate: the smallest the runtime's counter moves by is a whole allocation buffer, so there is no
        // threshold. 6 MB in 60 s is 0.1 MB/s.
        Assert.That(BotCpuScript.ProcessWindow(earlier, stuck with { AllocatedBytes = 7_000_000_000 + 6 * MB }, 60)
            .AllocMBSec, Is.EqualTo(0.1).Within(1e-9));
    }

    [Test]
    public void ALoopThreadWhoseSchedstatReadsZeroMakesTheFourSchedstatFieldsDashes()
    {
        // 3.9: loopThreadsCpuPct, offLoopCpuPct, campThreadCpuPct and loopWaitMs are then -, not 0.
        BotCpuProcessSample earlier = Sample(0, new() { [101] = LoopThread(10_000_000_000, 1_000_000),
            [104] = CampThread(1_000_000_000) });
        BotCpuProcessSample later = Sample(60_000, new() { [101] = new(BotCpuThreadRole.Loop, null),
            [104] = CampThread(2_000_000_000) });

        BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);

        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.ProcessCpuPct, Is.EqualTo(100.0).Within(1e-9));

        // A camp thread that reads 0 makes only its own field -.
        later = Sample(60_000, new() { [101] = LoopThread(16_000_000_000, 1_000_000),
            [104] = new(BotCpuThreadRole.Camp, null) });
        process = BotCpuScript.ProcessWindow(earlier, later, 60);
        Assert.That(process.CampThreadCpuPct, Is.Null);
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(10.0).Within(1e-9));
    }

    [Test]
    public void NoLoopThreadMakesTheFourSchedstatFieldsDashesAndNoCampThreadIsZero()
    {
        // The camp planner thread starts with the first camp planning (CPP:52), so a server may have none: it then
        // costs nothing. A server always has its loop threads, so none found means they could not be told or read
        // (no schedstat files, unreadable comm files), and then the camp thread could not be either.
        BotCpuProcessSample loopOnly = Sample(0, new() { [101] = LoopThread(10_000_000_000, 0) });
        BotCpuProcess process = BotCpuScript.ProcessWindow(loopOnly, loopOnly, 60);
        Assert.That(process.CampThreadCpuPct, Is.EqualTo(0));
        Assert.That(process.LoopThreadsCpuPct, Is.EqualTo(0));

        BotCpuProcessSample none = Sample(0, new());
        process = BotCpuScript.ProcessWindow(none, none, 60);
        Assert.That(process.LoopThreadsCpuPct, Is.Null);
        Assert.That(process.OffLoopCpuPct, Is.Null);
        Assert.That(process.LoopWaitMs, Is.Null);
        Assert.That(process.CampThreadCpuPct, Is.Null);
    }

    [Test]
    public void ThreadsListedAtOnlyOneEndOfTheWindowGiveDashes()
    {
        // Without the earlier listing, every thread would count its whole life in the window.
        BotCpuProcessSample listed = Sample(0, new() { [101] = LoopThread(10_000_000_000, 0),
            [104] = CampThread(1_000_000_000) });
        foreach ((BotCpuProcessSample earlier, BotCpuProcessSample later) in new[]
            { (Sample(0, null), listed), (listed, Sample(0, null)) })
        {
            BotCpuProcess process = BotCpuScript.ProcessWindow(earlier, later, 60);
            Assert.That(process.LoopThreadsCpuPct, Is.Null);
            Assert.That(process.CampThreadCpuPct, Is.Null);
            Assert.That(process.LoopWaitMs, Is.Null);
        }
    }

    // ---- The CSV file (2.3)

    private string Csv => Path.Combine(_folder, "logs", "bot-cpu.csv");

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\n"));

    // A CSV of length bytes: the header line, then zeros (sparse where the file system allows it), and a line end as
    // its last byte, as the last row's.
    private static void CsvOf(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using FileStream stream = new(path, FileMode.Create);
        stream.Write(Encoding.UTF8.GetBytes(Lines(BotCpuReport.CsvHeader)));
        stream.SetLength(length);
        stream.Seek(-1, SeekOrigin.End);
        stream.WriteByte((byte)'\n');
    }

    private void AssertOneWarning(string exception)
    {
        Assert.That(_logs, Has.Count.EqualTo(1));
        Assert.That(_logs[0].Level, Is.EqualTo(ELogLevel.Warning));
        Assert.That(_logs[0].Line, Does.StartWith("Bot CPU meter: ").And.Contain(Csv).And.Contain(exception));
    }

    [Test]
    public void TheCsvIsBotCpuCsvInTheLogsFolderOfTheServersFolder() =>
        // In the image that logs folder is the volume's, /data/logs (deploy/entrypoint.sh:66).
        Assert.That(BotCpuScript.CsvPath, Is.EqualTo(Path.Combine(AppContext.BaseDirectory, "logs", "bot-cpu.csv")));

    [Test]
    public void TheMissingFolderIsCreatedAndTheNewFileStartsWithTheHeader()
    {
        // A launcher build whose logs folder does not exist yet (Review Focus 2).
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1", "row 2"]), Is.True);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 3"]), Is.True);

        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1", "row 2", "row 3")));
        Assert.That(File.Exists(Csv + ".1"), Is.False);
        Assert.That(_logs, Is.Empty);
    }

    [Test]
    public void AWindowsRowsAreAppendedAsUtf8LinesWithNoBom()
    {
        BotCpuTotals counts = new();
        counts.Add(BotCpuKind.Gamebot, BotCpuStage.Npc, 1_000_000_000, 1_800, 4_096);
        counts.Add(BotCpuKind.Unattributed, BotCpuStage.Timer, 500_000_000, 900, 0);
        BotCpuWindow window = BotCpuReport.Window(counts, 60, 1_000_000_000, 60_000, 1000.0 / 30,
            new BotCpuSamples().Add(240), []);
        IReadOnlyList<string> rows = BotCpuReport.CsvRows(window, null,
            new DateTime(2026, 10, 18, 19, 2, 10, DateTimeKind.Utc));

        Assert.That(BotCpuScript.AppendCsv(Csv, rows), Is.True);

        byte[] bytes = File.ReadAllBytes(Csv);
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.That(bytes, Has.No.Member((byte)'\r'));
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(File.ReadAllLines(Csv), Is.EqualTo(new[] { BotCpuReport.CsvHeader }.Concat(rows)));
    }

    [Test]
    public void AnEmptyFileGetsTheHeaderAndTheOldFileIsKept()
    {
        // An empty file holds nothing to keep, so it does not replace bot-cpu.csv.1.
        Directory.CreateDirectory(Path.GetDirectoryName(Csv));
        File.WriteAllText(Csv, "");
        File.WriteAllText(Csv + ".1", "older\n");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));
        Assert.That(File.ReadAllText(Csv + ".1"), Is.EqualTo("older\n"));
    }

    // An older release's columns: other ones, or this header and one more (a release that later dropped a column).
    [TestCase("utc,win_s,kind,n,ms_sec")]
    [TestCase(BotCpuReport.CsvHeader + ",Extra")]
    public void AFileWhoseFirstLineIsAnotherHeaderIsRenamedDotOne(string header)
    {
        // The file replaces an older bot-cpu.csv.1, and a new file starts.
        string old = Lines(header, "2026-10-01T10:00:00Z,60,gamebot,240.0,438.6");
        Directory.CreateDirectory(Path.GetDirectoryName(Csv));
        File.WriteAllText(Csv, old);
        File.WriteAllText(Csv + ".1", "older\n");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(File.ReadAllText(Csv + ".1"), Is.EqualTo(old));
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));
    }

    [Test]
    public void AFileOver50MBIsRenamedDotOneBeforeTheNextAppend()
    {
        Assert.That(BotCpuScript.CsvMaxBytes, Is.EqualTo(50 * MB));
        CsvOf(Csv, BotCpuScript.CsvMaxBytes + 1);
        File.WriteAllText(Csv + ".1", "older\n");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(new FileInfo(Csv + ".1").Length, Is.EqualTo(BotCpuScript.CsvMaxBytes + 1));
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));
    }

    [Test]
    public void AFileOfExactly50MBIsAppendedTo()
    {
        CsvOf(Csv, BotCpuScript.CsvMaxBytes);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(File.Exists(Csv + ".1"), Is.False);
        Assert.That(new FileInfo(Csv).Length, Is.EqualTo(BotCpuScript.CsvMaxBytes + "row 1\n".Length));
    }

    [Test]
    public void AFolderThatCannotBeCreatedLogsOneWarningAndTheNextRowsAreWrittenOnceItCan()
    {
        // Review Focus 2: a file holds the logs folder's name, so the folder cannot be made (an IOException).
        string logs = Path.GetDirectoryName(Csv);
        File.WriteAllText(logs, "not a folder");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.IO.IOException");

        File.Delete(logs);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
        Assert.That(_logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void AFolderThatIsNotWritableLogsOneWarningAndTheNextRowsAreWrittenOnceItIs()
    {
        // Review Focus 2: a read-only folder; the file cannot be created there (not an IOException).
        Assume.That(!OperatingSystem.IsWindows(), "Unix file modes");
        string logs = Path.GetDirectoryName(Csv);
        Directory.CreateDirectory(logs);
        SetMode(logs, ReadOnly);
        bool created;
        try
        {
            File.WriteAllText(Path.Combine(logs, "probe"), "");
            created = true;
        }
        catch (UnauthorizedAccessException)
        {
            created = false;
        }

        Assume.That(created, Is.False, "root writes to a read-only folder");

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.UnauthorizedAccessException");

        SetMode(logs, ReadWrite);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
        Assert.That(_logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void ANonIoExceptionLogsOneWarningToo()
    {
        // 2.3, 3.7: a failed write logs one warning whatever the exception, not only an I/O one (bot-ai-delay.json
        // catches IOException and UnauthorizedAccessException only, GameLoopWorkMetrics.cs:102), and the meter goes
        // on: an exception that escaped would turn it off with source=error.
        BotCpuMeter.Enable();
        CsvWriteHook.SetValue(null,
            (Action<FileStream, byte[]>)((_, _) => throw new InvalidOperationException("not I/O")));

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.InvalidOperationException: not I/O");
        Assert.That(BotCpuMeter.Enabled, Is.True);

        CsvWriteHook.SetValue(null, null);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
        Assert.That(_logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void AFolderWhereTheFileShouldBeLogsOneWarning()
    {
        // A folder in the file's place gives an UnauthorizedAccessException, which is not an IOException.
        BotCpuMeter.Enable();
        Directory.CreateDirectory(Csv);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        AssertOneWarning("System.UnauthorizedAccessException");
        Assert.That(BotCpuMeter.Enabled, Is.True);

        Directory.Delete(Csv);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
    }

    [Test]
    public void AWarningWhoseLogCallThrowsDoesNotEscape()
    {
        // NLog here throws from log calls (3.7): the warning sits in its own try, so the meter goes on.
        LogHook.SetValue(null, (Action<ELogLevel, string>)((_, _) => throw new InvalidOperationException("log")));
        Directory.CreateDirectory(Csv);

        bool written = true;
        Assert.That(() => written = BotCpuScript.AppendCsv(Csv, ["row 1"]), Throws.Nothing);
        Assert.That(written, Is.False);
    }

    // An exception whose text cannot be made.
    private sealed class ExceptionWhoseTextThrows : IOException
    {
        public override string ToString() => throw new InvalidOperationException("text");
    }

    [Test]
    public void AWarningWhoseExceptionTextThrowsDoesNotEscape()
    {
        // The warning carries the exception's text, so it is made inside the guarded writer's try too (3.7).
        CsvWriteHook.SetValue(null, (Action<FileStream, byte[]>)((_, _) => throw new ExceptionWhoseTextThrows()));

        bool written = true;
        Assert.That(() => written = BotCpuScript.AppendCsv(Csv, ["row 1"]), Throws.Nothing);
        Assert.That(written, Is.False);
    }

    // A write cut short, as on a full disk: part of the bytes reach the file, then the write throws.
    private static readonly Action<FileStream, byte[]> HalfThenNoSpace = (stream, bytes) =>
    {
        stream.Write(bytes, 0, bytes.Length / 2);
        throw new IOException("No space left on device");
    };

    [Test]
    public void AWriteCutShortLeavesTheFileAsItWas()
    {
        // 2.3 loses the failed window's rows, not the next: left in the file, the cut row would have no line end
        // and the next window's first row would join it on one line.
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);
        CsvWriteHook.SetValue(null, HalfThenNoSpace);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2 of a window", "row 3 of a window"]), Is.False);
        AssertOneWarning("System.IO.IOException: No space left on device");
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1")));

        CsvWriteHook.SetValue(null, null);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 4"]), Is.True);
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1", "row 4")));
    }

    [Test]
    public void AHeaderCutShortAfterARotationKeepsTheOldFile()
    {
        // Left in the new file, a cut header would read as another release's at the next append and be renamed .1,
        // over the 50 MB the rotation had just kept there.
        CsvOf(Csv, BotCpuScript.CsvMaxBytes + 1);
        CsvWriteHook.SetValue(null, HalfThenNoSpace);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.False);
        Assert.That(new FileInfo(Csv).Length, Is.EqualTo(0));

        CsvWriteHook.SetValue(null, null);
        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 2"]), Is.True);
        Assert.That(new FileInfo(Csv + ".1").Length, Is.EqualTo(BotCpuScript.CsvMaxBytes + 1));
        Assert.That(File.ReadAllText(Csv), Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 2")));
    }

    [Test]
    public void TheBytesReachTheFileInsideTheWrite()
    {
        // Taking a cut write back needs it: a buffered stream would hold a window's few hundred bytes until Dispose,
        // outside the try that undoes the write, and a full disk would then leave the cut row in the file.
        int written = 0;
        long inside = -1;
        CsvWriteHook.SetValue(null, (Action<FileStream, byte[]>)((stream, bytes) =>
        {
            stream.Write(bytes);
            written = bytes.Length;
            inside = new FileInfo(stream.Name).Length;
        }));

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 1"]), Is.True);

        Assert.That(written, Is.EqualTo(Lines(BotCpuReport.CsvHeader, "row 1").Length));
        Assert.That(inside, Is.EqualTo(written));
    }

    // A last line with no line end: the header alone, as a hand edit may leave it, or a row cut short whose taking
    // back failed.
    [TestCase(BotCpuReport.CsvHeader)]
    [TestCase(BotCpuReport.CsvHeader + "\nrow 1\nrow 2 cut sh")]
    public void ALastLineWithNoLineEndIsEndedBeforeTheNextRows(string old)
    {
        // Left as it is, the next window's first row would join it on one line.
        Directory.CreateDirectory(Path.GetDirectoryName(Csv));
        File.WriteAllText(Csv, old);

        Assert.That(BotCpuScript.AppendCsv(Csv, ["row 3"]), Is.True);

        Assert.That(File.ReadAllText(Csv), Is.EqualTo(old + "\nrow 3\n"));
        Assert.That(File.Exists(Csv + ".1"), Is.False);
    }

    // ---- The report side (3.7, 3.8, 4.1, 4.3, 4.4): its test hooks and the meter's state it reads

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo QueueHook = typeof(BotCpuScript).GetField("_queueHook", HiddenStatic)!;
    private static readonly FieldInfo TimerHook = typeof(BotCpuScript).GetField("_timerHook", HiddenStatic)!;
    private static readonly FieldInfo NowHook = typeof(BotCpuScript).GetField("_nowHook", HiddenStatic)!;
    private static readonly FieldInfo TimestampHook = typeof(BotCpuScript).GetField("_timestampHook", HiddenStatic)!;
    private static readonly FieldInfo ZoneHook = typeof(BotCpuScript).GetField("_zoneHook", HiddenStatic)!;
    private static readonly FieldInfo CsvFileHook = typeof(BotCpuScript).GetField("_csvFile", HiddenStatic)!;
    private static readonly FieldInfo OpenRunField = typeof(BotCpuScript).GetField("_open", HiddenStatic)!;
    private static readonly FieldInfo ResultField = typeof(BotCpuScript).GetField("_result", HiddenStatic)!;
    private static readonly FieldInfo JobsField = typeof(BotCpuScript).GetField("Jobs", HiddenStatic)!;
    private static readonly FieldInfo ReportLockField = typeof(BotCpuScript).GetField("ReportLock", HiddenStatic)!;
    private static readonly FieldInfo SlotsField = typeof(BotCpuMeter).GetField("_slots", HiddenStatic)!;
    private static readonly FieldInfo CountersField = typeof(BotCpuMeter).GetField("_counters", HiddenStatic)!;
    private static readonly FieldInfo PopulationField =
        typeof(AutonomousBotRegistry).GetField("_populationForBrainTick", HiddenStatic)!;
    private static readonly PropertyInfo GameLoopTimeProperty =
        typeof(GameLoop).GetProperty(nameof(GameLoop.GameLoopTime))!;
    private static readonly PropertyInfo TickDurationProperty =
        typeof(GameLoop).GetProperty(nameof(GameLoop.TickDuration))!;

    private const string Gm = "Aldwin";
    private const BotCpuKind Gamebot = BotCpuKind.Gamebot;
    private const BotCpuStage Npc = BotCpuStage.Npc;
    private const string OnLine = "BOT_CPU_STATE on source=command by=Aldwin minutes=-";
    private const string OffLine = "BOT_CPU_STATE off source=command by=Aldwin";

    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    public class EmptyReads : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            Type result = method.ReturnType;
            if (result.IsGenericType && typeof(IEnumerable).IsAssignableFrom(result))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GetGenericArguments()[0]));
            return null;
        }
    }

    private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReads>();

    // The work items the commands queue (3.7), held until the test runs them.
    private readonly List<WaitCallback> _queued = [];

    // The 1-second timers the report made, in order: a tick is the test's call; a disposed timer can still tick once,
    // as a real timer's callback already queued does.
    private readonly List<TestTimer> _timers = [];

    private sealed class TestTimer(TimerCallback callback, object state) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Tick() => callback(state);

        public void Dispose() => Disposed = true;
    }

    // The report's own System.Threading.Timers a test made (no timer hook): TearDown waits for their callbacks.
    private readonly List<System.Threading.Timer> _realTimers = [];

    // The open run's timer, or null with no run open.
    private static IDisposable OpenRunTimer()
    {
        object run = OpenRunField.GetValue(null);
        return (IDisposable)run?.GetType().GetField("Timer", Hidden)!.GetValue(run);
    }

    // The report side's clock: the server's local time and the Stopwatch timestamp, moved together.
    private DateTime _now;
    private long _timestamp;

    private static DateTime At(int hour, int minute, int second, int ms = 0) =>
        new(2026, 10, 18, hour, minute, second, ms, DateTimeKind.Local);

    // The clock and the game time move on by seconds, at 30 game ticks a second (3.8); no timer ticks.
    private void Advance(double seconds)
    {
        _now = _now.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        _timestamp += (long)Math.Round(seconds * Stopwatch.Frequency);
        GameLoopTimeProperty.SetValue(null, GameLoop.GameLoopTime + (long)Math.Round(seconds * 1000));
    }

    private TestTimer LastTimer
    {
        get
        {
            lock (_timers)
                return _timers[^1];
        }
    }

    // One second of the meter: the clock moves on, then the latest timer ticks.
    private void Second()
    {
        Advance(1);
        LastTimer.Tick();
    }

    private void Seconds(int count)
    {
        for (int i = 0; i < count; i++)
            Second();
    }

    // The thread pool runs the held work items, in the order they were queued.
    private void RunQueued()
    {
        WaitCallback[] work = [.. _queued];
        _queued.Clear();
        foreach (WaitCallback item in work)
            item(null);
    }

    // A GM's /botcpu on, and its job run.
    private void TurnOn(int? minutes = null)
    {
        BotCpuScript.CommandOn(Gm, minutes);
        RunQueued();
    }

    private static int QueuedJobs
    {
        get
        {
            object jobs = JobsField.GetValue(null);
            return (int)jobs.GetType().GetProperty("Count")!.GetValue(jobs)!;
        }
    }

    private static void Population(int bots) => PopulationField.SetValue(null, bots);

    // A slot of the test's own, registered as a loop thread's is (3.7): the meter's slot list exists once it was on.
    private static long[] Slot()
    {
        long[] slot = new long[BotCpuTotals.SlotLength];
        List<long[]> slots = (List<long[]>)SlotsField.GetValue(null);
        lock (slots)
            slots.Add(slot);
        return slot;
    }

    private static long StopwatchTicks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);

    // As the wrapper adds to its thread's slot (3.2, step 5): ms of time and calls charged to [kind, stage].
    private static void Charge(long[] slot, BotCpuKind kind, BotCpuStage stage, double ms, long calls)
    {
        int cell = BotCpuTotals.CellIndex(kind, stage);
        slot[cell] += StopwatchTicks(ms);
        slot[cell + 1] += calls;
    }

    // As the wrapper adds to a bot's counter in the run's per-bot table (3.10), made with the bot's kind.
    private static void ChargeBot(GameObject bot, double ms, long calls, BotCpuKind kind = Gamebot)
    {
        ConditionalWeakTable<GameObject, BotCpuCounter> table =
            (ConditionalWeakTable<GameObject, BotCpuCounter>)CountersField.GetValue(null);
        BotCpuCounter counter = table.GetValue(bot, _ => new BotCpuCounter(kind));
        counter.Ticks += StopwatchTicks(ms);
        counter.Calls += calls;
    }

    // An autonomous GameBot as the top lists it: its name, level 34, a Cleric, no region and no PersistentRecord.
    private static T Bot<T>(string name) where T : GameBot
    {
        T bot = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(bot, name);
        typeof(GameObject).GetField("m_level", Hidden)!.SetValue(bot, (byte)34);
        typeof(GameBot).GetField("<ClassName>k__BackingField", Hidden)!.SetValue(bot, "Cleric");
        typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", Hidden)!.SetValue(bot, true);
        return bot;
    }

    private static GameBot Bot(string name) => Bot<GameBot>(name);

    // A region of that id, made without its constructor.
    private static Region RegionOf(ushort id)
    {
        Region region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
        typeof(Region).GetField("m_regionData", Hidden)!.SetValue(region, new RegionData { Id = id });
        return region;
    }

    // An object put in a region of that id, as the top reads it (CurrentRegion).
    private static void PutInRegion(GameObject obj, ushort id) =>
        typeof(GameObject).GetField("<CurrentRegion>k__BackingField", Hidden)!.SetValue(obj, RegionOf(id));

    // A bot whose name cannot be read: the top reads another thread's objects (3.10). Made without its constructor.
    private sealed class BotWhoseNameThrows : GameBot
    {
        private BotWhoseNameThrows() : base((OfflineWorldBotRecord)null) { }

        public override string Name
        {
            get => throw new InvalidOperationException("name");
            set { }
        }
    }

    // A bot in an instance that collapses as the top reads it: Region.OnCollapse sets the region of each object in it
    // to null on the loop (world/Region.cs:270), here once the top has read it once. Made without its constructor.
    private sealed class BotInACollapsingInstance : GameBot
    {
        private BotInACollapsingInstance() : base((OfflineWorldBotRecord)null) { }

        internal Region Collapsing;

        public override Region CurrentRegion
        {
            get => Interlocked.Exchange(ref Collapsing, null);
            set { }
        }
    }

    // The meter's log lines, in order; a process line cut to its first two fields, as its values are the test
    // process's own.
    private List<string> Logged()
    {
        lock (_logs)
        {
            return _logs.Select(log => log.Line.StartsWith("BOT_CPU_PROCESS ", StringComparison.Ordinal)
                ? string.Join(' ', log.Line.Split(' ').Take(2))
                : log.Line).ToList();
        }
    }

    private string ProcessLine()
    {
        lock (_logs)
            return _logs.Single(log => log.Line.StartsWith("BOT_CPU_PROCESS ", StringComparison.Ordinal)).Line;
    }

    private bool AllTimersDisposed()
    {
        lock (_timers)
            return _timers.All(timer => timer.Disposed);
    }

    // 3.7: the first exception turns the meter off: Enabled false, the timer disposed, the run closed with no final
    // window, one Warn line BOT_CPU_ERROR <step>: <exception>, then BOT_CPU_STATE off source=error, and no more.
    private void AssertTurnedOffByError(string step, string exception)
    {
        List<(ELogLevel Level, string Line)> logs;
        lock (_logs)
            logs = [.. _logs];
        Assert.That(logs.Where(log => log.Line.StartsWith("BOT_CPU_ERROR", StringComparison.Ordinal)),
            Has.Exactly(1).Items);
        Assert.That(logs[^2].Level, Is.EqualTo(ELogLevel.Warning));
        Assert.That(logs[^2].Line, Does.StartWith($"BOT_CPU_ERROR {step}: {exception}"));
        Assert.That(logs[^1], Is.EqualTo((ELogLevel.Info, "BOT_CPU_STATE off source=error")));
        Assert.That(logs.Where(log => log.Line.StartsWith("BOT_CPU win=", StringComparison.Ordinal)), Is.Empty);
        AssertOff();
    }

    // Off: Enabled false, no run open, every timer disposed, and the published result says so.
    private void AssertOff()
    {
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(OpenRunField.GetValue(null), Is.Null);
        Assert.That(AllTimersDisposed(), Is.True);
        Assert.That(BotCpuScript.Result.On, Is.False);
    }

    // The gamebot line of a window with 3 GameBots registered and ms charged to it in Npc at 30 calls a bot-second,
    // no bot charged.
    private static string GamebotLine(int win, string msSec, string msPerBotSec, string callsPerBotSec) =>
        $"BOT_CPU win={win} kind=gamebot n=3.0 msSec={msSec} msPerBotSec={msPerBotSec} " +
        $"callsPerBotSec={callsPerBotSec} usPerCall=33.33 allocKBPerBotSec=0.0 p50=- p95=- max=- | Npc={msPerBotSec}";

    // ---- Off and on from a command (3.7, 6.1)

    [Test]
    public void OffFromACommandTurnsTheMeterOffAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        TurnOn();
        lock (_logs)
            _logs.Clear();
        // A /proc read would throw here: the process sample has no folder to read.
        ProcSelfHook.SetValue(null, null);

        BotCpuScript.CommandOff(Gm);

        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(BotCpuScript.Result.On, Is.True, "the report has not run the job yet");

        // The job, on the report side: it closes the run, whose last 0 s are too short to write (2.2).
        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { OffLine }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
        AssertOff();
    }

    [Test]
    public void OnFromACommandLeavesEnabledAsItIsAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        ProcSelfHook.SetValue(null, null);

        DateTime? stopsAt = BotCpuScript.CommandOn(Gm, 30);

        Assert.That(stopsAt, Is.EqualTo(At(14, 32, 0)));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(_timers, Is.Empty);
        Assert.That(BotCpuScript.Result.On, Is.False);

        // The job opens the run: Enabled goes true only there (3.7).
        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.On, Is.True);
        Assert.That(result.Run, Is.EqualTo(BotCpuMeter.RunNumber));
        Assert.That(result.ByCommand, Is.True);
        Assert.That(result.OnSince, Is.EqualTo(At(14, 2, 0)));
        Assert.That(result.StopsAt, Is.EqualTo(At(14, 32, 0)));
        Assert.That(result.WindowStart, Is.EqualTo(At(14, 2, 0)));
        Assert.That(result.Last, Is.Null);
        Assert.That(_timers, Has.Count.EqualTo(1));

        // On a running meter: Enabled stays true, one job queued.
        ProcSelfHook.SetValue(null, null);
        BotCpuScript.CommandOn(Gm, null);
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Has.Count.EqualTo(1));
    }

    [Test]
    public void ResetFromACommandOnlyQueuesAJob()
    {
        TurnOn();
        BotCpuScript.CommandReset();

        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(BotCpuScript.Result.Since.Reset, Is.False, "the report has not run the job yet");
        RunQueued();
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }), "reset writes no line");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheCommandsNeverWriteTheProperty(bool property)
    {
        // 4.3: /botcpu on and off override the property until the next start; they never write it. Off, then on, a
        // new stop time, reset and off, each job run, on a meter the property started or left off.
        BotCpuScript.BOT_CPU_METER = property;
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);

        foreach (Action command in new Action[]
        {
            () => BotCpuScript.CommandOff(Gm), () => BotCpuScript.CommandOn(Gm, null),
            () => BotCpuScript.CommandOn(Gm, 30), BotCpuScript.CommandReset, () => BotCpuScript.CommandOff(Gm),
        })
        {
            command();
            RunQueued();
            Assert.That(BotCpuScript.BOT_CPU_METER, Is.EqualTo(property));
        }

        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU_STATE ", StringComparison.Ordinal)),
            Is.EqualTo(property ? 5 : 3));
        AssertOff();
    }

    [Test]
    public void TheFinalWindowEndsWhenTheOffCommandStoppedTheMeter()
    {
        // 2.2, 3.7: off stops metering at the command. Its job, run 10 s later here, ends the final window at the
        // command's instant: 30 s on each clock (the window's length, its game ticks, its CSV time), the seconds the
        // command gives for its reply.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 90, 2_700);
        Seconds(30);

        double? seconds = BotCpuScript.CommandOff(Gm);
        Advance(10);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(30, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=30", OffLine }));
        Assert.That(ProcessLine(), Does.StartWith("BOT_CPU_PROCESS win=30 tps=30.0 "));
        Assert.That(File.ReadAllLines(Csv)[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 2, 30)) + ",30,30.0,"));
        Assert.That(seconds, Is.EqualTo(30).Within(1e-9));
        AssertOff();
    }

    [Test]
    public void TheOffReplyAndItsJobAgreeOnAWindowUnderFiveSeconds()
    {
        // 2.2, 2.4: 4.9 s into the window the command's seconds are under 5 s ("too short to write"); its job, run
        // after the 5 s mark, writes no window either.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 90, 2_700);
        Seconds(4);
        Advance(0.9);

        double? seconds = BotCpuScript.CommandOff(Gm);
        Advance(1);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine }));
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(seconds, Is.EqualTo(4.9).Within(1e-9));
        AssertOff();
    }

    [Test]
    public void AnOffJobThatRunsLateGivesTheProcessValuesOverTheirOwnSeconds()
    {
        // 2.2, 3.9: the final window ends at the off command, 30 s in, but its process values can only be read when its
        // job runs, off the loop, here 10 s later. Their rates divide by the time between their own two samples: the
        // loop thread's 20 s of CPU over those 40 s is 50% of one core, not 66.7% over the window's 30 s.
        string proc = Path.Combine(_folder, "proc");
        SampleThread(proc, "101", "GameLoop\n", "10000000000 0 1\n");
        ProcSelfHook.SetValue(null, proc);
        TurnOn();
        Seconds(30);

        BotCpuScript.CommandOff(Gm);
        Advance(10);
        SampleThread(proc, "101", "GameLoop\n", "30000000000 0 2\n");
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=30", OffLine }));
        Assert.That(ProcessLine(), Does.Contain(" loopThreadsCpuPct=50.0 "));
        AssertOff();
    }

    // ---- A window (3.7, 3.8)

    [Test]
    public void AWindowIsWrittenAtTheFirstTickAtOrAfterItsSixtySeconds()
    {
        // 3 GameBots registered; 180 ms charged to gamebot in Npc over 5,400 calls; Aldric, Brennos and Cathal charged
        // 90, 60 and 30 ms.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        GameBot aldric = Bot("Aldric");
        GameBot brennos = Bot("Brennos");
        GameBot cathal = Bot("Cathal");
        ChargeBot(aldric, 90, 1_800);
        ChargeBot(brennos, 60, 1_800);
        ChargeBot(cathal, 30, 1_800);

        Seconds(59);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }));

        Second();
        // The slots' adds since on, the registry sampled each second, the game ticks, the bots of the per-bot table.
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine,
            "BOT_CPU win=60 kind=gamebot n=3.0 msSec=3.0 msPerBotSec=1.000 callsPerBotSec=30.0 usPerCall=33.33 " +
            "allocKBPerBotSec=0.0 p50=1.000 p95=1.500 max=1.500 | Npc=1.000",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:1.500:- 2=Brennos:1.000:- 3=Cathal:0.500:-",
            "BOT_CPU_PROCESS win=60",
        }));
        Assert.That(ProcessLine(),
            Does.StartWith($"BOT_CPU_PROCESS win=60 tps=30.0 cores={Environment.ProcessorCount} ")
                .And.Contain(" chargedPct=0.3 callsSec=90 "));

        // Its CSV row, with the window's end in UTC.
        string[] csv = File.ReadAllLines(Csv);
        Assert.That(csv, Has.Length.EqualTo(2));
        Assert.That(csv[0], Is.EqualTo(BotCpuReport.CsvHeader));
        Assert.That(csv[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 3, 0)) + ",60,30.0,"));
        Assert.That(csv[1], Does.EndWith(",gamebot,3.0,3.0,90.0,0.0,1.000,30.0,33.33,0.0,1.000,1.500,1.500," +
            "0.000,0.000,0.000,3.000" + string.Concat(Enumerable.Repeat(",0.000", 14))));

        // Published for /botcpu: the last window, the totals since on and the open window's start.
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.On, Is.True);
        Assert.That(result.Last.Win, Is.EqualTo(60));
        Assert.That(result.Last.Top(Gamebot).Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Aldric", "Brennos", "Cathal" }));
        Assert.That(result.LastProcess, Is.Not.Null);
        Assert.That(result.Since.Windows, Is.EqualTo(1));
        Assert.That(result.Since.MsPerBotSec(Gamebot), Is.EqualTo(1.0).Within(1e-9));
        Assert.That(result.WindowStart, Is.EqualTo(At(14, 3, 0)));
        Assert.That(result.OnSince, Is.EqualTo(At(14, 2, 0)));
        GC.KeepAlive(new[] { aldric, brennos, cathal });
    }

    [Test]
    public void ABotThatLeavesTheWorldMidWindowLeavesNoStaleEntry()
    {
        // Review Focus 3, through a tick: Brennos is charged in both windows, then leaves the world; its counter is
        // collected, or it is simply absent from the next listing.
        Population(3);
        TurnOn();
        GameBot aldric = Bot("Aldric");
        GameBot brennos = Bot("Brennos");
        GameBot cathal = Bot("Cathal");
        ChargeBot(aldric, 60, 1_800);
        ChargeBot(brennos, 600, 1_800);
        ChargeBot(cathal, 120, 1_800);
        Seconds(60);
        Assert.That(BotCpuScript.Result.Last.Top(Gamebot).Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Brennos", "Cathal", "Aldric" }));

        ChargeBot(aldric, 300, 1_800);
        ChargeBot(brennos, 6_000, 1_800);
        ChargeBot(cathal, 30, 1_800);
        ((ConditionalWeakTable<GameObject, BotCpuCounter>)CountersField.GetValue(null)).Remove(brennos);
        Population(2);
        Assert.That(() => Seconds(60), Throws.Nothing);

        // The window, the spread and the top 5 are written from the bots present, with their adds since the last read.
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU_TOP ", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            "BOT_CPU_TOP win=60 kind=gamebot 1=Brennos:10.000:- 2=Cathal:2.000:- 3=Aldric:1.000:-",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:5.000:- 2=Cathal:0.500:-",
        }));
        // n is this window's registry samples only: 2 GameBots now, 3 before.
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU win=", StringComparison.Ordinal)).Last(),
            Does.StartWith("BOT_CPU win=60 kind=gamebot n=2.0 ").And.Contain(" p50=0.500 p95=5.000 max=5.000"));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        GC.KeepAlive(new[] { aldric, brennos, cathal });
    }

    [Test]
    public void TheTopListsEachBotWithItsCountersKindAndWhatTheChatShowsOfIt()
    {
        // 3.10, 2.1 BOT_CPU_TOP and 2.4 /botcpu top: each bot of the per-bot table under its counter's kind, with its
        // name, level, class, region, and for a GameBot with a PersistentRecord its goal and activity. Aldric has a
        // record and a region; Brennos neither; Wynn is a companion (a GameBot that is no autonomous world bot) and
        // Tamsin a player bot, a GamePlayer, whose class is its character class.
        Population(3);
        TurnOn();
        GameBot aldric = Bot("Aldric");
        typeof(GameBot).GetField("<PersistentRecord>k__BackingField", Hidden)!.SetValue(aldric,
            new OfflineWorldBotRecord { ObjectiveKind = "RvR", Activity = "Guarding Caer Benowyc" });
        PutInRegion(aldric, 163);
        GameBot brennos = Bot("Brennos");
        GameBot wynn = Bot("Wynn");
        typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", Hidden)!.SetValue(wynn, false);
        GamePlayer tamsin = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(tamsin, "Tamsin");
        typeof(GameObject).GetField("m_level", Hidden)!.SetValue(tamsin, (byte)20);
        typeof(GamePlayer).GetField("m_characterClass", Hidden)!.SetValue(tamsin, new ClassFriar());
        PutInRegion(tamsin, 1);
        ChargeBot(aldric, 90, 1_800);
        ChargeBot(brennos, 60, 1_800);
        ChargeBot(wynn, 30, 1_800, BotCpuKind.Companion);
        ChargeBot(tamsin, 120, 1_800, BotCpuKind.Playerbot);

        Seconds(60);

        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU_TOP ", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:1.500:RvR 2=Brennos:1.000:-",
            "BOT_CPU_TOP win=60 kind=companion 1=Wynn:0.500",
            "BOT_CPU_TOP win=60 kind=playerbot 1=Tamsin:2.000",
        }));
        BotCpuWindow last = BotCpuScript.Result.Last;
        Assert.That(last.Top(Gamebot).Select(entry => entry.Bot), Is.EqualTo(new[]
        {
            new BotCpuBot(Gamebot, "Aldric", 34, "Cleric", 163, "RvR", "Guarding Caer Benowyc", StopwatchTicks(90),
                1_800),
            new BotCpuBot(Gamebot, "Brennos", 34, "Cleric", 0, null, null, StopwatchTicks(60), 1_800),
        }));
        Assert.That(last.Top(BotCpuKind.Companion).Select(entry => entry.Bot), Is.EqualTo(new[]
            { new BotCpuBot(BotCpuKind.Companion, "Wynn", 34, "Cleric", 0, null, null, StopwatchTicks(30), 1_800) }));
        Assert.That(last.Top(BotCpuKind.Playerbot).Select(entry => entry.Bot), Is.EqualTo(new[]
            { new BotCpuBot(BotCpuKind.Playerbot, "Tamsin", 20, "Friar", 1, null, null, StopwatchTicks(120), 1_800) }));
        Assert.That(last.Top(BotCpuKind.Piloted), Is.Empty);
        GC.KeepAlive(new GameObject[] { aldric, brennos, wynn, tamsin });
    }

    [Test]
    public void ABotWhoseInstanceCollapsesAsTheTopReadsItIsListedWithTheRegionItRead()
    {
        // 3.10: the top reads another thread's objects without a lock, a value a moment old being harmless. Eldrin's
        // instance collapses on the loop while the top reads it: the region it read is listed, and the meter goes on.
        Population(1);
        TurnOn();
        BotInACollapsingInstance eldrin = Bot<BotInACollapsingInstance>("Eldrin");
        eldrin.Collapsing = RegionOf(163);
        ChargeBot(eldrin, 90, 1_800);

        Seconds(60);

        Assert.That(Logged(), Has.None.StartWith("BOT_CPU_ERROR"));
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU_TOP ", StringComparison.Ordinal)),
            Is.EqualTo(new[] { "BOT_CPU_TOP win=60 kind=gamebot 1=Eldrin:1.500:-" }));
        Assert.That(BotCpuScript.Result.Last.Top(Gamebot).Select(entry => entry.Bot), Is.EqualTo(new[]
            { new BotCpuBot(Gamebot, "Eldrin", 34, "Cleric", 163, null, null, StopwatchTicks(90), 1_800) }));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        GC.KeepAlive(eldrin);
    }

    [Test]
    public void ATickWithEveryPlatformReaderFailingStillWritesTheKindAndTopLinesAndTheRows()
    {
        // Review Focus 4: no /proc and no cgroup (Windows, or every read failing): those fields are - and the rest is
        // written as usual.
        ProcSelfHook.SetValue(null, Path.Combine(_folder, "no-proc"));
        CgroupCpuStatHook.SetValue(null, Path.Combine(_folder, "no-cgroup", "cpu.stat"));
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        GameBot aldric = Bot("Aldric");
        ChargeBot(aldric, 90, 1_800);

        Seconds(60);

        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine,
            "BOT_CPU win=60 kind=gamebot n=3.0 msSec=3.0 msPerBotSec=1.000 callsPerBotSec=30.0 usPerCall=33.33 " +
            "allocKBPerBotSec=0.0 p50=1.500 p95=1.500 max=1.500 | Npc=1.000",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:1.500:-",
            "BOT_CPU_PROCESS win=60",
        }));
        Assert.That(ProcessLine(), Does.Contain(" loopThreadsCpuPct=- offLoopCpuPct=- campThreadCpuPct=- ")
            .And.Contain(" loopWaitMs=- throttledMs=- ").And.EndWith(" rssMB=-"));
        string[] csv = File.ReadAllLines(Csv);
        Assert.That(csv, Has.Length.EqualTo(2));
        string[] header = csv[0].Split(',');
        string[] row = csv[1].Split(',');
        foreach (string column in new[] { "loop_threads_cpu_pct", "off_loop_cpu_pct", "camp_thread_cpu_pct",
            "loop_wait_ms", "throttled_ms", "rss_mb" })
            Assert.That(row[Array.IndexOf(header, column)], Is.Empty, column);
        Assert.That(row[Array.IndexOf(header, "kind")], Is.EqualTo("gamebot"));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        GC.KeepAlive(aldric);
    }

    [Test]
    public void AFinalWindowWithNoTickKeepsTheLastRegistrySample()
    {
        // 2.2: GameBot n in a window with no registry sample uses the last sample. The minute's tick samples 3; then
        // the registry reads 5, but no tick samples it before the off job closes the last 10 s: n is still 3.
        Population(3);
        TurnOn();
        Seconds(60);
        Population(5);
        Advance(10);

        BotCpuScript.CommandOff(Gm);
        RunQueued();

        string window = "kind=gamebot n=3.0 msSec=0.0 msPerBotSec=0.000 callsPerBotSec=0.0 usPerCall=- " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=-";
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine, "BOT_CPU win=60 " + window, "BOT_CPU_PROCESS win=60",
            "BOT_CPU win=10 " + window, "BOT_CPU_PROCESS win=10", OffLine,
        }));
    }

    [Test]
    public void ResetStartsTheTotalsSinceOnAgainAndTheWindowsGoOn()
    {
        Population(3);
        TurnOn();
        long[] slot = Slot();
        Charge(slot, Gamebot, Npc, 180, 5_400);
        Seconds(60);
        Charge(slot, Gamebot, Npc, 540, 16_200);
        Seconds(60);
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(2));
        Assert.That(BotCpuScript.Result.Since.MsPerBotSec(Gamebot), Is.EqualTo(2.0).Within(1e-9));

        BotCpuScript.CommandReset();
        RunQueued();
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(0));

        Charge(slot, Gamebot, Npc, 900, 27_000);
        Seconds(60);
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(1));
        Assert.That(BotCpuScript.Result.Since.MsPerBotSec(Gamebot), Is.EqualTo(5.0).Within(1e-9));
        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU win=", StringComparison.Ordinal)), Is.EqualTo(3));
    }

    [Test]
    public void ResetWithTheMeterOffDoesNothing()
    {
        BotCpuScript.CommandReset();
        RunQueued();

        Assert.That(Logged(), Is.Empty);
        AssertOff();
    }

    // ---- One at a time (3.7, 6.1)

    [Test]
    public void AMinuteTickThenAnOffJobPutTheCountsInOneWindow()
    {
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);

        Seconds(60); // the minute's tick closes the window
        BotCpuScript.CommandOff(Gm);
        RunQueued(); // the off job: its last 0 s are too short to write (2.2)
        // A tick that waited, even a minute, finds the run closed: no window after off.
        Advance(60);
        _timers[0].Tick();

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void AnOffJobThenAWaitingTickPutTheCountsInOneWindowAndNothingAfterOff()
    {
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);

        Seconds(59);
        Advance(1);
        BotCpuScript.CommandOff(Gm);
        RunQueued(); // the off job writes the whole minute as the final window
        _timers[0].Tick(); // the minute's tick, already waiting, finds the run closed and writes nothing
        Advance(60);
        _timers[0].Tick(); // so does one that waited a minute more

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void ATickThatTakesTheLockFirstRunsTheJobsWaitingBeforeItsOwnWork()
    {
        // 3.7: whoever holds the lock first runs every job waiting, in order, then its own work.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);

        Seconds(59);
        Advance(1);
        BotCpuScript.CommandOff(Gm); // its work item has not run yet
        _timers[0].Tick(); // the tick runs the off job, then finds its run closed
        Assert.That(QueuedJobs, Is.EqualTo(0));
        RunQueued(); // the work item finds no job

        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        AssertOff();
    }

    [Test]
    public void OffThenOnQueuedTogetherRunInOrder()
    {
        Population(3);
        TurnOn();
        int first = BotCpuMeter.RunNumber;
        long[] slot = Slot();
        Charge(slot, Gamebot, Npc, 180, 5_400); // the first run's
        Seconds(30);

        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOn(Gm, null);
        Assert.That(_queued, Has.Count.EqualTo(2));
        RunQueued(); // the first work item runs both jobs, in order; the second finds none

        // A final window, then a new run whose first window holds only its own adds.
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(first + 1));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(_timers, Has.Count.EqualTo(2));
        Assert.That(_timers[0].Disposed, Is.True);
        Assert.That(_timers[1].Disposed, Is.False);
        Charge(slot, Gamebot, Npc, 60, 1_800); // the second run's
        Seconds(60);
        // The first run's timer, disposed, ticks once more a minute later: it is not the open run's, so nothing.
        Advance(60);
        _timers[0].Tick();
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine, GamebotLine(30, "6.0", "2.000", "60.0"), "BOT_CPU_PROCESS win=30", OffLine,
            OnLine, GamebotLine(60, "1.0", "0.333", "10.0"), "BOT_CPU_PROCESS win=60",
        }));
        Assert.That(BotCpuScript.Result.Run, Is.EqualTo(first + 1));
        Assert.That(BotCpuScript.Result.Since.Windows, Is.EqualTo(1));
    }

    [Test]
    public void OnThenOffQueuedTogetherEndOff()
    {
        BotCpuScript.CommandOn(Gm, 30);
        Advance(1);
        // The published result is still off: no open window for the reply. The run its on job opens comes after
        // this off's instant, so its close, not the command, ends it.
        Assert.That(BotCpuScript.CommandOff(Gm), Is.Null);
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Advance(1);

        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OffLine }));
        Assert.That(_timers, Has.Count.EqualTo(1));
        AssertOff();
    }

    [Test]
    public void AnOffFromBeforeItsRunOpenedEndsTheRunAtItsClose()
    {
        // 3.7: on then off queued together; the off's instant comes before the run the on job opens, so that run is
        // metered until its close. Here the on job takes 10 s after its window starts (the timer hook moves the clock):
        // the final window is those 10 s, not the -1 s back to the command.
        Func<TimerCallback, object, IDisposable> timers =
            (Func<TimerCallback, object, IDisposable>)TimerHook.GetValue(null);
        TimerHook.SetValue(null, (Func<TimerCallback, object, IDisposable>)((callback, state) =>
        {
            Advance(10);
            return timers!(callback, state);
        }));
        BotCpuScript.CommandOn(Gm, null);
        Advance(1);
        BotCpuScript.CommandOff(Gm);
        Advance(1);

        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }

    [Test]
    public void AnOffWhoseMinuteATickClosedBeforeItsJobWasQueuedEndsTheRunWithNoFinalWindow()
    {
        // 3.7, 2.2: the minute's tick runs between the off command's instant and its job's place in the queue, so it
        // finds no job and starts a window after metering stopped. The job, run 10 s later, ends the run at the
        // command's instant, before that window started: only the off line follows the minute's lines, as the
        // command's seconds say ("too short to write"), not 10 s of a meter already off.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        Seconds(59);
        // The command reads the Stopwatch for its instant at 59 s; the tick at 60 s comes right after.
        Func<long> stopwatch = (Func<long>)TimestampHook.GetValue(null);
        bool ticked = false;
        TimestampHook.SetValue(null, (Func<long>)(() =>
        {
            long timestamp = stopwatch!();
            if (!ticked)
            {
                ticked = true;
                Second();
            }

            return timestamp;
        }));

        double? seconds = BotCpuScript.CommandOff(Gm);
        Advance(10);
        RunQueued();

        Assert.That(ticked, Is.True);
        Assert.That(seconds, Is.LessThan(BotCpuReport.MinimumWindowSeconds));
        Assert.That(BotCpuReport.OffText(seconds!.Value),
            Is.EqualTo("Bot CPU meter off. The last 0 s were too short to write."));
        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60", OffLine }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void TwoOffsQueuedTogetherCloseTheRunOnce()
    {
        // 3.7: an off job for a run already closed does nothing; 2.1: one BOT_CPU_STATE line per change. A GM types
        // /botcpu off twice before the first job runs.
        TurnOn();
        Seconds(10);

        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOff(Gm);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }

    [Test]
    public void AnOffQueuedAsTheStopTimeClosesTheRunDoesNothing()
    {
        // 3.7: a GM's off reaches the queue while the tick that reached the stop time is closing the run, after the
        // tick ran the jobs waiting: its job, run later, finds the run closed.
        Action<ELogLevel, string> log = (Action<ELogLevel, string>)LogHook.GetValue(null);
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            log!(level, line);
            if (line == "BOT_CPU_STATE off source=timer")
                BotCpuScript.CommandOff(Gm);
        }));
        TurnOn(1);

        Seconds(60);
        Assert.That(QueuedJobs, Is.EqualTo(1));
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[]
        {
            "BOT_CPU_STATE on source=command by=Aldwin minutes=1", "BOT_CPU_PROCESS win=60",
            "BOT_CPU_STATE off source=timer",
        }));
        AssertOff();
    }

    [Test]
    public void AnOffWithTheMeterOffDoesNothing()
    {
        Assert.That(BotCpuScript.CommandOff(Gm), Is.Null);
        RunQueued();

        Assert.That(Logged(), Is.Empty);
        Assert.That(_timers, Is.Empty);
        AssertOff();
    }

    [Test]
    public void ABurstOfCommandsWritesOneStateLinePerChangeAndEndsOffWithItsTimerDisposed()
    {
        // Review Focus 5: on, off, on, off inside one second, the work items on the real thread pool. The test waits
        // for all four to end, so none is left to run a job of the next test. The count is never disposed: a work
        // item that ends after a failed wait still signals it.
        CountdownEvent ended = new(4);
        QueueHook.SetValue(null, (Action<WaitCallback>)(work => ThreadPool.QueueUserWorkItem(state =>
        {
            try
            {
                work(state);
            }
            finally
            {
                ended.Signal();
            }
        })));

        BotCpuScript.CommandOn(Gm, null);
        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOn(Gm, null);
        BotCpuScript.CommandOff(Gm);

        Assert.That(ended.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine, OnLine, OffLine }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(_timers, Has.Count.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void ATickThatFindsTheLockHeldSkipsItsSecond()
    {
        // 3.7: a tick takes the lock with Monitor.TryEnter and skips that second while it is held: its registry sample
        // of 100 is not taken, so the window's n is the other 59 seconds' 200.
        TurnOn();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        Population(100);
        Advance(1);
        // On a thread of its own, so a tick that waited for the lock fails here rather than hanging the test.
        System.Threading.Tasks.Task tick = System.Threading.Tasks.Task.Run(() => LastTimer.Tick());
        bool returned = tick.Wait(TimeSpan.FromSeconds(10));
        release.Set();
        holder.Join();
        tick.Wait();
        Assert.That(returned, Is.True, "the tick waited for the lock");

        Population(200);
        Seconds(59);

        Assert.That(Logged()[1], Does.StartWith("BOT_CPU win=60 kind=gamebot n=200.0 "));
    }

    [Test]
    public void AQueuedWorkItemWaitsForTheLock()
    {
        // 3.7: the queued work item takes the lock, waiting for it, where a tick would skip its second.
        TurnOn();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        bool offReturned = ReturnsWithTheLockHeld(() => BotCpuScript.CommandOff(Gm));

        System.Threading.Tasks.Task work = System.Threading.Tasks.Task.Run(RunQueued);
        bool waited = !work.Wait(TimeSpan.FromMilliseconds(200));
        int linesWhileHeld = Logged().Count;
        release.Set();
        holder.Join();
        work.Wait();

        Assert.That(offReturned, Is.True, "the command waited for the lock");
        Assert.That(waited, Is.True, "the work item did not wait for the lock");
        Assert.That(linesWhileHeld, Is.EqualTo(1));
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine }));
        AssertOff();
    }

    // A command on a thread of its own while another thread holds the report lock: whether it returned. The game-loop
    // thread never takes the lock (3.7); a command that did would wait, and fail the test here rather than hang it.
    private static bool ReturnsWithTheLockHeld(Action command) =>
        System.Threading.Tasks.Task.Run(command).Wait(TimeSpan.FromSeconds(10));

    [Test]
    public void NoCommandTakesTheReportLock()
    {
        // 3.7: a command only turns the meter off or queues a job, on its game-loop thread, which never takes the
        // report lock: held by another thread, on, reset and off each return at once, their jobs left waiting.
        TurnOn();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        bool onReturned = ReturnsWithTheLockHeld(() => BotCpuScript.CommandOn(Gm, 30));
        bool resetReturned = ReturnsWithTheLockHeld(BotCpuScript.CommandReset);
        bool offReturned = ReturnsWithTheLockHeld(() => BotCpuScript.CommandOff(Gm));
        release.Set();
        holder.Join();

        Assert.That(onReturned, Is.True, "on waited for the lock");
        Assert.That(resetReturned, Is.True, "reset waited for the lock");
        Assert.That(offReturned, Is.True, "off waited for the lock");
        Assert.That(QueuedJobs, Is.EqualTo(3));
        Assert.That(BotCpuMeter.Enabled, Is.False, "off stops metering at once");
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[]
            { OnLine, "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OffLine }));
        AssertOff();
    }

    [TestCase("start")]
    [TestCase("stop")]
    public void TheStartedAndStoppedHandlersWaitForTheLock(string step)
    {
        // 4.1, 3.7: the handlers run their job at once on their own thread, under the report lock: held by another
        // thread, the handler waits for it.
        if (step == "start")
            BotCpuScript.BOT_CPU_METER = true;
        else
            TurnOn();
        lock (_logs)
            _logs.Clear();
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();

        System.Threading.Tasks.Task handler = System.Threading.Tasks.Task.Run(() =>
        {
            if (step == "start")
                BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
            else
                BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);
        });
        bool waited = !handler.Wait(TimeSpan.FromMilliseconds(200));
        int linesWhileHeld = Logged().Count;
        release.Set();
        holder.Join();
        handler.Wait();

        Assert.That(waited, Is.True, "the handler did not wait for the lock");
        Assert.That(linesWhileHeld, Is.EqualTo(0));
        Assert.That(Logged(), Is.EqualTo(new[]
            { step == "start" ? BotCpuReport.StateOnByPropertyLine : "BOT_CPU_STATE off source=stop" }));
        Assert.That(BotCpuMeter.Enabled, Is.EqualTo(step == "start"));
    }

    [Test]
    public void TheRealTimerTicksEverySecondUntilTheRunCloses()
    {
        // No timer hook: the report makes its own System.Threading.Timer, ticking every second (3.7, 3.8). A tick
        // once the clock has passed the window's 60 s closes the window, so two windows take two ticks: a one-shot
        // timer stops at the first. The second can come no sooner than the second tick, 2 s after on: a shorter
        // period writes it sooner.
        Assert.That(BotCpuScript.TickMilliseconds, Is.EqualTo(1000));
        TimerHook.SetValue(null, null);
        Stopwatch sinceOn = new();
        List<TimeSpan> writtenAt = [];
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            lock (_logs)
            {
                _logs.Add((level, line));
                writtenAt.Add(sinceOn.Elapsed);
            }
        }));
        Population(3);
        sinceOn.Start();
        TurnOn();
        _realTimers.Add((System.Threading.Timer)OpenRunTimer());

        Advance(60);
        Assert.That(SpinWait.SpinUntil(() => Logged().Count >= 3, TimeSpan.FromSeconds(10)), Is.True,
            "the first window");
        Advance(60);
        Assert.That(SpinWait.SpinUntil(() => Logged().Count >= 5, TimeSpan.FromSeconds(10)), Is.True,
            "the second window");
        BotCpuScript.CommandOff(Gm);
        // The off job, run here or by a tick that took the lock first: either way it is done once this returns.
        RunQueued();

        string window = "kind=gamebot n=3.0 msSec=0.0 msPerBotSec=0.000 callsPerBotSec=0.0 usPerCall=- " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=-";
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            OnLine, "BOT_CPU win=60 " + window, "BOT_CPU_PROCESS win=60", "BOT_CPU win=60 " + window,
            "BOT_CPU_PROCESS win=60", OffLine,
        }));
        lock (_logs)
            Assert.That(writtenAt[3], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9)), "the second window's tick");
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(BotCpuScript.Result.On, Is.False);
    }

    [Test]
    public void TheRealThreadPoolRunsEachCommandsJob()
    {
        // No queue hook: a command queues its work item on the thread pool itself (3.7). With the meter off no run
        // has a timer, so only that work item runs the on job; the off job too runs with no tick. One command at a
        // time: the test waits for its line, then for the report lock, which its work item holds until it has run
        // every job waiting, so no work item is left to run a job of the next command or of a later test.
        QueueHook.SetValue(null, null);
        object reportLock = ReportLockField.GetValue(null);

        BotCpuScript.CommandOn(Gm, null);
        Assert.That(SpinWait.SpinUntil(() => BotCpuScript.Result.On, TimeSpan.FromSeconds(10)), Is.True, "on");
        lock (reportLock)
        {
            // Nothing: the lock is only waited for.
        }

        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }));

        BotCpuScript.CommandOff(Gm);
        Assert.That(SpinWait.SpinUntil(() => !BotCpuScript.Result.On, TimeSpan.FromSeconds(10)), Is.True, "off");
        lock (reportLock)
        {
            // Nothing: the lock is only waited for.
        }

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, OffLine }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(_timers, Has.Count.EqualTo(1));
        AssertOff();
    }

    // ---- No exception escapes (3.7, 6.1)

    [TestCase("process")]
    [TestCase("bot")]
    public void ATickWhoseReaderThrowsReturnsNormallyAndTurnsTheMeterOff(string reader)
    {
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        GameBot bot = null;
        if (reader == "process")
            ProcSelfHook.SetValue(null, null); // the process sample's reads throw
        else
            ChargeBot(bot = Bot<BotWhoseNameThrows>("Aldric"), 90, 1_800); // the top's read of a bot throws

        Seconds(59);
        Assert.That(() => Second(), Throws.Nothing);

        AssertTurnedOffByError("tick", reader == "process"
            ? "System.ArgumentNullException"
            : "System.InvalidOperationException: name");
        Assert.That(File.Exists(Csv), Is.False);
        // A tick already waiting finds the run closed.
        int lines = Logged().Count;
        _timers[0].Tick();
        Assert.That(Logged(), Has.Count.EqualTo(lines));
        GC.KeepAlive(bot);
    }

    [Test]
    public void ATickWhoseWriterThrowsReturnsNormallyAndTurnsTheMeterOff()
    {
        // NLog here throws from log calls (3.7): the window's first line throws.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            if (line.StartsWith("BOT_CPU win=", StringComparison.Ordinal))
                throw new InvalidOperationException("log");
            lock (_logs)
                _logs.Add((level, line));
        }));

        Seconds(59);
        Assert.That(() => Second(), Throws.Nothing);

        AssertTurnedOffByError("tick", "System.InvalidOperationException: log");
        Assert.That(File.Exists(Csv), Is.False, "the run closed with no final window");

        // The server goes on: /botcpu on starts a new run (section 9).
        int run = BotCpuMeter.RunNumber;
        TurnOn();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(run + 1));
        Assert.That(Logged()[^1], Is.EqualTo(OnLine));
    }

    [TestCase("tick")]
    [TestCase("off")]
    [TestCase("start")]
    [TestCase("stop")]
    public void WithEveryLogCallThrowingNothingEscapesAndTheMeterEndsOff(string step)
    {
        Population(3);
        if (step == "start")
            BotCpuScript.BOT_CPU_METER = true;
        else
            TurnOn();

        LogHook.SetValue(null, (Action<ELogLevel, string>)((_, _) => throw new InvalidOperationException("log")));
        TestDelegate run = step switch
        {
            "tick" => () => Seconds(60),
            "off" => () =>
            {
                BotCpuScript.CommandOff(Gm);
                RunQueued();
            },
            "start" => () => BotCpuScript.OnServerStarted(null, null, EventArgs.Empty),
            _ => () => BotCpuScript.OnServerStopped(null, null, EventArgs.Empty),
        };

        Assert.That(run, Throws.Nothing);
        AssertOff();
    }

    [Test]
    public void WithEveryLogCallThrowingTheJobAfterAFailedOneStillRuns()
    {
        // 3.7: the catch never rethrows. The off job's line throws, then its BOT_CPU_ERROR and off source=error lines
        // too. The outer catches would hide a catch that threw, but not the jobs it left waiting: the on job queued
        // after the off runs from the same work item, opens the next run and fails the same way.
        TurnOn();
        int run = BotCpuMeter.RunNumber;
        LogHook.SetValue(null, (Action<ELogLevel, string>)((_, _) => throw new InvalidOperationException("log")));
        BotCpuScript.CommandOff(Gm);
        BotCpuScript.CommandOn(Gm, null);
        Assert.That(_queued, Has.Count.EqualTo(2));

        Assert.That(() => _queued[0](null), Throws.Nothing);

        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(run + 1));
        AssertOff();
    }

    [TestCase("on")]
    [TestCase("off")]
    [TestCase("start")]
    [TestCase("stop")]
    public void BotCpuErrorNamesTheJobOrHandlerThatFailed(string step)
    {
        // 2.1, 3.7: BOT_CPU_ERROR <step> names what failed. Only the step's own state line throws, so the error
        // and off source=error lines are written.
        string failing = step switch
        {
            "on" => OnLine,
            "off" => OffLine,
            "start" => BotCpuReport.StateOnByPropertyLine,
            _ => "BOT_CPU_STATE off source=stop",
        };
        if (step is "off" or "stop")
            TurnOn();
        BotCpuScript.BOT_CPU_METER = step == "start";
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            if (line == failing)
                throw new InvalidOperationException("log");
            lock (_logs)
                _logs.Add((level, line));
        }));

        switch (step)
        {
            case "on":
                BotCpuScript.CommandOn(Gm, null);
                RunQueued();
                break;
            case "off":
                BotCpuScript.CommandOff(Gm);
                RunQueued();
                break;
            case "start":
                BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
                break;
            default:
                BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);
                break;
        }

        AssertTurnedOffByError(step, "System.InvalidOperationException: log");
    }

    [TestCase("line")]
    [TestCase("text")]
    public void ABotCpuErrorThatCannotBeWrittenStillLeavesTheOffLine(string fault)
    {
        // 3.7: BOT_CPU_ERROR and off source=error each sit in a try of their own, and the error line is made inside
        // its own: the text of the exception it carries can throw. The window's first line throws; then the error
        // line's log call throws ("line"), or the text of the exception ("text"). The off line is still written.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        Exception failure = fault == "line" ? new InvalidOperationException("log") : new ExceptionWhoseTextThrows();
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) =>
        {
            if (line.StartsWith("BOT_CPU win=", StringComparison.Ordinal))
                throw failure;
            if (line.StartsWith("BOT_CPU_ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException("log");
            lock (_logs)
                _logs.Add((level, line));
        }));

        Seconds(59);
        Assert.That(() => Second(), Throws.Nothing);

        List<(ELogLevel Level, string Line)> logs;
        lock (_logs)
            logs = [.. _logs];
        Assert.That(logs, Is.EqualTo(new[]
            { (ELogLevel.Info, OnLine), (ELogLevel.Info, "BOT_CPU_STATE off source=error") }));
        AssertOff();
    }

    [Test]
    public void ACsvWriteThatThrowsANonIoExceptionLogsOneWarningAndTheMeterGoesOn()
    {
        Population(3);
        TurnOn();
        long[] slot = Slot();
        Charge(slot, Gamebot, Npc, 180, 5_400);
        CsvWriteHook.SetValue(null,
            (Action<FileStream, byte[]>)((_, _) => throw new InvalidOperationException("not I/O")));

        Seconds(60);

        List<(ELogLevel Level, string Line)> warnings;
        lock (_logs)
            warnings = _logs.Where(log => log.Level == ELogLevel.Warning).ToList();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].Line, Does.StartWith("Bot CPU meter: could not write")
            .And.Contain("System.InvalidOperationException: not I/O"));
        Assert.That(Logged().Where(line => line.StartsWith("BOT_CPU", StringComparison.Ordinal)),
            Is.EqualTo(new[] { OnLine, GamebotLine(60, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=60" }));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.On, Is.True);
        Assert.That(BotCpuScript.Result.Last.Win, Is.EqualTo(60));

        // The next minute's rows are written.
        CsvWriteHook.SetValue(null, null);
        Charge(slot, Gamebot, Npc, 180, 5_400);
        Seconds(60);
        string[] csv = File.ReadAllLines(Csv);
        Assert.That(csv, Has.Length.EqualTo(2));
        Assert.That(csv[1], Does.StartWith(BotCpuReport.CsvUtc(At(14, 4, 0)) + ",60,"));
    }

    // ---- The stop time (4.3, 6.1)

    [Test]
    public void AStopAt1432StopsAtTheFirstSecondAtOrAfterIt()
    {
        // on 30 at 14:02:00 stops at 14:32:00; the timer ticks half a second into each second.
        Assert.That(BotCpuScript.CommandOn(Gm, 30), Is.EqualTo(At(14, 32, 0)));
        RunQueued();
        Advance(0.5);

        Seconds(1_799);
        Assert.That(_now, Is.EqualTo(At(14, 31, 59, 500)));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.On, Is.True);

        Second();
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        // The minute closed at 14:32:00.5 is the 30th; the final window that follows it is 0 s long, not written.
        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU_PROCESS ", StringComparison.Ordinal)),
            Is.EqualTo(30));
        Assert.That(Logged()[^2], Is.EqualTo("BOT_CPU_PROCESS win=60"));
        AssertOff();
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void AStopTimeRunsItsMinutesAcrossAClockChange(int hours)
    {
        // 4.3: on 30 turns itself off after 30 minutes. Ten minutes in, the server's local clock moves an hour back
        // (summer time ends) or ahead (it begins): the stop time shown stays 14:32, and the meter still stops 30
        // minutes after on, not an hour later or at once.
        Assert.That(BotCpuScript.CommandOn(Gm, 30), Is.EqualTo(At(14, 32, 0)));
        RunQueued();
        Seconds(600);
        _now = _now.AddHours(hours);

        Seconds(1_199);
        Assert.That(BotCpuScript.Result.On, Is.True);
        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(At(14, 32, 0)));

        Second();
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void TheStatusCountsItsMinutesOnAcrossAClockChange(int hours)
    {
        // 2.4: the status line gives the minutes the meter has been on. Ten minutes in, the server's local clock moves
        // an hour back (summer time ends) or ahead (it begins): twenty minutes later it says 30 min, not 0 or 90, and
        // still on since 14:02. The minutes come from the Stopwatch, as the stop time's do (4.3).
        BotCpuScript.CommandOn(Gm, null);
        RunQueued();
        Seconds(600);
        _now = _now.AddHours(hours);
        Seconds(1_200);

        Assert.That(BotCpu()[0], Is.EqualTo("Bot CPU meter: on since 14:02 (by command, 30 min; no stop time)."));
    }

    [Test]
    public void AResetAndANewStopTimeKeepTheMinutesOn()
    {
        // 2.4: the minutes on count from when the meter went on. /botcpu reset starts only the totals since on again,
        // and on <minutes> on a running meter changes only its stop time: twenty minutes in, it says 20 min.
        BotCpuScript.CommandOn(Gm, null);
        RunQueued();
        Seconds(600);
        BotCpu("reset");
        RunQueued();
        Seconds(300);
        BotCpuScript.CommandOn(Gm, 30);
        RunQueued();
        Seconds(300);

        Assert.That(BotCpu()[0], Is.EqualTo("Bot CPU meter: on since 14:02 (by command, 20 min; stops at 14:47)."));
    }

    // A server's time zone with summer time: UTC-6, and UTC-5 from the second Sunday of March at 02:00 to the first
    // Sunday of November at 02:00, as US Central time.
    private static readonly TimeZoneInfo SummerTimeZone = TimeZoneInfo.CreateCustomTimeZone("HearthDAoC test",
        TimeSpan.FromHours(-6), "HearthDAoC test", "HearthDAoC test standard", "HearthDAoC test summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2,
                    DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1,
                    DayOfWeek.Sunday)),
        ]);

    [TestCase(2026, 11, 1, 0, 50, 90, 1, 20)] // summer time ends at 02:00, the clock back to 01:00: not 02:20
    [TestCase(2027, 3, 14, 1, 30, 60, 3, 30)] // summer time begins at 02:00, the clock on to 03:00: not 02:30
    public void AStopTimeShowsTheLocalTimeTheRunStopsAtAcrossAClockChange(int year, int month, int day, int hour,
        int minute, int minutes, int stopHour, int stopMinute)
    {
        // 4.3, 2.4: on <minutes> stops after that many minutes, and the reply and the status show when, in the server's
        // local time: that many minutes on in UTC, shown in the local time of then.
        ZoneHook.SetValue(null, SummerTimeZone);
        _now = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
        DateTime stopsAt = new(year, month, day, stopHour, stopMinute, 0, DateTimeKind.Local);

        Assert.That(BotCpuScript.CommandOn(Gm, minutes), Is.EqualTo(stopsAt));
        RunQueued();

        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(stopsAt));
    }

    // A server's time zone 5 hours ahead of UTC, with no summer time: a local time taken for UTC is 5 hours off here.
    private static readonly TimeZoneInfo FiveHoursAheadZone = TimeZoneInfo.CreateCustomTimeZone("HearthDAoC test +5",
        TimeSpan.FromHours(5), "HearthDAoC test +5", "HearthDAoC test +5");

    [Test]
    public void OnTheServersOwnClocksTheStopTimeIsItsMinutesOnInUtcAndTheRunStartsAtItsJob()
    {
        // 4.3, 2.4, 2.2: the server's clocks, not the test's (no local time or Stopwatch hook). on 30 shows the time
        // 30 minutes on in UTC, in the server's zone (here the zone hook's, as the stop time's way through UTC reads
        // it); the run opens at its job's local time (DateTime.Now) and Stopwatch timestamp.
        NowHook.SetValue(null, null);
        TimestampHook.SetValue(null, null);
        ZoneHook.SetValue(null, FiveHoursAheadZone);

        DateTime earliest = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddMinutes(30), FiveHoursAheadZone);
        DateTime? stopsAt = BotCpuScript.CommandOn(Gm, 30);
        DateTime latest = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddMinutes(30), FiveHoursAheadZone);
        DateTime localBefore = DateTime.Now;
        long timestampBefore = Stopwatch.GetTimestamp();
        RunQueued();
        long timestampAfter = Stopwatch.GetTimestamp();
        DateTime localAfter = DateTime.Now;

        Assert.That(stopsAt, Is.InRange(earliest, latest));
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.StopsAt, Is.EqualTo(stopsAt));
        Assert.That(result.OnSince, Is.InRange(localBefore, localAfter));
        Assert.That(result.WindowStartTimestamp, Is.InRange(timestampBefore, timestampAfter));
    }

    [Test]
    public void OnWithNoMinutesOnARunningMeterClearsTheStopTime()
    {
        TurnOn(30);
        Seconds(10);

        BotCpuScript.CommandOn(Gm, null);
        RunQueued();

        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OnLine }));
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);
        Assert.That(_timers, Has.Count.EqualTo(1));
        Advance(30 * 60);
        Second();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.On, Is.True);
    }

    [Test]
    public void On30OnARunningMeterWritesTheOnLineAgainAndStopsThere()
    {
        TurnOn();
        int run = BotCpuMeter.RunNumber;
        Seconds(10);

        DateTime? stopsAt = BotCpuScript.CommandOn(Gm, 30);
        Assert.That(BotCpuMeter.Enabled, Is.True);
        RunQueued();

        Assert.That(stopsAt, Is.EqualTo(At(14, 32, 10)));
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));
        Assert.That(BotCpuMeter.RunNumber, Is.EqualTo(run), "no new run");
        Assert.That(_timers, Has.Count.EqualTo(1));
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(result.StopsAt, Is.EqualTo(At(14, 32, 10)));
        Assert.That(result.OnSince, Is.EqualTo(At(14, 2, 0)));
        Assert.That(result.Run, Is.EqualTo(run));

        Advance(30 * 60 - 1);
        Second();
        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }

    // ---- The started and stopped handlers (4.1)

    [Test]
    public void ThePropertyOnStartsTheMeterWithTheServerAndItsStopWritesThePartialWindow()
    {
        Assert.That(typeof(BotCpuScript).GetMethod(nameof(BotCpuScript.OnServerStarted))!
            .GetCustomAttribute<GameServerStartedEventAttribute>(), Is.Not.Null);
        Assert.That(typeof(BotCpuScript).GetMethod(nameof(BotCpuScript.OnServerStopped))!
            .GetCustomAttribute<GameServerStoppedEventAttribute>(), Is.Not.Null);
        BotCpuScript.BOT_CPU_METER = true;
        Population(3);

        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);

        // At once, on the event's own thread: nothing queued.
        Assert.That(_queued, Is.Empty);
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=property" }));
        Assert.That(BotCpuScript.Result.On, Is.True);
        Assert.That(BotCpuScript.Result.ByCommand, Is.False);
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);

        Charge(Slot(), Gamebot, Npc, 90, 2_700);
        Seconds(30);
        BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);

        Assert.That(_queued, Is.Empty);
        Assert.That(Logged(), Is.EqualTo(new[]
        {
            "BOT_CPU_STATE on source=property", GamebotLine(30, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=30",
            "BOT_CPU_STATE off source=stop",
        }));
        Assert.That(File.ReadAllLines(Csv), Has.Length.EqualTo(2));
        AssertOff();
    }

    [Test]
    public void ThePropertyOffStartsNothing()
    {
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
        BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);

        Assert.That(Logged(), Is.Empty);
        Assert.That(_timers, Is.Empty);
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(BotCpuScript.Result.On, Is.False);
    }

    [Test]
    public void AStopTimeAlsoStopsAMeterThePropertyStarted()
    {
        // 4.3: /botcpu on <minutes> turns itself off, whether the property or a command started the meter.
        BotCpuScript.BOT_CPU_METER = true;
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);
        BotCpuScript.CommandOn(Gm, 1);
        RunQueued();
        Assert.That(BotCpuScript.Result.ByCommand, Is.False, "the meter was turned on by the property");

        Seconds(60);

        Assert.That(Logged()[^1], Is.EqualTo("BOT_CPU_STATE off source=timer"));
        AssertOff();
    }

    [Test]
    public void TheStopRunsAGmsOffWaitingFirst()
    {
        // The stopped handler runs the jobs waiting before its own work: the run closes by the GM's off.
        TurnOn();
        Seconds(10);
        BotCpuScript.CommandOff(Gm);

        BotCpuScript.OnServerStopped(null, null, EventArgs.Empty);

        Assert.That(Logged(), Is.EqualTo(new[] { OnLine, "BOT_CPU_PROCESS win=10", OffLine }));
        AssertOff();
    }

    // ---- /botcpu (2.4, 3.7, 6.1)

    // The command's description and syntax lines (2.4), which a GM sees for arguments it refuses.
    private const string CommandDescription =
        "HearthDAoC: measure the game loop's CPU per kind of bot (BOT_CPU lines in the server log)";

    private static readonly string[] CommandUsage =
    [
        "/botcpu - status, the last minute and the totals since on",
        "/botcpu on [minutes] - start, for 1-1440 minutes, or until off or the next start",
        "/botcpu off - stop and write the last partial minute",
        "/botcpu top [gamebot|companion|playerbot|piloted] - the 5 costliest bots of the last minute " +
        "(gamebot if none given)",
        "/botcpu reset - start the totals since on again",
    ];

    private const string OffStatus = "Bot CPU meter: off. /botcpu on [minutes] turns it on.";
    private const string WhereTheLinesGo = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

    // /botcpu with these words after it, typed by the GM: its reply's lines, or null for the syntax lines.
    private static IReadOnlyList<string> BotCpu(string words = "") =>
        BotCpuCommandHandler.Run(Gm, words.Length == 0 ? ["&botcpu"] : ["&botcpu", .. words.Split(' ')]);

    [Test]
    public void BotCpuIsAGmCommandWithTheSpecsAttribute()
    {
        // 2.4: &botcpu, for GMs, with no alias, found by ScriptMgr as a command handler of the GameServer assembly.
        Type handler = typeof(BotCpuCommandHandler);
        CmdAttribute[] attributes = handler.GetCustomAttributes<CmdAttribute>(false).ToArray();
        Assert.That(attributes, Has.Length.EqualTo(1));
        Assert.That(attributes[0].Cmd, Is.EqualTo("&botcpu"));
        Assert.That(attributes[0].Aliases, Is.Null);
        Assert.That(attributes[0].Header, Is.Null);
        Assert.That(attributes[0].Level, Is.EqualTo((uint)ePrivLevel.GM));
        Assert.That(attributes[0].Description, Is.EqualTo(CommandDescription));
        Assert.That(attributes[0].Usage, Is.EqualTo(CommandUsage));
        Assert.That(handler.GetInterface("DOL.GS.Commands.ICommandHandler"), Is.Not.Null);
        Assert.That(handler.Assembly, Is.SameAs(typeof(GameServer).Assembly));
    }

    [Test]
    public void BotCpuIsTheOnlyCommandNameThatStartsWithBot()
    {
        // 2.4: a command resolves its exact name first, then the one command the caller may use whose name or alias
        // starts with what was typed, in any case (ScriptMgr.GuessCommand). /bot is not registered (section 1), so a
        // GM's /bot runs /botcpu: on purpose, as long as no other command name or alias starts with &bot.
        string[] names = typeof(GameServer).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<CmdAttribute>(false))
            .SelectMany(attribute => new[] { attribute.Cmd }.Concat(attribute.Aliases ?? []))
            .Where(name => name.StartsWith("&bot", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.That(names, Is.EqualTo(new[] { "&botcpu" }));
    }

    [Test]
    public void BotCpuOffTurnsTheMeterOffAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        // 6.1, 3.7: on its game-loop thread the command only turns the meter off and queues the job; its reply comes
        // from the published result, 37 s into the open window.
        Population(3);
        TurnOn();
        Charge(Slot(), Gamebot, Npc, 111, 3_330);
        Seconds(37);
        lock (_logs)
            _logs.Clear();
        // A /proc read would throw here: the process sample has no folder to read.
        ProcSelfHook.SetValue(null, null);

        IReadOnlyList<string> reply = BotCpu("off");

        Assert.That(reply, Is.EqualTo(new[] { "Bot CPU meter off. The last 37 s are being written." }));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(BotCpuScript.Result.On, Is.True, "the report has not run the job yet");

        // The job, on the report side: the last 37 s, then the GM's off line.
        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[]
            { GamebotLine(37, "3.0", "1.000", "30.0"), "BOT_CPU_PROCESS win=37", OffLine }));
        AssertOff();
    }

    [Test]
    public void BotCpuOnLeavesEnabledAsItIsAndQueuesOneJobBeforeAnyFileProcReadOrLogWrite()
    {
        // 6.1, 3.7: Enabled goes true only in the on job, on the report side.
        ProcSelfHook.SetValue(null, null);

        IReadOnlyList<string> reply = BotCpu("on 30");

        Assert.That(reply, Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Is.Empty);
        Assert.That(File.Exists(Csv), Is.False);
        Assert.That(_timers, Is.Empty);

        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));

        // On a running meter: Enabled stays true, one job queued, still before any file, /proc read or log write.
        Seconds(10);
        ProcSelfHook.SetValue(null, null);
        reply = BotCpu("on");
        Assert.That(reply,
            Is.EqualTo(new[] { "Bot CPU meter already on; it now runs until /botcpu off or the next start." }));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(_queued, Has.Count.EqualTo(1));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(Logged(), Has.Count.EqualTo(1));

        ProcSelfHook.SetValue(null, _procSelf);
        RunQueued();
        Assert.That(Logged()[^1], Is.EqualTo(OnLine));
        Assert.That(BotCpuScript.Result.StopsAt, Is.Null);
    }

    [Test]
    public void BotCpuShowsTheStatusOfThePublishedResult()
    {
        // 2.4: off; on with no window yet; on with a window, the status line, the last minute, its kind lines and the
        // totals since on, made by BotCpuReport from the result the report published. None queues a job.
        Assert.That(BotCpu(), Is.EqualTo(new[] { OffStatus }));

        Population(3);
        TurnOn(30);
        Assert.That(BotCpu(), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by command, 0 min; stops at 14:32).",
            "No full minute yet; the first lines come at 14:03.",
        }));

        Charge(Slot(), Gamebot, Npc, 180, 5_400);
        Seconds(60);
        Advance(30);
        IReadOnlyList<string> status = BotCpu();
        BotCpuResult result = BotCpuScript.Result;
        Assert.That(status, Is.EqualTo(BotCpuReport.StatusText(result.OnSince, result.ByCommand, result.StopsAt,
            TimeSpan.FromSeconds(90), result.Last, result.LastProcess, result.Since)));
        Assert.That(status, Has.Count.EqualTo(4));
        Assert.That(status[0], Is.EqualTo("Bot CPU meter: on since 14:02 (by command, 1 min; stops at 14:32)."));
        // The process values are the test process's own; it has no loop threads, so its off-the-loop share is -.
        Assert.That(status[1],
            Does.StartWith("Last minute (60 s, 30.0 ticks/s): process ").And.EndWith(" off the loop."));
        Assert.That(status[2], Is.EqualTo("gamebot n=3.0: 1.000 ms/s per bot (3.0 ms/s in all); most in Npc 1.000"));
        Assert.That(status[3], Is.EqualTo("Since on (1 min): gamebot 1.000 ms/s per bot."));
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        BotCpu("off");
        RunQueued();
        Assert.That(BotCpu(), Is.EqualTo(new[] { OffStatus }));
    }

    [Test]
    public void BotCpuShowsARunThePropertyStartedAsByPropertyWithNoStopTime()
    {
        // 2.4: the status line's other variants, through the command: by property, no stop time.
        BotCpuScript.BOT_CPU_METER = true;
        BotCpuScript.OnServerStarted(null, null, EventArgs.Empty);

        Assert.That(BotCpu(), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by property, 0 min; no stop time).",
            "No full minute yet; the first lines come at 14:03.",
        }));
        Assert.That(QueuedJobs, Is.EqualTo(0));
    }

    [Test]
    public void BotCpuOnRepliesWithItsStopTimeOrNone()
    {
        // 2.4: on and on <minutes>, with the meter off, then on.
        Assert.That(BotCpu("on"),
            Is.EqualTo(new[] { "Bot CPU meter on until /botcpu off or the next start" + WhereTheLinesGo }));
        RunQueued();
        Seconds(10);
        Assert.That(BotCpu("on 30"), Is.EqualTo(new[] { "Bot CPU meter already on; it now stops at 14:32." }));
        RunQueued();
        Assert.That(BotCpu("on"),
            Is.EqualTo(new[] { "Bot CPU meter already on; it now runs until /botcpu off or the next start." }));
        RunQueued();
        Assert.That(Logged(),
            Is.EqualTo(new[] { OnLine, "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OnLine }));

        BotCpu("off");
        RunQueued();
        Assert.That(BotCpu("on 30"),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
    }

    [TestCase("on 1", "Bot CPU meter on for 1 minute (until 14:03)", "1")]
    [TestCase("on 1440", "Bot CPU meter on for 1440 minutes (until 14:02)", "1440")]
    public void TheMinutesRunFromOneToADay(string words, string reply, string minutes)
    {
        // 2.4: 1-1440 minutes; 1440 stops at the same time the next day.
        Assert.That(BotCpu(words), Is.EqualTo(new[] { reply + WhereTheLinesGo }));
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=" + minutes }));
        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(At(14, 2, 0).AddMinutes(int.Parse(minutes))));
    }

    [TestCase(37, "Bot CPU meter off. The last 37 s are being written.")]
    [TestCase(3, "Bot CPU meter off. The last 3 s were too short to write.")]
    public void BotCpuOffRepliesWithTheLastSecondsOrThatItIsAlreadyOff(int seconds, string reply)
    {
        // 2.4, 2.2: the open window's seconds, written from 5 s; then, off, a second off changes nothing.
        TurnOn();
        Seconds(seconds);

        Assert.That(BotCpu("off"), Is.EqualTo(new[] { reply }));
        RunQueued();
        AssertOff();

        Assert.That(BotCpu("off"), Is.EqualTo(new[] { "Bot CPU meter is already off." }));
        RunQueued();
        Assert.That(Logged().Count(line => line.StartsWith("BOT_CPU_STATE ", StringComparison.Ordinal)),
            Is.EqualTo(2));
        Assert.That(Logged()[^1], Is.EqualTo(OffLine));
        AssertOff();
    }

    [Test]
    public void BotCpuOffRightAfterOnIsStillQueuedAndEndsOff()
    {
        // 3.7: on then off ends off. The on job has not run, so the published result is still off and the reply says
        // so; the off is queued all the same, after the on, and its job closes the run the on job opens.
        Assert.That(BotCpu("on 30"),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));

        Assert.That(BotCpu("off"), Is.EqualTo(new[] { "Bot CPU meter is already off." }));
        Assert.That(BotCpuMeter.Enabled, Is.False);
        Assert.That(QueuedJobs, Is.EqualTo(2));

        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30", OffLine }));
        AssertOff();
    }

    [Test]
    public void BotCpuTopListsTheLastMinutesCostliestBotsOfABotKind()
    {
        // 2.4: off, the off text; on with no window yet, when the first lines come; then the top of the last window,
        // for gamebot when no kind is given, or that no bot of the kind was charged. None queues a job.
        Assert.That(BotCpu("top"), Is.EqualTo(new[] { OffStatus }));

        Population(2);
        TurnOn();
        Assert.That(BotCpu("top companion"),
            Is.EqualTo(new[] { "No full minute yet; the first lines come at 14:03." }));

        GameBot aldric = Bot("Aldric");
        typeof(GameBot).GetField("<PersistentRecord>k__BackingField", Hidden)!.SetValue(aldric,
            new OfflineWorldBotRecord { ObjectiveKind = "RvR", Activity = "Guarding Caer Benowyc" });
        PutInRegion(aldric, 163);
        GameBot brennos = Bot("Brennos");
        ChargeBot(aldric, 90, 1_800);
        ChargeBot(brennos, 60, 1_800);
        Seconds(60);

        string[] gamebots =
        [
            "1. Aldric (34 Cleric, region 163): 1.500 ms/s, 30 calls/s, RvR, \"Guarding Caer Benowyc\"",
            "2. Brennos (34 Cleric, region 0): 1.000 ms/s, 30 calls/s, -, \"\"",
        ];
        Assert.That(BotCpu("top"), Is.EqualTo(gamebots));
        Assert.That(BotCpu("top gamebot"), Is.EqualTo(gamebots));
        foreach (string kind in new[] { "companion", "playerbot", "piloted" })
            Assert.That(BotCpu("top " + kind), Is.EqualTo(new[] { $"No {kind} was charged in the last minute." }));
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        BotCpu("off");
        RunQueued();
        Assert.That(BotCpu("top"), Is.EqualTo(new[] { OffStatus }));
        GC.KeepAlive(new[] { aldric, brennos });
    }

    [Test]
    public void BotCpuResetStartsTheTotalsAgainOnlyWhileOn()
    {
        // 2.4: off, the off text and no job; on, the totals since on start again in the job.
        Assert.That(BotCpu("reset"), Is.EqualTo(new[] { OffStatus }));
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        TurnOn();
        Assert.That(BotCpu("reset"), Is.EqualTo(new[] { "Bot CPU totals reset." }));
        Assert.That(QueuedJobs, Is.EqualTo(1));
        Assert.That(BotCpuScript.Result.Since.Reset, Is.False, "the report has not run the job yet");
        RunQueued();
        Assert.That(BotCpuScript.Result.Since.Reset, Is.True);
        Assert.That(BotCpuMeter.Enabled, Is.True);
    }

    [TestCase("x")]
    [TestCase("on 0")]
    [TestCase("on 1441")]
    [TestCase("on x")]
    [TestCase("on -5")]
    [TestCase("on 30 x")]
    [TestCase("off now")]
    [TestCase("top monster")]
    [TestCase("top player")]
    [TestCase("top unattributed")]
    [TestCase("top x")]
    [TestCase("top gamebot x")]
    [TestCase("reset x")]
    public void RefusedArgumentsGetTheSyntaxLinesAndChangeNothing(string words)
    {
        // 2.4: anything else, a kind that is not a bot kind, minutes out of range: the syntax lines, the meter off or
        // on, with nothing turned off and no job queued.
        Assert.That(BotCpu(words), Is.Null);
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));

        Population(3);
        TurnOn(30);
        Seconds(60);
        List<string> logged = Logged();
        Assert.That(BotCpu(words), Is.Null);
        Assert.That(_queued, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(0));
        Assert.That(BotCpuMeter.Enabled, Is.True);
        Assert.That(BotCpuScript.Result.StopsAt, Is.EqualTo(At(14, 32, 0)));
        Assert.That(Logged(), Is.EqualTo(logged));
    }

    [TestCase("ON 30")]
    [TestCase("On 30")]
    public void OnInAnotherCaseIsAccepted(string words)
    {
        // Review Focus 1: case is ignored, as HEARTHDAOC_BOT_CPU ignores it.
        Assert.That(BotCpu(words),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { "BOT_CPU_STATE on source=command by=Aldwin minutes=30" }));
        Assert.That(BotCpu("Reset"), Is.EqualTo(new[] { "Bot CPU totals reset." }));
        Assert.That(BotCpu("OFF"), Is.EqualTo(new[] { "Bot CPU meter off. The last 0 s were too short to write." }));
        RunQueued();
        AssertOff();
    }

    [TestCase("top GameBot")]
    [TestCase("TOP GAMEBOT")]
    [TestCase("Top gamebot")]
    public void ATopKindInAnotherCaseIsAccepted(string words)
    {
        // Review Focus 1: case is ignored, as HEARTHDAOC_BOT_CPU ignores it.
        Population(1);
        TurnOn();
        GameBot aldric = Bot("Aldric");
        ChargeBot(aldric, 90, 1_800);
        Seconds(60);

        Assert.That(BotCpu(words),
            Is.EqualTo(new[] { "1. Aldric (34 Cleric, region 0): 1.500 ms/s, 30 calls/s, -, \"\"" }));
        Assert.That(BotCpu("top Companion"), Is.EqualTo(new[] { "No companion was charged in the last minute." }));
        GC.KeepAlive(aldric);
    }

    [Test]
    public void TheRepliesSpeakOfTheStateBeforeTheirJobRuns()
    {
        // 3.7: a command's job can run on the thread pool before the command replies. Here each job runs inside its
        // queueing: on still says the meter was off, and off gives the last seconds of the run it ends.
        QueueHook.SetValue(null, (Action<WaitCallback>)(work => work(null)));

        Assert.That(BotCpu("on 30"),
            Is.EqualTo(new[] { "Bot CPU meter on for 30 minutes (until 14:32)" + WhereTheLinesGo }));
        Assert.That(BotCpuScript.Result.On, Is.True);
        Seconds(37);
        Assert.That(BotCpu("off"), Is.EqualTo(new[] { "Bot CPU meter off. The last 37 s are being written." }));
        AssertOff();
    }

    [Test]
    public void NoBotCpuReplyWaitsForTheReportLock()
    {
        // 2.4, 3.7: the command never builds a report: it reads the result the report published and, for a change,
        // queues a job. With the report lock held by another thread, each reply comes at once.
        Population(3);
        TurnOn();
        Seconds(60);
        object reportLock = ReportLockField.GetValue(null);
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim release = new();
        Thread holder = new(() =>
        {
            lock (reportLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        List<string> waited = [];
        foreach (string words in new[] { "", "top", "top companion", "reset", "on 30", "on", "off", "x" })
        {
            if (!ReturnsWithTheLockHeld(() => BotCpu(words)))
                waited.Add(words);
        }

        release.Set();
        holder.Join();

        Assert.That(waited, Is.Empty);
        Assert.That(QueuedJobs, Is.EqualTo(4));
        RunQueued();
        AssertOff();
    }

    // A GM's chat: each line the server sends the client.
    public class ChatRecorder : DispatchProxy
    {
        public readonly List<string> Lines = [];

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IPacketLib.SendMessage))
                Lines.Add((string)args[0]);
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType)
                : null;
        }
    }

    // A GM in game, the test's Gm, made without the constructors, whose chat the test reads.
    private static GameClient GmClient(out ChatRecorder chat)
    {
        GamePlayer gm = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GameObject).GetField("m_name", Hidden)!.SetValue(gm, Gm);
        GameClient client = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
        typeof(GameClient).GetField("<ClientState>k__BackingField", Hidden)!
            .SetValue(client, GameClient.eClientState.Playing);
        typeof(GameClient).GetField("<Player>k__BackingField", Hidden)!.SetValue(client, gm);
        client.Out = DispatchProxy.Create<IPacketLib, ChatRecorder>();
        chat = (ChatRecorder)client.Out;
        return client;
    }

    [Test]
    public void TheCommandSendsItsReplyAsItIsOrTheSyntaxLinesToTheGm()
    {
        // 2.4: the handler sends the reply's lines, with the GM's character as by=, or the attribute's syntax lines
        // for arguments it refuses. The syntax lines go through LanguageMgr, which gives an untranslated line back as
        // it is: the pet tests' language scope gives it no language files.
        using var language = new DOL.UnitTests.PetTestLanguageScope();
        GameClient client = GmClient(out ChatRecorder chat);
        BotCpuCommandHandler handler = new();

        handler.OnCommand(client, ["&botcpu", "on", "0"]);
        Assert.That(chat.Lines, Is.EqualTo(new[] { CommandDescription }.Concat(CommandUsage)));
        Assert.That(_queued, Is.Empty);

        chat.Lines.Clear();
        handler.OnCommand(client, ["&botcpu"]);
        Assert.That(chat.Lines, Is.EqualTo(new[] { OffStatus }));

        // A change, with the GM's character as by=. ScriptMgr puts the registered name first whatever was typed
        // (gameutils/ScriptMgr.cs:373), so a GM's /bot reaches the handler as this; that /bot resolves to /botcpu
        // (2.4) is BotCpuIsTheOnlyCommandNameThatStartsWithBot's.
        chat.Lines.Clear();
        handler.OnCommand(client, ["&botcpu", "on"]);
        Assert.That(chat.Lines,
            Is.EqualTo(new[] { "Bot CPU meter on until /botcpu off or the next start" + WhereTheLinesGo }));
        RunQueued();
        Assert.That(Logged(), Is.EqualTo(new[] { OnLine }));

        // Sent as it is: a bot's activity holds braces, which a format would take for its arguments.
        Population(1);
        GameBot aldric = Bot("Aldric");
        typeof(GameBot).GetField("<PersistentRecord>k__BackingField", Hidden)!.SetValue(aldric,
            new OfflineWorldBotRecord { ObjectiveKind = "RvR", Activity = "Holding {0} at {the bridge}" });
        ChargeBot(aldric, 90, 1_800);
        Seconds(60);
        chat.Lines.Clear();
        handler.OnCommand(client, ["&botcpu", "top"]);
        Assert.That(chat.Lines, Is.EqualTo(new[]
            { "1. Aldric (34 Cleric, region 0): 1.500 ms/s, 30 calls/s, RvR, \"Holding {0} at {the bridge}\"" }));
        GC.KeepAlive(aldric);
    }
}
