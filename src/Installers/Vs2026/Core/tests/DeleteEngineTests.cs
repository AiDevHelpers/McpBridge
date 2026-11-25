using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class DeleteEngineTests
{
    private const string File = @"C:\scratch\d.cs";

    private const string Unused =
        "class C\n" +
        "{\n" +
        "    /// <summary>Old.</summary>\n" +
        "    int Gone() { return 1; }\n" +
        "    int Kept() { return 2; }\n" +
        "}\n";

    private const string Used =
        "class C\n" +
        "{\n" +
        "    int Helper() { return 1; }\n" +
        "    int Use() { return Helper(); }\n" +
        "}\n";

    private static Task<MethodEditPlan> DeleteAsync(string code, string marker, IReadOnlyList<CallerEdit>? callerEdits = null, IReadOnlyList<string>? deleteTests = null)
    {
        var (line, column) = MethodEditEngineTests.Locate(code, marker);
        return DeleteEngine.DeleteMemberAsync(
            TestSolutions.Single([(File, code)]), File, line, column,
            callerEdits ?? Array.Empty<CallerEdit>(), deleteTests ?? Array.Empty<string>(), _ => { });
    }

    [Fact]
    public async Task AnUnusedMethodIsDeletedWithItsDocComment()
    {
        var plan = await DeleteAsync(Unused, "Gone()");

        Assert.False(plan.Refused);
        var text = (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.DoesNotContain("Gone", text);
        Assert.DoesNotContain("Old.", text);
        Assert.Contains("int Kept()", text);
    }

    [Fact]
    public async Task AUsedMethodIsRefusedUntilItsCallIsRemoved()
    {
        var refused = await DeleteAsync(Used, "Helper()");
        Assert.True(refused.Refused);
        Assert.NotEmpty(refused.Uncovered);

        var (line, column) = MethodEditEngineTests.Locate(Used, "Helper(); }");
        var removed = await DeleteAsync(Used, "Helper()", [new CallerEdit(File, line, column, "0")]);
        Assert.False(removed.Refused);
        Assert.Contains("return 0;", (await removed.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString());
    }

    [Fact]
    public async Task AMemberTheTestsCallIsRefusedUnlessNamed()
    {
        const string code =
            "class C\n{\n    int Helper() { return 1; }\n}\n" +
            "class CTests\n{\n    [Fact] void T() { var c = new C(); c.Helper(); }\n}\n" +
            "public class FactAttribute : System.Attribute { }\n";

        var refused = await DeleteAsync(code, "Helper()");
        Assert.True(refused.Refused);
    }

    [Fact]
    public async Task AnOverrideIsRefused()
    {
        const string code = "class A { public virtual int V() { return 1; } }\nclass B : A { public override int V() { return 2; } }\n";

        var plan = await DeleteAsync(code, "V() { return 2;");

        Assert.True(plan.Refused);
    }
}
