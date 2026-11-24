using AiDevHelpers.Common;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RepoApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-apply-" + Guid.NewGuid().ToString("N"));

    public RepoApplyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "refs", "remotes", "origin"));
        File.WriteAllText(Path.Combine(_root, ".git", "refs", "remotes", "origin", "HEAD"), "ref: refs/remotes/origin/main\n");
        Write("src/Drv/Drv.csproj", "<Project />");
        Write("src/Drv/app.manifest", """<x><requestedExecutionLevel level="requireAdministrator" /></x>""");
        Write(".github/workflows/ci.yml", "on:\n  push:\n    branches: [main, dev]\njobs:\n  build:\n    runs-on: x\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed class Never : IConfirmer
    {
        public bool Confirm(string prompt) => throw new InvalidOperationException("apply must not ask");
    }

    private int Apply(RepoDecisions decisions, out string output)
    {
        var writer = new StringWriter();
        var exit = RepoSetup.Apply(_root, decisions, writer);
        output = writer.ToString();
        return exit;
    }

    [Fact]
    public void Everything_IsWrittenLikeTheInteractiveSetup()
    {
        var exit = Apply(new RepoDecisions(["src/Drv/Drv.csproj"], null, ["main", "dev"], ["build"], false, "always"), out _);

        Assert.Equal(0, exit);
        var elevation = ElevationFiles.Read(_root);
        Assert.Equal("src/Drv/Drv.csproj", Assert.Single(elevation.Projects).Path);
        Assert.Equal(ElevationMode.Always, elevation.Mode);
        var rules = RepoRulesFile.Read(_root);
        Assert.Equal(["main", "dev"], rules.ProtectedBranches);
        Assert.Equal(["build"], rules.RequiredChecks);
        Assert.Equal("main", rules.DefaultBranch);
        Assert.Contains(".aidevtools/user.json", File.ReadAllLines(Path.Combine(_root, ".gitignore")));
        Assert.Empty(RepoSetup.Scan(_root).PendingProjects);
    }

    [Fact]
    public void ANameTheScanDoesNotReport_RefusesTheWholeCall_AndWritesNothing()
    {
        var exit = Apply(new RepoDecisions(["src/Drv/Drv.csproj"], null, ["main", "release"], null, false, null), out var output);

        Assert.Equal(1, exit);
        Assert.Contains("'release' is not a pending protected branch", output);
        Assert.False(Directory.Exists(Path.Combine(_root, ".aidevtools")));
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("")]
    public void AnUnknownMode_IsRefused(string mode)
    {
        Assert.Equal(1, Apply(new RepoDecisions(Mode: mode), out var output));
        Assert.Contains("unknown mode", output);
        Assert.False(Directory.Exists(Path.Combine(_root, ".aidevtools")));
    }

    [Fact]
    public void AProjectCannotBeBothListedAndDeclined()
    {
        var exit = Apply(new RepoDecisions(["src/Drv/Drv.csproj"], ["src/Drv/Drv.csproj"]), out var output);

        Assert.Equal(1, exit);
        Assert.Contains("both listed and declined", output);
    }

    [Fact]
    public void Declining_IsRemembered_SoNothingIsPendingAfterwards()
    {
        var exit = Apply(new RepoDecisions(null, ["src/Drv/Drv.csproj"], null, null, DeclineRules: true), out _);

        Assert.Equal(0, exit);
        var report = RepoSetup.Scan(_root);
        Assert.Empty(report.PendingProjects);
        Assert.Empty(report.PendingProtectedBranches);
        Assert.Empty(report.PendingRequiredChecks);
        Assert.False(File.Exists(RepoRulesFile.FilePath(_root)));
    }

    [Fact]
    public void RecordingSome_AndDecliningTheRest_KeepsWhatWasRecorded()
    {
        Apply(new RepoDecisions(null, null, ["main"], null, DeclineRules: true), out _);

        var rules = RepoRulesFile.Read(_root);
        Assert.Equal(["main"], rules.ProtectedBranches);
        Assert.Empty(RepoSetup.Scan(_root).PendingProtectedBranches);
        Assert.Empty(RepoSetup.Scan(_root).PendingRequiredChecks);
    }

    [Fact]
    public void AMissingFolder_IsRefused()
    {
        var writer = new StringWriter();

        Assert.Equal(1, RepoSetup.Apply(Path.Combine(_root, "nope"), new RepoDecisions(), writer));
    }

    [Fact]
    public void TheVerb_TakesJson_AndRefusesBadJsonAndWrongArity()
    {
        var output = new StringWriter();
        var json = """{"protectedBranches":["main"],"mode":"never"}""";

        Assert.True(ElevLocalCommands.TryRun(["repo-apply", _root, json], output, new Never(), out var exit));
        Assert.Equal(0, exit);
        Assert.Equal(ElevationMode.Never, ElevationFiles.Read(_root).Mode);

        Assert.True(ElevLocalCommands.TryRun(["repo-apply", _root, "{ broken"], output, new Never(), out var bad));
        Assert.Equal(2, bad);
        Assert.True(ElevLocalCommands.TryRun(["repo-apply", _root], output, new Never(), out var arity));
        Assert.Equal(2, arity);
    }
}
