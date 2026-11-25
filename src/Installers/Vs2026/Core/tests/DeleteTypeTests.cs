using Xunit;

namespace AiDevHelpers.Installers.Vs2026.Core.Tests;

public sealed class DeleteTypeTests
{
    private const string File = @"C:\scratch\dt.cs";

    private static Task<MethodEditPlan> DeleteTypeAsync(string code, string marker, bool deleteFile = false, IReadOnlyList<CallerEdit>? callerEdits = null)
    {
        var (line, column) = MethodEditEngineTests.Locate(code, marker);
        return DeleteEngine.DeleteTypeAsync(TestSolutions.Single([(File, code)]), File, line, column, callerEdits ?? Array.Empty<CallerEdit>(), Array.Empty<string>(), deleteFile);
    }

    [Fact]
    public async Task AnUnusedTypeIsDeleted()
    {
        const string code = "class Keep { }\nclass Gone { int x; }\n";

        var plan = await DeleteTypeAsync(code, "class Gone");

        Assert.False(plan.Refused);
        var text = (await plan.Result!.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        Assert.DoesNotContain("Gone", text);
        Assert.Contains("class Keep", text);
    }

    [Fact]
    public async Task AUsedTypeIsRefusedUntilItsReferenceIsCovered()
    {
        const string code = "class Gone { }\nclass User { Gone g; }\n";

        var refused = await DeleteTypeAsync(code, "class Gone");
        Assert.True(refused.Refused);
        Assert.NotEmpty(refused.Uncovered);

        var (line, column) = MethodEditEngineTests.Locate(code, "Gone g");
        var covered = await DeleteTypeAsync(code, "class Gone", callerEdits: [new CallerEdit(File, line, column, "object")]);
        Assert.False(covered.Refused);
    }

    [Fact]
    public async Task DeletingTheOnlyTypeOfAFilePlansItsDelete()
    {
        const string code = "using System;\nclass Only { }\n";

        var plan = await DeleteTypeAsync(code, "class Only", deleteFile: true);

        Assert.False(plan.Refused);
        Assert.Equal(File, Assert.Single(plan.FileDeletes));
    }

    [Fact]
    public async Task AFileWithOtherTypesIsNotPlannedForDelete()
    {
        const string code = "class Gone { }\nclass Other { }\n";

        var plan = await DeleteTypeAsync(code, "class Gone", deleteFile: true);

        Assert.Empty(plan.FileDeletes);
    }
}
