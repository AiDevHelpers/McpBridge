using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class SetupScanTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-setupscan-" + Guid.NewGuid());
    private readonly string _data;
    private readonly string _root;

    public SetupScanTests()
    {
        _data = Path.Combine(_base, "data");
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        Write(".elevated.json", """{ "scripts": [ { "path": "scripts/listed.ps1" } ] }""");
        Write("scripts/listed.ps1", "x");
        Write("scripts/new.ps1", "x");
        Write(".vscode/launch.json", """{ "configurations": [ { "program": "scripts/listed.ps1" }, { "program": "scripts/new.ps1" } ] }""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private SetupCommands Commands(bool elevated = true) => new(
        new ScriptHelperConfig(_data), new FakeElevation(elevated), new FakeVolumes(), new FakeInspector(), new FakeInterpreter(),
        new FakeSigner(), new FakeConfirmer(), @"C:\Users\tester", p => Directory.CreateDirectory(p));

    private SetupCommands Registered()
    {
        var c = Commands();
        Assert.True(c.Register(_root).Ok);
        return c;
    }

    [Fact]
    public void Scan_ReportsListedAndNewScripts()
    {
        var report = Registered().Scan(_root);

        Assert.True(report.Ok, report.Reason);
        Assert.True(report.Scripts.Single(s => s.Path == "scripts/listed.ps1").Listed);
        Assert.False(report.Scripts.Single(s => s.Path == "scripts/new.ps1").Listed);
    }

    [Fact]
    public void Scan_NeedsAnElevatedShellAndARegisteredProject()
    {
        Assert.False(Commands(elevated: false).Scan(_root).Ok);
        Assert.Contains("not registered", Commands().Scan(_root).Reason);
    }

    [Fact]
    public void AddToAllowlist_AddsOnlyTheScriptsTheUserConfirms()
    {
        var c = Registered();

        var results = c.AddToAllowlist(_root, path => path == "scripts/new.ps1");

        Assert.Single(results, r => r.Ok);
        var manifest = ManifestParser.Parse(File.ReadAllText(Path.Combine(_root, ".elevated.json")));
        Assert.Contains(manifest.Entries, e => e.Path == "scripts/new.ps1");
    }

    [Fact]
    public void AddToAllowlist_DeclinedAddsNothing()
    {
        var c = Registered();
        var before = File.ReadAllText(Path.Combine(_root, ".elevated.json"));

        var results = c.AddToAllowlist(_root, _ => false);

        Assert.Empty(results);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, ".elevated.json")));
    }

    [Fact]
    public void AddToAllowlist_NeverAsksAboutAScriptThatIsAlreadyListed()
    {
        var asked = new List<string>();

        Registered().AddToAllowlist(_root, p =>
        {
            asked.Add(p);
            return false;
        });

        Assert.Equal(["scripts/new.ps1"], asked);
    }

    [Fact]
    public void AddedScripts_AreUnsignedUntilApproved_AndTheAllowlistChangeIsReported()
    {
        var c = Registered();
        c.AddToAllowlist(_root, _ => true);

        var report = c.Verify(_root);

        Assert.Equal(ApprovalStatus.Unsigned, report.Scripts.Single(s => s.Path == "scripts/new.ps1").Status);
    }
}
