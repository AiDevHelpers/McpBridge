using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class ImplementMembersEngineTests
{
    private const string File = @"C:\scratch\impl.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AnUnimplementedInterfaceMethodIsStubbed()
    {
        const string code = "interface I\n{\n    int Get(int id);\n}\nclass K : I\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ImplementMembersEngine.PlanAsync(solution, File, 5, "interface", [], false);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("public int Get(int id)", text);
        Assert.Contains("throw new System.NotImplementedException();", text);
    }

    [Fact]
    public async Task AnExplicitStubCarriesTheInterfaceName()
    {
        const string code = "interface I\n{\n    int Get(int id);\n}\nclass K : I\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ImplementMembersEngine.PlanAsync(solution, File, 5, "interface", [], true);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("int I.Get(int id)", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AVirtualBaseMemberIsOverridden()
    {
        const string code = "class A\n{\n    public virtual int Size() => 0;\n}\nclass B : A\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ImplementMembersEngine.PlanAsync(solution, File, 5, "override", ["Size"], false);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public override int Size()", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AMemberThatIsNotVirtualIsRefused()
    {
        const string code = "class A\n{\n    public int Size() => 0;\n}\nclass B : A\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await ImplementMembersEngine.PlanAsync(solution, File, 5, "override", ["Size"], false);

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("Size", System.StringComparison.Ordinal));
    }
}
