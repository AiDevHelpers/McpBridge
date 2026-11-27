using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.ConfigHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Finds real config file paths and reads them with secrets redacted. Reads are limited to allowed roots.</summary>
[McpServerToolType]
internal sealed class ConfigTools
{
    [McpServerTool]
    [SafeTool]
    [Description("Resolve the real path of a config file. Kinds: RemoteDebugger, LaunchSettings, AppSettings, UserSettings, SolutionSettings. " +
                 "LaunchSettings and AppSettings need projectPath. SolutionSettings needs solutionPath.")]
    public static string ConfigResolve(
        [Description("Config kind: RemoteDebugger, LaunchSettings, AppSettings, UserSettings or SolutionSettings")] string kind,
        [Description("Optional: .csproj path or its folder")] string? projectPath = null,
        [Description("Optional: full path of the .sln")] string? solutionPath = null)
    {
        if (!Enum.TryParse<ConfigFileKind>(kind, ignoreCase: true, out var parsed))
        {
            return Json(new { ok = false, error = Strings.Format("UnknownConfigKind", kind) });
        }

        var resolver = new ConfigPathResolver(new ConfigPathOptions { SolutionPath = solutionPath });
        var result = resolver.Resolve(parsed, projectPath);
        return Json(new { ok = result.Exists, kind = result.Kind.ToString(), path = result.Path, error = result.Error });
    }

    [McpServerTool]
    [SafeTool]
    [Description("Read a config file with secret values redacted. The file must lie under the solution folder, the project folder, " +
                 "the user profile, AppData, or a Remote Debugger search root.")]
    public static string ConfigRead(
        [Description("Full path of the file to read")] string path,
        [Description("Optional: full path of the .sln, which adds its folder to the allowed roots")] string? solutionPath = null,
        [Description("Optional: .csproj path or folder, which adds its folder to the allowed roots")] string? projectPath = null)
    {
        var options = new ConfigPathOptions { SolutionPath = solutionPath };
        var roots = new List<string> { options.UserRoot, options.AppDataRoot, options.LocalAppDataRoot };
        roots.AddRange(options.RemoteDebuggerSearchRoots);
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            roots.Add(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!);
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var full = Path.GetFullPath(projectPath);
            roots.Add(Path.HasExtension(full) ? Path.GetDirectoryName(full)! : full);
        }

        var result = ConfigReader.ReadRedacted(path, roots.Where(r => !string.IsNullOrWhiteSpace(r)));
        return Json(new { ok = result.Ok, content = result.Content, error = result.Error });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
