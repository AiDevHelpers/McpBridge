using System;
using System.Threading.Tasks;
using AiDevHelpers.Bridge;
using EnvDTE;
using EnvDTE80;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Runs this Visual Studio instance's bridge: the named-pipe server, the instance registry entry,
/// solution-path tracking and the watchdog that restarts the server. Must be started on the UI thread.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UiPingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UiHungAfter = TimeSpan.FromSeconds(30);

    private volatile bool _uiResponsive = true;
    private DateTime _lastUiAnswerUtc = DateTime.UtcNow;
    private int _uiPingInFlight;

    private readonly DTE2 _dte;
    private readonly int _pid;
    private readonly Func<Func<object>, Task<object>> _runOnMainThread;
    private BridgeServer? _server;
    private SolutionEvents? _solutionEvents;
    private System.Threading.Timer? _watchdog;
    private volatile string? _solutionPath;
    private bool _disposed;

    private BridgeHost(DTE2 dte, int pid, Func<Func<object>, Task<object>> runOnMainThread)
    {
        _dte = dte;
        _pid = pid;
        _runOnMainThread = runOnMainThread;
    }

    /// <summary>Solution path currently open in this instance, or null.</summary>
    public string? SolutionPath => _solutionPath;

    /// <summary>Starts the bridge for this instance.</summary>
    /// <param name="dte">Visual Studio automation object.</param>
    /// <param name="pid">Process id of this Visual Studio instance.</param>
    /// <param name="switchToMainThread">Marshals a call onto the VS UI thread.</param>
    public static BridgeHost Start(DTE2 dte, int pid, Func<Func<object>, Task<object>> runOnMainThread)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        var host = new BridgeHost(dte, pid, runOnMainThread);
        host.Initialize();
        return host;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog?.Dispose();
        if (_solutionEvents is not null)
        {
            _solutionEvents.Opened -= OnSolutionOpened;
            _solutionEvents.AfterClosing -= OnSolutionAfterClosing;
            _solutionEvents = null;
        }

        InstanceRegistry.Remove(_pid);
        _server?.Dispose();
    }

    // Named so Dispose can unhook them. Lambdas could not be removed.
    private void OnSolutionOpened() => UpdateSolutionPath(_dte.Solution?.FullName);

    private void OnSolutionAfterClosing() => UpdateSolutionPath(null);

    private void Initialize()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        _solutionPath = NullIfEmpty(_dte.Solution?.FullName);

        var actions = new IdeActions(_dte);
        BridgeLog.Write("INFO", $"Visual Studio bridge package initialized for process {_pid}.");
        var pipeName = InstanceRegistry.PipeNameFor(_pid);
        _server = new BridgeServer(
            pipeName,
#pragma warning disable VSTHRD010 // _runOnMainThread marshals the call onto the UI thread before Invoke runs.
            (action, args) => _runOnMainThread(() => actions.Invoke(action, args)),
#pragma warning restore VSTHRD010
            () => _solutionPath,
            new DebugBridgeLogger());
        _server.UiResponsive = () => _uiResponsive;
        BridgeControl.Attach(_server, _pid, pipeName, () => _solutionPath);
        BridgeControl.Start();

        _solutionEvents = _dte.Events.SolutionEvents;
#pragma warning disable VSTHRD010 // DTE solution events are raised on the UI thread.
        _solutionEvents.Opened += OnSolutionOpened;
        _solutionEvents.AfterClosing += OnSolutionAfterClosing;
#pragma warning restore VSTHRD010

        var pid = _pid;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => InstanceRegistry.Remove(pid);
        _watchdog = new System.Threading.Timer(_ => Watchdog(), null, WatchdogInterval, WatchdogInterval);
    }

    private void Watchdog()
    {
        try
        {
            if (BridgeControl.UserStopped)
            {
                return;
            }

            _ = PingUiAsync();

            if (_server is { IsRunning: false })
            {
                _server.Start();
            }

            InstanceRegistry.Write(_pid, _solutionPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IdeBridge] Watchdog error: {ex}");
        }
    }

    // Posts a no-op to the UI thread. An answer in time keeps the UI marked responsive; no answer for UiHungAfter marks it hung.
    // The ping stays in flight until the UI thread answers, so a hung UI does not pile up queued pings.
    private Task PingUiAsync()
    {
        if (Interlocked.CompareExchange(ref _uiPingInFlight, 1, 0) != 0)
        {
            MarkHungIfOverdue();
            return Task.CompletedTask;
        }

        var answer = _runOnMainThread(() => (object)DateTime.UtcNow);
        answer.ContinueWith(_ => Interlocked.Exchange(ref _uiPingInFlight, 0), TaskScheduler.Default);
        return Task.WhenAny(answer, Task.Delay(UiPingTimeout)).ContinueWith(done =>
        {
            if (ReferenceEquals(done.Result, answer))
            {
                if (!_uiResponsive)
                {
                    BridgeLog.Write("INFO", $"Visual Studio UI thread answers again (pid {_pid}).");
                }

                _lastUiAnswerUtc = DateTime.UtcNow;
                _uiResponsive = true;
            }
            else
            {
                MarkHungIfOverdue();
            }
        }, TaskScheduler.Default);
    }

    private void MarkHungIfOverdue()
    {
        if (_uiResponsive && DateTime.UtcNow - _lastUiAnswerUtc > UiHungAfter)
        {
            _uiResponsive = false;
            BridgeLog.Write("WARN", $"Visual Studio UI thread has not answered for {UiHungAfter.TotalSeconds} s (pid {_pid}).");
        }
    }

    private void UpdateSolutionPath(string? solutionPath)
    {
        _solutionPath = NullIfEmpty(solutionPath);
        InstanceRegistry.Write(_pid, _solutionPath);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
