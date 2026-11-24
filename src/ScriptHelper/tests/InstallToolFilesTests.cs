using AiDevHelpers.Common;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class InstallToolFilesTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-tools-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptHelperConfig _config;

    private sealed class Yes : AiDevHelpers.Common.Contracts.IConfirmer
    {
        public List<string> Prompts { get; } = [];

        public bool Confirm(string prompt)
        {
            Prompts.Add(prompt);
            return true;
        }
    }

    public InstallToolFilesTests()
    {
        Directory.CreateDirectory(_base);
        _config = new ScriptHelperConfig(Path.Combine(_base, "data"));
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private SetupCommands Commands(bool elevated, Yes confirmer) => new(
        _config, new FakeElevation(elevated), new FakeVolumes(), new FakeInspector(), new FakeInterpreter(), new FakeSigner(), confirmer, @"C:\Users\tester",
        p => Directory.CreateDirectory(p));

    [Fact]
    public void TheScript_HasTheTableOfPackages_AndRefusesAnythingElse()
    {
        var script = InstallToolFiles.Script();

        foreach (var tool in InstallableTools.All)
            Assert.Contains($"'{tool.Name}' = '{tool.PackageId}'", script);

        Assert.Contains("--exact --source winget", script);
        Assert.Contains("exit 2", script);
        Assert.DoesNotContain("\r", script);
        Assert.DoesNotContain("Invoke-Expression", script);
    }

    [Fact]
    public async Task TheScript_IsValidPowerShell()
    {
        var file = Path.Combine(_base, InstallToolFiles.ScriptName);
        File.WriteAllText(file, InstallToolFiles.Script());

        var result = await AiDevHelpers.Common.Processes.CommandRunner.RunAsync(new AiDevHelpers.Common.Processes.CommandRequest
        {
            FileName = "powershell.exe",
            Arguments =
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:SCRIPT_UNDER_TEST, [ref]$null, [ref]$e); if ($e.Count) { $e | ForEach-Object { $_.Message }; exit 1 }",
            ],
            Environment = new Dictionary<string, string?> { ["SCRIPT_UNDER_TEST"] = file },
        });

        Assert.True(result.Success, result.StdOut + result.StdErr);
    }

    [Fact]
    public void TheAllowlist_AllowsEachToolByNameOnly()
    {
        var manifest = ManifestParser.Parse(InstallToolFiles.Manifest());
        Assert.True(manifest.Ok, manifest.Reason);

        foreach (var tool in InstallableTools.All)
            Assert.True(Allowlist.Evaluate(manifest.Entries, InstallToolFiles.ScriptName, [tool.Name]).Allowed, tool.Name);

        Assert.False(Allowlist.Evaluate(manifest.Entries, InstallToolFiles.ScriptName, ["Microsoft.PowerShell"]).Allowed);
        Assert.False(Allowlist.Evaluate(manifest.Entries, InstallToolFiles.ScriptName, ["pwsh", "extra"]).Allowed);
        Assert.False(Allowlist.Evaluate(manifest.Entries, InstallToolFiles.ScriptName, []).Allowed);
        Assert.False(Allowlist.Evaluate(manifest.Entries, "other.ps1", ["pwsh"]).Allowed);
    }

    [Fact]
    public void SetupTools_WritesTheFiles_RegistersTheFolder_AndApprovesAfterTheConfirmation()
    {
        var confirmer = new Yes();

        var result = Commands(elevated: true, confirmer).InstallTools();

        Assert.True(result.Ok, result.Reason);
        var root = InstallToolFiles.ToolsRoot(_config);
        Assert.Equal(InstallToolFiles.Script(), File.ReadAllText(Path.Combine(root, InstallToolFiles.ScriptName)));
        Assert.Equal(InstallToolFiles.Manifest(), File.ReadAllText(Path.Combine(root, InstallToolFiles.ManifestName)));
        Assert.Contains(InstallToolFiles.ScriptName, Assert.Single(confirmer.Prompts));
        Assert.True(File.Exists(_config.RegistryPath));
    }

    [Fact]
    public void SetupTools_NeedsAnElevatedShell_AndWritesNothingWithout()
    {
        var result = Commands(elevated: false, new Yes()).InstallTools();

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(InstallToolFiles.ToolsRoot(_config)));
    }

    [Fact]
    public void TheSetupCommand_IsWiredIntoTheCli()
    {
        var tools = new FakeTools();

        var exit = SetupCli.Run(["tools"], new NoSetup(), new StringWriter(), tools: tools);

        Assert.Equal(0, exit);
        Assert.Equal(1, tools.Calls);
    }

    private sealed class NoSetup : AiDevHelpers.Common.Contracts.ISetupCommands
    {
        public AiDevHelpers.Common.Contracts.SetupResult Register(string root) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.SetupResult Approve(string root, string relativeScript) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.ApprovalPreview PrepareApproval(string root, string relativeScript) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.SetupResult ConfirmApproval(string root, string relativeScript, string fingerprint, string allowlistHash) => throw new NotSupportedException();

        public AiDevHelpers.Common.Contracts.SetupResult AllowAll(string root) => throw new NotSupportedException();
        public AiDevHelpers.Common.Contracts.SetupResult AllowAllApproved(string root, string approvedHash) => throw new NotSupportedException();
        public AiDevHelpers.Common.Contracts.SetupResult PruneStale(string root) => throw new NotSupportedException();
    }

    private sealed class FakeTools : IToolCommands
    {
        public int Calls { get; private set; }

        public AiDevHelpers.Common.Contracts.SetupResult InstallTools()
        {
            Calls++;
            return new AiDevHelpers.Common.Contracts.SetupResult(true, "done");
        }
    }
}
