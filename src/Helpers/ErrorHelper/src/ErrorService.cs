using AiDevHelpers.Helpers.LogHelper.Models;

namespace AiDevHelpers.Helpers.ErrorHelper;

/// <summary>
/// Holds the diagnostics from the latest build output and answers filtered queries, with in-memory snapshots
/// so a caller can ask only for what changed since a point in time.
/// </summary>
public sealed class ErrorService
{
    private readonly SuppressionSettings _suppression;
    private readonly Dictionary<string, HashSet<string>> _snapshots = new();
    private readonly object _gate = new();
    private IReadOnlyList<Diagnostic> _current = [];

    public ErrorService(SuppressionSettings suppression)
    {
        _suppression = suppression;
    }

    /// <summary>Replaces the current diagnostics with those parsed from build output.</summary>
    public void Ingest(string output)
    {
        var parsed = DiagnosticParser.Parse(output);
        lock (_gate)
        {
            _current = parsed;
        }
    }

    public IReadOnlyList<Diagnostic> GetErrors(DiagnosticFilter? filter = null) =>
        Visible(Current().Where(d => d.Severity == LogSeverity.Error), filter);

    public IReadOnlyList<Diagnostic> GetWarnings(DiagnosticFilter? filter = null) =>
        Visible(Current().Where(d => d.Severity == LogSeverity.Warning), filter);

    public IReadOnlyList<Diagnostic> GetNotices(DiagnosticFilter? filter = null) =>
        Visible(Current().Where(d => d.Severity == LogSeverity.Information), filter);

    /// <summary>Records the current diagnostics under a new id and returns only the id.</summary>
    public string TakeSnapshot()
    {
        var id = Guid.NewGuid().ToString("N");
        var keys = Current().Select(d => d.Key).ToHashSet();
        lock (_gate)
        {
            _snapshots[id] = keys;
        }

        return id;
    }

    /// <summary>Returns the current, visible diagnostics that were not present when the snapshot was taken.</summary>
    public IReadOnlyList<Diagnostic> GetChanges(string snapshotId, DiagnosticFilter? filter = null)
    {
        HashSet<string> known;
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(snapshotId, out var found))
            {
                throw new ArgumentException($"Unknown snapshot id '{snapshotId}'.", nameof(snapshotId));
            }

            known = found;
        }

        return Visible(Current().Where(d => !known.Contains(d.Key)), filter);
    }

    private IReadOnlyList<Diagnostic> Current()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    private IReadOnlyList<Diagnostic> Visible(IEnumerable<Diagnostic> items, DiagnosticFilter? filter)
    {
        var query = items.Where(d =>
            !_suppression.Codes.Contains(d.Code) && !_suppression.Sources.Contains(d.Source));

        if (filter?.Codes is { Count: > 0 } codes)
        {
            var wanted = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(d => wanted.Contains(d.Code));
        }

        if (filter?.Sources is { Count: > 0 } sources)
        {
            var wanted = sources.ToHashSet();
            query = query.Where(d => wanted.Contains(d.Source));
        }

        return query.ToList();
    }
}
