using AiDevHelpers.Common;

namespace AiDevHelpers.Helpers.ProjectHelper;

public enum GuardStatus
{
    Allowed,
    Excepted,
    Refused,
}

/// <summary>The guard's decision for one framework of one project. <see cref="Message"/> is set when refused.</summary>
public sealed record GuardResult(string Project, string Framework, GuardStatus Status, string? Message);

/// <summary>
/// Decides whether a project may target a framework. The base framework, optionally with an allowed platform suffix, is allowed.
/// Anything else needs a recorded exception for that project and framework. Missing standards fail closed. Never writes a file.
/// </summary>
public static class FrameworkGuard
{
    public static IReadOnlyList<GuardResult> Check(string project, IEnumerable<string> frameworks, ProjectStandards? standards, string configPath)
    {
        var results = new List<GuardResult>();
        foreach (var framework in frameworks.Distinct(StringComparer.Ordinal))
        {
            results.Add(CheckOne(project, framework, standards, configPath));
        }

        return results;
    }

    private static GuardResult CheckOne(string project, string framework, ProjectStandards? standards, string configPath)
    {
        if (standards is null)
        {
            return new GuardResult(project, framework, GuardStatus.Refused, Strings.Format("GuardNoStandards", configPath));
        }

        if (framework == standards.BaseFramework
            || standards.AllowedPlatformSuffixes.Any(suffix => framework == standards.BaseFramework + suffix))
        {
            return new GuardResult(project, framework, GuardStatus.Allowed, null);
        }

        var normalized = ElevationFiles.Normalize(project);
        if (standards.Exceptions.Any(e => e.Project == normalized && e.Framework == framework))
        {
            return new GuardResult(project, framework, GuardStatus.Excepted, null);
        }

        return new GuardResult(project, framework, GuardStatus.Refused,
            Strings.Format("GuardRefused", framework, standards.BaseFramework, project, configPath));
    }
}
