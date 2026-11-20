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

    private readonly DTE2 _dte;
    private readonly int _pid;
    private readonly Func<Task> _switchToMainThread;
    private BridgeServer? _server;
    private SolutionEvents? _solutionEvents;
    private System.Threading.Timer? _watchdog;
    private volatile string? _solutionPath;
    private bool _disposed;

    private BridgeHost(DTE2 dte, int pid, Func<Task> switchToMainThread)
    {
        _dte = dte;
        _pid = pid;
        _switchToMainThread = switchToMainThread;
    }

    /// <summary>Solution path currently open in this instance, or null.</summary>
    public string? SolutionPath => _solutionPath;

    /// <summary>Starts the bridge for this instance.</summary>
    /// <param name="dte">Visual Studio automation object.</param>
    /// <param name="pid">Process id of this Visual Studio instance.</param>
    /// <param name="switchToMainThread">Marshals a call onto the VS UI thread.</param>
    public static BridgeHost Start(DTE2 dte, int pid, Func<Task> switchToMainThread)
    {
        var host = new BridgeHost(dte, pid, switchToMainThread);
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
        InstanceRegistry.Remove(_pid);
        _server?.Dispose();
    }

    private void Initialize()
    {
        _solutionPath = NullIfEmpty(_dte.Solution?.FullName);

        var actions = new IdeActions(_dte);
        BridgeLog.Write("INFO", $"Visual Studio bridge package initialized for process {_pid}.");
        var pipeName = InstanceRegistry.PipeNameFor(_pid);
        _server = new BridgeServer(
            pipeName,
            async (action, args) =>
            {
                await _switchToMainThread();
                return actions.Invoke(action, args);
            },
            () => _solutionPath,
            new DebugBridgeLogger());
        BridgeControl.Attach(_server, _pid, pipeName, () => _solutionPath);
        BridgeControl.Start();

        _solutionEvents = _dte.Events.SolutionEvents;
#pragma warning disable VSTHRD010 // DTE solution events are raised on the UI thread.
        _solutionEvents.Opened += () => UpdateSolutionPath(_dte.Solution?.FullName);
        _solutionEvents.AfterClosing += () => UpdateSolutionPath(null);
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

    private void UpdateSolutionPath(string? solutionPath)
    {
        _solutionPath = NullIfEmpty(solutionPath);
        InstanceRegistry.Write(_pid, _solutionPath);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
