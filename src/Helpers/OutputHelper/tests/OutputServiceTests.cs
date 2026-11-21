using System.Text.Json;
using AiDevHelpers.Helpers.OutputHelper;
using Xunit;

namespace AiDevHelpers.Helpers.OutputHelper.Tests;

public sealed class OutputServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "output-helper-tests", Guid.NewGuid().ToString("N"));
    private string Solution => Path.Combine(_root, "App.sln");

    public OutputServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeBridge : IOutputBridge
    {
        public List<(string Action, string Ide, string Channel, string Text)> Calls { get; } = [];

        public Task<string> CallAsync(string action, object args, string? solutionPath, string ide, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(args));
            Calls.Add((action, ide, json.RootElement.GetProperty("channel").GetString()!, json.RootElement.GetProperty("text").GetString()!));
            return Task.FromResult("{\"ok\":true}");
        }
    }

    [Fact]
    public void SetAndReadChannelIde_PersistsPerRepo()
    {
        OutputService.SetChannelIde(Solution, "Build", "VsCode");

        Assert.Equal("VsCode", OutputService.ChannelIde(Solution, "Build"));
        Assert.Null(OutputService.ChannelIde(Solution, "Other"));
    }

    [Fact]
    public void SetChannelIde_KeepsOtherKeysAndChannels()
    {
        var path = OutputService.ConfigPathFor(Solution);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"preferredIde\": \"VisualStudio\", \"outputChannels\": { \"Build\": \"VisualStudio\" } }");

        OutputService.SetChannelIde(Solution, "Tests", "VsCode");

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("VisualStudio", json.RootElement.GetProperty("preferredIde").GetString());
        Assert.Equal("VisualStudio", json.RootElement.GetProperty("outputChannels").GetProperty("Build").GetString());
        Assert.Equal("VsCode", json.RootElement.GetProperty("outputChannels").GetProperty("Tests").GetString());
    }

    [Fact]
    public void SetChannelIde_RejectsUnknownIdeAndEmptyChannel()
    {
        Assert.Throws<ArgumentException>(() => OutputService.SetChannelIde(Solution, "Build", "Notepad"));
        Assert.Throws<ArgumentException>(() => OutputService.SetChannelIde(Solution, " ", "VsCode"));
    }

    [Fact]
    public async Task Write_UsesTheStoredIdeWhenNoneIsPassed()
    {
        OutputService.SetChannelIde(Solution, "Build", "VsCode");
        var bridge = new FakeBridge();

        var reply = await new OutputService(bridge).WriteAsync(Solution, "Build", "hello", ide: null);

        Assert.Contains("\"ok\":true", reply);
        var call = Assert.Single(bridge.Calls);
        Assert.Equal("outputWrite", call.Action);
        Assert.Equal("VsCode", call.Ide);
        Assert.Equal("Build", call.Channel);
        Assert.Equal("hello", call.Text);
    }

    [Fact]
    public async Task Write_ExplicitIdeWinsOverStoredIde()
    {
        OutputService.SetChannelIde(Solution, "Build", "VsCode");
        var bridge = new FakeBridge();

        await new OutputService(bridge).WriteAsync(Solution, "Build", "hello", ide: "VisualStudio");

        Assert.Equal("VisualStudio", Assert.Single(bridge.Calls).Ide);
    }

    [Fact]
    public async Task Write_NoIdeAnywhere_ReturnsAnErrorAndCallsNothing()
    {
        var bridge = new FakeBridge();

        var reply = await new OutputService(bridge).WriteAsync(Solution, "Build", "hello", ide: null);

        Assert.Contains("No IDE is set", reply);
        Assert.Empty(bridge.Calls);
    }
}
