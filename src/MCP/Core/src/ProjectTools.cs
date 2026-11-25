using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Common;
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

    [McpServerTool]
    [Description("Audit the repo's .NET projects against .aidevhelpers/project-standards.json: framework (with exceptions), required properties, required analyzers, test SDK, and package versions pinned differently. Read only. ok is false when any error is found.")]
    public static string ProjectAudit(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Optional repo-relative project path to audit one project")] string? project = null)
    {
        var standards = ProjectStandardsFile.Read(root);
        var report = AiDevHelpers.Helpers.ProjectHelper.ProjectAudit.Run(root, standards.Standards, project);
        return Json(new
        {
            ok = report.Ok,
            warnings = standards.Warnings,
            findings = report.Findings.Select(f => new { f.Project, f.Severity, f.Check, f.Message }),
        });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
