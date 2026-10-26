using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using DOL.AI.Brain;
using DOL.Events;
using DOL.GS.Commands;
using DOL.GS.Keeps;
using DOL.GS.Styles;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

public enum PlayerBotActivity { Waiting, Dead, Resting, Hunting, Fighting, Casting, Roaming, Task }

/// <summary>A scripted job (the errands: quest 20206, promotion) that runs before grinding until Finished. Tick returns
/// true when it used this think (walked, stood, acted), false to let the driver grind this think.</summary>
public interface IPlayerBotTask
{
    bool Finished { get; }
    bool Tick(PlayerBotState state, long now);
}

// One per bot, owned by its player: TimerService charges the think to playerbot, stage Timer (BotCpuMeter.cs:657, :528).
public sealed class PlayerBotDriverTimer : ECSGameTimerWrapperBase // TimerService.cs:156-164
{
    public PlayerBotDriverTimer(GamePlayer owner, PlayerBotState state) : base(owner) => State = state; // not started
    public PlayerBotState State { get; }
    protected override int OnTick(ECSGameTimer timer) => PlayerBotDriver.Tick(State);
}

public sealed class PlayerBotState
{
    public PlayerBotState(GamePlayer player)
    {
        Player = player;
        Mover = new PlayerBotPathMover(player);
        AnchorRegion = player.CurrentRegionID;
        Anchor = new(player.X, player.Y, player.Z);
        LastRegion = AnchorRegion;
        LastLevel = player.Level;
        LastExperience = player.Experience;
        StartExperience = LastExperience;
    }
    public GamePlayer Player { get; }
    public PlayerBotPathMover Mover { get; }
    public ushort AnchorRegion { get; set; }
    public Vector3 Anchor { get; set; }             // spawn point: the leash centre; moved when a task ends far away
    public PlayerBotDriverTimer Timer { get; set; }
    public IPlayerBotTask Task { get; set; }
    public volatile bool Stopped;
    public volatile PlayerBotActivity Activity;      // read by the report thread
    public volatile int NearPlayerBots, NearGamebots;
    public GameLiving Target, Unreachable;
    public bool Engaged;
    public Spell Spell;
    public SpellLine SpellLine;
    public Vector3? RoamDestination;
    public ushort LastRegion;
    public int LastLevel, DeathSafetySteps, ErrorsLogged;
    public long RoamingSince, NextIdleLine, NextRelocateTick; // no target reached since: PLAYER_BOT_IDLE, PLAYER_BOT_RELOCATE
    public readonly HashSet<GameNPC> RelocateRejected = new(); // no corridor to them: not checked again
    public long AliveSince;                                      // PLAYER_BOT_DEATH: the last release or login
    public int Attackers;                                        //   and the attackers on the last think alive
    public long NextBlockedLine;                                 // PLAYER_BOT_BLOCKED
    public int BlockedSinceLine;
    public long NextWakeScanTick;                                // PlayerBotWake's 3,600-unit scan (the #137 lever)
    public bool HoldAnchor;                                      // a spot bot (PLAYER_BOT_SPOTS): never relocates,
    public PlayerBotKitPlan Kit;                                 //   unless it has a build (levelmatch): TrainTo on level-up
    public long SpellBlockedUntil;                               // the cast watch (levelmatch): swing, not cast, until then
    public GameLiving CastTarget;                                //   the target of the current run of requests,
    public int CastAttempts;                                     //   how many, and its health at the first
    public byte CastHealthMark;
    public int Kills, Deaths, CastFailures, Retrains;            // PLAYER_BOT_TALLY: this bot's own, since login,
    public long Gained, StartExperience;                         //   its experience gained, and at login
    public long NetExperience => Interlocked.Read(ref LastExperience) - StartExperience;   // REVIEW-FIXES 9: losses too
    public long LastExperience, UnreachableUntil, NextTargetRefreshTick, NextLootTick, NextLosTick, NextStyleTick,
        NextCombatChoiceTick, NextNeighbourCountTick, NextProgressCheckTick, LastProgressTick, ProgressExperience, ProgressMoney;
    public Vector3 ProgressPosition;
    public bool DeathRecorded, ReleaseRequested;
    public long EngagedSince;                                    // the deaths' diagnostics (levelmatch/DEATHS.md 2): the
    public byte EngageHealth, EngageMana;                        //   fight's first think in reach and the bot's health and
    public int FightAdds, Swings, NotVisible, FightCasts;        //   power then, its most attackers, swings and casts,
    public AttackData LastSeenSwing;                             //   the swing counted last (CountSwing), its target
    public bool FromDefence;                                     //   hitting the bot when picked (PLAYER_BOT_FIGHT);
    public int LifeKills, LifeRests;                             //   this life's fights won and sits, its last kill,
    public long LastKillTick;                                    //   and the killer the Dying event names (OnBotDying)
    public GameObject Killer;
    public bool RecoverBeforePull;                               // a kill since the last full rest: rest to full first
    public (string Head, PlayerBotDeathDetail Detail, GameObject Target)? PendingDeath;   // a DEATH line awaiting Dying (AwaitsDying)
}

// HearthDAoC #137 spike: a thin copy of the /bot pilot's think (bots/autonomous/AutonomousPlayerPilot.cs:229-374) for a
// player bot: no quickbar file, pets, party, HUD or saves. Each bot is ticked every 250 ms by its own timer. It
// keeps the bot alive, rests, loots, fights back a monster that hits it, else picks one the way a GameBot does
// (AutonomousWorldBotController.cs:3050-3148, no camp), walks with PlayerBotPathMover, faces, sets "target in view"
// from the navmesh, and casts or swings. It writes no database rows on the loop; autosave and logout save. It runs in
// TimerService's phase, in parallel with every other timer (the bot's own regeneration and release timers too), so the
// player's position, heading, target, sitting and areas change there instead of in ClientService's packet phase;
// attacks and casts are only requested.
public static class PlayerBotDriver
{
    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotDriver));

    public const int TickMilliseconds = 250;                 // APP:22
    public const int TargetRefreshMilliseconds = 2000;       // APP:24, :306
    public const ushort ImmediateSearchRadius = 2600;        // GameBot: AWBC:31, :3148
    public const ushort SearchRadius = 5000;                 // AWBC:35, :3148
    public const int ConPenalty = 220;                       // AWBC:3100
    public const int AddMargin = 200;                        // the adds rule: an add's reach past its aggro range
    public const int AddScanMargin = 700;                    // (inf.) 500, the aggro range of 54,397 of the world's
                                                             //   aggressive rows, + AddMargin: adds past a ring's edge
    public const ushort LootRadius = 260;                    // APP:26
    public const int LootMilliseconds = 1000;                // APP:408
    public const int RoamRadius = 1400;                      // APP:483
    public const int LeashRadius = 4000;                     // stay in the start area (fair runs)
    public const int LosRefreshMilliseconds = 1000;
    public const int StyleMilliseconds = 1000;
    public const int NeighbourCountMilliseconds = 60_000;
    public const int UnreachableSkipMilliseconds = 30_000;
    public const int ProgressCheckMilliseconds = 10_000;     // the pilot checked every tick (APP:585-604)
    public const int MeaningfulMoveDistance = 96;
    public const int DeathReleaseDelayMilliseconds = 10_000; // APP:29, :575-581
    public const int IdleLineMilliseconds = 5 * 60_000;
    public const int RelocateAfterMilliseconds = 2 * 60_000; // no target reached this long: the hunting ground is outgrown
    public const int RelocateCandidates = 5;                 // corridor checks per try
    public static readonly long StuckMilliseconds = (long)AutonomousStuckWatchdog.StuckThreshold.TotalMilliseconds; // 15 min
    public const int MaxFruitlessCasts = 3;                  // the cast watch (levelmatch/DESIGN.md 2.7)
    public const int SpellBlockMilliseconds = 10_000;        // (inf.) a few swings, then the spell again

    private static readonly ConcurrentDictionary<GamePlayer, PlayerBotState> _states = new();
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int Score)> _candidates;      // FindWithin's, per timer
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int AggroRange)> _adds;       //   thread (TimerService)

    public static IEnumerable<PlayerBotState> States => _states.Values;
    public static bool IsDriven(GamePlayer player) => player != null && _states.ContainsKey(player);
    public static PlayerBotState StateOf(GamePlayer player) => player != null && _states.TryGetValue(player, out PlayerBotState s) ? s : null;

    public static bool Start(GamePlayer player)
    {
        if (player?.Client is not IPlayerBotClient || player.ObjectState is not GameObject.eObjectState.Active ||
            player.Client.ClientState is not GameClient.eClientState.Playing)
            return false;                                       // never drive a human's character
        PlayerBotState state = new(player);
        if (!_states.TryAdd(player, state))
            return true;
        GameEventMgr.AddHandler(player, GameLivingEvent.Dying, OnBotDying);   // the DEATH line's killer; Stop removes it
        if (player.Client is HeadlessClient headless)
            headless.HeartbeatEnabled = false;                  // the mover's IdleBroadcast takes over
        player.CurrentSpeed = 0;
        player.TargetInView = false;
        player.LastPositionUpdatePacketReceivedTime = GameLoop.GameLoopTime;
        player.OnPositionUpdateFromPacket();                    // puts PlayerMovementComponent in its service (PMC:143)
        state.LastProgressTick = GameLoop.GameLoopTime;
        state.ProgressPosition = new(player.X, player.Y, player.Z);
        state.Timer = new PlayerBotDriverTimer(player, state);
        state.Timer.Start(TickMilliseconds + player.ObjectID % TickMilliseconds); // spreads the bots over the ticks
        PlayerBotDriverReport.EnsureStarted();
        return true;
    }

    public static void Stop(GamePlayer player)
    {
        if (player == null || !_states.TryRemove(player, out PlayerBotState state)) return;
        GameEventMgr.RemoveHandler(player, GameLivingEvent.Dying, OnBotDying);
        state.Stopped = true;
        state.Task = null;
        state.Timer?.Stop();
        if (player.Client is HeadlessClient headless)
            headless.HeartbeatEnabled = true;
        if (player.ObjectState is GameObject.eObjectState.Active)
        {
            state.Mover.Stop(GameLoop.GameLoopTime);
            player.attackComponent.StopAttack();
            player.StopCurrentSpellcast();
        }
    }

    // Never throws: an exception escaping a timer callback removes the timer and kicks its GamePlayer owner to the
    // character screen (TimerService.cs:64-67 -> GameServiceUtils.cs:25-41, :73-87). 0 stops the timer.
    public static int Tick(PlayerBotState state)
    {
        if (state == null || state.Stopped) return 0;
        GamePlayer player = state.Player;
        GameClient client = player?.Client;
        if (client == null || player.ObjectState is GameObject.eObjectState.Deleted ||
            client.ClientState is GameClient.eClientState.NotConnected or GameClient.eClientState.Disconnected)
        {
            state.Stopped = true;
            try { Stop(player); }                            // StopAttack can write to the client: keep the promise above
            catch (Exception e)
            {
                PlayerBotDriverReport.Error();
                if (Log.IsErrorEnabled) Log.Error($"PLAYER_BOT_DRIVER error stopping {player?.Name}", e);
            }
            return 0;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            long now = GameLoop.GameLoopTime;
            player.LastPositionUpdatePacketReceivedTime = now;                     // 1. keep-alive (PMC:58-66)
            if (client.ClientState is not GameClient.eClientState.Playing ||
                player.ObjectState is not GameObject.eObjectState.Active)
            {
                state.Activity = PlayerBotActivity.Waiting;                        // 2. loading, re-entry
                return TickMilliseconds;
            }
            Think(state, player, now);                                             // 3-16
            state.Mover.IdleBroadcast(now);                                        // 17
        }
        catch (Exception e)
        {
            PlayerBotDriverReport.Error();
            if (state.ErrorsLogged++ < 3 && Log.IsErrorEnabled)
                Log.Error($"PLAYER_BOT_DRIVER error for {player.Name}", e);
        }
        finally
        {
            PlayerBotDriverReport.AddTick(Stopwatch.GetTimestamp() - started);
        }
        return TickMilliseconds;
    }

    private static void Think(PlayerBotState state, GamePlayer player, long now)
    {
        if (player.IsLinkDeathTimerRunning)                  // 3. a soft link death started anyway (driver stalled > 5 s)
        {
            player.IsPositionUpdateFromPacketAllowed();      //    what a client's next packet does (GP:857-867; PPUH:27-28)
            state.Mover.Reset();
            PlayerBotDriverReport.LinkDeathCancelled();
        }
        if (player.CurrentRegionID != state.LastRegion)      // 4. after a release or re-entry in another region
        {
            state.LastRegion = player.CurrentRegionID;
            state.Mover.Reset();
            state.Target = null;
            state.RoamDestination = null;
            state.RoamingSince = 0;
        }
        ObserveXp(state, player);                            // 5
        if (state.PendingDeath is { } held)                                              //    last think's DEATH line, its
        {                                                                                //    killer named since (AwaitsDying)
            state.PendingDeath = null;
            if (Log.IsInfoEnabled) Log.Info(DeathLine(held.Head, held.Detail, state.Killer, held.Target));
        }
        if (!player.IsAlive) { HandleDeath(state, player, now); return; }               // 6
        if (state.DeathRecorded || state.AliveSince == 0)                                //    a new life
        {
            state.AliveSince = now;
            state.LifeKills = state.LifeRests = 0;
            state.Killer = null;
            state.RecoverBeforePull = false;                                             //    the release restores it all
        }
        state.DeathRecorded = false;
        state.ReleaseRequested = false;
        state.Attackers = player.attackComponent.AttackerTracker.Count;                 //    Die clears it (GameLiving.cs:2050)
        PlayerBotWake.OnThink(state, player, now);                                       // 6b. the #137 lever's wakes
        if (RecoverIfStuck(state, player, now)) return;                                  // 7
        if (player.IsCrowdControlled)                                                    // 8
        {
            state.Mover.Stop(now);
            state.Activity = PlayerBotActivity.Waiting;
            return;
        }
        CountNeighbours(state, player, now);                                             // 9. once a minute
        SettleTarget(state, player);                                                     //    a fight that ended counts, task or not
        if (state.Task != null)                                                          // 10
        {
            if (state.Task.Finished)
            {
                state.Task = null;
                state.AnchorRegion = player.CurrentRegionID;                             // grind where the task ended
                state.Anchor = new(player.X, player.Y, player.Z);
                state.RoamDestination = null;                                            // not a relocation's spot
            }
            else if (state.Task.Tick(state, now))
            {
                state.Activity = PlayerBotActivity.Task;
                state.RoamingSince = 0;
                if (state.Target != null) Disengage(state, player);                     // the next defence picks afresh
                return;
            }
        }
        TryLoot(state, player, now);                                                     // 11. APP:403-414, before a sit
        if (state.RecoverBeforePull && Recovered(player)) state.RecoverBeforePull = false;
        if (MayRest(Attacker(state, player, now) != null, state.Engaged && state.Target?.IsAlive == true) &&
            WantsRest(player, state.RecoverBeforePull))                                  // 12. APP:290-296, DEATHS.md A1
        {
            if (state.Target != null) Disengage(state, player);                         //    a pull not begun: re-picked after
            Rest(state, player, now);
            return;
        }
        if (player.IsSitting) player.Sit(false);                                         // 13. rested, or hit: stand
        RefreshTarget(state, player, now);                                               // 14
        if (state.Target == null)                                                        // 15
        {
            RelocateIfOutgrown(state, player, now);
            Roam(state, player, now);
            ReportIdle(state, player, now);
            return;
        }
        Engage(state, player, now);                                                      // 16
        if (state.Engaged) state.RoamingSince = 0;   // reached: a target blocked on the way leaves the idle clock running
    }

    // A task owns the bot's position while it runs: no leash then.
    private static bool Leashed(PlayerBotState state, GamePlayer player) =>
        state.Task == null && player.CurrentRegionID == state.AnchorRegion;

    private static void Engage(PlayerBotState state, GamePlayer player, long now)
    {
        GameLiving target = state.Target;
        player.TargetObject = target;                         // before AttackRange: MeleeAttackRange reads TargetObject (GP:5320-5335)
        int distance = player.GetDistanceTo(target);
        bool canCast = ShouldCast(state.Spell != null, player.ManaPercent, now, state.SpellBlockedUntil,
            player.IsBeingInterruptedByOther, state.Spell != null && Interruptible(state.Spell));
        int stop = canCast ? SpellStopDistance(player.castingComponent.CalculateSpellRange(state.Spell))
                           : MeleeStopDistance(player.attackComponent.AttackRange);
        if (distance > stop)
        {
            player.attackComponent.StopAttack();
            player.StopCurrentSpellcast();
            state.Activity = PlayerBotActivity.Hunting;
            Vector3 goal = new(target.X, target.Y, target.Z);
            if (state.Mover.StepToward(goal, stop, now) is PlayerBotStepResult.Blocked)
            {
                ReportBlocked(state, player, "target", goal, now);
                state.Unreachable = target;
                state.UnreachableUntil = now + UnreachableSkipMilliseconds;
                state.Target = null;
            }
            return;
        }
        state.Mover.Stop(now);
        Face(player, target);                                 // in front: AttackComponent.cs:1065
        if (now >= state.NextLosTick)                         // TargetInView: AttackComponent.cs:1065, SpellHandler.cs:450
        {
            state.NextLosTick = now + LosRefreshMilliseconds;
            player.TargetInView = HeadlessLos.Answer(PathfindingProvider.Instance, player.CurrentZone,
                new Vector3(player.X, player.Y, player.Z), new Vector3(target.X, target.Y, target.Z));
        }
        if (!state.Engaged)                                   // the fight's first think in reach: PLAYER_BOT_FIGHT's start
        {
            state.EngagedSince = now;
            state.EngageHealth = player.HealthPercent;
            state.EngageMana = PowerPercent(player.MaxMana, player.ManaPercent);
            state.FightAdds = state.Swings = state.NotVisible = state.FightCasts = 0;
        }
        state.Engaged = true;
        state.FightAdds = Math.Max(state.FightAdds, state.Attackers);
        CountSwing(state, player.attackComponent.attackAction.LastAttackData);
        if (canCast && !player.IsCasting && now >= state.NextCombatChoiceTick &&
            player.IsWithinRadius(target, player.castingComponent.CalculateSpellRange(state.Spell)))
        {
            player.attackComponent.StopAttack();
            (state.CastAttempts, state.CastHealthMark, bool stalled) =
                WatchCast(state.CastTarget == target, state.CastAttempts, state.CastHealthMark, target.HealthPercent);
            state.CastTarget = target;
            if (stalled || !player.CastSpell(state.Spell, state.SpellLine))             // APP:345-353 without the Animist policy
            {
                // CastSpell only queues the request (CastingComponent.cs:116-144); range and sight fail later
                // (SpellHandler.cs:681, :694, :731). A run of requests that hurt nothing: swing instead for a while.
                state.SpellBlockedUntil = now + SpellBlockMilliseconds;
                state.CastAttempts = 0;
                state.CastFailures++;
                state.Activity = PlayerBotActivity.Fighting;
                return;                                                                 // the next think walks into melee
            }
            state.NextCombatChoiceTick = now + Math.Max(900, state.Spell.CastTime + 250);
            state.FightCasts++;
            state.Activity = PlayerBotActivity.Casting;
        }
        else if (!player.IsCasting)
        {
            if (now >= state.NextStyleTick)
            {
                state.NextStyleTick = now + StyleMilliseconds;
                QueueUsableStyle(player);                                               // APP:694-710 without the quickbar filter
            }
            if (!player.attackComponent.AttackState)
                player.attackComponent.RequestStartAttack(target);                      // AttackComponent.cs:491-496
            state.Activity = PlayerBotActivity.Fighting;
        }
    }

    /// <summary>Whether the bot casts its damage spell this think (APP:670, the cast watch's block) or swings: never an
    /// interruptible cast while a monster's hits interrupt it (IsBeingInterruptedByOther, GameLiving.cs:739), which a
    /// player gets "must wait" for (SpellHandler.cs:570-590; DEATHS.md A2). A GameBot under melee stops casting and
    /// fights (BotBrain.cs:3336-3370). An instant or uninterruptible spell still goes.</summary>
    public static bool ShouldCast(bool hasSpell, byte manaPercent, long now, long blockedUntil, bool interrupted,
        bool interruptible) =>
        hasSpell && manaPercent >= 18 && now >= blockedUntil && (!interrupted || !interruptible);

    /// <summary>The server's own test for the interrupt timer (SpellHandler.cs:571).</summary>
    public static bool Interruptible(Spell spell) => !spell.Uninterruptible && !spell.IsInstantCast;

    /// <summary>The cast watch, one request on: (attempts, health mark, stalled). A new target, or one hurt since the
    /// mark, starts a new run; more than MaxFruitlessCasts requests that leave it at the mark have stalled.</summary>
    public static (int Attempts, byte Mark, bool Stalled) WatchCast(bool sameTarget, int attempts, byte mark, byte health)
    {
        if (!sameTarget || health < mark) (attempts, mark) = (0, health);
        attempts++;
        return (attempts, mark, attempts > MaxFruitlessCasts);
    }

    public static void Face(GamePlayer player, GameObject target)
    {
        ushort heading = player.GetHeading(target.X, target.Y);                          // GS/world/Point2D.cs:102
        if (player.Heading == heading) return;
        player.Heading = heading;
        player.OnHeadingPacketReceived();                                                // GP:874-877 -> PMC:151-154
    }

    // Drops a target that died or is no longer fair game (an attacker stays fair game whatever its con). A death counts
    // for the TALLY of the first bot to report it only, so bots sharing a camp sum to the DRIVER line's kills.
    private static void SettleTarget(PlayerBotState state, GamePlayer player)
    {
        if (state.Target == null || IsValidTarget(player, state.Target, state.DeathSafetySteps) || IsAttacking(player, state.Target))
            return;
        bool dead = !state.Target.IsAlive;
        if (state.Engaged) ReportFight(state, player, GameLoop.GameLoopTime, dead);   // with this life's kills before it
        if (state.Engaged && dead)
        {
            state.LifeKills++;                                    // every fight won, a monster another bot counted too
            state.LastKillTick = GameLoop.GameLoopTime;
            state.RecoverBeforePull = true;                       // rest to full before the next pull (WantsRest)
            if (PlayerBotDriverReport.Kill(state.Target)) state.Kills++;
        }
        else if (dead) state.RoamingSince = 0;                   // killed by another before this bot got there: not outgrown
        state.Target = null;
        state.Engaged = false;
    }

    private static void Disengage(PlayerBotState state, GamePlayer player)
    {
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        state.Target = null;
        state.Engaged = false;
    }

    private static void RefreshTarget(PlayerBotState state, GamePlayer player, long now)
    {
        bool fighting = state.Engaged && (player.attackComponent.AttackState || player.IsCasting || player.InCombat);
        bool searchDue = now >= state.NextTargetRefreshTick;
        if (state.Target != null && (fighting || !searchDue)) return;  // keep a target while fighting it
        if (searchDue) state.NextTargetRefreshTick = now + TargetRefreshMilliseconds;
        // Self-defence every think; the rings only every 2 s, so an idle bot doesn't scan 5,000 units 4 times a second
        GameNPC found = Attacker(state, player, now) ??                                    // self-defence first
            (searchDue ? FindWithin(state, player, ImmediateSearchRadius, now) ?? FindWithin(state, player, SearchRadius, now) : null); // AWBC:3148
        if (found == state.Target) return;
        state.Target = found;
        state.FromDefence = found != null && IsAttacking(player, found);                  // FIGHT and DEATH from=
        state.Engaged = false;
        state.NextLosTick = 0;
        state.SpellBlockedUntil = 0;                                                       // a new target clears the watch
        state.CastTarget = null;
        (state.Spell, state.SpellLine) = found == null ? (null, null) : FindDamageSpell(player);
    }

    // The candidates within the con cap go through the adds rule (PickUncrowded): the 2,600-unit ring takes only one with
    // no potential add near it, the 5,000-unit ring the least crowded. The scan reaches AddScanMargin past the ring for
    // the adds by a candidate at its edge.
    private static GameNPC FindWithin(PlayerBotState state, GamePlayer player, ushort radius, long now)
    {
        GameNPC safest = null;
        int safestScore = int.MaxValue;
        ConColor cap = MaximumTargetCon(1, state.DeathSafetySteps), safestCon = ConColor.PURPLE;
        bool leashed = Leashed(state, player);
        List<(GameNPC Npc, Vector3 At, int Score)> candidates = _candidates ??= new();
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _adds ??= new();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)(radius + AddScanMargin)))
        {
            if (!IsExperienceTarget(npc)) continue;                                     // monsters only, adds too
            Vector3 at = new(npc.X, npc.Y, npc.Z);
            if (AddReach(player, npc) is > 0 and int reach) adds.Add((npc, at, reach));
            if (!player.IsWithinRadius(npc, radius) || !IsValidTarget(player, npc, 0) ||
                npc == state.Unreachable && now < state.UnreachableUntil)
                continue;
            ConColor con = ConLevels.GetConColor(player.GetConLevel(npc));
            if (!AutonomousPlayerPilot.IsEligibleExperienceTarget(con, npc.EffectiveLevel, player.Level, 1) ||
                leashed && Vector3.Distance(at, state.Anchor) > LeashRadius + RoamRadius)
                continue;
            int score = TargetScore(player.GetDistanceTo(npc), con);                    // AWBC:3100
            if (con <= cap) candidates.Add((npc, at, score));
            else if (con < safestCon || con == safestCon && score < safestScore) { safest = npc; safestCon = con; safestScore = score; }
        }
        GameNPC best = PickUncrowded(candidates, adds, uncrowdedOnly: radius < SearchRadius);
        candidates.Clear();                                                             // no monster held past the search
        adds.Clear();
        if (best != null || safest == null || radius < SearchRadius) return best;
        state.DeathSafetySteps = DeathStepsFor(safestCon, 1, state.DeathSafetySteps);    // nothing in reach fits the death cap
        return safest;
    }

    // The adds rule's view of a monster (IsExperienceTarget) near a candidate: its aggro range when it would join that
    // fight, else 0.
    private static int AddReach(GamePlayer player, GameNPC npc) =>
        npc.Brain is StandardMobBrain brain && npc.IsAlive &&
        IsPotentialAdd(brain.AggroLevel, brain.AggroRange, player.IsObjectGreyCon(npc),
            npc.IsAttacking && npc.TargetObject is GameLiving other && other != player)
            ? brain.AggroRange : 0;

    // Self-defence: the nearest monster that is hitting the bot (its AttackerTracker, as BotGroupSupport.UnderAttack
    // reads it), whatever its con and wherever the leash is. The pilot's filter alone left the bot roaming or resting
    // while an orange monster killed it, and each death lowered its con cap further.
    private static GameNPC Attacker(PlayerBotState state, GamePlayer player, long now)
    {
        GameNPC best = null;
        int bestDistance = int.MaxValue;
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
        {
            if (living is not GameNPC npc || !IsAttacking(player, npc) || npc == state.Unreachable && now < state.UnreachableUntil)
                continue;
            int distance = player.GetDistanceTo(npc);
            if (distance < bestDistance) { best = npc; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>For a task (the errands): true while the bot has a fight to finish, a monster hitting it or the one it
    /// is engaged with; then the task gives the think to the driver, which fights that one and seeks no other.</summary>
    public static bool UnderAttack(PlayerBotState state, long now) =>
        (state.Engaged && state.Target != null && state.Player.InCombat) || Attacker(state, state.Player, now) != null;

    private static bool IsAttacking(GamePlayer player, GameLiving target) =>
        target is GameNPC npc && npc.TargetObject == player && IsAttackable(player, npc);

    private static bool IsValidTarget(GamePlayer player, GameLiving target, int deathSteps) =>   // APP:447-463, group size 1
        IsAttackable(player, target) && !player.IsObjectGreyCon(target) &&
        ConLevels.GetConColor(player.GetConLevel(target)) <= MaximumTargetCon(1, deathSteps);

    private static bool IsAttackable(GamePlayer player, GameLiving target) =>
        target != null && target != player && target.IsAlive && target.ObjectState is GameObject.eObjectState.Active &&
        target.CurrentRegion == player.CurrentRegion && GameServer.ServerRules.IsAllowedToAttack(player, target, true) &&
        (target is not GameNPC npc || (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0);

    // A copy of AutonomousWorldBotController.IsExperienceMonster (AWBC:4855-4861, private there).
    public static bool IsExperienceTarget(GameNPC npc) =>
        npc?.ObjectState is GameObject.eObjectState.Active && npc.Realm == eRealm.None && npc.Brain != null &&
        (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 &&
        npc is not GameMerchant and not GameGuard and not GameTaxi and not GameSummonedPet and not GameKeepGuard and
            not GameTrainer and not GameTeleporter and not GameHealer and not CraftNPC &&
        !AutonomousNeutralTownPolicy.IsTownResident(npc);

    // = APP:549-559 = AWBC:3834-3846: YELLOW solo, one step lower per death, never below GREEN.
    public static ConColor MaximumTargetCon(int groupSize, int deathSteps)
    {
        ConColor natural = groupSize switch { >= 6 => ConColor.PURPLE, >= 4 => ConColor.RED, >= 2 => ConColor.ORANGE, _ => ConColor.YELLOW };
        return (ConColor)Math.Max((int)ConColor.GREEN, (int)natural - deathSteps);
    }

    /// <summary>The death steps to keep when nothing in reach fits the lowered cap: just enough for the safest monster
    /// there, as a GameBot falls back to the safest camp (AWBC:3422-3437). The pilot never lowers them (APP:558, :567),
    /// and nothing cons green to a level-1 player (ConLevels.cs:20), so run 1's bots stopped hunting after two deaths.</summary>
    public static int DeathStepsFor(ConColor safest, int groupSize, int deathSteps) =>
        Math.Min(deathSteps, Math.Max(0, (int)MaximumTargetCon(groupSize, 0) - (int)safest));

    // The GameBot's recovery thresholds (AutonomousRestPolicy.NeedsRecovery, AutonomousBotDecisionEngine.cs:384-394: 70%
    // health, 45% power, 35% endurance; the pilot's 50/22/20 at APP:186-187 left the bots pulling at half health,
    // levelmatch/DEATHS.md A1). Power counts only for a class with a pool: one with none (ManaStat UNDEFINED,
    // MaxManaCalculator.cs:32-51) reads ManaPercent 0 (GameLiving.cs:2753) and would sit forever.
    public static bool NeedsRest(byte healthPercent, int maxMana, byte manaPercent, byte endurancePercent) =>
        AutonomousRestPolicy.NeedsRecovery(healthPercent, manaPercent, endurancePercent, maxMana > 0);

    /// <summary>The rest gate (DEATHS.md A1): nothing is hitting the bot and it has no live target engaged. A player may
    /// sit in combat (GamePlayer.Sit, GamePlayer.cs:8734-8815, has no combat check); the old gate waited out InCombat's
    /// 10 s after the last swing (GameLiving.cs:31, :225-227), and the next pull began in the think of the kill.</summary>
    public static bool MayRest(bool beingHit, bool inLiveFight) => !beingHit && !inLiveFight;

    /// <summary>Power for the diagnostics' lines: full for a class with no power pool, as NeedsRest reads it.</summary>
    public static byte PowerPercent(int maxMana, byte manaPercent) => maxMana > 0 ? manaPercent : (byte)100;

    /// <summary>Counts the bot's last swing once (WeaponAction.cs:309 makes an AttackData per swing), and apart those the
    /// player-only view gate refused (AttackComponent.cs:1062-1070). A refused swing retries every 100 ms
    /// (AttackAction.cs:372-383), so NotVisible counts the thinks spent refused rather than the swings lost.</summary>
    public static void CountSwing(PlayerBotState state, AttackData swing)
    {
        if (swing == null || swing == state.LastSeenSwing) return;
        state.LastSeenSwing = swing;
        state.Swings++;
        if (swing.AttackResult is eAttackResult.TargetNotVisible) state.NotVisible++;
    }

    /// <summary>The DEATH line's killer. ProcessDeath notifies Dying last (GameLiving.cs:2073), in ReaperService's phase of
    /// the tick the bot was killed in, so before its next think (GameLoop.cs:149, :159). Never throws: any sender but a
    /// driven bot is ignored.</summary>
    public static void OnBotDying(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player && StateOf(player) is PlayerBotState state && args is DyingEventArgs dying)
            state.Killer = dying.Killer;
    }

    /// <summary>The DEATH line waits a think for its killer: the bot was killed in this tick's timer phase (a bolt or an
    /// arrow lands from a timer, BoltSpellHandler.cs:153-165, AttackAction.cs:364-365), so the reaper's Dying event that
    /// names the killer comes later in the tick (GameLiving.cs:1987-1988, GameLoop.cs:149-159).</summary>
    public static bool AwaitsDying(GameObject killer, bool beingReaped) => killer == null && beingReaped;

    /// <summary>PLAYER_BOT_DEATH: its first fields, then FormatDeathDetail's with the killer known by then.</summary>
    public static string DeathLine(string head, PlayerBotDeathDetail detail, GameObject killer, GameObject target)
    {
        detail = detail with { Killer = killer?.Name, KillerLevel = killer?.EffectiveLevel ?? 0, KillerIsTarget = killer != null && killer == target };
        return head + " " + PlayerBotDriverReport.FormatDeathDetail(detail);
    }

    /// <summary>Low, or sitting and not yet full (APP:290-291 without the combat check), or after a kill until full
    /// (recoverBeforePull): the driver rests when MayRest says so, and a task that walks lets it.</summary>
    public static bool WantsRest(GamePlayer player, bool recoverBeforePull = false) =>
        WantsRest(NeedsRest(player.HealthPercent, player.MaxMana, player.ManaPercent, player.EndurancePercent),
            player.IsSitting, recoverBeforePull, Recovered(player));

    /// <summary>After a kill the bot rests to full before it picks the next target, as a GameBot does after a camp fight:
    /// the fight sets _recoverBeforeNextCampTarget (AWBC:423-424) and WorkCamp rests until IsFullyRecovered before its
    /// next target search (AWBC:2727-2730, HandleCampRecovery :3019-3047).</summary>
    public static bool WantsRest(bool needsRest, bool sitting, bool recoverBeforePull, bool recovered) =>
        needsRest || (sitting || recoverBeforePull) && !recovered;

    public static int TargetScore(int distance, ConColor con) => distance + Math.Abs((int)con) * ConPenalty;
    public static int MeleeStopDistance(int attackRange) => Math.Max(48, (attackRange > 0 ? attackRange : 128) - 48);
    public static int SpellStopDistance(int spellRange) => Math.Max(145, spellRange - 80);   // APP:320

    /// <summary>Whether a monster would join a fight by it (the adds rule; in the diagnostic smoke 26% of the deaths with a
    /// named killer were to a monster other than the target): aggressive (aggro level and range above 0,
    /// StandardMobBrain.cs:705, as PlayerBotWake.ScanReason reads them), not grey to the bot (a grey monster never aggroes,
    /// StandardMobBrain.cs:685) and not already fighting someone else.</summary>
    public static bool IsPotentialAdd(int aggroLevel, int aggroRange, bool greyToBot, bool fightingSomeoneElse) =>
        aggroLevel > 0 && aggroRange > 0 && !greyToBot && !fightingSomeoneElse;

    /// <summary>The potential adds within their aggro range + AddMargin of a candidate at `at`: the bot fights by its
    /// target. The candidate itself is no add.</summary>
    public static int Crowding<T>(T candidate, Vector3 at, IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class
    {
        int crowd = 0;
        for (int i = 0; i < adds.Count; i++)                                            // no enumerator per candidate
        {
            (T npc, Vector3 position, int aggroRange) = adds[i];
            float reach = aggroRange + AddMargin;
            if (!ReferenceEquals(npc, candidate) && Vector3.DistanceSquared(position, at) <= reach * reach) crowd++;
        }
        return crowd;
    }

    /// <summary>The adds rule over a ring's candidates within the con cap: the best score (TargetScore) among those with no
    /// potential add near them; with none such, the least crowded, then the best score; null with uncrowdedOnly, so the
    /// wider ring decides. The first of equals wins, as before.</summary>
    public static T PickUncrowded<T>(IReadOnlyList<(T Npc, Vector3 At, int Score)> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, bool uncrowdedOnly) where T : class
    {
        T best = null;
        int bestCrowd = int.MaxValue, bestScore = int.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            (T npc, Vector3 at, int score) = candidates[i];
            if (bestCrowd == 0 && score >= bestScore) continue;                         // nothing beats a clear better one
            int crowd = Crowding(npc, at, adds);
            if (uncrowdedOnly && crowd > 0) continue;
            if (crowd < bestCrowd || crowd == bestCrowd && score < bestScore) (best, bestCrowd, bestScore) = (npc, crowd, score);
        }
        return best;
    }

    // APP:667-692 without the quickbar, the Animist policy, the range filter (checked at cast time) and the mana check. A
    // list caster's spells are in its spell lines; a hybrid's (Paladin, Thane...) are among its usable skills, each with
    // its line (GamePlayerUtils.cs:544-560; REVIEW-FIXES 19).
    internal static (Spell, SpellLine) FindDamageSpell(GamePlayer player)
    {
        Spell best = null;
        SpellLine bestLine = null;
        void Consider(Spell spell, SpellLine line)
        {
            if (!spell.IsHarmful || spell.Damage <= 0 || spell.Range <= 0 || spell.NeedInstrument || spell.Level > player.Level)
                return;
            if (best == null || Better(spell, best)) { best = spell; bestLine = line; }
        }
        foreach ((SpellLine line, List<Skill> skills) in player.GetAllUsableListSpells())
            foreach (Spell spell in skills.OfType<Spell>())
                Consider(spell, line);
        foreach ((Skill skill, Skill parent) in player.GetAllUsableSkills())
            if (skill is Spell spell && parent is SpellLine line)
                Consider(spell, line);
        return (best, bestLine);
    }

    /// <summary>A direct hit over damage over time (a DoT kills slowly and, with the watch, reads as fruitless), then the
    /// higher level, then the more damage.</summary>
    public static bool Better(Spell candidate, Spell best)
    {
        bool dot = IsDamageOverTime(candidate), bestDot = IsDamageOverTime(best);
        if (dot != bestDot) return !dot;
        if (candidate.Level != best.Level) return candidate.Level > best.Level;
        return candidate.Damage > best.Damage;
    }

    private static bool IsDamageOverTime(Spell spell) =>
        spell.SpellType is eSpellType.DamageOverTime or eSpellType.DamageOverTimeNoVariance;

    public static string BestDamageSpellName(GamePlayer player) =>
        FindDamageSpell(player).Item1 is Spell s ? FormattableString.Invariant($"{s.Name.Replace(' ', '_')}:{s.Level}") : "-";

    // APP:694-710 without the quickbar filter.
    private static void QueueUsableStyle(GamePlayer player)
    {
        if (player.styleComponent.NextCombatStyle != null || player.EndurancePercent < 15 || player.ActiveWeapon == null)
            return;
        AttackData lastAttack = player.attackComponent.attackAction.LastAttackData;
        Style style = player.GetAllUsableSkills()
            .Select(entry => entry.Item1)
            .OfType<Style>()
            .Where(candidate => StyleProcessor.CanUseStyle(lastAttack, player, candidate, player.ActiveWeapon))
            .OrderByDescending(candidate => candidate.Level)
            .FirstOrDefault();
        if (style != null)
            player.styleComponent.NextCombatStyle = style;
    }

    // APP:474-500, plus the leash.
    private static void Roam(PlayerBotState state, GamePlayer player, long now)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        bool leashed = Leashed(state, player);
        if (leashed && Vector3.Distance(here, state.Anchor) > LeashRadius)
            state.RoamDestination = state.Anchor;
        else if (state.RoamDestination == null || Vector3.Distance(here, state.RoamDestination.Value) < 80)
            state.RoamDestination = PickRoamPoint(player, leashed ? state.Anchor : here);
        state.Activity = PlayerBotActivity.Roaming;
        PlayerBotStepResult step = state.Mover.StepToward(state.RoamDestination.Value, 20, now);
        if (step is PlayerBotStepResult.Blocked) ReportBlocked(state, player, "roam", state.RoamDestination.Value, now);
        if (step is PlayerBotStepResult.Blocked or PlayerBotStepResult.Arrived)
            state.RoamDestination = null;
    }

    // The rerun of run 1: from level 4 the bots were blocked 18-20 times a minute with no trace of where or why. One line
    // per bot per minute: the last block, and how many there were since the previous line.
    private static void ReportBlocked(PlayerBotState state, GamePlayer player, string walk, Vector3 goal, long now)
    {
        state.BlockedSinceLine++;
        if (now < state.NextBlockedLine || !Log.IsInfoEnabled) return;
        state.NextBlockedLine = now + 60_000;
        Vector3 here = new(player.X, player.Y, player.Z);
        Log.Info($"PLAYER_BOT_BLOCKED bot={player.Name} level={player.Level} walk={walk} at=({here.X:0},{here.Y:0},{here.Z:0}) " +
                 $"goal=({goal.X:0},{goal.Y:0},{goal.Z:0}) distance={(int)Vector3.Distance(here, goal)} zone={player.CurrentZone?.ID} " +
                 $"why=\"{state.Mover.LastBlock}\" count={state.BlockedSinceLine}");
        state.BlockedSinceLine = 0;
    }

    // A bot that has reached no target for 5 minutes says why, once per 5 minutes: run 1's bots roamed for over an hour
    // and the minute line could not tell the death cap from a start area they had outgrown.
    private static void ReportIdle(PlayerBotState state, GamePlayer player, long now)
    {
        if (state.RoamingSince == 0) { (state.RoamingSince, state.NextIdleLine) = (now, now + IdleLineMilliseconds); return; }
        if (now < state.NextIdleLine) return;
        state.NextIdleLine = now + IdleLineMilliseconds;
        int[] byCon = new int[7];                                                        // GREY .. PURPLE
        int outsideLeash = 0;
        foreach (GameNPC npc in player.GetNPCsInRadius(SearchRadius))
        {
            if (!IsExperienceTarget(npc) || !IsAttackable(player, npc)) continue;
            byCon[Math.Clamp((int)ConLevels.GetConColor(player.GetConLevel(npc)) + 3, 0, 6)]++;
            if (Vector3.Distance(new(npc.X, npc.Y, npc.Z), state.Anchor) > LeashRadius + RoamRadius) outsideLeash++;
        }
        if (Log.IsInfoEnabled)
            Log.Info($"PLAYER_BOT_IDLE bot={player.Name} level={player.Level} minutes={(now - state.RoamingSince) / 60_000} " +
                     $"deathSteps={state.DeathSafetySteps} cap={MaximumTargetCon(1, state.DeathSafetySteps)} " +
                     $"fromAnchor={(int)Vector3.Distance(new(player.X, player.Y, player.Z), state.Anchor)} " +
                     $"grey/green/blue/yellow/orange/red/purple={string.Join('/', byCon)} outsideLeash={outsideLeash}");
    }

    // Run r1b: a level-4 bot found 16 grey monsters and one purple within 5,000 units of its start, and nothing else, so
    // it roamed. After 2 minutes with no target a bot moves its leash to the nearest monster in its zone it would hunt and
    // can walk to (a GameBot leaves an outgrown camp for one at its level, AWBC:3244-3300).
    private static void RelocateIfOutgrown(PlayerBotState state, GamePlayer player, long now)
    {
        if (state.HoldAnchor && state.Kit == null) return;       // the lever test keeps its bots on their spots; a build
                                                                 // bot leaves an outgrown camp (levelmatch REVIEW-FIXES 2)
        if (state.RoamingSince == 0 || now - state.RoamingSince < RelocateAfterMilliseconds || now < state.NextRelocateTick ||
            player.CurrentRegionID != state.AnchorRegion || player.CurrentZone is not Zone zone)
            return;
        state.NextRelocateTick = now + RelocateAfterMilliseconds;
        Vector3 here = new(player.X, player.Y, player.Z);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (nav.HasNavmesh(zone) && nav.GetClosestPoint(zone, here, 64, 64, 128, nav.DefaultFilters) == null)
            return;   // off the mesh here, every corridor fails (PlayerTravelPath.cs:48-50): blame no monster for it
        GameNPC[] nearest = RelocationCandidates(WorldMgr.GetNPCsFromRegion(player.CurrentRegionID)
            .Where(npc => npc.CurrentZone == zone && IsExperienceTarget(npc) && IsValidTarget(player, npc, 0) &&
                          AutonomousPlayerPilot.IsEligibleExperienceTarget(ConLevels.GetConColor(player.GetConLevel(npc)),
                              npc.EffectiveLevel, player.Level, 1)),
            npc => new Vector3(npc.X, npc.Y, npc.Z), here, state.RelocateRejected, RelocateCandidates);
        foreach (GameNPC npc in nearest)
        {
            Vector3 spot = new(npc.X, npc.Y, npc.Z);
            if (nav.HasNavmesh(zone) && !PlayerTravelPath.TryBuildCorridor(nav, zone, here, spot, out _))
            {
                state.RelocateRejected.Add(npc);
                continue;
            }
            if (Log.IsInfoEnabled)
                Log.Info($"PLAYER_BOT_RELOCATE bot={player.Name} level={player.Level} from=({state.Anchor.X:0},{state.Anchor.Y:0}) " +
                         $"to=({spot.X:0},{spot.Y:0},{spot.Z:0}) distance={(int)Vector3.Distance(here, spot)} " +
                         $"monster=\"{npc.Name}\" monsterLevel={npc.EffectiveLevel} zone={zone.ID}");
            state.Anchor = spot;
            state.RoamDestination = spot;   // walk there: a task bot levelling for its errand has no leash to pull it
            state.RelocateRejected.Clear();
            return;
        }
        if (Log.IsInfoEnabled)
            Log.Info($"PLAYER_BOT_RELOCATE bot={player.Name} level={player.Level} none checked={nearest.Length} zone={zone.ID}");
    }

    // The nearest candidates not yet found unreachable, so a camp on a ledge or in water is checked once and each try
    // goes on to the next nearest (review of 76e41cf).
    public static T[] RelocationCandidates<T>(IEnumerable<T> eligible, Func<T, Vector3> position, Vector3 here,
        ISet<T> rejected, int count) =>
        eligible.Where(candidate => !rejected.Contains(candidate))
            .OrderBy(candidate => Vector3.DistanceSquared(position(candidate), here))
            .Take(count)
            .ToArray();

    private static Vector3 PickRoamPoint(GamePlayer player, Vector3 centre)
    {
        IPathfindingMgr nav = PathfindingProvider.Instance;
        Vector3? random = nav.GetRandomPoint(player.CurrentZone, centre, RoamRadius, nav.DefaultFilters);
        if (random.HasValue && Vector3.DistanceSquared(random.Value, new(player.X, player.Y, player.Z)) > 6400)
            return random.Value;
        double angle = Random.Shared.NextDouble() * Math.PI * 2;
        return new(centre.X + (float)(Math.Cos(angle) * 700), centre.Y + (float)(Math.Sin(angle) * 700), centre.Z);
    }

    // APP:561-583 without SaveIntoDatabase.
    private static void HandleDeath(PlayerBotState state, GamePlayer player, long now)
    {
        PlayerBotActivity last = state.Activity;                                       // what it was doing when killed
        state.Activity = PlayerBotActivity.Dead;
        if (!state.DeathRecorded)
        {
            ReportDeath(state, player, now, last);
            state.DeathRecorded = true;
            state.Deaths++;
            state.DeathSafetySteps++;
            state.Target = null;
            state.Engaged = false;
            state.RoamDestination = null;
            state.RoamingSince = 0;                                                     // back at the bind point: a fresh start
            state.RelocateRejected.Clear();
            state.Mover.Reset();
            PlayerBotDriverReport.Death();
        }
        if (!state.ReleaseRequested && now - player.DeathTick >= DeathReleaseDelayMilliseconds)
        {
            state.ReleaseRequested = true;
            player.Release(eReleaseType.Normal, true);                                     // GP:1425
        }
    }

    // The rerun of run 1 (76e41cf): from level 3 the bots died about once a minute with no trace of what killed them.
    // The target and its con, the attackers counted on the last think alive, and the time since the release or login.
    // Then the deaths' diagnostics (levelmatch/DEATHS.md 2): the fatal fight as PLAYER_BOT_FIGHT gives a settled one,
    // this life's kills and sits, the killer the Dying event named, and the distance from the bind.
    private static void ReportDeath(PlayerBotState state, GamePlayer player, long now, PlayerBotActivity last)
    {
        if (!Log.IsInfoEnabled) return;
        GameLiving target = state.Target;
        string fight = target == null ? "target=none" :
            $"target=\"{target.Name}\" targetLevel={target.EffectiveLevel} " +
            $"con={ConLevels.GetConColor(player.GetConLevel(target))} engaged={state.Engaged}";
        int fromBind = player.CurrentRegionID == player.BindRegion ? (int)Vector3.Distance(new(player.X, player.Y, player.Z),
            new(player.BindXpos, player.BindYpos, player.BindZpos)) : -1;
        bool inFight = state.Engaged && target != null;       // else the fight fields are the last fight's: -1
        PlayerBotDeathDetail detail = new(last, inFight ? state.EngageHealth : -1,
            inFight ? (now - state.EngagedSince) / 1000 : -1, target?.HealthPercent ?? -1, target != null && state.FromDefence,
            inFight ? state.FightAdds : -1, state.LifeKills, state.LifeRests,
            state.LastKillTick > state.AliveSince ? (now - state.LastKillTick) / 1000 : -1, PowerPercent(player.MaxMana, player.ManaPercent),
            inFight ? state.Swings : -1, inFight ? state.NotVisible : -1, null, 0, false, fromBind);   // the killer: DeathLine
        string head = $"PLAYER_BOT_DEATH bot={player.Name} level={player.Level} at=({player.X},{player.Y},{player.Z}) " +
                      $"zone={player.CurrentZone?.ID} {fight} attackers={state.Attackers} deathSteps={state.DeathSafetySteps} " +
                      $"aliveSec={(now - state.AliveSince) / 1000} task={state.Task != null}";
        if (AwaitsDying(state.Killer, player.IsBeingHandledByReaperService))
            state.PendingDeath = (head, detail, target);                                   // the next think writes it
        else
            Log.Info(DeathLine(head, detail, state.Killer, target));
    }

    // One line per fight the bot was in when it settles (SettleTarget), so a fight's cost shows beside the deaths.
    private static void ReportFight(PlayerBotState state, GamePlayer player, long now, bool killed)
    {
        if (!Log.IsInfoEnabled) return;
        GameLiving target = state.Target;
        Log.Info(PlayerBotDriverReport.FormatFight(new(player.Name, player.Level, target.Name, target.EffectiveLevel,
            ConLevels.GetConColor(player.GetConLevel(target)), state.FromDefence, killed, state.EngageHealth, player.HealthPercent,
            state.EngageMana, PowerPercent(player.MaxMana, player.ManaPercent), (now - state.EngagedSince) / 1000, state.FightAdds,
            state.Swings, state.NotVisible, state.FightCasts, state.LifeKills, state.LifeRests)));
    }

    // APP:585-624, changed: every 10 s, no inventory count, no saves; after 15 minutes with no progress, back to the
    // anchor in the same region (the pilot moved to the capital in another region, which needs re-entry).
    private static bool RecoverIfStuck(PlayerBotState state, GamePlayer player, long now)
    {
        if (now < state.NextProgressCheckTick) return false;
        state.NextProgressCheckTick = now + ProgressCheckMilliseconds;
        Vector3 here = new(player.X, player.Y, player.Z);
        long money = player.GetCurrentMoney();
        if (Vector3.Distance(here, state.ProgressPosition) >= MeaningfulMoveDistance || player.Experience != state.ProgressExperience ||
            money != state.ProgressMoney || player.InCombat || player.IsAttacking || player.IsCasting)
        {
            state.ProgressPosition = here;
            state.ProgressExperience = player.Experience;
            state.ProgressMoney = money;
            state.LastProgressTick = now;
            return false;
        }
        if (now - state.LastProgressTick < StuckMilliseconds || player.CurrentRegionID != state.AnchorRegion) return false;
        state.Mover.Stop(now);
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        player.TargetObject = null;
        state.Target = null;
        state.RoamDestination = null;
        state.RoamingSince = 0;
        state.RelocateRejected.Clear();
        player.MoveTo(state.AnchorRegion, (int)state.Anchor.X, (int)state.Anchor.Y, (int)state.Anchor.Z, player.Heading);
        state.Mover.Reset();
        state.LastProgressTick = now;
        PlayerBotDriverReport.StuckRecovered();
        return true;
    }

    // APP:392-401.
    private static void Rest(PlayerBotState state, GamePlayer player, long now)
    {
        state.Activity = PlayerBotActivity.Resting;
        state.Mover.Stop(now);
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        if (!player.IsSitting)
        {
            player.Sit(true);
            state.LifeRests++;                                                             // FIGHT and DEATH rests=
        }
    }

    private static bool Recovered(GamePlayer player) =>                                    // APP:389-390
        player.HealthPercent >= 100 && (player.MaxMana <= 0 || player.ManaPercent >= 100) && player.EndurancePercent >= 100;

    private static void TryLoot(PlayerBotState state, GamePlayer player, long now)       // APP:403-414
    {
        if (now < state.NextLootTick) return;
        state.NextLootTick = now + LootMilliseconds;
        foreach (GameStaticItem item in player.GetItemsInRadius(LootRadius))
        {
            if (item is WorldInventoryItem or GameMoney)
                player.PickupObject(item, false);
        }
    }

    // On the loop thread: these calls use per-tick pooled lists (GameObject.cs:937-985).
    private static void CountNeighbours(PlayerBotState state, GamePlayer player, long now)
    {
        if (now < state.NextNeighbourCountTick) return;
        state.NextNeighbourCountTick = now + NeighbourCountMilliseconds;
        int playerBots = 0, gameBots = 0;
        foreach (GamePlayer other in player.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            if (other != player && other.Client is IPlayerBotClient) playerBots++;
        foreach (GameNPC npc in player.GetNPCsInRadius(WorldMgr.VISIBILITY_DISTANCE))
            if (npc is GameBot { IsAutonomousWorldBot: true }) gameBots++;
        state.NearPlayerBots = playerBots;
        state.NearGamebots = gameBots;
    }

    private static void ObserveXp(PlayerBotState state, GamePlayer player)
    {
        if (player.Level > state.LastLevel)
        {
            PlayerBotDriverReport.LevelUp(player.Level - state.LastLevel, player.Level);
            if (state.Kit != null)                                                   // levelmatch: train as a GameBot would
            {
                if (PlayerBotKit.TrainTo(player, state.Kit, out string line) > 0) state.Retrains++;
                if (Log.IsInfoEnabled) Log.Info(line);
                if (state.Target != null) (state.Spell, state.SpellLine) = FindDamageSpell(player);   // a new spell
            }
        }
        long gained = player.Experience - state.LastExperience;
        if (gained > 0)
        {
            PlayerBotDriverReport.Experience(gained);
            state.Gained += gained;
        }
        state.LastLevel = player.Level;
        state.LastExperience = player.Experience;
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        foreach (PlayerBotState state in _states.Values)
            state.Stopped = true;
        _states.Clear();
        PlayerBotDriverReport.Stop();
    }
}
