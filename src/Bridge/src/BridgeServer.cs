using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Collections.Generic;



namespace AiDevHelpers.Bridge;

/// <summary>
/// Named-pipe server for one Visual Studio instance. Each request is one JSON line:
/// <c>{"action":"...","args":{...}}</c>. Each reply is one JSON line: <c>{"ok":true,"result":{...}}</c> or
/// <c>{"ok":false,"error":"..."}</c>. The pipe name embeds the process id, so several instances run side by side.
/// The listen loop survives faults: an unexpected error is logged and the listener is recreated after a backoff.
/// <see cref="IsRunning"/> lets a watchdog restart it if the loop has stopped. The <c>health</c> action is answered
/// here, not on the UI thread, so it responds even while Visual Studio is busy.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _pipeName;
    private readonly Func<string, JsonElement, Task<object>> _invoke;
    private readonly Func<string?> _solutionPath;
    private readonly IBridgeLogger _logger;
    private CancellationTokenSource _cts = new();
    private Task? _loop;
    private DateTime? _startedUtc;
    private DateTime? _lastRequestUtc;
    private long _requestsHandled;

    /// <summary>Creates a server for one instance's request pipe. <paramref name="invoke"/> runs each action; <paramref name="solutionPath"/> reports the open solution for health.</summary>
    public BridgeServer(
        string pipeName,
        Func<string, JsonElement, Task<object>> invoke,
        Func<string?> solutionPath,
        IBridgeLogger logger)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _solutionPath = solutionPath ?? throw new ArgumentNullException(nameof(solutionPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True while the listen loop is running (it is not faulted, cancelled, or never started).</summary>
    public bool IsRunning => _loop is { IsCompleted: false } && !_cts.IsCancellationRequested;

    /// <summary>Starts the listen loop on a background task. Does nothing when it is already running.</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _startedUtc = DateTime.UtcNow;
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    /// <summary>Cancels the listen loop. <see cref="Start"/> can run it again.</summary>
    public void Stop() => _cts.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream(_pipeName);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                backoff = TimeSpan.FromMilliseconds(250);
                await HandleAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = Min(TimeSpan.FromTicks(backoff.Ticks * 2), MaxBackoff);
            }
        }
    }

    /// <summary>
    /// The pipe's access list: the current user, LocalSystem and Administrators. An elevated Visual Studio creates the pipe with its
    /// elevated token, and the default list then refuses the same user's non-elevated MCP server (Administrators is deny-only in that
    /// token), so the user's own SID is granted explicitly. No one else is, so other local users still cannot connect.
    /// </summary>
    public static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var security = CreatePipeSecurity();
#if NETFRAMEWORK
        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
    }
    private async Task HandleAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        ct.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        var reply = await ReplyAsync(line).ConfigureAwait(false);
        await writer.WriteLineAsync(reply).ConfigureAwait(false);
    }

    private async Task<string> ReplyAsync(string? line)
    {
        Interlocked.Increment(ref _requestsHandled);
        _lastRequestUtc = DateTime.UtcNow;

        try
        {
            using var doc = JsonDocument.Parse(line ?? string.Empty);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString() ?? string.Empty;

            if (action == "health")
            {
                return JsonSerializer.Serialize(new { ok = true, pid = System.Diagnostics.Process.GetCurrentProcess().Id, result = Health() });
            }

            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var call = _invoke(action, args);
            var finished = await Task.WhenAny(call, Task.Delay(ActionLimit)).ConfigureAwait(false);
            if (finished != call)
            {
                // The UI thread did not answer in time, usually because a dialog is open. Tell subscribers, then reply instead of holding the caller.
                try
                {
                    ActionTimedOut?.Invoke(action);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex);
                }

                return JsonSerializer.Serialize(new { ok = false, pid = System.Diagnostics.Process.GetCurrentProcess().Id, error = "The IDE did not answer in time; a dialog may be open." });
            }

            var result = await call.ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, pid = System.Diagnostics.Process.GetCurrentProcess().Id, result });
        }
        catch (Exception ex)
        {
            // Every failure gets an error reply. A connection closed without one looks like an empty reply to the caller.
            return JsonSerializer.Serialize(new { ok = false, pid = System.Diagnostics.Process.GetCurrentProcess().Id, error = ex.Message });
        }
    }

    // Answered without touching Visual Studio, so it reports even when the UI thread is busy.
    /// <summary>True while the UI thread answers the watchdog's ping. Set by the host; defaults to true when no host sets it.</summary>
    public Func<bool>? UiResponsive { get; set; }

    /// <summary>How long one action may take on the IDE's UI thread before the caller gets an error. Default 60 seconds.</summary>
    public TimeSpan ActionLimit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Called with the action name when an action passes <see cref="ActionLimit"/>. The host uses it to report an open dialog.</summary>
    public Action<string>? ActionTimedOut { get; set; }

    private object Health() => new
    {
        listener_running = IsRunning,
        ui_responsive = UiResponsive?.Invoke() ?? true,
        started_utc = _startedUtc,
        last_request_utc = _lastRequestUtc,
        requests_handled = Interlocked.Read(ref _requestsHandled),
        solution_path = _solutionPath(),
        pipe_name = _pipeName,
    };

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    /// <summary>Cancels the listen loop and releases its cancellation source.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>Finds the titles of visible Win32 message dialogs (class #32770) owned by one process. Used to name a dialog that blocks the IDE.</summary>
public static class DialogWindows
{
    private const string DialogClass = "#32770";

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>Titles of the visible message dialogs that process <paramref name="processId"/> has open. Empty when there are none.</summary>
    public static IReadOnlyList<string> OpenTitles(int processId)
    {
        var titles = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(hwnd))
            {
                return true;
            }

            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != DialogClass)
            {
                return true;
            }

            var text = new StringBuilder(512);
            GetWindowText(hwnd, text, text.Capacity);
            titles.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }
}
