using System.Collections.Concurrent;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Holds the toolset path each chat session resolved at its first tool call, so a session keeps one path for its lifetime.
/// Sessions are keyed by their MCP session id. The stdio host may have no session id, so all such calls share one process key.
/// The HTTP host serves many sessions in one process, which is why the lock is keyed per session and not held in a static field.
/// </summary>
public sealed class ToolsetSessionLock
{
    /// <summary>The key used when the host gives no session id.</summary>
    public const string ProcessKey = "<process>";

    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);

    /// <summary>
    /// The path locked for <paramref name="sessionId"/>. The first call runs <paramref name="resolve"/> and keeps its result;
    /// later calls return the locked path without running it again.
    /// </summary>
    public string Resolve(string? sessionId, Func<string> resolve)
    {
        var key = string.IsNullOrEmpty(sessionId) ? ProcessKey : sessionId;
        return _paths.GetOrAdd(key, _ => ToolsetPaths.Normalize(resolve()));
    }
}
