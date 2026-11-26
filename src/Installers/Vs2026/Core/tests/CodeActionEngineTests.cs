using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class CodeActionEngineTests
{
    private const string File = @"C:\scratch\actions.cs";

    [Fact]
    public async Task AMissingNamespaceIsOfferedAsAUsing()
    {
        const string code =
            "namespace Lib\n{\n    public class Widget { }\n}\n" +
            "class U\n{\n    Lib.Widget Make() => new Widget();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var actions = await CodeActionEngine.ListAsync(solution, File, 7);

        Assert.NotEmpty(actions);
    }

    [Fact]
    public async Task AListedFixAddsItsUsing()
    {
        const string code =
            "namespace Lib\n{\n    public class Widget { }\n}\n" +
            "class U\n{\n    Lib.Widget Make() => new Widget();\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var listed = await CodeActionEngine.ListAsync(solution, File, 7);
        var id = (string)listed.Select(a => a.GetType().GetProperty("id")!.GetValue(a)!).First();

        var plan = await CodeActionEngine.ApplyAsync(solution, File, 7, id);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.Contains("using Lib;", text);
    }

    [Fact]
    public async Task AnUnknownIdIsRefusedAsStale()
    {
        const string code = "class U\n{\n    void M() { }\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await CodeActionEngine.ApplyAsync(solution, File, 3, "using:nowhere:Nothing");

        Assert.True(plan.Refused);
        Assert.Contains(plan.Reasons, r => r.Contains("list them again", System.StringComparison.Ordinal));
    }
}
