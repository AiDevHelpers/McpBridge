using System.Text;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

internal sealed class FakeRunner : IScriptRunner
{
    public List<(string Path, IReadOnlyList<string> Args, string ContentAtRun)> Runs { get; } = new();

    public ScriptRunResult Run(string scriptPath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var content = File.ReadAllText(scriptPath);
        Runs.Add((scriptPath, args, content));
        return new ScriptRunResult(0, "out", "", false);
    }
}

internal sealed class FakeVisible : IVisibleScriptRunner
{
    public List<(string Path, IReadOnlyList<string> Args, string ContentAtRun)> Runs { get; } = new();

    public ScriptRunResult RunVisible(string scriptPath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Runs.Add((scriptPath, args, File.ReadAllText(scriptPath)));
        return new ScriptRunResult(0, "transcript", "", false);
    }
}

public sealed class RequestHandlerTests : IDisposable
{
    private const string Script = "scripts/debug/reset.ps1";
    private const string Approved = "Write-Output 'reset v1'";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-broker-" + Guid.NewGuid());
    private readonly string _root;
    private readonly string _data;
    private readonly string _staging;
    private readonly FakeElevation _elevation = new(true);
    private readonly FakeVolumes _volumes = new();
    private readonly FakeInspector _inspector = new();
    private readonly FakeInterpreter _interpreter = new();
    private readonly FakeSigner _signer = new();
    private readonly FakeRunner _runner = new();
    private readonly List<string> _audit = new();
    private readonly ScriptHelperConfig _config;
    private readonly ProjectRegistry _registry;
    private readonly VersionCounter _versions;
    private string _projectId = string.Empty;
    private const string Owner = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public RequestHandlerTests()
    {
        _root = Path.Combine(_base, "repo");
        _data = Path.Combine(_base, "data");
        _staging = Path.Combine(_base, "staging");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_staging);
        _config = new ScriptHelperConfig(_data);
        _registry = new ProjectRegistry(_config.RegistryPath, _elevation, _volumes, @"C:\Users\tester", currentUserSid: () => Owner);
        _versions = new VersionCounter(_config.VersionsDirectory);

        WriteFile(".elevated.json", """{ "scripts": [ { "path": "scripts/debug/*.ps1" } ] }""");
        WriteFile(Script, Approved);
        WriteFile("scripts/debug/other.ps1", "Write-Output 'other'");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private void WriteFile(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private RequestHandler Handler(IVisibleScriptRunner? visible = null) => new(
        _registry, _signer, _versions, _config, _inspector, _interpreter, _runner, _staging, _audit.Add, visible);

    private BrokerRequest VisibleRequest(string script = Script, params string[] args) =>
        new(_root, script, args, Owner, BrokerRequest.InteractiveKind);

    /// <summary>Registers the project and approves the script, as the setup steps would.</summary>
    private void RegisterAndApprove(string script = Script)
    {
        var reg = _registry.Register(_root);
        Assert.True(reg.Ok, reg.Reason);
        _projectId = reg.ProjectId!;

        var fp = FingerprintBuilder.Build(_root, script, _interpreter, _inspector);
        var allowHash = Fingerprint.Sha256Hex(File.ReadAllBytes(Path.Combine(_root, ".elevated.json")));
        var version = _versions.Get(_projectId) + 1;
        ApprovalStore.Save(
            _config.ApprovalsPath(_projectId),
            new ApprovalDocument(_projectId, _root, version, allowHash,
                new[] { new ApprovalRecord(script, fp.Fingerprint) }, Signature: ""),
            _signer);
        _versions.Bump(_projectId, version);
    }

    private BrokerRequest Request(string script = Script, params string[] args) =>
        new(_root, script, args, Owner);

    private void ApproveAllowAll(string settingsJson)
    {
        var reg = _registry.Register(_root);
        Assert.True(reg.Ok, reg.Reason);
        _projectId = reg.ProjectId!;
        Directory.CreateDirectory(Path.Combine(_root, ".aidevtools"));
        var settings = AiDevHelpers.Common.RepoSettingsFile.SettingsPath(_root);
        File.WriteAllText(settings, settingsJson);
        var version = _versions.Get(_projectId) + 1;
        AllowAllStore.Save(
            _config.AllowAllPath(_projectId),
            new AllowAllApproval(_projectId, _root, version, AiDevHelpers.Common.RepoSettingsFile.Hash(settings), ""),
            _signer);
        _versions.Bump(_projectId, version);
    }

    [Fact]
    public void AllowAll_RunsAScriptThatIsNotInTheManifestOrApproved()
    {
        ApproveAllowAll("""{"allowAll":true}""");
        WriteFile("tools/anything.ps1", "Write-Output 'free'");

        var r = Handler().Handle(Request("tools/anything.ps1"));

        Assert.True(r.Ok, r.Reason);
        Assert.Single(_runner.Runs);
    }

    [Fact]
    public void AllowAllRequestedWithoutAnApproval_UsesTheNormalChecks()
    {
        _registry.Register(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".aidevtools"));
        File.WriteAllText(AiDevHelpers.Common.RepoSettingsFile.SettingsPath(_root), """{"allowAll":true}""");
        WriteFile("tools/anything.ps1", "Write-Output 'free'");

        var r = Handler().Handle(Request("tools/anything.ps1"));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void EditingTheSettingsAfterAllowAllWasApproved_FallsBackToTheNormalChecks()
    {
        ApproveAllowAll("""{"allowAll":true}""");
        File.WriteAllText(AiDevHelpers.Common.RepoSettingsFile.SettingsPath(_root), """{"allowAll":true,"IdeTools":{}}""");
        WriteFile("tools/anything.ps1", "Write-Output 'free'");

        var r = Handler().Handle(Request("tools/anything.ps1"));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void AllowAllDoesNotLiftTheInsideTheRepoRule()
    {
        ApproveAllowAll("""{"allowAll":true}""");

        var r = Handler().Handle(Request("../outside.ps1"));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void AllowAllDoesNotLiftTheOwnerCheck()
    {
        ApproveAllowAll("""{"allowAll":true}""");
        WriteFile("tools/anything.ps1", "Write-Output 'free'");

        var r = Handler().Handle(new BrokerRequest(_root, "tools/anything.ps1", Array.Empty<string>(), "S-1-5-21-someone-else"));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    private BrokerRequest VerifyRequest(string sid = Owner) => new(_root, string.Empty, Array.Empty<string>(), sid, BrokerRequest.VerifyKind);

    [Fact]
    public void Verify_ReportsEachListedScript_AndRunsNothing()
    {
        RegisterAndApprove();

        var r = Handler().Handle(VerifyRequest());

        Assert.True(r.Ok, r.Reason);
        Assert.Empty(_runner.Runs);
        using var json = System.Text.Json.JsonDocument.Parse(r.Stdout);
        var scripts = json.RootElement.GetProperty("scripts").EnumerateArray()
            .ToDictionary(s => s.GetProperty("path").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal("Approved", scripts[Script]);
        Assert.Equal("Unsigned", scripts["scripts/debug/other.ps1"]);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Verify_ExitsZeroWhenEverythingListedIsApproved()
    {
        RegisterAndApprove();
        File.Delete(Path.Combine(_root, "scripts", "debug", "other.ps1"));

        var r = Handler().Handle(VerifyRequest());

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Verify_FromANonOwner_IsDenied()
    {
        RegisterAndApprove();

        var r = Handler().Handle(VerifyRequest("S-1-5-21-someone-else"));

        Assert.False(r.Ok);
        Assert.Contains("not the project owner", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ForAnUnregisteredProject_IsDenied()
    {
        var r = Handler().Handle(VerifyRequest());

        Assert.False(r.Ok);
        Assert.Contains("not registered", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Request_from_a_sid_that_is_not_the_owner_is_denied_and_nothing_runs()
    {
        RegisterAndApprove();

        var r = Handler().Handle(new BrokerRequest(_root, Script, Array.Empty<string>(), "S-1-5-21-someone-else"));

        Assert.False(r.Ok);
        Assert.Contains("not the project owner", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Request_with_no_sid_is_denied_and_nothing_runs()
    {
        RegisterAndApprove();

        var r = Handler().Handle(new BrokerRequest(_root, Script, Array.Empty<string>()));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Owner_sid_comparison_ignores_case()
    {
        RegisterAndApprove();

        var r = Handler().Handle(new BrokerRequest(_root, Script, Array.Empty<string>(), Owner.ToLowerInvariant()));

        Assert.True(r.Ok, r.Reason);
    }

    [Fact]
    public void Unregistered_project_is_denied_and_nothing_runs()
    {
        var r = Handler().Handle(Request());

        Assert.False(r.Ok);
        Assert.Contains("not registered", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_runner.Runs);
        Assert.Single(_audit);
    }

    [Fact]
    public void Unlisted_script_is_denied_and_nothing_runs()
    {
        RegisterAndApprove();
        WriteFile("scripts/other/evil.ps1", "Write-Output 'evil'");

        var r = Handler().Handle(Request("scripts/other/evil.ps1"));

        Assert.False(r.Ok);
        Assert.Contains("not in the allowlist", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Arguments_outside_the_allowed_pattern_are_denied()
    {
        RegisterAndApprove();

        var r = Handler().Handle(Request(Script, "-Force"));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Approved_unchanged_script_runs_from_a_staged_copy_and_returns_its_output()
    {
        RegisterAndApprove();

        var r = Handler().Handle(Request());

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("out", r.Stdout);
        var run = Assert.Single(_runner.Runs);
        Assert.False(run.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase), "must not run from the repo");
        Assert.StartsWith(_staging, run.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Approved, run.ContentAtRun.TrimEnd());
    }

    [Fact]
    public void The_staged_copy_is_removed_after_the_run()
    {
        RegisterAndApprove();

        Handler().Handle(Request());

        Assert.Empty(Directory.GetFileSystemEntries(_staging));
    }

    [Fact]
    public void Script_edited_after_approval_is_denied_and_nothing_runs()
    {
        RegisterAndApprove();
        WriteFile(Script, "Write-Output 'tampered'");

        var r = Handler().Handle(Request());

        Assert.False(r.Ok);
        Assert.Contains("re-approval needed", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_runner.Runs);
        Assert.Empty(Directory.GetFileSystemEntries(_staging));
    }

    [Fact]
    public void Dependency_edited_after_approval_is_denied_and_nothing_runs()
    {
        WriteFile(Script, ". .\\helpers.ps1");
        WriteFile("scripts/debug/helpers.ps1", "helper v1");
        RegisterAndApprove();
        WriteFile("scripts/debug/helpers.ps1", "helper v2");

        var r = Handler().Handle(Request());

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Every_decision_is_audited_allowed_and_denied()
    {
        RegisterAndApprove();

        Handler().Handle(Request());
        Handler().Handle(Request("scripts/debug/nope.ps1"));

        Assert.Equal(2, _audit.Count);
        Assert.Contains("allowed", _audit[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("denied", _audit[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dot_dot_escape_is_denied_and_nothing_runs()
    {
        RegisterAndApprove();

        var r = Handler().Handle(Request("../outside.ps1"));

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Interactive_RunsTheApprovedStagedScriptInTheVisibleRunner_NotTheCapturedOne()
    {
        RegisterAndApprove();
        var visible = new FakeVisible();

        var r = Handler(visible).Handle(VisibleRequest(Script));

        Assert.True(r.Ok, r.Reason);
        Assert.Equal("transcript", r.Stdout);
        var run = Assert.Single(visible.Runs);
        Assert.Equal(Approved, run.ContentAtRun);
        Assert.Empty(run.Args);
        Assert.Empty(_runner.Runs);
        Assert.Contains("visible", _audit.Last(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Interactive_AnUnapprovedOrUnlistedScript_IsDenied_AndNothingOpens()
    {
        RegisterAndApprove();
        var visible = new FakeVisible();

        var unlisted = Handler(visible).Handle(VisibleRequest("scripts/other/x.ps1"));
        var unapproved = Handler(visible).Handle(VisibleRequest("scripts/debug/other.ps1"));

        Assert.False(unlisted.Ok);
        Assert.False(unapproved.Ok);
        Assert.Empty(visible.Runs);
    }

    [Fact]
    public void Interactive_ARequesterThatIsNotTheOwner_IsDenied()
    {
        RegisterAndApprove();
        var visible = new FakeVisible();

        var r = Handler(visible).Handle(new BrokerRequest(_root, Script, [], "S-1-5-21-9-9-9-9", BrokerRequest.InteractiveKind));

        Assert.False(r.Ok);
        Assert.Empty(visible.Runs);
    }

    [Fact]
    public void Interactive_ABrokerWithoutAVisibleRunner_Refuses()
    {
        RegisterAndApprove();

        var r = Handler().Handle(VisibleRequest());

        Assert.False(r.Ok);
        Assert.Contains("not available", r.Reason);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public void Interactive_ScriptChangedAfterApproval_IsDenied()
    {
        RegisterAndApprove();
        WriteFile(Script, "Write-Output 'tampered'");
        var visible = new FakeVisible();

        var r = Handler(visible).Handle(VisibleRequest());

        Assert.False(r.Ok);
        Assert.Empty(visible.Runs);
    }}
