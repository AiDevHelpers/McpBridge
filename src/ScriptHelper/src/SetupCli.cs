namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>
/// Argument handling for the setup executable. Exit codes: 0 done, 1 refused, 2 bad usage.
/// Kept in the library so it can be tested; the exe only wires in the real parts.
/// </summary>
public static class SetupCli
{
    public const string Usage =
        "usage:\n" +
        "  setup register <projectRoot>\n" +
        "  setup approve <projectRoot> <relativeScript>\n" +
        "Run both from an elevated shell. Add --allow-no-tpm only on a machine without a TPM 2.0.";

    public static int Run(string[] args, ISetupCommands commands, TextWriter output)
    {
        if (args.Length == 2 && args[0] == "register")
        {
            return Report(commands.Register(args[1]), output);
        }

        if (args.Length == 3 && args[0] == "approve")
        {
            return Report(commands.Approve(args[1], args[2]), output);
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
