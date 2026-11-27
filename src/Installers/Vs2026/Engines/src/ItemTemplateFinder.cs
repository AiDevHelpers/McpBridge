using System.Xml;
using System.Xml.Linq;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>One item template: what Add New Item offers, and the path to pass as <c>template</c> to <c>ide_add_project_item</c>.</summary>
internal sealed class ItemTemplate(string name, string description, string language, string category, string path)
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Language { get; } = language;

    public string Category { get; } = category;

    public string Path { get; } = path;
}

/// <summary>The templates found, and whether the limit cut the list short.</summary>
internal sealed class ItemTemplateList(IReadOnlyList<ItemTemplate> templates, bool truncated)
{
    public IReadOnlyList<ItemTemplate> Templates { get; } = templates;

    public bool Truncated { get; } = truncated;
}

/// <summary>
/// Finds the item templates (<c>.vstemplate</c> files of Type Item) under the template folders of Visual Studio, so a
/// caller can choose one for <c>ide_add_project_item</c> mode fromNew. Pure file work, no Visual Studio types.
/// A file that cannot be read or parsed is skipped.
/// </summary>
internal static class ItemTemplateFinder
{
    private const int MaxDepth = 8;

    public static ItemTemplateList Find(IEnumerable<string> roots, string? filter, string? language, int limit)
    {
        var found = new List<ItemTemplate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in TemplateFiles(root, 0))
            {
                if (!seen.Add(file))
                {
                    continue;
                }

                var template = Read(root, file);
                if (template is not null && Matches(template, filter, language))
                {
                    found.Add(template);
                }
            }
        }

        var ordered = found
            .OrderBy(t => t.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered.Count > limit
            ? new ItemTemplateList(ordered.Take(limit).ToList(), truncated: true)
            : new ItemTemplateList(ordered, truncated: false);
    }

    private static IEnumerable<string> TemplateFiles(string folder, int depth)
    {
        var files = new List<string>();
        var folders = new List<string>();
        try
        {
            files.AddRange(Directory.GetFiles(folder, "*.vstemplate"));
            if (depth < MaxDepth)
            {
                folders.AddRange(Directory.GetDirectories(folder));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var child in folders)
        {
            foreach (var file in TemplateFiles(child, depth + 1))
            {
                yield return file;
            }
        }
    }

    private static ItemTemplate? Read(string root, string file)
    {
        try
        {
            var document = XDocument.Load(file);
            var top = document.Root;
            if (top is null || top.Name.LocalName != "VSTemplate"
                || !string.Equals((string?)top.Attribute("Type"), "Item", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var data = top.Elements().FirstOrDefault(e => e.Name.LocalName == "TemplateData");
            string? Child(string name) => data?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

            var nameElement = data?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            var name = nameElement is not null && nameElement.Attribute("Package") is null ? nameElement.Value.Trim() : string.Empty;
            if (name.Length == 0)
            {
                name = System.IO.Path.GetFileNameWithoutExtension(file);
            }

            var segments = RelativeSegments(root, file);
            var languageName = Child("ProjectType");
            if (string.IsNullOrEmpty(languageName))
            {
                languageName = segments.Count > 0 ? segments[0] : string.Empty;
            }

            return new ItemTemplate(name, Child("Description") ?? string.Empty, languageName!, Category(segments), file);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> RelativeSegments(string root, string file)
    {
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var fullFile = System.IO.Path.GetFullPath(file);
        var relative = fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? fullFile.Substring(fullRoot.Length) : fullFile;
        return relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).ToList();
    }

    // Segments after the language folder, without the culture folder (digits) and the template's own folder and file.
    private static string Category(List<string> segments)
    {
        var inner = segments.Skip(1).Take(Math.Max(0, segments.Count - 3)).Where(s => !s.All(char.IsDigit));
        return string.Join("/", inner);
    }

    private static bool Matches(ItemTemplate template, string? filter, string? language) =>
        (string.IsNullOrEmpty(language) || string.Equals(template.Language, language, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrEmpty(filter)
            || template.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
            || template.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
}
