using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

public sealed record PlayerBotSpot(eRealm Realm, ushort Region, int X, int Y, int? Z);

// HearthDAoC #137 lever test: HEARTHDAOC_PLAYER_BOT_SPOTS names a file of "realm|region|x|y[|z]" lines (# comments).
// The realm is alb/mid/hib, or 1/2/3 as the owner's richer GameBot export writes it (Realm|RegionId|X|Y|Z). The n-th
// bot of a realm starts and binds at that realm's n-th spot; bots past the list start as before. A Shrouded Isles spot
// needs nothing more: no login step compares the region's expansion with the client type (only a move does,
// RegionChangeRequestHandler.cs:140, jump.cs:433), and the bot's client type is above every region's anyway
// (HeadlessClient.cs:28).
public sealed class PlayerBotSpots
{
    public const string EnvironmentVariable = "HEARTHDAOC_PLAYER_BOT_SPOTS";
    public const int SeedReach = 1_000, SeedFar = WorldMgr.VISIBILITY_DISTANCE, NavBox = 256, NavHeight = 1_024,
        FloorAbove = 2_048, FloorDepth = 8_192, UnseededTop = 32_000, UnseededDepth = 40_000;
    public static readonly PlayerBotSpots None = new([]);

    private readonly Dictionary<eRealm, List<PlayerBotSpot>> _byRealm;
    public int Count { get; }

    private PlayerBotSpots(IReadOnlyCollection<PlayerBotSpot> spots)
    {
        Count = spots.Count;
        _byRealm = spots.GroupBy(s => s.Realm).ToDictionary(g => g.Key, g => g.ToList());
    }

    public static PlayerBotSpots Load(string path) =>
        string.IsNullOrWhiteSpace(path) ? None : Parse(File.ReadAllLines(path.Trim()));

    public static PlayerBotSpots Parse(IEnumerable<string> lines)
    {
        var spots = new List<PlayerBotSpot>();
        int number = 0;
        foreach (string raw in lines)
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] p = line.Split('|').Select(part => part.Trim()).ToArray();
            eRealm? realm = p[0].ToLowerInvariant() switch
            {
                "alb" or "1" => eRealm.Albion,
                "mid" or "2" => eRealm.Midgard,
                "hib" or "3" => eRealm.Hibernia,
                _ => null,
            };
            if (realm == null || p.Length is < 4 or > 5 ||
                !ushort.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort region) ||
                !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out int y))
                throw new FormatException($"{EnvironmentVariable} line {number} must be realm|region|x|y[|z], got '{raw}'");
            int? z = null;
            if (p.Length == 5)
            {
                if (!int.TryParse(p[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int zz))
                    throw new FormatException($"{EnvironmentVariable} line {number} has a bad z, got '{raw}'");
                z = zz;
            }
            spots.Add(new(realm.Value, region, x, y, z));
        }
        return new(spots);
    }

    public PlayerBotSpot For(PlayerBotSlot slot) =>
        _byRealm.TryGetValue(slot.Realm, out List<PlayerBotSpot> list) && slot.Index <= list.Count ? list[slot.Index - 1] : null;

    public string Describe() => Count == 0 ? "none" : string.Join(",",
        new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }.Select(r =>
            PlayerBotPlan.RealmKey(r) + ":" + (_byRealm.TryGetValue(r, out List<PlayerBotSpot> l) ? l.Count : 0)));

    /// <summary>The Z (and a mesh-snapped X/Y). The file's own Z, else the seed NPC's when it stands within SeedFar
    /// (one farther off stands elsewhere: no seed). Then the navmesh near that Z, else the floor under it (from the top
    /// of the world with no Z), else the file's Z as it is, else the seed's when it stands within SeedReach, else
    /// skipped.</summary>
    public static bool ResolveZ(IPathfindingMgr nav, Zone zone, int x, int y, int? fileZ, int? seedZ, int seedDistance,
        out Vector3 at, out string how)
    {
        if (seedDistance > SeedFar) seedZ = null;
        int? z = fileZ ?? seedZ;
        if (nav.IsAvailable && nav.HasNavmesh(zone))
        {
            if (z is int z0 && nav.GetClosestPoint(zone, new(x, y, z0), NavBox, NavBox, NavHeight, nav.DefaultFilters) is Vector3 near)
            { at = near; how = "mesh"; return true; }
            Vector3 from = z is int z1 ? new(x, y, z1 + FloorAbove) : new(x, y, UnseededTop);
            if (nav.GetFloorBeneath(zone, from, z.HasValue ? FloorDepth : UnseededDepth, nav.DefaultFilters) is Vector3 floor)
            { at = floor; how = "floor"; return true; }
        }
        if (fileZ is int zf) { at = new(x, y, zf); how = "file"; return true; }
        if (seedZ is int z2 && seedDistance <= SeedReach) { at = new(x, y, z2); how = "npc"; return true; }
        at = default; how = "skipped"; return false;
    }

    public static void Apply(DbCoreCharacter row, ushort region, Vector3 at)
    {
        row.Region = region; row.Xpos = (int)at.X; row.Ypos = (int)at.Y; row.Zpos = (int)at.Z; row.Direction = 0;
        row.BindRegion = region; row.BindXpos = (int)at.X; row.BindYpos = (int)at.Y; row.BindZpos = (int)at.Z;
        row.BindHeading = 0;                                     // DbCoreCharacter.cs:775-928, :1064
    }

    public static string Format(string account, PlayerBotSpot spot, Vector3 at, string how) => FormattableString.Invariant(
        $"PLAYER_BOT_SPOT account={account} region={spot.Region} spot=({spot.X},{spot.Y}) at=({at.X:0},{at.Y:0},{at.Z:0}) how={how}");

    /// <summary>On the game loop, before the login: the spot onto the row LoadPlayer reads (PlayerBotAccounts.LoginRow).
    /// False leaves the row as it is.</summary>
    public static bool Place(PlayerBotSpot spot, DbCoreCharacter row, string account, Logger log)
    {
        Zone zone = WorldMgr.GetRegion(spot.Region)?.GetZone(spot.X, spot.Y);   // Region.cs:1171
        string how = "no-zone";
        Vector3 at = default;
        bool placed = false;
        if (zone != null)
        {
            int? seedZ = null;
            int seedDistance = int.MaxValue;
            if (spot.Z == null)
            {
                GameNPC seed = null;
                long best = long.MaxValue;
                foreach (GameNPC npc in WorldMgr.GetNPCsFromRegion(spot.Region))   // every NPC in it (WorldMgr.cs:776-783)
                {
                    long dx = npc.X - spot.X, dy = npc.Y - spot.Y, d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; seed = npc; }
                }
                seedZ = seed?.Z;
                seedDistance = seed == null ? int.MaxValue : (int)Math.Min(int.MaxValue, Math.Sqrt(best));
            }
            placed = ResolveZ(PathfindingProvider.Instance, zone, spot.X, spot.Y, spot.Z, seedZ, seedDistance, out at, out how);
        }
        if (placed)
        {
            Apply(row, spot.Region, at);
            GameServer.Database.SaveObject(row);
        }
        if (log.IsInfoEnabled)
            log.Info(Format(account, spot, at, how));
        return placed;
    }
}
