using System.Text.RegularExpressions;
using AiDevHelpers.Helpers.LogHelper.Models;

namespace AiDevHelpers.Helpers.LogHelper.Filtering;

/// <summary>
/// Builds regexes for untrusted input. Always uses <see cref="RegexOptions.NonBacktracking"/>, so
/// matching is linear-time; constructs it cannot run (lookaround, backreferences) are rejected
/// with a message that names the construct.
/// </summary>
public static class SafeRegex
{
    public static bool TryCreate(string pattern, LogHelperOptions options, out Regex? regex, out string? error)
    {
        regex = null;
        error = null;

        if (pattern.Length > options.MaxRegexLength)
        {
            error = Strings.Format("PatternTooLong", pattern.Length, options.MaxRegexLength);
            return false;
        }

        try
        {
            regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            return true;
        }
        catch (NotSupportedException ex)
        {
            error = ClassifyUnsupported(pattern, ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            error = Strings.Format("InvalidRegex", ex.Message);
            return false;
        }
    }

    private static string ClassifyUnsupported(string pattern, string message)
    {
        if (pattern.Contains("(?=", StringComparison.Ordinal)
            || pattern.Contains("(?!", StringComparison.Ordinal)
            || pattern.Contains("(?<=", StringComparison.Ordinal)
            || pattern.Contains("(?<!", StringComparison.Ordinal))
        {
            return Strings.Get("UnsupportedLookaround");
        }

        return Strings.Format("UnsupportedBackreference", message);
    }
}
