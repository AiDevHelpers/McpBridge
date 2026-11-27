using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiDevHelpers.Tray;

/// <summary>
/// The Status page's state, fed by the MCP server's reading and the service state. Every property is set from those streams, so the
/// page only binds to these properties.
/// </summary>
public sealed class StatusViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _subscription;
    private HealthSnapshot? _health;
    private ServiceState? _service;
    private TrayIconState _icon = TrayIconState.Disconnected;
    private string _headline = Strings.Get("CheckingServer");
    private int? _sessions;

    public StatusViewModel(IObservable<HealthSnapshot> health, IObservable<ServiceState> service, int port, IScheduler scheduler)
    {
        Endpoint = $"http://127.0.0.1:{port}/mcp";
        Port = port;
        _subscription = new CompositeDisposable(
            health.ObserveOn(scheduler).Subscribe(h =>
            {
                _health = h;
                Apply();
            }),
            service.ObserveOn(scheduler).Subscribe(s =>
            {
                _service = s;
                Apply();
            }));
    }

    public int Port { get; }

    public string Endpoint { get; }

    public TrayIconState Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    public string Headline
    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    public int? Sessions
    {
        get => _sessions;
        private set => SetProperty(ref _sessions, value);
    }

    public void Dispose() => _subscription.Dispose();

    private void Apply()
    {
        if (_service is null && _health is null)
        {
            return;
        }

        var health = _health ?? new HealthSnapshot(TrayConnection.Disconnected, "no reading yet");
        var service = _service ?? ServiceState.NotInstalled;
        Icon = TrayStatus.IconFor(health, service);
        Sessions = health.State == TrayConnection.Connected ? health.Sessions : null;
        Headline = HeadlineFor(health, service);
    }

    private string HeadlineFor(HealthSnapshot health, ServiceState service) =>
        health.State switch
        {
            TrayConnection.Connected when service == ServiceState.Running => Strings.Get("Running"),
            TrayConnection.Connected => Strings.Get("RunningServiceNot"),
            TrayConnection.PortConflict => Strings.Format("PortInUse", Port),
            _ when service is ServiceState.Stopped or ServiceState.NotInstalled => Strings.Get("ServiceStopped"),
            _ => Strings.Get("ServerNotReachable"),
        };
}

