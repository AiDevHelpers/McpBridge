using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

// ide_edit_method on the base method kinds: constructors and operators, not only ordinary methods.
public sealed class MemberKindEditTests
{
    private const string File = @"C:\scratch\kinds.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AConstructorBodyCanBeEdited()
    {
        const string code = "class C\n{\n    public C(int a) { }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "C(int");

        var plan = await MethodEditEngine.PlanAsync(solution, File, line, column, "public C(int a) { System.Console.Write(a); }", []);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("System.Console.Write(a)", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnOperatorBodyCanBeEdited()
    {
        const string code = "class C\n{\n    public static int operator +(C x, C y) => 0;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "operator +");

        var plan = await MethodEditEngine.PlanAsync(solution, File, line, column, "public static int operator +(C x, C y) => 1;", []);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("=> 1;", await TextOfAsync(plan));
    }
}
