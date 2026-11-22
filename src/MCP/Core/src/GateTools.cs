using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.GateHelper;
using AiDevHelpers.Helpers.PackageHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Package setup checks and the full gate (D3, D6). gate_run executes the repo's build and test commands.</summary>
[McpServerToolType]
internal sealed class GateTools
{
    [McpServerTool]
    [Description("Check NuGet, pip and npm package setup in a folder: nuget.config, Directory.Packages.props, xunit analyzers, " +
                 "pinned requirements, and npm lockfiles. Reads files only; nothing is installed.")]
    public static string PackageCheck(
        [Description("Folder to check, usually the repo root")] string root)
    {
        try
        {
            var findings = PackageChecks.Analyze(root);
            return Json(new { ok = true, count = findings.Count, findings });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Show the full gate steps for a repo without running them: the commands in .claude/reference/full-gate.md, or the steps detected from the stack.")]
    public static string GatePlan(
        [Description("Repo root")] string root)
    {
        try
        {
            var steps = GatePlanner.Plan(root);
            return Json(new { ok = true, steps = steps.Select(s => new { name = s.Name, command = s.Tool + " " + string.Join(" ", s.Arguments), allowFailure = s.AllowFailure }) });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Run the full gate for a repo, step by step, and stop at the first failure. Runs build and test commands in the repo.")]
    public static async Task<string> GateRun(
        [Description("Repo root")] string root)
    {
        try
        {
            var results = await GateRunner.RunAsync(GatePlanner.Plan(root), root, new GateProcessRunner());
            var passed = results.All(r => r.Passed);
            return Json(new
            {
                ok = passed,
                steps = results.Select(r => new { name = r.Name, command = r.Command, passed = r.Passed, skipped = r.Skipped, exitCode = r.ExitCode, output = Tail(r.Output) }),
            });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Tail(string output) => output.Length <= 4000 ? output : output[^4000..];

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

/// <summary>Runs one gate step through cmd, so npm and pip (which are .cmd on Windows) resolve.</summary>
internal sealed class GateProcessRunner : IGateRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(GateStep step, string workingDirectory, CancellationToken ct = default)
    {
        var arguments = new List<string> { "/d", "/c", step.Tool };
        arguments.AddRange(step.Arguments);

        var result = await CommandRunner.RunAsync(
            new CommandRequest { FileName = "cmd.exe", Arguments = arguments, WorkingDirectory = workingDirectory },
            ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Format("CouldNotStart", step.Tool));
        }

        return (result.ExitCode, result.StdOut + result.StdErr);
    }
}
