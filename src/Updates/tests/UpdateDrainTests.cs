using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateDrainTests
{
    // Records the hold and release calls; the process stops (or not) as the test says.
    private sealed class FakeProcessControl(bool stops) : IMcpProcessControl
    {
        public List<string> Calls { get; } = [];

        public void Hold() => Calls.Add("hold");

        public void Release() => Calls.Add("release");

        public Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add("wait");
            return Task.FromResult(stops);
        }
    }

    [Fact]
    public async Task DrainHoldsAndWaits()
    {
        var control = new FakeProcessControl(stops: true);
        var drain = new McpUpdateDrain(control, TimeSpan.FromSeconds(30));

        bool drained = await drain.DrainAsync(CancellationToken.None);

        Assert.True(drained);
        Assert.Equal(["hold", "wait"], control.Calls);
    }

    // The process did not stop in time: the drain reports it, and the installer must not apply the update.
    [Fact]
    public async Task DrainTimeoutReturnsFalse()
    {
        var control = new FakeProcessControl(stops: false);
        var drain = new McpUpdateDrain(control, TimeSpan.FromSeconds(30));

        bool drained = await drain.DrainAsync(CancellationToken.None);

        Assert.False(drained);
    }

    [Fact]
    public async Task NoOpDrainAlwaysSucceeds()
    {
        Assert.True(await new NoOpUpdateDrain().DrainAsync(CancellationToken.None));
    }
}
