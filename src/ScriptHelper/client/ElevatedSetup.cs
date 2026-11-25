using System.ComponentModel;
using System.Diagnostics;
using AiDevHelpers.ScriptHelper.Core;

namespace AiDevHelpers.ScriptHelper.Client;

/// <summary>Runs setup steps with administrator rights: in this process when it is elevated, otherwise in one UAC-prompted copy of this exe.</summary>
internal static class ElevatedSetup
{
    public static int Run(string[] setupArgs, bool allowNoTpm = false) =>
        new WindowsElevationCheck().IsElevated()
            ? SetupHost.Run(setupArgs, allowNoTpm)
            : SelfElevation.Run(setupArgs, allowNoTpm);
}

/// <summary>
/// Relaunches this exe elevated, so one UAC prompt covers the step. The elevated copy runs in its own console window, where the
/// person answers the confirmations.
/// </summary>
internal static class SelfElevation
{
    private const int ErrorCancelled = 1223;

    public static int Run(string[] setupArgs, bool allowNoTpm)
    {
        var tokens = new List<string> { "elevated-step" };
        if (allowNoTpm)
        {
            tokens.Add("--allow-no-tpm");
        }

        tokens.AddRange(setupArgs);

        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = WindowsArguments.Join(tokens),
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                Console.Error.WriteLine("refused: the elevated copy did not start");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            Console.Error.WriteLine("the administrator prompt was declined");
            return 1;
        }
        catch (Win32Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
