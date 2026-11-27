using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class InstancesViewModelTests
{
    private static InstanceRow Row(int pid) => new(pid, "VisualStudio", "app", "wip", @"C:\src\app\app.sln", DateTime.UtcNow);

    [Fact]
    public void TheListFollowsTheSource()
    {
        var source = new Subject<IReadOnlyList<InstanceRow>>();
        var vm = new InstancesViewModel(source, Scheduler.Immediate);

        source.OnNext(new[] { Row(1), Row(2) });

        Assert.Equal(2, vm.Rows.Count);
        Assert.False(vm.ShowEmpty);
    }

    [Fact]
    public void AnEmptyListShowsTheEmptyMessage()
    {
        var source = new Subject<IReadOnlyList<InstanceRow>>();
        var vm = new InstancesViewModel(source, Scheduler.Immediate);

        source.OnNext(Array.Empty<InstanceRow>());

        Assert.True(vm.ShowEmpty);
        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyMessage));
    }

    [Fact]
    public void AfterDisposeNoMoreChangesArrive()
    {
        var source = new Subject<IReadOnlyList<InstanceRow>>();
        var vm = new InstancesViewModel(source, Scheduler.Immediate);
        vm.Dispose();

        source.OnNext(new[] { Row(3) });

        Assert.Empty(vm.Rows);
    }
}

