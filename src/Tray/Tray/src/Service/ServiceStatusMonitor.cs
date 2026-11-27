using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.ServiceProcess;

namespace AiDevHelpers.Tray.Service;

/// <summary>The state of the AiDevHelpers Windows service as the tray sees it.</summary>
public enum ServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Other,
}

/// <summary>Asks the service control manager about a service. A standard user may read the state; starting and stopping need administrator rights and go through the pinned scripts.</summary>
public interface IServiceQuery
{
    ServiceState Query(string serviceName);
}

/// <summary>The real query, through <see cref="ServiceController"/>.</summary>
public sealed class ServiceControllerQuery : IServiceQuery
{
    public ServiceState Query(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending => ServiceState.StartPending,
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.StopPending => ServiceState.StopPending,
                _ => ServiceState.Other,
            };
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        {
            // ERROR_SERVICE_DOES_NOT_EXIST.
            return ServiceState.NotInstalled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return ServiceState.Unknown;
        }
    }
}

/// <summary>Publishes the service state: the current one at once, then every change. Polls only while someone subscribes; a failing query is <see cref="ServiceState.Unknown"/>, never an error on the stream.</summary>
public sealed class ServiceStatusMonitor
{
    public ServiceStatusMonitor(IServiceQuery query, string serviceName, TimeSpan interval, IScheduler scheduler)
    {
        State = Observable.Timer(TimeSpan.Zero, interval, scheduler)
            .Select(_ => Safe(query, serviceName))
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();
    }

    public IObservable<ServiceState> State { get; }

    private static ServiceState Safe(IServiceQuery query, string serviceName)
    {
        try
        {
            return query.Query(serviceName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return ServiceState.Unknown;
        }
    }
}
