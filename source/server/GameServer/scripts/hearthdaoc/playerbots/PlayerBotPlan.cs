using System;
using System.Collections.Generic;
using System.Globalization;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC proto (#137, branch proto/player-bots, throwaway): the HEARTHDAOC_PLAYER_BOTS switch, parsed; the fixed
// roster (one level-1 base-class template per realm, names, stats); the login pacing. Pure, so UT_PlayerBotPlan
// covers it all; PlayerBotSpawner reads the variable once at start.

/// <summary>One realm's character template. Each has a StartupLocation row (500/532/540 in the v0.37b world, moved
/// by no start_towns.json entry) and a StarterEquipment kit.</summary>
public sealed record PlayerBotTemplate(eRealm Realm, eRace Race, eCharacterClass BaseClass, char Letter);

/// <summary>One player bot: its account (hdbot-alb-01), realm and 1-based index in the realm.</summary>
public sealed record PlayerBotSlot(string AccountName, eRealm Realm, int Index)
{
    /// <summary>hdbot-alb-01 is the Saracen Fighter of the promotion and quest 20206 checks (run 1).</summary>
    public bool IsQuestFighter => Realm == eRealm.Albion && Index == 1;
    public PlayerBotTemplate Template => PlayerBotRoster.For(Realm);
    public string CharacterName => PlayerBotRoster.CharacterName(Template, Index);
}

public sealed record PlayerBotPlan(bool On, int Albion, int Midgard, int Hibernia)
{
    public const string EnvironmentVariable = "HEARTHDAOC_PLAYER_BOTS";
    public const string AccountPrefix = "hdbot-"; // '-' fails LoginRequestHandler.cs:254-255: no client can log into or auto-create it
    public const int MaxPerRealm = 150; // 99 until the #137 lever test: one bot at each of 390 spots (125/132/133)
    public static readonly PlayerBotPlan Off = new(false, 0, 0, 0);

    public int Total => Albion + Midgard + Hibernia;

    public static string RealmKey(eRealm realm) => realm switch
    {
        eRealm.Albion => "alb",
        eRealm.Midgard => "mid",
        eRealm.Hibernia => "hib",
        _ => throw new ArgumentOutOfRangeException(nameof(realm)),
    };

    public static string AccountName(eRealm realm, int index) =>
        AccountPrefix + RealmKey(realm) + "-" + index.ToString("D2", CultureInfo.InvariantCulture);

    public static PlayerBotPlan Parse(string text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return Off;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int total))
        {
            if (total > 3 * MaxPerRealm)
                throw Bad(text);
            int each = total / 3, rest = total % 3;
            return new PlayerBotPlan(true, each + (rest > 0 ? 1 : 0), each + (rest > 1 ? 1 : 0), each);
        }
        int? alb = null, mid = null, hib = null;
        foreach (string part in value.Split(','))
        {
            string[] pair = part.Split(':');
            if (pair.Length != 2 ||
                !int.TryParse(pair[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                count > MaxPerRealm)
                throw Bad(text);
            switch (pair[0].Trim().ToLowerInvariant())
            {
                case "alb" when alb == null: alb = count; break;
                case "mid" when mid == null: mid = count; break;
                case "hib" when hib == null: hib = count; break;
                default: throw Bad(text);
            }
        }
        return new PlayerBotPlan(true, alb ?? 0, mid ?? 0, hib ?? 0);
    }

    /// <summary>Login order, realms interleaved (alb-01, mid-01, hib-01, alb-02, ...), so a partial login stays
    /// balanced.</summary>
    public IReadOnlyList<PlayerBotSlot> Slots()
    {
        var slots = new List<PlayerBotSlot>(Total);
        for (int i = 1; i <= Math.Max(Albion, Math.Max(Midgard, Hibernia)); i++)
        {
            if (i <= Albion) slots.Add(new(AccountName(eRealm.Albion, i), eRealm.Albion, i));
            if (i <= Midgard) slots.Add(new(AccountName(eRealm.Midgard, i), eRealm.Midgard, i));
            if (i <= Hibernia) slots.Add(new(AccountName(eRealm.Hibernia, i), eRealm.Hibernia, i));
        }
        return slots;
    }

    public string Describe() =>
        On ? FormattableString.Invariant($"alb={Albion} mid={Midgard} hib={Hibernia}") : "off";

    private static FormatException Bad(string text) => new(
        $"{EnvironmentVariable} must be off, a number of bots from 0 to {3 * MaxPerRealm}, or realm counts like " +
        $"alb:10,mid:10,hib:10 (each 0 to {MaxPerRealm}), got '{text}'");
}

public static class PlayerBotRoster
{
    public const string CharacterPrefix = "Pbot";

    // Male melee base classes; the owner may want a mixed roster (owner question 2).
    public static readonly PlayerBotTemplate Alb = new(eRealm.Albion, eRace.Saracen, eCharacterClass.Fighter, 'a');
    public static readonly PlayerBotTemplate Mid = new(eRealm.Midgard, eRace.Norseman, eCharacterClass.Viking, 'm');
    public static readonly PlayerBotTemplate Hib = new(eRealm.Hibernia, eRace.Celt, eCharacterClass.Guardian, 'h');
    public static readonly IReadOnlyList<PlayerBotTemplate> All = [Alb, Mid, Hib];

    public static PlayerBotTemplate For(eRealm realm) => realm switch
    {
        eRealm.Albion => Alb,
        eRealm.Midgard => Mid,
        eRealm.Hibernia => Hib,
        _ => throw new ArgumentOutOfRangeException(nameof(realm)),
    };

    // "Pbot" + realm letter + n as 4 lower-case letters (Pbotaaaab, Pbotmaaab...): passes CharacterCreateRequestHandler.cs:43-44.
    public static string CharacterName(PlayerBotTemplate t, int n)
    {
        Span<char> letters = stackalloc char[4];
        for (int i = 3; i >= 0; i--) { letters[i] = (char)('a' + n % 26); n /= 26; }
        return CharacterPrefix + t.Letter + new string(letters);
    }

    // Race base (GlobalConstants.cs:910-921) + 10 STR, CON, DEX: 30 points, none above 10 costing extra = the budget
    // (gameutils/CharacterStatValidator.cs:45-93); at level 1 the class adds nothing.
    public static Dictionary<eStat, int> StartingStats(eRace race)
    {
        var stats = new Dictionary<eStat, int>(GlobalConstants.STARTING_STATS_DICT[race]);
        stats[eStat.STR] += 10; stats[eStat.CON] += 10; stats[eStat.DEX] += 10;
        return stats;
    }

    // CharacterCreateRequestHandler.cs:439 stores AccountSlot = slot + Realm*100; the bot uses slot 0.
    public static int AccountSlot(eRealm realm) => (int)realm * 100;
}

public static class PlayerBotPacing
{
    /// <summary>How many due slots to start this second: at most perSecond, and never more than maxLoading loading at
    /// once.</summary>
    public static int LoginsToStart(int due, int loading, int perSecond, int maxLoading) =>
        Math.Max(0, Math.Min(Math.Min(due, perSecond), maxLoading - loading));
}
