using System.Security.Cryptography;
using System.Text;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class TpmApprovalSignerTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("approvals-payload-v1");

    private static TpmApprovalSigner NewSigner()
    {
        // Software ECDSA stands in for the TPM key; the TPM itself is covered by the Tpm-trait tests.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new TpmApprovalSigner(key);
    }

    [Fact]
    public void Sign_then_verify_succeeds()
    {
        var signer = NewSigner();

        var sig = signer.Sign(Payload);

        Assert.True(signer.Verify(Payload, sig));
    }

    [Fact]
    public void Tampered_payload_fails_verification()
    {
        var signer = NewSigner();
        var sig = signer.Sign(Payload);

        Assert.False(signer.Verify(Encoding.UTF8.GetBytes("approvals-payload-v2"), sig));
    }

    [Fact]
    public void Unsigned_empty_signature_fails_when_tpm_is_used()
    {
        var signer = NewSigner();

        Assert.False(signer.Verify(Payload, ""));
    }

    [Fact]
    public void Garbage_signature_fails_verification()
    {
        var signer = NewSigner();

        Assert.False(signer.Verify(Payload, "not-hex-at-all"));
    }

    [Fact]
    public void Signature_from_a_different_key_fails_verification()
    {
        var a = NewSigner();
        var b = NewSigner();

        Assert.False(b.Verify(Payload, a.Sign(Payload)));
    }
}

public class AclOnlyApprovalSignerTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("payload");

    [Fact]
    public void Sign_returns_empty_signature()
    {
        Assert.Equal("", new AclOnlyApprovalSigner().Sign(Payload));
    }

    [Fact]
    public void Empty_signature_verifies()
    {
        Assert.True(new AclOnlyApprovalSigner().Verify(Payload, ""));
    }

    [Fact]
    public void Any_non_empty_signature_is_rejected()
    {
        Assert.False(new AclOnlyApprovalSigner().Verify(Payload, "abc"));
    }
}

public class ModeResolverTests
{
    [Fact]
    public void Tpm_present_selects_tpm_mode()
    {
        var d = ModeResolver.Resolve(tpmPresent: true, lastRecorded: null, allowNoTpm: false);

        Assert.True(d.Ok);
        Assert.Equal(ProtectionMode.Tpm, d.Mode);
    }

    [Fact]
    public void No_tpm_without_opt_in_is_denied()
    {
        var d = ModeResolver.Resolve(tpmPresent: false, lastRecorded: null, allowNoTpm: false);

        Assert.False(d.Ok);
        Assert.Contains("--allow-no-tpm", d.Reason);
    }

    [Fact]
    public void No_tpm_with_opt_in_selects_acl_only_mode()
    {
        var d = ModeResolver.Resolve(tpmPresent: false, lastRecorded: null, allowNoTpm: true);

        Assert.True(d.Ok);
        Assert.Equal(ProtectionMode.AclOnly, d.Mode);
    }

    [Fact]
    public void No_tpm_after_tpm_was_seen_is_denied_even_with_opt_in()
    {
        var d = ModeResolver.Resolve(tpmPresent: false, lastRecorded: ProtectionMode.Tpm, allowNoTpm: true);

        Assert.False(d.Ok);
        Assert.Contains("previously seen", d.Reason);
    }

    [Fact]
    public void Tpm_returning_after_acl_only_selects_tpm_mode()
    {
        var d = ModeResolver.Resolve(tpmPresent: true, lastRecorded: ProtectionMode.AclOnly, allowNoTpm: false);

        Assert.True(d.Ok);
        Assert.Equal(ProtectionMode.Tpm, d.Mode);
    }

    [Fact]
    public void Mode_file_round_trips_out_of_band()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sh-mode-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "mode.txt");
            Assert.Null(ModeFile.Read(path));

            ModeFile.Write(path, ProtectionMode.Tpm);

            Assert.Equal(ProtectionMode.Tpm, ModeFile.Read(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class VersionCounterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-ver-" + Guid.NewGuid());

    public VersionCounterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Unknown_project_reads_zero()
    {
        Assert.Equal(0, new VersionCounter(_dir).Get("proj-a"));
    }

    [Fact]
    public void Bump_persists_across_instances()
    {
        new VersionCounter(_dir).Bump("proj-a", 5);

        Assert.Equal(5, new VersionCounter(_dir).Get("proj-a"));
    }

    [Fact]
    public void Bump_to_lower_version_is_rejected()
    {
        var c = new VersionCounter(_dir);
        c.Bump("proj-a", 5);

        Assert.Throws<InvalidOperationException>(() => c.Bump("proj-a", 3));
    }

    [Fact]
    public void Counters_are_per_project()
    {
        var c = new VersionCounter(_dir);
        c.Bump("proj-a", 5);

        Assert.Equal(0, c.Get("proj-b"));
    }
}
