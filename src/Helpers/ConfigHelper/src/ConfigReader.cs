namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>Reads a config file only when it lies under an allowed root, and redacts secrets in the result.</summary>
public static class ConfigReader
{
    public static ConfigReadResult ReadRedacted(string path, IEnumerable<string> allowedRoots)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ConfigReadResult(false, null, Strings.Get("InvalidPath"));
        }

        if (!allowedRoots.Any(root => IsUnder(full, Path.GetFullPath(root))))
        {
            return new ConfigReadResult(false, null, Strings.Get("PathOutsideAllowedRoots"));
        }

        if (!File.Exists(full))
        {
            return new ConfigReadResult(false, null, Strings.Get("FileNotFound"));
        }

        return new ConfigReadResult(true, ConfigRedactor.Redact(File.ReadAllText(full)), null);
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
