namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>Finds the real path of each supported config kind from the open solution, the project and the user profile.</summary>
public sealed class ConfigPathResolver
{
    private const int RemoteDebuggerMaxDepth = 6;
    private static readonly string[] RemoteDebuggerFilePatterns = ["*.xml", "*.json"];

    private readonly ConfigPathOptions _options;

    public ConfigPathResolver(ConfigPathOptions options)
    {
        _options = options;
    }

    /// <summary>Resolves one kind. projectPath is a .csproj file or its folder, used by LaunchSettings and AppSettings.</summary>
    public ConfigPathResult Resolve(ConfigFileKind kind, string? projectPath = null) => kind switch
    {
        ConfigFileKind.RemoteDebugger => ResolveRemoteDebugger(),
        ConfigFileKind.LaunchSettings => ResolveProjectFile(kind, projectPath, Path.Combine("Properties", "launchSettings.json")),
        ConfigFileKind.AppSettings => ResolveProjectFile(kind, projectPath, "appsettings.json"),
        ConfigFileKind.UserSettings => ResolveUserSettings(),
        ConfigFileKind.SolutionSettings => ResolveSolutionSettings(),
        _ => new ConfigPathResult(kind, null, false, Strings.Get("UnknownConfigKind")),
    };

    private ConfigPathResult ResolveProjectFile(ConfigFileKind kind, string? projectPath, string relative)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return new ConfigPathResult(kind, null, false, Strings.Get("ProjectPathRequired"));
        }

        var full = Path.GetFullPath(projectPath);
        var projectDir = Path.HasExtension(full) ? Path.GetDirectoryName(full)! : full;
        var path = Path.Combine(projectDir, relative);
        return new ConfigPathResult(kind, path, File.Exists(path), File.Exists(path) ? null : Strings.Get("FileNotFound"));
    }

    private ConfigPathResult ResolveSolutionSettings()
    {
        const ConfigFileKind kind = ConfigFileKind.SolutionSettings;
        if (string.IsNullOrWhiteSpace(_options.SolutionPath))
        {
            return new ConfigPathResult(kind, null, false, Strings.Get("NoSolutionOpen"));
        }

        var sln = Path.GetFullPath(_options.SolutionPath);
        var repoDir = Path.GetDirectoryName(sln)!;
        var vsconfig = Path.Combine(repoDir, ".vsconfig");
        if (File.Exists(vsconfig))
        {
            return new ConfigPathResult(kind, vsconfig, true, null);
        }

        var vsFolder = Path.Combine(repoDir, ".vs", Path.GetFileNameWithoutExtension(sln));
        if (Directory.Exists(vsFolder))
        {
            return new ConfigPathResult(kind, vsFolder, true, null);
        }

        return new ConfigPathResult(kind, vsconfig, false, Strings.Get("NoVsconfigOrVsFolder"));
    }

    private ConfigPathResult ResolveUserSettings()
    {
        const ConfigFileKind kind = ConfigFileKind.UserSettings;
        var vsCode = Path.Combine(_options.AppDataRoot, "Code", "User", "settings.json");
        if (File.Exists(vsCode))
        {
            return new ConfigPathResult(kind, vsCode, true, null);
        }

        var vsRoot = Path.Combine(_options.LocalAppDataRoot, "Microsoft", "VisualStudio");
        if (Directory.Exists(vsRoot))
        {
            var newest = Directory.EnumerateDirectories(vsRoot)
                .OrderByDescending(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                .Select(dir => Path.Combine(dir, "Settings", "CurrentSettings.vssettings"))
                .FirstOrDefault(File.Exists);
            if (newest is not null)
            {
                return new ConfigPathResult(kind, newest, true, null);
            }
        }

        return new ConfigPathResult(kind, vsCode, false, Strings.Get("NoUserSettingsFound"));
    }

    private ConfigPathResult ResolveRemoteDebugger()
    {
        const ConfigFileKind kind = ConfigFileKind.RemoteDebugger;
        foreach (var root in _options.RemoteDebuggerSearchRoots.Where(Directory.Exists))
        {
            var folder = FindRemoteDebuggerFolder(root, 0);
            if (folder is null)
            {
                continue;
            }

            var file = RemoteDebuggerFilePatterns
                .SelectMany(pattern => Directory.EnumerateFiles(folder, pattern))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (file is not null)
            {
                return new ConfigPathResult(kind, file, true, null);
            }
        }

        return new ConfigPathResult(kind, null, false, Strings.Get("NoRemoteDebuggerConfig"));
    }

    private static string? FindRemoteDebuggerFolder(string dir, int depth)
    {
        if (string.Equals(Path.GetFileName(dir), "Remote Debugger", StringComparison.OrdinalIgnoreCase))
        {
            return dir;
        }

        if (depth >= RemoteDebuggerMaxDepth)
        {
            return null;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var child in children)
        {
            var found = FindRemoteDebuggerFolder(child, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
