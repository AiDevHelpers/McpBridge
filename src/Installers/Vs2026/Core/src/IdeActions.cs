using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using EnvDTE;
using EnvDTE80;

using AiDevHelpers.Bridge;

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

    public object Invoke(string action, JsonElement args)
    {
        // Callers switch to the UI thread first (BridgePackage); this asserts it at runtime.
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

        // Modal prompts (line endings, reload, save) would freeze the UI thread and the bridge with it. Suppressing UI for the
        // duration of one action lets the IDE answer with its defaults instead of waiting. Restored afterwards.
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
        "listDebuggedProcesses" => ListDebuggedProcesses(),
        "listThreads" => ListThreads(),
        "listExceptions" => ListExceptions(),
        "listStackFrames" => ListStackFrames(),
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

            var doc = pane.TextDocument;
            var text = doc.StartPoint.CreateEditPoint().GetText(doc.EndPoint);
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
        var doc = pane.TextDocument;
        var text = doc.StartPoint.CreateEditPoint().GetText(doc.EndPoint);
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
            var found = FindInProject(project, name);
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

    private static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Reads an optional boolean argument. Returns null when absent or not a boolean, so callers choose the default.
    internal static bool? OptionalBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    // Process lists for the debugger. Read-only: nothing is attached or detached here.
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
    private object ListExceptions()
    {
        var list = new List<object>();
        foreach (EnvDTE90.ExceptionSettings group in ((EnvDTE100.Debugger5)dte.Debugger).ExceptionGroups)
        {
            foreach (EnvDTE90.ExceptionSetting setting in group)
            {
                list.Add(new
                {
                    group = group.Name,
                    name = setting.Name,
                    code = setting.Code,
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

    private static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static int RequireInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException(Strings.Format("MissingIntegerArgument", name));
        }

        return value.GetInt32();
    }

    private static string RequireString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(Strings.Format("MissingStringArgument", name));
        }

        return value.GetString()!;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
