using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using System.Reactive.Concurrency;
using AiDevHelpers.Bridge;
using AiDevHelpers.MCP.Core.Services;

namespace AiDevHelpers.Tray;

/// <summary>The tray icon, its menu, the Status window, toasts, and the approval pipe. Lives for the whole tray session.</summary>
public sealed class TrayApp : IDisposable
{
    public static readonly int Port = McpPortSettings.Read(McpPortSettings.DefaultPath);
    private const string ServiceName = "AiDevHelpers";

    private readonly TaskbarIcon _icon;
    private readonly StatusViewModel _status;
    private readonly CompositeDisposable _feeds = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<TrayIconState, System.Drawing.Icon> _icons;
    private readonly InstancesViewModel _instances;
    private readonly OpenFileWatcher _openFiles;
    private StatusWindow? _window;
    private TrayIconState? _previous;

    public TrayApp()
    {
        _icons = Enum.GetValues<TrayIconState>().ToDictionary(state => state, MakeIcon);

        var handler = new SocketsHttpHandler();
        var healthz = new Uri($"http://127.0.0.1:{Port}/healthz");
        var statusFile = Path.Combine(
            Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "AiDevHelpers", "mcp-http", "status.json");
        var health = HealthFeed.Watch(handler, healthz, () => File.Exists(statusFile) ? File.ReadAllText(statusFile) : null, TimeSpan.FromSeconds(5));
        var service = Observable.Interval(TimeSpan.FromSeconds(5))
            .StartWith(0L)
            .Select(_ => ServiceStatus.Read(ServiceName))
            .DistinctUntilChanged();

        // The UI thread's context exists here because the WPF Application was created first.
        var ui = new System.Reactive.Concurrency.SynchronizationContextScheduler(SynchronizationContext.Current!);
        _status = new StatusViewModel(health, service, Port, ui);

        // The IDE instances: read off the UI thread every 5 s, validated, turned into rows, then bound on the UI thread.
        var rowBuilder = new InstanceRowBuilder(InstanceProcessFacts.StartedAt, InstanceProcessFacts.OwnerOf, GitWorktree.RootFor, GitWorktree.BranchFor);
        _openFiles = new OpenFileWatcher((pipe, onEvent, ct) =>
            InstanceEventsClient.RunAsync(pipe, onEvent, TimeSpan.FromSeconds(45), _ => { }, ct));
        var instanceList = InstanceFeed.Watch(ValidInstances, TimeSpan.FromSeconds(5), TaskPoolScheduler.Default).Replay(1).RefCount();
        _feeds.Add(instanceList.Subscribe(list => _openFiles.Sync(list)));
        var rows = instanceList
            .Select(list => (IReadOnlyList<InstanceRow>)list.Select(rowBuilder.Build).OfType<InstanceRow>().ToList())
            .CombineLatest(_openFiles.Snapshots, (instanceRows, files) => InstanceRowFiles.WithFiles(instanceRows, files));
        _instances = new InstancesViewModel(rows, ui);
        _status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusViewModel.Icon))
            {
                OnIconChanged();
            }
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = "AiDevHelpers",
            ContextMenu = BuildMenu(),
            Icon = _icons[TrayIconState.Disconnected],
        };
        _icon.TrayLeftMouseUp += (_, _) => ShowStatus();
        _icon.ForceCreate();
    }

    /// <summary>Serves approval requests from the MCP server until the tray exits.</summary>
    public Task RunApprovalsAsync() =>
        ApprovalPipeHost.RunAsync(ApprovalWire.PipeNameForCurrentUser(), AskAsync, _stop.Token);

    public void Dispose()
    {
        _stop.Cancel();
        _feeds.Dispose();
        _status.Dispose();
        _instances.Dispose();
        _openFiles.Dispose();
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    /// <summary>The manifests the page may show: each one is checked, and a refused one is traced with its reason.</summary>
    private static IReadOnlyList<AiDevHelpers.Bridge.VsInstance> ValidInstances() =>
        AiDevHelpers.Bridge.VsInstanceRegistry.ListRunning()
            .Where(instance =>
            {
                var problem = InstanceValidator.Problem(instance);
                if (problem is not null)
                {
                    System.Diagnostics.Trace.TraceWarning($"IDE manifest for pid {instance.Pid} refused: {problem}");
                }

                return problem is null;
            })
            .ToList();

    private Task<bool> AskAsync(ApprovalRequest request) =>
        Application.Current.Dispatcher.InvokeAsync(() => ApprovalWindow.Ask(request)).Task.Unwrap();

    private void OnIconChanged()
    {
        var current = _status.Icon;
        _icon.Icon = _icons[current];
        var text = TrayNotifications.For(_previous, current);
        if (text is not null)
        {
            _icon.ShowNotification("AiDevHelpers", text);
        }

        _previous = current;
    }

    private void ShowStatus()
    {
        _window ??= new StatusWindow(_status, _instances);
        _window.Show();
        _window.Activate();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = Strings.Get("OpenStatus") };
        open.Click += (_, _) => ShowStatus();
        var exit = new MenuItem { Header = Strings.Get("ExitTray") };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(open);
        menu.Items.Add(exit);
        return menu;
    }

    private static System.Drawing.Icon MakeIcon(TrayIconState state)
    {
        var color = state switch
        {
            TrayIconState.Running => Color.SeaGreen,
            TrayIconState.Degraded => Color.Goldenrod,
            TrayIconState.Disconnected => Color.Gray,
            _ => Color.Firebrick,
        };
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(brush, 1, 1, 14, 14);
        }

        // Made once per state at startup (four icons), so the native icon handle is not released per change.
        return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(bitmap.GetHicon()).Clone();
    }
}

