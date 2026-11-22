using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class ProcessScriptRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-run-" + Guid.NewGuid());

    public ProcessScriptRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Script(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    [Fact]
    public void Exit_code_and_stdout_are_returned()
    {
        var script = Script("ok.ps1", "Write-Output 'hello'\nexit 3\n");

        var r = new ProcessScriptRunner().Run(script, Array.Empty<string>(), Generous);

        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("hello", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Stderr_is_returned_separately()
    {
        var script = Script("err.ps1", "[Console]::Error.WriteLine('oops')\n");

        var r = new ProcessScriptRunner().Run(script, Array.Empty<string>(), Generous);

        Assert.Contains("oops", r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("oops", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_reach_the_script_literally_and_no_shell_runs_them()
    {
        var script = Script("echo.ps1", "param($a)\nWrite-Output $a\n");
        var hostile = "a;Write-Output pwned \"quoted\" | whoami & calc";

        var r = new ProcessScriptRunner().Run(script, new[] { hostile }, Generous);

        // If a shell had split or run the text, the output would not be exactly this one line.
        Assert.Equal(hostile, r.Stdout.TrimEnd());
    }

    [Fact]
    public void Output_over_the_cap_is_truncated_and_marked()
    {
        var script = Script("big.ps1", "1..100000 | ForEach-Object { 'x' * 20 }\n");

        var r = new ProcessScriptRunner().Run(script, Array.Empty<string>(), Generous);

        Assert.True(r.Stdout.Length <= ProcessScriptRunner.MaxOutputChars + 64, $"length {r.Stdout.Length}");
        Assert.Contains("output truncated", r.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void A_run_past_the_timeout_is_killed_and_reported_as_timed_out()
    {
        var script = Script("hang.ps1", "Start-Sleep -Seconds 120\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new ProcessScriptRunner().Run(script, Array.Empty<string>(), TimeSpan.FromSeconds(3));

        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Timeout_kills_child_processes_too()
    {
        var pidFile = Path.Combine(_dir, "child.pid");
        var script = Script("tree.ps1",
            "param($PidFile)\n" +
            "$c = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -PassThru\n" +
            "$c.Id | Set-Content -LiteralPath $PidFile\n" +
            "Start-Sleep -Seconds 120\n");

        var r = new ProcessScriptRunner().Run(script, new[] { pidFile }, TimeSpan.FromSeconds(15));

        Assert.True(r.TimedOut);
        var childId = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.False(IsRunning(childId), "child process survived the timeout");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
