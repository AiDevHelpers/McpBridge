using AiDevHelpers.Helpers.PackageHelper;
using Xunit;

namespace AiDevHelpers.Helpers.PackageHelper.Tests;

public sealed class PackageUpdateRuleTests
{
    [Theory]
    [InlineData("net10.0", 10)]
    [InlineData("net10.0-windows", 10)]
    [InlineData("net8.0", 8)]
    public void MaxMajorReadsTheDotNetMajor(string framework, int expected) =>
        Assert.Equal(expected, PackageUpdateRule.MaxMajor([framework]));

    [Fact]
    public void MaxMajorIsTheHighestAmongTheFrameworks() =>
        Assert.Equal(10, PackageUpdateRule.MaxMajor(["net48", "net8.0", "net10.0-windows"]));

    [Theory]
    [InlineData("net48")]
    [InlineData("netstandard2.0")]
    [InlineData("netcoreapp3.1")]
    public void MaxMajorIsNullWithoutADotNetFiveOrLaterFramework(string framework) =>
        Assert.Null(PackageUpdateRule.MaxMajor([framework]));

    [Fact]
    public void NewestAllowedPicksTheHighestVersionAtOrBelowTheCapNumerically()
    {
        var picked = PackageUpdateRule.NewestAllowed(["9.0.0", "10.0.9", "10.0.10", "11.0.0", "11.0.1-preview.1", "10.1.0-rc.1"], 10);

        Assert.Equal("10.0.10", picked);
    }

    [Fact]
    public void NewestAllowedIsNullWhenEverythingIsAboveTheCap() =>
        Assert.Null(PackageUpdateRule.NewestAllowed(["11.0.0", "12.0.0"], 10));

    [Theory]
    [InlineData("System.Text.Json", true)]
    [InlineData("Microsoft.Extensions.Options", true)]
    [InlineData("Microsoft.AspNetCore.Http", true)]
    [InlineData("xunit.runner.visualstudio", false)]
    [InlineData("Microsoft.NET.Test.Sdk", false)]
    [InlineData("ModelContextProtocol", false)]
    public void OnlyPackagesThatVersionWithDotNetAreCapped(string package, bool expected) =>
        Assert.Equal(expected, PackageUpdateRule.VersionsWithDotNet(package));
}

public sealed class OutdatedPlannerTests
{
    private static string ListJson(string project, string framework, params (string Id, string Current, string Latest)[] packages) =>
        "{\"version\":1,\"problems\":[{\"project\":\"x.esproj\",\"level\":\"error\",\"text\":\"not a PackageReference project\"}],\"projects\":[{\"path\":\"" + project +
        "\",\"frameworks\":[{\"framework\":\"" + framework + "\",\"topLevelPackages\":[" +
        string.Join(",", packages.Select(p => $"{{\"id\":\"{p.Id}\",\"requestedVersion\":\"{p.Current}\",\"resolvedVersion\":\"{p.Current}\",\"latestVersion\":\"{p.Latest}\"}}")) +
        "]}]}]}";

    private static string SearchJson(params string[] versions) =>
        "{\"version\":2,\"problems\":[],\"searchResult\":[{\"sourceName\":\"nuget.org\",\"packages\":[" +
        string.Join(",", versions.Select(v => $"{{\"id\":\"p\",\"version\":\"{v}\"}}")) + "]}]}";

    private sealed class FakePackages(string list, string? search = null, int listExit = 0) : IPackageRunner
    {
        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(string.Join(" ", arguments));
            return Task.FromResult(arguments[0] == "list" ? (listExit, list) : (0, search ?? SearchJson()));
        }
    }

    [Fact]
    public async Task ADotNetPackageAboveTheFrameworkMajorGetsTheNewestAllowedVersion()
    {
        var runner = new FakePackages(ListJson("a/Bridge.csproj", "net10.0-windows", ("System.Text.Json", "9.0.0", "11.0.0")), SearchJson("9.0.0", "10.0.3", "11.0.0"));

        var updates = await new OutdatedPlanner(runner).PlanAsync("root", "App.slnx");

        var update = Assert.Single(updates);
        Assert.Equal("a/Bridge.csproj", update.Project);
        Assert.Equal("System.Text.Json", update.Package);
        Assert.Equal("9.0.0", update.Current);
        Assert.Equal("11.0.0", update.Latest);
        Assert.Equal("10.0.3", update.Chosen);
        Assert.Contains("11", update.Note);
        Assert.Equal("list App.slnx package --outdated --format json", runner.Calls[0]);
        Assert.Equal("package search System.Text.Json --exact-match --format json", runner.Calls[1]);
    }

    [Fact]
    public async Task APackageThatDoesNotVersionWithDotNetTakesTheLatestWithoutASearch()
    {
        var runner = new FakePackages(ListJson("a/T.csproj", "net10.0", ("xunit.runner.visualstudio", "3.1.5", "4.0.1")));

        var update = Assert.Single(await new OutdatedPlanner(runner).PlanAsync("root", "App.slnx"));

        Assert.Equal("4.0.1", update.Chosen);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task ADotNetPackageWithinTheCapTakesTheLatest()
    {
        var runner = new FakePackages(ListJson("a/C.csproj", "net10.0", ("Microsoft.Extensions.Options", "10.0.1", "10.0.9")));

        var update = Assert.Single(await new OutdatedPlanner(runner).PlanAsync("root", "App.slnx"));

        Assert.Equal("10.0.9", update.Chosen);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task AProjectWithoutADotNetFrameworkHasNoCap()
    {
        var runner = new FakePackages(ListJson("a/F.csproj", "net48", ("System.Text.Json", "9.0.0", "11.0.0")));

        var update = Assert.Single(await new OutdatedPlanner(runner).PlanAsync("root", "App.slnx"));

        Assert.Equal("11.0.0", update.Chosen);
    }

    [Fact]
    public async Task NothingNewerThanCurrentWithinTheCapMeansNoUpdate()
    {
        var runner = new FakePackages(ListJson("a/Bridge.csproj", "net10.0", ("System.Text.Json", "10.0.3", "11.0.0")), SearchJson("10.0.3", "11.0.0"));

        var update = Assert.Single(await new OutdatedPlanner(runner).PlanAsync("root", "App.slnx"));

        Assert.Null(update.Chosen);
        Assert.NotEmpty(update.Note);
    }

    [Fact]
    public async Task ListFailureThrowsWithTheOutput()
    {
        var runner = new FakePackages("restore needed", listExit: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new OutdatedPlanner(runner).PlanAsync("root", "App.slnx"));

        Assert.Contains("restore needed", ex.Message);
    }
}
