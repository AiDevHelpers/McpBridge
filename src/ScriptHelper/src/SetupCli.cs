using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Argument handling for the setup executable. Exit codes: 0 done, 1 refused, 2 bad usage.
/// Kept in the library so it can be tested; the exe only wires in the real parts.
/// Approval has two forms: the interactive question (approve), or two steps for a caller that
/// asks the person itself (approve --prepare shows the values; approve --confirm FP AL writes
/// the approval only if they still match).
/// </summary>
public static class SetupCli
{
    public const string Usage =
        "usage:\n" +
        "  setup register <projectRoot>\n" +
        "  setup approve <projectRoot> <relativeScript>\n" +
        "  setup approve <projectRoot> <relativeScript> --prepare\n" +
        "  setup approve <projectRoot> <relativeScript> --confirm <fingerprint> <allowlistHash>\n" +
        "Run from an elevated shell. Add --allow-no-tpm only on a machine without a TPM 2.0.";

    public static int Run(string[] args, ISetupCommands commands, TextWriter output)
    {
        if (args.Length == 2 && args[0] == "register")
        {
            return Report(commands.Register(args[1]), output);
        }

        if (args.Length == 2 && args[0] == "allow-all")
        {
            return Report(commands.AllowAll(args[1]), output);
        }

        if (args.Length >= 3 && args[0] == "approve")
        {
            return Approve(args, commands, output);
        }

        output.WriteLine(Usage);
        return 2;
    }

    private static int Approve(string[] args, ISetupCommands commands, TextWriter output)
    {
        var root = args[1];
        var script = args[2];

        if (args.Length == 3)
        {
            return Report(commands.Approve(root, script), output);
        }

        if (args.Length == 4 && args[3] == "--prepare")
        {
            var preview = commands.PrepareApproval(root, script);
            if (!preview.Ok)
            {
                output.WriteLine($"refused: {preview.Reason}");
                return 1;
            }

            output.WriteLine(preview.Prompt);
            output.WriteLine($"To approve, run: approve {root} {script} --confirm {preview.Fingerprint} {preview.AllowlistHash}");
            return 0;
        }

        if (args.Length == 6 && args[3] == "--confirm")
        {
            return Report(commands.ConfirmApproval(root, script, args[4], args[5]), output);
        }

        output.WriteLine(Usage);
        return 2;
    }

    private static int Report(SetupResult result, TextWriter output)
    {
        output.WriteLine(result.Ok ? result.Reason : $"refused: {result.Reason}");
        return result.Ok ? 0 : 1;
    }
}
