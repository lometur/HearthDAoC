using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using DOL.Database;
using DOL.Events;
using DOL.GS.ServerProperties;
using DOL.Logging;
using static System.FormattableString;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC proto (#137): creates the missing player bots of HEARTHDAOC_PLAYER_BOTS at start and logs them in a few per
// second, the way AutonomousPopulationController does for GameBots (bots/autonomous/AutonomousPopulationController.cs:
// 46-70, :126, :157): a thread-pool timer paces and observes, and the game loop does the work (posted to NpcService).
// "off", the default, does nothing at all. Any other value is test-world mode: Discord webhooks off for this run, a
// status line every minute, and one forced full collection at minute 51 for retained heap per bot (the meter has no
// /botcpu heap, spec 4.5). A bot that drops out of Playing is never logged in again (F1 must stay visible); the one
// exception is the quest fighter's single re-login after its errands, in run 1's small plans. HEARTHDAOC_PLAYER_BOT_WAKE
// (gamebot, the default: the #137 lever, PlayerBotWake; or player) and HEARTHDAOC_PLAYER_BOT_SPOTS (a start spots file,
// PlayerBotSpots) are read only when bots are on, so a default start logs nothing (smoke.sh:56); a bad value or an
// unreadable file keeps them off. A bot placed at its spot holds it (PlayerBotState.HoldAnchor). A spot that carries a
// GameBot's level, class and race makes a level-matched bot (levelmatch/DESIGN.md 2.5): created at that build with its
// class's GameBot plan (PlayerBotKit), geared by the GameBot's rules at its first entered world (PlayerBotKitGear), and
// trained on each level-up.
//
// Lines marked "// T4" came with the driver (task 4), lines marked "// T5" with the errands (task 5).
public static class PlayerBotSpawner
{
    public const string LogPrefix = "HEARTHDAOC_PLAYER_BOTS";
    public const int LoginsPerSecond = 4;
    public const int MaxLoading = 8;
    public const int FirstTickMs = 5_000;
    public const int LoginTimeoutSeconds = 90;  // above HeadlessClient.LoginTimeoutMs (60 s), so the client's own reason is logged first
    public const int RetryAfterSeconds = 30;
    public const int MaxAttempts = 3;
    public const int StatusEverySeconds = 60;
    public const int HeapSnapshotMinute = 51;   // after the measured windows, minutes 20-50
    public const int MaxPlayersReserve = 50;    // bots count in ClientService.ClientCount, which max_players gates (LoginRequestHandler.cs:426)
    public const int ErrandMaxBots = 3;         // errands and their re-login only when the plan has 3 bots or fewer (run 1)
    private const long MB = 1024 * 1024;

    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotSpawner));
    private static readonly object Gate = new();
    private static Timer _timer;
    private static SlotState[] _slots = [];
    private static PlayerBotSpots _spots = PlayerBotSpots.None;
    private static DateTime _startedUtc, _nextStatusUtc;
    private static bool _readyLogged;
    private static int _ticking, _heapDone;

    public static PlayerBotPlan Plan { get; private set; } = PlayerBotPlan.Off;

    public enum SlotPhase { Pending, Loading, Playing, Relogging, Failed, Lost }

    public sealed class SlotState(PlayerBotSlot slot, bool selfTest)
    {
        public PlayerBotSlot Slot { get; } = slot;
        public bool SelfTest { get; } = selfTest;   // the first slot that is not the quest fighter
        public volatile HeadlessClient Client;      // set by StartOnGameLoop
        public volatile string Error;               // set by StartOnGameLoop when a step fails
        public volatile bool RelogRequested;        // set on the game loop when the errands are done
        public volatile bool ErrandRelogged;        // set by the timer before the re-login is posted
        public volatile bool Spotted;               // set by StartOnGameLoop: placed at its PLAYER_BOT_SPOTS spot
        public volatile PlayerBotKitPlan Kit;       // set by StartOnGameLoop, once per slot: a level-matched bot's plan
        public volatile bool Equipped;              // set by OnEnteredWorld: the kit's gear went on (not again on a re-login)
        public SlotPhase Phase;                     // the timer only, under Gate
        public int Attempts;
        public DateTime LoadingSinceUtc, RetryAtUtc;
        public string Name => Client?.Player?.Name ?? "-";
    }

    // GameServer.cs:510 raises Started after the properties load (:323) and before GameLoop.Init (:517).
    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        try
        {
            Plan = PlayerBotPlan.Parse(Environment.GetEnvironmentVariable(PlayerBotPlan.EnvironmentVariable));
        }
        catch (FormatException exception)
        {
            Log.Error($"{LogPrefix} error {exception.Message}; player bots stay off");
            return;
        }
        if (!Plan.On)
            return;
        try
        {
            PlayerBotWake.Lever = PlayerBotWake.Parse(Environment.GetEnvironmentVariable(PlayerBotWake.EnvironmentVariable));
            _spots = PlayerBotSpots.Load(Environment.GetEnvironmentVariable(PlayerBotSpots.EnvironmentVariable));
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"{LogPrefix} error {exception.Message}; player bots stay off");
            Plan = PlayerBotPlan.Off;
            return;
        }
        if (Properties.MAX_PLAYERS > 0 && Plan.Total > Properties.MAX_PLAYERS - MaxPlayersReserve)
        {
            Log.Error(Invariant($"{LogPrefix} error {Plan.Total} bots leave fewer than {MaxPlayersReserve} logins under max_players={Properties.MAX_PLAYERS}; player bots stay off"));
            Plan = PlayerBotPlan.Off;
            return;
        }
        if (_spots.Builds > 0)
        {
            try                                     // its own: any failure keeps the bots off (levelmatch REVIEW-FIXES 22)
            {
                int items = PlayerBotKitGear.Load(_spots.MaxBuildLevel);
                Log.Info(Invariant($"{LogPrefix} kit items={items} maxLevel={_spots.MaxBuildLevel}"));
            }
            catch (Exception exception)
            {
                Log.Error($"{LogPrefix} error kit catalog {exception.GetType().Name}: {exception.Message}; player bots stay off");
                Plan = PlayerBotPlan.Off;
                return;
            }
        }
        if (Properties.DISCORD_ACTIVE)
        {
            // Read at each post (Managers/Discord/DiscordClientManager.cs:31; GameServer.cs:533), so this also stops
            // the "server open" post. In memory only: the world's row keeps its value.
            Properties.DISCORD_ACTIVE = false;
            Log.Info($"{LogPrefix} discord=off (was on; this run only)");
        }
        bool selfTestTaken = false;
        _slots = Plan.Slots().Select(slot =>
        {
            bool selfTest = !selfTestTaken && !slot.IsQuestFighter;
            selfTestTaken |= selfTest;
            return new SlotState(slot, selfTest);
        }).ToArray();
        _startedUtc = DateTime.UtcNow;
        _nextStatusUtc = _startedUtc.AddSeconds(StatusEverySeconds);
        Log.Info(Invariant($"{LogPrefix} plan {Plan.Describe()} slots={_slots.Length} perSecond={LoginsPerSecond} maxLoading={MaxLoading} wake={PlayerBotWake.Describe(PlayerBotWake.Lever)} spots={_spots.Describe()} levels={_spots.DescribeBuilds()}"));
        GameEventMgr.AddHandler(GameClientEvent.PlayerLoaded, HeadlessClient.OnPlayerLoaded); // no ghost after a timed-out load
        _timer = new Timer(Tick, null, FirstTickMs, 1_000);
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        Timer timer = Interlocked.Exchange(ref _timer, null);
        if (timer == null)
            return;
        timer.Dispose();
        GameEventMgr.RemoveHandler(GameClientEvent.PlayerLoaded, HeadlessClient.OnPlayerLoaded);
        lock (Gate)
            Log.Info($"{LogPrefix} stop {Counts()}");
    }

    private static void Tick(object state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0)
            return;
        try
        {
            DateTime now = DateTime.UtcNow;
            lock (Gate)
            {
                Observe(now);
                StartDue(now);
                if (!_readyLogged && _slots.Length > 0 && _slots.All(s => s.Phase == SlotPhase.Playing))
                {
                    _readyLogged = true;
                    Log.Info(Invariant($"{LogPrefix} ready playing={_slots.Length}/{_slots.Length} sinceStartS={(long)(now - _startedUtc).TotalSeconds}"));
                }
                if (now >= _nextStatusUtc)
                {
                    _nextStatusUtc = now.AddSeconds(StatusEverySeconds);
                    Log.Info($"{LogPrefix} status {Counts()}");
                }
            }
            if (now - _startedUtc >= TimeSpan.FromMinutes(HeapSnapshotMinute) && Interlocked.Exchange(ref _heapDone, 1) == 0)
                HeapSnapshot();
        }
        catch (Exception exception)
        {
            try { Log.Error($"{LogPrefix} tick failed", exception); } catch { } // nothing escapes a timer callback
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private static void Observe(DateTime now)
    {
        long loopNow = GameLoop.GameLoopTime;
        foreach (SlotState s in _slots)
        {
            HeadlessClient c = s.Client;
            switch (s.Phase)
            {
                case SlotPhase.Loading when s.Error != null:
                    Fail(s, now, s.Error);
                    break;
                case SlotPhase.Loading when c != null && c.Step == HeadlessStep.InWorld:
                    s.Phase = SlotPhase.Playing;
                    Log.Info(Invariant($"{LogPrefix} login account={s.Slot.AccountName} name={s.Name} ms={(long)(now - s.LoadingSinceUtc).TotalMilliseconds} attempt={s.Attempts + 1}"));
                    break;
                case SlotPhase.Loading when c != null && c.ClientState == GameClient.eClientState.Disconnected:
                    Fail(s, now, "logged-out reason=" + c.LogoutReason);
                    break;
                case SlotPhase.Loading when (now - s.LoadingSinceUtc).TotalSeconds > LoginTimeoutSeconds:
                    Fail(s, now, "timeout step=" + (c?.Step.ToString() ?? "none"));
                    break;
                case SlotPhase.Playing when c.NeedsReap(loopNow):
                    c.Logout("reaped");
                    s.Phase = SlotPhase.Lost;
                    Log.Warn($"{LogPrefix} lost account={s.Slot.AccountName} name={s.Name} reason=reaped");
                    break;
                case SlotPhase.Playing when c.ClientState == GameClient.eClientState.Disconnected || c.Step == HeadlessStep.LoggingOut:
                    // Never logged in again: F1 (no link death, no kick) must stay visible.
                    s.Phase = SlotPhase.Lost;
                    Log.Warn($"{LogPrefix} lost account={s.Slot.AccountName} name={s.Name} reason={c.LogoutReason}");
                    break;
                case SlotPhase.Playing when s.RelogRequested && !s.ErrandRelogged:                   // T5
                    s.RelogRequested = false;
                    s.ErrandRelogged = true;
                    s.Phase = SlotPhase.Relogging;
                    Log.Info($"{LogPrefix} relog account={s.Slot.AccountName} name={s.Name} reason=errands-done");
                    c.Logout("errand-relog");
                    break;
                case SlotPhase.Relogging when c == null || c.ClientState == GameClient.eClientState.Disconnected: // T5
                    s.Client = null;
                    s.Phase = SlotPhase.Pending;
                    s.RetryAtUtc = now.AddSeconds(5);
                    break;
            }
        }
    }

    private static void Fail(SlotState s, DateTime now, string why)
    {
        s.Attempts++;
        Log.Warn(Invariant($"{LogPrefix} failed account={s.Slot.AccountName} attempt={s.Attempts}/{MaxAttempts} error={why}"));
        s.Client?.Logout("aborted"); // posts the quit to ClientService; a never-registered client just becomes Disconnected
        s.Client = null;
        s.Error = null;
        s.Phase = s.Attempts < MaxAttempts ? SlotPhase.Pending : SlotPhase.Failed;
        s.RetryAtUtc = now.AddSeconds(RetryAfterSeconds);
    }

    private static void StartDue(DateTime now)
    {
        int loading = _slots.Count(s => s.Phase == SlotPhase.Loading);
        SlotState[] due = _slots.Where(s => s.Phase == SlotPhase.Pending && now >= s.RetryAtUtc).ToArray();
        foreach (SlotState s in due.Take(PlayerBotPacing.LoginsToStart(due.Length, loading, LoginsPerSecond, MaxLoading)))
        {
            s.Phase = SlotPhase.Loading;
            s.LoadingSinceUtc = now;
            NpcService.Instance.Post(StartOnGameLoop, s); // as AutonomousPopulationController.cs:157
        }
    }

    // On a game-loop thread (NpcService's posted actions, GS/ECS-Services/GameServiceBase.cs:27, :42).
    private static void StartOnGameLoop(SlotState s)
    {
        try
        {
            DbAccount account = PlayerBotAccounts.EnsureAccount(s.Slot);
            HeadlessClient client = new(account);
            PlayerBotSpot spot = s.ErrandRelogged ? null : _spots.For(s.Slot);                         // the start, not the errand's re-login
            if (spot?.Build != null && s.Kit == null)                                                 // once per slot: a retry keeps it
            {
                PlayerBotKitPlan kit = PlayerBotKit.Plan(spot.Build, PlayerBotKit.Seed(s.Slot));
                if (kit == null)
                {
                    s.Error = "character reason=no-plan";
                    return;
                }
                string[] unknown = PlayerBotKit.UnknownSpecs(kit.Lines, PlayerBotKit.OpenSpecs((int)kit.Build.Class, GamePlayer.MAX_LEVEL));
                if (unknown.Length > 0)                                                               // skipped, never looked up (REVIEW-FIXES 20)
                    Log.Warn(Invariant($"{LogPrefix} kit account={s.Slot.AccountName} class={(int)kit.Build.Class} unknownSpecs={string.Join(",", unknown.Select(u => u.Replace(' ', '_')))}"));
                s.Kit = kit;
            }
            DbCoreCharacter ch = PlayerBotAccounts.EnsureCharacter(account, s.Slot, client, s.Kit, out bool created, out string failure);
            if (ch == null)
            {
                s.Error = "character reason=" + failure;
                return;
            }
            if (created)
                Log.Info(Invariant($"{LogPrefix} created account={s.Slot.AccountName} name={ch.Name} realm={PlayerBotPlan.RealmKey(s.Slot.Realm)} race={ch.Race} class={ch.Class} level={ch.Level} region={ch.Region} x={ch.Xpos} y={ch.Ypos} guild={ch.GuildID}"));
            client.EnteredWorld += (c, first) => OnEnteredWorld(s, c, first);
            client.LoggedOut += c => PlayerBotDriver.Stop(c.Player);                                  // T4
            s.Client = client;
            DbCoreCharacter row = PlayerBotAccounts.LoginRow(account, ch);                            // the one the login reads
            if (spot != null)
                s.Spotted = PlayerBotSpots.Place(spot, row, s.Slot.AccountName, Log);
            client.BeginLogin(row);
        }
        catch (Exception exception)
        {
            s.Error = exception.GetType().Name + ": " + exception.Message;
            Log.Error($"{LogPrefix} start failed account={s.Slot.AccountName}", exception);
        }
    }

    // HeadlessClient.EnteredWorld: on the game loop (a ClientService worker), right after player init.
    private static void OnEnteredWorld(SlotState s, HeadlessClient c, bool first)
    {
        if (!first)
            return; // a re-entry after a region change: the driver keeps running and waits for it
        try
        {
            GamePlayer player = c.Player;
            if (s.Kit != null && !s.Equipped)                                                         // levelmatch: before any driving
            {
                s.Equipped = true;
                PlayerBotKitResult gear = PlayerBotKitGear.Equip(player, s.Kit, PlayerBotKit.Seed(s.Slot));
                Log.Info(PlayerBotKit.Format(s.Slot.AccountName, player, s.Kit, gear));
            }
            bool Drive(GamePlayer p)                                                                  // a spot bot holds its spot
            {
                if (!PlayerBotDriver.Start(p)) return false;
                if (PlayerBotDriver.StateOf(p) is { } state)
                {
                    if (s.Spotted) state.HoldAnchor = true;
                    state.Kit = s.Kit;                                                                // TrainTo on each level-up
                }
                return true;
            }
            bool errands = s.Slot.IsQuestFighter && Plan.Total <= ErrandMaxBots;                       // T5
            if (errands && s.ErrandRelogged)                                                          // T5
            {
                PlayerBotErrands.CheckAfterLogin(player);                                             // T5
                if (!Drive(player))                                                                   // T5
                    Log.Warn($"{LogPrefix} driver not started account={s.Slot.AccountName} after=relog");
                return;                                                                               // T5
            }
            if (s.SelfTest)
            {
                // onDone runs on the self-test's timer; a region trip that has not re-entered yet leaves it undriven.
                HeadlessSelfTest.Start(c, done =>                                                     // T4 (T3: Start(c))
                {
                    if (!Drive(done.Player))
                        Log.Warn($"{LogPrefix} driver not started account={s.Slot.AccountName} after=selftest");
                });
                return;
            }
            if (!Drive(player))                                                                       // T4
            {
                Log.Warn($"{LogPrefix} driver not started account={s.Slot.AccountName}");             // T4
                return;                                                                               // T4
            }
            if (errands)                                                                              // T5
                PlayerBotDriver.StateOf(player).Task = new PlayerBotErrandTask(new PlayerBotErrands(player),
                    done => { if (done) s.RelogRequested = true; });                                  // T5
        }
        catch (Exception exception)
        {
            Log.Error($"{LogPrefix} entered-world failed account={s.Slot.AccountName}", exception);
        }
    }

    // Retained heap (cpu §6): a blocking, compacting full collection, twice around the finalizers. It pauses the game
    // loop (est. 0.2-2 s on a 2 GB heap), so it runs at minute 51, after the measured windows.
    private static void HeapSnapshot()
    {
        var watch = Stopwatch.StartNew();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        watch.Stop();
        string rss = "-";
        try { rss = (BotCpuScript.ParseVmRssKB(File.ReadAllText("/proc/self/status")) / 1024)?.ToString(CultureInfo.InvariantCulture) ?? "-"; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        int playing;
        lock (Gate)
            playing = Count(SlotPhase.Playing);
        Log.Info(Invariant($"{LogPrefix} heap minute={HeapSnapshotMinute} heapMB={GC.GetGCMemoryInfo().HeapSizeBytes / MB} totalMB={GC.GetTotalMemory(false) / MB} rssMB={rss} gamebots={AutonomousBotRegistry.Count} playerbots={playing} pauseMs={watch.ElapsedMilliseconds}"));
    }

    private static int Count(SlotPhase phase) => _slots.Count(s => s.Phase == phase);

    private static string Counts() => Invariant(
        $"playing={Count(SlotPhase.Playing)} loading={Count(SlotPhase.Loading)} pending={Count(SlotPhase.Pending)} relogging={Count(SlotPhase.Relogging)} failed={Count(SlotPhase.Failed)} lost={Count(SlotPhase.Lost)} of={_slots.Length}");
}
