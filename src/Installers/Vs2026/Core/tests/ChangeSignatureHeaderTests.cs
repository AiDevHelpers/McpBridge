using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ChangeSignatureHeaderTests
{
    private const string File = @"C:\scratch\header.cs";

    private static async Task<MethodEditPlan> PlanAsync(string code, HeaderChange header)
    {
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, "Add(int");
        return await ChangeSignatureEngine.PlanAsync(solution, File, line, column,
            [new NewParameter("int a", 0, null)], null, header);
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AReturnTypeChangeIsWritten()
    {
        const string code = "class C\n{\n    int Add(int a) => a;\n    void Use() { Add(1); }\n}\n";

        var plan = await PlanAsync(code, new HeaderChange { ReturnType = "long" });

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("long Add(int a)", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnAccessibilityChangeKeepsTheOtherModifiers()
    {
        const string code = "class C\n{\n    static int Add(int a) => a;\n    void Use() { Add(1); }\n}\n";

        var plan = await PlanAsync(code, new HeaderChange { Accessibility = "public" });

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public static int Add(int a)", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnUnknownAccessibilityIsRefused()
    {
        const string code = "class C\n{\n    int Add(int a) => a;\n}\n";

        var plan = await PlanAsync(code, new HeaderChange { Accessibility = "sealed" });

        Assert.True(plan.Refused);
    }
}
