using AiDevHelpers.Common;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class SetupAllowAllTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-allowall-" + Guid.NewGuid());
    private readonly string _data;
    private readonly string _root;
    private readonly FakeElevation _elevation = new(true);
    private readonly FakeSigner _signer = new();
    private readonly ScriptHelperConfig _config;

    // Answers yes to the first N confirmations, then no.
    private sealed class CountingConfirmer(int yes) : AiDevHelpers.Common.Contracts.IConfirmer
    {
        public List<string> Prompts { get; } = [];

        public bool Confirm(string prompt)
        {
            Prompts.Add(prompt);
            return Prompts.Count <= yes;
        }
    }

    public SetupAllowAllTests()
    {
        _data = Path.Combine(_base, "data");
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        _config = new ScriptHelperConfig(_data);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private SetupCommands Commands(CountingConfirmer confirmer) => new(
        _config, _elevation, new FakeVolumes(), new FakeInspector(), new FakeInterpreter(), _signer, confirmer, @"C:\Users\tester",
        p => Directory.CreateDirectory(p));

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".aidevtools"));
        File.WriteAllText(RepoSettingsFile.SettingsPath(_root), json);
    }

    private static string Register(SetupCommands c, string root)
    {
        var r = c.Register(root);
        Assert.True(r.Ok, r.Reason);
        return r.ProjectId!;
    }

    [Fact]
    public void NotElevated_WritesNothing()
    {
        var elevation = new FakeElevation(false);
        var c = new SetupCommands(_config, elevation, new FakeVolumes(), new FakeInspector(), new FakeInterpreter(), _signer, new CountingConfirmer(2), @"C:\Users\tester", p => Directory.CreateDirectory(p));
        WriteSettings("""{"allowAll":true}""");

        var r = c.AllowAll(_root);

        Assert.False(r.Ok);
        Assert.False(Directory.Exists(_config.AllowAllDirectory));
    }

    [Fact]
    public void UnregisteredProject_IsRefused()
    {
        WriteSettings("""{"allowAll":true}""");

        var r = Commands(new CountingConfirmer(2)).AllowAll(_root);

        Assert.False(r.Ok);
        Assert.Contains("not registered", r.Reason);
    }

    [Fact]
    public void SettingsThatDoNotRequestAllowAll_AreRefused()
    {
        var confirmer = new CountingConfirmer(2);
        var c = Commands(confirmer);
        Register(c, _root);
        WriteSettings("""{"IdeTools":{}}""");

        var r = c.AllowAll(_root);

        Assert.False(r.Ok);
        Assert.Empty(confirmer.Prompts);
    }

    [Fact]
    public void DecliningTheFirstConfirmation_WritesNothing()
    {
        var confirmer = new CountingConfirmer(0);
        var c = Commands(confirmer);
        var id = Register(c, _root);
        WriteSettings("""{"allowAll":true}""");

        var r = c.AllowAll(_root);

        Assert.False(r.Ok);
        Assert.Single(confirmer.Prompts);
        Assert.False(File.Exists(_config.AllowAllPath(id)));
    }

    [Fact]
    public void DecliningTheSecondConfirmation_WritesNothing()
    {
        var confirmer = new CountingConfirmer(1);
        var c = Commands(confirmer);
        var id = Register(c, _root);
        WriteSettings("""{"allowAll":true}""");

        var r = c.AllowAll(_root);

        Assert.False(r.Ok);
        Assert.Equal(2, confirmer.Prompts.Count);
        Assert.False(File.Exists(_config.AllowAllPath(id)));
    }

    [Fact]
    public void BothConfirmations_WriteASignedApprovalAndTheUserPointer()
    {
        var confirmer = new CountingConfirmer(2);
        var c = Commands(confirmer);
        var id = Register(c, _root);
        WriteSettings("""{"allowAll":true}""");

        var r = c.AllowAll(_root);

        Assert.True(r.Ok, r.Reason);
        var hash = RepoSettingsFile.Hash(RepoSettingsFile.SettingsPath(_root));
        Assert.True(AllowAllStore.Check(_config.AllowAllPath(id), _signer, id, _root, 0, hash).Allowed);
        Assert.Contains(id, File.ReadAllText(RepoSettingsFile.UserFilePath(_root)));
        Assert.Contains(".aidevtools/user.json", File.ReadAllLines(Path.Combine(_root, ".gitignore")));
    }

    [Fact]
    public void BothPromptsNameTheRepoAndTheSecondOneIsDistinct()
    {
        var confirmer = new CountingConfirmer(2);
        var c = Commands(confirmer);
        Register(c, _root);
        WriteSettings("""{"allowAll":true}""");

        c.AllowAll(_root);

        Assert.All(confirmer.Prompts, p => Assert.Contains(Path.GetFullPath(_root), p));
        Assert.NotEqual(confirmer.Prompts[0], confirmer.Prompts[1]);
    }

    [Fact]
    public void EditingTheSettingsAfterApproval_InvalidatesIt()
    {
        var c = Commands(new CountingConfirmer(2));
        var id = Register(c, _root);
        WriteSettings("""{"allowAll":true}""");
        c.AllowAll(_root);

        WriteSettings("""{"allowAll":true,"IdeTools":{"IdeTestRunMode":"DotnetTest"}}""");
        var hash = RepoSettingsFile.Hash(RepoSettingsFile.SettingsPath(_root));

        Assert.False(AllowAllStore.Check(_config.AllowAllPath(id), _signer, id, _root, 0, hash).Allowed);
    }
}
