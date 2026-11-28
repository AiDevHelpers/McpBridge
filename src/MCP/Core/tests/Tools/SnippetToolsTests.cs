using AiDevHelpers.MCP.Core.Tools;
using System.Text.Json;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class SnippetToolsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "snip-" + Guid.NewGuid().ToString("N"));

    public SnippetToolsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
            // a locked file in the temp folder is not a test failure
        }
    }

    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
          <CodeSnippet Format="1.0.0">
            <Header>
              <Title>Guard clause</Title>
              <Shortcut>guard</Shortcut>
              <Description>Throw when null</Description>
              <SnippetTypes>
                <SnippetType>Expansion</SnippetType>
              </SnippetTypes>
            </Header>
            <Snippet>
              <Code Language="CSharp"><![CDATA[if ($name$ is null) throw new ArgumentNullException(nameof($name$));$end$]]></Code>
            </Snippet>
          </CodeSnippet>
        </CodeSnippets>
        """;

    [Fact]
    public void A_snippet_file_is_parsed_into_its_header_and_language()
    {
        var info = SnippetCatalog.Parse(Sample, "guard.snippet");

        Assert.NotNull(info);
        Assert.Equal("Guard clause", info!.Title);
        Assert.Equal("guard", info.Shortcut);
        Assert.Equal("Throw when null", info.Description);
        Assert.Equal("CSharp", info.Language);
        Assert.Equal(["Expansion"], info.SnippetTypes);
    }

    [Fact]
    public void A_file_that_is_not_a_snippet_is_skipped()
    {
        Assert.Null(SnippetCatalog.Parse("<notes/>", "x.snippet"));
        Assert.Null(SnippetCatalog.Parse("not xml at all", "y.snippet"));
    }

    [Fact]
    public void Scan_finds_snippets_under_the_folder_and_filters_by_language()
    {
        var sub = Path.Combine(_folder, "My Code Snippets");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "guard.snippet"), Sample);
        File.WriteAllText(Path.Combine(sub, "bad.snippet"), "<broken");

        var all = SnippetCatalog.Scan([_folder], null, null, 200);
        Assert.Single(all.Snippets);
        Assert.Equal("guard", all.Snippets[0].Shortcut);

        Assert.Empty(SnippetCatalog.Scan([_folder], null, "VisualBasic", 200).Snippets);
        Assert.Single(SnippetCatalog.Scan([_folder], "guard", "csharp", 200).Snippets);
    }

    [Fact]
    public void Scan_caps_the_number_of_results()
    {
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_folder, $"s{i}.snippet"), Sample);

        var found = SnippetCatalog.Scan([_folder], null, null, 2);

        Assert.Equal(2, found.Snippets.Count);
        Assert.True(found.Truncated);
    }

    [Fact]
    public void The_list_tool_returns_the_snippets_as_json()
    {
        File.WriteAllText(Path.Combine(_folder, "guard.snippet"), Sample);

        using var doc = JsonDocument.Parse(SnippetTools.ListSnippetsIn([_folder], null, null, 200));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("guard", doc.RootElement.GetProperty("snippets")[0].GetProperty("shortcut").GetString());
    }
}
