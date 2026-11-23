using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RulesScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-rules-" + Guid.NewGuid().ToString("N"));

    public RulesScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string Workflow = """
        name: CI
        on:
          push:
            branches: [main, dev]
          pull_request:
            branches:
              - main
              - 'release/*'
        jobs:
          build:
            name: Build and test
            runs-on: windows-latest
            steps:
              - name: Checkout
                uses: actions/checkout@v4
              - uses: actions/upload-artifact@v4
                with:
                  name: logs
          lint:
            runs-on: ubuntu-latest
            steps:
              - run: echo hi
        """;

    [Fact]
    public void AWorkflow_GivesItsBranchesAndItsJobNames()
    {
        Write(".github/workflows/ci.yml", Workflow);

        var report = RulesScanner.Scan(_root);

        var workflow = Assert.Single(report.Workflows);
        Assert.Equal(".github/workflows/ci.yml", workflow.Path);
        Assert.Equal(["main", "dev", "release/*"], workflow.Branches.ToList());
        Assert.Equal(["Build and test", "lint"], workflow.Jobs.ToList());
        Assert.Equal(["dev", "main", "release/*"], report.CandidateProtectedBranches.ToList());
        Assert.Equal(["Build and test", "lint"], report.CandidateRequiredChecks.ToList());
    }

    [Fact]
    public void InstructionFiles_AreQuotedOnlyWhereTheyMentionBranchesOrChecks()
    {
        Write("CLAUDE.md", "# Repo\nNever push to main.\nOpen a pull request.\nUnrelated line.\n");
        Write("CONTRIBUTING.md", "Be nice.\n");
        Write("docs/release.md", "The default branch is protected; required checks must pass.\n");
        Write("docs/other.md", "nothing here\n");

        var report = RulesScanner.Scan(_root);

        Assert.Equal(["CLAUDE.md", "docs/release.md"], report.Instructions.Select(i => i.Path).ToList());
        Assert.Equal(2, report.Instructions[0].Lines.Count);
        Assert.DoesNotContain("Unrelated line.", report.Instructions[0].Lines);
    }

    [Fact]
    public void HooksAndCodeOwnersAndTheDefaultBranch_AreReported()
    {
        Write(".githooks/pre-commit", "#!/bin/sh\n");
        Write(".husky/pre-push", "x");
        Write(".git/config", "[core]\n\thooksPath = .githooks\n");
        Write(".git/refs/remotes/origin/HEAD", "ref: refs/remotes/origin/main\n");
        Write(".github/CODEOWNERS", "* @team\n");

        var report = RulesScanner.Scan(_root);

        Assert.Equal([".githooks/pre-commit", ".husky/pre-push", "core.hooksPath = .githooks"], report.Hooks.ToList());
        Assert.Equal(".github/CODEOWNERS", report.CodeOwners);
        Assert.Equal("main", report.DefaultBranch);
    }

    [Fact]
    public void AnEmptyRepo_ReportsNothing()
    {
        var report = RulesScanner.Scan(_root);

        Assert.Empty(report.Workflows);
        Assert.Empty(report.Instructions);
        Assert.Empty(report.Hooks);
        Assert.Null(report.CodeOwners);
        Assert.Null(report.DefaultBranch);
    }

    [Fact]
    public void AMalformedWorkflow_DoesNotThrow()
    {
        Write(".github/workflows/bad.yml", "{{{ not yaml\n\tjobs:\n");

        var report = RulesScanner.Scan(_root);

        Assert.Single(report.Workflows);
    }
}
