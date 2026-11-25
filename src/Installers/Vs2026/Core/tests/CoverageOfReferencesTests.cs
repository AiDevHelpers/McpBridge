using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class CoverageOfReferencesTests
{
    [Fact]
    public void AReferenceWithoutAMatchingCallerEditIsUncovered()
    {
        var references = new List<MethodEditEngine.Reference>
        {
            new() { File = @"C:\a.cs", Line = 3, Column = 5 },
            new() { File = @"C:\a.cs", Line = 9, Column = 1 },
        };

        var uncovered = CoverageOfReferences.Uncovered(references, [new CallerEdit(@"C:\a.cs", 9, 1, "x")]);

        Assert.Single(uncovered);
        Assert.Contains("\"line\":3", System.Text.Json.JsonSerializer.Serialize(uncovered[0]));
    }

    [Fact]
    public void AnInterfaceImplementationIsRelatedToItsInterfaceMember()
    {
        const string code =
            "interface IThing { int Value { get; } }\n" +
            "class Thing : IThing { public int Value { get; } }\n";
        var solution = TestSolutions.Single([(@"C:\t.cs", code)]);
        var compilation = solution.Projects.Single().GetCompilationAsync().Result!;
        var property = compilation.GetTypeByMetadataName("Thing")!.GetMembers("Value").OfType<Microsoft.CodeAnalysis.IPropertySymbol>().Single();

        var related = CoverageOfReferences.RelatedAsync(solution, property).Result;

        Assert.Contains(related, r => r.Contains("IThing.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void AMemberWithNoOverridesOrImplementationsHasNoRelatedMembers()
    {
        var solution = TestSolutions.Single([(@"C:\t.cs", "class Plain { public int Value { get; } }")]);
        var compilation = solution.Projects.Single().GetCompilationAsync().Result!;
        var property = compilation.GetTypeByMetadataName("Plain")!.GetMembers("Value").OfType<Microsoft.CodeAnalysis.IPropertySymbol>().Single();

        Assert.Empty(CoverageOfReferences.RelatedAsync(solution, property).Result);
    }
}
