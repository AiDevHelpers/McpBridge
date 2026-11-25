namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>One project template from <c>dotnet new list</c>. <see cref="Tags"/> holds the CLI's tag cell as one entry.</summary>
public sealed record TemplateInfo(string Name, string ShortName, IReadOnlyList<string> Languages, IReadOnlyList<string> Tags);

/// <summary>The parsed template list. When the header is missing, <see cref="Ok"/> is false and <see cref="Error"/> says why.</summary>
public sealed record TemplateListResult(bool Ok, IReadOnlyList<TemplateInfo> Items, string? Error);

/// <summary>Parses the fixed-width table that <c>dotnet new list --type project</c> prints.</summary>
public static class TemplateList
{
    public static TemplateListResult Parse(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var dashes = Array.FindIndex(lines, l => l.StartsWith("---", StringComparison.Ordinal));
        if (dashes < 1 || !lines[dashes - 1].Contains("Template Name", StringComparison.Ordinal))
        {
            return new TemplateListResult(false, [], Strings.Get("TemplateHeaderMissing"));
        }

        var columns = Columns(lines[dashes]);
        if (columns.Count != 4)
        {
            return new TemplateListResult(false, [], Strings.Get("TemplateColumnCount"));
        }

        var items = new List<TemplateInfo>();
        foreach (var line in lines.Skip(dashes + 1).Where(l => l.Trim().Length > 0))
        {
            var name = Cell(line, columns[0]);
            var shortName = Cell(line, columns[1]);
            var languages = Cell(line, columns[2])
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Trim('[', ']'))
                .Where(l => l.Length > 0)
                .ToList();
            var tags = Cell(line, columns[3]);
            items.Add(new TemplateInfo(name, shortName, languages, tags.Length == 0 ? [] : [tags]));
        }

        return new TemplateListResult(true, items, null);
    }

    /// <summary>Templates whose languages and tags match the filters. A null filter matches everything.</summary>
    public static IReadOnlyList<TemplateInfo> Filter(IReadOnlyList<TemplateInfo> items, string? language, string? tag) =>
        items.Where(t => language is null || t.Languages.Any(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)))
            .Where(t => tag is null || t.Tags.Any(x => x.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>The start index and width of each column, read from the dashed rule line.</summary>
    private static List<(int Start, int Width)> Columns(string rule)
    {
        var columns = new List<(int Start, int Width)>();
        var i = 0;
        while (i < rule.Length)
        {
            if (rule[i] != '-')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < rule.Length && rule[i] == '-')
            {
                i++;
            }

            columns.Add((start, i - start));
        }

        return columns;
    }

    private static string Cell(string line, (int Start, int Width) column)
    {
        if (column.Start >= line.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(column.Width, line.Length - column.Start);
        return line.Substring(column.Start, length).Trim();
    }
}
