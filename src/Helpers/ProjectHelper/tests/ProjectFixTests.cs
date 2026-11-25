using System.Text;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class ProjectFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fix-" + Guid.NewGuid().ToString("N"));

    public ProjectFixTests()
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

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Csproj(string frameworks, string properties = "<Nullable>enable</Nullable>", string items = "") =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>" + frameworks + "</TargetFrameworks>\n    " + properties
        + "\n  </PropertyGroup>\n  <ItemGroup>\n    " + items + "\n  </ItemGroup>\n</Project>\n";

    private const string Analyzer = "<PackageReference Include=\"Foo.Analyzers\" Version=\"1.0.0\" />";
    private const string TestSdk = "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" />";

    private static RunCommand Build(int exitCode) =>
        (request, ct) => Task.FromResult(new CommandResult { ExitCode = exitCode, StdOut = "build output" });

    private FixPlanResult Plan(IReadOnlyCollection<string>? skip = null) =>
        ProjectFixer.Plan(_root, projectFilter: null, skip ?? []);

    [Fact]
    public void MissingPropertyIsSetAndTheAuditPasses()
    {
        var path = Write("src/Lib/Lib.csproj", Csproj("net10.0", properties: "", items: Analyzer));

        var plan = Plan();
        Assert.Contains(plan.Actions, a => a.Kind == FixKind.SetProperty && a.Name == "Nullable");

        var result = ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.Ok, result.Error);
        Assert.Contains("<Nullable>enable</Nullable>", File.ReadAllText(path));
        Assert.True(ProjectAudit.Run(_root, ProjectStandardsFile.Read(_root).Standards, null).Ok);
    }

    [Fact]
    public void ExceptedFrameworkIsNotRetargeted()
    {
        Write("src/Host/Host.csproj", Csproj("net48", items: Analyzer));

        Assert.DoesNotContain(Plan().Actions, a => a.Kind == FixKind.Retarget);
    }

    [Fact]
    public void RefusedFrameworkIsRetargetedToTheBase()
    {
        Write("src/Lib/Lib.csproj", Csproj("net8.0", items: Analyzer));

        var action = Assert.Single(Plan().Actions, a => a.Kind == FixKind.Retarget);

        Assert.Equal("net8.0", action.Name);
        Assert.Equal("net10.0", action.Value);
    }

    [Fact]
    public void MissingAnalyzerIsAddedAtTheChosenVersion()
    {
        Write("src/A/A.csproj", Csproj("net10.0", items: Analyzer));
        Write("src/Lib/Lib.csproj", Csproj("net10.0"));

        var action = Assert.Single(Plan().Actions, a => a.Kind == FixKind.AddPackage && a.Name == "Foo.Analyzers");

        Assert.Equal("1.0.0", action.Value);
    }

    [Fact]
    public void TestProjectGetsTheTestSdkAtTheRepoVersion()
    {
        Write("src/A/tests/A.Tests.csproj", Csproj("net10.0", items: Analyzer + TestSdk));
        Write("src/B/tests/B.Tests.csproj", Csproj("net10.0", items: Analyzer));

        var action = Assert.Single(Plan().Actions, a => a.Kind == FixKind.AddPackage && a.Name == "Microsoft.NET.Test.Sdk");

        Assert.Equal("src/B/tests/B.Tests.csproj", action.Project);
        Assert.Equal("18.0.0", action.Value);
    }

    [Fact]
    public void DifferingVersionIsAlignedToTheChoice()
    {
        Write("src/A/A.csproj", Csproj("net10.0", items: Analyzer));
        Write("src/B/B.csproj", Csproj("net10.0", items: "<PackageReference Include=\"Foo.Analyzers\" Version=\"2.0.0\" />"));

        var action = Assert.Single(Plan().Actions, a => a.Kind == FixKind.SetPackageVersion);

        // A pins 1.0.0 and B pins 2.0.0; the tie goes to the newest allowed, so A moves.
        Assert.Equal("src/A/A.csproj", action.Project);
        Assert.Equal("2.0.0", action.Value);
    }

    [Fact]
    public void SkippedActionIsNotPlanned()
    {
        Write("src/Lib/Lib.csproj", Csproj("net10.0", properties: "", items: Analyzer));

        var plan = Plan(["SetProperty:src/Lib/Lib.csproj:Nullable"]);

        Assert.DoesNotContain(plan.Actions, a => a.Kind == FixKind.SetProperty);
    }

    [Fact]
    public void DriftAfterPlanIsRefusedAndNothingIsWritten()
    {
        var path = Write("src/Lib/Lib.csproj", Csproj("net10.0", properties: "", items: Analyzer));
        var plan = Plan();
        File.WriteAllText(path, Csproj("net10.0", properties: "", items: Analyzer) + "\n<!-- edited -->\n");
        var edited = File.ReadAllText(path);

        var result = ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.Ok);
        Assert.Equal(edited, File.ReadAllText(path));
    }

    [Fact]
    public void BuildFailureRollsBackByteForByte()
    {
        var original = Csproj("net8.0", properties: "", items: Analyzer);
        var path = Write("src/Lib/Lib.csproj", original);
        var before = File.ReadAllBytes(path);
        var plan = Plan();

        var result = ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(1), CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.Ok);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SecondPlanAfterAFixIsEmpty()
    {
        Write("src/Lib/Lib.csproj", Csproj("net10.0", properties: "", items: Analyzer));
        var plan = Plan();
        Assert.True(ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult().Ok);

        Assert.Empty(Plan().Actions);
    }

    [Fact]
    public void TokenIsSingleUse()
    {
        Write("src/Lib/Lib.csproj", Csproj("net10.0", properties: "", items: Analyzer));
        var plan = Plan();
        _ = ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult();

        var again = ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(again.Ok);
    }

    [Fact]
    public void CrlfLineEndingsArePreserved()
    {
        var text = Csproj("net10.0", properties: "", items: Analyzer).Replace("\n", "\r\n");
        var path = Write("src/Lib/Lib.csproj", text);
        var plan = Plan();

        Assert.True(ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult().Ok);

        var written = File.ReadAllText(path);
        Assert.DoesNotContain("\r\r", written);
        Assert.Equal(written.Count(c => c == '\n'), written.Count(c => c == '\r'));
    }

    [Fact]
    public void StandardsFileBytesAreUnchanged()
    {
        Write("src/Lib/Lib.csproj", Csproj("net10.0", properties: "", items: Analyzer));
        var standards = ProjectStandardsFile.FilePath(_root);
        var before = File.ReadAllBytes(standards);
        var plan = Plan();

        _ = ProjectFixer.ConfirmAsync(_root, plan.Token!, Build(0), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(before, File.ReadAllBytes(standards));
    }
}
