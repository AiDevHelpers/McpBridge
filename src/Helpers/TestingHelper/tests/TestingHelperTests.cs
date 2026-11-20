using AiDevHelpers.Helpers.TestingHelper;
using Xunit;

namespace AiDevHelpers.Helpers.TestingHelper.Tests;

public sealed class HotReloadPolicyTests
{
    [Fact]
    public void IdeSettingOnWinsEvenWhenDotnetWatchRuns()
    {
        var state = HotReloadPolicy.Resolve(ideEnabled: true, dotnetWatchRunning: true);

        Assert.Equal(HotReloadSource.Ide, state.Source);
        Assert.True(state.Active);
    }

    [Fact]
    public void IdeSettingOffFallsBackToDotnetWatch()
    {
        var state = HotReloadPolicy.Resolve(ideEnabled: false, dotnetWatchRunning: true);

        Assert.Equal(HotReloadSource.DotnetWatch, state.Source);
        Assert.True(state.Active);
    }

    [Fact]
    public void UnknownIdeSettingFallsBackToDotnetWatch()
    {
        var state = HotReloadPolicy.Resolve(ideEnabled: null, dotnetWatchRunning: true);

        Assert.Equal(HotReloadSource.DotnetWatch, state.Source);
    }

    [Fact]
    public void NothingEnabledIsInactive()
    {
        var state = HotReloadPolicy.Resolve(ideEnabled: false, dotnetWatchRunning: false);

        Assert.Equal(HotReloadSource.None, state.Source);
        Assert.False(state.Active);
    }
}

public sealed class TrxFailuresTests
{
    private const string Trx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Adds_Two" outcome="Passed" />
            <UnitTestResult testName="Adds_Negative" outcome="Failed">
              <Output><ErrorInfo><Message>Assert.Equal() Failure: expected 3 actual 4</Message></ErrorInfo></Output>
            </UnitTestResult>
            <UnitTestResult testName="Skips_Me" outcome="NotExecuted" />
          </Results>
        </TestRun>
        """;

    [Fact]
    public void ReturnsOnlyFailedTestsWithMessage()
    {
        var failures = TrxFailures.Parse(Trx, "run.trx");

        var f = Assert.Single(failures);
        Assert.Equal("Adds_Negative", f.TestName);
        Assert.Contains("expected 3 actual 4", f.Message);
        Assert.Equal("run.trx", f.Trx);
    }
}

public sealed class TestFailureQueueTests
{
    private static TestFailure F(string name) => new(name, "msg", "run.trx", DateTimeOffset.UtcNow);

    [Fact]
    public void DrainReturnsQueuedFailuresOnceThenEmpty()
    {
        var queue = new TestFailureQueue();
        queue.Add([F("a"), F("b")]);

        Assert.Equal(2, queue.Drain().Count);
        Assert.Empty(queue.Drain());
    }
}

public sealed class TestMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "testing-monitor-tests", Guid.NewGuid().ToString("N"));

    public TestMonitorTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private const string FailingTrx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Breaks" outcome="Failed">
              <Output><ErrorInfo><Message>boom</Message></ErrorInfo></Output>
            </UnitTestResult>
          </Results>
        </TestRun>
        """;

    [Fact]
    public void PollQueuesFailuresFromNewTrxOnce()
    {
        var file = Path.Combine(_dir, "run1.trx");
        File.WriteAllText(file, FailingTrx);
        var queue = new TestFailureQueue();
        var monitor = new TestMonitor(queue);

        var first = monitor.Poll(_dir);
        var second = monitor.Poll(_dir);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(queue.Drain());
    }

    [Fact]
    public void PollWithNoTrxReturnsEmpty()
    {
        var monitor = new TestMonitor(new TestFailureQueue());

        Assert.Empty(monitor.Poll(_dir));
    }
}

public sealed class CoverageTests
{
    [Fact]
    public void ProviderIsCoverletWhenRepoReferencesIt()
    {
        var provider = CoverageProviderSelector.Select(["<PackageReference Include=\"coverlet.collector\" Version=\"6.0.0\" />"]);

        Assert.Equal(CoverageProvider.Coverlet, provider);
    }

    [Fact]
    public void ProviderIsVisualStudioWhenCoverletAbsent()
    {
        var provider = CoverageProviderSelector.Select(["<PackageReference Include=\"xunit\" Version=\"2.9.3\" />"]);

        Assert.Equal(CoverageProvider.VisualStudio, provider);
    }

    [Fact]
    public void CoverletArgsUseCollectorAndResultsDirectory()
    {
        var args = CoverageArgs.Coverlet(@"C:\r\App.sln", @"C:\r\TestResults");

        Assert.Equal(new[] { "test", @"C:\r\App.sln", "--collect", "XPlat Code Coverage", "--results-directory", @"C:\r\TestResults" }, args);
    }

    [Fact]
    public void DotnetCoverageArgsWrapDotnetTestAsCobertura()
    {
        var args = CoverageArgs.DotnetCoverage(@"C:\r\App.sln", @"C:\r\TestResults\coverage.cobertura.xml");

        Assert.Equal(new[] { "collect", "dotnet test \"C:\\r\\App.sln\"", "-f", "cobertura", "-o", @"C:\r\TestResults\coverage.cobertura.xml" }, args);
    }

    [Fact]
    public void CoberturaLineRateIsReadFromRoot()
    {
        const string xml = "<coverage line-rate=\"0.82\" branch-rate=\"0.5\" version=\"1.9\" />";

        Assert.Equal(0.82, CoverageSummary.LineRate(xml)!.Value, 3);
    }
}

public sealed class CoverageServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "testing-coverage-tests", Guid.NewGuid().ToString("N"));

    public CoverageServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task VisualStudioProviderFallsBackToCliWhenBridgeUnavailable()
    {
        var runner = new FakeTestRunner
        {
            Handler = (file, args) => file == "dotnet-coverage" && args[0] == "--version"
                ? new TestCommandResult(0, "18.0.0", string.Empty)
                : new TestCommandResult(0, string.Empty, string.Empty),
        };
        var service = new CoverageService(runner, _dir);

        var result = await service.RunAsync(Path.Combine(_dir, "App.sln"), CoverageProvider.VisualStudio, bridgeAvailable: false);

        Assert.True(result.Ok);
        Assert.Equal("dotnet-coverage", result.Tool);
        Assert.Contains(runner.Calls, c => c.StartsWith("dotnet-coverage collect"));
    }

    [Fact]
    public async Task MissingCliGivesClearErrorNamingTool()
    {
        var runner = new FakeTestRunner
        {
            Handler = (_, _) => new TestCommandResult(1, string.Empty, "not found"),
        };
        var service = new CoverageService(runner, _dir);

        var result = await service.RunAsync(Path.Combine(_dir, "App.sln"), CoverageProvider.VisualStudio, bridgeAvailable: false);

        Assert.False(result.Ok);
        Assert.Contains("dotnet-coverage", result.Error);
    }

    [Fact]
    public async Task CoverletProviderRunsDotnetTestWithCollector()
    {
        var runner = new FakeTestRunner();
        var service = new CoverageService(runner, _dir);

        var result = await service.RunAsync(Path.Combine(_dir, "App.sln"), CoverageProvider.Coverlet, bridgeAvailable: false);

        Assert.True(result.Ok);
        Assert.Equal("coverlet", result.Tool);
        Assert.Contains(runner.Calls, c => c.StartsWith("dotnet test") && c.Contains("XPlat Code Coverage"));
    }
}
