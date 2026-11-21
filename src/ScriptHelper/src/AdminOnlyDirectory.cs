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

    /// <summary>
    /// True when only Administrators and SYSTEM own the folder and hold write-type rights on it.
    /// Read-only access for other accounts is fine; write, delete and permission rights are not.
    /// </summary>
    public static bool IsAdminOnly(string path)
    {
        if (!Directory.Exists(path))
            return false;

        const string adminsSid = "S-1-5-32-544";
        const string systemSid = "S-1-5-18";
        const FileSystemRights WriteRights = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        var sec = new DirectoryInfo(path).GetAccessControl();
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (owner.Value != adminsSid && owner.Value != systemSid))
            return false;

        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            var who = rule.IdentityReference.Value;
            if (who == adminsSid || who == systemSid)
                continue;

            if ((rule.FileSystemRights & WriteRights) != 0)
                return false;
        }

        return true;
    }

    /// <summary>Creates the directory and applies the ACL. Needs an elevated process.</summary>
    public static void Ensure(string path)
    {
        var info = Directory.CreateDirectory(path);
        info.SetAccessControl(BuildSecurity());
    }
}
