import * as vscode from "vscode";
import { startEvents } from "./events.js";
import { startIdeBridge } from "./ideBridge.js";
import { childProcessSpawner, findSolutionFile, startRoslynHelper, type RoslynWiring } from "./roslynHelper.js";

let roslyn: RoslynWiring | undefined;

// VS Code entry point. Starts the bridge and the events pipe for this window; both are disposed with the extension.
// The Roslyn helper starts for the workspace's solution file and stops on deactivate.
export function activate(context: vscode.ExtensionContext): void {
    startIdeBridge(context);
    startEvents(context);
    roslyn = startRoslynHelper({
        extensionPath: context.extensionPath,
        workspaceFolder: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
        spawner: childProcessSpawner,
        findSolution: findSolutionFile,
    });
}

export function deactivate(): void {
    // The bridge's disposables are registered on the context. The Roslyn helper is stopped here.
    roslyn?.stop();
    roslyn = undefined;
}
