using AiDevHelpers.Mcp.Client;

namespace AiDevHelpers.Host;

/// <summary>
/// Asks the HTTP MCP whether it is well, on the configured port. A refused connection, a non-200 answer or a body that is
/// not a health answer is unhealthy.
/// </summary>
public sealed class HttpHealthProbe(int port, IHealthClient client) : IHealthProbe
{
    public HttpHealthProbe(int port, TimeSpan timeout)
        : this(port, new HttpHealthClient(timeout))
    {
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        (await client.CheckAsync(port, cancellationToken).ConfigureAwait(false)).Ok;
}
