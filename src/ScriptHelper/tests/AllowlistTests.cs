using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class AllowlistTests
{
    private static readonly AllowlistEntry Attach =
        new("scripts/debug/attach.ps1", new[] { "-Port", "*" });

    [Fact]
    public void Listed_path_with_matching_args_is_allowed()
    {
        var d = Allowlist.Evaluate(new[] { Attach }, "scripts/debug/attach.ps1", new[] { "-Port", "5000" });

        Assert.True(d.Allowed);
    }

    [Fact]
    public void Unlisted_path_is_denied()
    {
        var d = Allowlist.Evaluate(new[] { Attach }, "scripts/debug/other.ps1", new[] { "-Port", "5000" });

        Assert.False(d.Allowed);
    }

    [Fact]
    public void Empty_allowlist_denies_everything()
    {
        var d = Allowlist.Evaluate(Array.Empty<AllowlistEntry>(), "scripts/debug/attach.ps1", Array.Empty<string>());

        Assert.False(d.Allowed);
    }

    [Fact]
    public void Wildcard_path_matches_files_in_its_folder()
    {
        var entries = new[] { new AllowlistEntry("scripts/debug/*.ps1", null) };

        var d = Allowlist.Evaluate(entries, "scripts/debug/reset.ps1", Array.Empty<string>());

        Assert.True(d.Allowed);
    }

    [Fact]
    public void Wildcard_path_does_not_cross_directories()
    {
        var entries = new[] { new AllowlistEntry("scripts/debug/*.ps1", null) };

        var d = Allowlist.Evaluate(entries, "scripts/debug/sub/reset.ps1", Array.Empty<string>());

        Assert.False(d.Allowed);
    }

    [Fact]
    public void Entry_without_args_denies_any_argument()
    {
        var entries = new[] { new AllowlistEntry("scripts/debug/reset.ps1", null) };

        var d = Allowlist.Evaluate(entries, "scripts/debug/reset.ps1", new[] { "-Force" });

        Assert.False(d.Allowed);
    }

    [Fact]
    public void Argument_count_mismatch_is_denied()
    {
        var d = Allowlist.Evaluate(new[] { Attach }, "scripts/debug/attach.ps1", new[] { "-Port" });

        Assert.False(d.Allowed);
    }

    [Theory]
    [InlineData("5000;calc.exe")]
    [InlineData("5000 | evil")]
    [InlineData("5000 & evil")]
    [InlineData("\"5000\"")]
    [InlineData("'5000'")]
    [InlineData("5000`n")]
    [InlineData("$(evil)")]
    public void Wildcard_does_not_match_shell_metacharacters(string value)
    {
        var d = Allowlist.Evaluate(new[] { Attach }, "scripts/debug/attach.ps1", new[] { "-Port", value });

        Assert.False(d.Allowed);
    }

    [Fact]
    public void Literal_argument_must_match_exactly()
    {
        var entries = new[] { new AllowlistEntry("scripts/debug/reset.ps1", new[] { "-Force" }) };

        Assert.True(Allowlist.Evaluate(entries, "scripts/debug/reset.ps1", new[] { "-Force" }).Allowed);
        Assert.False(Allowlist.Evaluate(entries, "scripts/debug/reset.ps1", new[] { "-Forc" }).Allowed);
    }

    [Fact]
    public void Denial_carries_a_reason()
    {
        var d = Allowlist.Evaluate(Array.Empty<AllowlistEntry>(), "scripts/debug/x.ps1", Array.Empty<string>());

        Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }
}
