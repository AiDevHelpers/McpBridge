using System.Text.Json;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class OpenFileWatcherTests
{
    private static VsInstance Vs(int pid, string ide = "VisualStudio") =>
        new(pid, $"AiDevHelpers.{(ide == "VsCode" ? "VsCode" : "VS")}.{pid}", @"C:\src\a\a.sln", DateTime.UtcNow, ide, []);

    private static EventLine Ev(string name, string file) =>
        new(name, 1, JsonSerializer.SerializeToElement(new { file }));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The condition was not reached in time.");
    }

    private static async Task Hold(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled when the instance goes away.
        }
    }

    // A watcher starts one session per instance, and reports the file each one has open.
    [Fact]
    public async Task Sync_StartsASessionPerInstanceAndReportsItsOpenFile()
    {
        var started = new List<string>();
        using var watcher = new OpenFileWatcher((pipe, onEvent, ct) =>
        {
            started.Add(pipe);
            onEvent(Ev("documentOpened", @"C:\a.cs"));
            return Hold(ct);
        });

        watcher.Sync([Vs(1)]);

        await WaitFor(() => watcher.Current.TryGetValue(1, out var file) && file == @"C:\a.cs");
        Assert.Equal(["AiDevHelpers.VS.1.events"], started);
    }

    // VS Code's events pipe is named by its own prefix, and its open file comes from the path field.
    [Fact]
    public async Task Sync_UsesTheVsCodePipeForAVsCodeInstance()
    {
        var started = new List<string>();
        using var watcher = new OpenFileWatcher((pipe, onEvent, ct) =>
        {
            started.Add(pipe);
            return Hold(ct);
        });

        watcher.Sync([Vs(2, "VsCode")]);

        await WaitFor(() => started.Count == 1);
        Assert.Equal(["AiDevHelpers.VsCode.2.events"], started);
    }

    // An instance that leaves the list has its session cancelled and its file dropped.
    [Fact]
    public async Task Sync_CancelsTheSessionOfAnInstanceThatLeaves()
    {
        CancellationToken seen = default;
        using var watcher = new OpenFileWatcher((pipe, onEvent, ct) =>
        {
            seen = ct;
            onEvent(Ev("documentOpened", @"C:\a.cs"));
            return Hold(ct);
        });

        watcher.Sync([Vs(3)]);
        await WaitFor(() => watcher.Current.ContainsKey(3));

        watcher.Sync([]);

        Assert.True(seen.IsCancellationRequested);
        Assert.False(watcher.Current.ContainsKey(3));
    }

    // The open file is the one the instance has open now: closing that file clears it.
    [Fact]
    public async Task Sync_ClearsTheFileWhenItIsClosed()
    {
        using var watcher = new OpenFileWatcher((pipe, onEvent, ct) =>
        {
            onEvent(Ev("documentOpened", @"C:\a.cs"));
            onEvent(Ev("documentClosing", @"C:\a.cs"));
            return Hold(ct);
        });

        watcher.Sync([Vs(4)]);

        await WaitFor(() => watcher.Current.ContainsKey(4) && watcher.Current[4] is null);
    }

    // The rows take the file from the watcher; a row with no reading shows no file.
    [Fact]
    public void WithFiles_SetsTheOpenFileOnEachRow()
    {
        var rows = new List<InstanceRow>
        {
            new(1, "VisualStudio", "a", "main", @"C:\a.sln", DateTime.UtcNow, "u"),
            new(2, "VsCode", "b", "main", "/w/b", DateTime.UtcNow, "u"),
        };
        var files = new Dictionary<int, string?> { [1] = @"C:\a\Program.cs" };

        var result = InstanceRowFiles.WithFiles(rows, files);

        Assert.Equal(@"C:\a\Program.cs", result[0].OpenFile);
        Assert.Null(result[1].OpenFile);
    }
}
