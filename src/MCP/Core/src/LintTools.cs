using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AiDevHelpers.Helpers.LintHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Lint setup checks: .editorconfig against csproj NoWarn, tsconfig strict, ESLint and TSLint, dotnet format.</summary>
[McpServerToolType]
internal sealed class LintTools
{
    [McpServerTool]
    [Description("Check a folder's lint setup for mismatches: .editorconfig vs csproj NoWarn, tsconfig strict, ESLint flat config for TypeScript, deprecated TSLint. " +
                 "Set runDotnetFormat to also run 'dotnet format --verify-no-changes' on the root solution.")]
    public static string LintCheck(
        [Description("Folder to check, usually the repo root")] string root,
        [Description("Run dotnet format --verify-no-changes on the root solution (slower)")] bool runDotnetFormat = false)
    {
        try
        {
            var findings = new LintService(new LintProcessCommandRunner()).Analyze(root, runDotnetFormat);
            return Json(new { ok = true, count = findings.Count, findings });
        }
        catch (DirectoryNotFoundException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

/// <summary>Runs a command and captures stdout and stderr together.</summary>
internal sealed class LintProcessCommandRunner : ICommandRunner
{
    public (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(Strings.Format("CouldNotStart", fileName));
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }
}
