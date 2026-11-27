using System.IO.Pipes;
using System.Text;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class InstanceEventsTests
{
    [Fact]
    public void Parse_ReadsTheNameSeqAndData()
    {
        var e = InstanceEvents.Parse("""{"event":"documentOpened","seq":3,"data":{"file":"C:\\src\\a.cs"}}""");

        Assert.NotNull(e);
        Assert.Equal("documentOpened", e!.Name);
        Assert.Equal(3, e.Seq);
        Assert.Equal(@"C:\src\a.cs", InstanceEvents.FileOf(e));
    }

    [Fact]
    public void Parse_RefusesAMalformedLine()
    {
        Assert.Null(InstanceEvents.Parse("not json"));
        Assert.Null(InstanceEvents.Parse("""{"seq":1}"""));
    }

    [Fact]
    public void FileOf_ReadsVsCodesPathToo()
    {
        var e = InstanceEvents.Parse("""{"event":"documentOpened","seq":1,"data":{"path":"/w/b.ts"}}""");

        Assert.Equal("/w/b.ts", InstanceEvents.FileOf(e!));
    }

    [Fact]
    public void OpenFile_IsSetByOpenAndClearedByClosingTheSameFile()
    {
        var tracker = new OpenFileTracker();
        tracker.Apply(InstanceEvents.Parse("""{"event":"documentOpened","seq":1,"data":{"file":"C:\\a.cs"}}""")!);
        Assert.Equal(@"C:\a.cs", tracker.Current);

        tracker.Apply(InstanceEvents.Parse("""{"event":"documentClosing","seq":2,"data":{"file":"C:\\b.cs"}}""")!);
        Assert.Equal(@"C:\a.cs", tracker.Current);

        tracker.Apply(InstanceEvents.Parse("""{"event":"documentClosing","seq":3,"data":{"file":"C:\\a.cs"}}""")!);
        Assert.Null(tracker.Current);
    }

    // The client subscribes, reads events in order, and ends cleanly on "closed".
    [Fact]
    public async Task Client_ReadsEventsUntilClosed()
    {
        var pipe = "AiDevHelpers.Test.events." + Guid.NewGuid().ToString("N");
        var server = ServeOnce(pipe, new[]
        {
            """{"ok":true,"pid":5,"subscriptionId":"s1"}""",
            """{"event":"documentOpened","seq":1,"data":{"file":"C:\\a.cs"}}""",
            """{"event":"heartbeat","seq":2}""",
            """{"event":"closed","reason":"bridgeStopped"}""",
        });

        var seen = new List<string>();
        var end = await InstanceEventsClient.RunOnceAsync(pipe, e => seen.Add(e.Name), TimeSpan.FromSeconds(5), CancellationToken.None);

        await server;
        Assert.Equal(SessionEnd.Closed, end);
        // Heartbeats keep the session alive but are not passed on.
        Assert.Equal(new[] { "documentOpened" }, seen);
    }

    // No line for longer than the silence limit ends the session as disconnected.
    [Fact]
    public async Task Client_ReportsSilenceAsDisconnected()
    {
        var pipe = "AiDevHelpers.Test.events." + Guid.NewGuid().ToString("N");
        var server = ServeOnce(pipe, new[] { """{"ok":true,"pid":6,"subscriptionId":"s2"}""" }, holdOpenMs: 2000);

        var end = await InstanceEventsClient.RunOnceAsync(pipe, _ => { }, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await server;
        Assert.Equal(SessionEnd.Silent, end);
    }

    // A pipe that is not there is a refusal, not an exception: the caller retries.
    [Fact]
    public async Task Client_ReportsAMissingPipeAsRefused()
    {
        var end = await InstanceEventsClient.RunOnceAsync(
            "AiDevHelpers.Test.events.missing." + Guid.NewGuid().ToString("N"),
            _ => { },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(SessionEnd.Refused, end);
    }

    private static Task ServeOnce(string pipeName, string[] lines, int holdOpenMs = 0) => Task.Run(async () =>
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await reader.ReadLineAsync(); // the subscribe line
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line);
        }

        if (holdOpenMs > 0)
        {
            await Task.Delay(holdOpenMs);
        }
    });
}
