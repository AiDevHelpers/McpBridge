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

    // ide_edit_member with a declaration: an indexer body may change; a change of shape is refused while code uses the member.
    [Fact]
    public async Task AnIndexerBodyEditIsAccepted()
    {
        const string indexer = "class C\n{\n    public int this[int i] { get => 0; }\n}\n";
        var solution = TestSolutions.Single([(File, indexer)]);

        var plan = await MemberEditEngine.PlanAsync(solution, File, 3, new MemberEdit { NewDeclaration = "public int this[int i] { get => 1; }" }, []);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("get => 1;", (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString());
    }

    [Fact]
    public async Task AShapeChangeWhileUsedIsRefused()
    {
        const string evented = "class C\n{\n    public event System.EventHandler E;\n    void M() { E += null; }\n}\n";
        var solution = TestSolutions.Single([(File, evented)]);

        var plan = await MemberEditEngine.PlanAsync(solution, File, 3,
            new MemberEdit { NewDeclaration = "public event System.EventHandler<System.EventArgs> E;" }, []);

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("references", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task APropertyDeclarationIsRefused()
    {
        var plan = await PlanAsync("public int Y { get; }");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("declaration", System.StringComparison.Ordinal));
    }
}
