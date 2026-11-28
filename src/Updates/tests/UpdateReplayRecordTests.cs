using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class UpdateReplayRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "updreplay-" + Guid.NewGuid().ToString("N"));
    private readonly string _packageA;
    private readonly string _packageB;

    public UpdateReplayRecordTests()
    {
        _packageA = Path.Combine(_root, "pkg-a");
        _packageB = Path.Combine(_root, "pkg-b");
        Directory.CreateDirectory(_packageA);
        Directory.CreateDirectory(_packageB);
        File.WriteAllText(Path.Combine(_packageA, UpdateManifest.FileName), "{\"Version\":\"2.0.0\",\"Files\":[]}");
        File.WriteAllText(Path.Combine(_packageB, UpdateManifest.FileName), "{\"Version\":\"2.1.0\",\"Files\":[]}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // After a package is applied, offering the same package again (for example after a rollback) is a replay and is refused.
    [Fact]
    public async Task ReplayOfAppliedPackageRejected()
    {
        var record = new UpdateReplayRecord(_root);
        Assert.False(await record.IsReplayAsync(_packageA, CancellationToken.None));

        await record.RecordAsync(_packageA, CancellationToken.None);

        Assert.True(await record.IsReplayAsync(_packageA, CancellationToken.None));
    }

    [Fact]
    public async Task DifferentPackageIsNotAReplay()
    {
        var record = new UpdateReplayRecord(_root);
        await record.RecordAsync(_packageA, CancellationToken.None);

        Assert.False(await record.IsReplayAsync(_packageB, CancellationToken.None));
    }
}
