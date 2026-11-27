using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using AiDevHelpers.Bridge;
using Microsoft.Reactive.Testing;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class InstancesTests
{
    private static VsInstance Vs(int pid, string? solution = @"C:\src\a\a.sln", string ide = "VisualStudio") =>
        new(pid, $"AiDevHelpers.VS.{pid}", solution, DateTime.UtcNow, ide, []);

    // 6.1: the feed reads on a schedule and emits only when the list changes.
    [Fact]
    public void Feed_EmitsOnlyWhenTheListChanges()
    {
        var scheduler = new TestScheduler();
        var current = new List<VsInstance>();
        var counts = new List<int>();
        using var sub = InstanceFeed.Watch(() => current, TimeSpan.FromSeconds(5), scheduler).Subscribe(list => counts.Add(list.Count));

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        current = [Vs(1)];
        scheduler.AdvanceBy(TimeSpan.FromSeconds(5).Ticks);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(10).Ticks);

        Assert.Equal(new[] { 0, 1 }, counts);
    }

    // 6.2: untrusted manifests are refused with a reason.
    [Fact]
    public void Validator_AcceptsAWellFormedManifest()
    {
        Assert.Null(InstanceValidator.Problem(Vs(42)));
        Assert.Null(InstanceValidator.Problem(Vs(43, solution: null, ide: "VsCode") with { PipeName = "AiDevHelpers.VsCode.43" }));
    }

    [Fact]
    public void Validator_RefusesAPidOfZero()
    {
        Assert.NotNull(InstanceValidator.Problem(Vs(0)));
    }

    [Fact]
    public void Validator_RefusesAnUnknownIde()
    {
        Assert.NotNull(InstanceValidator.Problem(Vs(7, ide: "Other")));
    }

    [Fact]
    public void Validator_RefusesAPipeNameForAnotherPid()
    {
        Assert.NotNull(InstanceValidator.Problem(Vs(8) with { PipeName = "AiDevHelpers.VS.9" }));
    }

    [Fact]
    public void Validator_RefusesAPathWithAControlCharacter()
    {
        Assert.NotNull(InstanceValidator.Problem(Vs(10, solution: "C:\\src\\a\u0007.sln")));
    }

    [Fact]
    public void Validator_RefusesARelativeOrOverlongPath()
    {
        Assert.NotNull(InstanceValidator.Problem(Vs(11, solution: "src\\a.sln")));
        Assert.NotNull(InstanceValidator.Problem(Vs(12, solution: "C:\\" + new string('a', 1100) + ".sln")));
    }

    // 6.3 and 6.4: repo, branch and start time, with the file and git reads injected.
    private static InstanceRowBuilder Builder(Func<int, DateTime?>? started = null) => new(
        startedAt: started ?? (_ => new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc)),
        ownerOf: _ => @"DOMAIN\richard",
        rootFor: path => path.StartsWith(@"C:\src\a", StringComparison.Ordinal) ? @"C:\src\a" : null,
        branchFor: root => root == @"C:\src\a" ? "feat/x" : null);

    [Fact]
    public void Row_ShowsRepoAndBranchForASolutionInGit()
    {
        var row = Builder().Build(Vs(20));

        Assert.NotNull(row);
        Assert.Equal("a", row!.Repo);
        Assert.Equal("feat/x", row.Branch);
        Assert.Equal(@"DOMAIN\richard", row.User);
    }

    [Fact]
    public void Row_SaysNotARepoOutsideGit()
    {
        var row = Builder().Build(Vs(21, solution: @"D:\other\b.sln"));

        Assert.NotNull(row);
        Assert.Null(row!.Repo);
        Assert.Null(row.Branch);
    }

    [Fact]
    public void Row_ShowsDetachedWhenGitHasNoBranch()
    {
        var builder = new InstanceRowBuilder(
            _ => new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc),
            _ => null,
            _ => @"C:\src\a",
            _ => null);

        var row = builder.Build(Vs(22));

        Assert.Equal("detached", row!.Branch);
    }

    [Fact]
    public void Row_IsDroppedWhenTheProcessHasGone()
    {
        Assert.Null(Builder(_ => null).Build(Vs(23)));
    }

    // 6.5: the view model follows the rows and stops when disposed.
    [Fact]
    public void ViewModel_FollowsTheRowsAndStopsWhenDisposed()
    {
        var scheduler = new TestScheduler();
        var source = new BehaviorSubject<IReadOnlyList<InstanceRow>>([]);
        var vm = new InstancesViewModel(source, scheduler);
        scheduler.AdvanceBy(1);
        Assert.True(vm.IsEmpty);

        source.OnNext([new InstanceRow(30, "VisualStudio", "a", "main", @"C:\a.sln", DateTime.UtcNow, "u")]);
        scheduler.AdvanceBy(1);
        Assert.Single(vm.Rows);

        vm.Dispose();
        source.OnNext([]);
        scheduler.AdvanceBy(1);
        Assert.Single(vm.Rows);
    }
}
