namespace AiDevHelpers.Helpers.FileEditHelper;

/// <summary>Outcome of <see cref="WorkspacePathGuard.Resolve" />.</summary>
public sealed record PathCheck(bool Ok, string FullPath, string? Error)
{
    internal static PathCheck Pass(string fullPath) => new(true, fullPath, null);

    internal static PathCheck Fail(string error) => new(false, "", error);
}

/// <summary>
/// Confines a caller-supplied path to a workspace root. The path is made absolute, symbolic links and junctions
/// are followed, and the real location must be inside the root (or one of the extra roots).
/// </summary>
public static class WorkspacePathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="path" /> (absolute, or relative to <paramref name="workspaceRoot" />) and checks it lies
    /// under the root or one of <paramref name="extraRoots" />. On success <see cref="PathCheck.FullPath" /> is the
    /// normalized path (links not substituted); otherwise <see cref="PathCheck.Error" /> says why.
    /// </summary>
    public static PathCheck Resolve(string? path, string? workspaceRoot, params string[] extraRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PathCheck.Fail(Strings.Get("PathRequired"));

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return PathCheck.Fail(Strings.Get("NoWorkspaceRoot"));

        string normalized;
        string realRoot;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            normalized = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            realRoot = RealPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return PathCheck.Fail(Strings.Format("InvalidPath", ex.Message));
        }

        var real = RealPath(normalized);
        if (IsUnder(real, realRoot))
            return PathCheck.Pass(normalized);

        foreach (var extra in extraRoots)
        {
            if (!string.IsNullOrWhiteSpace(extra) &&
                IsUnder(real, RealPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(extra)))))
                return PathCheck.Pass(normalized);
        }

        return PathCheck.Fail(Strings.Format("PathOutsideRoot", path, workspaceRoot));
    }

    /// <summary>True when <paramref name="path" /> is <paramref name="root" /> itself or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(trimmedRoot, PathComparison))
            return true;

        return path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, PathComparison) ||
               path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    // Replaces every existing symbolic link or junction by its final target; missing segments are kept.
    private static string RealPath(string fullPath)
    {
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
            return fullPath;

        var current = pathRoot;
        var segments = fullPath[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                next = target is null ? next : Path.GetFullPath(target.FullName);
            }

            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
