using AiDevHelpers.Installers.Vs2026.Core;
using AiDevHelpers.Bridge;
using System;
using System.ComponentModel.Design;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AiDevHelpers.Installers.Vs2026.Installer;

/// <summary>Visual Studio menu commands for opening bridge pages and managing its listener.</summary>
[SuppressMessage("Usage", "VSTHRD010:Invoke main-thread members", Justification = "Menu command callbacks execute on the Visual Studio UI thread.")]
internal sealed class BridgeStatusCommand
{
    private const int ShowStatusCommandId = 0x0100;
    private const int StartCommandId = 0x0101;
    private const int StopCommandId = 0x0102;
    private const int SettingsCommandId = 0x0103;
    private const int ServicesCommandId = 0x0104;
    private const int LogsCommandId = 0x0105;
    private static readonly Guid CommandSet = new("75A66F44-1E3A-460D-A985-E49344792805");
    private readonly AsyncPackage _package;

    private BridgeStatusCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        Add(commandService, ShowStatusCommandId, ShowStatus);
        Add(commandService, StartCommandId, Start);
        Add(commandService, StopCommandId, Stop);
        Add(commandService, SettingsCommandId, (_, _) => ShowPage(BridgeToolWindow.SettingsPage));
        Add(commandService, ServicesCommandId, (_, _) => ShowPage(BridgeToolWindow.ServicesPage));
        Add(commandService, LogsCommandId, (_, _) => ShowPage(BridgeToolWindow.LogsPage));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (await package.GetServiceAsync(typeof(IMenuCommandService))) as OleMenuCommandService;
        if (commandService is null)
        {
            throw new InvalidOperationException(AiDevHelpers.Installers.Vs2026.Core.Strings.Get("MenuCommandServiceUnavailable"));
        }

        _ = new BridgeStatusCommand(package, commandService);
    }

    private static void Add(OleMenuCommandService commandService, int commandId, EventHandler handler) =>
        commandService.AddCommand(new MenuCommand(handler, new CommandID(CommandSet, commandId)));

    private void ShowPage(int page) => _ = _package.JoinableTaskFactory.RunAsync(() => ShowPageAsync(page));

    private async Task ShowPageAsync(int page)
    {
        try
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = _package.FindToolWindow(typeof(BridgeToolWindow), 0, true) as BridgeToolWindow;
            if (window is null)
            {
                VsShellUtilities.ShowMessageBox(_package, AiDevHelpers.Installers.Vs2026.Core.Strings.Get("BridgeWindowNotCreated"),
                    "AiDevHelpers IDE Bridge", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            window.SelectPage(page);
            await _package.ShowToolWindowAsync(typeof(BridgeToolWindow), 0, true, _package.DisposalToken);
        }
        catch (Exception ex)
        {
            BridgeLog.Write("ERROR", $"Could not open IDE Bridge page {page}: {ex}");
        }
    }

    private void ShowStatus(object sender, EventArgs e)
    {
        BridgeLog.Write("INFO", $"Status requested: {BridgeControl.Describe()}");
    }

    private void Start(object sender, EventArgs e)
    {
        BridgeLog.Write("INFO", BridgeControl.Start());
    }

    private void Stop(object sender, EventArgs e)
    {
        BridgeLog.Write("INFO", BridgeControl.Stop());
    }
}
