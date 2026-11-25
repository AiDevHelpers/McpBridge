using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class PlanHashTests
{
    private const string File = @"C:\scratch\h.cs";

    [Fact]
    public async Task TheSameChangeHashesTheSame()
    {
        var before = TestSolutions.Single([(File, "class C { int Old() { return 1; } }")]);
        var a = await MethodEditEngine.PlanAsync(before, File, 1, 11, "int Old() { return 2; }", Array.Empty<CallerEdit>());
        var b = await MethodEditEngine.PlanAsync(before, File, 1, 11, "int Old() { return 2; }", Array.Empty<CallerEdit>());

        Assert.Equal(await PlanHash.ComputeAsync(before, a.Result), await PlanHash.ComputeAsync(before, b.Result));
    }

    [Fact]
    public async Task ADifferentChangeHashesDifferently()
    {
        var before = TestSolutions.Single([(File, "class C { int Old() { return 1; } }")]);
        var a = await MethodEditEngine.PlanAsync(before, File, 1, 11, "int Old() { return 2; }", Array.Empty<CallerEdit>());
        var b = await MethodEditEngine.PlanAsync(before, File, 1, 11, "int Old() { return 3; }", Array.Empty<CallerEdit>());

        Assert.NotEqual(await PlanHash.ComputeAsync(before, a.Result), await PlanHash.ComputeAsync(before, b.Result));
    }

    [Fact]
    public async Task ANoChangeHasNoHash()
    {
        var before = TestSolutions.Single([(File, "class C { }")]);

        Assert.Null(await PlanHash.ComputeAsync(before, before));
    }

    [Fact]
    public void MatchesOnlyWhenTheShownHashIsPresentAndEqual()
    {
        Assert.True(PlanHash.Matches("x", "x"));
        Assert.False(PlanHash.Matches("x", "y"));
        Assert.False(PlanHash.Matches(null, null));
    }
}
