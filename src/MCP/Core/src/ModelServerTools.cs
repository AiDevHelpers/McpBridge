using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Reads the SimpleModelServer router's status and capacity over HTTP, and generates or chats with the models a model type routes to.
/// A model type is resolved from the <c>ModelServer:ModelTypes</c> settings; a <c>chat</c> type hands the request back to the calling chat.
/// </summary>
[McpServerToolType]
internal sealed class ModelServerTools(IOptions<ModelServerOptions> options)
{
    private static readonly Lazy<ModelServerClient> Shared = new(() => ModelServerClient.CreateDefault());

    /// <summary>Test seam: replaces the shared client.</summary>
    internal static Func<ModelServerClient>? ClientFactory { get; set; }

    private static ModelServerClient Client() => ClientFactory?.Invoke() ?? Shared.Value;

    private static readonly Lazy<ModelServerGenerator> SharedGenerator = new(() => ModelServerGenerator.CreateDefault());

    /// <summary>Test seam: replaces the shared generator.</summary>
    internal static Func<ModelServerGenerator>? GeneratorFactory { get; set; }

    private static ModelServerGenerator Generator() => GeneratorFactory?.Invoke() ?? SharedGenerator.Value;

    [McpServerTool]
    [SafeTool]
    [Description("SimpleModelServer router status: whether it is reachable, the models it lists, which are loaded in VRAM, and whether it is healthy. An unreachable router is ok with running=false.")]
    public async Task<string> ModelServerStatus(CancellationToken ct = default)
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
    public async Task<string> ModelServerCapacity(
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

    [McpServerTool]
    [SafeTool]
    [Description("Generate text (non-streaming). Give either modelType (a name from the ModelServer:ModelTypes settings) or model (a model name the router lists), not both. A router type runs on the router; a chat type returns handoff=true with the model and a prompt that asks chat to use it. The reply is capped at 20,000 characters.")]
    public async Task<string> ModelGenerate(
        [Description("The prompt")] string prompt,
        [Description("Model type from the settings. Give this or model.")] string? modelType = null,
        [Description("Model name the router lists. Give this or modelType.")] string? model = null,
        [Description("Optional system text")] string? system = null,
        [Description("Timeout in seconds, 1-600 (default 120)")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        try
        {
            var s = Select(model, modelType);
            if (!s.Ok)
                return Json(new { ok = false, error = s.Error });
            if (s.Handoff)
                return Json(new { ok = true, handoff = true, modelType = s.ModelType, model = s.Model, prompt = Strings.Format("HandoffPrompt", s.Model, prompt) });

            var r = await Generator().GenerateAsync(s.Model!, prompt, system, timeoutSeconds, ct);
            return Json(new { ok = r.Ok, modelType = s.ModelType, model = s.Model, response = r.Text, capped = r.Capped, timedOut = r.TimedOut, status = r.Status, error = r.Error });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Chat (non-streaming). Give either modelType (from the ModelServer:ModelTypes settings) or model, not both. Messages take role system, user or assistant. A chat type returns handoff=true with the model and the messages, for chat to answer. The reply is capped at 20,000 characters.")]
    public async Task<string> ModelChat(
        [Description("Messages in order, each with role (system, user or assistant) and content")] ChatTurn[] messages,
        [Description("Model type from the settings. Give this or model.")] string? modelType = null,
        [Description("Model name the router lists. Give this or modelType.")] string? model = null,
        [Description("Timeout in seconds, 1-600 (default 120)")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        try
        {
            var s = Select(model, modelType);
            if (!s.Ok)
                return Json(new { ok = false, error = s.Error });
            if (s.Handoff)
                return Json(new { ok = true, handoff = true, modelType = s.ModelType, model = s.Model, messages });

            var r = await Generator().ChatAsync(s.Model!, messages, timeoutSeconds, ct);
            return Json(new { ok = r.Ok, modelType = s.ModelType, model = s.Model, content = r.Text, capped = r.Capped, timedOut = r.TimedOut, status = r.Status, error = r.Error });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Clean a prompt (trim, remove control characters, 20,000 character limit), send it with earlier history in one chat call. Give either modelType or model, not both. The 20 most recent history entries are sent; historyTrimmed counts the rest. A chat type returns handoff=true with the model and the cleaned prompt instead.")]
    public async Task<string> CleanAndChat(
        [Description("The prompt to clean and send")] string prompt,
        [Description("Model type from the settings. Give this or model.")] string? modelType = null,
        [Description("Model name the router lists. Give this or modelType.")] string? model = null,
        [Description("Earlier messages, oldest first, each with role (system, user or assistant) and content")] ChatTurn[]? history = null,
        [Description("Timeout in seconds, 1-600 (default 120)")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        try
        {
            var s = Select(model, modelType);
            if (!s.Ok)
                return Json(new { ok = false, error = s.Error });
            if (s.Handoff)
                return Json(new { ok = true, handoff = true, modelType = s.ModelType, model = s.Model, prompt = Strings.Format("HandoffPrompt", s.Model, PromptCleaner.Clean(prompt)) });

            var r = await Generator().CleanAndChatAsync(s.Model!, prompt, history ?? [], timeoutSeconds, ct);
            return Json(new { ok = r.Reply.Ok, modelType = s.ModelType, model = s.Model, content = r.Reply.Text, capped = r.Reply.Capped, timedOut = r.Reply.TimedOut, status = r.Reply.Status, error = r.Reply.Error, historyTrimmed = r.HistoryTrimmed });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Resolve a model type from the ModelServer:ModelTypes settings: the target (router or chat) and the model. Does not call the router. An unknown type or one with a bad target returns ok=false.")]
    public string ModelRoute(
        [Description("Model type name, as the settings define it")] string modelType)
    {
        try
        {
            var r = ModelRouter.Resolve(modelType, options.Value.ToRoutes());
            return Json(new { ok = r.Ok, target = r.Target, model = r.Model, error = r.Error });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private sealed record Selection(bool Ok, string? Model, string? ModelType, bool Handoff, string? Error);

    /// <summary>Picks the model for a call: a literal model name, or the model a model type routes to. A chat target hands off.</summary>
    private Selection Select(string? model, string? modelType)
    {
        var hasModel = !string.IsNullOrWhiteSpace(model);
        var hasType = !string.IsNullOrWhiteSpace(modelType);
        if (hasModel == hasType)
            return new Selection(false, null, null, false, Strings.Get("ModelSelectorInvalid"));
        if (hasModel)
            return new Selection(true, model!.Trim(), null, false, null);

        var r = ModelRouter.Resolve(modelType!, options.Value.ToRoutes());
        if (!r.Ok)
            return new Selection(false, null, modelType, false, r.Error);
        return new Selection(true, r.Model, modelType, r.Target == ModelRouter.ChatTarget, null);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
