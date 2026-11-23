namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Whether a script the repo allowlist lists has a valid approval from this machine's admin store.</summary>
public enum ApprovalStatus
{
    Approved,
    Unsigned,
    Changed,
    AllowlistChanged,
    Invalid,
}

/// <summary>The verify steps of setup, split out so the CLI can be tested with a fake.</summary>
public interface IVerifyCommands
{
    VerifyReport Verify(string root);

    IReadOnlyList<SetupResult> ApproveUnsigned(string root, Func<string, bool> confirm);
}

public sealed record ScriptStatus(string Path, ApprovalStatus Status, string Reason);

/// <summary>The result of checking every allowlisted file of a repo. AllowAllActive means the repo may run any script elevated.</summary>
public sealed record VerifyReport(bool Ok, string Reason, bool AllowAllActive, IReadOnlyList<ScriptStatus> Scripts)
{
    public static VerifyReport Failed(string reason) => new(false, reason, false, []);

    /// <summary>True when every listed file is approved, or allow-all covers the repo.</summary>
    public bool FullyApproved => Ok && (AllowAllActive || Scripts.All(s => s.Status == ApprovalStatus.Approved));
}
