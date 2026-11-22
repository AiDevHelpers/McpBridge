using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class RequiresAdministratorFactAttributeTests
{
    [Fact]
    public void An_elevated_process_runs_the_test()
    {
        Assert.Null(RequiresAdministratorFactAttribute.SkipReason(isAdministrator: true));
    }

    [Fact]
    public void A_process_without_administrator_rights_skips_the_test_and_says_why()
    {
        var reason = RequiresAdministratorFactAttribute.SkipReason(isAdministrator: false);

        Assert.Contains("administrator", reason, StringComparison.OrdinalIgnoreCase);
    }
}
