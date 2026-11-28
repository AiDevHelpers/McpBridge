namespace AiDevHelpers.Helpers.SourceControlHelper;

/// <summary>One file in a diff. Binary files have no line counts (git prints "-").</summary>
public sealed record DiffFileStat(string Path, int Added, int Deleted, bool Binary);

/// <summary>Per-file stats and totals for a diff, plus the raw diff text. Error is set when git could not run.</summary>
public sealed record DiffSummary(
    bool Ok,
    string? Error,
    IReadOnlyList<DiffFileStat> Files,
    int Added,
    int Deleted,
    string RawDiff)
{
    public int RawChars => RawDiff.Length;

    public static DiffSummary Failed(string error) => new(false, error, [], 0, 0, "");
}

/// <summary>Parses <c>git diff --numstat</c> output: one <c>added⇥deleted⇥path</c> line per file.</summary>
public static class DiffStatParser
{
    public static IReadOnlyList<DiffFileStat> Parse(string numstat)
    {
        var files = new List<DiffFileStat>();
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            var binary = parts[0] == "-" || parts[1] == "-";
            var added = binary ? 0 : int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var deleted = binary ? 0 : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            files.Add(new DiffFileStat(parts[2], added, deleted, binary));
        }

        return files;
    }
}
