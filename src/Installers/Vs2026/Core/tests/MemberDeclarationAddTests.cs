using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

// ide_add_member with a declared indexer, event or delegate.
public sealed class MemberDeclarationAddTests
{
    private const string File = @"C:\scratch\members.cs";

    private const string Code = "class C\n{\n    public int X { get; set; }\n}\n";

    private static async Task<MethodEditPlan> PlanAsync(params string[] declarations)
    {
        var solution = TestSolutions.Single([(File, Code)]);
        var specs = declarations.Select(d => new MemberSpec { Declaration = d }).ToList();
        return await MemberAddEngine.PlanAsync(solution, File, 3, specs, true);
    }

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task AnIndexerIsAdded()
    {
        var plan = await PlanAsync("public int this[int i] { get => X; set { X = value; } }");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public int this[int i]", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnEventIsAdded()
    {
        var plan = await PlanAsync("public event System.EventHandler Changed;");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public event System.EventHandler Changed;", await TextOfAsync(plan));
    }

    [Fact]
    public async Task ADelegateIsAdded()
    {
        var plan = await PlanAsync("public delegate void Tick(int n);");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public delegate void Tick(int n);", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnEventThatClashesWithAMemberIsRefused()
    {
        var plan = await PlanAsync("public event System.EventHandler X;");

        Assert.True(plan.Refused);
    }

    [Fact]
    public async Task APropertyDeclarationIsRefused()
    {
        var plan = await PlanAsync("public int Y { get; }");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("declaration", System.StringComparison.Ordinal));
    }
}
