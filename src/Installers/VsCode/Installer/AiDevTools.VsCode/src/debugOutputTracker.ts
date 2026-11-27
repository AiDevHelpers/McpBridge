import * as vscode from "vscode";
import { DebugOutputStore, type DapMessage } from "./vendor/core/index.js";
import { forgetSession, recordDapMessage } from "./exceptionFilters.js";

/** Program output of debug sessions, shared with the bridge's getDebugOutput and listDebugSessions actions. */
export const debugOutputStore = new DebugOutputStore();

// Sessions that are running now. VS Code has no API to list them, so the bridge keeps its own list from the start and end events.
const liveSessions = new Map<string, vscode.DebugSession>();

/** Debug sessions started since the bridge registered its tracker and not yet ended. */
export function liveDebugSessions(): vscode.DebugSession[] {
    return [...liveSessions.values()];
}

type DapMessageListener = (sessionId: string, message: DapMessage) => void;
const dapListeners = new Set<DapMessageListener>();

/** Calls the listener with every Debug Adapter Protocol message from the adapters. Returns a disposable that removes it. */
export function onDebugAdapterMessage(listener: DapMessageListener): vscode.Disposable {
    dapListeners.add(listener);
    return {
        dispose: () => {
            dapListeners.delete(listener);
        },
    };
}

/** Observes Debug Adapter Protocol traffic for every debug session. Output events are captured without changing the session. */
export function registerDebugOutputTracker(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.debug.onDidStartDebugSession((session) => liveSessions.set(session.id, session)),
        vscode.debug.onDidTerminateDebugSession((session) => {
            liveSessions.delete(session.id);
            forgetSession(session.id);
        }),
        vscode.debug.registerDebugAdapterTrackerFactory("*", {
            createDebugAdapterTracker(session: vscode.DebugSession) {
                return {
                    onDidSendMessage(message: unknown) {
                        debugOutputStore.handle(session.id, message as DapMessage);
                        recordDapMessage(session.id, message);
                        for (const listener of dapListeners) listener(session.id, message as DapMessage);
                    },
                };
            },
        }),
    );
}
