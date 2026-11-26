using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

// ide_edit_method on a local function: the local function is changed, its enclosing method is not.
public sealed class LocalFunctionEditTests
{
    private const string File = @"C:\scratch\local.cs";

    private const string Code =
        "class C\n{\n    int Outer(int a)\n    {\n        int Local(int x) => x;\n        return Local(a);\n    }\n}\n";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task ALocalFunctionBodyIsEditedAndItsMethodIsNot()
    {
        var solution = TestSolutions.Single([(File, Code)]);
        var (line, column) = MethodEditEngineTests.Locate(Code, "Local(int");

        var plan = await MethodEditEngine.PlanAsync(solution, File, line, column, "int Local(int x) => x + 1;", []);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("int Local(int x) => x + 1;", text);
        Assert.Contains("int Outer(int a)", text);
        Assert.Contains("return Local(a);", text);
    }

    [Fact]
    public async Task ASignatureChangeWithACallerIsRefusedWithoutACallerEdit()
    {
        var solution = TestSolutions.Single([(File, Code)]);
        var (line, column) = MethodEditEngineTests.Locate(Code, "Local(int");

        var plan = await MethodEditEngine.PlanAsync(solution, File, line, column, "int Local(int x, int y) => x + y;", []);

        Assert.True(plan.Refused);
        Assert.NotEmpty(plan.Reasons);
    }
}
