using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class OrganizeFileEngineTests
{
    private const string File = @"C:\scratch\Many.cs";

    [Fact]
    public async Task ASplitMovesTheTypeToItsOwnFile()
    {
        const string code = "namespace N\n{\n    class A { }\n    class B { }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await OrganizeFileEngine.PlanAsync(solution, File, 4, "split");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var result = plan.Result!;
        var original = (await result.Projects.Single().Documents.Single(d => d.Name == "Many.cs").GetTextAsync()).ToString();
        var moved = (await result.Projects.Single().Documents.Single(d => d.Name == "B.cs").GetTextAsync()).ToString();
        Assert.DoesNotContain("class B", original);
        Assert.Contains("class A", original);
        Assert.Contains("class B", moved);
        Assert.Contains("namespace N", moved);
    }

    [Fact]
    public async Task ASingleTypeFileIsRenamedToItsType()
    {
        const string code = "class Widget { }\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await OrganizeFileEngine.PlanAsync(solution, File, 1, "rename");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var move = Assert.Single(plan.FileMoves);
        Assert.EndsWith("Widget.cs", move.To, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASplitOfAOneTypeFileIsRefused()
    {
        const string code = "class Only { }\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await OrganizeFileEngine.PlanAsync(solution, File, 1, "split");

        Assert.True(plan.Refused);
    }
}
