using System.CommandLine;
using AiDevHelpers.Common;
using AiDevHelpers.ScriptHelper.Client;
using AiDevHelpers.ScriptHelper.Core;

// One helper exe. Approved scripts run through the broker unelevated; the setup verbs need an elevated shell and get one through a single
// UAC prompt. Exit 0 done, 1 refused, 2 bad usage.

var allowNoTpm = new Option<bool>("--allow-no-tpm") { Recursive = true };
var visible = new Option<bool>("--visible");
var repo = new Argument<string>("repo");
var script = new Argument<string>("script");
var tool = new Argument<string>("name");
var verbRepo = new Argument<string>("repo");
var verbScript = new Argument<string>("script");
var prepare = new Option<bool>("--prepare");
var confirm = new Option<string[]>("--confirm");
var all = new Option<bool>("--all");
var ruleId = new Argument<string>("id");

var root = new RootCommand("Script Helper client: runs approved scripts and does the elevated setup steps.");
root.Add(allowNoTpm);

var run = Verb("run", "Run an approved script for a repo through the broker.", async (pr, ct) =>
{
    var host = new RunHost(pr.GetRequiredValue(repo), pr.GetRequiredValue(script), pr.UnmatchedTokens.ToArray(), pr.GetValue(visible), pr.GetValue(allowNoTpm));
    return Broker.Print(await RunFlow.RunAsync(host, ct));
}, passUnmatched: true);
run.Add(repo);
run.Add(script);
run.Add(visible);

var verify = Verb("verify", "Print the approval status of the repo's allowlisted scripts. Read only; never starts the broker.", async (pr, ct) =>
{
    var verified = await Broker.Link().Client.VerifyAsync(pr.GetRequiredValue(repo), TimeSpan.FromSeconds(15));
    if (!verified.Ok)
    {
        Console.Error.WriteLine($"unverified: {verified.Reason}");
        return 2;
    }

    Console.Out.WriteLine(verified.Stdout);
    return verified.ExitCode ?? 2;
});
verify.Add(repo);

var installTool = Verb("install-tool", "Install an allowed tool through the broker.", async (pr, ct) =>
{
    var name = pr.GetRequiredValue(tool);
    if (InstallableTools.TryGet(name) is not { } found)
    {
        Console.Error.WriteLine($"refused: '{name}' is not a tool this helper installs");
        return 2;
    }

    var link = Broker.Link();
    var ensured = await Broker.EnsureAsync(link, ct);
    if (!ensured.Ok)
    {
        Console.Error.WriteLine($"refused: {ensured.Reason}");
        return 1;
    }

    var result = await link.Client.RunAsync(InstallToolFiles.ToolsRoot(link.Config), InstallToolFiles.ScriptName, [found.Name], TimeSpan.FromMinutes(10));
    return Broker.Print(result);
});
installTool.Add(tool);

var repoScan = Verb("repo-scan", "Print the repo scan as JSON. Local; needs no broker.", Sync(pr => Local(["--repo-scan", pr.GetRequiredValue(repo)])));
repoScan.Add(repo);

var repoSetup = Verb("repo-setup", "Interactive setup for a repo. Local; needs no broker.", Sync(pr => Local(["repo-setup", pr.GetRequiredValue(repo)])));
repoSetup.Add(repo);

var rules = new Command("rules", "Rule overrides for a repo.");
var rulesOverride = Verb("override", "Override one rule for a repo.", Sync(pr => Local(["rules", "override", pr.GetRequiredValue(repo), pr.GetRequiredValue(ruleId)])));
rulesOverride.Add(repo);
rulesOverride.Add(ruleId);
rules.Add(rulesOverride);

var terminals = new Command("terminals", "Terminal setup. Local; needs no broker.");
terminals.Add(Verb("scan", "Scan the terminals.", Sync(_ => Local(["terminals", "scan"]))));
var terminalsSetup = Verb("setup", "Set up the terminals.", Sync(pr => Local(pr.GetValue(all) ? ["terminals", "setup", "--all"] : ["terminals", "setup"])));
terminalsSetup.Add(all);
terminals.Add(terminalsSetup);

var register = SetupVerb("register", "Register a repo as a project (administrator).", pr => ["register", pr.GetRequiredValue(repo)]);
register.Add(repo);

var approve = Verb("approve", "Approve a manifest-listed script for this user (administrator).", Sync(pr =>
{
    var values = pr.GetValue(confirm);
    if (values is not null && values.Length is not (0 or 2))
    {
        Console.Error.WriteLine("usage: approve <repo> <script> [--prepare | --confirm <fingerprint> <allowlistHash>]");
        return 2;
    }

    var setupArgs = new List<string> { "approve", pr.GetRequiredValue(verbRepo), pr.GetRequiredValue(verbScript) };
    if (values is { Length: 2 })
    {
        setupArgs.AddRange(["--confirm", values[0], values[1]]);
    }
    else if (pr.GetValue(prepare))
    {
        setupArgs.Add("--prepare");
    }

    return ElevatedSetup.Run(setupArgs.ToArray(), pr.GetValue(allowNoTpm));
}));
approve.Add(verbRepo);
approve.Add(verbScript);
approve.Add(prepare);
approve.Add(confirm);

var allowAll = SetupVerb("allow-all", "Let a registered repo's scripts run without per-script approval (administrator).", pr => ["allow-all", pr.GetRequiredValue(repo)]);
allowAll.Add(repo);

var allowScript = Verb("allow-script", "Add a script to this user's allows for a repo (administrator). Extra tokens are the script's arguments.", Sync(pr =>
    ElevatedSetup.Run(["allow-script", pr.GetRequiredValue(verbRepo), pr.GetRequiredValue(verbScript), .. pr.UnmatchedTokens], pr.GetValue(allowNoTpm))), passUnmatched: true);
allowScript.Add(verbRepo);
allowScript.Add(verbScript);

var review = SetupVerb("review", "Review a repo's approvals (administrator).", pr => ["verify", pr.GetRequiredValue(repo)]);
review.Add(repo);

var scanLaunch = SetupVerb("scan-launch", "Scan a repo's launch and debug configurations (administrator).", pr => ["scan", pr.GetRequiredValue(repo)]);
scanLaunch.Add(repo);

var tools = SetupVerb("tools", "Install the Script Helper tools (administrator).", _ => ["tools"]);

var elevatedStep = Verb("elevated-step", "Runs one setup step in an elevated process. Launched by the client, not by hand.", Sync(pr =>
{
    if (!new WindowsElevationCheck().IsElevated())
    {
        Console.Error.WriteLine("refused: setup needs an elevated shell");
        return 1;
    }

    var exit = SetupHost.Run(pr.UnmatchedTokens.ToArray(), pr.GetValue(allowNoTpm), confirmRegister: true);
    if (exit != 0 && !Console.IsInputRedirected)
    {
        // This window closes when the process ends, so a failure would vanish before it could be read.
        Console.Error.WriteLine("The step did not finish. Press Enter to close this window.");
        Console.ReadLine();
    }

    return exit;
}), passUnmatched: true);
elevatedStep.Hidden = true;

root.Add(run);
root.Add(verify);
root.Add(installTool);
root.Add(repoScan);
root.Add(repoSetup);
root.Add(rules);
root.Add(terminals);
root.Add(register);
root.Add(approve);
root.Add(allowAll);
root.Add(allowScript);
root.Add(review);
root.Add(scanLaunch);
root.Add(tools);
root.Add(elevatedStep);

return await root.Parse(args).InvokeAsync();

static Command Verb(string name, string description, Func<ParseResult, CancellationToken, Task<int>> action, bool passUnmatched = false)
{
    var command = new Command(name, description) { TreatUnmatchedTokensAsErrors = !passUnmatched };
    command.SetAction(action);
    return command;
}

static Func<ParseResult, CancellationToken, Task<int>> Sync(Func<ParseResult, int> action) =>
    (pr, _) => Task.FromResult(action(pr));

Command SetupVerb(string name, string description, Func<ParseResult, string[]> build) =>
    Verb(name, description, Sync(pr => ElevatedSetup.Run(build(pr), pr.GetValue(allowNoTpm))));

static int Local(string[] args) => ElevLocalCommands.TryRun(args, Console.Out, new ConsoleConfirmer(), out var exit) ? exit : 2;
