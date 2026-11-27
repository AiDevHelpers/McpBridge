using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class CrashLoopGuardTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tray-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void TripsWhenTheLimitIsReachedInsideTheWindow()
    {
        var guard = new CrashLoopGuard(Path.Combine(_folder, "crashes.txt"), maxCrashes: 3, window: TimeSpan.FromMinutes(5));
        var start = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

        guard.RecordCrash(start);
        guard.RecordCrash(start.AddMinutes(1));

        Assert.False(guard.IsTripped(start.AddMinutes(2)));

        guard.RecordCrash(start.AddMinutes(2));

        Assert.True(guard.IsTripped(start.AddMinutes(2)));
    }

    [Fact]
    public void OldCrashesOutsideTheWindowDoNotCount()
    {
        var guard = new CrashLoopGuard(Path.Combine(_folder, "crashes.txt"), maxCrashes: 3, window: TimeSpan.FromMinutes(5));
        var start = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

        guard.RecordCrash(start);
        guard.RecordCrash(start.AddMinutes(1));
        guard.RecordCrash(start.AddMinutes(10));

        Assert.False(guard.IsTripped(start.AddMinutes(10)));
    }

    [Fact]
    public void CrashesSurviveANewGuardOnTheSameFile()
    {
        var path = Path.Combine(_folder, "crashes.txt");
        var start = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        var first = new CrashLoopGuard(path, maxCrashes: 2, window: TimeSpan.FromMinutes(5));
        first.RecordCrash(start);
        first.RecordCrash(start.AddSeconds(30));

        var second = new CrashLoopGuard(path, maxCrashes: 2, window: TimeSpan.FromMinutes(5));

        Assert.True(second.IsTripped(start.AddMinutes(1)));
    }
}

