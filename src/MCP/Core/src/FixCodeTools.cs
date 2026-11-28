using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiDevHelpers.Bridge;
using AiDevHelpers.Common;
using AiDevHelpers.Helpers.FileEditHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// fix_code: automated fixes. enable-analysis writes a managed block into the repo's .editorconfig through file_edit (backed up, idempotent).
/// format asks the Visual Studio bridge to format the document the file is open as. The other operations refuse and name the existing tool.
/// </summary>
[McpServerToolType]
internal sealed class FixCodeTools
{
    internal const string BlockBegin = "# BEGIN AiDevHelpers managed analysis";
    internal const string BlockEnd = "# END AiDevHelpers managed analysis";

    internal static readonly string Block =
        BlockBegin + "\n[*.cs]\ndotnet_diagnostic.IDE0005.severity = warning\n" + BlockEnd + "\n";

    private static readonly Regex ExistingBlock = new(
        Regex.Escape(BlockBegin) + ".*?" + Regex.Escape(BlockEnd) + "\\r?\\n?",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly FileEditService Edits = new(new FileEditEngine());

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Test seam: replaces the bridge call (action, arguments, token) → reply JSON.</summary>
    internal static Func<string, object, CancellationToken, Task<string>>? IdeCall { get; set; }

    [McpServerTool]
    [DangerousTool]
    [Description("Apply an automated fix in the repo. operation: enable-analysis (writes the managed IDE0005 block into the repo's .editorconfig; needs an existing .editorconfig; plan unless confirm), or format (formats one file through the open Visual Studio instance, which formats the document it has active; needs path and confirm). fix-syntax, fix-imports and fix-namespace are not built here: they name the existing tool to use.")]
    public static async Task<string> FixCode(
        [Description("enable-analysis, or format")] string operation,
        [Description("Repo folder")] string root,
        [Description("For format: the file, inside the repo")] string? path = null,
        [Description("Set true to apply. Default false returns the plan only.")] bool confirm = false,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root))
                return Refuse(Strings.Get("FixCodeRootRequired"));

            var op = (operation ?? "").Trim().ToLowerInvariant();
            return op switch
            {
                "enable-analysis" => await EnableAnalysisAsync(root, confirm, ct),
                "format" => await FormatAsync(root, path, confirm, ct),
                "fix-syntax" => Refuse(Strings.Format("FixCodeUseOtherTool", op, "ide_code_cleanup")),
                "fix-imports" => Refuse(Strings.Format("FixCodeUseOtherTool", op, "ide_code_cleanup")),
                "fix-namespace" => Refuse(Strings.Format("FixCodeUseOtherTool", op, "ide_rename_namespace")),
                _ => Refuse(Strings.Format("FixCodeUnknownOperation", operation ?? "")),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(ex.Message);
        }
    }

    private static async Task<string> EnableAnalysisAsync(string root, bool confirm, CancellationToken ct)
    {
        var config = Path.Combine(root, ".editorconfig");
        if (!File.Exists(config))
            return Refuse(Strings.Get("FixCodeNoEditorConfig"));

        var current = File.ReadAllText(config);
        if (current.Length == 0)
            return Refuse(Strings.Get("FixCodeEditorConfigEmpty"));

        var updated = ExistingBlock.IsMatch(current)
            ? ExistingBlock.Replace(current, Block, 1)
            : (current.EndsWith('\n') ? current : current + "\n") + Block;

        if (!confirm)
            return Json(new { ok = true, written = false, path = config, block = Block });

        var result = await Edits.ExecuteAsync(
            new FileEditRequest("apply", FilePath: ".editorconfig", OldText: current, NewText: updated), root, ct);
        return Json(new { ok = result.Ok, written = result.Ok, path = config, error = result.Error, backupPath = result.BackupPath });
    }

    private static async Task<string> FormatAsync(string root, string? path, bool confirm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Refuse(Strings.Get("FixCodeFileRequired"));

        var target = WorkspacePathGuard.Resolve(path, root);
        if (!target.Ok)
            return Refuse(target.Error!);

        if (!confirm)
            return Json(new { ok = true, written = false, file = target.FullPath, note = Strings.Get("FixCodeFormatPlan") });

        var reply = IdeCall is not null
            ? await IdeCall("formatDocument", new { file = target.FullPath }, ct)
            : await IdeInstanceRouter.CallAsync("formatDocument", new { file = target.FullPath }, null, ct);
        using var doc = JsonDocument.Parse(reply);
        var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
        return Json(new { ok, written = ok, file = target.FullPath, reply = JsonSerializer.Deserialize<JsonElement>(reply, JsonOptions) });
    }

    private static string Refuse(string error) => Json(new { ok = false, error });

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
