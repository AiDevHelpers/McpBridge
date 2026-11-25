using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class LogicalDocumentsTests
{
    private const string Shared = @"C:\proj\Shared.cs";

    [Fact]
    public void ALinkedFileIsOneLogicalDocumentWithTwoIds()
    {
        var solution = TestSolutions.Build(
        [
            new TestSolutions.ProjectSpec("Net8", [new TestSolutions.SourceFile(Shared, "class S { }")]),
            new TestSolutions.ProjectSpec("NetStd", [new TestSolutions.SourceFile(Shared, "class S { }")]),
        ]);

        Assert.Equal(2, LogicalDocuments.IdsAt(solution, Shared).Count);
    }

    [Fact]
    public async Task AnEditToALinkedFileReachesEveryTarget()
    {
        var solution = TestSolutions.Build(
        [
            new TestSolutions.ProjectSpec("Net8", [new TestSolutions.SourceFile(Shared, "class S { int a; }")]),
            new TestSolutions.ProjectSpec("NetStd", [new TestSolutions.SourceFile(Shared, "class S { int a; }")]),
        ]);
        var first = LogicalDocuments.IdsAt(solution, Shared)[0];
        var span = await solution.GetDocument(first)!.GetTextAsync();
        var change = new TextChange(new TextSpan(span.ToString().IndexOf("a;", StringComparison.Ordinal), 1), "b");
        var changes = new Dictionary<DocumentId, List<TextChange>> { [first] = [change] };

        var divergent = new List<string>();
        var edited = await LogicalDocuments.ApplyAsync(solution, changes, divergent);

        foreach (var id in LogicalDocuments.IdsAt(edited, Shared))
        {
            Assert.Contains("int b;", (await edited.GetDocument(id)!.GetTextAsync()).ToString());
        }

        Assert.Empty(divergent);
    }

    [Fact]
    public async Task LinkedCopiesWithDifferentTextAreReportedAsDivergent()
    {
        var solution = TestSolutions.Build(
        [
            new TestSolutions.ProjectSpec("Net8", [new TestSolutions.SourceFile(Shared, "class S { int a; }")]),
            new TestSolutions.ProjectSpec("NetStd", [new TestSolutions.SourceFile(Shared, "class S { int a; int c; }")]),
        ]);
        var first = LogicalDocuments.IdsAt(solution, Shared)[0];
        var change = new TextChange(new TextSpan(14, 1), "b");
        var divergent = new List<string>();

        await LogicalDocuments.ApplyAsync(solution, new Dictionary<DocumentId, List<TextChange>> { [first] = [change] }, divergent);

        Assert.Single(divergent);
    }

    [Fact]
    public void AGeneratedPathIsRefused()
    {
        Assert.NotNull(LogicalDocuments.RefusalFor(TestSolutions.Single([]), @"C:\proj\obj\Debug\Gen.g.cs"));
        Assert.NotNull(LogicalDocuments.RefusalFor(TestSolutions.Single([]), @"C:\proj\bin\Debug\X.cs"));
    }

    [Fact]
    public void AnOrdinaryPathIsNotRefused()
    {
        Assert.Null(LogicalDocuments.RefusalFor(TestSolutions.Single([(@"C:\proj\A.cs", "class A { }")]), @"C:\proj\A.cs"));
    }
}
