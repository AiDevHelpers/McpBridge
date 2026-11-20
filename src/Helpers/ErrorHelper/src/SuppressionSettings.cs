using System.Text.Json;

namespace AiDevHelpers.Helpers.ErrorHelper;

/// <summary>
/// Codes and sources hidden from every ErrorHelper result. Read from the solution's .vsconfig under the
/// "aidevhelpersErrors" key: { "suppressCodes": [...], "suppressSources": [...] }. The solution wins (D18).
/// </summary>
public sealed record SuppressionSettings(IReadOnlySet<string> Codes, IReadOnlySet<DiagnosticSource> Sources)
{
    public const string SectionName = "aidevhelpersErrors";

    public static SuppressionSettings Empty { get; } =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<DiagnosticSource>());

    /// <summary>Loads suppression from the .vsconfig beside the solution. Missing file or section gives Empty.</summary>
    public static SuppressionSettings Load(string solutionPath)
    {
        var vsconfig = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, ".vsconfig");
        if (!File.Exists(vsconfig))
        {
            return Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(vsconfig));
            if (!doc.RootElement.TryGetProperty(SectionName, out var section))
            {
                return Empty;
            }

            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Strings(section, "suppressCodes"))
            {
                codes.Add(item);
            }

            var sources = new HashSet<DiagnosticSource>();
            foreach (var item in Strings(section, "suppressSources"))
            {
                if (Enum.TryParse<DiagnosticSource>(item, ignoreCase: true, out var source))
                {
                    sources.Add(source);
                }
            }

            return new SuppressionSettings(codes, sources);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static IEnumerable<string> Strings(JsonElement section, string name)
    {
        if (!section.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
            {
                yield return text;
            }
        }
    }
}
