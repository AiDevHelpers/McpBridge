using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class PartialEngineTests
{
    private const string First = @"C:\scratch\PartA.cs";
    private const string Second = @"C:\scratch\PartB.cs";

    [Fact]
    public async Task APartInAnotherFileIsMergedAndItsFileGoes()
    {
        var solution = TestSolutions.Single([
            (First, "partial class P\n{\n    void One() { }\n}\n"),
            (Second, "partial class P\n{\n    void Two() { }\n}\n"),
        ]);

        var plan = await PartialEngine.MergeAsync(solution, First, 1);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        Assert.Contains(Second, plan.FileDeletes);
        var merged = (await plan.Result!.Projects.Single().Documents.Single(d => d.FilePath == First).GetTextAsync()).ToString();
        Assert.Contains("void One()", merged);
        Assert.Contains("void Two()", merged);
    }

    [Fact]
    public async Task ANonPartialTypeIsRefused()
    {
        var solution = TestSolutions.Single([(First, "class P\n{\n    void One() { }\n}\n")]);

        var plan = await PartialEngine.MergeAsync(solution, First, 1);

        Assert.True(plan.Refused);
    }
}
