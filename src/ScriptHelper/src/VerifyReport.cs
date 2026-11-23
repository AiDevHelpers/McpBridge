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

    /// <summary>Finds the scripts the repo's launch and debug configuration runs and whether the allowlist already lists them.</summary>
    ScanReport Scan(string root);

    /// <summary>Adds each unlisted script the confirm function accepts to the repo's .elevated.json. Approval is a separate step.</summary>
    IReadOnlyList<SetupResult> AddToAllowlist(string root, Func<string, bool> confirm);
}

public sealed record ScanReport(bool Ok, string Reason, IReadOnlyList<FoundScript> Scripts)
{
    public static ScanReport Failed(string reason) => new(false, reason, []);
}

public sealed record ScriptStatus(string Path, ApprovalStatus Status, string Reason);

/// <summary>The result of checking every allowlisted file of a repo. AllowAllActive means the repo may run any script elevated.</summary>
public sealed record VerifyReport(bool Ok, string Reason, bool AllowAllActive, IReadOnlyList<ScriptStatus> Scripts)
{
    public static VerifyReport Failed(string reason) => new(false, reason, false, []);

    /// <summary>True when every listed file is approved, or allow-all covers the repo.</summary>
    public bool FullyApproved => Ok && (AllowAllActive || Scripts.All(s => s.Status == ApprovalStatus.Approved));
}
