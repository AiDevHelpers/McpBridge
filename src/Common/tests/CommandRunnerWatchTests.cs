using System.Diagnostics;
using AiDevHelpers.Common.Processes;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class CommandRunnerWatchTests
{
    private static CommandRequest Ps(string command, TimeSpan? noOutput = null, Action<string>? onOutput = null) => new()
    {
        FileName = "powershell.exe",
        Arguments = ["-NoProfile", "-NonInteractive", "-Command", command],
        Timeout = TimeSpan.FromSeconds(30),
        NoOutputTimeout = noOutput,
        OnOutput = onOutput,
    };

    [Fact]
    public async Task ASilentProcess_IsKilledAfterTheNoOutputTimeout_AndReportedAsSilent()
    {
        var clock = Stopwatch.StartNew();

        var result = await CommandRunner.RunAsync(Ps("Start-Sleep -Seconds 30", TimeSpan.FromSeconds(1)));

        Assert.True(result.Silent);
        Assert.False(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the watchdog should end the run well before the 30 second sleep");
    }

    [Fact]
    public async Task OutputKeepsTheWatchdogQuiet()
    {
        var result = await CommandRunner.RunAsync(Ps("1..5 | ForEach-Object { Write-Output $_; Start-Sleep -Milliseconds 500 }", TimeSpan.FromSeconds(2)));

        Assert.False(result.Silent);
        Assert.True(result.Success);
        Assert.Contains("5", result.StdOut);
    }

    [Fact]
    public async Task OnOutput_SeesEachChunk_FromBothStreams()
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var result = await CommandRunner.RunAsync(Ps("Write-Output out; [Console]::Error.WriteLine('err')", onOutput: seen.Enqueue));

        Assert.True(result.Success);
        var all = string.Concat(seen);
        Assert.Contains("out", all);
        Assert.Contains("err", all);
    }

    [Fact]
    public async Task WithoutANoOutputTimeout_ASlowQuietProcessIsNotKilled()
    {
        var result = await CommandRunner.RunAsync(Ps("Start-Sleep -Seconds 2; exit 3"));

        Assert.False(result.Silent);
        Assert.Equal(3, result.ExitCode);
    }
}
