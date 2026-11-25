using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class BrokerSessionTests
{
    private sealed class FakePing(params BrokerState[] states) : IBrokerPing
    {
        private int _i;
        public int Calls => _i;
        public BrokerState Check(TimeSpan timeout) => states[Math.Min(_i++, states.Length - 1)];
    }

    // Silent until the starter has been used, then answering, like a real broker.
    private sealed class StartedPing(FakeStarter starter) : IBrokerPing
    {
        public BrokerState Check(TimeSpan timeout) => starter.Starts > 0 ? BrokerState.Answering : BrokerState.Silent;
    }

    private sealed class FakeStarter(bool declined = false) : IBrokerProcessStarter
    {
        public int Starts { get; private set; }

        public StartOutcome StartElevated(string exePath)
        {
            Starts++;
            return declined ? StartOutcome.Declined : StartOutcome.Started;
        }
    }

    private static BrokerSession Session(IBrokerPing ping, IBrokerProcessStarter starter, Func<DateTime>? now = null) =>
        new(ping, starter, @"C:\broker\scripthelper-broker.exe", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), now ?? (() => DateTime.UtcNow), _ => Task.CompletedTask);

    [Fact]
    public async Task AnsweringBroker_IsNotStartedAgain()
    {
        var starter = new FakeStarter();

        var result = await Session(new FakePing(BrokerState.Answering), starter).EnsureRunningAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public async Task SilentBroker_IsStartedOnceAndWaitedFor()
    {
        var starter = new FakeStarter();
        var ping = new FakePing(BrokerState.Silent, BrokerState.Silent, BrokerState.Answering);

        var result = await Session(ping, starter).EnsureRunningAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, starter.Starts);
    }

    [Fact]
    public async Task LaterCalls_ReuseTheRunningBroker()
    {
        var starter = new FakeStarter();
        var session = Session(new StartedPing(starter), starter);

        await session.EnsureRunningAsync(CancellationToken.None);
        await session.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(1, starter.Starts);
    }

    [Fact]
    public async Task UntrustedInstall_IsRefusedAndNeverStarted()
    {
        var starter = new FakeStarter();

        var result = await Session(new FakePing(BrokerState.UntrustedInstall), starter).EnsureRunningAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public async Task DeclinedUac_IsNotAskedAgainDuringTheCooldown()
    {
        var starter = new FakeStarter(declined: true);
        var clock = DateTime.UtcNow;
        var session = Session(new FakePing(BrokerState.Silent), starter, () => clock);

        var first = await session.EnsureRunningAsync(CancellationToken.None);
        var second = await session.EnsureRunningAsync(CancellationToken.None);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(1, starter.Starts);

        clock = clock.AddMinutes(2);
        await session.EnsureRunningAsync(CancellationToken.None);
        Assert.Equal(2, starter.Starts);
    }

    [Fact]
    public async Task BrokerThatNeverAnswers_TimesOut()
    {
        var starter = new FakeStarter();
        var calls = 0;
        var clock = DateTime.UtcNow;
        var session = new BrokerSession(
            new FakePing(BrokerState.Silent),
            starter,
            @"C:\broker\scripthelper-broker.exe",
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(1),
            () => clock,
            delay => { calls++; clock += delay; return Task.CompletedTask; });

        var result = await session.EnsureRunningAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, starter.Starts);
        Assert.True(calls > 0);
    }

    [Fact]
    public async Task ConcurrentCalls_ShareOneStart()
    {
        var starter = new FakeStarter();
        var session = Session(new StartedPing(starter), starter);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.EnsureRunningAsync(CancellationToken.None)));

        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(1, starter.Starts);
    }
}
