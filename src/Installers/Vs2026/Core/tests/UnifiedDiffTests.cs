using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void AnUnchangedFileHasNoDiff()
    {
        Assert.Null(UnifiedDiff.Of("a.cs", "class A { }\n", "class A { }\n"));
    }

    [Fact]
    public void AOneLineChangeShowsOneHunk()
    {
        var diff = UnifiedDiff.Of("a.cs", "one\ntwo\nthree\n", "one\n2\nthree\n");

        Assert.Equal("--- a/a.cs\n+++ b/a.cs\n@@ -2,1 +2,1 @@\n-two\n+2\n", diff);
    }

    [Fact]
    public void AnAddedLineShowsAsAdditionOnly()
    {
        var diff = UnifiedDiff.Of("a.cs", "one\nthree\n", "one\ntwo\nthree\n");

        Assert.Contains("+two\n", diff);
        Assert.DoesNotContain("-", diff!.Split('\n').Skip(3).First());
    }

    [Fact]
    public void ALongDiffIsTruncatedAtTheLimit()
    {
        var before = string.Concat(Enumerable.Range(0, 1000).Select(i => "old" + i + "\n"));
        var after = string.Concat(Enumerable.Range(0, 1000).Select(i => "new" + i + "\n"));

        var diff = UnifiedDiff.Of("a.cs", before, after);

        Assert.Contains("diff truncated after 400 lines", diff);
    }

    [Fact]
    public void CrLfAndLfOfTheSameTextDoNotDiffer()
    {
        Assert.Null(UnifiedDiff.Of("a.cs", "a\r\nb\r\n", "a\nb\n"));
    }
}
