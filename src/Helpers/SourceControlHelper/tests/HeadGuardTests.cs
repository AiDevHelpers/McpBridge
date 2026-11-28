using AiDevHelpers.Helpers.SourceControlHelper;
using Xunit;

namespace AiDevHelpers.Helpers.SourceControlHelper.Tests;

/// <summary>Plan-time HEAD and branch guard. Fake runner: no real git.</summary>
public sealed class HeadGuardTests
{
    private sealed class Runner : IGitRunner
    {
        public string Head { get; set; } = "aaa111";
        public string Branch { get; set; } = "main";
        public bool Unborn { get; set; }
        public List<string> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            var line = string.Join(' ', arguments);
            Calls.Add(line);
            if (line == "rev-parse HEAD")
            {
                return Task.FromResult(Unborn ? new GitResult(false, 128, "fatal: ambiguous argument 'HEAD'") : new GitResult(true, 0, Head + "\n"));
            }

            if (line == "rev-parse --abbrev-ref HEAD")
            {
                return Task.FromResult(new GitResult(true, 0, Branch + "\n"));
            }

            return Task.FromResult(new GitResult(true, 0, string.Empty));
        }
    }

    [Fact]
    public async Task CommitRefusesWhenHeadMovedAfterPlan()
    {
        var runner = new Runner();
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync("r", "commit", message: "m");

        runner.Head = "bbb222";
        var result = await service.ConfirmAsync(planned.Token);

        Assert.False(result.Ok);
        Assert.DoesNotContain("commit -m m", runner.Calls);
    }

    [Fact]
    public async Task PushRefusesWhenBranchSwitchedToSameCommit()
    {
        var runner = new Runner();
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync("r", "push");

        runner.Branch = "feature/other";
        var result = await service.ConfirmAsync(planned.Token);

        Assert.False(result.Ok);
        Assert.DoesNotContain("push", runner.Calls);
    }

    [Fact]
    public async Task CommitRunsWhenHeadAndBranchUnchanged()
    {
        var runner = new Runner();
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync("r", "commit", message: "m");

        var result = await service.ConfirmAsync(planned.Token);

        Assert.True(result.Ok);
        Assert.Contains("commit -m m", runner.Calls);
    }

    [Fact]
    public async Task StageMakesNoHeadCall()
    {
        var runner = new Runner();
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync("r", "stage", paths: ["a.cs"]);

        _ = await service.ConfirmAsync(planned.Token);

        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("rev-parse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SequenceChecksHeadOnlyBeforeFirstStep()
    {
        var runner = new Runner();
        var service = new SourceControlService(runner);
        var sequence = await service.PlanSequenceAsync("r", ["commit", "push"], message: "m");

        var result = await service.ConfirmAsync(sequence.Token);

        Assert.True(result.Ok);
        // One read at plan time and one before the first step; none per step.
        Assert.Equal(2, runner.Calls.Count(c => c == "rev-parse HEAD"));
    }

    [Fact]
    public async Task UnbornHeadPlansAndCompares()
    {
        var runner = new Runner { Unborn = true };
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync("r", "commit", message: "m");

        var result = await service.ConfirmAsync(planned.Token);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task UnbornHeadThatBecomesBornIsRefused()
    {
        var runner = new Runner { Unborn = true };
        var service = new SourceControlService(runner);
        var planned = await service.PlanAsync("r", "commit", message: "m");

        runner.Unborn = false;
        var result = await service.ConfirmAsync(planned.Token);

        Assert.False(result.Ok);
    }
}
