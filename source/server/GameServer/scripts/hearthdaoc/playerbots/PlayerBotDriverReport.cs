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
/// Theurgist's pets cast at the target (F1), and the thinks a cast due waited for its own swing's interrupt (F6).</summary>
public readonly record struct PlayerBotFight(
    string Bot, int Level, string Target, int TargetLevel, ConColor Con, bool FromDefence, bool Killed,
    byte StartHp, byte EndHp, byte StartMana, byte EndMana, long Seconds, int Adds, int Swings, int NotVisible, int Casts,
    int LifeKills, int Rests, bool Pet = false, int Heals = 0, bool Quickcast = false, int Buffs = 0, int PetCasts = 0,
    int Waits = 0);

/// <summary>What a PLAYER_BOT_DEATH line adds after its first fields (levelmatch/DEATHS.md 2); -1 for none: the fight's
/// fields (startHp, fightSec, adds, swings, notVisible) out of a fight, targetHp with no target, sinceKillSec with no kill
/// this life, fromBind with the bind in another region. Avoided: the names the bot avoided as it died, before this
/// death's (DEATHS.md A3).</summary>
public readonly record struct PlayerBotDeathDetail(
    PlayerBotActivity Activity, int StartHp, long FightSeconds, int TargetHp, bool FromDefence, int Adds, int LifeKills,
    int Rests, long SinceKillSeconds, byte Mana, int Swings, int NotVisible, string Killer, int KillerLevel,
    bool KillerIsTarget, int FromBind, int Avoided = 0);

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
        $"qc={(f.Quickcast ? 1 : 0)} buffs={f.Buffs} petCasts={f.PetCasts} waits={f.Waits}");

    /// <summary>PLAYER_BOT_REST (#167), when a rest ends: the seconds the bot sat and why it stood (PlayerBotDriver.EndRest).</summary>
    public static string FormatRest(string bot, int level, long seconds, string why) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_REST bot={bot} level={level} sec={seconds} why={why}");

    /// <summary>The fields PLAYER_BOT_DEATH appends after task= (the ones before stay as they were: legcheck.py reads them).</summary>
    public static string FormatDeathDetail(PlayerBotDeathDetail d) => string.Create(CultureInfo.InvariantCulture,
        $"activity={d.Activity} startHp={d.StartHp} fightSec={d.FightSeconds} targetHp={d.TargetHp} " +
        $"from={(d.FromDefence ? "defence" : "pull")} adds={d.Adds} lifeKills={d.LifeKills} rests={d.Rests} " +
        $"sinceKillSec={d.SinceKillSeconds} mana={d.Mana} swings={d.Swings} notVisible={d.NotVisible} killer=\"{d.Killer}\" " +
        $"killerLevel={d.KillerLevel} killerIsTarget={d.KillerIsTarget} fromBind={d.FromBind} avoided={d.Avoided}");

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
