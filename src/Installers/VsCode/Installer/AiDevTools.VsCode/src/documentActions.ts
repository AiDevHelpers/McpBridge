import * as path from "path";
import { t } from "./strings.js";

// Pure rules for the document actions (listDocuments, saveDocument, closeDocument, closeSolution). No vscode import, so
// the rules are tested without the VS Code host. ideActions.ts supplies the open documents and performs the changes.
// The reply shapes follow the Visual Studio bridge (IdeActions.cs): planned/would without confirm, planned=false/applied with it.

export interface OpenDocument {
    uri: { fsPath: string };
    isDirty: boolean;
}

export interface DocumentEntry {
    fullName: string;
    saved: boolean;
    readOnly: boolean;
}

// Paths compare case-insensitively on Windows, as the file system does.
export function sameFile(a: string, b: string): boolean {
    const left = path.normalize(a);
    const right = path.normalize(b);
    return process.platform === "win32" ? left.toLowerCase() === right.toLowerCase() : left === right;
}

// The open document for a path, or an error naming the file the caller passed.
export function requireOpenDocument<T extends OpenDocument>(documents: readonly T[], fsPath: string, file: string): T {
    const match = documents.find((d) => sameFile(d.uri.fsPath, fsPath));
    if (match === undefined) {
        throw new Error(t("noOpenDocument", file));
    }
    return match;
}

export function shapeDocumentList(entries: readonly DocumentEntry[]): { documents: DocumentEntry[] } {
    return { documents: [...entries] };
}

export function dirtyPaths(documents: readonly OpenDocument[]): string[] {
    return documents.filter((d) => d.isDirty).map((d) => d.uri.fsPath);
}

// closeDocument refuses unsaved changes unless discardChanges is set. This applies with or without confirm.
export function refuseDirtyClose(file: string, dirty: boolean, discardChanges: boolean): void {
    if (dirty && !discardChanges) {
        throw new Error(t("documentDirty", file));
    }
}

// closeSolution refuses unsaved documents unless saveFirst is set. It is checked only when confirm is set, as in the VS bridge.
export function refuseDirtySolution(dirtyDocuments: readonly string[], saveFirst: boolean): void {
    if (!saveFirst && dirtyDocuments.length > 0) {
        throw new Error(t("solutionHasDirtyDocuments", dirtyDocuments.join(", ")));
    }
}

export function planSaveReply(file: string, dirty: boolean): { planned: true; would: { file: string; dirty: boolean } } {
    return { planned: true, would: { file, dirty } };
}

export function appliedSaveReply(file: string): { planned: false; applied: { file: string; saved: true } } {
    return { planned: false, applied: { file, saved: true } };
}

export function planCloseReply(file: string, dirty: boolean, discard: boolean): { planned: true; would: { file: string; dirty: boolean; discard: boolean } } {
    return { planned: true, would: { file, dirty, discard } };
}

export function appliedCloseReply(file: string): { planned: false; applied: { file: string; closed: true } } {
    return { planned: false, applied: { file, closed: true } };
}

export function planCloseSolutionReply(dirtyDocuments: string[], saveFirst: boolean): { planned: true; would: { dirtyDocuments: string[]; saveFirst: boolean } } {
    return { planned: true, would: { dirtyDocuments, saveFirst } };
}

export function appliedCloseSolutionReply(savedDocuments: string[]): { planned: false; applied: { closed: true; savedDocuments: string[] } } {
    return { planned: false, applied: { closed: true, savedDocuments } };
}
