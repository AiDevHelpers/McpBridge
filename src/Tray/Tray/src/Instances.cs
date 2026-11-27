using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using AiDevHelpers.Bridge;

namespace AiDevHelpers.Tray;

/// <summary>
/// The running IDE instances, read on a schedule from the manifests the Bridge already reads (<see cref="VsInstanceRegistry"/>). Emits
/// only when the list changes, and replays the latest list to a new subscriber.
/// </summary>
public static class InstanceFeed
{
    public static IObservable<IReadOnlyList<VsInstance>> Watch(Func<IReadOnlyList<VsInstance>> read, TimeSpan interval, IScheduler scheduler) =>
        Observable.Interval(interval, scheduler)
            .StartWith(0L)
            .Select(_ => read())
            .DistinctUntilChanged(list => string.Join("|", list.Select(i => $"{i.Pid}:{i.Ide}:{i.SolutionPath}")))
            .Replay(1)
            .RefCount();
}

/// <summary>
/// Refuses a manifest the tray should not show. The Bridge's parser checks JSON shape only; this checks the values, since a manifest is
/// written by a process that may not be trusted. Returns the reason, or null when the manifest is valid.
/// </summary>
public static class InstanceValidator
{
    public const int MaxPathLength = 1024;

    public static string? Problem(VsInstance instance)
    {
        if (instance.Pid <= 0)
        {
            return "pid is not positive";
        }

        var expectedPipe = instance.Ide switch
        {
            "VisualStudio" => $"AiDevHelpers.VS.{instance.Pid}",
            "VsCode" => $"AiDevHelpers.VsCode.{instance.Pid}",
            _ => null,
        };
        if (expectedPipe is null)
        {
            return "ide is not VisualStudio or VsCode";
        }

        if (!string.Equals(instance.PipeName, expectedPipe, StringComparison.Ordinal))
        {
            return "pipe name does not match the pid";
        }

        if (instance.SolutionPath is null)
        {
            return null;
        }

        if (instance.SolutionPath.Length > MaxPathLength)
        {
            return "path is too long";
        }

        if (instance.SolutionPath.Any(char.IsControl))
        {
            return "path has a control character";
        }

        return Path.IsPathFullyQualified(instance.SolutionPath) ? null : "path is not absolute";
    }
}

/// <summary>One row of the IDE Instances page. The *Text properties are what the grid shows, with the localized fallbacks.</summary>
public sealed record InstanceRow(int Pid, string Ide, string? Repo, string? Branch, string Path, DateTime? Started, string? User, string? OpenFile = null)
{
    public string OpenFileText => OpenFile ?? Strings.Get("UnknownFile");

    public string CloseText => Strings.Get("CloseButton");

    public string RepoText => Repo ?? Strings.Get("NotARepo");

    public string BranchText => Branch ?? string.Empty;

    public string StartedText => Started is { } started ? started.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    public string UserText => User ?? string.Empty;
}

/// <summary>Puts the open file from the watcher onto each row. A row whose instance has no reading keeps no file.</summary>
public static class InstanceRowFiles
{
    public static IReadOnlyList<InstanceRow> WithFiles(IReadOnlyList<InstanceRow> rows, IReadOnlyDictionary<int, string?> files) =>
        rows.Select(row => row with { OpenFile = files.TryGetValue(row.Pid, out var file) ? file : null }).ToList();
}

/// <summary>
/// Builds a row from a valid manifest. The process facts and the git reads are passed in, so the rules are tested without a real
/// process or repository. Returns null when the process has gone; the row is then dropped.
/// </summary>
public sealed class InstanceRowBuilder(
    Func<int, DateTime?> startedAt,
    Func<int, string?> ownerOf,
    Func<string, string?> rootFor,
    Func<string, string?> branchFor)
{
    public InstanceRow? Build(VsInstance instance)
    {
        var started = startedAt(instance.Pid);
        if (started is null)
        {
            return null;
        }

        string? repo = null;
        string? branch = null;
        if (instance.SolutionPath is not null && rootFor(instance.SolutionPath) is { } root)
        {
            repo = Path.GetFileName(root.TrimEnd('\\', '/'));

            // A repo with no branch name is a detached HEAD.
            // GitWorktree.BranchFor takes the solution path, not the repo root; it finds the root itself.
            branch = branchFor(instance.SolutionPath) ?? "detached";
        }

        return new InstanceRow(instance.Pid, instance.Ide, repo, branch, instance.SolutionPath ?? string.Empty, started, ownerOf(instance.Pid));
    }
}
