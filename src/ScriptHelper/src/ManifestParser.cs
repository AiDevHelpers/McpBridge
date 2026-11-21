using System.Text.Json;

namespace AiDevHelpers.ScriptHelper.Core;

public sealed record ManifestResult(bool Ok, IReadOnlyList<AllowlistEntry> Entries, string Reason);

/// <summary>Parses a project's .elevated.json into allowlist entries. Fails closed on anything unexpected.</summary>
public static class ManifestParser
{
    public static ManifestResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Array)
            {
                return Fail(".elevated.json needs a \"scripts\" array");
            }

            var entries = new List<AllowlistEntry>();
            foreach (var item in scripts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("path", out var path)
                    || path.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(path.GetString()))
                {
                    return Fail("every script entry needs a string \"path\"");
                }

                IReadOnlyList<string>? args = null;
                if (item.TryGetProperty("args", out var argsElement))
                {
                    if (argsElement.ValueKind != JsonValueKind.Array)
                    {
                        return Fail("\"args\" must be an array of strings");
                    }

                    var list = new List<string>();
                    foreach (var a in argsElement.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.String)
                        {
                            return Fail("\"args\" must be an array of strings");
                        }

                        list.Add(a.GetString()!);
                    }

                    args = list;
                }

                entries.Add(new AllowlistEntry(path.GetString()!, args));
            }

            return new ManifestResult(true, entries, "parsed");
        }
        catch (JsonException)
        {
            return Fail(".elevated.json is malformed");
        }
    }

    private static ManifestResult Fail(string reason) =>
        new(false, Array.Empty<AllowlistEntry>(), reason);
}
