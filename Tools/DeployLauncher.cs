using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

internal static class DeployLauncher
{
    private static int Main(string[] args)
    {
        int result = 1;
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string script = Path.Combine(root, "Tools", "release_game.py");
            if (!File.Exists(script)) throw new Exception("Keep DeployGame.exe at the project root beside Tools.");
            string[] allowed = { "--help", "--dry-run", "--deploy-existing", "--no-pause" };
            if (args.Any(a => !allowed.Contains(a))) throw new Exception("Unknown option. Use --help.");
            var process = Process.Start(new ProcessStartInfo {
                FileName = "python", WorkingDirectory = root, UseShellExecute = false,
                Arguments = "\"" + script + "\" " + string.Join(" ", args.Where(a => a != "--no-pause"))
            });
            process.WaitForExit(); result = process.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); }
        if (!args.Contains("--no-pause")) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
        return result;
    }
}
