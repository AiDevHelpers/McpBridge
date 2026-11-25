using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDevHelpers.Common;
using AiDevHelpers.Helpers.SourceControlHelper;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// MCP surface for repo setup. It reads only: it never creates the <c>.aidevhelpers</c> files and never applies a decision. The person
/// decides in the console with <c>elev repo-setup</c>, so the agent cannot list a project, record a rule or change a mode on their
/// behalf. The one thing it may write is the cached live GitHub policy, and only into a repo that already has an <c>.aidevhelpers</c> folder.
/// </summary>
[McpServerToolType]
internal sealed class RepoSetupTools(IElevScanner scanner, GitHubService gitHub)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool]
    [Description(
        "Repo setup, read only. action 'scan': find projects and launch scripts in the repo that need administrator rights (app.manifest " +
        "requireAdministrator, elevation markers in the scripts a launch or debug configuration runs), the repo's CI branches and checks, " +
        "instruction files, hooks and CODEOWNERS, and what GitHub says protects the branches (live, through gh; 'checked: false' means gh " +
        "could not answer, not that nothing is protected). action 'status': the elevated list, this user's mode (listed, always, never), " +
        "the recorded rules and what is still pending. repoPath is any folder inside the repo. Nothing is changed: ask the user, then have " +
        "them run 'elev repo-setup <repo>'.")]
    public async Task<string> RepoSetup(string repoPath, string action = "status", string decisions = "", CancellationToken ct = default)
    {
        var root = ResolveRepo(repoPath);
        if (root is null)
        {
            return Strings.Get("RepoSetupNeedsRepo");
        }

        if (action is not ("scan" or "status" or "apply"))
        {
            return Strings.Get("RepoSetupUnknownAction");
        }

        // Only the person can choose; this action records a choice the AI has been told by them, after a yes in chat. elev validates every
        // name against the scan and writes nothing if one is not pending.
        if (action == "apply")
        {
            if (string.IsNullOrWhiteSpace(decisions))
            {
                return Strings.Get("RepoSetupNeedsDecisions");
            }

            var applied = await scanner.ApplyAsync(root, decisions, ct);
            return applied.Output.Trim() + (applied.ExitCode == 0 ? string.Empty : "\n" + Strings.Get("RepoSetupNothingChanged"));
        }

        try
        {
            var scan = await scanner.ScanAsync(root, ct);
            if (scan.ExitCode != 0)
            {
                return Strings.Format("RepoSetupScanFailed", scan.Output.Trim());
            }

            var node = JsonNode.Parse(scan.Output)!.AsObject();
            var live = await RefreshLiveAsync(root, node, ct);

            if (action == "scan")
            {
                node["live"] = live;
                return node.ToJsonString(Json) + "\n" + Strings.Format("RepoSetupNextStep", root);
            }

            var elevation = node["elevation"]!;
            var recorded = node["recordedRules"]!;
            return new JsonObject
            {
                ["repoRoot"] = root,
                ["mode"] = elevation["mode"]?.DeepClone(),
                ["projects"] = elevation["projects"]?.DeepClone(),
                ["declined"] = elevation["declined"]?.DeepClone(),
                ["pendingProjects"] = node["pendingProjects"]?.DeepClone(),
                ["recordedRules"] = recorded.DeepClone(),
                ["pendingProtectedBranches"] = node["pendingProtectedBranches"]?.DeepClone(),
                ["pendingRequiredChecks"] = node["pendingRequiredChecks"]?.DeepClone(),
                ["live"] = live,
                ["warnings"] = elevation["warnings"]?.DeepClone(),
            }.ToJsonString(Json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception or JsonException or NullReferenceException)
        {
            return Strings.Format("RepoSetupScanFailed", ex.Message);
        }
    }

    // Asks GitHub about the branches the repo's own files name. A repo with an .aidevhelpers folder keeps the answer for the git tools.
    private async Task<JsonNode> RefreshLiveAsync(string root, JsonObject scan, CancellationToken ct)
    {
        var branches = new List<string>();
        branches.AddRange(StringList(scan["recordedRules"]?["protectedBranches"]));
        branches.AddRange(StringList(scan["rules"]?["candidateProtectedBranches"]));
        branches.AddRange(StringList(scan["rules"]?["defaultBranch"]));
        branches.AddRange(StringList(scan["recordedRules"]?["defaultBranch"]));

        var live = await gitHub.LivePolicyAsync(root, branches, ct);
        if (live.Checked && Directory.Exists(Path.Combine(root, RepoSettingsFile.FolderName)))
        {
            RepoRulesFile.WriteLive(root, new LivePolicy(live.ProtectedBranches, live.RequiredChecks, DateTime.UtcNow));
        }

        return JsonSerializer.SerializeToNode(new { live.Checked, live.ProtectedBranches, live.RequiredChecks, live.Error }, Json)!;
    }

    private static IEnumerable<string> StringList(JsonNode? node) => node switch
    {
        JsonArray array => array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>(),
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        _ => [],
    };

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
