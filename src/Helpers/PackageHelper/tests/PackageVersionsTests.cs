using AiDevHelpers.Helpers.PackageHelper;
using Xunit;

namespace AiDevHelpers.Helpers.PackageHelper.Tests;

public sealed class PackageVersionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkgver-" + Guid.NewGuid().ToString("N"));

    public PackageVersionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Project(string name, string frameworks, string items)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFrameworks>" + frameworks + "</TargetFrameworks></PropertyGroup>\n  <ItemGroup>\n"
            + items + "\n  </ItemGroup>\n</Project>\n");
    }

    private VersionChoice ChooseFor(string package) =>
        PackageVersions.Choose(PackageVersions.ReadAll(_root).Where(u => u.Package == package).ToList());

    [Fact]
    public void SingleVersionIsChosen()
    {
        Project("A", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"1.2.3\" />");

        Assert.Equal("1.2.3", ChooseFor("Foo").Version);
    }

    [Fact]
    public void MajorityWins()
    {
        Project("A", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"1.0.0\" />");
        Project("B", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"1.0.0\" />");
        Project("C", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"2.0.0\" />");

        Assert.Equal("1.0.0", ChooseFor("Foo").Version);
    }

    [Fact]
    public void TieGoesToNewestAllowedForTheFrameworks()
    {
        Project("A", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"8.0.0\" />");
        Project("B", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"10.0.1\" />");

        Assert.Equal("10.0.1", ChooseFor("Foo").Version);
    }

    [Fact]
    public void TieRespectsTheDotNetMajorOfTheFrameworks()
    {
        Project("A", "net8.0", "    <PackageReference Include=\"System.Text.Json\" Version=\"8.0.5\" />");
        Project("B", "net8.0", "    <PackageReference Include=\"System.Text.Json\" Version=\"10.0.1\" />");

        Assert.Equal("8.0.5", ChooseFor("System.Text.Json").Version);
    }

    [Fact]
    public void UpdateItemsAreRead()
    {
        Project("A", "net10.0", "    <PackageReference Update=\"Foo\" Version=\"3.1.0\" />");

        Assert.Single(PackageVersions.ReadAll(_root), u => u.Package == "Foo" && u.Version == "3.1.0");
    }

    [Fact]
    public void FloatingVersionIsReportedNotChosen()
    {
        Project("A", "net10.0", "    <PackageReference Include=\"Foo\" Version=\"1.*\" />");

        var choice = ChooseFor("Foo");

        Assert.Null(choice.Version);
        Assert.Contains("floating", choice.Reason);
    }

    [Fact]
    public void MissingVersionIsReportedNotGuessed()
    {
        Project("A", "net10.0", "    <PackageReference Include=\"Foo\" />");

        var choice = ChooseFor("Foo");

        Assert.Null(choice.Version);
        Assert.Contains("no version", choice.Reason);
    }
}
