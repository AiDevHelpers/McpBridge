using AiDevHelpers.Helpers.RoslynHelper;
using Microsoft.CodeAnalysis;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class SolutionLoaderTests
{
    [Fact]
    public async Task Fixture_solution_lists_Good_and_Dependent_ok_and_Broken_failed_with_error()
    {
        using var fixture = FixtureCopy.Create();
        var loader = new SolutionLoader();

        var loaded = await loader.LoadAsync(fixture.SolutionPath);

        Assert.Equal(3, loaded.Projects.Count);
        var good = loaded.Projects.Single(p => p.Project == Path.Combine("Good", "Good.csproj"));
        var dependent = loaded.Projects.Single(p => p.Project == Path.Combine("Dependent", "Dependent.csproj"));
        var broken = loaded.Projects.Single(p => p.Project == Path.Combine("Broken", "Broken.csproj"));
        Assert.True(good.Ok);
        Assert.Null(good.Error);
        Assert.True(dependent.Ok);
        Assert.Null(dependent.Error);
        Assert.False(broken.Ok);
        Assert.False(string.IsNullOrWhiteSpace(broken.Error));
    }

    [Fact]
    public async Task Second_load_of_the_same_path_returns_the_same_Solution_instance()
    {
        using var fixture = FixtureCopy.Create();
        var loader = new SolutionLoader();

        var first = await loader.LoadAsync(fixture.SolutionPath);
        var second = await loader.LoadAsync(fixture.SolutionPath);

        Assert.Same(first.Solution, second.Solution);
    }

    [Fact]
    public async Task Dependent_compilation_resolves_Goods_class_without_errors_in_Class2()
    {
        using var fixture = FixtureCopy.Create();
        var loader = new SolutionLoader();

        var loaded = await loader.LoadAsync(fixture.SolutionPath);

        var good = loaded.Solution.Projects.Single(p => p.Name == "Good");
        var dependent = loaded.Solution.Projects.Single(p => p.Name == "Dependent");
        Assert.Contains(dependent.ProjectReferences, r => r.ProjectId == good.Id);
        var compilation = await dependent.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree?.FilePath.EndsWith("Class2.cs", StringComparison.OrdinalIgnoreCase) == true)
            .Select(d => d.ToString())
            .ToList();
        Assert.Empty(errors);
    }
}
