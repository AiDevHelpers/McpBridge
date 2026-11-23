using System.Text.Json;

namespace AiDevHelpers.Helpers.GateHelper;

/// <summary>One command of the gate. AllowFailure is for reports that are advisory (such as outdated packages).</summary>
public sealed record GateStep(string Name, string Tool, IReadOnlyList<string> Arguments, bool AllowFailure = false);

/// <summary>Runs one gate command. Injected so tests don't start tools.</summary>
public interface IGateRunner
{
    Task<(int ExitCode, string Output)> RunAsync(GateStep step, string workingDirectory, CancellationToken ct = default);
}

/// <summary>The result of one step. Skipped steps never ran because an earlier step failed.</summary>
public sealed record GateStepResult(string Name, string Command, bool Passed, bool Skipped, int ExitCode, string Output);

/// <summary>
/// Full gate (D6). Steps come from <c>.claude/reference/full-gate.md</c> when it exists: each line of its
/// <c>gate</c> code block is one command, split on spaces. Otherwise the stack is detected from root files
/// (dotnet, npm, pip, go, cargo), following the full-gate skill. Lite gate is not defined yet (plan Q1 note).
/// </summary>
public static class GatePlanner
{
    public const string ReferencePath = ".claude/reference/full-gate.md";

    public static IReadOnlyList<GateStep> Plan(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(Strings.Format("FolderNotFound", root));
        }

        var reference = Path.Combine(root, ReferencePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(reference)
            ? FromReference(File.ReadAllText(reference))
            : DetectStack(root);
    }

    /// <summary>Parses the <c>gate</c> code block of a full-gate reference file.</summary>
    public static IReadOnlyList<GateStep> FromReference(string markdown)
    {
        var steps = new List<GateStep>();
        var inBlock = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!inBlock)
            {
                inBlock = line == "```gate";
                continue;
            }

            if (line == "```")
            {
                break;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = SplitArguments(line);
            var allowFailure = parts[0] == "!";
            if (allowFailure)
            {
                parts = parts[1..];
            }

            steps.Add(new GateStep(string.Join(" ", parts), parts[0], parts[1..], allowFailure));
        }

        if (steps.Count == 0)
        {
            throw new InvalidOperationException(Strings.Format("MissingGateBlock", ReferencePath));
        }

        return steps;
    }

    /// <summary>Splits a gate line on spaces. Text inside double quotes stays one argument and the quotes are dropped.</summary>
    private static string[] SplitArguments(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var started = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (started)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }

    /// <summary>Stack detection from root files. Solutions, package.json, Python, Go and Cargo.</summary>
    public static IReadOnlyList<GateStep> DetectStack(string root)
    {
        var steps = new List<GateStep>();

        var solution = Directory.EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (solution is not null)
        {
            var sln = Path.GetFileName(solution);
            steps.Add(new GateStep("dotnet outdated", "dotnet", ["list", sln, "package", "--outdated"], AllowFailure: true));
            steps.Add(new GateStep("dotnet clean", "dotnet", ["clean", sln]));
            steps.Add(new GateStep("dotnet build", "dotnet", ["build", sln, "-c", "Release"]));
            steps.Add(new GateStep("dotnet test", "dotnet", ["test", sln, "-c", "Release"]));
        }

        var packageJson = Path.Combine(root, "package.json");
        if (File.Exists(packageJson))
        {
            steps.Add(new GateStep("npm ci", "npm", ["ci"]));
            steps.Add(new GateStep("npm outdated", "npm", ["outdated"], AllowFailure: true));
            foreach (var script in ScriptsIn(packageJson).Where(s => s is "check" or "lint" or "build" or "test"))
            {
                steps.Add(new GateStep($"npm run {script}", "npm", ["run", script]));
            }
        }

        if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt")))
        {
            steps.Add(new GateStep("pip outdated", "pip", ["list", "--outdated"], AllowFailure: true));
            steps.Add(new GateStep("pytest", "pytest", []));
        }

        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            steps.Add(new GateStep("go vet", "go", ["vet", "./..."]));
            steps.Add(new GateStep("go build", "go", ["build", "./..."]));
            steps.Add(new GateStep("go test", "go", ["test", "./..."]));
        }

        if (File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            steps.Add(new GateStep("cargo build", "cargo", ["build"]));
            steps.Add(new GateStep("cargo test", "cargo", ["test"]));
        }

        if (steps.Count == 0)
        {
            throw new InvalidOperationException(Strings.Format("NoKnownStack", ReferencePath));
        }

        return steps;
    }

    private static IEnumerable<string> ScriptsIn(string packageJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
        return doc.RootElement.TryGetProperty("scripts", out var scripts)
            ? scripts.EnumerateObject().Select(p => p.Name).ToList()
            : [];
    }
}

/// <summary>Runs the steps in order and stops at the first failure. Later steps are reported as skipped.</summary>
public static class GateRunner
{
    public static async Task<IReadOnlyList<GateStepResult>> RunAsync(IReadOnlyList<GateStep> steps, string root, IGateRunner runner, CancellationToken ct = default)
    {
        var results = new List<GateStepResult>();
        var stopped = false;
        foreach (var step in steps)
        {
            var command = step.Tool + " " + string.Join(" ", step.Arguments);
            if (stopped)
            {
                results.Add(new GateStepResult(step.Name, command.Trim(), Passed: false, Skipped: true, ExitCode: -1, Output: string.Empty));
                continue;
            }

            var (code, output) = await runner.RunAsync(step, root, ct);
            var passed = code == 0 || step.AllowFailure;
            results.Add(new GateStepResult(step.Name, command.Trim(), passed, Skipped: false, code, output));
            if (!passed)
            {
                stopped = true;
            }
        }

        return results;
    }
}
