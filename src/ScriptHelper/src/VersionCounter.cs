using System.Globalization;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Per-project high-water mark for approvals versions. Stored as one file per project in an
/// admin-only directory. It only moves forward, so an older approvals file is a replay.
/// </summary>
public sealed class VersionCounter
{
    private readonly string _dir;

    public VersionCounter(string dir) => _dir = dir;

    public long Get(string projectId)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path))
            return 0;

        return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
    }

    public void Bump(string projectId, long version)
    {
        var current = Get(projectId);
        if (version < current)
            throw new InvalidOperationException($"version {version} is lower than the stored high-water mark {current}");

        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(projectId), version.ToString(CultureInfo.InvariantCulture));
    }

    private string PathFor(string projectId)
    {
        var safe = new string(projectId.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return Path.Combine(_dir, safe + ".ver");
    }
}
