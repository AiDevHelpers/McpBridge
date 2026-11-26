using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class PlaceholdersTests
{
    [Fact]
    public void FindsNumberedPlaceholders() =>
        Assert.Equal(["{0}", "{1}"], Placeholders.Of("a {0} b {1}").Order());

    [Fact]
    public void FindsNamedPlaceholders() =>
        Assert.Equal(["{name}"], Placeholders.Of("Hi {name}"));

    [Fact]
    public void FindsPlaceholdersWithFormat() =>
        Assert.Equal(["{0:N2}"], Placeholders.Of("Total {0:N2}"));

    [Fact]
    public void DoubleBracesAreNotPlaceholders() =>
        Assert.Empty(Placeholders.Of("Use {{literal}} braces"));
}
