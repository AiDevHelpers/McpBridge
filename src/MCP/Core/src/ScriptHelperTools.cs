using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>The outcome of one elev run: its exit code and the combined output it printed.</summary>
public sealed record ElevRunResult(int ExitCode, string Output);

/// <summary>Runs an approved script through elev.exe. The seam keeps the tool testable without elevation.</summary>
public interface IElevRunner
{
    Task<ElevRunResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Starts elev.exe from the MCP server's folder and collects what it prints.</summary>
internal sealed class ProcessElevRunner : IElevRunner
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    public async Task<ElevRunResult> RunAsync(string projectRoot, string script, IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "elev.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException(Strings.Format("ElevNotFound", exe));
        }

        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(projectRoot);
        info.ArgumentList.Add(script);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException(Strings.Get("ElevDidNotStart"));

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await stdout + await stderr;
        return new ElevRunResult(process.ExitCode, output);
    }
}

/// <summary>
///     MCP surface for Script Helper: runs an allowlisted script elevated through the running broker.
///     The script must be listed in the project's .elevated.json and approved by a person.
/// </summary>
[McpServerToolType]
internal sealed class ScriptHelperTools
{
    private readonly IElevRunner _runner;

    public ScriptHelperTools(IElevRunner runner)
    {
        _runner = runner;
    }

    [McpServerTool]
    [Description(
        "Run an approved debug script elevated through the Script Helper broker. projectRoot is the folder with " +
        "the .elevated.json manifest; script is the path relative to it; args are passed to the script in order. " +
        "Returns the exit code and output, or a refusal reason.")]
    public async Task<string> RunElevatedScript(
        string projectRoot,
        string script,
        string[]? args = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(script))
        {
            return Strings.Get("ScriptHelperNeedsArgs");
        }

        try
        {
            var result = await _runner.RunAsync(projectRoot, script, args ?? Array.Empty<string>(), ct);
            var text = new StringBuilder();
            text.Append("exit ").Append(result.ExitCode).AppendLine();
            text.Append(result.Output);
            return text.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Strings.Format("ScriptHelperError", ex.Message);
        }
    }
}
