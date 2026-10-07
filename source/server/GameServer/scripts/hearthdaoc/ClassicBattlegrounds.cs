using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the classic battlegrounds, Abermenai (15-19), Thidranki (20-24), Murdaigean (25-29) and
// Caledonia (30-35), each with a realm rank cap from its battleground row. This class holds every decision:
// where the frontier porter sends a character wearing the battlegrounds medallion, or why it won't and
// whether it says so now; when a character is over a battleground's limit and where it goes then; and a
// central keep's level after a capture. It reads nothing from the running server (GameServer, WorldMgr,
// ServerProperties, GamePlayer), so unit tests drive it directly; ClassicBattlegroundsScript feeds it the
// battleground rows and the character's state and carries out the outcome.

// One battleground's row. RealmPointCap is REALMPOINTS_FOR_LEVEL[MaxRealmLevel] (0 when MaxRealmLevel is 0);
// the script computes it, so this file never reads GamePlayer.
public sealed record BattlegroundBracket(ushort Region, string Name, byte MinLevel, byte MaxLevel,
    byte MaxRealmLevel, long RealmPointCap);

// Where the porter sends a realm in a battleground.
public sealed record BattlegroundLanding(string Name, ushort Region, int X, int Y, int Z, ushort Heading);

// Exactly one of the two is non-null, or both are null (a realm with no landing spot: nothing said).
public sealed record PorterDecision(BattlegroundLanding Destination, string Refusal);

public static class ClassicBattlegrounds
{
    public const string RefusedAtKey = "hdc_bg_porter_refused";   // player.TempProperties key
    public const long RefusalQuietMs = 30_000;                    // "within the last 30 seconds"
    public const int LoginCheckDelayMs = 1000;                    // GameEntered -> check
    public const int LoginCheckAttempts = 10;                     // checks while the client isn't Playing yet

    // 253, 252, 251, 250, in that order (also the order of log and report lists).
    public static readonly IReadOnlyList<ushort> Regions = new ReadOnlyCollection<ushort>(new ushort[] { 253, 252, 251, 250 });

    public static readonly IReadOnlyDictionary<ushort, string> Names = new ReadOnlyDictionary<ushort, string>(
        new Dictionary<ushort, string>
        {
            [253] = "Abermenai",
            [252] = "Thidranki",
            [251] = "Murdaigean",
            [250] = "Caledonia",
        });

    public static bool IsBattleground(int region)
    {
        return region >= 250 && region <= 253;
    }

    // Realm level is (rank - 1) * 10 + level: 2 -> "1L2", 9 -> "1L9", 10 -> "2L0".
    public static string RankLabel(int realmLevel)
    {
        return FormattableString.Invariant($"{realmLevel / 10 + 1}L{realmLevel % 10}");
    }

    // Today's landing spots beside each realm's portal keep, the same in every battleground
    // (OFTeleporters.cs). Any other realm has none.
    public static BattlegroundLanding Landing(int realm, BattlegroundBracket bracket)
    {
        return realm switch
        {
            1 => new BattlegroundLanding(bracket.Name, bracket.Region, 38113, 53507, 4160, 3268),
            2 => new BattlegroundLanding(bracket.Name, bracket.Region, 53568, 23643, 4530, 0),
            3 => new BattlegroundLanding(bracket.Name, bracket.Region, 17367, 18248, 4320, 0),
            _ => null,
        };
    }

    // The battleground whose MinLevel..MaxLevel holds the level, if the realm level is below its
    // MaxRealmLevel (0: no cap); otherwise the reason, with the levels, ranks and caps of the rows.
    public static PorterDecision Porter(int level, int realmLevel, long realmPoints, int realm,
        IReadOnlyList<BattlegroundBracket> brackets)
    {
        BattlegroundBracket bracket = brackets.FirstOrDefault(b => level >= b.MinLevel && level <= b.MaxLevel);

        if (bracket != null)
        {
            if (bracket.MaxRealmLevel == 0 || realmLevel < bracket.MaxRealmLevel)
                return new PorterDecision(Landing(realm, bracket), null);

            string rank = RankLabel(bracket.MaxRealmLevel - 1);
            return Refuse($"{bracket.Name} is for realm rank {rank} and below, under {bracket.RealmPointCap:N0} realm points. You have {realmPoints:N0}, so I cannot send you there.");
        }

        if (brackets.Count > 0)
        {
            int lowest = brackets.Min(b => b.MinLevel);
            int highest = brackets.Max(b => b.MaxLevel);

            if (level < lowest)
                return Refuse($"The battlegrounds are for levels {lowest} to {highest}. Come back when you reach level {lowest}.");

            if (level > highest)
                return Refuse($"You have outgrown the battlegrounds, which are for levels {lowest} to {highest}.");
        }

        return Refuse($"No battleground on this server takes level {level}.");
    }

    // Numbers as in English (350, 1,375, 7,125), whatever the server's culture.
    private static PorterDecision Refuse(FormattableString text)
    {
        return new PorterDecision(null, FormattableString.Invariant(text));
    }

    // lastSaid is the GameLoopTime of the last refusal said to this player, or null if none. The porter's
    // callback runs twice per ceremony (5 s and 15 s after the cast) and casts every 120 s, so this says a
    // refusal once per ceremony.
    public static bool ShouldSayRefusal(long now, long? lastSaid)
    {
        return lastSaid == null || now - lastSaid.Value >= RefusalQuietMs;
    }

    // A player's character (privilege level 1) in one of the four battlegrounds, above its MaxLevel or at or
    // above its MaxRealmLevel (0: no cap). Being below MinLevel is not over the limit. bracket is that
    // battleground's row when this returns true, and null otherwise.
    public static bool IsOverLimit(uint privLevel, int region, int level, int realmLevel,
        IReadOnlyList<BattlegroundBracket> brackets, out BattlegroundBracket bracket)
    {
        bracket = null;

        if (privLevel != 1 || !IsBattleground(region))
            return false;

        BattlegroundBracket row = brackets.FirstOrDefault(b => b.Region == region);

        if (row == null)
            return false;

        if (level <= row.MaxLevel && (row.MaxRealmLevel == 0 || realmLevel < row.MaxRealmLevel))
            return false;

        bracket = row;
        return true;
    }

    // true: the bind point. false: the realm's home portal keep (KeepManager.ExitBattleground).
    public static bool GoesToBind(bool bindRegionExists, bool bindHasZone, int bindRegion)
    {
        return bindRegionExists && bindHasZone && !IsBattleground(bindRegion);
    }

    // The login message for a character moved out of a battleground it has outgrown.
    public static string OutgrownMessage(BattlegroundBracket bracket, bool atBind)
    {
        string limits = FormattableString.Invariant($"levels {bracket.MinLevel} to {bracket.MaxLevel}");

        if (bracket.MaxRealmLevel > 0)
            limits += $", realm rank {RankLabel(bracket.MaxRealmLevel - 1)} and below";

        string place = atBind ? "your bind point" : "your realm's portal keep";
        return $"You have outgrown {bracket.Name} ({limits}), so you are back at {place}.";
    }

    // A capture resets a keep to starting_keep_level; a battleground's central keep goes back to level 1.
    public static bool ShouldResetKeepLevel(int region, bool isPortalKeep, int level)
    {
        return IsBattleground(region) && !isPortalKeep && level != 1;
    }

    // The load log line, over Regions in order.
    public static string Summary(IReadOnlyList<BattlegroundBracket> brackets)
    {
        IEnumerable<string> parts = Regions.Select(region =>
        {
            BattlegroundBracket bracket = brackets.FirstOrDefault(b => b.Region == region);

            if (bracket == null)
                return $"{Names[region]} off (no battleground row)";

            string levels = FormattableString.Invariant($"{bracket.Name} {bracket.MinLevel}-{bracket.MaxLevel}");
            return bracket.MaxRealmLevel == 0
                ? $"{levels} with no realm rank cap"
                : $"{levels} up to {RankLabel(bracket.MaxRealmLevel - 1)}";
        });

        return "Classic battlegrounds: " + string.Join(", ", parts);
    }
}
