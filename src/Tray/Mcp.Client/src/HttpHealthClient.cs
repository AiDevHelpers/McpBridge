using System.Text.Json;

namespace AiDevHelpers.Mcp.Client;

/// <summary>What <c>/healthz</c> said.</summary>
/// <param name="Ok">True only when the server answered 200 with a health body.</param>
/// <param name="Version">The server version from the body.</param>
/// <param name="Pid">The pid the server reported, so a caller can compare it with the endpoint record.</param>
public sealed record HealthResult(bool Ok, string? Version = null, int? Pid = null);

/// <summary>Asks the HTTP MCP whether it is well.</summary>
public interface IHealthClient
{
    Task<HealthResult> CheckAsync(int port, CancellationToken cancellationToken);
}

/// <summary>Calls <c>GET /healthz</c> on the loopback port. Any failure is a not-ok result, never an exception.</summary>
public sealed class HttpHealthClient(TimeSpan timeout) : IHealthClient
{
    public async Task<HealthResult> CheckAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new HealthResult(false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "ok")
            {
                return new HealthResult(false);
            }

            return new HealthResult(true, root.GetProperty("version").GetString(), root.GetProperty("pid").GetInt32());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new HealthResult(false);
        }
    }
}
