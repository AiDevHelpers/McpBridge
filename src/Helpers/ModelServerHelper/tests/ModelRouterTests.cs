using Xunit;

namespace AiDevHelpers.Helpers.ModelServerHelper.Tests;

public sealed class ModelRouterTests
{
    private static readonly Dictionary<string, string> Routes = new()
    {
        ["code"] = "qwen-coder",
        ["chat"] = "helper-chat",
        ["summarize"] = "helper-small",
        ["embed"] = "embed-model",
        ["rerank"] = "rerank-model",
    };

    [Theory]
    [InlineData("code", "qwen-coder")]
    [InlineData("chat", "helper-chat")]
    [InlineData("summarize", "helper-small")]
    [InlineData("embed", "embed-model")]
    [InlineData("rerank", "rerank-model")]
    public void Each_known_key_resolves_to_its_model(string key, string model)
    {
        var r = ModelRouter.Resolve(key, Routes);

        Assert.True(r.Ok);
        Assert.Equal(model, r.Model);
    }

    [Fact]
    public void An_unknown_key_is_refused()
    {
        var r = ModelRouter.Resolve("translate", Routes);

        Assert.False(r.Ok);
        Assert.Null(r.Model);
        Assert.Contains("translate", r.Error);
    }

    [Fact]
    public void A_known_key_with_no_route_is_refused()
    {
        var r = ModelRouter.Resolve("code", new Dictionary<string, string> { ["chat"] = "helper-chat" });

        Assert.False(r.Ok);
        Assert.Contains("code", r.Error);
    }

    [Fact]
    public void Any_model_type_name_resolves_when_the_table_defines_it()
    {
        var r = ModelRouter.Resolve("triage", new Dictionary<string, string> { ["triage"] = "small-model" });

        Assert.True(r.Ok);
        Assert.Equal("small-model", r.Model);
    }

    [Fact]
    public void An_empty_model_type_is_refused()
    {
        var r = ModelRouter.Resolve("  ", Routes);

        Assert.False(r.Ok);
    }

    [Fact]
    public void A_model_type_is_matched_case_insensitively()
    {
        var r = ModelRouter.Resolve("CODE", Routes);

        Assert.True(r.Ok);
        Assert.Equal("qwen-coder", r.Model);
    }
}
