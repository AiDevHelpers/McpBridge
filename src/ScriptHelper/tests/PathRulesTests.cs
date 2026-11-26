using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class PathRulesTests
{
    private const string Root = @"C:\repo";

    [Fact]
    public void Relative_path_inside_root_resolves()
    {
        var r = PathRules.ResolveInsideRoot(Root, "scripts/debug/attach.ps1");

        Assert.True(r.Ok);
        Assert.Equal(@"C:\repo\scripts\debug\attach.ps1", r.FullPath);
    }

    [Fact]
    public void Dot_dot_escape_is_rejected()
    {
        var r = PathRules.ResolveInsideRoot(Root, @"..\repo2\evil.ps1");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Absolute_path_is_rejected_even_inside_root()
    {
        var r = PathRules.ResolveInsideRoot(Root, @"C:\repo\scripts\a.ps1");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Rooted_path_outside_root_is_rejected()
    {
        var r = PathRules.ResolveInsideRoot(Root, @"C:\Windows\System32\cmd.exe");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Sibling_prefix_directory_is_rejected()
    {
        // "C:\repo2" starts with "C:\repo" but is outside the root.
        var r = PathRules.ResolveInsideRoot(Root, @"..\repo2\x.ps1");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Root_comparison_is_case_insensitive()
    {
        var r = PathRules.ResolveInsideRoot(@"C:\Repo", @"SCRIPTS\a.ps1");

        Assert.True(r.Ok);
    }

    [Fact]
    public void Forward_slash_and_backslash_resolve_the_same()
    {
        var a = PathRules.ResolveInsideRoot(Root, "scripts/a.ps1");
        var b = PathRules.ResolveInsideRoot(Root, @"scripts\a.ps1");

        Assert.Equal(a.FullPath, b.FullPath);
    }

    [Fact]
    public void Empty_path_is_rejected()
    {
        var r = PathRules.ResolveInsideRoot(Root, "");

        Assert.False(r.Ok);
    }

    [Theory]
    [InlineData("scripts/<an")]
    [InlineData("scripts/a|b.ps1")]
    [InlineData("scripts/what?.ps1")]
    [InlineData("scripts/a*.ps1")]
    [InlineData("scripts/a.ps1:stream")]
    [InlineData("scripts/\"quoted\".ps1")]
    [InlineData("scripts/a\tb.ps1")]
    public void NamesWindowsCannotHold_AreRefusedNotThrown(string path)
    {
        var r = PathRules.ResolveInsideRoot(Root, path);

        Assert.False(r.Ok);
        Assert.Contains("not allowed", r.Reason);
    }
}
