using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class ErrorToolsTests
{
    private const string Build =
        "F:\\a.cs(1,1): error CS0103: The name 'x' does not exist [F:\\App.csproj]\n" +
        "F:\\b.cs(2,1): warning CA1822: Mark members as static [F:\\App.csproj]\n";

    // A unique solution path per test keeps the static service registry isolated.
    private static string Sln() => $"C:\\__errortools_{Guid.NewGuid():N}\\App.sln";

    [Fact]
    public void ErrorList_AfterIngest_ReturnsErrorsOnly()
    {
        var sln = Sln();
        ErrorTools.ErrorIngest(Build, sln);

        using var doc = JsonDocument.Parse(ErrorTools.ErrorList("errors", solutionPath: sln));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("CS0103", doc.RootElement.GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void ErrorList_UnknownKind_ReportsError()
    {
        using var doc = JsonDocument.Parse(ErrorTools.ErrorList("bogus", solutionPath: Sln()));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ErrorList_UnknownSource_ReportsError()
    {
        using var doc = JsonDocument.Parse(ErrorTools.ErrorList("errors", sources: "Nope", solutionPath: Sln()));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ErrorChanges_ReturnsOnlyNewDiagnostics()
    {
        var sln = Sln();
        ErrorTools.ErrorIngest(Build, sln);
        using var snap = JsonDocument.Parse(ErrorTools.ErrorSnapshot(sln));
        var id = snap.RootElement.GetProperty("snapshotId").GetString()!;

        ErrorTools.ErrorIngest(Build + "F:\\c.cs(9,9): error CS1002: ; expected [F:\\App.csproj]\n", sln);

        using var doc = JsonDocument.Parse(ErrorTools.ErrorChanges(id, solutionPath: sln));
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("CS1002", doc.RootElement.GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void ErrorChanges_UnknownSnapshot_ReportsError()
    {
        using var doc = JsonDocument.Parse(ErrorTools.ErrorChanges("missing", solutionPath: Sln()));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }
}
