import { describe, expect, test } from "vitest";
import { readFileSync } from "node:fs";
import { ROSLYN_ACTIONS, RoslynHelperClient, type HelperManifest, type PipeTransport } from "./roslynClient.js";

const sharedEditActions = (JSON.parse(readFileSync(new URL("../../../../../../conformance/roslyn-actions.json", import.meta.url), "utf8")) as { editActions: string[] }).editActions;

const manifest: HelperManifest = { pid: 4321, pipeName: "AiDevHelpers.Roslyn.4321", solutionPath: "F:\\repo\\App.slnx" };

function recordingTransport(reply: string): PipeTransport & { sent: { pipeName: string; line: string }[] } {
    const sent: { pipeName: string; line: string }[] = [];
    return {
        sent,
        send: async (pipeName, line) => {
            sent.push({ pipeName, line });
            return reply;
        },
    };
}

describe("Roslyn helper client", () => {
    test("a mocked pipe gets the request line for an edit action", async () => {
        const transport = recordingTransport(JSON.stringify({ ok: true, pid: 4321, result: { planned: true } }));
        const client = new RoslynHelperClient(() => manifest, transport);

        const result = await client.runAction("editMethod", { file: "F:\\repo\\App\\Widget.cs", line: 9 });

        expect(transport.sent).toHaveLength(1);
        expect(transport.sent[0].pipeName).toBe("AiDevHelpers.Roslyn.4321");
        expect(JSON.parse(transport.sent[0].line)).toEqual({ action: "editMethod", args: { file: "F:\\repo\\App\\Widget.cs", line: 9 } });
        expect(result).toEqual({ planned: true });
    });

    test("with no helper running, the error says so and nothing is sent", async () => {
        const transport = recordingTransport("");
        const client = new RoslynHelperClient(() => undefined, transport);

        await expect(client.runAction("editMethod", {})).rejects.toThrow(/No Roslyn helper is running/);
        expect(transport.sent).toHaveLength(0);
    });

    test("an error reply from the helper is thrown with its message", async () => {
        const transport = recordingTransport(JSON.stringify({ ok: false, pid: 4321, error: "Unknown action: nope." }));
        const client = new RoslynHelperClient(() => manifest, transport);

        await expect(client.runAction("editMethod", {})).rejects.toThrow("Unknown action: nope.");
    });

    test("the routed set is the shared helper edit actions", () => {
        expect([...ROSLYN_ACTIONS].sort()).toEqual([...sharedEditActions].sort());
    });

    test("the routed set is the helper's edit actions, and other VS actions are not routed", () => {
        expect(ROSLYN_ACTIONS.has("editMethod")).toBe(true);
        expect(ROSLYN_ACTIONS.has("renameSymbol")).toBe(true);
        expect(ROSLYN_ACTIONS.has("checkDocs")).toBe(false);
        expect(ROSLYN_ACTIONS.has("build")).toBe(false);
    });
});
