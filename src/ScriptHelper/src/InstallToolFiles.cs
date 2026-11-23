using System.Text;
using System.Text.Json;
using AiDevHelpers.Common;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>The elevated setup step that puts the installer script in place.</summary>
public interface IToolCommands
{
    /// <summary>Writes the installer script and allowlist, registers the tools folder, and approves the script after the person confirms.</summary>
    SetupResult InstallTools();
}

/// <summary>
/// The installer script and its allowlist, written by <c>setup tools</c> into the admin-only install folder, registered as a project and
/// approved there like any other elevated script. The script takes a tool name, not a package id, and looks the id up in its own table
/// (built from <see cref="InstallableTools"/>), so even a caller that reaches the broker cannot name what gets installed.
/// </summary>
public static class InstallToolFiles
{
    public const string ScriptName = "install-tool.ps1";
    public const string ManifestName = ".elevated.json";
    public const string FolderName = "tools";

    public static string ToolsRoot(ScriptHelperConfig config) => Path.Combine(config.InstallDirectory, FolderName);

    public static string Script()
    {
        var table = string.Join("\n", InstallableTools.All.Select(t => $"    '{t.Name}' = '{t.PackageId}'"));
        return $$"""
            #Requires -Version 7
            # Installs one tool with winget. The tool name picks the package from the table below; anything else is refused.
            param([Parameter(Mandatory)][string]$Tool)
            $ErrorActionPreference = 'Stop'
            $packages = @{
            {{table}}
            }
            if (-not $packages.ContainsKey($Tool)) { [Console]::Error.WriteLine("unknown tool '$Tool'"); exit 2 }
            if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { [Console]::Error.WriteLine('winget is not installed'); exit 3 }
            & winget.exe install --id $packages[$Tool] --exact --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity
            exit $LASTEXITCODE

            """.Replace("\r\n", "\n");
    }

    /// <summary>One allowlist entry per tool, each with exactly that tool's name as its only argument.</summary>
    public static string Manifest()
    {
        var scripts = InstallableTools.All.Select(t => new { path = ScriptName, args = new[] { t.Name } });
        return JsonSerializer.Serialize(new { scripts }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Writes the script and the allowlist into the tools folder, replacing older copies.</summary>
    public static void Write(string toolsRoot)
    {
        Directory.CreateDirectory(toolsRoot);
        File.WriteAllText(Path.Combine(toolsRoot, ScriptName), Script(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(toolsRoot, ManifestName), Manifest(), new UTF8Encoding(false));
    }
}
