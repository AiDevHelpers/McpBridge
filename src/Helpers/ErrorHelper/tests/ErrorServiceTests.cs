using AiDevHelpers.Helpers.ErrorHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ErrorHelper.Tests;

public sealed class ErrorServiceTests
{
    private const string Build1 =
        "F:\\a.cs(1,1): error CS0103: The name 'x' does not exist [F:\\App.csproj]\n" +
        "F:\\b.cs(2,1): warning CA1822: Mark members as static [F:\\App.csproj]\n" +
        "info NETSDK1057: preview version [F:\\App.csproj]\n";

    [Fact]
    public void GetErrorsWarningsNoticesSplitBySeverity()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);
        svc.Ingest(Build1);

        Assert.Single(svc.GetErrors());
        Assert.Single(svc.GetWarnings());
        Assert.Single(svc.GetNotices());
    }

    [Fact]
    public void FilterByCodeReturnsMatchingOnly()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);
        svc.Ingest(Build1);

        var result = svc.GetWarnings(new DiagnosticFilter(Codes: ["ca1822"]));

        Assert.Single(result);
        Assert.Equal("CA1822", result[0].Code);
    }

    [Fact]
    public void FilterBySourceReturnsMatchingOnly()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);
        svc.Ingest(Build1);

        var result = svc.GetErrors(new DiagnosticFilter(Sources: [DiagnosticSource.Compiler]));

        Assert.Single(result);
        Assert.Equal("CS0103", result[0].Code);
        Assert.Empty(svc.GetErrors(new DiagnosticFilter(Sources: [DiagnosticSource.NuGet])));
    }

    [Fact]
    public void SuppressedCodesAndSourcesAreHiddenFromEveryGetter()
    {
        var suppression = new SuppressionSettings(
            Codes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CS0103" },
            Sources: new HashSet<DiagnosticSource> { DiagnosticSource.CodeAnalysis });
        var svc = new ErrorService(suppression);
        svc.Ingest(Build1);

        Assert.Empty(svc.GetErrors());
        Assert.Empty(svc.GetWarnings());
        Assert.Single(svc.GetNotices());
    }

    [Fact]
    public void ChangesReturnOnlyDiagnosticsNewSinceSnapshot()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);
        svc.Ingest(Build1);
        var snapshot = svc.TakeSnapshot();

        svc.Ingest(Build1 + "F:\\c.cs(9,9): error CS1002: ; expected [F:\\App.csproj]\n");

        var added = Assert.Single(svc.GetChanges(snapshot));
        Assert.Equal("CS1002", added.Code);
    }

    [Fact]
    public void TakeSnapshotReturnsIdOnly()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);

        var id = svc.TakeSnapshot();

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Empty(svc.GetChanges(id));
    }

    [Fact]
    public void GetChangesUnknownSnapshotThrows()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);

        Assert.Throws<ArgumentException>(() => svc.GetChanges("missing"));
    }

    [Fact]
    public void IngestReplacesPreviousOutput()
    {
        var svc = new ErrorService(SuppressionSettings.Empty);
        svc.Ingest(Build1);

        svc.Ingest("Build succeeded.\n");

        Assert.Empty(svc.GetErrors());
        Assert.Empty(svc.GetWarnings());
    }
}
