using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.TestingHelper;

/// <summary>Captured output of a command.</summary>
public sealed record TestCommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs a command without a shell. Tests replace it with a recorder.</summary>
public interface ITestCommandRunner
{
    Task<TestCommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>Real runner: delegates to the shared Common runner, so no argument is shell-parsed.</summary>
public sealed class ProcessTestCommandRunner : ITestCommandRunner
{
    public async Task<TestCommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var result = await CommandRunner.RunAsync(new CommandRequest { FileName = file, Arguments = args }, ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Format("CouldNotStartProcess", file));
        }

        return new TestCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }
}

/// <summary>Outcome of a coverage run.</summary>
public sealed record CoverageRunResult(bool Ok, string? Tool, string? ReportPath, double? LineRate, string? Error);

/// <summary>
/// Runs coverage. Coverlet runs dotnet test with the collector. Visual Studio uses the IDE bridge when it is
/// available, and otherwise falls back to the dotnet-coverage CLI (D4). With no tool available, it returns a clear error (D14).
/// </summary>
public sealed class CoverageService(ITestCommandRunner runner, string resultsDirectory)
{
    private const string CoberturaName = "coverage.cobertura.xml";

    public async Task<CoverageRunResult> RunAsync(
        string solutionPath,
        CoverageProvider provider,
        bool bridgeAvailable,
        CancellationToken ct = default)
    {
        if (provider == CoverageProvider.Coverlet)
        {
            var run = await runner.RunAsync("dotnet", CoverageArgs.Coverlet(solutionPath, resultsDirectory), ct);
            return new CoverageRunResult(run.ExitCode == 0, "coverlet", FindReport(), ReadRate(), run.ExitCode == 0 ? null : Tail(run));
        }

        if (bridgeAvailable)
        {
            // The bridge coverage action is not built yet (phase 7 protocol work); fall through to the CLI.
        }

        var version = await runner.RunAsync("dotnet-coverage", ["--version"], ct);
        if (version.ExitCode != 0)
        {
            return new CoverageRunResult(false, null, null, null,
                Strings.Get("DotnetCoverageNotInstalled"));
        }

        var report = Path.Combine(resultsDirectory, CoberturaName);
        Directory.CreateDirectory(resultsDirectory);
        var collect = await runner.RunAsync("dotnet-coverage", CoverageArgs.DotnetCoverage(solutionPath, report), ct);
        return new CoverageRunResult(collect.ExitCode == 0, "dotnet-coverage", report, ReadRate(report), collect.ExitCode == 0 ? null : Tail(collect));
    }

    private string? FindReport()
    {
        if (!Directory.Exists(resultsDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(resultsDirectory, CoberturaName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private double? ReadRate(string? path = null)
    {
        var report = path ?? FindReport();
        return report is not null && File.Exists(report) ? CoverageSummary.LineRate(File.ReadAllText(report)) : null;
    }

    private static string Tail(TestCommandResult run) =>
        (run.Stderr.Length > 0 ? run.Stderr : run.Stdout) is { Length: > 2000 } text ? text[^2000..] : run.Stderr + run.Stdout;
}
