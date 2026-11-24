using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RunFlowTests
{
    private static ClientResult Ran() => new(true, "ran", 0, "out", string.Empty);

    private static ClientResult Denied(string reason) => new(false, reason, null, string.Empty, string.Empty);

    private sealed class Host(IEnumerable<ClientResult> answers, bool inManifest = false, bool yes = true, bool stepOk = true) : IRunHost
    {
        private readonly Queue<ClientResult> _answers = new(answers);

        public List<RunStep> Steps { get; } = [];

        public List<string> Asked { get; } = [];

        public List<string> Said { get; } = [];

        public int Sent { get; private set; }

        public Task<ClientResult> SendAsync(CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(_answers.Count > 1 ? _answers.Dequeue() : _answers.Peek());
        }

        public bool ScriptInManifest() => inManifest;

        public bool Ask(string question)
        {
            Asked.Add(question);
            return yes;
        }

        public SetupResult ElevatedStep(RunStep step)
        {
            Steps.Add(step);
            return new SetupResult(stepOk, stepOk ? $"{step} done" : $"{step} failed");
        }

        public void Say(string text) => Said.Add(text);
    }

    [Fact]
    public async Task AnAllowedScript_RunsAtOnce_WithNoPrerequisiteSteps()
    {
        var host = new Host([Ran()]);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(host.Steps);
        Assert.Equal(1, host.Sent);
    }

    [Fact]
    public async Task AnUnregisteredProject_IsRegistered_ThenRetried()
    {
        var host = new Host([Denied("project is not registered"), Ran()]);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal([RunStep.Register], host.Steps);
        Assert.Empty(host.Asked);
        Assert.Equal(2, host.Sent);
    }

    [Fact]
    public async Task ANotAllowedScript_IsOfferedToTheUsersAllows_AndRunsAfterAYes()
    {
        var host = new Host([Denied("project is not registered"), Denied("script is not in the allowlist"), Ran()]);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal([RunStep.Register, RunStep.AllowScript], host.Steps);
        var question = Assert.Single(host.Asked);
        Assert.Contains("repo is not changed", question);
    }

    [Fact]
    public async Task ANo_EndsTheRunWithTheBrokersReason_AndStartsNoElevatedStep()
    {
        var host = new Host([Denied("script is not in the allowlist")], yes: false);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("allowlist", result.Reason);
        Assert.Empty(host.Steps);
    }

    [Fact]
    public async Task AManifestScriptThatIsNotApproved_IsApproved_WithoutAskingToAllowIt()
    {
        var host = new Host([Denied("script is not approved"), Ran()], inManifest: true);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal([RunStep.Approve], host.Steps);
        Assert.Empty(host.Asked);
    }

    [Fact]
    public async Task AUserAllowedScriptThatChanged_IsAllowedAgain()
    {
        var host = new Host([Denied("the script changed since this user allowed it; allow it again"), Ran()]);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal([RunStep.AllowScript], host.Steps);
    }

    [Fact]
    public async Task AFailedStep_EndsTheRunWithThatReason()
    {
        var host = new Host([Denied("project is not registered")], stepOk: false);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Register failed", result.Reason);
        Assert.Equal(1, host.Sent);
    }

    [Theory]
    [InlineData("requester is not the project owner")]
    [InlineData("broker is not responding; start it before running scripts")]
    [InlineData("refused: the broker is not in the admin-only install folder")]
    [InlineData("approvals signature is invalid")]
    public async Task AnythingElse_IsReportedAsIs(string reason)
    {
        var host = new Host([Denied(reason)]);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.Equal(reason, result.Reason);
        Assert.Empty(host.Steps);
    }

    [Fact]
    public async Task EachStepRunsAtMostOnce_SoAStubbornDenialCannotLoop()
    {
        var host = new Host([Denied("script is not in the allowlist")]);

        var result = await RunFlow.RunAsync(host, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal([RunStep.AllowScript], host.Steps);
        Assert.Equal(2, host.Sent);
    }

    [Theory]
    [InlineData("script is not in the allowlist", false, RunStep.AllowScript)]
    [InlineData("arguments do not match any allowed entry", true, RunStep.AllowScript)]
    [InlineData(".elevated.json cannot be read", false, RunStep.AllowScript)]
    [InlineData("script is not approved", true, RunStep.Approve)]
    [InlineData("approvals file missing; approve the script first", true, RunStep.Approve)]
    [InlineData("script changed; re-approval needed", true, RunStep.Approve)]
    [InlineData("allowlist changed; re-approval needed", true, RunStep.Approve)]
    [InlineData("script is not approved", false, RunStep.AllowScript)]
    public void TheBrokersOwnReasons_MapToTheRightStep(string reason, bool inManifest, RunStep expected) =>
        Assert.Equal(expected, RunPlanner.Next(Denied(reason), inManifest, new HashSet<RunStep>()));
}
