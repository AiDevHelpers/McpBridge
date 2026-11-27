using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Unsaved buffers: the editor sends documents: [{path, text}]. A plan reads the buffer text, not the disk. A buffer file is never
// written by the helper; its edits come back in the reply as "unsaved" for the editor to apply.
public class UnsavedBufferActionTests
{
    private const string DiskSource = """
        namespace Good;

        public class Widget
        {
            public int Twice(int a) => a * 2;
        }
        """;

    // The buffer has one more method than the disk: Thrice exists only in the editor, on line 7.
    private const string BufferSource = """
        namespace Good;

        public class Widget
        {
            public int Twice(int a) => a * 2;

            public int Thrice(int a) => a * 3;
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, string File)> Setup(FixtureCopy fixture)
    {
        var file = Path.Combine(fixture.Root, "Good", "Class1.cs");
        File.WriteAllText(file, DiskSource);
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
    public async Task a_plan_reads_the_buffer_text_not_the_disk()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("deleteMember", Args(
            $$"""{"file":{{Quote(file)}},"line":7,"column":5,"documents":[{"path":{{Quote(file)}},"text":{{Quote(BufferSource)}}}]}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.Equal(DiskSource, File.ReadAllText(file));
    }

    [Fact]
    public async Task confirmed_plan_for_a_dirty_file_returns_its_edits_and_writes_nothing_to_disk()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, file) = await Setup(fixture);
        var documents = $$"""[{"path":{{Quote(file)}},"text":{{Quote(BufferSource)}}}]""";
        var shown = Json(await dispatcher.InvokeAsync("deleteMember", Args(
            $$"""{"file":{{Quote(file)}},"line":7,"column":5,"documents":{{documents}}}""")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("deleteMember", Args(
            $$"""{"file":{{Quote(file)}},"line":7,"column":5,"documents":{{documents}},"confirm":true,"planHash":{{Quote(hash!)}}}""")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.Equal(DiskSource, File.ReadAllText(file));
        Assert.True(reply.TryGetProperty("unsaved", out var unsaved), reply.GetRawText());
        Assert.Equal(JsonValueKind.Array, unsaved.ValueKind);
        var edit = unsaved.EnumerateArray().Single();
        Assert.Equal(file, edit.GetProperty("path").GetString());
        Assert.DoesNotContain("Thrice", edit.GetProperty("text").GetString());
    }
}
