using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's hot path (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). While the
// meter is on, each game-loop callback is timed and charged to the kind of its owner (2.5, 3.3) and to its stage (3.4),
// so the owner can compare the average CPU of each kind of bot. Hook H1 in GameLoop.ExecuteForEach hands each call to
// BotCpuMeter.ExecuteForEach while the meter is on (3.1). BotCpuMeter has no static field initializer and no static
// constructor (5.1): nothing in it runs or allocates while the meter is off.

/// <summary>
/// The player-bot contract (2.5): a <see cref="GamePlayer"/> whose client implements this marker is counted as
/// <see cref="BotCpuKind.Playerbot"/>, with no meter change. Deriving the client from <see cref="BotDummyClient"/>
/// also works.
/// </summary>
public interface IPlayerBotClient { }

/// <summary>What an item is charged to (2.5), in the report's kind order. The text name is the lower-case member name.
/// </summary>
public enum BotCpuKind
{
    /// <summary>A <see cref="GameBot"/> with <see cref="GameBot.IsAutonomousWorldBot"/>.</summary>
    Gamebot,
    /// <summary>Any other <see cref="GameBot"/>: companions and temporary helpers.</summary>
    Companion,
    /// <summary>A <see cref="GamePlayer"/> whose client is an <see cref="IPlayerBotClient"/> or a
    /// <see cref="BotDummyClient"/>.</summary>
    Playerbot,
    /// <summary>A <see cref="GamePlayer"/> the <c>/bot</c> pilot drives (<see cref="AutonomousPlayerPilot.IsActive"/>).
    /// </summary>
    Piloted,
    /// <summary>Any other <see cref="GamePlayer"/>.</summary>
    Player,
    /// <summary>Any other living: monsters, guards, merchants, keep doors.</summary>
    Monster,
    /// <summary>No owner, an owner that is not living, or a pet walk longer than
    /// <see cref="BotCpuMeter.PetWalkSteps"/>.</summary>
    Unattributed,
}

/// <summary>
/// A game-loop stage (3.4), in the CSV's column order (2.3). The text name is the member name. <see cref="Fleet"/> is
/// the GameBot fleet planning scope (3.5) and <see cref="Pets"/> the work of pets in any stage: neither has a profile
/// key.
/// </summary>
public enum BotCpuStage
{
    GameLoop,
    Timer,
    ClientBegin,
    Npc,
    Fleet,
    Attack,
    Casting,
    Effect,
    EffectListBegin,
    EffectListEnd,
    Movement,
    Crafting,
    Reaper,
    Zone,
    ClientEnd,
    Quests,
    Pets,
    Other,
}

/// <summary>
/// What one game-loop item is charged to (3.2 step 1): the <paramref name="Kind"/> and <paramref name="Stage"/> of its
/// slot cell, the <paramref name="Bot"/> whose per-bot counter it adds to (3.10), and whether it counts one toward its
/// kind's <c>n</c> (<paramref name="Presence"/>, 3.8).
/// </summary>
/// <param name="Kind">The kind of the item's owner (3.3).</param>
/// <param name="Stage">The stage of the call, <see cref="BotCpuStage.Pets"/> for a pet's item, or
/// <see cref="BotCpuStage.Fleet"/> for an item with no owner inside the fleet scope (3.5).</param>
/// <param name="Bot">The owner, for a bot kind (<see cref="BotCpuMeter.IsBotKind"/>); null for any other kind and for
/// <see cref="BotCpuStage.Fleet"/>, which counts for no bot.</param>
/// <param name="Presence">A brain in <see cref="BotCpuStage.Npc"/> or a client in
/// <see cref="BotCpuStage.ClientBegin"/>, not a pet, of a kind whose <c>n</c> is its presence: companion, monster and
/// the three player kinds.</param>
public readonly record struct BotCpuCharge(BotCpuKind Kind, BotCpuStage Stage, GameObject Bot, bool Presence);

/// <summary>
/// Raw totals of the per-thread slots (3.7): for each kind and stage the <see cref="Stopwatch"/> ticks, calls and bytes
/// charged, and for each kind its presence count (3.8). A slot is a <c>long[]</c> of <see cref="SlotLength"/> longs:
/// the 7 × 18 cells of 3 values (ticks, calls, bytes) from <see cref="CellIndex"/>, then the 7 presence counts at
/// <see cref="PresenceIndex"/>, with <see cref="SlotPadding"/> unused longs at each end, so two threads' slots never
/// share a cache line. <see cref="BotCpuMeter.TakeSnapshot"/> sums every slot into one of these; a window is the
/// difference of two (3.7).
/// </summary>
public sealed class BotCpuTotals
{
    /// <summary>The kinds (2.5).</summary>
    public const int KindCount = 7;
    /// <summary>The stages (3.4).</summary>
    public const int StageCount = 18;
    /// <summary>The unused longs at each end of a slot: 64 bytes.</summary>
    public const int SlotPadding = 8;
    /// <summary>7 kinds × 18 stages × 3 values and 7 presence counts, 385 longs, between the padding: 401 longs, about
    /// 3.2 KB.</summary>
    public const int SlotLength = SlotPadding + KindCount * StageCount * 3 + KindCount + SlotPadding;

    private const int PresenceStart = SlotPadding + KindCount * StageCount * 3;

    // The values, laid out as in a slot.
    private readonly long[] _values = new long[SlotLength];

    /// <summary>The index of the ticks of [<paramref name="kind"/>, <paramref name="stage"/>] in a slot; its calls are
    /// at the next index and its bytes at the one after.</summary>
    public static int CellIndex(BotCpuKind kind, BotCpuStage stage) =>
        SlotPadding + ((int)kind * StageCount + (int)stage) * 3;

    /// <summary>The index of the presence count of <paramref name="kind"/> in a slot.</summary>
    public static int PresenceIndex(BotCpuKind kind) => PresenceStart + (int)kind;

    public long Ticks(BotCpuKind kind, BotCpuStage stage) => _values[CellIndex(kind, stage)];

    public long Calls(BotCpuKind kind, BotCpuStage stage) => _values[CellIndex(kind, stage) + 1];

    public long Bytes(BotCpuKind kind, BotCpuStage stage) => _values[CellIndex(kind, stage) + 2];

    public long Presence(BotCpuKind kind) => _values[PresenceIndex(kind)];

    /// <summary>Adds to the [<paramref name="kind"/>, <paramref name="stage"/>] cell: the report builds its windows
    /// from plain values.</summary>
    public void Add(BotCpuKind kind, BotCpuStage stage, long ticks, long calls, long bytes)
    {
        int cell = CellIndex(kind, stage);
        _values[cell] += ticks;
        _values[cell + 1] += calls;
        _values[cell + 2] += bytes;
    }

    /// <summary>Adds to the presence count of <paramref name="kind"/>.</summary>
    public void AddPresence(BotCpuKind kind, long count) => _values[PresenceIndex(kind)] += count;

    /// <summary>
    /// The sums of <paramref name="slots"/> (3.7, a window's step 2): each value read with
    /// <see cref="Volatile.Read(ref long)"/>, as its thread may be adding to it (aligned 64-bit reads are atomic on the
    /// 64-bit server). A slot is never written.
    /// </summary>
    public static BotCpuTotals Sum(IEnumerable<long[]> slots)
    {
        BotCpuTotals totals = new();
        foreach (long[] slot in slots)
        {
            for (int i = SlotPadding; i < SlotLength - SlotPadding; i++)
                totals._values[i] += Volatile.Read(ref slot[i]);
        }

        return totals;
    }
}

/// <summary>
/// One bot's counter (3.10): the <see cref="Stopwatch"/> ticks and the calls charged to the bot, added with
/// <see cref="Interlocked.Add(ref long, long)"/> by the loop threads (one bot's effects can run on two threads at
/// once), and the values the report last read, written by the report only. Counters are small objects allocated one
/// after another, so the two hot fields sit between 64 bytes of padding on each side (explicit field offsets, as the
/// pool's own <c>WorkState</c> does, MT:45-50): two bots' counters never share a cache line. Under 200 bytes a bot.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public sealed class BotCpuCounter
{
    /// <summary>The ticks charged to the bot.</summary>
    [FieldOffset(64)] public long Ticks;
    /// <summary>The callbacks charged to the bot.</summary>
    [FieldOffset(72)] public long Calls;
    /// <summary>The <see cref="Ticks"/> the report read last.</summary>
    [FieldOffset(144)] public long LastTicks;
    /// <summary>The <see cref="Calls"/> the report read last.</summary>
    [FieldOffset(152)] public long LastCalls;
    /// <summary>The bot's kind when its counter was made.</summary>
    [FieldOffset(160)] public readonly BotCpuKind Kind;

    public BotCpuCounter(BotCpuKind kind) => Kind = kind;
}

/// <summary>The meter's hot path: the switch and its runs, the wrapper hook H1 hands each call to, the per-thread slots
/// and the per-bot counters it charges, the stage of a profile key, the kind of an owner and what each item charges.
/// </summary>
public static class BotCpuMeter
{
    /// <summary>The most owners the pet walk follows (3.3); a longer walk, a cycle, is
    /// <see cref="BotCpuKind.Unattributed"/>.</summary>
    public const int PetWalkSteps = 8;

    /// <summary>
    /// The switch (3.11, 4.4): a plain static bool, false by default, which hook H1 reads on every
    /// <c>GameLoop.ExecuteForEach</c> call, so off costs one bool read. Written with
    /// <see cref="Volatile.Write(ref bool, bool)"/> by <see cref="Enable"/> and <see cref="Disable"/> only.
    /// </summary>
    public static bool Enabled;

    // The slot of every thread that ran a metered item, under the list's own lock (3.7): made by the first Enable and
    // kept. A slot is never removed or zeroed; a restarted worker gets a new one and the old one keeps its totals.
    private static List<long[]> _slots;

    // This thread's slot, written only by this thread, with plain adds; null until its first metered item.
    [ThreadStatic] private static long[] _threadSlot;

    // The run's per-bot counters (3.10), a new table at each Enable; an entry goes with its bot.
    private static ConditionalWeakTable<GameObject, BotCpuCounter> _counters;

    // The run number, counted up by Enable (3.7).
    private static int _run;

    // 1 once the setup fallback has written its warning in this run (3.1, 5.1).
    private static int _setupWarned;

    // A test hook (6.1, "Setup fallback"): set, it replaces StageOf in a call's setup. Null on the server.
    private static Func<string, BotCpuStage> _stageHook;

    /// <summary>The number of the current run, or of the last one once off; 0 before the first.</summary>
    public static int RunNumber => Volatile.Read(ref _run);

    /// <summary>
    /// Turns the meter on, for the report side, in an open job (3.7): first the slot list (the first time), a new
    /// per-bot table and the next run number, so no wrapper runs before they exist, then <see cref="Enabled"/>.
    /// Returns the new run's number.
    /// </summary>
    public static int Enable()
    {
        if (Volatile.Read(ref _slots) == null)
            Interlocked.CompareExchange(ref _slots, new List<long[]>(), null);

        Volatile.Write(ref _counters, new ConditionalWeakTable<GameObject, BotCpuCounter>());
        Volatile.Write(ref _setupWarned, 0);
        int run = Interlocked.Increment(ref _run);
        Volatile.Write(ref Enabled, true);
        return run;
    }

    /// <summary>
    /// Turns the meter off (3.7, 4.4): the next <c>GameLoop.ExecuteForEach</c> call runs unwrapped. Wrappers already
    /// in flight still add their items. The slots and the per-bot table stay, for the report's last window.
    /// </summary>
    public static void Disable() => Volatile.Write(ref Enabled, false);

    /// <summary>
    /// The per-bot counters of the current run, or of the last one once off (3.10), for the report: each bot charged
    /// in the run, while it lives, with its counter. Empty before the first run.
    /// </summary>
    public static IEnumerable<KeyValuePair<GameObject, BotCpuCounter>> Counters() =>
        Volatile.Read(ref _counters) ?? (IEnumerable<KeyValuePair<GameObject, BotCpuCounter>>)[];

    /// <summary>
    /// The sums of every thread's slot (3.7, a window's steps 1-2): the slot list copied under its lock, then each
    /// value read with <see cref="Volatile.Read(ref long)"/>, without a lock. Never writes a slot. All zero before the
    /// first run.
    /// </summary>
    public static BotCpuTotals TakeSnapshot()
    {
        List<long[]> slots = Volatile.Read(ref _slots);
        if (slots == null)
            return new BotCpuTotals();

        long[][] copy;
        lock (slots)
            copy = slots.ToArray();
        return BotCpuTotals.Sum(copy);
    }

    /// <summary>
    /// Hook H1's call while the meter is on (3.1). On the game-loop thread, once per call: reads the stage from
    /// <see cref="GameLoop.ActiveService"/>, builds one wrapper that carries it and the run's per-bot table to the
    /// worker threads, and hands the wrapper to the pool's own <see cref="GameLoopThreadPool.ExecuteForEach{T}"/>. A
    /// call with nothing to run is passed through unwrapped. The setup never throws: on an exception, after one
    /// warning, the pool gets the caller's own delegate, so each item still runs once, unmetered. An exception from the
    /// pool or a callback reaches the caller unchanged.
    /// </summary>
    public static void ExecuteForEach<T>(GameLoopThreadPool pool, List<T> items, int toExclusive, Action<T> action)
    {
        Action<T> run = action;
        try
        {
            // No table yet: Enabled was set without Enable, and nothing could be charged to a bot.
            ConditionalWeakTable<GameObject, BotCpuCounter> counters = Volatile.Read(ref _counters);
            if (items != null && Math.Min(items.Count, toExclusive) > 0 && counters != null)
            {
                string profileKey = GameLoop.ActiveService;
                BotCpuStage stage = _stageHook != null ? _stageHook(profileKey) : StageOf(profileKey);
                if ((uint)stage >= BotCpuTotals.StageCount)
                    stage = BotCpuStage.Other;
                run = new Metered<T>(action, stage, counters).Run;
            }
        }
        catch (Exception exception)
        {
            run = action;
            WarnSetup(exception);
        }

        pool.ExecuteForEach(items, toExclusive, run);
    }

    // The wrapper of one call (3.2), made on the game-loop thread with the call's stage and the run's per-bot table:
    // the worker threads read neither GameLoop.ActiveService nor a static.
    private sealed class Metered<T>
    {
        private readonly Action<T> _action;
        private readonly BotCpuStage _stage;
        private readonly ConditionalWeakTable<GameObject, BotCpuCounter> _counters;

        internal Metered(Action<T> action, BotCpuStage stage, ConditionalWeakTable<GameObject, BotCpuCounter> counters)
        {
            _action = action;
            _stage = stage;
            _counters = counters;
        }

        internal void Run(T item)
        {
            // 1. Before the callback, inside a try: this thread's slot on its first item, the owner, kind and stage,
            // and a bot's counter. An exception charges the item to unattributed, or, with no slot, leaves it
            // uncharged.
            long[] slot = null;
            int cell;
            int presence = -1;
            BotCpuCounter counter = null;
            try
            {
                slot = _threadSlot ?? RegisterSlot();
                BotCpuCharge charge = Classify(item, _stage, false, AutonomousPlayerPilot.IsActive);
                cell = BotCpuTotals.CellIndex(charge.Kind, charge.Stage);
                if (charge.Presence)
                    presence = BotCpuTotals.PresenceIndex(charge.Kind);
                if (charge.Bot != null)
                    counter = _counters.GetOrAdd(charge.Bot, static (_, kind) => new BotCpuCounter(kind), charge.Kind);
            }
            catch (Exception)
            {
                cell = BotCpuTotals.CellIndex(BotCpuKind.Unattributed, _stage);
                presence = -1;
                counter = null;
            }

            // 2-4. The callback, timed, with what it allocates; outside the try, so its exceptions reach the service as
            // today.
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            _action(item);
            long ticks = Stopwatch.GetTimestamp() - start;
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

            // 5-6. Plain adds to this thread's own slot, Interlocked ones to the bot's counter: neither can throw.
            if (slot != null)
            {
                slot[cell] += ticks;
                slot[cell + 1]++;
                slot[cell + 2] += bytes;
                if (presence >= 0)
                    slot[presence]++;
            }

            if (counter != null)
            {
                Interlocked.Add(ref counter.Ticks, ticks);
                Interlocked.Add(ref counter.Calls, 1);
            }
        }
    }

    // A thread's first metered item (3.7): its slot, added to the list under the list's lock, once per thread.
    private static long[] RegisterSlot()
    {
        long[] slot = new long[BotCpuTotals.SlotLength];
        List<long[]> slots = Volatile.Read(ref _slots);
        lock (slots)
            slots.Add(slot);
        _threadSlot = slot;
        return slot;
    }

    // The setup fallback's warning (3.1): at most one a run (5.1), its log call in its own try, as NLog here throws
    // from log calls. The logger is made here, not in a static field, so BotCpuMeter keeps no static initializer.
    private static void WarnSetup(Exception exception)
    {
        if (Interlocked.Exchange(ref _setupWarned, 1) != 0)
            return;

        try
        {
            Logger log = LoggerManager.Create(typeof(BotCpuMeter));
            if (log.IsWarnEnabled)
                log.Warn("Bot CPU meter: the setup of a game-loop call failed, so its items ran unmetered (later " +
                    $"failures in this run are not logged): {exception}");
        }
        catch (Exception)
        {
            // Nothing: the meter's own faults never reach the game loop.
        }
    }

    /// <summary>
    /// The stage of a <see cref="GameLoop.ActiveService"/> profile key (3.4): the service's class name, plus
    /// <c>.BeginTick</c> or <c>.EndTick</c> for those steps. An unknown, missing or empty key is
    /// <see cref="BotCpuStage.Other"/>. A switch, so there is no map to build and nothing runs before it is called.
    /// </summary>
    public static BotCpuStage StageOf(string profileKey) => profileKey switch
    {
        "GameLoopService" => BotCpuStage.GameLoop,
        "TimerService" => BotCpuStage.Timer,
        "ClientService.BeginTick" => BotCpuStage.ClientBegin,
        "NpcService" => BotCpuStage.Npc,
        "AttackService" => BotCpuStage.Attack,
        "CastingService" => BotCpuStage.Casting,
        "EffectService" => BotCpuStage.Effect,
        "EffectListService.BeginTick" => BotCpuStage.EffectListBegin,
        "EffectListService.EndTick" => BotCpuStage.EffectListEnd,
        "MovementService" => BotCpuStage.Movement,
        "CraftingService" => BotCpuStage.Crafting,
        "ReaperService" => BotCpuStage.Reaper,
        "ZoneService" => BotCpuStage.Zone,
        "ClientService.EndTick" => BotCpuStage.ClientEnd,
        "DailyQuestService" or "WeeklyQuestService" or "MonthlyQuestService" => BotCpuStage.Quests,
        _ => BotCpuStage.Other,
    };

    /// <summary>The kind of an owner (3.3), with the real pilot check, <see cref="AutonomousPlayerPilot.IsActive"/>.
    /// </summary>
    public static BotCpuKind KindOf(GameObject owner, out GameObject charged, out bool pet) =>
        KindOf(owner, AutonomousPlayerPilot.IsActive, out charged, out pet);

    /// <summary>
    /// The kind of an owner (3.3), tested in this order: a <see cref="GameBot"/>; a <see cref="GamePlayer"/>, asking
    /// <paramref name="isPiloted"/> only when its client is no bot client; a pet, walked up its owners to the first
    /// that is not a pet, following at most <see cref="PetWalkSteps"/> of them; any other living; anything else or
    /// none. <paramref name="charged"/> is the object whose kind it is (for a pet, the walk's end), null when
    /// <see cref="BotCpuKind.Unattributed"/>; <paramref name="pet"/> says the walk followed at least one owner to get
    /// there. Upstream's <c>GetLivingOwner</c> is not used: it gives a companion's pets to its player and can throw.
    /// </summary>
    public static BotCpuKind KindOf(GameObject owner, Func<GamePlayer, bool> isPiloted, out GameObject charged,
        out bool pet)
    {
        GameObject current = owner;
        pet = false;
        for (int steps = 0; ; steps++)
        {
            switch (current)
            {
                case GameBot bot: // First: a GameBot's BotBrain is itself an IControlledBrain (bots/BotBrain.cs:21).
                    charged = bot;
                    return bot.IsAutonomousWorldBot ? BotCpuKind.Gamebot : BotCpuKind.Companion;
                case GamePlayer player:
                    charged = player;
                    if (player.Client is IPlayerBotClient or BotDummyClient)
                        return BotCpuKind.Playerbot;
                    return isPiloted != null && isPiloted(player) ? BotCpuKind.Piloted : BotCpuKind.Player;
                case GameNPC npc when npc.Brain is IControlledBrain { Owner: { } next }:
                    if (steps == PetWalkSteps)
                    {
                        charged = null;
                        pet = false;
                        return BotCpuKind.Unattributed;
                    }

                    current = next;
                    pet = true;
                    break;
                case GameLiving living:
                    charged = living;
                    return BotCpuKind.Monster;
                default: // none, or not living: only the first owner, as every owner a walk follows is living
                    charged = null;
                    return BotCpuKind.Unattributed;
            }
        }
    }

    /// <summary>The kinds whose bots have a per-bot counter, a spread and a top 5 (3.10): gamebot, companion,
    /// playerbot and piloted.</summary>
    public static bool IsBotKind(BotCpuKind kind) =>
        kind is BotCpuKind.Gamebot or BotCpuKind.Companion or BotCpuKind.Playerbot or BotCpuKind.Piloted;

    /// <summary>
    /// What one item of a game-loop call is charged to (3.3, 3.8). Its owner is read as its type
    /// <typeparamref name="T"/> says (3.3's table; a posted action, an <c>int</c> or any other type has none), then the
    /// kind of that owner by the pure
    /// <see cref="KindOf(GameObject, Func{GamePlayer, bool}, out GameObject, out bool)"/> with
    /// <paramref name="isPiloted"/>. A pet's item goes to <see cref="BotCpuStage.Pets"/>, whatever
    /// <paramref name="stage"/>; an item with no owner inside the fleet scope (<paramref name="inFleet"/>, 3.5) to
    /// <see cref="BotCpuKind.Gamebot"/> / <see cref="BotCpuStage.Fleet"/>. Called before the item's callback runs, so
    /// the owner is read while the item is intact; it throws only if a game object's member does (<c>Body</c> is
    /// virtual), and the wrapper's <c>try</c> charges such an item to unattributed (3.2).
    /// </summary>
    public static BotCpuCharge Classify<T>(T item, BotCpuStage stage, bool inFleet, Func<GamePlayer, bool> isPiloted)
    {
        Func<T, GameObject> owner = ItemOwner<T>.Read;
        BotCpuKind kind = KindOf(owner?.Invoke(item), isPiloted, out GameObject charged, out bool pet);
        if (pet)
            stage = BotCpuStage.Pets;
        else if (inFleet && kind == BotCpuKind.Unattributed)
        {
            kind = BotCpuKind.Gamebot;
            stage = BotCpuStage.Fleet;
        }

        // Presence (3.8) only in the item type's own stage, so a pet's item (now Pets) and fleet work never count; nor
        // a gamebot, whose n comes from the registry, nor unattributed, which has none.
        bool presence = stage == ItemOwner<T>.PresenceStage &&
            kind is not (BotCpuKind.Gamebot or BotCpuKind.Unattributed);
        return new BotCpuCharge(kind, stage, IsBotKind(kind) ? charged : null, presence);
    }

    /// <summary>
    /// The owner reader of item type <typeparamref name="T"/> (3.3) and the stage in which its items count presence
    /// (3.8), chosen once per type when an item of it is first classified: only while the meter is on (5.1). A reader
    /// of a type also reads the types derived from it (<see cref="Func{T, TResult}"/> is contravariant); a value type
    /// has none, so it is never boxed.
    /// </summary>
    private static class ItemOwner<T>
    {
        internal static readonly Func<T, GameObject> Read = (Func<T, GameObject>)OwnerReader(typeof(T));

        internal static readonly BotCpuStage? PresenceStage =
            Is<ABrain>(typeof(T)) ? BotCpuStage.Npc : Is<GameClient>(typeof(T)) ? BotCpuStage.ClientBegin : null;
    }

    private static bool Is<TItem>(Type type) => typeof(TItem).IsAssignableFrom(type);

    // The owner of each item type in 3.3's table; each reader gives none for a null item.
    private static Delegate OwnerReader(Type type) => type switch
    {
        _ when Is<ABrain>(type) => (Func<ABrain, GameObject>)(static brain => brain?.Body),
        _ when Is<AttackComponent>(type) => (Func<AttackComponent, GameObject>)(static attack => attack?.owner),
        _ when Is<CastingComponent>(type) => (Func<CastingComponent, GameObject>)(static casting => casting?.Owner),
        _ when Is<ECSGameEffect>(type) => (Func<ECSGameEffect, GameObject>)(static effect => effect?.Owner),
        _ when Is<EffectListComponent>(type) => (Func<EffectListComponent, GameObject>)(static list => list?.Owner),
        _ when Is<MovementComponent>(type) => (Func<MovementComponent, GameObject>)(static move => move?.Owner),
        _ when Is<CraftComponent>(type) => (Func<CraftComponent, GameObject>)(static craft => craft?.Owner),
        _ when Is<ECSGameTimer>(type) => (Func<ECSGameTimer, GameObject>)(static timer => timer?.Owner),
        _ when Is<SubZoneObject>(type) => (Func<SubZoneObject, GameObject>)(static node => node?.Node?.Value),
        _ when Is<LivingBeingKilled>(type) => (Func<LivingBeingKilled, GameObject>)KillOwner,
        _ when Is<GameClient>(type) => (Func<GameClient, GameObject>)ClientOwner,
        _ => null,
    };

    // The Reaper rule (3.3): XP, loot and quest credit run for the killer, so a kill is its killer's when the killer's
    // kind, after the pet walk, is a bot or player kind; otherwise the killed's. Piloted or not changes nothing here,
    // so no pilot check is asked; Classify then walks the killer again, for its kind and its Pets stage.
    private static GameObject KillOwner(LivingBeingKilled kill)
    {
        if (kill == null)
            return null;

        GameObject killer = kill.Killer;
        return KindOf(killer, null, out _, out _) is BotCpuKind.Gamebot or BotCpuKind.Companion or
            BotCpuKind.Playerbot or BotCpuKind.Piloted or BotCpuKind.Player ? killer : kill.Killed;
    }

    // A client's owner (3.3): its player while the client IsPlaying (Playing or Linkdead) and the player is Active;
    // none while it loads (WorldEnter) or at the character screen, where it keeps its old player.
    private static GameObject ClientOwner(GameClient client)
    {
        GamePlayer player = client?.Player;
        return player != null && client.IsPlaying && player.ObjectState == GameObject.eObjectState.Active
            ? player
            : null;
    }
}
