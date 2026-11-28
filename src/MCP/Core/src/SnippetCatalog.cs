using System.Xml.Linq;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>One Visual Studio code snippet (a <c>.snippet</c> file): its header and the language of its code.</summary>
internal sealed record SnippetInfo(string Title, string Shortcut, string Description, string Language, IReadOnlyList<string> SnippetTypes, string Path);

/// <summary>The snippets found by a scan. Truncated is true when more matched than the limit.</summary>
internal sealed record SnippetScan(IReadOnlyList<SnippetInfo> Snippets, bool Truncated);

/// <summary>Reads Visual Studio <c>.snippet</c> files (the CodeSnippets schema from the Visual Studio docs).</summary>
internal static class SnippetCatalog
{
    /// <summary>Parses one snippet file. Returns null when the text is not a snippet.</summary>
    public static SnippetInfo? Parse(string xml, string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var snippet = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CodeSnippet");
        if (snippet is null)
            return null;

        var header = snippet.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        var code = snippet.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code");
        if (header is null || code is null)
            return null;

        var title = Child(header, "Title");
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var types = header.Descendants()
            .Where(e => e.Name.LocalName == "SnippetType")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();

        return new SnippetInfo(
            title.Trim(),
            Child(header, "Shortcut") ?? "",
            Child(header, "Description") ?? "",
            (string?)code.Attribute("Language") ?? "",
            types,
            path);
    }

    /// <summary>Scans the folders recursively for <c>.snippet</c> files. Unreadable or non-snippet files are skipped.</summary>
    public static SnippetScan Scan(IEnumerable<string> roots, string? filter, string? language, int max)
    {
        var found = new List<SnippetInfo>();
        var truncated = false;
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.snippet", SearchOption.AllDirectories))
            {
                SnippetInfo? info;
                try
                {
                    info = Parse(File.ReadAllText(file), file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (info is null || !Matches(info, filter, language))
                    continue;

                if (found.Count >= max)
                {
                    truncated = true;
                    return new SnippetScan(found, truncated);
                }

                found.Add(info);
            }
        }

        return new SnippetScan(found, truncated);
    }

    private static bool Matches(SnippetInfo info, string? filter, string? language)
    {
        if (!string.IsNullOrWhiteSpace(language) &&
            !string.Equals(info.Language, language.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var f = filter.Trim();
        return info.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
            || info.Shortcut.Contains(f, StringComparison.OrdinalIgnoreCase)
            || info.Description.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
}
