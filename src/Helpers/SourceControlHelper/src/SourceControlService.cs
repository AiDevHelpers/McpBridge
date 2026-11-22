using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AiDevHelpers.Helpers.SourceControlHelper;

/// <summary>Runs git. Injected so tests can see the exact arguments.</summary>
public interface IGitRunner
{
    GitResult Run(string workingDirectory, IReadOnlyList<string> arguments);
}

/// <summary>Result of one git call: exit code and combined stdout and stderr.</summary>
public sealed record GitResult(bool Ok, int ExitCode, string Output);

/// <summary>A write waiting for approval. Nothing runs until <see cref="SourceControlService.Confirm"/> is called with its token.</summary>
public sealed record PendingWrite(string Token, string Action, string Description, IReadOnlyList<string> Arguments, string RepoPath);

/// <summary>
/// Git status and diff run at once. Every write (stage, commit, pull, push, branch, switch, stash) is two-step:
/// Plan returns the exact git command and a token, and Confirm runs it once (D7, as in the extension Add flow).
/// Visual Studio has no public git write API, so this runs git.exe for both IDEs (see the plan's Phase 2 spike).
/// </summary>
public sealed class SourceControlService(IGitRunner git)
{
    private readonly ConcurrentDictionary<string, PendingWrite> _pending = new();

    public static readonly IReadOnlyList<string> WriteActions =
        ["stage", "commit", "pull", "push", "create_branch", "delete_branch", "switch", "stash_push", "stash_pop", "stash_apply"];

    public GitStatusResult Status(string repoPath)
    {
        var result = git.Run(repoPath, ["status", "--porcelain=v1", "--branch"]);
        return result.Ok ? GitStatusParser.Parse(result.Output) : new GitStatusResult(null, null, 0, 0, [], result.Output);
    }

    public GitResult Diff(string repoPath, bool staged, string? path = null)
    {
        var args = new List<string> { "diff" };
        if (staged)
        {
            args.Add("--cached");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return git.Run(repoPath, args);
    }

    /// <summary>Plans a write. Returns the exact command and a token; runs nothing.</summary>
    public PendingWrite Plan(string repoPath, string action, IReadOnlyList<string>? paths = null, string? message = null, string? branch = null)
    {
        if (!WriteActions.Contains(action))
        {
            throw new ArgumentException(Strings.Format("UnknownWriteAction", action, string.Join(", ", WriteActions)), nameof(action));
        }

        var args = BuildArguments(action, paths, message, branch);
        var pending = new PendingWrite(
            Token: Guid.NewGuid().ToString("N"),
            Action: action,
            Description: "git " + string.Join(" ", args.Select(Quote)),
            Arguments: args,
            RepoPath: repoPath);
        _pending[pending.Token] = pending;
        return pending;
    }

    /// <summary>Runs a planned write once. An unknown or reused token is refused.</summary>
    public GitResult Confirm(string token)
    {
        if (!_pending.TryRemove(token, out var pending))
        {
            return new GitResult(false, -1, Strings.Get("UnknownToken"));
        }

        return git.Run(pending.RepoPath, pending.Arguments);
    }

    public static IReadOnlyList<string> BuildArguments(string action, IReadOnlyList<string>? paths, string? message, string? branch)
    {
        switch (action)
        {
            case "stage":
                RequireItems(paths, Strings.Get("StageNeedsPath"));
                return ["add", "--", .. paths!];
            case "commit":
                if (string.IsNullOrWhiteSpace(message))
                {
                    throw new ArgumentException(Strings.Get("CommitNeedsMessage"), nameof(message));
                }

                return ["commit", "-m", message!];
            case "pull":
                return ["pull"];
            case "push":
                return string.IsNullOrWhiteSpace(branch) ? ["push"] : ["push", "origin", branch!];
            case "create_branch":
                RequireText(branch, Strings.Get("CreateBranchNeedsName"));
                return ["switch", "-c", branch!];
            case "delete_branch":
                RequireText(branch, Strings.Get("DeleteBranchNeedsName"));
                return ["branch", "-d", branch!];
            case "switch":
                RequireText(branch, Strings.Get("SwitchNeedsName"));
                return ["switch", branch!];
            case "stash_push":
                return ["stash", "push"];
            case "stash_pop":
                return ["stash", "pop"];
            case "stash_apply":
                return ["stash", "apply"];
            default:
                throw new ArgumentException(Strings.Format("UnknownWriteActionName", action), nameof(action));
        }
    }

    private static void RequireItems(IReadOnlyList<string>? items, string message)
    {
        if (items is null || items.Count == 0)
        {
            throw new ArgumentException(message);
        }
    }

    private static void RequireText(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(message);
        }
    }

    private static string Quote(string argument) =>
        argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;
}

/// <summary>Runs git.exe with an argument list (no shell), capturing stdout and stderr.</summary>
public sealed class ProcessGitRunner : IGitRunner
{
    public GitResult Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(Strings.Get("CouldNotStartGit"));
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var output = stdout + stderr.GetAwaiter().GetResult();
        return new GitResult(process.ExitCode == 0, process.ExitCode, output);
    }
}
