using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.MCP.Http.Tests;

public sealed class NamedPipeApprovalChannelTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string NewPipeName() => "AiDevHelpers.Test.Approval." + Guid.NewGuid().ToString("N");

    private static Task ServeOnceAsync(string pipeName, Func<ApprovalRequest, ApprovalResponse> reply)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        return Task.Run(async () =>
        {
            using (server)
            {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync();
                var request = JsonSerializer.Deserialize<ApprovalRequest>(line!, Json)!;
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply(request), Json));
            }
        });
    }

    private static ApprovalRequest SampleRequest() => new("n1", "script_run", "abc", "{}");

    [Fact]
    public async Task ARequestRoundTripsThroughThePipe()
    {
        var name = NewPipeName();
        var served = ServeOnceAsync(name, r => new ApprovalResponse(r.Nonce, r.ArgumentsHash, true));
        var channel = new NamedPipeApprovalChannel(name, _ => true, TimeSpan.FromSeconds(5));

        var response = await channel.RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(new ApprovalResponse("n1", "abc", true), response);
        await served;
    }

    [Fact]
    public async Task AServerThatIsNotTheTrayIsRefused()
    {
        var name = NewPipeName();
        var served = ServeOnceAsync(name, r => new ApprovalResponse(r.Nonce, r.ArgumentsHash, true));
        var channel = new NamedPipeApprovalChannel(name, _ => false, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<UntrustedApprovalServerException>(() => channel.RequestAsync(SampleRequest(), CancellationToken.None));

        await served.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
    }

    [Fact]
    public async Task ThePidOfTheServerIsHandedToTheTrustCheck()
    {
        var name = NewPipeName();
        var served = ServeOnceAsync(name, r => new ApprovalResponse(r.Nonce, r.ArgumentsHash, true));
        var seen = 0;
        var channel = new NamedPipeApprovalChannel(name, pid => { seen = pid; return true; }, TimeSpan.FromSeconds(5));

        _ = await channel.RequestAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(Environment.ProcessId, seen);
        await served;
    }

    [Fact]
    public async Task ANotRunningTrayFailsFastInsteadOfWaiting()
    {
        var channel = new NamedPipeApprovalChannel(NewPipeName(), _ => true, TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => channel.RequestAsync(SampleRequest(), CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
