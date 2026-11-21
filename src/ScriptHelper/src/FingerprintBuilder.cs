using System.Text;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Files lists every file that went into the fingerprint, as normalized relative paths.</summary>
public sealed record FingerprintResult(bool Ok, string Fingerprint, string Reason, IReadOnlyList<string> Files);

/// <summary>
/// Builds the fingerprint approval pins: the script, every dot-sourced or imported file it reaches
/// (transitively), and the interpreter. Approval and the broker both call this, so they agree.
/// Fails closed on anything it cannot pin: escapes, reparse points, missing files, unresolved paths.
/// </summary>
public static class FingerprintBuilder
{
    private const int MaxFiles = 64;
    private const string InterpreterPart = "interpreter";

    public static FingerprintResult Build(
        string root,
        string relativeScript,
        IInterpreterInfo interpreter,
        IPathInspector inspector)
    {
        var parts = new List<FingerprintPart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(relativeScript);

        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();

            var resolved = PathRules.ResolveInsideRoot(root, rel);
            if (!resolved.Ok)
                return Fail($"'{rel}': {resolved.Reason}");

            var key = ApprovalStore.NormalizeScriptPath(rel);
            if (key is null)
                return Fail($"'{rel}' is not a valid relative path");

            if (!seen.Add(key))
                continue;

            if (seen.Count > MaxFiles)
                return Fail($"too many dependencies (more than {MaxFiles} files)");

            if (HasReparsePoint(root, resolved.FullPath, inspector))
                return Fail($"'{key}' passes through a reparse point (junction or symlink)");

            if (!File.Exists(resolved.FullPath))
                return Fail($"'{key}' does not exist");

            var bytes = File.ReadAllBytes(resolved.FullPath);
            parts.Add(new FingerprintPart(key, bytes));

            var scan = DependencyScanner.Scan(Encoding.UTF8.GetString(bytes));
            if (scan.Unresolved.Count > 0)
                return Fail($"'{key}' has an unresolved dependency: {scan.Unresolved[0]}");

            var directory = Path.GetDirectoryName(key.Replace('/', '\\')) ?? string.Empty;
            foreach (var dependency in scan.Paths)
            {
                queue.Enqueue(Path.Combine(directory, dependency));
            }
        }

        var files = parts.Select(p => p.Name).ToList();
        parts.Add(new FingerprintPart(InterpreterPart, Encoding.UTF8.GetBytes(interpreter.Describe())));

        return new FingerprintResult(true, Fingerprint.Compute(parts), "fingerprint built", files);
    }

    private static bool HasReparsePoint(string root, string fullPath, IPathInspector inspector)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        if (inspector.IsReparsePoint(rootFull))
            return true;

        var relative = fullPath[rootFull.Length..].TrimStart('\\');
        var current = rootFull;
        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (inspector.IsReparsePoint(current))
                return true;
        }

        return false;
    }

    private static FingerprintResult Fail(string reason) =>
        new(false, string.Empty, reason, Array.Empty<string>());
}
