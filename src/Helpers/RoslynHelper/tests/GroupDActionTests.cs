using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Group D: addEnumMember, editEnumMember, threadParameter. Same plan, refuse and apply behaviour through the helper's dispatcher.
public class GroupDActionTests
{
    private const string Source = """
        namespace Good;

        public enum Color
        {
            Red = 1,
            Green = 2,
        }

        internal enum Shade
        {
            Dark = 1,
        }

        public class Chain
        {
            public int Run(int x)
            {
                return x;
            }
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
    public async Task edit_enum_member_value_is_blocked_on_a_public_enum_without_allow_value_change()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("editEnumMember", Args(
            $$"""{"file":{{Quote(file)}},"line":5,"value":"7","confirm":true,"planHash":"any"}""")));

        Assert.False(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task edit_enum_member_value_with_allow_value_change_plans_without_writing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("editEnumMember", Args(
            $$"""{"file":{{Quote(file)}},"line":5,"value":"7","allowValueChange":true}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task add_enum_member_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("addEnumMember", Args(
            $$"""{"file":{{Quote(file)}},"line":9,"name":"Light","value":"3"}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task thread_parameter_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("threadParameter", Args(
            $$"""{"file":{{Quote(file)}},"line":16,"column":5,"declaration":"int y","valueAtTop":"0","depth":1}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, File.ReadAllText(file));
    }
}
