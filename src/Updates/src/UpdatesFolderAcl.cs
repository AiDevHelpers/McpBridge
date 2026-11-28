using System.Security.AccessControl;
using System.Security.Principal;

namespace AiDevHelpers.Updates;

/// <summary>
/// Locks the updates folder so only SYSTEM and Administrators can write to it. Standard users may read and execute, which lets the
/// tray see staged packages, but cannot plant one. Inherited rules are removed so a parent ACL cannot reopen the folder.
/// </summary>
public static class UpdatesFolderAcl
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Creates the folder if needed and replaces its ACL. Needs administrator rights.</summary>
    public static void Apply(string folder)
    {
        Directory.CreateDirectory(folder);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));
        new DirectoryInfo(folder).SetAccessControl(security);
    }

    private static FileSystemAccessRule Allow(WellKnownSidType sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, Inherit, PropagationFlags.None, AccessControlType.Allow);
}
