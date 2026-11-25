using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.ProjectHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Project templates, project audit, and the add and fix tools for .NET projects. Writes are two-step and need approval.</summary>
[McpServerToolType]
internal sealed class ProjectTools
{
    [McpServerTool]
    [Description("List the .NET project templates the SDK offers (dotnet new list --type project). Optional language (C#, F#, VB) and tag filters.")]
    public static async Task<string> ProjectTypesList(
        [Description("Optional language: C#, F# or VB")] string? language = null,
        [Description("Optional tag, matched inside the tag cell, e.g. Test Project")] string? tag = null,
        CancellationToken ct = default)
    {
        var request = new CommandRequest
        {
            FileName = "dotnet",
            Arguments = ["new", "list", "--type", "project"],
            Timeout = TimeSpan.FromMinutes(2),
        };

        var run = await CommandSeam.Default(request, ct);
        if (!run.Launched || run.ExitCode != 0)
        {
            return Json(new { ok = false, error = run.LaunchError ?? (string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr).Trim() });
        }

        var parsed = TemplateList.Parse(run.StdOut);
        if (!parsed.Ok)
        {
            return Json(new { ok = false, error = parsed.Error });
        }

        var items = TemplateList.Filter(parsed.Items, language, tag);
        return Json(new
        {
            ok = true,
            count = items.Count,
            items = items.Select(t => new { t.Name, t.ShortName, t.Languages, t.Tags }),
        });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
