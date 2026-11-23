using AiDevHelpers.Common;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RepoSetupRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-setup-rules-" + Guid.NewGuid().ToString("N"));

    public RepoSetupRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "refs", "remotes", "origin"));
        File.WriteAllText(Path.Combine(_root, ".git", "refs", "remotes", "origin", "HEAD"), "ref: refs/remotes/origin/main\n");
        Directory.CreateDirectory(Path.Combine(_root, ".github", "workflows"));
        File.WriteAllText(Path.Combine(_root, ".github", "workflows", "ci.yml"), "on:\n  push:\n    branches: [main, dev]\njobs:\n  build:\n    runs-on: x\n");
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "Never push to main.\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Answers(params bool[] yes) : IConfirmer
    {
        private readonly Queue<bool> _yes = new(yes);

        public List<string> Asked { get; } = [];

        public bool Confirm(string prompt)
        {
            Asked.Add(prompt);
            return _yes.Count > 0 && _yes.Dequeue();
        }
    }

    [Fact]
    public void Scan_ReportsTheCandidates_AndWritesNothing()
    {
        var report = RepoSetup.Scan(_root);

        Assert.Equal(["dev", "main"], report.PendingProtectedBranches);
        Assert.Equal(["build"], report.PendingRequiredChecks);
        Assert.False(File.Exists(RepoRulesFile.FilePath(_root)));
    }

    [Fact]
    public void Setup_YesRecordsTheRules_WithTheDefaultBranch_AndShowsTheInstructionLines()
    {
        var output = new StringWriter();

        RepoSetup.Setup(_root, output, new Answers(true, true));

        var rules = RepoRulesFile.Read(_root);
        Assert.Equal(["dev", "main"], rules.ProtectedBranches);
        Assert.Equal(["build"], rules.RequiredChecks);
        Assert.Equal("main", rules.DefaultBranch);
        Assert.Contains("Never push to main.", output.ToString());
        Assert.Contains("only the CI branches and checks", output.ToString());
    }

    [Fact]
    public void Setup_NoRecordsNothing_AndIsNotAskedAgain()
    {
        RepoSetup.Setup(_root, new StringWriter(), new Answers(false, false));
        var again = new Answers(true, true);

        RepoSetup.Setup(_root, new StringWriter(), again);

        Assert.False(File.Exists(RepoRulesFile.FilePath(_root)));
        Assert.Empty(again.Asked);
    }

    [Fact]
    public void Setup_ARecordedRule_IsNotAskedAboutAgain()
    {
        RepoSetup.Setup(_root, new StringWriter(), new Answers(true, true));
        var again = new Answers(true, true);

        RepoSetup.Setup(_root, new StringWriter(), again);

        Assert.Empty(again.Asked);
    }
}
