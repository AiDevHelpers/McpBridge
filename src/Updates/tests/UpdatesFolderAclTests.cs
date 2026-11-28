using System.Security.AccessControl;
using System.Security.Principal;
using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdatesFolderAclTests
{
    // The updates folder is where staged packages wait for the service to install them. Standard users may read it but must not be able
    // to write a package into it. Setting the ACL needs an administrator, so this test is Category=Admin.
    [Fact]
    [Trait("Category", "Admin")]
    public void UpdatesFolderAclRestrictsWrites()
    {
        string dir = Path.Combine(Path.GetTempPath(), "updacl-" + Guid.NewGuid().ToString("N"));
        try
        {
            UpdatesFolderAcl.Apply(dir);

            var rules = new DirectoryInfo(dir).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var userAllow = rules.Where(r => r.IdentityReference.Equals(users) && r.AccessControlType == AccessControlType.Allow).ToList();
            Assert.Single(userAllow);
            // Windows adds Synchronize to every read rule; no write, modify or delete right is granted.
            Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, userAllow[0].FileSystemRights);
            Assert.Contains(rules, r => r.IdentityReference.Equals(admins) && r.FileSystemRights == FileSystemRights.FullControl);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
