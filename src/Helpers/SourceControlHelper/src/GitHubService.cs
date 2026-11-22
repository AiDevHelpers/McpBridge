using System.Text.Json;
using System.Text.RegularExpressions;
using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.SourceControlHelper;

/// <summary>Runs the GitHub CLI (gh). Injected so tests can see the exact arguments.</summary>
public interface IGhRunner
{
    Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

/// <summary>Runs gh.exe through the shared Common runner. A missing gh is reported as a failed result, not an exception.</summary>
public sealed class ProcessGhRunner : IGhRunner
{
    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var result = await CommandRunner.RunAsync(
            new CommandRequest { FileName = "gh", Arguments = arguments, WorkingDirectory = workingDirectory }, ct);
        if (!result.Launched)
        {
            return new GitResult(false, -1, Strings.Get("GhNotFound"));
        }

        return new GitResult(result.Success, result.ExitCode, result.StdOut + result.StdErr);
    }
}

/// <summary>One CI check of a pull request. <see cref="Bucket"/> is gh's grouping: pass, fail, pending, skipping or cancel.</summary>
public sealed record CheckStatus(string Name, string State, string Bucket, string? Link);

/// <summary>The checks of a pull request, or <see cref="Error"/> when gh gave none.</summary>
public sealed record ChecksResult(IReadOnlyList<CheckStatus> Checks, string? Error);

/// <summary>The first failing job of a pull request and the error lines of its log.</summary>
public sealed record CiFailure(string? Job, string? RunId, IReadOnlyList<string> Lines, string? Error);

/// <summary>Read-only pull request checks and failed CI logs, over gh.</summary>
public sealed partial class GitHubService(IGhRunner gh)
{
    public async Task<ChecksResult> ChecksAsync(string repoPath, int pr, CancellationToken ct = default)
    {
        // gh exits 1 for failing checks and 8 for pending ones, and still prints the JSON, so the output decides.
        var result = await gh.RunAsync(repoPath, ["pr", "checks", pr.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "name,state,bucket,link"], ct);
        var text = result.Output.Trim();
        if (!text.StartsWith('['))
        {
            return new ChecksResult([], string.IsNullOrWhiteSpace(text) ? Strings.Format("GhFailedSilently", result.ExitCode) : text);
        }

        var checks = new List<CheckStatus>();
        using var doc = JsonDocument.Parse(text);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            checks.Add(new CheckStatus(
                Text(item, "name") ?? string.Empty,
                Text(item, "state") ?? string.Empty,
                Text(item, "bucket") ?? string.Empty,
                Text(item, "link")));
        }

        return new ChecksResult(checks, null);
    }

    public async Task<CiFailure> CiFailureAsync(string repoPath, int pr, int maxLines = 40, CancellationToken ct = default)
    {
        var checks = await ChecksAsync(repoPath, pr, ct);
        if (checks.Error is not null)
        {
            return new CiFailure(null, null, [], checks.Error);
        }

        var failed = checks.Checks.FirstOrDefault(c => c.Bucket == "fail");
        if (failed is null)
        {
            return new CiFailure(null, null, [], Strings.Get("NoFailingCheck"));
        }

        var runId = failed.Link is null ? null : RunId().Match(failed.Link) is { Success: true } m ? m.Groups["id"].Value : null;
        if (runId is null)
        {
            return new CiFailure(failed.Name, null, [], Strings.Format("NoRunLink", failed.Name));
        }

        var log = await gh.RunAsync(repoPath, ["run", "view", runId, "--log-failed"], ct);
        var lines = Ansi().Replace(log.Output, string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        var errors = lines.Where(l => ErrorLine().IsMatch(l)).Take(maxLines).ToList();
        var picked = errors.Count > 0 ? errors : lines.TakeLast(maxLines).ToList();
        return new CiFailure(failed.Name, runId, picked, log.Ok || picked.Count > 0 ? null : Strings.Format("GhFailedSilently", log.ExitCode));
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"/actions/runs/(?<id>\d+)")]
    private static partial Regex RunId();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"error|##\[error\]|FAILED|\[FAIL\]", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();
}
