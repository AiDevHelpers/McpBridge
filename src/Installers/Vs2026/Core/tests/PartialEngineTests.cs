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
    public async Task AMemberMovesToANewPart()
    {
        const string code = "partial class P\n{\n    void A() { }\n    void B() { }\n}\n";
        var solution = TestSolutions.Single([(First, code)]);

        var plan = await PartialEngine.ExtractAsync(solution, First, 1, ["B"], "Extra");

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var result = plan.Result!;
        var original = (await result.Projects.Single().Documents.Single(d => d.FilePath == First).GetTextAsync()).ToString();
        var moved = (await result.Projects.Single().Documents.Single(d => d.Name == "P.Extra.cs").GetTextAsync()).ToString();
        Assert.DoesNotContain("void B()", original);
        Assert.Contains("void A()", original);
        Assert.Contains("partial class P", moved);
        Assert.Contains("void B()", moved);
    }

    [Fact]
    public async Task ANonPartialTypeIsRefused()
    {
        var solution = TestSolutions.Single([(First, "class P\n{\n    void One() { }\n}\n")]);

        var plan = await PartialEngine.MergeAsync(solution, First, 1);

        Assert.True(plan.Refused);
    }
}
