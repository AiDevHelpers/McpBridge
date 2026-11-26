using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ExtractMethodEngineTests
{
    private const string File = @"C:\scratch\extract.cs";

    private const string Code =
        "class C\n{\n    int Outer(int a)\n    {\n        int x = a + 1;\n        int y = x * 2;\n        return y;\n    }\n}\n";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AStatementRunBecomesAMethodWithItsParametersAndResult()
    {
        var solution = TestSolutions.Single([(File, Code)]);

        var plan = await ExtractMethodEngine.PlanAsync(solution, File, 5, 6, "Helper", "private");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("private int Helper(int a)", text);
        Assert.Contains("int x = a + 1;", text);
        Assert.Contains("return y;", text);
        Assert.Contains("int y = Helper(a);", text);
    }

    [Fact]
    public async Task ARangeWithAReturnIsRefused()
    {
        var solution = TestSolutions.Single([(File, Code)]);

        var plan = await ExtractMethodEngine.PlanAsync(solution, File, 5, 7, "Helper", "private");

        Assert.True(plan.Refused);
    }
}
