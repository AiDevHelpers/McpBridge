using AiDevHelpers.Common;
using AiDevHelpers.Helpers.PackageHelper;

namespace AiDevHelpers.Helpers.ProjectHelper;

/// <summary>One audit result. <see cref="Severity"/> is <c>error</c>, <c>warning</c> or <c>info</c>.</summary>
public sealed record AuditFinding(string Project, string Severity, string Check, string Message);

/// <summary>The audit. <see cref="Ok"/> is false when any finding is an error.</summary>
public sealed record ProjectAuditReport(bool Ok, IReadOnlyList<AuditFinding> Findings);

/// <summary>Checks projects against the repo's standards. Read only: it never writes a project or the standards file.</summary>
public static class ProjectAudit
{
    private const string TestSdk = "Microsoft.NET.Test.Sdk";
    private const string SolutionScope = "(solution)";

    public static ProjectAuditReport Run(string root, ProjectStandards? standards, string? projectFilter)
    {
        if (standards is null)
        {
            return Report([new AuditFinding(SolutionScope, "error", "standards", Strings.Format("AuditNoStandards", ProjectStandardsFile.FilePath(root)))]);
        }

        var configPath = ProjectStandardsFile.FilePath(root);
        var projects = ProjectFiles.Read(root)
            .Where(p => projectFilter is null || string.Equals(p.Path, ElevationFiles.Normalize(projectFilter), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var findings = new List<AuditFinding>();
        foreach (var project in projects)
        {
            CheckFrameworks(project, standards, configPath, findings);
            CheckProperties(project, standards, findings);
            CheckAnalyzers(project, standards, findings);
            CheckTestSdk(project, findings);
        }

        CheckVersions(projects, findings);
        return Report(findings);
    }

    private static void CheckFrameworks(ProjectFile project, ProjectStandards standards, string configPath, List<AuditFinding> findings)
    {
        if (project.Frameworks.Count == 0)
        {
            findings.Add(new AuditFinding(project.Path, "error", "framework", Strings.Get("AuditNoFramework")));
            return;
        }

        foreach (var result in FrameworkGuard.Check(project.Path, project.Frameworks, standards, configPath))
        {
            switch (result.Status)
            {
                case GuardStatus.Refused:
                    findings.Add(new AuditFinding(project.Path, "error", "framework", result.Message ?? string.Empty));
                    break;
                case GuardStatus.Excepted:
                    findings.Add(new AuditFinding(project.Path, "info", "framework", Strings.Format("AuditFrameworkExcepted", result.Framework, project.Path)));
                    break;
            }
        }
    }

    private static void CheckProperties(ProjectFile project, ProjectStandards standards, List<AuditFinding> findings)
    {
        foreach (var (name, expected) in standards.Properties)
        {
            var found = project.Properties.TryGetValue(name, out var value) ? value : null;
            if (!string.Equals(found, expected, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new AuditFinding(project.Path, "error", "property",
                    Strings.Format("AuditPropertyMissing", name, expected, found ?? Strings.Get("AuditNotSet"))));
            }
        }
    }

    private static void CheckAnalyzers(ProjectFile project, ProjectStandards standards, List<AuditFinding> findings)
    {
        foreach (var analyzer in standards.Analyzers)
        {
            if (!project.Uses.Any(u => string.Equals(u.Package, analyzer, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(new AuditFinding(project.Path, "error", "analyzer", Strings.Format("AuditAnalyzerMissing", analyzer)));
            }
        }
    }

    private static void CheckTestSdk(ProjectFile project, List<AuditFinding> findings)
    {
        var isTest = project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || project.Name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase);
        if (isTest && !project.Uses.Any(u => string.Equals(u.Package, TestSdk, StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new AuditFinding(project.Path, "error", "test-sdk", Strings.Get("AuditTestSdkMissing")));
        }
    }

    /// <summary>A package pinned to more than one version across the audited projects, with the version that would be chosen.</summary>
    private static void CheckVersions(IReadOnlyList<ProjectFile> projects, List<AuditFinding> findings)
    {
        var uses = projects.SelectMany(p => p.Uses).ToList();
        foreach (var group in uses.Where(u => u.Version is not null).GroupBy(u => u.Package, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(u => u.Version).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            var choice = PackageVersions.Choose(group.ToList());
            findings.Add(new AuditFinding(SolutionScope, "warning", "package-version",
                Strings.Format("AuditVersionsDiffer", group.Key, choice.Version ?? Strings.Get("AuditNotSet"), choice.Reason)));
        }
    }

    private static ProjectAuditReport Report(List<AuditFinding> findings) =>
        new(findings.All(f => f.Severity != "error"), findings);
}
