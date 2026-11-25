using AiDevHelpers.Common;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class RuleOverrideCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sh-override-" + Guid.NewGuid().ToString("N"));

    public RuleOverrideCliTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Answer(bool yes) : IConfirmer
    {
        public int Asked { get; private set; }

        public bool Confirm(string prompt)
        {
            Asked++;
            return yes;
        }
    }

    [Fact]
    public void AYes_GrantsThePendingRequestOnce()
    {
        var id = RuleOverrides.Request(_root, "push", "main", DateTime.UtcNow);
        var output = new StringWriter();

        Assert.True(ElevLocalCommands.TryRun(["rules", "override", _root, id], output, new Answer(true), out var exit));

        Assert.Equal(0, exit);
        Assert.True(RuleOverrides.TryConsume(_root, "push", "main", DateTime.UtcNow));
        Assert.Contains("allowed once", output.ToString());
    }

    [Fact]
    public void ANo_GrantsNothing()
    {
        var id = RuleOverrides.Request(_root, "push", "main", DateTime.UtcNow);

        ElevLocalCommands.TryRun(["rules", "override", _root, id], new StringWriter(), new Answer(false), out var exit);

        Assert.Equal(1, exit);
        Assert.False(RuleOverrides.TryConsume(_root, "push", "main", DateTime.UtcNow));
    }

    [Fact]
    public void AnUnknownRequest_IsRefusedWithoutAsking()
    {
        var answer = new Answer(true);

        ElevLocalCommands.TryRun(["rules", "override", _root, "deadbeef"], new StringWriter(), answer, out var exit);

        Assert.Equal(1, exit);
        Assert.Equal(0, answer.Asked);
    }

    [Fact]
    public void AMalformedCall_IsUsage()
    {
        Assert.True(ElevLocalCommands.TryRun(["rules", "override"], new StringWriter(), new Answer(true), out var exit));
        Assert.Equal(2, exit);
    }
}
