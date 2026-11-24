using System;
using System.Collections.Generic;
using AiDevHelpers.Common.Processes;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Runs tests for an IDE solution with <c>dotnet test</c> and a TRX reporter, and reads the newest run's TRX files back.
/// TRX is a documented, IDE-independent format. Visual Studio's Test Explorer results aren't exposed to extensions,
/// so IdeGetTestResults reads TRX files instead. Each run writes into its own folder under TestResults, one TRX per test project.
/// </summary>
internal static class IdeTestRunService
{
    public const string DotnetTestMode = "DotnetTest";
    public const string TestExplorerMode = "TestExplorer";
    private const string ResultsFolder = "TestResults";
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Returns the configured mode. Anything other than "DotnetTest" is treated as Test Explorer.</summary>
    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, DotnetTestMode, StringComparison.OrdinalIgnoreCase) ? DotnetTestMode : TestExplorerMode;

    // A .sln path gives its folder; a folder (a VS Code workspace) is used as is.
    private static string SolutionDirectory(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        return Directory.Exists(full)
            ? full
            : Path.GetDirectoryName(full) ?? throw new InvalidOperationException(Strings.Get("NoSolutionDirectory"));
    }

    /// <summary>The <c>dotnet test</c> arguments that write TRX files into <paramref name="runDir"/>.</summary>
    internal static IReadOnlyList<string> TestArguments(string solutionPath, string solutionDir, string runDir) =>
        UsesTestingPlatform(solutionDir)
            ? ["test", solutionPath, "--report-xunit-trx", "--results-directory", runDir]
            : ["test", solutionPath, "--logger", "trx", "--results-directory", runDir];

    private static readonly string[] TestingPlatformMarkers =
        ["xunit.v3", "Microsoft.Testing.Platform", "UseMicrosoftTestingPlatformRunner", "TestingPlatformDotnetTestSupport"];

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", ".git", "TestResults"];

    // Microsoft.Testing.Platform projects take --report-xunit-trx; VSTest projects reject it and need the trx logger.
    // A marker in any project or build props file under the solution folder counts, since props files apply to every project.
    private static bool UsesTestingPlatform(string solutionDir)
    {
        if (!Directory.Exists(solutionDir))
        {
            return false;
        }

        return Directory.EnumerateFiles(solutionDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(solutionDir, f).Split(Path.DirectorySeparatorChar)
                .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Any(f =>
            {
                var text = File.ReadAllText(f);
                return TestingPlatformMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
            });
    }

    /// <summary>Runs <c>dotnet test</c> on the solution, writing one TRX per test project into a new run folder under TestResults.</summary>
    public static async Task<string> RunDotnetTestAsync(string solutionPath, CancellationToken ct, string? project = null)
    {
        var solutionDir = SolutionDirectory(solutionPath);
        var runDir = Path.Combine(solutionDir, ResultsFolder, $"tokensaver-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        // A project file runs only that project; otherwise the whole solution runs.
        var target = project is null ? solutionPath : Path.GetFullPath(Path.Combine(solutionDir, project));

        CommandResult run = await CommandRunner.RunAsync(
            new CommandRequest
            {
                FileName = "dotnet",
                Arguments = [.. TestArguments(target, solutionDir, runDir)],
                WorkingDirectory = solutionDir,
                Timeout = TimeSpan.FromMinutes(30),
                OnStarted = process => ManagedProcessRegistry.Track(process, "test run"),
            },
            ct);
        if (!run.Launched)
        {
            throw new InvalidOperationException(Strings.Format("DotnetTestNotStarted", run.LaunchError));
        }

        var trxFiles = TrxFilesIn(runDir);
        var summary = ReadSummary(trxFiles);
        return JsonSerializer.Serialize(new
        {
            ok = run.Success,
            exit_code = run.ExitCode,
            mode = DotnetTestMode,
            trx = runDir,
            trx_files = trxFiles.Select(f => f.FullName).ToList(),
            summary,
            timed_out = run.TimedOut,
            output_tail = Tail(run.StdOut + run.StdErr, 4000),
        });
    }

    /// <summary>Reads every TRX file of the newest run folder under TestResults, summed across test projects.</summary>
    public static string GetLatestResult(string solutionPath)
    {
        var solutionDir = SolutionDirectory(solutionPath);
        var resultsDir = Path.Combine(solutionDir, ResultsFolder);
        if (!Directory.Exists(resultsDir))
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Format("NoResultsFolder", ResultsFolder, solutionDir) });
        }

        // Newest run folder that holds TRX files; a TRX written directly in TestResults counts as its own run.
        var newestRun = new DirectoryInfo(resultsDir).EnumerateDirectories()
            .Where(d => TrxFilesIn(d.FullName).Count > 0)
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault();
        var trxFiles = newestRun is not null
            ? TrxFilesIn(newestRun.FullName)
            : new DirectoryInfo(resultsDir).EnumerateFiles("*.trx")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(1)
                .ToList();
        if (trxFiles.Count == 0)
        {
            return JsonSerializer.Serialize(new { ok = false, error = Strings.Get("NoTrxResults") });
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            trx = trxFiles.OrderByDescending(f => f.LastWriteTimeUtc).First().FullName,
            trx_files = trxFiles.Select(f => f.FullName).ToList(),
            recorded_utc = trxFiles.Max(f => f.LastWriteTimeUtc),
            summary = ReadSummary(trxFiles),
        });
    }

    private static List<FileInfo> TrxFilesIn(string directory) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*.trx", SearchOption.AllDirectories).ToList()
            : [];

    private static object ReadSummary(IReadOnlyList<FileInfo> trxFiles)
    {
        if (trxFiles.Count == 0)
        {
            return new { error = Strings.Get("TrxFileNotFound") };
        }

        int total = 0, passed = 0, failed = 0, skipped = 0;
        var failures = new List<object>();
        foreach (var file in trxFiles)
        {
            var doc = XDocument.Load(file.FullName);
            var counters = doc.Descendants(Trx + "Counters").FirstOrDefault();
            total += (int?)counters?.Attribute("total") ?? 0;
            passed += (int?)counters?.Attribute("passed") ?? 0;
            failed += (int?)counters?.Attribute("failed") ?? 0;
            skipped += (int?)counters?.Attribute("notExecuted") ?? 0;
            failures.AddRange(doc.Descendants(Trx + "UnitTestResult")
                .Where(r => string.Equals((string?)r.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
                .Select(r => (object)new
                {
                    test_name = (string?)r.Attribute("testName"),
                    message = (string?)r.Descendants(Trx + "Message").FirstOrDefault(),
                    stack_trace = (string?)r.Descendants(Trx + "StackTrace").FirstOrDefault(),
                }));
        }

        return new { total, passed, failed, skipped, failures };
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[^maxChars..];
}
