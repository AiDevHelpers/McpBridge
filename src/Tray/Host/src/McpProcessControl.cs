using AiDevHelpers.Updates;

namespace AiDevHelpers.Host;

/// <summary>The updater's view of MCP.Http: hold it down, wait for it to stop, release it. Backed by the supervisor's <see cref="McpHold"/>.</summary>
public sealed class McpProcessControl(McpHold hold) : IMcpProcessControl
{
    public void Hold() => hold.Hold();

    public void Release() => hold.Release();

    public Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken ct) => hold.WaitUntilStoppedAsync(timeout, ct);
}
