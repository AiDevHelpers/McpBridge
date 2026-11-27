using System.Xml.Linq;
using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class PseudoLocalizerTests
{
    [Fact]
    public void PreservesNumberedPlaceholders() =>
        Assert.Contains("{0}", PseudoLocalizer.Transform("Copy {0} now"));

    [Fact]
    public void PreservesNamedPlaceholdersAndFormatSpecs()
    {
        var result = PseudoLocalizer.Transform("Total {name} is {1:N2} units");

        Assert.Contains("{name}", result);
        Assert.Contains("{1:N2}", result);
    }

    [Fact]
    public void PreservesEscapedBraces() =>
        Assert.Contains("{{", PseudoLocalizer.Transform("Use {{braces}} here"));

    [Fact]
    public void GrowsTextByAboutThirtyPercent()
    {
        var source = "The quick brown fox jumps over the lazy";

        var ratio = (double)PseudoLocalizer.Transform(source).Length / source.Length;

        Assert.InRange(ratio, 1.25, 1.5);
    }

    [Fact]
    public void WrapsInBrackets()
    {
        var result = PseudoLocalizer.Transform("Hello");

        Assert.StartsWith("[", result);
        Assert.EndsWith("]", result);
    }

    [Fact]
    public void PlaceholderSetIsUnchanged()
    {
        var source = "A {0} and {name} and {2:N0} end";

        Assert.True(Placeholders.Of(source).SetEquals(Placeholders.Of(PseudoLocalizer.Transform(source))));
    }

    [Fact]
    public void LettersBecomeAccentedLookAlikes()
    {
        var result = PseudoLocalizer.Transform("Error");

        Assert.DoesNotContain("E", result.Replace("[", string.Empty));
        Assert.Contains("É", result);
    }

    [Fact]
    public void EmptyStaysEmpty() =>
        Assert.Equal(string.Empty, PseudoLocalizer.Transform(string.Empty));

    [Fact]
    public void IsDeterministic() =>
        Assert.Equal(PseudoLocalizer.Transform("Same input"), PseudoLocalizer.Transform("Same input"));
}

public sealed class PseudoWriterTests : IDisposable
{
    private readonly ResxFixture _fx = new();
    private readonly PlanStore<PseudoPlan> _store = new();

    public void Dispose() => _fx.Dispose();

    private static (string, string, string?) E(string n, string v, string? c = null) => (n, v, c);

    private string Neutral() => _fx.Resx("P", null, "\n", false, E("A", "Hello {0}"), E("B", "Goodbye"));

    private string DefaultOutput() => Path.Combine(_fx.Root, "P", "Resources", "Strings.qps-ploc.resx");

    [Fact]
    public void PlanWritesNothing()
    {
        var neutral = Neutral();

        var plan = new PseudoWriter(_store).Plan(_fx.Root, neutral, null);

        Assert.Equal(2, plan.Changes.Count);
        Assert.False(File.Exists(DefaultOutput()));
    }

    [Fact]
    public void ConfirmWritesAResxThatXDocumentLoads()
    {
        var neutral = Neutral();
        var writer = new PseudoWriter(_store);

        var file = writer.Apply(writer.Plan(_fx.Root, neutral, null).Token);

        Assert.Equal(Path.GetRelativePath(_fx.Root, DefaultOutput()), file);
        var data = XDocument.Load(DefaultOutput()).Root!.Elements("data").ToList();
        Assert.Equal(2, data.Count);
        Assert.Equal("pseudo-localized", (string?)data[0].Element("comment"));
    }

    [Fact]
    public void RunningTwiceGivesIdenticalBytes()
    {
        var neutral = Neutral();
        var writer = new PseudoWriter(_store);

        writer.Apply(writer.Plan(_fx.Root, neutral, null).Token);
        var first = File.ReadAllBytes(DefaultOutput());
        writer.Apply(writer.Plan(_fx.Root, neutral, null).Token);

        Assert.Equal(first, File.ReadAllBytes(DefaultOutput()));
    }

    [Fact]
    public void OutputOutsideRootRefused()
    {
        var neutral = Neutral();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.resx");

        Assert.Throws<ArgumentException>(() => new PseudoWriter(_store).Plan(_fx.Root, neutral, outside));
    }

    [Fact]
    public void NonResxOutputRefused()
    {
        var neutral = Neutral();

        Assert.Throws<ArgumentException>(() => new PseudoWriter(_store).Plan(_fx.Root, neutral, Path.Combine(_fx.Root, "out.txt")));
    }

    [Fact]
    public void SyncCheckIgnoresThePseudoFile()
    {
        var neutral = Neutral();
        var writer = new PseudoWriter(_store);
        writer.Apply(writer.Plan(_fx.Root, neutral, null).Token);

        Assert.Empty(CultureSyncChecker.Check(_fx.Root));
    }

    [Fact]
    public void ConfirmRefusesWhenNeutralChangedSincePlan()
    {
        var neutral = Neutral();
        var writer = new PseudoWriter(_store);
        var token = writer.Plan(_fx.Root, neutral, null).Token;

        File.AppendAllText(neutral, "\n");

        Assert.Throws<InvalidOperationException>(() => writer.Apply(token));
    }
}
