using System.Text;
using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Models;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>What to run in a visible terminal tab: the interpreter, its arguments (script path first), and where.</summary>
public sealed record InteractiveRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string TabName, TimeSpan Timeout);

/// <summary>How a run in a visible tab ended. SessionId and TabId let the caller read the tab or close it.</summary>
public sealed record InteractiveResult(bool Success, int ExitCode, string? Stdout, string? Stderr, string SessionId, string TabId, IReadOnlyList<string>? Errors);

/// <summary>
/// Runs a script in a visible terminal tab on the user's desktop, so they see its prompts and any errors it does not report, and
/// returns what the tab printed. The tab is left open for the person to read. This is not elevated: an elevated script keeps going
/// through the broker.
/// </summary>
public sealed class InteractiveScriptRunner(ITerminalSessionManager manager)
{
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _initialized;

    public async Task<InteractiveResult> RunAsync(InteractiveRequest request, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var session = await manager.CreateSessionAsync(new TerminalSessionConfig(LaunchMode: TerminalLaunchMode.InteractiveUser, IsElevated: false), ct);
        var tab = await session.CreateTabAsync(request.TabName, null, ct);
        var result = await session.ExecuteInTabAsync(tab.TabId, Command(request), null, request.Timeout, ct);
        return new InteractiveResult(result.Success, result.ExitCode, result.Stdout, result.Stderr, session.SessionId, tab.TabId, result.Errors);
    }

    /// <summary>
    /// The PowerShell line a tab runs: change folder, then call the interpreter with every argument as a quoted literal. Nothing the
    /// caller supplied is parsed as PowerShell, including the smart quotes PowerShell treats like a single quote.
    /// </summary>
    public static string Command(InteractiveRequest request)
    {
        var line = new StringBuilder("Set-Location -LiteralPath ").Append(Quote(request.WorkingDirectory)).Append("; & ").Append(Quote(request.Executable));
        foreach (var argument in request.Arguments)
            line.Append(' ').Append(Quote(argument));

        return line.ToString();
    }

    private static string Quote(string text)
    {
        var quoted = new StringBuilder("'");
        foreach (var c in text)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                quoted.Append(c);
        }

        return quoted.Append('\'').ToString();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        await _init.WaitAsync(ct);
        try
        {
            if (!_initialized)
            {
                await manager.InitializeAsync(CancellationToken.None);
                _initialized = true;
            }
        }
        finally
        {
            _init.Release();
        }
    }
}
