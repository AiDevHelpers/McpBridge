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

    [Fact]
    public async Task AReplyWithoutACountHasNoSessions()
    {
        var snapshot = await HealthPoller.ReadOnceAsync(new JsonReply("{\"ok\":true}"), Health, () => null, CancellationToken.None);

        Assert.Null(snapshot.Sessions);
    }
}

