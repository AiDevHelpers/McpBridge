using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>One .NET diagnostic CLI: the global tool's executable name, its package id, and the resource name of its one-line purpose.</summary>
public sealed record DiagTool(string Executable, string Package, string PurposeKey);

/// <summary>Result of a status or run call. Error means nothing ran; otherwise ExitCode, Output and File describe the run.</summary>
public sealed record DiagOutcome(bool Ok, string? Error, string? Command = null, int? ExitCode = null, string? Output = null, string? File = null);

/// <summary>
/// Wraps the .NET diagnostic global tools (dotnet-dump, dotnet-stack, dotnet-counters, dotnet-gcdump, dotnet-trace, dotnet-symbol)
/// behind a fixed set of operations. Each operation builds its argument list itself, so the caller passes a pid, a number of seconds
/// or a path and never a command line. Nothing is installed here: a missing tool is reported with its install command.
/// </summary>
public sealed class DotnetDiagService(
    Func<CommandRequest, CancellationToken, Task<CommandResult>>? runner = null,
    Func<string, string?>? locate = null,
    string? outputDir = null)
{
    /// <summary>The six tools, by executable name. Install commands are in docs/prereqs.md.</summary>
    public static readonly IReadOnlyList<DiagTool> Tools =
    [
        new("dotnet-dump", "dotnet-dump", "DiagPurposeDump"),
        new("dotnet-stack", "dotnet-stack", "DiagPurposeStack"),
        new("dotnet-counters", "dotnet-counters", "DiagPurposeCounters"),
        new("dotnet-gcdump", "dotnet-gcdump", "DiagPurposeGcdump"),
        new("dotnet-trace", "dotnet-trace", "DiagPurposeTrace"),
        new("dotnet-symbol", "dotnet-symbol", "DiagPurposeSymbol"),
    ];

    private const int MaxSeconds = 300;

    private readonly Func<CommandRequest, CancellationToken, Task<CommandResult>> _runner = runner ?? CommandRunner.RunAsync;
    private readonly Func<string, string?> _locate = locate ?? LocateOnPath;
    private readonly string _outputDir = outputDir ?? Path.Combine(Path.GetTempPath(), "aidev-diag");

    /// <summary>Whether each tool is on PATH (or in %USERPROFILE%\.dotnet\tools), and its version when it is.</summary>
    public async Task<IReadOnlyList<(DiagTool Tool, string? Path, string? Version)>> StatusAsync(CancellationToken ct)
    {
        var rows = new List<(DiagTool, string?, string?)>();
        foreach (DiagTool tool in Tools)
        {
            string? path = _locate(tool.Executable);
            string? version = null;
            // dotnet-symbol has no --version flag; its help text would be reported as the version.
            if (path is not null && tool.Executable != "dotnet-symbol")
            {
                CommandResult probe = await _runner(new CommandRequest { FileName = path, Arguments = ["--version"], Timeout = TimeSpan.FromSeconds(30) }, ct);
                version = probe.Success ? probe.StdOut.Trim() : null;
            }

            rows.Add((tool, path, version));
        }

        return rows;
    }

    /// <summary>
    /// Runs one operation. action: processes | stack | dump | gcdump | counters | trace | symbols. pid is needed for every action
    /// except processes and symbols; seconds for counters and trace; path is the output file (or the file to symbolize).
    /// </summary>
    public async Task<DiagOutcome> RunAsync(string action, int pid, int seconds, string? path, CancellationToken ct)
    {
        string key = (action ?? "").Trim().ToLowerInvariant();
        string tool;
        List<string> args = [];
        string? file = null;
        TimeSpan timeout = TimeSpan.FromMinutes(5);

        switch (key)
        {
            case "processes":
                tool = "dotnet-dump";
                args.Add("ps");
                break;

            case "stack":
                tool = "dotnet-stack";
                if (!ValidPid(pid, out string? pidError)) return Fail(pidError);
                args.AddRange(["report", "-p", pid.ToString()]);
                break;

            case "dump":
                tool = "dotnet-dump";
                if (!ValidPid(pid, out pidError)) return Fail(pidError);
                file = OutputFile(path, "dump", pid, "dmp");
                args.AddRange(["collect", "-p", pid.ToString(), "-o", file]);
                timeout = TimeSpan.FromMinutes(10);
                break;

            case "gcdump":
                tool = "dotnet-gcdump";
                if (!ValidPid(pid, out pidError)) return Fail(pidError);
                file = OutputFile(path, "gcdump", pid, "gcdump");
                args.AddRange(["collect", "-p", pid.ToString(), "-o", file]);
                break;

            case "counters":
                tool = "dotnet-counters";
                if (!ValidPid(pid, out pidError)) return Fail(pidError);
                if (!ValidSeconds(seconds, out string? secError)) return Fail(secError);
                file = OutputFile(path, "counters", pid, "csv");
                args.AddRange(["collect", "-p", pid.ToString(), "--duration", Duration(seconds), "--format", "csv", "-o", file]);
                timeout = TimeSpan.FromSeconds(seconds + 60);
                break;

            case "trace":
                tool = "dotnet-trace";
                if (!ValidPid(pid, out pidError)) return Fail(pidError);
                if (!ValidSeconds(seconds, out secError)) return Fail(secError);
                file = OutputFile(path, "trace", pid, "nettrace");
                args.AddRange(["collect", "-p", pid.ToString(), "--duration", Duration(seconds), "-o", file]);
                timeout = TimeSpan.FromSeconds(seconds + 120);
                break;

            case "symbols":
                tool = "dotnet-symbol";
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return Fail(Strings.Get("DiagSymbolsNeedPath"));
                if (!File.Exists(path)) return Fail(Strings.Format("DiagFileNotFound", path));
                args.Add(path);
                timeout = TimeSpan.FromMinutes(10);
                break;

            default:
                return Fail(Strings.Format("DiagUnknownAction", action));
        }

        string? exe = _locate(tool);
        if (exe is null)
        {
            DiagTool info = Tools.Single(t => t.Executable == tool);
            return Fail(Strings.Format("DiagNotInstalled", tool, info.Package));
        }

        Directory.CreateDirectory(_outputDir);
        CommandResult result = await _runner(new CommandRequest { FileName = exe, Arguments = args, Timeout = timeout }, ct);
        if (!result.Launched)
        {
            return Fail(Strings.Format("DiagCouldNotStart", tool, result.LaunchError));
        }

        string output = (result.StdOut + result.StdErr).Trim();
        return new DiagOutcome(
            result.Success,
            result.Success ? null : (result.TimedOut ? Strings.Format("DiagTimedOut", tool) : Strings.Format("DiagExitCode", tool, result.ExitCode)),
            $"{tool} {string.Join(' ', args)}",
            result.ExitCode,
            output,
            file is not null && File.Exists(file) ? file : null);
    }

    private static DiagOutcome Fail(string? error) => new(false, error);

    private static bool ValidPid(int pid, out string? error)
    {
        error = pid > 0 ? null : Strings.Get("DiagPidInvalid");
        return pid > 0;
    }

    private static bool ValidSeconds(int seconds, out string? error)
    {
        error = seconds is >= 1 and <= MaxSeconds ? null : Strings.Format("DiagSecondsRange", MaxSeconds);
        return error is null;
    }

    private static string Duration(int seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private string OutputFile(string? path, string kind, int pid, string extension)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.IsPathRooted(path) ? path : Path.Combine(_outputDir, path);
        }

        return Path.Combine(_outputDir, $"{kind}-{pid}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}");
    }

    private static string? LocateOnPath(string executable)
    {
        string file = executable + ".exe";
        string toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", file);
        if (File.Exists(toolsDir))
        {
            return toolsDir;
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null)
        {
            return null;
        }

        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
