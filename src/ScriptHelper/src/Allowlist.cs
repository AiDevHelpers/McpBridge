using System.Text;
using System.Text.RegularExpressions;

namespace AiDevHelpers.ScriptHelper.Core;

public static class Allowlist
{
    // Characters a '*' must never match: quotes, shell metacharacters, line breaks.
    private const string ShellMeta = "\"'`;|&$<>()\r\n";

    public static Decision Evaluate(
        IEnumerable<AllowlistEntry> entries,
        string relativePath,
        IReadOnlyList<string> args)
    {
        var path = relativePath.Replace('\\', '/');
        var pathMatched = false;

        foreach (var entry in entries)
        {
            if (!GlobMatch(entry.Path.Replace('\\', '/'), path, excludeSlash: true))
                continue;

            pathMatched = true;

            if (ArgsMatch(entry.Args, args))
                return new Decision(true, $"allowed by entry '{entry.Path}'");
        }

        return pathMatched
            ? new Decision(false, "arguments do not match any allowed entry")
            : new Decision(false, "script is not in the allowlist");
    }

    /// <summary>True when the path matches any entry, ignoring arguments. Used at approval time.</summary>
    public static bool IsPathListed(IEnumerable<AllowlistEntry> entries, string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return entries.Any(e => GlobMatch(e.Path.Replace('\\', '/'), path, excludeSlash: true));
    }

    private static bool ArgsMatch(IReadOnlyList<string>? patterns, IReadOnlyList<string> args)
    {
        if (patterns is null)
            return args.Count == 0;

        if (patterns.Count != args.Count)
            return false;

        for (var i = 0; i < patterns.Count; i++)
        {
            if (!GlobMatch(patterns[i], args[i], excludeSlash: false))
                return false;
        }

        return true;
    }

    private static bool GlobMatch(string pattern, string value, bool excludeSlash)
    {
        var excluded = excludeSlash ? ShellMeta + "/" : ShellMeta;
        var sb = new StringBuilder("^");

        foreach (var c in pattern)
        {
            if (c == '*')
                sb.Append("[^").Append(Regex.Escape(excluded)).Append("]*");
            else
                sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append('$');
        return Regex.IsMatch(value, sb.ToString(), RegexOptions.CultureInvariant);
    }
}
