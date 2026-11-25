using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class FolderGlobTests
{
    [Fact]
    public void DoubleStarUnderADrive_MatchesADeepPath()
    {
        Assert.True(FolderGlob.IsMatch(@"F:\**\**", @"F:\src\tools\deep\run.ps1"));
    }

    [Fact]
    public void DoubleStarUnderADrive_DoesNotMatchAnotherDrive()
    {
        Assert.False(FolderGlob.IsMatch(@"F:\**\**", @"G:\x.ps1"));
    }

    [Fact]
    public void SingleStar_StaysInsideOneFolder()
    {
        Assert.True(FolderGlob.IsMatch(@"F:\repo\*.ps1", @"F:\repo\a.ps1"));
        Assert.False(FolderGlob.IsMatch(@"F:\repo\*.ps1", @"F:\repo\sub\a.ps1"));
    }

    [Fact]
    public void Matching_IgnoresCaseAndSlashDirection()
    {
        Assert.True(FolderGlob.IsMatch(@"f:\**\**", "F:/Src/Run.ps1"));
    }

    [Fact]
    public void Glob_MustStartWithADrive()
    {
        Assert.True(FolderGlob.IsAbsolute(@"F:\**\**"));
        Assert.False(FolderGlob.IsAbsolute("**"));
        Assert.False(FolderGlob.IsAbsolute(""));
    }
}
