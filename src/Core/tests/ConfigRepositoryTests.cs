using Xunit;

namespace AiDevHelpers.Core.Tests;

public class ConfigRepositoryTests
{
    [Fact]
    public void Root_is_ProgramData_slash_AiDevHelpers_from_the_environment()
    {
        var repo = ConfigRepository.FromEnvironment(name => name == "ProgramData" ? @"D:\PD" : null);

        Assert.Equal(@"D:\PD\AiDevHelpers", repo.Root);
    }

    [Fact]
    public void Root_falls_back_to_the_default_ProgramData_when_the_variable_is_missing()
    {
        var repo = ConfigRepository.FromEnvironment(_ => null);

        Assert.Equal(@"C:\ProgramData\AiDevHelpers", repo.Root);
    }

    [Fact]
    public void Explicit_root_is_used_as_given()
    {
        var repo = new ConfigRepository(@"E:\data\AiDevHelpers");

        Assert.Equal(@"E:\data\AiDevHelpers", repo.Root);
    }

    [Fact]
    public void Trailing_separator_on_the_root_is_trimmed()
    {
        Assert.Equal(@"E:\data\AiDevHelpers", new ConfigRepository(@"E:\data\AiDevHelpers\").Root);
    }

    [Fact]
    public void Relative_root_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ConfigRepository(@"data\AiDevHelpers"));
    }

    [Fact]
    public void ScriptHelper_element_lives_in_its_own_subfolder_of_the_root()
    {
        var sh = new ConfigRepository(@"E:\pd\AiDevHelpers").ScriptHelper;

        Assert.Equal(@"E:\pd\AiDevHelpers\script-helper", sh.DataRoot);
        Assert.Equal(@"E:\pd\AiDevHelpers\script-helper\projects.json", sh.RegistryPath);
        Assert.Equal(@"E:\pd\AiDevHelpers\script-helper\approvals", sh.ApprovalsDirectory);
        Assert.Equal(@"E:\pd\AiDevHelpers\script-helper\versions", sh.VersionsDirectory);
        Assert.Equal(@"E:\pd\AiDevHelpers\script-helper\mode.txt", sh.ModePath);
        Assert.Equal("ScriptHelper.Approvals", sh.KeyName);
    }

    [Fact]
    public void ScriptHelper_element_is_not_the_shared_root_so_the_acl_cannot_cover_other_components()
    {
        var repo = new ConfigRepository(@"E:\pd\AiDevHelpers");

        Assert.NotEqual(repo.Root, repo.ScriptHelper.DataRoot);
        Assert.StartsWith(repo.Root + "\\", repo.ScriptHelper.DataRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approvals_path_is_the_project_id_json_in_the_approvals_directory()
    {
        var sh = new ScriptHelperConfig(@"E:\sh");

        Assert.Equal(@"E:\sh\approvals\abc123.json", sh.ApprovalsPath("abc123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a.b")]
    public void Approvals_path_rejects_ids_that_could_escape_the_directory(string id)
    {
        Assert.Throws<ArgumentException>(() => new ScriptHelperConfig(@"E:\sh").ApprovalsPath(id));
    }

    [Fact]
    public void ScriptHelperConfig_requires_an_absolute_data_root()
    {
        Assert.Throws<ArgumentException>(() => new ScriptHelperConfig("relative"));
    }
}
