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
        "  setup tools\n" +
        "  setup allow-script <projectRoot> <relativeScript> [args...]\n" +
        "  setup allow-folder <glob>\n" +
        "Run from an elevated shell. Add --allow-no-tpm only on a machine without a TPM 2.0.";

    public static int Run(string[] args, ISetupCommands commands, TextWriter output, IVerifyCommands? verify = null, IConfirmer? confirmer = null, IToolCommands? tools = null, IAllowCommands? allow = null)
    {
        if (args.Length >= 3 && args[0] == "allow-script" && allow is not null)
        {
            return Report(allow.AllowScript(args[1], args[2], args[3..]), output);
        }

        if (args.Length == 2 && args[0] == "allow-folder" && allow is not null)
        {
            return Report(allow.AllowFolder(args[1]), output);
        }

        if (args.Length == 1 && args[0] == "tools" && tools is not null)
        {
            return Report(tools.InstallTools(), output);
        }

        if (args.Length == 2 && args[0] == "scan" && verify is not null && confirmer is not null)
        {
            return Scan(args[1], verify, confirmer, output);
        }

        if (args.Length == 2 && args[0] == "verify" && verify is not null && confirmer is not null)
        {
            return Verify(args[1], verify, confirmer, output);
        }

        if (args.Length == 2 && args[0] == "register")
        {
            return Report(commands.Register(args[1]), output);
        }

        if (args.Length == 2 && args[0] == "allow-all")
        {
            return Report(commands.AllowAll(args[1]), output);
        }

        if (args.Length == 2 && args[0] == "prune-stale")
        {
            return Report(commands.PruneStale(args[1]), output);
        }

        if (args.Length == 3 && args[0] == "allow-all-approved")
        {
            return Report(commands.AllowAllApproved(args[1], args[2]), output);
        }

        if (args.Length >= 3 && args[0] == "approve")
        {
            return Approve(args, commands, output);
        }

        output.WriteLine(Usage);
        return 2;
    }

    // Lists the launch and debug scripts found, then asks per unlisted one whether to add it to the allowlist.
    private static int Scan(string root, IVerifyCommands verify, IConfirmer confirmer, TextWriter output)
    {
        var report = verify.Scan(root);
        if (!report.Ok)
        {
            output.WriteLine($"refused: {report.Reason}");
            return 1;
        }

        if (report.Scripts.Count == 0)
        {
            output.WriteLine("No launch or debug scripts found.");
            return 0;
        }

        foreach (var script in report.Scripts)
        {
            output.WriteLine($"  {(script.Listed ? "listed" : "new"),-8} {script.Path}  ({script.Source})");
        }

        var results = verify.AddToAllowlist(
            root,
            path => confirmer.Confirm($"Add '{path}' to this repo's allowlist? It is run by a launch or debug configuration. You still approve it separately."));
        foreach (var result in results)
        {
            output.WriteLine(result.Ok ? result.Reason : $"refused: {result.Reason}");
        }

        if (results.Any(r => r.Ok))
        {
            output.WriteLine($"The allowlist changed. Run: setup verify {root}");
        }

        return results.All(r => r.Ok) ? 0 : 1;
    }

    // Lists each allowlisted file's approval status, then asks per file whether to approve the ones that are not approved.
    private static int Verify(string root, IVerifyCommands verify, IConfirmer confirmer, TextWriter output)
    {
        var report = verify.Verify(root);
        if (!report.Ok)
        {
            output.WriteLine($"refused: {report.Reason}");
            return 1;
        }

        if (report.AllowAllActive)
        {
            output.WriteLine("allow-all is active for this repo: any script in it may run elevated.");
        }

        foreach (var script in report.Scripts)
        {
            output.WriteLine($"  {script.Status,-16} {script.Path}");
        }

        if (report.FullyApproved)
        {
            output.WriteLine($"All {report.Scripts.Count} allowlisted scripts are approved.");
            return 0;
        }

        output.WriteLine("Some allowlisted scripts are not approved for you. Approve them one by one:");
        var results = verify.ApproveUnsigned(
            root,
            path => confirmer.Confirm($"Approve '{path}' to run elevated for you? It is in this repo's allowlist, which your team may have changed."));
        foreach (var result in results)
        {
            output.WriteLine(result.Ok ? result.Reason : $"refused: {result.Reason}");
        }

        return verify.Verify(root).FullyApproved ? 0 : 1;
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
