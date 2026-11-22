using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Helpers.ExtensionsHelper;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>List, describe and add VS Code and Visual Studio extensions. Adds are two-step and need approval.</summary>
[McpServerToolType]
internal sealed class ExtensionTools
{
    private static readonly Lazy<ExtensionsService> Service = new(() => new ExtensionsService(
        new ProcessCommandRunner(),
        new HttpMarketplaceClient(new HttpClient()),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions"),
        () => VsixInstallerLocator.Find([
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ])));

    [McpServerTool]
    [Description("List installed VS Code extensions (ide = VsCode). Visual Studio listing is not available yet.")]
    public static async Task<string> ExtensionList(
        [Description("VsCode or VisualStudio")] string ide,
        CancellationToken ct = default)
    {
        if (!TryParseIde(ide, out var parsed, out var error))
        {
            return Json(new { ok = false, error });
        }

        if (parsed == ExtensionIde.VisualStudio)
        {
            return Json(new { ok = false, error = Strings.Get("ExtensionListingUnavailable") });
        }

        var list = await Service.Value.ListVsCodeAsync(ct);
        return Json(new { ok = true, count = list.Count, items = list.Select(Project) });
    }

    [McpServerTool]
    [Description("Describe an extension. VsCode reads the installed package.json; VisualStudio queries the Marketplace.")]
    public static async Task<string> ExtensionInfo(
        [Description("VsCode or VisualStudio")] string ide,
        [Description("Extension id, e.g. publisher.name")] string id,
        CancellationToken ct = default)
    {
        if (!TryParseIde(ide, out var parsed, out var error))
        {
            return Json(new { ok = false, error });
        }

        var info = parsed == ExtensionIde.VsCode
            ? Service.Value.GetVsCodeInfo(id)
            : await Service.Value.GetVisualStudioInfoAsync(id, ct);
        return info is null
            ? Json(new { ok = false, error = Strings.Format("ExtensionNotFound", id) })
            : Json(new { ok = true, item = Project(info) });
    }

    [McpServerTool]
    [Description("Plan an extension add. Returns the exact command and a token. Nothing is installed until extension_add_confirm is called with the token and the user has approved it.")]
    public static async Task<string> ExtensionAddPlan(
        [Description("VsCode or VisualStudio")] string ide,
        [Description("Extension id, e.g. publisher.name")] string id,
        CancellationToken ct = default)
    {
        if (!TryParseIde(ide, out var parsed, out var error))
        {
            return Json(new { ok = false, error });
        }

        try
        {
            var pending = await Service.Value.PlanAddAsync(parsed, id, ct);
            return Json(new { ok = true, token = pending.Token, command = pending.Command, id = pending.Id });
        }
        catch (ArgumentException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [McpServerTool]
    [Description("Run a planned extension add. Use only after the user has approved the command returned by extension_add_plan.")]
    public static async Task<string> ExtensionAddConfirm(
        [Description("Token returned by extension_add_plan")] string token,
        CancellationToken ct = default)
    {
        var result = await Service.Value.ConfirmAddAsync(token, ct);
        return Json(new { ok = result.Ok, exitCode = result.ExitCode, output = result.Output });
    }

    [McpServerTool]
    [Description("Recommend the official C# extensions (C# Dev Kit, C#) in the folder's .vscode/extensions.json. " +
                 "When C# Dev Kit is installed, also write dotnet.defaultSolution to .vscode/settings.json. Existing keys are kept.")]
    public static async Task<string> ExtensionWorkspaceSetup(
        [Description("Folder that holds the solution")] string folderPath,
        [Description("Full path of the .sln to make the default solution")] string solutionPath,
        CancellationToken ct = default)
    {
        var result = await new WorkspaceSetup(new ProcessCommandRunner()).SetupAsync(folderPath, solutionPath, ct);
        return Json(new
        {
            ok = result.Ok,
            settingApplied = result.SettingApplied,
            recommended = result.Recommended,
            error = result.Error,
        });
    }

    [McpServerTool]
    [Description("List the Visual Studio installs on this machine (stable and prerelease) with their MSBuild path and whether the VSSDK targets " +
                 "a VSIX build needs are present. vsSdkTargetsPath is null when the extension development workload is missing.")]
    public static async Task<string> BuildEnv(CancellationToken ct = default)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return Json(new { ok = false, error = Strings.Format("VsWhereNotFound", vswhere) });
        }

        try
        {
            var installs = await new BuildEnvironment(new ProcessCommandRunner(), vswhere).ListAsync(ct);
            return Json(new
            {
                ok = true,
                count = installs.Count,
                installs = installs.Select(i => new
                {
                    path = i.Path,
                    displayName = i.DisplayName,
                    version = i.Version,
                    isPrerelease = i.IsPrerelease,
                    msBuildPath = i.MsBuildPath,
                    vsSdkTargetsPath = i.VsSdkTargetsPath,
                }),
            });
        }
        catch (InvalidOperationException ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static bool TryParseIde(string ide, out ExtensionIde parsed, out string? error)
    {
        if (Enum.TryParse(ide, ignoreCase: true, out parsed) && Enum.IsDefined(parsed))
        {
            error = null;
            return true;
        }

        error = Strings.Format("UnknownIde", ide);
        return false;
    }

    private static object Project(ExtensionInfo info) => new
    {
        ide = info.Ide.ToString(),
        id = info.Id,
        version = info.Version,
        displayName = info.DisplayName,
        publisher = info.Publisher,
        description = info.Description,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
