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
    public long CappedSince;                                     // DEATHS.md A4: the first search that passed monsters over
    public ConColor? SafestPassedOver;                           //   for the cap and found none (CappedClock), and the
    public ConColor? FallbackCap;                                //   safest con it passed over; a GameBot's fallback camp's
    public bool HuntAvoided;                                     //   con, its names hunted at a stale one, and its failed
    public readonly List<(Vector3 At, long Until)> FailedCamps = new();   // camps for 15 minutes (PlanRelocation)
    public readonly Dictionary<string, long> AvoidUntil = new(StringComparer.OrdinalIgnoreCase);   // DEATHS.md A3: the
    public bool KillerAvoided;                                   //   names of deaths' killers and targets (IsAvoided)
    public long AliveSince;                                      // PLAYER_BOT_DEATH: the last release or login
    public int Attackers;                                        //   and the attackers on the last think alive
    public long NextBlockedLine;                                 // PLAYER_BOT_BLOCKED
    public int BlockedSinceLine;
    public long NextWakeScanTick;                                // PlayerBotWake's 3,600-unit scan (the #137 lever)
    public long QuestJournal = long.MinValue;                    //   and the quest rule's names, read again only when
    public List<JournalStep> QuestSteps;                         //   the journal changes (PlayerBotWake.JournalKey)
    public List<GameNPC> QuestGivers;
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
    public bool SelfCast;                                        // a heal or summon on itself is being cast (DEATHS.md
    public long NextSelfHealTick, NextSummonTick;                //   A6, A5), the next tries, and the level at which it
    public byte NoPetSpellLevel;                                 //   found no main-pet summon (not looked for again then)
    public byte NoSelfHealLevel;                                 //   or no heal (#167 F2: looked for from 85% too)
    public long NextCombatPetTick;                               // #167 F1: a Theurgist's next pet at its target, and the
    public byte NoCombatPetLevel;                                //   level at which it found no such summon
    public long NextQuickcastTick;                               // Quickcast's next try (UseQuickcast)
    public bool EngagePet;                                       // PLAYER_BOT_FIGHT pet=, heals= and qc=: a living pet at
    public int FightHeals;                                       //   the fight's first think, its heals on itself since,
    public bool FightQuickcast;                                  //   and a cast with Quickcast on
    public (string Head, PlayerBotDeathDetail Detail, GameObject Target)? PendingDeath;   // a DEATH line awaiting Dying (AwaitsDying)
}

// HearthDAoC player bots (#137): a thin copy of the /bot pilot's think (bots/autonomous/AutonomousPlayerPilot.cs:229-374) for a
// player bot: no quickbar file, party, HUD or saves. Each bot is ticked every 250 ms by its own timer. It keeps the bot
// alive, heals itself, keeps its pet, rests, loots, fights back a monster that hits it, else picks one the way a
// GameBot does (AutonomousWorldBotController.cs:3050-3148, no camp), walks with PlayerBotPathMover, faces, sets "target
// in view" from the navmesh, and casts or swings. It writes no database rows on the loop; autosave and logout save. It
// runs in TimerService's phase, in parallel with every other timer (the bot's own regeneration and release timers too),
// so the player's position, heading, target, sitting and areas change there instead of in ClientService's packet phase;
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
    public const int CappedRelocateMilliseconds = 30_000;    // DEATHS.md A4: nothing in reach fits the cap this long
    public const int MaxDeathSteps = (int)ConColor.YELLOW - (int)ConColor.GREEN;   // a solo GameBot's (AWBC:3756, :3793-3796)
    public const string Outgrown = "outgrown", Capped = "capped";                  // RelocateReason
    public static readonly long StuckMilliseconds = (long)AutonomousStuckWatchdog.StuckThreshold.TotalMilliseconds; // 15 min
    public const int MaxFruitlessCasts = 3;                  // the cast watch (levelmatch/DESIGN.md 2.7)
    public const int SpellBlockMilliseconds = 10_000;        // (inf.) a few swings, then the spell again
    public const int SelfHealBelowPercent = 60;              // DEATHS.md A6 (a GameBot's: below 65%, BotBrain.cs:4897)
    public const int RestHealBelowPercent = 85;              // #167 F2: out of a fight, a heal before the sit,
    public const int RestHealPowerPercent = 50;              //   with this much power left after it (rest-work's model)
    public const int SelfHealRetryMilliseconds = 2_000;      // (inf.) a refused heal is not asked for every think
    public const int PetPowerPercent = 30;                   // DEATHS.md A5: summon at this much power or more
    public const int SummonRetryMilliseconds = 2_000;        // (inf.) as the heal's
    public const int QuickcastRetryMilliseconds = 2_000;     // (inf.) as the heal's

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
            state.CappedSince = 0;
            state.FallbackCap = null;
            state.HuntAvoided = false;
            state.FailedCamps.Clear();
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
            state.KillerAvoided = false;
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
        if (WaitsForSelfCast(state, player) || SelfHeal(state, player, now) || SummonPet(state, player, now))
            return;                                                                      // 11b. DEATHS.md A6, A5: before a rest
        if (state.RecoverBeforePull && Recovered(player)) state.RecoverBeforePull = false;
        if (MayRest(Attacker(state, player, now, anyHitter: true) != null, state.Engaged && state.Target?.IsAlive == true) &&
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
            if (HoldsForRelocation(CanRelocate(state, player), state.CappedSince, state.RoamDestination == state.Anchor))
            {
                state.Mover.Stop(now);                                                   //    capped: no walk into the camp
                state.Activity = PlayerBotActivity.Roaming;                              //    it cannot hunt (no target)
            }
            else Roam(state, player, now);
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
        bool quickcasting = player.effectListComponent.ContainsEffectForEffectType(eEffect.QuickCast);
        if (now >= state.NextCombatChoiceTick && !player.IsCasting)                    // a cast choice: the pet or the
            ChooseCombatSpell(state, player, target, now);                              //   damage spell (#167 F1)
        bool interruptible = state.Spell != null && Interruptible(state.Spell);
        bool canCast = ShouldCast(state.Spell != null, player.ManaPercent, now, state.SpellBlockedUntil,
            player.IsBeingInterruptedByOther && !quickcasting, interruptible);   // a quickcast one goes (SpellHandler.cs:581)
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
            state.FightAdds = state.Swings = state.NotVisible = state.FightCasts = state.FightHeals = 0;
            state.EngagePet = player.ControlledBrain?.Body?.IsAlive == true;
            state.FightQuickcast = false;
            if (player.ControlledBrain != null)                                         // its pet too: the pet window's
                player.CommandNpcAttack();                                              //   attack (PetWindowHandler.cs:79-82)
        }
        state.Engaged = true;
        state.FightAdds = Math.Max(state.FightAdds, state.Attackers);
        CountSwing(state, player.attackComponent.attackAction.LastAttackData);
        if (canCast && !player.IsCasting && now >= state.NextCombatChoiceTick &&
            player.IsWithinRadius(target, player.castingComponent.CalculateSpellRange(state.Spell)))
        {
            player.attackComponent.StopAttack();
            if (!CountsInCastWatch(state.Spell))                                        // a pet at the target (#167 F1)
            {
                if (!player.CastSpell(state.Spell, state.SpellLine))
                {
                    state.NextCombatPetTick = now + SummonRetryMilliseconds;           // the damage spell at the next think
                    state.Activity = PlayerBotActivity.Fighting;
                    return;
                }
                state.NextCombatPetTick = now + Math.Max(250, state.Spell.CastTime + 100);   // AutonomousPetSupport.cs:646
            }
            else
            {
                (state.CastAttempts, state.CastHealthMark, bool stalled) =
                    WatchCast(state.CastTarget == target, state.CastAttempts, state.CastHealthMark, target.HealthPercent);
                state.CastTarget = target;
                if (stalled || !player.CastSpell(state.Spell, state.SpellLine))         // APP:345-353 without the Animist policy
                {
                    // CastSpell only queues the request (CastingComponent.cs:116-144); range and sight fail later
                    // (SpellHandler.cs:681, :694, :731). A run of requests that hurt nothing: swing instead for a while.
                    state.SpellBlockedUntil = now + SpellBlockMilliseconds;
                    state.CastAttempts = 0;
                    state.CastFailures++;
                    state.Activity = PlayerBotActivity.Fighting;
                    return;                                                             // the next think walks into melee
                }
            }
            state.NextCombatChoiceTick = now + Math.Max(900, state.Spell.CastTime + 250);
            state.FightCasts++;
            state.FightQuickcast |= quickcasting;
            state.Activity = PlayerBotActivity.Casting;
        }
        else if (!player.IsCasting)
        {
            if (UseQuickcast(state, player, now, quickcasting, interruptible)) return;  // then the next think casts
            if (now >= state.NextStyleTick)
            {
                state.NextStyleTick = now + StyleMilliseconds;
                QueueUsableStyle(player);                                               // APP:694-710 without the quickbar filter
            }
            if (!player.attackComponent.AttackState && CanSwing(player.ControlledBrain?.Body))
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

    /// <summary>Whether a caster with Quickcast uses it this think: its damage spell is blocked only by the hits' interrupt
    /// (ShouldCast without it would cast), and a player's use would take (QuickCastAbilityHandler.cs:41-84): not on
    /// already (the ability toggles it off, :51-55), out of its 30-s disable after a quickcast spell (SpellHandler.cs:
    /// 1430-1434, CastingComponent.cs:487), not casting (:44-48); the retry timer due. A GameBot never quickcasts
    /// (BotBrain.cs:3959, GameBot.cs:2583-2585); a player does, so a player bot does.</summary>
    public static bool ShouldQuickcast(bool hasQuickcast, bool quickcastOn, int disabledMilliseconds, long nextTry, bool casting,
        bool hasSpell, byte manaPercent, long now, long blockedUntil, bool interrupted, bool interruptible) =>
        hasQuickcast && !quickcastOn && disabledMilliseconds <= 0 && now >= nextTry && !casting && interrupted &&
        interruptible && ShouldCast(hasSpell, manaPercent, now, blockedUntil, false, interruptible);

    /// <summary>The server's own test for the interrupt timer (SpellHandler.cs:571).</summary>
    public static bool Interruptible(Spell spell) => !spell.Uninterruptible && !spell.IsInstantCast;

    /// <summary>The self-heal's gate, out of a cast and the retry timer due: in a fight below SelfHealBelowPercent
    /// (DEATHS.md A6); out of one below RestHealBelowPercent with RestHealPowerPercent of its power or more left after the
    /// heal's cost (#167 F2), then it sits for power: a heal is quicker than sitting for health.</summary>
    public static bool WantsSelfHeal(byte healthPercent, bool casting, long now, long nextTry, bool inFight, byte manaPercent,
        int costPercent) =>
        !casting && now >= nextTry && (inFight
            ? healthPercent < SelfHealBelowPercent
            : healthPercent < RestHealBelowPercent && manaPercent - costPercent >= RestHealPowerPercent);

    /// <summary>A spell's nominal cost in percent of the pool, rounded up: a negative power is a percent already
    /// (SpellHandler.cs:1207-1213); 0 with no pool.</summary>
    public static int CostPercent(int maxMana, int power) =>
        power < 0 ? -power : maxMana <= 0 ? 0 : (power * 100 + maxMana - 1) / maxMana;

    /// <summary>The heal a bot casts on itself (DEATHS.md A6): a Heal on a realm mate, itself or its group, at or below
    /// its level, no song, ready, and while hits interrupt the bot an instant or uninterruptible one only (a GameBot's
    /// rule, BotBrain.cs:4761); the most health back (a negative value is a percent, GameBot.cs:1887-1892), then an
    /// instant one, then the higher level.</summary>
    public static (Spell, SpellLine) FindSelfHeal(IEnumerable<(Spell Spell, SpellLine Line)> spells, int level, int maxHealth,
        bool interrupted, Func<Spell, bool> ready)
    {
        Spell best = null;
        SpellLine bestLine = null;
        double bestAmount = 0;
        foreach ((Spell spell, SpellLine line) in spells)
        {
            if (spell.SpellType is not eSpellType.Heal ||
                spell.Target is not (eSpellTarget.REALM or eSpellTarget.SELF or eSpellTarget.GROUP) || spell.Level > level ||
                spell.NeedInstrument || interrupted && Interruptible(spell) || !ready(spell))
                continue;
            double amount = spell.Value >= 0 ? spell.Value : maxHealth * -spell.Value / 100;
            if (best == null || amount > bestAmount || amount == bestAmount &&
                (spell.IsInstantCast != best.IsInstantCast ? spell.IsInstantCast : spell.Level > best.Level))
                (best, bestLine, bestAmount) = (spell, line, amount);
        }
        return (best, bestLine);
    }

    /// <summary>The summon's gate (DEATHS.md A5): no pet, nothing hitting the bot, out of combat and of a cast, at
    /// PetPowerPercent or more, the retry timer due. A pet that died is summoned again so.</summary>
    public static bool WantsPet(bool hasPet, bool beingHit, bool inCombat, byte manaPercent, bool casting, long now, long nextTry) =>
        !hasPet && !beingHit && !inCombat && manaPercent >= PetPowerPercent && !casting && now >= nextTry;

    /// <summary>The pet a pet class summons (DEATHS.md A5): the main-pet summon (AutonomousPetSupport.IsMainPetSummon,
    /// AutonomousPetSupport.cs:1092-1100) of the highest level at or below the bot's, then the higher ID, as a GameBot
    /// picks its highest rank (AutonomousPetSupport.ChooseFromPool).</summary>
    public static (Spell, SpellLine) FindPetSpell(IEnumerable<(Spell Spell, SpellLine Line)> spells, int level)
    {
        Spell best = null;
        SpellLine bestLine = null;
        foreach ((Spell spell, SpellLine line) in spells)
        {
            if (!AutonomousPetSupport.IsMainPetSummon(spell.SpellType) || spell.Level > level) continue;
            if (best == null || spell.Level > best.Level || spell.Level == best.Level && spell.ID > best.ID)
                (best, bestLine) = (spell, line);
        }
        return (best, bestLine);
    }

    /// <summary>The pet a Theurgist casts at its target (#167 F1; a GameBot's, AutonomousPetSupport.cs:636-650): a
    /// SummonTheurgistPet at or below the bot's level that is ready (recast, power), the highest, then the higher ID, as
    /// FindPetSpell picks.</summary>
    public static (Spell, SpellLine) FindCombatPetSpell(IEnumerable<(Spell Spell, SpellLine Line)> spells, int level,
        Func<Spell, bool> ready)
    {
        Spell best = null;
        SpellLine bestLine = null;
        foreach ((Spell spell, SpellLine line) in spells)
        {
            if (spell.SpellType is not eSpellType.SummonTheurgistPet || spell.Level > level || !ready(spell)) continue;
            if (best == null || spell.Level > best.Level || spell.Level == best.Level && spell.ID > best.ID)
                (best, bestLine) = (spell, line);
        }
        return (best, bestLine);
    }

    /// <summary>The combat pet's gate at a cast choice (#167 F1): one ready, the target within its range and in view (a
    /// pet cast is no damage spell, so the cast watch never stops one the server refuses for sight), out of a cast, the
    /// retry timer due.</summary>
    public static bool WantsCombatPet(bool hasSpell, bool inRange, bool casting, long now, long nextTry) =>
        hasSpell && inRange && !casting && now >= nextTry;

    /// <summary>Whether a cast counts in the cast watch (WatchCast): a damage spell. A summon's pet hurts the target only
    /// once it reaches it, so three summons in a row would read as fruitless and block the bot's spells (#167 F1).</summary>
    public static bool CountsInCastWatch(Spell spell) => spell.SpellType is not eSpellType.SummonTheurgistPet;

    /// <summary>A Necromancer in shade form cannot swing (AttackComponent.cs:511-516): its servant fights for it.</summary>
    public static bool CanSwing(GameNPC pet) => pet is not NecromancerPet;

    /// <summary>Power for a spell's nominal cost: a negative power is a percent of the pool (SpellHandler.cs:1207-1213).
    /// The cast checks the real cost.</summary>
    public static bool HasPowerFor(int maxMana, int mana, int power) =>
        mana >= (power < 0 ? maxMana * -power / 100 : power);

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

    // Drops a target that died or is no longer fair game (one fighting the bot or its pet stays fair game whatever its
    // con, FightsBotOrPet). A death counts for the TALLY of the first bot to report it only, so bots sharing a camp sum
    // to the DRIVER line's kills.
    private static void SettleTarget(PlayerBotState state, GamePlayer player)
    {
        if (state.Target == null || IsValidTarget(player, state.Target, Cap(state)) ||
            state.Target is GameNPC npc && FightsBotOrPet(npc.TargetObject, player, player.ControlledBrain?.Body) &&
            IsAttackable(player, npc))
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
    // the adds by a candidate at its edge. One over the cap is passed over, never taken (DEATHS.md A4: the old rollback
    // reset the death steps to fit it), and the capped clock runs while the wider ring finds nothing else. An avoided
    // name is passed over too, unless the bot was sent to a camp of them (HuntAvoided).
    private static GameNPC FindWithin(PlayerBotState state, GamePlayer player, ushort radius, long now)
    {
        bool passedOver = false;
        ConColor? safestOver = null;
        ConColor cap = Cap(state), natural = MaximumTargetCon(1, 0);
        bool leashed = Leashed(state, player);
        List<(GameNPC Npc, Vector3 At, int Score)> candidates = _candidates ??= new();
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _adds ??= new();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)(radius + AddScanMargin)))
        {
            if (!IsExperienceTarget(npc)) continue;                                     // monsters only, adds too
            Vector3 at = new(npc.X, npc.Y, npc.Z);
            if (AddReach(player, npc) is > 0 and int reach) adds.Add((npc, at, reach));
            if (!player.IsWithinRadius(npc, radius) || !IsValidTarget(player, npc, natural) ||
                npc == state.Unreachable && now < state.UnreachableUntil)
                continue;
            ConColor con = ConLevels.GetConColor(player.GetConLevel(npc));
            if (!AutonomousPlayerPilot.IsEligibleExperienceTarget(con, npc.EffectiveLevel, player.Level, 1) ||
                leashed && Vector3.Distance(at, state.Anchor) > LeashRadius + RoamRadius)
                continue;
            int score = TargetScore(player.GetDistanceTo(npc), con);                    // AWBC:3100
            if (con <= cap && (state.HuntAvoided || !IsAvoided(state.AvoidUntil, npc.Name, now)))
                candidates.Add((npc, at, score));
            else
            {
                passedOver = true;                                                      // over the cap, or a death's name
                if (con > cap && (safestOver == null || con < safestOver)) safestOver = con;
            }
        }
        GameNPC best = PickUncrowded(candidates, adds, uncrowdedOnly: radius < SearchRadius);
        candidates.Clear();                                                             // no monster held past the search
        adds.Clear();
        if (best != null || radius >= SearchRadius)
        {
            state.CappedSince = CappedClock(state.CappedSince, best != null, passedOver, now);
            state.SafestPassedOver = best == null ? safestOver : null;                  // FallbackInPlace's
        }
        return best;
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
    // while an orange monster killed it, and each death lowered its con cap further. anyHitter: the one found unreachable
    // too, for the rest gate and the summon (SkipsHitter).
    private static GameNPC Attacker(PlayerBotState state, GamePlayer player, long now, bool anyHitter = false)
    {
        GameNPC best = null;
        int bestDistance = int.MaxValue;
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
        {
            if (living is not GameNPC npc || !IsAttacking(player, npc) ||
                SkipsHitter(npc == state.Unreachable, now, state.UnreachableUntil, anyHitter))
                continue;
            int distance = player.GetDistanceTo(npc);
            if (distance < bestDistance) { best = npc; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>Whether Attacker passes over a monster hitting the bot: the one Engage found unreachable, while
    /// UnreachableUntil lasts, when it picks whom to fight back. Never for anyHitter (the rest gate, MayRest; the
    /// summon): that monster still hits the bot, a sitting player takes double melee damage that never misses
    /// (AttackAction.cs:236-238, AttackComponent.cs:2206-2208), and no hit stands it up. The old gate, InCombat, kept
    /// such a bot standing.</summary>
    public static bool SkipsHitter(bool unreachable, long now, long unreachableUntil, bool anyHitter) =>
        !anyHitter && unreachable && now < unreachableUntil;

    /// <summary>For a task (the errands): true while the bot has a fight to finish, a monster hitting it or the one it
    /// is engaged with; then the task gives the think to the driver, which fights that one and seeks no other.</summary>
    public static bool UnderAttack(PlayerBotState state, long now) =>
        (state.Engaged && state.Target != null && state.Player.InCombat) || Attacker(state, state.Player, now) != null;

    private static bool IsAttacking(GamePlayer player, GameLiving target) =>
        target is GameNPC npc && npc.TargetObject == player && IsAttackable(player, npc);

    /// <summary>SettleTarget's rule for a target over the con cap: it stays while it fights the bot or the bot's pet. A
    /// pet takes its owner's fights (CommandNpcAttack; a defensive pet the owner's attackers, ControlledMobBrain
    /// OnOwnerAttacked), and the monster turns on it; dropped then, the bot sat (MayRest) or pulled another while its pet
    /// fought on alone, and the monster came back to it once the pet died.</summary>
    public static bool FightsBotOrPet(GameObject monstersTarget, GameObject bot, GameObject pet) =>
        monstersTarget != null && (monstersTarget == bot || monstersTarget == pet);

    private static bool IsValidTarget(GamePlayer player, GameLiving target, ConColor cap) =>     // APP:447-463, group size 1
        IsAttackable(player, target) && !player.IsObjectGreyCon(target) &&
        ConLevels.GetConColor(player.GetConLevel(target)) <= cap;

    // The bot's con cap: its death cap, or at a GameBot's fallback camp that camp's own con (AWBC:3446-3452), as
    // AutonomousDeathRecoveryPolicy.EncounterMaximum gives a GameBot's pull filter.
    private static ConColor Cap(PlayerBotState state) => AutonomousDeathRecoveryPolicy.EncounterMaximum(
        MaximumTargetCon(1, state.DeathSafetySteps), MaximumTargetCon(1, 0), state.FallbackCap, false);

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

    /// <summary>A death's steps: one more, at most MaxDeathSteps (green). They never go down: a GameBot's are clamped
    /// so (AWBC:3793-3796) and reset only when its controller is made (AWBC:3758-3765), neither over time nor on a
    /// level-up. A capped bot moves to a camp that fits instead (PlanRelocation; DEATHS.md A4).</summary>
    public static int NextDeathSteps(int deathSteps) => Math.Min(deathSteps + 1, MaxDeathSteps);

    /// <summary>Why the bot moves its leash now, or null: Capped when its searches have found monsters in reach but none
    /// under its cap for CappedRelocateMilliseconds (DEATHS.md A4), Outgrown when it has reached no target for
    /// RelocateAfterMilliseconds.</summary>
    public static string RelocateReason(long roamingSince, long cappedSince, long now) =>
        cappedSince != 0 && now - cappedSince >= CappedRelocateMilliseconds ? Capped :
        roamingSince != 0 && now - roamingSince >= RelocateAfterMilliseconds ? Outgrown : null;

    /// <summary>PLAYER_BOT_RELOCATE for a move: its fields as they were (runs/legcheck.py reads bot= and to), then the
    /// reason (RelocateReason) and the fallback camp's con, - for none (the death cap holds, Cap).</summary>
    public static string RelocateLine(string bot, int level, Vector3 from, Vector3 to, int distance, string monster,
        int monsterLevel, ushort zone, string reason, ConColor? fallback) =>
        $"PLAYER_BOT_RELOCATE bot={bot} level={level} from=({from.X:0},{from.Y:0}) to=({to.X:0},{to.Y:0},{to.Z:0}) " +
        $"distance={distance} monster=\"{monster}\" monsterLevel={monsterLevel} zone={zone} reason={reason} " +
        $"fallback={fallback?.ToString() ?? "-"}";

    /// <summary>PLAYER_BOT_RELOCATE when no camp checked had a corridor (or none was found): checked= of them.</summary>
    public static string RelocateNoneLine(string bot, int level, int candidates, ushort zone, string reason) =>
        $"PLAYER_BOT_RELOCATE bot={bot} level={level} none checked={candidates} zone={zone} reason={reason}";

    /// <summary>The capped clock after a search of the wider ring (or one that found a target): set at the first that
    /// passed a monster over for the cap and found none, kept while that lasts, 0 once a search finds a target or
    /// nothing to pass over.</summary>
    public static long CappedClock(long cappedSince, bool found, bool passedOver, long now) =>
        found || !passedOver ? 0 : cappedSince != 0 ? cappedSince : now;

    // The lever test's spot bots never move their leash; a build bot does (levelmatch REVIEW-FIXES 2), in its anchor
    // region (RelocateIfOutgrown plans in the bot's zone there).
    private static bool CanRelocate(PlayerBotState state, GamePlayer player) =>
        (!state.HoldAnchor || state.Kit != null) && player.CurrentRegionID == state.AnchorRegion;

    /// <summary>A capped bot that can relocate stands where it is until it does, unless it walks to its anchor (a
    /// relocation's spot, or back to the leash): no roam into a camp it cannot hunt, as a GameBot holds still after a
    /// death until it plans its next camp (HoldSoloDeathRecovery, AWBC:2998-3012). One that cannot roams as before
    /// (FallbackInPlace): held, it stood there for good.</summary>
    public static bool HoldsForRelocation(bool canRelocate, long cappedSince, bool walkingToAnchor) =>
        canRelocate && cappedSince != 0 && !walkingToAnchor;

    /// <summary>The cap a capped bot takes where it is when it cannot move its leash (a lever spot bot, one out of its
    /// anchor region or off the mesh, or no camp in its zone): the safest con it passed over, as a GameBot takes the
    /// safest camp there is rather than plan forever (AutonomousBotDecisionEngine.cs:287-313); the old rollback did so
    /// at once. Null for an outgrown bot, and for one that passed over only names it avoids: they expire.</summary>
    public static ConColor? FallbackInPlace(string reason, ConColor? safestPassedOver) =>
        reason == Capped ? safestPassedOver : null;

    /// <summary>The relocation's spots (DEATHS.md A4), at most count, nearest first: monsters the bot would hunt
    /// (Huntable, not rejected for want of a corridor) that are its camps. A monster's camp is the non-grey monsters
    /// within RoamRadius of it, itself too; it fits the cap when the monster is at or under the cap and so is the
    /// camp's median con, as a GameBot's camp fits by its levels' median (AWBC:3312-3330, :3386; unlike a GameBot's
    /// camp, one monster name, those over the natural cap count too). A name the bot avoids (DEATHS.md A3) counts as
    /// over the cap, and such a monster is no spot: a camp full of them fits no cap. With none that fits, the GameBot's
    /// fallback (SelectSafestAvailableAfterDeath, AutonomousBotDecisionEngine.cs:293-313; AWBC:3422-3437): of the camps
    /// with a median at or under the natural cap, the fresh ones while there are any (most of the camp not avoided, as
    /// PreferFreshTargets, AutonomousDeathRecoveryPolicy.cs:26-38; else Stale, and the bot hunts their names there as a
    /// GameBot hunts its camp's monster, AWBC:3087-3088), not one the bot died at in the last 15 minutes (within
    /// RoamRadius of a failedCamps spot; AWBC:3246, :3803-3804) while another is left, those with the safest median, and
    /// that median as Fallback, the con the bot hunts there (AWBC:3446-3452).</summary>
    public static (T[] Spots, ConColor? Fallback, bool Stale) PlanRelocation<T>(
        IReadOnlyList<(T Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters, Vector3 here, ConColor cap,
        ConColor natural, IReadOnlyCollection<Vector3> failedCamps, ISet<T> rejected, int count) where T : class
    {
        Dictionary<(int, int), List<int>> grid = new();
        for (int i = 0; i < monsters.Count; i++)
        {
            if (monsters[i].Con <= ConColor.GREY) continue;
            (int, int) cell = CampCell(monsters[i].At);
            if (!grid.TryGetValue(cell, out List<int> list)) grid[cell] = list = new();
            list.Add(i);
        }
        int[] order = Enumerable.Range(0, monsters.Count)
            .Where(i => monsters[i].Huntable && monsters[i].Con > ConColor.GREY && !rejected.Contains(monsters[i].Npc))
            .OrderBy(i => Vector3.DistanceSquared(monsters[i].At, here))
            .ToArray();
        List<T> fit = new();
        foreach (int i in order)
        {
            if (monsters[i].Con <= cap && !monsters[i].Avoided && CampCon(monsters, grid, i).Fresh <= cap)
                fit.Add(monsters[i].Npc);
            if (fit.Count == count) break;
        }
        if (fit.Count > 0) return (fit.ToArray(), null, false);
        List<(int I, ConColor Median, bool Fresh, bool Failed)> safe = new();
        foreach (int i in order)
        {
            (ConColor median, ConColor fresh) = CampCon(monsters, grid, i);
            if (median <= natural)
                safe.Add((i, median, fresh <= natural, failedCamps != null && failedCamps.Any(f =>
                    Vector3.DistanceSquared(monsters[i].At, f) <= RoamRadius * RoamRadius)));
        }
        if (safe.Count == 0) return (Array.Empty<T>(), null, false);
        bool stale = !safe.Any(c => c.Fresh);
        IEnumerable<(int I, ConColor Median, bool Fresh, bool Failed)> pool = stale ? safe : safe.Where(c => c.Fresh);
        pool = pool.Any(c => !c.Failed) ? pool.Where(c => !c.Failed) : pool;
        ConColor safest = pool.Min(c => c.Median);
        return (pool.Where(c => c.Median == safest).Take(count).Select(c => monsters[c.I].Npc).ToArray(), safest, stale);
    }

    private static (int, int) CampCell(Vector3 at) => ((int)MathF.Floor(at.X / RoamRadius), (int)MathF.Floor(at.Y / RoamRadius));

    // The median con of the camp at monsters[centre], its non-grey monsters within RoamRadius (the grid's cells), and the
    // same with each avoided one as purple.
    private static (ConColor Median, ConColor Fresh) CampCon<T>(
        IReadOnlyList<(T Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters,
        Dictionary<(int, int), List<int>> grid, int centre)
    {
        Span<int> byCon = stackalloc int[7], fresh = stackalloc int[7];                // GREY .. PURPLE
        int members = 0;
        Vector3 at = monsters[centre].At;
        (int cx, int cy) = CampCell(at);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (grid.TryGetValue((cx + dx, cy + dy), out List<int> list))
                    foreach (int j in list)
                        if (Vector3.DistanceSquared(monsters[j].At, at) <= RoamRadius * RoamRadius)
                        {
                            byCon[Math.Clamp((int)monsters[j].Con + 3, 0, 6)]++;
                            fresh[monsters[j].Avoided ? 6 : Math.Clamp((int)monsters[j].Con + 3, 0, 6)]++;
                            members++;
                        }
        return (Median(byCon, members), Median(fresh, members));
    }

    private static ConColor Median(ReadOnlySpan<int> byCon, int members)
    {
        for (int c = 0, seen = 0; c < byCon.Length; c++)                                // the members/2-th, sorted
            if ((seen += byCon[c]) > members / 2) return (ConColor)(c - 3);
        return ConColor.PURPLE;
    }

    /// <summary>Whether a monster's name is one the bot remembers from a death (DEATHS.md A3), until its time is up. A
    /// GameBot's names, by any case, last FailureMemoryMilliseconds (AWBC:147-148, :3811).</summary>
    public static bool IsAvoided(IReadOnlyDictionary<string, long> avoidUntil, string name, long now) =>
        name != null && avoidUntil.TryGetValue(name, out long until) && now < until;

    /// <summary>Remembers a death's name for AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds (15 minutes, the
    /// GameBot's failed camp and target memory, AWBC:3804, :3811), dropping those whose time is up (AWBC:3230-3233).</summary>
    public static void Remember(Dictionary<string, long> avoidUntil, string name, long now)
    {
        foreach (string old in avoidUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            avoidUntil.Remove(old);
        if (!string.IsNullOrEmpty(name)) avoidUntil[name] = now + AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds;
    }

    /// <summary>The names the bot avoids now (PLAYER_BOT_DEATH avoided=).</summary>
    public static int AvoidedCount(IReadOnlyDictionary<string, long> avoidUntil, long now) =>
        avoidUntil.Count(pair => now < pair.Value);

    /// <summary>Remembers where the bot died, its failed camp, for FailureMemoryMilliseconds, every one: a GameBot keeps
    /// its failed camps out of its plans that long (_recentFailedSoloCamps, AWBC:3246, :3803-3804), past the next camp
    /// it picks. Those whose time is up go.</summary>
    public static void RememberFailedCamp(List<(Vector3 At, long Until)> failed, Vector3 at, long now)
    {
        failed.RemoveAll(camp => camp.Until <= now);
        failed.Add((at, now + AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds));
    }

    /// <summary>The failed camps the bot still avoids (PlanRelocation).</summary>
    public static Vector3[] FailedCamps(IReadOnlyList<(Vector3 At, long Until)> failed, long now) =>
        failed.Where(camp => now < camp.Until).Select(camp => camp.At).ToArray();

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

    // A list caster's spells are in its spell lines; a hybrid's (Paladin, Thane...) are among its usable skills, each
    // with its line (GamePlayerUtils.cs:544-560; REVIEW-FIXES 19).
    internal static IEnumerable<(Spell Spell, SpellLine Line)> UsableSpells(GamePlayer player)
    {
        foreach ((SpellLine line, List<Skill> skills) in player.GetAllUsableListSpells())
            foreach (Spell spell in skills.OfType<Spell>())
                yield return (spell, line);
        foreach ((Skill skill, Skill parent) in player.GetAllUsableSkills())
            if (skill is Spell spell && parent is SpellLine line)
                yield return (spell, line);
    }

    // APP:667-692 without the quickbar, the Animist policy, the range filter (checked at cast time) and the mana check.
    internal static (Spell, SpellLine) FindDamageSpell(GamePlayer player)
    {
        Spell best = null;
        SpellLine bestLine = null;
        foreach ((Spell spell, SpellLine line) in UsableSpells(player))
        {
            if (!spell.IsHarmful || spell.Damage <= 0 || spell.Range <= 0 || spell.NeedInstrument || spell.Level > player.Level)
                continue;
            if (best == null || Better(spell, best)) { best = spell; bestLine = line; }
        }
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
                 $"why=\"{state.Mover.LastBlock}\" count={state.BlockedSinceLine}" +
                 (state.Mover.LastBlock == "stalled" ? $" stall: {state.Mover.LastStall}" : ""));
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
    // can walk to (a GameBot leaves an outgrown camp for one at its level, AWBC:3244-3300). A capped one moves after 30 s
    // (DEATHS.md A4): both to the nearest camp that fits the death cap, else to the safest one, as a GameBot falls back
    // after a death (PlanRelocation). A capped bot that cannot move takes the safest con in reach (FallbackInPlace).
    private static void RelocateIfOutgrown(PlayerBotState state, GamePlayer player, long now)
    {
        string reason = RelocateReason(state.RoamingSince, state.CappedSince, now);
        if (reason == null || now < state.NextRelocateTick) return;
        if (!CanRelocate(state, player) || player.CurrentZone is not Zone zone)   // the lever test keeps its bots on
        {                                                                          //   their spots
            FallBackInPlace(state, reason);
            return;
        }
        state.NextRelocateTick = now + (reason == Capped ? CappedRelocateMilliseconds : RelocateAfterMilliseconds);
        Vector3 here = new(player.X, player.Y, player.Z);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (nav.HasNavmesh(zone) && nav.GetClosestPoint(zone, here, 64, 64, 128, nav.DefaultFilters) == null)
        {
            FallBackInPlace(state, reason);   // off the mesh here, every corridor fails (PlayerTravelPath.cs:48-50): blame
            return;                           //   no monster for it
        }
        ConColor natural = MaximumTargetCon(1, 0);
        List<(GameNPC Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters = new();
        foreach (GameNPC npc in WorldMgr.GetNPCsFromRegion(player.CurrentRegionID))
        {
            if (npc.CurrentZone != zone || !IsExperienceTarget(npc) || !IsAttackable(player, npc)) continue;
            ConColor con = ConLevels.GetConColor(player.GetConLevel(npc));
            if (con > ConColor.GREY)
                monsters.Add((npc, new(npc.X, npc.Y, npc.Z), con,
                    con <= natural && AutonomousPlayerPilot.IsEligibleExperienceTarget(con, npc.EffectiveLevel, player.Level, 1),
                    IsAvoided(state.AvoidUntil, npc.Name, now)));
        }
        (GameNPC[] nearest, ConColor? fallback, bool stale) = PlanRelocation(monsters, here,
            MaximumTargetCon(1, state.DeathSafetySteps), natural, FailedCamps(state.FailedCamps, now), state.RelocateRejected,
            RelocateCandidates);
        foreach (GameNPC npc in nearest)
        {
            Vector3 spot = new(npc.X, npc.Y, npc.Z);
            if (nav.HasNavmesh(zone) && !PlayerTravelPath.TryBuildCorridor(nav, zone, here, spot, out _))
            {
                state.RelocateRejected.Add(npc);
                continue;
            }
            if (Log.IsInfoEnabled)
                Log.Info(RelocateLine(player.Name, player.Level, state.Anchor, spot, (int)Vector3.Distance(here, spot), npc.Name,
                    npc.EffectiveLevel, zone.ID, reason, fallback));
            state.Anchor = spot;
            state.RoamDestination = spot;   // walk there: a task bot levelling for its errand has no leash to pull it
            state.RelocateRejected.Clear();
            state.FallbackCap = fallback;   // that camp's con, or none: the death cap (Cap)
            state.HuntAvoided = stale;      //   and at a camp of avoided names, those too (FindWithin)
            state.CappedSince = 0;          // the next search starts the clock afresh, on the way there too
            return;
        }
        if (Log.IsInfoEnabled)
            Log.Info(RelocateNoneLine(player.Name, player.Level, nearest.Length, zone.ID, reason));
        if (nearest.Length == 0) FallBackInPlace(state, reason);   // no camp in the zone: none to try next time either
    }

    // FallbackInPlace's cap where the bot is; its next search finds the monster it passed over, and the hold ends.
    private static void FallBackInPlace(PlayerBotState state, string reason)
    {
        if (FallbackInPlace(reason, state.SafestPassedOver) is not ConColor cap) return;
        state.FallbackCap = cap;
        state.CappedSince = 0;
    }

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
            if (state.Engaged && state.Target != null)
                Remember(state.AvoidUntil, state.Target.Name, now);                    // DEATHS.md A3: died fighting it
            state.DeathRecorded = true;
            state.Deaths++;
            state.DeathSafetySteps = NextDeathSteps(state.DeathSafetySteps);
            state.FallbackCap = null;                                                   // a GameBot's fallback is its camp's
            state.HuntAvoided = false;                                                  //   only, and it fails here (AWBC:3801,
            RememberFailedCamp(state.FailedCamps, new(player.X, player.Y, player.Z), now);   //   :3803-3804)
            state.CappedSince = 0;
            state.Target = null;
            state.Engaged = false;
            state.RoamDestination = null;
            state.RoamingSince = 0;                                                     // back at the bind point: a fresh start
            state.RelocateRejected.Clear();
            state.Mover.Reset();
            PlayerBotDriverReport.Death();
        }
        if (!state.KillerAvoided && state.Killer is GameNPC killer)                    // and its killer, once the reaper
        {                                                                               //   names it (AwaitsDying)
            Remember(state.AvoidUntil, killer.Name, now);
            state.KillerAvoided = true;
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
            inFight ? state.Swings : -1, inFight ? state.NotVisible : -1, null, 0, false, fromBind,   // the killer: DeathLine
            AvoidedCount(state.AvoidUntil, now));                                          // this death's come after
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
            state.Swings, state.NotVisible, state.FightCasts, state.LifeKills, state.LifeRests, state.EngagePet,
            state.FightHeals, state.FightQuickcast)));
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

    // True while the bot's own heal or summon is being cast: the think waits it out (Engage would stop it to walk, Rest
    // to sit).
    private static bool WaitsForSelfCast(PlayerBotState state, GamePlayer player) =>
        state.SelfCast = state.SelfCast && player.IsCasting;

    // Quickcast as a player uses it: attack mode off, which the ability needs (QuickCastAbilityHandler.cs:65-69), then
    // the use-skill packet's request (UseSkillHandler.cs:83-84), handled in the Casting phase later in this tick
    // (CastingComponent.cs:173-186, :485-496). A refused use is not asked for again until QuickcastRetryMilliseconds.
    private static bool UseQuickcast(PlayerBotState state, GamePlayer player, long now, bool quickcastOn, bool interruptible)
    {
        Ability quickcast = player.GetAbility(Abilities.Quickcast);
        if (!ShouldQuickcast(quickcast != null, quickcastOn, quickcast == null ? 0 : player.GetSkillDisabledDuration(quickcast),
                state.NextQuickcastTick, player.IsCasting, state.Spell != null, player.ManaPercent, now,
                state.SpellBlockedUntil, player.IsBeingInterruptedByOther, interruptible))
            return false;
        state.NextQuickcastTick = now + QuickcastRetryMilliseconds;
        player.attackComponent.StopAttack();
        player.castingComponent.RequestUseAbility(quickcast);
        state.Activity = PlayerBotActivity.Casting;
        return true;
    }

    // A healer's heal on itself (DEATHS.md A6), before a rest as a GameBot's (AWBC:2725); out of a fight (MayRest's
    // view) from 85% health, then it sits for power (#167 F2). The gate runs first at no cost, then with the heal's.
    private static bool SelfHeal(PlayerBotState state, GamePlayer player, long now)
    {
        bool inFight = !MayRest(Attacker(state, player, now, anyHitter: true) != null, state.Engaged && state.Target?.IsAlive == true);
        byte mana = PowerPercent(player.MaxMana, player.ManaPercent);
        long nextTry = state.NextSelfHealTick;
        if (player.Level == state.NoSelfHealLevel ||
            !WantsSelfHeal(player.HealthPercent, player.IsCasting, now, nextTry, inFight, mana, 0))
            return false;
        state.NextSelfHealTick = now + SelfHealRetryMilliseconds;
        IEnumerable<(Spell, SpellLine)> spells = UsableSpells(player);
        (Spell heal, SpellLine line) = FindSelfHeal(spells, player.Level, player.MaxHealth, player.IsBeingInterruptedByOther,
            spell => player.GetSkillDisabledDuration(spell) <= 0 && HasPowerFor(player.MaxMana, player.Mana, spell.Power));
        if (heal == null && FindSelfHeal(spells, player.Level, player.MaxHealth, false, _ => true).Item1 == null)
            state.NoSelfHealLevel = player.Level;                                      // no heal at this level: none looked
        if (heal == null ||                                                             //   for until the next
            !WantsSelfHeal(player.HealthPercent, player.IsCasting, now, nextTry, inFight, mana, CostPercent(player.MaxMana, heal.Power)) ||
            !CastOnSelf(state, player, now, heal, line))
            return false;
        if (state.Engaged) state.FightHeals++;
        return true;
    }

    // A pet class's main pet (DEATHS.md A5), which a GameBot always keeps (BotBrain.cs:1450-1458,
    // AutonomousPetSupport.Maintain), summoned when it has none and so again once it dies. An Animist's is a turret on
    // its ground target: set at the bot's feet, as /groundset does (PlayerGroundTargetHandler.cs:26-27;
    // SummonAnimistPet.cs:20-41).
    private static bool SummonPet(PlayerBotState state, GamePlayer player, long now)
    {
        if (player.Level == state.NoPetSpellLevel ||
            !WantsPet(player.ControlledBrain != null, Attacker(state, player, now, anyHitter: true) != null, player.InCombat,
                player.ManaPercent, player.IsCasting, now, state.NextSummonTick))
            return false;
        state.NextSummonTick = now + SummonRetryMilliseconds;
        (Spell summon, SpellLine line) = FindPetSpell(UsableSpells(player), player.Level);
        if (summon == null) { state.NoPetSpellLevel = player.Level; return false; }
        if (player.GetSkillDisabledDuration(summon) > 0 || !HasPowerFor(player.MaxMana, player.Mana, summon.Power)) return false;
        if (summon.SpellType is eSpellType.SummonAnimistPet)
        {
            player.GroundTargetInView = true;
            player.SetGroundTarget(player.X, player.Y, player.Z);
        }
        return CastOnSelf(state, player, now, summon, line);
    }

    // #167 F1: at each cast choice a Theurgist casts a pet at its target before its damage spell, whenever it can, as a
    // GameBot does (AutonomousPetSupport.cs:636-650); after a pet, its damage spell again. The server's own cap applies
    // (SummonTheurgistPet.cs:67-71; 0, none, in 1.65).
    private static void ChooseCombatSpell(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        (Spell pet, SpellLine petLine) = CombatPet(state, player, target, now);
        if (pet != null) (state.Spell, state.SpellLine) = (pet, petLine);
        else if (state.Spell != null && !CountsInCastWatch(state.Spell)) (state.Spell, state.SpellLine) = FindDamageSpell(player);
    }

    private static (Spell, SpellLine) CombatPet(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        if (player.Level == state.NoCombatPetLevel || now < state.NextCombatPetTick) return default;
        bool underCap = DOL.GS.Spells.SummonTheurgistPet.PetLimitAllowsAnother(player.PetCount,
            DOL.GS.ServerProperties.Properties.THEURGIST_PET_CAP);                      // the server's own test (:67)
        (Spell pet, SpellLine line) = FindCombatPetSpell(UsableSpells(player), player.Level, spell => underCap &&
            player.GetSkillDisabledDuration(spell) <= 0 && HasPowerFor(player.MaxMana, player.Mana, spell.Power));
        if (pet == null && FindCombatPetSpell(UsableSpells(player), player.Level, _ => true).Item1 == null)
            state.NoCombatPetLevel = player.Level;                                     // none at this level: not looked for
        bool inRange = pet != null && player.TargetInView &&                            //   again until the next
            player.IsWithinRadius(target, player.castingComponent.CalculateSpellRange(pet));
        return WantsCombatPet(pet != null, inRange, player.IsCasting, now, state.NextCombatPetTick) ? (pet, line) : default;
    }

    // A spell on the bot itself, stopped and standing (no cast sitting, SpellHandler.cs:556-562). Targeted as a player
    // does it, F1 then the spell, unless it is a Self spell, which ignores the target (SpellHandler.cs:411-416): a
    // self-target is in view (GamePlayer.cs:127-139), and a Group heal takes the caster with no group
    // (SpellHandler.cs:1807-1811). The request is read in the Casting phase later in this tick (CastingComponent.cs:116-144,
    // SpellHandler.cs:403-406), so the target stays until the next think's Engage.
    private static bool CastOnSelf(PlayerBotState state, GamePlayer player, long now, Spell spell, SpellLine line)
    {
        state.Mover.Stop(now);
        if (player.IsSitting) player.Sit(false);
        if (spell.Target is not eSpellTarget.SELF) player.TargetObject = player;
        if (!player.CastSpell(spell, line)) return false;
        state.SelfCast = true;
        state.Activity = PlayerBotActivity.Casting;
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
