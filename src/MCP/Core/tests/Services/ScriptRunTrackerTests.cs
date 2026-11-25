using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ScriptRunTrackerTests : IDisposable
{
    private static readonly ScriptRunOutcome Failed = new(1, TimedOut: false, Silent: false);
    private static readonly ScriptRunOutcome Clean = new(0, TimedOut: false, Silent: false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "watch-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    public ScriptRunTrackerTests() => Directory.CreateDirectory(Path.Combine(_root, ".aidevhelpers"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ScriptRunTracker Tracker() => new(() => _now);

    [Fact]
    public void ARepeatedFailingScript_IsFlaggedOnTheThirdRunInTheWindow()
    {
        var tracker = Tracker();
        var options = new ScriptWatchOptions();
        var key = ScriptRunTracker.Key(_root, "a.ps1", []);

        tracker.Record(key, Failed);
        tracker.Record(key, Failed);
        Assert.Equal(ScriptFlag.None, tracker.Assess(key, options).Flag);

        tracker.Record(key, Failed);
        var third = tracker.Assess(key, options);

        Assert.Equal(ScriptFlag.Repeating, third.Flag);
        Assert.Equal(3, third.RunsInWindow);
        Assert.Contains("3 times in 10 minutes", third.Reason);
    }

    [Fact]
    public void RunsOutsideTheWindow_DoNotCount()
    {
        var tracker = Tracker();
        var key = ScriptRunTracker.Key(_root, "a.ps1", []);
        tracker.Record(key, Failed);
        tracker.Record(key, Failed);
        _now = _now.AddMinutes(11);

        tracker.Record(key, Failed);

        Assert.Equal(ScriptFlag.None, tracker.Assess(key, new ScriptWatchOptions()).Flag);
    }

    [Fact]
    public void TheKeyCoversTheRepoThePathAndTheArguments()
    {
        var a = ScriptRunTracker.Key(_root, "a.ps1", ["x"]);

        Assert.Equal(a, ScriptRunTracker.Key(_root + "\\", "A.ps1", ["x"]));
        Assert.NotEqual(a, ScriptRunTracker.Key(_root, "a.ps1", ["y"]));
        Assert.NotEqual(a, ScriptRunTracker.Key(_root, "b.ps1", ["x"]));

        var tracker = Tracker();
        for (var i = 0; i < 3; i++)
            tracker.Record(a, Failed);

        Assert.Equal(ScriptFlag.None, tracker.Assess(ScriptRunTracker.Key(_root, "a.ps1", ["y"]), new ScriptWatchOptions()).Flag);
    }

    [Fact]
    public void ASilentOrTimedOutRun_IsHung_AtOnce_WithTheLimitInTheReason()
    {
        var tracker = Tracker();
        var options = new ScriptWatchOptions { NoOutputSeconds = 45, TimeoutSeconds = 120 };
        var silent = ScriptRunTracker.Key(_root, "s.ps1", []);
        var slow = ScriptRunTracker.Key(_root, "t.ps1", []);

        tracker.Record(silent, new ScriptRunOutcome(-1, TimedOut: false, Silent: true));
        tracker.Record(slow, new ScriptRunOutcome(-1, TimedOut: true, Silent: false));

        Assert.Contains("nothing for 45 seconds", tracker.Assess(silent, options).Reason);
        Assert.Equal(ScriptFlag.Hung, tracker.Assess(silent, options).Flag);
        Assert.Contains("120 second limit", tracker.Assess(slow, options).Reason);
    }

    [Fact]
    public void ACleanRun_ClearsTheFlag()
    {
        var tracker = Tracker();
        var options = new ScriptWatchOptions();
        var key = ScriptRunTracker.Key(_root, "a.ps1", []);
        for (var i = 0; i < 3; i++)
            tracker.Record(key, Failed);
        Assert.Equal(ScriptFlag.Repeating, tracker.Assess(key, options).Flag);

        tracker.Record(key, Clean);

        Assert.Equal(ScriptFlag.None, tracker.Assess(key, options).Flag);
        Assert.Equal(0, tracker.Assess(key, options).RunsInWindow);
    }

    [Fact]
    public void ThePerUserOverride_WinsOverTheServerSettings_AndBadValuesAreIgnored()
    {
        File.WriteAllText(
            AiDevHelpers.Common.RepoSettingsFile.UserFilePath(_root),
            """{ "scriptWatch": { "repeatCount": 5, "noOutputSeconds": 0, "timeoutSeconds": "soon" } }""");
        var global = new ScriptWatchOptions { RepeatCount = 2, NoOutputSeconds = 30 };

        var effective = global.WithUserOverride(_root);

        Assert.Equal(5, effective.RepeatCount);
        Assert.Equal(30, effective.NoOutputSeconds);
        Assert.Equal(300, effective.TimeoutSeconds);
        Assert.Equal(2, global.RepeatCount);
    }

    [Fact]
    public void WithNoRepoOrAnUnreadableFile_TheServerSettingsStand()
    {
        var global = new ScriptWatchOptions { RepeatCount = 4 };

        Assert.Equal(4, global.WithUserOverride(null).RepeatCount);

        File.WriteAllText(AiDevHelpers.Common.RepoSettingsFile.UserFilePath(_root), "{ broken");
        Assert.Equal(4, global.WithUserOverride(_root).RepeatCount);
    }
}
