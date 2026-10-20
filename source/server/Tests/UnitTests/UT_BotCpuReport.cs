using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DOL.GS.HearthDAoC;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the bot CPU meter's report (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 6.1). Once a
// minute the report takes the difference of two snapshots of the per-thread slots (3.7) and turns it, with the window's
// exact length, the game time and the registry samples, into each kind's figures (2.1, 2.2, 3.8), the spread and the
// top 5 of each bot kind from the per-bot counters (3.10), the compare pairs (2.1) and the totals since on (2.4), then
// writes them: the log lines (2.1), the CSV rows (2.3) and the chat of /botcpu (2.4), in the invariant culture.
// BotCpuReport works on plain values only, with no server state, so these tests drive it directly.
[TestFixture]
public sealed class UT_BotCpuReport
{
    // Stopwatch.Frequency on Linux (1 ns ticks) and on Windows (100 ns ticks).
    private const long Linux = 1_000_000_000;
    private const long Windows = 10_000_000;

    // GameLoop.TickDuration at the server's 30 ticks a second: a 60 s window at 30.0 tps is 1,800 ticks (3.8).
    private const double TickMs = 1000.0 / 30;
    private const long WholeWindow = 1_800;

    private const double Tolerance = 1e-9;

    private const BotCpuKind Gamebot = BotCpuKind.Gamebot;
    private const BotCpuKind Companion = BotCpuKind.Companion;
    private const BotCpuKind Playerbot = BotCpuKind.Playerbot;
    private const BotCpuKind Piloted = BotCpuKind.Piloted;
    private const BotCpuKind Player = BotCpuKind.Player;
    private const BotCpuKind Monster = BotCpuKind.Monster;
    private const BotCpuKind Unattributed = BotCpuKind.Unattributed;

    // The Stopwatch ticks of ms milliseconds.
    private static long Ticks(double ms, long frequency = Linux) => (long)Math.Round(ms * frequency / 1000);

    // As a window's difference holds them: ms of time, calls and bytes charged to [kind, stage].
    private static void Charge(BotCpuTotals counts, BotCpuKind kind, BotCpuStage stage, double ms, long calls,
        long bytes = 0, long frequency = Linux) => counts.Add(kind, stage, Ticks(ms, frequency), calls, bytes);

    // The registry samples of a window, one a second (3.8).
    private static BotCpuSamples Samples(params int[] samples) =>
        samples.Aggregate(new BotCpuSamples(), (sum, sample) => sum.Add(sample));

    // A window as the report closes it, with game time grown by the window's length unless given.
    private static BotCpuWindow Close(BotCpuTotals counts, double seconds = 60, long frequency = Linux,
        BotCpuSamples gamebots = default, IEnumerable<BotCpuBot> bots = null, long? gameTimeChange = null) =>
        BotCpuReport.Window(counts, seconds, frequency, gameTimeChange ?? (long)Math.Round(seconds * 1000), TickMs,
            gamebots, bots ?? []);

    // A bot as the report lists it (3.10): its ticks and calls in a 60 s window, worth msSec ms per s.
    private static BotCpuBot Bot(BotCpuKind kind, string name, double msSec, long calls = WholeWindow) =>
        new(kind, name, 34, "Cleric", 163, "RvR", "Hunting", Ticks(msSec * 60), calls);

    // A spread computed from the counts: its three values, within the tolerance.
    private static void AssertSpread(BotCpuSpread? spread, double p50, double p95, double max)
    {
        Assert.That(spread, Is.Not.Null);
        Assert.That(spread.Value.P50, Is.EqualTo(p50).Within(Tolerance));
        Assert.That(spread.Value.P95, Is.EqualTo(p95).Within(Tolerance));
        Assert.That(spread.Value.Max, Is.EqualTo(max).Within(Tolerance));
    }

    // ---- The snapshot difference (3.7, a window's steps 1-4)

    private static long[] Slot() => new long[BotCpuTotals.SlotLength];

    // As the wrapper adds to its thread's slot (3.2, step 5).
    private static void Add(long[] slot, BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)
    {
        int cell = BotCpuTotals.CellIndex(kind, stage);
        slot[cell] += ticks;
        slot[cell + 1] += calls;
        slot[cell + 2] += bytes;
    }

    // Every value of the totals that is not zero, by kind, stage and value.
    private static Dictionary<string, long> NonZero(BotCpuTotals totals)
    {
        Dictionary<string, long> values = [];
        void Put(string name, long value)
        {
            if (value != 0)
                values[name] = value;
        }

        foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
        {
            foreach (BotCpuStage stage in Enum.GetValues<BotCpuStage>())
            {
                Put($"{kind}/{stage}/ticks", totals.Ticks(kind, stage));
                Put($"{kind}/{stage}/calls", totals.Calls(kind, stage));
                Put($"{kind}/{stage}/bytes", totals.Bytes(kind, stage));
            }

            Put($"{kind}/presence", totals.Presence(kind));
        }

        return values;
    }

    [Test]
    public void TheWindowHoldsOnlyTheAddsBetweenTwoSnapshots()
    {
        long[] one = Slot();
        long[] two = Slot();
        Add(one, Gamebot, BotCpuStage.Npc, 1_000, 2, 64);
        Add(two, Player, BotCpuStage.ClientEnd, 500, 1, 0);
        two[BotCpuTotals.PresenceIndex(Player)] += 3;
        BotCpuTotals first = BotCpuTotals.Sum([one, two]);

        Add(one, Gamebot, BotCpuStage.Npc, 400, 1, 32);
        Add(two, Monster, BotCpuStage.Npc, 70, 1, 0);
        two[BotCpuTotals.PresenceIndex(Player)] += 2;
        BotCpuTotals second = BotCpuTotals.Sum([one, two]);

        Assert.That(NonZero(BotCpuReport.Difference(second, first)), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["Gamebot/Npc/ticks"] = 400, ["Gamebot/Npc/calls"] = 1, ["Gamebot/Npc/bytes"] = 32,
            ["Monster/Npc/ticks"] = 70, ["Monster/Npc/calls"] = 1,
            ["Player/presence"] = 2,
        }));
    }

    [Test]
    public void ASlotThatAppearsBetweenSnapshotsCountsFromZero()
    {
        long[] one = Slot();
        Add(one, Gamebot, BotCpuStage.Npc, 1_000, 2, 64);
        BotCpuTotals first = BotCpuTotals.Sum([one]);

        // A worker the watchdog restarted, or a thread's first metered item: a new slot, in the list from then on.
        long[] restarted = Slot();
        Add(restarted, Companion, BotCpuStage.Attack, 300, 3, 96);
        restarted[BotCpuTotals.PresenceIndex(Companion)] += 4;
        Add(one, Gamebot, BotCpuStage.Npc, 10, 1, 0);
        BotCpuTotals second = BotCpuTotals.Sum([one, restarted]);

        Assert.That(NonZero(BotCpuReport.Difference(second, first)), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["Gamebot/Npc/ticks"] = 10, ["Gamebot/Npc/calls"] = 1,
            ["Companion/Attack/ticks"] = 300, ["Companion/Attack/calls"] = 3, ["Companion/Attack/bytes"] = 96,
            ["Companion/presence"] = 4,
        }));
    }

    [Test]
    public void ANegativeDifferenceCountsAsZero()
    {
        long[] one = Slot();
        long[] two = Slot();
        Add(one, Gamebot, BotCpuStage.Npc, 1_000, 2, 64);
        Add(two, Player, BotCpuStage.ClientEnd, 500, 1, 8);
        two[BotCpuTotals.PresenceIndex(Player)] += 3;
        BotCpuTotals first = BotCpuTotals.Sum([one, two]);

        // Totals that went down (a slot gone from the list, which the meter never does): 0, never negative.
        Add(one, Gamebot, BotCpuStage.Npc, 40, 1, 0);
        BotCpuTotals second = BotCpuTotals.Sum([one]);

        Assert.That(NonZero(BotCpuReport.Difference(second, first)), Is.EquivalentTo(new Dictionary<string, long>
        {
            ["Gamebot/Npc/ticks"] = 40, ["Gamebot/Npc/calls"] = 1,
        }));
    }

    [Test]
    public void ReadingNeverChangesASlot()
    {
        long[] one = Slot();
        long[] two = Slot();
        Add(one, Gamebot, BotCpuStage.Fleet, 1_000, 1, 64);
        Add(two, Monster, BotCpuStage.Npc, 500, 7, 8);
        two[BotCpuTotals.PresenceIndex(Monster)] += 7;
        long[] oneBefore = (long[])one.Clone();
        long[] twoBefore = (long[])two.Clone();

        BotCpuTotals first = BotCpuTotals.Sum([one, two]);
        BotCpuTotals second = BotCpuTotals.Sum([one, two]);
        BotCpuTotals window = BotCpuReport.Difference(second, first);

        Assert.That(one, Is.EqualTo(oneBefore));
        Assert.That(two, Is.EqualTo(twoBefore));
        Assert.That(NonZero(window), Is.Empty);
        Assert.That(NonZero(first), Is.EquivalentTo(NonZero(second)));
    }

    // ---- The arithmetic (2.1, 3.8)

    [TestCase(Linux)]
    [TestCase(Windows)]
    public void EachKindsFiguresComeFromItsRawCounts(long frequency)
    {
        BotCpuTotals counts = new();
        // 240 GameBots by the registry, 14,400 ms in 60 s: 1.0 ms per bot per s, the Fleet scope included.
        Charge(counts, Gamebot, BotCpuStage.Npc, 12_000, 1_200_000, 40_000_000, frequency);
        Charge(counts, Gamebot, BotCpuStage.Fleet, 2_400, 240_000, 4_236_800, frequency);
        // One player playing the whole window: 1,800 presences in 1,800 ticks.
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientEnd, 60, 20_000, 3_000_000, frequency);
        Charge(counts, Player, BotCpuStage.Attack, 30, 2_000, 686_400, frequency);
        Charge(counts, Player, BotCpuStage.Pets, 30, 2_000, 0, frequency);
        // 1,830 live monster brains every tick.
        counts.AddPresence(Monster, 1_830 * WholeWindow);
        Charge(counts, Monster, BotCpuStage.Npc, 5_760, 6_588_000, 2_635_776, frequency);
        // Ownerless timers.
        Charge(counts, Unattributed, BotCpuStage.Timer, 72.6, 132_600, 5_406_720, frequency);

        BotCpuWindow window = Close(counts, frequency: frequency, gamebots: Samples(236, 240, 244));

        Assert.That(window.Seconds, Is.EqualTo(60));
        Assert.That(window.Win, Is.EqualTo(60));
        Assert.That(window.GameTicks, Is.EqualTo(1_800).Within(Tolerance));
        Assert.That(window.Tps, Is.EqualTo(30.0).Within(Tolerance));
        Assert.That(window.CallsSec, Is.EqualTo((1_440_000 + 24_000 + 6_588_000 + 132_600) / 60.0).Within(Tolerance));
        // All time charged to all kinds, unattributed included: 20,352.6 ms in 60 s, as % of one core.
        Assert.That(window.ChargedPct, Is.EqualTo(20_352.6 / 60 / 10).Within(Tolerance));
        Assert.That(window.Kinds.Select(kind => kind.Kind),
            Is.EqualTo(new[] { Gamebot, Player, Monster, Unattributed }));

        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        Assert.That(gamebot.N, Is.EqualTo(240).Within(Tolerance));
        Assert.That(gamebot.BotSeconds, Is.EqualTo(240 * 60).Within(Tolerance));
        Assert.That(gamebot.ChargedMs, Is.EqualTo(14_400).Within(Tolerance));
        Assert.That(gamebot.MsSec, Is.EqualTo(240).Within(Tolerance));
        Assert.That(gamebot.MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(gamebot.CallsPerBotSec, Is.EqualTo(100.0).Within(Tolerance));
        Assert.That(gamebot.UsPerCall, Is.EqualTo(10.0).Within(Tolerance));
        Assert.That(gamebot.AllocKBPerBotSec, Is.EqualTo(3.0).Within(Tolerance));
        Assert.That(gamebot.CallsSec, Is.EqualTo(24_000).Within(Tolerance));
        Assert.That(gamebot.AllocKBSec, Is.EqualTo(720).Within(Tolerance));

        BotCpuKindWindow player = window.Kind(Player);
        Assert.That(player.N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(player.BotSeconds, Is.EqualTo(60).Within(Tolerance));
        Assert.That(player.MsSec, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(player.MsPerBotSec, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(player.CallsPerBotSec, Is.EqualTo(400.0).Within(Tolerance));
        Assert.That(player.UsPerCall, Is.EqualTo(5.0).Within(Tolerance));
        Assert.That(player.AllocKBPerBotSec, Is.EqualTo(60.0).Within(Tolerance));

        BotCpuKindWindow monster = window.Kind(Monster);
        Assert.That(monster.N, Is.EqualTo(1_830).Within(Tolerance));
        Assert.That(monster.MsSec, Is.EqualTo(96.0).Within(Tolerance));
        Assert.That(monster.MsPerBotSec, Is.EqualTo(5_760.0 / (1_830 * 60)).Within(Tolerance));
        Assert.That(monster.CallsPerBotSec, Is.EqualTo(60.0).Within(Tolerance));
        Assert.That(monster.UsPerCall, Is.EqualTo(5_760_000.0 / 6_588_000).Within(Tolerance));
        Assert.That(monster.AllocKBPerBotSec, Is.EqualTo(2_574.0 / (1_830 * 60)).Within(Tolerance));

        // Unattributed has no n: calls and allocation per second instead of per bot (2.1).
        BotCpuKindWindow unattributed = window.Kind(Unattributed);
        Assert.That(unattributed.N, Is.Null);
        Assert.That(unattributed.BotSeconds, Is.EqualTo(0));
        Assert.That(unattributed.MsSec, Is.EqualTo(1.21).Within(Tolerance));
        Assert.That(unattributed.CallsSec, Is.EqualTo(2_210.0).Within(Tolerance));
        Assert.That(unattributed.AllocKBSec, Is.EqualTo(88.0).Within(Tolerance));
        Assert.That(unattributed.UsPerCall, Is.EqualTo(72_600.0 / 132_600).Within(Tolerance));
        Assert.That(unattributed.MsPerBotSec, Is.Null);
        Assert.That(unattributed.CallsPerBotSec, Is.Null);
        Assert.That(unattributed.AllocKBPerBotSec, Is.Null);
    }

    [Test]
    public void OneMsPerSecondFor240BotsIs24PercentOfOneCore()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 14_400, 432_000);

        BotCpuWindow window = Close(counts, gamebots: Samples(240));

        Assert.That(window.Kind(Gamebot).MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).MsSec, Is.EqualTo(240).Within(Tolerance));
        Assert.That(window.ChargedPct, Is.EqualTo(24.0).Within(Tolerance));
    }

    [Test]
    public void PresenceGivesNOverTheWindowsGameTicks()
    {
        BotCpuTotals counts = new();
        counts.AddPresence(Companion, 2 * 900); // two companions, each half the window
        counts.AddPresence(Playerbot, 900);
        counts.AddPresence(Piloted, 2 * WholeWindow);
        foreach (BotCpuKind kind in new[] { Companion, Playerbot, Piloted })
            Charge(counts, kind, BotCpuStage.Npc, 6, 1_800);

        BotCpuWindow window = Close(counts);
        Assert.That(window.Kind(Companion).N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Playerbot).N, Is.EqualTo(0.5).Within(Tolerance));
        Assert.That(window.Kind(Piloted).N, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(window.Kind(Piloted).MsPerBotSec, Is.EqualTo(6.0 / (2 * 60)).Within(Tolerance));

        // A loop running late: game time grew by 30 s in a 60 s window, 900 ticks, 15.0 tps. n divides by the ticks.
        BotCpuWindow late = Close(counts, gameTimeChange: 30_000);
        Assert.That(late.GameTicks, Is.EqualTo(900).Within(Tolerance));
        Assert.That(late.Tps, Is.EqualTo(15.0).Within(Tolerance));
        Assert.That(late.Kind(Companion).N, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(late.Kind(Companion).BotSeconds, Is.EqualTo(120).Within(Tolerance));
    }

    [Test]
    public void AStagesValueIsPerBotOnTheLineAndForTheWholeKindInTheCsv()
    {
        BotCpuTotals counts = new();
        counts.AddPresence(Player, 2 * WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientBegin, 18, 3_600);
        Charge(counts, Player, BotCpuStage.ClientEnd, 157.2, 3_600);
        Charge(counts, Player, BotCpuStage.Pets, 0.0006, 1);
        Charge(counts, Unattributed, BotCpuStage.Npc, 413.4, 60_000);
        Charge(counts, Unattributed, BotCpuStage.Zone, 35.88, 6_000);

        BotCpuWindow window = Close(counts);
        BotCpuKindWindow player = window.Kind(Player);
        // The line's stage values: ms per bot per s, adding up to msPerBotSec (2.1).
        Assert.That(player.StageValues[(int)BotCpuStage.ClientBegin], Is.EqualTo(0.15).Within(Tolerance));
        Assert.That(player.StageValues[(int)BotCpuStage.ClientEnd], Is.EqualTo(1.31).Within(Tolerance));
        Assert.That(player.StageValues[(int)BotCpuStage.Timer], Is.EqualTo(0));
        Assert.That(player.StageValues.Sum(), Is.EqualTo(player.MsPerBotSec).Within(Tolerance));
        // The CSV's stage columns: the whole kind's raw stage ticks ÷ the exact window length, ms per s (2.3).
        Assert.That(player.StageMsSec[(int)BotCpuStage.ClientBegin], Is.EqualTo(0.3).Within(Tolerance));
        Assert.That(player.StageMsSec[(int)BotCpuStage.ClientEnd], Is.EqualTo(2.62).Within(Tolerance));
        Assert.That(player.StageMsSec[(int)BotCpuStage.Pets], Is.EqualTo(0.00001).Within(Tolerance));
        Assert.That(player.StageMsSec.Sum(), Is.EqualTo(player.MsSec).Within(Tolerance));
        Assert.That(player.StageMsSec, Has.Count.EqualTo(BotCpuTotals.StageCount));

        // Unattributed's line has its stage values per s, the same as its CSV columns, adding up to msSec.
        BotCpuKindWindow unattributed = window.Kind(Unattributed);
        Assert.That(unattributed.StageValues[(int)BotCpuStage.Npc], Is.EqualTo(6.89).Within(Tolerance));
        Assert.That(unattributed.StageValues, Is.EqualTo(unattributed.StageMsSec));
        Assert.That(unattributed.StageValues.Sum(), Is.EqualTo(unattributed.MsSec).Within(Tolerance));
    }

    [Test]
    public void AKindWithNothingChargedAndNoNHasNoLine()
    {
        BotCpuTotals counts = new();
        Charge(counts, Player, BotCpuStage.Timer, 1, 30);
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Unattributed, BotCpuStage.Timer, 1, 30);

        // GameBots in the registry but none charged still have a line; companion, playerbot, piloted and monster none.
        BotCpuWindow window = Close(counts, gamebots: Samples(12));
        Assert.That(window.Kinds.Select(kind => kind.Kind), Is.EqualTo(new[] { Gamebot, Player, Unattributed }));
        foreach (BotCpuKind kind in new[] { Companion, Playerbot, Piloted, Monster })
            Assert.That(window.Kind(kind), Is.Null, kind.ToString());

        Assert.That(Close(new BotCpuTotals()).Kinds, Is.Empty);
    }

    // ---- Nothing to divide by (2.1)

    [Test]
    public void ChargedTimeWithNoBotSecondsGivesNoPerBotFieldAndNoStageValues()
    {
        BotCpuTotals counts = new();
        // A player bot whose client is not registered in ClientService: charged, never present (2.5, section 9).
        Charge(counts, Playerbot, BotCpuStage.Timer, 30, 900, 2_048);
        // GameBots charged before the registry ever gave a sample.
        Charge(counts, Gamebot, BotCpuStage.Npc, 60, 1_800);

        BotCpuWindow window = Close(counts, bots: [Bot(Playerbot, "Proto", 0.5, 900)]);

        foreach (BotCpuKind kind in new[] { Playerbot, Gamebot })
        {
            BotCpuKindWindow figures = window.Kind(kind);
            Assert.That(figures.N, Is.EqualTo(0), kind.ToString());
            Assert.That(figures.BotSeconds, Is.EqualTo(0), kind.ToString());
            Assert.That(figures.MsPerBotSec, Is.Null, kind.ToString());
            Assert.That(figures.CallsPerBotSec, Is.Null, kind.ToString());
            Assert.That(figures.AllocKBPerBotSec, Is.Null, kind.ToString());
            Assert.That(figures.P50, Is.Null, kind.ToString());
            Assert.That(figures.P95, Is.Null, kind.ToString());
            Assert.That(figures.Max, Is.Null, kind.ToString());
            Assert.That(figures.StageValues, Is.Null, kind.ToString());
        }

        // msSec still shows the cost, usPerCall has its calls, and the CSV keeps the stage totals.
        BotCpuKindWindow playerbot = window.Kind(Playerbot);
        Assert.That(playerbot.MsSec, Is.EqualTo(0.5).Within(Tolerance));
        Assert.That(playerbot.UsPerCall, Is.EqualTo(30_000.0 / 900).Within(Tolerance));
        Assert.That(playerbot.StageMsSec[(int)BotCpuStage.Timer], Is.EqualTo(0.5).Within(Tolerance));
        // Its bot was charged: a spread and a top line, though the line's p50, p95 and max are - (2.1).
        AssertSpread(playerbot.Spread, 0.5, 0.5, 0.5);
        Assert.That(window.Top(Playerbot).Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Proto" }));
    }

    [Test]
    public void NoCallsGiveNoUsPerCall()
    {
        // 240 GameBots in the registry and nothing charged: a line of zeros, but no cost per call.
        BotCpuWindow window = Close(new BotCpuTotals(), gamebots: Samples(240));

        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        Assert.That(gamebot.UsPerCall, Is.Null);
        Assert.That(gamebot.MsSec, Is.EqualTo(0));
        Assert.That(gamebot.MsPerBotSec, Is.EqualTo(0));
        Assert.That(gamebot.CallsPerBotSec, Is.EqualTo(0));
        Assert.That(gamebot.AllocKBPerBotSec, Is.EqualTo(0));
        // No bot charged: no spread.
        Assert.That(gamebot.Spread, Is.Null);
        Assert.That(gamebot.P50, Is.Null);
        Assert.That(window.Top(Gamebot), Is.Empty);
    }

    // Every number in the window and everything it holds, through its public properties and the top 5 of each kind.
    private static IEnumerable<double> Numbers(object value)
    {
        switch (value)
        {
            case null or string:
                yield break;
            case double number:
                yield return number;
                yield break;
            case IEnumerable list:
                foreach (object item in list)
                {
                    foreach (double number in Numbers(item))
                        yield return number;
                }

                yield break;
        }

        Type type = value.GetType();
        if (type.Namespace != typeof(BotCpuReport).Namespace)
            yield break;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            foreach (double number in Numbers(property.GetValue(value)))
                yield return number;
        }

        if (value is BotCpuWindow window)
        {
            foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
            {
                foreach (double number in Numbers(window.Top(kind)))
                    yield return number;
            }
        }
    }

    [Test]
    public void NoFigureIsNaNOrInfinity()
    {
        BotCpuTotals counts = new();
        foreach (BotCpuKind kind in Enum.GetValues<BotCpuKind>())
        {
            counts.AddPresence(kind, 5);
            Charge(counts, kind, BotCpuStage.Npc, 3, 0); // time with no calls, which no wrapper adds
            counts.Add(kind, BotCpuStage.Zone, 0, 4, 100); // calls with no time
        }

        BotCpuBot[] bots = [Bot(Gamebot, "Idle", 0, 0), Bot(Companion, "Zero", 0), Bot(Playerbot, "Proto", 2)];
        BotCpuWindow[] windows =
        [
            Close(new BotCpuTotals(), seconds: BotCpuReport.MinimumWindowSeconds),
            Close(counts, bots: bots),
            Close(counts, gamebots: Samples(0), bots: bots, gameTimeChange: 0), // game time stood still
            Close(counts, seconds: 1e6, frequency: Windows, gamebots: Samples(3), bots: bots, gameTimeChange: -5),
        ];

        foreach (BotCpuWindow window in windows)
        {
            double[] numbers = Numbers(window).ToArray();
            Assert.That(numbers, Is.Not.Empty);
            Assert.That(numbers.Where(number => !double.IsFinite(number)), Is.Empty);
        }

        Assert.That(windows[2].Kind(Monster).N, Is.EqualTo(0));
        Assert.That(windows[2].Tps, Is.EqualTo(0));
    }

    // ---- The window's length (2.2)

    [Test]
    public void AWindowOf61_4SecondsDividesBy61_4AndIsWin61()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 240 * 61.4, 61_400);
        counts.AddPresence(Player, 1_842);
        Charge(counts, Player, BotCpuStage.ClientEnd, 61.4, 614);

        BotCpuWindow window = Close(counts, seconds: 61.4, gamebots: Samples(240));

        Assert.That(window.Seconds, Is.EqualTo(61.4));
        Assert.That(window.Win, Is.EqualTo(61));
        Assert.That(window.GameTicks, Is.EqualTo(1_842).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).MsSec, Is.EqualTo(240).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Gamebot).CallsSec, Is.EqualTo(1_000).Within(Tolerance));
        Assert.That(window.Kind(Player).N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Kind(Player).MsPerBotSec, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(Close(counts, seconds: 59.6).Win, Is.EqualTo(60));
    }

    [TestCase(0.4)]
    [TestCase(4.99)]
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(double.NaN)]
    public void AFinalWindowUnder5SecondsWritesNothing(double seconds)
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 3, 30);

        Assert.That(Close(counts, seconds: seconds, gamebots: Samples(240)), Is.Null);
    }

    [Test]
    public void AFinalWindowOf5SecondsIsWritten()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 3, 30);

        BotCpuWindow window = Close(counts, seconds: 5, gamebots: Samples(240));
        Assert.That(window.Win, Is.EqualTo(5));
        Assert.That(window.Kind(Gamebot).MsSec, Is.EqualTo(0.6).Within(Tolerance));
    }

    [Test]
    public void AWindowWithNoRegistrySampleUsesTheLastOne()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 4_800, 48_000);

        // The previous window's samples were 238 then 240; this one has none.
        BotCpuSamples none = Samples(238, 240).NextWindow();
        Assert.That(none.Count, Is.EqualTo(0));

        BotCpuKindWindow gamebot = Close(counts, seconds: 5, gamebots: none).Kind(Gamebot);
        Assert.That(gamebot.N, Is.EqualTo(240));
        Assert.That(gamebot.MsPerBotSec, Is.EqualTo(4_800.0 / (240 * 5)).Within(Tolerance));

        // The next window's samples count alone, and with no sample ever n is 0.
        Assert.That(none.Add(100).Add(102).Average, Is.EqualTo(101));
        Assert.That(new BotCpuSamples().Average, Is.EqualTo(0));
    }

    // ---- Since on (2.4)

    // A first window of 61.4 s (win=61): 240 GameBots at 1.0 ms per bot per s and one player at 2.0. Then 60 s: 200
    // GameBots at 2.0 and no player.
    private static (BotCpuWindow First, BotCpuWindow Second) TwoWindows()
    {
        BotCpuTotals first = new();
        Charge(first, Gamebot, BotCpuStage.Npc, 14_736, 432_000);
        first.AddPresence(Player, 1_842);
        Charge(first, Player, BotCpuStage.ClientEnd, 122.8, 3_600);
        Charge(first, Unattributed, BotCpuStage.Timer, 600, 3_600);

        BotCpuTotals second = new();
        Charge(second, Gamebot, BotCpuStage.Npc, 24_000, 360_000);
        Charge(second, Unattributed, BotCpuStage.Timer, 600, 3_600);

        return (Close(first, seconds: 61.4, gamebots: Samples(240)), Close(second, gamebots: Samples(200)));
    }

    [Test]
    public void SinceOnIsTheChargedMsOverTheBotSecondsOfTheWindowsWritten()
    {
        (BotCpuWindow first, BotCpuWindow second) = TwoWindows();

        BotCpuSince since = BotCpuSince.Start(reset: false).Add(first).Add(second);

        Assert.That(since.Reset, Is.False);
        Assert.That(since.Windows, Is.EqualTo(2));
        // The windows' exact lengths, not their wins (61 + 60).
        Assert.That(first.Win, Is.EqualTo(61));
        Assert.That(since.Seconds, Is.EqualTo(121.4).Within(Tolerance));
        Assert.That(since.Minutes, Is.EqualTo(121.4 / 60).Within(Tolerance));
        Assert.That(since.ChargedMs(Gamebot), Is.EqualTo(38_736).Within(Tolerance));
        Assert.That(since.BotSeconds(Gamebot), Is.EqualTo(240 * 61.4 + 200 * 60).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Gamebot), Is.EqualTo(38_736.0 / 26_736).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Player), Is.EqualTo(2.0).Within(Tolerance));
        // Only kinds with bot-seconds: never unattributed, nor a kind that was never present.
        Assert.That(since.MsPerBotSec(Unattributed), Is.Null);
        Assert.That(since.MsPerBotSec(Companion), Is.Null);
    }

    [Test]
    public void AfterResetOnlyTheLaterWindowsCount()
    {
        (BotCpuWindow _, BotCpuWindow second) = TwoWindows();

        BotCpuSince since = BotCpuSince.Start(reset: true).Add(second);

        Assert.That(since.Reset, Is.True);
        Assert.That(since.Windows, Is.EqualTo(1));
        Assert.That(since.Minutes, Is.EqualTo(1).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Gamebot), Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(since.MsPerBotSec(Player), Is.Null);
    }

    [Test]
    public void ThePartialWindowIsNotCounted()
    {
        (BotCpuWindow first, BotCpuWindow second) = TwoWindows();
        BotCpuSince start = BotCpuSince.Start(reset: false);
        Assert.That(start.Windows, Is.EqualTo(0));
        Assert.That(start.MsPerBotSec(Gamebot), Is.Null);

        BotCpuSince since = start.Add(first).Add(second);
        // The open window counts only once it is written; a final window under 5 s never is (its counts are dropped).
        BotCpuTotals open = new();
        Charge(open, Gamebot, BotCpuStage.Npc, 9_999, 99);
        BotCpuWindow tooShort = Close(open, seconds: 3, gamebots: Samples(240));
        Assert.That(tooShort, Is.Null);

        BotCpuSince after = since.Add(tooShort);
        Assert.That(after.Windows, Is.EqualTo(2));
        Assert.That(after.Seconds, Is.EqualTo(121.4).Within(Tolerance));
        Assert.That(after.MsPerBotSec(Gamebot), Is.EqualTo(38_736.0 / 26_736).Within(Tolerance));
        // Adding never changes the totals it started from.
        Assert.That(start.Windows, Is.EqualTo(0));
        Assert.That(start.ChargedMs(Gamebot), Is.EqualTo(0));
    }

    // ---- The spread (3.10)

    [Test]
    public void TheSpreadOfOneToTwentyIsP50TenP95NineteenMaxTwenty()
    {
        double[] values = Enumerable.Range(1, 20).Select(value => (double)value).Reverse().ToArray();

        Assert.That(BotCpuReport.Spread(values), Is.EqualTo(new BotCpuSpread(10, 19, 20)));
    }

    [Test]
    public void OneBotGivesAllThreeEqual() =>
        Assert.That(BotCpuReport.Spread([4.25]), Is.EqualTo(new BotCpuSpread(4.25, 4.25, 4.25)));

    [Test]
    public void NoBotGivesNoSpread()
    {
        Assert.That(BotCpuReport.Spread([]), Is.Null);
        Assert.That(BotCpuReport.Spread(null), Is.Null);
    }

    // ---- The top 5 (3.10)

    [Test]
    public void TheTopFiveAreRankedByMsThenByName()
    {
        BotCpuBot aldric = Bot(Gamebot, "Aldric", 7.412, 52_800);
        BotCpuBot[] bots =
        [
            Bot(Gamebot, "Eirik", 4.950), Bot(Gamebot, "Fenn", 1.0), Bot(Gamebot, "Cathal", 6.0204), aldric,
            Bot(Gamebot, "Gorm", 0, 0), Bot(Gamebot, "Brennos", 6.0196), Bot(Gamebot, "Dagny", 5.104),
            Bot(Gamebot, "Halvard", 0, 0), Bot(Companion, "Hilda", 9.0),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 60_000, 600_000);
        Charge(counts, Companion, BotCpuStage.Npc, 540, 1_800);
        counts.AddPresence(Companion, WholeWindow);

        BotCpuWindow window = Close(counts, gamebots: Samples(240), bots: bots);

        IReadOnlyList<BotCpuTopEntry> top = window.Top(Gamebot);
        // Brennos and Cathal are both 6.020 at the line's 3 decimals: a tie, so by name, though Cathal's ms is higher.
        Assert.That(top.Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Aldric", "Brennos", "Cathal", "Dagny", "Eirik" }));
        Assert.That(top.Select(entry => entry.Rank), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(top[0].Bot, Is.SameAs(aldric));
        Assert.That(top[0].MsSec, Is.EqualTo(7.412).Within(Tolerance));
        Assert.That(top[0].CallsSec, Is.EqualTo(880).Within(Tolerance));
        Assert.That(top[2].MsSec, Is.EqualTo(6.0204).Within(Tolerance));

        // The spread is over the six GameBots charged in the window, nearest rank: p50 at rank 3 of 6, p95 and max at
        // rank 6. Gorm and Halvard, listed with no call in it, are left out: counted, p50 would be 4.950, rank 4 of 8.
        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        AssertSpread(gamebot.Spread, 5.104, 7.412, 7.412);
        Assert.That(gamebot.P50, Is.EqualTo(5.104).Within(Tolerance));
        Assert.That(gamebot.P95, Is.EqualTo(7.412).Within(Tolerance));
        Assert.That(gamebot.Max, Is.EqualTo(7.412).Within(Tolerance));

        Assert.That(window.Top(Companion).Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Hilda" }));
        Assert.That(window.Kind(Companion).Max, Is.EqualTo(9.0).Within(Tolerance));
    }

    [Test]
    public void FewerThanFiveBotsGiveFewerEntries()
    {
        // A player charged in the window, listed with the bots: the per-bot table only holds bot kinds (3.10), so the
        // report leaves out any other kind it is given.
        BotCpuBot[] bots = [Bot(Companion, "Hilda", 2.0), Bot(Player, "Gm", 4.0), Bot(Companion, "Ivar", 3.0)];
        BotCpuTotals counts = new();
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientEnd, 240, 1_800);

        BotCpuWindow window = Close(counts, bots: bots);

        Assert.That(window.Top(Companion).Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Ivar", "Hilda" }));
        Assert.That(window.Top(Gamebot), Is.Empty);
        Assert.That(window.Top(Playerbot), Is.Empty);
        Assert.That(window.Top(Piloted), Is.Empty);
        // Only the bot kinds have a top 5 and a spread.
        Assert.That(window.Top(Player), Is.Empty);
        Assert.That(window.Kind(Player).Spread, Is.Null);
        Assert.That(window.Kind(Player).P50, Is.Null);
        Assert.That(window.Top(Unattributed), Is.Empty);
    }

    [Test]
    public void ABotNotChargedInTheWindowIsLeftOutOfTheSpreadAndTheTop()
    {
        // Gorm and Jorun were charged in an earlier window, so the per-bot table still lists them, with no call in this
        // one. Ivar's one call took under one Stopwatch tick: charged at least once, at 0 ms per s (3.10).
        BotCpuBot[] bots =
        [
            Bot(Gamebot, "Gorm", 0, 0),
            Bot(Companion, "Hilda", 2.0), Bot(Companion, "Ivar", 0, 1), Bot(Companion, "Jorun", 0, 0),
            Bot(Companion, "Kari", 3.0),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Companion, BotCpuStage.Npc, 300, 3_601);
        counts.AddPresence(Companion, 4 * WholeWindow);

        BotCpuWindow window = Close(counts, gamebots: Samples(240), bots: bots);

        // No GameBot charged: no spread, so P50, P95 and Max are null though gamebot has bot-seconds, and no top, so no
        // BOT_CPU_TOP line for gamebot and "No gamebot was charged in the last minute." (2.1, 2.4).
        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        Assert.That(gamebot.BotSeconds, Is.EqualTo(240 * 60).Within(Tolerance));
        Assert.That(gamebot.Spread, Is.Null);
        Assert.That(gamebot.P50, Is.Null);
        Assert.That(gamebot.P95, Is.Null);
        Assert.That(gamebot.Max, Is.Null);
        Assert.That(window.Top(Gamebot), Is.Empty);

        // Companion: Kari, Hilda and Ivar, not Jorun. Over those three p50 is 2.000 at rank 2; with Jorun's 0 it would
        // be 0.000.
        IReadOnlyList<BotCpuTopEntry> top = window.Top(Companion);
        Assert.That(top.Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Kari", "Hilda", "Ivar" }));
        Assert.That(top[2].MsSec, Is.EqualTo(0));
        Assert.That(top[2].CallsSec, Is.EqualTo(1 / 60.0).Within(Tolerance));
        AssertSpread(window.Kind(Companion).Spread, 2.0, 3.0, 3.0);
    }

    [TestCase("Aldric", "Aldric")]
    [TestCase("Sir Aldric", "Sir_Aldric")]
    [TestCase("a:b c:", "a_b_c_")]
    [TestCase("", "-")]
    [TestCase(null, "-")]
    public void ASpaceOrColonInATopNameBecomesAnUnderscore(string name, string expected) =>
        Assert.That(BotCpuReport.TopName(name), Is.EqualTo(expected));

    [Test]
    public void ACountersWindowIsItsAddsSinceTheReportLastReadIt()
    {
        BotCpuCounter counter = new(Gamebot) { Ticks = 1_000, Calls = 20 };

        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((1_000L, 20L)));
        Assert.That((counter.LastTicks, counter.LastCalls), Is.EqualTo((1_000L, 20L)));
        // The loop threads' values are never written by the report.
        Assert.That((counter.Ticks, counter.Calls), Is.EqualTo((1_000L, 20L)));

        counter.Ticks += 300;
        counter.Calls += 3;
        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((300L, 3L)));
        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((0L, 0L)));

        // Values that went down, which the loop threads never make: 0, never negative.
        counter.LastTicks = 5_000;
        counter.LastCalls = 50;
        Assert.That(BotCpuReport.TakeCounterWindow(counter), Is.EqualTo((0L, 0L)));
    }

    // A bot as the report lists it from the per-bot table, with its window's ticks and calls.
    private static BotCpuBot Listed(BotCpuCounter counter, string name)
    {
        (long ticks, long calls) = BotCpuReport.TakeCounterWindow(counter);
        return new BotCpuBot(counter.Kind, name, 50, "Armsman", 163, null, null, ticks, calls);
    }

    [Test]
    public void ABotAbsentFromThisListingLeavesNoStaleEntry()
    {
        BotCpuCounter aldric = new(Gamebot);
        BotCpuCounter brennos = new(Gamebot);
        BotCpuCounter cathal = new(Gamebot);
        aldric.Ticks += Ticks(60);
        brennos.Ticks += Ticks(600);
        cathal.Ticks += Ticks(120);
        aldric.Calls += 1_800;
        brennos.Calls += 1_800;
        cathal.Calls += 1_800;

        BotCpuWindow first = Close(new BotCpuTotals(), gamebots: Samples(3),
            bots: [Listed(aldric, "Aldric"), Listed(brennos, "Brennos"), Listed(cathal, "Cathal")]);
        Assert.That(first.Top(Gamebot).Select(entry => entry.Bot.Name),
            Is.EqualTo(new[] { "Brennos", "Cathal", "Aldric" }));

        // Brennos runs on, then leaves the world: its counter is collected, or simply absent from the next listing.
        aldric.Ticks += Ticks(300);
        brennos.Ticks += Ticks(6_000);
        cathal.Ticks += Ticks(30);
        aldric.Calls += 1_800;
        brennos.Calls += 1_800;
        cathal.Calls += 1_800;

        BotCpuWindow second = Close(new BotCpuTotals(), gamebots: Samples(2),
            bots: [Listed(aldric, "Aldric"), Listed(cathal, "Cathal")]);

        // The window, the spread and the top 5 come from the bots present, with only their adds since the last read.
        IReadOnlyList<BotCpuTopEntry> top = second.Top(Gamebot);
        Assert.That(top.Select(entry => entry.Bot.Name), Is.EqualTo(new[] { "Aldric", "Cathal" }));
        Assert.That(top.Select(entry => entry.MsSec), Is.EqualTo(new[] { 5.0, 0.5 }).Within(Tolerance));
        AssertSpread(second.Kind(Gamebot).Spread, 0.5, 5.0, 5.0);
    }

    // ---- The compare pairs (2.1)

    // The kind lines of 2.1's sample, whose BOT_CPU_COMPARE line is cpu=1.14x alloc=1.94x calls=0.95x usPerCall=1.21x:
    // 240 GameBots at msPerBotSec 1.840, callsPerBotSec 412.3, usPerCall 4.46 and allocKBPerBotSec 31.2, and one player
    // at 2.104, 390.0, 5.39 and 60.4.
    private static BotCpuTotals SampleCounts()
    {
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 1.840 * 240 * 60, 5_937_120, 460_062_720);
        counts.AddPresence(Player, WholeWindow);
        Charge(counts, Player, BotCpuStage.ClientEnd, 2.104 * 60, 23_400, 3_710_976);
        return counts;
    }

    [Test]
    public void ACompareIsWrittenOnlyForTheThreePairsWithBothNAtLeastOne()
    {
        BotCpuTotals counts = SampleCounts();
        (BotCpuKind Kind, long Presence)[] others = [(Playerbot, 1_800), (Piloted, 1_620), (Companion, 5_400)];
        foreach ((BotCpuKind kind, long presence) in others)
        {
            counts.AddPresence(kind, presence);
            Charge(counts, kind, BotCpuStage.Timer, 60, 1_800);
        }

        BotCpuWindow window = Close(counts, gamebots: Samples(240));
        // playerbot n=1.0 and player n=1.0 with gamebot n=240.0; piloted n=0.9 is under 1.0; companion is no pair.
        Assert.That(window.Compares.Select(compare => compare.Kind), Is.EqualTo(new[] { Playerbot, Player }));
        Assert.That(window.Compares.Select(compare => compare.GamebotN), Is.All.EqualTo(240).Within(Tolerance));
        Assert.That(window.Compares[0].N, Is.EqualTo(1.0).Within(Tolerance));

        // n is compared as the line shows it, to 1 decimal: 0.96 is n=1.0.
        BotCpuTotals almost = SampleCounts();
        almost.AddPresence(Playerbot, 1_728);
        Charge(almost, Playerbot, BotCpuStage.Timer, 60, 1_800);
        Assert.That(Close(almost, gamebots: Samples(240)).Compares.Select(compare => compare.Kind),
            Is.EqualTo(new[] { Playerbot, Player }));

        // GameBots under 1.0: no pair at all.
        Assert.That(Close(SampleCounts(), gamebots: Samples(0, 1)).Compares, Is.Empty);
    }

    [Test]
    public void AllThreePairsAreWrittenInTheirOrder()
    {
        BotCpuTotals counts = SampleCounts();
        // One player bot, one piloted player and three companions, each present the whole window. Piloted is at twice
        // gamebot's msPerBotSec (3.680), callsPerBotSec (824.6) and allocKBPerBotSec (62.4), at usPerCall 4.46.
        counts.AddPresence(Playerbot, WholeWindow);
        Charge(counts, Playerbot, BotCpuStage.Timer, 60, 1_800);
        counts.AddPresence(Piloted, WholeWindow);
        Charge(counts, Piloted, BotCpuStage.ClientEnd, 3.680 * 60, 49_476, 3_833_856);
        counts.AddPresence(Companion, 3 * WholeWindow);
        Charge(counts, Companion, BotCpuStage.Npc, 60, 1_800);

        BotCpuWindow window = Close(counts, gamebots: Samples(240));

        // The three pairs in 2.1's order: playerbot/gamebot, piloted/gamebot, player/gamebot. Companion is no pair.
        Assert.That(window.Compares.Select(compare => compare.Kind), Is.EqualTo(new[] { Playerbot, Piloted, Player }));
        Assert.That(window.Compares.Select(compare => compare.N), Is.All.EqualTo(1.0).Within(Tolerance));
        Assert.That(window.Compares.Select(compare => compare.GamebotN), Is.All.EqualTo(240).Within(Tolerance));
        BotCpuCompare piloted = window.Compares[1];
        Assert.That(piloted.Cpu, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(piloted.Alloc, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(piloted.Calls, Is.EqualTo(2.0).Within(Tolerance));
        Assert.That(piloted.UsPerCall, Is.EqualTo(1.0).Within(Tolerance));
    }

    [Test]
    public void CompareRatiosAreOfTheKindLinesFigures()
    {
        BotCpuWindow window = Close(SampleCounts(), gamebots: Samples(240));
        BotCpuKindWindow gamebot = window.Kind(Gamebot);
        BotCpuKindWindow player = window.Kind(Player);
        Assert.That(BotCpuReport.Round(gamebot.MsPerBotSec.Value, 3), Is.EqualTo(1.840));
        Assert.That(BotCpuReport.Round(gamebot.CallsPerBotSec.Value, 1), Is.EqualTo(412.3));
        Assert.That(BotCpuReport.Round(gamebot.UsPerCall.Value, 2), Is.EqualTo(4.46));
        Assert.That(BotCpuReport.Round(gamebot.AllocKBPerBotSec.Value, 1), Is.EqualTo(31.2));
        Assert.That(BotCpuReport.Round(player.UsPerCall.Value, 2), Is.EqualTo(5.39));

        BotCpuCompare compare = window.Compares.Single();
        Assert.That(compare.Kind, Is.EqualTo(Player));
        Assert.That(compare.N, Is.EqualTo(1.0).Within(Tolerance));
        Assert.That(compare.GamebotN, Is.EqualTo(240).Within(Tolerance));
        // The ratios of the fields as the kind lines show them, written with 2 decimals.
        Assert.That(compare.Cpu, Is.EqualTo(2.104 / 1.840).Within(Tolerance));
        Assert.That(compare.Alloc, Is.EqualTo(60.4 / 31.2).Within(Tolerance));
        Assert.That(compare.Calls, Is.EqualTo(390.0 / 412.3).Within(Tolerance));
        Assert.That(compare.UsPerCall, Is.EqualTo(5.39 / 4.46).Within(Tolerance));
        Assert.That(new[] { compare.Cpu, compare.Alloc, compare.Calls, compare.UsPerCall }
            .Select(ratio => BotCpuReport.Round(ratio.Value, 2)), Is.EqualTo(new[] { 1.14, 1.94, 0.95, 1.21 }));
    }

    [Test]
    public void AGamebotValueOfZeroOrADashGivesADash()
    {
        // GameBots in the registry with nothing charged: msPerBotSec, callsPerBotSec and allocKBPerBotSec 0, and
        // usPerCall -.
        BotCpuTotals idle = new();
        idle.AddPresence(Player, WholeWindow);
        Charge(idle, Player, BotCpuStage.ClientEnd, 120, 3_600, 1_024);
        BotCpuCompare compare = Close(idle, gamebots: Samples(240)).Compares.Single();
        Assert.That(compare.Cpu, Is.Null);
        Assert.That(compare.Alloc, Is.Null);
        Assert.That(compare.Calls, Is.Null);
        Assert.That(compare.UsPerCall, Is.Null);

        // A gamebot value that shows as 0.000 is 0 too; a player with no calls has usPerCall -.
        BotCpuTotals tiny = new();
        Charge(tiny, Gamebot, BotCpuStage.Npc, 0.0004 * 240 * 60, 432_000, 1_024 * 240 * 60);
        tiny.AddPresence(Player, WholeWindow);
        compare = Close(tiny, gamebots: Samples(240)).Compares.Single();
        Assert.That(compare.Cpu, Is.Null);
        Assert.That(compare.Alloc, Is.EqualTo(0));
        Assert.That(compare.Calls, Is.EqualTo(0));
        Assert.That(compare.UsPerCall, Is.Null);
    }

    // Round gives a value as the report writes it with that many decimals (2.1), so a rank, a threshold or a ratio
    // taken from it agrees with the line. The invariant fixed-point text rounds the double's exact value: 1.0005 is
    // just under the midpoint, and an exact midpoint (0.125, 60.5, 412.25) goes to the even digit.
    [TestCase(1.8404, 3, 1.84)]
    [TestCase(0.0004, 3, 0)]
    [TestCase(0.96, 1, 1.0)]
    [TestCase(1.0005, 3, 1.0)]
    [TestCase(0.125, 2, 0.12)]
    [TestCase(0.375, 2, 0.38)]
    [TestCase(60.5, 0, 60)]
    [TestCase(61.5, 0, 62)]
    [TestCase(412.25, 1, 412.2)]
    public void RoundGivesTheValueAsTheReportWritesIt(double value, int decimals, double expected) =>
        Assert.That(BotCpuReport.Round(value, decimals), Is.EqualTo(expected));

    // ---- The lines (2.1)

    // The report writes in the invariant culture, a . decimal point whatever the server's culture (2.1). The text tests
    // run under de-DE, whose decimal separator is a comma. This guard fails a run in which that culture is not really
    // in force: with invariant globalization every culture formats as the invariant one, and the tests would prove
    // nothing.
    private static void AssertTheCultureIsGerman() =>
        Assert.That(1.5.ToString(), Is.EqualTo("1,5"), "the text tests must run under de-DE");

    // Charges each stage its value × perSeconds ms: a per-bot value over the kind's bot-seconds, or, for unattributed,
    // a per-second value over the window's length.
    private static void ChargeStages(BotCpuTotals counts, BotCpuKind kind, double perSeconds,
        params (BotCpuStage Stage, double Value)[] stages)
    {
        foreach ((BotCpuStage stage, double value) in stages)
            Charge(counts, kind, stage, value * perSeconds, 0);
    }

    // 2.1's sample window: 238.4 GameBots by the registry, one player, 1,830.2 live monster brains a tick and the
    // unattributed work, in 60 s at 30.0 ticks a second. Each stage is charged at its value on the line; gamebot's Npc
    // is 0.9197 and monster's stages are 0.00011-0.00012 over the values shown, so that msSec is 438.6 and 96.0 as the
    // lines say. The calls and bytes give the per-bot figures of the sample (and its process line's callsSec=212534).
    private static BotCpuWindow SpecSample()
    {
        BotCpuTotals counts = new();
        ChargeStages(counts, Gamebot, 238.4 * 60, (BotCpuStage.Timer, 0.041), (BotCpuStage.Npc, 0.9197),
            (BotCpuStage.Fleet, 0.118), (BotCpuStage.Attack, 0.210), (BotCpuStage.Casting, 0.140),
            (BotCpuStage.Effect, 0.081), (BotCpuStage.EffectListBegin, 0.050), (BotCpuStage.EffectListEnd, 0.040),
            (BotCpuStage.Movement, 0.150), (BotCpuStage.Reaper, 0.020), (BotCpuStage.Zone, 0.010),
            (BotCpuStage.Pets, 0.060));
        counts.Add(Gamebot, BotCpuStage.Npc, 0, 5_897_537, 456_995_635);

        // The player's stages in the order of 2.1's sample; its line puts them in tick order.
        counts.AddPresence(Player, WholeWindow);
        ChargeStages(counts, Player, 60, (BotCpuStage.ClientBegin, 0.310), (BotCpuStage.Timer, 0.022),
            (BotCpuStage.Attack, 0.201), (BotCpuStage.Movement, 0.090), (BotCpuStage.ClientEnd, 1.311),
            (BotCpuStage.Pets, 0.170));
        counts.Add(Player, BotCpuStage.ClientEnd, 0, 23_400, 3_710_976);

        counts.AddPresence(Monster, 3_294_360);
        ChargeStages(counts, Monster, 1_830.2 * 60, (BotCpuStage.Npc, 0.03112), (BotCpuStage.Attack, 0.00811),
            (BotCpuStage.Movement, 0.01111), (BotCpuStage.Effect, 0.00211));
        counts.Add(Monster, BotCpuStage.Npc, 0, 6_698_532, 44_978_995);

        ChargeStages(counts, Unattributed, 60, (BotCpuStage.GameLoop, 0.402), (BotCpuStage.Timer, 1.210),
            (BotCpuStage.Npc, 6.890), (BotCpuStage.Zone, 0.598));
        counts.Add(Unattributed, BotCpuStage.Npc, 0, 132_600, 5_406_720);

        return Close(counts, gamebots: Samples(238, 239, 238, 239, 238), bots: SpecSampleBots());
    }

    // The 238 GameBots of the sample, all charged in the window: its top 5 with their goals, and 233 more placed so
    // that p50 (rank 119 of 238) is 1.620 and p95 (rank 227) is 3.904.
    private static BotCpuBot[] SpecSampleBots()
    {
        List<BotCpuBot> bots =
        [
            new(Gamebot, "Aldric", 34, "Cleric", 163, "RvR", "Riding stablemaster horse route", Ticks(7.412 * 60),
                52_800),
            new(Gamebot, "Brennos", 50, "Armsman", 1, "Battleground", "Choosing first live goal", Ticks(6.020 * 60),
                45_000),
            new(Gamebot, "Cathal", 42, "Wizard", 1, "GroupPve", "Defeated; reassessing target difficulty",
                Ticks(5.881 * 60), 43_200),
            new(Gamebot, "Dagny", 47, "Paladin", 163, "RvR", "Recovered to safe capital spawn", Ticks(5.104 * 60),
                36_000),
            new(Gamebot, "Eirik", 20, "Scout", 1, "SoloPve", "Queued after a failed world transfer", Ticks(4.950 * 60),
                30_000),
        ];
        for (int rank = 1; rank <= 233; rank++)
        {
            double msSec = rank switch
            {
                <= 118 => 1 + rank / 1000.0,
                119 => 1.620,
                <= 226 => 2 + (rank - 120) / 100.0,
                227 => 3.904,
                _ => 4 + (rank - 228) / 10.0,
            };
            bots.Add(new BotCpuBot(Gamebot, $"Bot{rank:000}", 30, "Armsman", 1, "SoloPve", "Roaming",
                Ticks(msSec * 60), 1_800));
        }

        return bots.ToArray();
    }

    // The sample's process values (3.9).
    private static readonly BotCpuProcess SpecSampleProcess = new(Cores: 4, ProcessCpuPct: 148.2,
        LoopThreadsCpuPct: 121.0, CampThreadCpuPct: 3.1, LoopWaitMs: 212, ThrottledMs: 0, GcPauseMs: 14, Gen2: 0,
        AllocMBSec: 41.2, HeapMB: 2_210, RssMB: 3_480);

    // A window of 61.4 s (win=61): a player bot charged but never present (its client is not registered in
    // ClientService, 2.5), so n 0, and unattributed work.
    private static BotCpuWindow ShortWindow()
    {
        BotCpuTotals counts = new();
        Charge(counts, Playerbot, BotCpuStage.Timer, 30, 900, 2_048);
        Charge(counts, Unattributed, BotCpuStage.Timer, 72.6, 3_600);
        return Close(counts, seconds: 61.4);
    }

    // A platform that gives none of the values it reads (Windows, or every /proc and cgroup read failing, 3.9).
    private static readonly BotCpuProcess NoPlatformValues = new(4, null, null, null, null, null, null, null, null,
        null, null);

    [Test]
    [SetCulture("de-DE")]
    public void TheSampleWindowsLinesAreTheSpecs()
    {
        AssertTheCultureIsGerman();

        // 2.1's sample word for word, in its order: the kind lines, BOT_CPU_TOP, BOT_CPU_PROCESS, BOT_CPU_COMPARE. Only
        // the stage order differs: the sample's player line has ClientBegin before Timer and its monster line Movement
        // before Effect, where the lines are in tick order (2.1, 3.4).
        Assert.That(BotCpuReport.Lines(SpecSample(), SpecSampleProcess), Is.EqualTo(new[]
        {
            "BOT_CPU win=60 kind=gamebot n=238.4 msSec=438.6 msPerBotSec=1.840 callsPerBotSec=412.3 usPerCall=4.46 " +
            "allocKBPerBotSec=31.2 p50=1.620 p95=3.904 max=7.412 | Timer=0.041 Npc=0.920 Fleet=0.118 Attack=0.210 " +
            "Casting=0.140 Effect=0.081 EffectListBegin=0.050 EffectListEnd=0.040 Movement=0.150 Reaper=0.020 " +
            "Zone=0.010 Pets=0.060",
            "BOT_CPU win=60 kind=player n=1.0 msSec=2.1 msPerBotSec=2.104 callsPerBotSec=390.0 usPerCall=5.39 " +
            "allocKBPerBotSec=60.4 | Timer=0.022 ClientBegin=0.310 Attack=0.201 Movement=0.090 ClientEnd=1.311 " +
            "Pets=0.170",
            "BOT_CPU win=60 kind=monster n=1830.2 msSec=96.0 msPerBotSec=0.052 callsPerBotSec=61.0 usPerCall=0.86 " +
            "allocKBPerBotSec=0.4 | Npc=0.031 Attack=0.008 Effect=0.002 Movement=0.011",
            "BOT_CPU win=60 kind=unattributed n=- msSec=9.1 callsSec=2210.0 usPerCall=4.12 allocKBSec=88.0 | " +
            "GameLoop=0.402 Timer=1.210 Npc=6.890 Zone=0.598",
            "BOT_CPU_TOP win=60 kind=gamebot 1=Aldric:7.412:RvR 2=Brennos:6.020:Battleground 3=Cathal:5.881:GroupPve " +
            "4=Dagny:5.104:RvR 5=Eirik:4.950:SoloPve",
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 " +
            "campThreadCpuPct=3.1 chargedPct=54.6 callsSec=212534 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480",
            "BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/238.4 cpu=1.14x alloc=1.94x calls=0.95x usPerCall=1.21x",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void AKindLinesStageValuesAddUpToItsMsPerBotSec()
    {
        AssertTheCultureIsGerman();
        BotCpuWindow window = SpecSample();

        foreach (BotCpuKindWindow kind in window.Kinds)
        {
            string[] parts = BotCpuReport.KindLine(window, kind).Split(" | ");
            Assert.That(parts, Has.Length.EqualTo(2), kind.Kind.ToString());
            Dictionary<string, string> fields = parts[0].Split(' ').Skip(1)
                .ToDictionary(field => field.Split('=')[0], field => field.Split('=')[1]);
            double[] stages = parts[1].Split(' ')
                .Select(stage => double.Parse(stage.Split('=')[1], CultureInfo.InvariantCulture)).ToArray();

            // Each value is rounded to 3 decimals, and the total to its own (msSec has 1 for unattributed).
            bool unattributed = kind.Kind == Unattributed;
            double total = double.Parse(fields[unattributed ? "msSec" : "msPerBotSec"], CultureInfo.InvariantCulture);
            double rounding = (unattributed ? 0.05 : 0.0005) + stages.Length * 0.0005;
            Assert.That(stages.Sum(), Is.EqualTo(total).Within(rounding), kind.Kind.ToString());
        }
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStagesAreInTickOrderAndAStageThatRoundsToZeroIsLeftOut()
    {
        AssertTheCultureIsGerman();
        BotCpuTotals counts = new();
        counts.AddPresence(Player, WholeWindow);
        // Every stage at (its index + 1) × 0.01 ms per bot per s, charged last stage first, but Crafting at 0.0004,
        // which the line shows as 0.000, and Quests not charged at all.
        foreach (BotCpuStage stage in Enum.GetValues<BotCpuStage>().Reverse())
        {
            double perBot = stage switch
            {
                BotCpuStage.Crafting => 0.0004,
                BotCpuStage.Quests => 0,
                _ => ((int)stage + 1) * 0.01,
            };
            if (perBot > 0)
                Charge(counts, Player, stage, perBot * 60, 30);
        }

        BotCpuWindow window = Close(counts);

        Assert.That(BotCpuReport.KindLine(window, window.Kind(Player)), Is.EqualTo(
            "BOT_CPU win=60 kind=player n=1.0 msSec=1.4 msPerBotSec=1.430 callsPerBotSec=8.5 usPerCall=168.28 " +
            "allocKBPerBotSec=0.0 | GameLoop=0.010 Timer=0.020 ClientBegin=0.030 Npc=0.040 Fleet=0.050 Attack=0.060 " +
            "Casting=0.070 Effect=0.080 EffectListBegin=0.090 EffectListEnd=0.100 Movement=0.110 Reaper=0.130 " +
            "Zone=0.140 ClientEnd=0.150 Pets=0.170 Other=0.180"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void NothingToDivideByIsADashAndALineWithNoStageValueHasNoBar()
    {
        AssertTheCultureIsGerman();

        // n 0 with time charged: every per-bot field is -, and no part after |; usPerCall has its calls (2.1).
        BotCpuWindow window = ShortWindow();
        Assert.That(BotCpuReport.KindLine(window, window.Kind(Playerbot)), Is.EqualTo(
            "BOT_CPU win=61 kind=playerbot n=0.0 msSec=0.5 msPerBotSec=- callsPerBotSec=- usPerCall=33.33 " +
            "allocKBPerBotSec=- p50=- p95=- max=-"));
        Assert.That(BotCpuReport.KindLine(window, window.Kind(Unattributed)), Is.EqualTo(
            "BOT_CPU win=61 kind=unattributed n=- msSec=1.2 callsSec=58.6 usPerCall=20.17 allocKBSec=0.0 | " +
            "Timer=1.182"));

        // A kind that is no bot kind has no spread fields, - or not: a player charged at ClientEnd, never present.
        BotCpuTotals player = new();
        Charge(player, Player, BotCpuStage.ClientEnd, 6, 60);
        BotCpuWindow neverPresent = Close(player);
        Assert.That(BotCpuReport.KindLine(neverPresent, neverPresent.Kind(Player)), Is.EqualTo(
            "BOT_CPU win=60 kind=player n=0.0 msSec=0.1 msPerBotSec=- callsPerBotSec=- usPerCall=100.00 " +
            "allocKBPerBotSec=-"));

        // GameBots in the registry with nothing charged: zeros, usPerCall - with no call, and every stage 0.000, so no
        // part after | either.
        BotCpuWindow idle = Close(new BotCpuTotals(), gamebots: Samples(240));
        Assert.That(BotCpuReport.KindLine(idle, idle.Kind(Gamebot)), Is.EqualTo(
            "BOT_CPU win=60 kind=gamebot n=240.0 msSec=0.0 msPerBotSec=0.000 callsPerBotSec=0.0 usPerCall=- " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=-"));

        // GameBots charged but none of them listed as charged in the window: no spread, so p50, p95 and max are -.
        BotCpuTotals unlisted = new();
        Charge(unlisted, Gamebot, BotCpuStage.Npc, 2_400, 48_000);
        BotCpuWindow noSpread = Close(unlisted, gamebots: Samples(240));
        Assert.That(BotCpuReport.KindLine(noSpread, noSpread.Kind(Gamebot)), Is.EqualTo(
            "BOT_CPU win=60 kind=gamebot n=240.0 msSec=40.0 msPerBotSec=0.167 callsPerBotSec=3.3 usPerCall=50.00 " +
            "allocKBPerBotSec=0.0 p50=- p95=- max=- | Npc=0.167"));

        // No NaN or Infinity in any line.
        foreach (BotCpuWindow each in new[] { window, neverPresent, idle, noSpread })
        {
            Assert.That(string.Join('\n', BotCpuReport.Lines(each, NoPlatformValues)),
                Does.Not.Contain("NaN").And.Not.Contain("Infinity").And.Not.Contain("∞"));
        }
    }

    [Test]
    [SetCulture("de-DE")]
    public void AKindWithNothingWritesNoLine()
    {
        AssertTheCultureIsGerman();

        // Companion, playerbot, piloted and monster have nothing in the sample: 4 kind lines of 7.
        Assert.That(BotCpuReport.Lines(SpecSample(), SpecSampleProcess).Where(line => line.StartsWith("BOT_CPU ")),
            Has.Exactly(4).Items);

        // A window with nothing at all has its process line only.
        Assert.That(BotCpuReport.Lines(Close(new BotCpuTotals()), SpecSampleProcess), Is.EqualTo(new[]
        {
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 " +
            "campThreadCpuPct=3.1 chargedPct=0.0 callsSec=0 loopWaitMs=212 throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void ATopLineGivesTheGoalOfGameBotsOnly()
    {
        AssertTheCultureIsGerman();
        BotCpuBot[] bots =
        [
            // No PersistentRecord: no goal, -.
            new(Gamebot, "Sir Fenn", 34, "Cleric", 163, null, null, Ticks(2.0 * 60), 1_800),
            // A companion's name, as GameBot gives a helper with none (bots/GameBot.cs:2279).
            new(Companion, "Gm's Cleric Bot", 40, "Cleric", 1, null, null, Ticks(1.5 * 60), 3_600),
            new(Companion, "Hilda", 34, "Cleric", 163, null, null, Ticks(9.0 * 60), 1_800),
            new(Gamebot, "Odd:Goal", 34, "Cleric", 163, "", null, Ticks(1.0 * 60), 1_800),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 180, 3_600);
        Charge(counts, Companion, BotCpuStage.Npc, 630, 5_400);
        counts.AddPresence(Companion, 2 * WholeWindow);

        BotCpuWindow window = Close(counts, gamebots: Samples(2), bots: bots);

        Assert.That(BotCpuReport.TopLine(window, Gamebot),
            Is.EqualTo("BOT_CPU_TOP win=60 kind=gamebot 1=Sir_Fenn:2.000:- 2=Odd_Goal:1.000:-"));
        Assert.That(BotCpuReport.TopLine(window, Companion),
            Is.EqualTo("BOT_CPU_TOP win=60 kind=companion 1=Hilda:9.000 2=Gm's_Cleric_Bot:1.500"));
        // No bot of the kind charged: no line.
        Assert.That(BotCpuReport.TopLine(window, Playerbot), Is.Null);
        Assert.That(BotCpuReport.TopLine(window, Player), Is.Null);

        // The top lines come after the kind lines, in kind order, and before the process line.
        Assert.That(BotCpuReport.Lines(window, NoPlatformValues).Select(line => line.Split(' ')[0] + " " +
                line.Split(' ')[2]),
            Is.EqualTo(new[]
            {
                "BOT_CPU kind=gamebot", "BOT_CPU kind=companion", "BOT_CPU_TOP kind=gamebot",
                "BOT_CPU_TOP kind=companion", "BOT_CPU_PROCESS tps=30.0",
            }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void AProcessValueThePlatformCannotGiveIsADash()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.ProcessLine(ShortWindow(), NoPlatformValues), Is.EqualTo(
            "BOT_CPU_PROCESS win=61 tps=30.0 cores=4 processCpuPct=- loopThreadsCpuPct=- offLoopCpuPct=- " +
            "campThreadCpuPct=- chargedPct=0.2 callsSec=73 loopWaitMs=- throttledMs=- gcPauseMs=- gen2=- " +
            "allocMBSec=- heapMB=- rssMB=-"));

        // A schedstat that reads 0 (3.9): the loop threads' fields are -, and offLoopCpuPct with them.
        BotCpuProcess noSchedstat = SpecSampleProcess with
        {
            LoopThreadsCpuPct = null, CampThreadCpuPct = null, LoopWaitMs = null,
        };
        Assert.That(noSchedstat.OffLoopCpuPct, Is.Null);
        Assert.That(BotCpuReport.ProcessLine(SpecSample(), noSchedstat), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=148.2 loopThreadsCpuPct=- offLoopCpuPct=- " +
            "campThreadCpuPct=- chargedPct=54.6 callsSec=212534 loopWaitMs=- throttledMs=0 gcPauseMs=14 gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=3480"));

        // A value that is not a number is never written as one: -.
        BotCpuProcess notANumber = SpecSampleProcess with
        {
            ProcessCpuPct = double.NaN, GcPauseMs = double.PositiveInfinity, RssMB = double.NegativeInfinity,
        };
        Assert.That(BotCpuReport.ProcessLine(SpecSample(), notANumber), Is.EqualTo(
            "BOT_CPU_PROCESS win=60 tps=30.0 cores=4 processCpuPct=- loopThreadsCpuPct=121.0 offLoopCpuPct=- " +
            "campThreadCpuPct=3.1 chargedPct=54.6 callsSec=212534 loopWaitMs=212 throttledMs=0 gcPauseMs=- gen2=0 " +
            "allocMBSec=41.2 heapMB=2210 rssMB=-"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void OffLoopIsTheDifferenceOfTheTwoValuesTheLineShows()
    {
        AssertTheCultureIsGerman();
        // 148.24 and 120.96 show as 148.2 and 121.0: off the loop is 27.2 as the owner subtracts them, not 27.3.
        BotCpuProcess process = SpecSampleProcess with { ProcessCpuPct = 148.24, LoopThreadsCpuPct = 120.96 };

        Assert.That(process.OffLoopCpuPct, Is.EqualTo(27.2).Within(Tolerance));
        Assert.That(BotCpuReport.ProcessLine(SpecSample(), process),
            Does.Contain(" processCpuPct=148.2 loopThreadsCpuPct=121.0 offLoopCpuPct=27.2 "));
    }

    [Test]
    [SetCulture("de-DE")]
    public void ACompareRatioIsADashWhenEitherValueIsOrTheGamebotValueIsZero()
    {
        AssertTheCultureIsGerman();

        // GameBots in the registry with nothing charged: their values are 0, or - for usPerCall.
        BotCpuTotals idle = new();
        idle.AddPresence(Player, WholeWindow);
        Charge(idle, Player, BotCpuStage.ClientEnd, 120, 3_600, 1_024);
        BotCpuWindow window = Close(idle, gamebots: Samples(240));
        Assert.That(BotCpuReport.CompareLine(window, window.Compares.Single()), Is.EqualTo(
            "BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/240.0 cpu=- alloc=- calls=- usPerCall=-"));

        // A gamebot value that shows as 0.000 is 0; a player value of 0 gives 0.00x; a player with no calls has
        // usPerCall -.
        BotCpuTotals tiny = new();
        Charge(tiny, Gamebot, BotCpuStage.Npc, 0.0004 * 240 * 60, 432_000, 1_024 * 240 * 60);
        tiny.AddPresence(Player, WholeWindow);
        window = Close(tiny, gamebots: Samples(240));
        Assert.That(BotCpuReport.CompareLine(window, window.Compares.Single()), Is.EqualTo(
            "BOT_CPU_COMPARE win=60 pair=player/gamebot n=1.0/240.0 cpu=- alloc=0.00x calls=0.00x usPerCall=-"));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStateAndErrorLinesAreTheSpecs()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.StateOnByPropertyLine, Is.EqualTo("BOT_CPU_STATE on source=property"));
        Assert.That(BotCpuReport.StateOnByCommandLine("Aldwin", 30),
            Is.EqualTo("BOT_CPU_STATE on source=command by=Aldwin minutes=30"));
        Assert.That(BotCpuReport.StateOnByCommandLine("Aldwin", 1440),
            Is.EqualTo("BOT_CPU_STATE on source=command by=Aldwin minutes=1440"));
        Assert.That(BotCpuReport.StateOnByCommandLine("Aldwin", null),
            Is.EqualTo("BOT_CPU_STATE on source=command by=Aldwin minutes=-"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Command, "Aldwin"),
            Is.EqualTo("BOT_CPU_STATE off source=command by=Aldwin"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Timer), Is.EqualTo("BOT_CPU_STATE off source=timer"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Stop), Is.EqualTo("BOT_CPU_STATE off source=stop"));
        Assert.That(BotCpuReport.StateOffLine(BotCpuOffSource.Error), Is.EqualTo("BOT_CPU_STATE off source=error"));

        Assert.That(BotCpuReport.ErrorLine("tick", new InvalidOperationException("no slot list")),
            Is.EqualTo("BOT_CPU_ERROR tick: System.InvalidOperationException: no slot list"));
    }

    // ---- The CSV (2.3)

    [Test]
    public void TheCsvHeaderIsTheSpecs()
    {
        Assert.That(BotCpuReport.CsvHeader, Is.EqualTo(
            "utc,win_s,tps,cores,process_cpu_pct,loop_threads_cpu_pct,off_loop_cpu_pct,camp_thread_cpu_pct," +
            "charged_pct,all_calls_sec,loop_wait_ms,throttled_ms,gc_pause_ms,gen2,alloc_mb_sec,heap_mb,rss_mb,kind,n," +
            "ms_sec,calls_sec,alloc_kb_sec,ms_per_bot_sec,calls_per_bot_sec,us_per_call,alloc_kb_per_bot_sec,p50,p95," +
            "max,GameLoop,Timer,ClientBegin,Npc,Fleet,Attack,Casting,Effect,EffectListBegin,EffectListEnd,Movement," +
            "Crafting,Reaper,Zone,ClientEnd,Quests,Pets,Other"));
        // Its stage columns are the stages, in their order.
        Assert.That(BotCpuReport.CsvHeader.Split(',').TakeLast(BotCpuTotals.StageCount),
            Is.EqualTo(Enum.GetNames<BotCpuStage>()));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheSampleWindowsRowsCarryItsProcessValuesAndEachKindsWholeStageTotals()
    {
        AssertTheCultureIsGerman();
        string window = "2026-10-18T19:02:10Z,60,30.0,4,148.2,121.0,27.2,3.1,54.6,212534,212,0,14,0,41.2,2210,3480,";

        IReadOnlyList<string> rows = BotCpuReport.CsvRows(SpecSample(), SpecSampleProcess,
            new DateTime(2026, 10, 18, 19, 2, 10, DateTimeKind.Utc));

        // The stage columns are the kind's raw stage ticks ÷ the window: gamebot's Npc is 13,155.3888 ms in 60 s,
        // 219.256 ms per s, where the line's rounded 0.920 per bot × n 238.4 would give 219.328 (2.3). Player and
        // monster have no p50, p95 or max; unattributed no n and no per-bot field: empty cells.
        Assert.That(rows, Is.EqualTo(new[]
        {
            window + "gamebot,238.4,438.6,98292.3,7438.1,1.840,412.3,4.46,31.2,1.620,3.904,7.412," +
            "0.000,9.774,0.000,219.256,28.131,50.064,33.376,19.310,11.920,9.536,35.760,0.000,4.768,2.384,0.000,0.000," +
            "14.304,0.000",
            window + "player,1.0,2.1,390.0,60.4,2.104,390.0,5.39,60.4,,,," +
            "0.000,0.022,0.310,0.000,0.000,0.201,0.000,0.000,0.000,0.000,0.090,0.000,0.000,0.000,1.311,0.000,0.170," +
            "0.000",
            window + "monster,1830.2,96.0,111642.2,732.1,0.052,61.0,0.86,0.4,,,," +
            "0.000,0.000,0.000,56.956,0.000,14.843,0.000,3.862,0.000,0.000,20.334,0.000,0.000,0.000,0.000,0.000," +
            "0.000,0.000",
            window + "unattributed,,9.1,2210.0,88.0,,,4.12,,,,," +
            "0.402,1.210,0.000,6.890,0.000,0.000,0.000,0.000,0.000,0.000,0.000,0.000,0.000,0.598,0.000,0.000,0.000," +
            "0.000",
        }));
        int columns = BotCpuReport.CsvHeader.Split(',').Length;
        Assert.That(rows.Select(row => row.Split(',').Length), Is.All.EqualTo(columns));
    }

    [Test]
    [SetCulture("de-DE")]
    public void ADashIsAnEmptyCellAndAKindWithNoNKeepsItsStageTotals()
    {
        AssertTheCultureIsGerman();
        string window = "2026-10-18T19:03:11Z,61,30.0,4,,,,,0.2,73,,,,,,,,";
        string zeros = string.Join(',', Enumerable.Repeat("0.000", 16));

        // A 61.4 s window divides by 61.4 (win_s 61): unattributed's 72.6 ms of timers are 1.182 ms per s, not 1.190.
        IReadOnlyList<string> rows = BotCpuReport.CsvRows(ShortWindow(), NoPlatformValues,
            new DateTime(2026, 10, 18, 19, 3, 11, DateTimeKind.Utc));

        Assert.That(rows, Is.EqualTo(new[]
        {
            window + "playerbot,0.0,0.5,14.7,0.0,,,33.33,,,,,0.000,0.489," + zeros,
            window + "unattributed,,1.2,58.6,0.0,,,20.17,,,,,0.000,1.182," + zeros,
        }));
        int columns = BotCpuReport.CsvHeader.Split(',').Length;
        Assert.That(rows.Select(row => row.Split(',').Length), Is.All.EqualTo(columns));
    }

    [Test]
    public void TheUtcCellIsTheWindowsEndToTheSecond() =>
        Assert.That(BotCpuReport.CsvUtc(new DateTime(2026, 10, 18, 19, 2, 10, 750, DateTimeKind.Utc)),
            Is.EqualTo("2026-10-18T19:02:10Z"));

    // ---- The chat (2.4)

    // When the sample's meter was turned on, in the server's local time.
    private static readonly DateTime OnAt = new(2026, 10, 18, 14, 2, 5);

    // The totals since on for the sample status: 11 windows before 2.1's sample with 238.4 GameBots at 1.7877 ms per
    // bot per s and one player at 2.0124, so that with the sample they are gamebot 1.792 and player 2.020 (2.4's
    // sample); monster has bot-seconds only in the sample, 0.052.
    private static BotCpuSince SinceTheSample(bool reset)
    {
        BotCpuTotals earlier = new();
        Charge(earlier, Gamebot, BotCpuStage.Npc, 1.7877 * 238.4 * 60, 5_900_000);
        earlier.AddPresence(Player, WholeWindow);
        Charge(earlier, Player, BotCpuStage.ClientEnd, 2.0124 * 60, 23_400);
        BotCpuWindow before = Close(earlier, gamebots: Samples(238, 239, 238, 239, 238));

        BotCpuSince since = BotCpuSince.Start(reset);
        for (int window = 0; window < 11; window++)
            since = since.Add(before);

        return since.Add(SpecSample());
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStatusOfTheSampleWindowIsTheSpecs()
    {
        AssertTheCultureIsGerman();

        // 2.4's sample word for word, but for the since-on line: it gives every kind with bot-seconds above 0 (2.4),
        // and monster has them, so monster is there too. 12 min 15 s after on: 12 min, and 12 windows written.
        IReadOnlyList<string> status = BotCpuReport.StatusText(OnAt, byCommand: true, stopsAt: OnAt.AddMinutes(30),
            on: TimeSpan.FromMinutes(12.25), SpecSample(), SpecSampleProcess, SinceTheSample(reset: false));
        Assert.That(status, Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by command, 12 min; stops at 14:32).",
            "Last minute (60 s, 30.0 ticks/s): process 148% of one core (4 cores), 55% charged to kinds, 27% off the " +
            "loop.",
            "gamebot n=238.4: 1.840 ms/s per bot (438.6 ms/s in all); most in Npc 0.920",
            "player n=1.0: 2.104 ms/s per bot (2.1 ms/s in all); most in ClientEnd 1.311",
            "monster n=1830.2: 0.052 ms/s per bot (96.0 ms/s in all); most in Npc 0.031",
            "unattributed: 9.1 ms/s; most in Npc 6.890",
            "Since on (12 min): gamebot 1.792, player 2.020, monster 0.052 ms/s per bot.",
        }));

        // After /botcpu reset the last line says so.
        Assert.That(BotCpuReport.StatusText(OnAt, byCommand: true, stopsAt: OnAt.AddMinutes(30),
                on: TimeSpan.FromMinutes(12.25), SpecSample(), SpecSampleProcess, SinceTheSample(reset: true)).Last(),
            Is.EqualTo("Since reset (12 min): gamebot 1.792, player 2.020, monster 0.052 ms/s per bot."));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStatusBeforeTheFirstWindowSaysWhenItComes()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.StatusText(OnAt, byCommand: false, stopsAt: null, on: TimeSpan.FromSeconds(35), null,
            null, BotCpuSince.Start(reset: false)), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by property, 0 min; no stop time).",
            "No full minute yet; the first lines come at 14:03.",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheStatusShowsADashAsTheLinesDo()
    {
        AssertTheCultureIsGerman();
        BotCpuWindow window = ShortWindow();

        // No platform values; a player bot with n 0 has no per-bot value and no stage; no kind has bot-seconds, so
        // there is no since-on line.
        Assert.That(BotCpuReport.StatusText(OnAt, byCommand: false, stopsAt: null, on: TimeSpan.FromSeconds(85), window,
            NoPlatformValues, BotCpuSince.Start(reset: false).Add(window)), Is.EqualTo(new[]
        {
            "Bot CPU meter: on since 14:02 (by property, 1 min; no stop time).",
            "Last minute (61 s, 30.0 ticks/s): process - of one core (4 cores), 0% charged to kinds, - off the loop.",
            "playerbot n=0.0: - ms/s per bot (0.5 ms/s in all); most in -",
            "unattributed: 1.2 ms/s; most in Timer 1.182",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void EachReplyIsTheSpecs()
    {
        AssertTheCultureIsGerman();
        const string where = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

        Assert.That(BotCpuReport.OffStatusText, Is.EqualTo("Bot CPU meter: off. /botcpu on [minutes] turns it on."));
        Assert.That(BotCpuReport.NoWindowYetText(OnAt),
            Is.EqualTo("No full minute yet; the first lines come at 14:03."));
        Assert.That(BotCpuReport.OnText(30, OnAt.AddMinutes(30)),
            Is.EqualTo("Bot CPU meter on for 30 minutes (until 14:32)" + where));
        Assert.That(BotCpuReport.OnText(), Is.EqualTo("Bot CPU meter on until /botcpu off or the next start" + where));
        Assert.That(BotCpuReport.AlreadyOnText(OnAt.AddMinutes(30)),
            Is.EqualTo("Bot CPU meter already on; it now stops at 14:32."));
        Assert.That(BotCpuReport.AlreadyOnText(null),
            Is.EqualTo("Bot CPU meter already on; it now runs until /botcpu off or the next start."));
        Assert.That(BotCpuReport.OffText(37.6), Is.EqualTo("Bot CPU meter off. The last 37 s are being written."));
        Assert.That(BotCpuReport.OffText(3.2), Is.EqualTo("Bot CPU meter off. The last 3 s were too short to write."));
        Assert.That(BotCpuReport.AlreadyOffText, Is.EqualTo("Bot CPU meter is already off."));
        Assert.That(BotCpuReport.ResetText, Is.EqualTo("Bot CPU totals reset."));
        Assert.That(BotCpuReport.TopText(SpecSample(), Companion),
            Is.EqualTo(new[] { "No companion was charged in the last minute." }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TheMinutesAndTimesOfTheReplies()
    {
        AssertTheCultureIsGerman();
        const string where = ": BOT_CPU lines every minute in the server log, rows in logs/bot-cpu.csv.";

        // 1 to 1440 minutes (2.4), no digit grouping; one minute is singular.
        Assert.That(BotCpuReport.OnText(1, OnAt.AddMinutes(1)),
            Is.EqualTo("Bot CPU meter on for 1 minute (until 14:03)" + where));
        Assert.That(BotCpuReport.OnText(1440, OnAt.AddMinutes(1440)),
            Is.EqualTo("Bot CPU meter on for 1440 minutes (until 14:02)" + where));
        // The 24-hour clock, with its leading zero.
        Assert.That(BotCpuReport.NoWindowYetText(new DateTime(2026, 10, 18, 8, 59, 30)),
            Is.EqualTo("No full minute yet; the first lines come at 09:00."));
        Assert.That(BotCpuReport.AlreadyOnText(new DateTime(2026, 10, 18, 23, 7, 0)),
            Is.EqualTo("Bot CPU meter already on; it now stops at 23:07."));
    }

    // The last seconds at off are written from 5 s (2.2), in whole seconds.
    [TestCase(4.99, "Bot CPU meter off. The last 4 s were too short to write.")]
    [TestCase(5.0, "Bot CPU meter off. The last 5 s are being written.")]
    [TestCase(59.9, "Bot CPU meter off. The last 59 s are being written.")]
    [TestCase(0, "Bot CPU meter off. The last 0 s were too short to write.")]
    [TestCase(-1, "Bot CPU meter off. The last 0 s were too short to write.")]
    [TestCase(double.NaN, "Bot CPU meter off. The last 0 s were too short to write.")]
    public void OffSaysWhetherTheLastSecondsAreWritten(double seconds, string reply) =>
        Assert.That(BotCpuReport.OffText(seconds), Is.EqualTo(reply));

    [Test]
    [SetCulture("de-DE")]
    public void TopListsTheFiveCostliestGameBotsWithTheirGoalAndActivity()
    {
        AssertTheCultureIsGerman();

        Assert.That(BotCpuReport.TopText(SpecSample(), Gamebot), Is.EqualTo(new[]
        {
            "1. Aldric (34 Cleric, region 163): 7.412 ms/s, 880 calls/s, RvR, \"Riding stablemaster horse route\"",
            "2. Brennos (50 Armsman, region 1): 6.020 ms/s, 750 calls/s, Battleground, \"Choosing first live goal\"",
            "3. Cathal (42 Wizard, region 1): 5.881 ms/s, 720 calls/s, GroupPve, " +
            "\"Defeated; reassessing target difficulty\"",
            "4. Dagny (47 Paladin, region 163): 5.104 ms/s, 600 calls/s, RvR, \"Recovered to safe capital spawn\"",
            "5. Eirik (20 Scout, region 1): 4.950 ms/s, 500 calls/s, SoloPve, \"Queued after a failed world transfer\"",
        }));
    }

    [Test]
    [SetCulture("de-DE")]
    public void TopShowsADashAndEmptyQuotesForAGameBotWithNoRecord()
    {
        AssertTheCultureIsGerman();
        BotCpuBot[] bots =
        [
            new(Gamebot, "Sir Fenn", 34, "Cleric", 163, null, null, Ticks(2.0 * 60), 1_800),
            new(Companion, "Hilda", 34, "Cleric", 163, null, null, Ticks(9.0 * 60), 1_800),
            new(Companion, "Gm's Cleric Bot", 40, "Cleric", 1, null, null, Ticks(1.5 * 60), 3_600),
        ];
        BotCpuTotals counts = new();
        Charge(counts, Gamebot, BotCpuStage.Npc, 120, 1_800);
        Charge(counts, Companion, BotCpuStage.Npc, 630, 5_400);

        BotCpuWindow window = Close(counts, gamebots: Samples(1), bots: bots);

        // The goal and activity as the slow-turn warning reads them, with ?. (NS:81): -, and "" (2.4).
        Assert.That(BotCpuReport.TopText(window, Gamebot),
            Is.EqualTo(new[] { "1. Sir Fenn (34 Cleric, region 163): 2.000 ms/s, 30 calls/s, -, \"\"" }));
        // The other bot kinds have no goal or activity; the name is the bot's own, not the log line's.
        Assert.That(BotCpuReport.TopText(window, Companion), Is.EqualTo(new[]
        {
            "1. Hilda (34 Cleric, region 163): 9.000 ms/s, 30 calls/s",
            "2. Gm's Cleric Bot (40 Cleric, region 1): 1.500 ms/s, 60 calls/s",
        }));
        Assert.That(BotCpuReport.TopText(window, Playerbot),
            Is.EqualTo(new[] { "No playerbot was charged in the last minute." }));
    }
}
