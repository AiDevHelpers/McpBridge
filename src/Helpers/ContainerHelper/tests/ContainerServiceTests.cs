using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.ContainerHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ContainerHelper.Tests;

/// <summary>Scripted stand-in for the process runner. Keyed by executable and first argument; never starts a process.</summary>
internal sealed class FakeRuntimeCommands : IRuntimeCommands
{
    private readonly Dictionary<(string File, string Verb), CommandResult> _script = new();

    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

    public List<CommandRequest> Requests { get; } = [];

    public FakeRuntimeCommands On(string file, string verb, CommandResult result)
    {
        _script[(file, verb)] = result;
        return this;
    }

    public FakeRuntimeCommands Ok(string file, string verb, string stdout = "") =>
        On(file, verb, new CommandResult { ExitCode = 0, StdOut = stdout });

    public FakeRuntimeCommands Fail(string file, string verb, int exitCode, string stderr) =>
        On(file, verb, new CommandResult { ExitCode = exitCode, StdErr = stderr });

    public FakeRuntimeCommands Missing(string file) =>
        On(file, "--version", new CommandResult { Launched = false, LaunchError = $"{file} not found" });

    public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        Calls.Add((request.FileName, request.Arguments));
        Requests.Add(request);
        string verb = request.Arguments.Count > 0 ? request.Arguments[0] : "";
        CommandResult result = _script.TryGetValue((request.FileName, verb), out CommandResult? scripted)
            ? scripted
            : new CommandResult { Launched = false, LaunchError = $"not scripted: {request.FileName} {verb}" };
        return Task.FromResult(result);
    }

    /// <summary>Docker present and healthy, container "web" running with the given inspect line.</summary>
    public static FakeRuntimeCommands HealthyDocker(string inspect = "running|healthy|0|2026-01-01T00:00:00Z") =>
        new FakeRuntimeCommands()
            .Ok("docker", "--version", "Docker version 28.0")
            .Ok("docker", "info", "28.0")
            .Ok("docker", "inspect", inspect)
            .Missing("podman");

    public static FakeRuntimeCommands HealthyPodman(string inspect = "running|healthy|0|2026-01-01T00:00:00Z") =>
        new FakeRuntimeCommands()
            .Missing("docker")
            .Ok("podman", "--version", "podman version 5.0")
            .Ok("podman", "info", "5.0")
            .Ok("podman", "inspect", inspect);
}

public class ContainerNameTests
{
    [Theory]
    [InlineData("web")]
    [InlineData("web-1.api_x")]
    [InlineData("9lives")]
    public void ValidNamesAreAccepted(string name) => Assert.True(ContainerName.IsValid(name));

    [Theory]
    [InlineData("-rf")]
    [InlineData("a;rm")]
    [InlineData("a b")]
    [InlineData("$(x)")]
    [InlineData("a|b")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidNamesAreRefused(string? name) => Assert.False(ContainerName.IsValid(name));
}

public class RuntimeDetectionTests
{
    [Fact]
    public async Task DockerPresentIsUsedFirst()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands()
            .Ok("docker", "--version", "Docker version 28.0")
            .Missing("podman");
        ContainerService service = new(fake);

        RuntimeDetection detection = await service.DetectAsync(null);

        Assert.True(detection.Ok);
        Assert.Equal("docker", detection.Runtime);
    }

    [Fact]
    public async Task DockerMissingFallsBackToPodman()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands()
            .Missing("docker")
            .Ok("podman", "--version", "podman version 5.0");
        ContainerService service = new(fake);

        RuntimeDetection detection = await service.DetectAsync(null);

        Assert.Equal("podman", detection.Runtime);
    }

    [Fact]
    public async Task NeitherRuntimeIsAnError()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands().Missing("docker").Missing("podman");
        ContainerService service = new(fake);

        RuntimeDetection detection = await service.DetectAsync(null);

        Assert.False(detection.Ok);
        Assert.Contains("install Docker or Podman", detection.Error);
    }

    [Fact]
    public async Task ExplicitRuntimeSkipsDetection()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands().Ok("podman", "--version", "podman version 5.0");
        ContainerService service = new(fake);

        RuntimeDetection detection = await service.DetectAsync("podman");

        Assert.Equal("podman", detection.Runtime);
        Assert.All(fake.Calls, c => Assert.Equal("podman", c.File));
    }

    [Fact]
    public async Task ExplicitRuntimeNotInstalledIsReported()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands().Missing("docker");
        ContainerService service = new(fake);

        RuntimeDetection detection = await service.DetectAsync("docker");

        Assert.False(detection.Ok);
        Assert.Contains("docker", detection.Error);
    }

    [Fact]
    public async Task ExplicitUnknownRuntimeIsRefusedWithoutRunning()
    {
        FakeRuntimeCommands fake = new();
        ContainerService service = new(fake);

        RuntimeDetection detection = await service.DetectAsync("kubectl");

        Assert.False(detection.Ok);
        Assert.Contains("kubectl", detection.Error);
        Assert.Empty(fake.Calls);
    }
}

public class PreflightAndStatusTests
{
    [Fact]
    public async Task HealthyContainerReturnsStatus()
    {
        ContainerService service = new(FakeRuntimeCommands.HealthyDocker());

        ContainerStatusResult status = await service.StatusAsync("web");

        Assert.True(status.Ok);
        Assert.Equal("docker", status.Runtime);
        Assert.Equal("web", status.Container);
        Assert.Equal("running", status.State);
        Assert.Equal("healthy", status.Health);
        Assert.Equal(0, status.ExitCode);
        Assert.Equal("2026-01-01T00:00:00Z", status.FinishedAt);
    }

    [Fact]
    public async Task PreflightRunsCliThenDaemonThenInspectInOrder()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker();
        ContainerService service = new(fake);

        await service.StatusAsync("web", "docker");

        Assert.True(fake.Calls[0].Args.SequenceEqual(new[] { "--version" }));
        Assert.True(fake.Calls[1].Args.SequenceEqual(new[] { "info", "--format", "{{.ServerVersion}}" }));
        Assert.Equal("inspect", fake.Calls[2].Args[0]);
    }

    [Fact]
    public async Task DockerDaemonDownNamesDockerDesktop()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands()
            .Ok("docker", "--version", "Docker version 28.0")
            .Fail("docker", "info", 1, "cannot connect to the Docker daemon");
        ContainerService service = new(fake);

        ContainerStatusResult status = await service.StatusAsync("web");

        Assert.False(status.Ok);
        Assert.Contains("Docker Desktop", status.Error);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task PodmanDaemonDownNamesMachineStart()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands()
            .Ok("podman", "--version", "podman version 5.0")
            .Fail("podman", "info", 125, "no machine");
        ContainerService service = new(fake);

        ContainerStatusResult status = await service.StatusAsync("web", "podman");

        Assert.False(status.Ok);
        Assert.Contains("podman machine start", status.Error);
    }

    [Fact]
    public async Task MissingContainerIsReported()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands()
            .Ok("docker", "--version")
            .Ok("docker", "info")
            .Fail("docker", "inspect", 1, "No such object: web");
        ContainerService service = new(fake);

        ContainerStatusResult status = await service.StatusAsync("web", "docker");

        Assert.False(status.Ok);
        Assert.Contains("web", status.Error);
        Assert.Contains("not found", status.Error);
    }

    [Fact]
    public async Task InvalidNameIsRefusedBeforeAnyCommand()
    {
        FakeRuntimeCommands fake = new();
        ContainerService service = new(fake);

        ContainerStatusResult status = await service.StatusAsync("-rf");

        Assert.False(status.Ok);
        Assert.Contains("Invalid container name", status.Error);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("running|healthy|0|2026-01-01T00:00:00Z", "running", "healthy", 0)]
    [InlineData("running|<nil>|0|2026-01-01T00:00:00Z", "running", "none", 0)]
    [InlineData("exited||137|2026-02-02T10:00:00Z", "exited", "none", 137)]
    public async Task InspectLineIsParsed(string inspect, string state, string health, int exitCode)
    {
        ContainerService service = new(FakeRuntimeCommands.HealthyDocker(inspect));

        ContainerStatusResult status = await service.StatusAsync("web", "docker");

        Assert.True(status.Ok);
        Assert.Equal(state, status.State);
        Assert.Equal(health, status.Health);
        Assert.Equal(exitCode, status.ExitCode);
    }

    [Fact]
    public async Task LaunchFailureIsReportedNotThrown()
    {
        FakeRuntimeCommands fake = new FakeRuntimeCommands()
            .Ok("docker", "--version")
            .On("docker", "info", new CommandResult { Launched = false, LaunchError = "boom" });
        ContainerService service = new(fake);

        ContainerStatusResult status = await service.StatusAsync("web", "docker");

        Assert.False(status.Ok);
        Assert.Contains("boom", status.Error);
    }
}

public class LogsTests
{
    [Fact]
    public async Task TailLinesZeroIsRefusedWithoutRunning()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker();
        ContainerService service = new(fake);

        ContainerLogsResult logs = await service.LogsAsync("web", tailLines: 0, runtime: "docker");

        Assert.False(logs.Ok);
        Assert.Contains("tailLines", logs.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task DefaultTailLinesAndDoubleDashBeforeName()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker()
            .Ok("docker", "logs", "line");
        ContainerService service = new(fake);

        await service.LogsAsync("web", runtime: "docker");

        Assert.True(fake.Calls[^1].Args.SequenceEqual(new[] { "logs", "--tail", "500", "--", "web" }));
    }

    [Fact]
    public async Task StdoutAndStderrAreCombined()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker();
        fake.On("docker", "logs", new CommandResult { ExitCode = 0, StdOut = "out line\n", StdErr = "err line\n" });
        ContainerService service = new(fake);

        ContainerLogsResult logs = await service.LogsAsync("web", runtime: "docker");

        Assert.True(logs.Ok);
        Assert.Contains("out line", logs.Logs);
        Assert.Contains("err line", logs.Logs);
        Assert.False(logs.Capped);
    }

    [Fact]
    public async Task ErrorLinesOnlyKeepsMatchingLines()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker();
        fake.Ok("docker", "logs", "info ok\nERROR boom\nall fine\nexception x\n");
        ContainerService service = new(fake);

        ContainerLogsResult logs = await service.LogsAsync("web", errorLinesOnly: true, runtime: "docker");

        Assert.Contains("ERROR boom", logs.Logs);
        Assert.Contains("exception x", logs.Logs);
        Assert.DoesNotContain("info ok", logs.Logs);
        Assert.DoesNotContain("all fine", logs.Logs);
        Assert.Equal(2, logs.Lines);
    }

    [Fact]
    public async Task LogsAreCappedAt20000Characters()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker();
        fake.Ok("docker", "logs", new string('a', 25_000));
        ContainerService service = new(fake);

        ContainerLogsResult logs = await service.LogsAsync("web", runtime: "docker");

        Assert.True(logs.Ok);
        Assert.Equal(20_000, logs.Logs.Length);
        Assert.True(logs.Capped);
    }
}

public class ContainerActionTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task ActionArgumentsPutDoubleDashBeforeName(string verb)
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker().Ok("docker", verb);
        ContainerService service = new(fake);

        ContainerActionResult result = verb switch
        {
            "start" => await service.StartAsync("web", "docker"),
            "stop" => await service.StopAsync("web", "docker"),
            _ => await service.RestartAsync("web", "docker"),
        };

        Assert.True(result.Ok);
        Assert.Equal(verb, result.Action);
        Assert.Contains(fake.Calls, c => c.Args.SequenceEqual(new[] { verb, "--", "web" }));
        Assert.NotNull(result.Status);
    }

    [Fact]
    public async Task FailedActionReportsExitCodeAndStderr()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker().Fail("docker", "stop", 1, "busy");
        ContainerService service = new(fake);

        ContainerActionResult result = await service.StopAsync("web", "docker");

        Assert.False(result.Ok);
        Assert.Contains("exit 1", result.Error);
        Assert.Contains("busy", result.Error);
    }
}

public class ExecTests
{
    [Fact]
    public async Task CommandIsOneArgumentElement()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker()
            .Ok("docker", "exec", "hi");
        ContainerService service = new(fake);

        ContainerExecResult result = await service.ExecAsync("web", "echo hi; ls", runtime: "docker");

        Assert.True(result.Ok);
        Assert.Contains(fake.Calls, c => c.Args.SequenceEqual(new[] { "exec", "web", "/bin/sh", "-c", "echo hi; ls" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task TimeoutOutOfRangeIsRefusedWithoutRunning(int seconds)
    {
        FakeRuntimeCommands fake = new();
        ContainerService service = new(fake);

        ContainerExecResult result = await service.ExecAsync("web", "ls", timeoutSeconds: seconds, runtime: "docker");

        Assert.False(result.Ok);
        Assert.Contains("timeoutSeconds", result.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task EmptyCommandIsRefused()
    {
        ContainerService service = new(new FakeRuntimeCommands());

        ContainerExecResult result = await service.ExecAsync("web", "   ", runtime: "docker");

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task TimedOutResultHasExitCodeMinusOne()
    {
        FakeRuntimeCommands fake = FakeRuntimeCommands.HealthyDocker();
        fake.On("docker", "exec", new CommandResult { ExitCode = -1, TimedOut = true, StdOut = "partial" });
        ContainerService service = new(fake);

        ContainerExecResult result = await service.ExecAsync("web", "sleep 999", timeoutSeconds: 1, runtime: "docker");

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("partial", result.Stdout);
    }
}
