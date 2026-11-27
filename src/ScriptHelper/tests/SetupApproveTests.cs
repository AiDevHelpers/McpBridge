using System.Text;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

internal sealed class FakeConfirmer : IConfirmer
{
    public bool Answer { get; set; } = true;
    public int Calls { get; private set; }
    public string? LastPrompt { get; private set; }

    public bool Confirm(string prompt)
    {
        Calls++;
        LastPrompt = prompt;
        return Answer;
    }
}

public sealed class ApprovalLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-load-" + Guid.NewGuid());
    private readonly FakeSigner _signer = new();

    public ApprovalLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string FilePath => Path.Combine(_dir, "approvals.json");

    private static ApprovalDocument Doc() =>
        new("proj-a", @"C:\repo", 3, "AL-1", new[] { new ApprovalRecord("scripts/a.ps1", "FP-1") }, "");

    [Fact]
    public void Missing_file_loads_as_no_approvals_yet()
    {
        var r = ApprovalStore.TryLoad(FilePath, _signer);

        Assert.True(r.Ok);
        Assert.Null(r.Document);
    }

    [Fact]
    public void Valid_file_loads_with_its_document()
    {
        ApprovalStore.Save(FilePath, Doc(), _signer);

        var r = ApprovalStore.TryLoad(FilePath, _signer);

        Assert.True(r.Ok);
        Assert.Equal(3, r.Document!.Version);
        Assert.Equal("scripts/a.ps1", Assert.Single(r.Document.Scripts).Path);
    }

    [Fact]
    public void Tampered_file_fails_to_load_with_a_signature_reason()
    {
        ApprovalStore.Save(FilePath, Doc(), _signer);
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("FP-1", "FP-forged"));

        var r = ApprovalStore.TryLoad(FilePath, _signer);

        Assert.False(r.Ok);
        Assert.Equal("approvals signature is invalid", r.Reason);
    }

    [Fact]
    public void Malformed_file_fails_to_load()
    {
        File.WriteAllText(FilePath, "{ not json");

        var r = ApprovalStore.TryLoad(FilePath, _signer);

        Assert.False(r.Ok);
        Assert.Equal("approvals file is malformed", r.Reason);
    }
}

public sealed class SetupApproveTests : IDisposable
{
    private const string Profile = @"C:\Users\tester";
    private const string Script = "scripts/debug/reset.ps1";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "sh-setup-" + Guid.NewGuid());
    private readonly string _data;
    private readonly string _root;
    private readonly FakeElevation _elevation = new(true);
    private readonly FakeVolumes _volumes = new();
    private readonly FakeInspector _inspector = new();
    private readonly FakeInterpreter _interpreter = new();
    private readonly FakeSigner _signer = new();
    private readonly FakeConfirmer _confirmer = new();
    private readonly List<string> _ensured = new();

    public SetupApproveTests()
    {
        _data = Path.Combine(_base, "data");
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" } ] }""");
        WriteScript(Script, "Write-Output 'reset v1'");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private SetupCommands Commands() => new(
        new ScriptHelperConfig(_data), _elevation, _volumes, _inspector, _interpreter, _signer, _confirmer, Profile,
        p =>
        {
            _ensured.Add(p);
            Directory.CreateDirectory(p);
        });

    private void WriteManifest(string json) => File.WriteAllText(Path.Combine(_root, ".elevated.json"), json);

    private void WriteScript(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string RegisterProject(SetupCommands c)
    {
        var r = c.Register(_root);
        Assert.True(r.Ok, r.Reason);
        return r.ProjectId!;
    }

    private string ManifestHash() => Fingerprint.Sha256Hex(File.ReadAllBytes(Path.Combine(_root, ".elevated.json")));

    private Decision Authorize(SetupCommands c, string id, string script = Script, long lastSeen = 0, string? allowHash = null)
    {
        var fp = FingerprintBuilder.Build(_root, script, _interpreter, _inspector);
        return ApprovalStore.Authorize(
            c.ApprovalsPath(id), _signer, id, _root, lastSeen, script, fp.Fingerprint, allowHash ?? ManifestHash());
    }

    // The person's yes was given in chat, so the one step writes the approval without a console question.
    [Fact]
    public void ApproveInChat_writes_in_one_step_without_asking_the_console()
    {
        var c = Commands();
        var id = RegisterProject(c);

        var r = c.ApproveInChat(_root, Script);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(0, _confirmer.Calls);
        Assert.True(File.Exists(c.ApprovalsPath(id)));
    }

    [Fact]
    public void ApproveInChat_refuses_when_not_elevated_and_writes_nothing()
    {
        var c = Commands();
        var id = RegisterProject(c);
        _elevation.Elevated = false;

        var r = c.ApproveInChat(_root, Script);

        Assert.False(r.Ok);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
    }

    [Fact]
    public void Approve_refuses_when_not_elevated_and_writes_nothing()
    {
        var c = Commands();
        var id = RegisterProject(c);
        _elevation.Elevated = false;

        var r = c.Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains("elevated", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
        Assert.Equal(0, _confirmer.Calls);
    }

    [Fact]
    public void Approve_refuses_an_unregistered_project()
    {
        var r = Commands().Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains("not registered", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approve_refuses_a_missing_manifest()
    {
        var c = Commands();
        RegisterProject(c);
        File.Delete(Path.Combine(_root, ".elevated.json"));

        var r = c.Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains(".elevated.json", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Approve_refuses_a_malformed_manifest()
    {
        var c = Commands();
        RegisterProject(c);
        WriteManifest("{ nope");

        Assert.False(c.Approve(_root, Script).Ok);
    }

    [Fact]
    public void Approve_refuses_a_script_that_is_not_listed()
    {
        var c = Commands();
        RegisterProject(c);
        WriteScript("scripts/other/x.ps1", "x");

        var r = c.Approve(_root, "scripts/other/x.ps1");

        Assert.False(r.Ok);
        Assert.Contains("not listed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approve_refuses_a_path_outside_the_root()
    {
        var c = Commands();
        RegisterProject(c);

        Assert.False(c.Approve(_root, "..\\outside.ps1").Ok);
    }

    [Fact]
    public void Approve_refuses_a_reparse_point_on_the_script()
    {
        var c = Commands();
        RegisterProject(c);
        _inspector.ReparsePaths.Add(Path.Combine(_root, "scripts", "debug", "reset.ps1"));

        var r = c.Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains("reparse", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approve_refuses_a_missing_script()
    {
        var c = Commands();
        RegisterProject(c);
        File.Delete(Path.Combine(_root, "scripts", "debug", "reset.ps1"));

        Assert.False(c.Approve(_root, Script).Ok);
    }

    [Fact]
    public void Declined_confirmation_writes_nothing()
    {
        var c = Commands();
        var id = RegisterProject(c);
        _confirmer.Answer = false;

        var r = c.Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains("declined", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _confirmer.Calls);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
    }

    [Fact]
    public void Confirmed_approval_is_written_and_authorizes_the_script()
    {
        var c = Commands();
        var id = RegisterProject(c);

        var r = c.Approve(_root, Script);

        Assert.True(r.Ok, r.Reason);
        Assert.True(Authorize(c, id).Allowed);
    }

    [Fact]
    public void Prompt_shows_root_script_fingerprint_and_allowlist_hash()
    {
        var c = Commands();
        RegisterProject(c);

        c.Approve(_root, Script);

        var fp = FingerprintBuilder.Build(_root, Script, _interpreter, _inspector).Fingerprint;
        var prompt = _confirmer.LastPrompt!;
        Assert.Contains(_root, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Script, prompt, StringComparison.Ordinal);
        Assert.Contains(fp, prompt, StringComparison.Ordinal);
        Assert.Contains(ManifestHash(), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_returns_the_prompt_and_both_hashes_and_writes_nothing()
    {
        var c = Commands();
        var id = RegisterProject(c);

        var p = c.PrepareApproval(_root, Script);

        var fp = FingerprintBuilder.Build(_root, Script, _interpreter, _inspector).Fingerprint;
        Assert.True(p.Ok, p.Reason);
        Assert.Equal(fp, p.Fingerprint);
        Assert.Equal(ManifestHash(), p.AllowlistHash);
        Assert.Contains("Approve this script to run elevated?", p.Prompt, StringComparison.Ordinal);
        Assert.Contains(fp, p.Prompt, StringComparison.Ordinal);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
        Assert.Equal(0, c.Versions.Get(id));
    }

    [Fact]
    public void Prepare_never_asks_the_console_question()
    {
        var c = Commands();
        RegisterProject(c);

        c.PrepareApproval(_root, Script);

        Assert.Equal(0, _confirmer.Calls);
    }

    [Fact]
    public void Prepare_refuses_when_not_elevated()
    {
        var c = Commands();
        RegisterProject(c);
        _elevation.Elevated = false;

        var p = c.PrepareApproval(_root, Script);

        Assert.False(p.Ok);
        Assert.Contains("elevated", p.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_refuses_a_script_that_is_not_listed()
    {
        var c = Commands();
        RegisterProject(c);
        WriteScript("scripts/other/x.ps1", "x");

        var p = c.PrepareApproval(_root, "scripts/other/x.ps1");

        Assert.False(p.Ok);
        Assert.Contains("not listed", p.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, p.Fingerprint);
    }

    [Fact]
    public void Confirm_with_the_shown_hashes_approves_without_the_console_question()
    {
        var c = Commands();
        var id = RegisterProject(c);
        var p = c.PrepareApproval(_root, Script);

        var r = c.ConfirmApproval(_root, Script, p.Fingerprint, p.AllowlistHash);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(0, _confirmer.Calls);
        Assert.True(Authorize(c, id).Allowed);
        Assert.Equal(1, c.Versions.Get(id));
    }

    [Fact]
    public void Confirm_refuses_when_the_script_changed_after_it_was_shown()
    {
        var c = Commands();
        var id = RegisterProject(c);
        var p = c.PrepareApproval(_root, Script);
        WriteScript(Script, "Write-Output 'swapped after the person looked'");

        var r = c.ConfirmApproval(_root, Script, p.Fingerprint, p.AllowlistHash);

        Assert.False(r.Ok);
        Assert.Contains("changed since it was shown", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
    }

    [Fact]
    public void Confirm_refuses_when_the_allowlist_changed_after_it_was_shown()
    {
        var c = Commands();
        var id = RegisterProject(c);
        var p = c.PrepareApproval(_root, Script);
        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" }, { "path": "scripts/extra.ps1" } ] }""");

        var r = c.ConfirmApproval(_root, Script, p.Fingerprint, p.AllowlistHash);

        Assert.False(r.Ok);
        Assert.Contains("changed since it was shown", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
    }

    [Theory]
    [InlineData("", "AL")]
    [InlineData("FP", "")]
    [InlineData("  ", "  ")]
    public void Confirm_refuses_empty_hashes(string fingerprint, string allowlistHash)
    {
        var c = Commands();
        var id = RegisterProject(c);

        var r = c.ConfirmApproval(_root, Script, fingerprint, allowlistHash);

        Assert.False(r.Ok);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
    }

    [Fact]
    public void Confirm_refuses_when_not_elevated_and_writes_nothing()
    {
        var c = Commands();
        var id = RegisterProject(c);
        var p = c.PrepareApproval(_root, Script);
        _elevation.Elevated = false;

        var r = c.ConfirmApproval(_root, Script, p.Fingerprint, p.AllowlistHash);

        Assert.False(r.Ok);
        Assert.Contains("elevated", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(c.ApprovalsPath(id)));
    }

    [Fact]
    public void Confirm_after_confirm_increments_the_version_like_an_interactive_approval()
    {
        var c = Commands();
        var id = RegisterProject(c);
        WriteScript("scripts/debug/second.ps1", "second");
        var first = c.PrepareApproval(_root, Script);
        c.ConfirmApproval(_root, Script, first.Fingerprint, first.AllowlistHash);
        var second = c.PrepareApproval(_root, "scripts/debug/second.ps1");

        c.ConfirmApproval(_root, "scripts/debug/second.ps1", second.Fingerprint, second.AllowlistHash);

        Assert.Equal(2, c.Versions.Get(id));
        Assert.True(Authorize(c, id, Script).Allowed);
        Assert.True(Authorize(c, id, "scripts/debug/second.ps1").Allowed);
    }

    private static string[] PromptLines(string prompt) =>
        prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>The trimmed text of the line after the one whose trimmed text is the label.</summary>
    private static string ValueUnder(string[] lines, string label)
    {
        var at = Array.FindIndex(lines, l => l.Trim() == label);
        Assert.True(at >= 0, $"label '{label}' not found as its own line");
        return lines[at + 1].Trim();
    }

    [Fact]
    public void Prompt_puts_each_value_on_its_own_line_under_a_label()
    {
        var c = Commands();
        RegisterProject(c);

        c.Approve(_root, Script);

        var lines = PromptLines(_confirmer.LastPrompt!);
        var fp = FingerprintBuilder.Build(_root, Script, _interpreter, _inspector).Fingerprint;
        Assert.Equal(Path.GetFullPath(_root).TrimEnd('\\'), ValueUnder(lines, "Project"));
        Assert.Equal(Script, ValueUnder(lines, "Script"));
        Assert.Equal(fp, ValueUnder(lines, "Fingerprint"));
        Assert.Equal(ManifestHash(), ValueUnder(lines, "Allowlist hash"));
    }

    [Fact]
    public void Prompt_separates_sections_with_a_blank_line_and_lists_each_file_on_its_own_line()
    {
        var c = Commands();
        RegisterProject(c);
        WriteScript(Script, ". .\\helpers.ps1");
        WriteScript("scripts/debug/helpers.ps1", "helper");

        c.Approve(_root, Script);

        var lines = PromptLines(_confirmer.LastPrompt!);
        var header = Array.FindIndex(lines, l => l.Trim() == "Files pinned (2)");
        Assert.True(header >= 0, "files header not found");
        Assert.Equal("scripts/debug/helpers.ps1", lines[header + 1].Trim());
        Assert.Equal("scripts/debug/reset.ps1", lines[header + 2].Trim());
        Assert.Equal(string.Empty, lines[header + 3]);

        foreach (var label in new[] { "Project", "Script", "Fingerprint", "Allowlist hash" })
        {
            var at = Array.FindIndex(lines, l => l.Trim() == label);
            Assert.Equal(string.Empty, lines[at + 2]);
        }
    }

    [Fact]
    public void Prompt_hashes_stay_on_one_unbroken_line_each_so_they_can_be_compared()
    {
        var c = Commands();
        RegisterProject(c);

        c.Approve(_root, Script);

        var lines = PromptLines(_confirmer.LastPrompt!);
        var fp = FingerprintBuilder.Build(_root, Script, _interpreter, _inspector).Fingerprint;
        Assert.Contains(lines, l => l.Trim() == fp);
        Assert.Contains(lines, l => l.Trim() == ManifestHash());
    }

    [Fact]
    public void Allowlist_change_warning_is_its_own_paragraph_with_the_previous_hash()
    {
        var c = Commands();
        RegisterProject(c);
        WriteScript("scripts/debug/second.ps1", "second");
        c.Approve(_root, Script);
        var previousHash = ManifestHash();

        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" }, { "path": "scripts/extra.ps1" } ] }""");
        c.Approve(_root, "scripts/debug/second.ps1");

        var lines = PromptLines(_confirmer.LastPrompt!);
        var warning = Array.FindIndex(lines, l => l.Contains("WARNING", StringComparison.Ordinal));
        Assert.True(warning > 0, "warning not found");
        Assert.Equal(string.Empty, lines[warning - 1]);
        Assert.Contains(lines, l => l.Trim() == previousHash);
    }

    [Fact]
    public void Editing_the_manifest_after_approval_denies_until_reapproved()
    {
        var c = Commands();
        var id = RegisterProject(c);
        c.Approve(_root, Script);
        var oldHash = ManifestHash();

        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" }, { "path": "scripts/evil.ps1" } ] }""");
        var d = Authorize(c, id);

        Assert.NotEqual(oldHash, ManifestHash());
        Assert.False(d.Allowed);
        Assert.Equal("allowlist changed; re-approval needed", d.Reason);
    }

    [Fact]
    public void Editing_the_script_after_approval_denies_until_reapproved()
    {
        var c = Commands();
        var id = RegisterProject(c);
        c.Approve(_root, Script);

        WriteScript(Script, "Write-Output 'tampered'");

        var d = Authorize(c, id);
        Assert.False(d.Allowed);
        Assert.Equal("script changed; re-approval needed", d.Reason);

        Assert.True(c.Approve(_root, Script).Ok);
        Assert.True(Authorize(c, id).Allowed);
    }

    [Fact]
    public void Version_increments_per_approval_and_the_counter_is_bumped()
    {
        var c = Commands();
        var id = RegisterProject(c);
        WriteScript("scripts/debug/second.ps1", "second");

        c.Approve(_root, Script);
        Assert.Equal(1, c.Versions.Get(id));

        c.Approve(_root, "scripts/debug/second.ps1");
        Assert.Equal(2, c.Versions.Get(id));
    }

    [Fact]
    public void Approving_a_second_script_keeps_the_first_approved()
    {
        var c = Commands();
        var id = RegisterProject(c);
        WriteScript("scripts/debug/second.ps1", "second");

        c.Approve(_root, Script);
        c.Approve(_root, "scripts/debug/second.ps1");

        Assert.True(Authorize(c, id, Script).Allowed);
        Assert.True(Authorize(c, id, "scripts/debug/second.ps1").Allowed);
    }

    [Fact]
    public void An_older_approvals_file_is_a_replay_once_the_counter_has_moved_on()
    {
        var c = Commands();
        var id = RegisterProject(c);
        c.Approve(_root, Script);

        var d = Authorize(c, id, lastSeen: c.Versions.Get(id) + 1);

        Assert.False(d.Allowed);
        Assert.Contains("replay", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tampered_existing_approvals_are_not_overwritten()
    {
        var c = Commands();
        var id = RegisterProject(c);
        c.Approve(_root, Script);
        var path = c.ApprovalsPath(id);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Version\": 1", "\"Version\": 9"));
        var tampered = File.ReadAllText(path);

        var r = c.Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains("existing approvals", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tampered, File.ReadAllText(path));
    }

    [Fact]
    public void Prompt_flags_an_allowlist_change_since_the_last_approval()
    {
        var c = Commands();
        RegisterProject(c);
        WriteScript("scripts/debug/second.ps1", "second");
        c.Approve(_root, Script);

        WriteManifest("""{ "scripts": [ { "path": "scripts/debug/*.ps1" }, { "path": "scripts/extra.ps1" } ] }""");
        c.Approve(_root, "scripts/debug/second.ps1");

        Assert.Contains("allowlist changed", _confirmer.LastPrompt!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approval_refuses_when_a_dependency_cannot_be_resolved()
    {
        var c = Commands();
        RegisterProject(c);
        WriteScript(Script, ". $dynamic");

        var r = c.Approve(_root, Script);

        Assert.False(r.Ok);
        Assert.Contains("unresolved", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _confirmer.Calls);
    }

    [Fact]
    public void Approval_directories_are_created_through_the_injected_hook()
    {
        var c = Commands();
        RegisterProject(c);

        c.Approve(_root, Script);

        Assert.Contains(Path.Combine(_data, "approvals"), _ensured);
        Assert.Contains(Path.Combine(_data, "versions"), _ensured);
    }

    [Fact]
    public void Register_creates_the_owners_queue_and_results_folders()
    {
        var c = Commands();

        RegisterProject(c);

        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var config = new ScriptHelperConfig(_data);
        Assert.True(Directory.Exists(Path.Combine(config.QueueDirectory, owner)));
        Assert.True(Directory.Exists(Path.Combine(config.ResultsDirectory, owner)));
    }

    [Fact]
    public void Register_through_setup_commands_uses_the_same_registry()
    {
        var c = Commands();
        var first = c.Register(_root);
        var second = c.Register(_root);

        Assert.Equal(first.ProjectId, second.ProjectId);
    }
}
