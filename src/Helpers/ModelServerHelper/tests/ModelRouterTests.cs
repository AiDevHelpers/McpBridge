using Xunit;

namespace AiDevHelpers.Helpers.ModelServerHelper.Tests;

public sealed class ModelRouterTests
{
    private static readonly Dictionary<string, ModelTypeRoute> Types = new()
    {
        ["code"] = new("router", "qwen-coder"),
        ["hard"] = new("chat", "sonnet"),
        ["broken"] = new("nowhere", "x"),
        ["blank"] = new("router", " "),
    };

    [Fact]
    public void A_router_type_resolves_to_its_model_on_the_router()
    {
        var r = ModelRouter.Resolve("code", Types);

        Assert.True(r.Ok);
        Assert.Equal("router", r.Target);
        Assert.Equal("qwen-coder", r.Model);
    }

    [Fact]
    public void A_chat_type_resolves_to_a_hand_back_to_chat()
    {
        var r = ModelRouter.Resolve("hard", Types);

        Assert.True(r.Ok);
        Assert.Equal("chat", r.Target);
        Assert.Equal("sonnet", r.Model);
    }

    [Fact]
    public void Any_type_name_resolves_when_the_settings_define_it()
    {
        var r = ModelRouter.Resolve("triage", new Dictionary<string, ModelTypeRoute> { ["triage"] = new("router", "small-model") });

        Assert.True(r.Ok);
        Assert.Equal("small-model", r.Model);
    }

    [Fact]
    public void An_unknown_type_is_refused()
    {
        var r = ModelRouter.Resolve("translate", Types);

        Assert.False(r.Ok);
        Assert.Contains("translate", r.Error);
    }

    [Fact]
    public void An_empty_type_is_refused()
    {
        var r = ModelRouter.Resolve("  ", Types);

        Assert.False(r.Ok);
    }

    [Fact]
    public void A_target_other_than_router_or_chat_is_refused()
    {
        var r = ModelRouter.Resolve("broken", Types);

        Assert.False(r.Ok);
        Assert.Contains("nowhere", r.Error);
    }

    [Fact]
    public void A_type_with_no_model_is_refused()
    {
        var r = ModelRouter.Resolve("blank", Types);

        Assert.False(r.Ok);
    }

    [Fact]
    public void A_type_is_matched_case_insensitively()
    {
        var r = ModelRouter.Resolve("CODE", Types);

        Assert.True(r.Ok);
        Assert.Equal("qwen-coder", r.Model);
    }
}
