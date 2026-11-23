using System.Text.Json;
using AiDevHelpers.Common.Contracts;
using AiDevHelpers.Common.Terminals;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>The machine probe and the per-user choices file the terminal commands work with. The real ones by default.</summary>
public sealed record TerminalServices(ITerminalProbe Probe, TerminalRegistry Registry)
{
    public static TerminalServices System() => new(new SystemTerminalProbe(), TerminalRegistry.ForCurrentUser());
}

/// <summary>
/// <c>elev terminals scan</c> lists the terminal types on this machine. <c>elev terminals setup</c> asks, for each type that is
/// installed but not registered, whether to register it, and adds a Windows Terminal profile when that has none. The question comes
/// from the console, never from arguments. A type that is not installed is only a hint: nothing is installed here.
/// </summary>
public static class TerminalSetup
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Scan(TerminalServices services, TextWriter output)
    {
        output.WriteLine(JsonSerializer.Serialize(new TerminalTypeScanner(services.Probe, services.Registry).Scan(), Json));
        return 0;
    }

    public static int Setup(TerminalServices services, bool askAgain, TextWriter output, IConfirmer confirmer)
    {
        if (askAgain)
            services.Registry.ClearDeclined();

        var scan = new TerminalTypeScanner(services.Probe, services.Registry).Scan();
        foreach (var hint in scan.Hints)
            output.WriteLine($"hint: {hint}");

        var missing = scan.Types.Where(t => t.Missing).ToList();
        if (missing.Count == 0)
        {
            output.WriteLine("Every installed terminal type is already registered or declined.");
            return 0;
        }

        foreach (var type in missing)
        {
            var addProfile = scan.WindowsTerminalFound && !type.InWindowsTerminal;
            var question = $"Register '{type.Name}' ({type.Path}) for running scripts?" + (addProfile ? " Windows Terminal has no profile for it; one will be added." : string.Empty);
            if (!confirmer.Confirm(question))
            {
                services.Registry.Decline(type.Id);
                output.WriteLine($"declined {type.Id}; it will not be asked again (elev terminals setup --all asks again)");
                continue;
            }

            services.Registry.Register(new RegisteredTerminal(type.Id, type.Name, type.Path, type.Version));
            output.WriteLine($"registered {type.Id}");
            if (addProfile)
                AddProfiles(services, type, output);
        }

        return 0;
    }

    // Adds the profile to each Windows Terminal settings file that does not cover the type. A file that cannot be edited safely is left alone.
    private static void AddProfiles(TerminalServices services, TerminalType type, TextWriter output)
    {
        foreach (var settings in services.Probe.WindowsTerminalSettingsFiles())
        {
            var text = services.Probe.ReadText(settings);
            if (text is null || WindowsTerminalSettings.Covers(text, type.Id, type.Name, type.Path, GeneratorOf(type.Id)))
                continue;

            try
            {
                output.WriteLine(WindowsTerminalSettings.AddProfile(settings, type.Id, type.Name, type.CommandLine)
                    ? $"added a Windows Terminal profile to {settings} (backup: {settings}.bak)"
                    : $"left {settings} unchanged: it could not be edited safely");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"left {settings} unchanged: {ex.Message}");
            }
        }
    }

    private static string GeneratorOf(string id) => id switch
    {
        "pwsh" => "Windows.Terminal.PowershellCore",
        "gitbash" => "Git",
        "vsdevshell" => "Windows.Terminal.VisualStudio",
        _ when id.StartsWith("wsl:", StringComparison.Ordinal) => "Windows.Terminal.Wsl",
        _ => string.Empty,
    };
}
