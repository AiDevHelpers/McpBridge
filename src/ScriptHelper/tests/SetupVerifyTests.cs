using AiDevHelpers.Common;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class SetupVerifyTests : IDisposable
{
    private const string Script = "scripts/debug/reset.ps1";
    private const string Other = "scripts/debug/other.ps1";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-verify-" + Guid.NewGuid());
    private readonly string _data;
    private readonly string _root;
    private readonly FakeSigner _signer = new();
    private readonly FakeConfirmer _confirmer = new();

    public SetupVerifyTests()
    {
        _data = Path.Combine(_base, "data");
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" } ] }""");
        WriteScript(Script, "Write-Output 'reset'");
        WriteScript(Other, "Write-Output 'other'");
        WriteScript("scripts/unlisted/x.ps1", "Write-Output 'x'");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private SetupCommands Commands() => new(
        new ScriptHelperConfig(_data), new FakeElevation(true), new FakeVolumes(), new FakeInspector(), new FakeInterpreter(),
        _signer, _confirmer, @"C:\Users\tester", p => Directory.CreateDirectory(p));

    private void WriteManifest(string json) => File.WriteAllText(Path.Combine(_root, ".elevated.json"), json);

    private void WriteScript(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private SetupCommands Registered()
    {
        var c = Commands();
        Assert.True(c.Register(_root).Ok);
        return c;
    }

    private static ScriptStatus Find(VerifyReport report, string path) => report.Scripts.Single(s => s.Path == path);

    [Fact]
    public void ListedScriptsWithoutAnApproval_AreUnsigned_AndUnlistedFilesAreIgnored()
    {
        var report = Registered().Verify(_root);

        Assert.True(report.Ok, report.Reason);
        Assert.Equal(ApprovalStatus.Unsigned, Find(report, Script).Status);
        Assert.Equal(ApprovalStatus.Unsigned, Find(report, Other).Status);
        Assert.DoesNotContain(report.Scripts, s => s.Path.Contains("unlisted"));
    }

    [Fact]
    public void AnApprovedScript_IsApproved_AndTheRestAreStillUnsigned()
    {
        var c = Registered();
        Assert.True(c.Approve(_root, Script).Ok);

        var report = c.Verify(_root);

        Assert.Equal(ApprovalStatus.Approved, Find(report, Script).Status);
        Assert.Equal(ApprovalStatus.Unsigned, Find(report, Other).Status);
    }

    [Fact]
    public void AScriptEditedAfterApproval_IsChanged()
    {
        var c = Registered();
        c.Approve(_root, Script);
        WriteScript(Script, "Write-Output 'tampered'");

        Assert.Equal(ApprovalStatus.Changed, Find(c.Verify(_root), Script).Status);
    }

    [Fact]
    public void AnEditedAllowlist_ReportsEveryListedScriptAsAllowlistChanged()
    {
        var c = Registered();
        c.Approve(_root, Script);
        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" }, { "path": "scripts/extra/*.ps1" } ] }""");

        var report = c.Verify(_root);

        Assert.Equal(ApprovalStatus.AllowlistChanged, Find(report, Script).Status);
        Assert.Equal(ApprovalStatus.AllowlistChanged, Find(report, Other).Status);
    }

    [Fact]
    public void UnregisteredProject_IsRefused()
    {
        var report = Commands().Verify(_root);

        Assert.False(report.Ok);
        Assert.Contains("not registered", report.Reason);
    }

    [Fact]
    public void AllowAllActive_IsReported()
    {
        var c = Registered();
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
        File.WriteAllText(RepoSettingsFile.SettingsPath(_root), """{"allowAll":true}""");
        var confirmer = new FakeConfirmer();
        var withAllowAll = new SetupCommands(
            new ScriptHelperConfig(_data), new FakeElevation(true), new FakeVolumes(), new FakeInspector(), new FakeInterpreter(),
            _signer, confirmer, @"C:\Users\tester", p => Directory.CreateDirectory(p));
        Assert.True(withAllowAll.AllowAll(_root).Ok);

        Assert.True(c.Verify(_root).AllowAllActive);
    }

    [Fact]
    public void ApproveUnsigned_ApprovesEachScriptTheUserConfirms()
    {
        var c = Registered();

        var results = c.ApproveUnsigned(_root, path => path == Script);

        Assert.Equal(ApprovalStatus.Approved, Find(c.Verify(_root), Script).Status);
        Assert.Equal(ApprovalStatus.Unsigned, Find(c.Verify(_root), Other).Status);
        Assert.Single(results, r => r.Ok);
    }
}
