import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterAll, beforeEach, describe, expect, it, vi } from "vitest";
import { addItemChange, formatLogLine, logLevel, readProjectProperties, removeItemChange, settingReply } from "./projectShell.js";

// A stand-in for the VS Code API: only what ideActions.ts touches for getSetting and writeActivityLog.
const settings: Record<string, unknown> = { "editor.tabSize": 4 };
const logged: string[] = [];
const createOutputChannel = vi.fn(() => ({ appendLine: (text: string) => logged.push(text), append: () => undefined }));

vi.mock("vscode", () => ({
    window: { createOutputChannel, setStatusBarMessage: () => undefined, showInformationMessage: () => undefined },
    workspace: {
        workspaceFolders: undefined,
        getConfiguration: () => ({ get: (key: string) => settings[key] }),
    },
    debug: { onDidStartDebugSession: () => ({ dispose: () => undefined }), onDidTerminateDebugSession: () => ({ dispose: () => undefined }), activeDebugSession: undefined },
    commands: { executeCommand: async () => undefined },
    Uri: { file: (p: string) => ({ fsPath: p }) },
}));

const { runIdeAction } = await import("./ideActions.js");

const fixtureDir = mkdtempSync(path.join(tmpdir(), "aidev-csproj-"));
const csproj = path.join(fixtureDir, "Sample.csproj");
writeFileSync(
    csproj,
    [
        '<Project Sdk="Microsoft.NET.Sdk">',
        "  <PropertyGroup>",
        "    <TargetFramework>net10.0</TargetFramework>",
        "    <OutputType>Exe</OutputType>",
        "  </PropertyGroup>",
        "  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">",
        "    <OutputType>Library</OutputType>",
        "  </PropertyGroup>",
        "</Project>",
        "",
    ].join("\n"),
);

afterAll(() => {
    rmSync(fixtureDir, { recursive: true, force: true });
});

describe("csproj property read", () => {
    it("returns the named properties from unconditional PropertyGroups", () => {
        const text = readFileSync(csproj, "utf8");
        expect(readProjectProperties(text, ["TargetFramework", "OutputType"])).toEqual({
            TargetFramework: "net10.0",
            OutputType: "Exe",
        });
    });

    it("returns null for an unknown property", () => {
        const text = readFileSync(csproj, "utf8");
        expect(readProjectProperties(text, ["NoSuchProperty"])).toEqual({ NoSuchProperty: null });
    });
});

describe("item plans", () => {
    it("add without confirm describes the change and fromNew carries the template", () => {
        expect(addItemChange("Sample", "Foo.cs", "fromNew", "Class.cs")).toEqual({
            project: "Sample",
            file: "Foo.cs",
            mode: "fromNew",
            template: "Class.cs",
        });
        expect(addItemChange("Sample", "Bar.cs", "fromFile", undefined).template).toBeNull();
    });

    it("refuses fromNew without a template and an unknown mode", () => {
        expect(() => addItemChange("Sample", "Foo.cs", "fromNew", undefined)).toThrow("template");
        expect(() => addItemChange("Sample", "Foo.cs", "copy", undefined)).toThrow("copy");
    });

    it("remove describes the file staying on disk", () => {
        expect(removeItemChange("Sample", "Foo.cs")).toEqual({ project: "Sample", file: "Foo.cs", stays_on_disk: true });
    });
});

describe("getSetting shape", () => {
    it("reports found with the value when the setting exists", () => {
        expect(settingReply("editor.tabSize", 4)).toEqual({ key: "editor.tabSize", found: true, value: 4 });
    });

    it("reports not found with a null value when the setting is undefined", () => {
        expect(settingReply("editor.nope", undefined)).toEqual({ key: "editor.nope", found: false, value: null });
    });
});

describe("activity log line", () => {
    it("is an ISO timestamp, the level, the source and the message", () => {
        const at = new Date("2026-11-25T10:00:00.000Z");
        expect(formatLogLine(at, "Warning", "build", "slow step")).toBe("2026-11-25T10:00:00.000Z [Warning] build: slow step");
    });

    it("accepts the three VS levels and refuses others", () => {
        expect(logLevel("Error")).toBe("Error");
        expect(logLevel("Information")).toBe("Information");
        expect(() => logLevel("Debug")).toThrow("Debug");
    });
});

describe("wiring through runIdeAction", () => {
    beforeEach(() => {
        logged.length = 0;
        createOutputChannel.mockClear();
    });

    it("getSetting reads the configuration and returns the shape", async () => {
        expect(await runIdeAction("getSetting", { key: "editor.tabSize" })).toEqual({ key: "editor.tabSize", found: true, value: 4 });
        expect(await runIdeAction("getSetting", { key: "editor.missing" })).toEqual({ key: "editor.missing", found: false, value: null });
    });

    it("writeActivityLog without confirm plans and writes nothing", async () => {
        const reply = await runIdeAction("writeActivityLog", { level: "Error", source: "bridge", message: "boom" });
        expect(reply).toEqual({ planned: true, would: { level: "Error", source: "bridge", message: "boom" } });
        expect(logged).toEqual([]);
    });

    it("writeActivityLog with confirm appends one timestamped line to the AiDevHelpers channel", async () => {
        const reply = await runIdeAction("writeActivityLog", { level: "Information", source: "bridge", message: "hello", confirm: true });
        expect(reply).toMatchObject({ planned: false, written: true });
        expect(createOutputChannel).toHaveBeenCalledWith("AiDevHelpers");
        expect(logged).toHaveLength(1);
        expect(logged[0]).toMatch(/^\d{4}-\d{2}-\d{2}T[\d:.]+Z \[Information\] bridge: hello$/);
    });

    it("setStatus without confirm returns the plan", async () => {
        expect(await runIdeAction("setStatus", { text: "Building" })).toEqual({ planned: true, would: { text: "Building" } });
    });

    it("removeProjectItem refuses when no workspace is open", async () => {
        await expect(runIdeAction("removeProjectItem", { project: "NoSuchProject", file: "Foo.cs" })).rejects.toThrow("workspace folder");
    });

    it("showInfoBar without confirm returns the plan with the labels as actions", async () => {
        expect(await runIdeAction("showInfoBar", { text: "Done", actions: [{ label: "Open" }, { label: "Later" }] })).toEqual({
            planned: true,
            would: { text: "Done", actions: ["Open", "Later"] },
        });
    });
});
