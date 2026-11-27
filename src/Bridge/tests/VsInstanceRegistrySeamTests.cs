using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

public sealed class VsInstanceRegistrySeamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsi-" + Guid.NewGuid().ToString("N"));

    public VsInstanceRegistrySeamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Manifest(int pid) =>
        $"{{\"pid\":{pid},\"pipeName\":\"AiDevHelpers.VS.{pid}\",\"solutionPath\":\"C:\\\\work\\\\app.sln\",\"updatedUtc\":\"2026-11-27T10:00:00Z\",\"ide\":\"VisualStudio\",\"logRoots\":[]}}";

    [Fact]
    public void ListRunningInDirectory_KeepsLiveInstances_AndDeletesDeadOnes()
    {
        File.WriteAllText(Path.Combine(_dir, "100.json"), Manifest(100));
        File.WriteAllText(Path.Combine(_dir, "200.json"), Manifest(200));
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");

        var running = VsInstanceRegistry.ListRunning(_dir, pid => pid == 100);

        Assert.Equal(new[] { 100 }, running.Select(i => i.Pid).ToArray());
        Assert.False(File.Exists(Path.Combine(_dir, "200.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "100.json")));
    }

    [Fact]
    public void ListRunningInDirectory_MissingFolder_IsEmpty()
    {
        var running = VsInstanceRegistry.ListRunning(Path.Combine(_dir, "missing"), _ => true);

        Assert.Empty(running);
    }
}

