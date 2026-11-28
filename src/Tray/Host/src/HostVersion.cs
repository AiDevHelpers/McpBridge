using System.Reflection;

namespace AiDevHelpers.Host;

/// <summary>The version of the running Host, for update comparisons: the assembly's informational version without its +build suffix.</summary>
public static class HostVersion
{
    public static string Current()
    {
        string? informational = typeof(HostVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(HostVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
