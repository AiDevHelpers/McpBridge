using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

/// <summary>
/// Real TPM checks. Run on demand: dotnet test --filter "Category=Tpm".
/// Needs a TPM 2.0 on this machine. Creating the throwaway key needs administrator rights, so that test skips itself
/// when not elevated (Visual Studio Test Explorer ignores runsettings filters).
/// </summary>
[Trait("Category", "Tpm")]
public class TpmIntegrationTests
{
    [Fact]
    public void Tpm_20_is_reported_present_on_this_machine()
    {
        Assert.True(TbsTpmDetector.Tpm20Present());
    }

    [RequiresAdministratorFact]
    public void Real_tpm_key_signs_and_verifies_then_is_deleted()
    {
        var name = "ScriptHelper.Test." + Guid.NewGuid().ToString("N");
        using var key = TpmKeys.Create(name);
        try
        {
            var signer = new TpmApprovalSigner(key);
            var payload = System.Text.Encoding.UTF8.GetBytes("integration-payload");

            var sig = signer.Sign(payload);

            Assert.True(signer.Verify(payload, sig));
            Assert.False(signer.Verify(System.Text.Encoding.UTF8.GetBytes("other"), sig));
        }
        finally
        {
            TpmKeys.Delete(name);
        }
    }
}
