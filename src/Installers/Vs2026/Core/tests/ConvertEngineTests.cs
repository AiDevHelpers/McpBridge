using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ConvertEngineTests
{
    private const string File = @"C:\scratch\convert.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task APrivateFieldBecomesAProperty()
    {
        const string code = "class C\n{\n    private int _count;\n    int Get() => _count;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "field-to-property");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("private int Count { get; set; }", text);
        Assert.Contains("int Get() => Count;", text);
        Assert.DoesNotContain("_count", text);
    }

    [Fact]
    public async Task ANullGuardIsAddedForAReferenceParameter()
    {
        const string code = "class C\n{\n    void Take(string name)\n    {\n        System.Console.Write(name);\n    }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "null-guards");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("if (name == null) throw new System.ArgumentNullException(nameof(name));", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnUnknownKindIsRefused()
    {
        const string code = "class C\n{\n    int _x;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ConvertEngine.PlanAsync(solution, File, 3, "rotate");

        Assert.True(plan.Refused);
    }
}
