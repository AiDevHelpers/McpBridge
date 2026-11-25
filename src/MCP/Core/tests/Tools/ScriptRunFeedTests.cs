using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class ScriptRunFeedTests
{
    private sealed class FailingElev : IElevRunner
    {
        public Task<ElevRunResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct) =>
            Task.FromResult(new ElevRunResult(1, "nope"));
    }

    [Fact]
    public async Task AnElevatedScriptThatKeepsFailing_GetsTheSameAdviceAsScriptRun()
    {
        var tracker = new ScriptRunTracker();
        var tools = new ScriptHelperTools(new FailingElev(), tracker, Options.Create(new ScriptWatchOptions()));

        var first = await tools.RunElevatedScript(@"C:\r", "a.ps1");
        await tools.RunElevatedScript(@"C:\r", "a.ps1");
        var third = await tools.RunElevatedScript(@"C:\r", "a.ps1");

        Assert.DoesNotContain("looks stuck", first);
        Assert.Contains("looks stuck", third);
        Assert.Contains("interactive", third);
    }

    [Fact]
    public async Task WithoutATracker_ElevatedRunsBehaveAsBefore()
    {
        var text = await new ScriptHelperTools(new FailingElev()).RunElevatedScript(@"C:\r", "a.ps1");

        Assert.StartsWith("exit 1", text);
        Assert.DoesNotContain("looks stuck", text);
    }
}
