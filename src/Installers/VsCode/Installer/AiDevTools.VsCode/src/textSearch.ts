import { type Dirent, readdirSync, readFileSync, writeFileSync } from "node:fs";
import * as path from "node:path";

// Text search over files on disk. No vscode import, so it can be tested without the extension host.
// It follows the Visual Studio bridge's managed search (IdeActions.cs FindInFiles and ReplaceInFiles):
// find is read-only and capped; replace counts matching lines per file and writes only when asked.

export const MAX_SEARCH_MATCHES = 500;

// Folders a search never enters. TestResults matches the exclusions the formatter uses in ideActions.ts.
const SKIPPED_FOLDERS = new Set(["bin", "obj", ".git", "node_modules", "TestResults"]);

export interface SearchMatch {
    file: string;
    line: number;
    text: string;
}

export interface FindResult {
    matches: SearchMatch[];
    truncated: boolean;
}

export interface FileCount {
    file: string;
    matches: number;
}

export interface ReplacePlan {
    planned: FileCount[];
    skipped: string[];
}

export interface ReplaceResult {
    applied: FileCount[];
    failed: Array<{ file: string; error: string }>;
}

function escapeRegex(text: string): string {
    return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// Literal text is escaped first. Case is ignored unless matchCase is set. The regex has no g flag, so test() is stateless.
export function buildSearchPattern(pattern: string, matchCase: boolean, useRegex: boolean): RegExp {
    return new RegExp(useRegex ? pattern : escapeRegex(pattern), matchCase ? "" : "i");
}

// Every file under the roots, skipping the folders above. Symbolic links are not followed, so a link cannot loop.
export function walkFiles(roots: readonly string[]): string[] {
    const files: string[] = [];
    const visit = (dir: string): void => {
        let entries: Dirent[];
        try {
            entries = readdirSync(dir, { withFileTypes: true });
        } catch {
            return;
        }
        entries.sort((a, b) => a.name.localeCompare(b.name));
        for (const entry of entries) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) {
                if (!SKIPPED_FOLDERS.has(entry.name)) {
                    visit(full);
                }
            } else if (entry.isFile()) {
                files.push(full);
            }
        }
    };
    for (const root of roots) {
        visit(root);
    }
    return files;
}

// The folder holding <projectName>.csproj under the roots, or undefined when there is none.
export function findProjectDirectory(roots: readonly string[], projectName: string): string | undefined {
    const wanted = `${projectName}.csproj`.toLowerCase();
    const project = walkFiles(roots).find((file) => path.basename(file).toLowerCase() === wanted);
    return project === undefined ? undefined : path.dirname(project);
}

// Lines split the way File.ReadAllLines does: \r\n, \r or \n, with no empty line after a final newline.
function readLines(file: string): string[] | undefined {
    let text: string;
    try {
        text = readFileSync(file, "utf8");
    } catch {
        return undefined;
    }
    const lines = text.split(/\r\n|\r|\n/);
    if (lines.length > 0 && lines[lines.length - 1] === "") {
        lines.pop();
    }
    return lines;
}

// Matches line by line, in file order. Stops once a 501st match is found, and reports that as truncated.
export function findInFiles(files: readonly string[], regex: RegExp): FindResult {
    const matches: SearchMatch[] = [];
    for (const file of files) {
        const lines = readLines(file) ?? [];
        for (let i = 0; i < lines.length; i++) {
            if (!regex.test(lines[i])) {
                continue;
            }
            if (matches.length >= MAX_SEARCH_MATCHES) {
                return { matches, truncated: true };
            }
            matches.push({ file, line: i + 1, text: lines[i] });
        }
    }
    return { matches, truncated: false };
}

function sameFileKey(file: string): string {
    const resolved = path.resolve(file);
    return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}

function countMatchingLines(file: string, regex: RegExp): number {
    return (readLines(file) ?? []).filter((line) => regex.test(line)).length;
}

// Files that would be written, in the order given and without repeats. A file with no match is left out.
// A file in dirtyFiles goes to skipped instead, so its unsaved edits are not overwritten.
export function planReplace(files: readonly string[], regex: RegExp, dirtyFiles: readonly string[]): ReplacePlan {
    const dirty = new Set(dirtyFiles.map(sameFileKey));
    const seen = new Set<string>();
    const planned: FileCount[] = [];
    const skipped: string[] = [];
    for (const file of files) {
        const key = sameFileKey(file);
        if (seen.has(key)) {
            continue;
        }
        seen.add(key);
        const matches = countMatchingLines(file, regex);
        if (matches === 0) {
            continue;
        }
        if (dirty.has(key)) {
            skipped.push(file);
        } else {
            planned.push({ file, matches });
        }
    }
    return { planned, skipped };
}

// Writes the replacement to each planned file. Literal replacement text is used as written. With useRegex the
// replacement uses JavaScript's $ patterns, which differ from .NET's in a few cases. A write error is reported per file.
export function applyReplace(planned: readonly FileCount[], regex: RegExp, replacement: string, useRegex: boolean): ReplaceResult {
    const global = new RegExp(regex.source, `${regex.flags}g`);
    const applied: FileCount[] = [];
    const failed: Array<{ file: string; error: string }> = [];
    for (const target of planned) {
        try {
            const original = readFileSync(target.file, "utf8");
            const updated = useRegex
                ? original.replace(global, replacement)
                : original.replace(global, () => replacement);
            writeFileSync(target.file, updated, "utf8");
            applied.push(target);
        } catch (err) {
            failed.push({ file: target.file, error: err instanceof Error ? err.message : String(err) });
        }
    }
    return { applied, failed };
}
