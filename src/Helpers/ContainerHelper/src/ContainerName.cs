using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.ContainerHelper;

/// <summary>Container name rules. A name is passed as an argument and never reaches a shell, but a leading dash would read as an option, so it is refused.</summary>
public static class ContainerName
{
    private static readonly Regex Pattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    public static bool IsValid(string? name) =>
        name is not null && !name.Contains('\n') && Pattern.IsMatch(name);
}
