namespace AiDevHelpers.Helpers.ExtensionsHelper;

/// <summary>Finds VSIXInstaller.exe under Visual Studio install folders, preferring the newest version and edition.</summary>
public static class VsixInstallerLocator
{
    public static string? Find(IEnumerable<string> programFilesRoots)
    {
        foreach (var root in programFilesRoots.Where(Directory.Exists))
        {
            var vsRoot = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vsRoot))
            {
                continue;
            }

            foreach (var version in Directory.EnumerateDirectories(vsRoot).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var edition in Directory.EnumerateDirectories(version).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(edition, "Common7", "IDE", "VSIXInstaller.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
