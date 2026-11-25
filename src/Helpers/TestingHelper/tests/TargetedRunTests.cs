using AiDevHelpers.Helpers.TestingHelper;
using Xunit;

namespace AiDevHelpers.Helpers.TestingHelper.Tests;

public sealed class TrxReaderTests
{
    private const string Trx =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">" +
        "<Results>" +
        "<UnitTestResult testId=\"a1\" testName=\"Adds\" outcome=\"Passed\" duration=\"00:00:00.0120000\" />" +
        "<UnitTestResult testId=\"b2\" testName=\"Subtracts\" outcome=\"Failed\" duration=\"00:00:00.5\"><Output><ErrorInfo><Message>Assert.Equal failed</Message></ErrorInfo></Output></UnitTestResult>" +
        "<UnitTestResult testId=\"c3\" testName=\"Skipped\" outcome=\"NotExecuted\" duration=\"00:00:00\" />" +
        "</Results>" +
        "<TestDefinitions>" +
        "<UnitTest id=\"a1\"><TestMethod className=\"Ns.CalcTests\" name=\"Adds\" /></UnitTest>" +
        "<UnitTest id=\"b2\"><TestMethod className=\"Ns.CalcTests\" name=\"Subtracts\" /></UnitTest>" +
        "</TestDefinitions></TestRun>";

    [Fact]
    public void ReadsEachResultWithItsFullNameOutcomeAndMessage()
    {
        var tests = TrxReader.ReadTests(Trx);

        Assert.Equal(3, tests.Count);
        Assert.Equal("Ns.CalcTests.Adds", tests[0].Name);
        Assert.Equal("Passed", tests[0].Outcome);
        Assert.Equal(12, tests[0].DurationMs);
        Assert.Equal("Failed", tests[1].Outcome);
        Assert.Equal("Assert.Equal failed", tests[1].Message);
        Assert.Equal("Skipped", tests[2].Name);
        Assert.Equal("NotExecuted", tests[2].Outcome);
    }
}

public sealed class MethodCoverageReaderTests
{
    private const string Report =
        "<coverage line-rate=\"0.5\"><sources><source>C:\\repo\\</source></sources><packages><package><classes>" +
        "<class filename=\"src\\Calc.cs\"><lines>" +
        "<line number=\"10\" hits=\"3\"/><line number=\"11\" hits=\"0\"/><line number=\"12\" hits=\"1\"/><line number=\"20\" hits=\"0\"/>" +
        "</lines></class>" +
        "<class filename=\"src\\Calc.cs\"><lines><line number=\"11\" hits=\"2\"/></lines></class>" +
        "<class filename=\"src\\Other.cs\"><lines><line number=\"10\" hits=\"0\"/></lines></class>" +
        "</classes></package></packages></coverage>";

    [Fact]
    public void CountsOnlyTheLinesOfTheMethodInItsFile()
    {
        var coverage = MethodCoverageReader.ForLines(Report, @"C:\repo\src\Calc.cs", 10, 12);

        Assert.NotNull(coverage);
        Assert.Equal(3, coverage!.LinesTotal);
        Assert.Equal(3, coverage.LinesCovered);
        Assert.Equal(1.0, coverage.Rate);
        Assert.Empty(coverage.UncoveredLines);
    }

    [Fact]
    public void ListsTheLinesThatNeverRan()
    {
        var coverage = MethodCoverageReader.ForLines(Report, @"C:\repo\src\Calc.cs", 10, 25);

        Assert.Equal(4, coverage!.LinesTotal);
        Assert.Equal(3, coverage.LinesCovered);
        Assert.Equal([20], coverage.UncoveredLines);
    }

    [Fact]
    public void AMethodWithNoInstrumentedLinesHasNoCoverage()
    {
        Assert.Null(MethodCoverageReader.ForLines(Report, @"C:\repo\src\Calc.cs", 30, 40));
        Assert.Null(MethodCoverageReader.ForLines(Report, @"C:\repo\src\Missing.cs", 10, 12));
    }
}

public sealed class TargetedRunArgsTests
{
    [Fact]
    public void FiltersTheRunToTheNamedTests()
    {
        var args = TargetedRunArgs.Test(@"C:\r\T.csproj", @"C:\r\out", ["Ns.A.One", "Ns.A.Two"], coverlet: false);

        Assert.Equal(["test", @"C:\r\T.csproj", "--logger", "trx", "--results-directory", @"C:\r\out", "--filter", "FullyQualifiedName=Ns.A.One|FullyQualifiedName=Ns.A.Two"], args);
    }

    [Fact]
    public void NoNamesRunsTheWholeProjectAndCoverletAddsTheCollector()
    {
        var args = TargetedRunArgs.Test("T.csproj", "out", [], coverlet: true);

        Assert.DoesNotContain("--filter", args);
        Assert.Equal(["--collect", "XPlat Code Coverage"], args.TakeLast(2));
    }

    [Fact]
    public void DotnetCoverageWrapsTheSameRunAndQuotesWhatNeedsIt()
    {
        var test = TargetedRunArgs.Test("T.csproj", "out", ["Ns.A.One", "Ns.A.Two"], coverlet: false);

        var args = TargetedRunArgs.DotnetCoverage(test, "cov.xml");

        Assert.Equal("collect", args[0]);
        Assert.Contains("--filter \"FullyQualifiedName=Ns.A.One|FullyQualifiedName=Ns.A.Two\"", args[1]);
        Assert.Equal(["-f", "cobertura", "-o", "cov.xml"], args.Skip(2));
    }
}
