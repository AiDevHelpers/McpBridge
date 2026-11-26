using System.Linq;
using System.Text.Json.Nodes;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Adds a "check" to the instance list: is the solution open, and is an instance open on the expected branch and worktree.
/// With no expectation given, the expected branch and worktree are the ones the solution's own files are on. Pure over the
/// list JSON, so it is tested without VS.
/// </summary>
internal static class InstanceCheck
{
    public static string Apply(string listJson, string? solutionPath, string? expectedBranch, string? expectedWorktree)
    {
        var root = JsonNode.Parse(listJson)!.AsObject();
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return root.ToJsonString();
        }

        var branch = expectedBranch ?? GitWorktree.BranchFor(solutionPath);
        var worktree = expectedWorktree ?? GitWorktree.RootFor(solutionPath);
        var instances = root["instances"]?.AsArray() ?? [];

        var sameSolution = instances.Where(i => PathCompare.Same(Text(i, "solution_path"), solutionPath)).ToList();
        var matching = sameSolution
            .Where(i => (branch is null || Text(i, "git_branch") == branch)
                        && (worktree is null || PathCompare.Same(Text(i, "git_worktree"), worktree)))
            .Select(i => (int)i!["pid"]!)
            .ToList();

        root["check"] = new JsonObject
        {
            ["solution_path"] = solutionPath,
            ["solution_open"] = sameSolution.Count > 0,
            ["expected_branch"] = branch,
            ["expected_worktree"] = worktree,
            ["matching_instance_pids"] = new JsonArray(matching.Select(pid => (JsonNode)pid).ToArray()),
            ["ok"] = matching.Count > 0,
        };
        return root.ToJsonString();
    }

    private static string? Text(JsonNode? node, string key) => node?[key]?.GetValue<string?>();
}
