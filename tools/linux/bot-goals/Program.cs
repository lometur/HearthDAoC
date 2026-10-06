using System.Net.NetworkInformation;
using OfflineDaoc.Configuration;

// bot-goals [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> | import <file.json>
// The server reads <dir>/bot-goals.json once at startup. Like the launcher, saving requires the
// server to be stopped, detected as no TCP listener on --port (default 10301).
internal static class Program
{
    static int _port = 10301;

    static bool ServerStopped() =>
        !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == _port);

    static void Print(BotGoalSettings s, string path)
    {
        Console.WriteLine(File.Exists(path) ? $"Bot goals from {path}:" : "No bot-goals.json yet; the server uses the defaults:");
        Console.WriteLine("  Levels    Solo PvE  Group PvE  RvR");
        foreach (var (name, row) in new[] { ("1-19 ", s.Levels1To19), ("20-49", s.Levels20To49), ("50   ", s.Level50) })
            Console.WriteLine($"  {name}     {row.SoloPve,4}%     {row.GroupPve,4}%  {row.RvR,3}%");
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
        if (args.Count < 2) { Console.Error.WriteLine("usage: bot-goals [--port N] <dir> show | reset | set <1-19|20-49|50> <solo> <group> <rvr> | import <file.json>"); return 2; }
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
                case "set" when args.Count == 6:
                    var row = new BotGoalWeights(int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
                    current = args[2] switch
                    {
                        "1-19" => current with { Levels1To19 = row },
                        "20-49" => current with { Levels20To49 = row },
                        "50" => current with { Level50 = row },
                        _ => throw new ArgumentException("Level band must be 1-19, 20-49 or 50."),
                    };
                    current.Save(path, ServerStopped);
                    break;
                case "import" when args.Count == 3:
                    BotGoalSettings.Load(Path.GetFullPath(args[2])).Save(path, ServerStopped); // Load validates
                    break;
                default:
                    Console.Error.WriteLine("Unknown command.");
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
