using System.Text.Json;
using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class InstanceActionsTests
{
    private static VsInstance Vs(int pid, string ide = "VisualStudio") =>
        new(pid, $"AiDevHelpers.VS.{pid}", @"C:\src\a\a.sln", DateTime.UtcNow, ide, []);

    private sealed class Recorder
    {
        public List<(string Action, string Args, int Pid)> Sent { get; } = [];
    }

    private static InstanceActions Make(Recorder rec, string debuggerMode, bool confirm, string reply = """{"ok":true,"result":{"applied":true}}""") =>
        new(
            send: (action, args, pid, ct) =>
            {
                rec.Sent.Add((action, JsonSerializer.Serialize(args), pid));
                return Task.FromResult(action == "debugState"
                    ? "{\"ok\":true,\"result\":{\"debugger_mode\":\"" + debuggerMode + "\"}}"
                    : reply);
            },
            confirm: (_) => Task.FromResult(confirm));

    // The close is refused while a debug session runs; nothing is sent to close the instance.
    [Fact]
    public async Task Close_IsRefusedWhileTheDebuggerIsRunning()
    {
        var rec = new Recorder();

        var result = await Make(rec, "dbgRunMode", confirm: true).CloseAsync(Vs(10), CancellationToken.None);

        Assert.Equal(ActionOutcome.Refused, result.Outcome);
        Assert.DoesNotContain(rec.Sent, s => s.Action == "closeSolution");
    }

    // A close sends closeSolution to that instance only, with save first and confirm.
    [Fact]
    public async Task Close_SendsCloseSolutionWithSaveFirstAndConfirmToThatPid()
    {
        var rec = new Recorder();

        var result = await Make(rec, "dbgDesignMode", confirm: true).CloseAsync(Vs(11), CancellationToken.None);

        Assert.Equal(ActionOutcome.Closed, result.Outcome);
        var close = Assert.Single(rec.Sent, s => s.Action == "closeSolution");
        Assert.Equal(11, close.Pid);
        Assert.Contains("\"saveFirst\":true", close.Args);
        Assert.Contains("\"confirm\":true", close.Args);
    }

    // A deny is a deny: nothing is sent to close the instance.
    [Fact]
    public async Task Close_DeniedLeavesTheInstanceUntouched()
    {
        var rec = new Recorder();

        var result = await Make(rec, "dbgDesignMode", confirm: false).CloseAsync(Vs(12), CancellationToken.None);

        Assert.Equal(ActionOutcome.Denied, result.Outcome);
        Assert.DoesNotContain(rec.Sent, s => s.Action == "closeSolution");
    }

    // VS Code is not closed from the page yet: refused, nothing sent.
    [Fact]
    public async Task Close_IsRefusedForVsCode()
    {
        var rec = new Recorder();

        var result = await Make(rec, "dbgDesignMode", confirm: true).CloseAsync(Vs(13, "VsCode"), CancellationToken.None);

        Assert.Equal(ActionOutcome.Refused, result.Outcome);
        Assert.Empty(rec.Sent);
    }

    // A bridge error is reported as a failure, not thrown.
    [Fact]
    public async Task Close_ABridgeErrorIsAFailure()
    {
        var rec = new Recorder();
        var actions = Make(rec, "dbgDesignMode", confirm: true, reply: """{"ok":false,"error":"busy"}""");

        var result = await actions.CloseAsync(Vs(14), CancellationToken.None);

        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("busy", result.Message);
    }
}
