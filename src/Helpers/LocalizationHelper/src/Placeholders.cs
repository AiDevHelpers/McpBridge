using System.Text.RegularExpressions;

namespace AiDevHelpers.Helpers.LocalizationHelper;

/// <summary>Finds the format placeholders ({0}, {name}, {0:N2}) in a resource string. Doubled braces are literal braces.</summary>
public static partial class Placeholders
{
    public static IReadOnlySet<string> Of(string text) =>
        Token().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"(?<!\{)\{[A-Za-z0-9_]+(?:[:,][^{}]*)?\}(?!\})")]
    private static partial Regex Token();
}
