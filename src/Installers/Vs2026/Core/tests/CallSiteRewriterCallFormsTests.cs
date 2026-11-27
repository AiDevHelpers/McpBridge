using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

// The call forms beyond a plain invocation: constructors, initializers, attributes, extension receivers and omitted defaults.
public sealed class CallSiteRewriterCallFormsTests
{
    private const string File = @"C:\scratch\forms.cs";

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
    public async Task AnObjectCreationIsRewritten()
    {
        const string code =
            "class C\n{\n    public C(int a, int b) { }\n    public int M() { var c = new C(1, 2); return 0; }\n}\n";

        var result = await RewriteAtAsync(code, "C(int", new NewParameter("int b", 1, null), new NewParameter("int a", 0, null));

        Assert.Empty(result.Uncovered);
        Assert.Equal("new C(2, 1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task ABaseInitializerIsRewritten()
    {
        const string code =
            "class A\n{\n    public A(int a, int b) { }\n}\n" +
            "class B : A\n{\n    public B() : base(1, 2) { }\n}\n";

        var result = await RewriteAtAsync(code, "A(int", new NewParameter("int b", 1, null), new NewParameter("int a", 0, null));

        Assert.Empty(result.Uncovered);
        // The initializer's span starts at the colon, so the replacement text does too.
        Assert.Equal(": base(2, 1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task AnAttributeIsRewritten()
    {
        const string code =
            "class Tag : System.Attribute\n{\n    public Tag(int a, int b) { }\n}\n" +
            "[Tag(1, 2)]\nclass D { }\n";

        var result = await RewriteAtAsync(code, "Tag(int", new NewParameter("int b", 1, null), new NewParameter("int a", 0, null));

        Assert.Empty(result.Uncovered);
        Assert.Equal("Tag(2, 1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task AnExtensionCallKeepsItsReceiver()
    {
        const string code =
            "static class E\n{\n    public static int Add(this int x, int y) => x + y;\n}\n" +
            "class U\n{\n    int M(int z) => z.Add(2);\n}\n";

        var result = await RewriteAtAsync(code, "Add(this", new NewParameter("int x", 0, null));

        Assert.Empty(result.Uncovered);
        Assert.Equal("z.Add()", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task AnExtensionReceiverThatIsRemovedIsUncovered()
    {
        const string code =
            "static class E\n{\n    public static int Add(this int x, int y) => x + y;\n}\n" +
            "class U\n{\n    int M(int z) => z.Add(2);\n}\n";

        var result = await RewriteAtAsync(code, "Add(this", new NewParameter("int y", 1, null));

        Assert.Empty(result.Edits);
        Assert.Single(result.Uncovered);
    }

    [Fact]
    public async Task AnOmittedTrailingDefaultStaysOmitted()
    {
        const string code =
            "class C\n{\n    int Add(int a, int b = 5) => a + b;\n    int Use() => Add(1);\n}\n";

        var result = await RewriteAtAsync(code, "Add(int", new NewParameter("int a", 0, null), new NewParameter("int b", 1, null));

        Assert.Empty(result.Uncovered);
        Assert.Equal("Add(1)", Assert.Single(result.Edits).NewText);
    }

    [Fact]
    public async Task AnOmittedDefaultBeforeAnArgumentIsUncovered()
    {
        const string code =
            "class C\n{\n    int Add(int a, int b = 5) => a + b;\n    int Use() => Add(1);\n}\n";

        var result = await RewriteAtAsync(code, "Add(int", new NewParameter("int b", 1, null), new NewParameter("int a", 0, null));

        Assert.Empty(result.Edits);
        Assert.Single(result.Uncovered);
    }
}
