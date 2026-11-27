using System.IO.Pipes;
using System.Text;
using AiDevHelpers.Tray.Approval;
using Xunit;
using Http = AiDevHelpers.MCP.Http;

namespace AiDevHelpers.Tray.Tests;

/// <summary>
/// The contract between the HTTP MCP's real pipe client (<see cref="Http.NamedPipeApprovalChannel"/>) and the tray's server.
/// </summary>
public sealed class ApprovalServerTests
{
    private sealed class FakePrompt(Func<ApprovalRequest, Task<bool>> answer) : IApprovalPrompt
    {
        public List<ApprovalRequest> Asked { get; } = [];

        public Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return answer(request);
        }
    }

    private static string NewPipeName() => "AiDevHelpers.Test.TrayApproval." + Guid.NewGuid().ToString("N");

    private static Http.ApprovalRequest SampleRequest() => new("nonce-1", "script_run", "hash-1", "{\"script\":\"x.ps1\"}");

    private static Http.NamedPipeApprovalChannel Client(string pipe) =>
        new(pipe, pid => pid == Environment.ProcessId, TimeSpan.FromSeconds(5));

    private static async Task<(ApprovalServer Server, CancellationTokenSource Stop, Task Run)> StartAsync(string pipe, FakePrompt prompt, Func<int, bool>? trust = null, TimeSpan? timeout = null)
    {
        var server = new ApprovalServer(pipe, trust ?? (pid => pid == Environment.ProcessId), prompt, timeout ?? TimeSpan.FromSeconds(5));
        var stop = new CancellationTokenSource();
        var run = server.RunAsync(stop.Token);
        await server.Ready;
        return (server, stop, run);
    }

    private static async Task StopAsync(CancellationTokenSource stop, Task run)
    {
        await stop.CancelAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task AnApprovedCallComesBackWithTheNonceAndTheHash()
    {
        var pipe = NewPipeName();
        var prompt = new FakePrompt(_ => Task.FromResult(true));
        var (_, stop, run) = await StartAsync(pipe, prompt);

        var response = await Client(pipe).RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(new Http.ApprovalResponse("nonce-1", "hash-1", true), response);
        Assert.Equal("script_run", Assert.Single(prompt.Asked).Tool);
        await StopAsync(stop, run);
    }

    [Fact]
    public async Task ADeniedCallComesBackDenied()
    {
        var pipe = NewPipeName();
        var (_, stop, run) = await StartAsync(pipe, new FakePrompt(_ => Task.FromResult(false)));

        var response = await Client(pipe).RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.False(response.Approved);
        await StopAsync(stop, run);
    }

    [Fact]
    public async Task APromptThatTakesTooLongIsDenied()
    {
        var pipe = NewPipeName();
        var (_, stop, run) = await StartAsync(pipe, new FakePrompt(_ => new TaskCompletionSource<bool>().Task), timeout: TimeSpan.FromMilliseconds(100));

        var response = await Client(pipe).RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.False(response.Approved);
        await StopAsync(stop, run);
    }

    [Fact]
    public async Task APromptThatThrowsIsDenied()
    {
        var pipe = NewPipeName();
        var (_, stop, run) = await StartAsync(pipe, new FakePrompt(_ => throw new InvalidOperationException("dialog failed")));

        var response = await Client(pipe).RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.False(response.Approved);
        await StopAsync(stop, run);
    }

    [Fact]
    public async Task AClientThatIsNotTheHttpMcpIsDroppedWithoutAPrompt()
    {
        var pipe = NewPipeName();
        var prompt = new FakePrompt(_ => Task.FromResult(true));
        var (_, stop, run) = await StartAsync(pipe, prompt, trust: _ => false);

        await Assert.ThrowsAnyAsync<Exception>(() => Client(pipe).RequestAsync(SampleRequest(), CancellationToken.None));

        Assert.Empty(prompt.Asked);
        await StopAsync(stop, run);
    }

    [Fact]
    public async Task AMalformedRequestGetsNoPromptAndTheServerKeepsServing()
    {
        var pipe = NewPipeName();
        var prompt = new FakePrompt(_ => Task.FromResult(true));
        var (_, stop, run) = await StartAsync(pipe, prompt);

        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            var bytes = Encoding.UTF8.GetBytes("this is not json\n");
            await raw.WriteAsync(bytes);
            await raw.FlushAsync();
            using var reader = new StreamReader(raw);
            _ = await reader.ReadLineAsync();
        }

        var response = await Client(pipe).RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.True(response.Approved);
        Assert.Single(prompt.Asked);
        await StopAsync(stop, run);
    }

    [Fact]
    public async Task AProcessThatAlreadyServesThePipeNameIsReportedAsBusy()
    {
        var pipe = NewPipeName();
        var (_, stop, run) = await StartAsync(pipe, new FakePrompt(_ => Task.FromResult(true)));

        var second = new ApprovalServer(pipe, _ => true, new FakePrompt(_ => Task.FromResult(true)), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ApprovalPipeBusyException>(() => second.RunAsync(CancellationToken.None));

        await StopAsync(stop, run);
    }

    [Fact]
    public async Task StoppingTheServerEndsItsRunTask()
    {
        var pipe = NewPipeName();
        var (_, stop, run) = await StartAsync(pipe, new FakePrompt(_ => Task.FromResult(true)));

        await StopAsync(stop, run);

        Assert.True(run.IsCompleted);
    }
}
