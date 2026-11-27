using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class McpDataFolderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "mcpdata-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreatesTheFolderForTheUserSystemAndAdministratorsOnly()
    {
        var user = WindowsIdentity.GetCurrent().User!;

        McpDataFolder.Ensure(_path, user);

        var rules = new DirectoryInfo(_path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .ToList();
        var allowed = rules.Select(r => r.IdentityReference.Value).ToHashSet();

        Assert.Contains(user.Value, allowed);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, allowed);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value, allowed);
        Assert.All(rules, r => Assert.False(r.IsInherited));
    }

    [Fact]
    public void EnsureCanRunTwice()
    {
        var user = WindowsIdentity.GetCurrent().User!;

        McpDataFolder.Ensure(_path, user);
        McpDataFolder.Ensure(_path, user);

        Assert.True(Directory.Exists(_path));
    }
}
