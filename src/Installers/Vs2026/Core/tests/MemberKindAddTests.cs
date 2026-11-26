using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

// ide_add_method on the base method kinds: a constructor and an operator, added to a type.
public sealed class MemberKindAddTests
{
    private const string File = @"C:\scratch\add.cs";

    private const string Code = "class C\n{\n    public int M() => 0;\n}\n";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AConstructorIsAddedToTheType()
    {
        var solution = TestSolutions.Single([(File, Code)]);

        var plan = await MethodAddEngine.PlanAsync(solution, File, 3, "public C(int a) { }", null, false, new DocUpdates());

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public C(int a) { }", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnOperatorIsAddedToTheType()
    {
        var solution = TestSolutions.Single([(File, Code)]);

        var plan = await MethodAddEngine.PlanAsync(solution, File, 3, "public static C operator -(C x) => x;", null, false, new DocUpdates());

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public static C operator -(C x) => x;", await TextOfAsync(plan));
    }
}
