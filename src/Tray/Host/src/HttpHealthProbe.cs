using AiDevHelpers.Mcp.Client;

namespace AiDevHelpers.Host;

/// <summary>
/// Asks the HTTP MCP whether it is well, using the shared endpoint reader and health client. The port comes from the
/// endpoint record the server wrote; a missing or invalid record, a record with an error, a refused connection or any
/// status except 200 is unhealthy.
/// </summary>
public sealed class HttpHealthProbe(string tokenPath, string endpointPath, IHealthClient client) : IHealthProbe
{
    public HttpHealthProbe(string tokenPath, string endpointPath, TimeSpan timeout)
        : this(tokenPath, endpointPath, new HttpHealthClient(timeout))
    {
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var read = McpEndpointFiles.Read(endpointPath, tokenPath);
        if (read.Kind != EndpointReadKind.Found || read.Endpoint!.Error is not null)
        {
            return false;
        }

        return (await client.CheckAsync(read.Endpoint.Port, read.Endpoint.Token, cancellationToken).ConfigureAwait(false)).Ok;
    }
}
