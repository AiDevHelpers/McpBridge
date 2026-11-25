using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>A repo whose allowlist has files this user has not approved. Unapproved lists their repo-relative paths.</summary>
public sealed record RepoVerifyNotice(string RepoRoot, IReadOnlyList<string> Unapproved);

/// <summary>
/// Looks at the open IDE solutions. Each time a repo shows up that was not open on the previous look, it asks the broker whether
/// every file in the repo's allowlist has this user's approval and raises a notice for the files that do not. A repo is checked
/// once per time it is opened. Repos outside git, and repos without an <c>.elevated.json</c> allowlist, are skipped.
/// </summary>
public sealed class RepoOpenChecker
{
    private const int SomeUnapproved = 1;

    private readonly Func<IReadOnlyList<string>> _openSolutions;
    private readonly IElevVerifier _verifier;
    private readonly Func<string, string?> _findRepoRoot;
    private readonly Func<string, bool> _hasAllowlist;
    private readonly Action<RepoVerifyNotice> _notify;
    private HashSet<string> _openRepos = new(StringComparer.OrdinalIgnoreCase);

    public RepoOpenChecker(
        Func<IReadOnlyList<string>> openSolutions,
        IElevVerifier verifier,
        Func<string, string?> findRepoRoot,
        Func<string, bool> hasAllowlist,
        Action<RepoVerifyNotice> notify)
    {
        _openSolutions = openSolutions;
        _verifier = verifier;
        _findRepoRoot = findRepoRoot;
        _hasAllowlist = hasAllowlist;
        _notify = notify;
    }

    public async Task CheckOnceAsync(CancellationToken ct)
    {
        var nowOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in _openSolutions())
        {
            if (_findRepoRoot(solution) is { } root && _hasAllowlist(root))
            {
                nowOpen.Add(root);
            }
        }

        var opened = nowOpen.Where(r => !_openRepos.Contains(r)).ToList();
        _openRepos = nowOpen;

        foreach (var root in opened)
        {
            await CheckRepoAsync(root, ct);
        }
    }

    private async Task CheckRepoAsync(string root, CancellationToken ct)
    {
        ElevRunResult result;
        try
        {
            result = await _verifier.VerifyAsync(root, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        if (result.ExitCode != SomeUnapproved)
        {
            // All approved, or the broker did not answer (nothing to say; it may not be running yet).
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var unapproved = doc.RootElement.GetProperty("scripts").EnumerateArray()
                .Where(s => s.GetProperty("status").GetString() != "Approved")
                .Select(s => s.GetProperty("path").GetString() ?? string.Empty)
                .ToList();
            if (unapproved.Count > 0)
            {
                _notify(new RepoVerifyNotice(root, unapproved));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Output that is not the verify report: nothing to tell.
        }
    }
}
