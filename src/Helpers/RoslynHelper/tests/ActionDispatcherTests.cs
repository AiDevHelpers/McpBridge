using System.Text.Json;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class ActionDispatcherTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task registered_action_receives_its_args_and_returns_its_result()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.Register("echo", args => Task.FromResult<object>(new { value = args.GetProperty("value").GetString() }));

        var result = await dispatcher.InvokeAsync("echo", Args("""{"value":"hi"}"""));

        Assert.Equal("hi", JsonSerializer.SerializeToElement(result).GetProperty("value").GetString());
    }

    [Fact]
    public async Task unknown_action_is_refused_with_its_name()
    {
        var dispatcher = new ActionDispatcher();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync("nope", default));

        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public void registering_the_same_action_twice_is_refused()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.Register("echo", _ => Task.FromResult<object>(new { }));

        Assert.Throws<InvalidOperationException>(() => dispatcher.Register("echo", _ => Task.FromResult<object>(new { })));
    }

    [Fact]
    public void has_reports_only_registered_actions()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.Register("echo", _ => Task.FromResult<object>(new { }));

        Assert.True(dispatcher.Has("echo"));
        Assert.False(dispatcher.Has("missing"));
    }
}
