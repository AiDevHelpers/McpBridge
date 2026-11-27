using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

/// <summary>The listing with an injected folder and process check (the seam the tray uses).</summary>
public sealed class VsInstanceRegistryListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AiDevHelpers.Tests", Guid.NewGuid().ToString("N"));

    public VsInstanceRegistryListTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Manifest(string name, string json) => File.WriteAllText(Path.Combine(_dir, name), json);

    private static string Json(int pid) =>
        $$"""{"Pid":{{pid}},"PipeName":"AiDevHelpers.VS.{{pid}}","SolutionPath":"C:\\src\\a.sln","UpdatedUtc":"2026-11-27T12:00:00Z","Ide":"VisualStudio","LogRoots":[]}""";

    [Fact]
    public void ListRunning_ReturnsOnlyLiveManifests_AndDeletesTheRest()
    {
        Manifest("1.json", Json(1));
        Manifest("2.json", Json(2));
        Manifest("3.json", "{ not json");
        Manifest("4.json", """{"Pid":4}""");

        var live = VsInstanceRegistry.ListRunning(_dir, pid => pid == 1);

        Assert.Equal([1], live.Select(i => i.Pid));
        Assert.True(File.Exists(Path.Combine(_dir, "1.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "2.json")));
    }

    [Fact]
    public void ListRunning_OnAMissingFolder_ReturnsEmpty()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Empty(VsInstanceRegistry.ListRunning(missing, _ => true));
    }
}
