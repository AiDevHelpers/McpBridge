using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class MoveMemberEngineTests
{
    private const string File = @"C:\scratch\move.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AStaticMethodMovesAndItsQualifiedCallIsRenamed()
    {
        const string code =
            "class Source\n{\n    public static int Helper() => 1;\n}\n" +
            "class Dest\n{\n}\n" +
            "class Use\n{\n    int M() => Source.Helper();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Helper() =>");

        var plan = await MoveMemberEngine.PlanAsync(solution, File, line, column, "Dest", []);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("class Dest", text);
        Assert.Contains("public static int Helper() => 1;", text);
        Assert.Contains("Dest.Helper()", text);
        Assert.DoesNotContain("class Source\n{\n    public static int Helper", text);
    }

    [Fact]
    public async Task AnInstanceMethodMovesAsStaticAndItsUnqualifiedCallIsRenamed()
    {
        const string code =
            "class Source\n{\n    public int Helper() => 1;\n    int M() => Helper();\n}\n" +
            "class Dest\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Helper() =>");

        var plan = await MoveMemberEngine.PlanAsync(solution, File, line, column, "Dest", [], toStatic: true);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("static public int Helper() => 1;", text);
        Assert.Contains("Dest.Helper()", text);
    }

    [Fact]
    public async Task AnInstanceMethodThatUsesFieldsIsNotMadeStatic()
    {
        const string code =
            "class Source\n{\n    int _n;\n    public int Helper() => _n;\n}\n" +
            "class Dest\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Helper() =>");

        var plan = await MoveMemberEngine.PlanAsync(solution, File, line, column, "Dest", [], toStatic: true);

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task AMissingDestinationIsRefused()
    {
        const string code = "class Source\n{\n    public static int Helper() => 1;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Helper() =>");

        var plan = await MoveMemberEngine.PlanAsync(solution, File, line, column, "Nowhere", []);

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("Nowhere", System.StringComparison.Ordinal));
    }
}
