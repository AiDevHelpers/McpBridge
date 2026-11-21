using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
    public static string GateRun(
        [Description("Repo root")] string root)
    {
        try
        {
            var results = GateRunner.Run(GatePlanner.Plan(root), root, new GateProcessRunner());
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
    public (int ExitCode, string Output) Run(GateStep step, string workingDirectory)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(step.Tool);
        foreach (var argument in step.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {step.Tool}.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }
}
