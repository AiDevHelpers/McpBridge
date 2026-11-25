using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class AllowAllStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "allowall-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSigner _signer = new();

    public AllowAllStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path1 => Path.Combine(_dir, "p1.json");

    private void SaveDefault(long version = 3) =>
        AllowAllStore.Save(Path1, new AllowAllApproval("p1", @"C:\repo", version, "HASH", ""), _signer);

    [Fact]
    public void MatchingApproval_IsAllowed()
    {
        SaveDefault();

        Assert.True(AllowAllStore.Check(Path1, _signer, "p1", @"C:\repo", 3, "HASH").Allowed);
    }

    [Fact]
    public void MissingApproval_IsDenied()
    {
        var decision = AllowAllStore.Check(Path1, _signer, "p1", @"C:\repo", 0, "HASH");

        Assert.False(decision.Allowed);
        Assert.Contains("not approved", decision.Reason);
    }

    [Fact]
    public void ChangedSettingsHash_IsDenied()
    {
        SaveDefault();

        var decision = AllowAllStore.Check(Path1, _signer, "p1", @"C:\repo", 3, "OTHER");

        Assert.False(decision.Allowed);
        Assert.Contains("settings changed", decision.Reason);
    }

    [Fact]
    public void AnotherProject_IsDenied()
    {
        SaveDefault();

        Assert.False(AllowAllStore.Check(Path1, _signer, "p2", @"C:\repo", 3, "HASH").Allowed);
    }

    [Fact]
    public void AnotherCheckout_IsDenied()
    {
        SaveDefault();

        Assert.False(AllowAllStore.Check(Path1, _signer, "p1", @"C:\other", 3, "HASH").Allowed);
    }

    [Fact]
    public void OlderVersionThanLastSeen_IsDeniedAsReplay()
    {
        SaveDefault(version: 2);

        var decision = AllowAllStore.Check(Path1, _signer, "p1", @"C:\repo", 5, "HASH");

        Assert.False(decision.Allowed);
        Assert.Contains("replay", decision.Reason);
    }

    [Fact]
    public void TamperedFile_FailsTheSignature()
    {
        SaveDefault();
        File.WriteAllText(Path1, File.ReadAllText(Path1).Replace("HASH", "FORGED"));

        var decision = AllowAllStore.Check(Path1, _signer, "p1", @"C:\repo", 3, "FORGED");

        Assert.False(decision.Allowed);
        Assert.Contains("signature", decision.Reason);
    }

    [Fact]
    public void MalformedFile_IsDenied()
    {
        File.WriteAllText(Path1, "{ not json");

        Assert.False(AllowAllStore.Check(Path1, _signer, "p1", @"C:\repo", 0, "HASH").Allowed);
    }
}
