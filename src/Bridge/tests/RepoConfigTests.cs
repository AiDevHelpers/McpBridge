using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

/// <summary>Reads and writes .claude/aidevhelpers.json next to the solution.</summary>
public sealed class RepoConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aidevhelpers-repo-config-tests", Guid.NewGuid().ToString("N"));

    public RepoConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Solution => Path.Combine(_root, "App.sln");

    [Fact]
    public void PreferredIde_NoFile_ReturnsNull()
    {
        Assert.Null(RepoConfig.PreferredIde(Solution));
    }

    [Fact]
    public void SetPreferredIde_CreatesFileAndReadsItBack()
    {
        RepoConfig.SetPreferredIde(Solution, "VsCode");

        Assert.Equal("VsCode", RepoConfig.PreferredIde(Solution));
        Assert.True(File.Exists(Path.Combine(_root, ".claude", "aidevhelpers.json")));
    }

    [Fact]
    public void SetPreferredIde_KeepsOtherKeys()
    {
        var path = RepoConfig.PathFor(Solution);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"outputChannels\": { \"build\": \"Build\" } }");

        RepoConfig.SetPreferredIde(Solution, "VisualStudio");

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Build", json.RootElement.GetProperty("outputChannels").GetProperty("build").GetString());
        Assert.Equal("VisualStudio", json.RootElement.GetProperty("preferredIde").GetString());
    }

    [Fact]
    public void SetPreferredIde_RejectsUnknownIde()
    {
        Assert.Throws<ArgumentException>(() => RepoConfig.SetPreferredIde(Solution, "Notepad"));
    }

    [Fact]
    public void SetPreferredIde_MalformedFile_ThrowsAndKeepsTheFile()
    {
        var path = RepoConfig.PathFor(Solution);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.ThrowsAny<JsonException>(() => RepoConfig.SetPreferredIde(Solution, "VsCode"));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }
}

/// <summary>Routing tie-break: a stored preference picks one instance only when two match the same solution.</summary>
[Collection("ProgramData")]
public sealed class PreferredIdeRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aidevhelpers-preferred-ide-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousProgramData;
    private readonly string _solution;
    private readonly Process _sleeper;

    public PreferredIdeRoutingTests()
    {
        Directory.CreateDirectory(_root);
        _previousProgramData = Environment.GetEnvironmentVariable("ProgramData");
        Environment.SetEnvironmentVariable("ProgramData", _root);
        _solution = Path.Combine(_root, "work", "App.sln");
        Directory.CreateDirectory(Path.GetDirectoryName(_solution)!);
        File.WriteAllText(_solution, string.Empty);

        // Two live processes stand in for two IDE windows. Nothing listens on their pipes, so calls fail
        // with "Could not reach", which names the instance that the router picked.
        _sleeper = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        WriteManifest(Environment.ProcessId, "VisualStudio");
        WriteManifest(_sleeper.Id, "VsCode");
    }

    public void Dispose()
    {
        if (!_sleeper.HasExited)
        {
            _sleeper.Kill();
        }

        _sleeper.Dispose();
        Environment.SetEnvironmentVariable("ProgramData", _previousProgramData);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteManifest(int pid, string ide)
    {
        var manifest = new VsInstance(pid, InstanceRegistry.PipeNameFor(pid), _solution, DateTime.UtcNow, ide, Array.Empty<string>());
        Directory.CreateDirectory(VsInstanceRegistry.Directory);
        File.WriteAllText(Path.Combine(VsInstanceRegistry.Directory, $"{pid}.json"), JsonSerializer.Serialize(manifest));
    }

    private async Task<JsonDocument> CallHealth(string? ide = null, int? instancePid = null) =>
        JsonDocument.Parse(await IdeInstanceRouter.CallAsync("health", new { }, _solution, CancellationToken.None, ide, instancePid));

    [Fact]
    public async Task Ambiguous_NoPreference_ReturnsTheErrorWithBothInstances()
    {
        using var json = await CallHealth();

        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Several instances", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Ambiguous_StoredPreference_RoutesToThePreferredIde()
    {
        RepoConfig.SetPreferredIde(_solution, "VsCode");

        using var json = await CallHealth();

        Assert.Equal("VsCode", json.RootElement.GetProperty("ide").GetString());
    }

    [Fact]
    public async Task ExplicitIde_WinsOverStoredPreference()
    {
        RepoConfig.SetPreferredIde(_solution, "VsCode");

        using var json = await CallHealth(ide: "VisualStudio");

        Assert.Equal("VisualStudio", json.RootElement.GetProperty("ide").GetString());
    }

    [Fact]
    public async Task ExplicitInstancePid_WinsOverStoredPreference()
    {
        RepoConfig.SetPreferredIde(_solution, "VsCode");

        using var json = await CallHealth(instancePid: Environment.ProcessId);

        Assert.Equal("VisualStudio", json.RootElement.GetProperty("ide").GetString());
    }

    [Fact]
    public async Task PreferenceNamingAnAbsentIde_StillReturnsTheError()
    {
        // Only the VS Code instance matches the preference, but the VS instance is also open. Two IDEs remain
        // after no narrowing is possible, so the router must not guess.
        var path = RepoConfig.PathFor(_solution);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"preferredIde\": \"Unknown\" }");

        using var json = await CallHealth();

        Assert.Contains("Several instances", json.RootElement.GetProperty("error").GetString());
    }
}
