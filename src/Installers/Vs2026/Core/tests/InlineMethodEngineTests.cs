using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class InlineMethodEngineTests
{
    private const string File = @"C:\scratch\inline.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task ACallIsReplacedByTheBodyAndTheMethodIsDeleted()
    {
        const string code = "class C\n{\n    int Twice(int a) => a * 2;\n    int Use() => Twice(3) + 1;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Twice(int");

        var plan = await InlineMethodEngine.PlanAsync(solution, File, line, column);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("int Use() => (3 * 2) + 1;", text);
        Assert.DoesNotContain("Twice(int a)", text);
    }

    [Fact]
    public async Task ARecursiveMethodIsRefused()
    {
        const string code = "class C\n{\n    int F(int a) => a == 0 ? 0 : F(a - 1);\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "F(int");

        var plan = await InlineMethodEngine.PlanAsync(solution, File, line, column);

        Assert.True(plan.Refused);
    }
}
