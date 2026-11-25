namespace AiDevHelpers.Common;

/// <summary>Whether to launch an IDE elevated, and why, in words for the person.</summary>
public sealed record LaunchDecision(bool Elevated, string Reason);

/// <summary>
/// The one rule for launching an IDE elevated. It never elevates on a guess: only when this user's mode is <c>always</c>, or the solution
/// contains a project that the repo's committed list says needs elevation. A user mode of <c>never</c> wins over the list. The decision only
/// asks for an elevated launch; the person still answers the UAC prompt.
/// </summary>
public static class ElevationPolicy
{
    /// <summary>Decides for a solution file, reading the repo's files from disk.</summary>
    public static LaunchDecision Decide(string solutionPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        var root = RepoSettingsFile.FindRepoRootFromFolder(folder);
        return root is null ? new LaunchDecision(false, "the solution is not in a git repo") : Decide(solutionPath, root, ElevationFiles.Read(root), SolutionProjects.Read);
    }

    public static LaunchDecision Decide(string solutionPath, string repoRoot, RepoElevation elevation, Func<string, IReadOnlyList<string>> projectsOf)
    {
        switch (elevation.Mode)
        {
            case ElevationMode.Never:
                return new LaunchDecision(false, "this user's mode for the repo is 'never'");
            case ElevationMode.Always:
                return new LaunchDecision(true, "this user's mode for the repo is 'always'");
        }

        if (elevation.Projects.Count == 0)
            return new LaunchDecision(false, "no project in the repo is listed as needing elevation");

        var listed = elevation.Projects.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var solutionFolder = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var matched = projectsOf(solutionPath)
            .Select(p => Path.GetRelativePath(repoRoot, Path.GetFullPath(Path.Combine(solutionFolder, p.Replace('/', Path.DirectorySeparatorChar)))).Replace('\\', '/'))
            .Where(listed.Contains)
            .ToList();

        return matched.Count > 0
            ? new LaunchDecision(true, "the solution has a project that needs elevation: " + string.Join(", ", matched))
            : new LaunchDecision(false, "no project in this solution is listed as needing elevation");
    }
}
