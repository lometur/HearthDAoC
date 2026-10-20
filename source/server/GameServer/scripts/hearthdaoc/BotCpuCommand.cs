using System;
using System.Collections.Generic;
using System.Globalization;
using DOL.GS.Commands;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: /botcpu, the GM's command for the bot CPU meter (#137; spec
// docs/fork/specs/2026-10-14-bot-cpu-meter-design.md, 2.4, 3.7, 4.3). It runs on its game-loop thread, so it never
// builds a report and never touches a file, /proc or the log: it reads the last result the report published
// (BotCpuScript.Result) and, for a change, calls BotCpuScript, which turns the meter off at once for off and queues a
// job for the report side. Every reply is made in BotCpuReport. Its words and kinds are read in any case, as
// HEARTHDAOC_BOT_CPU is. For a GM, /bot runs this command: no other command name starts with &bot.
[CmdAttribute(
    "&botcpu",
    ePrivLevel.GM,
    "HearthDAoC: measure the game loop's CPU per kind of bot (BOT_CPU lines in the server log)",
    "/botcpu - status, the last minute and the totals since on",
    "/botcpu on [minutes] - start, for 1-1440 minutes, or until off or the next start",
    "/botcpu off - stop and write the last partial minute",
    "/botcpu top [gamebot|companion|playerbot|piloted] - the 5 costliest bots of the last minute (gamebot if none given)",
    "/botcpu reset - start the totals since on again")]
public sealed class BotCpuCommandHandler : AbstractCommandHandler, ICommandHandler
{
    /// <summary>The most minutes <c>/botcpu on</c> takes (2.4): a day.</summary>
    public const int MaxMinutes = 1440;

    public void OnCommand(GameClient client, string[] args)
    {
        IReadOnlyList<string> reply = Run(client.Player?.Name, args);
        if (reply == null)
        {
            DisplaySyntax(client);
            return;
        }

        // Sent as they are, not through DisplayMessage, which formats its text: a bot's activity may hold a brace.
        foreach (string line in reply)
            ChatUtil.SendSystemMessage(client, line);
    }

    /// <summary>
    /// <c>/botcpu</c> with <paramref name="args"/> (the first is the command's registered name, <c>&amp;botcpu</c>,
    /// which ScriptMgr puts there whatever was typed; it is not read): does what it asks and returns the lines of its
    /// reply (2.4), or null for arguments it refuses (another word, a kind that is not a bot kind, minutes out of
    /// 1-1440, a word too many), which get the syntax lines and change nothing. Reads only the result the report
    /// published; a change only turns the meter off or queues a job (3.7).
    /// </summary>
    /// <param name="gm">The GM's name, for the <c>BOT_CPU_STATE</c> line.</param>
    /// <param name="args">The command's words, as ScriptMgr splits them.</param>
    public static IReadOnlyList<string> Run(string gm, string[] args)
    {
        if (args.Length <= 1)
            return Status();

        if (args.Length > 3)
            return null;

        string value = args.Length == 3 ? args[2] : null;
        return args[1].ToLowerInvariant() switch
        {
            "on" when value == null => On(gm, null),
            "on" when Minutes(value) is int minutes => On(gm, minutes),
            "off" when value == null => Off(gm),
            "top" when value == null => Top(BotCpuKind.Gamebot),
            "top" when BotKind(value) is BotCpuKind kind => Top(kind),
            "reset" when value == null => Reset(),
            _ => null,
        };
    }

    // 1-1440, in digits only; null for anything else.
    private static int? Minutes(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) &&
        minutes is >= 1 and <= MaxMinutes
            ? minutes
            : null;

    // A bot kind by its name (2.5), in any case; null for another kind or word.
    private static BotCpuKind? BotKind(string text) => text.ToLowerInvariant() switch
    {
        "gamebot" => BotCpuKind.Gamebot,
        "companion" => BotCpuKind.Companion,
        "playerbot" => BotCpuKind.Playerbot,
        "piloted" => BotCpuKind.Piloted,
        _ => null,
    };

    // /botcpu: off, the off text; on, the status, the last minute and the totals since on or reset.
    private static IReadOnlyList<string> Status()
    {
        BotCpuResult result = BotCpuScript.Result;
        return result.On
            ? BotCpuReport.StatusText(result.OnSince, result.ByCommand, result.StopsAt,
                BotCpuScript.ElapsedSince(result.OnSinceTimestamp), result.Last, result.LastProcess, result.Since)
            : [BotCpuReport.OffStatusText];
    }

    // /botcpu on [minutes]: the job queued. The reply speaks of the state published before it was queued, as the job
    // can run on the thread pool before this returns.
    private static IReadOnlyList<string> On(string gm, int? minutes)
    {
        bool on = BotCpuScript.Result.On;
        DateTime? stopsAt = BotCpuScript.CommandOn(gm, minutes);
        if (on)
            return [BotCpuReport.AlreadyOnText(stopsAt)];

        return [minutes is int stopIn && stopsAt is DateTime stop
            ? BotCpuReport.OnText(stopIn, stop)
            : BotCpuReport.OnText()];
    }

    // /botcpu off: metering stops at once and the job is queued, whatever the published state, so an on queued just
    // before still ends off (3.7). The reply gives the open window's seconds, or that the meter is already off.
    private static IReadOnlyList<string> Off(string gm) =>
        [BotCpuScript.CommandOff(gm) is double seconds ? BotCpuReport.OffText(seconds) : BotCpuReport.AlreadyOffText];

    // /botcpu top [kind]: off, the off text; before the first window, when its lines come; then the kind's top 5.
    private static IReadOnlyList<string> Top(BotCpuKind kind)
    {
        BotCpuResult result = BotCpuScript.Result;
        if (!result.On)
            return [BotCpuReport.OffStatusText];

        return result.Last == null
            ? [BotCpuReport.NoWindowYetText(result.OnSince)]
            : BotCpuReport.TopText(result.Last, kind);
    }

    // /botcpu reset: on, the job queued; off, the off text and nothing queued.
    private static IReadOnlyList<string> Reset()
    {
        if (!BotCpuScript.Result.On)
            return [BotCpuReport.OffStatusText];

        BotCpuScript.CommandReset();
        return [BotCpuReport.ResetText];
    }
}
