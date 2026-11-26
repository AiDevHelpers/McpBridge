using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Common.Processes;
using AiDevHelpers.Helpers.LintHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>Lint setup checks: .editorconfig against csproj NoWarn, tsconfig strict, ESLint and TSLint, dotnet format.</summary>
[McpServerToolType]
internal sealed class LintTools
{
    [McpServerTool]
    [Description("Check a folder's lint setup for mismatches: .editorconfig vs csproj NoWarn, tsconfig strict, ESLint flat config for TypeScript, deprecated TSLint, duplicate resource names. " +
                 "Set runDotnetFormat to also run 'dotnet format --verify-no-changes' on the root solution.")]
    public static async Task<string> LintCheck(
        [Description("Folder to check, usually the repo root")] string root,
        [Description("Run dotnet format --verify-no-changes on the root solution (slower)")] bool runDotnetFormat = false)
    {
        try
        {
            var findings = await new LintService(new LintProcessCommandRunner()).AnalyzeAsync(root, runDotnetFormat);
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
    public async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct = default)
    {
        var result = await CommandRunner.RunAsync(
            new CommandRequest { FileName = fileName, Arguments = arguments, WorkingDirectory = workingDirectory },
            ct);
        if (!result.Launched)
        {
            throw new InvalidOperationException(Strings.Format("CouldNotStart", fileName));
        }

        return (result.ExitCode, result.StdOut + result.StdErr);
    }
}
