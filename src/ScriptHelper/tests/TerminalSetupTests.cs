using System.Text;
using System.Text.Json;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.Common.Terminals;
using AiDevHelpers.ScriptHelper.Core;
using Xunit;

namespace AiDevHelpers.ScriptHelper.Core.Tests;

public sealed class TerminalSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sh-terminals-" + Guid.NewGuid().ToString("N"));
    private readonly string _settings;

    public TerminalSetupTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = Path.Combine(_dir, "settings.json");
        File.WriteAllText(_settings, "{\n  // mine\n  \"profiles\": { \"list\": [] },\n  \"disabledProfileSources\": [\"Git\"]\n}\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Probe(string settingsPath) : ITerminalProbe
    {
        public string? FindOnPath(string fileName) => fileName == "git.exe" ? @"C:\Program Files\Git\cmd\git.exe" : null;

        public bool FileExists(string path) => path is @"C:\Windows\System32\cmd.exe" or @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" or @"C:\Program Files\Git\bin\bash.exe";

        public string? FileVersion(string path) => "1.0";

        public string? Folder(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.System => @"C:\Windows\System32",
            Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
            _ => null,
        };

        public string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding) => null;

        public IReadOnlyList<string> WindowsTerminalSettingsFiles() => [settingsPath];

        public string? ReadText(string path) => File.ReadAllText(path);
    }

    private sealed class Answers(params bool[] yes) : IConfirmer
    {
        private readonly Queue<bool> _yes = new(yes);

        public List<string> Asked { get; } = [];

        public bool Confirm(string prompt)
        {
            Asked.Add(prompt);
            return _yes.Count > 0 && _yes.Dequeue();
        }
    }

    private TerminalServices Services() => new(new Probe(_settings), new TerminalRegistry(Path.Combine(_dir, "terminals.json")));

    [Fact]
    public void Scan_PrintsTheTypesAsJson_AndWritesNothing()
    {
        var output = new StringWriter();

        Assert.True(ElevLocalCommands.TryRun(["terminals", "scan"], output, new Answers(), out var exit, Services()));

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Contains(doc.RootElement.GetProperty("types").EnumerateArray(), t => t.GetProperty("id").GetString() == "gitbash");
        Assert.False(File.Exists(Path.Combine(_dir, "terminals.json")));
    }

    [Fact]
    public void Setup_YesRegistersTheType_AndAddsAProfileWhereWindowsTerminalLacksOne()
    {
        var services = Services();
        var output = new StringWriter();

        // powershell, cmd and gitbash are installed and unregistered: yes to the third (gitbash) only.
        var exit = TerminalSetup.Setup(services, askAgain: false, output, new Answers(false, false, true));

        Assert.Equal(0, exit);
        var choices = services.Registry.Read();
        Assert.Equal("gitbash", Assert.Single(choices.Registered).Id);
        Assert.Equal(["powershell", "cmd"], choices.Declined);
        var text = File.ReadAllText(_settings);
        Assert.Contains("// mine", text);
        Assert.Contains("Git Bash", text);
        Assert.True(File.Exists(_settings + ".bak"));
        Assert.Contains("hint: ", output.ToString());
    }

    [Fact]
    public void Setup_ADeclinedTypeIsNotAskedAgain_UntilAll()
    {
        var services = Services();
        TerminalSetup.Setup(services, askAgain: false, new StringWriter(), new Answers());

        var again = new Answers(false, false, false);
        TerminalSetup.Setup(services, askAgain: false, new StringWriter(), again);
        Assert.Empty(again.Asked);

        TerminalSetup.Setup(services, askAgain: true, new StringWriter(), again);
        Assert.Equal(3, again.Asked.Count);
    }

    [Fact]
    public void Setup_RunsTwiceWithYes_AddsTheProfileOnce()
    {
        var services = Services();
        TerminalSetup.Setup(services, askAgain: false, new StringWriter(), new Answers(false, false, true));
        var before = File.ReadAllText(_settings);

        TerminalSetup.Setup(services, askAgain: true, new StringWriter(), new Answers(false, false, true));

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void Setup_AnUnreadableSettingsFile_IsLeftAlone()
    {
        File.WriteAllText(_settings, "{ not json");

        TerminalSetup.Setup(Services(), askAgain: false, new StringWriter(), new Answers(true, true, true));

        Assert.Equal("{ not json", File.ReadAllText(_settings));
    }

    [Fact]
    public void AMalformedTerminalsCall_IsUsage()
    {
        Assert.True(ElevLocalCommands.TryRun(["terminals", "wipe"], new StringWriter(), new Answers(), out var exit, Services()));
        Assert.Equal(2, exit);
    }
}
