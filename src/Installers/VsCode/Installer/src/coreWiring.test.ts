import { describe, expect, test } from "vitest";
import { DebugOutputStore, OutputBuffer, exthostLogRoot, replyToBridgeRequest } from "./vendor/core/index.js";

describe("Core package wiring", () => {
    test("exports resolve from the Installer", () => {
        expect(typeof replyToBridgeRequest).toBe("function");
        expect(typeof exthostLogRoot).toBe("function");
        expect(typeof OutputBuffer).toBe("function");
        expect(typeof DebugOutputStore).toBe("function");
    });
});
