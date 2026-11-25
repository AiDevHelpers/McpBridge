using System.Text;
using System.Text.RegularExpressions;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Folder globs for allow-folder. '/' and '\' mean the same, and case is ignored. '**' matches any run of characters, separators
/// included, so <c>F:\**\**</c> covers every path under drive F:. '*' matches inside one folder name, and '?' one character.
/// </summary>
public static class FolderGlob
{
    /// <summary>True when the glob starts with a drive letter and a root separator, such as <c>F:\</c>.</summary>
    public static bool IsAbsolute(string glob)
    {
        var g = Normalize(glob);
        return g.Length >= 3 && char.IsAsciiLetter(g[0]) && g[1] == ':' && g[2] == '\\';
    }

    /// <summary>True when the whole full path matches the glob.</summary>
    public static bool IsMatch(string glob, string fullPath) =>
        Regex.IsMatch(Normalize(fullPath), ToRegex(Normalize(glob)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Normalize(string path) => path.Trim().Replace('/', '\\');

    private static string ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                sb.Append(@"[^\\]*");
            }
            else if (c == '?')
            {
                sb.Append(@"[^\\]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return sb.Append(@"\z").ToString();
    }
}
