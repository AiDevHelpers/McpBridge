using System.IO;
using System.Text.RegularExpressions;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.Tray;

/// <summary>
/// Checks a manifest before the page shows it. The manifests are written by other processes, so their values are untrusted.
/// Returns the reason a manifest is refused, or null when it is accepted.
/// </summary>
public static class InstanceValidator
{
    private const int MaxPathLength = 1024;
    private static readonly Regex PipeName = new(@"^AiDevHelpers\.(?:VS|VSCode)\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? Check(VsInstance instance)
    {
        if (instance.Pid <= 0)
        {
            return "pid is not positive";
        }

        if (instance.Ide is not ("VisualStudio" or "VsCode"))
        {
            return "unknown IDE";
        }

        var match = PipeName.Match(instance.PipeName);
        if (!match.Success || match.Groups[1].Value != instance.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return "pipe name does not match the pid";
        }

        if (instance.SolutionPath is null)
        {
            return null;
        }

        if (instance.SolutionPath.Length > MaxPathLength)
        {
            return "path is too long";
        }

        if (instance.SolutionPath.Any(char.IsControl))
        {
            return "path has a control character";
        }

        return Path.IsPathRooted(instance.SolutionPath) ? null : "path is not absolute";
    }
}

