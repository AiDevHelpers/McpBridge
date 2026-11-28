using System.Text.Json;
using System.Text.RegularExpressions;
using AiDevHelpers.Common;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>One file the plan creates: its project, the file path (inside the repo) and the item template to create it from.</summary>
internal sealed record CodeWorkStep(string Id, string Project, string File, string Template, bool Difficult = false);

/// <summary>
/// A drafted file content waiting for the calling AI's check. The token must be sent back to write it.
/// Content is null for a chat hand-off: the calling chat writes the content and passes it to confirm.
/// </summary>
internal sealed record CodeWorkDraft(string Token, string? Content);

/// <summary>A code_work session as stored under <c>.aidevhelpers/code-work/&lt;id&gt;.json</c>.</summary>
internal sealed class CodeWorkSession
{
    public string Id { get; set; } = "";

    public string Prompt { get; set; } = "";

    public List<CodeWorkStep> Steps { get; set; } = [];

    public Dictionary<string, CodeWorkDraft> Drafts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Reads and writes code_work sessions under the repo, so they survive a server restart.</summary>
internal static class CodeWorkStore
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static bool ValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    public static string Folder(string root) => Path.Combine(root, ".aidevhelpers", "code-work");

    /// <summary>Returns the error text for an invalid plan, or null when the plan can be saved.</summary>
    public static string? ValidatePlan(IReadOnlyList<CodeWorkStep>? steps, string root)
    {
        if (steps is null || steps.Count == 0)
            return Strings.Get("CodeWorkPlanEmpty");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!ValidId(step.Id))
                return Strings.Get("CodeWorkBadStepId");
            if (!seen.Add(step.Id))
                return Strings.Format("CodeWorkDuplicateStep", step.Id);
            if (string.IsNullOrWhiteSpace(step.Project) || string.IsNullOrWhiteSpace(step.Template))
                return Strings.Format("CodeWorkStepIncomplete", step.Id);

            var check = WorkspacePathGuard.Resolve(step.File, root);
            if (!check.Ok)
                return check.Error;
        }

        return null;
    }

    public static void Save(string root, CodeWorkSession session)
    {
        Directory.CreateDirectory(Folder(root));
        File.WriteAllText(Path.Combine(Folder(root), session.Id + ".json"), JsonSerializer.Serialize(session, JsonOptions));
    }

    public static CodeWorkSession? Load(string root, string id)
    {
        var path = Path.Combine(Folder(root), id + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<CodeWorkSession>(File.ReadAllText(path), JsonOptions) : null;
    }
}
