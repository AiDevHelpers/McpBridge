using AiDevHelpers.Bridge;
using Xunit;

namespace AiDevHelpers.Bridge.Tests;

public sealed class PathCompareTests
{
    [Fact]
    public void SamePathIgnoresCase()
    {
        Assert.True(PathCompare.Same(@"C:\Repo\App.sln", @"c:\repo\app.SLN"));
    }

    [Fact]
    public void MissingOrMalformedPathMatchesNothing()
    {
        Assert.False(PathCompare.Same(null, @"C:\Repo\App.sln"));
        Assert.False(PathCompare.Same(@"C:\Repo\App.sln", ""));
        Assert.False(PathCompare.Same("bad\0path", @"C:\Repo\App.sln"));
    }
}
