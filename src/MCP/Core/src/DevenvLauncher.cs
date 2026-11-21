using System.Diagnostics;

namespace AiDevHelpers.MCP.Core.Services;

/// <summary>Opens a solution in Visual Studio by launching devenv with the .sln path. Does not use the IDE bridge.</summary>
internal static class DevenvLauncher
{
    private const string VsWhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";

    /// <summary>Finds devenv.exe of the newest stable Visual Studio 18 install, or null.</summary>
    public static string? Find()
    {
        if (!File.Exists(VsWhere))
        {
            return null;
        }

        var psi = new ProcessStartInfo(VsWhere)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-latest");
        psi.ArgumentList.Add("-products");
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add("-version");
        psi.ArgumentList.Add("[18.0,19.0)");
        psi.ArgumentList.Add("-property");
        psi.ArgumentList.Add("installationPath");

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var installation = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var devenv = Path.Combine(installation, "Common7", "IDE", "devenv.exe");
        return installation.Length > 0 && File.Exists(devenv) ? devenv : null;
    }

    /// <summary>Starts devenv with the solution. Returns an error message, or null when the launch started.</summary>
    public static string? Open(string solutionPath)
    {
        var devenv = Find();
        if (devenv is null)
        {
            return "Visual Studio 18 (devenv.exe) was not found.";
        }

        Process.Start(new ProcessStartInfo(devenv) { UseShellExecute = false, ArgumentList = { solutionPath } });
        return null;
    }
}
