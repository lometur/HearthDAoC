using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's report (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). Once a minute
// the report side takes the difference of two snapshots of the per-thread slots (3.7) and of each bot's counter (3.10),
// and this file turns it, with the window's exact length, the game time and the registry samples, into the report's
// numbers: each kind's figures with the - rules of 2.1, n and bot-seconds (3.8), the spread and the top 5 of each bot
// kind (3.10), the compare pairs (2.1) and the totals since on (2.4). Plain values in and out, no server state, so it
// is unit-tested without a server (UT_BotCpuReport). BotCpuMeter is the hot path that fills the slots and counters;
// BotCpuScript reads the server's state and writes the lines.

/// <summary>
/// The registry samples of one window (3.8): <c>AutonomousBotRegistry.PopulationForBrainTick</c>, read once a second.
/// GameBot <c>n</c> is their average, or the last sample when the window has none (2.2).
/// </summary>
/// <param name="Sum">The samples' sum in this window.</param>
/// <param name="Count">The samples in this window.</param>
/// <param name="Last">The latest sample of the run, from this window or an earlier one; null before the first.</param>
public readonly record struct BotCpuSamples(long Sum, int Count, int? Last)
{
    /// <summary>These samples and one more.</summary>
    public BotCpuSamples Add(int sample) => new(Sum + sample, Count + 1, sample);

    /// <summary>The next window's samples: none yet, with this window's last sample kept.</summary>
    public BotCpuSamples NextWindow() => new(0, 0, Last);

    /// <summary>GameBot <c>n</c>: the samples' average, else the last sample, else 0.</summary>
    public double Average => Count > 0 ? (double)Sum / Count : Last ?? 0;
}

/// <summary>
/// One bot of a bot kind as the report lists it from the per-bot table (3.10): what the report shows of it and its
/// counter's ticks and calls in the window (<see cref="BotCpuReport.TakeCounterWindow"/>).
/// </summary>
/// <param name="Kind">Its counter's kind: gamebot, companion, playerbot or piloted.</param>
/// <param name="Name">Its name.</param>
/// <param name="Level">Its level.</param>
/// <param name="ClassName">Its class (<c>GameBot.ClassName</c>).</param>
/// <param name="Region">Its region.</param>
/// <param name="Goal">A GameBot's <c>PersistentRecord.ObjectiveKind</c>; null without a record or for another
/// kind.</param>
/// <param name="Activity">A GameBot's <c>PersistentRecord.Activity</c>; null without a record or for another
/// kind.</param>
/// <param name="Ticks">The <see cref="System.Diagnostics.Stopwatch"/> ticks charged to it in the window.</param>
/// <param name="Calls">The callbacks charged to it in the window.</param>
public sealed record BotCpuBot(BotCpuKind Kind, string Name, int Level, string ClassName, int Region, string Goal,
    string Activity, long Ticks, long Calls);

/// <summary>The spread of a bot kind's per-bot ms per s (3.10), nearest rank.</summary>
public readonly record struct BotCpuSpread(double P50, double P95, double Max);

/// <summary>One of a bot kind's 5 costliest bots in a window (3.10): its rank from 1, the bot, its ms per s and its
/// callbacks per s.</summary>
public sealed record BotCpuTopEntry(int Rank, BotCpuBot Bot, double MsSec, double CallsSec);

/// <summary>
/// A compare pair (2.1): <paramref name="Kind"/> (playerbot, piloted or player) against gamebot, both with <c>n</c> of
/// at least 1.0. Each ratio is null (<c>-</c>) when either value is <c>-</c> or the gamebot value is 0.
/// </summary>
/// <param name="Kind">The pair's first kind.</param>
/// <param name="N">Its <c>n</c>.</param>
/// <param name="GamebotN">Gamebot's <c>n</c>.</param>
/// <param name="Cpu">The ratio of the <c>msPerBotSec</c>s.</param>
/// <param name="Alloc">The ratio of the <c>allocKBPerBotSec</c>s.</param>
/// <param name="Calls">The ratio of the <c>callsPerBotSec</c>s: items per bot, not thinks.</param>
/// <param name="UsPerCall">The ratio of the <c>usPerCall</c>s.</param>
public sealed record BotCpuCompare(BotCpuKind Kind, double N, double GamebotN, double? Cpu, double? Alloc,
    double? Calls, double? UsPerCall);

/// <summary>
/// One kind's figures in a window (2.1, 2.3): what its <c>BOT_CPU</c> line and CSV row show. A per-bot figure is null
/// (<c>-</c>) when the kind's bot-seconds are 0, and <see cref="UsPerCall"/> when its calls are 0: never NaN, Infinity
/// or a 0 that stands for nothing to divide by.
/// </summary>
public sealed class BotCpuKindWindow
{
    internal BotCpuKindWindow(BotCpuKind kind, double? n, BotCpuTotals counts, double seconds, long frequency,
        BotCpuSpread? spread)
    {
        Kind = kind;
        N = n;
        BotSeconds = n > 0 ? n.Value * seconds : 0;

        double[] stageMsSec = new double[BotCpuTotals.StageCount];
        double[] perBot = BotSeconds > 0 ? new double[BotCpuTotals.StageCount] : null;
        for (int stage = 0; stage < BotCpuTotals.StageCount; stage++)
        {
            long ticks = counts.Ticks(kind, (BotCpuStage)stage);
            Ticks += ticks;
            Calls += counts.Calls(kind, (BotCpuStage)stage);
            Bytes += counts.Bytes(kind, (BotCpuStage)stage);
            double ms = ticks * 1000.0 / frequency;
            stageMsSec[stage] = ms / seconds;
            if (perBot != null)
                perBot[stage] = ms / BotSeconds;
        }

        ChargedMs = Ticks * 1000.0 / frequency;
        MsSec = ChargedMs / seconds;
        CallsSec = Calls / seconds;
        AllocKBSec = Bytes / 1024.0 / seconds;
        UsPerCall = Calls > 0 ? Ticks * 1_000_000.0 / frequency / Calls : null;
        if (BotSeconds > 0)
        {
            MsPerBotSec = ChargedMs / BotSeconds;
            CallsPerBotSec = Calls / BotSeconds;
            AllocKBPerBotSec = Bytes / 1024.0 / BotSeconds;
        }

        Spread = spread;
        StageMsSec = stageMsSec;
        // Unattributed's line has its stage values per second, the same as its CSV columns (2.1).
        StageValues = kind == BotCpuKind.Unattributed ? stageMsSec : perBot;
    }

    public BotCpuKind Kind { get; }

    /// <summary>The average count of the kind over the window (3.8); null (<c>-</c>) for unattributed.</summary>
    public double? N { get; }

    /// <summary><see cref="N"/> × the window's exact length (2.2), not the rounded <c>win</c>: every per-bot figure
    /// divides by it (3.8).</summary>
    public double BotSeconds { get; }

    /// <summary>The raw <see cref="System.Diagnostics.Stopwatch"/> ticks charged to the kind, every stage.</summary>
    public long Ticks { get; }

    /// <summary>The callbacks charged to the kind.</summary>
    public long Calls { get; }

    /// <summary>The bytes allocated inside the kind's callbacks.</summary>
    public long Bytes { get; }

    /// <summary>All the ms charged to the kind in the window, for the totals since on (2.4).</summary>
    public double ChargedMs { get; }

    /// <summary><c>msSec</c>: the ms charged per second of window.</summary>
    public double MsSec { get; }

    /// <summary><c>callsSec</c>, unattributed's line and every CSV row: callbacks per second of window.</summary>
    public double CallsSec { get; }

    /// <summary><c>allocKBSec</c>, unattributed's line and every CSV row: KB allocated per second of window.</summary>
    public double AllocKBSec { get; }

    /// <summary><c>msPerBotSec</c>, <c>msSec / n</c>: the number the owner asked for.</summary>
    public double? MsPerBotSec { get; }

    /// <summary><c>callsPerBotSec</c>: the callbacks per bot-second, counting idle visits.</summary>
    public double? CallsPerBotSec { get; }

    /// <summary><c>usPerCall</c>: the charged µs per callback.</summary>
    public double? UsPerCall { get; }

    /// <summary><c>allocKBPerBotSec</c>: the KB (1,024 bytes) allocated per bot-second.</summary>
    public double? AllocKBPerBotSec { get; }

    /// <summary>The spread of the kind's bots charged in the window (3.10); null with none, and for a kind that is no
    /// bot kind.</summary>
    public BotCpuSpread? Spread { get; }

    /// <summary><c>p50</c> of the line: null with no bot-seconds (<c>-</c>, 2.1) or no spread (3.10).</summary>
    public double? P50 => BotSeconds > 0 ? Spread?.P50 : null;

    /// <summary><c>p95</c> of the line: null with no bot-seconds (<c>-</c>, 2.1) or no spread (3.10).</summary>
    public double? P95 => BotSeconds > 0 ? Spread?.P95 : null;

    /// <summary><c>max</c> of the line: null with no bot-seconds (<c>-</c>, 2.1) or no spread (3.10).</summary>
    public double? Max => BotSeconds > 0 ? Spread?.Max : null;

    /// <summary>
    /// The line's stage values, by <see cref="BotCpuStage"/>: ms per bot per s, adding up to <see cref="MsPerBotSec"/>,
    /// or ms per s for unattributed, adding up to <see cref="MsSec"/>. Null with no bot-seconds: the line then has no
    /// part after <c>|</c> (2.1).
    /// </summary>
    public IReadOnlyList<double> StageValues { get; }

    /// <summary>The CSV's stage columns, by <see cref="BotCpuStage"/>: the whole kind's raw stage ticks ÷ the exact
    /// window length, ms per s, for every kind (2.3).</summary>
    public IReadOnlyList<double> StageMsSec { get; }

    /// <summary>A kind with nothing charged and <c>n</c> 0 writes no line or row (2.1).</summary>
    internal bool HasLine => Ticks > 0 || Calls > 0 || Bytes > 0 || N > 0;
}

/// <summary>
/// One window's numbers (2.1, 2.2): its length, the game ticks, the charged share of the process line, the kinds that
/// write a line, the top 5 of each bot kind and the compare pairs. The process values read from the platform (3.9) are
/// not here.
/// </summary>
public sealed class BotCpuWindow
{
    private readonly BotCpuKindWindow[] _kinds;
    private readonly IReadOnlyList<BotCpuTopEntry>[] _top;

    internal BotCpuWindow(double seconds, double gameTicks, BotCpuKindWindow[] kinds,
        IReadOnlyList<BotCpuTopEntry>[] top, IReadOnlyList<BotCpuCompare> compares)
    {
        Seconds = seconds;
        Win = Math.Max(1, (int)BotCpuReport.Round(seconds, 0));
        GameTicks = gameTicks;
        Tps = gameTicks / seconds;
        _kinds = kinds;
        _top = top;
        Kinds = kinds.Where(kind => kind.HasLine).ToArray();
        double allMsSec = kinds.Sum(kind => kind.MsSec);
        ChargedPct = allMsSec / 10; // 1,000 ms a second is 100% of one core
        CallsSec = kinds.Sum(kind => kind.CallsSec);
        Compares = compares;
    }

    /// <summary>The window's exact <see cref="System.Diagnostics.Stopwatch"/> length, which every per-second figure
    /// divides by.</summary>
    public double Seconds { get; }

    /// <summary><c>win</c>: <see cref="Seconds"/> rounded, at least 1.</summary>
    public int Win { get; }

    /// <summary>The game-loop ticks in the window: the change of game time ÷ the tick's duration (3.8).</summary>
    public double GameTicks { get; }

    /// <summary><c>tps</c>: game-loop ticks per second.</summary>
    public double Tps { get; }

    /// <summary><c>chargedPct</c>: all time charged to all kinds, unattributed included, as % of one core.</summary>
    public double ChargedPct { get; }

    /// <summary><c>callsSec</c> of the process line: all metered callbacks per second.</summary>
    public double CallsSec { get; }

    /// <summary>The kinds that write a line and a CSV row, in kind order (2.5).</summary>
    public IReadOnlyList<BotCpuKindWindow> Kinds { get; }

    /// <summary>The compare pairs written, in the order playerbot, piloted, player (2.1).</summary>
    public IReadOnlyList<BotCpuCompare> Compares { get; }

    /// <summary>The figures of <paramref name="kind"/>, or null when it writes no line.</summary>
    public BotCpuKindWindow Kind(BotCpuKind kind)
    {
        BotCpuKindWindow figures = _kinds[(int)kind];
        return figures.HasLine ? figures : null;
    }

    /// <summary>The top 5 of <paramref name="kind"/>, most costly first; empty when none of its bots was charged, and
    /// for a kind that is no bot kind.</summary>
    public IReadOnlyList<BotCpuTopEntry> Top(BotCpuKind kind) => _top[(int)kind];
}

/// <summary>
/// The totals since on, or since reset (2.4): per kind, the charged ms and the bot-seconds of every window written
/// since then, and those windows' length. Immutable: <see cref="Add"/> gives new totals.
/// </summary>
public sealed class BotCpuSince
{
    private readonly double[] _chargedMs;
    private readonly double[] _botSeconds;

    private BotCpuSince(bool reset, int windows, double seconds, double[] chargedMs, double[] botSeconds)
    {
        Reset = reset;
        Windows = windows;
        Seconds = seconds;
        _chargedMs = chargedMs;
        _botSeconds = botSeconds;
    }

    /// <summary>No window yet: at on, or at <c>/botcpu reset</c> (<paramref name="reset"/> true).</summary>
    public static BotCpuSince Start(bool reset) =>
        new(reset, 0, 0, new double[BotCpuTotals.KindCount], new double[BotCpuTotals.KindCount]);

    /// <summary>True after a reset: the chat line says "Since reset".</summary>
    public bool Reset { get; }

    /// <summary>The windows written; with none the chat line is left out.</summary>
    public int Windows { get; }

    /// <summary>Those windows' exact length.</summary>
    public double Seconds { get; }

    /// <summary>Those windows' length in minutes, the chat line's <c>N</c>.</summary>
    public double Minutes => Seconds / 60;

    public double ChargedMs(BotCpuKind kind) => _chargedMs[(int)kind];

    public double BotSeconds(BotCpuKind kind) => _botSeconds[(int)kind];

    /// <summary>The kind's charged ms ÷ its bot-seconds over the windows; null when it has no bot-seconds.</summary>
    public double? MsPerBotSec(BotCpuKind kind) =>
        _botSeconds[(int)kind] > 0 ? _chargedMs[(int)kind] / _botSeconds[(int)kind] : null;

    /// <summary>These totals and a written window; a null window (a final window too short to write, 2.2) adds
    /// nothing.</summary>
    public BotCpuSince Add(BotCpuWindow window)
    {
        if (window == null)
            return this;

        double[] chargedMs = (double[])_chargedMs.Clone();
        double[] botSeconds = (double[])_botSeconds.Clone();
        foreach (BotCpuKindWindow kind in window.Kinds)
        {
            chargedMs[(int)kind.Kind] += kind.ChargedMs;
            botSeconds[(int)kind.Kind] += kind.BotSeconds;
        }

        return new BotCpuSince(Reset, Windows + 1, Seconds + window.Seconds, chargedMs, botSeconds);
    }
}

/// <summary>
/// The report's numbers (2.1, 2.2, 2.4, 3.8, 3.10): plain values in and out, no server state.
/// </summary>
public static class BotCpuReport
{
    /// <summary>A final window shorter than this writes no lines or rows, and its counts are dropped (2.2).</summary>
    public const double MinimumWindowSeconds = 5;

    /// <summary>The bots in a kind's top (3.10).</summary>
    public const int TopCount = 5;

    /// <summary>A compare pair is written when both kinds have <c>n</c> of at least this, as the line shows it (2.1).
    /// </summary>
    public const double CompareMinimumN = 1.0;

    // The compare pairs, in their order (2.1); the other kind of each is gamebot.
    private static readonly BotCpuKind[] ComparedKinds = [BotCpuKind.Playerbot, BotCpuKind.Piloted, BotCpuKind.Player];

    /// <summary>
    /// A window's counts (3.7, step 3): <paramref name="later"/> less <paramref name="earlier"/>, value by value, a
    /// negative difference counted as 0. A slot that appeared between the two counts from zero, as the earlier totals
    /// never held it.
    /// </summary>
    public static BotCpuTotals Difference(BotCpuTotals later, BotCpuTotals earlier)
    {
        ArgumentNullException.ThrowIfNull(later);
        ArgumentNullException.ThrowIfNull(earlier);
        BotCpuTotals window = new();
        for (int k = 0; k < BotCpuTotals.KindCount; k++)
        {
            BotCpuKind kind = (BotCpuKind)k;
            for (int s = 0; s < BotCpuTotals.StageCount; s++)
            {
                BotCpuStage stage = (BotCpuStage)s;
                window.Add(kind, stage, Positive(later.Ticks(kind, stage) - earlier.Ticks(kind, stage)),
                    Positive(later.Calls(kind, stage) - earlier.Calls(kind, stage)),
                    Positive(later.Bytes(kind, stage) - earlier.Bytes(kind, stage)));
            }

            window.AddPresence(kind, Positive(later.Presence(kind) - earlier.Presence(kind)));
        }

        return window;
    }

    private static long Positive(long value) => Math.Max(0, value);

    /// <summary>
    /// A bot's ticks and calls since the report last read its counter (3.10), a negative difference counted as 0; the
    /// values read become the counter's <see cref="BotCpuCounter.LastTicks"/> and
    /// <see cref="BotCpuCounter.LastCalls"/>, which only the report writes. The loop threads'
    /// <see cref="BotCpuCounter.Ticks"/> and <see cref="BotCpuCounter.Calls"/> are only read. A bot gone from the table
    /// is simply not listed: its counter goes with it and leaves nothing behind.
    /// </summary>
    public static (long Ticks, long Calls) TakeCounterWindow(BotCpuCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        long ticks = Volatile.Read(ref counter.Ticks);
        long calls = Volatile.Read(ref counter.Calls);
        (long Ticks, long Calls) window = (Positive(ticks - counter.LastTicks), Positive(calls - counter.LastCalls));
        counter.LastTicks = ticks;
        counter.LastCalls = calls;
        return window;
    }

    /// <summary>
    /// <paramref name="value"/> as the report writes it with <paramref name="decimals"/> decimals (2.1): its
    /// invariant-culture fixed-point text, read back. So a rank, a threshold or a ratio taken from it agrees with what
    /// the lines show. That text rounds the double's exact value, and an exact midpoint to the even digit (0.125 is
    /// 0.12).
    /// </summary>
    public static double Round(double value, int decimals) =>
        double.Parse(value.ToString("F" + decimals, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// The spread of per-bot values (3.10), nearest rank: sorted ascending, <c>p50</c> is the value at rank ⌈0.50·m⌉,
    /// <c>p95</c> at ⌈0.95·m⌉ and <c>max</c> the largest. Null with no value.
    /// </summary>
    public static BotCpuSpread? Spread(IEnumerable<double> values)
    {
        double[] sorted = values?.ToArray() ?? [];
        if (sorted.Length == 0)
            return null;

        Array.Sort(sorted);
        int m = sorted.Length;
        // Ranks from 1, in integers: ⌈50·m / 100⌉ and ⌈95·m / 100⌉.
        return new BotCpuSpread(sorted[(50 * m + 99) / 100 - 1], sorted[(95 * m + 99) / 100 - 1], sorted[m - 1]);
    }

    /// <summary>A bot's name as <c>BOT_CPU_TOP</c> writes it (2.1): a space or <c>:</c> becomes <c>_</c>; none is
    /// <c>-</c>.</summary>
    public static string TopName(string name) =>
        string.IsNullOrEmpty(name) ? "-" : name.Replace(' ', '_').Replace(':', '_');

    /// <summary>
    /// One window's numbers (2.1, 2.2, 3.8, 3.10), or null for a window shorter than
    /// <see cref="MinimumWindowSeconds"/>, which writes nothing.
    /// </summary>
    /// <param name="counts">The window's counts, from <see cref="Difference"/>.</param>
    /// <param name="seconds">The window's exact <see cref="System.Diagnostics.Stopwatch"/> length.</param>
    /// <param name="frequency"><see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    /// <param name="gameTimeChange">The change of <c>GameLoop.GameLoopTime</c> over the window, ms.</param>
    /// <param name="tickDuration"><c>GameLoop.TickDuration</c>, ms.</param>
    /// <param name="gamebots">The window's registry samples, for GameBot <c>n</c>.</param>
    /// <param name="bots">The bots listed from the per-bot table, with their window's ticks and calls.</param>
    public static BotCpuWindow Window(BotCpuTotals counts, double seconds, long frequency, long gameTimeChange,
        double tickDuration, BotCpuSamples gamebots, IEnumerable<BotCpuBot> bots)
    {
        if (!(seconds >= MinimumWindowSeconds))
            return null;

        ArgumentNullException.ThrowIfNull(counts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        double gameTicks = gameTimeChange > 0 && tickDuration > 0 ? gameTimeChange / tickDuration : 0;
        // The bots charged at least once in the window (3.10); a bot present but never charged is left out.
        BotCpuBot[] charged = bots?.Where(bot => bot != null && bot.Calls > 0 && BotCpuMeter.IsBotKind(bot.Kind))
            .ToArray() ?? [];

        BotCpuKindWindow[] kinds = new BotCpuKindWindow[BotCpuTotals.KindCount];
        IReadOnlyList<BotCpuTopEntry>[] top = new IReadOnlyList<BotCpuTopEntry>[BotCpuTotals.KindCount];
        for (int k = 0; k < BotCpuTotals.KindCount; k++)
        {
            BotCpuKind kind = (BotCpuKind)k;
            double? n = kind switch
            {
                BotCpuKind.Unattributed => null,
                BotCpuKind.Gamebot => gamebots.Average,
                // Presence: one per client or live brain per tick, over the ticks (3.8).
                _ => gameTicks > 0 ? counts.Presence(kind) / gameTicks : 0,
            };

            // Per bot: its ticks as ms per second of the window. The top is ranked by that value as the line shows it,
            // 3 decimals, so bots that show the same value are in name order (2.1).
            BotCpuTopEntry[] perBot = charged.Where(bot => bot.Kind == kind)
                .Select(bot => new BotCpuTopEntry(0, bot, bot.Ticks * 1000.0 / frequency / seconds,
                    bot.Calls / seconds))
                .ToArray();
            top[k] = perBot.OrderByDescending(entry => Round(entry.MsSec, 3))
                .ThenBy(entry => entry.Bot.Name, StringComparer.Ordinal)
                .Take(TopCount)
                .Select((entry, index) => entry with { Rank = index + 1 })
                .ToArray();
            kinds[k] = new BotCpuKindWindow(kind, n, counts, seconds, frequency,
                Spread(perBot.Select(entry => entry.MsSec)));
        }

        List<BotCpuCompare> compares = [];
        BotCpuKindWindow gamebot = kinds[(int)BotCpuKind.Gamebot];
        foreach (BotCpuKind kind in ComparedKinds)
        {
            BotCpuKindWindow other = kinds[(int)kind];
            if (!(Round(other.N ?? 0, 1) >= CompareMinimumN && Round(gamebot.N ?? 0, 1) >= CompareMinimumN))
                continue;

            // The fields with the decimals their lines show (2.1).
            compares.Add(new BotCpuCompare(kind, other.N.Value, gamebot.N.Value,
                Ratio(other.MsPerBotSec, gamebot.MsPerBotSec, 3),
                Ratio(other.AllocKBPerBotSec, gamebot.AllocKBPerBotSec, 1),
                Ratio(other.CallsPerBotSec, gamebot.CallsPerBotSec, 1),
                Ratio(other.UsPerCall, gamebot.UsPerCall, 2)));
        }

        return new BotCpuWindow(seconds, gameTicks, kinds, top, compares);
    }

    // A compare ratio of two fields as their lines show them: - when either is - or the gamebot value is 0 (2.1).
    private static double? Ratio(double? value, double? gamebot, int decimals)
    {
        if (value == null || gamebot == null)
            return null;

        double divisor = Round(gamebot.Value, decimals);
        return divisor == 0 ? null : Round(value.Value, decimals) / divisor;
    }
}
