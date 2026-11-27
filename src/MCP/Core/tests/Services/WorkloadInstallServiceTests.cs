using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class WorkloadInstallServiceTests
{
    private sealed class FakeInstaller(int exitCode, string output = "") : IElevInstaller
    {
        public List<string> Calls { get; } = [];

        public Task<ElevRunResult> InstallAsync(string toolName, CancellationToken ct) =>
            Task.FromResult(new ElevRunResult(exitCode, output));

        public Task<ElevRunResult> InstallWorkloadAsync(string workloadName, CancellationToken ct)
        {
            Calls.Add(workloadName);
            return Task.FromResult(new ElevRunResult(exitCode, output));
        }
    }

    private static WorkloadInstallService Service(FakeInstaller installer, Func<DateTime>? clock = null) =>
        new(installer, clock);

    [Fact]
    public void PlanRefusesAWorkloadOutsideTheTable()
    {
        var outcome = Service(new FakeInstaller(0)).Plan("Microsoft.VisualStudio.Workload.ManagedDesktop", []);

        Assert.False(outcome.Ok);
        Assert.Null(outcome.Plan);
    }

    [Fact]
    public void PlanRefusesAnAlreadyInstalledWorkload()
    {
        var outcome = Service(new FakeInstaller(0)).Plan("maui", ["maui"]);

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Plan);
        Assert.Contains("maui", outcome.Message);
    }

    [Fact]
    public void PlanReturnsATokenAndSaysItNeedsAdmin()
    {
        var outcome = Service(new FakeInstaller(0)).Plan("android", []);

        Assert.True(outcome.Ok);
        Assert.NotNull(outcome.Plan);
        Assert.Contains("UAC", outcome.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmRunsTheElevatedInstallOnceAndRefusesAReuse()
    {
        var installer = new FakeInstaller(0, "installed");
        var service = Service(installer);
        var plan = service.Plan("ios", []).Plan!;

        var first = await service.ConfirmAsync(plan.Token, CancellationToken.None);
        var again = await service.ConfirmAsync(plan.Token, CancellationToken.None);

        Assert.True(first.Ok);
        Assert.False(again.Ok);
        Assert.Equal(["ios"], installer.Calls);
    }

    [Fact]
    public async Task ElevatedRefusalIsAFailure()
    {
        var service = Service(new FakeInstaller(2, "refused: the helper is not set up"));
        var plan = service.Plan("maui", []).Plan!;

        var result = await service.ConfirmAsync(plan.Token, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("refused", result.Error);
    }

    [Fact]
    public async Task ExpiredTokenIsRefused()
    {
        var now = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
        var installer = new FakeInstaller(0);
        var service = Service(installer, () => now);
        var plan = service.Plan("maui", []).Plan!;
        now = now.AddMinutes(16);

        var result = await service.ConfirmAsync(plan.Token, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(installer.Calls);
    }
}
