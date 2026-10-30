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

/// <summary>The recovery's step after a release (PlayerBotDriver.AfterReleaseStep, #167 F4; WalkOut, D4).</summary>
public enum PlayerBotRecovery { Fight, StepOut, Replan, Wait, Done, WalkOut }

/// <summary>A break-off's step (PlayerBotDriver.BreakOffStep, #167 C2).</summary>
public enum PlayerBotBreakOff { Run, NextPoint, Done, Fight, Caught }

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

/// <summary>An Animist's step before a pull (PlayerBotDriver.AnimistStepFor, #167 E CY3).</summary>
public enum PlayerBotAnimistStep { Pull, Walk, Replant, Wait, Skip }

/// <summary>A think's walk (#167 FIGHT and DEATH walk=, F12d's field; PlayerBotDriver.NextWalk): to its target or an
/// Animist's stand (Hunt), to its anchor (Anchor) or a roam point (Roam), out of reach after a release (Clear), to its
/// rest spot (Rest), a caster's step back (Kite), a break-off's run (BreakOff), the walk out toward the camp planned
/// under the release's immunity (WalkOut, #167 D4).</summary>
public enum PlayerBotWalk { None, Hunt, Anchor, Roam, Clear, Rest, Kite, BreakOff, WalkOut }

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
        SoloSteps = player.CharacterClass is ICharacterClass cls ? PlayerBotDriver.SoloSteps(cls.ID) : 0;   // #167 E CY4
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
    public GameLiving Target;
    public readonly List<(GameLiving Npc, long Until)> BlindSkips = new();   // #167 E M2, R1: the targets dropped for want
                                                                 //   of sight (or CY3's skip), each passed over by the
                                                                 //   pick only until its own time (MarkBlind)
    public readonly List<(GameLiving Npc, long Until)> Unreachables = new();   // #167 E U1: the monsters a walk found no
                                                                 //   path to, each passed over by the pick and by
                                                                 //   self-defence until its own time (LeaveUnreachable)
    public bool Engaged;
    public Spell Spell;
    public SpellLine SpellLine;
    public Vector3? RoamDestination;
    public ushort LastRegion;
    public int LastLevel, DeathSafetySteps, ErrorsLogged;
    public int SoloSteps;                                        // #167 E CY4: the class's floor under the death steps
    public long RoamingSince, NextIdleLine, NextRelocateTick; // no target reached since: PLAYER_BOT_IDLE, PLAYER_BOT_RELOCATE
    public long NextAnchorCheckTick;                             // #167 E fix U3: a blocked roam's next corridor test to its
                                                                 //   anchor (ReanchorAfterRoamBlock)
    public readonly HashSet<GameNPC> RelocateRejected = new(); // no corridor to them: not checked again
    public long CappedSince;                                     // DEATHS.md A4: the first search that passed monsters over
    public ConColor? SafestPassedOver;                           //   for the cap and found none (CappedClock), and the
    public ConColor? FallbackCap;                                //   safest con it passed over; a GameBot's fallback camp's
    public bool HuntAvoided;                                     //   con, its names hunted at a stale one, and its failed
    public readonly List<(ushort Region, Vector3 At, long Until)> FailedCamps = new();   // camps, 15 minutes (PlanRelocation)
    public readonly Dictionary<string, long> AvoidUntil = new(StringComparer.OrdinalIgnoreCase);   // DEATHS.md A3: the
    public bool KillerAvoided;                                   //   names of deaths' killers and targets (IsAvoided)
    public readonly List<(ConColor Con, long Until)> KillerCons = new();   // #167 D6: and the killers' cons, 5 minutes
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
    public long LastExperience, NextTargetRefreshTick, NextLootTick, NextLosTick, NextStyleTick,
        NextCombatChoiceTick, NextNeighbourCountTick, NextProgressCheckTick, LastProgressTick, ProgressExperience, ProgressMoney;
    public Vector3 ProgressPosition;
    public bool DeathRecorded;
    public long ReleaseAskedAt;                                  // #167 D7: the last release asked for in this death (0:
    public int ReleaseTries;                                     //   none; ReleaseDue) and the asks (PLAYER_BOT_RELEASE try=)
    public long EngagedSince;                                    // the deaths' diagnostics (levelmatch/DEATHS.md 2): the
    public byte EngageHealth, EngageMana;                        //   fight's first think in reach and the bot's health and
    public int FightAdds, Swings, NotVisible, FightCasts;        //   power then, its most attackers, swings and casts,
    public AttackData LastSeenSwing;                             //   the swing counted last (CountSwing), its target
    public long BlindSince;                                      // #167 E M2: its target out of sight since (0: in sight,
    public int FightSight;                                       //   BlindClock), the sight checks a hit in contact
    public bool FightBlind;                                      //   answered (FIGHT sight=) and a drop for it (blind=1)
    public int BlindStop, FightBlindSteps;                       // #167 E R1: the stop it steps in to while its target is
                                                                 //   out of sight (0: none; BlindStepStop), and the steps
                                                                 //   (FIGHT blindSteps=)
    public bool FromDefence;                                     //   hitting the bot when picked (PLAYER_BOT_FIGHT);
    public int LifeKills, LifeRests;                             //   this life's fights won and sits, its last kill,
    public long LastKillTick;                                    //   and the killer the Dying event names (OnBotDying)
    public GameObject Killer;
    public Vector3? LastKillAt;                                  // #167 E fix U3's review: where it stood at its last kill
                                                                 //   (none yet, or in another region: null; HuntedHere)
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
    public Dictionary<Spell, DOL.GS.Spells.SpellHandler> NukeHandlers = new();   // #167 E C1: each nuke's own handler
    public bool HasStun, HasRoot;                                // #167 E C1: a stun or a root among its controls (DotFits,
                                                                 //   KeepsRootCastable), at NukesLevel
    public int FightDots;                                        // #167 E C1: FIGHT and DEATH dots=, its DoT casts
    public bool KillingQuickcast, FightKillingQuickcast;         // #167 E C2b: Engage's, and FIGHT qcKill=; whether the
    public bool OneNukeKills;                                    //   nuke chosen at the last cast choice kills at once
    public int FightTargetMezzes;                                // #167 E C4: FIGHT and DEATH ctlMez=, its mezzes at the target
    public long NextAnimistStandTick;                            // #167 F10b: after a blocked walk to its stand
    public long TurretWaitSince;                                 // #167 E CY3: a pull held for a turret it can't plant yet
    public int FightControls, FightKites;                        // #167 FIGHT ctl= and kites=
    public int FightCtlBroken;                                   // #167 C3: FIGHT ctlBroken=, from the target's root or mez
    public (int Range, int Clear, int Mesh, int Fit) FightKiteNone;   // #167 D8: FIGHT kiteNone=, step backs with no point
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
    public IReadOnlyList<Vector3> RestSpots = Array.Empty<Vector3>();   // #167 D10: that check's clear spots (RestSpots),
    public int RestBlocked;                                      //   the walks to them blocked this rest, and the reaches it
    public readonly List<(GameNPC Npc, Vector3 At, int AggroRange)> RestReach = new();   //   read (positions only, no monster)
    public bool KilledWithAdds;                                  // #167 C2: a kill with 2 or more on the bot (read for
    public long BreakOffSince, NextBreakOffTick;                 //   3 s, SecondMonsterCase); the break-off under way (0:
    public Vector3? BreakOffGoal;                                //   none) and the next one's earliest, its point, a step
    public bool BreakOffArrived;                                 //   there arrived, the monsters still after it, the
    public readonly List<GameNPC> BreakOffChasers = new();       //   points it was blocked on or found no walk to
    public readonly List<Vector3> BreakOffBlocked = new();       //   (NextBreakOffPoint), and its start's health,
    public byte BreakOffHealth, BreakOffEndurance;               //   endurance and attackers (BREAKOFF)
    public int BreakOffAttackers;
    public long BreakOffSprintOffAt = -1;                        // #167 E-S: BREAKOFF sprintOff=, ms into the run (-1: none)
    public double BreakOffRate;                                  // #167 BREAKOFF rate=: its loss rate then (HealthRate)
    public string BreakOffWhy;                                   // #167 D1: BREAKOFF why= (BreakOffWhy), Spent's end
    public bool FollowsAddKill;                                  // #167 D2: the fight after an add kill (OpenFight), the
    public long CaughtSince;                                     //   caught clock (InReachSince), and a refusal logged in
    public bool BreakOffRefused;                                 //   this fight (BREAKOFF outcome=refused, once a fight)
    public long NextBreakOffRetryTick;                           // #167 E T1: with no point, the next try (0: at once),
    public int BreakOffHolds;                                    //   the tries that found none while it held (BREAKOFF
    public long BreakOffHoldSince;                               //   holds=), the hold's start (0: none, HitWhileHolding)
    public string BreakOffHoldWhy, BreakOffTurn;                 //   and why it began (nopoint, blocked), why it turned
    public int BreakOffSpeed = -1, BreakOffChaserSpeed = -1;     //   (turn=), and its run speed and the fastest chaser's
                                                                 //   when it began (speed=, chaserSpeed=; -1: not read)
    public bool BreakOffRunsOn;                                  // #167 E T2b: running on to its point past Done (RunsOnToClear)
    public bool BackPending, BackOwed, BackMoved;                // #167 C1: PLAYER_BOT_BACK, from a release to its first
    public long BackStepOutMs, BackHoldMs, BackWalkMs;           //   fight: the line due, the replan owed and a camp planned
                                                                 //   since, and the time stepping out, held and walking;
    public bool BackSame;                                        //   #167 D4: that camp the anchor it had (replan=same),
    public long BackImmuneMs;                                    //   and the time walked while immune (immune=)
    public long ImmuneUntil;                                     // #167 D4: the release's immunity's end (ImmunityEnd), the
    public Vector3? WalkOutGoal;                                 //   walk out's point (StepOutPoint), the walk over for this
    public bool WalkOutOver, WalkOutSprint;                      //   release, and a sprint of its own on (SprintChange)
    public Vector3 TargetSpawn;                                  // #167 C7: the target's spawn spot and the reach it aggroes
    public int TargetRespawnReach;                               //   from (RespawnOf, at the pick), and the last kills'
    public readonly List<(Vector3 At, int Reach, long KilledAt)> RecentKills = new();   //   (RememberKill): rested out of
    public (int At, int Hp) FightAdd = (-1, -1);                 // #167 FIGHT and DEATH addAt= and addHp= (FirstAdd), the
    public int EngageDistance = -1, PickCrowd = -1;              //   distance at the first think in reach, the pick's
                                                                 //   crowding (crowd=, FindWithin's; -1 for another pick);
    public byte SitHealth, SitMana;                              //   REST hp= and mp= when it sat, and respawn=: the spawn
    public int RestRespawns;                                     //   spots the rest spot was read against (WalksToRestSpot)
    public int SitBlocked;                                       // #167 D10: REST blocked= and inReach= as it sat (Rest)
    public bool SitInReach;
    public byte SitEndurance;                                    // #167 E-R: REST end=, its endurance as it sat
    public PlayerBotWalk Walk, LastWalk, FightWalk;              // #167 st-deathsD's FIGHT and DEATH fields: walk=, this
    public readonly Queue<(long Tick, byte Health)> HealthSamples = new();   //   think's walk, the last think's (NextWalk) and the
    public long FightHeldMs;                                     //   fight's (OpenFight); rate=, the last 3 s of thinks'
    public int FightCtlLanded, FightCtlFailed;                   //   health (SampleHealth); heldSec=, its target held
    public (Spell Spell, GameLiving Target, long At) PendingControl;   //   (Hold); ctlLanded= and ctlFailed=, and the control
    public int FightHpAtReach = -1, FightTargetHpAtReach = -1;   //   cast awaited (SettleControl); hpAtReach= and
    public int WouldBreakHp = -1;                                //   tHpAtReach= (HitNow) and wouldBreak= (ProjectedBreak)
    public int FightRaceLeft = PlayerBotDriver.NoRace;           // #167 E-L: FIGHT and DEATH raceLeft= (TrackFight)
    public int Reserve;                                          // #167 E-R: the endurance a close fight keeps for a run
    public long NextReserveTick;                                 //   (ReserveNow, read once a StyleMilliseconds); FIGHT
    public byte EngageEndurance;                                 //   endStart= (Engage's opening), styleHeld= and castHeld=
    public int FightStylesHeld, FightCastsHeld;                  //   (the style choices and the thinks it held one back)
    public int RunNeed;                                          // #167 E fix R2: read with the reserve, the run's need
    public bool TargetAlone;                                     //   (EscapeEndurance) and no monster on the bot but its
                                                                 //   target (HoldingReserve)
    public int FightBuffHitters;                                 // #167 D12: the live monsters on the bot when the fight's
                                                                 //   buff was last weighed (FightBuffCheck)
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
    public const int BlindDropMilliseconds = 5_000;          // #167 E M2: a target out of sight this long that fights
                                                             //   neither the bot nor its pet is dropped (inf.)
    public const int BlindStepDistance = 300;                // #167 E R1: out of sight beyond contact, a step in this
                                                             //   long, then a look again (BlindStepStop; inf.)
    public const int StyleMilliseconds = 1000;
    public const int NeighbourCountMilliseconds = 60_000;
    public const int UnreachableSkipMilliseconds = 30_000;
    public const int ProgressCheckMilliseconds = 10_000;     // the pilot checked every tick (APP:585-604)
    public const int MeaningfulMoveDistance = 96;
    public const int DeathReleaseDelayMilliseconds = 10_000; // APP:29, :575-581
    public const int ReleaseRetryMilliseconds = 10_000;      // #167 D7: an ask that didn't revive the bot goes again (inf.)
    public const int IdleLineMilliseconds = 5 * 60_000;
    public const int RelocateAfterMilliseconds = 2 * 60_000; // no target reached this long: the hunting ground is outgrown
    public const int RelocateCandidates = 5;                 // corridor checks per try
    public const int AnchorCheckMilliseconds = 30_000;       // #167 E fix U3: a blocked roam tests the corridor to its
                                                             //   anchor at most this often (ReanchorAfterRoamBlock; inf.)
    public const int NearWalk = 10_000;                      // #167 E W1: a plan after a death looks this near the bot
                                                             //   first (st-deathsD2/D2b: after-death plans of 10k+ died
                                                             //   first 27 of 72, under 10k 13 of 148)
    public const ConColor NearFallbackMax = ConColor.BLUE;   // #167 E W1: the near pass's hardest fallback median (BLUE
                                                             //   fallbacks after a death died first 0 of 16)
    public const int CappedRelocateMilliseconds = 30_000;    // DEATHS.md A4: nothing in reach fits the cap this long
    public const int MaxDeathSteps = (int)ConColor.YELLOW - (int)ConColor.GREEN;   // a solo GameBot's (AWBC:3756, :3793-3796)
    public const string Outgrown = "outgrown", Capped = "capped", Died = "died", BrokeOff = "brokeoff";   // RelocateReason
    public const int MaxHoldMilliseconds = 60_000;           // #167 F4: a hold while no camp is planned ends by then
    public const int KillerConMemoryMilliseconds = 300_000;  // #167 D6: a killer's con kept (st-deathsD's fix round, inf.)
    public static readonly int[] StepOutRings = [500, 900, 1_300, 1_700, 2_100, 2_500];   // #167 F4: a released bot's
    public const int SpotDirections = 12;                    //   candidate spots, every 30 degrees on each ring
    public const int StepOutPauseMilliseconds = 10_000;      // (inf.) after a blocked step out, it stands this long
    public const int SameCampDistance = 200;                 // #167 BACK replan=same (d-after-release/a3_samespot.py)
    public const int ContactDistance = 250;                   // #167 F11: a monster this near is in melee (inf.)
    public const int ControlMarginMilliseconds = 500;         //   a cast's slack before contact (inf.)
    public const int MaxControlTries = 3;                     //   control casts per target that held nothing (inf.)
    public const int KitePauseMilliseconds = 5_000;           //   after a blocked step back (inf.), or none found (C3)
    public const int KiteFallbackDistance = 400;              // #167 C3: in melee with no clear point, this far (inf.)
    public const int KiteArriveDistance = 20;                 //   the step back ends this near its point, this short of the
    public const int KiteStopMargin = 50;                     //   stop (KiteRange)
    public static readonly int[] KiteTurns = [0, 30, -30, 60, -60, 90, -90];   // degrees off straight away from the target
    public const int KiteMeshCalls = 8;                       // #167 D8: a step back's navmesh calls a think, at most (inf.);
    public const string KiteMissRange = "range", KiteMissClear = "clear",   //   kiteNone='s whys (KiteWalk, KiteNoneCounted)
        KiteMissMesh = "mesh", KiteMissFit = "fit";
    public const byte KillFirstHealthPercent = 60;            // #167 C11: the nukes before the control from this health
    public const int AnimistStandMargin = 150;                // #167 F10b: by its turret; the turret's reach less this
    public const int TurretWaitMilliseconds = 12_000;         // #167 E CY3: the in-combat window (GameLiving.cs:31) and 2 s
    public const int IncomingRadius = 2_000;                  // #167 F7: a monster this near with the bot as its target
    public static readonly int[] RestSpotRings = [300, 600, 900, 1_200];   // #167 F7: the rest spot's candidates (inf.)
    public const int RestSpotTries = 3;                       // #167 D10: rest walks per rest, each blocked one the next (inf.)
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
    public const byte RestEnduranceStop = 80;                //   caster regenerates at half rate under 50%,
                                                             //   PowerRegenerationAmountCalculator.cs:38-50: batch A's 70
                                                             //   left 46.6% of its pulls ending under 50%); all, endurance
                                                             //   (#167 E-R: 60 left a style class 0-20 after one fight,
                                                             //   a style costing 15-22 with a slow weapon and endurance
                                                             //   regenerating neither in combat nor moving,
                                                             //   ClassicRestRegeneration.cs; 80 holds a run's reserve and
                                                             //   a style or two: +0.3% of bot time on st-deathsD2's rests,
                                                             //   e-design s7). The chant (WantedChant), the regeneration
                                                             //   buff (KeepsUpOutOfFight) and the errand rests read it too
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
    public const int BreakOffMilliseconds = 30_000;          //   from then, done with no chaser near (#167 D2); after a
                                                             //   fought one, no break-off for as long (inf.)
    public const int ChaseRadius = 6_000;                    //   past this a monster drops it (StandardMobBrain.cs:23, :589)
    public const int ChaserReach = 200;                      //   a monster's melee reach (MeleeAttackRange, GameLiving.cs:303),
                                                             //   which the sprint's lead clears (Outruns, the review of 9aacd69)
    public const int SecondMonsterMilliseconds = 3_000;      //   the second-monster case this long from the kill (inf.: the
                                                             //   analysts' add fights leave out those begun within 3 s of one)
    public const int BreakOffBlockedRadius = 100;            //   a point this near one blocked is left out (inf.), and the
    public const int BreakOffCorridorTries = 3;              //   corridor tests per point chosen (inf.)
    public const string Shook = "shook", Fought = "fought";  //   PLAYER_BOT_BREAKOFF outcome= (and Died)
    public const string Refused = "refused";                 // #167 D2: and one that would fire but doesn't (BreakOffRefusal)
    public const string ByAdds = "adds", AtKill = "atkill", FollowUp = "followup", AvoidedName = "avoided",
        CasterLosing = "caster", Spent = "spent";            // #167 D1: BREAKOFF why=, the rule that fired (BreakOffWhy)
    public const byte CasterBreakOffTargetPercent = 25;      // #167 D1: a caster losing to one, or spent, with its target
                                                             //   above this breaks off (inf.)
    public const int BreakOffMaxMilliseconds = 45_000;       // #167 D2: still chased then, it fights (inf.); from
    public const int ShakenDistance = 1_500;                 //   BreakOffMilliseconds, done with none this near (inf.)
    public const int CaughtReach = ChaserReach + 50;         //   caught: a chaser not held this near (inf.), this long
    public const int CaughtMilliseconds = 2_000;             //   (inf.), from this long into the run, and a hit on the bot
    public const int CaughtGraceMilliseconds = 3_000;        //   this recent (CaughtNow, st-deathsD's fix round; inf.)
    public const int CaughtHitMilliseconds = 2_000;
    public const int BreakOffRetryMilliseconds = 1_000;      // #167 E T1: with no point, a try for one this often (inf.),
    public static readonly int[] RelaxedBreakOffRings = [800];   //   800 out too (NearBreakOffGoal, inf.); BREAKOFF turn=
    public const string TurnCaught = "caught", TurnNoPoint = "nopoint", TurnBlocked = "blocked", TurnCap = "cap";
    public const int RangedHarmRange = 1_000;                //   a chaser's spell from this far harms the run (inf.)
    public const int HealthRateMilliseconds = 3_000;         // #167 rate=: the health lost a second over the last 3 s
    public const int ProjectedSeconds = 6;                   //   wouldBreak=: 6 s of that loss kept in hand (breakoff #5,
                                                             //   d-breakoff/projected.py's P6; logged only)
    public const int RaceLossMargin = 10;                    // #167 E-L: one monster breaks a bot that is no caster off
    public const int RaceReadLoss = 20;                      //   when the race since its first hit projects its target
                                                             //   above this at the bot's death (TargetLeftAtDeath), read
                                                             //   from 20 lost since that hit (a shorter read is a swing's
                                                             //   noise). On st-deathsD2 and D2b: 113 such non-caster
                                                             //   deaths, 105 with no break-off (e-design s3, s13)
    public const int NoRace = -999;                          //   FIGHT and DEATH raceLeft= with no read
    public const string RaceLost = "race";                   //   BREAKOFF why=
    public const int ReserveMargin = 5;                      // #167 E-R: in a close fight (CloseFight) a style or a
    public const int MaxEscapeReserve = 41;                  //   hybrid's damage cast keeps the endurance a run needs
    public const int CloseRacePercent = 30;                  //   from the fastest monster on the bot (EscapeEndurance)
    public const int SpellEnduranceCost = 5;                 //   and this margin; none when that is past MaxEscapeReserve
                                                             //   (205 and faster: the two styles it costs buy no run). A
                                                             //   race projecting the bot under 30 at its target's death
                                                             //   is close (HealthLeftAtKill). A cast costs 5
                                                             //   (SpellHandler.cs:1244-1250, :1442-1445)
    public const int SprintOffGap = 400, SprintOnGap = 300;  // #167 E-S: a break-off's sprint goes off with the nearest
                                                             //   free chaser this far behind and 25 s of its speed over the
                                                             //   run's more (it can't close to its reach while it chases),
                                                             //   and on again when one comes within 300 (inf.)

    private static readonly ConcurrentDictionary<GamePlayer, PlayerBotState> _states = new();
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int Score)> _candidates;      // FindWithin's, per timer
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int AggroRange)> _adds;       //   thread (TimerService)
    [ThreadStatic] private static List<(GameNPC Npc, Vector3 At, int AggroRange)> _reach;      // ClearSpotNear's
    [ThreadStatic] private static List<(GameNPC Npc, int Distance, bool TargetsBot, bool Held)> _addCandidates;   // Add's
    [ThreadStatic] private static List<(byte Health, int Distance, bool Avoided)> _hitters;    // StartsBreakOff's

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
        NextWalk(state);                                     //    #167 walk=: none yet, the last think's kept
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
        StartLife(state, now, RoleOf(player.CharacterClass.ID));                         //    a new life
        state.SoloSteps = SoloSteps(player.CharacterClass.ID);                           //    #167 E CY4: the cap's floor
        SampleHealth(state.HealthSamples, now, player.HealthPercent);                    //    #167 rate=
        if (state.SitSince != 0 && !player.IsSitting)                                    //    stood by the server (a hit)
            EndRest(state, player, now, Attacker(state, player, now, anyHitter: true) != null ? "hit" : "other");
        state.DeathRecorded = false;
        (state.ReleaseAskedAt, state.ReleaseTries) = (0, 0);                             //    #167 D7: alive, the release took
        state.Attackers = player.attackComponent.AttackerTracker.Count;                 //    Die clears it (GameLiving.cs:2050)
        if (state.Engaged) state.FightAdds = Math.Max(state.FightAdds, state.Attackers);   //    #167 D2: adds= on a think
                                                                                         //    Engage doesn't reach too
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
        (state.RestSpots, state.RestBlocked) = (Array.Empty<Vector3>(), 0);             //    #167 D10: and its spots afresh
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
        SettleIdleClock(state);
    }

    /// <summary>Step 16's idle clock (RoamingSince, RelocateReason's Outgrown): a target reached stops it; a target
    /// blocked on the way leaves it running. #167 E fix U2: reached is engaged with no step in under way (BlindStop 0,
    /// R1): a step in out of sight opens the fight (Engaged) before its walk, and a walk that then ends blocked
    /// (LeaveUnreachable) left the fight open. On st-deathsE2b Pbotmaada, on a pocket of the navmesh above its camp,
    /// opened a step in each time it picked a crackler 719-880 units off, out of sight below the edge and within its
    /// spell stop; each reset the clock, so the 2 minutes of an outgrown camp never ran and no relocation moved it.
    /// </summary>
    public static void SettleIdleClock(PlayerBotState state)
    {
        if (state.Engaged && state.BlindStop == 0) state.RoamingSince = 0;
    }

    /// <summary>Step 6, a new life (after a release, or the login's first): the recovery LifeStart gives (StartRecovery),
    /// this life's counts afresh. The release restores it all and ends its buffs: the self-buffs scan at once. #167 D3: a
    /// caster's login life owes a rest (OpensLifeRested; role: Think's RoleOf, the UT fixture has no CharacterClass).
    /// </summary>
    public static void StartLife(PlayerBotState state, long now, PlayerBotRole role)
    {
        if (!state.DeathRecorded && state.AliveSince != 0) return;
        (bool stepOut, bool backOff) = LifeStart(state.DeathRecorded, state.AliveSince == 0);
        if (stepOut) StartRecovery(state, now, backOff);                                // #167 F4, C6
        state.RecoverBeforePull = OpensLifeRested(role, state.DeathRecorded);           // #167 D3
        state.AliveSince = now;
        state.HealthSamples.Clear();                                                     // #167 rate=: no gain from the release
        state.LifeKills = state.LifeRests = 0;
        state.Killer = null;
        state.KillerAvoided = false;
        state.NextSelfBuffTick = 0;
    }

    /// <summary>Whether a new life opens with a rest owed (#167 D3, StartLife's RecoverBeforePull): a caster's login life
    /// only. Its login buffs drain power and the rest came only under 45% (NeedsRest): 13 of st-deathsC's 53 caster login
    /// first fights with no rest were fatal, 7 of the 12 that died in the fight at 16% power or less. A release restores
    /// full power already (afterRelease; GamePlayer.cs:1723-1725), and a rest after every one would add about 70 rests.
    /// </summary>
    public static bool OpensLifeRested(PlayerBotRole role, bool afterRelease) => role == PlayerBotRole.Caster && !afterRelease;

    /// <summary>The recovery a new life starts (#167 F4, C6): (stepOut, backOff). After a release (afterDeath), all of it:
    /// the step out of reach, the back-off and the replan owed (AfterReleaseStep). The login's first life (firstLife) only
    /// the step out: the test spots are camp centres (5 or more monsters within 1,400), and 73 of st-deathsB's 370 logins
    /// died in their first fight, 37 of the 42 login-life deaths with adds within 60 s. No back-off (no death) and no
    /// replan owed. Live, it matters only for a logout inside a camp.</summary>
    public static (bool StepOut, bool BackOff) LifeStart(bool afterDeath, bool firstLife) =>
        (afterDeath || firstLife, afterDeath);

    /// <summary>Step 4: a bot in another region (a release to a bind there, a re-entry) starts its hunt afresh: no walk,
    /// target, idle or capped clock, fallback cap or stale camp's names. Its failed camps stay (#167 F4): each has its
    /// region (FailedCamps); cleared, a bot that came back planned into the camp it died at. #167 E fix U3's review: no
    /// last kill's spot either (LastKillAt, another region's; HuntedHere reads it in this one's zones).</summary>
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
        state.LastKillAt = null;
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
        Ability quickcast = player.GetAbility(Abilities.Quickcast);                     // #167 E C2b: before the spell choice
        bool interrupted = player.IsBeingInterruptedByOther && !quickcasting;           // a quickcast one goes (SpellHandler.cs:581)
        bool quickcastReady = QuickcastReady(quickcast != null, quickcasting,
            quickcast == null ? 0 : player.GetSkillDisabledDuration(quickcast), state.NextQuickcastTick, player.IsCasting, now);
        PlayerBotRole role = RoleOf(player.CharacterClass.ID);                          // #167 D3: before the cast gate
        bool choosesSpell = ChoosesSpell(due, player.IsCasting, now, state.NextSpellChoiceTick);
        bool casterFight = role == PlayerBotRole.Caster && state.Engaged, quickcastDue = interrupted && quickcastReady;
        if (choosesSpell)                                                               // a cast choice: the pet or the
        {                                                                               //   damage spell (#167 F1); #167
            state.NextSpellChoiceTick = now + SpellChoiceMilliseconds;                  //   E C2b: whether the nuke it
            (state.Spell, state.SpellLine, state.OneNukeKills) = KillingNukeChoice(     //   casts kills at once (risk-1)
                casterFight && state.Attackers <= 1, quickcasting || quickcastDue,
                () => Nuke(state, player, target, killingQuickcast: true), () => CombatSpell(state, player, target, now),
                spell => NukeKillsNow(state, player, target, spell, now));
        }
        state.KillingQuickcast = KillingQuickcast(casterFight, state.OneNukeKills, quickcasting, quickcastDue,
            state.Attackers);                                                           //   the killing quickcast
        bool interruptible = state.Spell != null && Interruptible(state.Spell);
        (bool held, bool breaksOnHit) = role == PlayerBotRole.Caster ? Hold(target) : default;   // #167 C3: a caster's hold
        bool otherHitting = held && OtherHitting(player, target);                                //   on its target, alone
        bool canCast = ShouldCastFor(role, state.Spell != null, player.ManaPercent,      // #167 D3: a caster's gate is its
            role == PlayerBotRole.Caster && CasterCanPay(state, player, state.Spell, state.SpellLine, held,   //   spell's
                quickcasting || interrupted && interruptible && quickcastReady,           //   cost, doubled under Quickcast,
                state.KillingQuickcast),                                                  //   no reserve to kill (#167 E C2b)
            now, state.SpellBlockedUntil);
        if (canCast && role is not (PlayerBotRole.Caster or PlayerBotRole.Pet) &&      // #167 E-R: a hybrid's damage cast
            HoldsDamageCast(role, player.Endurance,                                      //   keeps a run's reserve too (5 a
                ReserveNow(state, player, target, now), state.Spell))                    //   cast, SpellHandler.cs:1442-1445),
        {                                                                                //   never a lifetap (#167 E fix R2):
            canCast = false;                                                             //   it swings instead; FIGHT
            state.FightCastsHeld++;                                                      //   castHeld=, by think
        }
        if (state.Engaged) WatchFight(state, player, target, role, held, now);           // #167 heldSec= and on, walk in or not
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
        int walkStop = state.BlindStop > 0 ? Math.Min(stop, state.BlindStop) : stop;   //   each step: the review of
        if (addCast == null && distance > walkStop)                            //   5feb32b); #167 E R1: nearer while
        {                                                                      //   out of sight (BlindStepStop)
            player.attackComponent.StopAttack();
            player.StopCurrentSpellcast();
            state.Activity = PlayerBotActivity.Hunting;
            state.BlindSince = 0;                                                       // #167 E M2: no blind clock walking in
            Vector3 goal = new(target.X, target.Y, target.Z);
            state.Walk = PlayerBotWalk.Hunt;                                            // #167 walk=
            if (state.Mover.StepToward(goal, walkStop, now) is PlayerBotStepResult.Blocked)
            {
                ReportBlocked(state, player, "target", goal, now);
                LeaveUnreachable(state, target, now);                                   // #167 E U1: its own 30 s
            }
            return;
        }
        state.Mover.Stop(now);
        Face(player, EngageFacing(target, addCast, player.IsCasting, addCast?.IsAlive == true));   // in front:
                                                                        //   AttackComponent.cs:1065; the add (C4)
        if (now >= state.NextLosTick)                         // TargetInView: AttackComponent.cs:1065, SpellHandler.cs:450
        {
            state.NextLosTick = now + LosRefreshMilliseconds;
            bool los = HeadlessLos.Answer(PathfindingProvider.Instance, player.CurrentZone,
                new Vector3(player.X, player.Y, player.Z), new Vector3(target.X, target.Y, target.Z));
            bool fightsBot = !los && FightsBot(player, target);                         // #167 E M2: read only when the
            player.TargetInView = SightInReach(los, distance, fightsBot);               //   ray says no; in contact,
            if (!los && player.TargetInView) state.FightSight++;                        //   FIGHT sight=
            state.BlindSince = BlindClock(state.BlindSince, player.TargetInView, now);
            bool petOn = PetOn(player, target), targetsBot = target.TargetObject == player;
            // #167 E R1: out of sight beyond contact, a step in (BlindStepStop), not a drop, and a look again where it
            // ends; a cast under way (begun in sight) goes on, the step at the next look. The choice is BlindLook's.
            (bool drop, state.BlindStop) = BlindLook(player.TargetInView, fightsBot || petOn || targetsBot, distance,
                MeleeStopDistance(player.attackComponent.AttackRange), state.BlindSince, now, player.IsCasting,
                state.BlindStop);
            if (drop)
            {
                DropBlind(state, player, now);                                          //   FIGHT blind=1, the pick
                return;                                                                 //   passes it over 30 s
            }
            if (!player.IsCasting && state.BlindStop > 0)
            {
                state.NextLosTick = 0;
                state.FightBlindSteps++;                                                //   FIGHT blindSteps=
            }
        }
        bool opening = !state.Engaged;
        if (opening)                                          // the fight's first think in reach: PLAYER_BOT_FIGHT's start
        {
            if (OpenFight(state, now, player.HealthPercent, PowerPercent(player.MaxMana, player.ManaPercent), distance,
                    target.HealthPercent))
                ReportBack(state, player, now);                                         // #167 C1: the first since a release
            state.EngageBuffs = SelfBuffsUp(player.effectListComponent.GetSpellEffects()     // FIGHT buffs= (#167 F8)
                .Where(effect => effect.SpellHandler?.Spell != null).Select(effect => effect.SpellHandler.Spell.SpellType));
            state.EngagePet = player.ControlledBrain?.Body?.IsAlive == true;
            state.EngageIll = Ill(player);                                              // DEATH ill= (#167 F4)
            state.EngageEndurance = player.EndurancePercent;                            // #167 E-R FIGHT endStart=
            WatchFight(state, player, target, role, held, now);                         // #167 heldSec= and on
            if (player.ControlledBrain != null)                                         // its pet too: the pet window's
                player.CommandNpcAttack();                                              //   attack (PetWindowHandler.cs:79-82)
        }
        state.Engaged = true;
        if (state.BlindStop > 0 && distance > state.BlindStop) return;                 // #167 E R1: the step in from the
                                                                                        //   next think, no blind cast now
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
        (bool buffDue, state.FightBuffHitters) = FightBuffCheck(player.IsCasting, opening,   // #167 D12: a hard fight's
            opening ? ConLevels.GetConColor(player.GetConLevel(target)) : ConColor.GREY,      //   only (the con read at the
            player.IsCasting ? 0 : LiveHitters(player), state.FightBuffHitters);              //   opening only, its one use)
        if (buffDue && FightBuff(state, player, now)) return;                           // #167 C9: a Surge first
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
            if (IsDamageOverTime(state.Spell)) state.FightDots++;                       // #167 E C1 dots=
            state.FightQuickcast |= quickcasting;
            state.FightKillingQuickcast |= quickcasting && state.KillingQuickcast;      // #167 E C2b qcKill=
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
                QueueUsableStyle(state, player, ReserveNow(state, player, target, now));  // #167 E-R: a run's reserve
            }
            if (!player.attackComponent.AttackState && CanSwing(player.ControlledBrain?.Body))
                player.attackComponent.RequestStartAttack(target);                      // AttackComponent.cs:491-496
            state.Activity = PlayerBotActivity.Fighting;
        }
    }

    // #167 E M2: its target fights the bot: has it as its target and is on its attacker list (it hit or swung lately).
    private static bool FightsBot(GamePlayer player, GameLiving target) =>
        target.TargetObject == player && player.attackComponent.AttackerTracker.ContainsAttacker(target);

    // #167 E M2: the bot's live pet has its target as its own (a pet's kill is the owner's).
    private static bool PetOn(GamePlayer player, GameLiving target) =>
        player.ControlledBrain?.Body is GameNPC pet && pet.IsAlive && pet.TargetObject == target;

    // #167 E M2: a target out of sight BlindDropMilliseconds that fights neither the bot nor its pet, nor comes at it
    // (DropsBlindTarget): its fight's line (FIGHT result=dropped blind=1), then the pick alone passes it over 30 s
    // (MarkBlind; not LeaveUnreachable, the review).
    private static void DropBlind(PlayerBotState state, GamePlayer player, long now)
    {
        if (state.Engaged)
        {
            state.FightBlind = true;
            ReportFight(state, player, now, false, false);
        }
        MarkBlind(state, state.Target, now);
        Disengage(state, player);
    }

    /// <summary>Whether the bot has a spell it may cast (APP:670, the cast watch's block); the interrupts are
    /// CombatAction's.</summary>
    public static bool ShouldCast(bool hasSpell, byte manaPercent, long now, long blockedUntil) =>
        hasSpell && manaPercent >= 18 && now >= blockedUntil;

    /// <summary>The cast gate by role (#167 D3, Engage's canCast): a caster's is the cost of its spell (casterCanCast,
    /// CasterCanCast), every other role keeps ShouldCast's 18%; the cast watch's block for either.</summary>
    public static bool ShouldCastFor(PlayerBotRole role, bool hasSpell, byte manaPercent, bool casterCanCast, long now,
        long blockedUntil) =>
        role == PlayerBotRole.Caster ? hasSpell && now >= blockedUntil && casterCanCast
            : ShouldCast(hasSpell, manaPercent, now, blockedUntil);

    /// <summary>Whether a caster can pay for its spell now (#167 D3): its power less reserve (ControlReserve) covers the
    /// spell's cost as the server charges it (PowerCost: the pool maxMana for a percent cost, focusFactor, Quickcast's
    /// double for a spell with a cast time). Under 18% ShouldCast's gate had it swing with a nuke of about 9% still paid
    /// for: 14 of st-deathsC's 20 deaths under 25% power waited 0. Under Quickcast the server checks no cost
    /// (SpellHandler.cs:774-775, :978), only some power left for a fixed one (:967-973): with no reserve to keep, it
    /// casts.</summary>
    public static bool CasterCanCast(int maxMana, int mana, int power, int reserve, double focusFactor, bool quickcast,
        int castTime) =>
        quickcast && reserve == 0 && (mana > 0 || power <= 0) ||
        mana - reserve >= PowerCost(maxMana, power, focusFactor, quickcast, castTime);

    /// <summary>A spell's power as the server charges it (#167 D3; SpellHandler.PowerCost, SpellHandler.cs:1181-1240,
    /// is a handler's, with random free casts and the Quickcast read at the cast's start, so not called from here): a
    /// negative power is that percent of percentBase (:1206-1212), times a focus caster's factor (FocusCostFactor,
    /// :1214-1233), doubled under Quickcast for a spell with a cast time (:1237-1239), truncated as the server does.</summary>
    public static int PowerCost(int percentBase, int power, double focusFactor, bool quickcast, int castTime)
    {
        double cost = power < 0 ? percentBase * (double)power * -0.01 : power;
        cost *= focusFactor;
        if (quickcast && castTime > 0) cost *= 2;
        return (int)cost;
    }

    /// <summary>A focus caster's cost factor (#167 D3, SpellHandler.cs:1214-1233): 1.2 less its focus bonus, the focus
    /// level (focusLevel) x 0.4 over the spell's level, 0 to 0.4, scaled by its spec over the spell's level up to 1: 120%
    /// with no focus down to 80%. Not a focus caster (or a line with no focus: the caller's), 1.</summary>
    public static double FocusCostFactor(bool focusCaster, int focusLevel, int spellLevel, int specLevel)
    {
        if (!focusCaster) return 1;
        double bonus = focusLevel * 0.4;
        if (spellLevel > 0) bonus /= spellLevel;
        bonus = Math.Clamp(bonus, 0, 0.4);
        if (spellLevel > 0) bonus *= Math.Min(1, specLevel / (double)spellLevel);
        return 1.2 - bonus;
    }

    /// <summary>The power a caster keeps for the escape (#167 D3, CasterCanCast's reserve): the cheapest known control's
    /// cost (controlCosts) while its target isn't held, else none. With one root kept, a caster whose nukes ran its power
    /// down can still hold the monster that reaches it. #167 E C2b: none for the killing quickcast (killingQuickcast,
    /// KillingQuickcast; no default): the nuke ends the fight the control would only hold, and a dry Mentalist (5 of
    /// E2's 13 deaths) could not cast it.</summary>
    public static int ControlReserve(IEnumerable<int> controlCosts, bool held, bool killingQuickcast)
    {
        if (held || killingQuickcast) return 0;
        int least = int.MaxValue;
        foreach (int cost in controlCosts) least = Math.Min(least, cost);
        return least == int.MaxValue ? 0 : least;
    }

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

    /// <summary>Whether the bot rests for its chant (WantedChant's resting, #167 C13; the review of 9aacd69: with it false
    /// in SelfBuff no test failed and no Song of Rest ran): sitting, or wanting a rest (WantsRest).</summary>
    public static bool Resting(bool sitting, bool wantsRest) => sitting || wantsRest;

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

    /// <summary>SelfBuff's gate (#167 F8, C9; the review of 9aacd69: with BuffScanAllowed switched off in SelfBuff no test
    /// failed): its scan due (nextTick), no cast under way, out of a live fight (fighting: engaged with a live target) and
    /// never mid-rest (BuffScanAllowed). A monster coming at it (Threat) SelfBuff asks after these.</summary>
    public static bool SelfBuffScanDue(long now, long nextTick, bool casting, bool fighting, bool sitting, bool recovered) =>
        now >= nextTick && !casting && !fighting && BuffScanAllowed(sitting, recovered);

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

    /// <summary>Whether the fight's own buff (OpensFight's: a Reaver's Surge) goes now (#167 D12): only in a hard fight, a
    /// YELLOW or harder at its first think (opening), or a second live monster on the bot (attackers: LiveHitters) that
    /// wasn't there when it was last weighed (attackersBefore, FightBuffCheck). C9 cast it at every fight's first think:
    /// a Reaver's power per won fight went from 12.7 to 27.0 in st-deathsC, and a Surge costs about 23 s of rest to save
    /// 2.5 s or less in a GREEN fight.</summary>
    public static bool FightBuffDue(bool opening, ConColor con, int attackers, int attackersBefore) =>
        opening && con >= ConColor.YELLOW || attackers >= 2 && attackersBefore < 2;

    /// <summary>Engage's weighing of the fight's buff (#167 D12): never while it casts (no cast over a cast, as C9's
    /// opening), and then the monsters before stay those of the last think it was weighed on (the verifier's correction:
    /// an add that arrives on a think the bot casts brings the buff on the next one). (due, the monsters to keep).</summary>
    public static (bool Due, int Before) FightBuffCheck(bool casting, bool opening, ConColor con, int hitters, int before) =>
        casting ? (false, before) : (FightBuffDue(opening, con, hitters, before), hitters);

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
    // to the DRIVER line's kills. #167 D13 R1b: a kill stops a cast still under way at the dead target (StopsCastAtKill),
    // so the kill's buff scan runs before the rest.
    private static void SettleTarget(PlayerBotState state, GamePlayer player)
    {
        if (state.Target == null || IsValidTarget(player, state.Target, Cap(state, GameLoop.GameLoopTime)) ||
            state.Target is GameNPC npc && FightsBotOrPet(npc.TargetObject, player, player.ControlledBrain?.Body) &&
            IsAttackable(player, npc))
            return;
        bool dead = !state.Target.IsAlive;
        if (state.Engaged) ReportFight(state, player, GameLoop.GameLoopTime, dead, false);   // with this life's kills before it
        if (state.Engaged && dead)
        {
            state.LifeKills++;                                    // every fight won, a monster another bot counted too
            state.LastKillTick = GameLoop.GameLoopTime;
            state.LastKillAt = new(player.X, player.Y, player.Z);   // #167 E fix U3's review: ReanchorAfterRoamBlock
            state.RecoverBeforePull = true;                       // rest before the next pull (WantsRest)
            state.NextSelfBuffTick = 0;                           // #167 C9: the self-buffs first, before the rest
            CastingComponent casting = player.castingComponent;   // #167 D13 R1b: and no cast at the dead target to wait
            if (StopsCastAtKill(casting?.IsCasting == true,       //   out first (null-safe: the tests' players have no
                    casting?.SpellHandler?.Target == state.Target))   //   casting component)
                player.StopCurrentSpellcast();
            if (PlayerBotDriverReport.Kill(state.Target)) state.Kills++;
            if (state.TargetRespawnReach > 0)                     // #167 C7: rested out of (WalksToRestSpot)
                RememberKill(state.RecentKills, state.TargetSpawn, state.TargetRespawnReach, GameLoop.GameLoopTime);
        }
        SettleIdleClockAtTargetEnd(state, dead);
        state.KilledWithAdds = state.Engaged && dead && state.FightAdds >= 2;   // #167 C2: the second-monster case
        state.Target = null;
        state.Engaged = false;
    }

    /// <summary>SettleTarget's idle clock (RoamingSince): a target that died stops it, its camp not outgrown, engaged or
    /// not. Killed by another before this bot got there, as before; #167 E fix U2b: or while the bot stepped in out of
    /// sight, where step 16 no longer stops it (SettleIdleClock), so a kill (by another, or its own damage over time)
    /// left the clock running into the rest after it, and the outgrown camp's relocation could follow a kill. A target
    /// gone alive leaves it as it was.</summary>
    public static void SettleIdleClockAtTargetEnd(PlayerBotState state, bool dead)
    {
        if (dead) state.RoamingSince = 0;
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
        if (found != state.Target) TakeTarget(state, player, found, crowd);
    }

    // A new target (RefreshTarget's pick; #167 D2: a caught bot's, TurnOn): the fight's marks and the cast watch afresh,
    // and its damage spell.
    private static void TakeTarget(PlayerBotState state, GamePlayer player, GameNPC found, int crowd)
    {
        state.Target = found;
        state.FromDefence = found != null && IsAttacking(player, found);                  // FIGHT and DEATH from=
        state.PickCrowd = crowd;                                                           //   and crowd= (#167)
        (state.TargetSpawn, state.TargetRespawnReach) = found?.Brain is StandardMobBrain brain   // #167 C7: its respawn
            ? RespawnOf(found, brain.AggroLevel, brain.AggroRange, player.IsObjectGreyCon(found)) : default;
        state.Engaged = false;
        state.KillingQuickcast = state.OneNukeKills = false;                               // #167 E C2b: the last fight's
        state.NextLosTick = 0;
        state.SpellBlockedUntil = 0;                                                       // a new target clears the watch
        state.CastTarget = null;
        state.NextSpellChoiceTick = 0;                                                     //   and chooses at once
        state.KiteGoal = null;                                                             // #167 F11
        (state.BlindSince, state.FightSight, state.FightBlind) = (0, 0, false);            // #167 E M2
        (state.BlindStop, state.FightBlindSteps) = (0, 0);                                 // #167 E R1
        (state.Spell, state.SpellLine) = found == null ? (null, null) : Nuke(state, player, found);
    }

    // The candidates within the con cap go through the adds rule (PickUncrowded): the 2,600-unit ring takes only one with
    // no potential add near it or on the walk in to where the bot would stand at its stop (#167 C5: CrowdingAlong, from
    // the adds this scan finds, no new one), the 5,000-unit ring the least crowded. The scan reaches AddScanMargin past
    // the ring for the adds by a candidate at its edge; those along a walk in lie within it. One over the cap is passed over, never taken (DEATHS.md A4: the old rollback
    // reset the death steps to fit it), and the capped clock runs while the wider ring finds nothing else. An avoided
    // name is passed over too, unless the bot was sent to a camp of them (HuntAvoided). A keep guard of another realm or
    // of none counts as an add, never as a candidate (#167 D7, CountsAsThreat).
    private static GameNPC FindWithin(PlayerBotState state, GamePlayer player, ushort radius, long now, out int crowd)
    {
        bool passedOver = false;
        ConColor? safestOver = null;
        ConColor cap = Cap(state, now), natural = MaximumTargetCon(1, 0);
        bool leashed = Leashed(state, player);
        List<(GameNPC Npc, Vector3 At, int Score)> candidates = _candidates ??= new();
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _adds ??= new();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)(radius + AddScanMargin)))
        {
            bool experience = IsExperienceTarget(npc);                                  // monsters only, adds too, and
            if (!CountsAsThreat(experience, npc is GameKeepGuard, npc.Realm, player.Realm)) continue;   //   (#167 D7)
            Vector3 at = new(npc.X, npc.Y, npc.Z);                                      //   a hostile keep guard as an
            if (AddReach(player, npc) is > 0 and int reach) adds.Add((npc, at, reach)); //   add, never a pick
            if (!experience || !player.IsWithinRadius(npc, radius) || !IsValidTarget(player, npc, natural) ||
                PickSkips(state, npc, now))                                          // #167 E M2, U1
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

    // #167 C5: the stop a pull takes (PullStopDistance) with the damage spell ready to cast now (Nuke's choice, no add
    // looked for).
    private static int PullStop(PlayerBotState state, GamePlayer player)
    {
        KnowSpells(state, player);
        (Spell nuke, _) = ChooseNuke(state.Nukes, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
            HasPowerFor(player.MaxMana, player.Mana, spell.Power), false, null);
        return PullStopDistance(nuke, player.ManaPercent, player.attackComponent.AttackRange,
            nuke == null ? 0 : player.castingComponent.CalculateSpellRange(nuke));
    }

    // Self-defence: the nearest monster that is hitting the bot (its AttackerTracker, as BotGroupSupport.UnderAttack
    // reads it), whatever its con and wherever the leash is. The pilot's filter alone left the bot roaming or resting
    // while an orange monster killed it, and each death lowered its con cap further. anyHitter: the ones found unreachable
    // too, for the rest gate and the summon (SkipsHitter).
    private static GameNPC Attacker(PlayerBotState state, GamePlayer player, long now, bool anyHitter = false)
    {
        GameNPC best = null;
        int bestDistance = int.MaxValue;
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
        {
            if (living is not GameNPC npc || !IsAttacking(player, npc) || SkipsHitter(state, npc, now, anyHitter))
                continue;
            int distance = player.GetDistanceTo(npc);
            if (distance < bestDistance) { best = npc; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>Whether Attacker passes over a monster hitting the bot: one Engage found unreachable, while its mark
    /// lasts (UnreachableMarked; #167 E U1: each its own), when it picks whom to fight back. Never for anyHitter (the
    /// rest gate, MayRest; the summon): that monster still hits the bot, a sitting player takes double melee damage that
    /// never misses (AttackAction.cs:236-238, AttackComponent.cs:2206-2208), and no hit stands it up. The old gate,
    /// InCombat, kept such a bot standing. A blind mark is the pick's alone (BlindSkipped).</summary>
    public static bool SkipsHitter(PlayerBotState state, GameLiving npc, long now, bool anyHitter) =>
        !anyHitter && UnreachableMarked(state, npc, now);

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
    // tick's; between scans the one found while it still comes. The ones Engage found unreachable are no threat, between
    // scans too (the review of 9549283: the cached one was re-picked and walked to each think): it would keep the bot
    // from ever sitting. The errands' UnderAttack stays the hitters' (no detour for a monster on its way).
    private static GameNPC Incoming(PlayerBotState state, GamePlayer player, long now)
    {
        if (now < state.NextIncomingTick)
        {
            if (state.Incoming is GameNPC held && !IsIncoming(held.TargetObject == player, IsAttackable(player, held),
                    player.GetDistanceTo(held), UnreachableMarked(state, held, now)))
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
                    IsExperienceTarget(npc) && IsAttackable(player, npc), distance, UnreachableMarked(state, npc, now)))
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

    // The bot's con cap: its death cap (never under its class's solo cap, CapSteps, #167 E CY4), or at a GameBot's
    // fallback camp that camp's own con (AWBC:3446-3452), as AutonomousDeathRecoveryPolicy.EncounterMaximum gives a
    // GameBot's pull filter; under the cons that killed it (CapAfterDeaths, #167 D6).
    private static ConColor Cap(PlayerBotState state, long now) => CapAfterDeaths(
        MaximumTargetCon(1, CapSteps(state.DeathSafetySteps, state.SoloSteps)), state.FallbackCap,
        LowestKillerCon(state.KillerCons, now));

    /// <summary>#167 D6: the cap after deaths, EncounterMaximum's (the death cap deathCap, or the fallback camp's con),
    /// under lowestKillerCon, the lowest con that killed the bot in the last 5 minutes (LowestKillerCon), never under
    /// GREEN (KillerLimit: a YELLOW or harder killer only). HandleDeath steps the death cap down and clears FallbackCap,
    /// but a fallback plan set it to YELLOW again and EncounterMaximum lifted the cap back to it: st-deathsC's release
    /// first fights at fallback=YELLOW camps were fatal 32 of 42 (12 bots), at fit camps 4 of 76.</summary>
    public static ConColor CapAfterDeaths(ConColor deathCap, ConColor? fallback, ConColor? lowestKillerCon)
    {
        ConColor cap = AutonomousDeathRecoveryPolicy.EncounterMaximum(deathCap, MaximumTargetCon(1, 0), fallback, false);
        return KillerLimit(lowestKillerCon) is ConColor limit && cap > limit ? limit : cap;
    }

    /// <summary>#167 D6: the con the cap and the fallback (CapAfterDeaths, PlanRelocation, FallbackInPlace) stay at or
    /// under after lowestKillerCon: the con under it (UnderKillerCon) for a killer that counts (CountsAsKiller); none
    /// (null) with no killer or one that doesn't.</summary>
    public static ConColor? KillerLimit(ConColor? lowestKillerCon) =>
        lowestKillerCon is ConColor killer && CountsAsKiller(killer) ? UnderKillerCon(killer) : null;

    /// <summary>A killer's con that counts (#167 D6, st-deathsD's fix round): YELLOW or harder. A BLUE or GREEN killer
    /// leaves the cap and the fallback alone: the death cap already steps the bot down (DeathSafetySteps), and the
    /// fallback's filter under a BLUE killer left no camp (4 bots, 31 of st-deathsD's 75 capped-none plans).</summary>
    public static bool CountsAsKiller(ConColor con) => con >= ConColor.YELLOW;

    /// <summary>#167 D6: the highest con under a killer's, never under GREEN: the cap's limit (CapAfterDeaths) and the
    /// fallback's (PlanRelocation, FallbackInPlace). The review of D: the fallback took only a camp under the killer's
    /// con, so after a GREEN killer none was left (grey monsters form no camp), though the cap still hunts
    /// GREEN.</summary>
    public static ConColor UnderKillerCon(ConColor killer) => (ConColor)Math.Max((int)ConColor.GREEN, (int)killer - 1);

    /// <summary>#167 D6: the plan's caps (RelocateIfOutgrown): its fit pass under the death cap (never under the class's
    /// solo cap, CapSteps, #167 E CY4) and the killers' cons as the pulls are (CapAfterDeaths with no fallback: the plan
    /// picks the next one), else it planned a camp the bot then passed over and the capped clock moved it on; and the
    /// lowest killer's con, which the fallback stays under.</summary>
    public static (ConColor Fit, ConColor? KillerCon) PlanCaps(PlayerBotState state, long now)
    {
        ConColor? killer = LowestKillerCon(state.KillerCons, now);
        return (CapAfterDeaths(MaximumTargetCon(1, CapSteps(state.DeathSafetySteps, state.SoloSteps)), null, killer), killer);
    }

    /// <summary>#167 D6: a killer's con (con, against the bot's level then), each death's, for a killer that counts
    /// (CountsAsKiller: YELLOW or harder), kept KillerConMemoryMilliseconds; those whose time is up go. st-deathsD's fix
    /// round: kept for a GameBot's failure memory (FailureMemoryMilliseconds, 15 minutes), a YELLOW killer left a bot
    /// among YELLOW camps no fallback for that long, planning none every 30 s (6 bots, 44 capped-none plans); 16 bots
    /// lost 42 kills against st-hearth11.</summary>
    public static void RememberKillerCon(List<(ConColor Con, long Until)> cons, ConColor con, long now)
    {
        cons.RemoveAll(kept => kept.Until <= now);
        if (CountsAsKiller(con)) cons.Add((con, now + KillerConMemoryMilliseconds));
    }

    /// <summary>The lowest con kept that killed the bot (RememberKillerCon), null with none (#167 D6).</summary>
    public static ConColor? LowestKillerCon(IReadOnlyList<(ConColor Con, long Until)> cons, long now)
    {
        ConColor? lowest = null;
        foreach ((ConColor con, long until) in cons)
            if (now < until && (lowest == null || con < lowest)) lowest = con;
        return lowest;
    }

    private static bool IsAttackable(GamePlayer player, GameLiving target) =>
        target != null && target != player && target.IsAlive && target.ObjectState is GameObject.eObjectState.Active &&
        target.CurrentRegion == player.CurrentRegion && GameServer.ServerRules.IsAllowedToAttack(player, target, true) &&
        (target is not GameNPC npc || (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0);

    /// <summary>#167 D4: a monster the plan (RelocateIfOutgrown) counts in a camp: one the bot would hunt, alive, in its
    /// region; IsAttackable without the server's IsAllowedToAttack. For a monster of no realm (IsExperienceTarget) that
    /// test adds only the player's immunity: the release starts TIMER_KILLED_BY_MOB's 10 s (AbstractServerRules.cs:258-262,
    /// :280-285; ServerProperties.cs:1273-1274), and while it lasts a player may attack no monster
    /// (AbstractServerRules.cs:385-392; NormalServerRules.cs:28 calls the base). So the plan found no camp then (279 of
    /// st-deathsB's 312 died replans: none checked=0), and C1 held it until the immunity was over. The immunity only
    /// delays the bot's first swing, not where its camp is: it plans at once now.</summary>
    public static bool IsPlannable(GamePlayer player, GameNPC npc) =>
        IsExperienceTarget(npc) && npc.IsAlive && npc.CurrentRegion == player.CurrentRegion;

    /// <summary>#167 D4: the plan's monsters (RelocateIfOutgrown, PlanRelocation's) among npcs: those in zone it counts
    /// (IsPlannable; the review of D: with the old filter, IsAttackable, put back no test failed) over GREY, each with
    /// its con to the bot, whether the bot would hunt it under natural (IsEligibleExperienceTarget) and whether it
    /// avoids its name (avoidUntil).</summary>
    public static List<(GameNPC Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> PlanMonsters(GamePlayer player,
        IEnumerable<GameNPC> npcs, Zone zone, ConColor natural, IReadOnlyDictionary<string, long> avoidUntil, long now)
    {
        List<(GameNPC Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters = new();
        foreach (GameNPC npc in npcs)
        {
            if (npc.CurrentZone != zone || !IsPlannable(player, npc)) continue;
            ConColor con = ConLevels.GetConColor(player.GetConLevel(npc));
            if (con > ConColor.GREY)
                monsters.Add((npc, new(npc.X, npc.Y, npc.Z), con,
                    con <= natural && AutonomousPlayerPilot.IsEligibleExperienceTarget(con, npc.EffectiveLevel, player.Level, 1),
                    IsAvoided(avoidUntil, npc.Name, now)));
        }
        return monsters;
    }

    /// <summary>The walk out's start (#167 D4, StepsOutOfReach; the review of D pinned it): while the release's
    /// immunity lasts (immuneLeft), with no point to walk to yet (hasGoal), the walk not over for this release (over)
    /// and a camp planned since the release (backMoved, PlanCamp's).</summary>
    public static bool StartsWalkOut(long immuneLeft, bool hasGoal, bool over, bool backMoved) =>
        immuneLeft > 0 && !hasGoal && !over && backMoved;

    /// <summary>#167 D7: a monster the step out (ClearSpotNear) and the adds rule (FindWithin's adds: the pick's
    /// crowding, the rest spot's and the step back's) keep clear of: an experience target, or a keep guard of another
    /// realm or of none (Pbotaaacv's realm-less "Renegade" guards at its battleground bind killed it in all 14 of its
    /// release first fights). Keep guards stay out of pulls and plans (IsExperienceTarget). Threat sees them already
    /// (Attacker has no such filter): the bot fights back.</summary>
    public static bool CountsAsThreat(bool expTarget, bool keepGuard, eRealm guardRealm, eRealm botRealm) =>
        expTarget || keepGuard && guardRealm != botRealm;

    /// <summary>CountsAsThreat for a monster round a bot of botRealm.</summary>
    public static bool IsThreat(GameNPC npc, eRealm botRealm) =>
        CountsAsThreat(IsExperienceTarget(npc), npc is GameKeepGuard, npc.Realm, botRealm);

    /// <summary>A monster the break-off's run keeps clear of (BreakOffGoal's adds, #167 C2): a threat (IsThreat; the review
    /// of D: a hostile keep guard too, as the step out and the walk out keep clear of it, ThreatsWithin), not one with the
    /// bot as its target (on it already: a chaser).</summary>
    public static bool IsBreakOffAdd(GameNPC npc, GameObject bot, eRealm botRealm) =>
        npc.TargetObject != bot && IsThreat(npc, botRealm);

    // A copy of AutonomousWorldBotController.IsExperienceMonster (AWBC:4855-4861, private there); #167 E M1: and no
    // training dummy (IsTrainingPropName).
    public static bool IsExperienceTarget(GameNPC npc) =>
        npc?.ObjectState is GameObject.eObjectState.Active && npc.Realm == eRealm.None && npc.Brain != null &&
        (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 &&
        npc is not GameMerchant and not GameGuard and not GameTaxi and not GameSummonedPet and not GameKeepGuard and
            not GameTrainer and not GameTeleporter and not GameHealer and not CraftNPC &&
        !AutonomousNeutralTownPolicy.IsTownResident(npc) && !IsTrainingPropName(npc.Name);

    /// <summary>#167 E M1: a training dummy by its name ("training dummy", "fighting dummy", "Hitback Dummy - ..."): region
    /// 51's are plain GameNPC rows (levels 29-39) that never lose health. st-deathsD2: a bot died on one in 92 s, two
    /// planned camps of them and killed nothing for about 12 minutes each. "dummy golem" is a monster.</summary>
    public static bool IsTrainingPropName(string name) =>
        name != null && (name.EndsWith(" dummy", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(" dummy ", StringComparison.OrdinalIgnoreCase));

    /// <summary>#167 E (CY4): the classes that take no YELLOW monster solo: one step under it (BLUE) from the start, as
    /// any bot after one death (CapSteps). Their YELLOW one-monster pulls were fatal 20% or more on st-deathsD2 and D2b
    /// and 15% or more on st-hearth11 and 11b, with 2 or more deaths on each pair: Bard 16 of 36 (14 of 44), Ranger 6 of
    /// 21 (7 of 21), Healer 4 of 20 (4 of 18), Mercenary 2 of 9 (2 of 10), Nightshade 4 of 5 (3 of 14), Blademaster 3 of
    /// 4 (2 of 5), Mentalist 4 of 8 (6 of 7). It covers at most 36 deaths on the D legs (3 came after a death, at a BLUE
    /// cap already), about 29 net, for about 46 YELLOW wins (about 20 same-level kills of XP). A GameBot's YELLOW cap
    /// (MaximumTargetCon) rests on monster-formula armour and 10%/s regeneration. Wardens and Savages are left to their
    /// kits (PlayerBotKit.PlayerChoice, PlayerBotKitGear.FitsHand): their YELLOW deaths were the broken kits'.</summary>
    public static int SoloSteps(int classId) => (eCharacterClass)classId is eCharacterClass.Bard or eCharacterClass.Ranger or
        eCharacterClass.Healer or eCharacterClass.Mercenary or eCharacterClass.Nightshade or eCharacterClass.Blademaster or
        eCharacterClass.Mentalist ? 1 : 0;

    /// <summary>#167 E (CY4): the steps the cap reads (Cap, PlanCaps, the IDLE line): the death steps, never under the
    /// class's solo steps (SoloSteps). So a listed class's first death leaves it at BLUE and its second takes it to GREEN,
    /// as any bot's second does; the DEATH line's deathSteps= stays the deaths'.</summary>
    public static int CapSteps(int deathSteps, int soloSteps) => Math.Max(deathSteps, soloSteps);

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
    /// for RelocateAfterMilliseconds. #167 D4: also while the release's immunity lasts. C1 held the reason then, as the
    /// plan's IsAttackable found no monster while the bot was immune (AbstractServerRules.cs:385-392); the plan reads
    /// IsPlannable now, so the bot plans at once.</summary>
    public static string RelocateReason(long roamingSince, long cappedSince, long now, string owed) =>
        owed ??
        (cappedSince != 0 && now - cappedSince >= CappedRelocateMilliseconds ? Capped :
         roamingSince != 0 && now - roamingSince >= RelocateAfterMilliseconds ? Outgrown : null);

    /// <summary>PLAYER_BOT_RELOCATE for a move: its fields as they were (runs/legcheck.py reads bot= and to), then the
    /// reason (RelocateReason) and the fallback camp's con, - for none (the death cap holds, Cap); after a death, the
    /// milliseconds since the release, barred=old when the plan bars the anchor it had (#167 D9), and capKill= (#167 D6;
    /// RelocateSuffix). #167 E W1: then recent=1 on a plan after a death (AfterDeathPlan) and near=1 on a camp its near
    /// pass took (RelocateSuffix).</summary>
    public static string RelocateLine(string bot, int level, Vector3 from, Vector3 to, int distance, string monster,
        int monsterLevel, ushort zone, string reason, ConColor? fallback, long sinceReleaseMs, bool barredOld,
        ConColor? capKill, bool recent = false, bool near = false) =>
        $"PLAYER_BOT_RELOCATE bot={bot} level={level} from=({from.X:0},{from.Y:0}) to=({to.X:0},{to.Y:0},{to.Z:0}) " +
        $"distance={distance} monster=\"{monster}\" monsterLevel={monsterLevel} zone={zone} reason={reason} " +
        $"fallback={fallback?.ToString() ?? "-"}" + RelocateSuffix(reason, sinceReleaseMs, barredOld, capKill, recent, near);

    /// <summary>PLAYER_BOT_RELOCATE when no camp checked had a corridor (or none was found): checked= of them.</summary>
    public static string RelocateNoneLine(string bot, int level, int candidates, ushort zone, string reason,
        long sinceReleaseMs, bool barredOld, ConColor? capKill, bool recent = false) =>
        $"PLAYER_BOT_RELOCATE bot={bot} level={level} none checked={candidates} zone={zone} reason={reason}" +
        RelocateSuffix(reason, sinceReleaseMs, barredOld, capKill, recent, false);

    // #167 C1: RELOCATE sinceRelease= on a death's replan, last (the fields before it stay as they were); D9: then
    // barred=old when the plan bars the anchor the bot had (BarOldAnchor); D6: then capKill=, the lowest con that killed
    // the bot in the last 5 minutes, YELLOW or harder (RememberKillerCon), while it keeps one (PlanCaps). #167 E W1: then
    // recent=1 and near=1.
    private static string RelocateSuffix(string reason, long sinceReleaseMs, bool barredOld, ConColor? capKill,
        bool recent, bool near) =>
        (reason == Died ? $" sinceRelease={sinceReleaseMs}" : "") + (barredOld ? " barred=old" : "") +
        (capKill is ConColor kill ? $" capKill={kill}" : "") + (recent ? " recent=1" : "") + (near ? " near=1" : "");

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

    /// <summary>The replan a death owes, as HandleDeath sets it (#167 F4, C1b; the review of 9aacd69: the rule tested
    /// where it applies): Died when it owes one (OwesReplan), however far from its anchor (anchor) it died (deathSpot):
    /// the old limit, LeashRadius + RoamRadius, let a bot that died on a walk walk back past that camp; else none.</summary>
    public static string ReplanOwedAfterDeath(bool onTask, bool canRelocate, Vector3 deathSpot, Vector3 anchor) =>
        OwesReplan(onTask, canRelocate) ? Died : null;

    /// <summary>A camp planned (RelocateIfOutgrown's move to spot, for reason): the leash and the walk go there (a task bot
    /// levelling for its errand has no leash to pull it), its rejects start afresh, that camp's con the fallback cap
    /// (fallback; none: the death cap, Cap) and at a camp of avoided names (stale) those too (FindWithin), the capped
    /// clock afresh (the next search starts it, on the way there too), BACK replan=moved after a death (#167 C1; same at
    /// the anchor it had, D4), and the relocation owed planned (#167 F4, C2). The review of 9aacd69: with BackMoved's line
    /// dropped no test failed.</summary>
    public static void PlanCamp(PlayerBotState state, Vector3 spot, ConColor? fallback, bool stale, string reason)
    {
        if (reason == Died) state.BackSame = SameCamp(spot, state.Anchor);
        state.Anchor = spot;
        state.RoamDestination = spot;
        state.RelocateRejected.Clear();
        state.FallbackCap = fallback;
        state.HuntAvoided = stale;
        state.CappedSince = 0;
        state.BackMoved |= reason == Died;
        state.ReplanOwed = null;
        state.ReplanSince = 0;
    }

    /// <summary>The fight's first think in reach (Engage's opening; PLAYER_BOT_FIGHT's start): its counts afresh, the bot's
    /// health and power (mana: PowerPercent), its distance and the target's health then (#167 FIGHT engageDist=, C11's
    /// NukesNeeded), a fresh add scan (ScanComing), no control tried (F11) or step back due, the kill's second-monster case
    /// over (C2; #167 D2: kept for the whole fight when it opens within SecondMonsterMilliseconds of that kill,
    /// FollowsAddKill, and no break-off refusal logged yet), the pet's lead from now (F10c), the walk on the think before
    /// (#167 walk=, LastWalk), and no monster on the bot yet for the fight's buff (#167 D12, FightBuffCheck). True for the
    /// first fight since a release (BackPending, cleared): PLAYER_BOT_BACK then (#167 C1; the review of 9aacd69: with
    /// Engage's ReportBack dropped no test failed).</summary>
    public static bool OpenFight(PlayerBotState state, long now, byte health, byte mana, int distance, byte targetHealth)
    {
        state.EngagedSince = now;
        state.EngageHealth = health;
        state.EngageMana = mana;
        state.FightAdds = state.Swings = state.NotVisible = state.FightCasts = state.FightHeals = 0;
        state.FightPetCasts = state.FightWaits = state.FightControls = state.FightKites = state.FightCtlBroken = 0;
        state.FightKiteNone = default;                                              // #167 D8 kiteNone=
        state.FightDots = 0;                                                        // #167 E C1 dots=
        state.FightKillingQuickcast = false;                                        // #167 E C2b qcKill=
        state.FightTargetMezzes = 0;                                                // #167 E C4 ctlMez=
        state.FightRaceLeft = NoRace;                                               // #167 E-L raceLeft=
        (state.FightStylesHeld, state.FightCastsHeld) = (0, 0);                     // #167 E-R styleHeld=, castHeld=,
        (state.Reserve, state.NextReserveTick) = (0, 0);                            //   and a fresh reserve read
        state.TargetHeldBreaks = false;                                             // #167 C3
        state.FightAddControls = 0;                                                 // #167 C4
        (state.FightAdd, state.EngageDistance) = ((-1, -1), distance);              // #167 FIGHT addAt=, engageDist=
        state.NextAddScanTick = 0;                                                  //   and a fresh add scan (ScanComing)
        state.EngageTargetHealth = targetHealth;                                    // #167 C11
        state.ControlTries = 0;                                                     // #167 F11
        state.NextKiteTick = 0;
        state.FollowsAddKill = SecondMonsterCase(state.KilledWithAdds, state.LastKillTick, now);   // #167 D2: kept
        state.KilledWithAdds = false;                                               // #167 C2: the kill's case over
        state.BreakOffRefused = false;                                              // #167 D2: a refusal's line afresh
        state.FightBuffHitters = 0;                                                 // #167 D12: none on it before
        state.FightQuickcast = false;
        state.PetOrderTick = now;                                                   // #167 F10c
        state.FightWalk = state.LastWalk;                                           // #167 st-deathsD's fields: walk=,
        (state.FightHeldMs, state.FightCtlLanded, state.FightCtlFailed) = (0, 0, 0);   //   the think before's, and the
        (state.FightHpAtReach, state.FightTargetHpAtReach, state.WouldBreakHp) = (-1, -1, -1);   //   counts afresh
        state.PendingControl = default;                                             //   (a cast still awaited: neither)
        bool back = state.BackPending;                                              // #167 C1: the first since a release
        state.BackPending = false;
        return back;
    }

    /// <summary>A think's start (#167 FIGHT and DEATH walk=, F12d's field): no walk yet, the last think's kept (LastWalk).
    /// Each walk names itself as it steps (Walk); OpenFight reads the last think's: the walk on the think before the
    /// fight's first. It touches neither PlayerBotPathMover nor PlayerTravelPath.</summary>
    public static void NextWalk(PlayerBotState state) => (state.LastWalk, state.Walk) = (state.Walk, PlayerBotWalk.None);

    /// <summary>#167 rate=: a think's health, the last HealthRateMilliseconds of them kept (Think, each think alive; a new
    /// life drops them, StartLife).</summary>
    public static void SampleHealth(Queue<(long Tick, byte Health)> samples, long now, byte health)
    {
        while (samples.Count > 0 && now - samples.Peek().Tick > HealthRateMilliseconds) samples.Dequeue();
        samples.Enqueue((now, health));
    }

    /// <summary>#167 rate=: the health lost a second since the oldest think of the last 3 s (samples, oldest first), this
    /// think's own left out; 0 with none. A gain (a heal, regeneration) is negative.</summary>
    public static double HealthRate(IEnumerable<(long Tick, byte Health)> samples, long now, byte health)
    {
        foreach ((long tick, byte was) in samples)
            if (tick < now && now - tick <= HealthRateMilliseconds) return (was - health) * 1000.0 / (now - tick);
        return 0;
    }

    /// <summary>#167 wouldBreak=, logged only: breakoff #5's projected trigger (d-breakoff/projected.py's P6). Below
    /// BreakOffHealthPercent, or below the health ProjectedSeconds of the loss (rate, HealthRate) take, 95 at most: the
    /// seconds of loss a run would keep in hand to clear the chasers' reach.</summary>
    public static bool ProjectedBreak(byte health, double rate) =>
        health < Math.Max(BreakOffHealthPercent, Math.Min(95, rate * ProjectedSeconds));

    /// <summary>The race since reach (#167 E-L): its target's health when the bot dies, at both sides' loss since the first
    /// hit on the bot (hpAtReach, targetHpAtReach: FIGHT hpAtReach= and tHpAtReach=, TrackFight): targetHealth less what
    /// the target loses while the bot loses the health it has left. A heal shows as less lost. Null with no hit yet or
    /// under RaceReadLoss lost since; a target that lost nothing keeps its health.</summary>
    public static int? TargetLeftAtDeath(byte health, byte targetHealth, int hpAtReach, int targetHpAtReach)
    {
        int botLost = hpAtReach - health, targetLost = targetHpAtReach - targetHealth;
        if (hpAtReach < 0 || targetHpAtReach < 0 || botLost < RaceReadLoss) return null;
        return targetHealth - health * Math.Max(0, targetLost) / botLost;
    }

    /// <summary>The race lost (#167 E-L, OneMonsterCase's losingRace): its target projected above RaceLossMargin when the
    /// bot dies (TargetLeftAtDeath). On straight paths the projection holds from the read to the end, so it never fires
    /// on a fight the bot wins (rest E1's "target ahead" rule gave up 23 wins, 8 of them defences); with hits in chunks
    /// it reads high on some (the review's model: about 45 non-caster wins over two legs).</summary>
    public static bool LosingRace(byte health, byte targetHealth, int hpAtReach, int targetHpAtReach) =>
        TargetLeftAtDeath(health, targetHealth, hpAtReach, targetHpAtReach) > RaceLossMargin;

    /// <summary>The fight's target health as the break-off reads it (#167 C2): its target's while it lives, else (at a kill)
    /// the nearest hitter's (BreakOffFires, BreakOffStartWhy).</summary>
    public static byte FightTargetHealth(GameLiving target, IReadOnlyList<(byte Health, int Distance, bool Avoided)> hitters)
    {
        if (target?.IsAlive == true) return target.HealthPercent;
        byte health = 0;
        for (int i = 0, nearest = int.MaxValue; i < hitters.Count; i++)
            if (hitters[i].Distance < nearest) (health, nearest) = (hitters[i].Health, hitters[i].Distance);
        return health;
    }

    /// <summary>#167 hpAtReach=: an attack on the bot within the last second (lastHit:
    /// GameLiving.LastAttackedByEnemyTick, set on an attack that causes combat, GameLiving.cs:1515-1535: a miss, block,
    /// parry or evade too, AttackData.IsHit, AttackData.cs:44-53, and a resisted spell, SpellHandler.cs:2582-2594; 0,
    /// none since login).</summary>
    public static bool HitNow(long lastHit, long now) => lastHit > 0 && now - lastHit <= 1_000;

    /// <summary>#167 ctlLanded=: a control cast asked for at requestedAt held, its effect (spellId) the bot's own (Mine) and
    /// begun since (StartTick, ECSGameEffect.cs:65) on its target; else ctlFailed= (resisted, refused, interrupted).</summary>
    public static bool ControlLanded(IEnumerable<(int SpellId, bool Mine, long StartTick)> effects, int spellId,
        long requestedAt) =>
        effects.Any(effect => effect.Mine && effect.SpellId == spellId && effect.StartTick >= requestedAt);

    /// <summary>#167 ctlLanded=: a control cast settles (SettleControl) once its cast time and a think have passed since it
    /// was asked for and the bot casts no more (one queued behind another cast goes later).</summary>
    public static bool ControlResolves(long now, long requestedAt, int castTime, bool casting) =>
        !casting && now >= requestedAt + castTime + TickMilliseconds;

    /// <summary>A fight think (#167, st-deathsD's FIGHT and DEATH fields, Engage's WatchFight): heldSec= by think while its
    /// target is held (targetHeld: Hold), hpAtReach= and tHpAtReach= at the first think with a hit on the bot (HitNow),
    /// wouldBreak= at the first the projected break-off would fire (ProjectedBreak over HealthRate), and #167 E-L's
    /// raceLeft= at the first think under BreakOffHealthPercent with a race read (TargetLeftAtDeath).</summary>
    public static void TrackFight(PlayerBotState state, long now, bool targetHeld, byte health, byte targetHealth, long lastHit)
    {
        if (targetHeld) state.FightHeldMs += TickMilliseconds;
        if (state.FightHpAtReach < 0 && HitNow(lastHit, now))
            (state.FightHpAtReach, state.FightTargetHpAtReach) = (health, targetHealth);
        if (state.WouldBreakHp < 0 && ProjectedBreak(health, HealthRate(state.HealthSamples, now, health)))
            state.WouldBreakHp = health;
        if (state.FightRaceLeft == NoRace && health < BreakOffHealthPercent &&
            TargetLeftAtDeath(health, targetHealth, state.FightHpAtReach, state.FightTargetHpAtReach) is int left)
            state.FightRaceLeft = left;
    }

    /// <summary>PLAYER_BOT_BACK replan= (#167 C1): notOwed when the release owed no replan (OwesReplan); else moved once
    /// a camp was planned since, none when not (none found, or the hold ran out, or still owed at the fight). #167 D4, D9:
    /// same when that camp was the anchor the bot had (sameAnchor, SameCamp): 37 of st-deathsC's 174 died replans.</summary>
    public static string BackReplan(bool owed, bool planned, bool sameAnchor) =>
        !owed ? "notOwed" : !planned ? "none" : sameAnchor ? "same" : "moved";

    /// <summary>A camp planned at the anchor the bot had (#167 BACK replan=same): its spot within SameCampDistance of it,
    /// as d-after-release/a3_samespot.py counts "same anchor".</summary>
    public static bool SameCamp(Vector3 spot, Vector3 anchor) =>
        Vector3.DistanceSquared(spot, anchor) <= SameCampDistance * SameCampDistance;

    /// <summary>Where a relocation plans from (PlanRelocation's here): after a death (Died) the spot the bot died at, not
    /// its bind; else where it is (after a break-off, #167 C2, where it ran to).</summary>
    public static Vector3 RelocationOrigin(string reason, Vector3 deathSpot, Vector3 here) =>
        reason == Died ? deathSpot : here;

    /// <summary>#167 E W1: a death within a GameBot's failure memory (FailureMemoryMilliseconds, 15 minutes); sinceDeathMs
    /// -1: none since login.</summary>
    public static bool RecentDeath(long sinceDeathMs) =>
        sinceDeathMs >= 0 && sinceDeathMs < AutonomousDeathRecoveryPolicy.FailureMemoryMilliseconds;

    /// <summary>#167 E W1: a plan after a death: a died, brokeoff or capped plan within RecentDeath. It looks near the
    /// bot first (PlanCampChoice). An outgrown plan is left alone: it leaves a camp it outgrew for one at its level
    /// (st-deathsD2/D2b: 10k+ outgrown plans died first 6 of 55).</summary>
    public static bool AfterDeathPlan(string reason, long sinceDeathMs) =>
        reason is Died or BrokeOff or Capped && RecentDeath(sinceDeathMs);

    /// <summary>#167 E W1: the camp a plan takes. After a death (afterDeath, AfterDeathPlan) it first looks near the bot
    /// (tryNear): nearMonsters (the zone it stands in) from here, within NearWalk, no fallback over NearFallbackMax
    /// (BLUE), never a camp it failed at (PlanRelocation's maxWalk); a fresh one is taken (Near). Else the plan as
    /// before: monsters (the plan's zone) from from (RelocationOrigin).</summary>
    public static (T[] Spots, ConColor? Fallback, bool Stale, bool Near) PlanCampChoice<T>(
        IReadOnlyList<(T Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> nearMonsters,
        IReadOnlyList<(T Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters, Vector3 here, Vector3 from,
        ConColor cap, ConColor natural, IReadOnlyCollection<Vector3> failedCamps, ISet<T> rejected, int count,
        ConColor? killerCon, bool afterDeath, bool tryNear = true) where T : class
    {
        if (afterDeath && tryNear && nearMonsters != null)
        {
            (T[] spots, ConColor? fallback, bool stale) = PlanRelocation(nearMonsters, here, cap, natural, failedCamps,
                rejected, count, killerCon, natural < NearFallbackMax ? natural : NearFallbackMax, NearWalk);
            if (spots.Length > 0 && !stale) return (spots, fallback, false, true);
        }
        (T[] far, ConColor? farFallback, bool farStale) = PlanRelocation(monsters, from, cap, natural, failedCamps, rejected,
            count, killerCon);
        return (far, farFallback, farStale, false);
    }

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
    /// release restores health, power and endurance (GamePlayer.cs:1723-1725): no rest to full. #167 D4: while the
    /// release's immunity lasts with a camp planned and a point toward it (immuneWalk), it walks out toward the camp
    /// first (WalkOut), as no monster may attack it then (AbstractServerRules.cs:354; StandardMobBrain.cs:582, :675): the
    /// safest seconds of the life were spent standing, held for the replan.</summary>
    public static PlayerBotRecovery AfterReleaseStep(bool attacked, bool stepsOut, bool replanOwed, bool backingOff, bool ill,
        bool immuneWalk) =>
        attacked ? PlayerBotRecovery.Fight : immuneWalk ? PlayerBotRecovery.WalkOut :
        stepsOut ? PlayerBotRecovery.StepOut : replanOwed ? PlayerBotRecovery.Replan :
        backingOff || ill ? PlayerBotRecovery.Wait : PlayerBotRecovery.Done;

    /// <summary>The walk out's point (#167 D4): of the clear points (out of every reach) within maxDistance of here, the
    /// one that leaves the least distance to the goal, the camp planned (progress, not detour: with the least detour, every
    /// point on the line to the camp tied and the nearest ring won); on a tie, the one nearest here. With no goal, the
    /// nearest. None within maxDistance: null. Recomputed on each arrival while the immunity lasts.</summary>
    public static Vector3? StepOutPoint(Vector3 here, Vector3? goal, IReadOnlyList<Vector3> clearPoints, float maxDistance)
    {
        Vector3? best = null;
        float bestLeft = 0, bestNear = 0;
        foreach (Vector3 point in clearPoints)
        {
            float near = Vector3.Distance(here, point);
            if (near > maxDistance) continue;
            float left = goal is Vector3 camp ? Vector3.Distance(point, camp) : 0;
            if (best == null || left < bestLeft || left == bestLeft && near < bestNear)
                (best, bestLeft, bestNear) = (point, left, near);
        }
        return best;
    }

    /// <summary>Whether the walk out goes on to point (#167 D4): in a monster's reach (hereClear false) to any; from a clear
    /// spot only to one that leaves less to the goal. The rings start 500 units out, so at the camp's edge it stepped
    /// from clear point to clear point until the immunity ran out.</summary>
    public static bool WalksOut(Vector3 here, bool hereClear, Vector3? goal, Vector3? point) =>
        point is Vector3 next && (!hereClear || goal is Vector3 camp && Vector3.Distance(next, camp) < Vector3.Distance(here, camp));

    /// <summary>The release's immunity's end (#167 D4): the server's (m_invulnerabilityTick) is protected
    /// (GamePlayer.cs:12236). Release's Released event (GamePlayer.cs:1752) starts TIMER_KILLED_BY_MOB seconds
    /// (AbstractServerRules.cs:258-262, :280-285; ServerProperties.cs:1273-1274), from the release the bot asked for
    /// (askedAt: the ask that revived it, the think before its first alive), else from that first think (releasedAt). An
    /// ask that came earlier than the release that took gives an earlier end: a shorter walk, never one past it.</summary>
    public static long ImmunityEnd(long askedAt, long releasedAt, int immunitySeconds) =>
        (askedAt != 0 ? askedAt : releasedAt) + immunitySeconds * 1000L;

    /// <summary>The immunity left (#167 D4): to its end (ImmunityEnd) while the server still holds the bot immune
    /// (invulnerable, GamePlayer.cs:12284-12287); none on a PvE server, which starts none (GamePlayer.cs:12249-12250).</summary>
    public static long ImmunityLeft(long immuneUntil, long now, bool invulnerable) =>
        invulnerable ? Math.Max(0, immuneUntil - now) : 0;

    /// <summary>How far the walk out reaches in the immunity left (#167 D4): that time at the bot's run, or its sprint
    /// (RunSpeed, SprintSpeed).</summary>
    public static float WalkOutReach(long leftMs, bool sprinting) => leftMs * (sprinting ? SprintSpeed : RunSpeed) / 1000f;

    /// <summary>The walk out's sprint (#167 D4): on when a walk out starts with the endurance to start one (over
    /// SprintStartEndurance, GamePlayer.cs:8633), off when the walk ends (walkOutSprint: its own is on). A sprint drains
    /// endurance whenever the bot moves (GamePlayer.cs:2397-2425): left on, the walk on to the camp ended near 5%. A
    /// break-off's sprint is its own. Null: no change.</summary>
    public static bool? SprintChange(bool walkingOut, bool walkOutSprint, int endurance) =>
        walkingOut ? (walkOutSprint || endurance <= SprintStartEndurance ? null : true) : walkOutSprint ? false : null;

    /// <summary>Whether the bot breaks off and sprints out of reach (#167 C2): losing to two. 2 or more monsters on it, or
    /// right after a kill in a fight that had 2 or more on it with 1 still on it (addFight: the second-monster case, k12's
    /// rule at the kill, which would have saved 28 of 45 such deaths for 9 of 114 won follow-ups; #167 D2: and the whole
    /// fight after it); below BreakOffHealthPercent, its target (at the kill, the one still on it) above
    /// BreakOffTargetPercent, no control ready for the add (C4's comes first), and only from chasers it outruns (Outruns).
    /// #167 D1: one monster in the one-monster case too (oneMonster, OneMonsterCase); in an ordinary fight, it fights on.
    /// </summary>
    public static bool BreaksOff(int monstersOnBot, bool addFight, bool oneMonster, byte health, byte targetHealth,
        bool addControlReady, bool outruns) =>
        (monstersOnBot >= 2 || (addFight || oneMonster) && monstersOnBot >= 1) && health < BreakOffHealthPercent &&
        targetHealth > BreakOffTargetPercent && !addControlReady && outruns;

    /// <summary>The one-monster case (#167 D1): one monster breaks the bot off when it is a name the bot avoids
    /// (avoidedOnBot: a death's or a break-off's, IsAvoided; the avoidance stopped the pull, never a monster that aggroed:
    /// one such monster was fatal in 31 of st-deathsC's deaths, 22 begun at 95% health or more), unless the bot was sent to
    /// a camp of such names (huntAvoided: then it hunts them); or when the bot is a caster losing to it with its target
    /// above CasterBreakOffTargetPercent (the caster trap: 41 deaths, the monster reached the bot, and at death the target
    /// was above 25% in 33 of them); #167 E-L: or when a bot that is no caster is losing the race to it (losingRace,
    /// LosingRace: 113 non-caster deaths on st-deathsD2 and D2b, 105 with no break-off). A caster keeps D1's arm: its
    /// target at 25% or less is C2's killing nuke's, which the break-off (Think's step before Engage) would pre-empt. Not
    /// while a hold on its target is ready (holdReady, HoldReady): the hold first.</summary>
    public static bool OneMonsterCase(PlayerBotRole role, bool avoidedOnBot, bool huntAvoided, byte targetHealth,
        bool holdReady, bool losingRace) =>
        !holdReady && (avoidedOnBot && !huntAvoided || role == PlayerBotRole.Caster && targetHealth > CasterBreakOffTargetPercent ||
                       role != PlayerBotRole.Caster && losingRace);

    /// <summary>A caster spent (#167 D1): no damage spell it can pay for (canNuke false), its target held (a root it would
    /// stand off while it waits for power: 2 of st-deathsC's out-of-power deaths waited 190 and 168 times) and above
    /// CasterBreakOffTargetPercent. It breaks off at any health (StartsBreakOff), then rests (FinishBreakOff).</summary>
    public static bool CasterSpent(bool canNuke, bool held, byte targetHealth) =>
        !canNuke && held && targetHealth > CasterBreakOffTargetPercent;

    /// <summary>A caster's hold on its target ready (#167 D1, OneMonsterCase's holdReady): the target held now, or a control
    /// CasterStep would cast at it now (hasControl: TargetControl's), which in melee with hits interrupting the bot
    /// (interrupted) goes only with Quickcast ready or on, as CasterAction casts one.</summary>
    public static bool HoldReady(bool held, bool hasControl, bool interrupted, bool quickcastReady, bool quickcastOn) =>
        held || hasControl && (!interrupted || quickcastReady || quickcastOn);

    /// <summary>The one-monster case's holdReady (#167 D1; the review of D): a control cast at the one monster on the
    /// bot still under way (controlCasting, ControlCastAt: TargetControl reads none while it casts, NextControlTick, so
    /// the case fired mid-cast and the break-off's Disengage cancelled the root, CastingComponent.InterruptCasting);
    /// else, only when that monster is the bot's target (itsTarget; at a kill, the one on it), the target's hold ready
    /// (targetHoldReady, HoldReady; asked only then: it walks the spells). A target held at range beside another
    /// monster hitting the bot is no hold on that one; an add's control to come is C4's (AddControlReady,
    /// BreakOffFires).</summary>
    public static bool OneMonsterHold(bool controlCasting, bool itsTarget, Func<bool> targetHoldReady) =>
        controlCasting || itsTarget && targetHoldReady();

    /// <summary>A control cast at npc under way (#167 D1, the review of D): the bot's cast now (spell, at castTarget;
    /// none: null) a control (IsControlSpell) at that monster, CasterStep's at its target or AddAct's at an
    /// add.</summary>
    public static bool ControlCastAt(Spell spell, GameLiving castTarget, GameLiving npc) =>
        spell != null && castTarget == npc && IsControlSpell(spell);

    /// <summary>When the break-off reads the control CasterStep would cast (#167 D1, the review of D): at the end of
    /// the last control's cast and margin (nextControlTick, ControlMarginMilliseconds) when that is later, so a
    /// resisted root's margin before its next cast keeps the hold first; else now.</summary>
    public static long ControlReadTick(long now, long nextControlTick) => Math.Max(now, nextControlTick);

    /// <summary>Whether StartsBreakOff scans the monsters on the bot (#167 D1; the review of D pinned it): under
    /// BreakOffHealthPercent, or a spent caster (CasterSpent, SpentNow) at any health. No scan above it.</summary>
    public static bool BreakOffScans(bool spent, byte health) => spent || health < BreakOffHealthPercent;

    /// <summary>A break-off's start once StartsBreakOff scanned (#167 D1; the review of D pinned it): BREAKOFF why=
    /// (BreakOffWhy) for a spent caster at any health (spent), with no monster on it too, else when BreakOffFires holds
    /// over the monsters on the bot (hitters) with the one-monster case's holdReady (HoldReadyNow); none,
    /// null.</summary>
    public static string BreakOffStartWhy(PlayerBotState state, PlayerBotRole role, bool spent, byte health, bool holdReady,
        IReadOnlyList<(byte Health, int Distance, bool Avoided)> hitters, long now, Func<bool> addControlReady)
    {
        if (!spent && !BreakOffFires(state, role, holdReady, health, hitters, now, addControlReady)) return null;
        bool avoidedOnBot = false;
        for (int i = 0; i < hitters.Count; i++) avoidedOnBot |= hitters[i].Avoided;
        return BreakOffWhy(spent, hitters.Count, SecondMonsterCase(state.KilledWithAdds, state.LastKillTick, now),
            state.Engaged && state.FollowsAddKill, avoidedOnBot, state.HuntAvoided,
            role == PlayerBotRole.Caster && FightTargetHealth(state.Target, hitters) > CasterBreakOffTargetPercent);
    }

    /// <summary>BREAKOFF why= (#167 D1): the rule that fired. Spent (CasterSpent); else 2 or more monsters on the bot (adds);
    /// the second-monster case at the kill (atkill) or in the fight after it (followup, D2's FollowsAddKill); one monster
    /// of a name it avoids, not in a camp of them (avoided); a caster losing to one with its target above 25% (caster,
    /// casterArm); else #167 E-L's race lost to one (race).</summary>
    public static string BreakOffWhy(bool spent, int monstersOnBot, bool atKill, bool followUp, bool avoidedOnBot,
        bool huntAvoided, bool casterArm) =>
        spent ? Spent : monstersOnBot >= 2 ? ByAdds : atKill ? AtKill : followUp ? FollowUp :
        avoidedOnBot && !huntAvoided ? AvoidedName : casterArm ? CasterLosing : RaceLost;

    /// <summary>The dropped FIGHT line's adds= at a break-off (#167 D2, StartsBreakOff): BreaksOffNow returns before Engage
    /// folds the attackers into FightAdds, so the monsters on the bot (onBot, BREAKOFF attackers=) fold in here; with 2 or
    /// more and no add seen yet (add.At -1), the add came now: the whole seconds into the fight (fightMs) and the bot's
    /// health (FIGHT addAt= and addHp=). One on it, or an add seen, it stays as it was.</summary>
    public static (int Adds, (int At, int Hp) Add) FoldBreakOffAdds(int fightAdds, (int At, int Hp) add, int onBot,
        long fightMs, byte health) =>
        (Math.Max(fightAdds, onBot), add.At >= 0 || onBot < 2 ? add : ((int)(fightMs / 1000), health));

    /// <summary>The second-monster case (#167 C2): a kill in a fight that had 2 or more on the bot (killedWithAdds,
    /// SettleTarget), for SecondMonsterMilliseconds from it (lastKillTick). The review of 9aacd69: the mark held until the
    /// next fight's first think, and a bot that sat at 35% broke off from one monster that jumped it mid-rest.</summary>
    public static bool SecondMonsterCase(bool killedWithAdds, long lastKillTick, long now) =>
        killedWithAdds && now - lastKillTick < SecondMonsterMilliseconds;

    /// <summary>A break-off's first rule as StartsBreakOff applies it (#167 C2; then the chasers' speed, Outruns, and a
    /// point to run to): BreaksOff over the monsters on the bot (hitters: each one's health and distance, and #167 D1
    /// whether its name is one the bot avoids), its target's health while it lives, else (at a kill) the nearest hitter's
    /// (FightTargetHealth), the second-monster case (SecondMonsterCase; #167 D2: or the fight after it, FollowsAddKill,
    /// while engaged), #167 D1's one-monster case (OneMonsterCase: role and holdReady passed in, the UT fixture has no
    /// CharacterClass) with #167 E-L's race (LosingRace, engaged with its target alive: the race read since the first hit
    /// is that target's) and C4's add control (addControlReady, asked only once the rest holds: it scans).</summary>
    public static bool BreakOffFires(PlayerBotState state, PlayerBotRole role, bool holdReady, byte health,
        IReadOnlyList<(byte Health, int Distance, bool Avoided)> hitters, long now, Func<bool> addControlReady)
    {
        byte targetHealth = FightTargetHealth(state.Target, hitters);
        bool avoidedOnBot = false;
        for (int i = 0; i < hitters.Count; i++) avoidedOnBot |= hitters[i].Avoided;
        bool addFight = SecondMonsterCase(state.KilledWithAdds, state.LastKillTick, now) || state.Engaged && state.FollowsAddKill;
        bool losingRace = state.Engaged && state.Target?.IsAlive == true &&                                // #167 E-L
            LosingRace(health, targetHealth, state.FightHpAtReach, state.FightTargetHpAtReach);
        bool oneMonster = OneMonsterCase(role, avoidedOnBot, state.HuntAvoided, targetHealth, holdReady, losingRace);
        return BreaksOff(hitters.Count, addFight, oneMonster, health, targetHealth, addControlReady: false, outruns: true) &&
            BreaksOff(hitters.Count, addFight, oneMonster, health, targetHealth, addControlReady(), outruns: true);
    }

    /// <summary>Whether the bot outruns its fastest chaser (#167 C2): the sprint's lead on it, sprintSeconds x (SprintSpeed
    /// - v), clears its melee reach (ChaserReach; the review of 9aacd69: with no sprint, endurance 10 or less, the bot
    /// turned its back on a 191 or 185 chaser that started in its reach, and it swung at the bot's back for the 30 s of
    /// the break-off; a sprint is the old canSprint); and the chaser is no faster than its run (RunSpeed: the world DB sets
    /// enable_pve_speed off, MaxSpeedCalculator.cs:52-57, so after the sprint it runs at 191), or under its sprint
    /// (SprintSpeed) while the sprint's lead holds it off for the ChaseSeconds a monster chases after its last swing: from
    /// melee, sprintSeconds of sprint keep a chaser of speed v from a swing for sprintSeconds x 57 / (v - 191) seconds (the
    /// verifier's model). #167 D2: never from a chaser that harms from range (rangedHarm, RangedHarm).</summary>
    public static bool Outruns(int fastestSpeed, int sprintSeconds, bool rangedHarm) =>
        !rangedHarm && (long)sprintSeconds * (SprintSpeed - fastestSpeed) > ChaserReach &&
        (fastestSpeed <= RunSpeed || fastestSpeed < SprintSpeed &&
         (long)sprintSeconds * (SprintSpeed - RunSpeed) >= (long)ChaseSeconds * (fastestSpeed - RunSpeed));

    /// <summary>A chaser that harms the run from range (#167 D2, Outruns): one of its spells (GameNPC.Spells, GameNPC.cs:3379)
    /// a snare, a damage spell, a lifedrain, a stun or a mez at RangedHarmRange or more. All 6 of st-deathsC's break-offs
    /// from monsters with a 1,500-range snare, DoT or lifedrain died (grovewood, rainbow sprite), against 4 of the other
    /// 58: they hit the running bot's back.</summary>
    public static bool RangedHarm(IEnumerable<(eSpellType Type, int Range)> spells)
    {
        foreach ((eSpellType type, int range) in spells)
            if (range >= RangedHarmRange && type is eSpellType.SpeedDecrease or eSpellType.DamageSpeedDecrease or
                    eSpellType.UnbreakableSpeedDecrease or eSpellType.DirectDamage or eSpellType.Lifedrain or
                    eSpellType.DamageOverTime or eSpellType.Bolt or eSpellType.Stun or eSpellType.Mesmerize)
                return true;
        return false;
    }

    /// <summary>Why a break-off that would fire doesn't (#167 D2, BREAKOFF outcome=refused why=), in StartsBreakOff's order:
    /// none when it doesn't fire (fires: its rule held before the add's control); an add's control ready first (C4,
    /// control); a chaser as fast as the sprint (speed) or a sprint too short for the fastest (sprint, Outruns); a chaser
    /// that harms from range (ranged, RangedHarm); nowhere to run (point). None: it breaks off.</summary>
    public static string BreakOffRefusal(bool fires, bool controlReady, int fastest, int sprintSeconds, bool ranged,
        bool hasPoint) =>
        !fires ? null : controlReady ? "control" : fastest >= SprintSpeed ? "speed" :
        !Outruns(fastest, sprintSeconds, rangedHarm: false) ? "sprint" : ranged ? "ranged" : !hasPoint ? "point" : null;

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

    /// <summary>The least endurance whose sprint outruns a chaser of this speed (#167 E-R: Outruns over SprintSeconds, at
    /// the bot's regeneration, fatigue consumption and Long Wind), from just over SprintStartEndurance to
    /// MaxEscapeReserve; none within it, int.MaxValue. 26 against 191 (4 s of sprint), 31 against 200, 41 against 205,
    /// 21 against 150 (3 s).</summary>
    public static int EscapeEndurance(int fastestSpeed, int regeneration, int fatigueConsumption, int longWind)
    {
        for (int endurance = SprintStartEndurance + 1; endurance <= MaxEscapeReserve; endurance++)
            if (Outruns(fastestSpeed, SprintSeconds(endurance, regeneration, fatigueConsumption, longWind), rangedHarm: false))
                return endurance;
        return int.MaxValue;
    }

    /// <summary>The endurance a close fight keeps for a run (#167 E-R): EscapeEndurance against the fastest monster, read
    /// at the run's 191 at the least (a choice, not the arithmetic: a 150-180 chaser needs 21, and the floor keeps 5 more
    /// in hand), and ReserveMargin; 0 when that passes MaxEscapeReserve (a 205-or-faster monster: no reserve it could keep
    /// buys a run, so it keeps its styles).</summary>
    public static int EscapeReserve(int fastestSpeed, int regeneration, int fatigueConsumption, int longWind)
    {
        int need = EscapeEndurance(Math.Max(fastestSpeed, RunSpeed), regeneration, fatigueConsumption, longWind);
        return need <= MaxEscapeReserve - ReserveMargin ? need + ReserveMargin : 0;
    }

    /// <summary>A style's endurance cost as StyleProcessor.CalculateEnduranceCost charges it (#167 E-R;
    /// StyleProcessor.cs:460-479, private): a Battlemaster style 30 x cost / 40, any other weaponSpeed x cost / 40 and
    /// 1 more under speed 40, scaled by the fatigue consumption (percent), 1 at the least. Valhalla's Blessing's free
    /// proc (a 1.108 realm ability) is left out.</summary>
    public static int StyleEnduranceCost(int weaponSpeed, int styleCost, int fatigueConsumption, bool battlemaster)
    {
        int cost = battlemaster ? 30 * styleCost / 40 : weaponSpeed * styleCost / 40 + (weaponSpeed < 40 ? 1 : 0);
        return Math.Max(1, (int)Math.Ceiling(cost * fatigueConsumption * 0.01));
    }

    /// <summary>Whether a style of this cost leaves the bot's endurance at the reserve or above (#167 E-R, StyleWithin).
    /// With no reserve, only whether it can pay (StyleProcessor's own check).</summary>
    public static bool KeepsReserve(int endurance, int cost, int reserve) => endurance - cost >= reserve;

    /// <summary>QueueUsableStyle's choice (#167 E-R; the review's seam): of the usable styles by level, highest first
    /// (costs: each one's StyleEnduranceCost), the highest-level one that leaves the reserve (KeepsReserve); with no
    /// reserve, today's pick, the highest whatever it costs (the server drops one the bot can't pay for at the swing,
    /// PlayerAttackAction.cs:36-42); none, -1. Held: the reserve left the swing unstyled though the bot could pay for a
    /// style (FIGHT styleHeld=), not a choice with none it could pay for (the fatigue the reserve didn't cause).</summary>
    public static (int Index, bool Held) StyleWithin(IReadOnlyList<int> costs, int endurance, int reserve)
    {
        if (costs.Count == 0) return (-1, false);
        if (reserve <= 0) return (0, false);
        bool payable = false;
        for (int i = 0; i < costs.Count; i++)
        {
            if (KeepsReserve(endurance, costs[i], reserve)) return (i, false);
            payable |= endurance >= costs[i];
        }
        return (-1, payable);
    }

    /// <summary>Whether a hybrid's damage cast waits for the reserve (#167 E-R, Engage's cast gate): a reserve kept, the
    /// cast's 5 would take the bot under it, and the bot is no caster or pet class (their spells are their fight, and
    /// they style little). A heal is never held: it is SelfHeal's, not this gate's. #167 E fix R2: nor a cast that heals
    /// its caster (healsCaster, HealsCaster: a lifetap). Reaver deaths rose from 3 on st-deathsD2 and D2b to 11 on
    /// st-deathsE and Eb, 7 of them with the Siphon held (7-117 thinks, 5 of them 78-117, down to 0 endurance).</summary>
    public static bool HoldsCast(PlayerBotRole role, int endurance, int reserve, bool healsCaster) =>
        !healsCaster && reserve > 0 && role is not (PlayerBotRole.Caster or PlayerBotRole.Pet) &&
        endurance - SpellEnduranceCost < reserve;

    /// <summary>A cast that returns health to its caster (#167 E fix R2, HoldsCast's healsCaster): a lifedrain's StealLife
    /// (LifedrainSpellHandler.cs; LifedrainNoVariance's is the same), an omni-lifedrain's (OmniLifedrainSpellHandler.cs)
    /// and a damage snare's with a return (DamageSpeedDecrease.cs:32-33, its NoVariance kind), at a LifeDrainReturn above
    /// 0: the Reaver's Siphons (60-70) and the Valewalker's Cycle of Life, Cycle of Death and Force of Nature (40-50). A
    /// drain to power (DamageToPower) returns no health.</summary>
    public static bool HealsCaster(eSpellType type, int lifeDrainReturn) =>
        lifeDrainReturn > 0 && type is eSpellType.Lifedrain or eSpellType.LifedrainNoVariance or eSpellType.OmniLifedrain or
            eSpellType.DamageSpeedDecrease or eSpellType.DamageSpeedDecreaseNoVariance;

    /// <summary>Engage's cast gate on the spell the bot will cast (#167 E fix R2, its review's seam): HoldsCast with that
    /// spell's HealsCaster, so a lifetap is never held.</summary>
    public static bool HoldsDamageCast(PlayerBotRole role, int endurance, int reserve, Spell spell) =>
        HoldsCast(role, endurance, reserve, HealsCaster(spell.SpellType, spell.LifeDrainReturn));

    /// <summary>The reserve a hold keeps now (#167 E fix R2): the close fight's (reserve, ReserveNow's read) only while
    /// holding can still buy a run, else 0 (no hold: the style and cast as before). Not under the run's need against the
    /// fastest monster (runNeed, EscapeEndurance): endurance comes back neither in a fight nor moving
    /// (ClassicRestRegeneration.cs), so no hold buys a run from under it (26 of st-deathsE and Eb's 31 held in-fight deaths
    /// were under it at death, 15 in the fight after a caught run). Not while its target, the only monster on the bot
    /// (targetAlone), is at BreakOffTargetPercent or less: no break-off fires from it then (BreaksOff) and none is left to
    /// run from at the kill (2 held deaths alone with the target at 1-3%; no run on the E legs could have needed it). With
    /// another monster on the bot it holds on: the run at the kill (why=atkill, followup) needs it (21 such runs after a
    /// held kill, 18 alive). The threshold: 10 changes those 2 deaths and no run; 15 adds 1 death and puts race runs from
    /// a bot near death at risk (fires at targets of 13-15); 20 to 30 change no death more.</summary>
    public static int HoldingReserve(int reserve, int runNeed, int endurance, byte targetHealth, bool targetAlone) =>
        reserve > 0 && endurance >= runNeed && !(targetAlone && targetHealth <= BreakOffTargetPercent) ? reserve : 0;

    /// <summary>The bot's health at its target's death on the race since the first hit on it (#167 E-R, CloseFight):
    /// health less what it loses while its target loses the health it has left, both at their loss since that hit
    /// (hpAtReach, targetHpAtReach: FIGHT hpAtReach= and tHpAtReach=). Null with no hit yet or under RaceReadLoss
    /// lost since (TargetLeftAtDeath's read); int.MinValue when the target lost nothing since.</summary>
    public static int? HealthLeftAtKill(byte health, byte targetHealth, int hpAtReach, int targetHpAtReach)
    {
        int botLost = hpAtReach - health, targetLost = targetHpAtReach - targetHealth;
        if (hpAtReach < 0 || targetHpAtReach < 0 || botLost < RaceReadLoss) return null;
        return targetLost <= 0 ? int.MinValue : health - targetHealth * botLost / targetLost;
    }

    /// <summary>A close fight (#167 E-R), the one that keeps a run's reserve: 2 or more monsters on the bot now or in the
    /// fight (fightAdds, FIGHT adds=), a defence or the fight after an add kill (defence), or the race projecting the
    /// bot under CloseRacePercent at its target's death (healthLeftAtKill; none read yet, no). 14% of st-deathsD's
    /// style-class fights on straight-line races, about 18% with hits in chunks (the review); an ordinary pull keeps its
    /// styles.</summary>
    public static bool CloseFight(int monstersOnBot, int fightAdds, bool defence, int? healthLeftAtKill) =>
        monstersOnBot >= 2 || fightAdds >= 2 || defence || healthLeftAtKill < CloseRacePercent;

    /// <summary>ReserveNow's read (#167 E-R; #167 E fix R2, its review's seam), over the monsters attacking the bot
    /// (onBot: each one's speed and whether it is the bot's target) and its target's speed (targetSpeed, on the bot or
    /// not; 0 with none alive): the close fight's reserve (CloseFight on onBot's count, EscapeReserve against the
    /// fastest), the run's need against that fastest chaser itself (RunNeed: EscapeEndurance, not EscapeReserve's 191
    /// floor; 0 with no reserve) and whether no monster but the target is on the bot (TargetAlone), HoldingReserve's
    /// terms.</summary>
    public static (int Reserve, int RunNeed, bool TargetAlone) ReserveRead(int targetSpeed,
        IReadOnlyList<(int Speed, bool IsTarget)> onBot, int fightAdds, bool defence, int? leftAtKill, int regeneration,
        int fatigue, int longWind)
    {
        int others = 0, fastest = targetSpeed;
        for (int i = 0; i < onBot.Count; i++)
            (others, fastest) = (onBot[i].IsTarget ? others : others + 1, Math.Max(fastest, onBot[i].Speed));
        int reserve = CloseFight(onBot.Count, fightAdds, defence, leftAtKill)
            ? EscapeReserve(fastest, regeneration, fatigue, longWind)
            : 0;
        return (reserve, reserve > 0 ? EscapeEndurance(fastest, regeneration, fatigue, longWind) : 0, others == 0);
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

    /// <summary>Where a break-off runs with no ring point (#167 D2): straight away from its chasers' middle, distance out,
    /// at here's height, adds or not; none with no chaser (or none to run from). With no point it turned and fought: 6
    /// of st-deathsC's 13 caught runs.</summary>
    public static Vector3? StraightAwayPoint(Vector3 here, IReadOnlyList<Vector3> chasers, float distance)
    {
        Vector2 away = Vector2.Zero;
        for (int i = 0; i < chasers.Count; i++) away += new Vector2(here.X - chasers[i].X, here.Y - chasers[i].Y);
        if (away.LengthSquared() < 1) return null;
        away = Vector2.Normalize(away) * distance;
        return new(here.X + away.X, here.Y + away.Y, here.Z);
    }

    /// <summary>The straight-away point as the break-off takes it (#167 D2; the verifier's correction): on the navmesh
    /// (onMesh), not within BreakOffBlockedRadius of a point it was blocked on, and with a walk there (walkable, the
    /// corridor test NextBreakOffPoint uses); one with none is blocked too. One try: else null, and it fights as before
    /// (the island loop of 9aacd69 stays fixed).</summary>
    public static Vector3? StraightAwayGoal(Vector3 here, IReadOnlyList<Vector3> chasers, float distance,
        Func<Vector3, Vector3?> onMesh, List<Vector3> blocked, Func<Vector3, bool> walkable)
    {
        if (StraightAwayPoint(here, chasers, distance) is not Vector3 away || onMesh(away) is not Vector3 point ||
            blocked.Exists(b => Vector3.DistanceSquared(b, point) <= BreakOffBlockedRadius * BreakOffBlockedRadius))
            return null;
        if (walkable(point)) return point;
        blocked.Add(point);
        return null;
    }

    /// <summary>A break-off's point 800 out (#167 E T1; RelaxedBreakOffRings) when its rings and the straight-away point
    /// found none (BreakOffGoal's relaxed pass, from the same add scan): the ring's point (candidates) ahead of the
    /// chasers and out of every other monster's reach (NextBreakOffPoint), else the point straight away 800 out
    /// (StraightAwayGoal) only where no other monster's reach lies (InAddReach): a run into an add is worse than holding
    /// (the review). None, null: it holds or turns (NoPointTurns). st-deathsD2/D2b: 11 of 14 turns for want of a point were
    /// fatal.</summary>
    public static Vector3? NearBreakOffGoal<T>(Vector3 here, IReadOnlyList<Vector3> chasers, IReadOnlyList<Vector3> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, List<Vector3> blocked, Func<Vector3, bool> walkable,
        Func<Vector3, Vector3?> onMesh) where T : class =>
        NextBreakOffPoint(here, chasers, candidates, adds, blocked, walkable) ??
        StraightAwayGoal(here, chasers, RelaxedBreakOffRings[0],
            point => onMesh(point) is Vector3 on && !InAddReach(on, adds) ? on : null, blocked, walkable);

    /// <summary>With no point to run to (#167 E T1; BreakOffGoal's passes found none), whether the bot turns now: a chaser
    /// not held in reach (freeDistance within CaughtReach; none, int.MaxValue), a hit while it holds (hitNow,
    /// HitWhileHolding: a chaser that harms from range, or one held but casting, never comes within it), or a caster or
    /// pet class that can pay for a damage spell (casterOrPetWithPower, read after a try failed) at any distance: it opens
    /// at range. With no chaser (nearest int.MaxValue) there is none to turn on. Else it holds where it stands.
    /// st-deathsD2/D2b: 11 of 14 no-point turns were fatal; 7 of the 11 opened with a gap a ranged class could use, and
    /// the 2 casters that turned on chasers 1,400 away lived (the review).</summary>
    public static bool NoPointTurns(int freeDistance, bool hitNow, bool casterOrPetWithPower, int nearest) =>
        nearest != int.MaxValue && (freeDistance <= CaughtReach || hitNow || casterOrPetWithPower);

    /// <summary>Hit while it holds (#167 E T1, NoPointTurns): an attack on the bot (lastHit, HitNow's: within the last second,
    /// a miss too) since its hold began (holdSince; 0: not holding). One before the hold is the run's (CaughtNow).</summary>
    public static bool HitWhileHolding(long holdSince, long lastHit, long now) =>
        holdSince != 0 && lastHit >= holdSince && HitNow(lastHit, now);


    /// <summary>A break-off's step this think (#167 C2): with no monster still after it, Done; at its point (or blocked)
    /// with one still after it, NextPoint; else Run. #167 D2: caught (CaughtNow), Caught: it acts on the chaser; from
    /// BreakOffMilliseconds with its nearest chaser (nearestChaser, a held one too) beyond ShakenDistance, Done; still
    /// chased BreakOffMaxMilliseconds after it began, Fight (it turns on them). At the old 30-s cap it turned on any chaser
    /// still targeting it, however far behind (a monster keeps its target 25 s after its last swing,
    /// StandardMobState.cs:122): 7 of st-deathsC's 13 caught runs.</summary>
    public static PlayerBotBreakOff BreakOffStep(bool arrived, int chasers, long sinceMs, int nearestChaser, bool caught) =>
        chasers == 0 ? PlayerBotBreakOff.Done : caught ? PlayerBotBreakOff.Caught :
        sinceMs >= BreakOffMilliseconds && nearestChaser > ShakenDistance ? PlayerBotBreakOff.Done :
        sinceMs >= BreakOffMaxMilliseconds ? PlayerBotBreakOff.Fight :
        arrived ? PlayerBotBreakOff.NextPoint : PlayerBotBreakOff.Run;

    /// <summary>A break-off's sprint on its run (#167 E-S): off while sprinting with the nearest chaser not held
    /// (nearestFree; none, int.MaxValue) at least SprintOffGap behind and the ground the fastest chaser gains on the run's
    /// 191 in the ChaseSeconds it chases (fastest); on again with one within SprintOnGap and the endurance to start one
    /// (over SprintStartEndurance, GamePlayer.cs:8633). Null: no change. Left on to the run's end, a run ended near 5%
    /// and the next fight's break-off was refused why=sprint (breakoff E3: 15 deaths).</summary>
    public static bool? BreakOffSprint(bool sprinting, int nearestFree, int fastest, int endurance) =>
        sprinting ? (nearestFree >= SprintOffGap + (long)ChaseSeconds * Math.Max(0, fastest - RunSpeed) ? false : null)
            : nearestFree < SprintOnGap && endurance > SprintStartEndurance ? true : null;

    /// <summary>Caught on a break-off's run (#167 D2): a chaser not held within CaughtReach (nearestChaser) since
    /// inReachSince (InReachSince; 0: none) for CaughtMilliseconds. Health fell in 9 of st-deathsC's 12 caught runs, and
    /// 10 of the 13 died within 60 s, 6 within 6 s. st-deathsD's fix round: the clock started with the chaser still in
    /// contact as the run began (runStart), and the sprint gains 57 a second at most on a 191 chaser (Outruns), so it
    /// fired 2 s in: 89 of 98 break-offs ended fought at sec=2, 80 died within 60 s, and health fell during only 31 of 87
    /// runs. So caught is a chaser still attacking the bot once the sprint had time to build a lead:
    /// CaughtGraceMilliseconds into the run, the clock no earlier than the grace's end, and an attack on the bot
    /// (lastHitOnBot: HitNow's LastAttackedByEnemyTick; 0, none) within CaughtHitMilliseconds. That tick is any attack
    /// that causes combat, a miss, block, parry, evade or resisted spell too (HitNow), not only one that took health:
    /// the chaser is in contact.</summary>
    public static bool CaughtNow(int nearestChaser, long runStart, long inReachSince, long lastHitOnBot, long now) =>
        now - runStart >= CaughtGraceMilliseconds && nearestChaser <= CaughtReach &&
        inReachSince >= runStart + CaughtGraceMilliseconds && now - inReachSince >= CaughtMilliseconds &&
        lastHitOnBot > 0 && now - lastHitOnBot <= CaughtHitMilliseconds;

    /// <summary>The caught clock (#167 D2, CaughtNow): kept while a chaser not held is within CaughtReach (nearestChaser),
    /// started now when one comes in, 0 when none is; none inside the run's grace (runStart, CaughtGraceMilliseconds:
    /// st-deathsD's fix round).</summary>
    public static long InReachSince(long since, int nearestChaser, long runStart, long now) =>
        nearestChaser > CaughtReach || now - runStart < CaughtGraceMilliseconds ? 0 : since != 0 ? since : now;

    /// <summary>The caught clock's think (#167 D2, BreaksOffNow's; st-deathsD's fix round): the clock (CaughtSince,
    /// InReachSince) on the nearest chaser not held (freeDistance) from the run's start (BreakOffSince), and caught
    /// (CaughtNow) with the last attack on the bot (its LastAttackedByEnemyTick, HitNow's).</summary>
    public static bool CaughtThink(PlayerBotState state, GameLiving bot, int freeDistance, long now)
    {
        state.CaughtSince = InReachSince(state.CaughtSince, freeDistance, state.BreakOffSince, now);
        return CaughtNow(freeDistance, state.BreakOffSince, state.CaughtSince, bot.LastAttackedByEnemyTick, now);
    }

    /// <summary>Whether a monster stays a break-off's chaser (#167 D2, KeepChasers): it has the bot as its target
    /// (targetsBot) within ChaseRadius (distance), held or not. A mezzed or stunned monster keeps its target and comes back
    /// (CrowdControlECSEffect.cs:26-37 stops its attack only): dropped (OnBot), 2 of the 41 "shook" died within 10 s.
    /// A held one counts in no reach (CaughtNow, WeakestInReach).</summary>
    public static bool KeepsChaser(bool targetsBot, int distance) => targetsBot && distance <= ChaseRadius;

    /// <summary>#167 E T2b: a break-off done (BreakOffStep's Done) runs on to its point instead of stopping where it is when
    /// it has one it hasn't reached (hasGoal, arrived), under BreakOffMaxMilliseconds, and it stands in another monster's
    /// reach (inAddReachHere, StandsInAddReach: read once, runningOn after). A chaser that gave up stands in its own aggro
    /// reach, so this reaches it too (the review). st-deathsD2/D2b: a fight opened within 3 s of 10 of 135 shook ends, 7
    /// of them fatal.</summary>
    public static bool RunsOnToClear(bool hasGoal, bool arrived, long sinceMs, bool runningOn, Func<bool> inAddReachHere) =>
        hasGoal && !arrived && sinceMs < BreakOffMaxMilliseconds && (runningOn || inAddReachHere());

    /// <summary>Whom a caught bot turns on with no control to cast (#167 D2): the chaser within reach, not held, with the
    /// least health (a monster under 33% health also moves slower, MaxSpeedCalculator.cs:142-143); none, null.</summary>
    public static T WeakestInReach<T>(IReadOnlyList<(T Npc, byte Health, int Distance, bool Held)> chasers, int reach)
        where T : class
    {
        T best = null;
        int least = int.MaxValue;
        foreach ((T npc, byte health, int distance, bool held) in chasers)
            if (!held && distance <= reach && health < least) (best, least) = (npc, health);
        return best;
    }

    /// <summary>Whether a death or a break-off (reason) bars the anchor too (#167 D9): on the walk to it (walkingToAnchor,
    /// RoamDestination the anchor, as the hold reads it) more than RoamRadius from it (fromAnchor), where the bar of the
    /// spot it died at or ran from (RememberFailedCamp) leaves it a camp (PlanRelocation's Failed). In st-deathsC's 21 died
    /// replans that kept the anchor (Pbotaaacv aside) the death lay over 1,500 from it, and the first fights there were
    /// fatal 10 of 21, against 38 of 128 after a new camp; 6 of the 7 break-off relocations back to the old anchor came on
    /// the walk to it.</summary>
    public static bool BarsOldAnchor(string reason, bool walkingToAnchor, float fromAnchor) =>
        (reason == Died || reason == BrokeOff) && walkingToAnchor && fromAnchor > RoamRadius;

    /// <summary>The anchor's bar (BarsOldAnchor) for a death or a break-off at at, in the anchor's region, for a GameBot's
    /// failure memory (RememberFailedCamp; #167 D9).</summary>
    public static void BarOldAnchor(PlayerBotState state, string reason, Vector3 at, long now)
    {
        if (BarsOldAnchor(reason, state.RoamDestination == state.Anchor, Vector3.Distance(at, state.Anchor)))
            RememberFailedCamp(state.FailedCamps, state.AnchorRegion, state.Anchor, now);
    }

    /// <summary>A break-off's start (#167 C2): the camp it ran from and the names of its target and chasers, remembered as
    /// after a death (Remember, DEATHS.md A3; RememberFailedCamp, #167 F4), so it neither pulls them nor plans back
    /// there; on the walk to its anchor, that too (BarOldAnchor, #167 D9). #167 D1: none of it for a spent caster's
    /// (BreakOffWhy Spent): the power was the problem, not the camp or the names.</summary>
    public static void BrokeOffFrom(PlayerBotState state, ushort region, Vector3 at, IEnumerable<string> names, long now)
    {
        if (state.BreakOffWhy == Spent) return;
        foreach (string name in names) Remember(state.AvoidUntil, name, now);
        RememberFailedCamp(state.FailedCamps, region, at, now);
        BarOldAnchor(state, BrokeOff, at, now);                                    // #167 D9: and the anchor it walked to
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
    /// OwesReplan's rule); fought, no new break-off for BreakOffMilliseconds: it fights them out. #167 D1: a spent
    /// caster's owes no relocation but a rest (RecoverBeforePull): it was short of power, not of a camp. #167 E T1, T2b:
    /// the turn, the holds, the retry, the speeds and the run-on go with the run.</summary>
    public static void FinishBreakOff(PlayerBotState state, string outcome, bool owesReplan, long now)
    {
        bool spent = state.BreakOffWhy == Spent;
        (state.BreakOffSince, state.BreakOffGoal, state.BreakOffArrived) = (0, null, false);
        (state.BreakOffWhy, state.CaughtSince) = (null, 0);
        (state.BreakOffTurn, state.BreakOffHolds, state.NextBreakOffRetryTick) = (null, 0, 0);   // #167 E T1
        (state.BreakOffHoldSince, state.BreakOffHoldWhy) = (0, null);
        (state.BreakOffSpeed, state.BreakOffChaserSpeed, state.BreakOffRunsOn) = (-1, -1, false);   // #167 E T2b: the run-on
        state.BreakOffChasers.Clear();
        state.BreakOffBlocked.Clear();
        if (outcome == Died) return;
        if (outcome == Fought) state.NextBreakOffTick = now + BreakOffMilliseconds;
        if (spent) state.RecoverBeforePull = true;
        else AfterBreakOff(state, owesReplan, now);
    }

    /// <summary>Where a target respawns and the reach it aggroes from then (#167 C7), read at the pick: its spawn spot
    /// (GameNPC.SpawnPoint; RespawnTimerCallback puts it back there, GameNPC.cs:3278-3280), not where it stands (the
    /// verifier's correction: a defence pick often stands far from it; the review of 9aacd69: read from the monster
    /// here, as a spot passed in could be either), and its aggro range when it would aggro the bot at all
    /// (IsPotentialAdd: it respawns busy with no one); else none (Reach 0).</summary>
    public static (Vector3 At, int Reach) RespawnOf(GameNPC npc, int aggroLevel, int aggroRange, bool greyToBot) =>
        npc?.SpawnPoint is Point3D spawn && IsPotentialAdd(aggroLevel, aggroRange, greyToBot, false)
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

    /// <summary>The spots a bot in a potential add's reach (InAddReach) may walk to before it sits (#167 D10): the clear
    /// candidates nearest first (ClearSpots), RestSpot's the first; none when it stands clear. ClearSpotNear keeps
    /// RestSpot's single spot (SpotOutOfReach).</summary>
    public static List<Vector3> RestSpots<T>(Vector3 here, IEnumerable<Vector3> candidates,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds) where T : class =>
        InAddReach(here, adds) ? ClearSpots(here, candidates, adds) : new List<Vector3>();

    /// <summary>The rest walk's goal after blocked walks (#167 D10): the next clear spot (clear: RestSpots', nearest first),
    /// maxTries in all (RestSpotTries); then none, and the bot sits where it is.</summary>
    public static Vector3? RestGoalAt(IReadOnlyList<Vector3> clear, int blocked, int maxTries) =>
        blocked < Math.Min(clear.Count, maxTries) ? clear[blocked] : null;

    /// <summary>The rest walk's goal and blocks after a step toward goal (#167 D10, WalksToRestSpot): blocked, the next spot
    /// (RestGoalAt) and one block more; arrived, none (it sits there); walking or waiting, the same.</summary>
    public static (Vector3? Goal, int Blocked) AfterRestStep(PlayerBotStepResult step, Vector3 goal,
        IReadOnlyList<Vector3> spots, int blocked) => step switch
    {
        PlayerBotStepResult.Blocked => (RestGoalAt(spots, blocked + 1, RestSpotTries), blocked + 1),
        PlayerBotStepResult.Arrived => (null, blocked),
        _ => (goal, blocked),
    };

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

    /// <summary>The hold a stun or mez gives a monster now (#167 E C4): its duration (ms), or what the NPC's diminishing
    /// immunity leaves of it (NpcImmunityEffect.CalculateNewEffectDuration, ECSImmunityEffect.cs:46-51: a half the second
    /// time, a quarter the third); none on it, the whole duration.</summary>
    public static long HoldNowMs(NpcImmunityEffect immunity, int duration) =>
        immunity == null ? duration : immunity.CalculateNewEffectDuration(duration);

    /// <summary>The step back's walk (#167 E C4): from the bot's distance to its point (KiteRange: the stop less
    /// KiteStopMargin) at RunSpeed; nothing to walk, 0. From melee (150) to a 1,420 stop: 1,220 units, 6,387 ms. It
    /// assumes the walk to the stop: with no clear point KitePoint walks KiteFallbackDistance (400, about 1.3 s), which
    /// the stun outlasts, so the mez goes first where it wasn't needed (the review's note).</summary>
    public static long StepBackWalkMs(int stop, int distance) =>
        KiteRange(stop, distance) is int range ? (range - distance) * 1000L / RunSpeed : 0;

    /// <summary>Whether the escape's mez goes before the stun (#167 E C4): hits on the bot (hitsOnBot) from its target
    /// alone (soleHitter: CasterAction then steps back from a stunned target too; with an add's hits CasterHolds is false,
    /// no step back comes and the stun holds through the nukes, where the next nuke would end a mez) and the stun's hold
    /// ends before that step back's walk (walkMs, StepBackWalkMs) and the margin, so the monster wakes as the bot walks
    /// with its back to it. A Mentalist's mez (16-80 s) outlasts it, and the bot opens again from its stop as at a
    /// pull.</summary>
    public static bool MezBeforeStun(bool hitsOnBot, bool soleHitter, long stunHoldMs, long walkMs) =>
        hitsOnBot && soleHitter && stunHoldMs < walkMs + ControlMarginMilliseconds;

    /// <summary>The control CasterStep casts at its target (#167 E C4): ChooseControl's target order (a stun, a root, a mez
    /// as the escape), with the mez before the stun when the stun's hold now (holdMs: HoldNowMs) ends before the step
    /// back's walk (MezBeforeStun); no mez it may cast, the stun. allowed and ready as TargetControl gives them.</summary>
    public static (Spell, SpellLine) ChooseTargetControl(IEnumerable<(Spell Spell, SpellLine Line)> controls,
        Func<Spell, bool> ready, Func<Spell, bool> allowed, Func<Spell, long> holdMs, bool hitsOnBot, bool soleHitter,
        long walkMs)
    {
        (Spell Spell, SpellLine Line) choice = ChooseControl(controls, ready, allowed, forAdd: false, petClass: false);
        if (choice.Spell?.SpellType is eSpellType.Stun && MezBeforeStun(hitsOnBot, soleHitter, holdMs(choice.Spell), walkMs))
        {
            (Spell Spell, SpellLine Line) mez = ChooseControl(controls, ready,
                spell => spell.SpellType is eSpellType.Mesmerize && allowed(spell), forAdd: false, petClass: false);
            if (mez.Spell != null) return mez;
        }
        return choice;
    }

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
    /// nukesCast)); nothing measured (no cast, or no health off it yet), int.MaxValue. #167 E C1: a DoT counts as one
    /// cast. While its ticks are due it under-reads the cast (more nukes needed: the safe side); once they have landed it
    /// over-reads the next direct nuke, as a bolt does (on the casters design's slot model, Mentalist slots with 1 needed
    /// but 2 or more direct nukes short: 0 of 134 before, 5 of 80 with the DoTs; the review's r3).</summary>
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
    /// needs (NukesNeeded, from its health at the fight's first think) land first (KillsFirst, one more with Quickcast
    /// ready or already on: #167 E C2a, once on it is no longer "ready" (QuickcastReady) and the killing nuke lost the
    /// Quickcast it turned on to the control at the next think), the bot at KillFirstHealthPercent or more or its nuke
    /// killing at once (#167 E C2b: oneNukeKills, OneNukeKills; E2's 13 died with the target on 2-15%, and the 60% gate
    /// put the target's escape before C11's killing nuke), and one monster at most on it.</summary>
    public static bool KillsFirstNow(int engageHealth, int health, int casts, long contactMs, int nukeCastMs,
        bool quickcastReady, bool quickcastOn, bool oneNukeKills, byte botHealth, int attackers) =>
        KillsFirst(NukesNeeded(engageHealth, health, casts), contactMs, nukeCastMs, quickcastReady || quickcastOn) &&
        (botHealth >= KillFirstHealthPercent || oneNukeKills) && attackers <= 1;

    /// <summary>Whether one nuke kills the target (#167 E C2b, the review's test, not NukesNeeded's 1, which a bolt or
    /// a landed DoT over-reads): its hit at the variance minimum, less the resist (minimumHit: ExpectedHit with min for
    /// both bounds), at least the target's health (hit points) less its own DoTs' ticks still due (pendingDot,
    /// PendingDotDamage).</summary>
    public static bool OneNukeKills(double minimumHit, double health, double pendingDot) => minimumHit >= health - pendingDot;

    /// <summary>Engage's cast choice and whether it kills at once (#167 E C2b; the review of the batch, risk-1: judged on
    /// the nuke the bot will cast). Alone in a caster's fight (solo: one monster at most on it) with Quickcast on or due
    /// (quickcastOnOrDue), the nuke it has any power for (anyPower: Nuke under the killing quickcast, as the server lets
    /// it cast then) when that kills (kills: OneNukeKills from its handler, NukeKillsNow); else the cast it can pay for
    /// (paid: CombatSpell), and whether that kills (solo only). Before, the any-power nuke was judged at every cast choice
    /// while the bot cast the paid one: KillsFirstNow's health-gate bypass and the killing quickcast, which Engage reads
    /// every think from that verdict, gave the Quickcast and the control's reserve to a nuke that didn't kill. One Nuke
    /// pass in the common case, two when the any-power nuke under Quickcast doesn't kill.</summary>
    public static (Spell Spell, SpellLine Line, bool OneNukeKills) KillingNukeChoice(bool solo, bool quickcastOnOrDue,
        Func<(Spell, SpellLine)> anyPower, Func<(Spell, SpellLine)> paid, Func<Spell, bool> kills)
    {
        if (solo && quickcastOnOrDue && anyPower() is (Spell any, SpellLine anyLine) && any != null && kills(any))
            return (any, anyLine, true);
        (Spell spell, SpellLine line) = paid();
        return (spell, line, solo && spell != null && kills(spell));
    }

    /// <summary>Whether the bot's next nuke is the killing one under Quickcast (#167 E C2b): a caster in a fight
    /// (caster), its chosen nuke kills at once (oneNukeKills, OneNukeKills), one monster at most on it (attackers: the
    /// review), and the cast goes under Quickcast (on, or ready with hits interrupting the bot: quickcastDue). Then it
    /// keeps no control in reserve (ControlReserve) and a nuke it has any power for is ready (NukeReady): the server
    /// checks only power left under Quickcast (SpellHandler.cs:771-776, :978-984).</summary>
    public static bool KillingQuickcast(bool caster, bool oneNukeKills, bool quickcastOn, bool quickcastDue, int attackers) =>
        caster && oneNukeKills && attackers <= 1 && (quickcastOn || quickcastDue);

    /// <summary>Whether a control on the target is worth its cost (#167 D11): a BLUE or harder, the bot under
    /// KillFirstHealthPercent, or 2 or more live monsters on it (attackers: LiveHitters); a GREEN otherwise gets none, as a
    /// player nukes a GREEN down. 143 of st-deathsC's 347 GREEN wins had a control, 22.1 s and 40.9 power each against 7.9
    /// s and 19.2 without one, and 106 of the 143 spent Quickcast. CasterStep and TargetFirst both read it (the verifier's
    /// correction: a GREEN-plus-add fight held the add's control back for a target control that never came).</summary>
    public static bool ControlWorth(ConColor con, byte botHealth, int attackers) =>
        con >= ConColor.BLUE || botHealth < KillFirstHealthPercent || attackers >= 2;

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

    /// <summary>The step back's points in turn (#167 C3, D8): KiteStep's, each turn (KiteTurns) out of every potential
    /// add's reach (Clear), not only the first; then, the bot in melee (inMelee), the point straight away from the target
    /// KiteFallbackDistance out, adds or not (inf.: a null left it in the held monster's reach, where its hits stopped
    /// every cast), which C3 took only with none clear. Out of melee with none clear, none, as before. The caller
    /// (KiteWalk) puts each on the navmesh and checks a clear one's place there against the adds again. With one
    /// monster, a swing at a held target needs a blocked step back (stepBackBlocked, set only when KitePoint found
    /// none): 24 of st-deathsC's 43 ctlBroken fights had one monster, no step back and no break-off, and a clear point
    /// existed at 97-99% of the death spots (inf.: the database's spawn spots).</summary>
    public static List<(Vector3 Point, bool Clear)> KiteCandidates<T>(Vector3 bot, Vector3 target, int range,
        IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds, bool inMelee) where T : class
    {
        List<(Vector3 Point, bool Clear)> points = new(KiteTurns.Length + 1);
        Vector2 away = new(bot.X - target.X, bot.Y - target.Y);
        double heading = away.LengthSquared() < 1 ? 0 : Math.Atan2(away.Y, away.X);
        foreach (int turn in KiteTurns)
        {
            double angle = heading + turn * Math.PI / 180;
            Vector3 point = new(target.X + (float)(Math.Cos(angle) * range), target.Y + (float)(Math.Sin(angle) * range), bot.Z);
            if (!InAddReach(point, adds)) points.Add((point, true));
        }
        if (inMelee) points.Add((KiteStep(bot, target, KiteFallbackDistance, Array.Empty<(T, Vector3, int)>()).Value, false));
        return points;
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

    /// <summary>A step back's flat range for a point the navmesh put dz above or below the target (#167 D8): the flat
    /// distance that keeps it within the reach KiteRange keeps (the stop less KiteStopMargin) in 3D, rounded up (KiteFits
    /// leaves KiteStopMargin - KiteArriveDistance of slack), only when that is farther out than the bot stands
    /// (distance); else none. KiteFits's 30 units of 3D slack rule out a point about 287 units above or below the target at
    /// 1,370 out: KiteWalk pulls such a one in once to this.</summary>
    public static int? KiteRangeFor(int stop, int dz, int distance)
    {
        double reach = stop - KiteStopMargin, flat = reach * reach - (double)dz * dz;
        if (flat <= 0) return null;
        int range = (int)Math.Ceiling(Math.Sqrt(flat));
        return range > distance ? range : null;
    }

    /// <summary>The step back's point as KitePoint takes it (#167 D8): each candidate in turn (KiteCandidates) on the
    /// navmesh (onMesh), a clear one's place there out of the adds' reach again (InAddReach), then within the stop in 3D
    /// (KiteFits); one that misses on the height is pulled in once (KiteRangeFor, toward the target from the candidate,
    /// distance: the bot's from the target) and tried again. At most KiteMeshCalls navmesh calls. None: why, its nearest
    /// miss: fit (one on the mesh, clear, but too high or low), else clear (the mesh moved it into an add's reach, or no
    /// candidate at all), else mesh (none on the mesh).</summary>
    public static (Vector3? Point, string Why) KiteWalk<T>(IReadOnlyList<(Vector3 Point, bool Clear)> candidates, Vector3 target,
        int stop, int distance, Func<Vector3, Vector3?> onMesh, IReadOnlyList<(T Npc, Vector3 At, int AggroRange)> adds)
        where T : class
    {
        int calls = 0, miss = candidates.Count == 0 ? 1 : 0;                           // 0 mesh, 1 clear, 2 fit
        foreach ((Vector3 candidate, bool clear) in candidates)
        {
            Vector3 point = candidate;
            for (bool pulled = false; calls < KiteMeshCalls; pulled = true)
            {
                calls++;
                if (onMesh(point) is not Vector3 m) break;
                if (clear && InAddReach(m, adds))
                {
                    miss = Math.Max(miss, 1);
                    break;
                }
                if (KiteFits(m, target, stop)) return (m, null);
                miss = 2;
                Vector2 flat = new(candidate.X - target.X, candidate.Y - target.Y);
                if (pulled || KiteRangeFor(stop, (int)(m.Z - target.Z), distance) is not int range || range >= flat.Length())
                    break;
                flat = Vector2.Normalize(flat) * range;                                 // pulled in toward the target
                point = new(target.X + flat.X, target.Y + flat.Y, candidate.Z);
            }
            if (calls >= KiteMeshCalls) break;
        }
        return (null, miss switch { 2 => KiteMissFit, 1 => KiteMissClear, _ => KiteMissMesh });
    }

    /// <summary>FIGHT and DEATH kiteNone= (#167 D8): a step back that found no point, counted by why (KiteMissRange:
    /// KiteRange had none; else KiteWalk's).</summary>
    public static (int Range, int Clear, int Mesh, int Fit) KiteNoneCounted((int Range, int Clear, int Mesh, int Fit) counts,
        string why) => why switch
    {
        KiteMissRange => counts with { Range = counts.Range + 1 },
        KiteMissClear => counts with { Clear = counts.Clear + 1 },
        KiteMissMesh => counts with { Mesh = counts.Mesh + 1 },
        _ => counts with { Fit = counts.Fit + 1 },
    };

    /// <summary>A monster coming at the bot (#167 F7): it may attack it, it has the bot as its target and it is within
    /// IncomingRadius; not one Engage found unreachable while its mark lasts (unreachable), between scans too.</summary>
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
    /// at once. Null for an outgrown bot, and for one that passed over only names it avoids: they expire. #167 D6: null
    /// too for a con over the cap's limit after the lowest that killed the bot (lowestKillerCon, LowestKillerCon;
    /// KillerLimit: YELLOW or harder, never under GREEN).</summary>
    public static ConColor? FallbackInPlace(string reason, ConColor? safestPassedOver, ConColor? lowestKillerCon) =>
        reason == Capped && (KillerLimit(lowestKillerCon) is not ConColor limit || safestPassedOver <= limit)
            ? safestPassedOver : null;

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
    /// that median as Fallback, the con the bot hunts there (AWBC:3446-3452). #167 D6: no fallback camp whose median is
    /// over the cap's limit after killerCon, the lowest con that killed the bot in the last 5 minutes (KillerLimit: under
    /// a YELLOW or harder one, never under GREEN; none: no camp). #167 E W1: a near pass bounds it (maxWalk: only
    /// monsters within it of here; 0, none) and then takes no failed camp at all; fallbackMax (none: natural) is the
    /// hardest fallback median.</summary>
    public static (T[] Spots, ConColor? Fallback, bool Stale) PlanRelocation<T>(
        IReadOnlyList<(T Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters, Vector3 here, ConColor cap,
        ConColor natural, IReadOnlyCollection<Vector3> failedCamps, ISet<T> rejected, int count, ConColor? killerCon = null,
        ConColor? fallbackMax = null, int maxWalk = 0)
        where T : class
    {
        Dictionary<(int, int), List<int>> grid = new();
        for (int i = 0; i < monsters.Count; i++)
        {
            if (monsters[i].Con <= ConColor.GREY) continue;
            (int, int) cell = CampCell(monsters[i].At);
            if (!grid.TryGetValue(cell, out List<int> list)) grid[cell] = list = new();
            list.Add(i);
        }
        float reach = (float)maxWalk * maxWalk;                                          // #167 E W1
        int[] order = Enumerable.Range(0, monsters.Count)
            .Where(i => monsters[i].Huntable && monsters[i].Con > ConColor.GREY && !rejected.Contains(monsters[i].Npc) &&
                (maxWalk <= 0 || Vector3.DistanceSquared(monsters[i].At, here) <= reach))
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
        ConColor most = fallbackMax ?? natural;                                          // #167 E W1
        List<(int I, ConColor Median, bool Fresh, bool Failed)> safe = new();
        foreach (int i in order)
        {
            (ConColor median, ConColor fresh) = CampCon(monsters, grid, i);
            if (median <= most && (KillerLimit(killerCon) is not ConColor limit || median <= limit))   // #167 D6
                safe.Add((i, median, fresh <= natural, Failed(i)));
        }
        if (safe.Count == 0) return (Array.Empty<T>(), null, false);
        bool stale = !safe.Any(c => c.Fresh);
        IEnumerable<(int I, ConColor Median, bool Fresh, bool Failed)> pool = stale ? safe : safe.Where(c => c.Fresh);
        if (pool.Any(c => !c.Failed)) pool = pool.Where(c => !c.Failed);
        else if (maxWalk > 0) return (Array.Empty<T>(), null, false);                   // #167 E W1: near, never a failed one
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

    /// <summary>#167 E M2: the bot's TargetInView (AttackComponent.cs:1062-1070, SpellHandler.cs:450): the navmesh's 2D ray
    /// (los, HeadlessLos), or without it its target fighting it (fightsBot: FightsBot) in contact, within CaughtReach
    /// (distance): a client in contact sees what hits it. Not at range (the review): a ranged monster casting at a ranged
    /// bot from behind a wall is no sight. st-deathsD2/D2b: 5 deaths in fights with half the swings or more refused for
    /// sight (98 of 111, 344 of 345), every killer the bot's own target in contact.</summary>
    public static bool SightInReach(bool los, int distance, bool fightsBot) =>
        los || fightsBot && distance <= CaughtReach;

    /// <summary>#167 E M2: out of sight since (0: in sight): kept while out of sight, now when it goes.</summary>
    public static long BlindClock(long since, bool inView, long now) => inView ? 0 : since != 0 ? since : now;

    /// <summary>#167 E M2: a target out of sight BlindDropMilliseconds (blindSince, BlindClock) that fights neither the bot
    /// (fightsBot) nor its pet (petOnTarget), nor comes at it (targetsBot: it has the bot as its target), is dropped. 49
    /// stuck fights stood 2,390 s, 250 s at one cluricaun, swinging at nothing. One with the bot as its target is kept
    /// (the review): Incoming, which BATCH-E leaves as it is, took it back at the next think and 5 s on it was dropped
    /// again, a FIGHT line each time; it is a monster coming at the bot round an obstacle, or fighting it.</summary>
    public static bool DropsBlindTarget(long blindSince, long now, bool fightsBot, bool petOnTarget, bool targetsBot) =>
        blindSince != 0 && now - blindSince >= BlindDropMilliseconds && !fightsBot && !petOnTarget && !targetsBot;

    /// <summary>#167 E M2: a target dropped for want of sight (DropBlind), or one an Animist left for want of a turret in
    /// reach (CY3's skip, LeaveTurretTarget), is marked for the pick alone (BlindSkips, until UnreachableSkipMilliseconds
    /// on): the review, a blocked walk's unreachable mark (LeaveUnreachable) made self-defence (Attacker) and the
    /// monster coming at it (Incoming) pass it over too, so one that came round and hit the bot was no hitter for 30 s,
    /// and it overwrote a real unreachable mark. #167 E R1: each marked one keeps its own time (BlindSkips; marked again,
    /// from then): with one mark, a second drop forgot the first and the pick went back to it (st-deathsE/Eb: 213 drops
    /// of a name dropped again within 30 s with another between, A, B, A). The marks past their time go as a new one
    /// comes.</summary>
    public static void MarkBlind(PlayerBotState state, GameLiving target, long now) => Mark(state.BlindSkips, target, now);

    /// <summary>#167 E M2: whether the pick (FindWithin, and only it) passes over a monster: one MarkBlind marked, while
    /// its time lasts (#167 E R1: every one marked, BlindSkips).</summary>
    public static bool BlindSkipped(PlayerBotState state, GameLiving npc, long now) => Marked(state.BlindSkips, npc, now);

    /// <summary>#167 E U1, Engage's blocked walk to its target (no corridor leads there): the target is left and marked
    /// unreachable for its own UnreachableSkipMilliseconds (Unreachables; marked again, from then), as R1 made the blind
    /// marks. One mark (Unreachable) kept only the last: st-deathsE2b, Pbotmaada stood on a pocket of the navmesh in
    /// Muspelheim from which no corridor leads to its camp, and its picks every 2 s went A, B, A, each walk blocked, for
    /// the last 3.5 minutes of the leg (two hitters out of reach would take turns at self-defence the same way, one each
    /// think). The marks past their time go as a new one comes.</summary>
    public static void LeaveUnreachable(PlayerBotState state, GameLiving target, long now)
    {
        Mark(state.Unreachables, target, now);
        state.Target = null;
    }

    /// <summary>#167 E U1: whether a monster is marked unreachable now (LeaveUnreachable): the pick (PickSkips),
    /// self-defence (SkipsHitter) and the monster coming at the bot (Incoming) pass it over.</summary>
    public static bool UnreachableMarked(PlayerBotState state, GameLiving npc, long now) => Marked(state.Unreachables, npc, now);

    /// <summary>#167 E M2, U1: whether the pick (FindWithin) passes over a monster: marked unreachable, or blind (the
    /// pick's alone).</summary>
    public static bool PickSkips(PlayerBotState state, GameLiving npc, long now) =>
        UnreachableMarked(state, npc, now) || BlindSkipped(state, npc, now);

    // A mark of its own for 30 s (UnreachableSkipMilliseconds): the target's old one and those past their time go.
    private static void Mark(List<(GameLiving Npc, long Until)> marks, GameLiving target, long now)
    {
        for (int i = marks.Count - 1; i >= 0; i--)
            if (marks[i].Npc == target || now >= marks[i].Until) marks.RemoveAt(i);
        marks.Add((target, now + UnreachableSkipMilliseconds));
    }

    private static bool Marked(List<(GameLiving Npc, long Until)> marks, GameLiving npc, long now)
    {
        for (int i = 0; i < marks.Count; i++)
            if (marks[i].Npc == npc && now < marks[i].Until) return true;
        return false;
    }

    /// <summary>#167 E R1: the stop a bot steps in to (Engage's walk) when its target is out of sight (inView false) and
    /// it would drop the target for it (kept false: one fighting neither the bot nor its pet, nor coming at it,
    /// DropsBlindTarget's terms): from beyond contact (CaughtReach), BlindStepDistance nearer, never nearer than its melee
    /// stop (meleeStop) nor than contact; there it looks again. 0, no step: in sight, kept, or in contact, where the blind
    /// clock drops it after BlindDropMilliseconds as before. M2 dropped it from wherever it stood, and the next pick from
    /// there was as blind: st-deathsE/Eb, 644 of 657 blind drops beyond contact, 547 past 1,000 (a caster's or a Thane's
    /// spell stop, round a cave's bend), mostly after 2 fruitless casts (SpellHandler.cs:694), 434 within 15 s of the
    /// bot's previous one. D's cast watch had walked such a bot in after its 4th fruitless cast (MaxFruitlessCasts): the
    /// two looping Thanes, castFailed 13-18 a leg and 47 kills in 48 fights on D; on E castFailed 0, 344 blind drops
    /// and 20 kills.</summary>
    public static int BlindStepStop(bool inView, bool kept, int distance, int meleeStop) =>
        inView || kept || distance <= CaughtReach ? 0 : Math.Max(Math.Min(meleeStop, CaughtReach), distance - BlindStepDistance);

    /// <summary>#167 E R1, Engage's choice at a look (the fix's review: a seam its rows reach): a drop (Drop) only where no
    /// step is (BlindStepStop 0: in contact, in sight or kept) and DropsBlindTarget drops it (kept: it fights the bot or
    /// its pet, or comes at it); else the stop it steps in to (BlindStop: BlindStepStop's, 0 for none), or while a cast is
    /// under way (casting, begun in sight) the stop it had (blindStop). On a drop, blindStop as it was.</summary>
    public static (bool Drop, int BlindStop) BlindLook(bool inView, bool kept, int distance, int meleeStop,
        long blindSince, long now, bool casting, int blindStop)
    {
        int step = BlindStepStop(inView, kept, distance, meleeStop);
        if (step == 0 && DropsBlindTarget(blindSince, now, kept, false, false)) return (true, blindStop);
        return (false, casting ? blindStop : step);
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
    /// recovered (recoverBeforePull; Recovered: full health, the role's power stop, 80% endurance, #167 F3, E-R): the driver
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

    /// <summary>The stop a pull takes (#167 C5; FindWithin's walk-in check, PickUncrowded): Engage's CombatStopDistance at
    /// its first think, no hit on the bot yet: the spell's with a damage spell ready to cast now (nuke: Nuke's choice, no
    /// add looked for; its range, spellRange) and the power for it (ShouldCast), else melee's (attackRange). The review
    /// of 9aacd69: with no stop passed no test failed.</summary>
    public static int PullStopDistance(Spell nuke, byte manaPercent, int attackRange, int spellRange) =>
        CombatStopDistance(ShouldCast(nuke != null, manaPercent, 0, 0), false, nuke != null && Interruptible(nuke), false,
            attackRange, nuke == null ? 0 : spellRange, casterHold: false);

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
    /// 451121c: a caller that left it out brought back the root bomber as a nuke, and no test failed). #167 E C1: perLine
    /// (a caster's; no default) keeps a rank outclassed only by one of another line (OutclassedInLine): Greater
    /// Conflagration 37 at Fire 41 beside Ice Blast (Potent) 41 at Cold 6, the score (NukeValue) choosing between them.
    /// Bolts of two lines have two recast timers (SharedTimerGroup), so a Wizard, Eldritch or Runemaster with a bolt in
    /// each now casts both in 20 s.</summary>
    public static List<(Spell Spell, SpellLine Line)> DamageSpells(IEnumerable<(Spell Spell, SpellLine Line)> usable, int level,
        Func<int, Spell> payload, bool perLine)
    {
        List<(Spell Spell, SpellLine Line)> all = usable.Where(pair => !NoDamageBomber(pair.Spell, payload)).ToList(), nukes = new();
        List<Spell> known = all.ConvertAll(pair => pair.Spell);
        foreach ((Spell spell, SpellLine line) in all)
            if (spell.IsHarmful && spell.Damage > 0 && spell.Range > 0 && !spell.NeedInstrument && spell.Level <= level &&
                !(perLine ? OutclassedInLine(spell, line, all, level) : BotBrain.IsOutclassedDamageSpell(spell, known, level)))
                nukes.Add((spell, line));
        return nukes;
    }

    /// <summary>BotBrain.IsOutclassedDamageSpell (BotBrain.cs:3413-3429) with its same-kind rule within the spell's own line
    /// (#167 E C1): a higher rank of the same type, target, area, instant-ness and PBAoE in that line outclasses it; one
    /// under half the level of the best damage spell in any line, as before.</summary>
    public static bool OutclassedInLine(Spell spell, SpellLine line, IEnumerable<(Spell Spell, SpellLine Line)> known, int level)
    {
        int best = 0;
        foreach ((Spell other, SpellLine otherLine) in known)
        {
            if (other == null || other == spell || !other.IsHarmful || other.Level > level || !BotCasterPriority.IsDamage(other))
                continue;
            best = Math.Max(best, other.Level);
            if (otherLine?.KeyName == line?.KeyName && other.Level > spell.Level && other.SpellType == spell.SpellType &&
                other.Target == spell.Target && (other.Radius > 0) == (spell.Radius > 0) &&
                (other.CastTime <= 0) == (spell.CastTime <= 0) && other.IsPBAoE == spell.IsPBAoE)
                return true;
        }
        return best - spell.Level >= 5 && spell.Level * 2 < best;
    }

    // #167 F10b: an Animist's bomber does only its payload (BomberBrain.cs:32-34, its SubSpellID); the bomber's own
    // Damage (50 on every one) is no damage. One whose payload does none (Spreading Vines: a root) is no nuke, nor does it
    // outrank a damage bomber (BotBrain.IsOutclassedDamageSpell compares spell types). Production resolves payloads with
    // SkillBase.GetSpellByID; a payload it cannot resolve is none.
    private static bool NoDamageBomber(Spell spell, Func<int, Spell> payload) =>
        spell.SpellType is eSpellType.Bomber && !(payload?.Invoke(spell.SubSpellID) is Spell sub && sub.Damage > 0);

    /// <summary>Where an Animist stands to pull with its main turret planted (#167 F10b, E CY3): a turret is planted at
    /// the bot's feet (SummonPet) and reaches turretReach (its TurretSpell, 1,000; 1,350 for a Verdant taunt), and the
    /// monster the bot pulls comes to the bot. With a damage turret (damageTurret: Arboreal's), by its turret (here,
    /// within AnimistStandMargin of it) while both its spell and its turret reach the target from there, the turret less
    /// the margin (limit); else it plants a new one (Replant) by the target: here if it is within that limit of it
    /// already, else at a stand AnimistStandMargin inside the limit. AnimistStepFor plants within the margin of the
    /// stand, short of it on the walk in, so the new turret lands within the limit and keeps (with the stand at the
    /// limit it landed up to the margin beyond, and the bot planted again every think). F10b stood by the turret while
    /// only the spell reached (1,420 with most nukes), or at the spell's reach toward the turret (the target up to 2,270
    /// from it): the turret's aggro range is its spell's (TurretBrain.cs:18-24), so the monster reached the bot unhurt.
    /// Over st-deathsD2 and D2b Arboreal one-monster pulls opened beyond the limit died 8 of 15, within it 0 of 33. A
    /// snare or taunt turret (Creeping's, Verdant's) keeps F10b's stand: by it while its spell reaches the target, else
    /// at its spell's reach toward the turret if the turret reaches that spot (less the margin), else the new one, at
    /// the limit; it wears the monster down or draws it on its way in (its pulls beyond the limit died 1 of 33, 0 of 28
    /// on st-hearth11 and 11b).</summary>
    public static (Vector3 Stand, bool Replant) AnimistStand(Vector3 bot, Vector3 turret, Vector3 target, int spellStop,
        int turretReach, bool damageTurret)
    {
        float limit = Math.Min(spellStop, turretReach - AnimistStandMargin), toTurret = Vector3.Distance(target, turret);
        if (toTurret <= (damageTurret ? limit : spellStop))
            return (Vector3.Distance(bot, turret) <= AnimistStandMargin ? bot : turret, false);
        if (!damageTurret)                                                              // F10b: a snare or taunt turret
        {                                                                               //   wears or draws the monster
            Vector3 near = target + (turret - target) * (spellStop / toTurret);
            if (Vector3.Distance(near, turret) <= turretReach - AnimistStandMargin) return (near, false);
        }
        float toBot = Vector3.Distance(target, bot), standOff = damageTurret ? limit - AnimistStandMargin : limit;
        return (toBot <= limit ? bot : target + (bot - target) * (standOff / toBot), true);
    }

    /// <summary>#167 E CY3: an Animist's step before a pull (AnimistApproach), from AnimistStand's stand (toStand: the
    /// bot's distance to it) and replant, and whether it can plant a new turret now (CanReplant). When it must and
    /// can't, it waits (its in-combat flag, its pet's too, lasts 10 s after a fight: WantsPet), from waitSince (0: not
    /// yet) for TurretWaitMilliseconds, then leaves the target (Skip); F10b pulled it from its spell's reach then, out
    /// of the turret's. Else it walks to the stand, then plants (Replant) or pulls.</summary>
    public static PlayerBotAnimistStep AnimistStepFor(float toStand, bool replant, bool canReplant, long waitSince, long now) =>
        replant && !canReplant
            ? waitSince != 0 && now - waitSince >= TurretWaitMilliseconds ? PlayerBotAnimistStep.Skip : PlayerBotAnimistStep.Wait
            : toStand > AnimistStandMargin ? PlayerBotAnimistStep.Walk
            : replant ? PlayerBotAnimistStep.Replant : PlayerBotAnimistStep.Pull;

    /// <summary>#167 E CY3: the target an Animist leaves (AnimistStepFor's Skip) is out of a new turret's reach, not
    /// unreachable: the pick alone passes it over (MarkBlind, as M2's blind drop), so self-defence (Attacker) and the
    /// monster coming at it (Incoming) still answer it if it aggroes, and the blocked walks' unreachable marks are kept
    /// (the review: an unreachable mark left a skipped monster that hit the bot unanswered 30 s).</summary>
    public static void LeaveTurretTarget(PlayerBotState state, GameLiving target, long now)
    {
        MarkBlind(state, target, now);
        state.Target = null;
    }

    /// <summary>#167 E CY3: PLAYER_BOT_TURRET, an Animist's new turret by its target (step=replant: the old one
    /// released) or a target it left (step=skip: no turret it could plant within TurretWaitMilliseconds), with the
    /// target's distance from the turret it had and that turret's reach.</summary>
    public static string TurretLine(string bot, int level, PlayerBotAnimistStep step, string target, ConColor con,
        int toTurret, int reach) =>
        $"PLAYER_BOT_TURRET bot={bot} level={level} step={step.ToString().ToLowerInvariant()} target=\"{target}\" " +
        $"con={con} toTurret={toTurret} reach={reach}";

    /// <summary>The nuke for this cast (#167 F9), chosen at each cast choice: the bolt when it is ready (a 20-s recast:
    /// a Shaman's Fungal Thistle, a Wizard's Flame Streak), else the best ready single-target spell (Better), else an area
    /// one, but never one while an add stands in its radius (it would pull it). Ready: off its recast and paid for. None
    /// ready: none, and the bot swings until one is (the old choice kept the best spell, and its cast failed on the
    /// recast). #167 E C1: with a score (value: a caster's NukeValues; no default), the bolt and the single-target spells
    /// by it, the highest first (an exact tie: Better); the area ones still last, by Better. Without one (null: every other
    /// role, the pull's stop, TRAIN's spell=), as before.</summary>
    public static (Spell, SpellLine) ChooseNuke(IEnumerable<(Spell Spell, SpellLine Line)> spells, Func<Spell, bool> ready,
        bool addInRadius, Func<Spell, double> value)
    {
        Spell best = null;
        SpellLine bestLine = null;
        int bestRank = int.MaxValue;
        double bestValue = double.NegativeInfinity;
        foreach ((Spell spell, SpellLine line) in spells)
        {
            bool area = spell.Radius > 0;
            if (area && addInRadius || !ready(spell)) continue;
            bool scored = value != null && !area;
            int rank = area ? 2 : scored ? 1 : spell.SpellType is eSpellType.Bolt ? 0 : 1;
            double v = scored ? value(spell) : 0;
            if (best == null || rank < bestRank ||
                rank == bestRank && (scored ? v > bestValue || v == bestValue && Better(spell, best) : Better(spell, best)))
                (best, bestLine, bestRank, bestValue) = (spell, line, rank, v);
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

    /// <summary>Engage's cast cycle (#167 E C1): the next cast choice comes Math.Max(900, cast + 250) ms after a cast
    /// (Engage's NextCombatChoiceTick).</summary>
    public static int CastCycleMs(int castMs) => Math.Max(900, castMs + 250);

    /// <summary>The expected damage of one landed hit or tick (#167 E C1), from the server's own numbers: the handler's base
    /// (damageBase: CalculateDamageBase, the spell's damage by stat and item spec, SpellHandler.cs:3002-3114), the mean of
    /// its variance (CalculateDamageVariance's min and max: the spec's lower bound and the level offset, none for a DoT,
    /// SpellHandler.cs:2912-2996), less the target's resist to its damage type (GetResist: an NPC's racial resist too,
    /// :3221-3248). A bolt resists half and takes the other half x 2.5 L over the target's armour (boltArmorFactor,
    /// BoltSpellHandler.cs:58-85; a block isn't counted).</summary>
    public static double ExpectedHit(double damageBase, double varianceMin, double varianceMax, int resistPercent, bool bolt,
        double boltArmorFactor)
    {
        double hit = damageBase * (varianceMin + varianceMax) / 2;
        double kept = 1 - resistPercent * 0.01;
        return bolt ? hit * (0.5 * kept + 0.5 * boltArmorFactor) : hit * kept;
    }

    /// <summary>A DoT's ticks (#167 E C1): one at its start (DamageOverTimeECSEffect.cs:9-18), then one every frequency
    /// while before its expiry, which the pulse loop reads first (EffectService.cs:104-117): ceil(duration / frequency).
    /// Mind Annihilation (24 s, 4 s) 6; Illusory Excrutiation (20 s, 5 s) 4.</summary>
    public static int DotTicks(int durationMs, int frequencyMs) =>
        frequencyMs <= 0 ? 1 : Math.Max(1, (durationMs + frequencyMs - 1) / frequencyMs);

    /// <summary>The ticks a DoT lands before the target dies (#167 E C1): the first at once, one more each frequency of the
    /// fight's horizon (horizonMs, KillHorizonMs), at most its ticks.</summary>
    public static int RealizedTicks(int ticks, int frequencyMs, double horizonMs) =>
        frequencyMs <= 0 ? Math.Min(ticks, 1) : (int)Math.Min(ticks, 1 + Math.Floor(Math.Max(0, horizonMs) / frequencyMs));

    /// <summary>The fight's horizon for a new DoT (#167 E C1): the target's health (hit points) less its own DoTs' ticks
    /// still due (pendingDot, DotTicksLeft), at the bot's best sustained direct damage per millisecond (bestDirectPerMs:
    /// NukeValue's best non-bolt; none, no limit).</summary>
    public static double KillHorizonMs(double health, double pendingDot, double bestDirectPerMs) =>
        bestDirectPerMs <= 0 ? double.PositiveInfinity : Math.Max(0, health - pendingDot) / bestDirectPerMs;

    /// <summary>The ticks a DoT on the target still lands (#167 E C1): one at its next tick (nextInMs) and each frequency
    /// after, while before its expiry (expireInMs), the expiry read first (EffectService.cs:104-117).</summary>
    public static int DotTicksLeft(long expireInMs, long nextInMs, int frequencyMs) =>
        frequencyMs <= 0 || nextInMs >= expireInMs ? 0
            : (int)((expireInMs - Math.Max(0, nextInMs) + frequencyMs - 1) / frequencyMs);

    /// <summary>A nuke's expected damage per millisecond of its cast cycle (#167 E C1): the hit (ExpectedHit) times its land
    /// chance (hitChancePercent: CalculateToHitChance; a resist takes the whole cast, SpellHandler.cs:2482-2500) times its
    /// ticks (1 for a direct hit; a DoT's RealizedTicks), over CastCycleMs. Per cast-second, not per power: per power left
    /// 86 of 323 Sorcerer wins unfinished in their own cast slots (the casters design's s6).</summary>
    public static double NukeValue(double expectedHit, double hitChancePercent, int ticks, int castMs) =>
        expectedHit * Math.Clamp(hitChancePercent, 0, 100) * 0.01 * ticks / CastCycleMs(castMs);

    /// <summary>Whether a caster casts a DoT at its target (#167 E C1): its ticks are damage, which ends a root and a mez but
    /// not a stun (GameLiving.HandleCrowdControlOnAttacked, GameLiving.cs:1749-1830): so a stun caster always, a root or mez
    /// caster only where a control on the target isn't worth it (controlWorth: ControlWorth).</summary>
    public static bool DotFits(bool hasStun, bool controlWorth) => hasStun || !controlWorth;

    /// <summary>Whether a control holds the target while the bot's own DoT is on it (#167 E C1): a stun does; a root or a mez
    /// ends at the next tick (GameLiving.cs:1749-1830).</summary>
    public static bool HoldSurvivesDot(Spell control, bool ownDotOn) => !ownDotOn || control.SpellType is eSpellType.Stun;

    /// <summary>Whether the score's nuke keeps a root caster's root castable (#167 E C1): the server lets no root replace a
    /// damage-and-snare slow (RootTakes), so a caster with a root (rootWanted; the review: at any con, so no root caster is
    /// newly moved onto one) isn't moved onto one the old order (Better's, oldChoice) didn't choose. Where the old order
    /// chose one, the trade stays: forbidding it doubles a YELLOW kill's time and adds 75% power (the design's s11).</summary>
    public static bool KeepsRootCastable(Spell scored, Spell oldChoice, bool rootWanted) =>
        !rootWanted || scored?.SpellType is not eSpellType.DamageSpeedDecrease ||
        oldChoice?.SpellType is eSpellType.DamageSpeedDecrease;

    /// <summary>Nuke's ready test (#167 E C1, the review's seam): off its recast (offRecast) and paid for (paid,
    /// HasPowerFor), or under the killing quickcast (#167 E C2b: killingQuickcast, KillingQuickcast) with any power
    /// left (mana: the server checks only that under Quickcast, SpellHandler.cs:771-776, :978-984); with a score
    /// (scored: a caster's at a monster) a DoT (dot) only where it fits (dotFits, DotFits) and no DoT it conflicts with
    /// is on the target (dotConflict, DotConflicts: its cast would only replace it). Unscored (every other role), as
    /// before.</summary>
    public static bool NukeReady(bool offRecast, bool paid, bool killingQuickcast, int mana, bool scored, bool dot,
        bool dotFits, bool dotConflict) =>
        offRecast && (paid || killingQuickcast && mana > 0) && (!scored || !dot || dotFits && !dotConflict);

    /// <summary>A caster's nuke by its score (#167 E C1, the review's seam): ChooseNuke with the score (value: NukeValues),
    /// then, where a root may be wanted (rootWanted), not a damage-and-snare nuke the old order (no score) didn't choose
    /// (KeepsRootCastable): the score's choice among the rest. No score (value null): ChooseNuke as before.</summary>
    public static (Spell, SpellLine) ScoredNuke(IEnumerable<(Spell Spell, SpellLine Line)> spells, Func<Spell, bool> ready,
        bool addInRadius, Func<Spell, double> value, bool rootWanted)
    {
        (Spell spell, SpellLine line) = ChooseNuke(spells, ready, addInRadius, value);
        if (value != null && !KeepsRootCastable(spell, ChooseNuke(spells, ready, addInRadius, null).Item1, rootWanted))
            (spell, line) = ChooseNuke(spells, s => ready(s) && s.SpellType is not eSpellType.DamageSpeedDecrease, addInRadius,
                value);
        return (spell, line);
    }

    // PLAYER_BOT_TRAIN's spell=: the nuke it opens a fight with, everything ready and no add. #167 E C1: a caster's
    // damage spells by its own lines, in the old order (no score: there is no monster to score them at).
    public static string BestDamageSpellName(GamePlayer player) =>
        ChooseNuke(DamageSpells(UsableSpells(player), player.Level, SkillBase.GetSpellByID,
                player.CharacterClass is { } c && RoleOf(c.ID) == PlayerBotRole.Caster), _ => true, false, null).Item1 is Spell s
            ? FormattableString.Invariant($"{s.Name.Replace(' ', '_')}:{s.Level}") : "-";

    // #167 F9: the nuke at a cast choice, from the damage spells cached per level. An add in an area spell's radius is
    // looked for only when an area spell would be chosen: any other monster (IsExperienceTarget) within it of the target,
    // but one already on the bot. #167 E C1: a caster's single-target nukes at a monster by their expected damage per
    // cast-second (NukeValues); a DoT only where it fits (DotFits) and none it conflicts with is on the target
    // (DotConflicts, read once before the passes); a caster with a root not moved onto a damage-and-snare nuke the old
    // order didn't choose (ScoredNuke's KeepsRootCastable; the review: rootWanted at any con). NukeReady and ScoredNuke
    // are the tested seams. #167 E C2b: under the killing quickcast (killingQuickcast: KillingNukeChoice's any-power
    // pass; TakeTarget's and a level-up's choice read Engage's state.KillingQuickcast) a nuke it has any power for.
    private static (Spell, SpellLine) Nuke(PlayerBotState state, GamePlayer player, GameLiving target) =>
        Nuke(state, player, target, state.KillingQuickcast);

    private static (Spell, SpellLine) Nuke(PlayerBotState state, GamePlayer player, GameLiving target, bool killingQuickcast)
    {
        KnowSpells(state, player);
        bool scores = target is GameNPC && RoleOf(player.CharacterClass.ID) == PlayerBotRole.Caster;
        bool dotFits = scores && DotFits(state.HasStun, ControlWorth(ConLevels.GetConColor(player.GetConLevel(target)),
            player.HealthPercent, LiveHitters(player)));
        Dictionary<Spell, double> values = scores ? NukeValues(state, player, target, GameLoop.GameLoopTime) : null;
        HashSet<Spell> conflicts = scores && dotFits ? DotConflicts(state, target) : null;
        bool Ready(Spell spell) => NukeReady(player.GetSkillDisabledDuration(spell) <= 0,
            HasPowerFor(player.MaxMana, player.Mana, spell.Power), killingQuickcast, player.Mana, scores, IsDamageOverTime(spell),
            dotFits,
            conflicts?.Contains(spell) == true);
        Func<Spell, double> value = values == null ? null : spell => values.TryGetValue(spell, out double v) ? v : 0;
        bool rootWanted = scores && state.HasRoot;
        (Spell spell, SpellLine line) = ScoredNuke(state.Nukes, Ready, false, value, rootWanted);
        if (spell == null || spell.Radius <= 0 || target == null) return (spell, line);
        foreach (GameNPC npc in target.GetNPCsInRadius((ushort)spell.Radius))
            if (npc != target && npc.IsAlive && IsExperienceTarget(npc) && !(npc.IsAttacking && npc.TargetObject == player))
                return ScoredNuke(state.Nukes, Ready, true, value, rootWanted);
        return (spell, line);
    }

    // #167 E C1: each single-target nuke's expected damage per cast-second at the target (NukeValue), from its own handler
    // (KnowSpells' NukeHandlers): the direct ones first, whose best sustained one (no bolt: its 20-s recast) sets the
    // fight's horizon (KillHorizonMs) for the DoTs' ticks (RealizedTicks), less the ticks the bot's own DoTs still owe
    // (PendingDotDamage). One pass per Nuke: one a cast choice (at most one a second), two when the any-power nuke under
    // Quickcast doesn't kill (KillingNukeChoice); the dictionary is reused. CalculateToHitChance
    // copies the bot's effect list once per nuke: about 10 copies a cast choice, no world scan.
    [ThreadStatic] private static Dictionary<Spell, double> _nukeValues;
    private static Dictionary<Spell, double> NukeValues(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        Dictionary<Spell, double> values = _nukeValues ??= new();
        values.Clear();
        double bestDirect = 0;
        foreach ((Spell spell, _) in state.Nukes)
        {
            if (spell.Radius > 0 || IsDamageOverTime(spell) ||
                !state.NukeHandlers.TryGetValue(spell, out DOL.GS.Spells.SpellHandler handler))
                continue;
            double perMs = NukeValue(ExpectedHitOf(handler, spell, player, target), handler.CalculateToHitChance(target), 1,
                spell.CastTime);
            values[spell] = perMs;
            if (spell.SpellType is not eSpellType.Bolt) bestDirect = Math.Max(bestDirect, perMs);
        }
        double horizon = double.NaN;
        foreach ((Spell spell, _) in state.Nukes)
        {
            if (spell.Radius > 0 || !IsDamageOverTime(spell) ||
                !state.NukeHandlers.TryGetValue(spell, out DOL.GS.Spells.SpellHandler handler))
                continue;
            if (double.IsNaN(horizon))                                                  // read once, with a DoT only
                horizon = KillHorizonMs(target.Health, PendingDotDamage(state, player, target, now), bestDirect);
            values[spell] = NukeValue(ExpectedHitOf(handler, spell, player, target), handler.CalculateToHitChance(target),
                RealizedTicks(DotTicks(spell.Duration, spell.Frequency), spell.Frequency, horizon), spell.CastTime);
        }
        return values;
    }

    // #167 E C2b: whether a nuke the bot will cast (KillingNukeChoice's) kills its target at once (OneNukeKills): its hit
    // at the variance minimum less the resist (and a bolt's armour), against the target's health less its own DoTs'
    // ticks still due. A caster's at a monster (the score's handlers; a pet summon has none: false).
    private static bool NukeKillsNow(PlayerBotState state, GamePlayer player, GameLiving target, Spell spell, long now) =>
        target is GameNPC && spell != null && state.NukeHandlers.TryGetValue(spell, out DOL.GS.Spells.SpellHandler handler) &&
        OneNukeKills(ExpectedHitOf(handler, spell, player, target, minimum: true), target.Health,
            PendingDotDamage(state, player, target, now));

    // #167 E C1: ExpectedHit from the nuke's handler (CalculateDamageBase, CalculateDamageVariance), the target's resist to
    // its damage type (GetResist) and a bolt's armour factor (2.5 x the bot's level over the target's armour, as
    // BoltSpellHandler.ModifyDamageWithTargetResist reads it; an NPC has no hit location). #167 E C2b: minimum, the hit
    // at the variance minimum (OneNukeKills).
    private static double ExpectedHitOf(DOL.GS.Spells.SpellHandler handler, Spell spell, GamePlayer player, GameLiving target,
        bool minimum = false)
    {
        handler.CalculateDamageVariance(target, out double min, out double max);
        bool bolt = spell.SpellType is eSpellType.Bolt;
        return ExpectedHit(handler.CalculateDamageBase(target), min, minimum ? min : max, target.GetResist(spell.DamageType), bolt,
            bolt ? player.Level * 2.5 / Math.Max(1, AttackComponent.CalculateTargetArmor(target, eArmorSlot.NOTSET, out _, out _))
                : 0);
    }

    // #167 E C1: the damage the bot's own DoTs on the target still owe (DotTicksLeft at each one's expected tick; a landed
    // DoT rolls no resist again).
    private static double PendingDotDamage(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        double pending = 0;
        foreach (ECSGameSpellEffect effect in target.effectListComponent.GetSpellEffects(eEffect.DamageOverTime))
            if (effect.SpellHandler?.Caster == player && effect.SpellHandler.Spell is Spell spell &&
                state.NukeHandlers.TryGetValue(spell, out DOL.GS.Spells.SpellHandler handler))
                pending += DotTicksLeft(effect.ExpireTick - now, effect.NextTick - now, spell.Frequency) *
                    ExpectedHitOf(handler, spell, player, target);
        return pending;
    }

    // #167 E C1: the bot's DoTs that conflict with one on the target (the server's own test,
    // DoTSpellHandler.HasConflictingEffectWith, DoTSpellHandler.cs:43-57: the same effect group, or the same type and
    // frequency; a Mentalist's Mana and Mentalism DoTs, 4 s and 5 s, stack): a cast would only replace it. The target's
    // effect list read once per cast choice, before ScoredNuke's passes (the review); the set is reused.
    [ThreadStatic] private static HashSet<Spell> _dotConflicts;
    private static HashSet<Spell> DotConflicts(PlayerBotState state, GameLiving target)
    {
        HashSet<Spell> conflicts = _dotConflicts ??= new();
        conflicts.Clear();
        List<ECSGameSpellEffect> dots = target.effectListComponent.GetSpellEffects(eEffect.DamageOverTime);
        if (dots.Count == 0) return conflicts;
        foreach ((Spell spell, _) in state.Nukes)
        {
            if (!IsDamageOverTime(spell) || !state.NukeHandlers.TryGetValue(spell, out DOL.GS.Spells.SpellHandler handler))
                continue;
            foreach (ECSGameSpellEffect effect in dots)
                if (effect.SpellHandler != null && handler.HasConflictingEffectWith(effect.SpellHandler))
                {
                    conflicts.Add(spell);
                    break;
                }
        }
        return conflicts;
    }

    // #167 E C1: a DoT of the bot's own on the target (HoldSurvivesDot).
    private static bool OwnDotOn(GamePlayer player, GameLiving target)
    {
        foreach (ECSGameSpellEffect effect in target.effectListComponent.GetSpellEffects(eEffect.DamageOverTime))
            if (effect.SpellHandler?.Caster == player) return true;
        return false;
    }

    // APP:694-710 without the quickbar filter.
    // APP:694-710 without the quickbar filter. #167 E-R: the style StyleWithin takes of the usable ones by level, each at
    // its cost as StyleProcessor charges it (StyleEnduranceCost; a shield style off the left hand, as the swing pays for
    // it, PlayerAttackAction.cs:38), keeping a close fight's reserve (reserve: ReserveNow's; 0 out of one, today's pick);
    // a choice the reserve left unstyled counts FIGHT styleHeld=.
    private static void QueueUsableStyle(PlayerBotState state, GamePlayer player, int reserve)
    {
        if (player.styleComponent.NextCombatStyle != null || player.EndurancePercent < 15 || player.ActiveWeapon is not { } weapon)
            return;
        AttackData lastAttack = player.attackComponent.attackAction.LastAttackData;
        List<Style> styles = player.GetAllUsableSkills()
            .Select(entry => entry.Item1)
            .OfType<Style>()
            .Where(candidate => StyleProcessor.CanUseStyle(lastAttack, player, candidate, weapon))
            .OrderByDescending(candidate => candidate.Level)
            .ToList();
        if (styles.Count == 0) return;
        int fatigue = player.GetModified(eProperty.FatigueConsumption);
        DOL.Database.DbInventoryItem leftHand = null;
        int[] costs = new int[styles.Count];
        for (int i = 0; i < styles.Count; i++)
        {
            DOL.Database.DbInventoryItem paidBy = (eObjectType)styles[i].WeaponTypeRequirement is eObjectType.Shield
                ? leftHand ??= player.Inventory.GetItem(eInventorySlot.LeftHandWeapon) : weapon;
            costs[i] = paidBy == null ? 0 : StyleEnduranceCost(paidBy.SPD_ABS, styles[i].EnduranceCost, fatigue,
                styles[i].Spec == Specs.Battlemaster);
        }
        (int pick, bool held) = StyleWithin(costs, player.Endurance, reserve);
        if (pick >= 0) player.styleComponent.NextCombatStyle = styles[pick];
        if (held) state.FightStylesHeld++;
    }

    // #167 E-R: the endurance the bot keeps for a run now (EscapeReserve), in a fight only (the review: Engage's cast gate
    // runs on the walk in and the first think in reach, before OpenFight's resets) and a close one (CloseFight): 2 or
    // more monsters attacking it now (AttackerTracker's few, IsAttacking: no world scan) or in this fight (FightAdds), a
    // defence or the fight after an add kill, or the race since the first hit on it projecting it under
    // CloseRacePercent at its target's death (HealthLeftAtKill); else 0. Against the fastest of them and its target.
    // Read at most once a StyleMilliseconds (Reserve, NextReserveTick), the style choice's own pace. #167 E fix R2: held
    // only while holding can still buy a run (HoldingReserve), on every call against the endurance and the target's health
    // now, on the read's need (RunNeed: EscapeEndurance against that fastest monster) and whether its target is the only
    // monster on the bot (TargetAlone). The read itself is ReserveRead's (the fix's review: a seam its rows reach).
    private static int ReserveNow(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        if (!state.Engaged) return 0;
        bool targetAlive = target?.IsAlive == true;
        if (now >= state.NextReserveTick)
        {
            state.NextReserveTick = now + StyleMilliseconds;
            ICollection<GameLiving> attackers = player.attackComponent.AttackerTracker.Attackers;
            var onBot = new List<(int Speed, bool IsTarget)>(attackers.Count);
            foreach (GameLiving living in attackers)
                if (living is GameNPC npc && IsAttacking(player, npc))
                    onBot.Add((npc.MaxSpeed, npc == target));
            int? left = targetAlive ? HealthLeftAtKill(player.HealthPercent, target.HealthPercent, state.FightHpAtReach,
                state.FightTargetHpAtReach) : null;
            (state.Reserve, state.RunNeed, state.TargetAlone) = ReserveRead(targetAlive ? target.MaxSpeed : 0, onBot,
                state.FightAdds, state.FromDefence || state.FollowsAddKill, left,
                player.GetModified(eProperty.EnduranceRegenerationAmount), player.GetModified(eProperty.FatigueConsumption),
                LongWindCost(player));
        }
        return HoldingReserve(state.Reserve, state.RunNeed, player.Endurance, targetAlive ? target.HealthPercent : (byte)100,
            state.TargetAlone);
    }

    // APP:474-500, plus the leash. #167 E fix U3: a blocked walk on a leash the bot may move (CanRelocate: no lever spot
    // bot) moves its anchor where it stands when no corridor leads there and one leads to where it stood at its last
    // kill (ReanchorAfterRoamBlock, HuntedHere); where it stands after the walk, as a floor step may have moved it
    // (TryFloorStep).
    private static void Roam(PlayerBotState state, GamePlayer player, long now)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        bool leashed = Leashed(state, player);
        if (leashed && Vector3.Distance(here, state.Anchor) > LeashRadius)
            state.RoamDestination = state.Anchor;
        else if (state.RoamDestination == null || Vector3.Distance(here, state.RoamDestination.Value) < 80)
            state.RoamDestination = PickRoamPoint(player, leashed ? state.Anchor : here);
        state.Activity = PlayerBotActivity.Roaming;
        state.Walk = state.RoamDestination == state.Anchor ? PlayerBotWalk.Anchor : PlayerBotWalk.Roam;   // #167 walk=
        PlayerBotStepResult step = state.Mover.StepToward(state.RoamDestination.Value, 20, now);
        if (step is PlayerBotStepResult.Blocked)
        {
            ReportBlocked(state, player, "roam", state.RoamDestination.Value, now);
            Vector3 was = state.Anchor, at = new(player.X, player.Y, player.Z);
            if (ReanchorAfterRoamBlock(state, at, leashed && CanRelocate(state, player), now,
                    () => AnchorReachable(PathfindingProvider.Instance, player.CurrentRegion, at, was),
                    () => HuntedHere(PathfindingProvider.Instance, player.CurrentRegion, at, state.LastKillAt)) &&
                Log.IsInfoEnabled)
                Log.Info($"PLAYER_BOT_REANCHOR bot={player.Name} level={player.Level} from=({was.X:0},{was.Y:0},{was.Z:0}) " +
                         $"to=({at.X:0},{at.Y:0},{at.Z:0}) distance={(int)Vector3.Distance(was, at)} zone={player.CurrentZone?.ID}");
        }
        if (step is PlayerBotStepResult.Blocked or PlayerBotStepResult.Arrived)
            state.RoamDestination = null;
    }

    /// <summary>#167 E fix U3: a roam walk blocked (Roam) while the bot is on a leash it may move (leashed: no task, in its
    /// anchor's region, no lever spot bot, CanRelocate). At most once every AnchorCheckMilliseconds it tests whether a
    /// corridor leads from where it stands (here) to its anchor (anchorReachable, read only when due); with none the
    /// anchor moves here, as EndReplan moves it, and the roam picks afresh round it. st-deathsE2b, Pbotmaada on a pocket of
    /// Muspelheim's navmesh above its camp: no corridor led to its anchor from any spot on the pocket, so every think with
    /// no target blocked a roam toward it (about 4 a second, about 5 ms each), and its kills on the pocket kept stopping the
    /// idle clock (SettleIdleClock, SettleIdleClockAtTargetEnd), so the outgrown camp's relocation, the only move of its
    /// anchor there, never came. True when the anchor moved.
    /// U3's review: only where the bot hunts, a corridor leading from here to where it stood at its last kill (huntedHere,
    /// read only with no corridor to the anchor; HuntedHere). Else the camp stays, so RecoverIfStuck's move back to it
    /// after 15 minutes with no progress still comes: st-deathsE, Pbothaaem (Blademaster 26, Domnann) on an island of the
    /// navmesh after a floor step, every roam blocked and no kill there, its last one on the main mesh. Moved onto the
    /// island, it roamed round it (progress) and the move back led there. A spot, not a clock, so a second time on the
    /// island after a move back and kills at the camp keeps the camp too.</summary>
    public static bool ReanchorAfterRoamBlock(PlayerBotState state, Vector3 here, bool leashed, long now,
        Func<bool> anchorReachable, Func<bool> huntedHere)
    {
        if (!leashed || now < state.NextAnchorCheckTick) return false;
        state.NextAnchorCheckTick = now + AnchorCheckMilliseconds;
        if (anchorReachable() || !huntedHere()) return false;   // a corridor to the camp, or no kill made from here
        state.Anchor = here;
        state.RoamDestination = null;
        return true;
    }

    /// <summary>#167 E fix U3, ReanchorAfterRoamBlock's test: whether a corridor leads from here to the anchor
    /// (PlayerTravelPath.TryBuildCorridor, as the plan's tests). True wherever no test can tell: no zone here, an anchor
    /// in another zone (a corridor runs in one), no navmesh, or off the mesh here (every corridor fails then, PTP:79-82;
    /// RelocateIfOutgrown plans nothing there either).</summary>
    public static bool AnchorReachable(IPathfindingMgr nav, Region region, Vector3 here, Vector3 anchor)
    {
        Zone zone = region?.GetZone((int)here.X, (int)here.Y);
        return zone == null || region.GetZone((int)anchor.X, (int)anchor.Y) != zone || !nav.HasNavmesh(zone) ||
            nav.GetClosestPoint(zone, here, 64, 64, 128, nav.DefaultFilters) == null ||
            PlayerTravelPath.TryBuildCorridor(nav, zone, here, anchor, out _);
    }

    /// <summary>#167 E fix U3's review, ReanchorAfterRoamBlock's second test, read only after AnchorReachable found no
    /// corridor (a navmesh, and on it here): whether a corridor leads from here to where the bot stood at its last kill
    /// (killAt, LastKillAt), in this zone. Strict, unlike the anchor's test: false with no kill yet (or since a region
    /// change), a kill in another zone, no zone here, or no corridor. st-deathsE, Pbothaaem's island in Domnann: its last
    /// kill about 690 units off on the main mesh, no corridor to it.</summary>
    public static bool HuntedHere(IPathfindingMgr nav, Region region, Vector3 here, Vector3? killAt)
    {
        Zone zone = region?.GetZone((int)here.X, (int)here.Y);
        return killAt is Vector3 kill && zone != null && region.GetZone((int)kill.X, (int)kill.Y) == zone &&
            PlayerTravelPath.TryBuildCorridor(nav, zone, here, kill, out _);
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
                     $"deathSteps={state.DeathSafetySteps} cap={MaximumTargetCon(1, CapSteps(state.DeathSafetySteps, state.SoloSteps))} " +
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
    // failed camps barred; with no camp it hunts where it is. #167 D4: while the release's immunity lasts too, its
    // monsters read by IsPlannable (PlanMonsters; C1 held the plan until it was over: IsAttackable found none then). The
    // anchor's region is the bot's (CanRelocate). #167 E W1: after a death (AfterDeathPlan) it first takes a camp near
    // where it stands (PlanCampChoice's near pass: the zone it stands in, within NearWalk, corridors from here), and only
    // with none the plan as before. One read of the region's monsters serves both passes.
    private static void RelocateIfOutgrown(PlayerBotState state, GamePlayer player, long now)
    {
        string reason = RelocateReason(state.RoamingSince, state.CappedSince, now, state.ReplanOwed);
        if (reason == null || now < state.NextRelocateTick) return;
        bool died = reason == Died;
        if (!CanRelocate(state, player) ||                                         // the lever test keeps its bots on
            (died ? WorldMgr.GetRegion(state.AnchorRegion)?.GetZone((int)state.DeathSpot.X, (int)state.DeathSpot.Y)
                : player.CurrentZone) is not Zone zone)                            //   their spots
        {
            FallBack(state, player, reason, now);
            return;
        }
        state.NextRelocateTick = now + (reason == Outgrown ? RelocateAfterMilliseconds : CappedRelocateMilliseconds);
        Vector3 here = new(player.X, player.Y, player.Z), from = RelocationOrigin(reason, state.DeathSpot, here);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        if (nav.HasNavmesh(player.CurrentZone) && nav.GetClosestPoint(player.CurrentZone, here, 64, 64, 128, nav.DefaultFilters) == null)
        {
            FallBack(state, player, reason, now);  // off the mesh here, every corridor fails (PlayerTravelPath.cs:48-50): blame
            return;                                //   no monster for it
        }
        Vector3? corridorFrom = CorridorOrigin(reason, nav.HasNavmesh(zone), player.CurrentZone == zone, state.DeathSpot, here);
        if (corridorFrom is Vector3 deathStart && player.CurrentZone != zone &&  // a death spot off the mesh fails every
            nav.GetClosestPoint(zone, deathStart, 64, 64, 128, nav.DefaultFilters) == null)  // corridor (PTP:80-82): no
            corridorFrom = null;                                                 //   test then, as before
        ConColor natural = MaximumTargetCon(1, 0);
        GameNPC[] npcs = WorldMgr.GetNPCsFromRegion(state.AnchorRegion);          // #167 E W1: one read, both passes
        List<(GameNPC Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> monsters = PlanMonsters(player, npcs,
            zone, natural, state.AvoidUntil, now);
        Vector3[] failed = FailedCamps(state.FailedCamps, state.AnchorRegion, now);
        bool barredOld = Array.IndexOf(failed, state.Anchor) >= 0;                 // #167 D9: RELOCATE barred=old
        (ConColor fit, ConColor? killerCon) = PlanCaps(state, now);                 // #167 D6
        bool afterDeath = AfterDeathPlan(reason, state.LastDeathTick == 0 ? -1 : now - state.LastDeathTick);   // #167 E W1
        Zone nearZone = player.CurrentZone;                                        // the zone it stands in: after a
        List<(GameNPC Npc, Vector3 At, ConColor Con, bool Huntable, bool Avoided)> nearMonsters =   //   death its bind's
            !afterDeath || nearZone == null ? null : nearZone == zone ? monsters
                : PlanMonsters(player, npcs, nearZone, natural, state.AvoidUntil, now);
        Vector3? nearFrom = nearZone != null && nav.HasNavmesh(nearZone) ? here : null;   // on the mesh (checked above)
        (GameNPC[] spots, ConColor? fallback, bool stale, bool near) = PlanCampChoice(nearMonsters, monsters, here, from,
            fit, natural, failed, state.RelocateRejected, RelocateCandidates, killerCon, afterDeath);
        if (TakeCamp(spots, fallback, stale, near)) return;
        if (near)                                                                  // no corridor to any near camp: the
        {                                                                          //   plan as before
            (spots, fallback, stale, near) = PlanCampChoice(nearMonsters, monsters, here, from, fit, natural, failed,
                state.RelocateRejected, RelocateCandidates, killerCon, afterDeath, tryNear: false);
            if (TakeCamp(spots, fallback, stale, near)) return;
        }
        if (Log.IsInfoEnabled)
            Log.Info(RelocateNoneLine(player.Name, player.Level, spots.Length, zone.ID, reason, now - state.ReleasedAt,
                barredOld, killerCon, afterDeath));
        if (spots.Length == 0) FallBack(state, player, reason, now);   // no camp in the zone: none to try next time either

        // The first of a camp's spots it has a corridor to (one without is rejected, RelocateRejected): RELOCATE, PlanCamp.
        bool TakeCamp(GameNPC[] camp, ConColor? campFallback, bool campStale, bool nearCamp)
        {
            Zone campZone = nearCamp ? nearZone : zone;
            Vector3? walkFrom = nearCamp ? nearFrom : corridorFrom;
            foreach (GameNPC npc in camp)
            {
                Vector3 spot = new(npc.X, npc.Y, npc.Z);
                if (walkFrom is Vector3 origin && !PlayerTravelPath.TryBuildCorridor(nav, campZone, origin, spot, out _))
                {
                    state.RelocateRejected.Add(npc);
                    continue;
                }
                if (Log.IsInfoEnabled)
                    Log.Info(RelocateLine(player.Name, player.Level, state.Anchor, spot, (int)Vector3.Distance(here, spot),
                        npc.Name, npc.EffectiveLevel, campZone.ID, reason, campFallback, now - state.ReleasedAt, barredOld,
                        killerCon, afterDeath, nearCamp));
                PlanCamp(state, spot, campFallback, campStale, reason);           // and BACK replan=moved (#167 C1)
                return true;
            }
            return false;
        }
    }

    // A capped bot's FallBackInPlace; after a death (#167 F4) or a break-off (C2), the end of the relocation owed.
    private static void FallBack(PlayerBotState state, GamePlayer player, string reason, long now)
    {
        if (reason is Died or BrokeOff) EndReplan(state, player);
        else FallBackInPlace(state, reason, now);
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
    private static void FallBackInPlace(PlayerBotState state, string reason, long now)
    {
        if (FallbackInPlace(reason, state.SafestPassedOver, LowestKillerCon(state.KillerCons, now)) is not ConColor cap)
            return;
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
    // backOff): no release time (DEATH again= and sinceRelease= stay the release's), no back-off, no replan owed. #167
    // D4: the release's immunity's end (ImmunityEnd, from the ask, still set here) and the walk out afresh; a login none.
    private static void StartRecovery(PlayerBotState state, long now, bool backOff)
    {
        if (backOff)
        {
            state.ReleasedAt = now;
            state.RecoverUntil = now + AutonomousDeathRecoveryPolicy.RetryDelayMilliseconds(state.NoXpDeaths);
            state.ReplanSince = state.ReplanOwed != null ? now : 0;
            (state.BackPending, state.BackOwed, state.BackMoved) = (true, state.ReplanOwed != null, false);   // C1: BACK
            state.BackStepOutMs = state.BackHoldMs = state.BackWalkMs = 0;
            (state.BackSame, state.BackImmuneMs) = (false, 0);                                                  // D4
            state.ImmuneUntil = ImmunityEnd(state.ReleaseAskedAt, now, ServerProperties.Properties.TIMER_KILLED_BY_MOB);
        }
        else
        {
            state.RecoverUntil = now;
            state.ImmuneUntil = 0;
        }
        (state.WalkOutGoal, state.WalkOutOver, state.WalkOutSprint) = (null, false, false);
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
    // recovery lasts; when it is over, the idle clock starts afresh. True while it walks. #167 D4: while the release's
    // immunity lasts (ImmunityLeft) with a camp planned since the release (BackMoved: PlanCamp's, a death's), it walks
    // out toward that camp instead (WalkOut), point by point (NextWalkOutPoint, again on each arrival), sprinting
    // (SprintChange). The immunity over, no point, a blocked step or a fight end the walk out for this release
    // (EndWalkOut), and its sprint with it; the ring test pauses meanwhile and runs again at its end.
    private static bool StepsOutOfReach(PlayerBotState state, GamePlayer player, long now)
    {
        if (!state.AfterRelease) return false;
        long immuneLeft = ImmunityLeft(state.ImmuneUntil, now, player.IsInvulnerableToAttack);   // #167 D4
        if (immuneLeft <= 0 && state.WalkOutGoal != null) EndWalkOut(state);
        else if (StartsWalkOut(immuneLeft, state.WalkOutGoal != null, state.WalkOutOver, state.BackMoved))
            state.WalkOutOver = (state.WalkOutGoal = NextWalkOutPoint(state, player, immuneLeft)) == null;
        if (state.WalkOutGoal == null && now >= state.NextReachTick)
        {
            state.NextReachTick = now + TargetRefreshMilliseconds;
            state.ClearSpot = now < state.StepOutBlockedUntil ? null : ClearSpotNear(player, StepOutRings);
        }
        PlayerBotRecovery next = AfterReleaseStep(Threat(state, player, now) != null,   // #167 F7: coming at it too
            state.ClearSpot != null && !player.IsSitting, state.ReplanOwed != null, now < state.RecoverUntil, Ill(player),
            state.WalkOutGoal != null);
        if (next is not PlayerBotRecovery.WalkOut && state.WalkOutGoal != null) EndWalkOut(state);   // a fight
        if (SprintChange(next is PlayerBotRecovery.WalkOut, state.WalkOutSprint, player.Endurance) is bool sprint)
        {
            player.Sprint(sprint);                                                      // GamePlayer.cs:8625-8652
            state.WalkOutSprint = sprint;
        }
        switch (next)
        {
            case PlayerBotRecovery.Fight:
                state.ClearSpot = null;                                                 // it fights back (step 14)
                return false;
            case PlayerBotRecovery.WalkOut:
                state.Activity = PlayerBotActivity.Roaming;
                state.BackWalkMs += TickMilliseconds;                                   // #167 D4: BACK walk=, the walk
                state.BackImmuneMs += TickMilliseconds;                                 //   back, and immune=
                state.Walk = PlayerBotWalk.WalkOut;                                     // #167 walk=
                Vector3 point = state.WalkOutGoal.Value;
                PlayerBotStepResult walked = state.Mover.StepToward(point, 20, now);
                if (walked is PlayerBotStepResult.Blocked)
                {
                    ReportBlocked(state, player, "walkout", point, now);
                    state.StepOutBlockedUntil = now + StepOutPauseMilliseconds;         //   as a blocked step out
                    EndWalkOut(state);
                }
                else if (walked is PlayerBotStepResult.Arrived) state.WalkOutGoal = null;   // the next point next think
                return true;
            case PlayerBotRecovery.StepOut:
                state.Activity = PlayerBotActivity.Roaming;
                state.BackStepOutMs += TickMilliseconds;                                // #167 C1: BACK stepOut=
                if (immuneLeft > 0) state.BackImmuneMs += TickMilliseconds;             // #167 D4: and immune=
                state.Walk = PlayerBotWalk.Clear;                                       // #167 walk=
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

    // #167 D4: the walk out's end for this release (WalkOutOver): its point dropped, and the ring test at once (its 2-s
    // clock paused during the walk). Its sprint goes off on the same think (SprintChange).
    private static void EndWalkOut(PlayerBotState state) =>
        (state.WalkOutGoal, state.WalkOutOver, state.NextReachTick) = (null, true, 0);

    // #167 D4: the walk out's next point toward the camp planned (the anchor, PlanCamp's): of the step-out rings' points
    // within the reach the immunity left gives (WalkOutReach, at the sprint's speed when it sprints), on the navmesh and
    // out of the reach of every monster round it that would aggro (ThreatsWithin, as ClearSpotNear's), the one that
    // leaves the least to the camp (StepOutPoint); from a clear spot only one nearer the camp (WalksOut). None: null.
    private static Vector3? NextWalkOutPoint(PlayerBotState state, GamePlayer player, long immuneLeft)
    {
        float reach = WalkOutReach(immuneLeft, state.WalkOutSprint || player.Endurance > SprintStartEndurance);
        int[] rings = StepOutRings.Where(ring => ring <= reach).ToArray();
        if (rings.Length == 0) return null;
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = ThreatsWithin(player, rings[^1] + AddScanMargin);
        Vector3 here = new(player.X, player.Y, player.Z);
        List<Vector3> clear = ClearSpots(here, RingPoints(here, rings, SpotDirections).Select(p => OnMesh(player, p))
            .Where(p => p.HasValue).Select(p => p.Value), adds);
        bool hereClear = !InAddReach(here, adds);
        adds.Clear();                                                                   // no monster held past the scan
        Vector3? point = StepOutPoint(here, state.Anchor, clear, reach);
        return WalksOut(here, hereClear, state.Anchor, point) ? point : null;
    }

    // The spot out of every potential add's reach nearest the bot (SpotOutOfReach), or null: the monsters within the
    // rings' reach (AddScanMargin past the outer one) that would aggro, a hostile keep guard too (#167 D7, CountsAsThreat).
    private static Vector3? ClearSpotNear(GamePlayer player, int[] rings)
    {
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = ThreatsWithin(player, rings[^1] + AddScanMargin);
        Vector3? found = SpotOutOfReach(player, rings, adds);
        adds.Clear();                                                                   // no monster held past the scan
        return found;
    }

    // The monsters within radius round the bot that would aggro it (AddReach), a hostile keep guard too (#167 D7,
    // IsThreat), in the scans' list (_reach): the caller clears it.
    private static List<(GameNPC Npc, Vector3 At, int AggroRange)> ThreatsWithin(GamePlayer player, int radius)
    {
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)radius))
            if (IsThreat(npc, player.Realm) && AddReach(player, npc) is > 0 and int reach)  // #167 D7: hostile keep guards too
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        return adds;
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
    // and the spawn spots of its kills of the last 150 s (RespawnSpots, #167 C7). None clear, and it sits where it is.
    // True while it walks. #167 D10: the clear spots nearest first (RestSpots); a blocked walk takes the next at once
    // (AfterRestStep), RestSpotTries walks in all, then it sits where it is. The reaches it read stay as positions only
    // (RestReach: no monster held), for REST inReach= (Rest).
    private static bool WalksToRestSpot(PlayerBotState state, GamePlayer player, long now)
    {
        if (!state.RestSpotChecked)
        {
            state.RestSpotChecked = true;
            List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = state.RestReach;
            adds.Clear();
            foreach (GameNPC npc in state.SearchAdds)
                if (AddReach(player, npc) is > 0 and int reach)
                    adds.Add((null, new(npc.X, npc.Y, npc.Z), reach));                  //   where it is now
            int searchAdds = adds.Count;
            foreach ((Vector3 at, int reach) in RespawnSpots(state.RecentKills, now))    // #167 C7: and its kills'
                adds.Add((null, at, reach));                                             //   respawns (REST respawn=)
            state.RestRespawns = adds.Count - searchAdds;
            state.RestSpots = RestSpotsNear(player, RestSpotRings, adds);               // #167 D10
            state.RestBlocked = 0;
            state.RestGoal = RestGoalAt(state.RestSpots, 0, RestSpotTries);
        }
        if (state.RestGoal is not Vector3 goal) return false;
        state.Activity = PlayerBotActivity.Resting;
        state.Walk = PlayerBotWalk.Rest;                                                // #167 walk=
        PlayerBotStepResult step = state.Mover.StepToward(goal, 20, now);
        if (step is PlayerBotStepResult.Blocked) ReportBlocked(state, player, "rest", goal, now);
        (state.RestGoal, state.RestBlocked) = AfterRestStep(step, goal, state.RestSpots, state.RestBlocked);   // #167 D10
        return state.RestGoal != null;
    }

    // #167 D10: RestSpots over the ring points on the navmesh, as SpotOutOfReach reads them (each one's nearest mesh point;
    // with no navmesh, the points, looked up only when the bot stands in reach): the clear spots nearest first, none when
    // it stands clear.
    private static List<Vector3> RestSpotsNear(GamePlayer player, int[] rings,
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        return RestSpots(here, RingPoints(here, rings, SpotDirections).Select(p => OnMesh(player, p))
            .Where(p => p.HasValue).Select(p => p.Value), adds);
    }

    // #167 C2: losing to two (BreaksOff), the bot stops its fight, bars the camp and the names (BrokeOffFrom), sprints
    // and runs to the break-off point (BreakOffPoint), then on from there while one still chases it (BreakOffStep), at
    // most BreakOffMaxMilliseconds; EndBreakOff ends it. A blocked step bars its point and takes the next at once; with
    // none left it turns on them (the review of 9aacd69: the same point was chosen again every think). Before the
    // self-heal (11b): its cast stops the mover (CastOnSelf). #167 D2: caught (CaughtThink: attacked by a chaser in
    // reach after the run's grace, st-deathsD's fix round), it controls the chaser and runs on (CaughtControl),
    // standing and facing it while that cast is under way; with no control it turns on the weakest chaser in reach
    // (WeakestChaser, TurnOn). True while it runs. #167 E T1: with no point (NextPoint with none, a blocked step, or
    // holding) it tries for one each second, 800 out too, and holds or turns (GoalOrHold); BREAKOFF turn= says why it
    // turned. #167 E T2b: done in another monster's reach with its point ahead, it runs on to the point first
    // (RunsOnToClear); a blocked step with none after it then is done.
    private static bool BreaksOffNow(PlayerBotState state, GamePlayer player, long now)
    {
        if (state.BreakOffSince == 0 && !StartsBreakOff(state, player, now)) return false;
        KeepChasers(state, player);
        (int nearest, GameNPC free, int freeDistance) = NearestChasers(state, player);
        long since = now - state.BreakOffSince;
        PlayerBotBreakOff next = BreakOffStep(state.BreakOffArrived, state.BreakOffChasers.Count, since, nearest,
            CaughtThink(state, player, freeDistance, now));
        if (next is PlayerBotBreakOff.Done && RunsOnToClear(state.BreakOffGoal != null, state.BreakOffArrived, since,
                state.BreakOffRunsOn, () => StandsInAddReach(player)))
            (next, state.BreakOffRunsOn) = (PlayerBotBreakOff.Run, true);               // #167 E T2b: on to its point
        if (next is not (PlayerBotBreakOff.Done or PlayerBotBreakOff.Fight) && player.IsCasting)
        {
            state.Mover.Stop(now);                                                      // #167 D2: its control at a caught
            if (state.AddCastTarget?.IsAlive == true) Face(player, state.AddCastTarget); //   chaser (CheckEndCast's front
            state.Activity = PlayerBotActivity.Casting;                                 //   180 degrees, SpellHandler.cs:939)
            return true;
        }
        GameNPC turnOn = null;
        if (next is PlayerBotBreakOff.Caught)
        {
            if (CaughtControl(state, player, free, now)) return true;                   // #167 D2: then it runs on
            turnOn = WeakestChaser(state, player);
            (next, state.BreakOffTurn) = (PlayerBotBreakOff.Fight, TurnCaught);         // #167 E T1: turn=caught
        }
        else if (next is PlayerBotBreakOff.Fight) state.BreakOffTurn = TurnCap;         //   turn=cap (45 s)
        if (next is PlayerBotBreakOff.NextPoint)                                        // a new point at once
            (state.BreakOffGoal, state.BreakOffArrived, state.NextBreakOffRetryTick) = (null, false, 0);
        if ((next is PlayerBotBreakOff.NextPoint or PlayerBotBreakOff.Run) && state.BreakOffGoal == null)
        {                                                                               // #167 E T1: none, or holding
            if (!GoalOrHold(state, player, now, nearest, freeDistance, state.BreakOffHoldWhy ?? TurnNoPoint))
                return false;                                                           //   it turned
            if (state.BreakOffGoal == null) return true;                                //   it holds this think
        }
        if (next is PlayerBotBreakOff.Done or PlayerBotBreakOff.Fight)
        {
            EndBreakOff(state, player, now, next is PlayerBotBreakOff.Done ? Shook : Fought);
            if (turnOn != null) TurnOn(state, player, turnOn, now);                    // #167 D2: caught, no control
            return false;                                                               // Fight: step 14 picks a hitter
        }
        if (BreakOffSprint(player.IsSprinting, freeDistance, FastestChaser(state), player.Endurance) is bool sprint)
        {                                                                               // #167 E-S: off once clear, on
            player.Sprint(sprint);                                                      //   when a chaser closes
            if (!sprint && state.BreakOffSprintOffAt < 0) state.BreakOffSprintOffAt = now - state.BreakOffSince;   // sprintOff=
        }
        state.Activity = PlayerBotActivity.Fighting;
        state.Walk = PlayerBotWalk.BreakOff;                                            // #167 walk=
        Vector3 goal = state.BreakOffGoal.Value;
        PlayerBotStepResult step = state.Mover.StepToward(goal, 20, now);
        if (step is PlayerBotStepResult.Arrived) state.BreakOffArrived = true;
        else if (step is PlayerBotStepResult.Blocked)
        {
            ReportBlocked(state, player, "breakoff", goal, now);
            state.BreakOffBlocked.Add(goal);                                            // never chosen again (NextBreakOffPoint)
            (state.BreakOffGoal, state.NextBreakOffRetryTick) = (null, 0);              // #167 E T1: a new point at once,
            if (state.BreakOffChasers.Count == 0)                                       // #167 E T2b: running on, none after
            {                                                                           //   it: done
                EndBreakOff(state, player, now, Shook);
                return false;
            }
            return GoalOrHold(state, player, now, nearest, freeDistance, TurnBlocked);  //   else it holds or turns
        }
        return true;
    }

    // #167 E T2b: whether the bot stands in the reach of a monster that would aggro (IsBreakOffAdd, AddReach; InAddReach's
    // ring), from one scan of AddScanMargin round it (500 aggro and AddMargin), once per Done with a point ahead.
    private static bool StandsInAddReach(GamePlayer player)
    {
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)AddScanMargin))
            if (IsBreakOffAdd(npc, player, player.Realm) && AddReach(player, npc) is > 0 and int reach)
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        bool inReach = InAddReach(new Vector3(player.X, player.Y, player.Z), adds);
        adds.Clear();                                                                   // no monster held past the scan
        return inReach;
    }

    // #167 E T1: with no point to run to, a try for one each BreakOffRetryMilliseconds (BreakOffGoal with its 800 pass,
    // one add scan: GetNPCsInRadius of 3,200, up to 50 OnMesh calls and 8 corridor tests, only while a bot has no
    // point); none, it turns (NoPointTurns: a chaser not held in reach, a hit while it holds, or a caster or pet class with
    // power), BREAKOFF turn=why (the hold's first why: nopoint or blocked), else it holds where it stands for the chasers
    // to give up (Done), catch it (Caught) or the cap (Fight), and heals itself there (HoldForPoint). holds= counts the
    // tries that found none while it held. True while the break-off goes on.
    private static bool GoalOrHold(PlayerBotState state, GamePlayer player, long now, int nearest, int freeDistance,
        string why)
    {
        bool tried = false;
        if (now >= state.NextBreakOffRetryTick)
        {
            state.NextBreakOffRetryTick = now + BreakOffRetryMilliseconds;
            if ((state.BreakOffGoal = BreakOffGoal(state, player, relaxed: true)) != null)
            {
                (state.BreakOffHoldSince, state.BreakOffHoldWhy) = (0, null);           // it runs on
                return true;
            }
            tried = true;
        }
        if (NoPointTurns(freeDistance, HitWhileHolding(state.BreakOffHoldSince, player.LastAttackedByEnemyTick, now),
                tried && CasterOrPetWithPower(state, player), nearest))
        {
            state.BreakOffTurn = why;
            EndBreakOff(state, player, now, Fought);                                    // step 14 picks a hitter
            return false;
        }
        state.Mover.Stop(now);
        HoldForPoint(state, now, why, tried, () => SelfHeal(state, player, now));
        return true;
    }

    /// <summary>#167 E T1: a hold for want of a point (GoalOrHold, the bot stopped): its first think's why and time,
    /// holds= for a try that found none, and the self-heal (selfHeal: SelfHeal), Think's step 11b, which a think the
    /// break-off holds never reaches. Before T1 no point ended the break-off and the heal began in that think, the
    /// chaser still far off; held, a Healer-role bot (E-L's one-monster break-offs) cast nothing until it turned, then
    /// healed under melee. A heal it casts keeps the cast's activity (CastOnSelf), and the next think's WaitsForSelfCast
    /// waits it out; a hit meanwhile turns it after (HitWhileHolding).</summary>
    public static void HoldForPoint(PlayerBotState state, long now, string why, bool tried, Func<bool> selfHeal)
    {
        if (state.BreakOffHoldSince == 0) (state.BreakOffHoldSince, state.BreakOffHoldWhy) = (now, why);
        if (tried) state.BreakOffHolds++;
        state.Walk = PlayerBotWalk.BreakOff;
        if (!selfHeal()) state.Activity = PlayerBotActivity.Fighting;
    }

    // #167 E T1: a caster or pet class that broke off with power (not why=spent: breakoff E4's spent turn holds) and can
    // still pay for a damage spell (CanNuke, as a spent caster's test reads it). Read only after a try for a point failed,
    // so no per-think spell walk.
    private static bool CasterOrPetWithPower(PlayerBotState state, GamePlayer player)
    {
        if (state.BreakOffWhy == Spent || RoleOf(player.CharacterClass.ID) is not (PlayerBotRole.Caster or PlayerBotRole.Pet))
            return false;
        KnowSpells(state, player);
        return state.Nukes.Count > 0 && CanNuke(state, player);
    }

    // A break-off's start (BreakOffFires) from the monsters hitting the bot (Attacker's, each with the bot as its target)
    // and, only once that holds, the speed of every chaser (the hitters and those within IncomingRadius coming at it;
    // #167 D2: a held one too, KeepsChaser) against the sprint its endurance pays for now (SprintSeconds, Outruns), and
    // #167 D2 their spells (RangedHarm). A mezzed or stunned monster is on it no more (OnBot). C4's add control comes first
    // (AddControlReady, read only once the rest holds). No point to run to: it fights on. A scan that starts none waits
    // TargetRefreshMilliseconds, as Incoming's. #167 D1: one monster in the one-monster case (the hitters' names it avoids,
    // HoldReadyNow), and a spent caster at any health (SpentNow: an engaged caster's held target only, its cheap checks
    // first); BREAKOFF why= the rule (BreakOffWhy). #167 D2: one that would fire but doesn't logs why (ReportRefusal).
    // The review of D: its start in seams (BreakOffScans, BreakOffStartWhy).
    private static bool StartsBreakOff(PlayerBotState state, GamePlayer player, long now)
    {
        if (now < state.NextBreakOffTick) return false;
        byte health = player.HealthPercent;
        PlayerBotRole role = RoleOf(player.CharacterClass.ID);
        bool spent = SpentNow(state, player, role);                                     // #167 D1
        if (!BreakOffScans(spent, health)) return false;                                // no scan above it
        List<GameNPC> chasers = state.BreakOffChasers;
        List<(byte Health, int Distance, bool Avoided)> hitters = _hitters ??= new();
        chasers.Clear();
        hitters.Clear();
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
        {
            if (living is not GameNPC npc || !OnBot(IsAttacking(player, npc), npc.IsCrowdControlled) || chasers.Contains(npc))
                continue;
            chasers.Add(npc);
            hitters.Add((npc.HealthPercent, player.GetDistanceTo(npc), IsAvoided(state.AvoidUntil, npc.Name, now)));
        }
        int onBot = chasers.Count;
        bool controlReady = false;
        string why = BreakOffStartWhy(state, role, spent, health, HoldReadyNow(state, player, role, chasers, now),
            hitters, now, () => controlReady = AddControlReady(state, player, now));                       // #167 D1
        bool fires = why != null;
        hitters.Clear();
        if (!fires)
        {
            chasers.Clear();
            if (controlReady) ReportRefusal(state, player, now, onBot, "control");    // #167 D2: the rule held, C4's first
            return false;
        }
        foreach (GameNPC npc in player.GetNPCsInRadius(IncomingRadius))
            if (KeepsChaser(npc.TargetObject == player && IsAttackable(player, npc), player.GetDistanceTo(npc)) &&
                IsExperienceTarget(npc) && !chasers.Contains(npc))
                chasers.Add(npc);
        int fastest = 0;
        bool ranged = false;
        foreach (GameNPC npc in chasers)
        {
            fastest = Math.Max(fastest, npc.MaxSpeed);
            if (!ranged && npc.Spells is { Count: > 0 } spells)                         // #167 D2
                ranged = RangedHarm(spells.Select(spell => (spell.SpellType, spell.Range)));
        }
        int sprint = SprintSeconds(player.Endurance, player.GetModified(eProperty.EnduranceRegenerationAmount),
            player.GetModified(eProperty.FatigueConsumption), LongWindCost(player));
        state.BreakOffBlocked.Clear();
        Vector3? goal = Outruns(fastest, sprint, ranged) ? BreakOffGoal(state, player) : null;
        if (BreakOffRefusal(true, false, fastest, sprint, ranged, goal != null) is string refusal)
        {
            chasers.Clear();
            state.NextBreakOffTick = now + TargetRefreshMilliseconds;
            ReportRefusal(state, player, now, onBot, refusal, player.MaxSpeed, fastest);   // #167 D2; E T1: the speeds
            return false;
        }
        (state.BreakOffSince, state.BreakOffGoal, state.BreakOffArrived, state.BreakOffSprintOffAt) = (now, goal, false, -1);
        (state.BreakOffSpeed, state.BreakOffChaserSpeed) = (player.MaxSpeed, fastest);   // #167 E T1: before the sprint
        (state.BreakOffHealth, state.BreakOffEndurance, state.BreakOffAttackers) = (health, player.EndurancePercent, onBot);
        state.BreakOffRate = HealthRate(state.HealthSamples, now, health);              // #167 BREAKOFF rate=
        (state.BreakOffWhy, state.CaughtSince) = (why, 0);                              // #167 D1 why= (BrokeOffFrom reads it)
        state.KilledWithAdds = false;
        BrokeOffFrom(state, player.CurrentRegionID, new(player.X, player.Y, player.Z),
            chasers.Select(npc => npc.Name).Append(state.Target?.Name), now);
        if (state.Engaged && state.Target != null)
        {
            (state.FightAdds, state.FightAdd) = FoldBreakOffAdds(state.FightAdds, state.FightAdd, onBot,   // #167 D2: adds=
                now - state.EngagedSince, health);                                                         //   >= attackers=
            ReportFight(state, player, now, false, true);                               // FIGHT result=dropped breakOff=1
        }
        Disengage(state, player);
        if (player.Endurance > SprintStartEndurance) player.Sprint(true);              // GamePlayer.cs:8625-8652
        return true;
    }

    // #167 D1: a caster spent (CasterSpent), read only for an engaged caster whose target lives above
    // CasterBreakOffTargetPercent and is held (Hold): no per-think spell walk for the rest. A caster with no damage spell
    // at all is never spent.
    private static bool SpentNow(PlayerBotState state, GamePlayer player, PlayerBotRole role)
    {
        if (role != PlayerBotRole.Caster || !state.Engaged || state.Target is not GameLiving target || !target.IsAlive ||
            target.HealthPercent <= CasterBreakOffTargetPercent || !Hold(target).Held)
            return false;
        KnowSpells(state, player);
        return state.Nukes.Count > 0 && CasterSpent(CanNuke(state, player), held: true, target.HealthPercent);
    }

    // #167 D1: a damage spell the bot can pay for now, on its recast or not (power is what a spent caster lacks); the
    // area spells' add scan (Nuke) isn't needed for that. #167 D3: at its real cost (CasterCanCast; no reserve, the
    // target is held).
    private static bool CanNuke(PlayerBotState state, GamePlayer player)
    {
        bool quickcasting = player.effectListComponent.ContainsEffectForEffectType(eEffect.QuickCast);
        foreach ((Spell spell, SpellLine line) in state.Nukes)
            if (CasterCanCast(PercentPowerBase(player), player.Mana, spell.Power, 0, FocusFactor(player, spell, line),
                    quickcasting, spell.CastTime))
                return true;
        return false;
    }

    // #167 D3: Engage's CasterCanCast for the bot's spell (none: false), with the cheapest known control's cost kept in
    // reserve while its target isn't held (ControlReserve; Controls cached per level, KnowSpells) and quickcast when the
    // cast goes under Quickcast. #167 E C2b: no reserve for the killing quickcast.
    private static bool CasterCanPay(PlayerBotState state, GamePlayer player, Spell spell, SpellLine line, bool held,
        bool quickcast, bool killingQuickcast)
    {
        if (spell == null) return false;
        KnowSpells(state, player);
        return CasterCanCast(PercentPowerBase(player), player.Mana, spell.Power,
            ControlReserve(ControlCosts(state, player), held, killingQuickcast), FocusFactor(player, spell, line), quickcast,
            spell.CastTime);
    }

    // #167 D3: each known control's cost (PowerCost), not under Quickcast.
    private static IEnumerable<int> ControlCosts(PlayerBotState state, GamePlayer player)
    {
        foreach ((Spell spell, SpellLine line) in state.Controls)
            yield return PowerCost(PercentPowerBase(player), spell.Power, FocusFactor(player, spell, line), false, spell.CastTime);
    }

    // #167 D3: the pool a percent cost reads (SpellHandler.cs:1206-1212): the class's max power at the bot's level and
    // base stat, else its max power.
    private static int PercentPowerBase(GamePlayer player) =>
        player.CharacterClass.ManaStat is not eStat.UNDEFINED
            ? player.CalculateMaxMana(player.Level, player.GetBaseStat(player.CharacterClass.ManaStat)) : player.MaxMana;

    // #167 D3: the spell's focus factor (FocusCostFactor, SpellHandler.cs:1214-1233): a focus caster's on a line with a
    // focus (SkillBase.SpecToFocus), from its focus level and its spec.
    private static double FocusFactor(GamePlayer player, Spell spell, SpellLine line)
    {
        if (!player.CharacterClass.IsFocusCaster || line == null) return 1;
        eProperty focus = SkillBase.SpecToFocus(line.Spec);
        return focus is eProperty.Undefined ? 1
            : FocusCostFactor(true, player.GetModified(focus), spell.Level, player.GetModifiedSpecLevel(line.Spec));
    }

    // #167 D1: a caster's hold ready on the one monster on it (onBot: StartsBreakOff's; none or more, none), the
    // one-monster case's (OneMonsterHold; the review of D): a control the bot casts at it now (ControlCastAt), else, when
    // it is the bot's target (at a kill, the one on it), its target's hold (TargetHoldReady). Not a caster: none.
    private static bool HoldReadyNow(PlayerBotState state, GamePlayer player, PlayerBotRole role, List<GameNPC> onBot,
        long now)
    {
        if (role != PlayerBotRole.Caster || onBot.Count != 1 || onBot[0] is not { IsAlive: true } npc) return false;
        var cast = player.castingComponent?.SpellHandler;
        return OneMonsterHold(ControlCastAt(cast?.Spell, cast?.Target, npc),
            state.Target?.IsAlive != true || state.Target == npc, () => TargetHoldReady(state, player, npc, now));
    }

    // #167 D1: a caster's hold on its target ready (HoldReady): held now (Hold), or the control CasterStep would cast at it
    // (TargetControl, read at ControlReadTick: the review of D, a resisted root's margin), with Quickcast while hits
    // interrupt the bot.
    private static bool TargetHoldReady(PlayerBotState state, GamePlayer player, GameNPC npc, long now)
    {
        if (Hold(npc).Held) return true;
        KnowSpells(state, player);
        bool quickcasting = player.effectListComponent.ContainsEffectForEffectType(eEffect.QuickCast);
        Ability quickcast = player.GetAbility(Abilities.Quickcast);
        return HoldReady(false,
            TargetControl(state, player, npc, TimeToContact(player.GetDistanceTo(npc), npc.MaxSpeedBase),
                ControlReadTick(now, state.NextControlTick)).Item1 != null,
            player.IsBeingInterruptedByOther && !quickcasting,
            QuickcastReady(quickcast != null, quickcasting, quickcast == null ? 0 : player.GetSkillDisabledDuration(quickcast),
                state.NextQuickcastTick, player.IsCasting, now), quickcasting);
    }

    // #167 D2: PLAYER_BOT_BREAKOFF outcome=refused why= (BreakOffRefusal) at the bot's health, endurance and the monsters on
    // it now, once a fight (BreakOffRefused, cleared by OpenFight). #167 E T1: and its run speed and the fastest chaser's
    // (speed=, chaserSpeed=; -1 before they are read: a control refusal).
    private static void ReportRefusal(PlayerBotState state, GamePlayer player, long now, int onBot, string why,
        int speed = -1, int chaserSpeed = -1)
    {
        if (state.BreakOffRefused) return;
        state.BreakOffRefused = true;
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatBreakOff(player.Name, player.Level, player.HealthPercent,
                player.EndurancePercent, onBot, 0, Refused, HealthRate(state.HealthSamples, now, player.HealthPercent), why,
                -1, player.EndurancePercent, speed: speed, chaserSpeed: chaserSpeed));      // #167 E-S: no run
    }

    // A sprint's cost a second before the regeneration (GamePlayer.cs:2396-2405): LongWind, less Long Wind's 20-100%.
    private static int LongWindCost(GamePlayer player) =>
        player.GetAbility<AtlasOF_LongWindAbility>() is { } longWind
            ? LongWind - longWind.GetAmountForLevel(player.CalculateSkillLevel(longWind)) * LongWind / 100 : LongWind;

    // The monsters still after a break-off's bot: one goes when it dies or turns from the bot (its aggro state's exit
    // clears its target 25 s after its last swing, StandardMobState.cs:89, :121) or is past ChaseRadius (KeepsChaser);
    // #167 D2: a mezzed or stunned one stays, as it comes back (C4 dropped it, OnBot). One that hits the bot or targets
    // it on the way joins them.
    private static void KeepChasers(PlayerBotState state, GamePlayer player)
    {
        List<GameNPC> chasers = state.BreakOffChasers;
        for (int i = chasers.Count - 1; i >= 0; i--)
            if (!KeepsChaser(IsAttacking(player, chasers[i]), player.GetDistanceTo(chasers[i])))
                chasers.RemoveAt(i);
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
            if (living is GameNPC npc && KeepsChaser(IsAttacking(player, npc), player.GetDistanceTo(npc)) &&
                !chasers.Contains(npc))
                chasers.Add(npc);
    }

    // #167 D2: the nearest chaser's distance (BreakOffStep's, a held one too) and the nearest one not held (Hold), with its
    // distance (the caught clock's, CaughtNow); none: int.MaxValue.
    private static (int Nearest, GameNPC Free, int FreeDistance) NearestChasers(PlayerBotState state, GamePlayer player)
    {
        (int nearest, GameNPC free, int freeDistance) = (int.MaxValue, null, int.MaxValue);
        foreach (GameNPC npc in state.BreakOffChasers)
        {
            int distance = player.GetDistanceTo(npc);
            nearest = Math.Min(nearest, distance);
            if (distance < freeDistance && !Hold(npc).Held) (free, freeDistance) = (npc, distance);
        }
        return (nearest, free, freeDistance);
    }

    // #167 E-S: the fastest monster still after a break-off's bot (KeepChasers' list; no scan).
    private static int FastestChaser(PlayerBotState state)
    {
        int fastest = 0;
        foreach (GameNPC npc in state.BreakOffChasers) fastest = Math.Max(fastest, npc.MaxSpeed);
        return fastest;
    }

    // #167 D2: caught on the run (CaughtNow), a control at the chaser (free: the nearest not held) as at an add: AddAction's
    // step with it in contact (addContactMs 0), hits interrupting the bot and no target first: an instant or
    // uninterruptible control at once, else with Quickcast on, turned on first (the next think casts). Any control the
    // chaser takes (Takes), a root too: the bot runs on out of a rooted one's reach (the add step's ControlKeepsAddOff is
    // for a bot that stays). MaxControlTries casts that held nothing and it stops trying on that chaser (AddControlTries,
    // the add step's). True when it acted: it stands for the cast, then runs on (BreaksOffNow). #167 E C1: no root or mez
    // on a chaser its own DoT ticks on (HoldSurvivesDot: the next tick would free it).
    private static bool CaughtControl(PlayerBotState state, GamePlayer player, GameNPC chaser, long now)
    {
        if (chaser == null || now < state.NextAddTick) return false;
        KnowSpells(state, player);
        if (state.Controls.Count == 0) return false;
        if (chaser != state.ControlledAdd) (state.ControlledAdd, state.AddControlTries) = (chaser, 0);
        if (state.AddControlTries >= MaxControlTries) return false;
        bool dotOn = OwnDotOn(player, chaser);                                          // #167 E C1: read once
        (Spell control, SpellLine line) = ChooseControl(state.Controls, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
                HasPowerFor(player.MaxMana, player.Mana, spell.Power) &&
                player.IsWithinRadius(chaser, player.castingComponent.CalculateSpellRange(spell)),
            spell => Takes(player, chaser, spell) && HoldSurvivesDot(spell, dotOn), forAdd: true,
            petClass: RoleOf(player.CharacterClass.ID) == PlayerBotRole.Pet);
        if (control == null) return false;
        bool quickcasting = player.effectListComponent.ContainsEffectForEffectType(eEffect.QuickCast);
        Ability quickcast = player.GetAbility(Abilities.Quickcast);
        bool quickcastReady = QuickcastReady(quickcast != null, quickcasting,
            quickcast == null ? 0 : player.GetSkillDisabledDuration(quickcast), state.NextQuickcastTick, player.IsCasting, now);
        PlayerBotAddAction step = AddAction(true, true, 0, false, control.CastTime, Interruptible(control), true,
            quickcastReady, quickcasting);
        if (step is PlayerBotAddAction.None) return false;
        state.Mover.Stop(now);
        if (step is PlayerBotAddAction.QcControl)
        {
            UseQuickcast(state, player, now, quickcast);                                // then the next think casts it
            return true;
        }
        if (!CastAtAdd(state, player, chaser, chaser, control, line, now)) return false;   // no sight: it turns
        state.AddControlTries++;
        return true;
    }

    // #167 D2: the chaser a caught bot with no control turns on (WeakestInReach within CaughtReach).
    private static GameNPC WeakestChaser(PlayerBotState state, GamePlayer player)
    {
        List<(GameNPC, byte, int, bool)> chasers = new(state.BreakOffChasers.Count);
        foreach (GameNPC npc in state.BreakOffChasers)
            chasers.Add((npc, npc.HealthPercent, player.GetDistanceTo(npc), Hold(npc).Held));
        return WeakestInReach(chasers, CaughtReach);
    }

    // #167 D2: a caught bot's target (WeakestChaser), taken as RefreshTarget takes one (TakeTarget) and kept to the next
    // search (RefreshTarget keeps a target until then): else its self-defence took the nearest hitter.
    private static void TurnOn(PlayerBotState state, GamePlayer player, GameNPC npc, long now)
    {
        TakeTarget(state, player, npc, -1);
        state.NextTargetRefreshTick = now + TargetRefreshMilliseconds;
    }

    // The next break-off point (NextBreakOffPoint) from where the bot is: the ring points (BreakOffRings, SpotDirections
    // round it) on the navmesh, away from its chasers, out of the reach of every other monster round it that would
    // aggro (AddReach; not one with the bot as its target: it is on it already; a hostile keep guard too, #167 D7 and the
    // review of D: IsBreakOffAdd), not one it was blocked on, and one it has a corridor to (PlayerTravelPath.TryBuildCorridor,
    // as RelocateIfOutgrown's; with no navmesh, any); #167 D2: none, the point straight away from them under the same
    // tests, one try (StraightAwayGoal); none, null. #167 E T1: relaxed (with no point, GoalOrHold), with none of those
    // the 800 ring and the straight-away point 800 out too, never in another monster's reach (NearBreakOffGoal), from
    // the same add scan (RelaxedBreakOffRings lie inside the scan's radius).
    private static Vector3? BreakOffGoal(PlayerBotState state, GamePlayer player, bool relaxed = false)
    {
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in player.GetNPCsInRadius((ushort)(BreakOffRings[^1] + AddScanMargin)))
            if (IsBreakOffAdd(npc, player, player.Realm) && AddReach(player, npc) is > 0 and int reach)   // #167 D7
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        Vector3 here = new(player.X, player.Y, player.Z);
        IPathfindingMgr nav = PathfindingProvider.Instance;
        Zone zone = player.CurrentZone;
        bool meshed = zone != null && nav.HasNavmesh(zone);
        bool Walkable(Vector3 point) => !meshed || PlayerTravelPath.TryBuildCorridor(nav, zone, here, point, out _);
        Vector3? OnMeshHere(Vector3 point) => OnMesh(player, point);
        List<Vector3> Ring(int[] rings) => RingPoints(here, rings, SpotDirections).Select(OnMeshHere).Where(p => p.HasValue)
            .Select(p => p.Value).ToList();
        List<Vector3> chasers = state.BreakOffChasers.Select(npc => new Vector3(npc.X, npc.Y, npc.Z)).ToList();
        Vector3? goal = NextBreakOffPoint(here, chasers, Ring(BreakOffRings), adds, state.BreakOffBlocked, Walkable) ??
            StraightAwayGoal(here, chasers, BreakOffRings[0], OnMeshHere, state.BreakOffBlocked, Walkable);
        if (goal == null && relaxed)                                                    // #167 E T1
            goal = NearBreakOffGoal(here, chasers, Ring(RelaxedBreakOffRings), adds, state.BreakOffBlocked, Walkable,
                OnMeshHere);
        adds.Clear();                                                                   // no monster held past the scan
        return goal;
    }

    // A break-off's end: its line (PLAYER_BOT_BREAKOFF, #167 D1 why=), the state's end (FinishBreakOff: the relocation
    // owed, OwesReplan's rule as after a death; fought, no new break-off for BreakOffMilliseconds), and unless the bot died
    // (the death's own recovery then, HandleDeath) the sprint off. #167 E T1: turn=, holds=, speed= and chaserSpeed=.
    private static void EndBreakOff(PlayerBotState state, GamePlayer player, long now, string outcome)
    {
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatBreakOff(player.Name, player.Level, state.BreakOffHealth,
                state.BreakOffEndurance, state.BreakOffAttackers, (now - state.BreakOffSince) / 1000, outcome, state.BreakOffRate,
                state.BreakOffWhy, state.BreakOffSprintOffAt < 0 ? -1 : state.BreakOffSprintOffAt / 1000,   // #167 E-S
                player.EndurancePercent, state.BreakOffTurn ?? "-", state.BreakOffHolds, state.BreakOffSpeed,   // #167 E T1
                state.BreakOffChaserSpeed));
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
            BarOldAnchor(state, Died, state.DeathSpot, now);                            // #167 D9: the anchor it walked
                                                                                        //   to (RoamDestination, cleared below)
            state.NoXpDeaths = AutonomousDeathRecoveryPolicy.NextNoExperienceDeathStreak(state.NoXpDeaths,   // #167 F4:
                state.LastDeathTick, now, state.Gained > state.GainedAtDeath);          //   the back-off (AWBC:3778-3786)
            (state.LastDeathTick, state.GainedAtDeath) = (now, state.Gained);
            state.ReplanOwed = ReplanOwedAfterDeath(state.Task != null, CanRelocate(state, player), state.DeathSpot,
                state.Anchor);                                                          // wherever (C1b)
            state.NextRelocateTick = 0;                                                 //   and the plan before the walk back
            state.AfterRelease = false;
            (state.WalkOutGoal, state.WalkOutSprint) = (null, false);                    // #167 D4: Die ends the sprint
            state.CappedSince = 0;                                                      //   (GameLiving.cs:2058)
            state.Target = null;
            state.Engaged = false;
            state.RoamDestination = null;
            state.RoamingSince = 0;                                                     // back at the bind point: a fresh start
            state.RelocateRejected.Clear();
            state.Mover.Reset();
            PlayerBotDriverReport.Death();
        }
        if (!state.KillerAvoided && state.Killer is GameNPC killer)                    // and its killer, once the reaper
        {                                                                               //   names it (AwaitsDying), and
            Remember(state.AvoidUntil, killer.Name, now);                               //   its con (#167 D6)
            RememberKillerCon(state.KillerCons, ConLevels.GetConColor(player.GetConLevel(killer)), now);
            state.KillerAvoided = true;
        }
        if (ReleaseDue(now, player.DeathTick, state.LastDeathTick, player.IsBeingHandledByReaperService,   // #167 D7
                state.ReleaseAskedAt))
        {
            state.ReleaseAskedAt = now;
            state.ReleaseTries++;
            if (Log.IsInfoEnabled)
                Log.Info(PlayerBotDriverReport.FormatRelease(player.Name, player.Level, state.ReleaseTries,
                    now - Math.Max(player.DeathTick, state.LastDeathTick)));
            player.Release(eReleaseType.Normal, true);                                     // GP:1425
        }
    }

    /// <summary>Whether HandleDeath asks the release now (#167 D7). Not while the reaper has the bot (beingReaped: Die ran,
    /// ProcessDeath not yet, ReaperService.cs:55); DeathReleaseDelayMilliseconds from the later of the server's DeathTick,
    /// which ProcessDeath stamps (GamePlayer.cs:5953), and the driver's first dead think (lastDeathTick); and again
    /// ReleaseRetryMilliseconds after an ask (askedAt; 0, none) that didn't revive the bot. A bow's or a bolt's hit lands
    /// from a timer (AttackAction.cs:364-365), in the think's phase: a think before ProcessDeath read the last death's
    /// DeathTick and released at once; ProcessDeath then killed the revived bot (GameLiving.cs:2066) and the one-shot
    /// release was spent with no think alive to clear it (Pbotaaacv's last 6 minutes in st-deathsB and st-deathsC).</summary>
    public static bool ReleaseDue(long now, long deathTick, long lastDeathTick, bool beingReaped, long askedAt) =>
        !beingReaped && now - Math.Max(deathTick, lastDeathTick) >= DeathReleaseDelayMilliseconds &&
        (askedAt == 0 || now - askedAt >= ReleaseRetryMilliseconds);

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
            state.BreakOffSince != 0, inFight ? state.EngageDistance : -1,                //   and on a break-off's run
            inFight ? state.FightHeldMs / 1000 : -1, inFight ? state.FightCtlLanded : -1, //   st-deathsD's: as FIGHT's, but
            inFight ? state.FightCtlFailed : -1, inFight ? state.FightHpAtReach : -1,     //   rate= at the death, walk=
            inFight ? state.FightTargetHpAtReach : -1,                                    //   the last think's alive out of
            HealthRate(state.HealthSamples, now, player.HealthPercent), inFight ? state.WouldBreakHp : -1,   //   a fight,
            inFight ? state.FightWalk : state.LastWalk, player.EndurancePercent,          //   and end=; #167 D8
            inFight ? state.FightKiteNone : null,                                         //   kiteNone=, FIGHT's;
            inFight ? state.FightDots : -1,                                               // #167 E C1 dots=,
            inFight ? (state.FightKillingQuickcast ? 1 : 0) : -1,                         // #167 E C2b qcKill=,
            inFight ? state.FightTargetMezzes : -1,                                       // #167 E C4 ctlMez=,
            inFight ? state.FightRaceLeft : NoRace,                                       // #167 E-L raceLeft=,
            inFight ? state.FightStylesHeld : -1, inFight ? state.FightCastsHeld : -1);   // #167 E-R as FIGHT's
        string head = $"PLAYER_BOT_DEATH bot={player.Name} level={player.Level} at=({player.X},{player.Y},{player.Z}) " +
                      $"zone={player.CurrentZone?.ID} {fight} attackers={state.Attackers} deathSteps={state.DeathSafetySteps} " +
                      $"aliveSec={(now - state.AliveSince) / 1000} task={state.Task != null}";
        if (AwaitsDying(state.Killer, player.IsBeingHandledByReaperService))
            state.PendingDeath = (head, detail, target);                                   // the next think writes it
        else
            Log.Info(DeathLine(head, detail, state.Killer, target));
    }

    // #167 st-deathsD's fields: a fight think in Engage (TrackFight) from the bot and its target, the hold as Hold reads
    // it (a caster's is Engage's own, held), and the control cast awaited settled when due (SettleControl).
    private static void WatchFight(PlayerBotState state, GamePlayer player, GameLiving target, PlayerBotRole role, bool held,
        long now)
    {
        TrackFight(state, now, role == PlayerBotRole.Caster ? held : Hold(target).Held, player.HealthPercent,
            target.HealthPercent, player.LastAttackedByEnemyTick);
        SettleControl(state, player, now, force: false);
    }

    // #167 ctlLanded= and ctlFailed=: the control cast awaited (AwaitControl) once it could have ended (ControlResolves),
    // or at once when another is cast (force). Its target dead by then counts neither way; so does one still under way
    // when the fight ends (OpenFight drops it).
    private static void SettleControl(PlayerBotState state, GamePlayer player, long now, bool force)
    {
        if (state.PendingControl.Spell is not Spell spell) return;
        (_, GameLiving target, long at) = state.PendingControl;
        if (!force && !ControlResolves(now, at, spell.CastTime, player.IsCasting)) return;
        state.PendingControl = default;
        if (target?.IsAlive != true) return;
        if (ControlLanded(target.effectListComponent.GetSpellEffects().Where(effect => effect.SpellHandler?.Spell != null)
                .Select(effect => (effect.SpellHandler.Spell.ID, effect.SpellHandler.Caster == player, effect.StartTick)),
                spell.ID, at))
            state.FightCtlLanded++;
        else
            state.FightCtlFailed++;
    }

    // #167 ctlLanded=: a control cast asked for (CasterStep's at the target, AddAct's at an add), awaited; one still
    // awaited settles now.
    private static void AwaitControl(PlayerBotState state, GamePlayer player, Spell spell, GameLiving target, long now)
    {
        SettleControl(state, player, now, force: true);
        state.PendingControl = (spell, target, now);
    }

    // #167 C1: PLAYER_BOT_BACK at the first fight in reach after a release (the verifier's logging: the 40 s more from a
    // death to the next fight in st-deathsB was not the failed replan, gap.py). Its phases are counted by think; the
    // rest of sec= is the rests, heals, buffs and the walk to the target. OpenFight says when (BackPending).
    private static void ReportBack(PlayerBotState state, GamePlayer player, long now)
    {
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatBack(player.Name, player.Level, (now - state.ReleasedAt) / 1000,
                state.BackStepOutMs / 1000, state.BackHoldMs / 1000, state.BackWalkMs / 1000,
                BackReplan(state.BackOwed, state.BackMoved, state.BackSame), state.BackImmuneMs / 1000));
    }

    // One line per fight the bot was in when it settles (SettleTarget), so a fight's cost shows beside the deaths; or
    // a break-off drops it (StartsBreakOff, brokeOff).
    private static void ReportFight(PlayerBotState state, GamePlayer player, long now, bool killed, bool brokeOff)
    {
        if (!Log.IsInfoEnabled) return;
        GameLiving target = state.Target;
        SettleControl(state, player, now, force: false);                                // #167 ctlLanded=, when due
        Log.Info(PlayerBotDriverReport.FormatFight(new(player.Name, player.Level, target.Name, target.EffectiveLevel,
            ConLevels.GetConColor(player.GetConLevel(target)), state.FromDefence, killed, state.EngageHealth, player.HealthPercent,
            state.EngageMana, PowerPercent(player.MaxMana, player.ManaPercent), (now - state.EngagedSince) / 1000, state.FightAdds,
            state.Swings, state.NotVisible, state.FightCasts, state.LifeKills, state.LifeRests, state.EngagePet,
            state.FightHeals, state.FightQuickcast, state.EngageBuffs, state.FightPetCasts, state.FightWaits,
            state.FightControls, state.FightKites, state.FightCtlBroken, state.FightAddControls, state.FightAdd.At,
            state.FightAdd.Hp, brokeOff, state.EngageDistance, state.PickCrowd, state.FightHeldMs / 1000, state.FightCtlLanded,
            state.FightCtlFailed, state.FightHpAtReach, state.FightTargetHpAtReach,      // #167 st-deathsD's
            HealthRate(state.HealthSamples, now, player.HealthPercent), state.WouldBreakHp, state.FightWalk,
            state.FightKiteNone, state.FightDots, state.FightKillingQuickcast,          // #167 D8, E C1, E C2b,
            state.FightTargetMezzes, state.FightRaceLeft, state.EngageEndurance,       //   E C4, E-L, E-R
            player.EndurancePercent, state.FightStylesHeld, state.FightCastsHeld,
            state.FightSight, state.FightBlind, state.FightBlindSteps)));               // #167 E M2, R1
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
    // pull. #167 E CY3: else it holds the pull up to TurretWaitMilliseconds, then leaves the target for
    // UnreachableSkipMilliseconds: the pick passes it over, self-defence still answers it (LeaveTurretTarget; F10b took
    // the old approach then: the pull from its spell's reach that the turret didn't reach). A blocked walk: the old
    // approach for UnreachableSkipMilliseconds. True when it acted.
    private static bool AnimistApproach(PlayerBotState state, GamePlayer player, GameLiving target, int spellStop, long now)
    {
        if (player.CharacterClass.ID != (int)eCharacterClass.Animist || state.Engaged || state.FromDefence ||
            IsAttacking(player, target) || now < state.NextAnimistStandTick ||
            player.ControlledBrain?.Body is not TurretPet turret || !turret.IsAlive || turret.CurrentRegion != player.CurrentRegion)
        {
            state.TurretWaitSince = 0;
            return false;
        }
        int reach = turret.TurretSpell is Spell spell ? turret.castingComponent.CalculateSpellRange(spell) : 1_000;
        Vector3 here = new(player.X, player.Y, player.Z), planted = new(turret.X, turret.Y, turret.Z),
            goal = new(target.X, target.Y, target.Z);
        (Vector3 stand, bool replant) = AnimistStand(here, planted, goal, spellStop, reach,
            turret.TurretSpell?.SpellType is eSpellType.DirectDamage);                 // Arboreal's (world DB)
        PlayerBotAnimistStep step = AnimistStepFor(Vector3.Distance(here, stand), replant,
            replant && CanReplant(state, player, now), state.TurretWaitSince, now);
        state.TurretWaitSince = step is PlayerBotAnimistStep.Wait ? (state.TurretWaitSince == 0 ? now : state.TurretWaitSince) : 0;
        if (step is PlayerBotAnimistStep.Replant or PlayerBotAnimistStep.Skip && Log.IsInfoEnabled)
            Log.Info(TurretLine(player.Name, player.Level, step, target.Name, ConLevels.GetConColor(player.GetConLevel(target)),
                (int)Vector3.Distance(goal, planted), reach));
        switch (step)
        {
            case PlayerBotAnimistStep.Pull:
                return false;
            case PlayerBotAnimistStep.Wait:                                             // the in-combat flag runs out
                state.Mover.Stop(now);
                player.attackComponent.StopAttack();
                return true;
            case PlayerBotAnimistStep.Skip:
                LeaveTurretTarget(state, target, now);
                return true;
            case PlayerBotAnimistStep.Walk:
                player.attackComponent.StopAttack();
                state.Activity = PlayerBotActivity.Hunting;
                state.Walk = PlayerBotWalk.Hunt;                                        // #167 walk=
                if (state.Mover.StepToward(stand, AnimistStandMargin - 50, now) is not PlayerBotStepResult.Blocked) return true;
                ReportBlocked(state, player, "turret", stand, now);
                state.NextAnimistStandTick = now + UnreachableSkipMilliseconds;
                return false;
            default:                                                                    // Replant, at its stand
                state.Mover.Stop(now);
                player.CommandNpcRelease();                                             // the target is no pet of its:
                state.NextSummonTick = 0;                                               //   its main pet goes
                return true;
        }
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
    // the fight's casts took off it, NukesNeeded), the bot at KillFirstHealthPercent or more (#167 E C2b: or its nuke
    // killing at once, OneNukeKills read at Engage's cast choice) and one monster at most on it; the Quickcast it
    // turned on stays with that nuke (#167 E C2a). #167 D11: none when it isn't worth it (WorthyTargetControl: a GREEN,
    // the bot healthy and alone), so CasterAction nukes. held and breaksOnHit: Engage's Hold of the target. True when
    // it acted.
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
        (Spell control, SpellLine line) = !held && closing && due
            ? WorthyTargetControl(state, player, npc, contact, now) : default;                             // #167 D11
        bool killsFirst = KillsFirstNow(state.EngageTargetHealth, npc.HealthPercent, state.FightCasts, contact, nukeCast,
            quickcastReady, quickcasting, state.OneNukeKills, player.HealthPercent, state.Attackers);   // #167 C11, E C2a-b
        PlayerBotCasterAction step = CasterAction(closing, contact, held, breaksOnHit, control?.CastTime ?? 0, nukeCast,
            interrupted, quickcastReady, quickcasting, killsFirst);
        switch (step)
        {
            case PlayerBotCasterAction.StepBack:
                if (!casterHold || now < state.NextKiteTick) return false;
                if (KitePoint(state, player, npc, stop, distance, distance <= ContactDistance, out string why) is not Vector3 goal)
                {
                    state.NextKiteTick = now + KitePauseMilliseconds;                   // none: as a blocked one (C3)
                    state.FightKiteNone = KiteNoneCounted(state.FightKiteNone, why);    // #167 D8 kiteNone=
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
                AwaitControl(state, player, control, npc, now);                         // #167 ctlLanded=
                state.FightControls++;
                if (control.SpellType is eSpellType.Mesmerize) state.FightTargetMezzes++;   // #167 E C4 ctlMez=
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
    // in range, ready and paid for, the highest of ChooseControl's target order; none, default. #167 D11: CasterStep and
    // TargetFirst read it through WorthyTargetControl; the break-off's TargetHoldReady reads it as it is (it asks under 40%
    // health or with the target held, where ControlWorth holds or isn't asked). #167 E C1: no root or mez while its own
    // DoT ticks on the target (HoldSurvivesDot): the next tick would end it; so neither is a hold ready for the
    // one-monster break-off (TargetHoldReady) then. #167 E C4: the escape's mez first when hits from the target alone
    // would walk the bot out of a shorter stun (ChooseTargetControl: the stun's hold now through the server's
    // diminishing, the walk to Engage's spell stop; the hits as the server keeps them, not Engage's quickcast-masked
    // interrupted, so the think after Quickcast chooses as the one before it).
    private static (Spell, SpellLine) TargetControl(PlayerBotState state, GamePlayer player, GameNPC npc, long contact,
        long now)
    {
        if (now < state.NextControlTick || state.ControlTries >= MaxControlTries) return default;
        bool dotOn = OwnDotOn(player, npc);
        bool hitsOnBot = player.IsBeingInterruptedByOther;
        bool soleHitter = hitsOnBot && LiveHitters(player) <= 1 && npc.TargetObject == player;
        long walk = soleHitter ? StepBackWalkMs(SpellStopDistance(StopSpellRange(state.Spell, state.Nukes,
            player.castingComponent.CalculateSpellRange)), player.GetDistanceTo(npc)) : 0;
        return ChooseTargetControl(state.Controls, spell => player.GetSkillDisabledDuration(spell) <= 0 &&
                HasPowerFor(player.MaxMana, player.Mana, spell.Power) &&
                player.IsWithinRadius(npc, player.castingComponent.CalculateSpellRange(spell)),
            spell => CasterControlAllowed(spell, Takes(player, npc, spell), contact) && HoldSurvivesDot(spell, dotOn),
            spell => HoldNowMs(EffectListService.GetEffectOnTarget(npc, spell.SpellType is eSpellType.Stun
                ? eEffect.NPCStunImmunity : eEffect.NPCMezImmunity) as NpcImmunityEffect, spell.Duration),
            hitsOnBot, soleHitter, walk);
    }

    // #167 D11: TargetControl when a control on the target is worth it (ControlWorth: its con, the bot's health and the
    // live monsters on it, LiveHitters), else none: CasterStep's control and TargetFirst's, one gate for both.
    private static (Spell, SpellLine) WorthyTargetControl(PlayerBotState state, GamePlayer player, GameNPC npc, long contact,
        long now) =>
        ControlWorth(ConLevels.GetConColor(player.GetConLevel(npc)), player.HealthPercent, LiveHitters(player))
            ? TargetControl(state, player, npc, contact, now) : default;

    // #167 D11, D12: the live monsters hitting the bot (IsAttacking: alive, the bot their target), not the attacker list's
    // count, which keeps the last fight's dead target for its attack interval (AttackerTracker.cs:209-219).
    private static int LiveHitters(GamePlayer player)
    {
        int hitters = 0;
        foreach (GameLiving living in player.attackComponent.AttackerTracker.Attackers)
            if (IsAttacking(player, living)) hitters++;
        return hitters;
    }

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
            AwaitControl(state, player, spell, add, now);                               // #167 ctlLanded=
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
    // 9aacd69: any control the target takes, recasting, unpaid or a mez not yet wanted, held the add's back), and worth
    // it (#167 D11, WorthyTargetControl: CasterStep casts none at a GREEN while the bot is healthy and alone).
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
            spell => Takes(player, add, spell) && ControlKeepsAddOff(spell, distance), forAdd: true,
            petClass: role == PlayerBotRole.Pet);
        if (control == null) return default;
        long contact = TimeToContact(distance, add.MaxSpeedBase);
        GameNPC targetNpc = role == PlayerBotRole.Caster && !targetHeld ? target as GameNPC : null;   // else none first
        bool closing = targetNpc?.TargetObject == player;
        long targetContact = closing ? TimeToContact(targetDistance, targetNpc.MaxSpeedBase) : long.MaxValue;
        bool targetFirst = closing && TargetFirst(                                                     // #167 D11
            WorthyTargetControl(state, player, targetNpc, targetContact, now).Item1 != null,
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
        state.Walk = PlayerBotWalk.Kite;                                                // #167 walk=
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

    // The step back's point (KiteCandidates, KiteWalk) on the navmesh, short of Engage's stop (the spell's,
    // SpellStopDistance) so Engage doesn't walk it back in, in 3D too (KiteFits: on a slope or after OnMesh a flat range
    // lay past it; #167 D8: pulled in once, KiteRangeFor), and farther out than the bot stands (KiteRange); none, null,
    // and why (KiteMissRange, or KiteWalk's). In melee (inMelee) the straight-away one last (#167 C3). The adds are the
    // last search's (FindWithin), where they are now, the target not one.
    private static Vector3? KitePoint(PlayerBotState state, GamePlayer player, GameNPC target, int stop, int distance,
        bool inMelee, out string why)
    {
        why = KiteMissRange;
        if (KiteRange(stop, distance) is not int range) return null;
        List<(GameNPC Npc, Vector3 At, int AggroRange)> adds = _reach ??= new();
        adds.Clear();
        foreach (GameNPC npc in state.SearchAdds)
            if (npc != target && AddReach(player, npc) is > 0 and int reach)
                adds.Add((npc, new(npc.X, npc.Y, npc.Z), reach));
        Vector3 at = new(target.X, target.Y, target.Z);
        (Vector3? point, why) = KiteWalk(KiteCandidates(new Vector3(player.X, player.Y, player.Z), at, range, adds, inMelee), at,
            stop, distance, p => OnMesh(player, p), adds);
        adds.Clear();
        return point;
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
    // immune). st-deathsD's fix round: a root only where the server lets it replace the slow on the target (RootTakesNow).
    private static bool Takes(GamePlayer caster, GameLiving target, Spell spell) => spell.SpellType switch
    {
        eSpellType.Stun => Takes(spell, target.HasAbility(Abilities.CCImmunity), target.HasAbility(Abilities.StunImmunity) ||
            target.effectListComponent.ContainsEffectForEffectType(eEffect.StunImmunity),
            EffectListService.GetEffectOnTarget(target, eEffect.NPCStunImmunity) as NpcImmunityEffect, target.HealthPercent),
        eSpellType.Mesmerize => Takes(spell, target.HasAbility(Abilities.CCImmunity),
            target.HasAbility(Abilities.MezzImmunity) || target.effectListComponent.ContainsEffectForEffectType(eEffect.MezImmunity),
            EffectListService.GetEffectOnTarget(target, eEffect.NPCMezImmunity) as NpcImmunityEffect, target.HealthPercent),
        _ => Takes(spell, target.HasAbility(Abilities.CCImmunity), target.HasAbility(Abilities.RootImmunity) ||
            target.effectListComponent.ContainsEffectForEffectType(eEffect.SnareImmunity) ||
            target.effectListComponent.ContainsEffectForEffectType(eEffect.SpeedOfSound), null, target.HealthPercent) &&
            RootTakesNow(caster, target, spell),
    };

    // #167 C8, st-deathsD's fix round: RootTakes on the target's movement debuffs, each by its Effectiveness, against the
    // root at its caster's (a debuff's: 1 + DebuffEffectiveness%, SpellHandler.cs:2171; a root never crits,
    // SpeedDecreaseSpellHandler.cs:66-68), read only when one is on it. One of the root's own spell is renewed, not
    // weighed (EffectListComponent.cs:632-689).
    private static bool RootTakesNow(GamePlayer caster, GameLiving target, Spell root)
    {
        List<ECSGameSpellEffect> slows = target.effectListComponent.GetSpellEffects(eEffect.MovementSpeedDebuff);
        if (slows.Count == 0) return true;
        double effectiveness = 1 + caster.GetModified(eProperty.DebuffEffectiveness) * 0.01;
        return RootTakes(slows.Where(effect => effect.SpellHandler?.Spell is Spell spell && spell.ID != root.ID)
            .Select(effect => (effect.SpellHandler.Spell.Value * effect.Effectiveness,
                effect.SpellHandler.Spell.Damage * effect.Effectiveness, effect.IsDisabled)),
            root.Value * effectiveness, root.Damage * effectiveness);
    }

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

    /// <summary>Whether the server lets a root onto a target now (#167 C8, st-deathsD's fix round), from the movement
    /// debuffs on it (movementDebuffs: each one's Value and Damage by its Effectiveness, and whether it is disabled). A
    /// snare and a root always conflict (SpeedDecreaseSpellHandler.HasConflictingEffectWith,
    /// SpeedDecreaseSpellHandler.cs:44-48; the damage-and-snare nuke's handler, DamageSpeedDecreaseSpellHandler, doesn't
    /// override it), and a new effect takes the place of one it conflicts with only when it is better
    /// (ECSGameEffect.IsBetterThan, ECSGameEffect.cs:179-183: Value and Damage by Effectiveness, both at least as high),
    /// else it fails (EffectListComponent.cs:745-779). So: none enabled on the target, or the root (rootValue,
    /// rootDamage: its products) better than each. Every root's Damage is 0 and the snare nukes' 143-148: st-deathsD's
    /// snare group landed 10 controls and failed 56. D5 (AnyRoot) read a root under the snare that the server never
    /// lets on.</summary>
    public static bool RootTakes(IEnumerable<(double Value, double Damage, bool Disabled)> movementDebuffs,
        double rootValue, double rootDamage)
    {
        foreach ((double value, double damage, bool disabled) in movementDebuffs)
            if (!disabled && (rootValue < value || rootDamage < damage)) return false;
        return true;
    }

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
        if (CastsAfterWalkOut(state, player) ||                                         // #167 D4, step 11b
            player.Level == state.NoPetSpellLevel ||
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

    // #167 D4, step 11b: after a release, while its immunity lasts (GamePlayer.cs:12284-12287), no self-buff and no pet:
    // the bot plans first (step 15) and walks out (StepsOutOfReach's WalkOut), and casts them where the walk ends. A cast
    // returns the think before the plan, and stands the bot (CastOnSelf). Its heal is moot then: the release restores its
    // health (GamePlayer.cs:1723).
    private static bool CastsAfterWalkOut(PlayerBotState state, GamePlayer player) =>
        state.AfterRelease && player.IsInvulnerableToAttack;

    // #167 F8: the class's own self-buffs (NextSelfBuff), one cast at a time, scanned every SelfBuffScanMilliseconds out of
    // a fight (MayRest's view), before a rest as a player buffs and then sits. A release ends them all: the new life
    // scans at once (Think). #167 C9: never mid-rest (BuffScanAllowed), a kill scans at once (SettleTarget) and each buff
    // cast the next scan, back to back, and only those it keeps up out of a fight (KeepsUpOutOfFight). #167 C13: the
    // chant it wants (WantedChant): its regeneration one from the kill to the rest's end, once recovered (sitting, the
    // scan allowed again) its combat one back.
    private static bool SelfBuff(PlayerBotState state, GamePlayer player, long now)
    {
        if (CastsAfterWalkOut(state, player) ||                                         // #167 D4, step 11b
            !SelfBuffScanDue(now, state.NextSelfBuffTick, player.IsCasting, state.Engaged && state.Target?.IsAlive == true,
                player.IsSitting, Recovered(player)) ||                                 // #167 C9
            Threat(state, player, now) != null)                                         // MayRest's view, and one coming
            return false;                                                               //   at it (#167 F7)
        state.NextSelfBuffTick = now + SelfBuffScanMilliseconds;
        byte health = player.HealthPercent, mana = player.ManaPercent, endurance = player.EndurancePercent,
            powerStop = RestPowerStop(RoleOf(player.CharacterClass.ID));
        List<(Spell Spell, SpellLine Line)> usable = UsableSpells(player).ToList();
        bool resting = Resting(player.IsSitting, WantsRest(player, state.RecoverBeforePull));   // #167 C13: its chant's
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
    // picks one, cast on itself before the fight's first swing or spell. #167 D12: only in a hard fight (FightBuffDue: a
    // YELLOW or harder then, or later the think a second live monster is on the bot). True when it cast.
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
    // in 1.65). Otherwise the nuke ready now (#167 F9), paid for (#167 E C2b: the killing quickcast's any-power nuke is
    // KillingNukeChoice's).
    private static (Spell, SpellLine) CombatSpell(PlayerBotState state, GamePlayer player, GameLiving target, long now)
    {
        (Spell pet, SpellLine petLine) = CombatPet(state, player, target, now, player.TargetInView);
        return pet != null ? (pet, petLine) : Nuke(state, player, target, killingQuickcast: false);   // #167 F9: per cast
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
    // walk of the usable spells per level and after a training (ObserveXp clears NukesLevel). #167 E C1: a caster's damage
    // spells by its own lines (DamageSpells' perLine) and each one's handler (NukeValues reads the server's own numbers
    // through it; the constructor only stores the caster, spell and line), and whether its controls hold a stun or a root.
    private static void KnowSpells(PlayerBotState state, GamePlayer player)
    {
        if (state.NukesLevel == player.Level) return;
        List<(Spell Spell, SpellLine Line)> usable = UsableSpells(player).ToList();
        bool caster = RoleOf(player.CharacterClass.ID) == PlayerBotRole.Caster;
        state.Nukes = DamageSpells(usable, player.Level, SkillBase.GetSpellByID, perLine: caster);   // #167 F10b: payloads
        state.CombatPets = usable.FindAll(pair => pair.Spell.SpellType is eSpellType.SummonTheurgistPet);
        state.Controls = usable.FindAll(pair => IsControlSpell(pair.Spell) && pair.Spell.Level <= player.Level);   // #167 F11
        state.NukeHandlers = new();
        if (caster)
            foreach ((Spell spell, SpellLine line) in state.Nukes)
                if (ScriptMgr.CreateSpellHandler(player, spell, line) is DOL.GS.Spells.SpellHandler handler)
                    state.NukeHandlers[spell] = handler;
        state.HasStun = state.Controls.Exists(pair => pair.Spell.SpellType is eSpellType.Stun);
        state.HasRoot = state.Controls.Exists(pair => pair.Spell.SpellType is eSpellType.SpeedDecrease);
        state.NukesLevel = player.Level;
    }

    // A spell on the bot itself, stopped and standing (no cast sitting, SpellHandler.cs:556-562). Targeted as a player
    // does it, F1 then the spell, unless it is a Self spell, which ignores the target (SpellHandler.cs:411-416): a
    // self-target is in view (GamePlayer.cs:127-139), and a Group heal takes the caster with no group
    // (SpellHandler.cs:1807-1811). The request is read in the Casting phase later in this tick (CastingComponent.cs:116-144,
    // SpellHandler.cs:403-406), so the target stays until the next think's Engage. #167 D13 R1a: a cast as it sits
    // recovered ends the rest owed (RestOwedAfterCast), so the cast's cost doesn't sit it down again.
    private static bool CastOnSelf(PlayerBotState state, GamePlayer player, long now, Spell spell, SpellLine line, string why)
    {
        state.Mover.Stop(now);
        bool recovered = Recovered(player);
        state.RecoverBeforePull = RestOwedAfterCast(state.RecoverBeforePull, player.IsSitting, recovered);   // #167 D13 R1a
        if (player.IsSitting)
        {
            EndRest(state, player, now, RestEndWhy(why, recovered));                   // PLAYER_BOT_REST why= (#167)
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
            {                                                                              //   and hp= and mp=; #167 D10:
                (state.SitSince, state.SitHealth, state.SitMana, state.SitEndurance) =     //   blocked= and inReach=, by the
                    (now, player.HealthPercent, PowerPercent(player.MaxMana, player.ManaPercent),   //   reaches its rest
                        player.EndurancePercent);                                                     //   (#167 E-R end=)
                (state.SitBlocked, state.SitInReach) =                                     //   check read (RestReach)
                    (state.RestBlocked, InAddReach(new Vector3(player.X, player.Y, player.Z), state.RestReach));
            }
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

    /// <summary>Whether the rest after a kill is still owed after a cast on itself (#167 D13 R1a, CastOnSelf): not when the
    /// bot casts as it sits recovered (the rest is done; Think clears RecoverBeforePull only after step 11b's casts, and a
    /// Skald's chant swap, 5 endurance, SpellHandler.cs:1244-1250, took it under the 60% stop: 38 of st-deathsC's 84
    /// re-sits straight after REST why=recovered). A standing cast keeps it owed (D3: a caster's login buffs, cast
    /// standing, keep its login rest).</summary>
    public static bool RestOwedAfterCast(bool recoverBeforePull, bool sitting, bool recovered) =>
        recoverBeforePull && !(sitting && recovered);

    /// <summary>Whether the kill stops the bot's cast (#167 D13 R1b, SettleTarget): one under way (casting) at the target
    /// that just died (atDeadTarget). The kill's buff scan needs no cast under way (SelfBuffScanDue), so it waited; the
    /// rest then stopped the cast and sat, and the buffs waited for the rest's end (BuffScanAllowed): 46 of st-deathsC's
    /// 84 re-sits.</summary>
    public static bool StopsCastAtKill(bool casting, bool atDeadTarget) => casting && atDeadTarget;

    // #167: PLAYER_BOT_REST once per rest, when it ends.
    private static void EndRest(PlayerBotState state, GamePlayer player, long now, string why)
    {
        if (state.SitSince == 0) return;
        if (Log.IsInfoEnabled)
            Log.Info(PlayerBotDriverReport.FormatRest(player.Name, player.Level, (now - state.SitSince) / 1000, why,
                state.SitHealth, state.SitMana, state.RestRespawns, state.SitBlocked, state.SitInReach, state.SitEndurance));
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
