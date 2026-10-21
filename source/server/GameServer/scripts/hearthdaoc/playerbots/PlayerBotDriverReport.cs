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

// HearthDAoC #137 spike: one PLAYER_BOT_DRIVER line a minute, from a thread-pool timer off the game loop. The driver
// times itself (think, mover) because its TimerService row also holds the bot's other timers.
public static class PlayerBotDriverReport
{
    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotDriverReport));
    public const int WindowSeconds = 60;

    private static long _ticks, _tickStopwatch, _moverStopwatch, _kills, _deaths, _levelUps, _experience, _blocked, _stuck,
        _ldCancelled, _errors;
    private static int _maxLevel;
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

    public static void Stop() => Interlocked.Exchange(ref _timer, null)?.Dispose();

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
        }
        catch (Exception e)
        {
            if (Log.IsWarnEnabled) Log.Warn($"PLAYER_BOT_DRIVER report failed: {e.Message}");
        }
    }

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
