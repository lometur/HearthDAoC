using System.Net.NetworkInformation;
using OfflineDaoc.Configuration;

// bot-goals [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> [<battlegrounds>] | import <file.json>
// The server reads <dir>/bot-goals.json once at startup. Like the launcher, saving requires the
// server to be stopped, detected as no TCP listener on --port (default 10301).
// set without <battlegrounds> keeps that row's Battlegrounds % (upstream 0.35's fourth goal).
internal static class Program
{
    const string Usage = "usage: bot-goals [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> [<battlegrounds>] | import <file.json>";
    static int _port = 10301;

    static bool ServerStopped() =>
        !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == _port);

    static void Print(BotGoalSettings s, string path)
    {
        Console.WriteLine(File.Exists(path) ? $"Bot goals from {path}:" : "No bot-goals.json yet; the server uses the defaults:");
        Console.WriteLine("  Levels    Solo PvE  Group PvE  RvR  Battlegrounds");
        foreach (var (name, row) in new[] { ("1-19 ", s.Levels1To19), ("20-49", s.Levels20To49), ("50   ", s.Level50) })
            Console.WriteLine($"  {name}     {row.SoloPve,4}%     {row.GroupPve,4}%  {row.RvR,3}%              {row.Battlegrounds,3}%");
    }

    static int Main(string[] argv)
    {
        var args = argv.ToList();
        int portAt = args.IndexOf("--port");
        if (portAt >= 0)
        {
            if (portAt + 1 >= args.Count || !int.TryParse(args[portAt + 1], out _port)) { Console.Error.WriteLine("--port needs a number"); return 2; }
            args.RemoveRange(portAt, 2);
        }
        if (args.Count < 2) { Console.Error.WriteLine(Usage); return 2; }
        string path = Path.Combine(args[0], BotGoalSettings.FileName);
        try
        {
            BotGoalSettings current = BotGoalSettings.Load(path);
            switch (args[1])
            {
                case "show":
                    Print(current, path);
                    return 0;
                case "reset":
                    BotGoalSettings.Defaults.Save(path, ServerStopped);
                    break;
                case "set" when args.Count is 6 or 7:
                    BotGoalWeights old = args[2] switch
                    {
                        "1-19" => current.Levels1To19,
                        "20-49" => current.Levels20To49,
                        "50" => current.Level50,
                        _ => throw new ArgumentException("Level band must be 1-19, 20-49 or 50."),
                    };
                    bool keptBattlegrounds = args.Count == 6;
                    var row = new BotGoalWeights(int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]),
                        keptBattlegrounds ? old.Battlegrounds : int.Parse(args[6]));
                    current = args[2] switch
                    {
                        "1-19" => current with { Levels1To19 = row },
                        "20-49" => current with { Levels20To49 = row },
                        _ => current with { Level50 = row },
                    };
                    try { current.Save(path, ServerStopped); }
                    catch (InvalidDataException e) when (keptBattlegrounds && row.Battlegrounds != 0)
                    {
                        throw new InvalidDataException($"{e.Message} (Battlegrounds stays at {row.Battlegrounds}%; give it as a fourth number to change it.)");
                    }
                    break;
                case "import" when args.Count == 3:
                    BotGoalSettings.Load(Path.GetFullPath(args[2])).Save(path, ServerStopped); // Load validates
                    break;
                default:
                    Console.Error.WriteLine("Unknown command. " + Usage);
                    return 2;
            }
            Print(BotGoalSettings.Load(path), path);
            Console.WriteLine("Saved. The server applies it on its next start.");
            return 0;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("Not saved: " + e.Message);
            return 1;
        }
    }
}
