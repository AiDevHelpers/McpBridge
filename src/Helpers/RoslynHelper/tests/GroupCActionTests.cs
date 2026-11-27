using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Group C: editType, addType, addInterfaceMember, implementMembers, generateConstructor. Same plan, refuse and apply behaviour through the helper's dispatcher.
public class GroupCActionTests
{
    private const string Source = """
        namespace Good;

        public interface IGreeter
        {
            string Greet(string name);
        }

        public class Greeter
        {
            public string Name { get; set; }
        }

        public class Plain : IGreeter
        {
        }

        public interface IEmpty
        {
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, string File)> Setup(FixtureCopy fixture)
    {
        var file = Path.Combine(fixture.Root, "Good", "Class1.cs");
        File.WriteAllText(file, Source);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        PlanActions.Register(dispatcher, () => current, s => current = s);
        return (dispatcher, file);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json(object reply) => JsonSerializer.SerializeToElement(reply);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task implement_members_confirmed_with_the_shown_hash_stubs_the_interface_member()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);
        var shown = Json(await dispatcher.InvokeAsync("implementMembers", Args(
            $$"""{"file":{{Quote(file)}},"line":13,"mode":"interface","members":[]}""")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("implementMembers", Args(
            $$"""{"file":{{Quote(file)}},"line":13,"mode":"interface","members":[],"confirm":true,"planHash":{{Quote(hash!)}}}""")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.Equal(2, File.ReadAllText(file).Split("Greet(string name)").Length - 1);
    }

    [Fact]
    public async Task edit_type_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("editType", Args(
            $$"""{"file":{{Quote(file)}},"line":8,"modifiers":"public sealed"}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task add_type_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("addType", Args(
            $$"""{"anchorFile":{{Quote(file)}},"declaration":"public class Other { }","usings":[]}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task add_interface_member_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("addInterfaceMember", Args(
            $$"""{"file":{{Quote(file)}},"line":17,"declaration":"int Count { get; }"}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task generate_constructor_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("generateConstructor", Args(
            $$"""{"file":{{Quote(file)}},"line":8,"members":["Name"],"accessibility":"public"}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }
}
