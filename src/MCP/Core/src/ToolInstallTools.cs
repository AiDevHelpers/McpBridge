using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDevHelpers.MCP.Core.Services;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// MCP surface for installing a missing tool (PowerShell 7, Git for Windows, Python, Node). Only the tools in the built-in table can be
/// installed, with winget, through the elevated script helper. Two steps like git_plan and git_confirm: the user's yes in chat is the
/// permission, so the AI must ask before confirming.
/// </summary>
[McpServerToolType]
internal sealed class ToolInstallTools(ToolInstallService service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool]
    [Description(
        "Install a missing tool with winget through the elevated script helper. tool is one of pwsh, gitbash, python, node (wsl gets " +
        "instructions only). action 'plan' returns the exact command and a one-time token and installs nothing; ask the user, and only if " +
        "they say yes call action 'confirm' with the token. action 'decline' records that the user said no, so the install is not offered " +
        "again. The elevated helper must have been set up once by the user with 'setup tools'.")]
    public async Task<string> ToolInstall(string action, string tool = "", string token = "", CancellationToken ct = default)
    {
        var outcome = action.ToLowerInvariant() switch
        {
            "plan" => service.Plan(tool),
            "confirm" => await service.ConfirmAsync(token, ct),
            "decline" => service.Decline(tool),
            _ => new InstallOutcome(false, null, Strings.Get("InstallUnknownAction")),
        };

        return JsonSerializer.Serialize(outcome, Json);
    }
}
