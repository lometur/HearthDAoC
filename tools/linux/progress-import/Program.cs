namespace OfflineDaoc.ProgressImport;

// Same behaviour as the --import branch of upstream's Program.cs, without the Windows Forms window.
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--import" && args[3] == "--replace-progress")
        {
            bool leaveBots = args.Contains("--leave-sluaghbinder-bots");
            string report = args.Skip(4).FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(args[2], "import-test-result.txt");
            try { var lines = new List<string>(); string backup = ImportEngine.Import(args[1], args[2], lines.Add, leaveBots); lines.Add("SUCCESS " + backup); File.WriteAllLines(report, lines); return 0; }
            catch (Exception e) { File.WriteAllText(report, e.ToString()); return 1; }
        }
        Console.Error.WriteLine("usage: progress-import --import <old folder> <new folder> --replace-progress [report.txt] [--leave-sluaghbinder-bots]");
        return 2;
    }
}
