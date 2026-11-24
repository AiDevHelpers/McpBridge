using AiDevHelpers.ScriptHelper.Core;

namespace AiDevHelpers.ScriptHelper.Client;

/// <summary>The console side of a run: the broker, the repo manifest, the person's answers, and the elevated setup steps.</summary>
internal sealed class RunHost(string repo, string script, string[] scriptArgs, bool visible, bool allowNoTpm) : IRunHost
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(10);

    private readonly BrokerLink _link = Broker.Link();

    public async Task<ClientResult> SendAsync(CancellationToken ct)
    {
        var ensured = await Broker.EnsureAsync(_link, ct);
        if (!ensured.Ok)
        {
            return new ClientResult(false, ensured.Reason, null, string.Empty, string.Empty);
        }

        return visible
            ? await _link.Client.InteractiveAsync(repo, script, scriptArgs, ScriptTimeout)
            : await _link.Client.RunAsync(repo, script, scriptArgs, ScriptTimeout);
    }

    public bool ScriptInManifest()
    {
        try
        {
            var parsed = ManifestParser.Parse(File.ReadAllText(Path.Combine(repo, ".elevated.json")));
            return parsed.Ok && Allowlist.IsPathListed(parsed.Entries, script);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Ask(string question)
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.Error.Write($"{question} [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes";
    }

    public SetupResult ElevatedStep(RunStep step)
    {
        string[] setupArgs = step switch
        {
            RunStep.Register => ["register", repo],
            RunStep.AllowScript => ["allow-script", repo, script, .. scriptArgs],
            RunStep.Approve => ["approve", repo, script],
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "no setup step for this"),
        };

        var exit = ElevatedSetup.Run(setupArgs, allowNoTpm);
        return new SetupResult(exit == 0, exit == 0 ? "done" : "that step did not finish");
    }

    public void Say(string text) => Console.Error.WriteLine(text);
}
