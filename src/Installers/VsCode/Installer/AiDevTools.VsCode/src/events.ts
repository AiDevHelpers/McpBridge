import * as vscode from "vscode";
import { onDebugAdapterMessage } from "./debugOutputTracker.js";
import { EventsPipe } from "./eventsPipe.js";
import { EventHub } from "./vendor/core/index.js";

// Starts the events pipe for this window and maps VS Code events onto the protocol's event names
// (conformance/protocol.md, "Events pipe"). Everything is disposed with the extension.

/** Starts the events pipe and the VS Code event mapping. Called once from activate(). */
export function startEvents(context: vscode.ExtensionContext): void {
    const hub = new EventHub();
    const pipe = new EventsPipe(hub, process.pid);
    pipe.start();
    context.subscriptions.push(pipe, ...mapEvents(hub));
}

function mapEvents(hub: EventHub): vscode.Disposable[] {
    return [
        vscode.workspace.onDidSaveTextDocument((doc) => hub.publish("documentSaved", { path: doc.uri.fsPath })),
        vscode.workspace.onDidOpenTextDocument((doc) => hub.publish("documentOpened", { path: doc.uri.fsPath })),
        // Approximate: VS Code reports the close after the document has gone, Visual Studio before it.
        vscode.workspace.onDidCloseTextDocument((doc) => hub.publish("documentClosing", { path: doc.uri.fsPath })),
        // Approximate: the active stack item (thread or frame) stands in for Visual Studio's context change.
        vscode.debug.onDidChangeActiveStackItem((item) =>
            hub.publish(
                "contextChanged",
                item ? { sessionId: item.session.id, threadId: item.threadId, frameId: "frameId" in item ? item.frameId : null } : null,
            ),
        ),
        onDebugAdapterMessage((sessionId, message) => {
            if (message.type !== "event") return;
            const body = (message as { body?: { reason?: string; threadId?: number } }).body;
            if (message.event === "stopped") hub.publish("enterBreakMode", { sessionId, reason: body?.reason ?? null, threadId: body?.threadId ?? null });
            if (message.event === "continued") hub.publish("enterRunMode", { sessionId, threadId: body?.threadId ?? null });
        }),
        // Approximate: buildBegin and buildDone cover any task in the Build group, and buildDone fires when the task ends.
        vscode.tasks.onDidStartTask((e) => {
            if (isBuildTask(e.execution.task)) hub.publish("buildBegin", { name: e.execution.task.name });
        }),
        vscode.tasks.onDidEndTask((e) => {
            if (isBuildTask(e.execution.task)) hub.publish("buildDone", { name: e.execution.task.name });
        }),
        // Approximate: VS Code has no solution-open or solution-close event, so added and removed workspace folders stand in.
        vscode.workspace.onDidChangeWorkspaceFolders((e) => {
            if (e.added.length > 0) hub.publish("solutionOpened", { paths: e.added.map((f) => f.uri.fsPath) });
            if (e.removed.length > 0) hub.publish("solutionClosing", { paths: e.removed.map((f) => f.uri.fsPath) });
        }),
    ];
}

function isBuildTask(task: vscode.Task): boolean {
    return task.group?.id === vscode.TaskGroup.Build.id;
}
