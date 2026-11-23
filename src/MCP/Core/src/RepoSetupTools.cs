using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// MCP surface for repo setup. It reads only: it never creates the <c>.aidevtools</c> files and never applies a decision. The person
/// decides in the console with <c>elev repo-setup</c>, so the agent cannot list a project or change a mode on their behalf.
/// </summary>
[McpServerToolType]
internal sealed class RepoSetupTools(IElevScanner scanner)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool]
    [Description(
        "Repo setup, read only. action 'scan': find projects and launch scripts in the repo that need administrator rights (app.manifest " +
        "requireAdministrator, elevation markers in the scripts a launch or debug configuration runs) and say which are not yet listed or " +
        "declined. action 'status': the elevated list, this user's mode (listed, always, never) and what is still pending. repoPath is any " +
        "folder inside the repo. Nothing is changed: ask the user, then have them run 'elev repo-setup <repo>'.")]
    public async Task<string> RepoSetup(string repoPath, string action = "status", CancellationToken ct = default)
    {
        var root = ResolveRepo(repoPath);
        if (root is null)
        {
            return Strings.Get("RepoSetupNeedsRepo");
        }

        if (action is not ("scan" or "status"))
        {
            return Strings.Get("RepoSetupUnknownAction");
        }

        try
        {
            var scan = await scanner.ScanAsync(root, ct);
            if (scan.ExitCode != 0)
            {
                return Strings.Format("RepoSetupScanFailed", scan.Output.Trim());
            }

            if (action == "scan")
            {
                return scan.Output.TrimEnd() + "\n" + Strings.Format("RepoSetupNextStep", root);
            }

            using var doc = JsonDocument.Parse(scan.Output);
            var elevation = doc.RootElement.GetProperty("elevation");
            return JsonSerializer.Serialize(
                new
                {
                    repoRoot = root,
                    mode = elevation.GetProperty("mode").GetString(),
                    projects = elevation.GetProperty("projects").Clone(),
                    declined = elevation.GetProperty("declined").Clone(),
                    pendingProjects = doc.RootElement.GetProperty("pendingProjects").Clone(),
                    warnings = elevation.GetProperty("warnings").Clone(),
                },
                Json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception or JsonException or KeyNotFoundException)
        {
            return Strings.Format("RepoSetupScanFailed", ex.Message);
        }
    }

    // The repo that holds the folder: the nearest folder at or above it with a .git folder or file.
    private static string? ResolveRepo(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return null;
        }

        return RepoSettings.FindRepoRoot(Path.Combine(repoPath, "_"));
    }
}
