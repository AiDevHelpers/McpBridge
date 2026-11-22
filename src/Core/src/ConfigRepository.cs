namespace AiDevHelpers.Core;

/// <summary>
/// The one place that knows where AiDevHelpers keeps shared machine-wide data:
/// <c>%ProgramData%\AiDevHelpers</c>. Each component gets its own element with its own subfolder,
/// so permissions for one component (for example an admin-only ACL) never cover another.
/// </summary>
public sealed class ConfigRepository
{
    public const string FolderName = "AiDevHelpers";

    public ConfigRepository(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The config root must be a full path.", nameof(root));
        }

        Root = Path.TrimEndingDirectorySeparator(root);
        ScriptHelper = new ScriptHelperConfig(Path.Combine(Root, ScriptHelperConfig.FolderName));
    }

    /// <summary>Builds the repository from <c>%ProgramData%</c>, falling back to <c>C:\ProgramData</c>.</summary>
    public static ConfigRepository FromEnvironment(Func<string, string?>? getEnvironmentVariable = null)
    {
        var read = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var programData = read("ProgramData");
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\ProgramData";
        }

        return new ConfigRepository(Path.Combine(programData, FolderName));
    }

    /// <summary>The shared root. Other components (such as the Bridge's vs-instances) already write here as normal users.</summary>
    public string Root { get; }

    /// <summary>Script Helper's registry, approvals, version counters and mode file.</summary>
    public ScriptHelperConfig ScriptHelper { get; }
}
