using AiDevHelpers.Common;
using AiDevHelpers.Helpers.ProjectHelper;
using Xunit;

namespace AiDevHelpers.Helpers.ProjectHelper.Tests;

public sealed class FrameworkGuardTests
{
    private const string ConfigPath = ".aidevhelpers/project-standards.json";
    private const string Project = "src/Helpers/A/src/A.csproj";

    private static ProjectStandards Standards(params FrameworkException[] exceptions) =>
        new("net10.0", ["-windows"], new Dictionary<string, string>(), [], exceptions);

    private static GuardResult Only(IReadOnlyList<GuardResult> results, string framework) =>
        Assert.Single(results, r => r.Framework == framework);

    [Fact]
    public void BaseFrameworkIsAllowed()
    {
        var result = FrameworkGuard.Check(Project, ["net10.0"], Standards(), ConfigPath);

        Assert.Equal(GuardStatus.Allowed, Only(result, "net10.0").Status);
    }

    [Fact]
    public void BaseWithAllowedSuffixIsAllowed()
    {
        var result = FrameworkGuard.Check(Project, ["net10.0-windows"], Standards(), ConfigPath);

        Assert.Equal(GuardStatus.Allowed, Only(result, "net10.0-windows").Status);
    }

    [Fact]
    public void UnlistedSuffixIsRefused()
    {
        var result = FrameworkGuard.Check(Project, ["net10.0-linux"], Standards(), ConfigPath);

        Assert.Equal(GuardStatus.Refused, Only(result, "net10.0-linux").Status);
    }

    [Fact]
    public void DifferentFrameworkWithExceptionIsExcepted()
    {
        var result = FrameworkGuard.Check(Project, ["net48"], Standards(new FrameworkException(Project, "net48", "host")), ConfigPath);

        Assert.Equal(GuardStatus.Excepted, Only(result, "net48").Status);
    }

    [Fact]
    public void DifferentFrameworkWithoutExceptionIsRefusedAndNamesTheConfig()
    {
        var result = FrameworkGuard.Check(Project, ["net48"], Standards(), ConfigPath);

        var refused = Only(result, "net48");
        Assert.Equal(GuardStatus.Refused, refused.Status);
        Assert.Contains(ConfigPath, refused.Message);
        Assert.Contains("exceptions", refused.Message);
    }

    [Fact]
    public void ExceptionForAnotherProjectDoesNotApply()
    {
        var result = FrameworkGuard.Check(Project, ["net48"], Standards(new FrameworkException("src/Other/o.csproj", "net48", "host")), ConfigPath);

        Assert.Equal(GuardStatus.Refused, Only(result, "net48").Status);
    }

    [Fact]
    public void MultiTargetIsCheckedPerFramework()
    {
        var result = FrameworkGuard.Check(Project, ["net10.0", "net48"], Standards(new FrameworkException(Project, "net48", "host")), ConfigPath);

        Assert.Equal(GuardStatus.Allowed, Only(result, "net10.0").Status);
        Assert.Equal(GuardStatus.Excepted, Only(result, "net48").Status);
    }

    [Fact]
    public void NoStandardsFailsClosed()
    {
        var result = FrameworkGuard.Check(Project, ["net10.0"], null, ConfigPath);

        Assert.Equal(GuardStatus.Refused, Only(result, "net10.0").Status);
    }

    [Fact]
    public void CheckNeverWritesTheConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".aidevhelpers"));
        var file = Path.Combine(root, ".aidevhelpers", "project-standards.json");
        File.WriteAllText(file, "{ \"baseFramework\": \"net10.0\" }");
        try
        {
            var before = File.ReadAllBytes(file);

            FrameworkGuard.Check(Project, ["net48"], Standards(), file);

            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
