using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class UpdateDocsEngineTests
{
    private const string File = @"C:\scratch\updocs.cs";

    [Fact]
    public async Task ASummaryAndAParamTextAreReplacedWithoutACodeChange()
    {
        const string code =
            "class C\n{\n    /// <summary>old</summary>\n    /// <param name=\"a\">old</param>\n    int M(int a) => a;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);
        var items = new[] { new DocItem("param", "a", "fresh") };

        var plan = await UpdateDocsEngine.PlanAsync(solution, File, 5, "new summary", items);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var text = (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.Contains("new summary", text);
        Assert.Contains("fresh", text);
        Assert.DoesNotContain(">old<", text);
        Assert.Contains("int M(int a) => a;", text);
    }

    [Fact]
    public async Task AMethodWithNoDocCommentIsRefused()
    {
        const string code = "class C\n{\n    int M(int a) => a;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var plan = await UpdateDocsEngine.PlanAsync(solution, File, 3, "summary", []);

        Assert.True(plan.Refused);
    }
}
