using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

/// <summary>One report window: the counters since the last line, and a snapshot of the bots now.</summary>
public readonly record struct PlayerBotDriverWindow(
    int Seconds, int Bots, long Ticks, double TickMs, double MoverMs,
    int Waiting, int Dead, int Resting, int Hunting, int Fighting, int Casting, int Roaming, int Task,
    long Kills, long Deaths, long LevelUps, int MaxLevel, long Experience, long Blocked, long Stuck, long LinkDeathsCancelled,
    long Errors, double NearPlayerBots, double NearGamebots, int GamebotNearbyHuman, int GamebotStandard, int GamebotEfficient);

/// <summary>One settled fight (levelmatch/DEATHS.md 2): the bot's health and power at its first think in reach and now,
/// the most attackers on a think, the swings seen and those the view gate refused, the casts, and this life's kills and
/// sits before it; then whether it had a living pet at that first think and its heals on itself since (DEATHS.md A5,
/// A6), and whether a cast in it went with Quickcast on; then (#167) its self-buffs up at that first think (F8), a
/// Theurgist's pets cast at the target (F1), and the thinks a cast due waited for its own swing's interrupt (F6); a
/// caster's control casts and steps back (F11), the holds on its target its own swing ended (C3, BrokeHold), and its
/// control casts at an add (C4; in Controls too); then (#167, the next leg's) the whole seconds into the fight a second
/// monster first hit the bot or came at it and its health then (FirstAdd), the fight dropped by a break-off (C2), the
/// distance at that first think, and the pick's crowding (CrowdingAlong, C5): -1 for none, a defence or Incoming's pick;
/// then (#167, st-deathsD's) the whole seconds its target was held (Hold), its control casts that held or not
/// (ControlLanded), its and its target's health at the first hit on it (-1: none), the health it lost a second over the
/// last 3 s (HealthRate; a gain is negative), its health when the projected break-off would have fired (ProjectedBreak,
/// logged only; -1: never), and the walk on the think before the fight's first (F12d's field).</summary>
public readonly record struct PlayerBotFight(
    string Bot, int Level, string Target, int TargetLevel, ConColor Con, bool FromDefence, bool Killed,
    byte StartHp, byte EndHp, byte StartMana, byte EndMana, long Seconds, int Adds, int Swings, int NotVisible, int Casts,
    int LifeKills, int Rests, bool Pet = false, int Heals = 0, bool Quickcast = false, int Buffs = 0, int PetCasts = 0,
    int Waits = 0, int Controls = 0, int Kites = 0, int CtlBroken = 0, int AddControls = 0, int AddAt = -1, int AddHp = -1,
    bool BreakOff = false, int EngageDistance = -1, int Crowd = -1, long HeldSeconds = 0, int CtlLanded = 0, int CtlFailed = 0,
    int HpAtReach = -1, int TargetHpAtReach = -1, double Rate = 0, int WouldBreak = -1, PlayerBotWalk Walk = PlayerBotWalk.None);

/// <summary>What a PLAYER_BOT_DEATH line adds after its first fields (levelmatch/DEATHS.md 2); -1 for none: the fight's
/// fields (startHp, fightSec, adds, swings, notVisible) out of a fight, targetHp with no target, sinceKillSec with no kill
/// this life, fromBind with the bind in another region. Avoided: the names the bot avoided as it died, before this
/// death's (DEATHS.md A3). Again (#167 F4): no kill since its last release (DiedAgain); SinceRelease: the seconds since
/// it, -1 with none this login; Ill: the resurrection illness on at the fatal fight's start (false out of a fight). Then
/// (#167 C3, C4) the fatal fight's caster fields as PLAYER_BOT_FIGHT gives them, Quickcast 1 or 0: -1 out of a fight; and
/// FIGHT's AddAt, AddHp and EngageDistance (-1 out of a fight too), and BreakOff: the bot died on a break-off's run (C2;
/// out of a fight then, as the break-off dropped it). Then (#167, st-deathsD's) FIGHT's HeldSeconds, CtlLanded,
/// CtlFailed, HpAtReach, TargetHpAtReach and WouldBreak (-1 out of a fight), Rate at the death, Walk: the fight's, or out
/// of a fight the last think's alive, and Endurance.</summary>
public readonly record struct PlayerBotDeathDetail(
    PlayerBotActivity Activity, int StartHp, long FightSeconds, int TargetHp, bool FromDefence, int Adds, int LifeKills,
    int Rests, long SinceKillSeconds, byte Mana, int Swings, int NotVisible, string Killer, int KillerLevel,
    bool KillerIsTarget, int FromBind, int Avoided = 0, bool Again = false, long SinceRelease = -1, bool Ill = false,
    int Controls = -1, int Kites = -1, int Casts = -1, int Quickcast = -1, int Waits = -1, int CtlBroken = -1,
    int AddControls = -1, int AddAt = -1, int AddHp = -1, bool BreakOff = false, int EngageDistance = -1, long HeldSeconds = -1,
    int CtlLanded = -1, int CtlFailed = -1, int HpAtReach = -1, int TargetHpAtReach = -1, double Rate = 0, int WouldBreak = -1,
    PlayerBotWalk Walk = PlayerBotWalk.None, int Endurance = -1);

// HearthDAoC player bots (#137): one PLAYER_BOT_DRIVER line a minute, then the lever's PLAYER_BOT_WAKE line, from a thread-pool
// timer off the game loop, and every TallyEveryWindows windows a PLAYER_BOT_TALLY line per bot (the level-matched run).
// The driver times itself (think, mover) because its TimerService row also holds the bot's other timers. The driver's
// own lines are formatted here too: PLAYER_BOT_FIGHT per settled fight and the deaths' fields (levelmatch/DEATHS.md 2).
public static class PlayerBotDriverReport
{
    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotDriverReport));
    public const int WindowSeconds = 60;
    public const int TallyEveryWindows = 5;   // PLAYER_BOT_TALLY at windows 5, 10, ... 60 (levelmatch/DESIGN.md 2.7)

    private static long _ticks, _tickStopwatch, _moverStopwatch, _kills, _deaths, _levelUps, _experience, _blocked, _stuck,
        _ldCancelled, _errors;
    private static int _maxLevel, _windows;
    private static Timer _timer;
    private static readonly ConditionalWeakTable<GameLiving, StrongBox<long>> _countedLives = new();

    public static void AddTick(long stopwatchTicks) { Interlocked.Increment(ref _ticks); Interlocked.Add(ref _tickStopwatch, stopwatchTicks); }
    public static void AddMover(long stopwatchTicks) => Interlocked.Add(ref _moverStopwatch, stopwatchTicks);
    // One kill per monster life (its SpawnTick, set at each AddToWorld, GameObject.cs:614), however many player bots
    // fought it: each bot whose engaged target dies reports it, and a start row's bots share their monsters (runs 2-3).
    // False when another bot already counted this life.
    public static bool Kill(GameLiving monster)
    {
        StrongBox<long> counted = _countedLives.GetValue(monster, _ => new StrongBox<long>(long.MinValue));
        long life = monster.SpawnTick;
        if (Interlocked.Exchange(ref counted.Value, life) == life) return false;
        Interlocked.Increment(ref _kills);
        return true;
    }
    public static void Death() => Interlocked.Increment(ref _deaths);
    public static void Experience(long amount) => Interlocked.Add(ref _experience, amount);
    public static void Blocked() => Interlocked.Increment(ref _blocked);
    public static void StuckRecovered() => Interlocked.Increment(ref _stuck);
    public static void LinkDeathCancelled() => Interlocked.Increment(ref _ldCancelled);
    public static void Error() => Interlocked.Increment(ref _errors);

    public static void LevelUp(int levels, int newLevel)
    {
        Interlocked.Add(ref _levelUps, levels);
        int seen;
        while (newLevel > (seen = Volatile.Read(ref _maxLevel)) && Interlocked.CompareExchange(ref _maxLevel, newLevel, seen) != seen) { }
    }

    public static void EnsureStarted()
    {
        if (Volatile.Read(ref _timer) != null) return;
        Timer timer = new(_ => Write(), null, Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _timer, timer, null) != null) { timer.Dispose(); return; }
        timer.Change(WindowSeconds * 1000, WindowSeconds * 1000);
    }

    public static void Stop()
    {
        Interlocked.Exchange(ref _timer, null)?.Dispose();
        Interlocked.Exchange(ref _windows, 0);
    }

    private static void Write()
    {
        try
        {
            int bots = 0, waiting = 0, dead = 0, resting = 0, hunting = 0, fighting = 0, casting = 0, roaming = 0, task = 0, maxLevel = 0;
            long nearPlayerBots = 0, nearGamebots = 0;
            foreach (PlayerBotState s in PlayerBotDriver.States)
            {
                bots++;
                switch (s.Activity)
                {
                    case PlayerBotActivity.Waiting: waiting++; break;
                    case PlayerBotActivity.Dead: dead++; break;
                    case PlayerBotActivity.Resting: resting++; break;
                    case PlayerBotActivity.Hunting: hunting++; break;
                    case PlayerBotActivity.Fighting: fighting++; break;
                    case PlayerBotActivity.Casting: casting++; break;
                    case PlayerBotActivity.Roaming: roaming++; break;
                    case PlayerBotActivity.Task: task++; break;
                }
                nearPlayerBots += s.NearPlayerBots;
                nearGamebots += s.NearGamebots;
                maxLevel = Math.Max(maxLevel, s.LastLevel);
            }
            int human = 0, standard = 0, efficient = 0;
            foreach (GameBot bot in AutonomousBotRegistry.Snapshot())           // AutonomousBotRegistry.cs:75-85
            {
                switch (bot.AutonomousFidelity)                                  // GameBot.cs:82, set each think (BotBrain.cs:1330-1332)
                {
                    case eAutonomousFidelity.NearbyHuman: human++; break;
                    case eAutonomousFidelity.Standard: standard++; break;
                    default: efficient++; break;
                }
            }
            double toMs = 1000.0 / Stopwatch.Frequency;
            PlayerBotDriverWindow w = new(WindowSeconds, bots, Interlocked.Exchange(ref _ticks, 0),
                Interlocked.Exchange(ref _tickStopwatch, 0) * toMs, Interlocked.Exchange(ref _moverStopwatch, 0) * toMs,
                waiting, dead, resting, hunting, fighting, casting, roaming, task,
                Interlocked.Exchange(ref _kills, 0), Interlocked.Exchange(ref _deaths, 0), Interlocked.Exchange(ref _levelUps, 0),
                Math.Max(maxLevel, Interlocked.Exchange(ref _maxLevel, 0)), Interlocked.Exchange(ref _experience, 0),
                Interlocked.Exchange(ref _blocked, 0), Interlocked.Exchange(ref _stuck, 0), Interlocked.Exchange(ref _ldCancelled, 0),
                Interlocked.Exchange(ref _errors, 0),
                bots == 0 ? 0 : (double)nearPlayerBots / bots, bots == 0 ? 0 : (double)nearGamebots / bots, human, standard, efficient);
            if (Log.IsInfoEnabled) Log.Info(Format(w));
            PlayerBotWakeWindow wake = PlayerBotWake.TakeWindow(WindowSeconds);   // its own line: the DRIVER format is pinned
            if (Log.IsInfoEnabled) Log.Info(PlayerBotWake.Format(wake));
            int window = Interlocked.Increment(ref _windows);
            if (window % TallyEveryWindows == 0 && Log.IsInfoEnabled)
                foreach (PlayerBotState s in PlayerBotDriver.States)                   // fields written on the loop: whole-value reads
                    Log.Info(FormatTally(window, s.Player.Name, s.LastLevel, Volatile.Read(ref s.Kills), Volatile.Read(ref s.Deaths),
                        Interlocked.Read(ref s.Gained), s.NetExperience, Volatile.Read(ref s.CastFailures),
                        Volatile.Read(ref s.Retrains), s.Activity));
        }
        catch (Exception e)
        {
            if (Log.IsWarnEnabled) Log.Warn($"PLAYER_BOT_DRIVER report failed: {e.Message}");
        }
    }

    /// <summary>One bot's own counts since its login. Window N's lines follow the N-th DRIVER line: the counts at the end
    /// of window N (the start of the [N, ...) range summarize.py and legcheck.py use). xp is the gains the DRIVER line sums;
    /// netXp is its experience now minus at login (REVIEW-FIXES 9: a death's loss shows there).</summary>
    public static string FormatTally(int window, string bot, int level, int kills, int deaths, long xp, long netXp,
        int castFailed, int retrained, PlayerBotActivity activity) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_TALLY window={window} bot={bot} level={level} kills={kills} deaths={deaths} xp={xp} netXp={netXp} " +
        $"castFailed={castFailed} retrained={retrained} activity={activity}");

    /// <summary>PLAYER_BOT_FIGHT, from the driver when a fight it was in settles: the monster died (kill) or is no longer
    /// fair game (dropped). lifeKills and rests are this life's before the fight; tools/deathdiag.py reads the line.</summary>
    public static string FormatFight(PlayerBotFight f) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_FIGHT bot={f.Bot} level={f.Level} target=\"{f.Target}\" targetLevel={f.TargetLevel} con={f.Con} " +
        $"from={(f.FromDefence ? "defence" : "pull")} result={(f.Killed ? "kill" : "dropped")} startHp={f.StartHp} endHp={f.EndHp} " +
        $"startMana={f.StartMana} endMana={f.EndMana} sec={f.Seconds} adds={f.Adds} swings={f.Swings} notVisible={f.NotVisible} " +
        $"casts={f.Casts} lifeKills={f.LifeKills} rests={f.Rests} pet={(f.Pet ? "alive" : "none")} heals={f.Heals} " +
        $"qc={(f.Quickcast ? 1 : 0)} buffs={f.Buffs} petCasts={f.PetCasts} waits={f.Waits} ctl={f.Controls} kites={f.Kites} " +
        $"ctlBroken={f.CtlBroken} addCtl={f.AddControls} addAt={f.AddAt} addHp={f.AddHp} breakOff={(f.BreakOff ? 1 : 0)} " +
        $"engageDist={f.EngageDistance} crowd={f.Crowd} heldSec={f.HeldSeconds} ctlLanded={f.CtlLanded} ctlFailed={f.CtlFailed} " +
        $"hpAtReach={f.HpAtReach} tHpAtReach={f.TargetHpAtReach} rate={FormatRate(f.Rate)} wouldBreak={f.WouldBreak} " +
        $"walk={FormatWalk(f.Walk)}");

    /// <summary>#167 rate=: one decimal, a loss under 0.05 a second (either way) as 0.0, not -0.0.</summary>
    private static string FormatRate(double rate) =>
        (Math.Round(rate, 1) is var rounded && rounded != 0 ? rounded : 0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>#167 walk=: none, hunt, anchor, roam, clear, rest, kite or breakoff.</summary>
    private static string FormatWalk(PlayerBotWalk walk) => walk.ToString().ToLowerInvariant();

    /// <summary>PLAYER_BOT_REST (#167), when a rest ends: the seconds the bot sat and why it stood (PlayerBotDriver.EndRest);
    /// then its health and power when it sat, and the killed monsters' spawn spots its rest spot was read against (C7).</summary>
    public static string FormatRest(string bot, int level, long seconds, string why, int hp, int mp, int respawn) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_REST bot={bot} level={level} sec={seconds} why={why} hp={hp} mp={mp} respawn={respawn}");

    /// <summary>PLAYER_BOT_BACK (#167 C1), at the first fight after a release (PlayerBotDriver.ReportBack): the seconds
    /// since the release, those stepping out of reach, held (the back-off, the illness, the replan's hold) and walking
    /// (Roam), and the replan (PlayerBotDriver.BackReplan).</summary>
    public static string FormatBack(string bot, int level, long seconds, long stepOut, long hold, long walk, string replan) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_BACK bot={bot} level={level} sec={seconds} stepOut={stepOut} hold={hold} walk={walk} replan={replan}");

    /// <summary>PLAYER_BOT_BREAKOFF (#167 C2), when a break-off ends (PlayerBotDriver.EndBreakOff): the bot's health,
    /// endurance and attackers when it broke off, the seconds it ran, and how it ended (shook: none still after it;
    /// fought: still chased, or nowhere to run, it turned on them; died); then (#167, st-deathsD's) the health it lost a
    /// second over the 3 s before it broke off (PlayerBotDriver.HealthRate).</summary>
    public static string FormatBreakOff(string bot, int level, int hp, int end, int attackers, long seconds, string outcome,
        double rate) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_BREAKOFF bot={bot} level={level} hp={hp} end={end} attackers={attackers} sec={seconds} outcome={outcome} " +
            $"rate={FormatRate(rate)}");

    /// <summary>The fields PLAYER_BOT_DEATH appends after task= (the ones before stay as they were: legcheck.py reads them).</summary>
    public static string FormatDeathDetail(PlayerBotDeathDetail d) => string.Create(CultureInfo.InvariantCulture,
        $"activity={d.Activity} startHp={d.StartHp} fightSec={d.FightSeconds} targetHp={d.TargetHp} " +
        $"from={(d.FromDefence ? "defence" : "pull")} adds={d.Adds} lifeKills={d.LifeKills} rests={d.Rests} " +
        $"sinceKillSec={d.SinceKillSeconds} mana={d.Mana} swings={d.Swings} notVisible={d.NotVisible} killer=\"{d.Killer}\" " +
        $"killerLevel={d.KillerLevel} killerIsTarget={d.KillerIsTarget} fromBind={d.FromBind} avoided={d.Avoided} " +
        $"again={d.Again} sinceRelease={d.SinceRelease} ill={d.Ill} ctl={d.Controls} kites={d.Kites} casts={d.Casts} " +
        $"qc={d.Quickcast} waits={d.Waits} ctlBroken={d.CtlBroken} addCtl={d.AddControls} addAt={d.AddAt} addHp={d.AddHp} " +
        $"breakOff={(d.BreakOff ? 1 : 0)} engageDist={d.EngageDistance} heldSec={d.HeldSeconds} ctlLanded={d.CtlLanded} " +
        $"ctlFailed={d.CtlFailed} hpAtReach={d.HpAtReach} tHpAtReach={d.TargetHpAtReach} rate={FormatRate(d.Rate)} " +
        $"wouldBreak={d.WouldBreak} walk={FormatWalk(d.Walk)} end={d.Endurance}");

    public static string Format(PlayerBotDriverWindow w)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        double botSeconds = (double)w.Bots * w.Seconds;
        string PerBot(double value, string format) => w.Bots == 0 ? "-" : (value / botSeconds).ToString(format, c);
        string Mean(double value) => w.Bots == 0 ? "-" : value.ToString("0.0", c);
        return string.Create(c,
            $"PLAYER_BOT_DRIVER win={w.Seconds} bots={w.Bots} ticksPerBotSec={PerBot(w.Ticks, "0.0")} " +
            $"thinkMsPerBotSec={PerBot(w.TickMs - w.MoverMs, "0.000")} moverMsPerBotSec={PerBot(w.MoverMs, "0.000")} " +
            $"waiting={w.Waiting} dead={w.Dead} resting={w.Resting} hunting={w.Hunting} fighting={w.Fighting} casting={w.Casting} " +
            $"roaming={w.Roaming} task={w.Task} kills={w.Kills} deaths={w.Deaths} levelUps={w.LevelUps} maxLevel={w.MaxLevel} " +
            $"xp={w.Experience} blocked={w.Blocked} stuck={w.Stuck} ldCancelled={w.LinkDeathsCancelled} errors={w.Errors} " +
            $"nearPlayerBots={Mean(w.NearPlayerBots)} nearGamebots={Mean(w.NearGamebots)} " +
            $"gamebotFidelity={w.GamebotNearbyHuman}/{w.GamebotStandard}/{w.GamebotEfficient}");
    }
}
