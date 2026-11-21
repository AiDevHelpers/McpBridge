import * as vscode from "vscode";
import { startIdeBridge } from "./ideBridge.js";

// VS Code entry point. Starts the bridge for this window; the bridge is disposed with the extension.
export function activate(context: vscode.ExtensionContext): void {
    startIdeBridge(context);
}

export function deactivate(): void {
    // Nothing to clean up: the bridge's disposables are registered on the context.
}
