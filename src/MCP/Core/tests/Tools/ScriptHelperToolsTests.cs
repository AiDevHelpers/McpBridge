using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class ScriptHelperToolsTests
{
    private sealed class FakeElevRunner : IElevRunner
    {
        public int Calls { get; private set; }
        public string? ProjectRoot { get; private set; }
        public string? Script { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public ElevRunResult Result { get; set; } = new(0, "hello");
        public Exception? Throw { get; set; }

        public Task<ElevRunResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls++;
            ProjectRoot = projectRoot;
            Script = script;
            Args = args;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Run_passes_the_project_script_and_arguments_to_elev()
    {
        var runner = new FakeElevRunner();
        var tools = new ScriptHelperTools(runner);

        var text = await tools.RunElevatedScript(@"C:\repo", "scripts/debug/attach.ps1", new[] { "-Port", "5000" });

        Assert.Equal(1, runner.Calls);
        Assert.Equal(@"C:\repo", runner.ProjectRoot);
        Assert.Equal("scripts/debug/attach.ps1", runner.Script);
        Assert.Equal(new[] { "-Port", "5000" }, runner.Args);
        Assert.Contains("exit 0", text);
        Assert.Contains("hello", text);
    }

    [Fact]
    public async Task A_nonzero_exit_code_and_output_are_returned()
    {
        var runner = new FakeElevRunner { Result = new ElevRunResult(7, "partial") };
        var tools = new ScriptHelperTools(runner);

        var text = await tools.RunElevatedScript(@"C:\repo", "scripts/debug/a.ps1");

        Assert.Contains("exit 7", text);
        Assert.Contains("partial", text);
    }

    [Fact]
    public async Task A_missing_script_is_rejected_before_elev_runs()
    {
        var runner = new FakeElevRunner();
        var tools = new ScriptHelperTools(runner);

        var text = await tools.RunElevatedScript(@"C:\repo", "  ");

        Assert.StartsWith("Error:", text);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task A_failure_to_start_elev_is_returned_as_error_text()
    {
        var runner = new FakeElevRunner { Throw = new InvalidOperationException("elev.exe not found") };
        var tools = new ScriptHelperTools(runner);

        var text = await tools.RunElevatedScript(@"C:\repo", "scripts/debug/a.ps1");

        Assert.Contains("elev.exe not found", text);
    }
}
