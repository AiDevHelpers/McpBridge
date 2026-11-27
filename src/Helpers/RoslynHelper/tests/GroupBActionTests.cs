using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Group B: addMethod, addOverload, deleteMember, changeSignature. Same plan, refuse and apply behaviour through the helper's dispatcher.
public class GroupBActionTests
{
    private const string GoodSource = """
        namespace Good;

        public class Widget
        {
            public int Add(int a)
            {
                return a;
            }
        }
        """;

    private const string DependentSource = """
        namespace Dependent;

        public class Consumer
        {
            public int Read() => new Good.Widget().Add(1);
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, Func<Solution?> Current, string GoodFile, string DependentFile)> Setup(FixtureCopy fixture)
    {
        var goodFile = Path.Combine(fixture.Root, "Good", "Class1.cs");
        var dependentFile = Path.Combine(fixture.Root, "Dependent", "Class2.cs");
        File.WriteAllText(goodFile, GoodSource);
        File.WriteAllText(dependentFile, DependentSource);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        PlanActions.Register(dispatcher, () => current, s => current = s);
        return (dispatcher, () => current, goodFile, dependentFile);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json(object reply) => JsonSerializer.SerializeToElement(reply);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task change_signature_rewrites_a_caller_in_a_second_project()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, goodFile, dependentFile) = await Setup(fixture);
        var parameters = """[{"declaration":"int a","fromOld":0},{"declaration":"int b","valueForCallers":"2"}]""";
        var shown = Json(await dispatcher.InvokeAsync("changeSignature", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":5,"column":5,"parameters":{{parameters}}}""")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("changeSignature", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":5,"column":5,"parameters":{{parameters}},"confirm":true,"planHash":{{Quote(hash!)}}}""")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.Contains("int b", File.ReadAllText(goodFile));
        Assert.Contains("Add(1, 2)", File.ReadAllText(dependentFile));
    }

    [Fact]
    public async Task add_method_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, goodFile, _) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("addMethod", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":3,"newMethodText":"public int Twice() { return 2; }","members":[]}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(GoodSource, File.ReadAllText(goodFile));
    }

    [Fact]
    public async Task add_overload_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, goodFile, _) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("addOverload", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":5,"newMethodText":"public int Add(int a, int b) { return a + b; }","members":[]}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(GoodSource, File.ReadAllText(goodFile));
    }

    [Fact]
    public async Task delete_member_with_a_caller_in_another_project_is_refused_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, goodFile, dependentFile) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("deleteMember", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":5,"column":5}""")));

        Assert.False(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(GoodSource, File.ReadAllText(goodFile));
        Assert.Equal(DependentSource, File.ReadAllText(dependentFile));
    }
}
