using System.Text;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>Builds a Windows command line from separate arguments, for the one place that must pass a single string (a relaunch with the shell).</summary>
public static class WindowsArguments
{
    /// <summary>Quotes one argument: backslashes before a double quote, and at the end, are doubled; each double quote gets a backslash.</summary>
    public static string Quote(string token)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in token)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    public static string Join(IEnumerable<string> tokens) => string.Join(' ', tokens.Select(Quote));
}
