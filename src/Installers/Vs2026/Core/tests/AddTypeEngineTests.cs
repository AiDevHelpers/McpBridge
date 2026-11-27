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
    public async Task WithTestsWritesATestClassInTheReferencingTestProject()
    {
        var solution = TestSolutions.Build(
        [
            new TestSolutions.ProjectSpec("Core", [new TestSolutions.SourceFile(Anchor, Code)], @"C:\scratch\Core\Core.csproj"),
            new TestSolutions.ProjectSpec("CoreTests", [], @"C:\scratch\CoreTests\CoreTests.csproj"),
        ]);
        var core = solution.Projects.Single(p => p.Name == "Core").Id;
        var tests = solution.Projects.Single(p => p.Name == "CoreTests").Id;
        solution = solution.AddProjectReference(tests, new Microsoft.CodeAnalysis.ProjectReference(core));
        solution = solution.GetProject(tests)!
            .AddMetadataReference(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Xunit.FactAttribute).Assembly.Location)).Solution;

        var plan = await AddTypeEngine.AddAsync(solution, Anchor, "public sealed class Widget { }", [], null, null, [], withTests: true);

        Assert.False(plan.Refused, string.Join("; ", plan.Reasons));
        var testDocument = plan.Result!.GetProject(tests)!.Documents.Single(d => d.Name == "WidgetTests.cs");
        var text = (await testDocument.GetTextAsync()).ToString();
        Assert.Contains("using Xunit;", text);
        Assert.Contains("public sealed class WidgetTests", text);
        Assert.Contains("[Fact]", text);
        Assert.Contains("Assert.Fail(\"test not written\")", text);
    }

    [Fact]
    public async Task AnExistingTypeNameIsRefused()
    {
        var solution = TestSolutions.Single([(Anchor, Code)]);

        var plan = await AddTypeEngine.AddAsync(solution, Anchor, "public class A { }", [], null, null, []);

        Assert.True(plan.Refused);
    }
}
