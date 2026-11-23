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

/// <summary>
/// What GitHub says protects the named branches. Checked is false when gh could not answer for any branch (missing, signed out,
/// no access): that is "not checked", never "no protections". Error names the first failure.
/// </summary>
public sealed record LiveResult(bool Checked, IReadOnlyList<string> ProtectedBranches, IReadOnlyList<string> RequiredChecks, string? Error);

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

    /// <summary>
    /// Reads the branch protection and the active rulesets for each branch name (no wildcards). A branch counts as protected when
    /// either says it is. Read only.
    /// </summary>
    public async Task<LiveResult> LivePolicyAsync(string repoPath, IReadOnlyList<string> branches, CancellationToken ct = default)
    {
        var protectedBranches = new List<string>();
        var checks = new List<string>();
        var answered = false;
        string? error = null;

        foreach (var branch in branches.Where(b => !string.IsNullOrWhiteSpace(b) && !b.Contains('*')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Uri.EscapeDataString(branch);
            var isProtected = false;

            var protection = await gh.RunAsync(repoPath, ["api", $"repos/{{owner}}/{{repo}}/branches/{name}/protection"], ct);
            if (TryJson(protection.Output, out var protectionDoc))
            {
                using (protectionDoc)
                {
                    if (protectionDoc.RootElement.ValueKind == JsonValueKind.Object && protectionDoc.RootElement.TryGetProperty("url", out _))
                    {
                        answered = true;
                        isProtected = true;
                        checks.AddRange(ProtectionChecks(protectionDoc.RootElement));
                    }
                    else if (IsNotProtected(protectionDoc.RootElement))
                    {
                        answered = true;
                    }
                    else
                    {
                        error ??= protection.Output.Trim();
                    }
                }
            }
            else
            {
                error ??= string.IsNullOrWhiteSpace(protection.Output) ? Strings.Format("GhFailedSilently", protection.ExitCode) : protection.Output.Trim();
            }

            var rules = await gh.RunAsync(repoPath, ["api", $"repos/{{owner}}/{{repo}}/rules/branches/{name}"], ct);
            if (TryJson(rules.Output, out var rulesDoc))
            {
                using (rulesDoc)
                {
                    if (rulesDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        answered = true;
                        foreach (var rule in rulesDoc.RootElement.EnumerateArray())
                        {
                            var type = Text(rule, "type");
                            if (type is "pull_request" or "required_status_checks" or "deletion" or "non_fast_forward" or "update")
                                isProtected = true;

                            if (type == "required_status_checks")
                                checks.AddRange(RulesetChecks(rule));
                        }
                    }
                    else
                    {
                        error ??= rules.Output.Trim();
                    }
                }
            }
            else
            {
                error ??= string.IsNullOrWhiteSpace(rules.Output) ? Strings.Format("GhFailedSilently", rules.ExitCode) : rules.Output.Trim();
            }

            if (isProtected)
                protectedBranches.Add(branch);
        }

        return new LiveResult(answered, protectedBranches, checks.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), answered ? null : error);
    }

    private static bool TryJson(string text, out JsonDocument doc)
    {
        var trimmed = text.Trim();
        try
        {
            doc = JsonDocument.Parse(trimmed.StartsWith('{') || trimmed.StartsWith('[') ? trimmed : "null");
            return doc.RootElement.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException)
        {
            doc = JsonDocument.Parse("null");
            return false;
        }
    }

    // gh prints the API's 404 body ("Branch not protected") and exits 1.
    private static bool IsNotProtected(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && Text(root, "message") is { } message && message.Contains("not protected", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ProtectionChecks(JsonElement root)
    {
        if (!root.TryGetProperty("required_status_checks", out var required) || required.ValueKind != JsonValueKind.Object)
            yield break;

        if (required.TryGetProperty("contexts", out var contexts) && contexts.ValueKind == JsonValueKind.Array)
        {
            foreach (var context in contexts.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String))
                yield return context.GetString()!;
        }

        if (required.TryGetProperty("checks", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in list.EnumerateArray().Select(c => Text(c, "context")).OfType<string>())
                yield return check;
        }
    }

    private static IEnumerable<string> RulesetChecks(JsonElement rule)
    {
        if (!rule.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("required_status_checks", out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var check in list.EnumerateArray().Select(c => Text(c, "context")).OfType<string>())
            yield return check;
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
