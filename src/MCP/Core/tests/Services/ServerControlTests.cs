using AiDevHelpers.MCP.Core.Services;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

/// <summary>These tests point the control file at a temp folder through their own variable, so they run one at a time and never touch ProgramData.</summary>
[Collection("ServerControl")]
public sealed class ServerControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "server-control-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE");

    public ServerControlTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE", Path.Combine(_root, "state.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AIDEVHELPERS_CONTROL_STATE", _previous);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void NoFile_MeansRunning()
    {
        var state = ServerControl.Read();

        Assert.True(state.IsRunning);
    }

    [Fact]
    public void WrittenStateIsReadBack()
    {
        ServerControl.Write(ServerControl.Paused, "updating aidevtools");

        var state = ServerControl.Read();

        Assert.Equal(ServerControl.Paused, state.State);
        Assert.Equal("updating aidevtools", state.Message);
        Assert.False(state.IsRunning);
        Assert.True(state.UpdatedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ResumeClearsThePause()
    {
        ServerControl.Write(ServerControl.Paused, null);
        ServerControl.Write(ServerControl.Running, null);

        Assert.True(ServerControl.Read().IsRunning);
    }

    [Fact]
    public void UnknownStateIsRejectedAndNothingIsWritten()
    {
        Assert.Throws<ArgumentException>(() => ServerControl.Write("sleeping", null));

        Assert.False(File.Exists(ServerControl.StatePath));
    }

    [Fact]
    public void ACorruptFileMeansRunningSoAServerIsNeverStuckPaused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServerControl.StatePath)!);
        File.WriteAllText(ServerControl.StatePath, "{ not json");

        Assert.True(ServerControl.Read().IsRunning);
    }

    [Fact]
    public void RunningAllowsEveryTool()
    {
        Assert.Null(ServerControlGate.Refusal("ide_build", new ControlState(ServerControl.Running, null, DateTime.UtcNow)));
    }

    [Fact]
    public void PausedRefusesToolsAndShowsTheMessage()
    {
        var refusal = ServerControlGate.Refusal("ide_build", new ControlState(ServerControl.Paused, "restarting", DateTime.UtcNow));

        Assert.NotNull(refusal);
        Assert.Contains("paused", refusal);
        Assert.Contains("restarting", refusal);
    }

    [Fact]
    public void ClosingRefusesToolsAndSaysToReconnect()
    {
        var refusal = ServerControlGate.Refusal("git_status", new ControlState(ServerControl.Closing, null, DateTime.UtcNow));

        Assert.NotNull(refusal);
        Assert.Contains("closing", refusal);
        Assert.Contains("reconnect", refusal);
    }

    [Theory]
    [InlineData("server_control_set")]
    [InlineData("server_control_status")]
    public void ControlToolsWorkWhilePaused(string tool)
    {
        Assert.Null(ServerControlGate.Refusal(tool, new ControlState(ServerControl.Paused, null, DateTime.UtcNow)));
    }

    [Fact]
    public void ControlToolNamesMatchTheRegisteredTools()
    {
        var tools = typeof(ServerControlGate).Assembly.GetType("AiDevHelpers.MCP.Core.Tools.ServerControlTools", throwOnError: true)!
            .GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0)
            .Select(m => System.Text.RegularExpressions.Regex.Replace(m.Name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant())
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(ServerControlGate.AlwaysAllowed.OrderBy(n => n), tools);
    }

    [Fact]
    public void EveryMcpToolIsClassifiedAsSafeOrDangerous()
    {
        var assembly = typeof(McpServiceCollectionExtensions).Assembly;
        var unclassified = new List<string>();
        var dangerous = new List<string>();
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length > 0))
        {
            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                         .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0))
            {
                var safe = method.GetCustomAttributes(typeof(SafeToolAttribute), false).Length;
                var danger = method.GetCustomAttributes(typeof(DangerousToolAttribute), false).Length;
                if (safe + danger != 1)
                {
                    unclassified.Add($"{type.Name}.{method.Name}");
                }
                else if (danger == 1)
                {
                    dangerous.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.Empty(unclassified);
        Assert.Contains("IdeTools.IdeKillProcess", dangerous);
        Assert.Contains("TerminalSessionTools.TerminalSession", dangerous);
        Assert.Contains("ScriptRunTools.ScriptRun", dangerous);
    }

    private sealed class FakeTray(ApprovalReply? reply, Exception? failure = null) : IApprovalChannel
    {
        public ApprovalRequest? Seen { get; private set; }

        public Task<ApprovalReply?> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Seen = request;
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(reply is null ? null : reply with { Nonce = request.Nonce, ArgumentsHash = request.ArgumentsHash });
        }
    }

    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AYesForThisCallIsAccepted()
    {
        var tray = new FakeTray(new ApprovalReply("", "", Approved: true));

        Assert.True(await ApprovalGate.ApproveAsync(tray, "terminal_session", "{\"action\":\"open\"}", () => Now, CancellationToken.None));
        Assert.Equal("terminal_session", tray.Seen!.ToolName);
    }

    [Fact]
    public async Task ANoIsRefused()
    {
        var tray = new FakeTray(new ApprovalReply("", "", Approved: false));

        Assert.False(await ApprovalGate.ApproveAsync(tray, "script_run", "{}", () => Now, CancellationToken.None));
    }

    private sealed class SilentTray : IApprovalChannel
    {
        public async Task<ApprovalReply?> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public async Task ATrayThatNeverAnswersIsRefusedAtTheLimit()
    {
        var started = DateTime.UtcNow;

        var approved = await ApprovalGate.ApproveAsync(new SilentTray(), "script_run", "{}", () => Now, CancellationToken.None, TimeSpan.FromMilliseconds(200));

        Assert.False(approved);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NoTrayMeansNoApproval()
    {
        Assert.False(await ApprovalGate.ApproveAsync(null, "script_run", "{}", () => Now, CancellationToken.None));
    }

    [Fact]
    public async Task AChannelFailureIsRefused()
    {
        var tray = new FakeTray(null, new InvalidOperationException("tray gone"));

        Assert.False(await ApprovalGate.ApproveAsync(tray, "script_run", "{}", () => Now, CancellationToken.None));
    }

    [Fact]
    public void AReplyWithAnotherNonceOrHashIsRefused()
    {
        var request = ApprovalGate.CreateRequest("script_run", "{\"a\":1}", Now);

        Assert.False(ApprovalGate.Accepts(request, new ApprovalReply("other", request.ArgumentsHash, true), Now));
        Assert.False(ApprovalGate.Accepts(request, new ApprovalReply(request.Nonce, ApprovalGate.HashOf("script_run", "{\"a\":2}"), true), Now));
        Assert.True(ApprovalGate.Accepts(request, new ApprovalReply(request.Nonce, request.ArgumentsHash, true), Now));
    }

    [Fact]
    public void AnExpiredApprovalIsRefused()
    {
        var request = ApprovalGate.CreateRequest("script_run", "{}", Now);

        Assert.False(ApprovalGate.Accepts(request, new ApprovalReply(request.Nonce, request.ArgumentsHash, true), Now + ApprovalGate.Limit + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TheDangerousToolsAreTheOnesTheListNames()
    {
        Assert.True(ToolClassification.IsDangerous("terminal_session"));
        Assert.True(ToolClassification.IsDangerous("script_run"));
        Assert.True(ToolClassification.IsDangerous("run_elevated_script"));
        Assert.True(ToolClassification.IsDangerous("ide_kill_process"));
        Assert.False(ToolClassification.IsDangerous("server_control_status"));
    }

    [Fact]
    public void ATokenPlannedInOneSessionCannotBeUsedFromAnother()
    {
        var token = "plan-" + Guid.NewGuid().ToString("N");
        PlanOwnership.Record(token, "session-a");

        Assert.True(PlanOwnership.MayUse(token, "session-a"));
        Assert.False(PlanOwnership.MayUse(token, "session-b"));
    }

    [Fact]
    public void AnUnknownTokenIsLeftToTheToolToRefuse()
    {
        Assert.True(PlanOwnership.MayUse("never-planned-" + Guid.NewGuid().ToString("N"), "session-b"));
    }

    [Fact]
    public void ARecordedTokenIsLearnedFromAToolResultWithATokenField()
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        PlanOwnership.RecordFrom($"{{\"ok\":true,\"token\":\"{token}\"}}", "session-c");

        Assert.False(PlanOwnership.MayUse(token, "session-d"));
    }

    [Fact]
    public void AResultWithoutATokenRecordsNothing()
    {
        PlanOwnership.RecordFrom("{\"ok\":true}", "session-e");
        PlanOwnership.RecordFrom("not json", "session-e");
    }
}
