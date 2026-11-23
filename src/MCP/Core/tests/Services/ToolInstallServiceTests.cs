using System.Text;
using System.Text.Json;
using AiDevHelpers.Common;
using AiDevHelpers.Common.Terminals;
using AiDevHelpers.MCP.Core.Services;
using AiDevHelpers.MCP.Core.Tools;
using Xunit;

namespace AiDevHelpers.MCP.Core.Tests.Services;

public sealed class ToolInstallServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tool-install-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    public ToolInstallServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeInstaller(int exit = 0, string output = "ok") : IElevInstaller
    {
        public List<string> Installed { get; } = [];

        public Task<ElevRunResult> InstallAsync(string toolName, CancellationToken ct)
        {
            Installed.Add(toolName);
            return Task.FromResult(new ElevRunResult(exit, output));
        }
    }

    private sealed class Probe(bool winget) : ITerminalProbe
    {
        public string? FindOnPath(string fileName) => winget && fileName == "winget.exe" ? @"C:\winget.exe" : null;

        public bool FileExists(string path) => false;

        public string? FileVersion(string path) => null;

        public string? Folder(Environment.SpecialFolder folder) => null;

        public string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding) => null;

        public IReadOnlyList<string> WindowsTerminalSettingsFiles() => [];

        public string? ReadText(string path) => null;
    }

    private TerminalRegistry Registry() => new(Path.Combine(_dir, "terminals.json"));

    private ToolInstallService Service(FakeInstaller? installer = null, bool winget = true) =>
        new(installer ?? new FakeInstaller(), new Probe(winget), Registry(), () => _now);

    [Fact]
    public void APlan_NamesTheExactCommand_AndTellsTheAiToAskFirst_AndInstallsNothing()
    {
        var installer = new FakeInstaller();

        var outcome = Service(installer).Plan("pwsh");

        Assert.True(outcome.Ok);
        Assert.Equal("winget install --id Microsoft.PowerShell --exact --source winget", outcome.Plan!.Command);
        Assert.Contains("Ask the user", outcome.Message);
        Assert.Contains("administrator rights", outcome.Message);
        Assert.Empty(installer.Installed);
    }

    [Theory]
    [InlineData("Microsoft.PowerShell")]
    [InlineData("pwsh; calc")]
    [InlineData("")]
    [InlineData("../evil")]
    public void OnlyTheBuiltInNames_CanBePlanned(string tool)
    {
        var outcome = Service().Plan(tool);

        Assert.False(outcome.Ok);
        Assert.StartsWith("Error:", outcome.Error);
    }

    [Fact]
    public void WithoutWinget_NothingIsPlanned()
    {
        var outcome = Service(winget: false).Plan("node");

        Assert.False(outcome.Ok);
        Assert.Contains("winget is not installed", outcome.Error);
    }

    [Fact]
    public void Wsl_GetsInstructionsOnly()
    {
        var outcome = Service().Plan("wsl");

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Plan);
        Assert.Contains("wsl --install", outcome.Message);
    }

    [Fact]
    public async Task Confirm_RunsTheInstallOnceThroughTheElevatedHelper()
    {
        var installer = new FakeInstaller();
        var service = Service(installer);
        var token = service.Plan("gitbash").Plan!.Token;

        var done = await service.ConfirmAsync(token, CancellationToken.None);
        var again = await service.ConfirmAsync(token, CancellationToken.None);

        Assert.True(done.Ok);
        Assert.Equal(["gitbash"], installer.Installed);
        Assert.False(again.Ok);
        Assert.Single(installer.Installed);
    }

    [Fact]
    public async Task AnUnknownOrExpiredToken_RunsNothing()
    {
        var installer = new FakeInstaller();
        var service = Service(installer);
        var token = service.Plan("node").Plan!.Token;

        Assert.False((await service.ConfirmAsync("nope", CancellationToken.None)).Ok);

        _now = _now.AddMinutes(16);
        Assert.False((await service.ConfirmAsync(token, CancellationToken.None)).Ok);
        Assert.Empty(installer.Installed);
    }

    [Fact]
    public async Task AFailedInstall_ReportsTheExitCode_AndPointsAtSetupToolsWhenTheBrokerRefused()
    {
        var service = Service(new FakeInstaller(1, "refused: project is not registered"));
        var token = service.Plan("python").Plan!.Token;

        var outcome = await service.ConfirmAsync(token, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("exit 1", outcome.Error);
        Assert.Contains("setup tools", outcome.Error);

        var other = Service(new FakeInstaller(5, "winget failed"));
        var failed = await other.ConfirmAsync(other.Plan("python").Plan!.Token, CancellationToken.None);
        Assert.DoesNotContain("setup tools", failed.Error);
    }

    [Fact]
    public void Decline_IsRemembered_AndStopsTheOffer()
    {
        var service = Service();
        var choices = Registry().Read();
        Assert.NotNull(ToolInstallService.OfferFor("pwsh", choices));
        Assert.Contains("tool_install", ToolInstallService.OfferFor("bash", choices));

        Assert.True(service.Decline("pwsh").Ok);

        Assert.Null(ToolInstallService.OfferFor("pwsh", Registry().Read()));
        Assert.NotNull(ToolInstallService.OfferFor("python", Registry().Read()));
        Assert.False(service.Decline("calc").Ok);
    }

    [Fact]
    public void NoOfferIsMadeForInterpretersThatCannotBeInstalled() =>
        Assert.Null(ToolInstallService.OfferFor("powershell", new TerminalChoices([], [])));

    [Fact]
    public async Task TheTool_RoutesPlanConfirmAndDecline_AsJson()
    {
        var installer = new FakeInstaller();
        var tool = new ToolInstallTools(Service(installer));

        using var plan = JsonDocument.Parse(await tool.ToolInstall("plan", "node"));
        var token = plan.RootElement.GetProperty("plan").GetProperty("token").GetString()!;
        using var confirmed = JsonDocument.Parse(await tool.ToolInstall("confirm", token: token));
        using var bad = JsonDocument.Parse(await tool.ToolInstall("explode"));

        Assert.True(confirmed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["node"], installer.Installed);
        Assert.False(bad.RootElement.GetProperty("ok").GetBoolean());
    }
}
