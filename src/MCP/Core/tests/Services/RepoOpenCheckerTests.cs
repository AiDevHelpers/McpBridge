using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class RepoOpenCheckerTests
{
    private sealed class FakeVerifier(int exitCode, string output = "{}") : IElevVerifier
    {
        public List<string> Roots { get; } = [];

        public Task<ElevRunResult> VerifyAsync(string projectRoot, CancellationToken ct)
        {
            Roots.Add(projectRoot);
            return Task.FromResult(new ElevRunResult(exitCode, output));
        }
    }

    private static RepoOpenChecker Checker(
        FakeVerifier verifier,
        Func<IReadOnlyList<string>> solutions,
        bool hasAllowlist = true,
        List<RepoVerifyNotice>? notices = null) =>
        new(solutions, verifier, s => s.Contains("norepo") ? null : Path.GetDirectoryName(s), _ => hasAllowlist, n => notices?.Add(n));

    private const string Unsigned = """{"allowAllActive":false,"fullyApproved":false,"scripts":[{"path":"scripts/a.ps1","status":"Unsigned","reason":"x"},{"path":"scripts/b.ps1","status":"Approved","reason":""}]}""";

    [Fact]
    public async Task ANewlyOpenedRepo_IsVerifiedOnce()
    {
        var verifier = new FakeVerifier(0);
        var checker = Checker(verifier, () => [@"C:\r1\a.sln"]);

        await checker.CheckOnceAsync(CancellationToken.None);
        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal([@"C:\r1"], verifier.Roots);
    }

    [Fact]
    public async Task AReopenedRepo_IsVerifiedAgain()
    {
        var verifier = new FakeVerifier(0);
        var open = new List<string> { @"C:\r1\a.sln" };
        var checker = Checker(verifier, () => open.ToList());

        await checker.CheckOnceAsync(CancellationToken.None);
        open.Clear();
        await checker.CheckOnceAsync(CancellationToken.None);
        open.Add(@"C:\r1\a.sln");
        await checker.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(2, verifier.Roots.Count);
    }

    [Fact]
    public async Task ASolutionOutsideAGitRepo_IsSkipped()
    {
        var verifier = new FakeVerifier(0);

        await Checker(verifier, () => [@"C:\norepo\a.sln"]).CheckOnceAsync(CancellationToken.None);

        Assert.Empty(verifier.Roots);
    }

    [Fact]
    public async Task ARepoWithoutAnAllowlist_IsSkipped()
    {
        var verifier = new FakeVerifier(0);

        await Checker(verifier, () => [@"C:\r1\a.sln"], hasAllowlist: false).CheckOnceAsync(CancellationToken.None);

        Assert.Empty(verifier.Roots);
    }

    [Fact]
    public async Task UnapprovedFiles_RaiseANoticeNamingThem()
    {
        var notices = new List<RepoVerifyNotice>();
        var checker = Checker(new FakeVerifier(1, Unsigned), () => [@"C:\r1\a.sln"], notices: notices);

        await checker.CheckOnceAsync(CancellationToken.None);

        var notice = Assert.Single(notices);
        Assert.Equal(@"C:\r1", notice.RepoRoot);
        Assert.Equal(["scripts/a.ps1"], notice.Unapproved);
    }

    [Fact]
    public async Task AllApproved_RaisesNoNotice()
    {
        var notices = new List<RepoVerifyNotice>();

        await Checker(new FakeVerifier(0), () => [@"C:\r1\a.sln"], notices: notices).CheckOnceAsync(CancellationToken.None);

        Assert.Empty(notices);
    }

    [Fact]
    public async Task ABrokerThatDidNotAnswer_RaisesNoNotice()
    {
        var notices = new List<RepoVerifyNotice>();

        await Checker(new FakeVerifier(2), () => [@"C:\r1\a.sln"], notices: notices).CheckOnceAsync(CancellationToken.None);

        Assert.Empty(notices);
    }

    [Fact]
    public async Task UnreadableOutput_RaisesNoNoticeAndDoesNotThrow()
    {
        var notices = new List<RepoVerifyNotice>();

        await Checker(new FakeVerifier(1, "not json"), () => [@"C:\r1\a.sln"], notices: notices).CheckOnceAsync(CancellationToken.None);

        Assert.Empty(notices);
    }

    [Fact]
    public async Task TwoSolutionsInOneRepo_VerifyTheRepoOnce()
    {
        var verifier = new FakeVerifier(0);

        await Checker(verifier, () => [@"C:\r1\a.sln", @"C:\r1\b.sln"]).CheckOnceAsync(CancellationToken.None);

        Assert.Single(verifier.Roots);
    }
}
