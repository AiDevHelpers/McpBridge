using Xunit;

namespace AiDevHelpers.Helpers.LocalizationHelper.Tests;

public sealed class ResxFileTests : IDisposable
{
    private readonly ResxFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void LoadSaveIsByteIdentical(string newline, bool bom)
    {
        var path = _fx.Resx("P", null, newline, bom, ("A", "One", "c"), ("B", "Two", null));
        var before = File.ReadAllBytes(path);

        ResxFile.Load(path).Save();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AddWritesOneDataElementAndNothingElse()
    {
        var path = _fx.Resx("P", null, "\n", false, ("A", "One", null));
        var before = File.ReadAllLines(path);

        var file = ResxFile.Load(path);
        file.Add("B", "Two", "needs-translation");
        file.Save();

        var after = File.ReadAllLines(path);
        Assert.Equal(before.Length + 1, after.Length);
        Assert.Equal(before.Take(before.Length - 1), after.Take(before.Length - 1));
        Assert.Contains("name=\"B\"", after[^2]);
        Assert.Contains("<comment>needs-translation</comment>", after[^2]);
    }

    [Fact]
    public void AddKeepsCrlf()
    {
        var path = _fx.Resx("P", null, "\r\n", false, ("A", "One", null));

        var file = ResxFile.Load(path);
        file.Add("B", "Two", null);
        file.Save();

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void RemoveWritesAMinimalDiff()
    {
        var path = _fx.Resx("P", null, "\n", false, ("A", "One", null), ("B", "Two", null));
        var before = File.ReadAllLines(path);

        var file = ResxFile.Load(path);
        Assert.True(file.Remove("B"));
        file.Save();

        Assert.Equal(before.Where(l => !l.Contains("name=\"B\"")), File.ReadAllLines(path));
    }

    [Fact]
    public void SpecialCharactersRoundTrip()
    {
        var path = _fx.Resx("P", null, "\n", false, ("A", "x", null));
        var file = ResxFile.Load(path);

        file.Add("B", "a & b < c", null);
        file.Save();

        Assert.Equal("a & b < c", ResxFile.Load(path).Entries.Single(e => e.Name == "B").Value);
    }

    [Fact]
    public void CreateFromCopiesHeaderNotData()
    {
        var neutral = ResxFile.Load(_fx.Resx("P", null, "\n", false, ("A", "One", null)));
        var target = Path.Combine(_fx.Root, "P", "Resources", "Strings.de.resx");

        var created = ResxFile.CreateFrom(neutral, target);
        created.Save();

        var text = File.ReadAllText(target);
        Assert.Contains("resmimetype", text);
        Assert.DoesNotContain("name=\"A\"", text);
        Assert.Empty(ResxFile.Load(target).Entries);
    }
}
