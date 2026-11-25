using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class TestSolutionsTests
{
    [Fact]
    public async Task ALinkedFileHasOnePathAndOneDocumentPerProject()
    {
        const string shared = @"C:\proj\Shared.cs";
        var solution = TestSolutions.Build(
        [
            new TestSolutions.ProjectSpec("Net8", [new TestSolutions.SourceFile(shared, "class S { }")]),
            new TestSolutions.ProjectSpec("NetStd", [new TestSolutions.SourceFile(shared, "class S { }")]),
        ]);

        Assert.Equal(2, TestSolutions.DocumentsAt(solution, shared).Count);
        Assert.Equal("class S { }", (await TestSolutions.TextAsync(solution, shared)).Trim());
    }

    [Fact]
    public void ASingleProjectHoldsItsFiles()
    {
        var solution = TestSolutions.Single([(@"C:\proj\A.cs", "class A { }")]);

        Assert.Single(solution.Projects);
        Assert.Single(TestSolutions.DocumentsAt(solution, @"C:\proj\A.cs"));
    }
}
