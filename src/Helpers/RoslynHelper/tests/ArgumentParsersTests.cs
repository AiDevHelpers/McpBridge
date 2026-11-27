using System.Text.Json;
using AiDevHelpers.Installers.Vs2026.Core;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// The parser cases use realistic argument objects, not the empty addMember and changeSignature cases in conformance/fixtures.json.
public class ArgumentParsersTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void callerEdits_are_read_with_file_line_column_and_text()
    {
        var args = Parse("""{"callerEdits":[{"file":"C:\\s\\a.cs","line":12,"column":9,"newText":"Foo(1, 2)"}]}""");

        var edits = ArgumentParsers.ParseCallerEdits(args);

        var edit = Assert.Single(edits);
        Assert.Equal(@"C:\s\a.cs", edit.File);
        Assert.Equal(12, edit.Line);
        Assert.Equal(9, edit.Column);
        Assert.Equal("Foo(1, 2)", edit.NewText);
    }

    [Fact]
    public void members_default_accessibility_backing_and_setter()
    {
        var args = Parse("""{"members":[{"name":"Count","type":"int","attributes":["JsonIgnore"]}]}""");

        var spec = Assert.Single(ArgumentParsers.ParseMemberSpecs(args));

        Assert.Equal("Count", spec.Name);
        Assert.Equal("int", spec.Type);
        Assert.Equal("auto", spec.Backing);
        Assert.Equal("set", spec.Setter);
        Assert.Equal(new[] { "JsonIgnore" }, spec.Attributes);
    }

    [Fact]
    public void members_with_a_declaration_need_no_name_or_type()
    {
        var args = Parse("""{"members":[{"declaration":"public int Size { get; set; }"}]}""");

        var spec = Assert.Single(ArgumentParsers.ParseMemberSpecs(args));

        Assert.Equal("public int Size { get; set; }", spec.Declaration);
    }

    [Fact]
    public void new_parameters_keep_the_old_index_only_when_given()
    {
        var args = Parse("""{"parameters":[{"declaration":"int a","fromOld":0},{"declaration":"string b","valueForCallers":"\"x\""}]}""");

        var parameters = ArgumentParsers.ParseNewParameters(args);

        Assert.Equal(0, parameters[0].FromOld);
        Assert.Null(parameters[1].FromOld);
        Assert.Equal("\"x\"", parameters[1].Value);
    }

    [Fact]
    public void doc_updates_read_the_summary_and_tagged_items()
    {
        var args = Parse("""{"newSummary":"Adds two numbers.","docs":[{"tag":"param","name":"a","text":"First."},{"tag":"returns","text":"The sum."}]}""");

        var docs = ArgumentParsers.ParseDocUpdates(args);

        Assert.Equal("Adds two numbers.", docs.NewSummary);
        Assert.Equal(2, docs.Items.Count);
        Assert.Equal("param", docs.Items[0].Tag);
        Assert.Equal("a", docs.Items[0].Name);
        Assert.Null(docs.Items[1].Name);
    }

    [Fact]
    public void unused_items_default_kind_to_member()
    {
        var args = Parse("""{"items":[{"file":"a.cs","line":3},{"file":"b.cs","line":8,"kind":"type"}]}""");

        var items = ArgumentParsers.ParseUnusedItems(args);

        Assert.Equal("member", items[0].Kind);
        Assert.Equal("type", items[1].Kind);
    }

    [Fact]
    public void edits_read_every_range_field()
    {
        var args = Parse("""{"edits":[{"file":"a.cs","startLine":1,"startColumn":2,"endLine":3,"endColumn":4,"newText":"x"}]}""");

        var edit = Assert.Single(ArgumentParsers.ParseEdits(args));

        Assert.Equal((File: "a.cs", StartLine: 1, StartColumn: 2, EndLine: 3, EndColumn: 4, NewText: "x"), edit);
    }

    [Fact]
    public void a_missing_required_string_is_an_argument_error()
    {
        var args = Parse("""{"file":"a.cs"}""");

        var error = Assert.Throws<ArgumentException>(() => ArgumentParsers.RequireString(args, "newName"));

        Assert.Contains("newName", error.Message);
    }

    [Fact]
    public void optional_int_is_null_for_a_non_number()
    {
        var args = Parse("""{"column":"nine"}""");

        Assert.Null(ArgumentParsers.OptionalInt(args, "column"));
    }
}
