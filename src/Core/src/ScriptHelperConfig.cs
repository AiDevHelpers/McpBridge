namespace AiDevHelpers.Core;

/// <summary>
/// Locations for Script Helper, all under one data root that the setup step locks down to
/// Administrators. Nothing outside <see cref="DataRoot"/> belongs to Script Helper.
/// </summary>
public sealed class ScriptHelperConfig
{
    public const string FolderName = "script-helper";

    public ScriptHelperConfig(string dataRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The data root must be a full path.", nameof(dataRoot));
        }

        DataRoot = Path.TrimEndingDirectorySeparator(dataRoot);
    }

    public string DataRoot { get; }

    /// <summary>The registered project roots (no hashes).</summary>
    public string RegistryPath => Path.Combine(DataRoot, "projects.json");

    /// <summary>One signed approvals file per project.</summary>
    public string ApprovalsDirectory => Path.Combine(DataRoot, "approvals");

    /// <summary>Per-project replay high-water marks.</summary>
    public string VersionsDirectory => Path.Combine(DataRoot, "versions");

    /// <summary>The recorded protection mode, kept out of band from the approvals files.</summary>
    public string ModePath => Path.Combine(DataRoot, "mode.txt");

    /// <summary>Per-requester folders where requests are written (one subfolder per owner SID).</summary>
    public string QueueDirectory => Path.Combine(DataRoot, "queue");

    /// <summary>Per-requester folders where the broker writes answers (one subfolder per owner SID).</summary>
    public string ResultsDirectory => Path.Combine(DataRoot, "results");

    /// <summary>The TPM key that signs approvals.</summary>
    public string KeyName => "ScriptHelper.Approvals";

    /// <summary>
    /// The approvals file for a project. The id becomes a file name, so only letters, digits,
    /// '-' and '_' are accepted; anything that could escape the directory is refused.
    /// </summary>
    public string ApprovalsPath(string projectId)
    {
        if (string.IsNullOrEmpty(projectId) || !projectId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("A project id may only contain letters, digits, '-' and '_'.", nameof(projectId));
        }

        return Path.Combine(ApprovalsDirectory, projectId + ".json");
    }
}
