using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiDevHelpers.Tray;

/// <summary>The Instances page: the live list of running IDE instances. Subscriptions end on Dispose.</summary>
public sealed class InstancesViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _subscription;
    private bool _showEmpty = true;

    public InstancesViewModel(IObservable<IReadOnlyList<InstanceRow>> rows, IScheduler scheduler)
    {
        _subscription = rows.ObserveOn(scheduler).Subscribe(list =>
        {
            Rows.Clear();
            foreach (var row in list)
            {
                Rows.Add(row);
            }

            ShowEmpty = list.Count == 0;
        });
    }

    public ObservableCollection<InstanceRow> Rows { get; } = new();

    public bool ShowEmpty
    {
        get => _showEmpty;
        private set => SetProperty(ref _showEmpty, value);
    }

    public string EmptyMessage => Strings.Get("NoInstances");

    public void Dispose() => _subscription.Dispose();
}

