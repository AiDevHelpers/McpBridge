using System;
using System.IO;

namespace AiDevHelpers.Bridge;

/// <summary>Compares two file-system paths by their full form, ignoring case. A malformed path matches nothing.</summary>
public static class PathCompare
{
    /// <summary>True when both paths resolve to the same full path, ignoring case; false when either is empty or malformed.</summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
