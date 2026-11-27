namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// Finds the repo a session is working in. An explicit <c>root</c> or <c>solutionPath</c> argument wins; otherwise the
/// process's current directory is used. The result is the nearest folder at or above that path with a <c>.git</c> folder or file.
/// </summary>
public static class ToolsetRepoDiscovery
{
    /// <summary>The <c>root</c> or <c>solutionPath</c> string argument of a call, or null when the call has neither.</summary>
    public static string? ExplicitRepoArgument(IEnumerable<KeyValuePair<string, System.Text.Json.JsonElement>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        foreach (var (name, value) in arguments)
        {
            if ((name.Equals("root", StringComparison.OrdinalIgnoreCase) || name.Equals("solutionPath", StringComparison.OrdinalIgnoreCase))
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>The repo root for a call, or null when the path is not inside a git repo.</summary>
    public static string? RepoRootFor(string? explicitRoot, string currentDirectory)
    {
        var start = string.IsNullOrWhiteSpace(explicitRoot) ? currentDirectory : explicitRoot;
        return AiDevHelpers.Common.RepoSettingsFile.FindRepoRootFromFolder(start);
    }

    /// <summary>
    /// The session's lock: the existing one, or a new one when this call finds a repo. The path is the repo's setting
    /// (its <c>toolsetPath</c>, or the server default). Returns null, and locks nothing, while no repo is found.
    /// </summary>
    public static ToolsetLock? LockFor(
        ToolsetSessionLock locks, string? sessionId, IdeToolsOptions options, string? explicitRoot, string currentDirectory)
    {
        var existing = locks.Find(sessionId);
        if (existing is not null)
        {
            return existing;
        }

        var repoRoot = RepoRootFor(explicitRoot, currentDirectory);
        if (repoRoot is null)
        {
            return null;
        }

        // RepoSettings.Resolve takes a solution path and looks for the repo above its folder, so a probe file inside the repo root stands in.
        var probe = Path.Combine(repoRoot, "probe.sln");
        return locks.Lock(sessionId, repoRoot, () => RepoSettings.Resolve(options, probe).Options.ToolsetPath);
    }
}
