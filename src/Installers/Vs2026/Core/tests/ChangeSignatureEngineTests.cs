using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ChangeSignatureEngineTests
{
    private const string File = @"C:\scratch\sig.cs";

    private const string Code =
        "class C\n" +
        "{\n" +
        "    int Add(int a, int b) { return a + b; }\n" +
        "    int Use() { return Add(1, 2); }\n" +
        "}\n";

    private static async Task<MethodEditPlan> PlanAsync(IReadOnlyList<NewParameter> parameters, string code = Code)
    {
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Add(int");
        return await ChangeSignatureEngine.PlanAsync(solution, File, line, column, parameters);
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AReorderRewritesTheSignatureAndTheCalls()
    {
        var plan = await PlanAsync([new NewParameter("int b", 1, null), new NewParameter("int a", 0, null)]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("int Add(int b, int a)", text);
        Assert.Contains("Add(2, 1)", text);
    }

    [Fact]
    public async Task ANewParameterGetsItsValueAtEveryCall()
    {
        var plan = await PlanAsync([new NewParameter("int a", 0, null), new NewParameter("int b", 1, null), new NewParameter("int c", null, "0")]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("int Add(int a, int b, int c)", text);
        Assert.Contains("Add(1, 2, 0)", text);
    }

    [Fact]
    public async Task ANewParameterWithoutAValueIsRefused()
    {
        var plan = await PlanAsync([new NewParameter("int a", 0, null), new NewParameter("int b", 1, null), new NewParameter("int c", null, null)]);

        Assert.True(plan.Refused);
        Assert.Single(plan.Uncovered);
    }

    [Fact]
    public async Task ARemovedArgumentWithASideEffectIsRefused()
    {
        const string code =
            "class C\n{\n    int Add(int a, int b) { return a + b; }\n" +
            "    int Next() { return 1; }\n" +
            "    int Use() { return Add(1, Next()); }\n}\n";

        var plan = await PlanAsync([new NewParameter("int a", 0, null)], code);

        Assert.True(plan.Refused);
        Assert.Single(plan.Uncovered);
    }

    [Fact]
    public async Task AnEditThatReordersRewritesTheCalls()
    {
        var solution = TestSolutions.Single([(File, Code)]);
        var (line, column) = MethodEditEngineTests.Locate(Code, "Add(int");
        var plan = await ChangeSignatureEngine.PlanEditAsync(solution, File, line, column,
            "int Add(int b, int a) { return a + b; }", [], null, null);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("Add(2, 1)", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnEditThatAddsAParameterIsRefusedWithAPointerToChangeSignature()
    {
        var solution = TestSolutions.Single([(File, Code)]);
        var (line, column) = MethodEditEngineTests.Locate(Code, "Add(int");
        var plan = await ChangeSignatureEngine.PlanEditAsync(solution, File, line, column,
            "int Add(int a, int b, int c) { return a + b; }", [], null, null);

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("ide_change_signature", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKeptIndexUsedTwiceIsRefused()
    {
        var plan = await PlanAsync([new NewParameter("int a", 0, null), new NewParameter("int b", 0, null)]);

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("fromOld", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task AParameterThatIsNotADeclarationIsRefused()
    {
        var plan = await PlanAsync([new NewParameter("int a", 0, null), new NewParameter("int", 1, null)]);

        Assert.True(plan.Refused);
    }
}
