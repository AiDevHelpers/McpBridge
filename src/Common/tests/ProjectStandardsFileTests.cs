using AiDevHelpers.Common;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class ProjectStandardsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "standards-" + Guid.NewGuid().ToString("N"));

    public ProjectStandardsFileTests() => Directory.CreateDirectory(Path.Combine(_root, RepoSettingsFile.FolderName));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string json) =>
        File.WriteAllText(Path.Combine(_root, RepoSettingsFile.FolderName, ProjectStandardsFile.FileName), json);

    [Fact]
    public void ValidFileIsReadWithNoWarnings()
    {
        Write("""
        {
          "baseFramework": "net10.0",
          "allowedPlatformSuffixes": ["-windows"],
          "properties": { "LangVersion": "latest", "Nullable": "enable", "ImplicitUsings": "enable" },
          "analyzers": ["Microsoft.CodeAnalysis.NetAnalyzers"],
          "exceptions": [ { "project": "src/Installers/Vs2026/Core/src/Core.csproj", "framework": "net48", "reason": "VS extension host" } ]
        }
        """);

        var result = ProjectStandardsFile.Read(_root);

        Assert.Empty(result.Warnings);
        Assert.NotNull(result.Standards);
        Assert.Equal("net10.0", result.Standards!.BaseFramework);
        Assert.Equal(["-windows"], result.Standards.AllowedPlatformSuffixes);
        Assert.Equal("enable", result.Standards.Properties["Nullable"]);
        Assert.Equal(["Microsoft.CodeAnalysis.NetAnalyzers"], result.Standards.Analyzers);
        var exception = Assert.Single(result.Standards.Exceptions);
        Assert.Equal("src/Installers/Vs2026/Core/src/Core.csproj", exception.Project);
        Assert.Equal("net48", exception.Framework);
        Assert.Equal("VS extension host", exception.Reason);
    }

    [Fact]
    public void MissingFileGivesNoStandardsAndNoWarning()
    {
        var result = ProjectStandardsFile.Read(_root);

        Assert.Null(result.Standards);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void MalformedJsonIsReportedNotThrown()
    {
        Write("{ not json");

        var result = ProjectStandardsFile.Read(_root);

        Assert.Null(result.Standards);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("""{ "baseFramework": "net-ten" }""")]
    [InlineData("""{ "baseFramework": "" }""")]
    public void BadTargetFrameworkIsRejected(string json)
    {
        Write(json);

        var result = ProjectStandardsFile.Read(_root);

        Assert.Null(result.Standards);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void ExceptionWithEmptyReasonIsDropped()
    {
        Write("""{ "baseFramework": "net10.0", "exceptions": [ { "project": "a/b.csproj", "framework": "net48", "reason": " " } ] }""");

        var result = ProjectStandardsFile.Read(_root);

        Assert.Empty(result.Standards!.Exceptions);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("../outside/x.csproj")]
    [InlineData("C:/abs/x.csproj")]
    [InlineData("/abs/x.csproj")]
    public void UnsafeProjectPathIsDropped(string project)
    {
        Write($$"""{ "baseFramework": "net10.0", "exceptions": [ { "project": "{{project}}", "framework": "net48", "reason": "x" } ] }""");

        var result = ProjectStandardsFile.Read(_root);

        Assert.Empty(result.Standards!.Exceptions);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void UnknownKeyWarnsAndIsIgnored()
    {
        Write("""{ "baseFramework": "net10.0", "colour": "blue" }""");

        var result = ProjectStandardsFile.Read(_root);

        Assert.Equal("net10.0", result.Standards!.BaseFramework);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        Write("""
        {
          // comment
          "baseFramework": "net10.0",
          "analyzers": [ "A", ],
        }
        """);

        var result = ProjectStandardsFile.Read(_root);

        Assert.Empty(result.Warnings);
        Assert.Equal(["A"], result.Standards!.Analyzers);
    }

    [Fact]
    public void ReaderNeverWritesTheFile()
    {
        var text = """{ "baseFramework": "net10.0" }""";
        Write(text);
        var path = ProjectStandardsFile.FilePath(_root);

        ProjectStandardsFile.Read(_root);

        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void FilePathIsInsideTheSettingsFolder()
    {
        Assert.Equal(
            Path.Combine(_root, ".aidevhelpers", "project-standards.json"),
            ProjectStandardsFile.FilePath(_root));
    }
}
