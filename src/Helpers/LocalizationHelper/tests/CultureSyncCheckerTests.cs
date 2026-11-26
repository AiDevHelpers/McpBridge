using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class CultureSyncCheckerTests : IDisposable
{
    private readonly ResxFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static (string, string, string?) E(string n, string v, string? c = null) => (n, v, c);

    private IReadOnlyList<SyncFinding> Check() => CultureSyncChecker.Check(_fx.Root);

    [Fact]
    public void ReportsMissingKey()
    {
        _fx.Resx("P", null, entries: [E("A", "One"), E("B", "Two")]);
        _fx.Resx("P", "de", entries: [E("A", "Eins")]);

        var f = Assert.Single(Check());

        Assert.Equal(("missing", "B", "de"), (f.Kind, f.Key, f.Culture));
    }

    [Fact]
    public void ReportsExtraKey()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "de", entries: [E("A", "Eins"), E("C", "Drei")]);

        var f = Assert.Single(Check());

        Assert.Equal(("extra", "C"), (f.Kind, f.Key));
    }

    [Fact]
    public void ReportsUntranslatedWhenValueEqualsNeutral()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "de", entries: [E("A", "One")]);

        Assert.Equal("untranslated", Assert.Single(Check()).Kind);
    }

    [Fact]
    public void ReportsUntranslatedWhenCommentIsNeedsTranslation()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "de", entries: [E("A", "Eins", "needs-translation")]);

        Assert.Equal("untranslated", Assert.Single(Check()).Kind);
    }

    [Fact]
    public void SameInCultureCommentSuppressesUntranslated()
    {
        _fx.Resx("P", null, entries: [E("A", "OK")]);
        _fx.Resx("P", "de", entries: [E("A", "OK", "same-in-culture")]);

        Assert.Empty(Check());
    }

    [Fact]
    public void ReportsPlaceholderMismatch()
    {
        _fx.Resx("P", null, entries: [E("A", "Hi {0}")]);
        _fx.Resx("P", "de", entries: [E("A", "Hallo")]);

        Assert.Equal("placeholder-mismatch", Assert.Single(Check()).Kind);
    }

    [Fact]
    public void InvalidCultureFileNameIsReported()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "xx-bogus-yy-zz", entries: [E("A", "Eins")]);

        Assert.Equal("invalid-culture", Assert.Single(Check()).Kind);
    }

    [Fact]
    public void PseudoCultureFileIsSkipped()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "qps-ploc", entries: [E("A", "[One]")]);

        Assert.Empty(Check());
    }

    [Fact]
    public void ReportsNothingWhenInSync()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "de", entries: [E("A", "Eins")]);

        Assert.Empty(Check());
    }

    [Fact]
    public void ResxCheckerIgnoresCultureFiles()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        _fx.Resx("P", "de", entries: [E("A", "Eins"), E("Zed", "Z")]);
        _fx.Cs("P", "X.cs", "var a = Strings.Get(\"A\");\n");

        Assert.Empty(ResxChecker.Check(_fx.Root));
    }
}
