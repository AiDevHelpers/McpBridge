using System.ComponentModel;
using System.Text.Json;
using AiDevHelpers.Bridge;
using AiDevHelpers.Common;
using AiDevHelpers.Helpers.FileEditHelper;
using AiDevHelpers.Helpers.ModelServerHelper;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AiDevHelpers.MCP.Core.Tools;

/// <summary>
/// code_work: a plan the calling AI passes in. start saves it and, with confirm, creates each file from its Visual Studio item template
/// through the IDE bridge. continue drafts a step's file content on the local model; confirm writes that draft after the calling AI has checked it.
/// </summary>
[McpServerToolType]
internal sealed class CodeWorkTools(IOptions<CodeWorkOptions> options, IOptions<ModelServerOptions> models)
{
    private const int DraftTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private static readonly FileEditService Edits = new(new FileEditEngine());

    private static readonly Lazy<ModelServerGenerator> SharedGenerator = new(() => ModelServerGenerator.CreateDefault());

    /// <summary>Test seam: replaces the IDE bridge call (action, arguments, token) → reply JSON.</summary>
    internal static Func<string, object, CancellationToken, Task<string>>? IdeCall { get; set; }

    /// <summary>Test seam: replaces the shared generator.</summary>
    internal static Func<ModelServerGenerator>? GeneratorFactory { get; set; }

    private static ModelServerGenerator Generator() => GeneratorFactory?.Invoke() ?? SharedGenerator.Value;

    private static Task<string> Bridge(string action, object args, CancellationToken ct) =>
        IdeCall is not null
            ? IdeCall(action, args, ct)
            : IdeInstanceRouter.CallAsync(action, args, null, ct);

    [McpServerTool]
    [DangerousTool]
    [Description("code_work start: save a plan (a JSON array of steps, each with id, project, file and template) under the repo's .aidevhelpers/code-work. Without confirm it only saves the plan. With confirm true it creates each file from its Visual Studio item template through the IDE bridge (needs an open Visual Studio instance). Off unless the CodeWork:Enabled setting is on.")]
    public async Task<string> CodeWorkStart(
        [Description("Repo folder: files are checked against it")] string root,
        [Description("The plan: a JSON array of {id, project, file, template}. file is inside the repo; template is an item template name or path.")] string plan,
        [Description("What the work is for")] string prompt,
        [Description("Optional: session id (letters, digits, '-' and '_', up to 64). A new one is made when omitted.")] string? sessionId = null,
        [Description("Set true to create the files now. Default false only saves the plan.")] bool confirm = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!options.Value.Enabled)
                return Refuse(Strings.Get("CodeWorkDisabled"));
            if (string.IsNullOrWhiteSpace(root))
                return Refuse(Strings.Get("CodeWorkRootRequired"));
            if (string.IsNullOrWhiteSpace(prompt))
                return Refuse(Strings.Get("CodeWorkPromptRequired"));

            var id = sessionId ?? Guid.NewGuid().ToString("N")[..12];
            if (!CodeWorkStore.ValidId(id))
                return Refuse(Strings.Get("CodeWorkBadSessionId"));

            List<CodeWorkStep> steps;
            try
            {
                steps = JsonSerializer.Deserialize<List<CodeWorkStep>>(plan, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return Refuse(Strings.Get("CodeWorkPlanInvalid"));
            }

            var error = CodeWorkStore.ValidatePlan(steps, root);
            if (error is not null)
                return Refuse(error);

            var session = new CodeWorkSession { Id = id, Prompt = prompt, Steps = steps };
            CodeWorkStore.Save(root, session);
            if (!confirm)
                return Json(new { ok = true, sessionId = id, created = Array.Empty<string>(), note = Strings.Get("CodeWorkPlanSaved") });

            var created = new List<object>();
            foreach (var step in steps)
            {
                var reply = await Bridge("addProjectItem", new { project = step.Project, file = step.File, mode = "fromNew", template = step.Template, confirm = true }, ct);
                using var doc = JsonDocument.Parse(reply);
                var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                created.Add(new { stepId = step.Id, ok, reply = JsonSerializer.Deserialize<JsonElement>(reply, JsonOptions) });
                if (!ok)
                    return Json(new { ok = false, sessionId = id, created, error = Strings.Format("CodeWorkStepFailed", step.Id) });
                var methodError = await AddMethodsAsync(root, step, ct);
                if (methodError is not null)
                    return Json(new { ok = false, sessionId = id, created, error = methodError });
                session.Status[step.Id] = "created";
            }

            CodeWorkStore.Save(root, session);
            return Json(new { ok = true, sessionId = id, created });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(ex.Message);
        }
    }

    [McpServerTool]
    [SafeTool]
    [Description("code_work continue: draft the file content for one plan step on the local model (the CodeWork:DraftModelType type, a router type). Writes nothing. Returns the content and a token; the calling AI checks the content, then calls code_work confirm with the token.")]
    public async Task<string> CodeWorkContinue(
        [Description("Repo folder")] string root,
        [Description("Session id from start")] string sessionId,
        [Description("Step id from the plan")] string stepId,
        CancellationToken ct = default)
    {
        try
        {
            if (!options.Value.Enabled)
                return Refuse(Strings.Get("CodeWorkDisabled"));

            var session = CodeWorkStore.Load(root, sessionId);
            if (session is null)
                return Refuse(Strings.Get("CodeWorkSessionMissing"));
            var step = session.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is null)
                return Refuse(Strings.Format("CodeWorkStepUnknown", stepId));

            var typeName = step.Difficult ? options.Value.DifficultModelType : options.Value.DraftModelType;
            var route = ModelRouter.Resolve(typeName, models.Value.ToRoutes());
            if (!route.Ok)
                return Refuse(route.Error!);

            var prompt = Strings.Format("CodeWorkDraftPrompt", session.Prompt, step.File, step.Template);
            if (route.Target == ModelRouter.ChatTarget)
            {
                var handoffDraft = new CodeWorkDraft(Guid.NewGuid().ToString("N"), null);
                session.Drafts[stepId] = handoffDraft;
                CodeWorkStore.Save(root, session);
                return Json(new { ok = true, handoff = true, sessionId, stepId, modelType = typeName, model = route.Model, prompt = Strings.Format("HandoffPrompt", route.Model, prompt), token = handoffDraft.Token });
            }

            var reply = await Generator().GenerateAsync(route.Model!, prompt, null, DraftTimeoutSeconds, ct);
            if (!reply.Ok || string.IsNullOrWhiteSpace(reply.Text))
                return Refuse(reply.Error ?? Strings.Get("CodeWorkDraftEmpty"));

            var draft = new CodeWorkDraft(Guid.NewGuid().ToString("N"), reply.Text);
            session.Drafts[stepId] = draft;
            CodeWorkStore.Save(root, session);
            return Json(new { ok = true, sessionId, stepId, modelType = typeName, model = route.Model, content = draft.Content, token = draft.Token, capped = reply.Capped });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(ex.Message);
        }
    }

    [McpServerTool]
    [DangerousTool]
    [Description("code_work confirm: write the drafted content of one step to its file through file_edit (backed up first). Needs the token that continue returned; a file that is empty or missing is refused.")]
    public async Task<string> CodeWorkConfirm(
        [Description("Repo folder")] string root,
        [Description("Session id from start")] string sessionId,
        [Description("Step id from the plan")] string stepId,
        [Description("Token from continue for this step")] string token,
        [Description("File content, required when continue handed the step to chat (the chat writes it)")] string? content = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!options.Value.Enabled)
                return Refuse(Strings.Get("CodeWorkDisabled"));

            var session = CodeWorkStore.Load(root, sessionId);
            if (session is null)
                return Refuse(Strings.Get("CodeWorkSessionMissing"));
            var step = session.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is null)
                return Refuse(Strings.Format("CodeWorkStepUnknown", stepId));
            if (!session.Drafts.TryGetValue(stepId, out var draft) || draft.Token != token)
                return Refuse(Strings.Get("CodeWorkNoMatchingDraft"));

            var target = WorkspacePathGuard.Resolve(step.File, root);
            if (!target.Ok)
                return Refuse(target.Error!);
            if (!File.Exists(target.FullPath))
                return Refuse(Strings.Format("CodeWorkFileMissing", step.File));

            var newContent = draft.Content ?? content;
            if (string.IsNullOrEmpty(newContent))
                return Refuse(Strings.Get("CodeWorkNeedsContent"));

            var current = await File.ReadAllTextAsync(target.FullPath, ct);
            if (current.Length == 0)
                return Refuse(Strings.Format("CodeWorkFileEmpty", step.File));

            var result = await Edits.ExecuteAsync(new FileEditRequest("apply", FilePath: step.File, OldText: current, NewText: newContent), root, ct);
            if (!result.Ok)
                return Refuse(result.Error ?? Strings.Get("CodeWorkWriteFailed"));

            session.Drafts.Remove(stepId);
            session.Status[stepId] = "written";
            CodeWorkStore.Save(root, session);
            return Json(new { ok = true, sessionId, stepId, filePath = result.FilePath, linesChanged = result.LinesChanged, backupPath = result.BackupPath });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(ex.Message);
        }
    }

    [McpServerTool]
    [DangerousTool]
    [Description("code_work execute: run the next action for one plan step. A pending step is created from its template (through the IDE bridge). A created step is drafted (as continue does). A drafted step waits for code_work confirm with its token. A written step is done. Changes one step per call.")]
    public async Task<string> CodeWorkExecute(
        [Description("Repo folder")] string root,
        [Description("Session id from start")] string sessionId,
        [Description("Step id from the plan")] string stepId,
        CancellationToken ct = default)
    {
        try
        {
            if (!options.Value.Enabled)
                return Refuse(Strings.Get("CodeWorkDisabled"));

            var session = CodeWorkStore.Load(root, sessionId);
            if (session is null)
                return Refuse(Strings.Get("CodeWorkSessionMissing"));
            var step = session.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step is null)
                return Refuse(Strings.Format("CodeWorkStepUnknown", stepId));

            var status = session.Status.GetValueOrDefault(stepId, "pending");
            switch (status)
            {
                case "pending":
                    {
                        var reply = await Bridge("addProjectItem", new { project = step.Project, file = step.File, mode = "fromNew", template = step.Template, confirm = true }, ct);
                        using var made = JsonDocument.Parse(reply);
                        if (!(made.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True))
                            return Refuse(Strings.Format("CodeWorkStepFailed", stepId));

                        var methodError = await AddMethodsAsync(root, step, ct);
                        if (methodError is not null)
                            return Refuse(methodError);

                        session.Status[stepId] = "created";
                        CodeWorkStore.Save(root, session);
                        return Json(new { ok = true, sessionId, stepId, status = "created" });
                    }

                case "created":
                    {
                        // Drafting saves the session itself, so read the status back afterwards.
                        var drafted = await CodeWorkContinue(root, sessionId, stepId, ct);
                        using var doc = JsonDocument.Parse(drafted);
                        if (!(doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True))
                            return drafted;

                        var saved = CodeWorkStore.Load(root, sessionId)!;
                        saved.Status[stepId] = "drafted";
                        CodeWorkStore.Save(root, saved);
                        return Json(new { ok = true, sessionId, stepId, status = "drafted", token = doc.RootElement.GetProperty("token").GetString() });
                    }

                case "drafted":
                    return Refuse(Strings.Get("CodeWorkExecuteConfirmFirst"));

                default:
                    return Json(new { ok = true, sessionId, stepId, status = "written" });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse(ex.Message);
        }
    }

    // Adds the step's methods to its class through the bridge's addMethod, at the line that declares the class.
    // Returns the error text, or null when every method was added.
    private async Task<string?> AddMethodsAsync(string root, CodeWorkStep step, CancellationToken ct)
    {
        if (step.Methods is null || step.Methods.Count == 0)
            return null;

        var target = WorkspacePathGuard.Resolve(step.File, root);
        if (!target.Ok)
            return target.Error;

        var line = CodeWorkStore.TypeLine(target.FullPath);
        if (line is null)
            return Strings.Format("CodeWorkNoClass", step.File);

        foreach (var method in step.Methods)
        {
            var reply = await Bridge("addMethod", new { file = target.FullPath, line, newMethodText = method.Text, newSummary = method.Summary, docs = Array.Empty<object>(), members = Array.Empty<object>(), confirm = true, runTests = false }, ct);
            using var doc = JsonDocument.Parse(reply);
            if (!(doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True))
                return Strings.Format("CodeWorkMethodFailed", step.Id);
        }

        return null;
    }

    private static string Refuse(string error) => Json(new { ok = false, error });

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
