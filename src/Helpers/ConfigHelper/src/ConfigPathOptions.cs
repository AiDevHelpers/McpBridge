namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>Roots the resolver searches. Defaults come from the environment; tests override them.</summary>
public sealed class ConfigPathOptions
{
    /// <summary>Full path of the open .sln, or null when no solution is open.</summary>
    public string? SolutionPath { get; set; }

    /// <summary>The user profile folder (%USERPROFILE%).</summary>
    public string UserRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>%APPDATA% (Roaming).</summary>
    public string AppDataRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>%LOCALAPPDATA%.</summary>
    public string LocalAppDataRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Folders searched for a "Remote Debugger" folder holding the remote debugger config.</summary>
    public IReadOnlyList<string> RemoteDebuggerSearchRoots { get; set; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];
}
