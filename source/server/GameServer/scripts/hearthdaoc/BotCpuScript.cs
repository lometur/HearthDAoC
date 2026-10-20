using DOL.GS.ServerProperties;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's switch and server side (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md,
// section 4). The bot_cpu_meter server property, off in code, is set from HEARTHDAOC_BOT_CPU at every start (4.2) and
// read once, at start: on, the meter starts with the server (4.1). A GM's /botcpu turns the meter on or off until the
// next start and never writes the property (4.3). BotCpuMeter is the hot path; BotCpuReport makes the numbers and the
// text.
public static class BotCpuScript
{
    [ServerProperty("server", "bot_cpu_meter",
        "Measure the game loop's CPU time per kind of bot and player and write BOT_CPU lines to the server log every minute; a GM's /botcpu turns it on or off until the next start",
        false)]
    public static bool BOT_CPU_METER;
}
