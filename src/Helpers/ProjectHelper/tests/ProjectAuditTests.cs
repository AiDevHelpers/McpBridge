using AiDevHelpers.Common;
using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class ProjectAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audit-" + Guid.NewGuid().ToString("N"));

    public ProjectAuditTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));
        File.WriteAllText(Path.Combine(_root, ".aidevhelpers", "project-standards.json"), """
        {
          "baseFramework": "net10.0",
          "allowedPlatformSuffixes": [ "-windows" ],
          "properties": { "Nullable": "enable" },
          "analyzers": [ "Foo.Analyzers" ],
          "exceptions": [ { "project": "src/Host/Host.csproj", "framework": "net48", "reason": "extension host" } ]
        }
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Csproj(string relative, string frameworks, string properties = "<Nullable>enable</Nullable>", string items = "")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFrameworks>" + frameworks + "</TargetFrameworks>" + properties + "</PropertyGroup>\n  <ItemGroup>"
            + items + "</ItemGroup>\n</Project>\n");
    }

    private const string Analyzer = "<PackageReference Include=\"Foo.Analyzers\" Version=\"1.0.0\" />";
    private const string TestSdk = "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" />";

    private ProjectAuditReport Audit() => ProjectAudit.Run(_root, ProjectStandardsFile.Read(_root).Standards, projectFilter: null);

    [Fact]
    public void CleanProjectHasNoErrors()
    {
        Csproj("src/Lib/Lib.csproj", "net10.0", items: Analyzer);

        var report = Audit();

        Assert.True(report.Ok, string.Join("; ", report.Findings.Select(f => f.Message)));
    }

    [Fact]
    public void RefusedFrameworkIsAnError()
    {
        Csproj("src/Lib/Lib.csproj", "net8.0", items: Analyzer);

        var finding = Assert.Single(Audit().Findings, f => f.Check == "framework");

        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void ExceptedFrameworkIsInfoNotAnError()
    {
        Csproj("src/Host/Host.csproj", "net48", items: Analyzer);

        var report = Audit();

        Assert.True(report.Ok);
        Assert.Equal("info", Assert.Single(report.Findings, f => f.Check == "framework").Severity);
    }

    [Fact]
    public void MissingPropertyIsAnError()
    {
        Csproj("src/Lib/Lib.csproj", "net10.0", properties: "", items: Analyzer);

        var finding = Assert.Single(Audit().Findings, f => f.Check == "property");

        Assert.Equal("error", finding.Severity);
        Assert.Contains("Nullable", finding.Message);
    }

    [Fact]
    public void MissingAnalyzerIsAnError()
    {
        Csproj("src/Lib/Lib.csproj", "net10.0");

        var finding = Assert.Single(Audit().Findings, f => f.Check == "analyzer");

        Assert.Equal("error", finding.Severity);
        Assert.Contains("Foo.Analyzers", finding.Message);
    }

    [Fact]
    public void TestProjectWithoutTheTestSdkIsAnError()
    {
        Csproj("src/Lib/tests/Lib.Tests.csproj", "net10.0", items: Analyzer);

        var finding = Assert.Single(Audit().Findings, f => f.Check == "test-sdk");

        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void TestProjectWithTheTestSdkPasses()
    {
        Csproj("src/Lib/tests/Lib.Tests.csproj", "net10.0", items: Analyzer + TestSdk);

        Assert.DoesNotContain(Audit().Findings, f => f.Check == "test-sdk");
    }

    [Fact]
    public void DifferingVersionsAreAWarningWithTheChoice()
    {
        Csproj("src/A/A.csproj", "net10.0", items: Analyzer);
        Csproj("src/B/B.csproj", "net10.0", items: "<PackageReference Include=\"Foo.Analyzers\" Version=\"2.0.0\" />");

        var finding = Assert.Single(Audit().Findings, f => f.Check == "package-version");

        Assert.Equal("warning", finding.Severity);
        Assert.Contains("Foo.Analyzers", finding.Message);
    }

    [Fact]
    public void AuditNeverWritesTheStandardsOrTheProjects()
    {
        Csproj("src/Lib/Lib.csproj", "net8.0", properties: "");
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var before = files.ToDictionary(f => f, File.ReadAllBytes);

        Audit();

        foreach (var file in files)
        {
            Assert.Equal(before[file], File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void ProjectFilterLimitsTheAudit()
    {
        Csproj("src/Lib/Lib.csproj", "net8.0", items: Analyzer);
        Csproj("src/Other/Other.csproj", "net10.0", items: Analyzer);

        var report = ProjectAudit.Run(_root, ProjectStandardsFile.Read(_root).Standards, projectFilter: "src/Other/Other.csproj");

        Assert.True(report.Ok);
        Assert.All(report.Findings, f => Assert.Equal("src/Other/Other.csproj", f.Project));
    }
}
