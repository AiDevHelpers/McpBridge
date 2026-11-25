using System.Xml.Linq;

namespace AiDevHelpers.Helpers.PackageHelper;

/// <summary>One <c>PackageReference</c> (or <c>Update</c>) in one project. <see cref="Version"/> is null when it is missing.</summary>
public sealed record PackageUse(string Project, string Package, string? Version, IReadOnlyList<string> Frameworks);

/// <summary>The version the repo should use for a package, and why. <see cref="Version"/> is null when no version can be chosen.</summary>
public sealed record VersionChoice(string? Version, string Reason);

/// <summary>Reads the package versions the repo's existing project files pin, and chooses one per package.</summary>
public static class PackageVersions
{
    public static IReadOnlyList<PackageUse> ReadAll(string root)
    {
        var uses = new List<PackageUse>();
        foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            var frameworks = doc.Descendants("TargetFramework").Select(e => e.Value.Trim())
                .Concat(doc.Descendants("TargetFrameworks").SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Distinct()
                .ToList();
            var project = Path.GetRelativePath(root, file).Replace('\\', '/');

            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var name = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var version = item.Attribute("Version")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                uses.Add(new PackageUse(project, name, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), frameworks));
            }
        }

        return uses;
    }

    /// <summary>
    /// The majority pinned version. A tie goes to the newest stable version the tied projects' .NET major allows. A floating or
    /// missing version is reported in the reason and never chosen.
    /// </summary>
    public static VersionChoice Choose(IReadOnlyList<PackageUse> uses)
    {
        if (uses.Count == 0)
        {
            return new VersionChoice(null, "not referenced");
        }

        var pinned = uses.Where(u => u.Version is not null && !u.Version.Contains('*')).ToList();
        if (pinned.Count == 0)
        {
            return uses.Any(u => u.Version is not null)
                ? new VersionChoice(null, "floating version only")
                : new VersionChoice(null, "no version pinned");
        }

        var counts = pinned.GroupBy(u => u.Version!, StringComparer.Ordinal).Select(g => (Version: g.Key, Count: g.Count())).ToList();
        var top = counts.Max(c => c.Count);
        var tied = counts.Where(c => c.Count == top).Select(c => c.Version).ToList();
        if (tied.Count == 1)
        {
            return new VersionChoice(tied[0], $"used by {top} of {pinned.Count} projects");
        }

        var frameworks = pinned.Where(u => tied.Contains(u.Version!, StringComparer.Ordinal)).SelectMany(u => u.Frameworks);
        var maxMajor = PackageUpdateRule.MaxMajor(frameworks);
        var newest = maxMajor is int major
            ? PackageUpdateRule.NewestAllowed(tied, major)
            : tied.OrderByDescending(v => PackageUpdateRule.Parse(v)).FirstOrDefault();
        return newest is null
            ? new VersionChoice(null, "tie with no allowed version")
            : new VersionChoice(newest, $"tie of {top} projects each; newest allowed for the frameworks");
    }
}
