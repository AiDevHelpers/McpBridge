using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiDevHelpers.Tray;

/// <summary>
/// The IDE Instances page's state: the rows, fed from the instance stream on the given scheduler. Disposing it stops the updates.
/// </summary>
public sealed class InstancesViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _subscription;

    public InstancesViewModel(IObservable<IReadOnlyList<InstanceRow>> rows, IScheduler scheduler)
    {
        _subscription = rows.ObserveOn(scheduler).Subscribe(list =>
        {
            Rows.Clear();
            foreach (var row in list)
            {
                Rows.Add(row);
            }

            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    public ObservableCollection<InstanceRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public void Dispose() => _subscription.Dispose();
}
