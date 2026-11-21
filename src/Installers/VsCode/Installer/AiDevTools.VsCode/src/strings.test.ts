import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { t } from "./strings.js";

const table = JSON.parse(readFileSync(new URL("./strings.en.json", import.meta.url), "utf8")) as Record<string, string>;
const actions = readFileSync(new URL("./ideActions.ts", import.meta.url), "utf8");

describe("strings", () => {
    it("fills numbered placeholders", () => {
        expect(t("missingArgument", "line")).toBe("Missing required argument 'line'.");
        expect(t("processNotStarted", 42)).toBe("Process 42 was not started by this bridge; it will not be killed.");
    });

    it("returns the key for an unknown key", () => {
        expect(t("nope" as never)).toBe("nope");
    });

    it("has every key that ideActions.ts uses", () => {
        const used = [...actions.matchAll(/\bt\("([A-Za-z0-9]+)"/g)].map((m) => m[1]);
        expect(used.length).toBeGreaterThan(0);
        for (const key of used) {
            expect(table[key], `missing key ${key}`).toBeDefined();
        }
    });

    it("has no hard-coded error text left in ideActions.ts", () => {
        const literal = /new Error\((["`])/g;
        expect(actions.match(literal) ?? []).toEqual([]);
    });
});
