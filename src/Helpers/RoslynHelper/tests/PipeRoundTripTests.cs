using AiDevHelpers.Bridge;
using AiDevHelpers.Helpers.RoslynHelper;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class PipeRoundTripTests
{
    private sealed class NullLogger : IBridgeLogger
    {
        public void LogDebug(string message) { }
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogException(Exception exception) { }
    }

    [Fact]
    public async Task Health_request_over_the_pipe_gets_ok_and_pid()
    {
        var pipeName = "AiDevHelpers.Roslyn.test." + Guid.NewGuid().ToString("N");
        const string solution = @"C:\repo\App.slnx";
        var handler = new RoslynRequestHandler(() => solution);
        using var server = new BridgeServer(pipeName, handler.InvokeAsync, () => solution, new NullLogger());
        server.Start();

        try
        {
            using var client = await ConnectAsync(pipeName);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            await writer.WriteLineAsync("{\"action\":\"health\"}");
            var line = await reader.ReadLineAsync();

            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line!);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
        }
        finally
        {
            server.Stop();
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        // The server creates its pipe instance on a background task, so retry briefly until it exists.
        for (var attempt = 0; ; attempt++)
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(500);
                return client;
            }
            catch (Exception ex) when (attempt < 20 && (ex is TimeoutException or IOException))
            {
                client.Dispose();
                await Task.Delay(100);
            }
        }
    }
}
