using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.SourceControlHelper;

/// <summary>One changed path. Index and WorkTree are the porcelain status letters (' ', 'M', 'A', 'D', 'R', '?', ...).</summary>
public sealed record GitChange(string Path, char Index, char WorkTree);

/// <summary>Branch, tracking state and changed paths from <c>git status --porcelain=v1 --branch</c>. Error is set when git failed.</summary>
public sealed record GitStatusResult(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitChange> Changes,
    string? Error);

/// <summary>Parses porcelain v1 status. Pure, so it is tested without git.</summary>
public static partial class GitStatusParser
{
    public static GitStatusResult Parse(string output)
    {
        string? branch = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var changes = new List<GitChange>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranch(line[3..], out branch, out upstream, out ahead, out behind);
                continue;
            }

            if (line.Length < 4)
            {
                continue;
            }

            var path = line[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            changes.Add(new GitChange(path, line[0], line[1]));
        }

        return new GitStatusResult(branch, upstream, ahead, behind, changes, null);
    }

    private static void ParseBranch(string text, out string? branch, out string? upstream, out int ahead, out int behind)
    {
        branch = null;
        upstream = null;
        ahead = 0;
        behind = 0;

        var tracking = Tracking().Match(text);
        if (tracking.Success)
        {
            text = text[..tracking.Index];
            ahead = tracking.Groups["ahead"].Success ? int.Parse(tracking.Groups["ahead"].Value) : 0;
            behind = tracking.Groups["behind"].Success ? int.Parse(tracking.Groups["behind"].Value) : 0;
        }

        var dots = text.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0)
        {
            branch = text[..dots];
            upstream = text[(dots + 3)..];
        }
        else
        {
            branch = text.StartsWith("No commits yet on ", StringComparison.Ordinal) ? text["No commits yet on ".Length..] : text;
        }
    }

    [GeneratedRegex(@" \[(?:ahead (?<ahead>\d+))?(?:, )?(?:behind (?<behind>\d+))?\]$")]
    private static partial Regex Tracking();
}
