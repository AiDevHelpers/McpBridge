using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void AssemblyNameMatchesTheProject()
    {
        Assert.Equal("AiDevHelpers.Tray", typeof(ProjectMarker).Assembly.GetName().Name);
    }

    private sealed class FixedReply(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code));
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    private static readonly Uri Health = new("http://127.0.0.1:5058/healthz");

    [Fact]
    public async Task AHealthyReplyIsConnected()
    {
        var snapshot = await HealthPoller.ReadOnceAsync(new FixedReply(HttpStatusCode.OK), Health, () => null, CancellationToken.None);

        Assert.Equal(TrayConnection.Connected, snapshot.State);
    }

    [Fact]
    public async Task NoReplyIsDisconnected()
    {
        var snapshot = await HealthPoller.ReadOnceAsync(new Unreachable(), Health, () => null, CancellationToken.None);

        Assert.Equal(TrayConnection.Disconnected, snapshot.State);
    }

    [Fact]
    public async Task TheFeedEmitsTheStateToASubscriber()
    {
        var feed = HealthFeed.Watch(new FixedReply(HttpStatusCode.OK), Health, () => null, TimeSpan.FromMilliseconds(20));

        var first = await feed.FirstAsync().ToTask();

        Assert.Equal(TrayConnection.Connected, first.State);
    }

    [Fact]
    public void AServiceStateMapsToTheTrayStates()
    {
        Assert.Equal(ServiceState.Running, ServiceStatus.Map(System.ServiceProcess.ServiceControllerStatus.Running));
        Assert.Equal(ServiceState.Stopped, ServiceStatus.Map(System.ServiceProcess.ServiceControllerStatus.Stopped));
        Assert.Equal(ServiceState.Transitioning, ServiceStatus.Map(System.ServiceProcess.ServiceControllerStatus.StartPending));
        Assert.Equal(ServiceState.NotInstalled, ServiceStatus.Map(null));
    }

    [Fact]
    public async Task APortConflictIsVisibleEvenWhenNothingAnswers()
    {
        var status = "{\"ok\":false,\"state\":\"portConflict\",\"port\":5058,\"cause\":\"address already in use\"}";

        var snapshot = await HealthPoller.ReadOnceAsync(new Unreachable(), Health, () => status, CancellationToken.None);

        Assert.Equal(TrayConnection.PortConflict, snapshot.State);
        Assert.Contains("address already in use", snapshot.Detail, StringComparison.Ordinal);
    }
}
