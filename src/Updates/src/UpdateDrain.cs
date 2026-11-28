namespace AiDevHelpers.Updates;

/// <summary>Stops the things an update must not run alongside (unit 10b). Returns true when it is safe to apply.</summary>
public interface IUpdateDrain
{
    Task<bool> DrainAsync(CancellationToken ct);
}

/// <summary>Controls MCP.Http for the update: hold it down, wait for it to stop, and release it. The Host implements this over its hold gate.</summary>
public interface IMcpProcessControl
{
    /// <summary>Stops MCP.Http and keeps it down until <see cref="Release"/>.</summary>
    void Hold();

    /// <summary>Lets MCP.Http start again.</summary>
    void Release();

    /// <summary>Completes true when MCP.Http is no longer running, or false when the timeout passes first.</summary>
    Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>Drain for a build with nothing to stop.</summary>
public sealed class NoOpUpdateDrain : IUpdateDrain
{
    public Task<bool> DrainAsync(CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// Holds MCP.Http and waits for it to stop. A kill, not a graceful wait: requests in flight are cut off, which the release notes must say.
/// The caller releases the hold after the apply, or after a rollback.
/// </summary>
public sealed class McpUpdateDrain(IMcpProcessControl control, TimeSpan timeout) : IUpdateDrain
{
    public async Task<bool> DrainAsync(CancellationToken ct)
    {
        control.Hold();
        return await control.WaitUntilStoppedAsync(timeout, ct);
    }
}
