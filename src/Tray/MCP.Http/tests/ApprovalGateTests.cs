using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class ApprovalGateTests
{
    private sealed class FakeChannel(Func<ApprovalRequest, Task<ApprovalResponse>> reply) : IApprovalChannel
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return reply(request);
        }
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static FakeChannel Approving() => new(r => Task.FromResult(new ApprovalResponse(r.Nonce, r.ArgumentsHash, true)));

    private static ApprovalGate Gate(IApprovalChannel channel, TimeSpan? timeout = null) =>
        new(channel, timeout ?? TimeSpan.FromSeconds(5), () => "nonce-1");

    [Fact]
    public async Task ASafeToolRunsWithoutAskingTheTray()
    {
        var channel = Approving();

        var result = await Gate(channel).CheckAsync("ide_list_instances", "{}", CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task ADangerousToolAsksWithTheNonceAndTheHashOfItsArguments()
    {
        var channel = Approving();
        const string args = "{\"script\":\"build.ps1\"}";

        var result = await Gate(channel).CheckAsync("script_run", args, CancellationToken.None);

        Assert.True(result.Allowed);
        var request = Assert.Single(channel.Requests);
        Assert.Equal("nonce-1", request.Nonce);
        Assert.Equal("script_run", request.Tool);
        Assert.Equal(Sha256(args), request.ArgumentsHash);
        Assert.Contains("build.ps1", request.Summary);
    }

    [Fact]
    public async Task ADeniedCallDoesNotRun()
    {
        var channel = new FakeChannel(r => Task.FromResult(new ApprovalResponse(r.Nonce, r.ArgumentsHash, false)));

        var result = await Gate(channel).CheckAsync("script_run", "{}", CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public async Task AnUnclassifiedToolNeedsApproval()
    {
        var channel = Approving();

        var result = await Gate(channel).CheckAsync("a_tool_added_later", "{}", CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Single(channel.Requests);
    }

    [Fact]
    public async Task AChannelErrorFailsClosed()
    {
        var channel = new FakeChannel(_ => throw new IOException("no tray"));

        var result = await Gate(channel).CheckAsync("script_run", "{}", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task NoAnswerBeforeTheTimeoutFailsClosed()
    {
        var channel = new FakeChannel(_ => new TaskCompletionSource<ApprovalResponse>().Task);

        var result = await Gate(channel, TimeSpan.FromMilliseconds(50)).CheckAsync("script_run", "{}", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task AnAnswerForAnotherNonceIsRefused()
    {
        var channel = new FakeChannel(r => Task.FromResult(new ApprovalResponse("other", r.ArgumentsHash, true)));

        var result = await Gate(channel).CheckAsync("script_run", "{}", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task AnAnswerForOtherArgumentsIsRefused()
    {
        var channel = new FakeChannel(r => Task.FromResult(new ApprovalResponse(r.Nonce, Sha256("different"), true)));

        var result = await Gate(channel).CheckAsync("script_run", "{}", CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task CancellationByTheCallerPropagates()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeChannel(async _ =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
            return null!;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gate(channel).CheckAsync("script_run", "{}", cts.Token));
    }
}
