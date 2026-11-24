using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class UserAllowStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-ua-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSigner _signer = new();

    public UserAllowStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File1 => Path.Combine(_dir, "p1.json");

    private static UserAllowDocument Doc(long version = 1, params UserAllowEntry[] entries) => new("p1", @"C:\repo", version, entries, "");

    [Fact]
    public void ASavedDocument_LoadsVerified_AndNormalizesPaths()
    {
        UserAllowStore.Save(File1, Doc(1, new UserAllowEntry(@"scripts\a.ps1", ["-x"], "fp")), _signer);

        var loaded = UserAllowStore.Load(File1, _signer);

        Assert.True(loaded.Ok);
        Assert.Equal("scripts/a.ps1", Assert.Single(loaded.Document!.Entries).Path);
    }

    [Fact]
    public void AMissingFile_IsOkWithNoDocument_AndTamperingIsRefused()
    {
        Assert.Null(UserAllowStore.Load(File1, _signer).Document);
        Assert.True(UserAllowStore.Load(File1, _signer).Ok);

        UserAllowStore.Save(File1, Doc(1, new UserAllowEntry("a.ps1", [], "fp")), _signer);
        File.WriteAllText(File1, File.ReadAllText(File1).Replace("\"fp\"", "\"other\""));

        var tampered = UserAllowStore.Load(File1, _signer);

        Assert.False(tampered.Ok);
        Assert.Null(tampered.Document);
    }

    [Fact]
    public void Usable_ChecksProjectCheckoutAndVersion()
    {
        UserAllowStore.Save(File1, Doc(3, new UserAllowEntry("a.ps1", [], "fp")), _signer);

        Assert.NotNull(UserAllowStore.Usable(File1, _signer, "p1", @"C:\repo", 3, out _));
        Assert.Null(UserAllowStore.Usable(File1, _signer, "p2", @"C:\repo", 0, out var other));
        Assert.Contains("another project", other);
        Assert.Null(UserAllowStore.Usable(File1, _signer, "p1", @"C:\elsewhere", 0, out var checkout));
        Assert.Contains("another checkout", checkout);
        Assert.Null(UserAllowStore.Usable(File1, _signer, "p1", @"C:\repo", 4, out var replay));
        Assert.Contains("replay", replay);
    }

    [Fact]
    public void With_ReplacesAnEntryForTheSameScriptAndArguments_AndKeepsOthers()
    {
        var first = Doc(1, new UserAllowEntry("a.ps1", ["-x"], "old"), new UserAllowEntry("b.ps1", [], "b"));

        var next = UserAllowStore.With(first, "p1", @"C:\repo", 2, new UserAllowEntry("a.ps1", ["-x"], "new"));

        Assert.Equal(2, next.Entries.Count);
        Assert.Equal("new", next.Entries.Single(e => e.Path == "a.ps1").Fingerprint);
        Assert.Equal(2, next.Version);
    }

    [Fact]
    public void Authorize_NeedsTheExactScriptArgumentsAndFingerprint()
    {
        var doc = Doc(1, new UserAllowEntry("a.ps1", ["-x"], "fp"));

        Assert.True(UserAllowStore.Authorize(doc, "a.ps1", ["-x"], "fp").Allowed);
        Assert.False(UserAllowStore.Authorize(doc, "a.ps1", ["-x"], "other").Allowed);
        Assert.False(UserAllowStore.Authorize(doc, "a.ps1", [], "fp").Allowed);
        Assert.False(UserAllowStore.Authorize(null, "a.ps1", ["-x"], "fp").Allowed);
    }
}