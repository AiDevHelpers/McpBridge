using System.Net.Http.Headers;
using System.Text.Json;

namespace AiDevHelpers.Host;

/// <summary>
/// Calls <c>/healthz</c> on the HTTP MCP with its bearer token. The port comes from the endpoint record the server
/// wrote; a missing record, a record with an error, a refused connection or any status except 200 is unhealthy.
/// </summary>
public sealed class HttpHealthProbe(string tokenPath, string endpointPath, TimeSpan timeout) : IHealthProbe
{
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = (await File.ReadAllTextAsync(tokenPath, cancellationToken).ConfigureAwait(false)).Trim();
            using var record = JsonDocument.Parse(await File.ReadAllTextAsync(endpointPath, cancellationToken).ConfigureAwait(false));
            var root = record.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                return false;
            }

            var port = root.GetProperty("port").GetInt32();
            using var client = new HttpClient { Timeout = timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/healthz");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }
}
