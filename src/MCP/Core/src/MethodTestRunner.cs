using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.TestingHelper;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Runs the tests for a method that an edit tool just changed, with coverage, and reports each test's status and the method's line
/// coverage. The edit tool's reply names the tests that call the method (<c>check.tests</c>) and where the method sits
/// (<c>check.method</c>). With no direct test, the test projects the changed files affect run whole.
/// </summary>
internal static class MethodTestRunner
{
    private const string ResultsFolder = "TestResults";

    /// <summary>
    /// When the reply says an edit was applied, runs its tests and adds a <c>test_run</c> property to the reply. Any other reply is
    /// returned unchanged.
    /// </summary>
    /// <summary>Mode for <see cref="AttachAsync"/>: the first run of a new test, which should fail.</summary>
    public const string TddRed = "tdd-red";

    /// <summary>Mode for <see cref="AttachAsync"/>: a test whose body was just updated.</summary>
    public const string TddUpdate = "tdd-update";

    public static async Task<string> AttachAsync(string reply, bool enabled, CancellationToken ct, string? mode = null)
    {
        if (!enabled)
        {
            return reply;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(reply);
        }
        catch (JsonException)
        {
            return reply;
        }

        if (node is not JsonObject root || root["result"] is not JsonObject result || result["applied"]?.GetValue<bool>() != true
            || result["check"] is not JsonObject check || root["solution_path"]?.GetValue<string>() is not { Length: > 0 } solution)
        {
            return reply;
        }

        try
        {
            using var checkDocument = JsonDocument.Parse(check.ToJsonString());
            var run = JsonSerializer.SerializeToNode(await RunAsync(solution, checkDocument.RootElement, null, ct).ConfigureAwait(false));
            root["test_run"] = run;
            if (mode is not null && run is JsonObject runObject)
            {
                var first = check["tests"] is JsonArray { Count: > 0 } tests ? tests[0]?["name"]?.GetValue<string>() : null;
                root["tdd"] = JsonSerializer.SerializeToNode(Classify(runObject, first, mode));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            root["test_run"] = JsonSerializer.SerializeToNode(new { ran = false, error = ex.Message });
        }

        return root.ToJsonString();
    }

    // red: the test ran and failed; green: it ran and passed; not_run: it did not report (build failure, filter mismatch).
    private static object Classify(JsonObject run, string? testName, string mode)
    {
        var outcome = run["tests"] is JsonArray all
            ? all.OfType<JsonObject>().FirstOrDefault(t => testName is not null
                && (t["name"]?.GetValue<string>() ?? "").EndsWith(testName.Split('.').Last(), StringComparison.OrdinalIgnoreCase))
            : null;
        var result = outcome?["outcome"]?.GetValue<string>();
        var status = result switch { "Failed" => "red", "Passed" => "green", _ => "not_run" };
        var note = mode == TddRed
            ? status switch { "green" => Strings.Get("TddGreenOnFirstRun"), "not_run" => Strings.Get("TddNotRun"), _ => null }
            : status == "not_run" ? Strings.Get("TddNotRun") : null;
        return new { test = testName, status, red_light_ok = mode == TddRed ? status == "red" : (bool?)null, message = outcome?["message"]?.GetValue<string>(), note };
    }

    /// <summary>Runs the tests named in <paramref name="check"/>; <paramref name="onlyTests"/> overrides them (the TDD tools).</summary>
    public static async Task<object> RunAsync(string solutionPath, JsonElement check, IReadOnlyList<(string Project, string Name)>? onlyTests, CancellationToken ct)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var warnings = new List<string>();
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (project, name) in onlyTests ?? ReadTests(check))
        {
            if (!groups.TryGetValue(project, out var list))
            {
                groups[project] = list = new List<string>();
            }

            if (!list.Contains(name))
            {
                list.Add(name);
            }
        }

        var scope = "direct";
        if (groups.Count == 0)
        {
            scope = "project";
            warnings.Add(Strings.Get("MethodTestsNoDirect"));
            foreach (var project in AffectedTests.Find(root, ReadFiles(check)))
            {
                groups[project] = new List<string>();
            }
        }

        if (groups.Count == 0)
        {
            warnings.Add(Strings.Get("MethodTestsNoProject"));
            return new { ran = false, scope = "none", warnings };
        }

        var method = check.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.Object ? m : (JsonElement?)null;
        var all = new List<TestOutcome>();
        var projects = new List<object>();
        var coverageNotes = new List<string>();
        MethodCoverage? coverage = null;
        string? provider = null;
        string? report = null;
        foreach (var (project, names) in groups)
        {
            var run = await RunProjectAsync(root, project, names, method is not null, warnings, ct).ConfigureAwait(false);
            all.AddRange(run.Outcomes);
            projects.Add(new { project, exit_code = run.ExitCode, timed_out = run.TimedOut, tests = run.Outcomes.Count });
            if (run.Report is not null && method is { } span)
            {
                provider = run.Provider;
                report = run.Report;
                var file = span.GetProperty("file").GetString()!;
                coverage = MethodCoverageReader.ForLines(File.ReadAllText(run.Report), file, span.GetProperty("start_line").GetInt32(), span.GetProperty("end_line").GetInt32());
                if (coverage is null)
                {
                    coverageNotes.Add(Strings.Get("MethodTestsNoCoverageLines"));
                }
            }
        }

        // A test the edit expected to run but the run did not report is shown as not run, not dropped.
        foreach (var expected in (onlyTests ?? ReadTests(check)).Select(t => t.Name).Distinct())
        {
            if (!all.Any(o => o.Name.Equals(expected, StringComparison.OrdinalIgnoreCase) || o.Name.EndsWith("." + expected.Split('.').Last(), StringComparison.OrdinalIgnoreCase)))
            {
                all.Add(new TestOutcome(expected, "NotRun", 0, null));
            }
        }

        warnings.AddRange(coverageNotes);
        return new
        {
            ran = true,
            scope,
            summary = new
            {
                total = all.Count,
                passed = all.Count(o => o.Outcome == "Passed"),
                failed = all.Count(o => o.Outcome == "Failed"),
                skipped = all.Count(o => o.Outcome is "NotExecuted" or "NotRun"),
            },
            tests = all.Select(o => new { name = o.Name, outcome = o.Outcome, duration_ms = o.DurationMs, message = o.Message }).ToList(),
            projects,
            coverage = new
            {
                provider,
                report,
                method = coverage is null ? null : new
                {
                    lines_total = coverage.LinesTotal,
                    lines_covered = coverage.LinesCovered,
                    rate = coverage.Rate,
                    uncovered_lines = coverage.UncoveredLines,
                },
            },
            warnings,
        };
    }

    private sealed class ProjectRun
    {
        public List<TestOutcome> Outcomes { get; } = new();

        public int ExitCode { get; set; }

        public bool TimedOut { get; set; }

        public string? Report { get; set; }

        public string? Provider { get; set; }
    }

    private static async Task<ProjectRun> RunProjectAsync(
        string root, string project, List<string> names, bool wantCoverage, List<string> warnings, CancellationToken ct)
    {
        var result = new ProjectRun();
        var runDir = Path.Combine(root, ResultsFolder, $"method-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetFileNameWithoutExtension(project)}");
        var mtp = IdeTestRunService.UsesTestingPlatform(root);
        var provider = wantCoverage ? CoverageProviderSelector.Select(CoverageSources(root, project)) : (CoverageProvider?)null;
        var useDotnetCoverage = false;
        if (provider == CoverageProvider.VisualStudio)
        {
            var probe = await CommandRunner.RunAsync(new CommandRequest { FileName = "dotnet-coverage", Arguments = ["--version"] }, ct).ConfigureAwait(false);
            useDotnetCoverage = probe.Launched && probe.ExitCode == 0;
            if (!useDotnetCoverage)
            {
                warnings.Add(Strings.Get("MethodTestsNoCoverageTool"));
            }
        }

        IReadOnlyList<string> testArgs;
        if (mtp)
        {
            // Microsoft.Testing.Platform has no VSTest filter, so the whole project runs.
            if (names.Count > 0)
            {
                warnings.Add(Strings.Get("MethodTestsFilterUnsupported"));
            }

            testArgs = ["test", project, "--report-xunit-trx", "--results-directory", runDir];
        }
        else
        {
            testArgs = TargetedRunArgs.Test(project, runDir, names, provider == CoverageProvider.Coverlet);
        }

        string? report = null;
        string fileName = "dotnet";
        IReadOnlyList<string> args = testArgs;
        if (useDotnetCoverage)
        {
            Directory.CreateDirectory(runDir);
            report = Path.Combine(runDir, "coverage.cobertura.xml");
            fileName = "dotnet-coverage";
            args = TargetedRunArgs.DotnetCoverage(testArgs, report);
        }

        var run = await CommandRunner.RunAsync(
            new CommandRequest
            {
                FileName = fileName,
                Arguments = [.. args],
                WorkingDirectory = root,
                Timeout = TimeSpan.FromMinutes(30),
                OnStarted = process => ManagedProcessRegistry.Track(process, "method test run"),
            },
            ct).ConfigureAwait(false);
        if (!run.Launched)
        {
            throw new InvalidOperationException(Strings.Format("DotnetTestNotStarted", run.LaunchError));
        }

        result.ExitCode = run.ExitCode;
        result.TimedOut = run.TimedOut;
        if (Directory.Exists(runDir))
        {
            foreach (var trx in Directory.EnumerateFiles(runDir, "*.trx", SearchOption.AllDirectories))
            {
                result.Outcomes.AddRange(TrxReader.ReadTests(File.ReadAllText(trx)));
            }

            report ??= Directory.EnumerateFiles(runDir, "coverage.cobertura.xml", SearchOption.AllDirectories).FirstOrDefault();
        }

        if (wantCoverage && report is not null && File.Exists(report))
        {
            result.Report = report;
            result.Provider = useDotnetCoverage ? "dotnet-coverage" : "coverlet";
        }

        if (result.Outcomes.Count == 0)
        {
            warnings.Add(Strings.Format("MethodTestsNoResults", run.ExitCode));
        }

        return result;
    }

    private static IEnumerable<string> CoverageSources(string root, string project)
    {
        yield return File.ReadAllText(project);
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                yield return File.ReadAllText(path);
            }
        }
    }

    private static List<(string Project, string Name)> ReadTests(JsonElement check)
    {
        var list = new List<(string, string)>();
        if (check.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tests.EnumerateArray())
            {
                list.Add((t.GetProperty("project").GetString()!, t.GetProperty("name").GetString()!));
            }
        }

        return list;
    }

    private static List<string> ReadFiles(JsonElement check) =>
        check.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Select(f => f.GetString()!).ToList()
            : new List<string>();
}
