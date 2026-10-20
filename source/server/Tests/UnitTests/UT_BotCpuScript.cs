using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using DOL.GS.HearthDAoC;
using DOL.GS.ServerProperties;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter's switch (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 4.1, 6.1).
// BotCpuScript holds the bot_cpu_meter server property, which HEARTHDAOC_BOT_CPU sets at every start (4.2) and the
// server reads once, at start, to turn the meter on with it. Off in code, as the fork's other switches are. It also
// reads the process values of the BOT_CPU_PROCESS line (3.9), tested here as parsers of sample text and on sample
// files, and appends the CSV (2.3), tested in a folder of each test's own.
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

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "bot-cpu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _procSelf = ProcSelfHook.GetValue(null);
        _cgroupCpuStat = CgroupCpuStatHook.GetValue(null);
        _logs.Clear();
        LogHook.SetValue(null, (Action<ELogLevel, string>)((level, line) => _logs.Add((level, line))));
    }

    [TearDown]
    public void TearDown()
    {
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
        Assert.That(process.AllocMBSec, Is.EqualTo(0));
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
}
