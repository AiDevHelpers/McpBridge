using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class PlanRunnerTests
{
    [Fact]
    public async Task AQuickPlanFinishesWithItsResult()
    {
        var solution = TestSolutions.Single([(@"C:\a.cs", "class A { }")]);

        var outcome = await PlanRunner.RunAsync(solution, _ => Task.FromResult(new MethodEditPlan()), TimeSpan.FromSeconds(10));

        Assert.False(outcome.Incomplete);
        Assert.NotNull(outcome.Plan);
    }

    [Fact]
    public async Task APlanThatOverrunsTheLimitIsIncompleteAndNotReturned()
    {
        var solution = TestSolutions.Single([(@"C:\a.cs", "class A { }")]);

        var outcome = await PlanRunner.RunAsync(
            solution,
            async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return new MethodEditPlan();
            },
            TimeSpan.FromMilliseconds(100));

        Assert.True(outcome.Incomplete);
        Assert.Null(outcome.Plan);
    }

    [Fact]
    public void AnIncompletePlanIsRefusedWithItsReason()
    {
        var plan = PlanRunner.Incomplete(TimeSpan.FromSeconds(60));

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("60", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePlanRunsOffTheCallingThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var solution = TestSolutions.Single([(@"C:\a.cs", "class A { }")]);
        var planThread = -1;

        await PlanRunner.RunAsync(solution, _ => { planThread = Environment.CurrentManagedThreadId; return Task.FromResult(new MethodEditPlan()); }, TimeSpan.FromSeconds(10));

        Assert.NotEqual(caller, planThread);
    }
}
