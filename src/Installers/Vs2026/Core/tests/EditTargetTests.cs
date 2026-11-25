using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class EditTargetTests
{
    private const string File = @"C:\scratch\t.cs";

    private const string Code =
        "class C\n" +
        "{\n" +
        "    public C() { }\n" +
        "    public int this[int i] { get { return i; } }\n" +
        "    public static C operator +(C a, C b) { return a; }\n" +
        "    public event System.EventHandler Changed;\n" +
        "    int a, b;\n" +
        "    public int Value { get; set; }\n" +
        "}\n";

    private static async Task<(EditTarget? Target, string Reason)> AtAsync(int line, params Type[] kinds)
    {
        var reason = string.Empty;
        var target = await EditTarget.LocateAsync(TestSolutions.Single([(File, Code)]), File, line, 0, kinds, r => reason = r);
        return (target, reason);
    }

    [Fact]
    public async Task AConstructorIsFoundOnItsLine()
    {
        var (target, _) = await AtAsync(3, EditTarget.AllMemberKinds);

        Assert.NotNull(target);
        Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>(target!.Declaration);
        Assert.Equal(".ctor", target.Symbol.Name);
    }

    [Fact]
    public async Task AnIndexerOperatorAndEventAreFound()
    {
        Assert.Equal("this[]", (await AtAsync(4, EditTarget.AllMemberKinds)).Target!.Symbol.Name);
        Assert.Equal("op_Addition", (await AtAsync(5, EditTarget.AllMemberKinds)).Target!.Symbol.Name);
        Assert.Equal("Changed", (await AtAsync(6, EditTarget.AllMemberKinds)).Target!.Symbol.Name);
    }

    [Fact]
    public async Task AFieldWithSeveralVariablesIsNamedNotGuessed()
    {
        var (target, reason) = await AtAsync(7, EditTarget.AllMemberKinds);

        Assert.Null(target);
        Assert.Contains("several variables", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheKindsAskedForAreReturned()
    {
        var (target, reason) = await AtAsync(3, typeof(Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax));

        Assert.Null(target);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public async Task APropertyIsFoundWhenPropertyKindsAreAsked()
    {
        var (target, _) = await AtAsync(8, typeof(Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax));

        Assert.NotNull(target);
        Assert.Equal("Value", target!.Symbol.Name);
    }
}
