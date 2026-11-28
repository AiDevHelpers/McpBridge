using System.Diagnostics;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdatePathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updguard-" + Guid.NewGuid().ToString("N"));

    public UpdatePathGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        string junction = Path.Combine(_root, "link");
        if (Directory.Exists(junction))
        {
            Directory.Delete(junction, recursive: false); // removes the link only, never its target
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x")]
    public void RejectsRootedPath(string path)
    {
        Assert.False(UpdatePathGuard.TryResolve(_root, path, out _));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("bin/../../outside.txt")]
    public void RejectsParentSegment(string path)
    {
        Assert.False(UpdatePathGuard.TryResolve(_root, path, out _));
    }

    [Fact]
    public void RejectsPathOutsideRoot()
    {
        Assert.False(UpdatePathGuard.TryResolve(_root, "bin/../../x", out _));
    }

    [Fact]
    public void AcceptsAPlainRelativePath()
    {
        Assert.True(UpdatePathGuard.TryResolve(_root, "bin/app.exe", out string full));
        Assert.StartsWith(Path.GetFullPath(_root), full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsJunctionOnPath()
    {
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        string junction = Path.Combine(_root, "link");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!
            .WaitForExit();

        Assert.False(UpdatePathGuard.TryResolve(_root, "link/app.exe", out _));
    }
}

