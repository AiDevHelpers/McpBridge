using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class AddTypeEngineTests
{
    private const string Anchor = @"C:\scratch\Anchor.cs";

    private const string Code = "namespace N\n{\n    class A { }\n}\n";

    [Fact]
    public async Task ANewTypeIsWrittenBesideTheAnchorInItsNamespace()
    {
        var solution = TestSolutions.Single([(Anchor, Code)]);

        var plan = await AddTypeEngine.AddAsync(solution, Anchor, "public sealed class Widget { }", [], null, "A widget.", []);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var created = plan.Result!.Projects.Single().Documents.Single(d => d.Name == "Widget.cs");
        var text = (await created.GetTextAsync()).ToString();
        Assert.Contains("namespace N", text);
        Assert.Contains("/// <summary>A widget.</summary>", text);
        Assert.Contains("public sealed class Widget { }", text);
    }

    [Fact]
    public async Task AnExistingTypeNameIsRefused()
    {
        var solution = TestSolutions.Single([(Anchor, Code)]);

        var plan = await AddTypeEngine.AddAsync(solution, Anchor, "public class A { }", [], null, null, []);

        Assert.True(plan.Refused);
    }
}
