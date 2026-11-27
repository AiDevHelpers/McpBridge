using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Group A: editMethod, editMember, addMember. Same plan, refuse and apply behaviour through the helper's dispatcher.
public class GroupAActionTests
{
    private const string Source = """
        namespace Good;

        public class Widget
        {
            public string Name => "good";

            public string Describe()
            {
                return "good";
            }
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, Func<Solution?> Current, string File)> Setup(FixtureCopy fixture)
    {
        var file = Path.Combine(fixture.Root, "Good", "Class1.cs");
        File.WriteAllText(file, Source);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        PlanActions.Register(dispatcher, () => current, s => current = s);
        return (dispatcher, () => current, file);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json(object reply) => JsonSerializer.SerializeToElement(reply);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private const string NewMethod = "public string Describe() { return \"changed\"; }";

    [Fact]
    public async Task edit_method_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("editMethod", Args(
            $$"""{"file":{{Quote(file)}},"line":9,"column":5,"newMethodText":{{Quote(NewMethod)}}}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, reply.GetProperty("check").GetProperty("plan_hash").ValueKind);
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task edit_method_confirmed_with_the_shown_hash_writes_to_disk()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, current, file) = await Setup(fixture);
        var shown = Json(await dispatcher.InvokeAsync("editMethod", Args(
            $$"""{"file":{{Quote(file)}},"line":9,"column":5,"newMethodText":{{Quote(NewMethod)}}}""")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("editMethod", Args(
            $$"""{"file":{{Quote(file)}},"line":9,"column":5,"newMethodText":{{Quote(NewMethod)}},"confirm":true,"planHash":{{Quote(hash!)}}}""")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.Contains("\"changed\"", File.ReadAllText(file));
        var documentId = current()!.GetDocumentIdsWithFilePath(file).Single();
        var cached = await current()!.GetDocument(documentId)!.GetTextAsync();
        Assert.Contains("\"changed\"", cached.ToString());
    }

    [Fact]
    public async Task edit_method_with_a_stale_hash_is_refused_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("editMethod", Args(
            $$"""{"file":{{Quote(file)}},"line":9,"column":5,"newMethodText":{{Quote(NewMethod)}},"confirm":true,"planHash":"stale"}""")));

        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.False(reply.GetProperty("planned").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task add_member_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("addMember", Args(
            $$"""{"file":{{Quote(file)}},"line":3,"members":[{"name":"Count","type":"int","accessibility":"public"}]}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task edit_member_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("editMember", Args(
            $$"""{"file":{{Quote(file)}},"line":5,"newSummary":"Gives the name."}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }
}
