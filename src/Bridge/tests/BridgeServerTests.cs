using System;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

public sealed class BridgeServerTests
{
    private sealed class SilentLogger : IBridgeLogger
    {
        public void LogDebug(string message) { }

        public void LogInfo(string message) { }

        public void LogError(string message) { }

        public void LogException(Exception exception) { }
    }

    private static string NewPipeName() => $"AiDevHelpers.VS.Test.{Guid.NewGuid():N}";

    private static BridgeServer NewServer(string pipeName) =>
        new(pipeName, (_, _) => Task.FromResult<object>(new { }), () => null, new SilentLogger());

    private static async Task<string?> SendAsync(string pipeName, string request, int timeoutMs = 3000)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeoutMs);
        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(request);
        return await reader.ReadLineAsync();
    }

    [Fact]
    public async Task StopClosesThePipe_AndStartReopensIt()
    {
        var pipe = NewPipeName();
        using var server = NewServer(pipe);

        server.Start();
        var first = await SendWithRetryAsync(pipe, "{\"action\":\"health\"}");
        Assert.NotNull(first);
        Assert.Contains("\"listener_running\":true", first);

        server.Stop();
        Assert.False(server.IsRunning);
        await WaitForPipeClosedAsync(pipe);

        server.Start();
        Assert.True(server.IsRunning);
        var second = await SendWithRetryAsync(pipe, "{\"action\":\"health\"}");
        Assert.NotNull(second);
        Assert.Contains("\"listener_running\":true", second);
    }

    // Waits for the pipe to go away instead of sleeping a fixed time, which was flaky on a slow runner.
    private static async Task WaitForPipeClosedAsync(string pipeName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(200);
            }
            catch (TimeoutException)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The pipe was still open 10 seconds after Stop.");
    }

    // After a restart the new listener may need a moment to create its pipe, so retry the connect.
    private static async Task<string?> SendWithRetryAsync(string pipeName, string request)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return await SendAsync(pipeName, request, timeoutMs: 1000);
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public void StartTwice_DoesNotStartASecondListener()
    {
        var pipe = NewPipeName();
        using var server = NewServer(pipe);

        server.Start();
        server.Start();

        Assert.True(server.IsRunning);
        server.Stop();
    }

    [Fact]
    public void CreatePipeSecurity_AllowsTheCurrentUser_SoANonElevatedClientCanConnectToAnElevatedIde()
    {
        // An elevated Visual Studio creates the pipe with its elevated token. The default ACL then refuses the same user's
        // non-elevated MCP server, because the Administrators group is deny-only in that token. The user's own SID must be granted.
        var security = BridgeServer.CreatePipeSecurity();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;

        var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
        Assert.Contains(
            rules.Cast<System.IO.Pipes.PipeAccessRule>(),
            rule => rule.IdentityReference == user
                && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
    }

    [Fact]
    public void CreatePipeSecurity_DoesNotGrantEveryone()
    {
        var security = BridgeServer.CreatePipeSecurity();
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);

        var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
        Assert.DoesNotContain(rules.Cast<System.IO.Pipes.PipeAccessRule>(), rule => rule.IdentityReference == everyone);
    }
}
