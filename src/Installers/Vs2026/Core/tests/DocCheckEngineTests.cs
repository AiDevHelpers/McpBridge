using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class DocCheckEngineTests
{
    private const string File = @"C:\scratch\docs.cs";

    [Fact]
    public async Task AParameterWithNoTagIsReported()
    {
        const string code =
            "class C\n{\n    /// <summary>x</summary>\n    /// <param name=\"a\">a</param>\n    /// <returns>r</returns>\n    int M(int a, int b) => a + b;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var found = await DocCheckEngine.CheckAsync(solution, File);

        var report = Assert.Single(found);
        var missing = report.GetType().GetProperty("missing_param")!.GetValue(report) as System.Collections.IEnumerable;
        Assert.Contains("b", missing!.Cast<object>().Select(o => o.ToString()));
    }

    [Fact]
    public async Task AMethodWithCompleteDocsIsNotReported()
    {
        const string code =
            "class C\n{\n    /// <summary>x</summary>\n    /// <param name=\"a\">a</param>\n    /// <returns>r</returns>\n    int M(int a) => a;\n}\n";
        var solution = TestSolutions.Single([(File, code)]);

        var found = await DocCheckEngine.CheckAsync(solution, File);

        Assert.Empty(found);
    }
}
