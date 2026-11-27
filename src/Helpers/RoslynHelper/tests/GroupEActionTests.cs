using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Group E: deleteType, moveType, renameNamespace. File moves and deletes happen on disk after the text changes are written.
public class GroupEActionTests
{
    private const string GoodSource = """
        namespace Good;

        public class Widget
        {
            public string Name => "good";
        }
        """;

    private const string DependentSource = """
        namespace Dependent;

        public class Consumer
        {
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, string GoodFile)> Setup(FixtureCopy fixture)
    {
        var goodFile = Path.Combine(fixture.Root, "Good", "Class1.cs");
        File.WriteAllText(goodFile, GoodSource);
        File.WriteAllText(Path.Combine(fixture.Root, "Dependent", "Class2.cs"), DependentSource);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        PlanActions.Register(dispatcher, () => current, s => current = s);
        return (dispatcher, goodFile);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json(object reply) => JsonSerializer.SerializeToElement(reply);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task move_type_confirmed_moves_its_file_in_an_sdk_project()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, goodFile) = await Setup(fixture);
        var shown = Json(await dispatcher.InvokeAsync("moveType", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":3,"newNamespace":"Good.Moved","moveFiles":true}""")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("moveType", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":3,"newNamespace":"Good.Moved","moveFiles":true,"confirm":true,"planHash":{{Quote(hash!)}}}""")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.False(File.Exists(goodFile));
        var movedFile = Directory.GetFiles(fixture.Root, "*.cs", SearchOption.AllDirectories)
            .Single(f => File.ReadAllText(f).Contains("namespace Good.Moved"));
        Assert.Contains("class Widget", File.ReadAllText(movedFile));
    }

    [Fact]
    public async Task delete_type_confirmed_with_delete_file_removes_its_file()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, goodFile) = await Setup(fixture);
        var shown = Json(await dispatcher.InvokeAsync("deleteType", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":3,"deleteFile":true}""")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("deleteType", Args(
            $$"""{"file":{{Quote(goodFile)}},"line":3,"deleteFile":true,"confirm":true,"planHash":{{Quote(hash!)}}}""")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.False(File.Exists(goodFile));
    }

    [Fact]
    public async Task rename_namespace_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, goodFile) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("renameNamespace", Args(
            """{"oldNamespace":"Good","newNamespace":"Better","moveFiles":false}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(GoodSource, File.ReadAllText(goodFile));
    }
}
