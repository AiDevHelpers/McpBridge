using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Code elements of one file from Roslyn, through the Visual Studio workspace. Replaces the EnvDTE code model, which the
/// IDE's C# implementation refuses (E_FAIL). Must run on the UI thread; the async Roslyn calls run through the join factory.
/// </summary>
internal static class CodeModelActions
{
    public static object ListCodeElements(string file)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => ListCodeElementsAsync(file));
    }

    private static async Task<object> ListCodeElementsAsync(string file)
    {
        var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
        var workspace = model.GetService<VisualStudioWorkspace>();
        var solution = workspace.CurrentSolution;
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault()
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));
        var document = solution.GetDocument(documentId)
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));

        var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException(Strings.Format("NoSyntaxTreeFor", file));
        var root = await tree.GetRootAsync().ConfigureAwait(true);

        var list = new List<object>();
        foreach (var node in root.DescendantNodes())
        {
            var name = NameOf(node);
            if (name is null)
            {
                continue;
            }

            var span = tree.GetLineSpan(node.Span);
            list.Add(new
            {
                kind = node.Kind().ToString(),
                name,
                start_line = span.StartLinePosition.Line + 1,
                end_line = span.EndLinePosition.Line + 1,
            });
        }

        return new { elements = list };
    }

    // Symbol at a position: every reference to it across the solution. Read-only.
    public static object SemanticReferences(string file, int line, int column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => SemanticReferencesAsync(file, line, column));
    }

    // Symbol at a position: where it is declared in source. Read-only.
    public static object SemanticDefinition(string file, int line, int column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => SemanticDefinitionAsync(file, line, column));
    }

    private static async Task<object> SemanticReferencesAsync(string file, int line, int column)
    {
        var (solution, document, position) = await ResolvePositionAsync(file, line, column).ConfigureAwait(true);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(true)
            ?? throw new ArgumentException(Strings.Get("NoSymbolAtPosition"));

        // One referenced symbol comes back per project, so the same location can repeat. Report each location once.
        var found = new List<object>();
        var seen = new HashSet<(string File, int Line, int Column)>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(true))
        {
            foreach (var reference in referenced.Locations)
            {
                var location = (File: reference.Location.GetLineSpan().Path,
                    Line: reference.Location.GetLineSpan().StartLinePosition.Line,
                    Column: reference.Location.GetLineSpan().StartLinePosition.Character);
                if (seen.Add(location))
                {
                    found.Add(ToLocation(reference.Location));
                }
            }
        }

        return new { symbol = symbol.ToDisplayString(), locations = found };
    }

    private static async Task<object> SemanticDefinitionAsync(string file, int line, int column)
    {
        var (_, document, position) = await ResolvePositionAsync(file, line, column).ConfigureAwait(true);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(true)
            ?? throw new ArgumentException(Strings.Get("NoSymbolAtPosition"));

        var locations = new List<object>();
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                locations.Add(ToLocation(location));
            }
        }

        return new { symbol = symbol.ToDisplayString(), locations };
    }

    // Renames the symbol at a position across the solution. Plan-only lists the files that would change. Applied writes them
    // through the VS workspace, so the change is one undoable edit set in the IDE.
    public static object RenameSymbol(string file, int line, int column, string newName, bool confirm)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(() => RenameSymbolAsync(file, line, column, newName, confirm));
    }

    private static async Task<object> RenameSymbolAsync(string file, int line, int column, string newName, bool confirm)
    {
        var (solution, document, position) = await ResolvePositionAsync(file, line, column).ConfigureAwait(true);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position).ConfigureAwait(true)
            ?? throw new ArgumentException(Strings.Get("NoSymbolAtPosition"));

        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName).ConfigureAwait(true);
        var files = new List<string>();
        foreach (var projectChanges in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                var path = renamed.GetDocument(documentId)?.FilePath;
                if (path is not null)
                {
                    files.Add(path);
                }
            }
        }

        var change = new { symbol = symbol.ToDisplayString(), new_name = newName, files };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
        var workspace = model.GetService<VisualStudioWorkspace>();
        if (!workspace.TryApplyChanges(renamed))
        {
            throw new InvalidOperationException(Strings.Get("RenameNotApplied"));
        }

        return new { planned = false, applied = change };
    }

    // Replaces the method at a position with new text, after the checks in MethodEditEngine. Plan-only returns the checks; a
    // refused plan is a reply with refused true, never an applied change. Applied writes through the VS workspace as one change set.
    public static object EditMethod(
        string file, int line, int column, string newMethodText, IReadOnlyList<CallerEdit> callerEdits, DocUpdates docs, IReadOnlyList<MemberSpec> members,
        bool rewriteCallers, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(
            solution => WithMembersAsync(solution, file, line, members, (s, shift, baseline) => rewriteCallers
                ? ChangeSignatureEngine.PlanEditAsync(s, file, shift(line), column, newMethodText, callerEdits, docs, baseline)
                : MethodEditEngine.PlanAsync(s, file, shift(line), column, newMethodText, callerEdits, docs, baseline)),
            confirm, planHash);
    }

    // Changes a method's parameters and rewrites its call sites (ChangeSignatureEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object ChangeSignature(
        string file, int line, int column, IReadOnlyList<NewParameter> newParameters, DocUpdates docs, HeaderChange header, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ChangeSignatureEngine.PlanAsync(solution, file, line, column, newParameters, docs, header), confirm, planHash);
    }

    // Adds a parameter to a method and threads it up its callers to a depth (ThreadParameterEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object ThreadParameter(
        string file, int line, int column, string declaration, string valueAtTop, int depth, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ThreadParameterEngine.PlanAsync(solution, file, line, column, declaration, valueAtTop, depth), confirm, planHash);
    }

    // Adds a member to the enum at a line, or changes the value of the enum member at a line (EnumMemberEngine). Same plan, refuse and
    // apply behaviour as EditMethod.
    public static object AddEnumMember(string file, int line, string name, string? value, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => EnumMemberEngine.AddAsync(solution, file, line, name, value), confirm, planHash);
    }

    public static object EditEnumMember(string file, int line, string value, bool allowValueChange, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => EnumMemberEngine.EditValueAsync(solution, file, line, value, allowValueChange), confirm, planHash);
    }

    // Changes a class, struct or record's modifiers and base list (EditTypeEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object EditType(
        string file, int line, string? modifiers, IReadOnlyList<string>? baseList, IReadOnlyList<string> attributesAdd, IReadOnlyList<string> attributesRemove,
        string? typeParameters, string? constraints, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => EditTypeEngine.PlanAsync(solution, file, line, modifiers, baseList, attributesAdd, attributesRemove, typeParameters, constraints), confirm, planHash);
    }

    // Moves the named members of a partial type's part into a new part in a new file (PartialEngine.ExtractAsync).
    public static object ExtractPartial(string file, int line, IReadOnlyList<string> members, string suffix, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => PartialEngine.ExtractAsync(solution, file, line, members, suffix), confirm, planHash);
    }

    // Deletes the confirmed unused members and types in one change (RemoveUnusedEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object RemoveUnused(IReadOnlyList<(string File, int Line, string Kind)> items, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => RemoveUnusedEngine.PlanAsync(solution, items), confirm, planHash);
    }

    // Edits the doc comment of the method at a line, with no code change (UpdateDocsEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object UpdateDocs(string file, int line, string? summary, IReadOnlyList<DocItem> items, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => UpdateDocsEngine.PlanAsync(solution, file, line, summary, items), confirm, planHash);
    }

    // Read only: reports doc comments that disagree with their method (DocCheckEngine). Runs on the UI thread against the current solution.
    public static object CheckDocs(string file)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
            var workspace = model.GetService<VisualStudioWorkspace>();
            return (object)await DocCheckEngine.CheckAsync(workspace.CurrentSolution, file).ConfigureAwait(true);
        });
    }

    // Converts one member's shape: field to property, or null guards (ConvertEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object Convert(string file, int line, string kind, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ConvertEngine.PlanAsync(solution, file, line, kind), confirm, planHash);
    }

    // Extracts an interface from a class or struct, in a new file beside it (ExtractInterfaceEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object ExtractInterface(string file, int line, string interfaceName, IReadOnlyList<string> members, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ExtractInterfaceEngine.PlanAsync(solution, file, line, interfaceName, members), confirm, planHash);
    }

    // Extracts an expression into a local or a constant declared before its statement (ExtractValueEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object ExtractValue(string file, int line, string expression, string name, string kind, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ExtractValueEngine.PlanAsync(solution, file, line, expression, name, kind), confirm, planHash);
    }

    // Replaces every call of a one-expression method with its body and deletes the method (InlineMethodEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object InlineMethod(string file, int line, int column, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => InlineMethodEngine.PlanAsync(solution, file, line, column), confirm, planHash);
    }

    // Extracts statements into a new method (ExtractMethodEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object ExtractMethod(string file, int startLine, int endLine, string name, string accessibility, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ExtractMethodEngine.PlanAsync(solution, file, startLine, endLine, name, accessibility), confirm, planHash);
    }

    // Applies a code action chosen from ide_list_code_actions (CodeActionEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object ApplyCodeAction(string file, int line, string id, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => CodeActionEngine.ApplyAsync(solution, file, line, id), confirm, planHash);
    }

    // Merges the parts of a partial type into the part at the line (PartialEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object MergePartial(string file, int line, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => PartialEngine.MergeAsync(solution, file, line), confirm, planHash);
    }

    // Splits a top-level type into its own file, or renames a one-type file to its type (OrganizeFileEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object OrganizeFile(string file, int line, string action, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => OrganizeFileEngine.PlanAsync(solution, file, line, action), confirm, planHash);
    }

    // Moves a method, property or field to another type in the solution (MoveMemberEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object MoveMember(
        string file, int line, int column, string toType, IReadOnlyList<CallerEdit> callerEdits, bool toStatic, bool pullUp, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => MoveMemberEngine.PlanAsync(solution, file, line, column, toType, callerEdits, toStatic, pullUp), confirm, planHash);
    }

    // Adds Equals, GetHashCode or ToString over the chosen fields and properties (GenerateMembersEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object GenerateMembers(string file, int line, string kind, IReadOnlyList<string> members, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => GenerateMembersEngine.PlanAsync(solution, file, line, kind, members), confirm, planHash);
    }

    // Adds a constructor that assigns the chosen fields and properties (GenerateConstructorEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object GenerateConstructor(string file, int line, IReadOnlyList<string> members, string accessibility, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => GenerateConstructorEngine.PlanAsync(solution, file, line, members, accessibility), confirm, planHash);
    }

    // Adds stubs to a class, struct or record: its interface members, or overrides of named members (ImplementMembersEngine).
    public static object ImplementMembers(
        string file, int line, string mode, IReadOnlyList<string> names, bool explicitImplementation, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => ImplementMembersEngine.PlanAsync(solution, file, line, mode, names, explicitImplementation), confirm, planHash);
    }

    // Adds a new type in its own file beside the anchor file (AddTypeEngine). Same plan, refuse and apply behaviour as EditMethod.
    public static object AddType(
        string anchorFile, string declaration, IReadOnlyList<string> usings, string? namespaceName, string? summary, IReadOnlyList<string> attributes,
        bool withTests, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => AddTypeEngine.AddAsync(solution, anchorFile, declaration, usings, namespaceName, summary, attributes, withTests), confirm, planHash);
    }

    // Adds a method, property, event or indexer to the interface at a line (InterfaceMemberEngine). Same plan, refuse and apply
    // behaviour as EditMethod.
    public static object AddInterfaceMember(string file, int line, string declaration, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => InterfaceMemberEngine.AddAsync(solution, file, line, declaration), confirm, planHash);
    }

    // Adds the fields and properties a method needs to its type first, then plans the method on top, shifting its line numbers past
    // the inserted lines. The method plan checks diagnostics against the original solution, so members' own warnings are reported too.
    internal static async Task<MethodEditPlan> WithMembersAsync(
        Solution solution, string file, int line, IReadOnlyList<MemberSpec> members,
        Func<Solution, Func<int, int>, Solution, Task<MethodEditPlan>> plan)
    {
        if (members.Count == 0)
        {
            return await plan(solution, l => l, solution).ConfigureAwait(false);
        }

        var added = await MemberAddEngine.PlanAsync(solution, file, line, members, finalize: false).ConfigureAwait(false);
        if (added.Refused)
        {
            return added;
        }

        var result = await plan(added.Result!, l => l > added.InsertedAfterLine ? l + added.InsertedLines : l, solution).ConfigureAwait(false);
        result.MembersAdded.AddRange(members.Select(m => m.Name));
        result.Warnings.AddRange(added.Warnings);
        foreach (var f in added.Files.Where(f => !result.Files.Contains(f)))
        {
            result.Files.Add(f);
        }

        return result;
    }

    // Adds fields and properties to the type at a line, or changes an existing one, with the checks of the method tools.
    public static object AddMember(string file, int line, IReadOnlyList<MemberSpec> members, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => MemberAddEngine.PlanAsync(solution, file, line, members, finalize: true), confirm, planHash);
    }

    public static object EditMember(string file, int line, MemberEdit edit, IReadOnlyList<CallerEdit> callerEdits, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => MemberEditEngine.PlanAsync(solution, file, line, edit, callerEdits), confirm, planHash);
    }

    // Adds a method to the type at a line, or an overload of the method at that line, after the checks in MethodAddEngine. Same
    // plan, refuse and apply behaviour as EditMethod.
    public static object AddMethod(
        string file, int line, string newMethodText, int? afterLine, DocUpdates docs, bool overload, IReadOnlyList<MemberSpec> members, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(
            solution => WithMembersAsync(solution, file, line, members, (s, shift, baseline) =>
                MethodAddEngine.PlanAsync(s, file, shift(line), newMethodText, afterLine is { } a ? shift(a) : (int?)null, overload, docs, baseline)),
            confirm, planHash);
    }

    // The TDD steps: add a test with its method, or replace a test's body. Same plan, refuse and apply behaviour as EditMethod.
    public static object TddAddTest(
        string testFile, int testLine, string testMethodText, string methodFile, int methodLine, string methodText, DocUpdates docs,
        IReadOnlyList<MemberSpec> members, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(
            solution => WithMembersAsync(solution, methodFile, methodLine, members, (s, shift, baseline) =>
                TddEngine.StartAsync(s, testFile, testLine, testMethodText, methodFile, shift(methodLine), methodText, docs, baseline)),
            confirm, planHash);
    }

    public static object TddUpdateTest(string file, int line, string newBody, string? targetFile, int? targetLine, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => TddEngine.UpdateTestAsync(solution, file, line, newBody, targetFile, targetLine), confirm, planHash);
    }

    private static object RunPlan(Func<Solution, Task<MethodEditPlan>> plan, bool confirm, string? expectedHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
            var workspace = model.GetService<VisualStudioWorkspace>();
            // The snapshot is taken on the UI thread; planning runs on a thread-pool thread with a time limit.
            var outcome = await PlanRunner.RunAsync(workspace.CurrentSolution, plan, PlanRunner.DefaultTimeout).ConfigureAwait(true);
            var planned = outcome.Plan ?? PlanRunner.Incomplete(PlanRunner.DefaultTimeout);
            foreach (var path in planned.Files)
            {
                if (LogicalDocuments.RefusalFor(workspace.CurrentSolution, path) is { } refusal)
                {
                    planned.Reasons.Add(refusal);
                }
            }

            foreach (var path in planned.Divergent)
            {
                planned.Warnings.Add(Strings.Format("LogicalDivergent", path));
            }

            if (planned.Result is { } changed)
            {
                var current = workspace.CurrentSolution;
                var count = 0;
                foreach (var documentId in changed.GetChanges(current).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
                {
                    if (count++ >= UnifiedDiff.MaxFiles)
                    {
                        planned.Diffs.Add(Strings.Format("DiffFilesTruncated", UnifiedDiff.MaxFiles));
                        break;
                    }

                    var before = await current.GetDocument(documentId)!.GetTextAsync().ConfigureAwait(true);
                    var after = await changed.GetDocument(documentId)!.GetTextAsync().ConfigureAwait(true);
                    var path = changed.GetDocument(documentId)!.FilePath ?? documentId.ToString();
                    if (UnifiedDiff.Of(path, before.ToString(), after.ToString()) is { } diff)
                    {
                        planned.Diffs.Add(diff);
                    }
                }
            }

            planned.Hash = await PlanHash.ComputeAsync(workspace.CurrentSolution, planned.Result).ConfigureAwait(true);
            if (confirm && expectedHash is not null && !PlanHash.Matches(expectedHash, planned.Hash))
            {
                planned.Reasons.Add(Strings.Get("PlanHashChanged"));
            }

            if (planned.Refused || !confirm)
            {
                return (object)new { planned = !planned.Refused, applied = false, check = planned.ToReply() };
            }

            if (!workspace.TryApplyChanges(planned.Result!))
            {
                throw new InvalidOperationException(Strings.Get("MethodEditNotApplied"));
            }

            // Files move on disk only after the text changes are in, and only when the plan asked for it.
            var moved = planned.FileMoves.Count > 0 ? FileMover.Apply(planned.FileMoves) : new List<object>();
            var deleted = planned.FileDeletes.Select(p => FileOperations.Delete(p, planned.DeleteProject)).ToList();
            return (object)new { planned = false, applied = true, check = planned.ToReply(), moved, deleted };
        });
    }

    // Renames a namespace across the solution, or moves one type to another namespace.
    public static object RenameNamespace(string oldName, string newName, bool moveFiles, IReadOnlyList<CallerEdit> callerEdits, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => NamespaceEngine.RenameAsync(solution, oldName, newName, moveFiles, callerEdits), confirm, planHash);
    }

    public static object MoveType(string file, int line, string newNamespace, bool moveFiles, IReadOnlyList<CallerEdit> callerEdits, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => TypeMoveEngine.MoveAsync(solution, file, line, newNamespace, moveFiles, callerEdits), confirm, planHash);
    }

    // Deletes a member after the checks in DeleteEngine. Plan-only returns the checks; applied writes through the workspace.
    public static object DeleteMember(string file, int line, int column, IReadOnlyList<CallerEdit> callerEdits, IReadOnlyList<string> deleteTests, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => DeleteEngine.DeleteMemberAsync(solution, file, line, column, callerEdits, deleteTests, _ => { }), confirm, planHash);
    }

    // Deletes a type after the checks in DeleteEngine.DeleteTypeAsync. The file goes too when it held only this type and deleteFile is set.
    public static object DeleteType(string file, int line, int column, IReadOnlyList<CallerEdit> callerEdits, IReadOnlyList<string> deleteTests, bool deleteFile, bool confirm, string? planHash = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return RunPlan(solution => DeleteEngine.DeleteTypeAsync(solution, file, line, column, callerEdits, deleteTests, deleteFile), confirm, planHash);
    }

    // Lists private and internal members and types with no references (read only). Public ones only when includePublic is set.
    public static object ListUnused(string? project, bool includePublic)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
                ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
            var workspace = model.GetService<VisualStudioWorkspace>();
            var unused = await UnusedFinder.FindAsync(workspace.CurrentSolution, project, includePublic).ConfigureAwait(true);
            return (object)new { count = unused.Count, unused };
        });
    }

    internal static async Task<(Solution Solution, Document Document, int Position)> ResolvePositionAsync(string file, int line, int column)
    {
        var model = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException(Strings.Get("ComponentModelUnavailable"));
        var solution = model.GetService<VisualStudioWorkspace>().CurrentSolution;
        var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault()
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));
        var document = solution.GetDocument(documentId)
            ?? throw new ArgumentException(Strings.Format("NoRoslynDocumentFor", file));

        var text = await document.GetTextAsync().ConfigureAwait(true);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new ArgumentException(Strings.Format("LineOutOfRange", line, file));
        }

        var position = text.Lines[line - 1].Start + Math.Max(0, column - 1);
        return (solution, document, Math.Min(position, text.Length));
    }

    private static object ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new
        {
            file = span.Path,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
        };
    }

    // Names only the declarations an agent navigates by: namespaces, types, methods, constructors, properties.
    private static string? NameOf(SyntaxNode node) => node switch
    {
        NamespaceDeclarationSyntax ns => ns.Name.ToString(),
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        _ => null,
    };
}
