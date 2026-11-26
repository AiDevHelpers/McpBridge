using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class WorkloadParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesInstalledWorkloadsFromTheTable()
    {
        var result = DotnetWorkloadParser.ParseInstalled(Fixture("workload-list.txt"));

        Assert.True(result.Ok);
        Assert.Equal(7, result.Items.Count);
        var android = result.Items.Single(w => w.Id == "android");
        Assert.Equal("37.2.31/11.0.100", android.ManifestVersion);
        Assert.Contains("VS 18.12.12412.261", android.Source);
    }

    [Fact]
    public void NoInstalledWorkloadsIsOkAndEmpty()
    {
        var text = "Workload version: 11.0.100\n\nInstalled Workload Id      Manifest Version         Installation Source\n----------------------------------------------------------------\n";

        var result = DotnetWorkloadParser.ParseInstalled(text);

        Assert.True(result.Ok);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void MissingInstalledHeaderIsAnError()
    {
        var result = DotnetWorkloadParser.ParseInstalled("something else entirely\n");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParsesSearchResults()
    {
        var result = DotnetWorkloadParser.ParseSearch(Fixture("workload-search.txt"));

        Assert.True(result.Ok);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal("maui", result.Items.Last().Id);
        Assert.Contains("MAUI", result.Items.Last().Description);
    }

    [Fact]
    public void ParsesVswhereInstances()
    {
        var result = VswhereParser.Parse(Fixture("vswhere.json"));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Items.Count);
        var community = result.Items.Single(i => i.ProductId.EndsWith("Community"));
        Assert.True(community.IsComplete);
        Assert.Equal("18.12.12412.261", community.Version);
        Assert.False(result.Items.Single(i => i.ProductId.EndsWith("Enterprise")).IsComplete);
    }

    [Fact]
    public void VswhereBadJsonIsAnError()
    {
        var result = VswhereParser.Parse("{ not json");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void MissingWorkloadsAreReported()
    {
        var missing = WorkloadCheck.Missing(["android", "maui"], ["android", "ios"]);

        Assert.Equal(["maui"], missing);
    }

    [Fact]
    public void ProjectTypeMapsToItsWorkload()
    {
        Assert.Equal(["maui"], WorkloadCheck.ForProjectType("maui"));
        Assert.Empty(WorkloadCheck.ForProjectType("console"));
    }
}
