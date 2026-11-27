using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

// Calls of a method whose last parameter is params: arguments passed one by one, or as the array itself.
public sealed class CallSiteRewriterParamsTests
{
    private const string File = @"C:\scratch\params.cs";

    private static async Task<(List<CallerEdit> Edits, List<object> Uncovered)> RewriteAtAsync(string code, string marker, params NewParameter[] parameters)
    {
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, marker);
        var target = await EditTarget.LocateAsync(solution, File, line, column, EditTarget.AllMemberKinds, _ => { });
        var method = (IMethodSymbol)target!.Symbol;
        var references = await MethodEditEngine.CollectReferencesAsync(solution, method);
        references.RemoveAll(r => r.DocumentId == target.DocumentId && target.Declaration.Span.Contains(r.Span));
        return await CallSiteRewriter.RewriteAsync(solution, method, references, parameters);
    }

    [Fact]
    public async Task AnExpandedParamsCallIsKeptWhenTheArrayStays()
    {
        const string code = "class C\n{\n    int Sum(params int[] xs) => 0;\n    int Use() => Sum(1, 2, 3);\n}\n";

        var result = await RewriteAtAsync(code, "Sum(params", new NewParameter("params int[] xs", 0, null));

        Assert.Empty(result.Uncovered);
        Assert.Equal("Sum(1, 2, 3)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task ARemovedParamsDropsItsExpandedArguments()
    {
        const string code = "class C\n{\n    int Sum(params int[] xs) => 0;\n    int Use() => Sum(1, 2, 3);\n}\n";

        var result = await RewriteAtAsync(code, "Sum(params");

        Assert.Empty(result.Uncovered);
        Assert.Equal("Sum()", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task AnArrayArgumentStaysAnArgument()
    {
        const string code = "class C\n{\n    int Sum(params int[] xs) => 0;\n    int Use(int[] a) => Sum(a);\n}\n";

        var result = await RewriteAtAsync(code, "Sum(params", new NewParameter("params int[] xs", 0, null));

        Assert.Empty(result.Uncovered);
        Assert.Equal("Sum(a)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task AParameterAddedAfterParamsIsUncovered()
    {
        const string code = "class C\n{\n    int Sum(params int[] xs) => 0;\n    int Use() => Sum(1);\n}\n";

        var result = await RewriteAtAsync(code, "Sum(params", new NewParameter("params int[] xs", 0, null), new NewParameter("int y", null, "0"));

        Assert.Empty(result.Edits);
        Assert.Single(result.Uncovered);
    }
}
