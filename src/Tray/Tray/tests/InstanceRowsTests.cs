using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class InstanceRowsTests
{
    private static VsInstance Vs(int pid, string? path = @"C:\src\app\app.sln", string ide = "VisualStudio") =>
        new(pid, $"AiDevHelpers.VS.{pid}", path, DateTime.UtcNow, ide, Array.Empty<string>());

    private sealed class Processes(params int[] gone) : IProcessInfo
    {
        public DateTime? StartTime(int pid) => gone.Contains(pid) ? null : new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public void AValidInstanceIsAccepted()
    {
        Assert.Null(InstanceValidator.Check(Vs(10)));
        Assert.Null(InstanceValidator.Check(new VsInstance(11, "AiDevHelpers.VSCode.11", "C:\\work\\repo", DateTime.UtcNow, "VsCode", Array.Empty<string>())));
    }

    [Theory]
    [InlineData(0)]
    public void APidOfZeroIsRejected(int pid) => Assert.NotNull(InstanceValidator.Check(Vs(pid)));

    [Fact]
    public void AnUnknownIdeIsRejected() => Assert.NotNull(InstanceValidator.Check(Vs(12, ide: "Other")));

    [Fact]
    public void APipeNameForAnotherPidIsRejected()
    {
        var wrong = new VsInstance(13, "AiDevHelpers.VS.99", @"C:\a.sln", DateTime.UtcNow, "VisualStudio", Array.Empty<string>());

        Assert.NotNull(InstanceValidator.Check(wrong));
    }

    [Fact]
    public void AControlCharacterInThePathIsRejected() =>
        Assert.NotNull(InstanceValidator.Check(Vs(14, path: "C:\\a\u0007.sln")));

    [Fact]
    public void AnOverlongPathIsRejected() =>
        Assert.NotNull(InstanceValidator.Check(Vs(15, path: "C:\\" + new string('a', 1100) + ".sln")));

    [Fact]
    public void ARowTakesRepoAndBranchFromGit()
    {
        var rows = InstanceRows.Build(new[] { Vs(20) }, new Processes(), _ => @"C:\src\app", _ => "feat/x");

        var row = Assert.Single(rows);
        Assert.Equal("app", row.Repo);
        Assert.Equal("feat/x", row.Branch);
    }

    [Fact]
    public void OutsideGitAndDetachedHeadAreNamed()
    {
        var outside = InstanceRows.Build(new[] { Vs(21) }, new Processes(), _ => null, _ => null).Single();
        var detached = InstanceRows.Build(new[] { Vs(22) }, new Processes(), _ => @"C:\src\app", _ => null).Single();

        Assert.Equal("not a repo", outside.Repo);
        Assert.Equal("detached", detached.Branch);
    }

    [Fact]
    public void AnInstanceThatHasGoneIsDropped()
    {
        var rows = InstanceRows.Build(new[] { Vs(30), Vs(31) }, new Processes(31), _ => null, _ => null);

        Assert.Equal(new[] { 30 }, rows.Select(r => r.Pid).ToArray());
    }
}

