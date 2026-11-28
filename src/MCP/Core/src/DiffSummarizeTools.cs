using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AiDevHelpers.Helpers.ModelServerHelper;
using AiDevHelpers.Helpers.SourceControlHelper;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// Summarizes a git diff. Per-file stats and totals come from git; a model summary is added when a model type or model is given.
/// The model is picked the same way as <c>model_generate</c>, so a chat type hands the diff back to the calling chat.
/// </summary>
[McpServerToolType]
internal sealed class DiffSummarizeTools(IOptions<ModelServerOptions> options)
{
    /// <summary>Largest diff text sent to a model, in characters. A longer diff is cut and flagged.</summary>
    internal const int MaxDiffChars = 15000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Lazy<SourceControlService> SharedService = new(() => new SourceControlService(new ProcessGitRunner()));

    /// <summary>Test seam: replaces the shared git service.</summary>
    internal static Func<SourceControlService>? ServiceFactory { get; set; }

    private static SourceControlService Service() => ServiceFactory?.Invoke() ?? SharedService.Value;

    private static readonly Lazy<ModelServerGenerator> SharedGenerator = new(() => ModelServerGenerator.CreateDefault());

    /// <summary>Test seam: replaces the shared generator.</summary>
    internal static Func<ModelServerGenerator>? GeneratorFactory { get; set; }

    private static ModelServerGenerator Generator() => GeneratorFactory?.Invoke() ?? SharedGenerator.Value;

    [McpServerTool]
    [SafeTool]
    [Description("Summarize a git diff: per-file added and deleted lines, totals, and a model summary when modelType or model is given. Give neither for stats only. A router type or model runs on the router; a chat type returns handoff=true with the diff prompt for chat. Diffs over 15,000 characters are cut and diffTruncated is true. path must stay inside the repo.")]
    public async Task<string> DiffSummarize(
        [Description("Repo folder")] string repoPath,
        [Description("true for staged changes, false for unstaged")] bool staged = false,
        [Description("Optional: one path inside the repo")] string? path = null,
        [Description("Model type from the settings. Give this or model, or neither for stats only.")] string? modelType = null,
        [Description("Model name the router lists. Give this or modelType, or neither for stats only.")] string? model = null,
        [Description("Timeout in seconds, 1-600 (default 120)")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        try
        {
            var hasModel = !string.IsNullOrWhiteSpace(model);
            var hasType = !string.IsNullOrWhiteSpace(modelType);
            if (hasModel && hasType)
                return Reply(new SummarizeReply(false, Error: Strings.Get("ModelSelectorInvalid")));

            ModelServerTools.ModelSelection? selection = null;
            if (hasModel || hasType)
            {
                selection = ModelServerTools.Select(options.Value, model, modelType);
                if (!selection.Ok)
                    return Reply(new SummarizeReply(false, Error: selection.Error));
            }

            var summary = await Service().SummarizeDiffAsync(repoPath, staged, path, ct);
            if (!summary.Ok)
                return Reply(new SummarizeReply(false, Error: summary.Error));

            var diff = summary.RawDiff.Length > MaxDiffChars ? summary.RawDiff[..MaxDiffChars] : summary.RawDiff;
            var truncated = diff.Length < summary.RawDiff.Length;
            var prompt = BuildPrompt(summary.Files, diff, truncated);
            var stats = new SummarizeReply(
                Ok: true,
                Files: summary.Files,
                Added: summary.Added,
                Deleted: summary.Deleted,
                RawChars: summary.RawChars,
                DiffTruncated: truncated);

            if (selection is null)
                return Reply(stats with { Note = Strings.Get("DiffStatsOnlyNote") });

            if (selection.Handoff)
                return Reply(stats with
                {
                    Handoff = true,
                    ModelType = selection.ModelType,
                    Model = selection.Model,
                    Prompt = Strings.Format("HandoffPrompt", selection.Model, prompt),
                });

            var r = await Generator().GenerateAsync(selection.Model!, prompt, null, timeoutSeconds, ct);
            return Reply(stats with
            {
                Ok = r.Ok,
                ModelType = selection.ModelType,
                Model = selection.Model,
                Summary = r.Text,
                Error = r.Error,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Reply(new SummarizeReply(false, Error: ex.Message));
        }
    }

    private static string BuildPrompt(IReadOnlyList<DiffFileStat> files, string diff, bool truncated)
    {
        var sb = new StringBuilder(Strings.Get("DiffPromptHeader")).Append("\n\n");
        foreach (var f in files)
            sb.Append(f.Path).Append(": +").Append(f.Added).Append(" -").Append(f.Deleted).Append(f.Binary ? " (binary)" : "").Append('\n');
        sb.Append('\n').Append(diff);
        if (truncated)
            sb.Append('\n').Append(Strings.Get("DiffTruncatedMarker"));
        return sb.ToString();
    }

    private static string Reply(SummarizeReply reply) => JsonSerializer.Serialize(reply, JsonOptions);

    private sealed record SummarizeReply(
        bool Ok,
        string? Error = null,
        IReadOnlyList<DiffFileStat>? Files = null,
        int Added = 0,
        int Deleted = 0,
        int RawChars = 0,
        bool DiffTruncated = false,
        bool? Handoff = null,
        string? ModelType = null,
        string? Model = null,
        string? Prompt = null,
        string? Summary = null,
        string? Note = null);
}
