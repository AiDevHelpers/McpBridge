using System.Text.Json;
using AiDevHelpers.MCP.Core.Tools;
using AiDevHelpers.Helpers.ModelServerHelper;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Tools;

[Collection("CodeWorkTools")]
public sealed class CodeWorkMethodsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cwm-" + Guid.NewGuid().ToString("N"));

    public CodeWorkMethodsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CodeWorkTools.IdeCall = null;
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // a locked file in the temp folder is not a test failure
        }
    }

    private const string PlanWithMethod =
        "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"src/Thing.cs\",\"template\":\"Class.cs\"," +
        "\"methods\":[{\"text\":\"public void Run() { }\",\"summary\":\"Runs it.\"}]}]";

    private List<(string Action, string Json)> IdeCalls()
    {
        var calls = new List<(string, string)>();
        CodeWorkTools.IdeCall = (action, args, _) =>
        {
            calls.Add((action, JsonSerializer.Serialize(args)));
            return Task.FromResult("{\"ok\":true}");
        };
        return calls;
    }

    private static CodeWorkTools Tools()
    {
        var cw = new CodeWorkOptions { Enabled = true };
        return new CodeWorkTools(Options.Create(cw), Options.Create(new ModelServerOptions()));
    }

    private string WriteClass(string text)
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Thing.cs"), text);
        return Path.Combine(_root, "src", "Thing.cs");
    }

    [Fact]
    public async Task Start_with_confirm_adds_each_method_to_the_class_after_creating_the_file()
    {
        WriteClass("public class Thing\n{\n}\n");
        var calls = IdeCalls();

        using var doc = JsonDocument.Parse(await Tools().CodeWorkStart(_root, PlanWithMethod, "build it", sessionId: "m-add", confirm: true));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["addProjectItem", "addMethod"], calls.Select(c => c.Action));
        var add = calls[1].Json;
        Assert.Contains("public void Run() { }", add);
        Assert.Contains("\"line\":1", add);
        Assert.Contains("\"confirm\":true", add);
    }

    [Fact]
    public async Task A_file_without_a_class_is_refused_and_no_method_is_added()
    {
        WriteClass("// nothing here\n");
        var calls = IdeCalls();

        using var doc = JsonDocument.Parse(await Tools().CodeWorkStart(_root, PlanWithMethod, "build it", sessionId: "m-none", confirm: true));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(calls, c => c.Action == "addMethod");
    }

    [Fact]
    public async Task Plan_only_saves_the_methods_and_calls_no_ide()
    {
        var calls = IdeCalls();

        using var doc = JsonDocument.Parse(await Tools().CodeWorkStart(_root, PlanWithMethod, "build it", sessionId: "m-plan"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(calls);
        Assert.Contains("public void Run() { }", File.ReadAllText(Path.Combine(_root, ".aidevhelpers", "code-work", "m-plan.json")));
    }

    [Fact]
    public async Task A_method_with_no_text_is_refused_at_plan_time()
    {
        var calls = IdeCalls();
        var plan = "[{\"id\":\"s1\",\"project\":\"App\",\"file\":\"src/Thing.cs\",\"template\":\"Class.cs\",\"methods\":[{\"text\":\" \"}]}]";

        using var doc = JsonDocument.Parse(await Tools().CodeWorkStart(_root, plan, "build it", sessionId: "m-empty", confirm: true));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(calls);
    }
}
