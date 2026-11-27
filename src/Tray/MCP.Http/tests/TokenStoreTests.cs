using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokenstore-" + Guid.NewGuid().ToString("N"));

    public TokenStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string TokenPath => Path.Combine(_root, "token");

    [Fact]
    public void LoadOrCreateWritesALongTokenAndReturnsTheSameOneNextTime()
    {
        var first = TokenStore.LoadOrCreate(TokenPath);
        var second = TokenStore.LoadOrCreate(TokenPath);

        Assert.True(first.Length >= 32);
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(TokenPath).Trim());
    }

    [Fact]
    public void LoadOrCreateRejectsAShortTokenFile()
    {
        File.WriteAllText(TokenPath, "short");

        Assert.Throws<InvalidDataException>(() => TokenStore.LoadOrCreate(TokenPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public void VerifyRejectsMissingAndWrongTokens(string? candidate)
    {
        var token = TokenStore.LoadOrCreate(TokenPath);

        Assert.False(TokenStore.Verify(token, candidate));
    }

    [Fact]
    public void VerifyAcceptsTheExactToken()
    {
        var token = TokenStore.LoadOrCreate(TokenPath);

        Assert.True(TokenStore.Verify(token, token));
    }

    [Fact]
    public void TheTokenFileIsReadableOnlyByTheCurrentUserAndSystem()
    {
        _ = TokenStore.LoadOrCreate(TokenPath);

        var rules = new FileInfo(TokenPath).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference.Value).ToHashSet();

        Assert.Contains(WindowsIdentity.GetCurrent().User!.Value, allowed);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value, allowed);
        Assert.All(rules, r => Assert.False(r.IsInherited));
    }
}
