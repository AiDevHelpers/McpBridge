using AiDevHelpers.Bridge;

namespace AiDevHelpers.Helpers.RoslynHelper;

public static class Program
{
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

        var pid = Environment.ProcessId;
        var pipeName = $"AiDevHelpers.Roslyn.{pid}";
        var handler = new RoslynRequestHandler(() => solutionPath);
        using var server = new BridgeServer(pipeName, handler.InvokeAsync, () => solutionPath, new ConsoleLogger());
        var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ManifestWriter.Remove(manifestFolder, pid);

        try
        {
            ManifestWriter.Write(manifestFolder, pid, pipeName, solutionPath);
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
            error = "Usage: RoslynHelper --solution <path> [--manifest-folder <path>]";
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
