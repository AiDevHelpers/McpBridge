using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Reads the SimpleModelServer router's status and capacity over HTTP.</summary>
[McpServerToolType]
internal sealed class ModelServerTools
{
    private static readonly Lazy<ModelServerClient> Shared = new(() => ModelServerClient.CreateDefault());

    /// <summary>Test seam: replaces the shared client.</summary>
    internal static Func<ModelServerClient>? ClientFactory { get; set; }

    private static ModelServerClient Client() => ClientFactory?.Invoke() ?? Shared.Value;

    [McpServerTool]
    [SafeTool]
    [Description("SimpleModelServer router status: whether it is reachable, the models it lists, which are loaded in VRAM, and whether it is healthy. An unreachable router is ok with running=false.")]
    public static async Task<string> ModelServerStatus(CancellationToken ct = default)
    {
        try
        {
            var s = await Client().GetStatusAsync(ct);
            return Json(new { ok = true, url = s.Url, running = s.Running, models = s.Models, loaded = s.Loaded, vramState = s.VramState, healthy = s.Healthy, message = s.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("SimpleModelServer router capacity: CPU threads, GPU layers, whether a GPU is detected, and how many summary requests may run at once (at least 1). fromFallback is true when the router could not be read. refresh=true skips the 60 second cache.")]
    public static async Task<string> ModelServerCapacity(
        [Description("Skip the 60 second cache")] bool refresh = false,
        CancellationToken ct = default)
    {
        try
        {
            var c = await Client().GetCapacityAsync(refresh, ct);
            return Json(new { ok = true, url = c.Url, cpuThreads = c.CpuThreads, gpuLayers = c.GpuLayers, gpuDetected = c.GpuDetected, maxConcurrentSummaryRequests = c.MaxConcurrentSummaryRequests, fromFallback = c.FromFallback });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static readonly Lazy<ModelServerGenerator> SharedGenerator = new(() => ModelServerGenerator.CreateDefault());

    /// <summary>Test seam: replaces the shared generator.</summary>
    internal static Func<ModelServerGenerator>? GeneratorFactory { get; set; }

    private static ModelServerGenerator Generator() => GeneratorFactory?.Invoke() ?? SharedGenerator.Value;

    [McpServerTool]
    [SafeTool]
    [Description("Generate text with a model on the SimpleModelServer router (non-streaming). The reply is capped at 20,000 characters; capped=true means it was cut. Refused calls return ok=false with the reason.")]
    public static async Task<string> ModelGenerate(
        [Description("Model name as the router lists it")] string model,
        [Description("The prompt")] string prompt,
        [Description("Optional system text")] string? system = null,
        [Description("Timeout in seconds, 1-600 (default 120)")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        try
        {
            var r = await Generator().GenerateAsync(model, prompt, system, timeoutSeconds, ct);
            return Json(new { ok = r.Ok, response = r.Text, capped = r.Capped, timedOut = r.TimedOut, status = r.Status, error = r.Error });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Chat with a model on the SimpleModelServer router (non-streaming). Messages take role system, user or assistant. The reply is capped at 20,000 characters.")]
    public static async Task<string> ModelChat(
        [Description("Model name as the router lists it")] string model,
        [Description("Messages in order, each with role (system, user or assistant) and content")] ChatTurn[] messages,
        [Description("Timeout in seconds, 1-600 (default 120)")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        try
        {
            var r = await Generator().ChatAsync(model, messages, timeoutSeconds, ct);
            return Json(new { ok = r.Ok, content = r.Text, capped = r.Capped, timedOut = r.TimedOut, status = r.Status, error = r.Error });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
