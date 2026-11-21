namespace AiDevHelpers.Helpers.ConfigHelper;

/// <summary>The config files ConfigHelper can resolve by name (D10).</summary>
public enum ConfigFileKind
{
    /// <summary>Remote debugger configuration (XML or JSON) found under the search roots.</summary>
    RemoteDebugger,

    /// <summary>Properties/launchSettings.json of a project.</summary>
    LaunchSettings,

    /// <summary>appsettings.json beside a project.</summary>
    AppSettings,

    /// <summary>The user settings file: VS Code settings.json, or Visual Studio CurrentSettings.vssettings.</summary>
    UserSettings,

    /// <summary>Solution settings: .vsconfig beside the solution, or the .vs/&lt;solution&gt; folder.</summary>
    SolutionSettings,
}
