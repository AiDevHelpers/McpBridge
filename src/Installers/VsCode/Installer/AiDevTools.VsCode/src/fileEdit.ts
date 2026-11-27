import * as crypto from "crypto";
import * as fs from "fs";
import * as path from "path";

// The single-file edit engine behind the replaceInFile bridge action. It follows FileEditHelper's rules: a byte-exact backup
// before every write, the BOM kept, an atomic write, and a batch that stops at the first failing edit. No VS Code API here,
// so it is testable on its own.

export type Encoding = "utf8" | "utf16le" | "utf16be";

export interface BomInfo {
    encoding: Encoding;
    bomLength: number;
}

export interface Edit {
    oldText: string;
    newText: string;
    occurrence?: number;
}

export interface ApplyResult {
    text: string;
    matches: number;
}

export type ReplaceResult =
    | { planned: true; file: string; matches: number; wouldWrite: true }
    | { planned: false; file: string; linesChanged: number; backupPath: string };

// Reads the byte order mark. No BOM means UTF-8.
export function detectBom(bytes: Buffer): BomInfo {
    if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
        return { encoding: "utf8", bomLength: 3 };
    }
    if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
        return { encoding: "utf16le", bomLength: 2 };
    }
    if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
        return { encoding: "utf16be", bomLength: 2 };
    }
    return { encoding: "utf8", bomLength: 0 };
}

// Applies the edits in order to text. Each edit needs at least one match; the batch stops at the first edit that has none.
// occurrence 0 (the default) replaces every match, N replaces only the Nth.
export function applyEdits(text: string, edits: Edit[]): ApplyResult {
    let current = text;
    let total = 0;
    for (const edit of edits) {
        if (edit.oldText.length === 0) {
            throw new Error("An edit needs a non-empty oldText.");
        }
        const positions: number[] = [];
        let from = 0;
        for (;;) {
            const at = current.indexOf(edit.oldText, from);
            if (at < 0) {
                break;
            }
            positions.push(at);
            from = at + edit.oldText.length;
        }
        if (positions.length === 0) {
            throw new Error(`No match for oldText '${edit.oldText}'.`);
        }
        total += positions.length;
        const occurrence = edit.occurrence ?? 0;
        const chosen = occurrence === 0 ? positions : [positions[occurrence - 1]].filter((p): p is number => p !== undefined);
        if (chosen.length === 0) {
            throw new Error(`The file has ${positions.length} match(es); occurrence ${occurrence} does not exist.`);
        }
        // Replace from the end so earlier positions stay valid.
        for (const at of [...chosen].reverse()) {
            current = current.slice(0, at) + edit.newText + current.slice(at + edit.oldText.length);
        }
    }
    return { text: current, matches: total };
}

// True when target is root or inside it, comparing resolved paths.
function isInside(root: string, target: string): boolean {
    const rel = path.relative(root, target);
    return rel === "" || (!rel.startsWith("..") && !path.isAbsolute(rel));
}

// Any link (symlink or junction) on the way to the file, including the file itself, refuses the edit.
function refuseLinks(root: string, target: string): void {
    for (let current = target; isInside(root, current) && current !== root; current = path.dirname(current)) {
        if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) {
            throw new Error(`'${path.relative(root, current)}' is a link or junction; refusing to edit through it.`);
        }
    }
}

function backupName(relative: string): string {
    const now = new Date();
    const pad = (n: number, w = 2) => String(n).padStart(w, "0");
    const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}_${pad(now.getMilliseconds(), 3)}`;
    const safe = relative.replace(/[\\/:*?"<>|]/g, "_");
    return `${stamp}_${crypto.randomBytes(3).toString("hex")}_${safe}`;
}

// Plans or applies one file's edits. A plan writes nothing. An apply writes a byte-exact backup first, then the new text atomically.
export function replaceInFile(root: string, file: string, edits: Edit[], confirm: boolean): ReplaceResult {
    const resolvedRoot = path.resolve(root);
    const target = path.resolve(resolvedRoot, file);
    if (!isInside(resolvedRoot, target)) {
        throw new Error(`'${file}' is outside the repo root.`);
    }
    const relative = path.relative(resolvedRoot, target);
    refuseLinks(resolvedRoot, target);
    if (!fs.existsSync(target)) {
        throw new Error(`File not found: '${relative}'.`);
    }

    const original = fs.readFileSync(target);
    const bom = detectBom(original);
    const body = original.subarray(bom.bomLength);
    const decoded = body.toString(bom.encoding === "utf16be" ? "utf16le" : bom.encoding);
    const result = applyEdits(bom.encoding === "utf16be" ? swapBytes(decoded) : decoded, edits);

    if (!confirm) {
        return { planned: true, file: relative, matches: result.matches, wouldWrite: true };
    }

    const backupDir = path.join(resolvedRoot, ".aidevhelpers", "backups");
    fs.mkdirSync(backupDir, { recursive: true });
    const backupPath = path.join(backupDir, backupName(relative));
    fs.writeFileSync(backupPath, original);

    const encoded = encode(result.text, bom.encoding);
    const bomBytes = bom.bomLength === 0 ? Buffer.alloc(0) : original.subarray(0, bom.bomLength);
    const temp = `${target}.${crypto.randomBytes(4).toString("hex")}.tmp`;
    fs.writeFileSync(temp, Buffer.concat([bomBytes, encoded]));
    fs.renameSync(temp, target);

    return { planned: false, file: relative, linesChanged: countChangedLines(decoded, result.text), backupPath };
}

// Big-endian UTF-16 is read by swapping byte pairs, so the same string logic applies.
function swapBytes(s: string): string {
    const buf = Buffer.from(s, "utf16le");
    for (let i = 0; i + 1 < buf.length; i += 2) {
        const hi = buf[i]!;
        buf[i] = buf[i + 1]!;
        buf[i + 1] = hi;
    }
    return buf.toString("utf16le");
}

function encode(text: string, encoding: Encoding): Buffer {
    if (encoding === "utf8") {
        return Buffer.from(text, "utf8");
    }
    if (encoding === "utf16le") {
        return Buffer.from(text, "utf16le");
    }
    return Buffer.from(swapBytes(text), "utf16le");
}

function countChangedLines(before: string, after: string): number {
    const a = before.split("\n");
    const b = after.split("\n");
    let changed = Math.abs(a.length - b.length);
    for (let i = 0; i < Math.min(a.length, b.length); i++) {
        if (a[i] !== b[i]) {
            changed++;
        }
    }
    return changed;
}
