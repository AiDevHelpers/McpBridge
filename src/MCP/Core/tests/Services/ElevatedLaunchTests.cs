using AiDevHelpers.Bridge;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ElevatedLaunchTests
{
    private static readonly LaunchDecision Needs = new(true, "the solution has a project that needs elevation: src/Drv/Drv.csproj");
    private static readonly LaunchDecision NotNeeded = new(false, "no project in this solution is listed as needing elevation");

    [Theory]
    [InlineData(true, false, ElevationState.Unknown, false, LaunchAction.StartElevated)]
    [InlineData(false, false, ElevationState.Unknown, false, LaunchAction.StartNormal)]
    [InlineData(true, true, ElevationState.Elevated, false, LaunchAction.Reuse)]
    [InlineData(true, true, ElevationState.NotElevated, false, LaunchAction.AskRelaunch)]
    [InlineData(true, true, ElevationState.NotElevated, true, LaunchAction.RelaunchElevated)]
    [InlineData(false, true, ElevationState.NotElevated, true, LaunchAction.Reuse)]
    [InlineData(false, true, ElevationState.Elevated, false, LaunchAction.Reuse)]
    [InlineData(true, true, ElevationState.Unknown, true, LaunchAction.Reuse)]
    public void ThePlan_NeverElevatesOnAGuess_AndNeverClosesAnInstanceUnasked(bool needs, bool running, ElevationState state, bool relaunch, LaunchAction expected) =>
        Assert.Equal(expected, ElevatedLaunch.Plan(needs ? Needs : NotNeeded, running, state, relaunch));

    private static VsInstance Instance(int pid, string? solution, string ide = "VisualStudio") => new(pid, "pipe" + pid, solution, DateTime.UtcNow, ide, []);

    private static string? Check(string? solution, string? ide, int? pid, ElevationState state, LaunchDecision decision, params VsInstance[] running) =>
        DebugElevationGuard.Check(solution, ide, pid, () => running, _ => decision, _ => state);

    [Fact]
    public void Debugging_OnANonElevatedInstanceOfASolutionThatNeedsElevation_IsRefusedWithTheNextStep()
    {
        var refusal = Check(@"C:\r\App.sln", null, null, ElevationState.NotElevated, Needs, Instance(10, @"C:\r\App.sln"));

        Assert.NotNull(refusal);
        Assert.Contains("src/Drv/Drv.csproj", refusal);
        Assert.Contains("pid 10", refusal);
        Assert.Contains("relaunchElevated", refusal);
    }

    [Fact]
    public void Debugging_IsLeftAlone_WhenElevated_NotNeeded_UnknownOrAmbiguous()
    {
        var one = Instance(10, @"C:\r\App.sln");

        Assert.Null(Check(@"C:\r\App.sln", null, null, ElevationState.Elevated, Needs, one));
        Assert.Null(Check(@"C:\r\App.sln", null, null, ElevationState.NotElevated, NotNeeded, one));
        Assert.Null(Check(@"C:\r\App.sln", null, null, ElevationState.Unknown, Needs, one));
        Assert.Null(Check(@"C:\r\App.sln", null, null, ElevationState.NotElevated, Needs, one, Instance(11, @"C:\r\App.sln")));
        Assert.Null(Check(@"C:\r\Other.sln", null, null, ElevationState.NotElevated, Needs, one));
        Assert.Null(Check(null, "VsCode", null, ElevationState.NotElevated, Needs, Instance(12, @"C:\r\App.sln", "VsCode")));
    }

    [Fact]
    public void Debugging_PicksTheInstanceByPid_WhenTwoHaveTheSolutionOpen()
    {
        var refusal = Check(null, null, 11, ElevationState.NotElevated, Needs, Instance(10, @"C:\r\App.sln"), Instance(11, @"C:\r\App.sln"));

        Assert.Contains("pid 11", refusal);
    }

    [Fact]
    public void WithNoInstanceOrNoSolutionPath_NothingIsRefused()
    {
        Assert.Null(Check(null, null, null, ElevationState.NotElevated, Needs));
        Assert.Null(Check(null, null, null, ElevationState.NotElevated, Needs, Instance(10, null)));
    }
}
