using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class RemoveUnusedEngineTests
{
    private const string File = @"C:\scratch\unused.cs";

    [Fact]
    public async Task AnUnusedMemberIsDeleted()
    {
        const string code = "class C\n{\n    int A() => 1;\n    int B() => 2;\n    int Use() => A();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var items = new List<(string File, int Line, string Kind)> { (File, 4, "member") };

        var plan = await RemoveUnusedEngine.PlanAsync(solution, items);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.DoesNotContain("int B()", text);
        Assert.Contains("int A() => 1;", text);
    }

    [Fact]
    public async Task AMemberThatIsStillUsedStopsTheWholeChange()
    {
        const string code = "class C\n{\n    int A() => 1;\n    int Use() => A();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var items = new List<(string File, int Line, string Kind)> { (File, 3, "member") };

        var plan = await RemoveUnusedEngine.PlanAsync(solution, items);

        Assert.True(plan.Refused);
    }
}
