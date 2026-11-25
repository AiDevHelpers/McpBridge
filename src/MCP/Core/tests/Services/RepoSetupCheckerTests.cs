using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class RepoSetupCheckerTests
{
    private const string Pending = """{"pendingProjects":["src/Drv/Drv.csproj"],"findings":[],"elevation":{}}""";
    private const string NonePending = """{"pendingProjects":[],"findings":[],"elevation":{}}""";

    private sealed class FakeScanner(Func<string, Task<ElevRunResult>> scan) : IElevScanner
    {
        public List<string> Roots { get; } = [];

        public Task<ElevRunResult> ApplyAsync(string repoRoot, string decisionsJson, CancellationToken ct) => Task.FromResult(new ElevRunResult(0, "applied"));

        public Task<ElevRunResult> ScanAsync(string repoRoot, CancellationToken ct)
        {
            lock (Roots)
            {
                Roots.Add(repoRoot);
            }

            return scan(repoRoot);
        }
    }

    private static FakeScanner Returns(string json, int exit = 0) => new(_ => Task.FromResult(new ElevRunResult(exit, json)));

    private static RepoSetupChecker Checker(FakeScanner scanner, Func<IReadOnlyList<string>> solutions, List<RepoElevationNotice> notices, bool hasFolder = false) =>
        new(solutions, scanner, s => s.Contains("norepo") ? null : Path.GetDirectoryName(s), _ => hasFolder, notices.Add);

    [Fact]
    public async Task ARepoWithNoSetupFolder_IsScannedOnce_AndTheClientIsToldAboutPendingProjects()
    {
        var scanner = Returns(Pending);
        var notices = new List<RepoElevationNotice>();
        var checker = Checker(scanner, () => [@"C:\r1\a.sln"], notices);

        checker.CheckOnce(CancellationToken.None);
        checker.CheckOnce(CancellationToken.None);
        await checker.WhenIdleAsync();

        Assert.Equal([@"C:\r1"], scanner.Roots);
        var notice = Assert.Single(notices);
        Assert.Equal(["src/Drv/Drv.csproj"], notice.Projects);
    }

    [Fact]
    public async Task AReopenedRepo_IsNotScannedAgainInTheSameSession()
    {
        var scanner = Returns(NonePending);
        var open = new List<string> { @"C:\r1\a.sln" };
        var checker = Checker(scanner, () => open.ToList(), []);

        checker.CheckOnce(CancellationToken.None);
        open.Clear();
        checker.CheckOnce(CancellationToken.None);
        open.Add(@"C:\r1\a.sln");
        checker.CheckOnce(CancellationToken.None);
        await checker.WhenIdleAsync();

        Assert.Single(scanner.Roots);
    }

    [Fact]
    public async Task ARepoThatHasASetupFolder_OrIsNotInGit_IsSkipped()
    {
        var scanner = Returns(Pending);
        var notices = new List<RepoElevationNotice>();

        Checker(scanner, () => [@"C:\r1\a.sln"], notices, hasFolder: true).CheckOnce(CancellationToken.None);
        var other = Checker(scanner, () => [@"C:\norepo\a.sln"], notices);
        other.CheckOnce(CancellationToken.None);
        await other.WhenIdleAsync();

        Assert.Empty(scanner.Roots);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task NothingPending_RaisesNoNotice()
    {
        var notices = new List<RepoElevationNotice>();
        var checker = Checker(Returns(NonePending), () => [@"C:\r1\a.sln"], notices);

        checker.CheckOnce(CancellationToken.None);
        await checker.WhenIdleAsync();

        Assert.Empty(notices);
    }

    [Theory]
    [InlineData("not json", 0)]
    [InlineData("{}", 0)]
    [InlineData(Pending, 2)]
    public async Task AFailedOrUnreadableScan_RaisesNoNoticeAndDoesNotThrow(string output, int exit)
    {
        var notices = new List<RepoElevationNotice>();
        var checker = Checker(Returns(output, exit), () => [@"C:\r1\a.sln"], notices);

        checker.CheckOnce(CancellationToken.None);
        await checker.WhenIdleAsync();

        Assert.Empty(notices);
    }

    [Fact]
    public async Task AScannerThatThrows_IsContained()
    {
        var scanner = new FakeScanner(_ => throw new InvalidOperationException("elev missing"));
        var checker = Checker(scanner, () => [@"C:\r1\a.sln"], []);

        checker.CheckOnce(CancellationToken.None);

        await checker.WhenIdleAsync();
    }

    [Fact]
    public async Task ASlowScan_DoesNotDelayTheNextLook_AtAnotherRepo()
    {
        var gate = new TaskCompletionSource<ElevRunResult>();
        var scanner = new FakeScanner(root => root == @"C:\r1" ? gate.Task : Task.FromResult(new ElevRunResult(0, NonePending)));
        var open = new List<string> { @"C:\r1\a.sln" };
        var notices = new List<RepoElevationNotice>();
        var checker = Checker(scanner, () => open.ToList(), notices);

        checker.CheckOnce(CancellationToken.None);
        open.Add(@"C:\r2\b.sln");
        checker.CheckOnce(CancellationToken.None);

        // The first scan is still waiting, yet the second repo's scan was already started by the next look.
        SpinWait.SpinUntil(() => scanner.Roots.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, scanner.Roots.Count);
        Assert.Empty(notices);

        gate.SetResult(new ElevRunResult(0, Pending));
        await checker.WhenIdleAsync();
        Assert.Single(notices);
    }
}
