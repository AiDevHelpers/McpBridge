using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class GenerateConstructorEngineTests
{
    private const string File = @"C:\scratch\ctor.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AConstructorAssignsTheChosenMembers()
    {
        const string code = "class C\n{\n    int _count;\n    string Name { get; set; }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await GenerateConstructorEngine.PlanAsync(solution, File, 1, ["_count", "Name"], "public");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("public C(int count, string name)", text);
        Assert.Contains("this._count = count;", text);
        Assert.Contains("this.Name = name;", text);
    }

    [Fact]
    public async Task AMemberWithoutASetterIsRefused()
    {
        const string code = "class C\n{\n    int Size => 0;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await GenerateConstructorEngine.PlanAsync(solution, File, 1, ["Size"], "public");

        Assert.True(plan.Refused);
    }

    [Fact]
    public void AParameterNameLosesItsUnderscoreAndKeywordsAreEscaped()
    {
        Assert.Equal("count", GenerateConstructorEngine.ParameterName("_count"));
        Assert.Equal("@class", GenerateConstructorEngine.ParameterName("Class"));
    }
}
