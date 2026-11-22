namespace AiDevHelpers.ScriptHelper.Core;

public sealed record PathResolution(bool Ok, string FullPath, string Reason);

public static class PathRules
{
    /// <summary>
    /// Resolves a relative script path against a project root and rejects anything
    /// that is absolute or ends up outside the root. String-only; reparse-point
    /// checks happen in the broker, where the file system is touched.
    /// </summary>
    public static PathResolution ResolveInsideRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Fail("empty path");

        var normalized = relativePath.Replace('/', '\\');

        if (Path.IsPathRooted(normalized))
            return Fail("absolute paths are not allowed");

        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + '\\';
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return Fail("path escapes the project root");

        return new PathResolution(true, full, "inside project root");
    }

    private static PathResolution Fail(string reason) => new(false, string.Empty, reason);
}
