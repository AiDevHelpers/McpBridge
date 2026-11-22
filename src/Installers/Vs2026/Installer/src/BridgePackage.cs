using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AiDevHelpers.Installers.Vs2026.Core;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace AiDevHelpers.Installers.Vs2026.Installer;

/// <summary>Starts this Visual Studio instance's IDE bridge when the package loads. UI and registration only; logic is in the Core library.</summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("AiDevHelpers IDE Bridge", "Named-pipe bridge for IDE tools", "0.1.1")]
[Guid(BridgePackage.PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(BridgeToolWindow))]
[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class BridgePackage : AsyncPackage
{
    public const string PackageGuidString = "774B5915-DC74-4FB8-AE6F-A8EF4A7187F7";

    private BridgeHost? _host;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        int pid;
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            pid = process.Id;
        }

        var dte = (DTE2?)await GetServiceAsync(typeof(DTE))
            ?? throw new InvalidOperationException(AiDevHelpers.Installers.Vs2026.Core.Strings.Get("DteUnavailable"));

        _host = BridgeHost.Start(dte, pid, async run => { await JoinableTaskFactory.SwitchToMainThreadAsync(); return run(); });
        await BridgeStatusCommand.InitializeAsync(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host?.Dispose();
        }

        base.Dispose(disposing);
    }
}
