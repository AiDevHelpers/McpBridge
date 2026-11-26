using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ChangeSignatureFamilyTests
{
    private const string File = @"C:\scratch\family.cs";

    private static async Task<MethodEditPlan> PlanAsync(string code, string marker, params NewParameter[] parameters)
    {
        var solution = TestSolutions.Single([(File, code)]);
        var (line, column) = MethodEditEngineTests.Locate(code, marker);
        return await ChangeSignatureEngine.PlanAsync(solution, File, line, column, parameters);
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AnOverrideChangesWithItsBaseMethodAndItsCalls()
    {
        const string code =
            "class A\n{\n    public virtual int Add(int a, int b) => a + b;\n}\n" +
            "class B : A\n{\n    public override int Add(int a, int b) => a;\n}\n" +
            "class U\n{\n    int M(A x) => x.Add(1, 2);\n}\n";

        var plan = await PlanAsync(code, "Add(int", new NewParameter("int b", 1, null), new NewParameter("int a", 0, null));

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("public virtual int Add(int b, int a)", text);
        Assert.Contains("public override int Add(int b, int a)", text);
        Assert.Contains("x.Add(2, 1)", text);
    }

    [Fact]
    public async Task AnInterfaceMemberChangesWithItsImplementation()
    {
        const string code =
            "interface I\n{\n    int Add(int a, int b);\n}\n" +
            "class K : I\n{\n    public int Add(int a, int b) => a;\n}\n" +
            "class U\n{\n    int M(I x) => x.Add(1, 2);\n}\n";

        var plan = await PlanAsync(code, "Add(int", new NewParameter("int b", 1, null), new NewParameter("int a", 0, null));

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("int Add(int b, int a);", text);
        Assert.Contains("public int Add(int b, int a)", text);
        Assert.Contains("x.Add(2, 1)", text);
    }

    [Fact]
    public async Task AFamilyChangeNamesEveryMemberItChanged()
    {
        const string code =
            "class A\n{\n    public virtual int Add(int a, int b) => a + b;\n}\n" +
            "class B : A\n{\n    public override int Add(int a, int b) => a;\n}\n";

        var plan = await PlanAsync(code, "Add(int", new NewParameter("int a", 0, null), new NewParameter("int b", 1, null));

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains(plan.Warnings, w => w.Contains("B.Add", System.StringComparison.Ordinal));
    }
}
