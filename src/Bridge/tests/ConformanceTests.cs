using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

/// <summary>
/// Runs the shared fixtures in conformance/fixtures.json against <see cref="BridgeServer"/>. The VS Code bridge
/// is held to the same fixtures on the TypeScript side.
/// </summary>
public sealed class ConformanceTests
{
    private sealed class SilentLogger : IBridgeLogger
    {
        public void LogDebug(string message) { }

        public void LogInfo(string message) { }

        public void LogError(string message) { }

        public void LogException(Exception exception) { }
    }

    public static IEnumerable<object[]> Fixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json")));
        foreach (var fixture in doc.RootElement.EnumerateObject())
        {
            // A fixture may name the bridges it applies to ("visualstudio", "vscode"). Without the field it applies to all.
            if (fixture.Value.TryGetProperty("bridges", out var bridges) && !IsForBridge(bridges, "visualstudio"))
            {
                continue;
            }

            yield return new object[] { fixture.Name };
        }
    }

    private static bool IsForBridge(JsonElement bridges, string bridge)
    {
        foreach (var name in bridges.EnumerateArray())
        {
            if (name.GetString() == bridge)
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Reply_MatchesFixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json")));
        var fixture = doc.RootElement.GetProperty(name);

        var pipe = $"AiDevHelpers.VS.Conformance.{Guid.NewGuid():N}";
        using var server = new BridgeServer(
            pipe,
            (action, _) => Task.FromException<object>(new KeyNotFoundException($"Unknown action {action}")),
            () => null,
            new SilentLogger());
        server.Start();

        var raw = fixture.TryGetProperty("raw", out var rawProp)
            ? rawProp.GetString()!
            : fixture.GetProperty("request").GetRawText();
        var reply = await SendLineAsync(pipe, raw);

        using var replyDoc = JsonDocument.Parse(reply);
        var root = replyDoc.RootElement;

        Assert.Equal(fixture.GetProperty("ok").GetBoolean(), root.GetProperty("ok").GetBoolean());
        foreach (var key in fixture.GetProperty("reply_keys").EnumerateArray())
        {
            Assert.True(root.TryGetProperty(key.GetString()!, out _), $"reply is missing '{key.GetString()}'");
        }
    }

    private static async Task<string> SendLineAsync(string pipeName, string line)
    {
        // The server listens on a background task; on a loaded CI runner the first connect can outrun it. Retry until it is up.
        NamedPipeClientStream? client = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var attempt = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await attempt.ConnectAsync(500);
                client = attempt;
                break;
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                attempt.Dispose();
            }
        }

        using var connected = client!;
        using var reader = new StreamReader(connected, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(connected, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(line);
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}
