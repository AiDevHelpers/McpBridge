using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

public sealed class SnippetWriterTests : IDisposable
{
    private readonly string _codeSnippets = Path.Combine(Path.GetTempPath(), "snipw-" + Guid.NewGuid().ToString("N"), "Visual Studio 18", "Code Snippets");

    public SnippetWriterTests() => Directory.CreateDirectory(_codeSnippets);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_codeSnippets))!, true);
        }
        catch (IOException)
        {
            // a locked file in the temp folder is not a test failure
        }
    }

    [Fact]
    public void The_written_xml_parses_back_as_a_snippet_with_the_same_header()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Guard clause", "guard", "Throw when null", "CSharp", "if ($name$ is null) throw new ArgumentNullException(nameof($name$));$end$");

        Assert.True(plan.Ok, plan.Error);
        var info = SnippetCatalog.Parse(plan.Xml!, plan.Path!);
        Assert.NotNull(info);
        Assert.Equal("Guard clause", info!.Title);
        Assert.Equal("guard", info.Shortcut);
        Assert.Equal("CSharp", info.Language);
    }

    [Fact]
    public void Code_with_xml_characters_is_escaped_and_read_back_unchanged()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Compare", "cmp", "d", "CSharp", "if (a < b && c > d) return;$end$");

        Assert.True(plan.Ok, plan.Error);
        Assert.Contains("if (a < b && c > d) return;$end$", plan.Xml);
    }

    [Fact]
    public void A_shortcut_with_spaces_is_refused()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Guard", "bad shortcut", "d", "CSharp", "x$end$");

        Assert.False(plan.Ok);
    }

    [Fact]
    public void An_unsupported_language_is_refused()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Guard", "g", "d", "Cobol", "x$end$");

        Assert.False(plan.Ok);
    }

    [Fact]
    public void Plan_only_writes_nothing()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Guard", "guard", "d", "CSharp", "x$end$");

        Assert.True(plan.Ok, plan.Error);
        Assert.False(File.Exists(plan.Path));
    }

    [Fact]
    public void Confirm_writes_the_file_under_the_language_folder()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Guard", "guard", "d", "CSharp", "x$end$");

        var written = SnippetWriter.Write(plan);

        Assert.True(written.Ok, written.Error);
        Assert.True(File.Exists(plan.Path));
        Assert.Contains(Path.Combine("Visual C#", "My Code Snippets"), plan.Path);
    }

    [Fact]
    public void Confirm_refuses_to_overwrite_an_existing_snippet()
    {
        var plan = SnippetWriter.Plan(_codeSnippets, "Guard", "guard", "d", "CSharp", "x$end$");
        SnippetWriter.Write(plan);

        var again = SnippetWriter.Write(plan);

        Assert.False(again.Ok);
    }
}
