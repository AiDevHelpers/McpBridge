using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AiDevHelpers.Helpers.RoslynHelper.Tests;

public class PlanApplierTests
{
    [Fact]
    public async Task apply_writes_changed_text_to_disk()
    {
        using var fixture = FixtureCopy.Create();
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        var snapshot = loaded.Solution;
        var document = snapshot.Projects.Single(p => p.Name == "Good").Documents.Single(d => d.Name == "Class1.cs");
        var original = await document.GetTextAsync();
        var planned = snapshot.WithDocumentText(document.Id, SourceText.From("// planned\n" + original));
        var path = Path.Combine(fixture.Root, "Good", "Class1.cs");

        var result = await PlanApplier.ApplyAsync(snapshot, planned);

        Assert.True(result.Applied, result.Refusal);
        Assert.Single(result.Written);
        Assert.StartsWith("// planned\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task apply_refuses_when_a_changed_file_was_edited_on_disk()
    {
        using var fixture = FixtureCopy.Create();
        var loaded = await new SolutionLoader().LoadAsync(fixture.SolutionPath);
        var snapshot = loaded.Solution;
        var document = snapshot.Projects.Single(p => p.Name == "Good").Documents.Single(d => d.Name == "Class1.cs");
        var original = await document.GetTextAsync();
        var planned = snapshot.WithDocumentText(document.Id, SourceText.From("// planned\n" + original));
        var path = Path.Combine(fixture.Root, "Good", "Class1.cs");
        File.AppendAllText(path, "// edited elsewhere\n");

        var result = await PlanApplier.ApplyAsync(snapshot, planned);

        Assert.False(result.Applied);
        Assert.Contains("Class1.cs", result.Refusal);
        Assert.Empty(result.Written);
        Assert.EndsWith("// edited elsewhere\n", File.ReadAllText(path));
        Assert.DoesNotContain("// planned", File.ReadAllText(path));
    }
}
