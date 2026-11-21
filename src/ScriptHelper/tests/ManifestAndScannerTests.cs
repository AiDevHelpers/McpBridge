using System.Security.AccessControl;
using System.Security.Principal;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public class ManifestParserTests
{
    [Fact]
    public void Valid_manifest_parses_entries_with_and_without_args()
    {
        const string json = """
            { "scripts": [
                { "path": "scripts/debug/attach.ps1", "args": ["-Port", "*"] },
                { "path": "scripts/debug/*.ps1" }
            ] }
            """;

        var r = ManifestParser.Parse(json);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal("scripts/debug/attach.ps1", r.Entries[0].Path);
        Assert.Equal(new[] { "-Port", "*" }, r.Entries[0].Args);
        Assert.Null(r.Entries[1].Args);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        var r = ManifestParser.Parse("{ not json");

        Assert.False(r.Ok);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_scripts_array_is_rejected()
    {
        Assert.False(ManifestParser.Parse("{}").Ok);
    }

    [Fact]
    public void Entry_without_path_is_rejected()
    {
        Assert.False(ManifestParser.Parse("""{ "scripts": [ { "args": [] } ] }""").Ok);
    }

    [Fact]
    public void Non_string_args_are_rejected()
    {
        Assert.False(ManifestParser.Parse("""{ "scripts": [ { "path": "a.ps1", "args": [1] } ] }""").Ok);
    }
}

public class AllowlistPathListedTests
{
    private static readonly AllowlistEntry[] Entries =
    {
        new("scripts/debug/attach.ps1", new[] { "-Port", "*" }),
        new("scripts/tools/*.ps1", null),
    };

    [Fact]
    public void Exact_path_is_listed_regardless_of_args()
    {
        Assert.True(Allowlist.IsPathListed(Entries, "scripts/debug/attach.ps1"));
    }

    [Fact]
    public void Glob_path_is_listed()
    {
        Assert.True(Allowlist.IsPathListed(Entries, "scripts/tools/reset.ps1"));
    }

    [Fact]
    public void Unlisted_path_is_not_listed()
    {
        Assert.False(Allowlist.IsPathListed(Entries, "scripts/other.ps1"));
    }

    [Fact]
    public void Backslash_path_is_normalized_before_matching()
    {
        Assert.True(Allowlist.IsPathListed(Entries, @"scripts\debug\attach.ps1"));
    }
}

public class DependencyScannerTests
{
    [Fact]
    public void Dot_source_with_relative_path_is_found()
    {
        var r = DependencyScanner.Scan(". .\\helpers.ps1\nWrite-Output 'x'");

        Assert.Equal(new[] { ".\\helpers.ps1" }, r.Paths);
        Assert.Empty(r.Unresolved);
    }

    [Fact]
    public void Quoted_dot_source_with_script_root_is_found_relative_to_the_script()
    {
        var r = DependencyScanner.Scan(". \"$PSScriptRoot\\lib\\common.ps1\"");

        Assert.Equal(new[] { "lib\\common.ps1" }, r.Paths);
    }

    [Fact]
    public void Import_module_with_a_path_is_found()
    {
        var r = DependencyScanner.Scan("Import-Module .\\tools.psm1");

        Assert.Equal(new[] { ".\\tools.psm1" }, r.Paths);
    }

    [Fact]
    public void Import_module_by_name_is_not_a_file_dependency()
    {
        var r = DependencyScanner.Scan("Import-Module Pester");

        Assert.Empty(r.Paths);
        Assert.Empty(r.Unresolved);
    }

    [Fact]
    public void Dot_source_through_a_variable_is_unresolved()
    {
        var r = DependencyScanner.Scan(". $someVariable");

        Assert.Empty(r.Paths);
        Assert.Single(r.Unresolved);
    }

    [Fact]
    public void Commented_dot_source_is_ignored()
    {
        var r = DependencyScanner.Scan("# . .\\old.ps1");

        Assert.Empty(r.Paths);
    }

    [Fact]
    public void Script_without_dependencies_has_none()
    {
        var r = DependencyScanner.Scan("Write-Output 'hello'");

        Assert.Empty(r.Paths);
        Assert.Empty(r.Unresolved);
    }
}

public class AdminOnlyDirectoryTests
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static IReadOnlyList<FileSystemAccessRule> Rules()
    {
        var sec = AdminOnlyDirectory.BuildSecurity();
        return sec.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
    }

    [Fact]
    public void Inheritance_is_protected_so_parent_permissions_do_not_leak_in()
    {
        Assert.True(AdminOnlyDirectory.BuildSecurity().AreAccessRulesProtected);
    }

    [Theory]
    [InlineData("admins")]
    [InlineData("system")]
    public void Administrators_and_system_have_full_control(string who)
    {
        var sid = who == "admins" ? Admins : System;

        var rule = Assert.Single(Rules(), r => r.IdentityReference.Value == sid.Value);

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    [Fact]
    public void Users_may_only_read_and_execute()
    {
        var rule = Assert.Single(Rules(), r => r.IdentityReference.Value == Users.Value);

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        // .NET adds Synchronize to file system rules automatically.
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rule.FileSystemRights);
    }

    [Fact]
    public void No_rule_grants_write_to_anyone_but_administrators_and_system()
    {
        var writeBits = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        foreach (var rule in Rules())
        {
            if (rule.IdentityReference.Value == Admins.Value || rule.IdentityReference.Value == System.Value)
            {
                continue;
            }

            Assert.Equal(default, rule.FileSystemRights & writeBits);
        }
    }
}
