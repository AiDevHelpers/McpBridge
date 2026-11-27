using System.Collections.Concurrent;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>The repo root and toolset path a session locked at its first call that found a repo.</summary>
public sealed record ToolsetLock(string RepoRoot, string Path);

/// <summary>
/// Holds the repo and toolset path each chat session locked at its first tool call that found a repo, so a session keeps one path for its lifetime.
/// Sessions are keyed by their MCP session id. The stdio host may have no session id, so all such calls share one process key.
/// The HTTP host serves many sessions in one process, which is why the lock is keyed per session and not held in a static field.
/// </summary>
public sealed class ToolsetSessionLock
{
    /// <summary>The key used when the host gives no session id.</summary>
    public const string ProcessKey = "<process>";

    private readonly ConcurrentDictionary<string, ToolsetLock> _locks = new(StringComparer.Ordinal);

    /// <summary>The lock for a session, or null when none has been taken yet.</summary>
    public ToolsetLock? Find(string? sessionId) =>
        _locks.TryGetValue(Key(sessionId), out var locked) ? locked : null;

    /// <summary>
    /// Locks the session to <paramref name="repoRoot"/>. The first call runs <paramref name="resolve"/> for the path and keeps both;
    /// later calls return the existing lock without running it again.
    /// </summary>
    public ToolsetLock Lock(string? sessionId, string repoRoot, Func<string> resolve) =>
        _locks.GetOrAdd(Key(sessionId), _ => new ToolsetLock(repoRoot, ToolsetPaths.Normalize(resolve())));

    private static string Key(string? sessionId) => string.IsNullOrEmpty(sessionId) ? ProcessKey : sessionId;
}
