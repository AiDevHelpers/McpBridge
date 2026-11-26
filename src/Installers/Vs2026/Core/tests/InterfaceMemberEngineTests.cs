using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class InterfaceMemberEngineTests
{
    private const string File = @"C:\scratch\iface.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AMemberIsAddedToAnInterfaceWithNoImplementers()
    {
        var solution = TestSolutions.Single([(File, "interface J\n{\n    void A();\n}\n")]);

        var plan = await InterfaceMemberEngine.AddAsync(solution, File, 1, "void B();");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("void B();", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnInterfaceWithAnImplementerIsRefusedAndTheImplementerIsNamed()
    {
        const string code = "interface I\n{\n    void A();\n}\nclass K : I\n{\n    public void A() { }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await InterfaceMemberEngine.AddAsync(solution, File, 1, "void B();");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("K", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADuplicateNameIsRefused()
    {
        var solution = TestSolutions.Single([(File, "interface J\n{\n    void A();\n}\n")]);

        var plan = await InterfaceMemberEngine.AddAsync(solution, File, 1, "void A(int x);");

        Assert.True(plan.Refused);
    }
}
