// Linux CLI port of the OfflineDAoC launcher's "+ Lv.1 / + Lv.50" bot buttons.
// Code below the marker is copied verbatim from source/tools/OfflineDaoc.Launcher/MainForm.cs (GPL-3.0);
// when upstream changes those functions, re-copy them here.
using System.Data.SQLite;
namespace OfflineDaoc.Launcher;
internal sealed class BotCli
{
    private readonly string _database;
    private BotCli(string db) { _database = db; }
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "add") { Console.Error.WriteLine("usage: offline-bots add <db> <realm 1=Alb 2=Mid 3=Hib> <count 1|10|100>[x<level 1|50>]"); return 2; }
        string[] cl = args[3].Split('x');
        int count = int.Parse(cl[0]); int level = cl.Length > 1 ? int.Parse(cl[1]) : 1;
        var ids = new BotCli(args[1]).GenerateBotCharacters(int.Parse(args[2]), count, level);
        foreach (var i in ids) Console.WriteLine($"{i.Name}\tL{level} {i.RaceName} {i.ClassName}");
        return 0;
    }
    // ---- copied from MainForm.cs ----
    // Mirrors the server's classes/enable_sluaghbinder property; a missing row means enabled (the "b" edition default).
    private static bool SluaghbinderEnabled(SQLiteConnection connection, SQLiteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM ServerProperty WHERE `Key`='enable_sluaghbinder' LIMIT 1";
        object? value = command.ExecuteScalar();
        return value is not string text || !text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<BotCharacterGenerator.Identity> GenerateBotCharacters(int realm, int count, int level = 1)
    {
        if (!File.Exists(_database))
            throw new InvalidOperationException("The prepared world database is missing.");
        if (count is not (1 or 10 or 100))
            throw new ArgumentOutOfRangeException(nameof(count));
        if (level is not (1 or 50)) throw new ArgumentOutOfRangeException(nameof(level));

        using var connection = new SQLiteConnection($"Data Source={_database};Version=3;Pooling=False;Default Timeout=10");
        connection.Open();
        using (var busy = connection.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=OFF;";
            busy.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var names = connection.CreateCommand())
        {
            names.Transaction = transaction;
            names.CommandText = "SELECT Name FROM offline_world_bots UNION SELECT Name FROM DOLCharacters";
            using var reader = names.ExecuteReader();
            while (reader.Read()) reserved.Add(reader.GetString(0));
        }

        bool allowSluaghbinder = SluaghbinderEnabled(connection, transaction);
        var identities = new List<BotCharacterGenerator.Identity>(count);
        for (int index = 0; index < count; index++)
        {
            BotCharacterGenerator.Identity identity = BotCharacterGenerator.Generate(realm, reserved, allowSluaghbinder);
            BotStartingLocation start = level == 50
                ? CapitalBotStartingLocation(identity.Realm)
                : ChooseBotStartingLocation(connection, transaction, identity.Realm, identity.RaceId, identity.ClassId);
            identities.Add(identity);
            string now = DateTime.UtcNow.ToString("O");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO offline_world_bots
                    (Name, Realm, ClassId, ClassName, RaceId, RaceName, Gender, Level, Experience, RealmPoints,
                     ZoneName, Activity, IsOnline, IsAlive, LastUpdateUtc, MoneyCopper, InventoryRevision,
                     ZoneId, X, Y, Z, RegionId, Health, Mana, Endurance, BindRegionId, BindX, BindY, BindZ,
                     CurrentGoal, ObjectiveProgress, LastMeaningfulProgressUtc, IsRetired)
                VALUES
                    (@name, @realm, @classId, @className, @raceId, @raceName, @gender, @level, @xp, 0,
                     @zoneName, 'Queued at randomized starting location', 0, 1, @now, @money, 0,
                     @zoneId, @x, @y, @z, @region, 1, 0, 0, @region, @x, @y, @z,
                     'Awaiting staggered login queue', @placement, @now, 0)
                """;
            insert.Parameters.AddWithValue("@name", identity.Name);
            insert.Parameters.AddWithValue("@level", level);
            insert.Parameters.AddWithValue("@xp", level == 50 ? 169999999950L : 0L);
            insert.Parameters.AddWithValue("@money", level == 50 ? 100000000L : 0L);
            insert.Parameters.AddWithValue("@realm", identity.Realm);
            insert.Parameters.AddWithValue("@classId", identity.ClassId);
            insert.Parameters.AddWithValue("@className", identity.ClassName);
            insert.Parameters.AddWithValue("@raceId", identity.RaceId);
            insert.Parameters.AddWithValue("@raceName", identity.RaceName);
            insert.Parameters.AddWithValue("@gender", identity.Gender);
            insert.Parameters.AddWithValue("@now", now);
            insert.Parameters.AddWithValue("@region", start.RegionId);
            insert.Parameters.AddWithValue("@x", start.X);
            insert.Parameters.AddWithValue("@y", start.Y);
            insert.Parameters.AddWithValue("@z", start.Z);
            insert.Parameters.AddWithValue("@zoneId", start.ZoneId);
            insert.Parameters.AddWithValue("@zoneName", level == 50 ? start.ZoneName : start.RegionId is 51 or 151 or 181
                ? "Shrouded Isles starting area"
                : "Classic starting area");
            insert.Parameters.AddWithValue("@placement",
                $"Launcher assigned a realm/race/class-valid random start in region {start.RegionId} before server startup");
            insert.ExecuteNonQuery();
            if (level == 50)
            {
                long botId = connection.LastInsertRowId;
                using var gear = connection.CreateCommand();
                gear.Transaction = transaction;
                gear.CommandText = """
                    INSERT INTO Inventory (Inventory_ID,OwnerID,ITemplate_Id,SlotPosition,Count,Condition,Durability,LastTimeRowUpdated)
                    SELECT lower(hex(randomblob(16))),@owner,l.TemplateId,l.SlotPosition,1,t.MaxCondition,t.MaxDurability,@now
                    FROM offline_level50_loadouts l JOIN ItemTemplate t ON t.Id_nb=l.TemplateId WHERE l.ClassId=@class
                    """;
                gear.Parameters.AddWithValue("@owner", $"offlinebot:{botId}");
                gear.Parameters.AddWithValue("@now", now);
                gear.Parameters.AddWithValue("@class", identity.ClassId);
                if (gear.ExecuteNonQuery() < 15) throw new InvalidOperationException("The level-50 class template is missing or incomplete. No bots were added.");
            }
        }

        UpdatePopulationTarget(connection, transaction);
        transaction.Commit();
        return identities;
    }

    private sealed record BotStartingLocation(int RegionId, int X, int Y, int Z, int ZoneId = 0, string ZoneName = "");

    private static BotStartingLocation CapitalBotStartingLocation(int realm) => realm switch
    {
        1 => new(10, 35990, 30298, 8000, 26, "City of Camelot"),
        2 => new(101, 32020, 28294, 8819, 120, "Jordheim"),
        3 => new(201, 33197, 31200, 8000, 209, "Tir na Nog"),
        _ => throw new ArgumentOutOfRangeException(nameof(realm)),
    };



    private static BotStartingLocation ChooseBotStartingLocation(
        SQLiteConnection connection,
        SQLiteTransaction transaction,
        int realm,
        int raceId,
        int classId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Region, XPos, YPos, ZPos
            FROM StartupLocation
            WHERE MinVersion <= 168
              AND (RealmID=0 OR RealmID=@realm)
              AND (RaceID=0 OR RaceID=@race)
              AND (ClassID=0 OR ClassID=@class)
              AND (XPos<>0 OR YPos<>0 OR ZPos<>0)
              AND ((@realm=1 AND Region IN (1,51))
                OR (@realm=2 AND Region IN (100,151))
                OR (@realm=3 AND Region IN (200,181)))
            GROUP BY Region, XPos, YPos, ZPos
            """;
        command.Parameters.AddWithValue("@realm", realm);
        command.Parameters.AddWithValue("@race", raceId);
        command.Parameters.AddWithValue("@class", classId);

        var starts = new List<BotStartingLocation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            starts.Add(new BotStartingLocation(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        if (starts.Count == 0)
        {
            throw new InvalidOperationException(
                $"No authoritative Classic/Shrouded Isles starting location exists for realm {realm}, race {raceId}, class {classId}. No capital fallback was used.");
        }

        return starts[Random.Shared.Next(starts.Count)];
    }

    private static void UpdatePopulationTarget(SQLiteConnection connection, SQLiteTransaction transaction)
    {
        int count;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM offline_world_bots WHERE IsRetired=0";
            count = Convert.ToInt32(countCommand.ExecuteScalar());
        }
        int target = count;
        using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = "UPDATE offline_population_settings SET Value=CASE Key WHEN 'PopulationEnabled' THEN @enabled WHEN 'ActiveTarget' THEN @target WHEN 'HardActiveCap' THEN '0' ELSE Value END WHERE Key IN ('PopulationEnabled','ActiveTarget','HardActiveCap')";
            settings.Parameters.AddWithValue("@enabled", count > 0 ? "true" : "false");
            settings.Parameters.AddWithValue("@target", target.ToString());
            settings.ExecuteNonQuery();
        }
        using (var serverProperties = connection.CreateCommand())
        {
            serverProperties.Transaction = transaction;
            serverProperties.CommandText = "UPDATE ServerProperty SET Value=CASE Key WHEN 'population_enabled' THEN @enabled WHEN 'active_target' THEN @target WHEN 'hard_active_cap' THEN '0' ELSE Value END WHERE Key IN ('population_enabled','active_target','hard_active_cap')";
            serverProperties.Parameters.AddWithValue("@enabled", count > 0 ? "true" : "false");
            serverProperties.Parameters.AddWithValue("@target", target.ToString());
            serverProperties.ExecuteNonQuery();
        }
    }
}
