using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class PwshInterpreterInfoTests
{
    // The description is part of every fingerprint, so it must not depend on how the process resolves pwsh (an elevated shell and an
    // ordinary one can see different paths for the same Store alias). It names the interpreter and its major version only.
    [Fact]
    public void Describe_names_pwsh_and_its_major_version_without_a_path()
    {
        var description = new PwshInterpreterInfo().Describe();

        Assert.DoesNotContain("\\", description);
        Assert.DoesNotContain("/", description);
        Assert.StartsWith("pwsh|", description, StringComparison.Ordinal);
    }

    // The broker and the person's shell can have different PATH values, so the description must not depend on PATH at all.
    [Fact]
    public void Describe_does_not_depend_on_PATH()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Assert.Equal("pwsh|7", new PwshInterpreterInfo().Describe());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }
}
