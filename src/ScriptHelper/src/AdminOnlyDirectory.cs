using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// ACL for the ProgramData directories that hold the registry, approvals and counters:
/// Administrators and SYSTEM have full control, users can only read, and nothing is inherited.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AdminOnlyDirectory
{
    public static DirectorySecurity BuildSecurity()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var sec = new DirectorySecurity();
        sec.SetOwner(admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>Creates the directory and applies the ACL. Needs an elevated process.</summary>
    public static void Ensure(string path)
    {
        var info = Directory.CreateDirectory(path);
        info.SetAccessControl(BuildSecurity());
    }
}
