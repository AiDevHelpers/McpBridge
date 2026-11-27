using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// renameSymbol: one rename changes the declaration and every reference, including the ones in another project.
public class RenameSymbolActionTests
{
    private const string GoodSource = """
        namespace Good;

        public class Widget
        {
            public int Count { get; set; }

            public int Read() => Count;
        }
        """;

    private const string DependentSource = """
        namespace Dependent;

        public class Consumer
        {
            public int Get() => new Good.Widget().Count;
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, string GoodFile, string DependentFile)> Setup(FixtureCopy fixture)
    {
        var goodFile = Path.Combine(fixture.Root, "Good", "Class1.cs");
        var dependentFile = Path.Combine(fixture.Root, "Dependent", "Class2.cs");
        File.WriteAllText(goodFile, GoodSource);
        File.WriteAllText(dependentFile, DependentSource);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        RenameSymbolActions.Register(dispatcher, () => current, s => current = s);
        return (dispatcher, goodFile, dependentFile);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json(object reply) => JsonSerializer.SerializeToElement(reply);

    [Fact]
    public async Task rename_changes_the_declaration_and_every_reference_in_another_project()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, goodFile, dependentFile) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("renameSymbol", Args(
            $$"""{"file":{{JsonSerializer.Serialize(goodFile)}},"line":5,"column":16,"newName":"Total","confirm":true}""")));

        Assert.False(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.Contains("public int Total", File.ReadAllText(goodFile));
        Assert.Contains("=> Total;", File.ReadAllText(goodFile));
        Assert.Contains(".Total;", File.ReadAllText(dependentFile));
        Assert.DoesNotContain("Count", File.ReadAllText(goodFile));
        Assert.DoesNotContain("Count", File.ReadAllText(dependentFile));
    }

    [Fact]
    public async Task rename_without_confirm_plans_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, goodFile, dependentFile) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("renameSymbol", Args(
            $$"""{"file":{{JsonSerializer.Serialize(goodFile)}},"line":5,"column":16,"newName":"Total"}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.Equal(GoodSource, File.ReadAllText(goodFile));
        Assert.Equal(DependentSource, File.ReadAllText(dependentFile));
    }
}
