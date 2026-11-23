using System.Text;
using System.Text.Json;
using AiDevHelpers.Common.Terminals;
using Xunit;

namespace AiDevHelpers.Common.Tests;

public sealed class TerminalTypesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "terminals-" + Guid.NewGuid().ToString("N"));

    public TerminalTypesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private TerminalRegistry Registry() => new(Path.Combine(_dir, "terminals.json"));

    [Fact]
    public void TheScan_FindsInstalledTypes_AndSkipsWhatIsNotThere()
    {
        var scan = new TerminalTypeScanner(FakeTerminalProbe.Machine(), Registry()).Scan();

        var ids = scan.Types.Where(t => t.Installed).Select(t => t.Id).ToList();
        Assert.Equal(["powershell", "cmd", "gitbash", "wsl:Ubuntu"], ids);
        Assert.DoesNotContain(scan.Types, t => t.Id == "wsl:docker-desktop");
        Assert.False(scan.Types.Single(t => t.Id == "pwsh").Installed);
        Assert.Contains(scan.Hints, h => h.Contains("winget install Microsoft.PowerShell"));
        Assert.False(scan.WindowsTerminalFound);
    }

    [Fact]
    public void WslOutputInUtf16WithNulsAndABom_IsParsed_AndNoDistroIsAHint()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.WslOutput = "\uFEFFDebian\0\r\n";
        Assert.Contains(new TerminalTypeScanner(probe, Registry()).Scan().Types, t => t.Id == "wsl:Debian" && t.Installed);

        probe.WslOutput = null;
        var scan = new TerminalTypeScanner(probe, Registry()).Scan();
        Assert.DoesNotContain(scan.Types, t => t.Id.StartsWith("wsl:", StringComparison.Ordinal));
        Assert.Contains(scan.Hints, h => h.Contains("wsl --install"));
    }

    [Fact]
    public void GitBash_IsFoundFromGit_NotFromTheWslLauncher()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.Files.Add(@"C:\Windows\System32\bash.exe");

        var bash = new TerminalTypeScanner(probe, Registry()).Scan().Types.Single(t => t.Id == "gitbash");

        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", bash.Path);
    }

    [Fact]
    public void AnInstalledTypeThatIsNotRegistered_IsMissing_UntilRegisteredOrDeclined()
    {
        var registry = Registry();
        var scanner = new TerminalTypeScanner(FakeTerminalProbe.Machine(), registry);

        Assert.Equal(["powershell", "cmd", "gitbash", "wsl:Ubuntu"], scanner.Scan().Types.Where(t => t.Missing).Select(t => t.Id).ToList());

        registry.Register(new RegisteredTerminal("gitbash", "Git Bash", @"C:\Program Files\Git\bin\bash.exe", "2.0"));
        registry.Decline("wsl:Ubuntu");

        Assert.Equal(["powershell", "cmd"], scanner.Scan().Types.Where(t => t.Missing).Select(t => t.Id).ToList());
        Assert.True(scanner.Scan().Types.Single(t => t.Id == "gitbash").InHelper);
    }

    [Fact]
    public void WindowsTerminalCoverage_CountsGeneratorsUnlessTheyAreDisabled()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.Settings.Add("""{ "profiles": { "list": [] }, "disabledProfileSources": ["Git"] }""");

        var types = new TerminalTypeScanner(probe, Registry()).Scan();

        Assert.True(types.WindowsTerminalFound);
        Assert.False(types.Types.Single(t => t.Id == "gitbash").InWindowsTerminal);
        Assert.True(types.Types.Single(t => t.Id == "wsl:Ubuntu").InWindowsTerminal);
        Assert.True(types.Types.Single(t => t.Id == "cmd").InWindowsTerminal);
    }

    [Fact]
    public void AProfileThatRunsTheTool_CountsEvenWhenTheGeneratorIsDisabled()
    {
        var probe = FakeTerminalProbe.Machine();
        probe.Settings.Add("""{ "profiles": { "list": [ { "name": "My bash", "commandline": "C:\\Program Files\\Git\\bin\\bash.exe -i" } ] }, "disabledProfileSources": ["Git"] }""");

        Assert.True(new TerminalTypeScanner(probe, Registry()).Scan().Types.Single(t => t.Id == "gitbash").InWindowsTerminal);
    }

    [Fact]
    public void TheRegistry_KeepsChoices_RegisteringClearsADecline_AndMalformedMeansNone()
    {
        var registry = Registry();
        registry.Decline("pwsh");
        registry.Register(new RegisteredTerminal("pwsh", "PowerShell 7", @"C:\p\pwsh.exe", "7.5"));

        var choices = registry.Read();
        Assert.Equal("pwsh", Assert.Single(choices.Registered).Id);
        Assert.Empty(choices.Declined);

        registry.Decline("cmd");
        registry.ClearDeclined();
        Assert.Empty(registry.Read().Declined);

        File.WriteAllText(Path.Combine(_dir, "terminals.json"), "{ broken");
        Assert.Empty(registry.Read().Registered);
        Assert.Throws<IOException>(() => registry.Decline("cmd"));
    }

    private const string Commented = """
        {
            // my settings
            "defaultProfile": "{abc}",
            "profiles": {
                "defaults": {},
                "list": [
                    { "guid": "{1}", "name": "One" }, // first
                    { "guid": "{2}", "name": "Two" } /* last */,
                ]
            },
            "schemes": []
        }
        """;

    [Fact]
    public void InsertProfile_KeepsCommentsAndTrailingCommas_AndAddsTheProfileLast()
    {
        var updated = WindowsTerminalSettings.InsertProfile(Commented, "gitbash", "Git Bash", @"""C:\Program Files\Git\bin\bash.exe"" -i -l");

        Assert.NotNull(updated);
        Assert.Contains("// my settings", updated);
        Assert.Contains("// first", updated);
        Assert.Contains("/* last */", updated);
        Assert.Contains("\"schemes\": []", updated);
        using var doc = JsonDocument.Parse(updated!, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var list = doc.RootElement.GetProperty("profiles").GetProperty("list");
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal("Git Bash", list[2].GetProperty("name").GetString());
        Assert.Equal(@"""C:\Program Files\Git\bin\bash.exe"" -i -l", list[2].GetProperty("commandline").GetString());
    }

    [Fact]
    public void InsertProfile_WorksOnAnEmptyList_AndOnAnArrayShapedProfilesKey()
    {
        var empty = WindowsTerminalSettings.InsertProfile("""{ "profiles": { "list": [] } }""", "cmd", "Cmd", "cmd.exe");
        var legacy = WindowsTerminalSettings.InsertProfile("""{ "profiles": [ { "guid": "{1}", "name": "One" } ] }""", "cmd", "Cmd", "cmd.exe");

        Assert.NotNull(empty);
        Assert.NotNull(legacy);
        Assert.Contains("\"Cmd\"", empty);
        Assert.Contains("\"Cmd\"", legacy);
    }

    [Fact]
    public void InsertProfile_IsIdempotent_KeepsCrLf_AndRefusesWhatItCannotEditSafely()
    {
        var once = WindowsTerminalSettings.InsertProfile(Commented.Replace("\n", "\r\n"), "cmd", "Cmd", "cmd.exe")!;
        var twice = WindowsTerminalSettings.InsertProfile(once, "cmd", "Cmd", "cmd.exe");

        Assert.Equal(once, twice);
        Assert.DoesNotContain("\n", once.Replace("\r\n", string.Empty));
        Assert.Null(WindowsTerminalSettings.InsertProfile("{ broken", "cmd", "Cmd", "cmd.exe"));
        Assert.Null(WindowsTerminalSettings.InsertProfile("""{ "profiles": {} }""", "cmd", "Cmd", "cmd.exe"));
        Assert.Null(WindowsTerminalSettings.InsertProfile("""{ "other": 1 }""", "cmd", "Cmd", "cmd.exe"));
    }

    [Fact]
    public void AddProfile_WritesABackupFirst_AndLeavesAnUnreadableFileAlone()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, Commented);

        Assert.True(WindowsTerminalSettings.AddProfile(path, "gitbash", "Git Bash", "bash.exe"));
        Assert.Equal(Commented, File.ReadAllText(path + ".bak"));
        Assert.False(WindowsTerminalSettings.AddProfile(path, "gitbash", "Git Bash", "bash.exe"));

        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.False(WindowsTerminalSettings.AddProfile(broken, "cmd", "Cmd", "cmd.exe"));
        Assert.Equal("{ not json", File.ReadAllText(broken));
        Assert.False(File.Exists(broken + ".bak"));
    }

    [Fact]
    public void TheProfileGuid_IsStablePerId()
    {
        Assert.Equal(WindowsTerminalSettings.ProfileGuid("gitbash"), WindowsTerminalSettings.ProfileGuid("GitBash"));
        Assert.NotEqual(WindowsTerminalSettings.ProfileGuid("gitbash"), WindowsTerminalSettings.ProfileGuid("cmd"));
    }
}
