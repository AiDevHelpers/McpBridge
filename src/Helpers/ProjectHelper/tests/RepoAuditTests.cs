using AiDevHelpers.Common;
using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

/// <summary>The committed standards must audit this repo without errors.</summary>
public sealed class RepoAuditTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiDevHelpers.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AiDevHelpers.slnx not found above the test output folder.");
    }

    [Fact]
    public void ThisRepoAuditsWithoutErrors()
    {
        var root = RepoRoot();
        var standards = ProjectStandardsFile.Read(root).Standards;

        var report = ProjectAudit.Run(root, standards, projectFilter: null);

        var errors = report.Findings.Where(f => f.Severity == "error").Select(f => $"{f.Project} [{f.Check}] {f.Message}").ToList();
        Assert.True(errors.Count == 0, "Audit errors:\n" + string.Join("\n", errors));
    }
}
