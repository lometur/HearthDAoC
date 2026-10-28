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
using DOL.GS.RealmAbilities;
using DOL.GS.Styles;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

public enum PlayerBotActivity { Waiting, Dead, Resting, Hunting, Fighting, Casting, Roaming, Task }

/// <summary>A class's role, as tools/lmroles.py gives it (CLASS_ROLES; levelmatch REVIEW-FIXES 11).</summary>
public enum PlayerBotRole { Melee, Caster, Healer, Pet, Archer }

/// <summary>The recovery's step after a release (PlayerBotDriver.AfterReleaseStep, #167 F4).</summary>
public enum PlayerBotRecovery { Fight, StepOut, Replan, Wait, Done }

/// <summary>A break-off's step (PlayerBotDriver.BreakOffStep, #167 C2).</summary>
public enum PlayerBotBreakOff { Run, NextPoint, Done, Fight }

/// <summary>A caster's step against its target (PlayerBotDriver.CasterAction, #167 F11); Nuke hands the think to
/// CombatAction.</summary>
public enum PlayerBotCasterAction { Nuke, Control, StepBack, QcControl }

/// <summary>The bot's step against an add (PlayerBotDriver.AddAction, #167 C4); Pets, a Theurgist's pets at an add hitting
/// it (AddPlan).</summary>
public enum PlayerBotAddAction { None, Control, QcControl, Pets }

/// <summary>Engage's step once in reach (PlayerBotDriver.EngageStepOrder, #167 C4): Wait out a cast, the add's step, the
/// pet leads, or CasterStep and CombatAction.</summary>
public enum PlayerBotEngageStep { Wait, Add, PetLeads, Caster }

/// <summary>What the bot does in reach of its target this think (PlayerBotDriver.CombatAction, #167 F6).</summary>
public enum PlayerBotCombatAction { Cast, WaitToCast, Quickcast, Swing }

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
    public readonly List<(ushort Region, Vector3 At, long Until)> FailedCamps = new();   // camps, 15 minutes (PlanRelocation)
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
    public bool RecoverBeforePull;                               // a kill since the last rest: rest first (Recovered)
    public bool SelfCast;                                        // a heal or summon on itself is being cast (DEATHS.md
    public long NextSelfHealTick, NextSummonTick;                //   A6, A5), the next tries, and the level at which it
    public byte NoPetSpellLevel;                                 //   found no main-pet summon (not looked for again then)
    public byte NoSelfHealLevel;                                 //   or no heal (#167 F2: looked for from 85% too)
    public long NextCombatPetTick;                               // #167 F1: a Theurgist's next pet at its target
    public long NextSpellChoiceTick;                             //   and the next spell choice while no cast follows
    public long NextSelfBuffTick;                                // #167 F8: the next self-buff scan
    public List<(Spell Spell, SpellLine Line)> Nukes;            // #167 F9, F1: its damage spells (DamageSpells) and the
    public List<(Spell Spell, SpellLine Line)> CombatPets;       //   pets a Theurgist casts at its target, at NukesLevel:
    public int NukesLevel = -1;                                  //   built once per level and after a training (KnowSpells)
    public List<(Spell Spell, SpellLine Line)> Controls;         // #167 F11: a caster's roots, mezzes and stuns, then;
    public long NextControlTick;                                 //   its next control cast, the tries on this target, and
    public int ControlTries;                                     //   the point it steps back to (KiteStep)
    public Vector3? KiteGoal;
    public long NextKiteTick;
    public long NextAnimistStandTick;                            // #167 F10b: after a blocked walk to its stand
    public int FightControls, FightKites;                        // #167 FIGHT ctl= and kites=
    public int FightCtlBroken;                                   // #167 C3: FIGHT ctlBroken=, from the target's root or mez
    public bool TargetHeldBreaks;                                //   at the last think (BrokeHold)
    public GameLiving AddCastTarget;                             // #167 C4: the add a cast under way is at (FacesWhom),
    public readonly List<GameNPC> AddScan = new();               //   the monsters coming at the bot (ScanComing), its next
    public long NextAddScanTick, NextAddTick;                    //   scan, the add step's next try after no sight, the add
    public GameNPC ControlledAdd;                                //   it controls and the casts at it that held nothing;
    public int AddControlTries, FightAddControls;                //   FIGHT addCtl=
    public byte EngageTargetHealth;                              // #167 C11: the target's health at the first think (NukesNeeded)
    public bool EngageIll;                                       // #167 DEATH ill=: the illness at the fight's start
    public long PetOrderTick;                                    // #167 F10c: when its pet was sent at the target
    public long NextQuickcastTick;                               // Quickcast's next try (UseQuickcast)
    public bool EngagePet;                                       // PLAYER_BOT_FIGHT pet=, heals= and qc=: a living pet at
    public int FightHeals;                                       //   the fight's first think, its heals on itself since,
    public bool FightQuickcast;                                  //   and a cast with Quickcast on
    public int EngageBuffs, FightPetCasts, FightWaits;           // #167 FIGHT buffs=, petCasts= and waits=
    public long SitSince;                                        // #167 PLAYER_BOT_REST: when the bot sat down, 0 standing
    public (string Head, PlayerBotDeathDetail Detail, GameObject Target)? PendingDeath;   // a DEATH line awaiting Dying (AwaitsDying)
    public Vector3 DeathSpot;                                    // #167 F4: where it died last; after any death (C1b)
    public string ReplanOwed;                                    //   it owes a relocation (Died, planned from there; C2's
    public long ReplanSince;                                     //   BrokeOff), held MaxHoldMilliseconds at most; its
    public int NoXpDeaths;                                       //   deaths with no XP between them (the GameBot's streak)
    public long LastDeathTick, GainedAtDeath, RecoverUntil;      //   and the back-off's end; the last release, and the
    public long ReleasedAt;                                      //   recovery after it (AfterReleaseStep): the spot out of
    public bool AfterRelease;                                    //   every monster's reach it steps to, the ring test's
    public Vector3? ClearSpot;                                   //   next scan and a blocked step's pause
    public long NextReachTick, StepOutBlockedUntil;
    public GameNPC Incoming;                                     // #167 F7: the nearest monster coming at it (Incoming),
    public long NextIncomingTick;                                //   scanned every 2 s; the last search's potential adds
    public readonly List<GameNPC> SearchAdds = new();            //   (FindWithin), and the rest spot read from them once
    public bool RestSpotChecked;                                 //   per rest (RestSpot), walked to before the bot sits
    public Vector3? RestGoal;
    public bool KilledWithAdds;                                  // #167 C2: a kill with 2 or more on the bot (read for
    public long BreakOffSince, NextBreakOffTick;                 //   3 s, SecondMonsterCase); the break-off under way (0:
    public Vector3? BreakOffGoal;                                //   none) and the next one's earliest, its point, a step
    public bool BreakOffArrived;                                 //   there arrived, the monsters still after it, the
    public readonly List<GameNPC> BreakOffChasers = new();       //   points it was blocked on or found no walk to
    public readonly List<Vector3> BreakOffBlocked = new();       //   (NextBreakOffPoint), and its start's health,
    public byte BreakOffHealth, BreakOffEndurance;               //   endurance and attackers (BREAKOFF)
    public int BreakOffAttackers;
    public bool BackPending, BackOwed, BackMoved;                // #167 C1: PLAYER_BOT_BACK, from a release to its first
    public long BackStepOutMs, BackHoldMs, BackWalkMs;           //   fight: the line due, the replan owed and a camp planned
                                                                 //   since, and the time stepping out, held and walking
    public Vector3 TargetSpawn;                                  // #167 C7: the target's spawn spot and the reach it aggroes
    public int TargetRespawnReach;                               //   from (RespawnOf, at the pick), and the last kills'
    public readonly List<(Vector3 At, int Reach, long KilledAt)> RecentKills = new();   //   (RememberKill): rested out of
    public (int At, int Hp) FightAdd = (-1, -1);                 // #167 FIGHT and DEATH addAt= and addHp= (FirstAdd), the
    public int EngageDistance = -1, PickCrowd = -1;              //   distance at the first think in reach, the pick's
                                                                 //   crowding (crowd=, FindWithin's; -1 for another pick);
    public byte SitHealth, SitMana;                              //   REST hp= and mp= when it sat, and respawn=: the spawn
    public int RestRespawns;                                     //   spots the rest spot was read against (WalksToRestSpot)
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
    public const string Outgrown = "outgrown", Capped = "capped", Died = "died", BrokeOff = "brokeoff";   // RelocateReason
    public const int MaxHoldMilliseconds = 60_000;           // #167 F4: a hold while no camp is planned ends by then
    public static readonly int[] StepOutRings = [500, 900, 1_300, 1_700, 2_100, 2_500];   // #167 F4: a released bot's
    public const int SpotDirections = 12;                    //   candidate spots, every 30 degrees on each ring
    public const int StepOutPauseMilliseconds = 10_000;      // (inf.) after a blocked step out, it stands this long
    public const int ContactDistance = 250;                   // #167 F11: a monster this near is in melee (inf.)
    public const int ControlMarginMilliseconds = 500;         //   a cast's slack before contact (inf.)
    public const int MaxControlTries = 3;                     //   control casts per target that held nothing (inf.)
    public const int KitePauseMilliseconds = 5_000;           //   after a blocked step back (inf.), or none found (C3)
    public const int KiteFallbackDistance = 400;              // #167 C3: in melee with no clear point, this far (inf.)
    public const int KiteArriveDistance = 20;                 //   the step back ends this near its point, this short of the
    public const int KiteStopMargin = 50;                     //   stop (KiteRange)
    public static readonly int[] KiteTurns = [0, 30, -30, 60, -60, 90, -90];   // degrees off straight away from the target
    public const byte KillFirstHealthPercent = 60;            // #167 C11: the nukes before the control from this health
    public const int AnimistStandMargin = 150;                // #167 F10b: by its turret; the turret's reach less this
    public const int IncomingRadius = 2_000;                  // #167 F7: a monster this near with the bot as its target
    public static readonly int[] RestSpotRings = [300, 600, 900, 1_200];   // #167 F7: the rest spot's candidates (inf.)
    public const int RespawnWatchMilliseconds = 150_000;      // #167 C7: a kill's spawn spot counts this long (inf.: the
    public const int RecentKillCount = 3;                     //   usual respawn is 80 s), the last 3 kills'
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
    public const byte CasterPowerStop = 90;                  // #167 F3, C10: a caster stops resting power here (a list
    public const byte RestEnduranceStop = 60;                //   caster regenerates at half rate under 50%,
                                                             //   PowerRegenerationAmountCalculator.cs:38-50: batch A's 70
                                                             //   left 46.6% of its pulls ending under 50%); all, endurance
    public const int SelfBuffScanMilliseconds = 5_000;       // #167 F8: the self-buffs' scan, out of a fight
    public const int ShortBuffMilliseconds = 120_000;        // #167 C9: a shorter buff is kept up in fights only, but a
                                                             //   regeneration one while its resource is low
    public const int PetLeadMilliseconds = 4_000;            // #167 F10c: the owner's longest wait for its pet to lead
    public const int SpellChoiceMilliseconds = 1_000;        // (inf.) as the sight check's: the spell choice while no cast
    public const byte BreakOffHealthPercent = 40;            // #167 C2: below this, losing to two, it breaks off (k12),
    public const byte BreakOffTargetPercent = 10;            //   unless its target is this low or lower
    public const int RunSpeed = GamePlayer.PLAYER_BASE_SPEED;   //   191 (GamePlayer.cs:8250): enable_pve_speed is off in
    public const int SprintSpeed = 248;                      //   the world DB; 1.3 x 191 sprinting (MaxSpeedCalculator.cs:25,
                                                             //   :101, :148)
    public const int ChaseSeconds = 25;                      //   a monster gives up 25 s after its last swing
                                                             //   (StandardMobState.cs:89, :121)
    public const int SprintStopEndurance = 5, SprintStartEndurance = 10;   // GamePlayer.cs:2420-2423, :8633
    public const int LongWind = 5;                           //   a sprint's endurance a second (GamePlayer.cs:2396)
    public static readonly int[] BreakOffRings = [1_500, 2_000, 2_500];   // its points (inf.)
    public const int BreakOffMilliseconds = 30_000;          //   still chased then, it fights (inf.); and after that, no
                                                             //   break-off for as long
    public const int ChaseRadius = 6_000;                    //   past this a monster drops it (StandardMobBrain.cs:23, :589)
    public const int ChaserReach = 200;                      //   a monster's melee reach (MeleeAttackRange, GameLiving.cs:303),
                                                             //   which the sprint's lead clears (Outruns, the review of 9aacd69)
    public const int SecondMonsterMilliseconds = 3_000;      //   the second-monster case this long from the kill (inf.: the
                                                             //   analysts' add fights leave out those begun within 3 s of one)
    public const int BreakOffBlockedRadius = 100;            //   a point this near one blocked is left out (inf.), and the
    public const int BreakOffCorridorTries = 3;              //   corridor tests per point chosen (inf.)
    public const string Shook = "shook", Fought = "fought";  //   PLAYER_BOT_BREAKOFF outcome= (and Died)

    private static readonly ConcurrentDictionary<GamePlayer, PlayerBotState> _states = new();
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int Score)> _candidates;      // FindWithin's, per timer
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int AggroRange)> _adds;       //   thread (TimerService)
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int AggroRange)> _reach;      // ClearSpotNear's
    [ThreadStatic] private static List<(GameNPC Npc, int Distance, bool TargetsBot, bool Held)> _addCandidates;   // Add's
    [ThreadStatic] private static List<(byte Health, int Distance)> _hitters;                  // StartsBreakOff's

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
            ResetOnRegionChange(state, player.CurrentRegionID);
        ObserveXp(state, player);                            // 5
        if (state.PendingDeath is { } held)                                              //    last think's DEATH line, its
        {                                                                                //    killer named since (AwaitsDying)
            state.PendingDeath = null;
            if (Log.IsInfoEnabled) Log.Info(DeathLine(held.Head, held.Detail, state.Killer, held.Target));
        }
        if (!player.IsAlive) { HandleDeath(state, player, now); return; }               // 6
        StartLife(state, now);                                                           //    a new life
        if (state.SitSince != 0 && !player.IsSitting)                                    //    stood by the server (a hit)
            EndRest(state, player, now, Attacker(state, player, now, anyHitter: true) != null ? "hit" : "other");
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
        if (WaitsForSelfCast(state, player) || StepsOutOfReach(state, player, now))      // 11a. #167 F4, C6: a release, a login
            return;
        if (BreaksOffNow(state, player, now)) return;                                    //     #167 C2: losing to two
        if (SelfHeal(state, player, now) || SummonPet(state, player, now) || SelfBuff(state, player, now))
            return;                                                                      // 11b. DEATHS.md A6, A5, #167 F8: before a rest
        if (state.RecoverBeforePull && Recovered(player)) state.RecoverBeforePull = false;
        bool hit = Attacker(state, player, now, anyHitter: true) != null, liveFight = state.Engaged && state.Target?.IsAlive == true;
        bool incoming = !hit && !liveFight && Incoming(state, player, now) != null;      //    #167 F7: one coming at it too
        bool mayRest = MayRest(hit || incoming, liveFight);
        if (mayRest && WantsRest(player, state.RecoverBeforePull))                       // 12. APP:290-296, DEATHS.md A1
        {
            if (state.Target != null) Disengage(state, player);                         //    a pull not begun: re-picked after
            if (!player.IsSitting && WalksToRestSpot(state, player, now)) return;       //    #167 F7: out of the adds' reach
            Rest(state, player, now);
            return;
        }
        state.RestSpotChecked = false;                                                   //    the next rest reads its spot
        state.RestGoal = null;
        if (player.IsSitting)                                                            // 13. rested, or hit: stand
        {
            EndRest(state, player, now, RestStop(mayRest, incoming));
            player.Sit(false);
        }
        RefreshTarget(state, player, now);                                               // 14
        if (state.Target == null)                                                        // 15
        {
            RelocateIfOutgrown(state, player, now);
            if (state.ReplanOwed != null && !Holding(state.ReplanSince, now)) EndReplan(state, player);   // #167 F4, C2
            if (Recovering(state, player, now) ||                                        //    the back-off, the illness
                HoldsForRelocation(CanRelocate(state, player), state.CappedSince, state.ReplanSince,
                    state.RoamDestination == state.Anchor, now))
            {
                state.Mover.Stop(now);                                                   //    capped, or owed a relocation:
                state.Activity = PlayerBotActivity.Roaming;                              //    no walk into the camp; it
                state.BackHoldMs += TickMilliseconds;                                    //    cannot hunt (no target);
            }                                                                            //    #167 C1: BACK hold=
            else
            {
                Roam(state, player, now);
                state.BackWalkMs += TickMilliseconds;                                    //    #167 C1: BACK walk=
            }
            ReportIdle(state, player, now);
            return;
        }
        Engage(state, player, now);                                                      // 16
        if (state.Engaged) state.RoamingSince = 0;   // reached: a target blocked on the way leaves the idle clock running
    }

    /// <summary>Step 6, a new life (after a release, or the login's first): the recovery LifeStart gives (StartRecovery),
    /// this life's counts afresh. The release restores it all and ends its buffs: the self-buffs scan at once.</summary>
    public static void StartLife(PlayerBotState state, long now)
    {
        if (!state.DeathRecorded && state.AliveSince != 0) return;
        (bool stepOut, bool backOff) = LifeStart(state.DeathRecorded, state.AliveSince == 0);
        if (stepOut) StartRecovery(state, now, backOff);                                // #167 F4, C6
        state.AliveSince = now;
        state.LifeKills = state.LifeRests = 0;
        state.Killer = null;
        state.KillerAvoided = false;
        state.RecoverBeforePull = false;
        state.NextSelfBuffTick = 0;
    }

    /// <summary>The recovery a new life starts (#167 F4, C6): (stepOut, backOff). After a release (afterDeath), all of it:
    /// the step out of reach, the back-off and the replan owed (AfterReleaseStep). The login's first life (firstLife) only
    /// the step out: the test spots are camp centres (5 or more monsters within 1,400), and 73 of st-deathsB's 370 logins
    /// died in their first fight, 37 of the 42 login-life deaths with adds within 60 s. No back-off (no death) and no
    /// replan owed. Live, it matters only for a logout inside a camp.</summary>
    public static (bool StepOut, bool BackOff) LifeStart(bool afterDeath, bool firstLife) =>
        (afterDeath || firstLife, afterDeath);

    /// <summary>Step 4: a bot in another region (a release to a bind there, a re-entry) starts its hunt afresh: no walk,
    /// target, idle or capped clock, fallback cap or stale camp's names. Its failed camps stay (#167 F4): each has its
    /// region (FailedCamps); cleared, a bot that came back planned into the camp it died at.</summary>
    public static void ResetOnRegionChange(PlayerBotState state, ushort region)
    {
        state.LastRegion = region;
        state.Mover.Reset();
        state.Target = null;
        state.RoamDestination = null;
        state.RoamingSince = 0;
        state.CappedSince = 0;
        state.FallbackCap = null;
        state.HuntAvoided = false;
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
        bool due = now >= state.NextCombatChoiceTick;
        if (ChoosesSpell(due, player.IsCasting, now, state.NextSpellChoiceTick))       // a cast choice: the pet or the
        {                                                                               //   damage spell (#167 F1)
            state.NextSpellChoiceTick = now + SpellChoiceMilliseconds;
            ChooseCombatSpell(state, player, target, now);
        }
        bool interruptible = state.Spell != null && Interruptible(state.Spell);
        Ability quickcast = player.GetAbility(Abilities.Quickcast);
        bool canCast = ShouldCast(state.Spell != null, player.ManaPercent, now, state.SpellBlockedUntil);
        bool interrupted = player.IsBeingInterruptedByOther && !quickcasting;           // a quickcast one goes (SpellHandler.cs:581)
        bool quickcastReady = QuickcastReady(quickcast != null, quickcasting,
            quickcast == null ? 0 : player.GetSkillDisabledDuration(quickcast), state.NextQuickcastTick, player.IsCasting, now);
        PlayerBotRole role = RoleOf(player.CharacterClass.ID);
        (bool held, bool breaksOnHit) = role == PlayerBotRole.Caster ? Hold(target) : default;   // #167 C3: a caster's hold
        bool otherHitting = held && OtherHitting(player, target);                                //   on its target, alone
        long selfInterrupt = quickcasting ? 0 : Math.Max(0, player.SelfInterruptTime - now);
        int spellRange = StopSpellRange(state.Spell, state.Nukes, player.castingComponent.CalculateSpellRange);
        (PlayerBotCombatAction Action, int Stop, bool Holds) Plan() => CasterHoldPlan(role, held, breaksOnHit, otherHitting,
            now < state.NextKiteTick, canCast, interrupted, selfInterrupt, interruptible, quickcastReady, due,   // #167 F6, C3
            player.attackComponent.AttackRange, spellRange);
        (PlayerBotCombatAction action, int stop, bool casterHold) = Plan();
        GameLiving addCast = AddCastUnderWay(state, player);   // #167 C4: a cast at an add under way: no walk (a move ends
        if (addCast == null &&                                 //   it, and the walk in stopped it), facing the add
            stop > MeleeStopDistance(player.attackComponent.AttackRange) && AnimistApproach(state, player, target, stop, now))
            return;                                                                     // #167 F10b: by its turret
        if (addCast == null && Kites(state, player, target, casterHold, now))  // #167 F11: a step back under way, before
            return;                                                            //   the walk in (after it, the walk in undid
        if (addCast == null && distance > stop)                                //   each step: the review of 5feb32b)
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
        Face(player, EngageFacing(target, addCast, player.IsCasting, addCast?.IsAlive == true));   // in front:
                                                                        //   AttackComponent.cs:1065; the add (C4)
        if (now >= state.NextLosTick)                         // TargetInView: AttackComponent.cs:1065, SpellHandler.cs:450
        {
            state.NextLosTick = now + LosRefreshMilliseconds;
            player.TargetInView = HeadlessLos.Answer(PathfindingProvider.Instance, player.CurrentZone,
                new Vector3(player.X, player.Y, player.Z), new Vector3(target.X, target.Y, target.Z));
        }
        bool opening = !state.Engaged;
        if (opening)                                          // the fight's first think in reach: PLAYER_BOT_FIGHT's start
        {
            state.EngagedSince = now;
            state.EngageHealth = player.HealthPercent;
            state.EngageMana = PowerPercent(player.MaxMana, player.ManaPercent);
            state.FightAdds = state.Swings = state.NotVisible = state.FightCasts = state.FightHeals = 0;
            state.FightPetCasts = state.FightWaits = state.FightControls = state.FightKites = state.FightCtlBroken = 0;
            state.TargetHeldBreaks = false;                                             // #167 C3
            state.FightAddControls = 0;                                                 // #167 C4
            (state.FightAdd, state.EngageDistance) = ((-1, -1), distance);              // #167 FIGHT addAt=, engageDist=
            state.NextAddScanTick = 0;                                                  //   and a fresh add scan (ScanComing)
            state.EngageTargetHealth = target.HealthPercent;                            // #167 C11
            state.ControlTries = 0;                                                     // #167 F11
            state.NextKiteTick = 0;
            state.KilledWithAdds = false;                                               // #167 C2: the kill's case over
            if (state.BackPending) ReportBack(state, player, now);                      // #167 C1: the first since a release
            state.EngageBuffs = SelfBuffsUp(player.effectListComponent.GetSpellEffects()     // FIGHT buffs= (#167 F8)
                .Where(effect => effect.SpellHandler?.Spell != null).Select(effect => effect.SpellHandler.Spell.SpellType));
            state.EngagePet = player.ControlledBrain?.Body?.IsAlive == true;
            state.EngageIll = Ill(player);                                              // DEATH ill= (#167 F4)
            state.FightQuickcast = false;
            if (player.ControlledBrain != null)                                         // its pet too: the pet window's
                player.CommandNpcAttack();                                              //   attack (PetWindowHandler.cs:79-82)
            state.PetOrderTick = now;
        }
        state.Engaged = true;
        state.FightAdds = Math.Max(state.FightAdds, state.Attackers);
        if (state.FightAdd.At < 0)                                                      // #167 FIGHT addAt= and addHp=: the
        {                                                                               //   first second monster on the bot
            KnowSpells(state, player);
            bool scans = ScansComing(state.Controls.Count, state.CombatPets.Count);    // the review of 9aacd69: no scan of
            if (scans) ScanComing(state, player, target, now);                         //   its own for a log field
            state.FightAdd = FirstAdd<GameLiving>(state.FightAdd, target, player.attackComponent.AttackerTracker.Attackers,
                scans ? state.AddScan : Enumerable.Empty<GameLiving>(), living => IsAttacking(player, living),
                now - state.EngagedSince, player.HealthPercent);
        }
        int swings = state.Swings;
        CountSwing(state, player.attackComponent.attackAction.LastAttackData);
        if (BrokeHold(state.TargetHeldBreaks, held && breaksOnHit, state.Swings > swings)) state.FightCtlBroken++;   // #167 C3
        state.TargetHeldBreaks = held && breaksOnHit;
        if (opening && !player.IsCasting && FightBuff(state, player, now)) return;     // #167 C9: a Surge first
        GameNPC pet = player.ControlledBrain?.Body;
        bool petLeads = !player.IsCasting && action is not PlayerBotCombatAction.Swing && PetLeads(pet?.IsAlive == true,
            pet != null && target.TargetObject == pet, target.TargetObject == player,
            Attacker(state, player, now, anyHitter: true) != null, now - state.PetOrderTick, PetLeadMilliseconds);
        (PlayerBotAddAction addStep, GameNPC add, Spell addSpell, SpellLine addLine) = due && !player.IsCasting
            ? AddPlan(state, player, target, held, distance, now, interrupted, quickcastReady, quickcasting) : default;
        PlayerBotEngageStep engageStep = EngageStepOrder(due, player.IsCasting, addStep, petLeads);   // #167 C4: the add's
        if (engageStep is PlayerBotEngageStep.Add &&                                                  //   control first
            !AddAct(state, player, target, add, addStep, addSpell, addLine, now, quickcasting, quickcast))
            engageStep = EngageStepOrder(due, false, PlayerBotAddAction.None, petLeads);  // refused (no sight): on as before
        if (engageStep is PlayerBotEngageStep.Wait or PlayerBotEngageStep.Add) return;
        if (engageStep is PlayerBotEngageStep.PetLeads)
        {
            player.attackComponent.StopAttack();                                        // the pet leads (#167 F10c)
            state.Activity = PlayerBotActivity.Fighting;
            return;
        }
        if (CasterStep(state, player, target, distance, stop, held, breaksOnHit, casterHold, now, due, interrupted,
                quickcastReady, quickcasting, quickcast))
            return;                                                                     // #167 F11
        action = Plan().Action;                                                         // #167 C3: its step back blocked or
                                                                                        //   none just now: the old rule
        if (action is PlayerBotCombatAction.Cast &&
            player.IsWithinRadius(target, player.castingComponent.CalculateSpellRange(state.Spell)))
        {
            player.attackComponent.StopAttack();
            bool petCast = !CountsInCastWatch(state.Spell);                             // a pet at the target (#167 F1)
            (state.CastTarget, state.CastAttempts, state.CastHealthMark, bool stalled) =
                WatchStep(state.Spell, state.CastTarget, target, state.CastAttempts, state.CastHealthMark, target.HealthPercent);
            if (stalled || !player.CastSpell(state.Spell, state.SpellLine))             // APP:345-353 without the Animist policy
            {
                state.Activity = PlayerBotActivity.Fighting;
                if (petCast)
                {
                    state.NextCombatPetTick = now + SummonRetryMilliseconds;           // the damage spell at the next think
                    state.NextSpellChoiceTick = 0;
                    return;
                }
                // CastSpell only queues the request (CastingComponent.cs:116-144); range and sight fail later
                // (SpellHandler.cs:681, :694, :731). A run of requests that hurt nothing: swing instead for a while.
                state.SpellBlockedUntil = now + SpellBlockMilliseconds;
                state.CastAttempts = 0;
                state.CastFailures++;
                return;                                                                 // the next think walks into melee
            }
            if (petCast)
            {
                state.NextCombatPetTick = now + Math.Max(250, state.Spell.CastTime + 100);   // AutonomousPetSupport.cs:646
                state.FightPetCasts++;
            }
            state.NextSpellChoiceTick = 0;                                              // the next due think chooses
            state.NextCombatChoiceTick = now + Math.Max(900, state.Spell.CastTime + 250);
            state.FightCasts++;
            state.FightQuickcast |= quickcasting;
            state.Activity = PlayerBotActivity.Casting;
        }
        else if (action is PlayerBotCombatAction.Quickcast)
            UseQuickcast(state, player, now, quickcast);                                // then the next think casts
        else if (action is PlayerBotCombatAction.WaitToCast)
        {
            player.attackComponent.StopAttack();                                        // no swing to block the cast
            state.Activity = PlayerBotActivity.Fighting;
            if (due) state.FightWaits++;                                                // its own swing's interrupt: waits=
        }
        else                                                                            // Swing, or a cast out of its range
        {                                                                               //   (one under SpellStopDistance's
            if (now >= state.NextStyleTick)                                             //   145), as before
            {
                state.NextStyleTick = now + StyleMilliseconds;
                QueueUsableStyle(player);                                               // APP:694-710 without the quickbar filter
            }
            if (!player.attackComponent.AttackState && CanSwing(player.ControlledBrain?.Body))
                player.attackComponent.RequestStartAttack(target);                      // AttackComponent.cs:491-496
            state.Activity = PlayerBotActivity.Fighting;
        }
    }

    /// <summary>Whether the bot has a spell it may cast (APP:670, the cast watch's block); the interrupts are
    /// CombatAction's.</summary>
    public static bool ShouldCast(bool hasSpell, byte manaPercent, long now, long blockedUntil) =>
        hasSpell && manaPercent >= 18 && now >= blockedUntil;

    /// <summary>What the bot does in reach of its target (#167 F6). With no spell it may cast (ShouldCast), it swings. An
    /// instant or uninterruptible spell goes whatever the interrupts, when the cast choice is due (between casts the bot
    /// swings). An interruptible one: while a monster's hits interrupt the bot (IsBeingInterruptedByOther,
    /// GameLiving.cs:739), which a player gets "must wait" for (SpellHandler.cs:570-590; DEATHS.md A2), it quickcasts if
    /// it can, else swings, as a GameBot under melee fights (BotBrain.cs:3336-3370). Otherwise its own swing's interrupt
    /// (selfInterruptMilliseconds, GameLiving.cs:686-693) and the time between casts (the choice not due, which the old
    /// think filled with a swing that blocked the next cast) are waited out with auto-attack off, as a GameBot caster
    /// ends its own swings before a spell (BotBrain.cs:3074-3080); then it casts. due has no default: a caller that left
    /// it out would bring back the swing between casts. #167 C3: a swing that would end a caster's hold on its target
    /// (heldBreaksOnHit, SwingBreaksHold: a hit ends a root, a miss a mez, GameLiving.cs:1762-1777) becomes a wait, with
    /// nothing to cast too; no default either.</summary>
    public static PlayerBotCombatAction CombatAction(bool canCastSpell, bool interruptedByOther, long selfInterruptMilliseconds,
        bool interruptible, bool quickcastReady, bool due, bool heldBreaksOnHit)
    {
        PlayerBotCombatAction action = !canCastSpell ? PlayerBotCombatAction.Swing
            : !interruptible ? (due ? PlayerBotCombatAction.Cast : PlayerBotCombatAction.Swing)
            : interruptedByOther ? (quickcastReady ? PlayerBotCombatAction.Quickcast : PlayerBotCombatAction.Swing)
            : selfInterruptMilliseconds > 0 || !due ? PlayerBotCombatAction.WaitToCast : PlayerBotCombatAction.Cast;
        return heldBreaksOnHit && action is PlayerBotCombatAction.Swing ? PlayerBotCombatAction.WaitToCast : action;
    }

    /// <summary>Where the bot stops short of its target (#167 F6): at its spell's range while it has a spell it would
    /// cast at a cast choice (CombatAction with the choice due is no swing), between casts too; else in melee. Taken from
    /// this think's action, it walked an instant caster toward melee after each cast until its next choice (a swing
    /// between casts). A hit that blocks an interruptible spell with no Quickcast ready means melee, as at d1d524d. #167
    /// C3: a caster's held target (casterHold, CasterHolds) keeps the spell's stop whatever its interrupts or power, so
    /// its step back (KitePoint, short of the stop) is not walked back in, and it never walks into the held monster's
    /// reach. No default: a caller that left it out would bring back the melee trap.</summary>
    public static int CombatStopDistance(bool canCastSpell, bool interruptedByOther, bool interruptible, bool quickcastReady,
        int attackRange, int spellRange, bool casterHold) =>
        casterHold || HoldsSpellRange(canCastSpell, interruptedByOther, interruptible, quickcastReady)
            ? SpellStopDistance(spellRange) : MeleeStopDistance(attackRange);


    /// <summary>Engage's action and stop in reach of its target, and whether a caster keeps its hold (#167 C3; one plan,
    /// the review of 9aacd69: with Engage's own hold, swing and stop terms switched off one by one no test failed). The
    /// hold (CasterHolds) needs a spell range to keep (spellRange, StopSpellRange: with no damage spell at all there is
    /// none). Then the spell's stop (CombatStopDistance), and no swing at a root or mez (SwingBreaksHold) unless its
    /// step back is blocked (stepBackBlocked); the action is CombatAction's.</summary>
    public static (PlayerBotCombatAction Action, int Stop, bool Holds) CasterHoldPlan(PlayerBotRole role, bool held,
        bool breaksOnHit, bool otherHitting, bool stepBackBlocked, bool canCast, bool interrupted, long selfInterruptMs,
        bool interruptible, bool quickcastReady, bool due, int attackRange, int spellRange)
    {
        bool casterHold = CasterHolds(role, held, otherHitting) && spellRange > 0;
        return (CombatAction(canCast, interrupted, selfInterruptMs, interruptible, quickcastReady, due,
                SwingBreaksHold(casterHold, breaksOnHit, stepBackBlocked)),
            CombatStopDistance(canCast, interrupted, interruptible, quickcastReady, attackRange, spellRange, casterHold),
            casterHold);
    }

    /// <summary>The spell range Engage's stop reads (#167 C3): the chosen spell's (range: CalculateSpellRange); with none
    /// chosen (no nuke ready or paid for) its longest damage spell's, 0 with none. The review of 9aacd69: it read
    /// CalculateSpellRange(null), 32 (CastingComponent.cs:188-192), and a caster's hold took SpellStopDistance's 145, inside
    /// the held monster's reach, where the bot walked in and waited.</summary>
    public static int StopSpellRange(Spell chosen, IEnumerable<(Spell Spell, SpellLine Line)> nukes, Func<Spell, int> range)
    {
        if (chosen != null) return range(chosen);
        int longest = 0;
        if (nukes != null)
            foreach ((Spell spell, _) in nukes) longest = Math.Max(longest, range(spell));
        return longest;
    }

    /// <summary>Whether Engage stops the bot at its spell's range (CombatStopDistance), not in melee: a cast choice would
    /// be no swing.</summary>
    public static bool HoldsSpellRange(bool canCastSpell, bool interruptedByOther, bool interruptible, bool quickcastReady) =>
        CombatAction(canCastSpell, interruptedByOther, 0, interruptible, quickcastReady, due: true, heldBreaksOnHit: false)
            is not PlayerBotCombatAction.Swing;

    /// <summary>Whether a caster keeps its hold on its target (#167 C3, the caster melee trap: 81 of st-deathsB's 111
    /// caster deaths, in melee 15 s or more with 25% power or more): the Caster role, its target held (Hold: a stun, a
    /// root or a mez), and no other monster hitting it that is not held (otherHitting; then the add's control, C4, or a
    /// break-off, C2: a step back shakes no add, and a wait with an add's hits on it stands the bot idle). Then it holds
    /// the spell's stop (CombatStopDistance), steps back from melee (CasterAction) and never swings at a root or mez
    /// (SwingBreaksHold). Melee classes are unchanged.</summary>
    public static bool CasterHolds(PlayerBotRole role, bool held, bool otherHitting) =>
        role == PlayerBotRole.Caster && held && !otherHitting;

    /// <summary>Whether the bot's swing would end its caster's hold (#167 C3): a root or a mez (breaksOnHit) it keeps
    /// (CasterHolds). Not while its step back is blocked (the verifier's correction: Kites pauses it KitePauseMilliseconds,
    /// and a wait would stand the bot idle in the held monster's reach that long): then the old rule, a swing or a cast.
    /// A stun, which a hit doesn't end, takes the swing as before.</summary>
    public static bool SwingBreaksHold(bool casterHold, bool breaksOnHit, bool stepBackBlocked) =>
        casterHold && breaksOnHit && !stepBackBlocked;

    /// <summary>FIGHT and DEATH ctlBroken= (#167 C3): a hold the bot's own swing ended, as a think sees it: its target held
    /// by a root or a mez at the last think (heldBefore), no longer now, and a swing of its own counted since (CountSwing).
    /// A hold that ran out in the same 250 ms counts too.</summary>
    public static bool BrokeHold(bool heldBefore, bool heldNow, bool swung) => heldBefore && !heldNow && swung;

    /// <summary>Whether a player's use of Quickcast would take (QuickCastAbilityHandler.cs:41-84): the ability, not on
    /// already (it toggles off, :51-55), out of its 30-s disable after a quickcast spell (SpellHandler.cs:1430-1434,
    /// CastingComponent.cs:487), not casting (:44-48); the retry timer due. CombatAction uses it when hits interrupt an
    /// interruptible spell. A GameBot never quickcasts (BotBrain.cs:3959, GameBot.cs:2583-2585); a player does, so a
    /// player bot does.</summary>
    public static bool QuickcastReady(bool hasQuickcast, bool quickcastOn, int disabledMilliseconds, long nextTry, bool casting,
        long now) =>
        hasQuickcast && !quickcastOn && disabledMilliseconds <= 0 && now >= nextTry && !casting;

    /// <summary>The server's own test for the interrupt timer (SpellHandler.cs:571).</summary>
    public static bool Interruptible(Spell spell) => !spell.Uninterruptible && !spell.IsInstantCast;

    /// <summary>The self-heal's gate, out of a cast and the retry timer due: below SelfHealBelowPercent whatever its power,
    /// in a fight or out of one (DEATHS.md A6; a GameBot heals itself below 65% before it rests, BotBrain.cs:4890-4897);
    /// out of a fight also below RestHealBelowPercent with RestHealPowerPercent of its power or more left after the
    /// heal's cost (#167 F2), then it sits for power: a heal is quicker than sitting for health.</summary>
    public static bool WantsSelfHeal(byte healthPercent, bool casting, long now, long nextTry, bool inFight, byte manaPercent,
        int costPercent) =>
        !casting && now >= nextTry && (healthPercent < SelfHealBelowPercent || !inFight &&
            healthPercent < RestHealBelowPercent && manaPercent - costPercent >= RestHealPowerPercent);

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

    /// <summary>The pet leads (#167 F10c, our own rule): after sending its pet in, the owner holds its nukes until the
    /// target is on the pet or maxWaitMs pass, so the pet takes the monster and the owner is not hit. The GameBot holds
    /// one pulse only (AutonomousPetSupport.cs:888-921) and never while it is in combat (:913): nor does the bot while
    /// the target is on it or anything is hitting it (an add), which it fights.</summary>
    public static bool PetLeads(bool petAlive, bool targetOnPet, bool targetOnBot, bool beingHit, long sinceOrderMs,
        long maxWaitMs) =>
        petAlive && !targetOnPet && !targetOnBot && !beingHit && sinceOrderMs < maxWaitMs;

    /// <summary>Whether a cast counts in the cast watch (WatchCast): a damage spell. A summon's pet hurts the target only
    /// once it reaches it, so three summons in a row would read as fruitless and block the bot's spells (#167 F1).</summary>
    public static bool CountsInCastWatch(Spell spell) => spell.SpellType is not eSpellType.SummonTheurgistPet;

    /// <summary>The cast watch's step for one cast request at target (Engage): a damage spell's goes through WatchCast and
    /// makes target the watch's; a pet's (CountsInCastWatch) leaves the watch as it was, its target too (#167 F1).</summary>
    public static (T CastTarget, int Attempts, byte Mark, bool Stalled) WatchStep<T>(Spell spell, T castTarget, T target,
        int attempts, byte mark, byte targetHealth) where T : class
    {
        if (!CountsInCastWatch(spell)) return (castTarget, attempts, mark, false);
        (attempts, mark, bool stalled) = WatchCast(ReferenceEquals(castTarget, target), attempts, mark, targetHealth);
        return (target, attempts, mark, stalled);
    }

    /// <summary>Whether Engage makes the spell choice this think (#167 F1, F9): due, out of a cast, and at most every
    /// SpellChoiceMilliseconds while no cast follows (walking in, swinging, waiting), so a Theurgist's pet lookup and an
    /// area spell's add scan do not run every think. A cast, a refused pet and a new target reset the timer.</summary>
    public static bool ChoosesSpell(bool due, bool casting, long now, long nextChoice) => due && !casting && now >= nextChoice;

    // #167 F8: the self-buffs a bot keeps up, in this order: armour factor, absorb, bladeturn, ablative, damage shield,
    // damage add, base then spec stat buffs, the regeneration buffs (spells/RegenBuff.cs). No resist buff: the only
    // resist chants a class casts, the Paladin's (Soul Ward, Elemental Ward, Soul Shield, Elemental Shield), cost power
    // each pulse, which NextSelfBuff never runs.
    private static readonly eSpellType[] SelfBuffOrder =
    [
        eSpellType.BaseArmorFactorBuff, eSpellType.SpecArmorFactorBuff, eSpellType.PaladinArmorFactorBuff,
        eSpellType.ArmorAbsorptionBuff, eSpellType.Bladeturn, eSpellType.AblativeArmor, eSpellType.DamageShield,
        eSpellType.DamageAdd, eSpellType.StrengthBuff, eSpellType.DexterityBuff, eSpellType.ConstitutionBuff,
        eSpellType.QuicknessBuff, eSpellType.AcuityBuff, eSpellType.StrengthConstitutionBuff,
        eSpellType.DexterityQuicknessBuff, eSpellType.HealthRegenBuff, eSpellType.PowerRegenBuff,
        eSpellType.EnduranceRegenBuff,
    ];
    /// <summary>The kinds of SelfBuffOrder up, each once: PLAYER_BOT_FIGHT buffs= at the fight's first think (#167 F8).</summary>
    public static int SelfBuffsUp(IEnumerable<eSpellType> active) => active.Where(kind => Array.IndexOf(SelfBuffOrder, kind) >= 0).Distinct().Count();

    /// <summary>The next self-buff to cast (#167 F8): of the kinds in SelfBuffOrder not active on the bot, the first, its
    /// best rank the bot can cast now: at or below its level, on itself (a Self, Realm or Group target), no song (an
    /// instrument), the concentration it takes left, the power it costs (HasPowerFor), ready (recast). A chant (a
    /// pulsing spell) only of the kind wanted (wantedChant, WantedChant; #167 C13) while that kind is not the one up
    /// (chantUp): a new pulse effect ends the others (EffectListComponent.cs:362-371), so the bot swaps one for the other
    /// (the old rule, none while any was up, kept a Skald's DamageAdd chant and never ran its Song of Rest); and never one
    /// that costs power each pulse: it would drain the power the bot's rest waits for. No cap of the bot's own: the
    /// server's checks decide (its 20-effect limit counts concentration effects only, SpellHandler.cs:34, :818; a player
    /// has no GameBot exemption there).</summary>
    public static (Spell, SpellLine) NextSelfBuff(IEnumerable<(Spell Spell, SpellLine Line)> spells, IReadOnlySet<eSpellType> active,
        eSpellType? wantedChant, eSpellType? chantUp, int level, int concentrationLeft, int maxMana, int mana, Func<Spell, bool> ready)
    {
        Spell best = null;
        SpellLine bestLine = null;
        int bestKind = int.MaxValue;
        foreach ((Spell spell, SpellLine line) in spells)
        {
            int kind = Array.IndexOf(SelfBuffOrder, spell.SpellType);
            if (kind < 0 || kind > bestKind || active.Contains(spell.SpellType) || spell.Level > level ||
                spell.Target is not (eSpellTarget.SELF or eSpellTarget.REALM or eSpellTarget.GROUP) || spell.NeedInstrument ||
                spell.IsPulsing && (spell.SpellType != wantedChant || chantUp == wantedChant || spell.PulsePower != 0) ||
                spell.Concentration > concentrationLeft || !HasPowerFor(maxMana, mana, spell.Power) || !ready(spell))
                continue;
            if (best == null || kind < bestKind || spell.Level > best.Level || spell.Level == best.Level && spell.ID > best.ID)
                (best, bestLine, bestKind) = (spell, line, kind);
        }
        return (best, bestLine);
    }

    /// <summary>The chant kinds the bot can run (#167 C13): NextSelfBuff's chants, a pulsing spell of a SelfBuffOrder kind
    /// at or below its level, on itself, no song (an instrument) and no power each pulse.</summary>
    public static HashSet<eSpellType> ChantKinds(IEnumerable<(Spell Spell, SpellLine Line)> spells, int level)
    {
        HashSet<eSpellType> kinds = new();
        foreach ((Spell spell, _) in spells)
            if (spell.IsPulsing && spell.PulsePower == 0 && !spell.NeedInstrument && spell.Level <= level &&
                (spell.Target is eSpellTarget.SELF or eSpellTarget.REALM or eSpellTarget.GROUP) &&
                Array.IndexOf(SelfBuffOrder, spell.SpellType) >= 0)
                kinds.Add(spell.SpellType);
        return kinds;
    }

    /// <summary>The chant the bot runs (#167 C13; one at a time, EffectListComponent.cs:362-371): while it rests (resting:
    /// sitting, or WantsRest), the regeneration chant of what is short of its rest's stop (Recovered's: full health, the
    /// power stop, RestEnduranceStop), as a Skald runs its Song of Rest (a pulse runs on while it sits, SpellHandler.cs:196-232:
    /// its rest took about 32 s per kill in st-deathsB); else its first kind by SelfBuffOrder (a combat chant before a
    /// regeneration one), the one it fights with; none with no chant.</summary>
    public static eSpellType? WantedChant(IReadOnlyCollection<eSpellType> chantKinds, bool resting, byte health, byte endurance,
        int maxMana, byte mana, byte powerStop)
    {
        if (resting)
        {
            if (health < 100 && chantKinds.Contains(eSpellType.HealthRegenBuff)) return eSpellType.HealthRegenBuff;
            if (maxMana > 0 && mana < powerStop && chantKinds.Contains(eSpellType.PowerRegenBuff)) return eSpellType.PowerRegenBuff;
            if (endurance < RestEnduranceStop && chantKinds.Contains(eSpellType.EnduranceRegenBuff))
                return eSpellType.EnduranceRegenBuff;
        }
        eSpellType? first = null;
        int firstKind = int.MaxValue;
        foreach (eSpellType kind in chantKinds)
            if (Array.IndexOf(SelfBuffOrder, kind) is >= 0 and int order && order < firstKind)
                (first, firstKind) = (kind, order);
        return first;
    }

    /// <summary>Whether the self-buff scan runs (#167 C9): standing, or sitting once recovered (it stands to cast,
    /// CastOnSelf); never mid-rest. 708 of st-deathsB's 788 rests a buff ended were stood up in their middle, 2,989 s of
    /// casts, each a stand out of the sitting regeneration. The missing buffs go up in the post-kill window instead
    /// (SettleTarget scans at once), back to back.</summary>
    public static bool BuffScanAllowed(bool sitting, bool recovered) => !sitting || recovered;

    /// <summary>Whether the bot keeps a self-buff up out of a fight (#167 C9): a chant, or a buff of ShortBuffMilliseconds or
    /// more (or with no end: a concentration one), yes; a shorter one only a regeneration buff while its resource is under
    /// its rest's stop (Recovered's: full health, powerStop, RestEnduranceStop). A Spiritmaster's 60-s Benison of the Fallen
    /// (176 stands in st-deathsB), a Reaver's 30-s Surge of Maiming (85, and up at engage in only 35% of its fights) and a
    /// Shaman's 30-s Frigg's Cure at full health (157) were cast again and again between fights; an instant one goes up at
    /// the fight's start instead (OpensFight).</summary>
    public static bool KeepsUpOutOfFight(Spell spell, byte health, int maxMana, byte mana, byte powerStop, byte endurance) =>
        spell.IsPulsing || spell.Duration <= 0 || spell.Duration >= ShortBuffMilliseconds || spell.SpellType switch
        {
            eSpellType.HealthRegenBuff => health < 100,
            eSpellType.PowerRegenBuff => maxMana > 0 && mana < powerStop,
            eSpellType.EnduranceRegenBuff => endurance < RestEnduranceStop,
            _ => false,
        };

    /// <summary>A self-buff the bot casts at a fight's first think in reach (#167 C9): an instant one shorter than
    /// ShortBuffMilliseconds, no chant, that it no longer keeps up between fights (KeepsUpOutOfFight): a Reaver's Surge.
    /// One with a cast time (a Benison's 4 s) would cost the fight's opening; it is not kept up in fights (later).</summary>
    public static bool OpensFight(Spell spell) =>
        spell.IsInstantCast && !spell.IsPulsing && spell.Duration > 0 && spell.Duration < ShortBuffMilliseconds;

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
        if (state.Engaged) ReportFight(state, player, GameLoop.GameLoopTime, dead, false);   // with this life's kills before it
        if (state.Engaged && dead)
        {
            state.LifeKills++;                                    // every fight won, a monster another bot counted too
            state.LastKillTick = GameLoop.GameLoopTime;
            state.RecoverBeforePull = true;                       // rest before the next pull (WantsRest)
            state.NextSelfBuffTick = 0;                           // #167 C9: the self-buffs first, before the rest
            if (PlayerBotDriverReport.Kill(state.Target)) state.Kills++;
            if (state.TargetRespawnReach > 0)                     // #167 C7: rested out of (WalksToRestSpot)
                RememberKill(state.RecentKills, state.TargetSpawn, state.TargetRespawnReach, GameLoop.GameLoopTime);
        }
        else if (dead) state.RoamingSince = 0;                   // killed by another before this bot got there: not outgrown
        state.KilledWithAdds = state.Engaged && dead && state.FightAdds >= 2;   // #167 C2: the second-monster case
        state.Target = null;
        state.Engaged = false;
    }

    private static void Disengage(PlayerBotState state, GamePlayer player)
    {
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        state.Target = null;
        state.Engaged = false;
        state.KiteGoal = null;                                                          // #167 F11
    }

    private static void RefreshTarget(PlayerBotState state, GamePlayer player, long now)
    {
        bool fighting = state.Engaged && (player.attackComponent.AttackState || player.IsCasting || player.InCombat);
        bool searchDue = now >= state.NextTargetRefreshTick;
        if (state.Target != null && (fighting || !searchDue)) return;  // keep a target while fighting it
        if (searchDue) state.NextTargetRefreshTick = now + TargetRefreshMilliseconds;
        // Self-defence every think, then a monster coming at it (#167 F7: its scan every 2 s); the rings only every 2 s,
        // so an idle bot doesn't scan 5,000 units 4 times a second; none during the recovery after a release (#167 F4) or
        // a login's step out (C6), nor while a relocation is owed (SearchesRings): it pulls nothing until that is over
        int crowd = -1;                                                                    // FIGHT crowd=: FindWithin's
        GameNPC found = Attacker(state, player, now) ?? Incoming(state, player, now) ??     // self-defence first
            (SearchesRings(searchDue, state.AfterRelease, state.ReplanOwed)                // AWBC:3148
                ? FindWithin(state, player, ImmediateSearchRadius, now, out crowd) ??
                  FindWithin(state, player, SearchRadius, now, out crowd)
                : null);
        if (found == state.Target) return;
        state.Target = found;
        state.FromDefence = found != null && IsAttacking(player, found);                  // FIGHT and DEATH from=
        state.PickCrowd = crowd;                                                           //   and crowd= (#167)
        (state.TargetSpawn, state.TargetRespawnReach) = found?.Brain is StandardMobBrain brain   // #167 C7: its respawn
            ? RespawnOf(found.SpawnPoint, brain.AggroLevel, brain.AggroRange, player.IsObjectGreyCon(found)) : default;
        state.Engaged = false;
        state.NextLosTick = 0;
        state.SpellBlockedUntil = 0;                                                       // a new target clears the watch
        state.CastTarget = null;
        state.NextSpellChoiceTick = 0;                                                     //   and chooses at once
        state.KiteGoal = null;                                                             // #167 F11
        (state.Spell, state.SpellLine) = found == null ? (null, null) : Nuke(state, player, found);
    }

    // The candidates within the con cap go through the adds rule (PickUncrowded): the 2,600-unit ring takes only one with
    // no potential add near it or on the walk in to where the bot would stand at its stop (#167 C5: CrowdingAlong, from
    // the adds this scan finds, no new one), the 5,000-unit ring the least crowded. The scan reaches AddScanMargin past
    // the ring for the adds by a candidate at its edge; those along a walk in lie within it. One over the cap is passed over, never taken (DEATHS.md A4: the old rollback
    // reset the death steps to fit it), and the capped clock runs while the wider ring finds nothing else. An avoided
    // name is passed over too, unless the bot was sent to a camp of them (HuntAvoided).
    private static GameNPC FindWithin(PlayerBotState state, GamePlayer player, ushort radius, long now, out int crowd)
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
        GameNPC best = PickUncrowded(candidates, adds, uncrowdedOnly: radius < SearchRadius,   // #167 C5: the walk in
            new Vector3(player.X, player.Y, player.Z), PullStop(state, player), out crowd);      //   too; crowd= its count
        state.SearchAdds.Clear();                                                       // #167 F7: the rest spot's, read
        foreach ((GameNPC add, _, _) in adds) state.SearchAdds.Add(add);                //   live (WalksToRestSpot)
        candidates.Clear();                                                             // no other monster held past the
        adds.Clear();                                                                   //   search
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

    // #167 C5: the stop a pull takes (Engage's CombatStopDistance at its first think, no hit on the bot yet): the spell's
    // with a damage spell ready to cast now (Nuke's choice, no add looked for), else melee's.
    private static int PullStop(PlayerBotState state, GamePlayer player)
    {
        KnowSpells(state, player);
        (Spell nuke, _) = ChooseNuke(state.Nukes, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
            HasPowerFor(player.MaxMana, player.Mana, spell.Power), false);
        return CombatStopDistance(ShouldCast(nuke != null, player.ManaPercent, 0, 0), false, nuke != null && Interruptible(nuke),
            false, player.attackComponent.AttackRange, nuke == null ? 0 : player.castingComponent.CalculateSpellRange(nuke),
            casterHold: false);
    }

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

    /// <summary>Whether the search tick runs the rings (FindWithin; RefreshTarget's self-defence and Incoming run
    /// whatever): due, and not during the recovery after a release (#167 F4) or a login's step out (C6), nor while a
    /// relocation is owed (the review of 9aacd69: after a break-off, C2, the bot rested, and the first search after the
    /// rest ran before step 15's relocation, still leashed to the camp it fled, and could pull a monster of another name
    /// there).</summary>
    public static bool SearchesRings(bool searchDue, bool afterRelease, string replanOwed) =>
        searchDue && !afterRelease && replanOwed == null;

    /// <summary>For a task (the errands): true while the bot has a fight to finish, a monster hitting it or the one it
    /// is engaged with; then the task gives the think to the driver, which fights that one and seeks no other.</summary>
    public static bool UnderAttack(PlayerBotState state, long now) =>
        (state.Engaged && state.Target != null && state.Player.InCombat) || Attacker(state, state.Player, now) != null;

    // A monster hitting the bot, any (the rest gate's), else one coming at it (#167 F7).
    private static GameNPC Threat(PlayerBotState state, GamePlayer player, long now) =>
        Attacker(state, player, now, anyHitter: true) ?? Incoming(state, player, now);

    // #167 F7: the nearest monster coming at the bot (IsIncoming), from a scan every TargetRefreshMilliseconds, the search
    // tick's; between scans the one found while it still comes. The one Engage found unreachable is no threat, between
    // scans too (the review of 9549283: the cached one was re-picked and walked to each think): it would keep the bot
    // from ever sitting. The errands' UnderAttack stays the hitters' (no detour for a monster on its way).
    private static GameNPC Incoming(PlayerBotState state, GamePlayer player, long now)
    {
        if (now < state.NextIncomingTick)
        {
            if (state.Incoming is GameNPC held && !IsIncoming(held.TargetObject == player, IsAttackable(player, held),
                    player.GetDistanceTo(held), held == state.Unreachable && now < state.UnreachableUntil))
                state.Incoming = null;
            return state.Incoming;
        }
        state.NextIncomingTick = now + TargetRefreshMilliseconds;
        GameNPC best = null;
        int bestDistance = int.MaxValue;
        foreach (GameNPC npc in player.GetNPCsInRadius(IncomingRadius))
        {
            int distance = player.GetDistanceTo(npc);
            if (distance >= bestDistance || !IsIncoming(npc.TargetObject == player,
                    IsExperienceTarget(npc) && IsAttackable(player, npc), distance,
                    npc == state.Unreachable && now < state.UnreachableUntil))
                continue;
            best = npc;
            bestDistance = distance;
        }
        return state.Incoming = best;
    }

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

    /// <summary>Why the bot moves its leash now, or null: the relocation it owes (owed: Died after a death, OwesReplan,
    /// before it walks back, #167 F4: the GameBot replans 2.5-4 s after a solo death, AWBC:3780-3816; BrokeOff after a
    /// break-off, #167 C2, so it doesn't walk back into the camp); Capped when its searches have found monsters in
    /// reach but none under its cap for CappedRelocateMilliseconds (DEATHS.md A4), Outgrown when it has reached no target
    /// for RelocateAfterMilliseconds. None while the bot is immune (#167 C1): the release starts a 10-s immunity
    /// (TIMER_KILLED_BY_MOB, AbstractServerRules.cs:258-262, :280-285; ServerProperties.cs:1273-1274), during which a
    /// player may attack no monster (AbstractServerRules.cs:385-392; GST_Normal calls the base, NormalServerRules.cs:28),
    /// so IsAttackable dropped every monster from the plan and a death's replan found none (279 of 312 in st-deathsB);
    /// its anchor fell back to the bind. immune has no default: a caller that left it out would bring that back.</summary>
    public static string RelocateReason(long roamingSince, long cappedSince, long now, string owed, bool immune) =>
        immune ? null :
        owed ??
        (cappedSince != 0 && now - cappedSince >= CappedRelocateMilliseconds ? Capped :
         roamingSince != 0 && now - roamingSince >= RelocateAfterMilliseconds ? Outgrown : null);

    /// <summary>PLAYER_BOT_RELOCATE for a move: its fields as they were (runs/legcheck.py reads bot= and to), then the
    /// reason (RelocateReason) and the fallback camp's con, - for none (the death cap holds, Cap); after a death, the
    /// milliseconds since the release (RelocateSuffix).</summary>
    public static string RelocateLine(string bot, int level, Vector3 from, Vector3 to, int distance, string monster,
        int monsterLevel, ushort zone, string reason, ConColor? fallback, long sinceReleaseMs) =>
        $"PLAYER_BOT_RELOCATE bot={bot} level={level} from=({from.X:0},{from.Y:0}) to=({to.X:0},{to.Y:0},{to.Z:0}) " +
        $"distance={distance} monster=\"{monster}\" monsterLevel={monsterLevel} zone={zone} reason={reason} " +
        $"fallback={fallback?.ToString() ?? "-"}" + RelocateSuffix(reason, sinceReleaseMs);

    /// <summary>PLAYER_BOT_RELOCATE when no camp checked had a corridor (or none was found): checked= of them.</summary>
    public static string RelocateNoneLine(string bot, int level, int candidates, ushort zone, string reason,
        long sinceReleaseMs) =>
        $"PLAYER_BOT_RELOCATE bot={bot} level={level} none checked={candidates} zone={zone} reason={reason}" +
        RelocateSuffix(reason, sinceReleaseMs);

    // #167 C1: RELOCATE sinceRelease= on a death's replan, last (the fields before it stay as they were).
    private static string RelocateSuffix(string reason, long sinceReleaseMs) =>
        reason == Died ? $" sinceRelease={sinceReleaseMs}" : "";

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
    /// death until it plans its next camp (HoldSoloDeathRecovery, AWBC:2998-3012). So does one owed a relocation after a
    /// death (OwesReplan), from its release (replanSince, #167 F4). One that cannot roams as before (FallbackInPlace):
    /// held, it stood there for good. No hold lasts MaxHoldMilliseconds: FallbackInPlace has no cap for a death's
    /// relocation, nor for a capped bot that passed over only names it avoids, so with no camp planned by then the bot
    /// hunts where it is, under its cap and its avoided names.</summary>
    public static bool HoldsForRelocation(bool canRelocate, long cappedSince, long replanSince, bool walkingToAnchor, long now) =>
        canRelocate && !walkingToAnchor && (Holding(cappedSince, now) || Holding(replanSince, now));

    /// <summary>A hold begun at since (0: none) and not yet MaxHoldMilliseconds old.</summary>
    public static bool Holding(long since, long now) => since != 0 && now - since < MaxHoldMilliseconds;

    /// <summary>Whether a death owes a replan before the walk back (#167 F4): on no task, a leash it can move
    /// (CanRelocate: no lever spot bot, in its anchor's region), wherever it died (#167 C1b). Only a death within its
    /// anchor's leash and roam owed one before: a bot that died on a walk walked back past that camp, and those lives
    /// held 44 of st-deathsB's 146 again deaths. The plan starts from the death spot (RelocationOrigin) and bars the camp
    /// it died at, as every death's (RememberFailedCamp in HandleDeath).</summary>
    public static bool OwesReplan(bool onTask, bool canRelocate) => !onTask && canRelocate;

    /// <summary>PLAYER_BOT_BACK replan= (#167 C1): notOwed when the release owed no replan (OwesReplan); else moved once
    /// a camp was planned since, none when not (none found, or the hold ran out, or still owed at the fight).</summary>
    public static string BackReplan(bool owed, bool moved) => !owed ? "notOwed" : moved ? "moved" : "none";

    /// <summary>Where a relocation plans from (PlanRelocation's here): after a death (Died) the spot the bot died at, not
    /// its bind; else where it is (after a break-off, #167 C2, where it ran to).</summary>
    public static Vector3 RelocationOrigin(string reason, Vector3 deathSpot, Vector3 here) =>
        reason == Died ? deathSpot : here;

    /// <summary>Where a relocation's corridor test starts (a corridor is one zone's, PlayerTravelPath.cs:75-81): where the
    /// bot is when it stands in the plan's zone (inZone); after a death with the bot at a bind in another zone of its
    /// region, the death spot, which lies in the plan's zone (the review of 77ba437: the test was skipped then); none
    /// with no navmesh there.</summary>
    public static Vector3? CorridorOrigin(string reason, bool hasNavmesh, bool inZone, Vector3 deathSpot, Vector3 here) =>
        !hasNavmesh ? null : inZone ? here : reason == Died ? deathSpot : null;

    /// <summary>The recovery after a release (#167 F4), this think's step: it fights back what hits it (attacked); else
    /// it steps out of the reach of any monster that would aggro (stepsOut: in reach, with a clear spot to go to); then
    /// it plans the relocation owed after a death (replanOwed, OwesReplan); then it waits out its back-off (the GameBot's
    /// no-XP death streak, AutonomousDeathRecoveryPolicy) and the resurrection illness out there; then it is done. The
    /// release restores health, power and endurance (GamePlayer.cs:1723-1725): no rest to full.</summary>
    public static PlayerBotRecovery AfterReleaseStep(bool attacked, bool stepsOut, bool replanOwed, bool backingOff, bool ill) =>
        attacked ? PlayerBotRecovery.Fight : stepsOut ? PlayerBotRecovery.StepOut : replanOwed ? PlayerBotRecovery.Replan :
        backingOff || ill ? PlayerBotRecovery.Wait : PlayerBotRecovery.Done;

    /// <summary>Whether the bot breaks off and sprints out of reach (#167 C2): losing to two. 2 or more monsters on it, or
    /// right after a kill in a fight that had 2 or more on it with 1 still on it (addFight: the second-monster case, k12's
    /// rule at the kill, which would have saved 28 of 45 such deaths for 9 of 114 won follow-ups); below
    /// BreakOffHealthPercent, its target (at the kill, the one still on it) above BreakOffTargetPercent, no control ready
    /// for the add (C4's comes first), and only from chasers it outruns (Outruns). One monster in an ordinary fight, it
    /// fights on.</summary>
    public static bool BreaksOff(int monstersOnBot, bool addFight, byte health, byte targetHealth, bool addControlReady,
        bool outruns) =>
        (monstersOnBot >= 2 || addFight && monstersOnBot >= 1) && health < BreakOffHealthPercent &&
        targetHealth > BreakOffTargetPercent && !addControlReady && outruns;

    /// <summary>The second-monster case (#167 C2): a kill in a fight that had 2 or more on the bot (killedWithAdds,
    /// SettleTarget), for SecondMonsterMilliseconds from it (lastKillTick). The review of 9aacd69: the mark held until the
    /// next fight's first think, and a bot that sat at 35% broke off from one monster that jumped it mid-rest.</summary>
    public static bool SecondMonsterCase(bool killedWithAdds, long lastKillTick, long now) =>
        killedWithAdds && now - lastKillTick < SecondMonsterMilliseconds;

    /// <summary>A break-off's first rule as StartsBreakOff applies it (#167 C2; then the chasers' speed, Outruns, and a
    /// point to run to): BreaksOff over the monsters on the bot (hitters: each one's health and distance), its target's
    /// health while it lives, else (at a kill) the nearest hitter's, the second-monster case (SecondMonsterCase) and C4's
    /// add control (addControlReady, asked only once the rest holds: it scans).</summary>
    public static bool BreakOffFires(PlayerBotState state, byte health, IReadOnlyList<(byte Health, int Distance)> hitters,
        long now, Func<bool> addControlReady)
    {
        byte targetHealth = 0;
        if (state.Target?.IsAlive == true) targetHealth = state.Target.HealthPercent;
        else
            for (int i = 0, nearest = int.MaxValue; i < hitters.Count; i++)
                if (hitters[i].Distance < nearest) (targetHealth, nearest) = (hitters[i].Health, hitters[i].Distance);
        bool addFight = SecondMonsterCase(state.KilledWithAdds, state.LastKillTick, now);
        return BreaksOff(hitters.Count, addFight, health, targetHealth, addControlReady: false, outruns: true) &&
            BreaksOff(hitters.Count, addFight, health, targetHealth, addControlReady(), outruns: true);
    }

    /// <summary>Whether the bot outruns its fastest chaser (#167 C2): the sprint's lead on it, sprintSeconds x (SprintSpeed
    /// - v), clears its melee reach (ChaserReach; the review of 9aacd69: with no sprint, endurance 10 or less, the bot
    /// turned its back on a 191 or 185 chaser that started in its reach, and it swung at the bot's back for the 30 s of
    /// the break-off; a sprint is the old canSprint); and the chaser is no faster than its run (RunSpeed: the world DB sets
    /// enable_pve_speed off, MaxSpeedCalculator.cs:52-57, so after the sprint it runs at 191), or under its sprint
    /// (SprintSpeed) while the sprint's lead holds it off for the ChaseSeconds a monster chases after its last swing: from
    /// melee, sprintSeconds of sprint keep a chaser of speed v from a swing for sprintSeconds x 57 / (v - 191) seconds (the
    /// verifier's model).</summary>
    public static bool Outruns(int fastestSpeed, int sprintSeconds) =>
        (long)sprintSeconds * (SprintSpeed - fastestSpeed) > ChaserReach &&
        (fastestSpeed <= RunSpeed || fastestSpeed < SprintSpeed &&
         (long)sprintSeconds * (SprintSpeed - RunSpeed) >= (long)ChaseSeconds * (fastestSpeed - RunSpeed));

    /// <summary>The seconds a sprint lasts at the least from this endurance (#167 C2), at its real cost each second
    /// (GamePlayer.cs:2393-2413): the second's regeneration (moving or in combat, no base: buffs, items and abilities,
    /// ClassicRestRegeneration.cs:12-15) less longWind (5 less Long Wind's), scaled by the fatigue consumption when above
    /// 1% (FatigueConsumptionPercentCalculator.cs); the sprint ends at SprintStopEndurance or less (GamePlayer.cs:2420-2423,
    /// SprintECSEffect.cs) and can't start at SprintStartEndurance or less (GamePlayer.cs:8633). The first tick comes up to
    /// 1 s in, so one second less than the ticks it pays for; a cost of 0 or less never ends it. The clamp near full
    /// endurance (GamePlayer.cs:2412-2413) is left out: it costs at most one more tick's 5 once.</summary>
    public static int SprintSeconds(int endurance, int regeneration, int fatigueConsumption, int longWind)
    {
        if (endurance <= SprintStartEndurance) return 0;
        int net = regeneration - longWind;
        if (fatigueConsumption > 1) net = (int)Math.Ceiling(net * fatigueConsumption * 0.01);
        return net >= 0 ? int.MaxValue : (endurance - SprintStopEndurance - 1) / -net;
    }

    /// <summary>Where a break-off runs to (#167 C2): of the candidates, those ahead of straight away from the chasers (a
    /// dot product with here less their middle above 0: never square to them; any way with none to run from), out of every
    /// other monster's reach (InAddReach), the farthest from its nearest chaser; none, null.</summary>
    public static Vector3? BreakOffPoint<T>(Vector3 here, IReadOnlyList<Vector3> chasers, IEnumerable<Vector3> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class
    {
        Vector2 away = Vector2.Zero;
        for (int i = 0; i < chasers.Count; i++) away += new Vector2(here.X - chasers[i].X, here.Y - chasers[i].Y);
        bool anyWay = away.LengthSquared() < 1;
        Vector3? best = null;
        float bestGap = -1;
        foreach (Vector3 point in candidates)
        {
            if (!anyWay && Vector2.Dot(new Vector2(point.X - here.X, point.Y - here.Y), away) <= 0 || InAddReach(point, adds))
                continue;
            float gap = float.MaxValue;
            for (int i = 0; i < chasers.Count; i++) gap = Math.Min(gap, Vector3.DistanceSquared(point, chasers[i]));
            if (gap > bestGap) (best, bestGap) = (point, gap);
        }
        return best;
    }

    /// <summary>The next break-off point (#167 C2, the review of 9aacd69): BreakOffPoint's, leaving out the points the
    /// break-off was blocked on (blocked; within BreakOffBlockedRadius of one), and only one the bot can walk to
    /// (walkable: the corridor test), BreakOffCorridorTries at most; one with no walk is blocked too. A point on another mesh
    /// island (across water, up a cliff) blocked the step at once, and the next think chose it again from the same spot
    /// for 30 s, the bot standing in its chasers' hits; none left, null: it fights.</summary>
    public static Vector3? NextBreakOffPoint<T>(Vector3 here, IReadOnlyList<Vector3> chasers, IReadOnlyList<Vector3> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, List<Vector3> blocked, Func<Vector3, bool> walkable)
        where T : class
    {
        bool Blocked(Vector3 point) => blocked.Exists(b => Vector3.DistanceSquared(b, point) <=
            BreakOffBlockedRadius * BreakOffBlockedRadius);
        for (int i = 0; i < BreakOffCorridorTries; i++)
        {
            if (BreakOffPoint(here, chasers, candidates.Where(point => !Blocked(point)), adds) is not Vector3 point) return null;
            if (walkable(point)) return point;
            blocked.Add(point);
        }
        return null;
    }


    /// <summary>A break-off's step this think (#167 C2): with no monster still after it, Done; still chased
    /// BreakOffMilliseconds after it began, Fight (it turns on them); at its point (or blocked) with one still after it,
    /// NextPoint; else Run.</summary>
    public static PlayerBotBreakOff BreakOffStep(bool arrived, int chasers, long sinceMs) =>
        chasers == 0 ? PlayerBotBreakOff.Done : sinceMs >= BreakOffMilliseconds ? PlayerBotBreakOff.Fight :
        arrived ? PlayerBotBreakOff.NextPoint : PlayerBotBreakOff.Run;

    /// <summary>A break-off's start (#167 C2): the camp it ran from and the names of its target and chasers, remembered as
    /// after a death (Remember, DEATHS.md A3; RememberFailedCamp, #167 F4), so it neither pulls them nor plans back
    /// there.</summary>
    public static void BrokeOffFrom(PlayerBotState state, ushort region, Vector3 at, IEnumerable<string> names, long now)
    {
        foreach (string name in names) Remember(state.AvoidUntil, name, now);
        RememberFailedCamp(state.FailedCamps, region, at, now);
    }

    /// <summary>A break-off's end (#167 C2): it owes a relocation (BrokeOff) when it would owe one after a death
    /// (OwesReplan), planned at once from where it stands, the hold from now. FailedCamps alone is read by PlanRelocation
    /// only, and with its anchor left at the camp Roam's leash walked the bot back in; with no camp planned, EndReplan
    /// moves the anchor here.</summary>
    public static void AfterBreakOff(PlayerBotState state, bool owesReplan, long now)
    {
        if (!owesReplan) return;
        state.ReplanOwed = BrokeOff;
        state.ReplanSince = now;
        state.NextRelocateTick = 0;
        state.RelocateRejected.Clear();
    }

    /// <summary>A break-off's end in the state (#167 C2; EndBreakOff's, the review of 9aacd69): the run cleared, and unless
    /// the bot died (its death's own recovery then, HandleDeath) the relocation owed (AfterBreakOff, owesReplan:
    /// OwesReplan's rule); fought, no new break-off for BreakOffMilliseconds: it fights them out.</summary>
    public static void FinishBreakOff(PlayerBotState state, string outcome, bool owesReplan, long now)
    {
        (state.BreakOffSince, state.BreakOffGoal, state.BreakOffArrived) = (0, null, false);
        state.BreakOffChasers.Clear();
        state.BreakOffBlocked.Clear();
        if (outcome == Died) return;
        if (outcome == Fought) state.NextBreakOffTick = now + BreakOffMilliseconds;
        AfterBreakOff(state, owesReplan, now);
    }

    /// <summary>Where a target respawns and the reach it aggroes from then (#167 C7), read at the pick: its spawn spot
    /// (GameNPC.SpawnPoint; RespawnTimerCallback puts it back there, GameNPC.cs:3278-3280), not where it stands (the
    /// verifier's correction: a defence pick often stands far from it), and its aggro range when it would aggro the bot
    /// at all (IsPotentialAdd: it respawns busy with no one); else none (Reach 0).</summary>
    public static (Vector3 At, int Reach) RespawnOf(Point3D spawn, int aggroLevel, int aggroRange, bool greyToBot) =>
        spawn != null && IsPotentialAdd(aggroLevel, aggroRange, greyToBot, false)
            ? (new Vector3(spawn.X, spawn.Y, spawn.Z), aggroRange) : default;

    /// <summary>A kill's respawn (#167 C7), kept with the bot's other kills of the last RespawnWatchMilliseconds, the last
    /// RecentKillCount of them.</summary>
    public static void RememberKill(List<(Vector3 At, int Reach, long KilledAt)> kills, Vector3 at, int reach, long now)
    {
        kills.RemoveAll(kill => now - kill.KilledAt >= RespawnWatchMilliseconds);
        if (kills.Count >= RecentKillCount) kills.RemoveRange(0, kills.Count - RecentKillCount + 1);
        kills.Add((at, reach, now));
    }

    /// <summary>The spawn spots a bot rests out of (#167 C7): its kills' of the last RespawnWatchMilliseconds, each with the
    /// reach it aggroes from. 142 of st-deathsB's rests were stood up 75-95 s after the kill by a monster with the killed
    /// one's name (the world's usual RespawnInterval is 80 s, GameNPC.cs:1084-1087), and 18 deaths followed; AddReach
    /// gives a dead monster none, so F7's rest spot never saw the camp the bot had just emptied.</summary>
    public static IEnumerable<(Vector3 At, int Reach)> RespawnSpots(IReadOnlyList<(Vector3 At, int Reach, long KilledAt)> kills,
        long now)
    {
        foreach ((Vector3 at, int reach, long killedAt) in kills)
            if (now - killedAt < RespawnWatchMilliseconds) yield return (at, reach);
    }

    /// <summary>Whether a spot is in the reach of a potential add (the adds rule's ring, Crowding's: its aggro range +
    /// AddMargin) (#167 F4, F7).</summary>
    public static bool InAddReach<T>(Vector3 at, IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class
    {
        for (int i = 0; i < adds.Count; i++)
        {
            float reach = adds[i].AggroRange + AddMargin;
            if (Vector3.DistanceSquared(adds[i].At, at) <= reach * reach) return true;
        }
        return false;
    }

    /// <summary>The candidates out of every potential add's reach (InAddReach), nearest to here first.</summary>
    public static List<Vector3> ClearSpots<T>(Vector3 here, IEnumerable<Vector3> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class =>
        candidates.Where(spot => !InAddReach(spot, adds)).OrderBy(spot => Vector3.DistanceSquared(spot, here)).ToList();

    /// <summary>Where the bot stands out of reach (#167 F4, F7): here when no potential add reaches it; else the nearest
    /// clear candidate; with none, here.</summary>
    public static Vector3 RestSpot<T>(Vector3 here, IEnumerable<Vector3> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class =>
        InAddReach(here, adds) && ClearSpots(here, candidates, adds) is [Vector3 first, ..] ? first : here;

    /// <summary>A monster coming at the bot (#167 F7): it may attack it (a live monster, IsAttackable), the bot is its
    /// target (a monster's aggro state sets it and its exit clears it, StandardMobState.cs:113) and it is within
    /// IncomingRadius. It counts as an attacker before its first hit: the bot stands, and engages it first.</summary>
    /// <summary>A control spell (#167 F11): the GameBot's split (GameBot.cs:1957-1962: a mez, or a speed decrease of 99,
    /// a root), and a stun; on an enemy, single-target (an area one would pull the adds round the target), with a
    /// range; no song (an instrument), as the damage spells, heals, self-buffs and chants leave out (the review of
    /// 9aacd69: a Minstrel's Instruments mezzes, InstrumentRequirement 3, went to its add step for every role, and the
    /// server refused each with no instrument, SpellHandler.cs:546-553).</summary>
    public static bool IsControlSpell(Spell spell) =>
        (spell.SpellType is eSpellType.Stun or eSpellType.Mesmerize ||
         spell.SpellType is eSpellType.SpeedDecrease && spell.Value >= 99) &&
        spell.Target is eSpellTarget.ENEMY && spell.Radius <= 0 && spell.Range > 0 && !spell.NeedInstrument;

    /// <summary>The control to cast (#167 F11): on the target a stun first (damage doesn't break it, GameLiving.cs:1784-1803;
    /// the GameBot keeps stuns on its own target too, BotBrain.cs:3887-3890), then a root, then a mez (any damage breaks
    /// both); on an add (forAdd, #167 C4) a mez first, then a root, then a stun, the longest hold on a monster the bot
    /// doesn't hit (the GameBot mezzes adds, never its target, BotBrain.cs:3887-3893), and no mez from a pet class
    /// (petClass): its pet defends it and hits the add (ControlledMobBrain.OnOwnerAttacked, ControlledMobBrain.cs:930-970).
    /// One the monster can take now (allowed, #167 C8: the server's own tests, Takes; and on the target a mez only as the
    /// escape, MezWanted), the highest level ready.</summary>
    public static (Spell, SpellLine) ChooseControl(IEnumerable<(Spell Spell, SpellLine Line)> controls, Func<Spell, bool> ready,
        Func<Spell, bool> allowed, bool forAdd, bool petClass)
    {
        (Spell Spell, SpellLine Line) best = default;
        int bestRank = int.MaxValue;
        foreach ((Spell spell, SpellLine line) in controls)
        {
            bool mez = spell.SpellType is eSpellType.Mesmerize;
            int rank = forAdd ? spell.SpellType switch { eSpellType.Mesmerize => 0, eSpellType.Stun => 2, _ => 1 }
                : spell.SpellType switch { eSpellType.Stun => 0, eSpellType.Mesmerize => 2, _ => 1 };
            if (forAdd && petClass && mez || !allowed(spell) || !ready(spell)) continue;
            if (best.Spell == null || rank < bestRank || rank == bestRank && spell.Level > best.Spell.Level)
                (best, bestRank) = ((spell, line), rank);
        }
        return best;
    }

    /// <summary>Whether an NPC's diminishing immunity (#167 C8; none: null) lets a stun or a mez of this duration (ms) on
    /// it: the server's own test (NpcImmunityEffect.CanApplyNewEffect, ECSImmunityEffect.cs:38-44; CCSpellHandler.cs:168-169,
    /// :282-283). The first hold starts it for 60 s (CrowdControlECSEffect.cs:92-93, :131-132; ECSImmunityEffect.cs:35),
    /// and the n-th holds the duration over 2(n - 1) (ECSImmunityEffect.cs:46-51): an 8-s Prismatic Sheen holds 4 s the
    /// second time and 2 s the third; under 1 s it is refused. The old StunOk and MezOk read any such effect as immune, so the Eldritch and the Mentalist cast 0.90
    /// and 0.92 controls per kill in st-deathsB against the root classes' 1.30-1.81. The server's duration also scales
    /// with its hit chance (CCSpellHandler.cs:58-92): the spell's is near it.</summary>
    public static bool DiminishedAllows(NpcImmunityEffect immunity, int duration) =>
        immunity == null || immunity.CanApplyNewEffect(duration);

    /// <summary>Whether a mez on the target goes now (#167 C8): only as the escape, the monster reaching the bot before the
    /// mez's cast and its margin end; mezzed sooner, the bot's next nuke broke it (GameLiving.cs:1784-1803) for the cost
    /// of a nuke. An add takes one whenever (#167 C4).</summary>
    public static bool MezWanted(long contactMs, int mezCastMs) => contactMs <= mezCastMs + ControlMarginMilliseconds;

    /// <summary>CasterStep's filter on its target's controls (#167 C8): one the target takes (takes, Takes), and a mez only
    /// as the escape (MezWanted at contactMs). TargetFirst reads the same choice (the review of 9aacd69).</summary>
    public static bool CasterControlAllowed(Spell spell, bool takes, long contactMs) =>
        takes && (spell.SpellType is not eSpellType.Mesmerize || MezWanted(contactMs, spell.CastTime));

    /// <summary>The add the bot controls (#167 C4): the nearest monster with the bot as its target (TargetsBot), not held
    /// (Held: a stun, a root or a mez) and not its target; none, null. Caster add fights were fatal 51 times in 53 in
    /// st-deathsB: CasterStep controlled only the target.</summary>
    public static T ChooseAdd<T>(IReadOnlyList<(T Npc, int Distance, bool TargetsBot, bool Held)> monsters, T target)
        where T : class
    {
        T best = null;
        int bestDistance = int.MaxValue;
        foreach ((T npc, int distance, bool targetsBot, bool held) in monsters)
            if (targetsBot && !held && npc != target && distance < bestDistance)
                (best, bestDistance) = (npc, distance);
        return best;
    }

    /// <summary>The bot's step against its add (#167 C4), for every role with a control (hasControl; its cast time,
    /// controlCastMs, and whether hits interrupt it, interruptible): it controls the add while the cast ends before the
    /// add's contact with its margin and no hit interrupts the bot; else only with Quickcast, which it turns on when ready
    /// (then CasterStep sees it as not ready: one Quickcast in 30 s, SpellHandler.cs:1430-1434, goes to an un-held add
    /// first) and casts with when on. An instant or uninterruptible control goes whatever the hits and the contact, as
    /// CombatAction casts one (the review of 9aacd69: a cast time of 0 read as no control, so a Skald's shouts and a
    /// Minstrel's yells never went at an add). Nothing while the target's control comes first (targetFirst,
    /// TargetFirst).</summary>
    public static PlayerBotAddAction AddAction(bool hasAdd, bool hasControl, long addContactMs, bool targetFirst,
        int controlCastMs, bool interruptible, bool interrupted, bool quickcastReady, bool quickcastOn)
    {
        if (!hasAdd || !hasControl || targetFirst) return PlayerBotAddAction.None;
        if (quickcastOn || !interruptible) return PlayerBotAddAction.Control;
        if (addContactMs > controlCastMs + ControlMarginMilliseconds && !interrupted) return PlayerBotAddAction.Control;
        return quickcastReady ? PlayerBotAddAction.QcControl : PlayerBotAddAction.None;
    }

    /// <summary>Whether a caster's control on its target comes before the add's (#167 C4): it controls its target
    /// (controlsTarget: CasterStep's tries left and a control the target takes), the target not held, coming at it and
    /// reaching it before the add; then the add waits for CasterStep. Both in melee, the add first, as Quickcast's
    /// order has it.</summary>
    public static bool TargetFirst(bool controlsTarget, bool targetHeld, bool targetClosing, long targetContactMs,
        long addContactMs) =>
        controlsTarget && !targetHeld && targetClosing && targetContactMs < addContactMs;

    /// <summary>Whether a control keeps an add off the bot (#167 C4): a mez or a stun stops its hits anywhere; a root only
    /// while the add is still away (past ContactDistance), as in melee a rooted monster still hits (inf.).</summary>
    public static bool ControlKeepsAddOff(Spell spell, int addDistance) =>
        spell.SpellType is not eSpellType.SpeedDecrease || addDistance > ContactDistance;

    /// <summary>Whom the bot faces (#167 C4, the verifier's correction): while a cast at the add is under way (addCast,
    /// casting) the add, as CheckBeginCast and CheckEndCast need the spell's target in the front 180 degrees
    /// (SpellHandler.cs:725, :939); else its target.</summary>
    public static T FacesWhom<T>(T target, T addCast, bool casting) where T : class =>
        casting && addCast != null ? addCast : target;

    /// <summary>The add a cast under way is at (#167 C4, CastAtAdd's addCastTarget): while the bot casts and the add lives;
    /// else none.</summary>
    public static T AddCastNow<T>(T addCastTarget, bool casting, bool addAlive) where T : class =>
        casting && addAlive ? addCastTarget : null;

    /// <summary>Whom Engage faces this think (#167 C4; the review of 9aacd69): FacesWhom over the add a cast under way is
    /// at (AddCastNow).</summary>
    public static T EngageFacing<T>(T target, T addCastTarget, bool casting, bool addAlive) where T : class =>
        FacesWhom(target, AddCastNow(addCastTarget, casting, addAlive), casting);

    /// <summary>The order of Engage's steps once in reach (#167 C4; the review of 9aacd69): a cast under way, it waits; at a
    /// due cast choice the add's step first when it acts (addAction: AddPlan's), then the pet leads (petLeads, PetLeads),
    /// then CasterStep and CombatAction.</summary>
    public static PlayerBotEngageStep EngageStepOrder(bool due, bool casting, PlayerBotAddAction addAction, bool petLeads) =>
        casting ? PlayerBotEngageStep.Wait : due && addAction is not PlayerBotAddAction.None ? PlayerBotEngageStep.Add :
        petLeads ? PlayerBotEngageStep.PetLeads : PlayerBotEngageStep.Caster;

    /// <summary>Whether a monster is on the bot for the break-off (#167 C2, C4): it attacks the bot and isn't mezzed or
    /// stunned (crowdControlled). A held monster keeps the bot as its target (CrowdControlECSEffect.cs:26-37 stops its
    /// attack only) but hits nothing: a controlled add starts no break-off, and chases nothing.</summary>
    public static bool OnBot(bool attacksBot, bool crowdControlled) => attacksBot && !crowdControlled;

    /// <summary>The milliseconds a monster needs to come within ContactDistance of the bot at its own speed (its base
    /// speed: a rooted one's is 0); none at a speed of 0.</summary>
    public static long TimeToContact(int distance, int speed) =>
        speed <= 0 ? long.MaxValue : Math.Max(0, distance - ContactDistance) * 1000L / speed;

    /// <summary>A caster's step against its target (#167 F11). A held one (rooted, mezzed or stunned) it nukes, unless the
    /// monster would reach it before the next nuke ends and either the hold breaks on the nuke's damage (a root or a mez,
    /// GameLiving.cs:1784-1803) or hits interrupt the bot (#167 C3: a stun in melee too): then it steps back to range
    /// first. The step back no longer waits for Engage's spell stop (83dcb96's spellStop gate): a caster's held target
    /// keeps that stop (CombatStopDistance's casterHold), and Kites runs before the walk in; in melee with hits on it the
    /// gate made the held branch nuke, CombatAction swing, and the swing end the root (C3, the melee trap). One coming
    /// at it (closing) with a control ready (controlCastMs, 0 for none): it nukes while there is time for a nuke and the
    /// control after it; then it controls while the cast ends before contact (Quickcast first when hits interrupt it); in
    /// melee it quickcasts the control (the GameBot's rule: a caster under melee controls, BotBrain.cs:3874-3901), or casts
    /// it before the first hit, or with Quickcast on. #167 C11: not while the nukes kill it first (killsFirst, KillsFirst;
    /// no default): the control cost a GREEN pull of 3 nukes 9.5 s in st-deathsA and 16.8 s in st-deathsB. Else Nuke:
    /// CombatAction's cast, wait, quickcast or swing.</summary>
    public static PlayerBotCasterAction CasterAction(bool closing, long contactMs, bool held, bool holdBreaksOnHit,
        int controlCastMs, int nukeCastMs, bool interrupted, bool quickcastReady, bool quickcastOn, bool killsFirst)
    {
        if (held)
            return (holdBreaksOnHit || interrupted) && contactMs < nukeCastMs + ControlMarginMilliseconds
                ? PlayerBotCasterAction.StepBack : PlayerBotCasterAction.Nuke;
        if (killsFirst || !closing || controlCastMs <= 0 ||
            contactMs > nukeCastMs + controlCastMs + 2L * ControlMarginMilliseconds)
            return PlayerBotCasterAction.Nuke;
        if (contactMs > controlCastMs + ControlMarginMilliseconds)
            return quickcastOn || !interrupted ? PlayerBotCasterAction.Control
                : quickcastReady ? PlayerBotCasterAction.QcControl : PlayerBotCasterAction.Nuke;
        return quickcastOn ? PlayerBotCasterAction.Control : quickcastReady ? PlayerBotCasterAction.QcControl
            : !interrupted ? PlayerBotCasterAction.Control : PlayerBotCasterAction.Nuke;
    }

    /// <summary>The nukes the target still needs (#167 C11), from what the fight's casts took off it so far (its health
    /// at the fight's first think, startHealth, less now, over nukesCast): ceil(health / ((startHealth - health) /
    /// nukesCast)); nothing measured (no cast, or no health off it yet), int.MaxValue.</summary>
    public static int NukesNeeded(int startHealth, int health, int nukesCast)
    {
        int taken = startHealth - health;
        if (taken <= 0 || nukesCast <= 0) return int.MaxValue;
        return (int)Math.Min(int.MaxValue, ((long)health * nukesCast + taken - 1) / taken);
    }

    /// <summary>Whether the nukes kill the target before it reaches the bot (#167 C11): the nukes it needs land by
    /// contact, one per nuke's cast and 250 ms (Engage's next cast choice, Math.Max(900, cast + 250)), and one more with
    /// Quickcast ready (the killing nuke through the hits, CombatAction's Quickcast).</summary>
    public static bool KillsFirst(int needed, long contactMs, int nukeCastMs, bool quickcastReady) =>
        needed <= contactMs / Math.Max(1, nukeCastMs + 250) + (quickcastReady ? 1 : 0);

    /// <summary>CasterStep's killsFirst (#167 C11; the review of 9aacd69): the nukes the fight's casts say the target still
    /// needs (NukesNeeded, from its health at the fight's first think) land first (KillsFirst), the bot at
    /// KillFirstHealthPercent or more and one monster at most on it.</summary>
    public static bool KillsFirstNow(int engageHealth, int health, int casts, long contactMs, int nukeCastMs,
        bool quickcastReady, byte botHealth, int attackers) =>
        KillsFirst(NukesNeeded(engageHealth, health, casts), contactMs, nukeCastMs, quickcastReady) &&
        botHealth >= KillFirstHealthPercent && attackers <= 1;

    /// <summary>Where a caster steps back to (#167 F11): range units from its target, straight away from it (along +X when
    /// on top of it), else turned by KiteTurns, the first out of every potential add's reach (InAddReach) so the step
    /// pulls nothing; none, null. The caller puts it on the navmesh.</summary>
    public static Vector3? KiteStep<T>(Vector3 bot, Vector3 target, int range, IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds)
        where T : class
    {
        Vector2 away = new(bot.X - target.X, bot.Y - target.Y);
        double heading = away.LengthSquared() < 1 ? 0 : Math.Atan2(away.Y, away.X);
        foreach (int turn in KiteTurns)
        {
            double angle = heading + turn * Math.PI / 180;
            Vector3 point = new(target.X + (float)(Math.Cos(angle) * range), target.Y + (float)(Math.Sin(angle) * range), bot.Z);
            if (!InAddReach(point, adds)) return point;
        }
        return null;
    }

    /// <summary>The step back's point (#167 C3): KiteStep's, clear of the adds (Clear); with none clear and the bot in
    /// melee (inMelee), the point straight away from the target KiteFallbackDistance out, adds or not (inf.: a null left it
    /// in the held monster's reach, where its hits stopped every cast); else null, as before. The caller puts it on the
    /// navmesh and checks a clear one's place there against the adds again.</summary>
    public static (Vector3 Point, bool Clear)? KiteTarget<T>(Vector3 bot, Vector3 target, int range,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, bool inMelee) where T : class
    {
        if (KiteStep(bot, target, range, adds) is Vector3 clear) return (clear, true);
        return inMelee ? (KiteStep(bot, target, KiteFallbackDistance, Array.Empty<(T, Vector3, int)>()).Value, false) : null;
    }

    /// <summary>Whether a step back's point on the navmesh keeps the bot within Engage's stop of its target (the review
    /// of 5feb32b): Engage's distance (GetDistanceTo) is 3D, KiteStep's range flat at the bot's height, and OnMesh moves
    /// the point up or down; the bot ends within KiteArriveDistance of it. Past the stop, Engage walked the bot back in
    /// and it cast nothing while the hold lasted.</summary>
    public static bool KiteFits(Vector3 point, Vector3 target, int stop) =>
        Vector3.Distance(point, target) <= stop - KiteArriveDistance;

    /// <summary>The step back's range from its target (#167 F11): KiteStopMargin short of Engage's stop, only when that is
    /// farther out than the bot stands (distance); else none (the review of 9aacd69: with a stop of 145 the "step back"
    /// went 95 units from a target the bot stood 145 from).</summary>
    public static int? KiteRange(int stop, int distance) => stop - KiteStopMargin > distance ? stop - KiteStopMargin : null;

    /// <summary>A monster coming at the bot (#167 F7): it may attack it, it has the bot as its target and it is within
    /// IncomingRadius; not the one Engage found unreachable while that lasts (unreachable), between scans too.</summary>
    public static bool IsIncoming(bool targetsBot, bool attackable, int distance, bool unreachable) =>
        targetsBot && attackable && distance <= IncomingRadius && !unreachable;

    /// <summary>The candidate spots round here: directions points on each ring, at here's height, the first along +X; the
    /// caller puts each on the navmesh.</summary>
    public static IEnumerable<Vector3> RingPoints(Vector3 here, int[] rings, int directions)
    {
        foreach (int ring in rings)
            for (int i = 0; i < directions; i++)
            {
                double angle = 2 * Math.PI * i / directions;
                yield return new(here.X + (float)(Math.Cos(angle) * ring), here.Y + (float)(Math.Sin(angle) * ring), here.Z);
            }
    }

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
    /// over the cap, and such a monster is no spot: a camp full of them fits no cap. Nor is one the bot died at in the
    /// last 15 minutes (within RoamRadius of a failedCamps spot; #167 F4: the fit pass bars it too, so the bot no longer
    /// goes back to the nearest camp that fits, the one it died at). With none that fits, the GameBot's
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
        bool Failed(int i) => failedCamps != null && failedCamps.Any(f =>
            Vector3.DistanceSquared(monsters[i].At, f) <= RoamRadius * RoamRadius);
        List<T> fit = new();
        foreach (int i in order)
        {
            if (monsters[i].Con <= cap && !monsters[i].Avoided && !Failed(i) && CampCon(monsters, grid, i).Fresh <= cap)
                fit.Add(monsters[i].Npc);
            if (fit.Count == count) break;
        }
        if (fit.Count > 0) return (fit.ToArray(), null, false);
        List<(int I, ConColor Median, bool Fresh, bool Failed)> safe = new();
        foreach (int i in order)
        {
            (ConColor median, ConColor fresh) = CampCon(monsters, grid, i);
            if (median <= natural)
                safe.Add((i, median, fresh <= natural, Failed(i)));
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
    /// it picks. Those whose time is up go. Each is its region's, and a region change keeps them (#167 F4).</summary>
    public static void RememberFailedCamp(List<(ushort Region, Vector3 At, long Until)> failed, ushort region, Vector3 at,
        long now)
    {
        failed.RemoveAll(camp => camp.Until <= now);
        failed.Add((region, at, now + AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds));
    }

    /// <summary>The failed camps in a region the bot still avoids (PlanRelocation).</summary>
    public static Vector3[] FailedCamps(IReadOnlyList<(ushort Region, Vector3 At, long Until)> failed, ushort region, long now) =>
        failed.Where(camp => camp.Region == region && now < camp.Until).Select(camp => camp.At).ToArray();

    // The GameBot's recovery thresholds (AutonomousRestPolicy.NeedsRecovery, AutonomousBotDecisionEngine.cs:384-394: 70%
    // health, 45% power, 35% endurance; the pilot's 50/22/20 at APP:186-187 left the bots pulling at half health,
    // levelmatch/DEATHS.md A1). Power counts only for a class with a pool: one with none (ManaStat UNDEFINED,
    // MaxManaCalculator.cs:32-51) reads ManaPercent 0 (GameLiving.cs:2753) and would sit forever.
    public static bool NeedsRest(byte healthPercent, int maxMana, byte manaPercent, byte endurancePercent) =>
        AutonomousRestPolicy.NeedsRecovery(healthPercent, manaPercent, endurancePercent, maxMana > 0);

    /// <summary>The class's role (#167 F3), as tools/lmroles.py's CLASS_ROLES gives it: the Hunter has a pet and counts as
    /// pet; any class not named is melee.</summary>
    public static PlayerBotRole RoleOf(int classId) => (eCharacterClass)classId switch
    {
        eCharacterClass.Theurgist or eCharacterClass.Necromancer or eCharacterClass.Cabalist or eCharacterClass.Hunter or
            eCharacterClass.Spiritmaster or eCharacterClass.Bonedancer or eCharacterClass.Enchanter or eCharacterClass.Druid or
            eCharacterClass.Animist => PlayerBotRole.Pet,
        eCharacterClass.Cleric or eCharacterClass.Friar or eCharacterClass.Healer or eCharacterClass.Shaman or
            eCharacterClass.Warden or eCharacterClass.Bard => PlayerBotRole.Healer,
        eCharacterClass.Wizard or eCharacterClass.Sorcerer or eCharacterClass.Runemaster or eCharacterClass.Eldritch or
            eCharacterClass.Mentalist => PlayerBotRole.Caster,
        eCharacterClass.Scout or eCharacterClass.Ranger => PlayerBotRole.Archer,
        _ => PlayerBotRole.Melee,
    };

    /// <summary>Where a rest stops power (#167 F3): CasterPowerStop for a caster, whose fights cost little of it (90, C10:
    /// under 50% a list caster regenerates at half rate, so the last 10% costs less than a pull that ends under 50%); full
    /// for every other role (a pet class's fights cost more: a 75% stop raised pet deaths per 100 kills 16% in rest-work's
    /// model; the rest until measured).</summary>
    public static byte RestPowerStop(PlayerBotRole role) => role == PlayerBotRole.Caster ? CasterPowerStop : (byte)100;

    /// <summary>The rest's end (APP:389-390, #167 F3): full health, the power stop (only for a class with a pool, as
    /// NeedsRest reads it) and the endurance stop.</summary>
    public static bool Recovered(byte healthPercent, int maxMana, byte manaPercent, byte endurancePercent, byte powerStop,
        byte enduranceStop) =>
        healthPercent >= 100 && (maxMana <= 0 || manaPercent >= powerStop) && endurancePercent >= enduranceStop;

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

    /// <summary>DEATH again= (#167 F4): a death with no kill since the bot's last release (releasedAt, 0 with none this
    /// login), the measure of the loop F4 ends.</summary>
    public static bool DiedAgain(long releasedAt, int lifeKills) => releasedAt != 0 && lifeKills == 0;

    /// <summary>DEATH sinceRelease= (#167 F4): the seconds since the last release, -1 with none this login.</summary>
    public static long SecondsSinceRelease(long releasedAt, long now) => releasedAt == 0 ? -1 : (now - releasedAt) / 1000;

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

    /// <summary>Low, or sitting and not yet recovered (APP:290-291 without the combat check), or after a kill until
    /// recovered (recoverBeforePull; Recovered: full health, the role's power stop, 60% endurance, #167 F3): the driver
    /// rests when MayRest says so, and a task that walks lets it.</summary>
    public static bool WantsRest(GamePlayer player, bool recoverBeforePull = false) =>
        WantsRest(NeedsRest(player.HealthPercent, player.MaxMana, player.ManaPercent, player.EndurancePercent),
            player.IsSitting, recoverBeforePull, Recovered(player));

    /// <summary>After a kill the bot rests before it picks the next target, as a GameBot does after a camp fight:
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

    /// <summary>Where the bot stands to fight its target (#167 C5): stop units short of the target toward the bot (Engage
    /// stops there, CombatStopDistance), or where it is when it is that near already.</summary>
    public static Vector3 StandPoint(Vector3 bot, Vector3 target, int stop)
    {
        float distance = Vector3.Distance(bot, target);
        return distance <= stop ? bot : target + (bot - target) * (stop / distance);
    }

    /// <summary>Crowding over the bot's whole pull (#167 C5): each potential add once when its reach (aggro range +
    /// AddMargin) covers the candidate at `at`, or the walk in from the bot (from) to its stand point (stand,
    /// StandPoint). In st-deathsB 82 defence fights began 2 s or more after a rest that ended recovered: the bot was
    /// jumped on its way in (59% with adds, 17 fatal); and 16 pulls had an add's ring over the stand spot but not over the
    /// target, all Crowding looks at. The candidate itself is no add.</summary>
    public static int CrowdingAlong<T>(T candidate, Vector3 at, Vector3 from, Vector3 stand,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class
    {
        int crowd = 0;
        for (int i = 0; i < adds.Count; i++)                                            // no enumerator per candidate
        {
            (T npc, Vector3 position, int aggroRange) = adds[i];
            float reach = aggroRange + AddMargin;
            if (!ReferenceEquals(npc, candidate) && (Vector3.DistanceSquared(position, at) <= reach * reach ||
                    SegmentDistanceSquared(position, from, stand) <= reach * reach))
                crowd++;
        }
        return crowd;
    }

    // The squared distance from a point to the segment a-b (a point when they meet), in 3D as InAddReach's.
    private static float SegmentDistanceSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(point - a, ab) / length, 0, 1);
        return Vector3.DistanceSquared(point, a + ab * t);
    }

    /// <summary>The adds rule over a ring's candidates within the con cap: the best score (TargetScore) among those with no
    /// potential add near them or on the walk in from where the bot stands (from) to its stand point at its stop
    /// (CrowdingAlong, #167 C5); with none such, the least crowded, then the best score; null with uncrowdedOnly, so the
    /// wider ring decides. The first of equals wins, as before.</summary>
    public static T PickUncrowded<T>(IReadOnlyList<(T Npc, Vector3 At, int Score)> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, bool uncrowdedOnly, Vector3 from, int stop) where T : class =>
        PickUncrowded(candidates, adds, uncrowdedOnly, from, stop, out _);

    /// <summary>PickUncrowded, and the pick's crowding as it read it (FIGHT crowd=, #167): -1 when it took none.</summary>
    public static T PickUncrowded<T>(IReadOnlyList<(T Npc, Vector3 At, int Score)> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, bool uncrowdedOnly, Vector3 from, int stop, out int crowd)
        where T : class
    {
        T best = null;
        int bestCrowd = int.MaxValue, bestScore = int.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            (T npc, Vector3 at, int score) = candidates[i];
            if (bestCrowd == 0 && score >= bestScore) continue;                         // nothing beats a clear better one
            int count = CrowdingAlong(npc, at, from, StandPoint(from, at, stop), adds);
            if (uncrowdedOnly && count > 0) continue;
            if (count < bestCrowd || count == bestCrowd && score < bestScore) (best, bestCrowd, bestScore) = (npc, count, score);
        }
        crowd = best == null ? -1 : bestCrowd;
        return best;
    }

    /// <summary>FIGHT and DEATH addAt= and addHp= (#167, the next leg's: when the second-monster case starts): the first
    /// think in a fight a second monster, any but the target, alive and on the bot (onBot: IsAttacking), is on the bot's
    /// attacker list or comes at it (coming: the add scan's, ScanComing), as the whole seconds since the fight's first
    /// think and the bot's health then; kept after (seen), and (-1, -1) until then. The review of 9aacd69: the last
    /// fight's dead target stays in the attacker list until its attack interval runs out (AttackerTracker.cs:209-219),
    /// and a lone next monster engaged within it read addAt=0.</summary>
    public static (int At, int Hp) FirstAdd<T>((int At, int Hp) seen, T target, IEnumerable<T> attackers, IEnumerable<T> coming,
        Func<T, bool> onBot, long fightMilliseconds, byte health) where T : class
    {
        if (seen.At >= 0) return seen;
        foreach (T monster in attackers.Concat(coming))
            if (monster != target && onBot(monster)) return ((int)(fightMilliseconds / 1000), health);
        return seen;
    }

    /// <summary>Whether Engage scans for monsters coming at the bot for FIGHT addAt= (#167; the review of 9aacd69: every
    /// role scanned 2,000 units every 2 s for the whole of an add-free fight, for a log field): only a bot whose add step
    /// scans anyway (Add), one with a control (controls) or a Theurgist's pets (combatPets); the others read their
    /// attacker list alone.</summary>
    public static bool ScansComing(int controls, int combatPets) => controls > 0 || combatPets > 0;

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

    /// <summary>The damage spells a bot casts (#167 F9): APP:667-692's filter without the quickbar, the Animist policy and
    /// the range filter (checked at cast time), less the ranks a GameBot never casts (BotBrain.IsOutclassedDamageSpell,
    /// BotBrain.cs:3413): a lower rank of a kind it knows higher (Fungal Pin once it has Fungal Thistle), or one under half
    /// the level of its best. payload resolves a bomber's payload (NoDamageBomber); it has no default (the review of
    /// 451121c: a caller that left it out brought back the root bomber as a nuke, and no test failed).</summary>
    public static List<(Spell Spell, SpellLine Line)> DamageSpells(IEnumerable<(Spell Spell, SpellLine Line)> usable, int level,
        Func<int, Spell> payload)
    {
        List<(Spell Spell, SpellLine Line)> all = usable.Where(pair => !NoDamageBomber(pair.Spell, payload)).ToList(), nukes = new();
        List<Spell> known = all.ConvertAll(pair => pair.Spell);
        foreach ((Spell spell, SpellLine line) in all)
            if (spell.IsHarmful && spell.Damage > 0 && spell.Range > 0 && !spell.NeedInstrument && spell.Level <= level &&
                !BotBrain.IsOutclassedDamageSpell(spell, known, level))
                nukes.Add((spell, line));
        return nukes;
    }

    // #167 F10b: an Animist's bomber does only its payload (BomberBrain.cs:32-34, its SubSpellID); the bomber's own
    // Damage (50 on every one) is no damage. One whose payload does none (Spreading Vines: a root) is no nuke, nor does it
    // outrank a damage bomber (BotBrain.IsOutclassedDamageSpell compares spell types). Production resolves payloads with
    // SkillBase.GetSpellByID; a payload it cannot resolve is none.
    private static bool NoDamageBomber(Spell spell, Func<int, Spell> payload) =>
        spell.SpellType is eSpellType.Bomber && !(payload?.Invoke(spell.SubSpellID) is Spell sub && sub.Damage > 0);

    /// <summary>Where an Animist stands to pull with its main turret planted (#167 F10b): a turret is planted at the bot's
    /// feet (SummonPet) and reaches turretReach (its TurretSpell, 1,000; 1,350 for a Verdant taunt), and the monster the
    /// bot pulls comes to the bot. So by its turret (here, within AnimistStandMargin of it) while its spell reaches the
    /// target from there; else at its spell's reach (spellStop) toward the turret, if the turret reaches that spot (less
    /// the margin); else it plants a new one (Replant) by the target, within the turret's reach of it less the margin and
    /// its spell's, here if it is that near already. The old approach walked it to its spell's reach from wherever it
    /// was, 1,420 units from the target with most nukes, out of its turret's reach.</summary>
    public static (Vector3 Stand, bool Replant) AnimistStand(Vector3 bot, Vector3 turret, Vector3 target, int spellStop,
        int turretReach)
    {
        float toTurret = Vector3.Distance(target, turret);
        if (toTurret <= spellStop)
            return (Vector3.Distance(bot, turret) <= AnimistStandMargin ? bot : turret, false);
        Vector3 near = target + (turret - target) * (spellStop / toTurret);
        if (Vector3.Distance(near, turret) <= turretReach - AnimistStandMargin) return (near, false);
        float toBot = Vector3.Distance(target, bot), standOff = Math.Min(spellStop, turretReach - AnimistStandMargin);
        return (toBot <= standOff ? bot : target + (bot - target) * (standOff / toBot), true);
    }

    /// <summary>The nuke for this cast (#167 F9), chosen at each cast choice: the bolt when it is ready (a 20-s recast:
    /// a Shaman's Fungal Thistle, a Wizard's Flame Streak), else the best ready single-target spell (Better), else an area
    /// one, but never one while an add stands in its radius (it would pull it). Ready: off its recast and paid for. None
    /// ready: none, and the bot swings until one is (the old choice kept the best spell, and its cast failed on the
    /// recast).</summary>
    public static (Spell, SpellLine) ChooseNuke(IEnumerable<(Spell Spell, SpellLine Line)> spells, Func<Spell, bool> ready,
        bool addInRadius)
    {
        Spell best = null;
        SpellLine bestLine = null;
        int bestRank = int.MaxValue;
        foreach ((Spell spell, SpellLine line) in spells)
        {
            bool area = spell.Radius > 0;
            if (area && addInRadius || !ready(spell)) continue;
            int rank = spell.SpellType is eSpellType.Bolt ? 0 : area ? 2 : 1;
            if (best == null || rank < bestRank || rank == bestRank && Better(spell, best))
                (best, bestLine, bestRank) = (spell, line, rank);
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

    // PLAYER_BOT_TRAIN's spell=: the nuke it opens a fight with, everything ready and no add.
    public static string BestDamageSpellName(GamePlayer player) =>
        ChooseNuke(DamageSpells(UsableSpells(player), player.Level, SkillBase.GetSpellByID), _ => true, false).Item1 is Spell s
            ? FormattableString.Invariant($"{s.Name.Replace(' ', '_')}:{s.Level}") : "-";

    // #167 F9: the nuke at a cast choice, from the damage spells cached per level. An add in an area spell's radius is
    // looked for only when an area spell would be chosen: any other monster (IsExperienceTarget) within it of the target,
    // but one already on the bot.
    private static (Spell, SpellLine) Nuke(PlayerBotState state, GamePlayer player, GameLiving target)
    {
        KnowSpells(state, player);
        bool Ready(Spell spell) => player.GetSkillDisabledDuration(spell) <= 0 && HasPowerFor(player.MaxMana, player.Mana, spell.Power);
        (Spell spell, SpellLine line) = ChooseNuke(state.Nukes, Ready, false);
        if (spell == null || spell.Radius <= 0 || target == null) return (spell, line);
        foreach (GameNPC npc in target.GetNPCsInRadius((ushort)spell.Radius))
            if (npc != target && npc.IsAlive && IsExperienceTarget(npc) && !(npc.IsAttacking && npc.TargetObject == player))
                return ChooseNuke(state.Nukes, Ready, true);
        return (spell, line);
    }

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
    // After a death (#167 F4, C1b) it plans before it walks back, from where it died, in its anchor's region and
    // the zone it died in (the bind may lie elsewhere; then the corridors start at the death spot, CorridorOrigin), the
    // failed camps barred; with no camp it hunts where it is. Not while the release's immunity lasts (RelocateReason,
    // #167 C1): it returns before NextRelocateTick, so the next think tries again, and the replan's hold (ReplanSince,
    // HoldsForRelocation) keeps the bot out of the camp meanwhile. The immunity hides monsters from a plan only: a bot
    // that cannot plan (CanRelocate) falls back as before, and a lever spot bot reads no player here.
    private static void RelocateIfOutgrown(PlayerBotState state, GamePlayer player, long now)
    {
        string reason = RelocateReason(state.RoamingSince, state.CappedSince, now, state.ReplanOwed,
            CanRelocate(state, player) && player.IsInvulnerableToAttack);       // GamePlayer.cs:12284-12287
        if (reason == null || now < state.NextRelocateTick) return;
        bool died = reason == Died;
        if (!CanRelocate(state, player) ||                                         // the lever test keeps its bots on
            (died ? WorldMgr.GetRegion(state.AnchorRegion)?.GetZone((int)state.DeathSpot.X, (int)state.DeathSpot.Y)
                : player.CurrentZone) is not Zone zone)                            //   their spots
        {
            FallBack(state, player, reason);
            return;
        }
        state.NextRelocateTick = now + (reason == Outgrown ? RelocateAfterMilliseconds : CappedRelocateMilliseconds);
        Vector3 here = new(player.X, player.Y, player.Z), from = RelocationOrigin(reason, state.DeathSpot, here);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (nav.HasNavmesh(player.CurrentZone) && nav.GetClosestPoint(player.CurrentZone, here, 64, 64, 128, nav.DefaultFilters) == null)
        {
            FallBack(state, player, reason);  // off the mesh here, every corridor fails (PlayerTravelPath.cs:48-50): blame
            return;                           //   no monster for it
        }
        Vector3? corridorFrom = CorridorOrigin(reason, nav.HasNavmesh(zone), player.CurrentZone == zone, state.DeathSpot, here);
        if (corridorFrom is Vector3 start && player.CurrentZone != zone &&       // a death spot off the mesh fails every
            nav.GetClosestPoint(zone, start, 64, 64, 128, nav.DefaultFilters) == null)  // corridor (PTP:80-82): no test
            corridorFrom = null;                                                 //   then, as before
        ConColor natural = MaximumTargetCon(1, 0);
        List<(GameNPC Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters = new();
        foreach (GameNPC npc in WorldMgr.GetNPCsFromRegion(state.AnchorRegion))   // CanRelocate: the bot's region too
        {
            if (npc.CurrentZone != zone || !IsExperienceTarget(npc) || !IsAttackable(player, npc)) continue;
            ConColor con = ConLevels.GetConColor(player.GetConLevel(npc));
            if (con > ConColor.GREY)
                monsters.Add((npc, new(npc.X, npc.Y, npc.Z), con,
                    con <= natural && AutonomousPlayerPilot.IsEligibleExperienceTarget(con, npc.EffectiveLevel, player.Level, 1),
                    IsAvoided(state.AvoidUntil, npc.Name, now)));
        }
        (GameNPC[] nearest, ConColor? fallback, bool stale) = PlanRelocation(monsters, from,
            MaximumTargetCon(1, state.DeathSafetySteps), natural, FailedCamps(state.FailedCamps, state.AnchorRegion, now),
            state.RelocateRejected, RelocateCandidates);
        foreach (GameNPC npc in nearest)
        {
            Vector3 spot = new(npc.X, npc.Y, npc.Z);
            if (corridorFrom is Vector3 origin && !PlayerTravelPath.TryBuildCorridor(nav, zone, origin, spot, out _))
            {
                state.RelocateRejected.Add(npc);
                continue;
            }
            if (Log.IsInfoEnabled)
                Log.Info(RelocateLine(player.Name, player.Level, state.Anchor, spot, (int)Vector3.Distance(here, spot), npc.Name,
                    npc.EffectiveLevel, zone.ID, reason, fallback, now - state.ReleasedAt));
            state.Anchor = spot;
            state.RoamDestination = spot;   // walk there: a task bot levelling for its errand has no leash to pull it
            state.RelocateRejected.Clear();
            state.FallbackCap = fallback;   // that camp's con, or none: the death cap (Cap)
            state.HuntAvoided = stale;      //   and at a camp of avoided names, those too (FindWithin)
            state.CappedSince = 0;          // the next search starts the clock afresh, on the way there too
            state.BackMoved |= died;        //   and BACK replan=moved (#167 C1)
            state.ReplanOwed = null;        // planned (#167 F4, C2)
            state.ReplanSince = 0;
            return;
        }
        if (Log.IsInfoEnabled)
            Log.Info(RelocateNoneLine(player.Name, player.Level, nearest.Length, zone.ID, reason, now - state.ReleasedAt));
        if (nearest.Length == 0) FallBack(state, player, reason);   // no camp in the zone: none to try next time either
    }

    // A capped bot's FallBackInPlace; after a death (#167 F4) or a break-off (C2), the end of the relocation owed.
    private static void FallBack(PlayerBotState state, GamePlayer player, string reason)
    {
        if (reason is Died or BrokeOff) EndReplan(state, player);
        else FallBackInPlace(state, reason);
    }

    // #167 F4: no camp planned after a death, at once or within MaxHoldMilliseconds: the bot hunts where it is, under its
    // lowered cap and its avoided names; in its anchor's region the leash moves here, so it doesn't walk back (after a
    // break-off, #167 C2, off the camp it ran from).
    private static void EndReplan(PlayerBotState state, GamePlayer player)
    {
        state.ReplanOwed = null;
        state.ReplanSince = 0;
        if (player.CurrentRegionID != state.AnchorRegion || state.Task != null) return;
        state.Anchor = new(player.X, player.Y, player.Z);
        state.RoamDestination = null;
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

    // #167 F4: a release starts the recovery (AfterReleaseStep): the back-off from now (the GameBot's, from its replan
    // after the release, AWBC:3778-3786), and the hold for the relocation owed. #167 C6: a login only its step out (no
    // backOff): no release time (DEATH again= and sinceRelease= stay the release's), no back-off, no replan owed.
    private static void StartRecovery(PlayerBotState state, long now, bool backOff)
    {
        if (backOff)
        {
            state.ReleasedAt = now;
            state.RecoverUntil = now + AutonomousDeathRecoveryPolicy.RetryDelayMilliseconds(state.NoXpDeaths);
            state.ReplanSince = state.ReplanOwed != null ? now : 0;
            (state.BackPending, state.BackOwed, state.BackMoved) = (true, state.ReplanOwed != null, false);   // C1: BACK
            state.BackStepOutMs = state.BackHoldMs = state.BackWalkMs = 0;
        }
        else state.RecoverUntil = now;
        state.AfterRelease = true;
        state.ClearSpot = null;
        state.NextReachTick = state.StepOutBlockedUntil = 0;
    }

    // #167 F4: the back-off or the resurrection illness: no pull, out of reach (step 15 holds the bot).
    private static bool Recovering(PlayerBotState state, GamePlayer player, long now) =>
        state.AfterRelease && (now < state.RecoverUntil || Ill(player));

    // The resurrection illness (OnRevive's spell 2435 for a PvE or PvP death, 8181 for RvR, GamePlayer.cs:1823-1858;
    // EffectHelper.cs:242-245). A PvE death gives it only from pve_exp_loss_level (m_deathtype, GamePlayer.cs:6018-6035).
    private static bool Ill(GamePlayer player) =>
        player.effectListComponent.ContainsEffectForEffectType(eEffect.ResurrectionIllness) ||
        player.effectListComponent.ContainsEffectForEffectType(eEffect.RvrResurrectionIllness);

    // #167 F4: the recovery's step out of reach (AfterReleaseStep), after a release and at login (C6): its own ring
    // test every 2 s, over the monsters round it that would aggro (AddReach), and a walk to the nearest clear spot on
    // the navmesh, unless it sits (a rest after it fought back). A blocked walk stands it for StepOutPauseMilliseconds.
    // Its other steps run in Think: the relocation owed and the holds at step 15, no search at step 14 while the
    // recovery lasts; when it is over, the idle clock starts afresh. True while it walks.
    private static bool StepsOutOfReach(PlayerBotState state, GamePlayer player, long now)
    {
        if (!state.AfterRelease) return false;
        if (now >= state.NextReachTick)
        {
            state.NextReachTick = now + TargetRefreshMilliseconds;
            state.ClearSpot = now < state.StepOutBlockedUntil ? null : ClearSpotNear(player, StepOutRings);
        }
        switch (AfterReleaseStep(Threat(state, player, now) != null,                    // #167 F7: coming at it too
                    state.ClearSpot != null && !player.IsSitting, state.ReplanOwed != null, now < state.RecoverUntil, Ill(player)))
        {
            case PlayerBotRecovery.Fight:
                state.ClearSpot = null;                                                 // it fights back (step 14)
                return false;
            case PlayerBotRecovery.StepOut:
                state.Activity = PlayerBotActivity.Roaming;
                state.BackStepOutMs += TickMilliseconds;                                // #167 C1: BACK stepOut=
                PlayerBotStepResult step = state.Mover.StepToward(state.ClearSpot.Value, 20, now);
                if (step is PlayerBotStepResult.Blocked)
                {
                    ReportBlocked(state, player, "clear", state.ClearSpot.Value, now);
                    state.StepOutBlockedUntil = now + StepOutPauseMilliseconds;
                }
                if (step is PlayerBotStepResult.Blocked or PlayerBotStepResult.Arrived) state.ClearSpot = null;
                return true;
            case PlayerBotRecovery.Done:
                state.AfterRelease = false;
                state.RoamingSince = 0;
                return false;
            default:
                return false;                                                           // Replan, Wait: step 15
        }
    }

    // The spot out of every potential add's reach nearest the bot (SpotOutOfReach), or null: the monsters within the
    // rings' reach (AddScanMargin past the outer one) that would aggro.
    private static Vector3? ClearSpotNear(GamePlayer player, int[] rings)
    {
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)(rings[^1] + AddScanMargin)))
            if (IsExperienceTarget(npc) && AddReach(player, npc) is > 0 and int reach)
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        Vector3? found = SpotOutOfReach(player, rings, adds);
        adds.Clear();                                                                   // no monster held past the scan
        return found;
    }

    // RestSpot over the ring points on the navmesh (each one's nearest mesh point; with no navmesh, the points), or null
    // when the bot stands clear or no point is.
    private static Vector3? SpotOutOfReach(GamePlayer player, int[] rings, List<(GameNPC Npc, Vector3 At, int AggroRange)> adds)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        if (!InAddReach(here, adds)) return null;
        IEnumerable<Vector3> points = RingPoints(here, rings, SpotDirections)
            .Select(p => OnMesh(player, p)).Where(p => p.HasValue).Select(p => p.Value);
        Vector3 spot = RestSpot(here, points, adds);
        return spot == here ? null : spot;
    }

    // A point's nearest navmesh point, or none; with no navmesh in the zone, the point.
    private static Vector3? OnMesh(GamePlayer player, Vector3 point)
    {
        IPathfindingMgr nav = PathfindingProvider.Instance;
        Zone zone = player.CurrentZone;
        return zone != null && nav.HasNavmesh(zone) ? nav.GetClosestPoint(zone, point, 64, 64, 256, nav.DefaultFilters) : point;
    }

    // #167 F7: before it sits, a bot in a potential add's reach walks to the nearest clear spot on the rest rings, once
    // per rest; the adds are the last search's (FindWithin), where they are now, that would still aggro: no new scan;
    // and the spawn spots of its kills of the last 150 s (RespawnSpots, #167 C7). A blocked walk, or none clear, and it
    // sits where it is. True while it walks.
    private static bool WalksToRestSpot(PlayerBotState state, GamePlayer player, long now)
    {
        if (!state.RestSpotChecked)
        {
            state.RestSpotChecked = true;
            List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
            adds.Clear();
            foreach (GameNPC npc in state.SearchAdds)
                if (AddReach(player, npc) is > 0 and int reach)
                    adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
            int searchAdds = adds.Count;
            foreach ((Vector3 at, int reach) in RespawnSpots(state.RecentKills, now))    // #167 C7: and its kills'
                adds.Add((null, at, reach));                                             //   respawns (REST respawn=)
            state.RestRespawns = adds.Count - searchAdds;
            state.RestGoal = SpotOutOfReach(player, RestSpotRings, adds);
            adds.Clear();
        }
        if (state.RestGoal is not Vector3 goal) return false;
        state.Activity = PlayerBotActivity.Resting;
        PlayerBotStepResult step = state.Mover.StepToward(goal, 20, now);
        if (step is PlayerBotStepResult.Blocked) ReportBlocked(state, player, "rest", goal, now);
        if (step is not (PlayerBotStepResult.Blocked or PlayerBotStepResult.Arrived)) return true;
        state.RestGoal = null;
        return false;
    }

    // #167 C2: losing to two (BreaksOff), the bot stops its fight, bars the camp and the names (BrokeOffFrom), sprints
    // and runs to the break-off point (BreakOffPoint), then on from there while one still chases it (BreakOffStep), at
    // most BreakOffMilliseconds; EndBreakOff ends it. A blocked step bars its point and takes the next at once; with none
    // left it turns on them (the review of 9aacd69: the same point was chosen again every think). Before the self-heal
    // (11b): its cast stops the mover (CastOnSelf). True while it runs.
    private static bool BreaksOffNow(PlayerBotState state, GamePlayer player, long now)
    {
        if (state.BreakOffSince == 0 && !StartsBreakOff(state, player, now)) return false;
        KeepChasers(state, player);
        PlayerBotBreakOff next = BreakOffStep(state.BreakOffArrived, state.BreakOffChasers.Count, now - state.BreakOffSince);
        if (next is PlayerBotBreakOff.NextPoint)
        {
            state.BreakOffGoal = BreakOffGoal(state, player);
            state.BreakOffArrived = false;
            if (state.BreakOffGoal == null) next = PlayerBotBreakOff.Fight;             // nowhere further: it turns
        }
        if (next is PlayerBotBreakOff.Done or PlayerBotBreakOff.Fight)
        {
            EndBreakOff(state, player, now, next is PlayerBotBreakOff.Done ? Shook : Fought);
            return false;                                                               // Fight: step 14 picks a hitter
        }
        state.Activity = PlayerBotActivity.Fighting;
        Vector3 goal = state.BreakOffGoal.Value;
        PlayerBotStepResult step = state.Mover.StepToward(goal, 20, now);
        if (step is PlayerBotStepResult.Arrived) state.BreakOffArrived = true;
        else if (step is PlayerBotStepResult.Blocked)
        {
            ReportBlocked(state, player, "breakoff", goal, now);
            state.BreakOffBlocked.Add(goal);                                            // never chosen again (NextBreakOffPoint)
            if ((state.BreakOffGoal = BreakOffGoal(state, player)) == null)
            {
                EndBreakOff(state, player, now, Fought);                                 // none left: it turns at once
                return false;
            }
        }
        return true;
    }

    // A break-off's start (BreakOffFires) from the monsters hitting the bot (Attacker's, each with the bot as its target)
    // and, only once that holds, the speed of every chaser (the hitters and those within IncomingRadius coming at it)
    // against the sprint its endurance pays for now (SprintSeconds, Outruns). A mezzed or stunned monster is on it no more
    // (OnBot). C4's add control comes first (AddControlReady, read only once the rest holds). No point to run to: it
    // fights on. A scan that starts none waits TargetRefreshMilliseconds, as Incoming's.
    private static bool StartsBreakOff(PlayerBotState state, GamePlayer player, long now)
    {
        byte health = player.HealthPercent;
        if (health >= BreakOffHealthPercent || now < state.NextBreakOffTick) return false;     // no scan above it
        List<GameNPC> chasers = state.BreakOffChasers;
        List<(byte Health, int Distance)> hitters = _hitters ??= new();
        chasers.Clear();
        hitters.Clear();
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
        {
            if (living is not GameNPC npc || !OnBot(IsAttacking(player, npc), npc.IsCrowdControlled) || chasers.Contains(npc))
                continue;
            chasers.Add(npc);
            hitters.Add((npc.HealthPercent, player.GetDistanceTo(npc)));
        }
        int onBot = chasers.Count;
        bool fires = BreakOffFires(state, health, hitters, now, () => AddControlReady(state, player, now));
        hitters.Clear();
        if (!fires)
        {
            chasers.Clear();
            return false;
        }
        foreach (GameNPC npc in player.GetNPCsInRadius(IncomingRadius))
            if (OnBot(npc.TargetObject == player && IsAttackable(player, npc), npc.IsCrowdControlled) && IsExperienceTarget(npc) &&
                !chasers.Contains(npc))
                chasers.Add(npc);
        int fastest = 0;
        foreach (GameNPC npc in chasers) fastest = Math.Max(fastest, npc.MaxSpeed);
        int sprint = SprintSeconds(player.Endurance, player.GetModified(eProperty.EnduranceRegenerationAmount),
            player.GetModified(eProperty.FatigueConsumption), LongWindCost(player));
        state.BreakOffBlocked.Clear();
        if (!Outruns(fastest, sprint) || BreakOffGoal(state, player) is not Vector3 goal)
        {
            chasers.Clear();
            state.NextBreakOffTick = now + TargetRefreshMilliseconds;
            return false;
        }
        (state.BreakOffSince, state.BreakOffGoal, state.BreakOffArrived) = (now, goal, false);
        (state.BreakOffHealth, state.BreakOffEndurance, state.BreakOffAttackers) = (health, player.EndurancePercent, onBot);
        state.KilledWithAdds = false;
        BrokeOffFrom(state, player.CurrentRegionID, new(player.X, player.Y, player.Z),
            chasers.Select(npc => npc.Name).Append(state.Target?.Name), now);
        if (state.Engaged && state.Target != null) ReportFight(state, player, now, false, true);   // FIGHT result=dropped breakOff=1
        Disengage(state, player);
        if (player.Endurance > SprintStartEndurance) player.Sprint(true);              // GamePlayer.cs:8625-8652
        return true;
    }

    // A sprint's cost a second before the regeneration (GamePlayer.cs:2396-2405): LongWind, less Long Wind's 20-100%.
    private static int LongWindCost(GamePlayer player) =>
        player.GetAbility<AtlasOF_LongWindAbility>() is { } longWind
            ? LongWind - longWind.GetAmountForLevel(player.CalculateSkillLevel(longWind)) * LongWind / 100 : LongWind;

    // The monsters still after a break-off's bot: one goes when it dies or turns from the bot (its aggro state's exit
    // clears its target 25 s after its last swing, StandardMobState.cs:89, :121), is mezzed or stunned (OnBot, #167 C4)
    // or is past ChaseRadius; one that hits the bot on the way joins them.
    private static void KeepChasers(PlayerBotState state, GamePlayer player)
    {
        List<GameNPC> chasers = state.BreakOffChasers;
        for (int i = chasers.Count - 1; i >= 0; i--)
            if (!OnBot(IsAttacking(player, chasers[i]), chasers[i].IsCrowdControlled) ||
                !player.IsWithinRadius(chasers[i], ChaseRadius))
                chasers.RemoveAt(i);
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
            if (living is GameNPC npc && OnBot(IsAttacking(player, npc), npc.IsCrowdControlled) && !chasers.Contains(npc))
                chasers.Add(npc);
    }

    // The next break-off point (NextBreakOffPoint) from where the bot is: the ring points (BreakOffRings, SpotDirections
    // round it) on the navmesh, away from its chasers, out of the reach of every other monster round it that would
    // aggro (AddReach; not one with the bot as its target: it is on it already), not one it was blocked on, and one it
    // has a corridor to (PlayerTravelPath.TryBuildCorridor, as RelocateIfOutgrown's; with no navmesh, any); none, null.
    private static Vector3? BreakOffGoal(PlayerBotState state, GamePlayer player)
    {
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)(BreakOffRings[^1] + AddScanMargin)))
            if (npc.TargetObject != player && IsExperienceTarget(npc) && AddReach(player, npc) is > 0 and int reach)
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        Vector3 here = new(player.X, player.Y, player.Z);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        Zone zone = player.CurrentZone;
        bool meshed = zone != null && nav.HasNavmesh(zone);
        Vector3? goal = NextBreakOffPoint(here, state.BreakOffChasers.Select(npc => new Vector3(npc.X, npc.Y, npc.Z)).ToList(),
            RingPoints(here, BreakOffRings, SpotDirections).Select(p => OnMesh(player, p)).Where(p => p.HasValue)
                .Select(p => p.Value).ToList(), adds, state.BreakOffBlocked,
            point => !meshed || PlayerTravelPath.TryBuildCorridor(nav, zone, here, point, out _));
        adds.Clear();                                                                   // no monster held past the scan
        return goal;
    }

    // A break-off's end: its line (PLAYER_BOT_BREAKOFF), the state's end (FinishBreakOff: the relocation owed, OwesReplan's
    // rule as after a death; fought, no new break-off for BreakOffMilliseconds), and unless the bot died (the death's own
    // recovery then, HandleDeath) the sprint off.
    private static void EndBreakOff(PlayerBotState state, GamePlayer player, long now, string outcome)
    {
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatBreakOff(player.Name, player.Level, state.BreakOffHealth,
                state.BreakOffEndurance, state.BreakOffAttackers, (now - state.BreakOffSince) / 1000, outcome));
        FinishBreakOff(state, outcome, OwesReplan(state.Task != null, CanRelocate(state, player)), now);
        if (outcome == Died) return;
        player.Sprint(false);
        state.Mover.Stop(now);
    }

    // APP:561-583 without SaveIntoDatabase.
    private static void HandleDeath(PlayerBotState state, GamePlayer player, long now)
    {
        EndRest(state, player, now, "died");
        PlayerBotActivity last = state.Activity;                                       // what it was doing when killed
        state.Activity = PlayerBotActivity.Dead;
        if (!state.DeathRecorded)
        {
            ReportDeath(state, player, now, last);
            if (state.BreakOffSince != 0) EndBreakOff(state, player, now, Died);       // #167 C2: BREAKOFF outcome=died
            state.KilledWithAdds = false;
            if (state.Engaged && state.Target != null)
                Remember(state.AvoidUntil, state.Target.Name, now);                    // DEATHS.md A3: died fighting it
            state.DeathRecorded = true;
            state.Deaths++;
            state.DeathSafetySteps = NextDeathSteps(state.DeathSafetySteps);
            state.FallbackCap = null;                                                   // a GameBot's fallback is its camp's
            state.HuntAvoided = false;                                                  //   only, and it fails here (AWBC:3801,
            state.DeathSpot = new(player.X, player.Y, player.Z);                        //   :3803-3804)
            RememberFailedCamp(state.FailedCamps, player.CurrentRegionID, state.DeathSpot, now);
            state.NoXpDeaths = AutonomousDeathRecoveryPolicy.NextNoExperienceDeathStreak(state.NoXpDeaths,   // #167 F4:
                state.LastDeathTick, now, state.Gained > state.GainedAtDeath);          //   the back-off (AWBC:3778-3786)
            (state.LastDeathTick, state.GainedAtDeath) = (now, state.Gained);
            state.ReplanOwed = OwesReplan(state.Task != null, CanRelocate(state, player)) ? Died : null;   // wherever (C1b)
            state.NextRelocateTick = 0;                                                 //   and the plan before the walk back
            state.AfterRelease = false;
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
            AvoidedCount(state.AvoidUntil, now), DiedAgain(state.ReleasedAt, state.LifeKills),     // this death's come after
            SecondsSinceRelease(state.ReleasedAt, now), inFight && state.EngageIll,        // #167 F4
            inFight ? state.FightControls : -1, inFight ? state.FightKites : -1,          // #167 C3: the fatal fight's
            inFight ? state.FightCasts : -1, inFight ? (state.FightQuickcast ? 1 : 0) : -1, //   caster fields, as FIGHT's
            inFight ? state.FightWaits : -1, inFight ? state.FightCtlBroken : -1,
            inFight ? state.FightAddControls : -1,                                        // #167 C4
            inFight ? state.FightAdd.At : -1, inFight ? state.FightAdd.Hp : -1,           // #167, the next leg's: as FIGHT's,
            state.BreakOffSince != 0, inFight ? state.EngageDistance : -1);               //   and on a break-off's run
        string head = $"PLAYER_BOT_DEATH bot={player.Name} level={player.Level} at=({player.X},{player.Y},{player.Z}) " +
                      $"zone={player.CurrentZone?.ID} {fight} attackers={state.Attackers} deathSteps={state.DeathSafetySteps} " +
                      $"aliveSec={(now - state.AliveSince) / 1000} task={state.Task != null}";
        if (AwaitsDying(state.Killer, player.IsBeingHandledByReaperService))
            state.PendingDeath = (head, detail, target);                                   // the next think writes it
        else
            Log.Info(DeathLine(head, detail, state.Killer, target));
    }

    // #167 C1: PLAYER_BOT_BACK at the first fight in reach after a release (the verifier's logging: the 40 s more from a
    // death to the next fight in st-deathsB was not the failed replan, gap.py). Its phases are counted by think; the
    // rest of sec= is the rests, heals, buffs and the walk to the target.
    private static void ReportBack(PlayerBotState state, GamePlayer player, long now)
    {
        state.BackPending = false;
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatBack(player.Name, player.Level, (now - state.ReleasedAt) / 1000,
                state.BackStepOutMs / 1000, state.BackHoldMs / 1000, state.BackWalkMs / 1000,
                BackReplan(state.BackOwed, state.BackMoved)));
    }

    // One line per fight the bot was in when it settles (SettleTarget), so a fight's cost shows beside the deaths; or
    // a break-off drops it (StartsBreakOff, brokeOff).
    private static void ReportFight(PlayerBotState state, GamePlayer player, long now, bool killed, bool brokeOff)
    {
        if (!Log.IsInfoEnabled) return;
        GameLiving target = state.Target;
        Log.Info(PlayerBotDriverReport.FormatFight(new(player.Name, player.Level, target.Name, target.EffectiveLevel,
            ConLevels.GetConColor(player.GetConLevel(target)), state.FromDefence, killed, state.EngageHealth, player.HealthPercent,
            state.EngageMana, PowerPercent(player.MaxMana, player.ManaPercent), (now - state.EngagedSince) / 1000, state.FightAdds,
            state.Swings, state.NotVisible, state.FightCasts, state.LifeKills, state.LifeRests, state.EngagePet,
            state.FightHeals, state.FightQuickcast, state.EngageBuffs, state.FightPetCasts, state.FightWaits,
            state.FightControls, state.FightKites, state.FightCtlBroken, state.FightAddControls, state.FightAdd.At,
            state.FightAdd.Hp, brokeOff, state.EngageDistance, state.PickCrowd)));
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

    // Quickcast as a player uses it (CombatAction's Quickcast): attack mode off, which the ability needs
    // (QuickCastAbilityHandler.cs:65-69), then the use-skill packet's request (UseSkillHandler.cs:83-84), handled in the
    // Casting phase later in this tick (CastingComponent.cs:173-186, :485-496). A refused use is not asked for again
    // until QuickcastRetryMilliseconds.
    private static void UseQuickcast(PlayerBotState state, GamePlayer player, long now, Ability quickcast)
    {
        state.NextQuickcastTick = now + QuickcastRetryMilliseconds;
        player.attackComponent.StopAttack();
        player.castingComponent.RequestUseAbility(quickcast);
        state.Activity = PlayerBotActivity.Casting;
    }

    // #167 F10b: an Animist with its main turret planted pulls from by it (AnimistStand), before the pull (not engaged,
    // the target not on it, not a defence), at a spell's stop distance (spellStop). A new turret by the target: the old
    // one released (CommandNpcRelease, as a GameBot does, BotAnimistPolicy.cs:160-170) when the summon is ready and paid
    // for and the summon's gate (WantsPet) would pass, so SummonPet plants it at its feet at the next think, before the
    // pull; else the old approach. A blocked walk: the old approach for UnreachableSkipMilliseconds. True when it acted.
    private static bool AnimistApproach(PlayerBotState state, GamePlayer player, GameLiving target, int spellStop, long now)
    {
        if (player.CharacterClass.ID != (int)eCharacterClass.Animist || state.Engaged || state.FromDefence ||
            IsAttacking(player, target) || now < state.NextAnimistStandTick ||
            player.ControlledBrain?.Body is not TurretPet turret || !turret.IsAlive || turret.CurrentRegion != player.CurrentRegion)
            return false;
        int reach = turret.TurretSpell is Spell spell ? turret.castingComponent.CalculateSpellRange(spell) : 1_000;
        Vector3 here = new(player.X, player.Y, player.Z);
        (Vector3 stand, bool replant) = AnimistStand(here, new(turret.X, turret.Y, turret.Z), new(target.X, target.Y, target.Z),
            spellStop, reach);
        if (replant && !CanReplant(state, player, now)) return false;
        if (Vector3.Distance(here, stand) > AnimistStandMargin)
        {
            player.attackComponent.StopAttack();
            state.Activity = PlayerBotActivity.Hunting;
            if (state.Mover.StepToward(stand, AnimistStandMargin - 50, now) is not PlayerBotStepResult.Blocked) return true;
            ReportBlocked(state, player, "turret", stand, now);
            state.NextAnimistStandTick = now + UnreachableSkipMilliseconds;
            return false;
        }
        if (!replant) return false;
        state.Mover.Stop(now);
        player.CommandNpcRelease();                                                     // the target is no pet of its:
        state.NextSummonTick = 0;                                                       //   its main pet goes
        return true;
    }

    // The summon's gate with no pet (WantsPet), the summon ready and paid for: a released turret is planted again at once.
    private static bool CanReplant(PlayerBotState state, GamePlayer player, long now)
    {
        if (!WantsPet(false, Attacker(state, player, now, anyHitter: true) != null, player.InCombat, player.ManaPercent,
                player.IsCasting, now, 0))
            return false;
        (Spell summon, _) = FindPetSpell(UsableSpells(player), player.Level);
        return summon != null && player.GetSkillDisabledDuration(summon) <= 0 && HasPowerFor(player.MaxMana, player.Mana, summon.Power);
    }

    // #167 F11: a caster's control and step back against a monster (CasterAction), between its casts; Nuke leaves the
    // think to CombatAction. A control the target can take now (Takes, #167 C8: the server's own tests) and a mez only as
    // the escape (MezWanted), in range, ready and paid for, at a cast choice; MaxControlTries casts that held
    // nothing and it stops trying on this target until one holds. Control casts do no damage: no cast watch. The time to
    // contact is at the monster's base speed (a rooted one's speed is 0). The step back while the bot keeps its hold
    // (casterHold, CasterHolds: no other monster hitting it, whose case is the add's control or a break-off; the stop is
    // then the spell's), to a point within that stop (KitePoint); none, or one blocked a moment ago (KitePauseMilliseconds),
    // and CombatAction's old rule (#167 C3). No control while the nukes kill it first (#167 C11: KillsFirst, from what
    // the fight's casts took off it, NukesNeeded), the bot at KillFirstHealthPercent or more and one monster at most on
    // it. held and breaksOnHit: Engage's Hold of the target. True when it acted.
    private static bool CasterStep(PlayerBotState state, GamePlayer player, GameLiving target, int distance, int stop,
        bool held, bool breaksOnHit, bool casterHold, long now, bool due, bool interrupted, bool quickcastReady,
        bool quickcasting, Ability quickcast)
    {
        if (RoleOf(player.CharacterClass.ID) != PlayerBotRole.Caster || target is not GameNPC npc) return false;
        KnowSpells(state, player);
        if (held) state.ControlTries = 0;
        else if (state.Controls.Count == 0) return false;
        bool closing = npc.TargetObject == player;
        long contact = TimeToContact(distance, npc.MaxSpeedBase);
        int nukeCast = state.Spell?.CastTime ?? 3_000;                  // (inf.) 3 s: the casters' nukes cast in 2.5-3 s
        (Spell control, SpellLine line) = !held && closing && due ? TargetControl(state, player, npc, contact, now) : default;
        bool killsFirst = KillsFirstNow(state.EngageTargetHealth, npc.HealthPercent, state.FightCasts, contact, nukeCast,
            quickcastReady, player.HealthPercent, state.Attackers);                                         // #167 C11
        PlayerBotCasterAction step = CasterAction(closing, contact, held, breaksOnHit, control?.CastTime ?? 0, nukeCast,
            interrupted, quickcastReady, quickcasting, killsFirst);
        switch (step)
        {
            case PlayerBotCasterAction.StepBack:
                if (!casterHold || now < state.NextKiteTick) return false;
                if (KitePoint(state, player, npc, stop, distance, distance <= ContactDistance) is not Vector3 goal)
                {
                    state.NextKiteTick = now + KitePauseMilliseconds;                   // none: as a blocked one (C3)
                    return false;
                }
                state.KiteGoal = goal;
                state.FightKites++;
                return Kites(state, player, npc, casterHold, now);
            case PlayerBotCasterAction.Control:
                player.attackComponent.StopAttack();
                state.ControlTries++;
                state.NextControlTick = now + control.CastTime + ControlMarginMilliseconds;
                if (!player.CastSpell(control, line)) return false;
                state.FightControls++;
                state.FightQuickcast |= quickcasting;
                state.NextCombatChoiceTick = now + Math.Max(900, control.CastTime + 250);
                state.Activity = PlayerBotActivity.Casting;
                return true;
            case PlayerBotCasterAction.QcControl:
                UseQuickcast(state, player, now, quickcast);                            // then the next think casts it
                return true;
            default:
                return false;
        }
    }

    // #167 F11, C8: the control CasterStep casts at its target now (TargetFirst reads it too, the review of 9aacd69): its
    // next control due and tries left, one the target takes (Takes) and a mez only as the escape (CasterControlAllowed),
    // in range, ready and paid for, the highest of ChooseControl's target order; none, default.
    private static (Spell, SpellLine) TargetControl(PlayerBotState state, GamePlayer player, GameNPC npc, long contact,
        long now) =>
        now < state.NextControlTick || state.ControlTries >= MaxControlTries ? default :
        ChooseControl(state.Controls, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
                HasPowerFor(player.MaxMana, player.Mana, spell.Power) &&
                player.IsWithinRadius(npc, player.castingComponent.CalculateSpellRange(spell)),
            spell => CasterControlAllowed(spell, Takes(npc, spell), contact), forAdd: false, petClass: false);

    // #167 C4: the add's step, for every role with a control (healers, pet classes and melee cast none at an add in
    // st-deathsB, casters only at their target), at a due cast choice before the pet leads and CasterStep
    // (EngageStepOrder). The add (Add) from the monsters on the bot and those coming at it; its control (AddControl), or
    // Quickcast turned on for it first (AddAction). MaxControlTries casts that held nothing on an add and it stops
    // trying on that one. With no control to cast, a Theurgist casts its pets at an add hitting it (Pets), as at its
    // target (FindCombatPetSpell; AutonomousPetSupport.cs:636-650). targetHeld and targetDistance: Engage's. None, default.
    private static (PlayerBotAddAction Step, GameNPC Add, Spell Spell, SpellLine Line) AddPlan(PlayerBotState state,
        GamePlayer player, GameLiving target, bool targetHeld, int targetDistance, long now, bool interrupted,
        bool quickcastReady, bool quickcasting)
    {
        if (now < state.NextAddTick) return default;
        KnowSpells(state, player);
        if (state.Controls.Count == 0 && state.CombatPets.Count == 0) return default;
        if (Add(state, player, target, now) is not GameNPC add) return default;
        (PlayerBotAddAction step, Spell control, SpellLine line) = AddControl(state, player, target, targetHeld,
            targetDistance, add, interrupted, quickcastReady, quickcasting, now);
        if (step is not PlayerBotAddAction.None) return (step, add, control, line);
        if (state.CombatPets.Count == 0 || !IsAttacking(player, add) ||
            !player.attackComponent.AttackerTracker.Attackers.Contains(add))
            return default;
        (Spell pet, SpellLine petLine) = CombatPet(state, player, add, now, inView: true);     // its sight: CastAtAdd's
        return pet == null ? default : (PlayerBotAddAction.Pets, add, pet, petLine);
    }

    // #167 C4: the add's step (AddPlan) carried out: Quickcast turned on (the next think casts with it), or the control
    // or the pet cast at the add (CastAtAdd), a control counted in ctl= and addCtl=, a pet in petCasts= and casts=. False
    // when the cast was refused (no sight to the add): Engage goes on as without it.
    private static bool AddAct(PlayerBotState state, GamePlayer player, GameLiving target, GameNPC add,
        PlayerBotAddAction step, Spell spell, SpellLine line, long now, bool quickcasting, Ability quickcast)
    {
        if (step is PlayerBotAddAction.QcControl)
        {
            UseQuickcast(state, player, now, quickcast);                                // then the next think casts it
            return true;
        }
        bool pets = step is PlayerBotAddAction.Pets;
        if (!CastAtAdd(state, player, target, add, spell, line, now))
        {
            if (pets) state.NextCombatPetTick = now + SummonRetryMilliseconds;
            return false;
        }
        if (pets)
        {
            state.NextCombatPetTick = now + Math.Max(250, spell.CastTime + 100);        // as Engage's
            state.FightPetCasts++;
            state.FightCasts++;
        }
        else
        {
            state.AddControlTries++;
            state.FightControls++;
            state.FightAddControls++;
            state.FightQuickcast |= quickcasting;
        }
        state.NextCombatChoiceTick = now + Math.Max(900, spell.CastTime + 250);
        return true;
    }

    // #167 C4: the add (ChooseAdd) from the monsters on the bot (its attacker list) and those coming at it (ScanComing);
    // a new one starts its tries afresh (a held one is never chosen, so its next turn does too).
    private static GameNPC Add(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        ScanComing(state, player, target, now);
        List<(GameNPC Npc, int Distance, bool TargetsBot, bool Held)> monsters = _addCandidates ??= new();
        monsters.Clear();
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
            if (living is GameNPC npc) monsters.Add((npc, player.GetDistanceTo(npc), IsAttacking(player, npc), Hold(npc).Held));
        foreach (GameNPC npc in state.AddScan)
            monsters.Add((npc, player.GetDistanceTo(npc), IsAttacking(player, npc), Hold(npc).Held));
        GameNPC add = ChooseAdd(monsters, target as GameNPC);
        monsters.Clear();                                                               // no monster held past the think
        if (add != state.ControlledAdd) (state.ControlledAdd, state.AddControlTries) = (add, 0);
        return add;
    }

    // #167 C4: the monsters within IncomingRadius with the bot as their target, the target aside (AddScan), scanned every
    // TargetRefreshMilliseconds (Incoming's cadence; Incoming itself isn't asked in a live fight): Add's, and the first
    // add's (FirstAdd: every role's, until one is seen; a fight's first think scans afresh).
    private static void ScanComing(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        if (now < state.NextAddScanTick) return;
        state.NextAddScanTick = now + TargetRefreshMilliseconds;
        state.AddScan.Clear();
        foreach (GameNPC npc in player.GetNPCsInRadius(IncomingRadius))
            if (npc != target && npc.TargetObject == player && IsExperienceTarget(npc)) state.AddScan.Add(npc);
    }

    // #167 C4: the add step at this add (AddAction) and its control: ChooseControl's add order (no mez from a pet class),
    // one the add takes now (Takes) that keeps it off (ControlKeepsAddOff), in range, ready and paid for; none after
    // MaxControlTries casts on it that held nothing. A caster's control on its target comes first while the target
    // reaches it first (TargetFirst) with a control CasterStep would cast at it now (TargetControl; the review of
    // 9aacd69: any control the target takes, recasting, unpaid or a mez not yet wanted, held the add's back).
    private static (PlayerBotAddAction Step, Spell Spell, SpellLine Line) AddControl(PlayerBotState state,
        GamePlayer player, GameLiving target, bool targetHeld, int targetDistance, GameNPC add, bool interrupted,
        bool quickcastReady, bool quickcasting, long now)
    {
        if (state.AddControlTries >= MaxControlTries || state.Controls.Count == 0) return default;
        PlayerBotRole role = RoleOf(player.CharacterClass.ID);
        int distance = player.GetDistanceTo(add);
        (Spell control, SpellLine line) = ChooseControl(state.Controls, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
                HasPowerFor(player.MaxMana, player.Mana, spell.Power) &&
                player.IsWithinRadius(add, player.castingComponent.CalculateSpellRange(spell)),
            spell => Takes(add, spell) && ControlKeepsAddOff(spell, distance), forAdd: true, petClass: role == PlayerBotRole.Pet);
        if (control == null) return default;
        long contact = TimeToContact(distance, add.MaxSpeedBase);
        GameNPC targetNpc = role == PlayerBotRole.Caster && !targetHeld ? target as GameNPC : null;   // else none first
        bool closing = targetNpc?.TargetObject == player;
        long targetContact = closing ? TimeToContact(targetDistance, targetNpc.MaxSpeedBase) : long.MaxValue;
        bool targetFirst = closing && TargetFirst(TargetControl(state, player, targetNpc, targetContact, now).Item1 != null,
            targetHeld, closing, targetContact, contact);
        return (AddAction(true, true, contact, targetFirst, control.CastTime, Interruptible(control), interrupted,
            quickcastReady, quickcasting), control, line);
    }

    // #167 C4: a cast at the add as a player makes one (the verifier's corrections): the add in sight (the cast's start
    // reads TargetInView, SpellHandler.cs:450, set from the main target's sight at Engage), selected (the spell reads
    // TargetObject at its start in the Casting phase later in this tick, :403-406, :440; nothing in the cast reads it
    // again) and faced (CheckBeginCast's front 180 degrees, :725; Engage keeps facing it to CheckEndCast, :939,
    // FacesWhom). state.Target stays (a new one clears the cast watch, RefreshTarget): Engage selects it again at the next
    // think, and checks sight to it at once. No sight: the add step waits LosRefreshMilliseconds. A refused request puts
    // the target, its sight and the facing back for this think's casts.
    private static bool CastAtAdd(PlayerBotState state, GamePlayer player, GameLiving target, GameNPC add, Spell spell,
        SpellLine line, long now)
    {
        if (!HeadlessLos.Answer(PathfindingProvider.Instance, player, add))
        {
            state.NextAddTick = now + LosRefreshMilliseconds;
            return false;
        }
        bool inView = player.TargetInView;
        player.attackComponent.StopAttack();
        Face(player, add);
        player.TargetObject = add;
        player.TargetInView = true;
        if (!player.CastSpell(spell, line))
        {
            Face(player, target);
            player.TargetObject = target;
            player.TargetInView = inView;
            return false;
        }
        state.AddCastTarget = add;
        state.NextLosTick = 0;
        state.Activity = PlayerBotActivity.Casting;
        return true;
    }

    // #167 C4: the add a cast under way is at (CastAtAdd), while the bot casts and the add lives (AddCastNow); else none.
    private static GameLiving AddCastUnderWay(PlayerBotState state, GamePlayer player) =>
        state.AddCastTarget = AddCastNow(state.AddCastTarget, player.IsCasting, state.AddCastTarget?.IsAlive == true);

    // #167 C4 for C2's break-off (BreaksOff's addControlReady): the add step would control an add now (AddControl, the
    // same inputs as Engage's), the bot engaged with its target alive (at a kill the one still on it becomes the target:
    // no add then) and the step not waiting out a sight check (NextAddTick). A cast it can't make, and the break-off goes.
    private static bool AddControlReady(PlayerBotState state, GamePlayer player, long now)
    {
        GameLiving target = state.Target;
        if (!state.Engaged || target?.IsAlive != true || now < state.NextAddTick) return false;
        KnowSpells(state, player);
        if (state.Controls.Count == 0 || Add(state, player, target, now) is not GameNPC add) return false;
        bool quickcasting = player.effectListComponent.ContainsEffectForEffectType(eEffect.QuickCast);
        Ability quickcast = player.GetAbility(Abilities.Quickcast);
        bool quickcastReady = QuickcastReady(quickcast != null, quickcasting,
            quickcast == null ? 0 : player.GetSkillDisabledDuration(quickcast), state.NextQuickcastTick, player.IsCasting, now);
        bool held = RoleOf(player.CharacterClass.ID) == PlayerBotRole.Caster && Hold(target).Held;
        return AddControl(state, player, target, held, player.GetDistanceTo(target), add,
            player.IsBeingInterruptedByOther && !quickcasting, quickcastReady, quickcasting, now).Step != PlayerBotAddAction.None;
    }

    // #167 F11: a step back under way (CasterStep): on to its point while the bot keeps its hold (casterHold: the target
    // held and, #167 C3, no other monster hitting it); a blocked one stands the bot KitePauseMilliseconds, under the old
    // rule (SwingBreaksHold). True while it walks.
    private static bool Kites(PlayerBotState state, GamePlayer player, GameLiving target, bool casterHold, long now)
    {
        if (state.KiteGoal is not Vector3 goal) return false;
        if (!target.IsAlive || !casterHold)
        {
            state.KiteGoal = null;
            return false;
        }
        player.attackComponent.StopAttack();
        state.Activity = PlayerBotActivity.Fighting;
        PlayerBotStepResult step = state.Mover.StepToward(goal, KiteArriveDistance, now);
        if (step is PlayerBotStepResult.Blocked)
        {
            ReportBlocked(state, player, "kite", goal, now);
            state.NextKiteTick = now + KitePauseMilliseconds;
        }
        if (step is not (PlayerBotStepResult.Blocked or PlayerBotStepResult.Arrived)) return true;
        state.KiteGoal = null;
        return false;
    }

    // The step back's point (KiteTarget) on the navmesh, short of Engage's stop (the spell's, SpellStopDistance) so Engage
    // doesn't walk it back in, in 3D too (KiteFits: on a slope or after OnMesh a flat range lay past it), and farther out
    // than the bot stands (KiteRange); none, null. In melee (inMelee) with no point clear of the adds, the straight-away
    // one (#167 C3). The adds are the last search's (FindWithin), where they are now, the target not one.
    private static Vector3? KitePoint(PlayerBotState state, GamePlayer player, GameNPC target, int stop, int distance,
        bool inMelee)
    {
        if (KiteRange(stop, distance) is not int range) return null;
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in state.SearchAdds)
            if (npc != target && AddReach(player, npc) is > 0 and int reach)
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        Vector3 at = new(target.X, target.Y, target.Z);
        (Vector3 Point, bool Clear)? kite = KiteTarget(new Vector3(player.X, player.Y, player.Z), at, range, adds, inMelee);
        Vector3? onMesh = kite is { } k ? OnMesh(player, k.Point) : null;
        if (onMesh is Vector3 m && (kite.Value.Clear && InAddReach(m, adds) || !KiteFits(m, at, stop))) onMesh = null;
        adds.Clear();
        return onMesh;
    }

    // #167 C3's guard (CasterHolds): another monster hitting the bot (Attacker's: in its attacker list, the bot its
    // target) that no hold stops.
    private static bool OtherHitting(GamePlayer player, GameLiving target)
    {
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
            if (living is GameNPC npc && npc != target && IsAttacking(player, npc) && !Hold(npc).Held) return true;
        return false;
    }

    // What holds a monster (#167 F11): a stun, which damage doesn't break, or a mez or a root (a speed decrease of 99),
    // which any damage breaks (GameLiving.cs:1784-1803).
    private static (bool Held, bool BreaksOnHit) Hold(GameLiving target) =>
        target.IsStunned ? (true, false) : target.IsMezzed || Rooted(target) ? (true, true) : (false, false);

    private static bool Rooted(GameLiving target) =>
        EffectListService.GetEffectOnTarget(target, eEffect.MovementSpeedDebuff) is ECSGameSpellEffect effect &&
        effect.SpellHandler?.Spell is Spell spell && spell.SpellType is eSpellType.SpeedDecrease && spell.Value >= 99;

    // #167 C8: whether the target takes this control now, by the server's own tests of its kind: the immunities
    // (AbstractCCSpellHandler.ApplyEffectOnTarget's CCImmunity, CCSpellHandler.cs:17-21; a stun's, :276-283; a mez's,
    // :160-169, and its resist under 75% health, :173-177; a root's, SpeedDecreaseSpellHandler.cs:25-33), and an NPC's
    // diminishing immunity only when it would refuse this hold (DiminishedAllows; the old StunOk and MezOk read it as
    // immune).
    private static bool Takes(GameLiving target, Spell spell) => spell.SpellType switch
    {
        eSpellType.Stun => Takes(spell, target.HasAbility(Abilities.CCImmunity), target.HasAbility(Abilities.StunImmunity) ||
            target.effectListComponent.ContainsEffectForEffectType(eEffect.StunImmunity),
            EffectListService.GetEffectOnTarget(target, eEffect.NPCStunImmunity) as NpcImmunityEffect, target.HealthPercent),
        eSpellType.Mesmerize => Takes(spell, target.HasAbility(Abilities.CCImmunity),
            target.HasAbility(Abilities.MezzImmunity) || target.effectListComponent.ContainsEffectForEffectType(eEffect.MezImmunity),
            EffectListService.GetEffectOnTarget(target, eEffect.NPCMezImmunity) as NpcImmunityEffect, target.HealthPercent),
        _ => Takes(spell, target.HasAbility(Abilities.CCImmunity), target.HasAbility(Abilities.RootImmunity) ||
            target.effectListComponent.ContainsEffectForEffectType(eEffect.SnareImmunity) ||
            target.effectListComponent.ContainsEffectForEffectType(eEffect.SpeedOfSound), null, target.HealthPercent),
    };

    /// <summary>Whether a monster takes a control now (#167 C8; the review of 9aacd69: the rule Takes reads, tested): not
    /// CC-immune (ccImmune), not immune to its kind (kindImmune: a stun's, a mez's, a root's ability or effect), its
    /// diminishing immunity (diminishing: a stun's or a mez's NpcImmunityEffect, none for a root) still allowing this hold
    /// (DiminishedAllows), and a mez only from 75% health (CCSpellHandler.cs:173-177).</summary>
    public static bool Takes(Spell spell, bool ccImmune, bool kindImmune, NpcImmunityEffect diminishing, byte healthPercent) =>
        !ccImmune && !kindImmune && spell.SpellType switch
        {
            eSpellType.Stun => DiminishedAllows(diminishing, spell.Duration),
            eSpellType.Mesmerize => DiminishedAllows(diminishing, spell.Duration) && healthPercent >= 75,
            _ => true,
        };

    // A healer's heal on itself (DEATHS.md A6), before a rest as a GameBot's (AWBC:2725): below 60% health whatever its
    // power; out of a fight (MayRest's view) from 85% with power to spare, then it sits for power (#167 F2). The gate
    // runs first at no cost, then with the heal's.
    private static bool SelfHeal(PlayerBotState state, GamePlayer player, long now)
    {
        bool inFight = state.Engaged && state.Target?.IsAlive == true ||               // MayRest's view, and one coming
            Threat(state, player, now) != null;                                         //   at it (#167 F7)
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
            !CastOnSelf(state, player, now, heal, line, "heal"))
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
        return CastOnSelf(state, player, now, summon, line, "pet");
    }

    // #167 F8: the class's own self-buffs (NextSelfBuff), one cast at a time, scanned every SelfBuffScanMilliseconds out of
    // a fight (MayRest's view), before a rest as a player buffs and then sits. A release ends them all: the new life
    // scans at once (Think). #167 C9: never mid-rest (BuffScanAllowed), a kill scans at once (SettleTarget) and each buff
    // cast the next scan, back to back, and only those it keeps up out of a fight (KeepsUpOutOfFight). #167 C13: the
    // chant it wants (WantedChant): its regeneration one from the kill to the rest's end, once recovered (sitting, the
    // scan allowed again) its combat one back.
    private static bool SelfBuff(PlayerBotState state, GamePlayer player, long now)
    {
        if (now < state.NextSelfBuffTick || player.IsCasting || state.Engaged && state.Target?.IsAlive == true ||
            !BuffScanAllowed(player.IsSitting, Recovered(player)) ||                    // #167 C9
            Threat(state, player, now) != null)                                         // MayRest's view, and one coming
            return false;                                                               //   at it (#167 F7)
        state.NextSelfBuffTick = now + SelfBuffScanMilliseconds;
        byte health = player.HealthPercent, mana = player.ManaPercent, endurance = player.EndurancePercent,
            powerStop = RestPowerStop(RoleOf(player.CharacterClass.ID));
        List<(Spell Spell, SpellLine Line)> usable = UsableSpells(player).ToList();
        bool resting = player.IsSitting || WantsRest(player, state.RecoverBeforePull);  // #167 C13: the chant it runs
        eSpellType? chant = WantedChant(ChantKinds(usable, player.Level), resting, health, endurance, player.MaxMana, mana,
            powerStop);
        (Spell buff, SpellLine line) = NextSelfBuff(usable, ActiveSpellTypes(player), chant, ChantUp(player), player.Level,
            player.Concentration, player.MaxMana, player.Mana, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
                KeepsUpOutOfFight(spell, health, player.MaxMana, mana, powerStop, endurance));
        if (buff == null || !CastOnSelf(state, player, now, buff, line, "buff")) return false;
        state.NextSelfBuffTick = 0;                                                     // #167 C9: the next one at once
        return true;
    }

    // #167 C9: at a fight's first think in reach, an instant short self-buff (OpensFight: a Reaver's Surge), as SelfBuff
    // picks one, cast on itself before the fight's first swing or spell. True when it cast.
    private static bool FightBuff(PlayerBotState state, GamePlayer player, long now)
    {
        (Spell buff, SpellLine line) = NextSelfBuff(UsableSpells(player), ActiveSpellTypes(player), null, null, player.Level,
            player.Concentration, player.MaxMana, player.Mana,
            spell => OpensFight(spell) && player.GetSkillDisabledDuration(spell) <= 0);
        return buff != null && CastOnSelf(state, player, now, buff, line, "buff");
    }

    // #167 C13: the kind of the chant up (a pulse effect's spell), or none.
    private static eSpellType? ChantUp(GamePlayer player)
    {
        foreach (ECSPulseEffect effect in player.effectListComponent.GetPulseEffects())
            if (effect.SpellHandler?.Spell is Spell spell) return spell.SpellType;
        return null;
    }

    // The kinds of the spell effects on the bot, a chant's pulse effect too (NextSelfBuff's active).
    private static HashSet<eSpellType> ActiveSpellTypes(GamePlayer player)
    {
        HashSet<eSpellType> active = new();
        foreach (ECSGameSpellEffect effect in player.effectListComponent.GetSpellEffects())
            if (effect.SpellHandler?.Spell is Spell spell) active.Add(spell.SpellType);
        return active;
    }

    // #167 F1: at each cast choice a Theurgist casts a pet at its target before its damage spell, whenever it can, as a
    // GameBot does (AutonomousPetSupport.cs:636-650). The server's own cap applies (SummonTheurgistPet.cs:67-71; 0, none,
    // in 1.65). Otherwise the nuke ready now (#167 F9).
    private static void ChooseCombatSpell(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        (Spell pet, SpellLine petLine) = CombatPet(state, player, target, now, player.TargetInView);
        if (pet != null) (state.Spell, state.SpellLine) = (pet, petLine);
        else (state.Spell, state.SpellLine) = Nuke(state, player, target);              // #167 F9: chosen per cast
    }

    // From the summons known at this level (KnowSpells): none for every class but the Theurgist. inView: sight to target
    // (the add step's, #167 C4, is checked by CastAtAdd).
    private static (Spell, SpellLine) CombatPet(PlayerBotState state, GamePlayer player, GameLiving target, long now,
        bool inView)
    {
        KnowSpells(state, player);
        if (state.CombatPets.Count == 0 || now < state.NextCombatPetTick) return default;
        bool underCap = DOL.GS.Spells.SummonTheurgistPet.PetLimitAllowsAnother(player.PetCount,
            DOL.GS.ServerProperties.Properties.THEURGIST_PET_CAP);                      // the server's own test (:67)
        (Spell pet, SpellLine line) = FindCombatPetSpell(state.CombatPets, player.Level, spell => underCap &&
            player.GetSkillDisabledDuration(spell) <= 0 && HasPowerFor(player.MaxMana, player.Mana, spell.Power));
        bool inRange = pet != null && inView && player.IsWithinRadius(target, player.castingComponent.CalculateSpellRange(pet));
        return WantsCombatPet(pet != null, inRange, player.IsCasting, now, state.NextCombatPetTick) ? (pet, line) : default;
    }

    // #167 F9, F1, F11: the damage spells, the pet summons a Theurgist casts at its target and the control spells, from one
    // walk of the usable spells per level and after a training (ObserveXp clears NukesLevel).
    private static void KnowSpells(PlayerBotState state, GamePlayer player)
    {
        if (state.NukesLevel == player.Level) return;
        List<(Spell Spell, SpellLine Line)> usable = UsableSpells(player).ToList();
        state.Nukes = DamageSpells(usable, player.Level, SkillBase.GetSpellByID);    // #167 F10b: bombers' payloads
        state.CombatPets = usable.FindAll(pair => pair.Spell.SpellType is eSpellType.SummonTheurgistPet);
        state.Controls = usable.FindAll(pair => IsControlSpell(pair.Spell) && pair.Spell.Level <= player.Level);   // #167 F11
        state.NukesLevel = player.Level;
    }

    // A spell on the bot itself, stopped and standing (no cast sitting, SpellHandler.cs:556-562). Targeted as a player
    // does it, F1 then the spell, unless it is a Self spell, which ignores the target (SpellHandler.cs:411-416): a
    // self-target is in view (GamePlayer.cs:127-139), and a Group heal takes the caster with no group
    // (SpellHandler.cs:1807-1811). The request is read in the Casting phase later in this tick (CastingComponent.cs:116-144,
    // SpellHandler.cs:403-406), so the target stays until the next think's Engage.
    private static bool CastOnSelf(PlayerBotState state, GamePlayer player, long now, Spell spell, SpellLine line, string why)
    {
        state.Mover.Stop(now);
        if (player.IsSitting)
        {
            EndRest(state, player, now, RestEndWhy(why, Recovered(player)));           // PLAYER_BOT_REST why= (#167)
            player.Sit(false);
        }
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
            if (player.IsSitting)                                                          // REST sec= (#167), if it sat,
                (state.SitSince, state.SitHealth, state.SitMana) =                         //   and hp= and mp=
                    (now, player.HealthPercent, PowerPercent(player.MaxMana, player.ManaPercent));
        }
    }

    /// <summary>Why a rest ended at step 13 (PLAYER_BOT_REST why=): recovered, or a fight (MayRest false) stood the bot up,
    /// incoming when only a monster coming at it did (#167 F7). A cast on itself ends one as heal, pet or buff, a death as
    /// died, and the server's stand (a hit while sitting) as hit, or other.</summary>
    public static string RestStop(bool mayRest, bool incomingOnly) =>
        mayRest ? "recovered" : incomingOnly ? "incoming" : "hit";

    /// <summary>Why a cast on itself that stands the bot up ends its rest (PLAYER_BOT_REST why=, #167): heal, pet or buff
    /// (why), or recovered once the bot has (the review of 9aacd69: C9's buffs wait for the rest's end, BuffScanAllowed,
    /// so a rest that ended recovered logged why=buff, every Skald's with its Song of Rest swapped back, and C9's
    /// mid-rest count reads why=buff).</summary>
    public static string RestEndWhy(string why, bool recovered) => recovered ? "recovered" : why;

    // #167: PLAYER_BOT_REST once per rest, when it ends.
    private static void EndRest(PlayerBotState state, GamePlayer player, long now, string why)
    {
        if (state.SitSince == 0) return;
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatRest(player.Name, player.Level, (now - state.SitSince) / 1000, why,
                state.SitHealth, state.SitMana, state.RestRespawns));
        state.SitSince = 0;
    }

    private static bool Recovered(GamePlayer player) => Recovered(player.HealthPercent, player.MaxMana, player.ManaPercent,
        player.EndurancePercent, RestPowerStop(RoleOf(player.CharacterClass.ID)), RestEnduranceStop);

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
                state.NukesLevel = -1;                                               // new spells (#167 F9, F1)
                if (state.Target != null) (state.Spell, state.SpellLine) = Nuke(state, player, state.Target);
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
