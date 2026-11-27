using Xunit;

namespace AiDevHelpers.Tray.Tests;

public sealed class TraySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AiDevHelpers.Tests", Guid.NewGuid().ToString("N"));

    public TraySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path_() => Path.Combine(_dir, "tray-settings.json");

    [Fact]
    public void AMissingFileOpensTheWindowOnStart()
    {
        Assert.Equal(StartupMode.Open, TraySettings.Read(Path_()).Startup);
    }

    [Fact]
    public void TheChosenModeIsSavedAndRead()
    {
        TraySettings.Write(Path_(), StartupMode.Tray);

        Assert.Equal(StartupMode.Tray, TraySettings.Read(Path_()).Startup);
    }

    [Fact]
    public void AnUnknownModeFallsBackToOpen()
    {
        File.WriteAllText(Path_(), """{"startup":"sideways"}""");

        Assert.Equal(StartupMode.Open, TraySettings.Read(Path_()).Startup);
    }

    [Fact]
    public void AnUnreadableFileFallsBackToOpen()
    {
        File.WriteAllText(Path_(), "not json");

        Assert.Equal(StartupMode.Open, TraySettings.Read(Path_()).Startup);
    }
}
