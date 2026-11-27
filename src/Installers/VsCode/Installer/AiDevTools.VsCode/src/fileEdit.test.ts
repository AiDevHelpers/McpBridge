import { mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { describe, expect, it } from "vitest";
import { applyEdits, detectBom, replaceInFile } from "./fileEdit.js";

function freshRoot(): string {
    const root = mkdtempSync(path.join(tmpdir(), "fileedit-"));
    mkdirSync(path.join(root, ".git"));
    return root;
}

describe("detectBom", () => {
    it("reads UTF-8, UTF-16 LE and UTF-16 BE byte order marks", () => {
        expect(detectBom(Buffer.from([0xef, 0xbb, 0xbf, 0x61]))).toEqual({ encoding: "utf8", bomLength: 3 });
        expect(detectBom(Buffer.from([0xff, 0xfe, 0x61, 0x00]))).toEqual({ encoding: "utf16le", bomLength: 2 });
        expect(detectBom(Buffer.from([0xfe, 0xff, 0x00, 0x61]))).toEqual({ encoding: "utf16be", bomLength: 2 });
    });

    it("defaults to UTF-8 with no BOM", () => {
        expect(detectBom(Buffer.from("abc", "utf8"))).toEqual({ encoding: "utf8", bomLength: 0 });
    });
});

describe("applyEdits", () => {
    it("replaces every match when occurrence is 0", () => {
        const r = applyEdits("a b a", [{ oldText: "a", newText: "x" }]);
        expect(r.text).toBe("x b x");
        expect(r.matches).toBe(2);
    });

    it("replaces only the Nth match when occurrence is N", () => {
        const r = applyEdits("a b a", [{ oldText: "a", newText: "x", occurrence: 2 }]);
        expect(r.text).toBe("a b x");
    });

    it("refuses an oldText with no match", () => {
        expect(() => applyEdits("abc", [{ oldText: "zzz", newText: "x" }])).toThrow(/no match/i);
    });

    it("stops at the first failing edit in a batch", () => {
        expect(() => applyEdits("abc", [
            { oldText: "a", newText: "x" },
            { oldText: "zzz", newText: "y" },
        ])).toThrow(/no match/i);
    });
});

describe("replaceInFile", () => {
    it("plans without writing when confirm is not true", () => {
        const root = freshRoot();
        const file = path.join(root, "a.txt");
        writeFileSync(file, "one two");

        const r = replaceInFile(root, "a.txt", [{ oldText: "two", newText: "three" }], false);

        expect(r).toEqual({ planned: true, file: "a.txt", matches: 1, wouldWrite: true });
        expect(readFileSync(file, "utf8")).toBe("one two");
    });

    it("applies, keeps the BOM and writes a byte-exact backup", () => {
        const root = freshRoot();
        const file = path.join(root, "b.txt");
        const original = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from("hi there", "utf16le")]);
        writeFileSync(file, original);

        const r = replaceInFile(root, "b.txt", [{ oldText: "there", newText: "you" }], true);

        expect(r.planned).toBe(false);
        expect(r.linesChanged).toBe(1);
        const after = readFileSync(file);
        expect(after.subarray(0, 2)).toEqual(Buffer.from([0xff, 0xfe]));
        expect(after.subarray(2).toString("utf16le")).toBe("hi you");

        const backupDir = path.join(root, ".aidevhelpers", "backups");
        const backups = readdirSync(backupDir);
        expect(backups).toHaveLength(1);
        expect(readFileSync(path.join(backupDir, backups[0]!))).toEqual(original);
        expect(r.backupPath).toBe(path.join(backupDir, backups[0]!));
    });

    it("keeps a UTF-16 BE file big-endian after an edit", () => {
        const root = freshRoot();
        const file = path.join(root, "c.txt");
        writeFileSync(file, Buffer.concat([Buffer.from([0xfe, 0xff]), Buffer.from("hi there", "utf16le").swap16()]));

        replaceInFile(root, "c.txt", [{ oldText: "there", newText: "you" }], true);

        const after = readFileSync(file);
        expect(after.subarray(0, 2)).toEqual(Buffer.from([0xfe, 0xff]));
        expect(Buffer.from(after.subarray(2)).swap16().toString("utf16le")).toBe("hi you");
    });

    it("refuses a file outside the repo root", () => {
        const root = freshRoot();
        expect(() => replaceInFile(root, path.join("..", "escape.txt"), [{ oldText: "a", newText: "b" }], true)).toThrow(/outside/i);
    });

    it("refuses a missing file", () => {
        const root = freshRoot();
        expect(() => replaceInFile(root, "missing.txt", [{ oldText: "a", newText: "b" }], true)).toThrow(/not found|no such/i);
    });
});
