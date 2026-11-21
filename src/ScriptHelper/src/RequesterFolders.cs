using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Owner-only queue and results folders for one requester SID, created when the project is
/// registered. The request folder lets the owner write requests; the results folder lets the owner
/// read answers only. Administrators and SYSTEM have full control of both. Nothing is inherited,
/// so no other user (including Users and Everyone) gets access.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RequesterFolders
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static DirectorySecurity RequestFolderSecurity(string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid);
        return Build(owner, FileSystemRights.Modify);
    }

    public static DirectorySecurity ResultFolderSecurity(string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid);
        return Build(owner, FileSystemRights.ReadAndExecute);
    }

    public static void Ensure(ScriptHelperConfig config, string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid).Value;

        var queue = Directory.CreateDirectory(Path.Combine(config.QueueDirectory, owner));
        queue.SetAccessControl(RequestFolderSecurity(owner));

        var results = Directory.CreateDirectory(Path.Combine(config.ResultsDirectory, owner));
        results.SetAccessControl(ResultFolderSecurity(owner));
    }

    private static DirectorySecurity Build(SecurityIdentifier owner, FileSystemRights ownerRights)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        // The owner is left as the creator's: assigning BUILTIN\Administrators needs elevation.
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(owner, ownerRights, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }
}
