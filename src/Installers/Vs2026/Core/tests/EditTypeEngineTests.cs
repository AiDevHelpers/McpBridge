using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class EditTypeEngineTests
{
    private const string File = @"C:\scratch\type.cs";

    private static async Task<string> TextOfAsync(MethodEditPlan plan) =>
        (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Fact]
    public async Task ABaseListIsReplaced()
    {
        const string code = "interface I { }\ninterface J { }\nclass K : I\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 3, null, ["J"]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("class K : J", await TextOfAsync(plan));
    }

    [Fact]
    public async Task ModifiersAreReplaced()
    {
        const string code = "class K\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 2, "public sealed", null);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("public sealed class K", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AddingAbstractToAUsedTypeIsRefused()
    {
        const string code = "class K\n{\n}\nclass U\n{\n    K Make() => new K();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 2, "abstract", null);

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("abstract", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAttributeIsAddedBeforeTheHeader()
    {
        const string code = "class K\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 2, null, null, ["System.Serializable"]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = await TextOfAsync(plan);
        Assert.Contains("[System.Serializable]", text);
        Assert.Contains("class K", text);
    }

    [Fact]
    public async Task AnAttributeIsRemoved()
    {
        const string code = "[System.Serializable]\nclass K\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 3, null, null, null, ["Serializable"]);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.DoesNotContain("Serializable", await TextOfAsync(plan));
    }

    [Fact]
    public async Task TypeParametersAndConstraintsAreReplaced()
    {
        const string code = "class Box<T>\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 2, null, null, null, null, "<T>", "where T : class");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains("class Box<T> where T : class", await TextOfAsync(plan));
    }

    [Fact]
    public async Task AnUnknownModifierIsRefused()
    {
        const string code = "class K\n{\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await EditTypeEngine.PlanAsync(solution, File, 2, "async", null);

        Assert.True(plan.Refused);
    }
}
