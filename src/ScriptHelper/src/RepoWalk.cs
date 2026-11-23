namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Walks the files of a repo that is not trusted: it skips build and dependency folders, does not follow links or junctions, and
/// stops at <see cref="MaxDepth"/> levels, so a hostile or huge tree cannot make a scan loop or run long.
/// </summary>
internal static class RepoWalk
{
    /// <summary>The deepest folder level (the repo root is 0) that is searched.</summary>
    public const int MaxDepth = 12;

    private static readonly string[] SkippedFolders = [".git", ".vs", "bin", "obj", "node_modules", "packages", "dist"];

    public static IEnumerable<(string Dir, string File)> Files(string root)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            if (depth < MaxDepth)
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // A link or junction can loop back into the repo or leave it, so it is not followed.
                    if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)
                        && !new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push((sub, depth + 1));
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                yield return (dir, file);
            }
        }
    }
}
