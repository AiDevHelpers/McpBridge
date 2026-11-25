using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class TemplateListTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dotnet-new-list.txt"));

    [Fact]
    public void ParsesEveryTemplateRow()
    {
        var result = TemplateList.Parse(Fixture());

        Assert.True(result.Ok);
        Assert.Equal(6, result.Items.Count);
    }

    [Fact]
    public void ReadsNameShortNameLanguagesAndTags()
    {
        var item = TemplateList.Parse(Fixture()).Items.Single(t => t.ShortName == "classlib");

        Assert.Equal("Class Library", item.Name);
        Assert.Equal(["C#", "F#", "VB"], item.Languages);
        Assert.Equal(["Common/Library"], item.Tags);
    }

    [Fact]
    public void TruncatedNameIsKeptAsShownByTheCli()
    {
        var item = TemplateList.Parse(Fixture()).Items.Single(t => t.ShortName == "mvc");

        Assert.Equal("ASP.NET Core Web App (Model-View-Co...", item.Name);
    }

    [Fact]
    public void FilterByLanguageAndTag()
    {
        var items = TemplateList.Parse(Fixture()).Items;

        Assert.All(TemplateList.Filter(items, language: "F#", tag: null), t => Assert.Contains("F#", t.Languages));
        Assert.Equal(2, TemplateList.Filter(items, language: null, tag: "Test Project").Count);
    }

    [Fact]
    public void MissingHeaderIsAClearError()
    {
        var result = TemplateList.Parse("nothing useful here\n");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void NoTemplatesIsOkAndEmpty()
    {
        var text = "These templates matched your input: --type='project'\n\nTemplate Name    Short Name    Language    Tags\n-----------  ----------  ----------  ----\n";

        var result = TemplateList.Parse(text);

        Assert.True(result.Ok);
        Assert.Empty(result.Items);
    }
}
