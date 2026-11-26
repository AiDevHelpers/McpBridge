import * as vscode from "vscode";
import { startEvents } from "./events.js";
import { startIdeBridge } from "./ideBridge.js";

// VS Code entry point. Starts the bridge and the events pipe for this window; both are disposed with the extension.
export function activate(context: vscode.ExtensionContext): void {
    startIdeBridge(context);
    startEvents(context);
}

export function deactivate(): void {
    // Nothing to clean up: the bridge's disposables are registered on the context.
}
