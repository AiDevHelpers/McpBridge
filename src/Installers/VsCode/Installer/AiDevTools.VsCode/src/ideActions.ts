import { spawn, type ChildProcess } from "child_process";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import {
    appliedCloseReply,
    appliedCloseSolutionReply,
    appliedSaveReply,
    dirtyPaths,
    planCloseReply,
    planCloseSolutionReply,
    planSaveReply,
    refuseDirtyClose,
    refuseDirtySolution,
    requireOpenDocument,
    sameFile,
    shapeDocumentList,
} from "./documentActions.js";
import { OutputBuffer } from "./vendor/core/index.js";
import { debugOutputStore } from "./debugOutputTracker.js";
import { t } from "./strings.js";
import { isGitAction, runGitAction } from "./gitActions.js";
import { parseHitCountType, setBreakpointCondition, setBreakpointEnabled, type ConditionChange } from "./breakpointActions.js";
import { debuggerMode, isAttached, shapeDetachAll, shapeProcesses, shapeStackFrames, shapeThreads, type SessionInfo } from "./debugInspect.js";
import { liveDebugSessions } from "./debugOutputTracker.js";
import { attachConfiguration, findLocalProcess, listLocalProcesses } from "./processList.js";
import { commitExceptionChange, planExceptionChange, shapeExceptions, type ExceptionChange } from "./exceptionFilters.js";
import { applyReplace, buildSearchPattern, findInFiles, findProjectDirectory, planReplace, walkFiles } from "./textSearch.js";
import { replaceInFile as replaceInFileEdit, type Edit } from "./fileEdit.js";
import {
    addItemChange,
    formatLogLine,
    infoBarLabels,
    isInsideDir,
    isSdkStyle,
    logLevel,
    readProjectProperties,
    removeItemChange,
    settingReply,
} from "./projectShell.js";
import {
    changedFiles,
    dedupeLocations,
    forgetLastEditGroup,
    lastEditGroup,
    recordEditGroup,
    requireCSharpExtension,
    shapeCodeElements,
    shapeLocation,
    type SourceLocation,
    type SymbolNode,
} from "./languageActions.js";

// Common IDE actions for VS Code. The names match the Visual Studio bridge (AiDevHelpers.Installers.Vs2026.Core
// IdeActions.cs), so the MCP caller uses the same calls for either IDE. Actions VS Code's API cannot
// perform throw an "not supported" error instead of guessing, so the caller always knows what happened.
//
// Output and watches are kept by this bridge, because VS Code has no API to read other output channels or
// watch expressions. Output holds the lines from clean and publish runs this bridge started. Watches are
// evaluated in the active debug session on each listWatches call, so their values only show while paused.

type Args = Record<string, unknown>;

// VS Code has no API to read continuous mode. This is the bridge's own record of the last start or stop it ran.
let continuousOn = false;

export class UnsupportedActionError extends Error {
    constructor(action: string) {
        super(t("unsupportedAction", action));
    }
}

const OUTPUT_LIMIT = 5000;
const output = new OutputBuffer(OUTPUT_LIMIT);
const trackedProcesses = new Map<number, ChildProcess>();
const watches: string[] = [];
let selectedConfiguration: string | undefined;
// Named channels written by outputWrite. VS Code cannot read channels back, so each is created once and only appended to.
const namedChannels = new Map<string, vscode.OutputChannel>();
// The activity log channel, created on the first writeActivityLog that is confirmed.
let logChannel: vscode.OutputChannel | undefined;

function writeNamedChannel(name: string, text: string): void {
    let channel = namedChannels.get(name);
    if (channel === undefined) {
        channel = vscode.window.createOutputChannel(name);
        namedChannels.set(name, channel);
    }
    channel.append(text.endsWith("\n") ? text : text + "\n");
}

function requireString(args: Args, key: string): string {
    const value = args[key];
    if (typeof value !== "string" || value.length === 0) {
        throw new Error(t("missingArgument", key));
    }
    return value;
}

function optionalString(args: Args, key: string): string | undefined {
    const value = args[key];
    return typeof value === "string" && value.length > 0 ? value : undefined;
}

function optionalInt(args: Args, key: string): number | undefined {
    const value = args[key];
    return typeof value === "number" ? value : undefined;
}

function optionalBool(args: Args, key: string): boolean | undefined {
    const value = args[key];
    return typeof value === "boolean" ? value : undefined;
}

function requireInt(args: Args, key: string): number {
    const value = optionalInt(args, key);
    if (value === undefined) {
        throw new Error(t("missingArgument", key));
    }
    return value;
}

function resolveFile(file: string): vscode.Uri {
    if (path.isAbsolute(file)) {
        return vscode.Uri.file(file);
    }
    const root = vscode.workspace.workspaceFolders?.[0]?.uri;
    if (!root) {
        throw new Error(t("relativePathNeedsWorkspace", file));
    }
    return vscode.Uri.joinPath(root, file);
}

function launchConfigurationNames(): string[] {
    const configs = vscode.workspace.getConfiguration("launch").get<Array<{ name?: string }>>("configurations") ?? [];
    return configs.map((c) => c.name ?? "").filter((n) => n.length > 0);
}

function workspaceRoot(): string {
    const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!root) {
        throw new Error(t("needsWorkspace"));
    }
    return root;
}

// The files a findInFiles or replaceInFiles scope names. The Visual Studio "solution" is the workspace here, and
// "project" is the folder holding the named .csproj. "openDocuments" is the open documents that exist on disk.
// The scope names are compared with if, not switch: the docs test reads every "case" label in this file as an action.
function searchScopeFiles(scope: string, project: string | undefined): string[] {
    if (scope === "solution") {
        const roots = vscode.workspace.workspaceFolders?.map((f) => f.uri.fsPath) ?? [];
        if (roots.length === 0) {
            throw new Error(t("needsWorkspace"));
        }
        return walkFiles(roots);
    }
    if (scope === "project") {
        if (!project) {
            throw new Error(t("projectScopeNeedsName"));
        }
        const roots = vscode.workspace.workspaceFolders?.map((f) => f.uri.fsPath) ?? [];
        const dir = findProjectDirectory(roots, project);
        if (dir === undefined) {
            throw new Error(t("noProjectNamed", project));
        }
        return walkFiles([dir]);
    }
    if (scope === "openDocuments") {
        return vscode.workspace.textDocuments
            .filter((d) => d.uri.scheme === "file" && fs.existsSync(d.uri.fsPath))
            .map((d) => d.uri.fsPath);
    }
    throw new Error(t("unknownScope", scope));
}

// Session facts the inspection actions need. request "attach" marks a session the debugger attached to.
function sessionInfo(session: vscode.DebugSession): SessionInfo {
    const processId = session.configuration.processId;
    return { name: session.name, request: String(session.configuration.request ?? ""), processId: typeof processId === "number" ? processId : undefined };
}

// Debugger mode from the active session and its stopped stack item, named as the Visual Studio bridge names it.
function currentDebuggerMode(): ReturnType<typeof debuggerMode> {
    return debuggerMode(vscode.debug.activeDebugSession !== undefined, vscode.debug.activeStackItem !== undefined);
}

function currentThreadId(): number | undefined {
    const item = vscode.debug.activeStackItem;
    return item && "threadId" in item ? item.threadId : undefined;
}

function appendOutput(text: string): void {
    output.append(text);
}

// Runs `dotnet clean` or `dotnet publish` in the workspace folder. Output goes to the bridge's output buffer.
function runDotnet(verb: "clean" | "publish", project: string | undefined): Promise<unknown> {
    const cwd = workspaceRoot();
    const args = project ? [verb, project] : [verb];
    const command = `dotnet ${args.join(" ")}`;
    appendOutput(`> ${command}\n`);
    return new Promise((resolve, reject) => {
        const child = spawn("dotnet", args, { cwd, windowsHide: true });
        const pid = child.pid;
        if (pid === undefined) {
            reject(new Error(t("dotnetDidNotStart")));
            return;
        }
        trackedProcesses.set(pid, child);
        child.stdout?.on("data", (data: Buffer) => appendOutput(data.toString("utf8")));
        child.stderr?.on("data", (data: Buffer) => appendOutput(data.toString("utf8")));
        child.on("error", (err) => {
            trackedProcesses.delete(pid);
            reject(err);
        });
        child.on("close", (code) => {
            trackedProcesses.delete(pid);
            resolve({ ok: code === 0, exit_code: code, command, cwd, pid });
        });
    });
}

// Kills a process tree this bridge started. taskkill /T takes the children that dotnet spawns too.
function killTracked(pid: number): Promise<unknown> {
    if (!trackedProcesses.has(pid)) {
        throw new Error(t("processNotStarted", pid));
    }
    return new Promise((resolve, reject) => {
        const killer = spawn("taskkill", ["/T", "/F", "/PID", String(pid)], { windowsHide: true });
        killer.on("error", reject);
        killer.on("close", (code) => {
            trackedProcesses.delete(pid);
            resolve({ killed: code === 0, pid, exit_code: code });
        });
    });
}

// VS Code's API has no read-only flag on a document. The closest reading is the file's write permission on disk.
function isReadOnlyOnDisk(uri: vscode.Uri): boolean {
    if (uri.scheme !== "file" || !fs.existsSync(uri.fsPath)) {
        return false;
    }
    try {
        fs.accessSync(uri.fsPath, fs.constants.W_OK);
        return false;
    } catch {
        return true;
    }
}

// Closes the editor tabs showing a file. A document with no tab is not on screen, so there is no tab to close.
async function closeTabsFor(fsPath: string): Promise<void> {
    const tabs = vscode.window.tabGroups.all
        .flatMap((group) => group.tabs)
        .filter((tab) => tab.input instanceof vscode.TabInputText && sameFile(tab.input.uri.fsPath, fsPath));
    for (const tab of tabs) {
        await vscode.window.tabGroups.close(tab);
    }
}

async function evaluateWatches(): Promise<Array<Record<string, unknown>>> {
    const session = vscode.debug.activeDebugSession;
    const frame = vscode.debug.activeStackItem;
    const frameId = frame && "frameId" in frame ? frame.frameId : undefined;
    const results: Array<Record<string, unknown>> = [];
    for (const expression of watches) {
        if (!session) {
            results.push({ expression, error: "No active debug session." });
            continue;
        }
        try {
            const reply = await session.customRequest("evaluate", { expression, frameId, context: "watch" });
            results.push({ expression, value: reply.result, type: reply.type ?? null });
        } catch (err) {
            results.push({ expression, error: err instanceof Error ? err.message : String(err) });
        }
    }
    return results;
}

// Language actions. The C# Dev Kit or the C# extension provides the symbols, so one of them must be installed; without
// it these throw rather than return empty results. Lines and columns are 1-based, as in the Visual Studio bridge.
function requireCSharp(): void {
    requireCSharpExtension((id) => vscode.extensions.getExtension(id));
}

function languagePosition(args: Args): { uri: vscode.Uri; position: vscode.Position; line: number; column: number } {
    const uri = resolveFile(requireString(args, "file"));
    const line = requireInt(args, "line");
    const column = optionalInt(args, "column") ?? 1;
    return { uri, position: new vscode.Position(Math.max(0, line - 1), Math.max(0, column - 1)), line, column };
}

// The name of the word under the caret, or null when there is no word there.
async function symbolNameAt(uri: vscode.Uri, position: vscode.Position): Promise<string | null> {
    const doc = await vscode.workspace.openTextDocument(uri);
    const range = doc.getWordRangeAtPosition(position);
    return range ? doc.getText(range) : null;
}

function requireSymbol(name: string | null, uri: vscode.Uri, position: vscode.Position): string {
    if (name === null) {
        throw new Error(t("noSymbolAtPosition", uri.fsPath, position.line + 1, position.character + 1));
    }
    return name;
}

function toSourceLocations(items: ReadonlyArray<vscode.Location | vscode.LocationLink>): SourceLocation[] {
    return items.map((item) => {
        const target = "targetUri" in item ? { uri: item.targetUri, range: item.targetRange } : { uri: item.uri, range: item.range };
        return shapeLocation(target.uri.fsPath, target.range.start.line, target.range.start.character);
    });
}

async function listCodeElements(args: Args): Promise<unknown> {
    requireCSharp();
    const uri = resolveFile(requireString(args, "file"));
    const symbols = await vscode.commands.executeCommand<Array<vscode.DocumentSymbol | vscode.SymbolInformation> | undefined>(
        "vscode.executeDocumentSymbolProvider",
        uri,
    );
    // Only DocumentSymbol carries a range; a SymbolInformation list (no hierarchy) is skipped.
    const documentSymbols = (symbols ?? []).filter((s): s is vscode.DocumentSymbol => "range" in s);
    return { elements: shapeCodeElements(documentSymbols as unknown as SymbolNode[]) };
}

async function semanticLocations(action: "semanticDefinition" | "semanticReferences", args: Args): Promise<unknown> {
    requireCSharp();
    const { uri, position } = languagePosition(args);
    const symbol = requireSymbol(await symbolNameAt(uri, position), uri, position);
    if (action === "semanticDefinition") {
        const links = await vscode.commands.executeCommand<Array<vscode.Location | vscode.LocationLink> | undefined>(
            "vscode.executeDefinitionProvider",
            uri,
            position,
        );
        return { symbol, locations: toSourceLocations(links ?? []) };
    }
    // The reference command takes no includeDeclaration argument, so the declaration is reported when the provider includes it.
    const refs = await vscode.commands.executeCommand<Array<vscode.Location | vscode.LocationLink> | undefined>(
        "vscode.executeReferenceProvider",
        uri,
        position,
    );
    return { symbol, locations: dedupeLocations(toSourceLocations(refs ?? [])) };
}

async function renameSymbolAt(args: Args, confirm: boolean): Promise<unknown> {
    requireCSharp();
    const { uri, position, line, column } = languagePosition(args);
    const newName = requireString(args, "newName");
    const name = await symbolNameAt(uri, position);
    const edit = await vscode.commands.executeCommand<vscode.WorkspaceEdit | undefined>(
        "vscode.executeDocumentRenameProvider",
        uri,
        position,
        newName,
    );
    if (name === null || edit === undefined) {
        throw new Error(t("noSymbolAtPosition", uri.fsPath, line, column));
    }
    const files = changedFiles(edit.entries().map(([file]) => file));
    const change = { symbol: name, new_name: newName, files };
    if (!confirm) {
        return { planned: true, would: change };
    }
    if (!(await vscode.workspace.applyEdit(edit))) {
        throw new Error(t("renameNotApplied"));
    }
    recordEditGroup({ name: "renameSymbol", files });
    return { planned: false, applied: change };
}

// VS Code's undo is one global stack with no names, so this reverses the last edit group this bridge applied, not a group
// picked by name. The note in the reply says so.
async function undoLastGroup(name: string, confirm: boolean): Promise<unknown> {
    const group = lastEditGroup();
    if (group === undefined) {
        throw new Error(t("noEditGroupToUndo"));
    }
    const reply = { name, undone: group.name, files: group.files, note: t("undoGroupApproximate") };
    if (!confirm) {
        return { planned: true, would: reply };
    }
    await vscode.commands.executeCommand("undo");
    forgetLastEditGroup();
    return { planned: false, applied: reply };
}

function requireStringArray(args: Args, key: string): string[] {
    const value = args[key];
    if (!Array.isArray(value) || !value.every((item) => typeof item === "string")) {
        throw new Error(t("missingArgument", key));
    }
    return value as string[];
}

// The .csproj a project argument names: a path ending in .csproj, or a project name found in the workspace.
function resolveProjectFile(project: string): string {
    if (project.toLowerCase().endsWith(".csproj")) {
        const file = resolveFile(project).fsPath;
        if (!fs.existsSync(file)) {
            throw new Error(t("noProjectNamed", project));
        }
        return file;
    }
    const roots = vscode.workspace.workspaceFolders?.map((f) => f.uri.fsPath) ?? [];
    if (roots.length === 0) {
        throw new Error(t("needsWorkspace"));
    }
    const wanted = `${project}.csproj`.toLowerCase();
    const found = walkFiles(roots).find((file) => path.basename(file).toLowerCase() === wanted);
    if (found === undefined) {
        throw new Error(t("noProjectNamed", project));
    }
    return found;
}

// A file argument as a path: absolute as given, otherwise relative to the project folder.
function projectItemPath(projectFile: string, file: string): string {
    return path.isAbsolute(file) ? file : path.join(path.dirname(projectFile), file);
}

// SDK-style projects include files by globbing, so a fromFile add needs no csproj edit: the file just has to be in the
// project folder. fromNew copies the template to the file. Legacy (non-SDK) projects would need a csproj edit, which is refused.
function addProjectItem(args: Args, confirm: boolean): unknown {
    const project = requireString(args, "project");
    const file = requireString(args, "file");
    const change = addItemChange(project, file, requireString(args, "mode"), optionalString(args, "template"));
    const projectFile = resolveProjectFile(project);
    if (!confirm) {
        return { planned: true, would: change };
    }
    if (!isSdkStyle(fs.readFileSync(projectFile, "utf8"))) {
        throw new Error(t("addItemNotApplied", file));
    }
    const target = projectItemPath(projectFile, file);
    if (!isInsideDir(path.dirname(projectFile), target)) {
        throw new Error(t("itemOutsideProjectFolder", file));
    }
    if (change.mode === "fromFile") {
        if (!fs.existsSync(target)) {
            throw new Error(t("fileNotFound", target));
        }
        return { planned: false, applied: change, note: t("sdkGlobNote") };
    }
    const templatePath = resolveFile(change.template ?? "").fsPath;
    if (!fs.existsSync(templatePath)) {
        throw new Error(t("templateNotFound", templatePath));
    }
    if (fs.existsSync(target)) {
        throw new Error(t("fileAlreadyExists", target));
    }
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(templatePath, target);
    return { planned: false, applied: change, note: t("sdkGlobNote") };
}

// Matches the C# version for the item: it is removed from the project and the file stays on disk. For an SDK-style
// project that removal means excluding the file in the csproj, which this bridge does not edit, so a confirmed
// remove changes nothing and throws. The plan is still returned without confirm.
function removeProjectItem(args: Args, confirm: boolean): unknown {
    const project = requireString(args, "project");
    const file = requireString(args, "file");
    const projectFile = resolveProjectFile(project);
    if (!isInsideDir(path.dirname(projectFile), projectItemPath(projectFile, file))) {
        throw new Error(t("noProjectItemFor", file));
    }
    const change = removeItemChange(project, file);
    if (!confirm) {
        return { planned: true, would: change };
    }
    throw new Error(t("removeItemNotApplied", file));
}

export async function runIdeAction(action: string, args: Args): Promise<unknown> {
    switch (action) {
        case "status":
            return {
                workspace_folders: vscode.workspace.workspaceFolders?.map((f) => f.uri.fsPath) ?? [],
                active_file: vscode.window.activeTextEditor?.document.uri.fsPath ?? null,
                debug_session: vscode.debug.activeDebugSession?.name ?? null,
            };

        case "startDebugging": {
            const folder = vscode.workspace.workspaceFolders?.[0];
            const name = optionalString(args, "configuration") ?? selectedConfiguration ?? launchConfigurationNames()[0];
            if (!name) {
                throw new Error(t("noLaunchConfig"));
            }
            const started = await vscode.debug.startDebugging(folder, name);
            return { started, configuration: name };
        }

        case "stopDebugging":
            await vscode.commands.executeCommand("workbench.action.debug.stop");
            return { stopped: true };

        case "runTests":
            await vscode.commands.executeCommand("testing.runAll");
            return { command: "testing.runAll" };

        case "formatDocument":
            if (!vscode.window.activeTextEditor) {
                throw new Error(t("noActiveEditor"));
            }
            await vscode.commands.executeCommand("editor.action.formatDocument");
            return { formatted: true };

        case "build":
            await vscode.commands.executeCommand("workbench.action.tasks.build");
            return { command: "workbench.action.tasks.build" };

        case "clean":
            return runDotnet("clean", optionalString(args, "project"));

        case "publish":
            return runDotnet("publish", optionalString(args, "project"));

        case "getOutput": {
            const maxLines = optionalInt(args, "maxLines") ?? 200;
            const fromSeq = optionalInt(args, "fromSeq");
            if (fromSeq === undefined) {
                const tail = output.tail(maxLines);
                return { count: tail.length, lines: tail };
            }
            const page = output.read(fromSeq, maxLines);
            return { count: page.lines.length, lines: page.lines, nextSeq: page.nextSeq, dropped: page.dropped };
        }

        case "searchOutput": {
            const pattern = new RegExp(requireString(args, "pattern"));
            const lines = output.read(1, Number.MAX_SAFE_INTEGER).lines.filter((line) => pattern.test(line));
            return { count: lines.length, lines };
        }

        case "clearOutput":
            output.clear();
            return { cleared: true };

        case "outputWrite": {
            const channel = requireString(args, "channel");
            const text = requireString(args, "text");
            writeNamedChannel(channel, text);
            return { channel, written: text.length };
        }

        case "listDebugSessions":
            return {
                sessions: debugOutputStore.sessionIds().map((id) => ({ id, ended: debugOutputStore.isEnded(id) })),
                endedRuns: debugOutputStore.endedSessions(),
            };

        case "getDebugOutput": {
            const session = requireString(args, "session");
            const fromSeq = optionalInt(args, "fromSeq") ?? 1;
            const maxLines = optionalInt(args, "maxLines") ?? 200;
            const buffer = debugOutputStore.output(session);
            if (buffer === undefined) {
                return { count: 0, lines: [], nextSeq: fromSeq, dropped: 0, ended: debugOutputStore.isEnded(session) };
            }
            const page = buffer.read(fromSeq, maxLines);
            return {
                count: page.lines.length,
                lines: page.lines,
                nextSeq: page.nextSeq,
                dropped: page.dropped,
                ended: debugOutputStore.isEnded(session),
            };
        }

        case "getBuildDiagnostics": {
            const severity = typeof args.severity === "string" ? args.severity : "all";
            const wanted = severity === "error" ? [vscode.DiagnosticSeverity.Error]
                : severity === "warning" ? [vscode.DiagnosticSeverity.Warning]
                : [vscode.DiagnosticSeverity.Error, vscode.DiagnosticSeverity.Warning];
            const items: Array<{ file: string; line: number; severity: string; message: string }> = [];
            for (const [uri, diagnostics] of vscode.languages.getDiagnostics()) {
                for (const d of diagnostics) {
                    if (!wanted.includes(d.severity)) continue;
                    items.push({
                        file: uri.fsPath,
                        line: d.range.start.line + 1,
                        severity: vscode.DiagnosticSeverity[d.severity],
                        message: d.message,
                    });
                }
            }
            return { count: items.length, items };
        }

        case "codeCleanup":
            await vscode.commands.executeCommand("editor.action.sourceAction", { kind: "source.fixAll", apply: "first" });
            return { command: "editor.action.sourceAction", kind: "source.fixAll" };

        case "openSolution": {
            const solutionPath = requireString(args, "solutionPath");
            const folder = path.dirname(solutionPath);
            await vscode.commands.executeCommand("vscode.openFolder", vscode.Uri.file(folder), false);
            return { folder, opened: true, note: "VS Code opens the solution folder; .sln files are not opened." };
        }

        case "continuousTestingStart":
            await vscode.commands.executeCommand("testing.startContinuousRun");
            continuousOn = true;
            return { ide: "VsCode", continuous: true, command: "testing.startContinuousRun" };

        case "continuousTestingStop":
            await vscode.commands.executeCommand("testing.stopContinuousRun");
            continuousOn = false;
            return { ide: "VsCode", continuous: false, command: "testing.stopContinuousRun" };

        case "continuousTestingState":
            return { ide: "VsCode", tracked: continuousOn, note: "Bridge-tracked: set by continuousTestingStart and continuousTestingStop only." };

        case "hotReloadState": {
            // C# Dev Kit: csharp.experimental.debug.hotReload (Microsoft, Feb 2025). Null when the setting is not present.
            const value = vscode.workspace.getConfiguration("csharp.experimental.debug").get<boolean>("hotReload");
            return { ide: "VsCode", enabled: value ?? null, setting: "csharp.experimental.debug.hotReload" };
        }

        case "listCodeElements":
            return listCodeElements(args);

        case "semanticDefinition":
            return semanticLocations("semanticDefinition", args);

        case "semanticReferences":
            return semanticLocations("semanticReferences", args);

        case "renameSymbol":
            return renameSymbolAt(args, optionalBool(args, "confirm") === true);

        case "undoGroup":
            return undoLastGroup(requireString(args, "name"), optionalBool(args, "confirm") === true);

        case "goToDefinition":
        case "findAllReferences": {
            const uri = resolveFile(requireString(args, "file"));
            const line = requireInt(args, "line");
            const column = optionalInt(args, "column") ?? 1;
            const doc = await vscode.workspace.openTextDocument(uri);
            const editor = await vscode.window.showTextDocument(doc);
            const pos = new vscode.Position(Math.max(0, line - 1), Math.max(0, column - 1));
            editor.selection = new vscode.Selection(pos, pos);
            editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
            const command = action === "goToDefinition" ? "editor.action.revealDefinition" : "references-view.findReferences";
            await vscode.commands.executeCommand(command);
            return { file: uri.fsPath, line, column, command, caret_line: editor.selection.active.line + 1, caret_column: editor.selection.active.character + 1, active_file: vscode.window.activeTextEditor?.document.uri.fsPath ?? null };
        }

        case "formatSolution": {
            // C# files under the workspace, like the Visual Studio action. Each file is opened, formatted and saved.
            const files = await vscode.workspace.findFiles("**/*.cs", "**/{bin,obj,.git,node_modules,TestResults}/**");
            const formatted: string[] = [];
            for (const uri of files) {
                const doc = await vscode.workspace.openTextDocument(uri);
                const editor = await vscode.window.showTextDocument(doc);
                await vscode.commands.executeCommand("editor.action.formatDocument");
                await doc.save();
                formatted.push(editor.document.uri.fsPath);
            }
            return { formatted: formatted.length, files: formatted };
        }

        case "openDocument": {
            const uri = resolveFile(requireString(args, "file"));
            try {
                await vscode.workspace.fs.stat(uri);
            } catch {
                throw new Error(t("fileNotFound", uri.fsPath));
            }
            const line = optionalInt(args, "line");
            const doc = await vscode.workspace.openTextDocument(uri);
            const editor = await vscode.window.showTextDocument(doc);
            if (line !== undefined) {
                const pos = new vscode.Position(Math.max(0, line - 1), 0);
                editor.selection = new vscode.Selection(pos, pos);
                editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
            }
            return { file: uri.fsPath, line: line ?? null, opened: true };
        }

        case "findInFiles": {
            // Read-only. The Visual Studio names are pattern, matchCase, useRegex, scope and project.
            const regex = buildSearchPattern(requireString(args, "pattern"), optionalBool(args, "matchCase") === true, optionalBool(args, "useRegex") === true);
            return findInFiles(searchScopeFiles(optionalString(args, "scope") ?? "solution", optionalString(args, "project")), regex);
        }

        case "replaceInFile": {
            // One file, through the same rules as FileEditHelper (backup, BOM, atomic write). Without confirm it only plans.
            const file = requireString(args, "file");
            const edits = args.edits;
            if (!Array.isArray(edits) || edits.length === 0) {
                throw new Error(t("missingArgument", "edits"));
            }
            const root = optionalString(args, "root") ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
            if (!root) {
                throw new Error(t("needsWorkspace"));
            }
            return replaceInFileEdit(root, file, edits as Edit[], optionalBool(args, "confirm") === true);
        }
        case "replaceInFiles": {
            // Without confirm, only the per-file match counts are returned. An empty replacement is valid, so only its type is checked.
            const pattern = requireString(args, "pattern");
            if (typeof args.replacement !== "string") {
                throw new Error(t("missingArgument", "replacement"));
            }
            const useRegex = optionalBool(args, "useRegex") === true;
            const regex = buildSearchPattern(pattern, optionalBool(args, "matchCase") === true, useRegex);
            const files = searchScopeFiles(optionalString(args, "scope") ?? "solution", optionalString(args, "project"));
            const dirty = vscode.workspace.textDocuments.filter((d) => d.isDirty).map((d) => d.uri.fsPath);
            const plan = planReplace(files, regex, dirty);
            const skipped = plan.skipped.map((file) => ({ file, reason: t("openWithUnsavedChanges") }));
            if (optionalBool(args, "confirm") !== true) {
                return { planned: true, would: { files: plan.planned, skipped } };
            }
            const result = applyReplace(plan.planned, regex, args.replacement, useRegex);
            return { planned: false, applied: { files: result.applied, skipped }, failed: result.failed };
        }

        case "projectProperties": {
            // Read-only. An unknown property name gives null, as in the Visual Studio version.
            const project = requireString(args, "project");
            const projectFile = resolveProjectFile(project);
            return { project, values: readProjectProperties(fs.readFileSync(projectFile, "utf8"), requireStringArray(args, "names")) };
        }

        case "addProjectItem":
            return addProjectItem(args, optionalBool(args, "confirm") === true);

        case "removeProjectItem":
            return removeProjectItem(args, optionalBool(args, "confirm") === true);

        case "getSetting":
            // Read-only. The key is a dotted VS Code setting name, such as editor.tabSize.
            return settingReply(requireString(args, "key"), vscode.workspace.getConfiguration().get(requireString(args, "key")));

        case "setStatus": {
            const text = optionalString(args, "text") ?? "";
            const change = { text };
            if (optionalBool(args, "confirm") !== true) {
                return { planned: true, would: change };
            }
            // VS Code clears a status bar message after a timeout; the Visual Studio status bar text stays until replaced.
            vscode.window.setStatusBarMessage(text);
            return { planned: false, applied: change };
        }

        case "showInfoBar": {
            const text = requireString(args, "text");
            const actions = infoBarLabels(args.actions);
            const change = { text, actions };
            if (optionalBool(args, "confirm") !== true) {
                return { planned: true, would: change };
            }
            // The button choice is not awaited: the notification waits for the user, and the Visual Studio version does not return a choice either.
            void vscode.window.showInformationMessage(text, ...actions);
            return { planned: false, applied: change };
        }

        case "writeActivityLog": {
            const level = logLevel(requireString(args, "level"));
            const source = optionalString(args, "source") ?? "AiDevHelpers";
            const message = requireString(args, "message");
            const change = { level, source, message };
            if (optionalBool(args, "confirm") !== true) {
                return { planned: true, would: change };
            }
            logChannel ??= vscode.window.createOutputChannel("AiDevHelpers");
            logChannel.appendLine(formatLogLine(new Date(), level, source, message));
            return { planned: false, applied: change, written: true };
        }

        case "listDocuments":
            return shapeDocumentList(
                vscode.workspace.textDocuments.map((d) => ({ fullName: d.uri.fsPath, saved: !d.isDirty, readOnly: isReadOnlyOnDisk(d.uri) })),
            );

        case "saveDocument": {
            const file = requireString(args, "file");
            const document = requireOpenDocument(vscode.workspace.textDocuments, resolveFile(file).fsPath, file);
            if (optionalBool(args, "confirm") !== true) {
                return planSaveReply(document.uri.fsPath, document.isDirty);
            }
            if (!(await document.save())) {
                throw new Error(t("saveFailed", document.uri.fsPath));
            }
            return appliedSaveReply(document.uri.fsPath);
        }

        case "closeDocument": {
            const file = requireString(args, "file");
            const discard = optionalBool(args, "discardChanges") === true;
            const document = requireOpenDocument(vscode.workspace.textDocuments, resolveFile(file).fsPath, file);
            refuseDirtyClose(file, document.isDirty, discard);
            if (optionalBool(args, "confirm") !== true) {
                return planCloseReply(document.uri.fsPath, document.isDirty, discard);
            }
            if (document.isDirty) {
                // The extension API has no revert on a document, so the file is shown and the active-editor revert runs. save is never called.
                await vscode.window.showTextDocument(document);
                await vscode.commands.executeCommand("workbench.action.files.revert");
            }
            await closeTabsFor(document.uri.fsPath);
            return appliedCloseReply(document.uri.fsPath);
        }

        case "closeSolution": {
            const saveFirst = optionalBool(args, "saveFirst") === true;
            const dirty = dirtyPaths(vscode.workspace.textDocuments);
            if (optionalBool(args, "confirm") !== true) {
                return planCloseSolutionReply(dirty, saveFirst);
            }
            refuseDirtySolution(dirty, saveFirst);
            const savedDocuments: string[] = [];
            if (saveFirst) {
                for (const document of vscode.workspace.textDocuments.filter((d) => d.isDirty)) {
                    if (!(await document.save())) {
                        throw new Error(t("saveFailed", document.uri.fsPath));
                    }
                    savedDocuments.push(document.uri.fsPath);
                }
            }
            // VS Code has no solution; the closest equivalent is closing the workspace folder.
            await vscode.commands.executeCommand("workbench.action.closeFolder");
            return appliedCloseSolutionReply(savedDocuments);
        }

        case "setBreakpoint": {
            const uri = resolveFile(requireString(args, "file"));
            const line = optionalInt(args, "line");
            if (line === undefined) throw new Error(t("missingArgument", "line"));
            vscode.debug.addBreakpoints([new vscode.SourceBreakpoint(new vscode.Location(uri, new vscode.Position(line - 1, 0)))]);
            return { file: uri.fsPath, line };
        }

        case "removeBreakpoint": {
            const uri = resolveFile(requireString(args, "file"));
            const line = optionalInt(args, "line");
            const matches = vscode.debug.breakpoints.filter(
                (b): b is vscode.SourceBreakpoint =>
                    b instanceof vscode.SourceBreakpoint &&
                    b.location.uri.fsPath === uri.fsPath &&
                    (line === undefined || b.location.range.start.line + 1 === line),
            );
            vscode.debug.removeBreakpoints(matches);
            return { removed: matches.length };
        }

        case "listBreakpoints":
            return {
                breakpoints: vscode.debug.breakpoints
                    .filter((b): b is vscode.SourceBreakpoint => b instanceof vscode.SourceBreakpoint)
                    .map((b) => ({
                        file: b.location.uri.fsPath,
                        line: b.location.range.start.line + 1,
                        enabled: b.enabled,
                        condition: b.condition ?? null,
                    })),
            };

        case "enableBreakpoint":
        case "disableBreakpoint": {
            const uri = resolveFile(requireString(args, "file"));
            const line = requireInt(args, "line");
            return setBreakpointEnabled(uri.fsPath, line, action === "enableBreakpoint", args.confirm === true);
        }

        case "setBreakpointCondition": {
            const uri = resolveFile(requireString(args, "file"));
            const line = requireInt(args, "line");
            // An empty condition clears it, so it is kept as "" rather than treated as absent.
            const change: ConditionChange = {
                condition: typeof args.condition === "string" ? args.condition : undefined,
                hitCountType: parseHitCountType(optionalString(args, "hitCountType")),
                hitCount: optionalInt(args, "hitCount"),
            };
            return setBreakpointCondition(uri.fsPath, line, change, args.confirm === true);
        }

        case "listDebuggedProcesses":
            return shapeProcesses(liveDebugSessions().map(sessionInfo));

        case "listThreads": {
            const mode = currentDebuggerMode();
            // Outside break mode the list is not read at all; shapeThreads returns the empty reply.
            const reply = mode === "dbgBreakMode" && vscode.debug.activeDebugSession
                ? await vscode.debug.activeDebugSession.customRequest("threads", {})
                : undefined;
            return shapeThreads(mode, currentThreadId(), reply);
        }

        case "listStackFrames": {
            const mode = currentDebuggerMode();
            const threadId = currentThreadId();
            const reply = mode === "dbgBreakMode" && vscode.debug.activeDebugSession && threadId !== undefined
                ? await vscode.debug.activeDebugSession.customRequest("stackTrace", { threadId })
                : undefined;
            return shapeStackFrames(mode, reply);
        }

        case "listProcesses":
            return { processes: await listLocalProcesses() };

        case "attachProcess": {
            // The pid must be in the local process list, with or without confirm. Only confirm starts the attach.
            const pid = requireInt(args, "pid");
            const target = findLocalProcess(await listLocalProcesses(), pid);
            if (args.confirm !== true) {
                return { planned: true, would: { pid, name: target.name } };
            }
            const started = await vscode.debug.startDebugging(undefined, attachConfiguration(pid, target.name));
            if (!started) {
                throw new Error(t("attachDidNotStart", pid));
            }
            return { planned: false, applied: { pid, name: target.name } };
        }

        case "listExceptions":
            return shapeExceptions(vscode.debug.activeDebugSession?.id, optionalString(args, "group"));

        case "setExceptionBreak": {
            const session = vscode.debug.activeDebugSession;
            if (!session) throw new Error(t("noExceptionFilters"));
            const change: ExceptionChange = {
                group: optionalString(args, "group"),
                name: requireString(args, "name"),
                breakWhenThrown: optionalBool(args, "breakWhenThrown"),
                breakWhenUserUnhandled: optionalBool(args, "breakWhenUserUnhandled"),
            };
            const plan = planExceptionChange(session.id, change);
            // Reduced model: partial is true in both replies, and note says what the bridge does not cover.
            const partial = { partial: true, note: t("exceptionPartialNote") };
            if (args.confirm !== true) {
                return { planned: true, would: plan.reply, ...partial };
            }
            await session.customRequest("setExceptionBreakpoints", { filters: plan.filters });
            commitExceptionChange(session.id, plan.enabledAfter);
            return { planned: false, applied: plan.reply, ...partial };
        }

        case "detachAll": {
            // Only attached sessions are detached; sessions the debugger launched are left running.
            const sessions = liveDebugSessions();
            const attached = sessions.filter((s) => isAttached(sessionInfo(s)));
            const reply = shapeDetachAll(args.confirm === true, attached.map(sessionInfo));
            if (args.confirm !== true) {
                return reply;
            }
            for (const session of attached) {
                await vscode.debug.stopDebugging(session);
            }
            return reply;
        }

        case "step": {
            // The MCP ide_step tool sends "mode" (as the Visual Studio bridge expects); older callers sent "kind".
            const kind = optionalString(args, "kind") ?? requireString(args, "mode");
            const commands: Record<string, string> = {
                over: "workbench.action.debug.stepOver",
                into: "workbench.action.debug.stepInto",
                out: "workbench.action.debug.stepOut",
                continue: "workbench.action.debug.continue",
            };
            const command = commands[kind];
            if (!command) throw new Error(t("unknownStepKind", kind));
            await vscode.commands.executeCommand(command);
            return { step: kind };
        }

        case "debugState": {
            const session = vscode.debug.activeDebugSession;
            const frame = vscode.debug.activeStackItem;
            return {
                mode: session ? "debugging" : "none",
                session: session?.name ?? null,
                thread_id: frame && "threadId" in frame ? frame.threadId : null,
            };
        }

        case "evaluate": {
            const session = vscode.debug.activeDebugSession;
            if (!session) throw new Error(t("noDebugSession"));
            const expression = requireString(args, "expression");
            const frame = vscode.debug.activeStackItem;
            const frameId = frame && "frameId" in frame ? frame.frameId : undefined;
            const reply = await session.customRequest("evaluate", { expression, frameId, context: "repl" });
            return { expression, result: reply.result, type: reply.type ?? null };
        }

        case "addWatch": {
            const expression = requireString(args, "expression");
            if (!watches.includes(expression)) {
                watches.push(expression);
            }
            return { added: expression, count: watches.length };
        }

        case "removeWatch": {
            const expression = requireString(args, "expression");
            const index = watches.indexOf(expression);
            if (index >= 0) {
                watches.splice(index, 1);
            }
            return { removed: index >= 0, count: watches.length };
        }

        case "listWatches":
            return { count: watches.length, watches: await evaluateWatches() };

        case "getLocals": {
            const session = vscode.debug.activeDebugSession;
            const frame = vscode.debug.activeStackItem;
            if (!session || !frame || !("frameId" in frame)) {
                throw new Error(t("notStopped"));
            }
            const scopes = (await session.customRequest("scopes", { frameId: frame.frameId })).scopes as Array<{ name: string; variablesReference: number }>;
            const locals: Array<{ scope: string; name: string; value: string; type: string | null }> = [];
            for (const scope of scopes) {
                const vars = (await session.customRequest("variables", { variablesReference: scope.variablesReference })).variables as Array<{ name: string; value: string; type?: string }>;
                for (const v of vars) {
                    locals.push({ scope: scope.name, name: v.name, value: v.value, type: v.type ?? null });
                }
            }
            return { count: locals.length, locals };
        }

        case "listConfigurations":
            return { configurations: launchConfigurationNames(), selected: selectedConfiguration ?? null };

        case "setConfiguration": {
            const configuration = requireString(args, "configuration");
            if (!launchConfigurationNames().includes(configuration)) {
                throw new Error(t("noLaunchConfigNamed", configuration));
            }
            selectedConfiguration = configuration;
            return { configuration };
        }

        case "killProcess": {
            const pid = optionalInt(args, "pid");
            if (pid === undefined) throw new Error(t("missingArgument", "pid"));
            return killTracked(pid);
        }

        case "health":
            return { ide: "VsCode" };

        // Bookmarks are a Visual Studio feature in v1 (D8). VS Code has no bookmark API in the extension host.
        case "setStartupProject":
        case "prepareKill":
        case "bookmarkList":
        case "bookmarkGet":
        case "bookmarkSet":
        case "bookmarkRemove":
            throw new UnsupportedActionError(action);

        default:
            if (isGitAction(action)) {
                return runGitAction(action, args);
            }
            throw new Error(t("unknownAction", action));
    }
}
