import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// Parity ratchet: every action the Visual Studio bridge serves must be handled by ideActions.ts, or be listed
// here with the plan unit that will add it (or with "unsupported" and a reason). A unit removes its entries
// from `planned` when it lands. The lists come from the source files, so a new VS action fails this test
// until it is placed here.
const vsSource = readFileSync(new URL("../../../../Vs2026/Core/src/IdeActions.cs", import.meta.url), "utf8");
const vsCodeSource = readFileSync(new URL("./ideActions.ts", import.meta.url), "utf8");

// Mode values inside the C# dispatch (not actions).
const NOT_ACTIONS = new Set(["always", "equal", "multiple", "greaterOrEqual", "health"]);

const vsActions = [...vsSource.matchAll(/^\s*"([A-Za-z]+)"\s*=>/gm)]
    .map((m) => m[1])
    .filter((name) => !NOT_ACTIONS.has(name));
const vsCodeActions = new Set([...vsCodeSource.matchAll(/case "([A-Za-z]+)"/g)].map((m) => m[1]));

// Name -> plan unit that adds it.
const planned: Record<string, string> = {
    // Unit 1: breakpoint control
    // Unit 3: processes and attach
    listProcesses: "3", attachProcess: "3", listExceptions: "3", setExceptionBreak: "3",
    // Unit 4: documents and close solution
    listDocuments: "4", saveDocument: "4", closeDocument: "4", closeSolution: "4",
    // Unit 5: text search
    findInFiles: "5", replaceInFiles: "5",
    // Unit 6: language services and undo group
    listCodeElements: "6", semanticReferences: "6", semanticDefinition: "6", renameSymbol: "6", undoGroup: "6",
    // Unit 7: project and shell services
    projectProperties: "7", addProjectItem: "7", removeProjectItem: "7", getSetting: "7",
    setStatus: "7", showInfoBar: "7", writeActivityLog: "7",
    // Separate helper plan (answer 1): Roslyn edit tools, C# engines VS Code cannot host
    editMethod: "helper", threadParameter: "helper", addEnumMember: "helper", editEnumMember: "helper",
    editType: "helper", generateConstructor: "helper", implementMembers: "helper", addType: "helper",
    addInterfaceMember: "helper", changeSignature: "helper", addMethod: "helper", addOverload: "helper",
    deleteMember: "helper", deleteType: "helper", listUnused: "helper", renameNamespace: "helper",
    moveType: "helper", addMember: "helper", editMember: "helper", tddAddTest: "helper", tddUpdateTest: "helper",
};

// Name -> reason it will not be in VS Code (kept out of scope by the plan answers or the platform).
const unsupported: Record<string, string> = {
    listTasks: "Task List is unsupported in v1 (plan answer 4)",
    addTask: "Task List is unsupported in v1 (plan answer 4)",
    clearTasks: "Task List is unsupported in v1 (plan answer 4)",
    listOutputPanes: "VS Code's API cannot enumerate output channels",
    testHotReloadState: "VS-only name; VS Code serves hotReloadState",
};

describe("VS Code parity with the Visual Studio bridge", () => {
    it("every VS action is handled, planned, or listed as unsupported", () => {
        const missing = vsActions.filter((name) => !vsCodeActions.has(name) && !(name in planned) && !(name in unsupported));
        expect(missing, `actions with no VS Code handler, plan unit or reason: ${missing.join(", ")}`).toEqual([]);
    });

    it("planned and unsupported names are real VS actions", () => {
        const known = new Set(vsActions);
        const stale = [...Object.keys(planned), ...Object.keys(unsupported)].filter((name) => !known.has(name));
        expect(stale, `names not in IdeActions.cs: ${stale.join(", ")}`).toEqual([]);
    });

    it("a planned name is not already handled", () => {
        const done = Object.keys(planned).filter((name) => vsCodeActions.has(name));
        expect(done, `remove these from planned: ${done.join(", ")}`).toEqual([]);
    });
});
