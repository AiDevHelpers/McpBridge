using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace AiDevHelpers.Host.Tests;

public sealed class ServiceScriptsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "svcscripts-" + Guid.NewGuid().ToString("N"));

    public ServiceScriptsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string ScriptsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiDevHelpers.slnx")))
            {
                return Path.Combine(dir.FullName, "scripts");
            }
        }

        throw new DirectoryNotFoundException("AiDevHelpers.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Script(string name) => Path.Combine(ScriptsFolder(), name);

    private static (int ExitCode, string Output) Run(string scriptPath, params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-File", scriptPath }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string Source(bool withHost = true, bool withHttp = true)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        if (withHost)
        {
            File.WriteAllText(Path.Combine(source, "AiDevHelpers.Host.exe"), "host");
        }

        if (withHttp)
        {
            File.WriteAllText(Path.Combine(source, "AiDevHelpers.MCP.Http.exe"), "http");
        }

        return source;
    }

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [Theory]
    [InlineData("service-install")]
    [InlineData("service-uninstall")]
    public void TheCommittedPinMatchesTheScript(string name)
    {
        var script = Script(name + ".ps1");
        var pin = File.ReadAllText(Script(name + ".sha256")).Trim();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(script))), pin, ignoreCase: true);
    }

    [Fact]
    public void InstallDryRunPlansAnAutoStartLocalSystemServiceWithRecoveryActions()
    {
        var (code, output) = Run(Script("service-install.ps1"), "-SourceDirectory", Source(), "-InstallDirectory", Path.Combine(_root, "inst"), "-ServiceName", "AiDevHelpersTest", "-DryRun");

        Assert.Equal(0, code);
        Assert.Contains("PLAN: copy", output);
        Assert.Contains("PLAN: create service AiDevHelpersTest", output);
        Assert.Contains("start=auto", output);
        Assert.Contains("LocalSystem", output);
        Assert.Contains("restart/5000/restart/5000/restart/30000", output);
        Assert.Contains("PLAN: write install-manifest.json", output);
    }

    [Fact]
    public void InstallRefusesAScriptThatDoesNotMatchItsPin()
    {
        File.Copy(Script("service-install.ps1"), Path.Combine(_root, "service-install.ps1"));
        File.Copy(Script("service-install.sha256"), Path.Combine(_root, "service-install.sha256"));
        File.AppendAllText(Path.Combine(_root, "service-install.ps1"), "\n# changed\n");

        var (code, output) = Run(Path.Combine(_root, "service-install.ps1"), "-SourceDirectory", Source(), "-DryRun");

        Assert.Equal(1, code);
        Assert.Contains("does not match the pin", output);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InstallRefusesASourceWithoutBothExecutables(bool withHost, bool withHttp)
    {
        var (code, output) = Run(Script("service-install.ps1"), "-SourceDirectory", Source(withHost, withHttp), "-InstallDirectory", Path.Combine(_root, "inst"), "-DryRun");

        Assert.Equal(1, code);
        Assert.Contains("is missing", output);
    }

    [Fact]
    public void InstallRefusesAnInstallDirectoryThatAUserOwns()
    {
        if (IsElevated())
        {
            return; // An elevated shell creates folders owned by Administrators, so the case cannot be built here.
        }

        var inst = Path.Combine(_root, "inst");
        Directory.CreateDirectory(inst);

        var (code, output) = Run(Script("service-install.ps1"), "-SourceDirectory", Source(), "-InstallDirectory", inst, "-DryRun");

        Assert.Equal(1, code);
        Assert.Contains("is owned by", output);
    }

    [Fact]
    public void InstallRefusesToReplaceAnExistingServiceWithoutReinstall()
    {
        var (code, output) = Run(Script("service-install.ps1"), "-SourceDirectory", Source(), "-InstallDirectory", Path.Combine(_root, "inst"), "-ServiceName", "Spooler", "-DryRun");

        Assert.Equal(1, code);
        Assert.Contains("already installed", output);
    }

    [Fact]
    public void ReinstallDryRunPlansStoppingAndDeletingTheOldService()
    {
        var (code, output) = Run(Script("service-install.ps1"), "-SourceDirectory", Source(), "-InstallDirectory", Path.Combine(_root, "inst"), "-ServiceName", "Spooler", "-Reinstall", "-DryRun");

        Assert.Equal(0, code);
        Assert.Contains("PLAN: stop service Spooler", output);
        Assert.Contains("PLAN: delete service Spooler", output);
    }

    [Fact]
    public void UninstallDryRunPlansStoppingDeletingAndRemovingTheInstallFolder()
    {
        var inst = Path.Combine(_root, "inst");
        Directory.CreateDirectory(inst);
        File.WriteAllText(Path.Combine(inst, "install-manifest.json"), "{}");

        var (code, output) = Run(Script("service-uninstall.ps1"), "-InstallDirectory", inst, "-ServiceName", "Spooler", "-DryRun");

        Assert.Equal(0, code);
        Assert.Contains("PLAN: stop service Spooler", output);
        Assert.Contains("PLAN: delete service Spooler", output);
        Assert.Contains("PLAN: remove directory " + inst, output);
    }

    [Fact]
    public void UninstallRefusesToRemoveAFolderThatIsNotAnInstall()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "important.txt"), "keep");

        var (code, output) = Run(Script("service-uninstall.ps1"), "-InstallDirectory", other, "-ServiceName", "AiDevHelpersTest", "-DryRun");

        Assert.Equal(1, code);
        Assert.Contains("install-manifest.json", output);
        Assert.True(File.Exists(Path.Combine(other, "important.txt")));
    }
}
