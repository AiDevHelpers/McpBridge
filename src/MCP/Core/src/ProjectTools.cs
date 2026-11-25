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

    [McpServerTool]
    [Description("Plan the edits that bring projects to .aidevhelpers/project-standards.json: set properties, add missing analyzers and the test SDK, align differing package versions, retarget refused frameworks. Writes nothing; returns the actions and a token for project_fix_confirm. skip lists action ids to leave out (Kind:project:name).")]
    public static string ProjectFixPlan(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Optional repo-relative project path to plan for one project")] string? project = null,
        [Description("Action ids to leave out, e.g. SetProperty:src/A/A.csproj:Nullable")] string[]? skip = null)
    {
        var plan = AiDevHelpers.Helpers.ProjectHelper.ProjectFixer.Plan(root, project, skip ?? []);
        return Json(new
        {
            ok = true,
            token = plan.Token,
            actions = plan.Actions.Select(a => new { a.Id, a.Project, kind = a.Kind.ToString(), a.Name, a.Value, a.Description }),
            notes = plan.Notes,
        });
    }

    [McpServerTool]
    [Description("Apply a plan from project_fix_plan. Re-checks the plan, applies the edits, re-audits, and builds each changed project with -warnaserror. Any failure restores every file to its original bytes. Tokens are single use and expire after 15 minutes.")]
    public static async Task<string> ProjectFixConfirm(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Token returned by project_fix_plan")] string token,
        CancellationToken ct = default)
    {
        var result = await AiDevHelpers.Helpers.ProjectHelper.ProjectFixer.ConfirmAsync(root, token, CommandSeam.Default, ct);
        return Json(new { ok = result.Ok, error = result.Error, changed = result.Changed, buildOutput = result.BuildOutput });
    }

    [McpServerTool]
    [Description("Plan adding a .NET project from a dotnet new template to a .slnx solution, set up to the standards (properties, analyzers, test SDK for *.Tests). Framework defaults to the standard and is refused when another framework has no exception. folder is optional when the solution's layout gives a default. Writes nothing; returns a token for project_add_confirm.")]
    public static async Task<string> ProjectAddPlan(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Solution file, repo-relative, e.g. AiDevHelpers.slnx")] string solution,
        [Description("Template short name from project_types_list, e.g. classlib")] string template,
        [Description("Project name, e.g. Foo.Bar")] string name,
        [Description("Repo-relative project folder; optional when the layout gives a default")] string? folder = null,
        [Description("Solution folder, e.g. /Helpers/Foo/; omit to add at the top level")] string? solutionFolder = null,
        [Description("Language: C#, F# or VB")] string language = "C#",
        [Description("Target framework; defaults to the standard base framework")] string? framework = null,
        [Description("Extra package ids to add at the repo's pinned version")] string[]? packages = null,
        CancellationToken ct = default)
    {
        var request = new AiDevHelpers.Helpers.ProjectHelper.AddRequest(root, solution, template, name, folder, solutionFolder, language, framework, packages ?? []);
        var plan = await AiDevHelpers.Helpers.ProjectHelper.ProjectAdder.PlanAsync(request, CommandSeam.Default, ct);
        return Json(new
        {
            ok = plan.Errors.Count == 0,
            token = plan.Token,
            projectPath = plan.ProjectPath,
            framework = plan.Framework,
            errors = plan.Errors,
            notes = plan.Notes,
            edits = plan.Edits.Select(a => new { a.Id, a.Description }),
            stubs = plan.Stubs,
        });
    }

    [McpServerTool]
    [Description("Apply a plan from project_add_plan: creates the project with dotnet new, removes template stubs, applies the edits, lists the project in the solution, and builds it with -warnaserror. A failed build removes what was created and restores the solution. Tokens are single use and expire after 15 minutes.")]
    public static async Task<string> ProjectAddConfirm(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Token returned by project_add_plan")] string token,
        CancellationToken ct = default)
    {
        var result = await AiDevHelpers.Helpers.ProjectHelper.ProjectAdder.ConfirmAsync(root, token, CommandSeam.Default, ct);
        return Json(new { ok = result.Ok, error = result.Error, changed = result.Changed, buildOutput = result.BuildOutput });
    }

    [McpServerTool]
    [Description("Plan a test project beside a source project: <Source>.Tests, referencing the source, with the repo's test runner packages, the test SDK and the standards. folder defaults to the solution's test layout. seedTest adds one failing test. Writes nothing; returns a token for project_add_test_confirm.")]
    public static async Task<string> ProjectAddTestPlan(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Solution file, repo-relative, e.g. AiDevHelpers.slnx")] string solution,
        [Description("Source project path, repo-relative, e.g. src/Core/Core.csproj")] string sourceProject,
        [Description("Repo-relative test folder; optional when the layout gives a default")] string? folder = null,
        [Description("Solution folder, e.g. /Core/; omit to add at the top level")] string? solutionFolder = null,
        [Description("Add one failing test so the project starts red")] bool seedTest = false,
        CancellationToken ct = default)
    {
        var plan = await AiDevHelpers.Helpers.ProjectHelper.TestProjectAdder.PlanAsync(root, solution, sourceProject, folder, solutionFolder, seedTest, CommandSeam.Default, ct);
        return Json(new
        {
            ok = plan.Errors.Count == 0,
            token = plan.Token,
            projectPath = plan.ProjectPath,
            framework = plan.Framework,
            errors = plan.Errors,
            notes = plan.Notes,
            edits = plan.Edits.Select(a => new { a.Id, a.Description }),
            stubs = plan.Stubs,
        });
    }

    [McpServerTool]
    [Description("Apply a plan from project_add_test_plan. Same steps and rollback as project_add_confirm.")]
    public static async Task<string> ProjectAddTestConfirm(
        [Description("Repo root that holds .aidevhelpers/project-standards.json")] string root,
        [Description("Token returned by project_add_test_plan")] string token,
        CancellationToken ct = default)
    {
        var result = await AiDevHelpers.Helpers.ProjectHelper.ProjectAdder.ConfirmAsync(root, token, CommandSeam.Default, ct);
        return Json(new { ok = result.Ok, error = result.Error, changed = result.Changed, buildOutput = result.BuildOutput });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
