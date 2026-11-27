using System.Diagnostics;
using AiDevHelpers.Bridge;
using AiDevHelpers.Helpers.RoslynHelper;

namespace AiDevHelpers.Helpers.RoslynHelper48;

/// <summary>
/// The net48 helper's entry point. It mirrors the net10 helper's: one request line in, one reply line out, a manifest in the
/// shared folder. Its pipe is <c>AiDevHelpers.Roslyn48.&lt;pid&gt;</c> and its manifest says <c>framework: net48</c>.
/// </summary>
public static class Program
{
    private const string Framework = "net48";

    private static readonly string DefaultManifestFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AiDevHelpers",
        "roslyn-helpers");

    public static int Main(string[] args)
    {
        if (!TryParse(args, out var solutionPath, out var manifestFolder, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var pid = Process.GetCurrentProcess().Id;
        var pipeName = $"AiDevHelpers.Roslyn48.{pid}";
        var handler = new RoslynRequestHandler(() => solutionPath, framework: Framework);
        using var server = new BridgeServer(pipeName, handler.InvokeAsync, () => solutionPath, new ConsoleLogger());
        var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ManifestWriter.Remove(manifestFolder, pid);

        try
        {
            ManifestWriter.Write(manifestFolder, pid, pipeName, solutionPath, Framework);
            server.Start();
            exit.Wait();
            return 0;
        }
        finally
        {
            server.Stop();
            ManifestWriter.Remove(manifestFolder, pid);
        }
    }

    private static bool TryParse(string[] args, out string solutionPath, out string manifestFolder, out string error)
    {
        solutionPath = string.Empty;
        manifestFolder = DefaultManifestFolder;
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--solution" when i + 1 < args.Length:
                    solutionPath = args[++i];
                    break;
                case "--manifest-folder" when i + 1 < args.Length:
                    manifestFolder = args[++i];
                    break;
                default:
                    error = $"Unknown or incomplete argument: {args[i]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            error = "Usage: RoslynHelper48 --solution <path> [--manifest-folder <path>]";
            return false;
        }

        return true;
    }

    private sealed class ConsoleLogger : IBridgeLogger
    {
        public void LogDebug(string message) { }
        public void LogInfo(string message) => Console.WriteLine(message);
        public void LogError(string message) => Console.Error.WriteLine(message);
        public void LogException(Exception exception) => Console.Error.WriteLine(exception);
    }
}
