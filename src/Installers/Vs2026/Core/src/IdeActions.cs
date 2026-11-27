using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

using EnvDTE;
using EnvDTE80;

using AiDevHelpers.Bridge;
using static AiDevHelpers.Installers.Vs2026.Core.ArgumentParsers;

namespace AiDevHelpers.Installers.Vs2026.Core;

/// <summary>
/// Implements the common IDE actions over the EnvDTE automation object. Must run on the Visual Studio UI thread.
/// Every action returns a result object or throws; <see cref="BridgeServer"/> turns exceptions into an error reply.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010:Invoke main-thread members",
    Justification = "Every entry point runs on the UI thread: BridgePackage switches to it before calling Invoke, and Invoke asserts it with ThreadHelper.ThrowIfNotOnUIThread.")]
public sealed class IdeActions(DTE2 dte)
{
    // Watch expressions for this instance. Kept here (not in the Watch window) and evaluated on read.
    private readonly List<string> _watches = [];

    /// <summary>True (default) answers the IDE's dialogs with its defaults during each action. Set by the MCP server's "configure" action.</summary>
    public bool AutoDismissDialogs { get; set; } = true;

    public object Invoke(string action, JsonElement args)
    {
        // Callers switch to the UI thread first (BridgePackage); this asserts it at runtime.
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

        // Modal prompts (line endings, reload, save) would freeze the UI thread and the bridge with it. Suppressing UI for the
        // duration of one action lets the IDE answer with its defaults instead of waiting. Restored afterwards.
        if (!AutoDismissDialogs)
        {
            return InvokeOnUiThread(action, args);
        }

        var previous = dte.SuppressUI;
        dte.SuppressUI = true;
        try
        {
            return InvokeOnUiThread(action, args);
        }
        finally
        {
            dte.SuppressUI = previous;
        }
    }

    // Makes one file use a single line-ending style before the IDE opens it, so VS does not ask whether to normalize.
    // The dominant style wins; a BOM is kept. Only files the caller is about to write to are passed here.
    private static void NormalizeLineEndings(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        if (crlf == 0 || lf == 0)
        {
            return; // already one style (or no line breaks)
        }

        var normalized = (crlf >= lf ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text.Replace("\r\n", "\n"));
        File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom));
    }

    private object InvokeOnUiThread(string action, JsonElement args) => action switch
    {
        "status" => new { solution_path = NullIfEmpty(dte.Solution?.FullName) },
        "setStartupProject" => SetStartupProject(RequireString(args, "project")),
        "startDebugging" => StartDebugging(),
        "stopDebugging" => StopDebugging(),
        "runTests" => RunTests(),
        "formatDocument" => FormatDocument(),
        "formatSolution" => FormatSolution(),
        "build" => Build(),
        "getBuildDiagnostics" => GetBuildDiagnostics(OptionalString(args, "severity") ?? "all"),
        "codeCleanup" => CodeCleanup(),
        "getOutput" => GetOutput(OptionalString(args, "pane") ?? "Build", OptionalInt(args, "maxLines") ?? 200, OptionalInt(args, "fromOffset"), OptionalInt(args, "maxChars") ?? 20000, OptionalString(args, "anchor")),
        "listOutputPanes" => ListOutputPanes(),
        "searchOutput" => SearchOutput(OptionalString(args, "pane") ?? "Build", RequireString(args, "pattern")),
        "clearOutput" => ClearOutput(OptionalString(args, "pane") ?? "Build"),
        "outputWrite" => OutputChannelActions.Write(RequireString(args, "channel"), RequireString(args, "text")),
        "getLocals" => GetLocals(OptionalInt(args, "maxItems") ?? 100),
        "openSolution" => OpenSolution(RequireString(args, "solutionPath")),
        "testHotReloadState" => TestHotReloadState(),
        "openDocument" => OpenDocument(RequireString(args, "file"), OptionalInt(args, "line")),
        "goToDefinition" => RunAtCaret(RequireString(args, "file"), RequireInt(args, "line"), OptionalInt(args, "column") ?? 1, "Edit.GoToDefinition"),
        "findAllReferences" => RunAtCaret(RequireString(args, "file"), RequireInt(args, "line"), OptionalInt(args, "column") ?? 1, "Edit.FindAllReferences"),
        "bookmarkList" => BookmarkActions.List(dte),
        "bookmarkGet" => BookmarkActions.Get(RequireString(args, "id")),
        "bookmarkSet" => BookmarkActions.Set(dte, RequireString(args, "file"), RequireInt(args, "line")),
        "bookmarkRemove" => BookmarkActions.Remove(RequireString(args, "id")),
        "setBreakpoint" => SetBreakpoint(RequireString(args, "file"), RequireInt(args, "line")),
        "removeBreakpoint" => RemoveBreakpoint(RequireString(args, "file"), RequireInt(args, "line")),
        "listBreakpoints" => ListBreakpoints(),
        "step" => Step(RequireString(args, "mode")),
        "debugState" => DebugState(),
        "evaluate" => Evaluate(RequireString(args, "expression")),
        "addWatch" => AddWatch(RequireString(args, "expression")),
        "removeWatch" => RemoveWatch(RequireString(args, "expression")),
        "listWatches" => ListWatches(),
        "listConfigurations" => ListConfigurations(),
        "setConfiguration" => SetConfiguration(RequireString(args, "configuration")),
        "clean" => Clean(),
        "publish" => Publish(),
        "prepareKill" => PrepareKill(),
        "killProcess" => KillProcess(RequireInt(args, "pid")),
        "listProcesses" => ListProcesses(),
        "listItemTemplates" => ListItemTemplates(OptionalString(args, "filter"), OptionalString(args, "language"), OptionalInt(args, "limit")),
        "listDebuggedProcesses" => ListDebuggedProcesses(),
        "listThreads" => ListThreads(),
        "listExceptions" => ListExceptions(OptionalString(args, "group")),
        "listStackFrames" => ListStackFrames(),
        "findInFiles" => FindInFiles(
            RequireString(args, "pattern"),
            OptionalBool(args, "matchCase") == true,
            OptionalBool(args, "useRegex") == true,
            OptionalString(args, "scope") ?? "solution",
            OptionalString(args, "project")),
        "replaceInFiles" => ReplaceInFiles(
            RequireString(args, "pattern"),
            RequireString(args, "replacement"),
            OptionalBool(args, "matchCase") == true,
            OptionalBool(args, "useRegex") == true,
            OptionalString(args, "scope") ?? "solution",
            OptionalString(args, "project"),
            OptionalBool(args, "confirm") == true),
        "listDocuments" => ListDocuments(),
        "saveDocument" => SaveDocument(RequireString(args, "file"), OptionalBool(args, "confirm") == true),
        "closeDocument" => CloseDocument(RequireString(args, "file"), OptionalBool(args, "discardChanges") == true, OptionalBool(args, "confirm") == true),
        "undoGroup" => UndoGroup(RequireString(args, "name"), args, OptionalBool(args, "confirm") == true),
        "listCodeElements" => CodeModelActions.ListCodeElements(RequireString(args, "file")),
        "semanticReferences" => CodeModelActions.SemanticReferences(RequireString(args, "file"), RequireInt(args, "line"), OptionalInt(args, "column") ?? 1),
        "semanticDefinition" => CodeModelActions.SemanticDefinition(RequireString(args, "file"), RequireInt(args, "line"), OptionalInt(args, "column") ?? 1),
        "renameSymbol" => CodeModelActions.RenameSymbol(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            RequireString(args, "newName"),
            OptionalBool(args, "confirm") == true),
        "editMethod" => CodeModelActions.EditMethod(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            RequireString(args, "newMethodText"),
            ParseCallerEdits(args),
            ParseDocUpdates(args),
            ParseMemberSpecs(args),
            OptionalBool(args, "rewriteCallers") == true,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "threadParameter" => CodeModelActions.ThreadParameter(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            RequireString(args, "declaration"),
            RequireString(args, "valueAtTop"),
            OptionalInt(args, "depth") ?? 1,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "addEnumMember" => CodeModelActions.AddEnumMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "name"),
            OptionalString(args, "value"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "editEnumMember" => CodeModelActions.EditEnumMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "value"),
            OptionalBool(args, "allowValueChange") == true,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "editType" => CodeModelActions.EditType(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalString(args, "modifiers"),
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty("baseList", out _) ? StringList(args, "baseList").ToList() : null,
            StringList(args, "attributesAdd").ToList(),
            StringList(args, "attributesRemove").ToList(),
            OptionalString(args, "typeParameters"),
            OptionalString(args, "constraints"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "checkDocs" => CodeModelActions.CheckDocs(RequireString(args, "file")),
        "listCodeActions" => CodeModelActions.ListCodeActions(RequireString(args, "file"), RequireInt(args, "line")),
        "removeUnused" => CodeModelActions.RemoveUnused(ParseUnusedItems(args), OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "updateDocs" => CodeModelActions.UpdateDocs(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalString(args, "summary"),
            ParseDocUpdates(args).Items,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "convert" => CodeModelActions.Convert(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "kind"),
            OptionalInt(args, "depth") ?? 0,
            OptionalBool(args, "suffix") == true,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "extractInterface" => CodeModelActions.ExtractInterface(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "interfaceName"),
            StringList(args, "members").ToList(),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "extractValue" => CodeModelActions.ExtractValue(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "expression"),
            RequireString(args, "name"),
            RequireString(args, "kind"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "inlineMethod" => CodeModelActions.InlineMethod(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "extractMethod" => CodeModelActions.ExtractMethod(
            RequireString(args, "file"),
            RequireInt(args, "startLine"),
            RequireInt(args, "endLine"),
            RequireString(args, "name"),
            OptionalString(args, "accessibility") ?? "private",
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "applyCodeAction" => CodeModelActions.ApplyCodeAction(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "id"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "extractPartial" => CodeModelActions.ExtractPartial(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            StringList(args, "members").ToList(),
            RequireString(args, "suffix"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "mergePartial" => CodeModelActions.MergePartial(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "organizeFile" => CodeModelActions.OrganizeFile(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "action"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "moveMember" => CodeModelActions.MoveMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            RequireString(args, "toType"),
            ParseCallerEdits(args),
            OptionalBool(args, "toStatic") == true,
            OptionalBool(args, "pullUp") == true,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "generateMembers" => CodeModelActions.GenerateMembers(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "kind"),
            StringList(args, "members").ToList(),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "generateConstructor" => CodeModelActions.GenerateConstructor(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            StringList(args, "members").ToList(),
            OptionalString(args, "accessibility") ?? "public",
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "implementMembers" => CodeModelActions.ImplementMembers(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "mode"),
            StringList(args, "members").ToList(),
            OptionalBool(args, "explicit") == true,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "addType" => CodeModelActions.AddType(
            RequireString(args, "anchorFile"),
            RequireString(args, "declaration"),
            StringList(args, "usings").ToList(),
            OptionalString(args, "namespaceName"),
            OptionalString(args, "summary"),
            StringList(args, "attributes").ToList(),
            OptionalBool(args, "withTests") == true,
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "addInterfaceMember" => CodeModelActions.AddInterfaceMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "declaration"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "changeSignature" => CodeModelActions.ChangeSignature(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            ParseNewParameters(args),
            ParseDocUpdates(args),
            new HeaderChange { ReturnType = OptionalString(args, "returnType"), Accessibility = OptionalString(args, "accessibility") },
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "addMethod" => CodeModelActions.AddMethod(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "newMethodText"),
            OptionalInt(args, "afterLine"),
            ParseDocUpdates(args),
            false,
            ParseMemberSpecs(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "addOverload" => CodeModelActions.AddMethod(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "newMethodText"),
            null,
            ParseDocUpdates(args),
            true,
            ParseMemberSpecs(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "deleteMember" => CodeModelActions.DeleteMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            ParseCallerEdits(args),
            StringList(args, "deleteTests").ToList(),
            OptionalBool(args, "confirm") == true,
            OptionalString(args, "planHash")),
        "deleteType" => CodeModelActions.DeleteType(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalInt(args, "column") ?? 1,
            ParseCallerEdits(args),
            StringList(args, "deleteTests").ToList(),
            OptionalBool(args, "deleteFile") == true,
            OptionalBool(args, "confirm") == true,
            OptionalString(args, "planHash")),
        "listUnused" => CodeModelActions.ListUnused(OptionalString(args, "project"), OptionalBool(args, "includePublic") == true),
        "renameNamespace" => CodeModelActions.RenameNamespace(
            RequireString(args, "oldNamespace"),
            RequireString(args, "newNamespace"),
            OptionalBool(args, "moveFiles") == true,
            ParseCallerEdits(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "moveType" => CodeModelActions.MoveType(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "newNamespace"),
            OptionalBool(args, "moveFiles") == true,
            ParseCallerEdits(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "addMember" => CodeModelActions.AddMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            ParseMemberSpecs(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "editMember" => CodeModelActions.EditMember(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            ParseMemberEdit(args),
            ParseCallerEdits(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "tddAddTest" => CodeModelActions.TddAddTest(
            RequireString(args, "testFile"),
            RequireInt(args, "testLine"),
            RequireString(args, "testMethodText"),
            RequireString(args, "methodFile"),
            RequireInt(args, "methodLine"),
            RequireString(args, "methodText"),
            ParseDocUpdates(args),
            ParseMemberSpecs(args),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "tddUpdateTest" => CodeModelActions.TddUpdateTest(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            RequireString(args, "newBody"),
            OptionalString(args, "targetFile"),
            OptionalInt(args, "targetLine"),
            OptionalBool(args, "confirm") == true, OptionalString(args, "planHash")),
        "projectProperties" => ProjectProperties(RequireString(args, "project"), RequireStringArray(args, "names")),
        "addProjectItem" => AddProjectItem(
            RequireString(args, "project"),
            RequireString(args, "file"),
            RequireString(args, "mode"),
            OptionalString(args, "template"),
            OptionalBool(args, "confirm") == true),
        "removeProjectItem" => RemoveProjectItem(RequireString(args, "project"), RequireString(args, "file"), OptionalBool(args, "confirm") == true),
        "closeSolution" => CloseSolution(OptionalBool(args, "saveFirst") == true, OptionalBool(args, "confirm") == true),
        "listTasks" => ListTasks(),
        "addTask" => TaskListActions.AddTask(
            RequireString(args, "text"),
            OptionalString(args, "file"),
            OptionalInt(args, "line"),
            OptionalString(args, "category") ?? "Message",
            OptionalBool(args, "confirm") == true),
        "clearTasks" => TaskListActions.ClearTasks(OptionalBool(args, "confirm") == true),
        "setStatus" => ShellActions.SetStatus(OptionalString(args, "text") ?? string.Empty, OptionalBool(args, "confirm") == true),
        "writeActivityLog" => ShellActions.WriteActivityLog(
            RequireString(args, "level"),
            RequireString(args, "source"),
            RequireString(args, "message"),
            OptionalBool(args, "confirm") == true),
        "getSetting" => ShellActions.GetSetting(RequireString(args, "key")),
        "showInfoBar" => ShellActions.ShowInfoBar(RequireString(args, "text"), InfoBarLabels(args), OptionalBool(args, "confirm") == true),
        "setBreakpointCondition" => SetBreakpointCondition(
            RequireString(args, "file"),
            RequireInt(args, "line"),
            OptionalString(args, "condition"),
            OptionalString(args, "hitCountType"),
            OptionalInt(args, "hitCount"),
            OptionalBool(args, "confirm") == true),
        "enableBreakpoint" => SetBreakpointEnabled(RequireString(args, "file"), RequireInt(args, "line"), true, OptionalBool(args, "confirm") == true),
        "disableBreakpoint" => SetBreakpointEnabled(RequireString(args, "file"), RequireInt(args, "line"), false, OptionalBool(args, "confirm") == true),
        "attachProcess" => AttachProcess(RequireInt(args, "pid"), OptionalBool(args, "confirm") == true),
        "detachAll" => DetachAll(OptionalBool(args, "confirm") == true),
        "setExceptionBreak" => SetExceptionBreak(
            RequireString(args, "group"),
            RequireString(args, "name"),
            OptionalBool(args, "breakWhenThrown"),
            OptionalBool(args, "breakWhenUserUnhandled"),
            OptionalBool(args, "confirm") == true),
        _ => throw new NotSupportedException(Strings.Format("UnknownAction", action)),
    };

    // Clean and publish run through the IDE's build, not a child process this bridge starts, so no process is
    // tracked here. Every pid is refused; only processes the bridge started may be killed.
    private object KillProcess(int pid) =>
        throw new InvalidOperationException(Strings.Format("ProcessNotStartedByBridge", pid));

    private object SetStartupProject(string projectName)
    {
        var project = FindProject(projectName)
            ?? throw new InvalidOperationException(Strings.Format("NoProjectNamed", projectName));

        dte.Solution.SolutionBuild.StartupProjects = new object[] { project.UniqueName };
        return new { startup_project = project.UniqueName };
    }

    private object StartDebugging()
    {
        dte.Debugger.Go(false);
        return new { debugger_mode = dte.Debugger.CurrentMode.ToString() };
    }

    private object StopDebugging()
    {
        dte.Debugger.Stop(false);
        return new { debugger_mode = dte.Debugger.CurrentMode.ToString() };
    }

    // Starts Test Explorer's run for the whole solution. Test Explorer refuses a run while discovery or an earlier run is still
    // going. That refusal is reported as data, not thrown, so the caller can wait on its own thread and try again.
    private object RunTests()
    {
        try
        {
            dte.ExecuteCommand("TestExplorer.RunAllTests");
            return new { command = "TestExplorer.RunAllTests", refused = false, hresult = 0 };
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return new { command = "TestExplorer.RunAllTests", refused = true, hresult = ex.HResult };
        }
    }

    // Formats every C# file under the solution folder through the IDE, saving each one. SDK-style projects do not list
    // their .cs files as project items, so the files are found on disk. Each file is opened, formatted and saved.
    private object FormatSolution()
    {
        var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
        if (string.IsNullOrEmpty(solutionDir))
        {
            throw new InvalidOperationException(Strings.Get("NoSolutionOpenToFormat"));
        }

        var formatted = new List<string>();
        foreach (var path in Directory.EnumerateFiles(solutionDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = path.Substring(solutionDir.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git" or "TestResults" or "node_modules"))
            {
                continue;
            }

            NormalizeLineEndings(path);
            dte.ItemOperations.OpenFile(path);
            var window = dte.ActiveDocument;
            if (window is null || !string.Equals(window.FullName, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            dte.ExecuteCommand("Edit.FormatDocument");
            window.Save();
            formatted.Add(path);
        }

        return new { formatted = formatted.Count, files = formatted };
    }

    private object FormatDocument()
    {
        if (dte.ActiveDocument is null)
        {
            throw new InvalidOperationException(Strings.Get("NoActiveDocumentToFormat"));
        }

        dte.ExecuteCommand("Edit.FormatDocument");
        return new { document = dte.ActiveDocument.FullName };
    }

    private object Build()
    {
        dte.Solution.SolutionBuild.Build(WaitForBuildToFinish: true);
        var failed = dte.Solution.SolutionBuild.LastBuildInfo;
        return new { succeeded = failed == 0, failed_projects = failed };
    }

    // Reads the Error List, which holds the last build's errors and warnings.
    // vsBuildErrorLevel: 1 = error, 2 = warning (per EnvDTE); other levels are excluded.
    private object GetBuildDiagnostics(string severity)
    {
        var wanted = severity;
        var items = new List<object>();
        var errors = dte.ToolWindows.ErrorList.ErrorItems;
        for (var i = 1; i <= errors.Count; i++)
        {
            ErrorItem item = errors.Item(i);
            var kind = item.ErrorLevel switch
            {
                vsBuildErrorLevel.vsBuildErrorLevelHigh => "error",
                vsBuildErrorLevel.vsBuildErrorLevelMedium => "warning",
                _ => null,
            };

            if (kind is null || (!string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase) && !string.Equals(wanted, kind + "s", StringComparison.OrdinalIgnoreCase) && !string.Equals(wanted, kind, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            items.Add(new { severity = kind, file = item.FileName, line = item.Line, column = item.Column, message = item.Description });
        }

        return new { count = items.Count, diagnostics = items };
    }

    // Runs Visual Studio's default Code Cleanup profile on the active document. The command name was listed from a live VS 18.0
    // instance; "Edit.RunCodeCleanup" does not exist there and made this action fail with E_FAIL.
    private object CodeCleanup()
    {
        if (dte.ActiveDocument is null)
        {
            throw new InvalidOperationException(Strings.Get("NoActiveDocumentToCleanUp"));
        }

        dte.ExecuteCommand("EditorContextMenus.FileHealthIndicator.RunDefaultCodeCleanup");
        return new { document = dte.ActiveDocument.FullName };
    }

    private object GetOutput(string paneName, int maxLines, int? fromOffset = null, int maxChars = 20000, string? anchor = null)
    {
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (!string.Equals(pane.Name, paneName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = PaneText(pane);
            if (fromOffset is int offset)
            {
                var slice = OutputPaneSlice.Slice(text, offset, maxChars, anchor);
                return new { pane = pane.Name, text = slice.Text, nextOffset = slice.NextOffset, anchor = slice.Anchor, reset = slice.Reset };
            }

            var lines = text.Split('\n');
            var tail = lines.Skip(Math.Max(0, lines.Length - maxLines));
            return new { pane = pane.Name, lines = tail.Select(l => l.TrimEnd('\r')).ToList() };
        }

        throw new InvalidOperationException(Strings.Format("NoOutputPaneNamed", paneName));
    }

    private object ListOutputPanes()
    {
        var names = new List<string>();
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            names.Add(pane.Name);
        }

        return new { count = names.Count, panes = names };
    }

    private object SearchOutput(string paneName, string pattern)
    {
        var pane = FindOutputPane(paneName);
        var text = PaneText(pane);
        var regex = new System.Text.RegularExpressions.Regex(pattern);
        var matches = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => regex.IsMatch(l))
            .ToList();
        return new { pane = pane.Name, pattern, match_count = matches.Count, lines = matches };
    }

    private object ClearOutput(string paneName)
    {
        FindOutputPane(paneName).Clear();
        return new { pane = paneName, cleared = true };
    }

    // A pane that has never been written has no text document yet. That is an empty pane, not an error.
    private static string PaneText(OutputWindowPane pane)
    {
        try
        {
            var doc = pane.TextDocument;
            return doc.StartPoint.CreateEditPoint().GetText(doc.EndPoint);
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private OutputWindowPane FindOutputPane(string paneName)
    {
        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            if (string.Equals(pane.Name, paneName, StringComparison.OrdinalIgnoreCase))
            {
                return pane;
            }
        }

        throw new InvalidOperationException(Strings.Format("NoOutputPaneNamed", paneName));
    }

    private object GetLocals(int maxItems)
    {
        if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new InvalidOperationException(Strings.Get("NotAtBreakpointLocals"));
        }

        var frame = dte.Debugger.CurrentStackFrame
            ?? throw new InvalidOperationException(Strings.Get("NoCurrentStackFrame"));

        var locals = new List<object>();
        foreach (Expression expression in frame.Locals)
        {
            if (locals.Count >= maxItems)
            {
                break;
            }

            locals.Add(new { name = expression.Name, value = expression.Value, type = expression.Type });
        }

        return new { function = frame.FunctionName, count = locals.Count, locals };
    }

    // Reads test.hotReload from this instance's settings.json (the "Enable Hot Reloaded test runs" option under
    // Tools > Options > Test). The instance folder is 18.0_<first 8 chars of instanceId>, where instanceId comes from
    // the instance's state.json in ProgramData, matched by installationPath.
    private object TestHotReloadState()
    {
        var settingsPath = InstanceSettingsPath();
        if (settingsPath is null)
        {
            throw new InvalidOperationException(Strings.Get("SettingsJsonNotFound"));
        }

        // Visual Studio leaves the option out of settings.json until it is set, and an unset option is off.
        var value = false;
        using (var doc = JsonDocument.Parse(StripComments(File.ReadAllText(settingsPath))))
        {
            if (doc.RootElement.TryGetProperty("test.hotReload", out var prop) && (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
            {
                value = prop.GetBoolean();
            }
        }

        return new { ide = "VisualStudio", enabled = value, setting = "test.hotReload", settingsPath };
    }

    private string? InstanceSettingsPath()
    {
        var devenv = dte.FileName;
        if (string.IsNullOrEmpty(devenv))
        {
            return null;
        }

        // devenv.exe sits in <install>\Common7\IDE.
        var install = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(devenv)));
        var instancesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "VisualStudio", "Packages", "_Instances");
        if (install is null || !Directory.Exists(instancesRoot))
        {
            return null;
        }

        foreach (var dir in Directory.GetDirectories(instancesRoot))
        {
            var state = Path.Combine(dir, "state.json");
            if (!File.Exists(state))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            var root = doc.RootElement;
            if (!root.TryGetProperty("installationPath", out var ip) || !string.Equals(ip.GetString()?.TrimEnd('\\'), install.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Some state.json files leave instanceId empty. The folder name is the instance id.
            var instanceId = root.TryGetProperty("instanceId", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(instanceId))
            {
                instanceId = Path.GetFileName(dir);
            }

            if (string.IsNullOrEmpty(instanceId) || instanceId.Length < 8)
            {
                continue;
            }

            var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio", "18.0_" + instanceId.Substring(0, 8), "settings.json");
            return File.Exists(candidate) ? candidate : null;
        }

        return null;
    }

    // settings.json starts with a /* */ comment, which System.Text.Json does not accept by default.
    private static string StripComments(string text)
    {
        var start = text.IndexOf("/*", StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf("*/", start, StringComparison.Ordinal);
        return end < 0 ? text : text.Remove(start, end + 2 - start);
    }

    private object OpenSolution(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException(Strings.Get("SolutionFileNotFound"), solutionPath);
        }

        dte.Solution.Open(solutionPath);
        return new { solution_path = solutionPath, opened = true };
    }

    // Puts the caret on file:line:column, then runs one IDE command that works on the caret. The result is the IDE state after the
    // command: results of find commands appear in their own windows, so they are not returned as data.
    private object RunAtCaret(string file, int line, int column, string command)
    {
        if (!Path.IsPathRooted(file))
        {
            throw new ArgumentException(Strings.Get("PathMustBeAbsolute"), nameof(file));
        }

        var full = Path.GetFullPath(file);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(Strings.Get("FileNotFound"), full);
        }

        if (line < 1 || column < 1)
        {
            throw new ArgumentException(Strings.Get("LineAndColumnStartAtOne"));
        }

        dte.ItemOperations.OpenFile(full);
        var document = dte.ActiveDocument;
        if (document is null || !string.Equals(document.FullName, full, StringComparison.OrdinalIgnoreCase)
            || document.Selection is not TextSelection selection)
        {
            throw new InvalidOperationException(Strings.Get("NoTextDocumentAtCaret"));
        }

        var lastLine = document.Object("TextDocument") is TextDocument text ? text.EndPoint.Line : int.MaxValue;
        if (line > lastLine)
        {
            throw new ArgumentException(Strings.Format("LineBeyondEnd", line, lastLine));
        }

        selection.MoveToLineAndOffset(line, column);
        try
        {
            dte.ExecuteCommand(command);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new InvalidOperationException(Strings.Format("CommandUnavailable", command, ex.HResult), ex);
        }

        return new { file = full, requested_line = line, requested_column = column, command, caret_line = selection.CurrentLine, caret_column = selection.CurrentColumn, active_document = dte.ActiveDocument?.FullName };
    }

    private object OpenDocument(string file, int? line)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(Strings.Get("FileNotFound"), file);
        }

        dte.ItemOperations.OpenFile(file);
        if (line is int oneBased && oneBased >= 1)
        {
            ((TextSelection)dte.ActiveDocument.Selection).GotoLine(oneBased, true);
        }

        return new { file, line, opened = true };
    }

    private object SetBreakpoint(string file, int line)
    {
        var breakpoints = dte.Debugger.Breakpoints.Add(File: file, Line: line);
        return new { file, line, added = breakpoints.Count };
    }

    private object RemoveBreakpoint(string file, int line)
    {
        var breakpoints = dte.Debugger.Breakpoints;
        var removed = 0;
        for (var i = breakpoints.Count; i >= 1; i--)
        {
            var bp = breakpoints.Item(i);
            if (SameFile(bp.File, file) && bp.FileLine == line)
            {
                bp.Delete();
                removed++;
            }
        }

        return new { file, line, removed };
    }

    private object ListBreakpoints()
    {
        var breakpoints = dte.Debugger.Breakpoints;
        var list = new List<object>();
        for (var i = 1; i <= breakpoints.Count; i++)
        {
            var bp = breakpoints.Item(i);
            list.Add(new { file = bp.File, line = bp.FileLine, enabled = bp.Enabled, condition = bp.Condition, function = bp.FunctionName });
        }

        return new { count = list.Count, breakpoints = list };
    }

    private object Step(string mode)
    {
        switch (mode.ToUpperInvariant())
        {
            case "OVER":
                dte.Debugger.StepOver(false);
                break;
            case "INTO":
                dte.Debugger.StepInto(false);
                break;
            case "OUT":
                dte.Debugger.StepOut(false);
                break;
            default:
                throw new ArgumentException(Strings.Get("StepModeInvalid"));
        }

        return new { mode, debugger_mode = dte.Debugger.CurrentMode.ToString() };
    }

    private object DebugState()
    {
        var breakMode = dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
        return new
        {
            debugger_mode = dte.Debugger.CurrentMode.ToString(),
            function = breakMode ? dte.Debugger.CurrentStackFrame?.FunctionName : null,
        };
    }

    private object Evaluate(string expression)
    {
        RequireBreakMode();
        var result = dte.Debugger.GetExpression(expression, false, 5000);
        return new
        {
            expression,
            valid = result.IsValidValue,
            value = result.Value,
            type = result.Type,
        };
    }

    private object AddWatch(string expression)
    {
        if (!_watches.Contains(expression))
        {
            _watches.Add(expression);
        }

        return new { count = _watches.Count, watches = _watches.ToList() };
    }

    private object RemoveWatch(string expression)
    {
        var removed = _watches.Remove(expression);
        return new { removed, count = _watches.Count, watches = _watches.ToList() };
    }

    // Values are evaluated now. Outside break mode they are reported as unavailable, not as errors.
    private object ListWatches()
    {
        var breakMode = dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
        var items = _watches.Select(expression =>
        {
            if (!breakMode)
            {
                return new { expression, valid = false, value = (string?)null, type = (string?)null, reason = (string?)Strings.Get("NotAtBreakpointReason") };
            }

            var result = dte.Debugger.GetExpression(expression, false, 5000);
            return new { expression, valid = result.IsValidValue, value = (string?)result.Value, type = (string?)result.Type, reason = (string?)null };
        }).ToList();

        return new { count = items.Count, watches = items };
    }

    private object ListConfigurations()
    {
        var configurations = dte.Solution.SolutionBuild.SolutionConfigurations;
        var names = new List<string>();
        for (var i = 1; i <= configurations.Count; i++)
        {
            names.Add(configurations.Item(i).Name);
        }

        return new { active = dte.Solution.SolutionBuild.ActiveConfiguration?.Name, configurations = names };
    }

    private object SetConfiguration(string name)
    {
        var configurations = dte.Solution.SolutionBuild.SolutionConfigurations;
        for (var i = 1; i <= configurations.Count; i++)
        {
            var configuration = configurations.Item(i);
            if (string.Equals(configuration.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                configuration.Activate();
                return new { active = dte.Solution.SolutionBuild.ActiveConfiguration?.Name };
            }
        }

        throw new ArgumentException(Strings.Format("NoSolutionConfiguration", name));
    }

    private object Clean()
    {
        dte.Solution.SolutionBuild.Clean(true);
        var failed = dte.Solution.SolutionBuild.LastBuildInfo;
        return new { succeeded = failed == 0, configuration = dte.Solution.SolutionBuild.ActiveConfiguration?.Name };
    }

    // Publishes the startup project through the IDE's publish command. The command name is not verified against a live IDE.
    private object Publish()
    {
        dte.ExecuteCommand("Build.PublishSelection");
        return new { command = "Build.PublishSelection", configuration = dte.Solution.SolutionBuild.ActiveConfiguration?.Name };
    }

    // Saves every document, then reports any that are still unsaved. The MCP kills the instance only when safe is true.
    private object PrepareKill()
    {
        dte.ExecuteCommand("File.SaveAll");

        var documents = dte.Documents;
        var unsaved = new List<string>();
        for (var i = 1; i <= documents.Count; i++)
        {
            var doc = documents.Item(i);
            if (!doc.Saved)
            {
                unsaved.Add(doc.FullName);
            }
        }

        return new { safe = unsaved.Count == 0, unsaved };
    }

    private void RequireBreakMode()
    {
        if (dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new InvalidOperationException(Strings.Get("NotAtBreakpointExpressions"));
        }
    }

    private static bool SameFile(string? a, string b) =>
        !string.IsNullOrEmpty(a) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private Project? FindProject(string name)
    {
        foreach (Project project in dte.Solution.Projects)
        {
            // Some solution folders and unloaded projects refuse the walk (E_INVALIDARG). Skip them; a name that is
            // really missing then returns null, and the caller reports "no project named".
            Project? found;
            try
            {
                found = FindInProject(project, name);
            }
            catch (COMException)
            {
                continue;
            }

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // Walks solution folders recursively so projects nested in folders can be targeted.
    private static Project? FindInProject(Project project, string name)
    {
        if (string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(project.UniqueName, name, StringComparison.OrdinalIgnoreCase))
        {
            return project;
        }

        foreach (Project child in Subprojects(project))
        {
            var found = FindInProject(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<Project> Subprojects(Project folder)
    {
        foreach (ProjectItem item in folder.ProjectItems)
        {
            if (item.SubProject is Project sub)
            {
                yield return sub;
            }
        }
    }

    // Process lists for the debugger. Read-only: nothing is attached or detached here.
    // Item templates this Visual Studio offers in Add New Item: its shipped and extension templates (ItemTemplatesCache,
    // ItemTemplates), those the user's instance data folders cache, and the user's own under Documents. Each path can be
    // passed as the template of addProjectItem in mode fromNew.
    private object ListItemTemplates(string? filter, string? language, int? limit)
    {
        var max = Math.Min(Math.Max(limit ?? 200, 1), 1000);
        var ide = Path.GetDirectoryName(dte.FullName) ?? string.Empty;
        var roots = new List<string> { Path.Combine(ide, "ItemTemplatesCache"), Path.Combine(ide, "ItemTemplates") };
        roots.AddRange(MatchingFolders(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio"),
            "*", "ItemTemplatesCache"));
        roots.AddRange(MatchingFolders(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Visual Studio*", Path.Combine("Templates", "ItemTemplates")));

        var found = ItemTemplateFinder.Find(roots, filter, language, max);
        return new
        {
            count = found.Templates.Count,
            truncated = found.Truncated,
            templates = found.Templates.Select(t => new { name = t.Name, description = t.Description, language = t.Language, category = t.Category, path = t.Path }).ToList(),
        };
    }

    // <parent>\<match>\<suffix> for every folder under parent that matches the pattern and has the suffix folder.
    private static IEnumerable<string> MatchingFolders(string parent, string pattern, string suffix)
    {
        if (!Directory.Exists(parent))
        {
            yield break;
        }

        string[] matches;
        try
        {
            matches = Directory.GetDirectories(parent, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var match in matches)
        {
            var folder = Path.Combine(match, suffix);
            if (Directory.Exists(folder))
            {
                yield return folder;
            }
        }
    }

    private object ListProcesses()
    {
        var processes = dte.Debugger.LocalProcesses;
        var list = new List<object>();
        foreach (EnvDTE.Process process in processes)
        {
            list.Add(new { pid = process.ProcessID, name = process.Name });
        }

        return new { processes = list };
    }

    // Threads of the current process. Only meaningful in break mode; outside it the list is empty.
    private object ListThreads()
    {
        var mode = dte.Debugger.CurrentMode;
        if (mode != dbgDebugMode.dbgBreakMode)
        {
            return new { debugger_mode = mode.ToString(), current_thread_id = (int?)null, threads = Array.Empty<object>() };
        }

        var list = new List<object>();
        foreach (EnvDTE.Thread thread in ((EnvDTE90.Process3)dte.Debugger.CurrentProcess).Threads)
        {
            list.Add(new { id = thread.ID, name = thread.Name });
        }

        return new { debugger_mode = mode.ToString(), current_thread_id = dte.Debugger.CurrentThread?.ID, threads = list };
    }

    // Break-on-throw settings, grouped as the debugger groups them. Read-only: nothing is changed here.
    // Optional group filter: the full list is large (over 100 KB), so callers usually name one group.
    private object ListExceptions(string? groupFilter)
    {
        var list = new List<object>();
        foreach (EnvDTE90.ExceptionSettings group in ((EnvDTE100.Debugger5)dte.Debugger).ExceptionGroups)
        {
            if (groupFilter is not null && group.Name != groupFilter)
            {
                continue;
            }

            foreach (EnvDTE90.ExceptionSetting setting in group)
            {
                // Code throws for exception types that have no exception code; report null for those.
                uint? code = null;
                if (group.SupportsExceptionCodes)
                {
                    try
                    {
                        code = setting.Code;
                    }
                    catch (COMException)
                    {
                        code = null;
                    }
                }

                list.Add(new
                {
                    group = group.Name,
                    name = setting.Name,
                    code,
                    break_when_thrown = setting.BreakWhenThrown,
                    break_when_user_unhandled = setting.BreakWhenUserUnhandled,
                    user_defined = setting.UserDefined,
                });
            }
        }

        return new { exceptions = list };
    }

    // Changes break-on-throw settings for one exception. Without confirm it only reports the change.
    private object SetExceptionBreak(string group, string name, bool? breakWhenThrown, bool? breakWhenUserUnhandled, bool confirm)
    {
        if (breakWhenThrown is null && breakWhenUserUnhandled is null)
        {
            throw new ArgumentException(Strings.Get("ExceptionFlagRequired"));
        }

        foreach (EnvDTE90.ExceptionSettings groupSettings in ((EnvDTE100.Debugger5)dte.Debugger).ExceptionGroups)
        {
            if (groupSettings.Name != group)
            {
                continue;
            }

            foreach (EnvDTE90.ExceptionSetting setting in groupSettings)
            {
                if (setting.Name != name)
                {
                    continue;
                }

                var change = new
                {
                    group,
                    name,
                    break_when_thrown = new { current = setting.BreakWhenThrown, proposed = breakWhenThrown ?? setting.BreakWhenThrown },
                    break_when_user_unhandled = new { current = setting.BreakWhenUserUnhandled, proposed = breakWhenUserUnhandled ?? setting.BreakWhenUserUnhandled },
                };
                if (!confirm)
                {
                    return new { planned = true, would = change };
                }

                if (breakWhenThrown is bool thrown)
                {
                    groupSettings.SetBreakWhenThrown(thrown, setting);
                }

                if (breakWhenUserUnhandled is bool unhandled)
                {
                    groupSettings.SetBreakWhenUserUnhandled(unhandled, setting);
                }

                return new { planned = false, applied = change };
            }
        }

        throw new ArgumentException(Strings.Format("NoExceptionNamed", group, name));
    }

    // Stack frames of the current thread. Only meaningful in break mode; outside it the list is empty.
    private object ListStackFrames()
    {
        var mode = dte.Debugger.CurrentMode;
        var frames = new List<object>();
        var thread = mode == dbgDebugMode.dbgBreakMode ? dte.Debugger.CurrentThread : null;
        if (thread is not null)
        {
            var index = 0;
            foreach (EnvDTE.StackFrame frame in thread.StackFrames)
            {
                frames.Add(new { index, function = frame.FunctionName, return_type = frame.ReturnType });
                index++;
            }
        }

        return new { debugger_mode = mode.ToString(), frames };
    }

    // Changes the condition, hit-count rule, or both, on the breakpoint(s) at file:line. Without confirm it only reports the change.
    private object SetBreakpointCondition(string file, int line, string? condition, string? hitCountType, int? hitCount, bool confirm)
    {
        if (condition is null && hitCountType is null && hitCount is null)
        {
            throw new ArgumentException(Strings.Get("ConditionFlagRequired"));
        }

        var hitType = hitCountType switch
        {
            null => (dbgHitCountType?)null,
            "always" => dbgHitCountType.dbgHitCountTypeNone,
            "equal" => dbgHitCountType.dbgHitCountTypeEqual,
            "multiple" => dbgHitCountType.dbgHitCountTypeMultiple,
            "greaterOrEqual" => dbgHitCountType.dbgHitCountTypeGreaterOrEqual,
            _ => throw new ArgumentException(Strings.Format("UnknownHitCountType", hitCountType)),
        };

        var matches = new List<Breakpoint>();
        var breakpoints = dte.Debugger.Breakpoints;
        for (var i = 1; i <= breakpoints.Count; i++)
        {
            var bp = breakpoints.Item(i);
            if (SameFile(bp.File, file) && bp.FileLine == line)
            {
                matches.Add(bp);
            }
        }

        if (matches.Count == 0)
        {
            throw new ArgumentException(Strings.Format("NoBreakpointAt", file, line));
        }

        var changes = matches.Select(bp => new
        {
            file = bp.File,
            line = bp.FileLine,
            condition = new { current = bp.Condition, proposed = condition ?? bp.Condition },
            hit_count_type = new { current = bp.HitCountType.ToString(), proposed = hitType?.ToString() ?? bp.HitCountType.ToString() },
            hit_count = new { current = bp.HitCountTarget, proposed = hitCount ?? bp.HitCountTarget },
        }).ToList();

        if (!confirm)
        {
            return new { planned = true, would = changes };
        }

        // Breakpoint.Condition and the hit-count properties are read-only. The supported path is to re-add the breakpoint at the
        // same location with the new values (Breakpoints.Add overload with Condition and HitCount). Hit counts reset on re-add.
        foreach (var bp in matches)
        {
            var bpFile = bp.File;
            var bpLine = bp.FileLine;
            var wasEnabled = bp.Enabled;
            var newCondition = condition ?? bp.Condition;
            var newConditionType = condition is not null ? dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue : bp.ConditionType;
            var newHitType = hitType ?? bp.HitCountType;
            var newHitCount = hitCount ?? bp.HitCountTarget;

            bp.Delete();
            var added = dte.Debugger.Breakpoints.Add(
                File: bpFile,
                Line: bpLine,
                Condition: newCondition,
                ConditionType: newConditionType,
                HitCount: newHitCount,
                HitCountType: newHitType);
            added.Item(1).Enabled = wasEnabled;
        }

        return new { planned = false, applied = changes };
    }

    // Enables or disables every breakpoint at file:line. Without confirm it only reports the change.
    private object SetBreakpointEnabled(string file, int line, bool enabled, bool confirm)
    {
        var matches = new List<Breakpoint>();
        var breakpoints = dte.Debugger.Breakpoints;
        for (var i = 1; i <= breakpoints.Count; i++)
        {
            var bp = breakpoints.Item(i);
            if (SameFile(bp.File, file) && bp.FileLine == line)
            {
                matches.Add(bp);
            }
        }

        if (matches.Count == 0)
        {
            throw new ArgumentException(Strings.Format("NoBreakpointAt", file, line));
        }

        var changes = matches.Select(bp => new { file = bp.File, line = bp.FileLine, enabled = new { current = bp.Enabled, proposed = enabled } }).ToList();
        if (!confirm)
        {
            return new { planned = true, would = changes };
        }

        foreach (var bp in matches)
        {
            bp.Enabled = enabled;
        }

        return new { planned = false, applied = changes };
    }

    // Attaches the debugger to one local process. Without confirm it only reports the target.
    private object AttachProcess(int pid, bool confirm)
    {
        EnvDTE.Process? target = null;
        foreach (EnvDTE.Process process in dte.Debugger.LocalProcesses)
        {
            if (process.ProcessID == pid)
            {
                target = process;
                break;
            }
        }

        if (target is null)
        {
            throw new ArgumentException(Strings.Format("NoLocalProcess", pid));
        }

        if (!confirm)
        {
            return new { planned = true, would = new { pid, name = target.Name } };
        }

        target.Attach();
        return new { planned = false, applied = new { pid, name = target.Name } };
    }

    // Detaches from every attached process. Without confirm it lists what would be detached.
    private object DetachAll(bool confirm)
    {
        var attached = new List<object>();
        foreach (EnvDTE.Process process in ((EnvDTE90a.Debugger4)dte.Debugger).DebuggedProcesses)
        {
            attached.Add(new { pid = process.ProcessID, name = process.Name });
        }

        if (!confirm)
        {
            return new { planned = true, would = new { detached = attached } };
        }

        dte.Debugger.DetachAll();
        return new { planned = false, applied = new { detached = attached } };
    }

    // ---- Phase 2: search, documents, undo groups, code elements ----
    //
    // Search is done in managed code over the files on disk, not through Find2. Find2 exposes no results collection, and
    // driving it changes the global find state the user sees. The managed search is read-only and bounded.

    private const int MaxSearchMatches = 500;

    private object FindInFiles(string pattern, bool matchCase, bool useRegex, string scope, string? projectName)
    {
        var regex = BuildSearchPattern(pattern, matchCase, useRegex);
        var matches = new List<object>();
        var truncated = false;
        foreach (var file in SearchScopeFiles(scope, projectName))
        {
            foreach (var (line, text) in SearchFile(file, regex))
            {
                if (matches.Count >= MaxSearchMatches)
                {
                    truncated = true;
                    break;
                }

                matches.Add(new { file, line, text });
            }

            if (truncated)
            {
                break;
            }
        }

        return new { matches, truncated };
    }

    // Counts matches per file without writing. Applied: writes the replacement to each file that has matches, except files open
    // with unsaved changes, which are reported as skipped. Per-file write errors are reported in failed, not thrown.
    private object ReplaceInFiles(string pattern, string replacement, bool matchCase, bool useRegex, string scope, string? projectName, bool confirm)
    {
        var regex = BuildSearchPattern(pattern, matchCase, useRegex);
        var replaceWith = useRegex ? replacement : replacement.Replace("$", "$$");
        var planned = new List<object>();
        var skipped = new List<object>();
        var targets = new List<(string File, int Count)>();
        foreach (var file in SearchScopeFiles(scope, projectName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var count = SearchFile(file, regex).Count;
            if (count == 0)
            {
                continue;
            }

            var openDirty = OpenDocumentFor(file) is { Saved: false };
            if (openDirty)
            {
                skipped.Add(new { file, reason = Strings.Get("OpenWithUnsavedChanges") });
                continue;
            }

            targets.Add((file, count));
            planned.Add(new { file, matches = count });
        }

        if (!confirm)
        {
            return new { planned = true, would = new { files = planned, skipped } };
        }

        var applied = new List<object>();
        var failed = new List<object>();
        foreach (var (file, count) in targets)
        {
            try
            {
                var original = File.ReadAllText(file);
                var updated = regex.Replace(original, replaceWith);
                File.WriteAllText(file, updated);
                applied.Add(new { file, matches = count });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new { file, error = ex.Message });
            }
        }

        return new { planned = false, applied = new { files = applied, skipped }, failed };
    }

    private object ListDocuments()
    {
        var list = new List<object>();
        foreach (Document document in dte.Documents)
        {
            list.Add(new { fullName = document.FullName, saved = document.Saved, readOnly = document.ReadOnly });
        }

        return new { documents = list };
    }

    private object SaveDocument(string file, bool confirm)
    {
        var document = OpenDocumentFor(file) ?? throw new ArgumentException(Strings.Format("NoOpenDocument", file));
        var change = new { file = document.FullName, saved = document.Saved };
        if (!confirm)
        {
            return new { planned = true, would = new { save = change } };
        }

        document.Save();
        return new { planned = false, applied = new { file = document.FullName, saved = document.Saved } };
    }

    // Closes an open document. A document with unsaved changes is refused unless discardChanges is also set.
    private object CloseDocument(string file, bool discardChanges, bool confirm)
    {
        var document = OpenDocumentFor(file) ?? throw new ArgumentException(Strings.Format("NoOpenDocument", file));
        if (!document.Saved && !discardChanges)
        {
            throw new ArgumentException(Strings.Format("DocumentDirty", file));
        }

        var change = new { file = document.FullName, saved = document.Saved, discard_changes = discardChanges };
        if (!confirm)
        {
            return new { planned = true, would = new { close = change } };
        }

        document.Close(discardChanges ? vsSaveChanges.vsSaveChangesNo : vsSaveChanges.vsSaveChangesYes);
        return new { planned = false, applied = change };
    }

    // Applies a list of range edits inside one undo group, so the IDE undoes them as one step. Each edit's result is reported;
    // a failed edit does not stop the others, and the group always closes.
    private object UndoGroup(string name, JsonElement args, bool confirm)
    {
        var edits = ParseEdits(args);
        if (!confirm)
        {
            return new { planned = true, would = new { name, edits = edits.Select(e => new { e.File, e.StartLine, e.StartColumn, e.EndLine, e.EndColumn, e.NewText }).ToList() } };
        }

        var results = new List<object>();
        dte.UndoContext.Open(name, false);
        try
        {
            foreach (var edit in edits)
            {
                try
                {
                    ApplyEdit(edit);
                    results.Add(new { edit.File, ok = true });
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
                {
                    results.Add(new { edit.File, ok = false, error = ex.Message });
                }
            }
        }
        finally
        {
            dte.UndoContext.Close();
        }

        return new { planned = false, applied = new { name, edits = results } };
    }

    private static Regex BuildSearchPattern(string pattern, bool matchCase, bool useRegex)
    {
        var text = useRegex ? pattern : Regex.Escape(pattern);
        return new Regex(text, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    private static List<(int Line, string Text)> SearchFile(string path, Regex regex)
    {
        var hits = new List<(int Line, string Text)>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return hits;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (regex.IsMatch(lines[i]))
            {
                hits.Add((i + 1, lines[i]));
            }
        }

        return hits;
    }

    private IEnumerable<string> SearchScopeFiles(string scope, string? projectName)
    {
        switch (scope)
        {
            case "solution":
                var all = new List<string>();
                foreach (Project project in dte.Solution.Projects)
                {
                    all.AddRange(ProjectFiles(project.ProjectItems));
                }

                return all;
            case "project":
                var named = FindProject(projectName ?? throw new ArgumentException(Strings.Get("ProjectScopeNeedsName")))
                    ?? throw new ArgumentException(Strings.Format("NoProjectNamed", projectName));
                return ProjectFiles(named.ProjectItems);
            case "openDocuments":
                var open = new List<string>();
                foreach (Document document in dte.Documents)
                {
                    if (!string.IsNullOrEmpty(document.FullName) && File.Exists(document.FullName))
                    {
                        open.Add(document.FullName);
                    }
                }

                return open;
            default:
                throw new ArgumentException(Strings.Format("UnknownScope", scope));
        }
    }

    private static List<string> ProjectFiles(ProjectItems? items)
    {
        var files = new List<string>();
        if (items is null)
        {
            return files;
        }

        foreach (ProjectItem item in items)
        {
            if (item.FileCount > 0 && item.FileNames[1] is string path && File.Exists(path))
            {
                files.Add(path);
            }

            if (item.SubProject is { } sub)
            {
                files.AddRange(ProjectFiles(sub.ProjectItems));
            }
            else
            {
                files.AddRange(ProjectFiles(item.ProjectItems));
            }
        }

        return files;
    }

    private Document? OpenDocumentFor(string file)
    {
        foreach (Document document in dte.Documents)
        {
            if (SameFile(document.FullName, file))
            {
                return document;
            }
        }

        return null;
    }

    // Replaces one range through the editor's selection, so the change goes through the IDE's undo stack.
    private void ApplyEdit((string File, int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText) edit)
    {
        var document = OpenDocumentFor(edit.File) ?? dte.Documents.Open(edit.File);
        var selection = (TextSelection)document.Selection;
        selection.MoveToLineAndOffset(edit.StartLine, edit.StartColumn);
        selection.MoveToLineAndOffset(edit.EndLine, edit.EndColumn, true);
        selection.Delete();
        selection.Insert(edit.NewText);
    }

    // ---- Phase 3: solution and projects ----

    // Reads named project properties. An unknown name gives null, not an error.
    private object ProjectProperties(string projectName, IReadOnlyList<string> names)
    {
        var project = FindProject(projectName) ?? throw new ArgumentException(Strings.Format("NoProjectNamed", projectName));
        var values = new Dictionary<string, string?>();
        foreach (var name in names)
        {
            try
            {
                values[name] = project.Properties.Item(name).Value?.ToString();
            }
            catch (COMException)
            {
                values[name] = null;
            }
        }

        return new { project = projectName, values };
    }

    // mode "fromFile" adds an existing file. mode "fromNew" creates a file from a template path (template is required).
    private object AddProjectItem(string projectName, string file, string mode, string? template, bool confirm)
    {
        var project = FindProject(projectName) ?? throw new ArgumentException(Strings.Format("NoProjectNamed", projectName));
        if (mode == "fromNew" && string.IsNullOrEmpty(template))
        {
            throw new ArgumentException(Strings.Get("TemplateRequiredForNew"));
        }

        if (mode is not ("fromFile" or "fromNew"))
        {
            throw new ArgumentException(Strings.Format("UnknownItemMode", mode));
        }

        var change = new { project = projectName, file, mode, template };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        if (mode == "fromFile")
        {
            project.ProjectItems.AddFromFile(file);
        }
        else
        {
            project.ProjectItems.AddFromTemplate(template!, file);
        }

        return new { planned = false, applied = change };
    }

    // Removes the item from the project. The file stays on disk; the result says so.
    private object RemoveProjectItem(string projectName, string file, bool confirm)
    {
        var project = FindProject(projectName) ?? throw new ArgumentException(Strings.Format("NoProjectNamed", projectName));
        var item = FindProjectItemIn(project.ProjectItems, file) ?? throw new ArgumentException(Strings.Format("NoProjectItemFor", file));
        var change = new { project = projectName, file, stays_on_disk = true };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        item.Remove();
        return new { planned = false, applied = change };
    }

    // Closes the solution. Dirty documents are refused unless saveFirst is set, because SuppressUI would discard them silently.
    private object CloseSolution(bool saveFirst, bool confirm)
    {
        var dirty = new List<string>();
        foreach (Document document in dte.Documents)
        {
            if (!document.Saved)
            {
                dirty.Add(document.FullName);
            }
        }

        var change = new { solution = NullIfEmpty(dte.Solution.FullName), save_first = saveFirst, dirty_documents = dirty };
        if (!confirm)
        {
            return new { planned = true, would = change };
        }

        if (!saveFirst && dirty.Count > 0)
        {
            throw new ArgumentException(Strings.Format("SolutionHasDirtyDocuments", string.Join(", ", dirty)));
        }

        dte.Solution.Close(saveFirst);
        return new { planned = false, applied = change };
    }

    private static ProjectItem? FindProjectItemIn(ProjectItems? items, string file)
    {
        if (items is null)
        {
            return null;
        }

        foreach (ProjectItem item in items)
        {
            if (item.FileCount > 0 && item.FileNames[1] is string path && SameFile(path, file))
            {
                return item;
            }

            var nested = item.SubProject is { } sub ? FindProjectItemIn(sub.ProjectItems, file) : FindProjectItemIn(item.ProjectItems, file);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    // Labels of the info bar buttons: the actions argument is an array of { "label": "..." } objects.
    private static IReadOnlyList<string> InfoBarLabels(JsonElement args)
    {
        var labels = new List<string>();
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("actions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return labels;
        }

        foreach (var action in array.EnumerateArray())
        {
            labels.Add(RequireString(action, "label"));
        }

        return labels;
    }

    private static IReadOnlyList<string> RequireStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(Strings.Format("MissingStringArgument", name));
        }

        var values = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            values.Add(element.GetString() ?? throw new ArgumentException(Strings.Format("MissingStringArgument", name)));
        }

        return values;
    }

    // Read-only view of the Task List window. Adding or clearing tasks needs a registered task provider (unit 4.2, not here).
    private object ListTasks()
    {
        var list = new List<object>();
        foreach (TaskItem task in dte.ToolWindows.TaskList.TaskItems)
        {
            list.Add(new { category = task.Category.ToString(), text = task.Description, file = task.FileName, line = task.Line });
        }

        return new { tasks = list };
    }

    private object ListDebuggedProcesses()
    {
        var debugger = (EnvDTE90a.Debugger4)dte.Debugger;
        var list = new List<object>();
        foreach (EnvDTE.Process process in debugger.DebuggedProcesses)
        {
            list.Add(new { pid = process.ProcessID, name = process.Name });
        }

        return new { processes = list };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
