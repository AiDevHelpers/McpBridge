using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class CallSiteRewriterTests
{
    private const string File = @"C:\scratch\cs.cs";

    private const string Code =
        "class C\n" +
        "{\n" +
        "    int Add(int a, int b) { return a + b; }\n" +
        "    int Use() { return Add(1, 2); }\n" +
        "}\n";

    private static async Task<(List<CallerEdit> Edits, List<object> Uncovered)> RewriteAsync(IReadOnlyList<NewParameter> parameters, string code = Code)
    {
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Add(int");
        var target = await EditTarget.LocateAsync(solution, File, line, column, EditTarget.AllMemberKinds, _ => { });
        var method = (Microsoft.CodeAnalysis.IMethodSymbol)target!.Symbol;
        var references = await MethodEditEngine.CollectReferencesAsync(solution, method);
        references.RemoveAll(r => r.DocumentId == target.DocumentId && target.Declaration.Span.Contains(r.Span));
        return await CallSiteRewriter.RewriteAsync(solution, method, references, parameters);
    }

    [Fact]
    public async Task AReorderMovesTheArguments()
    {
        var result = await RewriteAsync([new NewParameter("int b", 1, null), new NewParameter("int a", 0, null)]);

        Assert.Empty(result.Uncovered);
        Assert.Equal("Add(2, 1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task ARemovedParameterDropsItsArgument()
    {
        var result = await RewriteAsync([new NewParameter("int a", 0, null)]);

        Assert.Equal("Add(1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task ANewParameterGetsItsValueForCallers()
    {
        var result = await RewriteAsync([new NewParameter("int a", 0, null), new NewParameter("int b", 1, null), new NewParameter("int c", null, "0")]);

        Assert.Equal("Add(1, 2, 0)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task ANewParameterWithoutAValueIsUncovered()
    {
        var result = await RewriteAsync([new NewParameter("int a", 0, null), new NewParameter("int b", 1, null), new NewParameter("int c", null, null)]);

        Assert.Empty(result.Edits);
        Assert.Single(result.Uncovered);
    }

    [Fact]
    public async Task ANamedArgumentIsMatchedByName()
    {
        const string code =
            "class C\n{\n    int Add(int a, int b) { return a + b; }\n" +
            "    int Use() { return Add(b: 2, a: 1); }\n}\n";

        var result = await RewriteAsync([new NewParameter("int b", 1, null), new NewParameter("int a", 0, null)], code);

        Assert.Equal("Add(2, 1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task ARemovedArgumentWithASideEffectIsUncovered()
    {
        const string code =
            "class C\n{\n    int Add(int a, int b) { return a + b; }\n" +
            "    int Next() { return 1; }\n" +
            "    int Use() { return Add(1, Next()); }\n}\n";

        var result = await RewriteAsync([new NewParameter("int a", 0, null)], code);

        Assert.Empty(result.Edits);
        Assert.Single(result.Uncovered);
    }
}
