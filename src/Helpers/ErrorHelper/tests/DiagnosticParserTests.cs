using AiDevHelpers.Helpers.ErrorHelper;
using AiDevHelpers.Helpers.LogHelper.Models;
using Xunit;

namespace AiDevHelpers.Helpers.ErrorHelper.Tests;

public sealed class DiagnosticParserTests
{
    [Fact]
    public void ParsesCompilerErrorWithLocationAndProject()
    {
        const string output = @"F:\src\App\Program.cs(12,5): error CS1002: ; expected [F:\src\App\App.csproj]";

        var d = Assert.Single(DiagnosticParser.Parse(output));

        Assert.Equal(LogSeverity.Error, d.Severity);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal(DiagnosticSource.Compiler, d.Source);
        Assert.Equal(@"F:\src\App\Program.cs", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(5, d.Column);
        Assert.Equal(@"F:\src\App\App.csproj", d.Project);
        Assert.Equal("; expected", d.Message);
    }

    [Fact]
    public void ParsesRestoreErrorAsNuGetWithoutFile()
    {
        const string output = @"error NU1101: Unable to find package Foo. [F:\src\App\App.csproj]";

        var d = Assert.Single(DiagnosticParser.Parse(output));

        Assert.Equal(LogSeverity.Error, d.Severity);
        Assert.Equal(DiagnosticSource.NuGet, d.Source);
        Assert.Null(d.File);
    }

    [Fact]
    public void ParsesAnalyzerWarningAsCodeAnalysis()
    {
        const string output = @"F:\src\App\Util.cs(3,1): warning CA1822: Mark members as static [F:\src\App\App.csproj]";

        var d = Assert.Single(DiagnosticParser.Parse(output));

        Assert.Equal(LogSeverity.Warning, d.Severity);
        Assert.Equal(DiagnosticSource.CodeAnalysis, d.Source);
    }

    [Fact]
    public void ParsesSpaceColonFormWithNuGetWarning()
    {
        const string output = @"F:\src\App\App.csproj : warning NU1603: Foo depends on Bar [F:\src\App\App.csproj]";

        var d = Assert.Single(DiagnosticParser.Parse(output));

        Assert.Equal(LogSeverity.Warning, d.Severity);
        Assert.Equal(DiagnosticSource.NuGet, d.Source);
        Assert.Equal(@"F:\src\App\App.csproj", d.File);
    }

    [Fact]
    public void ParsesInfoLineAsNotice()
    {
        const string output = "info NETSDK1057: You are using a preview version of .NET.";

        var d = Assert.Single(DiagnosticParser.Parse(output));

        Assert.Equal(LogSeverity.Information, d.Severity);
        Assert.Equal("NETSDK1057", d.Code);
    }

    [Fact]
    public void IgnoresSummaryLines()
    {
        const string output = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n";

        Assert.Empty(DiagnosticParser.Parse(output));
    }
}
