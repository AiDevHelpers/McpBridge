using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>
/// The limits for noticing a script that is run over and over or hangs. The defaults can be set in the server's configuration
/// (section <c>ScriptWatch</c>) and overridden per user in the repo's gitignored <c>user.json</c> (block <c>scriptWatch</c>). A repo's
/// committed files never set them, so a cloned repo cannot loosen them.
/// </summary>
public sealed class ScriptWatchOptions
{
    /// <summary>Runs that did not finish cleanly, within the window, before a script counts as repeating.</summary>
    public int RepeatCount { get; set; } = 3;

    public int RepeatWindowMinutes { get; set; } = 10;

    /// <summary>A script that prints nothing for this long is treated as hung and stopped.</summary>
    public int NoOutputSeconds { get; set; } = 60;

    /// <summary>The longest a script may run.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>These options with the per-user values from <c>user.json</c> applied. Values that are not positive whole numbers are ignored.</summary>
    public ScriptWatchOptions WithUserOverride(string? repoRoot)
    {
        var result = new ScriptWatchOptions { RepeatCount = RepeatCount, RepeatWindowMinutes = RepeatWindowMinutes, NoOutputSeconds = NoOutputSeconds, TimeoutSeconds = TimeoutSeconds };
        try
        {
            var path = repoRoot is null ? null : AiDevHelpers.Common.RepoSettingsFile.UserFilePath(repoRoot);
            if (path is null || !File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["scriptWatch"] is not JsonObject block)
                return result;

            int? Read(string key) => block[key] is JsonValue v && v.TryGetValue<int>(out var n) && n > 0 ? n : null;
            result.RepeatCount = Read("repeatCount") ?? result.RepeatCount;
            result.RepeatWindowMinutes = Read("repeatWindowMinutes") ?? result.RepeatWindowMinutes;
            result.NoOutputSeconds = Read("noOutputSeconds") ?? result.NoOutputSeconds;
            result.TimeoutSeconds = Read("timeoutSeconds") ?? result.TimeoutSeconds;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // An unreadable file means the defaults stay.
        }

        return result;
    }
}

public enum ScriptFlag
{
    None,
    Repeating,
    Hung,
}

/// <summary>Whether a script looks stuck, and why in words the AI can pass on to the person.</summary>
public sealed record ScriptAssessment(ScriptFlag Flag, string? Reason, int RunsInWindow);

/// <summary>How a run ended.</summary>
public sealed record ScriptRunOutcome(int ExitCode, bool TimedOut, bool Silent)
{
    public bool Clean => ExitCode == 0 && !TimedOut && !Silent;
}

/// <summary>
/// Remembers, in memory for the server session, how each script (by repo, path and arguments) has been running. A script is
/// <see cref="ScriptFlag.Repeating"/> when it has run <c>RepeatCount</c> times within the window without a clean finish, and
/// <see cref="ScriptFlag.Hung"/> when its last run went silent or hit the timeout. A clean run clears the script's history.
/// </summary>
public sealed class ScriptRunTracker(Func<DateTime>? clock = null)
{
    private const int MaxKeys = 500;

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, List<(DateTime EndedUtc, ScriptRunOutcome Outcome)>> _runs = [];
    private readonly object _gate = new();

    /// <summary>A stable key for one script run with these arguments in this repo.</summary>
    public static string Key(string repoRoot, string scriptPath, IReadOnlyList<string> arguments)
    {
        var text = string.Join('\n', [repoRoot.TrimEnd('\\', '/').ToLowerInvariant(), scriptPath.Replace('\\', '/').ToLowerInvariant(), .. arguments]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public void Record(string key, ScriptRunOutcome outcome)
    {
        lock (_gate)
        {
            if (outcome.Clean)
            {
                _runs.Remove(key);
                return;
            }

            if (!_runs.TryGetValue(key, out var list))
            {
                if (_runs.Count >= MaxKeys)
                    _runs.Remove(_runs.Keys.First());

                _runs[key] = list = [];
            }

            list.Add((_now(), outcome));
            if (list.Count > 50)
                list.RemoveAt(0);
        }
    }

    public ScriptAssessment Assess(string key, ScriptWatchOptions options)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(key, out var list) || list.Count == 0)
                return new ScriptAssessment(ScriptFlag.None, null, 0);

            var window = TimeSpan.FromMinutes(options.RepeatWindowMinutes);
            var inWindow = list.Count(r => _now() - r.EndedUtc <= window);
            var last = list[^1].Outcome;

            if (last.Silent)
                return new ScriptAssessment(ScriptFlag.Hung, Strings.Format("ScriptHungSilent", options.NoOutputSeconds), inWindow);

            if (last.TimedOut)
                return new ScriptAssessment(ScriptFlag.Hung, Strings.Format("ScriptHungTimeout", options.TimeoutSeconds), inWindow);

            return inWindow >= options.RepeatCount
                ? new ScriptAssessment(ScriptFlag.Repeating, Strings.Format("ScriptRepeating", inWindow, options.RepeatWindowMinutes), inWindow)
                : new ScriptAssessment(ScriptFlag.None, null, inWindow);
        }
    }
}
