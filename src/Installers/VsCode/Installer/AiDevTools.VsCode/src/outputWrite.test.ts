import { beforeEach, describe, expect, it, vi } from "vitest";

// A stand-in for the VS Code API: only what ideActions.ts touches while handling outputWrite.
const channels = new Map<string, { appended: string[] }>();
const createOutputChannel = vi.fn((name: string) => {
    const channel = { appended: [] as string[], append: (text: string) => channel.appended.push(text) };
    channels.set(name, channel);
    return channel;
});

vi.mock("vscode", () => ({
    window: { createOutputChannel },
    workspace: { workspaceFolders: undefined, getConfiguration: () => ({ get: () => undefined }) },
    debug: { onDidStartDebugSession: () => ({ dispose: () => undefined }), onDidTerminateDebugSession: () => ({ dispose: () => undefined }), activeDebugSession: undefined },
    commands: { executeCommand: async () => undefined },
    Uri: { file: (p: string) => ({ fsPath: p }) },
}));

const { runIdeAction } = await import("./ideActions.js");

describe("outputWrite", () => {
    beforeEach(() => {
        channels.clear();
        createOutputChannel.mockClear();
    });

    it("creates the channel on first use and appends the text with a newline", async () => {
        const reply = await runIdeAction("outputWrite", { channel: "SelfTest", text: "self test" });

        expect(reply).toEqual({ channel: "SelfTest", written: 9 });
        expect(createOutputChannel).toHaveBeenCalledTimes(1);
        expect(channels.get("SelfTest")?.appended).toEqual(["self test\n"]);
    });

    it("reuses the channel and does not double a trailing newline", async () => {
        await runIdeAction("outputWrite", { channel: "Build", text: "one" });
        await runIdeAction("outputWrite", { channel: "Build", text: "two\n" });

        expect(createOutputChannel).toHaveBeenCalledTimes(1);
        expect(channels.get("Build")?.appended).toEqual(["one\n", "two\n"]);
    });

    it("refuses a missing channel or text", async () => {
        await expect(runIdeAction("outputWrite", { text: "x" })).rejects.toThrow("Missing required argument 'channel'.");
        await expect(runIdeAction("outputWrite", { channel: "Build" })).rejects.toThrow("Missing required argument 'text'.");
        expect(createOutputChannel).not.toHaveBeenCalled();
    });
});
