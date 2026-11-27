using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class CultureSyncWriterTests : IDisposable
{
    private readonly ResxFixture _fx = new();
    private readonly PlanStore<SyncPlan> _store = new();

    public void Dispose() => _fx.Dispose();

    private static (string, string, string?) E(string n, string v, string? c = null) => (n, v, c);

    private CultureSyncWriter Writer() => new(_store);

    [Fact]
    public void PlanWritesNothing()
    {
        _fx.Resx("P", null, entries: [E("A", "One"), E("B", "Two")]);
        var de = _fx.Resx("P", "de", entries: [E("A", "Eins")]);
        var before = File.ReadAllBytes(de);

        var plan = Writer().Plan(_fx.Root, null, false);

        Assert.Single(plan.Changes);
        Assert.Equal(before, File.ReadAllBytes(de));
    }

    [Fact]
    public void ConfirmAddsStubsWithNeutralTextAndComment()
    {
        _fx.Resx("P", null, entries: [E("A", "One"), E("B", "Two")]);
        var de = _fx.Resx("P", "de", entries: [E("A", "Eins")]);
        var writer = Writer();

        var token = writer.Plan(_fx.Root, null, false).Token;
        var files = writer.Apply(token);

        var stub = ResxFile.Load(de).Entries.Single(e => e.Name == "B");
        Assert.Equal(("Two", "needs-translation"), (stub.Value, stub.Comment));
        Assert.Single(files);
    }

    [Fact]
    public void ConfirmCreatesNewCultureFile()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        var writer = Writer();

        writer.Apply(writer.Plan(_fx.Root, "fr", false).Token);

        var fr = Path.Combine(_fx.Root, "P", "Resources", "Strings.fr.resx");
        Assert.Equal("A", Assert.Single(ResxFile.Load(fr).Entries).Name);
    }

    [Fact]
    public void RemoveExtraOnlyWhenAsked()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);
        var de = _fx.Resx("P", "de", entries: [E("A", "Eins"), E("C", "Drei")]);

        Assert.Empty(Writer().Plan(_fx.Root, null, false).Changes);
        var writer = Writer();
        writer.Apply(writer.Plan(_fx.Root, null, true).Token);

        Assert.DoesNotContain(ResxFile.Load(de).Entries, e => e.Name == "C");
    }

    [Fact]
    public void InvalidCultureRefused()
    {
        _fx.Resx("P", null, entries: [E("A", "One")]);

        Assert.Throws<ArgumentException>(() => Writer().Plan(_fx.Root, "xx-bogus-yy-zz", false));
    }

    [Fact]
    public void TokenIsSingleUseAndExpires()
    {
        var now = DateTime.UtcNow;
        var store = new PlanStore<SyncPlan>(() => now);
        _fx.Resx("P", null, entries: [E("A", "One")]);
        var writer = new CultureSyncWriter(store);
        var token = writer.Plan(_fx.Root, "fr", false).Token;

        writer.Apply(token);
        Assert.Throws<InvalidOperationException>(() => writer.Apply(token));

        var second = writer.Plan(_fx.Root, "de", false).Token;
        now = now.AddMinutes(16);
        Assert.Throws<InvalidOperationException>(() => writer.Apply(second));
    }

    [Fact]
    public void ConfirmRefusesWhenFileChangedSincePlan()
    {
        _fx.Resx("P", null, entries: [E("A", "One"), E("B", "Two")]);
        var de = _fx.Resx("P", "de", entries: [E("A", "Eins")]);
        var writer = Writer();
        var token = writer.Plan(_fx.Root, null, false).Token;

        File.AppendAllText(de, "\n");

        Assert.Throws<InvalidOperationException>(() => writer.Apply(token));
    }

    [Fact]
    public void SecondSyncAfterConfirmPlansNothing()
    {
        _fx.Resx("P", null, entries: [E("A", "One"), E("B", "Two")]);
        _fx.Resx("P", "de", entries: [E("A", "Eins")]);
        var writer = Writer();
        writer.Apply(writer.Plan(_fx.Root, null, false).Token);

        Assert.Empty(writer.Plan(_fx.Root, null, false).Changes);
    }
}
