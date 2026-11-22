using AiDevHelpers.Helpers.PackageHelper;
using Xunit;

namespace AiDevHelpers.Helpers.PackageHelper.Tests;

public sealed class PackageChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "package-helper-tests", Guid.NewGuid().ToString("N"));

    public PackageChecksTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void TestProjectWithoutXunitAnalyzersIsWarned()
    {
        Write("tests/T.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" /></ItemGroup></Project>");

        var finding = Assert.Single(PackageChecks.Analyze(_root), f => f.Message.Contains("xunit.analyzers"));
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void TestProjectWithXunitAnalyzersIsClean()
    {
        Write("tests/T.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /><PackageReference Include=\"xunit.analyzers\" /></ItemGroup></Project>");

        Assert.DoesNotContain(PackageChecks.Analyze(_root), f => f.Message.Contains("xunit.analyzers"));
    }

    [Fact]
    public void DisabledNetAnalyzersIsWarned()
    {
        Write("app/App.csproj", "<Project><PropertyGroup><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>");

        Assert.Contains(PackageChecks.Analyze(_root), f => f.Ecosystem == "nuget" && f.Severity == "warning" && f.Message.Contains("EnableNETAnalyzers"));
    }

    [Fact]
    public void UnpinnedRequirementIsWarnedAndCommentsIgnored()
    {
        Write("requirements.txt", "# comment\nrequests==2.32.0\nflask>=3\n-r other.txt\n");

        var finding = Assert.Single(PackageChecks.Analyze(_root), f => f.Ecosystem == "pip");
        Assert.Contains("flask>=3", finding.Message);
    }

    [Fact]
    public void NpmPackageWithoutLockfileIsAnError()
    {
        Write("web/package.json", "{}");

        var finding = Assert.Single(PackageChecks.Analyze(_root), f => f.Ecosystem == "npm");
        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void NpmPackageWithLockfileIsClean()
    {
        Write("web/package.json", "{}");
        Write("web/package-lock.json", "{}");

        Assert.DoesNotContain(PackageChecks.Analyze(_root), f => f.Ecosystem == "npm");
    }

    [Fact]
    public void MissingFolderThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => PackageChecks.Analyze(Path.Combine(_root, "nope")));
    }
}
