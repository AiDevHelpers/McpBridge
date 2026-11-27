using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// listUnused: a read-only action. A private method nothing calls is listed; a public one is listed only with includePublic.
public class ListUnusedActionTests
{
    private const string Source = """
        namespace Good;

        public class Widget
        {
            public int Used() => Helper();

            private int Helper() => 1;

            private int Orphan() => 2;

            public int Shown() => 3;
        }
        """;

    private static async Task<ActionDispatcher> Setup(FixtureCopy fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "Good", "Class1.cs"), Source);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        PlanActions.Register(dispatcher, () => current, s => current = s);
        ListUnusedActions.Register(dispatcher, () => current);
        return dispatcher;
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task unused_private_method_is_listed_and_public_one_is_not()
    {
        using var fixture = FixtureCopy.Create();
        var dispatcher = await Setup(fixture);

        var reply = JsonSerializer.SerializeToElement(await dispatcher.InvokeAsync("listUnused", Args("{}")));

        var text = reply.GetRawText();
        Assert.Contains("Orphan", text);
        Assert.DoesNotContain("Helper", text);
        Assert.DoesNotContain("Shown", text);
    }

    [Fact]
    public async Task include_public_lists_the_unused_public_method_too()
    {
        using var fixture = FixtureCopy.Create();
        var dispatcher = await Setup(fixture);

        var reply = JsonSerializer.SerializeToElement(await dispatcher.InvokeAsync("listUnused", Args("""{"includePublic":true}""")));

        Assert.Contains("Shown", reply.GetRawText());
    }
}
