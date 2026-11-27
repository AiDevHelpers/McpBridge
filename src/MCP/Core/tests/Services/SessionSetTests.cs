using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class SessionSetTests
{
    private sealed class Client(string name)
    {
        public string Name { get; } = name;

        public List<string> Received { get; } = [];
    }

    [Fact]
    public async Task BroadcastReachesEveryRememberedSession()
    {
        var set = new SessionSet<Client>();
        var a = new Client("a");
        var b = new Client("b");
        set.Set("a", a);
        set.Set("b", b);

        await set.BroadcastAsync(c => { c.Received.Add("hello"); return Task.CompletedTask; });

        Assert.Equal(["hello"], a.Received);
        Assert.Equal(["hello"], b.Received);
    }

    [Fact]
    public async Task RememberingTheSameKeyReplacesTheEarlierSession()
    {
        var set = new SessionSet<Client>();
        var first = new Client("first");
        var second = new Client("second");
        set.Set("same", first);
        set.Set("same", second);

        await set.BroadcastAsync(c => { c.Received.Add("x"); return Task.CompletedTask; });

        Assert.Empty(first.Received);
        Assert.Single(second.Received);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public async Task ASessionWhoseSendFailsIsDroppedAndTheOthersStillReceive()
    {
        var set = new SessionSet<Client>();
        var gone = new Client("gone");
        var alive = new Client("alive");
        set.Set("gone", gone);
        set.Set("alive", alive);

        await set.BroadcastAsync(c => c == gone ? throw new IOException("client closed") : Record(c));
        await set.BroadcastAsync(Record);

        Assert.Equal(2, alive.Received.Count);
        Assert.Empty(gone.Received);
        Assert.Equal(1, set.Count);

        static Task Record(Client c)
        {
            c.Received.Add("x");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ACancelledSendIsNotTreatedAsAFailedSession()
    {
        var set = new SessionSet<Client>();
        set.Set("a", new Client("a"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => set.BroadcastAsync(_ => throw new OperationCanceledException()));

        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void RemoveForgetsASession()
    {
        var set = new SessionSet<Client>();
        set.Set("a", new Client("a"));

        Assert.True(set.Remove("a"));
        Assert.False(set.Remove("a"));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public async Task BroadcastToNoSessionsDoesNothing()
    {
        var set = new SessionSet<Client>();

        await set.BroadcastAsync(_ => throw new InvalidOperationException("must not be called"));
    }
}
