using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class GenerateMembersEngineTests
{
    private const string File = @"C:\scratch\members.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AHashCodeCombinesTheMembers()
    {
        const string code = "class C\n{\n    int A;\n    string B { get; set; }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await GenerateMembersEngine.PlanAsync(solution, File, 1, "hashcode", ["A", "B"]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public override int GetHashCode()", await TextOfAsync(plan));
        var text = await TextOfAsync(plan);
        Assert.Contains("hash = hash * 23 +", text);
        Assert.Contains("GetHashCode(B);", text);
    }

    [Fact]
    public async Task AToStringShowsEachMember()
    {
        const string code = "class C\n{\n    int A;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await GenerateMembersEngine.PlanAsync(solution, File, 1, "tostring", ["A"]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public override string ToString()", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnExistingEqualsIsRefused()
    {
        const string code = "class C\n{\n    int A;\n    public override bool Equals(object? o) => false;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await GenerateMembersEngine.PlanAsync(solution, File, 1, "equals", ["A"]);

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task AnUnknownKindIsRefused()
    {
        const string code = "class C\n{\n    int A;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await GenerateMembersEngine.PlanAsync(solution, File, 1, "clone", ["A"]);

        Assert.True(plan.Refused);
    }
}
