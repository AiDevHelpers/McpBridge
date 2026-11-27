using System.Collections.Concurrent;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// The MCP sessions of this server process, so a state change can be pushed to every client and not only the last
/// one that called a tool. A session whose send fails (the client is gone) is dropped.
/// </summary>
internal sealed class SessionSet<T> where T : class
{
    private readonly ConcurrentDictionary<string, T> _sessions = new();

    public int Count => _sessions.Count;

    /// <summary>Remembers a session, replacing an earlier one with the same key.</summary>
    public void Set(string key, T session) => _sessions[key] = session;

    public bool Remove(string key) => _sessions.TryRemove(key, out _);

    /// <summary>
    /// Sends to every session. A failure on one session drops that session and never stops the others;
    /// cancellation is not a failure and propagates.
    /// </summary>
    public async Task BroadcastAsync(Func<T, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        foreach (var (key, session) in _sessions.ToArray())
        {
            try
            {
                await send(session).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                _ = _sessions.TryRemove(new KeyValuePair<string, T>(key, session));
            }
        }
    }
}
