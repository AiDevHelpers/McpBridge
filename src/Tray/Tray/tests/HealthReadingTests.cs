using System.Net;
using System.Net.Http;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class HealthReadingTests
{
    private static readonly Uri Health = new("http://127.0.0.1:5058/healthz");

    private sealed class JsonReply(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Fact]
    public async Task TheSessionCountIsReadFromHealthz()
    {
        var snapshot = await HealthPoller.ReadOnceAsync(new JsonReply("{\"ok\":true,\"sessions\":2}"), Health, () => null, CancellationToken.None);

        Assert.Equal(TrayConnection.Connected, snapshot.State);
        Assert.Equal(2, snapshot.Sessions);
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no server");
    }

    // A port-conflict status left by an earlier run must not show as "in use" once nothing holds the port.
    [Fact]
    public async Task AStalePortConflictIsIgnoredWhenNothingHoldsThePort()
    {
        const string status = "{\"ok\":false,\"state\":\"portConflict\",\"port\":5058}";

        var snapshot = await HealthPoller.ReadOnceAsync(new Unreachable(), Health, () => status, CancellationToken.None, _ => false);

        Assert.Equal(TrayConnection.Disconnected, snapshot.State);
    }

    // A healthy /healthz reply is the truth; a stale status file does not override it.
    [Fact]
    public async Task AHealthyReplyWinsOverAStalePortConflict()
    {
        const string status = "{\"ok\":false,\"state\":\"portConflict\",\"port\":5058}";

        var snapshot = await HealthPoller.ReadOnceAsync(new JsonReply("{\"ok\":true}"), Health, () => status, CancellationToken.None, _ => true);

        Assert.Equal(TrayConnection.Connected, snapshot.State);
    }

    [Fact]
    public async Task AReplyWithoutACountHasNoSessions()
    {
        var snapshot = await HealthPoller.ReadOnceAsync(new JsonReply("{\"ok\":true}"), Health, () => null, CancellationToken.None);

        Assert.Null(snapshot.Sessions);
    }
}

