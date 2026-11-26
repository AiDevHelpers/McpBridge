using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ThreadParameterEngineTests
{
    private const string File = @"C:\scratch\thread.cs";

    private static async Task<MethodEditPlan> PlanAsync(string code, string marker, string declaration, string valueAtTop, int depth)
    {
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, marker);
        return await ThreadParameterEngine.PlanAsync(solution, File, line, column, declaration, valueAtTop, depth);
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task ADepthOneThreadsTheDirectCallersAndPassesTheTopValue()
    {
        const string code =
            "class C\n{\n    public void Leaf(int a) { }\n" +
            "    void Mid() { Leaf(1); }\n" +
            "    void Top() { Mid(); }\n}\n";

        var plan = await PlanAsync(code, "Leaf(int", "int ct", "0", 1);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("void Leaf(int a, int ct)", text);
        Assert.Contains("void Mid(int ct)", text);
        Assert.Contains("Leaf(1, ct)", text);
        Assert.Contains("Mid(0)", text);
        Assert.Contains(plan.Warnings, w => w.Contains("C.Mid", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallerThatAlreadyHasTheTypePassesItsParameter()
    {
        const string code =
            "class C\n{\n    public void Leaf(int a) { }\n" +
            "    void Mid(int ct) { Leaf(1); }\n}\n";

        var plan = await PlanAsync(code, "Leaf(int", "int ct", "0", 1);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("void Mid(int ct)", text);
        Assert.Contains("Leaf(1, ct)", text);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("C.Mid", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADepthZeroPassesTheTopValueToEveryCaller()
    {
        const string code =
            "class C\n{\n    public void Leaf(int a) { }\n" +
            "    void Mid() { Leaf(1); }\n}\n";

        var plan = await PlanAsync(code, "Leaf(int", "int ct", "0", 0);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("void Mid()", text);
        Assert.Contains("Leaf(1, 0)", text);
    }

    [Fact]
    public async Task AParamsMethodIsRefused()
    {
        const string code =
            "class C\n{\n    public void Leaf(params int[] a) { }\n}\n";

        var plan = await PlanAsync(code, "Leaf(params", "int ct", "0", 1);

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task ANegativeDepthIsRefused()
    {
        const string code = "class C\n{\n    public void Leaf(int a) { }\n}\n";

        var plan = await PlanAsync(code, "Leaf(int", "int ct", "0", -1);

        Assert.True(plan.Refused);
    }
}
