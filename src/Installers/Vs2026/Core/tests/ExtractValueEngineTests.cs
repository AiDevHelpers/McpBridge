using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ExtractValueEngineTests
{
    private const string File = @"C:\scratch\value.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AVariableIsDeclaredBeforeItsStatement()
    {
        const string code = "class C\n{\n    int M(int a)\n    {\n        return a * 2 + 1;\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ExtractValueEngine.PlanAsync(solution, File, 5, "a * 2", "twice", "variable");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("var twice = a * 2;", text);
        Assert.Contains("return twice + 1;", text);
    }

    [Fact]
    public async Task AConstantIsDeclaredWithItsType()
    {
        const string code = "class C\n{\n    int M()\n    {\n        return 3 * 4;\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ExtractValueEngine.PlanAsync(solution, File, 5, "3 * 4", "Twelve", "constant");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("const int Twelve = 3 * 4;", text);
        Assert.Contains("return Twelve;", text);
    }

    [Fact]
    public async Task ANonConstantIsRefusedAsAConstant()
    {
        const string code = "class C\n{\n    int M(int a)\n    {\n        return a * 2;\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ExtractValueEngine.PlanAsync(solution, File, 5, "a * 2", "Twice", "constant");

        Assert.True(plan.Refused);
    }
}
