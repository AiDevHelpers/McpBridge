using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDevHelpers.MCP.Core.Services;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// MCP surface for running a script from a repo. It is the one entry for scripts the AI runs: it picks the interpreter, applies a
/// no-output watchdog and a timeout, and notices a script that fails again and again or hangs.
/// </summary>
[McpServerToolType]
internal sealed class ScriptRunTools(ScriptRunService service, IOptions<ScriptWatchOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [McpServerTool]
    [Description(
        "Run a script from a repo (.ps1, .cmd, .bat, .sh, .py, .js) with the interpreter that fits it: PowerShell 7 or 5.1 from " +
        "#Requires, cmd, Git Bash, Python or Node. Output is captured, the run is stopped after the timeout or after a stretch with no " +
        "output, and arguments are passed as data with no shell in between. repoPath is a folder inside the repo; script is a path " +
        "inside it. The result says which interpreter ran and why. When 'advice' is present the script looks stuck (it keeps " +
        "failing or hangs): ask the user, and only if they agree call again with interactive true, which runs it in a visible terminal tab on their desktop so they see prompts and errors the script does not report (not elevated). If the interpreter is missing, the result says how to get it; nothing is installed.")]
    public async Task<string> ScriptRun(string repoPath, string script, string[]? args = null, bool interactive = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(script))
        {
            return Strings.Get("ScriptRunNeedsRepo");
        }

        var report = await service.RunAsync(repoPath, script, args ?? [], options.Value, ct, interactive);
        return JsonSerializer.Serialize(report, Json);
    }
}
