using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>A repo with projects that need elevation and are not yet listed or declined. Projects are repo-relative.</summary>
public sealed record RepoElevationNotice(string RepoRoot, IReadOnlyList<string> Projects);

/// <summary>
/// The "repo opened with no .aidevtools folder" trigger. For each open repo that has no setup folder it runs the elevation scan
/// once per server session, and tells the client when projects need elevation. It only reads: it creates no folder, asks nothing
/// and never edits the elevated list; the person runs <c>elev repo-setup</c> to decide.
/// Scans run on their own tasks, so a slow scan never delays the next look at the open solutions.
/// </summary>
public sealed class RepoSetupChecker
{
    private readonly Func<IReadOnlyList<string>> _openSolutions;
    private readonly IElevScanner _scanner;
    private readonly Func<string, string?> _findRepoRoot;
    private readonly Func<string, bool> _hasSetupFolder;
    private readonly Action<RepoElevationNotice> _notify;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _scans = [];
    private readonly object _gate = new();

    public RepoSetupChecker(
        Func<IReadOnlyList<string>> openSolutions,
        IElevScanner scanner,
        Func<string, string?> findRepoRoot,
        Func<string, bool> hasSetupFolder,
        Action<RepoElevationNotice> notify)
    {
        _openSolutions = openSolutions;
        _scanner = scanner;
        _findRepoRoot = findRepoRoot;
        _hasSetupFolder = hasSetupFolder;
        _notify = notify;
    }

    /// <summary>Starts a scan for each open repo that has no setup folder and was not scanned this session. Returns at once.</summary>
    public void CheckOnce(CancellationToken ct)
    {
        foreach (var solution in _openSolutions())
        {
            if (_findRepoRoot(solution) is not { } root || _hasSetupFolder(root))
            {
                continue;
            }

            lock (_gate)
            {
                if (!_seen.Add(root))
                {
                    continue;
                }

                _scans.Add(Task.Run(() => ScanAsync(root, ct), CancellationToken.None));
            }
        }
    }

    /// <summary>Completes when every scan started so far has finished. For tests and shutdown.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_scans.ToArray());
        }
    }

    private async Task ScanAsync(string root, CancellationToken ct)
    {
        try
        {
            var result = await _scanner.ScanAsync(root, ct);
            if (result.ExitCode != 0)
            {
                return;
            }

            using var doc = JsonDocument.Parse(result.Output);
            var pending = doc.RootElement.GetProperty("pendingProjects").EnumerateArray()
                .Select(p => p.GetString() ?? string.Empty)
                .Where(p => p.Length > 0)
                .ToList();
            if (pending.Count > 0)
            {
                _notify(new RepoElevationNotice(root, pending));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or OperationCanceledException)
        {
            // A failed scan is not retried this session and must never take the server down; setup can still be run by hand.
        }
    }
}
