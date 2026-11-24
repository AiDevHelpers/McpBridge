using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class SetupAllowScriptTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-allowscript-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ScriptHelperConfig _config;
    private readonly FakeSigner _signer = new();

    private sealed class Answer(bool yes) : AiDevHelpers.Common.Contracts.IConfirmer
    {
        public List<string> Prompts { get; } = [];

        public bool Confirm(string prompt)
        {
            Prompts.Add(prompt);
            return yes;
        }
    }

    public SetupAllowScriptTests()
    {
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllText(Path.Combine(_root, "scripts", "a.ps1"), "Write-Output a");
        File.WriteAllText(Path.Combine(_root, "scripts", "b.ps1"), "Write-Output b");
        _config = new ScriptHelperConfig(Path.Combine(_base, "data"));
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private SetupCommands Commands(Answer answer, bool elevated = true) => new(
        _config, new FakeElevation(elevated), new FakeVolumes(), new FakeInspector(), new FakeInterpreter(), _signer, answer, @"C:\Users\tester",
        p => Directory.CreateDirectory(p));

    private string Register(SetupCommands c)
    {
        var r = c.Register(_root);
        Assert.True(r.Ok, r.Reason);
        return r.ProjectId!;
    }

    [Fact]
    public void AYes_RecordsTheScriptWithItsArgumentsAndFingerprint_AndLeavesTheRepoAlone()
    {
        var answer = new Answer(true);
        var c = Commands(answer);
        var id = Register(c);
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToList();

        var result = c.AllowScript(_root, "scripts/a.ps1", ["-Fast"]);

        Assert.True(result.Ok, result.Reason);
        var doc = UserAllowStore.Load(_config.UserAllowPath(id), _signer).Document!;
        var entry = Assert.Single(doc.Entries);
        Assert.Equal("scripts/a.ps1", entry.Path);
        Assert.Equal(["-Fast"], entry.Args);
        Assert.False(string.IsNullOrEmpty(entry.Fingerprint));
        Assert.Equal(1, c.Versions.Get(UserAllowStore.VersionKey(id)));
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToList());
        var prompt = Assert.Single(answer.Prompts);
        Assert.Contains("scripts/a.ps1", prompt);
        Assert.Contains("-Fast", prompt);
        Assert.Contains("repo is not changed", prompt);
    }

    [Fact]
    public void TheCliVerb_PassesTheScriptAndItsArgumentsThrough()
    {
        var allow = new RecordingAllow();

        var exit = SetupCli.Run(["allow-script", @"C:\repo", "scripts/a.ps1", "-Fast", "x y"], new NoSetup(), new StringWriter(), allow: allow);

        Assert.Equal(0, exit);
        Assert.Equal((@"C:\repo", "scripts/a.ps1"), (allow.Root, allow.Script));
        Assert.Equal(["-Fast", "x y"], allow.Args);
        Assert.Equal(2, SetupCli.Run(["allow-script", @"C:\repo"], new NoSetup(), new StringWriter(), allow: allow));
    }

    private sealed class RecordingAllow : IAllowCommands
    {
        public string? Root { get; private set; }

        public string? Script { get; private set; }

        public IReadOnlyList<string> Args { get; private set; } = [];

        public AiDevHelpers.Common.Contracts.SetupResult AllowScript(string root, string relativeScript, IReadOnlyList<string> args)
        {
            (Root, Script, Args) = (root, relativeScript, args);
            return new AiDevHelpers.Common.Contracts.SetupResult(true, "allowed");
        }
    }

    private sealed class NoSetup : AiDevHelpers.Common.Contracts.ISetupCommands
    {
        public AiDevHelpers.Common.Contracts.SetupResult Register(string root) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.SetupResult Approve(string root, string relativeScript) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.ApprovalPreview PrepareApproval(string root, string relativeScript) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.SetupResult ConfirmApproval(string root, string relativeScript, string fingerprint, string allowlistHash) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.SetupResult AllowAll(string root) => throw new NotSupportedException();
    }

    [Fact]
    public void ANo_WritesNothing()
    {
        var c = Commands(new Answer(false));
        var id = Register(c);

        var result = c.AllowScript(_root, "scripts/a.ps1", []);

        Assert.False(result.Ok);
        Assert.False(File.Exists(_config.UserAllowPath(id)));
    }

    [Fact]
    public void ASecondScript_IsAddedWithoutDroppingTheFirst_AndTheVersionMovesForward()
    {
        var c = Commands(new Answer(true));
        var id = Register(c);

        c.AllowScript(_root, "scripts/a.ps1", []);
        c.AllowScript(_root, "scripts/b.ps1", []);

        var doc = UserAllowStore.Load(_config.UserAllowPath(id), _signer).Document!;
        Assert.Equal(["scripts/a.ps1", "scripts/b.ps1"], doc.Entries.Select(e => e.Path).ToList());
        Assert.Equal(2, doc.Version);
    }

    [Fact]
    public void NotElevatedNotRegisteredOrOutsideTheRepo_AreRefused_WithoutAsking()
    {
        var answer = new Answer(true);

        Assert.False(Commands(answer, elevated: false).AllowScript(_root, "scripts/a.ps1", []).Ok);
        var c = Commands(answer);
        Assert.Contains("not registered", c.AllowScript(_root, "scripts/a.ps1", []).Reason);
        Register(c);
        Assert.False(c.AllowScript(_root, @"..\outside.ps1", []).Ok);
        Assert.False(c.AllowScript(_root, "scripts/missing.ps1", []).Ok);
        Assert.Empty(answer.Prompts);
    }

    [Fact]
    public void AnExistingFileThatCannotBeTrusted_IsNotOverwritten()
    {
        var c = Commands(new Answer(true));
        var id = Register(c);
        c.AllowScript(_root, "scripts/a.ps1", []);
        var path = _config.UserAllowPath(id);
        var tampered = File.ReadAllText(path).Replace("scripts/a.ps1", "scripts/evil.ps1");
        File.WriteAllText(path, tampered);

        var result = c.AllowScript(_root, "scripts/b.ps1", []);

        Assert.False(result.Ok);
        Assert.Contains("not trusted", result.Reason);
        Assert.Equal(tampered, File.ReadAllText(path));
    }
}
