#pragma warning disable CA2000 // Test helpers hand disposables to the session under test; each test is short-lived.
namespace AiDevHelpers.Helpers.TerminalControl.Tests;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit;
using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Core;
using AiDevHelpers.Helpers.TerminalControl.Models;
using AiDevHelpers.Helpers.TerminalControl.Output;

/// <summary>
/// Runs the real wrapper script with pwsh directly (no Windows Terminal window).
/// Covers the pipe protocol end to end.
/// </summary>
public sealed class StandardTerminalSessionE2ETests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());
    private readonly List<Process> _tabProcesses = new();

    public void Dispose()
    {
        foreach (var process in _tabProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            process.Dispose();
        }

        _loggerFactory.Dispose();
    }

    [Fact]
    public async Task ExecuteInTab_RunsCommandsThroughWrapper()
    {
        var wrapper = FindWrapperScript();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = new StandardTerminalSession(
            _loggerFactory.CreateLogger<StandardTerminalSession>(),
            pipes,
            new DirectTabLauncher(wrapper, _tabProcesses));
        await session.InitializeAsync(new TerminalSessionConfig());

        var tab = await session.CreateTabAsync("e2e");

        var ok = await session.ExecuteInTabAsync(tab.TabId, "Write-Output 'hello'", timeout: TimeSpan.FromSeconds(60));
        Assert.True(ok.Success);
        Assert.Equal(0, ok.ExitCode);
        Assert.Equal("hello", ok.Stdout);

        var failed = await session.ExecuteInTabAsync(tab.TabId, "throw 'boom'", timeout: TimeSpan.FromSeconds(60));
        Assert.False(failed.Success);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("boom", failed.Stderr ?? string.Empty, StringComparison.Ordinal);

        // Output after an error must still reach the next command, in order.
        var after = await session.ExecuteInTabAsync(tab.TabId, "Write-Output 'after'", timeout: TimeSpan.FromSeconds(60));
        Assert.True(after.Success);
        Assert.Equal("after", after.Stdout);

        await session.CloseTabAsync(tab.TabId);
        Assert.Null(await session.GetTabAsync(tab.TabId));
    }

    [Fact]
    public async Task Wrapper_EchoesCommandsAndOutputToTheWindow()
    {
        var wrapper = FindWrapperScript();
        var console = new System.Text.StringBuilder();
        using var pipes = new NamedPipeManager(_loggerFactory.CreateLogger<NamedPipeManager>());
        var session = new StandardTerminalSession(
            _loggerFactory.CreateLogger<StandardTerminalSession>(),
            pipes,
            new DirectTabLauncher(wrapper, _tabProcesses, console));
        await session.InitializeAsync(new TerminalSessionConfig());
        var tab = await session.CreateTabAsync("echo");

        await session.ExecuteInTabAsync(tab.TabId, "Write-Output 'visible-line'", timeout: TimeSpan.FromSeconds(60));

        // The redirected console stream lags the pipe slightly; poll for the exit line.
        for (var i = 0; i < 50 && !ConsoleText(console).Contains("[exit 0]", StringComparison.Ordinal); i++)
        {
            await Task.Delay(100);
        }

        var text = ConsoleText(console);
        Assert.Contains("ready", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Write-Output 'visible-line'", text, StringComparison.Ordinal);
        Assert.Contains("visible-line", text, StringComparison.Ordinal);
        Assert.Contains("[exit 0]", text, StringComparison.Ordinal);
    }

    private static string ConsoleText(System.Text.StringBuilder console)
    {
        lock (console)
        {
            return console.ToString();
        }
    }

    private static string FindWrapperScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Helpers", "TerminalControl", "src", "Scripts", "terminal-tab.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("terminal-tab.ps1 not found above the test output folder.");
    }

    /// <summary>
    /// Stands in for wt.exe: "new-tab" starts the wrapper with pwsh directly; the window call is a no-op.
    /// </summary>
    private sealed class DirectTabLauncher : ITerminalProcessLauncher
    {
        private static readonly Regex WrapperArg = new("-File \"([^\"]+)\"", RegexOptions.Compiled);
        private readonly string _wrapperPath;
        private readonly List<Process> _processes;
        private readonly System.Text.StringBuilder? _console;

        public DirectTabLauncher(string wrapperPath, List<Process> processes, System.Text.StringBuilder? console = null)
        {
            _wrapperPath = wrapperPath;
            _processes = processes;
            _console = console;
        }

        public Process? Start(ProcessStartInfo startInfo)
        {
            var args = startInfo.Arguments;
            var pwshAt = args.IndexOf("pwsh ", StringComparison.Ordinal);
            if (pwshAt < 0)
            {
                return null;
            }

            var pwshArgs = WrapperArg.Replace(args[(pwshAt + 5)..], $"-File \"{_wrapperPath}\"", 1);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = pwshArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = _console is not null,
            });

            if (process is not null)
            {
                _processes.Add(process);

                if (_console is not null)
                {
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data is not null)
                        {
                            lock (_console)
                            {
                                _console.AppendLine(e.Data);
                            }
                        }
                    };
                    process.BeginOutputReadLine();
                }
            }

            return process;
        }
    }
}
