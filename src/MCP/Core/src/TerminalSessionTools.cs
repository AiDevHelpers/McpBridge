using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using AiDevHelpers.Helpers.TerminalControl.Contracts;
using AiDevHelpers.Helpers.TerminalControl.Models;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
///     MCP surface for terminal sessions. A session's launchMode decides where its terminal runs: InteractiveUser
///     shows it on the logged-on user's desktop, ServiceAccount runs it headless under the host's own account.
/// </summary>
[McpServerToolType]
internal sealed class TerminalSessionTools
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ITerminalSessionManager _manager;
    private readonly Func<AiDevHelpers.Common.Terminals.TerminalScan> _scanTypes;
    private readonly AiDevHelpers.MCP.Core.Services.ScriptRunTracker? _tracker;
    private readonly AiDevHelpers.MCP.Core.Services.ScriptWatchOptions _watch;
    private readonly object _initGate = new();
    private Task? _initTask;

    public TerminalSessionTools(
        ITerminalSessionManager manager,
        Func<AiDevHelpers.Common.Terminals.TerminalScan>? scanTypes = null,
        AiDevHelpers.MCP.Core.Services.ScriptRunTracker? tracker = null,
        Microsoft.Extensions.Options.IOptions<AiDevHelpers.MCP.Core.Services.ScriptWatchOptions>? watch = null)
    {
        _manager = manager;
        _tracker = tracker;
        _watch = watch?.Value ?? new AiDevHelpers.MCP.Core.Services.ScriptWatchOptions();
        _scanTypes = scanTypes ?? (() => new AiDevHelpers.Common.Terminals.TerminalTypeScanner(
            new AiDevHelpers.Common.Terminals.SystemTerminalProbe(),
            AiDevHelpers.Common.Terminals.TerminalRegistry.ForCurrentUser()).Scan());
    }

    [McpServerTool]
    [Description(
        "Run commands in a terminal session. action: open (create a session with launchMode InteractiveUser " +
        "(visible on the user's desktop, default) or ServiceAccount (headless, under the service account) and a first tab; " +
        "elevated=true opens the session with UAC so its tabs run as administrator; tabName optional), " +
        "tab (new tab in sessionId; tabName optional), exec (run command in sessionId + tabId; " +
        "timeoutSeconds optional, default 60), list (open sessions), close (end sessionId), types (read only: the terminal types " +
        "installed on this machine, which the helper and Windows Terminal know, and install hints; a type with missing=true is installed but " +
        "not registered, so ask the user to run 'elev terminals setup').")]
    public async Task<string> TerminalSession(
        string action = "list",
        string? launchMode = null,
        bool elevated = false,
        string? sessionId = null,
        string? tabId = null,
        string? tabName = null,
        string? command = null,
        int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        switch (action.ToUpperInvariant())
        {
            case "OPEN":
                if (!TryParseMode(launchMode, out var mode))
                {
                    return Strings.Format("InvalidLaunchMode", launchMode);
                }

                return await RunAsync(async () =>
                {
                    await EnsureInitializedAsync(ct);
                    var session = await _manager.CreateSessionAsync(new TerminalSessionConfig(LaunchMode: mode, IsElevated: elevated), ct);
                    var tab = await session.CreateTabAsync(tabName ?? "main", null, ct);
                    return Strings.Format("SessionOpened", session.SessionId, mode, tab.TabId);
                });

            case "TAB":
                if (sessionId is null)
                {
                    return Strings.Get("TabNeedsSession");
                }

                return await RunAsync(async () =>
                {
                    var session = _manager.GetSession(sessionId)
                        ?? throw new InvalidOperationException(Strings.Format("NoSession", sessionId));
                    var tab = await session.CreateTabAsync(tabName ?? "main", null, ct);
                    return Strings.Format("TabCreated", tab.TabId, sessionId);
                });

            case "EXEC":
                if (sessionId is null || tabId is null || command is null)
                {
                    return Strings.Get("ExecNeedsArgs");
                }

                return await RunAsync(async () =>
                {
                    var session = _manager.GetSession(sessionId)
                        ?? throw new InvalidOperationException(Strings.Format("NoSession", sessionId));
                    var result = await session.ExecuteInTabAsync(
                        tabId, command, null, TimeSpan.FromSeconds(timeoutSeconds), ct);

                    var text = new StringBuilder();
                    text.Append(System.Globalization.CultureInfo.InvariantCulture, $"exit {result.ExitCode}").AppendLine();
                    text.Append(result.Stdout);
                    if (!string.IsNullOrEmpty(result.Stderr))
                    {
                        text.AppendLine().Append("stderr: ").Append(result.Stderr);
                    }

                    // A command run here feeds the same tracker as script_run, so one that keeps failing is noticed.
                    if (_tracker is not null)
                    {
                        var key = AiDevHelpers.MCP.Core.Services.ScriptRunTracker.Key(sessionId, command, []);
                        _tracker.Record(key, new AiDevHelpers.MCP.Core.Services.ScriptRunOutcome(result.ExitCode, TimedOut: false, Silent: false));
                        if (AiDevHelpers.MCP.Core.Services.ScriptRunService.AdviceFor(_tracker.Assess(key, _watch)) is { } advice)
                        {
                            text.AppendLine().Append(advice);
                        }
                    }

                    return text.ToString();
                });

            case "LIST":
                var sessions = _manager.GetActiveSessions();
                if (sessions.Count == 0)
                {
                    return Strings.Get("NoOpenSessions");
                }

                return string.Join("\n", sessions.Select(s =>
                    $"{s.SessionId}  state={s.State}  elevated={s.IsElevated}"));

            case "TYPES":
                // missing is the scanner's own property, which the record does not serialize, so it is added here for the caller.
                var scan = _scanTypes();
                return System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        scan.WindowsTerminalFound,
                        scan.Hints,
                        Types = scan.Types.Select(t => new { t.Id, t.Name, t.Path, t.Version, t.Installed, t.InHelper, t.InWindowsTerminal, t.Declined, t.Missing }),
                    },
                    Json);

            case "CLOSE":
                if (sessionId is null)
                {
                    return Strings.Get("CloseNeedsSession");
                }

                return await RunAsync(async () =>
                {
                    await _manager.CloseSessionAsync(sessionId, graceful: true, ct);
                    return $"Closed {sessionId}.";
                });

            default:
                return Strings.Get("InvalidTerminalAction");
        }
    }

    /// <summary>Accepts InteractiveUser or ServiceAccount (case-insensitive); empty means InteractiveUser.</summary>
    internal static bool TryParseMode(string? value, out TerminalLaunchMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = TerminalLaunchMode.InteractiveUser;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    /// <summary>
    /// Initializes the manager once. The init runs without the caller's token so a cancelled call cannot poison it;
    /// a failed init is cleared so the next call retries.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        Task init;
        lock (_initGate)
        {
            _initTask ??= _manager.InitializeAsync(CancellationToken.None);
            init = _initTask;
        }

        try
        {
            await init.WaitAsync(ct);
        }
        catch (Exception) when (init.IsFaulted)
        {
            lock (_initGate)
            {
                if (ReferenceEquals(_initTask, init))
                {
                    _initTask = null;
                }
            }

            throw;
        }
    }

    private static async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }
}
