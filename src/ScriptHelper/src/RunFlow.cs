using AiDevHelpers.Common.Contracts;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>What a run still needs before the broker will run the script.</summary>
public enum RunStep
{
    /// <summary>The broker ran it; nothing more to do.</summary>
    Done,

    /// <summary>Register the repo as a project (needs administrator rights).</summary>
    Register,

    /// <summary>Add the script to this user's allows (needs administrator rights; asks first).</summary>
    AllowScript,

    /// <summary>Approve the manifest-listed script's fingerprint (needs administrator rights; the console shows what is approved).</summary>
    Approve,

    /// <summary>Nothing the helper can fix by itself; report the broker's reason.</summary>
    Stop,
}

/// <summary>The next step for a denial, from the broker's own words.</summary>
public static class RunPlanner
{
    public static RunStep Next(ClientResult result, bool scriptInManifest, ISet<RunStep> alreadyDone)
    {
        if (result.Ok)
            return RunStep.Done;

        var reason = result.Reason;
        var step = Classify(reason, scriptInManifest);
        return step == RunStep.Stop || alreadyDone.Contains(step) ? RunStep.Stop : step;
    }

    private static RunStep Classify(string reason, bool scriptInManifest)
    {
        if (reason.Contains("not registered", StringComparison.OrdinalIgnoreCase))
            return RunStep.Register;

        // The user's own allow went stale (the script changed): allow it again, which pins the new fingerprint.
        if (reason.Contains("allow it again", StringComparison.OrdinalIgnoreCase))
            return RunStep.AllowScript;

        // The repo manifest does not allow it and this user has not: offer to add it to the user's allows.
        if (reason.Contains("not allowed by the repo or by this user", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("not in the allowlist", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("arguments do not match", StringComparison.OrdinalIgnoreCase)
            || reason.Contains(".elevated.json", StringComparison.OrdinalIgnoreCase))
        {
            return RunStep.AllowScript;
        }

        // Listed by the team but not approved for this user, or approved for an older version of it.
        if (reason.Contains("not approved", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("approve the script first", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("re-approval needed", StringComparison.OrdinalIgnoreCase))
        {
            return scriptInManifest ? RunStep.Approve : RunStep.AllowScript;
        }

        return RunStep.Stop;
    }
}

/// <summary>What <see cref="RunFlow"/> needs from its surroundings. The real one starts the broker and relaunches itself elevated.</summary>
public interface IRunHost
{
    /// <summary>Makes sure the broker answers (one UAC prompt if it must be started), sends the request and waits for the answer.</summary>
    Task<ClientResult> SendAsync(CancellationToken ct);

    /// <summary>True when the repo's .elevated.json lists the script (so approving it is enough).</summary>
    bool ScriptInManifest();

    /// <summary>Asks the person a yes or no question in the console. Must return false when no person can answer.</summary>
    bool Ask(string question);

    /// <summary>Runs one setup step with administrator rights (the real host relaunches itself elevated once) and returns its outcome.</summary>
    SetupResult ElevatedStep(RunStep step);

    void Say(string text);
}

/// <summary>
/// Runs a script through the broker and does the prerequisite steps it finds missing, one at a time, each at most once: register the
/// project, add the script to the user's allows after asking, approve a manifest-listed script. Allow-all, when active, makes the broker
/// allow everything, so none of the allow steps come up. A step the person declines or that fails ends the run with that reason.
/// </summary>
public static class RunFlow
{
    public static async Task<ClientResult> RunAsync(IRunHost host, CancellationToken ct)
    {
        var done = new HashSet<RunStep>();
        var result = await host.SendAsync(ct);

        for (var round = 0; round < 4; round++)
        {
            var step = RunPlanner.Next(result, host.ScriptInManifest(), done);
            if (step is RunStep.Done or RunStep.Stop)
                return result;

            done.Add(step);
            switch (step)
            {
                case RunStep.Register:
                    host.Say("This repo is not registered with Script Helper yet. Registering needs administrator rights (one prompt).");
                    break;
                case RunStep.AllowScript:
                    if (!host.Ask("This script is not allowed to run elevated. Add it to your allowed scripts for this repo? (Only you, on this machine; the repo is not changed.)"))
                        return result;

                    break;
                case RunStep.Approve:
                    host.Say("The script is listed by the repo but not approved for you yet. You will be shown what is approved.");
                    break;
            }

            var outcome = host.ElevatedStep(step);
            if (!outcome.Ok)
                return new ClientResult(false, outcome.Reason, null, string.Empty, string.Empty);

            host.Say(outcome.Reason);
            result = await host.SendAsync(ct);
        }

        return result;
    }
}
