using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

/// <summary>Test-only signer: keyed hash. Not the TPM signer.</summary>
internal sealed class FakeSigner : IApprovalSigner
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-key");

    public string Sign(byte[] payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Key, payload));

    public bool Verify(byte[] payload, string signature) =>
        string.Equals(Sign(payload), signature, StringComparison.Ordinal);
}

public sealed class ApprovalStoreTests : IDisposable
{
    private const string ProjectId = "proj-a";
    private const string ProjectRoot = @"C:\repo";
    private const string Script = "scripts/debug/reset.ps1";
    private const string Fp = "FP-1";
    private const string AllowHash = "AL-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-tests-" + Guid.NewGuid());
    private readonly FakeSigner _signer = new();

    public ApprovalStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string FilePath => Path.Combine(_dir, "approvals.json");

    private static ApprovalDocument Doc(
        string projectId = ProjectId, long version = 1, string script = Script,
        string fingerprint = Fp, string allowHash = AllowHash) =>
        new(projectId, ProjectRoot, version, allowHash,
            new[] { new ApprovalRecord(script, fingerprint) }, Signature: "");

    private void SaveSigned(ApprovalDocument doc) => ApprovalStore.Save(FilePath, doc, _signer);

    private void WriteRaw(ApprovalDocument doc) =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(doc));

    private ApprovalDocument ReadRaw() =>
        JsonSerializer.Deserialize<ApprovalDocument>(File.ReadAllText(FilePath))!;

    private Decision Authorize(
        string projectId = ProjectId, string root = ProjectRoot, long lastSeen = 0,
        string script = Script, string fingerprint = Fp, string allowHash = AllowHash) =>
        ApprovalStore.Authorize(FilePath, _signer, projectId, root, lastSeen, script, fingerprint, allowHash);

    [Fact]
    public void Approved_unchanged_script_is_allowed()
    {
        SaveSigned(Doc());

        var d = Authorize();

        Assert.True(d.Allowed);
        Assert.Equal("approved and unchanged", d.Reason);
    }

    [Fact]
    public void Edited_script_is_denied_with_re_approval_reason()
    {
        SaveSigned(Doc());

        var d = Authorize(fingerprint: "FP-changed");

        Assert.False(d.Allowed);
        Assert.Equal("script changed; re-approval needed", d.Reason);
    }

    [Fact]
    public void Changed_allowlist_hash_is_denied_with_reason()
    {
        SaveSigned(Doc());

        var d = Authorize(allowHash: "AL-changed");

        Assert.False(d.Allowed);
        Assert.Equal("allowlist changed; re-approval needed", d.Reason);
    }

    [Fact]
    public void Missing_approvals_file_is_denied()
    {
        var d = Authorize();

        Assert.False(d.Allowed);
        Assert.Equal("approvals file missing; approve the script first", d.Reason);
    }

    [Fact]
    public void Malformed_approvals_file_is_denied()
    {
        File.WriteAllText(FilePath, "{ not json");

        var d = Authorize();

        Assert.False(d.Allowed);
        Assert.Equal("approvals file is malformed", d.Reason);
    }

    [Fact]
    public void Null_scripts_field_is_denied_as_malformed_not_crashing()
    {
        File.WriteAllText(FilePath,
            "{\"ProjectId\":\"proj-a\",\"ProjectRoot\":\"C:\\\\repo\",\"Version\":1," +
            "\"AllowlistHash\":\"AL-1\",\"Scripts\":null,\"Signature\":\"x\"}");

        var d = Authorize();

        Assert.False(d.Allowed);
        Assert.Equal("approvals file is malformed", d.Reason);
    }

    [Fact]
    public void Null_signature_is_denied_as_malformed()
    {
        SaveSigned(Doc());
        WriteRaw(ReadRaw() with { Signature = null! });

        var d = Authorize();

        Assert.False(d.Allowed);
        Assert.Equal("approvals file is malformed", d.Reason);
    }

    [Fact]
    public void Oversized_approvals_file_is_denied()
    {
        File.WriteAllText(FilePath, new string('x', 2 * 1024 * 1024));

        var d = Authorize();

        Assert.False(d.Allowed);
        Assert.Equal("approvals file is too large", d.Reason);
    }

    [Theory]
    [InlineData("ProjectId")]
    [InlineData("ProjectRoot")]
    [InlineData("Version")]
    [InlineData("AllowlistHash")]
    [InlineData("Fingerprint")]
    [InlineData("Path")]
    public void Tampering_any_signed_field_fails_the_signature(string field)
    {
        SaveSigned(Doc());
        var doc = ReadRaw();

        var tampered = field switch
        {
            "ProjectId" => doc with { ProjectId = "proj-b" },
            "ProjectRoot" => doc with { ProjectRoot = @"C:\other" },
            "Version" => doc with { Version = doc.Version + 1 },
            "AllowlistHash" => doc with { AllowlistHash = "AL-forged" },
            "Fingerprint" => doc with { Scripts = new[] { new ApprovalRecord(Script, "FP-forged") } },
            "Path" => doc with { Scripts = new[] { new ApprovalRecord("scripts/other.ps1", Fp) } },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        WriteRaw(tampered);

        var d = Authorize(fingerprint: "FP-forged");

        Assert.False(d.Allowed);
        Assert.Equal("approvals signature is invalid", d.Reason);
    }

    [Fact]
    public void Older_version_than_last_seen_is_denied_as_replay()
    {
        SaveSigned(Doc(version: 5));

        var d = Authorize(lastSeen: 6);

        Assert.False(d.Allowed);
        Assert.Equal("replay: approvals version is older than the last seen version", d.Reason);
    }

    [Fact]
    public void Approvals_file_moved_to_another_project_is_denied()
    {
        SaveSigned(Doc(projectId: "proj-a"));

        var d = Authorize(projectId: "proj-b");

        Assert.False(d.Allowed);
        Assert.Equal("approvals belong to another project", d.Reason);
    }

    [Fact]
    public void Copied_checkout_with_same_project_id_is_denied()
    {
        SaveSigned(Doc());

        var d = Authorize(root: @"D:\elsewhere");

        Assert.False(d.Allowed);
        Assert.Equal("approvals belong to another checkout", d.Reason);
    }

    [Fact]
    public void Backslash_and_slash_paths_match_the_same_entry()
    {
        SaveSigned(Doc(script: @"scripts\debug\reset.ps1"));

        var d = Authorize(script: "scripts/debug/reset.ps1");

        Assert.True(d.Allowed);
    }

    [Fact]
    public void Dot_dot_segments_normalize_to_the_approved_path()
    {
        SaveSigned(Doc());

        var d = Authorize(script: "scripts/debug/../debug/reset.ps1");

        Assert.True(d.Allowed); // normalizes to the approved path; the ".." itself is removed
    }

    [Fact]
    public void Path_escaping_the_root_is_denied()
    {
        SaveSigned(Doc());

        var d = Authorize(script: "../outside.ps1");

        Assert.False(d.Allowed);
        Assert.Equal("script is not approved", d.Reason);
    }

    [Fact]
    public void Duplicate_normalized_paths_are_rejected_on_save()
    {
        var doc = new ApprovalDocument(ProjectId, ProjectRoot, 1, AllowHash,
            new[]
            {
                new ApprovalRecord(@"scripts\reset.ps1", Fp),
                new ApprovalRecord("scripts/reset.ps1", Fp),
            }, Signature: "");

        Assert.Throws<ArgumentException>(() => SaveSigned(doc));
    }

    [Fact]
    public void Save_stores_no_temp_file_left_behind()
    {
        SaveSigned(Doc());

        Assert.Single(Directory.GetFiles(_dir));
    }
}
