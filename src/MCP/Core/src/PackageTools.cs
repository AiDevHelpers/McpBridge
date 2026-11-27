using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.PackageHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Outdated NuGet packages with the update rule applied. Reads and reports only; nothing is edited.</summary>
[McpServerToolType]
internal sealed class PackageTools
{
    [McpServerTool]
    [SafeTool]
    [Description("List the outdated NuGet packages of the solution at a folder, with the version to move to. A package that versions with .NET " +
                 "(System.*, Microsoft.Extensions.* and the like) is never moved past the .NET major of its project, so a net10.0 project does not " +
                 "get an 11.x package; chosen is null when nothing newer is allowed. Restore first. Edits nothing.")]
    public static async Task<string> PackageOutdated(
        [Description("Folder that holds the .slnx or .sln")] string root,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
        {
            return Json(new { ok = false, error = Strings.Format("FolderNotFound", root) });
        }

        var solution = Directory.EnumerateFiles(root, "*.slnx").Concat(Directory.EnumerateFiles(root, "*.sln")).FirstOrDefault();
        if (solution is null)
        {
            return Json(new { ok = false, error = Strings.Format("NoSolutionIn", root) });
        }

        try
        {
            var updates = await new OutdatedPlanner(new ProcessPackageRunner()).PlanAsync(root, Path.GetFileName(solution), ct);
            return Json(new
            {
                ok = true,
                count = updates.Count,
                updates = updates.Select(u => new { project = u.Project, package = u.Package, current = u.Current, latest = u.Latest, chosen = u.Chosen, note = u.Note }),
            });
        }
        catch (InvalidOperationException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("Restore the NuGet packages of the solution at a folder (dotnet restore). Use it before a build, a format check or " +
                 "PackageOutdated when packages may be missing. Edits no project file. Returns ok, the exit code and the tail of the output.")]
    public static async Task<string> PackageRestore(
        [Description("Folder that holds the .slnx or .sln")] string root,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
        {
            return Json(new { ok = false, error = Strings.Format("FolderNotFound", root) });
        }

        var solution = Directory.EnumerateFiles(root, "*.slnx").Concat(Directory.EnumerateFiles(root, "*.sln")).FirstOrDefault();
        if (solution is null)
        {
            return Json(new { ok = false, error = Strings.Format("NoSolutionIn", root) });
        }

        try
        {
            var result = await new PackageRestorer(new ProcessPackageRunner()).RestoreAsync(root, Path.GetFileName(solution), ct);
            return Json(new { ok = result.Ok, exit_code = result.ExitCode, output = result.Output });
        }
        catch (InvalidOperationException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
