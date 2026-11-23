namespace AiDevHelpers.Common;

/// <summary>A tool the helper may offer to install. PackageId is the exact winget package id. Nothing outside this table is ever installed.</summary>
public sealed record InstallableTool(string Name, string Display, string PackageId, bool NeedsAdmin);

/// <summary>
/// The only tools the helper will install, and the winget package for each. It is the single source for the MCP plan step and for the
/// installer script that the admin approves, so the two cannot drift apart. A name or id from a repo, or from the AI, is never added here.
/// </summary>
public static class InstallableTools
{
    public static readonly IReadOnlyList<InstallableTool> All =
    [
        new("pwsh", "PowerShell 7", "Microsoft.PowerShell", NeedsAdmin: true),
        new("gitbash", "Git for Windows (Git Bash)", "Git.Git", NeedsAdmin: true),
        new("python", "Python 3", "Python.Python.3.13", NeedsAdmin: false),
        new("node", "Node.js (LTS)", "OpenJS.NodeJS.LTS", NeedsAdmin: true),
    ];

    public static InstallableTool? TryGet(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The installable tool for an interpreter name from the resolver ("pwsh", "bash", "python", "node"), or null.</summary>
    public static InstallableTool? ForInterpreter(string interpreterName) =>
        TryGet(string.Equals(interpreterName, "bash", StringComparison.OrdinalIgnoreCase) ? "gitbash" : interpreterName);
}
