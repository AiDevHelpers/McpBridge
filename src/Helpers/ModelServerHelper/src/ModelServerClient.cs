using System.Text.Json;

namespace AiDevHelpers.Helpers.ModelServerHelper;

/// <summary>Router reachability and model state.</summary>
public sealed record ModelServerStatus(
    string Url, bool Running, string[] Models, string[] Loaded, string VramState, bool Healthy, string Message);

/// <summary>Capacity reported by the router, or the conservative fallback when it cannot be read.</summary>
public sealed record ModelServerCapacity(
    string Url, int? CpuThreads, int? GpuLayers, bool GpuDetected, int MaxConcurrentSummaryRequests, bool FromFallback);

/// <summary>
/// HTTP client for the SimpleModelServer router (<c>/api/tags</c>, <c>/api/ps</c>, <c>/api/capacity</c>).
/// A failed capacity call is never cached; a good one is cached for <see cref="CacheTtl" />.
/// </summary>
public sealed class ModelServerClient(HttpClient http, Uri baseUrl, TimeProvider clock, TimeSpan timeout)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private ModelServerCapacity? _cached;
    private DateTimeOffset _cachedAt;

    public static ModelServerClient CreateDefault(Uri? url = null) =>
        new(new HttpClient(), url ?? ModelServerAddress.Resolve(), TimeProvider.System, TimeSpan.FromSeconds(5));

    public async Task<ModelServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string[] models;
        try
        {
            using var doc = await GetJsonAsync("/api/tags", ct) ?? throw new JsonException();
            models = Names(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException)
        {
            return Down();
        }

        string[] loaded = [];
        try
        {
            using var ps = await GetJsonAsync("/api/ps", ct);
            if (ps is not null)
                loaded = Names(ps.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException)
        {
            // loaded stays empty
        }

        var url = baseUrl.ToString();
        if (models.Length == 0)
            return new ModelServerStatus(url, true, models, loaded, loaded.Length == 0 ? "idle" : "loaded", false, Strings.Get("NoModels"));

        var idle = loaded.Length == 0;
        var message = idle
            ? Strings.Format("HealthyIdle", models.Length)
            : Strings.Format("HealthyLoaded", loaded.Length, models.Length);
        return new ModelServerStatus(url, true, models, loaded, idle ? "idle" : "loaded", true, message);
    }

    public async Task<ModelServerCapacity> GetCapacityAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cached is not null && clock.GetUtcNow() - _cachedAt < CacheTtl)
            return _cached;

        try
        {
            using var doc = await GetJsonAsync("/api/capacity", ct);
            if (doc is null)
                return Fallback();

            var root = doc.RootElement;
            var gpuDetected = root.TryGetProperty("gpu", out var gpu) && gpu.ValueKind == JsonValueKind.Object &&
                              gpu.TryGetProperty("detected", out var d) && d.ValueKind == JsonValueKind.True;
            var result = new ModelServerCapacity(
                baseUrl.ToString(),
                Int(root, "cpuThreads"),
                Int(root, "gpuLayers"),
                gpuDetected,
                Math.Max(1, Int(root, "maxConcurrentSummaryRequests") ?? 1),
                false);
            _cached = result;
            _cachedAt = clock.GetUtcNow();
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException)
        {
            return Fallback();
        }
    }

    private ModelServerStatus Down() =>
        new(baseUrl.ToString(), false, [], [], "unknown", false, Strings.Format("NotReachable", baseUrl));

    private ModelServerCapacity Fallback() => new(baseUrl.ToString(), null, null, false, 1, true);

    // Returns null on a non-success status. Throws TimeoutException when the per-call timeout passes;
    // the caller's own cancellation propagates as OperationCanceledException.
    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync(new Uri(baseUrl, path), cts.Token).WaitAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static string[] Names(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray()
            : [];

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
