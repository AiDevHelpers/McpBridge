using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class CallerPropagatorTests
{
    private const string Code =
        "class C\n" +
        "{\n" +
        "    void Leaf() { }\n" +
        "    void Middle() { Leaf(); }\n" +
        "    void Top() { Middle(); }\n" +
        "    void Loop() { Loop(); Leaf(); }\n" +
        "}\n";

    private static async Task<(Solution Solution, IMethodSymbol Leaf)> LeafAsync()
    {
        var solution = TestSolutions.Single([(@"C:\c.cs", Code)]);
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var leaf = compilation.GetTypeByMetadataName("C")!.GetMembers("Leaf").OfType<IMethodSymbol>().Single();
        return (solution, leaf);
    }

    [Fact]
    public async Task DepthOneIsTheDirectCallers()
    {
        var (solution, leaf) = await LeafAsync();

        var callers = await CallerPropagator.CallersAsync(solution, leaf, 1);

        Assert.Equal(new[] { "Middle", "Loop" }.OrderBy(n => n).ToArray(), callers.Select(c => c.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task DepthTwoReachesTheCallersOfCallers()
    {
        var (solution, leaf) = await LeafAsync();

        var callers = await CallerPropagator.CallersAsync(solution, leaf, 2);

        Assert.Contains(callers, c => c.Name == "Top");
    }

    [Fact]
    public async Task ARecursiveCallerIsVisitedOnce()
    {
        var (solution, leaf) = await LeafAsync();

        var callers = await CallerPropagator.CallersAsync(solution, leaf, 5);

        Assert.Equal(callers.Count, callers.Select(c => c.Name).Distinct().Count());
        Assert.DoesNotContain(callers, c => c.Name == "Leaf");
    }

    [Fact]
    public async Task ZeroDepthHasNoCallers()
    {
        var (solution, leaf) = await LeafAsync();

        Assert.Empty(await CallerPropagator.CallersAsync(solution, leaf, 0));
    }
}
