using System.Security.Principal;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

/// <summary>
/// A fact that is skipped unless the test process is elevated. Visual Studio Test Explorer ignores runsettings filters,
/// so the TPM tests skip themselves there; scripts/gate-tpm.ps1 runs them elevated.
/// </summary>
public sealed class RequiresAdministratorFactAttribute : FactAttribute
{
    public RequiresAdministratorFactAttribute()
    {
        Skip = SkipReason(IsAdministrator());
    }

    internal static string? SkipReason(bool isAdministrator) =>
        isAdministrator ? null : "Needs administrator rights. Run scripts/gate-tpm.ps1 or an elevated terminal.";

    private static bool IsAdministrator() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
