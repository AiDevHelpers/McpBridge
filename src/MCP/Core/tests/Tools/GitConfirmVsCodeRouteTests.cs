using AiDevHelpers.Helpers.SourceControlHelper;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

/// <summary>git_confirm routes approved writes to an open VS Code window, and keeps the rest on git.exe.</summary>
public sealed class GitConfirmVsCodeRouteTests
{
    private const string Repo = "F:/work/app";

    [Fact]
    public async Task PlannedStage_RoutesToVsCodeGitStage_AndSkipsGit()
    {
        var git = new FakeGit();
        var router = new FakeRouter(new GitResult(true, 0, "{\"staged\":true}"));
        var service = new SourceControlService(git, null, router);

        var plan = await service.PlanAsync(Repo, "stage", paths: ["a.txt", "b.txt"]);
        var result = await service.ConfirmAsync(plan.Token);

        Assert.True(result.Ok);
        Assert.Single(router.Calls);
        Assert.Equal("gitStage", router.Calls[0].Action);
        Assert.Equal(Repo, router.Calls[0].RepoPath);
        Assert.Equal(new[] { "a.txt", "b.txt" }, (string[])router.Calls[0].Arguments["paths"]!);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task PlannedPush_StaysLocal_AndNeverReachesVsCode()
    {
        var git = new FakeGit();
        var router = new FakeRouter(new GitResult(true, 0, "{}"));
        var service = new SourceControlService(git, null, router);

        var plan = await service.PlanAsync(Repo, "push");
        var result = await service.ConfirmAsync(plan.Token);

        Assert.True(result.Ok);
        Assert.Empty(router.Calls);
        Assert.Single(git.Calls);
        Assert.Equal(new[] { "push" }, git.Calls[0].Arguments);
    }

    [Fact]
    public async Task VsCodeFailure_ConsumesToken_DoesNotFallBackToGit_AndSecondConfirmIsUnknown()
    {
        var git = new FakeGit();
        var router = new FakeRouter(new GitResult(false, -1, "VS Code refused"));
        var service = new SourceControlService(git, null, router);

        var plan = await service.PlanAsync(Repo, "stage", paths: ["a.txt"]);
        var first = await service.ConfirmAsync(plan.Token);
        var second = await service.ConfirmAsync(plan.Token);

        Assert.False(first.Ok);
        Assert.Contains("VS Code refused", first.Output);
        Assert.Empty(git.Calls);
        Assert.Single(router.Calls);
        Assert.False(second.Ok);
        Assert.Equal(-1, second.ExitCode);
        Assert.Single(router.Calls);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task NoVsCodeWindowOpen_RunsGitExactlyAsBefore()
    {
        var git = new FakeGit();
        var router = new FakeRouter(null);
        var service = new SourceControlService(git, null, router);

        var plan = await service.PlanAsync(Repo, "stage", paths: ["a.txt"]);
        var result = await service.ConfirmAsync(plan.Token);

        Assert.True(result.Ok);
        Assert.Single(router.Calls);
        Assert.Single(git.Calls);
        Assert.Equal(new[] { "add", "--", "a.txt" }, git.Calls[0].Arguments);
    }

    [Fact]
    public async Task Sequence_RoutesEachStep_AndStopsAtFirstVsCodeFailure()
    {
        var git = new FakeGit();
        var router = new FakeRouter(new GitResult(false, -1, "stage failed"));
        var service = new SourceControlService(git, null, router);

        var plan = await service.PlanSequenceAsync(Repo, ["stage", "commit", "push"], paths: ["a.txt"], message: "m");
        var result = await service.ConfirmAsync(plan.Token);

        Assert.False(result.Ok);
        Assert.Single(router.Calls);
        Assert.Equal("gitStage", router.Calls[0].Action);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task Sequence_StageAndCommitRouted_PushStaysLocal()
    {
        var git = new FakeGit();
        var router = new FakeRouter(new GitResult(true, 0, "{}"));
        var service = new SourceControlService(git, null, router);

        var plan = await service.PlanSequenceAsync(Repo, ["stage", "commit", "push"], paths: ["a.txt"], message: "fix");
        var result = await service.ConfirmAsync(plan.Token);

        Assert.True(result.Ok);
        Assert.Equal(new[] { "gitStage", "gitCommit" }, router.Calls.Select(c => c.Action));
        Assert.Equal("fix", router.Calls[1].Arguments["message"]);
        Assert.Single(git.Calls);
        Assert.Equal(new[] { "push" }, git.Calls[0].Arguments);
    }

    private sealed class FakeGit : IGitRunner
    {
        public List<(string Dir, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add((workingDirectory, arguments));
            return Task.FromResult(new GitResult(true, 0, string.Empty));
        }
    }

    private sealed class FakeRouter(GitResult? reply) : IVsCodeWriteRouter
    {
        public List<(string RepoPath, string Action, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];

        public Task<GitResult?> TryRunAsync(string repoPath, string vsCodeAction, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            Calls.Add((repoPath, vsCodeAction, arguments));
            return Task.FromResult(reply);
        }
    }
}
