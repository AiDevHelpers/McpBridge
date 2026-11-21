using System.Security.AccessControl;
using System.Security.Principal;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RequesterFoldersTests : IDisposable
{
    private const string Owner = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-folders-" + Guid.NewGuid());

    /// <summary>
    /// The protected ACL excludes the unelevated test user, so cleanup first grants it back. The creator
    /// owns these folders, so it may rewrite their ACL.
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(_base)) return;

        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        RestoreAccess(_base, me);
        Directory.Delete(_base, recursive: true);
    }

    /// <summary>Restores the folder's access before listing it, since a locked folder cannot be listed.</summary>
    private static void RestoreAccess(string path, System.Security.Principal.SecurityIdentifier me)
    {
        var info = new DirectoryInfo(path);
        var sec = info.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(sec);

        foreach (var child in info.GetDirectories())
        {
            RestoreAccess(child.FullName, me);
        }
    }

    private static List<FileSystemAccessRule> Rules(DirectorySecurity sec) =>
        sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    private static FileSystemAccessRule For(DirectorySecurity sec, SecurityIdentifier who) =>
        Assert.Single(Rules(sec), r => r.IdentityReference.Value == who.Value && r.AccessControlType == AccessControlType.Allow);

    [Fact]
    public void Request_folder_inheritance_is_protected()
    {
        Assert.True(RequesterFolders.RequestFolderSecurity(Owner).AreAccessRulesProtected);
    }

    [Fact]
    public void Request_folder_lets_the_owner_modify_and_gives_nobody_else_access()
    {
        var sec = RequesterFolders.RequestFolderSecurity(Owner);

        // .NET reads Modify back with Synchronize added.
        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, For(sec, new SecurityIdentifier(Owner)).FileSystemRights);
        Assert.Equal(FileSystemRights.FullControl, For(sec, Admins).FileSystemRights);
        Assert.Equal(FileSystemRights.FullControl, For(sec, LocalSystem).FileSystemRights);
        var allowed = new[] { Owner, Admins.Value, LocalSystem.Value };
        Assert.All(Rules(sec), r => Assert.Contains(r.IdentityReference.Value, allowed));
        Assert.DoesNotContain(Rules(sec), r => r.IdentityReference.Value == Users.Value);
        Assert.DoesNotContain(Rules(sec), r => r.IdentityReference.Value == Everyone.Value);
    }

    [Fact]
    public void Results_folder_lets_the_owner_read_only_and_cannot_write_answers()
    {
        var sec = RequesterFolders.ResultFolderSecurity(Owner);

        var ownerRule = For(sec, new SecurityIdentifier(Owner));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, ownerRule.FileSystemRights);
        Assert.Equal(FileSystemRights.FullControl, For(sec, Admins).FileSystemRights);
        Assert.DoesNotContain(Rules(sec), r => r.IdentityReference.Value == Users.Value);
    }

    [Fact]
    public void Results_folder_inheritance_is_protected()
    {
        Assert.True(RequesterFolders.ResultFolderSecurity(Owner).AreAccessRulesProtected);
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("")]
    [InlineData("Everyone")]
    public void Owner_must_be_a_real_sid(string sid)
    {
        Assert.ThrowsAny<Exception>(() => RequesterFolders.RequestFolderSecurity(sid));
        Assert.ThrowsAny<Exception>(() => RequesterFolders.ResultFolderSecurity(sid));
    }

    [Fact]
    public void Ensure_creates_both_folders_for_the_owner_with_their_acls()
    {
        var config = new ScriptHelperConfig(Path.Combine(_base, "script-helper"));

        RequesterFolders.Ensure(config, Owner);

        var queue = Path.Combine(config.QueueDirectory, Owner);
        var results = Path.Combine(config.ResultsDirectory, Owner);
        Assert.True(Directory.Exists(queue));
        Assert.True(Directory.Exists(results));
        var queueAcl = new DirectoryInfo(queue).GetAccessControl();
        var resultsAcl = new DirectoryInfo(results).GetAccessControl();
        Assert.Contains(queueAcl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference.Value == Owner && r.FileSystemRights.HasFlag(FileSystemRights.Modify));
        Assert.Contains(resultsAcl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference.Value == Owner && !r.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }

    [Fact]
    public void Ensure_is_idempotent()
    {
        var config = new ScriptHelperConfig(Path.Combine(_base, "script-helper"));

        RequesterFolders.Ensure(config, Owner);
        RequesterFolders.Ensure(config, Owner);

        Assert.True(Directory.Exists(Path.Combine(config.QueueDirectory, Owner)));
    }
}
