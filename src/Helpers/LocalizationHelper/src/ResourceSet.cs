using System.Globalization;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>A neutral Strings.resx with the Strings.&lt;culture&gt;.resx files beside it.</summary>
public sealed record ResourceSetInfo(
    string NeutralPath,
    string Dir,
    IReadOnlyDictionary<string, string> CulturePaths,
    IReadOnlyList<string> InvalidFiles);

public static class ResourceSet
{
    /// <summary>The pseudo-localization culture (unit 6). Accepted by name, skipped by the sync check.</summary>
    public const string PseudoCulture = "qps-ploc";

    public static bool IsValidCulture(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (string.Equals(name, PseudoCulture, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    public static IReadOnlyList<ResourceSetInfo> Find(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(Strings.Format("FolderNotFound", root));
        }

        var fullRoot = Path.GetFullPath(root);
        var sets = new List<ResourceSetInfo>();
        foreach (var neutral in Directory.EnumerateFiles(fullRoot, "Strings.resx", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (StringScanner.InBuildFolder(Path.GetRelativePath(fullRoot, neutral)))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(neutral)!;
            var cultures = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var invalid = new List<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "Strings.*.resx").Order(StringComparer.Ordinal))
            {
                var culture = Path.GetFileNameWithoutExtension(file)["Strings.".Length..];
                if (IsValidCulture(culture))
                {
                    cultures[culture] = file;
                }
                else
                {
                    invalid.Add(file);
                }
            }

            sets.Add(new ResourceSetInfo(neutral, dir, cultures, invalid));
        }

        return sets;
    }
}
