using System.Text.Json.Nodes;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class RepoRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repo-rules-" + Guid.NewGuid().ToString("N"));

    public RepoRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoFile_MeansNoRules()
    {
        Assert.True(RepoRulesFile.Read(_root).IsEmpty);
        Assert.True(RepoRulesFile.Effective(_root).IsEmpty);
    }

    [Fact]
    public void RulesAreWritten_ReadBack_AndKeepUnknownKeys()
    {
        File.WriteAllText(RepoRulesFile.FilePath(_root), """{ "note": "keep" }""");

        RepoRulesFile.Write(_root, new RepoRules("main", ["main", "release/*"], ["build"], "^(feat|fix)/", ["dotnet test"]));

        var rules = RepoRulesFile.Read(_root);
        Assert.Equal("main", rules.DefaultBranch);
        Assert.Equal(["main", "release/*"], rules.ProtectedBranches);
        Assert.Equal(["build"], rules.RequiredChecks);
        Assert.Equal("^(feat|fix)/", rules.BranchNamePattern);
        Assert.Equal(["dotnet test"], rules.GateCommands);
        Assert.Equal("keep", JsonNode.Parse(File.ReadAllText(RepoRulesFile.FilePath(_root)))!["note"]!.GetValue<string>());
    }

    [Fact]
    public void AMalformedFile_MeansNoRules_WithAWarning_AndIsNotOverwritten()
    {
        File.WriteAllText(RepoRulesFile.FilePath(_root), "{ broken");
        var warnings = new List<string>();

        Assert.True(RepoRulesFile.Read(_root, warnings).IsEmpty);
        Assert.Single(warnings);
        Assert.Throws<IOException>(() => RepoRulesFile.Write(_root, RepoRules.Empty with { DefaultBranch = "main" }));
        Assert.Equal("{ broken", File.ReadAllText(RepoRulesFile.FilePath(_root)));
    }

    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("MAIN", "main", true)]
    [InlineData("release/*", "release/1.2", true)]
    [InlineData("release/*", "feature/release/1", false)]
    [InlineData("main", "main2", false)]
    [InlineData("", "main", false)]
    public void BranchPatterns_UseAWildcardAndIgnoreCase(string pattern, string branch, bool expected) =>
        Assert.Equal(expected, RepoRulesFile.BranchMatches(pattern, branch));

    [Fact]
    public void TheLivePolicy_IsAddedToTheCommittedRules()
    {
        RepoRulesFile.Write(_root, RepoRules.Empty with { ProtectedBranches = ["main"], RequiredChecks = ["build"] });
        RepoRulesFile.WriteLive(_root, new LivePolicy(["dev", "main"], ["lint"], Now));

        var effective = RepoRulesFile.Effective(_root);

        Assert.Equal(["main", "dev"], effective.ProtectedBranches);
        Assert.Equal(["build", "lint"], effective.RequiredChecks);
        Assert.True(effective.IsProtected("dev"));
        Assert.False(effective.IsProtected("feat/x"));
        Assert.Equal(Now, RepoRulesFile.ReadLive(_root)!.CheckedUtc);
    }

    [Theory]
    [InlineData("^(feat|fix)/.+", "feat/a", true)]
    [InlineData("^(feat|fix)/.+", "random", false)]
    [InlineData("([", "anything", true)]
    [InlineData(null, "anything", true)]
    public void BranchNames_AreCheckedAgainstThePattern_AndABadPatternNeverRefuses(string? pattern, string name, bool expected)
    {
        var rules = new EffectiveRules(RepoRules.Empty with { BranchNamePattern = pattern }, null);

        Assert.Equal(expected, rules.NameAllowed(name));
    }

    [Fact]
    public void AnOverride_IsRequestedGrantedAndUsedOnce()
    {
        var id = RuleOverrides.Request(_root, "push", "main", Now);

        Assert.False(RuleOverrides.TryConsume(_root, "push", "main", Now));
        Assert.Equal(("push", "main"), RuleOverrides.Find(_root, id, Now));
        Assert.True(RuleOverrides.Grant(_root, id, Now));
        Assert.False(RuleOverrides.Grant(_root, id, Now));
        Assert.False(RuleOverrides.TryConsume(_root, "commit", "main", Now));
        Assert.True(RuleOverrides.TryConsume(_root, "push", "MAIN", Now.AddMinutes(1)));
        Assert.False(RuleOverrides.TryConsume(_root, "push", "main", Now.AddMinutes(1)));
    }

    [Fact]
    public void AGrant_ExpiresAfterTenMinutes_AndARequestAfterAnHour()
    {
        var id = RuleOverrides.Request(_root, "push", "main", Now);
        RuleOverrides.Grant(_root, id, Now);

        Assert.False(RuleOverrides.TryConsume(_root, "push", "main", Now.AddMinutes(11)));

        var stale = RuleOverrides.Request(_root, "commit", "main", Now);
        Assert.Null(RuleOverrides.Find(_root, stale, Now.AddHours(2)));
        Assert.False(RuleOverrides.Grant(_root, stale, Now.AddHours(2)));
    }

    [Fact]
    public void FindRepoRootFromFolder_FindsAGitFolderOrFile_AndNullOutsideGit()
    {
        var sub = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(sub);

        Assert.Equal(_root, RepoSettingsFile.FindRepoRootFromFolder(sub));
        Assert.Null(RepoSettingsFile.FindRepoRootFromFolder(null));
    }
}
