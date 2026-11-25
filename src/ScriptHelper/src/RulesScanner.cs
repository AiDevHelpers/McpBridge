using System.Text.RegularExpressions;

namespace AiDevHelpers.ScriptHelper.Core;

/// <summary>An instruction file that talks about branches, checks or releases, with the lines that matched (a few, trimmed).</summary>
public sealed record InstructionFile(string Path, IReadOnlyList<string> Lines);

/// <summary>A CI workflow: the branches its triggers name and its job names (the names GitHub shows as checks).</summary>
public sealed record WorkflowInfo(string Path, IReadOnlyList<string> Branches, IReadOnlyList<string> Jobs);

/// <summary>
/// What a repo's own files say about branches, CI and protections. The prose in instruction files is only quoted for a person to
/// read; only the workflow triggers and job names become candidates for structured rules, and a person still confirms each.
/// </summary>
public sealed record RulesReport(
    string? DefaultBranch,
    IReadOnlyList<InstructionFile> Instructions,
    IReadOnlyList<WorkflowInfo> Workflows,
    IReadOnlyList<string> Hooks,
    string? CodeOwners,
    IReadOnlyList<string> CandidateProtectedBranches,
    IReadOnlyList<string> CandidateRequiredChecks);

/// <summary>Reads a repo's instruction files, CI definitions, hooks and CODEOWNERS. Reads only, inside the repo, with size limits.</summary>
public static class RulesScanner
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxInstructionFiles = 40;
    private const int MaxLinesPerFile = 5;

    private static readonly string[] RootInstructionNames = ["CLAUDE.md", "AGENTS.md", "CONTRIBUTING.md"];
    private static readonly string[] CodeOwnerPaths = ["CODEOWNERS", ".github/CODEOWNERS", "docs/CODEOWNERS"];

    private static readonly Regex RuleLine = new(
        @"protected branch|branch protection|required check|required status|pull request|merge into|never push|do not push|force[- ]push|\bgate\b|default branch",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BranchesLine = new(@"^(?<indent>\s*)branches:\s*(?<inline>\[[^\]]*\])?\s*(#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ListItem = new(@"^\s*-\s*['""]?(?<value>[^'""#\s][^'""#]*?)['""]?\s*(#.*)?$", RegexOptions.CultureInvariant);

    public static RulesReport Scan(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var workflows = Workflows(full);

        return new RulesReport(
            DefaultBranch(full),
            Instructions(full),
            workflows,
            Hooks(full),
            CodeOwnerPaths.FirstOrDefault(p => File.Exists(Path.Combine(full, p.Replace('/', Path.DirectorySeparatorChar)))),
            workflows.SelectMany(w => w.Branches).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
            workflows.SelectMany(w => w.Jobs).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList());
    }

    // "ref: refs/remotes/origin/main" in the clone's origin HEAD, when it has one.
    private static string? DefaultBranch(string root)
    {
        var text = ReadText(Path.Combine(root, ".git", "refs", "remotes", "origin", "HEAD"));
        const string prefix = "ref: refs/remotes/origin/";
        return text is not null && text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..].Trim() : null;
    }

    private static List<InstructionFile> Instructions(string root)
    {
        var found = new List<InstructionFile>();
        foreach (var (_, file) in RepoWalk.Files(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var isInstruction = RootInstructionNames.Contains(rel, StringComparer.OrdinalIgnoreCase)
                || (rel.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) && rel.Contains("pull_request_template", StringComparison.OrdinalIgnoreCase))
                || (rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                || (rel.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
            if (!isInstruction || ReadText(file) is not { } text)
                continue;

            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => RuleLine.IsMatch(l))
                .Select(l => l.Length <= 200 ? l : l[..200])
                .Take(MaxLinesPerFile)
                .ToList();
            if (lines.Count > 0)
                found.Add(new InstructionFile(rel, lines));

            if (found.Count >= MaxInstructionFiles)
                break;
        }

        return [.. found.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    private static List<WorkflowInfo> Workflows(string root)
    {
        var dir = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(f => (File: f, Text: ReadText(f)))
            .Where(w => w.Text is not null)
            .Select(w => ParseWorkflow(".github/workflows/" + Path.GetFileName(w.File), w.Text!))
            .ToList();
    }

    // A line-based reading of the parts of a workflow that matter here; it does not claim to parse all of YAML.
    private static WorkflowInfo ParseWorkflow(string path, string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var branches = new List<string>();
        var jobs = new List<string>();
        var jobsAt = Array.FindIndex(lines, l => l.StartsWith("jobs:", StringComparison.Ordinal));
        var header = jobsAt < 0 ? lines : lines[..jobsAt];

        for (var i = 0; i < header.Length; i++)
        {
            if (BranchesLine.Match(header[i]) is not { Success: true } m)
                continue;

            if (m.Groups["inline"].Success)
            {
                branches.AddRange(m.Groups["inline"].Value.Trim('[', ']').Split(',').Select(b => b.Trim().Trim('\'', '"')).Where(b => b.Length > 0));
                continue;
            }

            var indent = m.Groups["indent"].Length;
            for (var j = i + 1; j < header.Length; j++)
            {
                if (string.IsNullOrWhiteSpace(header[j]))
                    continue;

                var lead = header[j].Length - header[j].TrimStart().Length;
                if (lead < indent || ListItem.Match(header[j]) is not { Success: true } item)
                    break;

                branches.Add(item.Groups["value"].Value.Trim());
            }
        }

        if (jobsAt >= 0)
        {
            // A job id sits at the first indent under 'jobs:'; its own keys (name, runs-on, steps) sit at the next indent in.
            int? jobIndent = null;
            int? propertyIndent = null;
            string? currentId = null;
            string? currentName = null;
            void Flush()
            {
                if (currentId is not null)
                    jobs.Add(currentName ?? currentId);
            }

            foreach (var line in lines.Skip(jobsAt + 1))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;

                var lead = line.Length - line.TrimStart().Length;
                if (lead == 0)
                    break;

                jobIndent ??= lead;
                var trimmed = line.Trim();
                if (lead == jobIndent && trimmed.EndsWith(':') && !trimmed.StartsWith('-'))
                {
                    Flush();
                    currentId = trimmed[..^1].Trim('\'', '"');
                    currentName = null;
                    propertyIndent = null;
                    continue;
                }

                if (lead > jobIndent)
                {
                    propertyIndent ??= lead;
                    if (lead == propertyIndent && currentName is null && trimmed.StartsWith("name:", StringComparison.Ordinal))
                    {
                        var name = trimmed["name:".Length..].Trim().Trim('\'', '"');
                        if (name.Length > 0 && !name.Contains("${{", StringComparison.Ordinal))
                            currentName = name;
                    }
                }
            }

            Flush();
        }

        return new WorkflowInfo(path, branches.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), jobs);
    }

    private static List<string> Hooks(string root)
    {
        var hooks = new List<string>();
        foreach (var folder in new[] { ".githooks", ".husky" })
        {
            var dir = Path.Combine(root, folder);
            if (Directory.Exists(dir))
            {
                hooks.AddRange(Directory.EnumerateFiles(dir).Select(f => folder + "/" + Path.GetFileName(f)).Order(StringComparer.Ordinal));
            }
        }

        var config = ReadText(Path.Combine(root, ".git", "config"));
        if (config is not null && Regex.Match(config, @"hooksPath\s*=\s*(?<p>\S+)") is { Success: true } m)
            hooks.Add("core.hooksPath = " + m.Groups["p"].Value);

        return hooks;
    }

    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
