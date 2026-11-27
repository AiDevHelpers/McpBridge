using System.Text.Json;
using System.Text.Json.Nodes;
using AiDevHelpers.Installers.Vs2026.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AiDevHelpers.Helpers.RoslynHelper;

/// <summary>
/// Group A edit actions: editMethod, editMember and addMember. Each one plans on the helper's current solution through
/// <see cref="PlanFlow"/>, and applies only when the caller confirms with the plan hash it was shown.
/// </summary>
public static class PlanActions
{
    public static void Register(ActionDispatcher dispatcher, Func<Solution?> current, Action<Solution> setCurrent)
    {
        dispatcher.Register("editMethod", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            var column = ArgumentParsers.OptionalInt(a, "column") ?? 1;
            var newText = ArgumentParsers.RequireString(a, "newMethodText");
            var callerEdits = ArgumentParsers.ParseCallerEdits(a);
            var docs = ArgumentParsers.ParseDocUpdates(a);
            var members = ArgumentParsers.ParseMemberSpecs(a);
            var rewriteCallers = ArgumentParsers.OptionalBool(a, "rewriteCallers") == true;
            return MemberPlanning.WithMembersAsync(solution, file, line, members, (s, shift, baseline) => rewriteCallers
                ? ChangeSignatureEngine.PlanEditAsync(s, file, shift(line), column, newText, callerEdits, docs, baseline)
                : MethodEditEngine.PlanAsync(s, file, shift(line), column, newText, callerEdits, docs, baseline));
        }));

        dispatcher.Register("editMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            return MemberEditEngine.PlanAsync(solution, file, line, ArgumentParsers.ParseMemberEdit(a), ArgumentParsers.ParseCallerEdits(a));
        }));

        dispatcher.Register("addMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            return MemberAddEngine.PlanAsync(solution, file, line, ArgumentParsers.ParseMemberSpecs(a), finalize: true);
        }));

        dispatcher.Register("addMethod", args => RunAsync(args, current, setCurrent, (solution, a) => AddMethodPlan(solution, a, overload: false)));

        dispatcher.Register("addOverload", args => RunAsync(args, current, setCurrent, (solution, a) => AddMethodPlan(solution, a, overload: true)));

        dispatcher.Register("deleteMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            var column = ArgumentParsers.OptionalInt(a, "column") ?? 1;
            return DeleteEngine.DeleteMemberAsync(solution, file, line, column, ArgumentParsers.ParseCallerEdits(a),
                ArgumentParsers.StringList(a, "deleteTests").ToList(), _ => { });
        }));

        dispatcher.Register("changeSignature", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            var column = ArgumentParsers.OptionalInt(a, "column") ?? 1;
            var header = new HeaderChange
            {
                ReturnType = ArgumentParsers.OptionalString(a, "returnType"),
                Accessibility = ArgumentParsers.OptionalString(a, "accessibility"),
            };
            return ChangeSignatureEngine.PlanAsync(solution, file, line, column, ArgumentParsers.ParseNewParameters(a),
                ArgumentParsers.ParseDocUpdates(a), header);
        }));

        dispatcher.Register("editType", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var file = ArgumentParsers.RequireString(a, "file");
            var line = ArgumentParsers.RequireInt(a, "line");
            var baseList = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("baseList", out _)
                ? ArgumentParsers.StringList(a, "baseList").ToList()
                : null;
            return EditTypeEngine.PlanAsync(solution, file, line, ArgumentParsers.OptionalString(a, "modifiers"), baseList,
                ArgumentParsers.StringList(a, "attributesAdd").ToList(), ArgumentParsers.StringList(a, "attributesRemove").ToList(),
                ArgumentParsers.OptionalString(a, "typeParameters"), ArgumentParsers.OptionalString(a, "constraints"));
        }));

        dispatcher.Register("addType", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return AddTypeEngine.AddAsync(solution, ArgumentParsers.RequireString(a, "anchorFile"), ArgumentParsers.RequireString(a, "declaration"),
                ArgumentParsers.StringList(a, "usings").ToList(), ArgumentParsers.OptionalString(a, "namespaceName"),
                ArgumentParsers.OptionalString(a, "summary"), ArgumentParsers.StringList(a, "attributes").ToList(),
                ArgumentParsers.OptionalBool(a, "withTests") == true);
        }));

        dispatcher.Register("addInterfaceMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return InterfaceMemberEngine.AddAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.RequireString(a, "declaration"));
        }));

        dispatcher.Register("implementMembers", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return ImplementMembersEngine.PlanAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.RequireString(a, "mode"), ArgumentParsers.StringList(a, "members").ToList(),
                ArgumentParsers.OptionalBool(a, "explicit") == true);
        }));

        dispatcher.Register("generateConstructor", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return GenerateConstructorEngine.PlanAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.StringList(a, "members").ToList(), ArgumentParsers.OptionalString(a, "accessibility") ?? "public");
        }));

        dispatcher.Register("addEnumMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return EnumMemberEngine.AddAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.RequireString(a, "name"), ArgumentParsers.OptionalString(a, "value"));
        }));

        dispatcher.Register("editEnumMember", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return EnumMemberEngine.EditValueAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.RequireString(a, "value"), ArgumentParsers.OptionalBool(a, "allowValueChange") == true);
        }));

        dispatcher.Register("threadParameter", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return ThreadParameterEngine.PlanAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.OptionalInt(a, "column") ?? 1, ArgumentParsers.RequireString(a, "declaration"),
                ArgumentParsers.RequireString(a, "valueAtTop"), ArgumentParsers.OptionalInt(a, "depth") ?? 1);
        }));

        dispatcher.Register("deleteType", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return DeleteEngine.DeleteTypeAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.OptionalInt(a, "column") ?? 1, ArgumentParsers.ParseCallerEdits(a),
                ArgumentParsers.StringList(a, "deleteTests").ToList(), ArgumentParsers.OptionalBool(a, "deleteFile") == true);
        }));

        dispatcher.Register("moveType", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return TypeMoveEngine.MoveAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.RequireString(a, "newNamespace"), ArgumentParsers.OptionalBool(a, "moveFiles") == true,
                ArgumentParsers.ParseCallerEdits(a));
        }));

        dispatcher.Register("renameNamespace", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return NamespaceEngine.RenameAsync(solution, ArgumentParsers.RequireString(a, "oldNamespace"), ArgumentParsers.RequireString(a, "newNamespace"),
                ArgumentParsers.OptionalBool(a, "moveFiles") == true, ArgumentParsers.ParseCallerEdits(a));
        }));

        dispatcher.Register("tddAddTest", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            var testFile = ArgumentParsers.RequireString(a, "testFile");
            var testLine = ArgumentParsers.RequireInt(a, "testLine");
            var testText = ArgumentParsers.RequireString(a, "testMethodText");
            var methodFile = ArgumentParsers.RequireString(a, "methodFile");
            var methodLine = ArgumentParsers.RequireInt(a, "methodLine");
            var methodText = ArgumentParsers.RequireString(a, "methodText");
            var docs = ArgumentParsers.ParseDocUpdates(a);
            var members = ArgumentParsers.ParseMemberSpecs(a);
            return MemberPlanning.WithMembersAsync(solution, methodFile, methodLine, members, (s, shift, baseline) =>
                TddEngine.StartAsync(s, testFile, testLine, testText, methodFile, shift(methodLine), methodText, docs, baseline));
        }));

        dispatcher.Register("tddUpdateTest", args => RunAsync(args, current, setCurrent, (solution, a) =>
        {
            return TddEngine.UpdateTestAsync(solution, ArgumentParsers.RequireString(a, "file"), ArgumentParsers.RequireInt(a, "line"),
                ArgumentParsers.RequireString(a, "newBody"), ArgumentParsers.OptionalString(a, "targetFile"), ArgumentParsers.OptionalInt(a, "targetLine"));
        }));
    }

    // addMethod and addOverload share one planner: a method added to the type at a line, or an overload of the method at that line.
    private static Task<MethodEditPlan> AddMethodPlan(Solution solution, JsonElement a, bool overload)
    {
        var file = ArgumentParsers.RequireString(a, "file");
        var line = ArgumentParsers.RequireInt(a, "line");
        var newText = ArgumentParsers.RequireString(a, "newMethodText");
        var afterLine = overload ? null : ArgumentParsers.OptionalInt(a, "afterLine");
        var docs = ArgumentParsers.ParseDocUpdates(a);
        var members = ArgumentParsers.ParseMemberSpecs(a);
        return MemberPlanning.WithMembersAsync(solution, file, line, members, (s, shift, baseline) =>
            MethodAddEngine.PlanAsync(s, file, shift(line), newText, afterLine is { } after ? shift(after) : null, overload, docs, baseline));
    }

    private static async Task<object> RunAsync(
        JsonElement args,
        Func<Solution?> current,
        Action<Solution> setCurrent,
        Func<Solution, JsonElement, Task<MethodEditPlan>> plan)
    {
        var disk = current() ?? throw new InvalidOperationException(Strings.Get("NoSolutionLoaded"));
        var confirm = ArgumentParsers.OptionalBool(args, "confirm") == true;
        var planHash = ArgumentParsers.OptionalString(args, "planHash");
        var buffers = ParseBuffers(args);
        var dirty = new HashSet<string>(buffers.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        var snapshot = ApplyBuffers(disk, buffers);
        Solution? applied = null;
        MethodEditPlan? planDone = null;
        IReadOnlyList<UnsavedEdit>? unsavedEdits = null;

        var reply = await PlanFlow.RunAsync(
            snapshot,
            async solution =>
            {
                planDone = await plan(solution, args).ConfigureAwait(false);
                return planDone;
            },
            confirm,
            planHash,
            apply: async planned =>
            {
                // A file with unsaved editor text must stay where it is: the editor owns its contents until it is saved.
                if (planDone?.FileMoves.Any(m => dirty.Contains(m.From)) == true || planDone?.FileDeletes.Any(p => dirty.Contains(p)) == true)
                {
                    throw new InvalidOperationException(Strings.Get("UnsavedMoveRefused"));
                }

                var result = await PlanApplier.ApplyAsync(snapshot, planned, planDone?.FileDeletes, dirty).ConfigureAwait(false);
                if (!result.Applied)
                {
                    throw new InvalidOperationException(result.Refusal ?? Strings.Get("PlanNotApplied"));
                }

                unsavedEdits = result.Unsaved;
                applied = planned;
            },
            // Files move and delete on disk only after the text changes are written, as the VS host does.
            afterApply: planned => (
                planned.FileMoves.Select(m => FileOperations.Move(m.From, m.To, m.Project)).ToList(),
                planned.FileDeletes.Select(p => FileOperations.Delete(p, planned.DeleteProject)).ToList())).ConfigureAwait(false);

        if (applied is not null)
        {
            // The helper's own copy keeps the disk text of the buffered files, since their edits went back to the editor.
            var cache = applied;
            foreach (var buffer in buffers)
            {
                var id = disk.GetDocumentIdsWithFilePath(buffer.Path).First();
                cache = cache.WithDocumentText(id, await disk.GetDocument(id)!.GetTextAsync().ConfigureAwait(false));
            }

            setCurrent(cache);
        }

        if (unsavedEdits is { Count: > 0 })
        {
            var node = JsonSerializer.SerializeToNode(reply)!.AsObject();
            node["unsaved"] = JsonSerializer.SerializeToNode(unsavedEdits, CamelCase);
            return node;
        }

        return reply;
    }

    // The reply keys are camelCase, as the bridge's replies are.
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static List<(string Path, string Text)> ParseBuffers(JsonElement args)
    {
        var buffers = new List<(string Path, string Text)>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("documents", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                buffers.Add((ArgumentParsers.RequireString(e, "path"), e.GetProperty("text").GetString() ?? string.Empty));
            }
        }

        return buffers;
    }

    // Each buffer replaces its file's text in the snapshot, so the plan and its hash read the editor's text.
    private static Solution ApplyBuffers(Solution solution, IReadOnlyList<(string Path, string Text)> buffers)
    {
        foreach (var (path, text) in buffers)
        {
            var id = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault()
                ?? throw new InvalidOperationException(Strings.Format("BufferNotInSolution", path));
            solution = solution.WithDocumentText(id, SourceText.From(text));
        }

        return solution;
    }
}
