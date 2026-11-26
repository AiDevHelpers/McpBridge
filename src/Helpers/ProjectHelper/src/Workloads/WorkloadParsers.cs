using System.Text.Json;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>An installed .NET workload from <c>dotnet workload list</c>.</summary>
public sealed record InstalledWorkload(string Id, string ManifestVersion, string Source);

/// <summary>A workload from <c>dotnet workload search</c>.</summary>
public sealed record WorkloadInfo(string Id, string Description);

/// <summary>A Visual Studio instance from <c>vswhere -format json</c>.</summary>
public sealed record VsInstance(string Path, string Version, string ProductId, string DisplayName, bool IsComplete);

/// <summary>A parse result. <see cref="Ok"/> is false when the expected table or JSON is missing or malformed; <see cref="Error"/> says why.</summary>
public sealed record ParseResult<T>(bool Ok, IReadOnlyList<T> Items, string? Error);

/// <summary>Parses the fixed-width tables the dotnet CLI prints for workloads.</summary>
public static class DotnetWorkloadParser
{
    public static ParseResult<InstalledWorkload> ParseInstalled(string text)
    {
        var rows = Rows(text, "Installed Workload Id", 3);
        if (rows is null)
        {
            return new ParseResult<InstalledWorkload>(false, [], Strings.Get("WorkloadListHeaderMissing"));
        }

        return new ParseResult<InstalledWorkload>(true, rows.Select(r => new InstalledWorkload(r[0], r[1], r[2])).ToList(), null);
    }

    public static ParseResult<WorkloadInfo> ParseSearch(string text)
    {
        var rows = Rows(text, "Workload ID", 2);
        if (rows is null)
        {
            return new ParseResult<WorkloadInfo>(false, [], Strings.Get("WorkloadSearchHeaderMissing"));
        }

        return new ParseResult<WorkloadInfo>(true, rows.Select(r => new WorkloadInfo(r[0], r[1])).ToList(), null);
    }

    /// <summary>The body rows of the table whose header starts with <paramref name="header"/>, one cell per column; null when there is no such header.</summary>
    private static List<string[]>? Rows(string text, string header, int columns)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var index = Array.FindIndex(lines, l => l.TrimStart().StartsWith(header, StringComparison.Ordinal));
        if (index < 0 || index + 1 >= lines.Length || !lines[index + 1].StartsWith("---", StringComparison.Ordinal))
        {
            return null;
        }

        var starts = Starts(lines[index]);
        if (starts.Count != columns)
        {
            return null;
        }

        var rows = new List<string[]>();
        foreach (var line in lines.Skip(index + 2).Where(l => l.Trim().Length > 0))
        {
            var cells = new string[columns];
            for (var c = 0; c < columns; c++)
            {
                var start = starts[c];
                var end = c + 1 < columns ? starts[c + 1] : line.Length;
                cells[c] = start >= line.Length ? string.Empty : line[start..Math.Min(end, line.Length)].Trim();
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// Column starts in the header line: a word that begins after two or more spaces. Single spaces inside a title (such as
    /// "Installed Workload Id") do not start a column.
    /// </summary>
    private static List<int> Starts(string header)
    {
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || (header[i - 1] == ' ' && (i < 2 || header[i - 2] == ' '))))
            {
                starts.Add(i);
            }
        }

        return starts;
    }
}

/// <summary>Parses the JSON <c>vswhere -format json</c> prints.</summary>
public static class VswhereParser
{
    public static ParseResult<VsInstance> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ParseResult<VsInstance>(false, [], Strings.Get("VswhereNotArray"));
            }

            var items = doc.RootElement.EnumerateArray().Select(e => new VsInstance(
                Text(e, "installationPath"),
                Text(e, "installationVersion"),
                Text(e, "productId"),
                Text(e, "displayName"),
                e.TryGetProperty("isComplete", out var complete) && complete.ValueKind == JsonValueKind.True)).ToList();
            return new ParseResult<VsInstance>(true, items, null);
        }
        catch (JsonException ex)
        {
            return new ParseResult<VsInstance>(false, [], ex.Message);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}

/// <summary>What a project type needs, and which required workloads are missing.</summary>
public static class WorkloadCheck
{
    /// <summary>The workloads a project template needs. Empty for templates that need none.</summary>
    private static readonly Dictionary<string, string[]> TemplateWorkloads = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maui"] = ["maui"],
        ["maui-blazor"] = ["maui"],
        ["mauilib"] = ["maui"],
        ["android"] = ["android"],
        ["androidlib"] = ["android"],
        ["ios"] = ["ios"],
        ["ioslib"] = ["ios"],
        ["blazorwasm"] = ["wasm-tools"],
    };

    public static IReadOnlyList<string> ForProjectType(string shortName) =>
        TemplateWorkloads.TryGetValue(shortName, out var workloads) ? workloads : [];

    public static IReadOnlyList<string> Missing(IEnumerable<string> required, IEnumerable<string> installed)
    {
        var have = installed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.Where(r => !have.Contains(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
