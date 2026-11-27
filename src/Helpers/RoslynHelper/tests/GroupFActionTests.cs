using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

// Group F: tddAddTest, tddUpdateTest. The test goes into the test file the caller names, beside the method's file. The test
// attribute is a local class named Fact, so the fixture needs no test framework reference.
public class GroupFActionTests
{
    private const string ClassSource = """
        namespace Good;

        public class FactAttribute : System.Attribute
        {
        }

        public class Widget
        {
            public int Twice(int a) => a * 2;
        }
        """;

    private const string TestSource = """
        namespace Good;

        public class WidgetTests
        {
            [Fact]
            public void Existing()
            {
            }
        }
        """;

    private static async Task<(ActionDispatcher Dispatcher, string ClassFile, string TestFile)> Setup(FixtureCopy fixture)
    {
        var classFile = Path.Combine(fixture.Root, "Good", "Class1.cs");
        var testFile = Path.Combine(fixture.Root, "Good", "WidgetTests.cs");
        File.WriteAllText(classFile, ClassSource);
        File.WriteAllText(testFile, TestSource);
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        Solution? current = loaded.Solution;
        var dispatcher = new ActionDispatcher();
        PlanActions.Register(dispatcher, () => current, s => current = s);
        return (dispatcher, classFile, testFile);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json(object reply) => JsonSerializer.SerializeToElement(reply);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task tdd_add_test_confirmed_writes_the_method_and_the_test_beside_it()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, classFile, testFile) = await Setup(fixture);
        var args = $$"""{"testFile":{{Quote(testFile)}},"testLine":3,"testMethodText":"[Fact] public void Triple_triples() { var value = new Widget().Triple(1); }","methodFile":{{Quote(classFile)}},"methodLine":7,"methodText":"public int Triple(int a)","members":[]""";
        var shown = Json(await dispatcher.InvokeAsync("tddAddTest", Args(args + "}")));
        var hash = shown.GetProperty("check").GetProperty("plan_hash").GetString();

        var reply = Json(await dispatcher.InvokeAsync("tddAddTest", Args(args + ",\"confirm\":true,\"planHash\":" + Quote(hash!) + "}")));

        Assert.True(reply.GetProperty("applied").GetBoolean(), reply.GetRawText());
        Assert.Contains("Triple(int a)", File.ReadAllText(classFile));
        Assert.Contains("Triple_triples", File.ReadAllText(testFile));
    }

    [Fact]
    public async Task tdd_add_test_with_the_test_in_the_method_file_is_refused()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, classFile, _) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("tddAddTest", Args(
            $$"""{"testFile":{{Quote(classFile)}},"testLine":7,"testMethodText":"[Fact] public void Triple_triples() { }","methodFile":{{Quote(classFile)}},"methodLine":7,"methodText":"public int Triple(int a)"}""")));

        Assert.False(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.Equal(ClassSource, File.ReadAllText(classFile));
    }

    [Fact]
    public async Task tdd_update_test_without_confirm_returns_the_plan_and_writes_nothing()
    {
        using var fixture = FixtureCopy.Create();
        var (dispatcher, _, testFile) = await Setup(fixture);

        var reply = Json(await dispatcher.InvokeAsync("tddUpdateTest", Args(
            $$"""{"file":{{Quote(testFile)}},"line":6,"newBody":"{ var value = new Widget().Twice(1); }"}""")));

        Assert.True(reply.GetProperty("planned").GetBoolean(), reply.GetRawText());
        Assert.False(reply.GetProperty("applied").GetBoolean());
        Assert.Equal(TestSource, File.ReadAllText(testFile));
    }
}
