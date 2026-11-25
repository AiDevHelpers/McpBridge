using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class InstallableToolsTests
{
    [Fact]
    public void TheTable_HasUniqueNamesAndExactWingetIds()
    {
        Assert.Equal(InstallableTools.All.Count, InstallableTools.All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(InstallableTools.All, t => Assert.Matches(@"^[A-Za-z0-9]+(\.[A-Za-z0-9]+)+$", t.PackageId));
    }

    [Theory]
    [InlineData("pwsh", "Microsoft.PowerShell")]
    [InlineData("bash", "Git.Git")]
    [InlineData("gitbash", "Git.Git")]
    [InlineData("python", "Python.Python.3.13")]
    [InlineData("node", "OpenJS.NodeJS.LTS")]
    public void InterpreterNamesMapToPackages(string name, string id) => Assert.Equal(id, InstallableTools.ForInterpreter(name)!.PackageId);

    [Theory]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("Microsoft.PowerShell")]
    [InlineData(null)]
    public void AnythingElseIsNotInstallable(string? name) => Assert.Null(InstallableTools.ForInterpreter(name ?? string.Empty));
}
