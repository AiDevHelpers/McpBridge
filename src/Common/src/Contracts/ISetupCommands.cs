namespace AiDevHelpers.Common.Contracts;

/// <summary>The outcome of a setup step. ProjectId is set when a project was registered or approved.</summary>
public sealed record SetupResult(bool Ok, string Reason, string? ProjectId = null);

/// <summary>
/// What approving a script would pin, shown to a person before anything is written.
/// Prompt is the labelled text to show; Fingerprint and AllowlistHash are the exact values a
/// later confirm must present, so a script changed after it was shown is refused.
/// </summary>
public sealed record ApprovalPreview(bool Ok, string Reason, string Prompt, string Fingerprint, string AllowlistHash);

/// <summary>The elevated Script Helper setup steps: register a project root, approve a script for it.</summary>
public interface ISetupCommands
{
    SetupResult Register(string root);

    /// <summary>Approves after a person answers the question in the console.</summary>
    SetupResult Approve(string root, string relativeScript);

    /// <summary>Computes what would be approved and writes nothing.</summary>
    ApprovalPreview PrepareApproval(string root, string relativeScript);

    /// <summary>
    /// Approves without a console question, but only if the script and allowlist still hash to the
    /// values that were shown. The caller is responsible for having asked a person.
    /// </summary>
    SetupResult ConfirmApproval(string root, string relativeScript, string fingerprint, string allowlistHash);

    /// <summary>
    /// Approves allow-all for a registered project whose repo settings request it, after two separate confirmations in the
    /// elevated console. Any script inside the repo may then run elevated without per-script approval.
    /// </summary>
    SetupResult AllowAll(string root);

    /// <summary>Allow-all after the person answered both questions in chat. Writes only if the settings hash still equals approvedHash.</summary>
    SetupResult AllowAllApproved(string root, string approvedHash);
}
