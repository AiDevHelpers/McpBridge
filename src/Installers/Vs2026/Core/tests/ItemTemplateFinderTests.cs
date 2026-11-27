using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ItemTemplateFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "itemtemplates-" + Guid.NewGuid().ToString("N"));

    public ItemTemplateFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativePath, string xml)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return path;
    }

    private static string Item(string name, string description, string projectType) =>
        $"<VSTemplate Type=\"Item\" Version=\"3.0.0\" xmlns=\"http://schemas.microsoft.com/developer/vstemplate/2005\"><TemplateData><Name>{name}</Name><Description>{description}</Description><ProjectType>{projectType}</ProjectType></TemplateData></VSTemplate>";

    [Fact]
    public void ListsItemTemplatesWithNameDescriptionLanguageAndPath()
    {
        var path = Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "An empty class", "CSharp"));

        var result = ItemTemplateFinder.Find([_root], null, null, 50);

        var template = Assert.Single(result.Templates);
        Assert.Equal("Class", template.Name);
        Assert.Equal("An empty class", template.Description);
        Assert.Equal("CSharp", template.Language);
        Assert.Equal("Code", template.Category);
        Assert.Equal(path, template.Path);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ProjectTemplatesAreSkipped()
    {
        Write(@"CSharp\Console\1033\App\App.vstemplate", "<VSTemplate Type=\"Project\" xmlns=\"http://schemas.microsoft.com/developer/vstemplate/2005\"><TemplateData><Name>App</Name></TemplateData></VSTemplate>");
        Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "A class", "CSharp"));

        var result = ItemTemplateFinder.Find([_root], null, null, 50);

        Assert.Equal(["Class"], result.Templates.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void AResourceReferenceNameFallsBackToTheTemplateFileName()
    {
        Write(@"VisualBasic\Code\1033\Module\Module.vstemplate", "<VSTemplate Type=\"Item\" xmlns=\"http://schemas.microsoft.com/developer/vstemplate/2005\"><TemplateData><Name Package=\"{00000000-0000-0000-0000-000000000000}\" ID=\"4000\" /><ProjectType>VisualBasic</ProjectType></TemplateData></VSTemplate>");

        var result = ItemTemplateFinder.Find([_root], null, null, 50);

        var template = Assert.Single(result.Templates);
        Assert.Equal("Module", template.Name);
        Assert.Equal("VisualBasic", template.Language);
    }

    [Fact]
    public void TheFilterMatchesNameOrDescriptionIgnoringCase()
    {
        Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "An empty class", "CSharp"));
        Write(@"CSharp\Code\1033\Interface\Interface.vstemplate", Item("Interface", "Declares a contract", "CSharp"));

        Assert.Equal(["Interface"], ItemTemplateFinder.Find([_root], "CONTRACT", null, 50).Templates.Select(t => t.Name).ToArray());
        Assert.Equal(["Class"], ItemTemplateFinder.Find([_root], "class", null, 50).Templates.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void TheLanguageFilterIgnoresCase()
    {
        Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "c#", "CSharp"));
        Write(@"VisualBasic\Code\1033\Class\Class.vstemplate", Item("Class", "vb", "VisualBasic"));

        var result = ItemTemplateFinder.Find([_root], null, "csharp", 50);

        Assert.Equal(["CSharp"], result.Templates.Select(t => t.Language).ToArray());
    }

    [Fact]
    public void AMalformedTemplateIsSkipped()
    {
        Write(@"CSharp\Code\1033\Bad\Bad.vstemplate", "<VSTemplate><not closed");
        Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "A class", "CSharp"));

        var result = ItemTemplateFinder.Find([_root], null, null, 50);

        Assert.Equal(["Class"], result.Templates.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void ALimitTruncatesAndSaysSo()
    {
        for (var i = 0; i < 5; i++)
        {
            Write($@"CSharp\Code\1033\T{i}\T{i}.vstemplate", Item("T" + i, "d", "CSharp"));
        }

        var result = ItemTemplateFinder.Find([_root], null, null, 3);

        Assert.Equal(3, result.Templates.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void AMissingRootIsIgnored()
    {
        Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "A class", "CSharp"));

        var result = ItemTemplateFinder.Find([Path.Combine(_root, "nope"), _root], null, null, 50);

        Assert.Single(result.Templates);
    }

    [Fact]
    public void TheSameTemplateUnderTwoRootsIsListedOnce()
    {
        Write(@"CSharp\Code\1033\Class\Class.vstemplate", Item("Class", "A class", "CSharp"));

        var result = ItemTemplateFinder.Find([_root, _root], null, null, 50);

        Assert.Single(result.Templates);
    }
}
