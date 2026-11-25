using AiDevHelpers.Common.Processes;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class DotnetDiagServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dotnet-diag-" + Guid.NewGuid().ToString("N"));
    private readonly List<CommandRequest> _ran = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private Task<CommandResult> Runner(CommandRequest request, CancellationToken ct)
    {
        _ran.Add(request);
        return Task.FromResult(new CommandResult { ExitCode = 0, StdOut = "ok" });
    }

    private DotnetDiagService Service(params string[] installed) =>
        new(Runner, name => installed.Contains(name) ? @"C:\tools\" + name + ".exe" : null, _dir);

    [Fact]
    public async Task Stack_runs_report_with_the_pid_from_the_located_tool()
    {
        DiagOutcome outcome = await Service("dotnet-stack").RunAsync("stack", 4242, 0, null, default);

        Assert.True(outcome.Ok);
        CommandRequest request = Assert.Single(_ran);
        Assert.Equal(@"C:\tools\dotnet-stack.exe", request.FileName);
        Assert.Equal(["report", "-p", "4242"], request.Arguments);
    }

    [Fact]
    public async Task Missing_tool_runs_nothing_and_gives_the_install_command()
    {
        DiagOutcome outcome = await Service().RunAsync("gcdump", 7, 0, null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("dotnet tool install --global dotnet-gcdump", outcome.Error);
        Assert.Empty(_ran);
    }

    [Theory]
    [InlineData("stack", 0)]
    [InlineData("dump", -1)]
    public async Task Pid_must_be_positive_for_process_actions(string action, int pid)
    {
        DiagOutcome outcome = await Service("dotnet-dump", "dotnet-stack").RunAsync(action, pid, 0, null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("pid must be", outcome.Error);
        Assert.Empty(_ran);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Counters_seconds_are_bounded(int seconds)
    {
        DiagOutcome outcome = await Service("dotnet-counters").RunAsync("counters", 10, seconds, null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("seconds must be between 1 and 300", outcome.Error);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Trace_uses_duration_and_an_output_file_under_the_output_dir()
    {
        await Service("dotnet-trace").RunAsync("trace", 99, 10, null, default);

        CommandRequest request = Assert.Single(_ran);
        Assert.Equal(["collect", "-p", "99", "--duration", "00:00:10", "-o", Path.Combine(_dir, Path.GetFileName(request.Arguments[^1]))], request.Arguments);
        Assert.StartsWith("trace-99-", Path.GetFileName(request.Arguments[^1]));
    }

    [Fact]
    public async Task Relative_output_path_is_placed_under_the_output_dir()
    {
        await Service("dotnet-dump").RunAsync("dump", 5, 0, "mine.dmp", default);

        CommandRequest request = Assert.Single(_ran);
        Assert.Equal(Path.Combine(_dir, "mine.dmp"), request.Arguments[^1]);
    }

    [Fact]
    public async Task Unknown_action_is_refused()
    {
        DiagOutcome outcome = await Service("dotnet-dump").RunAsync("kill", 5, 0, null, default);

        Assert.False(outcome.Ok);
        Assert.Contains("Unknown action", outcome.Error);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Status_does_not_probe_dotnet_symbol_for_a_version()
    {
        var rows = await Service("dotnet-symbol").StatusAsync(default);

        Assert.Contains(rows, r => r.Tool.Executable == "dotnet-symbol" && r.Path is not null && r.Version is null);
        Assert.DoesNotContain(_ran, r => r.FileName.EndsWith("dotnet-symbol.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_lists_every_tool_and_marks_the_missing_ones()
    {
        var rows = await Service("dotnet-dump").StatusAsync(default);

        Assert.Equal(DotnetDiagService.Tools.Count, rows.Count);
        Assert.Contains(rows, r => r.Tool.Executable == "dotnet-dump" && r.Path is not null);
        Assert.Contains(rows, r => r.Tool.Executable == "dotnet-trace" && r.Path is null);
    }
}
