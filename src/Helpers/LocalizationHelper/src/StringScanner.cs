using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>A multi-word string literal in C# source. <see cref="ResxKey"/> names the resource when the same text is already in a Strings.resx.</summary>
public sealed record StringFinding(string File, int Line, string Literal, string? ResxKey);

/// <summary>
/// Finds user-facing text that is still a literal in code. Comments, attributes (tool and parameter descriptions are for the
/// client's tool list), the continuation lines of a multi-line description, logger calls, Strings.cs, and bin or obj folders are skipped.
/// </summary>
public static partial class StringScanner
{
    private const int MaxResxSearchDepth = 8;

    /// <summary>Scans a .cs file or a folder of them. <paramref name="isAllowed"/> drops literals that are not user-facing.</summary>
    public static IReadOnlyList<StringFinding> Scan(string path, Func<string, bool>? isAllowed = null)
    {
        string baseDir;
        IEnumerable<string> files;
        if (File.Exists(path))
        {
            baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            baseDir = Path.GetFullPath(path);
            files = Directory.EnumerateFiles(baseDir, "*.cs", SearchOption.AllDirectories);
        }
        else
        {
            throw new DirectoryNotFoundException(Strings.Format("FolderNotFound", path));
        }

        var findings = new List<StringFinding>();
        var resxCache = new Dictionary<string, Dictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(baseDir, Path.GetFullPath(file));
            if (InBuildFolder(relative) || Path.GetFileName(file) == "Strings.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var isDescriptionContinuation = trimmed.StartsWith('"') && (trimmed.EndsWith('+') || trimmed.EndsWith(")]"));
                if (trimmed.StartsWith("//") || trimmed.StartsWith('[') || trimmed.StartsWith('*')
                    || isDescriptionContinuation || LoggerCall().IsMatch(line))
                {
                    continue;
                }

                foreach (Match m in Literal().Matches(line))
                {
                    var text = m.Groups["text"].Value;
                    if (!MultiWord().IsMatch(text) || (isAllowed is not null && isAllowed(text)))
                    {
                        continue;
                    }

                    findings.Add(new StringFinding(relative, i + 1, text, ResxKeyFor(file, text, resxCache)));
                }
            }
        }

        return findings;
    }

    internal static bool InBuildFolder(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResxKeyFor(string file, string text, Dictionary<string, Dictionary<string, string>?> cache)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        for (var depth = 0; dir is not null && depth < MaxResxSearchDepth; depth++, dir = Path.GetDirectoryName(dir))
        {
            var resx = Path.Combine(dir, "Resources", "Strings.resx");
            if (!File.Exists(resx))
            {
                continue;
            }

            if (!cache.TryGetValue(resx, out var values))
            {
                values = ReadValues(resx);
                cache[resx] = values;
            }

            return values?.FirstOrDefault(kv => kv.Value == text).Key;
        }

        return null;
    }

    private static Dictionary<string, string>? ReadValues(string resx)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var data in XDocument.Load(resx).Descendants("data"))
            {
                var name = (string?)data.Attribute("name");
                if (name is not null && !values.ContainsKey(name))
                {
                    values[name] = (string?)data.Element("value") ?? string.Empty;
                }
            }

            return values;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\$?""(?<text>(?:[^""\\]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"[A-Za-z]{2,} [A-Za-z]{2,}")]
    private static partial Regex MultiWord();

    [GeneratedRegex(@"Log(Information|Warning|Debug|Error|Trace|Critical)\(")]
    private static partial Regex LoggerCall();
}
