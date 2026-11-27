using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

// The contract between the MCP server and the tray, over a real pipe: the MCP side (ApprovalPipeChannel through ApprovalGate) asks, the tray
// side (ApprovalPipeHost) answers, and only an exact yes runs the call.
public sealed class ApprovalPipeHostTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    private static string UniquePipe() => ApprovalWire.PipeNamePrefix + "test." + Guid.NewGuid().ToString("N");

    private static async Task<bool> AskAsync(string pipe, string tool = "terminal_session")
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await ApprovalGate.ApproveAsync(new ApprovalPipeChannel(pipe), tool, "{}", () => Now, cts.Token, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AYesFromTheTrayRunsTheCall()
    {
        var pipe = UniquePipe();
        using var host = new CancellationTokenSource();
        var serving = ApprovalPipeHost.RunAsync(pipe, _ => Task.FromResult(true), host.Token);

        Assert.True(await AskAsync(pipe));

        host.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    [Fact]
    public async Task ANoFromTheTrayRefusesTheCall()
    {
        var pipe = UniquePipe();
        using var host = new CancellationTokenSource();
        var serving = ApprovalPipeHost.RunAsync(pipe, _ => Task.FromResult(false), host.Token);

        Assert.False(await AskAsync(pipe));

        host.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    [Fact]
    public async Task WithNoTrayTheCallIsRefused()
    {
        Assert.False(await AskAsync(UniquePipe()));
    }

    [Fact]
    public async Task ASecondTrayCannotTakeOverTheName()
    {
        var pipe = UniquePipe();
        using var host = new CancellationTokenSource();
        var serving = ApprovalPipeHost.RunAsync(pipe, _ => Task.FromResult(true), host.Token);
        await Task.Delay(200);

        // Windows refuses the name with access denied or a pipe error, depending on the call; either way the second host does not start.
        await Assert.ThrowsAnyAsync<Exception>(() => ApprovalPipeHost.RunAsync(pipe, _ => Task.FromResult(true), CancellationToken.None))
            .ContinueWith(t => Assert.True(t.Result is IOException or UnauthorizedAccessException), TaskScheduler.Default);

        host.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }
}
