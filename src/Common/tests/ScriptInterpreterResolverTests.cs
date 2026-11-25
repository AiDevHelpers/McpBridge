using AiDevHelpers.Common.Terminals;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class ScriptInterpreterResolverTests
{
    private static ResolvedInterpreter Resolve(FakeTerminalProbe probe, string script, string? text = null, IReadOnlyDictionary<string, string>? overrides = null, string? repo = null)
    {
        if (text is not null)
            probe.Texts[script] = text;

        var resolved = ScriptInterpreterResolver.Resolve(probe, script, repo, overrides, out var unsupported);
        Assert.Null(unsupported);
        return resolved!;
    }

    private static FakeTerminalProbe WithPwsh()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.Files.Add(@"C:\Program Files\PowerShell\7\pwsh.exe");
        return probe;
    }

    [Fact]
    public void APowerShellScriptWithNoMarker_PrefersPowerShell7_ElseFallsBackToWindowsPowerShell()
    {
        var withSeven = Resolve(WithPwsh(), @"C:\r\a.ps1", "Write-Host hi");
        var without = Resolve(FakeTerminalProbe.Machine(), @"C:\r\a.ps1", "Write-Host hi");

        Assert.Equal("pwsh", withSeven.Name);
        Assert.Equal("powershell", without.Name);
        Assert.Contains("not installed", without.Reason);
    }

    [Theory]
    [InlineData("#Requires -Version 7.0", "pwsh")]
    [InlineData("#requires -version 5.1", "powershell")]
    [InlineData("#Requires -PSEdition Core", "pwsh")]
    [InlineData("#Requires -PSEdition Desktop", "powershell")]
    public void RequiresMarkers_WinOverTheDefault(string marker, string expected) =>
        Assert.Equal(expected, Resolve(WithPwsh(), @"C:\r\a.ps1", marker + "\nWrite-Host hi").Name);

    [Fact]
    public void AScriptThatNeedsPowerShell7_IsNeverRunWithTheWrongOneWhenItIsMissing()
    {
        var resolved = Resolve(FakeTerminalProbe.Machine(), @"C:\r\a.ps1", "#Requires -Version 7\n");

        Assert.Equal("pwsh", resolved.Name);
        Assert.False(resolved.Installed);
        Assert.Contains("winget install Microsoft.PowerShell", resolved.InstallHint);
    }

    [Theory]
    [InlineData(@"C:\r\a.cmd", "cmd")]
    [InlineData(@"C:\r\a.BAT", "cmd")]
    public void CmdAndBatFiles_RunInCmd(string script, string expected) => Assert.Equal(expected, Resolve(FakeTerminalProbe.Machine(), script, "@echo off").Name);

    [Fact]
    public void AShellScript_RunsInGitBash_NeverTheWslLauncher()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.Files.Add(@"C:\Windows\System32\bash.exe");

        var resolved = Resolve(probe, @"C:\r\a.sh", "echo hi");

        Assert.Equal("bash", resolved.Name);
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", resolved.Path);
        Assert.Equal("gitbash", resolved.TerminalTypeId);
    }

    [Fact]
    public void ShellScriptsWithoutGit_ReportAMissingInterpreterWithAHint()
    {
        var probe = new FakeTerminalProbe();

        var resolved = Resolve(probe, @"C:\r\a.sh", "echo hi");

        Assert.False(resolved.Installed);
        Assert.Contains("winget install Git.Git", resolved.InstallHint);
    }

    [Fact]
    public void Python_PrefersTheReposVenv_ThenThePyLauncher_ThenPython_AndSkipsTheStoreStub()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.OnPath["python.exe"] = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe";
        probe.Files.Add(@"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe");
        Assert.False(Resolve(probe, @"C:\r\a.py", "print(1)", repo: @"C:\r").Installed);

        probe.OnPath["py.exe"] = @"C:\Windows\py.exe";
        probe.Files.Add(@"C:\Windows\py.exe");
        Assert.Equal(@"C:\Windows\py.exe", Resolve(probe, @"C:\r\a.py", "print(1)", repo: @"C:\r").Path);

        probe.Files.Add(@"C:\r\.venv\Scripts\python.exe");
        Assert.Equal(@"C:\r\.venv\Scripts\python.exe", Resolve(probe, @"C:\r\a.py", "print(1)", repo: @"C:\r").Path);
    }

    [Fact]
    public void Node_IsFoundOnPath()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.OnPath["node.exe"] = @"C:\nodejs\node.exe";
        probe.Files.Add(@"C:\nodejs\node.exe");

        Assert.Equal(@"C:\nodejs\node.exe", Resolve(probe, @"C:\r\a.mjs", "console.log(1)").Path);
    }

    [Theory]
    [InlineData("#!/usr/bin/env python3\nprint(1)", "python")]
    [InlineData("#!/bin/bash\n", "bash")]
    [InlineData("#!/usr/bin/env pwsh\n", "pwsh")]
    [InlineData("#!/usr/bin/env node\n", "node")]
    public void ANamedInterpreterOnAShebangLine_WinsOverTheExtension(string text, string expected) =>
        Assert.Equal(expected, Resolve(WithPwsh(), @"C:\r\script.txt".Replace("script.txt", "run.ps1"), text).Name);

    [Fact]
    public void AShebangIsMatchedByNameOnly_ThePathItNamesIsIgnored()
    {
        var resolved = Resolve(FakeTerminalProbe.Machine(), @"C:\r\a.sh", "#!/tmp/evil/bash\necho hi\n");

        Assert.Equal("bash", resolved.Name);
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", resolved.Path);
    }

    [Fact]
    public void AnUnknownShebang_IsIgnored_AndTheExtensionDecides()
    {
        Assert.Equal("cmd", Resolve(FakeTerminalProbe.Machine(), @"C:\r\a.cmd", "#!/usr/bin/ruby\n").Name);
    }

    [Fact]
    public void AnOverride_WinsOverEverything_ButOnlyForKnownNames()
    {
        var overrides = new Dictionary<string, string> { [".ps1"] = "powershell", [".cmd"] = @"C:\evil\x.exe" };

        Assert.Equal("powershell", Resolve(WithPwsh(), @"C:\r\a.ps1", "#Requires -Version 7\n", overrides).Name);
        Assert.Equal("cmd", Resolve(WithPwsh(), @"C:\r\a.cmd", "@echo off", overrides).Name);
    }

    [Fact]
    public void AnUnknownExtension_IsUnsupported()
    {
        var resolved = ScriptInterpreterResolver.Resolve(FakeTerminalProbe.Machine(), @"C:\r\a.rb", null, null, out var unsupported);

        Assert.Null(resolved);
        Assert.Contains(".rb", unsupported!.Reason);
    }

    [Fact]
    public void BuildArguments_PassesArgumentsAsDataWithNoShellInBetween()
    {
        var ps = Resolve(WithPwsh(), @"C:\r\a.ps1", "x");
        var cmd = Resolve(FakeTerminalProbe.Machine(), @"C:\r\a.cmd", "x");

        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", @"C:\r\a.ps1", "one two", "&calc"], ps.BuildArguments(@"C:\r\a.ps1", ["one two", "&calc"]));
        Assert.Equal(["/c", @"C:\r\a.cmd", "x"], cmd.BuildArguments(@"C:\r\a.cmd", ["x"]));
    }

    [Fact]
    public void ReadOverrides_ReadsTheInterpretersBlock_AndIgnoresUnknownNamesAndBadFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "interp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".aidevhelpers"));
        try
        {
            File.WriteAllText(RepoSettingsFile.UserFilePath(root), """{ "interpreters": { ".ps1": "powershell", "py": "python", ".sh": "/evil/bash" } }""");

            var overrides = ScriptInterpreterResolver.ReadOverrides(root);

            Assert.Equal("powershell", overrides[".ps1"]);
            Assert.Equal("python", overrides[".py"]);
            Assert.DoesNotContain(".sh", overrides.Keys);

            File.WriteAllText(RepoSettingsFile.UserFilePath(root), "{ broken");
            Assert.Empty(ScriptInterpreterResolver.ReadOverrides(root));
            Assert.Empty(ScriptInterpreterResolver.ReadOverrides(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
