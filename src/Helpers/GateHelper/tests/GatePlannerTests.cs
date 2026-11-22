using AiDevHelpers.Helpers.GateHelper;
using Xunit;

namespace AiDevHelpers.Helpers.GateHelper.Tests;

public sealed class GatePlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-helper-tests", Guid.NewGuid().ToString("N"));

    public GatePlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ReferenceFileWinsOverDetection()
    {
        Write("App.sln", string.Empty);
        Write(".claude/reference/full-gate.md", "# Gate\n\n```gate\n# comment\ndotnet build App.sln\n! dotnet list App.sln package --outdated\ndotnet test App.sln\n```\n");

        var steps = GatePlanner.Plan(_root);

        Assert.Equal(3, steps.Count);
        Assert.Equal("dotnet", steps[0].Tool);
        Assert.Equal(["build", "App.sln"], steps[0].Arguments);
        Assert.True(steps[1].AllowFailure);
        Assert.Equal(["list", "App.sln", "package", "--outdated"], steps[1].Arguments);
    }

    [Fact]
    public void ReferenceFileWithoutGateBlockIsAnError()
    {
        Write(".claude/reference/full-gate.md", "# Gate\nNo commands here.\n");

        Assert.Throws<InvalidOperationException>(() => GatePlanner.Plan(_root));
    }

    [Fact]
    public void DetectsDotnetAndNpmStacks()
    {
        Write("App.sln", string.Empty);
        Write("web/package.json", "{ \"scripts\": { \"build\": \"tsc\", \"test\": \"vitest\", \"deploy\": \"x\" } }");

        var names = GatePlanner.DetectStack(_root).Select(s => s.Name).ToList();

        Assert.Equal(["dotnet outdated", "dotnet clean", "dotnet build", "dotnet test"], names.Take(4));
    }

    [Fact]
    public void DetectsOnlyKnownNpmScripts()
    {
        Write("package.json", "{ \"scripts\": { \"build\": \"tsc\", \"deploy\": \"x\" } }");

        var names = GatePlanner.DetectStack(_root).Select(s => s.Name).ToList();

        Assert.Contains("npm run build", names);
        Assert.DoesNotContain("npm run deploy", names);
    }

    [Fact]
    public void NoKnownStackIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => GatePlanner.DetectStack(_root));
    }

    private sealed class FakeRunner(Dictionary<string, int> exitCodes) : IGateRunner
    {
        public List<string> Ran { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(GateStep step, string workingDirectory, CancellationToken ct = default)
        {
            Ran.Add(step.Name);
            return Task.FromResult((exitCodes.GetValueOrDefault(step.Name, 0), "output of " + step.Name));
        }
    }

    [Fact]
    public async Task RunnerStopsAtTheFirstFailureAndSkipsTheRest()
    {
        var steps = new List<GateStep>
        {
            new("build", "dotnet", ["build"]),
            new("test", "dotnet", ["test"]),
            new("lint", "npm", ["run", "lint"]),
        };
        var runner = new FakeRunner(new() { ["test"] = 1 });

        var results = await GateRunner.RunAsync(steps, _root, runner);

        Assert.True(results[0].Passed);
        Assert.False(results[1].Passed);
        Assert.True(results[2].Skipped);
        Assert.Equal(["build", "test"], runner.Ran);
    }

    [Fact]
    public async Task AdvisoryStepFailureDoesNotStopTheGate()
    {
        var steps = new List<GateStep>
        {
            new("outdated", "npm", ["outdated"], AllowFailure: true),
            new("test", "npm", ["test"]),
        };
        var runner = new FakeRunner(new() { ["outdated"] = 1 });

        var results = await GateRunner.RunAsync(steps, _root, runner);

        Assert.True(results[0].Passed);
        Assert.True(results[1].Passed);
        Assert.Equal(["outdated", "test"], runner.Ran);
    }
}
