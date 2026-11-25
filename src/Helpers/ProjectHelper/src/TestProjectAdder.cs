using AiDevHelpers.Common;
using AiDevHelpers.Common.Processes;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>
/// Adds a test project beside a source project: <c>&lt;Source&gt;.Tests</c>, referencing the source, with the repo's test runner, the test
/// SDK, and the standards. It reuses <see cref="ProjectAdder"/>, so its plan and confirm rules are the same.
/// </summary>
public static class TestProjectAdder
{
    private const string TemplateShortName = "xunit";

    /// <summary>
    /// Plans the test project. The folder defaults to the solution's inferred test layout for the source's name segment; it is
    /// required when the solution gives no default.
    /// </summary>
    public static Task<AddPlanResult> PlanAsync(string root, string solution, string sourceProject, string? folder, string? solutionFolder, bool seedTest, RunCommand run, CancellationToken ct)
    {
        var sourceProjectPath = ElevationFiles.Normalize(sourceProject);
        var sourceName = Path.GetFileNameWithoutExtension(sourceProjectPath);
        var segment = sourceName[(sourceName.LastIndexOf('.') + 1)..];
        var testFolder = folder ?? SolutionLayout.Infer(Path.Combine(root, solution)).TestFolder?.Replace("{name}", segment);
        if (testFolder is null)
        {
            return Task.FromResult(new AddPlanResult(null, null, null, [], [], [Strings.Get("AddFolderRequired")], []));
        }

        var request = new AddRequest(
            root,
            solution,
            TemplateShortName,
            sourceName + ".Tests",
            testFolder,
            solutionFolder,
            "C#",
            null,
            [],
            sourceProjectPath,
            seedTest);
        return ProjectAdder.PlanAsync(request, run, ct);
    }
}
